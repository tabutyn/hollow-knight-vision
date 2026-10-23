namespace HollowKnightVision.Windows.Core;

public static class PresentationPosePrediction
{
    public static CameraVector Velocity(
        AtlasPoint oldPosition,
        double? oldTime,
        AtlasPoint newPosition,
        double time,
        bool continuous)
    {
        if (!continuous || oldTime is not { } prior || time <= prior || time - prior > .1)
            return CameraVector.Zero;
        var dx = newPosition.X - oldPosition.X;
        var dy = newPosition.Y - oldPosition.Y;
        if (CameraMath.Length(dx, dy) > 24) return CameraVector.Zero;
        var dt = time - prior;
        var speed = CameraMath.Length(dx, dy) / dt;
        return speed <= 1_000 ? new CameraVector(dx / dt, dy / dt) : CameraVector.Zero;
    }

    public static AtlasPoint Position(
        AtlasPoint measured,
        CameraVector velocity,
        double? measuredAt,
        double presentedAt)
    {
        if (measuredAt is not { } time || presentedAt < time || presentedAt - time > .1)
            return measured;
        var dt = Math.Min(.05, presentedAt - time);
        var dx = velocity.X * dt;
        var dy = velocity.Y * dt;
        var scale = Math.Min(1, 8 / Math.Max(.001, CameraMath.Length(dx, dy)));
        return new AtlasPoint(measured.X + dx * scale, measured.Y + dy * scale);
    }
}

public static class GroundMotionEvidence
{
    public static bool IsMeasured(
        bool poseVerified,
        bool hasConfirmedGround,
        int localTextureSupport,
        int inlierCount,
        int globalMatchCount) =>
        poseVerified && hasConfirmedGround
        && (localTextureSupport > 0 || inlierCount > 0 || globalMatchCount > 0);
}

public static class CaptureGroundReliability
{
    public const double MaximumAge = .12;

    public static bool IsReliable(
        bool? groundAnchored,
        double? poseTimestamp,
        double frameTimestamp,
        bool frameAdmitsTracking)
    {
        if (groundAnchored != true || !frameAdmitsTracking
            || poseTimestamp is not { } pose
            || !double.IsFinite(pose) || !double.IsFinite(frameTimestamp)) return false;
        var age = frameTimestamp - pose;
        return age >= 0 && age <= MaximumAge;
    }
}

public static class TransitionPosePublication
{
    public static bool ReplacesTrackingState(
        bool roomOwnershipChanged,
        bool coarseMotionIsControlling) =>
        roomOwnershipChanged || coarseMotionIsControlling;
}

public readonly record struct LowResolutionCameraVelocitySeed(
    CameraVector Velocity,
    double Timestamp);

public static class FloorlessMotionHandoff
{
    public const double MaximumSeedAge = .48;
    public const double MaximumPoseAge = .25;
    public const double MaximumPredictedTravelFraction = .18;

    public static LowResolutionCameraVelocitySeed? Seed(
        CameraVector? velocity,
        double? measuredAt,
        double presentedAt)
    {
        if (velocity is not { } value || measuredAt is not { } time
            || !double.IsFinite(value.X) || !double.IsFinite(value.Y)
            || !double.IsFinite(time) || !double.IsFinite(presentedAt)) return null;
        var age = presentedAt - time;
        return age is >= 0 and <= MaximumSeedAge
            ? new LowResolutionCameraVelocitySeed(value, time)
            : null;
    }

    public static AtlasPoint Position(
        AtlasPoint measured,
        double? measuredAt,
        LowResolutionCameraVelocitySeed? seed,
        double presentedAt,
        double solveWidth)
    {
        if (measuredAt is not { } time || seed is not { } velocitySeed
            || !double.IsFinite(measured.X) || !double.IsFinite(measured.Y)
            || !double.IsFinite(time) || !double.IsFinite(presentedAt)
            || !double.IsFinite(solveWidth) || solveWidth <= 0) return measured;
        var age = presentedAt - time;
        if (age < 0 || age > MaximumPoseAge) return measured;
        var dx = velocitySeed.Velocity.X * age;
        var dy = velocitySeed.Velocity.Y * age;
        var distance = CameraMath.Length(dx, dy);
        var maximumTravel = Math.Max(8, solveWidth * MaximumPredictedTravelFraction);
        if (distance > maximumTravel)
        {
            var scale = maximumTravel / distance;
            dx *= scale;
            dy *= scale;
        }
        return new AtlasPoint(measured.X + dx, measured.Y + dy);
    }
}

public enum FloorlessPoseCandidateSource
{
    CoarseCaptureRate,
    MaskedRegistration
}

public readonly record struct FloorlessPoseCandidate(
    AtlasPoint Position,
    FloorlessPoseCandidateSource Source);

public static class FloorlessPoseCandidateSelector
{
    private const double StationaryTolerance = 1;
    private const double VerticalPreferenceThreshold = 2;

    public static FloorlessPoseCandidate? Select(
        AtlasPoint current,
        AtlasPoint? coarse,
        AtlasPoint? masked,
        VisualRoomDirection? expectedDirection,
        bool coarseIsPlaceMatch)
    {
        if (!Finite(current)) return null;
        var coarseCandidate = Candidate(
            coarse, FloorlessPoseCandidateSource.CoarseCaptureRate, current);
        var maskedCandidate = Candidate(
            masked, FloorlessPoseCandidateSource.MaskedRegistration, current);
        if (coarseIsPlaceMatch && coarseCandidate is not null) return coarseCandidate;
        if (expectedDirection is null)
        {
            if (coarseCandidate is { } coarseValue)
            {
                var step = Step(current, coarseValue.Position);
                if (Math.Abs(step.Y) >= VerticalPreferenceThreshold
                    && Math.Abs(step.Y) > Math.Abs(step.X)) return coarseValue;
            }
            return maskedCandidate ?? coarseCandidate;
        }
        var directional = new[] { coarseCandidate, maskedCandidate }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Where(value => Agrees(Step(current, value.Position).X,
                expectedDirection.Value))
            .OrderByDescending(value => Math.Abs(Step(current, value.Position).X))
            .ToArray();
        if (directional.FirstOrDefault() is { } strongest && directional.Length > 0)
            return strongest;
        if (maskedCandidate is { } stoppedMasked
            && Math.Abs(Step(current, stoppedMasked.Position).X) <= StationaryTolerance)
            return stoppedMasked;
        if (coarseCandidate is { } stoppedCoarse
            && Math.Abs(Step(current, stoppedCoarse.Position).X) <= StationaryTolerance)
            return stoppedCoarse;
        return null;
    }

    private static FloorlessPoseCandidate? Candidate(
        AtlasPoint? position,
        FloorlessPoseCandidateSource source,
        AtlasPoint current) =>
        position is { } value && Finite(value)
            && double.IsFinite(CameraMath.Length(value.X - current.X, value.Y - current.Y))
            ? new FloorlessPoseCandidate(value, source)
            : null;

    private static CameraVector Step(AtlasPoint current, AtlasPoint candidate) =>
        new(candidate.X - current.X, candidate.Y - current.Y);

    private static bool Agrees(double step, VisualRoomDirection direction) =>
        direction == VisualRoomDirection.Left
            ? step < -StationaryTolerance
            : step > StationaryTolerance;

    private static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}
