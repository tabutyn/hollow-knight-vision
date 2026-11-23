using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public readonly record struct GroundTruthRect(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double MidX => X + Width * .5;
    public double MidY => Y + Height * .5;
}

/// <summary>
/// Development-only transform derived from mod receiver telemetry. Screen
/// pixels are top-left-origin; atlas/world Y increases upward.
/// </summary>
public sealed record GroundTruthCameraTransform(
    string SceneName,
    AtlasPoint CameraWorld,
    double PixelsPerWorldUnitX,
    double PixelsPerWorldUnitY,
    WorldSize FrameSize,
    double ObservedAt = 0)
{
    public static GroundTruthCameraTransform? FromSample(
        ReceiverGroundTruthSample sample,
        double observedAt,
        WorldSize? frameSize = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var size = frameSize ?? new WorldSize(640, 360);
        if (!sample.CameraAvailable
            || size.Width <= 1
            || size.Height <= 1
            || sample.ProjectionPixelWidth is not > 0
            || sample.ProjectionPixelHeight is not > 0)
        {
            return null;
        }

        var rawY = sample.PixelsPerWorldUnitY
            ?? (sample.OrthographicSize > 0
                ? sample.ProjectionPixelHeight.Value / (2 * sample.OrthographicSize)
                : 0);
        var rawX = sample.PixelsPerWorldUnitX ?? rawY;
        var scaleX = rawX * size.Width / sample.ProjectionPixelWidth.Value;
        var scaleY = rawY * size.Height / sample.ProjectionPixelHeight.Value;
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY)
            || scaleX <= 0 || scaleY <= 0)
        {
            return null;
        }

        return new(
            sample.SceneName,
            new AtlasPoint(sample.CameraX, sample.CameraY),
            scaleX,
            scaleY,
            size,
            observedAt);
    }

    public AtlasPoint ScreenToWorld(AtlasPoint topLeftPixel) => new(
        CameraWorld.X
            + (topLeftPixel.X - FrameSize.Width * .5) / PixelsPerWorldUnitX,
        CameraWorld.Y
            + (FrameSize.Height * .5 - topLeftPixel.Y) / PixelsPerWorldUnitY);

    public AtlasPoint AtlasToWorld(AtlasPoint atlasPoint, GroundTruthRect liveBounds) => new(
        CameraWorld.X + (atlasPoint.X - liveBounds.MidX) / PixelsPerWorldUnitX,
        CameraWorld.Y + (atlasPoint.Y - liveBounds.MidY) / PixelsPerWorldUnitY);

    public AtlasPoint WorldToAtlas(AtlasPoint worldPoint, GroundTruthRect liveBounds) => new(
        liveBounds.MidX + (worldPoint.X - CameraWorld.X) * PixelsPerWorldUnitX,
        liveBounds.MidY + (worldPoint.Y - CameraWorld.Y) * PixelsPerWorldUnitY);
}

public enum GroundTruthLineKind
{
    Positive,
    Negative,
    Wall,
    Spike,
    Door,
    DoorVertical,
}

public enum GroundTruthTerrainTool
{
    Ground,
    Wall,
    Spike,
    Door,
}

public enum TerrainLineEnd
{
    Start,
    End,
}

public sealed record GroundTruthDoorLatch(
    [property: JsonPropertyName("groundID"), JsonRequired] Guid GroundId,
    [property: JsonRequired] TerrainLineEnd End,
    [property: JsonRequired] double Fraction);

