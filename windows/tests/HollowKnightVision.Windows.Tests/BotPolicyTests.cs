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
