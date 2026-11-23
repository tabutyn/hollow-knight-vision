namespace HollowKnightVision.Windows.Core;

internal static class MenuSelectorPlacement
{
    internal const int MaximumOffsetX = 8;
    internal const int MaximumOffsetY = 6;

    private static readonly HashSet<(string Context, string Identifier)> TextRows = LabelingContractCatalog.Contexts
        .Where(context => context.Identifier is "main-title" or "extras" or "options" or "pause" or "quit-to-menu" or "quit-game")
        .SelectMany(context => context.Labels.Select(label => (context.Identifier, LabelingClassIdentity.CanonicalIdentifier(label.Identifier))))
        .Where(row => row.Item2 != "extras.menu-style").ToHashSet();

    internal static bool AlignsWithText(string context, string identifier) =>
        TextRows.Contains((context, LabelingClassIdentity.CanonicalIdentifier(identifier)));

    internal static bool IsLocal(StencilRect candidate, StencilRect reference) =>
        Math.Abs(candidate.MidX - reference.MidX) <= MaximumOffsetX
        && Math.Abs(candidate.MidY - reference.MidY) <= MaximumOffsetY;

    internal static StencilRect Resolve(StencilRect candidate, StencilRect reference) =>
        IsLocal(candidate, reference) ? candidate : reference;

    internal static StencilRect BesideText(StencilRect reference, StencilRect text, bool isLeft)
    {
        var gap = isLeft ? text.X - reference.Right : reference.X - text.Right;
        if (gap is < 2 or > 24) gap = 10;
        return new(isLeft ? text.X - gap - reference.Width : text.Right + gap,
            text.MidY - reference.Height / 2, reference.Width, reference.Height);
    }
}
