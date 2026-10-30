using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
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

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HollowKnightVision.Windows.UiTests")]

namespace HollowKnightVision.Windows.App;

internal static class GameControlKeyPolicy
{
    private static IReadOnlyDictionary<Key, InputButtons> ReceiverButtons { get; } =
        new Dictionary<Key, InputButtons>
        {
            [Key.Left] = InputButtons.Left,
            [Key.Right] = InputButtons.Right,
            [Key.Down] = InputButtons.Down,
            [Key.Up] = InputButtons.Up,
            [Key.A] = InputButtons.ActionA,
            [Key.Z] = InputButtons.ActionZ,
            [Key.X] = InputButtons.ActionX,
            [Key.I] = InputButtons.Inventory,
            [Key.P] = InputButtons.PauseMenu
        };

    private static IReadOnlySet<Key> ModelReviewKeys { get; } = new HashSet<Key>
    {
        Key.Left,
        Key.Right,
        Key.Down,
        Key.Up,
        Key.X
    };

    internal static IReadOnlySet<int> ReceiverSupportedVirtualKeys { get; } =
        ReceiverButtons.Keys.Select(KeyInterop.VirtualKeyFromKey).ToHashSet();

    internal static IReadOnlySet<int> ModelReviewReservedVirtualKeys { get; } =
        ModelReviewKeys.Select(KeyInterop.VirtualKeyFromKey).ToHashSet();

    internal static bool IsModelReviewReserved(string workspace, Key key) =>
        workspace == "Model" && ModelReviewKeys.Contains(key);

    internal static bool TryMapReceiverButton(Key key, out InputButtons button)
    {
        if (ReceiverButtons.TryGetValue(key, out button)) return true;
        button = InputButtons.None;
        return false;
    }

    internal static bool TryMapReceiverButton(
        string workspace,
        Key key,
        out InputButtons button)
    {
        if (!IsModelReviewReserved(workspace, key))
            return TryMapReceiverButton(key, out button);
        button = InputButtons.None;
        return false;
    }
}

internal enum LabelingEditorMode
{
    Add,
    Modify,
    Delete,
    Negative
}

internal enum LabelingResizeCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal static class LabelingEditorPolicy
{
    internal const double MinimumNormalizedDimension = .003;
    internal const double ControlPointSize = 5;

    internal static IReadOnlyList<LabelingEditorMode> Modes { get; } =
        [LabelingEditorMode.Add, LabelingEditorMode.Modify,
         LabelingEditorMode.Delete, LabelingEditorMode.Negative];

    private static IReadOnlyList<LabelingEditorMode> NonGameplayModes { get; } =
        [LabelingEditorMode.Add, LabelingEditorMode.Modify, LabelingEditorMode.Delete];

    internal static IReadOnlyList<LabelingEditorMode> AvailableModes(string? context) =>
        StencilInitializationPolicy.NormalizeContext(context ?? "") == "game"
            ? Modes
            : NonGameplayModes;

    internal static char Shortcut(LabelingEditorMode mode) => mode switch
    {
        LabelingEditorMode.Add => '1',
        LabelingEditorMode.Modify => '2',
        LabelingEditorMode.Delete => '3',
        LabelingEditorMode.Negative => '4',
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal static LabelingEditorMode? ModeForKey(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => LabelingEditorMode.Add,
        Key.D2 or Key.NumPad2 => LabelingEditorMode.Modify,
        Key.D3 or Key.NumPad3 => LabelingEditorMode.Delete,
        Key.D4 or Key.NumPad4 => LabelingEditorMode.Negative,
        _ => null
    };

    internal static bool ShouldPersist(int rectangleCount, bool editsSavedExample) =>
        editsSavedExample || rectangleCount > 0;

    internal static IReadOnlyList<MenuStencilContext> SelectableContexts { get; } =
        LabelingContractCatalog.Contexts.Where(context =>
            context.Identifier is not ("shared" or "enemies" or "world")).ToArray();

    internal static string CycledContextIdentifier(
        string current,
        int offset,
        IReadOnlyList<string>? selectableIdentifiers = null)
    {
        var identifiers = selectableIdentifiers
            ?? SelectableContexts.Select(context => context.Identifier).ToArray();
        if (identifiers.Count == 0) return current;
        var currentIndex = Array.IndexOf(identifiers.ToArray(), current);
        if (currentIndex < 0) return identifiers[0];
        return identifiers[Modulo(currentIndex + offset, identifiers.Count)];
    }

    internal static string CycledClassIdentifier(
        string contextIdentifier,
        string current,
        int offset)
    {
        var identifiers = SelectableContexts.FirstOrDefault(context =>
            context.Identifier == contextIdentifier)?.Labels.Select(label => label.Identifier).ToArray() ?? [];
        return CycledClassIdentifier(identifiers, current, offset);
    }

    internal static string CycledClassIdentifier(
        IReadOnlyList<string> identifiers,
        string current,
        int offset)
    {
        if (identifiers.Count == 0) return current;
        var currentIndex = Array.IndexOf(identifiers.ToArray(), current);
        if (currentIndex < 0) currentIndex = 0;
        return identifiers[Modulo(currentIndex + offset, identifiers.Count)];
    }

    internal static bool CanTrain(
        string classIdentifier,
        IEnumerable<EditableBox> currentRectangles,
        IEnumerable<Guid> savedExampleIdentifiers,
        Guid? editingExampleIdentifier)
    {
        if (currentRectangles.Any(rectangle =>
                !rectangle.IsNegative && rectangle.ClassIdentifier == classIdentifier))
        {
            return true;
        }
        return savedExampleIdentifiers.Any(identifier => identifier != editingExampleIdentifier);
    }

    internal static Rect Translated(Rect rectangle, Vector offset)
    {
        var source = Normalize(rectangle);
        return new Rect(
            Math.Clamp(source.X + offset.X, 0, 1 - source.Width),
            Math.Clamp(source.Y + offset.Y, 0, 1 - source.Height),
            source.Width,
            source.Height);
    }

    internal static Rect Resized(Rect rectangle, LabelingResizeCorner corner, Point point)
    {
        var source = Normalize(rectangle);
        var opposite = corner switch
        {
            LabelingResizeCorner.TopLeft => new Point(source.Right, source.Bottom),
            LabelingResizeCorner.TopRight => new Point(source.Left, source.Bottom),
            LabelingResizeCorner.BottomLeft => new Point(source.Right, source.Top),
            _ => new Point(source.Left, source.Top)
        };
        var bounded = new Point(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1));
        return new Rect(
            Math.Min(opposite.X, bounded.X),
            Math.Min(opposite.Y, bounded.Y),
            Math.Abs(opposite.X - bounded.X),
            Math.Abs(opposite.Y - bounded.Y));
    }

    internal static IReadOnlyDictionary<LabelingResizeCorner, Rect> ControlPoints(
        Rect rectangle,
        double markerSize = ControlPointSize)
    {
        var source = Normalize(rectangle);
        return new Dictionary<LabelingResizeCorner, Rect>
        {
            [LabelingResizeCorner.TopLeft] = new(
                source.Left - markerSize, source.Top - markerSize, markerSize, markerSize),
            [LabelingResizeCorner.TopRight] = new(
                source.Right, source.Top - markerSize, markerSize, markerSize),
            [LabelingResizeCorner.BottomLeft] = new(
                source.Left - markerSize, source.Bottom, markerSize, markerSize),
            [LabelingResizeCorner.BottomRight] = new(
                source.Right, source.Bottom, markerSize, markerSize)
        };
    }

    private static Rect Normalize(Rect rectangle) => new(
        Math.Min(rectangle.Left, rectangle.Right),
        Math.Min(rectangle.Top, rectangle.Bottom),
        Math.Abs(rectangle.Width),
        Math.Abs(rectangle.Height));

    private static int Modulo(int value, int divisor) => (value % divisor + divisor) % divisor;
}

internal sealed record EditableBox(
    Guid Id,
    string ClassIdentifier,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsNegative)
{
    internal Rect NormalizedRect => new(X, Y, Width, Height);

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

    internal EditableBox WithRect(Rect rectangle) => this with
    {
        X = rectangle.X,
        Y = rectangle.Y,
        Width = rectangle.Width,
        Height = rectangle.Height
    };

    public override string ToString() =>
        $"{(IsNegative ? "NEG " : "")}{ClassIdentifier} "
        + $"({X:F3}, {Y:F3}, {Width:F3}, {Height:F3})";
}

internal sealed class LabelingEditorDraft
{
    private sealed record Snapshot(IReadOnlyList<EditableBox> Rectangles, Guid? SelectedId);
    private readonly Stack<Snapshot> history = [];

    internal Guid? SelectedId { get; private set; }
    internal bool CanUndo => history.Count > 0;
    internal bool CanDelete => SelectedId is not null;

    internal void Reset(Guid? selectedId = null)
    {
        history.Clear();
        SelectedId = selectedId;
    }

    internal void Clear() => Reset();

    internal void ClearHistory() => history.Clear();

    internal EditableBox? Select(
        IReadOnlyList<EditableBox> rectangles,
        Point point,
        string? classIdentifier = null)
    {
        var match = rectangles.Reverse().FirstOrDefault(rectangle =>
            (classIdentifier is null || rectangle.ClassIdentifier == classIdentifier)
            && rectangle.NormalizedRect.Contains(point));
        SelectedId = match?.Id;
        return match;
    }

    internal EditableBox? Select(
        IReadOnlyList<EditableBox> rectangles,
        string classIdentifier)
    {
        var match = rectangles.LastOrDefault(rectangle =>
            rectangle.ClassIdentifier == classIdentifier);
        SelectedId = match?.Id;
        return match;
    }

    internal bool Select(IReadOnlyList<EditableBox> rectangles, Guid id)
    {
        if (!rectangles.Any(rectangle => rectangle.Id == id)) return false;
        SelectedId = id;
        return true;
    }

    internal EditableBox BeginRectangle(
        IList<EditableBox> rectangles,
        string classIdentifier,
        Point point,
        bool isNegative = false)
    {
        Record(rectangles);
        var rectangle = new EditableBox(
            Guid.NewGuid(), classIdentifier, point.X, point.Y, 0, 0, isNegative);
        rectangles.Add(rectangle);
        SelectedId = rectangle.Id;
        return rectangle;
    }

    internal bool BeginModification(IReadOnlyList<EditableBox> rectangles, Guid id)
    {
        if (!rectangles.Any(rectangle => rectangle.Id == id)) return false;
        Record(rectangles);
        SelectedId = id;
        return true;
    }

    internal bool Update(IList<EditableBox> rectangles, Guid id, Rect rectangle)
    {
        var index = IndexOf(rectangles, id);
        if (index < 0) return false;
        rectangles[index] = rectangles[index].WithRect(rectangle);
        SelectedId = id;
        return true;
    }

    internal bool Finish(
        IList<EditableBox> rectangles,
        Guid id,
        double minimumDimension = LabelingEditorPolicy.MinimumNormalizedDimension)
    {
        var rectangle = rectangles.FirstOrDefault(item => item.Id == id);
        if (rectangle is null) return false;
        if (rectangle.Width < minimumDimension || rectangle.Height < minimumDimension)
        {
            RestoreLatest(rectangles);
            return false;
        }
        DropNoOp(rectangles);
        return true;
    }

    internal bool DeleteSelected(IList<EditableBox> rectangles)
    {
        if (SelectedId is not { } id) return false;
        var index = IndexOf(rectangles, id);
        if (index < 0) return false;
        Record(rectangles);
        rectangles.RemoveAt(index);
        SelectedId = null;
        return true;
    }

    internal EditableBox? MarkNegative(IList<EditableBox> rectangles, Point point)
    {
        var match = rectangles.Reverse().FirstOrDefault(rectangle =>
            !rectangle.IsNegative && rectangle.NormalizedRect.Contains(point));
        if (match is null) return null;
        Record(rectangles);
        var index = IndexOf(rectangles, match.Id);
        var replacement = match with { IsNegative = true };
        rectangles[index] = replacement;
        SelectedId = replacement.Id;
        return replacement;
    }

    internal void Clear(IList<EditableBox> rectangles)
    {
        Record(rectangles);
        rectangles.Clear();
        SelectedId = null;
    }

    internal bool Undo(IList<EditableBox> rectangles)
    {
        if (history.Count == 0) return false;
        Restore(rectangles, history.Pop());
        return true;
    }

    private void Record(IEnumerable<EditableBox> rectangles) =>
        history.Push(new Snapshot(rectangles.ToArray(), SelectedId));

    private void RestoreLatest(IList<EditableBox> rectangles)
    {
        if (history.Count == 0) return;
        Restore(rectangles, history.Pop());
    }

    private void Restore(IList<EditableBox> rectangles, Snapshot snapshot)
    {
        rectangles.Clear();
        foreach (var rectangle in snapshot.Rectangles) rectangles.Add(rectangle);
        SelectedId = snapshot.SelectedId;
    }

    private void DropNoOp(IEnumerable<EditableBox> rectangles)
    {
        if (history.TryPeek(out var snapshot)
            && snapshot.SelectedId == SelectedId
            && snapshot.Rectangles.SequenceEqual(rectangles))
        {
            history.Pop();
        }
    }

    private static int IndexOf(IList<EditableBox> rectangles, Guid id)
    {
        for (var index = 0; index < rectangles.Count; index++)
            if (rectangles[index].Id == id) return index;
        return -1;
    }
}

internal static class DashboardPresentationPolicy
{
    internal static IReadOnlyList<string> Workspaces { get; } =
        ["Gameplay", "Label", "Stencil", "Model", "Hacker", "Ops"];
    internal static IReadOnlyList<string> BottomWorkspaces { get; } =
        ["Gameplay", "Label", "Stencil", "Model", "Hacker"];
    internal static IReadOnlyList<string> HackerInteractionModes { get; } =
        ["Ground", "Rooms", "Fit", "Free Fly", "Current"];

    internal static bool ShowsFraming(
        string workspace,
        bool phaseOneComplete,
        string? recognizedContext) =>
        ShowsGameplayFraming(workspace, phaseOneComplete, recognizedContext)
        || ShowsHackerFraming(workspace, recognizedContext);

    internal static bool ShowsGameplayFraming(
        string workspace,
        bool phaseOneComplete,
        string? recognizedContext) =>
        workspace == "Gameplay" && phaseOneComplete && IsGameplay(recognizedContext);

    internal static bool ShowsHackerFraming(
        string workspace,
        string? recognizedContext) =>
        workspace == "Hacker" && IsGameplay(recognizedContext);

    internal static bool ShowsOps(
        string workspace,
        GameRuntimeMode mode,
        string? recognizedContext) =>
        workspace == "Gameplay" && mode == GameRuntimeMode.Modded
        && IsGameplay(recognizedContext);

    internal static string HackerFramingMode(string interactionMode) =>
        interactionMode switch
        {
            "Fit" => "Fit",
            "Current" => "Current",
            _ => "Free Fly"
        };

    internal static string? WorkspaceForShortcut(Key key) => key switch
    {
        Key.G => "Gameplay",
        Key.L => "Label",
        Key.S => "Stencil",
        Key.M => "Model",
        Key.H => "Hacker",
        Key.O => "Ops",
        _ => null
    };

    internal static bool ShowsPathRecording(
        string workspace,
        GameRuntimeMode mode,
        string? recognizedContext) =>
        mode == GameRuntimeMode.Modded
        && (workspace == "Hacker"
            || workspace == "Gameplay" && IsGameplay(recognizedContext));

    internal static bool ShouldExitWorkspace(
        string workspace,
        Key key,
        bool isKeyDown,
        bool isRepeat,
        ModifierKeys modifiers) =>
        workspace is "Label" or "Model" or "Stencil"
        && key == Key.X && isKeyDown && !isRepeat
        && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0;

    internal static bool ShowsGroundTrackingTools(
        string workspace,
        string? recognizedContext) =>
        workspace == "Gameplay" && IsGameplay(recognizedContext);

    private static bool IsGameplay(string? context) => context is "game" or "Gameplay";
}

public partial class MainWindow : Window
{
    private const double CanvasWidth = 640;
    private const double CanvasHeight = 360;
    private const int LegacyGroundMigrationVersion = 1;
    private readonly ObservableCollection<SavedLabelingExample> examples = [];
    private readonly GameRuntimeMode gameMode;
    private readonly string? gamePath;
    private readonly ObservableCollection<EditableBox> boxes = [];
    private SavedLabelingExample? currentExample;
    private BgraFrame? currentFrame;
    private Guid captureGroupIdentifier = Guid.NewGuid();
    private Point? dragStart;
    private Rectangle? dragPreview;
    private CancellationTokenSource? visionCancellation;
    private Task? visionRunCompletion;
    private bool visionRestartPending;
    private bool atlasOperationInProgress;
    private Task? atlasOperationCompletion;
    private Task? persistentWorldInitialization;
    private CancellationTokenSource? startupCancellation;
    private CancellationTokenSource? gameLostCountdownCancellation;
    private Process? monitoredGameProcess;
    private readonly WindowsGameControlForwarder gameControls = new();
    private readonly WindowsNormalGameInput? normalGameInput;
    private readonly DispatcherTimer inputHeartbeat;
    private readonly DispatcherTimer viewportAnimationTimer;
    private readonly HashSet<Key> forwardedKeys = [];
    private readonly HashSet<Key> locallyConsumedKeys = [];
    private GroundTruthAtlasMosaic gameplayAtlas = new(pixelsPerWorldUnit: 1, sourceTopInsetFraction: 0);
    private GroundTruthAtlasMosaic? gameplayAtlasPresentationPending;
    private const int HackerAtlasTileSize = 256;
    private const double HackerAtlasPixelsPerWorldUnit = 1;
    private const int HackerAtlasSampleStride = 8;
    private const int HackerPublicationRetryLimit = 3;
    private readonly object hackerAtlasGate = new();
    private readonly HackerFramePreparer hackerFramePreparer = new();
    private readonly HackerAtlasPipeline hackerAtlasPipeline = new();
    private readonly VisualGameplayTracker visualGameplayTracker = new();
    private readonly GameplayPresentationState gameplayPresentation = new();
    private DateTimeOffset lastGpuDiagnosticAt;
    private Task? gpuDiagnosticWrite;
    private WindowsLiveWorldPipeline? liveWorldPipeline;
    private readonly PlaybackDivergenceMonitor inputPathDivergenceMonitor = new();
    private CancellationTokenSource? inputPathReplayCancellation;
    private PlaybackDivergenceDecision? inputPathReplayDivergence;
    private readonly WindowsMenuStencilRecognizer menuStencilRecognizer;
    private readonly Dictionary<AtlasTileCoordinate, Image> gameplayTileImages = [];
    private readonly Dictionary<HackerTileKey, Image> hackerTileImages = [];
    private readonly Dictionary<AtlasTileCoordinate, Image> hackerPreviewTileImages = [];
    private readonly Dictionary<AtlasTileCoordinate, ViewportRect> gameplayOccupiedTileBounds = [];
    private readonly Dictionary<HackerTileKey, HackerAtlasRect> hackerTileBounds = [];
    private readonly Dictionary<HackerTileKey, ViewportRect> hackerTileContentBounds = [];
    private readonly Dictionary<HackerTileKey, ViewportRect> hackerOccupiedTileBounds = [];
    private const int ReceiverFrameJoinCapacity = 180;
    private readonly HackerFrameSynchronizer receiverFrameSynchronizer = new(ReceiverFrameJoinCapacity);
    private readonly Dictionary<int, long> receiverFrameLabelRevisions = [];
    private readonly Dictionary<int, bool> receiverFrameHackerOwnership = [];
    private readonly Dictionary<int, long> receiverFrameHackerGenerations = [];
    private readonly Dictionary<int, long> receiverFrameCaptureIds = [];
    private readonly List<int> receiverFrameLabelRevisionOrder = [];
    private readonly SortedDictionary<long, HackerPipelineWork> receiverPendingHackerMatches = [];
    private readonly object receiverFrameJoinGate = new();
    private ChannelWriter<PendingVisionFrame>? receiverFrameAnalysisWriter;
    private ChannelWriter<HackerPipelineWork>? receiverFrameHackerWriter;
    private long receiverFrameJoinGeneration = -1;
    private long latestMatchedCaptureId = -1;
    private long latestPresentedMatchedCaptureId = -1;
    private readonly GameplayViewportController gameplayViewport = new();
    private readonly GameplayViewportController hackerViewport = new();
    private readonly LabelingEditorDraft labelDraft = new();
    // Older partials only need the historical Clear spelling. Both names point
    // at the single canonical draft/history state.
    private LabelingEditorDraft labelUndo => labelDraft;
    private readonly GroundTruthLabelController groundTruthLabels;
    private IReadOnlyList<GroundEditLine> legacyGroundEdits = [];
    private int legacyGroundMigrationVersion;
    private bool canPersistHackerEdits;
    private readonly List<TrackedGroundLine> trackedGround = [];
    private readonly Dictionary<string, RoomEdit> rooms = new(StringComparer.Ordinal);
    private readonly List<RoomConnectionEdit> roomConnections = [];
    private AtlasStateStore atlasStateStore;
    private readonly UserObjectCatalog objectCatalog;
    private readonly StencilInitializationCoordinator initialization;
    private readonly List<ClassChoice> customObjectChoices = [];
    private string activeAtlasDirectory;
    private readonly string hackerEditPath;
    private DateTimeOffset activeAtlasCreatedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset lastAtlasPersistedAt = DateTimeOffset.MinValue;
    private long gameplayPersistedRevision = -1;
    private Task<AtlasPersistencePublication>? atlasPersistTask;
    private long gameplayRenderedRevision = -1;
    private long hackerWorkGeneration;
    private HackerPipelinePublication? pendingHackerPublication;
    private HackerPipelinePublication? latestHackerPublication;
    private HackerAtlasPublication? latestStagedHackerAtlas;
    private int hackerPublicationScheduled;
    private ulong hackerPublishedRevision;
    private bool gameplayAtlasRequiresFullRewrite;
    private bool activeAtlasPersistenceBlocked;
    private readonly MaskedFrameMotionTracker frameMotionTracker = new();
    private readonly LowResolutionRoomMotionTracker inputPathMotionTracker = new();
    private long groundHypothesisResetGeneration;
    private LowResolutionTranslationDiagnostic? latestMotion;
    private double objectDetectionExtension = .12;
    private volatile bool createsObjectPixelDiagnostic;
    private long latestPresentedCaptureId = -1;
    private string workspace = "Gameplay";
    private string gameplayFramingValue = "Current";
    private string gameplayFraming
    {
        get => gameplayFramingValue;
        set { gameplayFramingValue = value; SelectModeButtons(GameplayControlsPanel, value); }
    }
    private string hackerInteractionModeValue = "Fit";
    private string hackerInteractionMode
    {
        get => hackerInteractionModeValue;
        set
        {
            hackerInteractionModeValue = value;
            SelectModeButtons(HackerCameraControlsPanel, value);
            SelectModeButtons(HackerControlsPanel, value);
        }
    }
    private bool pointerWithinLiveFrame;
    private bool passingPrimaryMouse;
    private bool hasFreshGameFrame;
    private bool gameLostCountdownStarted;
    private bool isClosing;
    private bool windowZoomed = true;
    private Rect restoredWindowBounds;
    private Point? lastForwardedPointer;
    private Point? viewportPanStart;
    private Matrix viewportPanMatrix = Matrix.Identity;
    private int? hackerAtlasMinimumTileX;
    private int? hackerAtlasMinimumTileY;
    private BgraFrame? hackerLiveFrame;
    private GroundTruthMatchedContext? latestGroundTruthContext;
    private BgraFrame? gameplayLiveFrame;
    private bool gameplayFrameRegistered;
    private ReceiverGroundTruthSample? gameplayLiveTelemetry;
    private int? gameplayAtlasMinimumTileX;
    private int? gameplayAtlasMinimumTileY;
    private HackerPreparedFrame? hackerLivePrepared;
    private HackerAtlasOutput? hackerAtlasOutput;
    private bool hackerPreviewActive;
    private Point? groundDragStart;
    private GroundTruthCameraTransform? groundDragTransform;
    private GroundTruthRect groundDragBounds;
    private EditableBox? modifyingBox;
    private Point? modifyDragStart;
    private EditableBox? modifyOriginal;
    private LabelingResizeCorner? modifyingCorner;
    private RoomEdit? activeRoom;
    private Point? roomDragStart;
    private double roomOriginalOffsetX;
    private double roomOriginalOffsetY;
    private string? initializationTargetContext;
    private bool refreshingClassChoices;
    private bool factoryResetRequested;
    private bool shutdownInProgress;
    private bool shutdownReady;
    private sealed record PendingVisionFrame(
        BgraFrame Frame,
        long LabelRevision,
        double CaptureTimestamp,
        int? RenderedGameFrame = null,
        ReceiverGroundTruthSample? ReceiverTelemetry = null,
        double? ReceiverObservedAt = null,
        bool OwnsHackerWork = false,
        bool IntegratesHackerAtlas = false,
        long HackerWorkGeneration = 0);

    private readonly record struct HackerTileKey(Guid RoomId, long LayerId);

    private sealed record HackerPipelineWork(
        HackerMatchedFrame Match,
        long CaptureGeneration,
        long WorkGeneration,
        bool OwnsHackerWork);

    private sealed record HackerTilePublication(
        HackerTileKey Key,
        string SceneName,
        BitmapSource Bitmap,
        HackerAtlasRect Bounds,
        ViewportRect? ContentBounds,
        ulong Revision);

    private sealed record HackerAtlasPublication(
        HackerAtlasOutput Output,
        IReadOnlyList<HackerTilePublication> Tiles,
        ulong Revision);

    private sealed record HackerPipelinePublication(
        HackerPreparedFrame Prepared,
        BitmapSource LiveBitmap,
        HackerAtlasPublication? Atlas,
        long CaptureGeneration,
        long WorkGeneration,
        int ApplyAttempt = 0);

    private sealed record GroundTruthMatchedContext(
        BgraFrame Frame,
        ReceiverGroundTruthSample Telemetry,
        double ReceiverObservedAt,
        IReadOnlyList<GroundLinePresenceReview> GroundPresenceReviews,
        bool AdmitsTracking,
        long CaptureGeneration,
        long CaptureId);

    private sealed class GroundTruthSeedEvidence
    {
        private readonly GroundLinePresence presence = new(
            minimumObservationSeconds: .2,
            confirmedLossGraceSeconds: 3);
        private string? sceneName;

        internal void Reset()
        {
            presence.Reset();
            sceneName = null;
        }

        internal IReadOnlyList<GroundLinePresenceReview> Observe(
            BgraFrame frame,
            IReadOnlyList<DetectedFloorLine> lines,
            IReadOnlyList<PixelRect> exclusions,
            ReceiverGroundTruthSample telemetry,
            double timestamp,
            bool poseVerified)
        {
            if (string.IsNullOrWhiteSpace(telemetry.SceneName)
                || !telemetry.CameraAvailable
                || !telemetry.HasFiniteCoordinates()
                || !double.IsFinite(timestamp))
            {
                Reset();
                return [];
            }
            if (!string.Equals(sceneName, telemetry.SceneName, StringComparison.Ordinal))
            {
                presence.Reset();
                sceneName = telemetry.SceneName;
            }
            var scale = telemetry.PixelsPerWorldUnit(frame.Height);
            if (scale is not > 0 || !double.IsFinite(scale.Value)) return [];
            var camera = new AtlasPoint(
                telemetry.CameraX * scale.Value,
                telemetry.CameraY * scale.Value);
            presence.Update(
                poseVerified
                    ? lines.Select(line => line.ToSemanticLine()).ToArray()
                    : [],
                camera,
                frame.Width,
                frame.Height,
                timestamp,
                poseVerified,
                exclusions);
            return presence.Reviews(camera, frame.Width, frame.Height);
        }
    }

    private sealed class GroundHypothesisWorkerState
    {
        internal GroundHypothesisTracker Tracker { get; } = new(
            minimumObservationSeconds: .2,
            backgroundGlobalSearch: true,
            confirmedLossGraceSeconds: 3,
            productionCameraSolver: true);
        internal bool NeedsCameraAnchor { get; set; } = true;
        internal GroundSemanticAtlas SemanticAtlas { get; } = new();
        internal GroundReferenceTracker ReferenceTracker { get; } = new();
        internal string? ReceiverSceneName { get; private set; }
        internal int FrameWidth { get; private set; }
        internal int FrameHeight { get; private set; }

        internal void PrepareFrame(
            int width,
            int height,
            string? receiverSceneName)
        {
            if (FrameWidth != 0 && (FrameWidth != width || FrameHeight != height))
            {
                Reset();
            }
            FrameWidth = width;
            FrameHeight = height;

            if (string.IsNullOrWhiteSpace(receiverSceneName)) return;
            if (ReceiverSceneName is not null
                && !string.Equals(
                    ReceiverSceneName,
                    receiverSceneName,
                    StringComparison.Ordinal))
            {
                Reset();
                FrameWidth = width;
                FrameHeight = height;
            }
            ReceiverSceneName = receiverSceneName;
        }

        internal void SuspendTracking()
        {
            Tracker.Reset(keepingGlobalFeatures: true);
            ReferenceTracker.Reset();
        }

        internal void Reset()
        {
            NeedsCameraAnchor = true;
            Tracker.Reset();
            SemanticAtlas.Reset();
            ReferenceTracker.Reset();
            ReceiverSceneName = null;
            FrameWidth = 0;
            FrameHeight = 0;
        }
    }

    private sealed class NormalAtlasRoomIdentity
    {
        private long captureGeneration = long.MinValue;
        private long roomEpoch;
        private bool transitionResumeActive;

