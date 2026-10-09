using System.Runtime.InteropServices;

namespace HollowKnightVision.Windows.Host;

public static class WindowsDpiAwareness
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. This keeps client rectangles
    // and BitBlt coordinates in physical pixels on scaled displays.
    private static readonly nint PerMonitorAwareV2 = new(-4);

    public static bool TryEnablePerMonitorV2()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
}
