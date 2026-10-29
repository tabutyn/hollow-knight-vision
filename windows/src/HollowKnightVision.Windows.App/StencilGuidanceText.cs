using System.IO;
using System.Text.Json;

namespace HollowKnightVision.Windows.App;

internal static class StencilGuidanceText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalog =
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "stencil-guidance.json")))
        ?? throw new InvalidDataException("Missing stencil guidance translations.");

    internal static string Text(string language, string key, params (string Key, string Value)[] values)
    {
        // The shared Mac catalog calls this controller backend MFI. Windows
        // shows XInput; keep the shared key and annotation ID for saved labels.
        if (key == "MFI") return "XInput";
        var result = Catalog.GetValueOrDefault(language)?.GetValueOrDefault(key)
            ?? Catalog["en"].GetValueOrDefault(key) ?? key;
        foreach (var (name, value) in values) result = result.Replace("{" + name + "}", value);
        return result;
    }
}
