using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public enum LiveWorldSessionStoreError
{
    CorruptSnapshot,
    UnsupportedSnapshotSchema,
    SnapshotRevisionMismatch,
    MissingSourceObservation,
    ArchiveRollbackFailed,
    SessionInvalidated,
    CorruptSessionManifest,
    EvidenceRollbackFailed
}

public sealed class LiveWorldSessionStoreException : Exception
{
    public LiveWorldSessionStoreException(
        LiveWorldSessionStoreError error,
        string message,
        int? snapshotRevision = null,
        int? diskRevision = null,
        int? observationId = null,
        int? schemaVersion = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        SnapshotRevision = snapshotRevision;
        DiskRevision = diskRevision;
        ObservationId = observationId;
        SchemaVersion = schemaVersion;
    }

    public LiveWorldSessionStoreError Error { get; }
    public int? SnapshotRevision { get; }
    public int? DiskRevision { get; }
    public int? ObservationId { get; }
    public int? SchemaVersion { get; }
}

public sealed record LiveWorldSessionState(
    LiveWorldSnapshot Snapshot,
    int SceneRevision);

public sealed record LiveWorldSessionArchive(
    string ArchiveDirectory,
    LiveWorldSessionStore FreshStore);

/// <summary>
/// Durable ownership of masked live captures and their validated pose graph.
/// SceneSessionStore remains the sole writer of PNG evidence and the locked,
/// atomically replaced scene envelope.
/// </summary>
public sealed class LiveWorldSessionStore
{
    private const string ManifestFileName = "manifest.json";
    private const string SceneFileName = "scene.snapshot";
    private const string FramesDirectoryName = "frames";
    private const string TemporaryMarker = ".tmp-";
    private const string DurableMarker = ".durable-";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions =
        LabelingExampleStore.CreateJsonOptions();
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonOptions)
    {
        PropertyNameCaseInsensitive = true
    };

    private SceneSessionStore sceneStore;
    private readonly Guid generationId;
    private readonly string lifecycleLockPath;
    private readonly Action<string>? beforeAppendManifestWrite;
    private volatile bool invalidated;

    public LiveWorldSessionStore(string rootDirectory)
        : this(
            PrepareStorePaths(rootDirectory),
            lifecycleLockAlreadyHeld: false,
            beforeAppendManifestWrite: null,
            afterManifestValidation: null) { }

    internal LiveWorldSessionStore(
        string rootDirectory,
        Action<string> beforeAppendManifestWrite)
        : this(
            PrepareStorePaths(rootDirectory),
            lifecycleLockAlreadyHeld: false,
            beforeAppendManifestWrite,
            afterManifestValidation: null)
    {
        ArgumentNullException.ThrowIfNull(beforeAppendManifestWrite);
    }

    internal LiveWorldSessionStore(
        string rootDirectory,
        Action afterManifestValidation)
        : this(
            PrepareStorePaths(rootDirectory),
            lifecycleLockAlreadyHeld: false,
            beforeAppendManifestWrite: null,
            afterManifestValidation)
    {
        ArgumentNullException.ThrowIfNull(afterManifestValidation);
    }

    private LiveWorldSessionStore(
        StorePaths paths,
        bool lifecycleLockAlreadyHeld,
        Action<string>? beforeAppendManifestWrite,
        Action? afterManifestValidation)
    {
        lifecycleLockPath = paths.LifecycleLockPath;
        this.beforeAppendManifestWrite = beforeAppendManifestWrite;
        IDisposable? lifecycleLock = null;
        if (!lifecycleLockAlreadyHeld)
        {
            lifecycleLock = AcquireLifecycleLock(lifecycleLockPath);
        }
        try
        {
            sceneStore = OpenValidatedSceneStore(
                paths.RootDirectory,
                afterManifestValidation);
            if (!string.Equals(
                    sceneStore.RootDirectory,
                    paths.RootDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Live-world root changed through a reparse point while it was being opened.");
            }
            RecoverOwnedStagingFiles(paths.RootDirectory, sceneStore.Manifest);
            RootDirectory = sceneStore.RootDirectory;
            generationId = sceneStore.Manifest.SessionId;
            _ = LoadCore(sceneStore);
        }
        finally
        {
            lifecycleLock?.Dispose();
        }
    }

    public string RootDirectory { get; }

    public StoredFrameObservation Append(
        BgraFrame maskedImage,
        double timestamp,
        AtlasPoint rawCameraPose,
        double solveWidth,
        IReadOnlyList<StencilRect>? excludedRects = null)
    {
        ArgumentNullException.ThrowIfNull(maskedImage);
        var exclusions = excludedRects?.ToArray() ?? [];
        ValidateAppendInput(timestamp, rawCameraPose, solveWidth, exclusions);

        using var lifecycleLock = AcquireLifecycleLock();
        var current = OpenCurrentStoreUnderLock();
        var nextObservationId = ValidateManifest(current.Manifest, RootDirectory);
        var imagePath = string.Format(
            CultureInfo.InvariantCulture,
            "frames/{0:D8}.png",
            nextObservationId);
        var evidencePath = ContainedEvidencePath(imagePath);
        DeleteUnreferencedEvidence(
            evidencePath,
            "Could not recover unreferenced live-world evidence before append.");

        try
        {
            DurablyWriteEvidence(maskedImage, evidencePath);
            var observation = new StoredFrameObservation(
                nextObservationId,
                current.Manifest.SessionId,
                current.Manifest.SessionId,
                timestamp,
                imagePath,
                rawCameraPose,
                solveWidth,
                exclusions,
                current.SceneRevision);
            var nextManifest = new SessionManifest(
                current.Manifest.SchemaVersion,
                current.Manifest.SessionId,
                [.. current.Manifest.Observations, observation],
                checked(nextObservationId + 1));
            beforeAppendManifestWrite?.Invoke(
                Path.Combine(RootDirectory, ManifestFileName));
            AtomicFile.Write(
                Path.Combine(RootDirectory, ManifestFileName),
                JsonSerializer.SerializeToUtf8Bytes(nextManifest, ManifestJsonOptions));
            return observation;
        }
        catch (Exception persistenceError)
        {
            DeleteUnreferencedEvidence(
                evidencePath,
                "Could not roll back live-world evidence after the manifest append failed.",
                persistenceError);
            throw;
        }
    }

    public BgraFrame Source(int observationId)
    {
        using var lifecycleLock = AcquireLifecycleLock();
        var current = OpenCurrentStoreUnderLock();
        var observation = current.Manifest.Observations.FirstOrDefault(
            value => value.Id == observationId);
        if (observation is null)
        {
            throw Error(
                LiveWorldSessionStoreError.MissingSourceObservation,
                $"Stored source observation {observationId} does not exist.",
                observationId: observationId);
        }
        var expectedPath = ContainedEvidencePath(observation.ImagePath);
        return current.Source(
            observation,
            (handle, _) => ValidateOpenedHandlePath(handle, expectedPath));
    }

    public LiveWorldSessionState Load()
    {
        using var lifecycleLock = AcquireLifecycleLock();
        var current = OpenCurrentStoreUnderLock();
        return LoadCore(current);
    }

    /// <summary>
    /// Validates and loads an already-published session without taking a lock
    /// or creating, recovering, cleaning, or otherwise modifying any file.
    /// This is intended for immutable restore-point and transaction-generation
    /// validation, where an incomplete directory must remain available as
    /// failure evidence rather than being repaired in place.
    /// </summary>
    public static LiveWorldSessionState ValidateExisting(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var requestedRoot = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(requestedRoot))
            throw new DirectoryNotFoundException("The live-world directory is missing.");
        var root = CanonicalizePath(requestedRoot);
        RejectExistingPublicationStaging(root);

        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw CorruptManifest("Live-world manifest is missing.");

        SessionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SessionManifest>(
                    ReadExistingFile(manifestPath),
                    ManifestJsonOptions)
                ?? throw new JsonException("Live-world manifest is empty.");
            ValidateManifest(manifest, root);
        }
        catch (LiveWorldSessionStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or NotSupportedException or OverflowException)
        {
            throw CorruptManifest("Live-world manifest is malformed.", exception);
        }

        var scenePath = Path.Combine(root, SceneFileName);
        if (!File.Exists(scenePath))
        {
            if (manifest.Observations.Count != 0)
            {
                throw Error(
                    LiveWorldSessionStoreError.CorruptSnapshot,
                    "Live-world evidence exists without a committed scene snapshot.");
            }
            return new LiveWorldSessionState(new LiveWorldSnapshot(), 0);
        }

        ExistingSceneSnapshotEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ExistingSceneSnapshotEnvelope>(
                    ReadExistingFile(scenePath),
                    JsonOptions)
                ?? throw new JsonException("Scene snapshot envelope is empty.");
            if (envelope.SchemaVersion != ExistingSceneSnapshotEnvelope.CurrentSchemaVersion
                || envelope.Revision < 0)
            {
                throw Error(
                    LiveWorldSessionStoreError.UnsupportedSnapshotSchema,
                    $"Unsupported scene snapshot schema {envelope.SchemaVersion}.",
                    schemaVersion: envelope.SchemaVersion);
            }
        }
        catch (LiveWorldSessionStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException or NotSupportedException or OverflowException)
        {
            throw Error(
                LiveWorldSessionStoreError.CorruptSnapshot,
                "Scene snapshot envelope is corrupt.",
                innerException: exception);
        }

        var snapshot = DeserializeSnapshot(envelope.Data);
        if (snapshot.MapRevision != envelope.Revision)
        {
            throw Error(
                LiveWorldSessionStoreError.SnapshotRevisionMismatch,
                "Live-world snapshot and scene envelope revisions disagree.",
                snapshotRevision: snapshot.MapRevision,
                diskRevision: envelope.Revision);
        }

        using var sourceHandles = ValidateExistingEvidence(snapshot, manifest, root);
        return new LiveWorldSessionState(snapshot, envelope.Revision);
    }

    private LiveWorldSessionState LoadCore(SceneSessionStore current)
    {
        var data = TranslateSnapshotStoreErrors(current.LoadScene);
        if (data is null)
        {
            return new LiveWorldSessionState(
                new LiveWorldSnapshot(),
                current.SceneRevision);
        }

        var snapshot = DeserializeSnapshot(data);

        if (snapshot.MapRevision != current.SceneRevision)
        {
            throw Error(
                LiveWorldSessionStoreError.SnapshotRevisionMismatch,
                "Live-world snapshot and scene envelope revisions disagree.",
                snapshotRevision: snapshot.MapRevision,
                diskRevision: current.SceneRevision);
        }

        using var sourceHandles = ValidateSourceReferences(snapshot, current);
        sceneStore = current;
        return new LiveWorldSessionState(snapshot, current.SceneRevision);
    }

    public bool Commit(
        LiveWorldSnapshot snapshot,
        int expectedRevision,
        int newRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (snapshot.MapRevision != newRevision)
        {
            throw Error(
                LiveWorldSessionStoreError.SnapshotRevisionMismatch,
                "Live-world snapshot revision does not match the requested revision.",
                snapshotRevision: snapshot.MapRevision,
                diskRevision: newRevision);
        }

        using var lifecycleLock = AcquireLifecycleLock();
        var current = OpenCurrentStoreUnderLock();
        using var sourceHandles = ValidateSourceReferences(snapshot, current);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var committed = TranslateSnapshotStoreErrors(
            () => current.SaveScene(encoded, expectedRevision, newRevision));
        sceneStore = current;
        return committed;
    }

    public LiveWorldSessionArchive ArchiveAndReset(
        DateTimeOffset? now = null,
        Action<string>? beforeFreshStoreCreation = null)
    {
        using var lifecycleLock = AcquireLifecycleLock();
        _ = OpenCurrentStoreUnderLock();
        var parent = Directory.GetParent(RootDirectory)?.FullName
            ?? throw new IOException("Live-world root needs a parent directory.");
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var milliseconds = checked((long)Math.Round(
            (timestamp - DateTimeOffset.UnixEpoch).TotalMilliseconds,
            MidpointRounding.AwayFromZero));
        var archiveDirectory = Path.Combine(
            parent,
            $"{Path.GetFileName(RootDirectory)}.archive-{milliseconds}-{Guid.NewGuid():D}");

        Directory.Move(RootDirectory, archiveDirectory);
        try
        {
            beforeFreshStoreCreation?.Invoke(RootDirectory);
            var freshStore = new LiveWorldSessionStore(
                new StorePaths(RootDirectory, lifecycleLockPath),
                lifecycleLockAlreadyHeld: true,
                beforeAppendManifestWrite: null,
                afterManifestValidation: null);
            if (!string.Equals(
                    freshStore.RootDirectory,
                    RootDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || freshStore.generationId == generationId
                || freshStore.invalidated)
            {
                throw new InvalidOperationException(
                    "Fresh live-world store does not own the reset root generation.");
            }
            invalidated = true;
            return new LiveWorldSessionArchive(archiveDirectory, freshStore);
        }
        catch (Exception initializationError)
        {
            if (Directory.Exists(RootDirectory))
            {
                try
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
                catch (Exception rollbackError)
                {
                    throw Error(
                        LiveWorldSessionStoreError.ArchiveRollbackFailed,
                        $"Could not remove partial live-world root; original archive remains at '{archiveDirectory}'.",
                        innerException: new AggregateException(initializationError, rollbackError));
                }
            }

            try
            {
                Directory.Move(archiveDirectory, RootDirectory);
            }
            catch (Exception rollbackError)
            {
                throw Error(
                    LiveWorldSessionStoreError.ArchiveRollbackFailed,
                    $"Could not restore live-world archive '{archiveDirectory}'.",
                    innerException: new AggregateException(initializationError, rollbackError));
            }
            throw;
        }
    }

    private DisposableHandles ValidateSourceReferences(
        LiveWorldSnapshot snapshot,
        SceneSessionStore current)
    {
        var handles = new DisposableHandles();
        var openedIds = new HashSet<int>();
        var storedById = current.Manifest.Observations.ToDictionary(
            value => value.Id);
        try
        {
            foreach (var observation in snapshot.Observations)
            {
                if (!storedById.TryGetValue(observation.SourceObservationId, out var stored)
                    || stored.Timestamp != observation.Timestamp
                    || stored.CameraPosition != observation.RawPose
                    || stored.SolveWidth != observation.SolveWidth
                    || !SameRectangles(stored.ExcludedRects, observation.ExcludedRects)
                    || !stored.ImagePath.StartsWith("frames/", StringComparison.Ordinal))
                {
                    throw MissingSource(observation.SourceObservationId);
                }

                if (!openedIds.Add(stored.Id)) continue;
                try
                {
                    var sourcePath = ContainedEvidencePath(stored.ImagePath);
                    var handle = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete);
                    try
                    {
                        ValidateOpenedHandlePath(handle, sourcePath);
                        handles.Add(handle);
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException
                    or IOException or NotSupportedException or UnauthorizedAccessException)
                {
                    throw MissingSource(observation.SourceObservationId, exception);
                }
            }
            return handles;
        }
        catch
        {
            handles.Dispose();
            throw;
        }
    }

    private static LiveWorldSnapshot DeserializeSnapshot(byte[] data)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<LiveWorldSnapshot>(data, JsonOptions)
                ?? throw new JsonException("Live-world snapshot is empty.");
            snapshot.Validate();
            return snapshot;
        }
        catch (LiveWorldModelException exception)
            when (exception.Error == LiveWorldModelError.UnsupportedSchema)
        {
            throw Error(
                LiveWorldSessionStoreError.UnsupportedSnapshotSchema,
                $"Unsupported live-world snapshot schema {exception.RelatedId}.",
                schemaVersion: exception.RelatedId,
                innerException: exception);
        }
        catch (Exception exception) when (exception is JsonException
            or NotSupportedException or LiveWorldModelException)
        {
            throw Error(
                LiveWorldSessionStoreError.CorruptSnapshot,
                "Live-world snapshot is corrupt.",
                innerException: exception);
        }
    }

    private static DisposableHandles ValidateExistingEvidence(
        LiveWorldSnapshot snapshot,
        SessionManifest manifest,
        string rootDirectory)
    {
        var handles = new DisposableHandles();
        var storedById = manifest.Observations.ToDictionary(value => value.Id);
        try
        {
            foreach (var stored in manifest.Observations)
            {
                var sourcePath = ContainedEvidencePath(rootDirectory, stored.ImagePath);
                FileStream? handle = null;
                try
                {
                    handle = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete);
                    ValidateOpenedHandlePath(handle, sourcePath);
                    if (handle.Length > int.MaxValue)
                        throw new InvalidDataException("PNG source is too large.");
                    var encoded = new byte[checked((int)handle.Length)];
                    handle.ReadExactly(encoded);
                    _ = GroundLabelPngDecoder.Decode(encoded, DateTimeOffset.UnixEpoch);
                    handle.Position = 0;
                    handles.Add(handle);
                    handle = null;
                }
                catch (Exception exception) when (exception is ArgumentException
                    or IOException or InvalidDataException or NotSupportedException
                    or UnauthorizedAccessException or OverflowException)
                {
                    throw MissingSource(stored.Id, exception);
                }
                finally
                {
                    handle?.Dispose();
                }
            }

            foreach (var observation in snapshot.Observations)
            {
                if (!storedById.TryGetValue(observation.SourceObservationId, out var stored)
                    || stored.Timestamp != observation.Timestamp
                    || stored.CameraPosition != observation.RawPose
                    || stored.SolveWidth != observation.SolveWidth
                    || !SameRectangles(stored.ExcludedRects, observation.ExcludedRects)
                    || !stored.ImagePath.StartsWith("frames/", StringComparison.Ordinal))
                {
                    throw MissingSource(observation.SourceObservationId);
                }
            }
            return handles;
        }
        catch
        {
            handles.Dispose();
            throw;
        }
    }

    private static byte[] ReadExistingFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        ValidateOpenedHandlePath(stream, path);
        if (stream.Length > int.MaxValue)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is too large.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return data;
    }

    private static void RejectExistingPublicationStaging(string rootDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(
                     rootDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (IsAtomicStagingName(name, ManifestFileName)
                || IsAtomicStagingName(name, SceneFileName))
            {
                throw CorruptManifest(
                    $"Live-world publication staging '{name}' is incomplete.");
            }
        }

        var framesDirectory = Path.Combine(rootDirectory, FramesDirectoryName);
        if (!Directory.Exists(framesDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(
                     framesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (TryParseDurableStagingName(name, out _)
                || TryStripGuidSuffix(name, TemporaryMarker, out var baseName)
                && (IsEvidenceFileName(baseName)
                    || TryParseDurableStagingName(baseName, out _)))
            {
                throw CorruptManifest(
                    $"Live-world evidence staging '{name}' is incomplete.");
            }
        }
    }

    private SceneSessionStore OpenCurrentStoreUnderLock()
    {
        if (invalidated
            || !Directory.Exists(RootDirectory)
            || !File.Exists(Path.Combine(RootDirectory, ManifestFileName)))
        {
            invalidated = true;
            throw Invalidated();
        }

        var current = OpenValidatedSceneStore(RootDirectory);
        if (!string.Equals(
                current.RootDirectory,
                RootDirectory,
                StringComparison.OrdinalIgnoreCase)
            || current.Manifest.SessionId != generationId)
        {
            invalidated = true;
            throw Invalidated();
        }
        RecoverOwnedStagingFiles(RootDirectory, current.Manifest);
        sceneStore = current;
        return current;
    }

    private IDisposable AcquireLifecycleLock() =>
        AcquireLifecycleLock(lifecycleLockPath);

    private static FileStream AcquireLifecycleLock(string lockPath)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
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

    private static int ValidateManifest(SessionManifest manifest, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != SessionManifest.CurrentSchemaVersion
            || manifest.SessionId == Guid.Empty
            || manifest.NextObservationId < 0
            || manifest.NextObservationId == int.MaxValue)
        {
            throw CorruptManifest("Live-world manifest header is invalid.");
        }

        var ids = new HashSet<int>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in manifest.Observations)
        {
            if (observation is null)
            {
                throw CorruptManifest("Live-world manifest contains an empty evidence entry.");
            }
            var expectedPath = string.Format(
                CultureInfo.InvariantCulture,
                "frames/{0:D8}.png",
                observation.Id);
            if (observation.Id < 0
                || !ids.Add(observation.Id)
                || string.IsNullOrWhiteSpace(observation.ImagePath)
                || !string.Equals(
                    observation.ImagePath,
                    expectedPath,
                    StringComparison.Ordinal)
                || !paths.Add(observation.ImagePath)
                || observation.RoomId != manifest.SessionId
                || observation.VisitId != manifest.SessionId
                || !double.IsFinite(observation.Timestamp)
                || !double.IsFinite(observation.CameraPosition.X)
                || !double.IsFinite(observation.CameraPosition.Y)
                || !double.IsFinite(observation.SolveWidth)
                || observation.SolveWidth <= 0
                || observation.ExcludedRects is null
                || observation.ExcludedRects.Any(rect => !rect.IsFinite)
                || observation.PoseRevision < 0)
            {
                throw CorruptManifest("Live-world manifest has duplicate or invalid evidence entries.");
            }
            try
            {
                _ = ContainedEvidencePath(rootDirectory, observation.ImagePath);
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException or NotSupportedException)
            {
                throw CorruptManifest(
                    "Live-world manifest contains an unsafe evidence path.",
                    exception);
            }
        }

        if (ids.Count > 0 && ids.Max() >= manifest.NextObservationId)
        {
            throw CorruptManifest(
                "Live-world manifest next observation ID does not advance existing evidence.");
        }
        _ = checked(manifest.NextObservationId + 1);
        return manifest.NextObservationId;
    }

    private static void ValidateAppendInput(
        double timestamp,
        AtlasPoint rawCameraPose,
        double solveWidth,
        IReadOnlyList<StencilRect> excludedRects)
    {
        if (!double.IsFinite(timestamp)
            || !double.IsFinite(rawCameraPose.X)
            || !double.IsFinite(rawCameraPose.Y)
            || !double.IsFinite(solveWidth)
            || solveWidth <= 0
            || excludedRects.Any(rect => !rect.IsFinite))
        {
            throw new SceneSessionStoreException(
                SceneSessionStoreError.InvalidObservation,
                "Scene observation contains invalid geometry or timing.");
        }
    }

    private static void DurablyWriteEvidence(BgraFrame frame, string evidencePath)
    {
        var stagingPath = evidencePath + $".durable-{Guid.NewGuid():N}";
        try
        {
            PngWriter.Write(frame, stagingPath);
            using var stream = new FileStream(
                stagingPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete);
            ValidateOpenedHandlePath(stream, stagingPath);
            stream.Flush(flushToDisk: true);
            PublishDurableFile(stagingPath, evidencePath);
            ValidateOpenedHandlePath(stream, evidencePath);
        }
        catch
        {
            try { File.Delete(stagingPath); } catch (IOException) { }
            throw;
        }
    }

    private static SceneSessionStore OpenValidatedSceneStore(
        string rootDirectory,
        Action? afterManifestValidation = null)
    {
        ValidateStoredManifest(rootDirectory);
        afterManifestValidation?.Invoke();
        SceneSessionStore store;
        try
        {
            store = new SceneSessionStore(rootDirectory);
        }
        catch (SceneSessionStoreException exception)
            when (exception.Error is SceneSessionStoreError.CorruptManifest
                or SceneSessionStoreError.UnsupportedSchema
                or SceneSessionStoreError.UnsafeImagePath)
        {
            throw CorruptManifest("Live-world manifest is invalid.", exception);
        }
        catch (SceneSessionStoreException exception)
            when (exception.Error == SceneSessionStoreError.CorruptSnapshot)
        {
            throw Error(
                LiveWorldSessionStoreError.CorruptSnapshot,
                "Scene snapshot envelope is corrupt.",
                innerException: exception);
        }
        catch (SceneSessionStoreException exception)
            when (exception.Error == SceneSessionStoreError.UnsupportedSnapshotSchema)
        {
            throw Error(
                LiveWorldSessionStoreError.UnsupportedSnapshotSchema,
                $"Unsupported scene snapshot schema {exception.SchemaVersion}.",
                schemaVersion: exception.SchemaVersion,
                innerException: exception);
        }
        ValidateManifest(store.Manifest, store.RootDirectory);
        return store;
    }

    private static void PublishDurableFile(string stagingPath, string destinationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(stagingPath, destinationPath, overwrite: false);
            return;
        }

        if (!MoveFileEx(
                stagingPath,
                destinationPath,
                MoveFileFlags.WriteThrough))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            throw new IOException(
                $"Could not durably publish '{Path.GetFileName(destinationPath)}'.",
                error);
        }
    }

    private static void RecoverOwnedStagingFiles(
        string rootDirectory,
        SessionManifest manifest)
    {
        foreach (var path in Directory.EnumerateFiles(
                     rootDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (IsAtomicStagingName(name, ManifestFileName)
                || IsAtomicStagingName(name, SceneFileName))
            {
                DeleteOwnedStaging(path);
            }
        }

        var framesDirectory = Path.Combine(rootDirectory, FramesDirectoryName);
        if (!Directory.Exists(framesDirectory)) return;
        var canonicalFrames = CanonicalizePath(framesDirectory);
        var framesRelative = Path.GetRelativePath(rootDirectory, canonicalFrames);
        if (framesRelative is "." or ".."
            || framesRelative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || framesRelative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal)
            || Path.IsPathRooted(framesRelative))
        {
            throw CorruptManifest(
                "Live-world frames directory escapes the session root.");
        }

        var referenced = manifest.Observations
            .Select(observation => Path.GetFileName(observation.ImagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var durableByEvidence = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     canonicalFrames,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (TryStripGuidSuffix(name, TemporaryMarker, out var temporaryBase)
                && (IsEvidenceFileName(temporaryBase)
                    || TryParseDurableStagingName(temporaryBase, out _)))
            {
                DeleteOwnedStaging(path);
                continue;
            }
            if (!TryParseDurableStagingName(name, out var evidenceName)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                DeleteOwnedStaging(path);
                continue;
            }
            if (!durableByEvidence.TryGetValue(evidenceName, out var paths))
            {
                paths = [];
                durableByEvidence.Add(evidenceName, paths);
            }
            paths.Add(path);
        }

        foreach (var (evidenceName, stagingPaths) in durableByEvidence)
        {
            var relativePath = $"{FramesDirectoryName}/{evidenceName}";
            var evidencePath = ContainedEvidencePath(rootDirectory, relativePath);
            if (File.Exists(evidencePath) || !referenced.Contains(evidenceName))
            {
                foreach (var cleanupPath in stagingPaths)
                    DeleteOwnedStaging(cleanupPath);
                continue;
            }
            if (stagingPaths.Count != 1)
            {
                throw Error(
                    LiveWorldSessionStoreError.EvidenceRollbackFailed,
                    $"Multiple durable staging files claim '{relativePath}'.");
            }

            var recoveryPath = stagingPaths[0];
            try
            {
                using var stream = new FileStream(
                    recoveryPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete);
                ValidateOpenedHandlePath(stream, recoveryPath);
                stream.Flush(flushToDisk: true);
                PublishDurableFile(recoveryPath, evidencePath);
                ValidateOpenedHandlePath(stream, evidencePath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException or NotSupportedException)
            {
                throw Error(
                    LiveWorldSessionStoreError.EvidenceRollbackFailed,
                    $"Could not recover durable staging for '{relativePath}'.",
                    innerException: exception);
            }
        }
    }

    private static T TranslateSnapshotStoreErrors<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (SceneSessionStoreException exception)
            when (exception.Error == SceneSessionStoreError.CorruptSnapshot)
        {
            throw Error(
                LiveWorldSessionStoreError.CorruptSnapshot,
                "Scene snapshot envelope is corrupt.",
                innerException: exception);
        }
        catch (SceneSessionStoreException exception)
            when (exception.Error == SceneSessionStoreError.UnsupportedSnapshotSchema)
        {
            throw Error(
                LiveWorldSessionStoreError.UnsupportedSnapshotSchema,
                $"Unsupported scene snapshot schema {exception.SchemaVersion}.",
                schemaVersion: exception.SchemaVersion,
                innerException: exception);
        }
    }

    private static bool IsAtomicStagingName(string name, string targetName) =>
        TryStripGuidSuffix(name, TemporaryMarker, out var baseName)
        && string.Equals(baseName, targetName, StringComparison.Ordinal);

    private static bool TryParseDurableStagingName(
        string name,
        out string evidenceName) =>
        TryStripGuidSuffix(name, DurableMarker, out evidenceName)
        && IsEvidenceFileName(evidenceName);

    private static bool TryStripGuidSuffix(
        string name,
        string marker,
        out string baseName)
    {
        var markerIndex = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0
            || !Guid.TryParseExact(name[(markerIndex + marker.Length)..], "N", out _))
        {
            baseName = string.Empty;
            return false;
        }
        baseName = name[..markerIndex];
        return true;
    }

    private static bool IsEvidenceFileName(string name)
    {
        if (!name.EndsWith(".png", StringComparison.Ordinal)
            || !int.TryParse(
                name.AsSpan(0, name.Length - ".png".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var observationId)
            || observationId < 0)
        {
            return false;
        }
        return string.Equals(
            name,
            $"{observationId:D8}.png",
            StringComparison.Ordinal);
    }

    private static void DeleteOwnedStaging(string path)
    {
        try
        {
            File.Delete(path);
            if (File.Exists(path))
            {
                throw new IOException("Owned staging cleanup did not remove the file.");
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            throw Error(
                LiveWorldSessionStoreError.EvidenceRollbackFailed,
                $"Could not clean owned staging file '{Path.GetFileName(path)}'.",
                innerException: exception);
        }
    }

    private static void ValidateStoredManifest(string rootDirectory)
    {
        var manifestPath = Path.Combine(rootDirectory, ManifestFileName);
        if (!File.Exists(manifestPath)) return;
        try
        {
            var manifest = JsonSerializer.Deserialize<SessionManifest>(
                    File.ReadAllBytes(manifestPath),
                    ManifestJsonOptions)
                ?? throw new JsonException("Live-world manifest is empty.");
            ValidateManifest(manifest, rootDirectory);
        }
        catch (LiveWorldSessionStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or NotSupportedException or OverflowException)
        {
            throw CorruptManifest("Live-world manifest is malformed.", exception);
        }
    }

    private void DeleteUnreferencedEvidence(
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
                LiveWorldSessionStoreError.EvidenceRollbackFailed,
                message,
                innerException: cause);
        }
    }

    private string ContainedEvidencePath(string imagePath) =>
        ContainedEvidencePath(RootDirectory, imagePath);

    private static string ContainedEvidencePath(string rootDirectory, string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)
            || Path.IsPathRooted(imagePath)
            || imagePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal))
        {
            throw new IOException("Stored source path escapes the live-world root.");
        }

        var candidate = CanonicalizePath(Path.Combine(rootDirectory, imagePath));
        var relative = Path.GetRelativePath(rootDirectory, candidate);
        if (relative is "." or ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new IOException("Stored source path escapes the live-world root.");
        }
        return candidate;
    }

    private static string CanonicalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("Path has no root.", nameof(path));
        var current = root;
        foreach (var component in fullPath[root.Length..].Split(
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
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static StorePaths PrepareStorePaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var canonicalRoot = CanonicalizePath(rootDirectory);
        var parent = Directory.GetParent(canonicalRoot)?.FullName
            ?? throw new IOException("Live-world root needs a parent directory.");
        Directory.CreateDirectory(parent);
        return new StorePaths(
            canonicalRoot,
            Path.Combine(
                parent,
                $".{Path.GetFileName(canonicalRoot)}.live-world-session.lock"));
    }

    private static bool SamePath(string first, string second) => string.Equals(
        CanonicalizePath(first),
        CanonicalizePath(second),
        StringComparison.OrdinalIgnoreCase);

    private static void ValidateOpenedHandlePath(
        FileStream handle,
        string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
        {
            var canonical = CanonicalizePath(expectedPath);
            if (!string.Equals(
                    canonical,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "Evidence path changed through a symbolic link while it was open.");
            }
            return;
        }

        var actualPath = GetWindowsFinalPath(handle);
        var normalizedExpected = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(expectedPath));
        if (!string.Equals(
                actualPath,
                normalizedExpected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Opened evidence resolves to '{actualPath}' instead of '{normalizedExpected}'.");
        }
    }

    private static string GetWindowsFinalPath(FileStream handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(
                handle.SafeFileHandle,
                buffer,
                checked((uint)buffer.Capacity),
                0);
            if (length == 0)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new IOException(
                    "Could not resolve the final path of opened evidence.",
                    error);
            }
            if (length < buffer.Capacity)
            {
                var path = buffer.ToString();
                if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                {
                    path = "\\\\" + path[8..];
                }
                else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
                {
                    path = path[4..];
                }
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            capacity = checked((int)length + 1);
        }
    }

    private static LiveWorldSessionStoreException MissingSource(
        int observationId,
        Exception? innerException = null) => Error(
            LiveWorldSessionStoreError.MissingSourceObservation,
            $"Live-world observation references unavailable source {observationId}.",
            observationId: observationId,
            innerException: innerException);

    private static LiveWorldSessionStoreException Invalidated() => Error(
        LiveWorldSessionStoreError.SessionInvalidated,
        "Live-world session was archived or replaced; use the fresh store.");

    private static LiveWorldSessionStoreException CorruptManifest(
        string message,
        Exception? innerException = null) => Error(
        LiveWorldSessionStoreError.CorruptSessionManifest,
        message,
        innerException: innerException);

    private static bool SameRectangles(
        IReadOnlyList<StencilRect> stored,
        IReadOnlyList<WorldRect> snapshot)
    {
        if (stored.Count != snapshot.Count) return false;
        for (var index = 0; index < stored.Count; index++)
        {
            if (stored[index].X != snapshot[index].X
                || stored[index].Y != snapshot[index].Y
                || stored[index].Width != snapshot[index].Width
                || stored[index].Height != snapshot[index].Height)
            {
                return false;
            }
        }
        return true;
    }

    private static LiveWorldSessionStoreException Error(
        LiveWorldSessionStoreError error,
        string message,
        int? snapshotRevision = null,
        int? diskRevision = null,
        int? observationId = null,
        int? schemaVersion = null,
        Exception? innerException = null) => new(
            error,
            message,
            snapshotRevision,
            diskRevision,
            observationId,
            schemaVersion,
            innerException);

    private sealed class DisposableHandles : IDisposable
    {
        private readonly List<FileStream> handles = [];

        internal void Add(FileStream handle) => handles.Add(handle);

        public void Dispose()
        {
            foreach (var handle in handles) handle.Dispose();
            handles.Clear();
        }
    }

    private sealed record ExistingSceneSnapshotEnvelope
    {
        internal const int CurrentSchemaVersion = 1;

        [JsonConstructor]
        public ExistingSceneSnapshotEnvelope(
            int schemaVersion,
            int revision,
            byte[] data)
        {
            SchemaVersion = schemaVersion;
            Revision = revision;
            Data = data ?? throw new JsonException("Scene snapshot data is missing.");
        }

        public int SchemaVersion { get; init; }
        public int Revision { get; init; }
        public byte[] Data { get; init; }
    }

    private sealed record StorePaths(
        string RootDirectory,
        string LifecycleLockPath);

    [Flags]
    private enum MoveFileFlags : uint
    {
        WriteThrough = 0x00000008
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        MoveFileFlags flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
