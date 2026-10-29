namespace HollowKnightVision.Windows.Core;

public static class GroundReferencePromotionPolicy
{
    public static bool Permits(int support, double error) => (support >= 6 && error <= 18) || (support >= 4 && error <= 10);
}
public static class GroundRecentReferenceCadence
{
    public const double MinimumAge = .08, MinimumTravel = 16;
    public static bool ShouldRefresh(double age, double travel) => age >= MinimumAge || travel >= MinimumTravel;
}
public static class GroundPoseAcceptancePolicy
{
    public static bool Rejects(int support, double error) => (support <= 4 && error > 14) || (support < 8 && error > 18) || (support < 12 && error > 20);
}
public static class GroundPoseInnovationPolicy
{
    public static bool Rejects(int support, double error, double innovation) => support < 12 && error > 19 && innovation > 8;
}
public static class GroundProvisionalPromotionPolicy
{
    public const double MinimumDuration = .20;
    public const int MinimumConsecutiveMatches = 12;
    public static bool Permits(double elapsed, int consecutiveMatches, int support, double error) =>
        elapsed >= MinimumDuration && consecutiveMatches >= MinimumConsecutiveMatches && GroundReferencePromotionPolicy.Permits(support, error);
}

// Unlike PixelSpan, projected texture ranges can extend outside the image.
public readonly record struct GroundTextureSpan(int Start, int EndInclusive);
public sealed record GroundTextureLine(int Row, int StartX, int EndX, IReadOnlyList<GroundTextureSpan>? EvidenceRanges = null)
{
    public static GroundTextureLine FromDetected(DetectedFloorLine line) => new(line.Row, line.XSpan.Start, line.XSpan.EndInclusive,
        line.EvidenceSpans.Select(span => new GroundTextureSpan(span.Start, span.EndInclusive)).ToArray());
}

/// <summary>
/// Mac ground-texture registration: rows propose Y; independent, disjoint 16x12
/// patches verify both axes. Image coordinates are top-origin. Seeding is not
/// verified placement, and provisional references must never authorize atlas writes.
/// </summary>
public sealed record GroundCameraSolveProfile(int ReferenceSearches, double PreparationMilliseconds,
    double SearchMilliseconds, double ConsensusMilliseconds, double SubpixelMilliseconds);

public sealed partial class GroundCameraSolver
{
    private int profiledReferenceSearches;
    private double profiledPreparation, profiledSearch, profiledConsensus, profiledSubpixel;
    public GroundCameraSolveProfile LastProfile => new(profiledReferenceSearches, profiledPreparation,
        profiledSearch, profiledConsensus, profiledSubpixel);
    public static readonly CameraVector HackerCalibratedAnchorScale = new(.9929, .9947);
    public static readonly CameraVector HackerCalibratedRecentScale = new(.9809, .9923);
    public static readonly CameraVector BroadCalibratedCameraScale = new(.9956, .9947);
    private readonly CameraVector cameraCoordinateScale;
    private readonly bool subpixelEnabled, keyframeSubpixelEnabled, usesAdaptiveRecentMotionScale;
    private readonly CameraVector anchorImageMotionScale, recentImageMotionScale;
    private readonly GroundComputeBackend matcherBackend;
    private readonly int maximumLocalSearchRadius;
    private readonly bool broadSearchEveryFrame;
    private readonly bool fineParallaxConsensus = true;
    private readonly bool symmetricSubpixel = true;
    private Reference? observationRecentReference;
    private int consecutiveRecentObservations;

