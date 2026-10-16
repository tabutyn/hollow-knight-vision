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
            "Hollow Knight executable not found. Pass --game-path PATH.");
    }

    public static void ValidateInstallation(string executable, bool requireModded)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var managed = Path.Combine(gameRoot, "hollow_knight_Data", "Managed");
        var gameAssembly = Path.Combine(managed, "Assembly-CSharp.dll");
        if (!File.Exists(gameAssembly))
        {
            throw new InvalidOperationException(
                "The selected path is not a complete Hollow Knight installation.");
        }
        var loader = Path.Combine(managed, "MMHOOK_Assembly-CSharp.dll");
        var receiver = Path.Combine(
            managed, "Mods", "HollowKnightVisionInputReceiver",
            "HollowKnightVisionInputReceiver.dll");
        if (!requireModded && (File.Exists(loader) || File.Exists(receiver)))
        {
            throw new InvalidOperationException(
                "Normal mode requires an unmodified Hollow Knight installation. " +
                "Choose a stock executable with --game-path.");
        }
        if (requireModded && (!File.Exists(loader) || !File.Exists(receiver)))
        {
            throw new InvalidOperationException(
                "Modded mode requires the matching Modding API loader and " +
                "HollowKnightVisionInputReceiver. Choose that executable with --game-path.");
        }
    }

    public static string? FindCompatibleExecutable(
        bool requireModded,
        string? explicitPath = null,
        IEnumerable<string>? additionalCandidates = null)
    {
        IEnumerable<string> candidates;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            candidates = File.Exists(full) ? [full] : [];
        }
        else
        {
            candidates = (additionalCandidates ?? []).Concat(CandidateExecutables());
        }
        foreach (var candidate in candidates
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ValidateInstallation(candidate, requireModded);
                return candidate;
            }
            catch (InvalidOperationException)
            {
                // This candidate belongs to the other mode or is incomplete.
            }
        }
        return null;
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
        yield return @"D:\Games\Hollow Knight\hollow_knight.exe";
        yield return @"D:\Modded\Hollow Knight\hollow_knight.exe";
        yield return @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", RelativeGamePath);

        if (!OperatingSystem.IsWindows()) yield break;
        var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
            ?.GetValue("SteamPath") as string;
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            yield return Path.Combine(steamPath, RelativeGamePath);
            var librariesPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(librariesPath))
            {
                foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(librariesPath)))
                {
                    var root = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(root))
                        yield return Path.Combine(root, RelativeGamePath);
                }
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            yield return Path.Combine(drive.RootDirectory.FullName,
                "Games", "Hollow Knight", "hollow_knight.exe");
            yield return Path.Combine(drive.RootDirectory.FullName,
                "Modded", "Hollow Knight", "hollow_knight.exe");
            yield return Path.Combine(drive.RootDirectory.FullName,
                "SteamLibrary", RelativeGamePath);
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
