namespace HollowKnightVision.Windows.Core;

public enum GameStartupScreen
{
    Title,
    ProfileOne
}

public enum GameStartupAction
{
    MoveSelectionUp,
    SelectStartGame,
    SelectProfileOne
}

public enum GameStartupPhase
{
    AwaitingTitle,
    AwaitingProfile,
    AwaitingGameplay,
    Gameplay
}

public sealed record GameStartupEvidence(
    GameStartupScreen? Screen,
    bool GameplayLikely,
    string? SelectedOption = null)
{
    public static readonly GameStartupEvidence Unknown = new(null, false);
}

public sealed record GameStartupDecision(GameStartupPhase Phase, GameStartupAction? Action)
{
    public bool AdmitsWorldFrames => Phase == GameStartupPhase.Gameplay;
}

/// <summary>
/// One-shot startup navigation state machine. Menu actions and the world-write
/// gate require consecutive matching observations, matching the macOS runner.
/// </summary>
public sealed class GameStartupCoordinator
{
    private int stableTitleFrames;
    private int stableProfileFrames;
    private int stableGameplayFrames;
    private int navigationCooldownFrames;

    public GameStartupCoordinator(int requiredStableFrames = 8)
    {
        RequiredStableFrames = Math.Max(1, requiredStableFrames);
    }

    public GameStartupPhase Phase { get; private set; } = GameStartupPhase.AwaitingTitle;
    public int RequiredStableFrames { get; }

    public void Reset(bool preservingGameplay = false)
    {
        if (preservingGameplay && Phase == GameStartupPhase.Gameplay)
        {
            stableTitleFrames = 0;
            stableProfileFrames = 0;
            stableGameplayFrames = 0;
            return;
        }

        Phase = GameStartupPhase.AwaitingTitle;
        stableTitleFrames = 0;
        stableProfileFrames = 0;
        stableGameplayFrames = 0;
        navigationCooldownFrames = 0;
    }

    public void RestoreGameplaySession()
    {
        Phase = GameStartupPhase.Gameplay;
        stableTitleFrames = 0;
        stableProfileFrames = 0;
        stableGameplayFrames = 0;
    }

    public GameStartupDecision Observe(GameStartupEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (Phase == GameStartupPhase.Gameplay)
        {
            switch (evidence.Screen)
            {
                case GameStartupScreen.Title:
                    Phase = GameStartupPhase.AwaitingTitle;
                    break;
                case GameStartupScreen.ProfileOne:
                    Phase = GameStartupPhase.AwaitingProfile;
                    break;
                default:
                    return new GameStartupDecision(GameStartupPhase.Gameplay, null);
            }
            stableTitleFrames = 0;
            stableProfileFrames = 0;
            stableGameplayFrames = 0;
        }

        if (evidence.GameplayLikely)
        {
            stableGameplayFrames++;
            stableTitleFrames = 0;
            stableProfileFrames = 0;
            if (stableGameplayFrames >= RequiredStableFrames)
            {
                Phase = GameStartupPhase.Gameplay;
            }
            return new GameStartupDecision(Phase, null);
        }
        stableGameplayFrames = 0;

        if (evidence.Screen == GameStartupScreen.Title
            && Phase is GameStartupPhase.AwaitingTitle
                or GameStartupPhase.AwaitingProfile
                or GameStartupPhase.AwaitingGameplay)
        {
            if (Phase != GameStartupPhase.AwaitingTitle)
            {
                Phase = GameStartupPhase.AwaitingTitle;
                stableTitleFrames = 0;
            }
            if (navigationCooldownFrames > 0)
            {
                navigationCooldownFrames--;
                stableTitleFrames = 0;
                return new GameStartupDecision(Phase, null);
            }
            stableTitleFrames++;
            stableProfileFrames = 0;
            if (stableTitleFrames >= RequiredStableFrames)
            {
                if (evidence.SelectedOption is null)
                {
                    stableTitleFrames = 0;
                }
                else if (!evidence.SelectedOption.Equals(
                             "Start Game", StringComparison.OrdinalIgnoreCase))
                {
                    stableTitleFrames = 0;
                    navigationCooldownFrames = Math.Max(16, RequiredStableFrames * 4);
                    return new GameStartupDecision(Phase, GameStartupAction.MoveSelectionUp);
                }
                else
                {
                    Phase = GameStartupPhase.AwaitingProfile;
                    return new GameStartupDecision(Phase, GameStartupAction.SelectStartGame);
                }
            }
        }
        else if (evidence.Screen == GameStartupScreen.ProfileOne
                 && Phase is GameStartupPhase.AwaitingTitle or GameStartupPhase.AwaitingProfile)
        {
            if (navigationCooldownFrames > 0)
            {
                navigationCooldownFrames--;
                stableProfileFrames = 0;
                return new GameStartupDecision(Phase, null);
            }
            stableProfileFrames++;
            stableTitleFrames = 0;
            if (stableProfileFrames >= RequiredStableFrames)
            {
                if (evidence.SelectedOption is null)
                {
                    stableProfileFrames = 0;
                }
                else if (!evidence.SelectedOption.Equals("1.", StringComparison.OrdinalIgnoreCase))
                {
                    stableProfileFrames = 0;
                    navigationCooldownFrames = Math.Max(16, RequiredStableFrames * 4);
                    return new GameStartupDecision(Phase, GameStartupAction.MoveSelectionUp);
                }
                else
                {
                    Phase = GameStartupPhase.AwaitingGameplay;
                    return new GameStartupDecision(Phase, GameStartupAction.SelectProfileOne);
                }
            }
        }
        else if (evidence.Screen is null)
        {
            stableTitleFrames = 0;
            stableProfileFrames = 0;
        }

        return new GameStartupDecision(Phase, null);
    }

