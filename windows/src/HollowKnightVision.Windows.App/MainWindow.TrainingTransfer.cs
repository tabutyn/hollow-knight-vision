using System.Diagnostics;
using System.IO;
using System.Windows;
using HollowKnightVision.Windows.Core;
using Microsoft.Win32;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    internal Func<FileDialog, bool?>? ShowTrainingLibraryPicker { get; init; }
    private Task? trainingTransferTask;

    private void ExportTrainingLibrary_Click(object sender, RoutedEventArgs e) =>
        BeginTrainingLibraryTransfer(importing: false);

    private void ImportTrainingLibrary_Click(object sender, RoutedEventArgs e) =>
        BeginTrainingLibraryTransfer(importing: true);

    private void BeginTrainingLibraryTransfer(bool importing)
    {
        if (isClosing || trainingTransferTask is { IsCompleted: false }) return;
        trainingTransferTask = TransferTrainingLibraryAsync(importing);
    }

    private async Task TransferTrainingLibraryAsync(bool importing)
    {
        if (trainingInProgress || botTrainingCancellation is not null || botCapture is not null || gameControls.IsRecording)
        {
            TrainingTransferStatusText.Text = "Finish training or recording before transferring the library.";
            return;
        }

        var transferStarted = false;
        var resumeCapture = false;
        try
        {
            const string filter = "Vision Training Library (*.hkvtraining)|*.hkvtraining";
            FileDialog dialog = importing
                ? new OpenFileDialog { Filter = filter, CheckFileExists = true }
                : new SaveFileDialog
                {
                    Filter = filter, AddExtension = true, DefaultExt = ".hkvtraining",
                    FileName = $"Vision Training {DateTime.Now:yyyy-MM-dd} {Guid.NewGuid().ToString("N")[..8]}.hkvtraining"
                };
            var accepted = ShowTrainingLibraryPicker is { } picker ? picker(dialog) : dialog.ShowDialog(this);
            if (accepted != true || isClosing) return;
            transferStarted = true;
            IsEnabled = false;
            resumeCapture = importing && visionCancellation is not null;
            TrainingTransferStatusText.Text = importing ? "Importing training library…" : "Exporting training library…";
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
            if (importing && !isClosing)
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
            if (transferStarted) IsEnabled = true;
            if (resumeCapture && !isClosing && !gameLostCountdownStarted)
                StartVision_Click(this, new RoutedEventArgs());
        }
    }
}
