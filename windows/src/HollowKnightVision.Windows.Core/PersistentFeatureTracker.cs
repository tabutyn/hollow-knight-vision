namespace HollowKnightVision.Windows.Core;

public static class LiveRegistrationAdmission
{
    public static bool RequiresRecovery(CameraUpdate? update) =>
        update?.State != CameraUpdateState.Accepted;

    public static bool CanWriteAtlas(
        CameraUpdate? update,
        WorldBasisTrackingState worldState) =>
        update?.State == CameraUpdateState.Accepted
        && worldState == WorldBasisTrackingState.Tracking;
}

public static class WorldPlacement
{
    public static AtlasPoint Offset(
        AtlasPoint cameraPosition,
        AtlasPoint anchorPosition,
        double presentationWidth,
        double solveWidth,
        double gain)
    {
        var scale = presentationWidth / Math.Max(1, solveWidth);
        return new AtlasPoint(
            (cameraPosition.X - anchorPosition.X) * scale * gain,
            (cameraPosition.Y - anchorPosition.Y) * scale * gain);
    }
}

public enum MapFeatureKind
{
    Candidate,
    Landmark,
    Relocalized,
}

public sealed record MapFeature(
    int ID,
    AtlasPoint Point,
    double Confidence,
    double Depth,
    double DepthConfidence,
    MapFeatureKind Kind);

public sealed record WorldMapFeature(
    int ID,
    AtlasPoint WorldPoint,
    AtlasPoint RegistrationCameraPosition,
    double Depth,
    double Confidence);

public sealed record FeatureTrackingResult(
    IReadOnlyList<MapFeature> Features,
    IReadOnlyList<WorldMapFeature> WorldFeatures,
    AtlasPoint CameraPosition,
    int LandmarkCount,
    int RelocalizedCount,
    CameraVector CameraPoseCorrection,
    int CameraPoseCorrectionSupport)
{
    public bool HasCameraPoseConsensus =>
        CameraPoseCorrectionSupport >= 3
        && double.IsFinite(CameraPosition.X)
        && double.IsFinite(CameraPosition.Y)
        && double.IsFinite(CameraPoseCorrection.X)
        && double.IsFinite(CameraPoseCorrection.Y);

    public bool DidCorrectCameraPose =>
        HasCameraPoseConsensus
        && Length(CameraPoseCorrection.X, CameraPoseCorrection.Y) >= .05;

    public static FeatureTrackingResult Empty { get; } = new(
        [],
        [],
        default,
        0,
        0,
        CameraVector.Zero,
        0);

    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
}

/// <summary>
/// Maintains durable image landmarks. Candidates must remain static relative
/// to solved camera motion before entering the atlas. Landmarks survive normal
/// viewport exits and can correct backtracking drift through depth-normalized
/// consensus.
/// </summary>
public sealed class PersistentFeatureTracker
{
    public const int TargetFeatureCount = 40;

    private const int SampleWidth = 240;
    private const int PatchRadius = 4;
    private const int ActiveSearchRadius = 4;
    private const int RelocalizationSearchRadius = 12;
    private const int MaximumClosureMatches = 20;
    private const int MaximumClosureAttempts = 48;
    private const double MinimumCornerStrength = 900;
    private const double MinimumSpacing = 12;
    private const int MaturationFrames = 4;
    private const double MinimumDepthConfidence = .05;
    private const double MaximumDepthRefinementConfidence = .75;
    private const double MaximumDescriptorError = .46;
    private const double MaximumMotionResidual = 3.2;

    private readonly Dictionary<int, ActiveFeature> active = [];
    private readonly Dictionary<int, Landmark> landmarks = [];
    private readonly List<AtlasPoint> rejectedWorldPoints = [];
    private int nextID = 1;
    private int frameIndex;
    private AtlasPoint? previousCameraPosition;

    public void Reset()
    {
        active.Clear();
        landmarks.Clear();
        rejectedWorldPoints.Clear();
        nextID = 1;
        frameIndex = 0;
        previousCameraPosition = null;
    }

    public void ResetTracking(bool keepingLandmarks)
    {
        active.Clear();
        previousCameraPosition = null;
        if (keepingLandmarks) return;
        landmarks.Clear();
        rejectedWorldPoints.Clear();
        nextID = 1;
    }

