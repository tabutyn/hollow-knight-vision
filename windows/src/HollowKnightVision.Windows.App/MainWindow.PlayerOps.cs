using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly Brush playerOpsStatusBrush = new SolidColorBrush(Color.FromRgb(153, 153, 153));
    private readonly Brush playerOpsErrorBrush = new SolidColorBrush(Color.FromRgb(255, 104, 104));
    private readonly PlayerOpsPreferences playerOpsPreferences;
    private bool playerOpsBusy;
    private bool confirmedInvincibility;
    private bool randomizeAfterPendingOperation;

    private bool CanStartPlayerOps() => CanUseOps() && recognizedMenuContext == "game"
        && !playerOpsBusy && !isClosing && !gameLostCountdownStarted;

    private void RefreshOps()
    {
        var windowsRoot = FindWindowsRoot();
        var repositoryRoot = windowsRoot is null
            ? null : Directory.GetParent(Directory.GetParent(Directory.GetParent(windowsRoot)!.FullName)!.FullName)?.FullName;
        var python = repositoryRoot is null
            ? null : Path.Combine(repositoryRoot, ".tools", "hkv-training-windows", "Scripts", "python.exe");
        var window = HollowKnightWindowLocator.FindBest();
        OpsText.Text = string.Join(Environment.NewLine, new[]
        {
            $"Windows tooling: {(windowsRoot is null ? "NOT FOUND" : windowsRoot)}",
            $"CPU training Python: {(python is not null && File.Exists(python) ? python : "NOT FOUND")}",
            $"Examples: {ExamplesRootText.Text}",
            $"Datasets: {DatasetRootText.Text}",
            $"Game window: {(window is null ? "not running" : $"PID {window.ProcessId}, {window.ClientWidth}x{window.ClientHeight}")}",
            "ML device policy: CPU; train-model-windows.ps1 sets CUDA_VISIBLE_DEVICES=-1",
            "Capture policy: target-window PrintWindow with GDI fallback, normalized to 640x360 on CPU"
        });
        if (!CanUseOps() && !playerOpsBusy)
            SetPlayerOpsStatus("Updated game receiver is not ready.", error: true);
        _ = QueryPlayerOpsAsync();
    }

    private Task QueryPlayerOpsAsync() => SendPlayerOpsAsync(
        "Loading player state…", ReceiverPlayerOpsCommand.Query, requireState: true);

    private async void OpsApply_Click(object sender, RoutedEventArgs e) => await ApplyPlayerDraftAsync();

    private async void OpsValue_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await ApplyPlayerDraftAsync();
    }

    private Task ApplyPlayerDraftAsync()
    {
        if (!CanStartPlayerOps() || !TryReadPlayerTestState(out var state)) return Task.CompletedTask;
        return SendPlayerOpsAsync("Applying player state…",
            session => ReceiverPlayerOpsCommand.Apply(session, state), requireState: true);
    }

    private async void OpsInvincible_Click(object sender, RoutedEventArgs e)
    {
        var enabled = OpsInvincibleCheck.IsChecked == true;
        if (!CanStartPlayerOps())
        {
            OpsInvincibleCheck.IsChecked = confirmedInvincibility;
            return;
        }
        // Use the game's current counters for this one-field change. Keep any
        // unsent numeric edits in the form for the next explicit Apply.
        await ChangeCurrentPlayerStateAsync("Applying invincibility…",
            state => state with { Invincible = enabled }, updateFields: false);
    }

    private async void OpsRandomize_Click(object sender, RoutedEventArgs e) => await RandomizePlayerStateAsync();

    private Task RandomizePlayerStateAsync() => ChangeCurrentPlayerStateAsync(
        "Randomizing player state…", state => PlayerTestState.Randomized(state.Invincible));

    private Task RandomizeAfterLabelAsync()
    {
        if (isClosing || OpsRandomizeAfterLabelCheck.IsChecked != true) return Task.CompletedTask;
        if (!playerOpsBusy) return RandomizePlayerStateAsync();
        randomizeAfterPendingOperation = true;
        return Task.CompletedTask;
    }

    private void OpsRandomizeAfterLabel_Click(object sender, RoutedEventArgs e)
    {
        try { playerOpsPreferences.SaveRandomizeAfterLabel(OpsRandomizeAfterLabelCheck.IsChecked == true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            OpsRandomizeAfterLabelCheck.IsChecked = playerOpsPreferences.LoadRandomizeAfterLabel();
            SetPlayerOpsStatus("Could not save the Label randomization setting.", error: true);
        }
    }

    private async void OpsRestoreEnemies_Click(object sender, RoutedEventArgs e) => await SendPlayerOpsAsync(
        "Restoring enemies…", ReceiverPlayerOpsCommand.RestoreEnemies);

    private Task SendPlayerOpsAsync(string status, Func<Guid, ReceiverPlayerOpsCommand> command, bool requireState = false) =>
        RunPlayerOpsAsync(status, async () => ApplyPlayerOpsAcknowledgement(
            await gameControls.PerformPlayerOpsAsync(command), requireState: requireState));

    private Task ChangeCurrentPlayerStateAsync(string status, Func<PlayerTestState, PlayerTestState> change, bool updateFields = true) =>
        RunPlayerOpsAsync(status, async () =>
        {
            var current = await gameControls.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.Query);
            if (current is not { Accepted: true, State: { } state })
            {
                ApplyPlayerOpsAcknowledgement(current, requireState: true);
                return;
            }
            confirmedInvincibility = state.Invincible;
            if (isClosing || gameLostCountdownStarted || recognizedMenuContext != "game")
            {
                OpsInvincibleCheck.IsChecked = confirmedInvincibility;
                SetPlayerOpsStatus("Connect to gameplay to load player state.", error: true);
                return;
            }
            var requested = change(state.Normalized());
            var acknowledgement = await gameControls.PerformPlayerOpsAsync(
                session => ReceiverPlayerOpsCommand.Apply(session, requested), requiredSession: current.SessionId);
            ApplyPlayerOpsAcknowledgement(acknowledgement, updateFields, requireState: true);
        });

    private async Task RunPlayerOpsAsync(string status, Func<Task> operation)
    {
        if (!CanStartPlayerOps()) return;
        SetPlayerOpsBusy(true);
        SetPlayerOpsStatus(status);
        try { await operation(); }
        finally { SetPlayerOpsBusy(false); }
        if (!randomizeAfterPendingOperation) return;
        randomizeAfterPendingOperation = false;
        await RandomizeAfterLabelAsync();
    }

    private void SetPlayerOpsBusy(bool busy)
    {
        playerOpsBusy = busy;
        PlayerOpsControls.IsEnabled = !busy;
        OpsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OpsResetRoomButton.IsEnabled = CanResetRoom();
    }

    private bool CanResetRoom() => CanStartPlayerOps() && gameControls.RoomResetAvailable
        && !atlasOperationInProgress && botCapture is null && !changingBotCapture
        && Volatile.Read(ref botPlayback) is null && inputPathReplayCancellation is null;

    private bool CanResetRoomFromShortcut() => CanResetRoom()
        && (SettingsOverlay.Visibility == Visibility.Visible
            ? SettingsSystemTab.IsSelected
            : workspace is "Gameplay" or "Hacker" or "Ops");

    private async void OpsResetRoom_Click(object sender, RoutedEventArgs e) => await ResetRoomAsync();

    private Task ResetRoomAsync()
    {
        if (!CanResetRoom()) return Task.CompletedTask;
        ReleaseForwardedInput();
        const string resetting = "Resetting room…";
        return RunPlayerOpsAsync(resetting, async () =>
        {
            try
            {
                await RunAtlasOperationAsync(async () =>
                {
                    var acknowledgement = await gameControls.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.ResetRoom);
                    if (acknowledgement?.ConfirmsRoomReset != true)
                    {
                        if (acknowledgement?.Accepted == true)
                            SetPlayerOpsStatus("Room reset was not confirmed.", error: true);
                        else ApplyPlayerOpsAcknowledgement(acknowledgement);
                        return;
                    }

                    if (acknowledgement.State is { } state) SetPlayerTestState(state.Normalized());
                    SetPlayerOpsStatus("Clearing Atlas…");
                    liveWorldPipeline?.InvalidatePendingWork();
                    var errors = new List<string>();
                    try { await ReplaceActiveAtlasWithEmptyAsync(retainPrevious: false); }
                    catch (Exception error) { errors.Add(error.Message); }
                    try { await ClearHackerAtlasAsync(); }
                    catch (Exception error) { errors.Add(error.Message); }
                    SetPlayerOpsStatus("Room reset.", error: errors.Count > 0);
                    if (errors.Count > 0) OpsStatusText.Text += " " + string.Join(" ", errors);
                }, canReplaceFaultedActive: true);
            }
            finally
            {
                if (OpsStatusText.Text == initialization.Text(resetting))
                    SetPlayerOpsStatus("Room reset did not finish.", error: true);
            }
        });
    }

    private void OpsReload_Click(object sender, RoutedEventArgs e) => RefreshOps();

    private bool TryReadPlayerTestState(out PlayerTestState state)
    {
        var controls = new[]
        {
            OpsMaxHealthText, OpsHealthText, OpsLifebloodText,
            OpsManaText, OpsManaSlotsText, OpsGeoText
        };
        var values = new int[controls.Length];
        for (var index = 0; index < controls.Length; index++)
        {
            if (!int.TryParse(controls[index].Text, out values[index]))
            {
                SetPlayerOpsStatus("Enter whole numbers for player state.", error: true);
                state = PlayerTestState.Default;
                return false;
            }
        }
        state = new PlayerTestState(values[0], values[1], values[2], values[3], values[4], values[5],
            OpsInvincibleCheck.IsChecked == true).Normalized();
        return true;
    }

    private void ApplyPlayerOpsAcknowledgement(ReceiverPlayerOpsAcknowledgement? acknowledgement,
        bool updateFields = true, bool requireState = false)
    {
        if (isClosing) return;
        if (acknowledgement?.Accepted != true)
        {
            OpsInvincibleCheck.IsChecked = confirmedInvincibility;
            SetPlayerOpsStatus(acknowledgement is null ? "Updated game receiver is not ready."
                : acknowledgement.Failure ?? "Player operation failed.", error: true);
            return;
        }
        if (requireState && acknowledgement.State is null)
        {
            OpsInvincibleCheck.IsChecked = confirmedInvincibility;
            SetPlayerOpsStatus("Player state is unavailable.", error: true);
            return;
        }
        if (acknowledgement.State is { } state)
        {
            if (updateFields) SetPlayerTestState(state.Normalized());
            else OpsInvincibleCheck.IsChecked = confirmedInvincibility = state.Invincible;
        }
        SetPlayerOpsStatus(acknowledgement.EnemiesRestored is not null ? "Enemies restored." : "Player state updated.");
    }

    private void SetPlayerOpsStatus(string message, bool error = false)
    {
        OpsStatusText.Text = initialization.Text(message);
        OpsStatusText.Foreground = error ? playerOpsErrorBrush : playerOpsStatusBrush;
    }

    private void SetPlayerTestState(PlayerTestState state)
    {
        OpsMaxHealthText.Text = state.MaxHealth.ToString(CultureInfo.InvariantCulture);
        OpsHealthText.Text = state.Health.ToString(CultureInfo.InvariantCulture);
        OpsLifebloodText.Text = state.LifebloodSeed.ToString(CultureInfo.InvariantCulture);
        OpsManaSlotsText.Text = state.ExtraManaSlots.ToString(CultureInfo.InvariantCulture);
        OpsManaText.Text = state.Mana.ToString(CultureInfo.InvariantCulture);
        OpsGeoText.Text = state.Geo.ToString(CultureInfo.InvariantCulture);
        OpsInvincibleCheck.IsChecked = confirmedInvincibility = state.Invincible;
    }
}
