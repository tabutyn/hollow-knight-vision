using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HollowKnightVision.Windows.Host;

namespace HollowKnightVision.Windows.App;

/// <summary>
/// Keeps stock Hollow Knight as the real foreground input target while Vision
/// remains a visible, clickable, non-activating overlay above it. A low-level
/// keyboard hook reserves only Vision shortcuts; all gameplay input travels
/// through Windows to Hollow Knight unchanged, including holds and chords.
/// </summary>
internal sealed class WindowsNormalGameInput : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTop = nint.Zero;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);

    private readonly Window owner;
    private readonly Func<bool> allowsGameInput;
    private readonly Action<Key> shortcut;
    private readonly Action<string> status;
    private readonly DispatcherTimer monitor;
    private readonly NormalGameShortcutState shortcutState = new();
    private readonly LowLevelKeyboardProc keyboardProc;
    private HollowKnightWindow? target;
    private nint ownerHandle;
    private nint keyboardHook;
    private bool overlayActive;
    private NativePoint? freePointer;
    private bool disposed;

    internal WindowsNormalGameInput(
        Window owner,
        Func<bool> allowsGameInput,
        Action<Key> shortcut,
        Action<string> status)
    {
        this.owner = owner;
        this.allowsGameInput = allowsGameInput;
        this.shortcut = shortcut;
        this.status = status;
        keyboardProc = KeyboardHook;
        // Cursor confinement must be released even while gameplay continuously
        // queues Render work. Input priority is lower than Render in WPF.
        monitor = new DispatcherTimer(DispatcherPriority.Normal, owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        monitor.Tick += (_, _) => MaintainOverlay();
    }

    internal void Start(HollowKnightWindow game)
    {
        if (disposed) return;
        target = game;
        ownerHandle = new WindowInteropHelper(owner).Handle;
        EnsureKeyboardHook();
        monitor.Start();
        UpdateContext();
    }

    /// <summary>
    /// WPF consumed this shortcut before switching into Gameplay. Reserve its
    /// eventual key-up so it cannot leak into the newly focused game.
    /// </summary>
    internal void ReserveLocalKey(Key key)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (NormalGameInputPolicy.IsVisionShortcut(virtualKey))
            shortcutState.Capture(virtualKey);
    }

    internal Task<bool> PrepareForInputAsync()
    {
        UpdateContext();
        return Task.FromResult(overlayActive && IsGameForeground());
    }

    // These remain on the shared WPF route for the brief transition before
    // Gameplay overlay mode is established. The first press is intentionally
    // consumed rather than posted as a fake background message; subsequent
    // native input goes straight to the foreground game.
    internal bool ForwardFirstKey(Key key, bool isRepeat)
    {
        _ = isRepeat;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!NormalGameInputPolicy.IsGameKey(virtualKey)) return false;
        if (!CanUseTarget() || !allowsGameInput()) return false;
        EnterOverlay(forceActivation: true);
        return true;
    }

    internal bool ForwardKeyUp(Key key) =>
        NormalGameInputPolicy.IsGameKey(KeyInterop.VirtualKeyFromKey(key))
        && allowsGameInput();

    /// <summary>
    /// Applies an explicit workspace/settings transition. Gameplay activates
    /// Hollow Knight behind the overlay; every other context returns focus to
    /// Vision so its editors retain their normal keyboard behavior.
    /// </summary>
    internal void UpdateContext()
    {
        if (disposed) return;
        if (CanUseTarget() && allowsGameInput())
        {
            var foreground = GetForegroundWindow();
            if (foreground == ownerHandle || foreground == target!.Handle || !overlayActive)
                EnterOverlay(forceActivation: true);
            return;
        }
        if (overlayActive) ExitOverlay(activateOwner: true);
    }

    private void MaintainOverlay()
    {
        if (disposed) return;
        if (!CanUseTarget())
        {
            if (overlayActive) ExitOverlay(activateOwner: false);
            return;
        }

        var foreground = GetForegroundWindow();
        if (!allowsGameInput())
        {
            if (overlayActive)
                ExitOverlay(activateOwner: foreground == target!.Handle || foreground == ownerHandle);
            return;
        }

        if (foreground != target!.Handle && foreground != ownerHandle)
        {
            // Do not cover unrelated applications when the user Alt-Tabs.
            if (overlayActive) ExitOverlay(activateOwner: false);
            return;
        }

        if (!overlayActive || foreground == ownerHandle)
            EnterOverlay(forceActivation: true);
        else
            ApplyOverlayStyle(enabled: true);
        ReleaseGameCursor();
    }

    private void EnterOverlay(bool forceActivation)
    {
        if (!CanUseTarget() || !allowsGameInput()) return;
        // Unity can confine the desktop cursor to its center when activated.
        // Remember the free position before that happens, not the clipped one.
        if (!IsGameCursorConfined() && GetCursorPos(out var pointer))
            freePointer = pointer;
        ApplyOverlayStyle(enabled: true);
        overlayActive = true;
        if (!forceActivation && IsGameForeground())
        {
            ReleaseGameCursor();
            return;
        }
        if (!ActivateWindowBehindOverlay(target!.Handle, ownerHandle))
        {
            status("Hollow Knight could not take keyboard focus. Click Hollow Knight once, then return to Vision.");
            return;
        }
        ApplyOverlayStyle(enabled: true);
        ReleaseGameCursor();
    }

    private bool IsGameCursorConfined()
    {
        if (target is null || !GetClipCursor(out var clip)
            || !GetWindowRect(target.Handle, out var game)
            || !GetWindowRect(ownerHandle, out var overlay)) return false;
        // Only release confinement inside our game's bounds that excludes some
        // of Vision. Never change another foreground application's cursor clip.
        return clip.Left >= game.Left && clip.Top >= game.Top
            && clip.Right <= game.Right && clip.Bottom <= game.Bottom
            && (clip.Left > overlay.Left || clip.Top > overlay.Top
                || clip.Right < overlay.Right || clip.Bottom < overlay.Bottom);
    }

    private void ReleaseGameCursor()
    {
        if (!overlayActive || !IsGameForeground()) return;
        if (IsGameCursorConfined())
        {
            if (!ClipCursor(0)) return;
            if (freePointer is { } pointer)
                _ = SetCursorPos(pointer.X, pointer.Y);
        }
        if (GetCursorPos(out var current)) freePointer = current;
    }

    private void ExitOverlay(bool activateOwner)
    {
        ReleaseGameCursor();
        overlayActive = false;
        freePointer = null;
        ApplyOverlayStyle(enabled: false);
        if (activateOwner && owner.IsVisible && owner.WindowState != WindowState.Minimized)
            _ = ActivateWindow(ownerHandle);
    }

    private void ApplyOverlayStyle(bool enabled)
    {
        if (ownerHandle == 0 || !IsWindow(ownerHandle)) return;
        // Keep WPF's managed z-order state in sync with the native HWND. If
        // Topmost remains false, WPF can restore the window to the normal band
        // after a render/layout transition even after SetWindowPos succeeded.
        if (owner.Topmost != enabled) owner.Topmost = enabled;
        var current = GetWindowLongPtr(ownerHandle, GwlExStyle).ToInt64();
        var desired = enabled ? current | WsExNoActivate : current & ~WsExNoActivate;
        if (desired != current)
        {
            SetLastError(0);
            var previous = SetWindowLongPtr(ownerHandle, GwlExStyle, new nint(desired));
            if (previous == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not update Vision overlay style.");
        }
        _ = SetWindowPos(
            ownerHandle,
            enabled ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    private bool CanUseTarget()
    {
        if (disposed || target is null || !owner.IsVisible || !owner.IsEnabled
            || owner.WindowState == WindowState.Minimized || !IsWindow(target.Handle)
            || IsIconic(target.Handle)) return false;
        _ = GetWindowThreadProcessId(target.Handle, out var processId);
        return processId == target.ProcessId;
    }

    private bool IsGameForeground() => target is { } game
        && GetForegroundWindow() == game.Handle;

    private void EnsureKeyboardHook()
    {
        if (keyboardHook != 0) return;
        keyboardHook = SetWindowsHookEx(
            WhKeyboardLl,
            keyboardProc,
            GetModuleHandle(null),
            0);
        if (keyboardHook == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not reserve Vision keyboard shortcuts.");
    }

    private nint KeyboardHook(int code, nint message, nint dataPointer)
    {
        if (code >= 0 && !disposed && overlayActive && CanUseTarget() && IsGameForeground())
        {
            var messageValue = unchecked((int)message);
            var down = messageValue is 0x0100 or 0x0104;
            var up = messageValue is 0x0101 or 0x0105;
            if (down || up)
            {
                var data = Marshal.PtrToStructure<LowLevelKeyboardData>(dataPointer);
                var virtualKey = unchecked((int)data.VirtualKey);
                var modified = IsModifierDown();
                if (shortcutState.Handle(
                        virtualKey,
                        down,
                        canIntercept: allowsGameInput(),
                        modified,
                        out var invoke))
                {
                    if (invoke)
                    {
                        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
                        owner.Dispatcher.BeginInvoke(
                            DispatcherPriority.Input,
                            new Action(() =>
                            {
                                if (!disposed) shortcut(key);
                            }));
                    }
                    return 1;
                }
            }
        }
        return CallNextHookEx(keyboardHook, code, message, dataPointer);
    }

    private static bool IsModifierDown() =>
        IsKeyDown(0x11) || IsKeyDown(0x12) || IsKeyDown(0x5B) || IsKeyDown(0x5C);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool ActivateWindowBehindOverlay(nint game, nint overlay)
    {
        _ = ShowWindow(game, SwRestore);
        _ = ShowWindow(overlay, SwRestore);
        _ = SetWindowPos(overlay, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        _ = SetWindowPos(game, HwndTop, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpShowWindow);

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var gameThread = GetWindowThreadProcessId(game, out _);
        var attachedForeground = currentThread != foregroundThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedGame = currentThread != gameThread
            && AttachThreadInput(currentThread, gameThread, true);
        try
        {
            _ = BringWindowToTop(game);
            _ = SetActiveWindow(game);
            _ = SetFocus(game);
            _ = SetForegroundWindow(game);
        }
        finally
        {
            if (attachedGame) _ = AttachThreadInput(currentThread, gameThread, false);
            if (attachedForeground) _ = AttachThreadInput(currentThread, foregroundThread, false);
        }
        _ = SetWindowPos(overlay, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        return GetForegroundWindow() == game;
    }

    private static bool ActivateWindow(nint window)
    {
        _ = ShowWindow(window, SwRestore);
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = currentThread != foregroundThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            _ = BringWindowToTop(window);
            _ = SetActiveWindow(window);
            _ = SetFocus(window);
            _ = SetForegroundWindow(window);
        }
        finally
        {
            if (attached) _ = AttachThreadInput(currentThread, foregroundThread, false);
        }
        return GetForegroundWindow() == window;
    }

    public void Dispose()
    {
        if (disposed) return;
        monitor.Stop();
        shortcutState.Clear();
        if (overlayActive) ExitOverlay(activateOwner: false);
        if (keyboardHook != 0)
        {
            _ = UnhookWindowsHookEx(keyboardHook);
            keyboardHook = 0;
        }
        target = null;
        disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardData
    {
        internal readonly uint VirtualKey;
        internal readonly uint ScanCode;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    private delegate nint LowLevelKeyboardProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(nint rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint error);
}
