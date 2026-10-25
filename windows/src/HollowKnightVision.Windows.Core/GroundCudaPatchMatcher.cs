using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace HollowKnightVision.Windows.Core;

/// <summary>
/// CUDA counterpart of Mac's normalized 16x12 Metal patch scorer. ILGPU compiles
/// the kernel to PTX; an NVIDIA driver is sufficient on installed machines.
/// One lock owns reusable buffers and the stream. Any device failure disables
/// this backend for the process and leaves the caller on the CPU reference path.
/// </summary>
public sealed class GroundCudaPatchMatcher : IDisposable
{
    // Public blittable layouts are required by ILGPU's generated launcher assembly.
    public struct Source { public int X, Offset; public float Mean, Energy; }
    public struct Candidate { public int Offset, Count; }
    public struct Sample { public int SourceIndex, TargetOffset; }
    public struct Score { public float Error; public int Support; }
    private readonly object gate = new();
    private Context? context;
    private Accelerator? accelerator;
    private Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<Source>,
        ArrayView<Candidate>, ArrayView<Sample>, ArrayView<Score>, int>? kernel;
    private MemoryBuffer1D<byte, Stride1D.Dense>? lumaBuffer, sourcePixelBuffer;
    private MemoryBuffer1D<Source, Stride1D.Dense>? sourceBuffer;
    private MemoryBuffer1D<Candidate, Stride1D.Dense>? candidateBuffer;
    private MemoryBuffer1D<Sample, Stride1D.Dense>? sampleBuffer;
    private MemoryBuffer1D<Score, Stride1D.Dense>? outputBuffer;
    private long dispatchCount;
    public string? DeviceName { get; private set; }
    public string? Failure { get; private set; }
    public long DispatchCount => Interlocked.Read(ref dispatchCount);
    public bool Available => Volatile.Read(ref accelerator) is not null;

    public GroundCudaPatchMatcher()
    {
        try
        {
            context = Context.Create(builder => builder.Cuda());
            CudaDevice? device = null;
            foreach (var candidate in context.GetCudaDevices()) { device = candidate; break; }
            if (device is null) throw new InvalidOperationException("No CUDA-capable NVIDIA device");
            accelerator = device.CreateAccelerator(context);
            DeviceName = accelerator.Name;
            kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>,
                ArrayView<byte>, ArrayView<Source>, ArrayView<Candidate>, ArrayView<Sample>,
                ArrayView<Score>, int>(ScoreCandidates);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Dispose();
        }
    }

