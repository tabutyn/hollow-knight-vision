using System.IO.Compression;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class HackerAtlasArchiveTests
{
    public static void TestRestoresPlaythroughAndContinuesCompositing()
    {
        using var workspace = new Fixture();
        var pipeline = new HackerAtlasPipeline();
        var preparer = new HackerFramePreparer();
        _ = Integrate(pipeline, "Tutorial_01", -64, 1, preparer);
        var original = Integrate(pipeline, "Tutorial_02", 180, 2, preparer);
        var offsets = new Dictionary<string, AtlasPoint> { ["Tutorial_02"] = new(12.5, -6) };
        var archived = pipeline.CaptureArchive(offsets);
        Check(archived.Archive.RegistrationCount == 2 && original.RoomCount == 2 && original.Connections.Count == 1,
            "Two captured rooms and their measured doorway should be archived.");

        var first = new SaveProfileAtlas(1, Guid.NewGuid());
        var reused = new SaveProfileAtlas(1, Guid.NewGuid());
        workspace.Archives.Write(first, archived.Archive);
        Check(workspace.Archives.Read(reused) is null, "A new playthrough cannot inherit the previous map from a reused slot.");
        var other = new HackerAtlasPipeline();
        _ = Integrate(other, "Different", 10, 1);
        workspace.Archives.Write(reused, other.CaptureArchive(new Dictionary<string, AtlasPoint>()).Archive);
        Check(Read(workspace.Archives, reused).Rooms.Single().SceneName == "Different", "Each playthrough needs its own rooms.");

        var loaded = Read(workspace.Archives, first);
        Check(loaded.CreatedAt == archived.Archive.CreatedAt && loaded.RegistrationCount == 2
              && loaded.RoomOffsets.Single().Value == offsets["Tutorial_02"], "Registration metadata and edited room positions must survive.");
        var restored = HackerAtlasPipeline.FromArchive(loaded);
        var restoredOutput = restored.Snapshot();
        Check(original.Connections.SequenceEqual(restoredOutput.Connections), "The restored rooms lost their measured doorway.");
        Check(original.AtlasBounds == restoredOutput.AtlasBounds, "The composed Atlas bounds changed after load.");
        foreach (var expected in original.Rooms)
        {
            var actual = restoredOutput.Rooms.Single(room => room.SceneName == expected.SceneName);
            Check(actual.Id == expected.Id && actual.Position == expected.Position && actual.Bounds == expected.Bounds
                && actual.TerrainProjection == expected.TerrainProjection, "Room identities, anchors and projections must survive.");
            foreach (var tile in expected.AtlasTiles)
            {
                var saved = actual.AtlasTiles.Single(item => item.Id == tile.Id);
                Check(saved.Bounds == tile.Bounds && saved.Image.Pixels.SequenceEqual(tile.Image.Pixels),
                    "The archive must reproduce the exact composited BGRA tile.");
            }
        }
        var restoredPreparer = new HackerFramePreparer();
        restoredPreparer.RestoreRoomAnchors(loaded.Rooms);
        var expectedNext = Prepare("Tutorial_02", 800, 3, preparer);
        var actualNext = Prepare("Tutorial_02", 800, 3, restoredPreparer);
        Check(actualNext.AnchorPosition == expectedNext.AnchorPosition && actualNext.LiveBounds == expectedNext.LiveBounds,
            "A different camera position after loading must keep the existing pixel origin.");
        var resumed = restored.Process(actualNext) ?? throw new InvalidOperationException("Expected resumed composition.");
        var priorRoom = restoredOutput.Rooms.Single(room => room.SceneName == "Tutorial_02");
        var extended = resumed.Rooms.Single(room => room.Id == priorRoom.Id);
        Check(extended.AtlasTiles.Count > priorRoom.AtlasTiles.Count && extended.Bounds.MinX == priorRoom.Bounds.MinX,
            "New observations must extend the restored room without shrinking its existing bounds.");
        Check(priorRoom.AtlasTiles.All(tile => extended.AtlasTiles.Single(item => item.Id == tile.Id)
              .Image.Pixels.SequenceEqual(tile.Image.Pixels)), "Nonoverlapping observations must keep every restored pixel.");
        Check(restored.CaptureArchive(offsets).Archive.RegistrationCount == 3
            && resumed.Connections.SequenceEqual(original.Connections), "Resume must retain existing registrations and doorways.");
    }

    public static void TestArchiveWriteAndReadFailurePreserveLastGoodVersion()
    {
        using var workspace = new Fixture();
        var pipeline = new HackerAtlasPipeline();
        _ = Integrate(pipeline, "Tutorial_01", 0, 1);
        var original = pipeline.CaptureArchive(new Dictionary<string, AtlasPoint>()).Archive;
        workspace.Archives.Write(null, original);
        var unassigned = Path.Combine(workspace.ArchiveRoot, "unassigned.zip");
        var originalBytes = File.ReadAllBytes(unassigned);
        var room = original.Rooms.Single();
        var tile = room.Tiles.First();
        var malformed = original with
        {
            Rooms = [room with { Tiles = [tile with { Image = TexturedFrame(1, 1, 0) }] }]
        };
        Throws<InvalidDataException>(() => workspace.Archives.Write(null, malformed));
        Check(File.ReadAllBytes(unassigned).SequenceEqual(originalBytes)
            && Directory.GetFiles(workspace.ArchiveRoot).Length == 1, "A failed write cannot replace the prior map or retain staging data.");
        var wrong = new SaveProfileAtlas(2, Guid.NewGuid());
        File.Copy(unassigned, Path.Combine(workspace.ArchiveRoot, $"{wrong.Id:N}.zip"));
        Throws<InvalidDataException>(() => workspace.Archives.Read(wrong));
        using (var zip = ZipFile.Open(unassigned, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(item => item.Name.EndsWith(".bgra", StringComparison.Ordinal));
            var name = entry.FullName;
            entry.Delete();
            using var truncated = zip.CreateEntry(name).Open();
            truncated.WriteByte(7);
        }
        Throws<InvalidDataException>(() => workspace.Archives.Read(null));

        var another = new SaveProfileAtlas(4, Guid.NewGuid());
        workspace.Archives.Write(another, original);
        workspace.Archives.Write(null, new HackerAtlasPipeline().CaptureArchive(new Dictionary<string, AtlasPoint>()).Archive);
        Check(workspace.Archives.Read(null) is { RegistrationCount: 0, Rooms.Count: 0 }
            && Read(workspace.Archives, another).Rooms.Count == 1,
            "An explicit empty replacement must clear only its own archive.");
    }

    public static void TestProfilePreflightKeepsBothWritersOnFailure()
    {
        using var workspace = new Fixture();
        var pipeline = new HackerAtlasPipeline();
        _ = Integrate(pipeline, "Tutorial_01", 4, 1);
        var legacy = pipeline.CaptureArchive(new Dictionary<string, AtlasPoint>()).Archive;
        var states = new AtlasStateStore(Path.Combine(workspace.Root, "states"));
        var active = Directory.CreateDirectory(Path.Combine(workspace.Root, "active")).FullName;
        VisionResetController.WritePersistedAtlasMetadata(active, new(0, DateTimeOffset.UtcNow, 32, 1));
        var adopted = VisionResetController.ActivateProfileAtlas(states, active, new(1, false), 32, 1,
            prepareProfile: (profile, adopt) =>
            {
                Check(adopt && SaveProfileAtlas.Read(active) is null, "Legacy adoption must be staged before either active writer changes.");
                workspace.Archives.Write(profile, legacy);
            });
        Check(adopted.Replacement is null && SaveProfileAtlas.Read(active) == adopted.Profile,
            "Adoption should keep the previous Gameplay Atlas in place.");
        byte[] priorArchive = File.ReadAllBytes(Path.Combine(workspace.ArchiveRoot, $"{adopted.Profile.Id:N}.zip"));
        Throws<IOException>(() => VisionResetController.ActivateProfileAtlas(states, active, new(2, true), 32, 1,
            prepareProfile: (_, _) => throw new IOException("Fixture Hacker archive unavailable.")));
        Check(SaveProfileAtlas.Read(active) == adopted.Profile
            && File.ReadAllBytes(Path.Combine(workspace.ArchiveRoot, $"{adopted.Profile.Id:N}.zip")).SequenceEqual(priorArchive),
            "A failed Hacker preflight must preserve the previously selected save in both writers.");
        var changed = VisionResetController.ActivateProfileAtlas(states, active, new(2, true), 32, 1,
            prepareProfile: (profile, adopt) =>
            {
                Check(!adopt && workspace.Archives.Read(profile) is null, "A fresh profile needs an empty Hacker map.");
            });
        Check(changed.Profile.Slot == 2 && changed.Replacement?.ValidatedWorld.Metadata.RegistrationCount == 0
            && Read(workspace.Archives, adopted.Profile).Rooms.Count == 1,
            "A fresh Gameplay Atlas must not destroy the previous Hacker data.");
    }

    public static void TestNamedStatesPreserveCurrentAndOriginalPlaythroughs()
    {
        using var workspace = new Fixture();
        var originalOwner = new SaveProfileAtlas(1, Guid.NewGuid());
        var currentOwner = new SaveProfileAtlas(2, Guid.NewGuid());
        var original = new HackerAtlasPipeline();
        var current = new HackerAtlasPipeline();
        _ = Integrate(original, "Tutorial_01", 30, 1);
        _ = Integrate(current, "Town", -40, 1);
        var offsets = new Dictionary<string, AtlasPoint>();
        var originalArchive = original.CaptureArchive(offsets).Archive;
        var currentArchive = current.CaptureArchive(offsets).Archive;
        workspace.Archives.Write(originalOwner, originalArchive);
        workspace.Archives.Write(currentOwner, currentArchive);
        var snapshot = workspace.Archives.SaveState("  Before Dirtmouth  ", originalOwner, originalArchive);
        Check(workspace.Archives.ListStates().Single().Id == snapshot.Id && snapshot.Profile == originalOwner
            && snapshot.Name == "Before Dirtmouth", "The named library must preserve the snapshot's owner and name.");
        Check(workspace.Archives.NamedBytes(snapshot) > 0 && workspace.Archives.ActiveBytes(currentOwner) > 0
            && workspace.Archives.ActiveBytes(new SaveProfileAtlas(3, Guid.NewGuid())) == 0,
            "The named and active archive sizes must describe their own files.");

        var loaded = workspace.Archives.LoadState(snapshot);
        var backup = workspace.Archives.Replace(currentOwner, currentArchive, loaded, "Before load");
        Check(backup is not null && backup.Profile == currentOwner
            && workspace.Archives.LoadState(backup).Rooms.Single().SceneName == "Town",
            "Load must keep the current autosave as a named restore point.");
        Check(Read(workspace.Archives, currentOwner).Rooms.Single().SceneName == "Tutorial_01"
            && Read(workspace.Archives, originalOwner).Rooms.Single().Id == loaded.Rooms.Single().Id,
            "The selected snapshot is copied to the current playthrough.");
        Check(workspace.Archives.LoadState(snapshot).Rooms.Single().Id == loaded.Rooms.Single().Id,
            "Loading must preserve the immutable snapshot.");

        var empty = new HackerAtlasPipeline().CaptureArchive(offsets).Archive;
        var beforeNew = workspace.Archives.Replace(currentOwner, loaded, empty, "Before New");
        Check(Read(workspace.Archives, currentOwner).Rooms.Count == 0 && beforeNew is not null
            && workspace.Archives.LoadState(beforeNew).Rooms.Count == 1,
            "New must preserve the previous active raster and start with empty data.");
        workspace.Archives.DeleteState(snapshot);
        Check(!workspace.Archives.ListStates().Any(state => state.Id == snapshot.Id)
            && workspace.Archives.ListStates().Any(state => state.Id == beforeNew!.Id), "Delete must remove only the selected named snapshot.");
        Check(Read(workspace.Archives, originalOwner).Rooms.Count == 1 && Read(workspace.Archives, currentOwner).Rooms.Count == 0,
            "A named delete must preserve both active archives.");
    }

    private static HackerAtlasArchive Read(HackerAtlasArchiveStore store, SaveProfileAtlas profile) =>
        store.Read(profile) ?? throw new InvalidOperationException("The profile's archive is missing.");

    private static HackerAtlasOutput Integrate(HackerAtlasPipeline pipeline, string scene, double x, int index,
        HackerFramePreparer? preparer = null) =>
        pipeline.Process(Prepare(scene, x, index, preparer ?? new HackerFramePreparer()))
        ?? throw new InvalidOperationException("Expected a registered playable image.");

    private static HackerPreparedFrame Prepare(string scene, double x, int index, HackerFramePreparer preparer)
    {
        const int width = 192;
        const int height = 128;
        const double scale = 20;
        var image = TexturedFrame(width, height, index);
        var telemetry = new ReceiverGroundTruthSample(ReceiverProtocol.ControlVersion, "groundTruth", Guid.Empty,
            (ulong)index, index, index, scene, true, x / scale + 2, 2, 0, 1, 0, true, true, true,
            x / scale, 2, 0, x / scale, 2, 0, height / (2 * scale), scale, scale,
            width - 12, height / 2, width, height, width, height) { AtlasGameplayActive = true };
        return preparer.Process(new(new HackerCapturedFrame(index, image, index, true), telemetry, index))
            ?? throw new InvalidOperationException("Expected a registered playable image.");
    }

    private static BgraFrame TexturedFrame(int width, int height, int index)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(34 + (x * 3 + index * 7) % 150);
            pixels[offset + 1] = (byte)(65 + y * 5 % 140);
            pixels[offset + 2] = (byte)(89 + (x + y * 3) % 100);
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(width, height, width * 4, pixels, index, DateTimeOffset.UnixEpoch);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("hkv-hacker-atlas-archive-").FullName;
        public string ArchiveRoot => Path.Combine(Root, "maps");
        public HackerAtlasArchiveStore Archives { get; }

        public Fixture() => Archives = new(ArchiveRoot);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
