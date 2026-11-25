using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class LoadingGapReplayAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record GridFrame(double CaptureTimestamp, int Width, int Height, byte[] Luma);
    private sealed record DirectionInput(int? HorizontalInput);
    private sealed record InputFrame(double CaptureTimestamp, DirectionInput? Input);
    private sealed record Archive(GridFrame[] LowResolutionFrames, InputFrame[] CoarseMotionSamples);

    internal sealed record MeasuredCut(double DepartureSeconds, double ArrivalSeconds, int Direction);

    internal static void TestArchivedVisualCuts()
    {
        var cuts = Replay(GetSetting("HKV_SCENE_CUT_AUDIT_INPUT"));
        Console.WriteLine($"Visual cut arrivals: {string.Join(", ", cuts.Select(cut => cut.ArrivalSeconds))}");
        if (cuts.Count != 1)
            throw new InvalidDataException($"The first-bench recording must contain one loading cut. Found {cuts.Count}.");
    }

    internal static void TestArchivedLoadingGapsWhenRequested()
    {
        var input = Path.GetFullPath(GetSetting("HKV_LOADING_GAP_AUDIT_INPUT"));
        var output = Path.GetFullPath(GetSetting("HKV_LOADING_GAP_AUDIT_OUTPUT"));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The loading-gap report must use a different path from the recording.");
        var windows = JsonSerializer.Deserialize<double[][]>(GetSetting("HKV_LOADING_GAP_AUDIT_WINDOWS"), Json);
        if (windows is not { Length: > 0 } || windows.Any(window => window is not { Length: 2 }
            || !double.IsFinite(window[0]) || !double.IsFinite(window[1]) || window[0] > window[1]))
            throw new InvalidDataException("Supply at least one valid loading-gap arrival window.");

        var cuts = Replay(input);
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(cuts, Json));
        foreach (var window in windows)
        {
            if (!cuts.Any(cut => cut.ArrivalSeconds >= window[0] && cut.ArrivalSeconds <= window[1]))
                throw new InvalidDataException($"No image-confirmed arrival between {window[0]} and {window[1]} seconds.");
        }
    }

    internal static IReadOnlyList<MeasuredCut> Replay(string path)
    {
        using var stream = File.OpenRead(path);
        var archive = JsonSerializer.Deserialize<Archive>(stream, Json);
        if (archive?.LowResolutionFrames is not { Length: > 0 } frames || archive.CoarseMotionSamples is not { } motion)
            throw new InvalidDataException("The loading-gap recording must contain image grids and input samples.");
        var inputs = new Dictionary<double, int?>();
        foreach (var sample in motion)
            inputs[sample.CaptureTimestamp] = sample.Input?.HorizontalInput;

        var start = frames[0].CaptureTimestamp;
        var tracker = new VisualSceneDiscontinuityTracker();
        var cuts = new List<MeasuredCut>();
        foreach (var frame in frames)
        {
            var direction = inputs.GetValueOrDefault(frame.CaptureTimestamp) switch
            {
                -1 => VisualRoomDirection.Left,
                1 => VisualRoomDirection.Right,
                _ => (VisualRoomDirection?)null
            };
            var grid = new LowResolutionMotionGrid(frame.Width, frame.Height, frame.Luma);
            // Keep this audit image-only. Neither scene identity nor a saved pose belongs in the detector.
            if (tracker.Observe(grid, frame.CaptureTimestamp, direction, default) is { } cut)
            {
                cuts.Add(new(cut.DepartureTimestamp - start, cut.ArrivalTimestamp - start, (int)cut.Direction));
            }
        }
        return cuts;
    }

    private static string GetSetting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Set {name} to run the recorded loading-gap audit.");
}
