namespace HollowKnightVision.Windows.Core;

public enum GroundHypothesisClassification
{
    Candidate,
    GroundPlane,
    GlobalMatch,
    Occluded,
    DepthError,
}

public sealed record GroundHypothesisFeature(
    int Id,
    int SegmentId,
    int SequenceIndex,
    PixelRect ImageRect,
    GroundHypothesisClassification Classification,
    double? MotionResidual,
    double? PhotometricError);

public sealed record GroundHypothesisAtlasFeature(
    int SegmentId,
    int SequenceIndex,
    AtlasPoint AtlasPosition,
    IReadOnlyList<byte> ReferencePixels,
    IReadOnlyList<byte> ReferenceOpacity);

public sealed record GroundHypothesisTrackingResult(
    IReadOnlyList<GroundHypothesisFeature> Features,
    CameraVector? CameraTranslation,
    int InlierCount,
    double? ResidualRms,
    int GlobalFeatureCount,
    int GlobalMatchCount,
    AtlasPoint? GlobalCameraPosition,
    CameraVector? GlobalCorrection,
    double? VerticalLineCorrection,
    int GroundSegmentCount,
    IReadOnlyList<GroundHypothesisAtlasFeature> AtlasFeatures,
    IReadOnlyList<GroundHypothesisAtlasLine> AtlasLines,
    IReadOnlyList<GroundLinePresenceReview> LineReviews,
    AtlasPoint? CameraPosition,
    bool PoseVerified,
    bool HasConfirmedGround,
    int LocalTextureSupport,
    double? LocalTextureError,
    bool ProvisionalContinuityActive)
{
    public static GroundHypothesisTrackingResult Empty { get; } = new(
        [], null, 0, null, 0, 0, null, null, null, 0, [], [], [], null, false, false,
        0, null, false);
}

public readonly record struct GroundFeatureMotion(
    int Id,
    CameraVector ImageTranslation,
    double PhotometricError);

public sealed record GroundTranslationFit(
    CameraVector ImageTranslation,
    IReadOnlySet<int> InlierIds,
    double ResidualRms);

/// <summary>
/// Deterministic translation-only RANSAC. Every observed motion is a seed;
/// the largest consensus is refined with photometric-error weighted means.
/// </summary>
public static class GroundTranslationRansac
{
    public const double InlierRadius = 2.5;

    public static GroundTranslationFit? Fit(
        IReadOnlyList<GroundFeatureMotion> motions,
        int minimumInliers = 3)
    {
        ArgumentNullException.ThrowIfNull(motions);
        if (minimumInliers <= 0) throw new ArgumentOutOfRangeException(nameof(minimumInliers));
        if (motions.Count < minimumInliers) return null;

        GroundFeatureMotion[] best = [];
        var bestSeedError = double.PositiveInfinity;
        foreach (var seed in motions)
        {
            var inliers = motions.Where(motion =>
                    Distance(motion.ImageTranslation, seed.ImageTranslation) <= InlierRadius)
                .ToArray();
            var error = inliers.Sum(motion =>
                Distance(motion.ImageTranslation, seed.ImageTranslation));
            if (inliers.Length > best.Length
                || (inliers.Length == best.Length && error < bestSeedError))
            {
                best = inliers;
                bestSeedError = error;
            }
        }

        if (best.Length < minimumInliers) return null;
        var translation = WeightedMean(best);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var refined = motions.Where(motion =>
                    Distance(motion.ImageTranslation, translation) <= InlierRadius)
                .ToArray();
            if (refined.Length < minimumInliers) break;
            best = refined;
            translation = WeightedMean(refined);
        }

        var squaredError = best.Sum(motion =>
        {
            var error = Distance(motion.ImageTranslation, translation);
            return error * error;
        });
        return new GroundTranslationFit(
            translation,
            best.Select(motion => motion.Id).ToHashSet(),
            Math.Sqrt(squaredError / best.Length));
    }

    private static CameraVector WeightedMean(IReadOnlyList<GroundFeatureMotion> motions)
    {
        var sumX = 0d;
        var sumY = 0d;
        var sumWeight = 0d;
        foreach (var motion in motions)
        {
            var weight = 1 / (1 + Math.Max(0, motion.PhotometricError) / 20);
            sumX += motion.ImageTranslation.X * weight;
            sumY += motion.ImageTranslation.Y * weight;
            sumWeight += weight;
        }
        return new CameraVector(sumX / Math.Max(double.Epsilon, sumWeight),
            sumY / Math.Max(double.Epsilon, sumWeight));
    }

    private static double Distance(CameraVector left, CameraVector right) =>
        Math.Sqrt((left.X - right.X) * (left.X - right.X)
            + (left.Y - right.Y) * (left.Y - right.Y));
}

public sealed record GroundHypothesisOverlay(int Width, int Height, byte[] Rgba);

/// <summary>
/// Persistent local ground-cell tracker. Image geometry is top-origin; atlas
/// geometry is bottom-origin. Cells remain on one world-wide 16-pixel phase.
/// </summary>
public sealed class GroundHypothesisTracker
{
    public const int FeatureWidth = 16;
    public const int FeatureHeight = 12;
    public const int FeatureSize = FeatureWidth;
    public const int FeaturePixelCount = FeatureWidth * FeatureHeight;

    private const int HorizontalSearchRadius = 4;
    private const int VerticalSearchRadius = 3;
    private const int RecoveryHorizontalSearchRadius = 16;
    private const int RecoveryVerticalSearchRadius = 24;
    private const int RecoveryFeatureLimit = 48;
    private const double MaximumPhotometricError = 64;
    private const int VerticalRefinementRadius = 3;
    private const int EvidenceRowTolerance = 5;
    private const double SegmentAssociationRowTolerance = 6;
    private const double PromotionPhotometricError = 28;
    private const double DescriptorMatchThreshold = 18;
    private const int MissingLineRejectionFrames = 5;

    private sealed class ActiveFeature
    {
        public required int Id { get; init; }
        public required int SegmentId { get; set; }
        public required int SequenceIndex { get; set; }
        public required int X { get; set; }
        public required int Y { get; set; }
        public required byte[] Patch { get; set; }
        public required int Age { get; set; }
        public required GroundHypothesisClassification Classification { get; set; }
        public double? MotionResidual { get; set; }
        public double? PhotometricError { get; set; }
        public int ConsecutiveGroundFrames { get; set; }
        public int? GlobalFeatureId { get; set; }
        public bool ReferenceValid { get; set; }
    }

    private sealed class CellEvidence
    {
        public byte[] ReferencePatch { get; set; } = [];
        public byte[] ReferenceOpacity { get; set; } = [];
    }

    private sealed class Segment
    {
        public required int Id { get; init; }
        public required double OriginWorldX { get; set; }
        public required double MinimumWorldX { get; set; }
        public required double MaximumWorldX { get; set; }
        public required double WorldTopY { get; set; }
        public Dictionary<int, int> CellIds { get; } = [];
        public Dictionary<int, CellEvidence> CellEvidence { get; } = [];
    }

    private sealed class GlobalFeature
    {
        public required int Id { get; init; }
        public required int SegmentId { get; set; }
        public required int SequenceIndex { get; set; }
        public required double WorldX { get; set; }
        public required double WorldTopY { get; set; }
        public required short[] Descriptor { get; init; }
        public required byte[] MatchingPatch { get; init; }
        public required byte[] ReferencePatch { get; set; }
        public required byte[] ReferenceOpacity { get; set; }
        public int ObservationCount { get; set; }
        public int LastSeenFrame { get; set; }

        public GlobalFeature Copy() => new()
        {
            Id = Id,
            SegmentId = SegmentId,
            SequenceIndex = SequenceIndex,
            WorldX = WorldX,
            WorldTopY = WorldTopY,
            Descriptor = Descriptor.ToArray(),
            MatchingPatch = MatchingPatch.ToArray(),
            ReferencePatch = ReferencePatch.ToArray(),
            ReferenceOpacity = ReferenceOpacity.ToArray(),
            ObservationCount = ObservationCount,
            LastSeenFrame = LastSeenFrame,
        };
    }

    private sealed record GlobalObservation(
        int FeatureId,
        int CurrentSegmentId,
        int GlobalId,
        int GlobalSegmentId,
        double GlobalWorldX,
        double ImageX,
        AtlasPoint CameraPosition,
        double DescriptorError);

    private sealed record GlobalFit(
        AtlasPoint CameraPosition,
        IReadOnlySet<int> FeatureIds,
        IReadOnlySet<int> GlobalIds,
        IReadOnlyList<GlobalObservation> Inliers,
        IReadOnlySet<int>? VerificationFeatureIds = null);

    private sealed record BackgroundSearchResult(
        GlobalFit? Fit,
        double Timestamp,
        AtlasPoint LocalPose);

    private sealed record Match(
        ActiveFeature Feature,
        int X,
        int Y,
        double PhotometricError,
        CameraVector ImageTranslation,
        int DetectedLineRow);

    private sealed record RecoveryAttempt(
        IReadOnlyList<Match> Matches,
        GroundTranslationFit? Fit);

    private sealed record ObservedLine(
        int Row,
        PixelSpan Span,
        IReadOnlyList<PixelSpan> EvidenceSpans);

    private sealed record PendingLinePatch(double WorldX, byte[] Pixels);

    private sealed class LineMotionCandidate
    {
        public required double MinimumWorldX { get; set; }
        public required double MaximumWorldX { get; set; }
        public required double WorldTopY { get; init; }
        public required int LastSeenFrame { get; set; }
        public required AtlasPoint ReferenceCamera { get; set; }
        public required IReadOnlyList<PendingLinePatch> ReferencePatches { get; set; }
        public int SuccessfulMotionChecks { get; set; }
        public int FailedMotionChecks { get; set; }
        public double AccumulatedCameraTravel { get; set; }
        public bool Verified { get; set; }
    }

    private readonly GroundCameraSolver cameraSolver;
    private readonly GroundLinePresence linePresence;
    private readonly bool backgroundGlobalSearch;
    private readonly Action? beforeBackgroundGlobalSearch;
    private readonly Action<AtlasPoint?>? backgroundGlobalSearchCompleted;
    private readonly BoundedBackgroundSearch<BackgroundSearchResult> globalSearchWorker = new();
    private readonly List<Segment> segments = [];
    private readonly Dictionary<int, GlobalFeature> globalFeatures = [];
    private readonly Dictionary<int, GlobalFeature> retiredGlobalFeatures = [];
    private readonly Dictionary<int, GlobalFeature> canonicalAnchorFeatures = [];
    private readonly HashSet<int> canonicalFirstViewIds = [];
    private readonly Dictionary<int, AtlasPoint> canonicalViewOrigins = [];
    private readonly HashSet<int> closedCanonicalViews = [];
    private readonly List<LineMotionCandidate> lineMotionCandidates = [];
    private List<ActiveFeature> active = [];
    private IReadOnlyList<CleanFloorLine> latestStabilizedFloorLines = [];
    private int nextFeatureId = 1;
    private int nextSegmentId = 1;
    private int nextGlobalId = 1;
    private int frameIndex;
    private int frameWidth;
    private int frameHeight;
    private double cameraX;
    private double cameraY;
    private double? globalTilePhaseX;
    private double? lastTimestamp;
    private double? lastProvisionalTextureTimestamp;
    private double? provisionalPromotionStartedAt;
    private bool provisionalPromotionAllowed;
    private int consecutiveStrongProvisionalMatches;
    private double? lastGlobalSearchTimestamp;
    private double? lastCanonicalPhaseSearchTimestamp;
    private double? bootstrapFailureStartedAt;
    private double lastLocalFailureTimestamp = double.NegativeInfinity;
    private bool globalAnchorValid = true;
    private GroundCorrectionConfirmation correctionConfirmation;
    private CameraVector lastImageTranslation;

