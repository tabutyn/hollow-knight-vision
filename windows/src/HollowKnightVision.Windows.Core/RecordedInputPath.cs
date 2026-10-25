using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public enum RecordedGameButton { Left, Right, Down, Up, A, Z, X, Inventory, Pause }
public enum RecordedInputTransition { Pressed, Released }

public sealed record RecordedInputPathEvent(
    double Offset, RecordedGameButton Button, RecordedInputTransition Transition);

public sealed record RecordedTrackingSample
{
    public double Offset { get; init; }
    public double CaptureTimestamp { get; init; }
    public double? CameraX { get; init; }
    public double? CameraY { get; init; }
    public double? PublishedCameraX { get; init; }
    public double? PublishedCameraY { get; init; }
    public string? PoseSource { get; init; }
    public double? MotionBridgeConfidence { get; init; }
    public string? MotionBridgeSource { get; init; }
    public bool PoseVerified { get; init; }
    public bool HasConfirmedGround { get; init; }
    public double? GlobalCorrectionX { get; init; }
    public double? GlobalCorrectionY { get; init; }
    public int GlobalMatchCount { get; init; }
    public int GroundSegmentCount { get; init; }
    public int InlierCount { get; init; }
    [JsonPropertyName("residualRMS")] public double? ResidualRms { get; init; }
    [JsonPropertyName("visibleLineIDs")] public IReadOnlyList<int> VisibleLineIds { get; init; } = [];
    [JsonPropertyName("atlasLineIDs")] public IReadOnlyList<int> AtlasLineIds { get; init; } = [];
    public double? GroundTrackingMilliseconds { get; init; }
    public double? SignalMeanPeak { get; init; }
    public double? SignalVisibleFraction { get; init; }
    [JsonPropertyName("roomID")] public int? RoomId { get; init; }
    public ulong? RoomRevision { get; init; }
    public double? CaptureOffset { get; init; }
    public int? RenderedGameFrame { get; init; }
    public int? LocalTextureSupport { get; init; }
    public double? LocalTextureError { get; init; }
    public string? LocalMatchSource { get; init; }
    public bool? ProvisionalContinuityActive { get; init; }
    public double? LocalReferenceCaptureTimestamp { get; init; }
    public double? LocalReferenceImageX { get; init; }
    public double? LocalReferenceImageY { get; init; }
    [JsonPropertyName("localMatchImageDX")] public double? LocalMatchImageDx { get; init; }
    [JsonPropertyName("localMatchImageDY")] public double? LocalMatchImageDy { get; init; }
    public string? PlacementRecoveryState { get; init; }
    public bool? AtlasWriteAllowed { get; init; }
    public double? FloorlessCoarseX { get; init; }
    public double? FloorlessCoarseY { get; init; }
    public double? FloorlessMaskedX { get; init; }
    public double? FloorlessMaskedY { get; init; }
    public int? FloorlessExpectedDirection { get; init; }
    public string? FloorlessSelectionSource { get; init; }
}

public sealed record RecordedLoopClosureEvent(
    double Offset,
    double CaptureTimestamp,
    int WorldRevision,
    double CorrectionX,
    double CorrectionY,
    [property: JsonPropertyName("fromKeyframeID")] int FromKeyframeId,
    [property: JsonPropertyName("toKeyframeID")] int ToKeyframeId,
    int Support,
    double Ambiguity);

public sealed record RecordedGroundTruthSample(
    double Offset, double ReceivedTimestamp, ReceiverGroundTruthSample Sample);

public sealed record RecordedCoarseMotionSample
{
    public double Offset { get; init; }
    public double CaptureTimestamp { get; init; }
    public int? RenderedGameFrame { get; init; }
    public double PresentedCameraX { get; init; }
    public double PresentedCameraY { get; init; }
    public double? CoarseCameraX { get; init; }
    public double? CoarseCameraY { get; init; }
    public int? Direction { get; init; }
    public int? ScreenShift { get; init; }
    public double? Confidence { get; init; }
    [JsonPropertyName("placeMatchKeyframeID")] public int? PlaceMatchKeyframeId { get; init; }
    public double? PlaceMatchScore { get; init; }
    public double? PlaceMatchMargin { get; init; }
    public bool IsControlling { get; init; }
    public bool IsTransitioning { get; init; }
    [JsonPropertyName("roomID")] public int RoomId { get; init; }
    public ulong RoomRevision { get; init; }
    public double SignalMeanPeak { get; init; }
    public double SignalVisibleFraction { get; init; }
    public ulong? CoarsePoseRevision { get; init; }
}

