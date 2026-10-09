using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

public static class AutoNavigationRunner
{
    private static readonly TimeSpan InputPulse = TimeSpan.FromMilliseconds(85);

    public static async Task<int> RunAsync(
        string? gameExecutable,
        int timeoutSeconds,
        int intervalMilliseconds)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        Console.CancelKeyPress += Cancel;
        try
        {
            var window = HollowKnightWindowLocator.FindBest();
            if (window is null)
            {
                var process = HollowKnightGameLauncher.Start(gameExecutable);
                WriteEvent("game-launched", new { processId = process.Id, executable = process.MainModule?.FileName });
                window = await WaitForWindowAsync(cancellation.Token).ConfigureAwait(false);
            }
            WriteEvent("game-window", new { window.ProcessId, window.Title, window.ClientWidth, window.ClientHeight });
            var foreground = HollowKnightWindowLocator.TryBringToForeground(window);
            WriteEvent("game-foreground", new { ok = foreground, window.ProcessId });
            if (!foreground)
            {
                throw new InvalidOperationException(
                    "Hollow Knight could not be brought to the foreground for visible-client capture.");
            }
            await Task.Delay(500, cancellation.Token).ConfigureAwait(false);

            await using var receiver = await ReceiverConnection
                .ConnectWithRetryAsync(cancellation.Token).ConfigureAwait(false);
            WriteEvent("receiver-ready", new { sessionID = receiver.SessionId });

            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation.Token);
            var monitor = new ReceiverMonitor();
            var monitorTask = monitor.RunAsync(receiver, monitorCancellation.Token);

            var detector = new GameStartupDetector();
            var coordinator = new GameStartupCoordinator();
            var capture = new GdiWindowCapture();
            long frameId = 0;
            GameStartupPhase? reportedPhase = null;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var latest = HollowKnightWindowLocator.FindBest()
                        ?? throw new InvalidOperationException("Hollow Knight window closed during startup.");
                    var native = capture.Capture(latest, ++frameId);
                    var frame = CpuBgraNormalizer.Normalize(native);
                    // The receiver is authoritative for gameplay readiness. The title
                    // screen can resemble the old visual HUD heuristic, so while a
                    // receiver session is active use pixels only to identify menus.
                    var evidence = monitor.GameplayObserved
                        ? new GameStartupEvidence(null, true)
                        : detector.Detect(frame, gameplayIsLatched: true);
                    var decision = coordinator.Observe(evidence);
                    if (reportedPhase != decision.Phase)
                    {
                        reportedPhase = decision.Phase;
                        WriteEvent("startup-phase", new
                        {
                            phase = decision.Phase.ToString(),
                            screen = evidence.Screen?.ToString(),
                            evidence.GameplayLikely,
                            evidence.SelectedOption,
                            frameId
                        });
                    }
                    if (decision.Action is not null)
                    {
                        try
                        {
                            var button = decision.Action == GameStartupAction.MoveSelectionUp
                                ? InputButtons.Up
                                : InputButtons.ActionZ;
                            await receiver.PulseAsync(button, InputPulse, cancellation.Token)
                                .ConfigureAwait(false);
                            WriteEvent("startup-action", new
                            {
                                action = decision.Action.ToString(),
                                evidence.SelectedOption,
                                frameId
                            });
                        }
                        catch
                        {
                            coordinator.Retry(decision.Action.Value);
                            throw;
                        }
                    }
                    if (decision.AdmitsWorldFrames)
                    {
                        WriteEvent("gameplay-ready", new { ok = true, frameId, processId = latest.ProcessId });
                        return 0;
                    }
                    await Task.Delay(intervalMilliseconds, cancellation.Token).ConfigureAwait(false);
                }
                return 7;
            }
            finally
            {
                monitorCancellation.Cancel();
                try
                {
                    await monitorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Automatic navigation timed out after {timeoutSeconds} seconds.");
            return 7;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static async Task<HollowKnightWindow> WaitForWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = HollowKnightWindowLocator.FindBest();
            if (window is not null) return window;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteEvent(string type, object details) => Console.WriteLine(
        System.Text.Json.JsonSerializer.Serialize(new { type, details }));

    private sealed class ReceiverMonitor
    {
        private int gameplayObserved;

        internal bool GameplayObserved => Volatile.Read(ref gameplayObserved) != 0;

        internal async Task RunAsync(
            ReceiverClient receiver,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await receiver.ReceiveLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("Receiver closed during automatic navigation.");
                if (ReceiverWireCodec.MessageType(line) != "groundTruth") continue;
                var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
                if (sample.HeroAvailable
                    && !string.IsNullOrWhiteSpace(sample.SceneName)
                    && sample.HasFiniteCoordinates())
                {
                    Volatile.Write(ref gameplayObserved, 1);
                }
            }
        }
    }
}
