using System.Text.Json;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

internal static class GroundDetectionReplayAuditTests
{
    internal static void TestAllFrameReplayKeepsCapturedIndicesAndOrdinaryWorkerSelection()
    {
        var root = Directory.CreateTempSubdirectory("hkv-all-frame-replay-");
        try
        {
            var recorder = new InputPathRecorder();
            recorder.Start(1, DateTimeOffset.UnixEpoch, visualArchiveRoot: root.FullName);
            recorder.RecordVisualFrame(Frame(0), 1, 1, 1, null);
            recorder.RecordVisualFrame(Frame(1), 1.1, 1.1, 1, null);
            recorder.RecordVisualStage("visionStarted", 1.1, 1);
            var path = new RecordedInputPathStore(root.FullName).Save(recorder.Stop(1.2)!);
            var defaultReplay = VisualProductionReplay.Run(Options(path, Path.Combine(root.FullName, "worker")));
            var fullReplay = VerifyComplete(Options(path, Path.Combine(root.FullName, "all"), "--visual-replay-all-frames"));
            Check(defaultReplay is { Frames: 1, SkippedBeforeVision: 1, ArchiveFrameCount: 2 }
                && DiagnosticIndices(defaultReplay).SequenceEqual([1]), "The worker replay must retain its usual frame selection.");
            Check(fullReplay is { Frames: 2, SkippedBeforeVision: 0, ArchiveFrameCount: 2 }
                && DiagnosticIndices(fullReplay).SequenceEqual([0, 1]), "The all-frame replay must retain both captured indices.");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestPlatformPatchTrackingOnRecordedGameplayWhenRequested()
    {
        var input = Environment.GetEnvironmentVariable("HKV_PATCH_REPLAY_INPUT");
        var output = Environment.GetEnvironmentVariable("HKV_PATCH_REPLAY_OUTPUT");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Set HKV_PATCH_REPLAY_INPUT and HKV_PATCH_REPLAY_OUTPUT.");
        var report = VerifyComplete(Options(input, output, "--visual-replay-all-frames"));
        Check(DiagnosticIndices(report).SequenceEqual(Enumerable.Range(0, report.ArchiveFrameCount)),
            "Every archived capture must have a diagnostic row in source order.");
        Console.WriteLine($"Platform patch replay processed {report.Frames} archived captures.");
    }

    private static VisualProductionReplayReport VerifyComplete(VisualProductionReplayOptions options)
    {
        var report = VisualProductionReplay.Run(options);
        Check(report.ArchiveComplete && report.Frames == report.ArchiveFrameCount && report.MissingFrameIndices.Count == 0,
            $"The selected ground replay did not deliver the whole archive ({report.Frames} of {report.ArchiveFrameCount} frames).");
        return report;
    }

    private static VisualProductionReplayOptions Options(string input, string output, params string[] flags) =>
        VisualProductionReplayOptions.Parse(["--replay-visual-archive", input, "--visual-replay-output", output, .. flags]);

    private static IEnumerable<int> DiagnosticIndices(VisualProductionReplayReport report)
    {
        foreach (var row in File.ReadLines(report.DiagnosticsPath))
        {
            using var document = JsonDocument.Parse(row);
            yield return document.RootElement.GetProperty("index").GetInt32();
        }
    }

    private static BgraFrame Frame(long id)
    {
        const int width = 64, height = 36;
        var pixels = new byte[width * height * 4];
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = byte.MaxValue;
        return new(width, height, width * 4, pixels, id, DateTimeOffset.UnixEpoch);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
