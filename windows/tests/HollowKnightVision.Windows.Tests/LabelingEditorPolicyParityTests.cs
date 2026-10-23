using HollowKnightVision.Windows.Core;

internal static class LabelingEditorPolicyParityTests
{
    private static void Check(
        bool condition,
        string message = "Labeling editor policy parity assertion failed.")
    {
        if (!condition) throw new Exception(message);
    }

    private static MenuStencilContext Context(string identifier) =>
        LabelingContractCatalog.Contexts.Single(context =>
            context.Identifier == identifier);

    private static void EqualNames(string context, params string[] expected)
    {
        var actual = Context(context).Labels.Select(label => label.Name);
        Check(actual.SequenceEqual(expected, StringComparer.Ordinal),
            $"Unexpected ordered labels for {context}: {string.Join(", ", actual)}");
    }

    public static void TestLabelSetsContainTheirExpectedObjects()
    {
        EqualNames("main-title",
            "Hollow Knight Title", "Start Game", "Options", "Achievements", "Extras",
            "Quit Game", "Select Decorations");
        EqualNames("options",
            "Options", "Game", "Audio", "Video", "Controller", "Keyboard", "Mods", "Back",
            "Select Decorations");
        EqualNames("game-options",
            "Game Options", "Language", "Camera Shake", "HUD Apperance",
            "Show Achievements", "Backer Credits", "Reset Defaults", "Back",
            "Select Decorations");
        EqualNames("audio",
            "Audio", "Master Volume", "Sound Volume", "Music Volume", "Reset Defaults", "Back",
            "Select Decorations");
        EqualNames("video",
            "Video", "Resolution", "Full Screen", "V-Sync", "Frame Rate Cap", "Screen Scale",
            "Brightness", "Advanced Settings", "Reset Defaults", "Back",
            "Select Decorations");
        EqualNames("screen-scale",
            "Scale", "Screen Corner", "Select Decorations", "Back");
        EqualNames("brightness",
            "Brightness", "Pointer", "Back", "Select Decorations");
        EqualNames("video-advanced-settings",
            "Advanced Settings", "Particle Effects", "Blur Quality", "Dithering",
            "Film Grain", "Reset Defaults", "Back", "Select Decorations");
        EqualNames("controller",
            "Controller", "Controller Diagram", "Remap Controlls", "Advanced Settings", "Back",
            "Select Decorations");
        EqualNames("remap-controller",
            "Remap Controller", "Jump", "Attack", "Dash", "Focus / Cast", "Quick Map",
            "Super Dash", "Dream Nail", "Quick Cast", "Reset Defaults", "Done",
            "Select Decorations");
        EqualNames("controller-advanced-settings",
            "Advanced Settings", "Vibration", "Native Controller Input", "MFI",
            "Reset Defaults", "Back", "Select Decorations");
        EqualNames("keyboard",
            "Keyboard", "Up", "Down", "Jump", "Attack", "Dash", "Focus / Cast", "Left",
            "Right", "Quick Map", "Super Dash", "Dream Nail", "Quick Cast", "Inventory",
            "Reset Defaults", "Back", "Select Decorations");
        EqualNames("mods",
            "Mods", "Mod Decoration", "Back", "Select Decorations");
        EqualNames("achievements",
            "Achievements", "Back", "Select Decorations");
        EqualNames("select-profile",
            "Select Profile", "1.", "2.", "3.", "4.", "Clear Save", "Back",
            "Select Decorations");
        EqualNames("clear-save",
            "Clear Save?", "Yes", "No", "Select Decorations");
        EqualNames("game",
            "Hallow Knight", "Mana", "Health", "Geode", "Crawlid", "Vengfly",
            "Shade", "Geo Deposit", "Lifeblood Cacoon", "Sign");
        EqualNames("enemies",
            "Crawlid", "Vengfly", "Shade");
        EqualNames("world",
            "Geo Deposit", "Lifeblood Cacoon", "Sign");
        EqualNames("pause",
            "Header", "Continue", "Options", "Quit To Menu", "Select Decorations");
        EqualNames("quit-to-menu",
            "Quit To Menu", "Yes", "No", "Select Decorations");
        EqualNames("extras",
            "Extras", "Menu Style", "Credits", "Hidden Dreams", "The Grimm Troupe",
            "Lifeblood", "Godmaster", "Back", "Select Decorations");
        EqualNames("hidden-dreams",
            "Hidden Dreams", "Hidden Dreams Poster", "Back", "Select Decorations");
        EqualNames("the-grimm-troupe",
            "The Grimm Troupe", "Grimm Troupe Poster", "Back", "Select Decorations");
        EqualNames("lifeblood",
            "Lifeblood", "Lifeblood Poster", "Back", "Select Decorations");
        EqualNames("godmaster",
            "Godmaster", "Godmaster Poster", "Back", "Select Decorations");
        EqualNames("inventory",
            "Inventory", "Inventory Corner");
        EqualNames("quit-game",
            "Quit Game", "Yes", "No", "Select Decorations");
    }

