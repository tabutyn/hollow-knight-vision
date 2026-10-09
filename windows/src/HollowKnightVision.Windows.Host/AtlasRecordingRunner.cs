using System.Diagnostics;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

public static class AtlasRecordingRunner
{
    public static async Task<int> RunAsync(
        string outputDirectory,
        int durationSeconds,
        int intervalMilliseconds,
        double pixelsPerWorldUnit)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new IOException($"Atlas output directory is not empty: {output}");
        }
        var window = HollowKnightWindowLocator.FindBest();
        if (window is null) throw new InvalidOperationException("Hollow Knight window not found.");

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(durationSeconds + 15));
        await using var receiver = await ReceiverConnection
            .ConnectWithRetryAsync(cancellation.Token).ConfigureAwait(false);
        var telemetry = new TelemetryMonitor();
        var monitorTask = telemetry.RunAsync(receiver, cancellation.Token);
        var mosaic = new GroundTruthAtlasMosaic(pixelsPerWorldUnit);
        var capture = new GdiWindowCapture();
        var elapsed = Stopwatch.StartNew();
        long captured = 0;
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var latestWindow = HollowKnightWindowLocator.FindBest()
                    ?? throw new InvalidOperationException("Hollow Knight window closed while recording atlas.");
                var frame = CpuBgraNormalizer.Normalize(capture.Capture(latestWindow, ++captured));
                var sample = telemetry.Latest;
                if (sample is not null
                    && DateTimeOffset.UtcNow - sample.ReceivedAt <= TimeSpan.FromMilliseconds(500))
                {
                    _ = mosaic.AddFrame(frame, sample.Value);
                }
                await Task.Delay(intervalMilliseconds, cancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        if (mosaic.AcceptedFrameCount == 0)
        {
            throw new InvalidOperationException("No fresh camera telemetry was available; atlas was not written.");
        }
        Write(output, mosaic, captured, elapsed.Elapsed);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            mode = "record-atlas",
            output,
            capturedFrames = captured,
            acceptedFrames = mosaic.AcceptedFrameCount,
            mosaic.TileCount,
            mosaic.PixelsPerWorldUnit,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void Write(
        string output,
        GroundTruthAtlasMosaic mosaic,
        long capturedFrames,
        TimeSpan elapsed)
    {
        Directory.CreateDirectory(output);
        var snapshots = mosaic.Snapshot();
        foreach (var tile in snapshots)
        {
            BmpWriter.Write(
                tile.Frame,
                Path.Combine(output, $"tile_{tile.Coordinate.X}_{tile.Coordinate.Y}.bmp"));
        }
        var metadata = new
        {
            schemaVersion = 1,
            createdAt = DateTimeOffset.UtcNow,
            coordinateSystem = "x-right-y-down; atlasY=-worldY*pixelsPerWorldUnit",
            mosaic.PixelsPerWorldUnit,
            mosaic.TileSize,
            mosaic.SourceTopInsetFraction,
            capturedFrames,
            acceptedFrames = mosaic.AcceptedFrameCount,
            elapsedMilliseconds = elapsed.TotalMilliseconds,
            tiles = snapshots.Select(tile => new
            {
                x = tile.Coordinate.X,
                y = tile.Coordinate.Y,
                filename = $"tile_{tile.Coordinate.X}_{tile.Coordinate.Y}.bmp",
                tile.WrittenPixelCount,
                tile.ObservationCount
            })
        };
        File.WriteAllText(
            Path.Combine(output, "atlas.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record ReceivedTelemetry(
        ReceiverGroundTruthSample Value,
        DateTimeOffset ReceivedAt);

    private sealed class TelemetryMonitor
    {
        private ReceivedTelemetry? latest;

        internal ReceivedTelemetry? Latest => Volatile.Read(ref latest);

        internal async Task RunAsync(
            ReceiverClient receiver,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await receiver.ReceiveLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("Receiver closed while recording atlas.");
                if (ReceiverWireCodec.MessageType(line) != "groundTruth") continue;
                var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
                if (sample.CameraAvailable && sample.HasFiniteCoordinates())
                {
                    Volatile.Write(ref latest, new ReceivedTelemetry(sample, DateTimeOffset.UtcNow));
                }
            }
        }
    }
}
