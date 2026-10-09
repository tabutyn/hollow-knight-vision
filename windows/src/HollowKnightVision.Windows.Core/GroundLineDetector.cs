namespace HollowKnightVision.Windows.Core;

public sealed record GroundEdgeAnalysis(
    int Width,
    int Height,
    byte[] GroundEdgePixels,
    byte[]? SourceLuma = null,
    byte[]? ValidSourcePixels = null,
    Range? ValidRows = null);

public sealed record GroundComparisonAnalysis(
    int Width,
    int Height,
    byte[] GroundPixels,
    byte[]? SourceLuma = null,
    byte[]? ValidSourcePixels = null);

public readonly record struct GroundTheoryTuning(
    int GroundThreshold,
    int MinimumSegmentLength,
    int LineSeparation = 4,
    int OcclusionMergeGap = 0)
{
    public static GroundTheoryTuning Default => new(10, 48, 36, 180);
    public static GroundTheoryTuning SemanticDefault => new(8, 40, 40, 180);
}

public sealed record DetectedFloorLine
{
    public DetectedFloorLine(
        int row,
        PixelSpan xSpan,
        IReadOnlyList<PixelSpan>? evidenceSpans = null)
    {
        if (row < 0) throw new ArgumentOutOfRangeException(nameof(row));
        Row = row;
        XSpan = xSpan;
        EvidenceSpans = (evidenceSpans ?? [xSpan]).ToArray();
        if (EvidenceSpans.Count == 0
            || EvidenceSpans.Any(span => span.Start < xSpan.Start
                || span.EndInclusive > xSpan.EndInclusive))
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceSpans));
        }
    }

    public int Row { get; }
    public PixelSpan XSpan { get; }
    public IReadOnlyList<PixelSpan> EvidenceSpans { get; }
    public CleanFloorLine ToSemanticLine() => new(Row, XSpan);
}

public readonly record struct GroundLineSegment(
    double FrameY,
    int MinimumFrameX,
    int MaximumFrameX,
    double Confidence);

public sealed record GroundLineDetection(
    double FrameY,
    IReadOnlyList<GroundLineSegment> Segments,
    IReadOnlyList<double> NotchFrameXs,
    double Confidence);

/// <summary>
/// CPU ground-line extractor matching the macOS 32-column by 8-row theory.
/// Detector rows and exclusion rectangles use top-left image coordinates.
/// </summary>
public static class GroundLineDetector
{
    public const int ComparisonKernelSpan = 32;
    private static readonly int[] ComparisonWeights = [-1, -2, -3, -4, 4, 3, 2, 1];

    public static GroundEdgeAnalysis Analyze(
        BgraFrame frame,
        IReadOnlyList<PixelRect>? excludedRects = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var width = frame.Width;
        var height = frame.Height;
        var length = checked(width * height);
        var luma = new byte[length];
        for (var row = 0; row < height; row++)
        {
            var source = frame.Row(row);
            var targetOffset = row * width;
            for (var x = 0; x < width; x++)
            {
                var offset = x * BgraFrame.BytesPerPixel;
                luma[targetOffset + x] = (byte)((19 * source[offset]
                    + 183 * source[offset + 1]
                    + 54 * source[offset + 2]
                    + 128) >> 8);
            }
        }

        var edges = new byte[length];
        for (var index = 0; index < width * (height - 1); index++)
        {
            edges[index] = (byte)Math.Abs(luma[index] - luma[index + width]);
        }

        var valid = new byte[length];
        Array.Fill(valid, (byte)1);
        foreach (var rect in excludedRects ?? [])
        {
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var left = Math.Clamp((long)rect.X - 24, 0, width);
            var right = Math.Clamp((long)rect.Right + 24, 0, width);
            var top = Math.Clamp((long)rect.Y - 12, 0, height);
            var bottom = Math.Clamp((long)rect.Bottom + 12, 0, height);
            if (left >= right || top >= bottom) continue;
            for (var row = (int)top; row < bottom; row++)
            {
                Array.Clear(edges, row * width + (int)left, (int)(right - left));
                Array.Clear(valid, row * width + (int)left, (int)(right - left));
            }
        }

        var contentTop = 0;
        var contentBottom = height;
        bool BlackRow(int row)
        {
            var offset = row * width;
            for (var x = 0; x < width; x++)
            {
                if (luma[offset + x] > 1) return false;
            }
            return true;
        }
        while (contentTop < Math.Min(12, height) && BlackRow(contentTop)) contentTop++;
        while (contentBottom > Math.Max(0, height - 12) && BlackRow(contentBottom - 1))
        {
            contentBottom--;
        }
        if (contentTop == 12) contentTop = 0;
        if (height - contentBottom == 12) contentBottom = height;
        if (contentTop > 0) Array.Clear(valid, 0, contentTop * width);
        if (contentBottom < height)
        {
            Array.Clear(valid, contentBottom * width, (height - contentBottom) * width);
        }

        Range? validRows = contentTop == 0 && contentBottom == height
            ? null
            : new Range(contentTop, Math.Max(contentTop, contentBottom));
        return new GroundEdgeAnalysis(width, height, edges, luma, valid, validRows);
    }