public sealed record RecordedLowResolutionFrame
{
    public double Offset { get; init; }
    public double CaptureTimestamp { get; init; }
    public int? RenderedGameFrame { get; init; }
    [JsonPropertyName("roomID")] public int RoomId { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Luma { get; init; } = [];
    public bool GroundTrackingReliable { get; init; }
}

public sealed record RecordedInputPath
{
    public const int CurrentSchemaVersion = 1;

    public RecordedInputPath(
        int schemaVersion,
        Guid id,
        DateTimeOffset createdAt,
        double duration,
        Guid? sourcePathId,
        int? replayIteration,
        RecordedGameCheckpoint? startCheckpoint,
        IReadOnlyList<RecordedInputPathEvent> events,
        IReadOnlyDictionary<string, string>? runtimeMetadata)
        : this(schemaVersion, id, createdAt, duration, sourcePathId, replayIteration,
            startCheckpoint, events, [], null, null, null, null, runtimeMetadata) { }

    [JsonConstructor]
    public RecordedInputPath(
        int schemaVersion,
        Guid id,
        DateTimeOffset createdAt,
        double duration,
        Guid? sourcePathId,
        int? replayIteration,
        RecordedGameCheckpoint? startCheckpoint,
        IReadOnlyList<RecordedInputPathEvent> events,
        IReadOnlyList<RecordedTrackingSample>? trackingSamples,
        IReadOnlyList<RecordedGroundTruthSample>? groundTruthSamples = null,
        IReadOnlyList<RecordedCoarseMotionSample>? coarseMotionSamples = null,
        IReadOnlyList<RecordedLowResolutionFrame>? lowResolutionFrames = null,
        IReadOnlyList<RecordedLoopClosureEvent>? loopClosures = null,
        IReadOnlyDictionary<string, string>? runtimeMetadata = null)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        CreatedAt = createdAt;
        Duration = duration;
        SourcePathId = sourcePathId;
        ReplayIteration = replayIteration;
        StartCheckpoint = startCheckpoint;
        Events = events ?? [];
        TrackingSamples = trackingSamples ?? [];
        GroundTruthSamples = groundTruthSamples;
        CoarseMotionSamples = coarseMotionSamples;
        LowResolutionFrames = lowResolutionFrames;
        LoopClosures = loopClosures;
        RuntimeMetadata = runtimeMetadata;
    }

    public int SchemaVersion { get; }
    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public double Duration { get; }
    [JsonPropertyName("sourcePathID")] public Guid? SourcePathId { get; }
    public int? ReplayIteration { get; }
    public RecordedGameCheckpoint? StartCheckpoint { get; }
    public IReadOnlyList<RecordedInputPathEvent> Events { get; }
    public IReadOnlyList<RecordedTrackingSample> TrackingSamples { get; }
    public IReadOnlyList<RecordedGroundTruthSample>? GroundTruthSamples { get; }
    public IReadOnlyList<RecordedCoarseMotionSample>? CoarseMotionSamples { get; }
    public IReadOnlyList<RecordedLowResolutionFrame>? LowResolutionFrames { get; }
    public IReadOnlyList<RecordedLoopClosureEvent>? LoopClosures { get; }
    public IReadOnlyDictionary<string, string>? RuntimeMetadata { get; }

