using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record VisualRoomSnapshot(
    int RoomID,
    string RoomName,
    ulong Revision,
    AtlasPoint EntryWorldPose,
    bool IsTransitioning,
    VisualRoomDirection? TransitionDirection,
    AtlasPoint? TransitionWorldPose,
    AtlasPoint? ActivationWorldPose,
    AtlasPoint? CoarseWorldPose,
    ulong CoarsePoseRevision,
    LowResolutionMotionGrid? CoarseMotionGrid,
    LowResolutionRoomMotionEstimate? CoarseMotionEstimate,
    LowResolutionPlaceMatch? CoarsePlaceMatch,
    bool CoarseMotionIsControlling,
    IReadOnlyList<VisualRoomPortal> Portals,
    VisualRoomCompositionBounds CompositionBounds)
{
    public string StatusText => IsTransitioning && TransitionDirection is { } direction
        ? $"{RoomName} → {direction.Label()}"
        : RoomName;
}

public sealed record VisualRoomPortal(
    int ID,
    int LeftRoomID,
    int RightRoomID,
    double LeftDoorWorldX,
    double RightDoorWorldX,
    FrameFractionRange LeftDoorYRange,
    FrameFractionRange RightDoorYRange)
{
    public double ConnectorLength => Math.Max(0, RightDoorWorldX - LeftDoorWorldX);
}

public readonly record struct VisualRoomCompositionBounds(
    double? MinimumWorldX,
    double? MaximumWorldX)
{
    public static VisualRoomCompositionBounds Unbounded => new(null, null);
}

public static class VisualRoomCompositionMask
{
    public static IReadOnlyList<StencilRect> OmittedRects(
        VisualRoomCompositionBounds bounds,
        AtlasPoint cameraPosition,
        double solveWidth,
        WorldSize frameSize)
    {
        if (!double.IsFinite(cameraPosition.X)
            || !double.IsFinite(solveWidth) || solveWidth <= 0
            || !double.IsFinite(frameSize.Width) || frameSize.Width <= 0
            || !double.IsFinite(frameSize.Height) || frameSize.Height <= 0)
        {
            return [];
        }

        var pixelsPerWorldUnit = frameSize.Width / solveWidth;
        var result = new List<StencilRect>(2);
        if (bounds.MinimumWorldX is { } minimum && double.IsFinite(minimum))
        {
            var x = Math.Min(frameSize.Width, Math.Max(
                0, (minimum - cameraPosition.X) * pixelsPerWorldUnit));
            if (x > 0) result.Add(new StencilRect(0, 0, x, frameSize.Height));
        }
        if (bounds.MaximumWorldX is { } maximum && double.IsFinite(maximum))
        {
            var x = Math.Min(frameSize.Width, Math.Max(
                0, (maximum - cameraPosition.X) * pixelsPerWorldUnit));
            if (x < frameSize.Width)
            {
                result.Add(new StencilRect(
                    x, 0, frameSize.Width - x, frameSize.Height));
            }
        }
        return result;
    }
}

public sealed record VisualRoomPortalLayout(
    AtlasPoint TargetEntryWorldPose,
    double SourceDoorWorldX,
    double TargetDoorWorldX,
    FrameFractionRange SourceDoorYRange,
    FrameFractionRange TargetDoorYRange)
{
    public static AtlasPoint? PlausibleContinuousArrival(
        AtlasPoint? candidate,
        VisualRoomDirection direction,
        AtlasPoint departureWorldPose,
        double solveWidth,
        double solveHeight)
    {
        if (candidate is not { } value) return null;
        var deltaX = value.X - departureWorldPose.X;
        var signedTravel = deltaX * (int)direction;
        return signedTravel >= 0
            && signedTravel <= solveWidth * 1.5
            && Math.Abs(value.Y - departureWorldPose.Y) <= solveHeight * .75
            ? value
            : null;
    }

    public static VisualRoomPortalLayout Make(
        VisualRoomDirection direction,
        AtlasPoint departureWorldPose,
        double solveWidth,
        double solveHeight,
        FrameEdgeBlackBand? sourceBand,
        FrameEdgeBlackBand? targetBand,
        LowResolutionTransitionTravel? measuredTravel,
        AtlasPoint? continuousArrivalWorldPose = null)
    {
        var sourceBandWidth = Math.Min(solveWidth * .72,
            Math.Max(0, (sourceBand?.WidthFraction ?? 0) * solveWidth));
        var targetBandWidth = Math.Min(solveWidth * .72,
            Math.Max(0, (targetBand?.WidthFraction ?? 0) * solveWidth));
        var sourceScreenX = direction == VisualRoomDirection.Left
            ? sourceBandWidth
            : solveWidth - sourceBandWidth;
        var targetScreenX = direction == VisualRoomDirection.Left
            ? solveWidth - targetBandWidth
            : targetBandWidth;
        var visibleTravel = Math.Max(0,
            solveWidth - sourceBandWidth - targetBandWidth);
        double? measuredConnector = measuredTravel is
            { SampleCount: >= 2, MeanConfidence: >= .12 } travel
            ? Math.Max(0, Math.Abs(travel.WorldDeltaX) - visibleTravel)
            : null;
        var sourceDoorWorldX = departureWorldPose.X + sourceScreenX;
        AtlasPoint targetEntry;
        double targetDoorWorldX;
        if (continuousArrivalWorldPose is { } arrival)
        {
            targetEntry = arrival;
            targetDoorWorldX = arrival.X + targetScreenX;
        }
        else
        {
            var connector = Math.Min(solveWidth * .30,
                Math.Max(8, measuredConnector ?? solveWidth * .025));
            targetDoorWorldX = sourceDoorWorldX + (int)direction * connector;
            targetEntry = new AtlasPoint(
                targetDoorWorldX - targetScreenX,
                departureWorldPose.Y);
        }

        static FrameFractionRange YRange(FrameEdgeBlackBand? band, double cameraY,
            double solveHeight)
        {
            var normalized = band?.ContentYRangeFraction ?? new FrameFractionRange(.38, .68);
            return new FrameFractionRange(
                cameraY + normalized.Lower * solveHeight,
                cameraY + normalized.Upper * solveHeight);
        }

        var aligned = VisualRoomDoorGeometry.Aligned(
            YRange(sourceBand, departureWorldPose.Y, solveHeight),
            YRange(targetBand, targetEntry.Y, solveHeight));
        return new VisualRoomPortalLayout(
            targetEntry,
            sourceDoorWorldX,
            targetDoorWorldX,
            aligned.First,
            aligned.Second);
    }
}

public static class VisualRoomDoorGeometry
{
    public const double MaximumHeight = 120;

    public static (FrameFractionRange First, FrameFractionRange Second) Aligned(
        FrameFractionRange first,
        FrameFractionRange second)
    {
        var firstCenter = (first.Lower + first.Upper) * .5;
        var secondCenter = (second.Lower + second.Upper) * .5;
        var center = Math.Min(firstCenter, secondCenter);
        var measuredHeight = Math.Min(
            first.Upper - first.Lower,
            second.Upper - second.Lower);
        var height = Math.Min(MaximumHeight, Math.Max(24, measuredHeight));
        var range = new FrameFractionRange(center - height * .5, center + height * .5);
        return (range, range);
    }
}

public sealed record VisualRoomBoundaryEvidence(
    int LeftRoomID,
    int RightRoomID,
    AtlasPoint LeftOriginalEntryWorldPose,
    AtlasPoint RightDepartureWorldPose,
    double SolveWidth,
    double SolveHeight,
    FrameEdgeBlackBand? RightRoomLeftBand,
    FrameEdgeBlackBand? LeftRoomRightBand);

public sealed record LowResolutionTransitionTravel(
    double WorldDeltaX,
    int SampleCount,
    double MeanConfidence);

public sealed record LowResolutionPlaceMatch(
    int KeyframeID,
    AtlasPoint CameraPosition,
    double Score,
    double Margin);

/// <summary>
/// Sparse 64x36 room-local place memory. Two independently timed matches must
/// agree before a remembered camera position can reanchor floorless tracking.
/// </summary>
public sealed class LowResolutionPlaceMemory
{
    public const int MaximumEntriesPerRoom = 24;
    public const int MaximumFullAlignmentCandidates = 3;
    public const double MinimumEntryDistance = 80;
    public const double MinimumEntryInterval = .25;
    public const double SearchInterval = .09;
    public const double MaximumScore = .34;
    public const double MaximumSingleEntryScore = .20;
    public const double MaximumMaskedSingleEntryScore = .38;
    public const double MinimumMargin = .08;
    public const double ConfirmationIntervalMinimum = .08;
    public const double ConfirmationIntervalMaximum = .60;
    public const double ConfirmationDistance = 24;
    public const int MaximumAlignmentShift = 2;
    private const byte MinimumVisibleLuma = 20;
    private const double VisibilityMaskDarkFraction = .12;

    private readonly Dictionary<int, List<Entry>> entries = [];
    private readonly Dictionary<int, double> lastRememberedAt = [];
    private int nextID = 1;
    private double? lastSearchAt;
    private Pending? pending;

    public void Reset()
    {
        entries.Clear();
        nextID = 1;
        lastRememberedAt.Clear();
        lastSearchAt = null;
        pending = null;
    }

    public void Remember(
        LowResolutionMotionGrid? grid,
        int roomID,
        AtlasPoint cameraPosition,
        double timestamp)
    {
        if (grid is null || !double.IsFinite(timestamp)
            || !double.IsFinite(cameraPosition.X) || !double.IsFinite(cameraPosition.Y)
            || lastRememberedAt.TryGetValue(roomID, out var last)
                && timestamp - last < MinimumEntryInterval)
        {
            return;
        }
        var normalized = Normalized(grid);
        if (normalized.Deviation < LowResolutionRoomMotionTracker.MinimumTextureDeviation)
            return;
        if (!entries.TryGetValue(roomID, out var roomEntries))
        {
            roomEntries = [];
            entries[roomID] = roomEntries;
        }
        if (roomEntries.Any(entry => Distance(entry.CameraPosition, cameraPosition)
            < MinimumEntryDistance)) return;

        roomEntries.Add(new Entry(
            nextID++, cameraPosition, grid.Width, grid.Height,
            normalized.Values, normalized.Visible));
        if (roomEntries.Count > MaximumEntriesPerRoom)
            roomEntries.RemoveAt(roomEntries.Count > 1 ? 1 : 0);
        lastRememberedAt[roomID] = timestamp;
    }

