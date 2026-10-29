using System.Windows;
using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly LabelCaptureSession labelCapture = new();
    private bool prepareLabelWhenPaused;

    private static void PresentLabelPauseControls(FrameworkElement editor, Control labelButton,
        LabelCaptureSession session, bool recording)
    {
        editor.IsEnabled = session.State == GamePauseState.Paused && session.FrozenFrame is not null;
        labelButton.IsEnabled = session.CanToggle && !recording;
        labelButton.ToolTip = recording ? "Stop path recording before labeling."
            : session.State == GamePauseState.Unavailable ? "Waiting for compatible pause controls and game capture."
            : session.State == GamePauseState.Pausing ? "Waiting for Hollow Knight to confirm its pause."
            : session.State == GamePauseState.Resuming ? "Waiting for Hollow Knight to resume."
            : !session.CanToggle ? "Waiting for a clean game frame." : null;
    }

    private void SyncLabelPauseState()
    {
        if (gameMode != GameRuntimeMode.Modded) return;
        // Replay owns the game pause, but still needs live rendered frames for
        // preparation and scoring. Only a labeling pause freezes capture.
        ApplyLabelPauseState(visionCancellation is null ? GamePauseState.Unavailable
            : Volatile.Read(ref stepCapture) is not null ? GamePauseState.Ready : gameControls.PauseState);
    }

    private void BeginLabelSession(bool capture)
    {
        prepareLabelWhenPaused = capture;
        LabelAddButton.IsChecked = true;
        LabelPanel.IsEnabled = false;
        if (capture) LabelImage.Source = null;
        if (gameMode == GameRuntimeMode.Normal) ApplyLabelPauseState(GamePauseState.Paused);
        else
        {
            _ = gameControls.SetLabelPauseActive(true);
            SyncLabelPauseState();
        }
    }

    private void EndLabelSession()
    {
        prepareLabelWhenPaused = false;
        if (gameMode == GameRuntimeMode.Normal) ApplyLabelPauseState(GamePauseState.Ready);
        else { _ = gameControls.SetLabelPauseActive(false); SyncLabelPauseState(); }
    }

    private void ApplyLabelPauseState(GamePauseState state)
    {
        if (labelCapture.State == state) return;
        var completed = labelCapture.Apply(state);
        menuStencilRecognizer.SetObjectInferenceSuspended(labelCapture.SuspendsInference);
        PresentLabelPauseControls(LabelPanel, LabelWorkspaceButton, labelCapture, gameControls.IsRecording);
        if (state == GamePauseState.Paused && prepareLabelWhenPaused && labelCapture.FrozenFrame is not null)
        { prepareLabelWhenPaused = false; PreparePausedLabelEditor(); }
        else if (state is GamePauseState.Pausing or GamePauseState.Resuming)
            SetStatus(state == GamePauseState.Pausing ? "Pausing Hollow Knight for labeling…" : "Resuming Hollow Knight…");
        else if (workspace == "Label" && state is GamePauseState.Ready or GamePauseState.Unavailable)
        {
            prepareLabelWhenPaused = false;
            _ = SelectWorkspace("Gameplay", capture: false);
            SetStatus(state == GamePauseState.Unavailable
                ? "Label pause unavailable. Waiting for compatible receiver controls."
                : "Hollow Knight did not confirm the label pause. Try Label again.");
        }
        if (completed && !isClosing && OpsRandomizeAfterLabelCheck.IsChecked == true)
            OpsRandomize_Click(this, new RoutedEventArgs());
    }

    private void PreparePausedLabelEditor()
    {
        ApplyLabelSelection(() =>
        {
            initialization.Refresh();
            var context = initialization.LabelContextForCapture(recognizedMenuContext,
                initializationTargetContext, StencilInitializationPolicy.NormalizeContext(CurrentContextName));
            CancelLabelGesture();
            LabelAddButton.IsChecked = true;
            HardNegativeCheck.IsChecked = false;
            ContextCombo.SelectedValue = ContextDisplayName(context);
            var saved = context == "game" ? null : initialization.ExampleFor(context);
            if (saved is not null) OpenLabelExample(saved);
            else CaptureFrame_Click(this, new RoutedEventArgs());
            // Opening a saved image can restore its first annotated object,
            // and a new capture can retain a selection from another screen.
            // Guided Add mode must instead start at this screen's next gap.
            ClassIdentifierText.Text = initialization.Steps
                .FirstOrDefault(item => item.ContextIdentifier == context)?.ClassIdentifier
                ?? AvailableClassChoices(CurrentContextName, hidesCompleted: true).FirstOrDefault()?.Identifier ?? "";
            RefreshClassChoices(); RefreshGuidance();
        });
    }
}
