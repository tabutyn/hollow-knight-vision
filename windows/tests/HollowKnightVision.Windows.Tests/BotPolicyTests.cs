using System.Security.Cryptography;
using System.Text.Json;
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
            File.AppendAllText(checkpoint, "changed");
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
        }
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

            var legacy = Publish(root, "v1", null, 0, publishResult: false);
            Expect(gameplay, legacy);
            var first = Publish(Path.Combine(root, "hacker"), "bot-first", "hacker", 1);
            Expect(hacker, first);
            var second = Publish(Path.Combine(root, "hacker"), "bot-second", "hacker", 2);
            Expect(hacker, second);
            var independent = Publish(Path.Combine(root, "gameplay"), "bot-own", "gameplay", 3);
            Expect(gameplay, independent);

            var current = Path.Combine(root, "hacker", "active.json");
            using (var selection = JsonDocument.Parse(File.ReadAllText(current)))
            {
                if (selection.RootElement.GetProperty("previousDirectory").GetString() != "bot-first")
                    throw new InvalidOperationException("Automatic activation should remember the previous valid version.");
            }
            File.WriteAllText(current,
                """{"directory":"bot-first","previousDirectory":"bot-second","manualChoiceThrough":"bot-second"}""");
            Expect(hacker, first);

            var newest = Publish(Path.Combine(root, "hacker"), "bot-newest", "hacker", 4);
            Publish(Path.Combine(root, "hacker"), "bot-unverified", "hacker", 7, nativeParity: false);
            Publish(Path.Combine(root, "hacker"), "bot-partial", "hacker", 8, publishResult: false);
            Publish(Path.Combine(root, "hacker"), "bot-wrong-domain", "gameplay", 9);
            var corrupt = Publish(Path.Combine(root, "hacker"), "bot-corrupt", "hacker", 10);
            File.AppendAllText(Path.Combine(corrupt, "model.zip"), "corrupt");
            Expect(hacker, newest);
            Expect(gameplay, independent);

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

        static string Publish(string models, string name, string? domain, int offset,
            bool nativeParity = true, bool publishResult = true)
        {
            var folder = Path.Combine(models, name);
            Directory.CreateDirectory(folder);
            WriteRuntime(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new { botDomain = domain }));
            if (publishResult)
            {
                File.WriteAllText(Path.Combine(folder, "training-result.json"), JsonSerializer.Serialize(new
                {
                    createdAt = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero).AddHours(offset),
                    modelDirectory = name, botDomain = domain, nativeParity, eligibleForActivation = false
                }));
            }
            return folder;
        }
    }

    private static int WriteRuntime(string root)
    {
        var fields = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!;
        var width = fields.Count * BotRecordingContract.HistoryLength;
        var checkpoint = Path.Combine(root, "model.zip");
        File.WriteAllText(checkpoint, "fixture checkpoint");
        string Hash(byte[] e) => Convert.ToHexString(SHA256.HashData(e)).ToLowerInvariant();
        var digest = Hash(File.ReadAllBytes(checkpoint));
        var weights = new float[width * 2];
        weights[0] = 1;
        weights[width + 1] = -1;
        var final = new float[20];
        final[4] = 2;
        final[10] = 1;
        final[19] = 1;
        var biases = new float[10];
        biases[6] = 3;
        var model = new
        {
            schemaVersion = 1, observationSchemaVersion = 16, modelName = "Fixture",
            featureNames = fields, historyLength = 4, actionSizes = new[] { 3, 3, 2, 2 },
            sourceCheckpointSHA256 = digest, layers = new[]
            {
                new { inputSize = width, outputSize = 2, activation = "tanh", weights, biases = new float[2] },
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

    private static void Reject(Action attempt)
    {
        try { attempt(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The model verification should have rejected this input.");
    }
}
