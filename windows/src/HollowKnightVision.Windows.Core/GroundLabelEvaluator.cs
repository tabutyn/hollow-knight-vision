using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public readonly record struct GroundLabelParameters(
    int Threshold = 8,
    int MinimumSegment = 40,
    int LineSeparation = 40,
    int OcclusionGap = 180,
    string? SurfaceArchitecture = GroundLabelParameters.CurrentSurfaceAlgorithm)
{
    public const string CurrentSurfaceAlgorithm = "hysteresis";

    public GroundLabelParameters(GroundTheoryTuning tuning)
        : this(
            tuning.GroundThreshold,
            tuning.MinimumSegmentLength,
            tuning.LineSeparation,
            tuning.OcclusionMergeGap,
            CurrentSurfaceAlgorithm)
    {
    }

    public GroundTheoryTuning Tuning => new(
        Threshold,
        MinimumSegment,
        LineSeparation,
        OcclusionGap);
}

public sealed record GroundLabelFrameResult(
    Guid Id,
    string Source,
    string Group,
    GroundLabelSplit Split,
    bool FullyReviewed,
    bool RuntimeMasksKnown,
    bool RuntimeKnightKnown,
    GroundEdgeScore Spans,
    GroundEdgeScore Evidence,
    IReadOnlyList<GroundLabelEdge> PredictedSpans,
    IReadOnlyList<GroundLabelEdge> PredictedEvidence);

public sealed record GroundLabelCandidateResult(
    GroundLabelParameters Parameters,
    GroundEdgeScore Train,
    GroundEdgeScore Check,
    GroundEdgeScore TrainEvidence,
    GroundEdgeScore CheckEvidence,
    IReadOnlyList<GroundLabelFrameResult> Frames,
    double? PredictionMillisecondsP50 = null,
    double? PredictionMillisecondsP95 = null);

public sealed record GroundLabelCandidateSummary(
    GroundLabelParameters Parameters,
    GroundEdgeScore Train,
    GroundEdgeScore Check,
    GroundEdgeScore TrainEvidence,
    GroundEdgeScore CheckEvidence)
{
    public GroundLabelCandidateSummary(GroundLabelCandidateResult result)
        : this(
            result.Parameters,
            result.Train,
            result.Check,
            result.TrainEvidence,
            result.CheckEvidence)
    {
    }
}

public sealed record GroundLabelEvaluationReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string DatasetDigest,
    IReadOnlyDictionary<string, string> DetectorBuild,
    int FrameCount,
    int ReviewedTrainFrames,
    int ReviewedCheckFrames,
    int TolerancePixels,
    int CandidatesTested,
    IReadOnlyList<GroundLabelCandidateSummary> CandidateSummaries,
    GroundLabelCandidateResult Baseline,
    GroundLabelCandidateResult Selected,
    bool Provisional,
    IReadOnlyList<string> Notes);

public static class GroundLabelEvaluator
{
    public const int MatchingTolerance = 4;

    public sealed record PreparedFrame(
        GroundLabelDocument Document,
        GroundComparisonAnalysis Comparison);

    public static IReadOnlyList<PreparedFrame> Prepare(GroundLabelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var documents = store.Documents();
        GroundLabelStore.ValidateSplits(documents);
        return documents.Select(document =>
        {
            var image = store.Image(document);
            var masks = PixelRects(document.RuntimeForeground, document.Width, document.Height);
            var analysis = GroundLineDetector.Analyze(image, masks);
            return new PreparedFrame(document, GroundLineDetector.Compare(analysis));
        }).ToArray();
    }

    public static IReadOnlyList<DetectedFloorLine> Predictions(
        PreparedFrame frame,
        GroundLabelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var document = frame.Document;
        var foreground = PixelRects(
            document.RuntimeForeground,
            document.Width,
            document.Height);
        PixelRect? knight = document.RuntimeKnight is { } knightRect
            ? PixelRects([knightRect], document.Width, document.Height).FirstOrDefault()
            : null;
        return GroundLineDetector.RejectKnownForegroundLines(
            GroundLineDetector.SemanticFloorLines(frame.Comparison, parameters.Tuning),
            document.Height,
            foreground,
            knight);
    }

    public static GroundLabelCandidateResult Evaluate(
        IReadOnlyList<PreparedFrame> frames,
        GroundLabelParameters parameters,
        int tolerance = MatchingTolerance)
    {
        ArgumentNullException.ThrowIfNull(frames);
        parameters = parameters with
        {
            SurfaceArchitecture = GroundLabelParameters.CurrentSurfaceAlgorithm
        };
        var durations = new List<double>(frames.Count);
        var results = frames.Select(frame =>
        {
            var started = Stopwatch.GetTimestamp();
            var lines = Predictions(frame, parameters);
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var document = frame.Document;
            return new GroundLabelFrameResult(
                document.Id,
                document.Source,
                document.Group,
                document.Split,
                document.FullyReviewed,
                document.RuntimeMasksKnown,
                document.RuntimeKnightKnown,
                GroundLabelScoring.Score(document, lines, tolerance),
                GroundLabelScoring.Score(document, lines, tolerance, evidenceOnly: true),
                lines.Select(line => new GroundLabelEdge(
                    line.XSpan.Start,
                    line.XSpan.EndInclusive,
                    line.Row)).ToArray(),
                lines.SelectMany(line => line.EvidenceSpans.Select(span =>
                    new GroundLabelEdge(span.Start, span.EndInclusive, line.Row))).ToArray());
        }).ToArray();

        var train = results.Where(result =>
            result.FullyReviewed && result.Split == GroundLabelSplit.Train).ToArray();
        var check = results.Where(result =>
            result.FullyReviewed && result.Split == GroundLabelSplit.Check).ToArray();
        durations.Sort();
        return new GroundLabelCandidateResult(
            parameters,
            GroundEdgeScore.Total(train.Select(result => result.Spans).ToArray()),
            GroundEdgeScore.Total(check.Select(result => result.Spans).ToArray()),
            GroundEdgeScore.Total(train.Select(result => result.Evidence).ToArray()),
            GroundEdgeScore.Total(check.Select(result => result.Evidence).ToArray()),
            results,
            durations.Count == 0
                ? null
                : durations[(int)((durations.Count - 1) * .5)],
            durations.Count == 0
                ? null
                : durations[(int)((durations.Count - 1) * .95)]);
    }

