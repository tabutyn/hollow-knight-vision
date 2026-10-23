namespace HollowKnightVision.Windows.Core;

public readonly record struct PlaybackFrameSize(double Width, double Height);

public readonly record struct ConnectedGroundTruthCameraSample(
    int FrameKey,
    string SceneName,
    AtlasPoint CameraPosition,
    double ObservedAt);

/// <summary>
/// Converts scene-local mod camera coordinates into the same connected pixel
/// space used by the Hacker atlas. New scenes join at the Knight's projected
/// doorway point; returning to a known scene reuses its original anchor.
/// </summary>
public sealed class ConnectedGroundTruthCameraTracker
{
    private sealed record PlayableReference(
        AtlasPoint CameraPosition,
        AtlasPoint? HeroScreenPoint,
        CameraVector Velocity,
        bool FacingRight);

    private readonly Dictionary<string, AtlasPoint> anchors = [];
    private PlayableReference? lastPlayableReference;

    public void Reset()
    {
        anchors.Clear();
        lastPlayableReference = null;
    }

    public ConnectedGroundTruthCameraSample? Observe(
        ReceiverGroundTruthSample sample,
        double observedAt,
        PlaybackFrameSize frameSize)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!TryCameraScale(sample, frameSize, out var scaleX, out var scaleY))
        {
            return null;
        }

        var rawPosition = new AtlasPoint(
            sample.CameraX * scaleX,
            sample.CameraY * scaleY);
        var heroPoint = sample.HeroAvailable
            ? HeroScreenPoint(sample, frameSize)
            : null;
        var suppliesProjection = sample.HeroScreenX is not null
            && sample.HeroScreenY is not null
            && sample.ProjectionPixelWidth is > 0
            && sample.ProjectionPixelHeight is > 0;
        var canInitializeScene = sample.HeroAvailable
            && (heroPoint is not null || !suppliesProjection);

        if (!anchors.TryGetValue(sample.SceneName, out var anchor))
        {
            if (!canInitializeScene) return null;
            var origin = TransitionOrigin(frameSize, heroPoint);
            anchor = new AtlasPoint(
                rawPosition.X - origin.X,
                rawPosition.Y - origin.Y);
            anchors[sample.SceneName] = anchor;
        }

        var connected = new AtlasPoint(
            rawPosition.X - anchor.X,
            rawPosition.Y - anchor.Y);
        if (canInitializeScene)
        {
            lastPlayableReference = new PlayableReference(
                connected,
                heroPoint,
                new CameraVector(sample.VelocityX, sample.VelocityY),
                sample.FacingRight);
        }
        return new ConnectedGroundTruthCameraSample(
            FrameKey(sample.UnityFrame),
            sample.SceneName,
            connected,
            observedAt);
    }

    /// <summary>Matches the 24-bit value decoded by <see cref="RenderedFrameMarker"/>.</summary>
    public static int FrameKey(long unityFrame) =>
        (int)(unchecked((ulong)unityFrame) & 0x00FF_FFFFUL);

    private AtlasPoint TransitionOrigin(
        PlaybackFrameSize frameSize,
        AtlasPoint? heroScreenPoint)
    {
        if (lastPlayableReference is not { } previous) return default;
        if (previous.HeroScreenPoint is { } oldHero
            && heroScreenPoint is { } newHero)
        {
            return new AtlasPoint(
                previous.CameraPosition.X + oldHero.X - newHero.X,
                previous.CameraPosition.Y + oldHero.Y - newHero.Y);
        }

        var overlapX = Math.Min(
            32,
            Math.Round(frameSize.Width * 0.1, MidpointRounding.AwayFromZero));
        var overlapY = Math.Min(
            24,
            Math.Round(frameSize.Height * 0.1, MidpointRounding.AwayFromZero));
        if (Math.Abs(previous.Velocity.Y) > Math.Abs(previous.Velocity.X)
            && Math.Abs(previous.Velocity.Y) > 0.05)
        {
            return new AtlasPoint(
                previous.CameraPosition.X,
                previous.CameraPosition.Y + (previous.Velocity.Y > 0
                    ? frameSize.Height - overlapY
                    : -frameSize.Height + overlapY));
        }
        var exitsRight = Math.Abs(previous.Velocity.X) > 0.05
            ? previous.Velocity.X > 0
            : previous.FacingRight;
        return new AtlasPoint(
            previous.CameraPosition.X + (exitsRight
                ? frameSize.Width - overlapX
                : -frameSize.Width + overlapX),
            previous.CameraPosition.Y);
    }

    private static AtlasPoint? HeroScreenPoint(
        ReceiverGroundTruthSample sample,
        PlaybackFrameSize frameSize)
    {
        if (sample.HeroScreenX is not { } x
            || sample.HeroScreenY is not { } y
            || sample.ProjectionPixelWidth is not > 0
            || sample.ProjectionPixelHeight is not > 0)
        {
            return null;
        }
        var point = new AtlasPoint(
            x * frameSize.Width / sample.ProjectionPixelWidth.Value,
            y * frameSize.Height / sample.ProjectionPixelHeight.Value);
        return point.X >= 0 && point.X <= frameSize.Width
            && point.Y >= 0 && point.Y <= frameSize.Height
            ? point
            : null;
    }

    private static bool TryCameraScale(
        ReceiverGroundTruthSample sample,
        PlaybackFrameSize frameSize,
        out double scaleX,
        out double scaleY)
    {
        scaleX = 0;
        scaleY = 0;
        if (!sample.CameraAvailable
            || frameSize.Width <= 1
            || frameSize.Height <= 1
            || sample.ProjectionPixelWidth is not > 0
            || sample.ProjectionPixelHeight is not > 0)
        {
            return false;
        }
        var rawY = sample.PixelsPerWorldUnitY
            ?? (sample.OrthographicSize > 0
                ? sample.ProjectionPixelHeight.Value / (2 * sample.OrthographicSize)
                : 0);
        var rawX = sample.PixelsPerWorldUnitX ?? rawY;
        scaleX = rawX * frameSize.Width / sample.ProjectionPixelWidth.Value;
        scaleY = rawY * frameSize.Height / sample.ProjectionPixelHeight.Value;
        return double.IsFinite(scaleX) && double.IsFinite(scaleY)
            && scaleX > 0 && scaleY > 0;
    }
}