    public GroundHypothesisTracker(
        double minimumObservationSeconds = 2,
        bool backgroundGlobalSearch = false,
        double confirmedLossGraceSeconds = 0,
        bool productionCameraSolver = false)
        : this(minimumObservationSeconds, backgroundGlobalSearch,
            confirmedLossGraceSeconds, beforeBackgroundGlobalSearch: null,
            backgroundGlobalSearchCompleted: null,
            productionCameraSolver: productionCameraSolver)
    {
    }

    internal GroundHypothesisTracker(
        double minimumObservationSeconds,
        bool backgroundGlobalSearch,
        double confirmedLossGraceSeconds,
        Action? beforeBackgroundGlobalSearch,
        Action<AtlasPoint?>? backgroundGlobalSearchCompleted)
        : this(
            minimumObservationSeconds,
            backgroundGlobalSearch,
            confirmedLossGraceSeconds,
            beforeBackgroundGlobalSearch,
            backgroundGlobalSearchCompleted,
            productionCameraSolver: false)
    {
    }

    internal GroundHypothesisTracker(
        double minimumObservationSeconds,
        bool backgroundGlobalSearch,
        double confirmedLossGraceSeconds,
        Action? beforeBackgroundGlobalSearch,
        Action<AtlasPoint?>? backgroundGlobalSearchCompleted,
        bool productionCameraSolver)
    {
        this.backgroundGlobalSearch = backgroundGlobalSearch;
        this.beforeBackgroundGlobalSearch = beforeBackgroundGlobalSearch;
        this.backgroundGlobalSearchCompleted = backgroundGlobalSearchCompleted;
        cameraSolver = productionCameraSolver
            ? new GroundCameraSolver(
                keyframeSubpixelEnabled: true,
                calibratedMotionEnabled: true,
                matcherBackend: GroundComputeBackend.Automatic,
                maximumLocalSearchRadius: 192)
            : new GroundCameraSolver();
        linePresence = new GroundLinePresence(
            minimumObservationSeconds,
            confirmedLossGraceSeconds);
    }

    public GroundPatchSearchExecution? LatestPatchSearchExecution =>
        cameraSolver.LastPatchSearchExecution;

    public IReadOnlyList<CleanFloorLine> LatestStabilizedFloorLines =>
        latestStabilizedFloorLines;

    public void Reset(bool keepingGlobalFeatures = false)
    {
        cameraSolver.Reset();
        globalSearchWorker.Invalidate();
        correctionConfirmation.Reset();
        bootstrapFailureStartedAt = null;
        lastLocalFailureTimestamp = double.NegativeInfinity;
        lastGlobalSearchTimestamp = null;
        lastCanonicalPhaseSearchTimestamp = null;
        if (!keepingGlobalFeatures) linePresence.Reset();
        if (!keepingGlobalFeatures) segments.Clear();
        active = [];
        lineMotionCandidates.Clear();
        latestStabilizedFloorLines = [];
        frameWidth = 0;
        frameHeight = 0;
        lastTimestamp = null;
        lastProvisionalTextureTimestamp = null;
        provisionalPromotionStartedAt = null;
        provisionalPromotionAllowed = false;
        consecutiveStrongProvisionalMatches = 0;
        lastImageTranslation = default;
        if (!keepingGlobalFeatures)
        {
            globalFeatures.Clear();
            retiredGlobalFeatures.Clear();
            canonicalAnchorFeatures.Clear();
            canonicalFirstViewIds.Clear();
            canonicalViewOrigins.Clear();
            closedCanonicalViews.Clear();
            nextFeatureId = 1;
            nextSegmentId = 1;
            nextGlobalId = 1;
            frameIndex = 0;
            cameraX = 0;
            cameraY = 0;
            globalTilePhaseX = null;
            globalAnchorValid = true;
        }
        else
        {
            globalAnchorValid = RelocalizationFeatures().Length == 0;
            if (RelocalizationFeatures().Length == 0)
            {
                cameraX = 0;
                cameraY = 0;
            }
        }
    }

    /// <summary>
    /// Reconciles the private local epoch with a camera pose published while
    /// ground tracking was absent. Persistent place search deliberately keeps
    /// running; repeated floorless handoffs must not starve confirmation.
    /// </summary>
    public void ReanchorLocalCamera(
        AtlasPoint position,
        byte[]? luma = null,
        int width = 0,
        int height = 0,
        IReadOnlyList<DetectedFloorLine>? floorLines = null,
        double? timestamp = null,
        IReadOnlyList<PixelRect>? protectedOcclusions = null,
        bool allowsProvisionalPromotion = false)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        var correction = new CameraVector(position.X - cameraX, position.Y - cameraY);
        cameraX = position.X;
        cameraY = position.Y;
        bootstrapFailureStartedAt = null;
        lastImageTranslation = default;
        active = [];
        lineMotionCandidates.Clear();
        globalAnchorValid = true;
        if (luma is not null
            && width >= FeatureWidth
            && height >= FeatureHeight
            && luma.LongLength == (long)width * height
            && timestamp is { } now
            && floorLines is { Count: > 0 } lines)
        {
            cameraSolver.FollowMeasuredCamera(
                correction,
                luma,
                width,
                height,
                lines.Select(GroundTextureLine.FromDetected).ToArray(),
                now,
                (protectedOcclusions ?? []).Select(rect =>
                    new StencilRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray(),
                preservesTrustedHistory: segments.Count > 0 || RelocalizationFeatures().Length > 0);
            if (cameraSolver.HasProvisionalReference)
            {
                lastProvisionalTextureTimestamp = now;
                provisionalPromotionStartedAt = now;
                provisionalPromotionAllowed = allowsProvisionalPromotion;
                consecutiveStrongProvisionalMatches = 0;
            }
        }
        else
        {
            lastGlobalSearchTimestamp = null;
            lastCanonicalPhaseSearchTimestamp = null;
            cameraSolver.Reset();
            lastProvisionalTextureTimestamp = null;
            provisionalPromotionStartedAt = null;
            provisionalPromotionAllowed = false;
            consecutiveStrongProvisionalMatches = 0;
        }
    }

