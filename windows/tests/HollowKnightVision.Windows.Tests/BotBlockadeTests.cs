using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotBlockadeTests
{
    internal static void TestCameraCompensationAndCrossingInBothDirections()
    {
        foreach (var side in new[] { -1, 1 })
        {
            var visit = new BotBlockadeVisit();
            for (var i = 0; i < 9; i++)
                Require(Observe(i, side * i * .025, .5, side) is null, "Following the moving Knight cannot prove a stall.");
            for (var i = 9; i < 15; i++)
                Require(Observe(i, side * .2, .5, 0) is null, "Neutral cannot establish a blockade.");
            BotBlockadeEvidence? evidence = null;
            for (var i = 15; i < 23; i++)
            {
                var drift = side * (i - 15) * .003;
                evidence = Observe(i, side * .2 + drift, .5 - drift, side);
                if (i < 17) Require(evidence is null, "The camera eased before contact was confirmed.");
            }
            Require(evidence?.Mark.Direction == side, "A world-stationary push must expose the correct side.");
            Require(Observe(23, side * .24, .5, side) is null, "Passage must retire the barrier.");
            Require(Observe(24, side * .2, .5, 0) is null, "Returning to a crossed barrier must leave it retired.");

            BotBlockadeEvidence? Observe(int tick, double camera, double knight, int drive)
            {
                var row = Row(tick, knight, drive, camera);
                return visit.Observe(row, BotBlockadePose.Visual(row));
            }
        }
    }

    internal static void TestRequiresFreshGroundContactsAndResetsCoordinates()
    {
        var visit = new BotBlockadeVisit();
        var pose = new BotBlockadePose("test", 0, 0, 1);
        for (var i = 0; i < 9; i++)
            Require(visit.Observe(Row(i) with { DetectionFrameId = 10 }, pose) is null,
                "Repeating one detector frame with new timestamps cannot establish a blockade.");
        for (var i = 9; i < 15; i++)
        {
            var row = Row(i) with { Grounded = i % 2 == 0 };
            Require(visit.Observe(row, pose) is null, "Airborne samples interrupt a ground push.");
        }
        Require(visit.Observe(Row(15), null) is null, "A missing pose cannot prove a stationary push.");
        Require(visit.Observe(Row(16) with { KnightSource = "visual-predicted" }, pose) is null,
            "A predicted position cannot prove contact.");
        Require(visit.Observe(Row(16) with { Features = [.5, .2, .03, .05, 0, 0, 1] }, pose) is null,
            "A frame without a measured Knight cannot prove contact.");
        Require(visit.Observe(Row(17), pose) is null && visit.Observe(Row(18), pose) is null
            && visit.Observe(Row(19), pose) is not null, "Fresh frames can establish the contact after a gap.");
        Require(visit.Observe(Row(20) with { RoomId = 2 }, pose) is null
            && visit.Observe(Row(21, drive: 0), pose) is null, "The old room must not retain a contact on reentry.");
        Require(visit.Observe(Row(22), pose) is null && visit.Observe(Row(23), pose) is null
            && visit.Observe(Row(24), pose) is not null, "A fresh visit may remeasure the obstruction.");
        Require(visit.Observe(Row(25, drive: 0), pose with { Space = "new-camera-chart" }) is null,
            "Changing visual coordinates must retire the old chart.");
        for (var i = 26; i <= 28; i++)
            _ = visit.Observe(Row(i), pose);
        visit.Reset();
        Require(visit.Observe(Row(29), pose) is null, "A new bot run needs new evidence.");
    }

    internal static void TestMeasuredCameraHandoffPreservesBlockadeAndCrossing()
    {
        foreach (var side in new[] { -1, 1 })
        {
            var visit = new BotBlockadeVisit();
            BotCaptureObservation? contact = null;
            for (var tick = 10; tick <= 12; tick++)
            {
                visit.ObserveVisualBasis(1, 1, 4, 640, tick / 10d, true, GroundHypothesisTrackingResult.Empty);
                contact = visit.AddVisualEvidence(Row(tick, drive: side));
            }
            Require(contact?.Blockade?.Mark.Direction == side, "The source chart must have a measured wall.");
            var original = contact!.Blockade!.Mark;
            var shift = new AtlasPoint(side * 40 / 640d, 16 / 640d);
            visit.ObserveVisualBasis(1, 1, 5, 640, 1.3, true, CameraCorrection(side * 40, 16));
            // The correction arrived between sampled rows. An older analysis update cannot undo it.
            visit.ObserveVisualBasis(1, 1, 4, 640, 1.25, true, GroundHypothesisTrackingResult.Empty);
            visit.ObserveVisualBasis(1, 1, 5, 640, 1.4, true, GroundHypothesisTrackingResult.Empty);
            var updated = visit.AddVisualEvidence(Translated(14, .5));
            Require(updated.Blockade is { } evidence && evidence.Mark.Space == updated.BlockadePose!.Space
                && evidence.Mark.Space != original.Space && evidence.Mark.Direction == side
                && Math.Abs(evidence.Mark.X - original.X - shift.X) < 1e-9
                && Math.Abs(evidence.Mark.Y - original.Y - shift.Y) < 1e-9
                && Math.Abs(evidence.Dx - contact.Blockade.Dx) < 1e-9 && Math.Abs(evidence.Dy) < 1e-9,
                "A measured translation must preserve the physical contact and its policy distance.");
            Require(visit.AddVisualEvidence(Translated(15, .5 + side * .05)).Blockade is null
                && visit.AddVisualEvidence(Translated(16, .5)).Blockade is null,
                "Crossing the translated wall must retire it for the remainder of the visit.");

            BotCaptureObservation Translated(int tick, double knight) =>
                Row(tick, knight, drive: 0) with { VisualContext = new(640, 360d / 640, shift.X, shift.Y) };
        }

        // Gym can measure a correction without changing its room counter.
        var gym = new BotBlockadeVisit();
        for (var tick = 10; tick <= 11; tick++)
        {
            gym.ObserveVisualBasis(1, 1, 4, 640, tick / 10d, true, GroundHypothesisTrackingResult.Empty);
            Require(gym.AddVisualEvidence(Row(tick)).Blockade is null, "The push must wait for 0.15 seconds.");
        }
        gym.ObserveVisualBasis(1, 1, 4, 640, 1.2, true, CameraCorrection(40, 0) with { LocalReferenceTimestamp = 1.1 });
        Require(gym.AddVisualEvidence(Row(12, camera: 40 / 640d)).Blockade is { Mark.Direction: 1 },
            "A verified basis correction must carry the in-progress push while Gym remains in the same room.");
    }

    internal static void TestUnverifiedHandoffCannotCarryBlockade()
    {
        var proof = CameraCorrection(40, 16);
        foreach (var (candidate, room, width, verified) in new[]
        {
            (GroundHypothesisTrackingResult.Empty, 1, 640, true),
            (proof with { LocalReferenceTimestamp = .91 }, 1, 640, true),
            (proof with { LocalReferenceTimestamp = 1.3 }, 1, 640, true),
            (proof with { LocalMatchedImageDisplacement = new(double.NaN, 1) }, 1, 640, true),
            (proof with { LocalTextureSupport = 0, InlierCount = 4 }, 1, 640, true),
            (proof, 1, 320, true),
            (proof, 1, 640, false),
            (proof, 2, 640, true)
        })
        {
            var visit = new BotBlockadeVisit();
            BotCaptureObservation? contact = null;
            for (var tick = 10; tick <= 12; tick++)
            {
                visit.ObserveVisualBasis(1, 1, 4, 640, tick / 10d, true, GroundHypothesisTrackingResult.Empty);
                contact = visit.AddVisualEvidence(Row(tick));
            }
            Require(contact?.Blockade is not null, "The source observation must have contact before the rejected handoff.");
            visit.ObserveVisualBasis(1, room, 5, width, 1.3, verified, candidate);
            Require(visit.AddVisualEvidence(Row(13, drive: 0, camera: 40 / 640d) with { RoomId = room }).Blockade is null,
                "A changed room, scale, or unverified correction must never inherit a wall.");
        }
    }

    internal static void TestPinnedSceneryCameraAllowsWalkAndMeasuresDoorPush()
    {
        foreach (var side in new[] { -1, 1 })
        {
            var visit = new BotBlockadeVisit();
            for (var tick = 0; tick <= 12; tick++)
                Require(Observe(tick, .5 + side * .004 * tick).Blockade is null,
                    "Slow walking at a camera limit must remain unblocked.");

            var pinned = .5 + side * .05;
            for (var tick = 13; tick <= 15; tick++) _ = Observe(tick, pinned);
            var wall = Observe(16, pinned);
            Require(wall.Blockade is { } contact && contact.Mark.Direction == side
                && contact.Mark.Space == wall.BlockadePose?.Space
                && wall.BlockadePose is { X: .1, Y: -.05, Width: 1 },
                "Held movement against a door must produce the blockade with a measured scenery pose and no ground labels.");
            Require(Observe(17, pinned + side * .05).Blockade is null && Observe(18, pinned, drive: false).Blockade is null,
                "Passage must clear the blocked direction for this visit.");

            BotCaptureObservation Observe(int tick, double knight, bool drive = true)
            {
                var row = Row(tick, knight, drive ? side : 0) with
                {
                    Issues = ["missing-terrain-analysis"],
                    VisualContext = new(640, 360d / 640, null, null)
                };
                visit.ObserveVisualBasis(row.Generation, row.RoomId, 4, 640, row.CaptureTimestamp,
                    false, GroundHypothesisTrackingResult.Empty, new(64, -32));
                return visit.AddVisualEvidence(row);
            }
        }
    }

    internal static void TestSceneryBlockadePoseMatchesTheCapture()
    {
        var visit = new BotBlockadeVisit();
        var row = Row(10) with { VisualContext = new(640, 360d / 640, null, null) };
        var ground = GroundHypothesisTrackingResult.Empty;
        visit.ObserveVisualBasis(1, 1, 4, 640, 1, false, ground, new(64, -32));
        Require(visit.AddVisualEvidence(row with { CaptureTimestamp = .99 }).BlockadePose is null
            && visit.AddVisualEvidence(row with { Generation = 2 }).BlockadePose is null
            && visit.AddVisualEvidence(row with { RoomId = 2 }).BlockadePose is null
            && visit.AddVisualEvidence(row with { VisualContext = new(320, 180d / 320, null, null) }).BlockadePose is null,
            "A measured pose belongs to one frame, capture generation, room and scale.");
        visit.ObserveVisualBasis(1, 1, 4, 640, .95, false, ground, new(128, 0));
        Require(visit.AddVisualEvidence(row).BlockadePose is { X: .1, Y: -.05 },
            "An older analysis update cannot replace the current scenery pose.");

        visit.ObserveVisualBasis(1, 1, 4, 640, 1.1, false, ground, new(double.NaN, 0));
        Require(visit.AddVisualEvidence(Row(11) with { VisualContext = row.VisualContext }).BlockadePose is null,
            "Invalid or unmeasured camera poses must not leave reusable evidence in the visit.");
        visit.ObserveVisualBasis(1, 1, 4, 640, 1.2, true, ground, new(128, 0));
        Require(visit.AddVisualEvidence(Row(12, camera: .3)).BlockadePose is { X: .3 },
            "A verified ground camera must own its capture's coordinates.");
    }

    internal static void TestWallContactFollowsRegisteredIslandsAndLatestCorrection()
    {
        const int width = 640;
        var graph = WallIslands();
        var first = Project(graph, 0);
        var next = Project(graph, 1);
        var visit = new BotBlockadeVisit();
        BotCaptureObservation? row = null;
        for (var tick = 10; tick <= 12; tick++)
            row = visit.AddAtlasEvidence(Row(tick), first, default(AtlasPoint), width);
        var mark = row?.Blockade?.Mark ?? throw new InvalidOperationException("The first island must have a measured wall contact.");
        Require(mark.Space == first.Space && mark.ViewWidth == width, "The captured mark must retain its measured island and scale.");

        var nextOrigin = new AtlasPoint(150, -30);
        var transferred = visit.AddAtlasEvidence(Row(13, drive: 0), next, nextOrigin, width);
        Require(transferred.Blockade is { } contact && contact.Mark == mark && Math.Abs(contact.Dx - .015) < 1e-9
            && Math.Abs(contact.Dy) < 1e-9 && transferred.BlockadePose?.Space == next.Space,
            "The next island must see the same wall at the same policy distance.");

        var corrected = graph.ReplaceOptimizedPoses(new Dictionary<int, AtlasPoint> { [0] = new(436, 220), [1] = new(420, 220) },
            graph.MapRevision);
        next = Project(corrected, 1);
        var repeated = visit.AddAtlasEvidence(Row(14, .58, 0) with { DetectionFrameId = 13 }, next, nextOrigin, width);
        Require(repeated.Blockade is { } cached && cached.Mark == mark && Math.Abs(cached.Dx - .04) < 1e-9,
            "A repeated detection can reproject the contact, without asserting that the Knight crossed it.");
        var moved = visit.AddAtlasEvidence(Row(14, drive: 0), next, nextOrigin, width);
        Require(moved.Blockade is { } changed && changed.Mark == mark && Math.Abs(changed.Dx - .04) < 1e-9,
            "A later correction must move the wall in policy coordinates while leaving the saved mark local.");
        Require(visit.AddAtlasEvidence(Row(15, .58, 0), next, nextOrigin, width).Blockade is null
            && visit.AddAtlasEvidence(Row(16, drive: 0), Project(corrected, 0), default(AtlasPoint), width).Blockade is null,
            "Passing a projected wall must retire its original mark before returning to its island.");
    }

    internal static void TestWallContactWaitsForVerifiedIslandAndScope()
    {
        var graph = WallIslands(pendingSecond: true);
        var first = Project(graph, 0);
        var pending = Project(graph, 1);
        Require(first.OffsetFrom(pending.Space) is null && pending.OffsetFrom(first.Space) is null
            && first.OffsetFrom(first.Space + "1") is null && first.OffsetFrom("visual/8/0/0") is null
            && first.OffsetFrom("other/7/0/0") is null && first.OffsetFrom("invalid") is null,
            "Only verified placements with matching room and saved island identity can be related.");
        Require(BotAtlasBlockadeProjection.Create(graph, 8, new(0, new(100, 40), true)) is null
            && BotAtlasBlockadeProjection.Create(graph, 7, new(0, default, true)) is null
            && BotAtlasBlockadeProjection.Create(graph, 7, null) is null,
            "An absent, stale or wrong-room owner must not supply a wall projection.");

        const int width = 640;
        var visit = new BotBlockadeVisit();
        for (var tick = 10; tick <= 11; tick++)
            Require(visit.AddAtlasEvidence(Row(tick), first, default(AtlasPoint), width).Blockade is null,
                "A stationary push needs fresh contact over time.");
        var placed = WallIslands();
        var other = Project(placed, 1);
        var otherOrigin = new AtlasPoint(150, -30);
        Require(visit.AddAtlasEvidence(Row(12), other, otherOrigin, width).Blockade?.Mark.Space == other.Space,
            "A verified translation must carry a fresh stationary push into the new island.");
        Require(visit.AddAtlasEvidence(Row(13, drive: 0), pending, otherOrigin, width).Blockade?.Mark.Space == pending.Space,
            "A measured island can use its own contact while its placement is still pending.");
        Require(visit.AddAtlasEvidence(Row(14, drive: 0), first, default(AtlasPoint), width).Blockade is null,
            "An unplaced source cannot publish its wall into another island.");
        Require(visit.AddAtlasEvidence(Row(15, drive: 0), Project(placed, 0), default(AtlasPoint), width).Blockade is not null,
            "A contact retained during recovery becomes available once its island is placed.");
        Require(visit.AddAtlasEvidence(Row(16, drive: 0), null, null, width).BlockadePose is null
            && visit.AddAtlasEvidence(Row(17, drive: 0), other, otherOrigin, width / 2).Blockade is null
            && visit.AddAtlasEvidence(Row(18, drive: 0) with { Generation = 2 }, other, otherOrigin, width).Blockade is null,
            "Missing placement, incompatible image scale and a new capture visit cannot inherit old contacts.");
    }

    internal static void TestMeasuredPushSurvivesAtlasAdmissionCadence()
    {
        const int width = 640;
        var assigned = Project(WallIslands(), 0);
        foreach (var coordinateEpoch in new long[] { 4, 5 })
        {
            var visit = new BotBlockadeVisit();
            var first = visit.AddAtlasEvidence(Row(10), null, default(AtlasPoint), width, measuredLineage: 1, coordinateEpoch: 4);
            Require(first.Blockade is null && first.BlockadePose?.Space.StartsWith("visual-local/", StringComparison.Ordinal) == true,
                "A measured frame may track a local contact before its island is committed.");
            var registered = visit.AddAtlasEvidence(Row(12), assigned, default(AtlasPoint), width,
                measuredLineage: 1, coordinateEpoch: coordinateEpoch);
            Require(registered.BlockadePose?.Space == assigned.Space
                && (coordinateEpoch == 4 ? registered.Blockade?.Mark.Space == assigned.Space : registered.Blockade is null),
                "Only the exact measured coordinate epoch can carry an in-progress push through Atlas admission.");
        }

        var local = new BotBlockadeVisit();
        for (var tick = 10; tick <= 12; tick++)
            _ = local.AddAtlasEvidence(Row(tick), null, default(AtlasPoint), width, measuredLineage: 1, coordinateEpoch: 4);
        var result = local.AddAtlasEvidence(Row(13, drive: 0), assigned, default(AtlasPoint), width, measuredLineage: 1, coordinateEpoch: 4);
        Require(result.Blockade is { } contact && contact.Mark.Space == assigned.Space && Math.Abs(contact.Dx - .015) < 1e-9,
            "Binding a measured local contact to its assigned island must preserve its position and policy distance.");
    }

    private static LiveWorldSnapshot WallIslands(bool pendingSecond = false)
    {
        var frame = new BgraFrame(32, 24, 128, new byte[32 * 24 * 4], 1, DateTimeOffset.UnixEpoch);
        var world = new LiveWorldTracker();
        world.Ingest(0, 1, frame, new(320, 180), 640, searchForClosure: false, localPose: new(320, 180),
            captureGeneration: 1, roomId: 7, placedCameraPose: new(420, 220), captureEvidence: new(1) { MeasuredLineage = 1 });
        world.Ingest(1, 2, frame, new(470, 150), 640, searchForClosure: false, localPose: new(470, 150),
            captureGeneration: 1, roomId: 7, placedCameraPose: new(420, 220), captureEvidence: new(2) { MeasuredLineage = 2 },
            placementPending: pendingSecond);
        return world.Snapshot;
    }

    private static BotAtlasBlockadeProjection Project(LiveWorldSnapshot graph, int islandId)
    {
        var island = graph.TrackingIslands.Single(item => item.Id == islandId);
        return BotAtlasBlockadeProjection.Create(graph, island.RoomId,
            new(island.Id, island.WorldFromLocal, !island.RequiresPlacement))!;
    }

    private static GroundHypothesisTrackingResult CameraCorrection(double x, double y) =>
        GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 4,
            LocalReferenceTimestamp = 1.2, LocalReferencePosition = default(CameraVector),
            LocalMatchedImageDisplacement = default(CameraVector), GlobalCorrection = new(x, y)
        };

    internal static void TestLegacyReplayUsesOnlyPriorInputAndLeavesRecordedRowsIntact()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-blockade-replay-");
        try
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var id = Guid.NewGuid();
            var manifest = new BotCaptureManifest(2, id, DateTimeOffset.UnixEpoch, 0, "normal", "fixture", "native-posted",
                "completed", 1, 10, 10, 1, 3, "gameplay", null, null);
            File.WriteAllText(Path.Combine(temporary.FullName, "manifest.json"), JsonSerializer.Serialize(manifest, json));
            var frames = Enumerable.Range(0, 10).Select(i => Row(i, drive: null) with
            {
                BlockadeVersion = i == 6 ? 1 : null,
                VisualContext = i == 7 ? null : Row(i).VisualContext,
                Features = i >= 8 ? Row(i, .54).Features : Row(i).Features,
                ImagePath = i is 4 or 5 or 6 ? $"{i}.png" : null
            });
            foreach (var index in new[] { 4, 5, 6 }) File.WriteAllText(Path.Combine(temporary.FullName, $"{index}.png"), "fixture");
            var events = Path.Combine(temporary.FullName, "events.jsonl");
            File.WriteAllLines(events, frames.Select(row => JsonSerializer.Serialize(row, json))
                .Append(JsonSerializer.Serialize(new BotCaptureInput(.25, "right", true), json)));
            var before = File.ReadAllBytes(events);
            var recovered = BotBlockadeReplay.Recover(temporary.FullName);
            Require(recovered.Id == id && recovered.Rows.Select(row => row.CaptureTimestamp).SequenceEqual([.5])
                && recovered.Rows[0].Blockade.Mark.Direction == 1,
                "The replay may only recover contact after prior input, with a measured pose, on rows without a saved decision.");
            var review = BotClipTimeline.Open(temporary.FullName, Path.Combine(temporary.FullName, "no-source-images"));
            Require(review.Count == 3 && review.Overlay(0)?.Blockade is null
                && review.Overlay(1)?.Blockade == recovered.Rows[0].Blockade && review.Overlay(2)?.Blockade is null,
                "Clip review must attach recovered wall contact only to its original observation, without overriding a saved decision.");
            Require(before.SequenceEqual(File.ReadAllBytes(events)), "Replaying blockade evidence cannot modify the clip.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static BotCaptureObservation Row(int tick, double knightX = .5, int? drive = 1, double camera = 0)
    {
        var time = tick / 10d;
        return new(time, time, time + .001, 1, 1, 0, [knightX, .2, .03, .05, 1, 0, 1], [], "detected")
        {
            DetectionTimestamp = time,
            DetectionFrameId = tick,
            DriveDirection = drive,
            KnightSource = "visual",
            VisualContext = new(640, 360d / 640, camera, 0)
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
