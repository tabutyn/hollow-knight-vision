namespace HollowKnightVision.Windows.Core;

public enum BotDemoPhase
{
    Navigating, Archiving, SelectingClear, ConfirmingClear, WaitingForEmptySlot,
    StartingSave, LeavingProfile, Opening, Gameplay, Stopped
}

public enum BotDemoSaveAction { None, Archive, VerifyClear }

public readonly record struct BotDemoDecision(BotDemoPhase Phase, InputButtons Buttons = InputButtons.None,
    BotDemoSaveAction SaveAction = BotDemoSaveAction.None, string? Issue = null);

public readonly record struct BotDemoFrame(double CapturedAt, bool FreshGameplay, string? Context,
    string? Selection, double? MenuTimestamp, int CheckedSlots, int OccupiedSlots, InputButtons DirectionToYes)
{
    public bool ProfilesMeasured => Context == "select-profile" && CheckedSlots == 15;
    public bool Slot4Occupied => (OccupiedSlots & 8) != 0;

    public static BotDemoFrame FromVision(double captureTime, double frameTime, bool freshGameplay, MenuStencilResult? menu)
    {
        if (menu is not { IsMatch: true } || !(frameTime - menu.SourceTimestamp is >= 0 and <= .3))
            return new(captureTime, freshGameplay, null, null, null, 0, 0, InputButtons.None);
        var option = menu.SelectedPairAccepted
            ? menu.SelectedOptionIdentifier ?? menu.SelectorSearchRegions.FirstOrDefault(region => region.IsSolved)?.OptionIdentifier
            : null;
        return new(captureTime, freshGameplay, menu.ContextIdentifier, option, menu.SourceTimestamp,
            Mask(menu.CheckedProfileSlots), Mask(menu.OccupiedProfileSlots), YesDirection(menu));
    }

    private static int Mask(IEnumerable<int> slots)
    {
        var mask = 0;
        foreach (var slot in slots)
            if (slot is >= 1 and <= 4) mask |= 1 << (slot - 1);
        return mask;
    }

    private static InputButtons YesDirection(MenuStencilResult menu)
    {
        var yes = menu.Anchors.FirstOrDefault(anchor => anchor.ClassIdentifier == "shared.yes");
        var no = menu.Anchors.FirstOrDefault(anchor => anchor.ClassIdentifier == "shared.no");
        if (yes is null || no is null) return InputButtons.None;
        var dx = yes.Rect.MidX - no.Rect.MidX;
        var dy = yes.Rect.MidY - no.Rect.MidY;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || Math.Abs(dx) + Math.Abs(dy) <= 1) return InputButtons.None;
        return Math.Abs(dy) >= Math.Abs(dx)
            ? dy > 0 ? InputButtons.Down : InputButtons.Up
            : dx < 0 ? InputButtons.Left : InputButtons.Right;
    }
}

public sealed class BotDemoNavigation(double startedAt)
{
    private const string SlotCard = "select-profile.slot-4";
    private const string ClearSlot = "select-profile.clear-save-4";
    private const double PulseInterval = .65;
    private BotDemoPhase phase;
    private bool archiveVerified;
    private readonly double runStartedAt = startedAt;
    private double phaseStartedAt = startedAt;
    private double lastCapture = double.NegativeInfinity;
    private double lastMenu = double.NegativeInfinity;
    private (string? Screen, string? Option, bool Gameplay, int Occupied, int Checked, InputButtons Yes)? identity;
    private double stableSince;
    private double nextActionAt;
    private string? expectedContext;
    private double expectedContextDeadline;
    private Retry? retry;
    private int actions;
    private int startAttempts;
    private double lastStart;
    private bool skipWithAttack = true;
    private double? firstHud;
    private double lastHud;

    private sealed record Retry(InputButtons Button, string? Context, string? Selection, bool Gameplay, double SentAt, int Count);

    public BotDemoPhase Phase => phase;

    public string Status => phase switch
    {
        BotDemoPhase.Navigating => "Finding Select Profile.",
        BotDemoPhase.Archiving => "Archiving slot 4.",
        BotDemoPhase.SelectingClear or BotDemoPhase.ConfirmingClear => "Resetting archived slot 4.",
        BotDemoPhase.WaitingForEmptySlot => "Waiting for empty slot 4.",
        BotDemoPhase.StartingSave or BotDemoPhase.LeavingProfile => "Starting a new save in slot 4.",
        BotDemoPhase.Opening => "Skipping the opening cutscene.",
        BotDemoPhase.Gameplay => "Gameplay confirmed.",
        _ => "Bot stopped."
    };

