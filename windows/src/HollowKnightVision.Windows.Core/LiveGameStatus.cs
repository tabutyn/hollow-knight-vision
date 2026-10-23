namespace HollowKnightVision.Windows.Core;

public enum GameSelectionPresentation { SelectedSuffix, Hyphen }
public sealed record LiveGameStatus(string State, string ContextIdentifier, string? SelectedMenuOption = null,
    GameSelectionPresentation SelectionPresentation = GameSelectionPresentation.Hyphen)
{
    public bool IsGameplay => ContextIdentifier == "game";
    public string DisplayText => SelectedMenuOption is null ? State : SelectionPresentation == GameSelectionPresentation.SelectedSuffix
        ? $"{State} · {SelectedMenuOption} selected" : $"{State} - {SelectedMenuOption}";
}

public sealed class LiveGameStatusTracker
{
    public const double MissingCueGrace = .8;
    public LiveGameStatus? Current { get; private set; }
    private double? lastRecognitionTimestamp, lastObservedTimestamp;
    public void Reset(bool preservingGameplay = false)
    {
        if (!preservingGameplay || Current?.IsGameplay != true) Current = null;
        lastRecognitionTimestamp = lastObservedTimestamp = null;
    }
    public LiveGameStatus? Observe(LiveGameStatus? recognized, double timestamp)
    {
        if (!double.IsFinite(timestamp) || timestamp < (lastObservedTimestamp ?? double.NegativeInfinity)) return Current;
        lastObservedTimestamp = timestamp;
        // The cheap Credits cue also occurs in dark gameplay. It may only
        // establish Credits when reached from Extras (or already in Credits).
        if (recognized?.ContextIdentifier == "credits" && Current?.ContextIdentifier is not ("extras" or "credits")) recognized = null;
        if (recognized is not null) { Current = recognized; lastRecognitionTimestamp = timestamp; }
        else if (Current?.IsGameplay == true) return Current;
        else if (lastRecognitionTimestamp is { } previous && timestamp - previous > MissingCueGrace)
        { Current = null; lastRecognitionTimestamp = null; }
        return Current;
    }
}

public sealed class GameplayMenuEvidenceGate
{
    public const double HudHoldDuration = .35;
    public const int RequiredConsecutiveMenuFrames = 2;
    private double lastHudTimestamp = double.NegativeInfinity;
    private string? candidateContext, admittedContext;
    private int candidateCount;
    public void Reset() { lastHudTimestamp = double.NegativeInfinity; ClearCandidate(); }
    private void ClearCandidate() { candidateContext = admittedContext = null; candidateCount = 0; }
    public bool Admits(string? menuContext, bool gameplayLatched, int hudHealthMatchCount, double timestamp)
    {
        if (!double.IsFinite(timestamp)) return false;
        if (!gameplayLatched) { Reset(); return menuContext is not null; }
        if (hudHealthMatchCount >= 2) { lastHudTimestamp = timestamp; ClearCandidate(); return false; }
        if (timestamp - lastHudTimestamp <= HudHoldDuration || menuContext is null) { ClearCandidate(); return false; }
        if (admittedContext == menuContext) return true;
        if (candidateContext == menuContext) candidateCount++;
        else { candidateContext = menuContext; candidateCount = 1; }
        if (candidateCount < RequiredConsecutiveMenuFrames) return false;
        admittedContext = menuContext; return true;
    }
}

public static class LiveGameStatusEvidencePolicy
{
    public static bool ShouldRunObjectInference(LiveGameStatus? status) => status is null || status.IsGameplay || status.ContextIdentifier == "credits";
}

