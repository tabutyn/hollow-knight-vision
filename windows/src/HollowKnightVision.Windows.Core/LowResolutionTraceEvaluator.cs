namespace HollowKnightVision.Windows.Core;

public enum LowResolutionTraceEvaluatorError
{
    MissingPersistedWorldEvidence
}

public sealed class LowResolutionTraceEvaluatorException : Exception
{
    public LowResolutionTraceEvaluatorException(LowResolutionTraceEvaluatorError error)
        : base(error.ToString()) => Error = error;

    public LowResolutionTraceEvaluatorError Error { get; }
}

public sealed record LowResolutionTraceErrorStatistics(
    int Count, double? Rms, double? P50, double? P95, double? Maximum);

public sealed record LowResolutionTraceWorstMotion(
    double Offset,
    string SceneName,
    double ExpectedDx,
    double ExpectedDy,
    double? PredictedDx,
    double? PredictedDy,
    double ErrorPixels);

public sealed record LowResolutionTraceRejectedTravelSummary(
    int Comparisons,
    LowResolutionTraceErrorStatistics ExpectedDistancePixels,
    double SignedExpectedX,
    double SignedExpectedY,
    double AbsoluteExpectedX,
    double AbsoluteExpectedY);

public sealed record LowResolutionTraceBridgeEvent(
    double Offset,
    string SceneName,
    string? ExpectedDirection,
    string Rejection,
    double ExpectedDx,
    double ExpectedDy,
    double? BridgedDx,
    double? BridgedDy);

public sealed record LowResolutionTraceGroundLossEvent(
    double Offset,
    string SceneName,
    string? ExpectedDirection,
    string? Rejection,
    double ExpectedDx,
    double ExpectedDy,
    double? MeasuredDx,
    double? MeasuredDy,
    double? BridgedDx,
    double? BridgedDy);

public sealed record LowResolutionTraceMotionSummary(
    int Comparisons,
    int RepresentableComparisons,
    int AcceptedComparisons,
    int PredictedAcceptedComparisons,
    int AcceptedRepresentableComparisons,
    int RejectedRepresentableComparisons,
    int DirectionGateRejections,
    IReadOnlyDictionary<string, int> TranslationRejections,
    IReadOnlyDictionary<string, LowResolutionTraceRejectedTravelSummary> RejectedTravelByReason,
    int OutOfRangeComparisons,
    int AcceptedOutOfRangeComparisons,
    double? RepresentableCoveragePercent,
    double? HorizontalDirectionAgreementPercent,
    LowResolutionTraceErrorStatistics VectorErrorPixels,
    LowResolutionTraceErrorStatistics IntegratedDriftPixels,
    double? HorizontalBiasPixels,
    double? VerticalBiasPixels,
    LowResolutionTraceWorstMotion? WorstMotion);

public sealed record LowResolutionTracePlaceSummary(
    int Matches,
    int MatchesOver24Pixels,
    LowResolutionTraceErrorStatistics ErrorPixels);

public sealed record LowResolutionTraceConnectedCameraSummary(
    string Source,
    int ExactFramePairs,
    IReadOnlyList<string> SceneNames,
    LowResolutionTraceErrorStatistics ErrorPixels,
    IReadOnlyDictionary<string, LowResolutionTraceErrorStatistics> SceneErrorPixels,
    LowResolutionTraceErrorStatistics JoinedSceneErrorPixels,
    IReadOnlyDictionary<string, double> FirstErrorOverPixelsAtOffset,
    double? MaximumErrorOffset,
    string? MaximumErrorScene,
    double? JoinedSceneFirstOffset,
    double? JoinedSceneFirstErrorX,
    double? JoinedSceneFirstErrorY,
    double? EndErrorX,
    double? EndErrorY);

public sealed record LowResolutionTraceEvaluationReport(
    int SchemaVersion,
    Guid PathId,
    int? ReplayIteration,
    int TraceFrames,
    int ValidTraceFrames,
    int ExactTruthFrames,
    double? ExactTruthCoveragePercent,
    int GroundReliableFrames,
    int GroundUnverifiedFrames,
    LowResolutionTraceErrorStatistics TraceIntervalSeconds,
    bool Evaluable,
    double SolveWidth,
    double MotionMinimumImprovement,
    LowResolutionTraceMotionSummary AllMotion,
    LowResolutionTraceMotionSummary GroundLossMotion,
    LowResolutionTraceMotionSummary GroundLossBridgedMotion,
    IReadOnlyList<LowResolutionTraceBridgeEvent> GroundLossBridgeEvents,
    IReadOnlyList<LowResolutionTraceGroundLossEvent> GroundLossEvents,
    LowResolutionTracePlaceSummary OracleSeededPlaceRecognition,
    LowResolutionTraceConnectedCameraSummary ConnectedCamera,
    LowResolutionTraceConnectedCameraSummary ConnectedTrackingCamera,
    IReadOnlyList<string> Notes)
{
    public const int CurrentSchemaVersion = 10;
}

/// <summary>
/// Replays recorded 64x36 matcher input through the production translation
/// implementation. Receiver truth is joined only by the exact rendered frame.
/// </summary>
public static class LowResolutionTraceEvaluator
{
    private sealed record ComparableFrame(
        RecordedLowResolutionFrame Recorded,
        LowResolutionMotionGrid Grid,
        ReceiverGroundTruthSample Truth,
        AtlasPoint TruthPosition,
        AtlasPoint? ObservedVisionPosition,
        double PixelScale,
        VisualRoomDirection? ExpectedDirection);

    private sealed class RejectedTravelAccumulator
    {
        private readonly List<double> distances = [];
        private double signedX;
        private double signedY;
        private double absoluteX;
        private double absoluteY;

        public void Append(double dx, double dy)
        {
            distances.Add(CameraMath.Length(dx, dy));
            signedX += dx;
            signedY += dy;
            absoluteX += Math.Abs(dx);
            absoluteY += Math.Abs(dy);
        }

        public LowResolutionTraceRejectedTravelSummary Summary() => new(
            distances.Count,
            Statistics(distances),
            signedX,
            signedY,
            absoluteX,
            absoluteY);
    }

    private sealed class MotionAccumulator
    {
        private int comparisons;
        private int representable;
        private int accepted;
        private int predictedAccepted;
        private int acceptedRepresentable;
        private int rejectedRepresentable;
        private int directionGateRejections;
        private readonly Dictionary<string, int> translationRejections = [];
        private readonly Dictionary<string, RejectedTravelAccumulator> rejectedTravel = [];
        private int outOfRange;
        private int acceptedOutOfRange;
        private readonly List<double> errors = [];
        private readonly List<double> integratedErrors = [];
        private readonly List<double> xErrors = [];
        private readonly List<double> yErrors = [];
        private int horizontalDirections;
        private int matchingHorizontalDirections;
        private LowResolutionTraceWorstMotion? worst;
        private double integratedExpectedX;
        private double integratedExpectedY;
        private double integratedPredictedX;
        private double integratedPredictedY;

        public void ResetIntegration()
        {
            integratedExpectedX = 0;
            integratedExpectedY = 0;
            integratedPredictedX = 0;
            integratedPredictedY = 0;
        }

