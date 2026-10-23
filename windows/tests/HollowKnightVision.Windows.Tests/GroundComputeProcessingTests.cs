using HollowKnightVision.Windows.Core;

internal static class GroundComputeProcessingTests
{
    private sealed record SearchFixture(
        GroundPatchSearchSource[] Sources,
        byte[] Pixels,
        GroundPatchSearchCandidate[] Candidates);

    internal static void TestParallelGroundImageProcessingMatchesCpuExactly()
    {
        const int width = 640, height = 360;
        var luma = Enumerable.Range(0, width * height)
            .Select(index => unchecked((byte)(index * 73 ^ index / width * 131)))
            .ToArray();
        var validity = Enumerable.Repeat((byte)1, luma.Length).ToArray();
        for (var row = 40; row <= 100; row++)
        for (var x = 50; x <= 170; x++)
            validity[row * width + x] = 0;

        var processor = new GroundImageProcessor();
        var cpu = Require(processor.Process(
            luma, width, height, validity, null, GroundComputeBackend.Cpu));
        var parallel = Require(processor.Process(
            luma, width, height, validity, null, GroundComputeBackend.ParallelCpu));
        var automatic = Require(processor.Process(
            luma, width, height, validity, null, GroundComputeBackend.Automatic));

        Check(parallel.Backend == GroundComputeBackend.ParallelCpu,
            "Explicit parallel ground-image processing did not use its requested backend.");
        Check(automatic.Backend == GroundComputeBackend.Cpu,
            "Automatic ground-image processing should avoid parallel dispatch overhead.");
        Check(parallel.GroundEdges.SequenceEqual(cpu.GroundEdges),
            "Parallel edge pixels differ from the CPU reference.");
        Check(parallel.GroundPixels.SequenceEqual(cpu.GroundPixels),
            "Parallel ground pixels differ from the CPU reference.");
        Check(automatic.GroundEdges.SequenceEqual(cpu.GroundEdges)
            && automatic.GroundPixels.SequenceEqual(cpu.GroundPixels),
            "Automatic ground-image processing differs from the CPU reference.");
    }

    internal static void TestParallelPatchSearchMatchesCpuSelection()
    {
        var fixture = MakeSearchFixture(96);
        var matcher = new GroundPatchMatcher();
        var cpu = matcher.Search(
            fixture.Sources, fixture.Pixels, 640, 360, fixture.Candidates,
            GroundComputeBackend.Cpu);
        var parallel = matcher.Search(
            fixture.Sources, fixture.Pixels, 640, 360, fixture.Candidates,
            GroundComputeBackend.ParallelCpu);

        Check(parallel.Backend == GroundComputeBackend.ParallelCpu,
            "Patch search did not report the parallel backend.");
        Check(parallel.Results.Count == cpu.Results.Count,
            "Parallel patch search returned a different candidate count.");
        for (var index = 0; index < cpu.Results.Count; index++)
        {
            var expected = cpu.Results[index];
            var actual = parallel.Results[index];
            Check((actual.Dx, actual.Dy, actual.Support)
                == (expected.Dx, expected.Dy, expected.Support),
                $"Parallel candidate {index} differs from CPU selection.");
            Check(Math.Abs(actual.Error - expected.Error) <= .002,
                $"Parallel candidate {index} error differs from CPU: {actual.Error} vs {expected.Error}.");
        }
    }

    internal static void TestWiderParallelSearchRecoversMotionOutsideFormerRadius()
    {
        const int width = 640, height = 200;
        var line = new[] { new GroundTextureLine(80, 16, 623) };
        var former = new GroundCameraSolver(
            matcherBackend: GroundComputeBackend.Cpu,
            maximumLocalSearchRadius: 96);
        var widened = new GroundCameraSolver(
            matcherBackend: GroundComputeBackend.Automatic,
            maximumLocalSearchRadius: 192);
        _ = former.Solve(MotionPixels(width, height, 0, 0), width, height, line, 0, []);
        _ = widened.Solve(MotionPixels(width, height, 0, 0), width, height, line, 0, []);

        Check(former.Solve(
            MotionPixels(width, height, -140, 0), width, height, line, 1, []) is null,
            "The former 96-pixel search unexpectedly recovered 140-pixel motion.");
        var recovered = Require(widened.Solve(
            MotionPixels(width, height, -140, 0), width, height, line, 1, []));
        Check(recovered.ImageTranslation.X == -140,
            $"Widened search recovered {recovered.ImageTranslation.X}, not -140 pixels.");
        Check(widened.LastPatchSearchExecution?.Backend
            == (Environment.ProcessorCount > 1
                ? GroundComputeBackend.ParallelCpu
                : GroundComputeBackend.Cpu),
            "Automatic Windows patch-search backend selection was not reported correctly.");
    }

