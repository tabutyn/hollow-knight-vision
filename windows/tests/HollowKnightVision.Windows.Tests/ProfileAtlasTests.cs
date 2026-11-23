using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

internal static class ProfileAtlasTests
{
    public static void TestRequiresStableProfileAndGameplay()
    {
        var detector = new ProfileLaunchDetector();
        var occupied = Card(1, 2, [1, 2, 3, 4], [2]);
        Check(detector.Observe(occupied, false, 10, 1) is null, "One menu image cannot arm a launch.");
        _ = detector.Observe(occupied, false, 10.2, 1);
        Check(!detector.AwaitingGameplay, "A stale menu image cannot arm a launch.");
        _ = detector.Observe(occupied with { SourceTimestamp = 1.2, OccupiedProfileSlots = [] }, false, 10.3, 1);
        Check(detector.AwaitingGameplay, "A checked card and stable selectors should arm the launch.");
        var faded = occupied with
        {
            SourceTimestamp = 1.3,
            SelectedOptionIdentifier = "shared.back",
            CheckedProfileSlots = [],
            OccupiedProfileSlots = []
        };
        _ = detector.Observe(faded, false, 10.4, 1);
        Check(detector.AwaitingGameplay, "A grace frame without card evidence cannot replace the measured selection.");
        _ = detector.Observe(null, false, 11, 1);
        _ = detector.Observe(occupied with { ContextIdentifier = "brightness" }, false, 12, 1);
        Check(detector.Observe(null, true, 13, 1) is null, "One HUD sample cannot switch the Atlas.");
        _ = detector.Observe(null, false, 13.05, 1);
        Check(detector.Observe(null, true, 13.2, 1) is null, "HUD confirmation must be continuous.");
        Check(detector.Observe(null, true, 13.4, 1) == new ProfileLaunch(2, false),
            "The last chosen slot was occupied before the fade.");
        Check(detector.Observe(null, true, 13.6, 1) is null && !detector.AwaitingGameplay,
            "A confirmed launch must be consumed exactly once.");
    }

    public static void TestAbandonedSelectionsCannotSwitchAtlases()
    {
        var detector = new ProfileLaunchDetector();
        var empty = Card(1, 4, [1, 2, 3, 4], [2]);
        _ = detector.Observe(empty with { CheckedProfileSlots = [1, 2, 3] }, false, 10, 1);
        _ = detector.Observe(empty with { SourceTimestamp = 1.2 }, false, 10.2, 1);
        Check(!detector.AwaitingGameplay, "An unsearched selected slot must never be treated as empty.");
        _ = detector.Observe(empty with { SourceTimestamp = 1.4 }, false, 10.4, 1);
        Check(detector.AwaitingGameplay, "The searched empty card should arm.");
        _ = detector.Observe(empty with { SourceTimestamp = 1.5, SelectedOptionIdentifier = "shared.back" }, false, 10.5, 1);
        Check(!detector.AwaitingGameplay, "Selecting Back clears the previous card.");
        _ = detector.Observe(empty with { SourceTimestamp = 2 }, false, 11, 1);
        _ = detector.Observe(empty with { SourceTimestamp = 2.2 }, false, 11.2, 1);
        _ = detector.Observe(empty with { ContextIdentifier = "clear-save" }, false, 11.4, 1);
        Check(!detector.AwaitingGameplay, "Clear Save cancels a pending launch.");
        _ = detector.Observe(empty with { SourceTimestamp = 3 }, false, 12, 1);
        _ = detector.Observe(empty with { SourceTimestamp = 3.2 }, false, 12.2, 1);
        Check(detector.Observe(null, true, 12.4, 2) is null && !detector.AwaitingGameplay,
            "A replaced capture source cannot confirm a stale selected slot.");
        _ = detector.Observe(empty, false, 13, 2);
        _ = detector.Observe(empty with { SourceTimestamp = 1.2 }, false, 13.2, 2);
        _ = detector.Observe(null, true, 13.4, 2);
        Check(detector.Observe(null, true, 13.6, 2) == new ProfileLaunch(4, true),
            "A stable empty card can launch a new playthrough.");
    }

    public static void TestProfileAtlasRoundTrip()
    {
        using var workspace = new Fixture();
        var firstTile = new AtlasTileCoordinate(-1, 2);
        workspace.AddTile(firstTile, 73);
        var initial = workspace.Switch(new(1, false));
        Check(initial.Replacement is null && SaveProfileAtlas.Read(workspace.Active) == initial.Profile,
            "The first known occupied profile should adopt an existing unassigned Atlas.");
        Check(workspace.Switch(new(1, false)).Replacement is null, "The same playthrough should retain its active world.");
        var second = workspace.Switch(new(2, true));
        Check(second.Replacement?.ValidatedWorld.Metadata.RegistrationCount == 0, "A new save needs an empty Atlas.");
        Check(workspace.ReadTile(firstTile) is null, "The new Atlas inherited the prior profile's tiles.");
        var archivedFirst = workspace.KeepPrevious(second);
        Check(archivedFirst.Profile == initial.Profile && archivedFirst.Name == "Atlas1",
            "The archived world must carry its owner and slot name.");
        var secondTile = new AtlasTileCoordinate(3, -5);
        workspace.AddTile(secondTile, 149);
        var restored = workspace.Switch(new(1, false));
        Check(restored.Profile == initial.Profile && workspace.ReadTile(firstTile) == 73
            && workspace.ReadTile(secondTile) is null, "Returning to the occupied slot must restore its exact tiles.");
        Check(workspace.KeepPrevious(restored).Profile == second.Profile, "The departing playthrough remains recoverable.");

        var reused = workspace.Switch(new(1, true));
        Check(reused.Profile.Slot == 1 && reused.Profile.Id != initial.Profile.Id && workspace.ReadTile(firstTile) is null,
            "Clearing a slot must create a different Atlas identity without restoring old tiles.");
        Check(SaveProfileAtlas.Read(workspace.States.WorldPath(workspace.States.List().Single(state => state.Id == archivedFirst.Id)))
            == initial.Profile, "Reusing a slot cannot modify its previous named archive.");
    }