    public void Retry(GameStartupAction action)
    {
        switch (action)
        {
            case GameStartupAction.MoveSelectionUp:
                navigationCooldownFrames = 0;
                if (Phase == GameStartupPhase.AwaitingTitle) stableTitleFrames = 0;
                else if (Phase == GameStartupPhase.AwaitingProfile) stableProfileFrames = 0;
                break;
            case GameStartupAction.SelectStartGame:
                Phase = GameStartupPhase.AwaitingTitle;
                stableTitleFrames = 0;
                break;
            case GameStartupAction.SelectProfileOne:
                Phase = GameStartupPhase.AwaitingProfile;
                stableProfileFrames = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}

/// <summary>
/// CPU-only startup recognizer. Coordinates come from the bundled macOS
/// menu-stencil calibration at 640x360. It validates neutral bright UI strokes
/// in both selector decorations and multiple menu anchors before naming a row.
/// </summary>
public sealed class GameStartupDetector
{
    private readonly record struct Rect(double X, double Y, double Width, double Height);
    private readonly record struct Selection(string Name, Rect Left, Rect Right);

    private static readonly Rect[] TitleAnchors =
    [
        new(289, 209, 61, 9), new(297, 231, 45, 8), new(283, 254, 75, 7),
        new(303, 274, 35, 8), new(291, 296, 57, 10)
    ];

    private static readonly Selection[] TitleSelections =
    [
        new("Start Game", new(267, 201, 14, 11), new(359, 202, 13, 11)),
        new("Options", new(276, 224, 12, 10), new(351, 223, 14, 11)),
        new("Achievements", new(260, 246, 13, 9), new(368, 246, 11, 9)),
        new("Extras", new(282.5, 267.5, 13, 10), new(346.5, 267.5, 13, 11)),
        new("Quit Game", new(269, 288, 14, 11), new(356, 287, 15, 13))
    ];

    private static readonly Rect[] ProfileAnchors =
    [
        new(252, 26, 139, 15), new(101, 102, 13, 14), new(100, 156, 14, 14),
        new(100, 209, 14, 15), new(92, 260, 22, 17), new(309, 304, 27, 8)
    ];

    private static readonly Selection[] ProfileSelections =
    [
        new("1.", new(57, 100, 15, 13), new(434, 100, 15, 14)),
        new("2.", new(57, 154, 15, 13), new(434, 154, 16, 13)),
        new("3.", new(57, 207, 15, 13), new(434, 207, 15, 14)),
        new("4.", new(57, 262, 15, 11), new(433, 260, 17, 14)),
        new("Back", new(282, 299, 16, 14), new(346, 299, 15, 14))
    ];

    public GameStartupEvidence Detect(BgraFrame frame, bool gameplayIsLatched = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var menu = DetectMenu(frame);
        if (gameplayIsLatched) return menu ?? GameStartupEvidence.Unknown;

        if (HasBrightContent(frame, new Rect(0.045 * 640, 0.035 * 360, 0.085 * 640, 0.18 * 360), 8)
            && HasBrightContent(frame, new Rect(0.115 * 640, 0.045 * 360, 0.16 * 640, 0.11 * 360), 8))
        {
            return new GameStartupEvidence(null, true);
        }
        return menu ?? GameStartupEvidence.Unknown;
    }

    private static GameStartupEvidence? DetectMenu(BgraFrame frame)
    {
        var titleAnchors = TitleAnchors.Count(rect => HasBrightContent(frame, rect, 4));
        if (titleAnchors >= 3 && TrySelected(frame, TitleSelections, out var titleSelection))
        {
            return new GameStartupEvidence(GameStartupScreen.Title, false, titleSelection);
        }

        var profileAnchors = ProfileAnchors.Count(rect => HasBrightContent(frame, rect, 3));
        if (profileAnchors >= 3 && TrySelected(frame, ProfileSelections, out var profileSelection))
        {
            return new GameStartupEvidence(GameStartupScreen.ProfileOne, false, profileSelection);
        }
        return null;
    }

    private static bool TrySelected(
        BgraFrame frame,
        IReadOnlyList<Selection> selections,
        out string? selected)
    {
        Selection? best = null;
        var bestScore = 0;
        foreach (var candidate in selections)
        {
            var left = BrightPixelCount(frame, candidate.Left);
            var right = BrightPixelCount(frame, candidate.Right);
            var score = Math.Min(left, right);
            if (score >= 3 && score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        selected = best?.Name;
        return selected is not null;
    }

    private static bool HasBrightContent(BgraFrame frame, Rect rect, int minimumPixels) =>
        BrightPixelCount(frame, rect) >= minimumPixels;

    private static int BrightPixelCount(BgraFrame frame, Rect referenceRect)
    {
        var x0 = Math.Clamp((int)Math.Floor(referenceRect.X * frame.Width / 640.0), 0, frame.Width);
        var x1 = Math.Clamp((int)Math.Ceiling(
            (referenceRect.X + referenceRect.Width) * frame.Width / 640.0), 0, frame.Width);
        var y0 = Math.Clamp((int)Math.Floor(referenceRect.Y * frame.Height / 360.0), 0, frame.Height);
        var y1 = Math.Clamp((int)Math.Ceiling(
            (referenceRect.Y + referenceRect.Height) * frame.Height / 360.0), 0, frame.Height);
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = frame.Row(y);
            for (var x = x0; x < x1; x++)
            {
                var offset = x * BgraFrame.BytesPerPixel;
                var blue = row[offset];
                var green = row[offset + 1];
                var red = row[offset + 2];
                var minimum = Math.Min(red, Math.Min(green, blue));
                var maximum = Math.Max(red, Math.Max(green, blue));
                if (minimum >= 145 && maximum - minimum <= 72) count++;
            }
        }
        return count;
    }
}
