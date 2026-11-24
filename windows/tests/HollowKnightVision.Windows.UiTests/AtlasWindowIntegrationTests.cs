extern alias VisionApp;

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using WindowsLiveWorldFrame = VisionApp::HollowKnightVision.Windows.App.WindowsLiveWorldFrame;
using WindowsLiveWorldPipelineResult = VisionApp::HollowKnightVision.Windows.App.WindowsLiveWorldPipelineResult;
using WindowsLiveWorldPipelineState = VisionApp::HollowKnightVision.Windows.App.WindowsLiveWorldPipelineState;

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
            VerifyGameplayWorldHandoff(Directory.CreateDirectory(Path.Combine(temporary.FullName, "handoff")).FullName);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        Console.WriteLine("PASS Atlas window saves, restores and deletes scoped snapshots and switches both playthroughs");
        Console.WriteLine("PASS Hacker Atlas draws restored rooms at the measured passage elevation");
        Console.WriteLine("PASS Gameplay Atlas draws measured doorways independently of optional outlines and hides them outside gameplay");
        Console.WriteLine("PASS Gameplay window applies only ordered Atlas placements and keeps menu images in capture coordinates");
        Console.WriteLine("PASS Game Play toggles ground lines and tracking patches in the live view and Atlas");
        Console.WriteLine("PASS Game Play ground diagnostics are opt-in and use the analyzed frame's exact pixels");
        Console.WriteLine("PASS Hacker displays fresh object detections through the measured camera and drops cross-room results");
        Console.WriteLine("PASS Game Play and Hacker draw only scoped bot wall contacts while Atlas Paths is enabled");
    }

    private static void VerifyGameplayWorldHandoff(string root)
    {
        const long generation = 5;
        const long coordinateEpoch = 4;
        const ulong savedGeneration = 15;
        const string room = "Tutorial_01";
        var first = SolidFrame(1);
        var newest = SolidFrame(2);
        var raw = new AtlasPoint(20, 10);
        var world = new LiveWorldTracker();
        world.Ingest(0, 1, first, raw, first.Width, searchForClosure: false, localPose: raw,
            captureGeneration: savedGeneration, roomId: 7, placedCameraPose: new(120, 40),
            captureEvidence: MeasuredCapture(1, 1));
        world.Ingest(1, 2, newest, new(50, 7), newest.Width, searchForClosure: false, localPose: new(50, 7),
            captureGeneration: savedGeneration, roomId: 7, placedCameraPose: new(350, 37),
            captureEvidence: MeasuredCapture(2, 2));
        var before = world.Snapshot;
        var corrected = before.ReplaceOptimizedPoses(new Dictionary<int, AtlasPoint> { [0] = new(140, 35), [1] = new(350, 37) },
            before.MapRevision);
        var publication = new WindowsLiveWorldFrame(newest, newest, 2, 2, new(50, 7), newest.Width, [], [], generation,
            room, 1, LiveTiledAtlas.TileSize);
        var result = new WindowsLiveWorldPipelineResult(WindowsLiveWorldPipelineState.Committed, generation,
            new(350, 37), default, new(300, 30), corrected.MapRevision, null, null, null, "test placement")
        {
            CommittedGraph = corrected,
            CommittedRoomId = 7,
            PersistentCaptureGeneration = savedGeneration,
            WalkedCoordinateEpoch = coordinateEpoch,
            SavedWorld = LiveWorldGroundProjection.Empty with
            {
                Placement = new([new(new(35, 12), new(65, 12))], [])
            }
        };
        var window = new MainWindow(GameRuntimeMode.Normal, gamePath: null, root: root, documents: root);
        var capture = new BotCaptureSession(Path.Combine(root, "wall-contacts"), "normal", "fixture", generation, 0, new());
        using var vision = new CancellationTokenSource();
        try
        {
            SetField(window, "workspace", "Gameplay");
            SetField(window, "recentCaptureGeneration", generation);
            SetField(window, "hasFreshGameFrame", true);
            SetField(window, "botCapture", capture);
            SetField(window, "visionCancellation", vision);
            var completedWorld = typeof(MainWindow).GetNestedType("CompletedWorld", BindingFlags.NonPublic)!;
            SetField(window, "completedWorld", Activator.CreateInstance(completedWorld, publication, result)!);

            var presentation = GetField<GameplayPresentationState>(window, "gameplayPresentation");
            var evidence = GetField<AtlasPathFrameEvidence>(window, "atlasPathEvidence");
            var menuRecognizer = GetField<WindowsMenuStencilRecognizer>(window, "menuStencilRecognizer");
            var bitmap = Control<Image>(window, "GameplayImage");
            var placement = Control<Canvas>(window, "GameplayPlacementCanvas");
            var walls = Control<Canvas>(window, "GameplayWallContactsCanvas");
            var paths = Control<CheckBox>(window, "AtlasPathsCheck");
            var firstOwner = corrected.TrackingIslands.Single(island => island.MeasuredLineage == 1);
            var space = FormattableString.Invariant($"visual/7/{firstOwner.Id}/{BitConverter.DoubleToUInt64Bits(firstOwner.StartedAt)}");
            var wall = new BotBlockadeMark(space, 15, 20, 1, 640, false);
            var epoch = GetField<int>(window, "botRouteEpoch");
            var contacts = new MainWindow.BotWallContactsFrame(new(capture), epoch, generation, first.CaptureId,
                1.5, 7, 7, null, null, [wall, wall with { Crossed = true }, wall with { Space = space + "1" }]);
            SetField(window, "gameplayWallContacts", contacts);

            void PresentCapture(BgraFrame frame, bool gameplay = true)
            {
                presentation.Capture(frame);
                SetField(window, "latestPresentedCaptureId", frame.CaptureId);
                bitmap.Source = (System.Windows.Media.Imaging.BitmapSource)typeof(MainWindow)
                    .GetMethod("FrameBitmap", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [frame])!;
                evidence.ObserveGameplayPreview(frame.CaptureId, gameplay);
            }

            PresentCapture(newest);
            var newestPixels = bitmap.Source;
            var tiles = new AtlasMosaicDelta(1, [new(new(0, 0), SolidFrame(1, LiveTiledAtlas.TileSize, LiveTiledAtlas.TileSize), 1, 1)]);
            var initial = new MainWindow.VisionAnalysisResult(first, [], [], tiles, Camera(120, 40), null,
                new("Playing", "game"), null, null, GetField<long>(menuRecognizer, "generation"), null, true, "")
            {
                CaptureGeneration = generation,
                CaptureTimestamp = 1.5,
                LabelRevision = GetField<LabelCaptureSession>(window, "labelCapture").Revision,
                WorldFrame = new(generation, room, 1.5, coordinateEpoch, 1, 1, false),
                WorldBasisOffset = new(100, 30),
                WorldBasisTimestamp = 1.5,
                WorldPlacementConfirmed = true,
                AtlasPathState = AtlasPathFrameState.Gameplay
            };
            window.ApplyVisionAnalysis(initial);
            Check(ReferenceEquals(newestPixels, bitmap.Source) && Canvas.GetLeft(bitmap) == 124 && Canvas.GetTop(bitmap) == -47,
                "The newest pixels must use the measured owner's revised placement when an older analysis arrives.");
            Check(placement.Visibility == Visibility.Visible && placement.Children.OfType<Line>().Count() == 1,
                "The measured doorway should be presented with confirmed gameplay placement.");
            AssertWall(walls, 135, -45, 22.4);
            Check(Control<Canvas>(window, "GameplayPathCanvas").Visibility == Visibility.Collapsed,
                "A wall contact must be visible with Atlas Paths enabled even when no route can be drawn.");
            paths.IsChecked = false;
            _ = Invoke(window, "UpdateGameplayPathVisibility");
            Check(walls.Visibility == Visibility.Collapsed, "Toggling Atlas Paths off must hide Game Play wall contacts.");
            paths.IsChecked = true;
            _ = Invoke(window, "UpdateGameplayPathVisibility");
            AssertWall(walls, 135, -45, 22.4);

            var movedAgain = corrected.ReplaceOptimizedPoses(new Dictionary<int, AtlasPoint> { [0] = new(147, 39), [1] = new(350, 37) },
                corrected.MapRevision);
            SetField(window, "completedWorld", Activator.CreateInstance(completedWorld, publication,
                result with { Revision = movedAgain.MapRevision, CommittedGraph = movedAgain })!);
            _ = Invoke(window, "UpdateGameplayPathVisibility");
            AssertWall(walls, 142, -49, 22.4);

            window.ApplyVisionAnalysis(initial with
            {
                Frame = newest,
                GameplayTiles = null,
                GameplayTelemetry = Camera(600, 300),
                CaptureTimestamp = 2.5,
                WorldFrame = new(generation, room, 2.5, coordinateEpoch, 99, 99, false)
            });
            Check(ReferenceEquals(newestPixels, bitmap.Source) && Canvas.GetLeft(bitmap) == 124 && Canvas.GetTop(bitmap) == -47
                && placement.Visibility == Visibility.Collapsed && walls.Visibility == Visibility.Collapsed,
                "An unresolved capture must hold the displayed placement while hiding registered doorway overlays.");

            var latestAnalysis = initial with { Frame = newest, GameplayTiles = null, WorldFrame = null };
            VerifyGroundOverlayControls(window, latestAnalysis);
            VerifyGroundDiagnostics(window, latestAnalysis);

            var viewport = GetField<GameplayViewportController>(window, "gameplayViewport");
            viewport.SetFreeFlyTransform(new(1, 0, 0));
            var settledPan = viewport.AtlasPan(4);
            window.ApplyVisionAnalysis(initial with
            {
                WorldFrame = null,
                WorldBasisOffset = new(999, 999),
                WorldBasisTimestamp = 4,
                GameplayTiles = null
            });
            Check(ReferenceEquals(newestPixels, bitmap.Source) && Canvas.GetLeft(bitmap) == 124 && Canvas.GetTop(bitmap) == -47
                && viewport.AtlasPan(4) == settledPan, "Out-of-order analysis must not move the window or update its camera basis.");

            var menu = SolidFrame(3);
            PresentCapture(menu, gameplay: false);
            var menuPixels = bitmap.Source;
            window.ApplyVisionAnalysis(initial with
            {
                Frame = menu,
                Status = new("Options", "options"),
                WorldFrame = null,
                WorldBasisOffset = null,
                WorldBasisTimestamp = null,
                WorldPlacementConfirmed = false,
                AtlasPathState = AtlasPathFrameState.Hidden,
                GameplayTiles = tiles with { Revision = 2 },
                AdmitsTracking = false
            });
            Check(ReferenceEquals(menuPixels, bitmap.Source) && Canvas.GetLeft(bitmap) == 0 && Canvas.GetTop(bitmap) == 0
                && bitmap.Width == menu.Width && bitmap.Height == menu.Height && placement.Visibility == Visibility.Collapsed
                && walls.Visibility == Visibility.Collapsed,
                "Rendering committed tiles must leave a menu image in capture coordinates.");

            var sample = Camera(120, 40) with { SessionId = Guid.NewGuid(), AtlasGameplayActive = true };
            var camera = new GroundTruthCameraTransform(room, new(120, 40), 2, 2, new(32, 24));
            var prepared = new HackerPreparedFrame(menu, new(16, 29, 32, 24), default, camera,
                sample, default, room, 4, 4, true, true);
            var current = contacts with { CaptureId = menu.CaptureId, CapturedAt = 4, ReceiverSession = sample.SessionId,
                Scene = room, PlacedRoom = null,
                Marks = [new("hacker/" + room, 123, 42, -1, 16, false), new("hacker/OtherRoom", 123, 42, -1, 16, false)] };
            SetField(window, "workspace", "Hacker");
            SetField(window, "hackerWallContacts", current);
            var hackerPublication = typeof(MainWindow).GetNestedType("HackerPipelinePublication", BindingFlags.NonPublic)!;
            var publicationArgs = new object?[] { prepared, menuPixels, null, generation,
                GetField<long>(window, "hackerWorkGeneration"), true, 0 };
            _ = Invoke(window, "ApplyHackerPipelinePublication", Activator.CreateInstance(hackerPublication, publicationArgs)!);
            var hackerImage = Control<Image>(window, "HackerLiveImage");
            var hackerWalls = Control<Canvas>(window, "HackerWallContactsCanvas");
            AssertWall(hackerWalls, Canvas.GetLeft(hackerImage) + 22, Canvas.GetTop(hackerImage) + 8, 1.12);
            SetField(window, "hackerWallContacts", current with { ReceiverSession = Guid.NewGuid() });
            _ = Invoke(window, "RefreshAtlasPathVisibility");
            Check(hackerWalls.Visibility == Visibility.Collapsed, "Another receiver session cannot draw its wall on the current Hacker room.");
            SetField(window, "hackerWallContacts", current);
            paths.IsChecked = false;
            _ = Invoke(window, "RefreshAtlasPathVisibility");
            Check(hackerWalls.Visibility == Visibility.Collapsed, "The Atlas Paths toggle must also hide Hacker wall contacts.");
            VerifyHackerObjectOverlay(window, sample, generation, GetField<long>(menuRecognizer, "generation"));
        }
        finally
        {
            SetField(window, "botCapture", null!);
            SetField(window, "visionCancellation", null!);
            _ = capture.FinishAsync(5, null, completed: false).GetAwaiter().GetResult();
            SetField(window, "isClosing", true);
            Wait(InvokeTask(window, "PrepareShutdownAsync"));
        }
    }

    private static void VerifyHackerObjectOverlay(MainWindow window, ReceiverGroundTruthSample sample,
        long captureGeneration, long menuGeneration)
    {
        var workGeneration = GetField<long>(window, "hackerWorkGeneration");
        var labelRevision = GetField<LabelCaptureSession>(window, "labelCapture").Revision;
        var check = Control<CheckBox>(window, "ObjectDetectionCheck");
        var raw = Control<CheckBox>(window, "RawDetectionCheck");
        var pixels = Control<CheckBox>(window, "DetectionPixelsCheck");
        var canvas = Control<Canvas>(window, "HackerObjectDetectionsCanvas");
        var live = Control<Image>(window, "HackerLiveImage");
        var publicationType = typeof(MainWindow).GetNestedType("HackerPipelinePublication", BindingFlags.NonPublic)!;
        var bitmapFactory = typeof(MainWindow).GetMethod("FrameBitmap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var tracked = new ObjectDetection("enemies.crawlid", new(.35, .4, .15, .2), .9f);
        var sourceCamera = sample with { CameraX = 110, CameraY = 30 };
        var currentCamera = sample with { CameraX = 113, CameraY = 32, PixelsPerWorldUnitX = 2, PixelsPerWorldUnitY = 2 };

        void Detect(long captureId, double time, ObjectDetection detection)
        {
            var frame = SolidFrame(captureId);
            var result = new LiveObjectInferencePublication(frame, menuGeneration, [detection])
            {
                Snapshot = new([detection], [detection with { Box = detection.Box with { X = .5 } }], [], frame)
            };
            _ = Invoke(window, "PublishHackerObjectDetections", result, sourceCamera, time, captureGeneration, workGeneration, labelRevision);
        }

        void Present(long captureId, double time, ReceiverGroundTruthSample camera, bool gameplay = true)
        {
            var frame = SolidFrame(captureId);
            var transform = GroundTruthCameraTransform.FromSample(camera, time, new(frame.Width, frame.Height))!;
            var prepared = new HackerPreparedFrame(frame, new(30, 40, frame.Width, frame.Height), default,
                transform, camera, default, camera.SceneName, (int)captureId, time, true, true);
            var bitmap = bitmapFactory.Invoke(null, [frame]);
            _ = Invoke(window, "ApplyHackerPipelinePublication", Activator.CreateInstance(publicationType,
                [prepared, bitmap, null, captureGeneration, workGeneration, gameplay, 0])!);
        }

        check.IsChecked = true;
        Detect(4, 4.1, tracked);
        Detect(6, 4.25, tracked with { Box = tracked.Box with { X = .8 } });
        Present(5, 4.2, currentCamera);
        Program.Pump();
        var box = canvas.Children.OfType<Rectangle>().Single();
        Check(Math.Abs(Canvas.GetLeft(box) - 11.2) < 1e-9 && Canvas.GetLeft(canvas) == -22 && Canvas.GetTop(canvas) == -8
            && canvas.RenderTransform is System.Windows.Media.ScaleTransform { ScaleX: 2, ScaleY: 2 },
            "The current Hacker frame must use the eligible older result at its measured camera position.");
        Check(canvas.Parent is Canvas clip && clip.ClipToBounds && !clip.IsHitTestVisible
            && Canvas.GetLeft(clip) == Canvas.GetLeft(live) && Canvas.GetTop(clip) == Canvas.GetTop(live),
            "The detection layer must track the exact live image bounds and leave terrain editing interactive.");

        Present(5, 4.22, currentCamera with { CameraX = 114 });
        Check(ReferenceEquals(box, canvas.Children.OfType<Rectangle>().Single()) && Canvas.GetLeft(canvas) == -24,
            "A camera move should reposition the existing detections without rebuilding their boxes.");
        raw.IsChecked = true;
        pixels.IsChecked = true;
        Check(Canvas.GetLeft(canvas.Children.OfType<Rectangle>().Single()) == 16
            && canvas.Children.OfType<Image>().Any(), "Raw detections and detection pixels must be available in Hacker.");

        Present(5, 4.22, currentCamera with { SceneName = "DifferentRoom" });
        Check(canvas.Children.Count == 0, "A detection batch must not cross a room boundary.");
        Present(5, 4.22, currentCamera with { SessionId = Guid.NewGuid() });
        Check(canvas.Children.Count == 0, "A detection batch must not cross receiver sessions.");
        Present(5, 4.22, currentCamera, gameplay: false);
        Check(canvas.Children.Count == 0, "A non-gameplay Hacker frame must hide object detections.");
        Present(7, 4.7, currentCamera);
        Check(canvas.Children.Count == 0, "The current image must discard expired detection evidence.");

        Detect(8, 4.72, tracked);
        Present(8, 4.72, currentCamera);
        Check(canvas.Children.OfType<Rectangle>().Count() == 1, "Fresh results must resume after a gap.");
        check.IsChecked = false;
        Check(canvas.Children.Count == 0 && !GetField<bool>(window, "createsObjectPixelDiagnostic"),
            "Turning object detection off must clear Hacker overlays and stop producing diagnostic pixels.");
    }

    private static void VerifyGroundOverlayControls(MainWindow window, MainWindow.VisionAnalysisResult gameplay)
    {
        _ = Invoke(window, "UpdateModeAvailability");
        var lines = Control<CheckBox>(window, "GroundLinesCheck");
        var patches = Control<CheckBox>(window, "GroundTrackingPatchesCheck");
        var livePatches = Control<Canvas>(window, "LiveGroundPatchesCanvas");
        var liveCorners = Control<Canvas>(window, "LiveGroundCornersCanvas");
        var atlas = Control<Canvas>(window, "GameplayGroundCanvas");
        var atlasLines = Control<Canvas>(window, "AtlasGroundLinesCanvas");
        var atlasPatches = Control<Canvas>(window, "AtlasGroundPatchesCanvas");
        var analysis = gameplay with
        {
            Ground = [new(7, new(2, 24))],
            MeasuredPlatformPatches = [new(3, 7, 16, 12)],
            Terrain = new([], [new(0, 0, 20, 7, 24)]),
            GroundDisplayLines = [new(WalkedGroundMemory.WalkedSegmentId, new(-22, 18), new(21, 18))],
            GroundHypotheses = GroundHypothesisTrackingResult.Empty with
            {
                AtlasFeatures = [new(1, 0, new(-8, 12), [], [])]
            }
        };
        window.ApplyVisionAnalysis(analysis);
        Program.Pump();
        Check(lines.IsChecked == true && patches.IsChecked == true && patches.IsEnabled
            && atlas.Visibility == Visibility.Visible && livePatches.Visibility == Visibility.Visible
            && atlasPatches.Visibility == Visibility.Visible && atlasLines.Children.OfType<Line>().Count() == 1
            && livePatches.Children.OfType<Rectangle>().Count() == 1 && atlasPatches.Children.OfType<Rectangle>().Count() == 1,
            "Game Play must start with one green stroke and both live and persistent tracking patches.");

        patches.IsChecked = false;
        Program.Pump();
        Check(livePatches.Visibility == Visibility.Collapsed && atlasPatches.Visibility == Visibility.Collapsed
            && atlas.Visibility == Visibility.Visible && atlasLines.Children.OfType<Line>().Count() == 1,
            "Toggling Track Patches must hide both box layers before another analysis arrives.");
        window.ApplyVisionAnalysis(analysis);
        Check(livePatches.Children.Count == 0 && atlasPatches.Children.Count == 0
            && liveCorners.Children.OfType<Line>().Count() == 1 && atlasLines.Children.OfType<Line>().Count() == 1,
            "Tracking patches must stay off without removing the green stroke or the horizontal corner.");

        patches.IsChecked = true;
        lines.IsChecked = false;
        Program.Pump();
        Check(atlas.Visibility == Visibility.Collapsed && liveCorners.Parent is Canvas { Visibility: Visibility.Collapsed }
            && !patches.IsEnabled && patches.IsChecked == true,
            "Turning off Ground Lines must hide ground overlays and preserve the tracking patch preference.");
        window.ApplyVisionAnalysis(analysis);
        Check(livePatches.Children.Count == 0 && liveCorners.Children.Count == 0
            && atlasLines.Children.Count == 0 && atlasPatches.Children.Count == 0,
            "Hidden ground overlays should skip drawing on subsequent frames.");

        lines.IsChecked = true;
        Program.Pump();
        window.ApplyVisionAnalysis(analysis);
        Check(patches.IsEnabled && livePatches.Children.Count == 1 && atlasPatches.Children.Count == 1
            && atlasLines.Children.OfType<Line>().Count() == 1,
            "The next gameplay analysis should restore the ground stroke and both patch layers.");
    }

    private static void VerifyGroundDiagnostics(MainWindow window, MainWindow.VisionAnalysisResult gameplay)
    {
        const int row = 7, column = 8;
        var width = gameplay.Frame.Width;
        var height = gameplay.Frame.Height;
        var edgePixels = new byte[width * height];
        edgePixels[row * width + column] = 83;
        var responsePixels = new byte[width * height];
        responsePixels[row * width + column] = 200;
        var response = new GroundComparisonAnalysis(width, height, responsePixels);
        var reference = GroundLineDetector.Theory(response with { GroundPixels = new byte[width * height] }, GroundTheoryTuning.Default);
        var terrain = new GameplayTerrainAnalysis(new(width, height, edgePixels), reference, [new(row, new(7, 9))], [])
        {
            SemanticTheory = GroundLineDetector.Theory(response, GroundTheoryTuning.SemanticDefault)
        };
        var switches = new[] { "GroundEdgeCheck", "GroundResponseCheck", "GroundCandidatesCheck", "CleanFloorCheck" }
            .Select(name => Control<CheckBox>(window, name)).ToArray();
        var images = new[] { "GroundEdgeImage", "GroundResponseImage", "GroundCandidatesImage", "CleanFloorImage" }
            .Select(name => Control<Image>(window, name)).ToArray();
        Check(switches.All(toggle => toggle.IsEnabled && toggle.IsChecked == false)
            && images.All(image => image.Visibility == Visibility.Collapsed && image.Source is null)
            && MainWindow.PrepareGroundDiagnostics(terrain, window.RequestedGroundDiagnostics) is null,
            "The four ground diagnostics must start hidden and allocate no images while off.");

        switches[1].IsChecked = true;
        var responseOnly = MainWindow.PrepareGroundDiagnostics(terrain, window.RequestedGroundDiagnostics);
        Check(responseOnly is { Edges: null, FilterResponse: not null, CandidateRuns: null, CleanFloor: null },
            "Enabling filter response must allocate only its own pixels.");
        foreach (var toggle in switches) toggle.IsChecked = true;
        var result = gameplay with
        {
            GroundDiagnostics = MainWindow.PrepareGroundDiagnostics(terrain, window.RequestedGroundDiagnostics)
        };
        window.ApplyVisionAnalysis(result);
        Program.Pump();
        AssertPixel(images[0], [83, 83, 83, 255]);
        AssertPixel(images[1], [200, 200, 200, 255]);
        AssertPixel(images[2], [255, 230, 0, 255]);
        AssertPixel(images[3], [120, 255, 70, 255]);

        foreach (var toggle in switches) toggle.IsChecked = false;
        Program.Pump();
        Check(images.All(image => image.Visibility == Visibility.Collapsed),
            "Turning off diagnostics must hide all images before receiving another frame.");
        window.ApplyVisionAnalysis(result);
        Check(images.All(image => image.Source is null),
            "In-flight diagnostic images must not reappear after turning a layer off.");

        static void AssertPixel(Image image, byte[] expected)
        {
            Check(image.Visibility == Visibility.Visible && image.Source is System.Windows.Media.Imaging.BitmapSource,
                "The selected diagnostic image was not shown.");
            var pixel = new byte[4];
            ((System.Windows.Media.Imaging.BitmapSource)image.Source).CopyPixels(new Int32Rect(column, row, 1, 1), pixel, 4, 0);
            Check(pixel.SequenceEqual(expected), $"The layer {image.Name} must show its own frame-matched diagnostic pixels.");
        }
    }

    private static void AssertWall(Canvas canvas, double x, double y, double halfHeight)
    {
        var line = canvas.Children.OfType<Line>().Single();
        Check(canvas.Visibility == Visibility.Visible && !canvas.IsHitTestVisible && !line.IsHitTestVisible
            && line.Stroke is System.Windows.Media.SolidColorBrush orange && orange.Color.R == 255 && orange.Color.G == 133
            && Math.Abs(line.X1 - x) < 1e-9 && Math.Abs(line.X2 - x) < 1e-9
            && Math.Abs(line.Y1 - (y - halfHeight)) < 1e-9 && Math.Abs(line.Y2 - (y + halfHeight)) < 1e-9,
            "The live wall contact must be a noninteractive orange marker at its current Atlas position.");
    }

    private static BgraFrame SolidFrame(long id, int width = 32, int height = 24)
    {
        var pixels = Enumerable.Repeat((byte)255, width * height * BgraFrame.BytesPerPixel).ToArray();
        return new(width, height, width * BgraFrame.BytesPerPixel, pixels, id, DateTimeOffset.UnixEpoch);
    }

    private static LiveWorldCaptureEvidence MeasuredCapture(double timestamp, long lineage)
    {
        var evidence = new LiveWorldCaptureEvidence(timestamp);
        typeof(LiveWorldCaptureEvidence).GetProperty(nameof(evidence.MeasuredLineage))!.SetValue(evidence, lineage);
        return evidence;
    }

    private static ReceiverGroundTruthSample Camera(double x, double y) => new(
        2, "groundTruth", Guid.Empty, 0, 0, 0, "Tutorial_01", false, 0, 0, 0, 0, 0, false, false, true,
        x, y, -38, x, y, -38, 12, 1, 1, null, null, 32, 24, 32, 24);

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

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, InstanceMembers)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().Name, name));

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