public static class LiveGameStatusResolver
{
    public static LiveGameStatus? Resolve(IReadOnlyList<ObjectDetection> detections, MenuStencilResult? menuStencil = null,
        HudStencilResult? hudStencil = null, bool creditsLikely = false, IReadOnlySet<string>? customClassIdentifiers = null)
    {
        if (menuStencil?.IsMatch == true)
            return new(LabelingContractCatalog.ContextName(menuStencil.ContextIdentifier), menuStencil.ContextIdentifier, menuStencil.SelectedOption);
        if (hudStencil?.EstablishesGameplay == true || LiveLabelSetScorer.GameplayMatch(detections, customClassIdentifiers) is not null)
            return new(LabelingContractCatalog.ContextName("game"), "game");
        return creditsLikely ? new(LabelingContractCatalog.ContextName("credits"), "credits") : null;
    }
}

/// <summary>Serial state handoff shared by capture and isolated integration tests.</summary>
public sealed class LiveGameStatusPipeline
{
    public sealed record Observation(LiveGameStatus? Status, LiveGameStatus? RecognizedStatus,
        MenuStencilResult? Menu, IReadOnlyList<ObjectDetection> Detections, bool RanObjectInference);
    private readonly LiveGameStatusTracker tracker = new();
    private readonly GameplayMenuEvidenceGate menuGate = new();
    public LiveGameStatus? Current => tracker.Current;
    public void Reset(bool preservingGameplay = false) { tracker.Reset(preservingGameplay); menuGate.Reset(); }
    public Observation Observe(MenuStencilResult? rawMenu, HudStencilResult? hud, double timestamp, bool creditsLikely = false,
        Func<IReadOnlyList<ObjectDetection>>? inferObjects = null, IReadOnlySet<string>? customClassIdentifiers = null)
    {
        var menu = menuGate.Admits(rawMenu?.IsMatch == true ? rawMenu.ContextIdentifier : null,
            tracker.Current?.IsGameplay == true, hud?.Health.Count ?? 0, timestamp) ? rawMenu : null;
        var provisional = LiveGameStatusResolver.Resolve([], menu, hud, creditsLikely, customClassIdentifiers);
        var runsInference = inferObjects is not null && LiveGameStatusEvidencePolicy.ShouldRunObjectInference(provisional);
        var detections = runsInference ? inferObjects!() : Array.Empty<ObjectDetection>();
        var recognized = LiveGameStatusResolver.Resolve(detections, menu, hud, creditsLikely, customClassIdentifiers);
        return new(tracker.Observe(recognized, timestamp), recognized, menu, detections, runsInference);
    }
}

public static class CreditsVisualSignature
{
    public static bool IsLikelyCredits(BgraFrame image)
    {
        // Mac renders the entire image into a 16x9 RGB grid; do not center-crop
        // non-16:9 sources before measuring the center, ring and corners.
        var pixels = new ImageStencilPixels(image, 16, 9);
        double Luma(int x, int y)
        { var rgb = pixels.Rgb(x, y)!.Value; return (.2126 * rgb.Red + .7152 * rgb.Green + .0722 * rgb.Blue) / 255; }
        var center = Enumerable.Range(3, 3).SelectMany(y => Enumerable.Range(6, 4).Select(x => Luma(x, y))).Average();
        var corners = (Luma(0, 0) + Luma(15, 0) + Luma(0, 8) + Luma(15, 8)) / 4;
        var ring = (Luma(3, 2) + Luma(12, 2) + Luma(3, 6) + Luma(12, 6)) / 4;
        return corners < .10 && center > .20 && center > ring * 1.25 && ring > corners * 1.4;
    }
}

public static class QuitGameTerminationPolicy
{
    public static bool RecognizesExitIntent(string? context, string? selectedOption) => context == "quit-game"
        && string.Equals(selectedOption, "Yes", StringComparison.OrdinalIgnoreCase);
    public static bool ShouldAwaitGameExit(string? context, string? selectedOption, string pointerEvent, bool accepted) =>
        accepted && RecognizesExitIntent(context, selectedOption) && pointerEvent == "leftUp";
    public static bool ShouldAwaitGameExit(string? context, string? selectedOption, RecordedGameButton button, bool isPressed, bool accepted) =>
        accepted && RecognizesExitIntent(context, selectedOption) && button == RecordedGameButton.Z && isPressed;
}
