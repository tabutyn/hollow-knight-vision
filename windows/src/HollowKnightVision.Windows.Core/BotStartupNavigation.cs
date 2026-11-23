namespace HollowKnightVision.Windows.Core;

public enum BotStartupPhase { Menu, LeavingProfile, Opening, Gameplay, Stopped }
public readonly record struct BotStartupDecision(BotStartupPhase Phase, InputButtons Buttons, string? Issue = null);

/// <summary>Controls only the supported startup menus and the opening cutscene.</summary>
public sealed class BotStartupNavigation(double startedAt)
{
    private const double ActionInterval = .65;
    private BotStartupPhase phase;
    private (string Context, string? Option)? lastMenu;
    private double stableMenuSince;
    private double nextActionAt;
    private double pressedProfileAt;
    private string? pressedSlot;
    private int profileAttempts;
    private double lastFrameAt = double.NegativeInfinity;
    private double? firstHud;
    private double lastHudAt;
    private bool skipWithAttack = true;

    public BotStartupDecision Observe(double captureTime, double now, bool freshGameplay, bool receiverGameplay,
        string? context, string? option)
    {
        if (phase is BotStartupPhase.Gameplay or BotStartupPhase.Stopped)
            return new(phase, InputButtons.None);
        if (now - startedAt > 100)
        {
            phase = BotStartupPhase.Stopped;
            return new(phase, InputButtons.None, "Bot startup timed out.");
        }
        if (now - captureTime is < 0 or > BotRecordingContract.MaximumCaptureAge
            || !double.IsFinite(now) || !double.IsFinite(captureTime) || captureTime < startedAt || captureTime <= lastFrameAt)
            return new(phase, InputButtons.None);
        lastFrameAt = captureTime;

        if (freshGameplay)
        {
            if (firstHud is null || captureTime - lastHudAt > .25) firstHud = captureTime;
            lastHudAt = captureTime;
            if (captureTime - firstHud >= .25) phase = BotStartupPhase.Gameplay;
            return new(phase, InputButtons.None);
        }
        firstHud = null;
        if (receiverGameplay) return new(phase, InputButtons.None);

        if (phase == BotStartupPhase.LeavingProfile)
        {
            if (context == "select-profile")
            {
                if (now - pressedProfileAt < 2 || option != pressedSlot || now < nextActionAt || profileAttempts >= 4)
                    return new(phase, InputButtons.None);
                profileAttempts++;
                pressedProfileAt = now;
                nextActionAt = now + ActionInterval;
                return new(phase, InputButtons.ActionZ);
            }
            if (now - pressedProfileAt < ActionInterval) return new(phase, InputButtons.None);
            phase = BotStartupPhase.Opening;
        }

        if (phase == BotStartupPhase.Opening)
        {
            if (now < nextActionAt) return new(phase, InputButtons.None);
            nextActionAt = now + ActionInterval;
            var button = skipWithAttack ? InputButtons.ActionX : InputButtons.ActionZ;
            skipWithAttack = !skipWithAttack;
            return new(phase, button);
        }

        if (context is null)
        {
            lastMenu = null;
            return new(phase, InputButtons.None);
        }
        var identity = (Context: context, Option: option);
        if (identity != lastMenu)
        {
            lastMenu = identity;
            stableMenuSince = captureTime;
            return new(phase, InputButtons.None);
        }
        if (captureTime - stableMenuSince < .18 || now < nextActionAt) return new(phase, InputButtons.None);
        var selected = (context, option) switch
        {
            ("main-title", "main-title.start-game") => InputButtons.ActionZ,
            ("main-title", not null) => InputButtons.Up,
            ("select-profile", _) when ProfileSlot(option) => InputButtons.ActionZ,
            ("clear-save" or "quit-game" or "quit-to-menu" or "options" or "brightness", _) => InputButtons.ActionA,
            _ => InputButtons.None
        };
        if (selected == InputButtons.None) return new(phase, selected);

        nextActionAt = now + ActionInterval;
        if (context == "select-profile")
        {
            pressedProfileAt = now;
            pressedSlot = option;
            profileAttempts = 1;
            phase = BotStartupPhase.LeavingProfile;
        }
        return new(phase, selected);
    }

    private static bool ProfileSlot(string? selected) => selected is
        "select-profile.slot-1" or "select-profile.slot-2" or "select-profile.slot-3" or "select-profile.slot-4";
}
