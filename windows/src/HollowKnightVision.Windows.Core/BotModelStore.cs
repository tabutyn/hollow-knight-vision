using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record BotPlaybackModel(string Directory, BotNativePolicy Policy);

/// <summary>Selects a verified checkpoint independently for each observation domain.</summary>
public sealed class BotModelStore(string root, BotCaptureDomain domain)
{
    private static readonly object SelectionLock = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string legacyModels = Path.GetFullPath(Path.Combine(root, "models"));
    private readonly string domainName = domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay";
    private string Models => Path.Combine(legacyModels, domainName);
    private string SelectionPath => Path.Combine(Models, "active.json");

    private sealed record Selection(string Directory, string? PreviousDirectory, string? ManualChoiceThrough = null);
    private sealed record Result(bool NativeParity, string? ModelDirectory, string? BotDomain, DateTimeOffset CreatedAt);
    private sealed record Manifest(string? BotDomain);
    private sealed record Candidate(string Directory, DateTimeOffset CreatedAt);

    public BotPlaybackModel? LoadPlayback()
    {
        lock (SelectionLock)
        {
            var selected = ReadSelection();
            var currentDirectory = FindDirectory(selected.Directory);
            foreach (var candidate in Candidates())
            {
                var policy = TryLoad(candidate.Directory);
                if (policy is null) continue;
                var name = Path.GetFileName(candidate.Directory);
                if (SameName(name, selected.ManualChoiceThrough) && !SameName(name, selected.Directory)
                    && TryLoadSelected(currentDirectory) is { } manual)
                    return manual;

                if (!SameName(name, selected.Directory) || selected.ManualChoiceThrough is not null)
                {
                    var previous = SameName(name, selected.Directory) || TryLoadSelected(currentDirectory) is null
                        ? selected.PreviousDirectory : selected.Directory;
                    if (SameName(name, previous)) previous = null;
                    AtomicFile.Write(SelectionPath, JsonSerializer.SerializeToUtf8Bytes(new Selection(name, previous), Json));
                }
                return new(candidate.Directory, policy);
            }
            return TryLoadSelected(currentDirectory) ?? TryLoadSelected(FindDirectory(selected.PreviousDirectory));
        }
    }

    private IEnumerable<Candidate> Candidates() => ListDirectories(Models)
        .Concat(ListDirectories(legacyModels).Where(directory => !Directory.Exists(Path.Combine(Models, Path.GetFileName(directory)))))
        .Select(ReadCandidate).OfType<Candidate>()
        .OrderByDescending(candidate => candidate.CreatedAt)
        .ThenByDescending(candidate => Path.GetFileName(candidate.Directory), StringComparer.OrdinalIgnoreCase);

    private Candidate? ReadCandidate(string directory)
    {
        var name = Path.GetFileName(directory);
        if (!IsVersionName(name) || ReadManifestForDomain(directory) is null) return null;
        var result = TryRead<Result>(Path.Combine(directory, "training-result.json"));
        if (result is null || !result.NativeParity || result.CreatedAt == default || !SameName(result.ModelDirectory, name)
            || result.BotDomain is not null && result.BotDomain != domainName)
            return null;
        return new(directory, result.CreatedAt);
    }

    private BotPlaybackModel? TryLoadSelected(string? directory)
    {
        if (directory is null) return null;
        if (Path.GetFileName(directory) == "v1")
        {
            if (ReadManifestForDomain(directory) is null) return null;
        }
        else if (ReadCandidate(directory) is null) return null;
        return TryLoad(directory) is { } policy ? new(directory, policy) : null;
    }

    private Manifest? ReadManifestForDomain(string directory)
    {
        var manifest = TryRead<Manifest>(Path.Combine(directory, "manifest.json"));
        if (manifest is null) return null;
        if (manifest.BotDomain is { } explicitDomain) return explicitDomain == domainName ? manifest : null;
        if (string.Equals(Path.GetDirectoryName(directory), Models, StringComparison.OrdinalIgnoreCase)) return manifest;

        // Legacy versions predate the domain subdirectories and sometimes omit their domain.
        var name = Path.GetFileName(directory);
        var inferred = name == "v1" ? "gameplay" : name.StartsWith("bot-", StringComparison.Ordinal) ? "hacker" : null;
        return inferred == domainName ? manifest : null;
    }

    private Selection ReadSelection()
    {
        if (File.Exists(SelectionPath))
        {
            var selection = Read<Selection>(SelectionPath);
            if (selection is null || !IsVersionName(selection.Directory)
                || selection.PreviousDirectory is { } previous && !IsVersionName(previous)
                || selection.ManualChoiceThrough is { } through && !IsVersionName(through))
                throw new InvalidDataException("The bot checkpoint selection contains an invalid version name.");
            return selection;
        }
        var legacy = TryRead<Selection>(Path.Combine(legacyModels, "active.json"));
        if (domainName == "hacker" && legacy is not null && IsVersionName(legacy.Directory)
            && ReadManifestForDomain(Path.Combine(legacyModels, legacy.Directory)) is not null)
            return new(legacy.Directory, null);
        return new("v1", null);
    }

    private string? FindDirectory(string? name)
    {
        if (name is null) return null;
        var local = Path.Combine(Models, name);
        if (Directory.Exists(local)) return local;
        var legacy = Path.Combine(legacyModels, name);
        return Directory.Exists(legacy) ? legacy : null;
    }

    private static bool IsVersionName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".." && name.Length <= 128 && !name.EndsWith('.') && !name.EndsWith(' ')
        && !name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character));

    private static bool SameName(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static string[] ListDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.GetDirectories(directory) : [];

    private static BotNativePolicy? TryLoad(string directory)
    {
        try { return BotNativePolicy.Load(directory); }
        catch (Exception error) when (Unreadable(error)) { return null; }
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try { return File.Exists(path) ? Read<T>(path) : null; }
        catch (Exception error) when (Unreadable(error)) { return null; }
    }

    private static T? Read<T>(string path) where T : class
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 2 * 1024 * 1024) throw new InvalidDataException("Bot checkpoint metadata is too large.");
        return JsonSerializer.Deserialize<T>(file, Json);
    }

    private static bool Unreadable(Exception error) =>
        error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException;
}
