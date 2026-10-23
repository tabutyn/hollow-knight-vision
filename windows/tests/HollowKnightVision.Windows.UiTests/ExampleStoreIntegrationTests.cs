using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class ExampleStoreIntegrationTests
{
    private static void Check(bool value, string message = "Native example-store round-trip failed.")
    { if (!value) throw new Exception(message); }

    internal static void Run()
    {
        TestSaveDraftRoundTrip();
        Console.WriteLine("PASS Saved screenshots round-trip through the editor's native PNG reader without changing pixels or identity");
    }

    // Direct equivalent of Mac testSaveDraftAtomicallyStoresCleanImageAndNormalizedAnnotations,
    // with pixel-by-pixel verification through the actual production WPF loader.
    internal static void TestSaveDraftRoundTrip()
    {
        var root = Directory.CreateTempSubdirectory("hkv-example-image-parity-").FullName;
        try
        {
            var date = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            var store = new LabelingExampleStore(Path.Combine(root, "examples"), now: () => date);
            const int width = 7, height = 5, stride = 32;
            var bytes = new byte[stride * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var offset = y * stride + x * 4;
                    bytes[offset] = (byte)(11 + x * 9); bytes[offset + 1] = (byte)(37 + y * 13);
                    bytes[offset + 2] = (byte)(90 + x + y); bytes[offset + 3] = 255;
                }
            var image = new BgraFrame(width, height, stride, bytes, 17, date);
            var rectangle = new LabelingExampleAnnotation(Guid.NewGuid(), "game.mana", .125, .25, .5, .375);
            var group = Guid.NewGuid();
            var saved = store.Save(image, "game", [rectangle], captureGroupIdentifier: group, languageIdentifier: "fr");
            var loaded = store.LoadManifest(saved.DirectoryPath);
            Check(loaded.SchemaVersion == LabelingExampleManifest.CurrentSchemaVersion && loaded.ImageWidth == width && loaded.ImageHeight == height);
            Check(loaded.CaptureGroupIdentifier == group && loaded.ContextIdentifier == "game" && loaded.CreatedAt == date
                && loaded.LanguageIdentifier == "fr" && loaded.Completion == LabelingExampleCompletion.Draft);
            Check(loaded.Annotations.Single() == rectangle && loaded.KnownClassIdentifiers!.SequenceEqual(new[] { "game.mana" }));
            var loadBitmap = typeof(MainWindow).GetMethod("LoadBitmap", BindingFlags.Static | BindingFlags.NonPublic)!;
            void VerifyPixels(SavedLabelingExample example)
            {
                var decoded = (BitmapSource)loadBitmap.Invoke(null, [example.ImagePath])!;
                Check(decoded.IsFrozen && decoded.PixelWidth == width && decoded.PixelHeight == height);
                var bgra = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[width * height * 4]; bgra.CopyPixels(pixels, width * 4, 0);
                for (var y = 0; y < height; y++)
                    Check(pixels.AsSpan(y * width * 4, width * 4).SequenceEqual(bytes.AsSpan(y * stride, width * 4)),
                        "Stored PNG includes overlay, padding, flipped rows, changed colours or alpha.");
                using var exclusive = new FileStream(example.ImagePath, FileMode.Open, FileAccess.Read, FileShare.None);
                Check(exclusive.Length > 0, "Editor loader retained a file handle after decoding.");
            }
            VerifyPixels(saved);
            var examples = store.Load(); Check(examples.Count == 1 && examples.Single().Id == saved.Id); VerifyPixels(examples.Single());
            Check(Directory.GetFileSystemEntries(store.RootDirectory).Length == 1
                && Directory.GetFiles(saved.DirectoryPath).Length == 2, "Staging or temporary files survived the commit.");
            var png = File.ReadAllBytes(saved.ImagePath);
            var health = new LabelingExampleAnnotation(Guid.NewGuid(), "game.health", .2, .3, .4, .5);
            var updated = store.Update(saved, "game", [rectangle, health]);
            Check(updated.Id == saved.Id && updated.Manifest.ImageIdentifier == saved.Manifest.ImageIdentifier
                && updated.Manifest.CaptureGroupIdentifier == group && updated.Manifest.CreatedAt == date && updated.Manifest.LanguageIdentifier == "fr");
            Check(updated.Manifest.Annotations.SequenceEqual(new[] { rectangle, health })
                && updated.Manifest.KnownClassIdentifiers!.SequenceEqual(new[] { "game.health", "game.mana" })
                && updated.Manifest.Completion == LabelingExampleCompletion.Draft);
            Check(File.ReadAllBytes(saved.ImagePath).SequenceEqual(png) && Directory.GetFileSystemEntries(store.RootDirectory).Length == 1);
            var reopened = store.Load().Single(); VerifyPixels(reopened);
            Check(reopened.Manifest.Annotations.SequenceEqual(updated.Manifest.Annotations)
                && reopened.Manifest.KnownClassIdentifiers!.SequenceEqual(updated.Manifest.KnownClassIdentifiers!));
        }
        finally { Directory.Delete(root, true); }
    }
}
