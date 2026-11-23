namespace HollowKnightVision.Windows.Core;

public sealed record BotKnightPrediction(double LastSeenAt, double Age, double Uncertainty,
    bool CameraCompensated, IReadOnlyList<string> ObservedIssues, bool? UsedWeakDetection = null)
{
    internal double MaximumAge => CameraCompensated ? .6 : .35;

    internal static bool Bridgeable(string issue) => issue is "missing-knight" or "stale-or-missing-detection";
}

/// <summary>Bridges brief visual occlusions using the last measured Knight and the current frame's geometry.</summary>
public sealed class BotVisualKnightMemory
{
    private sealed record Track(BotCaptureObservation Observation, double SeenAt, AtlasPoint? Camera,
        AtlasPoint Position, AtlasPoint? Velocity, double AccelerationY, double VelocityError, BotPolicyAction? Action);
    private Track? track;
    private (long Generation, int Room, long Coordinates, double Width, double Height)? chart;
    private double latestTimestamp = double.NegativeInfinity;

    public void Reset()
    {
        track = null;
        chart = null;
        latestTimestamp = double.NegativeInfinity;
    }

    public BotCaptureObservation Resolve(BotCaptureObservation raw, BotPolicyAction? previousAction = null,
        LiveObjectDetectionBatch? detections = null, long coordinateEpoch = 0)
    {
        if (raw.TerrainSource != "detected" || raw.KnightPrediction is not null
            || raw.VisualContext is not { FrameWidth: > 0 and < double.PositiveInfinity,
                FrameHeight: > 0 and < double.PositiveInfinity } context
            || raw.Features.Count != BotRecordingContract.FeatureNames.Count
            || raw.Features[6] != 1 || raw.Features.Any(value => !double.IsFinite(value))
            || !double.IsFinite(raw.CaptureTimestamp)
            || raw.AvailableAt - raw.CapturedAt is not (>= 0 and <= BotRecordingContract.MaximumCaptureAge))
        {
            Reset();
            return raw;
        }

        var currentChart = (raw.Generation, raw.RoomId, coordinateEpoch, context.FrameWidth, context.FrameHeight);
        if (chart != currentChart) Reset();
        if (raw.CaptureTimestamp <= latestTimestamp) return raw;
        chart = currentChart;
        latestTimestamp = raw.CaptureTimestamp;

        if (raw.Issues.Count == 0 && raw.KnightSource == "visual" && raw.Features[4] >= .5
            && raw.Features[2] is > 0 and < 1 && raw.Features[3] > 0 && raw.Features[3] < context.FrameHeight)
        {
            Remember(raw, context.Camera, previousAction);
            return raw;
        }
        if (!raw.Issues.Contains("missing-knight") || !raw.Issues.All(BotKnightPrediction.Bridgeable))
        {
            track = null;
            return raw;
        }
        if (track is not { } previous) return raw;

        var age = raw.CaptureTimestamp - previous.SeenAt;
        var elapsed = raw.CaptureTimestamp - previous.Observation.CaptureTimestamp;
        var compensated = previous.Camera.HasValue && context.Camera.HasValue;
        var horizon = compensated ? .6 : .35;
        if (age <= 0 || elapsed <= 0 || age + (raw.AvailableAt - raw.CapturedAt) >= horizon) return raw;

        var changedControl = previousAction is { } action && previous.Action is { } oldAction
            && (action.Horizontal != oldAction.Horizontal || action.Jump != oldAction.Jump);
        var uncertainty = .003 + (compensated ? .025 : .07) * age + .045 * age * age
            + previous.VelocityError * elapsed + (previous.Velocity is null ? .035 * age : 0)
            + (changedControl ? .018 : 0);
        if (uncertainty > .06) return raw;

        var camera = compensated ? context.Camera!.Value : previous.Camera.GetValueOrDefault();
        var velocity = previous.Velocity.GetValueOrDefault();
        var oldY = previous.Position.Y - camera.Y;
        var x = previous.Position.X + velocity.X * elapsed - camera.X;
        var y = oldY + velocity.Y * elapsed + .5 * previous.AccelerationY * elapsed * elapsed;
        var halfWidth = previous.Observation.Features[2] / 2;
        var halfHeight = previous.Observation.Features[3] / 2;
        if (x < halfWidth || x > 1 - halfWidth || y < halfHeight || y > context.FrameHeight - halfHeight) return raw;

        var lines = Floors(raw, context.FrameWidth);
        if (velocity.Y + previous.AccelerationY * elapsed <= 0)
        {
            var floor = lines.Where(line => line.Confidence >= .65 && x + halfWidth > line.MinimumX
                && x - halfWidth < line.MaximumX && oldY - halfHeight >= line.Y - .006 && y - halfHeight <= line.Y)
                .Select(line => (double?)line.Y).Max();
            if (floor.HasValue) y = floor.Value + halfHeight;
        }

        var cue = WeakKnightCenter(detections, raw, previous, context.FrameHeight, new(x, y));
        if (cue.HasValue) (x, y) = (cue.Value.X, cue.Value.Y);
        var features = raw.Features.ToArray();
        features[0] = x;
        features[1] = y;
        features[2] = 2 * halfWidth;
        features[3] = 2 * halfHeight;
        features[4] = Math.Max(.2, previous.Observation.Features[4] * (1 - .75 * age / horizon));
        features[5] = Math.Min(1, age / BotRecordingContract.MaximumDetectionAge);
        BotObservationBuilder.WriteFloorFeatures(features, lines, x, y, 1);
        for (var i = 96; i < BotRecordingContract.LegacyWidth; i += 6)
        {
            if (features[i + 5] <= 0) continue;
            features[i] += raw.Features[0] - x;
            features[i + 1] += raw.Features[1] - y;
        }
        if (raw.DetectedTerrain is { } terrain)
            BotObservationBuilder.WriteWallFeatures(features, terrain, x * terrain.Width, y * terrain.Width);

        return raw with
        {
            Features = features,
            Issues = [],
            KnightSource = "visual-predicted",
            EnemySource = raw.Issues.Contains("stale-or-missing-detection") ? "visual-unavailable" : raw.EnemySource,
            KnightPrediction = new(previous.SeenAt, age, uncertainty, compensated, raw.Issues, cue.HasValue ? true : null),
            Grounded = null
        };
    }

