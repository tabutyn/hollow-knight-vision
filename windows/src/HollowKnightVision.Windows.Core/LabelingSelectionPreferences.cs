using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record LabelingSelection(string ContextIdentifier, string ClassIdentifier);

/// <summary>Persists editor selection only. It never selects Normal/Modded launch mode.</summary>
public sealed class LabelingSelectionPreferences(string path)
{
    public const string ContextKey = "labeling.last-context";
    public const string ClassIdentifierKey = "labeling.last-class-identifier";
    private readonly string path = Path.GetFullPath(path);
    private readonly object gate = new();

    public LabelingSelection Load(IReadOnlyList<string>? customClasses = null)
    {
        lock (gate)
        {
            var values = Read();
            return Resolve(values.GetValueOrDefault(ContextKey), values.GetValueOrDefault(ClassIdentifierKey), customClasses);
        }
    }

    public static LabelingSelection Resolve(string? persistedContext, string? persistedClass, IReadOnlyList<string>? customClasses = null)
    {
        var savedContext = LabelingContractCatalog.Contexts.Any(item => item.Identifier == persistedContext) ? persistedContext! : "game";
        var savedClass = string.IsNullOrWhiteSpace(persistedClass) ? null : LabelingClassIdentity.CanonicalIdentifier(persistedClass);
        string? Containing(string identifier) => LabelingContractCatalog.ScreenContexts.FirstOrDefault(context =>
            LabelingFrameOpeningPolicy.ContextClasses(context.Identifier, customClasses).Contains(identifier))?.Identifier;
        if (!LabelingContractCatalog.ScreenContexts.Any(context => context.Identifier == savedContext))
            savedContext = savedClass is null ? "game" : Containing(savedClass) ?? "game";
        var context = savedClass is not null && !LabelingFrameOpeningPolicy.ContextClasses(savedContext, customClasses).Contains(savedClass)
            ? Containing(savedClass) ?? savedContext : savedContext;
        var classes = LabelingFrameOpeningPolicy.ContextClasses(context, customClasses);
        return new(context, savedClass is not null && classes.Contains(savedClass) ? savedClass : classes.First());
    }

    public void SaveContext(string context) => Write(values => values[ContextKey] = context);
    public void SaveClassIdentifier(string identifier) => Write(values => values[ClassIdentifierKey] = CanonicalOrEmpty(identifier));
    public void Save(string context, string identifier) => Write(values =>
    { values[ContextKey] = context; values[ClassIdentifierKey] = CanonicalOrEmpty(identifier); });
    private static string CanonicalOrEmpty(string identifier) => string.IsNullOrWhiteSpace(identifier) ? "" : LabelingClassIdentity.CanonicalIdentifier(identifier);

    private Dictionary<string, string> Read()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    private void Write(Action<Dictionary<string, string>> update)
    {
        lock (gate)
        {
            var values = Read(); var previous = JsonSerializer.Serialize(values); update(values);
            var json = JsonSerializer.Serialize(values); if (json == previous) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
