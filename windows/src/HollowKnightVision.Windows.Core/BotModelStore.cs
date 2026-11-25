using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record BotPlaybackModel(string Directory, BotNativePolicy Policy);
public sealed record BotCheckpointScore(string Label, double Value, double? Baseline)
{
    public string DisplayValue => Baseline is { } baseline
        ? $"{baseline * 100:F0}% → {Value * 100:F0}%" : $"{Value * 100:F0}%";
}

public sealed record BotCheckpointVersion(string Id, string Name, DateTimeOffset? CreatedAt)
{
    public string DisplayName => CreatedAt is { } created ? $"{Name} · {created.LocalDateTime:g}" : Name;
    public IReadOnlyList<BotCheckpointScore> Scores { get; init; } = [];
}

public sealed record BotCheckpointCatalog(IReadOnlyList<BotCheckpointVersion> Versions, string? ActiveId, string? PreviousId);

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
    private sealed record Result(bool NativeParity, string? ModelDirectory, string? BotDomain, DateTimeOffset CreatedAt,
        JsonElement Candidate, JsonElement Baseline, JsonElement CandidateSequential, JsonElement BaselineSequential);
    private sealed record Manifest(string? BotDomain, string? Name);
    private sealed record Candidate(string Directory, string? Name, Result TrainingResult);

    public BotPlaybackModel? LoadPlayback() => LoadPlayback(out _);

    internal BotPlaybackModel? LoadPlayback(out BotPlaybackModel? latest) => LoadPlayback(honorManualChoice: true, out latest);

    internal BotPlaybackModel? UseLatestVerified() => LoadPlayback(honorManualChoice: false, out _);

    private BotPlaybackModel? LoadPlayback(bool honorManualChoice, out BotPlaybackModel? latest)
    {
        lock (SelectionLock)
        {
            var selected = ReadSelection();
            var currentDirectory = FindDirectory(selected.Directory);
            latest = LatestVerified();
            if (latest is not null)
            {
                var name = Path.GetFileName(latest.Directory);
                if (honorManualChoice && SameName(name, selected.ManualChoiceThrough) && !SameName(name, selected.Directory)
                    && TryLoadSelected(currentDirectory) is { } manual)
                    return manual;

                SaveSelection(name, selected, manualChoiceThrough: null);
                return latest;
            }
            return TryLoadSelected(currentDirectory) ?? TryLoadSelected(FindDirectory(selected.PreviousDirectory));
        }
    }

    public BotCheckpointCatalog Catalog()
    {
        lock (SelectionLock)
        {
            var selection = ReadSelection();
            var versions = Candidates().Where(item => HasNativeExport(item.Directory)).Select(item =>
            {
                var id = Path.GetFileName(item.Directory);
                return new BotCheckpointVersion(id, item.Name ?? id, item.TrainingResult.CreatedAt)
                {
                    Scores = ReadScores(item.TrainingResult)
                };
            }).ToList();
            if (FindDirectory("v1") is { } baseVersion && HasNativeExport(baseVersion)
                && ReadManifestForDomain(baseVersion) is { } manifest && !versions.Any(item => SameName(item.Id, "v1")))
                versions.Add(new("v1", manifest.Name ?? "v1", null));
            return new(versions.AsReadOnly(), FindListed(selection.Directory), FindListed(selection.PreviousDirectory));

            string? FindListed(string? id) => versions.FirstOrDefault(item => SameName(item.Id, id))?.Id;
        }
    }

    public BotPlaybackModel Activate(string id)
    {
        lock (SelectionLock) return SelectVerified(id, ReadSelection());
    }

    public BotPlaybackModel? RestorePrevious()
    {
        lock (SelectionLock)
        {
            var selection = ReadSelection();
            return selection.PreviousDirectory is { } previous ? SelectVerified(previous, selection) : null;
        }
    }

    private BotPlaybackModel SelectVerified(string id, Selection selection)
    {
        if (!IsVersionName(id) || TryLoadSelected(FindDirectory(id)) is not { } target)
            throw new InvalidDataException("Choose a completed checkpoint with a verified export for this bot.");
        var latest = LatestVerified();
        var name = Path.GetFileName(target.Directory);
        var latestName = latest is null ? null : Path.GetFileName(latest.Directory);
        SaveSelection(name, selection, SameName(name, latestName) ? null : latestName);
        return target;
    }

    private void SaveSelection(string id, Selection selection, string? manualChoiceThrough)
    {
        var unchanged = SameName(id, selection.Directory);
        if (unchanged && SameName(manualChoiceThrough, selection.ManualChoiceThrough)) return;
        var previous = unchanged || TryLoadSelected(FindDirectory(selection.Directory)) is null
            ? selection.PreviousDirectory : selection.Directory;
        if (SameName(id, previous)) previous = null;
        AtomicFile.Write(SelectionPath, JsonSerializer.SerializeToUtf8Bytes(new Selection(id, previous, manualChoiceThrough), Json));
    }

    private BotPlaybackModel? LatestVerified()
    {
        foreach (var candidate in Candidates())
            if (TryLoad(candidate.Directory) is { } policy) return new(candidate.Directory, policy);
        return null;
    }

    private IEnumerable<Candidate> Candidates() => ListDirectories(Models)
        .Concat(ListDirectories(legacyModels).Where(directory => !Directory.Exists(Path.Combine(Models, Path.GetFileName(directory)))))
        .Select(ReadCandidate).OfType<Candidate>()
        .OrderByDescending(candidate => candidate.TrainingResult.CreatedAt)
        .ThenByDescending(candidate => Path.GetFileName(candidate.Directory), StringComparer.OrdinalIgnoreCase);

    private Candidate? ReadCandidate(string directory)
    {
        var name = Path.GetFileName(directory);
        if (!IsVersionName(name) || ReadManifestForDomain(directory) is not { } manifest) return null;
        var result = TryRead<Result>(Path.Combine(directory, "training-result.json"));
        if (result is null || !result.NativeParity || result.CreatedAt == default || !SameName(result.ModelDirectory, name)
            || result.BotDomain is not null && result.BotDomain != domainName)
            return null;
        return new(directory, manifest.Name, result);
    }

    private static IReadOnlyList<BotCheckpointScore> ReadScores(Result result)
    {
        var scores = new List<BotCheckpointScore>(3);
        Add("Action agreement", Accuracy(result.Candidate, "exactActionAccuracy"),
            Accuracy(result.Baseline, "exactActionAccuracy"));
        Add("Jump balanced accuracy", JumpAccuracy(result.Candidate), JumpAccuracy(result.Baseline));
        Add("Own-action agreement", Accuracy(result.CandidateSequential, "exactActionAccuracy"),
            Accuracy(result.BaselineSequential, "exactActionAccuracy"));
        return scores.AsReadOnly();

        void Add(string label, double? value, double? baseline)
        {
            if (value is { } measured) scores.Add(new(label, measured, baseline));
        }

        static double? JumpAccuracy(JsonElement score)
        {
            var heads = Field(score, "heads");
            if (heads.ValueKind != JsonValueKind.Array) return null;
            foreach (var head in heads.EnumerateArray())
            {
                var name = Field(head, "name");
                if (name.ValueKind == JsonValueKind.String && name.ValueEquals("jump"))
                    return Accuracy(head, "balancedAccuracy");
            }
            return null;
        }

        static double? Accuracy(JsonElement score, string name)
        {
            var value = Field(score, name);
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var measured)
                && measured is >= 0 and <= 1 ? measured : null;
        }

        static JsonElement Field(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field) ? field : default;
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

    private static bool HasNativeExport(string directory) =>
        File.Exists(Path.Combine(directory, "runtime-manifest.json"))
        && File.Exists(Path.Combine(directory, "runtime-policy.json"))
        && File.Exists(Path.Combine(directory, "model.zip"));

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