        public void Append(
            ComparableFrame current,
            double expectedDx,
            double expectedDy,
            LowResolutionMotionVector? motion,
            bool isRepresentable,
            bool rejectedByDirectionGate,
            LowResolutionTranslationRejection? translationRejection,
            bool isPredicted = false)
        {
            comparisons++;
            if (isRepresentable)
            {
                representable++;
                integratedExpectedX += expectedDx;
                integratedExpectedY += expectedDy;
            }
            else
            {
                outOfRange++;
                ResetIntegration();
            }

            if (motion is null)
            {
                if (isRepresentable) rejectedRepresentable++;
                if (rejectedByDirectionGate)
                {
                    directionGateRejections++;
                    if (isRepresentable) Rejected("directionGate").Append(expectedDx, expectedDy);
                }
                if (translationRejection is { } rejection)
                {
                    var key = RejectionKey(rejection);
                    translationRejections[key] = translationRejections.GetValueOrDefault(key) + 1;
                    if (isRepresentable && !rejectedByDirectionGate)
                        Rejected(key).Append(expectedDx, expectedDy);
                }
                if (isRepresentable)
                {
                    integratedErrors.Add(CameraMath.Length(
                        integratedPredictedX - integratedExpectedX,
                        integratedPredictedY - integratedExpectedY));
                }
                ReplaceWorst(current, expectedDx, expectedDy, null, null,
                    CameraMath.Length(expectedDx, expectedDy));
                return;
            }

            accepted++;
            if (isPredicted) predictedAccepted++;
            if (isRepresentable) acceptedRepresentable++;
            else acceptedOutOfRange++;
            var predictedDx = -motion.ScreenShiftX * current.PixelScale;
            var predictedDy = motion.ScreenShiftY * current.PixelScale;
            if (isRepresentable)
            {
                integratedPredictedX += predictedDx;
                integratedPredictedY += predictedDy;
                integratedErrors.Add(CameraMath.Length(
                    integratedPredictedX - integratedExpectedX,
                    integratedPredictedY - integratedExpectedY));
            }
            var xError = predictedDx - expectedDx;
            var yError = predictedDy - expectedDy;
            var error = CameraMath.Length(xError, yError);
            errors.Add(error);
            xErrors.Add(xError);
            yErrors.Add(yError);
            if (Math.Abs(expectedDx) >= 1 && Math.Abs(predictedDx) >= .25)
            {
                horizontalDirections++;
                if ((expectedDx < 0) == (predictedDx < 0)) matchingHorizontalDirections++;
            }
            ReplaceWorst(current, expectedDx, expectedDy, predictedDx, predictedDy, error);
        }

        public LowResolutionTraceMotionSummary Summary() => new(
            comparisons,
            representable,
            accepted,
            predictedAccepted,
            acceptedRepresentable,
            rejectedRepresentable,
            directionGateRejections,
            new Dictionary<string, int>(translationRejections),
            rejectedTravel.ToDictionary(item => item.Key, item => item.Value.Summary()),
            outOfRange,
            acceptedOutOfRange,
            representable > 0 ? acceptedRepresentable / (double)representable * 100 : null,
            horizontalDirections > 0
                ? matchingHorizontalDirections / (double)horizontalDirections * 100 : null,
            Statistics(errors),
            Statistics(integratedErrors),
            Mean(xErrors),
            Mean(yErrors),
            worst);

        private RejectedTravelAccumulator Rejected(string key)
        {
            if (!rejectedTravel.TryGetValue(key, out var value))
                rejectedTravel[key] = value = new RejectedTravelAccumulator();
            return value;
        }

        private void ReplaceWorst(
            ComparableFrame current,
            double expectedDx,
            double expectedDy,
            double? predictedDx,
            double? predictedDy,
            double error)
        {
            if (worst is not null && error <= worst.ErrorPixels) return;
            worst = new LowResolutionTraceWorstMotion(
                current.Recorded.Offset,
                current.Truth.SceneName,
                expectedDx,
                expectedDy,
                predictedDx,
                predictedDy,
                error);
        }
    }

    public static LowResolutionTraceEvaluationReport Evaluate(RecordedInputPath path) =>
        Evaluate(path, LowResolutionRoomMotionTracker.MinimumImprovement);

