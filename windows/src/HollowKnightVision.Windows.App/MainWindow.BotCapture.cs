using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private BotCaptureSession? botCapture;
    private bool changingBotCapture;
    // The frame-analysis worker owns the planner. A capture or playback gets its own visit.
    private object? botRouteOwner;
    private BotHackerVisit? botRouteVisit;
    private int botRouteEpoch, activeBotRouteEpoch;
    private readonly BotVisualAtlasCoverage botVisualCoverage = new();
    private readonly BotCommittedAtlasCoverage botCommittedCoverage = new();
    internal readonly record struct CommittedBotFrame(LiveWorldSnapshot Graph, int Room, AtlasPoint? CameraCenter)
    {
        public LiveIslandPlacementProjection? MeasuredOwner { get; init; }
        internal LiveWorldPlacementProjection? DoorPlacement { get; init; }

        internal IReadOnlyList<BotLine>? ProjectDoors(WorldSize size)
        {
            if (DoorPlacement is not { } placement || CameraCenter is not { } center) return null;
            // Live Windows capture solves at the frame width. Recorded markings
            // use the bottom-left origin rather than the saved camera center.
            var origin = new AtlasPoint(center.X - size.Width / 2, center.Y - size.Height / 2);
            return BotObservationBuilder.ProjectVisualDoors(placement, Room, origin, size.Width, size);
        }
    }
    private readonly BotVisualKnightMemory botVisualKnightMemory = new();
    private readonly BotBlockadeVisit botVisualBlockade = new();
    private (int Room, long Coordinates)? botVisualChart;
    private string BotRoot { get; }
    private string BotCaptureRoot => Path.Combine(BotRoot, "demonstrations");
    private string InputPathRoot { get; }

    private static double BotCaptureClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void CaptureBotObservations(BgraFrame frame, long generation, long visualGeneration, double timestamp,
        int roomId, ulong roomRevision, int? renderedFrame, ReceiverGroundTruthSample? receiverTelemetry,
        LiveObjectDetectionBatch? batch, IReadOnlyList<DetectedFloorLine> floors, TerrainWallDetection terrain,
        bool analyzedTerrain, AtlasPoint? camera, HudStencilResult? hud, AtlasPoint? measuredFoot, InputButtons? input,
        BotRun? run, long walkedCoordinateEpoch, GroundHypothesisTrackingResult ground,
        bool admitsPlacement, IReadOnlyList<PixelRect> atlasExclusions, GameplaySceneMasks? auditMasks,
        bool usesDurableWorld, CommittedBotFrame? committedAtlas, IReadOnlyList<GroundHypothesisAtlasLine> walked,
        AtlasPoint? measuredSceneryCamera, long? wallLineage)
    {
        var capture = Volatile.Read(ref botCapture);
        var owner = (object?)capture ?? run;
        var epoch = Volatile.Read(ref botRouteEpoch);
        if (!ReferenceEquals(owner, botRouteOwner) || activeBotRouteEpoch != epoch)
        {
            botRouteOwner = owner;
            activeBotRouteEpoch = epoch;
            var labels = capture is { CapturesHacker: true } ? capture.Authored : run?.Authored;
            botRouteVisit = labels is null ? null : new(labels, hackerPathLinks);
            botVisualCoverage.Reset();
            botCommittedCoverage.Reset();
            botVisualKnightMemory.Reset();
            botVisualBlockade.Reset();
            botVisualChart = null;
            Volatile.Write(ref gameplayWallContacts, null);
            Volatile.Write(ref hackerWallContacts, null);
        }
        if (Volatile.Read(ref gameplayWallContacts) is { } oldGameplay
            && (oldGameplay.RoomId != roomId || oldGameplay.CaptureGeneration != generation))
            Volatile.Write(ref gameplayWallContacts, null);
        if (receiverTelemetry is { } sample && Volatile.Read(ref hackerWallContacts) is { } oldHacker
            && (oldHacker.ReceiverSession != sample.SessionId || oldHacker.Scene != sample.SceneName
                || oldHacker.CaptureGeneration != generation))
            Volatile.Write(ref hackerWallContacts, null);
        if (!usesDurableWorld && (capture is not null || run?.Domain == BotCaptureDomain.Gameplay))
            botVisualBlockade.ObserveVisualBasis(generation, roomId, walkedCoordinateEpoch, frame.Width,
                timestamp, camera.HasValue, ground, measuredSceneryCamera);
        if (capture?.WantsFrame(generation, timestamp) != true) capture = null;
        var playback = run is null ? null : Volatile.Read(ref run.Loaded)?.Policy;
        if (playback?.WantsObservation(timestamp) != true) playback = null;
        if (capture is null && playback is null) return;

        if (batch?.CaptureGeneration == visualGeneration) batch = batch with { CaptureGeneration = generation };
        else batch = null;
        var time = new BotCaptureTiming(timestamp, BotCaptureClock(), generation, roomId, roomRevision);
        var roomKey = receiverTelemetry?.SceneName is { Length: > 0 } scene
            ? HackerLayoutId("room", scene).ToString("D").ToUpperInvariant() : null;
        var objectives = Volatile.Read(ref hackerObjectives);
        var objectiveScene = receiverTelemetry?.SceneName;
        var published = latestStagedHackerAtlas;
        var registeredRoom = objectiveScene is not null && published is not null && published.CapturedAt <= timestamp
            ? published.Output.Rooms.FirstOrDefault(candidate => candidate.SceneName == objectiveScene) : null;
        var exact = botRouteVisit?.Observe(new(frame.Width, frame.Height), renderedFrame, receiverTelemetry, time, input,
            (int)Math.Min(roomRevision, int.MaxValue), objective: objectiveScene is null ? null : objectives.SelectedFor(objectiveScene),
            roomKey: roomKey, automaticObjective: objectiveScene is not null && objectives.IsAutomatic(objectiveScene), registeredRoom: registeredRoom);
        if (exact is not null && receiverTelemetry is not null)
        {
            var cameraTruth = GroundTruthCameraTransform.FromSample(receiverTelemetry, timestamp, new(frame.Width, frame.Height));
            exact = exact with { AtlasView = cameraTruth is null ? null : registeredRoom?.BotView(cameraTruth, exact.MeasuredHealth) };
            var contacts = botRouteVisit!.ActiveWallContacts;
            Volatile.Write(ref hackerWallContacts, contacts.Count > 0 && owner is not null
                ? new(new(owner), epoch, generation, frame.CaptureId, timestamp, roomId, null,
                    receiverTelemetry.SessionId, receiverTelemetry.SceneName, contacts) : null);
        }
        var visualFloors = GroundStrokeGeometry.MergeWalkedFloors(floors, walked, camera ?? measuredSceneryCamera,
            frame.Width, frame.Height, auditMasks?.GroundKnight);
        var visual = capture is not null || playback is not null && run?.Domain == BotCaptureDomain.Gameplay
            ? BotObservationBuilder.Gameplay(frame, time, batch, visualFloors, terrain.Walls, analyzedTerrain,
                camera, hud, measuredFoot, input, exact?.BlueHealthOutcomes,
                committedAtlas?.ProjectDoors(new(frame.Width, frame.Height))).WithSharedHackerPath(exact) : null;
        if (visual is not null)
        {
            BotPolicyAction? appliedAction = playback is not null && input is { } applied
                ? BotPolicyAction.FromButtons(applied) : null;
            visual = botVisualKnightMemory.Resolve(visual, appliedAction, batch, walkedCoordinateEpoch);
            var blockedIsland = committedAtlas is { } chart
                ? BotAtlasBlockadeProjection.Create(chart.Graph, chart.Room, chart.MeasuredOwner) : null;
            visual = usesDurableWorld
                ? botVisualBlockade.AddAtlasEvidence(visual, blockedIsland, camera ?? measuredSceneryCamera, frame.Width,
                    wallLineage, walkedCoordinateEpoch)
                : botVisualBlockade.AddVisualEvidence(visual);
            var size = new WorldSize(frame.Width, frame.Height);
            BotAtlasView? atlasView = null;
            if (usesDurableWorld)
            {
                if (committedAtlas is { } placement)
                {
                    botCommittedCoverage.Update(placement.Graph);
                    atlasView = botCommittedCoverage.View(placement.CameraCenter, size, placement.Room, visual.MeasuredHealth);
                }
            }
            else
            {
                var currentChart = (roomId, walkedCoordinateEpoch);
                if (botVisualChart != currentChart)
                {
                    botVisualCoverage.Reset();
                    botVisualChart = currentChart;
                }
                if (measuredSceneryCamera is { } scenery && admitsPlacement)
                    botVisualCoverage.Register(scenery, size, atlasExclusions);
                else
                    botVisualCoverage.ObserveVerified(camera, size, atlasExclusions, default, ground, admitsPlacement);
                atlasView = botVisualCoverage.View(camera ?? measuredSceneryCamera, size,
                    (int)Math.Min(roomRevision, int.MaxValue), visual.MeasuredHealth);
            }
            visual = visual with { AtlasView = atlasView };
            var contacts = botVisualBlockade.ActiveMarks;
            Volatile.Write(ref gameplayWallContacts, contacts.Count > 0 && owner is not null
                ? new(new(owner), epoch, generation, frame.CaptureId, timestamp, roomId, committedAtlas?.Room,
                    null, null, contacts) : null);
        }
        if (capture is not null) capture.TryRecord(frame, visual!, exact);
        if (run is not null && playback is not null)
        {
            var row = run.Domain == BotCaptureDomain.Hacker ? exact : visual;
            if (Volatile.Read(ref run.Loaded)?.Evaluation is { } evaluation)
                evaluation.RecordFrame(frame, time, row, receiverTelemetry?.SceneName,
                    visual?.DetectedTerrain?.Floors ?? BotObservationBuilder.ProjectDetectedFloors(frame, visualFloors),
                    exact?.AuthoredTerrain, auditMasks is null ? null : VisualReplayGroundInput.FromTracking(
                        timestamp, (ulong)generation, frame.Height, auditMasks, input), renderedFrame);
            DriveBotFrame(run, playback, row, input, receiverTelemetry?.SceneName);
        }
    }

    private async void PathRecording_Click(object sender, RoutedEventArgs e)
    {
        if (changingBotCapture || deletingBotClip || gameControls.IsReplaying) return;
        if (Volatile.Read(ref botPlayback) is not null)
        {
            SetStatus("Stop the bot with B before recording a demonstration.");
            return;
        }
        changingBotCapture = true;
        UpdateBotClipDeletionControls();
        try
        {
            if (Interlocked.Exchange(ref botCapture, null) is { } active)
            {
                var end = BotCaptureClock();
                RecordedInputPath? path;
                try { path = active.CapturesHacker ? await gameControls.StopRecordingAsync() : null; }
                catch
                {
                    await active.FinishAsync(end, null, completed: false);
                    throw;
                }
                var finish = active.FinishAsync(end, path, completed: true);
                var pathSave = path is not null ? Task.Run<string?>(() => new RecordedInputPathStore(InputPathRoot).Save(path))
                    : Task.FromResult<string?>(null);
                try
                {
                    var result = await finish;
                    RefreshBotLibrary();
                    var count = result.Published.Count;
                    var message = count switch
                    {
                        2 => "Saved both Hacker and Game Play clips.",
                        1 => $"Saved the {DisplayBotDomain(result.Published[0].BotDomain)} clip.",
                        _ => "Capture discarded because it contained no eligible training observations."
                    };
                    if (path?.VisualFrameArchive is { Complete: false } archive)
                        message += $" Frame archive incomplete ({archive.SavedFrameCount}/{archive.CapturedFrameCount} images saved).";
                    SetStatus(message);
                }
                finally
                {
                    if (await pathSave is { } output) InputPathText.Text = output;
                }
                return;
            }

            if (visionCancellation is null || !DashboardPresentationPolicy.ShowsBotCapture(workspace, gameMode, recognizedMenuContext))
                return;
            if (gameMode == GameRuntimeMode.Modded && !await gameControls.StartRecordingAsync(visualArchiveRoot: InputPathRoot))
            {
                SetStatus("Updated game receiver is not ready.");
                return;
            }
            var recording = gameControls.Recording;
            if (visionCancellation is null || isClosing || gameMode == GameRuntimeMode.Modded && recording is null)
            {
                if (recording.HasValue) _ = await gameControls.StopRecordingAsync(cancelled: true);
                return;
            }
            var paired = recording.HasValue;
            Volatile.Write(ref botCapture, new BotCaptureSession(BotCaptureRoot,
                gameMode == GameRuntimeMode.Modded ? "modded" : "normal", "visual",
                Interlocked.Read(ref recentCaptureGeneration), recording?.StartedAt ?? BotCaptureClock(),
                groundTruthLabels.Document, recording?.Id, paired));
            PathRecordingButton.Background = Brushes.Firebrick;
            SetStatus(paired ? "Recording Hacker and Game Play. Press K to stop." : "Recording Game Play. Press K to stop.");
        }
        catch (Exception error)
        {
            if (gameControls.IsRecording && botCapture is null) _ = await gameControls.StopRecordingAsync(cancelled: true);
            ShowError(error);
        }
        finally
        {
            changingBotCapture = false;
            UpdateBotClipDeletionControls();
            if (botCapture is null) PathRecordingButton.ClearValue(BackgroundProperty);
            UpdateModeAvailability();
        }
    }

    private async Task AbandonBotCaptureAsync()
    {
        if (Interlocked.Exchange(ref botCapture, null) is not { } abandoned) return;
        try
        {
            if (gameControls.IsRecording) _ = await gameControls.StopRecordingAsync(cancelled: true);
            await abandoned.FinishAsync(BotCaptureClock(), null, completed: false);
            SetStatus("Incomplete bot capture discarded.");
        }
        catch (Exception error) { SetStatus($"Bot capture cleanup failed. {error.Message}"); }
        PathRecordingButton.ClearValue(BackgroundProperty);
    }

    private void BotWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SelectWorkspace("Bot");
    }

    private void RefreshBotLibrary()
    {
        var sessions = BotCaptureSession.Library(BotCaptureRoot);
        RefreshBotTrainingPlans(sessions);
        var runs = sessions.Select(session => session.ReplayId ?? session.Id).Distinct().Count();
        BotCaptureSummary.Text = $"{runs} recorded runs · {sessions.Count(session => session.BotDomain == "hacker")} Hacker clips · {sessions.Count(session => session.BotDomain == "gameplay")} Game Play clips";
        var previous = (BotCaptureClips.SelectedItem as BotClipRow)?.Folder;
        var clips = sessions.Select(session => new BotClipRow(
            DisplayBotDomain(session.BotDomain), session.ObservationCount, session.UsableObservationCount,
            session.Duration.ToString("0.0 s", System.Globalization.CultureInfo.CurrentCulture),
            Path.Combine(BotCaptureRoot, BotCaptureSession.DirectoryName(session.Id)), session)).ToArray();
        BotCaptureClips.ItemsSource = clips;
        BotCaptureClips.SelectedItem = clips.FirstOrDefault(clip => clip.Folder == previous) ?? clips.FirstOrDefault();
        UpdateBotClipDeletionControls();
    }

    private static string DisplayBotDomain(string domain) => domain == "hacker" ? "Hacker" : "Game Play";
    private sealed record BotClipRow(string Domain, int Samples, int Usable, string Duration, string Folder, BotCaptureManifest Manifest);
}
