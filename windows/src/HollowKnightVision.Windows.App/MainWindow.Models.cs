using System.IO;
using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly LabelingModelLifecycle modelLifecycle;
    private string[] activeModelRuns = [];
    private IReadOnlyList<LabelingActiveModelArtifact> activeModelArtifacts = [];
    private bool trainingInProgress;

    private void RefreshModelArtifacts()
    {
        activeModelRuns = modelLifecycle.ActiveRunDirectories();
        activeModelArtifacts = modelLifecycle.ActiveArtifacts();
        VisionRunText.Text = activeModelRuns.FirstOrDefault() ?? BundledModelRun() ?? "";
        TrainingControls.VersionText.Text = modelLifecycle.ActiveVersionName();
        trainedGameplayModelReady = HasTrainedGameplayModel();
    }

    private void InitializeModels()
    {
        try
        {
            modelLifecycle.Refresh();
            modelLifecycle.PromoteLatest();
            RefreshModelArtifacts();
        }
        catch (Exception error)
        {
            // Model review failure must not prevent game capture or stencil labeling.
            activeModelRuns = [];
            activeModelArtifacts = [];
            VisionRunText.Text = BundledModelRun() ?? "";
            trainedGameplayModelReady = false;
            TrainingControls.VersionText.Text = "Unavailable";
            TrainingControls.VersionText.ToolTip = error.Message;
            SetStatus($"Model review failed: {error.Message}");
        }
    }

    private async Task ActivateCompletedModelAsync(string runDirectory)
    {
        if (isClosing || gameLostCountdownStarted) return;
        modelLifecycle.Refresh();
        var candidate = modelLifecycle.Candidates.FirstOrDefault(value =>
            string.Equals(Path.GetFullPath(value.RunDirectory), Path.GetFullPath(runDirectory), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Completed training run was not found in the model review store.");
        modelLifecycle.Promote(candidate);
        var resume = visionCancellation is not null;
        StopVision();
        if (visionRunCompletion is { } completion) await completion;
        if (isClosing) return;
        RefreshModelArtifacts();
        if (workspace == "Model") RefreshModelWorkspace();
        if (resume && !isClosing && !gameLostCountdownStarted) StartVision_Click(this, new RoutedEventArgs());
    }
}