    public static LowResolutionTraceEvaluationReport Evaluate(
        RecordedInputPath path,
        double minimumImprovement)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!double.IsFinite(minimumImprovement) || minimumImprovement is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumImprovement));
        return Evaluate(path, path.LowResolutionFrameTrace, minimumImprovement, []);
    }

    public static LowResolutionTraceEvaluationReport EvaluatePersistedWorld(
        RecordedInputPath path,
        string worldRootDirectory,
        double minimumImprovement = LowResolutionRoomMotionTracker.MinimumImprovement)
    {
        if (!double.IsFinite(minimumImprovement) || minimumImprovement is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumImprovement));
        return Evaluate(
            path,
            PersistedWorldFrames(path, worldRootDirectory),
            minimumImprovement,
            ["64x36 input was reconstructed from persisted masked atlas PNG evidence."]);
    }

    public static IReadOnlyList<RecordedLowResolutionFrame> PersistedWorldFrames(
        RecordedInputPath path,
        string worldRootDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldRootDirectory);
        var store = new SceneSessionStore(worldRootDirectory);
        var createdAt = path.CreatedAt.ToUnixTimeMilliseconds() / 1000d;
        var candidates = store.Manifest.Observations.Where(observation =>
            observation.Timestamp >= createdAt - .25
            && observation.Timestamp <= createdAt + path.Duration + 1.5).ToArray();
        if (candidates.Length == 0) throw MissingWorldEvidence();
        var first = candidates[0];
        var trackingByFrame = new Dictionary<int, RecordedTrackingSample>();
        foreach (var sample in path.TrackingSamples)
        {
            if (sample.RenderedGameFrame is { } frame
                && sample.CaptureOffset is { } offset && double.IsFinite(offset))
                trackingByFrame[frame] = sample;
        }
        if (trackingByFrame.Count == 0) throw MissingWorldEvidence();

        var result = new List<RecordedLowResolutionFrame>();
        foreach (var observation in candidates)
        {
            var offset = observation.Timestamp - first.Timestamp;
            if (offset < -.05 || offset > path.Duration + .25) continue;
            BgraFrame image;
            try { image = store.Source(observation); }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or SceneSessionStoreException) { continue; }
            var renderedFrame = RenderedFrameMarker.Decode(image);
            if (renderedFrame is not { } frame
                || !trackingByFrame.TryGetValue(frame, out var tracking)
                || tracking.CaptureOffset is not { } trackingOffset
                || !double.IsFinite(trackingOffset)
                || FrameRegionRenderer.SignalProfile(image).MotionGrid is not { } grid) continue;
            result.Add(new RecordedLowResolutionFrame
            {
                Offset = trackingOffset,
                CaptureTimestamp = tracking.CaptureTimestamp,
                RenderedGameFrame = frame,
                RoomId = tracking.RoomId ?? 0,
                Width = grid.Width,
                Height = grid.Height,
                Luma = grid.Luma.ToArray(),
                GroundTrackingReliable = IsMeasured(tracking)
            });
        }
        if (result.Count == 0) throw MissingWorldEvidence();
        return result;
    }

    private static LowResolutionTraceEvaluationReport Evaluate(
        RecordedInputPath path,
        IReadOnlyList<RecordedLowResolutionFrame> frames,
        double minimumImprovement,
        IReadOnlyList<string> additionalNotes)
    {
        var groundByFrame = new Dictionary<int, bool>();
        foreach (var tracking in path.TrackingSamples)
            if (tracking.RenderedGameFrame is { } frame) groundByFrame[frame] = IsMeasured(tracking);
        var orderedFrames = frames.Select(frame =>
            frame.GroundTrackingReliable
                && frame.RenderedGameFrame is { } rendered
                && groundByFrame.TryGetValue(rendered, out var measured)
                && !measured
                    ? frame with { GroundTrackingReliable = false }
                    : frame)
            .OrderBy(frame => frame.Offset)
            .ToArray();
        var solveWidth = 640d;
        if (path.RuntimeMetadata?.TryGetValue("captureWidth", out var widthText) == true
            && double.TryParse(widthText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedWidth)
            && double.IsFinite(parsedWidth) && parsedWidth > 0) solveWidth = parsedWidth;

        var truthByFrame = new Dictionary<long, ReceiverGroundTruthSample>();
        foreach (var recorded in path.GroundTruthTrace)
            if (recorded.Sample.CameraAvailable && recorded.Sample.HasFiniteCoordinates())
                truthByFrame[recorded.Sample.UnityFrame] = recorded.Sample;
        var observedByFrame = new Dictionary<int, AtlasPoint>();
        foreach (var tracking in path.TrackingSamples)
        {
            if (tracking.RenderedGameFrame is not { } frame) continue;
            var x = tracking.PublishedCameraX ?? tracking.CameraX;
            var y = tracking.PublishedCameraY ?? tracking.CameraY;
            if (x is { } finiteX && y is { } finiteY
                && double.IsFinite(finiteX) && double.IsFinite(finiteY))
                observedByFrame[frame] = new AtlasPoint(finiteX, finiteY);
        }
        var traceHeight = orderedFrames.Length > 0
            ? solveWidth * orderedFrames[0].Height / Math.Max(1d, orderedFrames[0].Width)
            : solveWidth * 9 / 16;
        var connectedCamera = ConnectedCameraSummary(path, solveWidth, traceHeight, false);
        var connectedTracking = path.CoarseMotionTrace.Count == 0
            ? connectedCamera
            : ConnectedCameraSummary(path, solveWidth, traceHeight, true);
        var seeds = VelocitySeeds(path.TrackingSamples);

        var comparable = new List<ComparableFrame>(frames.Count);
        var validFrameCount = 0;
        var events = path.Events.OrderBy(item => item.Offset).ToArray();
        var eventIndex = 0;
        var leftHeld = false;
        var rightHeld = false;
        foreach (var frame in orderedFrames)
        {
            while (eventIndex < events.Length && events[eventIndex].Offset <= frame.Offset)
            {
                var item = events[eventIndex++];
                if (item.Button == RecordedGameButton.Left)
                    leftHeld = item.Transition == RecordedInputTransition.Pressed;
                else if (item.Button == RecordedGameButton.Right)
                    rightHeld = item.Transition == RecordedInputTransition.Pressed;
            }
            VisualRoomDirection? direction = leftHeld == rightHeld
                ? null
                : leftHeld ? VisualRoomDirection.Left : VisualRoomDirection.Right;
            if (frame.Width <= 0 || frame.Height <= 0
                || frame.Luma.LongLength != (long)frame.Width * frame.Height) continue;
            validFrameCount++;
            if (frame.RenderedGameFrame is not { } rendered
                || !truthByFrame.TryGetValue(rendered, out var truth)
                || TruthPosition(truth, solveWidth,
                    solveWidth * frame.Height / (double)frame.Width) is not { } position) continue;
            comparable.Add(new ComparableFrame(
                frame,
                new LowResolutionMotionGrid(frame.Width, frame.Height, frame.Luma.ToArray()),
                truth,
                position,
                observedByFrame.GetValueOrDefault(rendered),
                solveWidth / frame.Width,
                direction));
        }

        var allMotion = new MotionAccumulator();
        var groundLossMotion = new MotionAccumulator();
        var bridgedMotion = new MotionAccumulator();
        var odometry = new TraceTransitionOdometry();
        var bridgeEvents = new List<LowResolutionTraceBridgeEvent>();
        var groundLossEvents = new List<LowResolutionTraceGroundLossEvent>();
        var seedIndex = 0;
        LowResolutionCameraVelocitySeed? latestSeed = null;
        ComparableFrame? previous = null;
        var placeMemory = new TracePlaceMemory();
        var placeErrors = new List<double>();

        foreach (var current in comparable)
        {
            while (seedIndex < seeds.Count
                && seeds[seedIndex].Timestamp <= current.Recorded.CaptureTimestamp)
                latestSeed = seeds[seedIndex++];
            if (current.Recorded.GroundTrackingReliable)
                placeMemory.Remember(current.Grid, current.Recorded.RoomId,
                    current.TruthPosition, current.Recorded.CaptureTimestamp);
            else if (placeMemory.Observe(current.Grid, current.Recorded.RoomId,
                         solveWidth, current.Recorded.CaptureTimestamp) is { } match)
                placeErrors.Add(CameraMath.Length(
                    match.CameraPosition.X - current.TruthPosition.X,
                    match.CameraPosition.Y - current.TruthPosition.Y));

            var reference = previous;
            previous = current;
            if (reference is null || !ComparableEpoch(reference, current)
                || reference.Recorded.RoomId != current.Recorded.RoomId)
            {
                allMotion.ResetIntegration();
                groundLossMotion.ResetIntegration();
                bridgedMotion.ResetIntegration();
                odometry.Reset();
                continue;
            }
            var age = current.Recorded.CaptureTimestamp - reference.Recorded.CaptureTimestamp;
            if (age < LowResolutionRoomMotionTracker.ComparisonAgeMinimum
                || age > LowResolutionRoomMotionTracker.ComparisonAgeMaximum)
            {
                allMotion.ResetIntegration();
                groundLossMotion.ResetIntegration();
                bridgedMotion.ResetIntegration();
                odometry.Reset();
                continue;
            }
            var expectedDx = current.TruthPosition.X - reference.TruthPosition.X;
            var expectedDy = current.TruthPosition.Y - reference.TruthPosition.Y;
            var expectedShiftX = -expectedDx / current.PixelScale;
            var expectedShiftY = expectedDy / current.PixelScale;
            var representable = Math.Abs(expectedShiftX) <= LowResolutionRoomMotionTracker.MaximumShift
                && Math.Abs(expectedShiftY) <= LowResolutionRoomMotionTracker.MaximumVerticalShift;
            var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(
                reference.Grid, current.Grid, minimumImprovement);
            var rawMotion = diagnostic.Motion;
            var directionRejected = rawMotion is not null
                && !TraceTransitionOdometry.AcceptsDirection(rawMotion, current.ExpectedDirection);
            var motion = directionRejected ? null : rawMotion;
            allMotion.Append(current, expectedDx, expectedDy, motion, representable,
                directionRejected, diagnostic.FinalRejection);

            if (!current.Recorded.GroundTrackingReliable)
            {
                if (reference.Recorded.GroundTrackingReliable)
                {
                    groundLossMotion.ResetIntegration();
                    bridgedMotion.ResetIntegration();
                    odometry.Arm(reference.Grid, reference.Recorded.CaptureTimestamp);
                    odometry.PrimeHorizontalVelocity(latestSeed, current.ExpectedDirection);
                }
                groundLossMotion.Append(current, expectedDx, expectedDy, motion, representable,
                    directionRejected, diagnostic.FinalRejection);
                var beforeX = odometry.WorldDeltaX;
                var beforeY = odometry.WorldDeltaY;
                var step = odometry.Observe(current.Grid, current.Recorded.CaptureTimestamp,
                    solveWidth, current.ExpectedDirection);
                LowResolutionMotionVector? bridge = step is null ? null : new(
                    -(odometry.WorldDeltaX - beforeX) / current.PixelScale,
                    (odometry.WorldDeltaY - beforeY) / current.PixelScale,
                    0);
                var rejection = directionRejected
                    ? "directionGate"
                    : diagnostic.FinalRejection is { } reason ? RejectionKey(reason) : null;
                groundLossEvents.Add(new LowResolutionTraceGroundLossEvent(
                    current.Recorded.Offset,
                    current.Truth.SceneName,
                    DirectionKey(current.ExpectedDirection),
                    rejection,
                    expectedDx,
                    expectedDy,
                    motion is null ? null : -motion.ScreenShiftX * current.PixelScale,
                    motion is null ? null : motion.ScreenShiftY * current.PixelScale,
                    bridge is null ? null : -bridge.ScreenShiftX * current.PixelScale,
                    bridge is null ? null : bridge.ScreenShiftY * current.PixelScale));
                if (motion is null)
                {
                    bridgeEvents.Add(new LowResolutionTraceBridgeEvent(
                        current.Recorded.Offset,
                        current.Truth.SceneName,
                        DirectionKey(current.ExpectedDirection),
                        rejection ?? "unknown",
                        expectedDx,
                        expectedDy,
                        bridge is null ? null : -bridge.ScreenShiftX * current.PixelScale,
                        bridge is null ? null : bridge.ScreenShiftY * current.PixelScale));
                }
                bridgedMotion.Append(current, expectedDx, expectedDy, bridge, representable,
                    bridge is null && directionRejected,
                    bridge is null ? diagnostic.FinalRejection : null,
                    bridge is not null && motion is null);
            }
            else
            {
                groundLossMotion.ResetIntegration();
                bridgedMotion.ResetIntegration();
                odometry.Reset();
            }
        }

        return new LowResolutionTraceEvaluationReport(
            LowResolutionTraceEvaluationReport.CurrentSchemaVersion,
            path.Id,
            path.ReplayIteration,
            frames.Count,
            validFrameCount,
            comparable.Count,
            validFrameCount > 0 ? comparable.Count / (double)validFrameCount * 100 : null,
            comparable.Count(item => item.Recorded.GroundTrackingReliable),
            comparable.Count(item => !item.Recorded.GroundTrackingReliable),
            Statistics(comparable.Zip(comparable.Skip(1), (prior, current) =>
                    current.Recorded.CaptureTimestamp - prior.Recorded.CaptureTimestamp)
                .Where(value => double.IsFinite(value) && value >= 0)),
            comparable.Count >= 2,
            solveWidth,
            minimumImprovement,
            allMotion.Summary(),
            groundLossMotion.Summary(),
            bridgedMotion.Summary(),
            bridgeEvents,
            groundLossEvents,
            new LowResolutionTracePlaceSummary(
                placeErrors.Count,
                placeErrors.Count(value => value > 24),
                Statistics(placeErrors)),
            connectedCamera,
            connectedTracking,
            new[]
            {
                "Motion truth uses exact rendered Unity frames; no nearest-time substitution.",
                "Place memory is seeded with Hacker camera poses on frames marked ground-reliable.",
                "Connected-camera error keeps one route-start baseline across scene changes.",
                "connectedCamera is capture-rate presentation; connectedTrackingCamera is the atlas-producing Vision stream."
            }.Concat(additionalNotes).ToArray());
    }

    private static IReadOnlyList<LowResolutionCameraVelocitySeed> VelocitySeeds(
        IReadOnlyList<RecordedTrackingSample> samples)
    {
        var ordered = samples.OrderBy(sample => sample.CaptureTimestamp).ToArray();
        var result = new List<LowResolutionCameraVelocitySeed>();
        for (var index = 1; index < ordered.Length; index++)
        {
            var prior = ordered[index - 1];
            var current = ordered[index];
            if (!IsMeasured(prior) || !IsMeasured(current)
                || prior.PoseSource != "ground" || current.PoseSource != "ground"
                || prior.RoomId != current.RoomId || prior.RoomRevision != current.RoomRevision
                || current.GlobalCorrectionX is not null || current.GlobalCorrectionY is not null)
                continue;
            var oldX = prior.PublishedCameraX ?? prior.CameraX;
            var oldY = prior.PublishedCameraY ?? prior.CameraY;
            var newX = current.PublishedCameraX ?? current.CameraX;
            var newY = current.PublishedCameraY ?? current.CameraY;
            if (oldX is not { } ox || oldY is not { } oy
                || newX is not { } nx || newY is not { } ny) continue;
            var velocity = PresentationPosePrediction.Velocity(
                new AtlasPoint(ox, oy), prior.CaptureTimestamp,
                new AtlasPoint(nx, ny), current.CaptureTimestamp, true);
            if (velocity.X != 0 || velocity.Y != 0)
                result.Add(new LowResolutionCameraVelocitySeed(velocity, current.CaptureTimestamp));
        }
        return result;
    }

    private static LowResolutionTraceConnectedCameraSummary ConnectedCameraSummary(
        RecordedInputPath path,
        double solveWidth,
        double solveHeight,
        bool forceTracking)
    {
        var connector = new ConnectedGroundTruthCameraTracker();
        var truthByFrame = new Dictionary<int, ConnectedGroundTruthCameraSample>();
        foreach (var recorded in path.GroundTruthTrace.OrderBy(item => item.Offset))
        {
            var connected = connector.Observe(recorded.Sample, recorded.ReceivedTimestamp,
                new PlaybackFrameSize(solveWidth, solveHeight));
            if (connected is { } value) truthByFrame[value.FrameKey] = value;
        }
        var presented = new List<(double Offset, int Frame, AtlasPoint Position)>();
        string source;
        if (forceTracking || path.CoarseMotionTrace.Count == 0)
        {
            source = "published-tracking";
            foreach (var sample in path.TrackingSamples)
                if (sample.RenderedGameFrame is { } frame
                    && sample.PublishedCameraX is { } x && sample.PublishedCameraY is { } y
                    && double.IsFinite(x) && double.IsFinite(y))
                    presented.Add((sample.CaptureOffset ?? sample.Offset, frame, new AtlasPoint(x, y)));
        }
        else
        {
            source = "capture-rate-coarse";
            foreach (var sample in path.CoarseMotionTrace)
                if (sample.RenderedGameFrame is { } frame
                    && double.IsFinite(sample.PresentedCameraX)
                    && double.IsFinite(sample.PresentedCameraY))
                    presented.Add((sample.Offset, frame,
                        new AtlasPoint(sample.PresentedCameraX, sample.PresentedCameraY)));
        }

        AtlasPoint? baseline = null;
        string? firstScene = null;
        var scenes = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<double>();
        var errorsByScene = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var joinedErrors = new List<double>();
        var thresholdOffsets = new Dictionary<string, double>(StringComparer.Ordinal);
        (double Value, double Offset, string Scene)? maximum = null;
        double? joinedOffset = null, joinedX = null, joinedY = null, endX = null, endY = null;
        foreach (var sample in presented.OrderBy(item => item.Offset))
        {
            if (!truthByFrame.TryGetValue(sample.Frame, out var truth)) continue;
            baseline ??= new AtlasPoint(
                sample.Position.X - truth.CameraPosition.X,
                sample.Position.Y - truth.CameraPosition.Y);
            firstScene ??= truth.SceneName;
            var x = sample.Position.X - baseline.Value.X - truth.CameraPosition.X;
            var y = sample.Position.Y - baseline.Value.Y - truth.CameraPosition.Y;
            var error = CameraMath.Length(x, y);
            errors.Add(error);
            if (!errorsByScene.TryGetValue(truth.SceneName, out var sceneErrors))
                errorsByScene[truth.SceneName] = sceneErrors = [];
            sceneErrors.Add(error);
            scenes.Add(truth.SceneName);
            if (truth.SceneName != firstScene)
            {
                joinedErrors.Add(error);
                if (joinedOffset is null)
                {
                    joinedOffset = sample.Offset;
                    joinedX = x;
                    joinedY = y;
                }
            }
            foreach (var threshold in new[] { 8, 16, 32, 64, 128, 256 })
                if (error > threshold) thresholdOffsets.TryAdd(threshold.ToString(), sample.Offset);
            if (maximum is null || error > maximum.Value.Value)
                maximum = (error, sample.Offset, truth.SceneName);
            endX = x;
            endY = y;
        }
        return new LowResolutionTraceConnectedCameraSummary(
            source,
            errors.Count,
            scenes.Order(StringComparer.Ordinal).ToArray(),
            Statistics(errors),
            errorsByScene.ToDictionary(item => item.Key, item => Statistics(item.Value),
                StringComparer.Ordinal),
            Statistics(joinedErrors),
            thresholdOffsets,
            maximum?.Offset,
            maximum?.Scene,
            joinedOffset,
            joinedX,
            joinedY,
            endX,
            endY);
    }

    private static bool ComparableEpoch(ComparableFrame first, ComparableFrame second) =>
        first.Truth.SessionId == second.Truth.SessionId
        && first.Truth.SceneName == second.Truth.SceneName
        && first.Truth.ProjectionPixelWidth == second.Truth.ProjectionPixelWidth
        && first.Truth.ProjectionPixelHeight == second.Truth.ProjectionPixelHeight
        && Math.Abs(first.Truth.OrthographicSize - second.Truth.OrthographicSize) < .000001;

    private static AtlasPoint? TruthPosition(
        ReceiverGroundTruthSample truth,
        double solveWidth,
        double solveHeight)
    {
        if (!truth.CameraAvailable || solveWidth <= 0 || solveHeight <= 0) return null;
        var fallback = truth.PixelsPerWorldUnit((int)Math.Round(solveHeight,
            MidpointRounding.AwayFromZero));
        var xScale = truth.PixelsPerWorldUnitX is > 0
            && truth.ProjectionPixelWidth is > 0
                ? truth.PixelsPerWorldUnitX.Value * solveWidth / truth.ProjectionPixelWidth.Value
                : fallback;
        var yScale = fallback;
        return xScale is { } x && yScale is { } y
            && double.IsFinite(x) && double.IsFinite(y)
                ? new AtlasPoint(truth.CameraX * x, truth.CameraY * y)
                : null;
    }

    private static bool IsMeasured(RecordedTrackingSample sample) =>
        GroundMotionEvidence.IsMeasured(
            sample.PoseVerified,
            sample.HasConfirmedGround,
            sample.LocalTextureSupport ?? 0,
            sample.InlierCount,
            sample.GlobalMatchCount);

    private static LowResolutionTraceEvaluatorException MissingWorldEvidence() =>
        new(LowResolutionTraceEvaluatorError.MissingPersistedWorldEvidence);

    private static string? DirectionKey(VisualRoomDirection? direction) => direction switch
    {
        VisualRoomDirection.Left => "left",
        VisualRoomDirection.Right => "right",
        _ => null
    };

    private static string RejectionKey(LowResolutionTranslationRejection rejection) => rejection switch
    {
        LowResolutionTranslationRejection.IncompatibleGrid => "incompatibleGrid",
        LowResolutionTranslationRejection.InsufficientTexture => "insufficientTexture",
        LowResolutionTranslationRejection.InsufficientOverlap => "insufficientOverlap",
        LowResolutionTranslationRejection.InsufficientImprovement => "insufficientImprovement",
        LowResolutionTranslationRejection.InsignificantSubcellMotion => "insignificantSubcellMotion",
        _ => throw new ArgumentOutOfRangeException(nameof(rejection))
    };

    private static LowResolutionTraceErrorStatistics Statistics(IEnumerable<double> source)
    {
        var values = source.ToArray();
        if (values.Length == 0) return new(0, null, null, null, null);
        var sorted = values.Order().ToArray();
        var rms = Math.Sqrt(values.Sum(value => value * value) / values.Length);
        return new(values.Length, rms, Percentile(sorted, .5), Percentile(sorted, .95), sorted[^1]);
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        var index = (int)Math.Round((sorted.Length - 1) * fraction, MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static double? Mean(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? null : values.Average();

    private sealed record TracePlaceMatch(int KeyframeId, AtlasPoint CameraPosition,
        double Score, double Margin);

    private sealed class TracePlaceMemory
    {
        private sealed record Entry(int Id, AtlasPoint CameraPosition, int Width, int Height,
            double[] NormalizedLuma, bool[]? Visible);
        private sealed record Pending(int KeyframeId, AtlasPoint CameraPosition, double Timestamp);
        private sealed record Alignment(int ShiftX, int ShiftY, double Score);
        private sealed record NormalizedGrid(double[] Values, double Deviation, bool[]? Visible);

        private const int MaximumEntriesPerRoom = 24;
        private const int MaximumFullAlignmentCandidates = 3;
        private const double MinimumEntryDistance = 80;
        private const double MinimumEntryInterval = .25;
        private const double SearchInterval = .09;
        private const double MaximumScore = .34;
        private const double MaximumSingleEntryScore = .20;
        private const double MaximumMaskedSingleEntryScore = .38;
        private const double MinimumMargin = .08;
        private const double ConfirmationMinimum = .08;
        private const double ConfirmationMaximum = .60;
        private const double ConfirmationDistance = 24;
        private const int MaximumAlignmentShift = 2;
        private const byte MinimumVisibleLuma = 20;
        private const double VisibilityMaskDarkFraction = .12;

        private readonly Dictionary<int, List<Entry>> entries = [];
        private readonly Dictionary<int, double> lastRememberedAt = [];
        private int nextId = 1;
        private double? lastSearchAt;
        private Pending? pending;

        public void Remember(LowResolutionMotionGrid grid, int roomId,
            AtlasPoint cameraPosition, double timestamp)
        {
            if (!double.IsFinite(timestamp) || !double.IsFinite(cameraPosition.X)
                || !double.IsFinite(cameraPosition.Y)
                || lastRememberedAt.TryGetValue(roomId, out var last)
                    && timestamp - last < MinimumEntryInterval) return;
            var normalized = Normalize(grid);
            if (normalized.Deviation < LowResolutionRoomMotionTracker.MinimumTextureDeviation) return;
            if (!entries.TryGetValue(roomId, out var roomEntries))
                entries[roomId] = roomEntries = [];
            if (roomEntries.Any(entry => CameraMath.Length(
                    entry.CameraPosition.X - cameraPosition.X,
                    entry.CameraPosition.Y - cameraPosition.Y) < MinimumEntryDistance)) return;
            roomEntries.Add(new Entry(nextId++, cameraPosition, grid.Width, grid.Height,
                normalized.Values, normalized.Visible));
            if (roomEntries.Count > MaximumEntriesPerRoom)
                roomEntries.RemoveAt(roomEntries.Count > 1 ? 1 : 0);
            lastRememberedAt[roomId] = timestamp;
        }

        public TracePlaceMatch? Observe(LowResolutionMotionGrid grid, int roomId,
            double solveWidth, double timestamp)
        {
            if (!double.IsFinite(timestamp) || !double.IsFinite(solveWidth) || solveWidth <= 0
                || lastSearchAt is { } last && timestamp - last < SearchInterval
                || !entries.TryGetValue(roomId, out var roomEntries) || roomEntries.Count == 0)
                return null;
            lastSearchAt = timestamp;
            var normalized = Normalize(grid);
            if (normalized.Deviation < LowResolutionRoomMotionTracker.MinimumTextureDeviation)
            {
                pending = null;
                return null;
            }
            var pixelScale = solveWidth / Math.Max(1, grid.Width);
            var shortlist = roomEntries
                .Where(entry => entry.Width == grid.Width && entry.Height == grid.Height)
                .Select(entry => (Entry: entry, Score: QuickScore(entry.NormalizedLuma,
                    normalized.Values, entry.Visible, normalized.Visible, grid.Width, grid.Height)))
                .OrderBy(item => item.Score)
                .Take(MaximumFullAlignmentCandidates);
            var ranked = shortlist
                .Select(item => (item.Entry,
                    Alignment: Align(item.Entry, normalized.Values, normalized.Visible)))
                .Where(item => item.Alignment is not null)
                .Select(item => (item.Entry, Alignment: item.Alignment!))
                .OrderBy(item => item.Alignment.Score)
                .ToArray();
            if (ranked.Length == 0)
            {
                pending = null;
                return null;
            }
            var best = ranked[0];
            double? secondScore = ranked.Length > 1 ? ranked[1].Alignment.Score : null;
            var margin = secondScore is { } second
                ? second - best.Alignment.Score : double.PositiveInfinity;
            var scoreLimit = secondScore is null
                ? normalized.Visible is null ? MaximumSingleEntryScore : MaximumMaskedSingleEntryScore
                : MaximumScore;
            if (best.Alignment.Score > scoreLimit
                || secondScore is not null && margin < MinimumMargin)
            {
                pending = null;
                return null;
            }
            var position = new AtlasPoint(
                best.Entry.CameraPosition.X - best.Alignment.ShiftX * pixelScale,
                best.Entry.CameraPosition.Y + best.Alignment.ShiftY * pixelScale);
            if (pending is not { } prior || prior.KeyframeId != best.Entry.Id
                || timestamp - prior.Timestamp < ConfirmationMinimum
                || timestamp - prior.Timestamp > ConfirmationMaximum
                || CameraMath.Length(prior.CameraPosition.X - position.X,
                    prior.CameraPosition.Y - position.Y) > ConfirmationDistance)
            {
                pending = new Pending(best.Entry.Id, position, timestamp);
                return null;
            }
            pending = null;
            return new TracePlaceMatch(best.Entry.Id, position, best.Alignment.Score, margin);
        }

        private static Alignment? Align(Entry reference, double[] current, bool[]? currentVisible)
        {
            if (reference.NormalizedLuma.Length != current.Length
                || reference.Width <= MaximumAlignmentShift * 2 + 8
                || reference.Height <= MaximumAlignmentShift * 2 + 8) return null;
            var xStart = MaximumAlignmentShift + 2;
            var xEnd = reference.Width - MaximumAlignmentShift - 2;
            var yStart = Math.Max(MaximumAlignmentShift, reference.Height / 6);
            var yEnd = Math.Min(reference.Height - MaximumAlignmentShift,
                reference.Height * 5 / 6);
            double Score(int shiftX, int shiftY)
            {
                double referenceSum = 0, currentSum = 0, referenceSquares = 0, currentSquares = 0;
                var count = 0;
                var available = 0;
                for (var y = yStart; y < yEnd; y++) for (var x = xStart; x < xEnd; x++)
                {
                    available++;
                    var referenceIndex = y * reference.Width + x;
                    var currentIndex = (y + shiftY) * reference.Width + x + shiftX;
                    if (reference.Visible?[referenceIndex] == false
                        || currentVisible?[currentIndex] == false) continue;
                    var rv = reference.NormalizedLuma[referenceIndex];
                    var cv = current[currentIndex];
                    referenceSum += rv; currentSum += cv;
                    referenceSquares += rv * rv; currentSquares += cv * cv;
                    count++;
                }
                if (count < Math.Max(24, available / 10)) return double.PositiveInfinity;
                var rMean = referenceSum / count;
                var cMean = currentSum / count;
                var rDeviation = Math.Sqrt(Math.Max(0, referenceSquares / count - rMean * rMean));
                var cDeviation = Math.Sqrt(Math.Max(0, currentSquares / count - cMean * cMean));
                if (rDeviation < .1 || cDeviation < .1) return double.PositiveInfinity;
                var error = 0d;
                for (var y = yStart; y < yEnd; y++) for (var x = xStart; x < xEnd; x++)
                {
                    var ri = y * reference.Width + x;
                    var ci = (y + shiftY) * reference.Width + x + shiftX;
                    if (reference.Visible?[ri] == false || currentVisible?[ci] == false) continue;
                    error += Math.Abs((reference.NormalizedLuma[ri] - rMean) / rDeviation
                        - (current[ci] - cMean) / cDeviation);
                }
                return error / count;
            }
            Alignment? best = null;
            for (var shiftY = -MaximumAlignmentShift; shiftY <= MaximumAlignmentShift; shiftY++)
                for (var shiftX = -MaximumAlignmentShift; shiftX <= MaximumAlignmentShift; shiftX++)
                {
                    var score = Score(shiftX, shiftY);
                    if (!double.IsFinite(score)) continue;
                    if (best is null || score < best.Score) best = new Alignment(shiftX, shiftY, score);
                }
            return best;
        }

        private static double QuickScore(double[] reference, double[] current,
            bool[]? referenceVisible, bool[]? currentVisible, int width, int height)
        {
            if (reference.Length != current.Length || reference.Length != width * height)
                return double.PositiveInfinity;
            double referenceSum = 0, currentSum = 0, referenceSquares = 0, currentSquares = 0;
            var count = 0;
            var available = 0;
            for (var y = Math.Max(2, height / 6); y < Math.Min(height - 2, height * 5 / 6); y += 3)
                for (var x = 4; x < width - 4; x += 4)
                {
                    available++;
                    var index = y * width + x;
                    if (referenceVisible?[index] == false || currentVisible?[index] == false) continue;
                    var rv = reference[index]; var cv = current[index];
                    referenceSum += rv; currentSum += cv;
                    referenceSquares += rv * rv; currentSquares += cv * cv;
                    count++;
                }
            if (count < Math.Max(12, available / 10)) return double.PositiveInfinity;
            var rMean = referenceSum / count;
            var cMean = currentSum / count;
            var rDeviation = Math.Sqrt(Math.Max(0, referenceSquares / count - rMean * rMean));
            var cDeviation = Math.Sqrt(Math.Max(0, currentSquares / count - cMean * cMean));
            if (rDeviation < .1 || cDeviation < .1) return double.PositiveInfinity;
            var error = 0d;
            for (var y = Math.Max(2, height / 6); y < Math.Min(height - 2, height * 5 / 6); y += 3)
                for (var x = 4; x < width - 4; x += 4)
                {
                    var index = y * width + x;
                    if (referenceVisible?[index] == false || currentVisible?[index] == false) continue;
                    error += Math.Abs((reference[index] - rMean) / rDeviation
                        - (current[index] - cMean) / cDeviation);
                }
            return error / count;
        }

        private static NormalizedGrid Normalize(LowResolutionMotionGrid grid)
        {
            var values = grid.Luma.Select(value => (double)value).ToArray();
            var usesMask = grid.Luma.Count(value => value <= MinimumVisibleLuma)
                / (double)Math.Max(1, grid.Luma.Length) >= VisibilityMaskDarkFraction;
            var visible = usesMask ? grid.Luma.Select(value => value > MinimumVisibleLuma).ToArray() : null;
            var retained = visible is null
                ? values : values.Where((_, index) => visible[index]).ToArray();
            if (retained.Length == 0) return new(values.Select(_ => 0d).ToArray(), 0, visible);
            var mean = retained.Average();
            var deviation = Math.Sqrt(retained.Sum(value => (value - mean) * (value - mean))
                / retained.Length);
            if (deviation <= .001)
                return new(values.Select(_ => 0d).ToArray(), deviation, visible);
            return new(LocalNormalize(values, grid.Width, grid.Height, visible), deviation, visible);
        }

        private static double[] LocalNormalize(double[] values, int width, int height, bool[]? visible)
        {
            if (width <= 0 || height <= 0 || values.Length != width * height)
                return values.Select(_ => 0d).ToArray();
            var output = new double[values.Length];
            const int radius = 2;
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (visible?[index] == false) continue;
                double sum = 0, squares = 0;
                var count = 0;
                for (var sy = Math.Max(0, y - radius); sy <= Math.Min(height - 1, y + radius); sy++)
                    for (var sx = Math.Max(0, x - radius); sx <= Math.Min(width - 1, x + radius); sx++)
                    {
                        var sample = sy * width + sx;
                        if (visible?[sample] == false) continue;
                        var value = values[sample];
                        sum += value; squares += value * value; count++;
                    }
                if (count < 6) continue;
                var mean = sum / count;
                var deviation = Math.Sqrt(Math.Max(0, squares / count - mean * mean));
                if (deviation >= 1) output[index] = (values[index] - mean) / deviation;
            }
            return output;
        }
    }

    private sealed class TraceTransitionOdometry
    {
        private sealed record Sample(LowResolutionMotionGrid Grid, double Timestamp);
        private sealed record Velocity(double WorldPixelsPerSecond, double Confidence);
        private const double MaximumOppositeDirectionSlackCells = 1.5;
        private const double PredictionHorizon = .48;
        private const int MaximumPredictedSteps = 9;
        private const int RetainedVelocitySamples = 3;
        private const double VerticalPredictionHorizon = .25;
        private const int MaximumVerticalPredictions = 2;
        private Sample? reference;
        private readonly List<Velocity> horizontalVelocities = [];
        private readonly List<Velocity> verticalVelocities = [];
        private double? lastMeasuredAt;
        private double? lastVerticalMeasuredAt;
        private VisualRoomDirection? lastExpectedDirection;
        private int predictedSteps;
        private int predictedVerticalSteps;
        private bool hasReliableVelocityLineage;

        public double WorldDeltaX { get; private set; }
        public double WorldDeltaY { get; private set; }

        public void Reset()
        {
            reference = null;
            WorldDeltaX = 0;
            WorldDeltaY = 0;
            horizontalVelocities.Clear();
            verticalVelocities.Clear();
            lastMeasuredAt = null;
            lastVerticalMeasuredAt = null;
            lastExpectedDirection = null;
            predictedSteps = 0;
            predictedVerticalSteps = 0;
            hasReliableVelocityLineage = false;
        }

        public void Arm(LowResolutionMotionGrid grid, double timestamp)
        {
            Reset();
            if (double.IsFinite(timestamp)) reference = new Sample(grid, timestamp);
        }

        public void PrimeHorizontalVelocity(LowResolutionCameraVelocitySeed? seed,
            VisualRoomDirection? expectedDirection)
        {
            if (seed is not { } value || !double.IsFinite(value.Timestamp)
                || !double.IsFinite(value.Velocity.X) || !double.IsFinite(value.Velocity.Y)) return;
            var seeded = false;
            if (expectedDirection is { } direction && Math.Abs(value.Velocity.X) is >= 5 and <= 1000
                && StepAgrees(value.Velocity.X, direction))
            {
                horizontalVelocities.Add(new Velocity(value.Velocity.X, .5));
                lastMeasuredAt = value.Timestamp;
                lastExpectedDirection = direction;
                seeded = true;
            }
            if (Math.Abs(value.Velocity.Y) is >= 5 and <= 1000)
            {
                verticalVelocities.Add(new Velocity(value.Velocity.Y, .5));
                lastVerticalMeasuredAt = value.Timestamp;
                seeded = true;
            }
            if (seeded) lastExpectedDirection = expectedDirection;
            hasReliableVelocityLineage = seeded;
        }

        public double? Observe(LowResolutionMotionGrid grid, double timestamp,
            double solveWidth, VisualRoomDirection? expectedDirection)
        {
            if (!double.IsFinite(timestamp) || !double.IsFinite(solveWidth) || solveWidth <= 0)
                return null;
            if (reference is not { } prior)
            {
                reference = new Sample(grid, timestamp);
                return null;
            }
            var age = timestamp - prior.Timestamp;
            if (age < LowResolutionRoomMotionTracker.ComparisonAgeMinimum) return null;
            reference = new Sample(grid, timestamp);
            if (age > LowResolutionRoomMotionTracker.ComparisonAgeMaximum)
            {
                ClearHorizontal(); ClearVertical(); return null;
            }
            if (expectedDirection != lastExpectedDirection)
            {
                ClearHorizontal();
                if (lastExpectedDirection is { } oldDirection
                    && expectedDirection is { } newDirection && oldDirection != newDirection)
                    ClearVertical();
                lastExpectedDirection = expectedDirection;
            }
            var pixelScale = solveWidth / Math.Max(1, grid.Width);
            var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(prior.Grid, grid);
            if (diagnostic.Motion is not { } motion)
            {
                if (diagnostic.FinalRejection is not (
                    LowResolutionTranslationRejection.InsufficientOverlap
                    or LowResolutionTranslationRejection.InsufficientImprovement)) return null;
                var horizontal = PredictedHorizontal(age, timestamp, pixelScale, expectedDirection);
                var vertical = PredictedVertical(age, timestamp, pixelScale);
                if (horizontal is null && vertical is null) return null;
                var x = horizontal?.Step ?? 0;
                var y = vertical?.Step ?? 0;
                WorldDeltaX += x; WorldDeltaY += y;
                if (horizontal is not null) predictedSteps++;
                if (vertical is not null) predictedVerticalSteps++;
                return x;
            }
            var stepX = -motion.ScreenShiftX * pixelScale;
            var stepY = motion.ScreenShiftY * pixelScale;
            if (!double.IsFinite(stepX) || !double.IsFinite(stepY)) return null;
            var predictedVertical = PredictedVertical(age, timestamp, pixelScale);
            var useVertical = predictedVertical is { } pv && ShouldUsePredictedVertical(stepY, pv.Step);
            var acceptedY = useVertical ? predictedVertical!.Value.Step : stepY;
            var predictedHorizontal = PredictedHorizontal(age, timestamp, pixelScale, expectedDirection);
            if (expectedDirection is { } direction && hasReliableVelocityLineage
                && !StepAgrees(stepX, direction) && predictedHorizontal is { } lineage)
                return AcceptPredicted(lineage, predictedVertical, useVertical, stepY, age, timestamp,
                    motion.Confidence);
            if (!AcceptsDirection(motion, expectedDirection))
            {
                if (predictedHorizontal is { } predicted)
                    return AcceptPredicted(predicted, predictedVertical, useVertical, stepY, age,
                        timestamp, motion.Confidence);
                ClearHorizontal();
                lastExpectedDirection = expectedDirection;
                return null;
            }
            var useHorizontal = predictedHorizontal is { } ph
                && ShouldUsePredictedHorizontal(stepX, ph.Step);
            var acceptedX = useHorizontal ? predictedHorizontal!.Value.Step : stepX;
            WorldDeltaX += acceptedX; WorldDeltaY += acceptedY;
            if (useHorizontal) predictedSteps++;
            else RememberHorizontal(stepX, age, timestamp, motion.Confidence, expectedDirection);
            if (useVertical) predictedVerticalSteps++;
            else RememberVertical(stepY, age, timestamp, motion.Confidence);
            return acceptedX;
        }

        public static bool AcceptsDirection(LowResolutionMotionVector motion,
            VisualRoomDirection? expectedDirection)
        {
            if (expectedDirection is not { } direction || Math.Abs(motion.ScreenShiftX) < .0001)
                return true;
            var cameraStepX = -motion.ScreenShiftX;
            var opposes = direction == VisualRoomDirection.Left
                ? cameraStepX >= 0 : cameraStepX <= 0;
            if (!opposes) return true;
            return Math.Abs(motion.ScreenShiftX) < Math.Abs(motion.ScreenShiftY)
                || Math.Abs(motion.ScreenShiftX) <= MaximumOppositeDirectionSlackCells;
        }

        private double AcceptPredicted((double Step, double Confidence) horizontal,
            (double Step, double Confidence)? vertical, bool useVertical, double measuredY,
            double age, double timestamp, double confidence)
        {
            var acceptedY = useVertical ? vertical!.Value.Step : measuredY;
            WorldDeltaX += horizontal.Step; WorldDeltaY += acceptedY;
            predictedSteps++;
            if (useVertical) predictedVerticalSteps++;
            else RememberVertical(measuredY, age, timestamp, confidence);
            return horizontal.Step;
        }

        private void RememberHorizontal(double step, double age, double timestamp,
            double confidence, VisualRoomDirection? expectedDirection)
        {
            if (!hasReliableVelocityLineage || expectedDirection is not { } direction || age <= 0
                || !StepAgrees(step, direction) || Math.Abs(step) < .25)
            {
                ClearHorizontal(); lastExpectedDirection = expectedDirection; return;
            }
            horizontalVelocities.Add(new Velocity(step / age, confidence));
            Trim(horizontalVelocities);
            lastMeasuredAt = timestamp;
            predictedSteps = 0;
        }

        private void RememberVertical(double step, double age, double timestamp, double confidence)
        {
            if (!hasReliableVelocityLineage || age <= 0 || !double.IsFinite(step)
                || Math.Abs(step) < .25)
            {
                if (Math.Abs(step) < .25) predictedVerticalSteps++;
                return;
            }
            var velocity = step / age;
            if (MedianVertical() is { } prior && velocity * prior < 0) verticalVelocities.Clear();
            verticalVelocities.Add(new Velocity(velocity, confidence));
            Trim(verticalVelocities);
            lastVerticalMeasuredAt = timestamp;
            predictedVerticalSteps = 0;
        }

        private (double Step, double Confidence)? PredictedHorizontal(double age, double timestamp,
            double pixelScale, VisualRoomDirection? expectedDirection)
        {
            if (expectedDirection is not { } direction || !hasReliableVelocityLineage
                || direction != lastExpectedDirection || horizontalVelocities.Count == 0
                || lastMeasuredAt is not { } measured || timestamp - measured > PredictionHorizon
                || predictedSteps >= MaximumPredictedSteps) return null;
            var ordered = horizontalVelocities.OrderBy(value => value.WorldPixelsPerSecond).ToArray();
            var velocity = ordered[ordered.Length / 2].WorldPixelsPerSecond;
            if (!StepAgrees(velocity, direction)) return null;
            var maximum = LowResolutionRoomMotionTracker.MaximumShift * pixelScale;
            var step = Math.Clamp(velocity * age, -maximum, maximum);
            if (!double.IsFinite(step) || Math.Abs(step) < .25) return null;
            return (step, horizontalVelocities.Average(value => value.Confidence) * .35);
        }

        private (double Step, double Confidence)? PredictedVertical(double age, double timestamp,
            double pixelScale)
        {
            if (!hasReliableVelocityLineage || verticalVelocities.Count == 0
                || lastVerticalMeasuredAt is not { } measured
                || timestamp - measured > VerticalPredictionHorizon
                || predictedVerticalSteps >= MaximumVerticalPredictions) return null;
            var velocity = verticalVelocities[^1].WorldPixelsPerSecond;
            var maximum = LowResolutionRoomMotionTracker.MaximumVerticalShift * pixelScale;
            var step = Math.Clamp(velocity * age, -maximum, maximum);
            if (!double.IsFinite(step) || Math.Abs(step) < .25) return null;
            return (step, verticalVelocities.Average(value => value.Confidence) * .35);
        }

        private double? MedianVertical() => verticalVelocities.Count == 0
            ? null : verticalVelocities.OrderBy(value => value.WorldPixelsPerSecond)
                .ElementAt(verticalVelocities.Count / 2).WorldPixelsPerSecond;
        private static bool ShouldUsePredictedVertical(double measured, double predicted) =>
            Math.Abs(predicted) >= 4
            && (measured * predicted < 0 || Math.Abs(measured) < Math.Abs(predicted) * .35);
        private static bool ShouldUsePredictedHorizontal(double measured, double predicted) =>
            Math.Abs(predicted) >= 4 && measured * predicted > 0
            && Math.Abs(measured) < Math.Abs(predicted) * .85;
        private static bool StepAgrees(double step, VisualRoomDirection direction) =>
            direction == VisualRoomDirection.Left ? step < 0 : step > 0;
        private void ClearHorizontal()
        {
            horizontalVelocities.Clear(); lastMeasuredAt = null; predictedSteps = 0;
        }
        private void ClearVertical()
        {
            verticalVelocities.Clear(); lastVerticalMeasuredAt = null; predictedVerticalSteps = 0;
        }
        private static void Trim(List<Velocity> values)
        {
            if (values.Count > RetainedVelocitySamples)
                values.RemoveRange(0, values.Count - RetainedVelocitySamples);
        }
    }
}
