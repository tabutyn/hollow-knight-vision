using System.IO;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private static readonly TimeSpan HackerAutosaveInterval = TimeSpan.FromSeconds(15);
    private readonly HackerAtlasArchiveStore hackerArchiveStore;
    private SaveProfileAtlas? hackerArchiveProfile;
    private bool hackerArchiveReady;
    private Task? hackerArchiveWrite;
    private ulong hackerSavedRasterRevision;
    private long hackerLayoutRevision;
    private long hackerSavedLayoutRevision;
    private IReadOnlyDictionary<string, AtlasPoint> archivedHackerOffsets = new Dictionary<string, AtlasPoint>();
    private DateTimeOffset nextHackerAutosave;

    private sealed record LoadedHackerArchive(
        SaveProfileAtlas? Profile,
        HackerAtlasPipeline Pipeline,
        IReadOnlyDictionary<string, AtlasPoint> Offsets,
        HackerAtlasPublication Publication);

    private async Task InitializeHackerArchiveAsync()
    {
        if (gameMode != GameRuntimeMode.Modded || activeAtlasPersistenceBlocked) return;
        try
        {
            var profile = await Task.Run(() => SaveProfileAtlas.Read(activeAtlasDirectory));
            hackerArchiveProfile = profile;
            var loaded = await Task.Run(() =>
            {
                var archive = hackerArchiveStore.Read(profile);
                return archive is not null || profile is not null ? CreateHackerArchivePresentation(profile, archive) : null;
            });
            if (isClosing) return;
            if (loaded is not null) InstallHackerArchive(loaded);
            else hackerArchiveReady = true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"Hacker Atlas could not be loaded: {error.Message}");
        }
    }

    private async Task PersistHackerAtlasPeriodicallyAsync()
    {
        try { await QueueHackerAtlasSave(force: false); }
        catch (Exception error)
        {
            SetStatus($"Hacker Atlas autosave failed: {error.Message}");
        }
    }

    private async Task FlushHackerAtlasAsync()
    {
        if (hackerArchiveWrite is { IsCompleted: false } pending) await pending;
        await QueueHackerAtlasSave(force: true);
    }

    private Task QueueHackerAtlasSave(bool force)
    {
        if (!hackerArchiveReady || hackerArchiveWrite is { IsCompleted: false })
            return Task.CompletedTask;
        if (!force && (atlasOperationInProgress || isClosing || Volatile.Read(ref profileAtlasHeld)
            || DateTimeOffset.UtcNow < nextHackerAutosave))
            return Task.CompletedTask;
        if (hackerAtlasPipeline.Revision == hackerSavedRasterRevision && hackerLayoutRevision == hackerSavedLayoutRevision)
            return Task.CompletedTask;

        nextHackerAutosave = DateTimeOffset.UtcNow + HackerAutosaveInterval;
        var layoutRevision = hackerLayoutRevision;
        var profile = hackerArchiveProfile;
        var capture = hackerAtlasPipeline.CaptureArchive(CaptureHackerRoomOffsets());
        return hackerArchiveWrite = WriteAsync();

        async Task WriteAsync()
        {
            await Task.Run(() => hackerArchiveStore.Write(profile, capture.Archive));
            hackerSavedRasterRevision = capture.Revision;
            hackerSavedLayoutRevision = layoutRevision;
        }
    }

    private Dictionary<string, AtlasPoint> CaptureHackerRoomOffsets()
    {
        var offsets = new Dictionary<string, AtlasPoint>(archivedHackerOffsets, StringComparer.Ordinal);
        foreach (var room in rooms.Values)
        {
            if (room.OffsetX != 0 || room.OffsetY != 0) offsets[room.SceneName] = new(room.OffsetX, room.OffsetY);
            else offsets.Remove(room.SceneName);
        }
        return offsets;
    }

    private static LoadedHackerArchive CreateHackerArchivePresentation(SaveProfileAtlas? profile, HackerAtlasArchive? archive)
    {
        var pipeline = archive is null ? new HackerAtlasPipeline() : HackerAtlasPipeline.FromArchive(archive);
        return new(profile, pipeline, archive?.RoomOffsets ?? new Dictionary<string, AtlasPoint>(),
            StageHackerAtlas(pipeline.Snapshot()));
    }

    private void InstallHackerArchive(LoadedHackerArchive loaded)
    {
        ClearHackerPresentation();
        lock (hackerAtlasGate)
        {
            hackerAtlasPipeline = loaded.Pipeline;
            latestStagedHackerAtlas = loaded.Publication;
        }
        hackerArchiveProfile = loaded.Profile;
        hackerArchiveReady = true;
        hackerSavedRasterRevision = loaded.Pipeline.Revision;
        hackerSavedLayoutRevision = hackerLayoutRevision;
        hackerPublishedRevision = loaded.Publication.Revision;
        nextHackerAutosave = default;
        archivedHackerOffsets = loaded.Offsets;
        RenderHackerAtlasPublication(loaded.Publication);
        RenderHackerAtlasLayout();
    }

    private async Task ClearHackerAtlasAsync()
    {
        if (hackerArchiveWrite is { IsCompleted: false } pending) await pending;
        var empty = CreateHackerArchivePresentation(hackerArchiveProfile, null);
        var canWrite = hackerArchiveReady || gameMode == GameRuntimeMode.Modded && !activeAtlasPersistenceBlocked;
        if (canWrite)
        {
            var state = empty.Pipeline.CaptureArchive(empty.Offsets).Archive;
            await Task.Run(() => hackerArchiveStore.Write(empty.Profile, state));
        }
        InstallHackerArchive(empty);
        hackerArchiveReady = canWrite;
        PersistHackerEdits();
    }
}
