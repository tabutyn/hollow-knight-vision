using System.IO;
using System.Text.Json;

namespace HollowKnightVision.Windows.App;

internal static class StencilGuidanceText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalog =
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "stencil-guidance.json")))
        ?? throw new InvalidDataException("Missing stencil guidance translations.");

    private static readonly Dictionary<string, string> WindowsGuidance = new()
    {
        ["profileClearSave"] = "Return to Select Profile, highlight an occupied save slot, and press L to label Clear Save.",
        ["modelLabelGameplay"] = "Stencils complete. Enter gameplay, press L, and label Hollow Knight and the visible gameplay objects.",
        ["modelOpen"] = "Stencils complete. Open Model to train your labeled gameplay screenshots.",
        ["modelReview"] = "Open Model, then Screenshots. Fix or exempt the {count} screenshots with labeling errors before training.",
        ["modelTrain"] = "Click Train to build your model from the labeled gameplay screenshots.",
        ["modelTraining"] = "Training your model. Wait for training to finish; progress appears above.",
        ["modelReady"] = "Model trained. Open Settings and enable Object Detection to test it during gameplay."
    };

    internal static string Text(string language, string key, params (string Key, string Value)[] values)
    {
        // The shared Mac catalog calls this controller backend MFI. Windows
        // shows XInput; keep the shared key and annotation ID for saved labels.
        if (key == "MFI") return "XInput";
        var result = Catalog.GetValueOrDefault(language)?.GetValueOrDefault(key)
            ?? Catalog["en"].GetValueOrDefault(key) ?? WindowsGuidance.GetValueOrDefault(key) ?? key;
        foreach (var (name, value) in values) result = result.Replace("{" + name + "}", value);
        return result;
    }
}
