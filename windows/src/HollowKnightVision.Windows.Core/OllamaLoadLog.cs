using System.Globalization;
using System.Text.RegularExpressions;

namespace HollowKnightVision.Windows.Core;

public sealed record OllamaLoadProgress(string Stage, string? OffloadedLayers, double GpuMiB, double CpuMiB);

/// <summary>Reads only recognized progress from the selected model's local runner.</summary>
internal sealed partial class OllamaLoadLog(string digest)
{
    private const int MaximumPartialLine = 4096;
    private string partial = "";
    private bool matchesModel;
    private OllamaLoadProgress progress = new("Waiting for Ollama to load the model", null, 0, 0);

    internal static string? ModelDigest(string? modelfile)
    {
        var source = modelfile?.Split('\n').FirstOrDefault(line => line.StartsWith("FROM ", StringComparison.Ordinal));
        return source is null ? null : FindDigest(source);
    }

    private static string? FindDigest(string value)
    {
        var match = DigestPattern().Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal IEnumerable<OllamaLoadProgress> Consume(string chunk)
    {
        var lines = (partial + chunk).Split('\n');
        partial = lines[^1];
        if (partial.Length > MaximumPartialLine) partial = partial[^MaximumPartialLine..];
        foreach (var line in lines.Take(lines.Length - 1))
        {
            var prior = progress;
            if (line.Contains("loading model via llama-server", StringComparison.Ordinal)
                || line.Contains("starting llama-server", StringComparison.Ordinal)
                || line.Contains("load_model: loading model '", StringComparison.Ordinal))
            {
                matchesModel = string.Equals(FindDigest(line), digest, StringComparison.OrdinalIgnoreCase);
                if (matchesModel) progress = new("Opening model weights", null, 0, 0);
            }
            if (!matchesModel) continue;

            if (line.Contains("load_tensors: loading model tensors", StringComparison.Ordinal))
                progress = progress with { Stage = "Mapping model weights into memory" };
            else if (line.Contains("warming up the model with an empty run", StringComparison.Ordinal))
                progress = progress with { Stage = "Warming up the language model" };
            else if (line.Contains("warmup with image size", StringComparison.Ordinal))
                progress = progress with { Stage = "Warming up the vision encoder" };
            else if (line.Contains("load_model: initializing, n_slots", StringComparison.Ordinal))
                progress = progress with { Stage = "Preparing the inference context" };

            if (line.Contains("load_tensors:", StringComparison.Ordinal))
            {
                var offload = OffloadPattern().Match(line);
                if (offload.Success)
                    progress = progress with { OffloadedLayers = offload.Groups[1].Value };
                var buffer = BufferPattern().Match(line);
                if (buffer.Success && double.TryParse(buffer.Groups[2].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var size) && double.IsFinite(size))
                {
                    var device = buffer.Groups[1].Value;
                    if (device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || device.EndsWith("_Host", StringComparison.OrdinalIgnoreCase))
                        progress = progress with { CpuMiB = progress.CpuMiB + size };
                    else if (GpuDevicePattern().IsMatch(device))
                        progress = progress with { GpuMiB = progress.GpuMiB + size };
                }
            }
            if (progress != prior) yield return progress;
        }
    }

    [GeneratedRegex(@"sha256[-:]([0-9a-fA-F]{64})(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"offloaded (\d+/\d+) layers to GPU", RegexOptions.CultureInvariant)]
    private static partial Regex OffloadPattern();
    [GeneratedRegex(@"([A-Za-z0-9_]+)\s+(?:Mapped\s+)?model buffer size\s*=\s*([0-9.]+) MiB",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BufferPattern();
    [GeneratedRegex(@"^(CUDA|Vulkan|HIP|ROCm|Metal|MTL|SYCL|GPU)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GpuDevicePattern();
}
