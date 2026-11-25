using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly LabelingModelSummarySession modelSummary = new();
    private ModelSummaryReviewPanel? modelSummaryReviewPanel;
    private readonly LabelingCampaignPanel campaignPanel = new();

    private void SelectModelClass(string identifier)
    {
        ApplyLabelSelection(() => { ContextCombo.SelectedValue = "Gameplay"; ClassIdentifierText.Text = identifier; });
        modelSummaryReviewPanel?.SetSelectedClass(ClassIdentifierText.Text);
    }

    private void RefreshModelSummary()
    {
        modelSummaryReviewPanel ??= new(modelSummary, key => initialization.Text(key), SelectModelClass, OpenScreenshotForRepair);
        if (!ModelSummaryPanel.Children.Contains(modelSummaryReviewPanel))
        {
            ModelSummaryPanel.Children.Clear(); ModelSummaryPanel.RowDefinitions.Clear();
            ModelSummaryPanel.RowDefinitions.Add(new() { Height = System.Windows.GridLength.Auto });
            ModelSummaryPanel.RowDefinitions.Add(new());
            ModelSummaryPanel.Children.Add(campaignPanel);
            System.Windows.Controls.Grid.SetRow(modelSummaryReviewPanel, 1);
            ModelSummaryPanel.Children.Add(modelSummaryReviewPanel);
        }
        try
        {
            var custom = customObjectChoices.Select(item => new MenuStencilLabel(item.Identifier, item.Name)).ToArray();
            var classes = LabelingObjectReferencePolicy.ModelClasses(custom);
            var counts = LabelingCampaignPolicy.Counts(examples);
            var progress = classes.OrderBy(item => item.Identifier)
                .Select(item => new LabelingCampaignProgress(item.Identifier, counts.GetValueOrDefault(item.Identifier))).ToArray();
            campaignPanel.Update(progress, LabelingCampaignPolicy.History(modelLifecycle.Candidates),
                key => initialization.Text(key), identifier => initialization.Text(classes.First(item => item.Identifier == identifier).Name));
            modelSummary.SetContent(examples, modelLifecycle.Latest(), classes.Select(item => item.Identifier), ClassIdentifierText.Text);
            modelSummaryReviewPanel.Refresh(custom.Select(item => item.Identifier), objectReferencePanel?.Atlas);
        }
        catch (Exception error) { modelSummaryReviewPanel.ShowError($"Model review failed: {error.Message}"); }
    }
}
