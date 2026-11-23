namespace HollowKnightVision.Windows.Core;

public static class StencilLabelSelectionPolicy
{
    public static IReadOnlyList<MenuStencilLabel> AvailableLabels(string context,
        IReadOnlyList<LabelingExampleAnnotation> rectangles, bool modded, bool hidesCompleted,
        IReadOnlyList<MenuStencilLabel>? custom = null)
    {
        var definitions = LabelingContractCatalog.Contexts.FirstOrDefault(item => item.Identifier == context)?.Labels ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return definitions.Concat(custom ?? []).Where(definition =>
        {
            var identifier = LabelingClassIdentity.CanonicalIdentifier(definition.Identifier);
            if (!seen.Add(identifier) || (!modded && identifier == "shared.mods")
                || !LabelingHudStencilPolicy.IsLabelable(context, identifier)) return false;
            // Recognition accepts an empty profile. Setup needs one positive
            // Clear Save stencil on its occupied-slot reference.
            if (hidesCompleted && identifier == "select-profile.clear-save")
                return !rectangles.Any(item => !item.IsHardNegative
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
