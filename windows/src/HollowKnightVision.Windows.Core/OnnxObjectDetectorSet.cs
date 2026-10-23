namespace HollowKnightVision.Windows.Core;

/// <summary>One shared pass, or the active legacy per-object passes selected by the version store.</summary>
public sealed class OnnxObjectDetectorSet : IDisposable
{
    private readonly List<OnnxObjectDetector> detectors = [];
    public bool HasModels => detectors.Count > 0;
    public OnnxObjectDetectorSet(IEnumerable<string> runDirectories, IReadOnlyList<LabelingActiveModelArtifact>? artifacts = null)
    {
        try
        {
            foreach (var directory in runDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var version = artifacts?.FirstOrDefault(item => string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(item.ModelPath)), directory, StringComparison.OrdinalIgnoreCase))?.Version;
                detectors.Add(new(directory, version));
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
