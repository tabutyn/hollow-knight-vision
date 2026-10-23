using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

public sealed partial class MenuStencilTracker
{
    private MenuStencilResult? Reacquire(ImageStencilPixels pixels, BgraFrame image, double timestamp, string? preferredLanguage = null)
    {
        if (catalog is null) return null;
        var candidates = catalog.Scenes.SelectMany(scene => ReacquisitionProbes(scene, pixels, preferredLanguage).Select(score => new Candidate(scene, score)))
            .OrderByDescending(candidate => candidate.Score.Confidence).ToArray();
        var eligible = candidates.Where(candidate => candidate.Score.Confidence >= ProbeThresholdFor(candidate.Scene.ContextIdentifier)).ToArray();
        MenuStencilResult? Failed(Candidate? candidate)
        {
            if (preferredLanguage is not null && ++languageMissFrames >= ValidationInterval)
            { languageMissFrames = 0; return Reacquire(pixels, image, timestamp); }
            return candidate is null ? null : FailedSearchResult(candidate, image, timestamp);
        }
        if (eligible.Length == 0) return Failed(candidates.FirstOrDefault());
        var contexts = eligible.Select(candidate => candidate.Scene.ContextIdentifier).Distinct(StringComparer.Ordinal).ToArray();
        var plausible = contexts.SelectMany(context => candidates.Where(candidate => candidate.Scene.ContextIdentifier == context));
        var validated = new List<Validated>();
        foreach (var candidate in plausible)
        {
            trackedOffset = ProbeOffset(candidate.Scene, candidate.Score);
            if (Validate(candidate.Scene, candidate.Score, pixels, image, timestamp, MenuStencilPhase.Searching) is { } result)
                validated.Add(new(candidate, result, trackedOffset));
        }
        static double HorizontalSpan(MenuStencilResult result) => result.SelectorCandidates.Count < 2 ? 0
            : result.SelectorCandidates.Max(match => match.Rect.MidX) - result.SelectorCandidates.Min(match => match.Rect.MidX);
        var rankedContext = validated.MaxBy(SceneScore)?.Candidate.Scene.ContextIdentifier;
        var modalContext = validated.Any(item => item.Candidate.Scene.ContextIdentifier == "clear-save" && item.Candidate.Score.Confidence >= .90
            && item.Result.Confidence >= .80 && item.Result.SceneEvidenceRatio >= .90) ? "clear-save" : null;
        var gameOptionsContext = eligible.Any(candidate => candidate.Scene.ContextIdentifier == "game-options" && candidate.Score.Confidence >= .65)
            && validated.Any(item => item.Candidate.Scene.ContextIdentifier == "game-options" && item.Result.SceneEvidenceRatio >= .50 && HorizontalSpan(item.Result) >= 240)
                ? "game-options" : null;
        var bestContext = modalContext ?? gameOptionsContext ?? rankedContext;
        var sameScene = validated.Where(item => item.Candidate.Scene.ContextIdentifier == bestContext).ToArray();
        var resolved = sameScene.OrderByDescending(item => item.Result.Confidence).ThenByDescending(item => item.Candidate.Score.Confidence).FirstOrDefault();
        if (resolved is null) return Failed(eligible[0]);
        if (preferredLanguage is not null && (resolved.Result.SceneEvidenceRatio < .90 || resolved.Result.Confidence < .75))
            return Reacquire(pixels, image, timestamp);
        var baseResult = resolved.Result;
        bool BracketsAnchor(MenuStencilResult candidate)
        {
            if (candidate.SelectedOption is not { } name || candidate.SelectorCandidates.Count != 2) return false;
            var named = baseResult.Anchors.FirstOrDefault(anchor => anchor.Name == name);
            var option = resolved.Candidate.Scene.Options.FirstOrDefault(option => option.Name == name);
            StencilRect? anchorRect = named?.Rect ?? (option is null ? null : OutputRect(option.Bounds.Offset(resolved.Offset.X, resolved.Offset.Y), image, resolved.Candidate.Scene));
            if (anchorRect is not { } anchor) return false;
            var selectors = candidate.SelectorCandidates.OrderBy(match => match.Rect.MidX).ToArray();
            var solved = candidate.SelectorSearchRegions.Where(region => region.IsSolved).ToArray();
            if (selectors.Any(selector => Math.Abs(selector.Rect.MidY - anchor.MidY) > 16
                || !solved.Any(region => Intersects(region.Rect, selector.Rect)))) return false;
            return named is null ? selectors[1].Rect.MidX - selectors[0].Rect.MidX >= 24
                : selectors[0].Rect.MidX < anchor.X && selectors[1].Rect.MidX > anchor.Right;
        }
        var baseMinimum = baseResult.SelectorCandidates.Count == 0 ? 0 : baseResult.SelectorCandidates.Min(match => match.Confidence);
        var baseStrong = baseResult.SelectedOption is not null && BracketsAnchor(baseResult) && (baseMinimum >= .65
            || baseResult.SelectorForegroundEvidenceCount >= 12 || baseResult.ContextIdentifier == "select-profile" && baseResult.SelectorLanguageEvidenceCount > 0);
        var geometric = sameScene.Where(item => BracketsAnchor(item.Result)).ToArray();
        var supported = geometric.Where(item => item.Result.SelectorForegroundEvidenceCount >= (bestContext == "main-title" ? 12 : SelectorMinimumForegroundPixels)).ToArray();
        var pool = supported.Length == 0 ? geometric : supported;
        var selection = baseStrong ? resolved : pool.MaxBy(item => SelectorConfidence(item.Result));
        var resultWithSelection = selection is null ? baseResult : baseResult with
        {
            SelectedOption = selection.Result.SelectedOption, SelectorCandidates = selection.Result.SelectorCandidates,
            SelectorSearchRegions = selection.Result.SelectorSearchRegions, SelectorLanguageEvidenceCount = selection.Result.SelectorLanguageEvidenceCount,
            SelectorForegroundEvidenceCount = selection.Result.SelectorForegroundEvidenceCount, ComparisonCount = comparisons,
            LanguageIdentifier = resolved.Candidate.Score.Language ?? preferredLanguage ?? trackedLanguage
        };
        trackedOffset = resolved.Offset; trackedContext = resolved.Candidate.Scene.ContextIdentifier;
        trackedLanguage = resolved.Candidate.Score.Language ?? preferredLanguage ?? trackedLanguage;
        missingProbeFrames = 0; languageMissFrames = 0; framesUntilValidation = ValidationInterval;
        lastValidated = resultWithSelection; return resultWithSelection;
    }