    public GroundHypothesisTrackingResult Update(
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        int lineSeparation = 40,
        int occlusionMergeGap = 300,
        double? timestamp = null,
        IReadOnlyList<PixelRect>? protectedOcclusions = null,
        Func<GroundPlacementProposal, bool>? placementValidator = null)
    {
        ArgumentNullException.ThrowIfNull(luma);
        ArgumentNullException.ThrowIfNull(floorLines);
        if (width < FeatureWidth || height < FeatureHeight
            || luma.LongLength != (long)width * height)
        {
            Reset(keepingGlobalFeatures: true);
            return GroundHypothesisTrackingResult.Empty;
        }
        if (frameWidth != 0 && (frameWidth != width || frameHeight != height)) Reset();
        frameWidth = width;
        frameHeight = height;
        frameIndex++;
        var exclusions = (protectedOcclusions ?? []).ToArray();
        var now = timestamp ?? ((lastTimestamp ?? (-1d / 60)) + 1d / 60);
        lastTimestamp = now;

        var textureLines = floorLines.Select(GroundTextureLine.FromDetected).ToArray();
        var stencilExclusions = exclusions.Select(rect =>
            new StencilRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray();
        var solution = cameraSolver.Solve(
            luma, width, height, textureLines, now, stencilExclusions);
        if (solution is null
            && segments.Count == 0
            && RelocalizationFeatures().Length == 0
            && active.Count == 0
            && floorLines.Count > 0)
        {
            if (bootstrapFailureStartedAt is { } began
                && now - began >= 0.25)
            {
                cameraSolver.Reset();
                linePresence.Reset();
                lineMotionCandidates.Clear();
                latestStabilizedFloorLines = [];
                lastImageTranslation = default;
                solution = cameraSolver.Solve(
                    luma, width, height, textureLines, now, stencilExclusions);
                bootstrapFailureStartedAt = null;
            }
            else if (bootstrapFailureStartedAt is null)
            {
                bootstrapFailureStartedAt = now;
            }
        }
        else
        {
            bootstrapFailureStartedAt = null;
        }
        if (solution is null) lastLocalFailureTimestamp = now;
        if (solution is not null)
        {
            lastImageTranslation = solution.ImageTranslation;
            if (!solution.ReferenceTrusted)
            {
                lastProvisionalTextureTimestamp = now;
                consecutiveStrongProvisionalMatches = provisionalPromotionAllowed
                    && solution.MatchSource == "provisional"
                    && GroundReferencePromotionPolicy.Permits(solution.Support, solution.Error)
                    ? consecutiveStrongProvisionalMatches + 1 : 0;
            }
        }
        var previousActive = active;
        var recoveryAttempt = solution is null
            ? RecoveryConsensus(
                RecoveryFeatureCandidates(previousActive),
                luma, width, height, floorLines, exclusions)
            : null;
        var matches = recoveryAttempt?.Matches.ToArray() ?? previousActive
            .Where(feature => feature.ReferenceValid
                && feature.Patch.Length == FeaturePixelCount
                && DescriptorEnergy(NormalizedDescriptor(feature.Patch)) >= 6)
            .Select(feature => BestMatch(feature, luma, width, height, floorLines))
            .Where(match => match is not null
                && match.PhotometricError <= MaximumPhotometricError
                && !exclusions.Any(rect => Intersects(
                    match.X, match.Y, FeatureWidth, FeatureHeight, rect)))
            .Cast<Match>()
            .ToArray();
        GroundTranslationFit? cameraFit = null;
        if (solution is not null && !cameraSolver.Seeded)
        {
            var inliers = matches.Where(match =>
                    Length(
                        match.ImageTranslation.X - solution.ImageTranslation.X,
                        match.ImageTranslation.Y - solution.ImageTranslation.Y)
                    <= GroundTranslationRansac.InlierRadius)
                .ToArray();
            cameraFit = new GroundTranslationFit(
                solution.ImageTranslation,
                inliers.Select(match => match.Feature.Id).ToHashSet(),
                solution.Error);
            cameraX -= cameraFit.ImageTranslation.X;
            cameraY += cameraFit.ImageTranslation.Y;
        }
        else if (solution is null && recoveryAttempt?.Fit is { } recovered)
        {
            var recoveredMatches = matches
                .Where(match => recovered.InlierIds.Contains(match.Feature.Id))
                .ToArray();
            var spread = recoveredMatches.Length == 0
                ? 0
                : recoveredMatches.Max(match => match.X) - recoveredMatches.Min(match => match.X);
            var photometricMean = recoveredMatches.Length == 0
                ? double.PositiveInfinity
                : recoveredMatches.Average(match => match.PhotometricError);
            var acceleration = Length(
                recovered.ImageTranslation.X - lastImageTranslation.X,
                recovered.ImageTranslation.Y - lastImageTranslation.Y);
            if (recovered.ResidualRms <= 1.5
                && spread >= 48
                && photometricMean <= PromotionPhotometricError
                && acceleration <= 24)
            {
                cameraSolver.AcceptFeatureRecovery(
                    luma, width, height, textureLines, now, stencilExclusions,
                    recovered.ImageTranslation);
                lastImageTranslation = recovered.ImageTranslation;
                solution = new GroundCameraSolver.Solution(
                    recovered.ImageTranslation,
                    recovered.InlierIds.Count,
                    photometricMean);
                cameraFit = recovered;
                cameraX -= cameraFit.ImageTranslation.X;
                cameraY += cameraFit.ImageTranslation.Y;
            }
        }

        var stabilized = StabilizeFloorLines(floorLines, matches, cameraFit);
        if (solution is { ReferenceTrusted: false }
            && provisionalPromotionStartedAt is { } started
            && provisionalPromotionAllowed
            && GroundProvisionalPromotionPolicy.Permits(now - started,
                consecutiveStrongProvisionalMatches, solution.Support, solution.Error))
        {
            cameraSolver.PromoteProvisionalReference(luma, width, height,
                stabilized.Select(GroundTextureLine.FromDetected).ToArray(), now, stencilExclusions);
            solution = solution with { ReferenceTrusted = true };
        }

        GlobalFit? globalFit = null;
        CameraVector? globalCorrection = null;
        // Foreground search owns a cadence even before descriptors mature, as
        // on macOS. Otherwise the first mature frame can immediately consume
        // its own just-created bank and bypass outer placement validation.
        if (!backgroundGlobalSearch || RelocalizationFeatures().Length >= 6)
        {
            globalFit = backgroundGlobalSearch
                ? BackgroundFit(luma, width, height, floorLines, exclusions, now)
                : ForegroundFit(luma, width, height, floorLines, exclusions, now);
            if (globalFit is not null)
            {
                var position = new AtlasPoint(
                    SnapInteger(globalFit.CameraPosition.X),
                    SnapInteger(globalFit.CameraPosition.Y));
                globalFit = globalFit with { CameraPosition = position };
                globalCorrection = new CameraVector(position.X - cameraX, position.Y - cameraY);
                globalSearchWorker.Invalidate();
                correctionConfirmation.Reset();
                cameraSolver.ApplyGlobalCorrection(
                    globalCorrection.Value, luma, textureLines, now, stencilExclusions);
                cameraX = position.X;
                cameraY = position.Y;
                globalAnchorValid = true;
                if (solution is null || !solution.ReferenceTrusted)
                {
                    solution = new GroundCameraSolver.Solution(
                        CameraVector.Zero, globalFit.FeatureIds.Count, 0);
                    cameraFit = new GroundTranslationFit(
                        CameraVector.Zero, globalFit.FeatureIds, 0);
                }
            }
        }

        if (solution is null || !solution.ReferenceTrusted || !globalAnchorValid)
        {
            linePresence.Update([], new AtlasPoint(cameraX, cameraY),
                width, height, now, false, exclusions);
            var failedReviews = linePresence.Reviews(
                new AtlasPoint(cameraX, cameraY), width, height);
            RemoveRejectedSegments(ref previousActive, ref matches);
            active = previousActive;
            latestStabilizedFloorLines = [];
            return Result(null, globalFit, globalCorrection, failedReviews, false, solution);
        }

        if (placementValidator?.Invoke(new GroundPlacementProposal(
                new AtlasPoint(cameraX, cameraY),
                solution.Support,
                solution.Error,
                cameraFit?.InlierIds.Count ?? 0,
                globalFit?.FeatureIds.Count ?? 0,
                globalCorrection is not null,
                cameraSolver.DistinctFrame,
                segments.Count > 0 || RelocalizationFeatures().Length > 0)) == false)
        {
            cameraSolver.DeferReferenceUpdates(now);
            active = previousActive;
            linePresence.Update([], new AtlasPoint(cameraX, cameraY),
                width, height, now, false, exclusions);
            var deferredReviews = linePresence.Reviews(
                new AtlasPoint(cameraX, cameraY), width, height);
            return Result(cameraFit, globalFit, globalCorrection,
                deferredReviews, false, solution);
        }

        provisionalPromotionStartedAt = null;
        provisionalPromotionAllowed = false;
        consecutiveStrongProvisionalMatches = 0;
        var tracked = matches.Select(match => UpdatedFeature(match, cameraFit, luma, width))
            .Where(feature => feature is not null)
            .Cast<ActiveFeature>()
            .ToArray();

        var admissionLines = AtlasAdmissionLines(
            PreservingKnownFloorGaps(stabilized),
            luma, width, height, lineSeparation, exclusions);

        linePresence.Update(
            admissionLines.Select(line => new CleanFloorLine(line.Row, line.XSpan)).ToArray(),
            new AtlasPoint(cameraX, cameraY), width, height, now,
            solution.ReferenceTrusted, exclusions);
        var reviews = linePresence.Reviews(new AtlasPoint(cameraX, cameraY), width, height);
        var confirmed = reviews.Where(review => review.State == GroundLinePresenceState.Confirmed)
            .Select(review => new ObservedLine(
                review.Line.Row,
                review.Line.XSpan,
                admissionLines.Where(line => Math.Abs(line.Row - review.Line.Row) <= 4)
                    .SelectMany(line => line.EvidenceSpans)
                    .ToArray()))
            .ToArray();
        latestStabilizedFloorLines = confirmed
            .Select(line => new CleanFloorLine(line.Row, line.Span))
            .ToArray();

        RemoveRejectedSegments(ref previousActive, ref tracked);

        IntegrateSegments(confirmed, occlusionMergeGap, confirmed);
        active = ProjectSegments(confirmed, tracked, previousActive, luma, width, height, exclusions);
        if (globalFit is not null) AttachGlobalMatches(globalFit);
        RecordReferencePatches();
        PromoteVerifiedFeatures();
        return Result(cameraFit, globalFit, globalCorrection, reviews, true, solution);
    }

    public static GroundHypothesisOverlay? OverlayImage(
        GroundHypothesisTrackingResult result,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (width <= 0 || height <= 0) return null;
        var pixels = new byte[checked(width * height * 4)];
        foreach (var feature in result.Features)
        {
            var color = feature.Classification switch
            {
                GroundHypothesisClassification.Candidate => (R: (byte)0, G: (byte)200, B: (byte)255, A: (byte)255),
                GroundHypothesisClassification.GroundPlane => (R: (byte)70, G: (byte)255, B: (byte)120, A: (byte)255),
                GroundHypothesisClassification.GlobalMatch => (R: (byte)190, G: (byte)80, B: (byte)255, A: (byte)255),
                GroundHypothesisClassification.Occluded => (R: (byte)255, G: (byte)128, B: (byte)0, A: (byte)255),
                _ => (R: (byte)255, G: (byte)60, B: (byte)60, A: (byte)255),
            };
            var left = feature.ImageRect.X;
            var top = feature.ImageRect.Y;
            var right = left + FeatureWidth - 1;
            var bottom = top + FeatureHeight - 1;
            for (var x = left; x <= right; x++)
            {
                Paint(pixels, width, height, x, top, color);
                Paint(pixels, width, height, x, bottom, color);
            }
            for (var y = top; y <= bottom; y++)
            {
                Paint(pixels, width, height, left, y, color);
                Paint(pixels, width, height, right, y, color);
            }
        }
        return new GroundHypothesisOverlay(width, height, pixels);
    }

    private GroundHypothesisTrackingResult Result(
        GroundTranslationFit? cameraFit,
        GlobalFit? globalFit,
        CameraVector? globalCorrection,
        IReadOnlyList<GroundLinePresenceReview>? reviews,
        bool poseVerified,
        GroundCameraSolver.Solution? localSolution)
    {
        var outputs = active.Select(feature => new GroundHypothesisFeature(
                feature.Id,
                feature.SegmentId,
                feature.SequenceIndex,
                new PixelRect(feature.X, feature.Y, FeatureWidth, FeatureHeight),
                feature.Classification,
                feature.MotionResidual,
                feature.PhotometricError))
            .OrderBy(feature => feature.ImageRect.Y)
            .ThenBy(feature => feature.ImageRect.X)
            .ToArray();
        var activeIds = outputs.Select(feature => feature.Id).ToHashSet();
        var atlasFeatures = new List<GroundHypothesisAtlasFeature>();
        var atlasLines = new List<GroundHypothesisAtlasLine>();
        foreach (var segment in segments.OrderBy(segment => segment.Id))
        {
            foreach (var pair in segment.CellIds.OrderBy(pair => pair.Key))
            {
                var evidence = segment.CellEvidence[pair.Key];
                var worldX = segment.OriginWorldX + pair.Key * FeatureSize;
                atlasFeatures.Add(new GroundHypothesisAtlasFeature(
                    segment.Id,
                    pair.Key,
                    new AtlasPoint(worldX + FeatureWidth * .5,
                        frameHeight - segment.WorldTopY - FeatureHeight * .5),
                    evidence.ReferencePatch.ToArray(),
                    evidence.ReferenceOpacity.ToArray()));
            }

            foreach (var run in ContiguousRuns(segment.CellIds.Keys.Order()))
            {
                var y = frameHeight - segment.WorldTopY;
                atlasLines.Add(new GroundHypothesisAtlasLine(
                    segment.Id,
                    new AtlasPoint(segment.OriginWorldX + run.Start * FeatureSize, y),
                    new AtlasPoint(segment.OriginWorldX + (run.End + 1) * FeatureSize, y)));
            }
        }

        return new GroundHypothesisTrackingResult(
            outputs,
            cameraFit is null ? null : new CameraVector(
                -cameraFit.ImageTranslation.X,
                cameraFit.ImageTranslation.Y),
            cameraFit?.InlierIds.Count(activeIds.Contains) ?? 0,
            cameraFit?.ResidualRms,
            RelocalizationFeatures().Length,
            globalFit?.GlobalIds.Count ?? 0,
            globalFit?.CameraPosition,
            globalCorrection,
            null,
            segments.Count,
            atlasFeatures,
            atlasLines,
            reviews ?? linePresence.Reviews(new AtlasPoint(cameraX, cameraY), frameWidth, frameHeight),
            new AtlasPoint(cameraX, cameraY),
            poseVerified,
            segments.Count > 0,
            localSolution?.Support ?? 0,
            localSolution?.Error,
            cameraSolver.HasProvisionalReference
                && lastProvisionalTextureTimestamp is { } provisionalTimestamp
                && (lastTimestamp ?? provisionalTimestamp) - provisionalTimestamp <= .35);
    }

    private void RemoveRejectedSegments(
        ref List<ActiveFeature> previous,
        ref Match[] matches)
    {
        var rejected = RejectedSegmentIds();
        if (rejected.Count == 0) return;
        previous = previous.Where(feature => !rejected.Contains(feature.SegmentId)).ToList();
        matches = matches.Where(match => !rejected.Contains(match.Feature.SegmentId)).ToArray();
    }

    private void RemoveRejectedSegments(
        ref List<ActiveFeature> previous,
        ref ActiveFeature[] tracked)
    {
        var rejected = RejectedSegmentIds();
        if (rejected.Count == 0) return;
        previous = previous.Where(feature => !rejected.Contains(feature.SegmentId)).ToList();
        tracked = tracked.Where(feature => !rejected.Contains(feature.SegmentId)).ToArray();
    }

    private HashSet<int> RejectedSegmentIds()
    {
        var rejected = segments.Where(segment =>
                linePresence.State(segment.MinimumWorldX, segment.MaximumWorldX,
                    segment.WorldTopY) == GroundLinePresenceState.Rejected)
            .Select(segment => segment.Id)
            .ToHashSet();
        if (rejected.Count == 0) return rejected;
        segments.RemoveAll(segment => rejected.Contains(segment.Id));
        foreach (var id in globalFeatures.Where(pair => rejected.Contains(pair.Value.SegmentId))
                     .Select(pair => pair.Key).ToArray())
        {
            retiredGlobalFeatures[id] = globalFeatures[id].Copy();
            globalFeatures.Remove(id);
        }
        return rejected;
    }

    private void PromoteVerifiedFeatures()
    {
        if (RelocalizationFeatures().Length > 0 && !globalAnchorValid) return;
        foreach (var pair in canonicalViewOrigins)
        {
            if (Length(cameraX - pair.Value.X, cameraY - pair.Value.Y) > 32)
                closedCanonicalViews.Add(pair.Key);
        }
        foreach (var feature in active)
        {
            if (feature.GlobalFeatureId is not null
                || feature.Classification != GroundHypothesisClassification.GroundPlane
                || feature.Age < 3
                || feature.ConsecutiveGroundFrames < 2
                || feature.PhotometricError is null
                || feature.PhotometricError > PromotionPhotometricError
                || feature.Patch.Length != FeaturePixelCount)
                continue;
            var descriptor = NormalizedDescriptor(feature.Patch);
            if (DescriptorEnergy(descriptor) < 6) continue;
            var segment = segments.FirstOrDefault(item => item.Id == feature.SegmentId);
            if (segment is null
                || !segment.CellIds.TryGetValue(feature.SequenceIndex, out var featureId)
                || featureId != feature.Id)
                continue;
            var worldX = segment.OriginWorldX + feature.SequenceIndex * FeatureSize;
            var worldY = segment.WorldTopY;
            var existing = globalFeatures.Values
                .OrderBy(item => Length(item.WorldX - worldX, item.WorldTopY - worldY))
                .FirstOrDefault();
            if (existing is not null
                && Length(existing.WorldX - worldX, existing.WorldTopY - worldY) <= 4
                && DescriptorDistance(descriptor, existing.Descriptor) <= 12)
            {
                feature.GlobalFeatureId = existing.Id;
                feature.Classification = GroundHypothesisClassification.GlobalMatch;
                existing.ObservationCount++;
                existing.LastSeenFrame = frameIndex;
                continue;
            }
            var evidence = segment.CellEvidence[feature.SequenceIndex];
            var id = nextGlobalId++;
            var promoted = new GlobalFeature
            {
                Id = id,
                SegmentId = segment.Id,
                SequenceIndex = feature.SequenceIndex,
                WorldX = worldX,
                WorldTopY = worldY,
                Descriptor = descriptor,
                MatchingPatch = feature.Patch.ToArray(),
                ReferencePatch = feature.Patch.ToArray(),
                ReferenceOpacity = evidence.ReferenceOpacity.Length == FeaturePixelCount
                    ? evidence.ReferenceOpacity.ToArray()
                    : Enumerable.Repeat((byte)96, FeaturePixelCount).ToArray(),
                ObservationCount = 1,
                LastSeenFrame = frameIndex,
            };
            globalFeatures[id] = promoted;
            if (canonicalAnchorFeatures.Values.Count(item =>
                    item.SegmentId == promoted.SegmentId) < 64)
            {
                canonicalAnchorFeatures[id] = promoted.Copy();
                if (!closedCanonicalViews.Contains(promoted.SegmentId))
                {
                    canonicalViewOrigins.TryAdd(
                        promoted.SegmentId, new AtlasPoint(cameraX, cameraY));
                    canonicalFirstViewIds.Add(id);
                }
            }
            feature.GlobalFeatureId = id;
            feature.Classification = GroundHypothesisClassification.GlobalMatch;
        }
    }

    private void AttachGlobalMatches(GlobalFit fit)
    {
        foreach (var feature in active)
        {
            var global = globalFeatures.Values.FirstOrDefault(item =>
                item.SegmentId == feature.SegmentId
                && item.SequenceIndex == feature.SequenceIndex);
            if (global is null) continue;
            feature.GlobalFeatureId = global.Id;
            if (!fit.GlobalIds.Contains(global.Id)) continue;
            feature.Classification = GroundHypothesisClassification.GlobalMatch;
            global.ObservationCount++;
            global.LastSeenFrame = frameIndex;
        }
    }

    private GlobalFit? ForegroundFit(
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<PixelRect> exclusions,
        double timestamp)
    {
        if (lastGlobalSearchTimestamp is { } prior && timestamp - prior < .25) return null;
        lastGlobalSearchTimestamp = timestamp;
        return SearchGlobalFit(luma, width, height, floorLines, exclusions,
            ShouldScanCanonical(timestamp));
    }

    private GlobalFit? BackgroundFit(
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<PixelRect> exclusions,
        double timestamp)
    {
        BackgroundSearchResult? completed = null;
        if (globalSearchWorker.TryTake(out var result)) completed = result;
        if ((lastGlobalSearchTimestamp is null || timestamp - lastGlobalSearchTimestamp >= .25)
            && RelocalizationFeatures().Length >= 6)
        {
            var pixels = luma.ToArray();
            var lines = floorLines.ToArray();
            var masks = exclusions.ToArray();
            var features = RelocalizationFeatures()
                .Select(feature => feature.Copy()).ToArray();
            var localPose = new AtlasPoint(cameraX, cameraY);
            var snapshot = new GroundHypothesisTracker
            {
                cameraX = cameraX, cameraY = cameraY, globalAnchorValid = globalAnchorValid
            };
            foreach (var feature in features) snapshot.globalFeatures[feature.Id] = feature;
            snapshot.canonicalFirstViewIds.UnionWith(canonicalFirstViewIds);
            var priorCanonicalTimestamp = lastCanonicalPhaseSearchTimestamp;
            var scanCanonical = ShouldScanCanonical(timestamp);
            if (globalSearchWorker.Submit(() =>
                {
                    beforeBackgroundGlobalSearch?.Invoke();
                    var fit = snapshot.SearchGlobalFit(
                        pixels, width, height, lines, masks, scanCanonical);
                    backgroundGlobalSearchCompleted?.Invoke(fit?.CameraPosition);
                    return new BackgroundSearchResult(
                        fit,
                        timestamp,
                        localPose);
                }))
                lastGlobalSearchTimestamp = timestamp;
            else lastCanonicalPhaseSearchTimestamp = priorCanonicalTimestamp;
        }
        if (completed is null
            || timestamp < completed.Timestamp
            || timestamp - completed.Timestamp > .5
            || completed.Fit is null)
            return null;
        var continuous = lastLocalFailureTimestamp < completed.Timestamp;
        var transported = completed.Fit with
        {
            CameraPosition = continuous
                ? new AtlasPoint(
                    completed.Fit.CameraPosition.X + cameraX - completed.LocalPose.X,
                    completed.Fit.CameraPosition.Y + cameraY - completed.LocalPose.Y)
                : completed.Fit.CameraPosition,
        };
        var verified = VerifyGlobalFit(
            transported, luma, width, height, floorLines, exclusions, refinementRadius: 0);
        if (verified is null) return null;
        var correction = new CameraVector(
            verified.CameraPosition.X - cameraX,
            verified.CameraPosition.Y - cameraY);
        return correctionConfirmation.Accepts(correction, completed.Timestamp)
            ? verified
            : null;
    }

    private bool ShouldScanCanonical(double timestamp)
    {
        var cadence = lastLocalFailureTimestamp >= timestamp ? .75 : 2;
        if (!correctionConfirmation.NeedsConfirmation
            && lastCanonicalPhaseSearchTimestamp is { } prior && timestamp - prior < cadence)
            return false;
        lastCanonicalPhaseSearchTimestamp = timestamp;
        return true;
    }

    // Mac challenges drifted duplicates with the oldest persistent strips,
    // scanning every X phase without the local predicted-Y bound. Both the
    // worker snapshot and current frame must verify the same frozen population.
    private GlobalFit? SearchGlobalFit(byte[] luma, int width, int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<PixelRect> exclusions, bool scanCanonical)
    {
        var stored = RelocalizationFeatures();
        var fits = new List<GlobalFit>();
        var ordinary = VerifyGlobalFit(PhaseIndependentGlobalFit(stored, luma,
            width, height, floorLines, exclusions, cameraY),
            luma, width, height, floorLines, exclusions);
        if (ordinary is not null) fits.Add(ordinary);
        if (scanCanonical)
        {
            var oldestLines = stored.Select(feature => feature.SegmentId).Distinct()
                .Order().Take(4).ToHashSet();
            var canonical = stored.Where(feature => oldestLines.Contains(feature.SegmentId)).ToArray();
            var firstView = canonical.Where(feature => canonicalFirstViewIds.Contains(feature.Id)).ToArray();
            foreach (var population in new[] { firstView, canonical })
            {
                if (population.Length < 6) continue;
                var ids = population.Select(feature => feature.Id).ToHashSet();
                foreach (var candidate in PhaseIndependentGlobalCandidates(population,
                    luma, width, height, floorLines, exclusions, cameraY,
                    maximumPredictedRowDistance: null).Take(6))
                {
                    var verified = VerifyGlobalFit(candidate with { VerificationFeatureIds = ids },
                        luma, width, height, floorLines, exclusions);
                    if (verified is null) continue;
                    fits.Add(verified);
                    break;
                }
                if (fits.Any(fit => fit.VerificationFeatureIds is not null)) break;
            }
        }
        return fits.OrderBy(fit => fit.Inliers.Min(item => item.GlobalSegmentId))
            .ThenByDescending(fit => fit.Inliers.Count).ThenBy(MeanDescriptorError).FirstOrDefault();
    }

    private static GlobalFit? PhaseIndependentGlobalFit(
        IReadOnlyList<GlobalFeature> storedFeatures, byte[] luma, int width, int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<PixelRect> exclusions, double localCameraY)
    {
        var fits = PhaseIndependentGlobalCandidates(storedFeatures, luma, width, height,
            floorLines, exclusions, localCameraY);
        if (fits.Length == 0) return null;
        var best = fits[0];
        var bestError = MeanDescriptorError(best);
        var ambiguous = fits.Skip(1).Any(rival =>
            Length(rival.CameraPosition.X - best.CameraPosition.X,
                rival.CameraPosition.Y - best.CameraPosition.Y) > 4
            && rival.Inliers.Count >= best.Inliers.Count - 1
            && MeanDescriptorError(rival) <= bestError * 1.15 + 1);
        return ambiguous ? null : best;
    }

    private static GlobalFit[] PhaseIndependentGlobalCandidates(
        IReadOnlyList<GlobalFeature> storedFeatures,
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<PixelRect> exclusions,
        double localCameraY,
        double? maximumPredictedRowDistance = 64)
    {
        if (storedFeatures.Count < 6 || floorLines.Count == 0) return [];
        var observationsByPhase = Enumerable.Range(0, FeatureSize)
            .Select(_ => new List<GlobalObservation>()).ToArray();
        var seenPositions = new HashSet<int>();
        var nearest = new (GlobalFeature Feature, double Error)[3];
        for (var lineIndex = 0; lineIndex < floorLines.Count; lineIndex++)
        {
            var line = floorLines[lineIndex];
            if (line.Row < 0 || line.Row + FeatureHeight > height) continue;
            var candidates = storedFeatures.Where(feature =>
                    (maximumPredictedRowDistance is null
                        || Math.Abs(feature.WorldTopY + localCameraY - line.Row) <= maximumPredictedRowDistance.Value))
                .ToArray();
            if (candidates.Length < 6) continue;
            var lower = Math.Max(0, line.XSpan.Start);
            var upper = Math.Min(width - FeatureWidth,
                line.XSpan.EndInclusive - FeatureWidth + 1);
            for (var x = lower; x <= upper; x++)
            {
                var positionKey = lineIndex * width + x;
                if (!seenPositions.Add(positionKey)
                    || exclusions.Any(rect => Intersects(
                        x, line.Row, FeatureWidth, FeatureHeight, rect)))
                    continue;
                var patch = ReadPatch(luma, width, x, line.Row);
                var descriptor = NormalizedDescriptor(patch);
                if (DescriptorEnergy(descriptor) < 6) continue;
                var nearestCount = 0;
                foreach (var feature in candidates)
                {
                    var error = DescriptorDistance(descriptor, feature.Descriptor);
                    if (error > DescriptorMatchThreshold + 4) continue;
                    var insertion = nearestCount;
                    while (insertion > 0 && error < nearest[insertion - 1].Error) insertion--;
                    if (insertion == nearest.Length) continue;
                    for (var shift = Math.Min(nearestCount, nearest.Length - 1); shift > insertion; shift--)
                        nearest[shift] = nearest[shift - 1];
                    nearest[insertion] = (feature, error);
                    nearestCount = Math.Min(nearest.Length, nearestCount + 1);
                }
                var featureId = -(positionKey + 1);
                for (var candidateIndex = 0; candidateIndex < nearestCount; candidateIndex++)
                {
                    var candidate = nearest[candidateIndex];
                    observationsByPhase[x % FeatureSize].Add(new GlobalObservation(
                        featureId,
                        -(lineIndex + 1),
                        candidate.Feature.Id,
                        candidate.Feature.SegmentId,
                        candidate.Feature.WorldX,
                        x,
                        new AtlasPoint(
                            candidate.Feature.WorldX - x,
                            line.Row - candidate.Feature.WorldTopY),
                        candidate.Error));
                }
            }
        }
        var fits = observationsByPhase
            .Select(observations => FitGlobalObservations(observations, 6, 96))
            .Where(fit => fit is not null)
            .Cast<GlobalFit>()
            .OrderByDescending(fit => fit.Inliers.Count)
            .ThenBy(MeanDescriptorError)
            .ToArray();
        return fits;
    }

    private static GlobalFit? FitGlobalObservations(
        IReadOnlyList<GlobalObservation> observations,
        int minimumInliers,
        double minimumHorizontalSpread)
    {
        if (observations.Count < minimumInliers) return null;
        GlobalObservation[] best = [];
        var bestError = double.PositiveInfinity;
        foreach (var seed in observations)
        {
            var inliers = UniqueOrderedInliers(observations, seed.CameraPosition);
            if (HorizontalSpread(inliers) < minimumHorizontalSpread
                || !HasLineSequenceAnchor(inliers))
                continue;
            var error = inliers.Sum(item => Length(
                item.CameraPosition.X - seed.CameraPosition.X,
                item.CameraPosition.Y - seed.CameraPosition.Y));
            if (inliers.Length > best.Length
                || (inliers.Length == best.Length && error < bestError))
            {
                best = inliers;
                bestError = error;
            }
        }
        if (best.Length < minimumInliers) return null;
        var position = WeightedMean(best);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var refined = UniqueOrderedInliers(observations, position);
            if (refined.Length < minimumInliers
                || HorizontalSpread(refined) < minimumHorizontalSpread
                || !HasLineSequenceAnchor(refined))
                break;
            best = refined;
            position = WeightedMean(refined);
        }
        return new GlobalFit(
            position,
            best.Select(item => item.FeatureId).ToHashSet(),
            best.Select(item => item.GlobalId).ToHashSet(),
            best);
    }

