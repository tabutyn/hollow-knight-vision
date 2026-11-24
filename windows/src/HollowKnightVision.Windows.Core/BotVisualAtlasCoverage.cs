namespace HollowKnightVision.Windows.Core;

/// <summary>Tracks which cells a visual route has exposed from verified poses.</summary>
public sealed class BotVisualAtlasCoverage
{
    private readonly HashSet<(int X, int Y)> occupied = [];
    private readonly HashSet<(int X, int Y)> earned = [];
    private AtlasPoint? lastPlaced;

    public bool HasCoverage => occupied.Count > 0;

    public void Interrupt() => lastPlaced = null;

    public void Reset()
    {
        Interrupt();
        occupied.Clear();
        earned.Clear();
    }

    public void Register(AtlasPoint pose, WorldSize size, IReadOnlyList<PixelRect> omissions,
        VisualRoomCompositionBounds bounds = default) => Place(pose, size, omissions, bounds, measured: true);

    public void ObserveVerified(AtlasPoint? pose, WorldSize size, IReadOnlyList<PixelRect> omissions,
        VisualRoomCompositionBounds bounds, GroundHypothesisTrackingResult tracking, bool admitsPlacement)
    {
        // A global match establishes a position in the existing grid. Its
        // difference from the local estimate is not camera travel.
        if (pose is null || tracking.GlobalCorrection is { } correction && (correction.X != 0 || correction.Y != 0))
            Interrupt();
        if (pose is { } placed && admitsPlacement)
            Place(placed, size, omissions, bounds, GroundMotionEvidence.IsMeasured(tracking));
    }

    private void Place(AtlasPoint pose, WorldSize size, IReadOnlyList<PixelRect> omissions,
        VisualRoomCompositionBounds bounds, bool measured)
    {
        if (!AtlasPathPoint.TryQuantize(pose.X, pose.Y, out _)) return;
        var left = Math.Max(pose.X, bounds.MinimumWorldX ?? pose.X);
        var right = Math.Min(pose.X + size.Width, bounds.MaximumWorldX ?? pose.X + size.Width);
        var delta = lastPlaced is { } old
            ? Math.Sqrt(Math.Pow(old.X - pose.X, 2) + Math.Pow(old.Y - pose.Y, 2)) : 0;
        lastPlaced = pose;
        var step = BotAtlasFrontiers.CellSize;
        for (var y = BotAtlasFrontiers.FirstCenter(pose.Y + size.Height * .18);
             y < BotAtlasFrontiers.FirstCenter(pose.Y + size.Height * .78); y++)
        for (var x = BotAtlasFrontiers.FirstCenter(left); x < BotAtlasFrontiers.FirstCenter(right); x++)
        {
            var pixelX = (x + .5) * step - pose.X;
            var pixelY = size.Height - ((y + .5) * step - pose.Y);
            if (omissions.Any(rect => rect.X <= pixelX && pixelX < rect.Right && rect.Y <= pixelY && pixelY < rect.Bottom)) continue;
            if (occupied.Add((x, y)) && measured && delta > 1 && delta <= size.Width / 2) earned.Add((x, y));
        }
    }

    public BotAtlasView? View(AtlasPoint? pose, WorldSize size, int revision, int? health) =>
        pose is { } placed && HasCoverage && AtlasPathPoint.TryQuantize(placed.X, placed.Y, out _)
            ? BotAtlasFrontiers.View(placed, size, revision, health, occupied.Count, earned.Count, (x, y) => occupied.Contains((x, y)))
            : null;
}
