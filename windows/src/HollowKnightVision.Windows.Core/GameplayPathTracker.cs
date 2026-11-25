namespace HollowKnightVision.Windows.Core;

public sealed record GameplayPathFrame(
    string Room, double Time, int Width, int Height, AtlasPoint? VerifiedCamera,
    IReadOnlyList<GroundHypothesisAtlasLine> Ground,
    IReadOnlyList<DetectedFloorLine> VisibleFloors, TerrainWallDetection Terrain,
    AtlasPoint? KnightFoot, PixelRect? KnightSize, DetectedWall? ContactWall)
{
    public GameplayPathRegistration? Atlas { get; init; }
    public long CaptureGeneration { get; init; }
    public GameplayPathLocalCamera? LocalCamera { get; init; }
}

/// <summary>A measured camera position in the current solver epoch, pending world placement.</summary>
public sealed record GameplayPathLocalCamera(long CoordinateEpoch, AtlasPoint Position);

public sealed record GameplaySavedRoomPath(int RoomId, IReadOnlyList<GroundHypothesisAtlasLine> Ground,
    IReadOnlyList<WorldRect> CameraFootprints);

public sealed record GameplayPathRegistration(GameplaySavedRoomPath Room, AtlasPoint WorldFromLocal, long GeometryEpoch);

/// <summary>Path view in world-up pixels, paired with the capture that supplied it.</summary>
public sealed record GameplayPathView(
    AtlasPoint? Camera, IReadOnlyList<AtlasPathStroke> Strokes, int KnightWidth, AtlasPathHint? Hint)
{
    // An unplaced view follows only the pixels from its own capture.
    public AtlasPoint LocalOrigin { get; init; }
    public AtlasPathPoint? SelectedObjective { get; init; }
    public IReadOnlyList<AtlasPathPoint> SelectedRoute { get; init; } = [];
    internal AtlasPlatformGraph? Graph { get; init; }
    internal AtlasPathPoint? Feet { get; init; }
    internal object? ChartIdentity { get; init; }
    internal AtlasPathPoint? RequestedObjective { get; init; }
    internal long ObjectiveRevision { get; init; }

    public GameplayPathView PreserveNewerSelection(GameplayPathView previous) =>
        ChartIdentity is not null && ChartIdentity == previous.ChartIdentity && ObjectiveRevision < previous.ObjectiveRevision
            ? PreviewSelection(previous.RequestedObjective, previous.ObjectiveRevision) : this;

    internal GameplayPathView PreviewSelection(AtlasPathPoint? target, long revision)
    {
        var navigation = new AtlasPathNavigator();
        var hint = target is not null && Graph is { } graph && Feet is { } feet
            ? navigation.Follow(graph, feet, grounded: null, measured: false, admitted: null,
                walked: [], fixedObjective: target, fixedObjectiveOnGround: false) : null;
        return this with
        {
            RequestedObjective = target,
            ObjectiveRevision = revision,
            SelectedObjective = ProjectObjective(Graph, target),
            SelectedRoute = navigation.SelectedRoute,
            Hint = hint
        };
    }

    internal static AtlasPathPoint? ProjectObjective(AtlasPlatformGraph? graph, AtlasPathPoint? target) =>
        graph is not null && target is { } point ? graph.NearestStroke(point, graph.KnightHeight)?.Closest(point) : null;
}

/// <summary>Keeps path evidence in its measured camera space. An unplaced frame has a short-lived chart.</summary>
public sealed class GameplayPathTracker
{
    private const double GraphInterval = .125;
    private const double HandoffInterval = .35;
    private const double SeamLifetime = .7;
    private string? activeRoom;
    private long activeGeneration;
    private (int Room, long Epoch)? activeAtlas;
    private Chart registered = new();
    private (long Epoch, Chart Chart)? provisional;
    private Chart? frame;
    private Chart? previousChart;
    private double previousTime;
    private (long Epoch, AtlasPoint ChartFromLocal)? previousMeasurement;
    private SeamHandoff? handoff;
    private int knightHeight;
    private int knightWidth;
    private bool measuredSize;
    private long objectiveRevision;
    private ObjectiveEdit? pendingObjective;

    private sealed record ObjectiveEdit(object ChartIdentity, AtlasPathPoint? Point, long Revision);
    private sealed record SeamHandoff(long Epoch, double Time, AtlasPathPlatform[] Floors);

