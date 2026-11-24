using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HollowKnightVision.Windows.Host;

namespace HollowKnightVision.Windows.App;

// Mirrors the macOS game-focused R shortcut. Every key still goes to its
// ordinary Win32 target, and only a fresh unmodified R can queue a reset.
internal sealed class WindowsFocusedRoomReset : IDisposable
{
    private const int VirtualKeyR = 0x52;
    private readonly Dispatcher dispatcher;
    private readonly Func<bool> canReset;
    private readonly Action reset;
    private readonly WindowsKeyboardHook hook;
    private bool rDown;
    private bool disposed;

    internal WindowsFocusedRoomReset(Dispatcher dispatcher, Func<bool> canReset, Action reset)
    {
        this.dispatcher = dispatcher;
        this.canReset = canReset;
        this.reset = reset;
        hook = new WindowsKeyboardHook(HandleKey);
    }

    internal bool Start()
    {
        rDown = WindowsKeyboardHook.IsKeyDown(VirtualKeyR);
        return hook.Install();
    }

    internal static bool IsManagedGameFocused(Window owner, int gameProcessId) =>
        owner.IsVisible && owner.IsEnabled && owner.WindowState != WindowState.Minimized
        && IsWindowEnabled(new WindowInteropHelper(owner).Handle)
        && HollowKnightWindowLocator.OwnsForegroundWindow(gameProcessId);

    private bool HandleKey(int virtualKey, bool down)
    {
        if (virtualKey == VirtualKeyR)
        {
            var modified = down && (WindowsKeyboardHook.IsKeyDown(0x10) // Shift
                || WindowsKeyboardHook.IsKeyDown(0x11) // Ctrl
                || WindowsKeyboardHook.IsKeyDown(0x12) // Alt
                || WindowsKeyboardHook.IsKeyDown(0x5B) || WindowsKeyboardHook.IsKeyDown(0x5C)); // Win
            ObserveR(down, modified);
        }
        return false;
    }

    internal void ObserveR(bool down, bool modified)
    {
        var freshPress = down && !rDown;
        rDown = down;
        if (disposed || !freshPress || modified || !canReset()) return;
        _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!disposed && canReset()) reset();
        }));
    }

    public void Dispose()
    {
        disposed = true;
        hook.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
}