    internal static void TestParallelSolverMatchesCpuAcrossMotionSequence()
    {
        const int width = 320, height = 180;
        var cpu = new GroundCameraSolver(matcherBackend: GroundComputeBackend.Cpu);
        var parallel = new GroundCameraSolver(matcherBackend: GroundComputeBackend.ParallelCpu);
        (int X, int Y)[] positions =
        [
            (0, 0), (-4, 0), (-11, 2), (-19, 7), (-31, 12),
            (-46, 8), (-62, 1), (-40, -4), (-17, -1), (0, 0),
        ];
        for (var index = 0; index < positions.Length; index++)
        {
            var position = positions[index];
            var line = new[] { new GroundTextureLine(70 + position.Y, 16, 303) };
            var pixels = MotionPixels(width, height, position.X, position.Y);
            var timestamp = index / 30.0;
            var cpuResult = cpu.Solve(pixels, width, height, line, timestamp, []);
            var parallelResult = parallel.Solve(pixels, width, height, line, timestamp, []);
            Check((parallelResult is null) == (cpuResult is null),
                $"CPU and parallel solvers disagreed about frame {index} acceptance.");
            if (cpuResult is null || parallelResult is null) continue;
            Check(parallelResult.ImageTranslation == cpuResult.ImageTranslation,
                $"CPU and parallel translation differ at frame {index}.");
            Check(parallelResult.Support == cpuResult.Support,
                $"CPU and parallel support differ at frame {index}.");
            Check(Math.Abs(parallelResult.Error - cpuResult.Error) <= .002,
                $"CPU and parallel errors differ at frame {index}.");
        }
    }

    internal static void TestReleaseParallelSearchCapacityBenchmark()
    {
        if (Environment.GetEnvironmentVariable("HKV_WINDOWS_COMPUTE_BENCHMARK") != "1") return;
        var matcher = new GroundPatchMatcher();
        var ordinary = MakeSearchFixture(96);
        var wide = MakeSearchFixture(192);
        _ = matcher.Search(ordinary.Sources, ordinary.Pixels, 640, 360,
            ordinary.Candidates, GroundComputeBackend.ParallelCpu);
        var cpu = MedianMilliseconds(9, () => matcher.Search(
            ordinary.Sources, ordinary.Pixels, 640, 360, ordinary.Candidates,
            GroundComputeBackend.Cpu));
        var parallel = MedianMilliseconds(9, () => matcher.Search(
            ordinary.Sources, ordinary.Pixels, 640, 360, ordinary.Candidates,
            GroundComputeBackend.ParallelCpu));
        var wideParallel = MedianMilliseconds(9, () => matcher.Search(
            wide.Sources, wide.Pixels, 640, 360, wide.Candidates,
            GroundComputeBackend.ParallelCpu));
        Console.WriteLine(FormattableString.Invariant(
            $"GROUND_WINDOWS_SEARCH_MS radius96 cpuMedian={cpu:F3} parallelMedian={parallel:F3} speedup={cpu / parallel:F2} radius192 parallelMedian={wideParallel:F3}"));
    }

    internal static void TestReleaseParallelGroundImageBenchmark()
    {
        if (Environment.GetEnvironmentVariable("HKV_WINDOWS_COMPUTE_BENCHMARK") != "1") return;
        const int width = 640, height = 360;
        var luma = Enumerable.Range(0, width * height)
            .Select(index => unchecked((byte)(index * 73 ^ index / width * 131)))
            .ToArray();
        var validity = Enumerable.Repeat((byte)1, luma.Length).ToArray();
        var processor = new GroundImageProcessor();
        var cpu = MedianMilliseconds(15, () => Require(processor.Process(
            luma, width, height, validity, null, GroundComputeBackend.Cpu)));
        var parallel = MedianMilliseconds(15, () => Require(processor.Process(
            luma, width, height, validity, null, GroundComputeBackend.ParallelCpu)));
        Console.WriteLine(FormattableString.Invariant(
            $"GROUND_WINDOWS_IMAGE_640x360_MS cpuMedian={cpu:F3} parallelMedian={parallel:F3} speedup={cpu / parallel:F2}"));
    }

    private static SearchFixture MakeSearchFixture(int radius)
    {
        const int width = 640, height = 360;
        var pixels = Enumerable.Range(0, width * height)
            .Select(index => unchecked((byte)(index * 73 ^ index / width * 131 ^ index >> 5)))
            .ToArray();
        var sources = Enumerable.Range(0, (560 - 48) / 32 + 1)
            .Select(index => 48 + index * 32)
            .Select(x => new GroundPatchSearchSource(
                x, 180, Require(GroundPixels.Patch(pixels, width, height, x, 180))))
            .ToArray();
        var candidates = new List<GroundPatchSearchCandidate>();
        for (var dy = -3; dy <= 3; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var samples = new List<GroundPatchSearchSample>();
            for (var index = 0; index < sources.Length; index++)
            {
                var x = sources[index].X + dx;
                var y = sources[index].Y + dy;
                if (x < 0 || x + GroundPixels.PatchWidth > width
                    || y < 0 || y + GroundPixels.PatchHeight > height) continue;
                samples.Add(new GroundPatchSearchSample(index, y * width + x));
            }
            candidates.Add(new GroundPatchSearchCandidate(dx, dy, samples));
        }
        return new SearchFixture(sources, pixels, candidates.ToArray());
    }

    private static byte[] MotionPixels(int width, int height, int dx, int dy) =>
        Enumerable.Range(0, width * height).Select(index =>
        {
            var x = index % width - dx;
            var y = index / width - dy;
            if (x < 0 || x >= width || y < 0 || y >= height) return (byte)0;
            var hash = unchecked((ulong)(x * 73_856_093) ^ (ulong)(y * 19_349_663));
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            return unchecked((byte)(hash >> 11));
        }).ToArray();

    private static double MedianMilliseconds<T>(int count, Func<T> operation)
    {
        var values = Enumerable.Range(0, count).Select(index =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = operation();
            return System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }).Order().ToArray();
        return values[values.Length / 2];
    }

    private static T Require<T>(T? value) where T : class =>
        value ?? throw new Exception($"Expected {typeof(T).Name}, got null.");

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
