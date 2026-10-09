using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HollowKnightVision.Windows.Host;

public static partial class HollowKnightGameLauncher
{
    private const string SteamAppId = "367520";
    private const string RelativeGamePath = @"steamapps\common\Hollow Knight\hollow_knight.exe";

    public static string ResolveExecutable(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full)) throw new FileNotFoundException("Hollow Knight executable not found.", full);
            return full;
        }

        foreach (var candidate in CandidateExecutables())
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException(
            "Hollow Knight executable not found. Pass --game-exe PATH.");
    }

    public static Process Start(string? explicitPath = null)
    {
        var executable = ResolveExecutable(explicitPath);
        var steam = ResolveSteamExecutable();
        if (steam is not null && IsSteamInstallation(executable))
        {
            return Process.Start(new ProcessStartInfo(steam)
            {
                Arguments = $"-applaunch {SteamAppId}",
                WorkingDirectory = Path.GetDirectoryName(steam)!,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Steam did not start Hollow Knight.");
        }
        return Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows did not start Hollow Knight.");
    }

    private static bool IsSteamInstallation(string executable) => executable.Contains(
        $"{Path.DirectorySeparatorChar}steamapps{Path.DirectorySeparatorChar}common"
            + $"{Path.DirectorySeparatorChar}Hollow Knight{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase);

    private static string? ResolveSteamExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steamExe = key?.GetValue("SteamExe") as string;
        var steamPath = key?.GetValue("SteamPath") as string;
        var candidates = new[]
        {
            steamExe?.Replace('/', Path.DirectorySeparatorChar),
            string.IsNullOrWhiteSpace(steamPath) ? null : Path.Combine(steamPath, "steam.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steam.exe")
        };
        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

    private static IEnumerable<string> CandidateExecutables()
    {
        yield return @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", RelativeGamePath);

        if (!OperatingSystem.IsWindows()) yield break;
        var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
            ?.GetValue("SteamPath") as string;
        if (string.IsNullOrWhiteSpace(steamPath)) yield break;
        yield return Path.Combine(steamPath, RelativeGamePath);

        var librariesPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(librariesPath)) yield break;
        foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(librariesPath)))
        {
            var root = match.Groups[1].Value.Replace("\\\\", "\\");
            if (!string.IsNullOrWhiteSpace(root)) yield return Path.Combine(root, RelativeGamePath);
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
