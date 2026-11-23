using System.Windows;
using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private bool atlasPathsEnabled = true;
    private GameplayPathPresentation? gameplayPathPresentation;

    private bool ShowsGameplayPaths =>
        workspace == "Gameplay" && visionCancellation is not null && labelCapture.State is not (GamePauseState.Pausing or GamePauseState.Paused or GamePauseState.Resuming)
        && gameplayPathPresentation?.IsCurrent(Volatile.Read(ref atlasPathsEnabled), recognizedMenuContext,
            latestPresentedCaptureId, gameplayFrameRegistered) == true;

    private void RenderGameplayPaths()
    {
        GameplayPathCanvas.Children.Clear();
        GameplayPathCanvas.Width = GameplayAtlasCanvas.Width;
        GameplayPathCanvas.Height = GameplayAtlasCanvas.Height;
        UpdateGameplayPathVisibility();
        if (ShowsGameplayPaths)
            gameplayPathPresentation!.Draw(GameplayPathCanvas,
                new(Canvas.GetLeft(GameplayImage), Canvas.GetTop(GameplayImage), GameplayImage.Width, GameplayImage.Height));
    }

    private void UpdateGameplayPathVisibility() =>
        GameplayPathCanvas.Visibility = ShowsGameplayPaths ? Visibility.Visible : Visibility.Collapsed;

    private void ResetGameplayPaths()
    {
        gameplayPathPresentation = null;
        GameplayPathCanvas.Children.Clear();
        GameplayPathCanvas.Visibility = Visibility.Collapsed;
    }
}
