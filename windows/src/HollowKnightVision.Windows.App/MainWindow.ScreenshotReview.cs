using System.IO;
using System.Windows;
using System.Windows.Input;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private LabelingScreenshotReviewSession? screenshotReview;
    private ScreenshotReviewPanel? screenshotReviewPanel;

    private void RefreshModelReviewSession()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExamplesRootText.Text));
        if (screenshotReview is null || !string.Equals(screenshotReview.RootDirectory, root, StringComparison.OrdinalIgnoreCase))
        {
            screenshotReview = new(new LabelingExampleStore(root));
            screenshotReviewPanel = null;
        }
        screenshotReview.SetExamples(examples);
    }

    private void RefreshScreenshotReview()
    {
        if (screenshotReview is null) RefreshModelReviewSession();
        screenshotReviewPanel ??= new(screenshotReview!, key => initialization.Text(key), OpenScreenshotForRepair,
            example => MessageBox.Show(this,
                initialization.Text("Images, labels, and exemptions will be removed. This cannot be undone."),
                initialization.Text("Delete Screenshot"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes,
            ScreenshotsDeleted);
        screenshotReviewPanel.Refresh(ClassIdentifierText.Text, customObjectChoices.Select(item => item.Identifier), objectReferencePanel?.Atlas);
        if (!ModelScreenshotsPanel.Children.Contains(screenshotReviewPanel))
        { ModelScreenshotsPanel.Children.Clear(); ModelScreenshotsPanel.Children.Add(screenshotReviewPanel); }
    }

    private void OpenScreenshotForRepair(SavedLabelingExample example)
    {
        var valid = LabelingFrameOpeningPolicy.ContextClasses(example.Manifest.ContextIdentifier, customObjectChoices.Select(item => item.Identifier).ToArray());
        var selected = valid.Contains(ClassIdentifierText.Text) ? ClassIdentifierText.Text : valid.FirstOrDefault();
        if (!SelectWorkspace("Label", capture: false)) return;
        try { ApplySavedLabelExample(example, selected); LabelAddButton.IsChecked = true; }
        catch (Exception error) { ShowError(error); }
    }

    private void ScreenshotsDeleted(LabelingScreenshotDeletionResult result)
    {
        try
        {
            if (result.Deleted.Count > 0)
            {
                if (currentExample is not null && result.Deleted.Contains(currentExample.Id))
                {
                    CancelLabelGesture(); currentExample = null; currentFrame = null;
                    boxes.Clear(); labelUndo.Clear(); ExamplesList.SelectedItem = null;
                    KnownClassesText.Clear(); LabelImage.Source = null; RedrawBoxes();
                }
                RefreshExamples();
                menuStencilRecognizer.Refresh(initialization.LanguageIdentifier);
                RefreshInitializationStatus();
            }
            SetStatus(result.Failures.Count > 0 ? $"Delete screenshot failed: {result.Failures.Values.First()}"
                : $"Deleted {result.Deleted.Count} screenshot(s), labels and exemptions. This cannot be undone.");
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ModelTab_Checked(object sender, RoutedEventArgs e)
    { if (IsLoaded) ModelTab_Click(sender, e); }

    private bool HandleModelShortcut(Key key)
    {
        if (workspace != "Model" || SettingsOverlay.Visibility == Visibility.Visible) return false;
        switch (key)
        {
            case Key.D1: case Key.NumPad1: ModelSummaryButton.IsChecked = true; return true;
            case Key.D2: case Key.NumPad2: ModelObjectsButton.IsChecked = true; return true;
            case Key.D3: case Key.NumPad3: ModelScreenshotsButton.IsChecked = true; return true;
            case Key.Up when ModelScreenshotsButton.IsChecked == true: screenshotReviewPanel?.Move(-1); return true;
            case Key.Down when ModelScreenshotsButton.IsChecked == true: screenshotReviewPanel?.Move(1); return true;
            default: return false;
        }
    }
}
