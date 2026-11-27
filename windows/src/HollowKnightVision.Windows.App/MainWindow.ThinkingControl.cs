using System.IO;
using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record ThinkingFrame(BgraFrame Image, ThinkingSight Sight, GameTextReader.Reading? Reading)
    {
        internal string Event { get; init; } = "";
        internal bool AllowsMore { get; init; } = true;
        internal string? PreviousState { get; init; }
        internal ThinkingLandmarks? Landmarks { get; init; }
        internal string? DetectorEvidence { get; init; }
        internal ThinkingNotebookContext? Notebook { get; init; }
    }
    private sealed record ThinkingNotebookContext(Guid? Profile, string Language, bool ReadsText, string Memory, string Goal);
    private sealed record ThinkingLandmarks(BgraFrame Image, long Generation, double CapturedAt, ObjectDetection[] Signs, long[] Npcs);
    private ThinkingLandmarks? latestThinkingLandmarks;
    // These game-state transitions are observed on the capture worker, including short intervening fades.
    private ThinkingScope? lastThinkingScope;
    private long thinkingStateVisit;
    private (ThinkingScope Scope, string Element, double At)? sentMenuConfirm;
    private string? previousMenuConfirm;
    private sealed record ThinkingOutcome(ThinkingScope Scope, long DecisionId, string Status);
    private ThinkingOutcome? lastThinkingOutcome;

    private void ObserveThinkingLandmarks(LiveObjectInferencePublication publication, long generation, double capturedAt)
    {
        if (!thinkingEnabled) return;
        var current = publication.Detections.Where(d => d.TrackIdentifier.HasValue && d.SourceFrameIdentifier == publication.Frame.CaptureId);
        var signs = current.Where(d => d.ClassIdentifier == "world.sign").ToArray();
        var npcs = current.Where(d => d.ClassIdentifier.Split('.', '-').Contains("npc")).Select(d => d.TrackIdentifier!.Value).ToArray();
        if (signs.Length != 0 || npcs.Length != 0)
            Volatile.Write(ref latestThinkingLandmarks, new(publication.Frame, generation, capturedAt, signs, npcs));
    }
    private sealed record ThinkingAuthored(int Epoch, GroundTruthLineDocument Labels);
    private sealed record ThinkingAbilityModel(BotLearnedSkill? Skill, BotNativePolicy? Motor, BotStarterPolicy? Starter = null);
    private sealed record ThinkRequest(ThinkingPlan Plan, double Time, IReadOnlyDictionary<string, ThinkingAbilityModel>? Models);
    private sealed record ThinkingLease(Guid Owner, BotRun? PriorRun, ThinkingManeuver Maneuver, ThinkingAbilityDrive? Ability,
        ThinkingPlan Plan, GroundTruthLineDocument Authored, BotEvaluationRecorder? Evaluation);
    private sealed record ThinkingPlaybackFrame(ThinkingLease Lease, ThinkingFrame Frame, InputButtons? Input, double Blockage);

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
    private ThinkingPlaybackFrame? thinkingPlaybackFrame; // Analysis worker only.
    private long lastSubmittedThinkingDecision;
    private bool thinkingControlAvailable;

    // The analysis thread consumes the decision once. The ordinary PPO loop
    // does not run on a frame handled by this maneuver.
    private bool ObserveThinkingFrame(BgraFrame frame, long generation, double capturedAt, int room,
        long chartEpoch, string? menuContext, MenuStencilResult? menu, ReceiverGroundTruthSample? receiver,
        int? renderedFrame, GameplayPathView? paths, bool admitted, InputButtons? input, double blockage,
        long firstVisitFrame = 0, BgraFrame? menuSource = null)
    {
        thinkingPlaybackFrame = null;
        if (!thinkingEnabled) return false;
        var detector = Volatile.Read(ref thinkingEvents);
        var domain = workspace == "Hacker" ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay;
        if (receiver is not null) thinkingReceiverSession = receiver.SessionId;
        var page = ThinkingMenuPage.From(menu);
        if (page is null && menuContext is not (null or "game")) page = new(menuContext, null, []);
        var gameplay = admitted && page is null;
        if (gameplay) thinkingScene = receiver?.SceneName ?? thinkingScene;
        else thinkingScene = null;
        var context = gameplay ? thinkingScene ?? "Gameplay"
            : page?.State == "opening-cutscene" ? GameTextNotebook.OpeningContext
            : page is null ? "Game capture" : ContextDisplayName(page.State);
        var reading = Volatile.Read(ref latestThinkingReading);
        if (reading is null || reading.Context != context || capturedAt - reading.CapturedAt is not (>= 0 and <= 2.5)) reading = null;
        ThinkingScene? scene = null;
        if (gameplay && receiver is { } matched && renderedFrame == HackerFrameSynchronizer.Key(matched.UnityFrame))
        {
            room = WindowsLiveWorldPipeline.RoomIdentifier(matched.SceneName);
            var routes = ThinkingHackerRoutes(frame, matched, renderedFrame, generation, capturedAt, room, input);
            scene = ThinkingScene.FromMatchedReceiver(room, matched, routes);
        }
        else if (gameplay && gameMode == GameRuntimeMode.Normal
            && paths is { Camera: not null, Hint: { } hint } && paths.KnightWidth > 0)
        {
            var route = ThinkingRoute.FromPath(FormattableString.Invariant($"gameplay/{room}/{chartEpoch}"),
                hint, paths.KnightWidth, frame.Width, frontier: paths.SelectedObjective is null);
            scene = new(room, false, null, [], [], [route]);
        }
        var scope = new ThinkingScope(generation, domain, context, thinkingReceiverSession, gameplay ? room : 0);
        if (scope != lastThinkingScope)
        {
            thinkingStateVisit++;
            previousMenuConfirm = sentMenuConfirm is { } confirm && confirm.Scope.Generation == generation
                && capturedAt - confirm.At is >= 0 and <= 5 ? confirm.Element : null;
            if (gameplay || page is not null) sentMenuConfirm = null;
            lastThinkingScope = scope;
        }
        scope = scope with { Visit = thinkingStateVisit };
        var outcome = Volatile.Read(ref lastThinkingOutcome);
        var position = scene is not null && gameMode == GameRuntimeMode.Modded && thinkingRouteScene == receiver?.SceneName
            && thinkingRouteSession == receiver?.SessionId && capturedAt - thinkingRouteAt is >= 0 and <= .075
            ? thinkingVisit?.ThinkingPosition : null;
        var landmarks = Volatile.Read(ref latestThinkingLandmarks);
        if (!gameplay || landmarks is null || landmarks.Generation != generation || landmarks.Image.CaptureId < firstVisitFrame
            || capturedAt - landmarks.CapturedAt is not (>= 0 and <= 1.5)) landmarks = null;
        var sight = new ThinkingSight(scope, capturedAt, gameplay, page?.State, page?.Selected, reading?.Cues ?? [], scene)
        {
            Page = page, Blocked = Math.Abs(blockage) > .5,
            SignTracks = landmarks?.Signs.Select(d => d.TrackIdentifier!.Value).ToArray() ?? [], NpcTracks = landmarks?.Npcs ?? [],
            ActiveRoute = Volatile.Read(ref thinkingLease)?.Maneuver.Candidate.Route,
            Chart = gameMode == GameRuntimeMode.Normal ? FormattableString.Invariant($"gameplay/{room}/{chartEpoch}")
                : position is { } p ? FormattableString.Invariant($"{p.Chart}/{thinkingVisitEpoch.Epoch}") : null,
            NavigationFeet = gameMode == GameRuntimeMode.Normal ? paths?.Feet : position?.Feet,
            ArrivalRadius = position?.ArrivalRadius ?? Math.Max(3, (paths?.KnightWidth ?? 0) / 2),
            OutcomeId = outcome?.Scope == scope ? outcome.DecisionId : null,
            Outcome = outcome?.Scope == scope ? outcome.Status : null, PreviousConfirm = previousMenuConfirm
        };
        var eventImage = sight.Page is not null && menuSource is not null ? menuSource : frame;
        var publication = new ThinkingFrame(eventImage, sight, ReferenceEquals(eventImage, frame) ? reading : null)
            { Landmarks = landmarks };
        Volatile.Write(ref latestThinkingFrame, publication);
        CaptureThinkingEvent(publication, detector);

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
                    StartThinkingControlLocked(request.Plan, candidate, sight, now, request.Models?.GetValueOrDefault(candidate.Id));
                else
                    PostThinkingHandoff("The offered target changed before controls were available.");
            }

            if (thinkingLease is not { } lease) return false;
            if (gameplay && (lease.Ability is not null || lease.Evaluation?.WantsSkillSample(capturedAt) == true))
                thinkingPlaybackFrame = new(lease, publication, input, blockage);
            else
            {
                if (!gameplay)
                    lease.Evaluation?.RecordFrame(frame, new(capturedAt, now, generation, room, 0),
                        null, null, [], null, null, renderedFrame, skill: true);
                DriveThinkingMotionLocked(lease, publication, input, blockage, null);
            }
            return true;
        }
    }

    private void DriveThinkingFrame(ThinkingPlaybackFrame frame, BotCaptureObservation? observation)
    {
        lock (thinkingControlLock)
            if (ReferenceEquals(thinkingLease, frame.Lease))
                DriveThinkingMotionLocked(frame.Lease, frame.Frame, frame.Input, frame.Blockage, observation);
    }

    private void DriveThinkingMotionLocked(ThinkingLease lease, ThinkingFrame frame, InputButtons? input, double blockage,
        BotCaptureObservation? observation)
    {
        if (!gameControls.OwnsBotControl(lease.Owner)
            || lease.PriorRun is { } prior && !ReferenceEquals(prior, Volatile.Read(ref botPlayback)))
        {
            StopThinkingControlLocked("Policy input was interrupted.");
            return;
        }
        var now = BotCaptureClock();
        var passage = frame.Reading?.SatisfiedInspection == lease.Maneuver.StartedAt;
        var motion = lease.Maneuver.Step(frame.Sight, now, input ?? InputButtons.None, passage, blockage);
        if (motion.Finished)
        {
            if (lease.Maneuver.Candidate.MenuPage is null)
                Volatile.Write(ref lastThinkingOutcome, new(lease.Plan.Scope, lease.Plan.DecisionId, motion.Status));
            if (lease.Maneuver.Candidate is { Action: ThinkingAction.MenuMore, MenuPage: { } page }
                && motion.Status.StartsWith("Menu advanced", StringComparison.Ordinal))
                _ = Dispatcher.InvokeAsync(() => { if (thinkingDisplayFrame?.Sight.Scope == lease.Plan.Scope) browsedMenuPages.Add(page.Key); });
            StopThinkingControlLocked(motion.Status, "skill-finished");
            return;
        }
        var buttons = motion.Buttons;
        var deadline = motion.Deadline;
        if (lease.Ability is { } ability)
        {
            try
            {
                buttons = ability.Apply(motion, frame.Sight, observation, input, now, gameControls.SkillControlsAvailable);
                deadline = ability.Deadline(deadline, now);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            {
                StopThinkingControlLocked("Ability inference failed. " + error.Message);
                return;
            }
        }
        if (buttons == InputButtons.None)
            gameControls.NeutralizeBotControl(lease.Owner);
        else if (deadline <= now || !gameControls.RenewBotControl(lease.Owner, buttons, deadline))
        {
            StopThinkingControlLocked("Policy controls expired.");
            return;
        }
        else if (buttons == InputButtons.ActionZ && lease.Maneuver.Candidate is { Action: ThinkingAction.MenuSelect, ElementId: { } id })
            sentMenuConfirm = (frame.Sight.Scope, id, now);
        if (observation is not null && input is { } applied)
            lease.Evaluation?.RecordDecision(observation, buttons, applied, frame.Sight.Scope.Context);
        if (motion.BeginReading)
        {
            var old = frame.Reading?.Lines ?? [];
            _ = Dispatcher.InvokeAsync(() =>
            {
                lock (thinkingControlLock)
                    if (ReferenceEquals(thinkingLease, lease))
                        gameTextReader.BeginInspection(frame.Sight.Scope.Context, lease.Maneuver.StartedAt, old);
            });
        }
    }

    private IReadOnlyList<ThinkingRoute> ThinkingHackerRoutes(BgraFrame frame, ReceiverGroundTruthSample receiver,
        int? renderedFrame, long generation, double capturedAt, int room, InputButtons? input)
    {
        if (Volatile.Read(ref thinkingAuthored) is not { } authored) return [];
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
                new(capturedAt, capturedAt, generation, room, 0), input, 0,
                objective: objectives.SelectedFor(scene), roomKey: HackerLayoutId("room", scene).ToString("D").ToUpperInvariant(),
                automaticObjective: objectives.IsAutomatic(scene), registeredRoom: registered);
            thinkingRouteAt = capturedAt;
            thinkingRouteScene = scene;
            thinkingRouteSession = receiver.SessionId;
        }
        return thinkingVisit.ThinkingRoutes.Select(route =>
            route with { Chart = FormattableString.Invariant($"{route.Chart}/{authored.Epoch}") }).ToArray();
    }

    private void StartThinkingControlLocked(ThinkingPlan plan, ThinkingCandidate candidate, ThinkingSight sight, double now,
        ThinkingAbilityModel? model)
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
            if (model is not null && !ReferenceEquals(model.Motor, Volatile.Read(ref prior.Loaded)?.Motor)) model = null;
        }
        else owner = gameControls.BeginBotControl();

        if (owner is not { } leaseOwner)
        {
            PostThinkingHandoff("Release the game controls before a policy can start.");
            return;
        }
        var ability = model is null || model.Starter is null && !gameControls.SkillControlsAvailable
            ? null : new ThinkingAbilityDrive(model.Skill, model.Motor, candidate, model.Starter);
        var authored = prior?.Authored ?? Volatile.Read(ref thinkingAuthored)?.Labels ?? new GroundTruthLineDocument();
        var motor = candidate.Action is ThinkingAction.Traverse or ThinkingAction.Explore && ability is not null ? model?.Motor : null;
        var evaluation = prior is null
            ? new BotEvaluationRecorder(BotEvaluationRoot, sight.Scope.Domain, motor?.Name ?? "Built-in playing policies",
                motor?.SourceCheckpointSha256, authored, sight.Scope.Generation, sight.CapturedAt)
            : Volatile.Read(ref prior.Evaluation);
        evaluation?.RecordSkill(plan, candidate, plan.Scope.Context, "selected", now);
        Volatile.Write(ref thinkingLease, new(leaseOwner, prior, new(candidate, sight, now), ability, plan, authored, evaluation));
        PostThinkingHandoff($"Dispatched · {candidate.Id} · {candidate.Action}" + (ability is null ? "" : " · learned abilities"));
    }

    private async Task<string> SubmitThinkingPlanAsync(ThinkingPlan plan, CancellationToken token, ThinkingFrame sourceFrame)
    {
        if (gameMode == GameRuntimeMode.Normal) return "Inspection only · Normal mode.";
        if (!thinkingAutomatic || !CanDispatchThinking()) return "Automatic policies are paused or controls are unavailable.";
        var active = Volatile.Read(ref botPlayback);
        var motor = active?.Domain == plan.Scope.Domain ? Volatile.Read(ref active.Loaded)?.Motor : null;
        IReadOnlyDictionary<string, ThinkingAbilityModel>? models = null;
        if (plan.Scores.Any(s => s.Candidate.Action == ThinkingAction.Explore) || gameControls.SkillControlsAvailable && plan.Scores.Count > 0)
        {
            try
            {
                models = await Task.Run(() => LoadThinkingAbilityModels(plan, motor), token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                return "Ability model unavailable. " + error.Message;
            }
        }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(sourceFrame, thinkingDisplayFrame)) return "A newer event is available.";
        if (!thinkingAutomatic || !CanDispatchThinking()) return "Automatic policies are paused or controls are unavailable.";
        return QueueThinkingPlan(plan, models);
    }

    private Dictionary<string, ThinkingAbilityModel> LoadThinkingAbilityModels(ThinkingPlan plan, BotNativePolicy? motor)
    {
        var models = new Dictionary<string, ThinkingAbilityModel>(StringComparer.Ordinal);
        var skills = new Dictionary<BotAbilitySkill, BotLearnedSkill?>();
        foreach (var score in plan.Scores)
        {
            var candidate = score.Candidate;
            var ability = candidate.Action switch
            {
                ThinkingAction.Traverse => Load(BotAbilitySkill.Traverse),
                ThinkingAction.Explore => Load(BotAbilitySkill.Explore),
                ThinkingAction.Fight => Load(BotAbilitySkill.Fight),
                _ => null
            };
            if (ability is null && candidate.Action != ThinkingAction.Explore) continue;
            motor ??= new BotModelStore(BotRoot, plan.Scope.Domain).LoadPlayback()?.Policy;
            if (motor is not null && ability is not null && motor.Version != ability.ObservationSchema)
                throw new InvalidDataException("The ability model observation schema differs from the selected PPO checkpoint.");
            var starter = candidate.Action == ThinkingAction.Explore ? BotStarterPolicy.Load(BotAbilitySkill.Explore,
                Math.Max(BotPolicyContract.CurrentVersion, ability?.ObservationSchema ?? motor?.Version ?? BotPolicyContract.CurrentVersion)) : null;
            models[candidate.Id] = new(ability, motor, starter);
        }
        return models;

        BotLearnedSkill? Load(BotAbilitySkill skill)
        {
            if (!skills.TryGetValue(skill, out var model))
                skills[skill] = model = BotLearnedSkill.Load(BotRoot, plan.Scope.Domain, skill);
            return model;
        }
    }

    private string QueueThinkingPlan(ThinkingPlan plan, IReadOnlyDictionary<string, ThinkingAbilityModel>? models)
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
            pendingThinkingControl = new(plan, now, models);
            return "Validating the chosen target on the next frame…";
        }
    }

    private bool StopThinkingControl(string? status = null, string reason = "interrupted")
    {
        lock (thinkingControlLock)
        {
            var wasActive = pendingThinkingControl is not null || thinkingLease is not null;
            StopThinkingControlLocked(status, reason);
            return wasActive;
        }
    }

    private void StopThinkingControlLocked(string? status, string reason = "interrupted")
    {
        var pending = pendingThinkingControl;
        pendingThinkingControl = null;
        if (thinkingLease is { } lease)
        {
            Volatile.Write(ref thinkingLease, null);
            lease.Evaluation?.RecordSkill(lease.Plan, lease.Maneuver.Candidate, lease.Plan.Scope.Context,
                status ?? reason, BotCaptureClock());
            if (lease.PriorRun is null) gameControls.EndBotControl(lease.Owner);
            else gameControls.NeutralizeBotControl(lease.Owner);
            if (lease.PriorRun is null && lease.Evaluation is { } evaluation)
                FinishEvaluation(evaluation, reason);
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
        && botTrainingCancellation is null;
}
