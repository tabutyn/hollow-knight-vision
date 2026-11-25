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

    internal static Fixture WriteFixture(string root)
    {
        var replay = Guid.NewGuid();
        var archiveRoot = Path.Combine(root, "recorded_paths");
        var archive = new VisualReplayArchiveWriter(archiveRoot, replay, new Dictionary<string, string>(), maximumPendingImages: 24);
        var image = StrongWallFrame();
        for (var index = 0; index < 9; index++)
        {
            var captureAt = Timestamp(index);
            if (index == 5) captureAt += .0001; // Adjacent timestamp, same image and capture generation.
            if (index != 8)
                archive.Append(image, captureAt, captureAt, index == 4 ? 8UL : 7UL, Width, null);
            if (index == 2) archive.Append(image, captureAt, captureAt, 7, Width, null);
            var input = new VisualReplayGroundInput(Timestamp(index), 7,
                index == 1 ? [[[48, 0], [20, Height]]] : [], SemanticTuning: index == 7 ? null : GroundTheoryTuning.SemanticDefault);
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

    private static BgraFrame StrongWallFrame()
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var index = (y * Width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = x < 58 ? (byte)92 : (byte)8;
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
