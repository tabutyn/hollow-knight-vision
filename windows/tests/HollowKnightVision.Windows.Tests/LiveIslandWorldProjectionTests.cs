using System.Text.Json.Nodes;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class LiveIslandWorldProjectionTests
{
    private const ulong Capture = 6;
    private const ulong SavedGeneration = 20;
    private const ulong LocalEpoch = 2;
    private const long CoordinateEpoch = 3;
    private const int RoomId = 7;
    private static readonly BgraFrame Frame = new(32, 24, 128, new byte[32 * 24 * 4], 9, DateTimeOffset.UnixEpoch);

    internal static void TestCorrectionReprojectsAnOlderOwnerWithoutChangingRawMotion()
    {
        var world = new LiveWorldTracker();
        Admit(world, 0, 1, new(10, 5), new(110, 15), 1);
        Admit(world, 1, 3, new(50, 7), new(350, 37), 2);
        var before = world.Snapshot;
        var moved = before.ReplaceOptimizedPoses(new Dictionary<int, AtlasPoint>
        {
            [0] = new(130, 13),
            [1] = new(350, 37)
        }, before.MapRevision);
        var completed = Publication(moved);
        var returning = new WindowsLiveWorldFrameScope(Capture, "room", 8, CoordinateEpoch, 1, 1, false);
        var queuedOld = returning with { CaptureTimestamp = 1.5 };
        var owner = moved.TrackingIslands.Single(value => value.MeasuredLineage == 1);
        var expected = new LiveIslandPlacementProjection(owner.Id, new(120, 8), true);
        Check(completed.Result.ProjectFrame(completed.Frame, returning) == expected
            && completed.Result.ProjectFrame(completed.Frame, queuedOld) == expected,
            "Both a returning frame and a delayed frame need the revised placement of their own image owner.");
        Check(completed.Result.ProjectFrame(completed.Frame, returning with { MeasuredLineage = 2 })?.WorldFromLocal == new AtlasPoint(300, 30)
            && moved.Observations.Select(row => row.RawPose).SequenceEqual(before.Observations.Select(row => row.RawPose)),
            "Moving one island cannot change the camera's measured motion or another island's translation.");
        Check(completed.Result.ProjectFrame(completed.Frame, queuedOld with { Room = "another room" }) is null
            && completed.Result.ProjectFrame(completed.Frame, queuedOld with { CaptureGeneration = Capture + 1 }) is null,
            "A delayed image must not borrow the correction after its lineage number reappears in a different capture or room.");
    }

    internal static void TestUnknownAndProvisionalOwnersCannotInheritTheLastPlacement()
    {
        var world = new LiveWorldTracker();
        Admit(world, 0, 1, new(10, 5), new(110, 15), 1);
        Admit(world, 1, 3, new(30, 7), new(330, 37), 1, pending: true);
        Admit(world, 2, 4, new(50, 7), new(550, 37), 2);
        var before = world.Snapshot;
        var coordinates = before.Observations.ToDictionary(row => row.Id, row => row.OptimizedPose);
        coordinates[0] = new(130, 13);
        var completed = Publication(before.ReplaceOptimizedPoses(coordinates, before.MapRevision));
        var returning = new WindowsLiveWorldFrameScope(Capture, "room", 5, CoordinateEpoch, 1, 1, false);
        Check(completed.Result.ProjectFrame(completed.Frame, returning) is { IsPlaced: false } provisional
            && provisional.WorldFromLocal == new AtlasPoint(300, 30),
            "A provisional incarnation cannot borrow an earlier incarnation's registered position.");
        var duringGap = returning with { CaptureTimestamp = 2, MeasuredLineage = null };
        var estimated = completed.Result.ProjectFrame(completed.Frame, duringGap);
        Check(estimated is { IsPlaced: false, WorldFromLocal: { X: 120, Y: 8 } },
            "An unmeasured frame retains the display offset of its last owner without authorizing registered geometry.");
        Check(completed.Result.ProjectFrame(completed.Frame, duringGap with { MeasuredLineage = 1 }) is { IsPlaced: true } earlier
            && earlier.IslandId == estimated?.IslandId && earlier.WorldFromLocal == estimated?.WorldFromLocal,
            "An exact earlier reference recovers the corrected island for that capture.");
        Check(completed.Result.ProjectFrame(completed.Frame, returning with { MeasuredLineage = 3 }) is null
            && completed.Result.ProjectFrame(completed.Frame, returning with { CaptureTimestamp = .5 }) is null,
            "An unknown owner or a future measurement must not supply a world correction.");
        Check(completed.Result.ProjectFrame(completed.Frame, returning with { MeasuredLineage = null, RetainedLineage = 2 })
            is { IsPlaced: false } held && held.WorldFromLocal == new AtlasPoint(500, 30),
            "Held visual tracking can retain its display position without authorizing registered geometry.");
    }

    internal static void TestRigidCorrectionMovesEveryMemberAndItsLandmarks()
    {
        var world = new LiveWorldTracker();
        Admit(world, 0, 1, default, new(100, 10), 1);
        Admit(world, 1, 2, new(10, 0), new(110, 10), 1);
        Admit(world, 2, 3, default, new(300, 40), 2);
        var tracked = world.Snapshot;
        var source = tracked.Keyframes.Single(keyframe => keyframe.ObservationId == 0);
        var landmark = new LiveWorldLandmark(0, source.Id, 0, new(4, 4),
            new(104, 14), [1, 2, 3], Depth: 1, DepthConfidence: 1);
        var before = new LiveWorldSnapshot(tracked.SchemaVersion, tracked.MapRevision,
            tracked.AnchoredObservationId, tracked.Observations, tracked.Keyframes, [landmark],
            tracked.RelativeMotionEdges, tracked.LoopClosureEdges, tracked.TrackingIslands);
        var original = LiveWorldJson.WriteSnapshot(before);
        var poses = before.Observations.ToDictionary(row => row.Id, row => row.OptimizedPose);
        poses[0] = new(108, 7);

        ExpectRejected(poses, before.MapRevision, LiveWorldModelError.InvalidIslandMembership);
        poses[1] = new(118, 7);
        ExpectRejected(poses, before.MapRevision - 1, LiveWorldModelError.StaleRevision);
        Check(LiveWorldJson.WriteSnapshot(before).SequenceEqual(original),
            "A rejected correction must leave the original graph immutable.");

        var moved = before.ReplaceOptimizedPoses(poses, before.MapRevision);
        var sameOwner = moved.TrackingIslands.Single(island => island.MeasuredLineage == 1);
        Check(moved.MapRevision == before.MapRevision + 1
            && moved.Observations.Select(row => row.OptimizedPose).SequenceEqual([new(108, 7), new(118, 7), new(300, 40)])
            && moved.Observations.Select(row => row.RawPose).SequenceEqual(before.Observations.Select(row => row.RawPose))
            && sameOwner.WorldFromLocal == new AtlasPoint(108, 7)
            && sameOwner.Members.SequenceEqual(before.TrackingIslands[0].Members)
            && moved.TrackingIslands[1] == before.TrackingIslands[1]
            && moved.Landmarks.Single() == landmark with { WorldPoint = new(112, 11) },
            "The rigid correction moves both members and their landmark by one common offset.");
        var bytes = LiveWorldJson.WriteSnapshot(moved);
        Check(LiveWorldJson.WriteSnapshot(LiveWorldJson.ReadSnapshot(bytes)).SequenceEqual(bytes),
            "The rigid layout and measured data must survive save and reopen.");

        void ExpectRejected(IReadOnlyDictionary<int, AtlasPoint> candidate, int revision, LiveWorldModelError error)
        {
            try { _ = before.ReplaceOptimizedPoses(candidate, revision); }
            catch (LiveWorldModelException rejection) when (rejection.Error == error) { return; }
            throw new InvalidOperationException($"The correction should fail with {error}.");
        }
    }

    internal static void TestWorldProjectionFencesCaptureRoomEpochAndRecovery()
    {
        var world = new LiveWorldTracker();
        Admit(world, 0, 1, default, new(92, 15), 1);
        var completed = Publication(world.Snapshot);
        var frame = new WindowsLiveWorldFrameScope(Capture, "room", 2, CoordinateEpoch, 1, 1, false);
        Check(completed.Result.ProjectFrame(completed.Frame, frame)?.IsPlaced == true,
            "The matching capture must have a registered projection.");
        Check(completed.Result.ProjectFrame(completed.Frame, frame with { CaptureGeneration = Capture + 1 }) is null
            && completed.Result.ProjectFrame(completed.Frame, frame with { CoordinateEpoch = CoordinateEpoch + 1 }) is null
            && completed.Result.ProjectFrame(completed.Frame, frame with { Room = "another" }) is null
            && completed.Result.ProjectFrame(completed.Frame, frame with { RequiresRecovery = true }) is null
            && completed.Result.ProjectFrame(completed.Frame, frame with { CaptureTimestamp = double.NaN }) is null
            && (completed.Result with { State = WindowsLiveWorldPipelineState.PendingRecovery }).ProjectFrame(completed.Frame, frame) is null,
            "An obsolete epoch, room, clock or recovery state must invalidate placement.");
        Check(LiveIslandWorldProjection.Resolve(world.Snapshot, SavedGeneration + 1, RoomId, LocalEpoch, 1, 2) is null
            && LiveIslandWorldProjection.Resolve(world.Snapshot, SavedGeneration, RoomId, LocalEpoch + 1, 1, 2) is null,
            "A persisted owner cannot cross a generation or graph epoch boundary.");

        var predicted = frame with
        {
            CaptureTimestamp = 1.05, RequiresRecovery = true, MeasuredLineage = null, RetainedLineage = null,
            FallDisplayReference = (1, 1)
        };
        Check(completed.Result.ProjectFrame(completed.Frame, predicted) is { IsPlaced: false, WorldFromLocal: { X: 92, Y: 15 } },
            "A short prediction can place the display in its prior measured basis without restoring Atlas placement.");
        Check(completed.Result.ProjectFrame(completed.Frame, predicted with { CaptureTimestamp = 1.09 }) is null
            && completed.Result.ProjectFrame(completed.Frame, predicted with { FallDisplayReference = (9, 1) }) is null
            && completed.Result.ProjectFrame(completed.Frame, predicted with { FallDisplayReference = (1, .99) }) is null
            && completed.Result.ProjectFrame(completed.Frame, predicted with { Room = "another" }) is null
            && (completed.Result with { PlacementPending = true }).ProjectFrame(completed.Frame, predicted) is null,
            "An old prediction, unknown reference, other room or pending placement cannot retrieve a display basis.");
    }

    internal static void TestExactCaptureClockSurvivesCorrectionAndLegacyLoad()
    {
        var world = new LiveWorldTracker();
        _ = world.Ingest(0, 1000, Frame, default, Frame.Width, captureEvidence: new(123.456) { MeasuredLineage = 1 });
        var snapshot = world.Snapshot;
        var moved = snapshot.ReplaceOptimizedPoses(new Dictionary<int, AtlasPoint> { [0] = new(12, 8) }, snapshot.MapRevision);
        var bytes = LiveWorldJson.WriteSnapshot(moved);
        var restored = LiveWorldJson.ReadSnapshot(bytes).Observations.Single();
        Check(restored.CaptureTimestamp == 123.456 && restored.Timestamp == 1000 && restored.OptimizedPose == new AtlasPoint(12, 8),
            "A corrected position must preserve the two original clocks.");
        var older = JsonNode.Parse(bytes)!;
        older["observations"]![0]!.AsObject().Remove("captureTimestamp");
        var legacy = LiveWorldJson.ReadSnapshot(System.Text.Encoding.UTF8.GetBytes(older.ToJsonString())).Observations.Single();
        Check(legacy.CaptureTimestamp is null && legacy.OptimizedPose == restored.OptimizedPose,
            "A saved observation predating the capture clock must keep its geometry without inventing a timestamp.");
        try
        {
            _ = world.Ingest(1, 1001, Frame, default, Frame.Width, captureEvidence: new(double.NaN) { MeasuredLineage = 2 });
        }
        catch (LiveWorldTrackerException error) when (error.Error == LiveWorldTrackerError.InvalidScan)
        {
            Check(ReferenceEquals(world.Snapshot, snapshot), "An invalid capture must keep the existing graph.");
            return;
        }
        throw new InvalidOperationException("A nonfinite capture clock must be rejected as invalid input.");
    }

    private static void Admit(LiveWorldTracker world, int id, double time, AtlasPoint local, AtlasPoint placement,
        long lineage, bool pending = false) =>
        world.Ingest(id, time, Frame, local, Frame.Width, searchForClosure: false, localPose: local,
            captureGeneration: SavedGeneration, localEpoch: LocalEpoch, roomId: RoomId, placedCameraPose: placement,
            captureEvidence: new(time) { MeasuredLineage = lineage }, placementPending: pending);

    private static (WindowsLiveWorldFrame Frame, WindowsLiveWorldPipelineResult Result) Publication(LiveWorldSnapshot graph) =>
        (new(Frame, Frame, 5, 5, default, Frame.Width, [], [], Capture, "room", 1, 64),
         new(WindowsLiveWorldPipelineState.Committed, Capture, default, default, new(500, 30),
             graph.MapRevision, null, null, null, "fixture")
         {
             CommittedGraph = graph,
             CommittedRoomId = RoomId,
             PersistentCaptureGeneration = SavedGeneration,
             LocalEpoch = LocalEpoch,
             WalkedCoordinateEpoch = CoordinateEpoch
         });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