    private sealed class Chart
    {
        internal readonly object Identity = new();
        internal AtlasPathPoint? Objective;
        internal long ObjectiveRevision;
        internal AtlasPlatformGraph? Graph;
        internal AtlasPathPlatform[] Floors = [];
        internal AtlasPathPlatform[] Walked = [];
        internal AtlasPathWall[] Walls = [];
        internal IReadOnlyList<AtlasPathStroke> Display = [];
        internal IReadOnlyList<AtlasPathPlatform> Seams = [];
        internal IReadOnlyList<AtlasPathPlatform> HandoffSeams = [];
        internal double LastBuild = double.NegativeInfinity;
        internal readonly VisualPathWallHistory Witnesses = new();
        internal AtlasPathWall[] RememberedWalls = [];
        internal IReadOnlyList<AtlasPathPlatform> Exposed = [];
        internal IReadOnlyList<AtlasPathPlatform> Visible = [];
        internal readonly AtlasPathNavigator Navigation = new();
        internal readonly PathCoverage Coverage = new();
    }

    /// <summary>Uses the presented chart for hit testing and queues its selection for the capture worker.</summary>
    public GameplayPathView? SelectObjective(GameplayPathView view, AtlasPoint point, double tolerance)
    {
        if (view.ChartIdentity is not { } owner || view.Graph is not { } graph
            || tolerance is not (>= 0 and <= 10_000) || !AtlasPathPoint.TryQuantize(point.X, point.Y, out var click))
            return null;
        var radius = Math.Max(2, (int)Math.Ceiling(tolerance));
        if (graph.NearestStroke(click, radius) is not { } stroke) return null;
        var hit = stroke.Closest(click);
        AtlasPathPoint? target = view.SelectedObjective is { } selected
            && hit.DistanceTo(selected) <= Math.Min(radius, Math.Max(2, graph.KnightWidth / 2)) ? null : hit;
        var revision = Interlocked.Increment(ref objectiveRevision);
        Volatile.Write(ref pendingObjective, new(owner, target, revision));
        return view.PreviewSelection(target, revision);
    }

    public GameplayPathView? Observe(GameplayPathFrame sample)
    {
        if (!double.IsFinite(sample.Time) || sample.Width < 1 || sample.Height < 1) return null;
        var placed = sample.VerifiedCamera is { } pose && AtlasPathPoint.TryQuantize(pose.X, pose.Y, out _);
        (int Room, long Epoch)? atlasScope = sample.Atlas is { } registration
            ? (registration.Room.RoomId, registration.GeometryEpoch) : null;
        if (sample.Room != activeRoom || sample.CaptureGeneration != activeGeneration
            || placed && atlasScope is { } incoming && activeAtlas is { } prior && incoming.Room != prior.Room)
        {
            Reset();
            activeRoom = sample.Room;
            activeGeneration = sample.CaptureGeneration;
        }
        ApplyPendingObjective();
        if (placed && atlasScope != activeAtlas)
        {
            var sameRoom = atlasScope is { } current && activeAtlas?.Room == current.Room;
            registered = new()
            {
                Objective = sameRoom ? registered.Objective : null,
                ObjectiveRevision = sameRoom ? registered.ObjectiveRevision : 0
            };
            activeAtlas = atlasScope;
        }
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

        if (placed) return ObserveRegistered(sample, sample.VerifiedCamera.GetValueOrDefault());
        return sample.LocalCamera is { CoordinateEpoch: >= 0 } local
            && AtlasPathPoint.TryQuantize(local.Position.X, local.Position.Y, out _)
            ? ObserveProvisional(sample, local) : ObserveFrame(sample);
    }

    private void ApplyPendingObjective()
    {
        if (Interlocked.Exchange(ref pendingObjective, null) is not { } edit
            || previousChart is not { } chart || chart.Identity != edit.ChartIdentity) return;
        chart.Objective = edit.Point;
        chart.ObjectiveRevision = edit.Revision;
        chart.Navigation.ClearObjective();
    }

    public void Reset()
    {
        activeRoom = null;
        activeAtlas = null;
        registered = new();
        provisional = null;
        frame = null;
        previousChart = null;
        previousMeasurement = null;
        handoff = null;
        Interlocked.Exchange(ref pendingObjective, null);
        measuredSize = false;
    }

