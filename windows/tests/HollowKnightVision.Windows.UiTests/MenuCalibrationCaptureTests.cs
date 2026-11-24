using System.IO;
using System.Text.Json;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;
using HostProgram = HollowKnightVision.Windows.Host.Program;

internal static class MenuCalibrationCaptureTests
{
    internal static void Run()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Check(HostProgram.RunAsync(["--menu-calibration", "menu-calibration-capture"],
            standardOutput: stdout, standardError: stderr).GetAwaiter().GetResult() == 64,
            "Menu calibration commands must require explicit automation control.");
        Check(stdout.ToString().Length == 0 && stderr.ToString().Contains("--enable-automation-control"),
            "The denied command must report the missing flag without trying to capture.");

        var directory = Directory.CreateTempSubdirectory("hkv-menu-calibration-");
        try
        {
            var store = new MenuStencilLiveCaptureStore(Path.Combine(directory.FullName, "captures"));
            var catalog = MakeCatalog();
            var pixels = Enumerable.Repeat((byte)100, 16 * 8 * 4).ToArray();
            var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(182);
            var frame = new BgraFrame(16, 8, 16 * 4, pixels, 4, timestamp);
            var menu = new MenuStencilResult("extras", true, .95, "Credits", [], [], [], 0, 0,
                .95, MenuStencilPhase.Tracking, 10, 182, "de") { SelectedOptionIdentifier = "extras.credits" };
            var status = Request("menu-calibration-status");
            var capture = Request("menu-calibration-capture");
            var recognized = status.Evaluate(frame, catalog, menu, store);
            Check(recognized is { Ok: true, ContextIdentifier: "extras", SelectedIdentifier: "extras.credits",
                SelectedName: "Credits", CapturePath: null }, "Status did not return the recognized option.");
            using var json = JsonDocument.Parse(recognized.Serialize());
            Check(json.RootElement.GetProperty("ok").GetBoolean()
                && json.RootElement.GetProperty("contextIdentifier").GetString() == "extras"
                && !json.RootElement.TryGetProperty("capturePath", out _), "Status JSON differs from the Mac command.");

            Check(!capture.Evaluate(frame, catalog, menu with { IsMatch = false }, store).Ok
                && !capture.Evaluate(frame, catalog, menu with { SelectedOptionIdentifier = "extras.invalid" }, store).Ok
                && !Request("menu-calibration-capture:brightness:brightness.exit").Evaluate(frame, catalog, menu, store).Ok
                && !Directory.Exists(store.RootDirectory), "Missing match, unknown menu or unknown row must leave storage untouched.");

            var automatic = capture.Evaluate(frame, catalog, menu, store);
            var manual = Request("menu-calibration-capture:ja:extras:extras.credits")
                .Evaluate(frame, catalog, null, store);
            var legacy = Request("menu-calibration-capture:extras:extras.credits")
                .Evaluate(frame, catalog, null, store);
            Check(automatic.Ok && manual.Ok && legacy.Ok, "Recognized and manually identified menu frames must be capturable.");
            var saved = store.Load().ToDictionary(item => item.Capture.ResolvedLanguageIdentifier);
            Check(saved.Count == 3 && saved["de"].ImagePath == automatic.CapturePath
                && saved["ja"].ImagePath == manual.CapturePath && saved["en"].ImagePath == legacy.CapturePath,
                "The capture paths or inferred, explicit and legacy language identifiers did not round trip.");
            Check(saved.Values.All(item => item.Capture is { CapturedAt: var time, ImageWidth: 16, ImageHeight: 8,
                SelectedIdentifier: "extras.credits" } && time == timestamp && File.Exists(item.ImagePath)),
                "The saved frame lost its selection, dimensions or capture timestamp.");

            var invalidRoot = Path.Combine(directory.FullName, "file");
            File.WriteAllText(invalidRoot, "occupied");
            Check(capture.Evaluate(frame, catalog, menu, new MenuStencilLiveCaptureStore(invalidRoot))
                is { Ok: false, SelectedIdentifier: "extras.credits", CapturePath: null },
                "A disk failure must retain the identified row and omit a capture path.");
            Check(MenuCalibrationCapture.Parse("menu-calibration-capture::extras.credits") is null
                && MenuCalibrationCapture.Parse("menu-calibration-capture:unknown:extras:extras.credits") is null,
                "Invalid row and language arguments must be rejected.");
        }
        finally { directory.Delete(recursive: true); }
        Console.WriteLine("PASS Windows menu calibration reports and saves recognized or manually identified frames");
    }

    private static MenuCalibrationCapture Request(string command) => MenuCalibrationCapture.Parse(command)
        ?? throw new InvalidOperationException($"Fixture command did not parse: {command}");

    private static MenuStencilCatalog MakeCatalog()
    {
        var bounds = new StencilRect(2, 1, 8, 4);
        var reference = new ImageStencilReference(8, 4, Enumerable.Repeat((byte)255, 8 * 4 * 3).ToArray());
        var variant = MenuStencilCatalog.MakeVariant("extras.credits", reference);
        var anchor = new MenuStencilCatalog.Anchor("extras.credits", "Credits", bounds, null, [variant], variant);
        return new([new("extras", 16, 8, anchor, [anchor], [new("extras.credits", "Credits", bounds, null, null, null)],
            new Dictionary<string, IReadOnlyList<MenuStencilCatalog.Variant>>(), null)], 16, 8, 0, 8, []);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
