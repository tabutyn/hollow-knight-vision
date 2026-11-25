using HollowKnightVision.Windows.Core;

internal static class LiveWorldGroundOverlayTests
{
    internal static void TestCaptureKeepsOnlyObservedConfirmedSpansAtSolveScale()
    {
        const int width = 1280, height = 720, solveWidth = 640;
        var observed = new DetectedFloorLine(600, new(32, 400), [new(32, 159), new(288, 400)]);
        var confirmed = new GroundLinePresenceReview(7, observed.ToSemanticLine(), GroundLinePresenceState.Confirmed, 2, 1);
        var evidence = LiveWorldCaptureEvidence.FromGround(1, width, height, solveWidth,
            [observed], [confirmed], new AtlasPoint(108, 600), observedPlayerBounds: new(96, 552, 24, 42));
        var expected = new LiveGroundLineEvidence[]
        {
            new(7, new(16, 60), new(80, 60)),
            new(7, new(144, 60), new(200.5, 60))
        };
        Check(evidence.GroundGeometry?.Lines.SequenceEqual(expected) == true
            && expected.All(line => line.IsInImage(solveWidth, width, height)),
            "Persist measured spans in solve pixels, preserving the gap and exclusive right edge.");
        Check(evidence.FloorCandidates?.Lines.SequenceEqual(expected.Select(line => line with { SegmentId = 0 })) == true
            && evidence.PlayerBounds == new WorldRect(new(48, 60), new(12, 24)),
            "Same-image recovery candidates and measured feet must use the same coordinate system.");

        var occluded = LiveWorldCaptureEvidence.FromGround(2, width, height, solveWidth,
            [], [confirmed], supportedFloor: new CleanFloorLine(600, new(1240, 1300)));
        Check(occluded.FloorCandidates is null && occluded.GroundGeometry?.Lines.SequenceEqual(
            [new(long.MaxValue, new(620, 60), new(640, 60))]) == true,
            "An old detection cannot fill an occlusion, and independent foot contact is clipped to the image.");
    }

    internal static void TestSweptKnightMaskCannotBecomeContactEvidenceAndMeasuredBoundsScaleToSolvePixels()
    {
        const int width = 320, height = 180, solveWidth = 640;
        const long generation = 4;
        const string knight = LiveObjectDetectionPostprocessor.Knight;
        var floor = new DetectedFloorLine(120, new(20, 220));
        var review = new GroundLinePresenceReview(7, floor.ToSemanticLine(), GroundLinePresenceState.Confirmed, 2, 1);
        var foot = new AtlasPoint(50, 120);
        var swept = new ObjectDetection(knight, new(.0625, .375, .25, .4375), .9f);
        var tracker = new LiveWorldTracker();

        for (var index = 1; index <= 2; index++)
        {
            var frame = new BgraFrame(width, height, width * 4, new byte[width * height * 4],
                index, DateTimeOffset.UnixEpoch.AddSeconds(index));
            var sourceFrame = index == 1 ? 0 : index;
            var body = new ObjectDetection(knight, new(.125, .5, .0625, .125), .9f)
                { SourceFrameIdentifier = sourceFrame };
            var detections = new LiveObjectDetectionSnapshot([body], [body], [swept], null)
                { MeasuredBatch = new([body], sourceFrame, sourceFrame, generation, 0) };
            var masks = GameplaySceneMasks.Create(frame, detections, hudStencil: null, generation);
            Check(masks.GroundKnight is { } mask && mask.Width > 20
                && (masks.ObservedPlayerBounds is not null) == (index == 2),
                "The fallback movement mask and current detector bounds should be separate.");
            var evidence = LiveWorldCaptureEvidence.FromGround(index, width, height, solveWidth, [floor], [review],
                foot, observedPlayerBounds: masks.ObservedPlayerBounds);
            tracker.Ingest(index, index, frame, default, solveWidth, captureGeneration: (ulong)generation,
                searchForClosure: false, captureEvidence: evidence);
        }

        var saved = LiveWorldJson.ReadSnapshot(LiveWorldJson.WriteSnapshot(tracker.Snapshot));
        Check(saved.Observations.Count == 2 && saved.Observations.All(frame => frame.GroundGeometry?.Lines.Count == 1),
            "Confirmed ground should persist in both captures.");
        Check(saved.Observations[0].ObservedPlayerBounds is null,
            "A foot inferred against a movement mask cannot prove the player's position.");
        Check(saved.Observations[1].ObservedPlayerBounds == new WorldRect(new(80, 120), new(40, 60)),
            "A capture-matched body and its image-supported foot must persist in solve pixels.");
    }

