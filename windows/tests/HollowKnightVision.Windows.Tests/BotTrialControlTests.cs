using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotTrialControlTests
{
    internal static void TestOptedInLocalTrialCommandsAndFraming() => Exercise().GetAwaiter().GetResult();

    private static async Task Exercise()
    {
        const string saved = "path-20261124-011722-00000000-0000-0000-0000-000000000001.json";
        foreach (var bad in new[] { "", "play", "restore-start:", "restore-start:..\\path-ok.json",
            "restore-start:path-outside/path-valid.json", "restore-start:path-c:drive.json", "restore-start:path-test.json\nstop" })
            Check(BotTrialCommand.Parse(bad) is null, "Reject unrelated commands and paths outside the recorded input library.");

        var pipe = "hkv-trial-test-" + Guid.NewGuid().ToString("N");
        var commands = new List<BotTrialCommand>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        await using (var server = new BotTrialControl((command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(command.Kind switch
            {
                BotTrialCommandKind.Status => new BotTrialReply(true, Status: JsonSerializer.SerializeToElement(new { ready = true })),
                BotTrialCommandKind.Return => new(false, "No marked point"),
                _ => new(true)
            });
        }, pipe))
        {
            using (var oversized = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await oversized.ConnectAsync(2000, token);
                await oversized.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 1024)), token);
                try { Check(await oversized.ReadAsync(new byte[1], token) == 0, "Close an oversized message before dispatching it."); }
                catch (IOException) { } // Windows may report a broken pipe rather than EOF.
            }
            var status = await BotTrialControl.SendAsync("status", pipe, token);
            Check(status.Accepted && status.Status?.GetProperty("ready").GetBoolean() == true, "The opted-in listener returns status.");
            Check((await BotTrialControl.SendAsync("stop", pipe, token)).Accepted
                && (await BotTrialControl.SendAsync("mark-return", pipe, token)).Accepted
                && (await BotTrialControl.SendAsync("return", pipe, token)) is { Accepted: false, Issue: "No marked point" }
                && (await BotTrialControl.SendAsync("restore-start:" + saved, pipe, token)).Accepted,
                "Forward bounded commands and preserve command acceptance or rejection.");
            Check(commands.Select(command => command.Kind).SequenceEqual(Enum.GetValues<BotTrialCommandKind>())
                && commands[^1].FileName == saved, "Parse exactly the supported command set and saved input filename.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { _ = await BotTrialControl.SendAsync("status", pipe, cancelled.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A stopped transport must honor cancellation.");
    }

    internal static async Task TestHostExecutable(string host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        await using var listener = new BotTrialControl((command, _) => Task.FromResult(command.Kind switch
        {
            BotTrialCommandKind.Status => new BotTrialReply(true, Status: JsonSerializer.SerializeToElement(new { phase = "playing" })),
            _ => new(false, "No return point")
        }));
        var status = await Send("status", enabled: true);
        Check(status.Exit == 0 && status.Output.Contains("\"phase\":\"playing\"", StringComparison.Ordinal), "The installed Host emits trial status.");
        var refused = await Send("return", enabled: true);
        Check(refused.Exit == 1 && refused.Output.Trim() == "rejected" && refused.Error.Contains("No return point"),
            "Return the opted-in desktop's rejection and propagate its failure status.");
        Check((await Send("stop", enabled: false)).Exit == 64, "The client requires explicit automation consent.");

        async Task<(int Exit, string Output, string Error)> Send(string command, bool enabled)
        {
            var assembly = Path.GetExtension(host).Equals(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(assembly ? "dotnet" : host)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (assembly) start.ArgumentList.Add(host);
            start.ArgumentList.Add("--bot-demo-control");
            start.ArgumentList.Add(command);
            if (enabled) start.ArgumentList.Add("--enable-automation-control");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token);
                return (process.ExitCode, await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
