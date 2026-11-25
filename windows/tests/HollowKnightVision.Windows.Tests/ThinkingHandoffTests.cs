using System.Net;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class ThinkingHandoffTests
{
    private static readonly ThinkingScope Scope = new(7, BotCaptureDomain.Hacker, "King's Pass", Guid.NewGuid());
    private static readonly ThinkingRoute Route = new("start", new(300, 85), new(40, 85), new(.7, .71, .02, .2, true));
    private static readonly ThinkingScene Scene = new(12, true, new(66, 3, 5, true),
        [new(-612, .24, .05)], [new(-802, -.25, .1)], [Route]);

    internal static void TestOfferScoresAndBindings()
    {
        var sight = Sight();
        var offer = new ThinkingPolicyOffer(sight);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(offer.Wire));
        var rows = wire.RootElement.GetProperty("candidates").EnumerateObject().ToArray();
        Check(wire.RootElement.GetProperty("version").GetInt32() == 2
            && rows.Select(row => row.Value.GetProperty("policy").GetString())
                .SequenceEqual(["traverse", "fight", "recover", "recover", "interact", "interact"]), "wire v2 policy order");
        Check(rows[1].Value.GetProperty("params").GetProperty("enemyId").GetInt32() == 0
            && !wire.RootElement.GetRawText().Contains("-612", StringComparison.Ordinal), "signed actor ID is held locally");
        Check(offer.Candidates[3] is { ActorId: -802, Action: ThinkingAction.Lifeblood }, "lifeblood binds its own actor");
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(offer.Schema));
        Check(!schema.RootElement.GetProperty("properties").GetProperty("scores").GetProperty("additionalProperties").GetBoolean(),
            "only offered handles are allowed in the reply");
        Check(Resolve(offer, """{"scores":{}}""").Pick(sight, 10) is null, "abstention");
        var plan = Resolve(offer, """{"scores":{"c1":91,"c0":91.0,"c2":35}}""");
        Check(plan.Pick(sight, 10) is { Action: ThinkingAction.Traverse }, "ties favor earlier candidates");
        Check(plan.Pick(sight with { Scene = Scene with { Routes = [Route with { Chart = "different" }] } }, 10)
            is { Action: ThinkingAction.Fight, ActorId: -612 }, "changed route cannot inherit its old score");
        Check(plan.Pick(sight with { Scene = Scene with { Enemies = [new(77, .3, .04)], Routes = [] } }, 10)
            is { Action: ThinkingAction.Focus }, "new enemies do not inherit the scored enemy handle");
        Check(plan.Pick(sight with { Scope = Scope with { Generation = 8 } }, 10) is null
            && plan.Pick(sight with { Scope = Scope with { ReceiverSession = Guid.NewGuid() } }, 10) is null
            && plan.Pick(sight, 10.46) is null, "stale capture, new generation or new receiver");
        foreach (var bad in new[]
        {
            """{"scores":{"c1":true}}""", """{"scores":{"c2":54.5}}""", """{"scores":{"c1":101}}""",
            """{"scores":{"c99":50}}""", """{"scores":{"c1":90,"c1":30}}""",
            """{"scores":{"c1":20},"action":"right"}""", """{"scores":[]}"""
        })
            Reject<InvalidDataException>(() => Resolve(offer, bad));
        var onlyPrompt = Resolve(offer, """{"scores":{"c4":80}}""");
        Check(onlyPrompt.Pick(sight with { Cues = [new("inspect", 10.1)] }, 10.1) is null,
            "replacement prompt with the same text cannot inherit a score");
    }

    internal static void TestMatchedActorsAndRecovery()
    {
        var sample = Telemetry();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(sample, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var parsed = json.RootElement.Deserialize<ReceiverGroundTruthSample>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var scene = ThinkingScene.FromMatchedReceiver(12, parsed, [Route]);
        Check(scene?.Recovery is { Soul: 66, Health: 3, MaxHealth: 5, CanFocus: true }, "native recovery wire fields");
        Check(scene!.Enemies.Select(actor => actor.Id).SequenceEqual([-7, -2])
            && Math.Abs(scene.Enemies[0].Dx - -.22) < .0001, "projected nearest actor ordering");
        Check(scene.CanStartFocus && !(scene with { Grounded = false }).CanStartFocus
            && !(scene with { Recovery = new(32, 3, 5, true) }).CanStartFocus
            && !(scene with { Recovery = new(66, 5, 5, true) }).CanStartFocus
            && !(scene with { Enemies = [new(1, .1, .1)] }).CanStartFocus, "Focus eligibility uses main Soul, health and space");
        Check(ThinkingScene.FromMatchedReceiver(12, sample with { AtlasGameplayActive = false }, null) is null
            && ThinkingScene.FromMatchedReceiver(12, sample with { Actors = null }, null) is null, "actors require live matched telemetry");
        var duplicate = sample with { Actors = sample.Actors! with { Enemies = [sample.Actors!.Enemies![0], sample.Actors.Enemies[0]] } };
        Check(ThinkingScene.FromMatchedReceiver(12, duplicate, null) is null, "duplicated signed instance IDs are ambiguous");
    }

    internal static void TestBoundManeuversAndRelease()
    {
        var sight = Sight();
        var offer = new ThinkingPolicyOffer(sight);
        var traverse = new ThinkingManeuver(offer.Candidates[0], sight, 10);
        var move = traverse.Step(sight, 10.02, InputButtons.None, false, 0);
        Check(move.Buttons == (InputButtons.Right | InputButtons.ActionZ) && move.Deadline <= 10.16, "route jump and short lease");
        Check(traverse.Step(sight, 10.12, move.Buttons, false, 0).Buttons == InputButtons.Right, "jump needs a release");
        Check(traverse.Step(sight with { Scene = Scene with { Routes = [Route with { Waypoint = new(42,85) }] } },
            10.15, move.Buttons, false, 0).Finished, "waypoint replacement revokes navigation");

        var focus = new ThinkingManeuver(offer.Candidates[2], sight, 10);
        Check(focus.Step(sight, 10.05, InputButtons.None, false, 0).Buttons == InputButtons.ActionA, "safe Focus");
        var healed = sight with { Scene = Scene with { Recovery = new(33, 4, 5, false) } };
        Check(focus.Step(healed, 10.13, InputButtons.ActionA, false, 0) is { Finished: true, Buttons: InputButtons.None },
            "healed mask releases Focus");
        var danger = new ThinkingManeuver(offer.Candidates[2], sight, 10);
        _ = danger.Step(sight, 10.05, InputButtons.None, false, 0);
        Check(danger.Step(sight with { Scene = Scene with { Enemies = [new(-612, .11, .03)] } },
            10.13, InputButtons.ActionA, false, 0).Finished, "enemy closes the Focus opening");

        var combat = new ThinkingManeuver(offer.Candidates[1], sight, 10);
        var close = sight with { Scene = Scene with { Enemies = [new(-612, -.07, .04)] } };
        Check(combat.Step(close, 10.01, InputButtons.None, false, 0).Buttons == (InputButtons.Left | InputButtons.ActionX)
            && combat.Step(close, 10.11, InputButtons.ActionX, false, 0).Buttons == InputButtons.Left,
            "combat uses the bound enemy and a fresh attack edge");
        Check(combat.Step(close with { Scene = Scene with { Enemies = [] } }, 10.21, InputButtons.None, false, 0).Finished,
            "missing actor terminates the maneuver");

        var reading = new ThinkingManeuver(offer.Candidates[4], sight, 10);
        Check(reading.Step(sight, 10.02, InputButtons.None, false, 0) is { Buttons: InputButtons.Up, BeginReading: true },
            "inspect starts one passage capture");
        Check(reading.Step(sight, 10.2, InputButtons.Up, false, 0) is { Buttons: InputButtons.None, Finished: false }
            && reading.Step(sight, 10.3, InputButtons.None, true, 0).Finished, "inspect releases before waiting for verified text");

        var pause = sight with { Gameplay = false, Menu = "pause", Selection = "pause.continue" };
        var resume = new ThinkingPolicyOffer(pause).Candidates.Single();
        Check(new ThinkingManeuver(resume, pause, 10).Step(pause, 10.05, InputButtons.None, false, 0).Buttons == InputButtons.ActionZ
            && !resume.IsEligible(pause with { Selection = "pause.quit-to-menu" }), "only observed Continue can be confirmed");
    }

    internal static void TestInspectionUsesSettledText()
    {
        var reader = new GameTextReader("unused");
        Read(1, "Inspect");
        Read(2, "Inspect");
        Check(reader.LatestReading?.Cues.Single().FirstSeen == 1, "stable prompt first sight");
        Read(3, "Dust moves");
        Read(4, "Inspect");
        Check(reader.LatestReading?.Cues.Single().FirstSeen == 4, "returning prompt gets a new identity");
        reader.BeginInspection("Room", 4.1, ["Inspect"]);
        Read(5, "Seek the hidden gate", "Follow the trail");
        Check(reader.LatestReading?.SatisfiedInspection is null, "one frame is insufficient for a new page");
        Read(6, "Seek the hidden gate", "Follow the trail");
        Check(reader.LatestReading?.SatisfiedInspection == 4.1
            && reader.Observations.Count(row => row.IsPassage == true) == 1
            && reader.Observations.Single(row => row.IsPassage == true).Text == "Seek the hidden gate Follow the trail",
            "one settled passage enters the notebook");
        Read(7, "Seek the hidden gate", "Follow the trail");
        Check(reader.Observations.Count(row => row.Text is "Seek the hidden gate" or "Follow the trail") == 0,
            "visible component lines do not duplicate the verified page");
        Read(8, "Inspect", "Inspect");
        Check(reader.LatestReading?.Cues.Count == 0, "duplicate cue is ambiguous");

        void Read(int number, params string[] lines)
        {
            var image = new BgraFrame(2, 2, 8, new byte[16], number, DateTimeOffset.UnixEpoch.AddSeconds(number));
            var work = reader.TryStart(image, "en", "Room", number)!;
            var observed = lines.Select((text, idx) => new GameTextLine(idx, text, new(0,0,1,.1), .95)).ToArray();
            Check(reader.Complete(work, observed, 20, number + .02), "fixture read");
        }
    }

    internal static void TestThinkingModelLoadAndImageRequest()
    {
        Task.Run(async () =>
        {
            var calls = new List<(int Port, string Model, int? KeepAlive)>();
            using var wire = new ModelStub(async (request, token) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/ps", StringComparison.Ordinal))
                    return uri.Port == 11434 ? """{"models":[{"name":"gemma3:4b"},{"name":"unknown"}]}"""
                        : """{"models":[{"name":"llama3.2-vision:11b"}]}""";
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var body = json.RootElement;
                var model = body.GetProperty("model").GetString()!;
                var keep = body.GetProperty("keep_alive");
                calls.Add((uri.Port, model, keep.ValueKind == JsonValueKind.Number ? keep.GetInt32() : null));
                if (uri.AbsolutePath.EndsWith("/generate", StringComparison.Ordinal))
                {
                    if (model == "llama3.2-vision:11b") Check(body.GetProperty("options").GetProperty("num_ctx").GetInt32() == 4096,
                        "model loads with the policy window");
                    return """{"done":true,"done_reason":"load"}""";
                }
                Check(body.GetProperty("messages")[1].GetProperty("images")[0].GetString() == "cG5n"
                    && body.GetProperty("options").GetProperty("num_predict").GetInt32() == 640
                    && body.GetProperty("format").GetProperty("required")[0].GetString() == "scores", "image and score schema");
                return """{"message":{"role":"assistant","content":"{\"scores\":{}}"}}""";
            });
            using var http = new HttpClient(wire);
            var client = new LocalThinkingClient(http);
            var resident = await client.LoadForThinkingAsync(LocalThinkingModel.Llama, true, default);
            var offer = new ThinkingPolicyOffer(Sight());
            var answer = await client.ChatAsync<JsonElement>(resident, [new("system", "rules"), new("user", "evidence", ["cG5n"])],
                offer.Schema, 640, keepResident: true, default);
            Check(offer.Resolve(answer).Scores.Count == 0 && calls.SequenceEqual(
                [(11434, "gemma3:4b", (int?)0), (11435, "llama3.2-vision:11b", null), (11435, "llama3.2-vision:11b", null)]),
                "only known competing model unloaded, answer from the chosen service");
            using var rejectedWire = new ModelStub((request, _) => Task.FromResult(request.Method == HttpMethod.Get
                ? """{"models":[]}""" : """{"done":true,"done_reason":"unload"}"""));
            using var rejectedHttp = new HttpClient(rejectedWire);
            try { await new LocalThinkingClient(rejectedHttp).LoadForThinkingAsync(LocalThinkingModel.Gemma, false, default); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("An unload receipt cannot be treated as ready.");
        }).GetAwaiter().GetResult();
    }

    internal static void TestLoadProgressMatchesOnlyTheSelectedModel()
    {
        var selected = new string('a', 64);
        var other = new string('b', 64);
        var log = new OllamaLoadLog(selected);
        Check(OllamaLoadLog.ModelDigest("FROM C:\\models\\sha256-" + selected + "\r\n") == selected
            && OllamaLoadLog.ModelDigest("# FROM sha256:" + selected) is null, "select the model's real FROM digest");
        Check(!log.Consume($"starting llama-server --model sha256:{other}\nload_tensors: offloaded 99/99 layers to GPU\n").Any(),
            "another local client's model cannot supply progress");
        Check(!log.Consume($"srv load_model: loading model 'sha256-{selected}").Any(), "wait for a complete line");
        var states = log.Consume("'\r\nload_tensors: loading model tensors\r\nload_tensors: offloaded 30/33 layers to GPU\r\n"
            + "load_tensors: CUDA0 model buffer size = 2048.00 MiB\r\nload_tensors: Vulkan1 model buffer size = 256.00 MiB\r\n"
            + "load_tensors: CUDA_Host model buffer size = 1024.00 MiB\r\nload_tensors: CPU_Mapped model buffer size = 512.00 MiB\r\n"
            + "load_tensors: warming up the model with an empty run\r\nclip: warmup with image size 448\r\n"
            + "srv load_model: initializing, n_slots = 1\r\n").ToArray();
        Check(states[0].Stage == "Opening model weights" && states[1].Stage == "Mapping model weights into memory"
            && states.Any(state => state.Stage == "Warming up the language model")
            && states.Any(state => state.Stage == "Warming up the vision encoder")
            && states[^1] is { Stage: "Preparing the inference context", OffloadedLayers: "30/33", GpuMiB: 2304, CpuMiB: 1536 },
            "keep the selected model's stage, cumulative device buffers and layer count");
        Check(!log.Consume($"loading model via llama-server sha256-{other}\nclip: warmup with image size 128\n").Any(),
            "stop reporting as soon as a different runner begins");
    }

    internal static void TestLoadWaitsForReceiptWhileTailingTheSelectedService()
    {
        Task.Run(async () =>
        {
            var selected = new string('f', 64);
            var root = Directory.CreateTempSubdirectory("hkv-ollama-load-");
            try
            {
                var log = Path.Combine(root.FullName, "compat.log");
                await File.WriteAllTextAsync(log, $"loading model via llama-server sha256-{selected}\n"
                    + "load_tensors: offloaded 99/99 layers to GPU\n");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var stages = new List<OllamaLoadProgress>();
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reports = new LoadReports(state =>
                {
                    stages.Add(state);
                    if (state.Stage == "Warming up the vision encoder") observed.TrySetResult();
                });
                var shows = 0;
                using var wire = new ModelStub(async (request, token) =>
                {
                    var url = request.RequestUri!;
                    if (url.AbsolutePath == "/api/ps") return """{"models":[]}""";
                    Check(url.Port == 11435, "diagnostics and load must use the selected service");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    Check(body.RootElement.GetProperty("model").GetString() == "llama3.2-vision:11b", "request targets the chosen model");
                    if (url.AbsolutePath == "/api/show")
                    {
                        Interlocked.Increment(ref shows);
                        return JsonSerializer.Serialize(new { modelfile = $"FROM C:\\blobs\\sha256-{selected}\r\n" });
                    }
                    Check(url.AbsolutePath == "/api/generate", "expected a load request");
                    await File.AppendAllTextAsync(log, $"srv load_model: loading model 'sha256-{selected}'\n"
                        + "load_tensors: offloaded 24/28 layers to GPU\nclip: warmup with image size 224\n", token);
                    await observed.Task.WaitAsync(token);
                    return """{"done":true,"done_reason":"load"}""";
                });
                using var http = new HttpClient(wire);
                var client = new LocalThinkingClient(http, compatibility => compatibility ? log : null);
                var ready = await client.LoadForThinkingAsync(LocalThinkingModel.Llama, true, timeout.Token, reports);
                Check(ready.OnCompatibilityService && shows == 1 && stages.First().Stage == "Opening model weights"
                    && stages[^1].OffloadedLayers == "24/28" && stages.All(state => state.OffloadedLayers != "99/99"),
                    "tail the current load only, then return the receipt from the selected service");
                var count = stages.Count;
                await File.AppendAllTextAsync(log, "srv load_model: initializing, n_slots = 1\n", timeout.Token);
                Check(stages.Count == count, "the monitor must have stopped before the load returned");

                using var missingWire = new ModelStub((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
                {
                    "/api/ps" => """{"models":[]}""",
                    "/api/generate" => """{"done":true,"done_reason":"load"}""",
                    _ => throw new InvalidOperationException("An unavailable log must not add requests.")
                }));
                using var missingHttp = new HttpClient(missingWire);
                var withoutLog = new LocalThinkingClient(missingHttp, _ => Path.Combine(root.FullName, "missing.log"));
                _ = await withoutLog.LoadForThinkingAsync(LocalThinkingModel.Gemma, false, timeout.Token, reports);
            }
            finally { root.Delete(recursive: true); }
        }).GetAwaiter().GetResult();
    }

    private sealed class LoadReports(Action<OllamaLoadProgress> onProgress) : IProgress<OllamaLoadProgress>
    {
        public void Report(OllamaLoadProgress value) => onProgress(value);
    }

    internal static ReceiverGroundTruthSample Telemetry() => new(
        2, "ground_truth", Scope.ReceiverSession!.Value, 1, 1, 6, "Tutorial_01", true,
        0, 0, 0, 0, 0, true, true, true, 0, 0, 0, 0, 0, 0, 9, 10, 10, 100, 60, 200, 120, 200, 120)
    {
        AtlasGameplayActive = true,
        Actors = new(1, new(1, "Knight", 100,60,12,20),
            [new(-2, "Distant", 175,82,10,20), new(-7, "Closer", 56,60,12,20)],
            LifebloodCocoons: [new(-8, "Cocoon", 45,30,18,16)], Recovery: new(66, 3, 5, true))
    };

    private static ThinkingSight Sight() => new(Scope, 10, true, null, null, [new("inspect", 9), new("listen", 9)], Scene);
    private static ThinkingPlan Resolve(ThinkingPolicyOffer offer, string reply)
    {
        using var json = JsonDocument.Parse(reply);
        return offer.Resolve(json.RootElement);
    }

    private static void Reject<T>(Action block) where T : Exception
    {
        try { block(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected rejected Thinking response.");
    }

    private static void Check(bool result, string detail)
    {
        if (!result) throw new InvalidOperationException("Thinking handoff: " + detail);
    }

    private sealed class ModelStub(Func<HttpRequestMessage, CancellationToken, Task<string>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await send(request, cancellationToken)) };
    }
}
