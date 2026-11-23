using System.Security.Cryptography;
using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed class BotDemoSaveProfileMismatchException()
    : IOException("The profile stencils and save-file versions do not agree.");

public sealed record BotDemoSaveBackup(string Directory, bool HadActiveSave);

/// <summary>Copies slot 4 and its sidecars before the game is allowed to clear it.</summary>
public sealed class BotDemoSaveArchive
{
    public const int Slot = 4;
    private const string ManifestName = "manifest.json";
    private readonly string saves;
    private readonly string archives;

    private sealed record Manifest(int SchemaVersion, int Slot, DateTimeOffset CreatedAt, string SourceDirectory,
        Dictionary<string, string> Files, bool HadActiveSave);

    public BotDemoSaveArchive(string savesDirectory, string archiveDirectory)
    {
        saves = Path.TrimEndingDirectorySeparator(Path.GetFullPath(savesDirectory));
        archives = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveDirectory));
        if (!OutsideSaves(Path.GetRelativePath(saves, archives)))
            throw new ArgumentException("Save backups must be stored outside the game save directory.", nameof(archiveDirectory));
    }

    private static bool OutsideSaves(string relative) => Path.IsPathRooted(relative)
        || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public bool HasActiveSave(int occupiedSlotMask)
    {
        var families = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(saves))
        {
            if (!string.Equals(Path.GetExtension(path), ".dat", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length < 5 || !name.StartsWith("user", StringComparison.OrdinalIgnoreCase)
                || name[4] is < '1' or > '4' || name.Length > 5 && name[5] != '_') continue;
            RequireRegularFile(path);
            var family = name[5..];
            families[family] = families.GetValueOrDefault(family) | (1 << (name[4] - '1'));
        }
        if (families.Count == 0 && occupiedSlotMask == 0) return false;
        if (!families.Values.Contains(occupiedSlotMask)) throw new BotDemoSaveProfileMismatchException();
        return (occupiedSlotMask & (1 << (Slot - 1))) != 0;
    }

    public BotDemoSaveBackup Create(int occupiedSlotMask)
    {
        var active = HasActiveSave(occupiedSlotMask);
        var files = SlotFiles();
        Directory.CreateDirectory(archives);
        var key = Guid.NewGuid().ToString("D");
        var staging = Path.Combine(archives, $".staging-slot-4-{key}");
        var destination = Path.Combine(archives, $"slot-4-{key}");
        Directory.CreateDirectory(staging);
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                var copy = Path.Combine(staging, name);
                using (var source = File.OpenRead(path))
                using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                hashes.Add(name, Hash(copy));
            }
            if (HasActiveSave(occupiedSlotMask) != active) throw Changed();
            VerifyFiles(staging, hashes);
            var manifest = new Manifest(1, Slot, DateTimeOffset.UtcNow, saves, hashes, active);
            using (var stream = new FileStream(Path.Combine(staging, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, manifest, ModelStoreJson.Options);
                stream.Flush(flushToDisk: true);
            }
            Directory.Move(staging, destination);
            return new(destination, active);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public void VerifyBeforeClear(BotDemoSaveBackup backup)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(backup.Directory)), archives, PathComparison)
            || !backup.HadActiveSave) throw Changed();
        using var stream = File.OpenRead(Path.Combine(backup.Directory, ManifestName));
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, ModelStoreJson.Options);
        if (manifest is not { SchemaVersion: 1, Slot: Slot, HadActiveSave: true }
            || !string.Equals(manifest.SourceDirectory, saves, PathComparison)
            || manifest.Files is null) throw Changed();
        VerifyFiles(backup.Directory, manifest.Files);
    }

    private void VerifyFiles(string directory, Dictionary<string, string> hashes)
    {
        var files = SlotFiles();
        if (!files.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(hashes.Keys))
            throw Changed();
        foreach (var source in files)
        {
            var name = Path.GetFileName(source);
            var copy = Path.Combine(directory, name);
            if (!hashes.TryGetValue(name, out var expected) || Hash(source) != expected || Hash(copy) != expected)
                throw Changed();
        }
    }

    private string[] SlotFiles() => Directory.EnumerateFileSystemEntries(saves)
        .Where(path => Path.GetFileName(path).StartsWith("user4.", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).StartsWith("user4_", StringComparison.OrdinalIgnoreCase))
        .Select(path => { RequireRegularFile(path); return path; })
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static void RequireRegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Slot save data contains a link or a nonregular file.");
    }

    private static string Hash(string path)
    {
        RequireRegularFile(path);
        using var source = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IOException Changed() => new("Slot 4 or its archive changed before Clear Save.");
}
