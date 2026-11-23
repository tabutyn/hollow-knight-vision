namespace HollowKnightVision.Windows.Core;

/// <summary>Builds the same four causal frames that the shared trainer gives to PPO.</summary>
public sealed class BotObservationHistory
{
    private const double MaximumDeliveryAge = .25;
    private const int EnemyOffset = 96;
    private const int TerrainOffset = 16;
    private readonly int version;
    private readonly int width;
    private readonly List<double[]> frames = [];
    private (long Generation, int Room, double AvailableAt)? last;

    public BotObservationHistory(int version)
    {
        this.version = version;
        width = BotPolicyContract.Names(version)?.Count ?? throw new ArgumentOutOfRangeException(nameof(version));
    }

    public void Reset()
    {
        frames.Clear();
        last = null;
    }

    public float[]? Append(BotCaptureObservation row, BotPolicyAction previousAction, double now, bool allowMaskedKnight = false)
    {
        var masked = allowMaskedKnight && row.HasMaskedKnightEvidence;
        if (!IsFresh(row, now) || (row.Issues.Count != 0 && !masked)
            || (row.Features.Count != BotRecordingContract.LegacyWidth && row.Features.Count != BotRecordingContract.FeatureNames.Count)
            || row.Features.Any(value => !double.IsFinite(value) || Math.Abs(value) > 4)
            || row.Features[6] != 1 || (!masked && row.Features[4] < .5))
        {
            Reset();
            return null;
        }

        // A delayed or duplicate observation cannot extend the active control lease.
        if (last is { } consumed && row.AvailableAt <= consumed.AvailableAt) return null;
        var continuous = last is { } previous && previous.Generation == row.Generation && previous.Room == row.RoomId
            && row.AvailableAt - previous.AvailableAt <= BotRecordingContract.MaximumCaptureAge;
        if (!continuous) frames.Clear();

        var values = Encode(row, continuous ? previousAction : default, continuous, masked);
        if (values.Count != width) throw new InvalidOperationException("Bot observation fields do not match the policy schema.");
        if (frames.Count == BotRecordingContract.HistoryLength) frames.RemoveAt(0);
        frames.Add(values.ToArray());
        last = (row.Generation, row.RoomId, row.AvailableAt);

        var result = new float[width * BotRecordingContract.HistoryLength];
        var cursor = (BotRecordingContract.HistoryLength - frames.Count) * width;
        foreach (var frame in frames)
        foreach (var value in frame)
            result[cursor++] = (float)value;
        return result;
    }

    public static bool IsFresh(BotCaptureObservation row, double now) =>
        now - row.AvailableAt is >= 0 and <= MaximumDeliveryAge
        && now - row.CapturedAt is >= 0 and <= BotRecordingContract.MaximumCaptureAge
        && row.AvailableAt - row.CapturedAt is >= 0 and <= BotRecordingContract.MaximumCaptureAge
        && row.DetectionTimestamp is { } detected
        && row.CaptureTimestamp - detected is >= 0 and <= BotRecordingContract.MaximumDetectionAge;

