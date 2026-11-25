using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class BotPolicyTests
{
    internal static void TestNativePolicyChecksActionsAndCheckpointHashes()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-policy-fixture-");
        try
        {
            var root = temporary.FullName;
            var width = WriteRuntime(root);
            var checkpoint = Path.Combine(root, "model.zip");
            var policy = BotNativePolicy.Load(root);
            var input = new float[width];
            input[0] = .8f;
            input[1] = -.6f;
            var expected = new float[10];
            expected[2] = 2 * MathF.Tanh(input[0]);
            expected[5] = MathF.Tanh(input[0]);
            expected[6] = 3;
            expected[9] = MathF.Tanh(-input[1]);
            var fixture = Path.Combine(root, "fixture.json");
            File.WriteAllText(fixture, JsonSerializer.Serialize(new
            {
                inputs = new[] { input }, logits = new[] { expected }, actions = new[] { new[] { 2, 2, 0, 1 } }
            }));
            if (policy.Verify(fixture) != 1 || BotNativePolicy.Action(policy.Logits(input)).Buttons
                    != (InputButtons.Right | InputButtons.Down | InputButtons.ActionX))
                throw new InvalidOperationException("Expected right, down and attack.");
            expected[9] += .02f;
            File.WriteAllText(fixture, JsonSerializer.Serialize(new
            {
                inputs = new[] { input }, logits = new[] { expected }, actions = new[] { new[] { 2, 2, 0, 1 } }
            }));
            Reject(() => _ = policy.Verify(fixture));
            Reject(() => _ = policy.Logits(new float[3]));
            var invalid = new float[width];
            invalid[4] = float.NaN;
            Reject(() => _ = policy.Logits(invalid));
            File.AppendAllText(checkpoint, "changed");
            Reject(() => _ = BotNativePolicy.Load(root));
            WriteRuntime(root);
            // Valid JSON with changed bytes must still fail the bound actor hash.
            File.AppendAllText(Path.Combine(root, "runtime-policy.json"), Environment.NewLine);
            Reject(() => _ = BotNativePolicy.Load(root));
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestNativeActionsMapEachHeadWithoutConflictingButtons()
    {
        InputButtons[] horizontal = [InputButtons.None, InputButtons.Left, InputButtons.Right];
        InputButtons[] aim = [InputButtons.None, InputButtons.Up, InputButtons.Down];
        InputButtons[] jump = [InputButtons.None, InputButtons.ActionZ];
        InputButtons[] attack = [InputButtons.None, InputButtons.ActionX];
        for (var h = 0; h < horizontal.Length; h++)
        for (var a = 0; a < aim.Length; a++)
        for (var j = 0; j < jump.Length; j++)
        for (var x = 0; x < attack.Length; x++)
        {
            var scores = new float[10];
            scores[h] = scores[3 + a] = scores[6 + j] = scores[8 + x] = 1;
            var action = BotNativePolicy.Action(scores);
            var expected = horizontal[h] | aim[a] | jump[j] | attack[x];
            if (!action.Values.SequenceEqual([h, a, j, x]) || action.Buttons != expected
                || BotPolicyAction.FromButtons(expected) != action)
                throw new InvalidOperationException("The native policy must map each head to its own controls.");
        }
        if (BotNativePolicy.Action(new float[10]) != default)
            throw new InvalidOperationException("Tied logits should choose neutral for every head.");
        Reject(() => _ = BotNativePolicy.Action(new float[9]));
        Reject(() => _ = BotNativePolicy.Action([float.PositiveInfinity, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    internal static void TestNativePolicyRejectsMalformedModelParameters()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-invalid-policy-");
        try
        {
            var root = temporary.FullName;
            WriteRuntime(root);
            var policyPath = Path.Combine(root, "runtime-policy.json");
            var manifestPath = Path.Combine(root, "runtime-manifest.json");
            var policy = File.ReadAllText(policyPath);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            Action<JsonObject>[] corruptions =
            [
                model => model["schemaVersion"] = 2,
                model => model["layers"]![0]!["inputSize"] = 0,
                model => model["layers"]![0]!["weights"] = new JsonArray(0),
                model => model["layers"]![1]!["activation"] = "tanh",
                model => model["sourceCheckpointSHA256"] = new string('0', 64)
            ];
            foreach (var corrupt in corruptions)
            {
                var model = JsonNode.Parse(policy)!.AsObject();
                corrupt(model);
                var data = JsonSerializer.SerializeToUtf8Bytes(model);
                File.WriteAllBytes(policyPath, data);
                manifest["policySHA256"] = Hash(data);
                File.WriteAllText(manifestPath, manifest.ToJsonString());
                Reject(() => _ = BotNativePolicy.Load(root));
            }
        }
        finally { temporary.Delete(recursive: true); }
    }


    internal static void TestAtlasDirectionParity()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-atlas-direction-");
        try
        {
            var root = temporary.FullName;
            WriteRuntime(root);
            float[][] inputs =
            [
                [.2f, .7f, 0, 0, 0, 0, 0, -1], [.7f, .2f, 0, 0, 0, 0, 0, 1],
                [.3f, .3f, 0, 0, 0, 0, 0, -1], [.3f, .3f, 0, 0, 0, 0, 0, 1],
                [.3f, .3f, 0, 0, 0, 0, 0, 0]
            ];
            var fixture = Path.Combine(root, "fixture.json");
            var actions = new[] { 1, 2, 2, 1, 1 };
            WriteFixture(actions);
            Reject(() => BotNativePolicy.Load(root).VerifyAtlasDirections(fixture));

            var checkpoint = Path.Combine(root, "atlas-model.zip");
            File.WriteAllText(checkpoint, "fixture Atlas direction checkpoint");
            var runtimePath = Path.Combine(root, "runtime-policy.json");
            var runtime = JsonNode.Parse(File.ReadAllText(runtimePath))!.AsObject();
            var manifestPath = Path.Combine(root, "runtime-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var policy = WriteAtlas(0, 1, 0);
            if (policy.VerifyAtlasDirections(fixture) != inputs.Length)
                throw new InvalidOperationException("Atlas direction must agree in world coordinates.");

            Reject(() => policy.AtlasDirection(new float[7]));
            var invalid = new float[8];
            invalid[2] = float.NaN;
            Reject(() => policy.AtlasDirection(invalid));
            WriteFixture([1]);
            Reject(() => policy.VerifyAtlasDirections(fixture));
            WriteFixture([2, 2, 2, 1, 1]);
            Reject(() => policy.VerifyAtlasDirections(fixture));

            var neutral = WriteAtlas(0, 0, 0);
            WriteFixture(new int[inputs.Length]);
            if (neutral.VerifyAtlasDirections(fixture) != inputs.Length)
                throw new InvalidOperationException("Tied scores should choose neutral regardless of mirroring.");
            File.AppendAllText(checkpoint, "changed");
            Reject(() => BotNativePolicy.Load(root));

            void WriteFixture(int[] worldActions) => File.WriteAllText(fixture,
                JsonSerializer.Serialize(new { inputs, worldActions }));

            BotNativePolicy WriteAtlas(params float[] biases)
            {
                runtime["atlasPolicy"] = JsonSerializer.SerializeToNode(new
                {
                    schemaVersion = 1, checkpointSHA256 = Hash(File.ReadAllBytes(checkpoint)),
                    layers = new[]
                    {
                        new { inputSize = 8, outputSize = 3, activation = "linear", weights = new float[24], biases }
                    }
                });
                var bytes = JsonSerializer.SerializeToUtf8Bytes(runtime);
                File.WriteAllBytes(runtimePath, bytes);
                manifest["policySHA256"] = Hash(bytes);
                File.WriteAllText(manifestPath, manifest.ToJsonString());
                return BotNativePolicy.Load(root);
            }
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestProjectedPolicyUsesOnlySelectedHistoryColumns()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-projected-policy-");
        try
        {
            var root = temporary.FullName;
            var width = WriteRuntime(root, inputColumns: [3, 21], schemaVersion: 2);
            var policy = BotNativePolicy.Load(root);
            var observation = new float[width];
            observation[0] = -4;
            observation[1] = 4;
            observation[3] = .8f;
            observation[21] = -.6f;
            var logits = policy.Logits(observation);
            if (Math.Abs(logits[2] - 2 * MathF.Tanh(.8f)) > .00001f
                || Math.Abs(logits[9] - MathF.Tanh(.6f)) > .00001f
                || BotNativePolicy.Action(logits).Buttons != (InputButtons.Right | InputButtons.Down | InputButtons.ActionX))
            {
                throw new InvalidOperationException("Projected inference must use the declared history columns.");
            }
            Reject(() => _ = policy.Logits(new float[2]));
            foreach (var invalid in new int[][] { [], [-1, 21], [3, width], [3, 3], [21, 3] })
            {
                WriteRuntime(root, inputColumns: invalid, schemaVersion: 2);
                Reject(() => _ = BotNativePolicy.Load(root));
            }
            WriteRuntime(root, inputColumns: [3, 21]);
            Reject(() => _ = BotNativePolicy.Load(root));
            WriteRuntime(root, schemaVersion: 2);
            Reject(() => _ = BotNativePolicy.Load(root));
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestObservationHistoryRejectsStaleFramesAndResetsAtDiscontinuities()
    {
        var features = new double[BotRecordingContract.FeatureNames.Count];
        features[0] = .5;
        features[1] = .35;
        features[3] = .1;
        features[4] = .85;
        features[6] = 1;
        features[20] = 1;
        var terrain = new BotScreenTerrain(100, 60, [new(41, 58, 22, 1)], [new(12, 1, 48, 1)],
            Doors: [new(12, 8, 12, 21)], Spikes: []);
        var sample = new BotCaptureObservation(5, 5, 5.01, 1, 2, 0, features, [], "detected")
        {
            DetectionTimestamp = 5, DetectedTerrain = terrain, Grounded = false,
            GeoCaches = [new(.6, .41, .1, .11)], LifebloodCocoons = [new(.81, .42, .2, .3)],
            JoniBlessings = [new(.63, .62, .1, .2)], HackerPathVersion = 1,
            RoadHint = new(.2, -.5, .28, .6, true), AtlasView = new(.32, .87, [2, 5], 100, 9, 1, 6, 5, new double[15]),
            Blockade = new(new("fixture", 5, 5, -1, 100, false), -.2, .1)
        };
        var previous = new BotPolicyAction(2, 2, 1, 1);
        for (var version = 1; version <= BotPolicyContract.LandingVersion; version++)
        {
            var history = new BotObservationHistory(version);
            var names = BotPolicyContract.Names(version)!;
            var width = names.Count;
            var first = history.Append(sample, previous, 5.01)!;
            if (first.Length != width * BotRecordingContract.HistoryLength
                || first.Take(3 * width).Any(value => value != 0) || first[3 * width + 8] != 1
                || first[3 * width + 9] != 0)
                throw new InvalidOperationException($"The first frame of schema {version} must establish a new history.");

            if (history.Append(sample, previous, 5.02) is not null)
                throw new InvalidOperationException("Duplicate frames must not refresh an input decision.");
            var next = sample with { CaptureTimestamp = 5.1, CapturedAt = 5.1, AvailableAt = 5.11, DetectionTimestamp = 5.09 };
            var second = history.Append(next, previous, 5.12)!;
            if (!second.AsSpan(2 * width, width).SequenceEqual(first.AsSpan(3 * width, width))
                || !second.AsSpan(3 * width + 8, 5).SequenceEqual(new float[] { 0, 1, -1, 1, 1 }))
                throw new InvalidOperationException($"Schema {version} lost causal history or the previous applied action.");

            var changedRoom = next with { RoomId = 3, AvailableAt = 5.17 };
            var afterTransition = history.Append(changedRoom, previous, 5.18)!;
            if (afterTransition[3 * width + 8] != 1 || afterTransition.Take(3 * width).Any(value => value != 0))
                throw new InvalidOperationException("A room transition must clear all prior input frames.");
            if (history.Append(changedRoom, previous, 5.44) is not null)
                throw new InvalidOperationException("Late delivery must expire the observation.");

            var maskedFeatures = features.ToArray();
            Array.Clear(maskedFeatures, 0, 5);
            var masked = changedRoom with { Features = maskedFeatures, Issues = ["missing-knight"] };
            if (history.Append(masked, previous, 5.18) is not null
                || history.Append(masked, previous, 5.18, allowMaskedKnight: true) is not { } admitted
                || admitted[3 * width + 8] != 1)
                throw new InvalidOperationException("Masked Knight features require fresh terrain and an opted-in checkpoint.");
            if (history.Append(masked with { AvailableAt = 5.20 }, previous, 5.20) is not null
                || history.Append(changedRoom with { AvailableAt = 5.22 }, previous, 5.23) is not { } restored
                || restored.Take(3 * width).Any(value => value != 0)
                || !restored.AsSpan(3 * width + 8, 5).SequenceEqual([1f, 0f, 0f, 0f, 0f]))
                throw new InvalidOperationException("An invalid observation must reset history before the next valid sample.");
        }
    }

    internal static void TestRoadInputPreservesOlderPolicyHistoryAndRoundTrips()
    {
        var features = new double[BotRecordingContract.FeatureNames.Count];
        features[4] = .9;
        features[6] = 1;
        var input = new BotCaptureObservation(13, 13, 13.01, 1, 1, 0, features, [], "detected")
        {
            DetectionTimestamp = 13,
            RoadHint = new(-1, 0, .1, .25, false),
            AtlasPathVersion = 1
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(input, BotCaptureSession.Json);
        var row = JsonSerializer.Deserialize<BotCaptureObservation>(data, BotCaptureSession.Json)!;
        var legacy = new BotObservationHistory(7).Append(row, default, 13.02)!;
        var road = new BotObservationHistory(8).Append(row, default, 13.02)!;
        var unmarked = new BotObservationHistory(8).Append(row with { AtlasPathVersion = null }, default, 13.02)!;
        var legacyWidth = BotPolicyContract.Names(7)!.Count;
        if (legacy.Length != 4 * legacyWidth || road.Length != 4 * (legacyWidth + 6)
            || !road.AsSpan(road.Length - 6).SequenceEqual([1f, -1f, 0f, .1f, .25f, 0f])
            || !road.AsSpan(road.Length - 6 - legacyWidth, legacyWidth).SequenceEqual(legacy.AsSpan(3 * legacyWidth))
            || !unmarked.AsSpan(unmarked.Length - 6).SequenceEqual(new float[6]))
            throw new InvalidOperationException("A stored route must add only its own features and require path provenance.");
    }

    internal static void TestStartupSkipIsLatchedUntilFreshGameplay()
    {
        var startup = new BotStartupNavigation(0);
        Expect(Observe(0, "main-title", "main-title.start-game"), InputButtons.None, BotStartupPhase.Menu);
        Expect(Observe(.20, "main-title", "main-title.start-game"), InputButtons.ActionZ, BotStartupPhase.Menu);
        Expect(Observe(.9, "select-profile", "select-profile.clear-save"), InputButtons.None, BotStartupPhase.Menu);
        Expect(Observe(1.12, "select-profile", "select-profile.clear-save"), InputButtons.None, BotStartupPhase.Menu);
        Expect(Observe(1.25, "select-profile", "select-profile.slot-4"), InputButtons.None, BotStartupPhase.Menu);
        Expect(Observe(1.48, "select-profile", "select-profile.slot-4"), InputButtons.ActionZ, BotStartupPhase.LeavingProfile);
        Expect(Observe(1.7, "quit-game", null), InputButtons.None, BotStartupPhase.LeavingProfile);
        Expect(Observe(2.16, "brightness", null), InputButtons.ActionX, BotStartupPhase.Opening);
        Expect(Observe(2.9, "select-profile", "select-profile.slot-1"), InputButtons.ActionZ, BotStartupPhase.Opening);
        Expect(Observe(3.6, "quit-game", null, receiverGameplay: true), InputButtons.None, BotStartupPhase.Opening);
        Expect(Observe(3.7, null, null, hud: true), InputButtons.None, BotStartupPhase.Opening);
        Expect(Observe(3.8, null, null), InputButtons.ActionX, BotStartupPhase.Opening);
        Expect(Observe(4.0, null, null, hud: true), InputButtons.None, BotStartupPhase.Opening);
        Expect(Observe(4.15, null, null, hud: true), InputButtons.None, BotStartupPhase.Opening);
        Expect(Observe(4.3, null, null, hud: true), InputButtons.None, BotStartupPhase.Gameplay);

        BotStartupDecision Observe(double frame, string? menu, string? choice, bool hud = false, bool receiverGameplay = false) =>
            startup.Observe(frame, frame + .01, hud, receiverGameplay, menu, choice);
        static void Expect(BotStartupDecision decision, InputButtons buttons, BotStartupPhase phase)
        {
            if (decision.Buttons != buttons || decision.Phase != phase)
                throw new InvalidOperationException($"Expected {phase}/{buttons} but saw {decision.Phase}/{decision.Buttons}.");
        }
    }

    internal static void TestGamePlayInputExpiresWithLastMeasuredKnight()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-visual-prediction-policy-");
        try
        {
            WriteRuntime(temporary.FullName, alwaysAttack: true);
            var native = BotNativePolicy.Load(temporary.FullName);
            var policy = new BotPolicyPlayback(native, BotCaptureDomain.Gameplay);
            var memory = new BotVisualKnightMemory();
            for (var i = 0; i < 6; i++)
            {
                var time = 1 + i / 10d;
                var row = memory.Resolve(BotVisualKnightMemoryTests.Row(time, i < 2 ? .30 + i * .01 : null, camera: .01));
                if (policy.Decide(row, InputButtons.Right, time + .01) is not { Action.Buttons: var buttons }
                    || !buttons.HasFlag(InputButtons.ActionX))
                    throw new InvalidOperationException("The trained controls should continue through a brief visual occlusion.");
            }
            var predicted = memory.Resolve(BotVisualKnightMemoryTests.Row(1.6, null, camera: .01) with
                { DetectionTimestamp = null, Issues = ["missing-knight", "stale-or-missing-detection"] });
            var update = policy.Evaluate(predicted, InputButtons.Right, 1.61);
            var decision = update?.Decision;
            if (decision is null || !decision.Value.Action.Buttons.HasFlag(InputButtons.ActionX)
                || Math.Abs(decision.Value.Deadline - 1.7) > 1e-6
                || !update!.StatusAt(1.61).EndsWith(" · estimating Knight", StringComparison.Ordinal)
                || update.StatusAt(1.71) != "Bot waiting for a fresh observation"
                || policy.Evaluate(predicted, InputButtons.Right, 1.71) is not null)
                throw new InvalidOperationException("The policy action must be leased only through its real visual sighting's deadline.");
            var forged = predicted with { KnightPrediction = predicted.KnightPrediction! with { LastSeenAt = 1.3 } };
            if (new BotPolicyPlayback(native, BotCaptureDomain.Gameplay).Decide(forged, InputButtons.Right, 1.61) is not null)
                throw new InvalidOperationException("Inconsistent prediction provenance must not renew control.");
            var expired = memory.Resolve(BotVisualKnightMemoryTests.Row(1.71, null, camera: .01));
            if (policy.Evaluate(expired, InputButtons.None, 1.72) is not
                { Decision: null, Status: "Bot waiting for Knight detection" })
                throw new InvalidOperationException("The policy must release control after a longer occlusion.");
            var returned = memory.Resolve(BotVisualKnightMemoryTests.Row(1.8, .45, camera: .01));
            if (policy.Decide(returned, InputButtons.None, 1.81) is not { Action.Buttons: var resumed }
                || !resumed.HasFlag(InputButtons.ActionX))
                throw new InvalidOperationException("A measured reacquisition should resume trained controls.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestPlaybackIgnoresObservationsFromTheOtherDomain()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-playback-domain-");
        try
        {
            WriteRuntime(temporary.FullName, alwaysAttack: true);
            var native = BotNativePolicy.Load(temporary.FullName);
            foreach (var domain in new[] { BotCaptureDomain.Hacker, BotCaptureDomain.Gameplay })
            {
                var playback = new BotPolicyPlayback(native, domain);
                var source = domain == BotCaptureDomain.Hacker ? "human-authored" : "detected";
                var foreign = domain == BotCaptureDomain.Hacker ? "detected" : "human-authored";
                var first = BotVisualKnightMemoryTests.Row(100, .3) with { TerrainSource = source };
                var active = playback.Evaluate(first, InputButtons.None, 100.02);
                if (active?.Decision is not { Action.Buttons: var held } || !held.HasFlag(InputButtons.ActionX))
                    throw new InvalidOperationException($"Expected an active {domain} policy action before the domain switch.");

                var next = BotVisualKnightMemoryTests.Row(100.1, .31) with { TerrainSource = source };
                if (playback.Evaluate(next with { TerrainSource = foreign }, held, 100.12) is not null)
                    throw new InvalidOperationException($"A foreign frame interrupted the active {domain} action.");
                if (playback.Evaluate(next, held, 100.12)?.Decision is null)
                    throw new InvalidOperationException($"A foreign frame claimed the next valid {domain} timestamp.");
            }
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestPlaybackResetsHistoryWhenTheActionLeaseExpires()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-lease-history-");
        try
        {
            var root = temporary.FullName;
            var width = WriteRuntime(root);
            var policy = EditRuntime(root, model =>
            {
                var weights = model["layers"]![0]!["weights"]!.AsArray();
                weights[0] = 0;
                weights[3 * (width / BotRecordingContract.HistoryLength) + 8] = 1;
            });
            var playback = new BotPolicyPlayback(policy, BotCaptureDomain.Gameplay);
            var initial = Decide(100);
            var continuous = Decide(100.1);
            var resumed = Decide(continuous.Deadline + .02);
            if (initial.Action.Horizontal != 2 || continuous.Action.Horizontal != 0
                || resumed.Action != initial.Action)
                throw new InvalidOperationException("A new decision after its input lease expired must start with a fresh policy history.");

            BotPlaybackDecision Decide(double capture) =>
                playback.Decide(BotVisualKnightMemoryTests.Row(capture, .3), InputButtons.None, capture + .01)
                ?? throw new InvalidOperationException("The test requires each frame to be fresh.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestGameplayPlaybackKeepsActorControlThroughSustainedOcclusion()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-masked-playback-");
        try
        {
            var root = temporary.FullName;
            foreach (var action in new[] { new BotPolicyAction(1, 0, 1, 0), default })
            {
                var playback = new BotPolicyPlayback(ConstantActor(root, action, masked: true), BotCaptureDomain.Gameplay);
                var previous = InputButtons.None;
                BotPlaybackUpdate? last = null;
                for (var tick = 0; tick < 54; tick++)
                {
                    var observed = MaskedRow(100 + tick * .1);
                    last = playback.Evaluate(observed, previous, observed.AvailableAt);
                    if (last is not { Decision: { } decision } || decision.Action != action
                        || !last.Status.EndsWith(" · Knight occluded", StringComparison.Ordinal)
                        || decision.Deadline > observed.CapturedAt + BotRecordingContract.MaximumCaptureAge)
                        throw new InvalidOperationException("Every current masked frame should preserve the learned action, including neutral.");
                    previous = decision.Action.Buttons;
                }
                var gap = last!.Decision!.Value.Deadline + .01;
                if (last.StatusAt(gap) != BotPlaybackUpdate.WaitingForObservation)
                    throw new InvalidOperationException("An expired stream must release its last decision.");
                var emptyTerrain = MaskedRow(106);
                var unsupported = emptyTerrain with { Features = emptyTerrain.Features.Select((value, index) => index == 20 ? 0 : value).ToArray() };
                if (playback.Evaluate(unsupported, InputButtons.None, unsupported.AvailableAt)?.Decision is not null)
                    throw new InvalidOperationException("A masked frame without terrain cannot authorize input.");
                var visible = BotVisualKnightMemoryTests.Row(107, .3);
                if (playback.Decide(visible, InputButtons.None, 107.01)?.Action != action)
                    throw new InvalidOperationException("A fresh sighting must restore the same actor's decisions.");
            }
        }
        finally { temporary.Delete(recursive: true); }

        static BotCaptureObservation MaskedRow(double time)
        {
            var row = BotVisualKnightMemoryTests.Row(time, null);
            var features = row.Features.ToArray();
            features[20] = 1;
            return row with
            {
                Features = features,
                AvailableAt = time + .01,
                AtlasView = new(.13, .56, [0, 0], 640, 10, 0, 1, 5)
            };
        }
    }

    internal static void TestPlaybackPreservesActorActionsAtEnemiesGeoAndFrontiers()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-actor-actions-");
        try
        {
            var action = new BotPolicyAction(1, 2, 1, 1);
            var playback = new BotPolicyPlayback(ConstantActor(temporary.FullName, action, masked: false), BotCaptureDomain.Gameplay);
            var previous = InputButtons.None;
            for (var index = 0; index < 7; index++)
            {
                var row = BotVisualKnightMemoryTests.Row(100 + index * .1, .5);
                var features = row.Features.ToArray();
                features[96] = index % 2 == 0 ? -.04 : .04;
                features[101] = index is 0 or 4 or 6 ? 0 : 1;
                row = row with
                {
                    RoomId = index < 5 ? 1 : 2,
                    Features = features,
                    GeoCaches = index is 4 or 5 ? [new(.53, .3, .03, .03)] : null,
                    AtlasView = new(1, 0, [0, 0], 640, 1, 0, 0, index > 5 ? 4 : 5)
                };
                if (playback.Decide(row, previous, row.CapturedAt + .01)?.Action != action)
                    throw new InvalidOperationException("Playback must preserve all four learned heads through combat, pickups and exploration.");
                previous = action.Buttons;
            }
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestPlaybackOnRecordedEnemySightingsWhenRequested()
    {
        var model = Environment.GetEnvironmentVariable("HKV_COMBAT_GAMEPLAY_MODEL");
        var clips = Environment.GetEnvironmentVariable("HKV_COMBAT_CLIP_DIRECTORY");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(clips))
        {
            Console.WriteLine("SKIP recorded enemy playback: set HKV_COMBAT_GAMEPLAY_MODEL and HKV_COMBAT_CLIP_DIRECTORY.");
            return;
        }

        var policy = BotNativePolicy.Load(model);
        var sightings = 0;
        var attacks = 0;
        foreach (var folder in Directory.EnumerateDirectories(clips, "demo-*"))
        {
            var playback = new BotPolicyPlayback(policy, BotCaptureDomain.Gameplay);
            BotPlaybackDecision? held = null;
            foreach (var line in File.ReadLines(Path.Combine(folder, "events.jsonl")))
            {
                using var item = JsonDocument.Parse(line);
                var data = item.RootElement;
                if (data.GetProperty("type").GetString() != "observation"
                    || data.Deserialize<BotCaptureObservation>(BotCaptureSession.Json) is not { TerrainSource: "detected" } row)
                    continue;
                var previous = held is { } active && row.AvailableAt < active.Deadline
                    ? active.Action.Buttons : InputButtons.None;
                var update = playback.Evaluate(row, previous, row.AvailableAt);
                if (update is null) continue;
                held = update.Decision;
                if (held is not { } chosen || row.Features[101] != 1) continue;
                sightings++;
                attacks += chosen.Action.Attack;
            }
        }
        if (sightings == 0) throw new InvalidOperationException("The selected clips produced no accepted enemy observations.");
        Console.WriteLine($"Recorded enemy sightings: {sightings} accepted frames, model chose attack on {attacks}.");
    }

    internal static void TestPlaybackWaitingStatusNamesMissingEvidence()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-playback-status-");
        try
        {
            WriteRuntime(temporary.FullName);
            var native = BotNativePolicy.Load(temporary.FullName);
            var gameplay = new BotPolicyPlayback(native, BotCaptureDomain.Gameplay);
            var hacker = new BotPolicyPlayback(native, BotCaptureDomain.Hacker);
            var row = BotVisualKnightMemoryTests.Row(100, .3);
            Expect(gameplay, row with { Issues = ["missing-terrain-analysis"] }, 100.02, "ground lines");
            Expect(gameplay, BotVisualKnightMemoryTests.Row(100.1, null), 100.12, "Knight detection");
            Expect(gameplay, BotVisualKnightMemoryTests.Row(100.2, null) with { Issues = ["stale-capture", "missing-knight"] }, 100.22, "a fresh capture");
            Expect(gameplay, BotVisualKnightMemoryTests.Row(100.3, .3), 101, "a fresh capture");
            Expect(gameplay, BotVisualKnightMemoryTests.Row(100.4, null) with { Issues = ["stale-or-missing-detection", "missing-knight"] }, 100.42,
                "fresh object detection");
            Expect(gameplay, null, 100.5, "a fresh frame");
            Expect(gameplay, row with { CaptureTimestamp = 101 }, 100.5, "a fresh capture");
            Expect(gameplay, BotVisualKnightMemoryTests.Row(100.6, .3), 100.62, "current game controls", previous: null);
            Expect(hacker, null, 100.5, "mod actor telemetry");
            Expect(hacker, row with { TerrainSource = "" }, 100.02, "authored terrain");
            var authored = BotVisualKnightMemoryTests.Row(100.1, .3) with { TerrainSource = "human-authored" };
            Expect(hacker, authored with { Issues = ["invalid-hacker-actors"] }, 100.12, "mod actor telemetry");
            Expect(hacker, authored with { CaptureTimestamp = 100.2, Issues = ["missing-hacker-knight"] }, 100.22, "the mod Knight position");
            Expect(hacker, authored with { CaptureTimestamp = 100.3, Issues = ["missing-hacker-enemies"] }, 100.32, "mod enemy positions");
            Expect(hacker, authored with { CaptureTimestamp = 100.4, Issues = ["room-transition"] }, 100.42, "the room transition");
            if (new BotPolicyAction().DisplayName != "neutral" || new BotPolicyAction(2, 0, 0, 1).DisplayName != "right + attack")
                throw new InvalidOperationException("A chosen neutral or combined action should be readable in playback.");
        }
        finally { temporary.Delete(recursive: true); }

        static void Expect(BotPolicyPlayback playback, BotCaptureObservation? row, double now,
            string missing, InputButtons? previous = InputButtons.None)
        {
            var update = playback.Evaluate(row, previous, now);
            if (update is not { Decision: null } || update.StatusAt(now) != $"Bot waiting for {missing}")
                throw new InvalidOperationException($"Expected the status to identify {missing}, got {update?.Status ?? "<ignored>"}.");
        }
    }

    internal static void TestLivePolicyPreservesAttackAndRevokesInvalidPerception()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-playback-");
        try
        {
            WriteRuntime(temporary.FullName, alwaysAttack: true);
            var policy = new BotPolicyPlayback(BotNativePolicy.Load(temporary.FullName), BotCaptureDomain.Hacker);
            var features = new double[BotRecordingContract.FeatureNames.Count];
            features[0] = .8;
            features[1] = .6;
            features[4] = 1;
            features[6] = 1;
            var last = InputButtons.None;
            for (var i = 0; i < 5; i++)
            {
                var capture = 1 + i * .1;
                if (!policy.WantsObservation(capture) || policy.WantsObservation(capture + .03))
                    throw new InvalidOperationException("Bot decisions must follow the observation clock.");
                var observation = new BotCaptureObservation(capture, capture, capture + .002, 1, 2, 0,
                    features, [], "human-authored") { DetectionTimestamp = capture };
                var action = policy.Decide(observation, last, capture + .01);
                if (action is not { } decision || decision.Deadline - capture > .2)
                    throw new InvalidOperationException("A valid observation should supply a bounded bot decision.");
                last = decision.Action.Buttons;
                if ((last & InputButtons.ActionX) == 0)
                    throw new InvalidOperationException("The runtime changed the model's held attack.");
            }
            var stale = new BotCaptureObservation(1.51, 1.51, 1.512, 1, 2, 0, features, [], "human-authored")
                { DetectionTimestamp = 1.51 };
            if (policy.Decide(stale, last, 1.92) is not null)
                throw new InvalidOperationException("Stale perception renewed the bot.");
            var wrongDomain = new BotCaptureObservation(1.62, 1.62, 1.622, 1, 2, 0, features, [], "detected")
                { DetectionTimestamp = 1.62 };
            if (policy.Decide(wrongDomain, last, 1.63) is not null)
                throw new InvalidOperationException("Visual observations must not drive the Hacker checkpoint.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestNewestNativeModelIsSelectedWithinEachDomain()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-model-selection-");
        try
        {
            var root = Path.Combine(temporary.FullName, "models");
            var hacker = new BotModelStore(temporary.FullName, BotCaptureDomain.Hacker);
            var gameplay = new BotModelStore(temporary.FullName, BotCaptureDomain.Gameplay);
            if (hacker.LoadPlayback() is not null || gameplay.LoadPlayback() is not null)
                throw new InvalidOperationException("An empty store should have no model.");

            var first = PublishModel(Path.Combine(root, "hacker"), "bot-first", "hacker", 1);
            Expect(hacker, first);
            if (gameplay.LoadPlayback() is not null)
                throw new InvalidOperationException("A Hacker checkpoint must never load into Game Play.");
            var legacy = PublishModel(root, "v1", null, 0, publishResult: false);
            Expect(gameplay, legacy);
            var second = PublishModel(Path.Combine(root, "hacker"), "bot-second", "hacker", 2);
            Expect(hacker, second);
            var independent = PublishModel(Path.Combine(root, "gameplay"), "bot-own", "gameplay", 3);
            Expect(gameplay, independent);

            var current = Path.Combine(root, "hacker", "active.json");
            using (var selection = JsonDocument.Parse(File.ReadAllText(current)))
            {
                if (selection.RootElement.GetProperty("previousDirectory").GetString() != "bot-first")
                    throw new InvalidOperationException("Automatic activation should remember the previous valid version.");
            }
            if (hacker.RestorePrevious()?.Directory != first)
                throw new InvalidOperationException("Restore should select the prior verified checkpoint.");
            var reopened = new BotModelStore(temporary.FullName, BotCaptureDomain.Hacker);
            Expect(reopened, first);
            if (reopened.Catalog() is not { ActiveId: "bot-first", PreviousId: "bot-second" })
                throw new InvalidOperationException("Rollback must remain visible across reopening.");

            var newest = PublishModel(Path.Combine(root, "hacker"), "bot-newest", "hacker", 4);
            PublishModel(Path.Combine(root, "hacker"), "bot-unverified", "hacker", 7, nativeParity: false);
            PublishModel(Path.Combine(root, "hacker"), "bot-partial", "hacker", 8, publishResult: false);
            PublishModel(Path.Combine(root, "hacker"), "bot-wrong-domain", "gameplay", 9);
            var corrupt = PublishModel(Path.Combine(root, "hacker"), "bot-corrupt", "hacker", 10);
            File.AppendAllText(Path.Combine(corrupt, "model.zip"), "corrupt");
            Expect(reopened, newest);
            Expect(gameplay, independent);

            Reject(() => reopened.Activate("bot-corrupt"));
            if (reopened.Catalog().ActiveId != "bot-newest")
                throw new InvalidOperationException("A rejected manual choice must preserve the active policy.");
            File.AppendAllText(Path.Combine(newest, "runtime-policy.json"), "corrupt");
            Expect(hacker, second);
            File.WriteAllText(current, """{"directory":"..\\gameplay"}""");
            Reject(() => hacker.LoadPlayback());
        }
        finally { temporary.Delete(recursive: true); }

        static void Expect(BotModelStore store, string directory)
        {
            if (store.LoadPlayback()?.Directory != directory)
                throw new InvalidOperationException($"Expected {directory} as the verified checkpoint.");
        }

    }

    internal static void TestCatalogHidesPartialAndForeignVersionsAndRejectsInvalidPaths()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-model-catalog-");
        try
        {
            var folder = Path.Combine(temporary.FullName, "models", "hacker");
            var store = new BotModelStore(temporary.FullName, BotCaptureDomain.Hacker);
            var legacy = PublishModel(folder, "v1", "hacker", 0, publishResult: false);
            PublishModel(folder, "bot-partial", "hacker", 1, publishResult: false);
            PublishModel(folder, "bot-foreign", "gameplay", 2);
            PublishModel(folder, "bot-unverified", "hacker", 3, nativeParity: false);
            var missing = PublishModel(folder, "bot-missing", "hacker", 4);
            File.Delete(Path.Combine(missing, "runtime-manifest.json"));
            if (store.LoadPlayback()?.Directory != legacy || store.RestorePrevious() is not null
                || !store.Catalog().Versions.Select(version => version.Id).SequenceEqual(["v1"]))
                throw new InvalidOperationException("Only complete, compatible catalog entries should be offered.");
            foreach (var invalid in new[] { "", "..", "../outside", "/tmp/model", "bot-partial", "bot-foreign", "bot-missing" })
                Reject(() => store.Activate(invalid));
            if (File.Exists(Path.Combine(folder, "active.json")) || store.Catalog().ActiveId != "v1")
                throw new InvalidOperationException("Rejected selections must leave the default selection intact.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestTrainingCompletionActivatesNewestDespiteRollback()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-model-trained-");
        try
        {
            var root = temporary.FullName;
            var models = Path.Combine(root, "models", "hacker");
            var store = new BotModelStore(root, BotCaptureDomain.Hacker);
            var first = PublishModel(models, "bot-first", "hacker", 1);
            _ = store.LoadPlayback();
            var id = Guid.NewGuid();
            var next = PublishModel(models, $"bot-{id:D}", "hacker", 2, runId: id);
            _ = store.LoadPlayback();
            if (store.Activate("bot-first").Directory != first || store.LoadPlayback()?.Directory != first)
                throw new InvalidOperationException("An explicit rollback should live through the known export.");
            if (BotTrainingJob.VerifyCompleted(root, BotCaptureDomain.Hacker, id).Directory != next
                || store.LoadPlayback()?.Directory != next || store.Catalog().PreviousId != "bot-first")
                throw new InvalidOperationException("Confirmed training should use the newest native export and remember the rollback.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestCheckpointCatalogDisplaysAvailableValidationScores()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-model-scores-");
        try
        {
            var models = Path.Combine(temporary.FullName, "models", "gameplay");
            var legacy = PublishModel(models, "v1", "gameplay", 0, publishResult: false);
            var trained = PublishModel(models, "bot-trained", "gameplay", 1);
            var resultPath = Path.Combine(trained, "training-result.json");
            var result = JsonNode.Parse(File.ReadAllText(resultPath))!.AsObject();
            result["candidate"] = JsonNode.Parse("""
                {"exactActionAccuracy":0.72,"heads":[{"name":"attack","balancedAccuracy":0.95},
                    {"name":"jump","accuracy":0.9,"balancedAccuracy":0.65}]}
                """);
            result["baseline"] = JsonNode.Parse("""
                {"exactActionAccuracy":0.58,"heads":[{"name":"jump","balancedAccuracy":0.42}]}
                """);
            result["candidateSequential"] = JsonNode.Parse("""{"exactActionAccuracy":0.69}""");
            result["baselineSequential"] = JsonNode.Parse("""{"exactActionAccuracy":0.53}""");
            File.WriteAllText(resultPath, result.ToJsonString());

            var store = new BotModelStore(temporary.FullName, BotCaptureDomain.Gameplay);
            var versions = store.Catalog().Versions;
            if (versions.Single(item => item.Id == Path.GetFileName(legacy)).Scores.Count != 0
                || !versions.Single(item => item.Id == "bot-trained").Scores.SequenceEqual(new BotCheckpointScore[]
                {
                    new("Action agreement", .72, .58),
                    new("Jump balanced accuracy", .65, .42),
                    new("Own-action agreement", .69, .53)
                }))
                throw new InvalidOperationException("The validation display needs the candidate and baseline scores from this export.");

            result["baseline"] = true;
            result["candidateSequential"] = JsonNode.Parse("""{"exactActionAccuracy":"invalid"}""");
            result["candidate"] = JsonNode.Parse("""
                {"exactActionAccuracy":0.87,"heads":[null,{"name":"jump","balancedAccuracy":1.5}]}
                """);
            File.WriteAllText(resultPath, result.ToJsonString());
            var retained = store.Catalog().Versions.Single(item => item.Id == "bot-trained").Scores;
            if (retained.Single().DisplayValue != "87%" || store.LoadPlayback()?.Directory != trained)
                throw new InvalidOperationException("Malformed optional validation fields must not invalidate the verified checkpoint.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static string PublishModel(string models, string name, string? domain, int offset,
        bool nativeParity = true, bool publishResult = true, Guid? runId = null)
    {
        var folder = Path.Combine(models, name);
        Directory.CreateDirectory(folder);
        WriteRuntime(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new { botDomain = domain, name }));
        if (publishResult)
        {
            File.WriteAllText(Path.Combine(folder, "training-result.json"), JsonSerializer.Serialize(new
            {
                id = runId, createdAt = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero).AddHours(offset),
                modelDirectory = name, botDomain = domain, nativeParity, eligibleForActivation = false
            }));
        }
        return folder;
    }

    internal static int WriteRuntime(string root, bool alwaysAttack = false, int[]? inputColumns = null, int schemaVersion = 1)
    {
        var fields = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!;
        var width = fields.Count * BotRecordingContract.HistoryLength;
        var checkpoint = Path.Combine(root, "model.zip");
        File.WriteAllText(checkpoint, "fixture checkpoint");
        var digest = Hash(File.ReadAllBytes(checkpoint));
        var inputs = inputColumns is null ? width : Math.Max(2, inputColumns.Length);
        var weights = new float[inputs * 2];
        weights[0] = 1;
        weights[inputs + 1] = -1;
        var final = new float[20];
        final[4] = 2;
        final[10] = 1;
        final[19] = 1;
        var biases = new float[10];
        biases[6] = 3;
        if (alwaysAttack) biases[9] = 10;
        var model = new
        {
            schemaVersion, observationSchemaVersion = 16, modelName = "Fixture", inputColumns,
            featureNames = fields, historyLength = 4, actionSizes = new[] { 3, 3, 2, 2 },
            sourceCheckpointSHA256 = digest, layers = new[]
            {
                new { inputSize = inputs, outputSize = 2, activation = "tanh", weights, biases = new float[2] },
                new { inputSize = 2, outputSize = 10, activation = "linear", weights = final, biases }
            }
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(model);
        var file = Path.Combine(root, "runtime-policy.json");
        File.WriteAllBytes(file, json);
        File.WriteAllText(Path.Combine(root, "runtime-manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, policyFile = "runtime-policy.json",
            policySHA256 = Hash(json), sourceCheckpointSHA256 = digest
        }));
        return width;
    }

    private static BotNativePolicy ConstantActor(string root, BotPolicyAction action, bool masked)
    {
        WriteRuntime(root);
        return EditRuntime(root, model =>
        {
            var scores = new float[10];
            scores[action.Horizontal] = scores[3 + action.Aim] = scores[6 + action.Jump] = scores[8 + action.Attack] = 1;
            var final = model["layers"]![1]!.AsObject();
            final["weights"] = JsonSerializer.SerializeToNode(new float[20]);
            final["biases"] = JsonSerializer.SerializeToNode(scores);
            model["supportsMaskedKnight"] = masked;
            // The exported direction head deliberately disagrees with the actor.
            var atlasCheckpoint = Path.Combine(root, "atlas-model.zip");
            File.WriteAllText(atlasCheckpoint, "fixture Atlas checkpoint");
            model["atlasPolicy"] = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1, checkpointSHA256 = Hash(File.ReadAllBytes(atlasCheckpoint)),
                layers = new[] { new { inputSize = 8, outputSize = 3, activation = "linear",
                    weights = new float[24], biases = new[] { 0f, 0f, 1f } } }
            });
        });
    }

    private static BotNativePolicy EditRuntime(string root, Action<JsonObject> edit)
    {
        var path = Path.Combine(root, "runtime-policy.json");
        var model = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(model);
        var data = JsonSerializer.SerializeToUtf8Bytes(model);
        var manifestPath = Path.Combine(root, "runtime-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["policySHA256"] = Hash(data);
        File.WriteAllBytes(path, data);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        return BotNativePolicy.Load(root);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void Reject(Action attempt)
    {
        try { attempt(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The model verification should have rejected this input.");
    }
}
