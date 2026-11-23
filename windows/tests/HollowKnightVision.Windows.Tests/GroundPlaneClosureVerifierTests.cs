using HollowKnightVision.Windows.Core;

internal static class GroundPlaneClosureVerifierTests
{
    internal static void TestMeasuredFloorRefinesBroadSceneryProposal()
    {
        var result = GroundPlaneClosureVerifier.Verify(Image(), Ground(30), new(100, 200),
            Image(dx: 20, dy: 10), Ground(20), new(123, 208), 320);
        Require(result is { CameraPose: { X: 120, Y: 210 }, SupportedPatches: >= 4,
            HorizontalSpan: > 64, MeanCorrelation: > 0.99 },
            "Floor texture should refine both axes of the proposed position.");
    }

    internal static void TestBackgroundDisplacementDoesNotEstablishGroundConnection() =>
        Require(Verify(new(60, 10)) is null, "An incorrect background displacement must fail.");

    internal static void TestBlackAndPeriodicFloorsAreNotUniqueEvidence()
    {
        foreach (var pattern in new[] { 1, 2 })
        {
            var result = GroundPlaneClosureVerifier.Verify(Image(pattern: pattern), Ground(30), default,
                Image(dx: 20, dy: 10, pattern: pattern), Ground(20), new(20, 10), 320);
            Require(result is null, "Black or repeating floor texture must fail.");
        }
    }

    internal static void TestObservedSpansExcludeMovingOccluder()
    {
        var result = GroundPlaneClosureVerifier.Verify(Image(), Ground(30), default,
            Image(dx: 20, dy: 10, black: (100, 200)), Ground(20, (0, 90), (210, 320)), new(22, 10), 320);
        Require(result is { CameraPose: { X: 20, Y: 10 }, HorizontalSpan: > 150 },
            "Visible floor spans should verify the same position on both sides of an occluder.");
    }

    internal static void TestUnobservedOrWrongElevationFloorCannotValidateProposal()
    {
        foreach (var current in new IReadOnlyList<LiveGroundLineEvidence>[] { [], Ground(50), Ground(20, (0, 20)) })
        {
            Require(GroundPlaneClosureVerifier.Verify(Image(), Ground(30), default,
                Image(dx: 20, dy: 10), current, new(20, 10), 320) is null,
                "Missing floor texture at the proposed elevation must fail.");
        }
    }

    internal static void TestSearchBoundaryDoesNotAcceptClippedOptimum() =>
        Require(Verify(new(24, 10)) is null, "A best match on the boundary can conceal a better placement outside it.");

    internal static void TestPatchProposalSurvivesExposureChange()
    {
        var saved = Image();
        var current = Image();
        for (var i = 0; i < current.Pixels.Length; i += BgraFrame.BytesPerPixel)
        {
            var dim = (byte)(20 + current.Pixels[i] / 4);
            current.Pixels[i] = current.Pixels[i + 1] = current.Pixels[i + 2] = dim;
        }
        var pose = new AtlasPoint(80, 20);
        var matcher = new GlobalFeatureMatcher();
        var reference = matcher.MakeReference(saved, pose, 320);
        Require(reference is not null && matcher.Match(current, [reference], currentSolveWidth: 320) is null,
            "The fixture should require a patch proposal after the appearance matcher fails.");
        var floor = Ground(30);
        var observation = LiveWorldObservation.Create(0, 0, 0, saved.Width, saved.Height, 320, 160, [], pose, pose)
            with { GroundGeometry = new(floor) };
        var match = new LiveGroundClosureSearch().Find([new(0, observation)], current, [], floor, 320,
            _ => saved, _ => []);
        Require(match is not null && Math.Abs(match.CameraPose.X - pose.X) < 0.25 && match.CameraPose.Y == pose.Y,
            "Exposure-tolerant patch motion should propose a position that independent floor texture can verify.");
    }

    private static GroundPlaneClosureVerifier.Measurement? Verify(AtlasPoint proposed) =>
        GroundPlaneClosureVerifier.Verify(Image(), Ground(30), default, Image(dx: 20, dy: 10), Ground(20), proposed, 320);

    private static LiveGroundLineEvidence[] Ground(double y, params (int Left, int Right)[] spans)
    {
        if (spans.Length == 0) spans = [(0, 320)];
        return spans.Select((span, index) => new LiveGroundLineEvidence(index, new(span.Left, y), new(span.Right, y))).ToArray();
    }

    private static BgraFrame Image(int dx = 0, int dy = 0, int pattern = 0, (int Left, int Right)? black = null)
    {
        const int width = 320;
        const int height = 120;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        for (var x = 0; x < width; x++)
        {
            var wx = x + dx;
            var wy = height - row + dy;
            var value = unchecked((ulong)(wx + 10000) * 0x9e3779b185ebca87);
            value ^= unchecked((ulong)(wy + 10000) * 0xc2b2ae3d27d4eb4f);
            value ^= value >> 33;
            value = unchecked(value * 0xff51afd7ed558ccd);
            var intensity = pattern switch
            {
                1 => (byte)0,
                2 => (byte)((wx % 4) * 50 + (wy % 3) * 10),
                _ => (byte)(value >> 24)
            };
            if (black is { } band && x >= band.Left && x <= band.Right) intensity = 0;
            var offset = (row * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = intensity;
            pixels[offset + 3] = 255;
        }
        return new(width, height, width * 4, pixels, 0, DateTimeOffset.UnixEpoch);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