[method: JsonConstructor]
public sealed record GroundTruthWorldLine(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string SceneName,
    [property: JsonRequired] double MinimumWorldX,
    [property: JsonRequired] double MaximumWorldX,
    [property: JsonRequired] double WorldY,
    [property: JsonRequired] GroundTruthLineKind Kind,
    [property: JsonRequired] bool SeededFromDetector)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EndWorldY { get; init; }

    [JsonPropertyName("startCornerID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? StartCornerId { get; init; }

    [JsonPropertyName("endCornerID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? EndCornerId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroundTruthDoorLatch? GroundLatch { get; init; }

    public GroundTruthWorldLine(string sceneName, double minimumWorldX, double maximumWorldX,
        double worldY, GroundTruthLineKind kind, bool seededFromDetector)
        : this(Guid.NewGuid(), sceneName, minimumWorldX, maximumWorldX, worldY, kind, seededFromDetector) { }

    [JsonIgnore]
    public bool IsVertical => Kind is GroundTruthLineKind.Wall or GroundTruthLineKind.DoorVertical;

    [JsonIgnore]
    public AtlasPoint Start => new(MinimumWorldX, WorldY);

    [JsonIgnore]
    public AtlasPoint End => new(MaximumWorldX, EndWorldY ?? WorldY);

    [JsonIgnore]
    public GroundTruthWorldLine Standardized => IsVertical || MinimumWorldX <= MaximumWorldX ? this : this with
    {
        MinimumWorldX = MaximumWorldX,
        MaximumWorldX = MinimumWorldX,
        StartCornerId = EndCornerId,
        EndCornerId = StartCornerId
    };

    internal AtlasPoint PointAt(TerrainLineEnd end) => end == TerrainLineEnd.Start ? Start : End;
    internal Guid? CornerAt(TerrainLineEnd end) => end == TerrainLineEnd.Start ? StartCornerId : EndCornerId;

    internal GroundTruthWorldLine WithCorner(TerrainLineEnd end, Guid? id) => end == TerrainLineEnd.Start
        ? this with { StartCornerId = id } : this with { EndCornerId = id };

    internal GroundTruthWorldLine WithExtent(double first, double last, double across)
    {
        var low = Math.Min(first, last);
        var high = Math.Max(first, last);
        return IsVertical
            ? this with { MinimumWorldX = across, MaximumWorldX = across, WorldY = low, EndWorldY = high }
            : this with { MinimumWorldX = low, MaximumWorldX = high, WorldY = across, EndWorldY = null };
    }
}

public sealed partial class GroundTruthLineDocument : IEquatable<GroundTruthLineDocument>
{
    public const int CurrentSchemaVersion = 2;

    [JsonRequired]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonRequired]
    public HashSet<string> SeededScenes { get; set; } = [];

    [JsonRequired]
    public List<GroundTruthWorldLine> Lines { get; set; } = [];

    // Optional v2 collections preserve the floor-only contract used by replay tools.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GroundTruthWorldLine>? Walls { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GroundTruthWorldLine>? Spikes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GroundTruthWorldLine>? Doors { get; set; }

    [JsonIgnore]
    public IEnumerable<GroundTruthWorldLine> AuthoredLines => AllLines.Where(line => !line.SeededFromDetector);

    private IEnumerable<GroundTruthWorldLine> AllLines => Lines.Concat(Walls ?? []).Concat(Spikes ?? []).Concat(Doors ?? []);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Lines is null || SeededScenes is null
            || SeededScenes.Any(scene => string.IsNullOrEmpty(scene) || scene.Length > 256)
            || Lines.Count + (long)(Walls?.Count ?? 0) + (Spikes?.Count ?? 0) + (Doors?.Count ?? 0) > 20_000
            || Lines.Any(line => line?.Kind is not (GroundTruthLineKind.Positive or GroundTruthLineKind.Negative))
            || (Walls ?? []).Any(line => line?.Kind != GroundTruthLineKind.Wall)
            || (Spikes ?? []).Any(line => line?.Kind != GroundTruthLineKind.Spike)
            || (Doors ?? []).Any(line => line?.Kind is not (GroundTruthLineKind.Door or GroundTruthLineKind.DoorVertical)))
            throw new GroundTruthLineStoreException("Invalid ground-label document");

        var ids = new HashSet<Guid>();
        foreach (var line in AllLines)
        {
            var endY = line.End.Y;
            if (line.Id == Guid.Empty || !ids.Add(line.Id) || string.IsNullOrEmpty(line.SceneName)
                || line.SceneName.Length > 256
                || !IsWorldCoordinate(line.MinimumWorldX) || !IsWorldCoordinate(line.MaximumWorldX)
                || !IsWorldCoordinate(line.WorldY) || !IsWorldCoordinate(endY)
                || line.MaximumWorldX < line.MinimumWorldX || line.MaximumWorldX - line.MinimumWorldX > 100_000
                || (line.IsVertical
                    ? line.EndWorldY is null || line.MinimumWorldX != line.MaximumWorldX
                        || endY < line.WorldY || endY - line.WorldY > 100_000
                    : line.EndWorldY is not null))
                throw new GroundTruthLineStoreException("Invalid world-space terrain line");
        }
        ValidateAttachments();
    }

    private static bool IsWorldCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 1_000_000;

    internal void AddLine(GroundTruthWorldLine line)
    {
        var lines = line.Kind switch
        {
            GroundTruthLineKind.Wall => Walls ??= [],
            GroundTruthLineKind.Spike => Spikes ??= [],
            GroundTruthLineKind.Door or GroundTruthLineKind.DoorVertical => Doors ??= [],
            _ => Lines
        };
        lines.Add(line);
    }

    internal IReadOnlyList<GroundTruthWorldLine> LinesFor(GroundTruthTerrainTool tool) => tool switch
    {
        GroundTruthTerrainTool.Wall => Walls ?? [],
        GroundTruthTerrainTool.Spike => Spikes ?? [],
        GroundTruthTerrainTool.Door => Doors ?? [],
        _ => Lines
    };

    public GroundTruthLineDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        SeededScenes = new HashSet<string>(SeededScenes, StringComparer.Ordinal),
        Lines = [.. Lines],
        Walls = Walls is null ? null : [.. Walls],
        Spikes = Spikes is null ? null : [.. Spikes],
        Doors = Doors is null ? null : [.. Doors]
    };

    public bool Equals(GroundTruthLineDocument? other) => other is not null
        && SchemaVersion == other.SchemaVersion && SeededScenes.SetEquals(other.SeededScenes)
        && Lines.SequenceEqual(other.Lines) && (Walls ?? []).SequenceEqual(other.Walls ?? [])
        && (Spikes ?? []).SequenceEqual(other.Spikes ?? []) && (Doors ?? []).SequenceEqual(other.Doors ?? []);

    public override bool Equals(object? obj) => Equals(obj as GroundTruthLineDocument);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        foreach (var scene in SeededScenes.Order(StringComparer.Ordinal)) hash.Add(scene);
        foreach (var line in AllLines) hash.Add(line);
        return hash.ToHashCode();
    }
}

