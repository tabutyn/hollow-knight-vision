using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

/// <summary>Portable schema-1 selector geometry, shared with MenuStencilCalibration.swift.</summary>
public sealed record MenuStencilCalibration(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] int ReferenceWidth,
    [property: JsonRequired] int ReferenceHeight,
    [property: JsonRequired] DateTimeOffset GeneratedAt,
    [property: JsonRequired] IReadOnlyList<MenuStencilCalibration.Scene> Scenes,
    IReadOnlyList<Guid>? ReviewedCorrectionIdentifiers = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string Filename = "menu-stencil-positions.json";
    private const string MirrorArgumentPrefix = "--menu-stencil-project-calibration=";

    public readonly record struct Rect(
        [property: JsonRequired] double X, [property: JsonRequired] double Y,
        [property: JsonRequired] double Width, [property: JsonRequired] double Height)
    {
        public Rect(StencilRect bounds) : this(bounds.X, bounds.Y, bounds.Width, bounds.Height) { }
        [JsonIgnore] public StencilRect Bounds => new(X, Y, Width, Height);
    }

    public sealed record Match(
        [property: JsonRequired] string ClassIdentifier, [property: JsonRequired] string Role,
        [property: JsonRequired] Rect Rect, [property: JsonRequired] double Confidence,
        [property: JsonRequired] double CenterError, [property: JsonRequired] bool Accepted);
    public sealed record State(
        [property: JsonRequired] string SelectedIdentifier, Guid? SourceExampleIdentifier,
        [property: JsonRequired] bool Measured, [property: JsonRequired] IReadOnlyList<Match> Matches);
    public sealed record Anchor(
        [property: JsonRequired] string ClassIdentifier, [property: JsonRequired] Rect Rect,
        [property: JsonRequired] int EvidenceCount);
    public sealed record LocalizedPlacement(
        [property: JsonRequired] Rect LeftRect, [property: JsonRequired] Rect RightRect,
        [property: JsonRequired] bool Measured, [property: JsonRequired] int EvidenceCount);
    public sealed record Selector(
        [property: JsonRequired] string SelectedIdentifier,
        [property: JsonRequired] Rect LeftRect, [property: JsonRequired] Rect RightRect,
        [property: JsonRequired] bool Measured, [property: JsonRequired] int EvidenceCount,
        IReadOnlyDictionary<string, LocalizedPlacement>? LocalizedPlacements = null)
    {
        public LocalizedPlacement? Placement(string languageIdentifier) =>
            LocalizedPlacements?.GetValueOrDefault(languageIdentifier);
    }
    public sealed record Scene(
        [property: JsonRequired] string ContextIdentifier,
        [property: JsonRequired] IReadOnlyList<Anchor> Anchors,
        [property: JsonRequired] IReadOnlyList<Selector> Selectors,
        [property: JsonRequired] IReadOnlyList<State> States);

    public Scene? FindScene(string contextIdentifier) => Scenes.FirstOrDefault(scene => scene.ContextIdentifier == contextIdentifier);

    public static string WritablePath(string? examplesRoot = null) => Path.Combine(
        examplesRoot is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HollowKnightVision", "labeling-v1") : Path.GetDirectoryName(Path.GetFullPath(examplesRoot))!, Filename);

    // Explicit opt-in only. A workstation checkout must never become an implicit write target.
    public static string? ProjectMirrorPath(IEnumerable<string> arguments)
    {
        var argument = arguments.FirstOrDefault(value => value.StartsWith(MirrorArgumentPrefix, StringComparison.Ordinal));
        return argument is null || argument.Length == MirrorArgumentPrefix.Length
            ? null : Path.GetFullPath(argument[MirrorArgumentPrefix.Length..]);
    }

    public MenuStencilCalibration ReplacingSelector(string contextIdentifier, string selectedIdentifier,
        StencilRect leftRect, StencilRect rightRect, string languageIdentifier = "en", DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageIdentifier);
        if (!leftRect.IsFinite || leftRect.IsEmpty || !rightRect.IsFinite || rightRect.IsEmpty)
            throw new ArgumentException("Selector placements must be finite, non-empty rectangles.");
        var english = languageIdentifier == "en";
        var left = new Rect(leftRect); var right = new Rect(rightRect);
        var foundScene = false;
        Selector Replacement(Selector? existing)
        {
            var localized = existing?.LocalizedPlacements?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, LocalizedPlacement>(StringComparer.Ordinal);
            var languageEvidence = localized.GetValueOrDefault(languageIdentifier)?.EvidenceCount
                ?? (english ? existing?.EvidenceCount ?? 0 : 0);
            localized[languageIdentifier] = new(left, right, true, languageEvidence + 1);
            return new(selectedIdentifier, english || existing is null ? left : existing.LeftRect,
                english || existing is null ? right : existing.RightRect, true,
                (existing?.EvidenceCount ?? 0) + (english ? 1 : 0), localized);
        }
        var scenes = Scenes.Select(scene =>
        {
            if (scene.ContextIdentifier != contextIdentifier) return scene;
            foundScene = true;
            var selectors = scene.Selectors.ToList();
            var index = selectors.FindIndex(selector => selector.SelectedIdentifier == selectedIdentifier);
            if (index >= 0) selectors[index] = Replacement(selectors[index]);
            else
            {
                selectors.Add(Replacement(null));
                selectors.Sort((a, b) => StringComparer.Ordinal.Compare(a.SelectedIdentifier, b.SelectedIdentifier));
            }
            var states = scene.States.Select(state => !english || state.SelectedIdentifier != selectedIdentifier ? state
                : state with { Measured = true, Matches = state.Matches.Select(match => match.Role is "selector-left" or "selector-right"
                    ? match with { Rect = match.Role == "selector-left" ? left : right, CenterError = 0, Accepted = true } : match).ToArray() }).ToArray();
            return scene with { Selectors = selectors, States = states };
        }).ToList();
        if (!foundScene)
        {
            scenes.Add(new(contextIdentifier, [], [Replacement(null)], []));
            scenes.Sort((a, b) => StringComparer.Ordinal.Compare(a.ContextIdentifier, b.ContextIdentifier));
        }
        return this with { GeneratedAt = now ?? DateTimeOffset.UtcNow, Scenes = scenes };
    }

    public MenuStencilCalibration RecordingReviewedCorrections(IEnumerable<Guid> identifiers, DateTimeOffset? now = null) =>
        this with { GeneratedAt = now ?? DateTimeOffset.UtcNow,
            ReviewedCorrectionIdentifiers = identifiers.Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray() };

    public void Write(string path)
    {
        if (!IsValid) throw new InvalidDataException("Invalid menu stencil calibration.");
        var data = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        var createdTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdTemporary = true;
                output.Write(data);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (createdTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    public static MenuStencilCalibration? Load(string path)
    {
        try { using var input = File.OpenRead(path); return Read(input); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static MenuStencilCalibration? LoadBundled()
    {
        using var input = typeof(MenuStencilCalibration).Assembly.GetManifestResourceStream(Filename);
        return input is null ? null : Read(input);
    }

    public static MenuStencilCalibration? LoadDefault(string? writablePath = null) => Load(writablePath ?? WritablePath()) ?? LoadBundled();

    private static MenuStencilCalibration? Read(Stream input)
    {
        try
        {
            var value = JsonSerializer.Deserialize<MenuStencilCalibration>(input, JsonOptions);
            return value?.IsValid == true ? value : null;
        }
        catch (JsonException) { return null; }
    }

    // Swift Codable rejects missing/null non-optional members; mirror that instead of
    // silently accepting C# constructor defaults when loading corrupt user JSON.
    [JsonIgnore]
    private bool IsValid => SchemaVersion == CurrentSchemaVersion && ReferenceWidth > 0 && ReferenceHeight > 0
        && Scenes is not null && Scenes.All(scene => scene is not null && scene.ContextIdentifier is not null
            && scene.Anchors is not null && scene.Anchors.All(anchor => anchor is not null && anchor.ClassIdentifier is not null
                && anchor.Rect.Bounds.IsFinite)
            && scene.Selectors is not null && scene.Selectors.All(selector => selector is not null && selector.SelectedIdentifier is not null
                && selector.LeftRect.Bounds.IsFinite && selector.RightRect.Bounds.IsFinite
                && (selector.LocalizedPlacements is null || selector.LocalizedPlacements.Values.All(placement => placement is not null
                    && placement.LeftRect.Bounds.IsFinite && placement.RightRect.Bounds.IsFinite)))
            && scene.States is not null && scene.States.All(state => state is not null && state.SelectedIdentifier is not null
                && state.Matches is not null && state.Matches.All(match => match is not null && match.ClassIdentifier is not null
                    && match.Role is not null && match.Rect.Bounds.IsFinite && double.IsFinite(match.Confidence) && double.IsFinite(match.CenterError))));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new CalibrationDateConverter() }
    };

    private sealed class CalibrationDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            // Swift JSONDecoder.iso8601 does not accept fractional seconds on all deployed OS versions.
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}
