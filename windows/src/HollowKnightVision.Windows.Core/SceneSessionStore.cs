using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record StoredFrameObservation(
    int Id,
    Guid RoomId,
    Guid VisitId,
    double Timestamp,
    string ImagePath,
    AtlasPoint CameraPosition,
    double SolveWidth,
    IReadOnlyList<StencilRect> ExcludedRects,
    int PoseRevision);

public sealed record SessionManifest
{
    public const int CurrentSchemaVersion = 1;

    public SessionManifest(
        int schemaVersion = CurrentSchemaVersion,
        IReadOnlyList<StoredFrameObservation>? observations = null,
        int nextObservationId = 0)
        : this(
            schemaVersion,
            Guid.NewGuid(),
            observations ?? [],
            nextObservationId) { }

    [JsonConstructor]
    public SessionManifest(
        int schemaVersion,
        Guid sessionId,
        IReadOnlyList<StoredFrameObservation> observations,
        int nextObservationId)
    {
        SchemaVersion = schemaVersion;
        SessionId = sessionId;
        Observations = observations?.ToArray()
            ?? throw new JsonException("Scene session observations are missing.");
        NextObservationId = nextObservationId;
    }

    public int SchemaVersion { get; init; }
    public Guid SessionId { get; init; }
    public IReadOnlyList<StoredFrameObservation> Observations { get; init; }
    public int NextObservationId { get; init; }
}

public enum SceneSessionStoreError
{
    CorruptManifest,
    UnsupportedSchema,
    CorruptSnapshot,
    UnsupportedSnapshotSchema,
    UnsafeImagePath,
    MissingSource,
    CorruptSource,
    InvalidObservation,
    InvalidRevision,
    EvidenceRollbackFailed
}

public sealed class SceneSessionStoreException : Exception
{
    public SceneSessionStoreException(
        SceneSessionStoreError error,
        string message,
        int? schemaVersion = null,
        string? imagePath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        SchemaVersion = schemaVersion;
        ImagePath = imagePath;
    }

    public SceneSessionStoreError Error { get; }
    public int? SchemaVersion { get; }
    public string? ImagePath { get; }
}

/// <summary>
/// Atomic frame-evidence and opaque scene-snapshot persistence. Paths are
/// canonicalized across Windows reparse points before containment checks.
/// </summary>
public sealed class SceneSessionStore
{
    private const string ManifestFileName = "manifest.json";
    private const string FramesDirectoryName = "frames";
    private const string SceneFileName = "scene.snapshot";
    private const string SceneLockFileName = "scene.snapshot.lock";
    private const string ManifestLockFileName = "manifest.json.lock";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SceneSessionStore(string rootDirectory)
        : this(rootDirectory, readOnly: false) { }

    private SceneSessionStore(string rootDirectory, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var requestedRoot = Path.GetFullPath(rootDirectory);
        if (readOnly && !Directory.Exists(requestedRoot))
        {
            throw new DirectoryNotFoundException(
                $"Scene session root does not exist: {requestedRoot}");
        }

        if (readOnly)
        {
            RootDirectory = CanonicalizePath(requestedRoot);
            if (!File.Exists(ManifestPath))
            {
                throw new FileNotFoundException(
                    "Scene session manifest does not exist.",
                    ManifestPath);
            }
            Manifest = ReadManifest();
            return;
        }

        Directory.CreateDirectory(requestedRoot);
        RootDirectory = CanonicalizePath(requestedRoot);
        Directory.CreateDirectory(RootDirectory);

        using (AcquireManifestLock())
        {
            if (File.Exists(ManifestPath))
            {
                Manifest = ReadManifest();
            }
            else
            {
                Manifest = new SessionManifest();
                WriteManifest(Manifest);
            }
        }

        Directory.CreateDirectory(FramesDirectory);
        var scene = ReadSceneEnvelope();
        SceneRevision = scene?.Revision ?? 0;
        sceneData = scene?.Data;
    }

    /// <summary>
    /// Opens existing manifest and PNG evidence without locks, directory creation,
    /// migration, or any persistent writes.
    /// </summary>
    public static ReadOnlyEvidence OpenReadOnly(string rootDirectory) =>
        new(rootDirectory);

    public sealed class ReadOnlyEvidence : IDisposable
    {
        private SceneSessionStore? store;

        internal ReadOnlyEvidence(string rootDirectory)
        {
            store = new SceneSessionStore(rootDirectory, readOnly: true);
        }

        public string RootDirectory => Current.RootDirectory;
        public SessionManifest Manifest => Current.Manifest;

        public BgraFrame Source(StoredFrameObservation observation) =>
            Current.Source(observation);

        public void Dispose() => store = null;

        private SceneSessionStore Current => store
            ?? throw new ObjectDisposedException(nameof(ReadOnlyEvidence));
    }

    public string RootDirectory { get; }
    public SessionManifest Manifest { get; private set; }
    public int SceneRevision { get; private set; }
    private byte[]? sceneData;

