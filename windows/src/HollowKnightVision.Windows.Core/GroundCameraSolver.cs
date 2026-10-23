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
public sealed partial class GroundCameraSolver
{
    public static readonly CameraVector HackerCalibratedAnchorScale = new(.9929, .9947);
    public static readonly CameraVector HackerCalibratedRecentScale = new(.9809, .9923);
    private readonly bool subpixelEnabled, keyframeSubpixelEnabled, usesAdaptiveRecentMotionScale;
    private readonly CameraVector anchorImageMotionScale, recentImageMotionScale;
    public GroundCameraSolver(bool subpixelEnabled = false, bool keyframeSubpixelEnabled = false,
        bool calibratedMotionEnabled = false, CameraVector? imageMotionScale = null, CameraVector? recentImageMotionScale = null)
    {
        this.subpixelEnabled = subpixelEnabled; this.keyframeSubpixelEnabled = keyframeSubpixelEnabled;
        usesAdaptiveRecentMotionScale = calibratedMotionEnabled && imageMotionScale is null && recentImageMotionScale is null;
        anchorImageMotionScale = imageMotionScale ?? (calibratedMotionEnabled ? HackerCalibratedAnchorScale : new(1, 1));
        this.recentImageMotionScale = recentImageMotionScale ?? imageMotionScale ?? (calibratedMotionEnabled ? HackerCalibratedRecentScale : new(1, 1));
    }
    public sealed record Solution(CameraVector ImageTranslation, int Support, double Error, bool ReferenceTrusted = true,
        double? ReferenceTimestamp = null, CameraVector? ReferencePosition = null,
        CameraVector? MatchedImageDisplacement = null, string? MatchSource = null);
    private sealed record Reference(byte[] Pixels, GroundTextureLine[] Lines, StencilRect[] Exclusions,
        double Timestamp, CameraVector Position, bool PoseTrusted = true);
    private Reference? reference, recentReference, provisionalReference;
    private List<Reference> spatialReferences = [];
    private CameraVector velocity, imagePosition;
    private double? lastAcceptedTimestamp;
    private int frameWidth, frameHeight;
    private byte[]? lastObservedPixels;
    public bool Seeded { get; private set; }
    public bool DistinctFrame { get; private set; } = true;
    public bool HasProvisionalReference => provisionalReference is not null;
    public CameraVector ImagePosition => imagePosition;
    private sealed record State(Reference? Anchor, Reference? Recent, Reference? Provisional, CameraVector Velocity,
        CameraVector Position, double? Timestamp, Reference[] Spatial);
    private (double Timestamp, State State)? lastAttempt;
    private State CurrentState
    {
        get => new(reference, recentReference, provisionalReference, velocity, imagePosition, lastAcceptedTimestamp, spatialReferences.ToArray());
        set
        {
            reference = value.Anchor; recentReference = value.Recent; provisionalReference = value.Provisional;
            velocity = value.Velocity; imagePosition = value.Position; lastAcceptedTimestamp = value.Timestamp; spatialReferences = [.. value.Spatial];
        }
    }
    public void Reset()
    {
        reference = recentReference = provisionalReference = null; spatialReferences.Clear();
        velocity = imagePosition = CameraVector.Zero; lastAcceptedTimestamp = null;
        frameWidth = frameHeight = 0; Seeded = false; lastAttempt = null; lastObservedPixels = null; DistinctFrame = true;
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
        imagePosition = Add(imagePosition, new(-cameraCorrection.X, cameraCorrection.Y));
        if (Length(cameraCorrection) > 4) velocity = CameraVector.Zero;
        var anchor = CopyReference(currentPixels, lines, exclusions, timestamp, imagePosition);
        reference = recentReference = anchor; provisionalReference = null; lastAcceptedTimestamp = timestamp;
        spatialReferences.RemoveAll(value => Length(Subtract(value.Position, imagePosition)) <= 2); AppendSpatial(anchor);
    }
    public void AcceptFeatureRecovery(byte[] pixels, int width, int height, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions, CameraVector imageTranslation)
    {
        if (!Valid(pixels, width, height, timestamp) || !Finite(imageTranslation) || width != frameWidth || height != frameHeight) return;
        var elapsed = Math.Max(1.0 / 240, timestamp - (lastAcceptedTimestamp ?? timestamp));
        velocity = new(velocity.X * .5 + imageTranslation.X / elapsed * .5, velocity.Y * .5 + imageTranslation.Y / elapsed * .5);
        imagePosition = Add(imagePosition, imageTranslation); lastAcceptedTimestamp = timestamp;
        var recovered = CopyReference(pixels, lines, exclusions, timestamp, imagePosition);
        reference = recentReference = recovered; provisionalReference = null;
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
        var measured = new CameraVector(imagePosition.X - correction.X, imagePosition.Y + correction.Y);
        if (lastAttempt is { } attempt && Math.Abs(attempt.Timestamp - timestamp) < .000001) CurrentState = attempt.State;
        var elapsed = timestamp - (lastAcceptedTimestamp ?? timestamp);
        if (elapsed > 0) velocity = new(velocity.X * .5 + (measured.X - imagePosition.X) / elapsed * .5,
            velocity.Y * .5 + (measured.Y - imagePosition.Y) / elapsed * .5);
        imagePosition = measured; lastAcceptedTimestamp = timestamp;
        if (!preservesTrustedHistory)
        {
            var initial = CopyReference(pixels, lines, exclusions, timestamp, measured);
            reference = recentReference = initial; provisionalReference = null; spatialReferences = [initial];
        }
        else provisionalReference = CopyReference(pixels, lines, exclusions, timestamp, measured, trusted: false);
        Seeded = false; lastAttempt = null;
    }
    public void PromoteProvisionalReference(byte[] pixels, int width, int height, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions)
    {
        if (provisionalReference is null || !Valid(pixels, width, height, timestamp) || frameWidth != width || frameHeight != height) return;
        var trusted = CopyReference(pixels, lines, exclusions, timestamp, imagePosition);
        reference = recentReference = trusted; provisionalReference = null; AppendSpatial(trusted); Seeded = false;
    }
    public void DeferReferenceUpdates(double timestamp)
    {
        if (lastAttempt is not { } attempt || Math.Abs(attempt.Timestamp - timestamp) >= .000001) return;
        reference = attempt.State.Anchor; recentReference = attempt.State.Recent; provisionalReference = attempt.State.Provisional;
        spatialReferences = [.. attempt.State.Spatial];
    }

