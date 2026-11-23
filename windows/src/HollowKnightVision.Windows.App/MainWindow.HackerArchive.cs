using System.IO;
using System.Text.Json;
using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private static readonly TimeSpan HackerAutosaveInterval = TimeSpan.FromSeconds(15);
    private readonly HackerAtlasArchiveStore hackerArchiveStore;
    private SaveProfileAtlas? hackerArchiveProfile;
    private bool hackerArchiveReady;
    private bool hackerArchiveOwnerKnown;
    private Task? hackerArchiveWrite;
    private ulong hackerSavedRasterRevision;
    private long hackerLayoutRevision;
    private long hackerSavedLayoutRevision;
    private IReadOnlyDictionary<string, AtlasPoint> archivedHackerOffsets = new Dictionary<string, AtlasPoint>();
    private DateTimeOffset nextHackerAutosave;

    private sealed record LoadedHackerArchive(
        SaveProfileAtlas? Profile,
        HackerAtlasPipeline Pipeline,
        IReadOnlyList<HackerAtlasArchivedRoom> Rooms,
        IReadOnlyDictionary<string, AtlasPoint> Offsets,
        HackerAtlasPublication Publication);

    private async Task InitializeHackerArchiveAsync()
    {
        if (gameMode != GameRuntimeMode.Modded) return;
        try
        {
            var profile = await Task.Run(() => SaveProfileAtlas.Read(activeAtlasDirectory));
            hackerArchiveProfile = profile;
            hackerArchiveOwnerKnown = true;
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
        return new(profile, pipeline, archive?.Rooms ?? [], archive?.RoomOffsets ?? new Dictionary<string, AtlasPoint>(),
            StageHackerAtlas(pipeline.Snapshot()));
    }

    private void InstallHackerArchive(LoadedHackerArchive loaded)
    {
        ClearHackerPresentation();
        lock (hackerAtlasGate)
        {
            hackerAtlasPipeline = loaded.Pipeline;
            hackerFramePreparer.RestoreRoomAnchors(loaded.Rooms);
            latestStagedHackerAtlas = loaded.Publication;
        }
        hackerArchiveProfile = loaded.Profile;
        hackerArchiveOwnerKnown = true;
        hackerArchiveReady = true;
        hackerSavedRasterRevision = loaded.Pipeline.Revision;
        hackerSavedLayoutRevision = hackerLayoutRevision;
        hackerPublishedRevision = loaded.Publication.Revision;
        nextHackerAutosave = default;
        archivedHackerOffsets = loaded.Offsets;
        RenderHackerAtlasPublication(loaded.Publication);
        RenderHackerAtlasLayout();
    }

    private Task ClearHackerAtlasAsync() => ReplaceHackerArchiveAsync(state: null, keepPrevious: false);

    private async Task ReplaceHackerArchiveAsync(AtlasSavedState? state, bool keepPrevious)
    {
        if (!hackerArchiveOwnerKnown)
            throw new InvalidOperationException("The current Hacker Atlas profile could not be identified.");
        if (hackerArchiveWrite is { IsCompleted: false } pending) await pending;
        var profile = hackerArchiveProfile;
        var previous = keepPrevious && hackerArchiveReady
            ? hackerAtlasPipeline.CaptureArchive(CaptureHackerRoomOffsets()).Archive : null;
        var prepared = await Task.Run(() =>
        {
            var next = state is null ? null : hackerArchiveStore.LoadState(state);
            var presentation = CreateHackerArchivePresentation(profile, next);
            var newArchive = next ?? presentation.Pipeline.CaptureArchive(presentation.Offsets).Archive;
            var backupName = keepPrevious
                ? profile?.Name ?? $"Before {(state is null ? "reset" : "load")} {DateTime.Now:g}" : null;
            var backup = hackerArchiveStore.Replace(profile, previous, newArchive, backupName);
            return (Presentation: presentation, Backup: backup);
        });
        InstallHackerArchive(prepared.Presentation);
        if (state is not null) SettingsOverlay.Visibility = Visibility.Collapsed;
        var status = state is null ? "Active Hacker Atlas cleared." : $"Loaded {state.Name}.";
        if (prepared.Backup is { } saved) status += $" Previous Auto Save is available as {saved.Name}.";
        SetStatus(status);
    }

    private async Task SaveHackerAtlasStateAsync(string name)
    {
        if (!hackerArchiveReady)
            throw new InvalidOperationException("The current Hacker Atlas is unavailable.");
        await FlushHackerAtlasAsync();
        var profile = hackerArchiveProfile;
        var archive = hackerAtlasPipeline.CaptureArchive(CaptureHackerRoomOffsets()).Archive;
        var saved = await Task.Run(() => hackerArchiveStore.SaveState(name, profile, archive));
        AtlasStateNameText.Clear();
        SetStatus($"Saved immutable atlas state {saved.Name}.");
    }
}