    public LowResolutionPlaceMatch? Observe(
        LowResolutionMotionGrid? grid,
        int roomID,
        double solveWidth,
        double timestamp)
    {
        if (grid is null || !double.IsFinite(timestamp)
            || !double.IsFinite(solveWidth) || solveWidth <= 0
            || lastSearchAt is { } searched && timestamp - searched < SearchInterval
            || !entries.TryGetValue(roomID, out var roomEntries)
            || roomEntries.Count == 0)
        {
            return null;
        }
        lastSearchAt = timestamp;
        var current = Normalized(grid);
        if (current.Deviation < LowResolutionRoomMotionTracker.MinimumTextureDeviation)
        {
            pending = null;
            return null;
        }
        var pixelScale = solveWidth / Math.Max(1, grid.Width);
        var shortlist = roomEntries
            .Where(entry => entry.Width == grid.Width && entry.Height == grid.Height)
            .Select(entry => (Entry: entry, Score: QuickScore(
                entry.NormalizedLuma, current.Values, entry.Visible,
                current.Visible, grid.Width, grid.Height)))
            .OrderBy(item => item.Score)
            .Take(MaximumFullAlignmentCandidates)
            .Select(item => (item.Entry,
                Alignment: Align(item.Entry, current.Values, current.Visible)))
            .Where(item => item.Alignment is not null)
            .Select(item => (item.Entry, Alignment: item.Alignment!))
            .OrderBy(item => item.Alignment.Score)
            .ToArray();
        if (shortlist.Length == 0)
        {
            pending = null;
            return null;
        }
        var best = shortlist[0];
        double? secondScore = shortlist.Length > 1 ? shortlist[1].Alignment.Score : null;
        var margin = secondScore is { } second
            ? second - best.Alignment.Score
            : double.PositiveInfinity;
        var scoreLimit = secondScore is null
            ? current.Visible is null ? MaximumSingleEntryScore : MaximumMaskedSingleEntryScore
            : MaximumScore;
        if (best.Alignment.Score > scoreLimit
            || secondScore is not null && margin < MinimumMargin)
        {
            pending = null;
            return null;
        }
        var position = new AtlasPoint(
            best.Entry.CameraPosition.X - best.Alignment.ShiftX * pixelScale,
            best.Entry.CameraPosition.Y + best.Alignment.ShiftY * pixelScale);
        var candidate = new Candidate(best.Entry, position, best.Alignment.Score, margin);
        if (pending is not { } prior
            || prior.KeyframeID != candidate.Entry.ID
            || timestamp - prior.Timestamp < ConfirmationIntervalMinimum
            || timestamp - prior.Timestamp > ConfirmationIntervalMaximum
            || Distance(prior.CameraPosition, candidate.CameraPosition) > ConfirmationDistance)
        {
            pending = new Pending(candidate.Entry.ID, candidate.CameraPosition, timestamp);
            return null;
        }
        pending = null;
        return new LowResolutionPlaceMatch(
            candidate.Entry.ID, candidate.CameraPosition, candidate.Score, candidate.Margin);
    }

    public void ClearPending()
    {
        lastSearchAt = null;
        pending = null;
    }

    private static Alignment? Align(
        Entry reference,
        IReadOnlyList<double> current,
        IReadOnlyList<bool>? currentVisible)
    {
        if (reference.NormalizedLuma.Length != current.Count
            || reference.Width <= MaximumAlignmentShift * 2 + 8
            || reference.Height <= MaximumAlignmentShift * 2 + 8) return null;
        var xStart = MaximumAlignmentShift + 2;
        var xEnd = reference.Width - MaximumAlignmentShift - 2;
        var yStart = Math.Max(MaximumAlignmentShift, reference.Height / 6);
        var yEnd = Math.Min(
            reference.Height - MaximumAlignmentShift,
            reference.Height * 5 / 6);

        double Score(int shiftX, int shiftY)
        {
            var referenceSum = 0d;
            var currentSum = 0d;
            var referenceSquareSum = 0d;
            var currentSquareSum = 0d;
            var count = 0;
            var available = 0;
            for (var y = yStart; y < yEnd; y++)
            for (var x = xStart; x < xEnd; x++)
            {
                available++;
                var referenceIndex = y * reference.Width + x;
                var currentIndex = (y + shiftY) * reference.Width + x + shiftX;
                if (reference.Visible?[referenceIndex] == false
                    || currentVisible?[currentIndex] == false) continue;
                var referenceValue = reference.NormalizedLuma[referenceIndex];
                var currentValue = current[currentIndex];
                referenceSum += referenceValue;
                currentSum += currentValue;
                referenceSquareSum += referenceValue * referenceValue;
                currentSquareSum += currentValue * currentValue;
                count++;
            }
            if (count < Math.Max(24, available / 10)) return double.PositiveInfinity;
            var referenceMean = referenceSum / count;
            var currentMean = currentSum / count;
            var referenceDeviation = Math.Sqrt(Math.Max(
                0, referenceSquareSum / count - referenceMean * referenceMean));
            var currentDeviation = Math.Sqrt(Math.Max(
                0, currentSquareSum / count - currentMean * currentMean));
            if (referenceDeviation < .1 || currentDeviation < .1)
                return double.PositiveInfinity;
            var error = 0d;
            for (var y = yStart; y < yEnd; y++)
            for (var x = xStart; x < xEnd; x++)
            {
                var referenceIndex = y * reference.Width + x;
                var currentIndex = (y + shiftY) * reference.Width + x + shiftX;
                if (reference.Visible?[referenceIndex] == false
                    || currentVisible?[currentIndex] == false) continue;
                error += Math.Abs(
                    (reference.NormalizedLuma[referenceIndex] - referenceMean)
                        / referenceDeviation
                    - (current[currentIndex] - currentMean) / currentDeviation);
            }
            return error / count;
        }

        Alignment? best = null;
        for (var shiftY = -MaximumAlignmentShift;
             shiftY <= MaximumAlignmentShift; shiftY++)
        for (var shiftX = -MaximumAlignmentShift;
             shiftX <= MaximumAlignmentShift; shiftX++)
        {
            var candidateScore = Score(shiftX, shiftY);
            if (!double.IsFinite(candidateScore)) continue;
            var candidate = new Alignment(shiftX, shiftY, candidateScore);
            if (best is null || candidate.Score < best.Score) best = candidate;
        }
        return best;
    }

    private static double QuickScore(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> current,
        IReadOnlyList<bool>? referenceVisible,
        IReadOnlyList<bool>? currentVisible,
        int width,
        int height)
    {
        if (reference.Count != current.Count || reference.Count != width * height)
            return double.PositiveInfinity;
        var referenceSum = 0d;
        var currentSum = 0d;
        var referenceSquareSum = 0d;
        var currentSquareSum = 0d;
        var count = 0;
        var available = 0;
        for (var y = Math.Max(2, height / 6); y < Math.Min(height - 2, height * 5 / 6); y += 3)
        for (var x = 4; x < width - 4; x += 4)
        {
            available++;
            var index = y * width + x;
            if (referenceVisible?[index] == false || currentVisible?[index] == false) continue;
            var referenceValue = reference[index];
            var currentValue = current[index];
            referenceSum += referenceValue;
            currentSum += currentValue;
            referenceSquareSum += referenceValue * referenceValue;
            currentSquareSum += currentValue * currentValue;
            count++;
        }
        if (count < Math.Max(12, available / 10)) return double.PositiveInfinity;
        var referenceMean = referenceSum / count;
        var currentMean = currentSum / count;
        var referenceDeviation = Math.Sqrt(Math.Max(
            0, referenceSquareSum / count - referenceMean * referenceMean));
        var currentDeviation = Math.Sqrt(Math.Max(
            0, currentSquareSum / count - currentMean * currentMean));
        if (referenceDeviation < .1 || currentDeviation < .1)
            return double.PositiveInfinity;
        var error = 0d;
        for (var y = Math.Max(2, height / 6); y < Math.Min(height - 2, height * 5 / 6); y += 3)
        for (var x = 4; x < width - 4; x += 4)
        {
            var index = y * width + x;
            if (referenceVisible?[index] == false || currentVisible?[index] == false) continue;
            error += Math.Abs(
                (reference[index] - referenceMean) / referenceDeviation
                - (current[index] - currentMean) / currentDeviation);
        }
        return error / count;
    }

    private static NormalizedGrid Normalized(LowResolutionMotionGrid grid)
    {
        var values = grid.Luma.Select(value => (double)value).ToArray();
        var darkCount = grid.Luma.Count(value => value <= MinimumVisibleLuma);
        var usesVisibilityMask = (double)darkCount / Math.Max(1, grid.Luma.Length)
            >= VisibilityMaskDarkFraction;
        bool[]? visible = usesVisibilityMask
            ? grid.Luma.Select(value => value > MinimumVisibleLuma).ToArray()
            : null;
        var retained = visible is null
            ? values
            : values.Where((_, index) => visible[index]).ToArray();
        if (retained.Length == 0)
            return new NormalizedGrid(new double[values.Length], 0, visible);
        var mean = retained.Average();
        var variance = retained.Sum(value => (value - mean) * (value - mean))
            / retained.Length;
        var deviation = Math.Sqrt(variance);
        return deviation > .001
            ? new NormalizedGrid(
                LocallyNormalized(values, grid.Width, grid.Height, visible),
                deviation,
                visible)
            : new NormalizedGrid(new double[values.Length], deviation, visible);
    }

    private static double[] LocallyNormalized(
        IReadOnlyList<double> values,
        int width,
        int height,
        IReadOnlyList<bool>? visible)
    {
        if (width <= 0 || height <= 0 || values.Count != width * height)
            return new double[values.Count];
        var output = new double[values.Count];
        const int radius = 2;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            if (visible?[index] == false) continue;
            var sum = 0d;
            var squareSum = 0d;
            var count = 0;
            for (var sampleY = Math.Max(0, y - radius);
                 sampleY <= Math.Min(height - 1, y + radius); sampleY++)
            for (var sampleX = Math.Max(0, x - radius);
                 sampleX <= Math.Min(width - 1, x + radius); sampleX++)
            {
                var sampleIndex = sampleY * width + sampleX;
                if (visible?[sampleIndex] == false) continue;
                var value = values[sampleIndex];
                sum += value;
                squareSum += value * value;
                count++;
            }
            if (count < 6) continue;
            var mean = sum / count;
            var localDeviation = Math.Sqrt(Math.Max(
                0, squareSum / count - mean * mean));
            if (localDeviation < 1) continue;
            output[index] = (values[index] - mean) / localDeviation;
        }
        return output;
    }

    private static double Distance(AtlasPoint first, AtlasPoint second) =>
        Math.Sqrt(
            (first.X - second.X) * (first.X - second.X)
            + (first.Y - second.Y) * (first.Y - second.Y));

    private sealed record Entry(
        int ID,
        AtlasPoint CameraPosition,
        int Width,
        int Height,
        double[] NormalizedLuma,
        bool[]? Visible);
    private sealed record Candidate(Entry Entry, AtlasPoint CameraPosition,
        double Score, double Margin);
    private sealed record Pending(int KeyframeID, AtlasPoint CameraPosition, double Timestamp);
    private sealed record Alignment(int ShiftX, int ShiftY, double Score);
    private sealed record NormalizedGrid(double[] Values, double Deviation, bool[]? Visible);
}

public static class LowResolutionMotionDiagnosticPresentation
{
    public static string Text(
        LowResolutionRoomMotionEstimate? estimate,
        bool isControlling,
        double solveWidth,
        int gridWidth)
    {
        if (estimate is null
            || !double.IsFinite(solveWidth)
            || solveWidth <= 0
            || gridWidth <= 0)
        {
            return "Coarse Motion · ready · no reliable shift";
        }

        var worldStep = Math.Abs(estimate.ScreenShift) * solveWidth / gridWidth;
        var mode = isControlling ? "controlling" : "ready";
        var confidence = (int)Math.Round(
            estimate.Confidence * 100,
            MidpointRounding.AwayFromZero);
        var roundedStep = (int)Math.Round(
            worldStep,
            MidpointRounding.AwayFromZero);
        return $"Coarse Motion · {mode} · {estimate.Direction.Label()} "
            + $"{roundedStep} px · {confidence}%";
    }
}

