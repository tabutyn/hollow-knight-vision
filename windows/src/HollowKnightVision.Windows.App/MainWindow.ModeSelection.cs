using System.Windows.Controls;
using System.Windows;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private void LabelMode_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) LabelMode_Click(sender, e);
    }

    private void CameraMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string mode } button) return;
        // SelectionItem automation and keyboard navigation can check a radio
        // without a mouse Click. Do not let highlighted and active modes drift.
        if (button.GroupName == "GameplayFraming")
        {
            if (GameplayViewportPolicy.FramingButton(gameplayFraming) != mode) GameplayFraming_Click(sender, e);
        }
        else if (GameplayViewportPolicy.FramingButton(hackerFraming) != mode) HackerMode_Click(sender, e);
    }

    private void CycleAtlasFraming()
    {
        if (workspace == "Hacker")
        {
            hackerFraming = GameplayViewportPolicy.NextTabPreset(hackerFraming);
            ApplyViewport(AtlasCanvas, HackerViewportPanel, hackerFraming, HackerLiveImage, explicitRequest: true);
        }
        else
        {
            gameplayFraming = GameplayViewportPolicy.NextTabPreset(gameplayFraming);
            ApplyViewport(GameplayAtlasCanvas, GameplayPanel, gameplayFraming, GameplayImage, explicitRequest: true);
        }
    }

    // Keep the visual selection tied to actual mode changes, including a pan
    // switching to Free Fly and an atlas reset returning to Current.
    private static void SelectModeButtons(Panel? panel, string selected)
    {
        if (panel is null) return;
        foreach (var button in panel.Children.OfType<RadioButton>())
            button.IsChecked = string.Equals(button.Tag as string, selected, StringComparison.Ordinal);
    }
}
