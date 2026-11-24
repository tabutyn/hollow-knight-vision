using System.Collections.Immutable;

namespace HollowKnightVision.Windows.Core;

internal sealed record BotHackerCoverageSnapshot(ImmutableHashSet<(int X, int Y)> Cells, int Earned, int Revision)
{
    internal BotAtlasView? View(AtlasPoint pose, WorldSize size, int? health)
    {
        if (Cells.Count == 0 || size.Width <= 0 || size.Height <= 0
            || !AtlasPathPoint.TryQuantize(pose.X, pose.Y, out _)) return null;
        var view = BotAtlasFrontiers.View(pose, size, Revision, health, Cells.Count, Earned,
            (x, y) => Cells.Contains((x, y)));
        var scale = 640d / size.Width;
        return view with { Camera = [pose.X * scale, pose.Y * scale], Width = 640 };
    }
}

/// <summary>Samples opaque cells in the Hacker compositor's room-local tiles.</summary>
internal sealed class BotHackerAtlasCoverage
{
    private readonly Dictionary<long, WeakReference<BgraFrame>> sampledTiles = [];
    private AtlasPoint? lastCamera;

    internal BotHackerCoverageSnapshot Snapshot { get; private set; } = new(ImmutableHashSet<(int X, int Y)>.Empty, 0, 0);

    internal void Interrupt() => lastCamera = null;

    internal void Update(LiveTiledAtlasSnapshot atlas, AtlasPoint? camera = null, double width = 0)
    {
        var credit = false;
        if (lastCamera is { } old && camera is { } next)
        {
            var dx = next.X - old.X;
            var dy = next.Y - old.Y;
            var squared = dx * dx + dy * dy;
            credit = squared > 1 && squared <= width * width / 4;
        }
        lastCamera = camera;

        var cells = Snapshot.Cells.ToBuilder();
        var earned = Snapshot.Earned;
        const int step = (int)BotAtlasFrontiers.CellSize;
        foreach (var tile in atlas.Tiles)
        {
            var image = tile.Image;
            if (sampledTiles.TryGetValue(tile.Id, out var prior) && prior.TryGetTarget(out var previous)
                && ReferenceEquals(previous, image)) continue;
            sampledTiles[tile.Id] = new(image);
            for (var y = step / 2; y < image.Height; y += step)
            for (var x = step / 2; x < image.Width; x += step)
            {
                var alpha = image.Pixels[(image.Height - 1 - y) * image.Stride + x * 4 + 3];
                if (alpha < 128) continue;
                var cell = (BotAtlasFrontiers.Cell(tile.WorldBounds.X + x), BotAtlasFrontiers.Cell(tile.WorldBounds.Y + y));
                if (cells.Add(cell) && credit) earned++;
            }
        }
        Snapshot = new(cells.ToImmutable(), earned, Snapshot.Revision + 1);
    }
}