    public static IReadOnlyList<GroundLabelParameters> Grid()
    {
        var result = new List<GroundLabelParameters> { new() };
        foreach (var threshold in new[] { 6, 8, 10, 12, 16, 20 })
        foreach (var minimum in new[] { 24, 32, 48, 64 })
        foreach (var separation in new[] { 12, 24, 36, 48 })
        foreach (var gap in new[] { 0, 60, 120, 180 })
        {
            var parameters = new GroundLabelParameters(new GroundTheoryTuning(
                threshold,
                minimum,
                separation,
                gap));
            if (parameters != result[0]) result.Add(parameters);
        }
        return result;
    }

    public static GroundLabelEvaluationReport Run(
        GroundLabelStore store,
        bool sweep,
        GroundLabelParameters? baselineParameters = null,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var frames = Prepare(store);
        if (frames.Count == 0)
            throw new GroundLabelException("Capture or import a frame first");

        var initialParameters = baselineParameters ?? new GroundLabelParameters();
        var baseline = Evaluate(frames, initialParameters);
        var trainFrames = frames.Where(frame =>
            frame.Document.FullyReviewed
            && frame.Document.Split == GroundLabelSplit.Train).ToArray();
        var checkFrames = frames.Where(frame =>
            frame.Document.FullyReviewed
            && frame.Document.Split == GroundLabelSplit.Check).ToArray();
        if (sweep && baseline.Train.Recall is null)
        {
            throw new GroundLabelException(
                "Mark true ground and fully review at least one Train frame before tuning");
        }

        var candidates = sweep ? Grid() : [initialParameters];
        var selected = baseline;
        var summaries = new List<GroundLabelCandidateSummary>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var parameters = candidates[index];
            var candidate = parameters == baseline.Parameters
                ? baseline
                : Evaluate(frames, parameters);
            summaries.Add(new GroundLabelCandidateSummary(candidate));
            if ((candidate.Train.F1 ?? -1) > (selected.Train.F1 ?? -1) + .0000001)
                selected = candidate;
            progress?.Invoke(index + 1, candidates.Count);
        }

        var digestOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        digestOptions.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        var datasetDigest = GroundLabelStore.Digest(
            JsonSerializer.SerializeToUtf8Bytes(
                frames.Select(frame => frame.Document).ToArray(),
                digestOptions));
        var detectorBuild = LoadDetectorBuild();
        var notes = new List<string>
        {
            "All coordinates are top-left source pixels; matching tolerance is 4 px vertically (half the detector's 8-row kernel).",
            "Scores measure edge-pixel length. Partial frames show recall only and do not select parameters.",
            "Spans include inferred occlusion bridges; evidence scores include only directly detected ranges.",
            "Human ignore regions affect scoring only; detector input uses captured runtime masks.",
            "Selection maximizes Train span F1. Ties keep the first candidate (defaults first). Check labels never choose parameters.",
            "One generalized parameter set is proposed. No live settings are changed. Replay and additional independent areas are still required."
        };
        var provisional = trainFrames.Length == 0
            || selected.Train.Recall is null
            || checkFrames.Length == 0
            || selected.Check.Recall is null;
        if (provisional)
            notes.Add("Training-only or no positive Check labels: generalization is unmeasured.");
        if (frames.Any(frame =>
                !frame.Document.RuntimeMasksKnown
                || !frame.Document.RuntimeKnightKnown))
        {
            notes.Add("Some imported frames lack runtime masks or separate Knight metadata; their output does not reproduce the complete live masking stage.");
        }

        return new GroundLabelEvaluationReport(
            3,
            DateTimeOffset.UtcNow,
            datasetDigest,
            detectorBuild,
            frames.Count,
            trainFrames.Length,
            checkFrames.Length,
            MatchingTolerance,
            candidates.Count,
            summaries,
            baseline,
            selected,
            provisional,
            notes);
    }

    private static IReadOnlyList<PixelRect> PixelRects(
        IEnumerable<GroundLabelRect> rectangles,
        int width,
        int height) => SceneRegions.PixelRectangles(
            width,
            height,
            rectangles.Select(rectangle => new StencilRect(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height)));

    private static IReadOnlyDictionary<string, string> LoadDetectorBuild()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtime-build-info.json");
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllBytes(path))
                    ?? new Dictionary<string, string>
                    {
                        ["buildIdentity"] = "unpackaged executable"
                    };
            }
        }
        catch (Exception exception) when (exception is IOException
            or JsonException or NotSupportedException)
        {
        }
        return new Dictionary<string, string>
        {
            ["buildIdentity"] = "unpackaged executable"
        };
    }
}
