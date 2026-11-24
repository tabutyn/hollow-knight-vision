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
            await VerifyCheckpointControlsAsync(Path.Combine(temporary.FullName, "versions"), timeout.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(labelsEnvironment, previousLabels);
            temporary.Delete(recursive: true);
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

    private static async Task VerifyOpeningWithoutCheckpointAsync(string root, CancellationToken token)
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
                Step(null, receiverGameplay: true, visualGameplay: true, admitGameplay: false, showHud: true);
                while (states.Reader.TryRead(out var state))
                {
                    Check(!released || !state.Enabled, "A visual Gameplay HUD must suppress skip pulses while the profile Atlas is loading.");
                    released |= !state.Enabled && state.HeldButtons == InputButtons.None;
                }
                await Task.Delay(40, token);
            } while (Clock() < preparingUntil);
            Check(released && bridge.OwnsBotControl(owner), "Controls must stay neutral while the profile Atlas finishes loading.");
            while (Field("botPlayback").GetValue(window) is not null)
            {
                Step(null, receiverGameplay: true, visualGameplay: true, admitGameplay: true, showHud: true);
                await Task.Delay(40, token);
            }
            Check(!bridge.OwnsBotControl(owner), "The session must release ownership after a fresh Gameplay HUD without a checkpoint.");
            var status = (System.Windows.Controls.TextBlock)window.FindName("StatusText");
            Check(status.Text.Contains("Gameplay reached.", StringComparison.Ordinal)
                && status.Text.Contains("Train or select a Hacker", StringComparison.Ordinal)
                && status.Text.Contains("Controls returned to you.", StringComparison.Ordinal),
                "The no-checkpoint handoff must explain how to resume manual play.");
            Check(!Directory.Exists(Path.Combine(root, "bot-v1", "evaluations")), "Opening alone must not write a policy evaluation.");

            async Task ExpectAction(InputButtons button, string context, bool receiverGameplay, bool showHud = false)
            {
                while (true)
                {
                    Step(context, receiverGameplay, visualGameplay: false, admitGameplay: false, showHud);
                    while (states.Reader.TryRead(out var state))
                        if (state.Enabled && state.HeldButtons == button) return;
                    Check(bridge.OwnsBotControl(owner), $"Bot lost control before sending {button}.");
                    await Task.Delay(30, token);
                }
            }

            void Step(string? context, bool receiverGameplay, bool visualGameplay, bool admitGameplay, bool showHud)
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
                        null, [], [], [], default, false, timestamp)
                    : null;
                _ = Method("ObserveBotStartup").Invoke(window,
                    [image, Generation, Clock(), admitGameplay, visualGameplay,
                        Telemetry(session, index) with { AtlasGameplayActive = receiverGameplay }, menu, hud]);
            }
        }
        finally
        {
            Field("isClosing").SetValue(window, true);
            await ((Task)Method("PrepareShutdownAsync").Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(10));
            await peer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Console.WriteLine("PASS Untrained desktop bot skips opening despite receiver and menu ambiguity and returns controls at the Gameplay HUD");
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
        var botRoot = Path.Combine(root, "bot-v1");
        var modelHash = WriteCheckpoint(botRoot, domain, modelName);
        var window = new MainWindow(GameRuntimeMode.Modded, null, root, root, () => new ReceiverClient(session, port: port));
        try
        {
            Field("recentCaptureGeneration").SetValue(window, Generation);
            Field("workspace").SetValue(window, domain == "hacker" ? "Hacker" : "Gameplay");
            Field("recognizedMenuContext").SetValue(window, "game");
            var bridge = (WindowsGameControlForwarder)Field("gameControls").GetValue(window)!;
            var publishedControl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.BotInputSent += (_, buttons, _) =>
            {
                if (buttons == Action) publishedControl.TrySetResult();
            };
            bridge.Start();
            while (bridge.PauseState != GamePauseState.Ready) await Task.Delay(10, token);
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
            Check((bool)Method("HandleLocalShortcut").Invoke(window, [Key.B, false, ModifierKeys.None])!,
                "B did not handle stopping the active desktop bot.");
            await ExpectWireState(InputButtons.None);
            await ((Task)Field("botEvaluationWrites").GetValue(window)!).WaitAsync(token);

            var directory = Directory.GetDirectories(Path.Combine(botRoot, "evaluations", domain)).Single();
            var manifest = Read<BotCaptureManifest>(directory, "manifest.json");
            Check(manifest is { Status: "completed", StopReason: "user-stop", ExpertDemonstration: false,
                ObservationCount: 2, UsableObservationCount: 2, TerrainPairedFrameCount: 2, InputEventCount: >= 4 }
                && manifest.BotDomain == domain, "The finalized evaluation did not describe the desktop playback.");
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
                    Array.Empty<GroundHypothesisAtlasLine>(), null, null
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
        CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var reader = new StreamReader(client.GetStream());
        using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
        var hello = ReceiverWireCodec.DecodeLine<ReceiverCapabilityRequest>((await reader.ReadLineAsync(token))!);
        await writer.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCapabilitiesAcknowledgement(
            2, "capabilitiesAck", hello.SessionId, ["input-state-v1", ReceiverProtocol.PauseCapability], 2000)));
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
