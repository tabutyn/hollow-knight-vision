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
            var transition = new TransitionFrameDecision(index < 8 || index >= 18, index == 18);
            var result = rooms.Observe(6, true, transition, image, index * .05, InputButtons.Right, Departure);
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
        var newCapture = rooms.Observe(7, true, new(true, false), arrival, 2.2, InputButtons.None, Departure);
        Check(newCapture.Id == "normal:7:room:0" && newCapture.AllowsAtlasWrite);
    }

    internal static void TestAtlasRetainsSameRoomAfterLoadingEvidenceRejectsTheSplit()
    {
        var rooms = new GameplayTrackingPipeline.NormalAtlasRoomIdentity();
        var original = Texture(1);
        for (var index = 0; index < 40; index++)
        {
            var image = index is >= 8 and < 18 ? Black : original;
            // Atlas admission may resume one capture after matching the old scenery.
            var transition = new TransitionFrameDecision(index < 8 || index >= 19, index == 19);
            var result = rooms.Observe(6, true, transition, image, index * .05, InputButtons.Right, Departure);
            Check(result.Id == "normal:6:room:0" && result.Cut is null);
        }
        var unrelatedLaterFade = rooms.Observe(6, true, new(true, true), original, 3.5, InputButtons.None, Departure);
        Check(unrelatedLaterFade.Id == "normal:6:room:1");
        var inactive = rooms.Observe(6, false, new(false, false), Black, 3.6, InputButtons.Right, Departure);
        Check(inactive.AllowsAtlasWrite && inactive.Cut is null);
    }

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

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Visual scene discontinuity assertion failed.");
    }
}