    public static GroundComparisonAnalysis Compare(GroundEdgeAnalysis analysis)
    {
        ValidateEdgeAnalysis(analysis);
        return new GroundComparisonAnalysis(
            analysis.Width,
            analysis.Height,
            GroundDetectPixels(analysis),
            analysis.SourceLuma,
            analysis.ValidSourcePixels);
    }

    public static byte[] GroundDetectPixels(GroundEdgeAnalysis analysis)
    {
        ValidateEdgeAnalysis(analysis);
        var width = analysis.Width;
        var height = analysis.Height;
        var output = new byte[checked(width * height)];
        if (width < ComparisonKernelSpan || height < ComparisonWeights.Length) return output;

        var horizontalSums = new int[output.Length];
        const int halfWidth = ComparisonKernelSpan / 2;
        for (var row = 0; row < height; row++)
        {
            var offset = row * width;
            var sum = 0;
            for (var x = 0; x < ComparisonKernelSpan; x++)
            {
                sum += analysis.GroundEdgePixels[offset + x];
            }
            for (var x = halfWidth; x <= width - halfWidth; x++)
            {
                horizontalSums[offset + x] = sum;
                if (x < width - halfWidth)
                {
                    sum -= analysis.GroundEdgePixels[offset + x - halfWidth];
                    sum += analysis.GroundEdgePixels[offset + x + halfWidth];
                }
            }
        }

        const int halfHeight = 4;
        const int responseScale = ComparisonKernelSpan * 10;
        var validStart = 0;
        var validEnd = height;
        if (analysis.ValidRows is { } validRows)
        {
            validStart = validRows.Start.GetOffset(height);
            validEnd = validRows.End.GetOffset(height);
        }
        for (var row = halfHeight; row <= height - halfHeight; row++)
        {
            if (row - halfHeight < validStart || row + halfHeight >= validEnd) continue;
            for (var x = halfWidth; x <= width - halfWidth; x++)
            {
                var a = horizontalSums[(row - 4) * width + x];
                var b = horizontalSums[(row - 3) * width + x];
                var c = horizontalSums[(row - 2) * width + x];
                var d = horizontalSums[(row - 1) * width + x];
                var e = horizontalSums[row * width + x];
                var f = horizontalSums[(row + 1) * width + x];
                var g = horizontalSums[(row + 2) * width + x];
                var h = horizontalSums[(row + 3) * width + x];
                var response = (e - d) * 4 + (f - c) * 3 + (g - b) * 2 + h - a;
                if (response > 0)
                {
                    output[row * width + x] = (byte)Math.Clamp(response / responseScale, 0, 255);
                }
            }
        }
        return output;
    }

    public static IReadOnlyList<DetectedFloorLine> CleanFloorLines(
        GroundComparisonAnalysis analysis,
        GroundTheoryTuning tuning) => Theory(analysis, tuning, validateSurface: false);

    public static IReadOnlyList<DetectedFloorLine> SemanticFloorLines(
        GroundComparisonAnalysis analysis,
        GroundTheoryTuning tuning) => Theory(analysis, tuning, validateSurface: true);

