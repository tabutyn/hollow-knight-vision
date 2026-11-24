using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class LiveWorldWalkedGroundTests
{
    public static void TestWalkedGroundSurvivesStoreReopenAndFollowsOnlyItsOwningPose()
    {
        using var directory = new TestDirectory();
        var store = new LiveWorldSessionStore(directory.Path);
        var tracker = new LiveWorldTracker();
        var camera = new AtlasPoint(100, 20);
        var local = Span(63, 160, 90);
        var accepted = LiveWorldWalkedGround.NewEvidence([local], [], camera);
        Check(accepted.SequenceEqual([new LiveWorldWalkedFloor(-37, 60, 70)]));
        Add(1, 1, camera, new(50, 10), accepted);
        Add(2, 2, new(0, 0), new(20, 50), [new(10, 30, 60)]);
        Add(3, 1, new(115, 20), new(65, 10), []);

        var loaded = new LiveWorldSessionStore(directory.Path).Load().Snapshot;
        Check(loaded.Observations[0].RawPose == camera);
        Check(loaded.Observations[0].OptimizedPose == new AtlasPoint(50, 10));
        Check(loaded.Observations[0].WalkedGround!.SequenceEqual(accepted));
        Check(loaded.Observations[2].WalkedGround is null);
        Check(LiveWorldWalkedGround.ProjectRoom(loaded, 1).SequenceEqual([Span(13, 110, 80)]));
        Check(LiveWorldWalkedGround.ProjectRoom(loaded, 2).SequenceEqual([Span(30, 50, 110)]));
        var translated = loaded.TranslateRoom(1, new(17, -4));
        Check(store.Commit(translated, loaded.MapRevision, translated.MapRevision));
        var corrected = new LiveWorldSessionStore(directory.Path).Load().Snapshot;
        Check(corrected.Observations[0].WalkedGround!.SequenceEqual(accepted)
            && corrected.Observations[0].RawPose == camera);
        Check(LiveWorldWalkedGround.ProjectRoom(corrected, 1).SequenceEqual([Span(30, 127, 76)]));
        Check(LiveWorldWalkedGround.ProjectRoom(corrected, 2).SequenceEqual([Span(30, 50, 110)]));
        Check(LiveWorldWalkedGround.ProjectRoom(corrected, 3).Count == 0);

        void Add(long timestamp, int room, AtlasPoint raw, AtlasPoint placed, IReadOnlyList<LiveWorldWalkedFloor> floors)
        {
            var frame = new BgraFrame(32, 20, 128, new byte[32 * 20 * 4], timestamp, DateTimeOffset.UnixEpoch);
            var before = tracker.Snapshot.MapRevision;
            var source = store.Append(frame, timestamp, raw, 32);
            var update = tracker.Ingest(source.Id, timestamp, frame, raw, 32, roomId: room,
                searchForClosure: false, placedCameraPose: placed, walkedGround: floors);
            Check(store.Commit(update.Snapshot, before, update.Revision));
        }
    }

    public static void TestOnlyFreshWalkedColumnsAreCommittedAndAlreadySavedSpansFollowWorldCorrection()
    {
        var last = Span(10, 100, 80);
        var full = Span(5, 120, 80);
        var upper = Span(30, 90, 115);
        var delta = LiveWorldWalkedGround.NewEvidence([full, upper], [last], new(15, 0));
        Check(delta.SequenceEqual([
            new LiveWorldWalkedFloor(-10, -5, 80),
            new LiveWorldWalkedFloor(85, 105, 80),
            new LiveWorldWalkedFloor(15, 75, 115)]));
        Check(LiveWorldWalkedGround.NewEvidence([full], [full], new(0, 0)).Count == 0);

        var durable = new[] { Span(500, 590, 180) };
        var display = LiveWorldWalkedGround.MergeForDisplay([last], [last], durable, new(480, 110));
        Check(display.SequenceEqual([Span(20, 110, 70)]));
        var merged = LiveWorldWalkedGround.MergeForDisplay([full], [last], durable, new(490, 100));
        Check(merged.SequenceEqual([full]));
    }

    public static void TestPipelineTracksWalkedCoveragePerImageOwner()
    {
        using var directory = new TestDirectory();
        var pipeline = new WindowsLiveWorldPipeline(directory.Path);
        var initial = Span(10, 100, 80);
        var overlapping = Span(30, 90, 80);
        var extended = Span(10, 120, 80);
        Check(Commit(1, 1, 1, 1, initial).RecordedLocalWalk.SequenceEqual([initial]));
        var second = Commit(2, 1.2, 2, 2, overlapping);
        Check(second.RecordedLocalWalk.SequenceEqual([overlapping]));
        Check(LiveWorldWalkedGround.MergeForDisplay([overlapping], second.RecordedLocalWalk,
            second.WalkedGround, second.WorldBasisOffset).SequenceEqual([initial]));
        var preview = Commit(3, 1.25, 1, 1, extended);
        Check(preview.State == WindowsLiveWorldPipelineState.Throttled && preview.WalkedGroundLineage == 1
            && preview.RecordedLocalWalk.SequenceEqual([initial]));
        Check(Commit(4, 1.4, 1, 1, extended).RecordedLocalWalk.SequenceEqual([extended]));
        Check(Commit(5, 1.6, 3, 1, Span(10, 350, 80)).RecordedLocalWalk.SequenceEqual([extended]));

        var saved = new LiveWorldSessionStore(directory.Path).Load().Snapshot;
        Check(saved.Observations.Count == 4
            && saved.Observations[0].WalkedGround!.SequenceEqual([new LiveWorldWalkedFloor(10, 100, 80)])
            && saved.Observations[1].WalkedGround!.SequenceEqual([new LiveWorldWalkedFloor(30, 90, 80)])
            && saved.Observations[2].WalkedGround!.SequenceEqual([new LiveWorldWalkedFloor(100, 120, 80)])
            && saved.Observations[3].WalkedGround is null);
        Check(saved.TrackingIslands.Single(island => island.MeasuredLineage == 1)
            .Members.Select(member => member.ObservationId).SequenceEqual([0, 2]));
        Check(LiveWorldWalkedGround.ProjectRoom(saved, saved.Observations[0].RoomId).SequenceEqual([extended]));

        WindowsLiveWorldPipelineResult Commit(int id, double timestamp, long captureOwner, long walkOwner,
            GroundHypothesisAtlasLine span)
        {
            var frame = new BgraFrame(320, 180, 320 * 4, new byte[320 * 180 * 4], id, DateTimeOffset.UnixEpoch);
            return pipeline.Process(new WindowsLiveWorldFrame(frame, frame, timestamp, timestamp, default, 320,
                [], [], 1, "room", 1, 64)
            {
                WalkedGround = [span],
                WalkedGroundLineage = walkOwner,
                WorldEvidence = new(timestamp) { MeasuredLineage = captureOwner }
            });
        }
    }

    public static void TestInvalidPersistedWalkedGeometryFailsSnapshotValidation()
    {
        foreach (var invalid in new[] { new LiveWorldWalkedFloor(100, 20, 0),
                     new LiveWorldWalkedFloor(0, 20, double.NaN) })
        {
            var observation = LiveWorldObservation.Create(0, 0, 1, 640, 360, 640, 160, [],
                new(0, 0), new(0, 0)) with { WalkedGround = [invalid] };
            try
            {
                _ = new LiveWorldSnapshot(anchoredObservationId: 0, observations: [observation]);
            }
            catch (LiveWorldModelException error) when (error.Error == LiveWorldModelError.InvalidGeometry)
            {
                continue;
            }
            throw new InvalidOperationException("Invalid walked ground was accepted.");
        }
    }

    private static GroundHypothesisAtlasLine Span(double left, double right, double row) =>
        new(WalkedGroundMemory.WalkedSegmentId, new(left, row), new(right, row));

    private static void Check(bool success)
    {
        if (!success) throw new InvalidOperationException("Unexpected live-world walked ground.");
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("hkv-live-world-walk-");
        internal string Path => directory.FullName;
        public void Dispose() => directory.Delete(recursive: true);
    }
}