public sealed class GroundTruthLineStoreException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Atomic, schema-2 world-space ground-line storage.</summary>
public sealed class GroundTruthLineStore
{
    public const string Filename = "world-ground-lines-v2.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public GroundTruthLineStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? DefaultPath);
    }

    public string FilePath { get; }

    public static string DefaultPath
    {
        get
        {
            var explicitRoot = Environment.GetEnvironmentVariable("HKV_GROUND_LABEL_ROOT");
            var root = !string.IsNullOrEmpty(explicitRoot)
                ? explicitRoot
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HollowKnightVision");
            return Path.Combine(root, Filename);
        }
    }

    public GroundTruthLineDocument Load()
    {
        if (!File.Exists(FilePath)) return new();
        var document = JsonSerializer.Deserialize<GroundTruthLineDocument>(
            File.ReadAllBytes(FilePath),
            JsonOptions) ?? throw new GroundTruthLineStoreException(
                "Invalid ground-label document");
        document.Validate();
        return document;
    }

    public void Save(GroundTruthLineDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(FilePath)}.tmp-{Guid.NewGuid():N}");
        var created = false;
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                created = true;
                output.Write(data);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}

public enum GroundTruthOverlayLineKind
{
    PositiveGroundTruth,
    NegativeGroundTruth,
    SelectedGroundTruth,
    SpikeGroundTruth,
    DoorGroundTruth,
}

public readonly record struct GroundTruthOverlayLine(
    AtlasPoint Start,
    AtlasPoint End,
    GroundTruthOverlayLineKind Kind);

