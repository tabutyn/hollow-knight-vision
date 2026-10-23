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
}

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
    public GroundTruthWorldLine(
        string sceneName,
        double minimumWorldX,
        double maximumWorldX,
        double worldY,
        GroundTruthLineKind kind,
        bool seededFromDetector)
        : this(
            Guid.NewGuid(),
            sceneName,
            minimumWorldX,
            maximumWorldX,
            worldY,
            kind,
            seededFromDetector)
    {
    }

    [JsonIgnore]
    public GroundTruthWorldLine Standardized => this with
    {
        MinimumWorldX = Math.Min(MinimumWorldX, MaximumWorldX),
        MaximumWorldX = Math.Max(MinimumWorldX, MaximumWorldX)
    };
}

public sealed class GroundTruthLineDocument : IEquatable<GroundTruthLineDocument>
{
    public const int CurrentSchemaVersion = 2;

    [JsonRequired]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonRequired]
    public HashSet<string> SeededScenes { get; set; } = [];

    [JsonRequired]
    public List<GroundTruthWorldLine> Lines { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion
            || Lines is null
            || Lines.Count > 20_000
            || SeededScenes is null
            || SeededScenes.Any(scene => string.IsNullOrEmpty(scene) || scene.Length > 256)
            || Lines.Any(line => line is null)
            || Lines.Select(line => line.Id).Distinct().Count() != Lines.Count)
        {
            throw new GroundTruthLineStoreException("Invalid ground-label document");
        }

        foreach (var line in Lines)
        {
            if (line is null
                || line.Id == Guid.Empty
                || string.IsNullOrEmpty(line.SceneName)
                || line.SceneName.Length > 256
                || !Enum.IsDefined(line.Kind)
                || !double.IsFinite(line.MinimumWorldX)
                || !double.IsFinite(line.MaximumWorldX)
                || !double.IsFinite(line.WorldY)
                || Math.Abs(line.MinimumWorldX) > 1_000_000
                || Math.Abs(line.MaximumWorldX) > 1_000_000
                || Math.Abs(line.WorldY) > 1_000_000
                || line.MaximumWorldX < line.MinimumWorldX
                || line.MaximumWorldX - line.MinimumWorldX > 100_000)
            {
                throw new GroundTruthLineStoreException(
                    "Invalid world-space ground line");
            }
        }
    }

    public GroundTruthLineDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        SeededScenes = new HashSet<string>(SeededScenes, StringComparer.Ordinal),
        Lines = [.. Lines]
    };

    public bool Equals(GroundTruthLineDocument? other) => other is not null
        && SchemaVersion == other.SchemaVersion
        && SeededScenes.SetEquals(other.SeededScenes)
        && Lines.SequenceEqual(other.Lines);

    public override bool Equals(object? obj) => Equals(obj as GroundTruthLineDocument);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        foreach (var scene in SeededScenes.Order(StringComparer.Ordinal)) hash.Add(scene);
        foreach (var line in Lines) hash.Add(line);
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
        IEnumerable<GroundTruthWorldLine> lines = document.Lines;
        if (draft is not null) lines = lines.Append(draft);
        var result = new List<GroundTruthOverlayLine>();
        foreach (var line in lines)
        {
            if (line.SceneName != transform.SceneName) continue;
            var start = transform.WorldToAtlas(
                new AtlasPoint(line.MinimumWorldX, line.WorldY),
                liveBounds);
            var end = transform.WorldToAtlas(
                new AtlasPoint(line.MaximumWorldX, line.WorldY),
                liveBounds);
            if (!Finite(start) || !Finite(end)) continue;
            var kind = line.Id == selectedId
                ? GroundTruthOverlayLineKind.SelectedGroundTruth
                : line.Kind == GroundTruthLineKind.Positive
                    ? GroundTruthOverlayLineKind.PositiveGroundTruth
                    : GroundTruthOverlayLineKind.NegativeGroundTruth;
            result.Add(new(start, end, kind));
        }
        return result;
    }

    public static IReadOnlyList<GroundTruthWorldLine> SeedLines(
        IReadOnlyList<GroundHypothesisAtlasLine> atlasLines,
        IReadOnlyList<GroundLinePresenceReview> lineReviews,
        GroundTruthCameraTransform transform,
        GroundTruthRect liveBounds)
    {
        ArgumentNullException.ThrowIfNull(atlasLines);
        ArgumentNullException.ThrowIfNull(lineReviews);
        ArgumentNullException.ThrowIfNull(transform);
        var result = new List<GroundTruthWorldLine>();
        foreach (var line in atlasLines)
        {
            var first = transform.AtlasToWorld(line.AtlasStart, liveBounds);
            var last = transform.AtlasToWorld(line.AtlasEnd, liveBounds);
            AppendSeed(new(
                transform.SceneName,
                Math.Min(first.X, last.X),
                Math.Max(first.X, last.X),
                (first.Y + last.Y) * .5,
                GroundTruthLineKind.Positive,
                seededFromDetector: true), transform.PixelsPerWorldUnitY, result);
        }

        foreach (var review in GroundLineDetector.FeatureHypothesisPresenceReviews(
                     lineReviews))
        {
            var y = review.Line.Row + .5;
            var first = transform.ScreenToWorld(
                new AtlasPoint(review.Line.XSpan.Start, y));
            var last = transform.ScreenToWorld(
                new AtlasPoint(review.Line.XSpan.EndInclusive + 1, y));
            AppendSeed(new(
                transform.SceneName,
                Math.Min(first.X, last.X),
                Math.Max(first.X, last.X),
                (first.Y + last.Y) * .5,
                GroundTruthLineKind.Positive,
                seededFromDetector: true), transform.PixelsPerWorldUnitY, result);
        }
        return result;
    }

    private static void AppendSeed(
        GroundTruthWorldLine proposed,
        double pixelsPerWorldUnit,
        List<GroundTruthWorldLine> result)
    {
        var rowTolerance = 3 / Math.Max(1, pixelsPerWorldUnit);
        var duplicate = result.Any(existing =>
        {
            if (Math.Abs(existing.WorldY - proposed.WorldY) > rowTolerance)
                return false;
            var overlap = Math.Min(existing.MaximumWorldX, proposed.MaximumWorldX)
                - Math.Max(existing.MinimumWorldX, proposed.MinimumWorldX);
            var shorter = Math.Min(
                existing.MaximumWorldX - existing.MinimumWorldX,
                proposed.MaximumWorldX - proposed.MinimumWorldX);
            return overlap >= Math.Max(0, shorter * .6);
        });
        if (!duplicate) result.Add(proposed.Standardized);
    }

    private static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
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
    private sealed record Snapshot(
        GroundTruthLineDocument Document,
        Guid? SelectedId);

    private enum ModificationPart
    {
        Start,
        End,
        Whole,
    }

    private readonly GroundTruthLineStore store;
    private readonly List<Snapshot> history = [];
    private GroundTruthWorldLine? original;
    private ModificationPart? modificationPart;
    private AtlasPoint? gestureStartWorld;
    private bool mutatedOnBegin;
    private int? gestureHistoryIndex;

    public GroundTruthLabelController(GroundTruthLineStore? store = null)
    {
        this.store = store ?? new GroundTruthLineStore();
        try
        {
            Document = this.store.Load();
        }
        catch (Exception error)
        {
            Document = new();
            Error = error.Message;
        }
    }

    public GroundTruthEditingMode Mode { get; set; } = GroundTruthEditingMode.Add;
    public GroundTruthLineDocument Document { get; private set; }
    public Guid? SelectedId { get; private set; }
    public GroundTruthWorldLine? Draft { get; private set; }
    public string? Error { get; private set; }
    public bool CanUndo => history.Count != 0;
    public bool HasActiveGesture =>
        gestureHistoryIndex is not null
        || gestureStartWorld is not null
        || Draft is not null;
    public int VisiblePositiveCount => Document.Lines.Count(
        line => line.Kind == GroundTruthLineKind.Positive);
    public int VisibleNegativeCount => Document.Lines.Count(
        line => line.Kind == GroundTruthLineKind.Negative);

    public void Enter(
        IReadOnlyList<GroundHypothesisAtlasLine> atlasLines,
        IReadOnlyList<GroundLinePresenceReview> lineReviews,
        GroundTruthCameraTransform? transform,
        GroundTruthRect liveBounds)
    {
        if (transform is null)
        {
            Error = "Camera transform unavailable";
            return;
        }
        if (Document.SeededScenes.Contains(transform.SceneName))
        {
            Error = null;
            return;
        }
        var seeds = GroundTruthLineProjection.SeedLines(
            atlasLines,
            lineReviews,
            transform,
            liveBounds);
        if (seeds.Count == 0)
        {
            Error = "Waiting for solved ground lines";
            return;
        }
        RecordSnapshot();
        Document.Lines.AddRange(seeds);
        Document.SeededScenes.Add(transform.SceneName);
        Persist();
    }

    public IReadOnlyList<GroundTruthOverlayLine> OverlayLines(
        GroundTruthCameraTransform? transform,
        GroundTruthRect liveBounds) => transform is null
            ? []
            : GroundTruthLineProjection.OverlayLines(
                Document,
                transform,
                liveBounds,
                SelectedId,
                Draft);

    /// <summary>
    /// Atomically imports stable-ID legacy lines. Repeating the import after a
    /// marker-write failure is safe; an ID collision with different content is
    /// rejected instead of silently replacing either copy.
    /// </summary>
    public bool ImportLegacyLines(IReadOnlyList<GroundTruthWorldLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        try
        {
            var candidate = Document.Clone();
            var existing = candidate.Lines.ToDictionary(line => line.Id);
            foreach (var source in lines)
            {
                var line = source.Standardized;
                if (existing.TryGetValue(line.Id, out var prior))
                {
                    if (prior != line)
                    {
                        throw new GroundTruthLineStoreException(
                            $"Legacy ground-label ID collision: {line.Id}");
                    }
                    continue;
                }
                candidate.Lines.Add(line);
                existing.Add(line.Id, line);
            }
            candidate.Validate();
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

    public void Handle(
        GroundTruthPointerPhase phase,
        AtlasPoint atlasPoint,
        double atlasTolerance,
        GroundTruthCameraTransform? transform,
        GroundTruthRect liveBounds)
    {
        if (transform is null)
        {
            Error = "Camera transform unavailable";
            return;
        }
        var world = transform.AtlasToWorld(atlasPoint, liveBounds);
        var toleranceX = Math.Max(
            2 / transform.PixelsPerWorldUnitX,
            atlasTolerance / transform.PixelsPerWorldUnitX);
        var toleranceY = Math.Max(
            2 / transform.PixelsPerWorldUnitY,
            atlasTolerance / transform.PixelsPerWorldUnitY);
        switch (phase)
        {
            case GroundTruthPointerPhase.Began:
                Begin(world, toleranceX, toleranceY, transform.SceneName);
                break;
            case GroundTruthPointerPhase.Changed:
                Change(world);
                break;
            case GroundTruthPointerPhase.Ended:
                Change(world);
                Finish(4 / transform.PixelsPerWorldUnitX);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase));
        }
    }

    public void Undo()
    {
        var cancelledActiveGesture = HasActiveGesture;
        CancelGesture();
        if (cancelledActiveGesture) return;
        if (history.Count == 0) return;
        var index = history.Count - 1;
        var snapshot = history[index];
        history.RemoveAt(index);
        Document = snapshot.Document.Clone();
        SelectedId = snapshot.SelectedId;
        Draft = null;
        Persist();
    }

    public void ClearSelection() => SelectedId = null;

    public void CancelGesture()
    {
        if (gestureHistoryIndex is { } index
            && index >= 0 && index < history.Count)
        {
            var snapshot = history[index];
            history.RemoveAt(index);
            Document = snapshot.Document.Clone();
            SelectedId = snapshot.SelectedId;
            if (mutatedOnBegin) Persist();
        }
        Draft = null;
        original = null;
        modificationPart = null;
        gestureStartWorld = null;
        mutatedOnBegin = false;
        gestureHistoryIndex = null;
    }

    private void Begin(
        AtlasPoint world,
        double toleranceX,
        double toleranceY,
        string sceneName)
    {
        CancelGesture();
        gestureStartWorld = world;
        mutatedOnBegin = false;
        var hit = NearestLine(world, sceneName, toleranceX, toleranceY);
        switch (Mode)
        {
            case GroundTruthEditingMode.Add:
                SelectedId = null;
                Draft = new(
                    sceneName,
                    world.X,
                    world.X,
                    world.Y,
                    GroundTruthLineKind.Positive,
                    seededFromDetector: false);
                break;
            case GroundTruthEditingMode.Negative:
                if (hit is not null)
                {
                    RecordGestureSnapshot();
                    hit = hit with { Kind = GroundTruthLineKind.Negative };
                    Replace(hit);
                    SelectedId = hit.Id;
                    mutatedOnBegin = true;
                    Persist();
                }
                else
                {
                    SelectedId = null;
                    Draft = new(
                        sceneName,
                        world.X,
                        world.X,
                        world.Y,
                        GroundTruthLineKind.Negative,
                        seededFromDetector: false);
                }
                break;
            case GroundTruthEditingMode.Delete:
                if (hit is null)
                {
                    SelectedId = null;
                    return;
                }
                RecordGestureSnapshot();
                Document.Lines.RemoveAll(line => line.Id == hit.Id);
                SelectedId = null;
                mutatedOnBegin = true;
                Persist();
                break;
            case GroundTruthEditingMode.Modify:
                if (hit is null)
                {
                    SelectedId = null;
                    return;
                }
                SelectedId = hit.Id;
                original = hit;
                RecordGestureSnapshot();
                if (Math.Abs(world.X - hit.MinimumWorldX) <= toleranceX)
                    modificationPart = ModificationPart.Start;
                else if (Math.Abs(world.X - hit.MaximumWorldX) <= toleranceX)
                    modificationPart = ModificationPart.End;
                else modificationPart = ModificationPart.Whole;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Mode));
        }
    }

    private void Change(AtlasPoint world)
    {
        if (Draft is { } draft)
        {
            if (gestureStartWorld is not { } start) return;
            Draft = draft with
            {
                MinimumWorldX = Math.Min(start.X, world.X),
                MaximumWorldX = Math.Max(start.X, world.X),
                WorldY = start.Y
            };
            return;
        }
        if (Mode != GroundTruthEditingMode.Modify
            || original is not { } source
            || gestureStartWorld is not { } gestureStart
            || modificationPart is not { } part)
        {
            return;
        }
        var changed = source;
        switch (part)
        {
            case ModificationPart.Start:
                changed = changed with
                {
                    MinimumWorldX = Math.Min(world.X, changed.MaximumWorldX)
                };
                break;
            case ModificationPart.End:
                changed = changed with
                {
                    MaximumWorldX = Math.Max(world.X, changed.MinimumWorldX)
                };
                break;
            case ModificationPart.Whole:
                var dx = world.X - gestureStart.X;
                changed = changed with
                {
                    MinimumWorldX = changed.MinimumWorldX + dx,
                    MaximumWorldX = changed.MaximumWorldX + dx,
                    WorldY = changed.WorldY + world.Y - gestureStart.Y
                };
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        Replace(changed.Standardized);
    }

    private void Finish(double minimumLength)
    {
        try
        {
            if (Draft is { } draft)
            {
                if (draft.MaximumWorldX - draft.MinimumWorldX < minimumLength)
                    return;
                RecordSnapshot();
                draft = draft.Standardized;
                Document.Lines.Add(draft);
                SelectedId = draft.Id;
                Persist();
            }
            else if (Mode == GroundTruthEditingMode.Modify && original is not null)
            {
                Persist();
            }
            else if (mutatedOnBegin)
            {
                Persist();
            }
        }
        finally
        {
            Draft = null;
            original = null;
            modificationPart = null;
            gestureStartWorld = null;
            mutatedOnBegin = false;
            gestureHistoryIndex = null;
        }
    }

    private GroundTruthWorldLine? NearestLine(
        AtlasPoint point,
        string sceneName,
        double toleranceX,
        double toleranceY) => Document.Lines
        .Where(line => line.SceneName == sceneName
            && Math.Abs(line.WorldY - point.Y) <= toleranceY
            && point.X >= line.MinimumWorldX - toleranceX
            && point.X <= line.MaximumWorldX + toleranceX)
        .OrderBy(line => Math.Abs(line.WorldY - point.Y))
        .FirstOrDefault();

    private void Replace(GroundTruthWorldLine line)
    {
        var index = Document.Lines.FindIndex(item => item.Id == line.Id);
        if (index >= 0) Document.Lines[index] = line;
    }

    private void RecordSnapshot()
    {
        history.Add(new(Document.Clone(), SelectedId));
        if (history.Count > 100) history.RemoveAt(0);
    }

    private void RecordGestureSnapshot()
    {
        RecordSnapshot();
        gestureHistoryIndex = history.Count - 1;
    }

    private void Persist()
    {
        try
        {
            store.Save(Document);
            Error = null;
        }
        catch (Exception error)
        {
            Error = error.Message;
        }
    }
}
