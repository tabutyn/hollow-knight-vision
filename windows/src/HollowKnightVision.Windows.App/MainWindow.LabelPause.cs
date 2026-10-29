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
            var step = initialization.Steps.FirstOrDefault(item => item.ContextIdentifier == initializationTargetContext)
                ?? (initialization.IsFirstPhaseComplete ? null : initialization.Steps.FirstOrDefault());
            if (initializationTargetContext is null && !initialization.HasUsableHUD
                && !initialization.Steps.Any(item => item.ContextIdentifier is "main-title" or "select-profile"))
                step = new StencilInitializationStep("gameplay-hud", "Gameplay HUD", "game.health", "Health", 1, 2);
            var target = initialization.Contexts.FirstOrDefault(item => item.Identifier == initializationTargetContext);
            if (recognizedMenuContext == "game") ContextCombo.SelectedValue = "Gameplay";
            else if (target is not null) ContextCombo.SelectedValue = target.Name;
            else if (step is not null) ContextCombo.SelectedValue = step.ContextName;
            else if (recognizedMenuContext is { } contextIdentifier) ContextCombo.SelectedValue = ContextDisplayName(contextIdentifier);
            var context = StencilInitializationPolicy.NormalizeContext(CurrentContextName);
            var saved = context == "game" ? null : initialization.ExampleFor(context);
            if (saved is not null) OpenLabelExample(saved);
            else CaptureFrame_Click(this, new RoutedEventArgs());
            RefreshClassChoices(); RefreshGuidance();
        });
    }
}
