using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly ProfileLaunchDetector profileLaunchDetector = new();
    private bool profileAtlasHeld;
    private bool profileAtlasSwitchFailed;

    private ProfileLaunch? ObserveProfileAtlas(MenuStencilResult? menu, bool freshGameplay, double timestamp, long generation)
    {
        if (inputPathReplayCancellation is not null) return null;
        var launch = profileLaunchDetector.Observe(menu, freshGameplay, timestamp, generation);
        Volatile.Write(ref profileAtlasHeld, launch.HasValue || profileLaunchDetector.AwaitingGameplay || profileAtlasSwitchFailed);
        return launch;
    }

    private Task<bool> ActivateGameplayProfileAtlasAsync(ProfileLaunch launch) =>
        RunAtlasOperationAsync(async () =>
        {
            var modded = gameMode == GameRuntimeMode.Modded;
            if (modded) await FlushHackerAtlasAsync();
            var previousProfile = hackerArchiveProfile;
            var hackerWasReady = hackerArchiveReady;
            var previousHacker = modded && hackerWasReady
                ? hackerAtlasPipeline.CaptureArchive(CaptureHackerRoomOffsets()).Archive
                : null;
            LoadedHackerArchive? switchedHacker = null;
            var activated = await Task.Run(() =>
            {
                try
                {
                    return VisionResetController.ActivateProfileAtlas(atlasStateStore, activeAtlasDirectory, launch,
                        gameplayAtlas.TileSize, gameplayAtlas.PixelsPerWorldUnit, _ => ReopenActiveLiveWorld(),
                        prepareProfile: modded ? PrepareHacker : null);
                }
                catch (Exception replacementError)
                {
                    if (Directory.Exists(activeAtlasDirectory) && liveWorldPipeline is not null && !liveWorldPipeline.Reopen())
                        throw new AggregateException(replacementError, new InvalidDataException(
                            liveWorldPipeline.LastError ?? "The previous live-world pipeline could not be restored."));
                    throw;
                }
            });
            if (activated.Replacement is { } replacement)
            {
                atlasPersistTask = null;
                PublishPersistentAtlas(replacement.ValidatedWorld, explicitlyRequested: true, reopenLiveWorld: false);
                Interlocked.Increment(ref botRouteEpoch);
            }
            if (switchedHacker is not null) InstallHackerArchive(switchedHacker);
            if (activated.Replacement?.ArchivedPreviousAtlasDirectory is { } archive)
            {
                try
                {
                    await Task.Run(() => VisionResetController.ImportPreviousActiveAtlas(
                        atlasStateStore, archive, SaveProfileAtlas.Read(archive)?.Name ?? $"Atlas before {activated.Profile.Name}"));
                }
                catch (VisionResetController.PreviousActiveAtlasImportException error) { ShowError(error); }
            }
            SetStatus($"Atlas uses {activated.Profile.Name}.");

            void PrepareHacker(SaveProfileAtlas profile, bool adoptUnassigned)
            {
                if (hackerWasReady && previousProfile == profile) return;
                var archive = hackerArchiveStore.Read(profile);
                if (archive is null && adoptUnassigned && previousProfile is null && previousHacker is not null)
                {
                    hackerArchiveStore.Write(profile, previousHacker);
                    archive = previousHacker;
                }
                else if (previousHacker is not null && (previousProfile is not null || previousHacker.Rooms.Count > 0))
                {
                    _ = hackerArchiveStore.SaveState(previousProfile?.Name ?? $"Atlas before {profile.Name}", previousProfile, previousHacker);
                }
                switchedHacker = CreateHackerArchivePresentation(profile, archive);
            }
        }, suspendCapture: false);
}