    public FeatureTrackingResult Update(
        BgraFrame frame,
        AtlasPoint proposedCameraPosition,
        double solveWidth,
        IReadOnlyList<StencilRect> excluding)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(excluding);
        var sample = MakeSample(frame);
        if (sample is null || solveWidth <= 0)
        {
            return new FeatureTrackingResult(
                [],
                landmarks.Values.Select(WorldMapFeature).ToArray(),
                proposedCameraPosition,
                landmarks.Count,
                0,
                CameraVector.Zero,
                0);
        }

        frameIndex++;
        var cameraStep = previousCameraPosition is { } prior
            ? new CameraVector(
                proposedCameraPosition.X - prior.X,
                proposedCameraPosition.Y - prior.Y)
            : CameraVector.Zero;
        var solveToSample = (double)sample.Width / solveWidth;
        var activeIDs = active.Keys.ToHashSet();
        var matches = new List<FeatureMatch>();
        var failedIDs = new List<int>();

        foreach (var feature in active.Values.ToArray())
        {
            var expected = SamplePoint(
                feature.WorldPoint,
                proposedCameraPosition,
                solveToSample,
                sample.Height,
                feature.Depth);
            if (!IsInside(expected, sample, PatchRadius + 1))
            {
                active.Remove(feature.ID);
                continue;
            }
            if (IsExcluded(expected, sample, frame, excluding))
            {
                active.Remove(feature.ID);
                continue;
            }
            var match = BestMatch(
                feature.ReferenceDescriptor,
                expected,
                ActiveSearchRadius,
                sample);
            if (match is null)
            {
                failedIDs.Add(feature.ID);
                continue;
            }
            matches.Add(new FeatureMatch(
                feature.ID,
                match.Point,
                expected,
                match.Descriptor,
                match.Error,
                feature.Depth,
                feature.IsPersistent
                    && feature.DepthConfidence >= MinimumDepthConfidence,
                false));
        }

        var dormantLandmarks = landmarks.Values
            .Where(value =>
                !activeIDs.Contains(value.ID)
                && value.DepthConfidence >= MinimumDepthConfidence)
            .OrderByDescending(value => value.ObservationCount)
            .ThenByDescending(value => value.Strength)
            .ToArray();
        var liveFeatureMatches = matches.ToArray();
        var closureAttempts = 0;
        var closureMatches = 0;
        foreach (var landmark in dormantLandmarks)
        {
            if (closureMatches >= MaximumClosureMatches
                || closureAttempts >= MaximumClosureAttempts)
            {
                break;
            }
            var expected = SamplePoint(
                landmark.WorldPoint,
                proposedCameraPosition,
                solveToSample,
                sample.Height,
                landmark.Depth);
            if (!IsInside(expected, sample, RelocalizationSearchRadius + 1)
                || IsExcluded(expected, sample, frame, excluding))
            {
                continue;
            }
            closureAttempts++;
            var match = WorldMatch(landmark, expected, liveFeatureMatches, sample);
            if (match is null
                || matches.Any(value =>
                    value.IsRelocalized
                    && Distance(value.Point, match.Point) < MinimumSpacing / 2))
            {
                continue;
            }
            matches.Add(new FeatureMatch(
                landmark.ID,
                match.Point,
                expected,
                match.Descriptor,
                match.Error,
                landmark.Depth,
                true,
                true));
            closureMatches++;
        }

        var currentMatches = matches.Where(value => !value.IsRelocalized).ToArray();
        var loopClosureMatches = matches.Where(value => value.IsRelocalized).ToArray();
        var allMotion = ConsensusResidual(currentMatches, persistentOnly: false);
        var loopClosureMotion = ConsensusResidual(
            loopClosureMatches,
            persistentOnly: true,
            normalizeForDepth: true);
        var globalMotion = loopClosureMotion
            ?? ConsensusResidual(matches, persistentOnly: true, normalizeForDepth: true);
        var cameraPoseCorrection = CameraCorrection(globalMotion?.Residual, solveToSample);
        var correctedCameraPosition = new AtlasPoint(
            proposedCameraPosition.X + cameraPoseCorrection.X,
            proposedCameraPosition.Y + cameraPoseCorrection.Y);
        var cameraPoseCorrectionSupport = globalMotion?.Support ?? 0;

