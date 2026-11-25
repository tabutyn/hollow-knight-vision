using HollowKnightVision.Windows.App;
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
        var current = Image(dimmed: true);
        var pose = new AtlasPoint(80, 20);
        var matcher = new GlobalFeatureMatcher();
        var reference = matcher.MakeReference(saved, pose, 320);
        Require(reference is not null && matcher.Match(current, [reference], currentSolveWidth: 320) is null,
            "The fixture should require a patch proposal after the appearance matcher fails.");
        var floor = Ground(30);
        var observation = LiveWorldObservation.Create(0, 0, 0, saved.Width, saved.Height, 320, 160, [], pose, pose)
            with { GroundGeometry = new(floor) };
        var search = new LiveGroundClosureSearch();
        var match = search.Find([new(0, observation)], current, [], floor, 320,
            _ => saved, _ => []);
        Require(match is not null && Math.Abs(match.CameraPose.X - pose.X) < 0.25 && match.CameraPose.Y == pose.Y,
            "Exposure-tolerant patch motion should propose a position that independent floor texture can verify.");
        var otherRoom = observation with { Id = 1, SourceObservationId = 1, RoomId = 1 };
        var ambiguous = search.Find([new(0, observation), new(1, otherRoom)], current, [], floor, 320,
            _ => saved, _ => [], searchAcrossRooms: true);
        Require(ambiguous is null && search.LastSearchWasAmbiguous,
            "Patch proposals in distinct rooms must compete even when their coordinates agree.");
    }

    internal static void TestPlacedAtlasUsesFloorVerifiedPatchFallback()
    {
        var root = Directory.CreateTempSubdirectory("hkv-placed-atlas-patches-");
        try
        {
            var pipeline = new WindowsLiveWorldPipeline(root.FullName);
            var placed = new AtlasPoint(80, 20);
            var drifted = new AtlasPoint(130, 20);
            var bright = Image();
            var dimmed = Image(dimmed: true);
            var matcher = new GlobalFeatureMatcher();
            var reference = matcher.MakeReference(bright, placed, bright.Width)!;
            Require(matcher.Match(dimmed, [reference], currentSolveWidth: bright.Width) is null
                && !matcher.LastMatchWasAmbiguous, "The return must need patches after the ordinary feature search finds no unique proposal.");
            Require(pipeline.Process(Scan(bright, 1, .5, placed, 1)).AllowsAtlasComposition,
                "Save the bright room reference.");
            Require(pipeline.Process(Scan(Image(dx: 700, dy: 1000), 2, .9, new(400, 20), 2)).AllowsAtlasComposition,
                "Record the excursion on a separate measured island.");

            Require(pipeline.Process(Scan(dimmed, 3, 1.26, drifted, 2, floorY: 60)).AcceptedClosure is null
                && pipeline.Process(Scan(dimmed, 4, 1.56, drifted, 2, floorY: 60)).AcceptedClosure is null,
                "Matching scenery patches cannot confirm a floor at the wrong height.");
            Require(pipeline.Process(Scan(dimmed, 5, 1.86, drifted, 2)).AcceptedClosure is null,
                "The first floor-verified patch proposal must wait for a separate capture.");
            var corrected = pipeline.Process(Scan(dimmed, 6, 2.16, drifted, 2));
            Require(corrected is { AcceptedClosure: not null, AllowsAtlasComposition: true, RebuiltAtlas.TileCount: > 0 }
                && Math.Abs(corrected.CameraPose.X - placed.X) < .25 && corrected.CameraPose.Y == placed.Y,
                $"Two floor-verified patch proposals must correct the drawn Atlas ({corrected.Diagnostic}).");
            var saved = new LiveWorldSessionStore(root.FullName).Load().Snapshot;
            Require(saved.LoopClosureEdges.Count == 1 && saved.Observations[0].OptimizedPose == placed
                && saved.Observations[^1].RawPose == drifted && saved.Observations[^1].OptimizedPose == corrected.CameraPose,
                "Save the corrected placement without rewriting the raw capture.");
        }
        finally { root.Delete(recursive: true); }

        static WindowsLiveWorldFrame Scan(BgraFrame pixels, long id, double time, AtlasPoint pose,
            long lineage, double floorY = 30)
        {
            var frame = new BgraFrame(pixels.Width, pixels.Height, pixels.Stride, pixels.Pixels, id,
                DateTimeOffset.UnixEpoch.AddSeconds(time));
            return new(frame, frame, time, time, pose, frame.Width, [], [], 1, "exposure-room", 1, 128)
            {
                WorldEvidence = new(time, new(Ground(floorY))) { MeasuredLineage = lineage }
            };
        }
    }

    internal static void TestPatchSearchBoundsItsCacheAndReloadsChangedSource()
    {
        var bright = Image();
        var dimmed = Image(dimmed: true);
        var reference = new GlobalFeatureMatcher().MakeReference(bright, default, bright.Width)!;
        var references = Enumerable.Range(0, 96).Select(id =>
        {
            var observation = LiveWorldObservation.Create(id, id, id, bright.Width, bright.Height, bright.Width, 160,
                [], default, default) with { GroundGeometry = new(Ground(30)) };
            return (Source: new LiveGroundClosureSearch.Source(id, observation),
                Features: reference with { Id = new Guid(id, 0, 0, new byte[8]) });
        }).ToArray();
        var loads = 0;
        var search = new LiveGroundClosureSearch();
        _ = search.ProposePatchClosure(references, dimmed, [], bright.Width, ReadSavedFrame);
        Require(loads == 32, "A single patch search can load at most 32 saved references.");

        search = new LiveGroundClosureSearch();
        loads = 0;
        foreach (var group in references.Chunk(32))
            _ = search.ProposePatchClosure(group, dimmed, [], bright.Width, ReadSavedFrame);
        Require(loads == 96, "Each distinct reference loads once.");
        var newest = references[^1];
        var reused = search.ProposePatchClosure([newest], dimmed, [], bright.Width, ReadSavedFrame);
        Require(loads == 96 && reused is { InlierReferenceFeatureIds.Count: >= 6 }
            && reused.InlierReferenceFeatureIds.Count == reused.InlierCurrentFeatureIndices.Count,
            "Repeated searches must reuse exact cached pixels and preserve both patch support indexes.");

        var updated = newest.Source with { Observation = newest.Source.Observation with { Timestamp = 999 } };
        _ = search.ProposePatchClosure([(updated, newest.Features)], dimmed, [], bright.Width, ReadSavedFrame);
        Require(loads == 97, "A changed saved source must reload even while its keyframe ID is unchanged.");
        _ = search.ProposePatchClosure([references[0]], dimmed, [], bright.Width, ReadSavedFrame);
        Require(loads == 98, "The cache must evict the oldest reference once it holds 64 newer images.");

        (BgraFrame Image, IReadOnlyList<WorldRect> Exclusions) ReadSavedFrame(int id)
        {
            loads++;
            return (bright, []);
        }
    }

    private static GroundPlaneClosureVerifier.Measurement? Verify(AtlasPoint proposed) =>
        GroundPlaneClosureVerifier.Verify(Image(), Ground(30), default, Image(dx: 20, dy: 10), Ground(20), proposed, 320);

    private static LiveGroundLineEvidence[] Ground(double y, params (int Left, int Right)[] spans)
    {
        if (spans.Length == 0) spans = [(0, 320)];
        return spans.Select((span, index) => new LiveGroundLineEvidence(index, new(span.Left, y), new(span.Right, y))).ToArray();
    }

    private static BgraFrame Image(int dx = 0, int dy = 0, int pattern = 0, (int Left, int Right)? black = null, bool dimmed = false)
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
            if (dimmed) intensity = (byte)(20 + intensity / 4);
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
