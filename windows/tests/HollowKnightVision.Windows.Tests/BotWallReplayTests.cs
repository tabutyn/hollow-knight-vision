using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotWallReplayTests
{
    private const int Width = 160, Height = 110;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Fixture(string Clip, string Archive, string HackerClip, Guid ReplayId);

    internal static void TestRecoveryNeedsExactOriginalImageAndMasks()
    {
        var directory = Directory.CreateTempSubdirectory("hkv-wall-replay-");
        try
        {
            var fixture = WriteFixture(directory.FullName);
            var events = Path.Combine(fixture.Clip, "events.jsonl");
            var saved = File.ReadAllBytes(events);
            var rows = BotWallReplay.Recover(fixture.Clip, fixture.Archive);
            Check(rows is [{ Generation: 7 }, { Generation: 7 }]
                && rows[0].CaptureTimestamp == Timestamp(0) && rows[1].CaptureTimestamp == Timestamp(1),
                "Ambiguous, missing, and off-clock evidence must never enter wall recovery.");
            Check(rows[0].Features is [1, < -.18 and > -.24, _, _, _, 1, 0, 0, 0, 0, 0],
                "The exact source image should restore the left wall.");
            Check(rows[1].Features is [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                "The saved exclusion must suppress a wall that is visible in the raw pixels.");
            Check(File.ReadAllBytes(events).AsSpan().SequenceEqual(saved), "Replay must never edit source observations.");
            Check(BotWallReplay.Recover(fixture.HackerClip, fixture.Archive).Count == 0,
                "Pixel wall replay must not add detections to authored Hacker clips.");

            Reject(() => BotWallReplay.Recover(fixture.Clip, Path.Combine(directory.FullName, $"frames-{Guid.NewGuid():D}")));
            File.AppendAllText(Path.Combine(fixture.Archive, "00000000.png"), "changed");
            Reject(() => BotWallReplay.Recover(fixture.Clip, fixture.Archive));
        }
        finally { directory.Delete(recursive: true); }
    }

    internal static void TestMacWallRecoveryUsesTheRecordedHudShield()
    {
        var directory = Directory.CreateTempSubdirectory("hkv-wall-replay-hud-");
        try
        {
            var fixture = WriteFixture(directory.FullName);
            var filename = Path.Combine(fixture.Archive, "manifest.json");
            var manifest = JsonSerializer.Deserialize<VisualReplayManifest>(File.ReadAllBytes(filename), Json)!;
            double[][][] shield = [[[0, Height - 28], [64, 28]]];
            WriteMask(windows: false);
            var mac = BotWallReplay.Recover(fixture.Clip, fixture.Archive);
            Check(mac is [{ }, { }] && mac[0].Features is [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                "The full recorded Mac HUD shield must keep a short visible wall tail below the admission threshold.");

            WriteMask(windows: true);
            var windows = BotWallReplay.Recover(fixture.Clip, fixture.Archive);
            Check(windows is [{ }, { }]
                && windows[0].Features is [1, < -.18 and > -.24, _, _, _, 1, 0, 0, 0, 0, 0],
                "The Windows recorder's separate Atlas mask must retain the existing Windows ground mask policy.");
            Check(mac.Select(row => row.CaptureTimestamp).SequenceEqual(windows.Select(row => row.CaptureTimestamp)),
                "Reading the recorded mask must preserve the exact capture join.");

            void WriteMask(bool windows)
            {
                var masks = manifest.GroundInputs.Select(input => input.CaptureTimestamp != Timestamp(0) ? input : input with
                {
                    ExcludingRects = shield,
                    WindowsAtlasExcludingRects = windows ? shield : null
                }).ToArray();
                File.WriteAllBytes(filename, JsonSerializer.SerializeToUtf8Bytes(manifest with { GroundInputs = masks }, Json));
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    internal static void TestMacWallReplayHonorsSrgbAndFractionalExclusions()
    {
        var directory = Directory.CreateTempSubdirectory("hkv-wall-replay-pixels-");
        try
        {
            // PNG sRGB and cICP chunks with valid CRCs for intent 0 and BT.2020/PQ respectively.
            var srgb = Convert.FromHexString("000000017352474200AECE1CE9");
            var cicp = Convert.FromHexString("0000000463494350091000014D2323FE");
            Check(Recover("mac-srgb", srgb, windowsArchive: false)[0].Features
                is [1, < -.18 and > -.24, _, _, _, 1, 0, 0, 0, 0, 0],
                "The recorded red wall must reach the wall threshold after sRGB conversion.");
            foreach (var (name, profile, windows) in new[]
            {
                ("mac-untagged", Array.Empty<byte>(), false),
                ("mac-cicp", srgb.Concat(cicp).ToArray(), false),
                ("windows", srgb, true),
            })
            {
                Check(Recover(name, profile, windows)[0].Features is [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                    "A different, absent, or Windows color declaration must keep the existing grayscale path.");
            }

            var frame = StrongWallFrame();
            StencilRect[] exclusions = [new(58.6, 75.2, .2, .2), new(5.5, 10.5, 1, 1)];
            Check(GroundLineDetector.CaptureCenterExclusions(frame, exclusions).SequenceEqual([new PixelRect(5, 10, 2, 2)]),
                "Inclusive pixel-center sampling must preserve half-pixel boundaries.");
            var edges = GroundLineDetector.AnalyzeMacReplay(frame, [exclusions[0]], hasSrgbProfile: false);
            var valid = edges.ValidSourcePixels!;
            Check(valid[63 * Width + 35] == 0 && valid[86 * Width + 82] == 0
                && valid[63 * Width + 34] == 1 && valid[86 * Width + 83] == 1
                && valid[62 * Width + 35] == 1 && valid[87 * Width + 82] == 1,
                "The archive convolution padding must be applied before quantizing a subpixel rectangle.");

            // DeviceGray reference values measured by CoreGraphics on these straight-alpha sRGB samples.
            var alpha = new BgraFrame(6, 1, 24,
                [0, 0, 255, 255, 0, 0, 255, 128, 220, 190, 50, 80, 255, 0, 0, 0,
                    255, 255, 255, 128, 127, 127, 127, 127], 1, DateTimeOffset.UnixEpoch);
            Check(GroundPixels.SrgbArchiveLuma(alpha).SequenceEqual(new byte[] { 130, 65, 54, 0, 128, 63 }),
                "Archive grayscale must handle opacity and the original sRGB colors.");

            IReadOnlyList<BotRecoveredWallFrame> Recover(string name, byte[] profile, bool windowsArchive)
            {
                var fixture = WriteFixture(Path.Combine(directory.FullName, name), colored: true,
                    windowsArchive, (image, path) =>
                    {
                        PngWriter.Write(image, path);
                        var png = File.ReadAllBytes(path);
                        const int endOfHeader = 33;
                        File.WriteAllBytes(path, [.. png.AsSpan(0, endOfHeader), .. profile, .. png.AsSpan(endOfHeader)]);
                    });
                var rows = BotWallReplay.Recover(fixture.Clip, fixture.Archive);
                Check(rows.Count == 2 && rows[0].CaptureTimestamp == Timestamp(0),
                    "Image color conversions must preserve exact frame ownership.");
                return rows;
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    internal static Fixture WriteFixture(string root, bool colored = false, bool windowsArchive = false,
        Action<BgraFrame, string>? writeImage = null)
    {
        var replay = Guid.NewGuid();
        var archiveRoot = Path.Combine(root, "recorded_paths");
        var archive = new VisualReplayArchiveWriter(archiveRoot, replay, new Dictionary<string, string>(), maximumPendingImages: 24,
            writeImage);
        var image = StrongWallFrame(colored);
        for (var index = 0; index < 9; index++)
        {
            var captureAt = Timestamp(index);
            if (index == 5) captureAt += .0001; // Adjacent timestamp, same image and capture generation.
            if (index != 8)
                archive.Append(image, captureAt, captureAt, index == 4 ? 8UL : 7UL, Width, null);
            if (index == 2) archive.Append(image, captureAt, captureAt, 7, Width, null);
            var input = new VisualReplayGroundInput(Timestamp(index), 7,
                index == 1 ? [[[48, 0], [20, Height]]] : [], SemanticTuning: index == 7 ? null : GroundTheoryTuning.SemanticDefault,
                WindowsAtlasExcludingRects: windowsArchive ? [] : null);
            archive.RecordGroundInput(input);
            if (index == 3) archive.RecordGroundInput(input);
        }
        var summary = archive.FinishAsync().GetAwaiter().GetResult();
        var source = Path.Combine(archiveRoot, summary.DirectoryName);
        var clip = WriteLegacy(BotCaptureDomain.Gameplay);
        var hacker = WriteLegacy(BotCaptureDomain.Hacker);
        return new(clip, source, hacker, replay);

        string WriteLegacy(BotCaptureDomain domain)
        {
            var id = Guid.NewGuid();
            var output = Directory.CreateDirectory(Path.Combine(root, BotCaptureSession.DirectoryName(id))).FullName;
            var domainName = domain == BotCaptureDomain.Gameplay ? "gameplay" : "hacker";
            var manifest = new
            {
                schemaVersion = 1, id, status = "completed", startedAt = 100, duration = 1.2,
                observationCount = 9, usableObservationCount = 8, imageCount = 0, inputEventCount = 0,
                featureNames = BotRecordingContract.FeatureNames.Take(BotRecordingContract.LegacyWidth),
                actionSizes = BotRecordingContract.ActionSizes, historyLength = BotRecordingContract.HistoryLength,
                sampleInterval = BotRecordingContract.SampleInterval, botDomain = domainName, replayID = replay
            };
            File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            File.WriteAllText(Path.Combine(output, "audit-context.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, kind = "human-demonstration", domain = domainName, replayRoot = archiveRoot
            }, Json));
            var features = new double[BotRecordingContract.LegacyWidth];
            features[0] = .57;
            features[1] = .34;
            features[2] = .11;
            features[3] = .15;
            features[4] = .95;
            features[6] = 1;
            var rows = Enumerable.Range(0, 9).Select(index => new BotCaptureObservation(
                Timestamp(index), Timestamp(index), Timestamp(index) + .01, 7, 1, 0,
                features, index == 6 ? ["missing-knight"] : [], domain == BotCaptureDomain.Gameplay ? "detected" : "human-authored")
                { DetectionTimestamp = Timestamp(index) });
            File.WriteAllLines(Path.Combine(output, "events.jsonl"), rows.Select(row => JsonSerializer.Serialize(row, Json)));
            return output;
        }
    }

    private static BgraFrame StrongWallFrame(bool colored = false)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var index = (y * Width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = x < 58 ? (byte)92 : (byte)8;
            if (colored && x < 58)
            {
                pixels[index] = pixels[index + 1] = 0;
                pixels[index + 2] = 180;
            }
            pixels[index + 3] = 255;
        }
        return new(Width, Height, Width * 4, pixels, 1, DateTimeOffset.UnixEpoch);
    }

    private static double Timestamp(int index) => 100.05 + index * .1;

    private static void Reject(Action work)
    {
        try { work(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Foreign or modified archive evidence was accepted.");
    }

    private static void Check(bool value, string failure)
    {
        if (!value) throw new InvalidOperationException(failure);
    }
}
