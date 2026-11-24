using System.Runtime.InteropServices;

namespace HollowKnightVision.Windows.App;

internal sealed class WindowsKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private readonly Func<int, bool, bool> handleKey;
    private readonly LowLevelKeyboardProc callback;
    private nint handle;

    internal WindowsKeyboardHook(Func<int, bool, bool> handleKey)
    {
        this.handleKey = handleKey;
        callback = OnKey;
    }

    internal bool Install()
    {
        if (handle == 0) handle = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
        return handle != 0;
    }

    internal static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private nint OnKey(int code, nint message, nint data)
    {
        var kind = (int)message;
        // The first field of KBDLLHOOKSTRUCT is the virtual-key code.
        if (code >= 0 && kind is 0x0100 or 0x0101 or 0x0104 or 0x0105
            && handleKey(Marshal.ReadInt32(data), kind is 0x0100 or 0x0104))
        {
            return 1;
        }
        return CallNextHookEx(handle, code, message, data);
    }

    public void Dispose()
    {
        if (handle == 0) return;
        _ = UnhookWindowsHookEx(handle);
        handle = 0;
    }

    private delegate nint LowLevelKeyboardProc(int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
