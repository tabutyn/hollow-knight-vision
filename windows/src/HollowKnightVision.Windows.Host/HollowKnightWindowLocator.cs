using System.Diagnostics;
using System.Globalization;
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

public sealed record GameWindowCandidate(
    string Title,
    int ClientWidth,
    int ClientHeight,
    bool IsMinimized = false);

/// <summary>
/// Selects the gameplay surface from already validated Hollow Knight windows.
/// Unity can expose a small helper window before the titled gameplay window.
/// </summary>
public static class GameWindowSelection
{
    public static int? BestIndex(IReadOnlyList<GameWindowCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return null;

        return Enumerable.Range(0, candidates.Count)
            .OrderBy(index => candidates[index].IsMinimized)
            .ThenByDescending(index => HasGameplayTitle(candidates[index].Title))
            .ThenByDescending(index => Area(candidates[index]))
            .ThenBy(index => index)
            .First();
    }

    private static bool HasGameplayTitle(string? title) => string.Equals(
        title?.Trim(),
        "Hollow Knight",
        StringComparison.OrdinalIgnoreCase);

    private static long Area(GameWindowCandidate candidate) =>
        Math.Max(0L, candidate.ClientWidth)
        * Math.Max(0L, candidate.ClientHeight);
}

/// <summary>
/// Portable identity and capture-eligibility rules for a Windows game window.
/// Windows has no application bundle identifier, so the executable process
/// name is the authoritative identity. Window titles are deliberately ignored:
/// a browser or Vision window can display arbitrary game-named content.
/// </summary>
public static class SourceWindowIdentity
{
    public static bool IsHollowKnightProcess(string? processName) => Normalize(processName) is
        "hollowknight" or "hollowknight64" or "hollowknight32";

    /// <summary>
    /// Focus, occlusion and desktop placement are intentionally absent. The
    /// Win32 equivalent of CG layer zero is an unowned, non-tool top-level
    /// window supplied by EnumWindows.
    /// </summary>
    public static bool IsCaptureWindow(
        string? processName,
        int processId,
        int currentProcessId,
        bool isStandardTopLevel,
        double width,
        double height) =>
        processId > 0
        && processId != currentProcessId
        && isStandardTopLevel
        && double.IsFinite(width)
        && double.IsFinite(height)
        && width >= 320
        && height >= 180
        && IsHollowKnightProcess(processName);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return new string(value.Normalize(NormalizationForm.FormKD)
            .Where(character => char.IsLetterOrDigit(character)
                && CharUnicodeInfo.GetUnicodeCategory(character) is not (
                    UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark))
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}

public static class HollowKnightWindowLocator
{
    private const int ShowRestore = 9;
    private const int ExtendedWindowStyle = -20;
    private const uint GetOwner = 4;
    private const long ToolWindowStyle = 0x00000080L;
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    public static HollowKnightWindow? FindBest()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new List<HollowKnightWindow>();
        _ = EnumWindows((window, _) =>
        {
            // WS_VISIBLE is a style bit, not a focus or occlusion test. Keep it
            // only to exclude deliberately hidden helper windows.
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

            if (!GetClientRect(window, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (!SourceWindowIdentity.IsCaptureWindow(
                    processName,
                    (int)processId,
                    Environment.ProcessId,
                    IsStandardTopLevel(window),
                    width,
                    height)) return true;
            var title = ReadTitle(window);
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

        var selectedIndex = GameWindowSelection.BestIndex(candidates
            .Select(candidate => new GameWindowCandidate(
                candidate.Title,
                candidate.ClientWidth,
                candidate.ClientHeight,
                candidate.IsMinimized))
            .ToArray());
        return selectedIndex is { } index ? candidates[index] : null;
    }

    public static bool IsHollowKnight(string? processName, string? title)
    {
        _ = title;
        return SourceWindowIdentity.IsHollowKnightProcess(processName);
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

    private static bool IsStandardTopLevel(nint window)
    {
        var extendedStyle = IntPtr.Size == 8
            ? GetWindowLongPtr64(window, ExtendedWindowStyle).ToInt64()
            : GetWindowLong32(window, ExtendedWindowStyle);
        return GetWindow(window, GetOwner) == nint.Zero
            && (extendedStyle & ToolWindowStyle) == 0;
    }

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
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint window, int index);

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
