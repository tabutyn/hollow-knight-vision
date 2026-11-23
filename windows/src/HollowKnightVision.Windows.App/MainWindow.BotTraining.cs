using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private BotTrainingPlan? gameplayTrainingPlan, hackerTrainingPlan;
    private int botTrainingPlanGeneration;
    private CancellationTokenSource? botTrainingCancellation;
    private Task<TrainingProcessResult>? botTrainingProcessTask;
    private string? botTrainingMessage;

    private BotCaptureDomain SelectedBotTrainingDomain => BotTrainingDomain.SelectedIndex == 1
        ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay;

    private async void RefreshBotTrainingPlans(IReadOnlyList<BotCaptureManifest> library)
    {
        var generation = ++botTrainingPlanGeneration;
        var root = BotRoot;
        var plans = await Task.Run(() => (gameplay: Plan(BotCaptureDomain.Gameplay), hacker: Plan(BotCaptureDomain.Hacker)));
        if (generation != botTrainingPlanGeneration || isClosing) return;
        (gameplayTrainingPlan, hackerTrainingPlan) = plans;
        BotModelSummary.Text = $"Hacker · {plans.hacker.PolicyName ?? "No verified checkpoint"}    "
            + $"Game Play · {plans.gameplay.PolicyName ?? "No verified checkpoint"}";
        UpdateBotTrainingControls();

        BotTrainingPlan Plan(BotCaptureDomain domain)
        {
            try { return BotTrainingJob.Plan(root, domain, library); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                return new(domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay", "Checkpoint unavailable", [], false, error.Message);
            }
        }
    }

    private void BotTrainingDomain_Changed(object sender, SelectionChangedEventArgs e)
    {
        botTrainingMessage = null;
        UpdateBotTrainingControls();
    }

    private void UpdateBotTrainingControls()
    {
        if (BotTrainingStatus is null) return;
        var active = botTrainingCancellation;
        var plan = SelectedBotTrainingDomain == BotCaptureDomain.Hacker ? hackerTrainingPlan : gameplayTrainingPlan;
        BotTrainingDomain.IsEnabled = active is null;
        BotTrainButton.Content = active is null ? "Train" : "Stop";
        BotTrainButton.IsEnabled = !isClosing && (active is null ? plan?.CanTrain == true : !active.IsCancellationRequested);
        BotTrainingStatus.Text = botTrainingMessage ?? plan?.Unavailable ?? "";
    }

    private async void BotTrain_Click(object sender, RoutedEventArgs e)
    {
        if (isClosing) return;
        if (botTrainingCancellation is { } running)
        {
            running.Cancel();
            botTrainingMessage = "Stopping bot training…";
            UpdateBotTrainingControls();
            return;
        }

        var domain = SelectedBotTrainingDomain;
        var title = domain == BotCaptureDomain.Hacker ? "Hacker" : "Game Play";
        var root = BotRoot;
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        botTrainingCancellation = cancellation;
        botTrainingMessage = $"Preparing {title} training…";
        UpdateBotTrainingControls();
        var pump = new TrainingUiOutputPump(Dispatcher, output =>
        {
            if (isClosing || cancellation.IsCancellationRequested) return;
            if (output.ProgressText is { } progress) BotTrainingStatus.Text = progress;
            else if (output.LastMessage is { } message && message.StartsWith("Setting up ", StringComparison.Ordinal))
                BotTrainingStatus.Text = message;
        });
        var token = pump.Begin();
        try
        {
            var start = await Task.Run(() =>
            {
                var plan = BotTrainingJob.Plan(root, domain, BotCaptureSession.Library(BotCaptureRoot));
                var launcher = Path.Combine(AppContext.BaseDirectory, "training", "train-bot-windows.ps1");
                var host = Path.Combine(AppContext.BaseDirectory, "HollowKnightVision.Windows.Host.exe");
                return BotTrainingJob.Prepare(root, launcher, host, id, plan);
            }, cancellation.Token);
            if (isClosing) return;
            cancellation.Token.ThrowIfCancellationRequested();
            botTrainingMessage = $"Training and validating {title} clips…";
            UpdateBotTrainingControls();
            botTrainingProcessTask = WindowsTrainingProcess.RunAsync(start, cancellation.Token, output => pump.Post(token, output));
            var result = await botTrainingProcessTask;
            pump.Stop();
            if (isClosing) return;
            var log = Path.Combine(root, "training-runs", id.ToString("D"), "training.log");
            await File.WriteAllTextAsync(log, result.Output.Text);
            var receipt = BotTrainingJob.CompletionPath(root, domain, id);
            if (result.Cancelled && !File.Exists(receipt))
            {
                botTrainingMessage = "Bot training stopped.";
                return;
            }
            if (result.ExitCode != 0 && !result.Cancelled)
                throw new InvalidOperationException(result.Output.LastMessage ?? $"Trainer exited with code {result.ExitCode}.");
            var selected = await Task.Run(() => BotTrainingJob.VerifyCompleted(root, domain, id));
            botTrainingMessage = $"{title} is using {selected.Policy.Name}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            botTrainingMessage = "Bot training stopped.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            botTrainingMessage = $"Bot training failed. {error.Message}";
        }
        finally
        {
            pump.Stop();
            botTrainingProcessTask = null;
            botTrainingCancellation = null;
            if (!isClosing)
            {
                UpdateBotTrainingControls();
                RefreshBotLibrary();
            }
        }
    }

    private async Task<string?> StopBotTrainingForShutdownAsync()
    {
        botTrainingCancellation?.Cancel();
        if (botTrainingProcessTask is not { } process) return null;
        try
        {
            await process.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) { return error.Message; }
    }
}
