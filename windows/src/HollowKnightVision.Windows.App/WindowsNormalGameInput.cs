using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HollowKnightVision.Windows.Host;

namespace HollowKnightVision.Windows.App;

/// <summary>
/// Stock Unity must own foreground focus to receive keyboard input. Vision
/// stays above it during play and intercepts only its reserved shortcuts.
/// Editing, dialogs, and unrelated applications retain ordinary Windows focus.
/// All state is confined to the WPF dispatcher; no keystrokes are recorded.
/// </summary>
internal sealed class WindowsNormalGameInput : IDisposable
{
    private const nuint InjectionTag = 0x484B564E;
    private readonly Window owner;
    private readonly Func<bool> allowsGameInput;
    private readonly Action<Key> shortcut;
    private readonly Action<string> status;
    private readonly NormalGameShortcutState shortcuts = new();
    private readonly NormalGameHandoffState handoffState = new();
    private readonly KeyboardHook callback;
    private readonly DispatcherTimer monitor;
    private readonly Dictionary<int, HandoffKey> handoffKeys = [];
    private readonly Dictionary<(nint Handle, int ProcessId, int Generation, int VirtualKey), HandoffKey>
        pendingReleases = [];
    private HollowKnightWindow? target;
    private nint ownerHandle;
    private nint hook;
    private bool presenting;
    private bool disposed;
    private int focusGeneration;
    private HwndSource? mouseSource;
    private NativePoint? browsingPointer;
    private bool returningPointer;
    private FocusTransfer? focusTransfer;

    private sealed class HandoffKey(
        int virtualKey,
        ushort scanCode,
        bool extended,
        NormalGameInputTarget target)
    {
        internal readonly int VirtualKey = virtualKey;
        internal readonly ushort ScanCode = scanCode;
        internal readonly bool Extended = extended;
        internal readonly NormalGameInputTarget Target = target;
        internal bool Released;
        internal bool Injected;
    }

    private sealed class FocusTransfer(NormalGameInputTarget target)
    {
        internal readonly NormalGameInputTarget Target = target;
        internal Task<bool> Ready { get; set; } = Task.FromResult(false);
    }

    internal WindowsNormalGameInput(
        Window owner, Func<bool> allowsGameInput, Action<Key> shortcut, Action<string> status)
    {
        this.owner = owner;
        this.allowsGameInput = allowsGameInput;
        this.shortcut = shortcut;
        this.status = status;
        callback = OnKeyboard;
        monitor = new DispatcherTimer(DispatcherPriority.Input, owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        monitor.Tick += (_, _) => UpdateContext();
    }

    internal void Start(HollowKnightWindow game)
    {
        if (disposed) return;
        EndPresentation();
        target = game; // Caller has validated the executable and chosen mode.
        ownerHandle = new WindowInteropHelper(owner).Handle;
        if (mouseSource is null)
        {
            RawInputDevice[] devices = [new() { UsagePage = 1, Usage = 2, Flags = 0x100, Target = ownerHandle }];
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enable mouse navigation.");
            mouseSource = HwndSource.FromHwnd(ownerHandle);
            mouseSource?.AddHook(OnWindowMessage);
        }
        if (hook == 0)
        {
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows could not enable Vision keyboard shortcuts.");
        }
        monitor.Start();
    }

    internal void ReserveLocalKey(Key key) => shortcuts.Capture(KeyInterop.VirtualKeyFromKey(key));

    internal Task<bool> PrepareForInputAsync() =>
        EnsureFocusTransfer()?.Ready ?? Task.FromResult(false);

    private FocusTransfer? EnsureFocusTransfer()
    {
        if (!CanUseTarget() || !allowsGameInput()) return null;
        var game = target!;
        FlushPendingReleasesForForeground();
        if (focusTransfer is { } current
            && current.Target.Handle == game.Handle
            && current.Target.ProcessId == game.ProcessId
            && current.Target.Generation == focusGeneration
            && (!current.Ready.IsCompleted
                || current.Ready.Status == TaskStatus.RanToCompletion && current.Ready.Result
                && GetForegroundWindow() == game.Handle))
        {
            return current;
        }
        var foreground = GetForegroundWindow();
        // Do not take focus away from another program, an owned dialog or a menu.
        if (foreground != ownerHandle && foreground != game.Handle) return null;
        var generation = ++focusGeneration;
        var identity = new NormalGameInputTarget(game.Handle, game.ProcessId, generation);
        var transfer = new FocusTransfer(identity);
        focusTransfer = transfer;
        transfer.Ready = PrepareForInputCoreAsync(game, identity);
        return transfer;
    }

    private async Task<bool> PrepareForInputCoreAsync(
        HollowKnightWindow game,
        NormalGameInputTarget identity)
    {
        if (!CanUseTarget(identity) || !allowsGameInput())
        {
            EndPresentation(identity.Generation);
            return false;
        }
        var foreground = GetForegroundWindow();
        if (foreground != ownerHandle && foreground != game.Handle)
        {
            EndPresentation(identity.Generation);
            return false;
        }
        if (foreground == ownerHandle && GetCursorPos(out var pointer)) browsingPointer = pointer;
        presenting = true;
        RaisePresentation();
        if (foreground != game.Handle) _ = SetForegroundWindow(game.Handle);
        // Activation crosses input queues. SetForegroundWindow returning does
        // not mean Unity has processed WM_ACTIVATE yet. Keep Vision above it
        // throughout the handoff, including this asynchronous settling period.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (!CanUseTarget(identity) || !allowsGameInput())
            {
                EndPresentation(identity.Generation);
                return false;
            }
            foreground = GetForegroundWindow();
            if (foreground == game.Handle)
            {
                RaisePresentation();
                await Task.Delay(50);
                if (!CanUseTarget(identity) || !presenting
                    || GetForegroundWindow() != game.Handle)
                {
                    EndPresentation(identity.Generation);
                    return false;
                }
                RaisePresentation();
                return true;
            }
            if (foreground != 0 && foreground != ownerHandle) break;
            await Task.Delay(10);
        }
        EndPresentation(identity.Generation);
        return false;
    }

