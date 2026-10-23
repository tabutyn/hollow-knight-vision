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
/// CPU-only startup recognizer. Stable menu identity and selection come from
/// the shared menu stencil result; pixels are retained only for the startup HUD
/// fallback and the coarse profile-one geometry used before stencils settle.
/// </summary>
public sealed class GameStartupDetector
{
    private const int SampleWidth = 320;

    private readonly record struct Rect(double X, double Y, double Width, double Height)
    {
        public double MidY => Y + Height / 2;
    }

    public GameStartupEvidence Detect(
        BgraFrame frame,
        MenuStencilResult? menuStencil = null,
        HudStencilResult? hudStencil = null,
        bool gameplayIsLatched = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GameStartupEvidence? MenuEvidence()
        {
            if (menuStencil?.IsMatch != true) return null;
            return menuStencil.ContextIdentifier switch
            {
                "main-title" => new GameStartupEvidence(
                    GameStartupScreen.Title,
                    GameplayLikely: false,
                    menuStencil.SelectedOption),
                "select-profile" => new GameStartupEvidence(
                    GameStartupScreen.ProfileOne,
                    GameplayLikely: false,
                    menuStencil.SelectedOption),
                _ => GameStartupEvidence.Unknown
            };
        }

        if (gameplayIsLatched)
        {
            return MenuEvidence() ?? GameStartupEvidence.Unknown;
        }

        var sampleHeight = Math.Max(
            1,
            (int)Math.Round(
                frame.Height * (double)SampleWidth / Math.Max(1, frame.Width),
                MidpointRounding.AwayFromZero));
        var mana = BrightBounds(
            frame,
            sampleHeight,
            new Rect(0.045, 0.035, 0.085, 0.18),
            minimumPixels: 8);
        var health = BrightBounds(
            frame,
            sampleHeight,
            new Rect(0.115, 0.045, 0.16, 0.11),
            minimumPixels: 8);
        if (hudStencil?.EstablishesGameplay == true || mana is not null && health is not null)
        {
            return new GameStartupEvidence(null, GameplayLikely: true);
        }

        if (MenuEvidence() is { } menu) return menu;
        return ProfileEvidence(frame, sampleHeight) ?? GameStartupEvidence.Unknown;
    }

    private static GameStartupEvidence? ProfileEvidence(
        BgraFrame frame,
        int sampleHeight)
    {
        var left = BrightBounds(
            frame,
            sampleHeight,
            new Rect(0.075, 0.245, 0.075, 0.13),
            minimumPixels: 5);
        var numberOne = BrightBounds(
            frame,
            sampleHeight,
            new Rect(0.145, 0.245, 0.08, 0.13),
            minimumPixels: 7);
        var right = BrightBounds(
            frame,
            sampleHeight,
            new Rect(0.64, 0.245, 0.09, 0.13),
            minimumPixels: 5);
        if (left is null || numberOne is null || right is null)
        {
            return null;
        }

        var minimumMidY = Math.Min(left.Value.MidY, Math.Min(numberOne.Value.MidY, right.Value.MidY));
        var maximumMidY = Math.Max(left.Value.MidY, Math.Max(numberOne.Value.MidY, right.Value.MidY));
        return maximumMidY - minimumMidY <= sampleHeight * 0.055
            ? new GameStartupEvidence(GameStartupScreen.ProfileOne, GameplayLikely: false)
            : null;
    }

    private static Rect? BrightBounds(
        BgraFrame frame,
        int sampleHeight,
        Rect normalizedRoi,
        int minimumPixels)
    {
        var x0 = Math.Clamp((int)Math.Floor(normalizedRoi.X * SampleWidth), 0, SampleWidth);
        var x1 = Math.Clamp(
            (int)Math.Ceiling((normalizedRoi.X + normalizedRoi.Width) * SampleWidth),
            0,
            SampleWidth);
        var y0 = Math.Clamp((int)Math.Floor(normalizedRoi.Y * sampleHeight), 0, sampleHeight);
        var y1 = Math.Clamp(
            (int)Math.Ceiling((normalizedRoi.Y + normalizedRoi.Height) * sampleHeight),
            0,
            sampleHeight);
        if (x0 >= x1 || y0 >= y1) return null;

        var mask = new bool[SampleWidth * sampleHeight];
        for (var y = y0; y < y1; y++)
        {
            var sourceY = Math.Min(
                frame.Height - 1,
                (int)((long)y * frame.Height / sampleHeight));
            var row = frame.Row(sourceY);
            for (var x = x0; x < x1; x++)
            {
                var sourceX = Math.Min(
                    frame.Width - 1,
                    (int)((long)x * frame.Width / SampleWidth));
                var offset = sourceX * BgraFrame.BytesPerPixel;
                var blue = row[offset];
                var green = row[offset + 1];
                var red = row[offset + 2];
                var minimum = Math.Min(red, Math.Min(green, blue));
                var maximum = Math.Max(red, Math.Max(green, blue));
                if (minimum >= 145 && maximum - minimum <= 72)
                {
                    mask[y * SampleWidth + x] = true;
                }
            }
        }

        var visited = new bool[mask.Length];
        var retained = new List<(int X, int Y)>();
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var start = y * SampleWidth + x;
                if (!mask[start] || visited[start]) continue;
                var queue = new List<(int X, int Y)> { (x, y) };
                var component = new List<(int X, int Y)>();
                visited[start] = true;
                for (var cursor = 0; cursor < queue.Count; cursor++)
                {
                    var point = queue[cursor];
                    component.Add(point);
                    for (var neighborY = Math.Max(y0, point.Y - 1);
                         neighborY <= Math.Min(y1 - 1, point.Y + 1);
                         neighborY++)
                    {
                        for (var neighborX = Math.Max(x0, point.X - 1);
                             neighborX <= Math.Min(x1 - 1, point.X + 1);
                             neighborX++)
                        {
                            var index = neighborY * SampleWidth + neighborX;
                            if (!mask[index] || visited[index]) continue;
                            visited[index] = true;
                            queue.Add((neighborX, neighborY));
                        }
                    }
                }

                var lowY = component.Min(point => point.Y);
                var highY = component.Max(point => point.Y);
                if (component.Count >= 2 && highY > lowY)
                {
                    retained.AddRange(component);
                }
            }
        }

        var regionWidth = x1 - x0;
        var regionHeight = y1 - y0;
        var regionArea = checked(regionWidth * regionHeight);
        if (retained.Count < minimumPixels
            || retained.Count > regionArea * 0.6)
        {
            return null;
        }
        var lowX = retained.Min(point => point.X);
        var highX = retained.Max(point => point.X);
        var retainedLowY = retained.Min(point => point.Y);
        var retainedHighY = retained.Max(point => point.Y);
        var retainedWidth = highX - lowX + 1;
        var retainedHeight = retainedHighY - retainedLowY + 1;
        if (retainedWidth >= regionWidth * 0.9
            && retainedHeight >= regionHeight * 0.9
            && retained.Count > regionArea * 0.25)
        {
            return null;
        }
        return new Rect(
            lowX,
            retainedLowY,
            retainedWidth,
            retainedHeight);
    }
}