public static class LowResolutionMotionDiagnosticRenderer
{
    public static BgraFrame? Image(
        LowResolutionMotionGrid grid,
        LowResolutionRoomMotionEstimate? estimate,
        bool isControlling)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.Width <= 0
            || grid.Height <= 0
            || grid.Luma.Length != checked(grid.Width * grid.Height))
        {
            return null;
        }

        var mean = grid.Luma.Sum(value => (long)value) / (double)grid.Luma.Length;
        var pixels = new byte[checked(grid.Width * grid.Height * BgraFrame.BytesPerPixel)];
        for (var index = 0; index < grid.Luma.Length; index++)
        {
            var contrast = Math.Clamp(
                (int)Math.Round(
                    128 + (grid.Luma[index] - mean) * 2.2,
                    MidpointRounding.AwayFromZero),
                0,
                255);
            var output = index * BgraFrame.BytesPerPixel;
            pixels[output] = (byte)contrast;
            pixels[output + 1] = (byte)contrast;
            pixels[output + 2] = (byte)contrast;
            pixels[output + 3] = 255;
        }

        var accent = isControlling
            ? (Blue: (byte)31, Green: (byte)132, Red: (byte)255)
            : estimate is null
                ? (Blue: (byte)150, Green: (byte)150, Red: (byte)150)
                : (Blue: (byte)255, Green: (byte)230, Red: (byte)30);

        void Paint(int x, int y)
        {
            if ((uint)x >= (uint)grid.Width || (uint)y >= (uint)grid.Height) return;
            var index = (y * grid.Width + x) * BgraFrame.BytesPerPixel;
            pixels[index] = accent.Blue;
            pixels[index + 1] = accent.Green;
            pixels[index + 2] = accent.Red;
            pixels[index + 3] = 255;
        }

        for (var x = 0; x < grid.Width; x++)
        {
            Paint(x, 0);
            Paint(x, grid.Height - 1);
        }
        for (var y = 0; y < grid.Height; y++)
        {
            Paint(0, y);
            Paint(grid.Width - 1, y);
        }

        if (estimate is not null)
        {
            var centerX = grid.Width / 2;
            var centerY = grid.Height / 2;
            var length = Math.Min(
                Math.Max(2, Math.Abs(estimate.ScreenShift) * 2),
                Math.Max(2, grid.Width / 2 - 2));
            var sign = (int)estimate.Direction;
            var endX = Math.Min(
                grid.Width - 2,
                Math.Max(1, centerX + sign * length));
            for (var x = Math.Min(centerX, endX);
                 x <= Math.Max(centerX, endX);
                 x++)
            {
                Paint(x, centerY);
            }
            Paint(endX, centerY - 1);
            Paint(endX, centerY + 1);
        }

        return new BgraFrame(
            grid.Width,
            grid.Height,
            checked(grid.Width * BgraFrame.BytesPerPixel),
            pixels,
            captureId: 0,
            observedAt: DateTimeOffset.UnixEpoch);
    }
}

/// <summary>Capture-rate floorless odometry ported from macOS.</summary>
public sealed class LowResolutionTransitionOdometry
{
    public const double MinimumSampleAge = .025;
    public const double MaximumSampleAge = .12;
    public const double MaximumOppositeDirectionSlackCells = 1.5;
    public const double PredictionHorizon = .48;
    public const int MaximumPredictedSteps = 9;
    public const int RequiredVelocitySamples = 1;
    public const double VerticalPredictionHorizon = .25;
    public const int MaximumConsecutiveVerticalPredictions = 2;
    private const int RetainedVelocitySamples = 3;

    private Sample? reference;
    private readonly List<VelocitySample> horizontalVelocities = [];
    private readonly List<VelocitySample> verticalVelocities = [];
    private double confidenceTotal;
    private double? lastMeasuredAt;
    private double? lastVerticalMeasuredAt;
    private VisualRoomDirection? lastExpectedDirection;
    private int predictedSteps;
    private int predictedVerticalSteps;
    private bool hasReliableCameraVelocityLineage;

    public double WorldDeltaX { get; private set; }
    public double WorldDeltaY { get; private set; }
    public int SampleCount { get; private set; }
    public bool IsArmed => reference is not null;
    public LowResolutionTransitionTravel Travel => new(
        WorldDeltaX,
        SampleCount,
        SampleCount > 0 ? confidenceTotal / SampleCount : 0);

    public void Reset()
    {
        reference = null;
        WorldDeltaX = 0;
        WorldDeltaY = 0;
        SampleCount = 0;
        confidenceTotal = 0;
        horizontalVelocities.Clear();
        lastMeasuredAt = null;
        lastExpectedDirection = null;
        predictedSteps = 0;
        verticalVelocities.Clear();
        lastVerticalMeasuredAt = null;
        predictedVerticalSteps = 0;
        hasReliableCameraVelocityLineage = false;
    }

    public void Arm(LowResolutionMotionGrid? grid, double timestamp)
    {
        Reset();
        if (grid is not null && double.IsFinite(timestamp))
            reference = new Sample(grid, timestamp);
    }

    public void PrimeHorizontalVelocity(
        LowResolutionCameraVelocitySeed? seed,
        VisualRoomDirection? expectedDirection)
    {
        if (seed is not { } value
            || !double.IsFinite(value.Timestamp)
            || !double.IsFinite(value.Velocity.X)
            || !double.IsFinite(value.Velocity.Y)) return;
        var seeded = false;
        if (expectedDirection is { } direction
            && Math.Abs(value.Velocity.X) is >= 5 and <= 1_000
            && StepAgrees(value.Velocity.X, direction))
        {
            horizontalVelocities.Clear();
            horizontalVelocities.Add(new VelocitySample(value.Velocity.X, .5));
            lastMeasuredAt = value.Timestamp;
            lastExpectedDirection = direction;
            predictedSteps = 0;
            seeded = true;
        }
        if (Math.Abs(value.Velocity.Y) is >= 5 and <= 1_000)
        {
            verticalVelocities.Clear();
            verticalVelocities.Add(new VelocitySample(value.Velocity.Y, .5));
            lastVerticalMeasuredAt = value.Timestamp;
            predictedVerticalSteps = 0;
            seeded = true;
        }
        if (seeded) lastExpectedDirection = expectedDirection;
        hasReliableCameraVelocityLineage = seeded;
    }

    public double? Observe(
        LowResolutionMotionGrid? grid,
        double timestamp,
        double solveWidth,
        VisualRoomDirection? expectedDirection)
    {
        if (!double.IsFinite(timestamp) || !double.IsFinite(solveWidth)
            || solveWidth <= 0 || grid is null) return null;
        if (reference is not { } prior)
        {
            reference = new Sample(grid, timestamp);
            return null;
        }
        var age = timestamp - prior.Timestamp;
        if (age < MinimumSampleAge) return null;
        reference = new Sample(grid, timestamp);
        if (age > MaximumSampleAge)
        {
            ClearVelocityPrediction();
            ClearVerticalVelocityPrediction();
            return null;
        }
        if (expectedDirection != lastExpectedDirection)
        {
            ClearVelocityPrediction();
            if (lastExpectedDirection is { } oldDirection
                && expectedDirection is { } newDirection
                && oldDirection != newDirection)
            {
                ClearVerticalVelocityPrediction();
            }
            lastExpectedDirection = expectedDirection;
        }
        var pixelScale = solveWidth / Math.Max(1, grid.Width);
        var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(
            prior.Grid, grid);
        if (diagnostic.Motion is not { } motion)
        {
            if (diagnostic.FinalRejection is not
                (LowResolutionTranslationRejection.InsufficientOverlap
                or LowResolutionTranslationRejection.InsufficientImprovement))
            {
                return null;
            }
            var predictedHorizontal = PredictedHorizontalStep(
                age, timestamp, pixelScale, expectedDirection);
            var predictedVertical = PredictedVerticalStep(age, timestamp, pixelScale);
            if (predictedHorizontal is null && predictedVertical is null) return null;
            var stepX = predictedHorizontal?.Step ?? 0;
            var stepY = predictedVertical?.Step ?? 0;
            WorldDeltaX += stepX;
            WorldDeltaY += stepY;
            SampleCount++;
            confidenceTotal += Math.Max(
                predictedHorizontal?.Confidence ?? 0,
                predictedVertical?.Confidence ?? 0);
            if (predictedHorizontal is not null) predictedSteps++;
            if (predictedVertical is not null) predictedVerticalSteps++;
            return stepX;
        }
        var measuredStepX = -motion.ScreenShiftX * pixelScale;
        var measuredStepY = motion.ScreenShiftY * pixelScale;
        if (!double.IsFinite(measuredStepX) || !double.IsFinite(measuredStepY)) return null;
        var verticalPrediction = PredictedVerticalStep(age, timestamp, pixelScale);
        var usesPredictedVertical = verticalPrediction is { } predictedY
            && ShouldUsePredictedVertical(measuredStepY, predictedY.Step);
        var acceptedStepY = usesPredictedVertical
            ? verticalPrediction!.Value.Step
            : measuredStepY;

        if (expectedDirection is { } expected
            && hasReliableCameraVelocityLineage
            && !StepAgrees(measuredStepX, expected)
            && PredictedHorizontalStep(age, timestamp, pixelScale, expectedDirection)
                is { } contradictionPrediction)
        {
            WorldDeltaX += contradictionPrediction.Step;
            WorldDeltaY += acceptedStepY;
            SampleCount++;
            confidenceTotal += Math.Max(
                contradictionPrediction.Confidence,
                usesPredictedVertical ? verticalPrediction?.Confidence ?? 0 : motion.Confidence);
            predictedSteps++;
            if (usesPredictedVertical) predictedVerticalSteps++;
            else RememberMeasuredVerticalVelocity(measuredStepY, age, timestamp, motion.Confidence);
            return contradictionPrediction.Step;
        }

        if (!AcceptsDirection(motion, expectedDirection))
        {
            if (PredictedHorizontalStep(age, timestamp, pixelScale, expectedDirection)
                is { } directionPrediction)
            {
                WorldDeltaX += directionPrediction.Step;
                WorldDeltaY += acceptedStepY;
                SampleCount++;
                confidenceTotal += Math.Max(
                    directionPrediction.Confidence,
                    usesPredictedVertical ? verticalPrediction?.Confidence ?? 0 : motion.Confidence);
                predictedSteps++;
                if (usesPredictedVertical) predictedVerticalSteps++;
                else RememberMeasuredVerticalVelocity(
                    measuredStepY, age, timestamp, motion.Confidence);
                return directionPrediction.Step;
            }
            ClearVelocityPrediction();
            lastExpectedDirection = expectedDirection;
            return null;
        }

        var horizontalPrediction = PredictedHorizontalStep(
            age, timestamp, pixelScale, expectedDirection);
        var usesPredictedHorizontal = horizontalPrediction is { } predictedX
            && ShouldUsePredictedHorizontal(measuredStepX, predictedX.Step);
        var acceptedStepX = usesPredictedHorizontal
            ? horizontalPrediction!.Value.Step
            : measuredStepX;
        WorldDeltaX += acceptedStepX;
        WorldDeltaY += acceptedStepY;
        SampleCount++;
        confidenceTotal += Math.Max(
            usesPredictedHorizontal ? horizontalPrediction?.Confidence ?? 0 : motion.Confidence,
            usesPredictedVertical ? verticalPrediction?.Confidence ?? 0 : motion.Confidence);
        if (usesPredictedHorizontal) predictedSteps++;
        else RememberMeasuredVelocity(
            measuredStepX, age, timestamp, motion.Confidence, expectedDirection);
        if (usesPredictedVertical) predictedVerticalSteps++;
        else RememberMeasuredVerticalVelocity(
            measuredStepY, age, timestamp, motion.Confidence);
        return acceptedStepX;
    }

