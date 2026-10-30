using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly LabelingModelSummarySession modelSummary = new();
    private ModelSummaryReviewPanel? modelSummaryReviewPanel;
    private readonly LabelingCampaignPanel campaignPanel = new();
    private Dictionary<string, int> campaignCounts = [], campaignCompletedCounts = [];
    private bool campaignHistoryUnavailable;
    private MenuStencilLabel[] CampaignClasses() => LabelingObjectReferencePolicy.ModelClasses(
        customObjectChoices.Select(item => new MenuStencilLabel(item.Identifier, item.Name)).ToArray()).ToArray();
    private LabelingCampaignProgress[] CampaignProgress() => CampaignClasses().OrderBy(item => item.Identifier)
        .Select(item => new LabelingCampaignProgress(item.Identifier, campaignCounts.GetValueOrDefault(item.Identifier),
            campaignCompletedCounts.GetValueOrDefault(item.Identifier))).ToArray();
    private void RefreshCampaignProgress()
    {
        campaignCounts = LabelingCampaignPolicy.Counts(examples);
        try
        {
            campaignCompletedCounts = modelLifecycle?.Latest() is { } latest
                ? LabelingCampaignPolicy.CompletedCounts(latest.DatasetDirectory) : [];
            campaignHistoryUnavailable = false;
        }
        catch (Exception) { campaignCompletedCounts = []; campaignHistoryUnavailable = true; }
        RefreshGuidance();
    }
    private string? CampaignGuidance()
    {
        if (campaignHistoryUnavailable) return initialization.Text("campaignMissingHistory");
        var classes = CampaignClasses();
        var selected = classes.FirstOrDefault(item => item.Identifier == ClassIdentifierText.Text)
            ?? classes.FirstOrDefault(item => item.Identifier == "game.playable-knight");
        if (selected is null) return null;
        var progress = new LabelingCampaignProgress(selected.Identifier, campaignCounts.GetValueOrDefault(selected.Identifier),
            campaignCompletedCounts.GetValueOrDefault(selected.Identifier));
        return initialization.Text(progress.GuidanceKey(workspace == "Label", workspace == "Model"),
            ("object", initialization.Text(selected.Name)), ("count", progress.Count.ToString()),
            ("target", progress.Target.ToString()), ("remaining", progress.Remaining.ToString()));
    }

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
            RefreshCampaignProgress();
            var classes = CampaignClasses();
            campaignPanel.Update(CampaignProgress(), LabelingCampaignPolicy.History(modelLifecycle.Candidates),
                campaignHistoryUnavailable, key => initialization.Text(key),
                identifier => initialization.Text(classes.First(item => item.Identifier == identifier).Name));
            var custom = customObjectChoices.Select(item => new MenuStencilLabel(item.Identifier, item.Name)).ToArray();
            modelSummary.SetContent(examples, modelLifecycle.Latest(), LabelingObjectReferencePolicy.ModelClasses(custom).Select(item => item.Identifier), ClassIdentifierText.Text);
            modelSummaryReviewPanel.Refresh(custom.Select(item => item.Identifier), objectReferencePanel?.Atlas);
        }
        catch (Exception error) { modelSummaryReviewPanel.ShowError($"Model review failed: {error.Message}"); }
    }
}
