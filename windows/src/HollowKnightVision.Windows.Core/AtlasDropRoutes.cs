namespace HollowKnightVision.Windows.Core;

internal sealed class AtlasDropRoutes(
    IReadOnlyList<AtlasPathPlatform> floors, IReadOnlyList<AtlasPathWall> walls,
    IReadOnlyList<AtlasPathPlatform> spikes, IReadOnlyList<AtlasPathDoor> doors,
    int height, int width, AtlasPoint scale)
{
    internal void Add(IReadOnlySet<(int, int)> jumpPairs, Action<AtlasPathPoint, AtlasPathPoint, AtlasPathKind> add)
    {
        for (var i = 0; i < floors.Count; i++)
        {
            var upper = floors[i];
            foreach (var (lip, side) in new[] { (upper.Left, -1), (upper.Right, 1) })
            {
                var x = lip + side * Math.Max(1, (width + 1) / 2);
                if (BlocksLip(i, lip, x)) continue;
                for (var j = 0; j < floors.Count; j++)
                {
                    if (floors[j].Y >= upper.Y || jumpPairs.Contains((Math.Min(i, j), Math.Max(i, j)))) continue;
                    var flight = FindLanding(i, j, x, side);
                    if (flight is null) continue;

                    var previous = new AtlasPathPoint(x, upper.Y + height);
                    add(new(lip, previous.Y), previous, AtlasPathKind.Drop);
                    foreach (var point in flight)
                    {
                        var destination = new AtlasPathPoint(point.X, point.Y + height);
                        var corner = new AtlasPathPoint(previous.X, destination.Y);
                        add(previous, corner, AtlasPathKind.Drop);
                        add(corner, destination, AtlasPathKind.Drop);
                        previous = destination;
                    }
                }
            }
        }
    }

    private bool BlocksLip(int index, int lip, int x)
    {
        var upper = floors[index];
        var left = Math.Min(lip, x);
        var right = Math.Max(lip, x);
        var tolerance = Math.Clamp(height / 5, 2, 6);
        if (doors.Any(door => door.First.X == door.Second.X
                ? Math.Abs(door.First.X - lip) <= Math.Max(2, width)
                    && Math.Min(door.First.Y, door.Second.Y) <= upper.Y + height
                    && Math.Max(door.First.Y, door.Second.Y) >= upper.Y
                : Math.Abs(door.First.Y - upper.Y) <= Math.Max(2, height / 5)
                    && Math.Min(door.First.X, door.Second.X) <= lip + width
                    && Math.Max(door.First.X, door.Second.X) >= lip - width)) return true;
        if (spikes.Any(spike => Math.Abs(spike.Y - upper.Y) <= tolerance
                && spike.Left <= right + width / 2 && spike.Right >= left - width / 2)) return true;
        if (walls.Any(wall => wall.X > left - width / 2 && wall.X < right + width / 2
                && wall.High > upper.Y && wall.Low < upper.Y + height)) return true;

        for (var j = 0; j < floors.Count; j++)
        {
            if (j == index) continue;
            var other = floors[j];
            if (other.Y == upper.Y && other.Contains(x)) return true;
            if (other.Y > upper.Y && other.Y < upper.Y + height && other.Left <= right && other.Right >= left)
                return true;
        }
        return false;
    }

    private List<AtlasPathPoint>? FindLanding(int launch, int landing, int x, int side)
    {
        var upper = floors[launch];
        var lower = floors[landing];
        double Time(int y) => Math.Sqrt(2 * (upper.Y - y) / (AtlasPlatformGraph.Gravity * scale.Y));
        var duration = Time(lower.Y);
        var speed = AtlasPlatformGraph.HorizontalSpeed * scale.X;
        var half = width / 2.0;
        var overlap = Math.Max(1, (width - 1) / 2);
        var nearest = Math.Clamp(x, lower.Left - overlap, lower.Right + overlap);
        if (Math.Abs(nearest - x) > speed * duration) return null;

        var intermediate = floors.Where(floor => lower.Y < floor.Y && floor.Y < upper.Y).ToArray();
        var landings = new[] { nearest, lower.Left - overlap, lower.Right + overlap, lower.Left, lower.Right,
            lower.Left + (lower.Right - lower.Left) / 2 }.Distinct().OrderBy(destination => Math.Abs(destination - x));
        var waypoints = new List<AtlasPathPoint?> { null };
        var exitClearance = (width + 1) / 2;

        // A waypoint lets a fall skirt an intervening platform or descend below a wall before drifting back.
        foreach (var floor in intermediate)
        foreach (var clearance in new[] { Math.Max(2, height / 4), 1 })
        {
            var y = floor.Y + clearance;
            if (y >= upper.Y) continue;
            OfferWaypoint(floor.Left - exitClearance - 1, y);
            OfferWaypoint(floor.Right + exitClearance + 1, y);
        }
        foreach (var wall in walls)
        {
            var y = wall.Low - height - 1;
            if (y <= lower.Y || y >= upper.Y) continue;
            OfferWaypoint(wall.X - exitClearance, y);
            OfferWaypoint(wall.X + exitClearance, y);
        }

        foreach (var destination in landings)
        foreach (var waypoint in waypoints)
        {
            var midTime = waypoint is { } turn ? Time(turn.Y) : duration;
            var midX = waypoint?.X ?? destination;
            if (Math.Abs(midX - x) > speed * midTime
                || Math.Abs(destination - midX) > speed * (duration - midTime) + .001) continue;

            double Position(double time) => time <= midTime || midTime == duration
                ? x + (midX - x) * time / midTime
                : midX + (destination - midX) * (time - midTime) / (duration - midTime);

            var exitTime = Time(Math.Max(lower.Y, upper.Y - height));
            var exit = Position(exitTime);
            var lip = side < 0 ? upper.Left - half : upper.Right + half;
            if (side < 0 ? Math.Max(x, exit) > lip || (midTime < exitTime && midX > lip)
                         : Math.Min(x, exit) < lip || (midTime < exitTime && midX < lip)) continue;
            if (intermediate.Any(floor => InsideFloor(floor, Position(Time(floor.Y))))) continue;
            if (walls.Any(IntersectsWall)) continue;
            if (spikes.Any(spike =>
            {
                if (spike.Y >= upper.Y || spike.Y < lower.Y) return false;
                var at = Position(Time(spike.Y));
                return at >= spike.Left - width / 2 && at <= spike.Right + width / 2;
            })) continue;

            var points = new List<AtlasPathPoint>();
            if (waypoint is { } first) points.Add(first);
            points.Add(new(destination, lower.Y));
            var diagram = DrawClearTurns(points, x, upper.Y, intermediate, Time, Position);
            var support = Math.Clamp(destination, lower.Left, lower.Right);
            if (support != destination) diagram.Add(new(support, lower.Y));
            var priorX = x;
            var clear = true;
            foreach (var point in diagram)
            {
                if (!TurnClearsSolids(priorX, point.X, point.Y))
                {
                    clear = false;
                    break;
                }
                priorX = point.X;
            }
            if (clear) return diagram;

            bool IntersectsWall(AtlasPathWall wall)
            {
                // Landing on a wall cap is permitted when only the feet meet it.
                if (wall.High <= lower.Y || wall.Low >= upper.Y + height) return false;
                var top = Math.Min(upper.Y, wall.High);
                var bottom = Math.Max(lower.Y, wall.Low - height);
                if (top < bottom) return false;
                var first = Time(top);
                var last = Time(bottom);
                var atTop = Position(first);
                var atBottom = Position(last);
                var left = Math.Min(atTop, atBottom);
                var right = Math.Max(atTop, atBottom);
                if (first < midTime && midTime < last)
                {
                    left = Math.Min(left, midX);
                    right = Math.Max(right, midX);
                }
                return wall.X > left - half && wall.X < right + half;
            }
        }
        return null;

        void OfferWaypoint(int waypointX, int y)
        {
            if (Math.Abs(waypointX - x) <= speed * Time(y)) waypoints.Add(new AtlasPathPoint(waypointX, y));
        }
    }

    private List<AtlasPathPoint> DrawClearTurns(List<AtlasPathPoint> flight, int priorX, int priorY,
        AtlasPathPlatform[] intermediate, Func<int, double> time, Func<double, double> position)
    {
        var route = new List<AtlasPathPoint>();
        foreach (var point in flight)
        {
            // Keep the orthogonal drawing on the same side of obstacles as the flight.
            var levels = intermediate.Select(floor => floor.Y + 1)
                .Concat(walls.Select(wall => wall.High + 1))
                .Where(y => point.Y < y && y < priorY).Distinct().OrderDescending().ToArray();
            var currentY = priorY;
            foreach (var y in levels)
            {
                var floorInColumn = intermediate.Any(floor => floor.Y + 1 == y && InsideFloor(floor, priorX));
                var wallInColumn = walls.Any(wall => wall.High + 1 == y && Math.Abs(wall.X - priorX) * 2 < width);
                if (!floorInColumn && !wallInColumn) continue;
                var turnY = y;
                var raised = Math.Min(y + Math.Max(2, height / 4) - 1, currentY - 1);
                if (floorInColumn && raised > y && TurnClearsSolids(priorX, XAt(raised), raised)) turnY = raised;
                priorX = XAt(turnY);
                currentY = turnY;
                route.Add(new(priorX, turnY));
            }
            route.Add(point);
            priorX = point.X;
            priorY = point.Y;
        }
        return route;

        int XAt(int y) => (int)Math.Round(position(time(y)), MidpointRounding.AwayFromZero);
    }

    private bool InsideFloor(AtlasPathPlatform floor, double x) =>
        x > floor.Left - width / 2.0 && x < floor.Right + width / 2.0;

    private bool TurnClearsSolids(int a, int b, int y)
    {
        var left = Math.Min(a, b) - width / 2.0;
        var right = Math.Max(a, b) + width / 2.0;
        return !walls.Any(wall => left < wall.X && wall.X < right && wall.High > y && wall.Low < y + height)
            && !floors.Any(floor => floor.Y > y && floor.Y < y + height && floor.Right > left && floor.Left < right)
            && !spikes.Any(spike => spike.Y >= y && spike.Y < y + height && spike.Right >= left && spike.Left <= right);
    }
}
