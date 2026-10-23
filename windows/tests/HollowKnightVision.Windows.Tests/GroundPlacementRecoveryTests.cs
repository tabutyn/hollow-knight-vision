using HollowKnightVision.Windows.Core;

internal static class GroundPlacementRecoveryTests
{
    private static void Check(bool value, string message = "Ground placement recovery parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static GroundPlacementProposal Proposal(double x, double y, int support = 14,
        double error = 6.7, bool distinct = true, bool global = false, bool established = true) =>
        new(new(x, y), support, error, 3, global ? 4 : 0, global, distinct, established);
    private static GroundPlacementRecovery Trusted()
    {
        var gate = new GroundPlacementRecovery();
        Check(gate.Accepts(Proposal(1454, 508.837), new(1454, 508.837), 640, 51.99, 1d / 60));
        return gate;
    }

    public static void TestLandingCorrection()
    {
        var gate = Trusted(); gate.MissingObservation();
        Check(!gate.Accepts(Proposal(1463, 473.837, 10, 17.4494), new(1454, 508.837), 640, 52.0694, .0165));
        Check(gate.RetainsCandidate(52.0694) && !gate.AllowsAtlasWrite);
        Check(gate.Accepts(Proposal(1466, 467.837), new(1454, 511.837), 640, 52.0854, .016));
        Check(gate.AllowsAtlasWrite && !gate.RetainsCandidate(52.0854));
    }
    public static void TestOrdinaryFallback()
    {
        var gate = Trusted(); gate.MissingObservation();
        Check(gate.Accepts(Proposal(1457, 505.837), new(1454, 511.837), 640, 52.0854, .016));
        Check(gate.AllowsAtlasWrite);
    }
    public static void TestRejectsBadVotes()
    {
        foreach (var mode in new[] { "duplicate", "stale", "weak", "error", "opposite", "oversized", "missing" })
        {
            var gate = Trusted(); gate.MissingObservation();
            _ = gate.Accepts(Proposal(1463, 473.837, 10, 17.4494), new(1454, 508.837), 640, 52.0694, .0165);
            if (mode == "missing") gate.MissingObservation();
            var y = mode == "opposite" ? 550 : mode == "oversized" ? 400 : 467.837;
            Check(!gate.Accepts(Proposal(1466, y, mode == "weak" ? 2 : 14,
                    mode == "error" ? 15 : 6.7, mode != "duplicate"),
                new(1454, 511.837), 640, mode == "stale" ? 52.21 : 52.0854, .016), mode);
            Check(!gate.AllowsAtlasWrite, mode);
        }
    }
    public static void TestGlobalVerification()
    {
        var gate = Trusted(); gate.MissingObservation();
        Check(gate.Accepts(Proposal(900, 100, 0, global: true), new(1454, 508), 640, 52.1, .016));
        Check(gate.AllowsAtlasWrite);
    }
    public static void TestProvisionalBootstrap()
    {
        var gate = new GroundPlacementRecovery();
        Check(gate.Accepts(Proposal(0, 0, 0, established: false), new(0, 0), 640, 1, null));
        gate.MissingObservation();
        Check(gate.Accepts(Proposal(100, 0, 0, established: false), new(0, 0), 640, 1.02, .02));
        Check(gate.Accepts(Proposal(100, 0), new(100, 0), 640, 1.04, .02));
    }
    public static void TestNormalAndReset()
    {
        var gate = Trusted();
        Check(gate.Accepts(Proposal(1458, 510), new(1454, 508), 640, 52.01, .02));
        gate.MissingObservation(); gate.Reset(); gate.MissingObservation();
        Check(gate.Accepts(Proposal(0, 0, 0), new(0, 0), 640, 1, null));
    }
    public static void TestSparseDescent()
    {
        var gate = new SparseGroundRecoveryGate();
        Check(!gate.Accepts(new(2, 80), new(0, 100), 640, 1, 3, 5.5));
        Check(gate.Accepts(new(4, 71), new(0, 98), 640, 1.017, 3, 9.8));
        Check(!gate.Accepts(new(6, 62), new(0, 96), 640, 1.034, 3, 7));
    }
    public static void TestSparseRejections()
    {
        foreach (var kind in new[] { "static", "wrong direction", "stale", "weak", "huge", "reset" })
        {
            var gate = new SparseGroundRecoveryGate();
            _ = gate.Accepts(new(2, 80), new(0, 100), 640, 1, 3, 5.5);
            var y = kind == "static" ? 80 : kind == "wrong direction" ? 89 : kind == "huge" ? -90 : 71;
            if (kind == "reset") gate.Reset();
            Check(!gate.Accepts(new(4, y), new(0, 98), 640,
                kind == "stale" ? 1.2 : 1.017, 3, kind == "weak" ? 20 : 9.8), kind);
        }
    }
}
