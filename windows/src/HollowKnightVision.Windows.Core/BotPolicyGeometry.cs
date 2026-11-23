namespace HollowKnightVision.Windows.Core;

internal static class BotPolicyGeometry
{
    private const int Columns = 24;
    private const int Rows = 14;
    internal const int Cells = Columns * Rows;
    internal const int MetadataCount = 9;
    internal const int SlotWidth = 6;
    internal const int LandingCount = 6;

    private enum Kind { Floor = 1, Wall, Spike, Door }
    private readonly record struct Segment(Kind Kind, double Ax, double Ay, double Bx, double By)
    {
        public double DistanceSquared(double x, double y)
        {
            var dx = Bx - Ax;
            var dy = By - Ay;
            var t = Math.Clamp(((x - Ax) * dx + (y - Ay) * dy) / (dx * dx + dy * dy), 0, 1);
            var ex = x - Ax - t * dx;
            var ey = y - Ay - t * dy;
            return ex * ex + ey * ey;
        }
    }

    public static double[] Raster(BotScreenTerrain? geometry, bool doors)
    {
        var result = new double[doors ? Cells : 2 * Cells];
        if (!ValidSize(geometry)) return result;
        var width = geometry!.Width;
        var height = geometry.Height;
        foreach (var s in Segments(geometry, raster: true))
        {
            if (doors != (s.Kind == Kind.Door) || s.Kind == Kind.Spike) continue;
            var col = Cell(s.Ax, width, Columns);
            var row = Cell(s.Ay, height, Rows);
            if (s.Ay == s.By)
            {
                var last = Cell(s.Bx, width, Columns);
                for (var x = col; x <= last; x++) result[row * Columns + x] = 1;
            }
            else
            {
                var offset = doors ? 0 : Cells;
                var last = Cell(s.By, height, Rows);
                for (var y = row; y <= last; y++) result[offset + y * Columns + col] = 1;
            }
        }
        return result;

        static int Cell(double value, double extent, int count) =>
            Math.Min(count - 1, (int)(Math.Clamp(value / extent, 0, 1) * count));
    }

    public static double[] Markings(BotScreenTerrain? geometry, double knightX, double knightY, int version)
    {
        var capacity = version == 13 ? 64 : 16;
        var result = new double[MetadataCount + capacity * SlotWidth];
        if (!ValidSize(geometry)) return result;
        var width = geometry!.Width;
        var kx = knightX * width;
        var ky = knightY * width;
        var ordered = Segments(geometry).Distinct()
            .OrderBy(s => (s.DistanceSquared(kx, ky), s.Kind, s.Ax, s.Ay, s.Bx, s.By)).ToArray();
        result[0] = result[1] = 1;
        result[2] = geometry.Spikes is null ? 0 : 1;
        result[3] = geometry.Doors is null ? 0 : 1;
        var counts = new int[4];
        foreach (var segment in ordered) counts[(int)segment.Kind - 1]++;
        for (var i = 0; i < counts.Length; i++) result[4 + i] = Math.Min(4, counts[i] / (double)capacity);
        result[8] = Math.Min(4, Math.Max(0, ordered.Length - capacity) / (double)capacity);
        var slot = MetadataCount;
        foreach (var s in ordered.Take(capacity))
        {
            result[slot++] = Bound((s.Ax - kx) / width);
            result[slot++] = Bound((s.Ay - ky) / width);
            result[slot++] = Bound((s.Bx - kx) / width);
            result[slot++] = Bound((s.By - ky) / width);
            result[slot++] = version == 13 ? (int)s.Kind / 4d : s.Kind switch
            {
                Kind.Floor => .33, Kind.Wall => .66, Kind.Door => 1, _ => 0
            };
            result[slot++] = 1;
        }
        return result;
    }

    public static double[] Landing(double[] markings, double height, bool? grounded, ReadOnlySpan<double> previous)
    {
        var result = new double[LandingCount];
        if (grounded != false || height <= .005) return result;
        var bestRank = double.PositiveInfinity;
        for (var i = MetadataCount; i < markings.Length; i += SlotWidth)
        {
            if (markings[i + 5] <= .5 || markings[i + 4] != .33 || markings[i + 1] != markings[i + 3])
                continue;
            var left = markings[i] / height;
            var right = markings[i + 2] / height;
            var clearance = -markings[i + 1] / height - 1;
            var gap = Math.Max(0, Math.Max(left, -right));
            if (right - left is < .35 or > 3 || clearance is < -.07 or > 2.75 || gap > 2.5) continue;
            var rank = clearance + .05 * gap;
            if (rank >= bestRank) continue;
            bestRank = rank;
            result[0] = 1;
            result[1] = Bound(left);
            result[2] = Bound(right);
            result[3] = clearance;
        }
        if (result[0] == 1 && previous.Length == LandingCount && previous[0] == 1
            && Math.Abs((result[2] - result[1]) - (previous[2] - previous[1])) <= .12
            && Math.Abs(result[3] - previous[3]) <= 1.5)
        {
            result[4] = Bound((previous[1] + previous[2] - result[1] - result[2]) / 2);
            result[5] = result[3] - previous[3];
        }
        return result;
    }

    private static IEnumerable<Segment> Segments(BotScreenTerrain terrain, bool raster = false)
    {
        foreach (var floor in terrain.Floors)
            if ((!raster || floor.MinimumX < floor.MaximumX) && Clip(Kind.Floor, floor.MinimumX, floor.Y, floor.MaximumX, floor.Y) is { } f) yield return f;
        foreach (var wall in terrain.Walls)
            if ((!raster || wall.BottomY < wall.TopY) && Clip(Kind.Wall, wall.X, wall.BottomY, wall.X, wall.TopY) is { } w) yield return w;
        foreach (var spike in terrain.Spikes ?? [])
            if (Clip(Kind.Spike, spike.X1, spike.Y1, spike.X2, spike.Y2) is { } s) yield return s;
        foreach (var door in terrain.Doors ?? [])
        {
            if (raster && door.X1 == door.X2 && door.Y1 == door.Y2
                && door.X1 > 0 && door.X1 < terrain.Width && door.Y1 >= 0 && door.Y1 <= terrain.Height)
                yield return new(Kind.Door, door.X1, door.Y1, door.X2, door.Y2);
            else if (Clip(Kind.Door, door.X1, door.Y1, door.X2, door.Y2) is { } d) yield return d;
        }

        Segment? Clip(Kind kind, double ax, double ay, double bx, double by)
        {
            if (!double.IsFinite(ax) || !double.IsFinite(ay) || !double.IsFinite(bx) || !double.IsFinite(by))
                return null;
            if (ay == by && ay >= 0 && ay <= terrain.Height)
            {
                var left = Math.Clamp(Math.Min(ax, bx), 0, terrain.Width);
                var right = Math.Clamp(Math.Max(ax, bx), 0, terrain.Width);
                return right > left ? new Segment(kind, left, ay, right, ay) : null;
            }
            if (ax != bx || ax < 0 || ax > terrain.Width) return null;
            var low = Math.Clamp(Math.Min(ay, by), 0, terrain.Height);
            var high = Math.Clamp(Math.Max(ay, by), 0, terrain.Height);
            return high > low ? new Segment(kind, ax, low, ax, high) : null;
        }
    }

    private static bool ValidSize(BotScreenTerrain? geometry) => geometry is { Width: > 0, Height: > 0 }
        && double.IsFinite(geometry.Width) && double.IsFinite(geometry.Height);

    private static double Bound(double value) => Math.Clamp(value, -4, 4);
}
