using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record MenuStencilLiveCapture(
    [property: JsonRequired] int SchemaVersion, [property: JsonRequired] Guid Id,
    [property: JsonRequired] DateTimeOffset CapturedAt, [property: JsonRequired] string ContextIdentifier,
    [property: JsonRequired] string SelectedIdentifier, [property: JsonRequired] string SelectedName,
    string? LanguageIdentifier, [property: JsonRequired] string ImageFilename,
    [property: JsonRequired] int ImageWidth, [property: JsonRequired] int ImageHeight)
{
    [JsonIgnore] public string ResolvedLanguageIdentifier => LanguageIdentifier ?? "en";
}
public sealed record SavedMenuStencilCapture(MenuStencilLiveCapture Capture, string ImagePath)
{
    public MenuStencilLiveSource Source => new(Capture.Id, Capture.ContextIdentifier, Capture.SelectedIdentifier,
        Capture.ResolvedLanguageIdentifier, Capture.ImageWidth, Capture.ImageHeight, ImagePath);
}
public sealed class MenuStencilLiveCaptureStore
{
    public const string ManifestFilename = "capture.json", ImageFilename = "image.png";
    public static readonly IReadOnlySet<string> SupportedLanguages = new HashSet<string>(["en", "es", "fr", "it", "ja", "ko", "pt-BR", "ru", "zh-Hans", "zh-Hant", "de"], StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string RootDirectory { get; }
    public MenuStencilLiveCaptureStore(string rootDirectory) => RootDirectory = Path.GetFullPath(rootDirectory);
    public static string RootForExamples(string examplesRoot) => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(examplesRoot)))!, "menu-stencil-live-v1", "captures");
    public IReadOnlyList<SavedMenuStencilCapture> Load()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var result = new List<SavedMenuStencilCapture>();
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory).Where(path => !Path.GetFileName(path).StartsWith('.') && (File.GetAttributes(path) & FileAttributes.Hidden) == 0))
        {
            var capture = JsonSerializer.Deserialize<MenuStencilLiveCapture>(File.ReadAllText(Path.Combine(directory, ManifestFilename)), JsonOptions)
                ?? throw new InvalidDataException("Invalid live menu capture.");
            if (capture.SchemaVersion != 1) continue;
            if (string.IsNullOrWhiteSpace(capture.ContextIdentifier) || string.IsNullOrWhiteSpace(capture.SelectedIdentifier) || capture.SelectedName is null
                || capture.ImageWidth <= 0 || capture.ImageHeight <= 0 || string.IsNullOrWhiteSpace(capture.ImageFilename)
                || capture.ImageFilename is "." or ".." || capture.ImageFilename.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new InvalidDataException("Invalid live menu capture fields or image filename.");
            result.Add(new(capture, Path.Combine(directory, capture.ImageFilename)));
        }
        return result.OrderByDescending(item => item.Capture.CapturedAt).ToArray();
    }
    public SavedMenuStencilCapture Save(BgraFrame image, string contextIdentifier, string selectedIdentifier,
        string selectedName, string languageIdentifier = "en", DateTimeOffset? now = null)
    {
        if (!SupportedLanguages.Contains(languageIdentifier)) throw new ArgumentException($"Unsupported Hollow Knight language: {languageIdentifier}", nameof(languageIdentifier));
        ArgumentException.ThrowIfNullOrWhiteSpace(contextIdentifier); ArgumentException.ThrowIfNullOrWhiteSpace(selectedIdentifier);
        ArgumentNullException.ThrowIfNull(selectedName);
        var id = Guid.NewGuid(); var time = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        // Swift's ISO8601 decoder expects whole seconds, as with calibration files.
        time = DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds());
        var capture = new MenuStencilLiveCapture(1, id, time, contextIdentifier, selectedIdentifier, selectedName, languageIdentifier, ImageFilename, image.Width, image.Height);
        Directory.CreateDirectory(RootDirectory);
        var staging = Path.Combine(RootDirectory, ".staging-" + id.ToString("D")); var final = Path.Combine(RootDirectory, id.ToString("D"));
        Directory.CreateDirectory(staging);
        try
        {
            PngWriter.Write(image, Path.Combine(staging, ImageFilename));
            ModelStoreJson.Write(Path.Combine(staging, ManifestFilename), capture);
            Directory.Move(staging, final);
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
        return new(capture, Path.Combine(final, ImageFilename));
    }
}