    private void Remember(BotCaptureObservation row, AtlasPoint? camera, BotPolicyAction? action)
    {
        if (row.DetectionTimestamp is not { } seenAt
            || row.CaptureTimestamp - seenAt is not (>= 0 and <= BotRecordingContract.MaximumDetectionAge))
        {
            track = null;
            return;
        }
        if (track?.Observation.ModelVersion != row.ModelVersion) track = null;
        if (track is { } saved && (seenAt <= saved.SeenAt
            || row.DetectionFrameId.HasValue && row.DetectionFrameId == saved.Observation.DetectionFrameId)) return;

        var position = new AtlasPoint(row.Features[0] + (camera?.X ?? 0), row.Features[1] + (camera?.Y ?? 0));
        AtlasPoint? velocity = null;
        var acceleration = 0d;
        var error = 0d;
        if (track is { } old && camera.HasValue == old.Camera.HasValue && seenAt - old.SeenAt is >= .025 and <= .35)
        {
            var dt = seenAt - old.SeenAt;
            var vx = (position.X - old.Position.X) / dt;
            var vy = (position.Y - old.Position.Y) / dt;
            if (Math.Abs(vx) <= .8 && Math.Abs(vy) <= 1.5)
            {
                velocity = new(vx, vy);
                if (old.Velocity is { } prior)
                {
                    acceleration = Math.Clamp((vy - prior.Y) / dt, -4, 0);
                    error = Math.Min(.15, .1 * Math.Sqrt(Math.Pow(vx - prior.X, 2) + Math.Pow(vy - prior.Y, 2)));
                }
            }
        }
        track = new(row, seenAt, camera, position, velocity, acceleration, error, action);
    }

    private static BotFloor[] Floors(BotCaptureObservation raw, double width) =>
        raw.DetectedTerrain is { } terrain
            ? terrain.Floors.Where(line => double.IsFinite(line.MinimumX) && double.IsFinite(line.MaximumX)
                && double.IsFinite(line.Y) && line.Confidence is >= 0 and <= 1 && line.MaximumX > line.MinimumX)
                .Select(line => new BotFloor(line.MinimumX / width, line.MaximumX / width, line.Y / width, line.Confidence)).ToArray()
            : Enumerable.Range(0, 16).Select(i => 16 + i * 5).Where(i => raw.Features[i + 4] > 0)
                .Select(i => new BotFloor(raw.Features[0] + raw.Features[i], raw.Features[0] + raw.Features[i + 1],
                    raw.Features[1] + raw.Features[i + 2], raw.Features[i + 3])).ToArray();

    private static AtlasPoint? WeakKnightCenter(LiveObjectDetectionBatch? batch, BotCaptureObservation row,
        Track previous, double frameHeight, AtlasPoint estimate)
    {
        // DetectionTimestamp is already translated to the capture clock by the observation builder.
        if (batch is null || batch.CaptureGeneration != row.Generation || batch.SourceFrameIdentifier != row.DetectionFrameId
            || row.DetectionTimestamp is not { } seen || seen <= previous.SeenAt
            || row.CaptureTimestamp - seen is not (>= 0 and <= .05)) return null;
        var width = previous.Observation.Features[2];
        var height = previous.Observation.Features[3];
        AtlasPoint? best = null;
        var confidence = 0f;
        foreach (var cue in batch.Detections)
        {
            if (cue.Confidence is not (>= .16f and < .5f) || cue.Confidence <= confidence
                || cue.SourceFrameIdentifier != batch.SourceFrameIdentifier
                || cue.ModelVersion?.DisplayName != previous.Observation.ModelVersion
                || LabelingClassIdentity.CanonicalIdentifier(cue.ClassIdentifier) != LiveObjectDetectionPostprocessor.Knight) continue;
            var box = cue.Box;
            var x = box.X + box.Width / 2;
            var y = (1 - box.Y - box.Height / 2) * frameHeight;
            if (box.Width / width is not (>= .42 and <= 2.25) || box.Height * frameHeight / height is not (>= .42 and <= 2.25)
                || !double.IsFinite(x) || Math.Abs(x - estimate.X) > 2 * height || x < width / 2 || x > 1 - width / 2
                || !double.IsFinite(y) || Math.Abs(y - estimate.Y) > 4 * height || y < height / 2 || y > frameHeight - height / 2)
                continue;
            best = new(x, y);
            confidence = cue.Confidence;
        }
        return best;
    }
}
