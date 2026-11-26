using System.IO;
using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private void InitializeAtlasDebugPreferences(string root)
    {
        var saved = AtlasDebugPreferences.Load(root);
        AtlasPathsCheck.IsChecked = saved.AtlasPaths;
        EstimatedRoomOutlinesCheck.IsChecked = saved.EstimatedRoomOutlines;
        GroundTrackingPatchesCheck.IsChecked = saved.GroundTrackingPatches;

        // Attach after loading so restoring the defaults never writes a preference file.
        foreach (var toggle in new[] { AtlasPathsCheck, EstimatedRoomOutlinesCheck, GroundTrackingPatchesCheck })
        {
            toggle.Checked += Save;
            toggle.Unchecked += Save;
        }

        void Save(object sender, RoutedEventArgs args)
        {
            var preferences = new AtlasDebugPreferences
            {
                AtlasPaths = AtlasPathsCheck.IsChecked == true,
                EstimatedRoomOutlines = EstimatedRoomOutlinesCheck.IsChecked == true,
                GroundTrackingPatches = GroundTrackingPatchesCheck.IsChecked == true,
            };
            try { preferences.Save(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Atlas debug settings could not be saved. {error.Message}");
            }
        }
    }
}