    public static bool AcceptsDirection(
        LowResolutionMotionVector motion,
        VisualRoomDirection? expectedDirection)
    {
        if (expectedDirection is not { } expected
            || Math.Abs(motion.ScreenShiftX) < .0001) return true;
        var cameraStepX = -motion.ScreenShiftX;
        var opposesIntent = expected == VisualRoomDirection.Left
            ? cameraStepX >= 0
            : cameraStepX <= 0;
        if (!opposesIntent) return true;
        var horizontalDominant = Math.Abs(motion.ScreenShiftX)
            >= Math.Abs(motion.ScreenShiftY);
        return !horizontalDominant
            || Math.Abs(motion.ScreenShiftX) <= MaximumOppositeDirectionSlackCells;
    }

    private void RememberMeasuredVerticalVelocity(
        double stepY,
        double age,
        double timestamp,
        double confidence)
    {
        if (!hasReliableCameraVelocityLineage || age <= 0
            || !double.IsFinite(stepY) || Math.Abs(stepY) < .25)
        {
            if (Math.Abs(stepY) < .25) predictedVerticalSteps++;
            return;
        }
        var velocity = stepY / age;
        if (MedianVerticalVelocity() is { } prior && velocity * prior < 0)
            verticalVelocities.Clear();
        verticalVelocities.Add(new VelocitySample(velocity, confidence));
        if (verticalVelocities.Count > RetainedVelocitySamples)
            verticalVelocities.RemoveRange(
                0, verticalVelocities.Count - RetainedVelocitySamples);
        lastVerticalMeasuredAt = timestamp;
        predictedVerticalSteps = 0;
    }

    private void RememberMeasuredVelocity(
        double stepX,
        double age,
        double timestamp,
        double confidence,
        VisualRoomDirection? expectedDirection)
    {
        if (!hasReliableCameraVelocityLineage
            || expectedDirection is not { } expected
            || age <= 0 || !StepAgrees(stepX, expected)
            || Math.Abs(stepX) < .25)
        {
            ClearVelocityPrediction();
            lastExpectedDirection = expectedDirection;
            return;
        }
        horizontalVelocities.Add(new VelocitySample(stepX / age, confidence));
        if (horizontalVelocities.Count > RetainedVelocitySamples)
            horizontalVelocities.RemoveRange(
                0, horizontalVelocities.Count - RetainedVelocitySamples);
        lastMeasuredAt = timestamp;
        predictedSteps = 0;
    }

    private void ClearVelocityPrediction()
    {
        horizontalVelocities.Clear();
        lastMeasuredAt = null;
        predictedSteps = 0;
    }

    private void ClearVerticalVelocityPrediction()
    {
        verticalVelocities.Clear();
        lastVerticalMeasuredAt = null;
        predictedVerticalSteps = 0;
    }

    private PredictedStep? PredictedVerticalStep(
        double age,
        double timestamp,
        double pixelScale)
    {
        if (!hasReliableCameraVelocityLineage
            || verticalVelocities.Count < RequiredVelocitySamples
            || lastVerticalMeasuredAt is not { } last
            || timestamp - last > VerticalPredictionHorizon
            || predictedVerticalSteps >= MaximumConsecutiveVerticalPredictions)
        {
            return null;
        }
        var velocity = verticalVelocities[^1].WorldPixelsPerSecond;
        var maximumStep = LowResolutionRoomMotionTracker.MaximumVerticalShift * pixelScale;
        var step = Math.Clamp(velocity * age, -maximumStep, maximumStep);
        if (!double.IsFinite(step) || Math.Abs(step) < .25) return null;
        var confidence = verticalVelocities.Average(value => value.Confidence) * .35;
        return new PredictedStep(step, confidence);
    }

    private double? MedianVerticalVelocity()
    {
        if (verticalVelocities.Count == 0) return null;
        var ordered = verticalVelocities.OrderBy(value => value.WorldPixelsPerSecond).ToArray();
        return ordered[ordered.Length / 2].WorldPixelsPerSecond;
    }

    private static bool ShouldUsePredictedVertical(double measured, double predicted) =>
        Math.Abs(predicted) >= 4
        && (measured * predicted < 0 || Math.Abs(measured) < Math.Abs(predicted) * .35);

    private static bool ShouldUsePredictedHorizontal(double measured, double predicted) =>
        Math.Abs(predicted) >= 4
        && measured * predicted > 0
        && Math.Abs(measured) < Math.Abs(predicted) * .85;

    private PredictedStep? PredictedHorizontalStep(
        double age,
        double timestamp,
        double pixelScale,
        VisualRoomDirection? expectedDirection)
    {
        if (expectedDirection is not { } expected
            || !hasReliableCameraVelocityLineage
            || expected != lastExpectedDirection
            || horizontalVelocities.Count < RequiredVelocitySamples
            || lastMeasuredAt is not { } last
            || timestamp - last > PredictionHorizon
            || predictedSteps >= MaximumPredictedSteps) return null;
        var ordered = horizontalVelocities
            .OrderBy(value => value.WorldPixelsPerSecond).ToArray();
        var velocity = ordered[ordered.Length / 2].WorldPixelsPerSecond;
        if (!StepAgrees(velocity, expected)) return null;
        var maximumStep = LowResolutionRoomMotionTracker.MaximumShift * pixelScale;
        var step = Math.Clamp(velocity * age, -maximumStep, maximumStep);
        if (!double.IsFinite(step) || Math.Abs(step) < .25) return null;
        return new PredictedStep(
            step, horizontalVelocities.Average(value => value.Confidence) * .35);
    }

    private static bool StepAgrees(double step, VisualRoomDirection direction) =>
        direction == VisualRoomDirection.Left ? step < 0 : step > 0;

    private sealed record Sample(LowResolutionMotionGrid Grid, double Timestamp);
    private sealed record VelocitySample(double WorldPixelsPerSecond, double Confidence);
    private readonly record struct PredictedStep(double Step, double Confidence);
}

internal static class VisualRoomDirectionExtensions
{
    internal static VisualRoomDirection Opposite(this VisualRoomDirection direction) =>
        direction == VisualRoomDirection.Left
            ? VisualRoomDirection.Right
            : VisualRoomDirection.Left;

    internal static string Label(this VisualRoomDirection direction) =>
        direction == VisualRoomDirection.Left ? "left" : "right";
}

/// <summary>
/// Image-only room ownership, doorway topology, and floorless pose tracker.
/// This is the production state machine used by the macOS implementation;
/// screen and world Y coordinates remain top-origin throughout.
/// </summary>
public sealed class VisualRoomTransitionTracker
{
    public const double EvidenceLifetime = 1.5;
    public const int ArrivalStableFrames = 3;
    public const int MaximumArrivalFrames = 45;
    public const int MaximumFadeFrames = 600;
    public const int AppearanceStableFrames = 12;
    public const double EdgeFraction = .22;
    public const double PortalReentryToleranceInScreens = .30;
    public const double FadeFallbackDepartureFraction = .855;
    private const int CurrentSchemaVersion = 3;

    private readonly object sync = new();
    private int activeRoomID;
    private AtlasPoint activeEntryWorldPose;
    private ulong revision;
    private int nextRoomID = 1;
    private int nextPortalID = 1;
    private readonly Dictionary<EdgeKey, PortalTarget> portals = [];
    private readonly Dictionary<int, PortalConnection> portalConnections = [];
    private readonly Dictionary<int, RoomAppearance> roomAppearances = [];
    private RoomAppearance? activeAppearance;
    private TimedDirection? lastIntent;
    private TimedDirection? lastBoundary;
    private TimedKnightObservation? lastKnightObservation;
    private PendingTransition? pending;
    private readonly LowResolutionRoomMotionTracker motionTracker = new();
    private readonly LowResolutionTransitionOdometry transitionOdometry = new();
    private readonly LowResolutionTransitionOdometry floorlessOdometry = new();
    private readonly LowResolutionPlaceMemory placeMemory = new();
    private AtlasPoint? floorlessAnchorWorldPose;
    private AtlasPoint? floorlessWorldPose;
    private bool floorlessPoseMeasured;
    private ulong floorlessPoseRevision;
    private MotionSample? previousMotionSample;
    private FrameEdgeBlackBands? previousEdgeBlackBands;
    private LowResolutionMotionGrid? latestCoarseMotionGrid;
    private LowResolutionRoomMotionEstimate? latestCoarseMotionEstimate;
    private LowResolutionPlaceMatch? latestCoarsePlaceMatch;
    private AtlasPoint? activationWorldPose;

    public VisualRoomSnapshot Snapshot
    {
        get { lock (sync) return SnapshotLocked(); }
    }

    public bool ApplyFloorlessCorrection(
        CameraVector correction,
        int roomID,
        ulong coarsePoseRevision)
    {
        lock (sync)
        {
            if (roomID != activeRoomID
                || coarsePoseRevision != floorlessPoseRevision
                || !double.IsFinite(correction.X) || !double.IsFinite(correction.Y)
                || floorlessAnchorWorldPose is not { } anchor
                || floorlessWorldPose is not { } position
                || !floorlessPoseMeasured) return false;
            floorlessAnchorWorldPose = new AtlasPoint(
                anchor.X + correction.X, anchor.Y + correction.Y);
            floorlessWorldPose = new AtlasPoint(
                position.X + correction.X, position.Y + correction.Y);
            floorlessPoseRevision = unchecked(floorlessPoseRevision + 1);
            return true;
        }
    }

    public VisualRoomCompositionBounds CompositionBounds(int roomID)
    {
        lock (sync) return CompositionBoundsLocked(roomID);
    }