    internal static void TestUnconfirmedGroundDoesNotBecomePersistentGeometry()
    {
        var observed = new DetectedFloorLine(300, new(10, 600));
        var review = new GroundLinePresenceReview(7, observed.ToSemanticLine(), GroundLinePresenceState.Observing, .1, 1);
        var evidence = LiveWorldCaptureEvidence.FromGround(1, 640, 360, 640, [observed], [review]);
        Check(evidence.GroundGeometry is null && evidence.FloorCandidates?.Lines.SequenceEqual(
            [new(0, new(10, 60), new(601, 60))]) == true,
            "Unconfirmed evidence supplies an image recovery candidate without creating a floor.");
        var invalid = LiveWorldCaptureEvidence.FromGround(2, 640, 360, double.NaN, [observed], [review]);
        Check(invalid.GroundGeometry is null && invalid.FloorCandidates is null,
            "An invalid solve width cannot produce stored evidence.");
    }

    internal static void TestEightNearbyMeasurementsProduceOneFloorEstimate()
    {
        var observations = Enumerable.Range(0, 8).Select(id =>
            Observation(id, 0, default, Line(4, 20, 90, 80 + id))).ToArray();
        var visible = LiveWorldGroundOverlay.Project(World(observations, Island(5, observations)));
        Check(visible.SequenceEqual([Stroke(4, -300, -230, -96.5)]),
            "Eight measurements of the same edge should draw one median row.");
    }

    internal static void TestHeightClustersAreBoundedAndEachObservationVotesOnce()
    {
        var observations = new[]
        {
            Observation(0, 0, default,
                Line(4, 10, 55, 80), Line(4, 11, 54, 80), Line(4, 12, 53, 80), Line(4, 13, 52, 80)),
            Observation(1, 0, default, Line(4, 10, 55, 86)),
            Observation(2, 0, default, Line(4, 10, 55, 88)),
            Observation(3, 0, default, Line(4, 10, 55, 94)),
            Observation(4, 0, default, Line(4, 10, 55, 100)),
        };
        var projected = LiveWorldGroundOverlay.Project(World(observations, Island(0, observations)));
        Check(projected.SequenceEqual([Stroke(4, -310, -265, -94), Stroke(4, -310, -265, -83)]),
            "One fragmented image must not dominate the height vote, and nearby rows must not chain across heights.");
    }

    internal static void TestNearbySegmentsCombineOnlyWithinTheirRoomAndIslandWithoutFillingGaps()
    {
        var first = Observation(0, 0, default, Line(4, 0, 50, 80));
        var repeated = Observation(1, 0, default, Line(4, 25, 70, 80), Line(4, 80, 90, 80));
        var secondSegment = Observation(2, 0, default,
            Line(5, 12, 25, 81), Line(7, 0, 70, 135), Line(8, 90, 120, 81));
        var otherIsland = Observation(3, 0, default, Line(4, 0, 70, 80));
        var otherRoom = Observation(4, 1, default, Line(4, 0, 70, 80));
        var snapshot = World([first, repeated, secondSegment, otherIsland, otherRoom],
            Island(0, first, repeated, secondSegment), Island(1, otherIsland), Island(2, otherRoom));
        var projected = LiveWorldGroundOverlay.Project(snapshot);
        Check(projected.Count == 6 && projected.Count(line => line == Stroke(4, -320, -250, -100)) == 3
            && projected.Contains(Stroke(4, -240, -230, -100)) && projected.Contains(Stroke(7, -320, -250, -45))
            && projected.Contains(Stroke(8, -230, -200, -99)),
            "A crack, a higher platform, and distinct room and island owners remain separate.");
    }