    public static GroundLineDetection? Detect(
        GroundComparisonAnalysis analysis,
        double searchMinimumFrameY,
        double searchMaximumFrameY,
        GroundTheoryTuning tuning)
    {
        if (!double.IsFinite(searchMinimumFrameY) || !double.IsFinite(searchMaximumFrameY))
        {
            throw new ArgumentOutOfRangeException(nameof(searchMinimumFrameY));
        }
        var lines = CleanFloorLines(analysis, tuning);
        var segments = lines.Select(line =>
        {
            var sum = 0L;
            for (var x = line.XSpan.Start; x <= line.XSpan.EndInclusive; x++)
            {
                sum += analysis.GroundPixels[line.Row * analysis.Width + x];
            }
            var length = line.XSpan.EndInclusive - line.XSpan.Start + 1;
            return new GroundLineSegment(
                analysis.Height - line.Row - 0.5,
                line.XSpan.Start,
                line.XSpan.EndInclusive + 1,
                sum / (double)(Math.Max(1, length) * 255));
        }).ToArray();
        if (segments.Length == 0) return null;

        var lower = Math.Clamp(searchMinimumFrameY, 0, analysis.Height);
        var upper = Math.Clamp(searchMaximumFrameY, 0, analysis.Height);
        var anchor = (lower + upper) * 0.5;
        var pool = segments.Where(segment => segment.FrameY >= lower && segment.FrameY <= upper)
            .ToArray();
        if (pool.Length == 0) pool = segments;
        var primary = pool.OrderBy(segment => Math.Abs(segment.FrameY - anchor))
            .ThenByDescending(segment => segment.Confidence)
            .First();
        var notches = new List<double>();
        foreach (var segment in segments)
        {
            var length = Math.Max(1, segment.MaximumFrameX - segment.MinimumFrameX);
            var stride = Math.Max(4, length / 6);
            for (var x = segment.MinimumFrameX; x < segment.MaximumFrameX; x += stride)
            {
                notches.Add(x);
            }
            notches.Add(segment.MaximumFrameX - 1);
        }
        return new GroundLineDetection(primary.FrameY, segments, notches, primary.Confidence);
    }

    public static IReadOnlyList<DetectedFloorLine> RejectKnownForegroundLines(
        IReadOnlyList<DetectedFloorLine> lines,
        int imageHeight,
        IReadOnlyList<PixelRect> foregroundRects,
        PixelRect? knightRect = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(foregroundRects);
        if (imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageHeight));
        var expanded = foregroundRects.Where(rect => rect.Width > 0 && rect.Height > 0)
            .Select(rect => Expand(rect, 8, 4)).ToArray();
        PixelRect? knight = knightRect is { Width: > 0, Height: > 0 } candidate
            ? Expand(candidate, 8, 4)
            : null;
        if (knight is { } detected)
        {
            var inferredHeight = Math.Max(detected.Height, (int)Math.Ceiling(imageHeight * 0.12));
            knight = new PixelRect(
                detected.X,
                detected.Bottom - inferredHeight,
                detected.Width,
                inferredHeight);
        }

