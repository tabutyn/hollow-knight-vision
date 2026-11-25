using System.Diagnostics;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class HackerRecordingReplayAuditTests
{
    public static void TestRecordedHackerRoomLayoutWhenRequested()
    {
        var input = Environment.GetEnvironmentVariable("HKV_HACKER_RECORDING_AUDIT_INPUT");
        var output = Environment.GetEnvironmentVariable("HKV_HACKER_RECORDING_AUDIT_OUTPUT");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine("SKIP Hacker recording audit: set HKV_HACKER_RECORDING_AUDIT_INPUT and HKV_HACKER_RECORDING_AUDIT_OUTPUT.");
            return;
        }

        var inputPath = Path.GetFullPath(input);
        var summary = new RecordedInputPathStore().Load(inputPath);
        var archive = summary.VisualFrameArchive ?? throw new InvalidDataException("No source images were archived.");
        var complete = Environment.GetEnvironmentVariable("HKV_HACKER_RECORDING_AUDIT_ALLOW_INCOMPLETE") != "1";
        var reader = new VisualReplayArchiveReader(Path.GetDirectoryName(inputPath)!, archive, summary.Id, complete);
        var samples = UnambiguousSamples(summary.GroundTruthTrace);
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var rasterFrames = new List<int>();
        var joined = 0;
        var processed = 0;
        long peakWorkingSet = 0;
        double lastIntegrated = double.NegativeInfinity;
        string? previousScene = null;
        using var process = Process.GetCurrentProcess();
        foreach (var frame in reader.Manifest.Frames)
        {
            if (frame.FileName is null || frame.RenderedGameFrame is not { } marker
                || !samples.TryGetValue(marker, out var sample)) continue;
            joined++;
            var integrate = sample.SceneName != previousScene || frame.CaptureTimestamp - lastIntegrated >= 1;
            previousScene = sample.SceneName;
            var image = reader.ReadImage(frame);
            var prepared = preparer.Process(new(new(marker, image, frame.CaptureTimestamp, integrate), sample, frame.CaptureTimestamp));
            if (prepared is { } valid)
            {
                processed++;
                var result = pipeline.Process(valid);
                // Refined doorways can publish without registering a raster.
                if (pipeline.SaveSummary.RegistrationCount > rasterFrames.Count)
                {
                    rasterFrames.Add(result!.UnityFrame);
                    lastIntegrated = frame.CaptureTimestamp;
                }
            }
            if (joined % 100 == 0) SampleMemory();
        }
        SampleMemory();
        if (rasterFrames.Count == 0) throw new InvalidDataException("The replay produced no registered Hacker images.");

        var map = pipeline.Snapshot();
        var layout = HackerRoomLayout.Make(map.Rooms, map.Connections);
        var names = map.Rooms.ToDictionary(room => room.Id, room => room.SceneName);
        var connections = map.Connections.Select(edge =>
        {
            var from = layout.Positions[edge.FromRoomId];
            var to = layout.Positions[edge.ToRoomId];
            return new
            {
                id = edge.Id, fromScene = names[edge.FromRoomId], toScene = names[edge.ToRoomId],
                fromSide = edge.FromSide.ToString().ToLowerInvariant(),
                fromCoordinate = edge.FromCoordinate, toCoordinate = edge.ToCoordinate,
                floorAligned = edge.HasGroundedAlignment, freeform = layout.FreeformConnectionIds.Contains(edge.Id),
                connectorMismatchPixels = edge.FromSide.IsHorizontal()
                    ? Math.Abs(from.Y + edge.FromCoordinate - to.Y - edge.ToCoordinate)
                    : Math.Abs(from.X + edge.FromCoordinate - to.X - edge.ToCoordinate)
            };
        });
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourcePathID = summary.Id, exactJoinedImages = joined, processedImages = processed,
            sampling = "Every exact-joined image supplies geometry, with one-second raster cadence plus scene changes.",
            peakSampledWorkingSetBytes = peakWorkingSet, rasterFrames,
            rooms = map.Rooms.Select(room =>
            {
                var position = layout.Positions[room.Id];
                return new
                {
                    scene = room.SceneName, position = new[] { position.X, position.Y },
                    bounds = new[] { room.Bounds.MinX, room.Bounds.MinY, room.Bounds.Width, room.Bounds.Height }
                };
            }),
            connections,
            limits = "Independent Hacker geometry and reduced-cadence raster audit using exact mod camera data."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Hacker recorded layout: {map.Rooms.Count} rooms, {map.Connections.Count} connections, {joined} matched images.");

        void SampleMemory()
        {
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            if (peakWorkingSet >= 2L * 1024 * 1024 * 1024)
                throw new InvalidOperationException("Hacker replay reached 2 GiB before the audit finished.");
        }
    }

    private static Dictionary<int, ReceiverGroundTruthSample> UnambiguousSamples(IReadOnlyList<RecordedGroundTruthSample> source)
    {
        var samples = new Dictionary<int, ReceiverGroundTruthSample>();
        foreach (var group in source.GroupBy(row => HackerFrameSynchronizer.Key(row.Sample.UnityFrame)))
        {
            var first = group.First().Sample;
            // Compare the whole wire payload, including values inside actor lists.
            if (group.Count() > 1)
            {
                var encoded = ReceiverWireCodec.EncodeLine(first);
                if (group.Skip(1).Any(row => ReceiverWireCodec.EncodeLine(row.Sample) != encoded)) continue;
            }
            samples.Add(group.Key, first);
        }
        return samples;
    }
}
