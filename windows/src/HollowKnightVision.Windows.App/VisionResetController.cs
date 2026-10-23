using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class VisionResetController
{
    internal enum ActiveAtlasReplacementError
    {
        LoadRollbackFailed
    }

    internal sealed class ActiveAtlasReplacementException(
        ActiveAtlasReplacementError error,
        string archivedPreviousAtlasDirectory,
        Exception innerException) : IOException(
            "The saved atlas could not be loaded and the previous active atlas could not be restored. " +
            $"The previous atlas remains at '{archivedPreviousAtlasDirectory}'.",
            innerException)
    {
        internal ActiveAtlasReplacementError Error { get; } = error;
        internal string ArchivedPreviousAtlasDirectory { get; } = archivedPreviousAtlasDirectory;
    }

    internal sealed record ActiveAtlasReplacement<T>(
        T ValidatedWorld,
        string? ArchivedPreviousAtlasDirectory,
        string? RecoveredFailedGenerationDirectory = null);

    internal sealed record PersistedAtlasMetadata(
        long RegistrationCount,
        DateTimeOffset CreatedAt,
        int TileSize,
        double PixelsPerWorldUnit);

    internal sealed record ValidatedPersistedAtlas(
        GroundTruthAtlasMosaic Mosaic,
        PersistedAtlasMetadata Metadata);

    internal sealed class PreviousActiveAtlasImportException(
        string recoverableArchiveDirectory,
        Exception innerException) : IOException(
            "The saved atlas was loaded, but its previous active atlas could not be added "
            + "to the restore list. The recoverable archive remains at "
            + $"'{recoverableArchiveDirectory}'.",
            innerException)
    {
        internal string RecoverableArchiveDirectory { get; } = recoverableArchiveDirectory;
    }

    internal static AtlasSavedState? ArchiveActiveAtlas(AtlasStateStore states, string activeDirectory)
    {
        var active = ValidatedActiveAtlasPath(states, activeDirectory);
        return Directory.Exists(active)
            ? states.ImportArchive(active, $"Before reset {DateTime.Now:g}") : null;
    }

    /// <summary>
    /// Copies and validates a named state in a sibling staging directory before
    /// atomically replacing the active atlas. The previous active directory is
    /// returned for the caller to publish as a named restore point. A failed
    /// swap restores it; a failed rollback reports the archive's exact path.
    /// </summary>
    internal static ActiveAtlasReplacement<T> ReplaceActiveAtlas<T>(
        AtlasStateStore states,
        AtlasSavedState state,
        string activeDirectory,
        Func<string, T> validateStagedWorld,
        Action<string, string>? copyDirectory = null,
        Action<string, string>? moveDirectory = null,
        Action<string>? activateWorld = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(validateStagedWorld);
        var active = ValidatedActiveAtlasPath(states, activeDirectory);
        var recoveredFailedGeneration = RecoverInterruptedActiveAtlasPublication(
            states,
            active,
            validateStagedWorld);
        var saved = states.WorldPath(state);
        var parent = Path.GetDirectoryName(active)!;
        var leaf = Path.GetFileName(active);
        var staging = Path.Combine(parent, $"{leaf}.staging-load-{Guid.NewGuid():N}");
        var archive = Path.Combine(parent, $"{leaf}.archive-load-{Guid.NewGuid():N}");
        var copy = copyDirectory ?? CopyDirectoryDurably;
        var move = moveDirectory ?? MoveDirectoryDurably;
        var hadActive = Directory.Exists(active);
        var activeMoved = false;

        try
        {
            copy(saved, staging);
            FlushDirectoryTree(staging);
            var validated = validateStagedWorld(staging);
            if (hadActive)
            {
                move(active, archive);
                activeMoved = true;
            }
            try
            {
                move(staging, active);
                activateWorld?.Invoke(active);
            }
            catch (Exception replacementError)
            {
                try
                {
                    if (Directory.Exists(active)) Directory.Delete(active, recursive: true);
                    if (activeMoved) move(archive, active);
                }
                catch (Exception rollbackError)
                {
                    throw new ActiveAtlasReplacementException(
                        ActiveAtlasReplacementError.LoadRollbackFailed,
                        archive,
                        new AggregateException(replacementError, rollbackError));
                }
                throw;
            }
            return new ActiveAtlasReplacement<T>(
                validated,
                activeMoved ? archive : null,
                recoveredFailedGeneration);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>
    /// Builds a complete sibling generation, validates it, then swaps it into
    /// the fixed active path. The previous generation is never edited in place.
    /// </summary>
    internal static ActiveAtlasReplacement<T> PublishActiveAtlasGeneration<T>(
        AtlasStateStore states,
        string activeDirectory,
        bool copyActiveWorld,
        bool retainPrevious,
        Action<string> writeStagedGeneration,
        Func<string, T> validateStagedWorld,
        Action<string>? activateWorld = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(writeStagedGeneration);
        ArgumentNullException.ThrowIfNull(validateStagedWorld);
        var active = ValidatedActiveAtlasPath(states, activeDirectory);
        var recoveredFailedGeneration = RecoverInterruptedActiveAtlasPublication(
            states,
            active,
            validateStagedWorld);
        var parent = Path.GetDirectoryName(active)!;
        var leaf = Path.GetFileName(active);
        var staging = Path.Combine(parent, $"{leaf}.staging-publish-{Guid.NewGuid():N}");
        var archiveKind = retainPrevious ? "retained-publish" : "archive-publish";
        var archive = Path.Combine(parent, $"{leaf}.{archiveKind}-{Guid.NewGuid():N}");
        var hadActive = Directory.Exists(active);
        var activeMoved = false;

        try
        {
            if (copyActiveWorld && hadActive)
                CopyDirectoryDurably(active, staging);
            else
                Directory.CreateDirectory(staging);
            writeStagedGeneration(staging);
            FlushDirectoryTree(staging);
            var validated = validateStagedWorld(staging);
            if (hadActive)
            {
                MoveDirectoryDurably(active, archive);
                activeMoved = true;
            }
            try
            {
                MoveDirectoryDurably(staging, active);
                activateWorld?.Invoke(active);
            }
            catch (Exception replacementError)
            {
                try
                {
                    if (Directory.Exists(active)) Directory.Delete(active, recursive: true);
                    if (activeMoved) MoveDirectoryDurably(archive, active);
                }
                catch (Exception rollbackError)
                {
                    throw new ActiveAtlasReplacementException(
                        ActiveAtlasReplacementError.LoadRollbackFailed,
                        archive,
                        new AggregateException(replacementError, rollbackError));
                }
                throw;
            }

            var retainedArchive = activeMoved && retainPrevious ? archive : null;
            if (activeMoved && !retainPrevious) TryDeleteDirectory(archive);
            return new ActiveAtlasReplacement<T>(
                validated,
                retainedArchive,
                recoveredFailedGeneration);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>
    /// Repairs only transaction-owned sibling generations. An interrupted
    /// commit restores the last validated archive; a completed commit discards
    /// only validated stale staging/archive siblings and preserves corrupt
    /// generations as recovery evidence.
    /// </summary>
    /// <returns>The preserved failed-generation path, when recovery replaced one.</returns>
    internal static string? RecoverInterruptedActiveAtlasPublication<T>(
        AtlasStateStore states,
        string activeDirectory,
        Func<string, T> validateWorld)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(validateWorld);
        var active = ValidatedActiveAtlasPath(states, activeDirectory);
        var parent = Path.GetDirectoryName(active)!;
        var leaf = Path.GetFileName(active);
        var staging = EnumerateOwnedDirectories("staging-publish", "staging-load");
        var transientArchives = EnumerateOwnedDirectories("archive-publish");
        // A reset deliberately retains its predecessor until MainWindow imports
        // it as a named restore point. Legacy and current load archives have the
        // same contract. They must survive a process loss between the directory
        // swap and that import.
        var retainedArchives = EnumerateOwnedDirectories(
            "retained-publish", "archive-load", "retained-load");
        var archives = transientArchives.Concat(retainedArchives)
            .OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
        if (staging.Length == 0 && archives.Length == 0) return null;

        if (Directory.Exists(active))
        {
            try
            {
                _ = validateWorld(active);
                PreserveRetainedArchives(retainedArchives);
                foreach (var path in staging.Concat(transientArchives).Where(IsValid))
                    TryDeleteDirectory(path);
                return null;
            }
            catch when (archives.Length > 0 || staging.Length > 0)
            {
                // A torn final generation may be replaced by its transaction-owned
                // predecessor below; unrelated corrupt active worlds are untouched.
            }
        }

        var recovery = archives.FirstOrDefault(IsValid)
            ?? staging.FirstOrDefault(IsValid);
        if (recovery is null)
            throw new InvalidDataException(
                "Interrupted atlas publication has no validated recovery generation.");

        var failedActive = Directory.Exists(active)
            ? Path.Combine(parent, $"{leaf}.failed-generation-{Guid.NewGuid():N}")
            : null;
        try
        {
            if (failedActive is not null) MoveDirectoryDurably(active, failedActive);
            MoveDirectoryDurably(recovery, active);
            _ = validateWorld(active);
        }
        catch
        {
            if (failedActive is not null && Directory.Exists(failedActive))
            {
                if (Directory.Exists(active) && !Directory.Exists(recovery))
                    MoveDirectoryDurably(active, recovery);
                MoveDirectoryDurably(failedActive, active);
            }
            throw;
        }

        PreserveRetainedArchives(retainedArchives.Where(path =>
            !string.Equals(path, recovery, StringComparison.OrdinalIgnoreCase)));
        foreach (var path in staging.Concat(transientArchives))
        {
            if (!string.Equals(path, recovery, StringComparison.OrdinalIgnoreCase)
                && IsValid(path))
                TryDeleteDirectory(path);
        }
        if (failedActive is not null)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Recovered the previous Auto Save atlas. The failed generation was preserved at '{0}'.",
                failedActive);
        }
        return failedActive;

        bool IsValid(string directory)
        {
            try
            {
                _ = validateWorld(directory);
                return true;
            }
            catch (Exception error) when (error is InvalidDataException
                or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        string[] EnumerateOwnedDirectories(params string[] kinds)
        {
            if (!Directory.Exists(parent)) return [];
            return kinds.SelectMany(kind => Directory.EnumerateDirectories(
                    parent, $"{leaf}.{kind}-*", SearchOption.TopDirectoryOnly))
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToArray();
        }

        void PreserveRetainedArchives(IEnumerable<string> directories)
        {
            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory) || !IsValid(directory)) continue;
                try
                {
                    _ = ImportPreviousActiveAtlas(
                        states,
                        directory,
                        $"Recovered previous Auto Save {Directory.GetLastWriteTime(directory):g}");
                }
                catch (PreviousActiveAtlasImportException)
                {
                    // ImportPreviousActiveAtlas leaves the recoverable generation
                    // in place. A later startup can retry without blocking use of
                    // the already validated active generation.
                }
            }
        }
    }

    /// <summary>
    /// Strictly materializes a persisted atlas into a fresh mosaic. The caller
    /// can validate a staging copy without reading or mutating its live mosaic.
    /// </summary>
    internal static ValidatedPersistedAtlas ReadPersistedAtlas(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        try
        {
            if (!Directory.Exists(root))
                throw new InvalidDataException("The atlas directory is missing.");
            if (File.Exists(Path.Combine(root, ".atlas-publication")))
            {
                throw new InvalidDataException(
                    "The atlas contains an interrupted publication and was left unchanged.");
            }
            var metadata = ReadPersistedMetadata(Path.Combine(root, "atlas.json"));
            var mosaic = new GroundTruthAtlasMosaic(
                metadata.PixelsPerWorldUnit,
                metadata.TileSize,
                sourceTopInsetFraction: 0);
            var coordinates = new HashSet<AtlasTileCoordinate>();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(path);
                var isRootFile = string.Equals(
                    Path.GetDirectoryName(path),
                    root,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
                if (isRootFile
                    && fileName.Equals("atlas.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fileName.StartsWith("tile_", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetExtension(fileName).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!isRootFile)
                    throw new InvalidDataException(
                        $"Atlas tile file '{Path.GetRelativePath(root, path)}' is not at the atlas root.");
                var coordinate = ParsePersistedTileName(fileName, metadata.TileSize);
                if (!coordinates.Add(coordinate))
                    throw new InvalidDataException(
                        $"Atlas contains duplicate tile coordinate {coordinate.X},{coordinate.Y}.");
                mosaic.ImportTile(coordinate, ReadAtlasBmp(path, metadata.TileSize));
            }
            if (coordinates.Count > 0)
            {
                var width = ((long)coordinates.Max(item => item.X)
                    - coordinates.Min(item => item.X) + 1) * metadata.TileSize;
                var height = ((long)coordinates.Max(item => item.Y)
                    - coordinates.Min(item => item.Y) + 1) * metadata.TileSize;
                if (width > int.MaxValue || height > int.MaxValue)
                    throw new InvalidDataException(
                        "Atlas tile coordinates span an unsupported world extent.");
            }
            mosaic.RestoreAcceptedFrameCount(metadata.RegistrationCount);
            ValidateLiveWorld(root);
            return new ValidatedPersistedAtlas(mosaic, metadata);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException(
                $"The persisted atlas at '{root}' is invalid or unreadable.", error);
        }
    }

    /// <summary>
    /// Copies one quiesced active revision into a state-store staging world,
    /// then validates every atlas and live-world byte before the state store
    /// atomically publishes it.
    /// </summary>
    internal static void CopyValidatedAtlas(
        string activeDirectory,
        string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var active = Path.GetFullPath(activeDirectory);
        if (!Directory.Exists(active))
            throw new InvalidDataException("The active atlas directory is missing.");
        CopyDirectory(active, destinationDirectory);
        _ = ReadPersistedAtlas(destinationDirectory);
    }

    /// <summary>
    /// Copies a post-swap archive into the named-state library before removing
    /// it. Any import failure therefore leaves the exact archive path intact.
    /// </summary>
    internal static AtlasSavedState ImportPreviousActiveAtlas(
        AtlasStateStore states,
        string archiveDirectory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        var archive = Path.GetFullPath(archiveDirectory);
        try
        {
            var state = states.Save(
                name,
                destination => CopyDirectoryDurably(archive, destination));
            var savedWorld = states.WorldPath(state);
            FlushDirectoryTree(Directory.GetParent(savedWorld)?.FullName
                ?? throw new InvalidDataException(
                    "The imported atlas state directory is missing."));
            TryDeleteDirectory(archive);
            return state;
        }
        catch (Exception error)
        {
            throw new PreviousActiveAtlasImportException(archive, error);
        }
    }

    internal static string? ResetStencils(string? examplesRoot = null, string? supportRoot = null)
    {
        var root = supportRoot ?? ApplicationDataRoot();
        var store = new LabelingExampleStore(examplesRoot ?? Path.Combine(root, "labeling-v1", "examples"));
        var contexts = StencilInitializationPolicy.AllContexts.Where(item => item.Identifier != "game")
            .Select(item => item.Identifier).ToHashSet(StringComparer.Ordinal);
        var sources = store.Load().Where(example => contexts.Contains(
                StencilInitializationPolicy.NormalizeContext(example.Manifest.ContextIdentifier)))
            .Select(example => example.DirectoryPath)
            .Append(Path.Combine(root, "menu-stencil-live-v1"))
            .Where(Directory.Exists).ToArray();
        return Archive(sources, "stencils", supportRoot);
    }

    internal static string? FactoryReset()
    {
        var sources = new[]
        {
            ApplicationDataRoot(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "HollowKnightVision")
        }.Where(Directory.Exists).ToArray();
        return Archive(sources, "factory");
    }

    private static string ApplicationDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HollowKnightVision");

    private static string ValidatedActiveAtlasPath(AtlasStateStore states, string activeDirectory)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeDirectory);
        // Only the library's sibling 'active' directory is eligible. Never
        // accept a support root, saved-state directory, or arbitrary path.
        var active = Path.GetFullPath(activeDirectory);
        var expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(states.RootDirectory)!, "active"));
        if (!string.Equals(active, expected, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Only the current active atlas can be replaced.", nameof(activeDirectory));
        return active;
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceRoot = Path.GetFullPath(source);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException("Atlas copy source is missing.");
        if (Directory.Exists(destination))
            throw new IOException("Atlas staging directory already exists.");
        RejectReparsePoint(sourceRoot);
        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        var pending = new Queue<string>();
        pending.Enqueue(sourceRoot);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory);
                var target = ContainedCopyPath(
                    sourceRoot,
                    destinationRoot,
                    directory);
                Directory.CreateDirectory(target);
                pending.Enqueue(directory);
            }
            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(file);
                File.Copy(
                    file,
                    ContainedCopyPath(sourceRoot, destinationRoot, file));
            }
        }
    }

    private static void CopyDirectoryDurably(string source, string destination)
    {
        var sourceRoot = Path.GetFullPath(source);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException("Atlas copy source is missing.");
        if (Directory.Exists(destination))
            throw new IOException("Atlas staging directory already exists.");
        RejectReparsePoint(sourceRoot);
        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        var pending = new Queue<string>();
        pending.Enqueue(sourceRoot);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var directory in Directory.EnumerateDirectories(
                         current, "*", SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory);
                var target = ContainedCopyPath(sourceRoot, destinationRoot, directory);
                Directory.CreateDirectory(target);
                pending.Enqueue(directory);
            }
            foreach (var file in Directory.EnumerateFiles(
                         current, "*", SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(file);
                var target = ContainedCopyPath(sourceRoot, destinationRoot, file);
                using var input = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(
                    target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
        }
    }

    private static void FlushDirectoryTree(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(
                     directory, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(file);
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                4096, FileOptions.WriteThrough);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void MoveDirectoryDurably(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.Move(source, destination);
            return;
        }
        if (!MoveFileEx(source, destination, MoveFileFlags.WriteThrough))
        {
            throw new IOException(
                $"Could not publish atlas directory '{Path.GetFileName(destination)}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private static string ContainedCopyPath(
        string sourceRoot,
        string destinationRoot,
        string sourcePath)
    {
        var relative = Path.GetRelativePath(sourceRoot, sourcePath);
        if (relative == "."
            || Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Atlas copy escaped its source directory.");
        }
        var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));
        var prefix = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Atlas copy escaped its staging directory.");
        }
        return target;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Atlas copy refuses reparse point '{path}'.");
        }
    }

    private static PersistedAtlasMetadata ReadPersistedMetadata(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("Atlas metadata is missing.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Atlas metadata must be a JSON object.");
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value))
                throw new InvalidDataException(
                    $"Atlas metadata repeats '{property.Name}'.");
        }

        JsonElement Required(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException($"Atlas metadata is missing '{name}'.");
        var registration = Required("RegistrationCount");
        var created = Required("CreatedAt");
        var tileSizeValue = Required("TileSize");
        var pixelsPerWorldUnitValue = Required("PixelsPerWorldUnit");
        if (!registration.TryGetInt64(out var registrationCount) || registrationCount < 0)
            throw new InvalidDataException("Atlas registration count must be nonnegative.");
        if (created.ValueKind != JsonValueKind.String
            || !created.TryGetDateTimeOffset(out var createdAt))
            throw new InvalidDataException("Atlas creation timestamp is invalid.");
        if (!tileSizeValue.TryGetInt32(out var tileSize) || tileSize is < 32 or > 2048)
            throw new InvalidDataException("Atlas tile size is outside the supported range.");
        if (pixelsPerWorldUnitValue.ValueKind != JsonValueKind.Number
            || !pixelsPerWorldUnitValue.TryGetDouble(out var pixelsPerWorldUnit)
            || !double.IsFinite(pixelsPerWorldUnit)
            || pixelsPerWorldUnit is <= 0 or > 1024)
        {
            throw new InvalidDataException(
                "Atlas pixels-per-world-unit is outside the supported range.");
        }
        return new PersistedAtlasMetadata(
            registrationCount, createdAt, tileSize, pixelsPerWorldUnit);
    }

    private static void ValidateLiveWorld(string atlasRoot)
    {
        var liveWorld = Path.Combine(atlasRoot, "live-world");
        // Atlas generations created before live-world persistence are the only
        // valid case with no live-world directory at all. Once the directory
        // exists, every byte must belong to a complete, read-only-valid session.
        if (!Directory.Exists(liveWorld)) return;
        try
        {
            var state = LiveWorldSessionStore.ValidateExisting(liveWorld);
            _ = new LiveWorldTracker(state.Snapshot);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or LiveWorldSessionStoreException
            or ArgumentException
            or OverflowException)
        {
            throw new InvalidDataException(
                $"The persisted live world at '{liveWorld}' is invalid or unreadable.",
                error);
        }
    }

    private static AtlasTileCoordinate ParsePersistedTileName(string fileName, int tileSize)
    {
        var extension = Path.GetExtension(fileName);
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        if (!extension.Equals(".bmp", StringComparison.Ordinal)
            || parts.Length != 3
            || !parts[0].Equals("tile", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var y)
            || !fileName.Equals($"tile_{x}_{y}.bmp", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Atlas tile file name '{fileName}' is malformed.");
        }
        static bool IsLegalCoordinate(int coordinate, int size)
        {
            var first = (long)coordinate * size;
            var last = first + size - 1L;
            return first >= int.MinValue && last <= int.MaxValue;
        }
        if (!IsLegalCoordinate(x, tileSize) || !IsLegalCoordinate(y, tileSize))
            throw new InvalidDataException(
                $"Atlas tile coordinate {x},{y} is outside the supported world range.");
        return new AtlasTileCoordinate(x, y);
    }

    private static BgraFrame ReadAtlasBmp(string path, int expectedSize)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
            throw new InvalidDataException(
                $"Atlas tile '{Path.GetFileName(path)}' is not a BMP file.");
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(2, 4));
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10, 4));
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14, 4));
        if (declaredLength != data.Length || headerSize < 40
            || 14L + headerSize > data.Length || pixelOffset < 14 + headerSize)
            throw new InvalidDataException("Atlas BMP header is inconsistent.");
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26, 2));
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28, 2));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30, 4));
        if (signedHeight == int.MinValue)
            throw new InvalidDataException("Atlas BMP height is invalid.");
        var height = Math.Abs(signedHeight);
        if (width != expectedSize || height != expectedSize)
            throw new InvalidDataException(
                $"Atlas BMP dimensions must be {expectedSize}×{expectedSize}.");
        if (planes != 1 || bitsPerPixel is not (24 or 32)
            || compression != 0 && !(compression == 3 && bitsPerPixel == 32))
            throw new InvalidDataException("Atlas BMP pixel format is unsupported.");
        var sourceStride = checked((int)(((long)bitsPerPixel * width + 31) / 32 * 4));
        var requiredLength = checked((long)pixelOffset + (long)sourceStride * height);
        if (requiredLength > data.Length)
            throw new InvalidDataException("Atlas BMP pixel data is truncated.");

        uint redMask = 0x00ff0000;
        uint greenMask = 0x0000ff00;
        uint blueMask = 0x000000ff;
        uint alphaMask = compression == 0 ? 0xff000000 : 0;
        if (compression == 3)
        {
            if (pixelOffset < 66)
                throw new InvalidDataException("Atlas BMP bit masks are missing.");
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(54, 4));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(58, 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(62, 4));
            if (pixelOffset >= 70)
                alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(66, 4));
            ValidateMask(redMask);
            ValidateMask(greenMask);
            ValidateMask(blueMask);
            if (alphaMask != 0) ValidateMask(alphaMask);
            if ((redMask & greenMask) != 0 || (redMask & blueMask) != 0
                || (greenMask & blueMask) != 0 || (alphaMask & (redMask | greenMask | blueMask)) != 0)
                throw new InvalidDataException("Atlas BMP bit masks overlap.");
        }

        var outputStride = checked(width * BgraFrame.BytesPerPixel);
        var output = new byte[checked(outputStride * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = signedHeight < 0 ? y : height - y - 1;
            var source = checked((int)pixelOffset + sourceY * sourceStride);
            var destination = y * outputStride;
            for (var x = 0; x < width; x++)
            {
                var outputIndex = destination + x * BgraFrame.BytesPerPixel;
                if (bitsPerPixel == 24)
                {
                    var inputIndex = source + x * 3;
                    output[outputIndex] = data[inputIndex];
                    output[outputIndex + 1] = data[inputIndex + 1];
                    output[outputIndex + 2] = data[inputIndex + 2];
                    output[outputIndex + 3] = 255;
                }
                else
                {
                    var pixel = BinaryPrimitives.ReadUInt32LittleEndian(
                        data.AsSpan(source + x * 4, 4));
                    output[outputIndex] = Component(pixel, blueMask, 0);
                    output[outputIndex + 1] = Component(pixel, greenMask, 0);
                    output[outputIndex + 2] = Component(pixel, redMask, 0);
                    output[outputIndex + 3] = Component(pixel, alphaMask, 255);
                }
            }
        }
        return new BgraFrame(width, height, outputStride, output, 0, DateTimeOffset.UtcNow);
    }

    private static void ValidateMask(uint mask)
    {
        if (mask == 0) throw new InvalidDataException("Atlas BMP color mask is empty.");
        while ((mask & 1) == 0) mask >>= 1;
        if ((mask & (mask + 1)) != 0)
            throw new InvalidDataException("Atlas BMP color mask is non-contiguous.");
    }

    private static byte Component(uint pixel, uint mask, byte fallback)
    {
        if (mask == 0) return fallback;
        var shift = 0;
        while ((mask & 1) == 0) { mask >>= 1; shift++; }
        var value = (pixel >> shift) & mask;
        return checked((byte)((value * 255L + mask / 2) / mask));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? Archive(IReadOnlyList<string> sources, string kind, string? supportRoot = null)
    {
        if (sources.Count == 0) return null;
        var backupRoot = Path.Combine(
            supportRoot is null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.GetDirectoryName(supportRoot)!, "HollowKnightVision Backups");
        Directory.CreateDirectory(backupRoot);
        var destination = Path.Combine(
            backupRoot,
            $"{kind}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        var moved = new List<(string Source, string Destination)>();
        try
        {
            foreach (var source in sources)
            {
                var target = Path.Combine(destination, Path.GetFileName(source));
                if (Directory.Exists(target)) target += $"-{moved.Count + 1}";
                Directory.Move(source, target);
                moved.Add((source, target));
            }
            return destination;
        }
        catch
        {
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                if (!Directory.Exists(item.Source) && Directory.Exists(item.Destination))
                {
                    Directory.Move(item.Destination, item.Source);
                }
            }
            if (!Directory.EnumerateFileSystemEntries(destination).Any())
            {
                Directory.Delete(destination);
            }
            throw;
        }
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        WriteThrough = 0x8
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
}