    public VisualRoomSnapshot RestoreTopology(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(data);
        ValidatePersistedShape(document.RootElement);
        var state = JsonSerializer.Deserialize<PersistedState>(data, SerializerOptions)
            ?? throw new InvalidDataException("Visual room topology is empty.");
        ValidatePersistedState(state);

        lock (sync)
        {
            var appearances = UniqueDictionary(
                state.Appearances!, value => value.RoomID,
                value => ParseAppearance(value.Value), "appearance room");
            var connections = UniqueDictionary(
                state.Connections!, value => value.ID,
                value =>
                {
                    var ranges = VisualRoomDoorGeometry.Aligned(
                        new FrameFractionRange(
                            value.LeftMinimumWorldY, value.LeftMaximumWorldY),
                        new FrameFractionRange(
                            value.RightMinimumWorldY, value.RightMaximumWorldY));
                    return new PortalConnection(
                        value.ID, value.LeftRoomID, value.RightRoomID,
                        value.LeftDoorWorldX, value.RightDoorWorldX,
                        ranges.First, ranges.Second);
                }, "portal connection");
            var restoredPortals = new Dictionary<EdgeKey, PortalTarget>();
            foreach (var edge in state.Edges!)
            {
                var direction = (VisualRoomDirection)edge.Direction;
                if (!connections.ContainsKey(edge.PortalID))
                    throw new InvalidDataException("Portal edge references a missing connection.");
                var key = new EdgeKey(edge.SourceRoomID, direction);
                if (!restoredPortals.TryAdd(key, new PortalTarget(
                    edge.TargetRoomID,
                    new AtlasPoint(edge.EntryX, edge.EntryY),
                    new AtlasPoint(edge.DepartureX, edge.DepartureY),
                    edge.PortalID)))
                {
                    throw new InvalidDataException("Visual room topology has duplicate edges.");
                }
            }

            activeRoomID = state.ActiveRoomID;
            activeEntryWorldPose = new AtlasPoint(state.ActiveEntryX, state.ActiveEntryY);
            revision = state.Revision;
            nextRoomID = state.NextRoomID;
            nextPortalID = state.NextPortalID;
            roomAppearances.Clear();
            foreach (var pair in appearances) roomAppearances.Add(pair.Key, pair.Value);
            activeAppearance = state.ActiveAppearance is null
                ? null
                : ParseAppearance(state.ActiveAppearance);
            portalConnections.Clear();
            foreach (var pair in connections) portalConnections.Add(pair.Key, pair.Value);
            portals.Clear();
            foreach (var pair in restoredPortals) portals.Add(pair.Key, pair.Value);
            ClearTransientState(resetPlaceMemory: true);
            return SnapshotLocked();
        }
    }

