using HollowKnightVision.Windows.Core;

internal static class StencilSelectionTests
{
    private static void Check(bool condition, string message = "Stencil selection assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static LabelingExampleAnnotation Box(string id, bool negative = false) => new(Guid.NewGuid(), id, .1, .1, .2, .2, negative);
    private static IReadOnlyList<MenuStencilLabel> Choices(string context, IReadOnlyList<LabelingExampleAnnotation>? boxes = null,
        bool modded = true, bool hidesCompleted = true, bool deferred = false, IReadOnlyList<MenuStencilLabel>? custom = null) =>
        StencilLabelSelectionPolicy.AvailableLabels(context, boxes ?? [], modded, hidesCompleted, deferred, custom);

    public static void TestMenuOrderAndUndo()
    {
        Check(Choices("main-title", [Box("main-title.hollow-knight-logo")]).Select(item => item.Name).SequenceEqual(
            new[] { "Start Game", "Options", "Achievements", "Extras", "Quit Game", "Left Select Decoration" }));
        Check(Choices("main-title").First().Name == "Hollow Knight Title", "Undo did not make the missing label available again.");
    }

    public static void TestPositivePairsRemainEditable()
    {
        for (var count = 0; count <= 2; count++)
        {
            var boxes = Enumerable.Range(0, count).Select(_ => Box("main-title.select-decoration"))
                .Append(Box("shared.select-decoration", true)).ToArray();
            var choice = Choices("main-title", boxes).FirstOrDefault(item => item.Identifier == "shared.select-decoration");
            Check((choice is not null) == (count < 2));
            if (count < 2) Check(choice!.Name == (count == 0 ? "Left Select Decoration" : "Right Select Decoration"));
            Check(Choices("main-title", boxes, hidesCompleted: false).Any(item => item.Identifier == "shared.select-decoration"));
        }
        Check(Choices("gameplay-hud", [Box("game.health"), Box("game.health", true)]).Single().Identifier == "game.health");
        Check(Choices("gameplay-hud", [Box("game.health"), Box("game.health")]).Count == 0);
        Check(Choices("gameplay-hud", [Box("game.health"), Box("game.health")], hidesCompleted: false).Single().Identifier == "game.health");
    }

    public static void TestGameplayRepeatabilityAndMods()
    {
        var gameplay = LabelingContractCatalog.Contexts.Single(item => item.Identifier == "game").Labels;
        Check(Choices("game", gameplay.Select(item => Box(item.Identifier)).ToArray(), modded: false).SequenceEqual(gameplay));
        Check(!Choices("options", modded: false).Any(item => item.Identifier == "shared.mods"));
        Check(Choices("options").Any(item => item.Identifier == "shared.mods"));
        Check(!Choices("options", modded: false, hidesCompleted: false).Any(item => item.Identifier == "shared.mods"));
    }

    public static void TestDeferredAndCanonicalDuplicates()
    {
        Check(!Choices("select-profile").Any(item => item.Identifier == "select-profile.clear-save"));
        Check(Choices("select-profile", deferred: true).Any(item => item.Identifier == "select-profile.clear-save"));
        Check(!Choices("select-profile", [Box("select-profile.clear-save")], deferred: true).Any(item => item.Identifier == "select-profile.clear-save"));
        Check(Choices("select-profile", [Box("select-profile.clear-save", true)], deferred: true).Any(item => item.Identifier == "select-profile.clear-save"));
        Check(Choices("select-profile", hidesCompleted: false).Any(item => item.Identifier == "select-profile.clear-save"));
        var title = Choices("main-title", custom: [new("main-title.options", "duplicate"), new("custom.banner", "Banner")]);
        Check(title.Count(item => LabelingClassIdentity.CanonicalIdentifier(item.Identifier) == "shared.options") == 1);
        Check(title.Last().Identifier == "custom.banner");
        Check(Choices("game", [Box("custom.bench")], custom: [new("custom.bench", "Bench")]).Last().Identifier == "custom.bench");
    }
}
