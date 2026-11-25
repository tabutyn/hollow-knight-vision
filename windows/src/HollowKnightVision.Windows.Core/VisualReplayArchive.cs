using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record RecordedVisualFrameArchive(string DirectoryName, int CapturedFrameCount, int SavedFrameCount,
    int DroppedFrameCount, int FailedFrameCount, int PeakPendingImages, bool Complete, string? Issue = null);

public sealed record VisualReplayFrame(int Index, double CaptureTimestamp, double CapturedAt, ulong CaptureGeneration,
    double SolveWidth, int Width, int Height, int? RenderedGameFrame, string? FileName, string? Sha256, string? Issue);

public sealed record VisualReplayStageEvent(string Stage, double CaptureTimestamp, ulong CaptureGeneration);

public sealed record VisualReplayGroundInput(double CaptureTimestamp, ulong CaptureGeneration,
    double[][][]? ExcludingRects = null, double[][]? KnightRect = null, GroundTheoryTuning? Tuning = null,
    GroundTheoryTuning? SemanticTuning = null, double[][]? ObservedPlayerBounds = null,
    int? HorizontalDrive = null, bool? RecentJump = null, InputButtons? WindowsMovementButtons = null,
    double[][][]? WindowsAtlasExcludingRects = null)
{
    public static GameplaySceneMasks RestoreMasks(VisualReplayGroundInput? input, BgraFrame frame)
    {
        StencilRect Rect(double[][] value)
        {
            if (value is not [{ Length: 2 }, { Length: 2 }] || !value.All(pair => pair.All(double.IsFinite)))
                throw new InvalidDataException("Visual replay contains an invalid recorded mask.");
            return new(value[0][0], frame.Height - value[0][1] - value[1][1], value[1][0], value[1][1]);
        }

        var omissions = input?.ExcludingRects?.Select(Rect).ToArray() ?? [];
        var atlas = input?.WindowsAtlasExcludingRects?.Select(Rect).ToArray() ?? omissions;
        var knight = input?.KnightRect is { } box
            ? SceneRegions.PixelRectangles(frame.Width, frame.Height, [Rect(box)]) : [];
        return new(SceneRegions.Startup() with { HudStencilRects = atlas },
            SceneRegions.Startup() with { HudStencilRects = omissions },
            SceneRegions.PixelRectangles(frame.Width, frame.Height, atlas),
            SceneRegions.PixelRectangles(frame.Width, frame.Height, omissions),
            knight.Count > 0 ? knight[0] : null);
    }

    public static VisualReplayGroundInput FromTracking(double timestamp, ulong generation, int imageHeight,
        GameplaySceneMasks masks, InputButtons? buttons)
    {
        // The shared archive uses CGRect coordinates. Convert once at the wire boundary.
        double[][] Rect(StencilRect rect) =>
            [[rect.X, imageHeight - rect.Bottom], [rect.Width, rect.Height]];
        var knight = masks.GroundRegions.Knight is { } bounds ? Rect(bounds) : null;
        return new(timestamp, generation, masks.GroundRegions.OmittedRects.Select(Rect).ToArray(), knight,
            GroundTheoryTuning.Default, GroundTheoryTuning.SemanticDefault, knight,
            buttons.HasValue ? KnightLevelWalk.Direction(buttons.Value) : null,
            WindowsMovementButtons: buttons, WindowsAtlasExcludingRects: masks.AtlasRegions.OmittedRects.Select(Rect).ToArray());
    }
}

public sealed record VisualReplayManifest(int SchemaVersion, [property: JsonPropertyName("pathID")] Guid PathId,
    IReadOnlyDictionary<string, string> Configuration, IReadOnlyList<VisualReplayFrame> Frames,
    IReadOnlyList<VisualReplayStageEvent> StageEvents, IReadOnlyList<VisualReplayGroundInput> GroundInputs,
    RecordedVisualFrameArchive Summary);

public sealed record VisualReplayArchiveInspection([property: JsonPropertyName("pathID")] Guid PathId,
    bool ArchiveAvailable, bool Complete, int CapturedFrames, int ReadableFrames, int DroppedFrames, int FailedFrames,
    int GroundInputs, int GroundInputsWithImages, int ExactFrameMarkers, double? MarkerCoveragePercent,
    double? FirstCaptureTimestamp, double? LastCaptureTimestamp);

