using System.IO;
using System.Text.Json;
using HollowKnightVision.Windows.Core;
using HostProgram = HollowKnightVision.Windows.Host.Program;

internal static class VisualProductionReplayTests
{
    internal static void Run() => Exercise().GetAwaiter().GetResult();

    private static async Task Exercise()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hkv-production-visual-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const int width = 320, height = 180;
            const double startedAt = 120;
            var pathId = Guid.NewGuid();
            var writer = new VisualReplayArchiveWriter(root, pathId,
                new Dictionary<string, string> { ["inputStartedAt"] = "120" }, maximumPendingImages: 76);
            var pixels = Texture(width, height);
            for (var index = 0; index < 76; index++)
            {
                var time = startedAt + index * .04;
                var frame = new BgraFrame(width, height, width * 4, pixels, index, DateTimeOffset.UnixEpoch);
                writer.Append(frame, time, index == 75 ? double.NaN : time + .002, 7, width, index + 500);
                if (index is 1 or 75) continue;
                writer.RecordStage(new("visionStarted", time, 7));
                if (index != 0)
                    writer.RecordGroundInput(new(time, 7, ExcludingRects: [[[0, 150], [50, 30]]],
                        WindowsMovementButtons: InputButtons.Right));
                writer.RecordStage(new("visionCompleted", time, 7));
            }
            var archive = await writer.FinishAsync();
            var recorded = new RecordedInputPath(RecordedInputPath.CurrentSchemaVersion, pathId,
                DateTimeOffset.UnixEpoch, 4, null, null, null, [], trackingSamples: [], visualFrameArchive: archive);
            var source = new RecordedInputPathStore(root).Save(recorded);

            using var invalidOutput = new StringWriter();
            using var errors = new StringWriter();
            Task<int> RunHost(TextWriter output, params string[] args) =>
                HostProgram.RunAsync(args, standardOutput: output, standardError: errors,
                    normalRunner: _ => throw new InvalidOperationException("A visual replay tried to start live capture."));
            Check(await RunHost(invalidOutput, "--replay-visual-archive", source) == 64);
            var firstDirectory = Path.Combine(root, "first");
            Check(await RunHost(invalidOutput, "--replay-visual-archive", source, "--visual-replay-output", firstDirectory) == 1);
            Check(!Directory.Exists(firstDirectory), "A strict replay published a world for an incomplete archive.");

            using var firstOutput = new StringWriter();
            Check(await RunHost(firstOutput, "--replay-visual-archive", source, "--visual-replay-output", firstDirectory,
                "--visual-replay-allow-incomplete") == 0, errors.ToString());
            using var report = JsonDocument.Parse(firstOutput.ToString());
            var first = report.RootElement;
            Check(first.GetProperty("frames").GetInt32() == 74);
            Check(first.GetProperty("skippedBeforeVision").GetInt32() == 1);
            Check(first.GetProperty("missingFrameIndices").EnumerateArray().Single().GetInt32() == 75);
            Check(first.GetProperty("maskInputs").GetInt32() == 73);
            Check(first.GetProperty("groundVerifiedFrames").GetInt32() > 0, "The source floor did not anchor the camera.");
            Check(first.GetProperty("committedFrames").GetInt32() > 0 && first.GetProperty("atlasTiles").GetInt32() > 0,
                "Replay did not use the durable atlas pipeline.");
            Check(File.Exists(first.GetProperty("worldSnapshotPath").GetString()));
            var trace = new RecordedInputPathStore().Load(first.GetProperty("outputPath").GetString()!);
            Check(trace.SourcePathId == pathId && trace.TrackingSamples.Count == 74 && trace.GroundTruthTrace.Count == 0,
                "The replay output is not an independent visual trace.");
            var rows = File.ReadLines(first.GetProperty("diagnosticsPath").GetString()!).ToArray();
            Check(rows.Length == 74 && rows.Sum(line =>
            {
                using var row = JsonDocument.Parse(line);
                return row.RootElement.GetProperty("index").GetInt32() == 1 ? 1 : 0;
            }) == 0);

            var altered = Path.Combine(root, "path-altered-receiver.json");
            var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(source))!.AsObject();
            data["groundTruthSamples"] = System.Text.Json.Nodes.JsonNode.Parse("""
                [{"offset":0.1,"captureTimestamp":120.1,"sample":{"version":2,"type":"groundTruth",
                "sceneName":"unrelated-room","heroAvailable":true,"heroX":9999,"heroY":9999,
                "cameraAvailable":true,"cameraX":9999,"cameraY":9999}}]
                """);
            File.WriteAllText(altered, data.ToJsonString());
            using var secondOutput = new StringWriter();
            var secondDirectory = Path.Combine(root, "second");
            Check(await RunHost(secondOutput, "--replay-visual-archive", altered, "--visual-replay-output", secondDirectory,
                "--visual-replay-allow-incomplete") == 0, errors.ToString());
            using var second = JsonDocument.Parse(secondOutput.ToString());
            foreach (var key in new[] { "visualDigest", "coarseVisualDigest", "worldDigest", "atlasDigest" })
                Check(first.GetProperty(key).GetString() == second.RootElement.GetProperty(key).GetString(),
                    $"Reference receiver telemetry changed {key}.");
            Check(await RunHost(invalidOutput, "--replay-visual-archive", source, "--visual-replay-output", firstDirectory,
                "--visual-replay-allow-incomplete", "--visual-replay-frame-limit", "3") == 1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] Texture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pattern = (x * 73 ^ y * 131) & 31;
            var value = (byte)(y < 94 ? 130 + pattern / 2 : y < 98 ? pattern / 4 : 6 + pattern);
            var offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static void Check(bool condition, string message = "Visual production replay assertion failed.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
