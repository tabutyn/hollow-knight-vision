using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using System.Text.Json;

internal static class ReplayLifecycleTests
{
    internal static void Run() => Task.Run(async () => {
        var worker = new SerialWorldWorker();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = 0;
        var save = worker.Exclusive(() => { Interlocked.Increment(ref active); started.Set(); release.Wait(); Interlocked.Decrement(ref active); return true; });
        Check(started.Wait(3000), "Slow persistence never started.");
        var applied = new System.Collections.Concurrent.ConcurrentQueue<int>();
        for (var i = 0; i < 100; i++)
        {
            var frame = i;
            worker.Submit(() => { Check(active == 0, "World write overlapped directory swap."); applied.Enqueue(frame); });
            await Task.Yield(); // Camera analysis remains runnable while persistence is blocked.
        }
        Check(!save.IsCompleted && applied.Count == 0, "World work escaped persistence serialization.");
        release.Set(); await save;
        var until = DateTime.UtcNow.AddSeconds(3);
        while (applied.Count == 0 && DateTime.UtcNow < until) await Task.Delay(10);
        await worker.DrainAsync();
        Check(applied.SequenceEqual(new[] { 99 }), "Slow save accumulated stale frame work.");
        var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>("""{"version":2,"type":"groundTruth","sceneName":"Tutorial_01","heroAvailable":true,"cameraAvailable":true,"grounded":true} """);
        var checkpoint = JsonSerializer.Deserialize<RecordedGameCheckpoint>("""{"sceneName":"Tutorial_01","grounded":true} """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var gate = new ReplayReadiness();
        for (var i = 0; i < 3; i++) Check(!gate.Observe(checkpoint, sample with { UnityFrame = i }, 1 + i * .1, 1 + i * .1, true, true), "Readiness accepted fewer than four samples.");
        Check(!gate.Observe(checkpoint, sample with { UnityFrame = 3, HeroX = 5 }, 1.3, 1.3, true, true), "Readiness accepted wrong player position.");
        for (var i = 4; i < 7; i++) Check(!gate.Observe(checkpoint, sample with { UnityFrame = i }, 1 + i * .1, 1 + i * .1, true, true), "Readiness did not reset its consecutive count.");
        Check(gate.Observe(checkpoint, sample with { UnityFrame = 7 }, 1.7, 1.7, true, true), "Stable checkpoint never became ready.");
        var missingWorld = new ReplayReadiness();
        for (var i = 0; i < 8; i++) Check(!missingWorld.Observe(checkpoint, sample with { UnityFrame = i }, 2 + i * .1, 1, true, false), "Stale vision/no committed evidence passed readiness.");
        Check(!ReplayReadiness.Matches(checkpoint, sample with { CameraY = .1 }), "Readiness ignored camera easing.");
        Console.WriteLine("PASS isolated replay readiness and bounded world work during slow persistence");
    }).GetAwaiter().GetResult();
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
