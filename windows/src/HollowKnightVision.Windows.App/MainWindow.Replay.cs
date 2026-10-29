using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record ReplayVision(double Timestamp, long Generation, bool GroundReady, int Width, int Height);
    private ReplayVision? replayVision;
    private bool replayFailFast;
    private TaskCompletionSource? replayUiFinished;

    private void ReplayEvaluation_Click(object sender, RoutedEventArgs e)
    {
        replayFailFast = ((MenuItem)sender).Tag?.ToString() == "fail-fast";
        ReplayFullTraceItem.IsChecked = !replayFailFast;
        ReplayFailFastItem.IsChecked = replayFailFast;
    }

    private void ObserveReplayDecision(PlaybackDivergenceDecision? decision)
    {
        if (decision is null) return;
        Interlocked.CompareExchange(ref inputPathReplayDivergence, decision, null);
        if (replayFailFast) inputPathReplayCancellation?.Cancel();
    }

    private async Task StopReplayCaptureAsync()
    {
        StopVision();
        if (visionRunCompletion is { } capture) await capture;
        await worldWorker.DrainAsync();
        await AwaitAtlasPersistenceAsync();
        Volatile.Write(ref replayVision, null);
    }

    private async Task ResetReplayAtlasAsync(string directory, CancellationToken token, bool startCapture = true)
    {
        await StopReplayCaptureAsync();
        token.ThrowIfCancellationRequested();
        atlasStateStore = new AtlasStateStore(Path.Combine(directory, "states"));
        activeAtlasDirectory = Path.Combine(directory, "active");
        gameplayAtlas = new(pixelsPerWorldUnit: 1, sourceTopInsetFraction: 0);
        activeAtlasCreatedAt = DateTimeOffset.UtcNow;
        activeAtlasPersistenceBlocked = false;
        Volatile.Write(ref gameplayAtlasRequiresFullRewrite, false);
        liveWorldPipeline = await Task.Run(() => {
            _ = WriteAtlasGeneration(new AtlasMosaicDelta(gameplayAtlas.Revision, []),
                new VisionResetController.PersistedAtlasMetadata(0, activeAtlasCreatedAt, gameplayAtlas.TileSize, gameplayAtlas.PixelsPerWorldUnit),
                copyActiveWorld: false, retainPrevious: false);
            return new WindowsLiveWorldPipeline(Path.Combine(activeAtlasDirectory, "live-world"));
        }, token);
        ClearGameplayAtlasRuntime(reopenLiveWorld: false);
        if (startCapture) StartVision_Click(this, new RoutedEventArgs());
    }

    private async Task WaitForReplayReadyAsync(RecordedGameCheckpoint checkpoint, CancellationToken token)
    {
        var readiness = new ReplayReadiness();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            token.ThrowIfCancellationRequested();
            var vision = Volatile.Read(ref replayVision);
            var truth = gameControls.TryGetFreshGroundTruth(TimeSpan.FromMilliseconds(500), out var sample) ? sample : null;
            var world = Volatile.Read(ref completedWorld);
            var generation = Interlocked.Read(ref recentCaptureGeneration);
            if (readiness.Observe(checkpoint, truth, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
                vision?.Timestamp ?? 0, vision?.Generation == generation && vision.GroundReady,
                world?.Frame.CaptureGeneration == (ulong)generation && gameplayAtlas.TileCount > 0
                    && world.Result.State is WindowsLiveWorldPipelineState.Committed or WindowsLiveWorldPipelineState.Throttled)) return;
            SetStatus($"Preparing replay: waiting for {readiness.WaitingFor}…");
            await Task.Delay(100, token);
        }
        throw new TimeoutException($"Replay preparation timed out waiting for {readiness.WaitingFor}. No input path was started.");
    }
}