    public IReadOnlyList<GroundPatchSearchResult>? TrySearch(
        IReadOnlyList<GroundPatchSearchSource> sources, IReadOnlyList<byte> pixels,
        int width, int height, IReadOnlyList<GroundPatchSearchCandidate> candidates)
    {
        if (width <= 0 || height <= 0 || pixels.Count < (long)width * height
            || sources.Count == 0 || candidates.Count == 0) return null;
        // Bounded input also protects device allocations and flattened offsets.
        if (pixels.Count > 16_777_216 || sources.Count > 8192 || candidates.Count > 100_000
            || sources.Any(source => source.Pixels.Length != GroundPixels.PatchPixelCount)) return null;
        var sampleCount = candidates.Sum(candidate => (long)candidate.Samples.Count);
        if (sampleCount == 0 || sampleCount > 4_000_000) return null;
        lock (gate)
        {
            if (accelerator is null) return null;
            try
            {
                var sourcePixels = new byte[sources.Count * GroundPixels.PatchPixelCount];
                var sourceData = new Source[sources.Count];
                for (var i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    var offset = i * GroundPixels.PatchPixelCount;
                    source.Pixels.CopyTo(sourcePixels, offset);
                    sourceData[i] = new Source { X = source.X, Offset = offset,
                        Mean = source.Mean, Energy = source.Energy };
                }
                var candidateData = new Candidate[candidates.Count];
                var samples = new Sample[(int)sampleCount];
                var cursor = 0;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var start = cursor;
                    foreach (var sample in candidates[i].Samples)
                    {
                        if ((uint)sample.SourceIndex >= (uint)sources.Count) continue;
                        if (sample.TargetOffset is { } offset
                            && (offset < 0 || offset / width + GroundPixels.PatchHeight > height
                                || offset % width + GroundPixels.PatchWidth > width)) continue;
                        samples[cursor++] = new Sample { SourceIndex = sample.SourceIndex,
                            TargetOffset = sample.TargetOffset ?? -1 };
                    }
                    candidateData[i] = new Candidate { Offset = start, Count = cursor - start };
                }
                var luma = Upload(ref lumaBuffer, pixels as byte[] ?? pixels.ToArray());
                var patches = Upload(ref sourcePixelBuffer, sourcePixels);
                var metadata = Upload(ref sourceBuffer, sourceData);
                var searches = Upload(ref candidateBuffer, candidateData);
                var targets = Upload(ref sampleBuffer, samples);
                EnsureCapacity(ref outputBuffer, candidates.Count);
                kernel!(candidates.Count, luma, patches, metadata, searches, targets,
                    outputBuffer!.View, width);
                // The readback synchronizes this stream before buffers can be reused.
                var output = outputBuffer.View.SubView(0, candidates.Count).GetAsArray1D();
                Interlocked.Increment(ref dispatchCount);
                var results = new List<GroundPatchSearchResult>();
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (output[i].Error < 0) continue;
                    results.Add(new(candidates[i].Dx, candidates[i].Dy,
                        output[i].Error, output[i].Support));
                }
                return results;
            }
            catch (Exception error)
            {
                Failure = error.Message;
                Dispose();
                return null;
            }
        }
    }

    private ArrayView<T> Upload<T>(ref MemoryBuffer1D<T, Stride1D.Dense>? buffer, T[] values)
        where T : unmanaged
    {
        EnsureCapacity(ref buffer, values.Length);
        var view = buffer!.View.SubView(0, values.Length);
        view.CopyFromCPU(values);
        return view.BaseView;
    }

    private void EnsureCapacity<T>(ref MemoryBuffer1D<T, Stride1D.Dense>? buffer, int length)
        where T : unmanaged
    {
        if (buffer is not null && buffer.Length >= length) return;
        var replacement = accelerator!.Allocate1D<T>(Math.Max(1, length));
        buffer?.Dispose();
        buffer = replacement;
    }

    private static void ScoreCandidates(Index1D index, ArrayView<byte> luma,
        ArrayView<byte> sourcePixels, ArrayView<Source> sources,
        ArrayView<Candidate> candidates, ArrayView<Sample> samples,
        ArrayView<Score> output, int width)
    {
        var candidate = candidates[index];
        var support = 0;
        var minimumX = int.MaxValue;
        var maximumX = int.MinValue;
        float cost = 0;
        for (var i = 0; i < candidate.Count; i++)
        {
            var sample = samples[candidate.Offset + i];
            var source = sources[sample.SourceIndex];
            float error = 32;
            if (sample.TargetOffset >= 0)
            {
                float sum = 0, squared = 0;
                for (var y = 0; y < 12; y++)
                for (var x = 0; x < 16; x++)
                {
                    float value = luma[sample.TargetOffset + y * width + x];
                    sum += value;
                    squared += value * value;
                }
                var mean = sum / 192f;
                var energy = Math.Max(0f, squared / 192f - mean * mean);
                error = float.PositiveInfinity;
                if (energy >= Math.Max(1f, source.Energy * .1f))
                {
                    var gain = IntrinsicMath.Clamp(MathF.Sqrt(source.Energy / energy), .5f, 2f);
                    float total = 0;
                    for (var y = 0; y < 12; y++)
                    for (var x = 0; x < 16; x++)
                        total += MathF.Abs(sourcePixels[source.Offset + y * 16 + x] - source.Mean
                            - (luma[sample.TargetOffset + y * width + x] - mean) * gain);
                    error = total / 192f;
                }
            }
            cost += Math.Min(32f, error);
            if (error > 18) continue;
            support++;
            minimumX = Math.Min(minimumX, source.X);
            maximumX = Math.Max(maximumX, source.X);
        }
        var valid = candidate.Count >= 3 && support >= 3 && support * 2 > candidate.Count
            && maximumX - minimumX >= 32;
        output[index] = new Score { Error = valid ? cost / candidate.Count : -1, Support = support };
    }

    public void Dispose()
    {
        lock (gate)
        {
            // A lost device must not turn cleanup into a second fatal exception.
            IDisposable?[] resources = [lumaBuffer, sourcePixelBuffer, sourceBuffer,
                candidateBuffer, sampleBuffer, outputBuffer, accelerator, context];
            lumaBuffer = sourcePixelBuffer = null;
            sourceBuffer = null; candidateBuffer = null; sampleBuffer = null; outputBuffer = null;
            accelerator = null; context = null;
            kernel = null;
            foreach (var resource in resources)
            {
                try { resource?.Dispose(); }
                catch (Exception error) { Failure ??= error.Message; }
            }
        }
    }
}