    public void Suspend()
    {
        // An interruption breaks corroboration, while accepted room evidence remains available.
        ApplyPendingObjective();
        registered.Witnesses.Reset();
        registered.Navigation.Interrupt();
        if (provisional is { } local)
        {
            local.Chart.Witnesses.Reset();
            local.Chart.Navigation.Interrupt();
        }
        frame = null;
        previousChart = null;
        previousMeasurement = null;
        handoff = null;
    }

    internal void RecoverMeasuredBasis(string room, long generation, long sourceEpoch, long destinationEpoch,
        double time, GroundHypothesisTrackingResult ground)
    {
        var sameCaptureRoom = room == activeRoom && generation == activeGeneration;
        if (sameCaptureRoom) ApplyPendingObjective();
        var priorAtlas = sameCaptureRoom ? activeAtlas : null;
        var atlasObjective = priorAtlas.HasValue ? registered.Objective : null;
        var atlasObjectiveRevision = registered.ObjectiveRevision;

        AtlasPathPlatform[] witnessed = [];
        if (sameCaptureRoom && IsContinuous(time)
            && previousChart is { Graph: { } graph } chart
            && previousMeasurement is { } measurement && measurement.Epoch == sourceEpoch
            && ground.LocalTextureSupport > 0 && ground.LocalReferenceTimestamp is { } reference
            && time - reference is > 0 and <= HandoffInterval
            && ground.LocalReferencePosition is { } referencePosition
            && AtlasPathPoint.TryQuantize(referencePosition.X, referencePosition.Y, out _)
            && ground.LocalMatchedImageDisplacement is { } displacement
            && AtlasPathPoint.TryQuantize(displacement.X, displacement.Y, out _)
            && ground.GlobalCorrection is { } correction
            && AtlasPathPoint.TryQuantize(correction.X - measurement.ChartFromLocal.X,
                correction.Y - measurement.ChartFromLocal.Y, out var shift))
        {
            // A previous bridge supplies no evidence of its own. Only export
            // the parts of the road that the source chart actually observed.
            witnessed = (from floor in graph.Floors
                         from edge in chart.Visible
                         let left = Math.Max(floor.Left, edge.Left)
                         let right = Math.Min(floor.Right, edge.Right)
                         where left < right && Math.Abs(floor.Y - edge.Y) <= 2
                         select new AtlasPathPlatform(left + shift.X, right + shift.X, floor.Y + shift.Y))
                .Distinct().ToArray();
        }

        Reset();
        if (witnessed.Length == 0 && atlasObjective is null) return;
        activeRoom = room;
        activeGeneration = generation;
        // The correction retires local geometry. A registered destination is
        // already in Atlas coordinates and belongs to its original room.
        if (atlasObjective is not null)
        {
            activeAtlas = priorAtlas;
            registered.Objective = atlasObjective;
            registered.ObjectiveRevision = atlasObjectiveRevision;
        }
        if (witnessed.Length > 0) handoff = new(destinationEpoch, time, witnessed);
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
            // The placed pose and the locally measured pose must describe the
            // same camera before the registered basis can carry earlier evidence.
            if (provisional is { } local && sample.LocalCamera is { } measured
                && measured.CoordinateEpoch == local.Epoch && measured.Position == sample.VerifiedCamera
                && AtlasPathPoint.TryQuantize(offset.X, offset.Y, out var translation))
                Promote(local.Chart, translation);
            ground = atlas.Room.Ground.Concat(sample.Ground.Select(line => line with
            {
                AtlasStart = TranslatePoint(line.AtlasStart, offset),
                AtlasEnd = TranslatePoint(line.AtlasEnd, offset)
            }));
            registered.Coverage.SetSaved(atlas.Room.CameraFootprints);
        }
        provisional = null;
        frame = null;
        registered.Coverage.Include(camera, sample.Width, sample.Height);
        UpdateMeasuredChart(registered, sample, camera, ground);
        return Present(registered, sample, camera, camera, measured: true);
    }

    private GameplayPathView? ObserveProvisional(GameplayPathFrame sample, GameplayPathLocalCamera local)
    {
        if (provisional is not { } prior || prior.Epoch != local.CoordinateEpoch)
            provisional = (local.CoordinateEpoch, new());
        var chart = provisional.Value.Chart;
        if (previousChart == registered)
        {
            registered.Witnesses.Reset();
            registered.Navigation.Interrupt();
        }
        frame = null;
        UpdateMeasuredChart(chart, sample, local.Position, []);
        return Present(chart, sample, local.Position, null, measured: true);
    }

    private void Promote(Chart source, AtlasPathPoint shift)
    {
        AtlasPathPlatform Move(AtlasPathPlatform line) =>
            new(line.Left + shift.X, line.Right + shift.X, line.Y + shift.Y);
        registered.Visible = AtlasPathFloorGeometry.Extend(registered.Visible, source.Visible.Select(Move), knightHeight);
        registered.Exposed = AtlasPathFloorGeometry.Extend(registered.Exposed, source.Exposed.Select(Move), knightHeight);
        registered.RememberedWalls = RetainWalls(registered.RememberedWalls, source.RememberedWalls.Select(wall =>
            wall with { X = wall.X + shift.X, Low = wall.Low + shift.Y, High = wall.High + shift.Y }).ToArray());
        var heightShift = knightHeight - (source.Graph?.KnightHeight ?? knightHeight);
        registered.Navigation.MergeVisitedFrom(source.Navigation, shift with { Y = shift.Y + heightShift }, knightHeight);
        if (registered.Objective is null && source.Objective is { } target)
        {
            registered.Objective = new(target.X + shift.X, target.Y + shift.Y + heightShift);
            registered.ObjectiveRevision = source.ObjectiveRevision;
            registered.Navigation.ClearObjective();
        }
        registered.Navigation.Interrupt();
        registered.LastBuild = double.NegativeInfinity;
    }

    private void UpdateMeasuredChart(Chart chart, GameplayPathFrame sample, AtlasPoint camera,
        IEnumerable<GroundHypothesisAtlasLine> ground)
    {
        var geometry = ground.Select(line => (Walked: line.SegmentId == WalkedGroundMemory.WalkedSegmentId,
                Span: Translate(line)))
            .Where(line => line.Span.HasValue).ToLookup(line => line.Walked, line => line.Span.GetValueOrDefault());
        var walked = geometry[true].Distinct().OrderBy(LineOrder).ToArray();
        var floors = geometry[false];
        var current = ProjectFloors(sample, camera, exposed: false);
        chart.Visible = AtlasPathFloorGeometry.Extend(chart.Visible, current, knightHeight);
        var visibleEdges = ProjectFloors(sample, camera, exposed: true);
        var faces = ProjectWalls(sample, camera);
        var history = chart.Witnesses;
        var observed = history.Observe(faces, visibleEdges, sample.Time);
        var retained = chart.RememberedWalls.SelectMany(wall =>
            OutsideClearance(wall, history.ClearCrossings(wall, knightHeight, knightWidth)));
        chart.RememberedWalls = RetainWalls(retained, observed);

        if (visibleEdges.Length > 0)
            chart.Exposed = AtlasPathFloorGeometry.Extend(chart.Exposed,
                history.ExposedFloors(knightHeight, knightWidth), knightHeight);
        var supported = chart.RememberedWalls.Length == 0 ? []
            : AtlasPathFloorGeometry.StitchDetected(chart.Exposed, [], [], knightHeight, knightWidth,
                chart.Visible, maximumSupportedGap: knightHeight, requireWitness: true);
        var walls = chart.RememberedWalls.SelectMany(wall =>
        {
            var landing = floors.Concat(walked).Any(span => span.Right - span.Left >= knightWidth
                && Math.Abs((wall.SolidSide > 0 ? span.Left : span.Right) - wall.X) <= Math.Max(3, knightWidth / 2)
                && Math.Abs(span.Y - wall.High) <= Math.Max(3, knightHeight / 6));
            var exposed = wall.ConfirmedByContact || landing ? new[] { wall } : OutsideClearance(wall, supported);
            return exposed.SelectMany(part => OutsideClearance(part, walked));
        }).Distinct().OrderBy(wall => (wall.X, wall.Low, wall.High)).ToArray();

        // Accepted world strokes own the geometry outside the current image.
        // Current edges fill the visible chart until the tracker admits them.
        var all = GroundClearance.FilterPaths(floors.Concat(chart.Visible).Distinct().ToArray(), knightHeight)
            .SelectMany(line => ExcludeWalkedColumns(line, walked)).Distinct().OrderBy(LineOrder).ToArray();
        var seams = ResolveHandoffSeams(sample, camera);
        var changedHandoff = !seams.SequenceEqual(chart.HandoffSeams);
        chart.HandoffSeams = seams;
        UpdateGraph(chart, all, walked, walls, seams.Length == 0 ? chart.Visible : [.. chart.Visible, .. seams],
            sample.Time, force: changedHandoff);
    }

    private AtlasPathPlatform[] ResolveHandoffSeams(GameplayPathFrame sample, AtlasPoint camera)
    {
        if (handoff is not { } pending) return [];
        if (sample.Time - pending.Time is not (>= 0 and <= SeamLifetime)
            || sample.LocalCamera is not { } local || local.CoordinateEpoch != pending.Epoch
            || sample.VerifiedCamera is { } verified && verified != local.Position)
        {
            handoff = null;
            return [];
        }
        if (!AtlasPathPoint.TryQuantize(camera.X - local.Position.X, camera.Y - local.Position.Y, out var shift))
            return [];
        return shift == default ? pending.Floors : pending.Floors.Select(floor =>
            new AtlasPathPlatform(floor.Left + shift.X, floor.Right + shift.X, floor.Y + shift.Y)).ToArray();
    }

    private GameplayPathView? ObserveFrame(GameplayPathFrame sample)
    {
        handoff = null;
        previousMeasurement = null;
        registered.Witnesses.Reset();
        if (provisional is { } local) local.Chart.Witnesses.Reset();
        var current = ProjectFloors(sample, default, exposed: false);
        if (current.Length == 0) return null;
        // A visible cap corroborates a wall in a frame that has no registered history.
        var corners = sample.Terrain.Corners.Where(corner =>
        {
            var face = sample.Terrain.Walls[corner.WallIndex];
            return corner.Row <= face.Top + Math.Max(3, knightHeight / 6);
        }).Select(corner => corner.WallIndex).ToHashSet();
        var walls = ProjectWalls(sample, default, corners);
        if (frame != previousChart || !IsContinuous(sample.Time)) frame = null;
        var chart = frame ??= new();
        UpdateGraph(chart, current, [], walls, [], sample.Time, force: true);
        return Present(chart, sample, default, null, measured: false);
    }

    private GameplayPathView? Present(Chart chart, GameplayPathFrame sample, AtlasPoint position, AtlasPoint? camera, bool measured)
    {
        previousMeasurement = null;
        if (chart.Graph is not { } graph || graph.Strokes.Count == 0) return null;
        if (!ReferenceEquals(chart, previousChart))
        {
            if (camera is null && chart.Objective is null && IsContinuous(sample.Time)
                && previousChart is { Objective: null } previous)
                chart.Navigation.InheritAutomaticDirection(previous.Navigation);
            previousChart = chart;
        }
        previousTime = sample.Time;
        if (measured && sample.LocalCamera is { CoordinateEpoch: >= 0 } local
            && (sample.VerifiedCamera is null || sample.VerifiedCamera == local.Position))
            previousMeasurement = (local.CoordinateEpoch, new(position.X - local.Position.X, position.Y - local.Position.Y));
        AtlasPathHint? hint = null;
        AtlasPathPoint? observedFeet = null;
        if (sample.KnightFoot is { } foot && AtlasPathPoint.TryQuantize(
                position.X + foot.X, position.Y + sample.Height - foot.Y, out var feet))
        {
            observedFeet = feet;
            hint = chart.Navigation.Follow(graph, feet, grounded: null, measured,
                camera.HasValue && chart.Objective is null ? (a, b) => chart.Coverage.Allows(a, b, graph) : null,
                chart.Walked, fixedObjective: chart.Objective, fixedObjectiveOnGround: false);
        }
        return new(camera, chart.Display, graph.KnightWidth, hint)
        {
            LocalOrigin = position,
            Graph = graph,
            Feet = observedFeet,
            ChartIdentity = chart.Identity,
            RequestedObjective = chart.Objective,
            ObjectiveRevision = chart.ObjectiveRevision,
            SelectedObjective = GameplayPathView.ProjectObjective(graph, chart.Objective),
            SelectedRoute = chart.Objective.HasValue && hint.HasValue ? chart.Navigation.SelectedRoute : []
        };
    }

    private bool IsContinuous(double time) =>
        previousChart is not null && time - previousTime is >= 0 and <= HandoffInterval;

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

    internal static AtlasPathWall[] RetainWalls(IEnumerable<AtlasPathWall> current, IReadOnlyList<AtlasPathWall> observed)
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

        internal bool Allows(AtlasPathPoint a, AtlasPathPoint b, AtlasPlatformGraph graph) =>
            AtlasPathRouting.AllowsRegisteredSegment(graph, a, b, Cell, Known);

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
