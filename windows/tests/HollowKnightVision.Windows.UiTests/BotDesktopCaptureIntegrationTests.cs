using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

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
            var window = new MainWindow(GameRuntimeMode.Modded, gamePath: null, root: root, documents: root);
            try
            {
                VerifyDesktopRecording(window, hudFrame, Path.Combine(root, "demonstrations"));
            }
            finally
            {
                Field("isClosing").SetValue(window, true);
                Wait((Task)Method("PrepareShutdownAsync").Invoke(window, null)!);
            }
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        Console.WriteLine("PASS Desktop analysis captures room-owned doors and preserves wall evidence across a correction");
    }

    private static void VerifyDesktopRecording(MainWindow window, BgraFrame hudFrame, string library)
    {
        var capture = new BotCaptureSession(library, "normal", "visual", Generation, Clock(), new());
        Field("botCapture").SetValue(window, capture);
        var finished = false;
        try
        {
            VerifyGameplayAdmission(window, capture, hudFrame);

            var scene = FrameWithScenery(hudFrame);
            _ = Step(10, .50, InputButtons.Right, 2, default, GroundHypothesisTrackingResult.Empty);
            Thread.Sleep(115);
            _ = Step(11, .50, InputButtons.Right, 2, default, GroundHypothesisTrackingResult.Empty);
            Thread.Sleep(115);
            var before = Step(12, .50, InputButtons.Right, 2, default, GroundHypothesisTrackingResult.Empty);

            var shift = new AtlasPoint(40, -16);
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
            Thread.Sleep(115);
            _ = Step(14, .50, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty);
            Thread.Sleep(115);
            _ = Step(15, .56, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty);
            Thread.Sleep(115);
            _ = Step(16, .50, InputButtons.None, 3, shift, GroundHypothesisTrackingResult.Empty);

            finished = true;
            var published = capture.FinishAsync(Clock() + .2, null, completed: true).GetAwaiter().GetResult();
            var clip = published.Published.Single();
            Check(clip.BotDomain == "gameplay" && clip.ObservationCount == 6, "The desktop path must save all six admitted samples.");
            var path = Path.Combine(library, BotCaptureSession.DirectoryName(clip.Id), "events.jsonl");
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var rows = File.ReadLines(path).Select(line => JsonSerializer.Deserialize<BotCaptureObservation>(line, json)!).ToArray();
            Check(rows.Select(row => row.DetectionFrameId).SequenceEqual([10L, 11L, 12L, 14L, 15L, 16L]),
                "The unsampled correction frame must stay out of the clip.");
            var expectedDoor = new BotLine(16, 10, 16, 40);
            Check(rows.Take(5).All(row => row.DetectedTerrain?.Doors?.SequenceEqual([expectedDoor]) == true)
                && rows[^1].DetectedTerrain?.Doors is null && rows.All(row => row.AuthoredTerrain is null),
                "The clip must retain its room's visual door across correction and omit it without a measured camera.");
            var oldWall = rows[2].Blockade;
            var translatedWall = rows[3].Blockade;
            Check(rows.Take(2).All(row => row.Blockade is null)
                && oldWall is { Mark.Direction: 1 } && translatedWall is { Mark.Direction: 1 }
                && oldWall.Mark.Space != translatedWall.Mark.Space
                && Math.Abs(translatedWall.Mark.X - oldWall.Mark.X - shift.X / hudFrame.Width) < 1e-8
                && Math.Abs(translatedWall.Mark.Y - oldWall.Mark.Y - shift.Y / hudFrame.Width) < 1e-8
                && Math.Abs(translatedWall.Dx - oldWall.Dx) < 1e-8,
                "The published observation must carry the translated wall and its original relative distance.");
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
                var captureAt = timestamp ?? Clock();
                const int committedRoom = 19;
                var center = new AtlasPoint(frame.Width / 2d, frame.Height / 2d);
                double[] offsets = id switch
                {
                    <= 10 => [0],
                    11 => [0, 128],
                    12 => [0, 128, 256],
                    _ => [640, 768, 896, 768]
                };
                var observations = offsets.Select((offset, index) =>
                {
                    var pose = center with { X = center.X + offset };
                    return LiveWorldObservation.Create(index, index, index + 1, frame.Width, frame.Height,
                        frame.Width, 160, [], pose, pose, roomId: committedRoom);
                }).ToArray();
                var origin = observations[0].OptimizedPose;
                var island = new LiveTrackingIsland(0, committedRoom, 1, origin,
                    LiveIslandPlacement.Reference, default, "test",
                    observations.Select(row => new LiveIslandMember(row.Id, new(row.OptimizedPose.X - origin.X, 0), true)).ToArray());
                var graph = new LiveWorldSnapshot(mapRevision: (int)id, anchoredObservationId: 0,
                    observations: observations, trackingIslands: [island]);
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
                { DoorPlacement = doors };
                _ = Method("CaptureBotObservations").Invoke(window, [
                    frame, Generation, Generation, captureAt, 17, 1UL, null, null, batch,
                    Array.Empty<DetectedFloorLine>(), TerrainWallDetection.Empty, true, camera, null, null, input,
                    null, epoch, ground, false, Array.Empty<PixelRect>(), null, true, world
                ]);
                return captureAt;
            }
        }
        finally
        {
            Field("botCapture").SetValue(window, null);
            if (!finished) _ = capture.FinishAsync(Clock(), null, completed: false).GetAwaiter().GetResult();
        }
    }

    private static void VerifyGameplayAdmission(MainWindow window, BotCaptureSession capture, BgraFrame hudFrame)
    {
        var pixels = FrameWithScenery(hudFrame);
        var admission = new GameplayFrameAdmission();
        var tracking = new GameplayTrackingPipeline(new VisualGameplayTracker(), new MaskedFrameMotionTracker());
        for (var index = 1; index <= 6; index++)
        {
            var time = 1 + index * .2;
            var frame = new BgraFrame(hudFrame.Width, hudFrame.Height, hudFrame.Stride, pixels, index,
                DateTimeOffset.UnixEpoch.AddSeconds(time));
            _ = Method("AnalyzeVisionFrame").Invoke(window, [
                frame, null, admission, Generation, 0L, time, null, null, false, false, 0L, InputButtons.None, tracking
            ]);
            if (ReferenceEquals(Field("botRouteOwner").GetValue(window), capture)) return;
        }
        throw new InvalidOperationException("A game frame with its captured HUD did not reach the desktop bot capture path.");
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

    private static double Clock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

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
