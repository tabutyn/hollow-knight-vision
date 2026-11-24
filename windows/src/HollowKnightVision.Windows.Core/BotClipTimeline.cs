using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>Indexes the original capture images and the bot observations that belong to them.</summary>
public sealed class BotClipTimeline
{
    private readonly VisualReplayArchiveReader? archive;
    private readonly VisualReplayFrame[] sources;
    private readonly BotCaptureReviewFrame[] stills;
    private readonly Dictionary<ulong, BotCaptureReviewFrame[]> observations;
    private readonly double[] offsets;

    private BotClipTimeline(VisualReplayArchiveReader? archive, VisualReplayFrame[] sources,
        BotCaptureReviewFrame[] stills, Dictionary<ulong, BotCaptureReviewFrame[]> observations, double[] offsets,
        double duration)
    {
        this.archive = archive;
        this.sources = sources;
        this.stills = stills;
        this.observations = observations;
        this.offsets = offsets;
        Duration = duration;
    }

    public int Count => offsets.Length;
    public double Duration { get; }
    public bool UsesSourceFrames => sources.Length > 0;

    public static BotClipTimeline Open(string folder, string inputPaths, CancellationToken cancellationToken = default)
    {
        var (manifest, evidence) = BotCaptureSession.ReadReviewEvidence(folder, cancellationToken);
        var stills = evidence.Select(item => item.Frame).Where(item => item.ImageFile is not null)
            .OrderBy(item => item.Offset).ToArray();
        var duration = double.IsFinite(manifest.Duration) && manifest.Duration > 0 ? manifest.Duration : 0;
        VisualReplayArchiveReader? archive = null;
        VisualReplayFrame[] sources = [];
        if (manifest is { Status: "completed", SchemaVersion: BotRecordingContract.SchemaVersion }
            && double.IsFinite(manifest.StartedAt) && duration > 0)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                archive = VisualReplayArchiveReader.OpenClipFrames(inputPaths, manifest.ReplayId ?? manifest.Id);
                sources = archive.Manifest.Frames.Where(frame => VisualReplayArchiveReader.IsFrameFileName(frame)
                    && frame.CaptureTimestamp >= manifest.StartedAt && frame.CaptureTimestamp - manifest.StartedAt <= duration)
                    .OrderBy(frame => frame.CaptureTimestamp).ToArray();
                if (!sources.Any(frame => File.Exists(Path.Combine(archive.DirectoryPath, frame.FileName!)))) sources = [];
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                // Older clips and recordings whose frame archive did not finish still have preview images.
            }
        }
        var offsets = sources.Length > 0 ? sources.Select(frame => frame.CaptureTimestamp - manifest.StartedAt).ToArray()
            : stills.Select(frame => frame.Offset).ToArray();
        var byGeneration = sources.Length > 0 ? evidence.Where(item => item.Generation >= 0)
            .GroupBy(item => (ulong)item.Generation)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Frame).OrderBy(frame => frame.Offset).ToArray())
            : new Dictionary<ulong, BotCaptureReviewFrame[]>();
        return new(archive, sources, stills, byGeneration, offsets, Math.Max(duration, offsets.LastOrDefault()));
    }

    public double Offset(int index) => offsets[index];

    public int FrameAt(double offset)
    {
        if (offsets.Length == 0 || !double.IsFinite(offset)) return -1;
        return Math.Max(0, LastAtOrBefore(offsets.Length, index => offsets[index], offset));
    }

    public BotCaptureReviewFrame? Overlay(int index)
    {
        if (!UsesSourceFrames) return stills[index];
        var offset = Offset(index);
        if (!observations.TryGetValue(sources[index].CaptureGeneration, out var samples)) return null;
        var sample = LastAtOrBefore(samples.Length, index => samples[index].Offset, offset);
        return sample >= 0 && offset - samples[sample].Offset <= BotRecordingContract.MaximumCaptureAge
            ? samples[sample] : null;
    }

    public BgraFrame ReadSourceFrame(int index) => archive!.ReadImage(sources[index]);
    public string StillPath(int index) => stills[index].ImageFile!;

    private static int LastAtOrBefore(int count, Func<int, double> timeAt, double time)
    {
        var low = 0;
        var high = count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (timeAt(middle) <= time) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }
}