    public StoredFrameObservation Append(
        BgraFrame frame,
        Guid roomId,
        Guid visitId,
        double timestamp,
        AtlasPoint cameraPosition,
        double solveWidth,
        IReadOnlyList<StencilRect> excludedRects,
        int poseRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(excludedRects);
        if (!double.IsFinite(timestamp)
            || !double.IsFinite(cameraPosition.X)
            || !double.IsFinite(cameraPosition.Y)
            || !double.IsFinite(solveWidth)
            || solveWidth <= 0
            || excludedRects.Any(rect => !rect.IsFinite)
            || poseRevision < 0)
        {
            throw Error(
                SceneSessionStoreError.InvalidObservation,
                "Scene observation contains invalid geometry or timing.");
        }

        using var manifestLock = AcquireManifestLock();
        var currentManifest = ReadManifest();
        var id = currentManifest.NextObservationId;
        var nextObservationId = checked(id + 1);
        var imagePath = string.Format(
            CultureInfo.InvariantCulture,
            "frames/{0:D8}.png",
            id);
        var observation = new StoredFrameObservation(
            id,
            roomId,
            visitId,
            timestamp,
            imagePath,
            cameraPosition,
            solveWidth,
            excludedRects.ToArray(),
            poseRevision);
        var nextManifest = new SessionManifest(
            currentManifest.SchemaVersion,
            currentManifest.SessionId,
            [.. currentManifest.Observations, observation],
            nextObservationId);
        var targetPath = EvidencePath(imagePath);
        DeleteUnreferencedEvidence(
            targetPath,
            "Could not recover unreferenced scene evidence before append.");
        PngWriter.Write(frame, targetPath);
        try
        {
            WriteManifest(nextManifest);
            Manifest = nextManifest;
        }
        catch (Exception persistenceError)
        {
            Manifest = currentManifest;
            DeleteUnreferencedEvidence(
                targetPath,
                "Could not roll back scene evidence after manifest append failed.",
                persistenceError);
            throw;
        }
        return observation;
    }

    public BgraFrame Source(StoredFrameObservation observation)
        => Source(observation, validateOpenedHandle: null);