    internal bool ForwardFirstKey(Key key, bool isRepeat)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (disposed || !allowsGameInput() || !NormalGameInputPolicy.IsGameKey(virtualKey)) return false;
        if (isRepeat) return true;
        var transfer = EnsureFocusTransfer();
        if (transfer is null)
        {
            status("Click the game view to resume keyboard controls.");
            return true;
        }
        if (!handoffState.Press(virtualKey, transfer.Target, isRepeat: false)) return true;
        var scan = MapVirtualKey((uint)virtualKey, 4); // MAPVK_VK_TO_VSC_EX
        if (scan == 0)
        {
            handoffState.Release(virtualKey);
            return false;
        }
        var held = new HandoffKey(virtualKey, (ushort)(scan & 0xFF),
            (scan & 0xFF00) == 0xE000 || key is Key.Left or Key.Right or Key.Up or Key.Down,
            transfer.Target);
        handoffKeys.Add(virtualKey, held);
        _ = ForwardFirstKeyAsync(held, transfer.Ready);
        return true;
    }

    private async Task ForwardFirstKeyAsync(HandoffKey held, Task<bool> focusReady)
    {
        var virtualKey = held.VirtualKey;
        if (!await focusReady)
        {
            var removed = RemoveHandoff(held);
            if (!disposed && removed) status("Click the game view to resume keyboard controls.");
            return;
        }
        // Re-check immediately before SendInput; never inject into another app.
        if (!CanUseTarget(held.Target)
            || GetForegroundWindow() != held.Target.Handle
            || !SendKey(held, down: true))
        {
            var removed = RemoveHandoff(held);
            if (!disposed && removed)
                status("Keyboard input was blocked. Run Vision and Hollow Knight at the same privilege level.");
            return;
        }
        held.Injected = true;
        // A very quick release can arrive while Windows changes focus. Pair
        // that replayed press with a release after one game frame in that case.
        if (held.Released) _ = ReleaseQuickTapAsync(held);
    }

    internal void UpdateContext()
    {
        if (disposed) return;
        FlushPendingReleasesForForeground();
        if (!CanUseTarget() || !allowsGameInput())
        {
            if (presenting && target is not null && GetForegroundWindow() == target.Handle
                && owner.IsVisible && owner.IsEnabled && owner.WindowState != WindowState.Minimized)
                ActivateVision();
            EndPresentation();
            return;
        }
        var foreground = GetForegroundWindow();
        if (foreground != ownerHandle && foreground != target!.Handle) EndPresentation();
    }

    private bool CanUseTarget()
    {
        if (disposed || target is null || !owner.IsVisible || !owner.IsEnabled
            || owner.WindowState == WindowState.Minimized || !IsWindow(target.Handle)
            || IsIconic(target.Handle)) return false;
        _ = GetWindowThreadProcessId(target.Handle, out var processId);
        return processId == target.ProcessId;
    }

    private bool CanUseTarget(NormalGameInputTarget identity) =>
        identity.Generation == focusGeneration
        && target is { } game
        && game.Handle == identity.Handle
        && game.ProcessId == identity.ProcessId
        && CanUseTarget();

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code < 0 || disposed) return CallNextHookEx(hook, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardData>(data);
        if (key.ExtraInfo == InjectionTag) return CallNextHookEx(hook, code, message, data);
        FlushPendingReleasesForForeground();
        var down = message is 0x100 or 0x104;
        var up = message is 0x101 or 0x105;
        if (!down && !up) return CallNextHookEx(hook, code, message, data);
        var virtualKey = (int)key.VirtualKey;
        if (up && handoffKeys.TryGetValue(virtualKey, out var held))
        {
            held.Released = true;
            // The physical release reaches only the current foreground. Keep
            // target-bound ownership if another application owns focus, then
            // synthesize the release when that validated game returns.
            if (held.Injected && GetForegroundWindow() == held.Target.Handle)
                RemoveHandoff(held);
        }
        if (up)
        {
            var foreground = GetForegroundWindow();
            foreach (var pending in pendingReleases.Where(item =>
                         item.Value.VirtualKey == virtualKey).ToArray())
            {
                pending.Value.Released = true;
                // The physical key-up reaches this exact foreground target.
                if (foreground == pending.Value.Target.Handle
                    && TargetWindowExists(pending.Value.Target))
                    pendingReleases.Remove(pending.Key);
            }
        }
        var modified = (key.Flags & 0x20) != 0 || IsDown(0x11) || IsDown(0x5B) || IsDown(0x5C);
        var canIntercept = presenting && CanUseTarget() && allowsGameInput()
            && GetForegroundWindow() == target!.Handle;
        if (NormalGameInputPolicy.RelayAction(
            virtualKey, down, canIntercept, modified) == NormalGameRelayAction.Suppress)
        {
            return 1;
        }
        if (shortcuts.Handle(virtualKey, down, canIntercept, modified, out var invoke))
        {
            if (invoke)
            {
                // Keep the hook fast. UI work and game capture run afterwards.
                _ = owner.Dispatcher.InvokeAsync(() => InvokeShortcut(virtualKey), DispatcherPriority.Input);
            }
            return 1;
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private void InvokeShortcut(int virtualKey)
    {
        if (!CanUseTarget() || !presenting) return;
        var foreground = GetForegroundWindow();
        if (foreground != target!.Handle && foreground != ownerHandle) return;
        ActivateVision();
        if (GetForegroundWindow() != ownerHandle) return;
        owner.Focus();
        shortcut(KeyInterop.KeyFromVirtualKey(virtualKey));
        UpdateContext();
    }

    private nint OnWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Raw device motion, unlike WM_MOUSEMOVE, is not produced by Unity
        // recentering its cursor. Do not fight SetCursorPos or globally unclip it.
        if (message != 0x00FF || !presenting || returningPointer || !CanUseTarget()
            || GetForegroundWindow() != target!.Handle) return 0;
        var size = (uint)Marshal.SizeOf<RawMouseInput>();
        var read = GetRawInputData(lParam, 0x10000003, out var input, ref size,
            (uint)Marshal.SizeOf<RawInputHeader>());
        if (read != Marshal.SizeOf<RawMouseInput>() || input.Header.Type != 0
            || input.Mouse.X == 0 && input.Mouse.Y == 0 && input.Mouse.Buttons == 0) return 0;
        ReturnPointerAfterDeviceMotion(input.Mouse.X, input.Mouse.Y, (input.Mouse.Flags & 1) == 0);
        return 0; // WPF/DefWindowProc still owns WM_INPUT cleanup.
    }

    private void ReturnPointerAfterDeviceMotion(int dx, int dy, bool relative)
    {
        if (!presenting || returningPointer || !CanUseTarget() || GetForegroundWindow() != target!.Handle) return;
        returningPointer = true;
        _ = owner.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (!presenting || !CanUseTarget() || GetForegroundWindow() != target!.Handle) return;
                ActivateVision();
                if (GetForegroundWindow() != ownerHandle) return;
                EndPresentation();
                if (browsingPointer is { } previous)
                {
                    // Resume where the user left the pointer before the key
                    // handoff, rather than at Unity's locked center.
                    _ = SetCursorPos(previous.X + (relative ? dx : 0),
                        previous.Y + (relative ? dy : 0));
                    browsingPointer = null;
                }
            }
            finally { returningPointer = false; }
        }, DispatcherPriority.Input);
    }

    private async Task ReleaseQuickTapAsync(HandoffKey key)
    {
        await Task.Delay(32);
        if (disposed || !handoffKeys.TryGetValue(key.VirtualKey, out var current)
            || !ReferenceEquals(key, current)) return;
        if (!TargetWindowExists(key.Target))
        {
            RemoveHandoff(key);
            return;
        }
        if (CanUseTarget(key.Target) && GetForegroundWindow() == key.Target.Handle
            && SendKey(key, down: false))
        {
            RemoveHandoff(key);
            return;
        }
        MoveToPendingRelease(key);
    }

    private void ActivateVision()
    {
        RetireActiveHandoffs();
        // The selected game owns the input queue. Join it only for this
        // user-requested focus change, then detach immediately.
        var gameThread = GetWindowThreadProcessId(target!.Handle, out _);
        var thread = GetCurrentThreadId();
        var attached = thread != gameThread && AttachThreadInput(thread, gameThread, true);
        try { _ = SetForegroundWindow(ownerHandle); }
        finally { if (attached) _ = AttachThreadInput(thread, gameThread, false); }
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool SendKey(HandoffKey key, bool down)
    {
        NativeInput[] inputs = [new()
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    ScanCode = key.ScanCode,
                    Flags = 0x0008u | (key.Extended ? 1u : 0u) | (down ? 0u : 2u),
                    ExtraInfo = InjectionTag
                }
            }
        }];
        return SendInput(1, inputs, Marshal.SizeOf<NativeInput>()) == 1;
    }

    private bool RemoveHandoff(HandoffKey key)
    {
        if (!handoffKeys.TryGetValue(key.VirtualKey, out var current)
            || !ReferenceEquals(key, current)) return false;
        handoffKeys.Remove(key.VirtualKey);
        handoffState.Release(key.VirtualKey);
        return true;
    }

    private void MoveToPendingRelease(HandoffKey key)
    {
        if (!handoffKeys.TryGetValue(key.VirtualKey, out var current)
            || !ReferenceEquals(key, current)) return;
        handoffKeys.Remove(key.VirtualKey);
        handoffState.Release(key.VirtualKey);
        key.Released = true;
        pendingReleases[(key.Target.Handle, key.Target.ProcessId,
            key.Target.Generation, key.VirtualKey)] = key;
    }

    private void FlushPendingReleasesForForeground()
    {
        var foreground = GetForegroundWindow();
        foreach (var item in pendingReleases.ToArray())
        {
            var key = item.Value;
            if (!TargetWindowExists(key.Target))
            {
                pendingReleases.Remove(item.Key);
                continue;
            }
            if (!key.Injected || !key.Released || foreground != key.Target.Handle) continue;
            if (SendKey(key, down: false)) pendingReleases.Remove(item.Key);
        }
    }

    private static bool TargetWindowExists(NormalGameInputTarget identity)
    {
        if (!IsWindow(identity.Handle)) return false;
        _ = GetWindowThreadProcessId(identity.Handle, out var processId);
        return processId == identity.ProcessId;
    }

    private void RetireActiveHandoffs()
    {
        var foreground = GetForegroundWindow();
        foreach (var key in handoffKeys.Values.ToArray())
        {
            if (!key.Injected || !TargetWindowExists(key.Target))
            {
                handoffKeys.Remove(key.VirtualKey);
                continue;
            }
            key.Released = true;
            if (foreground == key.Target.Handle && SendKey(key, down: false))
            {
                handoffKeys.Remove(key.VirtualKey);
                continue;
            }
            pendingReleases[(key.Target.Handle, key.Target.ProcessId,
                key.Target.Generation, key.VirtualKey)] = key;
            handoffKeys.Remove(key.VirtualKey);
        }
        handoffState.ReleaseAll();
    }

    private void EndPresentation(int? expectedGeneration = null)
    {
        if (expectedGeneration is { } expected && expected != focusGeneration) return;
        RetireActiveHandoffs();
        focusGeneration++;
        focusTransfer = null;
        presenting = false;
        owner.Topmost = false;
        // A foreign foreground cannot safely receive synthetic releases meant
        // for Unity. Retired injected keys remain target-bound until that exact
        // window returns or disappears.
        FlushPendingReleasesForForeground();
    }

    private void RaisePresentation()
    {
        owner.Topmost = true;
        _ = SetWindowPos(ownerHandle, new nint(-1), 0, 0, 0, 0, 0x0013);
    }

    public void Dispose()
    {
        if (disposed) return;
        monitor.Stop();
        RetireActiveHandoffs();
        disposed = true;
        if (mouseSource is not null)
        {
            mouseSource.RemoveHook(OnWindowMessage);
            RawInputDevice[] devices = [new() { UsagePage = 1, Usage = 2, Flags = 1 }]; // RIDEV_REMOVE
            _ = RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>());
            mouseSource = null;
        }
        if (hook != 0) _ = UnhookWindowsHookEx(hook);
        hook = 0;
        shortcuts.Clear();
        EndPresentation();
    }

    private delegate nint KeyboardHook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { internal ushort UsagePage, Usage; internal uint Flags; internal nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { internal uint Type, Size; internal nint Device; internal nuint WParam; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        internal ushort Flags;
        internal uint Buttons, RawButtons;
        internal int X, Y;
        internal uint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouseInput { internal RawInputHeader Header; internal RawMouse Mouse; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint handle, uint command, out RawMouseInput data, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        internal uint VirtualKey, ScanCode, Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, KeyboardHook callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint thread, uint otherThread, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
}
