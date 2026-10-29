using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

public sealed partial class MenuStencilTracker
{
    private MenuStencilResult? Validate(Scene scene, Score probe, ImageStencilPixels pixels, BgraFrame image, double timestamp, MenuStencilPhase phase)
    {
        var scored = new List<(Anchor Anchor, Score Score)>();
        var threshold = ValidationThresholdFor(scene.ContextIdentifier);
        var dx = IsDialog(scene.ContextIdentifier) ? 8 : 4; var dy = IsDialog(scene.ContextIdentifier) ? 12 : 6;
        foreach (var anchor in scene.Anchors)
        {
            if (anchor.ClassIdentifier == scene.Probe.ClassIdentifier)
            {
                if (BestScore(VariantsFor(anchor.Variants, probe.Language), pixels, probe.Rect, 2, 2) is { } fullProbe) scored.Add((anchor, fullProbe));
                continue;
            }
            var score = BestScore(VariantsFor(anchor.Variants, probe.Language), pixels, anchor.Bounds.Offset(trackedOffset.X, trackedOffset.Y), dx, dy, anchor.Bounds);
            if ((score?.Confidence ?? double.NegativeInfinity) < AnchorThreshold && anchor.CalibratedBounds is { } calibratedBounds)
            {
                var calibrated = BestScore(VariantsFor(anchor.Variants, probe.Language), pixels, calibratedBounds.Offset(trackedOffset.X, trackedOffset.Y), dx, dy, anchor.Bounds);
                if (calibrated is not null && calibrated.Confidence > (score?.Confidence ?? double.NegativeInfinity)) score = calibrated;
            }
            if (score is not null) scored.Add((anchor, score));
        }
        if (IsDialog(scene.ContextIdentifier))
        {
            var stableRows = scored.Where(item => item.Anchor.ClassIdentifier is "shared.yes" or "shared.no" && item.Score.Confidence >= threshold).ToArray();
            if (stableRows.Length == 2)
            {
                trackedOffset = new(Median(stableRows.Select(item => item.Score.Rect.MidX - (item.Score.NominalBounds ?? item.Anchor.Bounds).MidX)),
                    Median(stableRows.Select(item => item.Score.Rect.MidY - (item.Score.NominalBounds ?? item.Anchor.Bounds).MidY)));
                var titleIndex = scored.FindIndex(item => item.Anchor.ClassIdentifier == scene.Probe.ClassIdentifier);
                if (titleIndex >= 0 && BestScore(VariantsFor(scene.Probe.Variants, probe.Language), pixels,
                        scene.Probe.Bounds.Offset(trackedOffset.X, trackedOffset.Y), 3, 3, scene.Probe.Bounds) is { } title)
                    scored[titleIndex] = (scene.Probe, title);
            }
        }
        var accepted = scored.Count(item => item.Score.Confidence >= threshold);
        var weighted = scored.Select(item =>
        {
            var membership = catalog?.ScreenMembershipCounts?.GetValueOrDefault(item.Anchor.ClassIdentifier)
                ?? catalog?.Scenes.Count(candidate => candidate.Anchors.Any(anchor => anchor.ClassIdentifier == item.Anchor.ClassIdentifier)) ?? 0;
            var weight = item.Anchor.ClassIdentifier == scene.Probe.ClassIdentifier ? 1 : membership == 1 ? 2 : .4;
            return (Value: item.Score.Confidence * weight, Weight: weight);
        }).ToArray();
        var confidence = weighted.Sum(item => item.Value) / Math.Max(1, weighted.Sum(item => item.Weight));
        var required = Math.Min(scene.ContextIdentifier == "options" ? 4 : 2, scene.Anchors.Count);
        if (confidence < threshold || accepted < required) return null;
        var anchors = WithRepeatedProfileActions(scene, pixels, image, probe.Language,
            scored.Select(item => new MenuStencilMatch(item.Anchor.ClassIdentifier, item.Anchor.Name,
                OutputRect(item.Score.Rect, image, scene), item.Score.Confidence, item.Score.Reference)).ToArray());
        var currentBounds = scored.ToDictionary(item => item.Anchor.ClassIdentifier, item => item.Score.Rect, StringComparer.Ordinal);
        var selection = DetectSelection(scene, probe.Language, pixels, image, timestamp, false, currentBounds);
        if (selection.Name is { } selectedName && scene.Options.FirstOrDefault(option => option.Name == selectedName) is { } selectedOption
            && anchors.FirstOrDefault(anchor => anchor.Name == selectedName) is { } selectedAnchor
            && selection.Matches.Any(match => Intersects(match.Rect, selectedAnchor.Rect)))
        {
            selection = DetectSelection(scene, probe.Language, pixels, image, timestamp, false, currentBounds, new HashSet<string> { selectedOption.ClassIdentifier });
            if (selection.Name == selectedName) selection = selection with { Name = null, Matches = [], LanguageEvidence = 0, ForegroundEvidence = 0 };
        }
        return new(scene.ContextIdentifier, true, confidence, selection.Name, anchors, selection.Matches, selection.Regions,
            selection.LanguageEvidence, selection.ForegroundEvidence, (double)accepted / Math.Max(1, scored.Count), phase,
            comparisons, timestamp, probe.Language ?? trackedLanguage);
    }

