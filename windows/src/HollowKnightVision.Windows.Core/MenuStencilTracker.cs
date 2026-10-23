using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

public sealed record MenuStencilMatch(string ClassIdentifier, string Name, StencilRect Rect, double Confidence, ImageStencilReference? Reference);
public enum MenuSelectorSide { Left, Right }
public sealed record MenuStencilSelectorSearchRegion(string OptionIdentifier, string OptionName, MenuSelectorSide Side, StencilRect Rect, bool IsSolved);
public enum MenuStencilPhase { Searching, Tracking }
public sealed record MenuStencilResult(string ContextIdentifier, bool IsMatch, double Confidence, string? SelectedOption,
    IReadOnlyList<MenuStencilMatch> Anchors, IReadOnlyList<MenuStencilMatch> SelectorCandidates,
    IReadOnlyList<MenuStencilSelectorSearchRegion> SelectorSearchRegions, int SelectorLanguageEvidenceCount,
    int SelectorForegroundEvidenceCount, double SceneEvidenceRatio, MenuStencilPhase Phase, int ComparisonCount,
    double SourceTimestamp, string? LanguageIdentifier)
{
    public bool SelectedPairAccepted => SelectedOption is not null && SelectorCandidates.Count == 2;
}

public sealed class MenuStencilSearchCadence(double minimumInterval)
{
    public double MinimumInterval { get; } = Math.Max(0, minimumInterval);
    public double? LastSearchTimestamp { get; private set; }
    public bool ShouldSearch(double timestamp)
    {
        if (!double.IsFinite(timestamp)) return true;
        if (LastSearchTimestamp is { } previous && timestamp >= previous && timestamp - previous < MinimumInterval) return false;
        LastSearchTimestamp = timestamp; return true;
    }
    public void Reset() => LastSearchTimestamp = null;
}

/// <summary>Serial-queue port of Mac's two-stage, scene/language-aware menu tracker.</summary>
public sealed partial class MenuStencilTracker
{
    public const double ProbeThreshold = .42, AnchorThreshold = .40, SceneThreshold = .40,
        SelectorThreshold = .48, SelectorEvidenceThreshold = .38, FadedSelectorEvidenceThreshold = .22,
        SelectorWinningMargin = .06;
    public const int SelectorMinimumForegroundPixels = 24, FadedSelectorMinimumForegroundPixels = 36,
        SelectorSearchRadiusX = 4, SelectorSearchRadiusY = 4, SelectorRecoveryRadiusX = 40, SelectorRecoveryRadiusY = 10,
        ValidationInterval = 8, LossGraceFrames = 2, SceneChallengeCandidateLimit = 16;
    private const double SelectorMaximumPairRowDelta = 5, SceneChallengeWinningMargin = .12;
    private sealed record Score(double Confidence, int Foreground, int BrightForeground, StencilRect Rect,
        ImageStencilReference Reference, string? Language, StencilRect? NominalBounds);
    private sealed record Candidate(Scene Scene, Score Score);
    private sealed record Validated(Candidate Candidate, MenuStencilResult Result, Offset Offset);
    private readonly record struct Offset(double X, double Y);
    private sealed record Selection(string? Name, IReadOnlyList<MenuStencilMatch> Matches,
        IReadOnlyList<MenuStencilSelectorSearchRegion> Regions, int LanguageEvidence, int ForegroundEvidence);
    private MenuStencilCatalog? catalog;
    private readonly MenuSelectorCalibrationTracker calibration;
    private readonly MenuStencilSearchCadence cadence;
    private string? trackedContext, trackedLanguage;
    private Offset trackedOffset;
    private int missingProbeFrames, languageMissFrames, framesUntilValidation, comparisons;
    private MenuStencilResult? lastValidated;
    public string? SelectorCalibrationDiagnostic => calibration.Diagnostic;

    public MenuStencilTracker(MenuStencilCatalog? catalog = null, double minimumSearchInterval = 0,
        MenuSelectorCalibrationTracker? calibration = null)
    {
        this.catalog = catalog; this.calibration = calibration ?? new(); cadence = new(minimumSearchInterval);
        if (catalog is not null) this.calibration.SynchronizeCorrections(catalog.SelectorCorrections);
    }
    public void Reset(bool preservingLanguage = false)
    {
        trackedContext = null; if (!preservingLanguage) { trackedLanguage = null; languageMissFrames = 0; }
        trackedOffset = default; missingProbeFrames = 0; framesUntilValidation = 0; lastValidated = null;
        calibration.Reset(); cadence.Reset();
    }
    public void ReplaceCatalog(MenuStencilCatalog value)
    { catalog = value; Reset(preservingLanguage: true); calibration.SynchronizeCorrections(value.SelectorCorrections); }