    private List<double> Encode(BotCaptureObservation row, BotPolicyAction previous, bool continuous, bool masked)
    {
        var compact = version >= 13;
        var authored = row.TerrainSource == "human-authored";
        var terrain = authored ? row.AuthoredTerrain : row.DetectedTerrain;
        var values = new List<double>(width);
        values.AddRange(row.Features.Take(compact ? TerrainOffset : BotRecordingContract.LegacyWidth));
        if (compact)
            values.AddRange(row.Features.Skip(EnemyOffset).Take(BotRecordingContract.LegacyWidth - EnemyOffset));
        else if (version >= 2)
        {
            if (masked || (authored && terrain is null) || row.Features.Count == BotRecordingContract.LegacyWidth)
                values.AddRange(new double[BotRecordingContract.FeatureNames.Count - BotRecordingContract.LegacyWidth]);
            else
                values.AddRange(row.Features.Skip(BotRecordingContract.LegacyWidth));
        }

        values[8] = continuous ? 0 : 1;
        values[9] = previous.Horizontal == 1 ? -1 : previous.Horizontal == 2 ? 1 : 0;
        values[10] = previous.Aim == 1 ? 1 : previous.Aim == 2 ? -1 : 0;
        values[11] = previous.Jump == 1 ? 1 : 0;
        values[12] = previous.Attack == 1 ? 1 : 0;
        values[13] = values[14] = values[15] = 0;
        if (version >= 4 && row.Blockade is { Mark: { Crossed: false, Direction: -1 or 1 } } obstacle
            && obstacle.Dx is >= -1 and <= 1 && obstacle.Dy is >= -1 and <= 1)
        {
            values[13] = obstacle.Dx;
            values[14] = obstacle.Dy;
            values[15] = obstacle.Mark.Direction;
        }

        if (!compact && version >= 3) values.AddRange(BotPolicyGeometry.Raster(terrain, doors: false));
        if (version >= 5) values.AddRange(Nearest(row.GeoCaches, row.Features));
        if (version >= 6) AppendAtlas(values, row.AtlasView, version >= 7);
        var road = (row.HackerPathVersion == 1 || row.AtlasPathVersion == 1 || row.PathProgress is not null)
            && row.RoadHint is { Dx: >= -1 and <= 1, Dy: >= -1 and <= 1, Clearance: >= 0 and <= 1, Distance: >= 0 and <= 1 }
            ? row.RoadHint : null;
        if (version >= 8) values.AddRange(road is null ? [0, 0, 0, 0, 0, 0] : [1, road.Dx, road.Dy, road.Clearance, road.Distance, road.Frontier ? 1 : 0]);
        if (version >= 9 && !compact) values.AddRange(Nearest(row.JoniBlessings, row.Features));
        if (version >= 10) values.AddRange(Nearest(row.LifebloodCocoons, row.Features));
        if (version >= 11 && !compact) values.AddRange(BotPolicyGeometry.Raster(terrain, doors: true));
        if (version >= 12)
            values.AddRange(row.HackerPathVersion == 1 && road is not null && (road.Dx != 0 || road.Dy != 0)
                ? [1, road.Dx, road.Dy] : [0, 0, 0]);
        if (!compact) return values;

        var marks = BotPolicyGeometry.Markings(terrain, values[0], values[1], version);
        values.AddRange(marks);
        if (version >= BotPolicyContract.GroundContactVersion) values.Add(row.Grounded is true ? 1 : row.Grounded is false ? -1 : 0);
        if (version == BotPolicyContract.LandingVersion)
        {
            ReadOnlySpan<double> old = frames.Count == 0 ? [] : frames[^1].AsSpan(width - BotPolicyGeometry.LandingCount);
            values.AddRange(BotPolicyGeometry.Landing(marks, values[3], row.Grounded, old));
        }
        return values;
    }

    private static void AppendAtlas(List<double> values, BotAtlasView? view, bool includeMap)
    {
        if (view is null || !double.IsFinite(view.Left) || !double.IsFinite(view.Right))
        {
            values.AddRange(new double[includeMap ? 21 : 5]);
            return;
        }
        values.AddRange([1, Math.Clamp(view.Left, 0, 1), Math.Clamp(view.Right, 0, 1),
            view.Health is null ? 0 : 1, Math.Clamp(view.Health ?? 0, 0, 16) / 16d]);
        if (!includeMap) return;
        if (view.Missing is { Count: 15 } coverage && coverage.All(value => value is >= 0 and <= 1))
        {
            values.Add(1);
            values.AddRange(coverage);
        }
        else values.AddRange(new double[16]);
    }

    private static double[] Nearest(IReadOnlyList<BotObjectBounds>? objects, IReadOnlyList<double> features)
    {
        if (features[4] <= 0 || objects is not { Count: > 0 }) return [0, 0, 0];
        var x = features[0];
        var y = features[1];
        var nearest = objects.Where(item => item is { X: >= 0 and <= 4, Y: >= 0 and <= 4, Width: >= 0 and <= 4, Height: >= 0 and <= 4 })
            .MinBy(item => Square(item.X - x) + Square(item.Y - y));
        return nearest is null ? [0, 0, 0] : [nearest.X - x, nearest.Y - y, 1];

        static double Square(double n) => n * n;
    }
}
