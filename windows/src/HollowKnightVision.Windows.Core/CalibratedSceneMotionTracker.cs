namespace HollowKnightVision.Windows.Core;

public sealed record CalibratedSceneMotionEvidence(AtlasPoint Position, double ReferenceTimestamp, double Timestamp,
    int SupportedPatches, bool HorizontalMeasured, bool VerticalMeasured, double ResidualX, double ResidualY)
{
    public AtlasPoint? ReferencePosition { get; init; }
}

/// <summary>Estimates local camera displacement from scenery calibrated against observed ground.</summary>
internal sealed partial class CalibratedSceneMotionTracker
{
    internal sealed record GroundCalibration(AtlasPoint Position, double ReferenceTimestamp, CameraVector Displacement);
    private sealed record Sample(Grid Grid, double Timestamp);
    private readonly record struct Patch(AtlasPoint Point, CameraVector Gain, bool CalibratedX, bool CalibratedY, int Votes)
    {
        internal Patch Move(CameraVector motion) => this with { Point = new(Point.X + motion.X, Point.Y + motion.Y) };
    }
    private readonly record struct Match(Patch Patch, CameraVector Motion);
    private const int GridWidth = 160, MaximumPatches = 64, MaximumHistory = 64;
    private readonly Dictionary<double, Grid> history = [];
    private readonly Queue<double> historyOrder = [];
    private Sample? reference;
    private List<Patch> patches = [];
    private List<Patch> pending = [];
    private AtlasPoint? position;
    private double? lastSample, lastCalibration, lastTimestamp, lastSolveWidth;

    internal void Reset()
    {
        history.Clear();
        historyOrder.Clear();
        ClearMotion();
        lastSample = lastCalibration = lastTimestamp = lastSolveWidth = null;
    }

    private void ClearMotion()
    {
        reference = null;
        position = null;
        patches.Clear();
        pending.Clear();
    }

    internal static Grid? FromLuma(byte[] pixels, int width, int height, IReadOnlyList<PixelRect> exclusions)
    {
        if (width < GridWidth || height < 24 || pixels.LongLength != (long)width * height) return null;
        var gridHeight = (int)Math.Round((double)height * GridWidth / width, MidpointRounding.AwayFromZero);
        if (gridHeight < 24) return null;
        var output = new float[GridWidth * gridHeight];
        for (var y = 0; y < gridHeight; y++)
        for (var x = 0; x < GridWidth; x++)
        {
            var left = x * width / GridWidth;
            var right = Math.Max(left + 1, (x + 1) * width / GridWidth);
            var top = y * height / gridHeight;
            var bottom = Math.Max(top + 1, (y + 1) * height / gridHeight);
            long sum = 0;
            for (var row = top; row < bottom; row++)
            for (var column = left; column < right; column++)
                sum += pixels[row * width + column];
            output[y * GridWidth + x] = (float)sum / ((right - left) * (bottom - top));
        }
        var scaleX = (double)GridWidth / width;
        var scaleY = (double)gridHeight / height;
        var masks = exclusions.Where(rect => rect.Width > 0 && rect.Height > 0)
            .Select(rect => new StencilRect(rect.X * scaleX, rect.Y * scaleY, rect.Width * scaleX, rect.Height * scaleY))
            .ToArray();
        return new(GridWidth, gridHeight, output, masks);
    }

