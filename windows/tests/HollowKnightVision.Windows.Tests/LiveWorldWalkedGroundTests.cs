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

    public static void TestPipelineCarriesAThrottledWalkUntilItsImageOwnerIsPlaced()
    {
        using var directory = new TestDirectory();
        var failCommit = false;
        var pipeline = new WindowsLiveWorldPipeline(directory.Path, transactionObserver: null,
            transactionFault: stage => failCommit && stage == "commit");
        var floor = Span(10, 90, 80);
        var oldFoot = new LiveWalkedGroundBatch(1, [floor]);
        Check(Submit(1, 1, owner: 2).AllowsAtlasWrite);
        Check(Submit(2, 1.05, owner: 1, current: [floor]).State == WindowsLiveWorldPipelineState.Throttled);
        failCommit = true;
        Check(Submit(3, 1.2, owner: 2, carried: [oldFoot]).State == WindowsLiveWorldPipelineState.Failed);
        Check(new LiveWorldSessionStore(directory.Path).Load().Snapshot.Observations.Count == 1);
        failCommit = false;
        var carrier = Submit(4, 1.4, owner: 2, carried: [oldFoot]);
        Check(carrier.AllowsAtlasWrite && carrier.WalkedGround.Count == 0);
        var beforePlacement = new LiveWorldSessionStore(directory.Path).Load().Snapshot;
        var stored = beforePlacement.Observations[^1].GroundGeometry!;
        var savedFoot = stored.WalkedIslands!.Single();
        Check(stored.Lines.Count == 0 && beforePlacement.Observations[^1].WalkedGround is null
            && savedFoot.Owner.UnplacedSource?.Lineage == 1
            && savedFoot.Owner.Id != beforePlacement.TrackingIslands[^1].Id
            && LiveWorldWalkedGround.ProjectRoom(beforePlacement, savedFoot.Owner.RoomId).Count == 0);
        Check(Submit(5, 1.6, owner: 2, carried: [oldFoot]).AllowsAtlasWrite);
        var placed = Submit(6, 1.8, owner: 1, current: [floor]);
        Check(placed.AllowsAtlasWrite && placed.RecordedLocalWalk.SequenceEqual([floor])
            && placed.WalkedGround.SequenceEqual([floor]));
        var snapshot = new LiveWorldSessionStore(directory.Path).Load().Snapshot;
        Check(snapshot.Observations[^1].WalkedGround is null
            && snapshot.Observations.Count(row => row.GroundGeometry?.WalkedIslands?.Count > 0) == 1
            && snapshot.TrackingIslands[^1].Id == savedFoot.Owner.Id);
        var revised = snapshot.ReplaceOptimizedPoses(snapshot.Observations.ToDictionary(row => row.Id,
            row => row.Id == snapshot.Observations[^1].Id ? new AtlasPoint(9, -4) : row.OptimizedPose), snapshot.MapRevision);
        Check(LiveWorldWalkedGround.ProjectRoom(revised, savedFoot.Owner.RoomId).SequenceEqual([Span(19, 99, 76)]));

        WindowsLiveWorldPipelineResult Submit(int id, double time, long owner,
            IReadOnlyList<GroundHypothesisAtlasLine>? current = null, IReadOnlyList<LiveWalkedGroundBatch>? carried = null)
        {
            var frame = new BgraFrame(320, 180, 320 * 4, new byte[320 * 180 * 4], id, DateTimeOffset.UnixEpoch);
            return pipeline.Process(new WindowsLiveWorldFrame(frame, frame, time, time, default, 320,
                [], [], 1, "room", 1, 64)
            {
                WalkedGround = current ?? [],
                WalkedGroundLineage = owner,
                WorldEvidence = new(time) { MeasuredLineage = owner, OtherWalks = carried ?? [] }
            });
        }
    }

    public static void TestCarrierPlacementCannotHideAPlacedFootprintOrBindAReusedLineage()
    {
        var tracker = new LiveWorldTracker();
        var placed = Span(10, 70, 80);
        var unknown = Span(200, 260, 90);
        Capture(0, 7, 1);
        Capture(1, 7, 2, pending: true, [new(1, [placed]), new(3, [unknown])]);
        var stored = tracker.Snapshot.Observations[^1].GroundGeometry!.WalkedIslands!;
        var reserved = stored.Last().Owner.Id;
        Check(tracker.Snapshot.UnplacedObservationIds().Contains(1)
            && stored.First().Owner.UnplacedSource is null
            && LiveWorldWalkedGround.ProjectRoom(tracker.Snapshot, 0).SequenceEqual([placed])
            && LiveWorldGroundOverlay.Project(tracker.Snapshot).Count == 1);
        tracker.BeginVisit();
        Capture(2, 8, 3);
        Check(tracker.Snapshot.TrackingIslands[^1].Id != reserved
            && LiveWorldWalkedGround.ProjectRoom(tracker.Snapshot, 0).SequenceEqual([placed]));
        tracker.BeginVisit();
        Capture(3, 7, 3);
        Check(tracker.Snapshot.TrackingIslands[^1].Id == reserved
            && LiveWorldWalkedGround.ProjectRoom(tracker.Snapshot, 0).SequenceEqual([placed, unknown]));

        void Capture(int id, ulong generation, long owner, bool pending = false,
            IReadOnlyList<LiveWalkedGroundBatch>? carried = null)
        {
            var timestamp = id + 1;
            var frame = new BgraFrame(320, 180, 320 * 4, new byte[320 * 180 * 4], id, DateTimeOffset.UnixEpoch);
            tracker.Ingest(id, timestamp, frame, default, frame.Width, searchForClosure: false,
                captureGeneration: generation, captureEvidence: new(timestamp) { MeasuredLineage = owner },
                placementPending: pending, carriedWalks: carried);
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
