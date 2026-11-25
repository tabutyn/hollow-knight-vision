using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class GeoCacheReplayAuditTests
{
    private const string Geo = "world.geo-deposit";
    private sealed record FrameResult(Guid ExampleId, int Labels, int Detected, int Captured,
        int Matched, bool PolicyObservation, bool PolicyHasDeposit, double[] LabelOverlaps);

    internal static void TestActiveDetectorFeedsLabeledGeoDepositsIntoGamePlayCapture()
    {
        var examplesRoot = PathFromEnvironment("HKV_GEO_LABELS_ROOT");
        var model = PathFromEnvironment("HKV_GEO_LABELS_MODEL");
        var output = PathFromEnvironment("HKV_GEO_LABELS_OUTPUT");
        if (Path.Exists(output)) throw new IOException("Choose a new path for the Geo cache replay report.");
        using var detector = new OnnxObjectDetector(model);
        Check(detector.TrainedClassIdentifiers.Contains(Geo), "The active export must have training evidence for Geo deposits.");
        var rows = new List<FrameResult>();
        foreach (var example in new LabelingExampleStore(examplesRoot).Load())
        {
            var labels = example.Manifest.Annotations.Where(item => !item.IsHardNegative
                && LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) == Geo)
                .Select(item => new NormalizedBox(item.X, item.Y, item.Width, item.Height)).ToArray();
            if (labels.Length == 0) continue;
            var frame = BgraPngReader.Read(example.ImagePath, rows.Count + 1);
            var detected = detector.Detect(frame);
            var eligible = detected.Where(item => item.ClassIdentifier == Geo && item.Confidence is >= .5f and <= 1).ToArray();
            var observation = Capture(frame, detected);
            var aspect = frame.Width / (double)frame.Height;
            var boxes = (observation.GeoCaches ?? []).Select(cache => new NormalizedBox(cache.X - cache.Width / 2,
                1 - (cache.Y + cache.Height / 2) * aspect, cache.Width, cache.Height * aspect)).ToArray();
            Check(observation.DetectionFrameId == frame.CaptureId && eligible.Length == boxes.Length
                && eligible.All(item => boxes.Any(box => item.Box.IntersectionOverUnion(box) > .999999)),
                "Every qualifying Geo detection must preserve its bounds in the exact frame’s Game Play capture.");
            var overlaps = labels.Select(label => boxes.Select(label.IntersectionOverUnion).DefaultIfEmpty(0).Max()).ToArray();
            var (policyObservation, policyHasDeposit) = ReadCurrentPolicy(observation);
            Check(!policyObservation || policyHasDeposit == (boxes.Length > 0), "The current policy input must preserve Geo presence.");
            rows.Add(new(example.Id, labels.Length, eligible.Length, boxes.Length,
                overlaps.Count(overlap => overlap >= .5), policyObservation, policyHasDeposit, overlaps));
        }

        var labeled = rows.Sum(row => row.Labels);
        var matched = rows.Sum(row => row.Matched);
        var captured = rows.Sum(row => row.Captured);
        var policyFrames = rows.Count(row => row.PolicyHasDeposit && row.Matched > 0);
        using var modelBytes = File.OpenRead(detector.ModelPath);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(model, "training.json")));
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            modelRun = receipt.RootElement.GetProperty("id").GetGuid(),
            modelSHA256 = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant(),
            frames = rows.Count,
            labeled,
            matched,
            captured,
            policyFrames,
            rows
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), overwrite: false);
        Check(matched > 0, "The production capture did not match any labeled Geo deposits at 0.5 IoU.");
        Console.WriteLine($"Geo capture matched {matched} of {labeled} labeled deposits, {captured} reported. {policyFrames} matched frames reached the current policy input.");
    }

    internal static BotCaptureObservation Capture(BgraFrame frame, IReadOnlyList<ObjectDetection> predictions)
    {
        const long generation = 1;
        const double timestamp = 10;
        var timed = new BgraFrame(frame.Width, frame.Height, frame.Stride, frame.Pixels, frame.CaptureId,
            DateTimeOffset.UnixEpoch.AddSeconds(timestamp));
        var timing = new BotCaptureTiming(timestamp, timestamp + .01, generation, 1, 0);
        var batch = new LiveObjectDetectionBatch(predictions, timed.CaptureId, timestamp, generation, .01);
        return BotObservationBuilder.Gameplay(timed, timing, batch, [], [], true, null, null, null, InputButtons.None);
    }

    internal static (bool Observation, bool Deposit) ReadCurrentPolicy(BotCaptureObservation row)
    {
        const int version = BotPolicyContract.CurrentVersion;
        var names = BotPolicyContract.Names(version)!;
        var index = names.ToList().IndexOf("geo_cache_present");
        Check(index >= 0, "The current policy schema must include Geo presence.");
        var values = new BotObservationHistory(version).Append(row, default, row.AvailableAt);
        return (values is not null, values is not null && values[values.Length - names.Count + index] == 1);
    }

    private static string PathFromEnvironment(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? Path.GetFullPath(value) : throw new InvalidOperationException($"Set {name}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
