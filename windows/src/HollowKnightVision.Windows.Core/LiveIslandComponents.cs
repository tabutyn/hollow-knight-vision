namespace HollowKnightVision.Windows.Core;

/// <summary>Rigid Atlas components backed by image links and axis-specific doorway evidence.</summary>
internal sealed class LiveIslandComponents
{
    internal sealed record LinkPlan(
        IReadOnlyDictionary<int, AtlasPoint> Shifts, IReadOnlySet<int> PlacedIslands);

    private sealed record Group(IReadOnlyList<int> Islands, bool Fixed);
    private sealed record Join(Group From, Group To, Group? Moving, double Shift, double Unresolved)
    {
        internal double ShiftFor(int island) => Moving?.Islands.Contains(island) == true ? Shift : 0;
        internal IEnumerable<int> Placed() => From.Fixed || To.Fixed ? From.Islands.Union(To.Islands) : [];
    }

    private readonly Dictionary<int, LiveTrackingIsland> owners;
    private readonly Dictionary<int, Group> horizontal;
    private readonly Dictionary<int, Group> vertical;

    internal LiveIslandComponents(LiveWorldSnapshot snapshot, IReadOnlySet<int>? recoveredDoorwayAnchors = null)
    {
        owners = snapshot.TrackingIslands.SelectMany(island =>
            island.Members.Select(member => (member.ObservationId, Island: island)))
            .ToDictionary(pair => pair.ObservationId, pair => pair.Island);
        var keyframes = snapshot.Keyframes.ToDictionary(keyframe => keyframe.Id);
        var visual = new List<(int From, int To)>();
        foreach (var link in snapshot.LoopClosureEdges)
        {
            if (Owner(keyframes[link.FromKeyframeId].ObservationId) is { } from
                && Owner(keyframes[link.ToKeyframeId].ObservationId) is { } to && from.Id != to.Id
                && (recoveredDoorwayAnchors is null
                    || from.Placement != LiveIslandPlacement.LegacyUnknown && to.Placement != LiveIslandPlacement.LegacyUnknown))
                visual.Add((from.Id, to.Id));
        }
        // Visual recovery treats already composed images as fixed. Doorway
        // evidence may refine the height of an estimated component.
        var fixedIds = snapshot.TrackingIslands.Where(island => recoveredDoorwayAnchors is null
            ? !island.RequiresPlacement
            : island.Placement is LiveIslandPlacement.Reference or LiveIslandPlacement.Connected
                || island.Members.Any(member => recoveredDoorwayAnchors.Contains(member.ObservationId)))
            .Select(island => island.Id).ToHashSet();
        horizontal = Groups(snapshot.TrackingIslands, visual, fixedIds);
        vertical = Groups(snapshot.TrackingIslands, visual.Concat(snapshot.DoorwayFloorConstraints.Select(link =>
            (owners[link.DepartureObservationId].Id, owners[link.ArrivalObservationId].Id))), fixedIds);
    }

    internal LiveTrackingIsland? Owner(int observation) => owners.GetValueOrDefault(observation);

    internal bool SameRigidComponent(int first, int second) =>
        ReferenceEquals(horizontal[first], horizontal[second]) && ReferenceEquals(vertical[first], vertical[second]);

    internal LinkPlan? Plan(int from, int to, AtlasPoint correction, double tolerance)
    {
        var x = JoinAxis(horizontal[from], horizontal[to], correction.X);
        var y = JoinAxis(vertical[from], vertical[to], correction.Y);
        if (x.Unresolved * x.Unresolved + y.Unresolved * y.Unresolved > tolerance * tolerance)
            return null;

        var shifts = new Dictionary<int, AtlasPoint>();
        foreach (var island in (x.Moving?.Islands ?? []).Union(y.Moving?.Islands ?? []))
            shifts.Add(island, new AtlasPoint(x.ShiftFor(island), y.ShiftFor(island)));
        return new(shifts, x.Placed().Intersect(y.Placed()).ToHashSet());
    }

    internal IReadOnlyDictionary<int, double>? PlanDoorwayElevation(int from, int to, double correction, double tolerance)
    {
        if (!double.IsFinite(correction)) return null;
        var height = JoinAxis(vertical[from], vertical[to], correction);
        if (Math.Abs(height.Unresolved) > tolerance) return null;
        return (height.Moving?.Islands ?? []).ToDictionary(id => id, height.ShiftFor);
    }

    private static Join JoinAxis(Group from, Group to, double delta)
    {
        if (ReferenceEquals(from, to) || from.Fixed && to.Fixed)
            return new(from, to, null, 0, delta);
        var moveTo = from.Fixed || !to.Fixed && from.Islands[0] < to.Islands[0];
        return new(from, to, moveTo ? to : from, moveTo ? delta : -delta, 0);
    }

    private static Dictionary<int, Group> Groups(IReadOnlyList<LiveTrackingIsland> islands,
        IEnumerable<(int From, int To)> links, HashSet<int> fixedIds)
    {
        var neighbors = islands.ToDictionary(island => island.Id, _ => new List<int>());
        foreach (var (from, to) in links)
        {
            neighbors[from].Add(to);
            neighbors[to].Add(from);
        }
        var byIsland = new Dictionary<int, Group>();
        var visited = new HashSet<int>();
        foreach (var island in islands)
        {
            if (!visited.Add(island.Id)) continue;
            var members = new List<int>();
            var pending = new Stack<int>();
            pending.Push(island.Id);
            while (pending.TryPop(out var id))
            {
                members.Add(id);
                foreach (var neighbor in neighbors[id])
                    if (visited.Add(neighbor)) pending.Push(neighbor);
            }
            members.Sort();
            var group = new Group(members, members.Any(fixedIds.Contains));
            foreach (var id in members) byIsland.Add(id, group);
        }
        return byIsland;
    }
}