    public void ArchiveCompleted(bool hadActiveSave, double now)
    {
        if (phase != BotDemoPhase.Archiving) return;
        archiveVerified = true;
        Transition(hadActiveSave ? BotDemoPhase.SelectingClear : BotDemoPhase.StartingSave, now);
    }

    internal BotDemoDecision? CheckTimeout(double now)
    {
        if (phase is BotDemoPhase.Gameplay or BotDemoPhase.Stopped || !double.IsFinite(now)) return null;
        if (now - phaseStartedAt > (phase == BotDemoPhase.Opening ? 300 : 90)
            || phase != BotDemoPhase.Opening && now - runStartedAt > 420)
            return Fail($"Bot timed out. {Status}");
        return null;
    }

    public BotDemoDecision Observe(BotDemoFrame frame, double now, bool? activeSave)
    {
        if (phase is BotDemoPhase.Gameplay or BotDemoPhase.Stopped || !double.IsFinite(now)) return new(phase);
        if (CheckTimeout(now) is { } expired) return expired;
        if (!double.IsFinite(frame.CapturedAt) || frame.CapturedAt < runStartedAt
            || frame.CapturedAt <= lastCapture || now - frame.CapturedAt is < 0 or > .5
            || phase == BotDemoPhase.Archiving) return new(phase);
        lastCapture = frame.CapturedAt;

        if (phase is BotDemoPhase.LeavingProfile or BotDemoPhase.Opening)
            return ObserveOpening(frame, now, activeSave);

        if (frame.MenuTimestamp is { } time)
        {
            if (!double.IsFinite(time) || time <= lastMenu) return new(phase);
            lastMenu = time;
        }
        var observed = (frame.Context, frame.Selection, frame.FreshGameplay, frame.OccupiedSlots,
            frame.CheckedSlots, frame.DirectionToYes);
        if (observed != identity)
        {
            identity = observed;
            stableSince = frame.CapturedAt;
            return new(phase);
        }
        if (frame.CapturedAt - stableSince < .25 || now < nextActionAt) return new(phase);

        if (expectedContext is { } expected)
        {
            if (frame.Context == expected)
            {
                expectedContext = null;
                retry = null;
            }
            else if (now > expectedContextDeadline)
                return Fail("The expected menu did not appear after the bot's last input.");
            else if (retry is { Count: < 3 } previous && now - previous.SentAt >= 1.5
                && frame.Context == previous.Context && frame.Selection == previous.Selection
                && frame.FreshGameplay == previous.Gameplay)
            {
                retry = previous with { SentAt = now, Count = previous.Count + 1 };
                return Pulse(previous.Button, now);
            }
            else return new(phase);
        }

        switch (phase)
        {
            case BotDemoPhase.Navigating:
                return Navigate(frame, now, activeSave);
            case BotDemoPhase.SelectingClear:
                if (archiveVerified && frame.ProfilesMeasured && activeSave == true && frame.Slot4Occupied)
                {
                    if (frame.Selection == SlotCard) return Press(InputButtons.Right, frame, now);
                    if (frame.Selection == ClearSlot)
                    {
                        Transition(BotDemoPhase.ConfirmingClear, now);
                        return Press(InputButtons.ActionZ, frame, now, "clear-save", canRetry: true);
                    }
                    return MoveToSlot(frame, now);
                }
                break;
            case BotDemoPhase.ConfirmingClear:
                if (!archiveVerified || frame.Context != "clear-save" || frame.Selection is null) break;
                if (frame.Selection == "shared.yes")
                {
                    Transition(BotDemoPhase.WaitingForEmptySlot, now);
                    return Press(InputButtons.ActionZ, frame, now, "select-profile") with
                    {
                        SaveAction = BotDemoSaveAction.VerifyClear
                    };
                }
                if (frame.Selection == "shared.no") return Press(frame.DirectionToYes, frame, now);
                break;
            case BotDemoPhase.WaitingForEmptySlot:
                if (frame.ProfilesMeasured && !frame.Slot4Occupied && activeSave == false)
                    Transition(BotDemoPhase.StartingSave, now);
                break;
            case BotDemoPhase.StartingSave:
                if (!archiveVerified || !frame.ProfilesMeasured || activeSave is null) break;
                if (activeSave == true || frame.Slot4Occupied)
                    return Fail("Slot 4 is occupied. Demo stopped before selecting a save.");
                if (frame.Selection != SlotCard) return MoveToSlot(frame, now);
                Transition(BotDemoPhase.LeavingProfile, now);
                startAttempts = 1;
                lastStart = now;
                return Press(InputButtons.ActionZ, frame, now);
        }
        return new(phase);
    }

