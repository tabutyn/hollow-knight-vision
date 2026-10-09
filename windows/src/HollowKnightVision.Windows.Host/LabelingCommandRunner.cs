using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal static class LabelingCommandRunner
{
    internal static int Capture(LabelingCommand command)
    {
        var window = HollowKnightWindowLocator.FindBest();
        if (window is null)
        {
            Console.Error.WriteLine("Hollow Knight window not found; label capture does not launch it.");
            return 3;
        }
        var frame = CpuBgraNormalizer.Normalize(new GdiWindowCapture().Capture(window, 1));
        var store = new LabelingExampleStore(command.TargetPath);
        var example = store.Save(
            frame,
            command.ContextIdentifier
                ?? throw new ArgumentException("--label-context is required for capture."),
            command.Boxes.Select(ToAnnotation),
            command.KnownClassIdentifiers,
            command.Ready ? LabelingExampleCompletion.Ready : LabelingExampleCompletion.Draft,
            command.CaptureGroupIdentifier);
        WriteJson(new
        {
            ok = true,
            mode = "label-capture",
            exampleDirectory = example.DirectoryPath,
            example.Manifest.Id,
            example.Manifest.ImageIdentifier,
            example.Manifest.CaptureGroupIdentifier,
            example.Manifest.ContextIdentifier,
            annotationCount = example.Manifest.Annotations.Count,
            example.Manifest.KnownClassIdentifiers
        });
        return 0;
    }

    internal static int List(LabelingCommand command)
    {
        var store = new LabelingExampleStore(command.TargetPath);
        var examples = store.Load();
        WriteJson(new
        {
            ok = true,
            mode = "label-list",
            rootDirectory = store.RootDirectory,
            count = examples.Count,
            examples = examples.Select(example => new
            {
                example.Manifest.Id,
                example.DirectoryPath,
                example.Manifest.ContextIdentifier,
                example.Manifest.CreatedAt,
                annotationCount = example.Manifest.Annotations.Count,
                positiveCount = example.Manifest.Annotations.Count(annotation =>
                    !annotation.IsHardNegative),
                hardNegativeCount = example.Manifest.Annotations.Count(annotation =>
                    annotation.IsHardNegative),
                example.Manifest.KnownClassIdentifiers
            })
        });
        return 0;
    }

    internal static int Update(LabelingCommand command)
    {
        var exampleDirectory = Path.GetFullPath(command.TargetPath);
        var rootDirectory = Directory.GetParent(exampleDirectory)?.FullName
            ?? throw new ArgumentException("Example directory has no parent store.");
        var store = new LabelingExampleStore(rootDirectory);
        var manifest = store.LoadManifest(exampleDirectory);
        var example = new SavedLabelingExample(exampleDirectory, manifest);
        var updated = store.Update(
            example,
            command.ContextIdentifier ?? manifest.ContextIdentifier,
            command.Boxes.Select(ToAnnotation),
            command.KnownClassIdentifiers,
            command.Ready ? LabelingExampleCompletion.Ready : LabelingExampleCompletion.Draft);
        WriteJson(new
        {
            ok = true,
            mode = "label-update",
            updated.DirectoryPath,
            updated.Manifest.Id,
            updated.Manifest.ContextIdentifier,
            annotationCount = updated.Manifest.Annotations.Count,
            updated.Manifest.KnownClassIdentifiers
        });
        return 0;
    }

    internal static int Delete(LabelingCommand command)
    {
        var exampleDirectory = Path.GetFullPath(command.TargetPath);
        var rootDirectory = Directory.GetParent(exampleDirectory)?.FullName
            ?? throw new ArgumentException("Example directory has no parent store.");
        var store = new LabelingExampleStore(rootDirectory);
        var example = new SavedLabelingExample(
            exampleDirectory,
            store.LoadManifest(exampleDirectory));
        store.Delete(example);
        WriteJson(new { ok = true, mode = "label-delete", exampleDirectory });
        return 0;
    }

    internal static int Export(LabelingCommand command)
    {
        var store = new LabelingExampleStore(command.TargetPath);
        var exporter = new LabelingDatasetExporter(
            command.DatasetRoot
                ?? throw new ArgumentException("--dataset-root is required for export."));
        var snapshot = exporter.Export(
            command.ModelIdentifier,
            command.KnownClassIdentifiers,
            store.Load());
        WriteJson(new
        {
            ok = true,
            mode = "label-export",
            datasetDirectory = snapshot.DirectoryPath,
            snapshot.Manifest.Id,
            snapshot.Manifest.ClassIdentifier,
            snapshot.Manifest.ClassIdentifiers,
            itemCount = snapshot.Manifest.Items.Count,
            trainingItems = snapshot.Manifest.Items.Count(item =>
                item.Split == LabelingDatasetSplit.Training),
            validationItems = snapshot.Manifest.Items.Count(item =>
                item.Split == LabelingDatasetSplit.Validation),
            snapshot.Manifest.IsPreliminary
        });
        return 0;
    }

    private static LabelingExampleAnnotation ToAnnotation(LabelBox box) => new(
        Guid.NewGuid(),
        box.ClassIdentifier,
        box.X,
        box.Y,
        box.Width,
        box.Height,
        box.IsNegative ? true : null);

    private static void WriteJson(object value) => Console.WriteLine(
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

internal enum LabelingCommandKind
{
    Capture,
    List,
    Update,
    Delete,
    Export
}

internal sealed record LabelBox(
    string ClassIdentifier,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsNegative);

internal sealed record LabelingCommand(
    LabelingCommandKind Kind,
    string TargetPath,
    string? ContextIdentifier,
    IReadOnlyList<LabelBox> Boxes,
    IReadOnlyList<string> KnownClassIdentifiers,
    Guid? CaptureGroupIdentifier,
    bool Ready,
    string? DatasetRoot,
    string ModelIdentifier);
