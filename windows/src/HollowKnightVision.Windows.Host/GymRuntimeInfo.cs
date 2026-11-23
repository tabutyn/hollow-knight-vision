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

        var (runs, _) = GameplayGymObservationEncoder.DetectorRuns(supportRoot, detectorOverride);
        var inputs = runs.SelectMany(DetectorFiles).Concat(AutoNavigationRunner.GymStencilInputs(supportRoot));
        return new(source, receiver, HashFiles(inputs));
    }

    private static IEnumerable<string> DetectorFiles(string directory)
    {
        var path = Path.Combine(directory, "training.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var model = manifest.RootElement.GetProperty("modelFilename").GetString();
        if (string.IsNullOrEmpty(model) || model != Path.GetFileName(model)
            || !string.Equals(Path.GetExtension(model), ".onnx", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.RootElement.GetProperty("modelFormat").GetString(), "onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid ONNX detector run: {directory}");
        yield return path;
        yield return Path.Combine(directory, model);
    }

    private static string HashFiles(IEnumerable<string> files)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
            digest.AppendData(HashFile(file));
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static byte[] HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }
}
