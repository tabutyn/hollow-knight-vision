using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

[JsonConverter(typeof(JsonStringEnumConverter<LabelingExampleCompletion>))]
public enum LabelingExampleCompletion
{
    Draft,
    Ready
}

public sealed record LabelingExampleAnnotation(
    Guid Id,
    string ClassIdentifier,
    double X,
    double Y,
    double Width,
    double Height,
    bool? IsNegative = null)
{
    [JsonIgnore]
    public bool IsHardNegative => IsNegative == true;

    public LabelingExampleAnnotation Canonicalized() => this with
    {
        ClassIdentifier = LabelingClassIdentity.CanonicalIdentifier(ClassIdentifier)
    };

    public LabelingExampleAnnotation Standardized() => this with
    {
        X = Math.Min(X, X + Width),
        Y = Math.Min(Y, Y + Height),
        Width = Math.Abs(Width),
        Height = Math.Abs(Height)
    };

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ClassIdentifier);
        if (!double.IsFinite(X) || !double.IsFinite(Y)
            || !double.IsFinite(Width) || !double.IsFinite(Height)
            || X < 0 || Y < 0 || Width <= 0 || Height <= 0
            || X + Width > 1.0000001 || Y + Height > 1.0000001)
        {
            throw new ArgumentException(
                "Label coordinates must be finite normalized top-origin bounds inside the image.");
        }
    }
}

public sealed record LabelingExampleManifest(
    int SchemaVersion,
    Guid Id,
    Guid ImageIdentifier,
    string ImageFilename,
    int ImageWidth,
    int ImageHeight,
    Guid CaptureGroupIdentifier,
    string ContextIdentifier,
    IReadOnlyList<LabelingExampleAnnotation> Annotations,
    IReadOnlyList<string>? KnownClassIdentifiers,
    LabelingExampleCompletion Completion,
    DateTimeOffset CreatedAt,
    string? LanguageIdentifier = null)
{
    public const int CurrentSchemaVersion = 5;

    [JsonIgnore]
    public string ResolvedLanguageIdentifier => LanguageIdentifier ?? "en";

    [JsonIgnore]
    public IReadOnlyList<string> EffectiveKnownClassIdentifiers => SchemaVersion >= 2
        && KnownClassIdentifiers is not null
            ? KnownClassIdentifiers
                .Select(LabelingClassIdentity.CanonicalIdentifier)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : Annotations
                .Select(annotation => LabelingClassIdentity.CanonicalIdentifier(
                    annotation.ClassIdentifier))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

    public LabelingExampleManifest Canonicalized()
    {
        var annotations = Annotations.Select(annotation =>
        {
            var canonical = annotation.Canonicalized();
            return SchemaVersion < 4
                && ContextIdentifier == "options"
                && canonical.ClassIdentifier == "game-options.game-options"
                    ? canonical with { ClassIdentifier = "options.game" }
                    : canonical;
        }).ToArray();
        var known = KnownClassIdentifiers?.Select(LabelingClassIdentity.CanonicalIdentifier)
            .ToHashSet(StringComparer.Ordinal);
        if (SchemaVersion < 4 && known?.Contains("game-options.game-options") == true)
        {
            known.Add("options.game");
        }
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            ContextIdentifier = LabelingExampleContextInference.Infer(ContextIdentifier, annotations),
            Annotations = annotations,
            KnownClassIdentifiers = known?.Order(StringComparer.Ordinal).ToArray()
        };
    }
}

public sealed record SavedLabelingExample(string DirectoryPath, LabelingExampleManifest Manifest)
{
    public Guid Id => Manifest.Id;
    public string ImagePath => Path.Combine(DirectoryPath, Manifest.ImageFilename);
}

public static class LabelingClassIdentity
{
    private static readonly IReadOnlyDictionary<string, string> LegacyIdentifiers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["main-title.select-decoration"] = "shared.select-decoration",
            ["select-profile.select-decoration"] = "shared.select-decoration",
            ["select-profile.back"] = "shared.back",
            ["main-title.options"] = "shared.options",
            ["pause.options"] = "shared.options",
            ["options.options"] = "shared.options",
            ["main-title.achievements"] = "shared.achievements",
            ["main-title.extras"] = "shared.extras",
            ["options.audio"] = "shared.audio",
            ["options.video"] = "shared.video",
            ["options.controller"] = "shared.controller",
            ["options.keyboard"] = "shared.keyboard",
            ["options.mods"] = "shared.mods",
            ["quit-to-menu.yes"] = "shared.yes",
            ["quit-to-menu.no"] = "shared.no",
            ["pause.quit-to-menu"] = "quit-to-menu.quit-to-menu",
            ["main-title.quit-game"] = "quit-game.quit-game"
        };

    public static string CanonicalIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return LegacyIdentifiers.TryGetValue(identifier, out var canonical)
            ? canonical
            : identifier;
    }
}

