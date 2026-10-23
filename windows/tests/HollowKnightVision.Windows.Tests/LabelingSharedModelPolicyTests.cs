using HollowKnightVision.Windows.Core;

internal static class LabelingSharedModelPolicyTests
{
    private static readonly MenuStencilLabel[] UserObjects =
    [
        new("custom.bench", "Bench")
    ];

    public static void TestSharedModelOwnsDynamicGameplayClassesOnly()
    {
        Equal(
            LabelingDatasetExporter.SharedObjectModelIdentifier,
            LabelingSharedModelPolicy.ModelIdentifier);

        var expected = LabelingContractCatalog.Contexts
            .Single(context => context.Identifier == "game")
            .Labels
            .Select(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier))
            .ToHashSet(StringComparer.Ordinal);
        expected.ExceptWith(LabelingHudStencilPolicy.ClassIdentifiers);
        expected.UnionWith(UserObjects.Select(label => label.Identifier));
        var actual = LabelingSharedModelPolicy.ClassIdentifiers(UserObjects);

        Check(actual.SetEquals(expected));
        Check(!actual.Contains("game.mana"));
        Check(!actual.Contains("game.health"));
        Check(!actual.Contains("game.geo"));
        Check(actual.Contains("game.playable-knight"));
        Check(actual.Contains("enemies.crawlid"));
        Check(actual.Contains("world.sign"));
        Check(actual.Contains("custom.bench"));
        Check(!actual.Contains("main-title.start-game"));
        Check(!actual.Contains("inventory.inventory"));

        Check(LabelingObjectReferencePolicy.ModelClasses(UserObjects)
            .Select(label => label.Identifier)
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(actual));
    }

    public static void TestLabelingCatalogKeepsMenuClassesForStencils()
    {
        var expected = LabelingContractCatalog.Contexts
            .SelectMany(context => context.Labels)
            .Select(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier))
            .Concat(UserObjects.Select(label => label.Identifier))
            .ToHashSet(StringComparer.Ordinal);
        var actual = LabelingCatalogPolicy.ClassIdentifiers(UserObjects);

        Check(actual.SetEquals(expected));
        Check(actual.Contains("main-title.start-game"));
        Check(actual.Contains("inventory.inventory"));
        Check(actual.Contains("custom.bench"));
        Check(actual.IsSupersetOf(LabelingHudStencilPolicy.ClassIdentifiers));
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected {expected}; received {actual}.");
        }
    }

    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Labeling shared-model policy assertion failed.");
        }
    }
}
