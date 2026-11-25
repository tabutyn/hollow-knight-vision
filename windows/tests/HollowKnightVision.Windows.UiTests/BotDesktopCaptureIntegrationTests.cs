using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using Line = System.Windows.Shapes.Line;

internal static class BotDesktopCaptureIntegrationTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Generation = 45;

    internal static void Run(BgraFrame hudFrame, IReadOnlyList<LabelingExampleAnnotation> health)
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-desktop-bot-capture-");
        try
        {
            var root = temporary.FullName;
            var examples = new LabelingExampleStore(Path.Combine(root, "labeling-v1", "examples"));
            _ = examples.Save(hudFrame, "gameplay-hud", health, languageIdentifier: "en");
            var auditDirectory = Path.Combine(root, "ground-audit");
            var audits = GroundFrameAudits.Launch(["--ground-audit-directory=" + auditDirectory,
                "--ground-raw-audit-directory=" + auditDirectory]);
            double availableAt = 0;
            var window = new MainWindow(GameRuntimeMode.Modded, gamePath: null, root: root, documents: root, groundAudits: audits)
            {
                ReadBotObservationTime = () => availableAt
            };
            try
            {
                VerifyDesktopRecording(window, hudFrame, Path.Combine(root, "demonstrations"), time => availableAt = time);
                Wait(VerifyTimedClipAsync(window, root));
                VerifyMatchedHackerRecording(window, Path.Combine(root, "paired-hacker"));
                VerifyHackerVisualEvidence(window, hudFrame, Path.Combine(root, "hacker-evaluation"));
            }
            finally
            {
                Field("isClosing").SetValue(window, true);
                Wait((Task)Method("PrepareShutdownAsync").Invoke(window, null)!);
            }
            var auditPath = Directory.EnumerateFiles(auditDirectory, "*.json").Order().First();
            using var audit = JsonDocument.Parse(File.ReadAllBytes(auditPath));
            var rawPath = Path.Combine(auditDirectory, "raw", Path.GetFileName(auditPath));
            using var rawAudit = JsonDocument.Parse(File.ReadAllBytes(rawPath));
            if (audit.RootElement.GetProperty("generation").GetInt64() != Generation
                || audit.RootElement.GetProperty("captureId").GetInt64() != rawAudit.RootElement.GetProperty("captureId").GetInt64()
                || !File.Exists(Path.ChangeExtension(auditPath, ".png")) || !File.Exists(Path.ChangeExtension(rawPath, ".luma")))
                throw new InvalidOperationException("The live diagnostics must retain a complete frame from the desktop capture generation.");
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        Console.WriteLine("PASS Desktop analysis captures room-owned doors and preserves wall evidence across a correction");
    }

    private static void VerifyMatchedHackerRecording(MainWindow window, string library)
    {
        const double start = 200;
        const double capturedAt = start + .02;
        const int marker = 2;
        var id = Guid.NewGuid();
        var labels = new GroundTruthLineDocument
        {
            Lines = [new("Tutorial_01", -30, 30, -6, GroundTruthLineKind.Positive, false)],
            Walls = [new("Tutorial_01", -8, -8, -3, GroundTruthLineKind.Wall, false) { EndWorldY = 8 }]
        };
        var recording = new BotCaptureSession(library, "modded", "visual", Generation, start, labels,
            id, paired: true, observationClock: () => capturedAt + .01);
        var recorder = new InputPathRecorder();
        recorder.Start(start, id: id);
        recorder.RecordInput(RecordedGameButton.Right, true, start);
        var path = recorder.Stop(start + 1);
        var withoutHud = new BgraFrame(80, 48, 320, new byte[80 * 48 * 4], marker,
            DateTimeOffset.UnixEpoch.AddSeconds(capturedAt));
        var sample = new ReceiverGroundTruthSample(2, "groundTruth", Guid.NewGuid(), marker, marker,
            capturedAt, "Tutorial_01", true, 0, 0, 0, 0, 0, true, true, true, 0, 0, 0, 0, 0, 0,
            24, 2, 2, 80, 36, 160, 96, 160, 96)
        {
            AtlasGameplayActive = true,
            Actors = new(1, new(1, "Knight", 80, 36, 10, 12), [])
        };
        var match = new HackerMatchedFrame(new(marker, withoutHud, capturedAt, IntegratesAtlas: false,
            MovementButtons: InputButtons.Right), sample, capturedAt + .005);
        var finished = false;
        Field("botCapture").SetValue(window, recording);
        try
        {
            SubmitReceiverMatch(window, Generation, match);
            finished = true;
            var result = recording.FinishAsync(start + 1, path, completed: true).GetAwaiter().GetResult();
            Check(result is { Published: [{ BotDomain: "hacker", ObservationCount: 1, PairedClipId: null }], Discarded: 1 },
                "The live receiver join should publish Hacker even when no Game Play observation completed.");
            var saved = BotRecordedAtlasPath.ReadCapture(Path.Combine(library, BotCaptureSession.DirectoryName(id))).Observations.Single();
            Check(saved.CaptureTimestamp == capturedAt && saved.DetectionFrameId == marker && saved.DetectedTerrain is null
                && saved.AuthoredTerrain is { Floors.Count: 1, Walls.Count: 1 },
                "The clip should keep the authored terrain from the exact received frame.");
            Console.WriteLine("PASS Desktop records matched Hacker frames without a visual analysis result");
        }
        finally
        {
            Field("botCapture").SetValue(window, null);
            if (!finished) _ = recording.FinishAsync(start + 1, path, completed: false).GetAwaiter().GetResult();
        }
    }

    internal static void SubmitReceiverMatch(MainWindow window, long generation, HackerMatchedFrame match)
    {
        // The joined frame is available before the test supplies any visual analysis.
        var pendingFrame = typeof(MainWindow).GetNestedType("PendingVisionFrame", BindingFlags.NonPublic)!;
        var factory = typeof(Channel).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "CreateUnbounded" && method.GetParameters().Length == 0);
        var analysis = factory.MakeGenericMethod(pendingFrame).Invoke(null, null)!;
        var analysisWriter = analysis.GetType().GetProperty("Writer")!.GetValue(analysis);
        Field("receiverFrameJoinGeneration").SetValue(window, generation);
        _ = Method("SubmitReceiverFrameMatch").Invoke(window, [analysisWriter, match, 0L, false, 0L]);
    }

    private static async Task VerifyTimedClipAsync(MainWindow window, string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        const double start = 30;
        var rawId = Guid.NewGuid();
        var stillId = Guid.NewGuid();
        var paths = Path.Combine(root, "HollowKnightVision", "paths");
        var captures = Path.Combine(root, "bot-v1", "demonstrations");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var archive = new VisualReplayArchiveWriter(paths, rawId, new Dictionary<string, string>());
        archive.Append(FrameWithPixel(10), start + .02, start + .02, Generation, 80, null);
        archive.Append(FrameWithPixel(20), start + .24, start + .24, Generation, 80, null);
        archive.Append(FrameWithPixel(30), start + .52, start + .52, Generation, 80, null);
        archive.Append(FrameWithPixel(40), start + .9, start + .9, Generation, 80, null);
        await archive.FinishAsync();

        SaveClip(rawId, rawId, "hacker", [.16, .42, .78]);
        SaveClip(stillId, null, "gameplay", [.2]);
        var clips = (DataGrid)window.FindName("BotCaptureClips");
        var image = (Image)window.FindName("BotPreviewImage");
        var canvas = (Canvas)window.FindName("BotPreviewOverlay");
        var position = (Slider)window.FindName("BotClipScrubber");
        var play = (Button)window.FindName("BotPlayClip");
        var previous = (Button)window.FindName("BotPreviousFrame");
        var inputs = (TextBlock)window.FindName("BotClipInputs");
        var route = (TextBlock)window.FindName("BotClipRouteLegend");
        var blockade = (TextBlock)window.FindName("BotClipBlockadeLegend");
        var issue = (TextBlock)window.FindName("BotClipFrameIssue");

        _ = Method("RefreshBotLibrary").Invoke(window, null);
        await WaitFor(() => HasPixel(10));
        Check(canvas.Children.Count == 0 && position.Maximum == 1 && play.IsEnabled
            && inputs.Text == "Recorded input · Right" && route.Visibility == Visibility.Collapsed,
            "The archive starts before the first observation and must show original pixels without future terrain.");
        position.Value = .55;
        await WaitFor(() => HasPixel(30));
        Check(canvas.Children.OfType<Line>().Single() is { Y1: 32 } floor && floor.Stroke == Brushes.SpringGreen
            && inputs.Text == "Recorded input · Released" && route.Visibility == Visibility.Collapsed
            && blockade.Visibility == Visibility.Collapsed,
            "The paused image must show the correct Hacker observation for that time.");
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => HasPixel(20));
        var lines = canvas.Children.OfType<Line>().ToArray();
        Check(lines.Single(line => line.Stroke == Brushes.SpringGreen).Y1 == 36 && inputs.Text == "Recorded input · Right + X",
            "Frame stepping must update terrain and held buttons with the image.");
        Check(lines.Count(line => line.Stroke == Brushes.MediumOrchid) == 3
            && lines.Any(line => line.Stroke == Brushes.MediumOrchid && line.X1 == 40 && line.X2 > 49)
            && lines.Count(line => line.Stroke == Brushes.Orange) == 2
            && blockade.Text == "Orange marker · Blocked moving right" && route.Visibility == Visibility.Visible,
            "The exact frame should show the recorded route arrow and rightward wall contact.");
        Check(canvas.Children.OfType<System.Windows.Shapes.Ellipse>().Count() == 2,
            "Geo and Lifeblood targets should use their saved positions on the image.");

        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => HasPixel(30));
        Check((string)play.Content == "Pause", "The playhead must advance on the original timestamps.");
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var stoppedAt = position.Value;
        await Task.Delay(90, timeout.Token);
        Check(position.Value == stoppedAt && (string)play.Content == "Play", "Pause should stop the clock.");

        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        clips.SelectedIndex = 1;
        await WaitFor(() => HasPixel(90));
        Check(!play.IsEnabled && (string)play.Content == "Play"
            && canvas.Children.OfType<Line>().Single().Stroke == Brushes.DeepSkyBlue
            && route.Visibility == Visibility.Collapsed && blockade.Visibility == Visibility.Collapsed
            && issue.Text == "Excluded frame · stale-or-missing-detection",
            "Changing clips should stop playback, clear the old image, and retain a legacy Game Play still.");
        Console.WriteLine("PASS Desktop clip annotations follow the displayed image and clear across clips");
        Console.WriteLine("PASS Desktop bot review plays raw clip frames with synchronized overlays and legacy still fallback");

        var delete = (Button)window.FindName("BotDeleteClip");
        var confirmation = (FrameworkElement)window.FindName("BotDeleteClipConfirmation");
        var confirm = (Button)window.FindName("BotConfirmDeleteClip");
        var cancel = (Button)window.FindName("BotCancelDeleteClip");
        var summary = (TextBlock)window.FindName("BotCaptureSummary");
        var stillFolder = Path.Combine(captures, BotCaptureSession.DirectoryName(stillId));
        var rawFolder = Path.Combine(captures, BotCaptureSession.DirectoryName(rawId));
        var rawPath = Path.Combine(paths, $"path-fixture-{rawId:D}.json");
        var rawFrames = Path.Combine(paths, $"frames-{rawId:D}");
        File.WriteAllText(rawPath, "recorded inputs");
        using (var training = new CancellationTokenSource())
        {
            Field("botTrainingCancellation").SetValue(window, training);
            _ = Method("UpdateBotTrainingControls").Invoke(window, null);
            Check(!delete.IsEnabled, "Clip deletion must be disabled while either domain is training.");
            Field("botTrainingCancellation").SetValue(window, null);
            _ = Method("UpdateBotTrainingControls").Invoke(window, null);
        }
        Click(delete);
        Check(confirmation.Visibility == Visibility.Visible && Directory.Exists(stillFolder), "Delete Clip must ask first.");
        Click(cancel);
        Click(confirm);
        Check(confirmation.Visibility == Visibility.Collapsed && Directory.Exists(stillFolder), "Cancel must preserve the clip.");
        Click(delete);
        Click(confirm);
        await WaitFor(() => HasPixel(10));
        Check(!Directory.Exists(stillFolder) && File.Exists(rawPath) && Directory.Exists(rawFrames)
            && summary.Text == "1 recorded runs · 1 Hacker clips · 0 Game Play clips",
            "Deleting a still must keep the other run selected and refresh the counts.");
        Click(delete);
        Click(confirm);
        await WaitFor(() => clips.Items.Count == 0);
        Check(!Directory.Exists(rawFolder) && !File.Exists(rawPath) && !Directory.Exists(rawFrames)
            && image.Source is null && !delete.IsEnabled,
            "Confirming removal of the last clip must clear the library, preview, and recorded replay.");
        Console.WriteLine("PASS Desktop clip deletion requires confirmation, respects training, and refreshes the library");

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        bool HasPixel(byte blue)
        {
            if (image.Source is not BitmapSource bitmap) return false;
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            return pixel[0] == blue;
        }

        async Task WaitFor(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(15, timeout.Token);
        }

        void SaveClip(Guid id, Guid? replay, string domain, double[] sampleTimes)
        {
            var folder = Directory.CreateDirectory(Path.Combine(captures, BotCaptureSession.DirectoryName(id))).FullName;
            var date = DateTimeOffset.UnixEpoch.AddSeconds(replay.HasValue ? 2 : 1);
            BotCaptureInput[] inputRows = replay.HasValue
                ? [new(start + .01, "right", true), new(start + .18, "x", true), new(start + .3, "x", false), new(start + .45, "right", false)] : [];
            var manifest = new BotCaptureManifest(BotRecordingContract.SchemaVersion, id, date, start, "modded", "fixture",
                "receiver-sent", "completed", 1, sampleTimes.Length, sampleTimes.Length, inputRows.Length, replay.HasValue ? 0 : 1, domain, replay, null);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, json));
            if (!replay.HasValue) PngWriter.Write(FrameWithPixel(90), Path.Combine(folder, "still.png"));
            var rows = sampleTimes.Select((time, index) =>
            {
                var terrain = new BotScreenTerrain(80, 48, [new(5, 70, 12 + index * 4, 1)], []);
                var authored = domain == "hacker";
                var annotated = authored && index == 0;
                var features = new double[BotRecordingContract.FeatureNames.Count];
                if (annotated) { features[0] = .5; features[1] = .25; features[4] = 1; }
                return new BotCaptureObservation(start + time, start + time, start + time, Generation, 1, 0,
                    features, authored ? [] : ["stale-or-missing-detection"], authored ? "human-authored" : "detected")
                {
                    AuthoredTerrain = authored ? terrain : null,
                    DetectedTerrain = authored ? null : terrain,
                    HackerPathVersion = authored ? 1 : null,
                    RoadHint = annotated ? new(1, 0, .2, .3, false) : null,
                    Blockade = annotated ? new(new("room", 0, 0, 1, 80, false), .14, 0) : null,
                    GeoCaches = annotated ? [new(.3, .2, .06, .08)] : null,
                    LifebloodCocoons = annotated ? [new(.4, .3, .08, .09)] : null,
                    ImagePath = replay.HasValue ? null : "still.png"
                };
            });
            File.WriteAllLines(Path.Combine(folder, "events.jsonl"), rows.Select(row => JsonSerializer.Serialize(row, json))
                .Concat(inputRows.Select(row => JsonSerializer.Serialize(row, json))));
        }

        static BgraFrame FrameWithPixel(byte blue)
        {
            var pixels = new byte[80 * 48 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = blue;
                pixels[index + 3] = 255;
            }
            return new(80, 48, 320, pixels, blue, DateTimeOffset.UnixEpoch);
        }
    }

    private static void VerifyDesktopRecording(MainWindow window, BgraFrame hudFrame, string library, Action<double> setAvailableAt)
    {
        const double startedAt = 10;
        const double sampleSpacing = .12;
        const double processingDelay = .015;
        const double stoppedAt = startedAt + 1.5;
        var lastCaptureAt = startedAt;
        var capture = new BotCaptureSession(library, "normal", "visual", Generation, startedAt, new());
        Field("botCapture").SetValue(window, capture);
        var finished = false;
        try
        {
            VerifyGameplayAdmission(window, capture, hudFrame);

            var scene = FrameWithScenery(hudFrame);
            var shift = new AtlasPoint(40, -16);
            _ = Step(10, .50, InputButtons.Right, 2, default, GroundHypothesisTrackingResult.Empty);
            _ = Step(11, .50, InputButtons.Right, 2, default, GroundHypothesisTrackingResult.Empty);
            var before = Step(12, .50, InputButtons.Right, 2, default, GroundHypothesisTrackingResult.Empty);
            Check(Field("gameplayWallContacts").GetValue(window) is MainWindow.BotWallContactsFrame
                { CaptureGeneration: Generation, CaptureId: 12, PlacedRoom: 19, Marks.Count: 1 },
                "The live overlay must receive the measured wall from the same desktop observation that the bot records.");

            var correction = GroundHypothesisTrackingResult.Empty with
            {
                PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 4,
                LocalReferenceTimestamp = before, LocalReferencePosition = default(CameraVector),
                LocalMatchedImageDisplacement = default(CameraVector), GlobalCorrection = new(shift.X, shift.Y)
            };
            // This capture belongs to the solver, but falls inside the clip's sampling interval.
            var betweenSamples = before + .03;
            Check(!capture.WantsFrame(Generation, betweenSamples), "The correction must arrive between clip samples.");
            _ = Step(13, .50, InputButtons.Right, 3, shift, correction, betweenSamples);
            var afterCorrection = before + .125;
            _ = Step(14, .50, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty, afterCorrection);
            _ = Step(15, .56, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty);
            Check(Field("gameplayWallContacts").GetValue(window) is null,
                "The desktop must remove a wall contact from the live overlay after a measured crossing.");
            _ = Step(16, .50, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty);
            _ = Step(17, .50, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty);
            _ = Step(18, .50, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty);

            finished = true;
            var published = capture.FinishAsync(stoppedAt, null, completed: true).GetAwaiter().GetResult();
            var clip = published.Published.Single();
            Check(clip.BotDomain == "gameplay" && clip.ObservationCount == 8, "The desktop path must save all eight admitted samples.");
            var path = Path.Combine(library, BotCaptureSession.DirectoryName(clip.Id), "events.jsonl");
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var rows = File.ReadLines(path).Select(line => JsonSerializer.Deserialize<BotCaptureObservation>(line, json)!).ToArray();
            Check(rows.All(row => Math.Abs(row.AvailableAt - row.CaptureTimestamp - processingDelay) < 1e-9
                && !row.Issues.Contains("stale-capture")), "The fixture must preserve the intended capture delay.");
            Check(rows.Select(row => row.DetectionFrameId).SequenceEqual([10L, 11L, 12L, 14L, 15L, 16L, 17L, 18L]),
                "The unsampled correction frame must stay out of the clip.");
            var expectedDoor = new BotLine(16, 10, 16, 40);
            Check(rows.Take(5).All(row => row.DetectedTerrain?.Doors?.SequenceEqual([expectedDoor]) == true)
                && rows[5].DetectedTerrain?.Doors is null && rows[6].DetectedTerrain?.Doors?.SequenceEqual([expectedDoor]) == true
                && rows.All(row => row.AuthoredTerrain is null),
                "The clip must keep its room's visual door through a correction and continued scenery, and omit it without a measured camera.");
            var expectedFloor = new BotFloor(20, 101, hudFrame.Height - 100, 1);
            Check(rows.Take(5).All(row => row.Grounded == true && row.DetectedTerrain?.Floors.SequenceEqual([expectedFloor]) == true)
                && rows[5].DetectedTerrain?.Floors.Any(floor => floor.MinimumX == 20) == false,
                "The saved Game Play observation must use the walked row and reject its competing edge, without projecting unverified ground.");
            Check(rows.Skip(6).All(row => row.Grounded == true && row.DetectedTerrain?.Floors.SequenceEqual([expectedFloor]) == true
                    && row.VisualContext is { CameraX: null, CameraY: null } && row.AtlasView is { CoveredCells: > 0 }),
                "Measured scenery should keep walked floor and both Atlas storage paths' coverage without adding a ground-tracker camera to the clip.");
            var oldWall = rows[2].Blockade;
            var translatedWall = rows[3].Blockade;
            Check(rows.Take(2).All(row => row.Blockade is null)
                && oldWall is { Mark.Direction: 1 } && translatedWall is { Mark.Direction: 1 }
                && translatedWall.Mark == oldWall.Mark
                && Math.Abs(translatedWall.Dx - oldWall.Dx) < 1e-8
                && Math.Abs(translatedWall.Dy - oldWall.Dy) < 1e-8
                && rows[3].BlockadePose is { } newPose && newPose.Space != oldWall.Mark.Space
                && newPose.Width == hudFrame.Width
                && rows[3].VisualContext is { CameraX: null, CameraY: null },
                "A clip frame in a different measured island must keep the original wall and its projected relative distance.");
            Check(rows.Skip(4).All(row => row.Blockade is null), "Walking through the wall must retire it in the clip.");
            var beforeCoverage = rows[2].AtlasView;
            var afterCoverage = rows[3].AtlasView;
            Check(rows[0].AtlasView is { EarnedCells: 0 }
                && beforeCoverage is { EarnedCells: > 0 } && afterCoverage is not null
                && afterCoverage.EarnedCells == beforeCoverage.EarnedCells
                && afterCoverage.CoveredCells == beforeCoverage.CoveredCells
                && Math.Abs(afterCoverage.Camera[0] - beforeCoverage.Camera[0] - 640) < 1e-8,
                "A saved visual clip must retain its committed coverage across a camera correction without duplicate credit.");

            double Step(long id, double knightX, InputButtons input, long epoch, AtlasPoint camera,
                GroundHypothesisTrackingResult ground, double? timestamp = null)
            {
                var observedAt = DateTimeOffset.UnixEpoch.AddSeconds(id);
                var frame = new BgraFrame(hudFrame.Width, hudFrame.Height, hudFrame.Stride,
                    scene, id, observedAt);
                var knight = new ObjectDetection("game.playable-knight", new(knightX - .02, .50, .04, .1), .95f)
                    { SourceFrameIdentifier = id };
                var batch = new LiveObjectDetectionBatch([knight], id, id, Generation, 0);
                var captureAt = timestamp ?? lastCaptureAt + sampleSpacing;
                lastCaptureAt = captureAt;
                const int committedRoom = 19;
                var center = new AtlasPoint(frame.Width / 2d, frame.Height / 2d);
                double[] offsets = id switch
                {
                    <= 10 => [0],
                    11 => [0, 128],
                    12 => [0, 128, 256],
                    _ => [640, 768, 896, 640]
                };
                var firstBasis = new AtlasPoint(offsets[0], 0);
                var secondBasis = new AtlasPoint(firstBasis.X - shift.X, -shift.Y);
                var observations = offsets.Select((offset, index) =>
                {
                    var pose = center with { X = center.X + offset };
                    var basis = index < 3 ? firstBasis : secondBasis;
                    return LiveWorldObservation.Create(index, index, index + 1, frame.Width, frame.Height,
                        frame.Width, 160, [], new(pose.X - basis.X, pose.Y - basis.Y), pose, roomId: committedRoom);
                }).ToArray();
                var origin = observations[0].OptimizedPose;
                var firstIsland = new LiveTrackingIsland(0, committedRoom, 1, firstBasis,
                    LiveIslandPlacement.Reference, default, "test",
                    observations.Take(3).Select(row => new LiveIslandMember(row.Id, row.LocalPose, true)).ToArray());
                LiveTrackingIsland[] islands = observations.Length <= 3 ? [firstIsland] :
                    [firstIsland, new(1, committedRoom, 4, secondBasis, LiveIslandPlacement.Connected, default, "test",
                        [new(3, observations[3].LocalPose, true)])];
                var graph = new LiveWorldSnapshot(mapRevision: (int)id, anchoredObservationId: 0,
                    observations: observations, trackingIslands: islands);
                var floor = new AtlasPoint(16 + offsets[0], 10);
                var doors = LiveWorldPlacementProjection.Empty with
                {
                    RoomDoorways = new Dictionary<int, IReadOnlyList<LiveWorldDoorwayStroke>>
                    {
                        [committedRoom] = [new(floor, new(floor.X, 40))],
                        [committedRoom + 1] = [new(new(floor.X + 5, 12), new(floor.X + 5, 30))]
                    }
                };
                var world = new MainWindow.CommittedBotFrame(graph, committedRoom, id == 16 ? null : origin)
                {
                    DoorPlacement = doors,
                    MeasuredOwner = id == 16 ? null : new(islands[^1].Id, islands[^1].WorldFromLocal, true)
                };
                var walkedY = camera.Y + frame.Height - 100;
                GroundHypothesisAtlasLine[] walked = [new(WalkedGroundMemory.WalkedSegmentId,
                    new(camera.X + 20, walkedY), new(camera.X + 80, walkedY))];
                var usesDurableWorld = id < 18;
                var groundCamera = id == 14 || id >= 16 ? (AtlasPoint?)null : camera;
                var sceneryCamera = id == 14 || id >= 17 ? camera : (AtlasPoint?)null;
                // Keep sample spacing and observation age independent of CI scheduling.
                setAvailableAt(captureAt + processingDelay);
                _ = Method("CaptureBotObservations").Invoke(window, [
                    frame, Generation, Generation, captureAt, 17, 1UL, null, null, batch,
                    new[] { new DetectedFloorLine(101, new(70, 100)), new DetectedFloorLine(112, new(30, 60)) },
                    TerrainWallDetection.Empty, true, groundCamera, null, new AtlasPoint(42, 100), input,
                    null, epoch, ground, !usesDurableWorld, Array.Empty<PixelRect>(), null, usesDurableWorld,
                    usesDurableWorld ? world : null, walked, sceneryCamera, null, null, false
                ]);
                return captureAt;
            }
        }
        finally
        {
            Field("botCapture").SetValue(window, null);
            if (!finished) _ = capture.FinishAsync(stoppedAt, null, completed: false).GetAwaiter().GetResult();
        }
    }

    private static void VerifyGameplayAdmission(MainWindow window, BotCaptureSession capture, BgraFrame hudFrame)
    {
        var pixels = FrameWithScenery(hudFrame);
        var recognizer = (WindowsMenuStencilRecognizer)Field("menuStencilRecognizer").GetValue(window)!;
        var firstFrame = new BgraFrame(hudFrame.Width, hudFrame.Height, hudFrame.Stride, pixels, 0,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var initial = recognizer.Analyze(firstFrame, false,
            () => [new ObjectDetection("game.playable-knight", new(.48, .62, .04, .16), .95f)]);
        Check(initial.HasFreshGameplayEvidence, "The captured HUD and the fixture's Knight must establish Gameplay.");

        var admission = new GameplayFrameAdmission();
        var tracking = (GameplayTrackingPipeline)Method("CreateLiveTrackingPipeline").Invoke(window, null)!;
        for (var index = 1; index <= 6; index++)
        {
            var time = 1 + index * .2;
            var frame = new BgraFrame(hudFrame.Width, hudFrame.Height, hudFrame.Stride, pixels, index,
                DateTimeOffset.UnixEpoch.AddSeconds(time));
            _ = Method("AnalyzeVisionFrame").Invoke(window, [
                frame, null, admission, Generation, 0L, time, null, null, false, false, 0L, InputButtons.None, false, tracking
            ]);
            if (ReferenceEquals(Field("botRouteOwner").GetValue(window), capture)) return;
        }
        throw new InvalidOperationException("Established Gameplay did not reach the desktop bot capture path.");
    }

    private static void VerifyHackerVisualEvidence(MainWindow window, BgraFrame hudFrame, string library)
    {
        const string scene = "Tutorial_01";
        const double start = 300;
        const double matchedAt = start + 1.25;
        const double unpairedAt = start + 1.5;
        const int marker = 800;
        var pixels = FrameWithScenery(hudFrame);
        BgraFrame Frame(int id, double timestamp) => new(hudFrame.Width, hudFrame.Height, hudFrame.Stride,
            pixels, id, DateTimeOffset.UnixEpoch.AddSeconds(timestamp));

        var recognizer = (WindowsMenuStencilRecognizer)Field("menuStencilRecognizer").GetValue(window)!;
        var initial = recognizer.Analyze(Frame(marker - 6, start), false,
            () => [new ObjectDetection("game.playable-knight", new(.48, .62, .04, .16), .95f)]);
        Check(initial.HasFreshGameplayEvidence, "The captured HUD must admit terrain analysis for the Hacker evaluation.");

        var labels = new GroundTruthLineDocument { Lines = [new(scene, -2, 4, 0, GroundTruthLineKind.Positive, false)] };
        var evaluation = new BotEvaluationRecorder(library, BotCaptureDomain.Hacker, "Hacker terrain audit",
            null, labels, Generation, start);
        var playback = Field("botPlayback");
        var run = Activator.CreateInstance(playback.FieldType, Guid.NewGuid(), BotCaptureDomain.Hacker,
            Generation, start, labels, null)!;
        playback.FieldType.GetField("UsesReceiverFrames", InstanceMembers)!.SetValue(run, true);
        playback.FieldType.GetField("Evaluation", InstanceMembers)!.SetValue(run, evaluation);
        Field("workspace").SetValue(window, "Hacker");
        playback.SetValue(window, run);
        try
        {
            // The receiver playback test independently covers queueing exact captures and driving the policy.
            // Supply its two accepted samples here to isolate their join with the desktop visual worker.
            var terrain = new BotScreenTerrain(hudFrame.Width, hudFrame.Height, [new(20, 170, 100, 1)], []);
            RecordAcceptedFrame(marker, matchedAt);
            RecordAcceptedFrame(marker + 1, unpairedAt);
            var admission = new GameplayFrameAdmission();
            var tracking = (GameplayTrackingPipeline)Method("CreateLiveTrackingPipeline").Invoke(window, null)!;
            for (var index = 1; index <= 5; index++)
            {
                var time = start + index * .25;
                var id = marker - 5 + index;
                _ = Method("AnalyzeVisionFrame").Invoke(window, [
                    Frame(id, time), null, admission, Generation, 0L, time, id, null, false, false, 0L,
                    InputButtons.Right, time == matchedAt, tracking
                ]);
            }

            var directory = evaluation.FinishAsync(start + 2, "user-stop").GetAwaiter().GetResult()!;
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var manifest = JsonSerializer.Deserialize<BotCaptureManifest>(File.ReadAllBytes(Path.Combine(directory, "manifest.json")), json)!;
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "evaluation.json")));
            var source = metadata.RootElement.GetProperty("visualFrameArchive").Deserialize<RecordedVisualFrameArchive>(json)!;
            var archive = new VisualReplayArchiveReader(directory, source, manifest.Id).Manifest;
            Check(manifest is { ObservationCount: 2, TerrainPairedFrameCount: 1 }
                && archive.Frames.Select(frame => frame.RenderedGameFrame).SequenceEqual([marker, marker + 1])
                && archive.StageEvents.Where(stage => stage.Stage == "visionCompleted").Select(stage => stage.CaptureTimestamp)
                    .SequenceEqual([matchedAt]), "Only the exact analyzed Hacker sample should receive visual terrain evidence.");
            Check(archive.GroundInputs.Single() is
                { CaptureTimestamp: matchedAt, CaptureGeneration: Generation, WindowsMovementButtons: InputButtons.Right,
                    HorizontalDrive: 1, RecentJump: true, ExcludingRects.Length: > 0 },
                "The Hacker evaluation must keep the desktop analysis' movement, recent jump and exclusion masks.");

            void RecordAcceptedFrame(int id, double time)
            {
                var row = new BotCaptureObservation(time, time, time + .01, Generation, 1, 0,
                    new double[BotRecordingContract.FeatureNames.Count], [], "human-authored") { AuthoredTerrain = terrain };
                evaluation.RecordFrame(Frame(id, time), new(time, row.AvailableAt, Generation, 1, 0), row,
                    scene, [], terrain, null, id, awaitHackerVisual: true);
            }
        }
        finally
        {
            playback.SetValue(window, null);
            _ = evaluation.FinishAsync(start + 2, "user-stop").GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS Desktop Hacker evaluation preserves the exact frame's visual terrain and recent jump evidence");
    }

    private static byte[] FrameWithScenery(BgraFrame hudFrame)
    {
        var scene = hudFrame.Pixels.ToArray();
        for (var y = hudFrame.Height / 2; y < hudFrame.Height; y++)
        for (var x = 0; x < hudFrame.Width; x++)
        {
            var i = y * hudFrame.Stride + x * 4;
            scene[i] = (byte)(40 + x % 73);
            scene[i + 1] = (byte)(60 + y % 101);
            scene[i + 2] = 73;
            scene[i + 3] = 255;
        }
        return scene;
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, InstanceMembers)
        ?? throw new MissingFieldException(typeof(MainWindow).Name, name);

    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, InstanceMembers)
        ?? throw new MissingMethodException(typeof(MainWindow).Name, name);

    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(25) };
            timer.Tick += (_, _) => frame.Continue = false;
            task.GetAwaiter().OnCompleted(() => frame.Continue = false);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        Check(task.IsCompleted, "The desktop capture fixture did not finish shutting down.");
        task.GetAwaiter().GetResult();
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