/// <summary>Atomic, mac-schema-compatible captured example storage.</summary>
public sealed class LabelingExampleStore
{
    public const string ManifestFilename = "example.json";
    public const string ImageFilename = "image.png";
    private static readonly JsonSerializerOptions JsonOptions = CreateManifestJsonOptions();
    private readonly Func<Guid> createId;
    private readonly Func<DateTimeOffset> now;

    public LabelingExampleStore(
        string rootDirectory,
        Func<Guid>? createId = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        this.createId = createId ?? Guid.NewGuid;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string RootDirectory { get; }

    public SavedLabelingExample Save(
        BgraFrame image,
        string contextIdentifier,
        IEnumerable<LabelingExampleAnnotation> annotations,
        IEnumerable<string>? knownClassIdentifiers = null,
        LabelingExampleCompletion completion = LabelingExampleCompletion.Draft,
        Guid? captureGroupIdentifier = null,
        string languageIdentifier = "en")
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextIdentifier);
        ArgumentNullException.ThrowIfNull(annotations);
        var canonicalAnnotations = annotations.Select(CanonicalAndValidated).ToArray();
        var introduced = IntroducedClassIdentifiers(canonicalAnnotations, knownClassIdentifiers);
        var id = createId();
        var manifest = new LabelingExampleManifest(
            LabelingExampleManifest.CurrentSchemaVersion,
            id,
            createId(),
            ImageFilename,
            image.Width,
            image.Height,
            captureGroupIdentifier ?? createId(),
            contextIdentifier,
            canonicalAnnotations,
            introduced,
            completion,
            now(),
            languageIdentifier);

