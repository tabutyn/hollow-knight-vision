using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HollowKnightVision.Windows.Core;
using static TrainingPresentationTests;

internal static class TrainingProcessTests
{
    public static async Task<bool> TryRunProbeAsync(string[] args)
    {
        if (args.Length < 2 || args[0] != "--training-probe") return false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (Environment.GetEnvironmentVariable(WindowsTrainingProcess.StartSignalVariable) is { } signalName)
        {
            using var signal = EventWaitHandle.OpenExisting(signalName);
            if (!signal.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException("Training probe gate did not open.");
        }
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args[1];
        if (mode == "child")
        {
            Console.WriteLine($"CHILD_READY={Environment.ProcessId}"); Console.Out.Flush();
            await Task.Delay(Timeout.Infinite); return true;
        }
        if (mode is "wait" or "parent-exits")
        {
            if (args.Length > 2) Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(args[2])!, ".staging-" + Path.GetFileName(args[2])));
            var childStart = Start("child"); childStart.RedirectStandardOutput = mode == "wait";
            using var child = Process.Start(childStart) ?? throw new Exception("Probe child did not start.");
            if (mode == "wait") Console.WriteLine(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Console.WriteLine($"ROOT={Environment.ProcessId} CHILD={child.Id}"); Console.Out.Flush();
            if (mode == "wait") await Task.Delay(Timeout.Infinite);
            return true;
        }
        if (mode is "fail" or "complete-then-wait")
        {
            var output = args[2];
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(output)!, ".staging-" + Path.GetFileName(output)));
            File.WriteAllText(Path.Combine(output, "training.json"), "incomplete probe output");
            Console.WriteLine("OUTPUT_READY"); Console.Out.Flush();
            if (mode == "complete-then-wait") await Task.Delay(Timeout.Infinite);
            Console.Error.Write("specific training failure"); Environment.ExitCode = 17; return true;
        }
        using var stream = Console.OpenStandardOutput(); var bytes = Encoding.UTF8.GetBytes("HKV_STAGE name=training\nCompleted 🐛\n");
        foreach (var value in bytes) { stream.WriteByte(value); stream.Flush(); }
        Console.Error.Write("stderr detail without newline"); return true;
    }

    private static ProcessStartInfo Start(string mode, string? output = null)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(TrainingProcessTests).Assembly.Location);
        start.ArgumentList.Add("--training-probe"); start.ArgumentList.Add(mode);
        if (output is not null) start.ArgumentList.Add(output);
        return start;
    }
    public static void TestSuccessAndObserverFailure() => Success().GetAwaiter().GetResult();
    private static async Task Success()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        long sequence = 0; var callbacks = 0; var ordered = true;
        var result = await WindowsTrainingProcess.RunAsync(Start("success"), timeout.Token, snapshot =>
        { ordered &= snapshot.Sequence > sequence; sequence = snapshot.Sequence; callbacks++; throw new InvalidOperationException("Disposed observer"); });
        Check(result.ExitCode == 0 && !result.Cancelled && callbacks > 0 && ordered);
        Check(result.Output.ProgressText == "Starting training…" && result.Output.Text.Contains("stderr detail without newline") && result.Output.Text.Contains("Completed 🐛"));
        Check(result.Output.Sequence == sequence);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await WindowsTrainingProcess.RunAsync(Start("success"), cancelled.Token); throw new Exception("Pre-cancelled run started."); }
        catch (OperationCanceledException) { }
        try { await WindowsTrainingProcess.RunAsync(new ProcessStartInfo("hkv-intentionally-missing-probe.exe"), timeout.Token); throw new Exception("Missing launcher accepted."); }
        catch (System.ComponentModel.Win32Exception) { }
    }
    public static void TestCancellationAndDescendants() => CancelTree().GetAwaiter().GetResult();
    private static async Task CancelTree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("hkv-training-tree-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ready = new TaskCompletionSource<TrainingOutputSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new TrainingRunWorkspace(Path.Combine(root, Guid.NewGuid().ToString("N")));
            var running = workspace.RunAsync(Start("wait", workspace.OutputDirectory), cancellation.Token, value =>
            { if (value.Text.Contains("ROOT=") && value.Text.Contains("CHILD_READY=")) ready.TrySetResult(value); });
            var completed = await Task.WhenAny(ready.Task, running);
            if (completed == running) throw new Exception("Probe ended before readiness: " + (await running).Output.Text);
            TrainingOutputSnapshot snapshot;
            try { snapshot = await ready.Task.WaitAsync(cancellation.Token); }
            finally { cancellation.Cancel(); }
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Cancelled && !Directory.Exists(workspace.StagingDirectory));
            Check(Directory.GetDirectories(root).Single().Contains(".interrupted-"));
            AssertProcessesExited(snapshot.Text);
            // An unexpected launcher exit must also close inherited pipes and stop descendants.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var exited = await WindowsTrainingProcess.RunAsync(Start("parent-exits"), limit.Token);
            Check(exited.ExitCode == 0 && !exited.Cancelled); AssertProcessesExited(exited.Output.Text);
        }
        finally { Directory.Delete(root, true); }
    }
    public static void TestInterruptedOutputIsolation() => Interrupted().GetAwaiter().GetResult();
    private static async Task Interrupted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("hkv-training-output-").FullName;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var workspace = new TrainingRunWorkspace(Path.Combine(root, "failed"));
            var failed = await workspace.RunAsync(Start("fail", workspace.OutputDirectory), timeout.Token);
            Check(failed.ExitCode == 17 && !failed.Cancelled && failed.Output.Text.Contains("specific training failure"));
            Check(!Directory.Exists(workspace.OutputDirectory) && !Directory.Exists(workspace.StagingDirectory));
            Check(Directory.GetDirectories(root).Length == 2 && Directory.GetFiles(root, "training.json", SearchOption.AllDirectories).Length == 1);
            Check(new LabelingModelReviewStore(root, root).LoadCandidates().Count == 0);
            workspace = new TrainingRunWorkspace(Path.Combine(root, "cancelled-after-export"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = workspace.RunAsync(Start("complete-then-wait", workspace.OutputDirectory), stop.Token,
                value => { if (value.Text.Contains("OUTPUT_READY")) ready.TrySetResult(); });
            await ready.Task.WaitAsync(stop.Token); stop.Cancel();
            Check((await task.WaitAsync(TimeSpan.FromSeconds(10))).Cancelled);
            Check(new LabelingModelReviewStore(root, root).LoadCandidates().Count == 0 && !Directory.Exists(workspace.OutputDirectory));
            var occupied = Path.Combine(root, "occupied"); Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "keep.txt"), "unchanged");
            try { _ = new TrainingRunWorkspace(occupied); throw new Exception("Occupied run accepted."); } catch (IOException) { }
            Check(File.ReadAllText(Path.Combine(occupied, "keep.txt")) == "unchanged");
            var raced = new TrainingRunWorkspace(Path.Combine(root, "raced")); Directory.CreateDirectory(raced.StagingDirectory);
            try { await raced.RunAsync(Start("success"), timeout.Token); throw new Exception("Occupied staging accepted."); } catch (IOException) { }
            Check(Directory.Exists(raced.StagingDirectory));
        }
        finally { Directory.Delete(root, true); }
    }
    private static void AssertProcessesExited(string text)
    {
        var ids = Regex.Matches(text, @"(?:ROOT|CHILD)=(\d+)").Select(match => int.Parse(match.Groups[1].Value)).Distinct().ToArray();
        Check(ids.Length == 2, "Probe must identify exactly its root and child.");
        foreach (var id in ids)
        {
            try { using var process = Process.GetProcessById(id); Check(process.WaitForExit(5000), $"Training descendant {id} survived its run."); }
            catch (ArgumentException) { } // Already reaped.
        }
    }

    public static void TestIndependentJobs() => IndependentJobs().GetAwaiter().GetResult();
    private static async Task IndependentJobs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<TrainingOutputSnapshot> Ready(TaskCompletionSource<string> ready) => value =>
        { if (value.Text.Contains("ROOT=") && value.Text.Contains("CHILD_READY=")) ready.TrySetResult(value.Text); };
        var first = WindowsTrainingProcess.RunAsync(Start("wait"), firstStop.Token, Ready(firstReady));
        var second = WindowsTrainingProcess.RunAsync(Start("wait"), secondStop.Token, Ready(secondReady));
        try
        {
            var firstText = await firstReady.Task.WaitAsync(firstStop.Token);
            var secondText = await secondReady.Task.WaitAsync(secondStop.Token);
            firstStop.Cancel(); Check((await first.WaitAsync(TimeSpan.FromSeconds(10))).Cancelled); AssertProcessesExited(firstText);
            Check(!second.IsCompleted, "Cancelling one run killed an unrelated run.");
            secondStop.Cancel(); Check((await second.WaitAsync(TimeSpan.FromSeconds(10))).Cancelled); AssertProcessesExited(secondText);
        }
        finally
        {
            firstStop.Cancel(); secondStop.Cancel();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
