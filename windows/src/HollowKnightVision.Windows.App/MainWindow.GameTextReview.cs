using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly GameTextReviewSession gameTextReview;
    private readonly string gameTextReviewPreference;
    private GameTextReviewOptions gameTextReviewOptions = new();
    private CancellationTokenSource? gameTextReviewCancellation;
    private Task? gameTextReviewTask;
    private string? gameTextReviewStatus;

    private void InitializeGameTextReview()
    {
        try
        {
            if (File.Exists(gameTextReviewPreference))
                gameTextReviewOptions = JsonSerializer.Deserialize<GameTextReviewOptions>(File.ReadAllBytes(gameTextReviewPreference))
                    ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not read the game text review preference: " + error.Message);
        }
    }

    private void SetGameTextReviewOptions(GameTextReviewOptions options)
    {
        if (gameTextReviewOptions == options) return;
        gameTextReviewOptions = options;
        StopGameTextReview();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(gameTextReviewPreference)!);
            File.WriteAllText(gameTextReviewPreference, JsonSerializer.Serialize(options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowError(error); }
        PresentGameText();
        StartGameTextReview();
    }

    private void SetGameTextGoal(string goal)
    {
        try { gameTextReader.SetGoal(goal); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowError(error); }
        PresentGameText();
    }

    private void PresentGameText() =>
        gameTextWindow?.Present(gameTextReader, gameTextReviewOptions, gameTextReviewStatus);

    private void StartGameTextReview()
    {
        if (isClosing || !gameTextReviewOptions.Enabled || !gameTextReader.Enabled
            || thinkingQuestionTask is { IsCompleted: false }
            || visionCancellation is not { IsCancellationRequested: false } vision
            || gameTextReviewTask is { IsCompleted: false }) return;
        var source = CancellationTokenSource.CreateLinkedTokenSource(vision.Token);
        gameTextReviewCancellation = source;
        gameTextReviewTask = RunGameTextReviewAsync(gameTextReviewOptions, source);
    }

    private void StopGameTextReview()
    {
        gameTextReviewCancellation?.Cancel();
        gameTextReviewStatus = null;
    }

    private async Task RunGameTextReviewAsync(GameTextReviewOptions options, CancellationTokenSource source)
    {
        try
        {
            while (!source.IsCancellationRequested)
            {
                var delay = await gameTextReview.ReviewAsync(options, status =>
                {
                    gameTextReviewStatus = status;
                    PresentGameText();
                }, source.Token);
                await Task.Delay(delay, source.Token);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(gameTextReviewCancellation, source)) gameTextReviewCancellation = null;
            source.Dispose();
        }
    }
}