        Directory.CreateDirectory(RootDirectory);
        var finalDirectory = Path.Combine(RootDirectory, id.ToString("D").ToLowerInvariant());
        var stagingDirectory = Path.Combine(
            RootDirectory,
            $".staging-{id:D}-{Guid.NewGuid():N}".ToLowerInvariant());
        if (Directory.Exists(finalDirectory))
        {
            throw new IOException("Label example destination already exists.");
        }
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            PngWriter.Write(image, Path.Combine(stagingDirectory, ImageFilename));
            WriteManifest(manifest, Path.Combine(stagingDirectory, ManifestFilename));
            Directory.Move(stagingDirectory, finalDirectory);
        }
        catch
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
        return new SavedLabelingExample(finalDirectory, manifest);
    }

    public IReadOnlyList<SavedLabelingExample> Load()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var examples = new List<SavedLabelingExample>();
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
            try
            {
                var manifest = LoadManifest(directory);
                if (!manifest.Id.ToString("D").Equals(
                        Path.GetFileName(directory),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                examples.Add(new SavedLabelingExample(directory, manifest));
            }
            catch (IOException) { }
            catch (JsonException) { }
            catch (InvalidDataException) { }
            catch (UnauthorizedAccessException) { }
        }
        return examples.OrderByDescending(example => example.Manifest.CreatedAt).ToArray();
    }

    public IReadOnlyList<SavedLabelingExample> LoadContainingClass(string classIdentifier)
    {
        var canonical = LabelingClassIdentity.CanonicalIdentifier(classIdentifier);
        return Load().Where(example => example.Manifest.Annotations.Any(annotation =>
            LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier) == canonical)).ToArray();
    }

    // Completion is not an admission gate on Mac: a draft with an introduced
    // target (including an explicitly empty frame) is still training evidence.
    public IReadOnlyList<SavedLabelingExample> LoadTrainingExamples(string? classIdentifier = null)
    {
        var canonical = classIdentifier is null ? null : LabelingClassIdentity.CanonicalIdentifier(classIdentifier);
        return Load().Where(example => canonical is null
            ? HasTrainingEvidence(example)
            : example.Manifest.Annotations.Any(annotation =>
                LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier) == canonical)
                || example.Manifest.EffectiveKnownClassIdentifiers.Contains(canonical, StringComparer.Ordinal)).ToArray();
    }

    public IReadOnlyList<SavedLabelingExample> LoadTrainingExamplesInContext(string contextIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextIdentifier);
        return Load().Where(example => example.Manifest.ContextIdentifier == contextIdentifier
            && HasTrainingEvidence(example)).ToArray();
    }

    private static bool HasTrainingEvidence(SavedLabelingExample example) =>
        example.Manifest.Annotations.Count > 0 || example.Manifest.EffectiveKnownClassIdentifiers.Count > 0;

    public LabelingExampleManifest LoadManifest(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var manifestPath = Path.Combine(Path.GetFullPath(directoryPath), ManifestFilename);
        var manifest = JsonSerializer.Deserialize<LabelingExampleManifest>(
            File.ReadAllBytes(manifestPath),
            JsonOptions) ?? throw new InvalidDataException("Empty label example manifest.");
        if (manifest.SchemaVersion is < 1 or > LabelingExampleManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported label example schema {manifest.SchemaVersion}.");
        }
        if (manifest.ImageWidth <= 0 || manifest.ImageHeight <= 0)
        {
            throw new InvalidDataException("Label example image dimensions are invalid.");
        }
        foreach (var annotation in manifest.Annotations) annotation.Validate();
        var migrated = manifest.Canonicalized();
        var needsMigration = manifest.SchemaVersion
                != LabelingExampleManifest.CurrentSchemaVersion
            || migrated.ContextIdentifier != manifest.ContextIdentifier
            || !migrated.Annotations.SequenceEqual(manifest.Annotations)
            || (manifest.KnownClassIdentifiers is not null
                && !migrated.KnownClassIdentifiers!.SequenceEqual(manifest.KnownClassIdentifiers, StringComparer.Ordinal));
        if (needsMigration) WriteManifest(migrated, manifestPath);
        return migrated;
    }

    public SavedLabelingExample Update(
        SavedLabelingExample example,
        string contextIdentifier,
        IEnumerable<LabelingExampleAnnotation> annotations,
        IEnumerable<string>? knownClassIdentifiers = null,
        LabelingExampleCompletion completion = LabelingExampleCompletion.Draft)
    {
        EnsureOwned(example);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextIdentifier);
        ArgumentNullException.ThrowIfNull(annotations);
        var canonicalAnnotations = annotations.Select(CanonicalAndValidated).ToArray();
        var known = IntroducedClassIdentifiers(canonicalAnnotations, knownClassIdentifiers);
        var updated = example.Manifest with
        {
            SchemaVersion = LabelingExampleManifest.CurrentSchemaVersion,
            ContextIdentifier = LabelingExampleContextInference.Infer(example.Manifest.Annotations.Count == 0
                ? contextIdentifier
                : example.Manifest.ContextIdentifier, canonicalAnnotations),
            Annotations = canonicalAnnotations,
            KnownClassIdentifiers = known,
            Completion = completion
        };
        WriteManifest(updated, Path.Combine(example.DirectoryPath, ManifestFilename));
        return example with { Manifest = updated };
    }

    public void Delete(SavedLabelingExample example)
    {
        EnsureOwned(example);
        Directory.Delete(example.DirectoryPath, recursive: true);
    }

    private void EnsureOwned(SavedLabelingExample example)
    {
        ArgumentNullException.ThrowIfNull(example);
        var parent = Directory.GetParent(Path.GetFullPath(example.DirectoryPath))?.FullName;
        if (!string.Equals(parent, RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Label example is outside this store.");
        }
    }

    private static LabelingExampleAnnotation CanonicalAndValidated(
        LabelingExampleAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var canonical = annotation.Canonicalized().Standardized() with
        {
            IsNegative = annotation.IsHardNegative ? true : null
        };
        canonical.Validate();
        return canonical;
    }

    private string[] IntroducedClassIdentifiers(IReadOnlyList<LabelingExampleAnnotation> annotations,
        IEnumerable<string>? explicitKnownClasses) => Load().SelectMany(example => example.Manifest.Annotations)
        .Select(annotation => LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier))
        .Concat(annotations.Select(annotation => annotation.ClassIdentifier))
        // The CLI can explicitly introduce classes. The graphical editor must
        // not pass its previous manifest's remembered snapshot here.
        .Concat((explicitKnownClasses ?? []).Select(LabelingClassIdentity.CanonicalIdentifier))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static void WriteManifest(LabelingExampleManifest manifest, string path)
    {
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
    }

    internal static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static JsonSerializerOptions CreateManifestJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false), new ManifestDateConverter() }
    };

    private sealed class ManifestDateConverter : JsonConverter<DateTimeOffset>
    {
        // Accept previous Windows fractional timestamps as well as Mac JSON.
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

internal static class AtomicFile
{
    internal static void Write(string path, ReadOnlySpan<byte> data)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("File has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }
}
