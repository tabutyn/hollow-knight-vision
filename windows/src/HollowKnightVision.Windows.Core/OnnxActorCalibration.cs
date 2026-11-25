using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HollowKnightVision.Windows.Core;

/// <summary>Optional run-bound actor heatmaps for the same captured image as the primary detector.</summary>
internal sealed class OnnxActorCalibration : IDisposable
{
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

    internal static OnnxActorCalibration? Load(string runDirectory, Guid run, IReadOnlySet<string> trained)
    {
        if (Path.GetDirectoryName(runDirectory) is not { } family) return null;
        var knight = LoadLocalizer("knight", LiveActorConfirmation.Knight);
        var deposit = LoadLocalizer("geo-deposit", LiveActorConfirmation.Deposit);
        var crawlid = LoadLocalizer("crawlid", LiveActorConfirmation.Crawlid);
        return knight is null && deposit is null && crawlid is null ? null : new(knight, deposit, crawlid);

        Localizer? LoadLocalizer(string stem, string identifier)
        {
            if (!trained.Contains(identifier)) return null;
            try
            {
                var receiptPath = ModelStoreJson.Child(family, "calibrations", $"{stem}-{run:D}.json");
                if (!File.Exists(receiptPath)) return null;
                var receipt = ModelStoreJson.Read<ActorCalibrationReceipt>(receiptPath);
                if (!receipt.Matches(identifier, run) || receipt.Localizer is not { } recipe) return null;
                var modelPath = ModelStoreJson.Child(family, "calibrations", recipe.ModelFilename);
                var model = File.ReadAllBytes(modelPath);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(model)), recipe.Sha256, StringComparison.OrdinalIgnoreCase))
                    return null;
                using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
                var session = new InferenceSession(model, options);
                if (session.InputNames.Count == 1 && session.InputNames.Contains("image") && session.OutputNames.Contains("heatmap"))
                    return new Localizer(identifier, receipt, recipe, session);
                session.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or OnnxRuntimeException)
            {
                // An optional bad calibration should leave the primary detector usable.
            }
            return null;
        }
    }

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
