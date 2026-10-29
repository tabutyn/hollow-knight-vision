using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HollowKnightVision.Windows.App;

internal sealed record ReplayFrameAuditSample(string Kind, string Workspace, int? GameFrame,
    long CaptureId, double CapturedAt, double ObservedAt, int Width, int Height,
    double? CameraX = null, double? CameraY = null, bool Registered = false);

/// <summary>In-memory observations only; write once after playback, never on the frame path.</summary>
internal sealed class ReplayFrameAudit(double startedAt, string workspace)
{
    private readonly ConcurrentQueue<ReplayFrameAuditSample> samples = new();
    private int count, omitted;
    internal void Append(ReplayFrameAuditSample sample)
    {
        if (Interlocked.Increment(ref count) <= 250_000) samples.Enqueue(sample);
        else Interlocked.Increment(ref omitted);
    }
    internal void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, startedAt, workspace, endedAt = Now(), omitted,
            timing = "monotonic seconds; presentation sampled at WPF CompositionTarget.Rendering, not monitor scanout",
            units = "capture-resolution camera pixels, world Y up", samples = samples.ToArray()
        }));
    }
    internal static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