        return lines.Where(line =>
        {
            var row = line.Row + 0.5;
            var lower = line.XSpan.Start;
            var upper = line.XSpan.EndInclusive + 1;
            var span = Math.Max(1, upper - lower);
            if (knight is { } body)
            {
                var upperBodyBottom = body.Y + body.Height * 0.82;
                if (row >= body.Y && row < upperBodyBottom)
                {
                    var total = 0.0;
                    var covered = 0.0;
                    foreach (var evidence in line.EvidenceSpans)
                    {
                        var start = Math.Max(lower, evidence.Start);
                        var end = Math.Min(upper, evidence.EndInclusive + 1);
                        total += Math.Max(0, end - start);
                        covered += Math.Max(0, Math.Min(end, body.Right) - Math.Max(start, body.X));
                    }
                    if (total > 0 && covered / total >= 0.55) return false;
                }
            }
            return !expanded.Any(rect => row >= rect.Y && row <= rect.Bottom
                && Math.Max(0, Math.Min(upper, rect.Right) - Math.Max(lower, rect.X))
                    / (double)span >= 0.55);
        }).ToArray();
    }

    private static IReadOnlyList<DetectedFloorLine> Theory(
        GroundComparisonAnalysis analysis,
        GroundTheoryTuning tuning,
        bool validateSurface)
    {
        ValidateComparisonAnalysis(analysis);
        var threshold = Math.Clamp(tuning.GroundThreshold, 0, 255);
        var minimumLength = Math.Max(1, tuning.MinimumSegmentLength);
        var maximumGap = Math.Max(0, tuning.OcclusionMergeGap);
        var accepted = new List<CandidateRun>[analysis.Height];
        var surface = validateSurface ? new GroundSurfaceEvidence(analysis) : null;

        for (var row = 0; row < analysis.Height; row++)
        {
            var rawRuns = new List<PixelSpan>();
            int? start = null;
            var offset = row * analysis.Width;
            for (var x = 0; x < analysis.Width; x++)
            {
                if (analysis.GroundPixels[offset + x] >= threshold)
                {
                    start ??= x;
                }
                else if (start is { } runStart)
                {
                    rawRuns.Add(new PixelSpan(runStart, x - 1));
                    start = null;
                }
            }
            if (start is { } finalStart)
            {
                rawRuns.Add(new PixelSpan(finalStart, analysis.Width - 1));
            }

            accepted[row] = MergeGroundRuns(rawRuns, maximumGap).Where(run =>
            {
                var evidenceLength = run.EvidenceSpans.Sum(SpanLength);
                if (SpanLength(run.Span) < minimumLength || evidenceLength < minimumLength)
                {
                    return false;
                }
                if (!validateSurface) return true;
                var strongThreshold = Math.Min(255, threshold + 1);
                var strong = run.EvidenceSpans.Sum(span => CountMatching(
                    span,
                    x => analysis.GroundPixels[offset + x] >= strongThreshold));
                return strong >= minimumLength;
            }).ToList();
        }

        var candidates = accepted.SelectMany((runs, row) => runs.Select(run =>
            new ScoredRun(
                row,
                run,
                run.SpanRange().Aggregate(
                    0UL,
                    (sum, x) => sum + analysis.GroundPixels[row * analysis.Width + x]))))
            .OrderByDescending(item => item.Run.EvidenceSpans.Sum(SpanLength))
            .ThenByDescending(item => item.Strength)
            .ThenBy(item => item.Row)
            .ThenBy(item => item.Run.Span.Start)
            .ToArray();
        var separation = Math.Max(0, tuning.LineSeparation);
        var surviving = new List<ScoredRun>();
        foreach (var candidate in candidates)
        {
            var candidateEvidence = candidate.Run.EvidenceSpans.Sum(SpanLength);
            var suppressed = surviving.Any(stronger =>
            {
                if (Math.Abs(stronger.Row - candidate.Row) > separation) return false;
                var directOverlap = stronger.Run.EvidenceSpans.Any(strong =>
                    candidate.Run.EvidenceSpans.Any(weak =>
                        OverlapLength(strong, weak) >= Math.Min(16, SpanLength(weak))));
                if (directOverlap) return true;
                var strongerEvidence = stronger.Run.EvidenceSpans.Sum(SpanLength);
                var inside = candidate.Run.Span.Start >= stronger.Run.Span.Start
                    && candidate.Run.Span.EndInclusive <= stronger.Run.Span.EndInclusive;
                return inside && strongerEvidence >= Math.Max(
                    candidateEvidence * 2,
                    minimumLength * 2);
            });
            if (suppressed) continue;
            if (surface?.Supports(candidate.Row, [candidate.Run.Span]) == false) continue;
            surviving.Add(candidate);
        }

        var results = new List<DetectedFloorLine>();
        foreach (var candidate in surviving)
        {
            var group = new List<PixelSpan>();
            void AppendGroup()
            {
                if (group.Count == 0) return;
                var span = new PixelSpan(group[0].Start, group[^1].EndInclusive);
                if (surface?.Supports(candidate.Row, [span]) != false)
                {
                    results.Add(new DetectedFloorLine(candidate.Row, span, group));
                }
            }

            foreach (var evidence in candidate.Run.EvidenceSpans)
            {
                if (group.Count > 0)
                {
                    var prior = group[^1];
                    var gapStart = prior.EndInclusive + 1;
                    var gapEnd = evidence.Start - 1;
                    var lowerSurface = gapEnd - gapStart + 1 >= minimumLength
                        && surviving.Any(lower => lower.Row > candidate.Row + 4
                            && lower.Run.EvidenceSpans.Sum(span => OverlapLength(
                                span,
                                new PixelSpan(gapStart, gapEnd))) >= minimumLength);
                    if (lowerSurface)
                    {
                        AppendGroup();
                        group.Clear();
                    }
                }
                group.Add(evidence);
            }
            AppendGroup();
        }
        return results.OrderBy(line => line.Row).ThenBy(line => line.XSpan.Start).ToArray();
    }

    private static IReadOnlyList<CandidateRun> MergeGroundRuns(
        IReadOnlyList<PixelSpan> runs,
        int maximumGap)
    {
        if (runs.Count == 0) return [];
        var current = runs[0];
        var evidence = new List<PixelSpan> { current };
        var merged = new List<CandidateRun>();
        foreach (var run in runs.Skip(1))
        {
            var gap = run.Start - current.EndInclusive - 1;
            if (gap <= maximumGap)
            {
                current = new PixelSpan(current.Start, Math.Max(current.EndInclusive, run.EndInclusive));
                evidence.Add(run);
            }
            else
            {
                merged.Add(new CandidateRun(current, evidence.ToArray()));
                current = run;
                evidence = [run];
            }
        }
        merged.Add(new CandidateRun(current, evidence.ToArray()));
        return merged;
    }

    private static int CountMatching(PixelSpan span, Func<int, bool> predicate)
    {
        var count = 0;
        for (var x = span.Start; x <= span.EndInclusive; x++)
        {
            if (predicate(x)) count++;
        }
        return count;
    }

    private static int SpanLength(PixelSpan span) => span.EndInclusive - span.Start + 1;
    private static int OverlapLength(PixelSpan left, PixelSpan right) => Math.Max(
        0,
        Math.Min(left.EndInclusive, right.EndInclusive) - Math.Max(left.Start, right.Start) + 1);

    private static PixelRect Expand(PixelRect rect, int horizontal, int vertical) => new(
        rect.X - horizontal,
        rect.Y - vertical,
        checked(rect.Width + horizontal * 2),
        checked(rect.Height + vertical * 2));

    private static void ValidateEdgeAnalysis(GroundEdgeAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Width <= 0 || analysis.Height <= 0
            || analysis.GroundEdgePixels.Length != checked(analysis.Width * analysis.Height))
        {
            throw new ArgumentException("Ground edge analysis dimensions are invalid.", nameof(analysis));
        }
        ValidateOptionalPlane(analysis.SourceLuma, analysis.Width, analysis.Height, nameof(analysis));
        ValidateOptionalPlane(
            analysis.ValidSourcePixels,
            analysis.Width,
            analysis.Height,
            nameof(analysis));
    }

    private static void ValidateComparisonAnalysis(GroundComparisonAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Width <= 0 || analysis.Height <= 0
            || analysis.GroundPixels.Length != checked(analysis.Width * analysis.Height))
        {
            throw new ArgumentException(
                "Ground comparison analysis dimensions are invalid.",
                nameof(analysis));
        }
        ValidateOptionalPlane(analysis.SourceLuma, analysis.Width, analysis.Height, nameof(analysis));
        ValidateOptionalPlane(
            analysis.ValidSourcePixels,
            analysis.Width,
            analysis.Height,
            nameof(analysis));
    }

    private static void ValidateOptionalPlane(byte[]? plane, int width, int height, string name)
    {
        if (plane is not null && plane.Length != checked(width * height))
        {
            throw new ArgumentException("Optional analysis plane dimensions are invalid.", name);
        }
    }

    private sealed record CandidateRun(PixelSpan Span, IReadOnlyList<PixelSpan> EvidenceSpans)
    {
        internal IEnumerable<int> SpanRange()
        {
            for (var x = Span.Start; x <= Span.EndInclusive; x++) yield return x;
        }
    }

    private sealed record ScoredRun(int Row, CandidateRun Run, ulong Strength);

    private sealed class GroundSurfaceEvidence(GroundComparisonAnalysis analysis)
    {
        private const int Depth = 12;
        private const int MaximumBodyLuma = 55;
        private readonly Dictionary<int, Band> bands = [];

        internal bool Supports(int row, IReadOnlyList<PixelSpan> ranges)
        {
            if (row < 0 || row >= analysis.Height - 1
                || analysis.SourceLuma is not { } luma)
            {
                return true;
            }
            if (!bands.TryGetValue(row, out var band))
            {
                var sums = new int[analysis.Width + 1];
                var counts = new int[analysis.Width + 1];
                for (var x = 0; x < analysis.Width; x++)
                {
                    var sum = 0;
                    var count = 0;
                    if (IsValid(row * analysis.Width + x))
                    {
                        var maximumOffset = Math.Min(Depth, analysis.Height - row - 1);
                        for (var offset = 1; offset <= maximumOffset; offset++)
                        {
                            var index = (row + offset) * analysis.Width + x;
                            if (!IsValid(index)) continue;
                            sum += luma[index];
                            count++;
                        }
                    }
                    sums[x + 1] = sums[x] + sum;
                    counts[x + 1] = counts[x] + count;
                }
                band = new Band(sums, counts);
                bands[row] = band;
            }

            var total = 0;
            var countTotal = 0;
            foreach (var range in ranges)
            {
                var lower = Math.Max(0, range.Start);
                var upper = Math.Min(analysis.Width, range.EndInclusive + 1);
                if (lower >= upper) continue;
                total += band.Sums[upper] - band.Sums[lower];
                countTotal += band.Counts[upper] - band.Counts[lower];
            }
            return countTotal == 0 || total <= MaximumBodyLuma * countTotal;
        }

        private bool IsValid(int index) => analysis.ValidSourcePixels is null
            || analysis.ValidSourcePixels[index] != 0;

        private sealed record Band(int[] Sums, int[] Counts);
    }
}