    [JsonIgnore] public int UnverifiedSampleCount => TrackingSamples.Count(sample => !sample.PoseVerified);
    [JsonIgnore] public int GlobalCorrectionCount => TrackingSamples.Count(sample =>
        sample.GlobalCorrectionX is not null || sample.GlobalCorrectionY is not null);
    [JsonIgnore] public IReadOnlyList<RecordedLoopClosureEvent> LoopClosureEvents => LoopClosures ?? [];
    [JsonIgnore] public IReadOnlyList<RecordedGroundTruthSample> GroundTruthTrace => GroundTruthSamples ?? [];
    [JsonIgnore] public IReadOnlyList<RecordedCoarseMotionSample> CoarseMotionTrace => CoarseMotionSamples ?? [];
    [JsonIgnore] public IReadOnlyList<RecordedLowResolutionFrame> LowResolutionFrameTrace => LowResolutionFrames ?? [];

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Id == Guid.Empty
            || !double.IsFinite(Duration) || Duration < 0 || Duration > 86_400)
            throw new InvalidDataException("Recorded input path header is invalid.");
        if (StartCheckpoint is not null && !StartCheckpoint.HasFiniteCoordinates())
            throw new InvalidDataException("Recorded input checkpoint is invalid.");
        var priorOffset = 0.0;
        var held = InputButtons.None;
        for (var index = 0; index < Events.Count; index++)
        {
            var item = Events[index];
            if (!double.IsFinite(item.Offset) || item.Offset < priorOffset
                || item.Offset < 0 || item.Offset > Duration)
                throw new InvalidDataException($"Recorded input event {index} has invalid timing.");
            var button = item.Button.ToInputButton();
            if (item.Transition == RecordedInputTransition.Pressed)
            {
                if ((held & button) != 0)
                    throw new InvalidDataException($"Recorded input event {index} presses an already-held button.");
                held |= button;
            }
            else
            {
                if ((held & button) == 0)
                    throw new InvalidDataException($"Recorded input event {index} releases a button that is not held.");
                held &= ~button;
            }
            priorOffset = item.Offset;
        }
        if (held != InputButtons.None)
            throw new InvalidDataException("Recorded input path ends with held buttons.");
    }

    public bool IsValidPlaybackPath()
    {
        if (!double.IsFinite(Duration) || Duration is < .05 or > 900
            || Events.Count > 10_000 || GroundTruthTrace.Count > 100_000) return false;
        var held = InputButtons.None;
        var priorOffset = 0.0;
        foreach (var item in Events)
        {
            if (!double.IsFinite(item.Offset) || item.Offset < priorOffset
                || item.Offset < 0 || item.Offset > Duration + .000_001) return false;
            var button = item.Button.ToInputButton();
            if (item.Transition == RecordedInputTransition.Pressed)
            {
                if ((held & button) != 0) return false;
                held |= button;
            }
            else
            {
                if ((held & button) == 0) return false;
                held &= ~button;
            }
            priorOffset = item.Offset;
        }
        if (held != InputButtons.None) return false;
        priorOffset = 0;
        foreach (var pose in GroundTruthTrace)
        {
            if (!double.IsFinite(pose.Offset) || pose.Offset < priorOffset
                || pose.Offset < 0 || pose.Offset > Duration + .000_001
                || !double.IsFinite(pose.ReceivedTimestamp)
                || pose.Sample is null || !pose.Sample.HasFiniteCoordinates()) return false;
            priorOffset = pose.Offset;
        }
        return true;
    }
}

public sealed record InputPathTrackingObservation
{
    public static InputPathTrackingObservation Empty { get; } = new();
    public AtlasPoint? CameraPosition { get; init; }
    public bool PoseVerified { get; init; }
    public bool HasConfirmedGround { get; init; }
    public CameraVector? GlobalCorrection { get; init; }
    public int GlobalMatchCount { get; init; }
    public int GroundSegmentCount { get; init; }
    public int InlierCount { get; init; }
    public double? ResidualRms { get; init; }
    public IReadOnlyList<int> VisibleLineIds { get; init; } = [];
    public IReadOnlyList<int> AtlasLineIds { get; init; } = [];
    public int LocalTextureSupport { get; init; }
    public double? LocalTextureError { get; init; }
    public string? LocalMatchSource { get; init; }
    public bool ProvisionalContinuityActive { get; init; }
    public double? LocalReferenceTimestamp { get; init; }
    public CameraVector? LocalReferencePosition { get; init; }
    public CameraVector? LocalMatchedImageDisplacement { get; init; }
}

public sealed record InputPathPlaceMatch(
    [property: JsonPropertyName("keyframeID")] int KeyframeId,
    AtlasPoint CameraPosition,
    double Score,
    double Margin);

public sealed class InputPathRecorder
{
    private sealed class Session
    {
        public required Guid Id { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required double StartedAt { get; init; }
        public Guid? SourcePathId { get; init; }
        public int? ReplayIteration { get; init; }
        public RecordedGameCheckpoint? StartCheckpoint { get; init; }
        public List<RecordedInputPathEvent> Events { get; } = [];
        public List<RecordedTrackingSample> TrackingSamples { get; } = [];
        public List<RecordedGroundTruthSample> GroundTruthSamples { get; } = [];
        public List<RecordedCoarseMotionSample> CoarseMotionSamples { get; } = [];
        public List<RecordedLowResolutionFrame> LowResolutionFrames { get; } = [];
        public Dictionary<double, int> LowResolutionFrameIndices { get; } = [];
        public double? LastLowResolutionFrameAt { get; set; }
        public List<RecordedLoopClosureEvent> LoopClosures { get; } = [];
        public HashSet<RecordedGameButton> HeldButtons { get; } = [];
    }

