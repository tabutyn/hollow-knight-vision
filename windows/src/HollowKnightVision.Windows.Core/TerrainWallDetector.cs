namespace HollowKnightVision.Windows.Core;

// Capture coordinates have a top-left origin. Walls remain available for
// terrain decisions while the player overlay draws only horizontal joins.
public readonly record struct DetectedWall(int X, int Top, int Bottom, bool SolidOnRight);
public readonly record struct DetectedTerrainCorner(int WallIndex, int FloorIndex, int X, int Row, int FloorEndpoint);

public sealed record TerrainWallDetection(IReadOnlyList<DetectedWall> Walls, IReadOnlyList<DetectedTerrainCorner> Corners)
{
    public static TerrainWallDetection Empty { get; } = new([], []);
}

public static class TerrainWallDetector
{
    private readonly record struct Candidate(DetectedWall Wall, int Quality, bool Standalone = false);
    private readonly record struct Junction(int Floor, int Wall, bool FloorStart, bool WallTop, long Distance);
    private static readonly int[] stripRows = [-2, 0, 2];

    public static TerrainWallDetection Detect(GroundEdgeAnalysis analysis, IReadOnlyList<DetectedFloorLine> floors)
    {
        var width = analysis.Width;
        var height = analysis.Height;
        if (width < 32 || height < 24 || analysis.SourceLuma is not { } luma
            || analysis.ValidSourcePixels is not { } valid
            || luma.LongLength != (long)width * height || valid.Length != luma.Length)
            return TerrainWallDetection.Empty;

        var tolerance = Math.Max(4, Round(width / 64d));
        var candidates = new List<Candidate>();
        ScanEdges(width, height, luma, valid, floors, tolerance, candidates);
        TracePlatformFaces(width, height, luma, valid, floors, tolerance, candidates);

        var kept = new List<DetectedWall>();
        var standalone = new HashSet<int>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Quality)
                     .ThenBy(item => item.Wall.X).ThenBy(item => item.Wall.Top))
        {
            var wall = candidate.Wall;
            if (kept.Any(other => Math.Abs(wall.X - other.X) <= tolerance
                && (Math.Min(wall.Bottom, other.Bottom) - Math.Max(wall.Top, other.Top) + 1) * 2
                    >= Math.Min(wall.Bottom - wall.Top + 1, other.Bottom - other.Top + 1)))
                continue;
            if (candidate.Standalone) standalone.Add(kept.Count);
            kept.Add(Localize(wall, width, luma, valid));
            if (kept.Count == 48) break;
        }

        bool Allowed(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height && valid[y * width + x] != 0;
        var first = Join(kept, floors, tolerance, Allowed);
        standalone.UnionWith(first.Corners.Select(corner => corner.WallIndex));
        return Join(kept.Where((_, index) => standalone.Contains(index)).ToArray(), floors, tolerance, Allowed);
    }

    private static void ScanEdges(int width, int height, byte[] luma, byte[] valid,
        IReadOnlyList<DetectedFloorLine> floors, int tolerance, List<Candidate> candidates)
    {
        for (var x = 10; x < width - 10; x += 2)
        {
            var sign = 0;
            var start = 0;
            var last = 0;
            var votes = 0;
            var contrast = 0;
            for (var y = 2; y < height - 2; y += 2)
            {
                var i = y * width + x;
                if (valid[i - 9] == 0 || valid[i] == 0 || valid[i + 9] == 0)
                {
                    Finish();
                    continue;
                }
                var near = luma[i - 2] - luma[i + 2];
                var left = luma[i - 5] + luma[i - 9];
                var right = luma[i + 5] + luma[i + 9];
                var broad = (left - right) / 2;
                var side = broad > 0 ? 1 : -1;
                var strength = side * (near + broad) / 2;
                if (strength >= 7 && side * broad >= 6 && side * near >= 0 && Math.Min(left, right) <= 112)
                {
                    if (side != sign || y - last > 6) Finish();
                    if (votes == 0)
                    {
                        sign = side;
                        start = y;
                        contrast = 0;
                    }
                    last = y;
                    votes++;
                    contrast += strength;
                }
                else if (y - last > 6) Finish();
            }
            Finish();

            void Finish()
            {
                var count = votes;
                votes = 0;
                var length = last - start + 1;
                if (count < 5 || count * 200 < length * 60) return;
                var wall = new DetectedWall(x, start, last, sign > 0);
                var nearest = tolerance + 1;
                var anchors = 0;
                foreach (var line in floors)
                {
                    if (line.Row < start - tolerance || line.Row > last + tolerance) continue;
                    Consider(line.XSpan.Start, wall.SolidOnRight ? start : last, line.Row);
                    Consider(line.XSpan.EndInclusive, wall.SolidOnRight ? last : start, line.Row);
                }
                var mean = contrast / count;
                if (anchors == 0 && (length < 72 || count * 200 < length * 92 || mean < 54)) return;
                var quality = Math.Min(2, anchors) * 65 - nearest * 2 + Math.Min(55, length) + Math.Min(70, mean);
                candidates.Add(new(wall, quality, anchors == 0));

                void Consider(int endpoint, int tip, int row)
                {
                    var distance = Math.Abs(endpoint - x) + Math.Abs(tip - row) / 2;
                    if (distance > tolerance) return;
                    anchors++;
                    nearest = Math.Min(nearest, distance);
                }
            }
        }
    }

    private static void TracePlatformFaces(int width, int height, byte[] luma, byte[] valid,
        IReadOnlyList<DetectedFloorLine> floors, int tolerance, List<Candidate> candidates)
    {
        foreach (var floor in floors)
        {
            if (floor.Row >= height) continue;
            Trace(floor, floor.XSpan.Start, left: true, step: 1);
            Trace(floor, floor.XSpan.Start, left: true, step: -1);
            Trace(floor, floor.XSpan.EndInclusive, left: false, step: 1);
            Trace(floor, floor.XSpan.EndInclusive, left: false, step: -1);
        }

        void Trace(DetectedFloorLine floor, int endpoint, bool left, int step)
        {
            if (endpoint >= width) return;
            var interior = left ? step : -step;
            for (var x = endpoint - tolerance; x <= endpoint + tolerance; x += 2)
            {
                if (x < 11 || x >= width - 11) continue;
                var quality = 0;
                var blocks = 0;
                var gaps = 0;
                var tip = floor.Row;
                var textured = 0;
                for (var distance = 4; distance < height; distance += 6)
                {
                    var y = floor.Row + step * distance;
                    if (y < 3 || y >= height - 3) break;
                    var inner = 0;
                    var outer = 0;
                    var insideTexture = 0;
                    var outsideTexture = 0;
                    var validBlock = true;
                    foreach (var row in stripRows)
                    {
                        var i = (y + row) * width + x;
                        if (valid[i] == 0 || valid[i - 10] == 0 || valid[i + 10] == 0)
                        {
                            validBlock = false;
                            break;
                        }
                        var a = luma[i + 2 * interior];
                        var b = luma[i + 6 * interior];
                        var c = luma[i - 2 * interior];
                        var d = luma[i - 6 * interior];
                        inner += a + b;
                        outer += c + d;
                        insideTexture += Math.Abs(a - b) + Math.Abs(b - luma[i + 10 * interior]);
                        outsideTexture += Math.Abs(c - d) + Math.Abs(d - luma[i - 10 * interior]);
                    }
                    if (!validBlock) break;
                    var contrast = (outer - inner) / 6;
                    var texture = (insideTexture - outsideTexture) / 6;
                    var hasTexture = texture >= 8 && outsideTexture < 75;
                    if (contrast >= 9 || hasTexture && outer >= 90)
                    {
                        quality += Math.Min(38, Math.Max(0, contrast) + Math.Max(0, texture));
                        if (hasTexture) textured++;
                        blocks++;
                        gaps = 0;
                        tip = y + step * 2;
                    }
                    else if (++gaps >= 2) break;
                }
                if (blocks < 2 || textured * 2 < blocks || Math.Abs(tip - floor.Row) < 10) continue;
                var rank = 65 + (step > 0 ? 24 : 0) + Math.Min(50, blocks * 6) + quality / blocks - Math.Abs(x - endpoint) * 3;
                candidates.Add(new(new(x, Math.Min(tip, floor.Row), Math.Max(tip, floor.Row), interior > 0), rank));
            }
        }
    }

    private static DetectedWall Localize(DetectedWall wall, int width, byte[] luma, byte[] valid)
    {
        var best = -1;
        var localized = wall;
        for (var x = Math.Max(5, wall.X - 5); x <= Math.Min(width - 6, wall.X + 5); x++)
        {
            var energy = 0;
            var count = 0;
            for (var y = wall.Top + 2; y < wall.Bottom - 1; y += 2)
            {
                var i = y * width + x;
                if (valid[i - 4] == 0 || valid[i + 4] == 0) continue;
                var edge = Math.Abs(luma[i - 1] - luma[i + 1]);
                var broad = luma[i - 4] - luma[i + 4];
                energy += Math.Min(55, edge) + Math.Min(40, Math.Max(0, wall.SolidOnRight ? broad : -broad));
                count++;
            }
            var score = energy / Math.Max(1, count) - Math.Abs(x - wall.X) * 2;
            if (score <= best) continue;
            best = score;
            localized = wall with { X = x };
        }
        return localized;
    }

    public static TerrainWallDetection Join(IReadOnlyList<DetectedWall> walls, IReadOnlyList<DetectedFloorLine> floors,
        int tolerance, Func<int, int, bool> allowed)
    {
        var pairs = new List<Junction>();
        for (var w = 0; w < walls.Count; w++)
        for (var f = 0; f < floors.Count; f++)
        {
            Consider(w, f, floorStart: true);
            Consider(w, f, floorStart: false);
        }

        var result = walls.ToArray();
        var corners = new List<DetectedTerrainCorner>();
        var usedFloors = new HashSet<(int Index, bool First)>();
        var usedWalls = new HashSet<(int Index, bool First)>();
        foreach (var pair in pairs.OrderBy(pair => pair.Wall).ThenBy(pair => pair.Distance).ThenBy(pair => pair.Floor))
        {
            var line = floors[pair.Floor];
            var wall = result[pair.Wall];
            if (pair.WallTop ? line.Row >= wall.Bottom : line.Row <= wall.Top) continue;
            var floorKey = (pair.Floor, pair.FloorStart);
            var wallKey = (pair.Wall, pair.WallTop);
            if (usedFloors.Contains(floorKey) || usedWalls.Contains(wallKey)) continue;
            usedFloors.Add(floorKey);
            usedWalls.Add(wallKey);
            result[pair.Wall] = pair.WallTop ? wall with { Top = line.Row } : wall with { Bottom = line.Row };
            corners.Add(new(pair.Wall, pair.Floor, wall.X, line.Row,
                pair.FloorStart ? line.XSpan.Start : line.XSpan.EndInclusive));
        }
        return new(result, corners);

        void Consider(int w, int f, bool floorStart)
        {
            var wall = walls[w];
            var floor = floors[f];
            var end = floorStart ? floor.XSpan.Start : floor.XSpan.EndInclusive;
            var top = floorStart == wall.SolidOnRight;
            var tip = top ? wall.Top : wall.Bottom;
            var dx = Math.Abs((long)end - wall.X);
            var dy = Math.Abs((long)tip - floor.Row);
            if (dx > tolerance || dy > tolerance) return;
            var distance = dx * dx + dy * dy;
            if (distance > (long)tolerance * tolerance) return;
            for (long x = Math.Min(end, wall.X); x <= Math.Max(end, wall.X); x++)
                if (!allowed((int)x, floor.Row)) return;
            for (long y = Math.Min(tip, floor.Row); y <= Math.Max(tip, floor.Row); y++)
                if (!allowed(wall.X, (int)y)) return;
            pairs.Add(new(f, w, floorStart, top, distance));
        }
    }

    public static TerrainWallDetection ApplyContact(TerrainWallDetection detection, KnightTerrainContact contact,
        IReadOnlyList<DetectedFloorLine> floors, GroundEdgeAnalysis frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        if (frame.ValidSourcePixels is not { } valid || valid.LongLength != (long)width * height
            || contact.Wall is null && contact.Floor is null)
            return detection;

        var reach = Math.Max(2, (int)(6 * width / 640d));
        var walls = detection.Walls.ToList();
        if (contact.Wall is { } wall)
        {
            var candidate = Enumerable.Range(0, walls.Count)
                .Where(i => walls[i].SolidOnRight == wall.SolidOnRight && Math.Abs(walls[i].X - wall.X) <= reach
                    && walls[i].Top <= wall.Bottom + reach && walls[i].Bottom >= wall.Top - reach)
                .OrderBy(i => Math.Abs(walls[i].X - wall.X))
                .DefaultIfEmpty(-1).First();
            if (candidate < 0) walls.Add(wall);
            else
            {
                var edge = walls[candidate];
                walls[candidate] = edge with { Top = Math.Min(edge.Top, wall.Top), Bottom = Math.Max(edge.Bottom, wall.Bottom) };
            }
        }
        if (contact.Floor is { } walked
            && !floors.Any(floor => Math.Abs(floor.Row - walked.Row) <= reach
                && floor.XSpan.Start <= walked.XSpan.Start && floor.XSpan.EndInclusive >= walked.XSpan.EndInclusive))
            floors = [.. floors, new DetectedFloorLine(walked.Row, walked.XSpan)];

        return Join(walls, floors, Math.Max(4, width / 64), (x, y) =>
            (uint)x < (uint)width && (uint)y < (uint)height && valid[y * width + x] != 0);
    }

    internal static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