        internal string Observe(
            long currentCaptureGeneration,
            bool resumedAfterTransition)
        {
            if (captureGeneration != currentCaptureGeneration)
            {
                captureGeneration = currentCaptureGeneration;
                roomEpoch = 0;
                transitionResumeActive = false;
            }
            if (resumedAfterTransition && !transitionResumeActive)
            {
                roomEpoch = checked(roomEpoch + 1);
            }
            transitionResumeActive = resumedAfterTransition;
            return FormattableString.Invariant(
                $"normal:{currentCaptureGeneration}:room:{roomEpoch}");
        }
    }

    private sealed record AtlasPersistencePublication(
        GroundTruthAtlasMosaic Atlas,
        long Revision,
        bool FullReplacement,
        DateTimeOffset CompletedAt,
        double DurationMilliseconds);
    private string? recognizedMenuContext;
    private string? recognizedMenuSelection;

    private sealed record VisionAnalysisResult(
        BgraFrame Frame,
        IReadOnlyList<ObjectDetection> Detections,
        IReadOnlyList<DetectedFloorLine> Ground,
        AtlasMosaicDelta? GameplayTiles,
        ReceiverGroundTruthSample GameplayTelemetry,
        ReceiverGroundTruthSample? ReceiverTelemetry,
        LiveGameStatus? Status,
        LowResolutionTranslationDiagnostic? Motion,
        MenuStencilResult? MenuStencil,
        long MenuGeneration,
        HudStencilResult? HudStencil,
        bool AdmitsTracking,
        string StencilDiagnostic)
    {
        public long AnalysisStartedTimestamp { get; init; }
        public double AnalysisMilliseconds { get; init; }
        public double QueueMilliseconds { get; init; }
        public double GroundMilliseconds { get; init; }
        public double AtlasAndWorldMilliseconds { get; init; }
        public string PoseSource { get; init; } = "held";
        public bool AllowsAtlasWrite { get; init; }
        public string? MenuContext => Status?.ContextIdentifier;
        public LiveObjectDetectionSnapshot Objects { get; init; } = LiveObjectDetectionSnapshot.Empty;
        public LiveObjectInferenceDiagnostics ObjectDiagnostics { get; init; } = new();
        public long LabelRevision { get; init; }
        public long CaptureGeneration { get; init; }
        public AtlasPoint? WorldBasisOffset { get; init; }
        public double? WorldBasisTimestamp { get; init; }
        public GroundTruthAtlasMosaic? AtlasSource { get; init; }
        public bool ReplacesGameplayAtlas { get; init; }
        public GroundTruthAtlasMosaic? GameplayAtlasReplacement { get; init; }
        public LiveWorldLoopClosureEdge? AcceptedClosure { get; init; }
        public int LiveWorldRevision { get; init; }
        public AtlasPoint AppliedWorldCorrection { get; init; }
        public double CaptureTimestamp { get; init; }
        public double? ReceiverObservedAt { get; init; }
        public bool OwnsHackerWork { get; init; }
        public bool IntegratesHackerAtlas { get; init; }
        public long HackerWorkGeneration { get; init; }
        public IReadOnlyList<GroundLinePresenceReview> GroundPresenceReviews { get; init; } = [];
        public GroundHypothesisTrackingResult GroundHypotheses { get; init; } =
            GroundHypothesisTrackingResult.Empty;
    }

    private sealed class GameWindowLostException : Exception
    {
    }

