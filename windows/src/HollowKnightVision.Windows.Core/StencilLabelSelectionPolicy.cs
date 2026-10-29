namespace HollowKnightVision.Windows.Core;

public static class StencilLabelSelectionPolicy
{
    public static IReadOnlyList<MenuStencilLabel> AvailableLabels(string context,
        IReadOnlyList<LabelingExampleAnnotation> rectangles, bool modded, bool hidesCompleted,
        bool includesDeferredLabels = false, IReadOnlyList<MenuStencilLabel>? custom = null)
    {
        var definitions = LabelingContractCatalog.Contexts.FirstOrDefault(item => item.Identifier == context)?.Labels ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return definitions.Concat(custom ?? []).Where(definition =>
        {
            var identifier = LabelingClassIdentity.CanonicalIdentifier(definition.Identifier);
            if (!seen.Add(identifier) || (!modded && identifier == "shared.mods")) return false;
            // The shared contract allows zero during phase one. Once deferred
            // labeling is enabled, one positive Clear Save box finishes this task.
            if (hidesCompleted && identifier == "select-profile.clear-save")
                return includesDeferredLabels && !rectangles.Any(item => !item.IsHardNegative
                    && LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) == identifier);
            return !hidesCompleted || context == "game"
                || !LabelingContractEvaluator.ShouldAdvance(identifier, context, rectangles);
        }).Select(definition => definition.Identifier == "shared.select-decoration"
            ? definition with { Name = rectangles.Count(item => !item.IsHardNegative
                && LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) == definition.Identifier) == 0
                    ? "Left Select Decoration" : "Right Select Decoration" }
            : definition).ToArray();
    }
}
