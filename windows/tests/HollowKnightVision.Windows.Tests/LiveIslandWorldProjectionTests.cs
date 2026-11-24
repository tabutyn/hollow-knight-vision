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
    }

    internal static void TestUnknownAndProvisionalOwnersCannotInheritTheLastPlacement()
    {
        var world = new LiveWorldTracker();
        Admit(world, 0, 1, new(10, 5), new(110, 15), 1);
        Admit(world, 1, 3, new(30, 7), new(330, 37), 1, pending: true);
        Admit(world, 2, 4, new(50, 7), new(550, 37), 2);
        var completed = Publication(world.Snapshot);
        var returning = new WindowsLiveWorldFrameScope(Capture, "room", 5, CoordinateEpoch, 1, 1, false);
        Check(completed.Result.ProjectFrame(completed.Frame, returning) is { IsPlaced: false } provisional
            && provisional.WorldFromLocal == new AtlasPoint(300, 30),
            "A provisional incarnation cannot borrow an earlier incarnation's registered position.");
        Check(completed.Result.ProjectFrame(completed.Frame, returning with { CaptureTimestamp = 2 }) is { IsPlaced: true } earlier
            && earlier.WorldFromLocal == new AtlasPoint(100, 10),
            "A delayed frame keeps the owner that was measured at its capture time.");
        Check(completed.Result.ProjectFrame(completed.Frame, returning with { MeasuredLineage = 3 }) is null
            && completed.Result.ProjectFrame(completed.Frame, returning with { CaptureTimestamp = .5 }) is null,
            "An unknown owner or a future measurement must not supply a world correction.");
        Check(completed.Result.ProjectFrame(completed.Frame, returning with { MeasuredLineage = null, RetainedLineage = 2 })
            is { IsPlaced: false } held && held.WorldFromLocal == new AtlasPoint(500, 30),
            "Held visual tracking can retain its display position without authorizing registered geometry.");
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
