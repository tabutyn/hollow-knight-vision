namespace HollowKnightVision.Windows.Core;

public enum VisualRoomDirection
{
    Left = -1,
    Right = 1
}

public sealed record LowResolutionMotionGrid
{
    public LowResolutionMotionGrid(int width, int height, byte[] luma)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(luma);
        if (luma.Length != checked(width * height))
        {
            throw new ArgumentException("Luma length must equal width × height.", nameof(luma));
        }
        Width = width;
        Height = height;
        Luma = luma;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Luma { get; }

    public static LowResolutionMotionGrid FromFrame(
        BgraFrame frame,
        int width = 64,
        int height = 36)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var output = new byte[checked(width * height)];
        for (var outputY = 0; outputY < height; outputY++)
        {
            var sourceY0 = outputY * frame.Height / height;
            var sourceY1 = Math.Max(sourceY0 + 1, (outputY + 1) * frame.Height / height);
            for (var outputX = 0; outputX < width; outputX++)
            {
                var sourceX0 = outputX * frame.Width / width;
                var sourceX1 = Math.Max(sourceX0 + 1, (outputX + 1) * frame.Width / width);
                long sum = 0;
                var count = 0;
                for (var sourceY = sourceY0; sourceY < sourceY1; sourceY++)
                {
                    var row = frame.Row(sourceY);
                    for (var sourceX = sourceX0; sourceX < sourceX1; sourceX++)
                    {
                        var index = sourceX * BgraFrame.BytesPerPixel;
                        sum += (29 * row[index] + 150 * row[index + 1] + 77 * row[index + 2]) >> 8;
                        count++;
                    }
                }
                output[outputY * width + outputX] = checked((byte)(sum / count));
            }
        }
        return new LowResolutionMotionGrid(width, height, output);
    }
}

public sealed record LowResolutionRoomMotionEstimate(
    VisualRoomDirection Direction,
    double Confidence,
    int ScreenShift);

public sealed record LowResolutionMotionVector(
    double ScreenShiftX,
    double ScreenShiftY,
    double Confidence);

public enum LowResolutionTranslationRejection
{
    IncompatibleGrid,
    InsufficientTexture,
    InsufficientOverlap,
    InsufficientImprovement,
    InsignificantSubcellMotion
}

public sealed record LowResolutionTranslationDiagnostic(
    LowResolutionMotionVector? Motion,
    LowResolutionTranslationRejection? NarrowRejection,
    LowResolutionTranslationRejection? WideRejection)
{
    public LowResolutionTranslationRejection? FinalRejection => Motion is null
        ? WideRejection ?? NarrowRejection
        : null;
}

/// <summary>Fade-resistant 64×36 room/camera translation ported from macOS.</summary>
public sealed class LowResolutionRoomMotionTracker
{
    public const double ComparisonAgeMinimum = 0.025;
    public const double ComparisonAgeMaximum = 0.12;
    public const double PreferredComparisonAge = 0.05;
    public const double VoteLifetime = 0.65;
    public const int MaximumShift = 4;
    public const int MaximumVerticalShift = 8;
    public const int MaximumFullScoreCandidates = 6;
    public const double MinimumImprovement = 0.055;
    public const double MinimumTextureDeviation = 4.0;
    private const byte MinimumVisibleLuma = 8;
    private const double VisibilityMaskDarkFraction = 0.12;
    private readonly List<Sample> history = [];
    private readonly List<Vote> votes = [];

    public void Reset()
    {
        history.Clear();
        votes.Clear();
    }

    public LowResolutionRoomMotionEstimate? Observe(
        LowResolutionMotionGrid? grid,
        double timestamp)
    {
        if (!double.IsFinite(timestamp) || grid is null)
        {
            Expire(timestamp);
            return Consensus(timestamp);
        }
        history.Add(new Sample(timestamp, grid));
        history.RemoveAll(sample => timestamp - sample.Timestamp > ComparisonAgeMaximum);
        var reference = history
            .Where(sample =>
                timestamp - sample.Timestamp >= ComparisonAgeMinimum
                && timestamp - sample.Timestamp <= ComparisonAgeMaximum)
            .OrderBy(sample => Math.Abs(
                timestamp - sample.Timestamp - PreferredComparisonAge))
            .FirstOrDefault();
        if (reference is not null)
        {
            var estimate = Estimate(reference.Grid, grid);
            if (estimate is not null) votes.Add(new Vote(timestamp, estimate));
        }
        Expire(timestamp);
        return Consensus(timestamp);
    }

