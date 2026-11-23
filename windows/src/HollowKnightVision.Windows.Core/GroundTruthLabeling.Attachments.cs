namespace HollowKnightVision.Windows.Core;

internal readonly record struct TerrainHandle(Guid LineId, TerrainLineEnd End)
{
    internal TerrainHandle Opposite => new(LineId, GroundTruthLineDocument.Opposite(End));
}

public sealed partial class GroundTruthLineDocument
{
    internal static readonly TerrainLineEnd[] Ends = [TerrainLineEnd.Start, TerrainLineEnd.End];

    internal static TerrainLineEnd Opposite(TerrainLineEnd end) =>
        end == TerrainLineEnd.Start ? TerrainLineEnd.End : TerrainLineEnd.Start;

    internal static AtlasPoint Components(AtlasPoint point, bool vertical) =>
        vertical ? new(point.Y, point.X) : point;

    internal GroundTruthWorldLine? FindLine(Guid id) => AllLines.FirstOrDefault(line => line.Id == id);

    private List<GroundTruthWorldLine>? LineStorage(GroundTruthLineKind kind) => kind switch
    {
        GroundTruthLineKind.Wall => Walls,
        GroundTruthLineKind.Spike => Spikes,
        GroundTruthLineKind.Door or GroundTruthLineKind.DoorVertical => Doors,
        _ => Lines
    };

    internal void ReplaceLine(GroundTruthWorldLine line)
    {
        var storage = LineStorage(line.Kind);
        if (storage is not null && storage.FindIndex(item => item.Id == line.Id) is var index && index >= 0)
            storage[index] = line;
    }

    internal void RemoveLine(GroundTruthWorldLine line)
    {
        LineStorage(line.Kind)?.RemoveAll(item => item.Id == line.Id);
        PruneCorners(line);
    }

