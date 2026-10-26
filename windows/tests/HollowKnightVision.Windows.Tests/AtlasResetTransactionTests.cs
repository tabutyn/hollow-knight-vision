using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class AtlasResetTransactionTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    public static void TestNewRetainsPreviousButDeleteDiscardsIt()
    {
        foreach (var retainPrevious in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "hkv-atlas-reset-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new AtlasStateStore(Path.Combine(root, "atlas-v1", "states"));
                var active = Path.Combine(root, "atlas-v1", "active");
                Directory.CreateDirectory(active);
                File.WriteAllText(Path.Combine(active, "world.txt"), "previous autosave");
                var labels = Path.Combine(root, "labeling-v1");
                Directory.CreateDirectory(labels);
                File.WriteAllText(Path.Combine(labels, "health.json"), "keep labels");
                var saved = store.Save("Keep this named state", destination =>
                {
                    Directory.CreateDirectory(destination);
                    File.WriteAllText(Path.Combine(destination, "world.txt"), "named state");
                });
                string Validate(string directory) => File.ReadAllText(Path.Combine(directory, "world.txt"));
                var reset = VisionResetController.PublishActiveAtlasGeneration(store, active,
                    copyActiveWorld: false, retainPrevious: retainPrevious,
                    writeStagedGeneration: directory => File.WriteAllText(Path.Combine(directory, "world.txt"), "empty atlas"),
                    validateStagedWorld: Validate);
                Check(Validate(active) == "empty atlas", "Reset did not install the empty generation.");
                if (retainPrevious)
                {
                    Check(reset.ArchivedPreviousAtlasDirectory is not null, "New discarded the previous autosave.");
                    var backup = VisionResetController.ImportPreviousActiveAtlas(store,
                        reset.ArchivedPreviousAtlasDirectory!, "Before New");
                    Check(Validate(store.WorldPath(backup)) == "previous autosave", "New did not retain exact previous data.");
                }
                else
                {
                    Check(reset.ArchivedPreviousAtlasDirectory is null, "Delete returned a backup to publish.");
                    Check(!Directory.EnumerateDirectories(Path.GetDirectoryName(active)!)
                            .Any(path => Path.GetFileName(path).StartsWith("active.", StringComparison.Ordinal)),
                        "Delete left a recoverable copy beside the active directory.");
                }
                VisionResetController.RecoverInterruptedActiveAtlasPublication(store, active, Validate);
                Check(store.List().Count == (retainPrevious ? 2 : 1), "Delete resurrected a backup or New lost one.");
                Check(Validate(store.WorldPath(saved)) == "named state", "Reset changed a named saved state.");
                Check(File.ReadAllText(Path.Combine(labels, "health.json")) == "keep labels", "Reset changed labels.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }
    }

    public static void TestFailedDeleteRestoresPreviousAutosave()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-atlas-reset-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AtlasStateStore(Path.Combine(root, "states"));
            var active = Path.Combine(root, "active");
            Directory.CreateDirectory(active);
            File.WriteAllText(Path.Combine(active, "world.txt"), "previous autosave");
            string Validate(string directory) => File.ReadAllText(Path.Combine(directory, "world.txt"));
            var failed = false;
            try
            {
                VisionResetController.PublishActiveAtlasGeneration(store, active,
                    copyActiveWorld: false, retainPrevious: false,
                    writeStagedGeneration: directory => File.WriteAllText(Path.Combine(directory, "world.txt"), "empty atlas"),
                    validateStagedWorld: Validate,
                    activateWorld: _ => throw new IOException("fixture activation failure"));
            }
            catch (IOException error) when (error.Message == "fixture activation failure") { failed = true; }
            Check(failed, "Activation failure was hidden.");
            Check(Validate(active) == "previous autosave", "Failed delete lost the previous autosave.");
            Check(store.List().Count == 0, "Failed delete created a named copy.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
