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
    private string BotCaptureRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HollowKnightVision", "bot-v1", "demonstrations");

    private static double BotCaptureClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void CaptureBotObservations(BgraFrame frame, long generation, long visualGeneration, double timestamp,
        int roomId, ulong roomRevision, int? renderedFrame, ReceiverGroundTruthSample? receiverTelemetry,
        LiveObjectDetectionBatch? batch, IReadOnlyList<DetectedFloorLine> floors, TerrainWallDetection terrain,
        bool analyzedTerrain, AtlasPoint? camera, HudStencilResult? hud, AtlasPoint? measuredFoot, InputButtons? input)
    {
        var capture = Volatile.Read(ref botCapture);
        if (capture is null || !capture.WantsFrame(generation, timestamp)) return;
        if (batch?.CaptureGeneration == visualGeneration) batch = batch with { CaptureGeneration = generation };
        else batch = null;
        var time = new BotCaptureTiming(timestamp, BotCaptureClock(), generation, roomId, roomRevision);
        var exact = capture.CapturesHacker
            ? BotObservationBuilder.Hacker(frame, renderedFrame, receiverTelemetry, capture.Authored, time, input) : null;
        var visual = BotObservationBuilder.Gameplay(frame, time, batch, floors, terrain.Walls, analyzedTerrain,
            camera, hud, measuredFoot, input, exact?.BlueHealthOutcomes);
        capture.TryRecord(frame, visual, exact);
    }

    private async void PathRecording_Click(object sender, RoutedEventArgs e)
    {
        if (changingBotCapture || gameControls.IsReplaying) return;
        changingBotCapture = true;
        try
        {
            if (Interlocked.Exchange(ref botCapture, null) is { } active)
            {
                var end = BotCaptureClock();
                var path = gameMode == GameRuntimeMode.Modded ? gameControls.StopRecording() : null;
                var finish = active.FinishAsync(end, path, completed: true);
                var pathSave = path is not null ? Task.Run<string?>(() => new RecordedInputPathStore(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HollowKnightVision", "paths")).Save(path))
                    : Task.FromResult<string?>(null);
                try
                {
                    var result = await finish;
                    RefreshBotLibrary();
                    var count = result.Published.Count;
                    SetStatus(count switch
                    {
                        2 => "Saved both Hacker and Game Play clips.",
                        1 => $"Saved the {DisplayBotDomain(result.Published[0].BotDomain)} clip.",
                        _ => "Capture discarded because it contained no eligible training observations."
                    });
                }
                finally
                {
                    if (await pathSave is { } output) InputPathText.Text = output;
                }
                return;
            }

            if (visionCancellation is null || !DashboardPresentationPolicy.ShowsBotCapture(workspace, gameMode, recognizedMenuContext))
                return;
            if (gameMode == GameRuntimeMode.Modded && !await gameControls.StartRecordingAsync())
            {
                SetStatus("Updated game receiver is not ready.");
                return;
            }
            var recording = gameControls.Recording;
            if (visionCancellation is null || isClosing || gameMode == GameRuntimeMode.Modded && recording is null)
            {
                if (recording.HasValue) _ = gameControls.StopRecording();
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
            if (gameControls.IsRecording && botCapture is null) _ = gameControls.StopRecording();
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
            if (gameControls.IsRecording) _ = gameControls.StopRecording();
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
        var runs = sessions.Select(session => session.ReplayId ?? session.Id).Distinct().Count();
        BotCaptureSummary.Text = $"{runs} training runs · {sessions.Count(session => session.BotDomain == "hacker")} Hacker clips · {sessions.Count(session => session.BotDomain == "gameplay")} Game Play clips";
        BotCaptureClips.ItemsSource = sessions.Select(session => new BotClipRow(
            DisplayBotDomain(session.BotDomain), session.ObservationCount, session.UsableObservationCount,
            session.Duration.ToString("0.0 s", System.Globalization.CultureInfo.CurrentCulture),
            Path.Combine(BotCaptureRoot, BotCaptureSession.DirectoryName(session.Id)))).ToArray();
    }

    private static string DisplayBotDomain(string domain) => domain == "hacker" ? "Hacker" : "Game Play";
    private sealed record BotClipRow(string Domain, int Samples, int Usable, string Duration, string Folder);
}
