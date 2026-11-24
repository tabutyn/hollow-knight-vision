using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace HollowKnightVision.Windows.Core;

/// <summary>Archives the capture source without waiting for PNG encoding on the capture thread.</summary>
public sealed class VisualReplayArchiveWriter
{
    private sealed record PendingFrame(BgraFrame Image, int Index);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object gate = new();
    private readonly Guid pathId;
    private readonly string directoryName;
    private readonly string directory;
    private readonly IReadOnlyDictionary<string, string> configuration;
    private readonly int maximumPendingImages;
    private readonly Action<BgraFrame, string> writeImage;
    private readonly Channel<PendingFrame> queue;
    private readonly List<VisualReplayFrame> frames = [];
    private readonly List<VisualReplayStageEvent> stages = [];
    private readonly List<VisualReplayGroundInput> groundInputs = [];
    private readonly Task worker;
    private Task<RecordedVisualFrameArchive>? finish;
    private int pendingImages;
    private int peakPendingImages;

    public VisualReplayArchiveWriter(string root, Guid pathId, IReadOnlyDictionary<string, string> configuration,
        int maximumPendingImages = 12, Action<BgraFrame, string>? writeImage = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingImages);
        if (pathId == Guid.Empty) throw new ArgumentException("The input path ID is empty.", nameof(pathId));
        this.pathId = pathId;
        directoryName = $"frames-{pathId:D}";
        directory = Path.Combine(Path.GetFullPath(root), directoryName);
        this.configuration = new Dictionary<string, string>(configuration);
        this.maximumPendingImages = maximumPendingImages;
        this.writeImage = writeImage ?? PngWriter.Write;
        queue = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(maximumPendingImages)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        worker = Task.Run(WriteImagesAsync);
    }

    /// <remarks>The pixels must be owned by the capture frame and remain read-only.</remarks>
    public void Append(BgraFrame image, double captureTimestamp, double capturedAt, ulong captureGeneration,
        double solveWidth, int? renderedGameFrame)
    {
        lock (gate)
        {
            if (finish is not null) return;
            var record = new VisualReplayFrame(frames.Count, captureTimestamp, capturedAt, captureGeneration,
                solveWidth, image.Width, image.Height, renderedGameFrame, null, null, null);
            if (!double.IsFinite(captureTimestamp) || !double.IsFinite(capturedAt)
                || !double.IsFinite(solveWidth) || solveWidth <= 0)
            {
                frames.Add(record with { CaptureTimestamp = 0, CapturedAt = 0, SolveWidth = 0, Issue = "invalidCapture" });
                return;
            }
            if (pendingImages >= maximumPendingImages || !queue.Writer.TryWrite(new(image, record.Index)))
            {
                frames.Add(record with { Issue = "writerBackpressure" });
                return;
            }
            // The worker acquires this gate before publishing the result.
            frames.Add(record);
            pendingImages++;
            peakPendingImages = Math.Max(peakPendingImages, pendingImages);
        }
    }

    public void RecordStage(VisualReplayStageEvent stage)
    {
        lock (gate)
            if (finish is null && double.IsFinite(stage.CaptureTimestamp)) stages.Add(stage);
    }

    public void RecordGroundInput(VisualReplayGroundInput input)
    {
        lock (gate)
            if (finish is null && double.IsFinite(input.CaptureTimestamp)) groundInputs.Add(input);
    }

    public Task<RecordedVisualFrameArchive> FinishAsync(bool cancelled = false, string? incompleteReason = null)
    {
        lock (gate)
        {
            if (finish is not null) return finish;
            queue.Writer.TryComplete();
            return finish = Task.Run(() => FinishCoreAsync(cancelled, incompleteReason));
        }
    }

    private async Task WriteImagesAsync()
    {
        await foreach (var sample in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var name = sample.Index.ToString("D8", CultureInfo.InvariantCulture) + ".png";
            string? issue = null;
            string? checksum = null;
            try
            {
                var path = Path.Combine(directory, name);
                writeImage(sample.Image, path);
                using var stream = File.OpenRead(path);
                checksum = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                issue = $"imageWriteFailed: {error.Message}";
            }

            lock (gate)
            {
                frames[sample.Index] = frames[sample.Index] with
                {
                    FileName = checksum is null ? null : name,
                    Sha256 = checksum,
                    Issue = issue
                };
                pendingImages--;
            }
        }
    }

    private async Task<RecordedVisualFrameArchive> FinishCoreAsync(bool cancelled, string? incompleteReason)
    {
        await worker.ConfigureAwait(false);
        var saved = frames.Count(frame => frame.FileName is not null);
        var dropped = frames.Count(frame => frame.Issue == "writerBackpressure");
        var failed = frames.Count - saved - dropped;
        var summary = new RecordedVisualFrameArchive(directoryName, frames.Count, saved, dropped, failed,
            peakPendingImages, !cancelled && incompleteReason is null && saved > 0 && dropped == 0 && failed == 0,
            cancelled ? "cancelled" : incompleteReason);
        try
        {
            var manifest = new VisualReplayManifest(1, pathId, configuration, frames, stages, groundInputs, summary);
            AtomicFile.Write(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            summary = summary with { Complete = false, Issue = $"manifestWriteFailed: {error.Message}" };
        }
        return summary;
    }
}