    internal BgraFrame Source(
        StoredFrameObservation observation,
        Action<FileStream, string>? validateOpenedHandle)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var path = EvidencePath(observation.ImagePath);
        if (!File.Exists(path))
        {
            throw Error(
                SceneSessionStoreError.MissingSource,
                $"Stored frame source is missing: {observation.ImagePath}",
                imagePath: observation.ImagePath);
        }
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            validateOpenedHandle?.Invoke(stream, path);
            return BgraPngReader.Read(stream, observation.Id);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or OverflowException)
        {
            throw Error(
                SceneSessionStoreError.CorruptSource,
                $"Stored frame source is corrupt: {observation.ImagePath}",
                imagePath: observation.ImagePath,
                innerException: exception);
        }
    }

    /// <summary>Stores a revision and complete scene in one atomic envelope.</summary>
    public bool SaveScene(byte[] data, int expectedRevision, int newRevision)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var sceneLock = AcquireSceneLock();
        var current = ReadSceneEnvelope();
        var currentRevision = current?.Revision ?? 0;
        SceneRevision = currentRevision;
        sceneData = current?.Data;
        if (expectedRevision != currentRevision)
        {
            return false;
        }
        if (newRevision <= expectedRevision)
        {
            throw Error(
                SceneSessionStoreError.InvalidRevision,
                "Scene revision must advance.");
        }

        var envelope = new SceneSnapshotEnvelope(newRevision, data.ToArray());
        AtomicFile.Write(
            ScenePath,
            JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
        SceneRevision = newRevision;
        sceneData = data.ToArray();
        return true;
    }

    public byte[]? LoadScene()
    {
        var scene = ReadSceneEnvelope();
        SceneRevision = scene?.Revision ?? 0;
        sceneData = scene?.Data;
        return sceneData?.ToArray();
    }

    private string ManifestPath => Path.Combine(RootDirectory, ManifestFileName);
    private string FramesDirectory => Path.Combine(RootDirectory, FramesDirectoryName);
    private string ScenePath => Path.Combine(RootDirectory, SceneFileName);
    private string SceneLockPath => Path.Combine(RootDirectory, SceneLockFileName);
    private string ManifestLockPath => Path.Combine(RootDirectory, ManifestLockFileName);

    private FileStream AcquireSceneLock()
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    SceneLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < LockTimeout)
            {
                Thread.Sleep(2);
            }
        }
    }

    private FileStream AcquireManifestLock()
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    ManifestLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < LockTimeout)
            {
                Thread.Sleep(2);
            }
        }
    }

    private void WriteManifest(SessionManifest manifest) => AtomicFile.Write(
        ManifestPath,
        JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

    private SessionManifest ReadManifest()
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SessionManifest>(
                    File.ReadAllBytes(ManifestPath), JsonOptions)
                ?? throw new InvalidDataException("Scene session manifest is empty.");
            if (manifest.SchemaVersion != SessionManifest.CurrentSchemaVersion)
            {
                throw Error(
                    SceneSessionStoreError.UnsupportedSchema,
                    $"Unsupported scene-session schema {manifest.SchemaVersion}.",
                    schemaVersion: manifest.SchemaVersion);
            }
            if (manifest.SessionId == Guid.Empty
                || manifest.NextObservationId < 0)
            {
                throw Error(
                    SceneSessionStoreError.CorruptManifest,
                    "Scene session manifest header is invalid.");
            }
            var ids = new HashSet<int>();
            foreach (var observation in manifest.Observations)
            {
                if (observation is null
                    || observation.Id < 0
                    || !ids.Add(observation.Id))
                {
                    throw Error(
                        SceneSessionStoreError.CorruptManifest,
                        "Scene session manifest contains invalid observations.");
                }
                _ = EvidencePath(observation.ImagePath);
            }
            if (ids.Count > 0 && ids.Max() >= manifest.NextObservationId)
            {
                throw Error(
                    SceneSessionStoreError.CorruptManifest,
                    "Scene session manifest next observation ID is invalid.");
            }
            return manifest;
        }
        catch (SceneSessionStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException or NotSupportedException or OverflowException)
        {
            throw Error(
                SceneSessionStoreError.CorruptManifest,
                "Scene session manifest is malformed.",
                innerException: exception);
        }
    }

    private static void DeleteUnreferencedEvidence(
        string evidencePath,
        string message,
        Exception? persistenceError = null)
    {
        if (!File.Exists(evidencePath)) return;
        try
        {
            File.Delete(evidencePath);
            if (File.Exists(evidencePath))
            {
                throw new IOException("Evidence cleanup did not remove the file.");
            }
        }
        catch (Exception cleanupError) when (cleanupError is IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            var cause = persistenceError is null
                ? cleanupError
                : new AggregateException(persistenceError, cleanupError);
            throw Error(
                SceneSessionStoreError.EvidenceRollbackFailed,
                message,
                innerException: cause);
        }
    }

    private string EvidencePath(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)
            || Path.IsPathRooted(imagePath)
            || imagePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal))
        {
            throw UnsafePath(imagePath);
        }

        string candidate;
        try
        {
            candidate = CanonicalizePath(Path.Combine(RootDirectory, imagePath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException or NotSupportedException)
        {
            throw UnsafePath(imagePath, exception);
        }

        var relative = Path.GetRelativePath(RootDirectory, candidate);
        if (relative is "." or ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw UnsafePath(imagePath);
        }
        return candidate;
    }

    private static string CanonicalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("Path has no root.", nameof(path));
        var current = root;
        var relative = fullPath[root.Length..];
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (info is not null
                && (info.Attributes & FileAttributes.ReparsePoint) != 0
                && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }

        var canonical = Path.GetFullPath(current);
        return canonical.Length == root.Length
            ? canonical
            : Path.TrimEndingDirectorySeparator(canonical);
    }

    private SceneSnapshotEnvelope? ReadSceneEnvelope()
    {
        if (!File.Exists(ScenePath))
        {
            return null;
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<SceneSnapshotEnvelope>(
                    File.ReadAllBytes(ScenePath), JsonOptions)
                ?? throw new InvalidDataException("Scene snapshot envelope is empty.");
            if (envelope.SchemaVersion != SceneSnapshotEnvelope.CurrentSchemaVersion)
            {
                throw Error(
                    SceneSessionStoreError.UnsupportedSnapshotSchema,
                    $"Unsupported scene snapshot schema {envelope.SchemaVersion}.",
                    schemaVersion: envelope.SchemaVersion);
            }
            return envelope;
        }
        catch (SceneSessionStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException or NotSupportedException or OverflowException)
        {
            throw Error(
                SceneSessionStoreError.CorruptSnapshot,
                "Scene snapshot envelope is malformed.",
                innerException: exception);
        }
    }

    private static SceneSessionStoreException UnsafePath(
        string? imagePath,
        Exception? innerException = null) => Error(
            SceneSessionStoreError.UnsafeImagePath,
            $"Unsafe stored frame path: {imagePath}",
            imagePath: imagePath,
            innerException: innerException);

    private static SceneSessionStoreException Error(
        SceneSessionStoreError error,
        string message,
        int? schemaVersion = null,
        string? imagePath = null,
        Exception? innerException = null) =>
        new(error, message, schemaVersion, imagePath, innerException);

    private sealed record SceneSnapshotEnvelope
    {
        internal const int CurrentSchemaVersion = 1;

        public SceneSnapshotEnvelope(int revision, byte[] data)
            : this(CurrentSchemaVersion, revision, data) { }

        [JsonConstructor]
        public SceneSnapshotEnvelope(int schemaVersion, int revision, byte[] data)
        {
            SchemaVersion = schemaVersion;
            Revision = revision;
            Data = data ?? throw new JsonException("Scene snapshot data is missing.");
        }

        public int SchemaVersion { get; init; }
        public int Revision { get; init; }
        public byte[] Data { get; init; }
    }

}
