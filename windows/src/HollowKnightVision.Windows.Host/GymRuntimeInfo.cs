using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal sealed record GymRuntimeInfo(string VisionSource, string Receiver, string? VisualDetectorAndHUD)
{
    public static GymRuntimeInfo Read(string domain, string executable, string supportRoot, string? detectorOverride)
    {
        if (domain is not ("hacker" or "gameplay")) throw new ArgumentException("Unknown bot domain.");
        var game = HollowKnightGameLauncher.ResolveExecutable(executable);
        HollowKnightGameLauncher.ValidateInstallation(game, requireModded: true);
        var source = HashFiles([typeof(Program).Assembly.Location, typeof(BotPolicyContract).Assembly.Location]);
        var receiver = Convert.ToHexString(HashFile(HollowKnightGameLauncher.ReceiverAssembly(game)));
        if (domain == "hacker") return new(source, receiver, null);

        var (runs, artifacts) = GameplayGymObservationEncoder.DetectorRuns(supportRoot, detectorOverride);
        var detectors = runs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(run => DetectorInputs(run, artifacts.FirstOrDefault(artifact => string.Equals(
                Path.GetDirectoryName(artifact.ModelPath), run, StringComparison.OrdinalIgnoreCase))?.TrainedClassIdentifiers))
            .ToArray();
        var inputs = detectors.SelectMany(detector => new[] { detector.Manifest, detector.Model })
            .Concat(AutoNavigationRunner.GymStencilInputs(supportRoot));
        return new(source, receiver, HashFiles(inputs, detectors.Select(detector => detector.TrainedClasses)));
    }

    private static (string Manifest, string Model, string[] TrainedClasses) DetectorInputs(
        string directory, IReadOnlySet<string>? trainedClasses)
    {
        var path = Path.Combine(directory, "training.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var model = manifest.RootElement.GetProperty("modelFilename").GetString();
        if (string.IsNullOrEmpty(model) || model != Path.GetFileName(model)
            || !string.Equals(Path.GetExtension(model), ".onnx", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.RootElement.GetProperty("modelFormat").GetString(), "onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid ONNX detector run: {directory}");
        var (_, enabled) = OnnxObjectDetector.ReadClasses(directory, manifest.RootElement, trainedClasses);
        return (path, Path.Combine(directory, model), enabled.Order(StringComparer.Ordinal).ToArray());
    }

    private static string HashFiles(IEnumerable<string> files, IEnumerable<string[]>? detectorClasses = null)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
            digest.AppendData(HashFile(file));
        if (detectorClasses is not null)
            digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(detectorClasses));
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static byte[] HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }
}
