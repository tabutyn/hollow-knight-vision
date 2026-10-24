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

internal readonly record struct NormalGameKeyMessage(
    int Message,
    nint WParam,
    nint LParam);

// Virtual-key values keep the routing contract testable without a desktop.
internal static class NormalGameInputPolicy
{
    internal static bool IsVisionShortcut(int key) => key is
        0x47 or 0x4C or 0x53 or 0x4D or 0x48 or 0x4F or 0x4B // G L S M H O K
        or >= 0x31 and <= 0x34 // 1 2 3 4
        or >= 0x61 and <= 0x64; // numpad 1 2 3 4

    internal static bool AllowsGameInput(string workspace, bool settings, bool textInput) =>
        workspace == "Gameplay" && !settings && !textInput;

    internal static bool IsGameKey(int key) => !IsVisionShortcut(key) &&
        (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F
            or >= 0x25 and <= 0x28 // arrows
            or 0x08 or 0x09 or 0x0D or 0x1B or 0x20); // Backspace, Tab, Enter, Esc, Space

    internal static NormalGameKeyMessage KeyMessage(
        int virtualKey,
        ushort scanCode,
        bool extended,
        bool down,
        bool repeat)
    {
        // WM_KEYDOWN/WM_KEYUP use a hardware-style scan code. Bits 30 and 31
        // distinguish repeats and releases; arrows carry the extended-key bit.
        long lParam = 1L | ((long)scanCode << 16);
        if (extended) lParam |= 1L << 24;
        if (repeat || !down) lParam |= 1L << 30;
        if (!down) lParam |= 1L << 31;
        return new(down ? 0x0100 : 0x0101, new nint(virtualKey), new nint(lParam));
    }

    // Modified combinations remain local so Windows and Vision keep their
    // normal shortcut behavior; only unmodified gameplay keys are posted.
    internal static NormalGameRelayAction RelayAction(
        int key,
        bool down,
        bool canIntercept,
        bool modified) => NormalGameRelayAction.Pass;
}

/// <summary>
/// Target-bound state for keys posted to the selected game window while Vision
/// remains foreground.
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
