using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;

internal static class AtlasWindowIntegrationTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(25);

    internal static void Run()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-atlas-window-");
        try
        {
            VerifyWorkspaceOperations(temporary.FullName);
            VerifyRestoredPassageAlignment(Directory.CreateDirectory(Path.Combine(temporary.FullName, "passages")).FullName);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        Console.WriteLine("PASS Atlas window saves, restores and deletes scoped snapshots and switches both playthroughs");
        Console.WriteLine("PASS Hacker Atlas draws restored rooms at the measured passage elevation");
        Console.WriteLine("PASS Gameplay Atlas draws measured doorways independently of optional outlines and hides them outside gameplay");
    }

    private static void VerifyRestoredPassageAlignment(string root)
    {
        var archive = SampleHackerArchive();
        var leftRoom = archive.Rooms.Single();
        var rightRoom = leftRoom with { Id = Guid.NewGuid(), SceneName = "SecondRoom" };
        var passage = new HackerRoomConnection(Guid.NewGuid(), leftRoom.Id, rightRoom.Id,
            HackerRoomSide.Right, HackerRoomSide.Left, 68, 44) { HasGroundedAlignment = true };
        var hackerStates = new HackerAtlasArchiveStore(Path.Combine(root, "hacker-atlas-v1"));
        hackerStates.Write(null, archive with
        {
            Rooms = [leftRoom, rightRoom],
            Connections = [passage],
            RoomOffsets = new Dictionary<string, AtlasPoint>()
        });

        var window = new MainWindow(GameRuntimeMode.Modded, gamePath: null, root: root, documents: root);
        try
        {
            Wait(InvokeTask(window, "InitializeAtlasesAsync"));
            var images = Control<Canvas>(window, "AtlasCanvas").Children.OfType<Image>()
                .Where(image => image.Source is not null && double.IsFinite(Canvas.GetLeft(image)))
                .OrderBy(Canvas.GetLeft).ToArray();
            Check(images.Length == 2 && Canvas.GetTop(images[0]) - Canvas.GetTop(images[1]) == 24,
                "Restoring the Hacker atlas must draw the two tiles at the measured doorway elevation.");

            var outputField = typeof(MainWindow).GetField("hackerAtlasOutput", InstanceMembers)
                ?? throw new MissingFieldException(nameof(MainWindow), "hackerAtlasOutput");
            var canonical = (HackerAtlasOutput?)outputField.GetValue(window)
                ?? throw new InvalidOperationException("No Hacker atlas was restored.");
            outputField.SetValue(window, null);
            try
            {
                var fallback = (System.Runtime.CompilerServices.ITuple)Invoke(window, "BuildHackerRoomLayout")!;
                var connection = ((IReadOnlyList<HackerRoomConnection>)fallback[2]!).Single();
                var layout = (HackerRoomLayout)fallback[3]!;
                Check(connection.HasGroundedAlignment
                    && layout.Positions[connection.ToRoomId].Y - layout.Positions[connection.FromRoomId].Y == 24,
                    "The editable room topology must retain the measured elevation and its provenance.");
            }
            finally
            {
                outputField.SetValue(window, canonical);
            }
        }
        finally
        {
            SetField(window, "isClosing", true);
            Wait(InvokeTask(window, "PrepareShutdownAsync"));
        }
    }

    private static void VerifyWorkspaceOperations(string root)
    {
        var activeWorld = Directory.CreateDirectory(Path.Combine(root, "atlas-v1", "active")).FullName;
        var gameplayStates = new AtlasStateStore(Path.Combine(root, "atlas-v1", "states"));
        var hackerStates = new HackerAtlasArchiveStore(Path.Combine(root, "hacker-atlas-v1"));
        var profile = new SaveProfileAtlas(1, Guid.NewGuid());
        VisionResetController.WritePersistedAtlasMetadata(activeWorld, new(7, DateTimeOffset.UtcNow, 256, 1));
        profile.Write(activeWorld);
        var initialHacker = SampleHackerArchive();
        hackerStates.Write(profile, initialHacker);

        // Keep the window unshown so its Loaded handler cannot start capture,
        // connect the receiver, or start a game. Exercise the registered XAML handlers.
        var window = new MainWindow(GameRuntimeMode.Modded, gamePath: null, root: root, documents: root);
        try
        {
            Wait(InvokeTask(window, "InitializeAtlasesAsync"));
            Check(Control<TextBlock>(window, "AtlasPreviewText").Text.Contains("1 rooms", StringComparison.Ordinal),
                "Startup did not render the Hacker autosave from the active profile.");

            var rows = Control<ListBox>(window, "AtlasStatesList");
            var stateName = Control<TextBox>(window, "AtlasStateNameText");
            var load = Control<Button>(window, "AtlasLoadStateButton");

            OpenManager(window, "Gameplay");
            Check(RowNames(rows).SequenceEqual(["Atlas1 · Auto Save"]) && !load.IsEnabled,
                "Gameplay should start with its autosave selected.");
            stateName.Text = "Gameplay run";
            Click(window, "AtlasSaveStateButton");
            Check(gameplayStates.List().Single().Name == "Gameplay run" && hackerStates.ListStates().Count == 0,
                "Gameplay Save State wrote to the wrong library.");
            Click(window, "AtlasNewButton");
            Check(VisionResetController.ReadPersistedAtlas(activeWorld).Metadata.RegistrationCount == 0
                && gameplayStates.List().Count == 2, "Gameplay New did not archive the previous world.");
            Select(rows, "Gameplay run");
            Check(load.IsEnabled, "A named Gameplay state should enable Load.");
            Click(window, "AtlasLoadStateButton");
            Check(VisionResetController.ReadPersistedAtlas(activeWorld).Metadata.RegistrationCount == 7
                && SaveProfileAtlas.Read(activeWorld) == profile, "Gameplay Load did not restore the current playthrough.");
            Select(rows, "Gameplay run");
            Wait(InvokeTask(window, "DeleteAtlasSelectionAsync", rows.SelectedItem!));
            Check(!gameplayStates.List().Any(state => state.Name == "Gameplay run") && gameplayStates.List().Count == 2
                && hackerStates.Read(profile)?.RegistrationCount == 3, "Gameplay Delete affected a different state.");

            OpenManager(window, "Hacker");
            Check(RowNames(rows).SequenceEqual(["Atlas1 · Auto Save"]) && !load.IsEnabled,
                "The Hacker settings list must have its own library.");
            stateName.Text = "Hacker route";
            Click(window, "AtlasSaveStateButton");
            var namedHacker = hackerStates.ListStates().Single();
            Check(namedHacker.Name == "Hacker route" && hackerStates.LoadState(namedHacker).Rooms.Single().Id
                == initialHacker.Rooms.Single().Id, "The Hacker window did not save its rendered room.");
            Click(window, "AtlasNewButton");
            Check(hackerStates.Read(profile)?.Rooms.Count == 0 && hackerStates.ListStates().Count == 2,
                "Hacker New did not keep the previous autosave.");
            Select(rows, "Hacker route");
            Click(window, "AtlasLoadStateButton");
            Check(hackerStates.Read(profile)?.Rooms.Single().Id == initialHacker.Rooms.Single().Id
                && VisionResetController.ReadPersistedAtlas(activeWorld).Metadata.RegistrationCount == 7,
                "Hacker Load affected Gameplay or did not restore the map.");
            Check(Control<TextBlock>(window, "AtlasPreviewText").Text.Contains("1 rooms", StringComparison.Ordinal),
                "Hacker Load did not publish its restored layout.");
            Select(rows, "Hacker route");
            Wait(InvokeTask(window, "DeleteAtlasSelectionAsync", rows.SelectedItem!));
            Check(!hackerStates.ListStates().Any(state => state.Id == namedHacker.Id)
                && hackerStates.ListStates().Count == 2, "Hacker Delete did not retain the recovery snapshots.");

            SetField(window, "hackerLayoutRevision", 1L);
            Wait(InvokeTask(window, "PersistHackerAtlasPeriodicallyAsync"));
            Check(hackerStates.Read(profile)?.Rooms.Single().Id == initialHacker.Rooms.Single().Id,
                "Periodic autosave did not preserve the loaded Hacker map.");

            var fresh = ConfirmLaunch(window, slot: 2, occupied: []);
            Check(fresh == new ProfileLaunch(2, true), "The live window did not confirm a measured empty slot.");
            Check((bool)Wait(InvokeTask(window, "ActivateGameplayProfileAtlasAsync", fresh))!,
                "The paired Atlas switch failed.");
            var next = SaveProfileAtlas.Read(activeWorld);
            Check(next is { Slot: 2 } && hackerStates.Read(next) is null && hackerStates.Read(profile)?.Rooms.Count == 1
                && VisionResetController.ReadPersistedAtlas(activeWorld).Metadata.RegistrationCount == 0,
                "Switching to a fresh save must keep the previous maps and reveal empty ones.");
            Check(RowNames(rows).First() == "Atlas2 · Auto Save", "The manager did not display the new playthrough.");

            var returning = ConfirmLaunch(window, slot: 1, occupied: [1, 2]);
            Check(returning == new ProfileLaunch(1, false), "The live window did not confirm a measured occupied slot.");
            Check((bool)Wait(InvokeTask(window, "ActivateGameplayProfileAtlasAsync", returning))!
                && SaveProfileAtlas.Read(activeWorld) == profile
                && VisionResetController.ReadPersistedAtlas(activeWorld).Metadata.RegistrationCount == 7
                && Control<TextBlock>(window, "AtlasPreviewText").Text.Contains("1 rooms", StringComparison.Ordinal),
                "Returning to a saved profile did not restore both atlases.");

            rows.SelectedIndex = 0;
            var count = hackerStates.ListStates().Count;
            Wait(InvokeTask(window, "DeleteAtlasSelectionAsync", rows.SelectedItem!));
            Check(hackerStates.Read(profile)?.Rooms.Count == 0 && hackerStates.ListStates().Count == count
                && VisionResetController.ReadPersistedAtlas(activeWorld).Metadata.RegistrationCount == 7,
                "Deleting the Hacker autosave must clear only that active map.");

            VerifyGameplayPlacementVisibility(window);
        }
        finally
        {
            SetField(window, "isClosing", true);
            Wait(InvokeTask(window, "PrepareShutdownAsync"));
        }
    }

    private static void VerifyGameplayPlacementVisibility(MainWindow window)
    {
        var layer = Control<Canvas>(window, "GameplayPlacementCanvas");
        var outlines = Control<CheckBox>(window, "EstimatedRoomOutlinesCheck");
        var tiles = (Dictionary<AtlasTileCoordinate, Image>)typeof(MainWindow)
            .GetField("gameplayTileImages", InstanceMembers)!.GetValue(window)!;
        tiles.Add(new(0, 0), new Image());
        try
        {
            SetField(window, "workspace", "Gameplay");
            SetField(window, "gameplaySavedWorld", LiveWorldGroundProjection.Empty with
            {
                Placement = new(
                    [new(new(30, -12), new(60, -12)), new(new(50, -12), new(50, -60))],
                    [new(new(40, -26), new(28, 20))])
            });
            SetField(window, "gameplayAtlasMinimumTileX", -1);
            _ = Invoke(window, "UpdateGameplayPathVisibility");
            var join = layer.Children.OfType<Line>().Single();
            Check(outlines.IsChecked == false && layer.Visibility == Visibility.Visible && !layer.IsHitTestVisible
                && layer.Children.Count == 1 && join.X1 == 286 && join.X2 == 316 && join.Y1 == 12 && join.Y2 == 12,
                "A saved Atlas should show its measured horizontal join with the tile origin, without vertical lines or outlines.");

            outlines.IsChecked = true;
            var box = layer.Children.OfType<Rectangle>().Single();
            Check(Canvas.GetLeft(box) == 296 && Canvas.GetTop(box) == 6 && box.Width == 28 && box.Height == 20
                && box.StrokeDashArray.Count == 2 && layer.Children.OfType<Line>().Count() == 1,
                "The Debug View toggle should add only the dashed footprint.");
            outlines.IsChecked = false;
            Check(layer.Children.Count == 1, "Turning outlines off must keep the measured join.");

            SetField(window, "hasFreshGameFrame", true);
            _ = Invoke(window, "UpdateGameplayPathVisibility");
            Check(layer.Visibility == Visibility.Collapsed, "A fresh capture awaiting gameplay evidence must hide the saved overlay.");
            SetField(window, "gameplayFrameRegistered", true);
            SetField(window, "gameplayPlacementFrame", (15L, 3d));
            _ = Invoke(window, "UpdateGameplayPathVisibility");
            Check(layer.Visibility == Visibility.Visible, "A registered gameplay result must show the measured overlay.");

            var evidence = (AtlasPathFrameEvidence)typeof(MainWindow)
                .GetField("atlasPathEvidence", InstanceMembers)!.GetValue(window)!;
            evidence.ObserveGameplayPreview(16, false);
            _ = Invoke(window, "UpdateGameplayPathVisibility");
            Check(layer.Visibility == Visibility.Collapsed, "A loading or menu frame must hide the previous gameplay overlay immediately.");
            SetField(window, "gameplayPlacementFrame", (17L, 4d));
            evidence.ObserveReceiver(Guid.NewGuid(), false, 4.1);
            _ = Invoke(window, "RefreshAtlasPathVisibility");
            Check(layer.Visibility == Visibility.Collapsed, "Receiver menus must hide the overlay even without a newer gameplay image.");
        }
        finally
        {
            tiles.Clear();
            _ = Invoke(window, "ClearGameplayAtlasRuntime", false);
        }
    }

    private static ProfileLaunch ConfirmLaunch(MainWindow window, int slot, int[] occupied)
    {
        MenuStencilMatch[] pair =
        [
            new("selector", "Selector", new(10, 20, 8, 8), 1, null),
            new("selector", "Selector", new(50, 20, 8, 8), 1, null)
        ];
        var first = new MenuStencilResult("select-profile", true, 1, $"Slot {slot}", [], pair, [],
            0, 0, 1, MenuStencilPhase.Tracking, 0, 1, "en")
        {
            SelectedOptionIdentifier = $"select-profile.slot-{slot}",
            CheckedProfileSlots = [1, 2, 3, 4],
            OccupiedProfileSlots = occupied
        };
        var generation = 10L + slot;
        _ = Invoke(window, "ObserveProfileAtlas", first, false, 10d, generation);
        _ = Invoke(window, "ObserveProfileAtlas", first with { SourceTimestamp = 1.2 }, false, 10.2d, generation);
        _ = Invoke(window, "ObserveProfileAtlas", null, true, 11d, generation);
        return (ProfileLaunch)(Invoke(window, "ObserveProfileAtlas", null, true, 11.2d, generation)
            ?? throw new InvalidOperationException("The fresh Gameplay HUD did not confirm a profile."));
    }

    private static HackerAtlasArchive SampleHackerArchive()
    {
        const int size = LiveTiledAtlas.TileSize;
        const int stride = size * 4;
        var pixels = Enumerable.Repeat((byte)164, size * stride).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var raster = new BgraFrame(size, size, stride, pixels, 1, DateTimeOffset.UnixEpoch);
        var room = new HackerAtlasArchivedRoom(Guid.NewGuid(), "Tutorial_01", new(-12, 8), new(12, 24),
            new(new(20, 20), new(64, 48)), new(0, 0, 192, 128), [new(new(0, 0), raster)]);
        return new([room], [], new Dictionary<string, AtlasPoint> { ["Tutorial_01"] = new(7, -3) }, 3, DateTimeOffset.UtcNow);
    }

    private static void OpenManager(MainWindow window, string workspace)
    {
        // Live Hacker workspace selection is gated by receiver telemetry.
        // Set that precondition without opening a receiver connection.
        SetField(window, "workspace", workspace);
        Wait(InvokeTask(window, "RefreshAtlasManagerAsync"));
    }

    private static void Click(MainWindow window, string name)
    {
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (typeof(MainWindow).GetField("atlasOperationCompletion", InstanceMembers)?.GetValue(window) is Task operation)
            Wait(operation);
    }

    private static IEnumerable<string> RowNames(ListBox rows) =>
        rows.Items.Cast<object>().Select(row =>
            (string)(row.GetType().GetProperty("Name")?.GetValue(row)
                ?? throw new InvalidOperationException("The Atlas row has no display name.")));

    private static void Select(ListBox rows, string name) =>
        rows.SelectedIndex = RowNames(rows).ToList().IndexOf(name);

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing {name}.");

    private static void SetField(MainWindow window, string name, object value) =>
        (typeof(MainWindow).GetField(name, InstanceMembers)
            ?? throw new MissingFieldException(nameof(MainWindow), name)).SetValue(window, value);

    private static object? Invoke(MainWindow window, string method, params object?[] args) =>
        (typeof(MainWindow).GetMethod(method, InstanceMembers)
            ?? throw new MissingMethodException(nameof(MainWindow), method)).Invoke(window, args);

    private static Task InvokeTask(MainWindow window, string method, params object?[] args) =>
        (Task)(Invoke(window, method, args) ?? throw new InvalidOperationException($"{method} returned no task."));

    private static object? Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = OperationTimeout };
            timer.Tick += (_, _) => frame.Continue = false;
            task.GetAwaiter().OnCompleted(() => frame.Continue = false);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        Check(task.IsCompleted, "Atlas window operation timed out.");
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
