using HollowKnightVision.Windows.Core;

internal static class VisualSceneDiscontinuityTrackerTests
{
    private const int Width = 64, Height = 36;
    private static readonly AtlasPoint Departure = new(12, 30);
    private static readonly LowResolutionMotionGrid Black = new(Width, Height,
        Enumerable.Range(0, Width * Height).Select(index => index < Width ? (byte)(index * 31 % 256) : (byte)0).ToArray());

    internal static void TestLoadingCutRequiresSustainedBlackoutAndStableNovelScenery()
    {
        var tracker = new VisualSceneDiscontinuityTracker();
        var cut = Sequence(tracker, Texture(1), Black, Texture(2)).Single();
        Check(cut.Direction == VisualRoomDirection.Right && cut.DepartureWorldPose == Departure);
        Check(Math.Abs(cut.DepartureTimestamp - .35) < .0001 && cut.ArrivalTimestamp > .7);
        Check(!tracker.IsPending);
    }

    internal static void TestFadeBackToSameSceneryDoesNotChangeOwnership()
    {
        var original = Texture(1);
        var dimmed = new LowResolutionMotionGrid(Width, Height, original.Luma.Select(value => (byte)(value * .8)).ToArray());
        Check(Sequence(new(), original, Black, dimmed).Count == 0);
    }

    internal static void TestOneChangingSideDoesNotDiscoverRoom()
    {
        var original = Texture(1);
        var halfChanged = Texture(2);
        for (var y = 0; y < Height; y++)
            Array.Copy(original.Luma, y * Width, halfChanged.Luma, y * Width, Width / 2);
        Check(Sequence(new(), original, Black, halfChanged).Count == 0);
    }

    internal static void TestBlackFramesAfterGapMustIndependentlyConfirmBlackout()
    {
        foreach (var trailingBlackFrames in new[] { 1, 6 })
        {
            var tracker = new VisualSceneDiscontinuityTracker();
            var destination = Texture(2);
            tracker.Observe(Texture(1), 0, VisualRoomDirection.Right, Departure);
            tracker.Observe(Black, .05, null, Departure);
            var cuts = 0;
            for (var index = 0; index < 20; index++)
            {
                var grid = index < trailingBlackFrames ? Black : destination;
                if (tracker.Observe(grid, .8 + index * .05, null, Departure) is not null) cuts++;
            }
            Check(cuts == (trailingBlackFrames == 1 ? 0 : 1));
        }
    }

    internal static void TestCounterSteeringAfterSceneryVanishesKeepsVisibleDeparture()
    {
        var tracker = new VisualSceneDiscontinuityTracker();
        var first = Texture(1);
        var second = Texture(2);
        for (var index = 0; index < 8; index++)
            tracker.Observe(first, index * .05, VisualRoomDirection.Right, Departure);
        var cuts = new List<VisualSceneDiscontinuityTracker.Cut>();
        for (var index = 0; index < 30; index++)
        {
            if (tracker.Observe(index < 10 ? Black : second, .4 + index * .05,
                VisualRoomDirection.Left, Departure) is { } cut) cuts.Add(cut);
        }
        Check(cuts.Single().Direction == VisualRoomDirection.Right);
    }

    internal static void TestTinyLoadingSpriteKeepsObservedBlackoutWithoutHidingHudOrScenery()
    {
        var pixels = (byte[])Black.Luma.Clone();
        for (var y = 25; y < 28; y++)
        for (var x = 54; x < 57; x++) pixels[y * Width + x] = 230;
        var glyph = new LowResolutionMotionGrid(Width, Height, pixels);
        Check(VisualSceneDiscontinuityTracker.IsCompleteBlackout(glyph));
        Check(Sequence(new(), Texture(1), glyph, Texture(2), blackFrames: 140).Count == 1);
        Check(Sequence(new(), Texture(1), glyph, Texture(1), blackFrames: 140).Count == 0);
        for (var y = 2; y < 4; y++)
        for (var x = 5; x < 9; x++) pixels[y * Width + x] = 230;
        Check(!VisualSceneDiscontinuityTracker.IsCompleteBlackout(glyph));
        var scattered = (byte[])Black.Luma.Clone();
        for (var x = 2; x < 60; x += 6) scattered[25 * Width + x] = 230;
        Check(!VisualSceneDiscontinuityTracker.IsCompleteBlackout(new(Width, Height, scattered)));
    }

