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
            var activated = await Task.Run(() =>
            {
                try
                {
                    return VisionResetController.ActivateProfileAtlas(atlasStateStore, activeAtlasDirectory, launch,
                        gameplayAtlas.TileSize, gameplayAtlas.PixelsPerWorldUnit, _ => ReopenActiveLiveWorld());
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
                InvalidatePendingHackerWork();
                if (replacement.ArchivedPreviousAtlasDirectory is { } archive)
                {
                    try
                    {
                        await Task.Run(() => VisionResetController.ImportPreviousActiveAtlas(
                            atlasStateStore, archive, SaveProfileAtlas.Read(archive)?.Name ?? $"Atlas before {activated.Profile.Name}"));
                    }
                    catch (VisionResetController.PreviousActiveAtlasImportException error) { ShowError(error); }
                }
            }
            SetStatus($"Gameplay Atlas uses {activated.Profile.Name}.");
        }, suspendCapture: false);
}