    public MenuStencilResult? Observe(BgraFrame image, double timestamp)
    {
        if (catalog is null || trackedContext is null && !cadence.ShouldSearch(timestamp)) return null;
        var pixels = new ImageStencilPixels(image, catalog.ReferenceWidth, catalog.ReferenceHeight, catalog.BandStart, catalog.BandEnd);
        comparisons = 0;
        if (trackedContext is { } context && catalog.Scenes.FirstOrDefault(scene => scene.ContextIdentifier == context) is { } scene)
        {
            if (context == "select-profile" && catalog.Scenes.FirstOrDefault(item => item.ContextIdentifier == "clear-save") is { } modal
                && BestReacquisitionProbe(modal, pixels) is { } modalProbe && modalProbe.Confidence >= ProbeThresholdFor("clear-save"))
            {
                var parentOffset = trackedOffset; trackedOffset = ProbeOffset(modal, modalProbe);
                if (Validate(modal, modalProbe, pixels, image, timestamp, MenuStencilPhase.Searching) is { } modalResult)
                {
                    trackedContext = "clear-save"; trackedLanguage = modalProbe.Language ?? trackedLanguage;
                    missingProbeFrames = 0; framesUntilValidation = ValidationInterval; lastValidated = modalResult; return modalResult;
                }
                trackedOffset = parentOffset;
            }
            var probe = BestScore(VariantsFor(scene.Probe.SearchProbeVariants, trackedLanguage), pixels,
                scene.Probe.Bounds.Offset(trackedOffset.X, trackedOffset.Y), 4, 6, scene.Probe.Bounds);
            if (probe is not null && IsDialog(context) && Validate(scene, probe, pixels, image, timestamp, MenuStencilPhase.Tracking) is { } dialogResult)
            {
                missingProbeFrames = 0; trackedLanguage = probe.Language ?? trackedLanguage;
                lastValidated = dialogResult; framesUntilValidation = ValidationInterval; return dialogResult;
            }
            if (probe is not null && !IsDialog(context) && probe.Confidence >= ProbeThresholdFor(context))
            {
                missingProbeFrames = 0; trackedLanguage = probe.Language ?? trackedLanguage; trackedOffset = ProbeOffset(scene, probe);
                framesUntilValidation--;
                if (framesUntilValidation <= 0 || lastValidated is null)
                {
                    if (Checkpoint(scene, probe, pixels, image, timestamp) is { } checkpoint) return checkpoint;
                    if (Validate(scene, probe, pixels, image, timestamp, MenuStencilPhase.Tracking) is { } validated)
                    { lastValidated = validated; framesUntilValidation = ValidationInterval; return validated; }
                    var preferred = trackedLanguage; Reset(preservingLanguage: true); _ = cadence.ShouldSearch(timestamp);
                    return Reacquire(pixels, image, timestamp, preferred);
                }
                return TrackedResult(scene, probe, pixels, image, timestamp);
            }
            missingProbeFrames++;
            if (missingProbeFrames < LossGraceFrames && lastValidated is { } previous)
            {
                var selection = DetectSelection(scene, trackedLanguage, pixels, image, timestamp, recordsCalibration: false);
                var result = previous with
                {
                    IsMatch = true, Confidence = probe?.Confidence ?? 0, SelectedOption = selection.Name ?? previous.SelectedOption,
                    SelectorCandidates = selection.Matches.Count == 0 ? previous.SelectorCandidates : selection.Matches,
                    SelectorSearchRegions = selection.Regions, SelectorLanguageEvidenceCount = selection.LanguageEvidence,
                    SelectorForegroundEvidenceCount = selection.Name is null ? previous.SelectorForegroundEvidenceCount : selection.ForegroundEvidence,
                    Phase = MenuStencilPhase.Tracking, ComparisonCount = comparisons, SourceTimestamp = timestamp, LanguageIdentifier = trackedLanguage
                };
                lastValidated = result; return result;
            }
            var preferredLanguage = trackedLanguage; Reset(preservingLanguage: true);
            return Reacquire(pixels, image, timestamp, preferredLanguage);
        }
        return Reacquire(pixels, image, timestamp, trackedLanguage);
    }

