namespace HollowKnightVision.Windows.Core;

public sealed record GameplayPathFrame(
    string Room, double Time, int Width, int Height, AtlasPoint? VerifiedCamera,
    IReadOnlyList<GroundHypothesisAtlasLine> Ground,
    IReadOnlyList<DetectedFloorLine> VisibleFloors, TerrainWallDetection Terrain,
    AtlasPoint? KnightFoot, PixelRect? KnightSize, DetectedWall? ContactWall)
{
    public GameplayPathRegistration? Atlas { get; init; }
}

public sealed record GameplaySavedRoomPath(int RoomId, IReadOnlyList<GroundHypothesisAtlasLine> Ground,
    IReadOnlyList<WorldRect> CameraFootprints);

public sealed record GameplayPathRegistration(GameplaySavedRoomPath Room, AtlasPoint WorldFromLocal, long GeometryEpoch);

/// <summary>Path view in world-up pixels, paired with the capture that supplied it.</summary>
public sealed record GameplayPathView(
    AtlasPoint? Camera, IReadOnlyList<AtlasPathStroke> Strokes, int KnightWidth, AtlasPathHint? Hint);

/// <summary>Keeps path evidence in its measured camera space. An unplaced frame has a short-lived chart.</summary>
public sealed class GameplayPathTracker
{
    private const double GraphInterval = .125;
    private string? activeRoom;
    private (int Room, long Epoch)? activeAtlas;
    private Chart registered = new();
    private Chart? frame;
    private int knightHeight;
    private int knightWidth;
    private bool measuredSize;

    private sealed class Chart
    {
        internal AtlasPlatformGraph? Graph;
        internal AtlasPathPlatform[] Floors = [];
        internal AtlasPathPlatform[] Walked = [];
        internal AtlasPathWall[] Walls = [];
        internal IReadOnlyList<AtlasPathStroke> Display = [];
        internal IReadOnlyList<AtlasPathPlatform> Seams = [];
        internal double LastBuild = double.NegativeInfinity;
        internal readonly VisualPathWallHistory Witnesses = new();
        internal AtlasPathWall[] RememberedWalls = [];
        internal IReadOnlyList<AtlasPathPlatform> Exposed = [];
        internal IReadOnlyList<AtlasPathPlatform> Visible = [];
        internal readonly AtlasPathNavigator Navigation = new();
        internal readonly PathCoverage Coverage = new();
    }

    public GameplayPathView? Observe(GameplayPathFrame sample)
    {
        if (!double.IsFinite(sample.Time) || sample.Width < 1 || sample.Height < 1) return null;
        var placed = sample.VerifiedCamera is { } pose && AtlasPathPoint.TryQuantize(pose.X, pose.Y, out _);
        (int Room, long Epoch)? atlasScope = sample.Atlas is { } registration
            ? (registration.Room.RoomId, registration.GeometryEpoch) : null;
        if (sample.Room != activeRoom || placed && atlasScope != activeAtlas)
        {
            Reset();
            activeRoom = sample.Room;
        }
        if (placed) activeAtlas = atlasScope;
        if (!measuredSize)
        {
            knightHeight = Math.Max(5, AtlasPathPoint.Round(sample.Height * .12));
            knightWidth = Math.Max(3, AtlasPathPoint.Round(knightHeight * .45));
            if (sample.KnightSize is { Width: > 0, Height: > 0 } actor && actor.Height < sample.Height / 2d
                && actor.Width < sample.Width / 4d && sample.KnightFoot is not null)
            {
                knightHeight = Math.Max(5, actor.Height);
                knightWidth = Math.Max(3, actor.Width);
                measuredSize = true;
            }
        }

        return placed ? ObserveRegistered(sample, sample.VerifiedCamera.GetValueOrDefault()) : ObserveFrame(sample);
    }

    public void Reset()
    {
        activeRoom = null;
        activeAtlas = null;
        registered = new();
        frame = null;
        measuredSize = false;
    }

    public void Suspend()
    {
        // An interruption breaks corroboration, while accepted room evidence remains available.
        registered.Witnesses.Reset();
        frame = null;
    }

