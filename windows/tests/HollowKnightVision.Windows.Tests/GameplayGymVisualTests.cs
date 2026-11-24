using HollowKnightVision.Windows.Core;

internal static class GameplayGymVisualTests
{
    private const int Width = 640, Height = 360;
    internal static void TestGamePlayGymUsesVisualBlockadeEvidence()
    {
        var source = new BotVisualGymObservationSource();
        var pixels = Scene();
        BotCaptureObservation? latest = null;
        for (var i = 0; i < 12; i++)
            latest = source.Observe(Frame(pixels, i), i / 10d, InputButtons.Right, frame => Detect(frame, true), hud: null).Observation;
        if (latest is not { BlockadeVersion: 1, Blockade.Mark.Direction: 1, BlockadePose.Space: var space }
            || !space.StartsWith("visual/", StringComparison.Ordinal))
            throw new InvalidOperationException("The Game Play Gym observation should recognize a measured stationary push.");
        var history = new BotObservationHistory(BotPolicyContract.LandingVersion);
        var vector = history.Append(latest, new(2, 0, 0, 0), latest.AvailableAt + .01)!;
        var names = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!.ToList();
        if (vector[3 * names.Count + names.IndexOf("blockade_direction")] != 1)
            throw new InvalidOperationException("The visual blockade did not reach the policy input.");
    }

    internal static void TestVisualFootprintAndMaskedKnightRecovery()
    {
        var source = new BotVisualGymObservationSource();
        var pixels = Scene();
        BotVisualGymReading? latest = null;
        for (var i = 0; i < 10; i++)
            latest = source.Observe(Frame(pixels, i), i / 10d, InputButtons.None, frame => Detect(frame, true), hud: null);
        if (latest?.Observation is not { } visible || latest.Camera?.GroundVerified != true
            || visible.AtlasView is not { CoveredCells: > 0, Health: null }
            || visible.DetectedTerrain?.Floors.Count is not > 0 || visible.KnightSource != "visual"
            || visible.LifebloodCocoons is not { Count: > 0 })
            throw new InvalidOperationException("Pixel-derived actor and terrain evidence must establish the episode Atlas.");

        var predicted = source.Observe(Frame(pixels, 10), 1, InputButtons.None, frame => Detect(frame, false), hud: null);
        if (predicted.Observation is not { KnightSource: "visual-predicted", KnightPrediction: not null }
            || predicted.Observation.Blockade is not null)
            throw new InvalidOperationException("A recent visual sighting should bridge the first occluded frame without proving a blockade.");
        BotVisualGymReading? masked = null;
        for (var i = 11; i < 18; i++)
            masked = source.Observe(Frame(pixels, i), i / 10d, InputButtons.None, frame => Detect(frame, false), hud: null);
        if (masked?.Observation is not { HasMaskedKnightEvidence: true })
            throw new InvalidOperationException("A longer occlusion should fall back to fresh terrain and no Knight estimate.");

        var blackout = source.Observe(Frame(new byte[pixels.Length], 18), 1.8, InputButtons.None,
            _ => throw new InvalidOperationException("Inference must skip a complete blackout."), hud: null);
        if (blackout.Observation is not null || blackout.Issues is not ["transition-blackout"])
            throw new InvalidOperationException("A black frame entered policy history.");

        for (var i = 19; i < 28; i++)
            latest = source.Observe(Frame(pixels, i), i / 10d, InputButtons.None, frame => Detect(frame, true), hud: null);
        if (latest?.Observation?.AtlasView is not { CoveredCells: > 0 } || latest.Observation.RoomId != visible.RoomId)
            throw new InvalidOperationException("A brief blackout followed by the same room must retain the episode Atlas.");
        if (latest.Observation.AtlasView.EarnedCells != visible.AtlasView.EarnedCells)
            throw new InvalidOperationException("A same-room fade must not count as measured exploration.");
    }

    internal static void TestAtlasCreditSurvivesGroundCameraRecovery()
    {
        const int width = 320, height = 100;
        var size = new WorldSize(width, height);
        var floors = new[] { new DetectedFloorLine(40, new(0, width - 1)) };
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var hash = (ulong)x * 73_856_093 ^ (ulong)y * 19_349_663;
            hash ^= hash >> 13;
            pixels[y * width + x] = unchecked((byte)((hash * 1_274_126_177) >> 11));
        }

        for (var index = 0; index < 4; index++)
            _ = tracker.Update(pixels, width, height, floors, timestamp: index / 10d);
        var drifted = new AtlasPoint(96, 0);
        tracker.ReanchorLocalCamera(drifted);
        var recovered = tracker.Update(pixels, width, height, floors, timestamp: 1);
        if (recovered is not { CameraPosition: { } anchor, GlobalCorrection.X: < -50, GlobalMatchCount: >= 6 }
            || Math.Abs(anchor.X) > 1)
            throw new InvalidOperationException("The tracker must supply a real, sub-half-screen global correction.");
        var measured = recovered with { GlobalCorrection = null };

