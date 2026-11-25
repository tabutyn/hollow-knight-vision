using System.Diagnostics;
using System.IO;
using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly GameTextReader gameTextReader;
    private readonly string gameTextPreference;
    private readonly WindowsGameTextRecognition gameTextRecognition = new();
    private GameTextDebugWindow? gameTextWindow;
    private Task? gameTextTask;
    private string? gameTextScene;

    private void InitializeGameText()
    {
        try
        {
            if (File.Exists(gameTextPreference)
                && bool.TryParse(File.ReadAllText(gameTextPreference), out var enabled))
                gameTextReader.SetEnabled(enabled);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not read the game text preference: " + error.Message);
        }
        ReadGameTextCheck.IsChecked = gameTextReader.Enabled;
        PresentGameText();
    }

    private void ReadGameTextCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SetGameTextEnabled(ReadGameTextCheck.IsChecked == true);
    }

    private void SetGameTextEnabled(bool enabled)
    {
        if (gameTextReader.Enabled != enabled)
        {
            gameTextReader.SetEnabled(enabled);
            if (!enabled)
            {
                StopGameTextReview();
                Volatile.Write(ref latestThinkingReading, null);
            }
            if (!enabled && gameTextTask?.IsCompleted != false) gameTextRecognition.Dispose();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(gameTextPreference)!);
                File.WriteAllText(gameTextPreference, enabled.ToString());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowError(error); }
        }
        ReadGameTextCheck.IsChecked = enabled;
        PresentGameText();
    }

    private void GameTextLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GameTextDebugWindow(SetGameTextEnabled, SetGameTextReviewOptions, SetGameTextGoal) { Owner = this };
        gameTextWindow = dialog;
        PresentGameText();
        try { dialog.ShowDialog(); }
        finally { gameTextWindow = null; }
    }

    private void SelectGameTextProfile(SaveProfileAtlas? profile)
    {
        if (gameTextReader.Profile != profile)
        {
            StopGameTextReview();
            ResetThinking();
            Volatile.Write(ref latestThinkingReading, null);
        }
        gameTextReader.SelectProfile(profile);
        PresentGameText();
    }

    private void ResetGameTextScan()
    {
        gameTextReader.ResetFrame();
        gameTextScene = null;
        Volatile.Write(ref latestThinkingReading, null);
        PresentGameText();
    }

    private void ObserveGameText(VisionAnalysisResult result)
    {
        if (isClosing || visionCancellation is not { IsCancellationRequested: false }
            || inputPathReplayCancellation is not null) return;
        if (gameTextReader.Profile is not null && result.MenuStencil is { IsMatch: true,
            ContextIdentifier: "main-title" or "select-profile" or "clear-save" })
            SelectGameTextProfile(null);
        gameTextScene = result.MenuContext == "game" ? result.ReceiverTelemetry?.SceneName ?? gameTextScene : null;
        var context = result.ReadingOpeningCutscene ? GameTextNotebook.OpeningContext : result.MenuContext switch
        {
            null => "Game capture",
            "game" => gameTextScene ?? "Gameplay",
            var menu => ContextDisplayName(menu)
        };
        var previous = gameTextReader.Scan;
        var work = gameTextReader.TryStart(result.Frame, initialization.LanguageIdentifier, context, result.CaptureTimestamp);
        if (!ReferenceEquals(previous, gameTextReader.Scan))
        {
            Volatile.Write(ref latestThinkingReading, gameTextReader.LatestReading);
            PresentGameText();
        }
        if (work is not null) gameTextTask = ReadGameTextAsync(work);
        StartGameTextReview();
    }

    private async Task ReadGameTextAsync(GameTextReader.Work work)
    {
        IReadOnlyList<GameTextLine> lines = [];
        string? issue = null;
        var start = Stopwatch.GetTimestamp();
        try { lines = await Task.Run(() => gameTextRecognition.Read(work.Image, work.Language)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or DllNotFoundException or BadImageFormatException or TypeInitializationException
            or Tesseract.TesseractException or InvalidOperationException)
        {
            issue = "Game text reading failed. " + error.GetBaseException().Message;
        }
        if (!gameTextReader.Enabled) gameTextRecognition.Dispose();
        if (gameTextReader.Complete(work, lines, Stopwatch.GetElapsedTime(start).TotalMilliseconds, BotCaptureClock(), issue))
        {
            Volatile.Write(ref latestThinkingReading, gameTextReader.LatestReading);
            PresentGameText();
        }
    }
}
