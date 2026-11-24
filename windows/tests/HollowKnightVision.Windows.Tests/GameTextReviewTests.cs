using System.Net;
using System.Text;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class GameTextReviewTests
{
    private static readonly StencilRect Bounds = new(.1, .2, .3, .1);
    private static readonly GameTextReviewOptions On = new(Enabled: true);

    internal static void TestConservativeTranscription()
    {
        Check(GameTextCleanup.ConservativeText("Map 35", "Map 55") == "Map 55", "changed purchase price");
        Check(GameTextCleanup.ConservativeText("Buy the map at Dirtmouth", "Map") == "Map", "invented instruction");
        Check(GameTextCleanup.ConservativeText("Collect SOUL", "Collect SOLIL") == "Collect SOUL", "nearby OCR edit");
        Check(GameTextCleanup.ConservativeText("é", "e\u0301") == "é", "Unicode reading");
        Check(GameTextCleanup.ConservativeText("zxyuvzxyuvzxyuv", "abcdefghijklmno") == "abcdefghijklmno",
            "unrelated same-length rewrite");
        Check(GameTextCleanup.ConservativeText("   ", "Dirtmouth") == "Dirtmouth", "empty proposal");
        const string page = "This old tablet tells travelers to rest first. PRESS A to heal using your stored SOUL.";
        Check(GameTextCleanup.HasControlInstruction(page, true, "en-GB"), "mixed Lore and control instruction");
        Check(!GameTextCleanup.HasControlInstruction(page, false, "en") && !GameTextCleanup.HasControlInstruction(page, true, "fr"),
            "instruction heuristic requires an inspected English passage");
        Check(!GameTextCleanup.HasControlInstruction("PRESS A to start", true, "en"), "short cue is not a tutorial");
    }

    internal static void TestResidentDiscovery()
    {
        Run(async () =>
        {
            using var unavailable = new Fixture((request, _) => Task.FromResult(
                request.Port == 11435
                    ? Raw("{\"models\":[{\"name\":\"gemma3:4b\",\"context_length\":1024}]}")
                    : Raw("unavailable", HttpStatusCode.ServiceUnavailable)));
            await RejectAsync<HttpRequestException>(() =>
                unavailable.Client.FindResidentAsync(LocalThinkingModel.Llama, false, default));
            var fallback = await unavailable.Client.FindResidentAsync(LocalThinkingModel.Llama, true, default);
            Check(fallback is { OnCompatibilityService: true, ContextLength: 1024, Model.Identifier: "gemma3:4b" },
                "a valid fallback resident remains available on the alternate service");
            Check(unavailable.Calls.All(call => call.Method == HttpMethod.Get && call.Path == "/api/ps"),
                "inventory must use read-only local requests");
            Check(unavailable.Calls.Select(call => call.Port).SequenceEqual([11434, 11435, 11434]),
                "the alternate service is opt-in and checked first for Llama");

            using var preferred = new Fixture((_, _) => Task.FromResult(
                Raw("{\"models\":[null,{\"name\":\"unknown\"},{\"name\":\"llama3.2-vision:11b\"}]}")));
            var model = await preferred.Client.FindResidentAsync(LocalThinkingModel.Llama, true, default);
            Check(model is { OnCompatibilityService: true, Model.Identifier: "llama3.2-vision:11b" }
                && preferred.Calls.Count == 1, "preferred model selected without consulting the other service");
        });
    }

    internal static void TestSavedReviewAndQuestNotes()
    {
        Run(async () =>
        {
            var root = TemporaryRoot();
            try
            {
                var reader = new GameTextReader(root);
                var save = new SaveProfileAtlas(1, Guid.NewGuid());
                reader.SelectProfile(save);
                reader.SetGoal("  Find the West Gate  ");
                Publish(reader, "Buy a map for 55 Geo", "Game capture", 1);
                Publish(reader, "The West Gate is past the lift.", "Game capture", 2);
                var loaded = false;
                using var server = new Fixture((request, _) =>
                {
                    if (request.Method == HttpMethod.Get)
                        return Task.FromResult(Raw(loaded ? "{\"models\":[{\"name\":\"gemma3:4b\",\"context_length\":4096}]}" : "{\"models\":[]}"));
                    using var payload = JsonDocument.Parse(request.Body!);
                    var body = payload.RootElement;
                    var user = CheckChat(body, "gemma3:4b", 4096);
                    if (body.GetProperty("options").GetProperty("num_predict").GetInt32() == 768)
                    {
                        using var evidence = JsonDocument.Parse(user);
                        Check(evidence.RootElement[0].GetProperty("ocr").GetString() == "Buy a map for 55 Geo",
                            "raw reading remains in its own user message");
                        return Task.FromResult(Chat("""
                        {"readings":[
                          {"id":0,"text":"Buy a map for 35 Geo","label":"shop","detail":"Map offer"},
                          {"id":1,"text":"The West Gate is past the lift.","label":"sign","detail":"West Gate directions"}
                        ]}
                        """));
                    }
                    Check(body.GetProperty("keep_alive").GetString() == "30m", "quest model keep-alive");
                    Check(body.GetProperty("format").GetProperty("properties").GetProperty("destinations")
                        .GetProperty("items").GetProperty("properties").GetProperty("sources").GetProperty("items")
                        .GetProperty("enum").EnumerateArray().Select(id => id.GetInt32()).SequenceEqual([1, 2]),
                        "quest schema enumerates permitted source rows");
                    Check(user.Contains("New notebook excerpts", StringComparison.Ordinal)
                        && user.Contains("[R2 · Game capture]", StringComparison.Ordinal), "numbered quest source");
                    return Task.FromResult(Chat("""
                        {"quests":[],"destinations":[{"text":"The West Gate","sources":[2]}],"unknowns":[]}
                        """));
                });
                var review = new GameTextReviewSession(reader, server.Client);
                var statuses = new List<string?>();
                _ = await review.ReviewAsync(new(), statuses.Add, default);
                Check(server.Calls.Count == 0, "proofreading defaults off");
                var wait = await review.ReviewAsync(On, statuses.Add, default);
                Check(wait.TotalSeconds == 12 && server.Calls.Count == 1 && statuses[^1]!.Contains("loaded"),
                    "an unloaded model must not receive a generation request");
                loaded = true;
                _ = await review.ReviewAsync(On, statuses.Add, default);
                Check(server.Calls.Count == 4 && reader.Observations[0].Reading == "Buy a map for 55 Geo"
                    && reader.Observations[0].Text == "Buy a map for 55 Geo"
                    && reader.Observations[0].DisplayLabel == "Map offer", "persistent proofreading protects the source");
                Check(statuses.Any(status => status?.Contains("Proofreading with Gemma") == true)
                    && statuses.Any(status => status?.Contains("Updating quest notes") == true) && statuses[^1] is null,
                    "model activity is visible and clears after review");
                var reopen = new GameTextReader(root);
                reopen.SelectProfile(save);
                Check(reopen.Goal == "Find the West Gate" && reopen.Observations[1].Label == "sign"
                    && reopen.QuestEvidence.Contains("\"text\":\"The West Gate\"", StringComparison.Ordinal),
                    "goal, corrections, and derived notes reopen in the same save");
                _ = await new GameTextReviewSession(reopen, server.Client).ReviewAsync(On, _ => { }, default);
                Check(server.Calls.Count == 4, "unchanged notebook is not resubmitted");
                reopen.SelectProfile(new SaveProfileAtlas(1, Guid.NewGuid()));
                Check(reopen.Goal.Length == 0 && reopen.Entries.Length == 0
                    && !reopen.QuestEvidence.Contains("West Gate", StringComparison.Ordinal), "reusing a save slot isolates the notebook");
                reopen.SelectProfile(save);
                reopen.SetGoal("");
                Check(reopen.DefaultGoal.Contains("Atlas", StringComparison.Ordinal) && reopen.Goal.Length == 0,
                    "clearing the goal restores the default objective");
            }
            finally { Remove(root); }
        });
    }

    internal static void TestInvalidTranscriptionAndLateReply()
    {
        Run(async () =>
        {
            var root = TemporaryRoot();
            try
            {
                var reader = new GameTextReader(root);
                var first = new SaveProfileAtlas(1, Guid.NewGuid());
                reader.SelectProfile(first);
                Publish(reader, "A door opens west.", "Opening cutscene", 1);
                string? status = null;
                using (var bad = new Fixture((request, _) => Task.FromResult(request.Method == HttpMethod.Get
                    ? Resident() : Chat("{\"readings\":[{\"text\":\"bad\"}]}"))))
                {
                    var delay = await new GameTextReviewSession(reader, bad.Client).ReviewAsync(On, value => status = value, default);
                    Check(delay.TotalSeconds == 20 && status?.StartsWith("Proofreading paused", StringComparison.Ordinal) == true
                        && reader.Observations.Single().Interpretation is null, "malformed response cannot annotate the notebook");
                }

                var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var slow = new Fixture(async (request, token) =>
                {
                    if (request.Method == HttpMethod.Get) return Resident();
                    waiting.TrySetResult();
                    await resume.Task.WaitAsync(token);
                    return Chat("{\"readings\":[{\"id\":0,\"text\":\"A door opens west.\",\"label\":\"sign\"}]}");
                });
                var review = new GameTextReviewSession(reader, slow.Client).ReviewAsync(On, _ => { }, default);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
                reader.SelectProfile(new SaveProfileAtlas(2, Guid.NewGuid()));
                resume.TrySetResult();
                await RejectAsync<OperationCanceledException>(() => review);
                Check(reader.Observations.Count == 0, "late correction cannot enter the new save");
                reader.SelectProfile(first);
                Check(reader.Observations.Single().Interpretation is null, "late correction cannot change the departed save");
            }
            finally { Remove(root); }
        });
    }

    internal static void TestTranscriptionAliasesAndTutorialSchema()
    {
        Run(async () =>
        {
            var previous = new GameTextNotebook().Import([Row("old", "The western ferry will reopen.", "en")]);
            var updated = new GameTextNotebook();
            var current = updated.Import(previous);
            current = updated.Interpret(new Dictionary<string, GameTextCorrection> { ["old"] = new(current[0].Text, "sign", "resident") });
            var entries = GameTextLogEntry.Group(current);
            using var test = new Fixture((request, _) =>
            {
                using var payload = JsonDocument.Parse(request.Body!);
                var evidence = CheckChat(payload.RootElement, "gemma3:4b", 4096);
                using var data = JsonDocument.Parse(evidence);
                Check(data.RootElement.GetProperty("currentLog")[0].GetProperty("id").GetInt32() == 0, "related log evidence");
                return Task.FromResult(Chat("""
                    {"readings":[{"id":0,"text":"The western ferry will reopen tomorrow.","label":"sign","updates":0}]}
                    """));
            });
            var resident = new ResidentThinkingModel(LocalThinkingModel.Gemma, 4096, false);
            var edits = await new GameTextCleanup(test.Client).CorrectAsync(
                [Row("new", "The western ferry will reopen tomorrow.", "en")], entries, resident, default);
            Check(edits["new"].SameAs == "old", "confirmed revision can merge with its previous reading");
            var sticky = updated.Interpret(new Dictionary<string, GameTextCorrection> { ["old"] = new("replaced", "unknown", "resident") });
            Check(sticky[0].Reading == current[0].Text, "a persisted correction remains intact");

            const string passage = "Listen to the ancients and take care in the dark. HOLD A to restore health.";
            using var tutorial = new Fixture((request, _) =>
            {
                using var payload = JsonDocument.Parse(request.Body!);
                var allowed = payload.RootElement.GetProperty("format").GetProperty("properties").GetProperty("readings")
                    .GetProperty("items").GetProperty("properties").GetProperty("label").GetProperty("enum");
                Check(allowed.GetArrayLength() == 1 && allowed[0].GetString() == "tutorial", "narrow tutorial prompt");
                return Task.FromResult(Chat(JsonSerializer.Serialize(
                    new { readings = new[] { new { id = 0, text = passage, label = "lore" } } })));
            });
            var teaching = await new GameTextCleanup(tutorial.Client).CorrectAsync(
                [Row("tablet", passage, "en") with { IsPassage = true }], [], resident, default);
            Check(teaching["tablet"].Label == "tutorial", "verified control takes precedence over a model mislabel");
        });
    }

    private static string CheckChat(JsonElement body, string model, int contextLength)
    {
        Check(body.GetProperty("model").GetString() == model && !body.GetProperty("stream").GetBoolean(), "specific loaded nonstreaming model");
        Check(body.GetProperty("options").GetProperty("num_ctx").GetInt32() == contextLength, "resident context size");
        var messages = body.GetProperty("messages");
        Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("role").GetString() == "system"
            && messages[1].GetProperty("role").GetString() == "user", "OCR text should not be a system instruction");
        return messages[1].GetProperty("content").GetString()!;
    }

    internal static GameTextObservation Row(string id, string text, string language = "en", string context = "King's Pass") =>
        new(id, text, context, language, DateTimeOffset.UnixEpoch);

    private static void Publish(GameTextReader reader, string value, string context, int id)
    {
        var frame = new BgraFrame(2, 2, 8, new byte[16], id, DateTimeOffset.UnixEpoch.AddSeconds(id));
        var work = reader.TryStart(frame, "en", context, id) ?? throw new InvalidOperationException("No work for OCR fixture.");
        Check(reader.Complete(work, [new(1, value, Bounds, .95)], 10, id + .01), "accepted OCR");
    }

    private static HttpResponseMessage Resident() => Raw("{\"models\":[{\"name\":\"gemma3:4b\",\"context_length\":4096}]}");
    private static HttpResponseMessage Chat(string content) => Raw(JsonSerializer.Serialize(new { message = new { role = "assistant", content } }));
    private static HttpResponseMessage Raw(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Call(HttpMethod Method, int Port, string Path, string? Body);
    private sealed class Fixture : HttpMessageHandler
    {
        private readonly Func<Call, CancellationToken, Task<HttpResponseMessage>> answer;
        private readonly HttpClient http;
        public LocalThinkingClient Client { get; }
        public List<Call> Calls { get; } = [];

        public Fixture(Func<Call, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            this.answer = answer;
            http = new(this, disposeHandler: false);
            Client = new(http);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var address = request.RequestUri!;
            Check(address.Host == "127.0.0.1" && address.Scheme == "http", "request leaves loopback");
            var call = new Call(request.Method, address.Port, address.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token));
            Calls.Add(call);
            return await answer(call, token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) http.Dispose();
            base.Dispose(disposing);
        }
    }

    internal static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "hkv-notebook-" + Guid.NewGuid().ToString("N"));
    internal static void Remove(string root) { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    internal static void Run(Func<Task> test) => test().GetAwaiter().GetResult();

    internal static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected notebook test failure of type " + typeof(T).Name);
    }

    internal static void Check(bool value, string detail)
    {
        if (!value) throw new InvalidOperationException("Notebook proofreader: " + detail);
    }
}