        foreach (var admitsCorrection in new[] { true, false })
        {
            var coverage = new BotVisualAtlasCoverage();
            coverage.ObserveVerified(drifted, size, [], default, measured, admitsPlacement: true);
            var entry = coverage.View(drifted, size, 0, null)!;
            coverage.ObserveVerified(anchor, size, [], default, recovered, admitsPlacement: admitsCorrection);
            if (!admitsCorrection)
            {
                if (coverage.View(drifted, size, 1, null)?.CoveredCells != entry.CoveredCells)
                    throw new InvalidOperationException("A held frame must not add its footprint to the map.");
                coverage.ObserveVerified(anchor, size, [], default, measured, admitsPlacement: true);
            }

            var baseline = coverage.View(anchor, size, 2, null)!;
            if (baseline.CoveredCells <= entry.CoveredCells || baseline.EarnedCells != 0)
                throw new InvalidOperationException("Camera recovery can re-establish coverage but must not earn cells.");
            coverage.ObserveVerified(new AtlasPoint(-64, 0), size, [], default, measured, admitsPlacement: true);
            if (coverage.View(new(-64, 0), size, 3, null) is not { EarnedCells: 1, CoveredCells: 7 })
                throw new InvalidOperationException("The next measured movement must earn only its new cells.");

            var noMotionProof = measured with { LocalTextureSupport = 0, InlierCount = 0, GlobalMatchCount = 0 };
            coverage.ObserveVerified(new AtlasPoint(-128, 0), size, [], default, noMotionProof, admitsPlacement: true);
            if (coverage.View(new(-128, 0), size, 4, null) is not { EarnedCells: 1, CoveredCells: 8 })
                throw new InvalidOperationException("The first registered footprint without a motion measurement cannot earn credit.");
        }
    }

    internal static void TestGymReprojectsTheRecoveryIslandIntoCommittedCoverage()
    {
        const int width = 320, height = 180;
        var size = new WorldSize(width, height);
        var pixels = new byte[width * height * 4];
        new Random(71).NextBytes(pixels);
        for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        var measured = GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 8
        };
        var atlas = new BotGymAtlasCoverage();
        atlas.EnterRoom(7);
        var start = new AtlasPoint(0, 0);
        var drifted = new AtlasPoint(96, 0);
        Observe(0, start, measured);
        var baseline = atlas.View(start, size, 5)!;
        if (baseline is not { CoveredCells: 5, EarnedCells: 0 })
            throw new InvalidOperationException("The first verified view must establish the episode baseline.");
        Observe(1, start, measured);
        if (atlas.Snapshot.Observations.Count != 1)
            throw new InvalidOperationException("An unchanged verified view must not add a duplicate graph observation.");
        Observe(2, drifted, measured with { GlobalCorrection = new(96, 0) });
        if (atlas.View(drifted, size, 5) is not null || atlas.Snapshot.UnplacedObservationIds().Count != 1)
            throw new InvalidOperationException("The first recovery vote must not extend committed coverage.");

        Observe(3, drifted, measured);
        var recovered = atlas.View(drifted, size, 5);
        var graph = atlas.Snapshot;
        if (recovered is not { CoveredCells: 5, EarnedCells: 0 }
            || Math.Abs(recovered.Camera[0]) > 1 || graph.LoopClosureEdges.Count != 1
            || graph.UnplacedObservationIds().Count != 0 || graph.Observations.Count != 3
            || graph.Observations.Skip(1).Any(row => row.RawPose.X != 256 || Math.Abs(row.OptimizedPose.X - 160) > 1))
            throw new InvalidOperationException("Two measured matches must reproject the whole recovery island without earning cells.");

        Observe(4, new(160, 0), measured);
        if (atlas.View(new(160, 0), size, 5) is not { CoveredCells: 6, EarnedCells: 1 })
            throw new InvalidOperationException("Movement beyond the corrected footprint must earn one new cell.");
        atlas.EnterRoom(8);
        if (atlas.View(start, size, 5) is not null)
            throw new InvalidOperationException("Coverage from the prior room must not become the current room's frontier.");
        Observe(5, start, measured);
        if (atlas.View(start, size, 5) is not { CoveredCells: 11, EarnedCells: 1 }
            || atlas.Snapshot.Observations.Count != 5)
            throw new InvalidOperationException("A new room must receive an independent baseline while preserving the episode map.");

        void Observe(int frame, AtlasPoint pose, GroundHypothesisTrackingResult evidence)
        {
            var time = frame / 5d;
            atlas.Observe(new(width, height, width * 4, pixels, frame, DateTimeOffset.UnixEpoch.AddSeconds(time)),
                pose, time, [], default, evidence, admitsPlacement: true);
        }
    }

    internal static void TestGymReportsCorrectionsOnlyForAnExistingVisualEstimate()
    {
        var source = new BotVisualGymObservationSource();
        var pixels = Scene();
        var cold = source.Observe(Frame(pixels, 0), 0, InputButtons.None, frame => Detect(frame, true, .3f), hud: null);
        if (cold.WeakKnightCorrection is not null)
            throw new InvalidOperationException("A weak candidate must not count as a correction without a measured track.");

        for (var i = 1; i < 10; i++)
            source.Observe(Frame(pixels, i), i / 10d, InputButtons.None, frame => Detect(frame, true), hud: null);
        var weak = source.Observe(Frame(pixels, 10), 1, InputButtons.None, frame => Detect(frame, true, .3f), hud: null);
        if (weak is not { WeakKnightCorrection: true, Observation: { KnightSource: "visual-predicted", Grounded: null } estimated }
            || Math.Abs(estimated.KnightPrediction!.LastSeenAt - .9) > 1e-6)
            throw new InvalidOperationException("The Gym observation should report a weak correction without renewing the measured sighting.");

        var motion = source.Observe(Frame(pixels, 11), 1.1, InputButtons.None, frame => Detect(frame, false), hud: null);
        if (motion is not { WeakKnightCorrection: null, Observation.KnightSource: "visual-predicted" })
            throw new InvalidOperationException("Motion-only estimates must not count as weak detection corrections.");

        var fresh = source.Observe(Frame(pixels, 12), 1.2, InputButtons.None, frame => Detect(frame, true), hud: null);
        if (fresh is not { WeakKnightCorrection: null, Observation.KnightSource: "visual" })
            throw new InvalidOperationException("A measured reacquisition is not a weak correction.");
    }

    internal static void TestGamePlayGymPublishesOnlyConfirmedRoomChanges()
    {
        var source = new BotVisualGymObservationSource();
        var departure = TexturedRoom(1);
        var arrival = TexturedRoom(2);
        var black = new byte[departure.Length];
        var rooms = new HashSet<int>();
        var loading = 0;
        for (var index = 0; index < 48; index++)
        {
            var pixels = index < 9 ? departure : index < 18 ? black : arrival;
            var reading = source.Observe(Frame(pixels, index), index / 10d, InputButtons.Right,
                frame => Detect(frame, index < 9 || index >= 27), hud: null);
            if (index is >= 9 and < 18 && reading.Observation is not null)
                throw new InvalidOperationException("A blackout cannot enter the Game Play policy history.");
            if (index is >= 18 and < 27 && reading.Issues is ["room-transition"]) loading++;
            if (reading.Observation is { } row)
            {
                rooms.Add(row.RoomId);
                if (index < 18 && row.RoomId != 0)
                    throw new InvalidOperationException("A room change appeared before the scene cut was confirmed.");
                if (index >= 27 && (row.RoomId != 1 || row.RoomRevision != 1))
                    throw new InvalidOperationException("The confirmed new room should remain stable after the Knight becomes visible.");
            }
        }
        if (!rooms.SetEquals([0, 1]) || loading == 0)
            throw new InvalidOperationException("The Gym encoder never published the confirmed room transition.");

        static byte[] TexturedRoom(ulong state)
        {
            var luma = new byte[64 * 36];
            for (var i = 0; i < luma.Length; i++)
            {
                state = unchecked(state * 6364136223846793005 + 1);
                luma[i] = (byte)(20 + ((state >> 32) % 220));
            }
            var pixels = new byte[Width * Height * 4];
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Width + x) * 4;
                var shade = luma[y / 10 * 64 + x / 10];
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
                pixels[offset + 3] = 255;
            }
            return pixels;
        }
    }

    private static BgraFrame Frame(byte[] pixels, int index) =>
        new(Width, Height, Width * 4, pixels, index, DateTimeOffset.UnixEpoch.AddSeconds(index / 10d));

    private static IReadOnlyList<ObjectDetection> Detect(BgraFrame frame, bool knight, float knightConfidence = .96f)
    {
        var result = new List<ObjectDetection>
        {
            new("enemies.crawlid", new(.61, .58, .06, .11), .91f) { SourceFrameIdentifier = frame.CaptureId },
            new("world.geo-deposit", new(.80, .57, .045, .11), .88f) { SourceFrameIdentifier = frame.CaptureId },
            new("world.lifeblood-cacoon", new(.48, .57, .045, .11), .93f) { SourceFrameIdentifier = frame.CaptureId }
        };
        if (knight)
            result.Add(new("game.playable-knight", new(.26, .53, .045, .14), knightConfidence) { SourceFrameIdentifier = frame.CaptureId });
        return result;
    }

    private static byte[] Scene()
    {
        var bytes = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var noise = (x * 23 + y * 37 + x / 13 * 17) % 33;
            var shade = y < 239 ? 20 + noise / 4 : y < 241 ? 120 + noise : 10 + noise;
            if (x is >= 168 and < 194 && y is >= 191 and < 239) shade = 229;
            if (x is >= 394 and < 429 && y is >= 209 and < 240) shade = 159;
            if (x is >= 514 and < 537 && y is >= 211 and < 240) shade = 171;
            var offset = (y * Width + x) * 4;
            bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = (byte)shade;
            bytes[offset + 3] = 255;
        }
        return bytes;
    }
}