    private static GlobalObservation[] UniqueOrderedInliers(
        IReadOnlyList<GlobalObservation> observations,
        AtlasPoint position)
    {
        var nearby = observations.Where(item => Length(
                    item.CameraPosition.X - position.X,
                    item.CameraPosition.Y - position.Y) <= 3)
            .OrderBy(item => item.DescriptorError)
            .ToArray();
        var featureIds = new HashSet<int>();
        var globalIds = new HashSet<int>();
        var unique = nearby.Where(item =>
                featureIds.Add(item.FeatureId) && globalIds.Add(item.GlobalId))
            .ToArray();
        return unique.GroupBy(item => (item.CurrentSegmentId, item.GlobalSegmentId))
            .SelectMany(OrderPreservingSequence)
            .ToArray();
    }

    private static IReadOnlyList<GlobalObservation> OrderPreservingSequence(
        IEnumerable<GlobalObservation> source)
    {
        var ordered = source.OrderBy(item => item.ImageX).ToArray();
        if (ordered.Length <= 1) return ordered;
        var counts = Enumerable.Repeat(1, ordered.Length).ToArray();
        var errors = ordered.Select(item => item.DescriptorError).ToArray();
        var previous = Enumerable.Repeat(-1, ordered.Length).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        for (var earlier = 0; earlier < index; earlier++)
        {
            if (ordered[earlier].GlobalWorldX >= ordered[index].GlobalWorldX) continue;
            var proposedCount = counts[earlier] + 1;
            var proposedError = errors[earlier] + ordered[index].DescriptorError;
            if (proposedCount <= counts[index]
                && (proposedCount != counts[index] || proposedError >= errors[index]))
                continue;
            counts[index] = proposedCount;
            errors[index] = proposedError;
            previous[index] = earlier;
        }
        var cursor = Enumerable.Range(0, ordered.Length).OrderByDescending(index => counts[index])
            .ThenBy(index => errors[index]).First();
        var sequence = new List<GlobalObservation>();
        while (cursor >= 0)
        {
            sequence.Add(ordered[cursor]);
            cursor = previous[cursor];
        }
        sequence.Reverse();
        return sequence;
    }

