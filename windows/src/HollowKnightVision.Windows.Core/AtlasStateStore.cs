using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record AtlasSavedState(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record AtlasAutoSaveSummary(
    int RegistrationCount,
    DateTimeOffset CreatedAt,
    long? TotalByteCount = null)
{
    public static readonly Guid ActiveId =
        Guid.Parse("a0705a5e-0000-4000-8000-000000000001");

    public Guid Id => ActiveId;
    public AtlasAutoSaveSummary WithTotalByteCount(long byteCount) => this with
    {
        TotalByteCount = byteCount
    };
}

public enum AtlasStateStoreError
{
    EmptyName,
    MissingState,
    ArchiveImportRollbackFailed
}

public sealed class AtlasStateStoreException(
    AtlasStateStoreError error,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public AtlasStateStoreError Error { get; } = error;
}

/// <summary>
/// Named immutable restore points for an auto-saved atlas world. State metadata
/// stays compatible with Swift JSONEncoder, including Apple's Date epoch.
/// </summary>
public sealed class AtlasStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Func<Guid> createId;
    private readonly Func<DateTimeOffset> now;

    public AtlasStateStore(
        string rootDirectory,
        Func<Guid>? createId = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        this.createId = createId ?? Guid.NewGuid;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string RootDirectory { get; }

    public IReadOnlyList<AtlasSavedState> List()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var states = new List<AtlasSavedState>();
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            var directoryName = Path.GetFileName(directory);
            if (!Guid.TryParse(directoryName, out _)) continue;
            try
            {
                var state = ReadState(Path.Combine(directory, "state.json"));
                if (!directoryName.Equals(
                        state.Id.ToString("D"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                states.Add(state);
            }
            catch (IOException)
            {
                // Ignore interrupted or externally damaged library entries.
            }
            catch (JsonException)
            {
                // Ignore interrupted or externally damaged library entries.
            }
            catch (AtlasStateStoreException)
            {
                // Ignore interrupted or externally damaged library entries.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore entries that are no longer readable by this process.
            }
        }
        return states.OrderByDescending(state => state.CreatedAt).ToArray();
    }

    public AtlasSavedState Save(string name, Action<string> copyActiveWorld)
    {
        ArgumentNullException.ThrowIfNull(copyActiveWorld);
        var trimmedName = ValidName(name);
        Directory.CreateDirectory(RootDirectory);
        var state = new AtlasSavedState(createId(), trimmedName, now());
        var stagingDirectory = Path.Combine(RootDirectory, $"staging-{UpperId(state.Id)}");
        var stateDirectory = StateDirectory(state.Id);
        EnsureDestinationAvailable(stagingDirectory, stateDirectory);
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var worldDirectory = Path.Combine(stagingDirectory, "world");
            copyActiveWorld(worldDirectory);
            if (!Directory.Exists(worldDirectory))
            {
                throw new AtlasStateStoreException(
                    AtlasStateStoreError.MissingState,
                    "Atlas copy did not create a world directory.");
            }
            WriteState(Path.Combine(stagingDirectory, "state.json"), state);
            Directory.Move(stagingDirectory, stateDirectory);
            return state;
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public string WorldPath(AtlasSavedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var stateDirectory = StateDirectory(state.Id);
        AtlasSavedState stored;
        try
        {
            stored = ReadState(Path.Combine(stateDirectory, "state.json"));
        }
        catch (Exception error) when (
            error is IOException or JsonException or AtlasStateStoreException)
        {
            throw MissingState(error);
        }
        if (stored.Id != state.Id) throw MissingState();
        var worldDirectory = Path.Combine(stateDirectory, "world");
        if (!Directory.Exists(worldDirectory)) throw MissingState();
        return worldDirectory;
    }

    public AtlasSavedState ImportArchive(string archiveDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        var archivePath = Path.GetFullPath(archiveDirectory);
        if (!Directory.Exists(archivePath)) throw MissingState();
        var trimmedName = ValidName(name);
        Directory.CreateDirectory(RootDirectory);
        var state = new AtlasSavedState(createId(), trimmedName, now());
        var stagingDirectory = Path.Combine(RootDirectory, $"staging-{UpperId(state.Id)}");
        var stateDirectory = StateDirectory(state.Id);
        var stagedWorldDirectory = Path.Combine(stagingDirectory, "world");
        EnsureDestinationAvailable(stagingDirectory, stateDirectory);
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            Directory.Move(archivePath, stagedWorldDirectory);
            WriteState(Path.Combine(stagingDirectory, "state.json"), state);
            Directory.Move(stagingDirectory, stateDirectory);
            return state;
        }
        catch (Exception originalError)
        {
            if (Directory.Exists(stagedWorldDirectory))
            {
                try
                {
                    Directory.Move(stagedWorldDirectory, archivePath);
                }
                catch (Exception rollbackError)
                {
                    throw new AtlasStateStoreException(
                        AtlasStateStoreError.ArchiveImportRollbackFailed,
                        "Archived atlas remains in the state-library staging directory.",
                        new AggregateException(originalError, rollbackError));
                }
            }
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Delete(AtlasSavedState state)
    {
        var worldDirectory = WorldPath(state);
        var stateDirectory = Directory.GetParent(worldDirectory)?.FullName
            ?? throw MissingState();
        Directory.Delete(stateDirectory, recursive: true);
    }

    public void DeleteActiveArchive(string archiveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        var archivePath = Path.GetFullPath(archiveDirectory);
        if (!Directory.Exists(archivePath)) throw MissingState();
        Directory.Delete(archivePath, recursive: true);
    }

    public void PurgeLegacyTrash()
    {
        var trashDirectory = Path.Combine(RootDirectory, "Trash");
        if (Directory.Exists(trashDirectory))
        {
            Directory.Delete(trashDirectory, recursive: true);
        }
    }

    public static long FileByteCount(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Directory.Exists(rootDirectory)) throw MissingState();
        return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
    }

    private string StateDirectory(Guid id) => Path.Combine(RootDirectory, UpperId(id));

    private static string ValidName(string name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new AtlasStateStoreException(
                AtlasStateStoreError.EmptyName,
                "Give the atlas state a name.");
        }
        return trimmed;
    }

    private static void EnsureDestinationAvailable(params string[] directories)
    {
        if (directories.Any(Directory.Exists))
        {
            throw new IOException("Atlas state destination already exists.");
        }
    }

    private static AtlasSavedState ReadState(string path)
    {
        var state = JsonSerializer.Deserialize<AtlasSavedState>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new JsonException("Atlas state metadata is empty.");
        if (state.Id == Guid.Empty || string.IsNullOrWhiteSpace(state.Name))
        {
            throw new AtlasStateStoreException(
                AtlasStateStoreError.MissingState,
                "Atlas state metadata is invalid.");
        }
        return state;
    }

    private static void WriteState(string path, AtlasSavedState state)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
        File.Move(temporaryPath, path);
    }

    private static string UpperId(Guid id) => id.ToString("D").ToUpperInvariant();

    private static AtlasStateStoreException MissingState(Exception? innerException = null) =>
        new(
            AtlasStateStoreError.MissingState,
            "That atlas state is no longer available.",
            innerException);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new AppleReferenceDateConverter());
        options.Converters.Add(new UpperGuidConverter());
        return options;
    }

    private sealed class UpperGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            Guid.Parse(reader.GetString() ?? throw new JsonException("Missing UUID."));

        public override void Write(
            Utf8JsonWriter writer,
            Guid value,
            JsonSerializerOptions options) => writer.WriteStringValue(UpperId(value));
    }

    private sealed class AppleReferenceDateConverter : JsonConverter<DateTimeOffset>
    {
        private static readonly DateTimeOffset ReferenceDate =
            new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return ReferenceDate.AddSeconds(reader.GetDouble());
            }
            if (reader.TokenType == JsonTokenType.String
                && DateTimeOffset.TryParse(reader.GetString(), out var value))
            {
                return value;
            }
            throw new JsonException("Invalid atlas state date.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue((value.ToUniversalTime() - ReferenceDate).TotalSeconds);
    }
}
