using System.IO;
using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly LabelingSelectionUpdateGuard labelSelectionUpdate = new();
    private LabelingSelectionPreferences? labelingSelectionPreferences;
    private bool labelSelectionReady;

    private void InitializeLabelingSelection(string root)
    {
        labelingSelectionPreferences = new(Path.Combine(root, "labeling-v1", "selection.json"));
        var selection = labelingSelectionPreferences.Load(customObjectChoices.Select(item => item.Identifier).ToArray());
        ApplyLabelSelection(() =>
        {
            var name = ContextDisplayName(selection.ContextIdentifier);
            ContextCombo.SelectedValue = ContextCombo.Items.OfType<ContextChoice>().Any(item => item.Name == name) ? name : "Gameplay";
            ClassIdentifierText.Text = selection.ClassIdentifier;
        });
        labelSelectionReady = true;
    }

    private void ApplyLabelSelection(Action change)
    {
        labelSelectionUpdate.Run(change);
        if (labelSelectionUpdate.IsUpdating) return;
        RefreshClassChoices();
    }

    private void RememberLabelSelection()
    {
        if (!labelSelectionReady || labelSelectionUpdate.IsUpdating || labelingSelectionPreferences is null) return;
        try { labelingSelectionPreferences.Save(StencilInitializationPolicy.NormalizeContext(CurrentContextName), ClassIdentifierText.Text); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { SetStatus($"Could not save labeling selection: {error.Message}"); }
    }

    private void ChangeLabelingContext(SelectionChangedEventArgs e)
    {
        if (ClassCombo is null || initialization is null || labelSelectionUpdate.IsUpdating) return;
        var previousName = e.RemovedItems.OfType<ContextChoice>().FirstOrDefault()?.Name;
        try
        {
            var destination = StencilInitializationPolicy.NormalizeContext(CurrentContextName);
            if (labelSelectionReady && workspace == "Label" && labelCapture.FrozenFrame is { } frozen)
            {
                var transition = LabelingContextTransitionPolicy.Resolve(destination, currentExample, frozen, initialization.ExampleFor);
                if (transition?.Example is { } saved) ApplySavedLabelExample(saved);
                else if (transition is not null)
                {
                    var bitmap = FrameBitmap(transition.Capture!);
                    ApplyLabelSelection(() =>
                    {
                        CancelLabelGesture(); currentExample = null; currentFrame = transition.Capture;
                        captureGroupIdentifier = transition.CaptureGroupIdentifier;
                        boxes.Clear(); labelUndo.Clear(); ExamplesList.SelectedItem = null;
                        KnownClassesText.Text = "";
                        LabelImage.Source = bitmap; ResetLabelViewport(); RedrawBoxes();
                    });
                    SetStatus($"Started an unsaved {initialization.Text(CurrentContextName)} draft from the paused capture.");
                }
                if (transition is not null && destination != "game")
                    ClassIdentifierText.Text = AvailableClassChoices(CurrentContextName, hidesCompleted: true).FirstOrDefault()?.Identifier ?? "";
            }
            RefreshClassChoices();
        }
        catch (Exception error)
        {
            // Failed reference reads must not leave an old saved example under
            // a new selector value, where the next autosave could reclassify it.
            if (previousName is not null) ApplyLabelSelection(() => ContextCombo.SelectedValue = previousName);
            ShowError(error);
        }
    }

    private void CancelLabelGesture()
    {
        EndLabelPan();
        if (LabelCanvas.IsMouseCaptured) LabelCanvas.ReleaseMouseCapture();
        dragStart = null; dragPreview = null;
        modifyingBox = null; modifyDragStart = null; modifyOriginal = null;
        BoxesList.SelectedItem = null;
    }
}