    public static void TestProfileAtlasRollback()
    {
        using var workspace = new Fixture();
        var first = workspace.Switch(new(1, true));
        var coordinate = new AtlasTileCoordinate(-3, 4);
        workspace.AddTile(coordinate, 38);

        try
        {
            _ = VisionResetController.ActivateProfileAtlas(workspace.States, workspace.Active, new(2, true),
                Fixture.TileSize, Fixture.Scale, _ => throw new IOException("Cannot reopen the new world."));
            throw new Exception("A failed activation must fail the switch.");
        }
        catch (IOException error) when (error.Message == "Cannot reopen the new world.") { }
        Check(SaveProfileAtlas.Read(workspace.Active) == first.Profile && workspace.ReadTile(coordinate) == 38,
            "A failed activation must restore the previous Atlas and identity.");

        var wrongOwner = new SaveProfileAtlas(2, Guid.NewGuid());
        var bad = workspace.States.Save("Corrupt backup", location =>
        {
            Directory.CreateDirectory(location);
            VisionResetController.WritePersistedAtlasMetadata(location, new(0, DateTimeOffset.UtcNow, Fixture.TileSize, Fixture.Scale));
            wrongOwner.Write(location);
        });
        new SaveProfileAtlas(3, Guid.NewGuid()).Write(workspace.States.WorldPath(bad));
        try
        {
            _ = workspace.Switch(new(2, false));
            throw new Exception("The mismatched archive should have been rejected.");
        }
        catch (InvalidDataException) { }
        Check(SaveProfileAtlas.Read(workspace.Active) == first.Profile && workspace.ReadTile(coordinate) == 38,
            "A mismatched archive must leave the active Atlas unchanged.");
    }

    private static MenuStencilResult Card(double time, int slot, int[] checkedSlots, int[] occupiedSlots)
    {
        MenuStencilMatch[] pair = [new("selector", "Selector", new(10, 20, 8, 8), 1, null), new("selector", "Selector", new(50, 20, 8, 8), 1, null)];
        return new("select-profile", true, 1, $"Save {slot}", [], pair, [], 0, 0, 1,
            MenuStencilPhase.Tracking, 0, time, "en")
        {
            SelectedOptionIdentifier = $"select-profile.slot-{slot}",
            CheckedProfileSlots = checkedSlots,
            OccupiedProfileSlots = occupiedSlots
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal const int TileSize = 32;
        internal const double Scale = 8;
        private readonly string root = Directory.CreateTempSubdirectory("hkv-profile-atlas-").FullName;
        public string Active { get; }
        public AtlasStateStore States { get; }

        public Fixture()
        {
            Active = Directory.CreateDirectory(Path.Combine(root, "active")).FullName;
            States = new(Path.Combine(root, "states"));
            VisionResetController.WritePersistedAtlasMetadata(Active, new(0, DateTimeOffset.UtcNow, TileSize, Scale));
        }

        public VisionResetController.ProfileAtlasActivation Switch(ProfileLaunch launch) =>
            VisionResetController.ActivateProfileAtlas(States, Active, launch, TileSize, Scale);

        public AtlasSavedState KeepPrevious(VisionResetController.ProfileAtlasActivation activated)
        {
            var path = activated.Replacement?.ArchivedPreviousAtlasDirectory ?? throw new Exception("The previous Atlas was lost.");
            var owner = SaveProfileAtlas.Read(path) ?? throw new Exception("The previous Atlas lost its owner.");
            return VisionResetController.ImportPreviousActiveAtlas(States, path, owner.Name);
        }

        public void AddTile(AtlasTileCoordinate coordinate, byte blue)
        {
            var pixels = new byte[TileSize * TileSize * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = blue;
                pixels[index + 3] = 255;
            }
            BmpWriter.Write(new BgraFrame(TileSize, TileSize, TileSize * 4, pixels, 0, DateTimeOffset.UnixEpoch),
                Path.Combine(Active, $"tile_{coordinate.X}_{coordinate.Y}.bmp"));
        }

        public byte? ReadTile(AtlasTileCoordinate coordinate) =>
            VisionResetController.ReadPersistedAtlas(Active).Mosaic.Snapshot()
                .SingleOrDefault(tile => tile.Coordinate == coordinate)?.Frame.Pixels[0];

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
