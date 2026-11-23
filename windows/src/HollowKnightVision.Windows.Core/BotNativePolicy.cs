using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public readonly record struct BotPolicyAction(int Horizontal, int Aim, int Jump, int Attack)
{
    public InputButtons Buttons => (Horizontal == 1 ? InputButtons.Left : Horizontal == 2 ? InputButtons.Right : InputButtons.None)
        | (Aim == 1 ? InputButtons.Up : Aim == 2 ? InputButtons.Down : InputButtons.None)
        | (Jump == 1 ? InputButtons.ActionZ : InputButtons.None) | (Attack == 1 ? InputButtons.ActionX : InputButtons.None);
    public int[] Values => [Horizontal, Aim, Jump, Attack];

    public static BotPolicyAction FromButtons(InputButtons buttons) => new(
        (buttons & (InputButtons.Left | InputButtons.Right)) switch { InputButtons.Left => 1, InputButtons.Right => 2, _ => 0 },
        (buttons & (InputButtons.Up | InputButtons.Down)) switch { InputButtons.Up => 1, InputButtons.Down => 2, _ => 0 },
        (buttons & InputButtons.ActionZ) != 0 ? 1 : 0,
        (buttons & InputButtons.ActionX) != 0 ? 1 : 0);

}

/// <summary>Verifies and evaluates the shared PPO runtime export without Python or the game.</summary>
public sealed class BotNativePolicy
{
    private const int PolicySizeLimit = 16 * 1024 * 1024;
    private const int FixtureSizeLimit = 64 * 1024 * 1024;
    private const double LogitTolerance = .001;
    private const string RuntimeFilename = "runtime-policy.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Document document;
    public int Version => document.ObservationSchemaVersion;
    public string Name => document.ModelName;
    public int FeatureCount => document.FeatureNames.Length;
    public bool SupportsMaskedKnight => document.SupportsMaskedKnight == true;

    private sealed record RuntimeManifest(int SchemaVersion, string PolicyFile,
        [property: JsonPropertyName("policySHA256")] string PolicySha256,
        [property: JsonPropertyName("sourceCheckpointSHA256")] string SourceSha256);
    private sealed record Layer(int InputSize, int OutputSize, string Activation, float[] Weights, float[] Biases);
    private sealed record SecondaryPolicy(int SchemaVersion, Layer[] Layers,
        [property: JsonPropertyName("checkpointSHA256")] string CheckpointSha256);
    private sealed record Document(int SchemaVersion, int ObservationSchemaVersion, string ModelName, string[] FeatureNames,
        int HistoryLength, int[] ActionSizes, Layer[] Layers,
        [property: JsonPropertyName("sourceCheckpointSHA256")] string SourceSha256,
        bool? SupportsMaskedKnight = null, SecondaryPolicy? PitPolicy = null, SecondaryPolicy? AtlasPolicy = null,
        JsonElement? RecoveryManeuver = null);
    private sealed record Fixture(float[][] Inputs, float[][] Logits, int[][] Actions);

    private BotNativePolicy(Document document) => this.document = document;

    public static BotNativePolicy Load(string directory)
    {
        var manifest = Decode<RuntimeManifest>(ReadBounded(Path.Combine(directory, "runtime-manifest.json"), 64 * 1024));
        if (manifest.SchemaVersion != 1 || manifest.PolicyFile != RuntimeFilename)
            throw new InvalidDataException("The bot runtime manifest is incompatible.");
        var policyData = ReadBounded(Path.Combine(directory, RuntimeFilename), PolicySizeLimit);
        RequireHash(SHA256.HashData(policyData), manifest.PolicySha256, "Bot policy verification failed.");
        RequireHash(HashFile(Path.Combine(directory, "model.zip")), manifest.SourceSha256, "Bot checkpoint verification failed.");
        var document = Decode<Document>(policyData);
        if (document.SchemaVersion != 1 || document.SourceSha256 != manifest.SourceSha256
            || BotPolicyContract.Names(document.ObservationSchemaVersion) is not { } names
            || document.FeatureNames is null || !document.FeatureNames.SequenceEqual(names)
            || document.ActionSizes is null || !document.ActionSizes.SequenceEqual(BotRecordingContract.ActionSizes)
            || document.HistoryLength != BotRecordingContract.HistoryLength || string.IsNullOrWhiteSpace(document.ModelName))
            throw new InvalidDataException("The bot policy uses an incompatible observation contract.");
        ValidateLayers(document.Layers, names.Count * document.HistoryLength, 10, document.ObservationSchemaVersion == BotPolicyContract.LandingVersion ? 518 : 512);
        if (document.PitPolicy is { } pit)
        {
            if (document.RecoveryManeuver is not null)
                throw new InvalidDataException("The bot policy contains conflicting recovery configurations.");
            ValidateSecondary(pit, "pit-model.zip", 5);
        }
        if (document.AtlasPolicy is { } atlas) ValidateSecondary(atlas, "atlas-model.zip", 3);
        return new(document);

        void ValidateSecondary(SecondaryPolicy other, string file, int outputs)
        {
            if (other.SchemaVersion != 1) throw new InvalidDataException("Unknown secondary policy version.");
            ValidateLayers(other.Layers, 8, outputs, 512);
            RequireHash(HashFile(Path.Combine(directory, file)), other.CheckpointSha256, "Secondary policy verification failed.");
        }
    }

