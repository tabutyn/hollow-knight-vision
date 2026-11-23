namespace HollowKnightVision.Windows.Core;

/// <summary>Quantized room pixels, with positive Y pointing up.</summary>
public readonly record struct AtlasPathPoint(int X, int Y) : IComparable<AtlasPathPoint>
{
    public static bool TryQuantize(double x, double y, out AtlasPathPoint point)
    {
        point = default;
        if (x is not (>= -1e8 and <= 1e8) || y is not (>= -1e8 and <= 1e8)) return false;
        point = new(Round(x), Round(y));
        return true;
    }

    internal static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    public int CompareTo(AtlasPathPoint other)
    {
        var horizontal = X.CompareTo(other.X);
        return horizontal == 0 ? Y.CompareTo(other.Y) : horizontal;
    }

    public int DistanceTo(AtlasPathPoint other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);
}

public readonly record struct AtlasPathPlatform(int Left, int Right, int Y)
{
    public bool Contains(int x) => Left <= x && x <= Right;
}

public readonly record struct AtlasPathWall(int X, int Low, int High, int SolidSide = 0, bool ConfirmedByContact = false);

// Manual connections use floor height. The graph draws all routes at Knight height above the floor.
public readonly record struct AtlasPathConnection(AtlasPathPoint First, AtlasPathPoint Second, bool DoubleJump);
public readonly record struct AtlasPathDoor(AtlasPathPoint First, AtlasPathPoint Second);

public enum AtlasPathKind { Walk, Jump, DoubleJump, Drop }

public readonly record struct AtlasPathStroke(AtlasPathPoint A, AtlasPathPoint B, AtlasPathKind Kind, bool Forward)
{
    public bool Horizontal => A.Y == B.Y;
    public AtlasPathPoint Closest(AtlasPathPoint point) =>
        new(Math.Clamp(point.X, A.X, B.X), Math.Clamp(point.Y, A.Y, B.Y));

    public bool Contains(AtlasPathPoint point) => Closest(point) == point;

    public bool AllowsTraversal(AtlasPathPoint from, AtlasPathPoint to) =>
        Contains(from) && Contains(to)
        && (Kind != AtlasPathKind.Drop || (from.CompareTo(to) <= 0) == Forward || from == to);

    internal bool Touches(AtlasPathStroke other) =>
        A.X <= other.B.X && other.A.X <= B.X && A.Y <= other.B.Y && other.A.Y <= B.Y;
}

public sealed class AtlasPlatformGraph
{
    internal const double JumpSpeed = 23;
    internal const double Gravity = 47.4;
    internal const double HorizontalSpeed = 8.3;

    private readonly Dictionary<AtlasPathStroke, IReadOnlyList<AtlasPathPoint>> intersections = new();

    public IReadOnlyList<AtlasPathPlatform> Floors { get; }
    public int KnightHeight { get; }
    public int KnightWidth { get; }
    public IReadOnlyList<AtlasPathStroke> Strokes { get; }
    public IReadOnlyDictionary<AtlasPathPoint, IReadOnlyList<AtlasPathPoint>> Nodes { get; }
    public IReadOnlySet<AtlasPathPoint> WalkNodes { get; }

    public AtlasPathPoint? UpperRightObjective
    {
        get
        {
            if (Floors.Count == 0) return null;
            var top = Floors.Max(floor => floor.Y);
            var tier = Math.Max(2, KnightHeight / 4);
            var goal = Floors.Where(floor => top - floor.Y <= tier).MaxBy(floor => (floor.Right, floor.Y));
            return new(goal.Right, goal.Y + KnightHeight);
        }
    }