    public static LowResolutionRoomMotionEstimate? Estimate(
        LowResolutionMotionGrid reference,
        LowResolutionMotionGrid current)
    {
        var motion = Translation(reference, current);
        if (motion is null || Math.Abs(motion.ScreenShiftX) < 0.10) return null;
        var direction = motion.ScreenShiftX > 0
            ? VisualRoomDirection.Left
            : VisualRoomDirection.Right;
        var shift = motion.ScreenShiftX > 0
            ? Math.Max(1, (int)Math.Round(motion.ScreenShiftX, MidpointRounding.AwayFromZero))
            : Math.Min(-1, (int)Math.Round(motion.ScreenShiftX, MidpointRounding.AwayFromZero));
        return new LowResolutionRoomMotionEstimate(direction, motion.Confidence, shift);
    }

    public static LowResolutionMotionVector? Translation(
        LowResolutionMotionGrid reference,
        LowResolutionMotionGrid current,
        double requiredImprovement = MinimumImprovement) =>
        DiagnoseTranslation(reference, current, requiredImprovement).Motion;

    public static LowResolutionTranslationDiagnostic DiagnoseTranslation(
        LowResolutionMotionGrid reference,
        LowResolutionMotionGrid current,
        double requiredImprovement = MinimumImprovement)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(current);
        var narrow = BestTranslationAttempt(reference, current, 3, requiredImprovement);
        var wideLimit = Math.Min(MaximumVerticalShift, Math.Max(3, reference.Height / 3));
        var needsWide = narrow.Motion is null || Math.Abs(narrow.Motion.ScreenShiftY) >= 2.75;
        var wide = wideLimit > 3 && needsWide
            ? BestTranslationAttempt(reference, current, wideLimit, requiredImprovement)
            : null;
        var fallback = narrow.Motion ?? wide?.Motion;
        if (fallback is null)
        {
            return new LowResolutionTranslationDiagnostic(
                null, narrow.Rejection, wide?.Rejection);
        }
        var vertical = wide?.Motion is { } wideMotion && Math.Abs(wideMotion.ScreenShiftY) > 3
            ? wideMotion
            : narrow.Motion ?? fallback;
        var horizontal = narrow.Motion ?? fallback;
        return new LowResolutionTranslationDiagnostic(
            new LowResolutionMotionVector(
                horizontal.ScreenShiftX,
                vertical.ScreenShiftY,
                Math.Min(horizontal.Confidence, vertical.Confidence)),
            narrow.Rejection,
            wide?.Rejection);
    }

    private void Expire(double timestamp)
    {
        history.RemoveAll(sample => timestamp - sample.Timestamp > ComparisonAgeMaximum);
        votes.RemoveAll(vote => timestamp - vote.Timestamp > VoteLifetime);
    }

    private LowResolutionRoomMotionEstimate? Consensus(double timestamp)
    {
        if (votes.Count == 0) return null;
        var signedWeight = 0.0;
        var totalWeight = 0.0;
        var signedShift = 0.0;
        foreach (var vote in votes)
        {
            var age = Math.Max(0, timestamp - vote.Timestamp);
            var freshness = Math.Max(0.2, 1 - age / VoteLifetime);
            var weight = vote.Estimate.Confidence * freshness;
            signedWeight += (int)vote.Estimate.Direction * weight;
            signedShift += vote.Estimate.ScreenShift * weight;
            totalWeight += weight;
        }
        if (totalWeight <= 0.12 || Math.Abs(signedWeight) / totalWeight < 0.30) return null;
        return new LowResolutionRoomMotionEstimate(
            signedWeight < 0 ? VisualRoomDirection.Left : VisualRoomDirection.Right,
            Math.Min(1, Math.Abs(signedWeight) / totalWeight),
            (int)Math.Round(signedShift / totalWeight, MidpointRounding.AwayFromZero));
    }

    private static TranslationAttempt BestTranslationAttempt(
        LowResolutionMotionGrid reference,
        LowResolutionMotionGrid current,
        int verticalShiftLimit,
        double requiredImprovement)
    {
        if (reference.Width != current.Width || reference.Height != current.Height
            || reference.Width <= MaximumShift * 2 + 4
            || reference.Height <= verticalShiftLimit * 2 + 1)
        {
            return TranslationAttempt.Rejected(LowResolutionTranslationRejection.IncompatibleGrid);
        }
        var usesVisibilityMask = RequiresVisibilityMask(reference)
            || RequiresVisibilityMask(current);
        var normalizedReference = Normalize(reference, usesVisibilityMask);
        var normalizedCurrent = Normalize(current, usesVisibilityMask);
        if (normalizedReference.Deviation < MinimumTextureDeviation
            || normalizedCurrent.Deviation < MinimumTextureDeviation)
        {
            return TranslationAttempt.Rejected(LowResolutionTranslationRejection.InsufficientTexture);
        }
        var yStart = Math.Max(verticalShiftLimit, reference.Height / 6);
        var yEnd = Math.Min(reference.Height - verticalShiftLimit, reference.Height * 5 / 6);
        var xStart = MaximumShift;
        var xEnd = reference.Width - MaximumShift;

        double Score(int shiftX, int shiftY, int sampleStride)
        {
            var error = 0.0;
            var count = 0;
            var available = 0;
            for (var y = yStart; y < yEnd; y += sampleStride)
            {
                for (var x = xStart; x < xEnd; x += sampleStride)
                {
                    available++;
                    var referenceIndex = y * reference.Width + x;
                    var currentIndex = (y + shiftY) * current.Width + x + shiftX;
                    if (normalizedReference.Visible is not null
                        && (!normalizedReference.Visible[referenceIndex]
                            || normalizedCurrent.Visible?[currentIndex] != true))
                    {
                        continue;
                    }
                    error += Math.Abs(
                        normalizedReference.Values[referenceIndex]
                        - normalizedCurrent.Values[currentIndex]);
                    count++;
                }
            }
            return count >= Math.Max(12, available / 10)
                ? error / count
                : double.PositiveInfinity;
        }

        var coarseScores = new List<(TranslationKey Key, double Score)>();
        for (var shiftY = -verticalShiftLimit; shiftY <= verticalShiftLimit; shiftY++)
        {
            for (var shiftX = -MaximumShift; shiftX <= MaximumShift; shiftX++)
            {
                coarseScores.Add((new TranslationKey(shiftX, shiftY),
                    Score(shiftX, shiftY, 2)));
            }
        }
        coarseScores.Sort((first, second) => first.Score.CompareTo(second.Score));
        var fullKeys = coarseScores.Take(MaximumFullScoreCandidates)
            .Select(item => item.Key).ToHashSet();
        var zeroKey = new TranslationKey(0, 0);
        fullKeys.Add(zeroKey);
        if (coarseScores.FirstOrDefault().Key is { } coarseBest)
        {
            for (var y = Math.Max(-verticalShiftLimit, coarseBest.Y - 1);
                 y <= Math.Min(verticalShiftLimit, coarseBest.Y + 1); y++)
            {
                for (var x = Math.Max(-MaximumShift, coarseBest.X - 1);
                     x <= Math.Min(MaximumShift, coarseBest.X + 1); x++)
                {
                    fullKeys.Add(new TranslationKey(x, y));
                }
            }
        }
        var scores = fullKeys.ToDictionary(key => key, key => Score(key.X, key.Y, 1));
        var best = scores.MinBy(pair => pair.Value);
        if (!scores.TryGetValue(zeroKey, out var zero) || !double.IsFinite(zero)
            || zero <= 0 || !double.IsFinite(best.Value))
        {
            return TranslationAttempt.Rejected(LowResolutionTranslationRejection.InsufficientOverlap);
        }
        var improvement = (zero - best.Value) / zero;
        var second = scores.Where(pair => pair.Key != best.Key)
            .Select(pair => pair.Value).DefaultIfEmpty(zero).Min();
        var uniqueness = Math.Max(0, (second - best.Value) / Math.Max(0.0001, second));
        var residual = SubcellResidual(
            normalizedReference.Values,
            normalizedCurrent.Values,
            reference.Width,
            xStart,
            xEnd,
            yStart,
            yEnd,
            best.Key.X,
            best.Key.Y,
            normalizedReference.Visible,
            normalizedCurrent.Visible);
        var refinedX = best.Key.X + residual.X;
        var refinedY = best.Key.Y + residual.Y;
        var integerMotion = best.Key.X != 0 || best.Key.Y != 0;
        if (integerMotion && improvement < requiredImprovement)
        {
            return TranslationAttempt.Rejected(
                LowResolutionTranslationRejection.InsufficientImprovement);
        }
        if (!integerMotion)
        {
            var residualMagnitude = Length(refinedX, refinedY);
            if (residualMagnitude < 0.10
                || (uniqueness < 0.002 && residualMagnitude < 0.20))
            {
                return TranslationAttempt.Rejected(
                    LowResolutionTranslationRejection.InsignificantSubcellMotion);
            }
        }
        var confidence = Math.Min(
            1,
            Math.Max(0, improvement) * 3.5 + uniqueness * 2
                + Math.Min(0.25, Length(refinedX, refinedY) * 0.25));
        return new TranslationAttempt(
            new LowResolutionMotionVector(refinedX, refinedY, confidence), null);
    }

    private static (double X, double Y) SubcellResidual(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> current,
        int width,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        int integerX,
        int integerY,
        IReadOnlyList<bool>? referenceVisible,
        IReadOnlyList<bool>? currentVisible)
    {
        var xx = 0.0;
        var xy = 0.0;
        var yy = 0.0;
        var xb = 0.0;
        var yb = 0.0;
        for (var y = yStart; y < yEnd; y++)
        {
            for (var x = xStart; x < xEnd; x++)
            {
                var referenceIndex = y * width + x;
                var currentIndex = (y + integerY) * width + x + integerX;
                if (referenceVisible is not null
                    && (!referenceVisible[referenceIndex]
                        || !referenceVisible[referenceIndex - 1]
                        || !referenceVisible[referenceIndex + 1]
                        || !referenceVisible[referenceIndex - width]
                        || !referenceVisible[referenceIndex + width]
                        || currentVisible?[currentIndex] != true))
                {
                    continue;
                }
                var gradientX = (reference[referenceIndex + 1]
                    - reference[referenceIndex - 1]) * 0.5;
                var gradientY = (reference[referenceIndex + width]
                    - reference[referenceIndex - width]) * 0.5;
                var difference = current[currentIndex] - reference[referenceIndex];
                xx += gradientX * gradientX;
                xy += gradientX * gradientY;
                yy += gradientY * gradientY;
                xb += gradientX * difference;
                yb += gradientY * difference;
            }
        }
        var determinant = xx * yy - xy * xy;
        if (!double.IsFinite(determinant)
            || determinant <= Math.Max(0.000001, xx * yy * 0.001)) return (0, 0);
        var xResidual = (xy * yb - yy * xb) / determinant;
        var yResidual = (xy * xb - xx * yb) / determinant;
        if (Math.Abs(xResidual) < 0.01) xResidual = 0;
        if (Math.Abs(yResidual) < 0.01) yResidual = 0;
        return (
            Math.Clamp(double.IsFinite(xResidual) ? xResidual : 0, -0.5, 0.5),
            Math.Clamp(double.IsFinite(yResidual) ? yResidual : 0, -0.5, 0.5));
    }

    private static bool RequiresVisibilityMask(LowResolutionMotionGrid grid) =>
        (double)grid.Luma.Count(value => value <= MinimumVisibleLuma) / grid.Luma.Length
        >= VisibilityMaskDarkFraction;

    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);

    private static NormalizedGrid Normalize(
        LowResolutionMotionGrid grid,
        bool excludingDarkPixels)
    {
        var values = grid.Luma.Select(value => (double)value).ToArray();
        var visible = excludingDarkPixels
            ? grid.Luma.Select(value => value > MinimumVisibleLuma).ToArray()
            : null;
        var retained = visible is null
            ? values
            : values.Where((_, index) => visible[index]).ToArray();
        if (retained.Length == 0) return new NormalizedGrid(new double[values.Length], 0, visible);
        var mean = retained.Average();
        var variance = retained.Sum(value => (value - mean) * (value - mean)) / retained.Length;
        var deviation = Math.Sqrt(variance);
        return deviation > 0.001
            ? new NormalizedGrid(values.Select(value => (value - mean) / deviation).ToArray(),
                deviation, visible)
            : new NormalizedGrid(new double[values.Length], deviation, visible);
    }

    private sealed record Sample(double Timestamp, LowResolutionMotionGrid Grid);
    private sealed record Vote(double Timestamp, LowResolutionRoomMotionEstimate Estimate);
    private readonly record struct TranslationKey(int X, int Y);
    private sealed record TranslationAttempt(
        LowResolutionMotionVector? Motion,
        LowResolutionTranslationRejection? Rejection)
    {
        internal static TranslationAttempt Rejected(LowResolutionTranslationRejection rejection) =>
            new(null, rejection);
    }
    private sealed record NormalizedGrid(double[] Values, double Deviation, bool[]? Visible);
}
