using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private BotCaptureSession? botCapture;
    private CancellationTokenSource? botPreviewCancellation;
    private IReadOnlyList<BotCaptureReviewFrame> botReview = [];
    private int botReviewIndex;
    private bool changingBotCapture;
    // The frame-analysis worker owns the planner. A capture or playback gets its own visit.
    private object? botRouteOwner;
    private BotHackerVisit? botRouteVisit;
    private int botRouteEpoch, activeBotRouteEpoch;
    private readonly BotVisualAtlasCoverage botVisualCoverage = new();
    private readonly BotCommittedAtlasCoverage botCommittedCoverage = new();
    internal readonly record struct CommittedBotFrame(LiveWorldSnapshot Graph, int Room, AtlasPoint? CameraCenter)
    {
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
    private static string InputPathRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HollowKnightVision", "paths");

    private static double BotCaptureClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void CaptureBotObservations(BgraFrame frame, long generation, long visualGeneration, double timestamp,
        int roomId, ulong roomRevision, int? renderedFrame, ReceiverGroundTruthSample? receiverTelemetry,
        LiveObjectDetectionBatch? batch, IReadOnlyList<DetectedFloorLine> floors, TerrainWallDetection terrain,
        bool analyzedTerrain, AtlasPoint? camera, HudStencilResult? hud, AtlasPoint? measuredFoot, InputButtons? input,
        BotRun? run, long walkedCoordinateEpoch, GroundHypothesisTrackingResult ground,
        bool admitsPlacement, IReadOnlyList<PixelRect> atlasExclusions, GameplaySceneMasks? auditMasks,
        bool usesDurableWorld, CommittedBotFrame? committedAtlas)
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
        }
        if (capture is not null || run?.Domain == BotCaptureDomain.Gameplay)
            botVisualBlockade.ObserveVisualBasis(generation, roomId, walkedCoordinateEpoch, frame.Width,
                timestamp, camera.HasValue, ground);
        if (capture?.WantsFrame(generation, timestamp) != true) capture = null;
        var playback = run is null ? null : Volatile.Read(ref run.Loaded)?.Policy;
        if (playback?.WantsObservation(timestamp) != true) playback = null;
        if (capture is null && playback is null) return;

        if (batch?.CaptureGeneration == visualGeneration) batch = batch with { CaptureGeneration = generation };
        else batch = null;
        var time = new BotCaptureTiming(timestamp, BotCaptureClock(), generation, roomId, roomRevision);
        var roomKey = receiverTelemetry?.SceneName is { Length: > 0 } scene
            ? HackerLayoutId("room", scene).ToString("D").ToUpperInvariant() : null;
        var exact = botRouteVisit?.Observe(new(frame.Width, frame.Height), renderedFrame, receiverTelemetry, time, input,
            (int)Math.Min(roomRevision, int.MaxValue), objective: Volatile.Read(ref hackerObjective), roomKey: roomKey);
        if (exact is not null && receiverTelemetry is not null)
        {
            var cameraTruth = GroundTruthCameraTransform.FromSample(receiverTelemetry, timestamp, new(frame.Width, frame.Height));
            var published = latestStagedHackerAtlas;
            var room = published is not null && published.CapturedAt <= timestamp
                ? published.Output.Rooms.FirstOrDefault(candidate => candidate.SceneName == receiverTelemetry.SceneName) : null;
            exact = exact with { AtlasView = cameraTruth is null ? null : room?.BotView(cameraTruth, exact.MeasuredHealth) };
        }
        var visual = capture is not null || playback is not null && run?.Domain == BotCaptureDomain.Gameplay
            ? BotObservationBuilder.Gameplay(frame, time, batch, floors, terrain.Walls, analyzedTerrain,
                camera, hud, measuredFoot, input, exact?.BlueHealthOutcomes,
                committedAtlas?.ProjectDoors(new(frame.Width, frame.Height))).WithSharedHackerPath(exact) : null;
        if (visual is not null)
        {
            BotPolicyAction? appliedAction = playback is not null && input is { } applied
                ? BotPolicyAction.FromButtons(applied) : null;
            visual = botVisualKnightMemory.Resolve(visual, appliedAction, batch, walkedCoordinateEpoch);
            visual = botVisualBlockade.AddVisualEvidence(visual);
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
                botVisualCoverage.ObserveVerified(camera, size, atlasExclusions, default, ground, admitsPlacement);
                atlasView = botVisualCoverage.View(camera, size, (int)Math.Min(roomRevision, int.MaxValue), visual.MeasuredHealth);
            }
            visual = visual with { AtlasView = atlasView };
        }
        if (capture is not null) capture.TryRecord(frame, visual!, exact);
        if (run is not null && playback is not null)
        {
            var row = run.Domain == BotCaptureDomain.Hacker ? exact : visual;
            if (Volatile.Read(ref run.Loaded)?.Evaluation is { } evaluation)
                evaluation.RecordFrame(frame, time, row, receiverTelemetry?.SceneName,
                    visual?.DetectedTerrain?.Floors ?? BotObservationBuilder.ProjectDetectedFloors(frame, floors),
                    exact?.AuthoredTerrain, auditMasks is null ? null : VisualReplayGroundInput.FromTracking(
                        timestamp, (ulong)generation, frame.Height, auditMasks, input), renderedFrame);
            DriveBotFrame(run, playback, row, input, receiverTelemetry?.SceneName);
        }
    }

    private async void PathRecording_Click(object sender, RoutedEventArgs e)
    {
        if (changingBotCapture || gameControls.IsReplaying) return;
        if (Volatile.Read(ref botPlayback) is not null)
        {
            SetStatus("Stop the bot with B before recording a demonstration.");
            return;
        }
        changingBotCapture = true;
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
        BotCaptureSummary.Text = $"{runs} training runs · {sessions.Count(session => session.BotDomain == "hacker")} Hacker clips · {sessions.Count(session => session.BotDomain == "gameplay")} Game Play clips";
        var previous = (BotCaptureClips.SelectedItem as BotClipRow)?.Folder;
        var clips = sessions.Select(session => new BotClipRow(
            DisplayBotDomain(session.BotDomain), session.ObservationCount, session.UsableObservationCount,
            session.Duration.ToString("0.0 s", System.Globalization.CultureInfo.CurrentCulture),
            Path.Combine(BotCaptureRoot, BotCaptureSession.DirectoryName(session.Id)))).ToArray();
        BotCaptureClips.ItemsSource = clips;
        BotCaptureClips.SelectedItem = clips.FirstOrDefault(clip => clip.Folder == previous) ?? clips.FirstOrDefault();
    }

    private async void BotCaptureClips_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BotPreviewImage is null) return;
        botPreviewCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        botPreviewCancellation = cancellation;
        botReview = [];
        botReviewIndex = 0;
        PresentBotPreview();
        try
        {
            if (BotCaptureClips.SelectedItem is not BotClipRow selected) return;
            var frames = await Task.Run(() => BotCaptureSession.Review(selected.Folder, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            botReview = frames;
            PresentBotPreview();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (!cancellation.IsCancellationRequested) SetStatus($"Capture preview could not be read. {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(botPreviewCancellation, cancellation)) botPreviewCancellation = null;
        }
    }

    private void BotPreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        if (botReviewIndex == 0) return;
        botReviewIndex--;
        PresentBotPreview();
    }

    private void BotNextFrame_Click(object sender, RoutedEventArgs e)
    {
        if (botReviewIndex + 1 >= botReview.Count) return;
        botReviewIndex++;
        PresentBotPreview();
    }

    private void PresentBotPreview()
    {
        BotPreviewImage.Source = null;
        BotPreviewOverlay.Children.Clear();
        BotPreviewPosition.Text = string.Empty;
        BotPreviousFrame.IsEnabled = botReviewIndex > 0;
        BotNextFrame.IsEnabled = botReviewIndex + 1 < botReview.Count;
        if (botReviewIndex >= botReview.Count) return;
        var frame = botReview[botReviewIndex];
        try
        {
            using var stream = File.OpenRead(frame.ImageFile);
            var source = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            BotPreviewImage.Width = source.PixelWidth;
            BotPreviewImage.Height = source.PixelHeight;
            BotPreviewOverlay.Width = source.PixelWidth;
            BotPreviewOverlay.Height = source.PixelHeight;
            BotPreviewImage.Source = source;
            BotCapturePreview.Draw(BotPreviewOverlay, frame, new(source.PixelWidth, source.PixelHeight));
            BotPreviewPosition.Text = $"{frame.Offset:0.0} s · {botReviewIndex + 1} / {botReview.Count}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatus($"Capture image could not be read. {error.Message}");
        }
    }

    private static string DisplayBotDomain(string domain) => domain == "hacker" ? "Hacker" : "Game Play";
    private sealed record BotClipRow(string Domain, int Samples, int Usable, string Duration, string Folder);
}
