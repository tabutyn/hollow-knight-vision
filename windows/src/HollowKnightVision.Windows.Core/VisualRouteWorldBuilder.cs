namespace HollowKnightVision.Windows.Core;

public sealed record VisualRouteWorldBuildReport(
    Guid SessionId,
    int SourceSamples,
    int Rooms,
    int Observations,
    int Keyframes,
    int RelativeMotionEdges,
    int RejectedSteps,
    IReadOnlyDictionary<string, int> Rejections,
    LiveWorldSnapshot Snapshot);

/// <summary>
/// Materializes immutable visual-route evidence into the validated live-world
/// graph. Scene changes start rooms; rejected visual steps never become edges.
/// Receiver camera truth is deliberately not used to construct poses.
/// </summary>
public static class VisualRouteWorldBuilder
{
    public static VisualRouteWorldBuildReport Build(
        VisualRouteSession session,
        int keyframeInterval = 10)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        if (keyframeInterval <= 0) throw new ArgumentOutOfRangeException(nameof(keyframeInterval));

        var rooms = new Dictionary<string, int>(StringComparer.Ordinal);
        var roomPoses = new Dictionary<int, AtlasPoint>();
        var roomObservationCounts = new Dictionary<int, int>();
        var lastObservationByRoom = new Dictionary<int, LiveWorldObservation>();
        var observations = new List<LiveWorldObservation>();
        var keyframes = new List<LiveWorldKeyframe>();
        var edges = new List<LiveWorldRelativeMotionEdge>();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejectedSteps = 0;

        foreach (var sample in session.Samples)
        {
            if (!rooms.TryGetValue(sample.SceneName, out var roomId))
            {
                roomId = rooms.Count;
                rooms[sample.SceneName] = roomId;
                roomPoses[roomId] = new AtlasPoint(0, 0);
                roomObservationCounts[roomId] = 0;
            }

            var isRoomStart = !lastObservationByRoom.TryGetValue(
                    roomId,
                    out var continuousPrevious)
                || continuousPrevious.SourceObservationId != sample.Index - 1;
            CameraVector? delta = null;
            double confidence = 1;
            if (!isRoomStart)
            {
                var previousSample = session.Samples[lastObservationByRoom[roomId].SourceObservationId];
                var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(
                    previousSample.Grid,
                    sample.Grid);
                if (diagnostic.Motion is { } motion)
                {
                    delta = new CameraVector(
                        -motion.ScreenShiftX * session.FrameWidth / sample.Grid.Width,
                        -motion.ScreenShiftY * session.FrameHeight / sample.Grid.Height);
                    confidence = motion.Confidence;
                }
                else if (diagnostic.FinalRejection
                        == LowResolutionTranslationRejection.InsignificantSubcellMotion
                    || VisuallyStationary(previousSample.Grid, sample.Grid))
                {
                    delta = new CameraVector(0, 0);
                    confidence = 0.25;
                }
                else
                {
                    rejectedSteps++;
                    var reason = diagnostic.FinalRejection?.ToString() ?? "Unknown";
                    rejections[reason] = rejections.GetValueOrDefault(reason) + 1;
                    continue;
                }
            }

            var priorPose = roomPoses[roomId];
            var pose = delta is { } movement
                ? new AtlasPoint(priorPose.X + movement.X, priorPose.Y + movement.Y)
                : priorPose;
            var observation = LiveWorldObservation.Create(
                observations.Count,
                sample.Index,
                sample.Timestamp,
                session.FrameWidth,
                session.FrameHeight,
                session.FrameWidth,
                sample.Grid.Width,
                [],
                pose,
                pose,
                localPose: pose,
                captureGeneration: (ulong)sample.Index,
                localEpoch: (ulong)roomId,
                roomId: roomId);
            observations.Add(observation);

            if (lastObservationByRoom.TryGetValue(roomId, out var previousObservation)
                && delta is { } edgeDelta)
            {
                edges.Add(new LiveWorldRelativeMotionEdge(
                    edges.Count,
                    previousObservation.Id,
                    observation.Id,
                    edgeDelta.X,
                    edgeDelta.Y,
                    Math.Max(1, (int)Math.Round(confidence * 100))));
            }

            var count = roomObservationCounts[roomId];
            if (isRoomStart || count % keyframeInterval == 0)
            {
                keyframes.Add(new LiveWorldKeyframe(
                    keyframes.Count,
                    observation.Id,
                    observation.SourceObservationId));
            }
            roomObservationCounts[roomId] = count + 1;
            roomPoses[roomId] = pose;
            lastObservationByRoom[roomId] = observation;
        }

        if (observations.Count == 0)
        {
            throw new InvalidDataException("Visual route produced no world observations.");
        }
        var snapshot = new LiveWorldSnapshot(
            mapRevision: 1,
            anchoredObservationId: observations[0].Id,
            observations: observations,
            keyframes: keyframes,
            relativeMotionEdges: edges);
        return new VisualRouteWorldBuildReport(
            session.Id,
            session.Samples.Count,
            rooms.Count,
            observations.Count,
            keyframes.Count,
            edges.Count,
            rejectedSteps,
            rejections,
            snapshot);
    }

    public static VisualRouteWorldBuildReport BuildAndSave(
        VisualRouteStore routeStore,
        LiveWorldSnapshotStore worldStore,
        int keyframeInterval = 10)
    {
        ArgumentNullException.ThrowIfNull(routeStore);
        ArgumentNullException.ThrowIfNull(worldStore);
        var current = worldStore.Load();
        if (current.Observations.Count != 0 || current.MapRevision != 0)
        {
            throw new IOException(
                "World output already contains evidence; choose a new or empty directory.");
        }
        var report = Build(routeStore.Load(), keyframeInterval);
        if (!worldStore.Commit(report.Snapshot, 0))
        {
            throw new IOException("World output changed while route evidence was being built.");
        }
        return report;
    }

    private static bool VisuallyStationary(
        LowResolutionMotionGrid previous,
        LowResolutionMotionGrid current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height) return false;
        var previousMean = previous.Luma.Average(value => (double)value);
        var currentMean = current.Luma.Average(value => (double)value);
        var centeredDifference = 0.0;
        for (var index = 0; index < previous.Luma.Length; index++)
        {
            centeredDifference += Math.Abs(
                previous.Luma[index] - previousMean
                    - (current.Luma[index] - currentMean));
        }
        return centeredDifference / previous.Luma.Length <= 2.5;
    }
}
