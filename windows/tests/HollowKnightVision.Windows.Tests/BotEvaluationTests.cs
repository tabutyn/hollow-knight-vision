using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotEvaluationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const long Generation = 7;

    internal static void TestBotEvaluationPreservesFramesControlsAndPairedAuditOutsideExpertData()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-evaluation-test-");
        try
        {
            foreach (var domain in new[] { BotCaptureDomain.Gameplay, BotCaptureDomain.Hacker })
                CheckArchive(temporary.FullName, domain);
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static void CheckArchive(string root, BotCaptureDomain domain)
    {
        const string scene = "Tutorial_01";
        var label = new GroundTruthWorldLine(Guid.NewGuid(), scene, 2, 8, 1, GroundTruthLineKind.Positive, false);
        var labelled = new GroundTruthLineDocument { Lines = [label] };
        var path = Path.Combine(root, "evaluations");
        var recorder = new BotEvaluationRecorder(path, domain, "Verified test policy", new string('a', 64), labelled, Generation, 1);
        labelled.Lines.Clear();
        var pixels = Enumerable.Range(0, 32 * 16 * 4).Select(value => (byte)(value % 4 == 3 ? 255 : value % 239)).ToArray();
        var image = new BgraFrame(32, 16, 32 * 4, pixels, 43, DateTimeOffset.UnixEpoch);
        var detected = new[] { new BotFloor(2, 21, 8, 1) };
        var audited = new BotScreenTerrain(32, 16, [new(3, 22, 8, 1)], []);
        var terrainSource = domain == BotCaptureDomain.Hacker ? "human-authored" : "detected";
        var row = new BotCaptureObservation(1.1, 1.1, 1.12, Generation, 1, 0, [], ["missing-knight"], terrainSource);

        recorder.RecordControls(InputButtons.Right | InputButtons.ActionZ, 1.13);
        recorder.RecordControls(InputButtons.Right | InputButtons.ActionZ, 1.14);
        recorder.RecordControls(InputButtons.Right, 1.18);
        recorder.RecordControls(InputButtons.Left, 1.17);
        var input = new VisualReplayGroundInput(1.1, Generation, WindowsMovementButtons: InputButtons.Right);
        recorder.RecordFrame(image, new(1.1, 1.12, Generation, 1, 0), row, scene, detected, audited, input, 43);
        recorder.RecordFrame(image, new(1.1, 1.12, Generation, 1, 0), row, scene, detected, audited, null, 43);
        recorder.RecordFrame(image, new(1.2, 1.22, Generation + 1, 1, 0), null, scene, detected, audited, null, 44);
        recorder.RecordFrame(image, new(1.2, 1.22, Generation, 1, 0), row with { CaptureTimestamp = 1.2, TerrainSource = "wrong" },
            "UnlabelledRoom", detected, audited, input, 44);
        recorder.RecordFrame(image, new(1.3, 1.32, Generation, 1, 0), null, scene, detected, audited with { Floors = [] }, null, 45);

        var stopReason = domain == BotCaptureDomain.Hacker ? "local-control" : "user-stop";
        var finish = recorder.FinishAsync(1.5, stopReason);
        Check(ReferenceEquals(finish, recorder.FinishAsync(2, "interrupted")), "Closing twice must use the same archive transaction.");
        var directory = finish.GetAwaiter().GetResult() ?? throw new InvalidOperationException("No evaluation was published.");
        var manifest = Load<BotCaptureManifest>(directory, "manifest.json");
        Check(manifest is { Status: "completed", ExpertDemonstration: false, ObservationCount: 1, UsableObservationCount: 0, ImageCount: 1,
            TerrainAuditCount: 3, TerrainPairedFrameCount: 2, InputEventCount: 4 } && manifest.StopReason == stopReason,
            "Evaluations must keep diagnostic frames even when the policy had no usable Knight observation.");
        Check(Path.GetFileName(Path.GetDirectoryName(directory)) == manifest.BotDomain
            && BotCaptureSession.Library(Path.Combine(root, "demonstrations")).Count == 0
            && BotCaptureSession.Library(Path.GetDirectoryName(directory)!).Count == 0, "Evaluations must stay out of the training library.");
        var edges = File.ReadLines(Path.Combine(directory, "events.jsonl")).Skip(1)
            .Select(line => JsonSerializer.Deserialize<BotCaptureInput>(line, Json)!).ToArray();
        Check(edges.SequenceEqual([new(1.13, "right", true), new(1.13, "z", true), new(1.18, "z", false), new(1.5, "right", false)]),
            "Recorder must preserve control edges, ignore old and duplicate sends, and release held controls on stop.");
        Check(BotCaptureSession.Review(directory).Single().Authored == (domain == BotCaptureDomain.Hacker),
            "The saved observation must be readable with the selected domain.");

        var metadata = Load<JsonElement>(directory, "evaluation.json");
        var source = metadata.GetProperty("visualFrameArchive").Deserialize<RecordedVisualFrameArchive>(Json)!;
        var archive = new VisualReplayArchiveReader(directory, source, manifest.Id);
        Check(metadata.GetProperty("kind").GetString() == "bot-evaluation"
            && metadata.GetProperty("modelSHA256").GetString() == new string('a', 64)
            && source is { Complete: true, SavedFrameCount: 3 }
            && archive.Manifest.Frames.Select(frame => frame.RenderedGameFrame).SequenceEqual([43, 44, 45])
            && archive.Manifest.GroundInputs.Single().WindowsMovementButtons == InputButtons.Right
            && archive.Manifest.StageEvents.Count == 6
            && archive.ReadImage(archive.Manifest.Frames[0]).Pixels.SequenceEqual(pixels),
            "Replay must retain original pixels, exact frame IDs, model identity, and frame-matched ground inputs.");
        var referencePath = Path.Combine(directory, "authored-ground.json");
        var context = Load<JsonElement>(directory, "audit-context.json");
        Check(new GroundTruthLineStore(referencePath).Load().Lines.SequenceEqual([label])
            && context.GetProperty("authoredGroundSHA256").GetString() ==
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(referencePath))).ToLowerInvariant(),
            "The audit must retain an immutable authored reference and its checksum.");
        var audits = File.ReadLines(Path.Combine(directory, "terrain-audit.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line, Json)).ToArray();
        Check(audits.Select(audit => audit.GetProperty("referenceAvailable").GetBoolean()).SequenceEqual([true, false, true])
            && audits[0].GetProperty("authored").GetArrayLength() == 1
            && audits[1].GetProperty("authored").GetArrayLength() == 0
            && audits[2].GetProperty("authored").GetArrayLength() == 0,
            "An unlabelled room must be distinguishable from a labelled room with no reference floor in view.");
    }

    internal static void TestEmptyAndFailedEvaluationsDoNotPublish()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-empty-evaluation-");
        try
        {
            var root = temporary.FullName;
            var emptyPath = Path.Combine(root, "empty");
            var empty = new BotEvaluationRecorder(emptyPath, BotCaptureDomain.Gameplay, "Empty", "", new(), Generation, 1);
            Check(empty.FinishAsync(2, "interrupted").GetAwaiter().GetResult() is null && !Directory.Exists(emptyPath),
                "Starting the bot without a playable frame must leave no evaluation directory.");
            var blocked = Path.Combine(root, "existing-file");
            File.WriteAllText(blocked, "preserve");
            var failed = new BotEvaluationRecorder(blocked, BotCaptureDomain.Hacker, "Failed", "", new(), Generation, 1);
            var frame = new BgraFrame(1, 1, 4, [0, 0, 0, 255], 1, DateTimeOffset.UnixEpoch);
            failed.RecordFrame(frame, new(1.1, 1.2, Generation, 1, 0), null, null, [], null, null, null);
            try
            {
                _ = failed.FinishAsync(2, "user-stop").GetAwaiter().GetResult();
                throw new InvalidOperationException("Expected an evaluation write error.");
            }
            catch (IOException) { }
            Check(File.ReadAllText(blocked) == "preserve", "A failed archive must preserve existing data.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static async Task TestEvaluationPreparationVerifiesDestinationBeforeFrames()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-prepared-evaluation-");
        try
        {
            var root = temporary.FullName;
            var prepared = new BotEvaluationRecorder(root, BotCaptureDomain.Gameplay,
                "Prepared", null, new(), Generation, 1, prepareImmediately: true);
            await prepared.Preparation;
            var domain = Path.Combine(root, "gameplay");
            Check(Directory.GetDirectories(domain, ".pending-evaluation-*").Length == 1,
                "Readiness must wait for the actual staging directory to open.");
            Check(await prepared.FinishAsync(2, "interrupted") is null && !Directory.EnumerateDirectories(domain).Any(),
                "Stopping before the first frame must clean up the prepared archive without publishing it.");

            var stopped = new BotEvaluationRecorder(root, BotCaptureDomain.Gameplay,
                "Stopped while preparing", null, new(), Generation, 1, prepareImmediately: true);
            var closing = stopped.FinishAsync(1.01, "interrupted");
            await stopped.Preparation;
            Check(await closing is null && !Directory.EnumerateDirectories(domain).Any(),
                "Stopping while the evaluation is opening must also remove its staging directory.");

            var blocked = Path.Combine(root, "blocked-evaluation");
            File.WriteAllText(blocked, "preserve");
            try
            {
                var failed = new BotEvaluationRecorder(blocked, BotCaptureDomain.Hacker,
                    "Failed", null, new(), Generation, 1, prepareImmediately: true);
                await failed.Preparation;
                throw new InvalidOperationException("Expected evaluation preparation to fail before playback.");
            }
            catch (IOException) { }
            Check(File.ReadAllText(blocked) == "preserve", "The failed preparation must preserve existing data.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestEvaluationBackpressureMarksArchiveIncomplete()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-overloaded-evaluation-");
        try
        {
            var recorder = new BotEvaluationRecorder(temporary.FullName, BotCaptureDomain.Gameplay,
                "Overloaded", "", new(), Generation, 1);
            var image = new BgraFrame(1, 1, 4, [4, 0, 0, 255], 1, DateTimeOffset.UnixEpoch);
            for (var time = 1; time <= 10_000; time++)
                recorder.RecordFrame(image, new(time, time, Generation, 1, 0), null, null, [], null, null, null);
            var directory = recorder.FinishAsync(10_001, "interrupted").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("No evaluation was published.");
            var manifest = Load<BotCaptureManifest>(directory, "manifest.json");
            var metadata = Load<JsonElement>(directory, "evaluation.json");
            var summary = metadata.GetProperty("visualFrameArchive").Deserialize<RecordedVisualFrameArchive>(Json)!;
            Check(manifest.Status == "interrupted" && manifest.TerrainAuditCount == summary.CapturedFrameCount
                && metadata.GetProperty("droppedSamples").GetInt32() > 0
                && metadata.GetProperty("droppedSamples").GetInt32() + summary.CapturedFrameCount == 10_000
                && summary is { Complete: false, Issue: "evaluationBackpressure", PeakPendingImages: <= 8 },
                "Dropped evaluation samples must be counted and must make the source archive incomplete.");
            _ = new VisualReplayArchiveReader(directory, summary, manifest.Id, requireComplete: false);
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestSkillEvaluationKeepsChoicesAndOutcome()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-skill-evaluation-");
        try
        {
            var scope = new ThinkingScope(Generation, BotCaptureDomain.Hacker, "Tutorial_01", Guid.NewGuid());
            var sight = new ThinkingSight(scope, 1.05, true, null, null, [new("inspect", 1.01)],
                new(42, true, null, [new(-78, .1, .04)], [], []));
            var offer = new ThinkingPolicyOffer(sight);
            using var json = JsonDocument.Parse("""{"scores":{"c0":94,"c1":72}}""");
            var plan = offer.Resolve(json.RootElement);
            var winner = plan.Pick(sight, 1.10)!;
            var evaluation = new BotEvaluationRecorder(temporary.FullName, BotCaptureDomain.Hacker,
                "Built-in playing policies", null, new(), Generation, 1);
            var image = new BgraFrame(32, 16, 128, new byte[32 * 16 * 4], 8, DateTimeOffset.UnixEpoch);
            evaluation.RecordSkill(plan, winner, scope.Context, "selected", 1.10);
            Check(evaluation.WantsSkillSample(1.05), "Capture the exact frame that admitted the selection.");
            evaluation.RecordFrame(image, new(1.05, 1.11, Generation, 42, 0), null, scope.Context, [], null, null, 8, skill: true);
            Check(!evaluation.WantsSkillSample(1.08) && evaluation.WantsSkillSample(1.14),
                "Intervening control frames do not require another image and terrain analysis.");
            evaluation.RecordFrame(image, new(1.08, 1.12, Generation, 42, 0), null, scope.Context, [], null, null, 9, skill: true);
            evaluation.RecordSkill(plan with { Scope = scope with { Generation = 1 } }, winner, scope.Context, "wrong generation", 1.13);
            evaluation.RecordControls(InputButtons.ActionX, 1.12);
            evaluation.RecordSkill(plan, winner, scope.Context, "Bound target expired.", 1.25);
            var saved = evaluation.FinishAsync(1.20, "skill-finished").GetAwaiter().GetResult()!;
            evaluation.RecordSkill(plan, winner, scope.Context, "after stop", 1.26);
            var metadata = Load<JsonElement>(saved, "evaluation.json");
            Check(metadata.GetProperty("actionSource").GetString() == "skill-plan"
                && metadata.GetProperty("skillEvents").GetString() == "skill-events.jsonl"
                && !metadata.TryGetProperty("modelSHA256", out _),
                "Only a real checkpoint may be named as the motor and the evaluation must link its skill sidecar.");
            var events = File.ReadLines(Path.Combine(saved, "skill-events.jsonl"))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line, Json)).ToArray();
            var selection = events[0].GetProperty("choice");
            var choices = events[0].GetProperty("plan").GetProperty("choices");
            Check(events.Length == 2 && events[0].GetProperty("event").GetString() == "selected"
                && events[1].GetProperty("event").GetString() == "Bound target expired."
                && events.All(row => row.GetProperty("context").GetString() == scope.Context)
                && events[0].GetProperty("plan").GetProperty("decisionID").GetInt64() == plan.DecisionId
                && selection.GetProperty("candidateID").GetString() == winner.Id
                && selection.GetProperty("policy").GetString() == "fight" && selection.GetProperty("points").GetInt32() == 94
                && selection.GetProperty("binding").GetProperty("target").GetProperty("fight").GetProperty("_0").GetInt32() == -78
                && choices[1].GetProperty("control").GetString() == "inspect"
                && choices[1].GetProperty("promptFirstSeen").GetDouble() == 1.01,
                "Retain the winning and alternative scores, bound actor, prompt evidence, and actual outcome.");
            var manifest = Load<BotCaptureManifest>(saved, "manifest.json");
            Check(manifest is { ExpertDemonstration: false, Status: "completed", InputEventCount: 2, TerrainAuditCount: 1 }
                && Math.Abs(manifest.Duration - .25) < 1e-10 && BotCaptureSession.Library(Path.GetDirectoryName(saved)!).Count == 0,
                "A skill evaluation must sample frames, finalize neutral controls, and stay outside training.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static T Load<T>(string directory, string name) => JsonSerializer.Deserialize<T>(
        File.ReadAllBytes(Path.Combine(directory, name)), BotCaptureSession.Json)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
