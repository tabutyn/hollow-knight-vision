using HollowKnightVision.Windows.Core;

internal static class LiveWorldGroundOverlayTests
{
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
