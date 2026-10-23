using HollowKnightVision.Windows.Core;

internal static class GameStartupDetectorTests
{
    public static void TestProductionStabilityWindowRequiresEightFramesBeforeSelection()
    {
        var coordinator = new GameStartupCoordinator();
        var title = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: "Start Game");
        for (var frame = 0; frame < 7; frame++)
        {
            Check(coordinator.Observe(title).Action is null);
        }
        Check(coordinator.Observe(title).Action == GameStartupAction.SelectStartGame);
    }

    public static void TestCoordinatorMovesToStartGameBeforeSelecting()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 2);
        var options = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: "Options");
        Check(coordinator.Observe(options).Action is null);
        Check(coordinator.Observe(options).Action == GameStartupAction.MoveSelectionUp);
        Equal(GameStartupPhase.AwaitingTitle, coordinator.Phase);

        var start = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: "Start Game");
        for (var frame = 0; frame < 17; frame++)
        {
            Check(coordinator.Observe(start).Action is null);
        }
        Check(coordinator.Observe(start).Action == GameStartupAction.SelectStartGame);
    }

    public static void TestCoordinatorMovesToProfileOneBeforeSelecting()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 1);
        var slotTwo = new GameStartupEvidence(
            GameStartupScreen.ProfileOne,
            GameplayLikely: false,
            SelectedOption: "2.");
        Check(coordinator.Observe(slotTwo).Action == GameStartupAction.MoveSelectionUp);
        Equal(GameStartupPhase.AwaitingTitle, coordinator.Phase);

        var slotOne = new GameStartupEvidence(
            GameStartupScreen.ProfileOne,
            GameplayLikely: false,
            SelectedOption: "1.");
        for (var frame = 0; frame < 16; frame++)
        {
            Check(coordinator.Observe(slotOne).Action is null);
        }
        Check(coordinator.Observe(slotOne).Action == GameStartupAction.SelectProfileOne);
    }

    public static void TestCoordinatorWaitsForStencilSelectionBeforeSelecting()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 1);
        var unresolvedTitle = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: null);
        Check(coordinator.Observe(unresolvedTitle).Action is null);
        Check(coordinator.Observe(unresolvedTitle).Action is null);
        Equal(GameStartupPhase.AwaitingTitle, coordinator.Phase);

        var resolvedTitle = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: "Start Game");
        Check(coordinator.Observe(resolvedTitle).Action == GameStartupAction.SelectStartGame);
    }

    public static void TestCoordinatorSelectsBothMenusThenAdmitsGameplay()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 3);
        var title = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: "Start Game");
        Check(coordinator.Observe(title).Action is null);
        Check(coordinator.Observe(title).Action is null);
        Check(coordinator.Observe(title).Action == GameStartupAction.SelectStartGame);
        Check(coordinator.Observe(title).Action is null);

        var profile = new GameStartupEvidence(
            GameStartupScreen.ProfileOne,
            GameplayLikely: false,
            SelectedOption: "1.");
        Check(coordinator.Observe(profile).Action is null);
        Check(coordinator.Observe(profile).Action is null);
        Check(coordinator.Observe(profile).Action == GameStartupAction.SelectProfileOne);

        var gameplay = new GameStartupEvidence(Screen: null, GameplayLikely: true);
        Check(!coordinator.Observe(gameplay).AdmitsWorldFrames);
        Check(!coordinator.Observe(gameplay).AdmitsWorldFrames);
        Check(coordinator.Observe(gameplay).AdmitsWorldFrames);
    }

    public static void TestBackFromProfileReturnsToTitlePhase()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 2);
        var profile = new GameStartupEvidence(
            GameStartupScreen.ProfileOne,
            GameplayLikely: false,
            SelectedOption: "1.");
        Check(coordinator.Observe(profile).Action is null);
        Check(coordinator.Observe(profile).Action == GameStartupAction.SelectProfileOne);
        Equal(GameStartupPhase.AwaitingGameplay, coordinator.Phase);

        var title = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: "Start Game");
        Check(coordinator.Observe(title).Action is null);
        Equal(GameStartupPhase.AwaitingTitle, coordinator.Phase);
        Check(coordinator.Observe(title).Action == GameStartupAction.SelectStartGame);
    }

    public static void TestRejectedSelectionCanBeRetriedButNeverRepeatsWhileTransitioning()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 2);
        var title = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false,
            SelectedOption: "Start Game");
        Check(coordinator.Observe(title).Action is null);
        Check(coordinator.Observe(title).Action == GameStartupAction.SelectStartGame);
        Check(coordinator.Observe(title).Action is null);
        coordinator.Retry(GameStartupAction.SelectStartGame);
        Check(coordinator.Observe(title).Action is null);
        Check(coordinator.Observe(title).Action == GameStartupAction.SelectStartGame);
    }

    public static void TestGameplayGateClosesWhenTitleIsRecognized()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 1);
        var gameplay = new GameStartupEvidence(Screen: null, GameplayLikely: true);
        Check(coordinator.Observe(gameplay).AdmitsWorldFrames);

        var title = new GameStartupEvidence(
            GameStartupScreen.Title,
            GameplayLikely: false);
        Check(!coordinator.Observe(title).AdmitsWorldFrames);
        Check(coordinator.Phase != GameStartupPhase.Gameplay);
    }

    public static void TestCaptureEpochResetPreservesEstablishedGameplay()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 1);
        var gameplay = new GameStartupEvidence(Screen: null, GameplayLikely: true);
        Check(coordinator.Observe(gameplay).AdmitsWorldFrames);

        coordinator.Reset(preservingGameplay: true);

        Equal(GameStartupPhase.Gameplay, coordinator.Phase);
        Check(coordinator.Observe(
            new GameStartupEvidence(Screen: null, GameplayLikely: false)).AdmitsWorldFrames);
    }

    public static void TestConfirmedCheckpointRestoreReopensGameplayGate()
    {
        var coordinator = new GameStartupCoordinator(requiredStableFrames: 8);

        coordinator.RestoreGameplaySession();

        Equal(GameStartupPhase.Gameplay, coordinator.Phase);
        Check(coordinator.Observe(
            new GameStartupEvidence(Screen: null, GameplayLikely: false)).AdmitsWorldFrames);
    }

    public static void TestBrightHudArtEstablishesGameplay()
    {
        var image = MakeFrame(
            (0.06, 0.04, 0.06, 0.11),
            (0.12, 0.06, 0.10, 0.05));
        var evidence = new GameStartupDetector().Detect(image);
        Check(evidence.Screen is null);
        Check(evidence.GameplayLikely);
    }

    public static void TestHudPairWinsWhenGameplaySceneryResemblesTitle()
    {
        var image = MakeFrame(
            (0.06, 0.06, 0.05, 0.11),
            (0.13, 0.06, 0.11, 0.06),
            (0.412, 0.57, 0.032, 0.055),
            (0.46, 0.57, 0.088, 0.055),
            (0.568, 0.57, 0.032, 0.055));
        var evidence = new GameStartupDetector().Detect(image);
        Check(evidence.Screen is null);
        Check(evidence.GameplayLikely);
    }

    public static void TestGameplayNeedsBothHudElementsButNotKnight()
    {
        var hud = MakeFrame(
            (0.06, 0.06, 0.05, 0.11),
            (0.13, 0.06, 0.11, 0.06));
        Check(new GameStartupDetector().Detect(hud).GameplayLikely);

        var manaOnly = MakeFrame((0.06, 0.06, 0.05, 0.11));
        Check(!new GameStartupDetector().Detect(manaOnly).GameplayLikely);
    }

    private static BgraFrame MakeFrame(
        params (double X, double Y, double Width, double Height)[] brightRects)
    {
        const int width = 640;
        const int height = 360;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        foreach (var rect in brightRects)
        {
            var x0 = Math.Max(0, (int)Math.Floor(rect.X * width));
            var x1 = Math.Min(width, (int)Math.Ceiling((rect.X + rect.Width) * width));
            var y0 = Math.Max(0, (int)Math.Floor(rect.Y * height));
            var y1 = Math.Min(height, (int)Math.Ceiling((rect.Y + rect.Height) * height));
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var offset = (y * width + x) * BgraFrame.BytesPerPixel;
                    pixels[offset] = 245;
                    pixels[offset + 1] = 245;
                    pixels[offset + 2] = 245;
                    pixels[offset + 3] = 255;
                }
            }
        }
        return new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            pixels,
            captureId: 0,
            observedAt: DateTimeOffset.UnixEpoch);
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected {expected}; received {actual}.");
        }
    }

    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Game startup detector assertion failed.");
        }
    }
}
