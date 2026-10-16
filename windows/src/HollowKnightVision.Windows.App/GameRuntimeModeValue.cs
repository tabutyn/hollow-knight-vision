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