    internal static void TestMovingArrivalConfirmsThroughConsecutiveOverlappingImages()
    {
        foreach (var (stepX, stepY) in new[] { (2, 0), (-2, 1), (0, 2) })
        {
            var tracker = new VisualSceneDiscontinuityTracker();
            var departure = Texture(1);
            var arrival = Texture(2);
            var cuts = 0;
            for (var index = 0; index < 45; index++)
            {
                var grid = index < 8 ? departure : index < 18 ? Black : Shift(arrival, stepX * (index - 18), stepY * (index - 18));
                if (tracker.Observe(grid, index * .05, VisualRoomDirection.Right, Departure) is not null) cuts++;
            }
            Check(cuts == 1);
        }
    }

    internal static void TestAtlasWithholdsUnresolvedLoadingAndSwitchesOwnershipOnce()
    {
        var rooms = new GameplayTrackingPipeline.NormalAtlasRoomIdentity();
        var original = Texture(1);
        var arrival = Texture(2);
        var seenCuts = 0;
        for (var index = 0; index < 40; index++)
        {
            var image = index < 8 ? original : index < 18 ? Black : arrival;
            var result = rooms.Observe(6, true, image, index * .05, InputButtons.Right, Departure);
            if (index < 18) Check(result.Id == "normal:6:room:0");
            if (index is >= 8 and < 23) Check(!result.AllowsAtlasWrite);
            if (result.Cut is not null)
            {
                seenCuts++;
                Check(result.Id == "normal:6:room:1" && !result.AllowsAtlasWrite);
            }
            if (index == 39) Check(result.Id == "normal:6:room:1" && result.AllowsAtlasWrite);
        }
        Check(seenCuts == 1);
        var saved = rooms.RecentDoorwayCut(1.95);
        Check(saved is not null && saved.Direction == VisualRoomDirection.Right);
        Check(rooms.RecentDoorwayCut(saved!.ArrivalTimestamp + 3.1) is null);
        var newCapture = rooms.Observe(7, true, arrival, 2.2, InputButtons.None, Departure);
        Check(newCapture.Id == "normal:7:room:0" && newCapture.AllowsAtlasWrite
            && rooms.RecentDoorwayCut(2.2) is null);
    }

    internal static void TestAtlasRetainsSameRoomAfterLoadingEvidenceRejectsTheSplit()
    {
        var rooms = new GameplayTrackingPipeline.NormalAtlasRoomIdentity();
        var original = Texture(1);
        for (var index = 0; index < 40; index++)
        {
            var image = index is >= 8 and < 18 ? Black : original;
            var result = rooms.Observe(6, true, image, index * .05, InputButtons.Right, Departure);
            Check(result.Id == "normal:6:room:0" && result.Cut is null);
        }
        var afterGap = rooms.Observe(6, true, original, 3.5, InputButtons.None, Departure);
        Check(afterGap.Id == "normal:6:room:0");
        var inactive = rooms.Observe(6, false, Black, 3.6, InputButtons.Right, Departure);
        Check(inactive.AllowsAtlasWrite && inactive.Cut is null);
    }

