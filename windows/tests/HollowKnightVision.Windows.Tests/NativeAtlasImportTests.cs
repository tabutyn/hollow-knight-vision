using System.Text.Json;
using System.Text.Json.Nodes;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class NativeAtlasImportTests
{
    private const double SwiftDateSeconds = 816_957_222;
    private const int SourceRoom = 5;
    private const int TargetRoom = 9;
    private const int SourceIsland = 7;
    private const int TargetIsland = 12;
    private static readonly StencilRect ForegroundMask = new(2.5, 3.125, 7, 4);
    private static readonly StencilRect BottomOriginMask = new(2.5, 12.875, 7, 4);

    internal static void TestNativeAtlasPersistsMasksIslandsAndDoorway()
    {
        WithFixture((root, store, payload) =>
        {
            var original = LiveWorldSessionStore.ValidateExisting(root).Snapshot;
            Check(original.SchemaVersion == 3 && original.Observations.Count == 6, "native graph");
            Check(original.CompositionMasks(original.Observations[0]).SequenceEqual([ForegroundMask]),
                "restore must flip masks to top-origin presentation exactly once");
            Check(original.Observations[0].ExcludedRects[0].Y == BottomOriginMask.Y, "immutable source mask");
            Check(original.TrackingIslands.Select(island => island.Id).SequenceEqual([SourceIsland, TargetIsland]),
                "both rigid islands");
            Check(original.DoorwayFloorConstraints.Single().ArrivalSupport.SequenceEqual([3, 4, 5]),
                "doorway support survives import");
            Check(original.Observations[0].CaptureTimestamp == 0.99
                && original.Observations[3].ObservedFloorCandidates?.Lines[0].SegmentId == 3
                && original.Observations[4].ObservedPlayerBounds?.Y == 12,
                "optional native capture geometry");
            var ground = LiveWorldWalkedGround.ProjectRoom(original, SourceRoom).Single();
            Check(ground.AtlasStart == new AtlasPoint(-30, 90) && ground.AtlasEnd == new AtlasPoint(50, 90),
                "walked span uses its island coordinates");

            var horizontal = original.TranslateRoom(SourceRoom, new(8, 0));
            Check(horizontal.TrackingIslands[0].WorldFromLocal == new AtlasPoint(8, 70)
                && horizontal.TrackingIslands[0].Members.SequenceEqual(original.TrackingIslands[0].Members),
                "room translation preserves rigid member poses");
            Check(LiveWorldWalkedGround.ProjectRoom(horizontal, SourceRoom).Single().AtlasStart == new AtlasPoint(-22, 90),
                "walked geometry moves with its island");
            Check(store.Commit(horizontal, original.MapRevision, horizontal.MapRevision), "commit rigid translation");
            var frame = Frame();
            var request = new WindowsLiveWorldFrame(frame, frame, 42, 42, new(100, 9), 640,
                [ForegroundMask], [], 1, "room", 1, 256);
            var timestamp = WindowsLiveWorldPipeline.StoredCaptureTimestamp(request, horizontal);
            Check(timestamp > horizontal.Observations[^1].Timestamp
                && WindowsLiveWorldPipeline.StoredCaptureTimestamp(request, new LiveWorldSnapshot()) == 42,
                "reopening preserves the saved clock while keeping the Windows live clock");
            var evidence = store.Append(frame, timestamp, new(100, 9), 640, [ForegroundMask]);
            Check(evidence.ExcludedRects.SequenceEqual([BottomOriginMask]), "new evidence uses native masks");

            var tracker = new LiveWorldTracker(horizontal);
            var update = tracker.Ingest(evidence.Id, evidence.Timestamp, Frame(), evidence.CameraPosition,
                evidence.SolveWidth, exclusions: evidence.ExcludedRects, searchForClosure: false, roomId: TargetRoom);
            Check(store.Commit(update.Snapshot, horizontal.MapRevision, update.Revision), "resume native graph");
            var reopened = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(reopened.Observations.Count == 7 && reopened.TrackingIslands.Count == 3,
                "appended observation owns one additional island");
            Check(reopened.TrackingIslands[^1].Placement == LiveIslandPlacement.LegacyUnknown
                && !reopened.TrackingIslands[^1].Members.Single().Measured, "new Windows capture has unknown island lineage");
            Check(reopened.DoorwayFloorConstraints.Count == 1, "saved doorway survives the new capture");
            Check(reopened.Observations[0].GroundGeometry!.WalkedIslands![0].Spans[0].SegmentId == long.MaxValue,
                "the original walked segment ID is preserved");

            using var envelope = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "scene.snapshot")));
            using var saved = JsonDocument.Parse(envelope.RootElement.GetProperty("data").GetBytesFromBase64());
            Check(saved.RootElement.GetProperty("anchoredObservationID").GetInt32() == 0
                && saved.RootElement.GetProperty("trackingIslands")[1].GetProperty("roomID").GetInt32() == TargetRoom,
                "Swift ID key spelling");
            var exclusions = saved.RootElement.GetProperty("observations")[6].GetProperty("excludedRects");
            Check(exclusions[0][0][1].GetDouble() == BottomOriginMask.Y, "Swift nested rectangle shape");
            Check(new SceneSessionStore(root).Manifest.UsesSwiftWireFormat, "manifest keeps Swift spelling");
        });
    }

    internal static void TestCorruptNativeAtlasAndLegacyOwners()
    {
        WithFixture((root, store, payload) =>
        {
            var shear = JsonNode.Parse(payload)!;
            shear["observations"]![0]!["optimizedPose"]![0] = 1;
            Reject(shear, LiveWorldModelError.InvalidIslandMembership);
            var brokenDoor = JsonNode.Parse(payload)!;
            brokenDoor["doorwayFloorConstraints"]![0]!["arrivalSupport"]![0] = 4;
            Reject(brokenDoor, LiveWorldModelError.InvalidDoorwayFloorConstraint);
            var brokenMask = JsonNode.Parse(payload)!;
            brokenMask["observations"]![0]!["excludedRects"]![0] = JsonNode.Parse("[[2.5],[7,4]]");
            try
            {
                _ = LiveWorldJson.ReadSnapshot(JsonSerializer.SerializeToUtf8Bytes(brokenMask));
                throw new InvalidOperationException("A malformed native mask was accepted.");
            }
            catch (JsonException) { }

            var legacyNative = JsonNode.Parse(payload)!.AsObject();
            legacyNative["schemaVersion"] = 2;
            legacyNative.Remove("trackingIslands");
            legacyNative.Remove("doorwayFloorConstraints");
            var migrated = LiveWorldJson.ReadSnapshot(JsonSerializer.SerializeToUtf8Bytes(legacyNative));
            Check(migrated.TrackingIslands.Count == 6 && migrated.TrackingIslands.All(island =>
                island.Placement == LiveIslandPlacement.LegacyUnknown
                && !island.Members.Single().Measured
                && island.WorldFromLocal == migrated.Observations[island.Id].OptimizedPose),
                "older Mac capture without explicit ownership preserves each pose");

            var windows = new LiveWorldSnapshot();
            var unchanged = LiveWorldJson.ReadSnapshot(LiveWorldJson.WriteSnapshot(windows));
            Check(unchanged.SchemaVersion == 1 && !unchanged.UsesSwiftWireFormat
                && unchanged.TrackingIslands.Count == 0, "legacy Windows format");
        });
    }

    internal static void TestNamedAtlasRestoreRebuildsTheWindowsMosaic()
    {
        WithFixture((root, store, payload) =>
        {
            var temporary = Directory.GetParent(root)!.FullName;
            var states = new AtlasStateStore(Path.Combine(temporary, "states"));
            var saved = states.Save("Mac Atlas", destination => Directory.Move(root, destination));
            var original = states.WorldPath(saved);
            var originalGraph = File.ReadAllBytes(Path.Combine(original, "scene.snapshot"));
            var active = Path.Combine(temporary, "active");
            var loaded = VisionResetController.LoadNamedAtlasForCurrentProfile(states, saved, active,
                activateWorld: folder =>
                {
                    var pipeline = new WindowsLiveWorldPipeline(Path.Combine(folder, "live-world"));
                    Check(pipeline.Reopen(), "the desktop can resume the imported live world");
                    Check(pipeline.SavedWorld.DisplayLines.Any(line => line.SegmentId != WalkedGroundMemory.WalkedSegmentId
                        && line.AtlasStart == new AtlasPoint(-270, -110)),
                        "confirmed green ground accompanies imported Atlas tiles before the first live frame");
                },
                saveTile: HollowKnightVision.Windows.Host.BmpWriter.Write);

            Check(loaded.ValidatedWorld.Mosaic.TileCount > 0
                && loaded.ValidatedWorld.Metadata.RegistrationCount == 6, "rebuilt a registered tile mosaic");
            var restored = LiveWorldSessionStore.ValidateExisting(Path.Combine(active, "live-world")).Snapshot;
            Check(restored.TrackingIslands.Count == 2 && restored.DoorwayFloorConstraints.Count == 1,
                "restore preserved the native graph under the active atlas");
            Check(File.ReadAllBytes(Path.Combine(original, "scene.snapshot")).SequenceEqual(originalGraph)
                && !File.Exists(Path.Combine(original, "atlas.json")), "the source named state is immutable");
        });
    }

    internal static void TestImportedAtlasRequiresTwoMatchesInTheSameRoom()
    {
        WithRecoveryFixture(identicalRooms: false, (root, before) =>
        {
            var pipeline = new WindowsLiveWorldPipeline(root);
            Check(pipeline.HasDurableEvidence, "Normal gameplay can recognize a restored Swift world");
            var unknown = pipeline.Process(ResumeFrame(1, 901));
            Check(unknown.State == WindowsLiveWorldPipelineState.PendingRecovery && !unknown.AllowsAtlasWrite,
                "unrelated scenery leaves the restored map pending");
            var first = pipeline.Process(ResumeFrame(2, 71));
            var switched = pipeline.Process(ResumeFrame(3, 191));
            Check(first.ConfirmedRecoveryPose is null && switched.ConfirmedRecoveryPose is null,
                "two matches in different Atlas rooms cannot confirm a scene");
            var confirmed = pipeline.Process(ResumeFrame(4, 191));
            Check(confirmed.ConfirmedRecoveryPose == new AtlasPoint(100, 20) && !confirmed.AllowsAtlasWrite
                && confirmed.RegisteredPath(0) is null,
                "a verified second frame places the camera without writing recovery evidence");
            var afterRecovery = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(afterRecovery.MapRevision == before.MapRevision && afterRecovery.Observations.Count == 2
                && afterRecovery.Landmarks.Count == 0, "regenerated descriptors stay outside the graph");

            var next = pipeline.Process(ResumeFrame(5, 191, localX: 84) with
            {
                WorldEvidence = new(1.5, new([new(44, new(20, 96), new(200, 96))]))
            });
            var persisted = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(next.State == WindowsLiveWorldPipelineState.Committed
                && persisted.Observations[^1].RoomId == TargetRoom
                && persisted.Observations[^1].OptimizedPose == new AtlasPoint(104, 20),
                "the next frame continues inside the matched room in Atlas coordinates");
            Check(next.RegisteredPath(0) is { Room: { RoomId: TargetRoom } path }
                && path.Ground.Any(line => line.SegmentId == 44
                    && line.AtlasStart == new AtlasPoint(-36, 26))
                && next.RegisteredPath(1) is null,
                "saved route publication uses the recovered room and fences an obsolete coordinate epoch");

            pipeline.NotifyTrackingLoss(1);
            Check(pipeline.Process(ResumeFrame(6, 191)).ConfirmedRecoveryPose is null
                && pipeline.Process(ResumeFrame(7, 191)).ConfirmedRecoveryPose == new AtlasPoint(100, 20),
                "local loss uses the recovered room's regenerated reference");
        });
    }

    internal static void TestImportedAtlasRejectsIdenticalSceneryAcrossRooms()
    {
        WithRecoveryFixture(identicalRooms: true, (root, before) =>
        {
            var pipeline = new WindowsLiveWorldPipeline(root);
            var first = pipeline.Process(ResumeFrame(1, 71));
            var second = pipeline.Process(ResumeFrame(2, 71));
            Check(first.ConfirmedRecoveryPose is null && second.ConfirmedRecoveryPose is null
                && second.Diagnostic.Contains("AmbiguousPlacement", StringComparison.Ordinal),
                "the same image and camera pose in two rooms cannot identify an imported room");
            Check(new LiveWorldSessionStore(root).Load().Snapshot.MapRevision == before.MapRevision
                && new SceneSessionStore(root).Manifest.Observations.Count == 2,
                "ambiguous frames cannot overwrite tiles or append evidence");
        });
    }

    internal static void TestLateFramesCannotInterruptSavedRoomRecovery()
    {
        WithRecoveryFixture(identicalRooms: false, (root, before) =>
        {
            var pipeline = new WindowsLiveWorldPipeline(root);
            var first = pipeline.Process(ResumeFrame(10, 191));
            Check(first is { State: WindowsLiveWorldPipelineState.PendingRecovery, ConfirmedRecoveryPose: null },
                "fresh recovery starts with a single match in the saved arrival room");

            var delayedRoom = ResumeFrame(99, 71) with
            {
                RoomName = "Tutorial_Before_Load", Timestamp = 2.9, MonotonicTimestamp = 2.9
            };
            CheckDiscarded(pipeline.Process(delayedRoom), "stale world timestamp",
                "a delayed frame from the departure room cannot replace an uncommitted recovery vote");
            var duplicateId = ResumeFrame(10, 71) with
            {
                RoomName = "Tutorial_Before_Load", Timestamp = 3.1, MonotonicTimestamp = 3.1
            };
            CheckDiscarded(pipeline.Process(duplicateId), "stale analysis frame",
                "a reused frame identifier cannot start another room visit");
            CheckDiscarded(pipeline.Process(delayedRoom with { CaptureGeneration = 2, MonotonicTimestamp = double.NaN }),
                "invalid frame timestamp",
                "an invalid clock cannot retire the active capture generation");

            var recovered = pipeline.Process(ResumeFrame(11, 191));
            Check(recovered.ConfirmedRecoveryPose == new AtlasPoint(100, 20),
                "the next fresh image completes the existing room match");
            var continued = pipeline.Process(ResumeFrame(12, 191, localX: 84));
            var snapshot = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(continued.AllowsAtlasWrite && continued.CameraPose == new AtlasPoint(104, 20)
                && snapshot.Observations.Count == before.Observations.Count + 1
                && snapshot.Observations[^1].RoomId == TargetRoom,
                "the next commit extends the saved arrival room from its recovered pose");
        });
    }

    internal static void TestAtlasOrderingSurvivesThrottlingAndRollback()
    {
        var root = Directory.CreateTempSubdirectory("hkv-ordered-room-recovery-");
        try
        {
            var failAppend = false;
            var pipeline = new WindowsLiveWorldPipeline(root.FullName, transactionObserver: null,
                transactionFault: stage => failAppend && stage == "append");
            var first = pipeline.Process(ResumeFrame(1, 71));
            Check(first.AllowsAtlasWrite && first.CommittedGraph is { Observations.Count: 1 } baseline
                && baseline.Observations[0].RoomId == first.CommittedRoomId, "the first frame publishes its committed graph");
            var throttled = ResumeFrame(2, 71) with { Timestamp = .35, MonotonicTimestamp = .35 };
            var betweenCommits = pipeline.Process(throttled);
            Check(betweenCommits.State == WindowsLiveWorldPipelineState.Throttled
                && ReferenceEquals(first.CommittedGraph, betweenCommits.CommittedGraph),
                "analysis between persistence ticks must keep the same committed graph");
            var delayed = ResumeFrame(3, 191) with
            {
                RoomName = "Tutorial_Other", Timestamp = .32, MonotonicTimestamp = .32
            };
            CheckDiscarded(pipeline.Process(delayed), "stale world timestamp",
                "a newer capture ID cannot replay time before a throttled frame");

            failAppend = true;
            Check(pipeline.Process(ResumeFrame(3, 71)) is { State: WindowsLiveWorldPipelineState.Failed, CommittedGraph: null },
                "an injected write failure cannot publish an uncommitted graph");
            failAppend = false;
            var oldCapture = ResumeFrame(5, 191) with { CaptureGeneration = 0, RoomName = "Tutorial_Other" };
            CheckDiscarded(pipeline.Process(oldCapture), "stale capture generation",
                "rolling back persistence retains the live capture generation fence");
            var oldTime = ResumeFrame(5, 191) with { Timestamp = .8, MonotonicTimestamp = .8 };
            CheckDiscarded(pipeline.Process(oldTime), "stale world timestamp",
                "rolling back persistence retains the admitted capture clock");
            Check(pipeline.Process(ResumeFrame(4, 71)).AllowsAtlasWrite,
                "fresh input continues the saved room at the existing placement");
            var snapshot = new LiveWorldSessionStore(root.FullName).Load().Snapshot;
            Check(snapshot.Observations.Count == 2 && snapshot.Observations.Select(row => row.RoomId).Distinct().Count() == 1,
                "the replayed room cannot leak into the durable world");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestReopenedAtlasRecoversFromFloorWithoutNarrowLandmarks()
    {
        foreach (var visual in new[] { true, false })
        {
            var root = Directory.CreateTempSubdirectory("hkv-floor-only-restore-");
            try
            {
                var pipeline = new WindowsLiveWorldPipeline(root.FullName);
                var room = visual ? "normal:original:0" : "Floor_only_saved_room";
                var initial = MeasuredRecoveryFrame(1, .5, 71, new(80, 20)) with
                    { RoomName = room, RoomIdentityIsVisual = visual };
                Check(pipeline.Process(initial).AllowsAtlasComposition, "store the placed floor view");
                var store = new LiveWorldSessionStore(root.FullName);
                var before = store.Load().Snapshot;
                Check(before.Keyframes.Count == 1 && before.Landmarks.Count == 0, "this view has no narrow-band landmarks");

                pipeline = new WindowsLiveWorldPipeline(root.FullName);
                WindowsLiveWorldFrame Revisit(long id, double time, double localX) =>
                    MeasuredRecoveryFrame(id, time, 71, new(localX, 35)) with
                    { RoomName = visual ? "normal:revisit:0" : room, RoomIdentityIsVisual = visual, CaptureGeneration = 2 };
                var first = pipeline.Process(Revisit(2, 1, 120));
                Check(first.ConfirmedRecoveryPose is null && first.Diagnostic.Contains("ConfirmationRequired", StringComparison.Ordinal),
                    $"the first broad floor match is tentative ({first.Diagnostic})");
                var confirmed = pipeline.Process(Revisit(3, 1.3, 120));
                Check(confirmed.ConfirmedRecoveryPose == new AtlasPoint(80, 20) && !confirmed.AllowsAtlasWrite,
                    $"two fresh floor matches locate the saved scene ({confirmed.Diagnostic})");
                Check(store.Load().Snapshot.MapRevision == before.MapRevision
                    && new SceneSessionStore(root.FullName).Manifest.Observations.Count == 1,
                    "transient floor recovery keeps both captures outside the saved graph");
                var continued = pipeline.Process(Revisit(4, 1.6, 124));
                var placed = store.Load().Snapshot.Observations[^1];
                Check(continued.AllowsAtlasComposition && placed.RoomId == before.Observations[0].RoomId
                    && placed.OptimizedPose == new AtlasPoint(84, 20), "the next frame follows the restored room's coordinate basis");
            }
            finally { root.Delete(recursive: true); }
        }
    }

    internal static void TestFloorOnlyRecoveryRejectsCompetingRoomsAndStaleVotes()
    {
        var first = MeasuredRecoveryFrame(1, .5, 71, new(80, 20));
        var texture = first.Frame;
        var placed = new[] { 0, 1 }.Select(id =>
            LiveWorldObservation.Create(id, id, id + .5, texture.Width, texture.Height, first.SolveWidth,
                160, [], first.LocalPose, first.LocalPose, roomId: id) with
                { GroundGeometry = first.WorldEvidence!.GroundGeometry }).ToArray();
        var snapshot = new LiveWorldSnapshot(anchoredObservationId: 0, observations: placed,
            keyframes: [new(0, 0, 0), new(1, 1, 1)]);
        var tracker = new LiveWorldTracker(snapshot);
        LiveWorldRecoveryUpdate Scan(double time, int? room) => tracker.RecoverFromPlacedGround(texture,
            first.LocalPose, first.SolveWidth, first.WorldEvidence! with { CaptureTimestamp = time },
            [], _ => texture, _ => [], room);

        var ambiguous = Scan(1, null);
        Check(ambiguous.ConfirmedPlacement is null
            && ambiguous.Review.Rejections.Contains(LiveWorldTrackerRejection.AmbiguousPlacement),
            "identical scenery in separate rooms stays ambiguous even at the same coordinates");
        Check(Scan(2, 0).ConfirmedPlacement is null && Scan(2, 0).ConfirmedPlacement is null,
            "submitting the same capture twice never confirms it");
        Check(Scan(4, 0).ConfirmedPlacement is null && Scan(4.3, 1).ConfirmedPlacement is null,
            "a stale vote and a vote from a different room each require a fresh confirmation");
        Check(Scan(4.6, 1).ConfirmedPlacement?.RoomId == 1 && ReferenceEquals(tracker.Snapshot, snapshot),
            "two fresh same-room votes confirm without mutating the saved graph");
    }

    internal static void TestImportedAtlasExtendsThroughAConfirmedDoorway()
    {
        foreach (var direction in new[] { VisualRoomDirection.Left, VisualRoomDirection.Right })
        {
            WithRecoveryFixture(identicalRooms: false, (root, before) =>
            {
                var pipeline = new WindowsLiveWorldPipeline(root);
                _ = pipeline.Process(ResumeFrame(1, 71));
                var anchor = pipeline.Process(ResumeFrame(2, 71));
                Check(anchor.ConfirmedRecoveryPose == new AtlasPoint(100, 20), "recover the departure room");
                pipeline.NotifyTrackingLoss(1);
                var cut = new VisualSceneDiscontinuityTracker.Cut(.75, 1.9, new(80, 0), direction);
                var target = "Unmapped_after_doorway";
                var surface = new DetectedFloorLine(135, new PixelSpan(20, 160),
                    [new PixelSpan(20, 61), new PixelSpan(94, 160)]);
                var competing = new DetectedFloorLine(115, new PixelSpan(40, 100));
                WindowsLiveWorldFrame Arrive(long id) => ResumeFrame(id, 901) with
                {
                    RoomName = target,
                    DoorwayCut = cut,
                    WorldEvidence = LiveWorldCaptureEvidence.FromGround(id * .3, 320, 180,
                        [surface, competing], [new(17, surface.ToSemanticLine(), GroundLinePresenceState.Confirmed, .8, 1)],
                        new PixelRect(20, 100, 24, 35), new AtlasPoint(30, 135))
                };
                for (var id = 7; id <= 9; id++)
                {
                    var search = pipeline.Process(Arrive(id));
                    Check(search.State == WindowsLiveWorldPipelineState.PendingRecovery
                        && search.ConfirmedRecoveryPose is null && !search.AllowsAtlasWrite,
                        "room search and provisional positioning do not write images");
                }
                Check(new LiveWorldSessionStore(root).Load().Snapshot.MapRevision == before.MapRevision,
                    "existing Atlas is untouched during entry");
                var recorded = pipeline.Process(Arrive(10));
                var after = new LiveWorldSessionStore(root).Load().Snapshot;
                var entered = after.Observations[^1];
                var expectedX = direction == VisualRoomDirection.Right ? 452 : -252;
                Check(recorded.AllowsAtlasWrite && entered.OptimizedPose == new AtlasPoint(expectedX, 20)
                    && entered.RoomId == WindowsLiveWorldPipeline.RoomIdentifier(target)
                    && after.Observations.Take(2).Select(row => row.OptimizedPose)
                        .SequenceEqual(before.Observations.Select(row => row.OptimizedPose)),
                    "new imagery begins beyond the restored footprint while retaining the vertical basis");

                Check(entered.CaptureTimestamp == 3
                    && entered.GroundGeometry!.Lines.SequenceEqual([
                        new LiveGroundLineEvidence(17, new(20, 45), new(62, 45)),
                        new LiveGroundLineEvidence(17, new(94, 45), new(161, 45))])
                    && entered.ObservedFloorCandidates!.Lines.Count == 3
                    && entered.ObservedPlayerBounds == new WorldRect(new(20, 45), new(24, 35)),
                    "observed spans, intervening gap, candidates and measured foot survive reopen");

                Check(pipeline.Process(Arrive(11) with { LocalPose = new(84, 0) }).AllowsAtlasWrite,
                    "successive measured normal captures extend the new room");
                var joined = new LiveWorldSessionStore(root).Load().Snapshot;
                var island = joined.TrackingIslands[^1];
                Check(island.Placement == LiveIslandPlacement.Estimated && island.Members.Count == 2
                    && island.Members.All(member => member.Measured)
                    && island.Members[1].LocalPose.X - island.Members[0].LocalPose.X == 4,
                    "continuous measured captures share a rigid estimated island");
                Check(pipeline.Process(Arrive(13)).AllowsAtlasWrite, "a separate measured frame can be stored after a gap");
                var separated = new LiveWorldSessionStore(root).Load().Snapshot;
                Check(separated.TrackingIslands.Count == joined.TrackingIslands.Count + 1
                    && separated.TrackingIslands[^1].Members.Count == 1,
                    "a gap without a camera measurement starts a separate island");

                for (var id = 14; id <= 17; id++)
                {
                    var staleCut = pipeline.Process(ResumeFrame(id, 744) with
                    {
                        RoomName = "Unrelated_third_scene",
                        DoorwayCut = cut
                    });
                    Check(!staleCut.AllowsAtlasWrite, "the previous doorway cannot be reused for another scene");
                }
                Check(new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count == 5,
                    "no evidence is appended for the unconfirmed third scene");
            });
        }
    }

    internal static void TestImportedAtlasKeepsSearchingForAKnownDoorwayArrival()
    {
        WithRecoveryFixture(identicalRooms: false, (root, before) =>
        {
            var pipeline = new WindowsLiveWorldPipeline(root);
            _ = pipeline.Process(ResumeFrame(1, 71));
            _ = pipeline.Process(ResumeFrame(2, 71));
            var cut = new VisualSceneDiscontinuityTracker.Cut(.75, 1.9, new(80, 0), VisualRoomDirection.Right);
            for (var id = 7; id <= 11; id++)
            {
                var search = pipeline.Process(ResumeFrame(id, id == 7 ? 191 : 901) with
                {
                    RoomName = "Another_previously_mapped_room",
                    DoorwayCut = cut
                });
                Check(search.ConfirmedRecoveryPose is null && !search.AllowsAtlasWrite,
                    "a competing match prevents the creation of a duplicate room");
            }
            Check(new LiveWorldSessionStore(root).Load().Snapshot.MapRevision == before.MapRevision,
                "a tentative known room is always recovered before writing");
        });
    }

    internal static void TestMeasuredWalkSavesFloorUnderAHiddenEdge()
    {
        var observed = LiveWorldCaptureEvidence.FromGround(2, 320, 180, [], [], null, null,
            supportedFloor: new CleanFloorLine(135, new PixelSpan(65, 119)));
        Check(observed.FloorCandidates is null && observed.GroundGeometry!.Lines.SequenceEqual([
            new LiveGroundLineEvidence(long.MaxValue, new(65, 45), new(120, 45))]),
            "independent ground contact saves the observed traversal span");
        var missing = LiveWorldCaptureEvidence.FromGround(3, 320, 180, [], [], null, null);
        Check(missing.GroundGeometry is null, "no current measurement cannot recreate a hidden floor");
    }

    internal static void TestDoorwayFloorAlignsOnlyWithMeasuredContactOnBothSides()
    {
        foreach (var (direction, airborne, competingSurface) in new[]
        {
            (VisualRoomDirection.Right, false, false),
            (VisualRoomDirection.Left, false, false),
            (VisualRoomDirection.Right, true, false),
            (VisualRoomDirection.Right, false, true)
        })
        {
            WithRecoveryFixture(identicalRooms: false, (root, saved) =>
            {
                var pipeline = new WindowsLiveWorldPipeline(root);
                _ = pipeline.Process(ResumeFrame(1, 71));
                Check(pipeline.Process(ResumeFrame(2, 71)).ConfirmedRecoveryPose is not null, "recover the departure");

                var exitsRight = direction == VisualRoomDirection.Right;
                for (var id = 3; id <= 5; id++)
                {
                    Check(pipeline.Process(FloorFrame(id, 71, exitsRight, 135, false, false)).AllowsAtlasWrite,
                        "save measured departure samples");
                }

                pipeline.NotifyTrackingLoss(1);
                var cut = new VisualSceneDiscontinuityTracker.Cut(1.65, 2.5, new(80, 0), direction);
                const string arrivingScene = "New_room_past_the_door";
                WindowsLiveWorldFrame Arrive(long id) => FloorFrame(id, 901, !exitsRight, 150, airborne, competingSurface) with
                {
                    RoomName = arrivingScene,
                    DoorwayCut = cut
                };
                for (var id = 9; id <= 11; id++)
                    Check(!pipeline.Process(Arrive(id)).AllowsAtlasWrite, "find an unmapped arrival without writing");

                WindowsLiveWorldPipelineResult? last = null;
                for (var id = 12; id <= 14; id++)
                {
                    last = pipeline.Process(Arrive(id));
                    Check(last.AllowsAtlasWrite, "save measured arrival samples");
                }
                var snapshot = new LiveWorldSessionStore(root).Load().Snapshot;
                var placement = last!.SavedWorld!.Placement;
                Check(snapshot.Observations.Take(saved.Observations.Count).Select(frame => frame.OptimizedPose)
                    .SequenceEqual(saved.Observations.Select(frame => frame.OptimizedPose)),
                    "doorway alignment preserves the restored poses");
                var expectedX = exitsRight ? 452 : -252;
                if (airborne || competingSurface)
                {
                    Check(snapshot.DoorwayFloorConstraints.Count == 0 && last!.RebuiltAtlas is null
                        && placement.Doorways.Count == 0 && placement.EstimatedFootprints.Count > 0
                        && snapshot.Observations[^1].OptimizedPose == new AtlasPoint(expectedX, 20),
                        "ambiguous or airborne evidence leaves height provisional");
                    Check(placement.RoomDoorways.Count == 0, "Unconfirmed crossings must not become policy doors.");
                    return;
                }

                var relation = snapshot.DoorwayFloorConstraints.Single();
                var departure = snapshot.Observations.Single(row => row.Id == relation.DepartureObservationId);
                var arrival = snapshot.Observations.Single(row => row.Id == relation.ArrivalObservationId);
                Check(relation.Direction == (int)direction && relation.DepartureSupport.Count == 3
                    && relation.ArrivalSupport.Count == 3, "the saved doorway names three source and target images");
                Check(departure.OptimizedPose.Y + relation.DepartureFloor.Y
                    == arrival.OptimizedPose.Y + relation.ArrivalFloor.Y, "the doorway floor aligns exactly");
                var connector = placement.Doorways.Single();
                var departureOffset = LiveWorldGroundOverlay.ImageOriginOffset(departure);
                var arrivalOffset = LiveWorldGroundOverlay.ImageOriginOffset(arrival);
                Check(connector == new LiveWorldDoorwayStroke(
                    new(departure.OptimizedPose.X + departureOffset.X + relation.DepartureFloor.X,
                        departure.OptimizedPose.Y + departureOffset.Y + relation.DepartureFloor.Y),
                    new(arrival.OptimizedPose.X + arrivalOffset.X + relation.ArrivalFloor.X,
                        arrival.OptimizedPose.Y + arrivalOffset.Y + relation.ArrivalFloor.Y))
                    && connector.Start.Y == connector.End.Y && placement.EstimatedFootprints.Count > 0,
                    "The registered display must use the shared, corrected floor row and retain the estimated footprint.");
                var reopened = new WindowsLiveWorldPipeline(root).SavedWorld.Placement;
                Check(reopened.Doorways.SequenceEqual(placement.Doorways)
                    && reopened.EstimatedFootprints.SequenceEqual(placement.EstimatedFootprints)
                    && reopened.RoomDoorways[arrival.RoomId].SequenceEqual(placement.RoomDoorways[arrival.RoomId]),
                    "The same measured connector and estimated footprints must be available before another live frame.");
                Check(placement.RoomDoorways.Count == 2
                    && placement.RoomDoorways[departure.RoomId].Single().Start == connector.Start
                    && placement.RoomDoorways[arrival.RoomId].Single() == new LiveWorldDoorwayStroke(
                        connector.End, new(connector.End.X, connector.End.Y + 35)),
                    "Each room should own the doorway's corrected floor and proven clearance.");
                Check(ReferenceEquals(last.BotDoorPlacement(last.WalkedCoordinateEpoch), placement)
                    && last.BotDoorPlacement(last.WalkedCoordinateEpoch + 1) is null
                    && (last with { PlacementPending = true }).BotDoorPlacement(last.WalkedCoordinateEpoch) is null
                    && (last with { State = WindowsLiveWorldPipelineState.Failed }).BotDoorPlacement(last.WalkedCoordinateEpoch) is null,
                    "Policy door evidence needs committed placement in the current coordinate epoch.");
                Check(last!.RebuiltAtlas is { TileCount: > 0 } && last.AcceptedClosure is null
                    && last.CameraPose == new AtlasPoint(expectedX, 35)
                    && last.AppliedCorrection == new AtlasPoint(0, 15),
                    "the durable graph, tiles and live camera agree after a vertical-only correction");
                var obscured = Arrive(15) with { WorldEvidence = null, LocalPose = new(98, 0) };
                var continued = pipeline.Process(obscured);
                Check(continued.AllowsAtlasWrite && continued.CameraPose == new AtlasPoint(expectedX + 18, 35),
                    "subsequent frames retain the aligned basis while the floor is occluded");
                var continuedPlacement = continued.SavedWorld!.Placement;
                Check(continuedPlacement.Doorways.SequenceEqual(placement.Doorways)
                    && continuedPlacement.EstimatedFootprints.Except(placement.EstimatedFootprints).Any(),
                    "a registered image without floor evidence must still update placement outlines without moving the join");
                var newer = Arrive(16) with { MonotonicTimestamp = obscured.MonotonicTimestamp + .01, WorldEvidence = null };
                var throttled = pipeline.Process(newer);
                Check(throttled.State == WindowsLiveWorldPipelineState.Throttled
                    && ReferenceEquals(throttled.SavedWorld, continued.SavedWorld),
                    "analysis between Atlas writes must keep the previous placement projection");
            });
        }
    }

    internal static void TestWindowsAtlasExtendsAndAlignsWithoutMovingLegacyFrames() =>
        VerifyWindowsDoorwayCommit(failAlignmentCommit: false);

    internal static void TestReturningDoorwayAlignsAnEstimatedRoomAgainstSavedImagery()
    {
        VerifyReturningDoorway(visualRoom: false);
        VerifyReturningDoorway(visualRoom: true);
    }

    private static void VerifyReturningDoorway(bool visualRoom)
    {
        var directory = Directory.CreateTempSubdirectory("hkv-return-doorway-");
        try
        {
            var root = directory.FullName;
            var pipeline = new WindowsLiveWorldPipeline(root);
            var store = new LiveWorldSessionStore(root);
            const string sourceRoom = "Doorway_start";
            const string targetRoom = "Doorway_other_side";
            WindowsLiveWorldFrame Departure(long id) => FloorFrame(id, 71, true, 135, false, false) with
            {
                RoomName = sourceRoom,
                RoomIdentityIsVisual = visualRoom
            };
            for (var id = 1; id <= 4; id++)
                Check(pipeline.Process(Departure(id)).AllowsAtlasWrite, "save the first room and its exit floor");

            pipeline.NotifyTrackingLoss(1);
            var outward = new VisualSceneDiscontinuityTracker.Cut(1.2, 1.8, new(80, 0), VisualRoomDirection.Right);
            WindowsLiveWorldFrame OtherSide(long id, bool airborne) => FloorFrame(id, 901, false, 150, airborne, false) with
            {
                RoomName = targetRoom,
                RoomIdentityIsVisual = visualRoom,
                DoorwayCut = outward
            };
            for (var id = 6; id <= 20; id++)
            {
                var arrival = pipeline.Process(OtherSide(id, airborne: id <= 16));
                if (id >= 17)
                    Check(arrival.AllowsAtlasWrite, $"save the second room's floor after the first doorway expires ({arrival.Diagnostic})");
            }
            var estimated = store.Load().Snapshot;
            Check(estimated.DoorwayFloorConstraints.Count == 0, "the airborne entry did not measure a doorway");

            pipeline.NotifyTrackingLoss(1);
            var returning = new VisualSceneDiscontinuityTracker.Cut(6, 6.6, new(432, 0), VisualRoomDirection.Left);
            WindowsLiveWorldFrame Return(long id) => Departure(id) with { DoorwayCut = returning };
            _ = pipeline.Process(Return(22));
            Check(pipeline.Process(Return(23)).ConfirmedRecoveryPose is not null,
                "recognize the saved first room before accepting return images");
            _ = pipeline.Process(Return(24));
            _ = pipeline.Process(Return(25));
            var aligned = pipeline.Process(Return(26));
            var reloaded = store.Load().Snapshot;
            Check(reloaded.DoorwayFloorConstraints.Count == 1,
                $"the grounded return connects the two rooms ({aligned.Diagnostic})");
            var edge = reloaded.DoorwayFloorConstraints.Single();
            var previousImages = reloaded.Observations.Take(estimated.Observations.Count).ToArray();
            var targetId = estimated.Observations[^1].RoomId;
            Check(aligned is { RebuiltAtlas.TileCount: > 0, CameraPose.Y: 0 } && edge.Direction == -1
                && edge.DepartureSupport.Count == 3 && edge.ArrivalSupport.Count == 3,
                "the return verifies and publishes a measured floor connection");
            Check(previousImages.Zip(estimated.Observations).All(pair =>
                pair.First.RoomId == pair.Second.RoomId && pair.First.RawPose == pair.Second.RawPose
                && pair.First.OptimizedPose.X == pair.Second.OptimizedPose.X
                && pair.First.OptimizedPose.Y == pair.Second.OptimizedPose.Y + (pair.First.RoomId == targetId ? 15 : 0)),
                "only the estimated floor component moves and all of its old images move together");
            Check(LiveDoorwayFloorEvidence.Propose(reloaded, new(
                targetId, estimated.Observations[0].RoomId, 1, 6, 6.6, VisualRoomDirection.Left)) is null,
                "replaying the same crossing cannot append duplicate evidence");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static void TestGymRetainsGroundedReturnDoorwaysAfterImageRecovery()
    {
        var atlas = new BotGymAtlasCoverage();
        var tracking = GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 8
        };
        var origin = new AtlasPoint(0, 0);
        var size = new WorldSize(320, 180);
        atlas.EnterRoom(0);
        for (var id = 1; id <= 4; id++) Observe(FloorFrame(id, 71, true, 135, false, false));
        atlas.EnterRoom(1, new(0, 1, 1, 1.2, 1.8, VisualRoomDirection.Right));
        for (var id = 6; id <= 10; id++) Observe(FloorFrame(id, 901, false, 150, false, false));
        var placed = atlas.View(origin, size, 5);
        Check(atlas.Snapshot.DoorwayFloorConstraints.Count == 1 && placed is not null,
            "visual Gym starts with one measured connection into the new room");
        atlas.EnterRoom(0, new(1, 0, 1, 3, 3.6, VisualRoomDirection.Left));
        for (var id = 12; id <= 17; id++) Observe(FloorFrame(id, 71, true, 135, false, false));
        var replayed = atlas.View(origin, size, 5);
        Check(atlas.Snapshot.DoorwayFloorConstraints.Select(edge => edge.Direction).SequenceEqual([1, -1])
            && replayed is not null && replayed.EarnedCells == placed!.EarnedCells
            && atlas.ProjectDoors(origin, size) is { Count: > 0 },
            "known-room recovery supplies return doorway geometry without earning exploration credit");

        void Observe(WindowsLiveWorldFrame request) =>
            atlas.Observe(request.Frame, origin, request.MonotonicTimestamp, [], default, tracking,
                admitsPlacement: true, captureEvidence: request.WorldEvidence);
    }

    internal static void TestDoorwayCommitFailureKeepsPlacedRoomAndRetriesAlignment() =>
        VerifyWindowsDoorwayCommit(failAlignmentCommit: true);

    private static void VerifyWindowsDoorwayCommit(bool failAlignmentCommit)
    {
        foreach (var visualRoom in new[] { false, true })
        {
            var directory = Directory.CreateTempSubdirectory("hkv-windows-doorway-");
            try
            {
                var root = directory.FullName;
                const string departureScene = "Windows_departure";
                const string arrivalScene = "Windows_arrival";
                var failCommit = false;
                var builtDoorway = false;
                var pipeline = new WindowsLiveWorldPipeline(root);
                Check(!pipeline.HasDurableEvidence, "an empty world does not claim durable evidence");
                Check(pipeline.Process(ResumeFrame(1, 71) with
                {
                    RoomName = departureScene,
                    RoomIdentityIsVisual = visualRoom
                }).AllowsAtlasWrite, "a pre-island Windows capture writes its original image");
                var legacy = new LiveWorldSessionStore(root).Load().Snapshot;
                Check(!legacy.UsesSwiftWireFormat && legacy.TrackingIslands.Count == 0,
                    "the prior Windows snapshot has no island membership");

                pipeline = new WindowsLiveWorldPipeline(root,
                    stage => builtDoorway |= stage == "doorway-rebuild",
                    stage => failCommit && stage == "commit");
                Check(pipeline.HasDurableEvidence, "a reopened Windows world can resume through the durable pipeline");
                WindowsLiveWorldFrame Depart(long id) => FloorFrame(id, 71, true, 135, false, false) with
                {
                    RoomName = departureScene,
                    RoomIdentityIsVisual = visualRoom
                };
                _ = pipeline.Process(Depart(2));
                Check(pipeline.Process(Depart(3)).ConfirmedRecoveryPose is not null, "recover the legacy image before extending it");

                for (var id = 4; id <= 6; id++)
                {
                    Check(pipeline.Process(Depart(id)).AllowsAtlasWrite,
                        "save a measured departure without replacing the legacy observation");
                }

                pipeline.NotifyTrackingLoss(1);
                var cut = new VisualSceneDiscontinuityTracker.Cut(1.95, 2.6, new(80, 0), VisualRoomDirection.Right);
                WindowsLiveWorldFrame Arrive(long id) => FloorFrame(id, 901, false, 150, false, false) with
                {
                    RoomName = arrivalScene,
                    RoomIdentityIsVisual = visualRoom,
                    DoorwayCut = cut
                };
                var next = visualRoom ? 9 : 11;
                for (var id = next; id <= 11; id++)
                {
                    Check(!pipeline.Process(Arrive(id)).AllowsAtlasWrite,
                        "recovery and provisional room placement do not append images");
                }

                WindowsLiveWorldPipelineResult? last = null;
                for (var id = 12; id <= 14; id++)
                {
                    if (id == 14 && failAlignmentCommit) break;
                    last = pipeline.Process(Arrive(id));
                    Check(last.AllowsAtlasWrite, "commit a measured arrival");
                }

                var store = new LiveWorldSessionStore(root);
                if (failAlignmentCommit)
                {
                    var unaligned = store.Load().Snapshot;
                    failCommit = true;
                    var failed = pipeline.Process(Arrive(14));
                    failCommit = false;
                    Check(builtDoorway && failed is { State: WindowsLiveWorldPipelineState.Failed, RebuiltAtlas: null }
                        && store.Load().Snapshot.MapRevision == unaligned.MapRevision
                        && store.Load().Snapshot.DoorwayFloorConstraints.Count == 0,
                        "failed alignment cannot publish its new tiles or pose graph");

                    var failedSource = new SceneSessionStore(root).Manifest.Observations[^1].Id;
                    const double retryTime = 4.23; // Keep the next measured image in the arrival island.
                    var retry = Arrive(15);
                    last = pipeline.Process(retry with
                    {
                        Timestamp = retryTime,
                        MonotonicTimestamp = retryTime,
                        WorldEvidence = retry.WorldEvidence! with { CaptureTimestamp = retryTime }
                    });
                    Check(last is { AllowsAtlasWrite: true, RebuiltAtlas.TileCount: > 0 },
                        $"the next measured frame retries the pending doorway ({last.Diagnostic})");
                    var aligned = store.Load().Snapshot;
                    var arrivalRoom = aligned.Observations[^1].RoomId;
                    Check(aligned.Observations.Count == unaligned.Observations.Count + 1
                        && aligned.Observations.Where(row => row.RoomId == arrivalRoom)
                            .All(row => row.RawPose.Y == 0 && row.OptimizedPose.Y == 15),
                        "the retry aligns the whole arrival while preserving its measured poses");
                    var rebuilt = WindowsLiveWorldPipeline.RebuildAtlas(aligned,
                        id => id == failedSource ? throw new InvalidOperationException("Failed image leaked into the Atlas.") : store.Source(id),
                        1, 256);
                    Check(rebuilt.TileCount > 0, "Atlas rebuild uses only the committed source images");
                }
                var snapshot = store.Load().Snapshot;
                var original = snapshot.Observations[0];
                var first = legacy.Observations.Single();
                Check(!snapshot.UsesSwiftWireFormat && original.Timestamp == first.Timestamp
                    && original.OptimizedPose == first.OptimizedPose && original.RawPose == first.RawPose
                    && original.ExcludedRects.SequenceEqual(first.ExcludedRects)
                    && snapshot.TrackingIslands[0].Placement == LiveIslandPlacement.LegacyUnknown
                    && !snapshot.TrackingIslands[0].Members.Single().Measured,
                    "existing Windows images and coordinates retain their original evidence");

                var doorway = snapshot.DoorwayFloorConstraints.Single();
                var source = snapshot.Observations.Single(row => row.Id == doorway.DepartureObservationId);
                var target = snapshot.Observations.Single(row => row.Id == doorway.ArrivalObservationId);
                Check(doorway.DepartureSupport.Count == 3 && doorway.ArrivalSupport.Count == 3
                    && source.OptimizedPose.Y + doorway.DepartureFloor.Y
                        == target.OptimizedPose.Y + doorway.ArrivalFloor.Y
                    && last is { RebuiltAtlas.TileCount: > 0 }
                    && last.CameraPose == new AtlasPoint(432, 15),
                    "the reopened Windows graph, rebuilt tiles, and camera reflect one vertical correction");

                var restored = new WindowsLiveWorldPipeline(root);
                WindowsLiveWorldFrame Resume(long id) => FloorFrame(id, 901, false, 150, false, false) with
                {
                    // Normal mode's names can change when the capture restarts.
                    RoomName = visualRoom ? departureScene : arrivalScene,
                    RoomIdentityIsVisual = visualRoom,
                    CaptureGeneration = 2
                };
                _ = restored.Process(Resume(15));
                Check(restored.Process(Resume(16)).ConfirmedRecoveryPose == new AtlasPoint(432, 15),
                    "a restored visit finds the saved arrival by image even when a visual room name is reused");
                Check(restored.Process(Resume(17)).CameraPose == new AtlasPoint(432, 15)
                    && store.Load().Snapshot.DoorwayFloorConstraints.Count == 1,
                    "resuming the saved room preserves its connection");
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }

    internal static void TestLocalRecoveryRetainsEvidenceUntilAPlacedMatch()
    {
        var root = Directory.CreateTempSubdirectory("hkv-local-recovery-");
        try
        {
            var pipeline = new WindowsLiveWorldPipeline(root.FullName);
            var store = new LiveWorldSessionStore(root.FullName);
            const int placedTexture = 71;
            const int newTexture = 901;
            WindowsLiveWorldFrame Measured(long id, double time, int texture, double x)
            {
                var frame = FloorFrame(id, texture, true, 135, false, false);
                return frame with
                {
                    Timestamp = time,
                    MonotonicTimestamp = time,
                    LocalPose = new AtlasPoint(x, 0),
                    WorldEvidence = frame.WorldEvidence! with { CaptureTimestamp = time, LocalReferenceTimestamp = time - .05 }
                };
            }

            Check(pipeline.Process(Measured(1, .5, placedTexture, 80)).AllowsAtlasWrite,
                "the original image is placed");
            pipeline.NotifyTrackingLoss(1);
            Check(!pipeline.Process(Measured(2, .625, newTexture, 120) with { WorldEvidence = null }).AllowsAtlasWrite,
                "a frame with no measured reference stays pending");
            Check(!pipeline.Process(Measured(3, .75, newTexture, 122)).AllowsAtlasWrite
                && !pipeline.Process(Measured(4, .84, newTexture, 124)).AllowsAtlasWrite,
                "local recovery needs independent measured samples");
            var resumed = pipeline.Process(Measured(5, .94, newTexture, 126));
            Check(resumed is { AllowsAtlasWrite: true, PlacementPending: true, RebuiltAtlas: null }
                && resumed.WalkedGround.Count == 0, $"the reacquired evidence saves provisionally ({resumed.Diagnostic})");
            Check(pipeline.Process(Measured(6, 1.09, newTexture, 128)) is
                    { AllowsAtlasWrite: true, PlacementPending: true }, "the next local measurement is recorded");

            var pending = store.Load().Snapshot;
            Check(pending.UnplacedObservationIds().SetEquals([1, 2])
                && pending.TrackingIslands[^1].Members.Count == 2,
                "the adjacent measurements survive reopen with a common unplaced owner");
            var composed = new List<int>();
            _ = WindowsLiveWorldPipeline.RebuildAtlas(pending,
                id => { composed.Add(id); return store.Source(id); }, 1, 256);
            Check(composed.SequenceEqual([0]), "unplaced screenshots cannot overwrite the saved mosaic");
            var reopening = new WindowsLiveWorldPipeline(root.FullName);
            var replayed = reopening.Process(Measured(7, 1.4, newTexture, 128) with { CaptureGeneration = 2 });
            var repeated = reopening.Process(Measured(8, 1.7, newTexture, 128) with { CaptureGeneration = 2 });
            Check(replayed.ConfirmedRecoveryPose is null && repeated.ConfirmedRecoveryPose is null
                && !repeated.AllowsAtlasWrite, "a saved unplaced image cannot localize a new capture");

            _ = pipeline.Process(Measured(9, 1.24, placedTexture, 130));
            var located = pipeline.Process(Measured(10, 1.54, placedTexture, 130));
            Check(located is { AllowsAtlasWrite: true, PlacementPending: false, RebuiltAtlas.TileCount: > 0 }
                && located.AcceptedClosure is not null, $"two matches to placed imagery reconnect the new island ({located.Diagnostic})");
            var confirmed = store.Load().Snapshot;
            Check(confirmed.TrackingIslands[^1].Placement == LiveIslandPlacement.Connected
                && confirmed.UnplacedObservationIds().Count == 0
                && confirmed.Observations[0].OptimizedPose == new AtlasPoint(80, 0)
                && confirmed.Observations[1].OptimizedPose == new AtlasPoint(76, 0)
                && confirmed.Observations[2].OptimizedPose == new AtlasPoint(78, 0),
                "reconnection moves only the measured island and retains its relative geometry");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestImportedWorldPlacesMeasuredRecoveryThroughRegeneratedFeatures()
    {
        WithRecoveryFixture(identicalRooms: false, (root, before) =>
        {
            var pipeline = new WindowsLiveWorldPipeline(root);
            _ = pipeline.Process(ResumeFrame(1, 71));
            Check(pipeline.Process(ResumeFrame(2, 71)).ConfirmedRecoveryPose == new AtlasPoint(100, 20),
                "the Swift world is initially located by regenerated features");
            pipeline.NotifyTrackingLoss(1);

            WindowsLiveWorldFrame Measured(long id, double time, int texture, double x)
            {
                var frame = FloorFrame(id, texture, true, 135, false, false);
                return frame with
                {
                    Timestamp = time,
                    MonotonicTimestamp = time,
                    LocalPose = new AtlasPoint(x, 0),
                    WorldEvidence = frame.WorldEvidence! with { CaptureTimestamp = time, LocalReferenceTimestamp = time - .05 }
                };
            }

            _ = pipeline.Process(Measured(3, .75, 901, 124));
            _ = pipeline.Process(Measured(4, .84, 901, 125));
            Check(pipeline.Process(Measured(5, 1.05, 901, 126)).PlacementPending,
                "relative measurements can be saved when a Mac reference is out of view");
            _ = pipeline.Process(Measured(6, 1.2, 901, 128));
            _ = pipeline.Process(Measured(7, 1.35, 71, 130));
            var placed = pipeline.Process(Measured(8, 1.65, 71, 130));
            var saved = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(placed is { AllowsAtlasWrite: true, PlacementPending: false, RebuiltAtlas.TileCount: > 0 }
                && placed.CameraPose == new AtlasPoint(100, 20)
                && saved.UnplacedObservationIds().Count == 0, $"a measured group is placed by restored imagery ({placed.Diagnostic})");
            Check(saved.Observations.Take(before.Observations.Count).Select(row => row.OptimizedPose)
                .SequenceEqual(before.Observations.Select(row => row.OptimizedPose))
                && saved.LoopClosureEdges.Count == before.LoopClosureEdges.Count,
                "the original positions and landmark relationships remain intact");
        });
    }

    internal static void TestSameRoomRecoveryVerifiesBroadMatchesWithFloorTexture()
    {
        var root = Directory.CreateTempSubdirectory("hkv-ground-verified-recovery-");
        try
        {
            var pipeline = new WindowsLiveWorldPipeline(root.FullName);
            var store = new LiveWorldSessionStore(root.FullName);
            WindowsLiveWorldFrame Measured(long id, double time, int texture, double x, int row = 135)
            {
                var request = FloorFrame(id, texture, true, row, false, false);
                return request with
                {
                    Timestamp = time,
                    MonotonicTimestamp = time,
                    LocalPose = new(x, 0),
                    // The narrow alignment mask leaves no scene landmarks.
                    GroundAlignmentExclusions = [new(0, 0, request.Frame.Width, request.Frame.Height)],
                    WorldEvidence = request.WorldEvidence! with { CaptureTimestamp = time, LocalReferenceTimestamp = time - .05 }
                };
            }

            var first = pipeline.Process(Measured(1, .5, 71, 80));
            Check(first.AllowsAtlasComposition && store.Load().Snapshot.Landmarks.Count == 0,
                "the saved image has ground geometry and pixels even without narrow-band landmarks");
            pipeline.NotifyTrackingLoss(1);
            _ = pipeline.Process(Measured(2, .7, 901, 124));
            _ = pipeline.Process(Measured(3, .8, 901, 125));
            Check(pipeline.Process(Measured(4, .9, 901, 126)).PlacementPending,
                "unseen imagery resumes in a pending island");
            Check(pipeline.Process(Measured(5, 1.2, 71, 130, row: 105)).PlacementPending,
                "identical scenery at the wrong floor elevation cannot place the island");
            Check(pipeline.Process(Measured(6, 1.5, 71, 130)).PlacementPending,
                "one ground-verified frame cannot place the island");
            var placed = pipeline.Process(Measured(7, 1.65, 71, 130));
            Check(placed is { AllowsAtlasComposition: true, RebuiltAtlas.TileCount: > 0 }
                && placed.Diagnostic == "live-world ground-verified recovery placed",
                $"two fresh ground matches should place the entire island ({placed.Diagnostic})");
            var after = store.Load().Snapshot;
            Check(after.UnplacedObservationIds().Count == 0
                && after.Observations[0].OptimizedPose == new AtlasPoint(80, 0)
                && after.Observations[1].OptimizedPose == new AtlasPoint(76, 0)
                && after.Observations[^1].OptimizedPose == new AtlasPoint(80, 0),
                "floor recovery rebuilds the new island without moving the prior map");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestDoorwaySpacingUsesOnlyPlacedImages()
    {
        var root = Directory.CreateTempSubdirectory("hkv-local-recovery-doorway-");
        try
        {
            var pipeline = new WindowsLiveWorldPipeline(root.FullName);
            Check(pipeline.Process(ResumeFrame(1, 71)).AllowsAtlasWrite, "the departure room has a saved footprint");
            pipeline.NotifyTrackingLoss(1);
            WindowsLiveWorldFrame Local(long id, double time) => ResumeFrame(id, 901, localX: 1600) with
            {
                Timestamp = time,
                MonotonicTimestamp = time,
                WorldEvidence = new LiveWorldCaptureEvidence(time) { LocalReferenceTimestamp = time - .05 }
            };
            _ = pipeline.Process(Local(2, .45));
            _ = pipeline.Process(Local(3, .57));
            Check(pipeline.Process(Local(4, .72)).PlacementPending, "distant geometry is saved with no world placement");

            var cut = new VisualSceneDiscontinuityTracker.Cut(.75, 1.35, new(1600, 0), VisualRoomDirection.Right);
            WindowsLiveWorldFrame Arrive(long id) => ResumeFrame(id, 191) with { RoomName = "Next_room", DoorwayCut = cut };
            Check(!pipeline.Process(Arrive(5)).AllowsAtlasWrite, "the cut provisionally places the new room");
            var arrival = pipeline.Process(Arrive(6));
            Check(arrival.AllowsAtlasComposition && arrival.CameraPose == new AtlasPoint(432, 0),
                "the next room sits alongside the visible map instead of the pending section");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestMeasuredAtlasLinksSurviveUntilTheirGroupIsPlaced()
    {
        var root = Directory.CreateTempSubdirectory("hkv-relative-atlas-link-");
        try
        {
            var pipeline = new WindowsLiveWorldPipeline(root.FullName);
            var store = new LiveWorldSessionStore(root.FullName);
            LinkUnplacedSections(pipeline);

            var saved = store.LoadTracker();
            var before = saved.Snapshot;
            var source = before.Observations[1];
            var arrival = before.Observations[2];
            var components = new LiveIslandComponents(before);
            Check(before.LoopClosureEdges.Count == 1 && before.UnplacedObservationIds().SetEquals([1, 2, 3])
                && components.SameRigidComponent(components.Owner(source.Id)!.Id, components.Owner(arrival.Id)!.Id)
                && before.Landmarks.Count >= 6 && before.Landmarks.All(landmark => landmark.GroundClosureOnly)
                && !saved.HasRecoveryReference(source.RoomId),
                "saved edges retain verified support without widening the unverified matcher");
            Check(source.OptimizedPose == arrival.OptimizedPose && before.Observations[0].OptimizedPose == new AtlasPoint(80, 0),
                "the relative correction preserves the existing anchor");

            var one = pipeline.Process(MeasuredRecoveryFrame(9, 1.72, 71, new(410, 0)));
            Check(one.PlacementPending, "the anchored match needs a separate confirmation");
            var placed = pipeline.Process(MeasuredRecoveryFrame(10, 1.87, 71, new(410, 0)));
            Check(placed is { AllowsAtlasComposition: true, AcceptedClosure: not null, RebuiltAtlas.TileCount: > 0 },
                $"the anchored match publishes the whole linked component ({placed.Diagnostic})");
            var after = store.Load().Snapshot;
            Check(after.LoopClosureEdges.Count == 2 && after.UnplacedObservationIds().Count == 0
                && after.Observations[0].OptimizedPose == new AtlasPoint(80, 0)
                && after.Observations[1].OptimizedPose == new AtlasPoint(70, 0)
                && after.Observations[2].OptimizedPose == new AtlasPoint(70, 0),
                "the two unplaced sections move together to the placed map");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestDoorwayDoesNotInheritUnplacedVerticalCorrection()
    {
        var root = Directory.CreateTempSubdirectory("hkv-relative-doorway-height-");
        try
        {
            var pipeline = new WindowsLiveWorldPipeline(root.FullName);
            var relative = LinkUnplacedSections(pipeline, targetY: 60);
            Check(relative.WorldBasisOffset.Y == -60, "the live relative coordinate system acquired a vertical correction");

            var cut = new VisualSceneDiscontinuityTracker.Cut(1.62, 2.1, new(400, 60), VisualRoomDirection.Right);
            WindowsLiveWorldFrame Arrival(long id, double time) => MeasuredRecoveryFrame(id, time, 191, new(80, 12))
                with { RoomName = "Linked_section_next_room", DoorwayCut = cut };
            Check(!pipeline.Process(Arrival(9, 2.24)).AllowsAtlasComposition, "the new doorway establishes a basis first");
            var published = pipeline.Process(Arrival(10, 2.39));
            Check(published.AllowsAtlasComposition && published.CameraPose.Y == 12,
                $"the next room must use the last placed height ({published.CameraPose.Y})");
            var world = new LiveWorldSessionStore(root.FullName).Load().Snapshot;
            Check(world.UnplacedObservationIds().SetEquals([1, 2, 3]) && world.LoopClosureEdges.Count == 1,
                "the relative link survives the transition without placing its component");
        }
        finally { root.Delete(recursive: true); }
    }

    private static WindowsLiveWorldPipelineResult LinkUnplacedSections(WindowsLiveWorldPipeline pipeline, double targetY = 0)
    {
        Check(pipeline.Process(MeasuredRecoveryFrame(1, .5, 71, new(80, 0))).AllowsAtlasComposition, "initial placed map");
        pipeline.NotifyTrackingLoss(1);
        _ = pipeline.Process(MeasuredRecoveryFrame(2, .7, 901, new(220, 0)));
        _ = pipeline.Process(MeasuredRecoveryFrame(3, .81, 901, new(220, 0)));
        Check(pipeline.Process(MeasuredRecoveryFrame(4, .95, 901, new(220, 0))).PlacementPending, "first unplaced section");
        pipeline.NotifyTrackingLoss(1);
        var local = new AtlasPoint(400, targetY);
        _ = pipeline.Process(MeasuredRecoveryFrame(5, 1.15, 901, local));
        _ = pipeline.Process(MeasuredRecoveryFrame(6, 1.28, 901, local));
        var firstVote = pipeline.Process(MeasuredRecoveryFrame(7, 1.42, 901, local));
        Check(firstVote.PlacementPending && firstVote.AcceptedClosure is null, "one image in the second section must remain tentative");
        var relative = pipeline.Process(MeasuredRecoveryFrame(8, 1.57, 901, local));
        Check(relative is { PlacementPending: true, AcceptedClosure: not null, RebuiltAtlas: null }
            && !relative.AllowsAtlasComposition && relative.CameraPose == new AtlasPoint(220, 0),
            $"two images link the unplaced sections without showing them ({relative.Diagnostic})");
        return relative;
    }

    private static WindowsLiveWorldFrame MeasuredRecoveryFrame(long id, double time, int texture, AtlasPoint pose)
    {
        var request = FloorFrame(id, texture, true, 135, false, false);
        return request with
        {
            Timestamp = time,
            MonotonicTimestamp = time,
            LocalPose = pose,
            GroundAlignmentExclusions = [new(0, 0, request.Frame.Width, request.Frame.Height)],
            WorldEvidence = request.WorldEvidence! with { CaptureTimestamp = time, LocalReferenceTimestamp = time - .05 }
        };
    }

    internal static void TestAtlasLinksPreserveDoorwayAxesAndFixedPlacements()
    {
        WithFixture((root, store, _) =>
        {
            var original = store.Load().Snapshot;
            var islands = original.TrackingIslands.Select(island => island.Id == TargetIsland
                ? island with { BreakReason = LiveTrackingIsland.RecoveryBreakReason }
                : island).ToArray();
            var pending = new LiveWorldSnapshot(original.SchemaVersion, original.MapRevision, original.AnchoredObservationId,
                original.Observations, original.Keyframes, original.Landmarks, original.RelativeMotionEdges,
                original.LoopClosureEdges, islands, original.DoorwayFloorConstraints);
            var components = new LiveIslandComponents(pending);
            Check(!components.SameRigidComponent(SourceIsland, TargetIsland)
                && components.Plan(SourceIsland, TargetIsland, new(20, 5), tolerance: 4) is null,
                "a doorway links elevation only, and an image cannot contradict it");
            var forward = components.Plan(SourceIsland, TargetIsland, new(20, 0), tolerance: 4);
            var reverse = components.Plan(TargetIsland, SourceIsland, new(-20, 0), tolerance: 4);
            Check(forward is not null && reverse is not null
                && forward.Shifts.Count == 1 && forward.Shifts[TargetIsland] == new AtlasPoint(20, 0)
                && forward.Shifts.SequenceEqual(reverse.Shifts)
                && forward.PlacedIslands.SetEquals([SourceIsland, TargetIsland]),
                "an image can establish horizontal placement in either direction without changing the measured floor");
            var anchored = new LiveIslandComponents(original);
            Check(anchored.Plan(SourceIsland, TargetIsland, new(3, 3), tolerance: 4) is null
                && anchored.Plan(SourceIsland, TargetIsland, new(2, 2), tolerance: 4)?.Shifts.Count == 0,
                "fixed placements require consistent two-axis evidence");

            var thirdRoom = original.Observations[^1] with { Id = 6, SourceObservationId = 6, RoomId = 10,
                Timestamp = original.Observations[^1].Timestamp + 1, LocalPose = default };
            var estimated = original.TrackingIslands.Select(island => island with { Placement = LiveIslandPlacement.Estimated });
            var third = new LiveTrackingIsland(13, thirdRoom.RoomId, thirdRoom.Timestamp, thirdRoom.OptimizedPose,
                LiveIslandPlacement.Reference, default, "reference", [new(thirdRoom.Id, default, true)]);
            var extended = new LiveWorldSnapshot(original.SchemaVersion, original.MapRevision, original.AnchoredObservationId,
                [.. original.Observations, thirdRoom], original.Keyframes, original.Landmarks, original.RelativeMotionEdges,
                original.LoopClosureEdges, [.. estimated, third], original.DoorwayFloorConstraints);
            var doorway = new LiveIslandComponents(extended, new HashSet<int>());
            var heights = doorway.PlanDoorwayElevation(third.Id, TargetIsland, 18, tolerance: 4);
            Check(heights?.Count == 2 && heights[SourceIsland] == 18 && heights[TargetIsland] == 18
                && doorway.PlanDoorwayElevation(SourceIsland, TargetIsland, 5, tolerance: 4) is null
                && doorway.PlanDoorwayElevation(third.Id, TargetIsland, double.NaN, tolerance: 4) is null,
                "a doorway moves a connected estimated floor pair together and rejects conflicting elevation");
            var recovered = new LiveIslandComponents(extended, new HashSet<int> { original.Observations[0].Id });
            Check(recovered.PlanDoorwayElevation(third.Id, TargetIsland, 18, tolerance: 4) is null
                && recovered.PlanDoorwayElevation(third.Id, TargetIsland, 2, tolerance: 4)?.Count == 0,
                "image-recovered placement is preserved across adjoining doorway constraints");
        });
    }

    internal static void TestConfirmedLoopPreservesMeasuredDoorwayElevation()
    {
        foreach (var fixedNeighbor in new[] { false, true })
        {
            var world = new LiveWorldTracker();
            Observe(0, 1, 0, 1, default, 71, right: false);
            for (var id = 1; id <= 3; id++)
                Observe(id, 1.85 + id * .15, 0, 2, new(260, 12), 901, right: true);
            for (var id = 4; id <= 6; id++)
                Observe(id, 3 + (id - 4) * .15, 1, 3, new(660, 0), 191, right: false);
            Check(world.AlignRoomThroughDoorway(new(0, 1, 1, 2.3, 3, VisualRoomDirection.Right)),
                "the tracked room and its neighbor must have a measured common elevation");
            Observe(7, 3.45, 2, 4, new(1050, 20), 811, right: false);
            Observe(8, 3.55, 0, 5, new(365, 12), 419, right: true);
            var initial = world.Snapshot;
            var landmarks = initial.Landmarks.Where(mark => mark.KeyframeId == 1).Take(3).Select(mark => mark.Id).ToArray();
            var measuredLink = new LiveWorldLoopClosureEdge(0, 1, 8, landmarks, 105, 0, 3, 0) { ResidualPixels = 2 };
            var islands = initial.TrackingIslands.Select(island => fixedNeighbor && island.RoomId == 1
                ? island with { Placement = LiveIslandPlacement.Connected, Uncertainty = new(2, 3) } : island).ToArray();
            world = new(new LiveWorldSnapshot(initial.SchemaVersion, initial.MapRevision, initial.AnchoredObservationId,
                initial.Observations, initial.Keyframes, initial.Landmarks, initial.RelativeMotionEdges,
                [measuredLink], islands, initial.DoorwayFloorConstraints));
            world.ResumeCommittedVisit(1);
            Observe(9, 3.7, 0, 2, new(270, 12), 901, right: true);
            var first = Observe(10, 3.85, 0, 2, new(8, 12), 71, right: true, search: true);
            Check(first.AcceptedClosure is null && first.Review.Rejections.Contains(LiveWorldTrackerRejection.ConfirmationRequired),
                "a doorway does not block an eligible image reference or grant a one-frame correction");
            var result = Observe(11, 4, 0, 2, new(8, 12), 71, right: true, search: true);
            Check(result.Snapshot.Observations.Take(9).Select(row => row.RawPose)
                .SequenceEqual(initial.Observations.Select(row => row.RawPose)),
                "an Atlas correction retains raw capture motion");
            if (fixedNeighbor)
            {
                Check(result.AcceptedClosure is null
                    && result.Review.Rejections.Contains(LiveWorldTrackerRejection.InconsistentConfirmation)
                    && result.Snapshot.LoopClosureEdges.Count == 1
                    && result.Snapshot.Observations.Take(9).Select(row => row.OptimizedPose)
                        .SequenceEqual(initial.Observations.Select(row => row.OptimizedPose)),
                    "a conflicting image vote cannot tear the floor or move either fixed component");
                continue;
            }

            var reopened = LiveWorldJson.ReadSnapshot(LiveWorldJson.WriteSnapshot(result.Snapshot));
            Check(result.AcceptedClosure is { ResidualPixels: 0 } && result.Correction == new AtlasPoint(-8, -12)
                && reopened.Observations[0].OptimizedPose == default
                && reopened.Observations.Skip(1).Take(3).All(row => row.OptimizedPose == new AtlasPoint(252, 0))
                && reopened.Observations.Skip(4).Take(3).All(row => row.OptimizedPose == new AtlasPoint(660, -15))
                && reopened.Observations[7].OptimizedPose == initial.Observations[7].OptimizedPose
                && reopened.Observations[8].OptimizedPose == new AtlasPoint(357, 0),
                "the image loop corrects both axes of its owner and only the elevation of its floor partner");
            Check(reopened.TrackingIslands.Single(island => island.MeasuredLineage == 2)
                is { Placement: LiveIslandPlacement.Connected, Uncertainty: { X: 1, Y: 1 } }
                && reopened.TrackingIslands.Single(island => island.MeasuredLineage == 5)
                is { Placement: LiveIslandPlacement.Connected, Uncertainty: { X: 3, Y: 3 } }
                && reopened.TrackingIslands.Single(island => island.RoomId == 1)
                is { Placement: LiveIslandPlacement.Estimated, Uncertainty: { X: null, Y: null } }
                && LiveWorldGroundOverlay.ProjectPlacement(reopened).Doorways.Single() is { Start.Y: -50, End.Y: -50 },
                "only two-axis image evidence locates the island and the saved doorway keeps equal floor heights");

            LiveWorldTrackerUpdate Observe(int id, double time, int room, long lineage, AtlasPoint pose,
                int seed, bool right, bool search = false)
            {
                var frame = RecoveryTexture(seed, id);
                var floor = room == 0 ? 40 : 55;
                var left = right ? 220 : 0;
                var player = new WorldRect(new(right ? 270 : 20, floor), new(24, 35));
                return world.Ingest(id, time, frame, pose, frame.Width, searchForClosure: search,
                    localPose: pose, captureGeneration: 1, localEpoch: 1, roomId: room,
                    captureEvidence: new LiveWorldCaptureEvidence(time, new([new(id, new(left, floor), new(left + 100, floor))]),
                        PlayerBounds: player) { MeasuredLineage = lineage });
            }
        }
    }

    private static WindowsLiveWorldFrame FloorFrame(long id, int seed, bool rightSide, int row, bool airborne, bool competing)
    {
        var request = ResumeFrame(id, seed);
        var image = request.Frame;
        var observedAt = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddSeconds(SwiftDateSeconds + 100 + request.MonotonicTimestamp);
        var frame = new BgraFrame(image.Width, image.Height, image.Stride, image.Pixels, id, observedAt);
        var span = rightSide ? new PixelSpan(220, 319) : new PixelSpan(0, 100);
        var surface = new DetectedFloorLine(row, span);
        var other = new DetectedFloorLine(row - 20, span);
        var footY = row - (airborne ? 15 : 0);
        var knightX = rightSide ? 268 : 20;
        var confirmed = new GroundLinePresenceReview(30, surface.ToSemanticLine(), GroundLinePresenceState.Confirmed, 1, 1);
        return request with
        {
            Frame = frame,
            MaskedFrame = frame,
            WorldEvidence = LiveWorldCaptureEvidence.FromGround(request.MonotonicTimestamp, image.Width, image.Height,
                competing ? [surface, other] : [surface],
                competing ? [confirmed, confirmed with { Id = 31, Line = other.ToSemanticLine() }] : [confirmed],
                new PixelRect(knightX, footY - 35, 24, 35), new AtlasPoint(knightX + 12, footY))
        };
    }

    private static void WithRecoveryFixture(bool identicalRooms, Action<string, LiveWorldSnapshot> body)
    {
        var directory = Directory.CreateTempSubdirectory("hkv-import-recovery-");
        try
        {
            var root = directory.FullName;
            WriteEmptySwiftManifest(root);
            var store = new LiveWorldSessionStore(root);
            var pose = new AtlasPoint(100, 20);
            var observations = new List<LiveWorldObservation>();
            var keyframes = new List<LiveWorldKeyframe>();
            foreach (var (room, seed) in new[] { (SourceRoom, 71), (TargetRoom, identicalRooms ? 71 : 191) })
            {
                var frame = RecoveryTexture(seed, room);
                var timestamp = SwiftDateSeconds + room;
                var saved = store.Append(frame, timestamp, pose, 320);
                observations.Add(LiveWorldObservation.Create(saved.Id, saved.Id, timestamp,
                    frame.Width, frame.Height, 320, 160, [], pose, pose, roomId: room));
                keyframes.Add(new LiveWorldKeyframe(saved.Id, saved.Id, saved.Id));
            }
            var graph = new LiveWorldSnapshot(schemaVersion: 3, mapRevision: 1, anchoredObservationId: 0,
                observations: observations, keyframes: keyframes);
            Check(store.Commit(graph, 0, 1), "publish Swift recovery fixture");
            body(root, graph);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static WindowsLiveWorldFrame ResumeFrame(long id, int seed, double localX = 80)
    {
        var frame = RecoveryTexture(seed, id);
        return new WindowsLiveWorldFrame(frame, frame, id * .3, id * .3, new(localX, 0), 320,
            [], [], 1, "Tutorial_After_Load", 1, 256);
    }

    private static BgraFrame RecoveryTexture(int seed, long id)
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        var state = unchecked((uint)seed);
        for (var i = 0; i < pixels.Length; i += BgraFrame.BytesPerPixel)
        {
            state = unchecked(state * 1_664_525u + 1_013_904_223u);
            var value = (byte)(state >> 24);
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = byte.MaxValue;
        }
        return new BgraFrame(width, height, width * BgraFrame.BytesPerPixel, pixels, id, DateTimeOffset.UnixEpoch);
    }

    private static void Reject(JsonNode payload, LiveWorldModelError expected)
    {
        try
        {
            _ = LiveWorldJson.ReadSnapshot(JsonSerializer.SerializeToUtf8Bytes(payload));
        }
        catch (LiveWorldModelException error) when (error.Error == expected)
        {
            return;
        }
        throw new InvalidOperationException($"Expected native atlas error {expected}.");
    }

    private static void WithFixture(Action<string, LiveWorldSessionStore, byte[]> body)
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-native-atlas-");
        try
        {
            // The fixture uses Swift's wire names and arrays independently of the Windows snapshot writer.
            var root = Path.Combine(temporary.FullName, "world");
            Directory.CreateDirectory(root);
            WriteEmptySwiftManifest(root);
            var store = new LiveWorldSessionStore(root);
            Check(store.Load().Snapshot.UsesSwiftWireFormat, "empty Swift manifest");
            var rows = Enumerable.Range(0, 6).Select(id =>
            {
                var source = id < 3;
                var startedAt = SwiftDateSeconds + (source ? 1 : 10);
                var timestamp = startedAt + (id % 3) * 0.2;
                var room = source ? SourceRoom : TargetRoom;
                var offset = (id % 3) * 10.0;
                var origin = source ? new[] { 0.0, 70 } : new[] { 150.0, 40 };
                var floorY = source ? 20.0 : 50.0;
                double[] raw = [offset, 3];
                var stored = store.Append(Frame(), timestamp, new(raw[0], raw[1]), 640, [ForegroundMask]);
                var line = new { segmentID = (long)id, start = new[] { 0.0, floorY }, end = new[] { 640.0, floorY } };
                return new
                {
                    id, sourceObservationID = stored.Id, timestamp, captureTimestamp = timestamp - SwiftDateSeconds - 0.01,
                    sourceWidth = 32, sourceHeight = 20, solveWidth = 640, sampleWidth = 160,
                    excludedRects = new[] { new[] { new[] { 2.5, 12.875 }, new[] { 7.0, 4 } } },
                    localPose = new[] { offset, 0.0 }, roomID = room, rawPose = raw,
                    optimizedPose = new[] { offset + origin[0], origin[1] },
                    groundGeometry = new
                    {
                        lines = new[] { line },
                        walkedIslands = id != 0 ? null : new[]
                        {
                            new
                            {
                                owner = new
                                {
                                    id = SourceIsland, roomID = room, startedAt, worldFromLocal = origin,
                                    localPose = new[] { 0.0, 0 }, placement = "reference",
                                    uncertainty = new { x = 0, y = 0 }, breakReason = "origin", measured = true
                                },
                                spans = new[]
                                {
                                    new { segmentID = long.MaxValue, start = new[] { -30.0, 20 }, end = new[] { 50.0, 20 } }
                                }
                            }
                        }
                    },
                    observedFloorCandidates = new { lines = new[] { line } },
                    observedPlayerBounds = new[] { new[] { 2, 12 }, new[] { 3, 6 } }
                };
            }).ToArray();
            var islands = rows.GroupBy(row => row.roomID).Select(group =>
            {
                var source = group.Key == SourceRoom;
                var first = group.First();
                return new
                {
                    id = source ? SourceIsland : TargetIsland, roomID = group.Key, startedAt = first.timestamp,
                    worldFromLocal = first.optimizedPose, placement = source ? "reference" : "estimated",
                    uncertainty = source ? new { x = (int?)0, y = (int?)0 } : new { x = (int?)null, y = (int?)2 },
                    breakReason = source ? "origin" : "room-cut",
                    members = group.Select(row => new { observationID = row.id, row.localPose, measured = true })
                };
            }).ToArray();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 3, mapRevision = 1, anchoredObservationID = 0, observations = rows,
                keyframes = Array.Empty<int>(), landmarks = Array.Empty<int>(),
                relativeMotionEdges = Array.Empty<int>(), loopClosureEdges = Array.Empty<int>(),
                trackingIslands = islands,
                doorwayFloorConstraints = new[]
                {
                    new
                    {
                        id = 0, departureObservationID = 2, arrivalObservationID = 5,
                        departureFloor = new[] { 550, 20 }, arrivalFloor = new[] { 30, 50 }, direction = 1,
                        departureSupport = new[] { 0, 1, 2 }, arrivalSupport = new[] { 3, 4, 5 }
                    }
                }
            });
            Check(new SceneSessionStore(root).SaveScene(payload, 0, 1), "write fixture envelope");
            body(root, store, payload);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static void WriteEmptySwiftManifest(string root) =>
        File.WriteAllText(Path.Combine(root, "manifest.json"),
            """
            {"schemaVersion":1,"sessionID":"C14FB382-EF87-4797-8624-FBEE1DD4B0DA",
             "observations":[],"nextObservationID":0}
            """);

    private static BgraFrame Frame()
    {
        var pixels = new byte[32 * 20 * 4];
        Array.Fill(pixels, byte.MaxValue);
        return new(32, 20, 128, pixels, 0, DateTimeOffset.UnixEpoch);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Native Atlas fixture failed: {message}.");
    }

    private static void CheckDiscarded(WindowsLiveWorldPipelineResult result, string reason, string message) =>
        Check(result is { Discarded: true, AllowsAtlasWrite: false, ConfirmedRecoveryPose: null }
            && result.Diagnostic.Contains(reason, StringComparison.Ordinal), message);
}
