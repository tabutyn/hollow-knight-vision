using HollowKnightVision.Windows.App;

internal static class NormalGameKeyForwarderParityTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static void TestHoldsMultipleKeysWithoutRepeatingDownTransitions()
    {
        var state = new NormalGameHandoffState();
        var target = new NormalGameInputTarget(new nint(1), 10, 1);

        Check(state.Press(0x27, target, isRepeat: false), "First Right press was rejected.");
        Check(!state.Press(0x27, target, isRepeat: true), "Repeated Right press was accepted.");
        Check(!state.Press(0x27, target, isRepeat: false), "Duplicate Right press was accepted.");
        Check(state.Press(0x5A, target, isRepeat: false), "Chorded Z press was rejected.");
        Check(state.HeldKeys.SetEquals([0x27, 0x5A]), "Chord ownership was not retained.");
        Check(state.Release(0x27), "Right release was rejected.");
        Check(state.Target == target, "Target cleared while Z remained held.");
        Check(state.Release(0x5A), "Z release was rejected.");
        Check(state.Target is null, "Target survived the final release.");
        Check(!state.Release(0x5A), "Duplicate release was accepted.");
    }

    public static void TestFocusLossReleasesAllAndRequiresFreshDown()
    {
        var state = new NormalGameHandoffState();
        var target = new NormalGameInputTarget(new nint(1), 10, 1);

        Check(state.Press(0x28, target, isRepeat: false), "First Down press was rejected.");
        Check(state.Press(0x58, target, isRepeat: false), "Chorded X press was rejected.");
        Check(state.ReleaseAll().SetEquals([0x28, 0x58]), "Focus loss did not drain every held key.");
        Check(state.HeldKeys.Count == 0 && state.Target is null, "Focus loss retained ownership.");
        Check(!state.Press(0x28, target with { Generation = 2 }, isRepeat: true),
            "A repeat incorrectly began the replacement focus generation.");
        Check(state.Press(0x28, target with { Generation = 2 }, isRepeat: false),
            "A fresh down did not begin the replacement focus generation.");
    }

    public static void TestReplacementGameCannotInheritHeldKeys()
    {
        var state = new NormalGameHandoffState();
        var first = new NormalGameInputTarget(new nint(1), 10, 1);
        var replacement = new NormalGameInputTarget(new nint(2), 20, 2);

        Check(state.Press(0x27, first, isRepeat: false), "First target did not acquire Right.");
        Check(!state.Press(0x5A, replacement, isRepeat: false),
            "Replacement target inherited a chord from the first game.");
        Check(state.Target == first, "Rejected replacement changed target ownership.");
        Check(state.ReleaseAll().SetEquals([0x27]), "First generation did not drain exactly Right.");
        Check(state.Press(0x5A, replacement, isRepeat: false),
            "Replacement target could not acquire a fresh Z press.");
        Check(state.Target == replacement, "Replacement target ownership was not recorded.");
    }

    public static void TestWorkspaceAndSettingsRouting()
    {
        Check(!NormalGameInputPolicy.AllowsGameInput("Label", settings: false, textInput: false),
            "Labeling must retain keyboard ownership.");
        Check(!NormalGameInputPolicy.AllowsGameInput("Model", settings: false, textInput: false),
            "Reviewing must retain keyboard ownership.");
        Check(!NormalGameInputPolicy.AllowsGameInput("Gameplay", settings: true, textInput: false),
            "Windows settings must retain focus instead of forwarding arrows to Unity.");
        Check(NormalGameInputPolicy.AllowsGameInput("Gameplay", settings: false, textInput: false),
            "Gameplay did not enable ordinary game input.");
        foreach (var key in new[] { 0x25, 0x26, 0x27, 0x28, 0x5A, 0x58, 0x0D, 0x1B })
            Check(NormalGameInputPolicy.IsGameKey(key), $"Expected game key 0x{key:X} was not routed.");
        Check(!NormalGameInputPolicy.IsGameKey(0x4C), "Vision L shortcut leaked into game routing.");
        Check(!NormalGameInputPolicy.IsGameKey(0x53), "Vision S shortcut leaked into game routing.");
        Check(NormalGameInputPolicy.RelayAction(0x58, down: true, canIntercept: true, modified: true)
              == NormalGameRelayAction.Pass,
            "Modified game key-down was swallowed instead of preserving the OS shortcut.");
        Check(NormalGameInputPolicy.RelayAction(0x58, down: false, canIntercept: true, modified: true)
              == NormalGameRelayAction.Pass,
            "Modified game key release was suppressed and could strand state.");
    }

    public static void TestBackgroundMessagesEncodePhysicalArrowTransitions()
    {
        var down = NormalGameInputPolicy.KeyMessage(
            virtualKey: 0x27, scanCode: 0x4D, extended: true, down: true, repeat: false);
        Check(down.Message == 0x0100 && down.WParam == new nint(0x27),
            "Right press was not encoded as WM_KEYDOWN.");
        Check(((long)down.LParam & 0x00FF0000) == 0x004D0000
              && ((long)down.LParam & (1L << 24)) != 0
              && ((long)down.LParam & (3L << 30)) == 0,
            "Right press lost its scan code or extended-key transition bits.");

        var repeat = NormalGameInputPolicy.KeyMessage(
            virtualKey: 0x27, scanCode: 0x4D, extended: true, down: true, repeat: true);
        Check(repeat.Message == 0x0100 && ((long)repeat.LParam & (1L << 30)) != 0
              && ((long)repeat.LParam & (1L << 31)) == 0,
            "Repeated Right press did not set the previous-state bit.");

        var up = NormalGameInputPolicy.KeyMessage(
            virtualKey: 0x27, scanCode: 0x4D, extended: true, down: false, repeat: false);
        Check(up.Message == 0x0101 && ((long)up.LParam & (3L << 30)) == (3L << 30),
            "Right release was not encoded as a completed WM_KEYUP transition.");
    }
}