    public void SaveTopology(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] data;
        lock (sync)
        {
            var state = new PersistedState
            {
                SchemaVersion = CurrentSchemaVersion,
                ActiveRoomID = activeRoomID,
                ActiveEntryX = activeEntryWorldPose.X,
                ActiveEntryY = activeEntryWorldPose.Y,
                Revision = revision,
                NextRoomID = nextRoomID,
                NextPortalID = nextPortalID,
                ActiveAppearance = activeAppearance is { } appearance
                    ? AppearanceWireValue(appearance)
                    : null,
                Appearances = roomAppearances.OrderBy(pair => pair.Key)
                    .Select(pair => new PersistedAppearance
                    {
                        RoomID = pair.Key,
                        Value = AppearanceWireValue(pair.Value)
                    }).ToArray(),
                Edges = portals.OrderBy(pair => pair.Key.RoomID)
                    .ThenBy(pair => (int)pair.Key.Direction)
                    .Select(pair => new PersistedEdge
                    {
                        SourceRoomID = pair.Key.RoomID,
                        Direction = (int)pair.Key.Direction,
                        TargetRoomID = pair.Value.RoomID,
                        EntryX = pair.Value.EntryWorldPose.X,
                        EntryY = pair.Value.EntryWorldPose.Y,
                        DepartureX = pair.Value.DepartureWorldPose.X,
                        DepartureY = pair.Value.DepartureWorldPose.Y,
                        PortalID = pair.Value.PortalID
                    }).ToArray(),
                Connections = portalConnections.Values.OrderBy(value => value.ID)
                    .Select(value => new PersistedConnection
                    {
                        ID = value.ID,
                        LeftRoomID = value.LeftRoomID,
                        RightRoomID = value.RightRoomID,
                        LeftDoorWorldX = value.LeftDoorWorldX,
                        RightDoorWorldX = value.RightDoorWorldX,
                        LeftMinimumWorldY = value.LeftDoorYRange.Lower,
                        LeftMaximumWorldY = value.LeftDoorYRange.Upper,
                        RightMinimumWorldY = value.RightDoorYRange.Lower,
                        RightMaximumWorldY = value.RightDoorYRange.Upper
                    }).ToArray()
            };
            data = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Visual room topology path has no parent.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public VisualRoomSnapshot BootstrapTwoRoomTopology(
        int leftRoomID,
        int rightRoomID,
        double leftDoorWorldX,
        double rightDoorWorldX,
        FrameFractionRange leftDoorYRange,
        FrameFractionRange rightDoorYRange,
        AtlasPoint leftEntryWorldPose,
        AtlasPoint rightEntryWorldPose,
        int? activeRoomID = null)
    {
        lock (sync)
        {
            if (portals.Count != 0 || portalConnections.Count != 0
                || leftRoomID < 0 || rightRoomID < 0 || leftRoomID == rightRoomID
                || !double.IsFinite(leftDoorWorldX) || !double.IsFinite(rightDoorWorldX)
                || leftDoorWorldX > rightDoorWorldX
                || !Valid(leftDoorYRange) || !Valid(rightDoorYRange))
            {
                return SnapshotLocked();
            }
            var portalID = nextPortalID++;
            var ranges = VisualRoomDoorGeometry.Aligned(leftDoorYRange, rightDoorYRange);
            portalConnections[portalID] = new PortalConnection(
                portalID, leftRoomID, rightRoomID,
                leftDoorWorldX, rightDoorWorldX, ranges.First, ranges.Second);
            portals[new EdgeKey(rightRoomID, VisualRoomDirection.Left)] =
                new PortalTarget(
                    leftRoomID, leftEntryWorldPose, rightEntryWorldPose, portalID);
            portals[new EdgeKey(leftRoomID, VisualRoomDirection.Right)] =
                new PortalTarget(
                    rightRoomID, rightEntryWorldPose, leftEntryWorldPose, portalID);
            this.activeRoomID = activeRoomID == leftRoomID ? leftRoomID : rightRoomID;
            activeEntryWorldPose = this.activeRoomID == leftRoomID
                ? leftEntryWorldPose
                : rightEntryWorldPose;
            nextRoomID = Math.Max(nextRoomID, Math.Max(leftRoomID, rightRoomID) + 1);
            revision = unchecked(revision + 1);
            activeAppearance = null;
            roomAppearances.Clear();
            ResetFloorlessTraversal();
            activationWorldPose = null;
            return SnapshotLocked();
        }
    }

    public AtlasPoint? EntryWorldPose(int roomID)
    {
        lock (sync)
        {
            if (activeRoomID == roomID) return activeEntryWorldPose;
            return portals.Values.FirstOrDefault(value => value.RoomID == roomID)
                ?.EntryWorldPose;
        }
    }

    public VisualRoomSnapshot RestoreActiveRoomFromDurableEvidence(int roomID)
    {
        lock (sync)
        {
            if (roomID < 0 || roomID == activeRoomID
                || portals.Values.FirstOrDefault(value => value.RoomID == roomID)
                    is not { } target)
            {
                return SnapshotLocked();
            }
            activeRoomID = roomID;
            activeEntryWorldPose = target.EntryWorldPose;
            activeAppearance = roomAppearances.TryGetValue(roomID, out var appearance)
                ? appearance
                : null;
            pending = null;
            lastIntent = null;
            lastBoundary = null;
            lastKnightObservation = null;
            transitionOdometry.Reset();
            ResetFloorlessTraversal();
            activationWorldPose = null;
            return SnapshotLocked();
        }
    }

    public VisualRoomSnapshot Reset()
    {
        lock (sync)
        {
            activeRoomID = 0;
            activeEntryWorldPose = default;
            revision = 0;
            nextRoomID = 1;
            nextPortalID = 1;
            portals.Clear();
            portalConnections.Clear();
            roomAppearances.Clear();
            activeAppearance = null;
            ClearTransientState(resetPlaceMemory: true);
            return SnapshotLocked();
        }
    }

    public VisualRoomSnapshot Observe(
        FrameSignalProfile profile,
        TransitionFrameDecision baseDecision,
        VisualRoomDirection? horizontalInput,
        StencilRect? knightRect,
        WorldSize frameSize,
        AtlasPoint currentWorldPose,
        double solveWidth,
        double timestamp,
        bool groundTrackingReliable = true,
        LowResolutionCameraVelocitySeed? reliableCameraVelocity = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (sync)
        {
            if (!double.IsFinite(timestamp)
                || !double.IsFinite(solveWidth) || solveWidth <= 0
                || !double.IsFinite(frameSize.Width) || frameSize.Width <= 0
                || !double.IsFinite(frameSize.Height) || frameSize.Height <= 0
                || !double.IsFinite(currentWorldPose.X)
                || !double.IsFinite(currentWorldPose.Y))
            {
                return SnapshotLocked();
            }
            var priorMotionSample = previousMotionSample;
            var priorEdgeBlackBands = previousEdgeBlackBands;
            try
            {
                if (horizontalInput is { } input)
                    lastIntent = new TimedDirection(input, timestamp);
                var knightScreenCenter = ScreenCenter(
                    knightRect, frameSize, solveWidth);
                if (knightScreenCenter is { } center)
                {
                    lastKnightObservation = new TimedKnightObservation(
                        center, currentWorldPose, timestamp);
                }
                var currentBoundary = BoundaryDirection(knightRect, frameSize);
                if (currentBoundary is { } boundary)
                    lastBoundary = new TimedDirection(boundary, timestamp);
                ExpireEvidence(timestamp);
                var screenMotion = motionTracker.Observe(profile.MotionGrid, timestamp);
                latestCoarseMotionGrid = profile.MotionGrid;
                latestCoarseMotionEstimate = screenMotion;
                UpdateFloorlessTraversal(
                    groundTrackingReliable,
                    horizontalInput,
                    screenMotion,
                    priorMotionSample,
                    profile.MotionGrid,
                    currentWorldPose,
                    reliableCameraVelocity,
                    solveWidth,
                    timestamp);
                var solveHeight = frameSize.Height / frameSize.Width * solveWidth;
                var entranceDirection = pending?.Direction
                    ?? EligibleDepartureDirection(timestamp, true, screenMotion)
                    ?? EntranceTrackingDirection(
                        horizontalInput, currentBoundary, currentWorldPose, solveWidth);
                if (entranceDirection is { } entrance)
                {
                    if (!transitionOdometry.IsArmed)
                    {
                        transitionOdometry.Arm(
                            priorMotionSample?.Grid ?? profile.MotionGrid,
                            priorMotionSample?.Timestamp ?? timestamp);
                    }
                    _ = transitionOdometry.Observe(
                        profile.MotionGrid, timestamp, solveWidth, entrance);
                }
                else
                {
                    transitionOdometry.Reset();
                }

                if (activeAppearance is null && profile.HasGameplaySignal)
                {
                    var initial = InitialAppearance(profile);
                    activeAppearance = initial;
                    roomAppearances[activeRoomID] = initial;
                }
                if (activeAppearance is not { } currentAppearance)
                    return SnapshotLocked();

                if (!profile.HasGameplaySignal)
                {
                    if (pending?.Kind != PendingKind.Blackout
                        && EligibleDepartureDirection(timestamp, true, screenMotion)
                            is { } direction)
                    {
                        pending = new PendingTransition(
                            activeRoomID,
                            direction,
                            floorlessAnchorWorldPose ?? currentWorldPose,
                            solveWidth,
                            solveHeight,
                            PendingKind.Blackout,
                            null,
                            priorEdgeBlackBands?.Band(direction),
                            null,
                            lastKnightObservation,
                            null,
                            screenMotion?.Direction == direction,
                            0,
                            false,
                            0,
                            0);
                    }
                    else if (pending?.Kind == PendingKind.Blackout)
                    {
                        pending.ConsecutiveTargetFrames = 0;
                    }
                    return SnapshotLocked();
                }

                var appearance = Appearance(profile, currentAppearance);
                if (pending is { } activePending)
                {
                    activePending.TotalFrames++;
                    if (horizontalInput == activePending.Direction.Opposite())
                    {
                        pending = null;
                        transitionOdometry.Reset();
                        return SnapshotLocked();
                    }
                    var coarseMotionOpposesDeparture = screenMotion?.Direction
                        == activePending.Direction.Opposite();
                    activePending.OppositeDirectionFrames = coarseMotionOpposesDeparture
                        ? activePending.OppositeDirectionFrames + 1
                        : 0;
                    if (activePending.OppositeDirectionFrames >= 3
                        && activePending.Kind != PendingKind.Fade)
                    {
                        pending = null;
                        transitionOdometry.Reset();
                        return SnapshotLocked();
                    }
                    if (screenMotion?.Direction == activePending.Direction)
                        activePending.HasScreenMotionEvidence = true;
                    if (currentBoundary == activePending.Direction.Opposite())
                    {
                        activePending.SawOppositeArrivalEdge = true;
                        activePending.ArrivalKnightCenter ??= knightScreenCenter;
                    }
                    if (profile.EdgeBlackBands?.Band(
                        activePending.Direction.Opposite()) is { } arrivalBand)
                    {
                        activePending.ArrivalEdgeBand = arrivalBand;
                    }

                    switch (activePending.Kind)
                    {
                        case PendingKind.Appearance:
                            if (appearance != activePending.TargetAppearance)
                            {
                                pending = null;
                                transitionOdometry.Reset();
                                return SnapshotLocked();
                            }
                            activePending.ConsecutiveTargetFrames++;
                            var isKnownDirectedPortal = portals.ContainsKey(new EdgeKey(
                                activePending.SourceRoomID, activePending.Direction));
                            var requiredFrames = isKnownDirectedPortal
                                ? ArrivalStableFrames
                                : AppearanceStableFrames;
                            if (activePending.ConsecutiveTargetFrames < requiredFrames)
                                return SnapshotLocked();
                            ActivateRoom(activePending, activePending.TargetAppearance!.Value);
                            break;
                        case PendingKind.Blackout:
                            activePending.ConsecutiveTargetFrames++;
                            var arrivalIsStable = activePending.ConsecutiveTargetFrames
                                >= ArrivalStableFrames || baseDecision.ResumedAfterTransition;
                            if (!arrivalIsStable
                                || !activePending.SawOppositeArrivalEdge
                                    && appearance == currentAppearance)
                            {
                                if (activePending.ConsecutiveTargetFrames >= MaximumArrivalFrames)
                                {
                                    pending = null;
                                    lastIntent = null;
                                    lastBoundary = null;
                                    transitionOdometry.Reset();
                                }
                                return SnapshotLocked();
                            }
                            ActivateRoom(activePending, appearance);
                            break;
                        case PendingKind.Fade:
                            var sourceAppearance = activePending.TargetAppearance!.Value;
                            if (appearance != sourceAppearance)
                            {
                                activePending.ConsecutiveTargetFrames = 0;
                                if (activePending.TotalFrames >= MaximumFadeFrames)
                                {
                                    pending = null;
                                    lastIntent = null;
                                    lastBoundary = null;
                                    transitionOdometry.Reset();
                                }
                                return SnapshotLocked();
                            }
                            activePending.ConsecutiveTargetFrames++;
                            if (activePending.ConsecutiveTargetFrames < ArrivalStableFrames
                                || !activePending.SawOppositeArrivalEdge)
                            {
                                if (activePending.TotalFrames >= MaximumFadeFrames)
                                {
                                    pending = null;
                                    lastIntent = null;
                                    lastBoundary = null;
                                    transitionOdometry.Reset();
                                }
                                return SnapshotLocked();
                            }
                            ActivateRoom(activePending, sourceAppearance);
                            break;
                    }
                    pending = null;
                    lastIntent = null;
                    lastBoundary = null;
                    transitionOdometry.Reset();
                    ResetFloorlessTraversal();
                    return SnapshotLocked();
                }

                if (appearance == currentAppearance) return SnapshotLocked();
                var strictDirection = EligibleDepartureDirection(
                    timestamp, true, screenMotion);
                if (appearance == RoomAppearance.Dark && knightRect is null
                    && strictDirection is { } fadeDirection)
                {
                    if (!transitionOdometry.IsArmed)
                    {
                        transitionOdometry.Arm(
                            priorMotionSample?.Grid ?? profile.MotionGrid,
                            priorMotionSample?.Timestamp ?? timestamp);
                    }
                    _ = transitionOdometry.Observe(
                        profile.MotionGrid, timestamp, solveWidth, fadeDirection);
                    pending = new PendingTransition(
                        activeRoomID,
                        fadeDirection,
                        floorlessAnchorWorldPose ?? currentWorldPose,
                        solveWidth,
                        solveHeight,
                        PendingKind.Fade,
                        currentAppearance,
                        priorEdgeBlackBands?.Band(fadeDirection),
                        null,
                        lastKnightObservation,
                        currentBoundary == fadeDirection.Opposite()
                            ? knightScreenCenter
                            : null,
                        screenMotion?.Direction == fadeDirection,
                        0,
                        currentBoundary == fadeDirection.Opposite(),
                        0,
                        0);
                    return SnapshotLocked();
                }
                if (strictDirection is not { } transitionDirection
                    || !AppearanceTransitionIsSpatiallyEligible(
                        activeRoomID,
                        transitionDirection,
                        appearance,
                        currentWorldPose,
                        solveWidth))
                {
                    return SnapshotLocked();
                }
                pending = new PendingTransition(
                    activeRoomID,
                    transitionDirection,
                    floorlessAnchorWorldPose ?? currentWorldPose,
                    solveWidth,
                    solveHeight,
                    PendingKind.Appearance,
                    appearance,
                    priorEdgeBlackBands?.Band(transitionDirection),
                    profile.EdgeBlackBands?.Band(transitionDirection.Opposite()),
                    lastKnightObservation,
                    currentBoundary == transitionDirection.Opposite()
                        ? knightScreenCenter
                        : null,
                    screenMotion?.Direction == transitionDirection,
                    1,
                    currentBoundary == transitionDirection.Opposite(),
                    0,
                    0);
                return SnapshotLocked();
            }
            finally
            {
                if (profile.MotionGrid is { } grid)
                    previousMotionSample = new MotionSample(grid, timestamp);
                previousEdgeBlackBands = profile.EdgeBlackBands;
            }
        }
    }

    public static bool IsNearBlack(FrameSignalProfile profile) =>
        InitialAppearance(profile) == RoomAppearance.Dark;

    private static RoomAppearance InitialAppearance(FrameSignalProfile profile) =>
        profile.MeanPeak <= 18 && profile.VisibleFraction <= .40
            ? RoomAppearance.Dark
            : RoomAppearance.Normal;

    private static RoomAppearance Appearance(
        FrameSignalProfile profile,
        RoomAppearance current)
    {
        if (profile.MeanPeak <= 18 && profile.VisibleFraction <= .40)
            return RoomAppearance.Dark;
        if (profile.MeanPeak >= 24 || profile.VisibleFraction >= .46)
            return RoomAppearance.Normal;
        return current;
    }

    private VisualRoomSnapshot SnapshotLocked()
    {
        var publicPortals = portalConnections.Values.OrderBy(value => value.ID)
            .Select(value => new VisualRoomPortal(
                value.ID,
                value.LeftRoomID,
                value.RightRoomID,
                value.LeftDoorWorldX,
                value.RightDoorWorldX,
                value.LeftDoorYRange,
                value.RightDoorYRange))
            .ToArray();
        AtlasPoint? floorlessPublishedPose = floorlessPoseMeasured
            ? floorlessWorldPose
            : null;
        AtlasPoint? transitionWorldPose = pending is { } activePending
            ? floorlessPublishedPose ?? new AtlasPoint(
                activePending.DepartureWorldPose.X + transitionOdometry.WorldDeltaX,
                activePending.DepartureWorldPose.Y + transitionOdometry.WorldDeltaY)
            : null;
        return new VisualRoomSnapshot(
            activeRoomID,
            $"Room {activeRoomID + 1}",
            revision,
            activeEntryWorldPose,
            pending is not null,
            pending?.Direction,
            transitionWorldPose,
            activationWorldPose,
            floorlessPublishedPose,
            floorlessPoseRevision,
            latestCoarseMotionGrid,
            latestCoarseMotionEstimate,
            latestCoarsePlaceMatch,
            floorlessPublishedPose is not null
                || pending is not null && transitionOdometry.SampleCount > 0,
            publicPortals,
            CompositionBoundsLocked(activeRoomID));
    }

    private void UpdateFloorlessTraversal(
        bool groundTrackingReliable,
        VisualRoomDirection? horizontalInput,
        LowResolutionRoomMotionEstimate? screenMotion,
        MotionSample? priorMotionSample,
        LowResolutionMotionGrid? currentGrid,
        AtlasPoint currentWorldPose,
        LowResolutionCameraVelocitySeed? reliableCameraVelocity,
        double solveWidth,
        double timestamp)
    {
        if (groundTrackingReliable)
        {
            placeMemory.Remember(
                currentGrid, activeRoomID, currentWorldPose, timestamp);
            latestCoarsePlaceMatch = null;
            ResetFloorlessTraversal();
            return;
        }
        if (floorlessAnchorWorldPose is null)
        {
            floorlessPoseRevision = unchecked(floorlessPoseRevision + 1);
            floorlessAnchorWorldPose = currentWorldPose;
            floorlessWorldPose = currentWorldPose;
            floorlessOdometry.Arm(
                priorMotionSample?.Grid ?? currentGrid,
                priorMotionSample?.Timestamp ?? timestamp);
            floorlessOdometry.PrimeHorizontalVelocity(
                reliableCameraVelocity,
                horizontalInput ?? screenMotion?.Direction);
        }
        if (floorlessOdometry.Observe(
            currentGrid,
            timestamp,
            solveWidth,
            horizontalInput ?? screenMotion?.Direction) is not null)
        {
            floorlessPoseMeasured = true;
        }
        if (floorlessAnchorWorldPose is { } anchor)
        {
            floorlessWorldPose = new AtlasPoint(
                anchor.X + floorlessOdometry.WorldDeltaX,
                anchor.Y + floorlessOdometry.WorldDeltaY);
        }
        latestCoarsePlaceMatch = placeMemory.Observe(
            currentGrid, activeRoomID, solveWidth, timestamp);
        if (latestCoarsePlaceMatch is { } match)
        {
            floorlessPoseRevision = unchecked(floorlessPoseRevision + 1);
            floorlessAnchorWorldPose = match.CameraPosition;
            floorlessWorldPose = match.CameraPosition;
            floorlessPoseMeasured = true;
            floorlessOdometry.Arm(currentGrid, timestamp);
        }
    }

    private void ResetFloorlessTraversal()
    {
        if (floorlessAnchorWorldPose is not null || floorlessPoseMeasured)
            floorlessPoseRevision = unchecked(floorlessPoseRevision + 1);
        floorlessOdometry.Reset();
        placeMemory.ClearPending();
        floorlessAnchorWorldPose = null;
        floorlessWorldPose = null;
        floorlessPoseMeasured = false;
    }

    private static VisualRoomDirection? BoundaryDirection(
        StencilRect? knightRect,
        WorldSize frameSize)
    {
        if (knightRect is not { } rect || rect.IsEmpty || !rect.IsFinite
            || !double.IsFinite(frameSize.Width) || frameSize.Width <= 0) return null;
        if (rect.MidX <= frameSize.Width * EdgeFraction)
            return VisualRoomDirection.Left;
        if (rect.MidX >= frameSize.Width * (1 - EdgeFraction))
            return VisualRoomDirection.Right;
        return null;
    }

    private static AtlasPoint? ScreenCenter(
        StencilRect? knightRect,
        WorldSize frameSize,
        double solveWidth)
    {
        if (knightRect is not { } rect || rect.IsEmpty || !rect.IsFinite
            || !double.IsFinite(frameSize.Width) || frameSize.Width <= 0
            || !double.IsFinite(frameSize.Height) || frameSize.Height <= 0) return null;
        var scale = solveWidth / frameSize.Width;
        return new AtlasPoint(rect.MidX * scale, rect.MidY * scale);
    }

    private VisualRoomDirection? EntranceTrackingDirection(
        VisualRoomDirection? horizontalInput,
        VisualRoomDirection? currentBoundary,
        AtlasPoint currentWorldPose,
        double solveWidth)
    {
        if (horizontalInput is { } input && currentBoundary == input) return input;
        if (horizontalInput is not { } direction
            || !portals.TryGetValue(new EdgeKey(activeRoomID, direction), out var portal)
            || !PortalIsNearby(portal, currentWorldPose, solveWidth)) return null;
        return direction;
    }

    private static bool Valid(FrameFractionRange range) =>
        double.IsFinite(range.Lower) && double.IsFinite(range.Upper)
        && range.Upper > range.Lower;

    private void ExpireEvidence(double timestamp)
    {
        if (lastIntent is { } intent && timestamp - intent.Timestamp > EvidenceLifetime)
            lastIntent = null;
        if (lastBoundary is { } boundary
            && timestamp - boundary.Timestamp > EvidenceLifetime)
            lastBoundary = null;
        if (lastKnightObservation is { } knight
            && timestamp - knight.Timestamp > EvidenceLifetime)
            lastKnightObservation = null;
    }

    private VisualRoomDirection? EligibleDepartureDirection(
        double timestamp,
        bool requiresBoundary,
        LowResolutionRoomMotionEstimate? screenMotion)
    {
        if (screenMotion is { } motion)
        {
            if (!requiresBoundary) return motion.Direction;
            if (lastBoundary is { } motionBoundary
                && timestamp - motionBoundary.Timestamp <= EvidenceLifetime
                && motionBoundary.Direction == motion.Direction)
            {
                return motion.Direction;
            }
        }
        if (lastIntent is not { } intent
            || timestamp - intent.Timestamp > EvidenceLifetime) return null;
        if (!requiresBoundary) return intent.Direction;
        if (lastBoundary is not { } boundary
            || timestamp - boundary.Timestamp > EvidenceLifetime
            || intent.Direction != boundary.Direction) return null;
        return intent.Direction;
    }

    private void ActivateRoom(
        PendingTransition transition,
        RoomAppearance arrivalAppearance)
    {
        var measuredArrival = ContinuousArrivalWorldPose(transition);
        var continuousArrival = VisualRoomPortalLayout.PlausibleContinuousArrival(
            measuredArrival,
            transition.Direction,
            transition.DepartureWorldPose,
            transition.SolveWidth,
            transition.SolveHeight);
        var outgoingKey = new EdgeKey(
            transition.SourceRoomID, transition.Direction);
        PortalTarget target;
        if (MatchingPortal(
            transition.SourceRoomID,
            transition.Direction,
            arrivalAppearance,
            transition.DepartureWorldPose,
            transition.SolveWidth) is { } known)
        {
            target = known;
        }
        else
        {
            if (HasPortal(transition.SourceRoomID, arrivalAppearance)) return;
            var acceptsFadeGeometry = transition.Kind == PendingKind.Fade;
            if ((!transition.HasScreenMotionEvidence && !acceptsFadeGeometry)
                || !transition.SawOppositeArrivalEdge
                || transition.SourceEdgeBand is not { } sourceBand
                || transition.ArrivalEdgeBand is not { } arrivalBand
                || continuousArrival is null && !acceptsFadeGeometry)
            {
                if (roomAppearances.TryGetValue(
                    activeRoomID, out var rememberedSourceAppearance))
                {
                    activeAppearance = rememberedSourceAppearance;
                }
                return;
            }
            var newRoomID = nextRoomID++;
            var portalID = nextPortalID++;
            var layout = VisualRoomPortalLayout.Make(
                transition.Direction,
                transition.DepartureWorldPose,
                transition.SolveWidth,
                transition.SolveHeight,
                sourceBand,
                arrivalBand,
                MeasuredTransitionTravel(),
                continuousArrival);
            var directionIsConsistent = transition.Direction == VisualRoomDirection.Left
                ? layout.TargetDoorWorldX <= layout.SourceDoorWorldX
                : layout.TargetDoorWorldX >= layout.SourceDoorWorldX;
            if (!directionIsConsistent) return;
            var connection = new PortalConnection(
                portalID,
                transition.Direction == VisualRoomDirection.Left
                    ? newRoomID
                    : transition.SourceRoomID,
                transition.Direction == VisualRoomDirection.Left
                    ? transition.SourceRoomID
                    : newRoomID,
                transition.Direction == VisualRoomDirection.Left
                    ? layout.TargetDoorWorldX
                    : layout.SourceDoorWorldX,
                transition.Direction == VisualRoomDirection.Left
                    ? layout.SourceDoorWorldX
                    : layout.TargetDoorWorldX,
                transition.Direction == VisualRoomDirection.Left
                    ? layout.TargetDoorYRange
                    : layout.SourceDoorYRange,
                transition.Direction == VisualRoomDirection.Left
                    ? layout.SourceDoorYRange
                    : layout.TargetDoorYRange);
            portalConnections[portalID] = connection;
            target = new PortalTarget(
                newRoomID,
                layout.TargetEntryWorldPose,
                transition.DepartureWorldPose,
                portalID);
            portals[outgoingKey] = target;
            portals[new EdgeKey(newRoomID, transition.Direction.Opposite())] =
                new PortalTarget(
                    transition.SourceRoomID,
                    transition.DepartureWorldPose,
                    layout.TargetEntryWorldPose,
                    portalID);
        }
        activeRoomID = target.RoomID;
        activeEntryWorldPose = target.EntryWorldPose;
        activationWorldPose = continuousArrival ?? target.EntryWorldPose;
        var rememberedAppearance = roomAppearances.GetValueOrDefault(
            target.RoomID, arrivalAppearance);
        roomAppearances[target.RoomID] = rememberedAppearance;
        activeAppearance = rememberedAppearance;
        revision = unchecked(revision + 1);
    }

    private AtlasPoint? ContinuousArrivalWorldPose(PendingTransition transition)
    {
        if (transition.Kind == PendingKind.Fade
            && transition.ArrivalKnightCenter is { } arrival)
        {
            var fallbackSourceX = transition.Direction == VisualRoomDirection.Right
                ? transition.SolveWidth * FadeFallbackDepartureFraction
                : transition.SolveWidth * (1 - FadeFallbackDepartureFraction);
            if (transition.SourceKnightObservation is { } source)
            {
                var sourceX = transition.Direction == VisualRoomDirection.Right
                    ? Math.Max(source.ScreenCenter.X, fallbackSourceX)
                    : Math.Min(source.ScreenCenter.X, fallbackSourceX);
                return new AtlasPoint(
                    transition.DepartureWorldPose.X + sourceX - arrival.X,
                    transition.DepartureWorldPose.Y
                        + source.ScreenCenter.Y - arrival.Y);
            }
            return new AtlasPoint(
                transition.DepartureWorldPose.X + fallbackSourceX - arrival.X,
                transition.DepartureWorldPose.Y);
        }
        if (floorlessOdometry.SampleCount > 0 && floorlessWorldPose is { } floorless)
            return floorless;
        if (transitionOdometry.SampleCount == 0) return null;
        return new AtlasPoint(
            transition.DepartureWorldPose.X + transitionOdometry.WorldDeltaX,
            transition.DepartureWorldPose.Y);
    }

    private LowResolutionTransitionTravel? MeasuredTransitionTravel() =>
        new[] { transitionOdometry.Travel, floorlessOdometry.Travel }
            .Where(value => value.SampleCount >= 2 && value.MeanConfidence >= .12)
            .OrderByDescending(value => Math.Abs(value.WorldDeltaX))
            .FirstOrDefault();

    private PortalTarget? MatchingPortal(
        int sourceRoomID,
        VisualRoomDirection preferredDirection,
        RoomAppearance arrivalAppearance,
        AtlasPoint departureWorldPose,
        double solveWidth)
    {
        if (!portals.TryGetValue(
            new EdgeKey(sourceRoomID, preferredDirection), out var exact)) return null;
        return PortalIsNearby(exact, departureWorldPose, solveWidth)
            && (!roomAppearances.TryGetValue(exact.RoomID, out var remembered)
                || remembered == arrivalAppearance)
            ? exact
            : null;
    }

    private bool AppearanceTransitionIsSpatiallyEligible(
        int sourceRoomID,
        VisualRoomDirection direction,
        RoomAppearance arrivalAppearance,
        AtlasPoint departureWorldPose,
        double solveWidth)
    {
        var sourceHasKnownPortal = portals.Keys.Any(key => key.RoomID == sourceRoomID);
        var matching = portals
            .Where(pair => pair.Key.RoomID == sourceRoomID
                && roomAppearances.TryGetValue(pair.Value.RoomID, out var appearance)
                && appearance == arrivalAppearance)
            .Select(pair => pair.Value)
            .ToArray();
        if (!portals.TryGetValue(new EdgeKey(sourceRoomID, direction), out var directed))
            return !sourceHasKnownPortal;
        if (matching.Length != 0
            && !matching.Any(value => value.PortalID == directed.PortalID)) return false;
        return PortalIsNearby(directed, departureWorldPose, solveWidth);
    }

    private bool HasPortal(int sourceRoomID, RoomAppearance appearance) =>
        portals.Any(pair => pair.Key.RoomID == sourceRoomID
            && roomAppearances.TryGetValue(pair.Value.RoomID, out var targetAppearance)
            && targetAppearance == appearance);

    private static bool PortalIsNearby(
        PortalTarget portal,
        AtlasPoint departureWorldPose,
        double solveWidth) =>
        Math.Abs(portal.DepartureWorldPose.X - departureWorldPose.X)
            <= Math.Max(48, solveWidth * PortalReentryToleranceInScreens);

    private VisualRoomCompositionBounds CompositionBoundsLocked(int roomID)
    {
        double? minimum = null;
        double? maximum = null;
        foreach (var portal in portalConnections.Values)
        {
            if (portal.LeftRoomID == roomID)
                maximum = maximum is { } oldMaximum
                    ? Math.Min(oldMaximum, portal.LeftDoorWorldX)
                    : portal.LeftDoorWorldX;
            if (portal.RightRoomID == roomID)
                minimum = minimum is { } oldMinimum
                    ? Math.Max(oldMinimum, portal.RightDoorWorldX)
                    : portal.RightDoorWorldX;
        }
        return new VisualRoomCompositionBounds(minimum, maximum);
    }

    private void ClearTransientState(bool resetPlaceMemory)
    {
        lastIntent = null;
        lastBoundary = null;
        lastKnightObservation = null;
        pending = null;
        motionTracker.Reset();
        transitionOdometry.Reset();
        if (resetPlaceMemory) placeMemory.Reset();
        ResetFloorlessTraversal();
        previousMotionSample = null;
        previousEdgeBlackBands = null;
        latestCoarseMotionGrid = null;
        latestCoarseMotionEstimate = null;
        latestCoarsePlaceMatch = null;
        activationWorldPose = null;
    }

    private static void ValidatePersistedState(PersistedState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion
            || state.ActiveRoomID < 0
            || state.NextRoomID <= state.ActiveRoomID
            || state.NextPortalID <= 0
            || !double.IsFinite(state.ActiveEntryX)
            || !double.IsFinite(state.ActiveEntryY)
            || state.Appearances is null || state.Edges is null || state.Connections is null
            || state.ActiveAppearance is not null
                && !TryParseAppearance(state.ActiveAppearance, out _)
            || state.Appearances.Any(value => value.RoomID < 0
                || !TryParseAppearance(value.Value, out _))
            || state.Edges.Any(edge => edge.SourceRoomID < 0 || edge.TargetRoomID < 0
                || edge.Direction is not (-1 or 1)
                || !double.IsFinite(edge.EntryX) || !double.IsFinite(edge.EntryY)
                || !double.IsFinite(edge.DepartureX)
                || !double.IsFinite(edge.DepartureY))
            || state.Connections.Any(connection => connection.ID <= 0
                || connection.LeftRoomID < 0 || connection.RightRoomID < 0
                || !double.IsFinite(connection.LeftDoorWorldX)
                || !double.IsFinite(connection.RightDoorWorldX)
                || connection.LeftDoorWorldX > connection.RightDoorWorldX
                || !double.IsFinite(connection.LeftMinimumWorldY)
                || !double.IsFinite(connection.LeftMaximumWorldY)
                || connection.LeftMaximumWorldY <= connection.LeftMinimumWorldY
                || !double.IsFinite(connection.RightMinimumWorldY)
                || !double.IsFinite(connection.RightMaximumWorldY)
                || connection.RightMaximumWorldY <= connection.RightMinimumWorldY))
        {
            throw new InvalidDataException("Visual room topology is corrupt or unsupported.");
        }
    }

