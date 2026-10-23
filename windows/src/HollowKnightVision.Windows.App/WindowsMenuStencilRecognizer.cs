using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>Owns the user's contract-validated menu catalog and captured HUD reference.</summary>
internal sealed class WindowsMenuStencilRecognizer
{
    internal sealed record Observation(LiveGameStatus? Status, LiveGameStatus? RecognizedStatus,
        MenuStencilResult? Menu, HudStencilResult? Hud, long Generation, IReadOnlyList<ObjectDetection> Detections, string Diagnostic)
    {
        public string? Context => Status?.ContextIdentifier;
        public bool HasFreshGameplayEvidence => RecognizedStatus?.IsGameplay == true;
        public LiveObjectDetectionSnapshot Objects { get; init; } = LiveObjectDetectionSnapshot.Empty;
        public LiveObjectInferenceDiagnostics ObjectDiagnostics { get; init; } = new();
    }
    private readonly string examplesRoot, calibrationPath;
    private readonly object gate = new();
    private readonly LiveMenuStencilWorker menuWorker;
    private readonly LiveGameStatusPipeline statusPipeline = new();
    private readonly LiveObjectInferenceWorker objects = new();
    private double objectExtension = .12;
    private bool createsPixelDiagnostic;
    private IReadOnlySet<string> customClassIdentifiers = new HashSet<string>();
    private HudStencilTemplate? hudTemplate;
    private HudStencilTracker hud = new(null);
    private HudStencilResult? latestHud;
    private long generation;
    private bool acceptsFrames = true;
    private string language;
    internal bool HasGameplayReference { get { lock (gate) return hudTemplate is not null; } }
    internal HudStencilResult? LatestHud => Volatile.Read(ref latestHud);
    internal bool IsCurrent(long value) => Interlocked.Read(ref generation) == value;
    internal void ConfigureObjectDiagnostics(double extension, bool pixels)
    {
        lock (gate) { objectExtension = double.IsFinite(extension) ? Math.Clamp(extension, 0, .5) : .12; createsPixelDiagnostic = pixels; }
    }
    internal void SetObjectInferenceSuspended(bool suspended) => objects.SetSuspended(suspended);
    internal void ResetObjectInference() => objects.Reset();
    internal Task DrainObjectInferenceAsync() => objects.WhenIdleAsync();
    internal LiveObjectDetectionSnapshot SnapshotObjectsAt(BgraFrame frame) => objects.Snapshot(
        Interlocked.Read(ref generation), (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds);

    internal WindowsMenuStencilRecognizer(string examplesRoot, string language = "en")
    {
        this.examplesRoot = Path.GetFullPath(examplesRoot); this.language = language;
        calibrationPath = MenuStencilCalibration.WritablePath(this.examplesRoot);
        var calibration = new MenuSelectorCalibrationTracker(calibrationPath,
            MenuStencilCalibration.ProjectMirrorPath(Environment.GetCommandLineArgs()),
            fallbackCalibration: MenuStencilCalibration.LoadDefault(calibrationPath));
        menuWorker = new(new MenuStencilTracker(minimumSearchInterval: .1, calibration: calibration));
        Refresh();
    }
    internal void Refresh(string? language = null)
    {
        lock (gate)
        {
            if (language is not null) this.language = language;
            var examples = new LabelingExampleStore(examplesRoot).Load();
            HudStencilTemplate? nextHud = null;
            var example = StencilInitializationPolicy.RepresentativeExample(examples, "gameplay-hud", this.language, GameRuntimeMode.Normal);
            if (example is not null)
                try { nextHud = HudStencilTemplate.Captured(LoadFrame(example.ImagePath), example.Manifest.Annotations, example.Id.ToString()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException) { }
            IReadOnlyList<MenuStencilLiveSource> live = [];
            try { live = new MenuStencilLiveCaptureStore(MenuStencilLiveCaptureStore.RootForExamples(examplesRoot)).Load().Select(item => item.Source).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            MenuStencilCatalog catalog;
            try
            {
                catalog = new MenuStencilCatalogBuilder(LabelingContractCatalog.ScreenContexts, LoadFrame,
                    item => LabelingContractEvaluator.AcceptsMenuExample(item)).Build(examples, MenuStencilCalibration.LoadDefault(calibrationPath), live);
            }
            catch (MenuStencilCatalogUnavailableException) { catalog = new([], 640, 360, 0, 360, []); }
            menuWorker.ReplaceCatalog(catalog);
            hudTemplate = nextHud; hud = new(nextHud);
            statusPipeline.Reset(preservingGameplay: true);
            objects.Reset();
            try
            {
                var support = Path.GetDirectoryName(Path.GetDirectoryName(examplesRoot))!;
                customClassIdentifiers = new UserObjectCatalog(Path.Combine(support, "object-catalog-v1.json")).Load()
                    .Select(item => item.Identifier).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { customClassIdentifiers = new HashSet<string>(); }
            Volatile.Write(ref latestHud, null); Interlocked.Increment(ref generation);
        }
    }
    internal void ResetTracking()
    {
        lock (gate)
        {
            menuWorker.Reset(); hud = new(hudTemplate); acceptsFrames = true;
            statusPipeline.Reset(preservingGameplay: true);
            objects.Reset();
            Volatile.Write(ref latestHud, null); Interlocked.Increment(ref generation);
        }
    }
    internal void ResetGameSession()
    {
        lock (gate)
        {
            menuWorker.Reset(); hud = new(hudTemplate); statusPipeline.Reset();
            objects.Reset();
            Volatile.Write(ref latestHud, null); Interlocked.Increment(ref generation);
        }
    }
    internal void StopTracking()
    {
        lock (gate)
        {
            // Drain running calibration writes before shutdown/factory reset
            // archives the support directory. Late capture work cannot restart it.
            acceptsFrames = false; menuWorker.Reset(); hud = new(hudTemplate);
            statusPipeline.Reset(preservingGameplay: true);
            objects.Reset();
            Volatile.Write(ref latestHud, null); Interlocked.Increment(ref generation);
        }
    }
    internal Observation Observe(BgraFrame frame, bool asynchronously = false) => Analyze(frame, asynchronously, null);
    // Exact analysis remains useful for isolated PNG/contract checks. Production
    // capture uses AnalyzeLive and never waits for the object model.
    internal Observation Analyze(BgraFrame frame, bool asynchronously, Func<IReadOnlyList<ObjectDetection>>? inferObjects)
        => AnalyzeCore(frame, asynchronously, inferObjects, asynchronousObjects: false);
    internal Observation AnalyzeLive(BgraFrame frame, bool asynchronously, Func<IReadOnlyList<ObjectDetection>>? inferObjects)
        => AnalyzeCore(frame, asynchronously, inferObjects, asynchronousObjects: true);
    internal Observation AnalyzeLiveWithPublication(BgraFrame frame, bool asynchronously,
        Func<IReadOnlyList<ObjectDetection>>? inferObjects, Action<LiveObjectInferencePublication> onPublished)
        => AnalyzeCore(frame, asynchronously, inferObjects, asynchronousObjects: true, onPublished);
    private Observation AnalyzeCore(BgraFrame frame, bool asynchronously, Func<IReadOnlyList<ObjectDetection>>? inferObjects,
        bool asynchronousObjects, Action<LiveObjectInferencePublication>? onPublished = null)
    {
        lock (gate)
        {
            if (!acceptsFrames) return new(null, null, null, null, generation, [], "stopped");
            var timestamp = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
            var menuObservation = menuWorker.Observe(frame, timestamp, asynchronously)?.Observation;
            var rawMenu = menuObservation?.Result;
            // HUD must run before the menu/gameplay decision. A false menu
            // candidate must not suppress the health evidence that disproves it.
            var hudResult = hud.Observe(frame, true, timestamp, generation);
            var snapshot = LiveObjectDetectionSnapshot.Empty;
            IReadOnlyList<ObjectDetection> InferAndTrack()
            {
                snapshot = objects.Observe(frame, generation, inferObjects!, objectExtension, createsPixelDiagnostic,
                    asynchronously: asynchronousObjects, onPublished: onPublished);
                return snapshot.Tracked;
            }
            var state = statusPipeline.Observe(rawMenu, hudResult, timestamp, CreditsVisualSignature.IsLikelyCredits(frame),
                inferObjects is null ? null : InferAndTrack, customClassIdentifiers);
            // Menus admit no new object work and expose no object overlay. Like
            // Mac, prior bounded work may finish but retains its capture age.
            Volatile.Write(ref latestHud, hudResult);
            var diagnostic = rawMenu is null ? "raw=none admitted=false" : FormattableString.Invariant(
                $"raw={rawMenu.ContextIdentifier}/{rawMenu.SelectedOption ?? "none"} phase={rawMenu.Phase} confidence={rawMenu.Confidence:F3} evidence={rawMenu.SceneEvidenceRatio:F3} language={rawMenu.LanguageIdentifier ?? "shared"} comparisons={rawMenu.ComparisonCount} admitted={(state.Menu is null ? "false" : "true")}; calibration={menuObservation?.Diagnostic ?? "idle"}");
            return new(state.Status, state.RecognizedStatus, state.Menu, hudResult, generation, state.Detections, diagnostic)
                { Objects = snapshot, ObjectDiagnostics = objects.Diagnostics };
        }
    }
    // Kept for HUD integration checks; production consumes one atomic observation.
    internal string? Detect(BgraFrame frame) => Observe(frame).RecognizedStatus?.ContextIdentifier;
    private static BgraFrame LoadFrame(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path, UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * BgraFrame.BytesPerPixel; var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return new(source.PixelWidth, source.PixelHeight, stride, pixels, 0, DateTimeOffset.UnixEpoch);
    }
}