public static class GroundTruthLineProjection
{
    public static IReadOnlyList<GroundTruthOverlayLine> OverlayLines(
        GroundTruthLineDocument document,
        GroundTruthCameraTransform transform,
        GroundTruthRect liveBounds,
        Guid? selectedId,
        GroundTruthWorldLine? draft)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(transform);
        var lines = draft is null ? document.AuthoredLines : document.AuthoredLines.Append(draft);
        return lines.Where(line => line.SceneName == transform.SceneName)
            .Select(line => new GroundTruthOverlayLine(
                transform.WorldToAtlas(line.Start, liveBounds),
                transform.WorldToAtlas(line.End, liveBounds),
                OverlayKind(line, selectedId)))
            .Where(line => Finite(line.Start) && Finite(line.End)).ToArray();
    }

    public static GroundTruthOverlayLineKind OverlayKind(GroundTruthWorldLine line, Guid? selectedId) =>
        line.Id == selectedId ? GroundTruthOverlayLineKind.SelectedGroundTruth : line.Kind switch
        {
            GroundTruthLineKind.Negative => GroundTruthOverlayLineKind.NegativeGroundTruth,
            GroundTruthLineKind.Spike => GroundTruthOverlayLineKind.SpikeGroundTruth,
            GroundTruthLineKind.Door or GroundTruthLineKind.DoorVertical => GroundTruthOverlayLineKind.DoorGroundTruth,
            _ => GroundTruthOverlayLineKind.PositiveGroundTruth
        };

    private static bool Finite(AtlasPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}

public enum GroundTruthPointerPhase
{
    Began,
    Changed,
    Ended,
}

public enum GroundTruthEditingMode
{
    Add,
    Modify,
    Delete,
    Negative,
}

/// <summary>Platform-neutral world-line editing and persistence state.</summary>
public sealed class GroundTruthLabelController
{
    private sealed record Snapshot(GroundTruthLineDocument Document, Guid? SelectedId);
    private readonly GroundTruthLineStore store;
    private readonly List<Snapshot> history = [];
    private GroundTruthEditingMode mode = GroundTruthEditingMode.Add;
    private GroundTruthTerrainTool tool;
    private GroundTruthLineDocument? preview;
    private GroundTruthWorldLine? newLine;
    private GroundTruthWorldLine? original;
    private TerrainLineEnd? modificationEnd;
    private TerrainHandle? startTarget;
    private AtlasPoint? gestureStart;
    private AtlasPoint gestureTolerance;
    private Guid? selectedBeforeGesture;
    private string? gestureScene;

    public GroundTruthLabelController(GroundTruthLineStore? store = null)
    {
        this.store = store ?? new GroundTruthLineStore();
        try { Document = this.store.Load(); }
        catch (Exception error)
        {
            Document = new();
            Error = error.Message;
        }
    }

    public GroundTruthEditingMode Mode
    {
        get => mode;
        set
        {
            if (mode == value) return;
            CancelGesture();
            mode = value;
        }
    }

    public GroundTruthTerrainTool Tool
    {
        get => tool;
        set
        {
            if (tool == value) return;
            CancelGesture();
            ClearSelection();
            tool = value;
        }
    }

    public GroundTruthLineDocument Document { get; private set; }
    public Guid? SelectedId { get; private set; }
    public GroundTruthWorldLine? Draft => newLine is { } line ? preview?.FindLine(line.Id) : null;
    public string? Error { get; private set; }
    public bool CanUndo => history.Count != 0;
    public bool HasActiveGesture => gestureStart is not null;
    public int VisiblePositiveCount => Document.AuthoredLines.Count(line => line.Kind == GroundTruthLineKind.Positive);
    public int VisibleNegativeCount => Document.AuthoredLines.Count(line => line.Kind == GroundTruthLineKind.Negative);
    public IEnumerable<GroundTruthWorldLine> VisibleLines => (preview ?? Document).AuthoredLines;

    public int LineCount(string sceneName) =>
        Document.LinesFor(Tool).Count(line => !line.SeededFromDetector && line.SceneName == sceneName);

    public IReadOnlyList<GroundTruthOverlayLine> OverlayLines(GroundTruthCameraTransform? transform,
        GroundTruthRect liveBounds) => transform is null ? []
        : GroundTruthLineProjection.OverlayLines(preview ?? Document, transform, liveBounds, SelectedId, draft: null);