    private GameplayPathView? ObserveRegistered(GameplayPathFrame sample, AtlasPoint camera)
    {
        IEnumerable<GroundHypothesisAtlasLine> ground = sample.Ground;
        if (sample.Atlas is { } atlas)
        {
            // Live ground is expressed relative to the bottom-left of the
            // image. The durable world stores camera positions at its center.
            var offset = new AtlasPoint(atlas.WorldFromLocal.X - sample.Width / 2d,
                atlas.WorldFromLocal.Y - sample.Height / 2d);
            camera = TranslatePoint(camera, offset);
            if (!AtlasPathPoint.TryQuantize(camera.X, camera.Y, out _)) return ObserveFrame(sample);
            ground = atlas.Room.Ground.Concat(sample.Ground.Select(line => line with
            {
                AtlasStart = TranslatePoint(line.AtlasStart, offset),
                AtlasEnd = TranslatePoint(line.AtlasEnd, offset)
            }));
            registered.Coverage.SetSaved(atlas.Room.CameraFootprints);
        }
        registered.Coverage.Include(camera, sample.Width, sample.Height);
        var geometry = ground.Select(line => (Walked: line.SegmentId == WalkedGroundMemory.WalkedSegmentId,
                Span: Translate(line)))
            .Where(line => line.Span.HasValue).ToLookup(line => line.Walked, line => line.Span.GetValueOrDefault());
        var walked = geometry[true].Distinct().OrderBy(LineOrder).ToArray();
        var floors = geometry[false];
        var current = ProjectFloors(sample, camera, exposed: false);
        registered.Visible = Extend(registered.Visible, current);
        var visibleEdges = ProjectFloors(sample, camera, exposed: true);
        var faces = ProjectWalls(sample, camera);
        var history = registered.Witnesses;
        var observed = history.Observe(faces, visibleEdges, sample.Time);
        var retained = registered.RememberedWalls.SelectMany(wall =>
            OutsideClearance(wall, history.ClearCrossings(wall, knightHeight, knightWidth)));
        registered.RememberedWalls = RetainWalls(retained, observed);

        if (visibleEdges.Length > 0)
            registered.Exposed = Extend(registered.Exposed, history.ExposedFloors(knightHeight, knightWidth));
        var supported = registered.RememberedWalls.Length == 0 ? []
            : AtlasPathFloorGeometry.StitchDetected(registered.Exposed, [], [], knightHeight, knightWidth,
                registered.Visible, maximumSupportedGap: knightHeight, requireWitness: true);
        var walls = registered.RememberedWalls.SelectMany(wall =>
        {
            var landing = floors.Concat(walked).Any(span => span.Right - span.Left >= knightWidth
                && Math.Abs((wall.SolidSide > 0 ? span.Left : span.Right) - wall.X) <= Math.Max(3, knightWidth / 2)
                && Math.Abs(span.Y - wall.High) <= Math.Max(3, knightHeight / 6));
            var exposed = wall.ConfirmedByContact || landing ? new[] { wall } : OutsideClearance(wall, supported);
            return exposed.SelectMany(part => OutsideClearance(part, walked));
        }).Distinct().OrderBy(wall => (wall.X, wall.Low, wall.High)).ToArray();

        // Accepted world strokes own the geometry outside the current image.
        // Current edges fill the visible chart until the tracker admits them.
        var all = GroundClearance.FilterPaths(floors.Concat(registered.Visible).Distinct().ToArray(), knightHeight)
            .SelectMany(line => ExcludeWalkedColumns(line, walked)).Distinct().OrderBy(LineOrder).ToArray();
        UpdateGraph(registered, all, walked, walls, registered.Visible, sample.Time);
        return Present(registered, sample, camera, camera, measured: true);
    }

    private GameplayPathView? ObserveFrame(GameplayPathFrame sample)
    {
        registered.Witnesses.Reset();
        var current = ProjectFloors(sample, default, exposed: false);
        if (current.Length == 0) return null;
        // A visible cap corroborates a wall in a frame that has no registered history.
        var corners = sample.Terrain.Corners.Where(corner =>
        {
            var face = sample.Terrain.Walls[corner.WallIndex];
            return corner.Row <= face.Top + Math.Max(3, knightHeight / 6);
        }).Select(corner => corner.WallIndex).ToHashSet();
        var walls = ProjectWalls(sample, default, corners);
        var chart = frame ??= new();
        UpdateGraph(chart, current, [], walls, [], sample.Time, force: true);
        return Present(chart, sample, default, null, measured: false);
    }

    private GameplayPathView? Present(Chart chart, GameplayPathFrame sample, AtlasPoint position, AtlasPoint? camera, bool measured)
    {
        if (chart.Graph is not { } graph || graph.Strokes.Count == 0) return null;
        AtlasPathHint? hint = null;
        if (sample.KnightFoot is { } foot && AtlasPathPoint.TryQuantize(
                position.X + foot.X, position.Y + sample.Height - foot.Y, out var feet))
            hint = chart.Navigation.Follow(graph, feet, grounded: null, measured,
                measured ? (a, b) => chart.Coverage.Allows(a, b, graph) : null, chart.Walked);
        return new(camera, chart.Display, graph.KnightWidth, hint);
    }

