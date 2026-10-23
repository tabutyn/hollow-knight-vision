using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly LabelingModelSummarySession modelSummary = new();
    private ModelSummaryReviewPanel? modelSummaryReviewPanel;

    private void SelectModelClass(string identifier)
    {
        ApplyLabelSelection(() => { ContextCombo.SelectedValue = "Gameplay"; ClassIdentifierText.Text = identifier; });
        modelSummaryReviewPanel?.SetSelectedClass(ClassIdentifierText.Text);
    }

    private void RefreshModelSummary()
    {
        modelSummaryReviewPanel ??= new(modelSummary, key => initialization.Text(key), SelectModelClass, OpenScreenshotForRepair);
        if (!ModelSummaryPanel.Children.Contains(modelSummaryReviewPanel))
        { ModelSummaryPanel.Children.Clear(); ModelSummaryPanel.Children.Add(modelSummaryReviewPanel); }
        try
        {
            var custom = customObjectChoices.Select(item => new MenuStencilLabel(item.Identifier, item.Name)).ToArray();
            modelSummary.SetContent(examples, modelLifecycle.Latest(), LabelingObjectReferencePolicy.ModelClasses(custom).Select(item => item.Identifier), ClassIdentifierText.Text);
            modelSummaryReviewPanel.Refresh(custom.Select(item => item.Identifier), objectReferencePanel?.Atlas);
        }
        catch (Exception error) { modelSummaryReviewPanel.ShowError($"Model review failed: {error.Message}"); }
    }
}
