using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HollowKnightVision.Windows.Host;

public sealed record HollowKnightWindow(
    nint Handle,
    int ProcessId,
    string ProcessName,
    string Title,
    int ClientWidth,
    int ClientHeight,
    bool IsMinimized);

public static class HollowKnightWindowLocator
{
    private const int ShowRestore = 9;
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    public static HollowKnightWindow? FindBest()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new List<HollowKnightWindow>();
        _ = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return true;
            string processName;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            var title = ReadTitle(window);
            if (!IsHollowKnight(processName, title) || !GetClientRect(window, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return true;
            candidates.Add(new HollowKnightWindow(
                window,
                (int)processId,
                processName,
                title,
                width,
                height,
                IsIconic(window)));
            return true;
        }, nint.Zero);

        return candidates
            .OrderBy(candidate => candidate.IsMinimized)
            .ThenByDescending(candidate => (long)candidate.ClientWidth * candidate.ClientHeight)
            .FirstOrDefault();
    }

    public static bool IsHollowKnight(string processName, string title)
    {
        var process = Normalize(processName);
        var windowTitle = Normalize(title);
        return process is "hollowknight" or "hollowknight64" or "hollowknight32"
            || windowTitle == "hollowknight";
    }

    public static bool TryBringToForeground(HollowKnightWindow window)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (window.IsMinimized) _ = ShowWindowAsync(window.Handle, ShowRestore);
        if (GetForegroundWindow() == window.Handle) return true;
        _ = SetForegroundWindow(window.Handle);
        if (GetForegroundWindow() != window.Handle)
        {
            object? shell = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("WScript.Shell could not be created.");
                var activated = shellType.InvokeMember(
                    "AppActivate",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args: [window.ProcessId]);
                if (activated is false) return false;
                Thread.Sleep(100);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (shell is not null && Marshal.IsComObject(shell))
                {
                    _ = Marshal.FinalReleaseComObject(shell);
                }
            }
        }
        return GetForegroundWindow() == window.Handle;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string ReadTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return string.Empty;
        var text = new StringBuilder(length + 1);
        _ = GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
