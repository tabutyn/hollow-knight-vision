using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HollowKnightVision.Windows.Host;

namespace HollowKnightVision.Windows.App;

/// <summary>
/// Sends supported gameplay keys directly to stock Hollow Knight while Vision
/// retains foreground focus. Keeping Vision active prevents Unity from raising
/// its window or locking the real pointer to the game-window center.
/// </summary>
internal sealed class WindowsNormalGameInput : IDisposable
{
    private readonly Window owner;
    private readonly Func<bool> allowsGameInput;
    private readonly Action<string> status;
    private readonly NormalGameHandoffState handoffState = new();
    private readonly DispatcherTimer monitor;
    private readonly Dictionary<int, HandoffKey> handoffKeys = [];
    private HollowKnightWindow? target;
    private nint ownerHandle;
    private bool disposed;
    private int generation;

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
        internal bool Injected;
    }

    internal WindowsNormalGameInput(
        Window owner, Func<bool> allowsGameInput, Action<Key> shortcut, Action<string> status)
    {
        this.owner = owner;
        this.allowsGameInput = allowsGameInput;
        this.status = status;
        _ = shortcut; // Local shortcuts stay in MainWindow's WPF input route.
        monitor = new DispatcherTimer(DispatcherPriority.Input, owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        monitor.Tick += (_, _) => UpdateContext();
    }

    internal void Start(HollowKnightWindow game)
    {
        if (disposed) return;
        RetireActiveHandoffs();
        target = game; // Caller has validated the executable and chosen mode.
        ownerHandle = new WindowInteropHelper(owner).Handle;
        generation++;
        monitor.Start();
    }

    // Retained for the shared MainWindow path. WPF owns the whole shortcut
    // press/release while Vision remains foreground, so no global reservation
    // is necessary.
    internal void ReserveLocalKey(Key key) => _ = key;

    internal Task<bool> PrepareForInputAsync() => Task.FromResult(CanRouteInput());

    internal bool ForwardFirstKey(Key key, bool isRepeat)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (disposed || !NormalGameInputPolicy.IsGameKey(virtualKey)) return false;
        if (!CanRouteInput())
        {
            status("Click the game view to resume keyboard controls.");
            return true;
        }

        if (isRepeat)
        {
            if (handoffKeys.TryGetValue(virtualKey, out var repeated)
                && CanUseTarget(repeated.Target))
            {
                _ = SendKey(repeated, down: true, repeat: true);
            }
            return true;
        }

        var game = target!;
        var identity = new NormalGameInputTarget(game.Handle, game.ProcessId, generation);
        if (!handoffState.Press(virtualKey, identity, isRepeat: false)) return true;
        var scan = MapVirtualKey((uint)virtualKey, 4); // MAPVK_VK_TO_VSC_EX
        if (scan == 0)
        {
            handoffState.Release(virtualKey);
            return false;
        }
        var held = new HandoffKey(
            virtualKey,
            (ushort)(scan & 0xFF),
            (scan & 0xFF00) == 0xE000 || key is Key.Left or Key.Right or Key.Up or Key.Down,
            identity);
        handoffKeys.Add(virtualKey, held);
        if (!SendKey(held, down: true, repeat: false))
        {
            RemoveHandoff(held);
            status("Keyboard input was blocked. Run Vision and Hollow Knight at the same privilege level.");
            return true;
        }
        held.Injected = true;
        return true;
    }

    internal bool ForwardKeyUp(Key key)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!NormalGameInputPolicy.IsGameKey(virtualKey)) return false;
        if (!handoffKeys.TryGetValue(virtualKey, out var held)) return true;
        if (held.Injected && TargetWindowExists(held.Target))
            _ = SendKey(held, down: false, repeat: false);
        RemoveHandoff(held);
        return true;
    }

    internal void UpdateContext()
    {
        if (disposed) return;
        if (!CanRouteInput()) RetireActiveHandoffs();
    }

    private bool CanRouteInput() => CanUseTarget()
        && allowsGameInput()
        && GetForegroundWindow() == ownerHandle;

    private bool CanUseTarget()
    {
        if (disposed || target is null || !owner.IsVisible || !owner.IsEnabled
            || owner.WindowState == WindowState.Minimized || !IsWindow(target.Handle)
            || IsIconic(target.Handle)) return false;
        _ = GetWindowThreadProcessId(target.Handle, out var processId);
        return processId == target.ProcessId;
    }

    private bool CanUseTarget(NormalGameInputTarget identity) =>
        identity.Generation == generation
        && target is { } game
        && game.Handle == identity.Handle
        && game.ProcessId == identity.ProcessId
        && CanUseTarget();

    private static bool SendKey(HandoffKey key, bool down, bool repeat)
    {
        var message = NormalGameInputPolicy.KeyMessage(
            key.VirtualKey, key.ScanCode, key.Extended, down, repeat);
        var currentThread = GetCurrentThreadId();
        var gameThread = GetWindowThreadProcessId(key.Target.Handle, out _);
        if (gameThread == 0) return false;

        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState)) return false;
        var restoredState = (byte[])keyboardState.Clone();
        keyboardState[key.VirtualKey] = down
            ? (byte)(keyboardState[key.VirtualKey] | 0x80)
            : (byte)(keyboardState[key.VirtualKey] & 0x7F);

        var attached = currentThread != gameThread
            && AttachThreadInput(currentThread, gameThread, attach: true);
        if (currentThread != gameThread && !attached) return false;
        try
        {
            if (!SetKeyboardState(keyboardState)) return false;
            return SendMessageTimeout(
                    key.Target.Handle,
                    message.Message,
                    message.WParam,
                    message.LParam,
                    SendMessageTimeoutFlags.Block | SendMessageTimeoutFlags.AbortIfHung,
                    50,
                    out _) != 0;
        }
        finally
        {
            _ = SetKeyboardState(restoredState);
            if (attached) _ = AttachThreadInput(currentThread, gameThread, attach: false);
        }
    }

    private bool RemoveHandoff(HandoffKey key)
    {
        if (!handoffKeys.TryGetValue(key.VirtualKey, out var current)
            || !ReferenceEquals(key, current)) return false;
        handoffKeys.Remove(key.VirtualKey);
        handoffState.Release(key.VirtualKey);
        return true;
    }

    private static bool TargetWindowExists(NormalGameInputTarget identity)
    {
        if (!IsWindow(identity.Handle)) return false;
        _ = GetWindowThreadProcessId(identity.Handle, out var processId);
        return processId == identity.ProcessId;
    }

    private void RetireActiveHandoffs()
    {
        foreach (var key in handoffKeys.Values.ToArray())
        {
            if (key.Injected && TargetWindowExists(key.Target))
                _ = SendKey(key, down: false, repeat: false);
        }
        handoffKeys.Clear();
        handoffState.ReleaseAll();
    }

    public void Dispose()
    {
        if (disposed) return;
        monitor.Stop();
        RetireActiveHandoffs();
        disposed = true;
        generation++;
    }

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        Block = 0x0001,
        AbortIfHung = 0x0002,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] keyState);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] keyState);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, int message, nint wParam,
        nint lParam, SendMessageTimeoutFlags flags, uint timeoutMilliseconds, out nint result);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
