namespace HollowKnightVision.Windows.Core;

public sealed record LabelingFrameOpenState(string ContextIdentifier, string SelectedClassIdentifier,
    IReadOnlyList<LabelingExampleAnnotation> Rectangles);

public static class LabelingFrameOpeningPolicy
{
    public static IReadOnlyList<string> ContextClasses(string context, IReadOnlyList<string>? customClasses = null) =>
        (LabelingContractCatalog.Contexts.FirstOrDefault(item => item.Identifier == context)?.Labels.Select(item => item.Identifier) ?? [])
        .Concat(context == "game" ? customClasses ?? [] : []).Select(LabelingClassIdentity.CanonicalIdentifier)
        .Distinct(StringComparer.Ordinal).ToArray();

    public static string ClassIdentifierForOpening(string context, IEnumerable<string> annotations,
        string? preferredClassIdentifier = null, IReadOnlyList<string>? customClasses = null)
    {
        var valid = ContextClasses(context, customClasses);
        if (!string.IsNullOrWhiteSpace(preferredClassIdentifier) && valid.Contains(LabelingClassIdentity.CanonicalIdentifier(preferredClassIdentifier)))
            return LabelingClassIdentity.CanonicalIdentifier(preferredClassIdentifier);
        return annotations.Select(LabelingClassIdentity.CanonicalIdentifier).FirstOrDefault(valid.Contains) ?? valid.FirstOrDefault() ?? "";
    }

    public static LabelingFrameOpenState? StateForOpening(LabelingExampleManifest manifest,
        string? preferredClassIdentifier = null, IReadOnlyList<string>? customClasses = null)
    {
        var saved = manifest.Canonicalized();
        if (!LabelingContractCatalog.Contexts.Any(item => item.Identifier == saved.ContextIdentifier)) return null;
        var context = saved.ContextIdentifier;
        if (context is "enemies" or "world" or "shared")
        {
            var anchor = preferredClassIdentifier ?? saved.Annotations.FirstOrDefault()?.ClassIdentifier;
            context = string.IsNullOrWhiteSpace(anchor) ? "game" : LabelingContractCatalog.ScreenContexts
                .FirstOrDefault(item => ContextClasses(item.Identifier, customClasses).Contains(LabelingClassIdentity.CanonicalIdentifier(anchor)))?.Identifier ?? "game";
        }
        // A saved screen owns its context; selection may not reclassify it.
        return new(context, ClassIdentifierForOpening(context, saved.Annotations.Select(item => item.ClassIdentifier),
            preferredClassIdentifier, customClasses), saved.Annotations);
    }
}

public static class RecentCapturedFrameLabelingPolicy
{
    public static SavedLabelingExample? SavePredictedDraft(LabelingExampleStore store, RecentCapturedFrame frame,
        LabelingFrameOpenState state, Guid captureGroupIdentifier, string languageIdentifier) =>
        state.Rectangles.Count == 0 ? null : store.Save(frame.Image, state.ContextIdentifier, state.Rectangles,
            captureGroupIdentifier: captureGroupIdentifier, languageIdentifier: languageIdentifier);

    public static LabelingFrameOpenState StateForOpening(RecentCapturedFrame frame, string fallbackContext,
        string fallbackClassIdentifier, IReadOnlyList<string>? customClasses = null)
    {
        var observed = frame.Detections.Select(item => LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier)).ToHashSet(StringComparer.Ordinal);
        var context = observed.Contains("main-title.hollow-knight-logo") ? "main-title"
            : observed.Contains("select-profile.heading") ? "select-profile"
            : new[] { "game.health", "game.mana", "game.geo" }.All(observed.Contains) ? "game" : fallbackContext;
        var known = LabelingContractCatalog.Contexts.SelectMany(item => item.Labels).Select(item => item.Identifier)
            .Concat(customClasses ?? []).Select(LabelingClassIdentity.CanonicalIdentifier).ToHashSet(StringComparer.Ordinal);
        var rectangles = frame.Detections.Select(item => item with { ClassIdentifier = LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) })
            .Where(item => known.Contains(item.ClassIdentifier))
            .Select(item => new LabelingExampleAnnotation(Guid.NewGuid(), item.ClassIdentifier,
                item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height)).ToArray();
        var valid = LabelingFrameOpeningPolicy.ContextClasses(context, customClasses);
        var fallback = string.IsNullOrWhiteSpace(fallbackClassIdentifier) ? "" : LabelingClassIdentity.CanonicalIdentifier(fallbackClassIdentifier);
        var selected = rectangles.FirstOrDefault(item => valid.Contains(item.ClassIdentifier))?.ClassIdentifier
            ?? (valid.Contains(fallback) ? fallback : valid.FirstOrDefault() ?? "");
        return new(context, selected, rectangles);
    }

    public static IReadOnlyList<SavedLabelingExample> RecentLabeledFrames(IEnumerable<SavedLabelingExample> examples) =>
        examples.Where(item => item.Manifest.Annotations.Count > 0 || item.Manifest.EffectiveKnownClassIdentifiers.Count > 0)
            .OrderByDescending(item => item.Manifest.CreatedAt).ToArray();
}