        foreach (var id in failedIDs)
        {
            if (!active.TryGetValue(id, out var feature)) continue;
            feature.FailureCount++;
            if (feature.FailureCount >= FailureLimit(feature.IsPersistent))
                RejectFeature(id);
        }

        var relocalizedCount = 0;
        foreach (var match in matches)
        {
            var residual = new CameraVector(
                match.Point.X - match.ExpectedPoint.X,
                match.Point.Y - match.ExpectedPoint.Y);
            var motionReference = (match.IsRelocalized ? loopClosureMotion : allMotion)?.Residual;
            var residualForConsensus = match.IsRelocalized && Math.Abs(match.Depth) >= .25
                ? new CameraVector(residual.X / match.Depth, residual.Y / match.Depth)
                : residual;
            var motionDeviation = motionReference is { } reference
                ? Length(
                    residualForConsensus.X - reference.X,
                    residualForConsensus.Y - reference.Y)
                : 0;
            var stable = match.Error <= MaximumDescriptorError
                && motionDeviation <= MaximumMotionResidual
                && (!match.IsRelocalized || loopClosureMotion is not null);

            if (active.TryGetValue(match.ID, out var feature))
            {
                if (stable)
                {
                    var refinedDepth = loopClosureMotion is null
                        && feature.DepthConfidence < MaximumDepthRefinementConfidence
                        && UpdateDepth(
                            feature,
                            match.Point,
                            cameraStep,
                            solveToSample);
                    feature.Point = match.Point;
                    feature.WorldPoint = BlendedWorldPoint(
                        feature.WorldPoint,
                        match.Point,
                        correctedCameraPosition,
                        solveToSample,
                        sample.Height,
                        feature.Depth,
                        feature.IsPersistent ? (refinedDepth ? 1 : 0) : .18);
                    feature.LastDescriptor = match.Descriptor;
                    feature.Age++;
                    feature.StableFrames++;
                    feature.FailureCount = 0;
                    feature.Confidence = Math.Max(
                        0,
                        1 - match.Error / MaximumDescriptorError);
                    feature.JustRelocalized = false;
                    if (feature.IsPersistent
                        && landmarks.TryGetValue(match.ID, out var landmark))
                    {
                        landmark.WorldPoint = feature.WorldPoint;
                        landmark.Depth = feature.Depth;
                        landmark.DepthConfidence = feature.DepthConfidence;
                        if (refinedDepth)
                            landmark.RegistrationCameraPosition = correctedCameraPosition;
                    }
                }
                else
                {
                    feature.FailureCount++;
                    if (feature.FailureCount >= FailureLimit(feature.IsPersistent))
                        RejectFeature(match.ID);
                }
            }
            else if (match.IsRelocalized
                && landmarks.TryGetValue(match.ID, out var landmark)
                && stable
                && active.Count < TargetFeatureCount
                && !active.Values.Any(value =>
                    Distance(value.Point, match.Point) < MinimumSpacing))
            {
                active[match.ID] = new ActiveFeature(
                    landmark.ID,
                    match.Point,
                    landmark.WorldPoint,
                    landmark.Descriptor,
                    match.Descriptor,
                    landmark.Strength,
                    Math.Max(0, 1 - match.Error / MaximumDescriptorError),
                    landmark.Depth,
                    landmark.DepthConfidence,
                    landmark.ObservationCount,
                    MaturationFrames,
                    0,
                    true,
                    true);
            }

            if (match.IsRelocalized && stable) relocalizedCount++;
            if (match.IsPersistent
                && landmarks.TryGetValue(match.ID, out var persistentLandmark))
            {
                persistentLandmark.LastSeenFrame = frameIndex;
                persistentLandmark.ObservationCount++;
            }
        }

        MatureCandidates(correctedCameraPosition, solveToSample, sample.Height);
        ReplenishFeatures(
            sample,
            correctedCameraPosition,
            solveToSample,
            frame,
            excluding);
        TrimAtlasIfNeeded();
        previousCameraPosition = correctedCameraPosition;