    private readonly object gate = new();
    private readonly IReadOnlyDictionary<string, string> runtimeMetadata;
    private Session? session;

    public InputPathRecorder(IReadOnlyDictionary<string, string>? runtimeMetadata = null)
    {
        var metadata = runtimeMetadata?.ToDictionary(item => item.Key, item => item.Value)
            ?? new Dictionary<string, string>();
        metadata["platform"] = "windows";
        metadata["recorder"] = "HollowKnightVision";
        metadata["groundRelocalization"] = "floorless-background-search-continuity-v1";
        metadata["lowResolutionMotionBridge"] = "tentative-transition-handoff-v14";
        metadata["lowResolutionFrameTrace"] = "64x36-20hz-measured-ground-reliability-v3";
        metadata["pathPlaybackPreparation"] = "purge-single-restore-settle-repurge-v2";
        this.runtimeMetadata = metadata;
    }

    public bool IsRecording { get { lock (gate) return session is not null; } }

    public bool Start(double? timestamp = null, DateTimeOffset? createdAt = null,
        Guid? id = null, Guid? sourcePathId = null, int? replayIteration = null,
        RecordedGameCheckpoint? startCheckpoint = null)
    {
        lock (gate)
        {
            if (session is not null) return false;
            session = new Session
            {
                Id = id ?? Guid.NewGuid(), CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
                StartedAt = timestamp ?? MonotonicSeconds(), SourcePathId = sourcePathId,
                ReplayIteration = replayIteration, StartCheckpoint = startCheckpoint
            };
            return true;
        }
    }

    public void RecordInput(RecordedGameButton button, bool isPressed, double? timestamp = null)
    {
        lock (gate)
        {
            if (session is not { } current) return;
            if (isPressed) { if (!current.HeldButtons.Add(button)) return; }
            else if (!current.HeldButtons.Remove(button)) return;
            current.Events.Add(new RecordedInputPathEvent(
                Offset(timestamp ?? MonotonicSeconds(), current.StartedAt), button,
                isPressed ? RecordedInputTransition.Pressed : RecordedInputTransition.Released));
        }
    }

    public void RecordTracking(InputPathTrackingObservation result, double captureTimestamp,
        double? groundTrackingMilliseconds = null, AtlasPoint? publishedCameraPosition = null,
        string? poseSource = null, double? motionBridgeConfidence = null,
        string? motionBridgeSource = null, double? signalMeanPeak = null,
        double? signalVisibleFraction = null, int? roomId = null, ulong? roomRevision = null,
        int? renderedGameFrame = null, string? placementRecoveryState = null,
        AtlasPoint? floorlessCoarsePosition = null, AtlasPoint? floorlessMaskedPosition = null,
        VisualRoomDirection? floorlessExpectedDirection = null,
        string? floorlessSelectionSource = null, double? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (gate)
        {
            if (session is not { } current) return;
            var timestamp = observedAt ?? MonotonicSeconds();
            current.TrackingSamples.Add(new RecordedTrackingSample
            {
                Offset = Offset(timestamp, current.StartedAt), CaptureTimestamp = captureTimestamp,
                CameraX = result.CameraPosition?.X, CameraY = result.CameraPosition?.Y,
                PublishedCameraX = publishedCameraPosition?.X, PublishedCameraY = publishedCameraPosition?.Y,
                PoseSource = poseSource, MotionBridgeConfidence = motionBridgeConfidence,
                MotionBridgeSource = motionBridgeSource, PoseVerified = result.PoseVerified,
                HasConfirmedGround = result.HasConfirmedGround,
                GlobalCorrectionX = result.GlobalCorrection?.X, GlobalCorrectionY = result.GlobalCorrection?.Y,
                GlobalMatchCount = result.GlobalMatchCount, GroundSegmentCount = result.GroundSegmentCount,
                InlierCount = result.InlierCount, ResidualRms = result.ResidualRms,
                VisibleLineIds = result.VisibleLineIds.Distinct().Order().ToArray(),
                AtlasLineIds = result.AtlasLineIds.Distinct().Order().ToArray(),
                GroundTrackingMilliseconds = groundTrackingMilliseconds, SignalMeanPeak = signalMeanPeak,
                SignalVisibleFraction = signalVisibleFraction, RoomId = roomId, RoomRevision = roomRevision,
                CaptureOffset = captureTimestamp - current.StartedAt, RenderedGameFrame = renderedGameFrame,
                LocalTextureSupport = result.LocalTextureSupport, LocalTextureError = result.LocalTextureError,
                LocalMatchSource = result.LocalMatchSource,
                ProvisionalContinuityActive = result.ProvisionalContinuityActive,
                LocalReferenceCaptureTimestamp = result.LocalReferenceTimestamp,
                LocalReferenceImageX = result.LocalReferencePosition?.X,
                LocalReferenceImageY = result.LocalReferencePosition?.Y,
                LocalMatchImageDx = result.LocalMatchedImageDisplacement?.X,
                LocalMatchImageDy = result.LocalMatchedImageDisplacement?.Y,
                PlacementRecoveryState = placementRecoveryState,
                FloorlessCoarseX = floorlessCoarsePosition?.X, FloorlessCoarseY = floorlessCoarsePosition?.Y,
                FloorlessMaskedX = floorlessMaskedPosition?.X, FloorlessMaskedY = floorlessMaskedPosition?.Y,
                FloorlessExpectedDirection = (int?)floorlessExpectedDirection,
                FloorlessSelectionSource = floorlessSelectionSource
            });
            if (current.LowResolutionFrameIndices.TryGetValue(captureTimestamp, out var index))
            {
                current.LowResolutionFrames[index] = current.LowResolutionFrames[index] with
                {
                    GroundTrackingReliable = GroundMotionEvidence.IsMeasured(result.PoseVerified,
                        result.HasConfirmedGround, result.LocalTextureSupport,
                        result.InlierCount, result.GlobalMatchCount)
                };
            }
        }
    }

