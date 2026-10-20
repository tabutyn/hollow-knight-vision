namespace HollowKnightVision.Windows.App;

public enum GameRuntimeMode
{
    Normal,
    Modded
}

internal static class GameRuntimeModeLaunchPolicy
{
    internal static bool RequiresChoice(GameRuntimeMode? argumentMode) => argumentMode is null;
}

internal static class GameplayOutputGate
{
    internal static bool CanShowRestoredAtlas(
        bool hasFreshGameFrame,
        bool explicitlyRequested) => hasFreshGameFrame || explicitlyRequested;

    internal static bool UsesAtlasCoordinates(string? recognizedMenuContext) =>
        string.Equals(recognizedMenuContext, "game", StringComparison.Ordinal);
}

internal static class GameLossCountdown
{
    internal const int InitialSeconds = 5;

    internal static string Message(int remainingSeconds) =>
        $"Hollow Knight lost. Closing app in {Math.Max(0, remainingSeconds)} seconds";
}
