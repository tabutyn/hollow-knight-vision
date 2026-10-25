using System.Collections.Concurrent;
using System.Diagnostics;

namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Windows ground-compute policy. CUDA scores patches on NVIDIA hardware;
/// CPU selection, pose validation and atlas admission remain unchanged.
/// </summary>
public enum GroundComputeBackend
{
    Cpu,
    ParallelCpu,
    Automatic,
    Cuda,
}

public sealed class GroundPatchSearchSource
{
    public GroundPatchSearchSource(int x, int y, IReadOnlyList<byte> pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        X = x;
        Y = y;
        Pixels = pixels.ToArray();
        float sum = 0;
        float squared = 0;
        foreach (var pixel in Pixels)
        {
            sum += pixel;
            squared += pixel * pixel;
        }
        var count = Math.Max(1, Pixels.Length);
        Mean = sum / count;
        Energy = Math.Max(0, squared / count - Mean * Mean);
    }

    public int X { get; }
    public int Y { get; }
    public byte[] Pixels { get; }
    public float Mean { get; }
    public float Energy { get; }
}

public readonly record struct GroundPatchSearchSample(
    int SourceIndex,
    int? TargetOffset);

public sealed record GroundPatchSearchCandidate(
    int Dx,
    int Dy,
    IReadOnlyList<GroundPatchSearchSample> Samples);

public readonly record struct GroundPatchSearchResult(
    int Dx,
    int Dy,
    double Error,
    int Support);

public sealed record GroundPatchSearchExecution(
    IReadOnlyList<GroundPatchSearchResult> Results,
    GroundComputeBackend Backend,
    int CandidateCount,
    int PatchComparisonCount,
    TimeSpan Elapsed);

public sealed class GroundPatchMatcher
{
    private sealed record TargetPatch(byte[] Pixels, float Mean, float Energy);
    private static readonly Lazy<GroundCudaPatchMatcher> cuda = new(() => new());
    private readonly bool enableCuda;
    public GroundPatchMatcher(bool enableCuda = true) => this.enableCuda = enableCuda;
    public bool CudaAvailable => enableCuda && cuda.Value.Available;
    public string? CudaDeviceName => enableCuda ? cuda.Value.DeviceName : null;
    public string? CudaFailure => enableCuda ? cuda.Value.Failure : "CUDA disabled for this matcher";
    public long CudaDispatchCount => enableCuda ? cuda.Value.DispatchCount : 0;
    public void WarmUp() { if (enableCuda) _ = cuda.Value.Available; }

    public static GroundPatchMatcher Shared { get; } = new();

