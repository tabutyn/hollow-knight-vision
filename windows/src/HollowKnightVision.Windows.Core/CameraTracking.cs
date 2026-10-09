namespace HollowKnightVision.Windows.Core;

public readonly record struct CameraVector(double X, double Y)
{
    public static readonly CameraVector Zero = new(0, 0);
}

public sealed record CameraSample(int Id, AtlasPoint Position, float Confidence);

public enum CameraUpdateState
{
    Accepted,
    HeldLowConfidence,
    HeldSceneChange,
    Invalid
}

public sealed record CameraUpdate(
    CameraUpdateState State,
    CameraVector RawStep,
    CameraVector AcceptedStep,
    AtlasPoint Position);

public sealed class LiveRegistrationContinuity
{
    public const int SoftFailureLimit = 3;
    public int ConsecutiveSoftFailures { get; private set; }

    public void Reset() => ConsecutiveSoftFailures = 0;

    public bool ShouldEnterRecovery(CameraUpdate? update, bool hasGameplaySignal)
    {
        if (!hasGameplaySignal)
        {
            ConsecutiveSoftFailures++;
            return ConsecutiveSoftFailures >= SoftFailureLimit;
        }
        switch (update?.State)
        {
            case CameraUpdateState.Accepted:
                ConsecutiveSoftFailures = 0;
                return false;
            case CameraUpdateState.HeldSceneChange:
            case CameraUpdateState.Invalid:
                ConsecutiveSoftFailures = 0;
                return true;
            default:
                ConsecutiveSoftFailures++;
                return ConsecutiveSoftFailures >= SoftFailureLimit;
        }
    }
}

public sealed class FreshAtlasBootstrapGate
{
    public const int RequiredStableSamples = 45;
    public const double MaximumPoseStep = 12;
    public const double MaximumStepChange = 8;
    public const double MaximumBootstrapDisplacement = 6;
    private AtlasPoint? stabilityOrigin;
    private AtlasPoint? previousPosition;
    private CameraVector? previousStep;

    public int StableSampleCount { get; private set; }
    public bool IsReady { get; private set; }

    public void Reset()
    {
        StableSampleCount = 0;
        IsReady = false;
        stabilityOrigin = null;
        previousPosition = null;
        previousStep = null;
    }

    public bool AllowsFirstAtlasWrite(
        AtlasPoint? position,
        bool registrationAccepted,
        bool groundVerified,
        bool hasCommittedEvidence)
    {
        if (hasCommittedEvidence)
        {
            IsReady = true;
            return true;
        }
        if (!registrationAccepted || !groundVerified || position is null
            || !double.IsFinite(position.Value.X) || !double.IsFinite(position.Value.Y))
        {
            Reset();
            return false;
        }
        if (IsReady) return true;
        if (previousPosition is null)
        {
            BeginWindow(position.Value);
            return false;
        }

        var step = new CameraVector(
            position.Value.X - previousPosition.Value.X,
            position.Value.Y - previousPosition.Value.Y);
        previousPosition = position;
        var continuousPose = CameraMath.Length(step.X, step.Y) <= MaximumPoseStep;
        var continuousMotion = previousStep is null
            || CameraMath.Length(
                step.X - previousStep.Value.X,
                step.Y - previousStep.Value.Y) <= MaximumStepChange;
        var stayedNearOrigin = stabilityOrigin is not null
            && CameraMath.Length(
                position.Value.X - stabilityOrigin.Value.X,
                position.Value.Y - stabilityOrigin.Value.Y) <= MaximumBootstrapDisplacement;
        if (!continuousPose || !continuousMotion || !stayedNearOrigin)
        {
            BeginWindow(position.Value);
            return false;
        }
        previousStep = step;
        StableSampleCount++;
        IsReady = StableSampleCount >= RequiredStableSamples;
        return IsReady;
    }

