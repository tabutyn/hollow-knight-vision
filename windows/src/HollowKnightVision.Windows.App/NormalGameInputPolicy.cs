namespace HollowKnightVision.Windows.App;

internal readonly record struct NormalGameInputTarget(
    nint Handle,
    int ProcessId,
    int Generation);

internal enum NormalGameRelayAction
{
    Pass,
    Suppress
}

// Virtual-key values keep the routing contract testable without a desktop.
internal static class NormalGameInputPolicy
{
    internal static bool IsVisionShortcut(int key) => key is
        0x47 or 0x4C or 0x53 or 0x4D or 0x48 or 0x4F or 0x4B; // G L S M H O K

    internal static bool AllowsGameInput(string workspace, bool settings, bool textInput) =>
        workspace == "Gameplay" && !settings && !textInput;

    internal static bool IsGameKey(int key) => !IsVisionShortcut(key) &&
        (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F
            or >= 0x25 and <= 0x28 // arrows
            or 0x08 or 0x09 or 0x0D or 0x1B or 0x20); // Backspace, Tab, Enter, Esc, Space

    // Windows cannot keep a modified key local once Unity owns foreground
    // focus. Passing both halves preserves OS shortcuts (Alt+Tab, Alt+F4,
    // Win+D, and Ctrl combinations) and prevents a swallowed transition.
    internal static NormalGameRelayAction RelayAction(
        int key,
        bool down,
        bool canIntercept,
        bool modified) => NormalGameRelayAction.Pass;
}

/// <summary>
/// Target-bound state for keys synthesized during the one-key Windows focus
/// handoff. Once Unity owns focus, ordinary hardware input bypasses this state.
/// </summary>
internal sealed class NormalGameHandoffState
{
    private readonly HashSet<int> held = [];

    internal NormalGameInputTarget? Target { get; private set; }
    internal IReadOnlySet<int> HeldKeys => held;

    internal bool Press(
        int key,
        NormalGameInputTarget target,
        bool isRepeat)
    {
        if (isRepeat || Target is { } current && current != target || !held.Add(key))
        {
            return false;
        }
        Target = target;
        return true;
    }

    internal bool Release(int key)
    {
        if (!held.Remove(key)) return false;
        if (held.Count == 0) Target = null;
        return true;
    }

    internal IReadOnlySet<int> ReleaseAll()
    {
        var released = held.ToHashSet();
        held.Clear();
        Target = null;
        return released;
    }
}

// A reserved shortcut owns its complete press/repeat/release sequence, even
// when the initial press changes which application or workspace has focus.
internal sealed class NormalGameShortcutState
{
    private readonly HashSet<int> suppressed = [];

    internal void Capture(int key) => suppressed.Add(key);

    internal bool Handle(int key, bool down, bool canIntercept, bool modified, out bool invoke)
    {
        invoke = false;
        if (!down) return suppressed.Remove(key);
        if (suppressed.Contains(key)) return true;
        if (!canIntercept || modified || !NormalGameInputPolicy.IsVisionShortcut(key)) return false;
        suppressed.Add(key);
        invoke = true;
        return true;
    }

    internal void Clear() => suppressed.Clear();
}
