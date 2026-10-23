using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class MenuLiveCaptureTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Live menu capture store assertion failed."); }
    public static void TestPortableStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-live-store-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MenuStencilLiveCaptureStore(Path.Combine(root, "menu-stencil-live-v1", "captures"));
            Check(store.RootDirectory == MenuStencilLiveCaptureStore.RootForExamples(Path.Combine(root, "labeling-v1", "examples")));
            Check(store.Load().Count == 0 && !Directory.Exists(store.RootDirectory));
            var frame = new BgraFrame(2, 1, 8, new byte[] { 4, 5, 6, 255, 7, 8, 9, 255 }, 1, DateTimeOffset.UnixEpoch);
            var first = store.Save(frame, "main-title", "main-title.start-game", "Start Game", "en", DateTimeOffset.UnixEpoch.AddSeconds(1));
            var second = store.Save(frame, "options", "shared.audio", "Audio", "fr", DateTimeOffset.UnixEpoch.AddSeconds(2.8));
            var loaded = store.Load(); Check(loaded.Select(item => item.Capture.Id).SequenceEqual(new[] { second.Capture.Id, first.Capture.Id }));
            Check(loaded[0].Capture == second.Capture && loaded[0].Source.LanguageIdentifier == "fr" && loaded[0].Capture.CapturedAt.Millisecond == 0);
            Check(File.ReadAllBytes(second.ImagePath).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            var manifest = Path.Combine(Path.GetDirectoryName(first.ImagePath)!, MenuStencilLiveCaptureStore.ManifestFilename);
            var json = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject(); json.Remove("languageIdentifier"); File.WriteAllText(manifest, json.ToJsonString());
            Check(store.Load()[1].Source.LanguageIdentifier == "en");
            Directory.CreateDirectory(Path.Combine(store.RootDirectory, ".staging-incomplete"));
            Check(store.Load().Count == 2);
            json["schemaVersion"] = 2; File.WriteAllText(manifest, json.ToJsonString()); Check(store.Load().Count == 1);
            json["schemaVersion"] = 1; json["imageFilename"] = "../outside.png"; File.WriteAllText(manifest, json.ToJsonString());
            try { store.Load(); throw new Exception("Traversal accepted."); } catch (InvalidDataException) { }
            try { store.Save(frame, "options", "shared.audio", "Audio", "unsupported"); throw new Exception("Invalid language accepted."); } catch (ArgumentException) { }
            Check(Directory.GetDirectories(store.RootDirectory).Length == 3);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