    private void BeginWindow(AtlasPoint position)
    {
        StableSampleCount = 1;
        stabilityOrigin = position;
        previousPosition = position;
        previousStep = null;
    }
}

public sealed class CameraAccumulator
{
    private readonly List<CameraSample> samples = [new(0, new AtlasPoint(0, 0), 1)];
    private int nextId = 1;

    public AtlasPoint Position { get; private set; }
    public IReadOnlyList<CameraSample> Samples => samples;
    public CameraVector LastStep { get; private set; }
    public float MinimumConfidence { get; set; } = 0.10f;
    public double MaximumStepFraction { get; set; } = 0.18;
    public double MaximumVerticalStepFraction { get; set; } = 0.06;
    public double Smoothing { get; set; } = 0.65;
    public bool InvertMotion { get; set; }
    public int SampleLimit { get; set; } = 2_400;

    public CameraUpdate Ingest(
        double alignmentX,
        double alignmentY,
        float confidence,
        double frameWidth)
    {
        var direction = InvertMotion ? -1 : 1;
        var raw = new CameraVector(alignmentX * direction, alignmentY * direction);
        if (!double.IsFinite(raw.X) || !double.IsFinite(raw.Y) || !float.IsFinite(confidence))
        {
            return new CameraUpdate(
                CameraUpdateState.Invalid, raw, CameraVector.Zero, Position);
        }
        if (confidence < MinimumConfidence)
        {
            LastStep = CameraVector.Zero;
            return new CameraUpdate(
                CameraUpdateState.HeldLowConfidence, raw, CameraVector.Zero, Position);
        }
        if (Math.Abs(raw.X) > Math.Max(8, frameWidth * MaximumStepFraction)
            || Math.Abs(raw.Y) > Math.Max(8, frameWidth * MaximumVerticalStepFraction))
        {
            LastStep = CameraVector.Zero;
            return new CameraUpdate(
                CameraUpdateState.HeldSceneChange, raw, CameraVector.Zero, Position);
        }
        var accepted = new CameraVector(
            raw.X * Smoothing + LastStep.X * (1 - Smoothing),
            raw.Y * Smoothing + LastStep.Y * (1 - Smoothing));
        LastStep = accepted;
        if (CameraMath.Length(accepted.X, accepted.Y) >= 0.05)
        {
            Position = new AtlasPoint(Position.X + accepted.X, Position.Y + accepted.Y);
            samples.Add(new CameraSample(nextId++, Position, confidence));
            TrimSamples();
        }
        return new CameraUpdate(CameraUpdateState.Accepted, raw, accepted, Position);
    }

    public void Reset()
    {
        Position = new AtlasPoint(0, 0);
        samples.Clear();
        samples.Add(new CameraSample(0, Position, 1));
        LastStep = CameraVector.Zero;
        nextId = 1;
    }

    public void ApplyGlobalCorrection(AtlasPoint correctedPosition)
    {
        if (!double.IsFinite(correctedPosition.X) || !double.IsFinite(correctedPosition.Y)) return;
        var correction = CameraMath.Length(
            correctedPosition.X - Position.X,
            correctedPosition.Y - Position.Y);
        if (correction < 0.05) return;
        Position = correctedPosition;
        if (correction > 4) LastStep = CameraVector.Zero;
        if (correction >= 0.25)
        {
            samples.Add(new CameraSample(nextId++, Position, 1));
            TrimSamples();
        }
    }

    public void ApplyGlobalOffset(CameraVector correction)
    {
        if (!double.IsFinite(correction.X) || !double.IsFinite(correction.Y)) return;
        ApplyGlobalCorrection(new AtlasPoint(
            Position.X + correction.X,
            Position.Y + correction.Y));
    }

    private void TrimSamples()
    {
        if (SampleLimit <= 0) throw new InvalidOperationException("Sample limit must be positive.");
        if (samples.Count > SampleLimit) samples.RemoveRange(0, samples.Count - SampleLimit);
    }
}

internal static class CameraMath
{
    internal static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
}