    public void RecordAtlasAdmission(double captureTimestamp, bool allowed)
    {
        lock (gate)
        {
            if (session is not { TrackingSamples.Count: > 0 } current) return;
            var index = current.TrackingSamples.Count - 1;
            if (current.TrackingSamples[index].CaptureTimestamp != captureTimestamp) return;
            current.TrackingSamples[index] = current.TrackingSamples[index] with { AtlasWriteAllowed = allowed };
        }
    }

    public void RecordLoopClosure(double captureTimestamp, int worldRevision,
        CameraVector correction, LiveWorldLoopClosureEdge closure, double? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(closure);
        lock (gate)
        {
            if (session is not { } current) return;
            current.LoopClosures.Add(new RecordedLoopClosureEvent(
                Offset(observedAt ?? MonotonicSeconds(), current.StartedAt), captureTimestamp,
                worldRevision, correction.X, correction.Y, closure.FromKeyframeId,
                closure.ToKeyframeId, closure.Support, closure.Ambiguity));
        }
    }

    public void RecordGroundTruth(ReceiverGroundTruthSample sample, double? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (gate)
        {
            if (session is not { } current) return;
            var timestamp = observedAt ?? MonotonicSeconds();
            current.GroundTruthSamples.Add(new RecordedGroundTruthSample(
                Offset(timestamp, current.StartedAt), timestamp, sample));
        }
    }

