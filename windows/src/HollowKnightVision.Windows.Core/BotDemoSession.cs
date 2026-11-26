using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>Connects the demo's visual transitions to the verified on-disk backup.</summary>
public sealed class BotDemoSession(BotDemoSaveArchive saves, double startedAt)
{
    private readonly BotDemoNavigation navigation = new(startedAt);
    private Task<(BotDemoSaveBackup? Backup, string? Issue)>? archiveTask;
    private string? failure;

    public BotDemoSaveBackup? Backup { get; private set; }
    public string Status => failure ?? navigation.Status;
    public BotDemoPhase Phase => failure is null ? navigation.Phase : BotDemoPhase.Stopped;

    public BotDemoDecision? CheckTimeout(double now)
    {
        if (failure is not null || navigation.CheckTimeout(now) is not { } expired) return null;
        failure = expired.Issue;
        return expired;
    }

    public BotDemoDecision Observe(BotDemoFrame frame, double now)
    {
        if (failure is not null) return new(BotDemoPhase.Stopped, Issue: failure);
        if (!double.IsFinite(now)) return new(navigation.Phase);
        if (CheckTimeout(now) is { } expired) return expired;
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
        if (!frame.ProfilesMeasured || now - frame.CapturedAt is not (>= 0 and <= .5)
            || navigation.Phase is BotDemoPhase.Archiving or BotDemoPhase.Opening or BotDemoPhase.Gameplay)
            return null;
        // The last profile image can outlive an on-disk save change. Use
        // current files before navigation can select or retry the slot.
        try { return saves.HasActiveSave(frame.OccupiedSlots); }
        catch (BotDemoSaveProfileMismatchException) { return null; }
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