    private BotDemoDecision Navigate(BotDemoFrame frame, double now, bool? activeSave)
    {
        if (frame.FreshGameplay) return Press(InputButtons.PauseMenu, frame, now, "pause", canRetry: true);
        var selected = frame.Selection;
        switch (frame.Context)
        {
            case "main-title" when selected is not null:
                return selected == "main-title.start-game"
                    ? Press(InputButtons.ActionZ, frame, now, "select-profile", canRetry: true)
                    : Press(InputButtons.Up, frame, now);
            case "select-profile":
                if (selected != SlotCard) return MoveToSlot(frame, now);
                if (!frame.ProfilesMeasured || activeSave is null || activeSave.Value != frame.Slot4Occupied) return new(phase);
                Transition(BotDemoPhase.Archiving, now);
                return new(phase, SaveAction: BotDemoSaveAction.Archive);
            case "pause" when selected is not null:
                return selected is "shared.quit-to-menu" or "pause.quit-to-menu" or "quit-to-menu.quit-to-menu"
                    ? Press(InputButtons.ActionZ, frame, now, "quit-to-menu", canRetry: true)
                    : Press(InputButtons.Down, frame, now);
            case "quit-to-menu" when selected == "shared.yes":
                return Press(InputButtons.ActionZ, frame, now, "main-title", canRetry: true);
            case "quit-to-menu" when selected == "shared.no":
                return Press(frame.DirectionToYes, frame, now);
            case "inventory":
                return Press(InputButtons.Inventory, frame, now);
            case "clear-save" or "quit-game" or "options" or "game-options" or "audio" or "video"
                or "screen-scale" or "brightness" or "video-advanced-settings" or "controller"
                or "remap-controller" or "controller-advanced-settings" or "keyboard" or "mods"
                or "achievements" or "extras" or "credits" or "hidden-dreams" or "grimm-troupe"
                or "lifeblood" or "godmaster":
                return Press(InputButtons.ActionA, frame, now);
            default:
                return new(phase);
        }
    }

    private BotDemoDecision ObserveOpening(BotDemoFrame frame, double now, bool? activeSave)
    {
        if (phase == BotDemoPhase.LeavingProfile)
        {
            if (frame.Context == "select-profile" && !frame.FreshGameplay)
            {
                if (frame.Selection != SlotCard || !frame.ProfilesMeasured || activeSave != false || frame.Slot4Occupied
                    || startAttempts >= 4 || now - lastStart < 2) return new(phase);
                startAttempts++;
                lastStart = now;
                return Press(InputButtons.ActionZ, frame, now);
            }
            if (!frame.FreshGameplay && now < nextActionAt) return new(phase);
            Transition(BotDemoPhase.Opening, now);
        }
        if (frame.FreshGameplay)
        {
            if (firstHud is null || frame.CapturedAt - lastHud > .25) firstHud = frame.CapturedAt;
            lastHud = frame.CapturedAt;
            if (frame.CapturedAt - firstHud >= .25) Transition(BotDemoPhase.Gameplay, now);
            return new(phase);
        }
        firstHud = null;
        if (now < nextActionAt) return new(phase);
        var button = skipWithAttack ? InputButtons.ActionX : InputButtons.ActionZ;
        skipWithAttack = !skipWithAttack;
        nextActionAt = now + PulseInterval;
        return new(phase, button);
    }

    private BotDemoDecision MoveToSlot(BotDemoFrame frame, double now)
    {
        var selected = frame.Selection;
        var button = selected switch
        {
            "shared.back" => InputButtons.Up,
            "select-profile.slot-1" or "select-profile.slot-2" or "select-profile.slot-3" => InputButtons.Down,
            _ when selected?.StartsWith("select-profile.clear-save-", StringComparison.Ordinal) == true => InputButtons.Left,
            _ => InputButtons.None
        };
        return Press(button, frame, now);
    }

    private BotDemoDecision Press(InputButtons button, BotDemoFrame frame, double now,
        string? expecting = null, bool canRetry = false)
    {
        if (button == InputButtons.None) return new(phase);
        expectedContext = expecting;
        expectedContextDeadline = now + 15;
        retry = canRetry ? new(button, frame.Context, frame.Selection, frame.FreshGameplay, now, 0) : null;
        return Pulse(button, now);
    }

    private BotDemoDecision Pulse(InputButtons button, double now)
    {
        if (++actions > 70) return Fail("Bot stopped after too many menu inputs.");
        nextActionAt = now + PulseInterval;
        identity = null;
        return new(phase, button);
    }

    private void Transition(BotDemoPhase next, double now)
    {
        phase = next;
        phaseStartedAt = now;
        identity = null;
        expectedContext = null;
        retry = null;
    }

    private BotDemoDecision Fail(string issue)
    {
        phase = BotDemoPhase.Stopped;
        return new(phase, Issue: issue);
    }
}