    internal static void TestProjectsOnlyPlacedMeasuredEvidenceThroughRevisedPoses()
    {
        const long nativeSegment = (long)int.MaxValue + 4;
        var measured = Observation(0, 0, new(100, 20), Line(nativeSegment, 20, 70, 70)) with
        {
            ObservedFloorCandidates = new([Line(12, 50, 100, 110)])
        };
        var pending = Observation(1, 0, new(10, 10), Line(6, 20, 90, 70));
        var prediction = Observation(2, 0, new(20, 20), Line(7, 10, 90, 80));
        var map = World([measured, pending, prediction], Island(0, measured),
            Island(1, pending) with { Placement = LiveIslandPlacement.Estimated, BreakReason = LiveTrackingIsland.RecoveryBreakReason },
            LiveTrackingIsland.Legacy(2, prediction));
        var revised = map.TranslateRoom(0, new(17, -4));
        var reopened = LiveWorldJson.ReadSnapshot(LiveWorldJson.WriteSnapshot(revised));
        Check(reopened.Observations[0].ObservedFloorCandidates!.Lines
            .SequenceEqual(measured.ObservedFloorCandidates!.Lines), "Candidate spans remain unchanged in the source image.");
        Check(LiveWorldGroundOverlay.Project(map).SequenceEqual([Stroke(nativeSegment, -200, -150, -90)])
            && LiveWorldGroundOverlay.Project(reopened).SequenceEqual([Stroke(nativeSegment, -183, -133, -94)]),
            "Only the placed, confirmed stroke follows the saved pose correction.");
        var legacy = new LiveWorldSnapshot(anchoredObservationId: 0, observations: [measured]);
        Check(LiveWorldGroundOverlay.Project(legacy).SequenceEqual([Stroke(nativeSegment, -200, -150, -90)]),
            "An older Windows save can contain a confirmed span without an island ledger.");
    }

    internal static void TestWalkedFloorReplacesOverlappingSavedAndLiveEvidence()
    {
        var savedFloor = Observation(0, 0, default, Line(4, 10, 110, 80), Line(5, 10, 110, 130),
            Line(long.MaxValue, 30, 80, 72));
        var saved = LiveWorldGroundOverlay.Project(World([savedFloor], Island(0, savedFloor)));
        var walked = WalkedGroundMemory.WalkedSegmentId;
        Check(saved.SequenceEqual([Stroke(walked, -290, -240, -108),
            Stroke(4, -310, -290, -100), Stroke(4, -240, -210, -100), Stroke(5, -310, -210, -50)]),
            "Persisted contact should cut only nearby covered columns and preserve the upper platform.");
        var live = new[] { Stroke(walked, -270, -230, -108), Stroke(7, -310, -215, -102) };
        var display = LiveWorldGroundOverlay.CombineWithLive(saved, live, 360, new(0, 0, 10, 25));
        Check(display.SequenceEqual([Stroke(walked, -290, -240, -108), Stroke(walked, -240, -230, -108),
            Stroke(4, -310, -290, -100), Stroke(5, -310, -210, -50), Stroke(4, -230, -210, -100)]),
            "The live walk extends the remembered footprint and hides the nearby edge and duplicate stroke.");
    }

    internal static void TestWalkedFootprintsJoinOnlyAcrossPlacedIslandsInOneRoom()
    {
        var walked = WalkedGroundMemory.WalkedSegmentId;
        var center = new AtlasPoint(320, 180);
        LiveWorldObservation[] frames =
        [
            Observation(0, 0, center, Line(walked, 10, 60, 80)),
            Observation(1, 0, center, Line(walked, 55, 95, 86)),
            Observation(2, 0, center, Line(walked, 100, 110, 81)),
            Observation(3, 0, center, Line(33, 0, 125, 85), Line(34, 40, 70, 140)),
            Observation(4, 1, center, Line(walked, 0, 90, 80)),
            Observation(5, 0, center, Line(walked, 95, 105, 84))
        ];
        var owners = frames.Select(frame => Island(frame.Id, frame)).ToArray();
        owners[5] = owners[5] with
        {
            Placement = LiveIslandPlacement.Estimated,
            BreakReason = LiveTrackingIsland.RecoveryBreakReason
        };
        var view = LiveWorldGroundOverlay.ProjectWorld(World(frames, owners));
        GroundHypothesisAtlasLine[] room0Walk = [Stroke(walked, 10, 95, 83), Stroke(walked, 100, 110, 81)];
        var room1Walk = Stroke(walked, 0, 90, 80);
        Check(view.DisplayLines.Where(line => line.SegmentId == walked).SequenceEqual([..room0Walk, room1Walk]),
            "Nearby walked rows must join across placed islands and retain gaps and room boundaries.");
        Check(view.DisplayLines.Where(line => line.SegmentId == 33).SequenceEqual(
            [Stroke(33, 0, 10, 85), Stroke(33, 95, 100, 85), Stroke(33, 110, 125, 85)])
            && view.DisplayLines.Contains(Stroke(34, 40, 70, 140)),
            "Walked evidence may cover a nearby edge only on its measured columns in the same room.");
        Check(view.PathRooms[0].Ground.Where(line => line.SegmentId == walked).SequenceEqual(room0Walk)
            && view.PathRooms[1].Ground.SequenceEqual([room1Walk]),
            "The saved route must follow the same verified walked footprints.");
    }