    public float[] Logits(IReadOnlyList<float> input)
    {
        var width = FeatureCount;
        var history = document.HistoryLength;
        var legacyPadding = Version == 1 && input.Count == BotRecordingContract.FeatureNames.Count * history;
        if ((!legacyPadding && input.Count != width * history) || input.Any(value => !float.IsFinite(value) || Math.Abs(value) > 4))
            throw new InvalidDataException("Invalid bot observation vector.");
        float[] current;
        if (legacyPadding)
        {
            var stepSize = BotRecordingContract.FeatureNames.Count;
            current = new float[width * history];
            for (var step = 0; step < history; step++)
                for (var i = 0; i < width; i++) current[step * width + i] = input[step * stepSize + i];
        }
        else current = input.ToArray();

        foreach (var layer in document.Layers)
        {
            var next = new float[layer.OutputSize];
            for (var row = 0; row < layer.OutputSize; row++)
            {
                var offset = row * layer.InputSize;
                var value = (double)layer.Biases[row];
                for (var i = 0; i < current.Length; i++) value += (double)current[i] * layer.Weights[offset + i];
                next[row] = layer.Activation == "tanh" ? (float)Math.Tanh(value) : (float)value;
                if (!float.IsFinite(next[row])) throw new InvalidDataException("Non-finite bot policy output.");
            }
            current = next;
        }
        return current;
    }

    public static BotPolicyAction Action(IReadOnlyList<float> logits)
    {
        if (logits.Count != 10 || logits.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Invalid bot action scores.");
        return new(ArgMax(0, 3), ArgMax(3, 3), ArgMax(6, 2), ArgMax(8, 2));

        int ArgMax(int offset, int size)
        {
            var highest = 0;
            for (var index = 1; index < size; index++)
                if (logits[offset + index] > logits[offset + highest]) highest = index;
            return highest;
        }
    }

    public int Verify(string fixturePath)
    {
        var fixture = Decode<Fixture>(ReadBounded(fixturePath, FixtureSizeLimit));
        if (fixture.Inputs is not { Length: > 0 } || fixture.Actions?.Length != fixture.Inputs.Length
            || fixture.Logits?.Length != fixture.Inputs.Length) throw new InvalidDataException("Empty or inconsistent parity fixture.");
        for (var i = 0; i < fixture.Inputs.Length; i++)
        {
            if (fixture.Inputs[i] is null || fixture.Logits[i] is null || fixture.Actions[i] is null)
                throw new InvalidDataException("The parity fixture contains a missing value.");
            var actual = Logits(fixture.Inputs[i]);
            if (actual.Length != fixture.Logits[i].Length
                || !actual.Zip(fixture.Logits[i]).All(pair => Math.Abs(pair.First - pair.Second) < LogitTolerance)
                || !Action(actual).Values.SequenceEqual(fixture.Actions[i]))
                throw new InvalidDataException($"Native bot inference disagreed with the trainer on fixture row {i + 1}.");
        }
        return fixture.Inputs.Length;
    }

    private static void ValidateLayers(Layer[]? layers, int input, int output, int maximumWidth)
    {
        if (layers is not { Length: >= 1 and <= 4 }) throw new InvalidDataException("Invalid bot network depth.");
        var expected = input;
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer is null || layer.InputSize != expected || layer.OutputSize <= 0 || layer.OutputSize > maximumWidth
                || layer.Weights?.Length != layer.InputSize * layer.OutputSize || layer.Biases?.Length != layer.OutputSize
                || layer.Weights.Any(value => !float.IsFinite(value)) || layer.Biases.Any(value => !float.IsFinite(value))
                || layer.Activation != (i == layers.Length - 1 ? "linear" : "tanh"))
                throw new InvalidDataException("The bot policy has invalid network parameters.");
            expected = layer.OutputSize;
        }
        if (expected != output) throw new InvalidDataException("The bot policy controls do not match the runtime.");
    }

    private static T Decode<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Json)
        ?? throw new InvalidDataException("Missing bot policy data.");

    private static byte[] ReadBounded(string path, int limit)
    {
        using var file = File.OpenRead(path);
        if (file.Length > limit) throw new InvalidDataException("The bot policy file is too large.");
        var data = new byte[checked((int)file.Length)];
        file.ReadExactly(data);
        return data;
    }

    private static byte[] HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }

    private static void RequireHash(byte[] actual, string? expected, string problem)
    {
        if (!string.Equals(Convert.ToHexString(actual), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(problem);
    }
}
