using System.Globalization;
using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class VisionResetController
{
    internal static string? ResetStencils(string? examplesRoot = null, string? supportRoot = null)
    {
        var root = supportRoot ?? ApplicationDataRoot();
        var store = new LabelingExampleStore(examplesRoot ?? Path.Combine(root, "labeling-v1", "examples"));
        var contexts = StencilInitializationPolicy.AllContexts.Where(item => item.Identifier != "game")
            .Select(item => item.Identifier).ToHashSet(StringComparer.Ordinal);
        var sources = store.Load().Where(example => contexts.Contains(
                StencilInitializationPolicy.NormalizeContext(example.Manifest.ContextIdentifier)))
            .Select(example => example.DirectoryPath)
            .Append(Path.Combine(root, "menu-stencil-live-v1"))
            .Where(Directory.Exists).ToArray();
        return Archive(sources, "stencils", supportRoot);
    }

    internal static string? FactoryReset()
    {
        var sources = new[]
        {
            ApplicationDataRoot(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "HollowKnightVision")
        }.Where(Directory.Exists).ToArray();
        return Archive(sources, "factory");
    }

    private static string ApplicationDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HollowKnightVision");

    private static string? Archive(IReadOnlyList<string> sources, string kind, string? supportRoot = null)
    {
        if (sources.Count == 0) return null;
        var backupRoot = Path.Combine(
            supportRoot is null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.GetDirectoryName(supportRoot)!, "HollowKnightVision Backups");
        Directory.CreateDirectory(backupRoot);
        var destination = Path.Combine(
            backupRoot,
            $"{kind}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        var moved = new List<(string Source, string Destination)>();
        try
        {
            foreach (var source in sources)
            {
                var target = Path.Combine(destination, Path.GetFileName(source));
                if (Directory.Exists(target)) target += $"-{moved.Count + 1}";
                Directory.Move(source, target);
                moved.Add((source, target));
            }
            return destination;
        }
        catch
        {
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                if (!Directory.Exists(item.Source) && Directory.Exists(item.Destination))
                {
                    Directory.Move(item.Destination, item.Source);
                }
            }
            if (!Directory.EnumerateFileSystemEntries(destination).Any())
            {
                Directory.Delete(destination);
            }
            throw;
        }
    }
}