    private Score? BestReacquisitionProbe(Scene scene, ImageStencilPixels pixels) => ReacquisitionProbes(scene, pixels).MaxBy(score => score.Confidence);
    private IReadOnlyList<Score> ReacquisitionProbes(Scene scene, ImageStencilPixels pixels, string? preferredLanguage = null)
    {
        var scores = new List<Score>();
        foreach (var variant in VariantsFor(scene.Probe.SearchProbeVariants, preferredLanguage))
        {
            Score? best = null;
            foreach (var offset in ProbeSearchOffsets(scene))
            {
                var score = BestScore([variant], pixels, scene.Probe.Bounds.Offset(offset.X, offset.Y), 3, 10, scene.Probe.Bounds);
                if (score is not null && score.Confidence > (best?.Confidence ?? double.NegativeInfinity)) best = score;
            }
            if (best is not null) scores.Add(best);
        }
        return scores;
    }

    public static IReadOnlyList<double> ClearSaveVerticalProbeOffsets(double referenceY, IEnumerable<double> profileRowCenters)
    {
        var centers = profileRowCenters.Order().ToArray();
        if (centers.Length == 0) return [0];
        var source = centers.MinBy(center => Math.Abs(center - referenceY));
        return centers.Select(center => center - source).ToArray();
    }
    private IReadOnlyList<Offset> ProbeSearchOffsets(Scene scene)
    {
        if (scene.ContextIdentifier != "clear-save" || catalog?.Scenes.FirstOrDefault(item => item.ContextIdentifier == "select-profile") is not { } profile) return [default];
        return ClearSaveVerticalProbeOffsets(scene.Probe.Bounds.MidY, profile.Options.Where(option => option.ClassIdentifier.StartsWith("select-profile.slot-", StringComparison.Ordinal))
            .Select(option => option.Bounds.MidY)).Select(y => new Offset(0, y)).ToArray();
    }
    private MenuStencilResult FailedSearchResult(Candidate candidate, BgraFrame image, double timestamp)
    {
        var scene = candidate.Scene; var score = candidate.Score;
        return new(scene.ContextIdentifier, false, score.Confidence, null,
            [new(scene.Probe.ClassIdentifier, scene.Probe.Name, OutputRect(score.Rect, image, scene), score.Confidence, score.Reference)],
            [], SearchRegions(scene, image, ProbeOffset(scene, score), score.Language), 0, 0, 0, MenuStencilPhase.Searching,
            comparisons, timestamp, score.Language);
    }
}