    /// <summary>Atomically imports legacy floor lines with stable identities.</summary>
    public bool ImportLegacyLines(IReadOnlyList<GroundTruthWorldLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        try
        {
            var candidate = Document.Clone();
            foreach (var source in lines)
            {
                var line = source.Standardized;
                if (candidate.FindLine(line.Id) is { } prior)
                {
                    if (prior != line)
                        throw new GroundTruthLineStoreException($"Legacy ground-label ID collision: {line.Id}");
                    continue;
                }
                candidate.Lines.Add(line);
            }
            store.Save(candidate);
            Document = candidate;
            Error = null;
            return true;
        }
        catch (Exception error)
        {
            Error = error.Message;
            return false;
        }
    }

    public void Handle(GroundTruthPointerPhase phase, AtlasPoint atlasPoint, double atlasTolerance,
        GroundTruthCameraTransform? transform, GroundTruthRect liveBounds)
    {
        if (transform is null)
        {
            Error = "Camera transform unavailable";
            return;
        }
        HandleWorld(phase, transform.AtlasToWorld(atlasPoint, liveBounds), atlasTolerance,
            transform.SceneName, new(transform.PixelsPerWorldUnitX, transform.PixelsPerWorldUnitY));
    }

    public void HandleWorld(GroundTruthPointerPhase phase, AtlasPoint world, double atlasTolerance,
        string sceneName, AtlasPoint pixelsPerWorldUnit)
    {
        if (!double.IsFinite(world.X) || !double.IsFinite(world.Y) || string.IsNullOrWhiteSpace(sceneName)
            || !double.IsFinite(pixelsPerWorldUnit.X) || !double.IsFinite(pixelsPerWorldUnit.Y)
            || pixelsPerWorldUnit.X <= 0 || pixelsPerWorldUnit.Y <= 0) return;
        if (phase == GroundTruthPointerPhase.Began)
        {
            var radius = double.IsFinite(atlasTolerance) ? Math.Max(2, atlasTolerance) : 2;
            Begin(world, new(radius / pixelsPerWorldUnit.X, radius / pixelsPerWorldUnit.Y), sceneName);
            return;
        }
        if (sceneName != gestureScene) return;
        Change(world);
        if (phase == GroundTruthPointerPhase.Ended) Finish(world, pixelsPerWorldUnit);
    }

    public void Undo()
    {
        if (HasActiveGesture) { CancelGesture(); return; }
        if (history.Count == 0) return;
        var snapshot = history[^1];
        if (!TrySave(snapshot.Document)) return;
        Document = snapshot.Document;
        SelectedId = snapshot.SelectedId;
        history.RemoveAt(history.Count - 1);
    }

    public void ClearSelection() => SelectedId = null;

    public void CancelGesture()
    {
        if (HasActiveGesture) SelectedId = selectedBeforeGesture;
        ResetGesture();
    }

    private void ResetGesture()
    {
        preview = null;
        newLine = null;
        original = null;
        modificationEnd = null;
        gestureStart = null;
        startTarget = null;
        gestureScene = null;
    }

