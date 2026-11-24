using HollowKnightVision.Windows.Core;

internal static class GameplayGymVisualTests
{
    private const int Width = 640, Height = 360;

    internal static void TestGymUsesWalkedGroundUnderAnOccluder()
    {
        var source = new BotVisualGymObservationSource();
        var clean = Scene();
        // Replace the generic detector fixture with a face that the foot locator can measure.
        for (var y = 191; y < 239; y++)
        for (var x = 168; x < 194; x++)
        {
            var shade = (byte)(20 + (x * 23 + y * 37 + x / 13 * 17) % 33 / 4);
            clean.AsSpan((y * Width + x) * 4, 3).Fill(shade);
        }

        BotVisualGymReading? hidden = null;
        for (var index = 0; index <= 19; index++)
        {
            var center = 180 + 3 * Math.Max(0, Math.Min(index, 18) - 8);
            var pixels = (byte[])clean.Clone();
            if (index == 19)
                for (var y = 232; y < Height; y++)
                for (var x = 164; x <= 239; x++)
                    pixels.AsSpan((y * Width + x) * 4, 3).Fill(17);
            for (var y = 213; y <= 224; y++)
            for (var x = center - 6; x <= center + 6; x++)
                pixels.AsSpan((y * Width + x) * 4, 3).Fill(y == 220 && (x == center || x == center + 1) ? (byte)0 : (byte)230);

            hidden = source.Observe(Frame(pixels, index), index / 10d,
                index < 8 ? InputButtons.None : InputButtons.Right, frame =>
                [.. Detect(frame, knight: false), new ObjectDetection("game.playable-knight",
                    new((center - 9d) / Width, 211d / Height, 18d / Width, 29d / Height), .96f)
                    { SourceFrameIdentifier = frame.CaptureId }], hud: null);
        }
        if (hidden?.Camera?.GroundVerified != true || hidden.Observation is not { Grounded: true, DetectedTerrain.Floors: { } floors }
            || !floors.Any(floor => floor.MinimumX <= 183 && floor.MaximumX >= 212 && Math.Abs(Height - 239 - floor.Y) <= 2))
            throw new InvalidOperationException("Game Play Gym must keep the recorded walked floor under the dark region.");
    }

