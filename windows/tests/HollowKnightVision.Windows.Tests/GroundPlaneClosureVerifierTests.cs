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

    internal static void TestPlacedAtlasUsesFloorVerifiedPatchFallback() => VerifyPlacedAtlasPatchFallback(sharedAtlas: false);

    internal static void TestSharedAtlasPatchFallbackUsesImageMasks() => VerifyPlacedAtlasPatchFallback(sharedAtlas: true);

    private static void VerifyPlacedAtlasPatchFallback(bool sharedAtlas)
    {
        var root = Directory.CreateTempSubdirectory("hkv-placed-atlas-patches-");
        try
        {
            if (sharedAtlas)
                File.WriteAllBytes(Path.Combine(root.FullName, "manifest.json"),
                    LiveWorldJson.WriteManifest(new SessionManifest { UsesSwiftWireFormat = true }));
            IReadOnlyList<StencilRect> masks = sharedAtlas ? [new(0, 0, 320, 90)] : [];
            var pipeline = new WindowsLiveWorldPipeline(root.FullName);
            var placed = new AtlasPoint(80, 20);
            var drifted = new AtlasPoint(130, 20);
            var bright = Mask(Image(height: sharedAtlas ? 180 : 120));
            var dimmed = Mask(Image(dimmed: true, height: bright.Height));
            var matcher = new GlobalFeatureMatcher();
            var alignmentMasks = masks.Select(mask => mask with { Y = bright.Height - mask.Bottom }).ToArray();
            var reference = matcher.MakeReference(bright, placed, bright.Width, excluding: alignmentMasks)!;
            Require(matcher.Match(dimmed, [reference], currentSolveWidth: bright.Width, excluding: alignmentMasks) is null
                && !matcher.LastMatchWasAmbiguous, "The return must need patches after the ordinary feature search finds no unique proposal.");
            if (sharedAtlas)
            {
                var source = LiveWorldObservation.Create(0, 0, 1, bright.Width, bright.Height, bright.Width, 160, [], placed, placed);
                var imageMask = new WorldRect(default, new WorldSize(320, 90));
                var proposal = new LiveGroundClosureSearch().ProposePatchClosure([(new(0, source), reference)],
                    dimmed, masks, bright.Width, _ => (bright, [imageMask]));
                Require(proposal is not null, "The shared-atlas fixture must support a closure when patches use image rows.");
            }
            Require(pipeline.Process(Scan(bright, 1, .5, placed, 1)).AllowsAtlasComposition,
                "Save the bright room reference.");
            Require(pipeline.Process(Scan(Mask(Image(dx: 700, dy: 1000, height: bright.Height)), 2, .9, new(400, 20), 2)).AllowsAtlasComposition,
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
            Require(saved.UsesSwiftWireFormat == sharedAtlas && saved.LoopClosureEdges.Count == 1
                && saved.CompositionMasks(saved.Observations[0]).SequenceEqual(masks)
                && saved.Observations[0].OptimizedPose == placed
                && saved.Observations[^1].RawPose == drifted && saved.Observations[^1].OptimizedPose == corrected.CameraPose,
                "Save the corrected placement without rewriting the raw capture.");

            BgraFrame Mask(BgraFrame frame)
            {
                if (sharedAtlas) Array.Clear(frame.Pixels, 0, frame.Stride * 90);
                return frame;
            }

            WindowsLiveWorldFrame Scan(BgraFrame pixels, long id, double time, AtlasPoint pose,
                long lineage, double floorY = 30)
            {
                var frame = new BgraFrame(pixels.Width, pixels.Height, pixels.Stride, pixels.Pixels, id,
                    DateTimeOffset.UnixEpoch.AddSeconds(time));
                return new(frame, frame, time, time, pose, frame.Width, masks, [], 1, "exposure-room", 1, 128)
                {
                    WorldEvidence = new(time, new(Ground(floorY))) { MeasuredLineage = lineage }
                };
            }
        }
        finally { root.Delete(recursive: true); }
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

    internal static void TestPatchSearchRecoversDisplacementWithExposureAndOcclusion()
    {
        var saved = PatchImage();
        var current = PatchImage(dx: 2, dy: -3, gain: .65, bias: 20, occlusion: true);
        var source = PatchReference(saved, 0, new(100, 50));
        var search = new LiveGroundClosureSearch();
        var loads = 0;
        var match = search.ProposePatchClosure([source], current, [], 640, Read);
        Require(match is { InlierReferenceFeatureIds.Count: >= 6 and <= 80 }
            && match.InlierReferenceFeatureIds.Count == match.InlierCurrentFeatureIndices.Count
            && Math.Abs(match.ProposedCameraPose.X - 92) <= .3 && Math.Abs(match.ProposedCameraPose.Y - 38) <= .3,
            $"Expected the measured two-axis displacement through exposure and occlusion, received {match?.ProposedCameraPose}.");
        Require(loads == 1, "The first search must load its immutable source once.");
        _ = search.ProposePatchClosure([source], current, [], 640, Read);
        Require(loads == 1, "Repeated searches must use the source cache.");

        (BgraFrame Image, IReadOnlyList<WorldRect> Exclusions) Read(int id)
        {
            Require(id == 0, "The search requested the wrong saved capture.");
            loads++;
            return (saved, []);
        }
    }

    internal static void TestPatchSearchRejectsSeparateMatchesAndAcceptsCoincidentViews()
    {
        var source = PatchImage();
        var first = PatchReference(source, 0, default);
        var distant = PatchReference(source, 1, new(200, 0));
        var agreeing = PatchReference(source, 2, new(.1, 0));
        var search = new LiveGroundClosureSearch();
        Require(search.ProposePatchClosure([first, distant], source, [], 640, _ => (source, [])) is null
            && search.LastSearchWasAmbiguous, "Two equally strong placements far apart must be ambiguous.");
        Require(search.ProposePatchClosure([first, agreeing], source, [], 640, _ => (source, [])) is
            { AmbiguityRatio: 0 } && !search.LastSearchWasAmbiguous,
            "Two views that agree on placement must not be treated as rivals.");
    }

    internal static void TestPatchSearchRejectsDarkMaskedRepeatedUnrelatedAndDistantViews()
    {
        var source = PatchImage();
        var reference = PatchReference(source, 0, default);
        var search = new LiveGroundClosureSearch();
        Require(search.ProposePatchClosure([reference], source, [], 640, _ => (source, [])) is not null,
            "The control image must match its reference before testing rejected evidence.");
        foreach (var (image, description) in new[]
        {
            (PatchImage(gain: 0), "dark"),
            (PatchImage(seed: 777), "unrelated"),
            (PatchImage(dx: 8), "out of search range")
        })
            Require(search.ProposePatchClosure([reference], image, [], 640, _ => (source, [])) is null,
                $"A {description} image must not propose a closure.");
        Require(search.ProposePatchClosure([reference], source, [new(0, 0, 640, 360)], 640, _ => (source, [])) is null,
            "A fully masked image must not propose a closure.");
        var repeated = PatchImage(repeated: true);
        var periodic = PatchReference(repeated, 1, default);
        Require(search.ProposePatchClosure([periodic], repeated, [], 640, _ => (repeated, [])) is null,
            "A repeating texture must not position the camera.");
        Require(search.ProposePatchClosure([reference], source, [], 320, _ => (source, [])) is null,
            "Incompatible solve widths cannot share the reference.");
    }

    private static (LiveGroundClosureSearch.Source Source, GlobalFeatureReference Features) PatchReference(
        BgraFrame image, int id, AtlasPoint pose)
    {
        var reference = new GlobalFeatureMatcher().MakeReference(image, pose, 640, new Guid(id, 0, 0, new byte[8]))
            ?? throw new InvalidOperationException("The patch fixture must provide a valid reference.");
        var observation = LiveWorldObservation.Create(id, id, 0, image.Width, image.Height, 640, 160, [], pose, pose)
            with { GroundGeometry = new(Ground(10, (0, 640))) };
        return (new(id, observation), reference);
    }

    // Match the macOS patch fixture's quarter-resolution texture and foreground band.
    private static BgraFrame PatchImage(int dx = 0, int dy = 0, double gain = 1, int bias = 0,
        bool occlusion = false, bool repeated = false, ulong seed = 1)
    {
        const int width = 640;
        const int height = 360;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sx = x / 4 - dx + 1000;
            var sy = y / 4 - dy + 1000;
            var hash = unchecked((ulong)sx * 0x9e3779b185ebca87);
            hash ^= unchecked((ulong)sy * 0xc2b2ae3d27d4eb4f);
            hash ^= unchecked(seed * 0x165667b19e3779f9);
            hash ^= hash >> 33;
            hash = unchecked(hash * 0xff51afd7ed558ccd);
            var raw = repeated ? sx % 4 * 40 + sy % 4 * 20 : (byte)(hash >> 24);
            var intensity = occlusion && x is >= 240 and < 400
                ? (byte)0 : (byte)Math.Clamp(Math.Round(raw * gain + bias, MidpointRounding.AwayFromZero), 0, 255);
            var index = (y * width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = intensity;
            pixels[index + 3] = byte.MaxValue;
        }
        return new(width, height, width * 4, pixels, 0, DateTimeOffset.UnixEpoch);
    }

    private static GroundPlaneClosureVerifier.Measurement? Verify(AtlasPoint proposed) =>
        GroundPlaneClosureVerifier.Verify(Image(), Ground(30), default, Image(dx: 20, dy: 10), Ground(20), proposed, 320);

    private static LiveGroundLineEvidence[] Ground(double y, params (int Left, int Right)[] spans)
    {
        if (spans.Length == 0) spans = [(0, 320)];
        return spans.Select((span, index) => new LiveGroundLineEvidence(index, new(span.Left, y), new(span.Right, y))).ToArray();
    }

    private static BgraFrame Image(int dx = 0, int dy = 0, int pattern = 0, (int Left, int Right)? black = null,
        bool dimmed = false, int height = 120)
    {
        const int width = 320;
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
