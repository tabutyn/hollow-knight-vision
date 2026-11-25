namespace HollowKnightVision.Windows.Core;

/// <summary>One shared pass, or the active legacy per-object passes selected by the version store.</summary>
public sealed class OnnxObjectDetectorSet : IDisposable
{
    private readonly List<OnnxObjectDetector> detectors = [];
    public bool HasModels => detectors.Count > 0;
    public OnnxObjectDetectorSet(IEnumerable<string> runDirectories, IReadOnlyList<LabelingActiveModelArtifact>? artifacts = null,
        bool useActorCalibrations = true)
    {
        try
        {
            foreach (var directory in runDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var artifact = artifacts?.FirstOrDefault(item => string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(item.ModelPath)), directory, StringComparison.OrdinalIgnoreCase));
                if (artifact?.TrainedClassIdentifiers is { Count: 0 }) continue;
                var detector = new OnnxObjectDetector(directory, artifact?.Version, artifact?.TrainedClassIdentifiers,
                    artifact?.SourceRunIdentifier, useActorCalibrations);
                if (detector.TrainedClassIdentifiers.Count > 0) detectors.Add(detector);
                else detector.Dispose();
            }
        }
        catch { Dispose(); throw; }
    }
    public IReadOnlyList<ObjectDetection> Detect(BgraFrame source, bool raw = false) =>
        detectors.SelectMany(detector => detector.Detect(source, raw)).ToArray();
    public void Dispose()
    {
        foreach (var detector in detectors) detector.Dispose();
        detectors.Clear();
    }
}