    internal CalibratedSceneMotionEvidence? Observe(Grid grid, double timestamp, double solveWidth,
        GroundCalibration? ground = null)
    {
        if (!double.IsFinite(timestamp) || !double.IsFinite(solveWidth) || solveWidth <= 0
            || grid.Width != GridWidth || grid.Height < 24 || grid.Pixels.LongLength != (long)grid.Width * grid.Height
            || ground is { } invalid && (!double.IsFinite(invalid.Position.X) || !double.IsFinite(invalid.Position.Y)
                || !double.IsFinite(invalid.Displacement.X) || !double.IsFinite(invalid.Displacement.Y)))
        {
            Reset();
            return null;
        }
        if (lastTimestamp is { } priorTime && (timestamp <= priorTime || timestamp - priorTime > .25)
            || lastSolveWidth is { } previousWidth && previousWidth != solveWidth)
            Reset();
        lastTimestamp = timestamp;
        lastSolveWidth = solveWidth;
        history.Add(timestamp, grid);
        historyOrder.Enqueue(timestamp);
        while (historyOrder.Count > MaximumHistory) history.Remove(historyOrder.Dequeue());
        if (lastSample is { } sampled && timestamp - sampled < .025) return null;
        lastSample = timestamp;
        var scale = solveWidth / grid.Width;
        if (ground is not null
            && timestamp - ground.ReferenceTimestamp is > 0 and <= .5
            && history.TryGetValue(ground.ReferenceTimestamp, out var prior)
            && Math.Max(Math.Abs(ground.Displacement.X), Math.Abs(ground.Displacement.Y)) >= 5 * solveWidth / 640
            && (lastCalibration is null || timestamp - lastCalibration.Value >= .04)
            && Calibrate(prior, grid, timestamp, scale, ground))
            return null;

        if (reference is not { } source || position is not { } priorPosition) return null;
        var age = timestamp - source.Timestamp;
        if (age < .025) return null;
        if (age > .12 || source.Grid.Width != grid.Width || source.Grid.Height != grid.Height)
        {
            ClearMotion();
            return null;
        }
        var matches = new List<Match>(patches.Count);
        var fits = FitBatch(source.Grid, grid, patches.Select(patch => patch.Point).ToArray());
        for (var index = 0; index < patches.Count; index++)
            if (fits[index] is { } fit) matches.Add(new(patches[index], fit.Motion));
        if (matches.Count < 3) return null;
        var medianX = Median(AxisMotions(matches, horizontal: true));
        var medianY = Median(AxisMotions(matches, horizontal: false));
        var agreeing = matches.Where(match => CameraMath.Length(
            match.Motion.X / match.Patch.Gain.X - medianX,
            match.Motion.Y / match.Patch.Gain.Y - medianY) <= .65).ToArray();
        if (agreeing.Length < 3
            || agreeing.Max(item => item.Patch.Point.X) - agreeing.Min(item => item.Patch.Point.X) < 20
            || agreeing.Max(item => item.Patch.Point.Y) - agreeing.Min(item => item.Patch.Point.Y) < 8)
            return null;
        var horizontalMotions = AxisMotions(agreeing, horizontal: true);
        var verticalMotions = AxisMotions(agreeing, horizontal: false);
        var delta = new CameraVector(Median(horizontalMotions), Median(verticalMotions));
        var horizontalMeasured = agreeing.Count(match => match.Patch.CalibratedX) >= 3;
        var verticalMeasured = agreeing.Count(match => match.Patch.CalibratedY) >= 3;
        var nextPosition = ground?.Position ?? new AtlasPoint(priorPosition.X - delta.X * scale, priorPosition.Y + delta.Y * scale);
        var residualX = Median(horizontalMotions.Select(value => Math.Abs(value - delta.X))) * scale;
        var residualY = Median(verticalMotions.Select(value => Math.Abs(value - delta.Y))) * scale;
        UpdatePatches(source.Grid, grid, delta, agreeing, horizontalMeasured, verticalMeasured);
        position = nextPosition;
        reference = new Sample(grid, timestamp);
        return new(nextPosition, source.Timestamp, timestamp, agreeing.Length, horizontalMeasured, verticalMeasured, residualX, residualY)
        {
            ReferencePosition = priorPosition
        };
    }