/// <summary>Validates exact frame evidence from a saved input path without starting a game capture.</summary>
public sealed class VisualReplayArchiveReader
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = LabelingExampleStore.CreateJsonOptions();

    public VisualReplayManifest Manifest { get; }
    public string DirectoryPath { get; }

    public VisualReplayArchiveReader(string root, RecordedVisualFrameArchive summary, Guid expectedPathId,
        bool requireComplete = true)
        : this(root, expectedPathId, summary, requireComplete) { }

    internal static VisualReplayArchiveReader OpenClipFrames(string root, Guid pathId) =>
        new(root, pathId, expectedSummary: null, requireComplete: false);

    private VisualReplayArchiveReader(string root, Guid expectedPathId, RecordedVisualFrameArchive? expectedSummary,
        bool requireComplete)
    {
        var directoryName = $"frames-{expectedPathId:D}";
        if (expectedPathId == Guid.Empty || expectedSummary is not null && expectedSummary.DirectoryName != directoryName)
            throw new InvalidDataException("Visual replay directory does not match the input path.");

        DirectoryPath = Path.Combine(Path.GetFullPath(root), directoryName);
        using var manifestStream = File.OpenRead(Path.Combine(DirectoryPath, "manifest.json"));
        Manifest = JsonSerializer.Deserialize<VisualReplayManifest>(manifestStream, Json)
            ?? throw new InvalidDataException("Visual replay manifest is empty.");
        if (Manifest.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Visual replay archive schema {Manifest.SchemaVersion} is unsupported.");

        var summary = Manifest.Summary;
        if (Manifest.PathId != expectedPathId || summary?.DirectoryName != directoryName
            || expectedSummary is not null && summary != expectedSummary || Manifest.Frames is null
            || Manifest.GroundInputs is null || Manifest.Configuration is null || Manifest.StageEvents is null
            || Manifest.Frames.Count != summary.CapturedFrameCount)
            throw new InvalidDataException("Visual replay manifest does not match the input path.");

        var saved = 0;
        var dropped = 0;
        for (var index = 0; index < Manifest.Frames.Count; index++)
        {
            var frame = Manifest.Frames[index];
            if (frame is null || frame.Index != index || frame.FileName is not null && frame.Issue is not null)
                throw new InvalidDataException($"Visual replay frame {index} has inconsistent metadata.");
            if (frame.FileName is not null) saved++;
            if (frame.Issue == "writerBackpressure") dropped++;
        }
        var failed = Manifest.Frames.Count - saved - dropped;
        if (saved != summary.SavedFrameCount || dropped != summary.DroppedFrameCount
            || failed != summary.FailedFrameCount)
            throw new InvalidDataException("Visual replay frame counts do not match the input path.");
        if (requireComplete && (!summary.Complete || saved == 0 || dropped != 0 || failed != 0))
            throw new InvalidDataException("Visual replay archive is incomplete.");
    }

    public BgraFrame ReadImage(VisualReplayFrame frame)
    {
        if (frame.Index < 0 || frame.Index >= Manifest.Frames.Count || Manifest.Frames[frame.Index] != frame
            || frame.FileName is not { } fileName
            || !IsFrameFileName(frame)
            || frame.Issue is not null || !double.IsFinite(frame.SolveWidth) || frame.SolveWidth <= 0
            || !double.IsFinite(frame.CaptureTimestamp) || !double.IsFinite(frame.CapturedAt))
            throw new InvalidDataException($"Visual replay frame {frame.Index} has invalid evidence.");

        using var imageStream = File.OpenRead(Path.Combine(DirectoryPath, fileName));
        var checksum = Convert.ToHexString(SHA256.HashData(imageStream));
        if (!string.Equals(checksum, frame.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Visual replay frame {frame.Index} has an invalid image checksum.");
        var image = BgraPngReader.Read(imageStream, frame.Index);
        if (image.Width != frame.Width || image.Height != frame.Height)
            throw new InvalidDataException($"Visual replay frame {frame.Index} has unexpected image dimensions.");
        return image;
    }

    internal static bool IsFrameFileName(VisualReplayFrame frame) =>
        frame.FileName == frame.Index.ToString("D8", CultureInfo.InvariantCulture) + ".png";

    public static VisualReplayArchiveInspection Inspect(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var recorded = new RecordedInputPathStore().Load(fullPath);
        if (recorded.VisualFrameArchive is not { } summary)
            return new(recorded.Id, false, false, 0, 0, 0, 0, 0, 0, 0, null, null, null);

        var archive = new VisualReplayArchiveReader(Path.GetDirectoryName(fullPath)!, summary, recorded.Id,
            requireComplete: false);
        var manifest = archive.Manifest;
        var imageKeys = new HashSet<(ulong Generation, long TimestampBits)>();
        var readable = 0;
        var markers = 0;
        foreach (var frame in manifest.Frames)
        {
            if (frame.FileName is null) continue;
            archive.ReadImage(frame);
            readable++;
            if (frame.RenderedGameFrame.HasValue) markers++;
            imageKeys.Add((frame.CaptureGeneration, BitConverter.DoubleToInt64Bits(frame.CaptureTimestamp)));
        }
        var matched = 0;
        foreach (var input in manifest.GroundInputs)
        {
            if (input is null || !double.IsFinite(input.CaptureTimestamp))
                throw new InvalidDataException("Visual replay ground input has invalid timing.");
            if (imageKeys.Contains((input.CaptureGeneration, BitConverter.DoubleToInt64Bits(input.CaptureTimestamp))))
                matched++;
        }
        var complete = summary.Complete && readable > 0 && readable == summary.CapturedFrameCount
            && matched == manifest.GroundInputs.Count;
        return new(recorded.Id, true, complete, summary.CapturedFrameCount, readable, summary.DroppedFrameCount,
            summary.FailedFrameCount, manifest.GroundInputs.Count, matched, markers,
            readable > 0 ? markers * 100d / readable : null,
            manifest.Frames.FirstOrDefault()?.CaptureTimestamp, manifest.Frames.LastOrDefault()?.CaptureTimestamp);
    }
}
