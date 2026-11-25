using HollowKnightVision.Windows.Core;

internal static class CaptureIslandLineageTests
{
    internal static void TestUncommittedMeasuredReferenceBridgesPredictionGap()
    {
        var ledger = new CaptureIslandLineage();
        var first = ledger.Observe(1, "room", 1, measured: true, null);
        Check(first is not null, "The first measured image needs an owner.");
        Check(ledger.Observe(1, "room", 1.1, measured: true, 1) == first,
            "An uncommitted measured frame needs the same owner as its reference.");
        Check(ledger.Observe(1, "room", 1.2, measured: false, 1.1) is null,
            "A predicted image cannot carry measured ownership.");
        Check(ledger.Observe(1, "room", 1.25, measured: true, 1.1) == first,
            "An exact measured reference must recover the original island across a prediction.");
        var second = ledger.Observe(1, "room", 1.3, measured: true, 1.2);
        Check(second is not null && second != first, "A prediction-only reference needs a different island.");
        Check(ledger.Observe(1, "room", 1.4, measured: true, 1.2) == second,
            "Repeated matches to a bootstrap image should agree on one new island.");
        Check(ledger.Observe(1, "room", 2.2, measured: true, 1.1000007) == first,
            "An actual older reference must restore its island across a gap.");
        Check(ledger.Observe(1, "room", 2.21, measured: true, null) is { } missing && missing != first,
            "A short interval without a reference does not prove ownership.");
    }

    internal static void TestBootstrapAndResetRetainUniqueMeasuredOwners()
    {
        var ledger = new CaptureIslandLineage();
        Check(ledger.Observe(1, "room", 0, measured: false, null) is null,
            "The bootstrap image cannot acquire measured ownership.");
        var established = ledger.Observe(1, "room", .02, measured: true, 0);
        Check(established is not null && ledger.Observe(1, "room", .04, measured: true, 0) == established,
            "The first actual measurement can use the bootstrap as a reference without changing its original admission.");
        ledger.Reset();
        var reopened = ledger.Observe(1, "room", 1, measured: true, .02);
        Check(reopened is { } newOwner && newOwner > established!.Value,
            "A capture reset must fence out old references and advance the measurement identity.");
    }

    internal static void TestDiscontinuitiesFenceOldImageReferences()
    {
        var ledger = new CaptureIslandLineage();
        var first = ledger.Observe(1, "room", 1, measured: true, null);
        var corrected = ledger.Observe(1, "room", 1.1, measured: true, 1, discontinuity: true);
        Check(corrected is not null && corrected != first, "A coordinate correction must break the old association.");
        var changedRoom = ledger.Observe(1, "other", 1.2, measured: true, 1.1);
        Check(changedRoom is not null && changedRoom != corrected, "The same timestamp in another room cannot supply ownership.");
        var generation = ledger.Observe(2, "other", 1.3, measured: true, 1.2);
        Check(generation is not null && generation != changedRoom, "A reference from a prior capture generation cannot be reused.");
        var rewound = ledger.Observe(2, "other", .2, measured: true, 1.3);
        Check(rewound is not null && rewound != generation && rewound != first, "A rewound capture clock needs new ownership.");
        Check(ledger.Observe(2, "other", double.PositiveInfinity, measured: true, .2) is null,
            "Invalid capture timestamps cannot supply ownership.");
        ledger.Reset();
        Check(ledger.Observe(2, "other", .3, measured: true, .2) != rewound,
            "Reset must discard old references.");
    }

    internal static void TestRestoredVisitUsesMeasuredOwnerAndKeyframeEdges()
    {
        var ledger = new CaptureIslandLineage();
        var tracker = new LiveWorldTracker();
        Capture(0, 1, null);
        ledger.Observe(1, "room", 1.1, measured: true, 1);
        Capture(1, 1.2, null);
        var owners = tracker.Snapshot.TrackingIslands;
        Check(owners.Count == 2 && tracker.Snapshot.Keyframes.Count == 2,
            "Nearby placements without reference evidence need separate owners and recovery keyframes.");

        tracker = new LiveWorldTracker(LiveWorldJson.ReadSnapshot(LiveWorldJson.WriteSnapshot(tracker.Snapshot)));
        tracker.ResumeCommittedVisit(1);
        Capture(2, 1.3, 1.1);
        var restored = tracker.Snapshot.TrackingIslands;
        Check(restored.Select(owner => owner.Id).SequenceEqual(new[] { owners[1].Id, owners[0].Id })
            && restored[^1].Members.Select(member => member.ObservationId).SequenceEqual(new[] { 0, 2 }),
            "A resumed capture must rejoin its actual older owner and make it active.");
        Check(tracker.Snapshot.RelativeMotionEdges.Single() is { FromObservationId: 0, ToObservationId: 2 },
            "Keyframe edges must follow measured ownership instead of adjacent captures.");

        Capture(3, 1.4, 1.3, pending: true);
        Check(tracker.LatestIslandAwaitsPlacement && tracker.Snapshot.TrackingIslands.Count == 3
            && tracker.Snapshot.RelativeMotionEdges.Count == 1
            && tracker.Snapshot.UnplacedObservationIds().SetEquals(new[] { 3 }),
            "The same image lineage cannot bypass a pending placement boundary.");
        tracker.BeginVisit();
        Capture(4, 1.5, 1.4, pending: true);
        Check(tracker.Snapshot.TrackingIslands.Count == 4,
            "A fresh visit must not append to a persisted owner without resuming it.");

        void Capture(int id, double time, double? reference, bool pending = false)
        {
            var lineage = ledger.Observe(1, "room", time, measured: true, reference);
            var frame = new BgraFrame(64, 32, 64 * 4, new byte[64 * 32 * 4], id, DateTimeOffset.UnixEpoch);
            var camera = new AtlasPoint(id == 1 ? 4 : 24 * id, 0);
            tracker.Ingest(id, time, frame, camera, 64, captureGeneration: 1,
                searchForClosure: false, captureEvidence: new(time) { MeasuredLineage = lineage },
                placementPending: pending);
        }
    }

    internal static void TestOldReferencesExpireWithoutDroppingRecentChain()
    {
        var ledger = new CaptureIslandLineage();
        var original = ledger.Observe(1, "room", 0, measured: true, null);
        for (var index = 1; index <= 16_384; index++)
            Check(ledger.Observe(1, "room", index, measured: true, index - 1) == original,
                "Eviction must preserve the active measured chain.");
        var recovered = ledger.Observe(1, "room", 16_385, measured: true, 0);
        Check(recovered is not null && recovered != original,
            "An evicted reference cannot establish continuity across the retained history.");
        Check(ledger.Observe(1, "room", 16_386, measured: true, 16_384) == original,
            "A newer exact reference can still reach the earlier island.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
