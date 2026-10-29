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
    public struct Source { public int X, Y, Offset; public float Mean, Energy; }
    public struct Candidate { public int Offset, Count, Dx, Dy; public uint Eligible, Matched; }
    public struct Sample { public int SourceIndex, TargetOffset; }
    public struct Score { public float Error; public int Support; }
    public struct RowMoments { public int Sum, Squared; }
    public struct PatchMoments { public float Mean, Energy; }
    private readonly object gate = new();
    private Context? context;
    private Accelerator? accelerator;
    private Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<Source>,
        ArrayView<Candidate>, ArrayView<Sample>, ArrayView<PatchMoments>, ArrayView<Score>, int, int>? kernel;
    private Action<Index1D, ArrayView<byte>, ArrayView<RowMoments>, int>? rowKernel;
    private Action<Index1D, ArrayView<RowMoments>, ArrayView<PatchMoments>, int, int>? momentKernel;
    private MemoryBuffer1D<RowMoments, Stride1D.Dense>? rowMomentBuffer;
    private MemoryBuffer1D<PatchMoments, Stride1D.Dense>? patchMomentBuffer;
    private MemoryBuffer1D<byte, Stride1D.Dense>? lumaBuffer, sourcePixelBuffer;
    private MemoryBuffer1D<Source, Stride1D.Dense>? sourceBuffer;
    private MemoryBuffer1D<Candidate, Stride1D.Dense>? candidateBuffer;
    private MemoryBuffer1D<Sample, Stride1D.Dense>? sampleBuffer;
    private MemoryBuffer1D<Score, Stride1D.Dense>? outputBuffer;
    // Reuse host staging too: the flattened sample array is several MB
    // for a broad search and otherwise triggers full collections mid-frame.
    private byte[] hostSourcePixels = [];
    private Source[] hostSources = [];
    private Candidate[] hostCandidates = [];
    private Sample[] hostSamples = [];
    private Score[] hostOutput = [];
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
                ArrayView<PatchMoments>, ArrayView<Score>, int, int>(ScoreCandidates);
            rowKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<RowMoments>, int>(ComputeRowMoments);
            momentKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<RowMoments>, ArrayView<PatchMoments>, int, int>(ComputePatchMoments);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Dispose();
        }
    }

    /// <summary>Reserve the full production search before timed frames, including sudden shake.</summary>
    public void ReserveSearchCapacity(int width, int height, int sources, int candidates)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 16_777_216
            || sources is <= 0 or > 8192 || candidates is <= 0 or > 100_000
            || (long)sources * candidates > 4_000_000) throw new ArgumentOutOfRangeException(nameof(candidates));
        lock (gate)
        {
            if (accelerator is null) return;
            EnsureCapacity(ref lumaBuffer, checked(width * height));
            EnsureCapacity(ref rowMomentBuffer, checked(width * height));
            EnsureCapacity(ref patchMomentBuffer, checked(width * height));
            EnsureCapacity(ref sourcePixelBuffer, sources * GroundPixels.PatchPixelCount);
            EnsureCapacity(ref sourceBuffer, sources);
            EnsureCapacity(ref candidateBuffer, candidates);
            EnsureCapacity(ref sampleBuffer, checked(sources * candidates));
            EnsureCapacity(ref outputBuffer, candidates);
            Host(ref hostSourcePixels, sources * GroundPixels.PatchPixelCount);
            Host(ref hostSources, sources);
            Host(ref hostCandidates, candidates);
            Host(ref hostSamples, checked(sources * candidates));
            Host(ref hostOutput, candidates);
        }
    }

    public IReadOnlyList<GroundPatchSearchResult>? TrySearch(
        IReadOnlyList<GroundPatchSearchSource> sources, IReadOnlyList<byte> pixels,
        int width, int height, IReadOnlyList<GroundPatchSearchCandidate> candidates, bool allowVerticalSpread = false)
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
                var sourcePixels = Host(ref hostSourcePixels, sources.Count * GroundPixels.PatchPixelCount);
                var sourceData = Host(ref hostSources, sources.Count);
                for (var i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    var offset = i * GroundPixels.PatchPixelCount;
                    source.Pixels.CopyTo(sourcePixels, offset);
                    sourceData[i] = new Source { X = source.X, Y = source.Y, Offset = offset,
                        Mean = source.Mean, Energy = source.Energy };
                }
                var candidateData = Host(ref hostCandidates, candidates.Count);
                var samples = Host(ref hostSamples, (int)sampleCount);
                var cursor = 0;
                var layout = candidates[0].Samples as IValidatedGroundPatchSamples;
                var compactLayout = layout is not null && sources.Count <= 32
                    && layout.IsValidatedFor(width, height, sources.Count)
                    && layout.MatchesSourcePositions(sources) ? layout.LayoutIdentity : null;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var start = cursor;
                    var candidateSamples = candidates[i].Samples;
                    var bounded = candidateSamples as IValidatedGroundPatchSamples;
                    var validated = bounded is not null && bounded.IsValidatedFor(width, height, sources.Count);
                    if (validated && compactLayout is not null && ReferenceEquals(bounded!.LayoutIdentity, compactLayout))
                    {
                        candidateData[i] = new Candidate { Offset = -1, Count = candidateSamples.Count,
                            Dx = bounded.DisplacementX, Dy = bounded.DisplacementY,
                            Eligible = bounded.EligibleSources, Matched = bounded.MatchedSources };
                        continue;
                    }
                    for (var sampleIndex = 0; sampleIndex < candidateSamples.Count; sampleIndex++)
                    {
                        var sample = candidateSamples[sampleIndex];
                        if (!validated)
                        {
                            if ((uint)sample.SourceIndex >= (uint)sources.Count) continue;
                            if (sample.TargetOffset is { } offset
                                && (offset < 0 || offset / width + GroundPixels.PatchHeight > height
                                    || offset % width + GroundPixels.PatchWidth > width)) continue;
                        }
                        samples[cursor++] = new Sample { SourceIndex = sample.SourceIndex,
                            TargetOffset = sample.TargetOffset ?? -1 };
                    }
                    candidateData[i] = new Candidate { Offset = start, Count = cursor - start };
                }
                var luma = Upload(ref lumaBuffer, pixels as byte[] ?? pixels.ToArray());
                var patches = Upload(ref sourcePixelBuffer, sourcePixels, sources.Count * GroundPixels.PatchPixelCount);
                var metadata = Upload(ref sourceBuffer, sourceData, sources.Count);
                var searches = Upload(ref candidateBuffer, candidateData, candidates.Count);
                var targets = Upload(ref sampleBuffer, samples, Math.Max(1, cursor));
                EnsureCapacity(ref outputBuffer, candidates.Count);
                ArrayView<PatchMoments> moments = default;
                if (sampleCount >= (long)width * height / 8)
                {
                    var count = checked(width * height);
                    EnsureCapacity(ref rowMomentBuffer, count);
                    EnsureCapacity(ref patchMomentBuffer, count);
                    var rows = rowMomentBuffer!.View.SubView(0, count).BaseView;
                    moments = patchMomentBuffer!.View.SubView(0, count).BaseView;
                    rowKernel!(count, luma, rows, width);
                    momentKernel!(count, rows, moments, width, height);
                }
                kernel!(candidates.Count, luma, patches, metadata, searches, targets,
                    moments, outputBuffer!.View, width, allowVerticalSpread ? 1 : 0);
                // The readback synchronizes this stream before buffers can be reused.
                var output = Host(ref hostOutput, candidates.Count);
                outputBuffer.View.SubView(0, candidates.Count).BaseView.CopyToCPU(output.AsSpan(0, candidates.Count));
                Interlocked.Increment(ref dispatchCount);
                // Usually only a handful of the broad candidates survive.
                // Reserving every candidate allocates a large-object array at
                // every search and can trigger a full collection mid-frame.
                var accepted = 0;
                for (var i = 0; i < candidates.Count; i++)
                    if (output[i].Error >= 0) accepted++;
                var results = new List<GroundPatchSearchResult>(accepted);
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

    private static T[] Host<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new T[Math.Max(length, buffer.Length * 2)];
        return buffer;
    }

    private ArrayView<T> Upload<T>(ref MemoryBuffer1D<T, Stride1D.Dense>? buffer, T[] values, int length = -1)
        where T : unmanaged
    {
        if (length < 0) length = values.Length;
        EnsureCapacity(ref buffer, length);
        var view = buffer!.View.SubView(0, length);
        ReadOnlySpan<T> source = values.AsSpan(0, length);
        view.BaseView.CopyFromCPU(source);
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

    // Every candidate used to rescan 192 target pixels just to compute the
    // same mean/energy. Separable integer sums compute each target patch once.
    // Even the maximum squared sum (192 * 255^2) is below 2^24, so conversion
    // retains the original float accumulator's exact integer result.
    private static void ComputeRowMoments(Index1D index, ArrayView<byte> luma,
        ArrayView<RowMoments> rows, int width)
    {
        if ((int)index % width + 16 > width) { rows[index] = default; return; }
        int sum = 0, squared = 0;
        for (var x = 0; x < 16; x++)
        {
            int value = luma[index + x]; sum += value; squared += value * value;
        }
        rows[index] = new RowMoments { Sum = sum, Squared = squared };
    }

    private static void ComputePatchMoments(Index1D index, ArrayView<RowMoments> rows,
        ArrayView<PatchMoments> moments, int width, int height)
    {
        if ((int)index % width + 16 > width || (int)index / width + 12 > height)
        { moments[index] = default; return; }
        int sum = 0, squared = 0;
        for (var y = 0; y < 12; y++)
        {
            var row = rows[index + y * width]; sum += row.Sum; squared += row.Squared;
        }
        var mean = sum / 192f;
        moments[index] = new PatchMoments { Mean = mean, Energy = Math.Max(0f, squared / 192f - mean * mean) };
    }

    private static void ScoreCandidates(Index1D index, ArrayView<byte> luma,
        ArrayView<byte> sourcePixels, ArrayView<Source> sources,
        ArrayView<Candidate> candidates, ArrayView<Sample> samples,
        ArrayView<PatchMoments> moments, ArrayView<Score> output, int width, int allowVerticalSpread)
    {
        var candidate = candidates[index];
        // Reject only candidates that cannot possibly meet the existing
        // majority rule. Surviving candidates retain every sample and exactly
        // the same cost accumulation order as the reference implementation.
        var compact = candidate.Offset < 0;
        var possible = 0;
        var iterations = compact ? (int)sources.Length : candidate.Count;
        for (var i = 0; i < iterations; i++)
            if (compact ? (candidate.Matched & (1u << i)) != 0
                : samples[candidate.Offset + i].TargetOffset >= 0) possible++;
        if (possible < 3 || possible * 2 <= candidate.Count)
        { output[index] = new Score { Error = -1 }; return; }
        var support = 0;
        var minimumX = int.MaxValue;
        var maximumX = int.MinValue;
        var minimumY = int.MaxValue; var maximumY = int.MinValue;
        float cost = 0; var processed = 0;
        for (var i = 0; i < iterations; i++)
        {
            if (compact && (candidate.Eligible & (1u << i)) == 0) continue;
            var remaining = candidate.Count - processed;
            if (support + remaining < 3 || (support + remaining) * 2 <= candidate.Count)
            { output[index] = new Score { Error = -1 }; return; }
            processed++;
            var sample = compact ? new Sample { SourceIndex = i, TargetOffset = -1 }
                : samples[candidate.Offset + i];
            var source = sources[sample.SourceIndex];
            if (compact && (candidate.Matched & (1u << i)) != 0)
                sample.TargetOffset = (source.Y + candidate.Dy) * width + source.X + candidate.Dx;
            float error = 32;
            if (sample.TargetOffset >= 0)
            {
                float mean, energy;
                if (moments.Length > 0)
                {
                    var stats = moments[sample.TargetOffset]; mean = stats.Mean; energy = stats.Energy;
                }
                else
                {
                    float sum = 0, squared = 0;
                    for (var y = 0; y < 12; y++)
                    for (var x = 0; x < 16; x++)
                    {
                        float value = luma[sample.TargetOffset + y * width + x];
                        sum += value; squared += value * value;
                    }
                    mean = sum / 192f;
                    energy = Math.Max(0f, squared / 192f - mean * mean);
                }
                error = float.PositiveInfinity;
                if (energy >= Math.Max(1f, source.Energy * .1f))
                {
                    var gain = IntrinsicMath.Clamp(MathF.Sqrt(source.Energy / energy), .5f, 2f);
                    float total = 0;
                    for (var y = 0; y < 12; y++)
                    {
                        for (var x = 0; x < 16; x++)
                            total += MathF.Abs(sourcePixels[source.Offset + y * 16 + x] - source.Mean
                                - (luma[sample.TargetOffset + y * width + x] - mean) * gain);
                        // Above the cost cap the patch cannot add support and
                        // its contribution is exactly 32, regardless of the
                        // remaining nonnegative pixel errors.
                        if (total >= 32 * 192) break;
                    }
                    error = total / 192f;
                }
            }
            cost += Math.Min(32f, error);
            if (error > 18) continue;
            support++;
            minimumX = Math.Min(minimumX, source.X);
            maximumX = Math.Max(maximumX, source.X);
            minimumY = Math.Min(minimumY, source.Y); maximumY = Math.Max(maximumY, source.Y);
        }
        var valid = candidate.Count >= 3 && support >= 3 && support * 2 > candidate.Count
            && (maximumX - minimumX >= 32 || allowVerticalSpread != 0 && maximumY - minimumY >= 32);
        output[index] = new Score { Error = valid ? cost / candidate.Count : -1, Support = support };
    }

    public void Dispose()
    {
        lock (gate)
        {
            // A lost device must not turn cleanup into a second fatal exception.
            IDisposable?[] resources = [lumaBuffer, sourcePixelBuffer, sourceBuffer,
                candidateBuffer, sampleBuffer, outputBuffer, rowMomentBuffer, patchMomentBuffer, accelerator, context];
            lumaBuffer = sourcePixelBuffer = null;
            sourceBuffer = null; candidateBuffer = null; sampleBuffer = null; outputBuffer = null;
            accelerator = null; context = null;
            kernel = null; rowKernel = null; momentKernel = null;
            rowMomentBuffer = null; patchMomentBuffer = null;
            hostSourcePixels = []; hostSources = []; hostCandidates = []; hostSamples = []; hostOutput = [];
            foreach (var resource in resources)
            {
                try { resource?.Dispose(); }
                catch (Exception error) { Failure ??= error.Message; }
            }
        }
    }
}