    internal static void TestConfirmedCrossingStartsFreshGroundTracking()
    {
        const int frameWidth = 640, frameHeight = 360;
        var texture = new byte[frameWidth * frameHeight];
        new Random(53).NextBytes(texture);
        var blank = new byte[texture.Length];
        var departure = Texture(1);
        var arrivals = new[] { departure, Texture(2) };
        foreach (var arrival in arrivals)
        {
            var sceneChanged = arrival != departure;
            var pipeline = new GameplayTrackingPipeline(new VisualGameplayTracker(), new MaskedFrameMotionTracker(),
                deterministicGlobalSearch: true);
            GameplayTrackedFrame? cut = null, anchored = null;
            for (var index = 0; index <= 66; index++)
            {
                var loading = index is >= 24 and < 34;
                var grid = index < 24 ? departure : loading ? Black : arrival;
                var time = index * .05;
                var frame = new BgraFrame(frameWidth, frameHeight, frameWidth * 4, new byte[texture.Length * 4],
                    index, DateTimeOffset.UnixEpoch.AddSeconds(time));
                GameplayTrackingPreparation preparation;
                if (loading)
                {
                    pipeline.ResetMotionReference();
                    preparation = new(null, null, [], []);
                }
                else
                {
                    DetectedFloorLine[] floors = index < 24 || sceneChanged && index >= 46
                        ? [new(index < 24 ? 198 : 210, new(0, frameWidth - 1))] : [];
                    var edges = new GroundEdgeAnalysis(frameWidth, frameHeight, blank, texture);
                    var comparison = new GroundComparisonAnalysis(frameWidth, frameHeight, blank, texture);
                    var theory = new GroundTheoryAnalysis(comparison, GroundTheoryTuning.Default, [], floors);
                    preparation = new(new(edges, theory, floors, floors), null, [], []);
                }
                var result = pipeline.Track(frame, preparation, null, new(!loading, !loading, loading, new(!loading, index == 34)),
                    Profile(grid), time, 1, null, true, InputButtons.Right, true, captureWorldEvidence: true);
                if (index == 23)
                {
                    Check(result.Observation.GroundVerified && result.Ground.GlobalFeatureCount > 0
                        && result.Ground.AtlasLines.Count > 0, "departure must have verified ground and saved local references");
                    anchored = result;
                }
                if (result.SceneCut is not null)
                {
                    Check(cut is null && sceneChanged && !result.Observation.GroundVerified
                        && result.Ground.GlobalFeatureCount == 0 && result.Ground.AtlasLines.Count == 0
                        && !result.AllowsAtlasWrite && result.WorldEvidence is null && result.RequiresWorldRecovery,
                        "the first image owned by a new room is display-only and carries no departure ground");
                    cut = result;
                }
                if (index == 42)
                {
                    Check(sceneChanged ? cut is not null && result.RecentDoorwayCut == cut.SceneCut
                        && result.Ground.GlobalFeatureCount == 0 && result.RoomId != anchored!.RoomId
                        : cut is null && result.Ground.GlobalFeatureCount > 0 && result.RoomId == anchored!.RoomId,
                        "a confirmed cut clears local references while a fade back to the same room retains them");
                }
                if (sceneChanged && index == 66)
                    Check(result.Observation.GroundVerified && result.Ground.GlobalFeatureCount > 0
                        && result.Ground.AtlasLines.Count > 0 && result.MeasuredLineage != anchored!.MeasuredLineage,
                        "fresh images can establish ground in the new room");
            }
        }
    }

