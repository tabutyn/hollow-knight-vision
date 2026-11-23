using HollowKnightVision.Windows.Core;

internal static class AtlasPathLinkStoreTests
{
    internal static void TestConnectionsPersistReprojectAndDeleteAsAWhole()
    {
        WithPath(path =>
        {
            var graph = Graph();
            var normal = Calibration();
            var store = new AtlasPathLinkStore(path);
            Require(!store.Click(graph, new(40, 40), 4, normal, AtlasPathEditTool.DoubleJump)
                && store.Selection?.Point == new AtlasPathPoint(40, 20) && !File.Exists(path),
                "The first click should select the ground position without saving.");
            Require(store.Click(graph, new(130, 80), 4, normal, AtlasPathEditTool.DoubleJump)
                && store.Selection is null && store.Revision == 1, "The second floor should save a double jump.");
            var reloaded = new AtlasPathLinkStore(path);
            var expected = new AtlasPathConnection(new(40, 20), new(130, 80), true);
            Require(reloaded.Connections(normal).SequenceEqual(new[] { expected }),
                "Room links must survive reopening.");
            var moved = normal with { Room = "alias", Origin = new(100, -50), Scale = new(4, 4) };
            Require(reloaded.Connections(moved).SequenceEqual(new AtlasPathConnection[]
            {
                new(new(180, -10), new(360, 110), true)
            }), "Other atlases should adopt the authored world connection at their own scale and origin.");
            Require(reloaded.HitDistance(graph, new(85, 105), normal, AtlasPathEditTool.Delete) == 0,
                "Clicking the shelf's horizontal stroke should target the stored connector.");
            Require(reloaded.Click(graph, new(85, 105), 5, normal, AtlasPathEditTool.Delete),
                "Deleting a shelf should save the deletion.");
            var after = new AtlasPathLinkStore(path);
            Require(after.Connections(normal).Count == 0 && after.Connections(moved).Count == 0,
                "Deletion should remove the whole shelf from room and scene views.");
            Require(!after.Click(graph, new(85, 105), 5, normal, AtlasPathEditTool.Delete),
                "Deleting with no authored connections should leave the authoring state alone.");
        });
    }

    internal static void TestManualConnectionReplacesItsPairAndRespectsRoomSelection()
    {
        WithPath(path =>
        {
            var graph = Graph();
            var store = new AtlasPathLinkStore(path);
            var first = Calibration();
            Require(!store.Click(graph, new(15, 20), 2, first, AtlasPathEditTool.Jump)
                && !store.Click(graph, new(45, 20), 2, first, AtlasPathEditTool.Jump)
                && store.Selection?.Point.X == 45, "Two clicks on one floor should move the pending endpoint.");
            var second = first with { Room = "different", Scene = "other" };
            Require(!store.Click(graph, new(130, 100), 2, second, AtlasPathEditTool.Jump)
                && store.Selection?.Room == "different", "Editing a different room must replace the incomplete selection.");
            store.CancelSelection();
            Require(!store.Click(graph, new(250, 120), 3, first, AtlasPathEditTool.Jump)
                && store.Selection is null, "Clicking out of range should have no effect.");
            ClickPair(AtlasPathEditTool.Jump, 30, 150);
            ClickPair(AtlasPathEditTool.DoubleJump, 25, 140);
            Require(store.Connections(first).Single() == new AtlasPathConnection(new(25, 20), new(140, 80), true),
                "Editing a pair should replace the first marked connection and its kind.");

            void ClickPair(AtlasPathEditTool tool, int a, int b)
            {
                Require(!store.Click(graph, new(a, 20), 3, first, tool)
                    && store.Click(graph, new(b, 80), 3, first, tool), "The pair should save.");
            }
        });
    }

    internal static void TestMacLegacyAndSceneLinksLoadAndInvalidOrUnwritableFileSurvives()
    {
        WithPath(path =>
        {
            var link = """
                {"id":"378A5CD1-17C8-4001-AD23-F37F4E559D3D","first":{"x":10,"y":10},
                 "second":{"x":60,"y":40},"doubleJump":true}
                """;
            var legacy = "{\"room\":[" + link + "]}";
            File.WriteAllText(path, legacy);
            Require(new AtlasPathLinkStore(path).Connections(Calibration()).Single() ==
                new AtlasPathConnection(new(10, 10), new(60, 40), true), "The original room schema should still load.");
            File.WriteAllText(path, "{\"rooms\":{},\"scenes\":{\"Tutorial_01\":[" + link + "]}}");
            Require(new AtlasPathLinkStore(path).Connections(Calibration()).Single() ==
                new AtlasPathConnection(new(20, 20), new(120, 80), true), "The Mac scene schema should use world-to-room projection.");

            var invalid = "{\"rooms\":{},\"scenes\":{\"Tutorial_01\":[{\"first\":{}}]}}";
            File.WriteAllText(path, invalid);
            var broken = new AtlasPathLinkStore(path);
            AddAndFail(broken);
            Require(File.ReadAllText(path) == invalid && broken.Issue?.Contains("could not be loaded", StringComparison.Ordinal) == true,
                "An invalid input file should survive an attempted edit byte for byte.");

            var blocker = Path.Combine(Path.GetDirectoryName(path)!, "file-instead-of-folder");
            File.WriteAllText(blocker, "existing content");
            var unwritable = new AtlasPathLinkStore(Path.Combine(blocker, "connections.json"));
            AddAndFail(unwritable);
            Require(File.ReadAllText(blocker) == "existing content" && unwritable.Issue?.Contains("could not be saved", StringComparison.Ordinal) == true,
                "A failed atomic save should preserve the previous filesystem entries.");
        });

        static void AddAndFail(AtlasPathLinkStore store)
        {
            var graph = Graph();
            var calibration = Calibration();
            Require(!store.Click(graph, new(30, 20), 2, calibration, AtlasPathEditTool.Jump)
                && !store.Click(graph, new(130, 80), 2, calibration, AtlasPathEditTool.Jump)
                && store.Revision == 0 && store.Connections(calibration).Count == 0,
                "Failed saves should preserve the committed editor state.");
        }
    }

    private static AtlasPlatformGraph Graph() => new([new(0, 80, 20), new(100, 180, 80)], [], height: 20, width: 10);
    private static AtlasPathCalibration Calibration() => new("room", "Tutorial_01", new(0, 0), new(2, 2));

    private static void WithPath(Action<string> run)
    {
        var directory = Directory.CreateTempSubdirectory("hkv-path-links-");
        try { run(Path.Combine(directory.FullName, AtlasPathLinkStore.Filename)); }
        finally { directory.Delete(recursive: true); }
    }

    private static void Require(bool value, string explanation)
    {
        if (!value) throw new InvalidOperationException(explanation);
    }
}
