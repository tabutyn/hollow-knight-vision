using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private (long CaptureId, double CapturedAt)? gameplayPlacementFrame;
    private (LiveWorldPlacementProjection Placement, GroundFeatureOverlayPresentation.AtlasProjection Projection, bool Outlines)?
        drawnGameplayPlacement;

    private void EstimatedRoomOutlines_Changed(object sender, RoutedEventArgs e) => RenderGameplayPlacement();

    private void RenderGameplayPlacement()
    {
        if (GameplayPlacementCanvas is null || EstimatedRoomOutlinesCheck is null) return;
        var visible = workspace == "Gameplay" && gameplayTileImages.Count > 0
            && (!hasFreshGameFrame || (gameplayFrameRegistered && gameplayPlacementFrame is { } frame
                && labelCapture.State is not (GamePauseState.Pausing or GamePauseState.Paused or GamePauseState.Resuming)
                && atlasPathEvidence.AdmitsGameplay(frame.CaptureId, frame.CapturedAt, gameMode == GameRuntimeMode.Modded)));
        GameplayPlacementCanvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        var display = (gameplaySavedWorld.Placement, Projection: SavedGroundProjection, Outlines: EstimatedRoomOutlinesCheck.IsChecked == true);
        if (drawnGameplayPlacement == display) return;
        GameplayPlacementPresentation.Draw(GameplayPlacementCanvas, display.Placement, display.Projection, display.Outlines);
        drawnGameplayPlacement = display;
    }

    private void ResetGameplayPlacement()
    {
        gameplayPlacementFrame = null;
        drawnGameplayPlacement = null;
        GameplayPlacementCanvas.Children.Clear();
        GameplayPlacementCanvas.Visibility = Visibility.Collapsed;
    }
}
