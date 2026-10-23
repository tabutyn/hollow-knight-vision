using HollowKnightVision.Windows.Core;

internal static class BroadGroundRecoveryGateTests
{
    private static void Check(bool value, string message = "Broad ground recovery parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    public static void TestLandingReboundNeedsTwoBroadIndependentMeasurements()
    {
        var gate = new SparseGroundRecoveryGate();
        Check(!gate.Accepts(
            new AtlasPoint(2221, 2),
            new AtlasPoint(2226, 38),
            640,
            1,
            15,
            16.188));
        Check(gate.Accepts(
            new AtlasPoint(2219, 5),
            new AtlasPoint(2226, 38),
            640,
            1.0165,
            15,
            4.583));
    }

    public static void TestBroadRecoveryRejectsWeakStaleDuplicateIncoherentOrExcessiveEvidence()
    {
        foreach (var mode in new[]
        {
            "weak-first",
            "weak-second",
            "expensive-first",
            "expensive-second",
            "stale",
            "duplicate",
            "horizontal",
            "vertical",
            "oversized",
            "opposite",
            "reset",
            "invalid",
        })
        {
            var gate = new SparseGroundRecoveryGate();
            _ = gate.Accepts(
                new AtlasPoint(2, 2),
                new AtlasPoint(0, 38),
                640,
                1,
                mode == "weak-first" ? 11 : 15,
                mode == "expensive-first" ? 19 : 16);
            if (mode == "reset")
            {
                gate.Reset();
            }
            var x = mode == "horizontal" ? 19 : mode == "duplicate" ? 2 : 0;
            var y = mode == "vertical" ? 15 : mode == "duplicate" ? 2 : 5;
            var currentY = mode == "oversized" ? 70 : mode == "opposite" ? -38 : 38;
            Check(!gate.Accepts(
                new AtlasPoint(x, y),
                new AtlasPoint(0, currentY),
                640,
                mode == "stale" ? 1.2 : 1.017,
                mode == "weak-second" ? 11 : 15,
                mode == "invalid" ? double.NaN : mode == "expensive-second" ? 7 : 4.5),
                mode);
        }
    }
}
