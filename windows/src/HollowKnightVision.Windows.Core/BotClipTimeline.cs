using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>Indexes the original capture images and the bot observations that belong to them.</summary>
public sealed class BotClipTimeline
{
    private readonly VisualReplayArchiveReader? archive;
    private readonly VisualReplayFrame[] sources;
    private readonly BotCaptureReviewFrame[] stills;
    private readonly Dictionary<ulong, BotCaptureReviewFrame[]> observations;
    private readonly IReadOnlyList<InputPathStateChange>? inputStates;
    private readonly double[] offsets;

    private BotClipTimeline(VisualReplayArchiveReader? archive, VisualReplayFrame[] sources,
        BotCaptureReviewFrame[] stills, Dictionary<ulong, BotCaptureReviewFrame[]> observations, double[] offsets,
        double duration, IReadOnlyList<InputPathStateChange>? inputStates)
    {
        this.archive = archive;
        this.sources = sources;
        this.stills = stills;
        this.observations = observations;
        this.offsets = offsets;
        this.inputStates = inputStates;
        Duration = duration;
    }

    public int Count => offsets.Length;
    public double Duration { get; }
    public bool UsesSourceFrames => sources.Length > 0;

    public static BotClipTimeline Open(string folder, string inputPaths, CancellationToken cancellationToken = default)
    {
        var (manifest, evidence, inputs) = BotCaptureSession.ReadReviewEvidence(folder, cancellationToken, replayRoot: inputPaths);
        var stills = evidence.Select(item => item.Frame).Where(item => item.ImageFile is not null)
            .OrderBy(item => item.Offset).ToArray();
        var duration = double.IsFinite(manifest.Duration) && manifest.Duration > 0 ? manifest.Duration : 0;
        VisualReplayArchiveReader? archive = null;
        VisualReplayFrame[] sources = [];
        if (manifest is { Status: "completed", SchemaVersion: 1 or BotRecordingContract.SchemaVersion }
            && double.IsFinite(manifest.StartedAt) && duration > 0)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var replayId = manifest.ReplayId ?? manifest.Id;
                var linkedRoot = BotBlockadeReplaySource.LinkedRoot(folder, replayId);
                var replayRoot = linkedRoot is not null && File.Exists(Path.Combine(linkedRoot, $"frames-{replayId:D}", "manifest.json"))
                    ? linkedRoot : inputPaths;
                archive = VisualReplayArchiveReader.OpenClipFrames(replayRoot, replayId);
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
        var buttons = inputs.Count == manifest.InputEventCount ? BuildInputStates(inputs, manifest.StartedAt) : null;
        return new(archive, sources, stills, byGeneration, offsets, Math.Max(duration, offsets.LastOrDefault()), buttons);
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

    public InputButtons? HeldButtons(int index)
    {
        if (inputStates is null) return null;
        var step = LastAtOrBefore(inputStates.Count, key => inputStates[key].Offset, Offset(index));
        return step >= 0 ? inputStates[step].HeldButtons : InputButtons.None;
    }

    private static IReadOnlyList<InputPathStateChange>? BuildInputStates(IReadOnlyList<BotCaptureInput> inputs, double startedAt)
    {
        var states = new List<InputPathStateChange>(inputs.Count);
        var held = InputButtons.None;
        foreach (var input in inputs.OrderBy(item => item.Timestamp))
        {
            var name = input.Button.ToLowerInvariant() switch { "i" => "inventory", "p" => "pause", _ => input.Button };
            if (!Enum.TryParse<RecordedGameButton>(name, ignoreCase: true, out var button) || !Enum.IsDefined(button)) return null;
            var bit = button.ToInputButton();
            held = input.IsPressed ? held | bit : held & ~bit;
            states.Add(new(input.Timestamp - startedAt, held));
        }
        return states;
    }

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
