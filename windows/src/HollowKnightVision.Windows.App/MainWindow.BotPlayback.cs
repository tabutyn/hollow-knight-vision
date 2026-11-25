using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Text.Json;
using System.Threading.Channels;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private const double BotDemoHandoffTimeout = 15;
    private const string BotDemoHandoffIssue = "Gameplay reached. Bot controls did not become ready. Controls returned to you.";

    private sealed record LoadedBot(BotPolicyPlayback? Policy, string? ModelName, string? Issue)
    {
        public BotNativePolicy? Motor { get; init; }
        public string? ModelHash { get; init; }
    }

    private sealed class BotRun(Guid owner, BotCaptureDomain domain, long generation, double startedAt,
        GroundTruthLineDocument authored, BotDemoSaveArchive? archive)
    {
        internal Guid Owner { get; } = owner;
        internal BotCaptureDomain Domain { get; } = domain;
        internal long Generation { get; } = generation;
        internal double StartedAt { get; } = startedAt;
        internal GroundTruthLineDocument Authored { get; } = authored;
        internal bool StartedInGameplay => Demo is null;
        internal BotDemoSession? Demo { get; } = archive is null ? null : new(archive, startedAt);
        internal BotStartupNavigation Startup { get; } = new(startedAt);
        internal LoadedBot? Loaded;
        internal BotEvaluationRecorder? Evaluation;
        internal int EvaluationStartQueued;
        internal double HandoffDeadline = double.PositiveInfinity;
        internal BotPlaybackUpdate PlaybackUpdate = new(BotPlaybackUpdate.WaitingForFrame);
        internal string? DisplayedPlaybackStatus; // UI thread
        internal Channel<HackerMatchedFrame>? HackerFrames { get; } = domain == BotCaptureDomain.Hacker
            ? LatestHackerChannel<HackerMatchedFrame>(singleWriter: true) : null;
        // The control lock serializes startup and action ownership across workers.
        internal bool UsesReceiverFrames;
        internal bool Playing;
        internal string? ReportedDemoStatus;
        internal BotDemoSaveBackup? ReportedBackup;
        internal TrialDecision? TrialDiagnostics;
    }

    private BotRun? botPlayback;
    private string? lastBotDemoArchive;
    private string BotDemoArchiveRoot => Path.Combine(BotRoot, "save-archives");
    private string BotEvaluationRoot => Path.Combine(BotRoot, "evaluations");
    private readonly object botEvaluationLock = new();
    private Task botEvaluationWrites = Task.CompletedTask;

    private async void BotPlayback_Click(object sender, RoutedEventArgs e) => await ToggleBotPlaybackAsync();

    private void BotEvaluations_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Directory.CreateDirectory(BotEvaluationRoot).FullName;
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError(error);
        }
    }

    private void BotDemoArchives_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = lastBotDemoArchive ?? BotDemoArchiveRoot;
            if (!Directory.Exists(path)) path = Directory.CreateDirectory(BotDemoArchiveRoot).FullName;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError(error);
        }
    }

    private async Task ToggleBotPlaybackAsync()
    {
        var stoppedThinking = StopThinkingControl("Policy stopped with B.", "user-stop");
        if (stoppedThinking || Volatile.Read(ref botPlayback) is not null)
        {
            thinkingAutomatic = false;
            PresentThinking();
            StopBotPlayback("Bot stopped.", reason: "user-stop");
            return;
        }
        if (gameMode != GameRuntimeMode.Modded || visionCancellation is null || !IsActive
            || workspace is not ("Gameplay" or "Hacker") || isClosing)
        {
            SetStatus("Bot playback requires an active modded game capture.");
            return;
        }
        thinkingAutomatic = true;
        PresentThinking();
        await StartBotPlaybackAsync();
    }

    internal async Task StartBotPlaybackAsync(BotDemoSaveArchive? demoArchive = null)
    {
        if (botCapture is not null || changingBotCapture || forwardedKeys.Count != 0
            || gameControls.BeginBotControl() is not { } owner)
        {
            SetStatus("Release the controls and finish the current recording or replay before starting the bot.");
            return;
        }

        var domain = workspace == "Hacker" ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay;
        var startedAt = BotCaptureClock();
        var inGameplay = recognizedMenuContext == "game"
            || domain == BotCaptureDomain.Hacker && atlasPathEvidence.ReceiverConfirmsGameplay(startedAt);
        var archive = inGameplay ? null : demoArchive ?? new BotDemoSaveArchive(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Team Cherry", "Hollow Knight"), BotDemoArchiveRoot);
        var run = new BotRun(owner, domain, Interlocked.Read(ref recentCaptureGeneration), startedAt,
            groundTruthLabels.Document.Clone(), archive);
        Volatile.Write(ref botPlayback, run);
        if (run.HackerFrames is not null) _ = Task.Run(() => RunMatchedHackerPlaybackAsync(run));
        SetStatus(run.StartedInGameplay ? "Loading the latest verified bot checkpoint."
            : "Bot Demo active. Press B again to stop.");

        LoadedBot loaded;
        try
        {
            var root = BotRoot;
            var selected = await Task.Run(() => new BotModelStore(root, domain).LoadPlayback());
            loaded = selected is null ? new(new BotPolicyPlayback(BotStarterPolicy.Load(), domain), "Built-in playing policies", null)
                : new(new BotPolicyPlayback(selected.Policy, domain), selected.Policy.Name, null)
                {
                    Motor = selected.Policy,
                    ModelHash = selected.Policy.SourceCheckpointSha256
                };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            loaded = new(null, null, $"Bot checkpoint unavailable. {error.Message}");
        }
        if (!ReferenceEquals(Volatile.Read(ref botPlayback), run)) return;
        if (run.StartedInGameplay && loaded is { Policy: not null, ModelName: { } modelName })
            await PrepareBotEvaluationAsync(run, modelName, loaded.ModelHash);
        if (!ReferenceEquals(Volatile.Read(ref botPlayback), run)) return;
        if (run.StartedInGameplay && loaded.Policy is null)
            StopBotPlayback(loaded.Issue);
        else
            Volatile.Write(ref run.Loaded, loaded);
    }

    private async Task PrepareBotEvaluationAsync(BotRun run, string modelName, string? modelHash)
    {
        if (!ReferenceEquals(Volatile.Read(ref botPlayback), run)) return;
        try
        {
            var evaluation = new BotEvaluationRecorder(BotEvaluationRoot, run.Domain, modelName,
                modelHash, run.Authored, run.Generation, run.StartedAt, prepareImmediately: true);
            Volatile.Write(ref run.Evaluation, evaluation);
            await evaluation.Preparation;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or GroundTruthLineStoreException or JsonException)
        {
            if (!ReferenceEquals(Volatile.Read(ref botPlayback), run)) return;
            Volatile.Write(ref run.Evaluation, null);
            StopBotPlayback($"Bot playback capture failed. {error.Message}", reason: "evaluation-start-failed");
        }
    }

    private void StopBotPlayback(string? status = null, string reason = "interrupted")
    {
        BotRun? run;
        lock (thinkingControlLock)
        {
            StopThinkingControlLocked(status, reason);
            run = Interlocked.Exchange(ref botPlayback, null);
        }
        if (run is null) return;
        run.HackerFrames?.Writer.TryComplete();
        gameControls.EndBotControl(run.Owner);
        FinishBotEvaluation(run, reason);
        if (status is not null) SetStatus(status);
    }

    private void ObserveBotInputSent(Guid owner, InputButtons buttons, double time)
    {
        if (Volatile.Read(ref botPlayback) is { } run && run.Owner == owner && Volatile.Read(ref run.Playing))
            Volatile.Read(ref run.Evaluation)?.RecordControls(buttons, time);
        else if (Volatile.Read(ref thinkingLease) is { PriorRun: null } lease && lease.Owner == owner)
            lease.Evaluation?.RecordControls(buttons, time);
    }

    private void FinishBotEvaluation(BotRun run, string reason)
    {
        if (Volatile.Read(ref run.Evaluation) is not { } evaluation) return;
        FinishEvaluation(evaluation, reason);
    }

    private void FinishEvaluation(BotEvaluationRecorder evaluation, string reason)
    {
        lock (botEvaluationLock)
        {
            var write = SaveBotEvaluationAsync(evaluation, reason);
            Volatile.Write(ref botEvaluationWrites, botEvaluationWrites.IsCompleted ? write : Task.WhenAll(botEvaluationWrites, write));
        }
    }

    private async Task SaveBotEvaluationAsync(BotEvaluationRecorder evaluation, string reason)
    {
        try { _ = await evaluation.FinishAsync(BotCaptureClock(), reason); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or GroundTruthLineStoreException or JsonException)
        {
            Trace.TraceError("Bot evaluation could not be saved: {0}", error.Message);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!isClosing && Volatile.Read(ref botPlayback) is null && Volatile.Read(ref thinkingLease) is null)
                    SetStatus($"Bot evaluation could not be saved. {error.Message}");
            });
        }
    }

    private void CheckBotOwnership()
    {
        if (Volatile.Read(ref botPlayback) is not { } run) return;
        if (!Volatile.Read(ref run.Playing) && BotCaptureClock() >= Volatile.Read(ref run.HandoffDeadline))
            StopBotPlayback(BotDemoHandoffIssue, reason: "handoff-timeout");
        else if (!gameControls.OwnsBotControl(run.Owner))
            StopBotPlayback("Bot stopped. Press B to start again.");
        else if (run.Demo is not null && !Volatile.Read(ref run.Playing) && BotCaptureClock() - run.StartedAt > 720)
            StopBotPlayback("Bot Demo timed out.");
        else if ((run.StartedInGameplay || Volatile.Read(ref run.Playing))
            && Volatile.Read(ref run.Loaded) is { Policy: not null } loaded)
        {
            var status = Volatile.Read(ref run.PlaybackUpdate).StatusAt(BotCaptureClock());
            if (status == run.DisplayedPlaybackStatus) return;
            run.DisplayedPlaybackStatus = status;
            SetStatus($"{status} · {loaded.ModelName} · B to stop");
        }
    }

    private BotRun? ObserveBotStartup(BgraFrame frame, long generation, double capturedAt, bool admitted, bool recognizedGameplay,
        ReceiverGroundTruthSample? receiver, MenuStencilResult? menu, HudStencilResult? hud)
    {
        lock (thinkingControlLock)
            return Volatile.Read(ref botPlayback) is { UsesReceiverFrames: false } run
                ? ObserveBotStartupLocked(run, frame, generation, capturedAt, admitted, recognizedGameplay, receiver, menu, hud)
                : null;
    }

    private BotRun? ObserveBotStartupLocked(BotRun run, BgraFrame frame, long generation, double capturedAt, bool admitted,
        bool recognizedGameplay, ReceiverGroundTruthSample? receiver, MenuStencilResult? menu, HudStencilResult? hud)
    {
        if (run.Generation != generation || capturedAt < run.StartedAt
            || !gameControls.OwnsBotControl(run.Owner) || Volatile.Read(ref run.Loaded) is not { } loaded) return null;
        if (!run.Playing)
        {
            var now = BotCaptureClock();
            var frameTime = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
            var receiverGameplay = receiver?.AtlasGameplayActive == true;
            var freshHud = hud is { EstablishesGameplay: true }
                && frameTime - hud.SourceTimestamp is >= 0 and <= .25;
            var freshGameplay = recognizedGameplay && freshHud
                || run.StartedInGameplay && admitted && receiverGameplay;
            if (run.Demo is { } demo)
            {
                var decision = demo.Observe(BotDemoFrame.FromVision(capturedAt, frameTime, freshGameplay, menu), now);
                PublishBotDemoStatus(run, demo);
                if (decision.Phase == BotDemoPhase.Stopped)
                {
                    EndBotFromWorker(run, decision.Issue ?? "Bot Demo stopped.");
                    return null;
                }
                if (decision.Buttons is InputButtons.PauseMenu or InputButtons.Inventory && !gameControls.MenuShortcutsAvailable)
                {
                    EndBotFromWorker(run, "Bot Demo needs a receiver with menu shortcuts.");
                    return null;
                }
                if (freshGameplay) gameControls.NeutralizeBotControl(run.Owner);
                var expiresAt = Math.Min(BotCaptureClock() + .09, capturedAt + .35);
                if (decision.Buttons != InputButtons.None
                    && !gameControls.RenewBotControl(run.Owner, decision.Buttons, expiresAt))
                {
                    EndBotFromWorker(run, "Bot Demo input expired or control was interrupted.");
                    return null;
                }
                if (decision.Phase != BotDemoPhase.Gameplay) return null;
                var deadline = Volatile.Read(ref run.HandoffDeadline);
                if (double.IsPositiveInfinity(deadline))
                {
                    deadline = now + BotDemoHandoffTimeout;
                    Volatile.Write(ref run.HandoffDeadline, deadline);
                }
                if (now >= deadline)
                {
                    EndBotFromWorker(run, BotDemoHandoffIssue);
                    return null;
                }
            }
            else
            {
                var decision = run.Startup.Observe(capturedAt, now, freshGameplay, receiverGameplay, null, null);
                if (decision.Phase == BotStartupPhase.Stopped)
                {
                    EndBotFromWorker(run, decision.Issue ?? "Bot startup stopped.");
                    return null;
                }
                if (decision.Phase != BotStartupPhase.Gameplay) return null;
            }
            if (!admitted || receiver?.AtlasGameplayActive == false) return null;
            if (loaded is { Policy: not null, ModelName: { } modelName }
                && Volatile.Read(ref run.Evaluation)?.Preparation.IsCompletedSuccessfully != true)
            {
                if (Interlocked.Exchange(ref run.EvaluationStartQueued, 1) == 0)
                    _ = Dispatcher.InvokeAsync(() => PrepareBotEvaluationAsync(run, modelName, loaded.ModelHash)).Task.Unwrap();
                return null;
            }
            Volatile.Write(ref run.Playing, true);
            gameControls.NeutralizeBotControl(run.Owner);
        }

        if (loaded.Policy is null)
        {
            EndBotFromWorker(run, $"Gameplay reached. {loaded.Issue ?? "No verified checkpoint is available."} Controls returned to you.");
            return null;
        }
        if (admitted && receiver?.AtlasGameplayActive != false) return run;
        loaded.Policy.ResetHistory();
        gameControls.NeutralizeBotControl(run.Owner);
        if (run.PlaybackUpdate.Status != "Bot waiting for gameplay")
            Volatile.Write(ref run.PlaybackUpdate, new("Bot waiting for gameplay"));
        return null;
    }

    private void PublishBotDemoStatus(BotRun run, BotDemoSession demo)
    {
        if (demo.Status == run.ReportedDemoStatus && demo.Backup == run.ReportedBackup) return;
        var status = run.ReportedDemoStatus = demo.Status;
        var backup = run.ReportedBackup = demo.Backup;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(Volatile.Read(ref botPlayback), run)) return;
            if (backup is not null) lastBotDemoArchive = backup.Directory;
            SetStatus(status);
        });
    }

    private void DriveBotFrame(BotRun run, BotPolicyPlayback playback, BotCaptureObservation? row, InputButtons? previous,
        string? scene)
    {
        try
        {
            var update = playback.Evaluate(row, previous, BotCaptureClock());
            if (!gameControls.ApplyBotPlayback(run.Owner, update))
            {
                playback.ResetHistory();
                gameControls.NeutralizeBotControl(run.Owner);
                update = new(BotPlaybackUpdate.WaitingForFrame);
            }
            else if (update?.Decision is { } decision && row is not null)
            {
                if (previous is { } applied)
                    Volatile.Read(ref run.Evaluation)?.RecordDecision(row, decision.Action.Buttons, applied, scene);
                if (Volatile.Read(ref botTrialControl) is not null)
                    Volatile.Write(ref run.TrialDiagnostics, new((run.TrialDiagnostics?.Count ?? 0) + 1,
                        row.TerrainSource, row.HumanTerrainCount, decision.Action));
            }
            if (update is not null) Volatile.Write(ref run.PlaybackUpdate, update);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            EndBotFromWorker(run, $"Bot playback stopped. {error.Message}");
        }
    }

    private void EndBotFromWorker(BotRun run, string issue)
    {
        run.HackerFrames?.Writer.TryComplete();
        gameControls.EndBotControl(run.Owner);
        Dispatcher.InvokeAsync(() =>
        {
            lock (thinkingControlLock)
            {
                if (Interlocked.CompareExchange(ref botPlayback, null, run) != run) return;
                if (ReferenceEquals(thinkingLease?.PriorRun, run)) StopThinkingControlLocked(issue);
            }
            FinishBotEvaluation(run, issue);
            SetStatus(issue);
        });
    }
}