    internal static void TestAtlasKeepsRoomOnUnprovenLoading()
    {
        var departure = Texture(1);
        var arrival = Texture(2);
        var hud = (byte[])Black.Luma.Clone();
        for (var y = 2; y < 5; y++)
        for (var x = 5; x < 13; x++) hud[y * Width + x] = 230;
        var darkWithHud = new LowResolutionMotionGrid(Width, Height, hud);
        Check(!VisualSceneDiscontinuityTracker.IsCompleteBlackout(darkWithHud));

        foreach (var (description, first, middle, movement, blackFrames, captureGap) in new[]
        {
            ("startup in black", Black, Black, InputButtons.Right, 10, 0d),
            ("no directional departure", departure, Black, InputButtons.None, 10, 0d),
            ("brief capture glitch", departure, Black, InputButtons.Right, 2, 0d),
            ("dark gameplay with a visible HUD", departure, darkWithHud, InputButtons.Right, 10, 0d),
            ("capture gap before visible loading", departure, Black, InputButtons.Right, 10, .75),
            ("long capture gap during loading", departure, Black, InputButtons.Right, 10, 4d),
        })
        {
            var rooms = new GameplayTrackingPipeline.NormalAtlasRoomIdentity();
            var admission = new GameplayFrameAdmission();
            var resumes = 0;
            for (var index = 0; index < 8 + blackFrames + 16; index++)
            {
                var image = index < 8 ? first : index < 8 + blackFrames ? middle : arrival;
                var gapIndex = captureGap > 3 ? 13 : 8;
                var time = index * .05 + (index >= gapIndex ? captureGap : 0);
                var frame = admission.Observe(Profile(image), true, 6);
                if (frame.Transition.ResumedAfterTransition) resumes++;
                var result = rooms.Observe(6, true, image, time, movement, Departure);
                Check(result.Id == "normal:6:room:0" && result.Cut is null,
                    $"The Atlas invented a room during {description}.");
                if (index == 7 + blackFrames + 16)
                    Check(frame.AllowsAtlasWrite && result.AllowsAtlasWrite, $"Gameplay never resumed after {description}.");
            }
            Check(resumes == 1, $"The capture did not exercise a tracking resume after {description}.");
            Check(rooms.RecentDoorwayCut(2) is null);
        }
    }

    internal static void TestLoadingRetainsEvidenceAcrossBoundedCaptureGaps()
    {
        foreach (var newScenery in new[] { false, true })
        foreach (var blackAfterGap in new[] { false, true })
        {
            var tracker = new VisualSceneDiscontinuityTracker();
            tracker.Observe(Texture(1), 0, VisualRoomDirection.Right, Departure);
            for (var i = 1; i <= 6; i++) tracker.Observe(Black, i * .05, null, Departure);
            Check(tracker.IsPending);
            var arrival = Texture(newScenery ? 2UL : 1UL);
            var cuts = new List<VisualSceneDiscontinuityTracker.Cut>();
            for (var i = 0; i < 20; i++)
            {
                var image = blackAfterGap && i < 4 ? Black : arrival;
                if (tracker.Observe(image, 1 + i * .05, null, Departure) is { } cut) cuts.Add(cut);
            }
            Check(cuts.Count == (newScenery ? 1 : 0));
            if (newScenery)
                Check(cuts.Single() is { DepartureTimestamp: 0, Direction: VisualRoomDirection.Right });
        }
    }

    internal static void TestArrivalRequiresStableNewTextureEvenWithSimilarShading()
    {
        var source = Shaded(1);
        var dimmed = new LowResolutionMotionGrid(Width, Height, source.Luma.Select(value => (byte)(value * .8)).ToArray());
        Check(Sequence(new(), source, Black, Shaded(2)).Count == 1, "Common shading hid a texture change.");
        Check(Sequence(new(), source, Black, dimmed).Count == 0, "An exposure change looked like a new room.");

        var tracker = new VisualSceneDiscontinuityTracker();
        var departure = Texture(1);
        for (var i = 0; i < 45; i++)
        {
            var grid = i < 8 ? departure : i < 18 ? Black : Texture((ulong)(i + 100));
            Check(tracker.Observe(grid, i * .05, VisualRoomDirection.Right, Departure) is null,
                "Flickering novel scenery did not establish a stable arrival.");
        }

        static LowResolutionMotionGrid Shaded(ulong seed)
        {
            var detail = Texture(seed);
            return new(Width, Height, detail.Luma.Select((value, index) => (byte)Math.Clamp(
                110 + 65 * Math.Sin(index % Width / 15d) + 35 * Math.Cos(index / Width / 11d)
                + (value - 128) * .16, 0, 255)).ToArray());
        }
    }

