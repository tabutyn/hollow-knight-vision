using System.Diagnostics;

internal static class NativeHostTestProcess
{
    internal static int Run(string executable, string[] arguments, TextWriter output, TextWriter error)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native Host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The native Host did not finish {arguments.FirstOrDefault()}.");
        }
        output.Write(stdout.GetAwaiter().GetResult());
        error.Write(stderr.GetAwaiter().GetResult());
        return process.ExitCode;
    }
}
