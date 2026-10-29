using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;
using HostProgram = HollowKnightVision.Windows.Host.Program;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Plain offscreen WPF windows only: never instantiate MainWindow,
        // launch Hollow Knight, or touch the user's labels/atlas/game saves.
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        if (args.Contains("--input-only"))
        {
            TestBackgroundKeyDelivery();
            Console.WriteLine("Windows background-input regression passed: 1");
            Application.Current.Shutdown();
            return;
        }
        if (args.Contains("--menu-stencil-corpus"))
        {
            MenuStencilCorpusTests.Run();
            Application.Current.Shutdown();
            return;
        }
        if (args.Contains("--key-policy"))
        {
            TestGameControlForwarderAcceptsOnlyRequestedKeys();
            TestGameKeyRelayForwardsSupportedPressAndRelease();
            TestModelReviewNavigationAndExitAreReservedFromGameRelay();
            Console.WriteLine("Windows key-policy regressions passed: 3");
            Application.Current.Shutdown();
            return;
        }
        InputPathReplayIntegrationTests.Run();
        MenuParticleTests.Run();
        ControlledStepCaptureTests.Run();
        ReplayLifecycleTests.Run();
        TestWorldReplayRequiresExplicitReturningIntent();
        TestWorldReplayCreatesRequestedReport();
        TestWorldReplayBadArgumentsAndFailureExitCodes();
        TestWorldReplaySkipsNormalStartup();
        TestRecognizesTheRunningGameApplication();
        TestNormalizesCaseWhitespaceAndPunctuationForBothIdentifiers();
        TestRejectsBrowsersRegardlessOfGameNamedTabOrWindowTitle();
        TestRejectsAnUnrelatedApplication();
        TestCaptureWindowEligibilityDoesNotRequireGameFocusOrVisibility();
        TestCaptureWindowRejectsNonstandardWindowLayers();
        TestCaptureWindowRejectsStartupUtilityWindows();
        TestGameWindowSelectionPrefersGameplayWindowOverUnityHelper();
        TestGameWindowSelectionFallsBackToLargestWindow();
        TestGameControlForwarderAcceptsOnlyRequestedKeys();
        TestGameKeyRelayForwardsSupportedPressAndRelease();
        TestModelReviewNavigationAndExitAreReservedFromGameRelay();
        TestAutoNavigationUsesStencilEvidenceBeforeReceiverAndTracksGameplayLatch();
        TestAutoNavigationEmptyStoreFallbackAndProductionPulses();
        TestFraming();
        TestLabelingViewportGeometry();
        TestAddModifyAndDeleteAreSeparateModes();
        TestArrowSetCyclingWrapsThroughEverySet();
        TestArrowCyclingWrapsThroughContextObjects();
        TestOneCurrentOrSavedInstanceEnablesTrainingForSelectedObject();
        TestNegativeBoxDoesNotCountAsPositiveTrainingInstance();
        TestAutosaveStartsWithFirstBoxAndKeepsEmptySavedExampleEdits();
        TestRepeatedClassInstancesCanBeSelectedOrAddedIndependently();
        TestDraftKeepsMultipleRectanglesAndSelectsTopmostHit();
        TestObjectSelectionUsesClassInsteadOfOverlappingGeometry();
        TestDeleteAndUndoRestoreRectangleAndSelection();
        TestNegativeCorrectionConvertsTopmostPositiveBoxAndCanBeUndone();
        TestResizeCanBeUndone();
        TestMovingRectangleKeepsSizeAndClampsInsideImage();
        TestMoveCanBeUndone();
        TestTinyRectangleRollsBackWithoutLeavingUndoEntry();
        TestControlPointPixelsStayOutsideRectangleInnerArea();
        TestNegativeModeAndFreshProfileChoices();
        TestModeSelection();
        TestBottomControlScope();
        TestFramingGroupsAreMutuallyExclusive();
        TestHackerInteractionModes();
        TestWorkspaceShortcuts();
        TestPathRecordingAvailability();
        TestWorkspaceExitShortcut();
        TestGroundDebugAvailability();
        TestLaunchPlaceholderNamesGameWait();
        TestMainWindowHasProductTitle();
        TestGroundFeatureOverlayStartsVisible();
        TestGroundFeatureOverlayUsesMacReviewPalette();
        RuntimeModeTests.Run();
        HudStencilTests.Run();
        MenuIntegrationTests.Run();
        MenuStencilCorpusTests.Run();
        ObjectDetectionIntegrationTests.Run();
        RecentFramesTests.Run();
        LabelPauseIntegrationTests.Run();
        LabelSelectionIntegrationTests.Run();
        ObjectReferenceIntegrationTests.Run();
        ScreenshotReviewIntegrationTests.Run();
        ModelSummaryIntegrationTests.Run();
        TrainingIntegrationTests.Run();
        ExampleStoreIntegrationTests.Run();
        TestSolidCapture();
        GpuCaptureTests.TestMappedBounds();
        GpuCaptureTests.RunIfRequested();
        if (args.Contains("--input")) TestBackgroundKeyDelivery();
        Console.WriteLine($"Windows UI regressions passed: {(args.Contains("--input") ? 72 : 71)}");
        Application.Current.Shutdown();
    }

    private static void TestGameWindowSelectionPrefersGameplayWindowOverUnityHelper()
    {
        GameWindowCandidate[] candidates =
        [
            new("Window", 66, 20),
            new("Hollow Knight", 735, 838)
        ];

        Check(GameWindowSelection.BestIndex(candidates) == 1,
            "The titled Hollow Knight gameplay window did not outrank Unity's helper window.");
        Console.WriteLine("PASS game window selection prefers gameplay window over Unity helper");
    }

    private static void TestGameWindowSelectionFallsBackToLargestWindow()
    {
        GameWindowCandidate[] candidates =
        [
            new("", 320, 240),
            new("Game", 1280, 720)
        ];

        Check(GameWindowSelection.BestIndex(candidates) == 1,
            "Untitled game-window selection did not fall back to the largest client area.");
        Console.WriteLine("PASS game window selection falls back to largest window");
    }

    private static void TestGameControlForwarderAcceptsOnlyRequestedKeys()
    {
        var expectedReceiverKeys = new HashSet<int>
        {
            KeyInterop.VirtualKeyFromKey(Key.A),
            KeyInterop.VirtualKeyFromKey(Key.Z),
            KeyInterop.VirtualKeyFromKey(Key.X),
            KeyInterop.VirtualKeyFromKey(Key.I),
            KeyInterop.VirtualKeyFromKey(Key.P),
            KeyInterop.VirtualKeyFromKey(Key.Left),
            KeyInterop.VirtualKeyFromKey(Key.Right),
            KeyInterop.VirtualKeyFromKey(Key.Down),
            KeyInterop.VirtualKeyFromKey(Key.Up)
        };
        Check(GameControlKeyPolicy.ReceiverSupportedVirtualKeys.SetEquals(expectedReceiverKeys),
            "The mod receiver key policy did not expose exactly A/Z/X/I/P plus the four arrows.");
        Check(GameControlKeyPolicy.TryMapReceiverButton(Key.A, out var actionA)
                && actionA == InputButtons.ActionA
                && GameControlKeyPolicy.TryMapReceiverButton(Key.I, out var inventory)
                && inventory == InputButtons.Inventory
                && GameControlKeyPolicy.TryMapReceiverButton(Key.P, out var pause)
                && pause == InputButtons.PauseMenu,
            "The canonical receiver policy lost an action, inventory, or pause mapping.");
        Check(!GameControlKeyPolicy.TryMapReceiverButton(Key.Space, out _)
                && !GameControlKeyPolicy.TryMapReceiverButton(Key.B, out _),
            "The mod receiver accepted Space or an unrelated letter.");

        var normalBroadKeys = new[] { Key.A, Key.I, Key.P, Key.Space, Key.B };
        Check(normalBroadKeys.All(key =>
                NormalGameInputPolicy.IsGameKey(KeyInterop.VirtualKeyFromKey(key))),
            "The stock-game key policy was narrowed to the mod receiver's key set.");
        var visionShortcuts = new[] { Key.G, Key.L, Key.S, Key.M, Key.H, Key.O, Key.K };
        Check(visionShortcuts.All(key =>
                NormalGameInputPolicy.IsVisionShortcut(KeyInterop.VirtualKeyFromKey(key))
                && !NormalGameInputPolicy.IsGameKey(KeyInterop.VirtualKeyFromKey(key))),
            "A Vision workspace shortcut leaked into normal-game input.");
        Console.WriteLine("PASS testGameControlForwarderAcceptsOnlyRequestedKeys");
    }

    private static void TestGameKeyRelayForwardsSupportedPressAndRelease()
    {
        var key = KeyInterop.VirtualKeyFromKey(Key.A);
        var target = new NormalGameInputTarget((nint)0x1234, 42, 7);
        var handoff = new NormalGameHandoffState();

        Check(NormalGameInputPolicy.IsGameKey(key)
                && handoff.Press(key, target, isRepeat: false),
            "A supported first key-down did not acquire its normal-game target.");
        Check(handoff.Target == target && handoff.HeldKeys.SetEquals([key]),
            "The acquired normal-game key was not bound to its original target.");
        Check(!handoff.Press(key, target, isRepeat: true)
                && !handoff.Press(key, target, isRepeat: false),
            "A repeated or duplicate key-down acquired normal-game ownership twice.");
        Check(handoff.Release(key) && handoff.Target is null && handoff.HeldKeys.Count == 0,
            "Key-up did not drain the original normal-game target ownership.");

        Check(NormalGameInputPolicy.RelayAction(
                    key, down: true, canIntercept: true, modified: true)
                == NormalGameRelayAction.Pass
                && NormalGameInputPolicy.RelayAction(
                    key, down: false, canIntercept: true, modified: true)
                == NormalGameRelayAction.Pass,
            "A modified key transition was swallowed instead of preserving its OS down/up pair.");

        var suppressed = new NormalGameHandoffState();
        static bool TryAcquire(
            NormalGameHandoffState state,
            string workspace,
            bool settings,
            bool textInput,
            int virtualKey,
            NormalGameInputTarget inputTarget) =>
            NormalGameInputPolicy.AllowsGameInput(workspace, settings, textInput)
            && NormalGameInputPolicy.IsGameKey(virtualKey)
            && state.Press(virtualKey, inputTarget, isRepeat: false);

        Check(!TryAcquire(suppressed, "Model", false, false, key, target)
                && !TryAcquire(suppressed, "Gameplay", true, false, key, target)
                && !TryAcquire(suppressed, "Gameplay", false, true, key, target)
                && suppressed.Target is null
                && suppressed.HeldKeys.Count == 0,
            "A non-game workspace, settings overlay, or text input acquired injection state.");
        Console.WriteLine("PASS testGameKeyRelayForwardsSupportedPressAndRelease");
    }

    private static void TestModelReviewNavigationAndExitAreReservedFromGameRelay()
    {
        Key[] localKeys = [Key.X, Key.Left, Key.Right, Key.Down, Key.Up];
        var expectedLocalVirtualKeys = localKeys
            .Select(KeyInterop.VirtualKeyFromKey)
            .ToHashSet();
        Check(GameControlKeyPolicy.ModelReviewReservedVirtualKeys.SetEquals(
                expectedLocalVirtualKeys),
            "Model review did not reserve exactly X plus the four navigation arrows.");

        var target = new NormalGameInputTarget((nint)0x5678, 84, 3);
        var normalOwnership = new NormalGameHandoffState();
        foreach (var key in localKeys)
        {
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            Check(GameControlKeyPolicy.IsModelReviewReserved("Model", key),
                $"Model review did not reserve {key} locally.");
            Check(!GameControlKeyPolicy.TryMapReceiverButton("Model", key, out _),
                $"Model review {key} was routed to the mod receiver.");
            if (NormalGameInputPolicy.AllowsGameInput("Model", false, false)
                && NormalGameInputPolicy.IsGameKey(virtualKey))
            {
                normalOwnership.Press(virtualKey, target, isRepeat: false);
            }
            Check(!GameControlKeyPolicy.IsModelReviewReserved("Gameplay", key)
                    && GameControlKeyPolicy.TryMapReceiverButton("Gameplay", key, out _),
                $"Gameplay unexpectedly reserved or rejected receiver key {key}.");
        }
        Check(normalOwnership.Target is null && normalOwnership.HeldKeys.Count == 0,
            "Model review navigation acquired normal-game handoff ownership.");
        Console.WriteLine("PASS testModelReviewNavigationAndExitAreReservedFromGameRelay");
    }

    private static void TestWorldReplayRequiresExplicitReturningIntent()
    {
        WithWorldReplaySession(root =>
        {
            var oneWayPath = System.IO.Path.Combine(root, "one-way.json");
            var oneWayExit = HostProgram.RunAsync(
                [
                    "--world-replay-session", root,
                    "--world-replay-report", oneWayPath,
                    "--world-replay-repeats", "1"
                ],
                standardOutput: new StringWriter(),
                standardError: new StringWriter()).GetAwaiter().GetResult();
            var oneWay = ReadWorldReplayReport(oneWayPath);
            Check(oneWayExit == 0 && oneWay.Passed,
                "A valid one-way replay did not produce a successful Host exit.");
            Check(oneWay.RouteMode == LiveWorldReplayRouteMode.OneWay
                    && oneWay.Passes.All(pass => !pass.RecordingReturnsToStart
                        && !pass.ClosureRequired),
                "Host inferred returning route intent without the explicit returning flag.");

            var returningPath = System.IO.Path.Combine(root, "returning.json");
            var returningExit = HostProgram.RunAsync(
                [
                    "--world-replay-session", root,
                    "--world-replay-report", returningPath,
                    "--world-replay-repeats", "1",
                    "--world-replay-returning"
                ],
                standardOutput: new StringWriter(),
                standardError: new StringWriter()).GetAwaiter().GetResult();
            var returning = ReadWorldReplayReport(returningPath);
            Check(returningExit == 1 && !returning.Passed,
                "A returning replay without a closure did not propagate evaluator failure.");
            Check(returning.RouteMode == LiveWorldReplayRouteMode.Returning
                    && returning.Passes.All(pass => pass.RecordingReturnsToStart
                        && pass.ClosureRequired),
                "Host did not preserve explicit returning route intent.");
            Console.WriteLine("PASS world replay return topology requires the explicit returning flag");
        });
    }

    private static void TestWorldReplayCreatesRequestedReport()
    {
        WithWorldReplaySession(root =>
        {
            var reportPath = System.IO.Path.Combine(root, "reports", "host-report.json");
            var output = new StringWriter();
            var exitCode = HostProgram.RunAsync(
                [
                    "--world-replay-session", root,
                    "--world-replay-report", reportPath,
                    "--world-replay-repeats", "1",
                    "--world-replay-reopen"
                ],
                standardOutput: output,
                standardError: new StringWriter()).GetAwaiter().GetResult();

            Check(exitCode == 0, "Host rejected a successful offline world replay.");
            Check(File.Exists(reportPath), "Host did not honor the requested replay report path.");
            var report = ReadWorldReplayReport(reportPath);
            Check(report.Passed && report.RepeatCount == 1
                    && report.ReopenedSnapshotBetweenPasses,
                "The Host-created replay report lost evaluator options or result state.");
            Check(output.ToString().Contains("\"routeMode\": \"oneWay\"", StringComparison.Ordinal),
                "Host did not emit the deterministic replay result to standard output.");
            Console.WriteLine("PASS world replay dispatch creates the requested atomic report");
        });
    }

    private static void TestWorldReplayBadArgumentsAndFailureExitCodes()
    {
        var badError = new StringWriter();
        var badExit = HostProgram.RunAsync(
            ["--world-replay-repeats", "1"],
            standardOutput: new StringWriter(),
            standardError: badError).GetAwaiter().GetResult();
        Check(badExit == 64
                && badError.ToString().Contains(
                    "--world-replay-session",
                    StringComparison.Ordinal),
            "Invalid world-replay arguments did not produce the deterministic usage exit.");

        var failedExit = HostProgram.RunAsync(
            ["--world-replay-session", "injected"],
            replayRunner: _ => ReplayReport(passed: false),
            standardOutput: new StringWriter(),
            standardError: new StringWriter()).GetAwaiter().GetResult();
        Check(failedExit == 1,
            "A failed evaluator report did not produce the deterministic failure exit.");
        Console.WriteLine("PASS world replay dispatch distinguishes bad arguments and failed evaluation");
    }

    private static void TestWorldReplaySkipsNormalStartup()
    {
        var replayRuns = 0;
        var normalRuns = 0;
        var exitCode = HostProgram.RunAsync(
            ["--world-replay-session", "injected"],
            replayRunner: arguments =>
            {
                replayRuns++;
                Check(arguments.SequenceEqual(
                    new[] { "--world-replay-session", "injected" }),
                    "Host changed replay arguments before evaluator dispatch.");
                return ReplayReport(passed: true);
            },
            normalRunner: _ =>
            {
                normalRuns++;
                return Task.FromResult(99);
            },
            standardOutput: new StringWriter(),
            standardError: new StringWriter()).GetAwaiter().GetResult();

        Check(exitCode == 0 && replayRuns == 1 && normalRuns == 0,
            "World replay entered normal game capture/navigation startup.");
        Console.WriteLine("PASS world replay dispatch bypasses every normal startup side effect");
    }

    private static LiveWorldReplayReport ReplayReport(bool passed) => new(
        LiveWorldReplayReport.CurrentSchemaVersion,
        1,
        2,
        12,
        24,
        false,
        LiveWorldReplayRouteMode.OneWay,
        [],
        passed,
        passed ? [] : ["injected replay failure"]);

    private static LiveWorldReplayReport ReadWorldReplayReport(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        return JsonSerializer.Deserialize<LiveWorldReplayReport>(
            File.ReadAllBytes(path),
            options) ?? throw new InvalidOperationException("World replay report did not decode.");
    }

    private static void WithWorldReplaySession(Action<string> body)
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"hkv-host-world-replay-{Guid.NewGuid():N}");
        try
        {
            var pixels = new byte[320 * 180 * BgraFrame.BytesPerPixel];
            for (var offset = 3; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
                pixels[offset] = 255;
            var store = new SceneSessionStore(root);
            _ = store.Append(
                new BgraFrame(
                    320,
                    180,
                    320 * BgraFrame.BytesPerPixel,
                    pixels,
                    1,
                    DateTimeOffset.UnixEpoch),
                Guid.NewGuid(),
                Guid.NewGuid(),
                0,
                new AtlasPoint(0, 0),
                320,
                []);
            body(root);
        }
        finally
        {
            if (System.IO.Directory.Exists(root))
                System.IO.Directory.Delete(root, recursive: true);
        }
    }

    // Windows exposes the executable process image instead of macOS's
    // application-name/bundle-ID pair. EnumWindows unowned non-tool windows
    // are the platform equivalent of CoreGraphics layer-zero windows.
    private static void TestRecognizesTheRunningGameApplication()
    {
        Check(SourceWindowIdentity.IsHollowKnightProcess("Hollow Knight"),
            "The running Hollow Knight process was not recognized.");
    }

    private static void TestNormalizesCaseWhitespaceAndPunctuationForBothIdentifiers()
    {
        Check(SourceWindowIdentity.IsHollowKnightProcess("  HOLLOW---knight  "),
            "Process identity did not normalize case, whitespace, and punctuation.");
        Check(SourceWindowIdentity.IsHollowKnightProcess(" HÓLLOW＿ＫＮＩＧＨＴ "),
            "Process identity did not normalize diacritics and compatibility-width punctuation.");
    }

    private static void TestRejectsBrowsersRegardlessOfGameNamedTabOrWindowTitle()
    {
        Check(!HollowKnightWindowLocator.IsHollowKnight("Safari", "Hollow Knight"),
            "Safari was accepted because of its window title.");
        Check(!HollowKnightWindowLocator.IsHollowKnight("Google Chrome", "Hollow Knight"),
            "Chrome was accepted because of its window title.");
    }

    private static void TestRejectsAnUnrelatedApplication()
    {
        Check(!HollowKnightWindowLocator.IsHollowKnight("Steam", "Hollow Knight"),
            "An unrelated process was accepted because of its title.");
    }

    private static void TestCaptureWindowEligibilityDoesNotRequireGameFocusOrVisibility()
    {
        Check(SourceWindowIdentity.IsCaptureWindow(
                "Hollow Knight", 101, 202, isStandardTopLevel: true, 1470, 923),
            "An occlusion-independent game window was rejected.");
        Check(!SourceWindowIdentity.IsCaptureWindow(
                "Hollow Knight", 202, 202, isStandardTopLevel: true, 1470, 923),
            "Vision accepted its own process as a capture source.");
    }

    private static void TestCaptureWindowRejectsNonstandardWindowLayers()
    {
        Check(!SourceWindowIdentity.IsCaptureWindow(
                "Hollow Knight", 101, 202, isStandardTopLevel: false, 1470, 923),
            "An owned/tool window was accepted as a standard capture layer.");
    }

    private static void TestCaptureWindowRejectsStartupUtilityWindows()
    {
        foreach (var (width, height) in new[] { (1470d, 33d), (1470d, 44d), (64d, 64d) })
        {
            Check(!SourceWindowIdentity.IsCaptureWindow(
                    "Hollow Knight", 101, 202, isStandardTopLevel: true, width, height),
                $"Startup utility window {width}×{height} was accepted.");
        }
        Check(!SourceWindowIdentity.IsCaptureWindow(
                "Hollow Knight", 101, 202, isStandardTopLevel: true, double.NaN, 923),
            "A nonfinite window width was accepted.");
        Check(!SourceWindowIdentity.IsCaptureWindow(
                "Hollow Knight", 101, 202, isStandardTopLevel: true, 1470, double.PositiveInfinity),
            "A nonfinite window height was accepted.");
    }

    private static void TestAutoNavigationUsesStencilEvidenceBeforeReceiverAndTracksGameplayLatch()
    {
        static MenuStencilResult Menu(string context, string selected) => new(
            context,
            IsMatch: true,
            Confidence: .95,
            selected,
            Anchors: [],
            SelectorCandidates: [],
            SelectorSearchRegions: [],
            SelectorLanguageEvidenceCount: 2,
            SelectorForegroundEvidenceCount: 48,
            SceneEvidenceRatio: .9,
            MenuStencilPhase.Tracking,
            ComparisonCount: 1,
            SourceTimestamp: 0,
            LanguageIdentifier: "en");
        static HudStencilResult Hud() => new(
            Health:
            [
                new(0, "full", new StencilRect(10, 10, 10, 10), .95),
                new(1, "full", new StencilRect(24, 10, 10, 10), .95)
            ],
            ManaMain: null,
            ManaReserves: [],
            HealthMasks: [],
            ManaMasks: [],
            Geo: default,
            HoldsHealthMask: false,
            SourceTimestamp: 0);

        var provider = new SequenceStartupStencilProvider(
        [
            new(Menu("main-title", "Start Game"), null),
            new(Menu("select-profile", "1."), null),
            new(null, null),
            new(null, Hud()),
            new(Menu("main-title", "Start Game"), null),
            new(null, null),
            new(null, null)
        ]);
        var startup = new AutoNavigationRunner.StartupEvidenceFlow(
            provider,
            requiredStableFrames: 1);
        var frame = new BgraFrame(
            640,
            360,
            640 * BgraFrame.BytesPerPixel,
            new byte[640 * 360 * BgraFrame.BytesPerPixel],
            captureId: 1,
            DateTimeOffset.UnixEpoch);
        var noReceiver = AutoNavigationRunner.StartupReceiverEvidence.None;
        var firstReceiver = new AutoNavigationRunner.StartupReceiverEvidence(
            Generation: 1,
            ObservedAt: 1,
            GameplayLikely: true);
        var newerReceiver = new AutoNavigationRunner.StartupReceiverEvidence(
            Generation: 2,
            ObservedAt: 2,
            GameplayLikely: true);

        var title = startup.Observe(frame, noReceiver);
        Check(title.Decision.Action == GameStartupAction.SelectStartGame,
            "Menu stencil did not navigate before receiver gameplay telemetry.");
        var profile = startup.Observe(frame, noReceiver);
        Check(profile.Decision.Action == GameStartupAction.SelectProfileOne,
            "Profile stencil did not navigate before receiver gameplay telemetry.");
        var receiverGameplay = startup.Observe(frame, firstReceiver);
        Check(receiverGameplay.Decision.Phase == GameStartupPhase.Gameplay,
            "Receiver telemetry did not establish gameplay.");
        var latchedHud = startup.Observe(frame, noReceiver);
        Check(latchedHud.Decision.Phase == GameStartupPhase.Gameplay
                && !latchedHud.Evidence.GameplayLikely,
            "Gameplay latch was not derived from coordinator state.");
        var returnedTitle = startup.Observe(frame, firstReceiver);
        Check(returnedTitle.Evidence.Screen == GameStartupScreen.Title
                && returnedTitle.Decision.Action == GameStartupAction.SelectStartGame
                && returnedTitle.Decision.Phase == GameStartupPhase.AwaitingProfile,
            "Recognized menu did not close gameplay ahead of stale receiver state.");
        startup.NavigationActionCompleted(firstReceiver);
        var staleTransition = startup.Observe(frame, firstReceiver);
        Check(!staleTransition.Evidence.GameplayLikely
                && staleTransition.Decision.Phase == GameStartupPhase.AwaitingProfile,
            "Blank transition reused receiver gameplay evidence from before the menu/action.");
        var reopened = startup.Observe(frame, newerReceiver);
        Check(reopened.Evidence.GameplayLikely
                && reopened.Decision.Phase == GameStartupPhase.Gameplay,
            "Newer receiver evidence did not reopen gameplay after the menu closed the latch.");
        Check(provider.ObservationCount == 7,
            "Startup flow did not run live stencil recognition for every frame.");
        Console.WriteLine("PASS startup menus invalidate stale receiver evidence and newer telemetry reopens gameplay");
    }

    private static void TestAutoNavigationEmptyStoreFallbackAndProductionPulses()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"hkv-startup-empty-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var anchors = new[]
            {
                (289, 209, 61, 9),
                (297, 231, 45, 8),
                (283, 254, 75, 7)
            };
            var start = StartupStructuredFrame(
                [.. anchors, (267, 201, 14, 11), (359, 202, 13, 11)]);
            var profile = StartupStructuredFrame(
                (64, 103, 22, 20),
                (109, 103, 16, 20),
                (432, 103, 22, 20));
            var blank = StartupStructuredFrame();
            var flow = AutoNavigationRunner.StartupEvidenceFlow.CreateDefault(root);
            Check(flow.Diagnostic.Contains("menu-scenes=0", StringComparison.Ordinal)
                    && flow.Diagnostic.Contains("title-fallback=geometry", StringComparison.Ordinal),
                "Empty default store unexpectedly supplied a user menu scene.");
            var staleReceiver = new AutoNavigationRunner.StartupReceiverEvidence(
                Generation: 1,
                ObservedAt: 1,
                GameplayLikely: true);
            for (var index = 0; index < 7; index++)
            {
                var pending = flow.Observe(start, staleReceiver);
                Check(pending.Evidence.Screen == GameStartupScreen.Title
                        && pending.Evidence.SelectedOption == "Start Game"
                        && pending.Decision.Action is null,
                    "Empty-store title fallback bypassed production stability.");
            }
            var title = flow.Observe(start, staleReceiver);
            Check(title.Decision.Action == GameStartupAction.SelectStartGame,
                "Empty-store title fallback did not select Start Game after eight frames.");
            flow.NavigationActionCompleted(staleReceiver);

            for (var index = 0; index < 7; index++)
            {
                var pending = flow.Observe(profile, staleReceiver);
                Check(pending.Evidence.Screen == GameStartupScreen.ProfileOne
                        && pending.Evidence.SelectedOption == "1."
                        && pending.Decision.Action is null,
                    "Empty-store profile fallback bypassed production stability.");
            }
            var selectedProfile = flow.Observe(profile, staleReceiver);
            Check(selectedProfile.Decision.Action == GameStartupAction.SelectProfileOne,
                "Empty-store profile fallback did not select profile one after eight frames.");
            flow.NavigationActionCompleted(staleReceiver);

            var staleTransition = flow.Observe(blank, staleReceiver);
            Check(!staleTransition.Evidence.GameplayLikely
                    && staleTransition.Decision.Phase == GameStartupPhase.AwaitingGameplay,
                "Empty-store navigation reused stale receiver gameplay evidence.");
            var newerReceiver = new AutoNavigationRunner.StartupReceiverEvidence(
                Generation: 2,
                ObservedAt: 2,
                GameplayLikely: true);
            for (var index = 0; index < 7; index++)
            {
                var pending = flow.Observe(blank, newerReceiver);
                Check(pending.Decision.Phase == GameStartupPhase.AwaitingGameplay,
                    "New gameplay evidence bypassed production stability.");
            }
            var gameplay = flow.Observe(blank, newerReceiver);
            Check(gameplay.Evidence.GameplayLikely
                    && gameplay.Decision.Phase == GameStartupPhase.Gameplay,
                "Newer receiver evidence did not relatch gameplay after empty-store navigation.");

            var pulses = new List<(InputButtons Button, TimeSpan Duration)>();
            Task Pulse(InputButtons button, TimeSpan duration, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pulses.Add((button, duration));
                return Task.CompletedTask;
            }
            AutoNavigationRunner.PulseNavigationActionAsync(
                    title.Decision.Action!.Value,
                    Pulse)
                .GetAwaiter().GetResult();
            AutoNavigationRunner.PulseNavigationActionAsync(
                    selectedProfile.Decision.Action!.Value,
                    Pulse)
                .GetAwaiter().GetResult();
            Check(pulses.SequenceEqual(new[]
                {
                    (InputButtons.ActionZ, TimeSpan.FromMilliseconds(85)),
                    (InputButtons.ActionZ, TimeSpan.FromMilliseconds(85))
                }),
                "Production action routing did not emit title/profile Z pulses.");

            var ambiguous = StartupStructuredFrame(
                [.. anchors,
                    (267, 201, 14, 11), (359, 202, 13, 11),
                    (276, 224, 12, 10), (351, 223, 14, 11)]);
            var lookalike = StartupStructuredFrame(
                (267, 201, 14, 11),
                (359, 202, 13, 11));
            var profileFillLookalike = StartupFilledFrame(
                (48, 89, 48, 45),
                (94, 89, 49, 45),
                (411, 89, 55, 45));
            var fullWhite = StartupUniformFrame(245);
            var brightFade = StartupUniformFrame(180);
            foreach (var rejected in new[]
                     {
                         (Name: "ambiguous title", Frame: ambiguous),
                         (Name: "selector-only", Frame: lookalike),
                         (Name: "full white", Frame: fullWhite),
                         (Name: "bright fade", Frame: brightFade)
                     })
            {
                var rejectionFlow = AutoNavigationRunner.StartupEvidenceFlow.CreateDefault(root);
                for (var frameIndex = 0; frameIndex < 8; frameIndex++)
                {
                    var observation = rejectionFlow.Observe(
                        rejected.Frame,
                        AutoNavigationRunner.StartupReceiverEvidence.None);
                    Check(observation.Evidence.Screen is null
                            && !observation.Evidence.GameplayLikely
                            && observation.Decision.Action is null
                            && observation.Decision.Phase != GameStartupPhase.Gameplay,
                        $"Bright fallback accepted {rejected.Name} pixels at frame {frameIndex + 1}: screen={observation.Evidence.Screen}, gameplay={observation.Evidence.GameplayLikely}, phase={observation.Decision.Phase}, action={observation.Decision.Action}.");
                }
            }

            var denseProfileCoordinator = new GameStartupCoordinator();
            for (var frameIndex = 0; frameIndex < 8; frameIndex++)
            {
                var evidence = new GameStartupDetector().Detect(profileFillLookalike);
                var decision = denseProfileCoordinator.Observe(evidence);
                Check(evidence.Screen is null
                        && !evidence.GameplayLikely
                        && decision.Action is null
                        && decision.Phase != GameStartupPhase.Gameplay,
                    $"Coarse detector accepted dense profile pixels at frame {frameIndex + 1}.");
            }

            var catalogRoot = System.IO.Path.Combine(root, "nonempty");
            SaveStartupMenuCatalog(catalogRoot);
            var catalogFlow = AutoNavigationRunner.StartupEvidenceFlow.CreateDefault(
                catalogRoot,
                requiredStableFrames: 1);
            Check(catalogFlow.Diagnostic.Contains("menu-scenes=1", StringComparison.Ordinal)
                    && catalogFlow.Diagnostic.Contains("title-fallback=disabled", StringComparison.Ordinal),
                "A usable saved catalog did not disable geometry fallback.");
            var catalogMiss = catalogFlow.Observe(
                start,
                AutoNavigationRunner.StartupReceiverEvidence.None);
            Check(catalogMiss.Evidence.Screen is null
                    && catalogMiss.Decision.Action is null,
                "Geometry fallback ran despite a nonempty saved menu catalog.");
            Console.WriteLine("PASS empty-store title/profile fallback is stable, scoped, unambiguous and uses production Z pulses");
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    private static BgraFrame StartupStructuredFrame(
        params (int X, int Y, int Width, int Height)[] brightRects)
    {
        const int width = 640;
        const int height = 360;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        for (var offset = 3; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
            pixels[offset] = 255;
        foreach (var rectangle in brightRects)
        {
            var middleX = rectangle.X + rectangle.Width / 2;
            var middleY = rectangle.Y + rectangle.Height / 2;
            for (var x = rectangle.X; x < rectangle.X + rectangle.Width; x += 2)
            {
                var offset = (middleY * width + x) * BgraFrame.BytesPerPixel;
                pixels[offset] = 245;
                pixels[offset + 1] = 245;
                pixels[offset + 2] = 245;
            }
            for (var y = rectangle.Y; y < rectangle.Y + rectangle.Height; y += 2)
            {
                var offset = (y * width + middleX) * BgraFrame.BytesPerPixel;
                pixels[offset] = 245;
                pixels[offset + 1] = 245;
                pixels[offset + 2] = 245;
            }
        }
        return new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            pixels,
            captureId: 1,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    private static BgraFrame StartupUniformFrame(byte value)
    {
        const int width = 640;
        const int height = 360;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        for (var offset = 0; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            pixels,
            captureId: 1,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    private static BgraFrame StartupFilledFrame(
        params (int X, int Y, int Width, int Height)[] brightRects)
    {
        const int width = 640;
        const int height = 360;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        for (var offset = 3; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
            pixels[offset] = 255;
        foreach (var rectangle in brightRects)
        {
            for (var y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
            {
                for (var x = rectangle.X; x < rectangle.X + rectangle.Width; x++)
                {
                    var offset = (y * width + x) * BgraFrame.BytesPerPixel;
                    pixels[offset] = 245;
                    pixels[offset + 1] = 245;
                    pixels[offset + 2] = 245;
                }
            }
        }
        return new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            pixels,
            captureId: 1,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    private static void SaveStartupMenuCatalog(string supportRoot)
    {
        var examples = System.IO.Path.Combine(supportRoot, "labeling-v1", "examples");
        var pixels = new byte[640 * 360 * BgraFrame.BytesPerPixel];
        for (var pixel = 0; pixel < 640 * 360; pixel++)
        {
            pixels[pixel * 4] = 62;
            pixels[pixel * 4 + 1] = 24;
            pixels[pixel * 4 + 2] = 12;
            pixels[pixel * 4 + 3] = 255;
        }
        var annotations = new List<LabelingExampleAnnotation>();
        void Paint(string identifier, int x, int y, int width, int height, int seed)
        {
            annotations.Add(new(
                Guid.NewGuid(), identifier,
                x / 640d, y / 360d, width / 640d, height / 360d));
            for (var dy = 0; dy < height; dy++)
            for (var dx = 0; dx < width; dx++)
            {
                var offset = ((y + dy) * 640 + x + dx) * BgraFrame.BytesPerPixel;
                var level = (byte)(140 + (dx * 17 + dy * 31 + seed * 23) % 114);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = level;
            }
        }

        var labels = LabelingContractCatalog.ScreenContexts
            .Single(item => item.Identifier == "main-title").Labels;
        var row = 0;
        foreach (var label in labels)
        {
            if (label.Identifier.EndsWith("hollow-knight-logo", StringComparison.Ordinal))
                Paint(label.Identifier, 100, 10, 440, 60, 8);
            else if (label.Identifier != "shared.select-decoration")
                Paint(label.Identifier, 260, 130 + row++ * 30, 120, 8, row);
        }
        Paint("shared.select-decoration", 240, 130, 8, 8, 17);
        Paint("shared.select-decoration", 392, 130, 8, 8, 21);
        var frame = new BgraFrame(
            640,
            360,
            640 * BgraFrame.BytesPerPixel,
            pixels,
            captureId: 1,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var saved = new LabelingExampleStore(examples).Save(
            frame,
            "main-title",
            annotations,
            languageIdentifier: "en");
        Check(LabelingContractEvaluator.AcceptsMenuExample(saved),
            "Startup catalog fixture violates the production menu contract.");
        new MenuStencilCalibration(1, 640, 360, DateTimeOffset.UnixEpoch, [])
            .Write(MenuStencilCalibration.WritablePath(examples));
    }

    private sealed class SequenceStartupStencilProvider(
        IEnumerable<AutoNavigationRunner.StartupStencilEvidence> evidence)
        : AutoNavigationRunner.IStartupStencilEvidenceProvider
    {
        private readonly Queue<AutoNavigationRunner.StartupStencilEvidence> remaining = new(evidence);
        internal int ObservationCount { get; private set; }

        public AutoNavigationRunner.StartupStencilEvidence Observe(BgraFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ObservationCount++;
            return remaining.Count > 0
                ? remaining.Dequeue()
                : throw new InvalidOperationException("Unexpected startup stencil observation.");
        }
    }

    private static Window Offscreen(UIElement content, double width = 960, double height = 600) => new()
    {
        Content = content, Width = width, Height = height, Left = -20000, Top = -20000,
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Black
    };

    private static void TestModeSelection()
    {
        var xml = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = xml.Descendants(ns + "Style").Single(item => (string?)item.Attribute(x + "Key") == "Segment");
        var style = (Style)System.Windows.Markup.XamlReader.Parse(source.ToString());
        foreach (var group in new[] { "GameplayFraming", "HackerFraming", "HackerSettingsMode", "LabelMode", "ModelTab" })
        {
            var controls = xml.Descendants(ns + "RadioButton").Where(item => (string?)item.Attribute("GroupName") == group).ToArray();
            Check(controls.Length >= 3, $"Missing segmented mode group {group}.");
            Check(controls.All(item => (string?)item.Attribute("Style") == "{StaticResource Segment}"), "Modes use different selected styles.");
            Check(controls.All(item => (string?)item.Attribute("Checked") == (group == "LabelMode" ? "LabelMode_Checked" : group == "ModelTab" ? "ModelTab_Checked" : "CameraMode_Checked")),
                "Keyboard/automation selection changes highlight without changing behavior.");
        }
        var panel = new StackPanel();
        foreach (var name in new[] { "Free Fly", "Fit", "Current" })
            panel.Children.Add(new RadioButton { Content = name, Tag = name, GroupName = "FramingTest", Style = style });
        var window = Offscreen(panel);
        window.Show(); Pump();
        try
        {
            var select = typeof(MainWindow).GetMethod("SelectModeButtons", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var mode in new[] { "Current", "Fit", "Free Fly", "Current" })
            {
                select.Invoke(null, [panel, mode]); Pump();
                foreach (var button in panel.Children.OfType<RadioButton>())
                {
                    var selected = (string)button.Tag == mode;
                    Check(button.IsChecked == selected, "Selected control drifted from actual mode.");
                    var border = (Border)button.Template.FindName("SegmentBorder", button);
                    Check(((SolidColorBrush)border.Background).Color == (Color)ColorConverter.ConvertFromString(selected ? "#245B86" : "#202124"), "Selected mode has no distinct highlight.");
                }
            }
            var triggers = panel.Children.OfType<RadioButton>().First().Template.Triggers.OfType<Trigger>().ToList();
            Check(triggers.FindIndex(t => t.Property == System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)
                > triggers.FindIndex(t => t.Property == UIElement.IsMouseOverProperty), "Hover hides selection highlight.");
            Console.WriteLine("PASS Camera and label modes share a persistent selected highlight");
        }
        finally { window.Close(); }
    }

    private static void TestBottomControlScope()
    {
        var workspaces = DashboardValues("Workspaces");
        Check(DashboardValues("BottomWorkspaces").SequenceEqual(
            new[] { "Gameplay", "Label", "Stencil", "Model", "Hacker" }),
            "Bottom controls exposed Ops or omitted a dashboard workspace.");
        foreach (var workspace in workspaces)
        foreach (var complete in new[] { false, true })
        foreach (var context in new string?[] { null, "Main Title", "Gameplay" })
        {
            var expected = context == "Gameplay"
                && (workspace == "Hacker" || workspace == "Gameplay" && complete);
            Check(Dashboard<bool>("ShowsFraming", workspace, complete, context) == expected,
                $"Framing scope drifted for {workspace}/{complete}/{context ?? "none"}.");
        }
        foreach (var workspace in workspaces)
        foreach (var mode in new[] { GameRuntimeMode.Normal, GameRuntimeMode.Modded })
        foreach (var context in new string?[] { null, "Main Title", "Gameplay" })
        {
            var expected = workspace == "Gameplay" && mode == GameRuntimeMode.Modded
                && context == "Gameplay";
            Check(Dashboard<bool>("ShowsOps", workspace, mode, context) == expected,
                $"Ops scope drifted for {workspace}/{mode}/{context ?? "none"}.");
        }
        Console.WriteLine("PASS dashboard bottom controls are scoped to gameplay and modded state");
    }

    private static void TestFramingGroupsAreMutuallyExclusive()
    {
        foreach (var workspace in DashboardValues("Workspaces"))
        foreach (var complete in new[] { false, true })
        foreach (var context in new string?[] { null, "Main Title", "Gameplay" })
        {
            var gameplay = Dashboard<bool>(
                "ShowsGameplayFraming", workspace, complete, context);
            var hacker = Dashboard<bool>(
                "ShowsHackerFraming", workspace, context);
            Check(!(gameplay && hacker),
                $"Gameplay and Hacker framing both appeared for {workspace}/{complete}/{context ?? "none"}.");
            Check(gameplay == (workspace == "Gameplay" && complete && context == "Gameplay"),
                "Gameplay framing escaped its workspace or setup gate.");
            Check(hacker == (workspace == "Hacker" && context == "Gameplay"),
                "Hacker framing escaped its workspace.");
        }
        Console.WriteLine("PASS Gameplay and Hacker framing groups are mutually exclusive");
    }

    private static void TestHackerInteractionModes()
    {
        var expected = new[] { "Ground", "Rooms", "Fit", "Free Fly", "Current" };
        Check(DashboardValues("HackerInteractionModes").SequenceEqual(expected),
            "Hacker interaction policy order drifted.");
        var xml = MainWindowSource();
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var controls = xml.Descendants(ns + "RadioButton")
            .Where(item => (string?)item.Attribute("GroupName") == "HackerSettingsMode")
            .Select(item => (string?)item.Attribute("Content")).ToArray();
        Check(controls.SequenceEqual(expected), "Hacker settings do not use the canonical mode order.");
        foreach (var mode in expected)
        {
            var expectedFraming = mode is "Fit" or "Current" ? mode : "Free Fly";
            Check(Dashboard<string>("HackerFramingMode", mode) == expectedFraming,
                $"Hacker {mode} did not map to {expectedFraming} framing.");
        }
        Console.WriteLine("PASS Hacker interactions form one ordered framing group");
    }

    private static void TestWorkspaceShortcuts()
    {
        var expected = new[] { "Gameplay", "Label", "Stencil", "Model", "Hacker", "Ops" };
        Check(DashboardValues("Workspaces").SequenceEqual(expected),
            "Workspace policy order drifted.");
        var xml = MainWindowSource();
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var controls = xml.Descendants(ns + "RadioButton")
            .Where(item => (string?)item.Attribute("GroupName") == "Workspace")
            .Select(item => (string?)item.Attribute("Content")).ToArray();
        Check(controls.SequenceEqual(expected), "Workspace controls do not use canonical order.");
        var shortcuts = new[]
        {
            (Key.G, "Gameplay"), (Key.L, "Label"), (Key.S, "Stencil"),
            (Key.M, "Model"), (Key.H, "Hacker"), (Key.O, "Ops")
        };
        foreach (var (key, workspace) in shortcuts)
            Check(Dashboard<string?>("WorkspaceForShortcut", key) == workspace,
                $"{key} did not select {workspace}.");
        Check(Dashboard<string?>("WorkspaceForShortcut", Key.X) is null,
            "X incorrectly became a destination shortcut instead of an exit command.");
        Console.WriteLine("PASS dashboard workspaces use G/L/S/M/H/O with separate X exit");
    }

    private static void TestPathRecordingAvailability()
    {
        bool Shows(string workspace, GameRuntimeMode mode, string? context) =>
            Dashboard<bool>("ShowsPathRecording", workspace, mode, context);
        Check(Shows("Gameplay", GameRuntimeMode.Modded, "Gameplay"),
            "Gameplay recording was hidden during gameplay.");
        Check(!Shows("Gameplay", GameRuntimeMode.Modded, "Main Title")
              && !Shows("Gameplay", GameRuntimeMode.Modded, null),
            "Gameplay recording remained available in a menu or unknown state.");
        Check(Shows("Hacker", GameRuntimeMode.Modded, null),
            "Hacker recording incorrectly required a recognized context.");
        Check(!Shows("Ops", GameRuntimeMode.Modded, "Gameplay")
              && !Shows("Gameplay", GameRuntimeMode.Normal, "Gameplay")
              && !Shows("Hacker", GameRuntimeMode.Normal, null),
            "Recording escaped its Gameplay/Hacker modded scope.");
        Console.WriteLine("PASS K path recording is available only in valid modded contexts");
    }

    private static void TestWorkspaceExitShortcut()
    {
        bool Exit(string workspace, Key key = Key.X, bool down = true,
            bool repeat = false, ModifierKeys modifiers = ModifierKeys.None) =>
            Dashboard<bool>("ShouldExitWorkspace", workspace, key, down, repeat, modifiers);
        foreach (var workspace in new[] { "Label", "Model", "Stencil" })
            Check(Exit(workspace), $"X did not exit {workspace}.");
        foreach (var workspace in new[] { "Gameplay", "Hacker", "Ops" })
            Check(!Exit(workspace), $"X incorrectly exited {workspace}.");
        Check(!Exit("Label", down: false) && !Exit("Model", repeat: true)
              && !Exit("Label", key: Key.Z),
            "Exit accepted key-up, repeat, or a non-X key.");
        foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Windows })
            Check(!Exit("Model", modifiers: modifiers), $"Modified X ({modifiers}) exited Model.");
        Check(Exit("Label", modifiers: ModifierKeys.Shift), "Shift+X was incorrectly treated as an OS shortcut.");
        Console.WriteLine("PASS X exit is scoped to unmodified nonrepeat editor presses");
    }

    private static void TestGroundDebugAvailability()
    {
        Check(Dashboard<bool>("ShowsGroundTrackingTools", "Gameplay", "Gameplay"),
            "Ground debug tools were unavailable during gameplay.");
        Check(!Dashboard<bool>("ShowsGroundTrackingTools", "Gameplay", "Main Title")
              && !Dashboard<bool>("ShowsGroundTrackingTools", "Gameplay", null)
              && !Dashboard<bool>("ShowsGroundTrackingTools", "Label", "Gameplay")
              && !Dashboard<bool>("ShowsGroundTrackingTools", "Hacker", "Gameplay"),
            "Ground debug tools escaped Gameplay-workspace gameplay scope.");
        Console.WriteLine("PASS ground debug tools require gameplay inside Gameplay workspace");
    }

    private static System.Xml.Linq.XDocument MainWindowSource() =>
        System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(
            AppContext.BaseDirectory, "MainWindow.source.xaml"));

    private static Type DashboardPolicyType => typeof(MainWindow).Assembly.GetType(
        "HollowKnightVision.Windows.App.DashboardPresentationPolicy", throwOnError: true)!;

    private static string[] DashboardValues(string property) =>
        ((IEnumerable<string>)DashboardPolicyType.GetProperty(
            property, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).ToArray();

    private static T Dashboard<T>(string method, params object?[] arguments) =>
        (T)DashboardPolicyType.GetMethod(
            method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void TestFraming()
    {
        var assembly = typeof(MainWindow).Assembly;
        var controllerType = assembly.GetType(
            "HollowKnightVision.Windows.App.GameplayViewportController", throwOnError: true)!;
        var sizeType = assembly.GetType(
            "HollowKnightVision.Windows.App.ViewportSize", throwOnError: true)!;
        var rectType = assembly.GetType(
            "HollowKnightVision.Windows.App.ViewportRect", throwOnError: true)!;
        var pointType = assembly.GetType(
            "HollowKnightVision.Windows.App.ViewportPoint", throwOnError: true)!;
        var transformType = assembly.GetType(
            "HollowKnightVision.Windows.App.ViewportTransform", throwOnError: true)!;
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        object Value(Type type, params object[] values) => Activator.CreateInstance(
            type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: values, culture: null)!;
        object Controller() => Activator.CreateInstance(controllerType, nonPublic: true)!;
        object Call(object target, string method, params object?[] values) =>
            controllerType.GetMethod(method, members)!.Invoke(target, values)!;
        var apply = typeof(MainWindow).GetMethod(
            "ApplyViewportTransform", BindingFlags.NonPublic | BindingFlags.Static)!;
        var surface = new Canvas();
        Matrix MatrixFor(object transform)
        {
            apply.Invoke(null, [surface, transform]);
            return ((MatrixTransform)surface.RenderTransform).Matrix;
        }

        var viewport = Value(sizeType, 800d, 600d);
        var atlas = Value(rectType, 0d, 0d, 3000d, 1800d);
        var fitController = Controller();
        var fit = MatrixFor(Call(fitController, "Configure",
            "Fit", viewport, atlas, null, null, 0d, false));
        Near(.24, fit.M11); Near(40, fit.OffsetX); Near(84, fit.OffsetY);

        var freeFly = Value(transformType, .4d, 123d, 87d);
        MatrixFor(Call(fitController, "SetFreeFlyTransform", freeFly));
        var panned = ((MatrixTransform)surface.RenderTransform).Matrix;
        Near(.4, panned.M11); Near(123, panned.OffsetX); Near(87, panned.OffsetY);
        var shifted = MatrixFor(Call(fitController, "Rebase", -256d, -512d));
        var oldPoint = panned.Transform(new Point(100, 200));
        var newPoint = shifted.Transform(new Point(356, 712));
        Near(oldPoint.X, newPoint.X); Near(oldPoint.Y, newPoint.Y);

        var currentController = Controller();
        var live = Value(rectType, 800d, 500d, 640d, 360d);
        var focus = Value(pointType, 1120d, 680d);
        var currentViewport = Value(sizeType, 960d, 600d);
        var current = MatrixFor(Call(currentController, "Configure",
            "Current", currentViewport, null, live, focus, 0d, false));
        Near(1.32, current.M11);
        Near(480, current.Transform(new Point(1120, 680)).X);
        Near(300, current.Transform(new Point(1120, 680)).Y);
        var zoomed = MatrixFor(Call(currentController, "ZoomCurrent",
            120d, currentViewport, live, focus, .1d));
        Near(1.32 * 1.12, zoomed.M11);
        Near(480, zoomed.Transform(new Point(1120, 680)).X);
        Near(300, zoomed.Transform(new Point(1120, 680)).Y);
        Console.WriteLine("PASS production viewport transforms drive WPF Fit/Free Fly/Current projection");
    }

    private static void TestLabelingViewportGeometry()
    {
        TestAspectFitGeometryAccountsForLetterboxing();
        TestDragNormalizesAndClampsToCleanImage();
        TestNormalizedRectangleTracksWindowResize();
        TestZoomAndPanTransformImageAndAnnotationsTogether();
        TestZoomKeepsExactImagePixelUnderCursorWithoutEdgeClamping();
        TestDragCannotStartInLetterbox();
        Console.WriteLine("PASS labeling geometry shares aspect-fit, normalized boxes, zoom and pan");
    }

    private static void TestAddModifyAndDeleteAreSeparateModes()
    {
        Check(LabelingEditorPolicy.Modes.Select(mode => mode.ToString()).SequenceEqual(
                ["Add", "Modify", "Delete", "Negative"]),
            "Labeling modes did not preserve the Mac ordering and identities.");
        Check(LabelingEditorPolicy.Modes.Select(LabelingEditorPolicy.Shortcut).SequenceEqual(
                ['1', '2', '3', '4']),
            "Labeling mode shortcuts did not preserve the Mac 1-4 mapping.");
        Console.WriteLine("PASS testAddModifyAndDeleteAreSeparateModes");
    }

    private static void TestArrowSetCyclingWrapsThroughEverySet()
    {
        var sets = LabelingEditorPolicy.SelectableContexts;
        Check(sets.All(context => context.Identifier is not ("shared" or "enemies" or "world")),
            "The arrow-key set cycle included a nonselectable organizational context.");
        for (var index = 0; index < sets.Count; index++)
        {
            Check(LabelingEditorPolicy.CycledContextIdentifier(
                    sets[index].Identifier, 1) == sets[(index + 1) % sets.Count].Identifier,
                $"Forward set cycling did not wrap from {sets[index].Identifier}.");
            Check(LabelingEditorPolicy.CycledContextIdentifier(
                    sets[index].Identifier, -1) == sets[(index - 1 + sets.Count) % sets.Count].Identifier,
                $"Backward set cycling did not wrap from {sets[index].Identifier}.");
        }
        Console.WriteLine("PASS testArrowSetCyclingWrapsThroughEverySet");
    }

    private static void TestArrowCyclingWrapsThroughContextObjects()
    {
        var context = LabelingEditorPolicy.SelectableContexts.Single(item =>
            item.Identifier == "main-title");
        var first = context.Labels[0].Identifier;
        var last = context.Labels[^1].Identifier;

        Check(LabelingEditorPolicy.CycledClassIdentifier(context.Identifier, first, -1) == last,
            "Backward object cycling did not wrap from the first Main Title object.");
        Check(LabelingEditorPolicy.CycledClassIdentifier(context.Identifier, last, 1) == first,
            "Forward object cycling did not wrap from the last Main Title object.");
        Check(LabelingEditorPolicy.CycledClassIdentifier(
                context.Identifier, context.Labels[2].Identifier, 1) == context.Labels[3].Identifier,
            "Object cycling did not advance through Main Title catalog order.");
        Console.WriteLine("PASS testArrowCyclingWrapsThroughContextObjects");
    }

    private static void TestOneCurrentOrSavedInstanceEnablesTrainingForSelectedObject()
    {
        var mana = DraftBox("game.mana", new Rect(.1, .1, .2, .2));

        Check(!MainWindow.CanTrainSelectedObject(
                "game.health", true, [mana], [], editingExampleIdentifier: null),
            "A different current object enabled training for Health.");
        Check(MainWindow.CanTrainSelectedObject(
                "game.mana", true, [mana], [], editingExampleIdentifier: null),
            "A current positive Mana instance did not enable training for Mana.");
        var savedExampleId = Guid.NewGuid();
        var saved = TrainingExample(savedExampleId,
            new LabelingExampleAnnotation(
                Guid.NewGuid(), "game.health", .1, .1, .2, .2));
        Check(MainWindow.CanTrainSelectedObject(
                "game.health", false, [], [saved], editingExampleIdentifier: null),
            "A saved positive Health instance did not enable training for Health.");
        Check(!MainWindow.CanTrainSelectedObject(
                "game.health", true, [], [saved], editingExampleIdentifier: savedExampleId),
            "The currently edited saved example was counted twice after its positive box was removed.");
        Console.WriteLine("PASS testOneCurrentOrSavedInstanceEnablesTrainingForSelectedObject");
    }

    private static void TestNegativeBoxDoesNotCountAsPositiveTrainingInstance()
    {
        var negative = DraftBox(
            "shared.select-decoration",
            new Rect(.1, .1, .2, .2)) with { IsNegative = true };

        Check(!MainWindow.CanTrainSelectedObject(
                "shared.select-decoration", true, [negative], [], editingExampleIdentifier: null),
            "A negative-only current box enabled training for its selected object.");
        Console.WriteLine("PASS testNegativeBoxDoesNotCountAsPositiveTrainingInstance");
    }

    private static SavedLabelingExample TrainingExample(
        Guid id,
        params LabelingExampleAnnotation[] annotations) => new(
        ".",
        new LabelingExampleManifest(
            LabelingExampleManifest.CurrentSchemaVersion,
            id,
            Guid.NewGuid(),
            "image.png",
            640,
            360,
            Guid.NewGuid(),
            "game",
            annotations,
            annotations.Select(annotation => annotation.ClassIdentifier).ToArray(),
            LabelingExampleCompletion.Ready,
            DateTimeOffset.UnixEpoch));

    private static void TestAutosaveStartsWithFirstBoxAndKeepsEmptySavedExampleEdits()
    {
        Check(!LabelingEditorPolicy.ShouldPersist(0, editsSavedExample: false),
            "An untouched unsaved draft unexpectedly autosaved.");
        Check(LabelingEditorPolicy.ShouldPersist(1, editsSavedExample: false),
            "The first rectangle did not start autosave.");
        Check(LabelingEditorPolicy.ShouldPersist(0, editsSavedExample: true),
            "Deleting the final rectangle from a saved example did not autosave.");
        Console.WriteLine("PASS testAutosaveStartsWithFirstBoxAndKeepsEmptySavedExampleEdits");
    }

    private static void TestRepeatedClassInstancesCanBeSelectedOrAddedIndependently()
    {
        var first = DraftBox("shared.select-decoration", new Rect(.1, .1, .1, .1));
        var second = DraftBox("shared.select-decoration", new Rect(.4, .1, .1, .1));
        var rectangles = DraftRectangles(first, second);
        var draft = new LabelingEditorDraft();

        Check(draft.Select(rectangles, new Point(.45, .15), "shared.select-decoration")?.Id == second.Id,
            "The second repeated class instance could not be selected independently.");
        Check(draft.Select(rectangles, new Point(.75, .75), "shared.select-decoration") is null,
            "Class selection accepted a point outside every repeated instance.");
        var third = draft.BeginRectangle(
            rectangles, "shared.select-decoration", new Point(.75, .75));
        draft.Update(rectangles, third.Id, new Rect(.75, .75, .1, .1));
        Check(draft.Finish(rectangles, third.Id), "A valid third repeated instance was rejected.");
        Check(rectangles.Count == 3
                && rectangles.All(item => item.ClassIdentifier == "shared.select-decoration"),
            "Adding a repeated class instance replaced or reclassified another rectangle.");
        Console.WriteLine("PASS testRepeatedClassInstancesCanBeSelectedOrAddedIndependently");
    }

    private static void TestDraftKeepsMultipleRectanglesAndSelectsTopmostHit()
    {
        var first = DraftBox("game.playable-knight", new Rect(.1, .1, .4, .4));
        var second = DraftBox("game.health", new Rect(.2, .2, .4, .4));
        var rectangles = DraftRectangles(first, second);
        var draft = new LabelingEditorDraft();

        Check(draft.Select(rectangles, new Point(.3, .3))?.Id == second.Id,
            "An overlapping hit did not select the topmost rectangle.");
        Check(draft.Select(rectangles, new Point(.15, .15))?.Id == first.Id,
            "A non-overlapping point did not select its rectangle.");
        Check(draft.Select(rectangles, new Point(.9, .9)) is null,
            "A point outside every rectangle unexpectedly selected one.");
        Check(rectangles.Count == 2, "Selection mutated the draft rectangles.");
        Console.WriteLine("PASS testDraftKeepsMultipleRectanglesAndSelectsTopmostHit");
    }

    private static void TestObjectSelectionUsesClassInsteadOfOverlappingGeometry()
    {
        var mana = DraftBox("game.mana", new Rect(.1, .1, .5, .5));
        var rectangles = DraftRectangles(mana);
        var draft = new LabelingEditorDraft();

        Check(draft.Select(rectangles, "game.health") is null,
            "Selecting an absent class reclassified overlapping geometry.");
        var health = draft.BeginRectangle(rectangles, "game.health", new Point(.2, .2));
        draft.Update(rectangles, health.Id, new Rect(.2, .2, .5, .5));
        Check(draft.Finish(rectangles, health.Id), "The overlapping health rectangle was rejected.");
        Check(draft.Select(rectangles, "game.mana")?.Id == mana.Id,
            "Modify class selection did not retain the original mana rectangle.");
        Check(draft.Select(rectangles, "game.health")?.Id == health.Id,
            "Modify class selection did not find the health rectangle.");
        Check(rectangles[0].ClassIdentifier == "game.mana"
                && rectangles[1].ClassIdentifier == "game.health",
            "Modify class selection reclassified a rectangle.");
        Console.WriteLine("PASS testObjectSelectionUsesClassInsteadOfOverlappingGeometry");
    }

    private static void TestDeleteAndUndoRestoreRectangleAndSelection()
    {
        var rectangles = DraftRectangles();
        var draft = new LabelingEditorDraft();
        var rectangle = draft.BeginRectangle(rectangles, "game.geo", new Point(.1, .1));
        draft.Update(rectangles, rectangle.Id, new Rect(.1, .1, .2, .2));
        Check(draft.Finish(rectangles, rectangle.Id), "Fixture rectangle was rejected.");

        Check(draft.DeleteSelected(rectangles) && rectangles.Count == 0 && !draft.CanDelete,
            "Delete did not remove the selected rectangle and selection.");
        Check(draft.Undo(rectangles)
                && rectangles.Single().Id == rectangle.Id
                && draft.SelectedId == rectangle.Id
                && draft.CanDelete,
            "Undo did not restore both the rectangle and selected identifier.");
        Console.WriteLine("PASS testDeleteAndUndoRestoreRectangleAndSelection");
    }

    private static void TestNegativeCorrectionConvertsTopmostPositiveBoxAndCanBeUndone()
    {
        var mana = DraftBox("game.mana", new Rect(.1, .1, .5, .5));
        var crawlid = DraftBox("enemies.crawlid", new Rect(.2, .2, .4, .4));
        var rectangles = DraftRectangles(mana, crawlid);
        var draft = new LabelingEditorDraft();

        var corrected = draft.MarkNegative(rectangles, new Point(.3, .3));
        Check(corrected?.Id == crawlid.Id && corrected.IsNegative
                && corrected.ClassIdentifier == "enemies.crawlid",
            "Negative correction did not convert the topmost positive rectangle.");
        Check(!rectangles.Single(item => item.Id == mana.Id).IsNegative
                && draft.SelectedId == crawlid.Id,
            "Negative correction changed the lower rectangle or lost selection.");
        Check(draft.Undo(rectangles)
                && !rectangles.Single(item => item.Id == crawlid.Id).IsNegative,
            "Undo did not restore the corrected rectangle to positive.");
        Console.WriteLine("PASS testNegativeCorrectionConvertsTopmostPositiveBoxAndCanBeUndone");
    }

    private static void TestNegativeModeAndFreshProfileChoices()
    {
        foreach (var context in LabelingEditorPolicy.SelectableContexts)
        {
            var negativeAvailable = LabelingEditorPolicy.AvailableModes(context.Identifier)
                .Contains(LabelingEditorMode.Negative);
            Check(negativeAvailable == (context.Identifier == "game"),
                $"Negative mode availability was incorrect for {context.Identifier}.");
        }

        var freshProfileLabels = StencilLabelSelectionPolicy.AvailableLabels(
            "select-profile", Array.Empty<LabelingExampleAnnotation>(),
            modded: false, hidesCompleted: true);
        Check(!freshProfileLabels.Any(label => label.Identifier == "select-profile.clear-save"),
            "A fresh Select Profile unexpectedly offered deferred Clear Save labeling.");
        var deferredProfileLabels = StencilLabelSelectionPolicy.AvailableLabels(
            "select-profile", Array.Empty<LabelingExampleAnnotation>(),
            modded: false, hidesCompleted: true, includesDeferredLabels: true);
        Check(deferredProfileLabels.Any(label => label.Identifier == "select-profile.clear-save"),
            "Select Profile did not offer Clear Save when deferred labels were included.");

        var requiredContexts = StencilInitializationPolicy.RequiredContexts
            .Select(context => context.Identifier).ToArray();
        Check(requiredContexts.Take(3).SequenceEqual(
                ["main-title", "select-profile", "gameplay-hud"]),
            "Stencil campaign does not begin with Main Title, Select Profile, and Gameplay HUD.");
        Check(requiredContexts.LastOrDefault() == "clear-save",
            "Stencil campaign does not end with Clear Save.");
        Console.WriteLine("PASS testNegativeModeAndFreshProfileChoices");
    }

    private static void TestResizeCanBeUndone()
    {
        var original = new Rect(.1, .1, .2, .2);
        var rectangles = DraftRectangles();
        var draft = new LabelingEditorDraft();
        var rectangle = draft.BeginRectangle(
            rectangles, "game.playable-knight", original.TopLeft);
        draft.Update(rectangles, rectangle.Id, original);
        Check(draft.Finish(rectangles, rectangle.Id), "Fixture rectangle was rejected.");
        Check(draft.BeginModification(rectangles, rectangle.Id), "Resize did not begin.");
        var resized = new Rect(.2, .25, .3, .4);
        draft.Update(rectangles, rectangle.Id, resized);
        Check(draft.Finish(rectangles, rectangle.Id), "Valid resize was rejected.");
        Check(draft.SelectedId == rectangle.Id
                && rectangles.Single(item => item.Id == draft.SelectedId).NormalizedRect == resized,
            "Resize did not publish the exact selected rectangle before undo.");
        Check(draft.Undo(rectangles)
                && draft.SelectedId == rectangle.Id
                && rectangles.Single(item => item.Id == draft.SelectedId).NormalizedRect == original,
            "Undo did not restore the pre-resize rectangle and selection.");
        Console.WriteLine("PASS testResizeCanBeUndone");
    }

    private static void TestMovingRectangleKeepsSizeAndClampsInsideImage()
    {
        var source = new Rect(.2, .3, .25, .4);
        var translated = LabelingEditorPolicy.Translated(source, new Vector(.1, -.2));
        LabelNear(.3, translated.X, .0001);
        LabelNear(.1, translated.Y, .0001);
        LabelNear(.25, translated.Width, .0001);
        LabelNear(.4, translated.Height, .0001);
        Check(LabelingEditorPolicy.Translated(source, new Vector(2, 2))
                == new Rect(.75, .6, .25, .4),
            "Translation did not clamp the rectangle inside normalized image bounds.");
        Console.WriteLine("PASS testMovingRectangleKeepsSizeAndClampsInsideImage");
    }

    private static void TestMoveCanBeUndone()
    {
        var original = new Rect(.2, .3, .25, .4);
        var rectangles = DraftRectangles();
        var draft = new LabelingEditorDraft();
        var rectangle = draft.BeginRectangle(rectangles, "game.mana", original.TopLeft);
        draft.Update(rectangles, rectangle.Id, original);
        Check(draft.Finish(rectangles, rectangle.Id), "Fixture rectangle was rejected.");
        Check(draft.BeginModification(rectangles, rectangle.Id), "Move did not begin.");
        draft.Update(rectangles, rectangle.Id,
            LabelingEditorPolicy.Translated(original, new Vector(.1, -.2)));
        Check(draft.Finish(rectangles, rectangle.Id), "Valid move was rejected.");
        Check(draft.Undo(rectangles)
                && draft.SelectedId == rectangle.Id
                && rectangles.Single(item => item.Id == draft.SelectedId).NormalizedRect == original,
            "Undo did not restore the pre-move rectangle and selection.");
        Console.WriteLine("PASS testMoveCanBeUndone");
    }

    private static void TestTinyRectangleRollsBackWithoutLeavingUndoEntry()
    {
        var rectangles = DraftRectangles();
        var draft = new LabelingEditorDraft();
        var rectangle = draft.BeginRectangle(rectangles, "game.health", new Point(.1, .1));
        draft.Update(rectangles, rectangle.Id, new Rect(.1, .1, .002, .2));
        Check(!draft.Finish(rectangles, rectangle.Id, minimumDimension: .003),
            "A sub-minimum rectangle was accepted.");
        Check(rectangles.Count == 0 && !draft.CanUndo,
            "Rejected drawing did not roll back its rectangle and undo snapshot.");
        Console.WriteLine("PASS testTinyRectangleRollsBackWithoutLeavingUndoEntry");
    }

    private static void TestControlPointPixelsStayOutsideRectangleInnerArea()
    {
        var source = new Rect(100, 80, 200, 120);
        var markers = LabelingEditorPolicy.ControlPoints(source, markerSize: 5);
        var topLeft = markers[LabelingResizeCorner.TopLeft];
        var topRight = markers[LabelingResizeCorner.TopRight];
        var bottomLeft = markers[LabelingResizeCorner.BottomLeft];
        var bottomRight = markers[LabelingResizeCorner.BottomRight];
        Check(topLeft.Right == source.Left && topLeft.Bottom == source.Top,
            "Top-left control point entered the rectangle interior.");
        Check(topRight.Left == source.Right && topRight.Bottom == source.Top,
            "Top-right control point entered the rectangle interior.");
        Check(bottomLeft.Right == source.Left && bottomLeft.Top == source.Bottom,
            "Bottom-left control point entered the rectangle interior.");
        Check(bottomRight.Left == source.Right && bottomRight.Top == source.Bottom,
            "Bottom-right control point entered the rectangle interior.");
        Check(markers.Values.All(marker => marker.Width == 5 && marker.Height == 5),
            "Control points were not the Mac 5-pixel size.");
        Console.WriteLine("PASS testControlPointPixelsStayOutsideRectangleInnerArea");
    }

    private static ObservableCollection<EditableBox> DraftRectangles(
        params EditableBox[] rectangles) => new(rectangles);

    private static EditableBox DraftBox(string classIdentifier, Rect rectangle) => new(
        Guid.NewGuid(), classIdentifier, rectangle.X, rectangle.Y,
        rectangle.Width, rectangle.Height, IsNegative: false);

    private static void TestAspectFitGeometryAccountsForLetterboxing()
    {
        var layout = LabelingLayout(
            new Size(1920, 1080),
            new Size(1000, 1000));
        var fitted = LabelingRect(layout, "FittedRect");
        LabelNear(0, fitted.X, .0001);
        LabelNear(218.75, fitted.Y, .0001);
        LabelNear(1000, fitted.Width, .0001);
        LabelNear(562.5, fitted.Height, .0001);
    }

    private static void TestDragNormalizesAndClampsToCleanImage()
    {
        var layout = LabelingLayout(
            new Size(640, 360),
            new Size(1280, 800));
        var normalized = LabelingNullableRect(
            layout,
            "NormalizedRect",
            new Point(320, 220),
            new Point(1500, 900))
            ?? throw new Exception("Production labeling layout rejected a valid image drag.");
        LabelNear(.25, normalized.X, .0001);
        LabelNear(.25, normalized.Y, .0001);
        LabelNear(.75, normalized.Width, .0001);
        LabelNear(.75, normalized.Height, .0001);
    }

    private static void TestNormalizedRectangleTracksWindowResize()
    {
        var normalized = new Rect(.2, .25, .3, .4);
        var compact = LabelingRenderedBounds(
            new Size(640, 360),
            new Size(640, 480),
            zoom: 1,
            pan: default,
            normalized);
        var large = LabelingRenderedBounds(
            new Size(640, 360),
            new Size(1280, 960),
            zoom: 1,
            pan: default,
            normalized);
        Check(compact.Image == new Rect(0, 60, 640, 360),
            $"Compact production Viewbox image bounds were {compact.Image}.");
        Check(large.Image == new Rect(0, 120, 1280, 720),
            $"Large production Viewbox image bounds were {large.Image}.");
        LabelNear(compact.Annotation.Left * 2, large.Annotation.Left, .0001);
        LabelNear(compact.Annotation.Top * 2, large.Annotation.Top, .0001);
        LabelNear(compact.Annotation.Width * 2, large.Annotation.Width, .0001);
        LabelNear(compact.Annotation.Height * 2, large.Annotation.Height, .0001);
    }

    private static void TestZoomAndPanTransformImageAndAnnotationsTogether()
    {
        var rendered = LabelingRenderedBounds(
            new Size(640, 360),
            new Size(1280, 800),
            zoom: 2,
            pan: new Vector(30, -20),
            new Rect(.25, .25, .5, .5));
        Check(rendered.Image == new Rect(-610, -340, 2560, 1440),
            "The transformed production Image bounds differ from Mac.");
        Check(rendered.Annotation == new Rect(30, 20, 1280, 720),
            "The real WPF Image and annotation did not share the production transform.");
    }

    private static void TestZoomKeepsExactImagePixelUnderCursorWithoutEdgeClamping()
    {
        var imageSize = new Size(640, 360);
        var containerSize = new Size(1280, 800);
        const double oldZoom = 2;
        var oldPan = new Vector(500, -270);
        var cursor = new Point(310, 260);
        var oldLayout = LabelingLayout(imageSize, containerSize, oldZoom, oldPan);
        var imagePoint = LabelingNullablePoint(oldLayout, "NormalizedPoint", cursor, false)
            ?? throw new Exception("Production layout rejected the cursor over the zoomed image.");
        const double newZoom = 3.5;
        var newPan = LabelingZoomedPan(oldPan, oldZoom, newZoom, cursor, containerSize);
        var newLayout = LabelingLayout(imageSize, containerSize, newZoom, newPan);
        var anchoredImagePoint = LabelingNullablePoint(newLayout, "NormalizedPoint", cursor, false)
            ?? throw new Exception("Zoom moved the cursor outside the production image layout.");
        LabelNear(imagePoint.X, anchoredImagePoint.X, .000001);
        LabelNear(imagePoint.Y, anchoredImagePoint.Y, .000001);
        Check(Math.Abs(newPan.X) > containerSize.Width / 2,
            "Production zoom unexpectedly clamped the cursor-anchored pan at the viewport edge.");
    }

    private static void TestDragCannotStartInLetterbox()
    {
        var layout = LabelingLayout(
            new Size(1920, 1080),
            new Size(1000, 1000));
        Check(LabelingNullableRect(
                layout,
                "NormalizedRect",
                new Point(500, 100),
                new Point(700, 400)) is null,
            "Production labeling layout accepted a drag starting in the letterbox.");
    }

    private static Type LabelingLayoutType => typeof(MainWindow).Assembly.GetType(
        "HollowKnightVision.Windows.App.LabelingImageLayout", throwOnError: true)!;

    private static object LabelingLayout(
        Size imageSize,
        Size containerSize,
        double zoom = 1,
        Vector pan = default) =>
        Activator.CreateInstance(
            LabelingLayoutType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [imageSize, containerSize, zoom, pan],
            culture: null)!;

    private static Rect LabelingRect(object layout, string property) =>
        (Rect)LabelingLayoutType.GetProperty(
            property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(layout)!;

    private static Rect? LabelingNullableRect(object layout, string method, params object[] arguments) =>
        (Rect?)LabelingLayoutType.GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(layout, arguments);

    private static Point? LabelingNullablePoint(object layout, string method, params object[] arguments) =>
        (Point?)LabelingLayoutType.GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(layout, arguments);

    private static Vector LabelingZoomedPan(
        Vector currentPan,
        double currentZoom,
        double newZoom,
        Point focus,
        Size containerSize) =>
        (Vector)LabelingLayoutType.GetMethod(
            "ZoomedPan",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(
            null,
            [currentPan, currentZoom, newZoom, focus, containerSize])!;

    private static (Rect Image, Rect Annotation) LabelingRenderedBounds(
        Size imageSize,
        Size containerSize,
        double zoom,
        Vector pan,
        Rect normalized)
    {
        var type = typeof(MainWindow).Assembly.GetType(
            "HollowKnightVision.Windows.App.LabelingViewportRendering",
            throwOnError: true)!;
        var rendered = type.GetMethod(
            "Measure",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(
            null,
            [imageSize, containerSize, zoom, pan, normalized])!;
        var resultType = rendered.GetType();
        return (
            (Rect)resultType.GetProperty("ImageBounds")!.GetValue(rendered)!,
            (Rect)resultType.GetProperty("AnnotationBounds")!.GetValue(rendered)!);
    }

    private static void LabelNear(double expected, double actual, double accuracy) =>
        Check(Math.Abs(expected - actual) <= accuracy,
            $"Expected {expected} ± {accuracy}, got {actual}.");

    private static void TestSolidCapture()
    {
        var surface = new Border();
        var window = Offscreen(surface, 128, 72);
        window.Show(); Pump();
        try
        {
            var target = new HollowKnightWindow(new WindowInteropHelper(window).Handle,
                Environment.ProcessId, "capture-test", "capture-test", 128, 72, false);
            foreach (var value in new byte[] { 0, 4, 64, 255 })
            {
                surface.Background = new SolidColorBrush(Color.FromRgb(value, value, value));
                window.UpdateLayout(); Pump();
                // UpdateLayout does not wait for the compositor. Keep the
                // pixel assertion, but allow the new brush to reach the surface.
                var deadline = Stopwatch.StartNew();
                var matched = false;
                do
                {
                    var captured = new GdiWindowCapture().Capture(target, value);
                    var offset = captured.Height / 2 * captured.Stride + captured.Width / 2 * 4;
                    matched = Enumerable.Range(0, 3).All(channel => captured.Pixels[offset + channel] == value);
                    if (matched) break;
                    Thread.Sleep(16); Pump();
                } while (deadline.Elapsed < TimeSpan.FromSeconds(2));
                Check(matched, $"Uniform {value} frame replaced by desktop pixels.");
            }
            Console.WriteLine("PASS black, fade, grey and white captures stay window-owned");
        }
        finally { window.Close(); }
    }

    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .001,
        $"Expected {expected}, got {actual}.");

    private static void TestLaunchPlaceholderNamesGameWait()
    {
        var source = File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "MainWindow.source.xaml"));
        Check(source.Contains("Text=\"Waiting for Hollow Knight…\"", StringComparison.Ordinal),
            "The launch placeholder does not say it is waiting for Hollow Knight.");
        Check(!source.Contains("Text=\"Starting live atlas…\"", StringComparison.Ordinal),
            "The launch placeholder still claims that the live atlas is starting.");
        Console.WriteLine("PASS launch placeholder names the Hollow Knight wait");
    }

    private static void TestMainWindowHasProductTitle()
    {
        var source = File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "MainWindow.source.xaml"));
        Check(Regex.IsMatch(source,
                @"<Window\s+[^>]*Title\s*=\s*""Hollow Knight Vision""",
                RegexOptions.Singleline),
            "The main window does not expose the Hollow Knight Vision product name.");
        Console.WriteLine("PASS main window exposes product title");
    }

    private static void TestGroundFeatureOverlayStartsVisible()
    {
        var source = File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "MainWindow.source.xaml"));
        foreach (var name in new[] { "GameStateCheck", "StencilDetectionCheck", "GroundFeaturesCheck" })
        {
            var element = Regex.Match(source, "<CheckBox\\s+[^>]*x:Name=\"" + name + "\"[^>]*/>", RegexOptions.Singleline);
            Check(element.Success && Regex.IsMatch(element.Value, @"IsChecked\s*=\s*""True"""),
                $"The Windows debug option {name} is not enabled by default.");
        }
        Console.WriteLine("PASS game state, stencil detection and ground features default on");
    }

    private static void TestGroundFeatureOverlayUsesMacReviewPalette()
    {
        var result = GroundHypothesisTrackingResult.Empty with
        {
            Features =
            [
                new(1, 1, 0, new PixelRect(10, 11, 16, 12),
                    GroundHypothesisClassification.Candidate, null, null),
                new(2, 1, 1, new PixelRect(20, 21, 16, 12),
                    GroundHypothesisClassification.GroundPlane, null, null),
                new(3, 1, 2, new PixelRect(30, 31, 16, 12),
                    GroundHypothesisClassification.GlobalMatch, null, null),
                new(4, 1, 3, new PixelRect(40, 41, 16, 12),
                    GroundHypothesisClassification.Occluded, null, null),
                new(5, 1, 4, new PixelRect(50, 51, 16, 12),
                    GroundHypothesisClassification.DepthError, null, null),
            ],
        };
        var canvas = new Canvas();
        var count = GroundFeatureOverlayPresentation.DrawLive(canvas, result);
        Check(count == 4 && canvas.Children.Count == 4,
            "The live renderer did not draw each accepted ground feature exactly once.");
        Check(canvas.Children.OfType<Rectangle>().All(rectangle =>
                rectangle.StrokeThickness == 1 && !rectangle.IsHitTestVisible),
            "Ground feature rectangles do not use the one-pixel non-interactive review style.");
        var purple = canvas.Children.OfType<Rectangle>()
            .Single(rectangle => Canvas.GetLeft(rectangle) == 30);
        Check(purple.Stroke is SolidColorBrush liveBrush
                && liveBrush.Color == Color.FromRgb(191, 79, 255)
                && Math.Abs(purple.Opacity - .95) < .001,
            "Global ground match is not rendered with the Mac purple review color.");

        var atlasFeature = new GroundHypothesisAtlasFeature(
            7,
            2,
            new AtlasPoint(80, 90),
            Enumerable.Repeat((byte)80, GroundHypothesisTracker.FeaturePixelCount).ToArray(),
            Enumerable.Repeat(byte.MaxValue, GroundHypothesisTracker.FeaturePixelCount).ToArray());
        var persistent = GroundFeatureOverlayPresentation.PersistentRectangle(
            atlasFeature,
            2,
            3);
        Check(persistent.Width == 32 && persistent.Height == 36,
            "Persistent ground rectangle did not scale with the displayed frame.");
        Check(persistent.Stroke is SolidColorBrush atlasBrush
                && atlasBrush.Color == Color.FromRgb(191, 79, 255)
                && Math.Abs(persistent.Opacity - .95) < .001,
            "Persistent ground feature is not rendered with the Mac purple review color.");
        Console.WriteLine("PASS live and persistent ground rectangles use Mac review palette");
    }

    private static void TestBackgroundKeyDelivery()
    {
        var previousFocus = GetForegroundWindow();
        _ = GetCursorPos(out var previousPointer);
        Check(GetClipCursor(out var previousClip), "Could not save desktop cursor confinement.");
        var owner = Offscreen(new Border(), 128, 72);
        owner.Show(); Pump();
        var messages = new ConcurrentQueue<(int Message, nint WParam, bool IsDown)>();
        var shortcuts = new ConcurrentQueue<Key>();
        using var gameReady = new ManualResetEventSlim();
        Dispatcher? gameDispatcher = null;
        nint gameHandle = 0;
        var lockOnFocus = 0;
        var allowsInput = true;
        Exception? gameError = null;
        var gameThread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var game = Offscreen(new Border(), 128, 72);
                game.Left = 200;
                game.Top = 200;
                game.Show();
                gameHandle = new WindowInteropHelper(game).Handle;
                var gameSource = HwndSource.FromHwnd(gameHandle)!;
                HwndSourceHook gameHook = (nint window, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    if (message == 0x0007 && Interlocked.Exchange(ref lockOnFocus, 0) != 0)
                        ConfineCursorToGameCenter(window);
                    if (message is 0x0100 or 0x0101)
                        messages.Enqueue((message, wParam, (GetKeyState(0x27) & 0x8000) != 0));
                    return 0;
                };
                gameSource.AddHook(gameHook);
                gameDispatcher = Dispatcher.CurrentDispatcher;
                gameReady.Set();
                Dispatcher.Run();
                gameSource.RemoveHook(gameHook);
                game.Close();
            }
            catch (Exception error)
            {
                gameError = error;
                gameReady.Set();
            }
        });
        gameThread.SetApartmentState(ApartmentState.STA);
        gameThread.Start();
        Check(gameReady.Wait(3000) && gameError is null && gameHandle != 0,
            $"Could not create the separate-thread test game: {gameError}");
        var type = typeof(MainWindow).Assembly.GetType("HollowKnightVision.Windows.App.WindowsNormalGameInput")!;
        var input = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [owner, (Func<bool>)(() => allowsInput), (Action<Key>)(key => shortcuts.Enqueue(key)), (Action<string>)(message => Console.WriteLine(message))], null)!;
        object? Invoke(string name, params object[] values) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, values);
        try
        {
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try { Check(SetForegroundWindow(ownerHandle), "Could not focus test owner."); }
            finally { if (attached) _ = AttachThreadInput(currentThread, foregroundThread, false); }
            var target = new HollowKnightWindow(gameHandle, Environment.ProcessId, "input-test", "input-test", 128, 72, false);
            Volatile.Write(ref lockOnFocus, 1);
            Invoke("Start", target);
            var prepare = (Task<bool>)Invoke("PrepareForInputAsync")!;
            var timer = Stopwatch.StartNew();
            while (!prepare.IsCompleted && timer.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(5); }
            Check(prepare.IsCompletedSuccessfully && prepare.Result,
                $"Vision could not establish native game focus. Status={prepare.Status}; error={prepare.Exception}.");
            Check(GetForegroundWindow() == gameHandle,
                "Hollow Knight test window was not the foreground input target.");
            var overlayStyle = GetWindowLongPtr(ownerHandle, -20).ToInt64();
            Check((overlayStyle & 0x08000000L) != 0 && (overlayStyle & 0x00000008L) != 0,
                "Vision was not a non-activating topmost overlay.");
            Check(owner.Topmost,
                "Vision did not retain WPF's managed topmost state.");
            Check(GetClipCursor(out var activationClip) && activationClip.Equals(previousClip),
                "Game activation left the cursor confined to the game's center.");
            Check(GetCursorPos(out var activationPointer)
                    && activationPointer.X == previousPointer.X && activationPointer.Y == previousPointer.Y,
                "Game activation did not preserve the free pointer position.");

            SendNativeKey(0x27, extended: true);
            for (var index = 0; index < 40 && messages.Count < 2; index++) { Pump(); Thread.Sleep(5); }
            Check(messages.Select(item => item.Message).SequenceEqual(new[] { 0x0100, 0x0101 }),
                $"Game received the wrong key sequence: {string.Join(',', messages.Select(item => $"0x{item.Message:X}"))}.");
            Check(messages.All(item => item.WParam == new nint(0x27)),
                "Native input changed the Right virtual key.");
            Check(messages.Select(item => item.IsDown).SequenceEqual(new[] { true, false }),
                "The foreground game could not observe the native Right key state.");

            var gameMessageCount = messages.Count;
            SendNativeKey(0x4C, extended: false);
            for (var index = 0; index < 40 && shortcuts.IsEmpty; index++) { Pump(); Thread.Sleep(5); }
            Check(shortcuts.SequenceEqual([Key.L]),
                "The overlay did not reserve the Vision L shortcut.");
            Check(messages.Count == gameMessageCount,
                "The Vision L shortcut leaked into the foreground game.");
            Check(GetForegroundWindow() == gameHandle,
                "Reserving a Vision shortcut unexpectedly deactivated the game.");
            Check(GetCursorPos(out var currentPointer)
                    && currentPointer.X == previousPointer.X
                    && currentPointer.Y == previousPointer.Y,
                "Native overlay input moved the real pointer.");
            // Exercise Unity-style re-locking during play while keyboard focus
            // stays with the game, including movement to Vision's controls.
            var buttonPointer = new NativePoint { X = previousClip.Right - 30, Y = previousClip.Bottom - 65 };
            Check(SetCursorPos(buttonPointer.X, buttonPointer.Y), "Could not move to the overlay control area.");
            Invoke("MaintainOverlay");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                gameDispatcher!.Invoke(() => ConfineCursorToGameCenter(gameHandle));
                var releaseWait = Stopwatch.StartNew();
                NativeRect clip;
                do { Pump(); Thread.Sleep(5); GetClipCursor(out clip); }
                while (!clip.Equals(previousClip) && releaseWait.ElapsedMilliseconds < 1000);
                Check(clip.Equals(previousClip), "Overlay monitor did not release renewed game cursor confinement.");
                Check(GetCursorPos(out var releasedPointer)
                        && releasedPointer.X == buttonPointer.X && releasedPointer.Y == buttonPointer.Y,
                    "Renewed game confinement moved the pointer away from Vision's controls.");
                Check(GetForegroundWindow() == gameHandle, "Releasing the cursor stole keyboard focus from the game.");
            }
            var foreignClip = new NativeRect { Left = 50, Top = 50, Right = 70, Bottom = 70 };
            gameDispatcher!.Invoke(() => ConfineCursorToGameCenter(gameHandle));
            var renderPressure = Stopwatch.StartNew();
            var releasedWhileRendering = false;
            Action? renderWork = null;
            renderWork = () =>
            {
                if (GetClipCursor(out var busyClip) && busyClip.Equals(previousClip))
                    releasedWhileRendering = true;
                if (renderPressure.ElapsedMilliseconds >= 350) return;
                Thread.Sleep(2);
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, renderWork!);
            };
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, renderWork);
            Pump();
            Check(releasedWhileRendering,
                "Continuous gameplay rendering starved cursor release and trapped the pointer.");
            Check(ClipCursor(ref foreignClip), "Could not set unrelated cursor confinement.");
            Invoke("MaintainOverlay");
            Check(GetClipCursor(out var preservedClip) && preservedClip.Equals(foreignClip),
                "Overlay cleared cursor confinement outside the target game.");
            _ = ClipCursor(ref previousClip);
            allowsInput = false;
            Invoke("UpdateContext");
            Check(ClipCursor(ref foreignClip), "Could not set editor cursor confinement.");
            Invoke("MaintainOverlay");
            Check(GetClipCursor(out preservedClip) && preservedClip.Equals(foreignClip),
                "Inactive overlay cleared another context's cursor confinement.");
            Console.WriteLine("PASS native game focus receives arrows beneath Vision and reserves Vision shortcuts");
            Console.WriteLine("PASS activation and repeated game cursor locks preserve GUI access without stealing keyboard focus");
        }
        finally
        {
            _ = ClipCursor(ref previousClip);
            ((IDisposable)input).Dispose();
            var restoredStyle = GetWindowLongPtr(new WindowInteropHelper(owner).Handle, -20).ToInt64();
            Check((restoredStyle & 0x08000008L) == 0,
                "Disposing native overlay input left Vision topmost or non-activating.");
            Check(!owner.Topmost,
                "Disposing native overlay input left WPF's managed topmost state enabled.");
            gameDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            Check(gameThread.Join(3000), "The separate-thread test game did not close.");
            owner.Close();
            _ = SetForegroundWindow(previousFocus);
            _ = SetCursorPos(previousPointer.X, previousPointer.Y);
        }
    }

    private static void ConfineCursorToGameCenter(nint game)
    {
        Check(GetWindowRect(game, out var bounds), "Could not read test game bounds.");
        var x = (bounds.Left + bounds.Right) / 2;
        var y = (bounds.Top + bounds.Bottom) / 2;
        var center = new NativeRect { Left = x, Top = y, Right = x, Bottom = y };
        Check(ClipCursor(ref center), "Could not simulate the game's center cursor lock.");
    }

    private static void SendNativeKey(byte virtualKey, bool extended)
    {
        var scanCode = (byte)MapVirtualKey(virtualKey, 0);
        var flags = extended ? 0x0001u : 0;
        KeybdEvent(virtualKey, scanCode, flags, 0);
        Thread.Sleep(30);
        KeybdEvent(virtualKey, scanCode, flags | 0x0002u, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(ref NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
