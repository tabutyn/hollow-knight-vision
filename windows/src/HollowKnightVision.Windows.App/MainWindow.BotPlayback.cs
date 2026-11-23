using System.IO;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record LoadedBot(BotPolicyPlayback? Policy, string? ModelName, string? Issue);

    private sealed class BotRun(Guid owner, BotCaptureDomain domain, long generation, double startedAt,
        GroundTruthLineDocument authored, bool startedInGameplay)
    {
        internal Guid Owner { get; } = owner;
        internal BotCaptureDomain Domain { get; } = domain;
        internal long Generation { get; } = generation;
        internal double StartedAt { get; } = startedAt;
        internal GroundTruthLineDocument Authored { get; } = authored;
        internal bool StartedInGameplay { get; } = startedInGameplay;
        internal BotStartupNavigation Startup { get; } = new(startedAt);
        internal LoadedBot? Loaded;
        // The vision analysis worker owns these fields.
        internal bool Playing;
        internal bool AnnouncedModel;
    }

    private BotRun? botPlayback;

    private async Task ToggleBotPlaybackAsync()
    {
        if (Volatile.Read(ref botPlayback) is not null)
        {
            StopBotPlayback("Bot stopped.");
            return;
        }
        if (gameMode != GameRuntimeMode.Modded || visionCancellation is null || !IsActive
            || workspace is not ("Gameplay" or "Hacker") || isClosing)
        {
            SetStatus("Bot playback requires an active modded game capture.");
            return;
        }
        if (botCapture is not null || changingBotCapture || forwardedKeys.Count != 0
            || gameControls.BeginBotControl() is not { } owner)
        {
            SetStatus("Release the controls and finish the current recording or replay before starting the bot.");
            return;
        }

        var domain = workspace == "Hacker" ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay;
        var run = new BotRun(owner, domain, Interlocked.Read(ref recentCaptureGeneration), BotCaptureClock(),
            groundTruthLabels.Document, recognizedMenuContext == "game");
        Volatile.Write(ref botPlayback, run);
        SetStatus(run.StartedInGameplay ? "Loading the latest verified bot checkpoint."
            : "Bot startup active. Press B again to stop.");

        LoadedBot loaded;
        try
        {
            var root = BotRoot;
            var selected = await Task.Run(() => new BotModelStore(root, domain).LoadPlayback());
            var name = domain == BotCaptureDomain.Hacker ? "Hacker" : "Game Play";
            loaded = selected is null ? new(null, null, $"Train or select a {name} checkpoint before bot gameplay.")
                : new(new BotPolicyPlayback(selected.Policy, domain), selected.Policy.Name, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            loaded = new(null, null, $"Bot checkpoint unavailable. {error.Message}");
        }
        if (!ReferenceEquals(Volatile.Read(ref botPlayback), run)) return;
        if (run.StartedInGameplay && loaded.Policy is null)
            StopBotPlayback(loaded.Issue);
        else
            Volatile.Write(ref run.Loaded, loaded);
    }

    private void StopBotPlayback(string? status = null)
    {
        if (Interlocked.Exchange(ref botPlayback, null) is not { } run) return;
        gameControls.EndBotControl(run.Owner);
        if (status is not null) SetStatus(status);
    }

    private void CheckBotOwnership()
    {
        if (Volatile.Read(ref botPlayback) is not { } run) return;
        if (!gameControls.OwnsBotControl(run.Owner))
            StopBotPlayback("Bot stopped. Press B to start again.");
    }

    private BotRun? ObserveBotStartup(BgraFrame frame, long generation, double capturedAt, bool admitted,
        ReceiverGroundTruthSample? receiver, MenuStencilResult? menu, HudStencilResult? hud)
    {
        var run = Volatile.Read(ref botPlayback);
        if (run is null || run.Generation != generation || capturedAt < run.StartedAt
            || !gameControls.OwnsBotControl(run.Owner)) return null;
        if (!run.Playing)
        {
            var now = BotCaptureClock();
            var frameTime = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
            var receiverGameplay = receiver?.AtlasGameplayActive == true;
            var freshHud = hud is { EstablishesGameplay: true, HoldsHealthMask: false }
                && frameTime - hud.SourceTimestamp is >= 0 and <= .25;
            var freshGameplay = freshHud && receiver?.AtlasGameplayActive != false
                || run.StartedInGameplay && admitted && receiverGameplay;
            string? context = null;
            string? option = null;
            if (menu is { IsMatch: true } && frameTime - menu.SourceTimestamp is >= 0 and <= .3)
            {
                context = menu.ContextIdentifier;
                if (menu.SelectedPairAccepted)
                    option = menu.SelectorSearchRegions.FirstOrDefault(region => region.IsSolved)?.OptionIdentifier;
            }
            var decision = run.Startup.Observe(capturedAt, now, freshGameplay, receiverGameplay, context, option);
            if (decision.Buttons != InputButtons.None)
                gameControls.RenewBotControl(run.Owner, decision.Buttons, Math.Min(now + .09, capturedAt + .35));
            if (decision.Phase == BotStartupPhase.Stopped)
            {
                EndBotFromWorker(run, decision.Issue ?? "Bot startup stopped.");
                return null;
            }
            if (decision.Phase != BotStartupPhase.Gameplay) return null;
            run.Playing = true;
            gameControls.NeutralizeBotControl(run.Owner);
        }

        if (Volatile.Read(ref run.Loaded) is not { } loaded) return null;
        if (loaded.Policy is null)
        {
            EndBotFromWorker(run, loaded.Issue ?? "No verified checkpoint is available.");
            return null;
        }
        if (!run.AnnouncedModel)
        {
            run.AnnouncedModel = true;
            Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(Volatile.Read(ref botPlayback), run))
                    SetStatus($"Bot active with {loaded.ModelName}. Press B to stop.");
            });
        }
        if (admitted && receiver?.AtlasGameplayActive != false) return run;
        loaded.Policy.ResetHistory();
        gameControls.NeutralizeBotControl(run.Owner);
        return null;
    }

    private void DriveBotFrame(BotRun run, BotPolicyPlayback playback, BotCaptureObservation? row, InputButtons? previous)
    {
        try
        {
            if (row is not null && previous.HasValue && playback.Decide(row, previous.Value, BotCaptureClock()) is { } decision
                && gameControls.RenewBotControl(run.Owner, decision.Action.Buttons, decision.Deadline))
                return;
            if (row is null || !previous.HasValue) playback.ResetHistory();
            gameControls.NeutralizeBotControl(run.Owner);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            EndBotFromWorker(run, $"Bot playback stopped. {error.Message}");
        }
    }

    private void EndBotFromWorker(BotRun run, string issue)
    {
        gameControls.EndBotControl(run.Owner);
        Dispatcher.InvokeAsync(() =>
        {
            if (Interlocked.CompareExchange(ref botPlayback, null, run) == run) SetStatus(issue);
        });
    }
}
