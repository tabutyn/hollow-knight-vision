using HollowKnightVision.Windows.Core;
using ILGPU;
using ILGPU.Runtime.CPU;

internal static class ScenePatchAcceleratorTests
{
    internal static void TestKernelAndCpuAgreeForContrastMasksAndAmbiguity()
    {
        using var context = Context.Create(builder => builder.CPU());
        using var device = context.CreateCPUAccelerator(0);
        using var scorer = new ScenePatchAccelerator(device);
        var fixture = new Fixture();
        CheckFits(fixture, scorer);
        var search = new ScenePatchAccelerator.Search(160, 90, 3, -4, 2, 2);
        var scores = new double[fixture.Points.Length * 25];
        scorer.Score(fixture.Source, fixture.Target, fixture.Points, search, scores);
        Check(scores[..25].All(score => score == -2)
            && scores[25 + 12] > .999 && scores[50 + 12] > .999, "The kernel must score the known displacements and enforce the target mask.");
    }

    internal static void TestCudaSceneryParityWhenAvailable()
    {
        if (!GroundPatchMatcher.Shared.CudaAvailable && Environment.GetEnvironmentVariable("HKV_REQUIRE_CUDA") == "1")
            throw new InvalidOperationException($"CUDA scenery verification requires a usable device. {GroundPatchMatcher.Shared.CudaFailure}");
        var before = GroundPatchMatcher.CudaSceneDispatchCount;
        var fixture = new Fixture();
        CheckFits(fixture, null);
        var dispatches = GroundPatchMatcher.CudaSceneDispatchCount - before;
        if (GroundPatchMatcher.Shared.CudaAvailable)
        {
            Check(dispatches == 4, $"Expected four CUDA scenery dispatches, received {dispatches}. {GroundPatchMatcher.CudaSceneFailure}");
            Console.WriteLine($"CUDA scenery on {GroundPatchMatcher.Shared.CudaDeviceName}: {dispatches} dispatches.");
        }
        else
        {
            Check(dispatches == 0, "The fallback may not report CUDA execution.");
            Console.WriteLine("CUDA scenery device unavailable. Verified CPU fallback.");
        }
    }

    private static void CheckFits(Fixture fixture, ScenePatchAccelerator? scorer)
    {
        var points = fixture.Points;
        foreach (CameraVector? expected in new CameraVector?[] { null, new(3, -4) })
        {
            var cpu = CalibratedSceneMotionTracker.FitBatch(fixture.Source, fixture.Target, points, expected, GroundComputeBackend.Cpu);
            var gpu = CalibratedSceneMotionTracker.FitBatch(fixture.Source, fixture.Target, points, expected, GroundComputeBackend.Cuda, scorer);
            Check(cpu[0] is null && cpu[1] is not null && cpu[2] is not null, "The fixture must provide one masked and two registered patches.");
            for (var index = 0; index < points.Length; index++)
            {
                Check(cpu[index].HasValue == gpu[index].HasValue, "Scoring backends disagreed on acceptance.");
                if (cpu[index] is not { } a || gpu[index] is not { } b) continue;
                Check(Math.Abs(a.Motion.X - b.Motion.X) < .001 && Math.Abs(a.Motion.Y - b.Motion.Y) < .001
                    && Math.Abs(a.Correlation - b.Correlation) < .001, "Scoring backends disagreed on displacement or correlation.");
                Check(Math.Abs(b.Motion.X - 3) < .01 && Math.Abs(b.Motion.Y + 4) < .01, "The known scene translation was not recovered.");
            }
        }
        foreach (var grid in new[] { fixture.Flat, fixture.Repeating })
        {
            var cpu = CalibratedSceneMotionTracker.FitBatch(grid, grid, points, backend: GroundComputeBackend.Cpu);
            var gpu = CalibratedSceneMotionTracker.FitBatch(grid, grid, points, backend: GroundComputeBackend.Cuda, testAccelerator: scorer);
            Check(cpu.All(fit => fit is null) && gpu.All(fit => fit is null),
                "Flat texture and repeat ambiguity must be rejected on both backends.");
        }
    }

    private sealed class Fixture
    {
        internal AtlasPoint[] Points { get; } = [new(20, 20), new(60, 50), new(120, 70)];
        internal CalibratedSceneMotionTracker.Grid Source { get; }
        internal CalibratedSceneMotionTracker.Grid Target { get; }
        internal CalibratedSceneMotionTracker.Grid Flat { get; }
        internal CalibratedSceneMotionTracker.Grid Repeating { get; }

        internal Fixture()
        {
            const int width = 160, height = 90;
            var random = new Random(8135);
            var source = Enumerable.Range(0, width * height).Select(_ => (float)random.Next(30, 220)).ToArray();
            var repeat = Enumerable.Range(0, 20).Select(_ => (float)random.Next(30, 220)).ToArray();
            var target = new float[width * height];
            for (var y = 0; y < height - 4; y++)
            for (var x = 3; x < width; x++)
                target[y * width + x] = source[(y + 4) * width + x - 3] * .7f + 14;
            Source = new(width, height, source, []);
            Target = new(width, height, target, [new(0, 0, 40, 40)]);
            Flat = new(width, height, new float[width * height], []);
            Repeating = new(width, height, Enumerable.Range(0, width * height)
                .Select(index => repeat[(index / width % 5) * 4 + index % width % 4]).ToArray(), []);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