public enum PlaybackDivergenceCause
{
    PoseError,
    VisionStall
}

public sealed record PlaybackDivergenceDecision(
    PlaybackDivergenceCause Cause,
    double ErrorPixels,
    double ThresholdPixels,
    string SceneName,
    int ConsecutiveSamples,
    double? SilenceSeconds);

/// <summary>
/// Exact-frame watchdog used only by controlled replay evaluation. Ground truth
/// never feeds the visual solver; sustained disagreement releases replay input.
/// </summary>
public sealed class PlaybackDivergenceMonitor
{
    private sealed record VisionSample(
        AtlasPoint CameraPosition,
        double ObservedAt,
        bool IsTransitioning);

    public const double ThresholdPixels = 160;
    public const int RequiredConsecutiveSamples = 12;
    public const double StartupGrace = 5;
    public const double SceneChangeGrace = 0.25;
    public const double MaximumVisionSilence = 0.75;
    public const double MinimumTruthTravelDuringSilence = 48;
    private const int Capacity = 240;

    private readonly object sync = new();
    private readonly ConnectedGroundTruthCameraTracker connector = new();
    private readonly Dictionary<int, ConnectedGroundTruthCameraSample> truth = [];
    private readonly Dictionary<int, VisionSample> vision = [];
    private readonly List<int> truthOrder = [];
    private readonly List<int> visionOrder = [];
    private bool accepting;
    private bool evaluating;
    private double startedAt;
    private AtlasPoint? baselineOffset;
    private string? lastSceneName;
    private double sceneGraceUntil;
    private int consecutiveDivergentSamples;
    private AtlasPoint? watchdogTruthPosition;
    private double? watchdogObservedAt;
    private string? watchdogSceneName;
    private int unmatchedTruthSamples;
    private bool triggered;
    private PlaybackDivergenceDecision? lastDecision;

    public bool IsAcceptingSamples
    {
        get
        {
            lock (sync) return accepting;
        }
    }

    public PlaybackDivergenceDecision? LastDecision
    {
        get
        {
            lock (sync) return lastDecision;
        }
    }

    public void Prepare()
    {
        lock (sync)
        {
            connector.Reset();
            ClearPairs();
            accepting = true;
            evaluating = false;
            startedAt = 0;
            baselineOffset = null;
            lastSceneName = null;
            sceneGraceUntil = 0;
            consecutiveDivergentSamples = 0;
            ClearVisionWatchdog();
            triggered = false;
            lastDecision = null;
        }
    }

    public void BeginEvaluation(double timestamp)
    {
        lock (sync)
        {
            if (!accepting) return;
            connector.Reset();
            ClearPairs();
            evaluating = true;
            startedAt = timestamp;
            baselineOffset = null;
            lastSceneName = null;
            sceneGraceUntil = timestamp + StartupGrace;
            consecutiveDivergentSamples = 0;
            ClearVisionWatchdog();
            triggered = false;
            lastDecision = null;
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            connector.Reset();
            ClearPairs();
            accepting = false;
            evaluating = false;
            baselineOffset = null;
            lastSceneName = null;
            consecutiveDivergentSamples = 0;
            ClearVisionWatchdog();
            triggered = false;
            lastDecision = null;
        }
    }

    public PlaybackDivergenceDecision? AppendGroundTruth(
        ReceiverGroundTruthSample sample,
        double observedAt,
        PlaybackFrameSize frameSize)
    {
        lock (sync)
        {
            if (!accepting) return null;
            var connected = connector.Observe(sample, observedAt, frameSize);
            if (connected is not { } groundTruth) return null;
            var key = groundTruth.FrameKey;
            Insert(groundTruth, key, truth, truthOrder);
            if (!vision.Remove(key, out var captured))
            {
                return EvaluateVisionSilence(groundTruth);
            }
            visionOrder.Remove(key);
            truth.Remove(key);
            truthOrder.Remove(key);
            return Evaluate(groundTruth, captured);
        }
    }