    private void UpdateGraph(Chart chart, AtlasPathPlatform[] floors, AtlasPathPlatform[] walked, AtlasPathWall[] walls,
        IReadOnlyList<AtlasPathPlatform> seams, double time, bool force = false)
    {
        var graph = chart.Graph;
        if (graph is not null && graph.KnightHeight == knightHeight && graph.KnightWidth == knightWidth)
        {
            if (!force && time - chart.LastBuild is >= 0 and < GraphInterval) return;
            if (floors.SequenceEqual(chart.Floors) && walked.SequenceEqual(chart.Walked) && walls.SequenceEqual(chart.Walls)
                && seams.SequenceEqual(chart.Seams)) return;
        }
        chart.Graph = new(floors, walls, knightHeight, knightWidth, previous: graph, detectedGround: true,
            walked: walked, handoffSeams: seams);
        chart.Floors = floors;
        chart.Walked = walked;
        chart.Walls = walls;
        chart.Seams = seams;
        chart.Display = AtlasPathRouting.DisplayStrokes(chart.Graph.Strokes);
        chart.LastBuild = time;
    }

    private IReadOnlyList<AtlasPathPlatform> Extend(IReadOnlyList<AtlasPathPlatform> current, IEnumerable<AtlasPathPlatform> samples)
    {
        var dy = Math.Clamp(knightHeight / 5, 2, 6);
        var additions = samples.Where(sample => !current.Any(span =>
            Math.Abs(span.Y - sample.Y) <= dy && span.Left <= sample.Left + 1 && span.Right >= sample.Right - 1)).ToArray();
        return additions.Length == 0 ? current : AtlasPathFloorGeometry.Merge(current.Concat(additions), knightHeight, bridgeSeams: false);
    }

    private static AtlasPathWall[] RetainWalls(IEnumerable<AtlasPathWall> current, IReadOnlyList<AtlasPathWall> observed)
    {
        var result = current.ToList();
        foreach (var wall in observed)
        {
            var index = result.FindIndex(old => Math.Abs(old.X - wall.X) <= 3 && old.SolidSide == wall.SolidSide
                && old.Low <= wall.High + 2 && wall.Low <= old.High + 2);
            if (index < 0)
            {
                result.Add(wall);
                continue;
            }
            var previous = result[index];
            result[index] = previous with { Low = Math.Min(previous.Low, wall.Low), High = Math.Max(previous.High, wall.High),
                ConfirmedByContact = previous.ConfirmedByContact || wall.ConfirmedByContact };
        }
        return result.ToArray();
    }

    private IEnumerable<AtlasPathWall> OutsideClearance(AtlasPathWall wall, IEnumerable<AtlasPathPlatform> ground)
    {
        var margin = Math.Max(1, knightWidth / 2);
        var bottom = wall.Low;
        foreach (var span in ground.Where(span => span.Left + margin <= wall.X && wall.X <= span.Right - margin
                     && span.Y < wall.High && span.Y + knightHeight > wall.Low).OrderBy(span => span.Y))
        {
            if (bottom < span.Y) yield return wall with { Low = bottom, High = span.Y };
            bottom = Math.Max(bottom, span.Y + knightHeight);
        }
        if (bottom < wall.High) yield return wall with { Low = bottom };
    }

    private IEnumerable<AtlasPathPlatform> ExcludeWalkedColumns(AtlasPathPlatform line, AtlasPathPlatform[] walked)
    {
        var left = line.Left;
        foreach (var span in walked)
        {
            if (span.Left >= line.Right) break;
            if (span.Right <= left || Math.Abs(span.Y - line.Y) >= knightHeight) continue;
            if (span.Left > left) yield return line with { Left = left, Right = span.Left };
            left = Math.Max(left, span.Right);
            if (left >= line.Right) yield break;
        }
        yield return line with { Left = left };
    }

    private static AtlasPathPlatform[] ProjectFloors(GameplayPathFrame frame, AtlasPoint camera, bool exposed)
    {
        var result = new List<AtlasPathPlatform>();
        foreach (var line in frame.VisibleFloors)
        {
            var row = camera.Y + frame.Height - line.Row - .5;
            foreach (var span in exposed ? line.EvidenceSpans : [line.XSpan])
                if (AtlasPathPoint.TryQuantize(camera.X + span.Start, row, out var a)
                    && AtlasPathPoint.TryQuantize(camera.X + span.EndInclusive + 1, row, out var b) && a.X < b.X)
                    result.Add(new(a.X, b.X, a.Y));
        }
        return result.Distinct().OrderBy(LineOrder).ToArray();
    }

