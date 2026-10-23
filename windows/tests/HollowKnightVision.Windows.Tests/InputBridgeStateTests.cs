using HollowKnightVision.Windows.Core;

internal static class InputBridgeStateTests
{
    private static void Check(bool value, string message = "Input bridge state parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    public static void TestPhysicalReconciliationDoesNotCancelAVisibleLocalPressWhenGlobalPollingIsUnavailable()
    {
        var reconciler = new PhysicalKeyReconciler();

        Check(reconciler.ReleasedKeyCodes(
            new HashSet<int> { 124 },
            _ => false).Count == 0);
        Check(reconciler.GloballyObservedKeyCodes.Count == 0);
    }

    public static void TestPhysicalReconciliationReleasesOnlyAfterObservingTheHeldKey()
    {
        var reconciler = new PhysicalKeyReconciler();

        Check(reconciler.ReleasedKeyCodes(
            new HashSet<int> { 124 },
            _ => true).Count == 0);
        Check(reconciler.GloballyObservedKeyCodes.SetEquals([124]));
        Check(reconciler.ReleasedKeyCodes(
            new HashSet<int> { 124 },
            _ => false).SetEquals([124]));
    }

    public static void TestSenderEmitsOrderedCompleteTransitionsAndNeutralRelease()
    {
        var sessionId = Guid.NewGuid();
        var sender = new InputBridgeSenderState(sessionId);

        var enabled = sender.SetEnabled(true);
        var left = sender.SetHeld(InputButtons.Left, true)
            ?? throw new Exception("Left press did not emit a snapshot.");
        var jump = sender.SetHeld(InputButtons.ActionZ, true)
            ?? throw new Exception("Jump press did not emit a snapshot.");
        var release = sender.ReleaseAll();

        Check(new[] { enabled.Sequence, left.Sequence, jump.Sequence, release.Sequence }
            .SequenceEqual<ulong>([0, 1, 2, 3]));
        Check(left.HeldButtons == InputButtons.Left);
        Check(jump.HeldButtons == (InputButtons.Left | InputButtons.ActionZ));
        Check(!release.Enabled);
        Check(release.HeldButtons == InputButtons.None);
    }

    public static void TestFreshPressEnablesAndHoldsInOneSnapshot()
    {
        var sessionId = Guid.NewGuid();
        var sender = new InputBridgeSenderState(sessionId);

        var firstPress = sender.BeginFreshPress(InputButtons.Right);
        Check(firstPress.Sequence == 0);
        Check(firstPress.Enabled);
        Check(firstPress.HeldButtons == InputButtons.Right);
    }
}