    public static void TestSharedObjectsBelongToEveryApplicableSet()
    {
        var catalog = LabelingContractCatalog.Bundled;
        Check(catalog.SetsContaining("shared.options")
            .Select(set => set.ContextIdentifier)
            .SequenceEqual(["main-title", "options", "pause"], StringComparer.Ordinal));
        Check(catalog.SetsContaining("shared.select-decoration")
            .Any(set => set.ContextIdentifier == "quit-game"));
        Check(catalog.SetsContaining("shared.yes")
            .Select(set => set.ContextIdentifier)
            .SequenceEqual(["clear-save", "quit-to-menu", "quit-game"], StringComparer.Ordinal));

        var selectable = LabelingContractCatalog.ScreenContexts
            .Select(context => context.Identifier)
            .ToHashSet(StringComparer.Ordinal);
        Check(!selectable.Contains("shared"));
        Check(!selectable.Contains("enemies"));
        Check(!selectable.Contains("world"));

        var allContextIdentifiers = LabelingContractCatalog.Contexts
            .SelectMany(context => context.Labels)
            .Select(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier))
            .ToHashSet(StringComparer.Ordinal);
        Check(allContextIdentifiers.SetEquals(LabelingCatalogPolicy.ClassIdentifiers()));
    }

    public static void TestLegacyMenuLabelsMigrateToTheirSingleCategoryOwners()
    {
        (string Legacy, string Canonical)[] migrations =
        [
            ("select-profile.back", "shared.back"),
            ("main-title.options", "shared.options"),
            ("pause.options", "shared.options"),
            ("options.options", "shared.options"),
            ("main-title.achievements", "shared.achievements"),
            ("main-title.extras", "shared.extras"),
            ("options.audio", "shared.audio"),
            ("options.video", "shared.video"),
            ("options.controller", "shared.controller"),
            ("options.keyboard", "shared.keyboard"),
            ("options.mods", "shared.mods"),
            ("quit-to-menu.yes", "shared.yes"),
            ("quit-to-menu.no", "shared.no"),
            ("pause.quit-to-menu", "quit-to-menu.quit-to-menu"),
            ("main-title.quit-game", "quit-game.quit-game")
        ];

        foreach (var (legacy, canonical) in migrations)
        {
            Check(LabelingClassIdentity.CanonicalIdentifier(legacy) == canonical,
                $"Expected {legacy} to migrate to {canonical}.");
        }

        Check(LabelingClassIdentity.CanonicalIdentifier("options.game") == "options.game");
        var game = Context("options").Labels.First(label => label.Name == "Game");
        var gameOptions = Context("game-options").Labels
            .First(label => label.Name == "Game Options");
        Check(game.Identifier != gameOptions.Identifier);
    }
}