    public GroundPatchSearchExecution Search(
        IReadOnlyList<GroundPatchSearchSource> sources,
        IReadOnlyList<byte> pixels,
        int width,
        int height,
        IReadOnlyList<GroundPatchSearchCandidate> candidates,
        GroundComputeBackend requestedBackend)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(candidates);
        var started = Stopwatch.GetTimestamp();
        if (enableCuda && (requestedBackend == GroundComputeBackend.Cuda
                || requestedBackend == GroundComputeBackend.Automatic && candidates.Count >= 64)
            && cuda.Value.TrySearch(sources, pixels, width, height, candidates) is { } gpuResults)
        {
            return new GroundPatchSearchExecution(gpuResults, GroundComputeBackend.Cuda,
                candidates.Count, candidates.Sum(candidate => candidate.Samples.Count),
                Stopwatch.GetElapsedTime(started));
        }
        var selected = requestedBackend switch
        {
            GroundComputeBackend.Automatic or GroundComputeBackend.Cuda when Environment.ProcessorCount > 1
                && candidates.Count >= 64 => GroundComputeBackend.ParallelCpu,
            GroundComputeBackend.Automatic or GroundComputeBackend.Cuda => GroundComputeBackend.Cpu,
            _ => requestedBackend,
        };
        var results = selected == GroundComputeBackend.ParallelCpu
            ? ParallelSearch(sources, pixels, width, height, candidates)
            : CpuSearch(sources, pixels, width, height, candidates);
        return new GroundPatchSearchExecution(
            results,
            selected,
            candidates.Count,
            candidates.Sum(candidate => candidate.Samples.Count),
            Stopwatch.GetElapsedTime(started));
    }

    private static IReadOnlyList<GroundPatchSearchResult> CpuSearch(
        IReadOnlyList<GroundPatchSearchSource> sources,
        IReadOnlyList<byte> pixels,
        int width,
        int height,
        IReadOnlyList<GroundPatchSearchCandidate> candidates)
    {
        if (!ValidImage(pixels, width, height)) return [];
        var targets = new Dictionary<int, TargetPatch>();
        var results = new List<GroundPatchSearchResult>();
        foreach (var candidate in candidates)
        {
            var result = Evaluate(
                candidate,
                sources,
                offset => Target(offset, pixels, width, height, targets));
            if (result is { } value) results.Add(value);
        }
        return results;
    }

    private static IReadOnlyList<GroundPatchSearchResult> ParallelSearch(
        IReadOnlyList<GroundPatchSearchSource> sources,
        IReadOnlyList<byte> pixels,
        int width,
        int height,
        IReadOnlyList<GroundPatchSearchCandidate> candidates)
    {
        if (!ValidImage(pixels, width, height)) return [];
        var targets = new ConcurrentDictionary<int, TargetPatch>();
        var results = new GroundPatchSearchResult?[candidates.Count];
        Parallel.For(0, candidates.Count, index =>
        {
            results[index] = Evaluate(
                candidates[index],
                sources,
                offset =>
                {
                    if (targets.TryGetValue(offset, out var cached)) return cached;
                    var value = ReadTarget(offset, pixels, width, height);
                    return value is null ? null : targets.GetOrAdd(offset, value);
                });
        });
        return results.Where(result => result.HasValue)
            .Select(result => result!.Value)
            .ToArray();
    }

    private static GroundPatchSearchResult? Evaluate(
        GroundPatchSearchCandidate candidate,
        IReadOnlyList<GroundPatchSearchSource> sources,
        Func<int, TargetPatch?> target)
    {
        var count = 0;
        var support = 0;
        var minimumInlierX = int.MaxValue;
        var maximumInlierX = int.MinValue;
        float cost = 0;
        foreach (var sample in candidate.Samples)
        {
            if (sample.SourceIndex < 0 || sample.SourceIndex >= sources.Count) continue;
            var source = sources[sample.SourceIndex];
            float error;
            if (sample.TargetOffset is { } targetOffset)
            {
                var patch = target(targetOffset);
                if (patch is null) continue;
                error = PatchError(source, patch);
            }
            else
            {
                error = 32;
            }
            count++;
            cost += Math.Min(32, error);
            if (error > 18) continue;
            support++;
            minimumInlierX = Math.Min(minimumInlierX, source.X);
            maximumInlierX = Math.Max(maximumInlierX, source.X);
        }
        if (count < 3 || support < 3 || support * 2 <= count
            || maximumInlierX - minimumInlierX < 32)
        {
            return null;
        }
        return new GroundPatchSearchResult(
            candidate.Dx,
            candidate.Dy,
            cost / count,
            support);
    }

    private static TargetPatch? Target(
        int offset,
        IReadOnlyList<byte> pixels,
        int width,
        int height,
        IDictionary<int, TargetPatch> cache)
    {
        if (cache.TryGetValue(offset, out var cached)) return cached;
        var value = ReadTarget(offset, pixels, width, height);
        if (value is not null) cache[offset] = value;
        return value;
    }

    private static TargetPatch? ReadTarget(
        int offset,
        IReadOnlyList<byte> pixels,
        int width,
        int height)
    {
        if (!ValidImage(pixels, width, height)
            || offset < 0
            || offset / width + GroundPixels.PatchHeight > height
            || offset % width + GroundPixels.PatchWidth > width
            || (long)offset + (GroundPixels.PatchHeight - 1L) * width
                + GroundPixels.PatchWidth - 1 >= pixels.Count)
        {
            return null;
        }
        var values = new byte[GroundPixels.PatchPixelCount];
        float sum = 0;
        float squared = 0;
        for (var row = 0; row < GroundPixels.PatchHeight; row++)
        for (var column = 0; column < GroundPixels.PatchWidth; column++)
        {
            var value = pixels[offset + row * width + column];
            values[row * GroundPixels.PatchWidth + column] = value;
            sum += value;
            squared += value * value;
        }
        var mean = sum / GroundPixels.PatchPixelCount;
        var energy = Math.Max(
            0,
            squared / GroundPixels.PatchPixelCount - mean * mean);
        return new TargetPatch(values, mean, energy);
    }

    private static float PatchError(
        GroundPatchSearchSource source,
        TargetPatch target)
    {
        if (source.Pixels.Length == 0
            || source.Pixels.Length != target.Pixels.Length
            || target.Energy < Math.Max(1, source.Energy * .1f))
        {
            return float.PositiveInfinity;
        }
        var gain = Math.Clamp(
            MathF.Sqrt(source.Energy / target.Energy),
            .5f,
            2f);
        float error = 0;
        for (var index = 0; index < source.Pixels.Length; index++)
        {
            error += MathF.Abs(
                source.Pixels[index] - source.Mean
                - (target.Pixels[index] - target.Mean) * gain);
        }
        return error / source.Pixels.Length;
    }

    private static bool ValidImage(
        IReadOnlyList<byte> pixels,
        int width,
        int height) => width > 0 && height > 0
        && pixels.Count >= (long)width * height;
}

