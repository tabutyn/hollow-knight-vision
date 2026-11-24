using HollowKnightVision.Windows.Core;

internal static class AtlasCaptureGapTests
{
    public static void TestOneMissCanReconnectOnlyToTheRetainedCapture()
    {
        var finder = new AtlasCaptureGap();
        var reference = Frame(0, 1);
        var missed = Frame(0, 2);
        var arriving = Frame(-10, 3);
        var origin = new AtlasPoint(24, 31);
        void StartGap()
        {
            finder.Reset();
            Check(finder.Complete(reference, [], null, 1, "room", 1, origin,
                currentReferenceAccepted: true, continuedAcrossGap: false, atlasFrameHeld: false)
                == AtlasCaptureGapState.None, "The measured capture should be stored.");
            Check(finder.Complete(missed, [], null, 1, "room", 1.03, origin,
                currentReferenceAccepted: false, continuedAcrossGap: false, atlasFrameHeld: true)
                == AtlasCaptureGapState.AwaitingRegistration, "One held frame should keep the exact reference.");
        }

        StartGap();
        Check(finder.Match(arriving, [], null, 2, "room", 1.06) is null, "The old capture generation leaked.");
        Check(finder.Match(arriving, [], null, 1, "other", 1.06) is null, "The old room reference leaked.");
        Check(finder.Match(arriving, [], null, 1, "room", 1.16) is null, "The old image exceeded its age limit.");
        var match = finder.Match(arriving, [], null, 1, "room", 1.06);
        Check(match is { ReferenceTimestamp: 1, Confidence: >= .8f }
            && Math.Abs(match.CameraPosition.X - origin.X - 10) < 1.5
            && Math.Abs(match.CameraPosition.Y - origin.Y) < 1.5,
            $"Expected the skip-frame displacement to use the accepted pose, received {match}.");
        Check(finder.Complete(arriving, [], null, 1, "room", 1.06, match!.CameraPosition,
            currentReferenceAccepted: true, continuedAcrossGap: true, atlasFrameHeld: false)
            == AtlasCaptureGapState.None, "The proven registration should keep the placed world.");

        StartGap();
        Check(finder.Complete(arriving, [], null, 1, "room", 1.06, origin,
            currentReferenceAccepted: true, continuedAcrossGap: false, atlasFrameHeld: false)
            == AtlasCaptureGapState.RequiresRecovery, "A relative pose after a lost capture must prove its reference.");
        StartGap();
        Check(finder.Complete(missed, [], null, 1, "room", 1.06, origin,
            currentReferenceAccepted: false, continuedAcrossGap: false, atlasFrameHeld: true)
            == AtlasCaptureGapState.RequiresRecovery, "A second consecutive miss must release the old placement.");
        Check(finder.Match(arriving, [], null, 1, "room", 1.08) is null, "Released placement left a usable reference.");
    }

    internal static void TestGymFloorlessIntervalKeepsOnlyVerifiedPlacement()
    {
        var masks = new GameplaySceneMasks(SceneRegions.Startup(), SceneRegions.Startup(), [], [], null);
        var origin = new AtlasPoint(24, 31);
        var size = new WorldSize(640, 360);
        var floor = GroundHypothesisTrackingResult.Empty with
        {
            HasConfirmedGround = true, PoseVerified = true, LocalTextureSupport = 8
        };
        foreach (var groundAgrees in new[] { true, false })
        {
            var coverage = new BotGymAtlasCoverage();
            var motion = new BotGymAtlasMotion();
            coverage.EnterRoom(7);
            Submit(Frame(0, 1), 1, origin, floor);
            var entry = coverage.View(origin, size, 5)!;

            Submit(Frame(-10, 2), 1.1, new(34, 31), source: FloorlessPoseCandidateSource.MaskedRegistration);
            // A motion estimate from the coarse scene is only presentation.
            Submit(Frame(-15, 3), 1.2, new(64, 31), source: FloorlessPoseCandidateSource.CoarseCaptureRate);
            var arriving = Frame(-20, 4);
            Check(motion.MatchSkipped(arriving, masks, 2, "7", 1.3) is null
                && motion.MatchSkipped(arriving, masks, 1, "8", 1.3) is null
                && motion.MatchSkipped(arriving, masks, 1, "7", 1.35) is null,
                "The skip must stay in its capture generation, room and age limit.");
            var skipped = motion.MatchSkipped(arriving, masks, 1, "7", 1.3);
            Check(skipped is { ReferenceTimestamp: 1.1, Confidence: >= .8f }
                && Math.Abs(skipped.CameraPosition.X - 44) < 1.5 && Math.Abs(skipped.CameraPosition.Y - 31) < 1.5,
                $"The skip must track from the previous accepted image. Received {skipped}.");
            Submit(arriving, 1.3, skipped!.CameraPosition, source: FloorlessPoseCandidateSource.MaskedRegistration,
                recoveredAt: skipped.ReferenceTimestamp);
            Check(!coverage.PlacementPending && coverage.Snapshot.Observations.Count == 1
                && coverage.View(origin, size, 5)?.EarnedCells == entry.EarnedCells,
                "Floorless measurements can preserve registration without adding Atlas images or earning cells.");

            var landing = groundAgrees ? new AtlasPoint(54, 31) : origin;
            Submit(Frame(-30, 5), 1.4, landing, floor);
            if (groundAgrees)
                Check(!coverage.PlacementPending && coverage.View(landing, size, 5) is not null
                    && coverage.Snapshot.UnplacedObservationIds().Count == 0,
                    "The independently corroborated landing must use its registered Atlas placement.");
            else
                Check(coverage.PlacementPending && coverage.View(landing, size, 5) is null
                    && coverage.PolicyView(landing, size, 5) is { } local && local.EarnedCells == entry.EarnedCells,
                    "A disagreement needs an independent place match, even at the last trusted ground pose.");

            void Submit(BgraFrame image, double time, AtlasPoint pose, GroundHypothesisTrackingResult? ground = null,
                FloorlessPoseCandidateSource? source = null, double? recoveredAt = null)
            {
                var observation = new VisualGameplayObservation(default!, null, ground is not null, true, 0, null, "test")
                {
                    FloorlessEvidence = source.HasValue ? new(null, pose, null, source, null, false) : null,
                    MotionBridgeConfidence = source.HasValue ? 1 : null,
                    RecoveredAtlasReferenceTimestamp = recoveredAt
                };
                var continuity = motion.Observe(image, masks, 1, "7", time, pose, observation,
                    coverage.CanContinueMeasuredScenery, ground is not null, measuredScenery: false, corrected: false);
                if (continuity.RequiresRecovery) coverage.Interrupt(requireMatch: true);
                coverage.Observe(image, ground is null ? null : pose, time, [], default,
                    ground ?? GroundHypothesisTrackingResult.Empty, admitsPlacement: true, retainBasis: continuity.RetainBasis);
            }
        }
    }

    private static BgraFrame Frame(int offset, long id)
    {
        const int width = 640, height = 360;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var col = (x - offset + 30) / 10;
            var row = y / 10;
            var sample = (byte)(25 + (col * 37 + row * 61 + col * row * 3) % 190);
            var index = (y * width + x) * 4;
            pixels[index] = sample;
            pixels[index + 1] = sample;
            pixels[index + 2] = sample;
            pixels[index + 3] = 255;
        }
        return new(width, height, width * 4, pixels, id, DateTimeOffset.UnixEpoch);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