    private static AtlasPathWall[] ProjectWalls(GameplayPathFrame frame, AtlasPoint camera, IReadOnlySet<int>? corners = null)
    {
        var result = new List<AtlasPathWall>();
        for (var i = 0; i < frame.Terrain.Walls.Count; i++)
        {
            var wall = frame.Terrain.Walls[i];
            var contact = frame.ContactWall is { } hit && hit.SolidOnRight == wall.SolidOnRight
                && Math.Abs(hit.X - wall.X) <= 3 && hit.Top < wall.Bottom && wall.Top < hit.Bottom;
            if (corners is not null && !corners.Contains(i) && !contact) continue;
            if (AtlasPathPoint.TryQuantize(camera.X + wall.X, camera.Y + frame.Height - wall.Bottom, out var a)
                && AtlasPathPoint.TryQuantize(camera.X + wall.X, camera.Y + frame.Height - wall.Top, out var b) && a.Y < b.Y)
                result.Add(new(a.X, a.Y, b.Y, wall.SolidOnRight ? 1 : -1, contact));
        }
        return result.ToArray();
    }

    private static AtlasPathPlatform? Translate(GroundHypothesisAtlasLine line) =>
        AtlasPathPoint.TryQuantize(line.AtlasStart.X, line.AtlasStart.Y, out var a)
        && AtlasPathPoint.TryQuantize(line.AtlasEnd.X, line.AtlasEnd.Y, out var b) && a.Y == b.Y && a.X < b.X
            ? new(a.X, b.X, a.Y) : null;

    private static (int, int, int) LineOrder(AtlasPathPlatform line) => (line.Left, line.Y, line.Right);

    private static AtlasPoint TranslatePoint(AtlasPoint point, AtlasPoint offset) =>
        new(point.X + offset.X, point.Y + offset.Y);

    private sealed class PathCoverage
    {
        private const int Cell = 32;
        private readonly HashSet<AtlasPathPoint> seen = [];
        private readonly record struct CellBounds(int Left, int Bottom, int Right, int Top)
        {
            internal bool Contains(AtlasPathPoint cell) =>
                Left <= cell.X && cell.X <= Right && Bottom <= cell.Y && cell.Y <= Top;
        }
        private CellBounds? previous;
        private IReadOnlyList<WorldRect>? savedFootprints;
        private CellBounds[] savedCells = [];

        internal void SetSaved(IReadOnlyList<WorldRect> footprints)
        {
            if (ReferenceEquals(footprints, savedFootprints)) return;
            savedFootprints = footprints;
            savedCells = footprints.Where(bounds => bounds.Width > 0 && bounds.Height > 0
                    && AtlasPathPoint.TryQuantize(bounds.X, bounds.Y, out _)
                    && AtlasPathPoint.TryQuantize(bounds.X + bounds.Width, bounds.Y + bounds.Height, out _))
                .Select(bounds => new CellBounds(Coordinate(bounds.X), Coordinate(bounds.Y),
                    Coordinate(bounds.X + bounds.Width), Coordinate(bounds.Y + bounds.Height))).Distinct().ToArray();
        }

        internal void Include(AtlasPoint camera, int width, int height)
        {
            var current = new CellBounds(Coordinate(camera.X), Coordinate(camera.Y),
                Coordinate(camera.X + width), Coordinate(camera.Y + height));
            if (current == previous) return;
            for (var y = current.Bottom; y <= current.Top; y++)
            for (var x = current.Left; x <= current.Right; x++)
                seen.Add(new(x, y));
            previous = current;
        }

        internal bool Allows(AtlasPathPoint a, AtlasPathPoint b, AtlasPlatformGraph graph)
        {
            var intervals = Math.Max(1, (a.DistanceTo(b) + Cell - 1) / Cell);
            for (var i = 1; i <= intervals; i++)
            {
                var point = new AtlasPathPoint(a.X + (int)((long)(b.X - a.X) * i / intervals),
                    a.Y + (int)((long)(b.Y - a.Y) * i / intervals));
                if (Known(point)) continue;
                var belowShelf = point with { Y = point.Y - Math.Max(2, graph.KnightHeight / 4) };
                if (graph.WalkNodes.Contains(point) || !Known(belowShelf) || !graph.Strokes.Any(stroke =>
                        stroke.Kind is AtlasPathKind.Jump or AtlasPathKind.DoubleJump && stroke.Horizontal && stroke.Contains(point)))
                    return false;
            }
            return true;
        }

        private bool Known(AtlasPathPoint point)
        {
            var cell = new AtlasPathPoint(Coordinate(point.X), Coordinate(point.Y));
            if (seen.Contains(cell)) return true;
            foreach (var bounds in savedCells)
                if (bounds.Contains(cell)) return true;
            return false;
        }
        private static int Coordinate(double value) => (int)Math.Floor(value / Cell);
    }
}