    public AtlasPlatformGraph(
        IReadOnlyList<AtlasPathPlatform> floors, IReadOnlyList<AtlasPathWall> walls,
        int height, int width, IReadOnlyList<AtlasPathConnection>? connections = null,
        AtlasPoint? pixelsPerWorldUnit = null, AtlasPlatformGraph? previous = null,
        IReadOnlyList<AtlasPathPlatform>? spikes = null, IReadOnlyList<AtlasPathDoor>? doors = null,
        bool detectedGround = false, IReadOnlyList<AtlasPathPlatform>? walked = null,
        IReadOnlyList<AtlasPathPlatform>? handoffSeams = null)
    {
        KnightHeight = Math.Max(4, height);
        KnightWidth = Math.Max(2, width);
        spikes ??= [];
        walked ??= [];
        var scale = pixelsPerWorldUnit is { X: > 0, Y: > 0 } measured
                    && double.IsFinite(measured.X) && double.IsFinite(measured.Y)
            ? measured : new(KnightHeight / 1.8, KnightHeight / 1.8);
        Floors = AtlasPathFloorGeometry.Prepare(floors, walls, spikes, walked, handoffSeams ?? [],
            KnightHeight, KnightWidth, detectedGround);
        var strokes = new List<AtlasPathStroke>();
        var seen = new HashSet<AtlasPathStroke>();
        void Add(AtlasPathPoint from, AtlasPathPoint to, AtlasPathKind kind)
        {
            if (from == to) return;
            var forward = from.CompareTo(to) < 0;
            var stroke = forward ? new AtlasPathStroke(from, to, kind, true)
                : new AtlasPathStroke(to, from, kind, kind != AtlasPathKind.Drop);
            if (seen.Add(stroke)) strokes.Add(stroke);
        }

        foreach (var floor in Floors)
            Add(new(floor.Left, floor.Y + KnightHeight), new(floor.Right, floor.Y + KnightHeight), AtlasPathKind.Walk);
        var jumps = AddJumps(walls, connections ?? [], scale, Add);
        new AtlasDropRoutes(Floors, walls, spikes, doors ?? [], KnightHeight, KnightWidth, scale).Add(jumps, Add);
        Strokes = strokes;

        var adjacency = new Dictionary<AtlasPathPoint, HashSet<AtlasPathPoint>>();
        var walk = new HashSet<AtlasPathPoint>();
        var old = previous?.KnightHeight == KnightHeight ? previous.intersections : null;
        var changes = strokes.ToHashSet();
        if (old is not null) changes.SymmetricExceptWith(old.Keys);
        foreach (var stroke in strokes)
        {
            var points = old is not null && old.TryGetValue(stroke, out var oldPoints) && !changes.Any(stroke.Touches)
                ? oldPoints : FindIntersections(stroke, strokes);
            intersections[stroke] = points;
            if (stroke.Kind == AtlasPathKind.Walk) walk.UnionWith(points);
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                if (stroke.AllowsTraversal(a, b)) Connect(a, b);
                if (stroke.AllowsTraversal(b, a)) Connect(b, a);
            }
        }
        Nodes = adjacency.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<AtlasPathPoint>)pair.Value.Order().ToArray());
        WalkNodes = walk;

        void Connect(AtlasPathPoint from, AtlasPathPoint to)
        {
            if (!adjacency.TryGetValue(from, out var points)) adjacency[from] = points = [];
            points.Add(to);
        }
    }

    public IReadOnlyList<AtlasPathPoint> Vertices(AtlasPathStroke stroke) =>
        intersections.TryGetValue(stroke, out var points) ? points : [];

    public AtlasPathStroke? NearestStroke(AtlasPathPoint point, int radius, bool walkOnly = false)
    {
        AtlasPathStroke? nearest = null;
        var distance = radius;
        foreach (var stroke in Strokes)
        {
            if (walkOnly && stroke.Kind != AtlasPathKind.Walk) continue;
            var candidate = stroke.Closest(point).DistanceTo(point);
            if (candidate > distance || (candidate == distance && nearest.HasValue)) continue;
            distance = candidate;
            nearest = stroke;
        }
        return nearest;
    }

    private HashSet<(int First, int Second)> AddJumps(IReadOnlyList<AtlasPathWall> walls,
        IReadOnlyList<AtlasPathConnection> connections, AtlasPoint scale,
        Action<AtlasPathPoint, AtlasPathPoint, AtlasPathKind> add)
    {
        var height = KnightHeight;
        var width = KnightWidth;
        var manualPairs = new HashSet<(int, int)>();
        var jumpPairs = new HashSet<(int, int)>();

        foreach (var connection in connections)
        {
            var a = Attach(connection.First);
            var b = Attach(connection.Second);
            if (a < 0 || b < 0 || a == b) continue;
            var pair = (Math.Min(a, b), Math.Max(a, b));
            manualPairs.Add(pair);
            jumpPairs.Add(pair);
            AddShelf(new(connection.First.X, Floors[a].Y + height), new(connection.Second.X, Floors[b].Y + height),
                connection.DoubleJump ? AtlasPathKind.DoubleJump : AtlasPathKind.Jump);
        }

        // Measured fresh-save movement in world units, shared with the macOS graph.
        var maximumGap = HorizontalSpeed * (2 * JumpSpeed / Gravity) * scale.X;
        for (var i = 0; i < Floors.Count; i++)
        for (var j = i + 1; j < Floors.Count; j++)
        {
            var a = Floors[i];
            var b = Floors[j];
            var gap = Math.Max(0, b.Left - a.Right);
            var rise = Math.Abs(a.Y - b.Y);
            if (gap > maximumGap) break;
            if (manualPairs.Contains((i, j)) || rise / scale.Y > JumpSpeed * JumpSpeed / (2 * Gravity)
                || (gap == 0 && rise == 0)) continue;

            var flight = (JumpSpeed + Math.Sqrt(Math.Max(0, JumpSpeed * JumpSpeed - 2 * Gravity * rise / scale.Y))) / Gravity;
            foreach (var (ax, bx) in JumpApproaches(a, b, width))
            {
                var from = new AtlasPathPoint(ax, a.Y + height);
                var to = new AtlasPathPoint(bx, b.Y + height);
                var top = ShelfHeight(from, to);
                if (Math.Abs(bx - ax) / scale.X > HorizontalSpeed * flight - .25
                    || !JumpClearsWalls(walls, a.Y, b.Y, ax, bx, flight, scale)) continue;
                var left = Math.Min(ax, bx);
                var right = Math.Max(ax, bx);
                var blocked = false;
                for (var k = 0; k < Floors.Count; k++)
                {
                    if (k == i || k == j) continue;
                    var other = Floors[k];
                    blocked = (other.Contains(ax) && other.Y > a.Y && other.Y <= top)
                        || (other.Contains(bx) && other.Y > b.Y && other.Y <= top)
                        || (other.Right > left && other.Left < right && other.Y >= top - height / 2 && other.Y <= top)
                        || (gap > 0 && other.Left >= a.Right && other.Right <= b.Left && Math.Abs(other.Y - Math.Max(a.Y, b.Y)) <= height);
                    if (blocked) break;
                }
                if (blocked) continue;
                AddShelf(from, to, AtlasPathKind.Jump);
                jumpPairs.Add((i, j));
            }
        }
        return jumpPairs;

        int Attach(AtlasPathPoint point)
        {
            var best = Math.Max(4, height / 3) + 1;
            var index = -1;
            for (var i = 0; i < Floors.Count; i++)
            {
                var distance = Math.Abs(Floors[i].Y - point.Y);
                if (distance >= best || !Floors[i].Contains(point.X)) continue;
                best = distance;
                index = i;
            }
            return index;
        }

        void AddShelf(AtlasPathPoint a, AtlasPathPoint b, AtlasPathKind kind)
        {
            var top = ShelfHeight(a, b);
            add(a, new(a.X, top), kind);
            add(new(a.X, top), new(b.X, top), kind);
            add(new(b.X, top), b, kind);
        }

        int ShelfHeight(AtlasPathPoint a, AtlasPathPoint b) => Math.Max(a.Y, b.Y) + Math.Max(2, height / 4);
    }

    private static IEnumerable<(int A, int B)> JumpApproaches(AtlasPathPlatform a, AtlasPathPlatform b, int width)
    {
        if (b.Left >= a.Right)
        {
            yield return (Math.Max(a.Left, a.Right - Math.Max(1, width / 2)),
                Math.Min(b.Right, b.Left + Math.Max(1, width / 2)));
            yield break;
        }

        // Overlapping ledges need a launch outside an exposed lip.
        var lower = a.Y < b.Y ? a : b;
        var upper = a.Y < b.Y ? b : a;
        var inside = Math.Min(width / 2, (upper.Right - upper.Left) / 2);
        foreach (var (launch, landing) in new[] { (upper.Left - width, upper.Left + inside),
                     (upper.Right + width, upper.Right - inside) })
            if (lower.Contains(launch))
                yield return a.Y < b.Y ? (launch, landing) : (landing, launch);
    }

    private bool JumpClearsWalls(IReadOnlyList<AtlasPathWall> walls, int ay, int by, int ax, int bx,
        double flight, AtlasPoint scale)
    {
        var left = Math.Min(ax, bx);
        var right = Math.Max(ax, bx);
        var barriers = walls.Where(wall => left <= wall.X && wall.X <= right).ToArray();
        var horizontalSpeed = HorizontalSpeed * scale.X;
        var horizontalTime = (right - left) / horizontalSpeed;
        if (barriers.Length == 0) return horizontalTime <= flight;
        var startX = ay <= by ? ax : bx;
        var direction = (ay <= by ? bx - ax : ax - bx) >= 0 ? 1 : -1;

        // Try delaying horizontal input until the Knight has cleared a nearby wall.
        for (var step = 0; step <= 50; step++)
        {
            var delay = step / 100.0;
            if (delay + horizontalTime > flight) break;
            if (barriers.All(wall =>
            {
                var time = delay + direction * (wall.X - startX) / horizontalSpeed;
                var foot = Math.Min(ay, by) + (JumpSpeed * time - Gravity * .5 * time * time) * scale.Y;
                return foot >= wall.High - 1 || foot + KnightHeight <= wall.Low + 1;
            })) return true;
        }
        return false;
    }

    private IReadOnlyList<AtlasPathPoint> FindIntersections(AtlasPathStroke stroke, IReadOnlyList<AtlasPathStroke> strokes)
    {
        var points = new HashSet<AtlasPathPoint> { stroke.A, stroke.B };
        if (stroke.Kind == AtlasPathKind.Walk) WalkCuts(stroke.A.X, stroke.B.X);
        foreach (var other in strokes)
        {
            if (other == stroke || !stroke.Touches(other)) continue;
            if (stroke.Kind == AtlasPathKind.Drop || other.Kind == AtlasPathKind.Drop)
            {
                var drop = stroke.Kind == AtlasPathKind.Drop ? stroke : other;
                var crossing = stroke.Kind == AtlasPathKind.Drop ? other : stroke;
                // Midair crossings never permit transfer onto a jump shelf.
                if (crossing.Kind == AtlasPathKind.Walk) AddEndpoints(drop, crossing);
                else if (crossing.Kind == AtlasPathKind.Drop)
                {
                    AddEndpoints(drop, crossing);
                    AddEndpoints(crossing, drop);
                }
                continue;
            }

            if (stroke.Kind != AtlasPathKind.Walk && other.Kind != AtlasPathKind.Walk)
            {
                if (other.A == stroke.A || other.A == stroke.B) points.Add(other.A);
                if (other.B == stroke.A || other.B == stroke.B) points.Add(other.B);
            }
            else if (stroke.Horizontal != other.Horizontal)
            {
                points.Add(stroke.Horizontal ? new(other.A.X, stroke.A.Y) : new(stroke.A.X, other.A.Y));
            }
            else
            {
                AddEndpoints(other, stroke);
                if (other.Kind == AtlasPathKind.Walk)
                    WalkCuts(Math.Max(other.A.X, stroke.A.X), Math.Min(other.B.X, stroke.B.X));
            }
        }
        return points.Order().ToArray();

        void AddEndpoints(AtlasPathStroke ends, AtlasPathStroke containing)
        {
            if (containing.Contains(ends.A)) points.Add(ends.A);
            if (containing.Contains(ends.B)) points.Add(ends.B);
        }

        void WalkCuts(int left, int right)
        {
            var step = Math.Max(KnightHeight, 8);
            var first = (int)Math.Ceiling((double)left / step) * step;
            for (var x = first; x <= right; x += step) points.Add(new(x, stroke.A.Y));
        }
    }
}