    private void Begin(AtlasPoint world, AtlasPoint tolerance, string sceneName)
    {
        CancelGesture();
        selectedBeforeGesture = SelectedId;
        gestureStart = world;
        gestureScene = sceneName;
        gestureTolerance = tolerance;
        var hit = Mode == GroundTruthEditingMode.Add ? null : NearestLine(world, sceneName);
        switch (Mode)
        {
            case GroundTruthEditingMode.Add:
            case GroundTruthEditingMode.Negative:
                if (Mode == GroundTruthEditingMode.Negative)
                {
                    if (Tool != GroundTruthTerrainTool.Ground) return;
                    if (hit is not null)
                    {
                        preview = Document.Clone();
                        preview.ReplaceLine(hit with { Kind = GroundTruthLineKind.Negative, StartCornerId = null, EndCornerId = null });
                        preview.PruneCorners(hit);
                        preview.UpdateLatchedDoors();
                        SelectedId = hit.Id;
                        return;
                    }
                }
                else if (Tool is GroundTruthTerrainTool.Ground or GroundTruthTerrainTool.Wall)
                {
                    startTarget = Document.NearestCorner(world, Tool == GroundTruthTerrainTool.Wall, sceneName, tolerance);
                }
                var origin = startTarget is { } target && Document.FindLine(target.LineId) is { } corner
                    ? corner.PointAt(target.End) : world;
                var kind = Mode == GroundTruthEditingMode.Negative ? GroundTruthLineKind.Negative : Tool switch
                {
                    GroundTruthTerrainTool.Wall => GroundTruthLineKind.Wall,
                    GroundTruthTerrainTool.Spike => GroundTruthLineKind.Spike,
                    GroundTruthTerrainTool.Door => GroundTruthLineKind.Door,
                    _ => GroundTruthLineKind.Positive
                };
                newLine = new GroundTruthWorldLine(sceneName, origin.X, origin.X, origin.Y, kind, seededFromDetector: false);
                SelectedId = null;
                Change(origin);
                break;
            case GroundTruthEditingMode.Delete:
                SelectedId = null;
                if (hit is null) return;
                preview = Document.Clone();
                preview.RemoveLine(hit);
                preview.UpdateLatchedDoors();
                break;
            case GroundTruthEditingMode.Modify:
                SelectedId = hit?.Id;
                original = hit;
                if (hit is null) return;
                var along = GroundTruthLineDocument.Components(world, hit.IsVertical).X;
                var start = Math.Abs(along - GroundTruthLineDocument.Components(hit.Start, hit.IsVertical).X);
                var end = Math.Abs(along - GroundTruthLineDocument.Components(hit.End, hit.IsVertical).X);
                if (Math.Min(start, end) <= GroundTruthLineDocument.Components(tolerance, hit.IsVertical).X)
                    modificationEnd = start <= end ? TerrainLineEnd.Start : TerrainLineEnd.End;
                break;
        }
    }

    private void Change(AtlasPoint world)
    {
        if (gestureStart is not { } start || newLine is null && original is null) return;
        var next = Document.Clone();
        if (newLine is { } line)
        {
            if (Tool == GroundTruthTerrainTool.Door)
                line = line with { Kind = Math.Abs(world.Y - start.Y) > Math.Abs(world.X - start.X)
                    ? GroundTruthLineKind.DoorVertical : GroundTruthLineKind.Door };
            var origin = GroundTruthLineDocument.Components(line.Start, line.IsVertical);
            var along = GroundTruthLineDocument.Components(world, line.IsVertical).X;
            var first = along >= origin.X ? TerrainLineEnd.Start : TerrainLineEnd.End;
            var last = GroundTruthLineDocument.Opposite(first);
            line = line.WithExtent(origin.X, along, origin.Y);
            next.AddLine(line);
            if (startTarget is { } initial) next.Join(new(line.Id, first), initial);
            if (Mode == GroundTruthEditingMode.Add && IsCornerMember(line)
                && Document.NearestCorner(line.PointAt(last), line.IsVertical, line.SceneName, gestureTolerance, startTarget) is { } end)
                next.Join(new(line.Id, last), end);
        }
        else if (original is { } source)
        {
            var a = GroundTruthLineDocument.Components(source.Start, source.IsVertical);
            var b = GroundTruthLineDocument.Components(source.End, source.IsVertical);
            var delta = GroundTruthLineDocument.Components(new(world.X - start.X, world.Y - start.Y), source.IsVertical);
            if (modificationEnd is { } end)
            {
                var handle = new TerrainHandle(source.Id, end);
                next.Move(handle, source.IsVertical, (end == TerrainLineEnd.Start ? a.X : b.X) + delta.X);
                if (source.CornerAt(end) is not null || Tool == GroundTruthTerrainTool.Wall && !source.IsVertical)
                    next.Move(handle, !source.IsVertical, a.Y + delta.Y);
                if (IsCornerMember(source) && source.CornerAt(end) is null
                    && next.FindLine(source.Id) is { } edited
                    && next.NearestCorner(edited.PointAt(end), source.IsVertical, source.SceneName, gestureTolerance) is { } target)
                    next.Join(handle, target, keepTarget: !source.IsVertical);
            }
            else if (source.StartCornerId is not null || source.EndCornerId is not null)
            {
                next.Move(new(source.Id, TerrainLineEnd.Start), !source.IsVertical, a.Y + delta.Y);
            }
            else next.ReplaceLine(source.WithExtent(a.X + delta.X, b.X + delta.X, a.Y + delta.Y));
        }

        if (Tool == GroundTruthTerrainTool.Door && (newLine?.Id ?? original?.Id) is { } id
            && next.FindLine(id) is { } door)
        {
            var latch = modificationEnd is { } end
                ? original?.GroundLatch is { } previous && previous.End != end ? previous
                    : next.NearestGroundLatch(door, [end], gestureTolerance)
                : next.NearestGroundLatch(door, GroundTruthLineDocument.Ends, gestureTolerance);
            next.ReplaceLine(door with { GroundLatch = latch });
        }
        next.UpdateLatchedDoors();
        preview = next;
    }

