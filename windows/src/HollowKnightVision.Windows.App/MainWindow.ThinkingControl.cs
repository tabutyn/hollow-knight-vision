using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record ThinkingFrame(BgraFrame Image, ThinkingSight Sight, GameTextReader.Reading? Reading);
    private sealed record ThinkingAuthored(int Epoch, GroundTruthLineDocument Labels);
    private sealed record ThinkRequest(ThinkingPlan Plan, double Time);
    private sealed record ThinkingLease(Guid Owner, BotRun? PriorRun, ThinkingManeuver Maneuver);

    private ThinkingFrame? latestThinkingFrame;
    private GameTextReader.Reading? latestThinkingReading;
    private ThinkingAuthored? thinkingAuthored;
    private Guid? thinkingReceiverSession;
    private string? thinkingScene;
    private BotHackerVisit? thinkingVisit;
    private (long Generation, int Epoch) thinkingVisitEpoch;
    private double thinkingRouteAt = double.NegativeInfinity;
    private string? thinkingRouteScene;
    private Guid? thinkingRouteSession;
    private readonly object thinkingControlLock = new();
    private ThinkRequest? pendingThinkingControl;
    private ThinkingLease? thinkingLease;
    private long lastSubmittedThinkingDecision;
    private bool thinkingControlAvailable;

    // The analysis thread consumes the decision once. The ordinary PPO loop
    // does not run on a frame handled by this maneuver.
    private bool ObserveThinkingFrame(BgraFrame frame, long generation, double capturedAt, int room,
        long chartEpoch, string? menuContext, MenuStencilResult? menu, ReceiverGroundTruthSample? receiver,
        int? renderedFrame, GameplayPathView? paths, bool admitted, InputButtons? input, double blockage)
    {
        if (!Volatile.Read(ref thinkingEnabled)) return false;
        var domain = workspace == "Hacker" ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay;
        if (receiver is not null) thinkingReceiverSession = receiver.SessionId;
        var gameplay = admitted && menu is not { IsMatch: true };
        if (gameplay) thinkingScene = receiver?.SceneName ?? thinkingScene;
        else thinkingScene = null;
        var context = gameplay ? thinkingScene ?? "Gameplay"
            : menuContext is null || menuContext == "game" ? "Game capture" : ContextDisplayName(menuContext);
        var reading = Volatile.Read(ref latestThinkingReading);
        if (reading is null || reading.Context != context || capturedAt - reading.CapturedAt is not (>= 0 and <= 2.5)) reading = null;
        ThinkingScene? scene = null;
        if (gameplay && domain == BotCaptureDomain.Hacker)
        {
            if (receiver is not null) room = WindowsLiveWorldPipeline.RoomIdentifier(receiver.SceneName);
            var routes = ThinkingHackerRoutes(frame, receiver, renderedFrame, generation, capturedAt, room);
            scene = ThinkingScene.FromMatchedReceiver(room, receiver, routes);
        }
        else if (gameplay && paths is { Camera: not null, Hint: { } hint } && paths.KnightWidth > 0)
        {
            var route = ThinkingRoute.FromPath(FormattableString.Invariant($"gameplay/{room}/{chartEpoch}"),
                hint, paths.KnightWidth, frame.Width, frontier: paths.SelectedObjective is null);
            scene = new(room, receiver?.Grounded == true, null, [], [], [route]);
        }
        var scope = new ThinkingScope(generation, domain, context, thinkingReceiverSession);
        var sight = new ThinkingSight(scope, capturedAt, gameplay, menu?.ContextIdentifier,
            menu is { IsMatch: true, SelectedPairAccepted: true } ? menu.SelectedOptionIdentifier : null,
            reading?.Cues ?? [], scene);
        Volatile.Write(ref latestThinkingFrame, new(frame, sight, reading));

        lock (thinkingControlLock)
        {
            if (!Volatile.Read(ref thinkingControlAvailable))
            {
                StopThinkingControlLocked("Controls are unavailable.");
                return false;
            }
            var now = BotCaptureClock();
            if (pendingThinkingControl is { } request)
            {
                pendingThinkingControl = null;
                if (thinkingLease is null && now - request.Time <= .45
                    && request.Plan.Pick(sight, now) is { } candidate)
                    StartThinkingControlLocked(candidate, sight, now);
                else
                    PostThinkingHandoff("The offered target changed before controls were available.");
            }

            if (thinkingLease is not { } lease) return false;
            if (!gameControls.OwnsBotControl(lease.Owner)
                || lease.PriorRun is { } prior && !ReferenceEquals(prior, Volatile.Read(ref botPlayback)))
            {
                StopThinkingControlLocked("Policy input was interrupted.");
                return true;
            }
            var passage = reading?.SatisfiedInspection == lease.Maneuver.StartedAt;
            var motion = lease.Maneuver.Step(sight, now, input ?? InputButtons.None, passage, blockage);
            if (motion.Finished)
            {
                StopThinkingControlLocked(motion.Status);
                return true;
            }
            if (motion.Buttons == InputButtons.None)
                gameControls.NeutralizeBotControl(lease.Owner);
            else if (motion.Deadline <= now || !gameControls.RenewBotControl(lease.Owner, motion.Buttons, motion.Deadline))
            {
                StopThinkingControlLocked("Policy controls expired.");
                return true;
            }
            if (motion.BeginReading)
            {
                var old = reading?.Lines ?? [];
                _ = Dispatcher.InvokeAsync(() =>
                {
                    lock (thinkingControlLock)
                    {
                        if (thinkingLease == lease)
                            gameTextReader.BeginInspection(context, lease.Maneuver.StartedAt, old);
                    }
                });
            }
            return true;
        }
    }

    private IReadOnlyList<ThinkingRoute> ThinkingHackerRoutes(BgraFrame frame, ReceiverGroundTruthSample? receiver,
        int? renderedFrame, long generation, double capturedAt, int room)
    {
        if (receiver is null || Volatile.Read(ref thinkingAuthored) is not { } authored) return [];
        var epoch = (generation, authored.Epoch);
        if (thinkingVisit is null || thinkingVisitEpoch != epoch)
        {
            thinkingVisit = new(authored.Labels, hackerPathLinks, includeThinkingRoutes: true);
            thinkingVisitEpoch = epoch;
            thinkingRouteAt = double.NegativeInfinity;
        }
        if (receiver.SceneName != thinkingRouteScene || receiver.SessionId != thinkingRouteSession
            || capturedAt - thinkingRouteAt >= .075)
        {
            var objectives = Volatile.Read(ref hackerObjectives);
            var scene = receiver.SceneName;
            var registered = latestStagedHackerAtlas is { } staged && staged.CapturedAt <= capturedAt
                ? staged.Output.Rooms.FirstOrDefault(candidate => candidate.SceneName == scene) : null;
            thinkingVisit.Observe(new(frame.Width, frame.Height), renderedFrame, receiver,
                new(capturedAt, capturedAt, generation, room, 0), null, 0,
                objective: objectives.SelectedFor(scene), roomKey: HackerLayoutId("room", scene).ToString("D").ToUpperInvariant(),
                automaticObjective: objectives.IsAutomatic(scene), registeredRoom: registered);
            thinkingRouteAt = capturedAt;
            thinkingRouteScene = scene;
            thinkingRouteSession = receiver.SessionId;
        }
        return thinkingVisit.ThinkingRoutes.Select(route =>
            route with { Chart = FormattableString.Invariant($"{route.Chart}/{authored.Epoch}") }).ToArray();
    }

    private void StartThinkingControlLocked(ThinkingCandidate candidate, ThinkingSight sight, double now)
    {
        var prior = Volatile.Read(ref botPlayback);
        Guid? owner;
        if (prior is not null)
        {
            if (!prior.Playing || prior.Domain != sight.Scope.Domain || Volatile.Read(ref prior.Loaded)?.Policy is not { } policy)
            {
                PostThinkingHandoff("Bot playback is currently unavailable.");
                return;
            }
            gameControls.NeutralizeBotControl(prior.Owner);
            policy.ResetHistory();
            owner = prior.Owner;
        }
        else owner = gameControls.BeginBotControl();

        if (owner is not { } leaseOwner)
        {
            PostThinkingHandoff("Release the game controls before a policy can start.");
            return;
        }
        thinkingLease = new(leaseOwner, prior, new(candidate, sight, now));
        PostThinkingHandoff($"Dispatched · {candidate.Id} · {candidate.Action}");
    }

    private string SubmitThinkingPlan(ThinkingPlan plan)
    {
        if (gameMode == GameRuntimeMode.Normal) return "Inspection only · Normal mode.";
        if (!thinkingAutomatic || !CanDispatchThinking()) return "Automatic policies are paused or controls are unavailable.";
        return QueueThinkingPlan(plan);
    }

    private string QueueThinkingPlan(ThinkingPlan plan)
    {
        if (plan.Scores.Count == 0) return "Model abstained.";
        lock (thinkingControlLock)
        {
            var now = BotCaptureClock();
            if (plan.DecisionId <= lastSubmittedThinkingDecision) return "Decision already consumed.";
            lastSubmittedThinkingDecision = plan.DecisionId;
            if (thinkingLease is not null || pendingThinkingControl is not null)
                return "A policy is already in progress.";
            if (Volatile.Read(ref latestThinkingFrame) is not { } current || plan.Pick(current.Sight, now) is null)
                return "No eligible option on the fresh screen.";
            pendingThinkingControl = new(plan, now);
            return "Validating the chosen target on the next frame…";
        }
    }

    private bool StopThinkingControl(string? status = null)
    {
        lock (thinkingControlLock)
        {
            var wasActive = pendingThinkingControl is not null || thinkingLease is not null;
            StopThinkingControlLocked(status);
            return wasActive;
        }
    }

    private void StopThinkingControlLocked(string? status)
    {
        var pending = pendingThinkingControl;
        pendingThinkingControl = null;
        if (thinkingLease is { } lease)
        {
            thinkingLease = null;
            if (lease.PriorRun is null) gameControls.EndBotControl(lease.Owner);
            else gameControls.NeutralizeBotControl(lease.Owner);
            if (status is not null) PostThinkingHandoff(status);
        }
        else if (pending is not null && status is not null) PostThinkingHandoff(status);
    }

    private void PostThinkingHandoff(string status) => _ = Dispatcher.InvokeAsync(() =>
    {
        thinkingHandoff = status;
        PresentThinking();
    });

    private void CheckThinkingControl()
    {
        var available = thinkingEnabled && thinkingAutomatic && CanDispatchThinking();
        Volatile.Write(ref thinkingControlAvailable, available);
        if (!thinkingEnabled) return;
        if (Volatile.Read(ref thinkingAuthored)?.Epoch != Volatile.Read(ref botRouteEpoch))
            Volatile.Write(ref thinkingAuthored, new(Volatile.Read(ref botRouteEpoch), groundTruthLabels.Document.Clone()));
        if (!available || Volatile.Read(ref latestThinkingFrame)?.Sight.IsFresh(BotCaptureClock()) != true)
            StopThinkingControl("Waiting for fresh game controls.");
        StartThinkingQuestion();
    }

    private bool CanDispatchThinking() => !isClosing && IsActive && IsVisible
        && visionCancellation is { IsCancellationRequested: false } && workspace is "Gameplay" or "Hacker"
        && SettingsOverlay.Visibility != Visibility.Visible && gameTextWindow is null && !FirstResponderConsumesTextInput()
        && initialization.IsFirstPhaseComplete && botCapture is null && !changingBotCapture
        && !gameControls.IsRecording && !gameControls.IsReplaying && trainingProcessTask?.IsCompleted != false
        && botTrainingProcessTask?.IsCompleted != false;
}
