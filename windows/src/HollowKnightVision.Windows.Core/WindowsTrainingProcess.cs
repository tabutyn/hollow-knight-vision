using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HollowKnightVision.Windows.Core;

public sealed record TrainingProcessResult(int ExitCode, bool Cancelled, TrainingOutputSnapshot Output);

/// <summary>Runs a cooperating launcher inside a kill-on-close job before permitting it to create children.</summary>
public static class WindowsTrainingProcess
{
    public const string StartSignalVariable = "HKV_TRAINER_START_SIGNAL";
    public static async Task<TrainingProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellation,
        Action<TrainingOutputSnapshot>? outputChanged = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Training job containment requires Windows.");
        cancellation.ThrowIfCancellationRequested();
        // The launcher opens this gate before it starts Git/Python or any other child.
        var signalName = "Local\\HKV-training-start-" + Guid.NewGuid().ToString("N");
        using var startSignal = new EventWaitHandle(false, EventResetMode.ManualReset, signalName);
        using var job = WindowsTrainingJob.Create();
        startInfo.UseShellExecute = false; startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        startInfo.Environment[StartSignalVariable] = signalName;
        using var process = new Process { StartInfo = startInfo };
        var output = new TrainingOutputBuffer(); var published = 0L; var publicationGate = new object();
        void Publish(TrainingOutputSnapshot value)
        {
            lock (publicationGate)
            {
                if (value.Sequence <= published) return; published = value.Sequence;
                try { outputChanged?.Invoke(value); } catch (Exception) { /* A closing UI observer must not orphan the trainer. */ }
            }
        }
        async Task Drain(Stream stream, bool standardError)
        {
            try
            {
                var bytes = new byte[4096]; int count;
                while ((count = await stream.ReadAsync(bytes).ConfigureAwait(false)) != 0) Publish(output.Append(bytes.AsSpan(0, count), standardError));
                Publish(output.Complete(standardError));
            }
            catch { job.Dispose(); throw; } // A broken reader must not leave a writer blocked on a full pipe.
        }
        if (!process.Start()) throw new InvalidOperationException("Trainer did not start.");
        try { job.Assign(process); }
        catch
        {
            // The cooperating launcher is still at its start gate and has no children.
            if (!process.HasExited) process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); throw;
        }
        using var registration = cancellation.Register(job.Dispose);
        var stdout = Drain(process.StandardOutput.BaseStream, false); var stderr = Drain(process.StandardError.BaseStream, true);
        try
        {
            startSignal.Set();
            await process.WaitForExitAsync().ConfigureAwait(false);
            job.Dispose(); // Also terminate unexpected descendants retaining redirected pipes after the launcher exits.
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new(process.ExitCode, cancellation.IsCancellationRequested, output.Current);
        }
        finally
        {
            job.Dispose();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }
}

internal sealed class WindowsTrainingJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private WindowsTrainingJob() : base(true) { }
    public static WindowsTrainingJob Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error); }
        return job;
    }
    public void Assign(Process process)
    { if (!AssignProcessToJobObject(this, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain the training process tree."); }
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern WindowsTrainingJob CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(WindowsTrainingJob job, int infoClass, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(WindowsTrainingJob job, SafeProcessHandle process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
