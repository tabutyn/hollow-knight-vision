using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class LayerSceneViewportParityTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Near(double actual, double expected, double tolerance = .000_001)
    {
        Check(Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected}, got {actual}.");
    }

    private static void Equal(
        ViewportTransform actual,
        ViewportTransform expected,
        double tolerance = .000_001)
    {
        Near(actual.Scale, expected.Scale, tolerance);
        Near(actual.OffsetX, expected.OffsetX, tolerance);
        Near(actual.OffsetY, expected.OffsetY, tolerance);
    }

    private static double WheelDeltaForFactor(double factor) =>
        120 * Math.Log(factor) / Math.Log(1.12);

    private static ViewportTransform FitTarget(ViewportSize size, ViewportRect bounds)
    {
        var controller = new GameplayViewportController();
        return controller.Configure("Fit", size, bounds, null, null, 0);
    }

    public static void TestFreeFlyOwnsPointerDragInsteadOfForwardingItToGame()
    {
        Check(GameplayViewportPolicy.CanPan("Free Fly"),
            "Free Fly did not own viewport drag input.");
        Check(!GameplayViewportPolicy.ForwardsGamePointer("Free Fly"),
            "Free Fly forwarded pointer input to the game.");
        Check(GameplayViewportPolicy.ForwardsGamePointer("Fit"),
            "Fit stopped forwarding ordinary game pointer input.");
        Check(GameplayViewportPolicy.ForwardsGamePointer("Current"),
            "Current stopped forwarding ordinary game pointer input.");
    }

    public static void TestFreeFlyKeepsViewportWhenAtlasBoundsGrow()
    {
        var controller = new GameplayViewportController();
        var expected = controller.SetFreeFlyTransform(new(2, 40, 50));
        var size = new ViewportSize(800, 600);

        controller.Configure("Free Fly", size,
            new ViewportRect(10, 20, 100, 50), null, null, 0);
        controller.Configure("Free Fly", size,
            new ViewportRect(10, 20, 240, 50), null, null, 1);

        Equal(controller.Transform, expected);
    }

    public static void TestZoomKeepsWorldPointUnderMouseCursor()
    {
        var controller = new GameplayViewportController();
        controller.SetFreeFlyTransform(new(2, 10, 20));
        var cursor = new ViewportPoint(110, 220);

        var zoomed = controller.ZoomFreeFly(
            WheelDeltaForFactor(2), cursor, timestamp: 0);

        Near(zoomed.Scale, 4);
        Near(zoomed.OffsetX, -90);
        Near(zoomed.OffsetY, -180);
        Near(50 * zoomed.Scale + zoomed.OffsetX, cursor.X);
        Near(100 * zoomed.Scale + zoomed.OffsetY, cursor.Y);
    }

    public static void TestCurrentZoomStaysCenteredAndTracksMovingViewport()
    {
        var controller = new GameplayViewportController();
        var size = new ViewportSize(1_280, 720);
        var bounds = new ViewportRect(20, 40, 640, 360);
        var focus = new ViewportPoint(340, 220);
        controller.Configure("Current", size, null, bounds, focus, 0);

        var zoomed = controller.ZoomCurrent(
            WheelDeltaForFactor(.5), size, bounds, focus, 0.1);
        Near(focus.X * zoomed.Scale + zoomed.OffsetX, size.Width * .5);
        Near(focus.Y * zoomed.Scale + zoomed.OffsetY, size.Height * .5);

        var movedFocus = new ViewportPoint(440, 220);
        var moved = controller.Configure(
            "Current", size, null, bounds, movedFocus, 1);
        Near(moved.Scale, zoomed.Scale);
        Near(movedFocus.X * moved.Scale + moved.OffsetX, size.Width * .5);
        Near(movedFocus.Y * moved.Scale + moved.OffsetY, size.Height * .5);
    }

    public static void TestCurrentButtonResetsCustomZoomAndCentersViewport()
    {
        var controller = new GameplayViewportController();
        var size = new ViewportSize(1_280, 720);
        var bounds = new ViewportRect(20, 40, 640, 360);
        var focus = new ViewportPoint(340, 220);
        controller.Configure("Current", size, null, bounds, focus, 0);
        controller.ZoomCurrent(
            WheelDeltaForFactor(.5), size, bounds, focus, .1);

        controller.Configure(
            "Current", size, null, bounds, focus, 1, explicitRequest: true);
        var reset = controller.Advance(1 + GameplayViewportController.TransitionDuration);

        Near(GameplayViewportController.CurrentMargin, .88);
        Near(reset.Scale, 1.76);
        Near(controller.CurrentZoomMultiplier, 1);
        Near(focus.X * reset.Scale + reset.OffsetX, size.Width * .5);
        Near(focus.Y * reset.Scale + reset.OffsetY, size.Height * .5);
    }

    public static void TestGamePointerUsesDrawnLiveFrameInsteadOfWholeVisionWindow()
    {
        var normalized = GameplayViewportPolicy.NormalizeGamePointer(
            new ViewportPoint(300, 275),
            new ViewportRect(100, 50, 800, 450),
            invertY: true);
        Check(normalized is not null, "A point inside the drawn live frame was rejected.");
        Near(normalized!.Value.X, .25);
        Near(normalized.Value.Y, .5);
        Check(GameplayViewportPolicy.NormalizeGamePointer(
                new ViewportPoint(50, 275),
                new ViewportRect(100, 50, 800, 450),
                invertY: true) is null,
            "A point outside the drawn live frame used whole-window coordinates.");

        var encoded = ReceiverWireCodec.EncodeLine(ReceiverPointerCommand.Create(
            Guid.Parse("7641a538-4a56-4e9f-9408-6d710238c91a"),
            sequence: 7,
            kind: "moved",
            normalizedX: normalized.Value.X,
            normalizedY: normalized.Value.Y,
            clickCount: 1));
        var command = ReceiverWireCodec.DecodeLine<ReceiverPointerCommand>(encoded);
        Check(command.Kind == "moved", "The pointer event kind changed on the receiver wire.");
        Near(command.NormalizedX, .25, .001);
        Near(command.NormalizedY, .5, .001);
    }

    public static void TestFitButtonStartsSmoothTransitionFromFreeFly()
    {
        var controller = new GameplayViewportController();
        var size = new ViewportSize(800, 600);
        var bounds = new ViewportRect(220, 340, 200, 100);
        var initial = controller.SetFreeFlyTransform(new(2, 40, 50));
        var target = FitTarget(size, bounds);

        var start = controller.Configure(
            "Fit", size, bounds, null, null, 1, explicitRequest: true);
        Equal(start, initial);
        var midpoint = controller.Advance(
            1 + GameplayViewportController.TransitionDuration / 2);
        Check(midpoint != initial, "Fit transition did not leave the Free Fly transform.");
        Check(midpoint != target, "Fit transition stepped directly to its target.");
        Equal(controller.Advance(
            1 + GameplayViewportController.TransitionDuration), target);
        Near(GameplayViewportController.TransitionDuration, .36);
        Near(GameplayViewportController.FitMargin, .90);
    }

    public static void TestFitFollowsGrowingAtlasBoundsWithoutStepping()
    {
        var controller = new GameplayViewportController();
        var size = new ViewportSize(800, 600);
        var initialBounds = new ViewportRect(220, 340, 200, 100);
        var grownBounds = new ViewportRect(220, 340, 800, 100);
        var initial = controller.Configure(
            "Fit", size, initialBounds, null, null, 0);

        var unchanged = controller.Configure(
            "Fit", size, grownBounds, null, null, 1);
        var target = FitTarget(size, grownBounds);
        Equal(unchanged, initial);

        var first = controller.Advance(1 + 1.0 / 60);
        Check(first.Scale < initial.Scale && first.Scale > target.Scale,
            "Growing Fit target did not ease between displayed and target scales.");
        Check(initial.Scale - first.Scale < (initial.Scale - target.Scale) * .1,
            "Growing Fit target stepped by ten percent or more on its first display frame.");
        for (var frame = 2; frame <= 90; frame++)
            controller.Advance(1 + frame / 60.0);
        Near(controller.Transform.Scale, target.Scale, .01);
        Near(controller.Transform.OffsetX, target.OffsetX, 2);
        Near(controller.Transform.OffsetY, target.OffsetY, 2);
        Near(GameplayViewportController.FitResponseTime, .22);
    }

    public static void TestFitUsesExactContentBoundsBeforeStorageTileChanges()
    {
        var controller = new GameplayViewportController();
        var size = new ViewportSize(800, 600);
        var initialBounds = new ViewportRect(0, 0, 200, 100);
        var onePixelGrowth = new ViewportRect(0, 0, 201, 100);
        var initial = controller.Configure(
            "Fit", size, initialBounds, null, null, 0);

        var unchanged = controller.Configure(
            "Fit", size, onePixelGrowth, null, null, 1);
        var target = FitTarget(size, onePixelGrowth);
        Check(target.Scale < initial.Scale,
            "One occupied-content pixel did not change the Fit target within one storage tile.");
        Equal(unchanged, initial);
        var first = controller.Advance(1 + 1.0 / 60);
        Check(first.Scale < initial.Scale && first.Scale > target.Scale,
            "Exact occupied bounds did not begin a smooth Fit follow.");
    }

    public static void TestTransitionInterruptsAtDisplayedTransform()
    {
        var controller = new GameplayViewportController();
        var size = new ViewportSize(100, 100);
        controller.SetFreeFlyTransform(ViewportTransform.Identity);
        controller.Configure("Fit", size,
            new ViewportRect(35, 35, 30, 30), null, null, 0,
            explicitRequest: true);
        var midpointTime = GameplayViewportController.TransitionDuration / 2;
        var midpoint = controller.Advance(midpointTime);
        Near(midpoint.Scale, 2);
        Near(midpoint.OffsetX, -50);
        Near(midpoint.OffsetY, -50);

        var interrupted = controller.Configure("Fit", size,
            new ViewportRect(5, 5, 90, 90), null, null, midpointTime,
            explicitRequest: true);
        Equal(interrupted, midpoint);
        Equal(controller.Advance(midpointTime), midpoint);
        Equal(controller.Advance(
            midpointTime + GameplayViewportController.TransitionDuration),
            ViewportTransform.Identity);
    }

    public static void TestFollowFrameCentersViewportWithSmallSurroundingMargin()
    {
        var controller = new GameplayViewportController();
        var size = new ViewportSize(1_280, 720);
        var liveFrame = new ViewportRect(20, 40, 640, 360);
        var focus = new ViewportPoint(liveFrame.CenterX, liveFrame.CenterY);

        var transform = controller.Configure(
            "Current", size, null, liveFrame, focus, 0);

        Near(transform.Scale, 1.76);
        Near(focus.X * transform.Scale + transform.OffsetX, 640);
        Near(focus.Y * transform.Scale + transform.OffsetY, 360);
    }

    public static void TestFeatureOverlayUsesSamePanAndZoomProjection()
    {
        var controller = new GameplayViewportController();
        controller.SetFreeFlyTransform(new(3, 5, 7));
        var marker = new ViewportPoint(10, 20);

        var projected = controller.Project(marker);
        Check(projected is not null, "The production viewport rejected a finite marker.");
        Near(projected!.Value.X, 35);
        Near(projected.Value.Y, 67);
    }
}