    internal void BeginObservation() => observationRecentReference = recentReference;
    internal void EndObservation(string? matchSource)
    {
        // Repeated short-baseline handoffs compound subpixel errors when the
        // durable anchor is unavailable. Keep a longer overlapping reference
        // during sustained fallback, while still matching every current frame.
        if (broadSearchEveryFrame && matchSource == "recent" && consecutiveRecentObservations >= 3
            && observationRecentReference is { } before && recentReference is { } after
            && after.Timestamp != before.Timestamp && after.Timestamp - before.Timestamp < .2
            && Length(Subtract(after.Position, before.Position)) < 48)
            recentReference = before;
        consecutiveRecentObservations = matchSource == "recent" ? Math.Min(100, consecutiveRecentObservations + 1) : 0;
        observationRecentReference = null;
    }
    private readonly List<GroundPatchSearchCandidate> searchCandidateBuffer = [];
    private GroundPatchSearchSample[] searchSampleBuffer = [];
    private uint[] searchValidityBuffer = [];
    private SearchSampleRange[] searchSampleRanges = [];
    private readonly SearchSampleLayout sampleLayout = new();
    private sealed class SearchSampleLayout
    {
        public readonly int[] X = new int[32], Y = new int[32];
        public int Count;
        public bool Matches(IReadOnlyList<GroundPatchSearchSource> sources)
        {
            if (sources.Count != Count) return false;
            for (var i = 0; i < Count; i++)
                if (sources[i].X != X[i] || sources[i].Y != Y[i]) return false;
            return true;
        }
    }
    private sealed class SearchSampleRange(GroundPatchSearchSample[] buffer, int offset, SearchSampleLayout layout) : IValidatedGroundPatchSamples
    {
        public int Count { get; set; }
        public int Width, Height, Sources;
        public object LayoutIdentity => layout;
        public bool MatchesSourcePositions(IReadOnlyList<GroundPatchSearchSource> sources) => layout.Matches(sources);
        public uint EligibleSources { get; set; }
        public uint MatchedSources { get; set; }
        public int DisplacementX { get; set; }
        public int DisplacementY { get; set; }
        public bool IsValidatedFor(int width, int height, int sources) =>
            Width == width && Height == height && Sources == sources;
        public GroundPatchSearchSample this[int index] => (uint)index < (uint)Count
            ? buffer[offset + index] : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<GroundPatchSearchSample> GetEnumerator()
        { for (var i = 0; i < Count; i++) yield return buffer[offset + i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Search consumes these views synchronously; no reference/pixel journal
    // retains them. Reserve broad capacity before any measured observation.
    private List<GroundPatchSearchCandidate> PrepareCandidates(int count)
    {
        var samples = checked(count * 32);
        if (searchSampleBuffer.Length < samples)
        {
            searchSampleBuffer = new GroundPatchSearchSample[samples];
            searchSampleRanges = new SearchSampleRange[count];
            for (var i = 0; i < count; i++) searchSampleRanges[i] = new(searchSampleBuffer, i * 32, sampleLayout);
        }
        if (searchCandidateBuffer.Capacity < count) searchCandidateBuffer.Capacity = count;
        searchCandidateBuffer.Clear();
        return searchCandidateBuffer;
    }
    public GroundPatchSearchExecution? LastPatchSearchExecution { get; private set; }
    public GroundCameraSolver(bool subpixelEnabled = false, bool keyframeSubpixelEnabled = false,
        bool calibratedMotionEnabled = false, CameraVector? imageMotionScale = null,
        CameraVector? recentImageMotionScale = null,
        GroundComputeBackend matcherBackend = GroundComputeBackend.Cpu,
        int maximumLocalSearchRadius = 96, bool broadSearchEveryFrame = false, CameraVector? cameraCoordinateScale = null)
    {
        this.subpixelEnabled = subpixelEnabled; this.keyframeSubpixelEnabled = keyframeSubpixelEnabled;
        // Keep established registration coordinates independent of output
        // calibration: changing the projection must not move reference cadence
        // thresholds or change which pixel hypotheses are searched. Inbound
        // camera corrections use the inverse conversion at the same boundary.
        this.cameraCoordinateScale = cameraCoordinateScale ??
            (broadSearchEveryFrame && calibratedMotionEnabled && imageMotionScale is null && recentImageMotionScale is null
                ? new(BroadCalibratedCameraScale.X / HackerCalibratedAnchorScale.X,
                    BroadCalibratedCameraScale.Y / HackerCalibratedAnchorScale.Y) : new(1, 1));
        if (!Finite(this.cameraCoordinateScale) || this.cameraCoordinateScale.X <= 0 || this.cameraCoordinateScale.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(cameraCoordinateScale));
        // Independent dense-patch consensus removes the sparse matcher's
        // displacement-dependent bias. Use the same ground-plane conversion
        // across reference handoffs instead of shrinking recent motion again.
        usesAdaptiveRecentMotionScale = !broadSearchEveryFrame && calibratedMotionEnabled && imageMotionScale is null && recentImageMotionScale is null;
        anchorImageMotionScale = imageMotionScale ?? (calibratedMotionEnabled ? HackerCalibratedAnchorScale : new(1, 1));
        this.recentImageMotionScale = recentImageMotionScale ?? imageMotionScale ?? (calibratedMotionEnabled ? (broadSearchEveryFrame ? HackerCalibratedAnchorScale : HackerCalibratedRecentScale) : new(1, 1));
        this.matcherBackend = matcherBackend;
        this.maximumLocalSearchRadius = Math.Max(16, maximumLocalSearchRadius);
        this.broadSearchEveryFrame = broadSearchEveryFrame;
        if (broadSearchEveryFrame)
        {
            var columns = 2 * Math.Min(192, this.maximumLocalSearchRadius) + 1;
            PrepareCandidates(Math.Max(28 * columns, 193 * 193));
            searchValidityBuffer = new uint[columns];
        }
    }
    public sealed record Solution(CameraVector ImageTranslation, int Support, double Error, bool ReferenceTrusted = true,
        double? ReferenceTimestamp = null, CameraVector? ReferencePosition = null,
        CameraVector? MatchedImageDisplacement = null, string? MatchSource = null)
    {
        public bool IndependentTextureConsensus { get; init; }
        public bool UniqueLocalTextureMatch { get; init; }
    }
    private sealed record Reference(byte[] Pixels, GroundTextureLine[] Lines, StencilRect[] Exclusions,
        double Timestamp, CameraVector Position, bool PoseTrusted = true);
    private Reference? reference, recentReference, provisionalReference, latestVerifiedReference;
    private List<Reference> spatialReferences = [];
    private CameraVector velocity, imagePosition;
    private double? lastAcceptedTimestamp;
    private int frameWidth, frameHeight;
    private byte[]? lastObservedPixels;
    public bool Seeded { get; private set; }
    public bool DistinctFrame { get; private set; } = true;
    public bool HasProvisionalReference => provisionalReference is not null;
    public CameraVector ImagePosition => ToCameraCoordinates(imagePosition);
    private CameraVector ToCameraCoordinates(CameraVector value) => new(value.X * cameraCoordinateScale.X, value.Y * cameraCoordinateScale.Y);
    private CameraVector ToRegistrationCoordinates(CameraVector value) => new(value.X / cameraCoordinateScale.X, value.Y / cameraCoordinateScale.Y);
    private sealed record State(Reference? Anchor, Reference? Recent, Reference? Provisional, CameraVector Velocity,
        CameraVector Position, double? Timestamp, Reference[] Spatial, Reference? LatestVerified);
    private (double Timestamp, State State)? lastAttempt;
    private State CurrentState
    {
        get => new(reference, recentReference, provisionalReference, velocity, imagePosition, lastAcceptedTimestamp, spatialReferences.ToArray(), latestVerifiedReference);
        set
        {
            reference = value.Anchor; recentReference = value.Recent; provisionalReference = value.Provisional;
            velocity = value.Velocity; imagePosition = value.Position; lastAcceptedTimestamp = value.Timestamp; spatialReferences = [.. value.Spatial]; latestVerifiedReference = value.LatestVerified;
        }
    }
    public void Reset()
    {
        reference = recentReference = provisionalReference = latestVerifiedReference = null; spatialReferences.Clear();
        observationRecentReference = null; consecutiveRecentObservations = 0;
        velocity = imagePosition = CameraVector.Zero; lastAcceptedTimestamp = null;
        frameWidth = frameHeight = 0; Seeded = false; lastAttempt = null; lastObservedPixels = null; DistinctFrame = true;
        LastPatchSearchExecution = null;
    }

    private static bool Valid(byte[] pixels, int width, int height, double timestamp) =>
        width > 0 && height > 0 && pixels.LongLength == (long)width * height && double.IsFinite(timestamp);
    private static bool Finite(CameraVector vector) => double.IsFinite(vector.X) && double.IsFinite(vector.Y);
    private static double Length(CameraVector vector) => Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
    private static CameraVector Add(CameraVector a, CameraVector b) => new(a.X + b.X, a.Y + b.Y);
    private static CameraVector Subtract(CameraVector a, CameraVector b) => new(a.X - b.X, a.Y - b.Y);
    private static GroundTextureLine[] FreezeLines(IReadOnlyList<GroundTextureLine> lines) => lines.Select(line => line with
        { EvidenceRanges = (line.EvidenceRanges ?? [new GroundTextureSpan(line.StartX, line.EndX)]).ToArray() }).ToArray();
    private static Reference CopyReference(byte[] pixels, IReadOnlyList<GroundTextureLine> lines, IReadOnlyList<StencilRect> exclusions,
        double timestamp, CameraVector position, bool trusted = true) => new(pixels.ToArray(), FreezeLines(lines), exclusions.ToArray(), timestamp, position, trusted);
    private void AppendSpatial(Reference value) { spatialReferences.Add(value); if (spatialReferences.Count > 12) spatialReferences.RemoveAt(0); }
    private int NearestSpatial(CameraVector position) => spatialReferences.Count == 0 ? -1 : Enumerable.Range(0, spatialReferences.Count)
        .MinBy(index => Length(Subtract(spatialReferences[index].Position, position)));

    public void ApplyGlobalCorrection(CameraVector cameraCorrection, byte[] currentPixels, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions)
    {
        if (!Valid(currentPixels, frameWidth, frameHeight, timestamp) || !Finite(cameraCorrection)) return;
        cameraCorrection = ToRegistrationCoordinates(cameraCorrection);
        // A correction establishes a new coordinate basis. Never restore a
        // recent reference from before that correction at observation end.
        observationRecentReference = null; consecutiveRecentObservations = 0;
        imagePosition = Add(imagePosition, new(-cameraCorrection.X, cameraCorrection.Y));
        if (Length(cameraCorrection) > 4) velocity = CameraVector.Zero;
        var anchor = CopyReference(currentPixels, lines, exclusions, timestamp, imagePosition);
        reference = recentReference = latestVerifiedReference = anchor; provisionalReference = null; lastAcceptedTimestamp = timestamp;
        spatialReferences.RemoveAll(value => Length(Subtract(value.Position, imagePosition)) <= 2); AppendSpatial(anchor);
    }
    public void AcceptFeatureRecovery(byte[] pixels, int width, int height, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions, CameraVector imageTranslation)
    {
        if (!Valid(pixels, width, height, timestamp) || !Finite(imageTranslation) || width != frameWidth || height != frameHeight) return;
        imageTranslation = ToRegistrationCoordinates(imageTranslation);
        var elapsed = Math.Max(1.0 / 240, timestamp - (lastAcceptedTimestamp ?? timestamp));
        velocity = new(velocity.X * .5 + imageTranslation.X / elapsed * .5, velocity.Y * .5 + imageTranslation.Y / elapsed * .5);
        imagePosition = Add(imagePosition, imageTranslation); lastAcceptedTimestamp = timestamp;
        var recovered = CopyReference(pixels, lines, exclusions, timestamp, imagePosition);
        reference = recentReference = latestVerifiedReference = recovered; provisionalReference = null;
        var nearest = NearestSpatial(imagePosition);
        if (nearest >= 0 && Length(Subtract(spatialReferences[nearest].Position, imagePosition)) <= 2) spatialReferences[nearest] = recovered;
        else AppendSpatial(recovered);
        Seeded = false;
    }
    public void FollowMeasuredCamera(CameraVector correction, byte[] pixels, int width, int height,
        IReadOnlyList<GroundTextureLine> lines, double timestamp, IReadOnlyList<StencilRect> exclusions, bool preservesTrustedHistory = true)
    {
        if (!Valid(pixels, width, height, timestamp) || !Finite(correction)) return;
        if (frameWidth != width || frameHeight != height || reference is null)
        { Reset(); _ = Solve(pixels, width, height, lines, timestamp, exclusions); return; }
        correction = ToRegistrationCoordinates(correction);
        var measured = new CameraVector(imagePosition.X - correction.X, imagePosition.Y + correction.Y);
        if (lastAttempt is { } attempt && Math.Abs(attempt.Timestamp - timestamp) < .000001) CurrentState = attempt.State;
        var elapsed = timestamp - (lastAcceptedTimestamp ?? timestamp);
        if (elapsed > 0) velocity = new(velocity.X * .5 + (measured.X - imagePosition.X) / elapsed * .5,
            velocity.Y * .5 + (measured.Y - imagePosition.Y) / elapsed * .5);
        imagePosition = measured; lastAcceptedTimestamp = timestamp;
        if (!preservesTrustedHistory)
        {
            var initial = CopyReference(pixels, lines, exclusions, timestamp, measured);
            reference = recentReference = initial; latestVerifiedReference = null; provisionalReference = null; spatialReferences = [initial];
        }
        else provisionalReference = CopyReference(pixels, lines, exclusions, timestamp, measured, trusted: false);
        Seeded = false; lastAttempt = null;
    }
    public void PromoteProvisionalReference(byte[] pixels, int width, int height, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions)
    {
        if (provisionalReference is null || !Valid(pixels, width, height, timestamp) || frameWidth != width || frameHeight != height) return;
        var trusted = CopyReference(pixels, lines, exclusions, timestamp, imagePosition);
        reference = recentReference = latestVerifiedReference = trusted; provisionalReference = null; AppendSpatial(trusted); Seeded = false;
    }
    public void DeferReferenceUpdates(double timestamp)
    {
        if (lastAttempt is not { } attempt || Math.Abs(attempt.Timestamp - timestamp) >= .000001) return;
        reference = attempt.State.Anchor; recentReference = attempt.State.Recent; provisionalReference = attempt.State.Provisional;
        spatialReferences = [.. attempt.State.Spatial]; latestVerifiedReference = attempt.State.LatestVerified;
    }

    public Solution? Solve(byte[] pixels, int width, int height, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions)
    {
        var result = SolveRegistration(pixels, width, height, lines, timestamp, exclusions);
        return result is null ? null : result with {
            ImageTranslation = ToCameraCoordinates(result.ImageTranslation),
            ReferencePosition = result.ReferencePosition is { } position ? ToCameraCoordinates(position) : null
        };
    }

    private Solution? SolveRegistration(byte[] pixels, int width, int height, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions)
    {
        profiledReferenceSearches = 0;
        profiledPreparation = profiledSearch = profiledConsensus = profiledSubpixel = 0;
        ArgumentNullException.ThrowIfNull(pixels); ArgumentNullException.ThrowIfNull(lines); ArgumentNullException.ThrowIfNull(exclusions);
        if (!Valid(pixels, width, height, timestamp)) return null;
        // Invalidate before snapshotting a probe transaction. Otherwise a failed
        // first solve after resize could restore references from the old size.
        if (frameWidth != width || frameHeight != height)
        { Reset(); frameWidth = width; frameHeight = height; }
        // Swift input arrays have value semantics. Own snapshots before retaining
        // them so pooled capture buffers cannot rewrite historical evidence.
        pixels = pixels.ToArray(); var ownedLines = FreezeLines(lines); var ownedExclusions = exclusions.ToArray();
        DistinctFrame = lastObservedPixels is null || !lastObservedPixels.AsSpan().SequenceEqual(pixels);
        lastObservedPixels = pixels; lastAttempt = (timestamp, CurrentState);
        double Distance(Reference value) => Math.Abs(value.Position.X - imagePosition.X) + Math.Abs(value.Position.Y - imagePosition.Y);
        var source = "anchor";
        var nearby = spatialReferences.MinBy(Distance);
        if (reference is { } current && nearby is not null && Distance(nearby) + Math.Min(width, height) * .04 < Distance(current))
        { reference = nearby; source = "spatial-anchor"; }
        var before = CurrentState;
        Solution? SolveProvisional()
        {
            CurrentState = before;
            if (before.Provisional is not { } provisional) return null;
            reference = provisional;
            var result = SolveReference(pixels, width, height, ownedLines, timestamp, ownedExclusions, "provisional");
            if (result is null) { CurrentState = before; return null; }
            reference = before.Anchor; recentReference = before.Recent; spatialReferences = [.. before.Spatial];
            return result;
        }
        Solution? SolveFallback()
        {
            if (!broadSearchEveryFrame) return SolveProvisional();
            // A provisional match inherits the coarse fallback's uncertain
            // origin. Exhaust trusted image references before returning it.
            CurrentState = before;
            var textureReference = before.Recent ?? before.Anchor;
            if (textureReference is null || !textureReference.PoseTrusted) return SolveProvisional();
            reference = textureReference;
            var result = SolveReference(pixels, width, height, ownedLines, timestamp, ownedExclusions,
                "texture-recent", textureFallback: true);
            if (result is null && before.LatestVerified is { } fresh
                && fresh.Timestamp > textureReference.Timestamp && timestamp - fresh.Timestamp <= .1)
            {
                // Preserve the durable baseline during normal matching. If it
                // loses overlap, try the last independently accepted image.
                // Failed/provisional placements cannot populate this reference.
                CurrentState = before;
                reference = fresh;
                result = SolveReference(pixels, width, height, ownedLines, timestamp, ownedExclusions,
                    "texture-fresh", textureFallback: true);
            }
            if (result is null) return SolveProvisional();
            reference = before.Anchor; provisionalReference = null;
            return result;
        }
        var anchored = SolveReference(pixels, width, height, ownedLines, timestamp, ownedExclusions, source);
        // A small old-anchor consensus can be a coherent foreground layer.
        // In broad mode compare it with recent ground before trusting its low
        // photometric error or promoting its coordinate into the next anchor.
        if (anchored is not null && (Seeded || anchored.Support >= 10
            || (anchored.Error < 10 && (!broadSearchEveryFrame || anchored.Support > 8))))
        { provisionalReference = null; return anchored; }
        var anchoredState = CurrentState;
        (bool Use, bool Stronger) AlternativeDecision(Solution? result, Solution anchor, Reference alternative)
        {
            if (result is null) return (false, false);
            var preciseSparse = anchor.Support <= 6 && result.Support >= Math.Max(5, anchor.Support)
                && result.Error <= 4 && result.Error * 3 + 1 < anchor.Error;
            var broadConsensus = anchor.Support <= 8 && result.Support >= Math.Max(10, anchor.Support * 2)
                && result.Error <= 10 && result.Error * 2 + 1 < anchor.Error;
            var distance = Length(Subtract(result.ImageTranslation, anchor.ImageTranslation));
            // Subpixel agreement is not evidence that the old anchor is wrong.
            // Keep its longer baseline instead of repeatedly rebasing onto
            // short recent matches and accumulating rounding/parallax bias.
            if (broadSearchEveryFrame && anchor.Error < 10 && distance < 1)
                return (false, false);
            var stronger = (preciseSparse || broadConsensus) && timestamp - alternative.Timestamp <= .1 && distance <= 32;
            var independentRecentConsensus = broadSearchEveryFrame && anchor.Error < 10 && anchor.Support <= 8
                && result.Support >= Math.Max(12, anchor.Support * 2)
                && result.Error <= Math.Min(10, anchor.Error + 1)
                && timestamp - alternative.Timestamp <= .2 && distance <= 6;
            return (independentRecentConsensus || ((result.Support >= 6 || stronger)
                && result.Error + 1 < anchor.Error * .65 && (stronger || distance <= 6)),
                stronger || independentRecentConsensus);
        }
        if (before.Anchor is not { } anchor || before.Recent is not { } recent || anchor.Timestamp == recent.Timestamp)
        {
            if (anchored is null) return SolveFallback();
            var provisional = before.Provisional; var candidate = SolveProvisional();
            if (provisional is not null && AlternativeDecision(candidate, anchored, provisional).Use) return candidate;
            CurrentState = anchoredState; provisionalReference = null; return anchored;
        }
        CurrentState = before; reference = recent;
        var recentResult = SolveReference(pixels, width, height, ownedLines, timestamp, ownedExclusions, "recent");
        if (anchored is not null)
        {
            var decision = AlternativeDecision(recentResult, anchored, recent);
            if (!decision.Use) { CurrentState = anchoredState; provisionalReference = null; return anchored; }
            if (decision.Stronger) { reference = recent.PoseTrusted ? recentReference : anchor; provisionalReference = null; return recentResult; }
        }
        if (recentResult is not null)
        {
            reference = recent.PoseTrusted && Math.Abs(imagePosition.X - anchor.Position.X) > width * .35 ? recentReference : anchor;
            provisionalReference = null; return recentResult;
        }
        return SolveFallback();
    }

    private CameraVector MotionScale(string source, CameraVector displacement)
    {
        if (source is not ("recent" or "provisional")) return anchorImageMotionScale;
        if (!usesAdaptiveRecentMotionScale) return recentImageMotionScale;
        return Length(displacement) switch { < 8 => new(.9920, .9982), < 16 => new(.9800, .9916), < 32 => new(.9773, .9934), _ => new(.9900, .9951) };
    }
    internal sealed class Patch
    {
        public int X { get; } public int Y { get; } public byte[] Pixels { get; }
        public double Mean { get; } public double Energy { get; }
        public Patch(int x, int y, byte[] pixels)
        {
            X = x; Y = y; Pixels = pixels; double sum = 0, squared = 0;
            foreach (var pixel in pixels) { sum += pixel; squared += (double)pixel * pixel; }
            var count = Math.Max(1, pixels.Length); Mean = sum / count; Energy = Math.Max(0, squared / count - Mean * Mean);
        }
    }
    public static double PatchError(byte[] reference, byte[] target) => PatchError(new Patch(0, 0, reference), new Patch(0, 0, target));
    private static double PatchError(Patch source, Patch target)
    {
        if (source.Pixels.Length == 0 || source.Pixels.Length != target.Pixels.Length || target.Energy < Math.Max(1, source.Energy * .1)) return double.PositiveInfinity;
        var gain = Math.Clamp(Math.Sqrt(source.Energy / target.Energy), .5, 2); double error = 0;
        for (var i = 0; i < source.Pixels.Length; i++) error += Math.Abs(source.Pixels[i] - source.Mean - (target.Pixels[i] - target.Mean) * gain);
        return error / source.Pixels.Length;
    }
    // Same double-precision gain-normalized score as the array reference path,
    // but reuse source statistics and read the target in place during refinement.
    internal static double PatchError(Patch source, byte[] pixels, int width, int x, int y)
    {
        if (source.Pixels.Length != 192) return double.PositiveInfinity;
        double sum = 0, squared = 0;
        for (var row = 0; row < 12; row++)
        for (var column = 0; column < 16; column++)
        {
            var value = pixels[(y + row) * width + x + column];
            sum += value; squared += (double)value * value;
        }
        var mean = sum / 192;
        var energy = Math.Max(0, squared / 192 - mean * mean);
        if (energy < Math.Max(1, source.Energy * .1)) return double.PositiveInfinity;
        var gain = Math.Clamp(Math.Sqrt(source.Energy / energy), .5, 2);
        double error = 0;
        for (var row = 0; row < 12; row++)
        for (var column = 0; column < 16; column++)
            error += Math.Abs(source.Pixels[row * 16 + column] - source.Mean
                - (pixels[(y + row) * width + x + column] - mean) * gain);
        return error / 192;
    }

    internal static byte[]? ReadPatch(byte[] pixels, int width, int height, int x, int y)
    {
        if (width <= 0 || height <= 0 || pixels.LongLength < (long)width * height || x < 0 || (long)x + 16 > width || y < 0 || (long)y + 12 > height) return null;
        var patch = new byte[192]; for (var row = 0; row < 12; row++) pixels.AsSpan((y + row) * width + x, 16).CopyTo(patch.AsSpan(row * 16));
        return patch;
    }
}