        var output = active.Values
            .OrderBy(value => value.ID)
            .Take(TargetFeatureCount)
            .Select(feature => new MapFeature(
                feature.ID,
                FramePoint(feature.Point, sample, frame),
                feature.Confidence,
                feature.Depth,
                feature.DepthConfidence,
                feature.JustRelocalized
                    ? MapFeatureKind.Relocalized
                    : feature.IsPersistent
                        ? MapFeatureKind.Landmark
                        : MapFeatureKind.Candidate))
            .ToArray();
        return new FeatureTrackingResult(
            output,
            landmarks.Values.OrderBy(value => value.ID).Select(WorldMapFeature).ToArray(),
            correctedCameraPosition,
            landmarks.Count,
            relocalizedCount,
            cameraPoseCorrection,
            cameraPoseCorrectionSupport);
    }

    private void MatureCandidates(
        AtlasPoint cameraPosition,
        double solveToSample,
        int sampleHeight)
    {
        foreach (var id in active.Keys.ToArray())
        {
            var feature = active[id];
            if (feature.IsPersistent || feature.StableFrames < MaturationFrames) continue;
            feature.IsPersistent = true;
            feature.WorldPoint = WorldPoint(
                feature.Point,
                cameraPosition,
                solveToSample,
                sampleHeight,
                feature.Depth);
            landmarks[id] = new Landmark(
                id,
                feature.WorldPoint,
                feature.ReferenceDescriptor,
                feature.Strength,
                feature.Depth,
                feature.DepthConfidence,
                cameraPosition,
                feature.Age,
                frameIndex);
        }
    }

    private void ReplenishFeatures(
        Sample sample,
        AtlasPoint cameraPosition,
        double solveToSample,
        BgraFrame frame,
        IReadOnlyList<StencilRect> excludedRects)
    {
        if (active.Count >= TargetFeatureCount) return;
        var needed = TargetFeatureCount - active.Count;
        var existingPoints = active.Values.Select(value => value.Point).ToArray();
        var candidates = StrongCorners(sample);
        var accepted = new List<AtlasPoint>();
        foreach (var candidate in candidates)
        {
            var candidateWorldPoint = WorldPoint(
                candidate.Point,
                cameraPosition,
                solveToSample,
                sample.Height,
                1);
            if (accepted.Count >= needed) break;
            if (IsExcluded(candidate.Point, sample, frame, excludedRects)
                || rejectedWorldPoints.Any(value =>
                    Distance(value, candidateWorldPoint) < 18)
                || existingPoints.Any(value =>
                    Distance(value, candidate.Point) < MinimumSpacing)
                || accepted.Any(value =>
                    Distance(value, candidate.Point) < MinimumSpacing))
            {
                continue;
            }
            var descriptor = Descriptor(candidate.Point, sample);
            if (descriptor is null) continue;
            var id = nextID++;
            active[id] = new ActiveFeature(
                id,
                candidate.Point,
                candidateWorldPoint,
                descriptor,
                descriptor,
                candidate.Strength,
                .35,
                1,
                0,
                1,
                1,
                0,
                false,
                false);
            accepted.Add(candidate.Point);
        }
    }

    private static CameraVector CameraCorrection(
        CameraVector? residual,
        double solveToSample)
    {
        if (residual is not { } value || solveToSample <= 0)
            return CameraVector.Zero;
        var x = -value.X / solveToSample;
        var y = value.Y / solveToSample;
        var magnitude = Length(x, y);
        if (magnitude > 28)
        {
            x *= 28 / magnitude;
            y *= 28 / magnitude;
        }
        return new CameraVector(x, y);
    }

    private static ResidualConsensus? ConsensusResidual(
        IEnumerable<FeatureMatch> source,
        bool persistentOnly,
        bool normalizeForDepth = false)
    {
        var eligible = source.Where(value =>
                (!persistentOnly || value.IsPersistent)
                && value.Error <= MaximumDescriptorError
                && (!normalizeForDepth || Math.Abs(value.Depth) >= .25))
            .ToArray();
        var minimum = persistentOnly ? 3 : 5;
        if (eligible.Length < minimum) return null;
        var residuals = eligible.Select(value =>
        {
            var divisor = normalizeForDepth ? value.Depth : 1;
            return new CameraVector(
                (value.Point.X - value.ExpectedPoint.X) / divisor,
                (value.Point.Y - value.ExpectedPoint.Y) / divisor);
        }).ToArray();
        var initial = new CameraVector(
            Median(residuals.Select(value => value.X)),
            Median(residuals.Select(value => value.Y)));
        var inliers = residuals.Where(value =>
            Length(value.X - initial.X, value.Y - initial.Y)
                <= MaximumMotionResidual).ToArray();
        if (inliers.Length < minimum) return null;
        return new ResidualConsensus(
            new CameraVector(
                Median(inliers.Select(value => value.X)),
                Median(inliers.Select(value => value.Y))),
            inliers.Length);
    }

    private static DescriptorMatch? BestMatch(
        float[] reference,
        AtlasPoint expected,
        int radius,
        Sample sample,
        double maximumError = MaximumDescriptorError)
    {
        var centerX = RoundInt(expected.X);
        var centerY = RoundInt(expected.Y);
        AtlasPoint? bestPoint = null;
        var bestError = double.MaxValue;
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var point = new AtlasPoint(centerX + dx, centerY + dy);
            var error = DescriptorError(reference, point, sample);
            if (error is not { } value || value >= bestError) continue;
            bestPoint = point;
            bestError = value;
        }
        if (bestPoint is not { } winner || bestError > maximumError) return null;
        var descriptor = Descriptor(winner, sample);
        return descriptor is null
            ? null
            : new DescriptorMatch(winner, descriptor, bestError);
    }

    private static DescriptorMatch? WorldMatch(
        Landmark landmark,
        AtlasPoint expected,
        IReadOnlyList<FeatureMatch> liveMatches,
        Sample sample)
    {
        DescriptorMatch? currentMatch = null;
        foreach (var liveMatch in liveMatches)
        {
            if (Distance(liveMatch.Point, expected) > RelocalizationSearchRadius) continue;
            var error = DescriptorDistance(landmark.Descriptor, liveMatch.Descriptor);
            if (error >= (currentMatch?.Error ?? double.MaxValue)) continue;
            currentMatch = new DescriptorMatch(
                liveMatch.Point,
                liveMatch.Descriptor,
                error);
        }
        if (currentMatch is { Error: <= .40 }) return currentMatch;
        return BestMatch(
            landmark.Descriptor,
            expected,
            RelocalizationSearchRadius,
            sample,
            .40);
    }

    private static IReadOnlyList<Corner> StrongCorners(Sample sample)
    {
        var margin = PatchRadius + 2;
        if (sample.Width <= margin * 2 || sample.Height <= margin * 2) return [];
        var corners = new List<Corner>();
        for (var y = margin; y < sample.Height - margin; y++)
        for (var x = margin; x < sample.Width - margin; x++)
        {
            var strength = CornerStrength(x, y, sample);
            if (strength >= MinimumCornerStrength)
                corners.Add(new Corner(new AtlasPoint(x, y), strength));
        }
        return corners.OrderByDescending(value => value.Strength).ToArray();
    }

    private static double CornerStrength(int x, int y, Sample sample)
    {
        double xx = 0;
        double xy = 0;
        double yy = 0;
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        for (var offsetX = -1; offsetX <= 1; offsetX++)
        {
            var gx = sample[x + offsetX + 1, y + offsetY]
                - sample[x + offsetX - 1, y + offsetY];
            var gy = sample[x + offsetX, y + offsetY + 1]
                - sample[x + offsetX, y + offsetY - 1];
            xx += gx * gx;
            xy += gx * gy;
            yy += gy * gy;
        }
        return Math.Max(
            0,
            (xx + yy - Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy)) / 2);
    }

    private static float[]? Descriptor(AtlasPoint point, Sample sample)
    {
        var x = RoundInt(point.X);
        var y = RoundInt(point.Y);
        var margin = PatchRadius + 1;
        if (x < margin || x >= sample.Width - margin
            || y < margin || y >= sample.Height - margin)
        {
            return null;
        }
        var values = new float[(PatchRadius * 2 + 1) * (PatchRadius * 2 + 1) * 2];
        float energy = 0;
        var index = 0;
        for (var offsetY = -PatchRadius; offsetY <= PatchRadius; offsetY++)
        for (var offsetX = -PatchRadius; offsetX <= PatchRadius; offsetX++)
        {
            var gx = (float)(sample[x + offsetX + 1, y + offsetY]
                - sample[x + offsetX - 1, y + offsetY]);
            var gy = (float)(sample[x + offsetX, y + offsetY + 1]
                - sample[x + offsetX, y + offsetY - 1]);
            values[index++] = gx;
            values[index++] = gy;
            energy += gx * gx + gy * gy;
        }
        if (energy < 400) return null;
        var inverseNorm = 1 / MathF.Sqrt(energy);
        for (var value = 0; value < values.Length; value++)
            values[value] *= inverseNorm;
        return values;
    }

    private static double DescriptorDistance(float[] first, float[] second)
    {
        if (first.Length != second.Length) return double.MaxValue;
        float squared = 0;
        for (var index = 0; index < first.Length; index++)
        {
            var difference = first[index] - second[index];
            squared += difference * difference;
        }
        return MathF.Sqrt(squared);
    }

    private static double? DescriptorError(
        float[] reference,
        AtlasPoint point,
        Sample sample)
    {
        var x = RoundInt(point.X);
        var y = RoundInt(point.Y);
        var margin = PatchRadius + 1;
        var requiredCount = (PatchRadius * 2 + 1) * (PatchRadius * 2 + 1) * 2;
        if (x < margin || x >= sample.Width - margin
            || y < margin || y >= sample.Height - margin
            || reference.Length != requiredCount)
        {
            return null;
        }
        float energy = 0;
        for (var offsetY = -PatchRadius; offsetY <= PatchRadius; offsetY++)
        for (var offsetX = -PatchRadius; offsetX <= PatchRadius; offsetX++)
        {
            var gx = (float)(sample[x + offsetX + 1, y + offsetY]
                - sample[x + offsetX - 1, y + offsetY]);
            var gy = (float)(sample[x + offsetX, y + offsetY + 1]
                - sample[x + offsetX, y + offsetY - 1]);
            energy += gx * gx + gy * gy;
        }
        if (energy < 400) return null;
        var inverseNorm = 1 / MathF.Sqrt(energy);
        float squared = 0;
        var index = 0;
        for (var offsetY = -PatchRadius; offsetY <= PatchRadius; offsetY++)
        for (var offsetX = -PatchRadius; offsetX <= PatchRadius; offsetX++)
        {
            var gx = (sample[x + offsetX + 1, y + offsetY]
                - sample[x + offsetX - 1, y + offsetY]) * inverseNorm;
            var gy = (sample[x + offsetX, y + offsetY + 1]
                - sample[x + offsetX, y + offsetY - 1]) * inverseNorm;
            var xDifference = reference[index] - gx;
            var yDifference = reference[index + 1] - gy;
            squared += xDifference * xDifference + yDifference * yDifference;
            index += 2;
        }
        return MathF.Sqrt(squared);
    }

    private static AtlasPoint BlendedWorldPoint(
        AtlasPoint old,
        AtlasPoint samplePoint,
        AtlasPoint cameraPosition,
        double scale,
        int sampleHeight,
        double depth,
        double alpha)
    {
        if (alpha <= 0) return old;
        var measured = WorldPoint(
            samplePoint,
            cameraPosition,
            scale,
            sampleHeight,
            depth);
        return new AtlasPoint(
            old.X * (1 - alpha) + measured.X * alpha,
            old.Y * (1 - alpha) + measured.Y * alpha);
    }

    private static AtlasPoint WorldPoint(
        AtlasPoint samplePoint,
        AtlasPoint cameraPosition,
        double scale,
        int sampleHeight,
        double depth) => new(
            cameraPosition.X * depth + samplePoint.X / scale,
            cameraPosition.Y * depth + (sampleHeight - samplePoint.Y) / scale);

    private static AtlasPoint SamplePoint(
        AtlasPoint worldPoint,
        AtlasPoint cameraPosition,
        double scale,
        int sampleHeight,
        double depth) => new(
            (worldPoint.X - cameraPosition.X * depth) * scale,
            sampleHeight - (worldPoint.Y - cameraPosition.Y * depth) * scale);

    private static AtlasPoint FramePoint(
        AtlasPoint point,
        Sample sample,
        BgraFrame frame) => new(
            point.X * frame.Width / sample.Width,
            frame.Height - point.Y * frame.Height / sample.Height);

    private static bool IsExcluded(
        AtlasPoint point,
        Sample sample,
        BgraFrame frame,
        IReadOnlyList<StencilRect> rects)
    {
        var pointInFrame = FramePoint(point, sample, frame);
        return rects.Any(rect =>
        {
            var expanded = rect.Expand(4);
            return pointInFrame.X >= expanded.X
                && pointInFrame.X < expanded.Right
                && pointInFrame.Y >= expanded.Y
                && pointInFrame.Y < expanded.Bottom;
        });
    }

    private static bool IsInside(AtlasPoint point, Sample sample, int margin) =>
        point.X >= margin
        && point.X < sample.Width - margin
        && point.Y >= margin
        && point.Y < sample.Height - margin;

    private static double Median(IEnumerable<double> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0) return 0;
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static double Distance(AtlasPoint first, AtlasPoint second) =>
        Length(first.X - second.X, first.Y - second.Y);

    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);

    private static int FailureLimit(bool isPersistent) => isPersistent ? 6 : 2;

    private static bool UpdateDepth(
        ActiveFeature feature,
        AtlasPoint matchedPoint,
        CameraVector cameraStep,
        double solveToSample)
    {
        var expectedMotion = new CameraVector(
            -cameraStep.X * solveToSample,
            cameraStep.Y * solveToSample);
        var magnitudeSquared = expectedMotion.X * expectedMotion.X
            + expectedMotion.Y * expectedMotion.Y;
        if (magnitudeSquared < .75 * .75) return false;
        var observed = new CameraVector(
            matchedPoint.X - feature.Point.X,
            matchedPoint.Y - feature.Point.Y);
        var magnitude = Math.Sqrt(magnitudeSquared);
        var measuredDepth = (observed.X * expectedMotion.X
            + observed.Y * expectedMotion.Y) / magnitudeSquared;
        var perpendicular = Math.Abs(
            observed.X * expectedMotion.Y - observed.Y * expectedMotion.X)
            / magnitude;
        if (measuredDepth < -.25 || measuredDepth > 3.5 || perpendicular > 2.5)
            return false;
        var evidence = Math.Min(1, magnitude / 3)
            * Math.Max(0, 1 - perpendicular / 2.5);
        var alpha = feature.DepthConfidence == 0
            ? 1
            : Math.Max(.08, Math.Min(.32, evidence * .28));
        feature.Depth = feature.Depth * (1 - alpha) + measuredDepth * alpha;
        feature.DepthConfidence = Math.Min(
            1,
            feature.DepthConfidence + evidence * .16);
        return true;
    }

    private static WorldMapFeature WorldMapFeature(Landmark landmark) => new(
        landmark.ID,
        landmark.WorldPoint,
        landmark.RegistrationCameraPosition,
        landmark.Depth,
        landmark.DepthConfidence);

    private void TrimAtlasIfNeeded()
    {
        if (landmarks.Count <= 1_200) return;
        var obsolete = landmarks.Values
            .OrderBy(value => value.LastSeenFrame)
            .Take(landmarks.Count - 1_200)
            .Select(value => value.ID)
            .ToArray();
        foreach (var id in obsolete)
            if (!active.ContainsKey(id)) landmarks.Remove(id);
    }

    private void RejectFeature(int id)
    {
        AtlasPoint? worldPoint = active.TryGetValue(id, out var feature)
            ? feature.WorldPoint
            : landmarks.TryGetValue(id, out var landmark)
                ? landmark.WorldPoint
                : null;
        if (worldPoint is { } rejected)
        {
            rejectedWorldPoints.Add(rejected);
            if (rejectedWorldPoints.Count > 1_000)
            {
                rejectedWorldPoints.RemoveRange(
                    0,
                    rejectedWorldPoints.Count - 1_000);
            }
        }
        active.Remove(id);
        landmarks.Remove(id);
    }

    private static Sample? MakeSample(BgraFrame frame)
    {
        var height = Math.Max(
            1,
            checked((int)Math.Round(
                (double)frame.Height * SampleWidth / Math.Max(1, frame.Width),
                MidpointRounding.AwayFromZero)));
        var pixels = new byte[checked(SampleWidth * height)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < SampleWidth; x++)
        {
            // Match CoreGraphics' low-quality scaled draw into a device-gray
            // bitmap. Its sample locations lie on the destination pixel grid,
            // rather than at pixel centers.
            var sourceX = Math.Clamp(
                (double)x * frame.Width / SampleWidth,
                0,
                frame.Width - 1);
            var sourceY = Math.Clamp(
                (double)y * frame.Height / height,
                0,
                frame.Height - 1);
            var x0 = (int)Math.Floor(sourceX);
            var y0 = (int)Math.Floor(sourceY);
            var x1 = Math.Min(frame.Width - 1, x0 + 1);
            var y1 = Math.Min(frame.Height - 1, y0 + 1);
            var fractionX = sourceX - x0;
            var fractionY = sourceY - y0;
            var topRow = frame.Row(y0);
            var bottomRow = frame.Row(y1);
            var topOffset0 = x0 * BgraFrame.BytesPerPixel;
            var topOffset1 = x1 * BgraFrame.BytesPerPixel;

            static double Interpolate(
                ReadOnlySpan<byte> top,
                ReadOnlySpan<byte> bottom,
                int offset0,
                int offset1,
                int component,
                double fractionX,
                double fractionY)
            {
                var topValue = top[offset0 + component]
                    + (top[offset1 + component] - top[offset0 + component]) * fractionX;
                var bottomValue = bottom[offset0 + component]
                    + (bottom[offset1 + component] - bottom[offset0 + component]) * fractionX;
                return topValue + (bottomValue - topValue) * fractionY;
            }

            var blue = Interpolate(
                topRow, bottomRow, topOffset0, topOffset1, 0, fractionX, fractionY);
            var green = Interpolate(
                topRow, bottomRow, topOffset0, topOffset1, 1, fractionX, fractionY);
            var red = Interpolate(
                topRow, bottomRow, topOffset0, topOffset1, 2, fractionX, fractionY);
            pixels[y * SampleWidth + x] = (byte)Math.Clamp(
                Math.Round(
                    (54 * red + 183 * green + 19 * blue) / 256,
                    MidpointRounding.AwayFromZero),
                byte.MinValue,
                byte.MaxValue);
        }
        return new Sample(pixels, SampleWidth, height);
    }

    private static int RoundInt(double value) => checked((int)Math.Round(
        value,
        MidpointRounding.AwayFromZero));

    private sealed record Sample(byte[] Pixels, int Width, int Height)
    {
        public byte this[int x, int y] => Pixels[y * Width + x];
    }

    private sealed class ActiveFeature(
        int id,
        AtlasPoint point,
        AtlasPoint worldPoint,
        float[] referenceDescriptor,
        float[] lastDescriptor,
        double strength,
        double confidence,
        double depth,
        double depthConfidence,
        int age,
        int stableFrames,
        int failureCount,
        bool isPersistent,
        bool justRelocalized)
    {
        public int ID { get; } = id;
        public AtlasPoint Point { get; set; } = point;
        public AtlasPoint WorldPoint { get; set; } = worldPoint;
        public float[] ReferenceDescriptor { get; } = referenceDescriptor;
        public float[] LastDescriptor { get; set; } = lastDescriptor;
        public double Strength { get; } = strength;
        public double Confidence { get; set; } = confidence;
        public double Depth { get; set; } = depth;
        public double DepthConfidence { get; set; } = depthConfidence;
        public int Age { get; set; } = age;
        public int StableFrames { get; set; } = stableFrames;
        public int FailureCount { get; set; } = failureCount;
        public bool IsPersistent { get; set; } = isPersistent;
        public bool JustRelocalized { get; set; } = justRelocalized;
    }

    private sealed class Landmark(
        int id,
        AtlasPoint worldPoint,
        float[] descriptor,
        double strength,
        double depth,
        double depthConfidence,
        AtlasPoint registrationCameraPosition,
        int observationCount,
        int lastSeenFrame)
    {
        public int ID { get; } = id;
        public AtlasPoint WorldPoint { get; set; } = worldPoint;
        public float[] Descriptor { get; } = descriptor;
        public double Strength { get; } = strength;
        public double Depth { get; set; } = depth;
        public double DepthConfidence { get; set; } = depthConfidence;
        public AtlasPoint RegistrationCameraPosition { get; set; } = registrationCameraPosition;
        public int ObservationCount { get; set; } = observationCount;
        public int LastSeenFrame { get; set; } = lastSeenFrame;
    }

    private sealed record FeatureMatch(
        int ID,
        AtlasPoint Point,
        AtlasPoint ExpectedPoint,
        float[] Descriptor,
        double Error,
        double Depth,
        bool IsPersistent,
        bool IsRelocalized);

    private sealed record ResidualConsensus(CameraVector Residual, int Support);
    private sealed record DescriptorMatch(AtlasPoint Point, float[] Descriptor, double Error);
    private sealed record Corner(AtlasPoint Point, double Strength);
}