    internal static void TestLongBlackoutWaitsForStableNewScenery()
    {
        var source = Texture(1);
        foreach (var newScenery in new[] { false, true })
        {
            var tracker = new VisualSceneDiscontinuityTracker();
            var arrival = newScenery ? Texture(2) : source;
            var cuts = 0;
            for (var i = 0; i < 200; i++)
            {
                var grid = i < 8 ? source : i < 180 ? Black : arrival;
                if (tracker.Observe(grid, i * .05, i < 8 ? VisualRoomDirection.Right : null, Departure) is not null)
                    cuts++;
                if (i is >= 8 and < 180) Check(tracker.IsPending);
            }
            Check(cuts == (newScenery ? 1 : 0));
        }
    }

    internal static void TestImageOnlyCutPersistsRoomWithoutInventingAPortal()
    {
        var tracker = new VisualRoomTransitionTracker();
        var source = Texture(1);
        var arrival = Texture(2);
        var arrivingPose = new AtlasPoint(207, -39);
        var departureRoom = tracker.Snapshot.RoomID;
        for (var index = 0; index < 40; index++)
        {
            var grid = index < 8 ? source : index < 18 ? Black : arrival;
            var current = tracker.Observe(Profile(grid), new(index < 8 || index >= 18, false),
                VisualRoomDirection.Right, null, new(640, 360), index < 18 ? Departure : arrivingPose, 640, index * .05);
            if (index is >= 8 and < 18)
                Check(current.IsTransitioning && current.TransitionDirection == VisualRoomDirection.Right
                    && current.TransitionWorldPose == Departure && current.RoomID == departureRoom,
                    "Loading must withhold the source room before a confirmed arrival.");
        }
        var arrived = tracker.Snapshot;
        Check(!arrived.IsTransitioning && arrived.RoomID == 1 && arrived.Revision == 1
            && arrived.EntryWorldPose == arrivingPose && arrived.Portals.Count == 0);
        var path = Path.GetTempFileName();
        try
        {
            tracker.SaveTopology(path);
            var restored = new VisualRoomTransitionTracker().RestoreTopology(path);
            Check(!restored.IsTransitioning && restored.RoomID == arrived.RoomID
                && restored.EntryWorldPose == arrived.EntryWorldPose && restored.Portals.Count == 0);
            Check(tracker.Reset() is { RoomID: 0, IsTransitioning: false, Portals.Count: 0 });
        }
        finally { File.Delete(path); }
    }

    private static FrameSignalProfile Profile(LowResolutionMotionGrid grid) =>
        new(grid.Luma.Length, grid.Luma.Count(value => value >= 5), grid.Luma.Count(value => value >= 16),
            grid.Luma.Average(value => (double)value), grid);

    private static List<VisualSceneDiscontinuityTracker.Cut> Sequence(VisualSceneDiscontinuityTracker tracker,
        LowResolutionMotionGrid departure, LowResolutionMotionGrid blackout, LowResolutionMotionGrid arrival, int blackFrames = 10)
    {
        var cuts = new List<VisualSceneDiscontinuityTracker.Cut>();
        for (var index = 0; index < 8 + blackFrames + 16; index++)
        {
            var grid = index < 8 ? departure : index < 8 + blackFrames ? blackout : arrival;
            if (tracker.Observe(grid, index * .05, VisualRoomDirection.Right, Departure) is { } cut) cuts.Add(cut);
        }
        return cuts;
    }

    private static LowResolutionMotionGrid Texture(ulong state)
    {
        var pixels = new byte[Width * Height];
        for (var index = 0; index < pixels.Length; index++)
        {
            state = unchecked(state * 6364136223846793005 + 1);
            pixels[index] = (byte)(20 + ((state >> 32) % 220));
        }
        return new(Width, Height, pixels);
    }

    private static LowResolutionMotionGrid Shift(LowResolutionMotionGrid grid, int dx, int dy)
    {
        var pixels = new byte[Width * Height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = ((index % Width + dx) % Width + Width) % Width;
            var y = ((index / Width + dy) % Height + Height) % Height;
            pixels[index] = grid.Luma[y * Width + x];
        }
        return new(Width, Height, pixels);
    }

    private static void Check(bool value, string message = "Visual scene discontinuity assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