    private static bool IsCornerMember(GroundTruthWorldLine line) =>
        line.Kind is GroundTruthLineKind.Positive or GroundTruthLineKind.Wall;

    private void Finish(AtlasPoint world, AtlasPoint scale)
    {
        try
        {
            if (preview is not { } next || next.Equals(Document)) return;
            if (newLine is { } line)
            {
                if (next.FindLine(line.Id) is not { } added || gestureStart is not { } start) return;
                var vertical = added.IsVertical;
                var minimum = 4 / GroundTruthLineDocument.Components(scale, vertical).X;
                if (Math.Abs(GroundTruthLineDocument.Components(world, vertical).X
                             - GroundTruthLineDocument.Components(start, vertical).X) < minimum
                    || GroundTruthLineDocument.Components(added.End, vertical).X
                        - GroundTruthLineDocument.Components(added.Start, vertical).X < minimum) return;
            }
            if (!TrySave(next)) { SelectedId = selectedBeforeGesture; return; }
            history.Add(new(Document, selectedBeforeGesture));
            if (history.Count > 100) history.RemoveAt(0);
            Document = next;
            SelectedId = newLine?.Id ?? original?.Id ?? SelectedId;
        }
        finally { ResetGesture(); }
    }

    private GroundTruthWorldLine? NearestLine(AtlasPoint world, string sceneName)
    {
        IEnumerable<GroundTruthWorldLine> candidates = Document.LinesFor(Tool);
        if (Tool == GroundTruthTerrainTool.Wall && Mode == GroundTruthEditingMode.Modify)
            candidates = candidates.Concat(Document.Lines.Where(line => line.Kind == GroundTruthLineKind.Positive));
        GroundTruthWorldLine? best = null;
        var rank = double.PositiveInfinity;
        foreach (var line in candidates)
        {
            if (line.SeededFromDetector || line.SceneName != sceneName) continue;
            var p = GroundTruthLineDocument.Components(world, line.IsVertical);
            var a = GroundTruthLineDocument.Components(line.Start, line.IsVertical);
            var b = GroundTruthLineDocument.Components(line.End, line.IsVertical);
            var tolerance = GroundTruthLineDocument.Components(gestureTolerance, line.IsVertical);
            var across = Math.Abs(p.Y - a.Y) / tolerance.Y;
            if (across > 1 || p.X < a.X - tolerance.X || p.X > b.X + tolerance.X) continue;
            var endpoint = Math.Min(Math.Abs(p.X - a.X), Math.Abs(p.X - b.X)) / tolerance.X;
            var distance = Math.Sqrt(endpoint * endpoint + across * across);
            var groundEndpointInWallMode = Tool == GroundTruthTerrainTool.Wall && !line.IsVertical;
            if (groundEndpointInWallMode && distance > 1) continue;
            var score = Mode == GroundTruthEditingMode.Modify && endpoint <= 1 ? distance : 3 + across;
            if (score >= rank) continue;
            best = line;
            rank = score;
        }
        return best;
    }

    private bool TrySave(GroundTruthLineDocument candidate)
    {
        try
        {
            store.Save(candidate);
            Error = null;
            return true;
        }
        catch (Exception error)
        {
            Error = error.Message;
            return false;
        }
    }
}