    private bool Calibrate(Grid prior, Grid grid, double timestamp, double scale, GroundCalibration ground)
    {
        var expected = new CameraVector(ground.Displacement.X / scale, ground.Displacement.Y / scale);
        var calibrated = new List<Patch>(MaximumPatches);
        var candidates = Candidates(grid).ToArray();
        var fits = FitBatch(prior, grid, candidates, expected);
        for (var index = 0; index < candidates.Length; index++)
        {
            if (fits[index]?.Motion is not { } fit
                || CameraMath.Length(fit.X - expected.X, fit.Y - expected.Y) >= .6) continue;
            var xKnown = Math.Abs(expected.X) >= 1.25;
            var yKnown = Math.Abs(expected.Y) >= 2;
            var gain = new CameraVector(xKnown ? fit.X / expected.X : 1, yKnown ? fit.Y / expected.Y : 1);
            if (gain.X is <= .8 or >= 1.4 || gain.Y is <= .8 or >= 1.4) continue;
            calibrated.Add(new Patch(candidates[index], gain, xKnown, yKnown, 3).Move(fit));
            if (calibrated.Count == MaximumPatches) break;
        }
        if (calibrated.Count < 3) return false;
        patches = calibrated;
        pending.Clear();
        position = ground.Position;
        reference = new Sample(grid, timestamp);
        lastCalibration = timestamp;
        return true;
    }

    private void UpdatePatches(Grid source, Grid target, CameraVector delta, IReadOnlyList<Match> agreeing,
        bool horizontalMeasured, bool verticalMeasured)
    {
        var next = agreeing.Select(match => match.Patch.Move(match.Motion)).ToList();
        var nextPending = new List<Patch>();
        foreach (var patch in pending)
        {
            if (patch.CalibratedX && !horizontalMeasured || patch.CalibratedY && !verticalMeasured
                || RegisterPatch(source, target, patch.Point) is not { } fit
                || Gain(fit, delta, patch.Gain) is not { } gain) continue;
            var tracked = patch.Move(fit) with { Gain = gain, Votes = patch.Votes + 1 };
            if (tracked.Votes >= 2) next.Add(tracked);
            else nextPending.Add(tracked);
        }
        patches = next.Take(MaximumPatches).ToList();
        pending = nextPending;
        foreach (var point in Candidates(target))
        {
            if (patches.Count + pending.Count >= MaximumPatches) break;
            if (patches.Concat(pending).Any(patch => CameraMath.Length(patch.Point.X - point.X, patch.Point.Y - point.Y) < 9)
                || RegisterPatch(source, target, point) is not { } fit
                || Gain(fit, delta, null) is not { } gain) continue;
            pending.Add(new Patch(point, gain, horizontalMeasured && Math.Abs(delta.X) >= 1,
                verticalMeasured && Math.Abs(delta.Y) >= 1, 1).Move(fit));
        }
    }

    private static IEnumerable<AtlasPoint> Candidates(Grid grid)
    {
        var count = 0;
        for (var y = 14; y < grid.Height - 11; y += 12)
        for (var x = 12; x < grid.Width - 11; x += 12)
        {
            if (count++ >= 80) yield break;
            yield return new(x, y);
        }
    }

    private static double[] AxisMotions(IReadOnlyList<Match> matches, bool horizontal)
    {
        var calibrated = matches.Where(match => horizontal ? match.Patch.CalibratedX : match.Patch.CalibratedY).ToArray();
        var voters = calibrated.Length >= 3 ? calibrated : matches;
        return voters.Select(match => horizontal
            ? match.Motion.X / match.Patch.Gain.X
            : match.Motion.Y / match.Patch.Gain.Y).ToArray();
    }

    private static CameraVector? Gain(CameraVector motion, CameraVector delta, CameraVector? prior)
    {
        static double? Axis(double motion, double camera, double? prior)
        {
            if (Math.Abs(camera) < 1)
                return Math.Abs(motion - (prior ?? 1) * camera) <= .5 ? prior ?? 1 : null;
            var gain = motion / camera;
            if (gain is < .4 or >= 2.5) return null;
            if (prior is not { } previous) return gain;
            return Math.Abs(gain - previous) <= .2 ? (gain + previous) * .5 : null;
        }
        return Axis(motion.X, delta.X, prior?.X) is { } x && Axis(motion.Y, delta.Y, prior?.Y) is { } y
            ? new CameraVector(x, y) : null;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) * .5 : ordered[middle];
    }
}