    private GlobalFeature[] RelocalizationFeatures()
    {
        var byId = retiredGlobalFeatures.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in globalFeatures)
            byId[pair.Key] = pair.Value;
        foreach (var pair in canonicalAnchorFeatures)
            byId[pair.Key] = pair.Value;
        return byId.Values.OrderBy(feature => feature.Id).ToArray();
    }

    private GlobalFit? VerifyGlobalFit(
        GlobalFit? fit,
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<PixelRect> exclusions,
        int refinementRadius = 8)
    {
        if (fit is null) return null;
        var segmentIds = fit.Inliers.Select(item => item.GlobalSegmentId).ToHashSet();
        var stored = RelocalizationFeatures()
            .Where(feature => segmentIds.Contains(feature.SegmentId)
                && (fit.VerificationFeatureIds is null || fit.VerificationFeatureIds.Contains(feature.Id)))
            .ToArray();
        if (stored.Length == 0) return null;
        GroundGlobalCorrectionPolicy.Quality? QualityAt(
            AtlasPoint pose,
            IReadOnlyList<GlobalFeature> population)
        {
            var errors = new Dictionary<(long X, long Y), (double X, double Error)>();
            foreach (var feature in population)
            {
                var x = Round(feature.WorldX - pose.X);
                var y = Round(feature.WorldTopY + pose.Y);
                if (x < 8 || x + FeatureWidth + 8 > width || y < 0
                    || y + FeatureHeight > height
                    || !floorLines.Any(line => Math.Abs(line.Row - y) <= 4
                        && line.XSpan.Start <= x
                        && line.XSpan.EndInclusive >= x + FeatureWidth - 1)
                    || exclusions.Any(rect => Intersects(
                        x, y, FeatureWidth, FeatureHeight, rect)))
                    continue;
                var error = GroundCameraSolver.PatchError(
                    feature.MatchingPatch, ReadPatch(luma, width, x, y));
                var key = ((long)Math.Round(feature.WorldX * 1024),
                    (long)Math.Round(feature.WorldTopY * 1024));
                if (!errors.TryGetValue(key, out var prior) || error < prior.Error)
                    errors[key] = (x, error);
            }
            if (errors.Count == 0) return null;
            var inliers = errors.Values.Where(value => value.Error <= 12).ToArray();
            return new GroundGlobalCorrectionPolicy.Quality(
                errors.Count,
                inliers.Length,
                inliers.Length == 0 ? 0 : inliers.Max(value => value.X) - inliers.Min(value => value.X),
                errors.Values.Sum(value => Math.Min(32, value.Error)) / errors.Count);
        }

        GlobalFit? VerifyPopulation(IReadOnlyList<GlobalFeature> population)
        {
            var seed = fit.CameraPosition;
            var offset = refinementRadius == 0
                ? new GroundPoseRefinementSearch.Offset(0, 0)
                : GroundPoseRefinementSearch.Find((dx, dy) =>
                    QualityAt(new AtlasPoint(seed.X + dx, seed.Y + dy), population),
                    refinementRadius)
                    ?? new GroundPoseRefinementSearch.Offset(0, 0);
            var refined = new AtlasPoint(seed.X + offset.Dx, seed.Y + offset.Dy);
            var displacement = Length(refined.X - cameraX, refined.Y - cameraY);
            return GroundGlobalCorrectionPolicy.Accepts(
                    QualityAt(refined, population),
                    QualityAt(new AtlasPoint(cameraX, cameraY), population),
                    displacement,
                    globalAnchorValid)
                ? fit with { CameraPosition = refined }
                : null;
        }

        var firstView = stored
            .Where(feature => canonicalFirstViewIds.Contains(feature.Id))
            .ToArray();
        if (firstView.Length >= 6 && firstView.Length != stored.Length
            && VerifyPopulation(firstView) is { } firstViewFit)
        {
            return firstViewFit;
        }
        return VerifyPopulation(stored);
    }

    private static AtlasPoint WeightedMean(IReadOnlyList<GlobalObservation> observations)
    {
        var x = 0d;
        var y = 0d;
        var weightSum = 0d;
        foreach (var observation in observations)
        {
            var weight = 1 / (1 + observation.DescriptorError / 8);
            x += observation.CameraPosition.X * weight;
            y += observation.CameraPosition.Y * weight;
            weightSum += weight;
        }
        return new AtlasPoint(x / Math.Max(double.Epsilon, weightSum),
            y / Math.Max(double.Epsilon, weightSum));
    }

    private static double HorizontalSpread(IReadOnlyList<GlobalObservation> observations) =>
        observations.Count == 0 ? 0
            : observations.Max(item => item.ImageX) - observations.Min(item => item.ImageX);

    private static bool HasLineSequenceAnchor(IReadOnlyList<GlobalObservation> observations) =>
        observations.GroupBy(item => (item.CurrentSegmentId, item.GlobalSegmentId))
            .Any(group => group.Count() >= 3 && HorizontalSpread(group.ToArray()) >= 32);

    private static double MeanDescriptorError(GlobalFit fit) =>
        fit.Inliers.Count == 0 ? double.PositiveInfinity
            : fit.Inliers.Average(item => item.DescriptorError);

    private static short[] NormalizedDescriptor(IReadOnlyList<byte> patch)
    {
        if (patch.Count != FeaturePixelCount) return [];
        var pooled = new int[8 * (FeatureHeight / 2)];
        for (var cellY = 0; cellY < FeatureHeight / 2; cellY++)
        for (var cellX = 0; cellX < 8; cellX++)
        {
            var sum = 0;
            for (var y = 0; y < 2; y++)
            {
                var source = (cellY * 2 + y) * FeatureWidth + cellX * 2;
                sum += patch[source] + patch[source + 1];
            }
            pooled[cellY * 8 + cellX] = sum / 4;
        }
        var mean = pooled.Sum() / Math.Max(1, pooled.Length);
        var centered = pooled.Select(value => value - mean).ToArray();
        var contrast = centered.Sum(Math.Abs) / (double)Math.Max(1, centered.Length);
        if (contrast < 1) return centered.Select(value => (short)value).ToArray();
        var scale = 32 / contrast;
        return centered.Select(value => (short)Math.Clamp(
                Round(value * scale), -127, 127))
            .ToArray();
    }

    private static double DescriptorDistance(short[] left, short[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return double.PositiveInfinity;
        long total = 0;
        for (var index = 0; index < left.Length; index++)
            total += Math.Abs((int)left[index] - right[index]);
        return (double)total / left.Length;
    }

    private static double DescriptorEnergy(IReadOnlyList<short> descriptor) =>
        descriptor.Count == 0 ? 0
            : descriptor.Sum(value => Math.Abs((int)value)) / (double)descriptor.Count;

    private static double SnapInteger(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Abs(value - rounded) < .000_001 ? rounded : value;
    }

    private static ActiveFeature[] RecoveryFeatureCandidates(
        IReadOnlyList<ActiveFeature> features)
    {
        var eligible = features
            .Where(feature => feature.ReferenceValid
                && feature.Patch.Length == FeaturePixelCount
                && DescriptorEnergy(NormalizedDescriptor(feature.Patch)) >= 6)
            .ToArray();
        if (eligible.Length <= RecoveryFeatureLimit) return eligible;

        var groups = eligible
            .GroupBy(feature => feature.SegmentId)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(feature => feature.SequenceIndex).ToArray())
            .ToArray();
        if (groups.Length == 0) return [];
        var quota = Math.Max(1, RecoveryFeatureLimit / groups.Length);
        var selected = new List<ActiveFeature>(RecoveryFeatureLimit);
        foreach (var group in groups)
        {
            var count = Math.Min(quota, group.Length);
            if (count == 1)
            {
                selected.Add(group[group.Length / 2]);
                continue;
            }
            for (var slot = 0; slot < count; slot++)
                selected.Add(group[slot * (group.Length - 1) / (count - 1)]);
        }
        if (selected.Count < RecoveryFeatureLimit)
        {
            var selectedIds = selected.Select(feature => feature.Id).ToHashSet();
            var remainder = eligible
                .Where(feature => !selectedIds.Contains(feature.Id))
                .OrderBy(feature => feature.X)
                .ToArray();
            var available = RecoveryFeatureLimit - selected.Count;
            if (remainder.Length <= available)
            {
                selected.AddRange(remainder);
            }
            else
            {
                for (var slot = 0; slot < available; slot++)
                {
                    var index = available == 1
                        ? remainder.Length / 2
                        : slot * (remainder.Length - 1) / (available - 1);
                    selected.Add(remainder[index]);
                }
            }
        }
        return selected.Take(RecoveryFeatureLimit).ToArray();
    }

    private RecoveryAttempt RecoveryConsensus(
        IReadOnlyList<ActiveFeature> features,
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<PixelRect> exclusions)
    {
        if (features.Count < 4 || floorLines.Count == 0)
            return new([], null);

        var predictedX = Round(lastImageTranslation.X);
        var predictedY = Round(lastImageTranslation.Y);
        var verticalTranslations = new HashSet<int>();
        foreach (var feature in features)
        {
            var expectedY = feature.Y + predictedY;
            foreach (var line in floorLines.Where(line =>
                         Math.Abs((long)line.Row - expectedY) <= RecoveryVerticalSearchRadius))
            {
                for (var refinement = -VerticalRefinementRadius;
                     refinement <= VerticalRefinementRadius;
                     refinement++)
                {
                    var translation = line.Row + refinement - feature.Y;
                    if (Math.Abs((long)translation - predictedY) <= RecoveryVerticalSearchRadius)
                        verticalTranslations.Add(translation);
                }
            }
        }
        if (verticalTranslations.Count == 0) return new([], null);

        var candidates = new List<(int Dx, int Dy, Match[] Matches, double MeanError)>();
        (int Dx, int Dy, Match[] Matches, double MeanError)? Evaluate(int dx, int dy)
        {
            var candidateMatches = new List<Match>();
            var totalError = 0d;
            foreach (var feature in features)
            {
                var x = feature.X + dx;
                var y = feature.Y + dy;
                if (x < 0 || (long)x + FeatureWidth > width
                    || y < 0 || (long)y + FeatureHeight > height
                    || exclusions.Any(rect => Intersects(
                        x, y, FeatureWidth, FeatureHeight, rect)))
                {
                    continue;
                }
                var line = floorLines
                    .Where(item => Math.Abs((long)item.Row - y) <= VerticalRefinementRadius
                        && x >= item.XSpan.Start
                        && (long)x + FeatureWidth - 1 <= item.XSpan.EndInclusive)
                    .MinBy(item => Math.Abs((long)item.Row - y));
                if (line is null) continue;
                var error = AbsolutePatchError(feature.Patch, luma, width, x, y);
                if (error > PromotionPhotometricError) continue;
                totalError += error;
                candidateMatches.Add(new Match(
                    feature, x, y, error, new CameraVector(dx, dy), line.Row));
            }
            if (candidateMatches.Count < 4
                || candidateMatches.Max(match => match.X)
                    - candidateMatches.Min(match => match.X) < 48)
            {
                return null;
            }
            return (dx, dy, candidateMatches.ToArray(),
                totalError / candidateMatches.Count);
        }

        var lowerX = predictedX - RecoveryHorizontalSearchRadius;
        var upperX = predictedX + RecoveryHorizontalSearchRadius;
        foreach (var dy in verticalTranslations.Order())
        {
            for (var dx = lowerX; dx <= upperX; dx += 2)
            {
                if (Evaluate(dx, dy) is { } candidate) candidates.Add(candidate);
            }
        }
        if (candidates.Count == 0) return new([], null);
        var coarse = candidates
            .OrderByDescending(candidate => candidate.Matches.Length)
            .ThenBy(candidate => candidate.MeanError)
            .First();
        for (var dy = coarse.Dy - 1; dy <= coarse.Dy + 1; dy++)
        {
            for (var dx = Math.Max(lowerX, coarse.Dx - 2);
                 dx <= Math.Min(upperX, coarse.Dx + 2);
                 dx++)
            {
                if (Evaluate(dx, dy) is { } candidate) candidates.Add(candidate);
            }
        }
        var ranked = candidates
            .OrderByDescending(candidate => candidate.Matches.Length)
            .ThenBy(candidate => candidate.MeanError)
            .ToArray();
        var best = ranked[0];
        var ambiguous = ranked.Skip(1).Any(rival =>
            (Math.Abs(rival.Dx - best.Dx) > 2 || Math.Abs(rival.Dy - best.Dy) > 2)
            && rival.Matches.Length >= best.Matches.Length - 1
            && rival.MeanError <= best.MeanError * 1.08 + .5);
        var fit = ambiguous
            ? null
            : new GroundTranslationFit(
                new CameraVector(best.Dx, best.Dy),
                best.Matches.Select(match => match.Feature.Id).ToHashSet(),
                0);
        return new(best.Matches, fit);
    }

    private Match? BestMatch(
        ActiveFeature feature,
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> floorLines)
    {
        var expectedX = feature.X + Round(lastImageTranslation.X);
        var expectedY = feature.Y + Round(lastImageTranslation.Y);
        Match? best = null;
        var bestError = double.PositiveInfinity;
        foreach (var line in floorLines.Where(line =>
                     Math.Abs(line.Row - expectedY) <= VerticalSearchRadius))
        {
            var lowerX = Math.Max(0, Math.Max(line.XSpan.Start,
                expectedX - HorizontalSearchRadius));
            var upperX = Math.Min(width - FeatureWidth,
                Math.Min(line.XSpan.EndInclusive - FeatureWidth + 1,
                    expectedX + HorizontalSearchRadius));
            var lowerY = Math.Max(0, line.Row - VerticalRefinementRadius);
            var upperY = Math.Min(height - FeatureHeight, line.Row + VerticalRefinementRadius);
            for (var y = lowerY; y <= upperY; y++)
            {
                for (var x = lowerX; x <= upperX; x++)
                {
                    var error = AbsolutePatchError(feature.Patch, luma, width, x, y);
                    if (error >= bestError) continue;
                    bestError = error;
                    best = new Match(feature, x, y, error,
                        new CameraVector(x - feature.X, y - feature.Y), line.Row);
                }
            }
        }
        return best;
    }

    private static ActiveFeature? UpdatedFeature(
        Match match,
        GroundTranslationFit? fit,
        byte[] luma,
        int width)
    {
        double? residual = fit is null ? null : Length(
            match.ImageTranslation.X - fit.ImageTranslation.X,
            match.ImageTranslation.Y - fit.ImageTranslation.Y);
        var isGround = fit?.InlierIds.Contains(match.Feature.Id) == true
            && residual <= GroundTranslationRansac.InlierRadius;
        var classification = fit is null
            ? GroundHypothesisClassification.Candidate
            : isGround
                ? GroundHypothesisClassification.GroundPlane
                : GroundHypothesisClassification.DepthError;
        var patch = new byte[FeaturePixelCount];
        for (var row = 0; row < FeatureHeight; row++)
        {
            luma.AsSpan((match.Y + row) * width + match.X, FeatureWidth)
                .CopyTo(patch.AsSpan(row * FeatureWidth));
        }
        return new ActiveFeature
        {
            Id = match.Feature.Id,
            SegmentId = match.Feature.SegmentId,
            SequenceIndex = match.Feature.SequenceIndex,
            X = match.X,
            Y = match.Y,
            Patch = patch,
            Age = match.Feature.Age + 1,
            Classification = classification,
            MotionResidual = residual,
            PhotometricError = match.PhotometricError,
            ConsecutiveGroundFrames = isGround
                ? match.Feature.ConsecutiveGroundFrames + 1
                : 0,
            GlobalFeatureId = match.Feature.GlobalFeatureId,
            ReferenceValid = true,
        };
    }

    private static DetectedFloorLine[] StabilizeFloorLines(
        IReadOnlyList<DetectedFloorLine> floorLines,
        IReadOnlyList<Match> matches,
        GroundTranslationFit? fit)
    {
        if (fit is null) return floorLines.ToArray();
        var inliers = matches.Where(match => fit.InlierIds.Contains(match.Feature.Id)).ToArray();
        return floorLines.Select(line =>
        {
            var offsets = inliers.Where(match =>
                    match.DetectedLineRow == line.Row
                    && line.XSpan.Start <= match.X
                    && line.XSpan.EndInclusive >= match.X + FeatureSize - 1)
                .Select(match => match.Y - line.Row)
                .Order()
                .ToArray();
            if (offsets.Length < 2) return line;
            var offset = Math.Clamp(offsets[offsets.Length / 2],
                -VerticalRefinementRadius, VerticalRefinementRadius);
            return offset == 0
                ? line
                : new DetectedFloorLine(line.Row + offset, line.XSpan, line.EvidenceSpans);
        }).ToArray();
    }

    /// <summary>
    /// A temporarily masked lower edge must not turn its known dip back into
    /// an upper-floor occlusion bridge. Keep both upper IDs and the lower floor.
    /// </summary>
    private DetectedFloorLine[] PreservingKnownFloorGaps(
        IReadOnlyList<DetectedFloorLine> lines)
    {
        var pieces = new List<DetectedFloorLine>();
        foreach (var line in lines)
        {
            var group = new List<PixelSpan>();
            void Finish()
            {
                if (group.Count == 0) return;
                pieces.Add(new DetectedFloorLine(
                    line.Row,
                    new PixelSpan(group[0].Start, group[^1].EndInclusive),
                    group.ToArray()));
            }

            foreach (var evidence in line.EvidenceSpans)
            {
                if (group.Count > 0
                    && HasLowerFloorInGap(
                        cameraX + group[^1].Start,
                        cameraX + group[^1].EndInclusive,
                        cameraX + evidence.Start,
                        cameraX + evidence.EndInclusive,
                        line.Row - cameraY,
                        lines.Select(item => (
                            MinimumX: cameraX + item.XSpan.Start,
                            MaximumX: cameraX + item.XSpan.EndInclusive,
                            WorldY: item.Row - cameraY))))
                {
                    Finish();
                    group.Clear();
                }
                group.Add(evidence);
            }
            Finish();
        }
        return pieces.ToArray();
    }

    private DetectedFloorLine[] AtlasAdmissionLines(
        IReadOnlyList<DetectedFloorLine> floorLines,
        byte[] luma,
        int width,
        int height,
        int lineSeparation,
        IReadOnlyList<PixelRect> protectedOcclusions)
    {
        if (linePresence.MinimumObservationSeconds <= 0) return floorLines.ToArray();

        static int EvidenceCount(DetectedFloorLine line) =>
            line.EvidenceSpans.Sum(span => span.EndInclusive - span.Start + 1);

        var ordered = floorLines
            .OrderByDescending(EvidenceCount)
            .ThenByDescending(line => line.XSpan.EndInclusive - line.XSpan.Start + 1)
            .ThenBy(line => line.Row)
            .ToArray();
        var bootstrap = segments.Count == 0 ? ordered.FirstOrDefault() : null;
        var admitted = new List<DetectedFloorLine>();
        var seenCandidateIndices = new HashSet<int>();

        foreach (var line in ordered)
        {
            var minimumX = cameraX + line.XSpan.Start;
            var maximumX = cameraX + line.XSpan.EndInclusive;
            var worldY = line.Row - cameraY;
            var matchesPersistent = segments.Any(segment =>
                Math.Abs(segment.WorldTopY - worldY) <= SegmentAssociationRowTolerance
                && Math.Min(segment.MaximumWorldX, maximumX)
                    - Math.Max(segment.MinimumWorldX, minimumX) + 1 >= FeatureSize);
            if (matchesPersistent || ReferenceEquals(line, bootstrap))
            {
                admitted.Add(line);
                continue;
            }

            var candidateIndex = Enumerable.Range(0, lineMotionCandidates.Count)
                .Where(index => !seenCandidateIndices.Contains(index)
                    && Math.Abs(lineMotionCandidates[index].WorldTopY - worldY)
                        <= SegmentAssociationRowTolerance
                    && Math.Min(lineMotionCandidates[index].MaximumWorldX, maximumX)
                        - Math.Max(lineMotionCandidates[index].MinimumWorldX, minimumX) + 1
                        >= FeatureSize)
                .OrderBy(index => Math.Abs(lineMotionCandidates[index].WorldTopY - worldY))
                .Cast<int?>()
                .FirstOrDefault();

            int index;
            if (candidateIndex is { } existing)
            {
                index = existing;
                UpdateLineMotionCandidate(index, line, luma, width, height, protectedOcclusions);
            }
            else
            {
                lineMotionCandidates.Add(new LineMotionCandidate
                {
                    MinimumWorldX = minimumX,
                    MaximumWorldX = maximumX,
                    WorldTopY = worldY,
                    LastSeenFrame = frameIndex,
                    ReferenceCamera = new AtlasPoint(cameraX, cameraY),
                    ReferencePatches = PendingLinePatches(
                        line, luma, width, height, protectedOcclusions),
                });
                index = lineMotionCandidates.Count - 1;
            }
            seenCandidateIndices.Add(index);
            if (lineMotionCandidates[index].Verified) admitted.Add(line);
        }

        lineMotionCandidates.RemoveAll(candidate =>
            !candidate.Verified
            && candidate.LastSeenFrame < frameIndex - MissingLineRejectionFrames);

        return admitted.Where(candidate =>
        {
            if (segments.Count == 0) return true;
            var candidateWorldY = candidate.Row - cameraY;
            var candidateMinimumX = cameraX + candidate.XSpan.Start;
            var candidateMaximumX = cameraX + candidate.XSpan.EndInclusive;
            return !segments.Any(segment =>
            {
                var rowDistance = Math.Abs(segment.WorldTopY - candidateWorldY);
                if (rowDistance <= SegmentAssociationRowTolerance
                    || rowDistance > Math.Max(0, lineSeparation)) return false;
                if (HorizontalGap(segment.MinimumWorldX, segment.MaximumWorldX,
                        candidateMinimumX, candidateMaximumX) > 0) return false;
                return !lineMotionCandidates.Any(motion =>
                    motion.Verified
                    && Math.Abs(motion.WorldTopY - candidateWorldY)
                        <= SegmentAssociationRowTolerance
                    && Math.Min(motion.MaximumWorldX, candidateMaximumX)
                        - Math.Max(motion.MinimumWorldX, candidateMinimumX) + 1
                        >= FeatureSize);
            });
        }).ToArray();
    }

    private void UpdateLineMotionCandidate(
        int index,
        DetectedFloorLine line,
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<PixelRect> protectedOcclusions)
    {
        var candidate = lineMotionCandidates[index];
        var movement = Length(
            cameraX - candidate.ReferenceCamera.X,
            cameraY - candidate.ReferenceCamera.Y);
        if (candidate.ReferencePatches.Count < 2)
        {
            candidate.ReferenceCamera = new AtlasPoint(cameraX, cameraY);
            candidate.ReferencePatches = PendingLinePatches(
                line, luma, width, height, protectedOcclusions);
        }
        else if (movement >= 1.5)
        {
            var expectedRow = Round(candidate.WorldTopY + cameraY);
            var comparisons = new List<(double WorldX, double Error)>();
            if (Math.Abs(expectedRow - line.Row) <= 2
                && expectedRow >= 0
                && expectedRow + FeatureHeight <= height)
            {
                foreach (var reference in candidate.ReferencePatches)
                {
                    var x = Round(reference.WorldX - cameraX);
                    var featureEnd = x + FeatureWidth - 1;
                    if (x < 0 || x + FeatureWidth > width
                        || !line.EvidenceSpans.Any(span =>
                            Math.Min(span.EndInclusive, featureEnd)
                                - Math.Max(span.Start, x) + 1 >= 8)
                        || protectedOcclusions.Any(rect =>
                            Intersects(x, expectedRow, FeatureWidth, FeatureHeight, rect)))
                        continue;
                    var observed = ReadPatch(luma, width, x, expectedRow);
                    comparisons.Add((reference.WorldX,
                        GroundCameraSolver.PatchError(reference.Pixels, observed)));
                }
            }
            var inliers = comparisons.Where(comparison => comparison.Error <= 18).ToArray();
            var spread = inliers.Length == 0
                ? 0
                : inliers.Max(item => item.WorldX) - inliers.Min(item => item.WorldX);
            var passed = comparisons.Count >= 2
                && inliers.Length * 2 > comparisons.Count
                && spread >= 8;
            if (passed)
            {
                candidate.SuccessfulMotionChecks++;
                candidate.FailedMotionChecks = 0;
                candidate.AccumulatedCameraTravel += movement;
                candidate.ReferenceCamera = new AtlasPoint(cameraX, cameraY);
                candidate.ReferencePatches = PendingLinePatches(
                    line, luma, width, height, protectedOcclusions);
            }
            else if (comparisons.Count < 2)
            {
                candidate.ReferenceCamera = new AtlasPoint(cameraX, cameraY);
                candidate.ReferencePatches = PendingLinePatches(
                    line, luma, width, height, protectedOcclusions);
            }
            else
            {
                candidate.FailedMotionChecks++;
                candidate.SuccessfulMotionChecks = Math.Max(
                    0, candidate.SuccessfulMotionChecks - 1);
                if (candidate.FailedMotionChecks >= 3)
                {
                    candidate.ReferenceCamera = new AtlasPoint(cameraX, cameraY);
                    candidate.ReferencePatches = PendingLinePatches(
                        line, luma, width, height, protectedOcclusions);
                    candidate.FailedMotionChecks = 0;
                    candidate.AccumulatedCameraTravel = 0;
                }
            }
        }
        candidate.MinimumWorldX = Math.Min(
            candidate.MinimumWorldX, cameraX + line.XSpan.Start);
        candidate.MaximumWorldX = Math.Max(
            candidate.MaximumWorldX, cameraX + line.XSpan.EndInclusive);
        candidate.LastSeenFrame = frameIndex;
        candidate.Verified = candidate.SuccessfulMotionChecks >= 2
            && candidate.AccumulatedCameraTravel >= 4;
    }

    private IReadOnlyList<PendingLinePatch> PendingLinePatches(
        DetectedFloorLine line,
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<PixelRect> protectedOcclusions)
    {
        if (line.Row < 0 || line.Row + FeatureHeight > height) return [];
        var positions = new HashSet<int>();
        foreach (var evidence in line.EvidenceSpans
                     .Where(span => span.EndInclusive - span.Start + 1 >= FeatureWidth))
        {
            for (var x = evidence.Start;
                 x + FeatureWidth - 1 <= evidence.EndInclusive;
                 x += FeatureSize)
                positions.Add(x);
            positions.Add(evidence.EndInclusive - FeatureWidth + 1);
        }
        var ordered = positions.Order().ToArray();
        var stride = Math.Max(1, (int)Math.Ceiling(ordered.Length / 16d));
        var result = new List<PendingLinePatch>();
        for (var offset = 0; offset < ordered.Length; offset++)
        {
            if (offset % stride != 0) continue;
            var x = ordered[offset];
            if (x < 0 || x + FeatureWidth > width
                || protectedOcclusions.Any(rect =>
                    Intersects(x, line.Row, FeatureWidth, FeatureHeight, rect)))
                continue;
            var pixels = ReadPatch(luma, width, x, line.Row);
            if (pixels.Max() - pixels.Min() < 12) continue;
            result.Add(new PendingLinePatch(cameraX + x, pixels));
        }
        return result;
    }

    private void IntegrateSegments(
        IReadOnlyList<ObservedLine> floorLines,
        int occlusionMergeGap,
        IReadOnlyList<ObservedLine> knownFloorLines)
    {
        foreach (var line in floorLines.OrderByDescending(line => line.Span.EndInclusive - line.Span.Start)
                     .ThenBy(line => line.Span.Start).ThenBy(line => line.Row))
        {
            var minimum = cameraX + line.Span.Start;
            var maximum = cameraX + line.Span.EndInclusive;
            var worldY = line.Row - cameraY;
            var candidates = segments.Where(segment =>
                    Math.Abs(segment.WorldTopY - worldY) <= SegmentAssociationRowTolerance
                    && HorizontalGap(segment.MinimumWorldX, segment.MaximumWorldX,
                        minimum, maximum) <= occlusionMergeGap
                    && !HasLowerFloorInGap(
                        segment.MinimumWorldX,
                        segment.MaximumWorldX,
                        minimum,
                        maximum,
                        worldY,
                        knownFloorLines.Select(item => (
                            MinimumX: cameraX + item.Span.Start,
                            MaximumX: cameraX + item.Span.EndInclusive,
                            WorldY: item.Row - cameraY))))
                .OrderBy(segment => segment.Id)
                .ToArray();
            if (candidates.Length == 0)
            {
                var bounds = RectifiedCellBounds(minimum, maximum);
                if (bounds is null) continue;
                var segment = new Segment
                {
                    Id = nextSegmentId++,
                    OriginWorldX = bounds.Value.Start,
                    MinimumWorldX = bounds.Value.Start,
                    MaximumWorldX = bounds.Value.End,
                    WorldTopY = worldY,
                };
                EnsureCellIds(segment);
                segments.Add(segment);
                continue;
            }

            var target = candidates[0];
            var unionMinimum = Math.Min(minimum, candidates.Min(segment => segment.MinimumWorldX));
            var unionMaximum = Math.Max(maximum, candidates.Max(segment => segment.MaximumWorldX));
            var mergedBounds = RectifiedCellBounds(unionMinimum, unionMaximum);
            if (mergedBounds is null) continue;
            var mergedSegmentIds = candidates.Select(segment => segment.Id).ToHashSet();
            var oldCells = candidates.SelectMany(segment => segment.CellIds.Select(pair =>
                    (SegmentId: segment.Id,
                     OldIndex: pair.Key,
                     WorldX: segment.OriginWorldX + pair.Key * FeatureSize,
                     FeatureId: pair.Value,
                     Evidence: segment.CellEvidence[pair.Key])))
                .OrderBy(cell => cell.WorldX)
                .ToArray();
            var retainedIndices = new Dictionary<(int SegmentId, int SequenceIndex), int>();
            target.OriginWorldX = mergedBounds.Value.Start;
            target.MinimumWorldX = mergedBounds.Value.Start;
            target.MaximumWorldX = mergedBounds.Value.End;
            target.CellIds.Clear();
            target.CellEvidence.Clear();
            foreach (var cell in oldCells)
            {
                var index = Round((cell.WorldX - target.OriginWorldX) / FeatureSize);
                var worldX = target.OriginWorldX + index * FeatureSize;
                if (worldX < target.MinimumWorldX - .5
                    || worldX + FeatureSize - 1 > target.MaximumWorldX + .5
                    || target.CellIds.ContainsKey(index)) continue;
                target.CellIds[index] = cell.FeatureId;
                target.CellEvidence[index] = cell.Evidence;
                retainedIndices[(cell.SegmentId, cell.OldIndex)] = index;
                var feature = active.FirstOrDefault(item => item.Id == cell.FeatureId);
                if (feature is not null)
                {
                    feature.SegmentId = target.Id;
                    feature.SequenceIndex = index;
                }
            }
            foreach (var merged in candidates.Skip(1)) segments.Remove(merged);
            EnsureCellIds(target);
            foreach (var id in globalFeatures.Keys.ToArray())
            {
                var feature = globalFeatures[id];
                if (!mergedSegmentIds.Contains(feature.SegmentId)) continue;
                if (!retainedIndices.TryGetValue(
                        (feature.SegmentId, feature.SequenceIndex), out var newIndex))
                {
                    globalFeatures.Remove(id);
                    retiredGlobalFeatures.Remove(id);
                    canonicalAnchorFeatures.Remove(id);
                    canonicalFirstViewIds.Remove(id);
                    continue;
                }
                feature.SegmentId = target.Id;
                feature.SequenceIndex = newIndex;
            }
        }
        segments.Sort((left, right) => left.Id.CompareTo(right.Id));
    }

    /// <summary>
    /// Do not merge two co-planar ends over a positively observed lower floor.
    /// Persistent geometry retains the boundary when its evidence is occluded.
    /// </summary>
    private bool HasLowerFloorInGap(
        double leftMinimum,
        double leftMaximum,
        double rightMinimum,
        double rightMaximum,
        double worldY,
        IEnumerable<(double MinimumX, double MaximumX, double WorldY)> floorLines)
    {
        if (leftMinimum <= rightMaximum && rightMinimum <= leftMaximum) return false;
        var lower = Math.Min(leftMaximum, rightMaximum);
        var upper = Math.Max(leftMinimum, rightMinimum);
        if (upper - lower < FeatureSize) return false;

        bool Crosses(double minimumX, double maximumX, double candidateWorldY) =>
            candidateWorldY > worldY + SegmentAssociationRowTolerance
            && Math.Min(upper, maximumX) - Math.Max(lower, minimumX) >= FeatureSize;

        return segments.Any(segment => Crosses(
                   segment.MinimumWorldX, segment.MaximumWorldX, segment.WorldTopY))
               || floorLines.Any(line => Crosses(
                   line.MinimumX, line.MaximumX, line.WorldY));
    }

    private List<ActiveFeature> ProjectSegments(
        IReadOnlyList<ObservedLine> floorLines,
        IReadOnlyList<ActiveFeature> tracked,
        IReadOnlyList<ActiveFeature> previous,
        byte[] luma,
        int width,
        int height,
        IReadOnlyList<PixelRect> protectedOcclusions)
    {
        var trackedById = tracked.ToDictionary(feature => feature.Id);
        var previousById = previous.ToDictionary(feature => feature.Id);
        var projected = new List<ActiveFeature>();
        foreach (var segment in segments)
        {
            foreach (var pair in segment.CellIds.OrderBy(pair => pair.Key))
            {
                var worldX = segment.OriginWorldX + pair.Key * FeatureSize;
                var x = Round(worldX - cameraX);
                var y = Round(segment.WorldTopY + cameraY);
                if (x < 0 || y < 0 || x + FeatureWidth > width || y + FeatureHeight > height)
                    continue;
                var hasEvidence = !protectedOcclusions.Any(rect =>
                        Intersects(x, y, FeatureWidth, FeatureHeight, rect))
                    && HasGroundEvidence(x, y, floorLines);
                if (hasEvidence && trackedById.TryGetValue(pair.Value, out var trackedFeature))
                {
                    trackedFeature.SegmentId = segment.Id;
                    trackedFeature.SequenceIndex = pair.Key;
                    trackedFeature.X = x;
                    trackedFeature.Y = y;
                    trackedFeature.Patch = ReadPatch(luma, width, x, y);
                    trackedFeature.ReferenceValid = true;
                    projected.Add(trackedFeature);
                }
                else if (!hasEvidence
                         && (trackedById.TryGetValue(pair.Value, out var hidden)
                             || previousById.TryGetValue(pair.Value, out hidden)))
                {
                    hidden.SegmentId = segment.Id;
                    hidden.SequenceIndex = pair.Key;
                    hidden.X = x;
                    hidden.Y = y;
                    hidden.Classification = GroundHypothesisClassification.Occluded;
                    hidden.MotionResidual = null;
                    hidden.PhotometricError = null;
                    projected.Add(hidden);
                }
                else
                {
                    projected.Add(new ActiveFeature
                    {
                        Id = pair.Value,
                        SegmentId = segment.Id,
                        SequenceIndex = pair.Key,
                        X = x,
                        Y = y,
                        Patch = hasEvidence ? ReadPatch(luma, width, x, y) : [],
                        Age = 1,
                        Classification = hasEvidence
                            ? GroundHypothesisClassification.Candidate
                            : GroundHypothesisClassification.Occluded,
                        ConsecutiveGroundFrames = 0,
                        ReferenceValid = hasEvidence,
                    });
                }
            }
        }
        return projected;
    }

    private void RecordReferencePatches()
    {
        foreach (var feature in active.Where(feature =>
                     feature.ReferenceValid
                     && feature.Classification != GroundHypothesisClassification.Occluded
                     && feature.Patch.Length == FeaturePixelCount))
        {
            var segment = segments.First(item => item.Id == feature.SegmentId);
            var evidence = segment.CellEvidence[feature.SequenceIndex];
            var cameraConsistent = feature.Classification is
                GroundHypothesisClassification.GroundPlane or
                GroundHypothesisClassification.GlobalMatch;
            if (evidence.ReferencePatch.Length != FeaturePixelCount)
            {
                evidence.ReferencePatch = feature.Patch.ToArray();
                evidence.ReferenceOpacity = Enumerable.Repeat(
                    cameraConsistent ? (byte)96 : (byte)32,
                    FeaturePixelCount).ToArray();
                SyncGlobalEvidence(feature, evidence);
                continue;
            }
            if (evidence.ReferenceOpacity.Length != FeaturePixelCount)
                evidence.ReferenceOpacity = Enumerable.Repeat((byte)32, FeaturePixelCount).ToArray();
            for (var index = 0; index < FeaturePixelCount; index++)
            {
                var confidence = evidence.ReferenceOpacity[index];
                var current = feature.Patch[index];
                var prior = evidence.ReferencePatch[index];
                if (feature.Classification == GroundHypothesisClassification.DepthError)
                    confidence = (byte)Math.Max(0, confidence - 48);
                else if (Math.Abs(current - prior) <= 14)
                    confidence = (byte)Math.Min(cameraConsistent ? 255 : 96,
                        confidence + (cameraConsistent ? 24 : 5));
                else if (cameraConsistent && confidence <= 8)
                    confidence = 48;
                else
                    confidence = (byte)Math.Max(0, confidence - (cameraConsistent ? 32 : 12));
                evidence.ReferencePatch[index] = current;
                evidence.ReferenceOpacity[index] = confidence;
            }
            SyncGlobalEvidence(feature, evidence);
        }
    }

    private void SyncGlobalEvidence(ActiveFeature feature, CellEvidence evidence)
    {
        if (feature.GlobalFeatureId is not { } id
            || !globalFeatures.TryGetValue(id, out var global)) return;
        global.ReferencePatch = evidence.ReferencePatch.ToArray();
        global.ReferenceOpacity = evidence.ReferenceOpacity.ToArray();
    }

    private (double Start, double End)? RectifiedCellBounds(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum)
            return null;
        globalTilePhaseX ??= minimum;
        var phase = globalTilePhaseX.Value;
        var firstIndex = (int)Math.Ceiling((minimum - phase) / FeatureSize);
        var lastIndex = (int)Math.Floor((maximum - (FeatureSize - 1) - phase) / FeatureSize);
        if (lastIndex < firstIndex) return null;
        return (phase + firstIndex * FeatureSize,
            phase + lastIndex * FeatureSize + FeatureSize - 1);
    }

    private void EnsureCellIds(Segment segment)
    {
        var minimumIndex = (int)Math.Ceiling(
            (segment.MinimumWorldX - segment.OriginWorldX) / FeatureSize);
        var maximumIndex = (int)Math.Floor(
            (segment.MaximumWorldX - (FeatureSize - 1) - segment.OriginWorldX) / FeatureSize);
        for (var index = minimumIndex; index <= maximumIndex; index++)
        {
            if (segment.CellIds.ContainsKey(index)) continue;
            segment.CellIds[index] = nextFeatureId++;
            segment.CellEvidence[index] = new CellEvidence();
        }
    }

    private static bool HasGroundEvidence(
        int x,
        int y,
        IReadOnlyList<ObservedLine> floorLines)
    {
        var end = x + FeatureSize - 1;
        return floorLines.Any(line =>
            Math.Abs(line.Row - y) <= EvidenceRowTolerance
            && line.EvidenceSpans.Any(span =>
                Math.Max(x, span.Start) <= Math.Min(end, span.EndInclusive)));
    }

    private static byte[] ReadPatch(byte[] luma, int width, int x, int y)
    {
        var patch = new byte[FeaturePixelCount];
        for (var row = 0; row < FeatureHeight; row++)
            luma.AsSpan((y + row) * width + x, FeatureWidth)
                .CopyTo(patch.AsSpan(row * FeatureWidth));
        return patch;
    }

    // Candidate bounds are already checked by both search loops. Compare the
    // image rows directly instead of allocating a 192-byte patch per candidate.
    private static double AbsolutePatchError(byte[] reference, byte[] pixels,
        int stride, int x, int y)
    {
        if (reference.Length != FeaturePixelCount) return double.PositiveInfinity;
        var error = 0;
        for (var row = 0; row < FeatureHeight; row++)
        {
            var source = (y + row) * stride + x;
            var target = row * FeatureWidth;
            for (var column = 0; column < FeatureWidth; column++)
                error += Math.Abs(reference[target + column] - pixels[source + column]);
        }
        return (double)error / FeaturePixelCount;
    }

    private static double HorizontalGap(
        double leftMinimum,
        double leftMaximum,
        double rightMinimum,
        double rightMaximum)
    {
        if (leftMinimum <= rightMaximum && rightMinimum <= leftMaximum) return 0;
        return leftMaximum < rightMinimum
            ? rightMinimum - leftMaximum - 1
            : leftMinimum - rightMaximum - 1;
    }

    private static bool Intersects(
        int x,
        int y,
        int width,
        int height,
        PixelRect rect) =>
        rect.Width > 0 && rect.Height > 0
        && (long)rect.X < (long)x + width
        && (long)rect.X + rect.Width > x
        && (long)rect.Y < (long)y + height
        && (long)rect.Y + rect.Height > y;

    private static void Paint(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        (byte R, byte G, byte B, byte A) color)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        var offset = (y * width + x) * 4;
        pixels[offset] = color.R;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.B;
        pixels[offset + 3] = color.A;
    }

    private static int Round(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);

    private static IEnumerable<(int Start, int End)> ContiguousRuns(IEnumerable<int> indices)
    {
        int? start = null;
        int? prior = null;
        foreach (var index in indices)
        {
            if (prior is not null && index != prior + 1)
            {
                yield return (start!.Value, prior.Value);
                start = index;
            }
            else start ??= index;
            prior = index;
        }
        if (prior is not null) yield return (start!.Value, prior.Value);
    }
}
