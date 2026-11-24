using System.Globalization;

namespace HollowKnightVision.Windows.Core;

/// <summary>Relates a measured wall contact to a capture through the current Atlas placements.</summary>
public sealed class BotAtlasBlockadeProjection
{
    private readonly LiveWorldSnapshot graph;
    private readonly LiveTrackingIsland island;
    private readonly bool placed;

    private BotAtlasBlockadeProjection(LiveWorldSnapshot graph, LiveTrackingIsland island, bool placed)
    {
        this.graph = graph;
        this.island = island;
        this.placed = placed;
        Space = FormattableString.Invariant($"visual/{island.RoomId}/{island.Id}/{BitConverter.DoubleToUInt64Bits(island.StartedAt)}");
    }

    internal string Space { get; }

    public static BotAtlasBlockadeProjection? Create(LiveWorldSnapshot graph, int room,
        LiveIslandPlacementProjection? projection)
    {
        if (projection is not { } owner) return null;
        var island = graph.TrackingIslands.FirstOrDefault(candidate => candidate.Id == owner.IslandId
            && candidate.RoomId == room && candidate.WorldFromLocal == owner.WorldFromLocal);
        return island is null ? null : new(graph, island, owner.IsPlaced && !island.RequiresPlacement);
    }

    internal BotBlockadePose Pose(AtlasPoint localOrigin, int width) =>
        new(Space, localOrigin.X, localOrigin.Y, width);

    internal AtlasPoint? OffsetFrom(string source)
    {
        if (source == Space) return default(AtlasPoint);
        if (!placed) return null;
        var parts = source.Split('/');
        if (parts.Length != 4 || parts[0] != "visual"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var room) || room != island.RoomId
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !ulong.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var started)) return null;
        var owner = graph.TrackingIslands.FirstOrDefault(candidate => candidate.Id == id && candidate.RoomId == room
            && BitConverter.DoubleToUInt64Bits(candidate.StartedAt) == started && !candidate.RequiresPlacement);
        return owner is null ? null
            : new(owner.WorldFromLocal.X - island.WorldFromLocal.X, owner.WorldFromLocal.Y - island.WorldFromLocal.Y);
    }
}
