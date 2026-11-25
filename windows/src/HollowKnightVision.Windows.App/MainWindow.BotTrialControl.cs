using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record TrialReturnPoint(Guid Session, Guid? Profile, RecordedGameCheckpoint Checkpoint);
    private sealed record TrialDecision(int Count, string TerrainSource, int? AuthoredSlots, BotPolicyAction Action);
    private BotTrialControl? botTrialControl;
    private TrialReturnPoint? trialReturnPoint;
    private CancellationTokenSource? trialCheckpointCancellation;
    private static readonly JsonSerializerOptions TrialJson = new(JsonSerializerDefaults.Web);

    internal void EnableBotTrialControl()
    {
        if (botTrialControl is not null) return;
        try
        {
            botTrialControl = new BotTrialControl((command, token) =>
                Dispatcher.InvokeAsync(() => HandleTrialCommandAsync(command, token), DispatcherPriority.Normal, token).Task.Unwrap());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Bot trial control could not start. {error.Message}");
        }
    }

    private bool CanUseTrialCheckpoint(bool owningRequest = false) => gameMode == GameRuntimeMode.Modded
        && IsActive && !isClosing && !gameLostCountdownStarted && visionCancellation is { IsCancellationRequested: false }
        && workspace is "Gameplay" or "Hacker" && recognizedMenuContext == "game"
        && !atlasOperationInProgress && !playerOpsBusy && !changingBotCapture && botCapture is null
        && inputPathReplayCancellation is null && Volatile.Read(ref botPlayback) is null
        && (owningRequest || trialCheckpointCancellation is null) && forwardedKeys.Count == 0 && gameControls.CanUseTrialCheckpoint;

    private TrialReturnPoint? CurrentTrialReturnPoint()
    {
        if (trialReturnPoint is not { } point || point.Profile != gameTextReader.Profile?.Id) return null;
        if (gameControls.TryGetFreshGroundTruth(TimeSpan.FromSeconds(.5), out var sample) && sample.SessionId == point.Session)
            return point;
        return null;
    }

    private async Task<BotTrialReply> HandleTrialCommandAsync(BotTrialCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (command.Kind == BotTrialCommandKind.Status) return new(true, Status: ReadTrialStatus());
        if (command.Kind == BotTrialCommandKind.Stop)
        {
            thinkingAutomatic = false;
            StopBotPlayback("Bot stopped.", reason: "local-control");
            PresentThinking();
            return new(true);
        }
        if (!CanUseTrialCheckpoint())
            return new(false, "Focus Vision in modded gameplay and finish recording, playback or editing before using a trial checkpoint.");

        var prior = CurrentTrialReturnPoint();
        if (command.Kind == BotTrialCommandKind.MarkReturn && prior is not null)
            return new(false, "A return point is already marked for this playthrough.");
        if (command.Kind == BotTrialCommandKind.Return && prior is null)
            return new(false, "There is no marked return point for this playthrough and receiver session.");
        if (command.Kind == BotTrialCommandKind.RestoreStart && workspace != "Gameplay")
            return new(false, "Choose Game Play to restore a saved trial start.");

        var profile = gameTextReader.Profile?.Id;
        var activeWorkspace = workspace;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        trialCheckpointCancellation = cancellation;
        var operation = cancellation.Token;
        try
        {
            var restore = command.Kind == BotTrialCommandKind.Return ? prior!.Checkpoint : null;
            if (command.Kind == BotTrialCommandKind.RestoreStart)
            {
                var store = new RecordedInputPathStore(InputPathRoot);
                restore = await Task.Run(() => store.LoadNamed(command.FileName!).StartCheckpoint, operation);
                if (restore is null) return new(false, "The saved input replay has no start checkpoint.");
            }
            operation.ThrowIfCancellationRequested();
            if (!CanUseTrialCheckpoint(owningRequest: true) || activeWorkspace != workspace || profile != gameTextReader.Profile?.Id)
                return new(false, "Gameplay or the active playthrough changed before using the checkpoint.");
            var result = await gameControls.UseTrialCheckpointAsync(restore,
                command.Kind == BotTrialCommandKind.Return ? prior!.Session : null, operation);
            if (command.Kind == BotTrialCommandKind.MarkReturn)
                trialReturnPoint = new(result.Session, profile, result.Checkpoint);
            else
            {
                if (command.Kind == BotTrialCommandKind.Return) trialReturnPoint = null;
                Interlocked.Increment(ref botRouteEpoch);
                Volatile.Write(ref gameplayWallContacts, null);
                Volatile.Write(ref hackerWallContacts, null);
            }
            return new(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return new(false, "The trial command was cancelled.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or TimeoutException or ArgumentException)
        {
            return new(false, error.Message);
        }
        finally
        {
            trialCheckpointCancellation = null;
        }
    }

    private JsonElement ReadTrialStatus()
    {
        var run = Volatile.Read(ref botPlayback);
        var skill = Volatile.Read(ref thinkingLease);
        var decision = run is null ? null : Volatile.Read(ref run.TrialDiagnostics);
        var evaluationPose = gameControls.TryGetFreshGroundTruth(TimeSpan.FromSeconds(.5), out var sample)
            && sample.HeroAvailable ? new { scene = sample.SceneName, x = sample.HeroX, y = sample.HeroY, sample.Grounded } : null;
        return JsonSerializer.SerializeToElement(new
        {
            active = run is not null || skill is not null, phase = skill is not null ? "playing" : run?.Demo?.Phase.ToString() ?? (run is null ? "idle"
                : Volatile.Read(ref run.Playing) ? "playing" : "waiting-for-gameplay"),
            status = run?.Demo?.Status ?? StatusText.Text, ready = CanUseTrialCheckpoint(),
            applicationActive = IsActive, capture = visionCancellation is { IsCancellationRequested: false },
            pause = gameControls.PauseState.ToString(), gameContext = recognizedMenuContext,
            trialReturnPoint = CurrentTrialReturnPoint() is not null,
            botActive = skill is not null || run is not null && Volatile.Read(ref run.Playing), botDecisions = decision?.Count ?? 0,
            botStatus = run is null ? null : Volatile.Read(ref run.PlaybackUpdate).StatusAt(BotCaptureClock()),
            botAction = decision?.Action.Values, terrainSource = decision?.TerrainSource,
            humanTerrainSlots = decision?.AuthoredSlots, archivePath = lastBotDemoArchive,
            pathRecording = gameControls.IsRecording, learningRecording = botCapture is not null,
            botDomain = (run?.Domain ?? skill?.Plan.Scope.Domain) switch
            {
                BotCaptureDomain.Hacker => "hacker", BotCaptureDomain.Gameplay => "gameplay", _ => null
            },
            skillCandidate = skill?.Maneuver.Candidate.Id,
            inputPath = InputPathText.Text, evaluationPose
        }, TrialJson);
    }
}