    public void RecordCoarseMotion(double captureTimestamp, int? renderedGameFrame,
        AtlasPoint presentedCameraPosition, AtlasPoint? coarseCameraPosition,
        LowResolutionRoomMotionEstimate? estimate, bool isControlling, bool isTransitioning,
        int roomId, ulong roomRevision, double signalMeanPeak, double signalVisibleFraction,
        InputPathPlaceMatch? placeMatch = null, LowResolutionMotionGrid? motionGrid = null,
        ulong? coarsePoseRevision = null, double? observedAt = null)
    {
        lock (gate)
        {
            if (session is not { } current) return;
            var timestamp = observedAt ?? MonotonicSeconds();
            current.CoarseMotionSamples.Add(new RecordedCoarseMotionSample
            {
                Offset = Offset(timestamp, current.StartedAt), CaptureTimestamp = captureTimestamp,
                RenderedGameFrame = renderedGameFrame, PresentedCameraX = presentedCameraPosition.X,
                PresentedCameraY = presentedCameraPosition.Y, CoarseCameraX = coarseCameraPosition?.X,
                CoarseCameraY = coarseCameraPosition?.Y,
                Direction = estimate is null ? null : (int)estimate.Direction,
                ScreenShift = estimate?.ScreenShift, Confidence = estimate?.Confidence,
                PlaceMatchKeyframeId = placeMatch?.KeyframeId, PlaceMatchScore = placeMatch?.Score,
                PlaceMatchMargin = placeMatch?.Margin, IsControlling = isControlling,
                IsTransitioning = isTransitioning, RoomId = roomId, RoomRevision = roomRevision,
                SignalMeanPeak = signalMeanPeak, SignalVisibleFraction = signalVisibleFraction,
                CoarsePoseRevision = coarsePoseRevision
            });
            if (motionGrid is not null && (current.LastLowResolutionFrameAt is null
                || timestamp - current.LastLowResolutionFrameAt >= .045))
            {
                var index = current.LowResolutionFrames.Count;
                current.LowResolutionFrames.Add(new RecordedLowResolutionFrame
                {
                    Offset = Offset(timestamp, current.StartedAt), CaptureTimestamp = captureTimestamp,
                    RenderedGameFrame = renderedGameFrame, RoomId = roomId,
                    Width = motionGrid.Width, Height = motionGrid.Height,
                    Luma = motionGrid.Luma.ToArray(), GroundTrackingReliable = false
                });
                current.LowResolutionFrameIndices[captureTimestamp] = index;
                current.LastLowResolutionFrameAt = timestamp;
            }
        }
    }

    public RecordedInputPath? Stop(double? timestamp = null)
    {
        lock (gate)
        {
            if (session is not { } current) return null;
            var duration = Offset(timestamp ?? MonotonicSeconds(), current.StartedAt);
            foreach (var button in current.HeldButtons.OrderBy(ButtonWireName))
                current.Events.Add(new RecordedInputPathEvent(duration, button, RecordedInputTransition.Released));
            session = null;
            return new RecordedInputPath(schemaVersion: RecordedInputPath.CurrentSchemaVersion,
                id: current.Id, createdAt: current.CreatedAt, duration: duration,
                sourcePathId: current.SourcePathId, replayIteration: current.ReplayIteration,
                startCheckpoint: current.StartCheckpoint, events: current.Events.ToArray(),
                trackingSamples: current.TrackingSamples.ToArray(),
                groundTruthSamples: current.GroundTruthSamples.ToArray(),
                coarseMotionSamples: current.CoarseMotionSamples.ToArray(),
                lowResolutionFrames: current.LowResolutionFrames.ToArray(),
                loopClosures: current.LoopClosures.ToArray(),
                runtimeMetadata: new Dictionary<string, string>(runtimeMetadata));
        }
    }