    internal static double DistanceInTolerance(AtlasPoint a, AtlasPoint b, AtlasPoint tolerance)
    {
        var dx = (a.X - b.X) / tolerance.X;
        var dy = (a.Y - b.Y) / tolerance.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal TerrainHandle? NearestCorner(AtlasPoint point, bool vertical, string scene, AtlasPoint tolerance,
        TerrainHandle? excluding = null)
    {
        TerrainHandle? best = null;
        var distance = double.PositiveInfinity;
        var oppositeAxis = vertical ? Lines : Walls ?? [];
        foreach (var line in oppositeAxis)
        {
            if (line.SceneName != scene || line.SeededFromDetector || line.Kind == GroundTruthLineKind.Negative) continue;
            foreach (var end in Ends)
            {
                var handle = new TerrainHandle(line.Id, end);
                if (handle == excluding) continue;
                var candidateDistance = DistanceInTolerance(point, line.PointAt(end), tolerance);
                if (candidateDistance > 1 || candidateDistance >= distance) continue;
                best = handle;
                distance = candidateDistance;
            }
        }
        return best;
    }

    // Propagate just the dragged coordinate. A floor shares Y and a wall
    // shares X between its own handles, while corner peers share both.
    internal void Move(TerrainHandle handle, bool verticalCoordinate, double value)
    {
        if (FindLine(handle.LineId) is not { } owner
            || Components(owner.PointAt(handle.End), verticalCoordinate).X == value) return;
        var byId = AuthoredLines.Where(line => line.SceneName == owner.SceneName).ToDictionary(line => line.Id);
        var corners = new Dictionary<Guid, List<TerrainHandle>>();
        foreach (var line in byId.Values)
        foreach (var end in Ends)
        {
            if (line.CornerAt(end) is not { } corner) continue;
            if (!corners.TryGetValue(corner, out var peers)) corners[corner] = peers = [];
            peers.Add(new(line.Id, end));
        }

        var moving = new HashSet<TerrainHandle>();
        var pending = new Stack<TerrainHandle>();
        pending.Push(handle);
        while (pending.TryPop(out var node))
        {
            if (!moving.Add(node) || !byId.TryGetValue(node.LineId, out var line)) continue;
            if (line.IsVertical != verticalCoordinate) pending.Push(node.Opposite);
            if (line.CornerAt(node.End) is { } id && corners.Remove(id, out var peers))
                foreach (var peer in peers) pending.Push(peer);
        }

        var position = value;
        foreach (var node in moving)
        {
            var line = byId[node.LineId];
            if (line.IsVertical != verticalCoordinate || moving.Contains(node.Opposite)) continue;
            var bound = Components(line.PointAt(node.Opposite.End), verticalCoordinate).X;
            position = node.End == TerrainLineEnd.Start ? Math.Min(bound, position) : Math.Max(bound, position);
        }

        foreach (var id in moving.Select(node => node.LineId).Distinct())
        {
            var line = byId[id];
            var first = Components(line.Start, line.IsVertical);
            var last = Components(line.End, line.IsVertical);
            var changed = line.IsVertical == verticalCoordinate
                ? line.WithExtent(moving.Contains(new(id, TerrainLineEnd.Start)) ? position : first.X,
                    moving.Contains(new(id, TerrainLineEnd.End)) ? position : last.X, first.Y)
                : line.WithExtent(first.X, last.X, position);
            if (changed != line) ReplaceLine(changed);
        }
    }

    internal void Join(TerrainHandle incoming, TerrainHandle target, bool keepTarget = false)
    {
        if (FindLine(incoming.LineId) is not { } source || FindLine(target.LineId) is not { } other
            || source.SceneName != other.SceneName || source.IsVertical == other.IsVertical
            || source.CornerAt(incoming.End) is not null) return;

        var point = keepTarget ? other.PointAt(target.End) : source.PointAt(incoming.End);
        var moving = keepTarget ? incoming : target;
        var joined = Clone();
        joined.Move(moving, verticalCoordinate: false, point.X);
        joined.Move(moving, verticalCoordinate: true, point.Y);
        var updated = joined.FindLine(incoming.LineId)!;
        var adjusted = joined.FindLine(target.LineId)!;
        if (updated.PointAt(incoming.End) != point || adjusted.PointAt(target.End) != point) return;

        var corner = adjusted.CornerAt(target.End) ?? Guid.NewGuid();
        joined.ReplaceLine(updated.WithCorner(incoming.End, corner));
        joined.ReplaceLine(adjusted.WithCorner(target.End, corner));
        Lines = joined.Lines;
        Walls = joined.Walls;
    }

    internal void PruneCorners(GroundTruthWorldLine removed)
    {
        foreach (var corner in new[] { removed.StartCornerId, removed.EndCornerId }.Distinct())
        {
            if (corner is null) continue;
            var members = AuthoredLines.Where(line => line.StartCornerId == corner || line.EndCornerId == corner).ToArray();
            if (members.Any(line => line.Kind == GroundTruthLineKind.Positive)
                && members.Any(line => line.Kind == GroundTruthLineKind.Wall)) continue;
            foreach (var line in members)
                ReplaceLine(line with
                {
                    StartCornerId = line.StartCornerId == corner ? null : line.StartCornerId,
                    EndCornerId = line.EndCornerId == corner ? null : line.EndCornerId
                });
        }
    }

    internal GroundTruthDoorLatch? NearestGroundLatch(GroundTruthWorldLine door,
        IReadOnlyList<TerrainLineEnd> ends, AtlasPoint tolerance)
    {
        GroundTruthDoorLatch? best = null;
        var distance = double.PositiveInfinity;
        foreach (var floor in Lines)
        {
            if (floor.Kind != GroundTruthLineKind.Positive || floor.SeededFromDetector
                || floor.SceneName != door.SceneName || floor.End.X <= floor.Start.X) continue;
            foreach (var end in ends)
            {
                var p = door.PointAt(end);
                var x = Math.Clamp(p.X, floor.Start.X, floor.End.X);
                var tip = Math.Abs(p.X - floor.Start.X) <= Math.Abs(p.X - floor.End.X) ? floor.Start : floor.End;
                if (DistanceInTolerance(p, tip, tolerance) <= 1) x = tip.X;
                var candidateDistance = DistanceInTolerance(p, new(x, floor.WorldY), tolerance);
                if (candidateDistance > 1 || candidateDistance >= distance) continue;
                best = new(floor.Id, end, (x - floor.Start.X) / (floor.End.X - floor.Start.X));
                distance = candidateDistance;
            }
        }
        return best;
    }

    internal void UpdateLatchedDoors()
    {
        if (Doors is not { Count: > 0 } doors) return;
        var ground = Lines.Where(line => line.Kind == GroundTruthLineKind.Positive && !line.SeededFromDetector)
            .ToDictionary(line => line.Id);
        for (var i = 0; i < doors.Count; i++)
        {
            var door = doors[i];
            if (door.GroundLatch is not { } latch) continue;
            if (!ground.TryGetValue(latch.GroundId, out var floor) || floor.SceneName != door.SceneName)
            {
                doors[i] = door with { GroundLatch = null };
                continue;
            }
            var point = door.PointAt(latch.End);
            var x = floor.Start.X + (floor.End.X - floor.Start.X) * latch.Fraction;
            var delta = Components(new(x - point.X, floor.WorldY - point.Y), door.IsVertical);
            var first = Components(door.Start, door.IsVertical);
            var last = Components(door.End, door.IsVertical);
            doors[i] = door.WithExtent(first.X + delta.X, last.X + delta.X, first.Y + delta.Y);
        }
    }

    private void ValidateAttachments()
    {
        var groundById = Lines.Where(line => line.Kind == GroundTruthLineKind.Positive && !line.SeededFromDetector)
            .ToDictionary(line => line.Id);
        var locations = new Dictionary<Guid, (string Scene, AtlasPoint Point)>();
        var groundCorners = new HashSet<Guid>();
        var wallCorners = new HashSet<Guid>();
        foreach (var line in AllLines)
        {
            if (line.GroundLatch is { } latch && (line.Kind is not (GroundTruthLineKind.Door or GroundTruthLineKind.DoorVertical)
                || !Enum.IsDefined(latch.End) || !double.IsFinite(latch.Fraction) || latch.Fraction is < 0 or > 1
                || !groundById.TryGetValue(latch.GroundId, out var floor) || floor.SceneName != line.SceneName))
                throw new GroundTruthLineStoreException("Invalid door attachment");
            foreach (var end in Ends)
            {
                if (line.CornerAt(end) is not { } corner) continue;
                var location = (line.SceneName, line.PointAt(end));
                if (corner == Guid.Empty || line.SeededFromDetector
                    || locations.TryGetValue(corner, out var existing) && existing != location)
                    throw new GroundTruthLineStoreException("Disconnected terrain corner");
                locations[corner] = location;
                if (line.Kind == GroundTruthLineKind.Positive) groundCorners.Add(corner);
                else if (line.Kind == GroundTruthLineKind.Wall) wallCorners.Add(corner);
                else throw new GroundTruthLineStoreException("Invalid terrain corner member");
            }
        }
        if (!groundCorners.SetEquals(wallCorners))
            throw new GroundTruthLineStoreException("Incomplete terrain corner");
    }
}
