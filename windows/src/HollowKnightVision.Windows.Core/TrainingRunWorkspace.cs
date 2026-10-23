using System.Diagnostics;

namespace HollowKnightVision.Windows.Core;

/// <summary>Owns only new output/staging paths for one run; interrupted results cannot become model candidates.</summary>
public sealed class TrainingRunWorkspace
{
    private readonly string parent;
    public string OutputDirectory { get; }
    public string StagingDirectory { get; }

    public TrainingRunWorkspace(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        parent = Path.GetDirectoryName(OutputDirectory) ?? throw new ArgumentException("A run needs a parent directory.");
        OutputDirectory = ModelStoreJson.Child(parent, Path.GetFileName(OutputDirectory));
        StagingDirectory = ModelStoreJson.Child(parent, ".staging-" + Path.GetFileName(OutputDirectory));
        RequireAbsent(OutputDirectory); RequireAbsent(StagingDirectory);
    }

    public async Task<TrainingProcessResult> RunAsync(ProcessStartInfo start, CancellationToken cancellation,
        Action<TrainingOutputSnapshot>? outputChanged = null)
    {
        // Recheck immediately before launch. A rejected run must never archive pre-existing data.
        RequireAbsent(OutputDirectory); RequireAbsent(StagingDirectory);
        try
        {
            var result = await WindowsTrainingProcess.RunAsync(start, cancellation, outputChanged).ConfigureAwait(false);
            if (result.Cancelled || result.ExitCode != 0) ArchiveInterrupted();
            return result;
        }
        catch { ArchiveInterrupted(); throw; }
    }

    public IReadOnlyList<string> ArchiveInterrupted()
    {
        var archived = new List<string>();
        // Preserve artifacts for recovery/debugging, but hide them from candidate discovery.
        foreach (var source in new[] { OutputDirectory, StagingDirectory })
        {
            var checkedSource = ModelStoreJson.Child(parent, Path.GetFileName(source));
            if (!Directory.Exists(checkedSource)) continue;
            var destination = ModelStoreJson.Child(parent, ".interrupted-" + Path.GetFileName(source) + "-" + Guid.NewGuid().ToString("N"));
            Directory.Move(checkedSource, destination);
            archived.Add(destination);
        }
        return archived;
    }

    private static void RequireAbsent(string path)
    {
        if (Path.Exists(path)) throw new IOException($"Training output already exists: {path}");
    }
}