    internal static void TestWalkedHeightUsesOneVotePerImageAndDoesNotChainRows()
    {
        var walked = WalkedGroundMemory.WalkedSegmentId;
        var center = new AtlasPoint(320, 180);
        var fragments = Enumerable.Range(0, 5).Select(index => Line(walked, index * 5, 80 + index * 5, 86)).ToArray();
        LiveWorldObservation[] frames =
        [
            Observation(0, 0, center, Line(walked, 0, 100, 80)),
            Observation(1, 0, center, Line(walked, 0, 100, 81)),
            Observation(2, 0, center, fragments),
            Observation(3, 0, center, Line(walked, 0, 100, 92))
        ];
        var world = World(frames, frames.Select(frame => Island(frame.Id, frame)).ToArray());
        Check(LiveWorldGroundOverlay.Project(world).SequenceEqual(
                [Stroke(walked, 0, 100, 81), Stroke(walked, 0, 100, 92)]),
            "The fragmented image has one vote and the next height cannot join through its adjacent row.");
    }

    internal static void TestSnapshotRejectsNonfiniteConfirmedGround()
    {
        var frame = Observation(0, 0, default,
            new LiveGroundLineEvidence(1, new(1, double.NaN), new(30, 100)));
        try
        {
            _ = World([frame]);
        }
        catch (LiveWorldModelException error) when (error.Error == LiveWorldModelError.InvalidGeometry)
        {
            return;
        }
        throw new InvalidOperationException("A confirmed stroke with an invalid coordinate entered the saved world.");
    }

    internal static void TestNavigationUsesOnlyPlacedGroundAndCameraFootprintsFromItsRoom()
    {
        var known = Observation(0, 0, new(150, 50), Line(4, 20, 90, 70));
        var estimated = Observation(1, 0, new(500, 50), Line(5, 20, 90, 70));
        var pending = Observation(2, 0, new(900, 50), Line(6, 20, 90, 70));
        var local = Observation(3, 1, new(1200, 70), Line(7, 20, 90, 70));
        var map = World([known, estimated, pending, local], Island(0, known),
            Island(1, estimated) with { Placement = LiveIslandPlacement.Estimated, BreakReason = "room-cut" },
            Island(2, pending) with { Placement = LiveIslandPlacement.Estimated, BreakReason = LiveTrackingIsland.RecoveryBreakReason },
            Island(3, local) with
            {
                Placement = LiveIslandPlacement.Estimated,
                BreakReason = LiveTrackingIsland.PlacedTrackingBreakReason
            });
        var projection = LiveWorldGroundOverlay.ProjectWorld(map);
        Check(projection.DisplayLines.Count == 3, "The estimated visible floor retains its display stroke.");
        Check(projection.PathRooms[0].Ground.SequenceEqual([Stroke(4, -150, -80, -60)])
            && projection.PathRooms[0].CameraFootprints.SequenceEqual([new(new(-170, -130), new(640, 360))])
            && projection.PathRooms[1].Ground.SequenceEqual([Stroke(7, 900, 970, -40)]),
            "The route and camera coverage must stay inside their measured room and placement.");
    }

    private static LiveWorldSnapshot World(IReadOnlyList<LiveWorldObservation> images, params LiveTrackingIsland[] islands) =>
        new(anchoredObservationId: images[0].Id, observations: images, trackingIslands: islands);

    private static LiveTrackingIsland Island(int id, params LiveWorldObservation[] images) =>
        new(id, images[0].RoomId, images[0].Timestamp, default, LiveIslandPlacement.Connected, default, "test",
            images.Select(image => new LiveIslandMember(image.Id, image.OptimizedPose, true)).ToArray());

    private static LiveWorldObservation Observation(int id, int room, AtlasPoint pose, params LiveGroundLineEvidence[] lines) =>
        LiveWorldObservation.Create(id, id, id + 1, 1280, 720, 640, 160, [], pose, pose, pose, roomId: room) with
        { GroundGeometry = new(lines) };

    private static LiveGroundLineEvidence Line(long id, double left, double right, double y) =>
        new(id, new(left, y), new(right, y));

    private static GroundHypothesisAtlasLine Stroke(long id, double left, double right, double y) =>
        new(id, new(left, y), new(right, y));

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
