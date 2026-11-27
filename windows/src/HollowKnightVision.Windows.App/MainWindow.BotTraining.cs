using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private const string WaitForModelTraining = "Wait for Model training to finish before training the bot.";
    private BotTrainingPlan? gameplayTrainingPlan, hackerTrainingPlan;
    private BotCheckpointCatalog? gameplayCheckpoints, hackerCheckpoints;
    private IReadOnlyList<BotCaptureManifest> botTrainingLibrary = [];
    private bool changingBotCheckpoint;
    private int botTrainingPlanGeneration;
    private CancellationTokenSource? botTrainingCancellation;
    private Task? botTrainingTask;
    private string? botTrainingMessage;

    private BotCaptureDomain SelectedBotTrainingDomain => BotTrainingDomain.SelectedIndex == 1
        ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay;
    private BotCheckpointCatalog? SelectedBotCheckpoints => SelectedBotTrainingDomain == BotCaptureDomain.Hacker
        ? hackerCheckpoints : gameplayCheckpoints;

    private async Task RefreshBotTrainingPlansAsync(IReadOnlyList<BotCaptureManifest> library)
    {
        var generation = ++botTrainingPlanGeneration;
        var root = BotRoot;
        var plans = await Task.Run(() => (gameplay: Plan(BotCaptureDomain.Gameplay), hacker: Plan(BotCaptureDomain.Hacker)));
        if (generation != botTrainingPlanGeneration || isClosing) return;
        (gameplayTrainingPlan, gameplayCheckpoints) = plans.gameplay;
        (hackerTrainingPlan, hackerCheckpoints) = plans.hacker;
        PresentNewBotCaptures();
        BotModelSummary.Text = $"Hacker · {hackerTrainingPlan.PolicyName ?? "No verified checkpoint"}    "
            + $"Game Play · {gameplayTrainingPlan.PolicyName ?? "No verified checkpoint"}";
        PresentBotCheckpoints();
        UpdateBotTrainingControls();

        (BotTrainingPlan Plan, BotCheckpointCatalog? Catalog) Plan(BotCaptureDomain domain)
        {
            try
            {
                var plan = BotTrainingJob.Plan(root, domain, library);
                return (plan, new BotModelStore(root, domain).Catalog());
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                return (new(domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay", "Checkpoint unavailable", [], false, error.Message), null);
            }
        }
    }

    private void PresentNewBotCaptures()
    {
        if (hackerTrainingPlan?.LatestIncludedClips is not { } hacker
            || gameplayTrainingPlan?.LatestIncludedClips is not { } gameplay)
        {
            BotNewCaptureSummary.Visibility = Visibility.Collapsed;
            return;
        }
        var pending = BotTrainingJob.CountNewCaptures(botTrainingLibrary, hacker, gameplay);
        var runs = pending.Runs == 1 ? "1 new run" : $"{pending.Runs} new runs";
        BotNewCaptureBadge.Text = $"+{pending.Runs} 🤖";
        BotNewCaptureBadge.Foreground = pending.Runs > 0 ? System.Windows.Media.Brushes.DeepSkyBlue : System.Windows.Media.Brushes.Silver;
        System.Windows.Automation.AutomationProperties.SetName(BotNewCaptureBadge, runs);
        BotNewCaptureSummary.Text = $"{runs} · Hacker {pending.HackerClips} · Game Play {pending.GameplayClips}";
        BotNewCaptureSummary.Visibility = Visibility.Visible;
    }

    private void BotTrainingDomain_Changed(object sender, SelectionChangedEventArgs e)
    {
        botTrainingMessage = null;
        PresentBotCheckpoints();
        UpdateBotTrainingControls();
    }

    private void PresentBotCheckpoints()
    {
        if (BotPlaybackVersion is null) return;
        var catalog = SelectedBotCheckpoints;
        BotPlaybackVersion.ItemsSource = catalog?.Versions;
        BotPlaybackVersion.SelectedValue = catalog?.ActiveId;
        if (BotPlaybackVersion.SelectedIndex == -1 && catalog?.Versions.Count > 0) BotPlaybackVersion.SelectedIndex = 0;
    }

    private void BotPlaybackVersion_Changed(object sender, SelectionChangedEventArgs e) => UpdateBotTrainingControls();

    private async void BotUseVersion_Click(object sender, RoutedEventArgs e)
    {
        if (BotPlaybackVersion.SelectedValue is string id) await SelectBotCheckpointAsync(id);
    }

    private async void BotRestorePrevious_Click(object sender, RoutedEventArgs e) => await SelectBotCheckpointAsync(null);

    private async Task SelectBotCheckpointAsync(string? version)
    {
        if (isClosing || changingBotCheckpoint || refreshingBotLibrary || botTrainingCancellation is not null) return;
        var domain = SelectedBotTrainingDomain;
        var store = new BotModelStore(BotRoot, domain);
        changingBotCheckpoint = true;
        ++botTrainingPlanGeneration;
        UpdateBotTrainingControls();
        try
        {
            var chosen = await Task.Run(() => version is null ? store.RestorePrevious() : store.Activate(version));
            if (isClosing || chosen is null) return;
            StopBotForCheckpointChange(domain);
            botTrainingMessage = $"Using {chosen.Policy.Name}.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            if (!isClosing) botTrainingMessage = $"Could not select checkpoint. {error.Message}";
        }
        finally
        {
            changingBotCheckpoint = false;
            if (!isClosing && !refreshingBotLibrary) await RefreshBotTrainingPlansAsync(botTrainingLibrary);
        }
    }

    private void StopBotForCheckpointChange(BotCaptureDomain domain)
    {
        if (Volatile.Read(ref botPlayback)?.Domain == domain)
            StopBotPlayback("Bot model changed. Press B to play the selected checkpoint.", reason: "model-changed");
    }

    private void UpdateBotTrainingControls()
    {
        if (BotTrainingStatus is null) return;
        var active = botTrainingCancellation;
        var plan = SelectedBotTrainingDomain == BotCaptureDomain.Hacker ? hackerTrainingPlan : gameplayTrainingPlan;
        var canSelect = !isClosing && active is null && !changingBotCheckpoint && !refreshingBotLibrary;
        var canTrain = canSelect && !deletingBotClip && !trainingInProgress;
        BotTrainingDomain.IsEnabled = active is null && !changingBotCheckpoint;
        BotPlaybackVersion.IsEnabled = canSelect && SelectedBotCheckpoints is { Versions.Count: > 0 };
        BotUseVersion.IsEnabled = canSelect && BotPlaybackVersion.SelectedItem is BotCheckpointVersion version
            && version.Id != SelectedBotCheckpoints?.ActiveId;
        BotRestorePrevious.IsEnabled = canSelect && SelectedBotCheckpoints?.PreviousId is not null;
        BotTrainButton.Content = active is null ? "Train" : "Stop";
        BotTrainButton.IsEnabled = active is null ? canTrain && plan?.CanTrain == true
            : !isClosing && !changingBotCheckpoint && !deletingBotClip && !active.IsCancellationRequested;
        BotTrainBothButton.IsEnabled = canTrain
            && (gameplayTrainingPlan?.CanTrain == true || hackerTrainingPlan?.CanTrain == true);
        BotTrainingStatus.Text = trainingInProgress && active is null ? WaitForModelTraining
            : botTrainingMessage ?? plan?.Unavailable ?? "";
        UpdateBotClipDeletionControls();
    }

    private async void BotTrain_Click(object sender, RoutedEventArgs e)
    {
        if (isClosing || changingBotCheckpoint || deletingBotClip) return;
        if (botTrainingCancellation is { } running)
        {
            running.Cancel();
            botTrainingMessage = "Stopping bot training…";
            UpdateBotTrainingControls();
            return;
        }
        await StartBotTrainingAsync([SelectedBotTrainingDomain]);
    }

    private async void BotTrainBoth_Click(object sender, RoutedEventArgs e) =>
        await StartBotTrainingAsync([BotCaptureDomain.Gameplay, BotCaptureDomain.Hacker]);

    private async Task StartBotTrainingAsync(IReadOnlyList<BotCaptureDomain> domains)
    {
        if (isClosing || changingBotCheckpoint || deletingBotClip || refreshingBotLibrary
            || botTrainingCancellation is not null) return;
        if (trainingInProgress)
        {
            SetStatus(WaitForModelTraining);
            return;
        }
        using var cancellation = new CancellationTokenSource();
        botTrainingCancellation = cancellation;
        botTrainingMessage = "Preparing bot training…";
        UpdateBotTrainingControls();
        UpdateModelTrainingControls();
        botTrainingTask = RunBotTrainingAsync(domains, cancellation.Token);
        try
        {
            await botTrainingTask;
        }
        finally
        {
            botTrainingTask = null;
            botTrainingCancellation = null;
            if (!isClosing)
            {
                UpdateBotTrainingControls();
                UpdateModelTrainingControls();
                RefreshBotLibrary();
            }
        }
    }

    private async Task RunBotTrainingAsync(IReadOnlyList<BotCaptureDomain> domains, CancellationToken cancellation)
    {
        var outcomes = new List<string>();
        var combined = domains.Count > 1;
        try
        {
            var library = await Task.Run(() => BotCaptureSession.Library(BotCaptureRoot), cancellation);
            foreach (var domain in domains)
            {
                cancellation.ThrowIfCancellationRequested();
                var title = domain == BotCaptureDomain.Hacker ? "Hacker" : "Game Play";
                try
                {
                    var plan = await Task.Run(() => BotTrainingJob.Plan(BotRoot, domain, library), cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (combined && !plan.CanTrain)
                    {
                        outcomes.Add($"{title} skipped. {plan.Unavailable}");
                        continue;
                    }
                    outcomes.Add(await TrainBotDomainAsync(domain, title, plan, cancellation));
                }
                catch (Exception error) when (IsBotTrainingError(error))
                {
                    outcomes.Add($"{(combined ? title : "Bot")} training failed. {error.Message}");
                }
            }
            cancellation.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            outcomes.Add("Bot training stopped.");
        }
        catch (Exception error) when (IsBotTrainingError(error))
        {
            outcomes.Add($"Bot training failed. {error.Message}");
        }
        if (!isClosing) botTrainingMessage = string.Join(Environment.NewLine, outcomes);
    }

    private async Task<string> TrainBotDomainAsync(BotCaptureDomain domain, string title, BotTrainingPlan plan,
        CancellationToken cancellation)
    {
        var id = Guid.NewGuid();
        var start = await Task.Run(() => BotTrainingJob.Prepare(BotRoot,
            Path.Combine(AppContext.BaseDirectory, "training", "train-bot-windows.ps1"),
            Path.Combine(AppContext.BaseDirectory, "HollowKnightVision.Windows.Host.exe"), id, plan), cancellation);
        cancellation.ThrowIfCancellationRequested();
        botTrainingMessage = $"Training and validating {title} clips…";
        UpdateBotTrainingControls();
        var pump = new TrainingUiOutputPump(Dispatcher, output =>
        {
            if (isClosing || cancellation.IsCancellationRequested) return;
            if (output.ProgressText is { } progress) BotTrainingStatus.Text = $"{title} · {progress}";
            else if (output.LastMessage is { } message && message.StartsWith("Setting up ", StringComparison.Ordinal))
                BotTrainingStatus.Text = message;
        });
        var token = pump.Begin();
        TrainingProcessResult result;
        try
        {
            result = await WindowsTrainingProcess.RunAsync(start, cancellation, output => pump.Post(token, output));
        }
        finally { pump.Stop(); }
        var log = Path.Combine(BotRoot, "training-runs", id.ToString("D"), "training.log");
        await File.WriteAllTextAsync(log, result.Output.Text);
        if (result.Cancelled && !File.Exists(BotTrainingJob.CompletionPath(BotRoot, domain, id)))
            cancellation.ThrowIfCancellationRequested();
        if (result.ExitCode != 0 && !result.Cancelled)
            throw new InvalidOperationException(result.FailureMessage);
        var selected = await Task.Run(() => BotTrainingJob.VerifyCompleted(BotRoot, domain, id));
        if (!isClosing) StopBotForCheckpointChange(domain);
        return $"{title} is using {selected.Policy.Name}.";
    }

    private static bool IsBotTrainingError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception;

    private async Task<string?> StopBotTrainingForShutdownAsync()
    {
        botTrainingCancellation?.Cancel();
        if (botTrainingTask is not { } training) return null;
        try
        {
            await training.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) { return error.Message; }
    }
}