public sealed record GroundImageProcessingResult(
    byte[] GroundEdges,
    byte[] GroundPixels,
    GroundComputeBackend Backend,
    TimeSpan Elapsed);

public sealed class GroundImageProcessor
{
    public static GroundImageProcessor Shared { get; } = new();

    public GroundImageProcessingResult? Process(
        IReadOnlyList<byte> luma,
        int width,
        int height,
        IReadOnlyList<byte> validSourcePixels,
        Range? validRows,
        GroundComputeBackend requestedBackend = GroundComputeBackend.Automatic)
    {
        ArgumentNullException.ThrowIfNull(luma);
        ArgumentNullException.ThrowIfNull(validSourcePixels);
        if (width <= 0 || height <= 0
            || luma.Count != (long)width * height
            || validSourcePixels.Count != luma.Count)
        {
            return null;
        }
        var started = Stopwatch.GetTimestamp();
        // Like macOS production, automatic keeps this small preprocessing job
        // on the CPU because dispatch overhead outweighs parallel work.
        var backend = requestedBackend == GroundComputeBackend.ParallelCpu
            ? GroundComputeBackend.ParallelCpu
            : GroundComputeBackend.Cpu;
        var edges = new byte[luma.Count];
        if (height > 1)
        {
            if (backend == GroundComputeBackend.ParallelCpu)
            {
                Parallel.For(0, width * (height - 1), index =>
                {
                    if (validSourcePixels[index] != 0)
                        edges[index] = (byte)Math.Abs(luma[index] - luma[index + width]);
                });
            }
            else
            {
                for (var index = 0; index < width * (height - 1); index++)
                {
                    if (validSourcePixels[index] != 0)
                        edges[index] = (byte)Math.Abs(luma[index] - luma[index + width]);
                }
            }
        }
        var analysis = new GroundEdgeAnalysis(
            width,
            height,
            edges,
            luma.ToArray(),
            validSourcePixels.ToArray(),
            validRows);
        return new GroundImageProcessingResult(
            edges,
            GroundLineDetector.GroundDetectPixels(analysis),
            backend,
            Stopwatch.GetElapsedTime(started));
    }
}
