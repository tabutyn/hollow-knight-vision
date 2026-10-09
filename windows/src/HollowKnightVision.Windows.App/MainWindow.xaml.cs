using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow : Window
{
    private const double CanvasWidth = 640;
    private const double CanvasHeight = 360;
    private readonly ObservableCollection<SavedLabelingExample> examples = [];
    private readonly ObservableCollection<EditableBox> boxes = [];
    private SavedLabelingExample? currentExample;
    private BgraFrame? currentFrame;
    private Guid captureGroupIdentifier = Guid.NewGuid();
    private Point? dragStart;
    private Rectangle? dragPreview;
    private CancellationTokenSource? visionCancellation;
    private CancellationTokenSource? startupCancellation;
    private readonly WindowsGameControlForwarder gameControls = new();
    private readonly DispatcherTimer inputHeartbeat;
    private readonly HashSet<Key> forwardedKeys = [];
    private readonly GroundTruthAtlasMosaic gameplayAtlas = new();
    private readonly GroundTruthAtlasMosaic hackerAtlas = new();
    private readonly Dictionary<AtlasTileCoordinate, Image> gameplayTileImages = [];
    private readonly Dictionary<AtlasTileCoordinate, Image> hackerTileImages = [];
    private readonly Stack<IReadOnlyList<EditableBox>> labelUndo = [];
    private readonly List<GroundEditLine> groundEdits = [];
    private readonly List<TrackedGroundLine> trackedGround = [];
    private readonly Stack<IReadOnlyList<GroundEditLine>> groundUndo = [];
    private readonly Dictionary<string, RoomEdit> rooms = new(StringComparer.Ordinal);
    private readonly AtlasStateStore atlasStateStore;
    private readonly UserObjectCatalog objectCatalog;
    private readonly List<ClassChoice> customObjectChoices = [];
    private readonly string activeAtlasDirectory;
    private readonly string hackerEditPath;
    private DateTimeOffset activeAtlasCreatedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset lastAtlasPersistedAt = DateTimeOffset.MinValue;
    private long gameplayPersistedRevision = -1;
    private Task? atlasPersistTask;
    private long gameplayRenderedRevision = -1;
    private long hackerRenderedRevision = -1;
    private LowResolutionMotionGrid? previousMotionGrid;
    private LowResolutionTranslationDiagnostic? latestMotion;
    private volatile bool rawDetectionEnabled;
    private string workspace = "Gameplay";
    private string gameplayFraming = "Current";
    private string hackerInteractionMode = "Fit";
    private bool pointerWithinLiveFrame;
    private bool passingPrimaryMouse;
    private bool isClosing;
    private bool windowZoomed = true;
    private Rect restoredWindowBounds;
    private Point? lastForwardedPointer;
    private Point? viewportPanStart;
    private Matrix viewportPanMatrix = Matrix.Identity;
    private int? hackerAtlasMinimumTileX;
    private int? hackerAtlasMinimumTileY;
    private BgraFrame? hackerLiveFrame;
    private ReceiverGroundTruthSample? hackerLiveTelemetry;
    private BgraFrame? gameplayLiveFrame;
    private ReceiverGroundTruthSample? gameplayLiveTelemetry;
    private int? gameplayAtlasMinimumTileX;
    private int? gameplayAtlasMinimumTileY;
    private string? lastRoomName;
    private GroundEditLine? activeGroundLine;
    private GroundEditLine? groundOriginalLine;
    private Point? groundDragStart;
    private EditableBox? modifyingBox;
    private Point? modifyDragStart;
    private EditableBox? modifyOriginal;
    private bool modifyingResize;
    private RoomEdit? activeRoom;
    private Point? roomDragStart;
    private double roomOriginalOffsetX;
    private double roomOriginalOffsetY;

    private sealed record VisionAnalysisResult(
        IReadOnlyList<ObjectDetection> Detections,
        IReadOnlyList<DetectedFloorLine> Ground,
        AtlasMosaicDelta? GameplayTiles,
        AtlasMosaicDelta? HackerTiles,
        ReceiverGroundTruthSample? Telemetry,
        LowResolutionTranslationDiagnostic? Motion);

    private static readonly string[] MacLabelingContexts =
    [
        "Main Title", "Options", "Game Options", "Audio", "Video", "Screen Scale",
        "Brightness", "Video Advanced Settings", "Controller", "Remap Controller",
        "Controller Advanced Settings", "Keyboard", "Mods", "Achievements", "Extras",
        "Credits", "Hidden Dreams", "The Grimm Troupe", "Lifeblood", "Godmaster",
        "Select Profile", "Clear Save", "Pause", "Quit To Menu", "Inventory", "Gameplay",
        "Quit Game", "Enemies", "World", "Shared"
    ];

    private static readonly ClassChoice[] GameplayClassChoices =
    [
        new("Hallow Knight", "game.playable-knight"),
        new("Mana", "game.mana"),
        new("Health", "game.health"),
        new("Geode", "game.geo"),
        new("Crawlid", "enemies.crawlid"),
        new("Vengfly", "enemies.vengfly"),
        new("Shade", "enemies.shade"),
        new("Geo Deposit", "world.geo-deposit"),
        new("Lifeblood Cacoon", "world.lifeblood-cacoon"),
        new("Sign", "world.sign")
    ];

    public MainWindow()
    {
        InitializeComponent();
        _ = WindowsDpiAwareness.TryEnablePerMonitorV2();
        var visibleFrame = SystemParameters.WorkArea;
        restoredWindowBounds = new Rect(
            visibleFrame.Left + Math.Max(0, (visibleFrame.Width - 1100) / 2),
            visibleFrame.Top + Math.Max(0, (visibleFrame.Height - 760) / 2),
            Math.Min(1100, visibleFrame.Width),
            Math.Min(760, visibleFrame.Height));
        Left = visibleFrame.Left;
        Top = visibleFrame.Top;
        Width = visibleFrame.Width;
        Height = visibleFrame.Height;
        inputHeartbeat = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        inputHeartbeat.Tick += (_, _) => gameControls.Heartbeat();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        PreviewGotKeyboardFocus += MainWindow_PreviewGotKeyboardFocus;
        Deactivated += (_, _) => ReleaseForwardedInput();
        ExamplesList.ItemsSource = examples;
        BoxesList.ItemsSource = boxes;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = IOPath.Combine(local, "HollowKnightVision");
        activeAtlasDirectory = IOPath.Combine(root, "atlas-v1", "active");
        atlasStateStore = new AtlasStateStore(IOPath.Combine(root, "atlas-v1", "states"));
        objectCatalog = new UserObjectCatalog(IOPath.Combine(root, "object-catalog-v1.json"));
        customObjectChoices.AddRange(objectCatalog.Load().Select(item =>
            new ClassChoice(item.Name, item.Identifier)));
        hackerEditPath = IOPath.Combine(root, "atlas-v1", "hacker-edits.json");
        ExamplesRootText.Text = IOPath.Combine(root, "labeling-v1", "examples");
        ModelExamplesRootText.Text = ExamplesRootText.Text;
        DatasetRootText.Text = IOPath.Combine(root, "training-v1", "datasets");
        TrainingOutputText.Text = IOPath.Combine(root, "training-v1", "runs", Guid.NewGuid().ToString("N"));
        VisionRunText.Text = LatestModelRun(IOPath.Combine(root, "training-v1", "runs"))
            ?? BundledModelRun()
            ?? "";
        AtlasOutputText.Text = IOPath.Combine(
            documents,
            "HollowKnightVision",
            "atlases",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var windowsRoot = FindWindowsRoot();
        var latestAtlas = LatestAtlas(
            IOPath.Combine(documents, "HollowKnightVision", "atlases"),
            windowsRoot is null ? null : IOPath.Combine(windowsRoot, "artifacts", "live-tests"));
        VisualRouteText.Text = IOPath.Combine(
            documents,
            "HollowKnightVision",
            "routes",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        InputPathText.Text = "";
        ContextCombo.ItemsSource = MacLabelingContexts;
        ContextCombo.SelectedItem = "Gameplay";
        ClassCombo.ItemsSource = AvailableClassChoices("Gameplay");
        ClassCombo.SelectedIndex = 0;
        ClassIdentifierText.Text = GameplayClassChoices[0].Identifier;
        LoadPersistentAtlas();
        LoadHackerEdits();
        RefreshExamples();
        RefreshOps();
        Loaded += async (_, _) =>
        {
            gameControls.Start();
            inputHeartbeat.Start();
            if (latestAtlas is not null)
            {
                try
                {
                    LoadAtlasPreview(latestAtlas);
                }
                catch (Exception error)
                {
                    SetStatus($"Latest atlas preview failed: {error.Message}");
                }
            }
            SelectWorkspace("Gameplay");
            await EnsureGameAndVisionAsync();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        isClosing = true;
        startupCancellation?.Cancel();
        inputHeartbeat.Stop();
        ReleaseForwardedInput();
        PersistActiveAtlas(force: true);
        PersistHackerEdits();
        gameControls.DisposeAsync().AsTask().GetAwaiter().GetResult();
        StopVision();
        base.OnClosed(e);
    }

    private async Task EnsureGameAndVisionAsync()
    {
        if (startupCancellation is not null) return;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        startupCancellation = cancellation;
        try
        {
            var window = HollowKnightWindowLocator.FindBest();
            if (window is null)
            {
                var runningGame = Process.GetProcessesByName("hollow_knight");
                try
                {
                    if (runningGame.Length == 0)
                    {
                        _ = HollowKnightGameLauncher.Start();
                    }
                }
                finally
                {
                    foreach (var process in runningGame) process.Dispose();
                }
                window = await WaitForGameWindowAsync(cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (visionCancellation is null)
            {
                StartVision_Click(this, new RoutedEventArgs());
            }
            Activate();
            Focus();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!isClosing)
            {
                ShowError(new TimeoutException("Hollow Knight did not open within two minutes."));
            }
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            if (ReferenceEquals(startupCancellation, cancellation)) startupCancellation = null;
            cancellation.Dispose();
        }
    }

    private static async Task<HollowKnightWindow> WaitForGameWindowAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = HollowKnightWindowLocator.FindBest();
            if (window is not null) return window;
            await Task.Delay(250, cancellationToken);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsActive) return;
        if (FirstResponderConsumesTextInput()) return;
        if (workspace == "Label" && key == Key.Z
            && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            UndoLabelEdit();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return;
        }
        if (!e.IsRepeat && HandleLocalShortcut(key))
        {
            e.Handled = true;
            return;
        }
        if (workspace == "Label") return;
        if (workspace == "Model" && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.X)
        {
            return;
        }
        if (!TryMapGameButton(key, out var button)) return;
        if (button is InputButtons.Inventory or InputButtons.PauseMenu
            && !gameControls.MenuShortcutsAvailable)
        {
            return;
        }
        e.Handled = true;
        if (e.IsRepeat || !forwardedKeys.Add(key)) return;
        gameControls.Press(button);
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsActive && !forwardedKeys.Contains(key)) return;
        if (!TryMapGameButton(key, out var button)) return;
        if (!forwardedKeys.Remove(key))
        {
            if (FirstResponderConsumesTextInput()) return;
            if (workspace == "Label"
                || workspace == "Model" && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.X)
            {
                return;
            }
            if (button is InputButtons.Inventory or InputButtons.PauseMenu
                && !gameControls.MenuShortcutsAvailable)
            {
                return;
            }
        }
        gameControls.Release(button);
        e.Handled = true;
    }

    private void MainWindow_PreviewGotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is TextBoxBase or PasswordBox or ComboBox)
        {
            ReleaseForwardedInput();
        }
    }

    private bool HandleLocalShortcut(Key key)
    {
        if (workspace == "Label")
        {
            var mode = key switch
            {
                Key.D1 or Key.NumPad1 => LabelAddButton,
                Key.D2 or Key.NumPad2 => LabelModifyButton,
                Key.D3 or Key.NumPad3 => LabelDeleteButton,
                Key.D4 or Key.NumPad4 => LabelNegativeButton,
                _ => null
            };
            if (mode is not null)
            {
                mode.IsChecked = true;
                LabelMode_Click(mode, new RoutedEventArgs());
                return true;
            }
            if (key == Key.R)
            {
                RecentFrames_Click(this, new RoutedEventArgs());
                return true;
            }
        }
        if (key == Key.X && workspace is "Label" or "Model")
        {
            SelectWorkspace("Gameplay");
            return true;
        }
        var destination = key switch
        {
            Key.G => "Gameplay",
            Key.L => "Label",
            Key.M => "Model",
            Key.H => "Hacker",
            Key.O => "Ops",
            _ => null
        };
        if (destination is not null)
        {
            if (workspace == destination && destination != "Gameplay")
            {
                destination = "Gameplay";
            }
            SelectWorkspace(destination);
            return true;
        }
        if (key == Key.K && workspace is "Gameplay" or "Hacker")
        {
            PathRecording_Click(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    private static bool TryMapGameButton(Key key, out InputButtons button)
    {
        button = key switch
        {
            Key.Left => InputButtons.Left,
            Key.Right => InputButtons.Right,
            Key.Down => InputButtons.Down,
            Key.Up => InputButtons.Up,
            Key.A => InputButtons.ActionA,
            Key.Z => InputButtons.ActionZ,
            Key.X => InputButtons.ActionX,
            Key.I => InputButtons.Inventory,
            Key.P => InputButtons.PauseMenu,
            _ => InputButtons.None
        };
        return button != InputButtons.None;
    }

    private static bool FirstResponderConsumesTextInput() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox;

    private void ReleaseForwardedInput()
    {
        forwardedKeys.Clear();
        gameControls.ReleaseAll();
        if (passingPrimaryMouse)
        {
            passingPrimaryMouse = false;
            Mouse.Capture(null);
        }
        SendPointerExit();
    }

    private void GameViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (workspace == "Hacker" && hackerInteractionMode == "Ground")
        {
            GroundEditorMouseDown(e);
            return;
        }
        if (workspace == "Hacker" && hackerInteractionMode == "Rooms")
        {
            RoomEditorMouseDown(e);
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0
            && (workspace == "Gameplay" ? gameplayFraming : hackerInteractionMode) == "Free Fly")
        {
            var surface = workspace == "Gameplay" ? GameplayAtlasCanvas : AtlasCanvas;
            viewportPanStart = e.GetPosition((IInputElement)sender);
            viewportPanMatrix = (surface.RenderTransform as MatrixTransform)?.Matrix ?? Matrix.Identity;
            Mouse.Capture((IInputElement)sender, CaptureMode.Element);
            e.Handled = true;
            return;
        }
        if (!ForwardsGamePointer()) return;
        var normalized = NormalizedGamePointer(e);
        if (normalized is null)
        {
            SendPointerExit();
            return;
        }
        if (!gameControls.ForwardPointer(
                "leftDown", normalized.Value.X, normalized.Value.Y, e.ClickCount)) return;
        pointerWithinLiveFrame = true;
        lastForwardedPointer = normalized;
        passingPrimaryMouse = true;
        Mouse.Capture((IInputElement)sender, CaptureMode.Element);
        e.Handled = true;
    }

    private void GameViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (workspace == "Hacker" && hackerInteractionMode == "Ground" && groundDragStart is not null)
        {
            GroundEditorMouseMove(e);
            return;
        }
        if (workspace == "Hacker" && hackerInteractionMode == "Rooms"
            && activeRoom is not null && roomDragStart is { } roomStart
            && e.LeftButton == MouseButtonState.Pressed)
        {
            var point = ToAtlasGlobal(e.GetPosition(AtlasCanvas));
            activeRoom.OffsetX = roomOriginalOffsetX + point.X - roomStart.X;
            activeRoom.OffsetY = roomOriginalOffsetY + point.Y - roomStart.Y;
            RedrawHackerEdits();
            e.Handled = true;
            return;
        }
        if (viewportPanStart is { } panStart && e.LeftButton == MouseButtonState.Pressed)
        {
            var current = e.GetPosition((IInputElement)sender);
            var matrix = viewportPanMatrix;
            matrix.OffsetX += current.X - panStart.X;
            matrix.OffsetY += current.Y - panStart.Y;
            (workspace == "Gameplay" ? GameplayAtlasCanvas : AtlasCanvas).RenderTransform =
                new MatrixTransform(matrix);
            e.Handled = true;
            return;
        }
        if (!ForwardsGamePointer())
        {
            SendPointerExit();
            return;
        }
        var normalized = NormalizedGamePointer(e);
        if (normalized is null)
        {
            SendPointerExit();
            return;
        }
        var kind = passingPrimaryMouse && e.LeftButton == MouseButtonState.Pressed
            ? "leftDragged" : "moved";
        if (gameControls.ForwardPointer(kind, normalized.Value.X, normalized.Value.Y))
        {
            pointerWithinLiveFrame = true;
            lastForwardedPointer = normalized;
        }
    }

    private void GameViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (workspace == "Hacker" && hackerInteractionMode == "Ground" && groundDragStart is not null)
        {
            GroundEditorMouseUp(e);
            return;
        }
        if (activeRoom is not null && roomDragStart is not null)
        {
            activeRoom = null;
            roomDragStart = null;
            Mouse.Capture(null);
            PersistHackerEdits();
            e.Handled = true;
            return;
        }
        if (viewportPanStart is not null)
        {
            viewportPanStart = null;
            Mouse.Capture(null);
            e.Handled = true;
            return;
        }
        if (!passingPrimaryMouse) return;
        var normalized = NormalizedGamePointer(e) ?? lastForwardedPointer;
        if (normalized is not null)
        {
            _ = gameControls.ForwardPointer(
                "leftUp", normalized.Value.X, normalized.Value.Y, e.ClickCount);
        }
        passingPrimaryMouse = false;
        Mouse.Capture(null);
        e.Handled = true;
    }

    private void GameViewport_MouseLeave(object sender, MouseEventArgs e) => SendPointerExit();

    private bool ForwardsGamePointer() => workspace switch
    {
        "Gameplay" => gameplayFraming != "Free Fly",
        "Hacker" => hackerInteractionMode is "Fit" or "Current",
        _ => false
    };

    private Point? NormalizedGamePointer(MouseEventArgs e)
    {
        FrameworkElement liveFrame = workspace == "Hacker" ? HackerLiveImage : GameplayImage;
        if (!liveFrame.IsVisible || liveFrame.ActualWidth <= 0 || liveFrame.ActualHeight <= 0)
        {
            return null;
        }
        var point = e.GetPosition(liveFrame);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || point.X < 0 || point.Y < 0
            || point.X > liveFrame.ActualWidth || point.Y > liveFrame.ActualHeight)
        {
            return null;
        }
        return new Point(
            point.X / liveFrame.ActualWidth,
            1 - point.Y / liveFrame.ActualHeight);
    }

    private void AtlasViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((workspace == "Gameplay" ? gameplayFraming : hackerInteractionMode) != "Free Fly") return;
        var surface = workspace == "Gameplay" ? GameplayAtlasCanvas : AtlasCanvas;
        var host = workspace == "Gameplay" ? GameplayPanel : HackerViewportPanel;
        var point = e.GetPosition(host);
        var matrix = (surface.RenderTransform as MatrixTransform)?.Matrix ?? Matrix.Identity;
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        var nextScale = Math.Clamp(matrix.M11 * factor, 0.05, 12);
        factor = nextScale / Math.Max(0.0001, matrix.M11);
        matrix.ScaleAt(factor, factor, point.X, point.Y);
        surface.RenderTransform = new MatrixTransform(matrix);
        e.Handled = true;
    }

    private void SendPointerExit()
    {
        if (!pointerWithinLiveFrame) return;
        _ = gameControls.ForwardPointer("exited", 0, 0);
        pointerWithinLiveFrame = false;
        if (!passingPrimaryMouse) lastForwardedPointer = null;
    }

    private void GameplayWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SelectWorkspace("Gameplay");
    }

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowZoom_Click(object sender, RoutedEventArgs e)
    {
        if (windowZoomed)
        {
            Left = restoredWindowBounds.Left;
            Top = restoredWindowBounds.Top;
            Width = restoredWindowBounds.Width;
            Height = restoredWindowBounds.Height;
            windowZoomed = false;
            WindowZoomButton.Content = "\uE922";
            WindowZoomButton.ToolTip = "Maximize";
            return;
        }

        restoredWindowBounds = new Rect(Left, Top, Width, Height);
        var visibleFrame = SystemParameters.WorkArea;
        Left = visibleFrame.Left;
        Top = visibleFrame.Top;
        Width = visibleFrame.Width;
        Height = visibleFrame.Height;
        windowZoomed = true;
        WindowZoomButton.Content = "\uE923";
        WindowZoomButton.ToolTip = "Restore Down";
    }

    private void WindowTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowZoom_Click(sender, e);
            return;
        }

        if (!windowZoomed && e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void LabelWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SelectWorkspace("Label");
    }

    private void ModelWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SelectWorkspace("Model");
    }

    private void HackerWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SelectWorkspace("Hacker");
    }

    private void OpsWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SelectWorkspace("Ops");
    }

    private void SelectWorkspace(string destination)
    {
        var leavingLabel = workspace == "Label" && destination != "Label";
        if (destination is "Label" or "Model") ReleaseForwardedInput();
        if (destination is not ("Gameplay" or "Hacker")) SendPointerExit();
        workspace = destination;
        GameplayPanel.Visibility = destination == "Gameplay" ? Visibility.Visible : Visibility.Collapsed;
        LabelPanel.Visibility = destination == "Label" ? Visibility.Visible : Visibility.Collapsed;
        ModelPanel.Visibility = destination == "Model" ? Visibility.Visible : Visibility.Collapsed;
        HackerPanel.Visibility = destination == "Hacker" ? Visibility.Visible : Visibility.Collapsed;
        OpsPanel.Visibility = destination == "Ops" ? Visibility.Visible : Visibility.Collapsed;
        GameplayControlsPanel.Visibility = destination == "Gameplay"
            ? Visibility.Visible : Visibility.Collapsed;
        HackerControlsPanel.Visibility = destination == "Hacker"
            ? Visibility.Visible : Visibility.Collapsed;
        PathRecordingButton.Visibility = destination is "Gameplay" or "Hacker"
            ? Visibility.Visible : Visibility.Collapsed;

        GameplayWorkspaceButton.IsChecked = destination == "Gameplay";
        LabelWorkspaceButton.IsChecked = destination == "Label";
        ModelWorkspaceButton.IsChecked = destination == "Model";
        HackerWorkspaceButton.IsChecked = destination == "Hacker";
        OpsWorkspaceButton.IsChecked = destination == "Ops";

        if (destination == "Label") CaptureFrame_Click(this, new RoutedEventArgs());
        if (destination == "Model") RefreshModelWorkspace();
        if (destination == "Ops") RefreshOps();
        if (destination == "Hacker") RedrawHackerEdits();
        if (leavingLabel && OpsRandomizeAfterLabelCheck.IsChecked == true)
        {
            OpsRandomize_Click(this, new RoutedEventArgs());
        }
    }

    private void ContextCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClassCombo is null) return;
        var context = ContextCombo.SelectedItem as string;
        ClassCombo.ItemsSource = AvailableClassChoices(context);
        ClassCombo.SelectedIndex = 0;
    }

    private ClassChoice[] AvailableClassChoices(string? context)
    {
        var prefix = context switch
        {
            "Enemies" => "enemies.",
            "World" => "world.",
            _ => null
        };
        return GameplayClassChoices.Concat(customObjectChoices)
            .Where(choice => prefix is null || choice.Identifier.StartsWith(prefix, StringComparison.Ordinal))
            .DistinctBy(choice => choice.Identifier, StringComparer.Ordinal)
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void NewObject_Click(object sender, RoutedEventArgs e)
    {
        var context = ContextCombo.Text;
        var group = context switch
        {
            "Enemies" => "enemies",
            "World" => "world",
            "Gameplay" => "game",
            _ => null
        };
        if (group is null)
        {
            SetStatus("Choose Gameplay, Enemies, or World before adding an object.");
            return;
        }
        var name = PromptForObjectName(group);
        if (name is null) return;
        try
        {
            var added = objectCatalog.Add(name, group);
            if (customObjectChoices.All(item => item.Identifier != added.Identifier))
            {
                customObjectChoices.Add(new ClassChoice(added.Name, added.Identifier));
            }
            ClassCombo.ItemsSource = AvailableClassChoices(context);
            ClassCombo.SelectedItem = ((IEnumerable<ClassChoice>)ClassCombo.ItemsSource)
                .First(item => item.Identifier == added.Identifier);
            ClassIdentifierText.Text = added.Identifier;
            SetStatus($"Added {added.Name}. Draw boxes and train when ready.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private string? PromptForObjectName(string group)
    {
        var field = new TextBox { MinWidth = 280, Margin = new Thickness(12) };
        var dialog = new Window
        {
            Title = $"New {group} object",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize
        };
        var confirm = new Button { Content = "Add", IsDefault = true, MinWidth = 80, Margin = new Thickness(4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(4) };
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(field.Text)) return;
            dialog.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Object name", Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(field);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => field.Focus();
        return dialog.ShowDialog() == true ? field.Text.Trim() : null;
    }

    private void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClassIdentifierText is null) return;
        if (ClassCombo.SelectedItem is ClassChoice choice)
        {
            ClassIdentifierText.Text = choice.Identifier;
            if (LabelModifyButton.IsChecked == true
                && BoxesList.SelectedItem is EditableBox selected)
            {
                PushLabelUndo();
                ReplaceBox(selected, selected with { ClassIdentifier = choice.Identifier });
                PersistLabelDraft();
            }
        }
    }

    private void LabelMode_Click(object sender, RoutedEventArgs e)
    {
        HardNegativeCheck.IsChecked = LabelNegativeButton.IsChecked == true;
    }

    private void ModelTab_Click(object sender, RoutedEventArgs e)
    {
        ModelSummaryPanel.Visibility = ModelSummaryButton.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        ModelObjectsPanel.Visibility = ModelObjectsButton.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        ModelScreenshotsPanel.Visibility = ModelScreenshotsButton.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MacTrain_Click(object sender, RoutedEventArgs e)
    {
        var classes = examples
            .SelectMany(example => example.Manifest.Annotations)
            .Where(annotation => !annotation.IsHardNegative)
            .Select(annotation => annotation.ClassIdentifier)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (classes.Length == 0) return;
        ExportClassesText.Text = string.Join(Environment.NewLine, classes);
        ExportDataset_Click(sender, e);
        if (Directory.Exists(LatestDatasetText.Text)) await RunTrainerAsync(validateOnly: false);
    }

    private void DebugViewCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (DebugPopup is null) return;
        DebugPopup.IsOpen = DebugViewCheck.IsChecked == true;
    }

    private void ObjectDetectionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ObjectDetectionOptions is null) return;
        ObjectDetectionOptions.Visibility = ObjectDetectionCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RawDetectionCheck_Changed(object sender, RoutedEventArgs e) =>
        rawDetectionEnabled = RawDetectionCheck.IsChecked == true;

    private void ManageAtlas_Click(object sender, RoutedEventArgs e)
    {
        var hacker = workspace == "Hacker";
        GameplayAtlasManager.Visibility = hacker ? Visibility.Collapsed : Visibility.Visible;
        HackerAtlasManager.Visibility = hacker ? Visibility.Visible : Visibility.Collapsed;
        if (!hacker) RefreshAtlasManager();
        AtlasManagerOverlay.Visibility = Visibility.Visible;
    }

    private void AtlasManagerDone_Click(object sender, RoutedEventArgs e) =>
        AtlasManagerOverlay.Visibility = Visibility.Collapsed;

    private void GameplayFraming_Click(object sender, RoutedEventArgs e)
    {
        gameplayFraming = (sender as Button)?.Content?.ToString() ?? gameplayFraming;
        if (gameplayFraming == "Free Fly") SendPointerExit();
        ApplyViewport(GameplayAtlasCanvas, GameplayPanel, gameplayFraming, GameplayImage);
    }

    private void HackerMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = (sender as Button)?.Content?.ToString();
        hackerInteractionMode = mode ?? hackerInteractionMode;
        HackerGroundToolbar.Visibility = mode == "Ground"
            ? Visibility.Visible : Visibility.Collapsed;
        if (mode is "Ground" or "Rooms" or "Free Fly") SendPointerExit();
        RedrawHackerEdits();
        ApplyViewport(AtlasCanvas, HackerViewportPanel, hackerInteractionMode, HackerLiveImage);
    }

    private void PathRecording_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!gameControls.IsRecording)
            {
                if (!gameControls.StartRecording())
                {
                    SetStatus("Updated game receiver is not ready.");
                    return;
                }
                PathRecordingButton.Background = Brushes.Firebrick;
                SetStatus("Recording input path. Press K to stop.");
                return;
            }
            var path = gameControls.StopRecording();
            if (path is null) return;
            var directory = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "HollowKnightVision", "paths");
            Directory.CreateDirectory(directory);
            var output = IOPath.Combine(
                directory,
                $"path-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            new RecordedInputPathStore().Save(path, output);
            InputPathText.Text = output;
            PathRecordingButton.ClearValue(BackgroundProperty);
            SetStatus($"Input path saved: {output}");
        }
        catch (Exception error)
        {
            PathRecordingButton.ClearValue(BackgroundProperty);
            ShowError(error);
        }
    }

    private void RecentFrames_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Owner = this,
            Title = "",
            Width = 760,
            Height = 520,
            MinWidth = 620,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(3, 7, 10)),
            Foreground = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var controls = new DockPanel { LastChildFill = false };
        controls.Children.Add(new TextBlock
        {
            Text = "Recent Frames",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button { Content = "Close", Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right);
        controls.Children.Add(close);
        var resume = new Button { Content = "Resume (L)", Margin = new Thickness(6, 0, 0, 0) };
        resume.Click += (_, _) => window.Close();
        DockPanel.SetDock(resume, Dock.Right);
        controls.Children.Add(resume);
        DockPanel.SetDock(controls, Dock.Top);
        root.Children.Add(controls);
        var list = new ListBox { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var example in examples.OrderByDescending(item => item.Manifest.CreatedAt))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = example };
            row.Children.Add(new Image
            {
                Source = LoadBitmap(example.ImagePath), Width = 160, Height = 90,
                Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 10, 8)
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{example.Manifest.ContextIdentifier}\n{example.Manifest.Annotations.Count} labels",
                VerticalAlignment = VerticalAlignment.Center
            });
            list.Items.Add(row);
        }
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not StackPanel { Tag: SavedLabelingExample selected }) return;
            OpenLabelExample(selected);
            window.Close();
        };
        root.Children.Add(list);
        window.Content = root;
        _ = window.ShowDialog();
    }

    private async void StartGameplay_Click(object sender, RoutedEventArgs e)
    {
        StartGameplayButton.IsEnabled = false;
        SetStatus("Starting gameplay and waiting for receiver…");
        try
        {
            var result = await AutoNavigationRunner.RunAsync(null, 120, 133);
            SetStatus(result == 0 ? "Gameplay verified." : $"Gameplay startup exited {result}.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            StartGameplayButton.IsEnabled = true;
        }
    }

    private async void StartVision_Click(object sender, RoutedEventArgs e)
    {
        var runDirectory = VisionRunText.Text.Trim();
        if (runDirectory.Length > 0 && !Directory.Exists(runDirectory))
        {
            ShowError(new DirectoryNotFoundException(
                "Choose a trained run containing Detector.onnx and training.json."));
            return;
        }
        StopVision();
        var cancellation = new CancellationTokenSource();
        visionCancellation = cancellation;
        StartVisionButton.IsEnabled = false;
        StopVisionButton.IsEnabled = true;
        try
        {
            using var detector = runDirectory.Length == 0
                ? null
                : new OnnxObjectDetector(runDirectory);
            var capture = new GdiWindowCapture();
            var analysisFrames = new LatestFrameSlot();
            using var analysisAvailable = new SemaphoreSlim(0, 1);
            var analysisTask = Task.Run(() => RunVisionAnalysisAsync(
                analysisFrames,
                analysisAvailable,
                detector,
                cancellation.Token));
            var frameId = 0L;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var iteration = Stopwatch.StartNew();
                    var frame = await Task.Run(() =>
                    {
                        var window = HollowKnightWindowLocator.FindBest()
                            ?? throw new InvalidOperationException(
                                "Hollow Knight window not found. Live Vision does not launch it.");
                        return CpuBgraNormalizer.Normalize(capture.Capture(window, ++frameId));
                    }, cancellation.Token);
                    var bitmap = FrameBitmap(frame);
                    if (gameControls.TryGetFreshGroundTruth(
                            TimeSpan.FromMilliseconds(500),
                            out var telemetry))
                    {
                        PresentGameplayFrame(frame, telemetry, bitmap);
                        if (workspace == "Hacker") PresentHackerFrame(frame, telemetry, bitmap);
                    }
                    else
                    {
                        GameplayImage.Source = bitmap;
                        GameplayImage.Width = frame.Width;
                        GameplayImage.Height = frame.Height;
                        Canvas.SetLeft(GameplayImage, 0);
                        Canvas.SetTop(GameplayImage, 0);
                    }
                    analysisFrames.Publish(frame);
                    if (analysisAvailable.CurrentCount == 0)
                    {
                        analysisAvailable.Release();
                    }
                    GameplayPlaceholder.Visibility = Visibility.Collapsed;
                    VisionStatusText.Text = "Gameplay";

                    var remaining = TimeSpan.FromMilliseconds(33) - iteration.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, cancellation.Token);
                    }
                }
            }
            finally
            {
                cancellation.Cancel();
                if (analysisAvailable.CurrentCount == 0) analysisAvailable.Release();
                try
                {
                    await analysisTask;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            GameplayPlaceholder.Visibility = Visibility.Visible;
            VisionStatusText.Text = "Finding State";
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            if (ReferenceEquals(visionCancellation, cancellation))
            {
                visionCancellation = null;
                StartVisionButton.IsEnabled = true;
                StopVisionButton.IsEnabled = false;
            }
            cancellation.Dispose();
        }
    }

    private async Task RunVisionAnalysisAsync(
        LatestFrameSlot frames,
        SemaphoreSlim available,
        OnnxObjectDetector? detector,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!frames.TryTake(out var frame) || frame is null) continue;
            var result = AnalyzeVisionFrame(frame, detector);
            await Dispatcher.InvokeAsync(
                () => ApplyVisionAnalysis(result),
                DispatcherPriority.Render,
                cancellationToken);
        }
    }

    private VisionAnalysisResult AnalyzeVisionFrame(
        BgraFrame frame,
        OnnxObjectDetector? detector)
    {
        var detections = detector?.Detect(frame, rawDetectionEnabled)
            ?? Array.Empty<ObjectDetection>();
        var foreground = detections.Select(DetectionPixelRect).ToArray();
        var ground = GroundLineDetector.SemanticFloorLines(
            GroundLineDetector.Compare(
                GroundLineDetector.Analyze(frame, foreground)),
            GroundTheoryTuning.SemanticDefault);
        var knight = detections
            .Select((detection, index) => (detection, index))
            .FirstOrDefault(item => item.detection.ClassIdentifier.Contains(
                "knight",
                StringComparison.OrdinalIgnoreCase));
        ground = knight.detection is not null
            ? GroundLineDetector.RejectKnownForegroundLines(
                ground, frame.Height, foreground, foreground[knight.index])
            : GroundLineDetector.RejectKnownForegroundLines(
                ground, frame.Height, foreground);

        var grid = LowResolutionMotionGrid.FromFrame(frame);
        var motion = previousMotionGrid is null
            ? null
            : LowResolutionRoomMotionTracker.DiagnoseTranslation(previousMotionGrid, grid);
        previousMotionGrid = grid;
        latestMotion = motion;

        AtlasMosaicDelta? gameplayTiles = null;
        AtlasMosaicDelta? hackerTiles = null;
        ReceiverGroundTruthSample? telemetry = null;
        if (gameControls.TryGetFreshGroundTruth(
                TimeSpan.FromMilliseconds(500), out var sample))
        {
            telemetry = sample;
            var gameplayAccepted = gameplayAtlas.AddFrame(frame, sample);
            var hackerAccepted = workspace == "Hacker"
                && (gameplayAtlas.AcceptedFrameCount == 1
                    || gameplayAtlas.AcceptedFrameCount % 3 == 0)
                && hackerAtlas.AddFrame(frame, sample);
            if (gameplayAccepted && (gameplayAtlas.AcceptedFrameCount == 1
                || gameplayAtlas.AcceptedFrameCount % 3 == 0))
            {
                gameplayTiles = gameplayAtlas.SnapshotSince(
                    Interlocked.Read(ref gameplayRenderedRevision));
            }
            if (hackerAccepted)
            {
                hackerTiles = hackerAtlas.SnapshotSince(
                    Interlocked.Read(ref hackerRenderedRevision));
            }
        }
        return new VisionAnalysisResult(
            detections, ground, gameplayTiles, hackerTiles, telemetry, motion);
    }

    private void ApplyVisionAnalysis(VisionAnalysisResult result)
    {
        DrawDetections(result.Detections, result.Ground);
        if (result.GameplayTiles is not null)
        {
            RenderLiveAtlas(
                GameplayAtlasCanvas,
                gameplayTileImages,
                gameplayAtlas,
                result.GameplayTiles,
                ref gameplayAtlasMinimumTileX,
                ref gameplayAtlasMinimumTileY,
                GameplayImage,
                gameplayLiveFrame,
                gameplayLiveTelemetry);
            Interlocked.Exchange(ref gameplayRenderedRevision, result.GameplayTiles.Revision);
        }
        if (result.HackerTiles is not null)
        {
            RenderLiveAtlas(
                AtlasCanvas,
                hackerTileImages,
                hackerAtlas,
                result.HackerTiles,
                ref hackerAtlasMinimumTileX,
                ref hackerAtlasMinimumTileY,
                HackerLiveImage,
                hackerLiveFrame,
                hackerLiveTelemetry);
            Interlocked.Exchange(ref hackerRenderedRevision, result.HackerTiles.Revision);
        }
        if (result.Telemetry is not null)
        {
            UpdateTrackedGround(result.Ground, result.Telemetry);
            UpdateRoomTopology(result.Telemetry);
        }
        DrawMotionOverlay(result.Motion);
        PersistActiveAtlas(force: false);
    }

    private void StopVision_Click(object sender, RoutedEventArgs e) => StopVision();

    private void StopVision()
    {
        visionCancellation?.Cancel();
        StopVisionButton.IsEnabled = false;
    }

    private void DrawDetections(
        IReadOnlyList<ObjectDetection> detections,
        IReadOnlyList<DetectedFloorLine> groundLines)
    {
        DetectionCanvas.Children.Clear();
        if (ObjectDetectionCheck.IsChecked == true)
        {
            foreach (var detection in detections)
            {
                var box = detection.Box;
                var extend = DetectionExtendSlider.Value / 100;
                var x = Math.Max(0, box.X - box.Width * extend / 2);
                var y = Math.Max(0, box.Y - box.Height * extend / 2);
                var width = Math.Min(1 - x, box.Width * (1 + extend));
                var height = Math.Min(1 - y, box.Height * (1 + extend));
                var rectangle = new Rectangle
                {
                    Width = width * CanvasWidth,
                    Height = height * CanvasHeight,
                    Stroke = RawDetectionCheck.IsChecked == true ? Brushes.Gold : Brushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(
                        DetectionPixelsCheck.IsChecked == true ? (byte)75 : (byte)20,
                        0, 255, 0))
                };
                Canvas.SetLeft(rectangle, x * CanvasWidth);
                Canvas.SetTop(rectangle, y * CanvasHeight);
                DetectionCanvas.Children.Add(rectangle);
                var label = new TextBlock
                {
                    Text = $"{detection.ClassIdentifier} {detection.Confidence:P0}",
                    Background = Brushes.Black,
                    Foreground = Brushes.Lime,
                    FontSize = 11,
                    Padding = new Thickness(2)
                };
                Canvas.SetLeft(label, x * CanvasWidth);
                Canvas.SetTop(label, Math.Max(0, y * CanvasHeight - 19));
                DetectionCanvas.Children.Add(label);
            }
        }
        if (StencilDetectionCheck.IsChecked == true)
        {
            foreach (var area in new[]
            {
                new Rect(0, 0, CanvasWidth, 48),
                new Rect(0, CanvasHeight - 38, CanvasWidth, 38)
            })
            {
                var stencil = new Rectangle
                {
                    Width = area.Width, Height = area.Height,
                    Stroke = Brushes.Magenta, StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(18, 255, 0, 255))
                };
                Canvas.SetLeft(stencil, area.X);
                Canvas.SetTop(stencil, area.Y);
                DetectionCanvas.Children.Add(stencil);
            }
        }
        if (GroundFeaturesCheck.IsChecked == true)
        {
            foreach (var line in groundLines)
            {
                var marker = new Rectangle
                {
                    Width = line.XSpan.EndInclusive - line.XSpan.Start + 1,
                    Height = 3,
                    Fill = Brushes.DeepSkyBlue,
                    ToolTip = $"semantic ground row {line.Row}"
                };
                Canvas.SetLeft(marker, line.XSpan.Start);
                Canvas.SetTop(marker, Math.Max(0, line.Row - 1));
                DetectionCanvas.Children.Add(marker);
            }
        }
    }

    private void DetectionExtendSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DetectionExtendText is not null)
        {
            DetectionExtendText.Text = $"{e.NewValue:F0}%";
        }
    }

    private void UpdateTrackedGround(
        IReadOnlyList<DetectedFloorLine> lines,
        ReceiverGroundTruthSample telemetry)
    {
        if (gameplayLiveFrame is not { } frame) return;
        var scale = telemetry.PixelsPerWorldUnit(frame.Height);
        if (scale is not > 0) return;
        var ppu = gameplayAtlas.PixelsPerWorldUnit;
        foreach (var line in lines)
        {
            var x0 = (telemetry.CameraX + (line.XSpan.Start - frame.Width / 2.0) / scale.Value) * ppu;
            var x1 = (telemetry.CameraX + (line.XSpan.EndInclusive + 1 - frame.Width / 2.0) / scale.Value) * ppu;
            var worldY = telemetry.CameraY - (line.Row - frame.Height / 2.0) / scale.Value;
            var y = -worldY * ppu;
            var existing = trackedGround
                .Where(candidate => Math.Abs(candidate.Y - y) <= 5
                    && Math.Min(candidate.X1, x1) - Math.Max(candidate.X0, x0) >= 8)
                .OrderBy(candidate => Math.Abs(candidate.Y - y))
                .FirstOrDefault();
            if (existing is null)
            {
                trackedGround.Add(new TrackedGroundLine(x0, x1, y));
            }
            else
            {
                existing.X0 = (existing.X0 * existing.Observations + x0) / (existing.Observations + 1);
                existing.X1 = (existing.X1 * existing.Observations + x1) / (existing.Observations + 1);
                existing.Y = (existing.Y * existing.Observations + y) / (existing.Observations + 1);
                existing.Observations++;
            }
        }
        DrawTrackedGround();
    }

    private void DrawTrackedGround()
    {
        GameplayGroundCanvas.Children.Clear();
        if (GroundFeaturesCheck.IsChecked != true
            || gameplayAtlasMinimumTileX is not { } minX
            || gameplayAtlasMinimumTileY is not { } minY) return;
        var originX = minX * gameplayAtlas.TileSize;
        var originY = minY * gameplayAtlas.TileSize;
        foreach (var ground in trackedGround.Where(item => item.Observations >= 3))
        {
            GameplayGroundCanvas.Children.Add(new Line
            {
                X1 = ground.X0 - originX,
                X2 = ground.X1 - originX,
                Y1 = ground.Y - originY,
                Y2 = ground.Y - originY,
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 3,
                ToolTip = $"tracked ground • {ground.Observations} observations"
            });
        }
    }

    private void DrawMotionOverlay(LowResolutionTranslationDiagnostic? diagnostic)
    {
        if (CoarseMotionCheck.IsChecked == true)
        {
            var motion = diagnostic?.Motion;
            var label = new TextBlock
            {
                Text = motion is null
                    ? $"motion: {diagnostic?.FinalRejection?.ToString() ?? "waiting"}"
                    : $"motion {motion.ScreenShiftX:+0.0;-0.0;0},{motion.ScreenShiftY:+0.0;-0.0;0}  {motion.Confidence:P0}",
                Foreground = Brushes.Cyan,
                Background = Brushes.Black,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(3)
            };
            Canvas.SetLeft(label, 10);
            Canvas.SetTop(label, 58);
            DetectionCanvas.Children.Add(label);
            if (motion is not null)
            {
                DetectionCanvas.Children.Add(new Line
                {
                    X1 = 320, Y1 = 180,
                    X2 = 320 + motion.ScreenShiftX * 12,
                    Y2 = 180 + motion.ScreenShiftY * 12,
                    Stroke = Brushes.Cyan, StrokeThickness = 3
                });
            }
        }
        if (TransitionsCheck.IsChecked == true && lastRoomName is not null)
        {
            var label = new TextBlock
            {
                Text = lastRoomName,
                Foreground = Brushes.Violet,
                Background = Brushes.Black,
                FontFamily = new FontFamily("Consolas"), Padding = new Thickness(3)
            };
            Canvas.SetLeft(label, 10);
            Canvas.SetTop(label, 86);
            DetectionCanvas.Children.Add(label);
        }
    }

    private void GroundMode_Click(object sender, RoutedEventArgs e) => RedrawHackerEdits();

    private void GroundUndo_Click(object sender, RoutedEventArgs e)
    {
        if (groundUndo.Count == 0) return;
        groundEdits.Clear();
        groundEdits.AddRange(groundUndo.Pop());
        GroundUndoButton.IsEnabled = groundUndo.Count > 0;
        PersistHackerEdits();
        RedrawHackerEdits();
    }

    private void GroundEditorMouseDown(MouseButtonEventArgs e)
    {
        var point = ToAtlasGlobal(e.GetPosition(AtlasCanvas));
        var nearest = NearestGroundLine(point);
        if (GroundDeleteButton.IsChecked == true)
        {
            if (nearest is null) return;
            PushGroundUndo();
            groundEdits.Remove(nearest);
            PersistHackerEdits();
            RedrawHackerEdits();
            e.Handled = true;
            return;
        }
        if (GroundNegativeButton.IsChecked == true && nearest is not null)
        {
            PushGroundUndo();
            var index = groundEdits.IndexOf(nearest);
            groundEdits[index] = nearest with { IsNegative = !nearest.IsNegative };
            PersistHackerEdits();
            RedrawHackerEdits();
            e.Handled = true;
            return;
        }
        if (GroundModifyButton.IsChecked == true)
        {
            if (nearest is null) return;
            PushGroundUndo();
            activeGroundLine = nearest;
            groundOriginalLine = nearest;
        }
        else
        {
            PushGroundUndo();
            activeGroundLine = new GroundEditLine(
                Guid.NewGuid(), point.X, point.Y, point.X, point.Y,
                GroundNegativeButton.IsChecked == true);
            groundOriginalLine = null;
        }
        groundDragStart = point;
        Mouse.Capture(AtlasCanvas, CaptureMode.Element);
        e.Handled = true;
    }

    private void GroundEditorMouseMove(MouseEventArgs e)
    {
        if (groundDragStart is not { } start || activeGroundLine is null) return;
        var point = ToAtlasGlobal(e.GetPosition(AtlasCanvas));
        if (groundOriginalLine is { } original)
        {
            var replacement = original with
            {
                X0 = original.X0 + point.X - start.X,
                Y0 = original.Y0 + point.Y - start.Y,
                X1 = original.X1 + point.X - start.X,
                Y1 = original.Y1 + point.Y - start.Y
            };
            var index = groundEdits.FindIndex(item => item.Id == original.Id);
            if (index >= 0) groundEdits[index] = replacement;
            activeGroundLine = replacement;
        }
        else
        {
            activeGroundLine = activeGroundLine with { X1 = point.X, Y1 = point.Y };
        }
        RedrawHackerEdits();
        e.Handled = true;
    }

    private void GroundEditorMouseUp(MouseButtonEventArgs e)
    {
        if (activeGroundLine is { } line && groundOriginalLine is null
            && Math.Abs(line.X1 - line.X0) + Math.Abs(line.Y1 - line.Y0) >= 4)
        {
            groundEdits.Add(line);
        }
        activeGroundLine = null;
        groundOriginalLine = null;
        groundDragStart = null;
        Mouse.Capture(null);
        PersistHackerEdits();
        RedrawHackerEdits();
        e.Handled = true;
    }

    private void PushGroundUndo()
    {
        groundUndo.Push(groundEdits.ToArray());
        GroundUndoButton.IsEnabled = true;
    }

    private GroundEditLine? NearestGroundLine(Point point) => groundEdits
        .Select(line => (line, distance: DistanceToSegment(point, line)))
        .Where(item => item.distance <= 12)
        .OrderBy(item => item.distance)
        .Select(item => item.line)
        .FirstOrDefault();

    private static double DistanceToSegment(Point point, GroundEditLine line)
    {
        var dx = line.X1 - line.X0;
        var dy = line.Y1 - line.Y0;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared <= 0 ? 0 : Math.Clamp(
            ((point.X - line.X0) * dx + (point.Y - line.Y0) * dy) / lengthSquared, 0, 1);
        var x = line.X0 + t * dx;
        var y = line.Y0 + t * dy;
        return Math.Sqrt((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y));
    }

    private void RoomEditorMouseDown(MouseButtonEventArgs e)
    {
        var point = ToAtlasGlobal(e.GetPosition(AtlasCanvas));
        activeRoom = rooms.Values.LastOrDefault(room =>
            point.X >= room.Left + room.OffsetX && point.X <= room.Right + room.OffsetX
            && point.Y >= room.Top + room.OffsetY && point.Y <= room.Bottom + room.OffsetY);
        if (activeRoom is null) return;
        roomDragStart = point;
        roomOriginalOffsetX = activeRoom.OffsetX;
        roomOriginalOffsetY = activeRoom.OffsetY;
        Mouse.Capture(AtlasCanvas, CaptureMode.Element);
        e.Handled = true;
    }

    private Point ToAtlasGlobal(Point local) => new(
        local.X + (hackerAtlasMinimumTileX ?? 0) * hackerAtlas.TileSize,
        local.Y + (hackerAtlasMinimumTileY ?? 0) * hackerAtlas.TileSize);

    private void UpdateRoomTopology(ReceiverGroundTruthSample telemetry)
    {
        if (hackerLiveFrame is not { } frame || string.IsNullOrWhiteSpace(telemetry.SceneName)) return;
        var scale = telemetry.PixelsPerWorldUnit(frame.Height);
        if (scale is not > 0) return;
        var ppu = hackerAtlas.PixelsPerWorldUnit;
        var left = (telemetry.CameraX - frame.Width / (2 * scale.Value)) * ppu;
        var right = (telemetry.CameraX + frame.Width / (2 * scale.Value)) * ppu;
        var top = -(telemetry.CameraY + frame.Height / (2 * scale.Value)) * ppu;
        var bottom = -(telemetry.CameraY - frame.Height / (2 * scale.Value)) * ppu;
        if (!rooms.TryGetValue(telemetry.SceneName, out var room))
        {
            room = new RoomEdit(telemetry.SceneName, left, top, right, bottom);
            rooms.Add(telemetry.SceneName, room);
        }
        else
        {
            room.Left = Math.Min(room.Left, left);
            room.Top = Math.Min(room.Top, top);
            room.Right = Math.Max(room.Right, right);
            room.Bottom = Math.Max(room.Bottom, bottom);
        }
        if (lastRoomName is not null && lastRoomName != telemetry.SceneName
            && rooms.TryGetValue(lastRoomName, out var previous))
        {
            previous.Connections.Add(telemetry.SceneName);
            room.Connections.Add(lastRoomName);
            PersistHackerEdits();
        }
        lastRoomName = telemetry.SceneName;
        if (hackerInteractionMode == "Rooms") RedrawHackerEdits();
    }

    private void RedrawHackerEdits()
    {
        HackerOverlayCanvas.Children.Clear();
        var originX = (hackerAtlasMinimumTileX ?? 0) * hackerAtlas.TileSize;
        var originY = (hackerAtlasMinimumTileY ?? 0) * hackerAtlas.TileSize;
        if (hackerInteractionMode == "Ground")
        {
            foreach (var line in groundEdits.Concat(activeGroundLine is not null
                         && groundOriginalLine is null ? [activeGroundLine] : []))
            {
                HackerOverlayCanvas.Children.Add(new Line
                {
                    X1 = line.X0 - originX, Y1 = line.Y0 - originY,
                    X2 = line.X1 - originX, Y2 = line.Y1 - originY,
                    Stroke = line.IsNegative ? Brushes.OrangeRed : Brushes.DeepSkyBlue,
                    StrokeThickness = 4
                });
            }
            GroundCountText.Text = $"+{groundEdits.Count(line => !line.IsNegative)}  −{groundEdits.Count(line => line.IsNegative)}";
        }
        if (hackerInteractionMode == "Rooms")
        {
            foreach (var room in rooms.Values)
            {
                var rectangle = new Rectangle
                {
                    Width = Math.Max(1, room.Right - room.Left),
                    Height = Math.Max(1, room.Bottom - room.Top),
                    Stroke = Brushes.Violet, StrokeThickness = 3,
                    Fill = new SolidColorBrush(Color.FromArgb(24, 238, 130, 238)),
                    ToolTip = room.SceneName
                };
                Canvas.SetLeft(rectangle, room.Left + room.OffsetX - originX);
                Canvas.SetTop(rectangle, room.Top + room.OffsetY - originY);
                HackerOverlayCanvas.Children.Add(rectangle);
                var label = new TextBlock
                {
                    Text = room.SceneName, Foreground = Brushes.Violet,
                    Background = Brushes.Black, Padding = new Thickness(3)
                };
                Canvas.SetLeft(label, room.Left + room.OffsetX - originX + 4);
                Canvas.SetTop(label, room.Top + room.OffsetY - originY + 4);
                HackerOverlayCanvas.Children.Add(label);
                foreach (var connection in room.Connections.Where(rooms.ContainsKey))
                {
                    var destination = rooms[connection];
                    HackerOverlayCanvas.Children.Add(new Line
                    {
                        X1 = (room.Left + room.Right) / 2 + room.OffsetX - originX,
                        Y1 = (room.Top + room.Bottom) / 2 + room.OffsetY - originY,
                        X2 = (destination.Left + destination.Right) / 2 + destination.OffsetX - originX,
                        Y2 = (destination.Top + destination.Bottom) / 2 + destination.OffsetY - originY,
                        Stroke = Brushes.Gold, StrokeThickness = 2
                    });
                }
            }
        }
    }

    private void PersistHackerEdits()
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(hackerEditPath)!);
        File.WriteAllBytes(
            hackerEditPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new HackerEditDocument(groundEdits, rooms.Values.ToArray()),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadHackerEdits()
    {
        if (!File.Exists(hackerEditPath)) return;
        try
        {
            var document = JsonSerializer.Deserialize<HackerEditDocument>(
                File.ReadAllBytes(hackerEditPath));
            if (document is null) return;
            groundEdits.AddRange(document.Ground);
            foreach (var room in document.Rooms) rooms[room.SceneName] = room;
        }
        catch (JsonException) { }
    }

    private static PixelRect DetectionPixelRect(ObjectDetection detection)
    {
        var left = (int)Math.Floor(Math.Clamp(detection.Box.X, 0, 1) * CanvasWidth);
        var top = (int)Math.Floor(Math.Clamp(detection.Box.Y, 0, 1) * CanvasHeight);
        var right = (int)Math.Ceiling(
            Math.Clamp(detection.Box.X + detection.Box.Width, 0, 1) * CanvasWidth);
        var bottom = (int)Math.Ceiling(
            Math.Clamp(detection.Box.Y + detection.Box.Height, 0, 1) * CanvasHeight);
        return new PixelRect(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }

    private void RefreshExamples_Click(object sender, RoutedEventArgs e) => RefreshExamples();

    private void RefreshExamples()
    {
        examples.Clear();
        foreach (var example in new LabelingExampleStore(ExamplesRootText.Text).Load())
        {
            examples.Add(example);
        }
        ModelExamplesRootText.Text = ExamplesRootText.Text;
        if (UntrainedCountText is not null)
        {
            UntrainedCountText.Text = $"+{examples.Count}";
            MacTrainButton.IsEnabled = examples.Any(example =>
                example.Manifest.Annotations.Any(annotation => !annotation.IsHardNegative));
        }
        SetStatus($"Loaded {examples.Count} labeling examples.");
        if (ModelSummaryPanel is not null && workspace == "Model") RefreshModelWorkspace();
    }

    private void RefreshModelWorkspace()
    {
        var annotations = examples.SelectMany(example => example.Manifest.Annotations).ToArray();
        var counts = annotations
            .Where(annotation => !annotation.IsHardNegative)
            .GroupBy(annotation => annotation.ClassIdentifier, StringComparer.Ordinal)
            .OrderBy(group => group.Count())
            .ToArray();
        UntrainedCountText.Text = $"+{examples.Count}";

        ModelSummaryPanel.Children.Clear();
        var summary = new StackPanel { Margin = new Thickness(8) };
        summary.Children.Add(new TextBlock
        {
            Text = "Least Registrations", FontSize = 16, FontWeight = FontWeights.SemiBold
        });
        foreach (var group in counts)
        {
            summary.Children.Add(new TextBlock
            {
                Text = $"{group.Key}    {group.Count()}",
                FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 5, 0, 0)
            });
        }
        summary.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        summary.Children.Add(new TextBlock
        {
            Text = $"{examples.Count} screenshots  •  {annotations.Count(annotation => !annotation.IsHardNegative)} positive  •  {annotations.Count(annotation => annotation.IsHardNegative)} negative",
            Foreground = Brushes.Gray
        });
        if (counts.Length == 0)
        {
            summary.Children.Add(new TextBlock
            {
                Text = "No labeled registrations.", Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
        ModelSummaryPanel.Children.Add(summary);

        ModelObjectsPanel.Children.Clear();
        var objectGrid = new Grid();
        objectGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        objectGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var classList = new ListBox { ItemsSource = counts.Select(group =>
            $"{group.Key}    {group.Count()}").ToArray() };
        objectGrid.Children.Add(classList);
        var detail = new WrapPanel { Margin = new Thickness(12) };
        Grid.SetColumn(detail, 1);
        objectGrid.Children.Add(detail);
        void ShowClass(string? identifier)
        {
            detail.Children.Clear();
            if (identifier is null) return;
            identifier = identifier.Split("    ")[0];
            foreach (var example in examples.Where(example => example.Manifest.Annotations.Any(
                         annotation => !annotation.IsHardNegative
                             && annotation.ClassIdentifier == identifier)).Take(20))
            {
                detail.Children.Add(new Image
                {
                    Source = LoadBitmap(example.ImagePath), Width = 160, Height = 90,
                    Stretch = Stretch.UniformToFill, Margin = new Thickness(4)
                });
            }
        }
        classList.SelectionChanged += (_, _) => ShowClass(classList.SelectedItem?.ToString());
        if (classList.Items.Count > 0) classList.SelectedIndex = 0;
        ModelObjectsPanel.Children.Add(objectGrid);

        ModelScreenshotsPanel.Children.Clear();
        var gallery = new WrapPanel();
        foreach (var example in examples.OrderByDescending(item => item.Manifest.CreatedAt))
        {
            gallery.Children.Add(new Image
            {
                Source = LoadBitmap(example.ImagePath), Width = 200, Height = 112.5,
                Stretch = Stretch.UniformToFill, Margin = new Thickness(5),
                ToolTip = $"{example.Manifest.ContextIdentifier} • {example.Manifest.Annotations.Count} labels"
            });
        }
        ModelScreenshotsPanel.Children.Add(new ScrollViewer
        {
            Content = gallery, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
    }

    private void CaptureFrame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = HollowKnightWindowLocator.FindBest()
                ?? throw new InvalidOperationException(
                    "Hollow Knight window not found. Capture never launches the game.");
            currentFrame = CpuBgraNormalizer.Normalize(new GdiWindowCapture().Capture(window, 1));
            currentExample = null;
            captureGroupIdentifier = Guid.NewGuid();
            boxes.Clear();
            labelUndo.Clear();
            KnownClassesText.Text = "";
            ExamplesList.SelectedItem = null;
            LabelImage.Source = FrameBitmap(currentFrame);
            RedrawBoxes();
            SetStatus("Captured unsaved 640×360 CPU frame. Drag boxes, then Save labels.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void ExamplesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExamplesList.SelectedItem is not SavedLabelingExample selected) return;
        OpenLabelExample(selected);
    }

    private void OpenLabelExample(SavedLabelingExample selected)
    {
        try
        {
            currentExample = selected;
            currentFrame = null;
            captureGroupIdentifier = selected.Manifest.CaptureGroupIdentifier;
            ContextCombo.Text = selected.Manifest.ContextIdentifier;
            KnownClassesText.Text = string.Join(Environment.NewLine,
                selected.Manifest.EffectiveKnownClassIdentifiers);
            boxes.Clear();
            labelUndo.Clear();
            foreach (var annotation in selected.Manifest.Annotations)
            {
                boxes.Add(EditableBox.From(annotation));
            }
            LabelImage.Source = LoadBitmap(selected.ImagePath);
            RedrawBoxes();
            SetStatus($"Editing {selected.Manifest.Id:D}.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void SaveLabels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var context = ContextCombo.Text.Trim();
            if (context.Length == 0) throw new InvalidOperationException("Choose a context.");
            var annotations = boxes.Select(box => box.ToAnnotation()).ToArray();
            var known = ParseIdentifiers(KnownClassesText.Text)
                .Concat(boxes.Select(box => box.ClassIdentifier))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var store = new LabelingExampleStore(ExamplesRootText.Text);
            currentExample = currentExample is null
                ? store.Save(
                    currentFrame ?? throw new InvalidOperationException(
                        "Capture a frame or select an example first."),
                    context,
                    annotations,
                    known,
                    captureGroupIdentifier: captureGroupIdentifier)
                : store.Update(currentExample, context, annotations, known);
            SetStatus($"Saved label example {currentExample.Id:D}.");
            RefreshExamples();
            ExamplesList.SelectedItem = examples.FirstOrDefault(example =>
                example.Id == currentExample.Id);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void DeleteExample_Click(object sender, RoutedEventArgs e)
    {
        if (currentExample is null) return;
        if (MessageBox.Show(
                this,
                $"Delete label example {currentExample.Id:D}?",
                "Delete example",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            new LabelingExampleStore(ExamplesRootText.Text).Delete(currentExample);
            currentExample = null;
            currentFrame = null;
            LabelImage.Source = null;
            boxes.Clear();
            RefreshExamples();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void LabelCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (LabelImage.Source is null) return;
        if (string.IsNullOrWhiteSpace(ClassIdentifierText.Text))
        {
            SetStatus("Enter a class identifier before drawing.");
            return;
        }
        var point = Clamp(e.GetPosition(LabelCanvas));
        var selected = boxes.LastOrDefault(box =>
            point.X >= box.X * CanvasWidth
            && point.X <= (box.X + box.Width) * CanvasWidth
            && point.Y >= box.Y * CanvasHeight
            && point.Y <= (box.Y + box.Height) * CanvasHeight);
        if (LabelNegativeButton.IsChecked == true && selected is not null)
        {
            PushLabelUndo();
            ReplaceBox(selected, selected with { IsNegative = !selected.IsNegative });
            PersistLabelDraft();
            return;
        }
        if (LabelDeleteButton.IsChecked == true || LabelModifyButton.IsChecked == true)
        {
            if (selected is null) return;
            BoxesList.SelectedItem = selected;
            if (LabelDeleteButton.IsChecked == true)
            {
                PushLabelUndo();
                boxes.Remove(selected);
                RedrawBoxes();
                PersistLabelDraft();
            }
            else
            {
                PushLabelUndo();
                modifyingBox = selected;
                modifyOriginal = selected;
                modifyDragStart = point;
                modifyingResize = Math.Abs(point.X - (selected.X + selected.Width) * CanvasWidth) <= 12
                    && Math.Abs(point.Y - (selected.Y + selected.Height) * CanvasHeight) <= 12;
                LabelCanvas.CaptureMouse();
            }
            return;
        }
        PushLabelUndo();
        dragStart = point;
        dragPreview = new Rectangle
        {
            Stroke = Brushes.Gold,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 215, 0))
        };
        LabelCanvas.Children.Add(dragPreview);
        LabelCanvas.CaptureMouse();
    }

    private void LabelCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (modifyingBox is not null && modifyOriginal is not null && modifyDragStart is { } origin)
        {
            var point = Clamp(e.GetPosition(LabelCanvas));
            var dx = (point.X - origin.X) / CanvasWidth;
            var dy = (point.Y - origin.Y) / CanvasHeight;
            var updated = modifyingResize
                ? modifyOriginal with
                {
                    Width = Math.Clamp(modifyOriginal.Width + dx, 2 / CanvasWidth, 1 - modifyOriginal.X),
                    Height = Math.Clamp(modifyOriginal.Height + dy, 2 / CanvasHeight, 1 - modifyOriginal.Y)
                }
                : modifyOriginal with
                {
                    X = Math.Clamp(modifyOriginal.X + dx, 0, 1 - modifyOriginal.Width),
                    Y = Math.Clamp(modifyOriginal.Y + dy, 0, 1 - modifyOriginal.Height)
                };
            ReplaceBox(modifyingBox, updated, redraw: true);
            modifyingBox = updated;
            return;
        }
        if (dragStart is null || dragPreview is null) return;
        PositionRectangle(dragPreview, dragStart.Value, Clamp(e.GetPosition(LabelCanvas)));
    }

    private void LabelCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (modifyingBox is not null)
        {
            modifyingBox = null;
            modifyOriginal = null;
            modifyDragStart = null;
            LabelCanvas.ReleaseMouseCapture();
            PersistLabelDraft();
            return;
        }
        if (dragStart is null || dragPreview is null) return;
        var start = Clamp(dragStart.Value);
        var end = Clamp(e.GetPosition(LabelCanvas));
        LabelCanvas.ReleaseMouseCapture();
        dragStart = null;
        dragPreview = null;
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        if (width >= 2 && height >= 2)
        {
            boxes.Add(new EditableBox(
                Guid.NewGuid(),
                LabelingClassIdentity.CanonicalIdentifier(ClassIdentifierText.Text.Trim()),
                Math.Min(start.X, end.X) / CanvasWidth,
                Math.Min(start.Y, end.Y) / CanvasHeight,
                width / CanvasWidth,
                height / CanvasHeight,
                HardNegativeCheck.IsChecked == true));
        }
        RedrawBoxes();
        if (width >= 2 && height >= 2) PersistLabelDraft();
    }

    private void PersistLabelDraft()
    {
        if (boxes.Count == 0 && currentExample is null) return;
        SaveLabels_Click(this, new RoutedEventArgs());
    }

    private void RemoveBox_Click(object sender, RoutedEventArgs e)
    {
        if (BoxesList.SelectedItem is EditableBox selected)
        {
            PushLabelUndo();
            boxes.Remove(selected);
        }
        RedrawBoxes();
    }

    private void UndoBox_Click(object sender, RoutedEventArgs e)
    {
        UndoLabelEdit();
    }

    private void ClearBoxes_Click(object sender, RoutedEventArgs e)
    {
        PushLabelUndo();
        boxes.Clear();
        RedrawBoxes();
    }

    private void PushLabelUndo() => labelUndo.Push(boxes.ToArray());

    private void UndoLabelEdit()
    {
        if (labelUndo.Count == 0) return;
        boxes.Clear();
        foreach (var box in labelUndo.Pop()) boxes.Add(box);
        RedrawBoxes();
        PersistLabelDraft();
    }

    private void ReplaceBox(EditableBox original, EditableBox replacement, bool redraw = true)
    {
        var index = boxes.IndexOf(original);
        if (index < 0) return;
        boxes[index] = replacement;
        BoxesList.SelectedItem = replacement;
        if (redraw) RedrawBoxes();
    }

    private void RedrawBoxes()
    {
        LabelCanvas.Children.Clear();
        foreach (var box in boxes)
        {
            var rectangle = new Rectangle
            {
                Stroke = box.IsNegative ? Brushes.OrangeRed : Brushes.DeepSkyBlue,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(box.IsNegative
                    ? Color.FromArgb(35, 255, 69, 0)
                    : Color.FromArgb(30, 0, 191, 255)),
                ToolTip = box.ToString()
            };
            Canvas.SetLeft(rectangle, box.X * CanvasWidth);
            Canvas.SetTop(rectangle, box.Y * CanvasHeight);
            rectangle.Width = box.Width * CanvasWidth;
            rectangle.Height = box.Height * CanvasHeight;
            LabelCanvas.Children.Add(rectangle);
        }
    }

    private void ExportDataset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var classes = ParseIdentifiers(ExportClassesText.Text);
            if (classes.Count == 0) throw new InvalidOperationException(
                "Enter at least one included class identifier.");
            var store = new LabelingExampleStore(ModelExamplesRootText.Text);
            var snapshot = new LabelingDatasetExporter(DatasetRootText.Text).Export(
                ModelIdentifierText.Text.Trim(),
                classes,
                store.Load());
            LatestDatasetText.Text = snapshot.DirectoryPath;
            SetStatus(
                $"Exported {snapshot.Manifest.Items.Count} items: "
                + $"{snapshot.Manifest.Items.Count(item => item.Split == LabelingDatasetSplit.Training)} train, "
                + $"{snapshot.Manifest.Items.Count(item => item.Split == LabelingDatasetSplit.Validation)} validation.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void ValidateDataset_Click(object sender, RoutedEventArgs e) =>
        await RunTrainerAsync(validateOnly: true);

    private async void TrainModel_Click(object sender, RoutedEventArgs e) =>
        await RunTrainerAsync(validateOnly: false);

    private async Task RunTrainerAsync(bool validateOnly)
    {
        try
        {
            var dataset = LatestDatasetText.Text.Trim();
            if (!Directory.Exists(dataset)) throw new InvalidOperationException(
                "Export or select a dataset directory first.");
            if (!int.TryParse(IterationsText.Text, out var iterations) || iterations <= 0)
            {
                throw new InvalidOperationException("Iterations must be a positive integer.");
            }
            var windowsRoot = FindWindowsRoot()
                ?? throw new FileNotFoundException("Could not locate train-model-windows.ps1.");
            var script = IOPath.Combine(windowsRoot, "train-model-windows.ps1");
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-Dataset");
            start.ArgumentList.Add(dataset);
            if (validateOnly)
            {
                start.ArgumentList.Add("-ValidateOnly");
            }
            else
            {
                start.ArgumentList.Add("-Output");
                start.ArgumentList.Add(TrainingOutputText.Text.Trim());
                start.ArgumentList.Add("-Iterations");
                start.ArgumentList.Add(iterations.ToString(CultureInfo.InvariantCulture));
                var baseCheckpoint = BaseCheckpointText.Text.Trim();
                if (baseCheckpoint.Length > 0)
                {
                    if (!File.Exists(baseCheckpoint))
                    {
                        throw new FileNotFoundException(
                            "Base checkpoint was not found.", baseCheckpoint);
                    }
                    start.ArgumentList.Add("-BaseCheckpoint");
                    start.ArgumentList.Add(baseCheckpoint);
                }
            }
            TrainingLogText.Clear();
            SetStatus(validateOnly ? "Validating dataset on CPU…" : "Training ONNX model on CPU…");
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => AppendTrainingLog(args.Data);
            process.ErrorDataReceived += (_, args) => AppendTrainingLog(args.Data);
            if (!process.Start()) throw new InvalidOperationException("Trainer did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(
                $"Trainer exited with code {process.ExitCode}.");
            if (!validateOnly)
            {
                VisionRunText.Text = TrainingOutputText.Text.Trim();
            }
            SetStatus(validateOnly
                ? "Dataset validation passed."
                : "CPU training complete; run selected for live Vision.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void RefreshOps_Click(object sender, RoutedEventArgs e) => RefreshOps();

    private void RefreshOps()
    {
        var windowsRoot = FindWindowsRoot();
        var repositoryRoot = windowsRoot is null
            ? null
            : Directory.GetParent(Directory.GetParent(Directory.GetParent(windowsRoot)!.FullName)!.FullName)?.FullName;
        var python = repositoryRoot is null
            ? null
            : IOPath.Combine(repositoryRoot, ".tools", "hkv-training-windows", "Scripts", "python.exe");
        var window = HollowKnightWindowLocator.FindBest();
        OpsText.Text = string.Join(Environment.NewLine, new[]
        {
            $"Windows tooling: {(windowsRoot is null ? "NOT FOUND" : windowsRoot)}",
            $"CPU training Python: {(python is not null && File.Exists(python) ? python : "NOT FOUND")}",
            $"Examples: {ExamplesRootText.Text}",
            $"Datasets: {DatasetRootText.Text}",
            $"Game window: {(window is null ? "not running" : $"PID {window.ProcessId}, {window.ClientWidth}x{window.ClientHeight}")}",
            "ML device policy: CPU; train-model-windows.ps1 sets CUDA_VISIBLE_DEVICES=-1",
            "Capture policy: target-window PrintWindow with GDI fallback, normalized to 640x360 on CPU"
        });
        OpsStatusText.Text = gameControls.PlayerOpsAvailable
            ? "Loading player state…" : "Updated game receiver is not ready.";
        _ = QueryPlayerOpsAsync();
    }

    private async Task QueryPlayerOpsAsync()
    {
        var acknowledgement = await gameControls.PerformPlayerOpsAsync(
            ReceiverPlayerOpsCommand.Query);
        await Dispatcher.InvokeAsync(() => ApplyPlayerOpsAcknowledgement(acknowledgement));
    }

    private async void OpsApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadPlayerTestState(out var state)) return;
        OpsStatusText.Text = "Applying player state…";
        var acknowledgement = await gameControls.PerformPlayerOpsAsync(
            session => ReceiverPlayerOpsCommand.Apply(session, state));
        ApplyPlayerOpsAcknowledgement(acknowledgement);
    }

    private async void OpsRandomize_Click(object sender, RoutedEventArgs e)
    {
        var random = Random.Shared;
        var maxHealth = random.Next(1, 10);
        var slots = random.Next(0, 4);
        var state = new PlayerTestState(
            maxHealth, random.Next(1, maxHealth + 1), random.Next(0, 10),
            random.Next(0, 100 + slots * 33), slots,
            random.Next(0, 10_000), OpsInvincibleCheck.IsChecked == true);
        SetPlayerTestState(state);
        OpsStatusText.Text = "Randomizing player state…";
        var acknowledgement = await gameControls.PerformPlayerOpsAsync(
            session => ReceiverPlayerOpsCommand.Apply(session, state));
        ApplyPlayerOpsAcknowledgement(acknowledgement);
    }

    private async void OpsRestoreEnemies_Click(object sender, RoutedEventArgs e)
    {
        OpsStatusText.Text = "Restoring enemies…";
        var acknowledgement = await gameControls.PerformPlayerOpsAsync(
            ReceiverPlayerOpsCommand.RestoreEnemies);
        ApplyPlayerOpsAcknowledgement(acknowledgement);
    }

    private void OpsReload_Click(object sender, RoutedEventArgs e) => RefreshOps();

    private bool TryReadPlayerTestState(out PlayerTestState state)
    {
        var controls = new[]
        {
            OpsMaxHealthText, OpsHealthText, OpsLifebloodText,
            OpsManaText, OpsManaSlotsText, OpsGeoText
        };
        var values = new int[controls.Length];
        for (var index = 0; index < controls.Length; index++)
        {
            if (!int.TryParse(controls[index].Text, out values[index]))
            {
                OpsStatusText.Text = "Enter whole numbers for player state.";
                state = PlayerTestState.Default;
                return false;
            }
        }
        state = new PlayerTestState(
            values[0], values[1], values[2], values[3], values[4], values[5],
            OpsInvincibleCheck.IsChecked == true).Normalized();
        SetPlayerTestState(state);
        return true;
    }

    private void ApplyPlayerOpsAcknowledgement(ReceiverPlayerOpsAcknowledgement? acknowledgement)
    {
        if (acknowledgement is null)
        {
            OpsStatusText.Text = "Updated game receiver is not ready.";
            return;
        }
        if (!acknowledgement.Accepted)
        {
            OpsStatusText.Text = acknowledgement.Failure ?? "Player operation failed.";
            return;
        }
        if (acknowledgement.State is not null) SetPlayerTestState(acknowledgement.State.Normalized());
        OpsStatusText.Text = acknowledgement.EnemiesRestored is not null
            ? "Enemies restored." : "Player state updated.";
    }

    private void SetPlayerTestState(PlayerTestState state)
    {
        OpsMaxHealthText.Text = state.MaxHealth.ToString(CultureInfo.InvariantCulture);
        OpsHealthText.Text = state.Health.ToString(CultureInfo.InvariantCulture);
        OpsLifebloodText.Text = state.LifebloodSeed.ToString(CultureInfo.InvariantCulture);
        OpsManaSlotsText.Text = state.ExtraManaSlots.ToString(CultureInfo.InvariantCulture);
        OpsManaText.Text = state.Mana.ToString(CultureInfo.InvariantCulture);
        OpsGeoText.Text = state.Geo.ToString(CultureInfo.InvariantCulture);
        OpsInvincibleCheck.IsChecked = state.Invincible;
    }

    private async void RecordAtlas_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(AtlasSecondsText.Text, out var seconds) || seconds <= 0)
            {
                throw new InvalidOperationException("Atlas seconds must be positive.");
            }
            if (!double.TryParse(
                    AtlasPpuText.Text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var ppu) || !double.IsFinite(ppu) || ppu <= 0)
            {
                throw new InvalidOperationException("Atlas pixels/world unit must be positive.");
            }
            RecordAtlasButton.IsEnabled = false;
            SetStatus("Recording ground-truth atlas…");
            var result = await AtlasRecordingRunner.RunAsync(
                AtlasOutputText.Text,
                seconds,
                133,
                ppu);
            if (result == 0) LoadAtlasPreview(AtlasOutputText.Text);
            SetStatus(result == 0 ? $"Atlas saved: {AtlasOutputText.Text}" : $"Atlas exited {result}.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            RecordAtlasButton.IsEnabled = true;
        }
    }

    private void PresentGameplayFrame(
        BgraFrame frame,
        ReceiverGroundTruthSample telemetry,
        BitmapSource bitmap)
    {
        gameplayLiveFrame = frame;
        gameplayLiveTelemetry = telemetry;
        GameplayImage.Source = bitmap;
        PositionLiveFrame(
            GameplayAtlasCanvas, GameplayImage, DetectionCanvas,
            gameplayAtlas, gameplayAtlasMinimumTileX, gameplayAtlasMinimumTileY,
            frame, telemetry);
        ApplyViewport(GameplayAtlasCanvas, GameplayPanel, gameplayFraming, GameplayImage);
    }

    private void RenderLiveAtlas(
        Canvas canvas,
        Dictionary<AtlasTileCoordinate, Image> images,
        GroundTruthAtlasMosaic mosaic,
        AtlasMosaicDelta delta,
        ref int? minimumTileX,
        ref int? minimumTileY,
        Image liveImage,
        BgraFrame? liveFrame,
        ReceiverGroundTruthSample? telemetry)
    {
        foreach (var tile in delta.Tiles)
        {
            if (!images.TryGetValue(tile.Coordinate, out var image))
            {
                image = new Image
                {
                    Width = mosaic.TileSize, Height = mosaic.TileSize,
                    Stretch = Stretch.Fill, SnapsToDevicePixels = true
                };
                Panel.SetZIndex(image, 0);
                images[tile.Coordinate] = image;
                canvas.Children.Insert(0, image);
            }
            image.Source = FrameBitmap(tile.Frame);
        }
        if (images.Count == 0) return;
        minimumTileX = images.Keys.Min(coordinate => coordinate.X);
        minimumTileY = images.Keys.Min(coordinate => coordinate.Y);
        var maximumTileX = images.Keys.Max(coordinate => coordinate.X);
        var maximumTileY = images.Keys.Max(coordinate => coordinate.Y);
        canvas.Width = (maximumTileX - minimumTileX.Value + 1) * mosaic.TileSize;
        canvas.Height = (maximumTileY - minimumTileY.Value + 1) * mosaic.TileSize;
        foreach (var pair in images)
        {
            Canvas.SetLeft(pair.Value, (pair.Key.X - minimumTileX.Value) * mosaic.TileSize);
            Canvas.SetTop(pair.Value, (pair.Key.Y - minimumTileY.Value) * mosaic.TileSize);
        }
        if (ReferenceEquals(canvas, GameplayAtlasCanvas))
        {
            GameplayGroundCanvas.Width = canvas.Width;
            GameplayGroundCanvas.Height = canvas.Height;
            DrawTrackedGround();
        }
        else
        {
            HackerOverlayCanvas.Width = canvas.Width;
            HackerOverlayCanvas.Height = canvas.Height;
            AtlasEmptyPanel.Visibility = Visibility.Collapsed;
            RedrawHackerEdits();
        }
        if (liveFrame is not null && telemetry is not null)
        {
            PositionLiveFrame(
                canvas, liveImage,
                ReferenceEquals(canvas, GameplayAtlasCanvas) ? DetectionCanvas : null,
                mosaic, minimumTileX, minimumTileY, liveFrame, telemetry);
        }
        var mode = ReferenceEquals(canvas, GameplayAtlasCanvas)
            ? gameplayFraming : hackerInteractionMode;
        var host = ReferenceEquals(canvas, GameplayAtlasCanvas)
            ? GameplayPanel : HackerViewportPanel;
        ApplyViewport(canvas, host, mode, liveImage);
    }

    private static void PositionLiveFrame(
        Canvas canvas,
        Image liveImage,
        Canvas? detectionCanvas,
        GroundTruthAtlasMosaic mosaic,
        int? minimumTileX,
        int? minimumTileY,
        BgraFrame frame,
        ReceiverGroundTruthSample telemetry)
    {
        var sourceScale = telemetry.PixelsPerWorldUnit(frame.Height);
        if (sourceScale is not > 0 || !double.IsFinite(sourceScale.Value)) return;
        var halfWorldWidth = frame.Width / (2 * sourceScale.Value);
        var halfWorldHeight = frame.Height / (2 * sourceScale.Value);
        var globalLeft = (telemetry.CameraX - halfWorldWidth) * mosaic.PixelsPerWorldUnit;
        var globalTop = -(telemetry.CameraY + halfWorldHeight) * mosaic.PixelsPerWorldUnit;
        var originX = (minimumTileX ?? 0) * mosaic.TileSize;
        var originY = (minimumTileY ?? 0) * mosaic.TileSize;
        var width = frame.Width / sourceScale.Value * mosaic.PixelsPerWorldUnit;
        var height = frame.Height / sourceScale.Value * mosaic.PixelsPerWorldUnit;
        liveImage.Width = width;
        liveImage.Height = height;
        Canvas.SetLeft(liveImage, globalLeft - originX);
        Canvas.SetTop(liveImage, globalTop - originY);
        if (detectionCanvas is not null)
        {
            detectionCanvas.Width = CanvasWidth;
            detectionCanvas.Height = CanvasHeight;
            Canvas.SetLeft(detectionCanvas, globalLeft - originX);
            Canvas.SetTop(detectionCanvas, globalTop - originY);
            detectionCanvas.RenderTransform = new ScaleTransform(
                width / CanvasWidth, height / CanvasHeight);
            detectionCanvas.RenderTransformOrigin = new Point(0, 0);
        }
    }

    private static void ApplyViewport(
        Canvas surface,
        FrameworkElement host,
        string mode,
        FrameworkElement liveFrame)
    {
        if (host.ActualWidth <= 0 || host.ActualHeight <= 0
            || surface.Width <= 0 || surface.Height <= 0) return;
        if (mode == "Free Fly"
            && surface.RenderTransform is MatrixTransform existing
            && !existing.Matrix.IsIdentity)
        {
            return;
        }
        var targetLeft = 0.0;
        var targetTop = 0.0;
        var targetWidth = surface.Width;
        var targetHeight = surface.Height;
        if (mode == "Current" && liveFrame.IsVisible
            && liveFrame.Width > 0 && liveFrame.Height > 0)
        {
            targetLeft = Canvas.GetLeft(liveFrame);
            targetTop = Canvas.GetTop(liveFrame);
            targetWidth = liveFrame.Width;
            targetHeight = liveFrame.Height;
        }
        var scale = Math.Min(host.ActualWidth / targetWidth, host.ActualHeight / targetHeight);
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var matrix = new Matrix(
            scale, 0, 0, scale,
            (host.ActualWidth - targetWidth * scale) / 2 - targetLeft * scale,
            (host.ActualHeight - targetHeight * scale) / 2 - targetTop * scale);
        surface.RenderTransform = new MatrixTransform(matrix);
    }

    private void PersistActiveAtlas(bool force)
    {
        if (gameplayAtlas.TileCount == 0) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && (now - lastAtlasPersistedAt < TimeSpan.FromSeconds(10)
            || atlasPersistTask is { IsCompleted: false })) return;
        if (force && atlasPersistTask is not null)
        {
            atlasPersistTask.GetAwaiter().GetResult();
            atlasPersistTask = null;
        }
        var delta = gameplayAtlas.SnapshotSince(gameplayPersistedRevision);
        if (delta.Tiles.Count == 0 && !force) return;
        var metadata = new AtlasDiskMetadata(
            gameplayAtlas.AcceptedFrameCount,
            activeAtlasCreatedAt,
            gameplayAtlas.TileSize,
            gameplayAtlas.PixelsPerWorldUnit);
        gameplayPersistedRevision = delta.Revision;
        lastAtlasPersistedAt = now;
        if (force)
        {
            WriteAtlasDelta(delta, metadata);
        }
        else
        {
            atlasPersistTask = Task.Run(() => WriteAtlasDelta(delta, metadata));
        }
    }

    private void WriteAtlasDelta(AtlasMosaicDelta delta, AtlasDiskMetadata metadata)
    {
        Directory.CreateDirectory(activeAtlasDirectory);
        foreach (var tile in delta.Tiles)
        {
            SaveBitmap(
                tile.Frame,
                IOPath.Combine(activeAtlasDirectory,
                    $"tile_{tile.Coordinate.X}_{tile.Coordinate.Y}.bmp"));
        }
        File.WriteAllBytes(
            IOPath.Combine(activeAtlasDirectory, "atlas.json"),
            JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }

    private void LoadPersistentAtlas()
    {
        if (!Directory.Exists(activeAtlasDirectory)) return;
        var metadataPath = IOPath.Combine(activeAtlasDirectory, "atlas.json");
        AtlasDiskMetadata? restoredMetadata = null;
        if (File.Exists(metadataPath))
        {
            try
            {
                restoredMetadata = JsonSerializer.Deserialize<AtlasDiskMetadata>(
                    File.ReadAllBytes(metadataPath));
                if (restoredMetadata is not null)
                {
                    activeAtlasCreatedAt = restoredMetadata.CreatedAt;
                }
            }
            catch (JsonException) { }
        }
        gameplayAtlas.Reset();
        foreach (var path in Directory.EnumerateFiles(activeAtlasDirectory, "tile_*_*.bmp"))
        {
            var coordinate = ParseAtlasTile(path);
            if (coordinate is null) continue;
            gameplayAtlas.ImportTile(
                new AtlasTileCoordinate(coordinate.Value.X, coordinate.Value.Y),
                LoadBgraFrame(path));
        }
        if (restoredMetadata is not null)
        {
            gameplayAtlas.RestoreAcceptedFrameCount(restoredMetadata.RegistrationCount);
        }
        gameplayPersistedRevision = gameplayAtlas.Revision;
        if (gameplayAtlas.TileCount > 0)
        {
            var delta = new AtlasMosaicDelta(gameplayAtlas.Revision, gameplayAtlas.Snapshot());
            RenderLiveAtlas(
                GameplayAtlasCanvas, gameplayTileImages, gameplayAtlas, delta,
                ref gameplayAtlasMinimumTileX, ref gameplayAtlasMinimumTileY,
                GameplayImage, gameplayLiveFrame, gameplayLiveTelemetry);
            gameplayRenderedRevision = delta.Revision;
        }
    }

    private void RefreshAtlasManager()
    {
        PersistActiveAtlas(force: true);
        AtlasStatesList.Items.Clear();
        var age = DateTimeOffset.UtcNow - activeAtlasCreatedAt;
        var bytes = Directory.Exists(activeAtlasDirectory)
            ? AtlasStateStore.FileByteCount(activeAtlasDirectory) : 0;
        AtlasStatesList.Items.Add(new AtlasManagerItem(
            null,
            "Auto Save",
            $"{gameplayAtlas.AcceptedFrameCount} registrations  •  {FormatBytes(bytes)}  •  {FormatAge(age)} old"));
        foreach (var state in atlasStateStore.List())
        {
            var world = atlasStateStore.WorldPath(state);
            AtlasStatesList.Items.Add(new AtlasManagerItem(
                state,
                state.Name,
                $"{FormatBytes(AtlasStateStore.FileByteCount(world))}  •  {FormatAge(DateTimeOffset.UtcNow - state.CreatedAt)} old"));
        }
        AtlasStatesList.SelectedIndex = 0;
    }

    private void AtlasStatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var saved = (AtlasStatesList.SelectedItem as AtlasManagerItem)?.State is not null;
        AtlasDeleteStateButton.IsEnabled = saved;
        AtlasLoadStateButton.IsEnabled = saved;
    }

    private void AtlasSaveState_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistActiveAtlas(force: true);
            atlasStateStore.Save(
                AtlasStateNameText.Text,
                destination => CopyDirectory(activeAtlasDirectory, destination));
            AtlasStateNameText.Clear();
            RefreshAtlasManager();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void AtlasNew_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (gameplayAtlas.TileCount > 0)
            {
                PersistActiveAtlas(force: true);
                atlasStateStore.Save(
                    $"Archive {DateTime.Now:g}",
                    destination => CopyDirectory(activeAtlasDirectory, destination));
            }
            ResetGameplayAtlas(deleteArchive: true);
            RefreshAtlasManager();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void AtlasDeleteState_Click(object sender, RoutedEventArgs e)
    {
        if (AtlasStatesList.SelectedItem is not AtlasManagerItem { State: { } state }) return;
        try
        {
            atlasStateStore.Delete(state);
            RefreshAtlasManager();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void AtlasLoadState_Click(object sender, RoutedEventArgs e)
    {
        if (AtlasStatesList.SelectedItem is not AtlasManagerItem { State: { } state }) return;
        try
        {
            ResetGameplayAtlas(deleteArchive: true);
            CopyDirectory(atlasStateStore.WorldPath(state), activeAtlasDirectory);
            LoadPersistentAtlas();
            AtlasManagerOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ResetGameplayAtlas(bool deleteArchive)
    {
        gameplayAtlas.Reset();
        trackedGround.Clear();
        gameplayRenderedRevision = gameplayAtlas.Revision;
        gameplayPersistedRevision = gameplayAtlas.Revision;
        foreach (var image in gameplayTileImages.Values) GameplayAtlasCanvas.Children.Remove(image);
        gameplayTileImages.Clear();
        GameplayGroundCanvas.Children.Clear();
        gameplayAtlasMinimumTileX = gameplayAtlasMinimumTileY = null;
        activeAtlasCreatedAt = DateTimeOffset.UtcNow;
        if (deleteArchive && Directory.Exists(activeAtlasDirectory))
        {
            Directory.Delete(activeAtlasDirectory, recursive: true);
        }
    }

    private void HackerDeleteAtlas_Click(object sender, RoutedEventArgs e)
    {
        hackerAtlas.Reset();
        hackerRenderedRevision = hackerAtlas.Revision;
        foreach (var image in hackerTileImages.Values) AtlasCanvas.Children.Remove(image);
        hackerTileImages.Clear();
        hackerAtlasMinimumTileX = hackerAtlasMinimumTileY = null;
        groundEdits.Clear();
        rooms.Clear();
        PersistHackerEdits();
        HackerLiveImage.Visibility = Visibility.Collapsed;
        AtlasEmptyPanel.Visibility = Visibility.Visible;
        AtlasManagerOverlay.Visibility = Visibility.Collapsed;
        RedrawHackerEdits();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, IOPath.Combine(destination, IOPath.GetFileName(file)), overwrite: true);
        }
        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, IOPath.Combine(destination, IOPath.GetFileName(child)));
        }
    }

    private static void SaveBitmap(BgraFrame frame, string path)
    {
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(FrameBitmap(frame)));
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary)) encoder.Save(stream);
        File.Move(temporary, path, overwrite: true);
    }

    private static BgraFrame LoadBgraFrame(string path)
    {
        var source = LoadBitmap(path);
        var converted = source.Format == PixelFormats.Bgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * BgraFrame.BytesPerPixel;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new BgraFrame(
            converted.PixelWidth, converted.PixelHeight, stride, pixels, 0, DateTimeOffset.UtcNow);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    private static string FormatAge(TimeSpan age) => age.TotalHours >= 1
        ? $"{age.TotalHours:F0} hours"
        : age.TotalMinutes >= 1 ? $"{age.TotalMinutes:F0} minutes" : $"{Math.Max(0, age.TotalSeconds):F0} seconds";

    private void LoadAtlas_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a Hollow Knight Vision atlas",
            InitialDirectory = Directory.Exists(AtlasOutputText.Text)
                ? AtlasOutputText.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        AtlasOutputText.Text = dialog.FolderName;
        try
        {
            LoadAtlasPreview(dialog.FolderName);
            SetStatus($"Atlas loaded: {dialog.FolderName}");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void LoadAtlasPreview(string directory)
    {
        var tiles = Directory.EnumerateFiles(directory, "tile_*_*.bmp")
            .Select(path => (path, coordinate: ParseAtlasTile(path)))
            .Where(item => item.coordinate is not null)
            .Select(item => (item.path, coordinate: item.coordinate!.Value))
            .ToArray();
        if (tiles.Length == 0)
        {
            throw new InvalidOperationException("That directory has no atlas tile BMP files.");
        }

        var minimumX = tiles.Min(item => item.coordinate.X);
        var maximumX = tiles.Max(item => item.coordinate.X);
        var minimumY = tiles.Min(item => item.coordinate.Y);
        var maximumY = tiles.Max(item => item.coordinate.Y);
        const double tileSize = 256;
        ClearAtlasTileImages();
        hackerAtlasMinimumTileX = minimumX;
        hackerAtlasMinimumTileY = minimumY;
        AtlasCanvas.Width = (maximumX - minimumX + 1) * tileSize;
        AtlasCanvas.Height = (maximumY - minimumY + 1) * tileSize;
        foreach (var tile in tiles)
        {
            var image = new Image
            {
                Source = LoadBitmap(tile.path),
                Width = tileSize,
                Height = tileSize,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(image, 0);
            Canvas.SetLeft(image, (tile.coordinate.X - minimumX) * tileSize);
            Canvas.SetTop(image, (tile.coordinate.Y - minimumY) * tileSize);
            AtlasCanvas.Children.Add(image);
        }
        AtlasEmptyPanel.Visibility = Visibility.Collapsed;
        AtlasPreviewText.Text =
            $"ATLAS  •  {tiles.Length} tiles  •  {maximumX - minimumX + 1}×{maximumY - minimumY + 1}";
    }

    private void RenderLiveHackerAtlas(IReadOnlyList<AtlasTileSnapshot> tiles)
    {
        if (tiles.Count == 0) return;
        var minimumX = tiles.Min(tile => tile.Coordinate.X);
        var maximumX = tiles.Max(tile => tile.Coordinate.X);
        var minimumY = tiles.Min(tile => tile.Coordinate.Y);
        var maximumY = tiles.Max(tile => tile.Coordinate.Y);
        var tileSize = hackerAtlas.TileSize;
        ClearAtlasTileImages();
        hackerAtlasMinimumTileX = minimumX;
        hackerAtlasMinimumTileY = minimumY;
        AtlasCanvas.Width = (maximumX - minimumX + 1) * tileSize;
        AtlasCanvas.Height = (maximumY - minimumY + 1) * tileSize;
        foreach (var tile in tiles)
        {
            var image = new Image
            {
                Source = FrameBitmap(tile.Frame),
                Width = tileSize,
                Height = tileSize,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(image, 0);
            Canvas.SetLeft(image, (tile.Coordinate.X - minimumX) * tileSize);
            Canvas.SetTop(image, (tile.Coordinate.Y - minimumY) * tileSize);
            AtlasCanvas.Children.Add(image);
        }
        AtlasEmptyPanel.Visibility = Visibility.Collapsed;
        AtlasPreviewText.Text =
            $"ATLAS  •  {tiles.Count} tiles  •  {maximumX - minimumX + 1}×{maximumY - minimumY + 1}";
        PositionHackerLiveFrame();
    }

    private void ClearAtlasTileImages()
    {
        foreach (var child in AtlasCanvas.Children
                     .Cast<UIElement>()
                     .Where(child => !ReferenceEquals(child, HackerLiveImage)
                         && !ReferenceEquals(child, HackerOverlayCanvas))
                     .ToArray())
        {
            AtlasCanvas.Children.Remove(child);
        }
    }

    private void PresentHackerFrame(
        BgraFrame frame,
        ReceiverGroundTruthSample telemetry,
        BitmapSource bitmap)
    {
        hackerLiveFrame = frame;
        hackerLiveTelemetry = telemetry;
        HackerLiveImage.Source = bitmap;
        HackerLiveImage.Visibility = Visibility.Visible;
        AtlasEmptyPanel.Visibility = Visibility.Collapsed;
        PositionHackerLiveFrame();
    }

    private void PositionHackerLiveFrame()
    {
        if (hackerLiveFrame is not { } frame
            || hackerLiveTelemetry is not { } telemetry)
        {
            return;
        }
        PositionLiveFrame(
            AtlasCanvas, HackerLiveImage, null, hackerAtlas,
            hackerAtlasMinimumTileX, hackerAtlasMinimumTileY, frame, telemetry);
        ApplyViewport(AtlasCanvas, HackerViewportPanel, hackerInteractionMode, HackerLiveImage);
    }

    private static (int X, int Y)? ParseAtlasTile(string path)
    {
        var parts = IOPath.GetFileNameWithoutExtension(path).Split('_');
        return parts.Length == 3
            && parts[0].Equals("tile", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                ? (x, y)
                : null;
    }

    private async void RecordRoute_Click(object sender, RoutedEventArgs e)
    {
        RecordRouteButton.IsEnabled = false;
        ReplayRouteButton.IsEnabled = false;
        BuildWorldButton.IsEnabled = false;
        OptimizeWorldButton.IsEnabled = false;
        SetStatus("Recording visual route with receiver camera truth…");
        try
        {
            var result = await VisualRouteRunner.RecordAsync(
                VisualRouteText.Text,
                30,
                133);
            RouteStatusText.Text = result == 0
                ? "Route saved. Replay it to score visual camera error."
                : $"Route recorder exited {result}.";
            SetStatus(RouteStatusText.Text);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            RecordRouteButton.IsEnabled = true;
            ReplayRouteButton.IsEnabled = true;
            BuildWorldButton.IsEnabled = true;
            OptimizeWorldButton.IsEnabled = true;
        }
    }

    private async void ReplayRoute_Click(object sender, RoutedEventArgs e)
    {
        RecordRouteButton.IsEnabled = false;
        ReplayRouteButton.IsEnabled = false;
        BuildWorldButton.IsEnabled = false;
        OptimizeWorldButton.IsEnabled = false;
        SetStatus("Replaying saved visual route three times…");
        try
        {
            var directory = VisualRouteText.Text;
            var reportPath = IOPath.Combine(directory, "replay-report.json");
            var result = await Task.Run(() => VisualRouteRunner.Replay(
                directory,
                3,
                12,
                32,
                reportPath));
            RouteStatusText.Text = result == 0
                ? $"Route replay passed ×3. Report: {reportPath}"
                : $"Route replay failed gate. Report: {reportPath}";
            SetStatus(RouteStatusText.Text);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            RecordRouteButton.IsEnabled = true;
            ReplayRouteButton.IsEnabled = true;
            BuildWorldButton.IsEnabled = true;
            OptimizeWorldButton.IsEnabled = true;
        }
    }

    private async void BuildWorld_Click(object sender, RoutedEventArgs e)
    {
        RecordRouteButton.IsEnabled = false;
        ReplayRouteButton.IsEnabled = false;
        BuildWorldButton.IsEnabled = false;
        OptimizeWorldButton.IsEnabled = false;
        SetStatus("Building validated room graph from saved visual evidence…");
        try
        {
            var directory = VisualRouteText.Text;
            var worldDirectory = IOPath.Combine(directory, "world");
            var result = await Task.Run(() => VisualRouteRunner.BuildWorld(
                directory,
                worldDirectory,
                10));
            RouteStatusText.Text = result == 0
                ? $"Room graph saved: {IOPath.Combine(worldDirectory, "world.json")}"
                : $"Room graph builder exited {result}.";
            SetStatus(RouteStatusText.Text);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            RecordRouteButton.IsEnabled = true;
            ReplayRouteButton.IsEnabled = true;
            BuildWorldButton.IsEnabled = true;
            OptimizeWorldButton.IsEnabled = true;
        }
    }

    private async void OptimizeWorld_Click(object sender, RoutedEventArgs e)
    {
        RecordRouteButton.IsEnabled = false;
        ReplayRouteButton.IsEnabled = false;
        BuildWorldButton.IsEnabled = false;
        OptimizeWorldButton.IsEnabled = false;
        SetStatus("Optimizing persisted room graph…");
        try
        {
            var worldDirectory = IOPath.Combine(VisualRouteText.Text, "world");
            var result = await Task.Run(() => VisualRouteRunner.OptimizeWorld(worldDirectory));
            RouteStatusText.Text = result == 0
                ? $"Room graph optimized atomically: {IOPath.Combine(worldDirectory, "world.json")}"
                : $"Room graph optimizer exited {result}.";
            SetStatus(RouteStatusText.Text);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            RecordRouteButton.IsEnabled = true;
            ReplayRouteButton.IsEnabled = true;
            BuildWorldButton.IsEnabled = true;
            OptimizeWorldButton.IsEnabled = true;
        }
    }

    private async void ReplayInputPath_Click(object sender, RoutedEventArgs e)
    {
        var path = InputPathText.Text.Trim();
        if (!File.Exists(path))
        {
            ShowError(new FileNotFoundException("Choose a recorded input-path JSON file.", path));
            return;
        }
        if (!int.TryParse(InputPathRepeatsText.Text, out var repeats) || repeats <= 0)
        {
            ShowError(new InvalidOperationException("Path repeats must be a positive integer."));
            return;
        }
        ReplayInputPathButton.IsEnabled = false;
        SetStatus("Replaying saved input path through receiver…");
        try
        {
            var result = await InputPathPlaybackRunner.RunAsync(
                path,
                repeats,
                RestorePathCheckpointCheck.IsChecked == true);
            SetStatus(result == 0
                ? $"Input path replay completed ×{repeats}."
                : $"Input path replay exited {result}.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            ReplayInputPathButton.IsEnabled = true;
        }
    }

    private void BrowseInputPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Hollow Knight Vision input path",
            Filter = "Hollow Knight Vision path (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) InputPathText.Text = dialog.FileName;
    }

    private static BitmapSource FrameBitmap(BgraFrame frame)
    {
        var bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Pixels,
            frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static IReadOnlyList<string> ParseIdentifiers(string value) => value
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(LabelingClassIdentity.CanonicalIdentifier)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, CanvasWidth),
        Math.Clamp(point.Y, 0, CanvasHeight));

    private static void PositionRectangle(Rectangle rectangle, Point first, Point second)
    {
        Canvas.SetLeft(rectangle, Math.Min(first.X, second.X));
        Canvas.SetTop(rectangle, Math.Min(first.Y, second.Y));
        rectangle.Width = Math.Abs(second.X - first.X);
        rectangle.Height = Math.Abs(second.Y - first.Y);
    }

    private static string? FindWindowsRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && current is not null; depth++, current = current.Parent)
        {
            if (File.Exists(IOPath.Combine(current.FullName, "train-model-windows.ps1")))
            {
                return current.FullName;
            }
        }
        return null;
    }

    private static string? LatestModelRun(string runsRoot)
    {
        if (!Directory.Exists(runsRoot)) return null;
        return Directory.EnumerateDirectories(runsRoot)
            .Where(directory => File.Exists(IOPath.Combine(directory, "Detector.onnx"))
                && File.Exists(IOPath.Combine(directory, "training.json")))
            .OrderByDescending(directory => Directory.GetLastWriteTimeUtc(directory))
            .FirstOrDefault();
    }

    private static string? LatestAtlas(params string?[] roots) => roots
        .Where(root => root is not null && Directory.Exists(root))
        .SelectMany(root => Directory.EnumerateDirectories(root!))
        .Where(directory => File.Exists(IOPath.Combine(directory, "atlas.json"))
            && Directory.EnumerateFiles(directory, "tile_*_*.bmp").Any())
        .OrderByDescending(directory => File.GetLastWriteTimeUtc(
            IOPath.Combine(directory, "atlas.json")))
        .FirstOrDefault();

    private void AppendTrainingLog(string? line)
    {
        if (line is null) return;
        Dispatcher.Invoke(() =>
        {
            TrainingLogText.AppendText(line + Environment.NewLine);
            TrainingLogText.ScrollToEnd();
        });
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void ShowError(Exception error)
    {
        SetStatus(error.Message);
        MessageBox.Show(this, error.Message, "Hollow Knight Vision", MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private sealed record AtlasDiskMetadata(
        long RegistrationCount,
        DateTimeOffset CreatedAt,
        int TileSize,
        double PixelsPerWorldUnit);

    private sealed record AtlasManagerItem(
        AtlasSavedState? State,
        string Name,
        string Detail)
    {
        public override string ToString() => $"{Name}\n{Detail}";
    }

    private sealed class TrackedGroundLine(double x0, double x1, double y)
    {
        public double X0 { get; set; } = x0;
        public double X1 { get; set; } = x1;
        public double Y { get; set; } = y;
        public int Observations { get; set; } = 1;
    }

    private sealed record GroundEditLine(
        Guid Id,
        double X0,
        double Y0,
        double X1,
        double Y1,
        bool IsNegative);

    private sealed class RoomEdit
    {
        public RoomEdit(string sceneName, double left, double top, double right, double bottom)
        {
            SceneName = sceneName;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public string SceneName { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public HashSet<string> Connections { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record HackerEditDocument(
        IReadOnlyList<GroundEditLine> Ground,
        IReadOnlyList<RoomEdit> Rooms);

    private sealed record EditableBox(
        Guid Id,
        string ClassIdentifier,
        double X,
        double Y,
        double Width,
        double Height,
        bool IsNegative)
    {
        internal static EditableBox From(LabelingExampleAnnotation annotation) => new(
            annotation.Id,
            annotation.ClassIdentifier,
            annotation.X,
            annotation.Y,
            annotation.Width,
            annotation.Height,
            annotation.IsHardNegative);

        internal LabelingExampleAnnotation ToAnnotation() => new(
            Id,
            ClassIdentifier,
            X,
            Y,
            Width,
            Height,
            IsNegative ? true : null);

        public override string ToString() =>
            $"{(IsNegative ? "NEG " : "")}{ClassIdentifier} "
            + $"({X:F3}, {Y:F3}, {Width:F3}, {Height:F3})";
    }

    private static string? BundledModelRun()
    {
        var directory = IOPath.Combine(
            AppContext.BaseDirectory, "models", "shared-object-model");
        return File.Exists(IOPath.Combine(directory, "Detector.onnx"))
            && File.Exists(IOPath.Combine(directory, "training.json"))
            ? directory
            : null;
    }

    private sealed record ClassChoice(string Name, string Identifier)
    {
        public override string ToString() => Name;
    }
}
