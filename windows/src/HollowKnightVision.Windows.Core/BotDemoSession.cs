using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>Connects the demo's visual transitions to the verified on-disk backup.</summary>
public sealed class BotDemoSession(BotDemoSaveArchive saves, double startedAt)
{
    private readonly BotDemoNavigation navigation = new(startedAt);
    private Task<(BotDemoSaveBackup? Backup, string? Issue)>? archiveTask;
    private string? failure;
    private double lastProfileCheck = double.NegativeInfinity;
    private int? checkedOccupancy;
    private bool? activeSave;

    public BotDemoSaveBackup? Backup { get; private set; }
    public string Status => failure ?? navigation.Status;

    public BotDemoDecision Observe(BotDemoFrame frame, double now)
    {
        if (failure is not null) return new(BotDemoPhase.Stopped, Issue: failure);
        try
        {
            if (archiveTask is { IsCompleted: true } finished)
            {
                archiveTask = null;
                var result = finished.GetAwaiter().GetResult();
                if (result.Backup is not { } verified) return Stop(result.Issue ?? "The slot 4 archive failed.");
                Backup = verified;
                navigation.ArchiveCompleted(verified.HadActiveSave, now);
            }
            var save = ObserveOccupancy(frame, now);
            var decision = navigation.Observe(frame, now, save);
            if (decision.Phase == BotDemoPhase.Stopped) return decision;
            if (decision.SaveAction == BotDemoSaveAction.Archive)
                archiveTask = Task.Run(() => Archive(frame.OccupiedSlots));
            if (decision.SaveAction == BotDemoSaveAction.VerifyClear)
            {
                if (Backup is not { HadActiveSave: true } backup) return Stop("No verified slot 4 archive.");
                saves.VerifyBeforeClear(backup);
            }
            return decision;
        }
        catch (Exception error) when (IsArchiveError(error))
        {
            return Stop($"Bot save check failed. {error.Message}");
        }
    }

    private bool? ObserveOccupancy(BotDemoFrame frame, double now)
    {
        if (!frame.ProfilesMeasured || now - frame.CapturedAt is < 0 or > .5
            || navigation.Phase is BotDemoPhase.Archiving or BotDemoPhase.Opening or BotDemoPhase.Gameplay)
            return null;
        if (checkedOccupancy == frame.OccupiedSlots && now - lastProfileCheck < .2) return activeSave;
        lastProfileCheck = now;
        checkedOccupancy = frame.OccupiedSlots;
        try { activeSave = saves.HasActiveSave(frame.OccupiedSlots); }
        catch (BotDemoSaveProfileMismatchException) { activeSave = null; }
        return activeSave;
    }

    private (BotDemoSaveBackup? Backup, string? Issue) Archive(int occupiedSlots)
    {
        try { return (saves.Create(occupiedSlots), null); }
        catch (Exception error) when (IsArchiveError(error)) { return (null, error.Message); }
    }

    private BotDemoDecision Stop(string issue)
    {
        failure = issue;
        return new(BotDemoPhase.Stopped, Issue: issue);
    }

    private static bool IsArchiveError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException;
}