    private static void ValidatePersistedShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Visual room topology must be an object.");
        Require(root, "schemaVersion", JsonValueKind.Number);
        Require(root, "activeRoomID", JsonValueKind.Number);
        Require(root, "activeEntryX", JsonValueKind.Number);
        Require(root, "activeEntryY", JsonValueKind.Number);
        Require(root, "revision", JsonValueKind.Number);
        Require(root, "nextRoomID", JsonValueKind.Number);
        Require(root, "nextPortalID", JsonValueKind.Number);
        var active = Require(root, "activeAppearance");
        if (active.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("activeAppearance has an invalid type.");
        var appearances = Require(root, "appearances", JsonValueKind.Array);
        foreach (var value in appearances.EnumerateArray())
        {
            Require(value, "roomID", JsonValueKind.Number);
            Require(value, "value", JsonValueKind.String);
        }
        var edges = Require(root, "edges", JsonValueKind.Array);
        foreach (var value in edges.EnumerateArray())
        {
            Require(value, "sourceRoomID", JsonValueKind.Number);
            Require(value, "direction", JsonValueKind.Number);
            Require(value, "targetRoomID", JsonValueKind.Number);
            Require(value, "entryX", JsonValueKind.Number);
            Require(value, "entryY", JsonValueKind.Number);
            Require(value, "departureX", JsonValueKind.Number);
            Require(value, "departureY", JsonValueKind.Number);
            Require(value, "portalID", JsonValueKind.Number);
        }
        var connections = Require(root, "connections", JsonValueKind.Array);
        foreach (var value in connections.EnumerateArray())
        {
            Require(value, "id", JsonValueKind.Number);
            Require(value, "leftRoomID", JsonValueKind.Number);
            Require(value, "rightRoomID", JsonValueKind.Number);
            Require(value, "leftDoorWorldX", JsonValueKind.Number);
            Require(value, "rightDoorWorldX", JsonValueKind.Number);
            Require(value, "leftMinimumWorldY", JsonValueKind.Number);
            Require(value, "leftMaximumWorldY", JsonValueKind.Number);
            Require(value, "rightMinimumWorldY", JsonValueKind.Number);
            Require(value, "rightMaximumWorldY", JsonValueKind.Number);
        }
    }

