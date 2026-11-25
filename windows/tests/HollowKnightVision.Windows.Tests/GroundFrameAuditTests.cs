using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class GroundFrameAuditTests
{
    internal static void TestLiveTrackingWritesExactSourceAndFeatureBank()
    {
        var temp = Directory.CreateTempSubdirectory("hkv-ground-audit-pipeline-");
        try
        {
            var tables = Path.Combine(temp.FullName, "data");
            var audit = GroundFrameAudits.Launch(["--ground-audit-directory=" + tables,
                "--ground-raw-audit-directory=" + tables], pngEnvironment: null)!;
            var pipeline = new GameplayTrackingPipeline(new(), new(), deterministicGlobalSearch: true, groundAudits: audit);
            var (frame, luma) = TexturedFrame(640, 360);
            var blank = new byte[luma.Length];
            var floor = new DetectedFloorLine(176, new(0, frame.Width - 1));
            var edges = new GroundEdgeAnalysis(frame.Width, frame.Height, blank, luma);
            var comparison = new GroundComparisonAnalysis(frame.Width, frame.Height, blank, luma);
            var theory = new GroundTheoryAnalysis(comparison, GroundTheoryTuning.SemanticDefault, [], [floor]);
            var terrain = new GameplayTerrainAnalysis(edges, theory, [floor], [floor]);
            var masks = new[] { new PixelRect(10, 5, 14, 8) };
            var prepared = new GameplayTrackingPreparation(terrain, null, masks, []);
            var signal = new FrameSignalProfile(30, 30, 30, 90);
            _ = pipeline.Track(frame, new(null, null, masks, []), null, new(false, false, true, default), signal, 1, 4,
                "room", false, null, false);
            Check(!Directory.EnumerateFiles(tables).Any(), "A frame outside gameplay has no tracker evidence to save.");

            GameplayTrackedFrame? tracked = null;
            for (var i = 1; i <= 9; i++)
            {
                tracked = pipeline.Track(frame, prepared, null, new(true, true, false, new(true, false)), signal,
                    1 + i * .125, 4, "room", false, null, false);
                audit.DrainAsync().GetAwaiter().GetResult();
            }
            const string name = "00000009";
            var raw = Path.Combine(tables, "raw");
            using var rendered = Read(tables, name);
            using var bytes = Read(raw, name);
            using var bank = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(raw, name + ".bank")));
            Check(tracked is { Ground.GlobalFeatureCount: > 0, Ground.PoseVerified: true },
                "The fixture must establish verified ground and populate the real relocalization bank.");
            Check(File.ReadAllBytes(Path.Combine(raw, name + ".luma")).SequenceEqual(luma)
                && BgraPngReader.Read(Path.Combine(tables, name + ".png")).Pixels.SequenceEqual(frame.Pixels),
                "Capture must preserve the original source image and exact solver luma.");
            Check(rendered.RootElement.GetProperty("captureId").GetInt64() == frame.CaptureId
                && bytes.RootElement.GetProperty("generation").GetInt64() == 4
                && rendered.RootElement.GetProperty("timestamp").GetDouble() == bytes.RootElement.GetProperty("timestamp").GetDouble()
                && rendered.RootElement.GetProperty("poseVerified").GetBoolean()
                && rendered.RootElement.GetProperty("exclusions").GetRawText() == "[[10,347,14,8]]"
                && rendered.RootElement.GetProperty("atlasLines").GetArrayLength() == tracked!.Ground.AtlasLines.Count,
                "Both streams must bind frame and ground results to the same generation, time and converted masks.");
            var features = bank.RootElement.GetProperty("features");
            Check(features.GetArrayLength() == tracked!.Ground.GlobalFeatureCount
                && features[0].GetProperty("descriptor").GetArrayLength() > 0
                && features[0].GetProperty("pixels").GetBytesFromBase64().Length > 0
                && bytes.RootElement.GetProperty("featureBank").GetString() == name + ".bank",
                "Bank diagnostics must use the real tracker descriptors and matching patch bytes.");
        }
        finally { temp.Delete(recursive: true); }
    }

    internal static void TestCadenceAndFailedWritesStayBounded()
    {
        var temp = Directory.CreateTempSubdirectory("hkv-ground-audit-disk-");
        try
        {
            var images = Path.Combine(temp.FullName, "png");
            var raw = Path.Combine(temp.FullName, "raw");
            var environment = Path.Combine(temp.FullName, "environment");
            Check(GroundFrameAudits.Launch([], pngEnvironment: null) is null
                && GroundFrameAudits.Launch(["--ground-raw-audit-directory=relative"], pngEnvironment: null) is null,
                "Audit capture must require an explicit absolute path.");
            Check(GroundFrameAudits.Launch([], environment) is not null && Directory.Exists(environment),
                "The PNG stream must accept the same environment fallback as Mac.");
            Directory.CreateDirectory(images);
            File.WriteAllText(Path.Combine(images, "00000012.json"), "original");
            using var writing = new CountdownEvent(2);
            using var release = new ManualResetEventSlim();
            var started = 0;
            var audit = GroundFrameAudits.Launch(["--ground-audit-directory=" + environment, "--ground-audit-directory=" + images,
                "--ground-raw-audit-directory=" + raw], environment, _ =>
            {
                if (Interlocked.Increment(ref started) > 2) return;
                writing.Signal();
                release.Wait(TimeSpan.FromSeconds(10));
            })!;
            var (frame, luma) = TexturedFrame(160, 110);
            var tracker = new GroundHypothesisTracker();
            var tracking = GroundHypothesisTrackingResult.Empty with
            {
                CameraPosition = new(4, 9), CameraTranslation = new(-2, 3), GlobalCorrection = new(7, -5),
                Features = [new(1, 2, 3, new PixelRect(13, 20, 8, 6), GroundHypothesisClassification.GroundPlane, null, null)]
            };
            try
            {
                Offer(1);
                Check(writing.Wait(TimeSpan.FromSeconds(10)), "Both writes must run independently of capture.");
                Offer(1.125);
                Offer(1.25);
                Check(!audit.DrainAsync().IsCompleted, "Only the already admitted writes should still be pending.");
            }
            finally
            {
                release.Set();
                audit.DrainAsync().GetAwaiter().GetResult();
            }
            Offer(1.375);
            audit.DrainAsync().GetAwaiter().GetResult();
            Offer(1.4375);
            audit.DrainAsync().GetAwaiter().GetResult();
            using var png = Read(images, "00000014");
            using var rawResult = Read(raw, "00000002");
            Check(rawResult.RootElement.GetProperty("skippedWrites").GetInt64() == 2
                && !rawResult.RootElement.TryGetProperty("featureBank", out _)
                && rawResult.RootElement.GetProperty("delta").GetRawText() == "[-2,3]"
                && rawResult.RootElement.GetProperty("correction").GetRawText() == "[7,-5]",
                "The raw stream must count busy frames and avoid taking more than one bank snapshot per second.");
            Check(Directory.EnumerateFiles(images, "*.png").Count() == 2
                && !Directory.EnumerateFiles(environment).Any()
                && File.ReadAllText(Path.Combine(images, "00000012.json")) == "original"
                && png.RootElement.GetProperty("matchedPatchRects").GetRawText() == "[[8,81,14,9]]"
                && png.RootElement.GetProperty("features")[0].GetProperty("classification").GetString() == "groundPlane",
                "The PNG stream must prefer the last launch option, retain existing evidence and respect its cadence.");

            var rawCollision = Path.Combine(raw, "00000004.json");
            File.WriteAllText(rawCollision, "another recording");
            Offer(2.25);
            audit.DrainAsync().GetAwaiter().GetResult();
            Check(File.ReadAllText(rawCollision) == "another recording"
                && !File.Exists(Path.Combine(raw, "00000004.luma")) && !File.Exists(Path.Combine(raw, "00000004.bank")),
                "A conflicting metadata publication must roll back only files owned by this sample.");
            Offer(3.5);
            audit.DrainAsync().GetAwaiter().GetResult();
            Check(File.Exists(Path.Combine(raw, "00000005.json")), "Failed disk work must release the next buffer slot.");

            void Offer(double timestamp) => audit.Record(frame, timestamp, 7, luma, [], [],
                GroundTheoryTuning.SemanticDefault, tracking, [new(8, 20, 14, 9)], tracker);
        }
        finally { temp.Delete(recursive: true); }
    }

    private static JsonDocument Read(string directory, string id) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, id + ".json")));

    private static (BgraFrame Frame, byte[] Luma) TexturedFrame(int width, int height)
    {
        var luma = new byte[width * height];
        new Random(53).NextBytes(luma);
        var pixels = new byte[luma.Length * 4];
        for (var i = 0; i < luma.Length; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = luma[i];
            pixels[i * 4 + 3] = 255;
        }
        return (new(width, height, width * 4, pixels, 64, DateTimeOffset.UnixEpoch), luma);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