    public static double? QualifiedSelectorPairConfidence(double left, double right, int leftForegroundPixelCount = 0, int rightForegroundPixelCount = 0)
    {
        static bool Qualifies(double score, int pixels) => score >= SelectorThreshold || score >= SelectorEvidenceThreshold && pixels >= SelectorMinimumForegroundPixels
            || score >= FadedSelectorEvidenceThreshold && pixels >= FadedSelectorMinimumForegroundPixels;
        return Qualifies(left, leftForegroundPixelCount) && Qualifies(right, rightForegroundPixelCount) ? (left + right) / 2 : null;
    }
    private static bool IsDialog(string context) => context is "quit-game" or "quit-to-menu";
    private static double ProbeThresholdFor(string context) => context switch
    {
        "controller" => .31, "select-profile" => .34, "quit-game" or "quit-to-menu" => .10,
        "options" or "game-options" or "video" or "video-advanced-settings" or "remap-controller" or "controller-advanced-settings" or "keyboard" => .36,
        _ => ProbeThreshold
    };
    private static double ValidationThresholdFor(string context) => context is "options" or "game-options" or "video" or "video-advanced-settings"
        or "controller" or "remap-controller" or "controller-advanced-settings" or "keyboard" or "quit-game" or "quit-to-menu" ? .36 : AnchorThreshold;
    private static IReadOnlyList<Variant> VariantsFor(IReadOnlyList<Variant> variants, string? language)
    {
        if (language is null) return variants;
        var matched = variants.Where(variant => variant.LanguageIdentifier == language).Concat(variants.Where(variant => variant.LanguageIdentifier is null)).ToArray();
        if (matched.Length != 0) return matched;
        var fallback = variants.Where(variant => variant.LanguageIdentifier is "en" or null).ToArray();
        return fallback.Length == 0 ? variants.Take(1).ToArray() : fallback;
    }
    private Score? BestScore(IReadOnlyList<Variant> variants, ImageStencilPixels pixels, StencilRect around,
        int radiusX, int radiusY, StencilRect? referenceBounds = null, Func<StencilRect, bool>? accepts = null,
        ImageStencilForegroundMask? idleMask = null)
    {
        Score? best = null;
        foreach (var variant in variants)
        {
            var bounds = referenceBounds is { } reference && variant.NominalBounds is { } nominal
                ? nominal.Offset(around.X - reference.X, around.Y - reference.Y) : around;
            for (var y = -radiusY; y <= radiusY; y++) for (var x = -radiusX; x <= radiusX; x++)
            {
                var candidate = bounds.Offset(x, y); if (accepts?.Invoke(candidate) == false) continue;
                comparisons++; if (pixels.Compare(variant.Kernel, candidate) is not { } comparison) continue;
                var confidence = comparison.Confidence(.82, 105);
                if (confidence > (best?.Confidence ?? double.NegativeInfinity))
                    best = new(confidence, 0, 0, candidate, variant.Reference, variant.LanguageIdentifier, variant.NominalBounds);
            }
        }
        return best is null ? null : best with { Foreground = pixels.LikelyMenuTextPixelCount(best.Rect, idleMask), BrightForeground = pixels.BrightMenuPixelCount(best.Rect, idleMask) };
    }
    private static StencilRect OutputRect(StencilRect rect, BgraFrame image, Scene scene) =>
        new StencilRect(rect.X * image.Width / scene.ReferenceWidth, rect.Y * image.Height / scene.ReferenceHeight,
            rect.Width * image.Width / scene.ReferenceWidth, rect.Height * image.Height / scene.ReferenceHeight).Intersect(new(0, 0, image.Width, image.Height));
    private static Offset ProbeOffset(Scene scene, Score probe) => new(probe.Rect.X - (probe.NominalBounds ?? scene.Probe.Bounds).X, probe.Rect.Y - (probe.NominalBounds ?? scene.Probe.Bounds).Y);
    private static double Median(IEnumerable<double> values)
    { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2]; }
    private static double CenterDistance(StencilRect a, StencilRect b) => Math.Sqrt(Math.Pow(a.MidX - b.MidX, 2) + Math.Pow(a.MidY - b.MidY, 2));
    private static bool Intersects(StencilRect a, StencilRect b) => !a.Intersect(b).IsEmpty;
    private static double SelectorConfidence(MenuStencilResult result) => result.SelectedOption is null || result.SelectorCandidates.Count == 0 ? 0 : result.SelectorCandidates.Average(match => match.Confidence);
    private static double SceneScore(Validated entry) => entry.Result.Confidence + entry.Candidate.Score.Confidence * .20 + entry.Result.SceneEvidenceRatio * .35
        + SelectorConfidence(entry.Result) * .25 * entry.Result.SceneEvidenceRatio;
}
