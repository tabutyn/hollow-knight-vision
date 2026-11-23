namespace HollowKnightVision.Windows.Core;

/// <summary>Tracks the displayed Hacker route through a jump or fall between authored platforms.</summary>
public sealed class HackerAtlasRoute
{
    private readonly AtlasPathNavigator navigator = new();
    private AtlasPathPoint? objective;
    private double lastTime = double.NegativeInfinity;

    public IReadOnlyList<AtlasPathPoint> Observe(AtlasPlatformGraph graph, AtlasPathPoint feet, bool grounded,
        AtlasPathPoint target, double time)
    {
        if (!double.IsFinite(time))
        {
            Interrupt();
            return [];
        }
        if (objective != target || time - lastTime is < 0 or >= .45)
            navigator.ClearObjective();
        objective = target;
        lastTime = time;
        navigator.Follow(graph, feet, grounded, measured: false, admitted: null, walked: [],
            fixedObjective: target, fixedObjectiveOnGround: false);
        return navigator.SelectedRoute;
    }

    public void Interrupt()
    {
        navigator.Interrupt();
        objective = null;
        lastTime = double.NegativeInfinity;
    }
}