    public MainWindow(GameRuntimeMode gameMode, string? gamePath = null)
    {
        this.gameMode = gameMode;
        this.gamePath = gamePath;
        InitializeComponent();
        gameControls.GroundTruthReceived += OnReceiverGroundTruth;
        gameplayFraming = gameplayFramingValue;
        hackerInteractionMode = hackerInteractionModeValue;
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
        inputHeartbeat.Tick += (_, _) =>
        {
            gameControls.Heartbeat();
            SyncLabelPauseState();
            UpdateModeAvailability();
        };
        viewportAnimationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        viewportAnimationTimer.Tick += (_, _) => AdvanceViewportAnimations();
        viewportAnimationTimer.Start();
        HackerViewportPanel.SizeChanged += (_, _) =>
            ApplyViewport(AtlasCanvas, HackerViewportPanel,
                DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode),
                HackerLiveImage);
        AtlasCanvas.LostMouseCapture += (_, _) =>
        {
            if (groundDragStart is not null) CancelGroundTruthGesture();
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        PreviewGotKeyboardFocus += MainWindow_PreviewGotKeyboardFocus;
        Deactivated += (_, _) =>
        {
            ReleaseForwardedInput();
            viewportPanStart = null;
            CancelGroundTruthGesture();
            EndLabelPan();
            HideLabelCrosshair();
        };
        if (gameMode == GameRuntimeMode.Normal)
        {
            normalGameInput = new WindowsNormalGameInput(this,
                AllowsNormalGameInput,
                key => HandleLocalShortcut(key),
                SetStatus);
            SettingsOverlay.IsVisibleChanged += (_, _) => normalGameInput.UpdateContext();
        }
        ExamplesList.ItemsSource = examples;
        BoxesList.ItemsSource = boxes;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = IOPath.Combine(local, "HollowKnightVision");
        activeAtlasDirectory = IOPath.Combine(root, "atlas-v1", "active");
        atlasStateStore = new AtlasStateStore(IOPath.Combine(root, "atlas-v1", "states"));
        groundTruthLabels = new GroundTruthLabelController();
        objectCatalog = new UserObjectCatalog(IOPath.Combine(root, "object-catalog-v1.json"));
        customObjectChoices.AddRange(objectCatalog.Load().Select(item =>
            new ClassChoice(item.Name, item.Identifier)));
        hackerEditPath = IOPath.Combine(root, "atlas-v1", "hacker-edits.json");
        ExamplesRootText.Text = IOPath.Combine(root, "labeling-v1", "examples");
        initialization = new StencilInitializationCoordinator(
            new LabelingExampleStore(ExamplesRootText.Text), gameMode);
        menuStencilRecognizer = new WindowsMenuStencilRecognizer(ExamplesRootText.Text, initialization.LanguageIdentifier);
        ModelExamplesRootText.Text = ExamplesRootText.Text;
        DatasetRootText.Text = IOPath.Combine(root, "training-v1", "datasets");
        TrainingOutputText.Text = IOPath.Combine(root, "training-v1", "runs", Guid.NewGuid().ToString("D"));
        modelLifecycle = new LabelingModelLifecycle(
            new LabelingModelReviewStore(IOPath.Combine(root, "training-v1", "runs"), DatasetRootText.Text),
            new LabelingModelVersionStore(IOPath.Combine(root, "models-v1")));
        InitializeModels();
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
        RefreshContextChoices();
        foreach (var item in StencilInitializationCoordinator.SupportedLanguages)
            SettingsLanguageCombo.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Identifier });
        SettingsLanguageCombo.SelectedItem = SettingsLanguageCombo.Items.Cast<ComboBoxItem>()
            .First(item => Equals(item.Tag, initialization.LanguageIdentifier));
        InitializeLabelingSelection(root);
        LoadHackerEdits();
        RefreshExamples();
        RefreshInitializationStatus();
        RefreshOps();
        UpdateModeAvailability();
        RefreshSettingsText();
        Loaded += async (_, _) =>
        {
            atlasOperationInProgress = true;
            GameplayAtlasManager.IsEnabled = false;
            try
            {
                var initializationTask = InitializePersistentWorldAsync();
                persistentWorldInitialization = initializationTask;
                await initializationTask;
            }
            finally
            {
                persistentWorldInitialization = null;
                atlasOperationInProgress = false;
                GameplayAtlasManager.IsEnabled = true;
            }
            if (isClosing) return;
            if (gameMode == GameRuntimeMode.Modded)
            {
                gameControls.Start();
                inputHeartbeat.Start();
            }

            PathRecordingButton.Visibility = DashboardPresentationPolicy.ShowsPathRecording(
                workspace, gameMode, recognizedMenuContext)
                ? Visibility.Visible : Visibility.Collapsed;
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (shutdownReady)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (shutdownInProgress) return;
        shutdownInProgress = true;
        isClosing = true;
        _ = PrepareShutdownAsync();
    }

    private async Task PrepareShutdownAsync()
    {
        if (!StopTrainingForShutdown(out var trainingShutdownError) && factoryResetRequested)
        {
            factoryResetRequested = false;
            MessageBox.Show("Factory reset was not performed because training could not finish shutting down. " + trainingShutdownError,
                "Factory reset cancelled", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        // Capture owns every live-world and atlas mutation. Cancel it before
        // waiting for any persistence or destructive filesystem operation.
        StopVision();
        objectReferencePanel?.StopAndDrain();
        MenuParticles.SetScene(null, null);
        gameLostCountdownCancellation?.Cancel();
        startupCancellation?.Cancel();
        inputPathReplayCancellation?.Cancel();
        StopMonitoringGameProcess();
        inputHeartbeat.Stop();
        viewportAnimationTimer.Stop();
        normalGameInput?.Dispose();
        ReleaseForwardedInput();
        try
        {
            if (visionRunCompletion is { } captureFinished)
                await captureFinished;
            if (replayUiFinished is { } replayUi) await replayUi.Task;
            await worldWorker.DrainAsync();
            if (persistentWorldInitialization is { } worldInitialization)
                await worldInitialization;
            if (atlasOperationCompletion is { } atlasOperation)
                await atlasOperation;
            if (factoryResetRequested)
            {
                await AwaitAtlasPersistenceAsync();
            }
            else
            {
                await FlushActiveAtlasAsync();
                PersistHackerEdits();
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Final save failed", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        try
        {
            await gameControls.DisposeAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Receiver shutdown failed", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        if (factoryResetRequested)
        {
            try
            {
                _ = await Task.Run(VisionResetController.FactoryReset);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Factory reset failed", MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        shutdownReady = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        isClosing = true;
        base.OnClosed(e);
    }

    private async Task EnsureGameAndVisionAsync()
    {
        if (startupCancellation is not null) return;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        startupCancellation = cancellation;
        try
        {
            var selectedExecutable = HollowKnightGameLauncher.FindCompatibleExecutable(
                    requireModded: gameMode == GameRuntimeMode.Modded,
                    explicitPath: gamePath)
                ?? throw new FileNotFoundException(
                    $"A compatible {gameMode} Hollow Knight installation was not found.");
            var running = Process.GetProcessesByName("hollow_knight");
            var matchingGameIsRunning = false;
            try
            {
                foreach (var process in running)
                {
                    var executable = TryExecutablePath(process) ?? throw new InvalidOperationException(
                        "Vision could not verify whether the running Hollow Knight installation " +
                        "matches the selected mode.");
                    if (string.Equals(
                        executable,
                        selectedExecutable,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        matchingGameIsRunning = true;
                    }
                    else
                    {
                        await CloseRunningGameAsync(process, cancellation.Token);
                    }
                }
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }
            var window = matchingGameIsRunning ? HollowKnightWindowLocator.FindBest() : null;
            if (!matchingGameIsRunning)
            {
                _ = HollowKnightGameLauncher.Start(selectedExecutable);
                window = await WaitForGameWindowAsync(cancellation.Token);
            }
            else if (window is null)
            {
                window = await WaitForGameWindowAsync(cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            ValidateGameWindowMode(window);
            MonitorGameProcess(window.ProcessId);
            normalGameInput?.Start(window);
            if (visionCancellation is null)
            {
                StartVision_Click(this, new RoutedEventArgs());
            }
            if (normalGameInput is null)
            {
                Activate();
                Focus();
            }
            else
            {
                // Stock Hollow Knight remains the native input foreground;
                // Vision is a clickable non-activating overlay above it.
                normalGameInput.UpdateContext();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!isClosing && !gameLostCountdownStarted)
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

    private static async Task CloseRunningGameAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (process.HasExited) return;
        if (!process.CloseMainWindow())
        {
            throw new InvalidOperationException(
                "The running Hollow Knight could not be closed. Close it and try again.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The running Hollow Knight did not close within 30 seconds.");
        }
    }

    private static string? TryExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception error) when (error is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private void ValidateGameWindowMode(HollowKnightWindow window)
    {
        using var process = Process.GetProcessById(window.ProcessId);
        var executable = TryExecutablePath(process) ?? throw new InvalidOperationException(
            "Vision could not verify whether the running Hollow Knight installation " +
            "matches the selected mode. Close it and launch the intended copy with --game-path.");
        HollowKnightGameLauncher.ValidateInstallation(
            executable,
            requireModded: gameMode == GameRuntimeMode.Modded);
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var expected = HollowKnightGameLauncher.ResolveExecutable(gamePath);
            if (!string.Equals(executable, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A different Hollow Knight installation is running. " +
                    "Close it before using the selected --game-path.");
            }
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

    private void MonitorGameProcess(int processId)
    {
        if (monitoredGameProcess is not null)
        {
            try
            {
                if (monitoredGameProcess.Id == processId) return;
            }
            catch (InvalidOperationException)
            {
            }
        }

        StopMonitoringGameProcess();
        menuStencilRecognizer.ResetGameSession();
        try
        {
            var process = Process.GetProcessById(processId);
            process.EnableRaisingEvents = true;
            process.Exited += GameProcess_Exited;
            monitoredGameProcess = process;
            if (process.HasExited) BeginGameLostCountdown();
        }
        catch (ArgumentException)
        {
            BeginGameLostCountdown();
        }
    }

    private void StopMonitoringGameProcess()
    {
        if (monitoredGameProcess is null) return;
        monitoredGameProcess.Exited -= GameProcess_Exited;
        monitoredGameProcess.Dispose();
        monitoredGameProcess = null;
    }

    private void GameProcess_Exited(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(BeginGameLostCountdown);

    private bool MonitoredGameHasExited()
    {
        if (monitoredGameProcess is null) return false;
        try
        {
            return monitoredGameProcess.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void BeginGameLostCountdown()
    {
        if (isClosing || gameLostCountdownStarted) return;
        gameLostCountdownStarted = true;
        normalGameInput?.Dispose();
        startupCancellation?.Cancel();
        inputHeartbeat.Stop();
        ReleaseForwardedInput();
        StopVision();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        GameLostOverlay.Visibility = Visibility.Visible;

        var cancellation = new CancellationTokenSource();
        gameLostCountdownCancellation = cancellation;
        _ = RunGameLostCountdownAsync(cancellation);
    }

    private async Task RunGameLostCountdownAsync(CancellationTokenSource cancellation)
    {
        try
        {
            for (var remaining = GameLossCountdown.InitialSeconds; remaining >= 0; remaining--)
            {
                GameLostCountdownText.Text = GameLossCountdown.Message(remaining);
                await Task.Delay(
                    remaining == 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(1),
                    cancellation.Token);
            }
            Close();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(gameLostCountdownCancellation, cancellation))
                gameLostCountdownCancellation = null;
            cancellation.Dispose();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsActive) return;
        if (FirstResponderConsumesTextInput() && !(SettingsOverlay.Visibility == Visibility.Visible && key is Key.Left or Key.Right or Key.Up or Key.Down)) return;
        if (workspace == "Hacker" && hackerInteractionMode == "Ground"
            && key == Key.Z && !e.IsRepeat
            && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            GroundUndo_Click(this, new RoutedEventArgs());
            locallyConsumedKeys.Add(key);
            e.Handled = true;
            return;
        }
        if (workspace == "Label" && key == Key.Z
            && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (LabelPanel.IsEnabled) UndoLabelEdit();
            locallyConsumedKeys.Add(key);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return;
        }
        if (HandleModelShortcut(key)
            || HandleLocalShortcut(key, e.IsRepeat, Keyboard.Modifiers))
        {
            locallyConsumedKeys.Add(key);
            normalGameInput?.ReserveLocalKey(key);
            normalGameInput?.UpdateContext();
            e.Handled = true;
            return;
        }
        if (workspace == "Label" && SettingsOverlay.Visibility != Visibility.Visible) return;
        if (GameControlKeyPolicy.IsModelReviewReserved(workspace, key))
        {
            return;
        }
        if (normalGameInput is not null)
        {
            if (normalGameInput.ForwardFirstKey(key, e.IsRepeat)) e.Handled = true;
            return;
        }
        if (gameMode != GameRuntimeMode.Modded
            || !GameControlKeyPolicy.TryMapReceiverButton(workspace, key, out var button)) return;
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
        if (locallyConsumedKeys.Remove(key))
        {
            e.Handled = true;
            return;
        }
        if (normalGameInput is not null)
        {
            if (normalGameInput.ForwardKeyUp(key)) e.Handled = true;
            return;
        }
        if (!IsActive && !forwardedKeys.Contains(key)) return;
        if (gameMode != GameRuntimeMode.Modded
            || !GameControlKeyPolicy.TryMapReceiverButton(key, out var button)) return;
        if (!forwardedKeys.Remove(key))
        {
            if (FirstResponderConsumesTextInput() && !(SettingsOverlay.Visibility == Visibility.Visible && key is Key.Left or Key.Right or Key.Up or Key.Down)) return;
            if (workspace == "Label"
                || GameControlKeyPolicy.IsModelReviewReserved(workspace, key))
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

    private bool HandleLocalShortcut(
        Key key,
        bool isRepeat = false,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        if (isRepeat) return false;
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape) SettingsOverlay.Visibility = Visibility.Collapsed;
            return key is not (Key.Left or Key.Right or Key.Up or Key.Down);
        }
        if (workspace == "Label")
        {
            if (!LabelPanel.IsEnabled && key is Key.R or Key.Left or Key.Right or Key.Up or Key.Down
                or Key.D1 or Key.D2 or Key.D3 or Key.D4
                or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4) return true;
            if (key is Key.Left or Key.Right)
            {
                CycleLabelContext(key == Key.Left ? -1 : 1);
                return true;
            }
            if (key is Key.Up or Key.Down)
            {
                CycleLabelObject(key == Key.Up ? -1 : 1);
                return true;
            }
            var mode = LabelingEditorPolicy.ModeForKey(key);
            if (mode is not null)
            {
                if (!LabelingEditorPolicy.AvailableModes(CurrentContextName).Contains(mode.Value)) return true;
                var button = LabelModeButton(mode.Value);
                button.IsChecked = true;
                LabelMode_Click(button, new RoutedEventArgs());
                return true;
            }
            if (key == Key.R)
            {
                RecentFrames_Click(this, new RoutedEventArgs());
                return true;
            }
        }
        if (workspace == "Hacker" && hackerInteractionMode == "Ground")
        {
            var mode = key switch
            {
                Key.D1 or Key.NumPad1 => GroundAddButton,
                Key.D2 or Key.NumPad2 => GroundModifyButton,
                Key.D3 or Key.NumPad3 => GroundDeleteButton,
                Key.D4 or Key.NumPad4 => GroundNegativeButton,
                _ => null
            };
            if (mode is not null)
            {
                mode.IsChecked = true;
                GroundMode_Click(mode, new RoutedEventArgs());
                return true;
            }
        }
        if (DashboardPresentationPolicy.ShouldExitWorkspace(
                workspace, key, isKeyDown: true, isRepeat, modifiers))
        {
            SelectWorkspace("Gameplay");
            return true;
        }
        var destination = DashboardPresentationPolicy.WorkspaceForShortcut(key);
        if (destination is not null)
        {
            if (workspace == destination && destination != "Gameplay")
            {
                destination = "Gameplay";
            }
            SelectWorkspace(destination);
            return true;
        }
        if (key == Key.K && DashboardPresentationPolicy.ShowsPathRecording(
                workspace, gameMode, recognizedMenuContext))
        {
            PathRecording_Click(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    private static bool FirstResponderConsumesTextInput() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox;

    private bool AllowsNormalGameInput() => !isClosing && !gameLostCountdownStarted
        && NormalGameInputPolicy.AllowsGameInput(
            workspace, SettingsOverlay.Visibility == Visibility.Visible,
            IsActive && FirstResponderConsumesTextInput());

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
        if (GameplayViewportPolicy.CanPan(workspace == "Gameplay"
                ? gameplayFraming
                : DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode)))
        {
            var surface = workspace == "Gameplay" ? GameplayAtlasCanvas : AtlasCanvas;
            AdvanceViewportAnimations();
            viewportPanStart = e.GetPosition((IInputElement)sender);
            viewportPanMatrix = (surface.RenderTransform as MatrixTransform)?.Matrix ?? Matrix.Identity;
            Mouse.Capture((IInputElement)sender, CaptureMode.Element);
            e.Handled = true;
            if ((workspace == "Gameplay"
                    ? gameplayFraming
                    : DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode)) != "Current") return;
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
            RenderHackerAtlasLayout();
            e.Handled = true;
            return;
        }
        if (viewportPanStart is { } panStart && e.LeftButton == MouseButtonState.Pressed)
        {
            var current = e.GetPosition((IInputElement)sender);
            var currentPointer = passingPrimaryMouse ? NormalizedGamePointer(e) : null;
            if ((current - panStart).Length < 4)
            {
                if (passingPrimaryMouse)
                {
                    if (currentPointer is { } pointer
                        && gameControls.ForwardPointer("leftDragged", pointer.X, pointer.Y))
                    {
                        pointerWithinLiveFrame = true;
                        lastForwardedPointer = pointer;
                    }
                    else if (currentPointer is null)
                    {
                        SendPointerExit();
                    }
                }
                return;
            }
            if (passingPrimaryMouse)
            {
                if (currentPointer is { } pointer)
                    gameControls.ForwardPointer("leftUp", pointer.X, pointer.Y);
                else
                    SendPointerExit();
                passingPrimaryMouse = false;
                if (currentPointer is null) lastForwardedPointer = null;
            }
            if (workspace == "Gameplay") gameplayFraming = "Free Fly";
            else hackerInteractionMode = "Free Fly";
            var matrix = viewportPanMatrix;
            matrix.OffsetX += current.X - panStart.X;
            matrix.OffsetY += current.Y - panStart.Y;
            var surface = workspace == "Gameplay" ? GameplayAtlasCanvas : AtlasCanvas;
            var controller = ViewportController(surface);
            ApplySettledViewportTransform(surface, controller.SetFreeFlyTransform(
                new ViewportTransform(matrix.M11, matrix.OffsetX, matrix.OffsetY)));
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
            if (!passingPrimaryMouse) return;
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
        "Gameplay" => GameplayViewportPolicy.ForwardsGamePointer(gameplayFraming),
        "Hacker" => hackerInteractionMode is "Fit" or "Current"
            && GameplayViewportPolicy.ForwardsGamePointer(hackerInteractionMode),
        _ => false
    };

    private Point? NormalizedGamePointer(MouseEventArgs e)
    {
        FrameworkElement liveFrame = workspace == "Hacker" ? HackerLiveImage : GameplayImage;
        if (!liveFrame.IsVisible || liveFrame.ActualWidth <= 0 || liveFrame.ActualHeight <= 0)
        {
            return null;
        }
        var host = workspace == "Hacker" ? HackerViewportPanel : GameplayPanel;
        Point point;
        Rect bounds;
        try
        {
            point = e.GetPosition(host);
            bounds = liveFrame.TransformToAncestor(host).TransformBounds(
                new Rect(0, 0, liveFrame.ActualWidth, liveFrame.ActualHeight));
        }
        catch (InvalidOperationException) { return null; }
        var normalized = GameplayViewportPolicy.NormalizeGamePointer(
            new ViewportPoint(point.X, point.Y),
            new ViewportRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            invertY: true);
        return normalized is { } value ? new Point(value.X, value.Y) : null;
    }

    private void AtlasViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var mode = workspace == "Gameplay"
            ? gameplayFraming
            : DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode);
        if (mode is not ("Current" or "Free Fly") || e.Delta == 0 || viewportPanStart is not null) return;
        var surface = workspace == "Gameplay" ? GameplayAtlasCanvas : AtlasCanvas;
        var host = workspace == "Gameplay" ? GameplayPanel : HackerViewportPanel;
        var liveFrame = workspace == "Gameplay" ? GameplayImage : HackerLiveImage;
        var controller = ViewportController(surface);
        var timestamp = ViewportTimestamp();
        if (mode == "Current")
        {
            var follow = LiveFrameBounds(liveFrame);
            if (follow is { } bounds)
            {
                var focus = new ViewportPoint(bounds.CenterX, bounds.CenterY);
                ApplySettledViewportTransform(surface, controller.ZoomCurrent(
                    e.Delta,
                    new ViewportSize(host.ActualWidth, host.ActualHeight),
                    bounds,
                    focus,
                    timestamp));
            }
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(host);
        ApplySettledViewportTransform(surface, controller.ZoomFreeFly(
            e.Delta, new ViewportPoint(point.X, point.Y), timestamp));
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
            WindowZoomButton.ToolTip = initialization.Text("Maximize");
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

    private void StencilsWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SelectWorkspace("Stencil");
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

    private bool SelectWorkspace(string destination, bool capture = true)
    {
        if (destination == "Model" && !initialization.IsFirstPhaseComplete) return false;
        if (destination == "Hacker" && (!CanUseHacker() || recognizedMenuContext != "game"))
        {
            SetStatus(gameMode == GameRuntimeMode.Normal
                ? "Hacker requires Modded Hollow Knight. Gameplay remains available."
                : "Hacker is waiting for compatible ground-truth telemetry.");
            GameplayWorkspaceButton.IsChecked = workspace == "Gameplay";
            return false;
        }
        if (destination == "Ops") { Settings_Click(this, new RoutedEventArgs()); return false; }
        if (destination == "Label" && workspace == "Label") return LabelPanel.IsEnabled;
        var enteringLabel = destination == "Label";
        if (labelCapture.State is GamePauseState.Pausing or GamePauseState.Resuming
            || enteringLabel && (!labelCapture.CanToggle || gameControls.IsRecording))
        {
            SetStatus(gameControls.IsRecording ? "Stop path recording before labeling."
                : "Labeling is waiting for a clean game frame and compatible pause controls.");
            RefreshWorkspaceSelection();
            return false;
        }
        var leavingLabel = workspace == "Label" && destination != "Label";
        if (leavingLabel)
        {
            EndLabelPan();
            HideLabelCrosshair();
        }
        if (leavingLabel && initializationTargetContext is not null
            && !initialization.Steps.Any(step => step.ContextIdentifier == initializationTargetContext))
            initializationTargetContext = null;
        if (workspace == "Hacker" && destination != "Hacker")
        {
            CancelGroundTruthGesture();
            InvalidatePendingHackerWork();
        }
        if (destination is "Label" or "Model") ReleaseForwardedInput();
        if (destination is not ("Gameplay" or "Hacker")) SendPointerExit();
        workspace = destination;
        if (leavingLabel) EndLabelSession();
        GameplayPanel.Visibility = destination == "Gameplay" ? Visibility.Visible : Visibility.Collapsed;
        LabelPanel.Visibility = destination == "Label" ? Visibility.Visible : Visibility.Collapsed;
        ModelPanel.Visibility = destination == "Model" ? Visibility.Visible : Visibility.Collapsed;
        HackerPanel.Visibility = destination == "Hacker" ? Visibility.Visible : Visibility.Collapsed;

        GameplayControlsPanel.Visibility = destination == "Gameplay"
            ? Visibility.Visible : Visibility.Collapsed;
        HackerControlsPanel.Visibility = destination == "Hacker"
            ? Visibility.Visible : Visibility.Collapsed;
        PathRecordingButton.Visibility = DashboardPresentationPolicy.ShowsPathRecording(
                destination, gameMode, recognizedMenuContext)
            ? Visibility.Visible : Visibility.Collapsed;

        RefreshWorkspaceSelection();

        if (enteringLabel) BeginLabelSession(capture);
        StencilsPanel.Visibility = destination == "Stencil" ? Visibility.Visible : Visibility.Collapsed;
        if (destination == "Stencil") RefreshStencilsWorkspace();
        RefreshGuidance();
        UpdateModeAvailability();
        if (destination == "Model") RefreshModelWorkspace();
        if (destination == "Ops") RefreshOps();
        if (destination == "Hacker")
        {
            if (!RestoreLatestHackerPresentation()
                && !PresentLatestGroundTruthContext())
            {
                RedrawHackerEdits();
            }
        }
        normalGameInput?.UpdateContext();
        return true;
    }

    private bool CanUseHacker() =>
        gameMode == GameRuntimeMode.Modded && gameControls.HasFreshGroundTruth;

    private void RefreshWorkspaceSelection()
    {
        GameplayWorkspaceButton.IsChecked = workspace == "Gameplay";
        LabelWorkspaceButton.IsChecked = workspace == "Label";
        ModelWorkspaceButton.IsChecked = workspace == "Model";
        HackerWorkspaceButton.IsChecked = workspace == "Hacker";
        OpsWorkspaceButton.IsChecked = workspace == "Ops";
        StencilsWorkspaceButton.IsChecked = workspace == "Stencil";
    }

    private bool CanUseOps() =>
        gameMode == GameRuntimeMode.Modded && gameControls.PlayerOpsAvailable;

    private bool ShowsOpsControl() => visionCancellation is not null
        && DashboardPresentationPolicy.ShowsOps(workspace, gameMode, recognizedMenuContext);

    private void UpdateModeAvailability()
    {
        if (HackerWorkspaceButton is null || OpsWorkspaceButton is null) return;
        if (initialization is null) return;
        PresentLabelPauseControls(LabelPanel, LabelWorkspaceButton, labelCapture, gameControls.IsRecording);
        GameplayControlsPanel.Visibility = visionCancellation is not null
            && DashboardPresentationPolicy.ShowsGameplayFraming(
                workspace, initialization.IsFirstPhaseComplete, recognizedMenuContext)
            ? Visibility.Visible : Visibility.Collapsed;
        HackerCameraControlsPanel.Visibility = visionCancellation is not null
            && DashboardPresentationPolicy.ShowsHackerFraming(
                workspace, recognizedMenuContext)
            ? Visibility.Visible : Visibility.Collapsed;
        PathRecordingButton.Visibility = DashboardPresentationPolicy.ShowsPathRecording(
                workspace, gameMode, recognizedMenuContext)
            ? Visibility.Visible : Visibility.Collapsed;
        ModelWorkspaceButton.Visibility = initialization.IsFirstPhaseComplete ? Visibility.Visible : Visibility.Collapsed;
        StencilDetectionCheck.IsEnabled = initialization.IsFirstPhaseComplete;
        ObjectDetectionCheck.IsEnabled = initialization.IsFirstPhaseComplete && trainedGameplayModelReady;
        if (!ObjectDetectionCheck.IsEnabled) ObjectDetectionCheck.IsChecked = false;
        ObjectDetectionOptions.IsEnabled = ObjectDetectionCheck.IsEnabled && ObjectDetectionCheck.IsChecked == true;
        StencilLockedText.Visibility = StencilDetectionCheck.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        ModelLockedText.Visibility = ObjectDetectionCheck.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        HackerWorkspaceButton.IsEnabled = CanUseHacker();
        OpsWorkspaceButton.IsEnabled = CanUseOps() && ShowsOpsControl();
        OpsWorkspaceButton.Visibility = Visibility.Collapsed;
        OpsPanel.Visibility = CanUseOps() && recognizedMenuContext == "game" ? Visibility.Visible : Visibility.Collapsed;
        OpsSettingsTitle.Visibility = OpsPanel.Visibility;
        var groundToolsAvailable = DashboardPresentationPolicy.ShowsGroundTrackingTools(
            workspace, recognizedMenuContext);
        GroundFeaturesCheck.IsEnabled = groundToolsAvailable;
        TransitionsCheck.IsEnabled = groundToolsAvailable;
        CoarseMotionCheck.IsEnabled = groundToolsAvailable;
        HackerWorkspaceButton.Visibility = CanUseHacker() && recognizedMenuContext == "game" ? Visibility.Visible : Visibility.Collapsed;
        HackerWorkspaceButton.ToolTip = CanUseHacker()
            ? null
            : gameMode == GameRuntimeMode.Normal
                ? "Available in Modded mode"
                : "Waiting for receiver telemetry";
        OpsWorkspaceButton.ToolTip = CanUseOps()
            ? null
            : gameMode == GameRuntimeMode.Normal
                ? "Available in Modded mode"
                : "Waiting for player-ops capability";
    }

    private void SettingsBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsOverlay)) SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private bool trainedGameplayModelReady;

    private bool HasTrainedGameplayModel()
    {
        try
        {
            return modelLifecycle?.HasTrainedGameplayModel(examples.Where(item =>
                StencilInitializationPolicy.NormalizeContext(item.Manifest.ContextIdentifier) == "game"
                && item.Manifest.Annotations.Any(box => !box.IsHardNegative)).Select(item => item.Id).ToHashSet()) == true;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        RefreshSettingsText();
        ReleaseForwardedInput();
        await RefreshAtlasManagerAsync();
        if (CanUseOps()) RefreshOps();
        HackerAtlasManager.Visibility = workspace == "Hacker" ? Visibility.Visible : Visibility.Collapsed;
        SettingsOverlay.Visibility = SettingsOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private readonly Dictionary<(DependencyObject, DependencyProperty), string> originalCaptions = [];
    private void LocalizeControls(DependencyObject parent)
    {
        void Translate(DependencyObject item, DependencyProperty property)
        {
            if (!originalCaptions.TryGetValue((item, property), out var original)) {
                if (item.GetValue(property) is not string text || string.IsNullOrEmpty(text)) return;
                original = text;
                originalCaptions[(item, property)] = original;
            }
            item.SetValue(property, initialization.Text(original));
        }
        if (parent is ButtonBase button && button.Name is not ("StencilsWorkspaceButton" or "SettingsFactoryButton"))
            Translate(parent, ContentControl.ContentProperty);
        if (parent is FrameworkElement element && element.ToolTip is string) Translate(element, FrameworkElement.ToolTipProperty);
        if (parent is TextBlock textBlock && (string.IsNullOrEmpty(textBlock.Name) || textBlock.Name == "OpsSettingsTitle")) Translate(parent, TextBlock.TextProperty);
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) LocalizeControls(child);
    }

    private void RefreshSettingsText()
    {
        trainedGameplayModelReady = HasTrainedGameplayModel();
        UpdateModeAvailability();
        SettingsTitle.Text = initialization.Text("settings");
        SettingsButton.ToolTip = SettingsTitle.Text;
        DebugSettings.Text = initialization.Text("debugView");
        GameStateCheck.Content = initialization.Text("gameState");
        SettingsLanguageTitle.Text = initialization.Text("language");
        SettingsFactoryButton.Content = initialization.Text("factory");
        ModelLockedText.Text = initialization.Text("modelLocked");
        StencilLockedText.Text = initialization.Text("stencilLocked");
        LocalizeControls(this);
        RefreshContextChoices();
        RefreshClassChoices();
    }

    private void SettingsLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialization is null || SettingsLanguageCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not string language || language == initialization.LanguageIdentifier) return;
        initialization.SetLanguage(language);
        initializationTargetContext = null;
        if (workspace is "Label" or "Model") SelectWorkspace("Gameplay");
        menuStencilRecognizer.Refresh(initialization.LanguageIdentifier);
        RefreshInitializationStatus();
        RefreshSettingsText();
        if (workspace == "Stencil") RefreshStencilsWorkspace();
    }

    private void ResetStencils_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        ResetStencils();
    }

    private void FactoryReset_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        RequestFactoryReset();
    }

    private void RefreshGuidance()
    {
        if (StencilGuidanceText is null || initialization is null) return;
        StencilGuidanceText.Text = (workspace == "Label" && CurrentContextName == "Gameplay" ? CampaignGuidance() : null) ?? initialization.ModelGuidance(workspace,
            screenshotReview?.HasGameplayLabels == true, screenshotReview?.BreachCount ?? 0,
            trainedGameplayModelReady, trainingInProgress, CampaignGuidance()) ?? initialization.Guidance(
            workspace == "Label" ? StencilInitializationPolicy.NormalizeContext(CurrentContextName) : null,
            ClassIdentifierText.Text, initializationTargetContext) ?? "";
        StencilGuidanceText.Visibility = StencilGuidanceText.Text.Length == 0
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshInitializationStatus()
    {
        initialization.Refresh();
        initialization.HasUsableHUD = menuStencilRecognizer.HasGameplayReference;
        UpdateModeAvailability();
        StencilsWorkspaceButton.Content = initialization.ButtonTitle;
        RefreshGuidance();
    }

    private void ResetStencils()
    {
        if (MessageBox.Show(this,
                initialization.Text("resetMessage"),
                initialization.Text("resetTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        try
        {
            var backup = VisionResetController.ResetStencils(ExamplesRootText.Text);
            initializationTargetContext = null;
            stencilSelectedContext = null;
            if (workspace is "Label" or "Model") SelectWorkspace("Gameplay");
            currentExample = null;
            currentFrame = null;
            boxes.Clear();
            RefreshExamples();
            menuStencilRecognizer.Refresh(initialization.LanguageIdentifier);
            RefreshInitializationStatus();
            if (workspace == "Stencil") RefreshStencilsWorkspace();
            SetStatus(backup is null ? "Stencil setup was already empty."
                : $"Stencil setup reset. Backup: {backup}");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void RequestFactoryReset()
    {
        if (MessageBox.Show(this,
                initialization.Text("factoryMessage"),
                initialization.Text("factoryTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        liveWorldPipeline?.InvalidatePendingWork();
        factoryResetRequested = true;
        Close();
    }

    private sealed record ContextChoice(string Name, string Display);
    private string CurrentContextName => ContextCombo.SelectedValue as string ?? "Gameplay";

    private void CycleLabelContext(int offset)
    {
        var identifiers = ContextCombo.Items.OfType<ContextChoice>()
            .Select(choice => StencilInitializationPolicy.NormalizeContext(choice.Name)).ToArray();
        var current = StencilInitializationPolicy.NormalizeContext(CurrentContextName);
        var next = LabelingEditorPolicy.CycledContextIdentifier(current, offset, identifiers);
        ContextCombo.SelectedValue = ContextDisplayName(next);
    }

    private void CycleLabelObject(int offset)
    {
        var choices = ClassCombo.Items.OfType<ClassChoice>().ToArray();
        var next = LabelingEditorPolicy.CycledClassIdentifier(
            choices.Select(choice => choice.Identifier).ToArray(),
            ClassIdentifierText.Text,
            offset);
        ClassCombo.SelectedItem = choices.FirstOrDefault(choice => choice.Identifier == next);
    }

    private void RefreshContextChoices()
    {
        var current = CurrentContextName;
        labelSelectionUpdate.Run(() =>
        {
            ContextCombo.DisplayMemberPath = nameof(ContextChoice.Display);
            ContextCombo.SelectedValuePath = nameof(ContextChoice.Name);
            ContextCombo.ItemsSource = StencilInitializationPolicy.AllContexts
                .Where(item => gameMode == GameRuntimeMode.Modded || item.Identifier != "mods")
                .Select(item => new ContextChoice(item.Name, initialization.Text(item.Name))).ToArray();
            ContextCombo.SelectedValue = current;
        });
    }

    private void ContextCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ChangeLabelingContext(e);
    }

    private void RefreshClassChoices()
    {
        if (ClassCombo is null || initialization is null) return;
        if (refreshingClassChoices) return;
        refreshingClassChoices = true;
        try
        {
            var previous = ClassIdentifierText.Text;
            var choices = AvailableClassChoices(CurrentContextName);
            ClassCombo.ItemsSource = choices;
            var selected = choices.FirstOrDefault(item => item.Identifier == previous) ?? choices.FirstOrDefault();
            ClassCombo.SelectedItem = selected;
            ClassIdentifierText.Text = selected?.Identifier ?? "";
            ClassCombo.Visibility = choices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            LabelObjectsComplete.Visibility = choices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            NewObjectButton.Visibility = CurrentContextName == "Gameplay" ? Visibility.Visible : Visibility.Collapsed;
            var availableModes = LabelingEditorPolicy.AvailableModes(CurrentContextName);
            var negativeAvailable = availableModes.Contains(LabelingEditorMode.Negative);
            LabelNegativeButton.Visibility = negativeAvailable ? Visibility.Visible : Visibility.Collapsed;
            LabelModeSegments.Columns = availableModes.Count;
            if (!negativeAvailable) {
                if (LabelNegativeButton.IsChecked == true) LabelAddButton.IsChecked = true;
                LabelNegativeButton.IsChecked = false;
                HardNegativeCheck.IsChecked = false;
            }
        }
        finally { refreshingClassChoices = false; }
        RefreshGuidance();
        RememberLabelSelection();
        UpdateModelTrainingControls();
    }

    private ClassChoice[] AvailableClassChoices(string? context, bool? hidesCompleted = null)
    {
        var identifier = StencilInitializationPolicy.NormalizeContext(context ?? "Gameplay");
        return StencilLabelSelectionPolicy.AvailableLabels(identifier, boxes.Select(box => box.ToAnnotation()).ToArray(),
                modded: gameMode == GameRuntimeMode.Modded, hidesCompleted: hidesCompleted ?? LabelAddButton.IsChecked == true,
                includesDeferredLabels: initialization.IsFirstPhaseComplete,
                custom: identifier == "game" ? customObjectChoices.Select(item => new MenuStencilLabel(item.Identifier, item.Name)).ToArray() : [])
            .Select(choice => new ClassChoice(initialization.Text(choice.Name), choice.Identifier)).ToArray();
    }

    private void NewObject_Click(object sender, RoutedEventArgs e)
    {
        var context = CurrentContextName;
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
            ApplyLabelSelection(() => ClassIdentifierText.Text = added.Identifier);
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
            Title = initialization.Text("newObject", ("group", initialization.Text(group))),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize
        };
        var confirm = new Button { Content = initialization.Text("Add"), IsDefault = true, MinWidth = 80, Margin = new Thickness(4) };
        var cancel = new Button { Content = initialization.Text("Cancel"), IsCancel = true, MinWidth = 80, Margin = new Thickness(4) };
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(field.Text)) return;
            dialog.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = initialization.Text("Object name"), Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(field);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => field.Focus();
        return dialog.ShowDialog() == true ? field.Text.Trim() : null;
    }

    private void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClassIdentifierText is null || refreshingClassChoices || labelSelectionUpdate.IsUpdating) return;
        if (ClassCombo.SelectedItem is ClassChoice choice)
        {
            ClassIdentifierText.Text = choice.Identifier;
            if (LabelModifyButton.IsChecked == true)
            {
                // Modify selects an existing instance of the chosen class. It
                // never silently reclassifies an overlapping rectangle.
                labelDraft.Select(boxes, choice.Identifier);
                SyncLabelSelection();
                RedrawBoxes();
            }
        }
        RefreshGuidance();
        RememberLabelSelection();
        UpdateModelTrainingControls();
    }

    private void LabelMode_Click(object sender, RoutedEventArgs e)
    {
        var negativeAvailable = LabelingEditorPolicy.AvailableModes(CurrentContextName)
            .Contains(LabelingEditorMode.Negative);
        if (!negativeAvailable && LabelNegativeButton.IsChecked == true) LabelAddButton.IsChecked = true;
        HardNegativeCheck.IsChecked = negativeAvailable && LabelNegativeButton.IsChecked == true;
        RefreshClassChoices();
        RedrawBoxes();
        UpdateLabelCrosshair();
    }

    private ToggleButton LabelModeButton(LabelingEditorMode mode) => mode switch
    {
        LabelingEditorMode.Add => LabelAddButton,
        LabelingEditorMode.Modify => LabelModifyButton,
        LabelingEditorMode.Delete => LabelDeleteButton,
        LabelingEditorMode.Negative => LabelNegativeButton,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private void ModelTab_Click(object sender, RoutedEventArgs e)
    {
        if (ModelObjectsButton.IsChecked == true) RefreshObjectReferences();
        if (ModelSummaryButton.IsChecked == true) modelSummaryReviewPanel?.SetSelectedClass(ClassIdentifierText.Text);
        if (ModelScreenshotsButton.IsChecked == true) RefreshScreenshotReview();
        ModelSummaryPanel.Visibility = ModelSummaryButton.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        ModelObjectsPanel.Visibility = ModelObjectsButton.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        ModelScreenshotsPanel.Visibility = ModelScreenshotsButton.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MacTrain_Click(object sender, RoutedEventArgs e)
    {
        if (trainingInProgress) { RequestStopTraining(); return; }
        try
        {
            if (!AdmitModelTraining(requireSelectedClass: false)) return;
        }
        catch (Exception error) { ShowError(error); return; }
        var classes = LabelingObjectReferencePolicy.ModelClasses(customObjectChoices.Select(item => new MenuStencilLabel(item.Identifier, item.Name)).ToArray())
            .Select(item => item.Identifier).Order(StringComparer.Ordinal)
            .ToArray();
        if (classes.Length == 0) return;
        try
        {
            modelLifecycle.Refresh();
            BaseCheckpointText.Text = modelLifecycle.TrainingCheckpointPath() ?? "";
        }
        catch (Exception error) { ShowError(error); return; }
        TrainingOutputText.Text = IOPath.Combine(IOPath.GetDirectoryName(TrainingOutputText.Text)!, Guid.NewGuid().ToString("D"));
        IterationsText.Text = LabelingTrainingConfiguration.ForModel(LabelingSharedModelPolicy.ModelIdentifier).MaximumIterations.ToString(CultureInfo.InvariantCulture);
        ExportClassesText.Text = string.Join(Environment.NewLine, classes);
        LatestDatasetText.Clear();
        ExportDataset_Click(sender, e);
        if (Directory.Exists(LatestDatasetText.Text)) await RunTrainerAsync(validateOnly: false);
    }

    private bool CanTrainSelectedObject()
    {
        return CanTrainSelectedObject(
            ClassIdentifierText.Text,
            LabelImage.Source is not null,
            boxes,
            examples,
            currentExample?.Id);
    }

    internal static bool CanTrainSelectedObject(
        string classIdentifier,
        bool hasActiveLabelImage,
        IEnumerable<EditableBox> currentRectangles,
        IEnumerable<SavedLabelingExample> examples,
        Guid? editingExampleIdentifier)
    {
        if (string.IsNullOrWhiteSpace(classIdentifier)) return false;
        var identifier = LabelingClassIdentity.CanonicalIdentifier(classIdentifier);
        IEnumerable<EditableBox> current = hasActiveLabelImage ? currentRectangles : [];
        var saved = examples.Where(example =>
                StencilInitializationPolicy.NormalizeContext(example.Manifest.ContextIdentifier) == "game"
                && example.Manifest.Annotations.Any(annotation =>
                    !annotation.IsHardNegative
                    && LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier) == identifier))
            .Select(example => example.Id);
        return LabelingEditorPolicy.CanTrain(
            identifier,
            current,
            saved,
            hasActiveLabelImage ? editingExampleIdentifier : null);
    }

    private void GameStateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (VisionStatusOverlay is not null)
            VisionStatusOverlay.Visibility = GameStateCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ObjectDetectionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ObjectDetectionOptions is null) return;
        ObjectDetectionOptions.IsEnabled = ObjectDetectionCheck.IsEnabled && ObjectDetectionCheck.IsChecked == true;
        if (ObjectDetectionCheck.IsChecked == true)
        {
            try { initialization?.RecordObjectDetectionEnabled(); }
            catch (Exception error) { ShowError(error); }
        }
        RefreshGuidance();
    }

    private void DetectionPixelsCheck_Changed(object sender, RoutedEventArgs e) =>
        createsObjectPixelDiagnostic = DetectionPixelsCheck.IsChecked == true;

    private void AtlasManagerDone_Click(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private void GameplayFraming_Click(object sender, RoutedEventArgs e)
    {
        gameplayFraming = (sender as FrameworkElement)?.Tag?.ToString() ?? gameplayFraming;
        if (gameplayFraming == "Free Fly") SendPointerExit();
        ApplyViewport(GameplayAtlasCanvas, GameplayPanel, gameplayFraming, GameplayImage,
            explicitRequest: true);
    }

    private void GameplayPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (hasFreshGameFrame)
            ApplyViewport(GameplayAtlasCanvas, GameplayPanel, gameplayFraming, GameplayImage);
    }

    private void HackerMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = (sender as FrameworkElement)?.Tag?.ToString();
        if (hackerInteractionMode == "Ground" && mode != "Ground")
            CancelGroundTruthGesture();
        hackerInteractionMode = mode ?? hackerInteractionMode;
        HackerGroundToolbar.Visibility = mode == "Ground"
            ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "Ground")
        {
            ReleaseForwardedInput();
            SelectGroundTruthMode();
            _ = PresentLatestGroundTruthContext();
        }
        else if (mode is "Rooms" or "Free Fly") SendPointerExit();
        RedrawHackerEdits();
        ApplyViewport(AtlasCanvas, HackerViewportPanel,
            DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode), HackerLiveImage,
            explicitRequest: true);
    }

    private async void PathRecording_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (gameControls.IsReplaying) return;
            if (!gameControls.IsRecording)
            {
                if (!await gameControls.StartRecordingAsync())
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
            var output = new RecordedInputPathStore(directory).Save(path);
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
        if (atlasOperationInProgress || isClosing || gameLostCountdownStarted || visionRestartPending) return;
        var runDirectory = VisionRunText.Text.Trim();
        if (runDirectory.Length > 0 && !Directory.Exists(runDirectory))
        {
            ShowError(new DirectoryNotFoundException(
                "Choose a trained run containing Detector.onnx and training.json."));
            return;
        }
        visionRestartPending = true;
        try
        {
            StopVision();
            // Never overlap capture runs or dispose an ONNX session while its
            // old inference callback is still executing.
            if (visionRunCompletion is { } previousRun) await previousRun;
            if (isClosing || gameLostCountdownStarted) return;
        }
        finally { visionRestartPending = false; }
        var cancellation = new CancellationTokenSource();
        visionCancellation = cancellation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        visionRunCompletion = completion.Task;
        menuStencilRecognizer.ResetTracking();
        labelCapture.Reset(gameMode == GameRuntimeMode.Normal || Volatile.Read(ref stepCapture) is not null
            ? GamePauseState.Ready : gameControls.PauseState);
        menuStencilRecognizer.SetObjectInferenceSuspended(labelCapture.SuspendsInference);
        StartVisionButton.IsEnabled = false;
        StopVisionButton.IsEnabled = true;
        try
        {
            using var detector = new OnnxObjectDetectorSet(
                activeModelRuns.Length > 0 && string.Equals(runDirectory, activeModelRuns[0], StringComparison.OrdinalIgnoreCase)
                    ? activeModelRuns : runDirectory.Length > 0 ? [runDirectory] : [], activeModelArtifacts);
            // Compile CUDA once before capture; device absence keeps the CPU fallback.
            await Task.Run(() => GroundPatchMatcher.Shared.WarmUp(), cancellation.Token);
            var preservesStepEvidence = Volatile.Read(ref stepCapture) is not null;
            // Controlled replay freezes simulation until a matching frame is
            // scored, so latest presentation safely discards paused backlog.
            using var capture = new WindowsGameCapture();
            var analysisFrames = Channel.CreateBounded<PendingVisionFrame>(new BoundedChannelOptions(preservesStepEvidence ? 16 : 1)
            { FullMode = preservesStepEvidence ? BoundedChannelFullMode.Wait : BoundedChannelFullMode.DropOldest,
                SingleReader = true, SingleWriter = false });
            var hackerFrames = Channel.CreateUnbounded<HackerPipelineWork>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
            var captureGeneration = Interlocked.Increment(ref recentCaptureGeneration);
            gameplayPresentation.Reset();
            previousAnalysisStartedTimestamp = 0;
            maxAnalysisGapMilliseconds = 0;
            BeginReceiverFrameJoin(
                captureGeneration,
                analysisFrames.Writer,
                hackerFrames.Writer);
            var analysisTask = Task.Run(() => RunVisionAnalysisAsync(
                analysisFrames.Reader,
                detector,
                captureGeneration,
                cancellation.Token));
            var hackerTask = Task.Run(() => RunHackerPipelineAsync(
                hackerFrames.Reader,
                captureGeneration));
            var frameId = 0L;
            var validatedProcessId = 0;
            try
            {
                if (Volatile.Read(ref stepCapture) is not null)
                    await RunStepCaptureAsync(capture, captureGeneration, analysisTask, hackerTask, cancellation.Token);
                else while (!cancellation.IsCancellationRequested)
                {
                    if (analysisTask.IsCompleted) await analysisTask;
                    if (hackerTask.IsCompleted) await hackerTask;
                    if (labelCapture.IsFrozen)
                    {
                        if (MonitoredGameHasExited()) throw new GameWindowLostException();
                        await Task.Delay(33, cancellation.Token);
                        continue;
                    }
                    var labelRevision = labelCapture.Revision;
                    var iteration = Stopwatch.StartNew();
                    var frame = await Task.Run(async () =>
                    {
                        var window = HollowKnightWindowLocator.FindBest()
                            ?? throw new GameWindowLostException();
                        if (window.ProcessId != validatedProcessId)
                        {
                            ValidateGameWindowMode(window);
                            validatedProcessId = window.ProcessId;
                        }
                        return await capture.CaptureAsync(window, frameId + 1, cancellation.Token).ConfigureAwait(false);
                    }, cancellation.Token);
                    if (frame is null) continue;
                    frameId++;
                    captureBackend = capture.Backend;
                    captureFallbackReason = capture.FallbackReason;
                    captureReadbackMilliseconds = capture.ReadbackMilliseconds;
                    var captureTimestamp = capture.CaptureTimestamp;
                    if (!labelCapture.Accept(frame, labelRevision)) continue;
                    gameplayPresentation.Capture(frame);
                    var bitmap = FrameBitmap(frame);
                    var firstFreshGameFrame = !hasFreshGameFrame;
                    hasFreshGameFrame = true;
                    Interlocked.Exchange(ref latestPresentedCaptureId, frame.CaptureId);
                    GameplayImage.Source = bitmap;
                    if (firstFreshGameFrame)
                    {
                        PositionUnregisteredGameplayFrame(frame);
                        ApplyViewport(
                            GameplayAtlasCanvas,
                            GameplayPanel,
                            "Current",
                            GameplayImage);
                    }
                    if (labelCapture.State == GamePauseState.Ready)
                        recentCapturedFrames.Append(new(captureGeneration, frame.CaptureId, frame,
                            menuStencilRecognizer.SnapshotObjectsAt(frame).Tracked));
                    var pending = new PendingVisionFrame(
                        frame,
                        labelRevision,
                        captureTimestamp);
                    if (gameMode == GameRuntimeMode.Normal)
                    {
                        analysisFrames.Writer.TryWrite(pending);
                    }
                    else
                    {
                        var renderedGameFrame = RenderedFrameMarker.Decode(frame);
                        CaptureReplayAudit(frame, renderedGameFrame, captureTimestamp);
                        BufferReceiverCapture(
                            captureGeneration,
                            pending,
                            renderedGameFrame);
                    }
                    GameplayPlaceholder.Visibility = Visibility.Collapsed;
                    VisionStatusText.Text = Volatile.Read(ref recognizedMenuContext) is { } state
                        ? initialization.Text(state == "game" ? "Gameplay" : ContextDisplayName(state))
                        : initialization.Text("Finding State");
                    if (Volatile.Read(ref recognizedMenuSelection) is { } selection)
                        VisionStatusText.Text += $" - {initialization.Text(selection)}";
                    UpdateModeAvailability();

                    // The GPU stream supplies its own cadence. Only pace the legacy fallback.
                    var remaining = TimeSpan.FromMilliseconds(capture.Backend == "GDI" ? 33 : 0) - iteration.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, cancellation.Token);
                    }
                }
            }
            finally
            {
                EndReceiverFrameJoin(captureGeneration);
                analysisFrames.Writer.TryComplete();
                hackerFrames.Writer.TryComplete();
                cancellation.Cancel();
                try
                {
                    await analysisTask;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                await hackerTask;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            GameplayPlaceholder.Visibility = Visibility.Visible;
            VisionStatusText.Text = initialization.Text("Finding State");
            UpdateModeAvailability();
        }
        catch (Exception error)
        {
            if (error is GameWindowLostException || MonitoredGameHasExited())
                BeginGameLostCountdown();
            else
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
            completion.TrySetResult();
        }
    }

    private async Task RunVisionAnalysisAsync(
        ChannelReader<PendingVisionFrame> frames,
        OnnxObjectDetectorSet? detector,
        long captureGeneration,
        CancellationToken cancellationToken)
    {
        var frameAdmission = new GameplayFrameAdmission();
        var groundTruthSeedEvidence = new GroundTruthSeedEvidence();
        var groundHypothesisState = new GroundHypothesisWorkerState();
        var normalAtlasRoomIdentity = new NormalAtlasRoomIdentity();
        var appliedGroundResetGeneration = Interlocked.Read(
            ref groundHypothesisResetGeneration);
        long? lastLabelRevision = null;
        try
        {
            await foreach (var pending in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!labelCapture.CanProcess(pending.LabelRevision)) continue;
                var requestedGroundResetGeneration = Interlocked.Read(
                    ref groundHypothesisResetGeneration);
                if (requestedGroundResetGeneration != appliedGroundResetGeneration)
                {
                    groundHypothesisState.Reset();
                    appliedGroundResetGeneration = requestedGroundResetGeneration;
                }
                if (lastLabelRevision != pending.LabelRevision)
                {
                    frameMotionTracker.Reset();
                    inputPathMotionTracker.Reset();
                    groundTruthSeedEvidence.Reset();
                    groundHypothesisState.Reset();
                    frameAdmission = new();
                    lastLabelRevision = pending.LabelRevision;
                }
                var analysisStarted = Stopwatch.GetTimestamp();
                Volatile.Read(ref stepCapture)?.Trace("analysis", pending.Frame.CaptureId, pending.RenderedGameFrame, pending.CaptureTimestamp);
                var queueMilliseconds = Math.Max(0,
                    (analysisStarted / (double)Stopwatch.Frequency - pending.CaptureTimestamp) * 1000);
                var result = AnalyzeVisionFrame(
                    pending.Frame,
                    detector,
                    frameAdmission,
                    captureGeneration,
                    pending.LabelRevision,
                    pending.CaptureTimestamp,
                    pending.RenderedGameFrame,
                    pending.ReceiverTelemetry,
                    pending.ReceiverObservedAt,
                    pending.OwnsHackerWork,
                    pending.IntegratesHackerAtlas,
                    pending.HackerWorkGeneration,
                    groundTruthSeedEvidence,
                    groundHypothesisState,
                    normalAtlasRoomIdentity);
                if (result is null) continue;
                result = result with
                {
                    AnalysisStartedTimestamp = analysisStarted,
                    AnalysisMilliseconds = Stopwatch.GetElapsedTime(analysisStarted).TotalMilliseconds,
                    QueueMilliseconds = queueMilliseconds
                };
                PublishAuthoritativeVisionState(result);
                await Dispatcher.InvokeAsync(
                    () => ApplyVisionAnalysis(result),
                    DispatcherPriority.Render,
                    cancellationToken);
                // World integration and snapshot swaps share worldWorker; camera analysis never waits on disk.
            }
        }
        finally
        {
            groundHypothesisState.Reset();
            menuStencilRecognizer.ResetObjectInference();
            await menuStencilRecognizer.DrainObjectInferenceAsync().ConfigureAwait(false);
        }
    }

    private async Task RunHackerPipelineAsync(
        ChannelReader<HackerPipelineWork> frames,
        long captureGeneration)
    {
        HackerAtlasPublication? stagedAtlas;
        lock (hackerAtlasGate) stagedAtlas = latestStagedHackerAtlas;
        await foreach (var work in frames.ReadAllAsync().ConfigureAwait(false))
        {
            if (!work.OwnsHackerWork
                || work.CaptureGeneration != captureGeneration
                || work.WorkGeneration != Interlocked.Read(ref hackerWorkGeneration))
            {
                continue;
            }

            HackerPreparedFrame? prepared;
            HackerAtlasOutput? output;
            lock (hackerAtlasGate)
            {
                if (work.WorkGeneration != hackerWorkGeneration) continue;
                prepared = hackerFramePreparer.Process(work.Match);
                hackerAtlasPipeline.ConfigureHudTemplate(menuStencilRecognizer.CapturedHudTemplate);
                output = prepared is null ? null : hackerAtlasPipeline.Process(prepared);
            }
            if (prepared is null) continue;

            if (output is not null)
            {
                stagedAtlas = StageHackerAtlas(output);
                lock (hackerAtlasGate)
                {
                    if (work.WorkGeneration != hackerWorkGeneration) continue;
                    latestStagedHackerAtlas = stagedAtlas;
                }
            }

            var publication = new HackerPipelinePublication(
                prepared,
                FrameBitmap(prepared.LiveImage),
                stagedAtlas,
                work.CaptureGeneration,
                work.WorkGeneration);
            QueueHackerPublication(publication);
        }
    }

    private static HackerAtlasPublication StageHackerAtlas(HackerAtlasOutput output)
    {
        var tiles = output.Rooms.SelectMany(room => room.AtlasTiles.Select(layer =>
        {
            var occupied = OccupiedBounds(
                layer.Image,
                new AtlasTileCoordinate(0, 0),
                HackerAtlasTileSize);
            ViewportRect? content = occupied is { } bounds
                ? new ViewportRect(
                    layer.Bounds.MinX + bounds.X,
                    -layer.Bounds.MaxY + bounds.Y,
                    bounds.Width,
                    bounds.Height)
                : null;
            return new HackerTilePublication(
                new HackerTileKey(room.Id, layer.Id),
                room.SceneName,
                FrameBitmap(layer.Image),
                layer.Bounds,
                content,
                layer.Revision);
        })).ToArray();
        return new HackerAtlasPublication(
            output,
            tiles,
            tiles.Select(tile => tile.Revision).DefaultIfEmpty(0UL).Max());
    }

    private void QueueHackerPublication(HackerPipelinePublication publication)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Interlocked.Exchange(ref pendingHackerPublication, publication);
        if (Interlocked.CompareExchange(ref hackerPublicationScheduled, 1, 0) != 0) return;
        _ = Dispatcher.BeginInvoke(DrainHackerPublications, DispatcherPriority.Render);
    }

    private void DrainHackerPublications()
    {
        while (true)
        {
            var pending = Interlocked.Exchange(ref pendingHackerPublication, null);
            try
            {
                if (pending is not null) ApplyHackerPipelinePublication(pending);
            }
            catch (Exception error)
            {
                var retry = pending is not null
                    && pending.ApplyAttempt < HackerPublicationRetryLimit
                    && pending.CaptureGeneration == Interlocked.Read(ref recentCaptureGeneration)
                    && pending.WorkGeneration == Interlocked.Read(ref hackerWorkGeneration)
                    && workspace == "Hacker"
                    && !Dispatcher.HasShutdownStarted
                    && !Dispatcher.HasShutdownFinished
                        ? pending with { ApplyAttempt = pending.ApplyAttempt + 1 }
                        : null;
                if (retry is not null)
                {
                    Interlocked.CompareExchange(
                        ref pendingHackerPublication,
                        retry,
                        comparand: null);
                }
                if (Volatile.Read(ref pendingHackerPublication) is null)
                {
                    ShowError(error);
                }
            }
            finally
            {
                Interlocked.Exchange(ref hackerPublicationScheduled, 0);
            }
            if (Volatile.Read(ref pendingHackerPublication) is null
                || Interlocked.CompareExchange(ref hackerPublicationScheduled, 1, 0) != 0)
            {
                break;
            }
        }
    }

    private void BeginReceiverFrameJoin(
        long captureGeneration,
        ChannelWriter<PendingVisionFrame> analysisFrames,
        ChannelWriter<HackerPipelineWork> hackerFrames)
    {
        lock (receiverFrameJoinGate)
        {
            receiverFrameSynchronizer.Reset();
            receiverFrameLabelRevisions.Clear();
            receiverFrameHackerOwnership.Clear();
            receiverFrameHackerGenerations.Clear();
            receiverFrameCaptureIds.Clear();
            receiverFrameLabelRevisionOrder.Clear();
            receiverPendingHackerMatches.Clear();
            receiverFrameJoinGeneration = captureGeneration;
            receiverFrameAnalysisWriter = analysisFrames;
            receiverFrameHackerWriter = hackerFrames;
            latestMatchedCaptureId = -1;
            latestPresentedMatchedCaptureId = -1;
            latestGroundTruthContext = null;
        }
    }

    private void EndReceiverFrameJoin(long captureGeneration)
    {
        lock (receiverFrameJoinGate)
        {
            if (receiverFrameJoinGeneration != captureGeneration) return;
            FlushReceiverHackerMatches(flushAll: true);
            receiverFrameAnalysisWriter = null;
            receiverFrameHackerWriter = null;
            receiverFrameJoinGeneration = -1;
            receiverFrameSynchronizer.Reset();
            receiverFrameLabelRevisions.Clear();
            receiverFrameHackerOwnership.Clear();
            receiverFrameHackerGenerations.Clear();
            receiverFrameCaptureIds.Clear();
            receiverFrameLabelRevisionOrder.Clear();
            receiverPendingHackerMatches.Clear();
            latestMatchedCaptureId = -1;
            latestPresentedMatchedCaptureId = -1;
            latestGroundTruthContext = null;
        }
    }

    private void InvalidatePendingHackerWork(bool resetCore = false)
    {
        lock (hackerAtlasGate)
        {
            Interlocked.Increment(ref hackerWorkGeneration);
            if (resetCore)
            {
                hackerFramePreparer.Reset();
                hackerAtlasPipeline.Reset();
                latestStagedHackerAtlas = null;
                hackerAtlasOutput = null;
                hackerLivePrepared = null;
                latestHackerPublication = null;
                hackerPublishedRevision = 0;
            }
        }
        Interlocked.Exchange(ref pendingHackerPublication, null);
        lock (receiverFrameJoinGate)
        {
            foreach (var captureId in receiverPendingHackerMatches
                         .Where(pair => pair.Value.WorkGeneration != hackerWorkGeneration)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                receiverPendingHackerMatches.Remove(captureId);
            }
            FlushReceiverHackerMatches(flushAll: false);
        }
    }

    private void BufferReceiverCapture(
        long captureGeneration,
        PendingVisionFrame capture,
        int? renderedGameFrame)
    {
        lock (receiverFrameJoinGate)
        {
            if (captureGeneration != receiverFrameJoinGeneration
                || receiverFrameAnalysisWriter is not { } writer
                || renderedGameFrame is not { } renderedFrame
                || !double.IsFinite(capture.CaptureTimestamp)) return;
            var key = HackerFrameSynchronizer.Key(renderedFrame);
            var ownsHackerWork = workspace == "Hacker";
            var hackerGeneration = Interlocked.Read(ref hackerWorkGeneration);
            var matched = receiverFrameSynchronizer.Append(new HackerCapturedFrame(
                key,
                capture.Frame,
                capture.CaptureTimestamp,
                IntegratesAtlas: ownsHackerWork
                    && capture.Frame.CaptureId % HackerAtlasSampleStride == 0));
            if (matched is null)
            {
                RememberReceiverFrameMetadata(
                    key,
                    capture.LabelRevision,
                    ownsHackerWork,
                    hackerGeneration,
                    capture.Frame.CaptureId);
                return;
            }
            ForgetReceiverFrameLabelRevision(key);
            SubmitReceiverFrameMatch(
                writer,
                matched,
                capture.LabelRevision,
                ownsHackerWork,
                hackerGeneration);
        }
    }

    private void OnReceiverGroundTruth(
        ReceiverGroundTruthSample sample,
        double observedAt)
    {
        Volatile.Read(ref stepCapture)?.Trace("truth", -1, sample.UnityFrame, observedAt);
        if (Volatile.Read(ref replayVision) is { } replayFrame)
            ObserveReplayDecision(inputPathDivergenceMonitor.AppendGroundTruth(sample, observedAt, new PlaybackFrameSize(replayFrame.Width, replayFrame.Height)));
        lock (receiverFrameJoinGate)
        {
            if (gameMode != GameRuntimeMode.Modded
                || receiverFrameAnalysisWriter is not { } writer
                || receiverFrameJoinGeneration < 0
                || !double.IsFinite(observedAt)) return;
            var matched = receiverFrameSynchronizer.Append(sample, observedAt);
            if (matched is null) return;
            var key = HackerFrameSynchronizer.Key(sample.UnityFrame);
            if (!receiverFrameLabelRevisions.TryGetValue(key, out var labelRevision)
                || !receiverFrameHackerOwnership.TryGetValue(key, out var ownsHackerWork)
                || !receiverFrameHackerGenerations.TryGetValue(key, out var hackerGeneration)) return;
            ForgetReceiverFrameLabelRevision(key);
            SubmitReceiverFrameMatch(
                writer,
                matched,
                labelRevision,
                ownsHackerWork,
                hackerGeneration);
        }
    }

    private void RememberReceiverFrameMetadata(
        int key,
        long labelRevision,
        bool ownsHackerWork,
        long hackerGeneration,
        long captureId)
    {
        if (!receiverFrameLabelRevisions.ContainsKey(key))
            receiverFrameLabelRevisionOrder.Add(key);
        receiverFrameLabelRevisions[key] = labelRevision;
        receiverFrameHackerOwnership[key] = ownsHackerWork;
        receiverFrameHackerGenerations[key] = hackerGeneration;
        receiverFrameCaptureIds[key] = captureId;
        while (receiverFrameLabelRevisionOrder.Count > ReceiverFrameJoinCapacity)
        {
            receiverFrameLabelRevisions.Remove(receiverFrameLabelRevisionOrder[0]);
            receiverFrameHackerOwnership.Remove(receiverFrameLabelRevisionOrder[0]);
            receiverFrameHackerGenerations.Remove(receiverFrameLabelRevisionOrder[0]);
            receiverFrameCaptureIds.Remove(receiverFrameLabelRevisionOrder[0]);
            receiverFrameLabelRevisionOrder.RemoveAt(0);
        }
        FlushReceiverHackerMatches(flushAll: false);
    }

    private void ForgetReceiverFrameLabelRevision(int key)
    {
        receiverFrameLabelRevisions.Remove(key);
        receiverFrameHackerOwnership.Remove(key);
        receiverFrameHackerGenerations.Remove(key);
        receiverFrameCaptureIds.Remove(key);
        receiverFrameLabelRevisionOrder.RemoveAll(value => value == key);
    }

    private void SubmitReceiverFrameMatch(
        ChannelWriter<PendingVisionFrame> writer,
        HackerMatchedFrame matched,
        long labelRevision,
        bool ownsHackerWork,
        long hackerGeneration)
    {
        // Every exact pair owned by Hacker reaches the ordered compositor lane.
        // Latest-only suppression below applies only to analysis/presentation.
        QueueReceiverHackerMatch(new HackerPipelineWork(
            matched,
            receiverFrameJoinGeneration,
            hackerGeneration,
            ownsHackerWork));
        if (matched.Capture.Image.CaptureId <= latestMatchedCaptureId) return;
        var pending = new PendingVisionFrame(
            matched.Capture.Image,
            labelRevision,
            matched.Capture.CapturedAt,
            matched.Capture.UnityFrame,
            matched.Sample,
            matched.SampleObservedAt,
            ownsHackerWork,
            matched.Capture.IntegratesAtlas,
            hackerGeneration);
        Volatile.Read(ref stepCapture)?.Trace("joined", pending.Frame.CaptureId, pending.RenderedGameFrame, pending.CaptureTimestamp);
        if (!writer.TryWrite(pending)) return;
        latestMatchedCaptureId = pending.Frame.CaptureId;
    }

    private void QueueReceiverHackerMatch(HackerPipelineWork work)
    {
        if (!work.OwnsHackerWork
            || work.WorkGeneration != Interlocked.Read(ref hackerWorkGeneration))
        {
            return;
        }
        receiverPendingHackerMatches[work.Match.Capture.Image.CaptureId] = work;
        FlushReceiverHackerMatches(flushAll: false);
    }

    private void FlushReceiverHackerMatches(bool flushAll)
    {
        while (receiverPendingHackerMatches.Count > 0
               && receiverFrameHackerWriter is { } writer)
        {
            var first = receiverPendingHackerMatches.First();
            var blockedByEarlierOwnedCapture = !flushAll
                && first.Value.OwnsHackerWork
                && receiverFrameCaptureIds.Any(pair =>
                    pair.Value < first.Key
                    && receiverFrameHackerOwnership.TryGetValue(pair.Key, out var owns)
                    && owns
                    && receiverFrameHackerGenerations.TryGetValue(pair.Key, out var generation)
                    && generation == first.Value.WorkGeneration);
            if (blockedByEarlierOwnedCapture) return;
            if (!writer.TryWrite(first.Value)) return;
            receiverPendingHackerMatches.Remove(first.Key);
        }
    }

    private bool TryAcceptLatestMatchedResult(VisionAnalysisResult result)
    {
        if (result.ReceiverTelemetry is null) return false;
        lock (receiverFrameJoinGate)
        {
            if (receiverFrameJoinGeneration != result.CaptureGeneration
                || result.Frame.CaptureId != latestMatchedCaptureId
                || result.Frame.CaptureId <= latestPresentedMatchedCaptureId)
            {
                return false;
            }
            latestPresentedMatchedCaptureId = result.Frame.CaptureId;
            return true;
        }
    }

    private VisionAnalysisResult? AnalyzeVisionFrame(
        BgraFrame frame,
        OnnxObjectDetectorSet? detector,
        GameplayFrameAdmission frameAdmission,
        long captureGeneration,
        long labelRevision,
        double captureTimestamp,
        int? exactRenderedGameFrame,
        ReceiverGroundTruthSample? exactReceiverTelemetry,
        double? exactReceiverObservedAt,
        bool ownsHackerWork,
        bool integratesHackerAtlas,
        long pendingHackerWorkGeneration,
        GroundTruthSeedEvidence groundTruthSeedEvidence,
        GroundHypothesisWorkerState groundHypothesisState,
        NormalAtlasRoomIdentity normalAtlasRoomIdentity)
    {
        menuStencilRecognizer.ConfigureObjectDiagnostics(Volatile.Read(ref objectDetectionExtension), createsObjectPixelDiagnostic);
        var recognition = menuStencilRecognizer.AnalyzeLiveWithPublication(frame,
            asynchronously: workspace == "Hacker" || Volatile.Read(ref stepCapture) is not null,
            detector is { HasModels: true } ? () => detector.Detect(frame) : null,
            publication => recentCapturedFrames.ReplaceDetections(publication.Detections, captureGeneration, publication.Frame.CaptureId));
        if (!labelCapture.CanProcess(labelRevision)) return null;
        var signal = FrameRegionRenderer.SignalProfile(frame);
        var coarseMotion = inputPathMotionTracker.Observe(
            signal.MotionGrid,
            captureTimestamp);
        // Match Mac: once Gameplay is established, ordinary HUD animation and
        // temporary HUD loss must not stop camera tracking. Positive menu
        // recognition releases the latch; the signal gate still blocks fades.
        var admission = frameAdmission.Observe(
            signal,
            recognition.GameplayEnabled,
            recognition.Generation);
        if (admission.ResetMotionReference)
        {
            frameMotionTracker.Reset();
            groundHypothesisState.SuspendTracking();
        }
        var hudStencil = recognition.Hud;
        var detections = recognition.Detections;
        var masks = GameplaySceneMasks.Create(frame.Width, frame.Height, detections, hudStencil, recognition.Objects.Masks);
        if (Volatile.Read(ref stepCapture) is not null)
        {
            if (exactReceiverTelemetry is { } stepTruth && exactRenderedGameFrame is { } stepFrame)
                ObserveTrackingStep(frame, masks, admission.AdmitsTracking, captureTimestamp, stepTruth, stepFrame);
            return null;
        }
        var atlasExclusions = masks.AtlasExclusions;
        var groundExclusions = masks.GroundExclusions;
        IReadOnlyList<DetectedFloorLine> ground = [];
        var groundHypotheses = GroundHypothesisTrackingResult.Empty;
        IReadOnlyList<DetectedFloorLine> hypothesisFloorLines = [];
        var hypothesisExclusions = groundExclusions.ToList();
        if (masks.GroundKnight is { } groundKnight) hypothesisExclusions.Add(groundKnight);
        GroundEdgeAnalysis? groundAnalysis = null;
        GroundTheoryAnalysis? groundTheory = null;
        groundHypothesisState.PrepareFrame(
            frame.Width,
            frame.Height,
            exactReceiverTelemetry?.SceneName);
        if (groundHypothesisState.NeedsCameraAnchor)
        {
            groundHypothesisState.Tracker.ReanchorLocalCamera(visualGameplayTracker.Position);
            groundHypothesisState.NeedsCameraAnchor = false;
        }
        if (admission.AdmitsTracking)
        {
            groundAnalysis = GroundLineDetector.Analyze(frame, groundExclusions);
            var groundComparison = GroundLineDetector.Compare(groundAnalysis);
            groundTheory = GroundLineDetector.Theory(
                groundComparison,
                GroundTheoryTuning.Default);
            ground = GroundLineDetector.SemanticFloorLines(
                groundComparison,
                GroundTheoryTuning.SemanticDefault);
            ground = GroundLineDetector.RejectKnownForegroundLines(
                ground, frame.Height, groundExclusions, masks.GroundKnight);

            hypothesisFloorLines = GroundLineDetector.RejectKnownForegroundLines(
                GroundLineDetector.CleanFloorLines(groundTheory),
                frame.Height, groundExclusions, masks.GroundKnight);
        }
        else groundHypothesisState.SuspendTracking();

        LowResolutionTranslationDiagnostic? motion = null;
        if (admission.AdmitsTracking)
        {
            motion = frameMotionTracker.Observe(frame, masks.GroundRegions.OmittedRects);
        }
        else frameMotionTracker.Reset();
        latestMotion = motion;
        var groundPresenceReviews = exactReceiverTelemetry is { } seedTelemetry
            ? groundTruthSeedEvidence.Observe(
                frame,
                ground,
                groundExclusions,
                seedTelemetry,
                captureTimestamp,
                admission.AdmitsTracking)
            : [];
        if (!labelCapture.CanProcess(labelRevision)) return null;

        AtlasMosaicDelta? gameplayTiles = null;
        var groundStarted = Stopwatch.GetTimestamp();
        var visualObservation = admission.AdmitsTracking
            && groundAnalysis?.SourceLuma is { } sourceLuma
            ? visualGameplayTracker.ObserveGroundAnchored(
                frame, motion, sourceLuma, hypothesisFloorLines, hypothesisExclusions,
                gameplayAtlas.TileCount > 0, masks.GroundKnight,
                groundHypothesisState.Tracker, captureTimestamp)
            : visualGameplayTracker.ObserveUnverified(frame, motion);
        if (!admission.AdmitsTracking) visualGameplayTracker.PauseGroundVerification();
        var groundMilliseconds = Stopwatch.GetElapsedTime(groundStarted).TotalMilliseconds;
        var atlasStarted = Stopwatch.GetTimestamp();
        var visualTelemetry = visualObservation.Telemetry;
        groundHypotheses = visualObservation.GroundTracking;
        var semanticAtlasLines = groundHypothesisState.SemanticAtlas.Update(
            groundHypotheses.AtlasLines,
            ground.Select(line => line.ToSemanticLine()).ToArray(),
            groundHypotheses.CameraPosition, frame.Width, frame.Height,
            groundHypotheses.PoseVerified);
        groundHypotheses = groundHypotheses with { AtlasLines = semanticAtlasLines };
        var groundReference = groundHypothesisState.ReferenceTracker.CurrentEstimate();
        if (visualObservation.Registration?.State == CameraUpdateState.Accepted
            && groundTheory is not null)
        {
            groundReference = groundHypothesisState.ReferenceTracker.Observe(
                frame,
                new AtlasPoint(visualTelemetry.CameraX, visualTelemetry.CameraY),
                frame.Width,
                masks.GroundKnight,
                groundExclusions,
                groundTheory);
        }
        var renderedGameFrame = exactRenderedGameFrame
            ?? (gameMode == GameRuntimeMode.Normal
                ? RenderedFrameMarker.Decode(frame)
                : null);
        var receiverTelemetry = gameMode == GameRuntimeMode.Modded
            ? exactReceiverTelemetry
            : null;
        var motionBridgeActive = visualObservation.Source is
            "transition-bridge" or "coarse-presentation";
        var receiverScene = receiverTelemetry?.SceneName?.Trim();
        var atlasRoomId = !string.IsNullOrWhiteSpace(receiverScene)
            ? receiverScene
            : normalAtlasRoomIdentity.Observe(
                captureGeneration,
                admission.Transition.ResumedAfterTransition);
        var atlasCaptureIdentity = FormattableString.Invariant(
            $"{captureGeneration}:{frame.CaptureId}:{BitConverter.DoubleToInt64Bits(captureTimestamp):X16}");
        var atlasCompositionExclusions = LiveAtlasTransitionMaskPolicy.Merge(
            atlasExclusions,
            frame.Width,
            frame.Height,
            signal,
            motionBridgeActive);
        var gameplayAdmitted = admission.AllowsAtlasWrite
            && visualObservation.AllowsAtlasWrite;
        WindowsLiveWorldPipelineResult? liveWorldResult = null;
        var liveWorldGeneration = captureGeneration > 0
            ? (ulong)captureGeneration
            : 0;
        if (gameMode == GameRuntimeMode.Modded
            && liveWorldPipeline is not null
            && !visualObservation.AllowsAtlasWrite)
        {
            Interlocked.Exchange(ref pendingWorldTrackingLoss, (long)liveWorldGeneration);
        }
        if (gameplayAdmitted && gameMode == GameRuntimeMode.Modded)
        {
            if (receiverTelemetry is not { SceneName: { Length: > 0 } roomName }
                || liveWorldPipeline is null)
            {
                gameplayAdmitted = false;
            }
            else
            {
                var localPose = new AtlasPoint(
                    visualTelemetry.CameraX,
                    visualTelemetry.CameraY);
                var compositionExclusions = atlasCompositionExclusions.Select(rectangle => new StencilRect(
                    rectangle.X,
                    rectangle.Y,
                    rectangle.Width,
                    rectangle.Height)).ToArray();
                var groundAlignmentExclusions = CreateGroundAlignmentExclusions(
                    frame,
                    localPose,
                    frame.Width,
                    ground,
                    groundExclusions,
                    signal,
                    visualObservation,
                    motionBridgeActive,
                    groundReference);
                QueueWorldFrame(new WindowsLiveWorldFrame(
                    Frame: frame,
                    MaskedFrame: FrameRegionRenderer.MapFrame(frame, masks.AtlasRegions),
                    Timestamp: captureTimestamp,
                    MonotonicTimestamp: captureTimestamp,
                    LocalPose: localPose,
                    SolveWidth: frame.Width,
                    Exclusions: compositionExclusions,
                    GroundAlignmentExclusions: groundAlignmentExclusions,
                    CaptureGeneration: liveWorldGeneration,
                    RoomName: roomName,
                    AtlasPixelsPerWorldUnit: gameplayAtlas.PixelsPerWorldUnit,
                    AtlasTileSize: gameplayAtlas.TileSize, StableTrackingVerified: visualObservation.GroundVerified), visualTelemetry, atlasCompositionExclusions, atlasRoomId, atlasCaptureIdentity);
                liveWorldResult = CurrentWorldResult(liveWorldGeneration, roomName);
                gameplayAdmitted = liveWorldResult?.State is WindowsLiveWorldPipelineState.Committed or WindowsLiveWorldPipelineState.Throttled;
                if (liveWorldResult?.CaptureGeneration == liveWorldGeneration
                    && liveWorldResult.State is WindowsLiveWorldPipelineState.Committed
                        or WindowsLiveWorldPipelineState.Throttled)
                {
                    visualTelemetry = visualTelemetry with
                    {
                        CameraX = localPose.X + liveWorldResult.WorldBasisOffset.X,
                        CameraY = localPose.Y + liveWorldResult.WorldBasisOffset.Y,
                        CameraTargetX = localPose.X + liveWorldResult.WorldBasisOffset.X,
                        CameraTargetY = localPose.Y + liveWorldResult.WorldBasisOffset.Y
                    };
                }
            }
        }
        if (inputPathDivergenceMonitor.IsAcceptingSamples
            && renderedGameFrame is { } renderedFrame)
        {
            var decision = inputPathDivergenceMonitor.AppendVision(
                renderedFrame,
                new AtlasPoint(visualTelemetry.CameraX, visualTelemetry.CameraY),
                captureTimestamp,
                isTransitioning: !admission.AdmitsTracking);
            if (decision is not null)
            {
                ObserveReplayDecision(decision);
            }
        }
        var replacesGameplayAtlas = false; // Background world owner publishes closure replacements.
        var resultGameplayAtlas = replacesGameplayAtlas
            ? liveWorldResult!.RebuiltAtlas!
            : gameplayAtlas;
        var gameplayAccepted = gameplayAdmitted && gameMode != GameRuntimeMode.Modded
            && (replacesGameplayAtlas
                || resultGameplayAtlas.AddFrame(
                    frame,
                    visualTelemetry,
                    atlasCompositionExclusions,
                    roomId: atlasRoomId,
                    captureGeneration: liveWorldGeneration,
                    captureIdentity: atlasCaptureIdentity));
        Volatile.Write(ref replayVision, new(captureTimestamp, captureGeneration,
            groundHypotheses.PoseVerified && groundHypotheses.HasConfirmedGround && groundHypotheses.GroundSegmentCount > 0, frame.Width, frame.Height));
        var publishedCameraPosition = new AtlasPoint(
            visualTelemetry.CameraX,
            visualTelemetry.CameraY);
        gameControls.RecordCoarseMotion(
            captureTimestamp,
            renderedGameFrame,
            publishedCameraPosition,
            motionBridgeActive ? publishedCameraPosition : null,
            coarseMotion,
            signal.MotionGrid,
            motionBridgeActive,
            !admission.AdmitsTracking,
            signal.MeanPeak,
            signal.VisibleFraction, WindowsLiveWorldPipeline.RoomIdentifier(atlasRoomId), (ulong)Math.Max(0, liveWorldResult?.Revision ?? 0));
        gameControls.RecordVisualTracking(
            captureTimestamp,
            new InputPathTrackingObservation
            {
                CameraPosition = groundHypotheses.CameraPosition,
                PoseVerified = groundHypotheses.PoseVerified,
                HasConfirmedGround = groundHypotheses.HasConfirmedGround,
                GlobalCorrection = groundHypotheses.GlobalCorrection,
                GlobalMatchCount = groundHypotheses.GlobalMatchCount,
                GroundSegmentCount = groundHypotheses.GroundSegmentCount,
                InlierCount = groundHypotheses.InlierCount,
                ResidualRms = groundHypotheses.ResidualRms,
                VisibleLineIds = groundHypotheses.LineReviews.Select(line => line.Id).ToArray(),
                AtlasLineIds = groundHypotheses.AtlasLines.Select(line => line.SegmentId).Distinct().ToArray(),
                LocalTextureSupport = groundHypotheses.LocalTextureSupport,
                LocalTextureError = groundHypotheses.LocalTextureError,
                LocalMatchSource = groundHypotheses.LocalMatchSource,
                LocalReferenceTimestamp = groundHypotheses.LocalReferenceTimestamp,
                LocalReferencePosition = groundHypotheses.LocalReferencePosition,
                LocalMatchedImageDisplacement = groundHypotheses.LocalMatchedImageDisplacement,
                ProvisionalContinuityActive = groundHypotheses.ProvisionalContinuityActive
            },
            publishedCameraPosition,
            visualObservation.Source,
            motionBridgeActive ? motion?.Motion?.Confidence : null,
            motionBridgeActive ? visualObservation.Source : null,
            signal.MeanPeak,
            signal.VisibleFraction,
            renderedGameFrame,
            gameplayAdmitted, groundMilliseconds, WindowsLiveWorldPipeline.RoomIdentifier(atlasRoomId),
            (ulong)Math.Max(0, liveWorldResult?.Revision ?? 0), visualGameplayTracker.PlacementRecoveryState,
            motionBridgeActive ? new AtlasPoint(visualObservation.Telemetry.CameraX, visualObservation.Telemetry.CameraY) : null,
            motionBridgeActive ? coarseMotion?.Direction : null);
        if ((gameMode == GameRuntimeMode.Modded && resultGameplayAtlas.Revision != Interlocked.Read(ref gameplayRenderedRevision))
            || gameplayAccepted && (replacesGameplayAtlas
            || resultGameplayAtlas.AcceptedFrameCount == 1
            || resultGameplayAtlas.AcceptedFrameCount % 3 == 0))
        {
            gameplayTiles = resultGameplayAtlas.SnapshotSince(
                replacesGameplayAtlas
                    ? -1
                    : Interlocked.Read(ref gameplayRenderedRevision));
        }
        return new VisionAnalysisResult(
            frame, detections, ground, gameplayTiles,
            visualTelemetry, receiverTelemetry, recognition.Status, motion, recognition.Menu, recognition.Generation,
            hudStencil, admission.AdmitsTracking, recognition.Diagnostic + FormattableString.Invariant(
                $"; signal={signal.SignaledFraction:F3} mean={signal.MeanPeak:F2} tracking={admission.AdmitsTracking} resumed={admission.Transition.ResumedAfterTransition} pose={visualObservation.Source} support={visualObservation.GroundSupport}")
                + (liveWorldResult is null ? "" : $"; {liveWorldResult.Diagnostic}"))
            {
                GroundMilliseconds = groundMilliseconds,
                AtlasAndWorldMilliseconds = Stopwatch.GetElapsedTime(atlasStarted).TotalMilliseconds,
                PoseSource = visualObservation.Source,
                AllowsAtlasWrite = gameplayAdmitted,
                Objects = recognition.Objects,
                ObjectDiagnostics = recognition.ObjectDiagnostics,
                LabelRevision = labelRevision,
                CaptureGeneration = captureGeneration,
                WorldBasisOffset = gameplayAdmitted
                    ? liveWorldResult?.WorldBasisOffset
                    : null,
                WorldBasisTimestamp = gameplayAdmitted
                    ? captureTimestamp
                    : null,
                AtlasSource = resultGameplayAtlas,
                ReplacesGameplayAtlas = replacesGameplayAtlas,
                GameplayAtlasReplacement = replacesGameplayAtlas
                    ? resultGameplayAtlas
                    : null,
                AcceptedClosure = gameplayAccepted
                    ? liveWorldResult?.AcceptedClosure
                    : null,
                LiveWorldRevision = liveWorldResult?.Revision ?? 0,
                AppliedWorldCorrection = liveWorldResult?.AppliedCorrection
                    ?? new AtlasPoint(0, 0),
                CaptureTimestamp = captureTimestamp,
                ReceiverObservedAt = exactReceiverObservedAt,
                OwnsHackerWork = ownsHackerWork,
                IntegratesHackerAtlas = integratesHackerAtlas,
                HackerWorkGeneration = pendingHackerWorkGeneration,
                GroundPresenceReviews = groundPresenceReviews,
                GroundHypotheses = groundHypotheses
            };
    }

    private static IReadOnlyList<StencilRect> CreateGroundAlignmentExclusions(
        BgraFrame frame,
        AtlasPoint cameraPosition,
        double solveWidth,
        IReadOnlyList<DetectedFloorLine> ground,
        IReadOnlyList<PixelRect> foregroundExclusions,
        FrameSignalProfile signal,
        VisualGameplayObservation observation,
        bool transitionBridgeActive,
        GroundReferenceEstimate? trackedEstimate)
    {
        var existing = foregroundExclusions.Select(rectangle => new StencilRect(
            rectangle.X,
            frame.Height - rectangle.Bottom,
            rectangle.Width,
            rectangle.Height)).ToList();
        if (transitionBridgeActive && signal.EdgeBlackBands is { } transitionBands)
        {
            existing.AddRange(transitionBands.RectangularOmissions(frame.Width, frame.Height));
        }

        var estimate = trackedEstimate;
        // A verified ground solve owns ordinary frames. During a measured
        // transition, current semantic line evidence may still define the
        // narrow matcher band, with the detected edge matte excluded above.
        // No current line evidence still produces the full-frame exclusion.
        if (estimate is null
            && (observation.GroundVerified || transitionBridgeActive)
            && double.IsFinite(solveWidth)
            && solveWidth > 0)
        {
            var scale = frame.Width / solveWidth;
            var segments = ground.Select(line =>
            {
                // Detector rows and foreground rectangles address the top-down
                // BGRA capture. Ground feature geometry, like the Mac tracker,
                // is world-up; retain the half-pixel line-center convention.
                var frameY = frame.Height - line.Row - 0.5;
                return new GroundReferenceSegment(
                    cameraPosition.X + line.XSpan.Start / scale,
                    cameraPosition.X + (line.XSpan.EndInclusive + 1) / scale,
                    cameraPosition.Y + frameY / scale);
            }).Where(segment =>
                double.IsFinite(segment.MinimumWorldX)
                && double.IsFinite(segment.MaximumWorldX)
                && segment.MaximumWorldX > segment.MinimumWorldX
                && double.IsFinite(segment.WorldY ?? double.NaN))
                .ToArray();
            if (segments.Length > 0)
            {
                var confidence = observation.GroundError is { } error
                    && double.IsFinite(error)
                    ? 1 / (1 + Math.Max(0, error))
                    : 1;
                estimate = new GroundReferenceEstimate(
                    segments[0].WorldY!.Value,
                    segments.Min(segment => segment.MinimumWorldX),
                    segments.Max(segment => segment.MaximumWorldX),
                    segments,
                    segments.SelectMany(segment => new[]
                        {
                            segment.MinimumWorldX,
                            segment.MaximumWorldX
                        })
                        .Distinct()
                        .Order()
                        .ToArray(),
                    Math.Max(segments.Length, observation.GroundSupport),
                    confidence);
            }
        }

        return GroundFeatureEligibility.AlignmentExclusions(
            new WorldSize(frame.Width, frame.Height),
            cameraPosition,
            solveWidth,
            estimate,
            existing);
    }

    private void PublishAuthoritativeVisionState(VisionAnalysisResult result)
    {
        if (result.GameplayAtlasReplacement is not { } replacement) return;
        Interlocked.Exchange(ref gameplayAtlas, replacement);
        Interlocked.Exchange(ref gameplayPersistedRevision, -1);
        Interlocked.Exchange(ref gameplayRenderedRevision, -1);
        Volatile.Write(ref gameplayAtlasRequiresFullRewrite, true);
        Interlocked.Exchange(ref gameplayAtlasPresentationPending, replacement);
    }

    private void ApplyVisionAnalysis(VisionAnalysisResult result)
    {
        if (result.CaptureGeneration != Interlocked.Read(ref recentCaptureGeneration)
            || !labelCapture.CanProcess(result.LabelRevision)) return;
        if (result.AtlasSource is not null && !ReferenceEquals(result.AtlasSource, gameplayAtlas))
            result = result with { GameplayTiles = null };
        PublishGpuDiagnostics(result);
        var presentsLatestFrame = result.Frame.CaptureId
            == Interlocked.Read(ref latestPresentedCaptureId);
        var presentsLatestMatchedFrame = TryAcceptLatestMatchedResult(result);
        if (presentsLatestMatchedFrame && result.ReceiverTelemetry is { } matchedTelemetry)
        {
            var observedAt = result.ReceiverObservedAt is { } exactObservedAt
                && double.IsFinite(exactObservedAt)
                    ? exactObservedAt
                    : result.CaptureTimestamp;
            latestGroundTruthContext = new(
                result.Frame,
                matchedTelemetry,
                observedAt,
                result.GroundPresenceReviews,
                result.AdmitsTracking,
                result.CaptureGeneration,
                result.Frame.CaptureId);
            if (workspace == "Hacker")
                PresentGroundTruthMatchedContext(latestGroundTruthContext);
        }
        var menuEvidenceIsCurrent = menuStencilRecognizer.IsCurrent(
            result.MenuGeneration);
        // A closure has already committed its optimized graph before it reaches
        // the dispatcher. Publish that rebuilt atlas even if unrelated async
        // menu evidence advanced meanwhile; never publish the stale menu result.
        if (!menuEvidenceIsCurrent
            && result.GameplayAtlasReplacement is null) return;
        var replacementToPresent = Interlocked.Exchange(
            ref gameplayAtlasPresentationPending,
            null);
        if (result.WorldBasisOffset is { } basis
            && result.WorldBasisTimestamp is { } basisTimestamp)
        {
            gameplayViewport.UpdateWorldBasisOffset(
                new ViewportPoint(
                    basis.X * gameplayAtlas.PixelsPerWorldUnit,
                    -basis.Y * gameplayAtlas.PixelsPerWorldUnit),
                basisTimestamp);
        }
        if (replacementToPresent is not null
            && ReferenceEquals(gameplayAtlas, replacementToPresent))
        {
            foreach (var image in gameplayTileImages.Values)
                GameplayAtlasCanvas.Children.Remove(image);
            gameplayTileImages.Clear();
            gameplayOccupiedTileBounds.Clear();
            gameplayAtlasMinimumTileX = gameplayAtlasMinimumTileY = null;
        }
        if (!menuEvidenceIsCurrent)
        {
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
                Interlocked.Exchange(
                    ref gameplayRenderedRevision,
                    result.GameplayTiles.Revision);
            }
            if (result.AcceptedClosure is { } committedClosure)
            {
                gameControls.RecordLoopClosure(
                    result.CaptureTimestamp,
                    result.LiveWorldRevision,
                    result.AppliedWorldCorrection,
                    committedClosure);
            }
            PersistActiveAtlas(force: false);
            return;
        }
        // The capture loop has usually displayed a newer bitmap while tracking
        // runs. Accept ordered positions without replacing it with older pixels.
        if (gameplayPresentation.TryAcceptAnalysis(result.Frame))
        {
            Volatile.Write(ref recognizedMenuContext, result.MenuContext);
            MenuParticles.SetScene(result.MenuContext, result.MenuStencil);
            Volatile.Write(ref recognizedMenuSelection, result.Status?.SelectedMenuOption);
            VisionStatusText.ToolTip = result.StencilDiagnostic;
            PresentObjectInferenceDiagnostics(ObjectInferenceIssueOverlay, ObjectInferenceIssueOverlayText,
                ObjectInferenceIssueText, ObjectDetectionOptions, result.ObjectDiagnostics);
            PresentGameplayFrame(
                gameplayPresentation.LatestFrame!,
                result.GameplayTelemetry,
                (BitmapSource)GameplayImage.Source,
                result.MenuContext);
        }
        if (presentsLatestFrame)
        {
            DrawDetections(
                result.Objects,
                result.Ground,
                result.GroundHypotheses,
                result.MenuStencil,
                result.HudStencil);
        }
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
        if (result.AcceptedClosure is { } closure)
        {
            gameControls.RecordLoopClosure(
                result.CaptureTimestamp,
                result.LiveWorldRevision,
                result.AppliedWorldCorrection,
                closure);
        }
        if (presentsLatestFrame)
        {
            if (GroundFeaturesCheck.IsChecked == true)
                DrawGroundHypothesisAtlasOverlay(result.GroundHypotheses, result.Frame);
            else if (result.AdmitsTracking)
                UpdateTrackedGround(result.Ground, result.GameplayTelemetry);
            else GameplayGroundCanvas.Children.Clear();
        }
        if (presentsLatestFrame) DrawMotionOverlay(result.Motion);
        PersistActiveAtlas(force: false);
    }

    private string captureBackend = "Starting";
    private string? captureFallbackReason;
    private double captureReadbackMilliseconds;
    private long previousAnalysisStartedTimestamp;
    private double maxAnalysisGapMilliseconds;
    private double lastAtlasSaveMilliseconds;
    private long diagnosticAnalysisFrames;
    private long lastDiagnosticAnalysisFrames;
    private long lastDiagnosticCaptureId;
    private void PublishGpuDiagnostics(VisionAnalysisResult result)
    {
        diagnosticAnalysisFrames++;
        if (previousAnalysisStartedTimestamp != 0)
        {
            maxAnalysisGapMilliseconds = Math.Max(maxAnalysisGapMilliseconds,
                Stopwatch.GetElapsedTime(previousAnalysisStartedTimestamp,
                    result.AnalysisStartedTimestamp).TotalMilliseconds);
        }
        previousAnalysisStartedTimestamp = result.AnalysisStartedTimestamp;
        var now = DateTimeOffset.UtcNow;
        var diagnosticElapsed = (now - lastGpuDiagnosticAt).TotalSeconds;
        if (now - lastGpuDiagnosticAt < TimeSpan.FromSeconds(5)
            || gpuDiagnosticWrite is { IsCompleted: false }) return;
        lastGpuDiagnosticAt = now;
        var matcher = GroundPatchMatcher.Shared;
        var localSearch = visualGameplayTracker.LastPatchSearchExecution;
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            ObservedAt = now,
            ProcessId = Environment.ProcessId,
            Device = matcher.CudaDeviceName,
            CudaAvailable = matcher.CudaAvailable,
            CaptureBackend = captureBackend,
            CaptureFallbackReason = captureFallbackReason,
            CaptureReadbackMilliseconds = captureReadbackMilliseconds,
            CudaDispatches = matcher.CudaDispatchCount,
            CameraBackend = localSearch?.Backend.ToString(),
            CameraSearchMilliseconds = localSearch?.Elapsed.TotalMilliseconds,
            CameraCandidates = localSearch?.CandidateCount,
            FallbackReason = matcher.CudaFailure,
            AnalyzedFramesPerSecond = lastDiagnosticAnalysisFrames == 0 ? (double?)null
                : (diagnosticAnalysisFrames - lastDiagnosticAnalysisFrames) / diagnosticElapsed,
            CapturedFramesPerSecond = lastDiagnosticCaptureId == 0 ? (double?)null
                : (Interlocked.Read(ref latestPresentedCaptureId) - lastDiagnosticCaptureId) / diagnosticElapsed,
            FrameAgeMilliseconds = Math.Max(0, (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency
                - result.CaptureTimestamp) * 1000),
            result.AnalysisMilliseconds,
            MaxAnalysisGapMilliseconds = maxAnalysisGapMilliseconds,
            LastAtlasSaveMilliseconds = lastAtlasSaveMilliseconds,
            AtlasSaveInProgress = atlasPersistTask is { IsCompleted: false },
            result.QueueMilliseconds,
            result.GroundMilliseconds,
            result.AtlasAndWorldMilliseconds,
            WorldWorkerError = worldWorker.LastError?.Message,
            result.PoseSource,
            result.AdmitsTracking,
            result.AllowsAtlasWrite,
            result.StencilDiagnostic,
            GroundVerified = result.GroundHypotheses.PoseVerified,
            result.GroundHypotheses.HasConfirmedGround,
            result.GroundHypotheses.LocalTextureSupport,
            result.GroundHypotheses.LocalTextureError,
            result.GroundHypotheses.InlierCount,
            result.GroundHypotheses.GlobalMatchCount,
            GroundFeatureCount = result.GroundHypotheses.Features.Count,
            result.GroundHypotheses.GlobalFeatureCount,
            result.GroundHypotheses.GroundSegmentCount,
            result.GameplayTelemetry.CameraX,
            result.GameplayTelemetry.CameraY
        });
        maxAnalysisGapMilliseconds = 0;
        lastDiagnosticAnalysisFrames = diagnosticAnalysisFrames;
        lastDiagnosticCaptureId = Interlocked.Read(ref latestPresentedCaptureId);
        gpuDiagnosticWrite = Task.Run(() =>
        {
            try
            {
                var directory = IOPath.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "HollowKnightVision", "diagnostics");
                Directory.CreateDirectory(directory);
                File.WriteAllText(IOPath.Combine(directory, "tracking-gpu.json"), json);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }

    private void StopVision_Click(object sender, RoutedEventArgs e) => StopVision();

    private void StopVision()
    {
        CancelGroundTruthGesture();
        prepareLabelWhenPaused = false;
        if (gameMode == GameRuntimeMode.Modded) _ = gameControls.SetLabelPauseActive(false);
        labelCapture.Reset(GamePauseState.Unavailable);
        if (workspace == "Label" && !isClosing) _ = SelectWorkspace("Gameplay", capture: false);
        visionCancellation?.Cancel();
        menuStencilRecognizer.StopTracking();
        PresentObjectInferenceDiagnostics(ObjectInferenceIssueOverlay, ObjectInferenceIssueOverlayText,
            ObjectInferenceIssueText, ObjectDetectionOptions, new());
        StopVisionButton.IsEnabled = false;
    }

    private void DrawDetections(
        LiveObjectDetectionSnapshot objects,
        IReadOnlyList<DetectedFloorLine> groundLines,
        GroundHypothesisTrackingResult groundHypotheses,
        MenuStencilResult? menuStencil,
        HudStencilResult? hudStencil)
    {
        DetectionCanvas.Children.Clear();
        if (ObjectDetectionCheck.IsChecked == true)
        {
            DrawObjectDetectionOverlay(DetectionCanvas, objects, CanvasWidth, CanvasHeight,
                RawDetectionCheck.IsChecked == true, DetectionPixelsCheck.IsChecked == true);
        }
        if (StencilDetectionCheck.IsEnabled && StencilDetectionCheck.IsChecked == true)
        {
            if (menuStencil is not null) DrawMenuStencil(DetectionCanvas, menuStencil);
            if (hudStencil is not null) DrawHudStencil(DetectionCanvas, hudStencil);
        }
        if (GroundFeaturesCheck.IsChecked == true)
        {
            GroundFeatureOverlayPresentation.DrawLive(
                DetectionCanvas,
                groundHypotheses);

            foreach (var review in GroundLineDetector.FeatureHypothesisPresenceReviews(
                         groundHypotheses.LineReviews))
            {
                DetectionCanvas.Children.Add(new Line
                {
                    X1 = review.Line.XSpan.Start,
                    X2 = review.Line.XSpan.EndInclusive + 1,
                    Y1 = review.Line.Row + .5,
                    Y2 = review.Line.Row + .5,
                    Stroke = GroundFeatureOverlayPresentation.Brush(
                        DashboardFeatureReviewLineKind.CurrentGround),
                    StrokeThickness = DashboardFeatureReviewGeometry.LineWidth,
                    Opacity = DashboardFeatureReviewPalette.Rgba(
                        DashboardFeatureReviewLineKind.CurrentGround).Alpha,
                    ToolTip = FormattableString.Invariant(
                        $"confirmed ground {review.Id} • {review.DetectedFraction:P0}")
                });
            }
        }
    }

    private void DrawGroundHypothesisAtlasOverlay(
        GroundHypothesisTrackingResult groundHypotheses,
        BgraFrame frame)
    {
        GameplayGroundCanvas.Children.Clear();
        if (groundHypotheses.CameraPosition is not { } camera
            || !double.IsFinite(camera.X)
            || !double.IsFinite(camera.Y)
            || frame.Width <= 0
            || frame.Height <= 0)
        {
            return;
        }

        var liveLeft = Canvas.GetLeft(GameplayImage);
        var liveTop = Canvas.GetTop(GameplayImage);
        var scaleX = GameplayImage.Width / frame.Width;
        var scaleY = GameplayImage.Height / frame.Height;
        if (!double.IsFinite(liveLeft) || !double.IsFinite(liveTop)
            || !double.IsFinite(scaleX) || scaleX <= 0
            || !double.IsFinite(scaleY) || scaleY <= 0)
        {
            return;
        }

        Point Project(AtlasPoint point) => new(
            liveLeft + (point.X - camera.X) * scaleX,
            liveTop + (frame.Height + camera.Y - point.Y) * scaleY);

        foreach (var feature in groundHypotheses.AtlasFeatures)
        {
            if (!double.IsFinite(feature.AtlasPosition.X)
                || !double.IsFinite(feature.AtlasPosition.Y)) continue;
            var center = Project(feature.AtlasPosition);
            var marker = GroundFeatureOverlayPresentation.PersistentRectangle(
                feature,
                scaleX,
                scaleY);
            Canvas.SetLeft(marker, center.X - marker.Width * .5);
            Canvas.SetTop(marker, center.Y - marker.Height * .5);
            GameplayGroundCanvas.Children.Add(marker);
        }

        foreach (var groundLine in groundHypotheses.AtlasLines)
        {
            if (!double.IsFinite(groundLine.AtlasStart.X)
                || !double.IsFinite(groundLine.AtlasStart.Y)
                || !double.IsFinite(groundLine.AtlasEnd.X)
                || !double.IsFinite(groundLine.AtlasEnd.Y)) continue;
            var start = Project(groundLine.AtlasStart);
            var end = Project(groundLine.AtlasEnd);
            GameplayGroundCanvas.Children.Add(new Line
            {
                X1 = start.X,
                Y1 = start.Y,
                X2 = end.X,
                Y2 = end.Y,
                Stroke = GroundFeatureOverlayPresentation.Brush(
                    DashboardFeatureReviewLineKind.PersistentGround),
                StrokeThickness = DashboardFeatureReviewGeometry.LineWidth,
                Opacity = DashboardFeatureReviewPalette.Rgba(
                    DashboardFeatureReviewLineKind.PersistentGround).Alpha,
                IsHitTestVisible = false,
                ToolTip = $"persistent ground {groundLine.SegmentId}"
            });
        }
    }

    private void DetectionExtendSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Volatile.Write(ref objectDetectionExtension, e.NewValue / 100);
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
        if (GroundFeaturesCheck.IsChecked == true) return;
        GameplayGroundCanvas.Children.Clear();
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
        if (TransitionsCheck.IsChecked == true
            && hackerLivePrepared?.SceneName is { Length: > 0 } currentHackerScene)
        {
            var label = new TextBlock
            {
                Text = currentHackerScene,
                Foreground = Brushes.Violet,
                Background = Brushes.Black,
                FontFamily = new FontFamily("Consolas"), Padding = new Thickness(3)
            };
            Canvas.SetLeft(label, 10);
            Canvas.SetTop(label, 86);
            DetectionCanvas.Children.Add(label);
        }
    }

    private void GroundMode_Click(object sender, RoutedEventArgs e)
    {
        ReleaseForwardedInput();
        CancelGroundTruthGesture();
        SelectGroundTruthMode();
        RedrawHackerEdits();
    }

    private void SelectGroundTruthMode()
    {
        groundTruthLabels.Mode = GroundModifyButton.IsChecked == true
            ? GroundTruthEditingMode.Modify
            : GroundDeleteButton.IsChecked == true
                ? GroundTruthEditingMode.Delete
                : GroundNegativeButton.IsChecked == true
                    ? GroundTruthEditingMode.Negative
                    : GroundTruthEditingMode.Add;
    }

    private void GroundUndo_Click(object sender, RoutedEventArgs e)
    {
        var cancelledActiveGesture = groundTruthLabels.HasActiveGesture;
        CancelGroundTruthGesture();
        if (cancelledActiveGesture)
        {
            GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
            PresentGroundTruthPersistenceError();
            RedrawHackerEdits();
            return;
        }
        groundTruthLabels.Undo();
        GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
        PresentGroundTruthPersistenceError();
        RedrawHackerEdits();
    }

    private void GroundEditorMouseDown(MouseButtonEventArgs e)
    {
        if (!TryGroundTruthPointer(
                e.GetPosition(AtlasCanvas),
                out var point,
                out var tolerance,
                out var transform,
                out var liveBounds)) return;
        SelectGroundTruthMode();
        groundTruthLabels.Handle(
            GroundTruthPointerPhase.Began,
            point,
            tolerance,
            transform,
            liveBounds);
        groundDragStart = e.GetPosition(AtlasCanvas);
        groundDragTransform = transform;
        groundDragBounds = liveBounds;
        Mouse.Capture(AtlasCanvas, CaptureMode.Element);
        GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
        PresentGroundTruthPersistenceError();
        RedrawHackerEdits();
        e.Handled = true;
    }

    private void GroundEditorMouseMove(MouseEventArgs e)
    {
        if (groundDragStart is null || e.LeftButton != MouseButtonState.Pressed
            || groundDragTransform is not { } transform
            || !TryGroundTruthPointer(
                e.GetPosition(AtlasCanvas),
                transform,
                groundDragBounds,
                out var point,
                out var tolerance)) return;
        groundTruthLabels.Handle(
            GroundTruthPointerPhase.Changed,
            point,
            tolerance,
            transform,
            groundDragBounds);
        RedrawHackerEdits();
        e.Handled = true;
    }

    private void GroundEditorMouseUp(MouseButtonEventArgs e)
    {
        var completed = false;
        if (groundDragStart is not null
            && groundDragTransform is { } transform
            && TryGroundTruthPointer(
                e.GetPosition(AtlasCanvas),
                transform,
                groundDragBounds,
                out var point,
                out var tolerance))
        {
            groundTruthLabels.Handle(
                GroundTruthPointerPhase.Ended,
                point,
                tolerance,
                transform,
                groundDragBounds);
            completed = true;
        }
        if (!completed) groundTruthLabels.CancelGesture();
        groundDragStart = null;
        groundDragTransform = null;
        Mouse.Capture(null);
        GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
        PresentGroundTruthPersistenceError();
        RedrawHackerEdits();
        e.Handled = true;
    }

    private void CancelGroundTruthGesture()
    {
        groundTruthLabels.CancelGesture();
        groundDragStart = null;
        groundDragTransform = null;
        if (ReferenceEquals(Mouse.Captured, AtlasCanvas)) Mouse.Capture(null);
        if (GroundUndoButton is not null)
            GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
        if (HackerOverlayCanvas is not null) RedrawHackerEdits();
    }

    private bool TryGroundTruthPointer(
        Point canvasPoint,
        out AtlasPoint controllerPoint,
        out double controllerTolerance,
        out GroundTruthCameraTransform transform,
        out GroundTruthRect liveBounds)
    {
        controllerPoint = default;
        controllerTolerance = 0;
        if (!TryGroundTruthContext(out transform, out liveBounds)) return false;
        return TryGroundTruthPointer(
            canvasPoint,
            transform,
            liveBounds,
            out controllerPoint,
            out controllerTolerance);
    }

    private bool TryGroundTruthPointer(
        Point canvasPoint,
        GroundTruthCameraTransform transform,
        GroundTruthRect liveBounds,
        out AtlasPoint controllerPoint,
        out double controllerTolerance)
    {
        var global = ToAtlasGlobal(canvasPoint);
        var roomPosition = HackerScenePosition(transform.SceneName);
        var world = new AtlasPoint(
            (global.X - roomPosition.X) / transform.PixelsPerWorldUnitX,
            -(global.Y + roomPosition.Y) / transform.PixelsPerWorldUnitY);
        controllerPoint = transform.WorldToAtlas(world, liveBounds);
        var zoom = Math.Abs((AtlasCanvas.RenderTransform as MatrixTransform)?.Matrix.M11 ?? 1);
        if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
        controllerTolerance = 8 / zoom;
        return true;
    }

    private bool TryGroundTruthContext(
        out GroundTruthCameraTransform transform,
        out GroundTruthRect liveBounds)
    {
        transform = null!;
        liveBounds = default;
        if (hackerLiveFrame is not { } frame
            || hackerLivePrepared is not { } prepared
            || string.IsNullOrWhiteSpace(prepared.SceneName)) return false;
        transform = prepared.CameraTransform;
        liveBounds = new GroundTruthRect(0, 0, frame.Width, frame.Height);
        return true;
    }

    private bool PresentLatestGroundTruthContext()
    {
        var context = latestGroundTruthContext;
        if (context is null
            || context.CaptureGeneration != Interlocked.Read(ref recentCaptureGeneration))
        {
            return false;
        }
        PresentGroundTruthMatchedContext(context);
        return true;
    }

    private void PresentGroundTruthMatchedContext(GroundTruthMatchedContext context)
    {
        if (hackerLiveFrame?.CaptureId != context.CaptureId)
        {
            RedrawHackerEdits();
            return;
        }
        TryMigrateLegacyGroundEdits();
        if (context.AdmitsTracking)
            EnterGroundTruthLabelScene(context.GroundPresenceReviews);
        else RedrawHackerEdits();
    }

    private void TryMigrateLegacyGroundEdits()
    {
        if (!canPersistHackerEdits
            || legacyGroundMigrationVersion >= LegacyGroundMigrationVersion
            || !TryGroundTruthContext(out var transform, out var liveBounds))
        {
            return;
        }
        if (legacyGroundEdits.Count == 0)
        {
            legacyGroundMigrationVersion = LegacyGroundMigrationVersion;
            try { PersistHackerEdits(); }
            catch (Exception error)
            {
                canPersistHackerEdits = false;
                SetStatus($"Legacy ground-label migration marker was not saved: {error.Message}");
            }
            return;
        }

        var migrated = new List<GroundTruthWorldLine>();
        var remaining = new List<GroundEditLine>();
        foreach (var legacy in legacyGroundEdits)
        {
            var sceneName = LegacyGroundScene(legacy, transform, liveBounds);
            if (sceneName is null || !ValidLegacyGroundLine(legacy))
            {
                remaining.Add(legacy);
                continue;
            }
            const double pixelsPerWorldUnit = HackerAtlasPixelsPerWorldUnit;
            migrated.Add(new GroundTruthWorldLine(
                legacy.Id,
                sceneName,
                Math.Min(legacy.X0, legacy.X1) / pixelsPerWorldUnit,
                Math.Max(legacy.X0, legacy.X1) / pixelsPerWorldUnit,
                -(legacy.Y0 + legacy.Y1) * .5 / pixelsPerWorldUnit,
                legacy.IsNegative
                    ? GroundTruthLineKind.Negative
                    : GroundTruthLineKind.Positive,
                false));
        }
        if (migrated.Count == 0) return;
        if (!groundTruthLabels.ImportLegacyLines(migrated))
        {
            PresentGroundTruthPersistenceError();
            return;
        }

        // The world-line store committed first. Stable legacy IDs make a retry
        // idempotent if publication of the migration marker fails.
        legacyGroundEdits = remaining;
        if (remaining.Count == 0)
            legacyGroundMigrationVersion = LegacyGroundMigrationVersion;
        try { PersistHackerEdits(); }
        catch (Exception error)
        {
            canPersistHackerEdits = false;
            SetStatus($"Legacy labels were migrated, but their marker was not saved: {error.Message}");
        }
    }

    private string? LegacyGroundScene(
        GroundEditLine line,
        GroundTruthCameraTransform transform,
        GroundTruthRect liveBounds)
    {
        if (!ValidLegacyGroundLine(line)) return null;
        var midpoint = new Point(
            (line.X0 + line.X1) * .5,
            (line.Y0 + line.Y1) * .5);
        var topLeftWorld = transform.ScreenToWorld(new AtlasPoint(0, 0));
        var bottomRightWorld = transform.ScreenToWorld(new AtlasPoint(
            liveBounds.Width,
            liveBounds.Height));
        const double pixelsPerWorldUnit = HackerAtlasPixelsPerWorldUnit;
        var currentLeft = Math.Min(topLeftWorld.X, bottomRightWorld.X) * pixelsPerWorldUnit;
        var currentRight = Math.Max(topLeftWorld.X, bottomRightWorld.X) * pixelsPerWorldUnit;
        var currentTop = Math.Min(-topLeftWorld.Y, -bottomRightWorld.Y) * pixelsPerWorldUnit;
        var currentBottom = Math.Max(-topLeftWorld.Y, -bottomRightWorld.Y) * pixelsPerWorldUnit;
        if (midpoint.X >= currentLeft && midpoint.X <= currentRight
            && midpoint.Y >= currentTop && midpoint.Y <= currentBottom)
        {
            return transform.SceneName;
        }

        var candidates = rooms.Values
            .Where(room => midpoint.X >= Math.Min(room.Left, room.Right)
                && midpoint.X <= Math.Max(room.Left, room.Right)
                && midpoint.Y >= Math.Min(room.Top, room.Bottom)
                && midpoint.Y <= Math.Max(room.Top, room.Bottom))
            .Select(room => room.SceneName)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool RenderableLegacyGroundLine(GroundEditLine line) =>
        double.IsFinite(line.X0) && double.IsFinite(line.Y0)
        && double.IsFinite(line.X1) && double.IsFinite(line.Y1)
        && Math.Abs(line.X0) <= 1_000_000 && Math.Abs(line.Y0) <= 1_000_000
        && Math.Abs(line.X1) <= 1_000_000 && Math.Abs(line.Y1) <= 1_000_000;

    private static bool ValidLegacyGroundLine(GroundEditLine line) =>
        line.Id != Guid.Empty && RenderableLegacyGroundLine(line)
        && Math.Abs(line.X1 - line.X0) >= 4;

    private void EnterGroundTruthLabelScene(
        IReadOnlyList<GroundLinePresenceReview> reviews)
    {
        if (!TryGroundTruthContext(out var transform, out var liveBounds)) return;
        TryMigrateLegacyGroundEdits();
        var confirmed = GroundLineDetector.FeatureHypothesisPresenceReviews(reviews);
        if (confirmed.Count > 0)
        {
            groundTruthLabels.Enter([], confirmed, transform, liveBounds);
            PresentGroundTruthPersistenceError();
        }
        GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
        RedrawHackerEdits();
    }

    private void PresentGroundTruthPersistenceError()
    {
        HackerGroundToolbar.ToolTip = groundTruthLabels.Error;
        if (groundTruthLabels.Error is { Length: > 0 } error)
            SetStatus(error);
    }

    private void RoomEditorMouseDown(MouseButtonEventArgs e)
    {
        var point = ToAtlasGlobal(e.GetPosition(AtlasCanvas));
        var topology = BuildHackerRoomLayout();
        activeRoom = topology.Rooms.Values.Reverse().Select(room =>
        {
            var position = HackerRoomPosition(room, topology.Edits, topology.Layout);
            var bounds = room.Bounds.Offset(position.X, position.Y);
            var left = bounds.MinX;
            var top = -bounds.MaxY;
            var right = bounds.MaxX;
            var bottom = -bounds.MinY;
            return point.X >= left && point.X <= right
                && point.Y >= top && point.Y <= bottom
                ? topology.Edits[room.Id]
                : null;
        }).FirstOrDefault(room => room is not null);
        if (activeRoom is null) return;
        roomDragStart = point;
        roomOriginalOffsetX = activeRoom.OffsetX;
        roomOriginalOffsetY = activeRoom.OffsetY;
        Mouse.Capture(AtlasCanvas, CaptureMode.Element);
        e.Handled = true;
    }

    private Point ToAtlasGlobal(Point local) => new(
        local.X + (hackerAtlasMinimumTileX ?? 0) * HackerAtlasTileSize,
        local.Y + (hackerAtlasMinimumTileY ?? 0) * HackerAtlasTileSize);

    private (
        IReadOnlyDictionary<Guid, HackerAtlasRoom> Rooms,
        IReadOnlyDictionary<Guid, RoomEdit> Edits,
        IReadOnlyList<HackerRoomConnection> Connections,
        HackerRoomLayout Layout) BuildHackerRoomLayout()
    {
        var coreRooms = hackerAtlasOutput?.Rooms.ToArray()
            ?? rooms.Values
                .OrderBy(room => room.SceneName, StringComparer.Ordinal)
                .Select(room => new HackerAtlasRoom(
                    HackerLayoutId("room", room.SceneName),
                    room.SceneName,
                    new AtlasPoint(room.LayoutX, room.LayoutY),
                    new HackerAtlasRect(
                        room.Left,
                        -room.Bottom,
                        Math.Max(1, room.Right - room.Left),
                        Math.Max(1, room.Bottom - room.Top))))
                .ToArray();
        var byName = coreRooms.ToDictionary(room => room.SceneName, StringComparer.Ordinal);
        var byId = coreRooms.ToDictionary(room => room.Id);
        var edits = coreRooms
            .Where(room => rooms.ContainsKey(room.SceneName))
            .ToDictionary(room => room.Id, room => rooms[room.SceneName]);
        var connections = new List<HackerRoomConnection>();
        if (hackerAtlasOutput is { } canonical)
        {
            connections.AddRange(canonical.Connections.Where(connection =>
                byId.ContainsKey(connection.FromRoomId)
                && byId.ContainsKey(connection.ToRoomId)));
        }
        else foreach (var measured in roomConnections.OrderBy(connection => connection.Id))
        {
            if (!byName.TryGetValue(measured.FromSceneName, out var from)
                || !byName.TryGetValue(measured.ToSceneName, out var to)
                || from.Id == to.Id
                || !double.IsFinite(measured.FromCoordinate)
                || !double.IsFinite(measured.ToCoordinate)) continue;
            connections.Add(new HackerRoomConnection(
                measured.Id,
                from.Id,
                to.Id,
                measured.FromSide,
                measured.ToSide,
                measured.FromCoordinate,
                measured.ToCoordinate));
        }

        return (byId, edits, connections, HackerRoomLayout.Make(coreRooms, connections));
    }

    private static AtlasPoint HackerRoomPosition(
        HackerAtlasRoom room,
        IReadOnlyDictionary<Guid, RoomEdit> edits,
        HackerRoomLayout layout)
    {
        var canonical = layout.Positions.TryGetValue(room.Id, out var position)
            ? position
            : new AtlasPoint(0, 0);
        return edits.TryGetValue(room.Id, out var edit)
            ? new AtlasPoint(
                canonical.X + edit.OffsetX,
                canonical.Y - edit.OffsetY)
            : canonical;
    }

    private AtlasPoint HackerScenePosition(string sceneName)
    {
        var topology = BuildHackerRoomLayout();
        var room = topology.Rooms.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.SceneName, sceneName, StringComparison.Ordinal));
        return room is null
            ? new AtlasPoint(0, 0)
            : HackerRoomPosition(room, topology.Edits, topology.Layout);
    }

    private static Guid HackerLayoutId(string kind, string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}\0{value}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static AtlasPoint HackerRoomEndpoint(
        HackerAtlasRoom room,
        AtlasPoint position,
        HackerRoomSide side,
        double coordinate)
    {
        var bounds = room.Bounds.Offset(position.X, position.Y);
        if (side.IsHorizontal())
        {
            return new AtlasPoint(
                side == HackerRoomSide.Left ? bounds.MinX : bounds.MaxX,
                position.Y + Math.Clamp(coordinate, room.Bounds.MinY, room.Bounds.MaxY));
        }
        return new AtlasPoint(
            position.X + Math.Clamp(coordinate, room.Bounds.MinX, room.Bounds.MaxX),
            side == HackerRoomSide.Bottom ? bounds.MinY : bounds.MaxY);
    }

    private void RedrawHackerEdits()
    {
        HackerOverlayCanvas.Children.Clear();
        var originX = (hackerAtlasMinimumTileX ?? 0) * HackerAtlasTileSize;
        var originY = (hackerAtlasMinimumTileY ?? 0) * HackerAtlasTileSize;
        if (hackerInteractionMode == "Ground")
        {
            var positiveBrush = new SolidColorBrush(Color.FromRgb(69, 255, 120));
            var negativeBrush = new SolidColorBrush(Color.FromRgb(255, 46, 46));
            foreach (var line in legacyGroundEdits.Where(RenderableLegacyGroundLine))
            {
                HackerOverlayCanvas.Children.Add(new Line
                {
                    X1 = line.X0 - originX,
                    Y1 = line.Y0 - originY,
                    X2 = line.X1 - originX,
                    Y2 = line.Y1 - originY,
                    Stroke = line.IsNegative ? negativeBrush : positiveBrush,
                    StrokeThickness = 4,
                    Opacity = .75,
                    IsHitTestVisible = false
                });
            }
            if (TryGroundTruthContext(out var transform, out var liveBounds))
            {
                var roomPosition = HackerScenePosition(transform.SceneName);
                var anchor = hackerLivePrepared?.AnchorPosition ?? new AtlasPoint(0, 0);
                foreach (var line in groundTruthLabels.OverlayLines(transform, liveBounds))
                {
                    var firstWorld = transform.AtlasToWorld(line.Start, liveBounds);
                    var lastWorld = transform.AtlasToWorld(line.End, liveBounds);
                    HackerOverlayCanvas.Children.Add(new Line
                    {
                        X1 = firstWorld.X * transform.PixelsPerWorldUnitX - anchor.X
                            + roomPosition.X - originX,
                        Y1 = -(firstWorld.Y * transform.PixelsPerWorldUnitY - anchor.Y
                            + roomPosition.Y) - originY,
                        X2 = lastWorld.X * transform.PixelsPerWorldUnitX - anchor.X
                            + roomPosition.X - originX,
                        Y2 = -(lastWorld.Y * transform.PixelsPerWorldUnitY - anchor.Y
                            + roomPosition.Y) - originY,
                        Stroke = line.Kind switch
                        {
                            GroundTruthOverlayLineKind.PositiveGroundTruth => positiveBrush,
                            GroundTruthOverlayLineKind.NegativeGroundTruth => negativeBrush,
                            GroundTruthOverlayLineKind.SelectedGroundTruth => Brushes.White,
                            _ => Brushes.White
                        },
                        StrokeThickness = 4
                    });
                }
                var sceneLines = groundTruthLabels.Document.Lines.Where(line =>
                    string.Equals(line.SceneName, transform.SceneName, StringComparison.Ordinal));
                GroundCountText.Text = $"+{sceneLines.Count(line => line.Kind == GroundTruthLineKind.Positive)
                    + legacyGroundEdits.Count(line => !line.IsNegative)}  "
                    + $"−{sceneLines.Count(line => line.Kind == GroundTruthLineKind.Negative)
                    + legacyGroundEdits.Count(line => line.IsNegative)}";
            }
            else GroundCountText.Text = $"+{legacyGroundEdits.Count(line => !line.IsNegative)}  "
                + $"−{legacyGroundEdits.Count(line => line.IsNegative)}";
            GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
            HackerGroundToolbar.ToolTip = groundTruthLabels.Error;
        }
        if (hackerInteractionMode == "Rooms")
        {
            var topology = BuildHackerRoomLayout();
            foreach (var room in topology.Rooms.Values)
            {
                var position = HackerRoomPosition(room, topology.Edits, topology.Layout);
                var bounds = room.Bounds.Offset(position.X, position.Y);
                var roomLeft = bounds.MinX;
                var roomTop = -bounds.MaxY;
                var rectangle = new Rectangle
                {
                    Width = Math.Max(1, bounds.MaxX - bounds.MinX),
                    Height = Math.Max(1, bounds.MaxY - bounds.MinY),
                    Stroke = Brushes.Violet, StrokeThickness = 3,
                    Fill = new SolidColorBrush(Color.FromArgb(24, 238, 130, 238)),
                    ToolTip = room.SceneName
                };
                Canvas.SetLeft(rectangle, roomLeft - originX);
                Canvas.SetTop(rectangle, roomTop - originY);
                HackerOverlayCanvas.Children.Add(rectangle);
                var label = new TextBlock
                {
                    Text = room.SceneName, Foreground = Brushes.Violet,
                    Background = Brushes.Black, Padding = new Thickness(3)
                };
                Canvas.SetLeft(label, roomLeft - originX + 4);
                Canvas.SetTop(label, roomTop - originY + 4);
                HackerOverlayCanvas.Children.Add(label);
            }
            foreach (var connection in topology.Connections)
            {
                var fromRoom = topology.Rooms[connection.FromRoomId];
                var toRoom = topology.Rooms[connection.ToRoomId];
                var from = HackerRoomEndpoint(
                    fromRoom,
                    HackerRoomPosition(fromRoom, topology.Edits, topology.Layout),
                    connection.FromSide,
                    connection.FromCoordinate);
                var to = HackerRoomEndpoint(
                    toRoom,
                    HackerRoomPosition(toRoom, topology.Edits, topology.Layout),
                    connection.ToSide,
                    connection.ToCoordinate);
                var freeform = topology.Layout.FreeformConnectionIds.Contains(connection.Id);
                HackerOverlayCanvas.Children.Add(new Line
                {
                    X1 = from.X - originX,
                    Y1 = -from.Y - originY,
                    X2 = to.X - originX,
                    Y2 = -to.Y - originY,
                    Stroke = freeform ? Brushes.OrangeRed : Brushes.Gold,
                    StrokeThickness = 2,
                    StrokeDashArray = freeform ? new DoubleCollection { 4, 3 } : null
                });
            }
        }
    }

    private void PersistHackerEdits()
    {
        if (!canPersistHackerEdits) return;
        var directory = IOPath.GetDirectoryName(hackerEditPath)!;
        Directory.CreateDirectory(directory);
        var temporary = IOPath.Combine(
            directory,
            $".{IOPath.GetFileName(hackerEditPath)}.tmp-{Guid.NewGuid():N}");
        var created = false;
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(
                new HackerEditDocument(
                    legacyGroundEdits,
                    rooms.Values.ToArray(),
                    legacyGroundMigrationVersion,
                    roomConnections.ToArray()),
                new JsonSerializerOptions { WriteIndented = true });
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                created = true;
                output.Write(data);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, hackerEditPath, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void LoadHackerEdits()
    {
        if (!File.Exists(hackerEditPath))
        {
            canPersistHackerEdits = true;
            return;
        }
        try
        {
            var backupPath = IOPath.Combine(
                IOPath.GetDirectoryName(hackerEditPath)!,
                "hacker-edits.legacy-v1-backup.json");
            if (!File.Exists(backupPath)) File.Copy(hackerEditPath, backupPath);
            var document = JsonSerializer.Deserialize<HackerEditDocument>(
                File.ReadAllBytes(hackerEditPath));
            if (document is null) return;
            legacyGroundEdits = document.Ground?.ToArray() ?? [];
            legacyGroundMigrationVersion = document.GroundMigrationVersion;
            foreach (var room in document.Rooms ?? []) rooms[room.SceneName] = room;
            roomConnections.Clear();
            roomConnections.AddRange((document.Connections ?? []).Where(connection =>
                connection.Id != Guid.Empty
                && !string.IsNullOrWhiteSpace(connection.FromSceneName)
                && !string.IsNullOrWhiteSpace(connection.ToSceneName)
                && !string.Equals(
                    connection.FromSceneName,
                    connection.ToSceneName,
                    StringComparison.Ordinal)
                && double.IsFinite(connection.FromCoordinate)
                && double.IsFinite(connection.ToCoordinate)
                && Enum.IsDefined(connection.FromSide)
                && Enum.IsDefined(connection.ToSide)));
            EnsureDistinctHackerRoomSeeds();
            canPersistHackerEdits = true;
        }
        catch (Exception error) when (error is JsonException or IOException
            or UnauthorizedAccessException)
        {
            canPersistHackerEdits = false;
            SetStatus($"Legacy Hacker edits were preserved without migration: {error.Message}");
        }
    }

    private void EnsureDistinctHackerRoomSeeds()
    {
        var occupied = new List<HackerAtlasRect>();
        foreach (var room in rooms.Values.OrderBy(room => room.SceneName, StringComparer.Ordinal))
        {
            if (!double.IsFinite(room.LayoutX)) room.LayoutX = 0;
            if (!double.IsFinite(room.LayoutY)) room.LayoutY = 0;
            var bounds = new HackerAtlasRect(
                room.Left + room.LayoutX,
                -room.Bottom + room.LayoutY,
                Math.Max(1, room.Right - room.Left),
                Math.Max(1, room.Bottom - room.Top));
            if (occupied.Any(existing => bounds.Intersects(existing)))
            {
                var nextLeft = occupied.Max(existing => existing.MaxX)
                    + HackerRoomLayout.RoomGap;
                room.LayoutX += nextLeft - bounds.MinX;
                bounds = bounds.Offset(nextLeft - bounds.MinX, 0);
            }
            occupied.Add(bounds);
        }
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
        RefreshTrainingNotification();
        RefreshModelReviewSession();
        trainedGameplayModelReady = HasTrainedGameplayModel();
        SetStatus($"Loaded {examples.Count} labeling examples.");
        if (ModelSummaryPanel is not null && workspace == "Model") RefreshModelWorkspace();
    }

    private void RefreshModelWorkspace()
    {
        RefreshModelReviewSession();
        RefreshTrainingNotification();
        RefreshObjectReferences();
        RefreshModelSummary();
        RefreshScreenshotReview();
    }

    private void CaptureFrame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var frozen = labelCapture.FrozenFrame
                ?? throw new InvalidOperationException("Wait for Hollow Knight's label pause before capturing a frame.");
            var bitmap = FrameBitmap(frozen);
            ApplyLabelSelection(() =>
            {
                CancelLabelGesture();
                currentFrame = frozen;
                currentExample = null;
                captureGroupIdentifier = Guid.NewGuid();
                boxes.Clear();
                labelUndo.Clear();
                KnownClassesText.Text = "";
                ExamplesList.SelectedItem = null;
                LabelImage.Source = bitmap;
                ResetLabelViewport();
                RedrawBoxes();
            });
            SetStatus($"Captured unsaved {frozen.Width}×{frozen.Height} frame. Drag boxes to label.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void ExamplesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (labelSelectionUpdate.IsUpdating) return;
        if (ExamplesList.SelectedItem is not SavedLabelingExample selected) return;
        OpenLabelExample(selected);
    }

    private void OpenLabelExample(SavedLabelingExample selected)
    {
        try { ApplySavedLabelExample(selected); }
        catch (Exception error) { ShowError(error); }
    }

    private void ApplySavedLabelExample(SavedLabelingExample selected, string? preferredClassIdentifier = null)
    {
        var changingImage = currentExample?.Id != selected.Id || LabelImage.Source is null;
        var state = LabelingFrameOpeningPolicy.StateForOpening(selected.Manifest,
            preferredClassIdentifier ?? (changingImage ? null : ClassIdentifierText.Text), customObjectChoices.Select(item => item.Identifier).ToArray())
            ?? throw new InvalidOperationException($"Unsupported saved context: {selected.Manifest.ContextIdentifier}");
        var bitmap = changingImage ? LoadBitmap(selected.ImagePath) : LabelImage.Source;
        ApplyLabelSelection(() =>
        {
            if (changingImage) CancelLabelGesture();
            currentExample = selected;
            currentFrame = null;
            captureGroupIdentifier = selected.Manifest.CaptureGroupIdentifier;
            ContextCombo.SelectedValue = ContextDisplayName(state.ContextIdentifier);
            KnownClassesText.Text = string.Join(Environment.NewLine,
                selected.Manifest.EffectiveKnownClassIdentifiers);
            boxes.Clear();
            if (changingImage) labelUndo.Clear();
            foreach (var annotation in state.Rectangles)
            {
                boxes.Add(EditableBox.From(annotation));
            }
            LabelImage.Source = bitmap;
            // Autosaving reselects this same example; keep the user's zoom.
            if (changingImage) { ResetLabelViewport(); LabelAddButton.IsChecked = true; }
            ClassIdentifierText.Text = state.SelectedClassIdentifier;
            RedrawBoxes();
        });
        SetStatus($"Editing {selected.Manifest.Id:D}.");
    }

    private void SaveLabels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var context = StencilInitializationPolicy.NormalizeContext(CurrentContextName);
            if (context.Length == 0) throw new InvalidOperationException("Choose a context.");
            var annotations = boxes.Select(box => box.ToAnnotation()).ToArray();
            var store = new LabelingExampleStore(ExamplesRootText.Text);
            currentExample = currentExample is null
                ? store.Save(
                    currentFrame ?? throw new InvalidOperationException(
                        "Capture a frame or select an example first."),
                    context,
                    annotations,
                    captureGroupIdentifier: captureGroupIdentifier,
                    languageIdentifier: initialization.LanguageIdentifier)
                : store.Update(currentExample, context, annotations);
            SetStatus($"Saved label example {currentExample.Id:D}.");
            labelSelectionUpdate.Run(() =>
            {
                RefreshExamples();
                ExamplesList.SelectedItem = examples.FirstOrDefault(example => example.Id == currentExample.Id);
            });
            menuStencilRecognizer.Refresh(initialization.LanguageIdentifier);
            RefreshInitializationStatus();
            RefreshClassChoices();

        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private static string ContextDisplayName(string identifier) =>
        StencilInitializationPolicy.ContextDisplayName(identifier);

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
            ResetLabelViewport();
            boxes.Clear();
            labelDraft.Reset();
            RefreshExamples();
            menuStencilRecognizer.Refresh(initialization.LanguageIdentifier);
            RefreshInitializationStatus();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void LabelCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (LabelImage.Source is null) return;
        var point = Clamp(e.GetPosition(LabelCanvas));
        var normalizedPoint = NormalizeLabelPoint(point);
        var mode = CurrentLabelingMode;
        if (mode == LabelingEditorMode.Negative
            && labelDraft.MarkNegative(boxes, normalizedPoint) is not null)
        {
            RedrawBoxes();
            PersistLabelDraft();
            return;
        }
        if (mode == LabelingEditorMode.Delete)
        {
            var selected = labelDraft.Select(boxes, normalizedPoint);
            if (selected is null) return;
            labelDraft.DeleteSelected(boxes);
            RedrawBoxes();
            PersistLabelDraft();
            return;
        }
        if (mode == LabelingEditorMode.Modify)
        {
            var corner = HitLabelResizeHandle(point);
            var selected = corner is not null && labelDraft.SelectedId is { } selectedId
                ? boxes.FirstOrDefault(box => box.Id == selectedId)
                : labelDraft.Select(boxes, normalizedPoint,
                    LabelingClassIdentity.CanonicalIdentifier(ClassIdentifierText.Text.Trim()));
            if (selected is null || !labelDraft.BeginModification(boxes, selected.Id)) return;
            modifyingBox = selected;
            modifyOriginal = selected;
            modifyDragStart = point;
            modifyingCorner = corner;
            SyncLabelSelection();
            RedrawBoxes();
            LabelCanvas.CaptureMouse();
            return;
        }
        if (string.IsNullOrWhiteSpace(ClassIdentifierText.Text))
        {
            SetStatus("Enter a class identifier before drawing.");
            return;
        }
        if (mode == LabelingEditorMode.Add && !AvailableClassChoices(CurrentContextName)
            .Any(item => item.Identifier == ClassIdentifierText.Text)) return;
        dragStart = point;
        dragPreview = new Rectangle
        {
            Stroke = Brushes.Gold,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 215, 0))
        };
        PositionLabelRectangle(dragPreview, point, point);
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
            var updatedRect = modifyingCorner is { } corner
                ? LabelingEditorPolicy.Resized(
                    modifyOriginal.NormalizedRect,
                    corner,
                    NormalizeLabelPoint(point))
                : LabelingEditorPolicy.Translated(
                    modifyOriginal.NormalizedRect,
                    new Vector(dx, dy));
            labelDraft.Update(boxes, modifyingBox.Id, updatedRect);
            modifyingBox = boxes.First(box => box.Id == modifyingBox.Id);
            RedrawBoxes();
            return;
        }
        if (dragStart is null || dragPreview is null) return;
        PositionLabelRectangle(dragPreview, dragStart.Value, Clamp(e.GetPosition(LabelCanvas)));
    }

    private void LabelCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (modifyingBox is not null)
        {
            var changedId = modifyingBox.Id;
            labelDraft.Finish(boxes, changedId);
            modifyingBox = null;
            modifyOriginal = null;
            modifyDragStart = null;
            modifyingCorner = null;
            LabelCanvas.ReleaseMouseCapture();
            RedrawBoxes();
            PersistLabelDraft();
            return;
        }
        if (dragStart is null || dragPreview is null) return;
        var start = Clamp(dragStart.Value);
        var end = Clamp(e.GetPosition(LabelCanvas));
        LabelCanvas.ReleaseMouseCapture();
        dragStart = null;
        dragPreview = null;
        var first = NormalizeLabelPoint(start);
        var second = NormalizeLabelPoint(end);
        var pending = labelDraft.BeginRectangle(
            boxes,
            LabelingClassIdentity.CanonicalIdentifier(ClassIdentifierText.Text.Trim()),
            first,
            HardNegativeCheck.IsChecked == true);
        labelDraft.Update(boxes, pending.Id, new Rect(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y)));
        var accepted = labelDraft.Finish(boxes, pending.Id);
        RedrawBoxes();
        if (accepted) PersistLabelDraft();
    }

    private void PersistLabelDraft()
    {
        if (!LabelingEditorPolicy.ShouldPersist(boxes.Count, currentExample is not null)) return;
        SaveLabels_Click(this, new RoutedEventArgs());
    }

    private void RemoveBox_Click(object sender, RoutedEventArgs e)
    {
        if (BoxesList.SelectedItem is EditableBox selected)
        {
            labelDraft.Select(boxes, selected.Id);
            labelDraft.DeleteSelected(boxes);
        }
        RedrawBoxes();
        PersistLabelDraft();
    }

    private void UndoBox_Click(object sender, RoutedEventArgs e)
    {
        UndoLabelEdit();
    }

    private void ClearBoxes_Click(object sender, RoutedEventArgs e)
    {
        labelDraft.Clear(boxes);
        RedrawBoxes();
        PersistLabelDraft();
    }

    private void UndoLabelEdit()
    {
        if (!labelDraft.Undo(boxes)) return;
        RedrawBoxes();
        PersistLabelDraft();
    }

    private LabelingEditorMode CurrentLabelingMode =>
        LabelModifyButton.IsChecked == true ? LabelingEditorMode.Modify
        : LabelDeleteButton.IsChecked == true ? LabelingEditorMode.Delete
        : LabelNegativeButton.IsChecked == true ? LabelingEditorMode.Negative
        : LabelingEditorMode.Add;

    private static Point NormalizeLabelPoint(Point point) =>
        new(point.X / CanvasWidth, point.Y / CanvasHeight);

    private LabelingResizeCorner? HitLabelResizeHandle(Point point)
    {
        if (labelDraft.SelectedId is not { } id) return null;
        var selected = boxes.FirstOrDefault(box => box.Id == id);
        if (selected is null) return null;
        var displayScale = LabelBorderWidth > 0 ? 2 / LabelBorderWidth : 1;
        var markerSize = LabelingEditorPolicy.ControlPointSize / Math.Max(displayScale, .0001);
        var canvasRect = new Rect(
            selected.X * CanvasWidth,
            selected.Y * CanvasHeight,
            selected.Width * CanvasWidth,
            selected.Height * CanvasHeight);
        foreach (var marker in LabelingEditorPolicy.ControlPoints(canvasRect, markerSize))
            if (marker.Value.Contains(point)) return marker.Key;
        return null;
    }

    private void SyncLabelSelection()
    {
        BoxesList.SelectedItem = labelDraft.SelectedId is { } id
            ? boxes.FirstOrDefault(box => box.Id == id)
            : null;
    }

    private void RedrawBoxes()
    {
        LabelCanvas.Children.Clear();
        foreach (var box in boxes)
        {
            var rectangle = new Rectangle
            {
                Stroke = box.IsNegative ? Brushes.OrangeRed : Brushes.DeepSkyBlue,
                Fill = new SolidColorBrush(box.IsNegative
                    ? Color.FromArgb(35, 255, 69, 0)
                    : Color.FromArgb(30, 0, 191, 255)),
                ToolTip = box.ToString()
            };
            PositionLabelRectangle(rectangle,
                new Point(box.X * CanvasWidth, box.Y * CanvasHeight),
                new Point((box.X + box.Width) * CanvasWidth, (box.Y + box.Height) * CanvasHeight));
            LabelCanvas.Children.Add(rectangle);
            var identifier = box.ClassIdentifier;
            var definition = StencilInitializationPolicy.AllContexts.SelectMany(item => item.Requirements)
                .FirstOrDefault(item => item.Identifier == identifier);
            var name = definition?.Name ?? customObjectChoices.FirstOrDefault(item => item.Identifier == identifier)?.Name ?? identifier;
            if (identifier == "shared.select-decoration")
                name = boxes.Where(item => item.ClassIdentifier == identifier && !item.IsNegative)
                    .OrderBy(item => item.X).FirstOrDefault() == box ? "Left Select Decoration" : "Right Select Decoration";
            // Captions live inside the zoomed 640x360 canvas. Their screen-space
            // metrics are refreshed with the viewport so they match the compact
            // Mac presentation at every window size and labeling zoom level.
            var caption = new Border
            {
                IsHitTestVisible = false,
                Tag = box,
                Child = new TextBlock
                {
                    Text = initialization.Text(name),
                    Foreground = Brushes.White
                }
            };
            ApplyLabelCaption(caption, box);
            LabelCanvas.Children.Add(caption);
        }
        SyncLabelSelection();
        if (CurrentLabelingMode == LabelingEditorMode.Modify
            && labelDraft.SelectedId is { } selectedId
            && boxes.FirstOrDefault(box => box.Id == selectedId) is { } selected)
        {
            var displayScale = LabelBorderWidth > 0 ? 2 / LabelBorderWidth : 1;
            var markerSize = LabelingEditorPolicy.ControlPointSize / Math.Max(displayScale, .0001);
            var canvasRect = new Rect(
                selected.X * CanvasWidth,
                selected.Y * CanvasHeight,
                selected.Width * CanvasWidth,
                selected.Height * CanvasHeight);
            foreach (var marker in LabelingEditorPolicy.ControlPoints(canvasRect, markerSize))
            {
                var handle = new Rectangle
                {
                    Width = marker.Value.Width,
                    Height = marker.Value.Height,
                    Fill = Brushes.White,
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = Math.Max(.5, LabelBorderWidth / 2),
                    Cursor = marker.Key is LabelingResizeCorner.TopLeft or LabelingResizeCorner.BottomRight
                        ? Cursors.SizeNWSE : Cursors.SizeNESW,
                    Tag = marker.Key,
                    ToolTip = $"Resize {marker.Key}"
                };
                Canvas.SetLeft(handle, marker.Value.X);
                Canvas.SetTop(handle, marker.Value.Y);
                LabelCanvas.Children.Add(handle);
            }
        }
    }

    private void ExportDataset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var classes = ParseIdentifiers(ExportClassesText.Text);
            if (classes.Count == 0) throw new InvalidOperationException(
                "Enter at least one included class identifier.");
            var snapshot = ExportSharedTrainingDataset(ModelExamplesRootText.Text, DatasetRootText.Text, classes);
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
            ? initialization.Text("Loading player state…") : initialization.Text("Updated game receiver is not ready.");
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
        OpsStatusText.Text = initialization.Text("Applying player state…");
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
        OpsStatusText.Text = initialization.Text("Randomizing player state…");
        var acknowledgement = await gameControls.PerformPlayerOpsAsync(
            session => ReceiverPlayerOpsCommand.Apply(session, state));
        ApplyPlayerOpsAcknowledgement(acknowledgement);
    }

    private async void OpsRestoreEnemies_Click(object sender, RoutedEventArgs e)
    {
        OpsStatusText.Text = initialization.Text("Restoring enemies…");
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
                OpsStatusText.Text = initialization.Text("Enter whole numbers for player state.");
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
            OpsStatusText.Text = initialization.Text("Updated game receiver is not ready.");
            return;
        }
        if (!acknowledgement.Accepted)
        {
            OpsStatusText.Text = acknowledgement.Failure ?? initialization.Text("Player operation failed.");
            return;
        }
        if (acknowledgement.State is not null) SetPlayerTestState(acknowledgement.State.Normalized());
        OpsStatusText.Text = acknowledgement.EnemiesRestored is not null
            ? initialization.Text("Enemies restored.") : initialization.Text("Player state updated.");
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
        BitmapSource bitmap,
        string? menuContext)
    {
        var usesAtlasCoordinates = GameplayViewportPolicy.UsesAtlasCoordinates(
            menuContext, gameplayFrameRegistered);
        gameplayFrameRegistered = usesAtlasCoordinates;
        gameplayLiveFrame = frame;
        if (menuContext == "game" || gameplayLiveTelemetry is null) gameplayLiveTelemetry = telemetry;
        GameplayImage.Source = bitmap;
        if (usesAtlasCoordinates)
        {
            PositionLiveFrame(
                GameplayAtlasCanvas, GameplayImage, DetectionCanvas,
                gameplayAtlas.PixelsPerWorldUnit, gameplayAtlas.TileSize,
                gameplayAtlasMinimumTileX, gameplayAtlasMinimumTileY,
                frame, gameplayLiveTelemetry);
        }
        else
        {
            PositionUnregisteredGameplayFrame(frame);
        }
        ApplyViewport(
            GameplayAtlasCanvas,
            GameplayPanel,
            gameplayFraming,
            GameplayImage);
    }

    private void PositionUnregisteredGameplayFrame(BgraFrame frame)
    {
        GameplayImage.Width = frame.Width;
        GameplayImage.Height = frame.Height;
        Canvas.SetLeft(GameplayImage, 0);
        Canvas.SetTop(GameplayImage, 0);
        DetectionCanvas.Width = CanvasWidth;
        DetectionCanvas.Height = CanvasHeight;
        Canvas.SetLeft(DetectionCanvas, 0);
        Canvas.SetTop(DetectionCanvas, 0);
        DetectionCanvas.RenderTransform = new ScaleTransform(
            frame.Width / CanvasWidth,
            frame.Height / CanvasHeight);
        DetectionCanvas.RenderTransformOrigin = new Point(0, 0);
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
        var occupiedBounds = gameplayOccupiedTileBounds;
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
            var occupied = OccupiedBounds(tile.Frame, tile.Coordinate, mosaic.TileSize);
            if (occupied is { } bounds) occupiedBounds[tile.Coordinate] = bounds;
            else occupiedBounds.Remove(tile.Coordinate);
        }
        if (images.Count == 0) return;
        var previousMinimumX = minimumTileX ?? 0;
        var previousMinimumY = minimumTileY ?? 0;
        minimumTileX = images.Keys.Min(coordinate => coordinate.X);
        minimumTileY = images.Keys.Min(coordinate => coordinate.Y);
        // Adding tiles to the left/top changes the canvas origin, not the
        // user's camera. Keep a manually fitted/panned scene stationary.
        if (minimumTileX.Value != previousMinimumX || minimumTileY.Value != previousMinimumY)
        {
            var originShift = new Vector(
                (minimumTileX.Value - previousMinimumX) * mosaic.TileSize,
                (minimumTileY.Value - previousMinimumY) * mosaic.TileSize);
            ApplySettledViewportTransform(canvas, ViewportController(canvas).Rebase(
                originShift.X, originShift.Y));
            if (viewportPanStart is not null && ReferenceEquals(canvas,
                    workspace == "Gameplay" ? GameplayAtlasCanvas : AtlasCanvas))
            {
                viewportPanMatrix.OffsetX += originShift.X * viewportPanMatrix.M11;
                viewportPanMatrix.OffsetY += originShift.Y * viewportPanMatrix.M22;
            }
        }
        var maximumTileX = images.Keys.Max(coordinate => coordinate.X);
        var maximumTileY = images.Keys.Max(coordinate => coordinate.Y);
        canvas.Width = (maximumTileX - minimumTileX.Value + 1) * mosaic.TileSize;
        canvas.Height = (maximumTileY - minimumTileY.Value + 1) * mosaic.TileSize;
        foreach (var pair in images)
        {
            Canvas.SetLeft(pair.Value, (pair.Key.X - minimumTileX.Value) * mosaic.TileSize);
            Canvas.SetTop(pair.Value, (pair.Key.Y - minimumTileY.Value) * mosaic.TileSize);
        }
        GameplayGroundCanvas.Width = canvas.Width;
        GameplayGroundCanvas.Height = canvas.Height;
        DrawTrackedGround();
        if (liveFrame is not null && telemetry is not null)
        {
            PositionLiveFrame(
                canvas, liveImage, DetectionCanvas,
                mosaic.PixelsPerWorldUnit, mosaic.TileSize,
                minimumTileX, minimumTileY, liveFrame, telemetry);
        }
        ApplyViewport(canvas, GameplayPanel, gameplayFraming, liveImage);
    }

    private static void PositionLiveFrame(
        Canvas canvas,
        Image liveImage,
        Canvas? detectionCanvas,
        double atlasPixelsPerWorldUnit,
        int atlasTileSize,
        int? minimumTileX,
        int? minimumTileY,
        BgraFrame frame,
        ReceiverGroundTruthSample telemetry,
        double offsetX = 0,
        double offsetY = 0)
    {
        var sourceScale = telemetry.PixelsPerWorldUnit(frame.Height);
        if (sourceScale is not > 0 || !double.IsFinite(sourceScale.Value)) return;
        var halfWorldWidth = frame.Width / (2 * sourceScale.Value);
        var halfWorldHeight = frame.Height / (2 * sourceScale.Value);
        var globalLeft = (telemetry.CameraX - halfWorldWidth) * atlasPixelsPerWorldUnit + offsetX;
        var globalTop = -(telemetry.CameraY + halfWorldHeight) * atlasPixelsPerWorldUnit + offsetY;
        var originX = (minimumTileX ?? 0) * atlasTileSize;
        var originY = (minimumTileY ?? 0) * atlasTileSize;
        var width = frame.Width / sourceScale.Value * atlasPixelsPerWorldUnit;
        var height = frame.Height / sourceScale.Value * atlasPixelsPerWorldUnit;
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

    private void ApplyViewport(
        Canvas surface,
        FrameworkElement host,
        string mode,
        FrameworkElement liveFrame,
        bool explicitRequest = false)
    {
        if (host.ActualWidth <= 1 || host.ActualHeight <= 1) return;
        var framing = mode is "Free Fly" or "Current" ? mode : "Fit";
        var follow = LiveFrameBounds(liveFrame);
        ViewportPoint? focus = follow is { } live
            ? new ViewportPoint(live.CenterX, live.CenterY) : null;
        var controller = ViewportController(surface);
        var transform = controller.Configure(
            framing,
            new ViewportSize(host.ActualWidth, host.ActualHeight),
            ViewportFitBounds(surface, liveFrame),
            follow,
            focus,
            ViewportTimestamp(),
            explicitRequest);
        ApplySettledViewportTransform(surface, transform);
    }

    private GameplayViewportController ViewportController(Canvas surface) =>
        ReferenceEquals(surface, GameplayAtlasCanvas) ? gameplayViewport : hackerViewport;

    private IEnumerable<ViewportRect> OccupiedTileBounds(Canvas surface) =>
        ReferenceEquals(surface, GameplayAtlasCanvas)
            ? gameplayOccupiedTileBounds.Values : hackerOccupiedTileBounds.Values;

    private static double ViewportTimestamp() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static ViewportRect? LiveFrameBounds(FrameworkElement liveFrame)
    {
        if (!liveFrame.IsVisible || !double.IsFinite(liveFrame.Width)
            || !double.IsFinite(liveFrame.Height)
            || liveFrame.Width <= 0 || liveFrame.Height <= 0) return null;
        var left = Canvas.GetLeft(liveFrame);
        var top = Canvas.GetTop(liveFrame);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        var bounds = new ViewportRect(left, top, liveFrame.Width, liveFrame.Height);
        return bounds.IsUsable ? bounds : null;
    }

    private ViewportRect? ViewportFitBounds(Canvas surface, FrameworkElement liveFrame)
    {
        var liveBounds = LiveFrameBounds(liveFrame);
        if (ReferenceEquals(surface, GameplayAtlasCanvas) && !gameplayFrameRegistered)
            return liveBounds;
        var minimumX = ReferenceEquals(surface, GameplayAtlasCanvas)
            ? gameplayAtlasMinimumTileX : hackerAtlasMinimumTileX;
        var minimumY = ReferenceEquals(surface, GameplayAtlasCanvas)
            ? gameplayAtlasMinimumTileY : hackerAtlasMinimumTileY;
        var tileSize = ReferenceEquals(surface, GameplayAtlasCanvas)
            ? gameplayAtlas.TileSize : HackerAtlasTileSize;
        var originX = (minimumX ?? 0) * (double)tileSize;
        var originY = (minimumY ?? 0) * (double)tileSize;
        ViewportRect? union = null;
        foreach (var global in OccupiedTileBounds(surface))
        {
            var local = new ViewportRect(
                global.X - originX, global.Y - originY,
                global.Width, global.Height);
            union = union is { } current ? current.Union(local) : local;
        }
        if (liveBounds is { } live)
            union = union is { } current ? current.Union(live) : live;
        return union;
    }

    private static ViewportRect? OccupiedBounds(
        BgraFrame frame,
        AtlasTileCoordinate coordinate,
        int tileSize)
    {
        var minimumX = frame.Width;
        var minimumY = frame.Height;
        var maximumX = -1;
        var maximumY = -1;
        for (var y = 0; y < frame.Height; y++)
        {
            var row = frame.Row(y);
            for (var x = 0; x < frame.Width; x++)
            {
                if (row[x * BgraFrame.BytesPerPixel + 3] == 0) continue;
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }
        if (maximumX < minimumX || maximumY < minimumY) return null;
        return new ViewportRect(
            coordinate.X * (double)tileSize + minimumX,
            coordinate.Y * (double)tileSize + minimumY,
            maximumX - minimumX + 1,
            maximumY - minimumY + 1);
    }

    private static ViewportRect? OccupiedBounds(
        BitmapSource bitmap,
        (int X, int Y) coordinate,
        int tileSize)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(source.PixelWidth * BgraFrame.BytesPerPixel);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        return OccupiedBounds(
            new BgraFrame(source.PixelWidth, source.PixelHeight, stride, pixels,
                0, DateTimeOffset.UtcNow),
            new AtlasTileCoordinate(coordinate.X, coordinate.Y),
            tileSize);
    }

    private void ApplySettledViewportTransform(Canvas surface, ViewportTransform transform)
    {
        if (!transform.IsUsable) return;
        if (!ReferenceEquals(surface, GameplayAtlasCanvas))
        {
            ApplyViewportTransform(surface, transform);
            return;
        }

        var atlasPan = gameplayViewport.AtlasPan(ViewportTimestamp());
        var atlasTransform = transform with
        {
            OffsetX = atlasPan.X,
            OffsetY = atlasPan.Y
        };
        ApplyViewportTransform(surface, atlasTransform);

        // The atlas eases into a new basis while the live capture remains at
        // the authoritative pose. Counter-translate only the live layers.
        var localX = (transform.OffsetX - atlasTransform.OffsetX) / transform.Scale;
        var localY = (transform.OffsetY - atlasTransform.OffsetY) / transform.Scale;
        GameplayImage.RenderTransformOrigin = new Point(0, 0);
        GameplayImage.RenderTransform = new TranslateTransform(localX, localY);
        DetectionCanvas.RenderTransformOrigin = new Point(0, 0);
        DetectionCanvas.RenderTransform = new MatrixTransform(new Matrix(
            GameplayImage.Width / CanvasWidth,
            0,
            0,
            GameplayImage.Height / CanvasHeight,
            localX,
            localY));
    }

    private static void ApplyViewportTransform(
        Canvas surface,
        ViewportTransform transform) =>
        surface.RenderTransform = new MatrixTransform(new Matrix(
            transform.Scale, 0, 0, transform.Scale,
            transform.OffsetX, transform.OffsetY));

    private void AdvanceViewportAnimations()
    {
        var timestamp = ViewportTimestamp();
        if (gameplayViewport.IsAnimating)
            ApplySettledViewportTransform(GameplayAtlasCanvas, gameplayViewport.Advance(timestamp));
        if (hackerViewport.IsAnimating)
            ApplySettledViewportTransform(AtlasCanvas, hackerViewport.Advance(timestamp));
    }

    private void PersistActiveAtlas(bool force)
    {
        if (!force && activeAtlasPersistenceBlocked
            && atlasPersistTask is { IsFaulted: true }) return;
        try
        {
            if (atlasPersistTask is { } pending)
            {
                if (!pending.IsCompleted && !force) return;
                CompleteAtlasPersistence();
            }
            if (!QueueAtlasPersistence(force)) return;
            if (force) CompleteAtlasPersistence();
        }
        catch (Exception error) when (!force)
        {
            activeAtlasPersistenceBlocked = true;
            SetStatus($"Auto Save stopped after a disk write failed: {error.Message}");
        }
    }

    private bool QueueAtlasPersistence(bool force)
    {
        if (atlasPersistTask is not null)
            throw new InvalidOperationException("An atlas publication is already pending.");
        if (activeAtlasPersistenceBlocked || gameplayAtlas.TileCount == 0) return false;
        var now = DateTimeOffset.UtcNow;
        var fullReplacement = Volatile.Read(ref gameplayAtlasRequiresFullRewrite);
        if (!force && !fullReplacement
            && now - lastAtlasPersistedAt < TimeSpan.FromSeconds(10)) return false;
        atlasPersistTask = worldWorker.Exclusive(() =>
        {
            var atlasAtWrite = gameplayAtlas;
            var snapshot = atlasAtWrite.SnapshotSince(-1);
            var metadata = new VisionResetController.PersistedAtlasMetadata(
                atlasAtWrite.AcceptedFrameCount, activeAtlasCreatedAt, atlasAtWrite.TileSize, atlasAtWrite.PixelsPerWorldUnit);
            var saveStarted = Stopwatch.GetTimestamp();
            _ = WriteAtlasGeneration(
                snapshot,
                metadata,
                copyActiveWorld: true,
                retainPrevious: false);
            return new AtlasPersistencePublication(
                atlasAtWrite,
                snapshot.Revision,
                FullReplacement: true,
                DateTimeOffset.UtcNow,
                Stopwatch.GetElapsedTime(saveStarted).TotalMilliseconds);
        });
        return true;
    }

    private void CompleteAtlasPersistence()
    {
        var pending = atlasPersistTask;
        if (pending is null) return;
        AtlasPersistencePublication publication;
        try
        {
            publication = pending.GetAwaiter().GetResult();
        }
        catch
        {
            activeAtlasPersistenceBlocked = true;
            throw;
        }
        ApplyAtlasPersistence(publication);
        atlasPersistTask = null;
    }

    private async Task AwaitAtlasPersistenceAsync()
    {
        var pending = atlasPersistTask;
        if (pending is null) return;
        AtlasPersistencePublication publication;
        try
        {
            publication = await pending;
        }
        catch
        {
            activeAtlasPersistenceBlocked = true;
            throw;
        }
        ApplyAtlasPersistence(publication);
        if (ReferenceEquals(atlasPersistTask, pending)) atlasPersistTask = null;
    }

    private async Task FlushActiveAtlasAsync()
    {
        await AwaitAtlasPersistenceAsync();
        if (activeAtlasPersistenceBlocked) return;
        _ = QueueAtlasPersistence(force: true);
        await AwaitAtlasPersistenceAsync();
    }

    private void ApplyAtlasPersistence(AtlasPersistencePublication publication)
    {
        if (!ReferenceEquals(gameplayAtlas, publication.Atlas)) return;
        Interlocked.Exchange(ref gameplayPersistedRevision, publication.Revision);
        lastAtlasPersistedAt = publication.CompletedAt;
        lastAtlasSaveMilliseconds = publication.DurationMilliseconds;
        if (publication.FullReplacement
            && publication.Atlas.Revision == publication.Revision)
        {
            Volatile.Write(ref gameplayAtlasRequiresFullRewrite, false);
        }
    }

    private VisionResetController.ActiveAtlasReplacement<
        VisionResetController.ValidatedPersistedAtlas> WriteAtlasGeneration(
        AtlasMosaicDelta snapshot,
        VisionResetController.PersistedAtlasMetadata metadata,
        bool copyActiveWorld,
        bool retainPrevious,
        Action<string>? activateWorld = null)
    {
        return VisionResetController.PublishActiveAtlasGeneration(
            atlasStateStore,
            activeAtlasDirectory,
            copyActiveWorld,
            retainPrevious,
            staging =>
            {
                foreach (var staleTile in Directory.EnumerateFiles(
                             staging,
                             "tile_*.bmp",
                             SearchOption.TopDirectoryOnly))
                {
                    File.Delete(staleTile);
                }
                foreach (var tile in snapshot.Tiles)
                {
                    SaveBitmap(
                        tile.Frame,
                        IOPath.Combine(staging,
                            $"tile_{tile.Coordinate.X}_{tile.Coordinate.Y}.bmp"));
                }
                var encoded = JsonSerializer.SerializeToUtf8Bytes(
                    metadata,
                    new JsonSerializerOptions { WriteIndented = true });
                using (var stream = new FileStream(
                           IOPath.Combine(staging, "atlas.json"),
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(encoded);
                    stream.Flush(flushToDisk: true);
                }
                if (!copyActiveWorld)
                {
                    _ = new LiveWorldSessionStore(
                        IOPath.Combine(staging, "live-world")).Load();
                }
            },
            VisionResetController.ReadPersistedAtlas,
            activateWorld: activateWorld);
    }

    private async Task InitializePersistentWorldAsync()
    {
        var emptyMetadata = new VisionResetController.PersistedAtlasMetadata(
            0,
            activeAtlasCreatedAt,
            gameplayAtlas.TileSize,
            gameplayAtlas.PixelsPerWorldUnit);
        try
        {
            var initialized = await Task.Run(() =>
            {
                VisionResetController.RecoverInterruptedActiveAtlasPublication(
                    atlasStateStore,
                    activeAtlasDirectory,
                    VisionResetController.ReadPersistedAtlas);
                if (!Directory.Exists(activeAtlasDirectory))
                {
                    _ = WriteAtlasGeneration(
                        new AtlasMosaicDelta(gameplayAtlas.Revision, []),
                        emptyMetadata,
                        copyActiveWorld: false,
                        retainPrevious: false);
                }
                var restored = VisionResetController.ReadPersistedAtlas(
                    activeAtlasDirectory);
                var pipeline = new WindowsLiveWorldPipeline(
                    IOPath.Combine(activeAtlasDirectory, "live-world"));
                if (pipeline.LastError is { Length: > 0 } pipelineError)
                {
                    throw new InvalidDataException(pipelineError);
                }
                return (Restored: restored, Pipeline: pipeline);
            });
            liveWorldPipeline = initialized.Pipeline;
            PublishPersistentAtlas(
                initialized.Restored,
                explicitlyRequested: false,
                reopenLiveWorld: false);
        }
        catch (Exception error) when (error is InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            gameplayAtlas = new GroundTruthAtlasMosaic(
                pixelsPerWorldUnit: 1,
                sourceTopInsetFraction: 0);
            activeAtlasCreatedAt = DateTimeOffset.UtcNow;
            activeAtlasPersistenceBlocked = true;
            liveWorldPipeline = null;
            ClearGameplayAtlasRuntime(reopenLiveWorld: false);
            SetStatus(
                $"Auto Save atlas could not be loaded: {error.Message} "
                + "The on-disk atlas was left unchanged.");
        }
    }

    private void PublishPersistentAtlas(
        VisionResetController.ValidatedPersistedAtlas restored,
        bool explicitlyRequested,
        bool reopenLiveWorld = true)
    {
        gameplayAtlas = restored.Mosaic;
        activeAtlasCreatedAt = restored.Metadata.CreatedAt;
        activeAtlasPersistenceBlocked = false;
        Volatile.Write(ref gameplayAtlasRequiresFullRewrite, false);
        ClearGameplayAtlasRuntime(reopenLiveWorld);
        if (gameplayAtlas.TileCount > 0
            && GameplayOutputGate.CanShowRestoredAtlas(
                hasFreshGameFrame,
                explicitlyRequested))
        {
            GameplayPlaceholder.Visibility = Visibility.Collapsed;
            var delta = new AtlasMosaicDelta(gameplayAtlas.Revision, gameplayAtlas.Snapshot());
            RenderLiveAtlas(
                GameplayAtlasCanvas, gameplayTileImages, gameplayAtlas, delta,
                ref gameplayAtlasMinimumTileX, ref gameplayAtlasMinimumTileY,
                GameplayImage, gameplayLiveFrame, gameplayLiveTelemetry);
            gameplayRenderedRevision = delta.Revision;
        }
    }

    private async Task RefreshAtlasManagerAsync()
    {
        var acceptedFrameCount = gameplayAtlas.AcceptedFrameCount;
        var createdAt = activeAtlasCreatedAt;
        var items = await Task.Run(() =>
        {
            var collected = new List<AtlasManagerItem>();
            var age = DateTimeOffset.UtcNow - createdAt;
            var bytes = Directory.Exists(activeAtlasDirectory)
                ? AtlasStateStore.FileByteCount(activeAtlasDirectory) : 0;
            collected.Add(new AtlasManagerItem(
                null,
                "Auto Save",
                $"{acceptedFrameCount} registrations  •  {FormatBytes(bytes)}  •  {FormatAge(age)} old"));
            foreach (var state in atlasStateStore.List())
            {
                var world = atlasStateStore.WorldPath(state);
                collected.Add(new AtlasManagerItem(
                    state,
                    state.Name,
                    $"{FormatBytes(AtlasStateStore.FileByteCount(world))}  •  "
                    + $"{FormatAge(DateTimeOffset.UtcNow - state.CreatedAt)} old"));
            }
            return collected;
        });
        if (isClosing) return;
        AtlasStatesList.Items.Clear();
        foreach (var item in items) AtlasStatesList.Items.Add(item);
        AtlasStatesList.SelectedIndex = 0;
    }

    private void AtlasStatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = AtlasStatesList.SelectedItem as AtlasManagerItem;
        var saved = selected?.State is not null;
        AtlasDeleteStateButton.IsEnabled = selected is not null;
        AtlasDeleteStateButton.Content = initialization.Text("Delete");
        AtlasDeleteStateButton.ToolTip = saved ? null : "Delete the active atlas without saving a copy. Stencil labels and named saved states are kept.";
        AtlasLoadStateButton.IsEnabled = saved;
    }

    private async void AtlasSaveState_Click(object sender, RoutedEventArgs e)
    {
        var name = AtlasStateNameText.Text;
        await RunAtlasOperationAsync(async () =>
        {
            var saved = await Task.Run(() => atlasStateStore.Save(
                name,
                destination => VisionResetController.CopyValidatedAtlas(
                    activeAtlasDirectory,
                    destination)));
            AtlasStateNameText.Clear();
            SetStatus($"Saved immutable atlas state {saved.Name}.");
        });
    }

    private async void AtlasNew_Click(object sender, RoutedEventArgs e) => await ResetActiveAtlasAsync(retainPrevious: true);

    private async Task ResetActiveAtlasAsync(bool retainPrevious)
    {
        await RunAtlasOperationAsync(async () =>
        {
            var resetAt = DateTimeOffset.UtcNow;
            var metadata = new VisionResetController.PersistedAtlasMetadata(
                0,
                resetAt,
                gameplayAtlas.TileSize,
                gameplayAtlas.PixelsPerWorldUnit);
            var reset = await Task.Run(() =>
            {
                VisionResetController.ActiveAtlasReplacement<
                    VisionResetController.ValidatedPersistedAtlas> replacement;
                try
                {
                    replacement = WriteAtlasGeneration(
                        new AtlasMosaicDelta(gameplayAtlas.Revision, []),
                        metadata,
                        copyActiveWorld: false,
                        retainPrevious: retainPrevious,
                        activateWorld: _ => ReopenActiveLiveWorld());
                }
                catch (Exception replacementError)
                {
                    try
                    {
                        if (Directory.Exists(activeAtlasDirectory))
                            ReopenActiveLiveWorld();
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException(replacementError, rollbackError);
                    }
                    throw;
                }

                AtlasSavedState? backup = null;
                VisionResetController.PreviousActiveAtlasImportException? importError = null;
                if (replacement.ArchivedPreviousAtlasDirectory is { } archive)
                {
                    try
                    {
                        backup = VisionResetController.ImportPreviousActiveAtlas(
                            atlasStateStore,
                            archive,
                            $"Before reset {DateTime.Now:g}");
                    }
                    catch (VisionResetController.PreviousActiveAtlasImportException error)
                    {
                        importError = error;
                    }
                }
                return (replacement, backup, importError);
            });
            atlasPersistTask = null;
            PublishPersistentAtlas(
                reset.replacement.ValidatedWorld,
                explicitlyRequested: true,
                reopenLiveWorld: false);
            SetStatus(reset.backup is null ? "Active atlas cleared. Stencil labels kept."
                : $"Active atlas cleared. Restore with Load: {reset.backup.Name}. Stencil labels kept.");
            if (reset.importError is { } importError) ShowError(importError);
        }, canReplaceFaultedActive: true, invalidateLiveWorld: true);
    }

    private void ReopenActiveLiveWorld()
    {
        liveWorldPipeline ??= new WindowsLiveWorldPipeline(
            IOPath.Combine(activeAtlasDirectory, "live-world"));
        if (!liveWorldPipeline.Reopen())
        {
            throw new InvalidDataException(
                liveWorldPipeline.LastError
                ?? "The active live-world pipeline could not be opened.");
        }
    }

    private async void AtlasDeleteState_Click(object sender, RoutedEventArgs e)
    {
        if (AtlasStatesList.SelectedItem is not AtlasManagerItem selected) return;
        if (selected.State is not { } state)
        {
            if (MessageBox.Show(this,
                    "Delete the current Auto Save atlas?\n\nThis clears its captured world, including old menu images. "
                    + "Auto Save will start fresh without saving a copy. This cannot be undone. "
                    + "Stencil labels, training data, saved states, and game saves are kept.",
                    "Delete Active Atlas", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await ResetActiveAtlasAsync(retainPrevious: false);
            return;
        }
        await RunAtlasOperationAsync(async () =>
        {
            await Task.Run(() => atlasStateStore.Delete(state));
        });
    }

    private async void AtlasLoadState_Click(object sender, RoutedEventArgs e)
    {
        if (AtlasStatesList.SelectedItem is not AtlasManagerItem { State: { } state }) return;
        await RunAtlasOperationAsync(async () =>
        {
            var loaded = await Task.Run(() =>
            {
                VisionResetController.ActiveAtlasReplacement<
                    VisionResetController.ValidatedPersistedAtlas> replacement;
                try
                {
                    replacement = VisionResetController.ReplaceActiveAtlas(
                        atlasStateStore,
                        state,
                        activeAtlasDirectory,
                        VisionResetController.ReadPersistedAtlas,
                        activateWorld: _ =>
                        {
                            liveWorldPipeline ??= new WindowsLiveWorldPipeline(
                                IOPath.Combine(activeAtlasDirectory, "live-world"));
                            if (!liveWorldPipeline.Reopen())
                            {
                                throw new InvalidDataException(
                                    liveWorldPipeline.LastError
                                    ?? "The loaded live-world pipeline could not be opened.");
                            }
                        });
                }
                catch (Exception replacementError)
                {
                    if (Directory.Exists(activeAtlasDirectory)
                        && liveWorldPipeline is not null
                        && !liveWorldPipeline.Reopen())
                    {
                        throw new AggregateException(
                            replacementError,
                            new InvalidDataException(
                                liveWorldPipeline.LastError
                                ?? "The previous live-world pipeline could not be restored."));
                    }
                    throw;
                }
                AtlasSavedState? previous = null;
                VisionResetController.PreviousActiveAtlasImportException? importError = null;
                if (replacement.ArchivedPreviousAtlasDirectory is { } archive)
                {
                    try
                    {
                        previous = VisionResetController.ImportPreviousActiveAtlas(
                            atlasStateStore,
                            archive,
                            $"Before Load {DateTime.Now:g}");
                    }
                    catch (VisionResetController.PreviousActiveAtlasImportException error)
                    {
                        importError = error;
                    }
                }
                return (replacement, previous, importError);
            });
            atlasPersistTask = null;
            PublishPersistentAtlas(
                loaded.replacement.ValidatedWorld,
                explicitlyRequested: true,
                reopenLiveWorld: false);
            if (loaded.previous is { } previous)
            {
                SetStatus(
                    $"Loaded {state.Name}. Previous Auto Save is available as {previous.Name}.");
            }
            else if (loaded.importError is null)
            {
                SetStatus($"Loaded {state.Name}.");
            }
            if (loaded.importError is { } importError) ShowError(importError);
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }, canReplaceFaultedActive: true, invalidateLiveWorld: true);
    }

    private async Task RunAtlasOperationAsync(
        Func<Task> operation,
        bool canReplaceFaultedActive = false,
        bool invalidateLiveWorld = false)
    {
        if (atlasOperationInProgress || inputPathReplayCancellation is not null || isClosing || gameLostCountdownStarted) return;
        var resume = visionCancellation is not null;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        atlasOperationCompletion = completion.Task;
        atlasOperationInProgress = true;
        GameplayAtlasManager.IsEnabled = false;

        StopVision();
        try
        {
            // Drain capture/analysis and disk writes before moving the active
            // directory. No pre-reset result may resurrect tiles afterwards.
            if (visionRunCompletion is { } captureFinished) await captureFinished;
            await worldWorker.DrainAsync();
            if (invalidateLiveWorld) liveWorldPipeline?.InvalidatePendingWork();
            try
            {
                await FlushActiveAtlasAsync();
            }
            catch when (canReplaceFaultedActive)
            {
                // The failed task was awaited and observed. A load/reset may
                // now replace that damaged active revision; Save may not.
                atlasPersistTask = null;
            }
            if (isClosing || gameLostCountdownStarted) return;
            await operation();
            await RefreshAtlasManagerAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            atlasOperationInProgress = false;
            GameplayAtlasManager.IsEnabled = true;
            completion.TrySetResult();
            if (ReferenceEquals(atlasOperationCompletion, completion.Task))
                atlasOperationCompletion = null;
            if (resume && !isClosing && !gameLostCountdownStarted)
                StartVision_Click(this, new RoutedEventArgs());
        }
    }

    private void ResetGameplayAtlas(bool deleteArchive)
    {
        gameplayAtlas.Reset();
        activeAtlasPersistenceBlocked = false;
        Volatile.Write(ref gameplayAtlasRequiresFullRewrite, false);
        activeAtlasCreatedAt = DateTimeOffset.UtcNow;
        ClearGameplayAtlasRuntime();
        if (deleteArchive && Directory.Exists(activeAtlasDirectory))
        {
            Directory.Delete(activeAtlasDirectory, recursive: true);
        }
    }

    private void ClearGameplayAtlasRuntime(bool reopenLiveWorld = true)
    {
        Volatile.Write(ref completedWorld, null);
        Interlocked.Exchange(ref pendingWorldTrackingLoss, -1);
        Interlocked.Increment(ref groundHypothesisResetGeneration);
        visualGameplayTracker.Reset();
        gameplayPresentation.Reset();
        frameMotionTracker.Reset();
        inputPathMotionTracker.Reset();
        latestMotion = null;
        gameplayFrameRegistered = false;
        gameplayLiveFrame = null;
        gameplayLiveTelemetry = null;
        Interlocked.Exchange(ref gameplayAtlasPresentationPending, null);
        hasFreshGameFrame = false;
        viewportPanStart = null;
        GameplayImage.Source = null;
        GameplayAtlasCanvas.Width = CanvasWidth;
        GameplayAtlasCanvas.Height = CanvasHeight;
        GameplayAtlasCanvas.RenderTransform = Transform.Identity;
        gameplayViewport.Reset();
        gameplayFraming = "Current";
        GameplayPlaceholder.Visibility = Visibility.Visible;
        DetectionCanvas.Children.Clear();
        trackedGround.Clear();
        if (reopenLiveWorld && liveWorldPipeline is not null
            && !liveWorldPipeline.Reopen())
        {
            throw new InvalidDataException(
                liveWorldPipeline.LastError
                ?? "The live-world pipeline could not be reopened.");
        }
        gameplayRenderedRevision = gameplayAtlas.Revision;
        gameplayPersistedRevision = gameplayAtlas.Revision;
        foreach (var image in gameplayTileImages.Values) GameplayAtlasCanvas.Children.Remove(image);
        gameplayTileImages.Clear();
        gameplayOccupiedTileBounds.Clear();
        GameplayGroundCanvas.Children.Clear();
        gameplayAtlasMinimumTileX = gameplayAtlasMinimumTileY = null;
    }

    private async void HackerDeleteAtlas_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        await RunAtlasOperationAsync(() =>
        {
            CancelGroundTruthGesture();
            InvalidatePendingHackerWork(resetCore: true);
            foreach (var image in hackerTileImages.Values)
                AtlasCanvas.Children.Remove(image);
            foreach (var image in hackerPreviewTileImages.Values)
                AtlasCanvas.Children.Remove(image);
            hackerTileImages.Clear();
            hackerPreviewTileImages.Clear();
            hackerTileBounds.Clear();
            hackerTileContentBounds.Clear();
            hackerOccupiedTileBounds.Clear();
            hackerAtlasMinimumTileX = hackerAtlasMinimumTileY = null;
            hackerLiveFrame = null;
            hackerPreviewActive = false;
            HackerLiveImage.Source = null;
            HackerLiveImage.Visibility = Visibility.Collapsed;
            AtlasCanvas.Width = CanvasWidth;
            AtlasCanvas.Height = CanvasHeight;
            HackerOverlayCanvas.Width = CanvasWidth;
            HackerOverlayCanvas.Height = CanvasHeight;
            hackerViewport.Reset("Fit");
            AtlasCanvas.RenderTransform = Transform.Identity;
            groundTruthLabels.ClearSelection();
            rooms.Clear();
            roomConnections.Clear();
            PersistHackerEdits();
            AtlasEmptyPanel.Visibility = Visibility.Visible;
            RedrawHackerEdits();
            return Task.CompletedTask;
        });
    }

    private static void SaveBitmap(BgraFrame frame, string path)
    {
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(FrameBitmap(frame)));
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        encoder.Save(stream);
        stream.Flush(flushToDisk: true);
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
        var stagedTiles = new List<(
            AtlasTileCoordinate Coordinate,
            Image Image,
            ViewportRect? OccupiedBounds)>(tiles.Length);
        var stagedCoordinates = new HashSet<AtlasTileCoordinate>();
        foreach (var tile in tiles)
        {
            var key = new AtlasTileCoordinate(tile.coordinate.X, tile.coordinate.Y);
            if (!stagedCoordinates.Add(key))
            {
                throw new InvalidDataException(
                    $"Atlas preview contains duplicate tile coordinates ({key.X}, {key.Y}).");
            }
            var bitmap = LoadBitmap(tile.path);
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                throw new InvalidDataException($"Atlas preview tile is empty: {tile.path}");
            }
            var image = new Image
            {
                Source = bitmap,
                Width = tileSize,
                Height = tileSize,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(image, 0);
            Canvas.SetLeft(image, (tile.coordinate.X - minimumX) * tileSize);
            Canvas.SetTop(image, (tile.coordinate.Y - minimumY) * tileSize);
            stagedTiles.Add((
                key,
                image,
                OccupiedBounds(bitmap, tile.coordinate, (int)tileSize)));
        }

        // Asset decoding, coordinate validation, occupancy scans, and WPF element
        // construction all complete before replacing either live or preview state.
        ClearHackerPreviewTileImages();
        foreach (var image in hackerTileImages.Values)
            image.Visibility = Visibility.Collapsed;
        hackerPreviewActive = true;
        HackerLiveImage.Visibility = Visibility.Collapsed;
        HackerOverlayCanvas.Visibility = Visibility.Collapsed;
        hackerOccupiedTileBounds.Clear();
        hackerAtlasMinimumTileX = minimumX;
        hackerAtlasMinimumTileY = minimumY;
        AtlasCanvas.Width = (maximumX - minimumX + 1) * tileSize;
        AtlasCanvas.Height = (maximumY - minimumY + 1) * tileSize;
        foreach (var tile in stagedTiles)
        {
            hackerPreviewTileImages[tile.Coordinate] = tile.Image;
            AtlasCanvas.Children.Add(tile.Image);
            if (tile.OccupiedBounds is { } bounds)
            {
                var previewId = ((long)tile.Coordinate.X << 32) | (uint)tile.Coordinate.Y;
                hackerOccupiedTileBounds[new HackerTileKey(Guid.Empty, previewId)] = bounds;
            }
        }
        AtlasEmptyPanel.Visibility = Visibility.Collapsed;
        AtlasPreviewText.Text =
            $"ATLAS  •  {tiles.Length} tiles  •  {maximumX - minimumX + 1}×{maximumY - minimumY + 1}";
        ApplyViewport(AtlasCanvas, HackerViewportPanel,
            DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode),
            HackerLiveImage);
    }

    private bool RestoreLatestHackerPresentation()
    {
        if (latestHackerPublication is not { } retained) return false;
        ApplyHackerPipelinePublication(retained with
        {
            CaptureGeneration = Interlocked.Read(ref recentCaptureGeneration),
            WorkGeneration = Interlocked.Read(ref hackerWorkGeneration)
        });
        return hackerLivePrepared is not null;
    }

    private void ApplyHackerPipelinePublication(HackerPipelinePublication publication)
    {
        if (publication.CaptureGeneration != Interlocked.Read(ref recentCaptureGeneration)
            || publication.WorkGeneration != Interlocked.Read(ref hackerWorkGeneration))
        {
            return;
        }
        if (workspace != "Hacker") return;

        var restoresPreview = hackerPreviewActive;
        ActivateLiveHackerRegistry();
        hackerLivePrepared = publication.Prepared;
        hackerLiveFrame = publication.Prepared.LiveImage;
        HackerLiveImage.Source = publication.LiveBitmap;
        HackerLiveImage.Visibility = Visibility.Visible;
        AtlasEmptyPanel.Visibility = Visibility.Collapsed;

        var stagedAtlas = publication.Atlas;
        if (stagedAtlas is not null
            && (stagedAtlas.Revision > hackerPublishedRevision || restoresPreview))
        {
            RenderHackerAtlasPublication(stagedAtlas);
        }
        RenderHackerAtlasLayout();
        if (latestGroundTruthContext is { } context
            && context.CaptureGeneration == publication.CaptureGeneration
            && context.CaptureId == publication.Prepared.LiveImage.CaptureId)
        {
            PresentGroundTruthMatchedContext(context);
        }

        // WPF sources, room geometry, live image, and overlays are now a full
        // coherent snapshot. Only now acknowledge the Core atlas revision.
        if (stagedAtlas is not null
            && publication.CaptureGeneration == Interlocked.Read(ref recentCaptureGeneration)
            && publication.WorkGeneration == Interlocked.Read(ref hackerWorkGeneration))
        {
            hackerPublishedRevision = Math.Max(hackerPublishedRevision, stagedAtlas.Revision);
        }
        if (publication.CaptureGeneration == Interlocked.Read(ref recentCaptureGeneration)
            && publication.WorkGeneration == Interlocked.Read(ref hackerWorkGeneration))
        {
            latestHackerPublication = publication;
        }
    }

    private void RenderHackerAtlasPublication(HackerAtlasPublication publication)
    {
        hackerAtlasOutput = publication.Output;
        SynchronizeHackerRoomEdits(publication.Output);
        var retained = publication.Tiles.Select(tile => tile.Key).ToHashSet();
        foreach (var stale in hackerTileImages.Keys.Where(key => !retained.Contains(key)).ToArray())
        {
            AtlasCanvas.Children.Remove(hackerTileImages[stale]);
            hackerTileImages.Remove(stale);
            hackerTileBounds.Remove(stale);
            hackerTileContentBounds.Remove(stale);
            hackerOccupiedTileBounds.Remove(stale);
        }
        foreach (var tile in publication.Tiles)
        {
            if (!hackerTileImages.TryGetValue(tile.Key, out var image))
            {
                image = new Image
                {
                    Stretch = Stretch.Fill,
                    SnapsToDevicePixels = true
                };
                Panel.SetZIndex(image, 0);
                hackerTileImages[tile.Key] = image;
                AtlasCanvas.Children.Insert(0, image);
            }
            image.Source = tile.Bitmap;
            image.Width = tile.Bounds.Width;
            image.Height = tile.Bounds.Height;
            image.Visibility = Visibility.Visible;
            hackerTileBounds[tile.Key] = tile.Bounds;
            if (tile.ContentBounds is { } content)
                hackerTileContentBounds[tile.Key] = content;
            else
                hackerTileContentBounds.Remove(tile.Key);
        }
    }

    private void SynchronizeHackerRoomEdits(HackerAtlasOutput output)
    {
        foreach (var room in output.Rooms)
        {
            if (!rooms.TryGetValue(room.SceneName, out var edit))
            {
                edit = new RoomEdit(
                    room.SceneName,
                    room.Bounds.MinX,
                    -room.Bounds.MaxY,
                    room.Bounds.MaxX,
                    -room.Bounds.MinY);
                rooms.Add(room.SceneName, edit);
            }
            edit.Left = room.Bounds.MinX;
            edit.Top = -room.Bounds.MaxY;
            edit.Right = room.Bounds.MaxX;
            edit.Bottom = -room.Bounds.MinY;
            edit.LayoutX = room.Position.X;
            edit.LayoutY = room.Position.Y;
        }

        var names = output.Rooms.ToDictionary(room => room.Id, room => room.SceneName);
        roomConnections.Clear();
        roomConnections.AddRange(output.Connections
            .Where(connection => names.ContainsKey(connection.FromRoomId)
                && names.ContainsKey(connection.ToRoomId))
            .Select(connection => new RoomConnectionEdit(
                connection.Id,
                names[connection.FromRoomId],
                names[connection.ToRoomId],
                connection.FromSide,
                connection.ToSide,
                connection.FromCoordinate,
                connection.ToCoordinate)));
    }

    private void RenderHackerAtlasLayout()
    {
        var topology = BuildHackerRoomLayout();
        AtlasPoint RoomPosition(string sceneName)
        {
            var room = topology.Rooms.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.SceneName, sceneName, StringComparison.Ordinal));
            return room is null
                ? new AtlasPoint(0, 0)
                : HackerRoomPosition(room, topology.Edits, topology.Layout);
        }

        var projected = new List<ViewportRect>();
        foreach (var key in hackerTileImages.Keys)
        {
            if (!hackerTileBounds.TryGetValue(key, out var bounds)) continue;
            var sceneName = hackerAtlasOutput?.Rooms
                .FirstOrDefault(room => room.Id == key.RoomId)?.SceneName;
            var position = RoomPosition(sceneName ?? string.Empty);
            projected.Add(new ViewportRect(
                bounds.MinX + position.X,
                -bounds.MaxY - position.Y,
                bounds.Width,
                bounds.Height));
        }
        foreach (var room in topology.Rooms.Values)
        {
            var position = HackerRoomPosition(room, topology.Edits, topology.Layout);
            var bounds = room.Bounds.Offset(position.X, position.Y);
            projected.Add(new ViewportRect(
                bounds.MinX,
                -bounds.MaxY,
                Math.Max(1, bounds.MaxX - bounds.MinX),
                Math.Max(1, bounds.MaxY - bounds.MinY)));
        }
        if (HackerProjectedLiveBounds(RoomPosition) is { } liveBounds)
            projected.Add(liveBounds);

        var previousMinimumX = hackerAtlasMinimumTileX ?? 0;
        var previousMinimumY = hackerAtlasMinimumTileY ?? 0;
        if (projected.Count == 0)
        {
            hackerAtlasMinimumTileX = hackerAtlasMinimumTileY = null;
            AtlasCanvas.Width = CanvasWidth;
            AtlasCanvas.Height = CanvasHeight;
        }
        else
        {
            var minimumX = projected.Min(bounds => bounds.X);
            var minimumY = projected.Min(bounds => bounds.Y);
            var maximumX = projected.Max(bounds => bounds.Right);
            var maximumY = projected.Max(bounds => bounds.Bottom);
            hackerAtlasMinimumTileX = checked((int)Math.Floor(minimumX / HackerAtlasTileSize));
            hackerAtlasMinimumTileY = checked((int)Math.Floor(minimumY / HackerAtlasTileSize));
            var originX = hackerAtlasMinimumTileX.Value * (double)HackerAtlasTileSize;
            var originY = hackerAtlasMinimumTileY.Value * (double)HackerAtlasTileSize;
            AtlasCanvas.Width = Math.Max(1, Math.Ceiling(maximumX - originX));
            AtlasCanvas.Height = Math.Max(1, Math.Ceiling(maximumY - originY));
            if (hackerAtlasMinimumTileX.Value != previousMinimumX
                || hackerAtlasMinimumTileY.Value != previousMinimumY)
            {
                var originShift = new Vector(
                    (hackerAtlasMinimumTileX.Value - previousMinimumX) * HackerAtlasTileSize,
                    (hackerAtlasMinimumTileY.Value - previousMinimumY) * HackerAtlasTileSize);
                ApplySettledViewportTransform(
                    AtlasCanvas,
                    hackerViewport.Rebase(originShift.X, originShift.Y));
                if (viewportPanStart is not null && workspace == "Hacker")
                {
                    viewportPanMatrix.OffsetX += originShift.X * viewportPanMatrix.M11;
                    viewportPanMatrix.OffsetY += originShift.Y * viewportPanMatrix.M22;
                }
            }
        }

        var canvasOriginX = (hackerAtlasMinimumTileX ?? 0) * (double)HackerAtlasTileSize;
        var canvasOriginY = (hackerAtlasMinimumTileY ?? 0) * (double)HackerAtlasTileSize;
        hackerOccupiedTileBounds.Clear();
        foreach (var pair in hackerTileImages)
        {
            if (!hackerTileBounds.TryGetValue(pair.Key, out var bounds)) continue;
            var sceneName = hackerAtlasOutput?.Rooms
                .FirstOrDefault(room => room.Id == pair.Key.RoomId)?.SceneName;
            var position = RoomPosition(sceneName ?? string.Empty);
            var left = bounds.MinX + position.X;
            var top = -bounds.MaxY - position.Y;
            Canvas.SetLeft(pair.Value, left - canvasOriginX);
            Canvas.SetTop(pair.Value, top - canvasOriginY);
            if (hackerTileContentBounds.TryGetValue(pair.Key, out var content))
            {
                hackerOccupiedTileBounds[pair.Key] = new ViewportRect(
                    content.X + position.X,
                    content.Y - position.Y,
                    content.Width,
                    content.Height);
            }
        }
        HackerOverlayCanvas.Width = AtlasCanvas.Width;
        HackerOverlayCanvas.Height = AtlasCanvas.Height;
        AtlasEmptyPanel.Visibility = hackerTileImages.Count == 0
            && hackerLiveFrame is null
                ? Visibility.Visible
                : Visibility.Collapsed;
        PositionHackerLiveFrame(RoomPosition, applyViewport: false);
        RedrawHackerEdits();
        var roomCount = hackerAtlasOutput?.RoomCount ?? 0;
        AtlasPreviewText.Text =
            $"ATLAS  •  {hackerTileImages.Count} tiles  •  {roomCount} rooms";
        ApplyViewport(
            AtlasCanvas,
            HackerViewportPanel,
            DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode),
            HackerLiveImage);
    }

    private ViewportRect? HackerProjectedLiveBounds(
        Func<string, AtlasPoint> roomPosition)
    {
        if (hackerLivePrepared is not { } prepared) return null;
        var position = roomPosition(prepared.SceneName);
        var bounds = HackerLocalLiveBounds(prepared);
        return new ViewportRect(
            bounds.MinX + position.X,
            -bounds.MaxY - position.Y,
            bounds.Width,
            bounds.Height);
    }

    private HackerAtlasRect HackerLocalLiveBounds(HackerPreparedFrame prepared)
    {
        var room = hackerAtlasOutput?.Rooms.FirstOrDefault(candidate =>
            string.Equals(candidate.SceneName, prepared.SceneName, StringComparison.Ordinal));
        return room is null
            ? prepared.LiveBounds
            : prepared.LiveBounds.Offset(-room.Position.X, -room.Position.Y);
    }

    private void ClearHackerPreviewTileImages()
    {
        foreach (var image in hackerPreviewTileImages.Values)
            AtlasCanvas.Children.Remove(image);
        hackerPreviewTileImages.Clear();
    }

    private void ActivateLiveHackerRegistry()
    {
        if (hackerPreviewActive || hackerPreviewTileImages.Count > 0)
        {
            ClearHackerPreviewTileImages();
            hackerOccupiedTileBounds.Clear();
            hackerPreviewActive = false;
        }
        foreach (var image in hackerTileImages.Values)
            image.Visibility = Visibility.Visible;
        HackerLiveImage.Visibility = hackerLivePrepared is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        HackerOverlayCanvas.Visibility = Visibility.Visible;
    }

    private void PositionHackerLiveFrame(
        Func<string, AtlasPoint>? roomPosition = null,
        bool applyViewport = true)
    {
        if (hackerLivePrepared is not { } prepared)
        {
            return;
        }
        var position = roomPosition?.Invoke(prepared.SceneName)
            ?? HackerScenePosition(prepared.SceneName);
        var bounds = HackerLocalLiveBounds(prepared);
        var originX = (hackerAtlasMinimumTileX ?? 0) * HackerAtlasTileSize;
        var originY = (hackerAtlasMinimumTileY ?? 0) * HackerAtlasTileSize;
        HackerLiveImage.Width = bounds.Width;
        HackerLiveImage.Height = bounds.Height;
        Canvas.SetLeft(
            HackerLiveImage,
            bounds.MinX + position.X - originX);
        Canvas.SetTop(
            HackerLiveImage,
            -bounds.MaxY - position.Y - originY);
        if (applyViewport)
        {
            ApplyViewport(AtlasCanvas, HackerViewportPanel,
                DashboardPresentationPolicy.HackerFramingMode(hackerInteractionMode),
                HackerLiveImage);
        }
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
        if (stepMode) { TrackingStep_Click(sender, e); return; }
        if (inputPathReplayCancellation is not null) { inputPathReplayCancellation.Cancel(); return; }
        if (string.IsNullOrWhiteSpace(InputPathText.Text)) BrowseInputPath_Click(sender, e);
        var path = InputPathText.Text.Trim();
        if (string.IsNullOrEmpty(path)) return;
        if (!int.TryParse(InputPathRepeatsText.Text, out var repeats) || repeats is <= 0 or > 100)
        { ShowError(new InvalidOperationException("Path repeats must be between 1 and 100.")); return; }
        using var cancellation = new CancellationTokenSource();
        inputPathReplayCancellation = cancellation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replayUiFinished = finished;
        ReplayInputPathButton.Content = "Stop Replay";
        PathRecordingButton.IsEnabled = false;
        ReplayFullTraceItem.IsEnabled = ReplayFailFastItem.IsEnabled = false;
        var originalDirectory = activeAtlasDirectory;
        var originalStore = atlasStateStore;
        GroundTruthAtlasMosaic? originalAtlas = null;
        var originalCreatedAt = activeAtlasCreatedAt;
        string? output = null;
        try
        {
            var recording = new RecordedInputPathStore().Load(path);
            _ = InputPathPlayback.Poses(recording);
            if (recording.StartCheckpoint is null) throw new InvalidOperationException("Controlled replay needs a saved start checkpoint.");
            if (recording.Duration * repeats > 86400) throw new InvalidOperationException("Total replay duration exceeds 24 hours.");
            await StopReplayCaptureAsync();
            await FlushActiveAtlasAsync();
            originalAtlas = gameplayAtlas;
            var store = new RecordedInputPathStore(System.IO.Path.GetDirectoryName(path)!);
            for (var iteration = 1; iteration <= repeats; iteration++)
            {
                output = null;
                var benchmark = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(originalDirectory)!, "benchmarks", Guid.NewGuid().ToString("N"));
                inputPathReplayDivergence = null;
                inputPathDivergenceMonitor.Prepare();
                var completed = false;
                try
                {
                    await gameControls.ReplayPathAsync(recording, iteration, true,
                        result => output = store.Save(result), cancellation.Token,
                        prepare: (restored, ct) => Dispatcher.InvokeAsync(async () => {
                            if (!restored) await ResetReplayAtlasAsync(System.IO.Path.Combine(benchmark, "settling"), ct, startCapture: false);
                            else
                            {
                                // Never seed visual references from the pre-restore endpoint.
                                StartVision_Click(this, new RoutedEventArgs());
                                await WaitForReplayReadyAsync(recording.StartCheckpoint, ct);
                                await ResetReplayAtlasAsync(System.IO.Path.Combine(benchmark, "run"), ct);
                                await WaitForReplayReadyAsync(recording.StartCheckpoint, ct);
                                SetStatus("Replaying recorded path…");
                            }
                        }).Task.Unwrap(),
                        beginEvaluation: BeginReplayEvaluation,
                        evaluationMode: replayFailFast ? "fail-fast" : "full-trace");
                    completed = true;
                }
                finally
                {
                    EndReplayFrameAudit(benchmark);
                    Directory.CreateDirectory(benchmark);
                    File.WriteAllText(System.IO.Path.Combine(benchmark, "evaluation.json"), JsonSerializer.Serialize(new {
                        sourcePath = path, output, iteration, workspace, mode = replayFailFast ? "fail-fast" : "full-trace",
                        decision = inputPathReplayDivergence ?? inputPathDivergenceMonitor.LastDecision,
                        completed,
                        matchedSamples = inputPathDivergenceMonitor.MatchedSampleCount,
                        evaluatedSamples = inputPathDivergenceMonitor.EvaluatedSampleCount,
                        maximumErrorPixels = inputPathDivergenceMonitor.MaximumErrorPixels,
                        cancelled = cancellation.IsCancellationRequested,
                        status = inputPathReplayDivergence is not null ? "diverged" : !completed ? "incomplete" : inputPathDivergenceMonitor.EvaluatedSampleCount < 12 ? "insufficient-evidence" : "no-sustained-divergence"
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    inputPathDivergenceMonitor.Stop();
                }
            }
            SetStatus(inputPathReplayDivergence is null ? $"Replay finished. Evaluation and trace saved: {output}"
                : $"Replay finished; tracking diverged ({inputPathReplayDivergence.Cause}). Trace: {output}");
        }
        catch (OperationCanceledException) { SetStatus($"Replay stopped. Trace saved: {output}"); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            try
            {
                try
                {
                    await StopReplayCaptureAsync();
                    if (originalAtlas is not null) await FlushActiveAtlasAsync();
                }
                finally
                {
                    activeAtlasDirectory = originalDirectory;
                    atlasStateStore = originalStore;
                    if (originalAtlas is not null)
                    {
                        gameplayAtlas = originalAtlas;
                        activeAtlasCreatedAt = originalCreatedAt;
                        activeAtlasPersistenceBlocked = false;
                        liveWorldPipeline = await Task.Run(() => new WindowsLiveWorldPipeline(System.IO.Path.Combine(originalDirectory, "live-world")));
                        ClearGameplayAtlasRuntime(reopenLiveWorld: false);
                    }
                }
                if (!isClosing && !gameLostCountdownStarted) StartVision_Click(this, new RoutedEventArgs());
            }
            catch (Exception error) { ShowError(error); }
            finally
            {
                inputPathReplayCancellation = null;
                ReplayInputPathButton.Content = "Replay";
                ReplayInputPathButton.ToolTip = output is null ? "Replay a recorded input path" : $"Replay trace: {output}";
                PathRecordingButton.IsEnabled = true;
                ReplayFullTraceItem.IsEnabled = ReplayFailFastItem.IsEnabled = true;
                finished.TrySetResult();
            }
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

    private static string? FindWindowsRoot()
    {
        var packaged = IOPath.Combine(AppContext.BaseDirectory, "training");
        if (File.Exists(IOPath.Combine(packaged, "train-model-windows.ps1"))) return packaged;
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

    private static string? LatestAtlas(params string?[] roots) => roots
        .Where(root => root is not null && Directory.Exists(root))
        .SelectMany(root => Directory.EnumerateDirectories(root!))
        .Where(directory => File.Exists(IOPath.Combine(directory, "atlas.json"))
            && Directory.EnumerateFiles(directory, "tile_*_*.bmp").Any())
        .OrderByDescending(directory => File.GetLastWriteTimeUtc(
            IOPath.Combine(directory, "atlas.json")))
        .FirstOrDefault();


    private void SetStatus(string message) => StatusText.Text = message;

    private void ShowError(Exception error)
    {
        SetStatus(error.Message);
        MessageBox.Show(this, error.Message, "Hollow Knight Vision", MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

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
        public double LayoutX { get; set; }
        public double LayoutY { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public HashSet<string> Connections { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record RoomConnectionEdit(
        Guid Id,
        string FromSceneName,
        string ToSceneName,
        HackerRoomSide FromSide,
        HackerRoomSide ToSide,
        double FromCoordinate,
        double ToCoordinate);

    private sealed record HackerEditDocument(
        IReadOnlyList<GroundEditLine> Ground,
        IReadOnlyList<RoomEdit> Rooms,
        int GroundMigrationVersion = 0,
        IReadOnlyList<RoomConnectionEdit>? Connections = null);

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