    public Solution? Solve(byte[] pixels, int width, int height, IReadOnlyList<GroundTextureLine> lines,
        double timestamp, IReadOnlyList<StencilRect> exclusions)
    {
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
        var anchored = SolveReference(pixels, width, height, ownedLines, timestamp, ownedExclusions, source);
        if (anchored is not null && (Seeded || anchored.Error < 10 || anchored.Support >= 10))
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
            var stronger = (preciseSparse || broadConsensus) && timestamp - alternative.Timestamp <= .1 && distance <= 32;
            return ((result.Support >= 6 || stronger) && result.Error + 1 < anchor.Error * .65 && (stronger || distance <= 6), stronger);
        }
        if (before.Anchor is not { } anchor || before.Recent is not { } recent || anchor.Timestamp == recent.Timestamp)
        {
            if (anchored is null) return SolveProvisional();
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
        return SolveProvisional();
    }

    private CameraVector MotionScale(string source, CameraVector displacement)
    {
        if (source is not ("recent" or "provisional")) return anchorImageMotionScale;
        if (!usesAdaptiveRecentMotionScale) return recentImageMotionScale;
        return Length(displacement) switch { < 8 => new(.9920, .9982), < 16 => new(.9800, .9916), < 32 => new(.9773, .9934), _ => new(.9900, .9951) };
    }
    private sealed class Patch
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
    internal static byte[]? ReadPatch(byte[] pixels, int width, int height, int x, int y)
    {
        if (width <= 0 || height <= 0 || pixels.LongLength < (long)width * height || x < 0 || (long)x + 16 > width || y < 0 || (long)y + 12 > height) return null;
        var patch = new byte[192]; for (var row = 0; row < 12; row++) pixels.AsSpan((y + row) * width + x, 16).CopyTo(patch.AsSpan(row * 16));
        return patch;
    }
}
