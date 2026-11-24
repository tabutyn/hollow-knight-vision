using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal sealed record MenuCalibrationCapture(bool SavesFrame, string? Context = null,
    string? Row = null, string Language = "en")
{
    internal const string Flag = "--menu-calibration";
    private const string CaptureCommand = "menu-calibration-capture";

    internal static MenuCalibrationCapture? Parse(string serialized) => serialized.Split(':') switch
    {
        ["menu-calibration-status"] => new(false),
        [CaptureCommand] => new(true),
        [CaptureCommand, var context, var row] when HasIdentifiers(context, row) => new(true, context, row),
        [CaptureCommand, var language, var context, var row]
            when HasIdentifiers(context, row) && MenuStencilLiveCaptureStore.SupportedLanguages.Contains(language)
            => new(true, context, row, language),
        _ => null
    };

    private static bool HasIdentifiers(string context, string row) =>
        !string.IsNullOrWhiteSpace(context) && !string.IsNullOrWhiteSpace(row);

    internal Reply CaptureLive()
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Live menu capture requires Windows");
        try
        {
            if (HollowKnightWindowLocator.FindBest() is not { IsMinimized: false } window)
                return new(false, "No visible Hollow Knight window available");
            var frame = CpuBgraNormalizer.Normalize(new GdiWindowCapture().Capture(window, 1));
            var supportRoot = AutoNavigationRunner.LiveStartupStencilEvidenceProvider.DefaultSupportRoot;
            var provider = AutoNavigationRunner.LiveStartupStencilEvidenceProvider.CreateDefault(supportRoot,
                allowFallback: false, allowCalibrationWrites: false);
            var examplesRoot = Path.Combine(supportRoot, "labeling-v1", "examples");
            var store = new MenuStencilLiveCaptureStore(MenuStencilLiveCaptureStore.RootForExamples(examplesRoot));
            return Evaluate(frame, provider.Catalog, Row is null ? provider.ObserveMenu(frame) : null, store);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new(false, $"Capture failed: {error.Message}");
        }
    }

    internal Reply Evaluate(BgraFrame frame, MenuStencilCatalog catalog, MenuStencilResult? observation,
        MenuStencilLiveCaptureStore store)
    {
        var recognized = observation is { IsMatch: true } ? observation : null;
        var context = Context ?? recognized?.ContextIdentifier;
        var scene = catalog.Scenes.FirstOrDefault(item => item.ContextIdentifier == context);
        if (scene is null)
            return new(false, "Waiting for a recognized menu and selected row", context,
                SelectedName: recognized?.SelectedOption);

        var identifier = Row ?? recognized?.SelectedOptionIdentifier;
        var option = identifier is not null
            ? scene.Options.FirstOrDefault(candidate => candidate.ClassIdentifier == identifier)
            : scene.Options.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, recognized?.SelectedOption, StringComparison.OrdinalIgnoreCase));
        if (option is null)
            return new(false, "Selected row is not part of the menu catalog", context,
                SelectedName: recognized?.SelectedOption);
        if (!SavesFrame) return new(true, "recognized", context, option.ClassIdentifier, option.Name);
        // The explicit form accepts a human-identified menu and row. This
        // supplies evidence for a language or selector the catalog cannot recognize yet.
        var language = Row is null ? recognized?.LanguageIdentifier ?? Language : Language;
        try
        {
            var saved = store.Save(frame, scene.ContextIdentifier, option.ClassIdentifier, option.Name, language,
                frame.ObservedAt);
            return new(true, "captured", context, option.ClassIdentifier, option.Name, saved.ImagePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"Capture failed: {error.Message}", context, option.ClassIdentifier, option.Name);
        }
    }

    internal sealed record Reply(bool Ok, string Message, string? ContextIdentifier = null,
        string? SelectedIdentifier = null, string? SelectedName = null, string? CapturePath = null)
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        internal string Serialize() => JsonSerializer.Serialize(this, Json);
    }
}
