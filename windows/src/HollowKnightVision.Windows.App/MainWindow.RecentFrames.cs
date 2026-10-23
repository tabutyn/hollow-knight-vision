using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly RecentCapturedFrameBuffer recentCapturedFrames = new();
    private long recentCaptureGeneration;

    private void RecentFrames_Click(object sender, RoutedEventArgs e)
    {
        if (workspace != "Label" || !LabelPanel.IsEnabled) return;
        try
        {
            var window = new RecentFramesWindow(new LabelingExampleStore(ExamplesRootText.Text).Load(), recentCapturedFrames.Frames,
                key => initialization.Text(key), OpenLabelExample, OpenRecentCapturedFrame, () => SelectWorkspace("Gameplay")) { Owner = this };
            _ = window.ShowDialog();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OpenRecentCapturedFrame(RecentCapturedFrame frame)
    {
        try
        {
            if (!SelectWorkspace("Label", capture: false)) return;
            var state = RecentCapturedFrameLabelingPolicy.StateForOpening(frame,
                StencilInitializationPolicy.NormalizeContext(CurrentContextName), ClassIdentifierText.Text,
                customObjectChoices.Select(item => item.Identifier).ToArray());
            // Match Mac: opening predictions creates a recoverable draft; an
            // empty capture remains unsaved until the user draws a label.
            var saved = RecentCapturedFrameLabelingPolicy.SavePredictedDraft(new LabelingExampleStore(ExamplesRootText.Text),
                frame, state, captureGroupIdentifier, initialization.LanguageIdentifier);
            ApplyLabelSelection(() =>
            {
                CancelLabelGesture();
                currentExample = saved; currentFrame = frame.Image;
                boxes.Clear(); foreach (var rectangle in state.Rectangles) boxes.Add(EditableBox.From(rectangle));
                labelUndo.Clear(); ExamplesList.SelectedItem = null;
                KnownClassesText.Text = string.Join(Environment.NewLine, saved?.Manifest.EffectiveKnownClassIdentifiers ?? []);
                ContextCombo.SelectedValue = ContextDisplayName(state.ContextIdentifier);
                LabelAddButton.IsChecked = true; HardNegativeCheck.IsChecked = false;
                ClassIdentifierText.Text = state.SelectedClassIdentifier;
                LabelImage.Source = FrameBitmap(frame.Image); ResetLabelViewport(); RedrawBoxes();
            });
            if (saved is not null)
            {
                RefreshExamples(); menuStencilRecognizer.Refresh(initialization.LanguageIdentifier); RefreshInitializationStatus();
            }
            SetStatus(saved is null ? "Opened an unsaved captured frame." : $"Opened captured frame with {state.Rectangles.Count} predicted boxes.");
        }
        catch (Exception error) { ShowError(error); }
    }
}