    private MenuStencilResult? TrackedResult(Scene scene, Score probe, ImageStencilPixels pixels, BgraFrame image, double timestamp)
    {
        if (lastValidated is not { } previous) return null;
        var selection = DetectSelection(scene, trackedLanguage, pixels, image, timestamp, true);
        var anchors = WithRepeatedProfileActions(scene, pixels, image, trackedLanguage,
            previous.Anchors.Select(match => match.ClassIdentifier == scene.Probe.ClassIdentifier
                ? match with { Rect = OutputRect(probe.Rect, image, scene), Confidence = probe.Confidence, Reference = probe.Reference } : match).ToArray());
        var result = new MenuStencilResult(scene.ContextIdentifier, true, probe.Confidence, selection.Name, anchors,
            selection.Matches, selection.Regions, selection.LanguageEvidence, selection.ForegroundEvidence, previous.SceneEvidenceRatio,
            MenuStencilPhase.Tracking, comparisons, timestamp, trackedLanguage);
        lastValidated = result; return result;
    }

    private MenuStencilResult? Checkpoint(Scene currentScene, Score currentProbe, ImageStencilPixels pixels, BgraFrame image, double timestamp)
    {
        if (catalog is null) return null;
        var currentOffset = trackedOffset; var validated = new List<Validated>();
        if (Validate(currentScene, currentProbe, pixels, image, timestamp, MenuStencilPhase.Tracking) is { } currentResult)
            validated.Add(new(new(currentScene, currentProbe), currentResult, currentOffset));
        var candidates = new List<Candidate>();
        foreach (var scene in catalog.Scenes.Where(scene => scene.ContextIdentifier != currentScene.ContextIdentifier))
        {
            Score? best = null;
            foreach (var offset in ProbeSearchOffsets(scene))
            {
                var score = BestScore(VariantsFor(scene.Probe.SearchProbeVariants, trackedLanguage), pixels,
                    scene.Probe.Bounds.Offset(offset.X, offset.Y), 3, 10, scene.Probe.Bounds);
                if (score is not null && score.Confidence > (best?.Confidence ?? double.NegativeInfinity)) best = score;
            }
            if (best is not null && best.Confidence >= ProbeThresholdFor(scene.ContextIdentifier)) candidates.Add(new(scene, best));
        }
        var reserved = new[] { "controller", "select-profile", "clear-save" };
        foreach (var scene in catalog.Scenes.Where(scene => scene.ContextIdentifier != currentScene.ContextIdentifier && reserved.Contains(scene.ContextIdentifier)))
        {
            if (BestReacquisitionProbe(scene, pixels) is not { } score || score.Confidence < .25) continue;
            candidates.RemoveAll(candidate => candidate.Scene.ContextIdentifier == scene.ContextIdentifier); candidates.Add(new(scene, score));
        }
        candidates = candidates.OrderByDescending(candidate => candidate.Score.Confidence).ToList();
        var selected = candidates.DistinctBy(candidate => candidate.Scene.ContextIdentifier).Take(SceneChallengeCandidateLimit).ToList();
        foreach (var context in reserved.Where(context => !selected.Any(candidate => candidate.Scene.ContextIdentifier == context)))
            if (candidates.FirstOrDefault(candidate => candidate.Scene.ContextIdentifier == context) is { } candidate) selected.Add(candidate);
        foreach (var candidate in selected)
        {
            trackedOffset = ProbeOffset(candidate.Scene, candidate.Score);
            if (Validate(candidate.Scene, candidate.Score, pixels, image, timestamp, MenuStencilPhase.Searching) is { } result)
                validated.Add(new(candidate, result, trackedOffset));
        }
        var current = validated.FirstOrDefault(item => item.Candidate.Scene.ContextIdentifier == currentScene.ContextIdentifier);
        if (current is null) { trackedOffset = currentOffset; return null; }
        if (current.Result.SceneEvidenceRatio < .90 || current.Result.Confidence < .75)
        { trackedOffset = currentOffset; return Reacquire(pixels, image, timestamp); }
        var challenger = validated.Where(item => item.Candidate.Scene.ContextIdentifier != currentScene.ContextIdentifier).MaxBy(SceneScore);
        if (challenger is not null && SceneScore(challenger) >= SceneScore(current) + SceneChallengeWinningMargin)
        { trackedOffset = currentOffset; return Reacquire(pixels, image, timestamp, trackedLanguage); }
        var updated = current.Result with { ComparisonCount = comparisons, LanguageIdentifier = current.Candidate.Score.Language ?? trackedLanguage };
        trackedOffset = current.Offset; trackedContext = current.Candidate.Scene.ContextIdentifier;
        trackedLanguage = current.Candidate.Score.Language ?? trackedLanguage; missingProbeFrames = 0;
        framesUntilValidation = ValidationInterval; lastValidated = updated; return updated;
    }
}
