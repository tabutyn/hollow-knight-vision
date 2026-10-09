using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        try
        {
            var options = HostOptions.Parse(arguments);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("HollowKnightVision.Windows.Host requires Windows.");
                return 2;
            }
            var perMonitorDpiAware = WindowsDpiAwareness.TryEnablePerMonitorV2();

            if (options.ModelProbeRunDirectory is not null)
            {
                using var detector = new OnnxObjectDetector(options.ModelProbeRunDirectory);
                var blank = new BgraFrame(
                    FrameGeometry.ReferenceWidth,
                    FrameGeometry.ReferenceHeight,
                    FrameGeometry.ReferenceWidth * BgraFrame.BytesPerPixel,
                    new byte[FrameGeometry.ReferenceWidth * FrameGeometry.ReferenceHeight * BgraFrame.BytesPerPixel],
                    1,
                    DateTimeOffset.UtcNow);
                var inferenceStopwatch = Stopwatch.StartNew();
                var detections = detector.Detect(blank);
                inferenceStopwatch.Stop();
                PrintJson(new
                {
                    ok = true,
                    mode = "model-probe",
                    model = detector.ModelPath,
                    classIdentifiers = detector.ClassIdentifiers,
                    elapsedMilliseconds = inferenceStopwatch.Elapsed.TotalMilliseconds,
                    detections
                });
                return 0;
            }

            if (options.LabelingCommand is not null)
            {
                return options.LabelingCommand.Kind switch
                {
                    LabelingCommandKind.Capture => LabelingCommandRunner.Capture(
                        options.LabelingCommand),
                    LabelingCommandKind.List => LabelingCommandRunner.List(options.LabelingCommand),
                    LabelingCommandKind.Update => LabelingCommandRunner.Update(
                        options.LabelingCommand),
                    LabelingCommandKind.Delete => LabelingCommandRunner.Delete(
                        options.LabelingCommand),
                    LabelingCommandKind.Export => LabelingCommandRunner.Export(
                        options.LabelingCommand),
                    _ => throw new InvalidOperationException("Unknown labeling command.")
                };
            }

            if (options.VisualTrackDurationSeconds is not null)
            {
                return await VisualMotionRunner.RunAsync(
                    options.VisualTrackDurationSeconds.Value,
                    options.IntervalMilliseconds).ConfigureAwait(false);
            }

            if (options.VisualRouteOutputDirectory is not null)
            {
                return await VisualRouteRunner.RecordAsync(
                    options.VisualRouteOutputDirectory,
                    options.VisualRouteDurationSeconds,
                    options.IntervalMilliseconds).ConfigureAwait(false);
            }

            if (options.VisualRouteReplayDirectory is not null)
            {
                return VisualRouteRunner.Replay(
                    options.VisualRouteReplayDirectory,
                    options.VisualRouteRepeatCount,
                    options.VisualRouteMaximumMeanError,
                    options.VisualRouteMaximumStepError,
                    options.VisualRouteReportPath);
            }

            if (options.VisualRouteWorldDirectory is not null)
            {
                return VisualRouteRunner.BuildWorld(
                    options.VisualRouteWorldDirectory,
                    options.WorldOutputDirectory,
                    options.WorldKeyframeInterval);
            }

            if (options.WorldOptimizeDirectory is not null)
            {
                return VisualRouteRunner.OptimizeWorld(options.WorldOptimizeDirectory);
            }

            if (options.InputPathFile is not null)
            {
                return await InputPathPlaybackRunner.RunAsync(
                    options.InputPathFile,
                    options.InputPathRepeatCount,
                    options.RestoreInputPathCheckpoint).ConfigureAwait(false);
            }

            if (options.GroundDetectOnce)
            {
                var groundWindow = HollowKnightWindowLocator.FindBest();
                if (groundWindow is null)
                {
                    Console.Error.WriteLine(
                        "Hollow Knight window not found. Ground detection never launches it.");
                    return 3;
                }
                var groundFrame = CpuBgraNormalizer.Normalize(
                    new GdiWindowCapture().Capture(groundWindow, 1));
                var groundStopwatch = Stopwatch.StartNew();
                var groundAnalysis = GroundLineDetector.Compare(
                    GroundLineDetector.Analyze(groundFrame));
                var groundLines = GroundLineDetector.SemanticFloorLines(
                    groundAnalysis,
                    options.GroundTuning);
                groundStopwatch.Stop();
                PrintJson(new
                {
                    ok = true,
                    mode = "ground-detect-once",
                    elapsedMilliseconds = groundStopwatch.Elapsed.TotalMilliseconds,
                    tuning = options.GroundTuning,
                    lines = groundLines.Select(line => new
                    {
                        line.Row,
                        start = line.XSpan.Start,
                        endInclusive = line.XSpan.EndInclusive,
                        evidence = line.EvidenceSpans.Select(span => new
                        {
                            span.Start,
                            span.EndInclusive
                        })
                    })
                });
                return 0;
            }

            if (options.AutoNavigateGameplay)
            {
                return await AutoNavigationRunner.RunAsync(
                    options.GameExecutable,
                    options.StartupTimeoutSeconds,
                    options.IntervalMilliseconds).ConfigureAwait(false);
            }

            if (options.AtlasOutputDirectory is not null)
            {
                return await AtlasRecordingRunner.RunAsync(
                    options.AtlasOutputDirectory,
                    options.AtlasDurationSeconds,
                    options.IntervalMilliseconds,
                    options.AtlasPixelsPerWorldUnit).ConfigureAwait(false);
            }

            if (options.ReceiverProbe || options.GroundTruthOnce)
            {
                return await RunReceiverProbe(options.GroundTruthOnce).ConfigureAwait(false);
            }

            var window = HollowKnightWindowLocator.FindBest();
            if (window is null)
            {
                Console.Error.WriteLine("Hollow Knight window not found.");
                return 3;
            }
            if (options.ProbeOnly)
            {
                PrintJson(new
                {
                    ok = true,
                    mode = "probe",
                    processId = window.ProcessId,
                    window.ProcessName,
                    window.Title,
                    window.ClientWidth,
                    window.ClientHeight,
                    window.IsMinimized,
                    perMonitorDpiAware
                });
                return 0;
            }

            if (options.InferenceRunDirectory is not null)
            {
                var inferenceCapture = new GdiWindowCapture();
                var inferenceFrame = CpuBgraNormalizer.Normalize(inferenceCapture.Capture(window, 1));
                using var detector = new OnnxObjectDetector(options.InferenceRunDirectory);
                var inferenceStopwatch = Stopwatch.StartNew();
                var detections = detector.Detect(inferenceFrame);
                inferenceStopwatch.Stop();
                PrintJson(new
                {
                    ok = true,
                    mode = "infer-once",
                    model = detector.ModelPath,
                    classIdentifiers = detector.ClassIdentifiers,
                    elapsedMilliseconds = inferenceStopwatch.Elapsed.TotalMilliseconds,
                    detections
                });
                return 0;
            }

            var capture = new GdiWindowCapture();
            var slot = new LatestFrameSlot();
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < options.FrameCount; index++)
            {
                slot.Publish(capture.Capture(window, index + 1));
                if (index + 1 < options.FrameCount && options.IntervalMilliseconds > 0)
                {
                    Thread.Sleep(options.IntervalMilliseconds);
                }
            }
            stopwatch.Stop();
            if (!slot.TryTake(out var native) || native is null)
            {
                Console.Error.WriteLine("Capture produced no frame.");
                return 4;
            }

            var frame = options.KeepNativeSize ? native : CpuBgraNormalizer.Normalize(native);
            if (options.OutputPath is not null) BmpWriter.Write(frame, options.OutputPath);
            var hash = Convert.ToHexString(SHA256.HashData(frame.Pixels)).ToLowerInvariant();
            PrintJson(new
            {
                ok = true,
                mode = "capture",
                captureMethod = "print-window-target-with-gdi-fallback",
                perMonitorDpiAware,
                nativeWidth = native.Width,
                nativeHeight = native.Height,
                outputWidth = frame.Width,
                outputHeight = frame.Height,
                framesRequested = options.FrameCount,
                elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                frameSha256 = hash,
                outputPath = options.OutputPath is null
                    ? null
                    : Path.GetFullPath(options.OutputPath),
                slot = slot.Statistics
            });
            return 0;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            PrintHelp();
            return 64;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void PrintJson(object value) => Console.WriteLine(
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private static async Task<int> RunReceiverProbe(bool groundTruthOnce)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var receiver = new ReceiverClient();
        try
        {
            await receiver.ConnectAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await receiver.ReceiveLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) throw new IOException("Receiver closed the connection.");
                var type = ReceiverWireCodec.MessageType(line);
                if (!groundTruthOnce && type == "capabilitiesAck")
                {
                    var acknowledgement =
                        ReceiverWireCodec.DecodeLine<ReceiverCapabilitiesAcknowledgement>(line);
                    PrintJson(new
                    {
                        ok = true,
                        mode = "receiver-probe",
                        sessionID = acknowledgement.SessionId,
                        acknowledgement.Version,
                        acknowledgement.Capabilities,
                        acknowledgement.PauseLeaseMilliseconds
                    });
                    return 0;
                }
                if (groundTruthOnce && type == "groundTruth")
                {
                    var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
                    if (!sample.HasFiniteCoordinates())
                    {
                        throw new InvalidDataException("Receiver sent invalid ground-truth coordinates.");
                    }
                    PrintJson(new
                    {
                        ok = true,
                        mode = "ground-truth",
                        sample.SceneName,
                        sample.UnityFrame,
                        sample.HeroAvailable,
                        sample.HeroX,
                        sample.HeroY,
                        sample.CameraAvailable,
                        sample.CameraX,
                        sample.CameraY,
                        sample.CameraZ,
                        sample.CameraTargetX,
                        sample.CameraTargetY,
                        sample.OrthographicSize,
                        sample.PixelsPerWorldUnitX,
                        sample.PixelsPerWorldUnitY,
                        sample.ProjectionPixelWidth,
                        sample.ProjectionPixelHeight,
                        sample.ScreenWidth,
                        sample.ScreenHeight
                    });
                    return 0;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                groundTruthOnce
                    ? "Timed out waiting for receiver ground truth."
                    : "Timed out waiting for receiver capabilities.");
            return 5;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Hollow Knight Vision Windows capture bootstrap

            --probe                 Locate the game window without capturing pixels.
            --receiver-probe        Verify the in-game receiver and print its capabilities.
            --ground-truth-once     Print one Hacker camera/hero telemetry sample.
            --auto-navigate-gameplay
                                    Launch Hollow Knight if needed, use the receiver to
                                    select Start Game and profile 1, then verify gameplay.
            --game-exe PATH         Explicit Hollow Knight executable for auto-navigation.
            --startup-timeout-seconds N
                                    Auto-navigation deadline (default 120).
            --infer-once RUN_DIR    Capture one frame and run a trainer-produced
                                    ONNX model on CPU.
            --model-probe RUN_DIR   Load an ONNX run and infer one blank frame;
                                    does not require or launch the game.
            --label-capture ROOT    Capture current game frame as a PNG labeling draft.
            --label-list ROOT       List saved labeling examples.
            --label-update EXAMPLE Replace an example's boxes; image remains unchanged.
            --label-delete EXAMPLE Delete one example directory.
            --label-export ROOT     Export examples to a trainer-ready dataset.
            --label-context ID      Context for label capture/update (for example: game).
            --label-box SPEC        Repeatable normalized top-origin box:
                                    CLASS,X,Y,WIDTH,HEIGHT[,negative]
            --label-known-class ID  Repeatable known-empty/exported class identifier.
            --label-capture-group ID
                                    Optional UUID linking captures that must not split.
            --label-ready           Mark a capture/update ready (eligibility uses labels).
            --dataset-root PATH     Dataset library for --label-export.
            --label-model ID        Export model identifier (default shared-object-model).
            --visual-track-seconds N
                                    CPU-only 64x36 visual camera-motion diagnostic;
                                    attaches to an already-running visible game.
            --ground-detect-once    Capture one already-running frame and print semantic
                                    ground lines using the CPU detector.
            --ground-threshold N    Ground response threshold (default 8).
            --ground-minimum-segment N
                                    Minimum semantic evidence pixels (default 40).
            --ground-line-separation N
                                    Vertical NMS distance (default 40).
            --ground-occlusion-gap N
                                    Maximum bridged horizontal gap (default 180).
            --record-visual-route DIR
                                    Save 64x36 luma plus receiver camera truth.
            --route-seconds N       Visual-route duration (default 30).
            --replay-visual-route DIR
                                    Replay saved route against camera truth; no game.
            --route-repeats N       Replay count (default 3).
            --route-mean-error N    Maximum mean camera error px (default 12).
            --route-step-error N    Maximum individual step error px (default 32).
            --route-report PATH     Optional replay report path.
            --build-route-world DIR Build a validated visual room graph from saved route
                                    evidence; receiver truth is not used for poses.
            --world-output DIR      World output (default ROUTE_DIR\world).
            --world-keyframe-interval N
                                    Accepted observations per keyframe (default 10).
            --optimize-world DIR    Atomically optimize a persisted room graph using
                                    motion and loop-closure constraints.
            --replay-input-path FILE
                                    Replay a mac-compatible recorded input path through
                                    the receiver; requires an already-running game.
            --path-repeats N        Input-path replay count (default 1).
            --skip-path-checkpoint  Do not restore the path's recorded start checkpoint.
            --record-atlas DIR      Record receiver-projected CPU atlas tiles.
            --atlas-seconds N       Atlas recording duration (default 30).
            --atlas-ppu N           Atlas pixels per world unit (default 64).
            --capture-once PATH     Capture and write the newest frame as a BMP.
            --frames N              Capture N frames; only the latest is retained (default 1).
            --interval-ms N         Delay between captures (default 133).
            --native                Keep native client size instead of CPU-normalizing to 640x360.
            --help                  Show this help.

            GDI capture is the initial visible-window fallback. Windows Graphics Capture
            remains the production path for occluded and high-frame-rate capture.
            """);
    }

    private sealed record HostOptions(
        bool ShowHelp,
        bool ProbeOnly,
        bool ReceiverProbe,
        bool GroundTruthOnce,
        bool AutoNavigateGameplay,
        string? GameExecutable,
        int StartupTimeoutSeconds,
        string? InferenceRunDirectory,
        string? ModelProbeRunDirectory,
        LabelingCommand? LabelingCommand,
        int? VisualTrackDurationSeconds,
        bool GroundDetectOnce,
        GroundTheoryTuning GroundTuning,
        string? VisualRouteOutputDirectory,
        string? VisualRouteReplayDirectory,
        string? VisualRouteWorldDirectory,
        string? WorldOutputDirectory,
        string? WorldOptimizeDirectory,
        int WorldKeyframeInterval,
        string? InputPathFile,
        int InputPathRepeatCount,
        bool RestoreInputPathCheckpoint,
        int VisualRouteDurationSeconds,
        int VisualRouteRepeatCount,
        double VisualRouteMaximumMeanError,
        double VisualRouteMaximumStepError,
        string? VisualRouteReportPath,
        string? AtlasOutputDirectory,
        int AtlasDurationSeconds,
        double AtlasPixelsPerWorldUnit,
        string? OutputPath,
        int FrameCount,
        int IntervalMilliseconds,
        bool KeepNativeSize)
    {
        internal static HostOptions Parse(string[] arguments)
        {
            var help = arguments.Length == 0;
            var probe = false;
            var receiverProbe = false;
            var groundTruthOnce = false;
            var autoNavigateGameplay = false;
            string? gameExecutable = null;
            var startupTimeoutSeconds = 120;
            string? inferenceRunDirectory = null;
            string? modelProbeRunDirectory = null;
            LabelingCommandKind? labelingKind = null;
            string? labelingTarget = null;
            string? labelingContext = null;
            var labelingBoxes = new List<LabelBox>();
            var labelingKnownClasses = new List<string>();
            Guid? labelingCaptureGroup = null;
            var labelingReady = false;
            string? datasetRoot = null;
            var labelingModel = LabelingDatasetExporter.SharedObjectModelIdentifier;
            int? visualTrackDurationSeconds = null;
            var groundDetectOnce = false;
            var groundThreshold = GroundTheoryTuning.SemanticDefault.GroundThreshold;
            var groundMinimumSegment = GroundTheoryTuning.SemanticDefault.MinimumSegmentLength;
            var groundLineSeparation = GroundTheoryTuning.SemanticDefault.LineSeparation;
            var groundOcclusionGap = GroundTheoryTuning.SemanticDefault.OcclusionMergeGap;
            string? visualRouteOutputDirectory = null;
            string? visualRouteReplayDirectory = null;
            string? visualRouteWorldDirectory = null;
            string? worldOutputDirectory = null;
            string? worldOptimizeDirectory = null;
            var worldKeyframeInterval = 10;
            string? inputPathFile = null;
            var inputPathRepeatCount = 1;
            var restoreInputPathCheckpoint = true;
            var visualRouteDurationSeconds = 30;
            var visualRouteRepeatCount = 3;
            var visualRouteMaximumMeanError = 12.0;
            var visualRouteMaximumStepError = 32.0;
            string? visualRouteReportPath = null;
            string? atlasOutputDirectory = null;
            var atlasDurationSeconds = 30;
            var atlasPixelsPerWorldUnit = 64.0;
            string? output = null;
            var frames = 1;
            var interval = 133;
            var native = false;
            for (var index = 0; index < arguments.Length; index++)
            {
                switch (arguments[index])
                {
                    case "--help" or "-h": help = true; break;
                    case "--probe": probe = true; break;
                    case "--receiver-probe": receiverProbe = true; break;
                    case "--ground-truth-once": groundTruthOnce = true; break;
                    case "--auto-navigate-gameplay": autoNavigateGameplay = true; break;
                    case "--game-exe": gameExecutable = RequiredValue(arguments, ref index); break;
                    case "--startup-timeout-seconds":
                        startupTimeoutSeconds = PositiveInt(
                            RequiredValue(arguments, ref index), "--startup-timeout-seconds");
                        break;
                    case "--infer-once":
                        inferenceRunDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--model-probe":
                        modelProbeRunDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--label-capture":
                        SetLabelingMode(
                            ref labelingKind,
                            ref labelingTarget,
                            LabelingCommandKind.Capture,
                            RequiredValue(arguments, ref index));
                        break;
                    case "--label-list":
                        SetLabelingMode(
                            ref labelingKind,
                            ref labelingTarget,
                            LabelingCommandKind.List,
                            RequiredValue(arguments, ref index));
                        break;
                    case "--label-update":
                        SetLabelingMode(
                            ref labelingKind,
                            ref labelingTarget,
                            LabelingCommandKind.Update,
                            RequiredValue(arguments, ref index));
                        break;
                    case "--label-delete":
                        SetLabelingMode(
                            ref labelingKind,
                            ref labelingTarget,
                            LabelingCommandKind.Delete,
                            RequiredValue(arguments, ref index));
                        break;
                    case "--label-export":
                        SetLabelingMode(
                            ref labelingKind,
                            ref labelingTarget,
                            LabelingCommandKind.Export,
                            RequiredValue(arguments, ref index));
                        break;
                    case "--label-context":
                        labelingContext = RequiredValue(arguments, ref index);
                        break;
                    case "--label-box":
                        labelingBoxes.Add(ParseLabelBox(RequiredValue(arguments, ref index)));
                        break;
                    case "--label-known-class":
                        labelingKnownClasses.Add(RequiredValue(arguments, ref index));
                        break;
                    case "--label-capture-group":
                        labelingCaptureGroup = GuidValue(
                            RequiredValue(arguments, ref index),
                            "--label-capture-group");
                        break;
                    case "--label-ready": labelingReady = true; break;
                    case "--dataset-root": datasetRoot = RequiredValue(arguments, ref index); break;
                    case "--label-model":
                        labelingModel = RequiredValue(arguments, ref index);
                        break;
                    case "--visual-track-seconds":
                        visualTrackDurationSeconds = PositiveInt(
                            RequiredValue(arguments, ref index),
                            "--visual-track-seconds");
                        break;
                    case "--ground-detect-once": groundDetectOnce = true; break;
                    case "--ground-threshold":
                        groundThreshold = PositiveInt(
                            RequiredValue(arguments, ref index),
                            "--ground-threshold");
                        if (groundThreshold > 255)
                        {
                            throw new ArgumentException("--ground-threshold must be at most 255.");
                        }
                        break;
                    case "--ground-minimum-segment":
                        groundMinimumSegment = PositiveInt(
                            RequiredValue(arguments, ref index),
                            "--ground-minimum-segment");
                        break;
                    case "--ground-line-separation":
                        groundLineSeparation = NonnegativeInt(
                            RequiredValue(arguments, ref index),
                            "--ground-line-separation");
                        break;
                    case "--ground-occlusion-gap":
                        groundOcclusionGap = NonnegativeInt(
                            RequiredValue(arguments, ref index),
                            "--ground-occlusion-gap");
                        break;
                    case "--record-visual-route":
                        visualRouteOutputDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--route-seconds":
                        visualRouteDurationSeconds = PositiveInt(
                            RequiredValue(arguments, ref index),
                            "--route-seconds");
                        break;
                    case "--replay-visual-route":
                        visualRouteReplayDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--route-repeats":
                        visualRouteRepeatCount = PositiveInt(
                            RequiredValue(arguments, ref index),
                            "--route-repeats");
                        break;
                    case "--route-mean-error":
                        visualRouteMaximumMeanError = PositiveDouble(
                            RequiredValue(arguments, ref index),
                            "--route-mean-error");
                        break;
                    case "--route-step-error":
                        visualRouteMaximumStepError = PositiveDouble(
                            RequiredValue(arguments, ref index),
                            "--route-step-error");
                        break;
                    case "--route-report":
                        visualRouteReportPath = RequiredValue(arguments, ref index);
                        break;
                    case "--build-route-world":
                        visualRouteWorldDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--world-output":
                        worldOutputDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--world-keyframe-interval":
                        worldKeyframeInterval = PositiveInt(
                            RequiredValue(arguments, ref index),
                            "--world-keyframe-interval");
                        break;
                    case "--optimize-world":
                        worldOptimizeDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--replay-input-path":
                        inputPathFile = RequiredValue(arguments, ref index);
                        break;
                    case "--path-repeats":
                        inputPathRepeatCount = PositiveInt(
                            RequiredValue(arguments, ref index),
                            "--path-repeats");
                        break;
                    case "--skip-path-checkpoint":
                        restoreInputPathCheckpoint = false;
                        break;
                    case "--record-atlas":
                        atlasOutputDirectory = RequiredValue(arguments, ref index);
                        break;
                    case "--atlas-seconds":
                        atlasDurationSeconds = PositiveInt(
                            RequiredValue(arguments, ref index), "--atlas-seconds");
                        break;
                    case "--atlas-ppu":
                        atlasPixelsPerWorldUnit = PositiveDouble(
                            RequiredValue(arguments, ref index), "--atlas-ppu");
                        break;
                    case "--native": native = true; break;
                    case "--capture-once": output = RequiredValue(arguments, ref index); break;
                    case "--frames": frames = PositiveInt(RequiredValue(arguments, ref index), "--frames"); break;
                    case "--interval-ms":
                        interval = NonnegativeInt(RequiredValue(arguments, ref index), "--interval-ms");
                        break;
                    default: throw new ArgumentException($"Unknown argument: {arguments[index]}");
                }
            }
            var selectedModes = (probe ? 1 : 0)
                + (receiverProbe ? 1 : 0)
                + (groundTruthOnce ? 1 : 0)
                + (autoNavigateGameplay ? 1 : 0)
                + (inferenceRunDirectory is not null ? 1 : 0)
                + (modelProbeRunDirectory is not null ? 1 : 0)
                + (labelingKind is not null ? 1 : 0)
                + (visualTrackDurationSeconds is not null ? 1 : 0)
                + (groundDetectOnce ? 1 : 0)
                + (visualRouteOutputDirectory is not null ? 1 : 0)
                + (visualRouteReplayDirectory is not null ? 1 : 0)
                + (visualRouteWorldDirectory is not null ? 1 : 0)
                + (worldOptimizeDirectory is not null ? 1 : 0)
                + (inputPathFile is not null ? 1 : 0)
                + (atlasOutputDirectory is not null ? 1 : 0)
                + (output is not null || frames != 1 ? 1 : 0);
            if (!help && selectedModes == 0)
            {
                throw new ArgumentException(
                    "Choose one probe, capture, labeling, inference, navigation, or atlas mode.");
            }
            if (!help && selectedModes > 1)
            {
                throw new ArgumentException("Choose only one probe or capture mode.");
            }
            if (visualRouteMaximumStepError < visualRouteMaximumMeanError)
            {
                throw new ArgumentException(
                    "--route-step-error must be at least --route-mean-error.");
            }
            if (worldOutputDirectory is not null && visualRouteWorldDirectory is null)
            {
                throw new ArgumentException("--world-output requires --build-route-world DIR.");
            }
            if (inputPathFile is null
                && (inputPathRepeatCount != 1 || !restoreInputPathCheckpoint))
            {
                throw new ArgumentException(
                    "--path-repeats and --skip-path-checkpoint require --replay-input-path FILE.");
            }
            if (labelingKind == LabelingCommandKind.Capture
                && string.IsNullOrWhiteSpace(labelingContext))
            {
                throw new ArgumentException("--label-capture requires --label-context ID.");
            }
            if (labelingKind == LabelingCommandKind.Export)
            {
                if (string.IsNullOrWhiteSpace(datasetRoot))
                {
                    throw new ArgumentException("--label-export requires --dataset-root PATH.");
                }
                if (labelingKnownClasses.Count == 0)
                {
                    throw new ArgumentException(
                        "--label-export requires at least one --label-known-class ID.");
                }
            }
            if (labelingKind is not (LabelingCommandKind.Capture or LabelingCommandKind.Update)
                && labelingBoxes.Count > 0)
            {
                throw new ArgumentException("--label-box is valid only for capture/update.");
            }
            var labelingCommand = labelingKind is null
                ? null
                : new LabelingCommand(
                    labelingKind.Value,
                    labelingTarget!,
                    labelingContext,
                    labelingBoxes,
                    labelingKnownClasses,
                    labelingCaptureGroup,
                    labelingReady,
                    datasetRoot,
                    labelingModel);
            return new HostOptions(
                help, probe, receiverProbe, groundTruthOnce, autoNavigateGameplay,
                gameExecutable, startupTimeoutSeconds, inferenceRunDirectory,
                modelProbeRunDirectory, labelingCommand, visualTrackDurationSeconds,
                groundDetectOnce,
                new GroundTheoryTuning(
                    groundThreshold,
                    groundMinimumSegment,
                    groundLineSeparation,
                    groundOcclusionGap),
                visualRouteOutputDirectory, visualRouteReplayDirectory,
                visualRouteWorldDirectory, worldOutputDirectory, worldOptimizeDirectory,
                worldKeyframeInterval, inputPathFile, inputPathRepeatCount,
                restoreInputPathCheckpoint,
                visualRouteDurationSeconds, visualRouteRepeatCount,
                visualRouteMaximumMeanError, visualRouteMaximumStepError,
                visualRouteReportPath,
                atlasOutputDirectory, atlasDurationSeconds,
                atlasPixelsPerWorldUnit, output, frames, interval, native);
        }

        private static string RequiredValue(string[] arguments, ref int index)
        {
            if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            {
                throw new ArgumentException("Missing argument value.");
            }
            return arguments[index];
        }

        private static int PositiveInt(string value, string option) =>
            int.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{option} requires a positive integer.");

        private static int NonnegativeInt(string value, string option) =>
            int.TryParse(value, out var parsed) && parsed >= 0
                ? parsed
                : throw new ArgumentException($"{option} requires a nonnegative integer.");

        private static Guid GuidValue(string value, string option) =>
            Guid.TryParse(value, out var parsed)
                ? parsed
                : throw new ArgumentException($"{option} requires a UUID.");

        private static void SetLabelingMode(
            ref LabelingCommandKind? kind,
            ref string? target,
            LabelingCommandKind nextKind,
            string nextTarget)
        {
            if (kind is not null) throw new ArgumentException("Choose one labeling mode.");
            kind = nextKind;
            target = nextTarget;
        }

        private static LabelBox ParseLabelBox(string value)
        {
            var fields = value.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length is not (5 or 6) || string.IsNullOrWhiteSpace(fields[0]))
            {
                throw new ArgumentException(
                    "--label-box requires CLASS,X,Y,WIDTH,HEIGHT[,negative].");
            }
            var values = fields.Skip(1).Take(4).Select(field =>
                double.TryParse(
                    field,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : throw new ArgumentException("--label-box coordinates must be numbers."))
                .ToArray();
            var negative = fields.Length == 6
                && fields[5].Equals("negative", StringComparison.OrdinalIgnoreCase);
            if (fields.Length == 6 && !negative)
            {
                throw new ArgumentException("--label-box sixth field must be 'negative'.");
            }
            var box = new LabelBox(fields[0], values[0], values[1], values[2], values[3], negative);
            new LabelingExampleAnnotation(
                Guid.Empty,
                box.ClassIdentifier,
                box.X,
                box.Y,
                box.Width,
                box.Height,
                box.IsNegative ? true : null).Validate();
            return box;
        }

        private static double PositiveDouble(string value, string option) =>
            double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            && double.IsFinite(parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{option} requires a positive number.");
    }
}