    internal static void TestGymMeasuresWalkWhileOnlySceneryIsVisible()
    {
        var source = new BotVisualGymObservationSource();
        const int center = 200;
        BotVisualGymReading? reading = null;
        double? lastGroundCameraX = null;
        for (var index = 0; index <= 32; index++)
        {
            reading = Observe(index, center);
            if (index == 24) lastGroundCameraX = reading.Observation?.AtlasView?.Camera[0];
            if (reading.Observation?.Blockade is not null)
                throw new InvalidOperationException("Camera following the advancing Knight must not produce a blockade.");
        }

        if (reading?.Camera is not { PoseSource: "calibrated-scene", GroundVerified: false, AllowsAtlasWrite: true, ReferenceFloors: 0 }
            || reading.Observation is not { Grounded: true, DetectedTerrain.Floors: { } floors, AtlasView: { CoveredCells: > 0 } atlas }
            || floors.Count != 1 || floors[0] is not { MinimumX: <= center, MaximumX: >= center + 20, Y: 181 }
            || lastGroundCameraX is not { } prior || Math.Abs(prior - atlas.Camera[0] - 32) > 2)
            throw new InvalidOperationException($"The Game Play policy must see the confirmed dark walk in the measured scenery pose. Got {reading?.Camera}, floors {string.Join(',', reading?.Observation?.DetectedTerrain?.Floors ?? [])} and grounded {reading?.Observation?.Grounded}.");

        for (var index = 33; index <= 36; index++)
            reading = Observe(index, center + 4 * (index - 32));
        if (reading.Camera is not { PoseSource: "calibrated-scene", GroundVerified: false }
            || reading.Observation is not { Blockade.Mark.Direction: -1, BlockadePose: not null,
                VisualContext: { CameraX: null, CameraY: null } } blocked)
            throw new InvalidOperationException("A scenery-measured stationary push must reach the Game Play Gym observation.");

        var history = new BotObservationHistory(BotPolicyContract.LandingVersion);
        var vector = history.Append(blocked, new(1, 0, 0, 0), blocked.AvailableAt + .01);
        var names = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!.ToList();
        if (vector is null || vector[3 * names.Count + names.IndexOf("blockade_direction")] != -1)
            throw new InvalidOperationException("The scenery-backed blockade did not reach the current policy input.");

        var passed = Observe(37, center - 12);
        var returned = Observe(38, center + 24);
        if (passed.Observation is not { BlockadePose: not null, Blockade: null }
            || returned.Observation is not { BlockadePose: not null, Blockade: null })
            throw new InvalidOperationException("Crossing the wall must retire its policy input while scenery continues tracking.");

        BotVisualGymReading Observe(int index, int actorX)
        {
            var time = index * .1;
            var calibrationStep = Math.Clamp(index - 19, 0, 5);
            var dx = 8 * calibrationStep + 4 * Math.Max(0, index - 24);
            var dy = -12 * calibrationStep;
            var row = 239 + dy;
            var pixels = new byte[Width * Height * 4];
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var hash = unchecked((ulong)((x - dx + 1000) / 4) * 0x9e3779b185ebca87uL)
                    ^ unchecked((ulong)((y - dy + 1000) / 4) * 0xc2b2ae3d27d4eb4fuL);
                hash = unchecked((hash ^ (hash >> 33)) * 0xff51afd7ed558ccduL);
                var shade = (byte)(37 + (hash >> 24) % 22);
                if (index <= 24 && y >= row && y < row + 2) shade = 190;
                if (y >= row - 26 && y <= row - 15 && Math.Abs(x - actorX) <= 6)
                    shade = y == row - 19 && (x == actorX || x == actorX + 1) ? (byte)0 : (byte)230;
                var offset = (y * Width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
                pixels[offset + 3] = 255;
            }
            var frame = new BgraFrame(Width, Height, Width * 4, pixels, index, DateTimeOffset.UnixEpoch.AddSeconds(time));
            var hud = new HudStencilResult([new(0, "full", new(12, 12, 10, 10), 1),
                new(1, "full", new(24, 12, 10, 10), 1)], null, [], [], [], default, false, time);
            var buttons = index is > 24 and < 38 ? InputButtons.Left : InputButtons.None;
            return source.Observe(frame, time, buttons, frame =>
                [new ObjectDetection("game.playable-knight",
                    new((actorX - 9d) / Width, (row - 28d) / Height, 18d / Width, 29d / Height), .96f)
                    { SourceFrameIdentifier = frame.CaptureId }], hud);
        }
    }

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
        var conflicting = (byte[])pixels.Clone();
        Array.Clear(conflicting, (height - 32) * width * 4, 20 * width * 4);
        LiveGroundLineEvidence[] floor = [new(0, new(0, 30), new(width, 30))];
        var measured = GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 8
        };
        var atlas = new BotGymAtlasCoverage();
        atlas.EnterRoom(7);
        var start = new AtlasPoint(0, 0);
        var drifted = new AtlasPoint(96, 0);
        var matcher = new GlobalFeatureMatcher();
        var broadReference = matcher.MakeReference(Frame(0, pixels), start, width);
        if (broadReference is null || matcher.Match(Frame(2, conflicting), [broadReference], currentSolveWidth: width) is null)
            throw new InvalidOperationException("The conflicting floor fixture must still have matching scenery.");
        Observe(0, start, measured);
        var baseline = atlas.View(start, size, 5)!;
        if (baseline is not { CoveredCells: 5, EarnedCells: 0 })
            throw new InvalidOperationException("The first verified view must establish the episode baseline.");
        Observe(1, start, measured);
        if (atlas.Snapshot.Observations.Count != 1)
            throw new InvalidOperationException("An unchanged verified view must not add a duplicate graph observation.");
        Observe(2, drifted, measured with { GlobalCorrection = new(96, 0) }, conflicting);
        if (atlas.View(drifted, size, 5) is not null || atlas.Snapshot.UnplacedObservationIds().Count != 1)
            throw new InvalidOperationException("The first recovery vote must not extend committed coverage.");
        if (atlas.PolicyView(drifted, size, 5) is not { CoveredCells: 5, EarnedCells: 0, Health: 5 } local
            || local.Camera[0] != drifted.X || local.Missing is not { Count: 15 })
            throw new InvalidOperationException("The recovering island must supply local policy context with the committed totals.");

        Observe(3, drifted, measured, conflicting);
        if (!atlas.PlacementPending || atlas.Snapshot.LoopClosureEdges.Count != 0)
            throw new InvalidOperationException("Matching scenery with a conflicting floor must stay unplaced.");
        Observe(4, drifted, measured);
        if (!atlas.PlacementPending || atlas.Snapshot.UnplacedObservationIds().Count != 3)
            throw new InvalidOperationException("The first corroborated floor match must wait for a separate capture.");
        Observe(5, drifted, measured);
        var recovered = atlas.View(drifted, size, 5);
        var graph = atlas.Snapshot;
        if (recovered is not { CoveredCells: 5, EarnedCells: 0 }
            || Math.Abs(recovered.Camera[0]) > 1 || graph.LoopClosureEdges.Count != 1
            || graph.UnplacedObservationIds().Count != 0 || graph.Observations.Count != 5
            || graph.Observations.Skip(1).Any(row => row.RawPose.X != 256 || Math.Abs(row.OptimizedPose.X - 160) > 1))
            throw new InvalidOperationException("Two measured matches must reproject the whole recovery island without earning cells.");

        Observe(6, new(160, 0), measured);
        if (atlas.View(new(160, 0), size, 5) is not { CoveredCells: 6, EarnedCells: 1 })
            throw new InvalidOperationException("Movement beyond the corrected footprint must earn one new cell.");
        atlas.EnterRoom(8);
        if (atlas.View(start, size, 5) is not null)
            throw new InvalidOperationException("Coverage from the prior room must not become the current room's frontier.");
        Observe(7, start, measured);
        if (atlas.View(start, size, 5) is not { CoveredCells: 11, EarnedCells: 1 }
            || atlas.Snapshot.Observations.Count != 7)
            throw new InvalidOperationException("A new room must receive an independent baseline while preserving the episode map.");

        for (var index = 8; index <= 40; index++)
            Observe(index, new(96 * (index - 7), 0), measured);
        atlas.EnterRoom(7);
        Observe(41, drifted, measured);
        Observe(42, drifted, measured);
        if (!atlas.PlacementPending || atlas.View(drifted, size, 5) is not null
            || atlas.Snapshot.LoopClosureEdges.Count != 1)
            throw new InvalidOperationException("An old room with evicted reference images must stay registered only locally.");

        void Observe(int frame, AtlasPoint pose, GroundHypothesisTrackingResult evidence, byte[]? image = null)
        {
            var time = frame / 5d;
            atlas.Observe(Frame(frame, image ?? pixels),
                pose, time, [], default, evidence, admitsPlacement: true,
                captureEvidence: new(time, new(floor), new(floor)));
        }

        static BgraFrame Frame(int id, byte[] image) =>
            new(width, height, width * 4, image, id, DateTimeOffset.UnixEpoch.AddSeconds(id / 5d));
    }

    internal static void TestGymContinuesItsLocalAtlasWhilePlacementIsPending()
    {
        const int width = 320, height = 180;
        var size = new WorldSize(width, height);
        var atlas = new BotGymAtlasCoverage();
        var measured = GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 8
        };
        var original = Texture(71);
        var otherView = Texture(81);
        atlas.EnterRoom(7);
        Observe(0, new(0, 0), original, measured, lineage: 1);
        Observe(1, new(128, 0), original, measured, lineage: 1);
        var baseline = atlas.View(new(128, 0), size, 5)!;
        if (baseline.EarnedCells <= 0) throw new InvalidOperationException("The placed map must already have earned cells.");

        Observe(2, null, otherView, GroundHypothesisTrackingResult.Empty);
        if (atlas.CanContinueMeasuredScenery || atlas.PolicyView(new(640, 0), size, 5) is not null)
            throw new InvalidOperationException("An unsupported gap must withhold policy placement.");

        var recoveredLocal = new AtlasPoint(640, 0);
        Observe(3, recoveredLocal, otherView, measured, lineage: 2);
        if (!atlas.CanContinueMeasuredScenery || !atlas.PlacementPending
            || atlas.PolicyView(recoveredLocal, size, 5) is not { } first
            || first.CoveredCells != baseline.CoveredCells || first.EarnedCells != baseline.EarnedCells)
            throw new InvalidOperationException("A fresh measured ground view must begin an unplaced local chart.");
        var keys = atlas.Snapshot.Keyframes.Count;
        var next = new AtlasPoint(768, 0);
        Observe(4, next, otherView, GroundHypothesisTrackingResult.Empty, scenery: true, lineage: 2);
        var local = atlas.PolicyView(next, size, 5);
        if (local is not { Missing: { Count: 15 } } || local.Camera[0] != next.X
            || local.Missing.SequenceEqual(first.Missing!) || atlas.View(next, size, 5) is not null
            || local.CoveredCells != baseline.CoveredCells || local.EarnedCells != baseline.EarnedCells
            || atlas.Snapshot.Keyframes.Count != keys || atlas.Snapshot.UnplacedObservationIds().Count != 2)
            throw new InvalidOperationException("Verified scenery must extend only the recovering chart without adding a landmark or credit.");

        var unrelated = new AtlasPoint(800, 0);
        Observe(5, unrelated, otherView, measured, lineage: 3);
        var standalone = new BotVisualAtlasCoverage();
        standalone.Register(unrelated, size, [], creditExploration: false);
        var separated = atlas.PolicyView(unrelated, size, 5);
        if (separated?.Missing is not { } missing
            || !missing.SequenceEqual(standalone.View(unrelated, size, 0, 5)!.Missing!)
            || separated.CoveredCells != baseline.CoveredCells || separated.EarnedCells != baseline.EarnedCells
            || atlas.Snapshot.TrackingIslands[^1].Members.Count != 1)
            throw new InvalidOperationException("A new unplaced image owner must have an independent frontier and no global credit.");

        Observe(6, next, otherView, measured, lineage: 2);
        var resumed = atlas.PolicyView(next, size, 5);
        if (resumed?.Missing is not { } resumedMissing || !resumedMissing.SequenceEqual(local.Missing)
            || resumed.CoveredCells != baseline.CoveredCells || resumed.EarnedCells != baseline.EarnedCells
            || atlas.Snapshot.TrackingIslands[^1].Members.Count != 3)
            throw new InvalidOperationException("A measured return must restore the prior local frontier without adding global credit.");

        atlas.EnterRoom(8);
        Observe(7, next, otherView, GroundHypothesisTrackingResult.Empty, scenery: true);
        if (atlas.CanContinueMeasuredScenery || atlas.PolicyView(next, size, 5) is not null)
            throw new InvalidOperationException("A new room cannot inherit the recovering chart or seed it from scenery.");

        void Observe(int index, AtlasPoint? pose, byte[] pixels, GroundHypothesisTrackingResult tracking,
            bool scenery = false, long? lineage = null)
        {
            var time = index * .2;
            atlas.Observe(new(width, height, width * 4, pixels, index, DateTimeOffset.UnixEpoch.AddSeconds(time)),
                pose, time, [], default, tracking, admitsPlacement: true, continuesMeasuredScenery: scenery,
                captureEvidence: new(time) { MeasuredLineage = lineage, HasMeasuredSceneryContinuity = scenery });
        }

        static byte[] Texture(int seed)
        {
            var pixels = new byte[width * height * 4];
            new Random(seed).NextBytes(pixels);
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            return pixels;
        }
    }

    internal static void TestGymUsesPlacedWalkedGroundAcrossOwnersAndKeepsPendingGroundLocal()
    {
        var atlas = new BotGymAtlasCoverage();
        var measured = GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 4
        };
        var placed = new GroundHypothesisAtlasLine(WalkedGroundMemory.WalkedSegmentId, new(20, 80), new(100, 80));
        var pending = new GroundHypothesisAtlasLine(WalkedGroundMemory.WalkedSegmentId, new(240, 92), new(320, 92));
        atlas.EnterRoom(7);
        Observe(0, default, 1, [placed]);
        Observe(1, new(24, 0), 2, []);
        if (!atlas.WalkedForPolicy([], 2).SequenceEqual([placed]))
            throw new InvalidOperationException("The Gym must project a committed footprint through its saved owner.");

        atlas.Interrupt(requireMatch: true);
        Observe(2, new(300, 0), 3, [pending]);
        if (!atlas.PlacementPending || atlas.WalkedForPolicy([], 3).Count != 0
            || !atlas.WalkedForPolicy([pending], 3).SequenceEqual([pending]))
            throw new InvalidOperationException("A pending chart must retain its own ground without borrowing placed coordinates.");

        atlas.EnterRoom(8);
        if (atlas.WalkedForPolicy([], 3).Count != 0)
            throw new InvalidOperationException("A new room cannot inherit saved ground from the previous room.");

        void Observe(int id, AtlasPoint camera, long owner, IReadOnlyList<GroundHypothesisAtlasLine> walk)
        {
            var time = id * .2;
            var pixels = new byte[320 * 180 * 4];
            var frame = new BgraFrame(320, 180, 320 * 4, pixels, id, DateTimeOffset.UnixEpoch.AddSeconds(time));
            atlas.Observe(frame, camera, time, [], default, measured, admitsPlacement: true,
                captureEvidence: new(time) { MeasuredLineage = owner }, walkedGround: walk);
        }
    }

    internal static void TestGymCarriesPendingFootprintsThroughStationaryImages()
    {
        var atlas = new BotGymAtlasCoverage();
        var measured = GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 4
        };
        var floor = new GroundHypothesisAtlasLine(WalkedGroundMemory.WalkedSegmentId, new(20, 80), new(100, 80));
        atlas.EnterRoom(7);
        Observe(1, 1, 2);
        Observe(2, 1.05, 1, [floor]);
        Observe(3, 1.2, 2, carried: [new(1, [floor])]);
        var saved = atlas.Snapshot.Observations[^1].GroundGeometry?.WalkedIslands?.Single();
        if (atlas.Snapshot.Observations.Count != 2 || saved?.Owner.UnplacedSource?.Lineage != 1
            || atlas.WalkedForPolicy([], 2).Count != 0)
            throw new InvalidOperationException("A stationary carrier must save the throttled walk without placing it.");
        Observe(4, 1.4, 1, [floor]);
        if (atlas.Snapshot.Observations.Count != 3 || atlas.Snapshot.Observations[^1].WalkedGround is not null
            || atlas.Snapshot.TrackingIslands[^1].Id != saved.Owner.Id
            || !atlas.WalkedForPolicy([], 1).SequenceEqual([floor]))
            throw new InvalidOperationException("The correct stationary image must place the already saved walk once.");

        void Observe(int id, double time, long owner,
            IReadOnlyList<GroundHypothesisAtlasLine>? current = null, IReadOnlyList<LiveWalkedGroundBatch>? carried = null)
        {
            var frame = new BgraFrame(320, 180, 320 * 4, new byte[320 * 180 * 4], id, DateTimeOffset.UnixEpoch);
            atlas.Observe(frame, default(AtlasPoint), time, [], default, measured, admitsPlacement: true,
                captureEvidence: new(time) { MeasuredLineage = owner, OtherWalks = carried ?? [] }, walkedGround: current);
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
                if (row.DetectedTerrain?.Doors is not null)
                    throw new InvalidOperationException("A loading cut without measured doorway floors must supply no door.");
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

    internal static void TestGymProjectsGroundedDoorwaysIntoPolicy()
    {
        if (!LiveDoorwayFloorEvidence.AtDepartureEdge(820, 1000, rightSide: true))
            throw new InvalidOperationException("An exact right-edge departure must retain the desktop cutoff.");
        foreach (var (direction, late) in new[] { (InputButtons.Left, false), (InputButtons.Right, false), (InputButtons.Right, true) })
        {
            var source = new BotVisualGymObservationSource();
            var right = direction == InputButtons.Right;
            var departureX = right ? 552 : 88;
            var arrivalX = right ? 88 : 552;
            const int departureRow = 239, arrivalRow = 261;
            var departure = DoorRoom(71, departureX, departureRow);
            var arrival = DoorRoom(191, arrivalX, arrivalRow);
            var blackout = new byte[departure.Length];
            var history = new BotObservationHistory(BotPolicyContract.LandingVersion);
            var fields = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!.ToList();
            BotCaptureObservation? lastDeparture = null;
            BotCaptureObservation? passage = null;
            BotCaptureObservation? latest = null;
            float[]? vector = null;
            for (var index = 0; index < (late ? 65 : 49); index++)
            {
                var leaving = index < 13;
                var loading = index is >= 13 and < 19;
                var pixels = leaving ? departure : loading ? blackout : arrival;
                var actorX = leaving ? departureX : arrivalX;
                var row = leaving ? departureRow : arrivalRow;
                var time = index / 10d;
                var canSeeActor = leaving || !late || index >= 58;
                var hud = new HudStencilResult([new(0, "full", new(12, 12, 10, 10), 1),
                    new(1, "full", new(24, 12, 10, 10), 1)], null, [], [], [], default, false, time);
                var reading = source.Observe(Frame(pixels, index), time, direction,
                    frame => canSeeActor ? [new ObjectDetection("game.playable-knight",
                        new((actorX - 9d) / Width, (row - 28d) / Height, 18d / Width, 13d / Height), .96f)
                        { SourceFrameIdentifier = frame.CaptureId }] : [], hud);
                if (reading.Observation is not { } observation) continue;
                latest = observation;
                if (leaving) lastDeparture = observation;
                vector = history.Append(observation, default, time);
                if (observation.DetectedTerrain?.Doors is { Count: > 0 }) passage = observation;
            }

            if (late)
            {
                if (passage is not null || latest is not { RoomId: 1, Grounded: true, AtlasView: not null })
                    throw new InvalidOperationException("Foot contact after the crossing expires must not become a doorway.");
                continue;
            }
            if (lastDeparture is not { RoomId: 0, AtlasView: { } before } || lastDeparture.DetectedTerrain?.Doors is not null
                || passage is not { RoomId: 1, AtlasView: { } after, DetectedTerrain.Doors: [var door], AuthoredTerrain: null })
                throw new InvalidOperationException($"A grounded visual crossing must produce the current room's door ({direction}).");
            if ((right ? after.Camera[0] - before.Camera[0] : before.Camera[0] - after.Camera[0]) < Width
                || Math.Abs(after.Camera[1] + Height - arrivalRow - (before.Camera[1] + Height - departureRow)) > 2
                || door.X1 != door.X2 || Math.Abs(door.Y1 - (Height - arrivalRow)) > 2 || door.Y2 <= door.Y1)
                throw new InvalidOperationException($"The confirmed opening must respect aligned floors and the placed camera: {door}.");
            var doorKnownIndex = fields.IndexOf("marking_door_known");
            if (vector is null || doorKnownIndex < 0 || vector[vector.Length - fields.Count + doorKnownIndex] != 1)
                throw new InvalidOperationException("The Gym policy vector must carry the visual door marking.");
        }

        static byte[] DoorRoom(ulong state, int actorX, int row)
        {
            var coarse = new byte[64 * 36];
            for (var index = 0; index < coarse.Length; index++)
            {
                state = unchecked(state * 6364136223846793005 + 1);
                coarse[index] = (byte)(24 + (state >> 32) % 44);
            }
            var pixels = new byte[Width * Height * 4];
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var value = (byte)(coarse[y / 10 * 64 + x / 10] + (x * 23 + y * 37) % 9);
                if (x is >= 35 and < 105 && y is >= 35 and < 55) value = 215;
                if (y == row || y == row + 1) value = 165;
                if (Math.Abs(x - actorX) <= 6 && y >= row - 26 && y <= row - 15)
                    value = y == row - 20 && Math.Abs(x - actorX) <= 1 ? (byte)0 : (byte)233;
                var offset = (y * Width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
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