    public PlaybackDivergenceDecision? AppendVision(
        int frameKey,
        AtlasPoint cameraPosition,
        double observedAt,
        bool isTransitioning)
    {
        lock (sync)
        {
            if (!accepting) return null;
            var captured = new VisionSample(
                cameraPosition,
                observedAt,
                isTransitioning);
            Insert(captured, frameKey, vision, visionOrder);
            if (!truth.Remove(frameKey, out var connected)) return null;
            truthOrder.Remove(frameKey);
            vision.Remove(frameKey);
            visionOrder.Remove(frameKey);
            return Evaluate(connected, captured);
        }
    }

    private PlaybackDivergenceDecision? Evaluate(
        ConnectedGroundTruthCameraSample groundTruth,
        VisionSample visual)
    {
        if (!evaluating || triggered) return null;
        watchdogTruthPosition = groundTruth.CameraPosition;
        watchdogObservedAt = Math.Max(groundTruth.ObservedAt, visual.ObservedAt);
        watchdogSceneName = groundTruth.SceneName;
        unmatchedTruthSamples = 0;
        if (baselineOffset is null)
        {
            baselineOffset = new AtlasPoint(
                visual.CameraPosition.X - groundTruth.CameraPosition.X,
                visual.CameraPosition.Y - groundTruth.CameraPosition.Y);
            lastSceneName = groundTruth.SceneName;
            return null;
        }
        if (groundTruth.SceneName != lastSceneName)
        {
            lastSceneName = groundTruth.SceneName;
            sceneGraceUntil = visual.ObservedAt + SceneChangeGrace;
            consecutiveDivergentSamples = 0;
            return null;
        }
        if (visual.ObservedAt < startedAt + StartupGrace
            || visual.ObservedAt < sceneGraceUntil
            || visual.IsTransitioning)
        {
            consecutiveDivergentSamples = 0;
            return null;
        }

        var offset = baselineOffset.Value;
        var error = Math.Sqrt(
            Math.Pow(visual.CameraPosition.X - offset.X - groundTruth.CameraPosition.X, 2)
            + Math.Pow(visual.CameraPosition.Y - offset.Y - groundTruth.CameraPosition.Y, 2));
        consecutiveDivergentSamples = error > ThresholdPixels
            ? consecutiveDivergentSamples + 1
            : 0;
        if (consecutiveDivergentSamples < RequiredConsecutiveSamples) return null;
        triggered = true;
        lastDecision = new PlaybackDivergenceDecision(
            PlaybackDivergenceCause.PoseError,
            error,
            ThresholdPixels,
            groundTruth.SceneName,
            consecutiveDivergentSamples,
            null);
        return lastDecision;
    }

    private PlaybackDivergenceDecision? EvaluateVisionSilence(
        ConnectedGroundTruthCameraSample groundTruth)
    {
        if (!evaluating || triggered) return null;
        unmatchedTruthSamples++;
        if (watchdogSceneName != groundTruth.SceneName
            || watchdogTruthPosition is not { } referencePosition
            || watchdogObservedAt is not { } referenceObservedAt)
        {
            watchdogTruthPosition = groundTruth.CameraPosition;
            watchdogObservedAt = groundTruth.ObservedAt;
            watchdogSceneName = groundTruth.SceneName;
            unmatchedTruthSamples = 1;
            return null;
        }
        var silence = groundTruth.ObservedAt - referenceObservedAt;
        if (groundTruth.ObservedAt < startedAt + StartupGrace
            || silence < MaximumVisionSilence)
        {
            return null;
        }
        var travel = Math.Sqrt(
            Math.Pow(groundTruth.CameraPosition.X - referencePosition.X, 2)
            + Math.Pow(groundTruth.CameraPosition.Y - referencePosition.Y, 2));
        if (travel < MinimumTruthTravelDuringSilence) return null;
        triggered = true;
        lastDecision = new PlaybackDivergenceDecision(
            PlaybackDivergenceCause.VisionStall,
            travel,
            MinimumTruthTravelDuringSilence,
            groundTruth.SceneName,
            unmatchedTruthSamples,
            silence);
        return lastDecision;
    }

    private static void Insert<T>(
        T value,
        int key,
        Dictionary<int, T> values,
        List<int> order)
    {
        if (!values.ContainsKey(key)) order.Add(key);
        values[key] = value;
        while (order.Count > Capacity)
        {
            values.Remove(order[0]);
            order.RemoveAt(0);
        }
    }

    private void ClearPairs()
    {
        truth.Clear();
        vision.Clear();
        truthOrder.Clear();
        visionOrder.Clear();
    }

    private void ClearVisionWatchdog()
    {
        watchdogTruthPosition = null;
        watchdogObservedAt = null;
        watchdogSceneName = null;
        unmatchedTruthSamples = 0;
    }
}