    private static JsonElement Require(
        JsonElement value,
        string name,
        JsonValueKind? kind = null)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(name, out var property)
            || kind is { } expected && property.ValueKind != expected)
        {
            throw new InvalidDataException($"Visual room topology is missing {name}.");
        }
        return property;
    }

    private static Dictionary<TKey, TValue> UniqueDictionary<TSource, TKey, TValue>(
        IEnumerable<TSource> source,
        Func<TSource, TKey> key,
        Func<TSource, TValue> value,
        string label) where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var item in source)
        {
            if (!result.TryAdd(key(item), value(item)))
                throw new InvalidDataException($"Visual room topology has duplicate {label} values.");
        }
        return result;
    }

    private static bool TryParseAppearance(string? value, out RoomAppearance appearance)
    {
        switch (value)
        {
            case "normal": appearance = RoomAppearance.Normal; return true;
            case "dark": appearance = RoomAppearance.Dark; return true;
            default: appearance = default; return false;
        }
    }

    private static RoomAppearance ParseAppearance(string? value) =>
        TryParseAppearance(value, out var appearance)
            ? appearance
            : throw new InvalidDataException("Visual room appearance is invalid.");

    private static string AppearanceWireValue(RoomAppearance appearance) =>
        appearance == RoomAppearance.Normal ? "normal" : "dark";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private enum RoomAppearance { Normal, Dark }
    private enum PendingKind { Blackout, Appearance, Fade }

    private readonly record struct EdgeKey(int RoomID, VisualRoomDirection Direction);
    private sealed record PortalTarget(
        int RoomID,
        AtlasPoint EntryWorldPose,
        AtlasPoint DepartureWorldPose,
        int PortalID);
    private sealed record PortalConnection(
        int ID,
        int LeftRoomID,
        int RightRoomID,
        double LeftDoorWorldX,
        double RightDoorWorldX,
        FrameFractionRange LeftDoorYRange,
        FrameFractionRange RightDoorYRange);
    private sealed record TimedDirection(VisualRoomDirection Direction, double Timestamp);
    private sealed record TimedKnightObservation(
        AtlasPoint ScreenCenter,
        AtlasPoint WorldPose,
        double Timestamp);
    private sealed record MotionSample(LowResolutionMotionGrid Grid, double Timestamp);

    private sealed class PendingTransition(
        int sourceRoomID,
        VisualRoomDirection direction,
        AtlasPoint departureWorldPose,
        double solveWidth,
        double solveHeight,
        PendingKind kind,
        RoomAppearance? targetAppearance,
        FrameEdgeBlackBand? sourceEdgeBand,
        FrameEdgeBlackBand? arrivalEdgeBand,
        TimedKnightObservation? sourceKnightObservation,
        AtlasPoint? arrivalKnightCenter,
        bool hasScreenMotionEvidence,
        int consecutiveTargetFrames,
        bool sawOppositeArrivalEdge,
        int oppositeDirectionFrames,
        int totalFrames)
    {
        internal int SourceRoomID { get; } = sourceRoomID;
        internal VisualRoomDirection Direction { get; } = direction;
        internal AtlasPoint DepartureWorldPose { get; } = departureWorldPose;
        internal double SolveWidth { get; } = solveWidth;
        internal double SolveHeight { get; } = solveHeight;
        internal PendingKind Kind { get; } = kind;
        internal RoomAppearance? TargetAppearance { get; } = targetAppearance;
        internal FrameEdgeBlackBand? SourceEdgeBand { get; } = sourceEdgeBand;
        internal FrameEdgeBlackBand? ArrivalEdgeBand { get; set; } = arrivalEdgeBand;
        internal TimedKnightObservation? SourceKnightObservation { get; } = sourceKnightObservation;
        internal AtlasPoint? ArrivalKnightCenter { get; set; } = arrivalKnightCenter;
        internal bool HasScreenMotionEvidence { get; set; } = hasScreenMotionEvidence;
        internal int ConsecutiveTargetFrames { get; set; } = consecutiveTargetFrames;
        internal bool SawOppositeArrivalEdge { get; set; } = sawOppositeArrivalEdge;
        internal int OppositeDirectionFrames { get; set; } = oppositeDirectionFrames;
        internal int TotalFrames { get; set; } = totalFrames;
    }

    private sealed class PersistedState
    {
        public int SchemaVersion { get; set; }
        public int ActiveRoomID { get; set; }
        public double ActiveEntryX { get; set; }
        public double ActiveEntryY { get; set; }
        public ulong Revision { get; set; }
        public int NextRoomID { get; set; }
        public int NextPortalID { get; set; }
        public string? ActiveAppearance { get; set; }
        public PersistedAppearance[]? Appearances { get; set; }
        public PersistedEdge[]? Edges { get; set; }
        public PersistedConnection[]? Connections { get; set; }
    }

    private sealed class PersistedAppearance
    {
        public int RoomID { get; set; }
        public string? Value { get; set; }
    }

    private sealed class PersistedEdge
    {
        public int SourceRoomID { get; set; }
        public int Direction { get; set; }
        public int TargetRoomID { get; set; }
        public double EntryX { get; set; }
        public double EntryY { get; set; }
        public double DepartureX { get; set; }
        public double DepartureY { get; set; }
        public int PortalID { get; set; }
    }

    private sealed class PersistedConnection
    {
        public int ID { get; set; }
        public int LeftRoomID { get; set; }
        public int RightRoomID { get; set; }
        public double LeftDoorWorldX { get; set; }
        public double RightDoorWorldX { get; set; }
        public double LeftMinimumWorldY { get; set; }
        public double LeftMaximumWorldY { get; set; }
        public double RightMinimumWorldY { get; set; }
        public double RightMaximumWorldY { get; set; }
    }
}