    public void Cancel() { lock (gate) session = null; }
    private static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static double Offset(double timestamp, double start) =>
        double.IsFinite(timestamp) && double.IsFinite(start) ? Math.Max(0, timestamp - start) : 0;
    private static string ButtonWireName(RecordedGameButton button) => button switch
    {
        RecordedGameButton.Left => "left", RecordedGameButton.Right => "right",
        RecordedGameButton.Down => "down", RecordedGameButton.Up => "up",
        RecordedGameButton.A => "a", RecordedGameButton.Z => "z", RecordedGameButton.X => "x",
        RecordedGameButton.Inventory => "inventory", RecordedGameButton.Pause => "pause",
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };
}

public readonly record struct InputPathStateChange(double Offset, InputButtons HeldButtons);

public static class RecordedInputPathExtensions
{
    public static InputButtons ToInputButton(this RecordedGameButton button) => button switch
    {
        RecordedGameButton.Left => InputButtons.Left, RecordedGameButton.Right => InputButtons.Right,
        RecordedGameButton.Down => InputButtons.Down, RecordedGameButton.Up => InputButtons.Up,
        RecordedGameButton.A => InputButtons.ActionA, RecordedGameButton.Z => InputButtons.ActionZ,
        RecordedGameButton.X => InputButtons.ActionX, RecordedGameButton.Inventory => InputButtons.Inventory,
        RecordedGameButton.Pause => InputButtons.PauseMenu,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    public static RecordedGameButton? ToRecordedGameButton(this InputButtons button) => button switch
    {
        InputButtons.Left => RecordedGameButton.Left, InputButtons.Right => RecordedGameButton.Right,
        InputButtons.Down => RecordedGameButton.Down, InputButtons.Up => RecordedGameButton.Up,
        InputButtons.ActionA => RecordedGameButton.A, InputButtons.ActionZ => RecordedGameButton.Z,
        InputButtons.ActionX => RecordedGameButton.X, InputButtons.Inventory => RecordedGameButton.Inventory,
        InputButtons.PauseMenu => RecordedGameButton.Pause, _ => null
    };

    /// <summary>Keep the receiver's 250ms input lease alive between recorded transitions.</summary>
    public static IEnumerable<InputPathStateChange> PlaybackStates(this RecordedInputPath path)
    {
        var changes = path.StateChanges();
        const double heartbeatSeconds = .1;
        var offset = 0.0;
        var held = InputButtons.None;
        yield return new InputPathStateChange(offset, held);
        foreach (var change in changes)
        {
            while (offset + heartbeatSeconds < change.Offset)
            {
                offset += heartbeatSeconds;
                yield return new InputPathStateChange(offset, held);
            }
            offset = change.Offset;
            held = change.HeldButtons;
            yield return change;
        }
        while (offset + heartbeatSeconds < path.Duration)
        {
            offset += heartbeatSeconds;
            yield return new InputPathStateChange(offset, held);
        }
        yield return new InputPathStateChange(path.Duration, InputButtons.None);
    }

    public static IReadOnlyList<InputPathStateChange> StateChanges(this RecordedInputPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.Validate();
        var held = InputButtons.None;
        var result = new List<InputPathStateChange>(path.Events.Count);
        foreach (var item in path.Events)
        {
            var button = item.Button.ToInputButton();
            held = item.Transition == RecordedInputTransition.Pressed ? held | button : held & ~button;
            result.Add(new InputPathStateChange(item.Offset, held));
        }
        return result;
    }
}

public sealed record GamePathPlaybackCommand(string FileName, int Iteration)
{
    public const string Prefix = "replay-path:";
    public static GamePathPlaybackCommand? Parse(string serialized)
    {
        if (string.IsNullOrEmpty(serialized) || !serialized.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var payload = serialized[Prefix.Length..];
        var separator = payload.LastIndexOf(':');
        if (separator <= 0 || separator == payload.Length - 1) return null;
        var fileName = payload[..separator];
        if (!int.TryParse(payload[(separator + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var iteration)
            || iteration is < 1 or > 10_000 || !RecordedInputPathStore.IsSafeFileName(fileName)) return null;
        return new GamePathPlaybackCommand(fileName, iteration);
    }
    public string Serialized => $"{Prefix}{FileName}:{Iteration}";
}

public sealed class RecordedInputPathStore
{
    private static readonly JsonSerializerOptions JsonOptions = LabelingExampleStore.CreateJsonOptions();
    public RecordedInputPathStore(string? rootDirectory = null) =>
        RootDirectory = Path.GetFullPath(rootDirectory ?? DefaultRootDirectory());
    public string RootDirectory { get; }

    public RecordedInputPath Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var value = JsonSerializer.Deserialize<RecordedInputPath>(
                File.ReadAllBytes(Path.GetFullPath(path)), JsonOptions)
                ?? throw new InvalidDataException("Recorded input path is empty.");
            value.Validate();
            return value;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Recorded input path JSON is corrupt.", error);
        }
    }

    public RecordedInputPath LoadNamed(string fileName)
    {
        if (!IsSafeFileName(fileName)) throw new InvalidDataException("Recorded input path file name is invalid.");
        return Load(Path.Combine(RootDirectory, fileName));
    }

    public void Save(RecordedInputPath path, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        path.Validate();
        AtomicFile.Write(Path.GetFullPath(outputPath), JsonSerializer.SerializeToUtf8Bytes(path, JsonOptions));
    }

    public string Save(RecordedInputPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Directory.CreateDirectory(RootDirectory);
        var timestamp = path.CreatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"path-{timestamp}-{path.Id:D}.json".ToLowerInvariant();
        var outputPath = Path.Combine(RootDirectory, fileName);
        Save(path, outputPath);
        return outputPath;
    }

    internal static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && !Path.IsPathRooted(fileName)
        && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
        && fileName.IndexOfAny(['/', '\\']) < 0
        && fileName.StartsWith("path-", StringComparison.Ordinal)
        && fileName.EndsWith(".json", StringComparison.Ordinal);

    private static string DefaultRootDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Environment.CurrentDirectory;
        return Path.Combine(local, "HollowKnightVision", "input-paths-v1");
    }
}
