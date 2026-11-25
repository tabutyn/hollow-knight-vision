using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class BotDesktopEvaluationTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Scene = "Tutorial_01";
    private const long Generation = 18;
    private const int Width = 192, Height = 128;
    private const InputButtons Action = InputButtons.Right | InputButtons.ActionX;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Run()
    {
        var scope = new DispatcherFrame();
        var task = VerifyAsync();
        task.GetAwaiter().OnCompleted(() => scope.Continue = false);
        Dispatcher.PushFrame(scope);
        task.GetAwaiter().GetResult();
        Console.WriteLine("PASS Desktop playback saves Game Play and Hacker evaluations with checkpoint, receiver controls, and exact frames");
    }

    private static async Task VerifyAsync()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-desktop-bot-evaluations-");
        const string labelsEnvironment = "HKV_GROUND_LABEL_ROOT";
        var previousLabels = Environment.GetEnvironmentVariable(labelsEnvironment);
        Environment.SetEnvironmentVariable(labelsEnvironment, temporary.FullName);
        try
        {
            var label = new GroundTruthWorldLine(Scene, -2, 4, 0, GroundTruthLineKind.Positive, false);
            var labels = new GroundTruthLineStore();
            labels.Save(new GroundTruthLineDocument { Lines = [label] });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var domain in new[] { "gameplay", "hacker" })
                await VerifyDomainAsync(temporary.FullName, domain, label, timeout.Token);
            await VerifyOpeningWithoutCheckpointAsync(Path.Combine(temporary.FullName, "opening"), timeout.Token);
            await VerifyOpeningWithoutCheckpointAsync(Path.Combine(temporary.FullName, "opening-timeout"), timeout.Token,
                expireHandoff: true);
            await VerifyCheckpointControlsAsync(Path.Combine(temporary.FullName, "versions"), timeout.Token);
            await VerifyThinkingLeaseAsync(Path.Combine(temporary.FullName, "thinking"), timeout.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(labelsEnvironment, previousLabels);
            temporary.Delete(recursive: true);
        }
    }

    private static async Task VerifyThinkingLeaseAsync(string root, CancellationToken token)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var session = Guid.NewGuid();
        var states = Channel.CreateUnbounded<ReceiverInputSnapshot>();
        var peer = ServeAsync(server, states.Writer, token, skills: true);
        var window = new MainWindow(GameRuntimeMode.Modded, null, root, root, () => new ReceiverClient(session, port: port),
            enableThinking: true);
        var bridge = (WindowsGameControlForwarder)Field("gameControls").GetValue(window)!;
        var applied = Channel.CreateUnbounded<InputButtons>();
        bridge.BotInputSent += (_, buttons, _) => applied.Writer.TryWrite(buttons);
        try
        {
            Field("recentCaptureGeneration").SetValue(window, Generation);
            Field("workspace").SetValue(window, "Hacker");
            Field("recognizedMenuContext").SetValue(window, "game");
            Field("thinkingControlAvailable").SetValue(window, true);
            bridge.Start();
            while (bridge.PauseState != GamePauseState.Ready) await Task.Delay(15, token);
            Check(bridge.SkillControlsAvailable, "The Windows bridge did not negotiate the shared skill capability.");
            bridge.Press(InputButtons.SuperDash);
            await ExpectWireState(InputButtons.SuperDash);
            bridge.ReleaseAll();
            await ExpectWireState(InputButtons.None);
            WriteCheckpoint(Path.Combine(root, "bot-v1"), "hacker", "Skill handoff actor", "bot-thinking",
                new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
            await window.StartBotPlaybackAsync().WaitAsync(token);
            var run = Field("botPlayback").GetValue(window)!;
            Check(run is not null, "Preparing Thinking needs the actual Windows bot lease.");
            var owner = (Guid)run!.GetType().GetProperty("Owner", Instance)!.GetValue(run)!;
            var first = WithRecovery(3, session);
            object? ready = null;
            for (var index = 0; index < 4 && ready is null; index++)
            {
                if (index > 0) await Task.Delay(140, token);
                var initial = Frame();
                ready = Method("ObserveBotStartup").Invoke(window, [initial.Image, Generation, initial.Time, true, true, first, null, null]);
            }
            Check(ReferenceEquals(ready, run), "The starter requires live gameplay before passing the PPO lease to Thinking.");
            Observe(first);
            var plan = OfferFocus();
            Check(((string)Method("QueueThinkingPlan").Invoke(window, [plan, null])!).Contains("next frame", StringComparison.Ordinal),
                "A matching score must wait for a fresh analysis frame.");
            Check(Observe(first), "The native maneuver did not consume the chosen offer.");
            await ExpectSkillPress(InputButtons.ActionA);
            Check(Observe(WithRecovery(4, session)), "The frame that verified healing should release Focus.");
            await ExpectWireState(InputButtons.None);
            Check(bridge.OwnsBotControl(owner) && ReferenceEquals(Field("botPlayback").GetValue(window), run),
                "The PPO lease should resume after a Thinking maneuver completes.");
            Check((string?)Method("QueueThinkingPlan").Invoke(window, [plan, null]) == "Decision already consumed.",
                "One model reply may only drive one maneuver.");

            Observe(first);
            var stale = OfferFocus();
            _ = Method("QueueThinkingPlan").Invoke(window, [stale, null]);
            Check(!Observe(WithRecovery(3, Guid.NewGuid())), "A score from an old receiver must not drive Focus.");
            Check(Field("thinkingLease").GetValue(window) is null && bridge.OwnsBotControl(owner),
                "Stale Thinking replies should leave ordinary playback ownership intact.");
            Check((bool)Method("HandleLocalShortcut").Invoke(window, [Key.B, false, ModifierKeys.None])!,
                "B still stops ordinary playback after a Thinking policy.");
            await ExpectWireState(InputButtons.None);

            Field("workspace").SetValue(window, "Gameplay");
            Observe(first);
            var gameplay = OfferFocus();
            Check(gameplay.Scope.Domain == BotCaptureDomain.Gameplay,
                "Game Play v2 must bind the same exact receiver actors without changing the playback observation domain.");
            _ = Method("QueueThinkingPlan").Invoke(window, [gameplay, null]);
            Check(Observe(first), "A fresh Game Play policy should use the receiver lease without a PPO checkpoint.");
            await ExpectSkillPress(InputButtons.ActionA);
            _ = Method("PathRecording_Click").Invoke(window, [window, new RoutedEventArgs()]);
            Check(Field("botCapture").GetValue(window) is null
                && ((TextBlock)window.FindName("StatusText")).Text.Contains("Stop the bot", StringComparison.Ordinal),
                "Human recording must refuse a standalone Thinking lease.");
            window.EnableBotTrialControl();
            var status = (await BotTrialControl.SendAsync("status", token)).Status!.Value;
            Check(status.GetProperty("active").GetBoolean() && status.GetProperty("botDomain").GetString() == "gameplay"
                && status.GetProperty("skillCandidate").GetString() == gameplay.Scores.Single().Candidate.Id,
                "Trial diagnostics must show the standalone maneuver and its domain.");
            Check((await BotTrialControl.SendAsync("stop", token)).Accepted,
                "The trial stop command must release a standalone Thinking maneuver.");
            await ExpectWireState(InputButtons.None);

            Field("workspace").SetValue(window, "Hacker");
            var labels = new GroundTruthLineDocument
            {
                Lines = [new(Scene, -3, 5, 0, GroundTruthLineKind.Positive, false)]
            };
            var authored = Field("thinkingAuthored");
            authored.SetValue(window, Activator.CreateInstance(authored.FieldType, 1, labels));
            var modelFolder = Directory.CreateDirectory(Path.Combine(root, "bot-v1", "skill-actors", "hacker"));
            File.WriteAllText(Path.Combine(modelFolder.FullName, "fight.json"), JsonSerializer.Serialize(new
            {
                version = 2, domain = "hacker", skill = "fight", observationSchema = BotPolicyContract.LandingVersion,
                historyLength = BotRecordingContract.HistoryLength, labels = new[] { "none", "dash" }, inputColumns = new[] { 0 },
                layers = new[] { new { inputSize = 1, outputSize = 2, activation = "linear", weights = new[] { 0, 0 }, biases = new[] { 0, 1 } } }
            }, Json));
            var target = first with { Actors = first.Actors! with { Enemies = [new(3, "Crawlid", 109, 49.6, 12, 12)] } };
            Observe(target);
            var combat = Offer(ThinkingAction.Fight);
            var abilityModels = Method("LoadThinkingAbilityModels").Invoke(window, [combat, null]);
            Check(((string)Method("QueueThinkingPlan").Invoke(window, [combat, abilityModels])!).Contains("next frame", StringComparison.Ordinal),
                "The learned fight ability did not enter the regular Thinking handoff.");
            Check(Observe(target), "A fresh exact observation must reach the learned fight model.");
            await ExpectSkillPress(InputButtons.Right | InputButtons.ActionX | InputButtons.Dash);
            Check(Observe(first), "A disappeared bound actor must release the learned ability.");
            await ExpectWireState(InputButtons.None);
            Check(Field("thinkingLease").GetValue(window) is null, "The lost actor retained its learned ability lease.");

            await ((Task)Field("botEvaluationWrites").GetValue(window)!).WaitAsync(token);
            var evaluationRoot = Path.Combine(root, "bot-v1", "evaluations");
            var hacker = Directory.GetDirectories(Path.Combine(evaluationRoot, "hacker"));
            var attached = hacker.Single(path => Read<JsonElement>(path, "evaluation.json").TryGetProperty("modelSHA256", out _));
            var autonomous = hacker.Single(path => path != attached);
            var standalone = Directory.GetDirectories(Path.Combine(evaluationRoot, "gameplay")).Single();
            VerifyEvaluation(attached, plan, "focus", "Focus restored a mask.", "user-stop", "a");
            VerifyEvaluation(standalone, gameplay, "focus", "Bot stopped.", "local-control", "a");
            VerifyEvaluation(autonomous, combat, "native", "Bound target expired.", "skill-finished", "dash");
            Check(BotCaptureSession.Library(Path.Combine(root, "bot-v1", "demonstrations")).Count == 0,
                "The Thinking evaluation must never enter the demonstration library.");

            (BgraFrame Image, double Time) Frame()
            {
                var image = new BgraFrame(Width, Height, Width * 4, new byte[Width * Height * 4],
                    Math.Max(1, Stopwatch.GetTimestamp()), DateTimeOffset.UtcNow);
                return (image, Clock());
            }
            ReceiverGroundTruthSample WithRecovery(int health, Guid connection)
            {
                var truth = Telemetry(connection, 42);
                return truth with { AtlasGameplayActive = true, Actors = truth.Actors! with
                {
                    Enemies = [], Recovery = new ReceiverRecovery(66, health, 5, true)
                } };
            }
            bool Observe(ReceiverGroundTruthSample truth)
            {
                var current = Frame();
                var handled = (bool)Method("ObserveThinkingFrame").Invoke(window,
                    [current.Image, Generation, current.Time, 1, 1L, "game", null, truth, 42, null, true, InputButtons.None, 0d])!;
                if (Field("thinkingPlaybackFrame").GetValue(window) is { } skillFrame)
                    _ = Method("CaptureBotObservations").Invoke(window, [
                        current.Image, Generation, Generation, current.Time, 1, 1UL, 42, truth, null,
                        Array.Empty<DetectedFloorLine>(), TerrainWallDetection.Empty, true, null, null, null, InputButtons.None,
                        null, 1L, GroundHypothesisTrackingResult.Empty, true, Array.Empty<PixelRect>(), null, false,
                        null, Array.Empty<GroundHypothesisAtlasLine>(), null, null, skillFrame
                    ]);
                return handled;
            }
            ThinkingPlan OfferFocus() => Offer(ThinkingAction.Focus);
            ThinkingPlan Offer(ThinkingAction action)
            {
                var publication = Field("latestThinkingFrame").GetValue(window)!;
                var sight = (ThinkingSight)publication.GetType().GetProperty("Sight")!.GetValue(publication)!;
                var offer = new ThinkingPolicyOffer(sight);
                var candidate = offer.Candidates.Single(item => item.Action == action);
                using var response = JsonDocument.Parse(JsonSerializer.Serialize(new { scores = new Dictionary<string, int> { [candidate.Id] = 98 } }));
                return offer.Resolve(response.RootElement);
            }

            static void VerifyEvaluation(string directory, ThinkingPlan plan, string control, string outcome,
                string reason, string button)
            {
                var manifest = Read<BotCaptureManifest>(directory, "manifest.json");
                var metadata = Read<JsonElement>(directory, "evaluation.json");
                var log = File.ReadLines(Path.Combine(directory, metadata.GetProperty("skillEvents").GetString()!))
                    .Select(line => JsonSerializer.Deserialize<JsonElement>(line, Json)).ToArray();
                Check(manifest is { Status: "completed", ExpertDemonstration: false, TerrainAuditCount: > 0 }
                    && manifest.StopReason == reason && metadata.GetProperty("actionSource").GetString() == "skill-plan"
                    && log.Length == 2 && log[0].GetProperty("event").GetString() == "selected"
                    && log[0].GetProperty("plan").GetProperty("decisionID").GetInt64() == plan.DecisionId
                    && log[0].GetProperty("choice").GetProperty("points").GetInt32() == 98
                    && log[0].GetProperty("choice").GetProperty("control").GetString() == control
                    && log[1].GetProperty("event").GetString() == outcome,
                    "The native policy archive lost its actual selection, original plan, or runtime outcome.");
                var controls = File.ReadLines(Path.Combine(directory, "events.jsonl"))
                    .Select(line => JsonSerializer.Deserialize<JsonElement>(line, Json))
                    .Where(row => row.TryGetProperty("button", out var name) && name.GetString() == button).ToArray();
                Check(controls.Length >= 2 && controls[0].GetProperty("isPressed").GetBoolean()
                    && !controls[^1].GetProperty("isPressed").GetBoolean(),
                    "An evaluation must pair receiver-sent skill controls with their release.");
            }
        }
        finally
        {
            Field("isClosing").SetValue(window, true);
            await ((Task)Method("PrepareShutdownAsync").Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(10));
            await peer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Console.WriteLine("PASS Thinking Focus and learned Dash use Windows receiver leases and release on expired bounds");

        async Task ExpectWireState(InputButtons buttons)
        {
            while (true)
            {
                var state = await states.Reader.ReadAsync(token);
                if (state.HeldButtons == buttons && state.Enabled == (buttons != InputButtons.None)) return;
            }
        }

        async Task ExpectSkillPress(InputButtons buttons)
        {
            await ExpectWireState(buttons);
            while (await applied.Reader.ReadAsync(token) != buttons) { }
        }
    }

    private static async Task VerifyCheckpointControlsAsync(string root, CancellationToken token)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var session = Guid.NewGuid();
        var peer = ServeAsync(server, Channel.CreateUnbounded<ReceiverInputSnapshot>().Writer, token);
        var botRoot = Path.Combine(root, "bot-v1");
        var captures = Path.Combine(botRoot, "demonstrations");
        var created = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var store = new BotModelStore(botRoot, BotCaptureDomain.Hacker);
        var initial = PublishPair(created);
        WriteCheckpoint(botRoot, "hacker", "First Hacker", "bot-first", created);
        _ = store.LoadPlayback();
        WriteCheckpoint(botRoot, "hacker", "Second Hacker", "bot-second", created.AddMinutes(1), [initial.Hacker]);
        WriteCheckpoint(botRoot, "gameplay", "Game Play actor", "bot-gameplay", created, [initial.Gameplay]);
        var window = new MainWindow(GameRuntimeMode.Modded, null, root, root, () => new ReceiverClient(session, port: port));
        try
        {
            var domain = (ComboBox)window.FindName("BotTrainingDomain");
            var versions = (ComboBox)window.FindName("BotPlaybackVersion");
            var use = (Button)window.FindName("BotUseVersion");
            var restore = (Button)window.FindName("BotRestorePrevious");
            var pending = (TextBlock)window.FindName("BotNewCaptureSummary");
            Refresh();
            await WaitFor(() => versions.SelectedValue as string == "bot-gameplay");
            Check(versions.Items.Count == 1 && !restore.IsEnabled, "Game Play must show only its own verified checkpoint.");

            domain.SelectedIndex = 1;
            Check(versions.SelectedValue as string == "bot-second" && versions.Items.Count == 2 && restore.IsEnabled,
                "Hacker must show its newest checkpoint and offer the previously used version.");
            Field("recentCaptureGeneration").SetValue(window, Generation);
            Field("workspace").SetValue(window, "Hacker");
            Field("recognizedMenuContext").SetValue(window, "game");
            var bridge = (WindowsGameControlForwarder)Field("gameControls").GetValue(window)!;
            bridge.Start();
            await WaitFor(() => bridge.PauseState == GamePauseState.Ready);
            await window.StartBotPlaybackAsync().WaitAsync(token);
            Check(Field("botPlayback").GetValue(window) is not null, "The current Hacker checkpoint should be ready for playback.");

            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => versions.SelectedValue as string == "bot-first" && restore.IsEnabled);
            Check(Field("botPlayback").GetValue(window) is null && store.Catalog().ActiveId == "bot-first",
                "Rollback must release the previous actor and persist the new selection.");
            Check(pending.Text == "0 new runs · Hacker 0 · Game Play 0", "Rollback must preserve completed clip counts.");

            WriteCheckpoint(botRoot, "hacker", "Third Hacker", "bot-third", created.AddMinutes(2), [initial.Hacker]);
            Refresh();
            await WaitFor(() => versions.SelectedValue as string == "bot-third");
            versions.SelectedValue = "bot-second";
            Check(use.IsEnabled, "An earlier verified checkpoint should be available for explicit selection.");
            use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !use.IsEnabled && restore.IsEnabled);
            Check(store.LoadPlayback()?.Policy.Name == "Second Hacker"
                && new BotModelStore(botRoot, BotCaptureDomain.Gameplay).Catalog().ActiveId == "bot-gameplay",
                "Selection should persist until a new export and must stay scoped to Hacker.");

            var fresh = PublishPair(created.AddMinutes(3));
            var clips = (DataGrid)window.FindName("BotCaptureClips");
            clips.SelectedIndex = -1;
            using (File.Open(Events(fresh.Hacker), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (File.Open(Events(fresh.Gameplay), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Refresh();
                Check(pending.Text == "1 new run · Hacker 1 · Game Play 1",
                    "The paired capture should appear from its manifests while the preview events are unavailable.");
            }
            WriteCheckpoint(botRoot, "hacker", "Trained Hacker", "bot-trained", created.AddMinutes(4), [initial.Hacker, fresh.Hacker]);
            Refresh();
            await WaitFor(() => pending.Text == "1 new run · Hacker 0 · Game Play 1");
            Check(versions.SelectedValue as string == "bot-trained", "New training must reduce the correct count while activating its policy.");

            void Refresh() => Method("RefreshBotLibrary").Invoke(window, null);
            async Task WaitFor(Func<bool> condition)
            {
                while (!condition()) await Task.Delay(25, token);
            }
        }
        finally
        {
            Field("isClosing").SetValue(window, true);
            await ((Task)Method("PrepareShutdownAsync").Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(10));
            await peer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Console.WriteLine("PASS Desktop bot version controls restore and select each domain and follow new completed checkpoints");
        Console.WriteLine("PASS Desktop new capture counts publish before preview and follow training independently");

        (Guid Hacker, Guid Gameplay) PublishPair(DateTimeOffset time)
        {
            var hacker = Guid.NewGuid();
            var gameplay = Guid.NewGuid();
            Save(hacker, gameplay, "hacker");
            Save(gameplay, hacker, "gameplay");
            return (hacker, gameplay);

            void Save(Guid id, Guid partner, string domain)
            {
                var folder = Directory.CreateDirectory(Path.Combine(captures, BotCaptureSession.DirectoryName(id))).FullName;
                var clip = new BotCaptureManifest(BotRecordingContract.SchemaVersion, id, time, 1, "modded", "fixture",
                    "receiver-sent", "completed", .4, 4, 4, 2, 0, domain, hacker, partner);
                File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(clip, Json));
                File.WriteAllText(Events(id), "{\"type\":\"input\"}\n");
            }
        }

        string Events(Guid id) => Path.Combine(captures, BotCaptureSession.DirectoryName(id), "events.jsonl");
    }

    private static async Task VerifyOpeningWithoutCheckpointAsync(string root, CancellationToken token, bool expireHandoff = false)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var session = Guid.NewGuid();
        var states = Channel.CreateUnbounded<ReceiverInputSnapshot>();
        var peer = ServeAsync(server, states.Writer, token);
        var saves = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName;
        var archives = Path.Combine(root, "bot-v1", "save-archives");
        var window = new MainWindow(GameRuntimeMode.Modded, null, root, root, () => new ReceiverClient(session, port: port));
        try
        {
            Field("recentCaptureGeneration").SetValue(window, Generation);
            Field("workspace").SetValue(window, "Hacker");
            Field("recognizedMenuContext").SetValue(window, "select-profile");
            var bridge = (WindowsGameControlForwarder)Field("gameControls").GetValue(window)!;
            bridge.Start();
            while (bridge.PauseState != GamePauseState.Ready) await Task.Delay(10, token);
            await window.StartBotPlaybackAsync(new BotDemoSaveArchive(saves, archives)).WaitAsync(token);
            var run = Field("botPlayback").GetValue(window);
            Check(run is not null, "An untrained bot must still start its menu routine.");
            var owner = (Guid)run!.GetType().GetProperty("Owner", Instance)!.GetValue(run)!;

            var pixels = new byte[Width * Height * 4];
            var index = 0;
            var selector = new MenuStencilMatch("selector", "Selector", new(10, 10, 6, 6), 1, null);
            await ExpectAction(InputButtons.ActionZ, "select-profile", receiverGameplay: false);
            Check(File.Exists(Path.Combine(Directory.GetDirectories(archives).Single(), "manifest.json"))
                && Directory.GetFileSystemEntries(saves).Length == 0,
                "The opening must start only after the fixture's empty slot has a verified archive.");
            await ExpectAction(InputButtons.ActionX, "quit-game", receiverGameplay: true);
            await ExpectAction(InputButtons.ActionZ, "brightness", receiverGameplay: true, showHud: true);
            Check(bridge.OwnsBotControl(owner), "Conflicting menu and HUD cues must preserve the opening until Gameplay is admitted.");

            var preparingUntil = Clock() + .8;
            var released = false;
            do
            {
                Step(null, receiverGameplay: false, recognizedGameplay: true, admitGameplay: false, showHud: true, holdsHealthMask: true);
                while (states.Reader.TryRead(out var state))
                {
                    Check(!released || !state.Enabled, "A visual Gameplay HUD must suppress skip pulses while the receiver and profile Atlas are getting ready.");
                    released |= !state.Enabled && state.HeldButtons == InputButtons.None;
                }
                await Task.Delay(40, token);
            } while (Clock() < preparingUntil);
            Check(released && bridge.OwnsBotControl(owner), "Controls must stay neutral while the receiver and profile Atlas are getting ready.");
            var handoffDeadline = run.GetType().GetField("HandoffDeadline", Instance)!;
            var remaining = (double)handoffDeadline.GetValue(run)! - Clock();
            Check(remaining is > 12 and <= 15, "A stable Gameplay HUD must start the bounded handoff even before the receiver is ready.");
            Step(null, receiverGameplay: false, recognizedGameplay: true, admitGameplay: true, showHud: true);
            Check(bridge.OwnsBotControl(owner), "A lagging receiver must defer the handoff even after the Atlas is ready.");

            var status = (System.Windows.Controls.TextBlock)window.FindName("StatusText");
            if (expireHandoff)
            {
                // Advance the pending deadline without waiting for more frames.
                handoffDeadline.SetValue(run, Clock() - .1);
                Method("CheckBotOwnership").Invoke(window, null);
                Check(Field("botPlayback").GetValue(window) is null
                    && status.Text.Contains("Bot controls did not become ready", StringComparison.Ordinal),
                    "The heartbeat must end an expired handoff without waiting for another capture.");
                Check(!bridge.OwnsBotControl(owner) && status.Text.Contains("Gameplay reached.", StringComparison.Ordinal)
                    && status.Text.Contains("Controls returned to you.", StringComparison.Ordinal),
                    "An expired gameplay handoff must release the receiver lease.");
            }
            else
            {
                object? started = null;
                while (started is null)
                {
                    started = Step(null, receiverGameplay: true, recognizedGameplay: true, admitGameplay: true, showHud: true);
                    await Task.Delay(40, token);
                }
                Check(ReferenceEquals(started, run) && bridge.OwnsBotControl(owner)
                    && !Directory.Exists(Path.Combine(root, "bot-v1", "models")),
                    "The verified gameplay handoff should retain the same lease before any checkpoint exists.");
                var gameplayFrame = new BgraFrame(Width, Height, Width * 4, pixels, ++index, DateTimeOffset.UtcNow);
                _ = Method("CaptureBotObservations").Invoke(window, [
                    gameplayFrame, Generation, Generation, Clock(), 1, 1UL, index, Telemetry(session, index), null,
                    Array.Empty<DetectedFloorLine>(), TerrainWallDetection.Empty, true, null, null, null, InputButtons.None,
                    run, 1L, GroundHypothesisTrackingResult.Empty, true, Array.Empty<PixelRect>(), null, false,
                    null, Array.Empty<GroundHypothesisAtlasLine>(), null, null, null
                ]);
                while ((await states.Reader.ReadAsync(token)).HeldButtons != InputButtons.Right) { }
                Check((bool)Method("HandleLocalShortcut").Invoke(window, [Key.B, false, ModifierKeys.None])!
                    && !bridge.OwnsBotControl(owner), "B must stop the built-in policy and return controls.");
                while ((await states.Reader.ReadAsync(token)).Enabled) { }
            }
            await ((Task)Field("botEvaluationWrites").GetValue(window)!).WaitAsync(token);
            var evaluationRoot = Path.Combine(root, "bot-v1", "evaluations");
            if (expireHandoff)
                Check(!Directory.Exists(evaluationRoot), "An opening without a playable frame must leave no evaluation.");
            else
            {
                var evaluation = Directory.GetDirectories(Path.Combine(evaluationRoot, "hacker")).Single();
                var metadata = Read<JsonElement>(evaluation, "evaluation.json");
                Check(metadata.GetProperty("actionSource").GetString() == "starter-policy"
                    && !metadata.TryGetProperty("modelSHA256", out _) && !metadata.TryGetProperty("skillEvents", out _),
                    "A built-in run must have its own evaluation with no fabricated model or skill plan.");
            }

            async Task ExpectAction(InputButtons button, string context, bool receiverGameplay, bool showHud = false)
            {
                while (true)
                {
                    Step(context, receiverGameplay, recognizedGameplay: false, admitGameplay: false, showHud);
                    while (states.Reader.TryRead(out var state))
                        if (state.Enabled && state.HeldButtons == button) return;
                    Check(bridge.OwnsBotControl(owner), $"Bot lost control before sending {button}.");
                    await Task.Delay(30, token);
                }
            }

            object? Step(string? context, bool receiverGameplay, bool recognizedGameplay, bool admitGameplay, bool showHud,
                bool holdsHealthMask = false)
            {
                ++index;
                var observed = DateTimeOffset.UtcNow;
                var image = new BgraFrame(Width, Height, Width * 4, pixels, index, observed);
                var timestamp = (observed - DateTimeOffset.UnixEpoch).TotalSeconds;
                var menu = context is null ? null : new MenuStencilResult(context, true, 1, "Slot 4",
                    [], [selector, selector], [], 0, 0, 1, MenuStencilPhase.Tracking, 1, timestamp, "en")
                {
                    CheckedProfileSlots = context == "select-profile" ? [1, 2, 3, 4] : [],
                    SelectedOptionIdentifier = context == "select-profile" ? "select-profile.slot-4" : "shared.yes"
                };
                var hud = showHud
                    ? new HudStencilResult([new(0, "full", default, 1), new(1, "full", default, 1)],
                        null, [], [], [], default, holdsHealthMask, timestamp)
                    : null;
                return Method("ObserveBotStartup").Invoke(window,
                    [image, Generation, Clock(), admitGameplay, recognizedGameplay,
                        Telemetry(session, index) with { AtlasGameplayActive = receiverGameplay }, menu, hud]);
            }
        }
        finally
        {
            Field("isClosing").SetValue(window, true);
            await ((Task)Method("PrepareShutdownAsync").Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(10));
            await peer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Console.WriteLine(expireHandoff ? "PASS Desktop bot returns control when the Gameplay HUD handoff expires without new frames"
            : "PASS Desktop bot stops cutscene input at the Gameplay HUD, starts the built-in explorer, and releases with B");
    }

    private static async Task VerifyDomainAsync(string root, string domain, GroundTruthWorldLine label,
        CancellationToken token)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var session = Guid.NewGuid();
        var states = Channel.CreateUnbounded<ReceiverInputSnapshot>();
        var peer = ServeAsync(server, states.Writer, token);
        var modelName = $"Desktop {domain}";
        var usesTrialControl = domain == "hacker";
        var botRoot = Path.Combine(root, "bot-v1");
        var modelHash = WriteCheckpoint(botRoot, domain, modelName);
        var window = new MainWindow(GameRuntimeMode.Modded, null, root, root, () => new ReceiverClient(session, port: port));
        try
        {
            Field("recentCaptureGeneration").SetValue(window, Generation);
            Field("workspace").SetValue(window, domain == "hacker" ? "Hacker" : "Gameplay");
            Field("recognizedMenuContext").SetValue(window, "game");
            if (usesTrialControl)
            {
                window.EnableBotTrialControl();
                var denied = await BotTrialControl.SendAsync("mark-return", token);
                Check(!denied.Accepted && denied.Issue?.Contains("Focus Vision", StringComparison.Ordinal) == true,
                    "An opted-in but unfocused desktop must reject trial checkpoint changes.");
            }
            var bridge = (WindowsGameControlForwarder)Field("gameControls").GetValue(window)!;
            var publishedControl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.BotInputSent += (_, buttons, _) =>
            {
                if (buttons == Action) publishedControl.TrySetResult();
            };
            bridge.Start();
            while (bridge.PauseState != GamePauseState.Ready) await Task.Delay(10, token);
            var guardLease = bridge.BeginBotControl()!.Value;
            Check(!bridge.SkillControlsAvailable && !bridge.RenewBotControl(guardLease, InputButtons.Dash, Clock() + .12),
                "A receiver without the skill capability must reject a learned ability snapshot.");
            bridge.EndBotControl(guardLease);
            await window.StartBotPlaybackAsync().WaitAsync(token);

            // The running policy and its audit must retain the labels present when B started playback.
            var editor = (GroundTruthLabelController)Field("groundTruthLabels").GetValue(window)!;
            Check(editor.Document.Lines.Count == 1, "The window failed to load the fixture's authored floor.");
            editor.Document.Lines.Clear();

            var pixels = Enumerable.Range(0, Width * Height * 4)
                .Select(index => (byte)(index % 4 == 3 ? 255 : index % 239)).ToArray();
            object? run = null;
            for (var index = 0; index < 10 && run is null; index++)
            {
                var image = new BgraFrame(Width, Height, Width * 4, pixels, index, DateTimeOffset.UtcNow);
                run = Method("ObserveBotStartup").Invoke(window,
                    [image, Generation, Clock(), true, true, Telemetry(session, index), null, null]);
                if (run is null) await Task.Delay(80, token);
            }
            Check(run is not null, "The loaded checkpoint never reached gameplay playback.");

            const int renderedFrame = 415;
            var timestamp = Capture(41, renderedFrame, InputButtons.None, 0);
            await ExpectWireState(Action);
            await publishedControl.Task.WaitAsync(token);
            await Task.Delay(110, token);
            var secondTimestamp = Capture(42, renderedFrame + 1, Action, 2);
            await ExpectWireState(Action);
            if (usesTrialControl)
            {
                var status = (await BotTrialControl.SendAsync("status", token)).Status
                    ?? throw new InvalidOperationException("Missing desktop trial status.");
                Check(status.GetProperty("active").GetBoolean() && status.GetProperty("botDecisions").GetInt32() == 2
                    && status.GetProperty("terrainSource").GetString() == "human-authored"
                    && status.GetProperty("botAction").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual([2, 0, 0, 1]),
                    "Trial status reports the actual policy, decisions and terrain source of the current run.");
                Check((await BotTrialControl.SendAsync("stop", token)).Accepted, "Trial stop failed to release the active desktop bot.");
            }
            else Check((bool)Method("HandleLocalShortcut").Invoke(window, [Key.B, false, ModifierKeys.None])!,
                    "B did not handle stopping the active desktop bot.");
            await ExpectWireState(InputButtons.None);
            await ((Task)Field("botEvaluationWrites").GetValue(window)!).WaitAsync(token);

            var directory = Directory.GetDirectories(Path.Combine(botRoot, "evaluations", domain)).Single();
            var manifest = Read<BotCaptureManifest>(directory, "manifest.json");
            Check(manifest is { Status: "completed", ExpertDemonstration: false,
                ObservationCount: 2, UsableObservationCount: 2, TerrainPairedFrameCount: 2, InputEventCount: >= 4 }
                && manifest.BotDomain == domain && manifest.StopReason == (usesTrialControl ? "local-control" : "user-stop"),
                "The finalized evaluation did not describe the desktop playback.");
            var events = File.ReadLines(Path.Combine(directory, "events.jsonl")).ToArray();
            var row = JsonSerializer.Deserialize<BotCaptureObservation>(events[0], Json)!;
            Check(domain == "hacker"
                ? row is { TerrainSource: "human-authored", DetectionFrameId: renderedFrame, AuthoredTerrain.Floors.Count: 1 }
                : row is { TerrainSource: "detected", DetectionFrameId: 41, DetectedTerrain.Floors.Count: 1 },
                "The evaluation saved the wrong input source for its policy.");
            var controls = events.Skip(2).Select(line => JsonSerializer.Deserialize<BotCaptureInput>(line, Json)!).ToArray();
            var perButton = controls.GroupBy(edge => edge.Button).OrderBy(group => group.Key).ToArray();
            Check(perButton.Select(group => group.Key).SequenceEqual(["right", "x"])
                && perButton.All(group => group.First() is { IsPressed: true } first && first.Timestamp >= timestamp
                    && !group.Last().IsPressed),
                "The archive did not preserve the controls sent to the receiver and their release.");
            var score = Read<BotAtlasScore>(directory, "atlas-score.json");
            Check(score is { BlueHealthGained: 2, HeldAttackDecisions: 1 }
                && Math.Abs(score.Reward - 1.95) < 1e-8,
                "The saved Atlas score must come from both admitted desktop policy decisions.");
            var metadata = Read<JsonElement>(directory, "evaluation.json");
            var archive = metadata.GetProperty("visualFrameArchive").Deserialize<RecordedVisualFrameArchive>(Json)!;
            var reader = new VisualReplayArchiveReader(directory, archive, manifest.Id);
            var frames = reader.Manifest.Frames;
            Check(metadata.GetProperty("modelName").GetString() == modelName
                && metadata.GetProperty("modelSHA256").GetString() == modelHash
                && frames.Select(saved => saved.RenderedGameFrame).SequenceEqual([renderedFrame, renderedFrame + 1])
                && frames.Select(saved => saved.CaptureTimestamp).SequenceEqual([timestamp, secondTimestamp])
                && reader.Manifest.GroundInputs.Select(input => input.WindowsMovementButtons).SequenceEqual([InputButtons.None, Action])
                && frames.All(saved => reader.ReadImage(saved).Pixels.SequenceEqual(pixels))
                && new GroundTruthLineStore(Path.Combine(directory, "authored-ground.json")).Load().Lines.SequenceEqual([label]),
                "The evaluation lost the verified checkpoint, frame, masks, or frozen authored reference.");
            Check(BotCaptureSession.Library(Path.Combine(botRoot, "demonstrations")).Count == 0,
                "Bot playback contaminated the training clips.");

            double Capture(long frameId, int unityFrame, InputButtons previous, int blueHealth)
            {
                var frame = new BgraFrame(Width, Height, Width * 4, pixels, frameId, DateTimeOffset.UnixEpoch.AddSeconds(5));
                var knight = new ObjectDetection("game.playable-knight", new(.48, .61, .04, .16), .97f)
                    { SourceFrameIdentifier = frameId };
                var batch = new LiveObjectDetectionBatch([knight], frameId, 5, Generation, 0);
                var masks = GameplaySceneMasks.Create(Width, Height, [knight], null);
                var sample = Telemetry(session, unityFrame) with { BlueHealthOutcomes = new(session, blueHealth) };
                var now = Clock();
                _ = Method("CaptureBotObservations").Invoke(window, [
                    frame, Generation, Generation, now, 1, 1UL, unityFrame, sample,
                    batch, new[] { new DetectedFloorLine(100, new(20, 170)) }, TerrainWallDetection.Empty,
                    true, new AtlasPoint(0, 0), null, new AtlasPoint(96, 100), previous,
                    run, 1L, GroundHypothesisTrackingResult.Empty, true, masks.AtlasExclusions, masks, true, null,
                    Array.Empty<GroundHypothesisAtlasLine>(), null, null, null
                ]);
                return now;
            }
        }
        finally
        {
            Field("isClosing").SetValue(window, true);
            await ((Task)Method("PrepareShutdownAsync").Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(10));
            await peer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task ExpectWireState(InputButtons buttons)
        {
            while (true)
            {
                var state = await states.Reader.ReadAsync(token);
                if (state.HeldButtons == buttons && state.Enabled == (buttons != InputButtons.None)) return;
            }
        }
    }

    private static async Task ServeAsync(TcpListener listener, ChannelWriter<ReceiverInputSnapshot> states,
        CancellationToken token, bool skills = false)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var reader = new StreamReader(client.GetStream());
        using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
        var hello = ReceiverWireCodec.DecodeLine<ReceiverCapabilityRequest>((await reader.ReadLineAsync(token))!);
        string[] capabilities = skills
            ? ["input-state-v1", ReceiverProtocol.PauseCapability, ReceiverProtocol.SkillControlsCapability]
            : ["input-state-v1", ReceiverProtocol.PauseCapability];
        await writer.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCapabilitiesAcknowledgement(
            2, "capabilitiesAck", hello.SessionId, capabilities, 2000)));
        while (await reader.ReadLineAsync(token) is { } line)
            if (ReceiverWireCodec.MessageType(line) == "state")
                states.TryWrite(ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(line));
    }

    private static ReceiverGroundTruthSample Telemetry(Guid session, int frame) => new(
        2, "groundTruth", session, (ulong)frame, frame, frame / 60d, Scene,
        true, 0, 1.28, 0, 0, 0, true, true, true, 0, 2, 0, 0, 2, 0, 3.2, 20, 20,
        96, 49.6, Width, Height, Width, Height)
    {
        Actors = new(1, new(1, "Knight", 96, 49.6, 10, 25.6), []),
        AtlasGameplayActive = true
    };

    private static string WriteCheckpoint(string root, string domain, string modelName,
        string version = "verified-fixture", DateTimeOffset? createdAt = null, Guid[]? included = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "models", domain, version)).FullName;
        var checkpoint = Path.Combine(directory, "model.zip");
        File.WriteAllText(checkpoint, modelName);
        var sourceCheckpointSHA256 = Hash(File.ReadAllBytes(checkpoint));
        const int observationSchemaVersion = BotPolicyContract.LandingVersion;
        var featureNames = BotPolicyContract.Names(observationSchemaVersion)!;
        var inputs = featureNames.Count * BotRecordingContract.HistoryLength;
        var policy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, observationSchemaVersion, modelName,
            featureNames, historyLength = BotRecordingContract.HistoryLength, actionSizes = BotRecordingContract.ActionSizes,
            sourceCheckpointSHA256, layers = new[]
            {
                new { inputSize = inputs, outputSize = 10, activation = "linear",
                    weights = new float[inputs * 10], biases = new float[] { 0, 0, 4, 4, 0, 0, 4, 0, 0, 4 } }
            }
        }, Json);
        File.WriteAllBytes(Path.Combine(directory, "runtime-policy.json"), policy);
        Write("runtime-manifest.json", new { schemaVersion = 1, policyFile = "runtime-policy.json",
            policySHA256 = Hash(policy), sourceCheckpointSHA256 });
        Write("manifest.json", new { botDomain = domain, name = modelName, trainingSessions = included });
        Write("training-result.json", new { nativeParity = true, botDomain = domain, modelDirectory = version,
            createdAt = createdAt ?? DateTimeOffset.UtcNow });
        return sourceCheckpointSHA256;

        void Write(string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, Json));
        static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static T Read<T>(string directory, string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(directory, file)), Json)!;

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, Instance)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, Instance)!;
    private static double Clock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException(reason);
    }
}
