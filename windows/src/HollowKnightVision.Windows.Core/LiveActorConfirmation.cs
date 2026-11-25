using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

internal sealed record ActorHeatmapRecipe(string ModelFilename, string Sha256,
    float StandaloneConfidence, double BoxWidth, double BoxHeight)
{
    internal const int Rows = 90;
    internal const int Columns = 160;

    internal bool IsValid(float minimumConfidence) =>
        ModelFilename is not null && string.Equals(Path.GetExtension(ModelFilename), ".onnx", StringComparison.OrdinalIgnoreCase)
        && Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit)
        && StandaloneConfidence >= minimumConfidence && StandaloneConfidence <= 1
        && BoxWidth is >= .005 and <= .3 && BoxHeight is >= .005 and <= .3;

    internal IReadOnlyList<ObjectDetection> Peaks(ReadOnlySpan<float> scores, string identifier, float threshold)
    {
        if (scores.Length != Rows * Columns) return [];
        var result = new List<ObjectDetection>();
        for (var y = 1; y < Rows; y++)
        for (var x = 1; x < Columns; x++)
        {
            var confidence = scores[y * Columns + x];
            if (!(confidence >= threshold && confidence <= 1) || !IsPeak(scores, x, y, confidence)) continue;
            var rect = new StencilRect((double)x / Columns - BoxWidth / 2, (double)y / Rows - BoxHeight / 2, BoxWidth, BoxHeight)
                .Intersect(ObjectDetectionGeometry.Unit);
            result.Add(new(identifier, ObjectDetectionGeometry.Box(rect), confidence));
        }
        return result.OrderByDescending(detection => detection.Confidence).Take(DetectionDecoder.DefaultMaximumPerClass).ToArray();
    }

    private static bool IsPeak(ReadOnlySpan<float> scores, int x, int y, float confidence)
    {
        for (var row = Math.Max(0, y - 2); row <= Math.Min(Rows - 1, y + 2); row++)
        for (var col = Math.Max(0, x - 2); col <= Math.Min(Columns - 1, x + 2); col++)
            if (scores[row * Columns + col] > confidence) return false;
        return true;
    }
}

internal sealed record ActorCalibrationReceipt(Guid SourceRunIdentifier, ActorHeatmapRecipe? Localizer,
    float WeakConfidence, float ReferenceConfidence, double MinimumOverlap,
    [property: JsonPropertyName("correctHUDMatches")] bool CorrectHudMatches)
{
    internal bool Matches(string identifier, Guid run) =>
        run != Guid.Empty && SourceRunIdentifier == run && identifier switch
        {
            LiveActorConfirmation.Deposit => Localizer?.IsValid(.9f) == true,
            LiveActorConfirmation.Knight => ValidAgreement && Localizer?.IsValid(ReferenceConfidence) == true,
            LiveActorConfirmation.Crawlid => ValidAgreement && MinimumOverlap is >= .2 and <= 1
                && (Localizer?.IsValid(ReferenceConfidence) ?? true),
            _ => false,
        };

    private bool ValidAgreement => WeakConfidence is >= .05f and < .5f && ReferenceConfidence is >= .5f and <= 1;
}

internal static class LiveActorConfirmation
{
    internal const string Knight = LiveObjectDetectionPostprocessor.Knight;
    internal const string Crawlid = "enemies.crawlid";
    internal const string Deposit = "world.geo-deposit";

    internal static bool NeedsKnightReference(IReadOnlyList<ObjectDetection> baseline, ActorCalibrationReceipt recipe)
    {
        var strong = StrongKnight(baseline);
        return strong is null || recipe.CorrectHudMatches && InHud(strong);
    }

    internal static IReadOnlyList<ObjectDetection> ConfirmKnight(IReadOnlyList<ObjectDetection> baseline,
        IReadOnlyList<ObjectDetection> reference, ActorCalibrationReceipt recipe)
    {
        if (!NeedsKnightReference(baseline, recipe) || recipe.Localizer is not { } heatmap) return baseline;
        var knights = baseline.Where(item => item.ClassIdentifier == Knight).ToArray();
        var strong = StrongKnight(knights);
        var candidate = reference.FirstOrDefault(item => item.ClassIdentifier == Knight && item.Confidence >= recipe.ReferenceConfidence
            && (item.Confidence >= heatmap.StandaloneConfidence
                || knights.Any(knight => knight.Confidence >= recipe.WeakConfidence && Distance(knight, item) <= 16)));
        if (candidate is null || strong is not null && (InHud(candidate) || Distance(strong, candidate) <= 35))
            return baseline;
        return [.. baseline.Where(item => item.ClassIdentifier != Knight
            || item.Confidence < .5f && Distance(item, candidate) > 16), candidate];
    }

    internal static bool NeedsDepositReference(IReadOnlyList<ObjectDetection> baseline) =>
        baseline.Count < DetectionDecoder.DefaultMaximumDetections && baseline.All(item => item.ClassIdentifier != Deposit);

    internal static bool VisibleDeposit(ObjectDetection detection)
    {
        var box = ObjectDetectionGeometry.Rect(detection.Box);
        var dx = (box.MidX - .091) / .027;
        var dy = (box.MidY - .111) / .045;
        return dx * dx + dy * dy > 1;
    }

    internal static IReadOnlyList<ObjectDetection> ConfirmCrawlids(IReadOnlyList<ObjectDetection> baseline,
        IReadOnlyList<ObjectDetection> weak, IReadOnlyList<ObjectDetection> reference, ActorCalibrationReceipt recipe)
    {
        var result = baseline.ToList();
        var existing = baseline.Where(item => item.ClassIdentifier == Crawlid).ToList();
        var duplicateOverlap = recipe.Localizer is null ? .35 : .33;
        foreach (var candidate in reference)
        {
            if (existing.Count >= DetectionDecoder.DefaultMaximumPerClass || result.Count >= DetectionDecoder.DefaultMaximumDetections)
                break;
            if (candidate.ClassIdentifier != Crawlid || candidate.Confidence < recipe.ReferenceConfidence) continue;
            var independent = recipe.Localizer is { } heatmap && candidate.Confidence >= heatmap.StandaloneConfidence;
            if (!independent && !weak.Any(item => item.ClassIdentifier == Crawlid && item.Confidence >= recipe.WeakConfidence
                && item.Confidence < .5f && item.Box.IntersectionOverUnion(candidate.Box) >= recipe.MinimumOverlap)) continue;
            if (existing.Any(item => item.Box.IntersectionOverUnion(candidate.Box) >= duplicateOverlap)) continue;
            existing.Add(candidate);
            result.Add(candidate);
        }
        return result;
    }

    private static ObjectDetection? StrongKnight(IEnumerable<ObjectDetection> detections) =>
        detections.Where(item => item.ClassIdentifier == Knight && item.Confidence >= .5f).MaxBy(item => item.Confidence);

    private static bool InHud(ObjectDetection detection)
    {
        var box = ObjectDetectionGeometry.Rect(detection.Box);
        return box.MidX * FrameGeometry.ReferenceWidth < 174 && box.MidY * FrameGeometry.ReferenceHeight < 64;
    }

    private static double Distance(ObjectDetection first, ObjectDetection second)
    {
        var a = ObjectDetectionGeometry.Rect(first.Box);
        var b = ObjectDetectionGeometry.Rect(second.Box);
        return ObjectDetectionGeometry.Length((a.MidX - b.MidX) * FrameGeometry.ReferenceWidth,
            (a.MidY - b.MidY) * FrameGeometry.ReferenceHeight);
    }
}
