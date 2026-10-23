namespace HollowKnightVision.Windows.Core;

public sealed record LabelingContextTransition(string ContextIdentifier, SavedLabelingExample? Example,
    BgraFrame? Capture, Guid CaptureGroupIdentifier, IReadOnlyList<LabelingExampleAnnotation> Rectangles,
    IReadOnlyList<string> KnownClasses);

public static class LabelingContextTransitionPolicy
{
    public static LabelingContextTransition? Resolve(string destination, SavedLabelingExample? currentExample,
        BgraFrame frozenFrame, Func<string, SavedLabelingExample?> findStencil)
    {
        if (currentExample?.Manifest.ContextIdentifier == destination) return null;
        // Saved menu references are context-owned. Gameplay always starts from
        // this paused capture; it must not reopen an arbitrary training image.
        var saved = destination == "game" ? null : findStencil(destination);
        if (saved is not null)
        {
            if (saved.Manifest.ContextIdentifier != destination)
                throw new InvalidOperationException("Stencil reference belongs to a different context.");
            var manifest = saved.Manifest.Canonicalized();
            return new(destination, saved, null, manifest.CaptureGroupIdentifier, manifest.Annotations, manifest.EffectiveKnownClassIdentifiers);
        }
        return new(destination, null, frozenFrame, Guid.NewGuid(), [], []);
    }
}

/// <summary>Suppresses synchronous selector callbacks while loading one complete editor state.</summary>
public sealed class LabelingSelectionUpdateGuard
{
    private int depth;
    public bool IsUpdating => depth > 0;
    public void Run(Action update)
    {
        depth++;
        try { update(); }
        finally { depth--; }
    }
}
