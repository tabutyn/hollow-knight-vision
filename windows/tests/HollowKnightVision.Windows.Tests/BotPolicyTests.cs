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

    private static void Reject(Action attempt)
    {
        try { attempt(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The model verification should have rejected this input.");
    }
}
