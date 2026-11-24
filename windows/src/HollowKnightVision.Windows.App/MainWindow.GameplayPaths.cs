using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private bool atlasPathsEnabled = true;
    private GameplayPathPresentation? gameplayPathPresentation;
    private readonly AtlasPathFrameEvidence atlasPathEvidence = new();

    private bool ShowsGameplayPaths =>
        workspace == "Gameplay" && visionCancellation is not null && labelCapture.State is not (GamePauseState.Pausing or GamePauseState.Paused or GamePauseState.Resuming)
        && gameplayPathPresentation is { } path
        && atlasPathEvidence.AdmitsGameplay(path.CaptureId, path.CapturedAt, gameMode == GameRuntimeMode.Modded)
        && path.IsCurrent(Volatile.Read(ref atlasPathsEnabled), recognizedMenuContext,
            latestPresentedCaptureId, gameplayFrameRegistered);

    private void RenderGameplayPaths()
    {
        GameplayPathCanvas.Children.Clear();
        GameplayPathCanvas.Width = GameplayAtlasCanvas.Width;
        GameplayPathCanvas.Height = GameplayAtlasCanvas.Height;
        UpdateGameplayPathVisibility();
        if (ShowsGameplayPaths)
            gameplayPathPresentation!.Draw(GameplayPathCanvas, GameplayPathImageBounds());
    }

    private bool SelectGameplayObjective(MouseButtonEventArgs e)
    {
        if (!ShowsGameplayPaths || (Keyboard.Modifiers & ModifierKeys.Shift) != 0
            || gameplayPathPresentation is not { } current) return false;
        AdvanceViewportAnimations();
        var pointer = e.GetPosition(GameplayAtlasCanvas);
        if (current.SelectObjective(new(pointer.X, pointer.Y),
            GameplayPathImageBounds(), GameplayAtlasCanvas.RenderTransform.Value.M11) is not { } updated)
            return false;
        gameplayPathPresentation = updated;
        RenderGameplayPaths();
        e.Handled = true;
        return true;
    }

    private ViewportRect GameplayPathImageBounds() =>
        new(Canvas.GetLeft(GameplayImage), Canvas.GetTop(GameplayImage), GameplayImage.Width, GameplayImage.Height);

    private void UpdateGameplayPathVisibility()
    {
        GameplayPathCanvas.Visibility = ShowsGameplayPaths ? Visibility.Visible : Visibility.Collapsed;
        RenderGameplayPlacement();
        RenderGameplayWallContacts();
    }

    private void ResetGameplayPaths()
    {
        gameplayPathPresentation = null;
        gameplayWallContactScope = null;
        drawnGameplayWallContacts = null;
        BotWallContactPresentation.Clear(GameplayWallContactsCanvas);
        atlasPathEvidence.ResetGameplayPreview();
        GameplayPathCanvas.Children.Clear();
        GameplayPathCanvas.Visibility = Visibility.Collapsed;
    }
}
