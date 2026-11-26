using System.Diagnostics;
using System.IO;
using System.Windows;
using HollowKnightVision.Windows.Core;
using Microsoft.Win32;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private async void ExportTrainingLibrary_Click(object sender, RoutedEventArgs e) =>
        await TransferTrainingLibraryAsync(importing: false);

    private async void ImportTrainingLibrary_Click(object sender, RoutedEventArgs e) =>
        await TransferTrainingLibraryAsync(importing: true);

    private async Task TransferTrainingLibraryAsync(bool importing)
    {
        if (trainingInProgress || botTrainingCancellation is not null || botCapture is not null || gameControls.IsRecording)
        {
            TrainingTransferStatusText.Text = "Finish training or recording before transferring the library.";
            return;
        }

        const string filter = "Vision Training Library (*.hkvtraining)|*.hkvtraining";
        FileDialog dialog = importing
            ? new OpenFileDialog { Filter = filter, CheckFileExists = true }
            : new SaveFileDialog
            {
                Filter = filter, AddExtension = true, DefaultExt = ".hkvtraining",
                FileName = $"Vision Training {DateTime.Now:yyyy-MM-dd} {Guid.NewGuid().ToString("N")[..8]}.hkvtraining"
            };
        if (dialog.ShowDialog(this) != true) return;
        IsEnabled = false;
        var resumeCapture = importing && visionCancellation is not null;
        TrainingTransferStatusText.Text = importing ? "Importing training library…" : "Exporting training library…";
        try
        {
            if (resumeCapture)
            {
                StopVision();
                if (visionRunCompletion is { } completion) await completion;
            }
            var training = FindWindowsRoot() ?? throw new FileNotFoundException("Vision's training scripts are missing.");
            var start = new ProcessStartInfo("powershell.exe");
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(training, "transfer-training-library-windows.ps1"), "-Operation", importing ? "import" : "export",
                "-Support", supportRoot, "-Bundle", dialog.FileName })
                start.ArgumentList.Add(argument);
            var result = await WindowsTrainingProcess.RunAsync(start, CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(result.FailureMessage);
            if (importing)
            {
                modelLifecycle.Refresh();
                RefreshExamples();
                menuStencilRecognizer.Refresh(initialization.LanguageIdentifier);
                RefreshModelArtifacts();
                RefreshInitializationStatus();
                await RefreshBotLibraryAsync();
            }
            TrainingTransferStatusText.Text = result.Output.LastMessage ?? "Training library transfer complete.";
        }
        catch (Exception error)
        {
            TrainingTransferStatusText.Text = error.Message;
        }
        finally
        {
            IsEnabled = true;
            if (resumeCapture && !isClosing && !gameLostCountdownStarted)
                StartVision_Click(this, new RoutedEventArgs());
        }
    }
}
