using System.Diagnostics;
using System.Globalization;
using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private CancellationTokenSource? trainingCancellation;
    private Task<TrainingProcessResult>? trainingProcessTask;
    private TrainingRunWorkspace? trainingRunWorkspace;
    private TrainingUiOutputPump? trainingOutputPump;
    private bool trainingStopping, trainingFinalizing;
    private int untrainedImageCount;
    private string? trainingProgress;

    internal static LabelingDatasetSnapshot ExportSharedTrainingDataset(string examplesRoot, string datasetRoot,
        IReadOnlyList<string> classes) => new LabelingDatasetExporter(datasetRoot).ExportSharedModel(classes,
            new LabelingExampleStore(examplesRoot).LoadTrainingExamplesInContext("game"), LabelingSharedModelPolicy.ModelIdentifier);

    private void RefreshTrainingNotification()
    {
        untrainedImageCount = LabelingTrainingNotificationPolicy.CountExamples(examples, modelLifecycle?.Latest(LabelingSharedModelPolicy.ModelIdentifier));
        UpdateModelTrainingControls();
    }
    private void ApplyTrainingOutput(TrainingOutputSnapshot snapshot)
    {
        if (isClosing || !trainingInProgress) return;
        TrainingLogText.Text = snapshot.Text; TrainingLogText.ScrollToEnd();
        trainingProgress = snapshot.ProgressText ?? trainingProgress; UpdateModelTrainingControls();
    }
    private void RequestStopTraining()
    {
        if (!trainingInProgress || trainingStopping || trainingFinalizing) return;
        trainingStopping = true; UpdateModelTrainingControls(); SetStatus("Stopping training…");
        trainingCancellation?.Cancel();
    }
    private bool StopTrainingForShutdown(out string? error)
    {
        error = null; trainingOutputPump?.Stop();
        if (trainingProcessTask is null || trainingFinalizing) return true;
        trainingCancellation?.Cancel();
        try
        {
            // The job terminates its descendants on cancellation. The core task never waits on this dispatcher.
            _ = trainingProcessTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            trainingRunWorkspace?.ArchiveInterrupted();
            return true;
        }
        catch (Exception failure) { error = failure.Message; return false; }
    }
    private async Task RunTrainerAsync(bool validateOnly)
    {
        if (trainingInProgress || isClosing) return;
        try { if (!validateOnly && !AdmitModelTraining(requireSelectedClass: false)) return; }
        catch (Exception error) { ShowError(error); return; }
        try
        {
            var dataset = LatestDatasetText.Text.Trim(); var output = TrainingOutputText.Text.Trim();
            if (!Directory.Exists(dataset)) throw new InvalidOperationException("Export or select a dataset directory first.");
            if (!int.TryParse(IterationsText.Text, out var iterations) || iterations is < 1 or > 10000)
                throw new InvalidOperationException("Iterations must be between 1 and 10000.");
            var windowsRoot = FindWindowsRoot() ?? throw new FileNotFoundException("Could not locate train-model-windows.ps1.");
            var start = new ProcessStartInfo("powershell.exe");
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(windowsRoot, "train-model-windows.ps1"), "-Dataset", dataset }) start.ArgumentList.Add(argument);
            if (validateOnly) start.ArgumentList.Add("-ValidateOnly");
            else
            {
                trainingRunWorkspace = new(output);
                foreach (var argument in new[] { "-Output", output, "-Iterations", iterations.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
                var checkpoint = BaseCheckpointText.Text.Trim();
                if (checkpoint.Length > 0)
                {
                    if (!File.Exists(checkpoint)) throw new FileNotFoundException("Base checkpoint was not found.", checkpoint);
                    start.ArgumentList.Add("-BaseCheckpoint"); start.ArgumentList.Add(checkpoint);
                }
            }
            trainingCancellation = new(); trainingInProgress = true; trainingStopping = trainingFinalizing = false;
            trainingProgress = "Preparing data…"; TrainingLogText.Clear(); UpdateModelTrainingControls();
            SetStatus(validateOnly ? "Validating dataset on CPU…" : "Training ONNX model on CPU…");
            trainingOutputPump ??= new(Dispatcher, ApplyTrainingOutput); var token = trainingOutputPump.Begin();
            Action<TrainingOutputSnapshot> changed = snapshot => trainingOutputPump.Post(token, snapshot);
            trainingProcessTask = validateOnly ? WindowsTrainingProcess.RunAsync(start, trainingCancellation.Token, changed)
                : trainingRunWorkspace!.RunAsync(start, trainingCancellation.Token, changed);
            var result = await trainingProcessTask;
            trainingOutputPump.Stop();
            if (isClosing) return;
            ApplyTrainingOutput(result.Output);
            if (result.Cancelled || trainingCancellation.IsCancellationRequested)
            {
                // Covers Stop arriving after the worker finished but before this dispatcher resumed.
                trainingRunWorkspace?.ArchiveInterrupted(); SetStatus("Training stopped"); return;
            }
            if (result.ExitCode != 0) throw new InvalidOperationException("Training failed: " +
                (result.Output.LastMessage ?? $"Helper exited with code {result.ExitCode}"));
            trainingFinalizing = true; UpdateModelTrainingControls();
            if (!validateOnly) await ActivateCompletedModelAsync(output);
            if (!isClosing) SetStatus(validateOnly ? "Dataset validation passed." : "CPU training complete; durable model version activated for live Vision.");
        }
        catch (Exception error) { if (!isClosing) ShowError(error); }
        finally
        {
            trainingOutputPump?.Stop(); trainingCancellation?.Dispose(); trainingCancellation = null;
            trainingProcessTask = null; trainingRunWorkspace = null;
            trainingInProgress = trainingStopping = trainingFinalizing = false; trainingProgress = null;
            if (!isClosing)
            {
                var status = StatusText.Text;
                try { RefreshExamples(); SetStatus(status); }
                catch (Exception error) { ShowError(error); }
                UpdateModelTrainingControls();
            }
        }
    }
}
