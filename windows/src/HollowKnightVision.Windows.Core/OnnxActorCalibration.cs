using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HollowKnightVision.Windows.Core;

/// <summary>Optional run-bound actor heatmaps for the same captured image as the primary detector.</summary>
internal sealed class OnnxActorCalibration : IDisposable
{
    private static readonly (string Identifier, string ReceiptName)[] Actors =
    [
        (LiveActorConfirmation.Knight, "knight"),
        (LiveActorConfirmation.Deposit, "geo-deposit"),
        (LiveActorConfirmation.Crawlid, "crawlid"),
    ];

    private readonly Localizer? knight;
    private readonly Localizer? deposit;
    private readonly Localizer? crawlid;

    private OnnxActorCalibration(Localizer? knight, Localizer? deposit, Localizer? crawlid)
    {
        this.knight = knight;
        this.deposit = deposit;
        this.crawlid = crawlid;
    }

    internal float? CrawlidWeakConfidence => crawlid?.Receipt.WeakConfidence;

    internal bool HasActor(string identifier) => identifier switch
    {
        LiveActorConfirmation.Knight => knight is not null,
        LiveActorConfirmation.Deposit => deposit is not null,
        LiveActorConfirmation.Crawlid => crawlid is not null,
        _ => false,
    };

    internal static OnnxActorCalibration? Load(string runDirectory, Guid run, IReadOnlySet<string> trained,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var loaded = ReadSources(runDirectory, run, trained, overrides).Select(LoadLocalizer)
            .OfType<Localizer>().ToDictionary(model => model.Identifier, StringComparer.Ordinal);
        var knight = loaded.GetValueOrDefault(LiveActorConfirmation.Knight);
        var deposit = loaded.GetValueOrDefault(LiveActorConfirmation.Deposit);
        var crawlid = loaded.GetValueOrDefault(LiveActorConfirmation.Crawlid);
        return knight is null && deposit is null && crawlid is null ? null : new(knight, deposit, crawlid);
    }

    internal static string[] Fingerprints(string runDirectory, Guid run, IReadOnlySet<string> trained,
        IReadOnlyDictionary<string, string?>? overrides) =>
        ReadSources(runDirectory, run, trained, overrides).Select(source =>
            $"{source.Identifier}:{Convert.ToHexString(SHA256.HashData(source.ReceiptBytes))}:{source.ModelHash}").ToArray();

    private static IEnumerable<LocalizerSource> ReadSources(string runDirectory, Guid run, IReadOnlySet<string> trained,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        if (run == Guid.Empty || Path.GetDirectoryName(runDirectory) is not { } family) yield break;
        foreach (var (identifier, receiptName) in Actors)
        {
            if (!trained.Contains(identifier)) continue;
            if (TryReadSource(family, run, identifier, receiptName, overrides) is { } source)
                yield return source;
        }
    }

    private static LocalizerSource? TryReadSource(string family, Guid run, string identifier, string receiptName,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        try
        {
            string receiptPath;
            if (overrides?.TryGetValue(identifier, out var selected) == true)
            {
                if (selected is null) return null;
                var absolutePath = Path.GetFullPath(selected);
                receiptPath = ModelStoreJson.Child(Path.GetDirectoryName(absolutePath)!, Path.GetFileName(absolutePath));
            }
            else receiptPath = ModelStoreJson.Child(family, "calibrations", $"{receiptName}-{run:D}.json");
            if (!File.Exists(receiptPath)) return null;
            var receiptBytes = File.ReadAllBytes(receiptPath);
            var receipt = JsonSerializer.Deserialize<ActorCalibrationReceipt>(receiptBytes, ModelStoreJson.Options);
            if (receipt is null || !receipt.Matches(identifier, run) || receipt.Localizer is not { } recipe) return null;
            var model = File.ReadAllBytes(ModelStoreJson.Child(Path.GetDirectoryName(receiptPath)!, recipe.ModelFilename));
            var modelHash = Convert.ToHexString(SHA256.HashData(model));
            return string.Equals(modelHash, recipe.Sha256, StringComparison.OrdinalIgnoreCase)
                ? new(identifier, receipt, recipe, receiptBytes, model, modelHash) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException)
        {
            // An optional bad calibration should leave the primary detector usable.
            return null;
        }
    }

    private static Localizer? LoadLocalizer(LocalizerSource source)
    {
        try
        {
            using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
            var session = new InferenceSession(source.Model, options);
            if (session.InputNames.Count == 1 && session.InputNames.Contains("image") && session.OutputNames.Contains("heatmap"))
                return new(source.Identifier, source.Receipt, source.Recipe, session);
            session.Dispose();
        }
        catch (OnnxRuntimeException) { }
        return null;
    }

    private sealed record LocalizerSource(string Identifier, ActorCalibrationReceipt Receipt, ActorHeatmapRecipe Recipe,
        byte[] ReceiptBytes, byte[] Model, string ModelHash);

    internal IReadOnlyList<ObjectDetection> Apply(IReadOnlyList<ObjectDetection> baseline,
        IReadOnlyList<ObjectDetection> weakCrawlids, IReadOnlyDictionary<string, OrtValue> inputs, RunOptions options)
    {
        if (knight is { } knightModel && LiveActorConfirmation.NeedsKnightReference(baseline, knightModel.Receipt))
            baseline = LiveActorConfirmation.ConfirmKnight(baseline,
                knightModel.Detect(inputs, options, knightModel.Receipt.ReferenceConfidence), knightModel.Receipt);
        if (deposit is { } depositModel && LiveActorConfirmation.NeedsDepositReference(baseline)
            && depositModel.Detect(inputs, options, depositModel.Recipe.StandaloneConfidence)
                .FirstOrDefault(LiveActorConfirmation.VisibleDeposit) is { } peak)
            baseline = [.. baseline, peak];
        if (crawlid is { } crawlidModel)
            baseline = LiveActorConfirmation.ConfirmCrawlids(baseline, weakCrawlids,
                crawlidModel.Detect(inputs, options, crawlidModel.Receipt.ReferenceConfidence), crawlidModel.Receipt);
        return baseline;
    }

    public void Dispose()
    {
        knight?.Session.Dispose();
        deposit?.Session.Dispose();
        crawlid?.Session.Dispose();
    }

    private sealed record Localizer(string Identifier, ActorCalibrationReceipt Receipt, ActorHeatmapRecipe Recipe,
        InferenceSession Session)
    {
        internal IReadOnlyList<ObjectDetection> Detect(IReadOnlyDictionary<string, OrtValue> inputs, RunOptions options,
            float threshold)
        {
            try
            {
                using var outputs = Session.Run(options, inputs, ["heatmap"]);
                var heatmap = outputs[0];
                var shape = heatmap.GetTensorTypeAndShape();
                if (shape.ElementDataType != TensorElementType.Float || shape.Shape is not [.., ActorHeatmapRecipe.Rows, ActorHeatmapRecipe.Columns]
                    || shape.Shape.SkipLast(2).Any(dimension => dimension != 1)) return [];
                return Recipe.Peaks(heatmap.GetTensorDataAsSpan<float>(), Identifier, threshold);
            }
            catch (OnnxRuntimeException) { return []; }
        }
    }
}
