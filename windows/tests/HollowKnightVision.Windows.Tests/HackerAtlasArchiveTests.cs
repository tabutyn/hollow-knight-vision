using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class HackerAtlasArchiveTests
{
    public static void TestRoadInspectionUsesSavedRoomAndAuthoredGraph() => CheckRoadInspection();

    public static void CheckRoadInspection(string? host = null)
    {
        using var workspace = new Fixture();
        var pipeline = new HackerAtlasPipeline();
        _ = Integrate(pipeline, "Room", -64, 1);
        _ = Integrate(pipeline, "Unlabeled", 180, 2);
        var archive = pipeline.CaptureArchive(new Dictionary<string, AtlasPoint>()).Archive;
        var rooms = archive.Rooms.Select(room => room with { TerrainProjection = new(new(8, 7), new(17, 23)) }).ToArray();
        archive = archive with { Rooms = rooms };
        var labels = new GroundTruthLineDocument { Lines =
        [
            new("Room", -35, 35, -35, GroundTruthLineKind.Positive, false),
            new("Room", -15, 4, -2, GroundTruthLineKind.Positive, false),
            new("Room", 7, 18, 11, GroundTruthLineKind.Positive, false),
            new("Room", 7, 18, 13, GroundTruthLineKind.Positive, true)
        ] };
        var profile = new SaveProfileAtlas(1, Guid.NewGuid());
        workspace.Archives.Write(profile, archive);
        var active = Path.Combine(workspace.ArchiveRoot, $"{profile.Id:N}.zip");
        var state = workspace.Archives.SaveState("Road fixture", profile, archive);
        var named = new AtlasStateStore(Path.Combine(workspace.ArchiveRoot, "states")).WorldPath(state);
        var links = Path.Combine(workspace.Root, "links.json");
        File.WriteAllText(links, JsonSerializer.Serialize(new
        {
            rooms = new Dictionary<string, object>(),
            scenes = new Dictionary<string, object>
            {
                ["Room"] = new[] { new { id = Guid.NewGuid(), first = new { x = 4, y = -2 },
                    second = new { x = 7, y = 11 }, doubleJump = true } }
            }
        }));
        var connections = new AtlasPathLinkStore(links);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var before = File.ReadAllBytes(active);
        var linksBefore = File.ReadAllBytes(links);
        var json = JsonSerializer.Serialize(AtlasRoadInspection.Inspect(active, labels, connections), options);
        Check(json == JsonSerializer.Serialize(AtlasRoadInspection.Inspect(named, labels, connections), options),
            "A named state and active ZIP with identical room geometry must report the same graph.");

        using var result = JsonDocument.Parse(json);
        var room = result.RootElement.EnumerateArray().Single(value => value.GetProperty("scene").GetString() == "Room");
        var strokes = room.GetProperty("strokes").EnumerateArray().ToArray();
        var nodes = room.GetProperty("nodes").EnumerateArray().ToDictionary(
            node => node.GetProperty("point").GetRawText(),
            node => node.GetProperty("neighbors").EnumerateArray().Select(point => point.GetRawText()).ToHashSet());
        Check(room.GetProperty("roomID").GetGuid() == rooms.Single(item => item.SceneName == "Room").Id
            && room.GetProperty("tileCount").GetInt32() > 0
            && room.GetProperty("floors").GetRawText() == "[[[-280,-245],[280,-245]],[[-120,-14],[32,-14]],[[56,77],[144,77]]]"
            && strokes.Any(stroke => stroke.GetProperty("kind").GetString() == "walk")
            && strokes.Any(stroke => stroke.GetProperty("kind").GetString() == "doubleJump")
            && nodes.Count > 2,
            "Report must use the archived scale, ignore non-authored lines and include the saved jump and its graph.");
        Check(strokes.Any(stroke =>
        {
            if (stroke.GetProperty("kind").GetString() != "drop") return false;
            var forward = stroke.GetProperty("forward").GetBoolean();
            var from = stroke.GetProperty(forward ? "from" : "to").GetRawText();
            var to = stroke.GetProperty(forward ? "to" : "from").GetRawText();
            return nodes.TryGetValue(from, out var down) && down.Contains(to)
                && (!nodes.TryGetValue(to, out var up) || !up.Contains(from));
        }), "A reported drop must have a forward neighbor with no reverse edge.");
        var unmarked = result.RootElement.EnumerateArray().Single(value => value.GetProperty("scene").GetString() == "Unlabeled");
        Check(unmarked.GetProperty("issue").GetString() == "No ground geometry is available." && unmarked.GetProperty("nodes").GetArrayLength() == 0,
            "Report rooms whose saved pixels have never been labeled.");
        Check(File.ReadAllBytes(active).SequenceEqual(before) && File.ReadAllBytes(links).SequenceEqual(linksBefore),
            "Inspection must preserve the archive and connections.");
        if (host is not null)
        {
            new GroundTruthLineStore(Path.Combine(workspace.Root, GroundTruthLineStore.Filename)).Save(labels);
            using var commandResult = JsonDocument.Parse(RunHost(0, active));
            var inspected = commandResult.RootElement.EnumerateArray().Single(value => value.GetProperty("scene").GetString() == "Room");
            Check(inspected.GetProperty("floors").GetRawText().Where(character => !char.IsWhiteSpace(character))
                .SequenceEqual(room.GetProperty("floors").GetRawText()), "The installed Host must use the same archived room graph.");
            _ = RunHost(64);
            _ = RunHost(1, Path.Combine(workspace.Root, "missing.zip"));
        }
        File.WriteAllText(links, "{");
        Throws<InvalidDataException>(() => AtlasRoadInspection.Inspect(active, labels, new(links)));

        string RunHost(int expectedCode, params string[] arguments)
        {
            var isAssembly = Path.HasExtension(host) && Path.GetExtension(host).Equals(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(isAssembly ? "dotnet" : host!)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (isAssembly) start.ArgumentList.Add(host!);
            start.ArgumentList.Add("--inspect-atlas-road");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["HKV_GROUND_LABEL_ROOT"] = workspace.Root;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("The Hacker Atlas road command timed out.");
            }
            Check(process.ExitCode == expectedCode, errors.GetAwaiter().GetResult());
            return output.GetAwaiter().GetResult();
        }
    }

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

    public static void TestGroundedDoorwayHeightsSurviveArchiveAndAirborneReturn()
    {
        foreach (var right in new[] { true, false })
        {
            using var workspace = new Fixture();
            var pipeline = new HackerAtlasPipeline();
            var preparer = new HackerFramePreparer();
            var session = Guid.NewGuid();
            var exit = right ? 60 : 4;
            var entry = right ? 4 : 60;
            for (var frame = 1; frame <= 3; frame++) Observe(frame, "A", exit, 12, grounded: true);
            Observe(4, "A", exit, 20, grounded: false);
            var provisional = Observe(5, "B", entry, 24, grounded: false)!.Connections.Single();
            Check(provisional is { HasGroundedAlignment: false, FromCoordinate: 20, ToCoordinate: 24 },
                "The airborne crossing must begin with a provisional doorway.");
            for (var frame = 6; frame <= 7; frame++) Observe(frame, "B", entry, 8, grounded: true);
            var revision = pipeline.Revision;
            var aligned = Observe(8, "B", entry, 8, grounded: true, compose: false)
                ?? throw new InvalidOperationException("An alignment change must publish even between tile writes.");
            var connection = aligned.Connections.Single();
            Check(connection is { HasGroundedAlignment: true, FromCoordinate: 12, ToCoordinate: 8 }
                && connection.Id == provisional.Id && connection.FromSide == (right ? HackerRoomSide.Right : HackerRoomSide.Left),
                "Three stable grounded observations must update the same horizontal doorway.");
            var layout = HackerRoomLayout.Make(aligned.Rooms, aligned.Connections);
            Check(layout.Positions[connection.FromRoomId].Y + 12 == layout.Positions[connection.ToRoomId].Y + 8,
                "Hacker room placement must align the two measured floor heights.");
            var offsets = new Dictionary<string, AtlasPoint> { ["B"] = new(15, 2) };
            var archive = pipeline.CaptureArchive(offsets);
            Check(archive.Revision > revision && archive.Archive.RegistrationCount == 7,
                "A topology-only update must be autosaveable without registering another image.");
            workspace.Archives.Write(null, archive.Archive);
            var saved = workspace.Archives.Read(null)!;
            Check(saved.Connections.Single() == connection && saved.RoomOffsets["B"] == offsets["B"],
                "Archives must persist the measured alignment and the user's room offset independently.");
            var named = workspace.Archives.SaveState("At the aligned doorway", null, saved);
            Check(workspace.Archives.LoadState(named).Connections.Single() == connection,
                "Named restore points must also keep the alignment.");

            pipeline = HackerAtlasPipeline.FromArchive(saved);
            preparer.RestoreRoomAnchors(saved.Rooms);
            Check(pipeline.Snapshot().Connections.Single() == connection, "Reopening must restore the grounded doorway.");
            Observe(9, "B", entry, 20, grounded: false);
            Check(Observe(10, "A", exit, 26, grounded: false)!.Connections.Single() == connection,
                "An airborne return must preserve the committed measurements.");

            var vertical = connection with { FromSide = HackerRoomSide.Top, ToSide = HackerRoomSide.Bottom };
            Throws<InvalidDataException>(() => workspace.Archives.Write(null, saved with { Connections = [vertical] }));
            Check(workspace.Archives.Read(null)?.Connections.Single() == connection,
                "A malformed floor flag must leave the existing archive intact.");

            HackerAtlasOutput? Observe(int frame, string scene, int x, int y, bool grounded, bool compose = true) =>
                ObserveDoorway(pipeline, preparer, session, frame, scene, x, y, right, grounded, compose: compose);
        }
    }

    public static void TestAirborneAndOutOfPositionSamplesDoNotAlignDoorways()
    {
        var pipeline = new HackerAtlasPipeline();
        var preparer = new HackerFramePreparer();
        var session = Guid.NewGuid();
        for (var frame = 1; frame <= 4; frame++) Observe(frame, "A", 60, 20, grounded: false);
        Observe(5, "B", 4, 24, grounded: false);
        for (var frame = 6; frame <= 8; frame++) Observe(frame, "B", 4, 24, grounded: false);
        CheckProvisional();

        // On the return trip B has a stable departure height. A still needs
        // three fresh measurements on the right side of its doorway.
        for (var frame = 9; frame <= 11; frame++) Observe(frame, "B", 4, 8, grounded: true);
        Observe(12, "A", 60, 26, grounded: false);
        for (var frame = 13; frame <= 15; frame++) Observe(frame, "A", 32, 12, grounded: true);
        for (var frame = 16; frame <= 18; frame++) Observe(frame, "A", 60, 12, grounded: true, verticalSpeed: .2);
        CheckProvisional();
        for (var frame = 19; frame <= 21; frame++) Observe(frame, "A", 60, 12, grounded: true);
        Check(pipeline.Snapshot().Connections.Single() is
            { HasGroundedAlignment: true, FromSide: HackerRoomSide.Right, FromCoordinate: 12, ToCoordinate: 8 },
            "A grounded return should update the original connection in its stored orientation.");

        pipeline.Reset();
        preparer.Reset();
        for (var frame = 1; frame <= 4; frame++) Observe(frame, "A", 60, 20, grounded: true);
        Observe(5, "B", 4, 24, grounded: false);
        for (var frame = 40; frame <= 42; frame++) Observe(frame, "B", 4, 8, grounded: true);
        CheckProvisional();

        void CheckProvisional() => Check(pipeline.Snapshot().Connections.Single() is
            { HasGroundedAlignment: false, FromCoordinate: 20, ToCoordinate: 24 },
            "Only fresh grounded samples near both ends of a passage can change its elevation.");
        void Observe(int frame, string scene, int x, int y, bool grounded, double verticalSpeed = 0) =>
            ObserveDoorway(pipeline, preparer, session, frame, scene, x, y, right: true, grounded, verticalSpeed: verticalSpeed);
    }

    public static void TestMeasuredReturnFloorsTakePriorityOverEstimatedSpacing()
    {
        var a = Room("A");
        var b = Room("B");
        var c = Room("C");
        var d = Room("D");
        var ab = Edge(a, b, HackerRoomSide.Right, grounded: true);
        var bc = Edge(b, c, HackerRoomSide.Bottom, grounded: false);
        var cd = Edge(c, d, HackerRoomSide.Left, grounded: true);
        var da = Edge(d, a, HackerRoomSide.Left, grounded: true);
        var layout = HackerRoomLayout.Make([a, b, c, d], [ab, bc, cd, da]);
        foreach (var edge in new[] { ab, cd, da })
            Check(layout.Positions[edge.FromRoomId].Y + edge.FromCoordinate
                == layout.Positions[edge.ToRoomId].Y + edge.ToCoordinate,
                "The measured return loop must preserve each grounded crossing's elevation.");
        Check(layout.FreeformConnectionIds.Contains(bc.Id), "Estimated vertical spacing must yield to measured floor heights.");

        static HackerAtlasRoom Room(string name) => new(Guid.NewGuid(), name, default, new(0, 0, 100, 100));
        static HackerRoomConnection Edge(HackerAtlasRoom from, HackerAtlasRoom to, HackerRoomSide side, bool grounded) =>
            new(Guid.NewGuid(), from.Id, to.Id, side, side.Opposite(), 50, 50) { HasGroundedAlignment = grounded };
    }

    private static HackerAtlasOutput? ObserveDoorway(HackerAtlasPipeline pipeline, HackerFramePreparer preparer,
        Guid session, int frame, string scene, int x, int y, bool right, bool grounded, bool compose = true, double verticalSpeed = 0)
    {
        var time = frame * .1;
        var camera = scene == "A" ? 10 : 100;
        var sample = new ReceiverGroundTruthSample(2, "groundTruth", session, (ulong)frame, frame, time, scene,
            true, 0, 0, 0, 0, verticalSpeed, right, grounded, true,
            camera, 4, -38, camera, 4, 0, 18, 1, 1, x, y, 64, 36, 64, 36);
        var image = TexturedFrame(64, 36, 0);
        var capture = new HackerCapturedFrame(HackerFrameSynchronizer.Key(frame), image, time, compose);
        var prepared = preparer.Process(new(capture, sample, time)) ?? throw new InvalidOperationException("Doorway frame was rejected.");
        return pipeline.Process(prepared);
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
        Throws<InvalidDataException>(() => workspace.Archives.Write(null, original with { Rooms = [room with { Tiles = [] }] }));
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

    public static void TestArchiveSavesOnlyRoomsWithCommittedPixels()
    {
        using var workspace = new Fixture();
        var pipeline = new HackerAtlasPipeline();
        var preparer = new HackerFramePreparer();
        var first = Integrate(pipeline, "A", 10, 1, preparer);
        var transition = Prepare("B", 100, 2, preparer, compose: false);
        Check(pipeline.Process(transition) is null && pipeline.Snapshot().RoomCount == 2,
            "A prepared room can have live geometry before its first registration.");

        var offsets = new Dictionary<string, AtlasPoint> { ["A"] = new(7, -3), ["B"] = new(-2, 1) };
        var pending = pipeline.CaptureArchive(offsets).Archive;
        Check(pending.Rooms.Count == 1 && pending.Rooms[0].Id == first.Rooms.Single().Id && pending.Connections.Count == 0
            && pending.RoomOffsets.Count == 1 && pending.RoomOffsets["A"] == offsets["A"]
            && pending.RegistrationCount == 1, "A restore point must contain only rooms, doorways, and offsets with committed pixels.");
        workspace.Archives.Write(null, pending);
        var named = workspace.Archives.SaveState("Before entering the next room", null, pending);

        var entered = Integrate(pipeline, "B", 101, 3, preparer);
        var committed = pipeline.CaptureArchive(offsets).Archive;
        Check(committed.Rooms.Count == 2 && committed.Connections.Single() == entered.Connections.Single()
            && committed.RoomOffsets["B"] == offsets["B"] && committed.RegistrationCount == 2,
            "The next registered image must make the new room, doorway, and offset saveable.");
        workspace.Archives.Write(null, committed);
        Check(workspace.Archives.Read(null)?.Rooms.Count == 2 && workspace.Archives.LoadState(named).Rooms.Count == 1,
            "Autosaving the new room must leave the older named state unchanged.");
    }

    public static void TestRestoresNamedRoomAndContinuesWithTheSameAnchor()
    {
        using var workspace = new Fixture();
        var pipeline = new HackerAtlasPipeline();
        var preparer = new HackerFramePreparer();
        for (var frame = 1; frame <= 3; frame++) _ = Integrate(pipeline, "A", 10 + frame, frame, preparer);
        var offsets = new Dictionary<string, AtlasPoint> { ["A"] = new(27, -3) };
        var original = pipeline.CaptureArchive(offsets).Archive;
        var saved = workspace.Archives.SaveState("In the first room", null, original);
        var loaded = workspace.Archives.LoadState(saved);
        var expected = original.Rooms.Single();
        var restored = loaded.Rooms.Single();
        Check(expected.Id == restored.Id && expected.Position == restored.Position
            && expected.CameraAnchor == restored.CameraAnchor && expected.ContentBounds == restored.ContentBounds
            && restored.Tiles.Count == expected.Tiles.Count && expected.Tiles.Zip(restored.Tiles).All(pair =>
                pair.First.Coordinate == pair.Second.Coordinate && pair.First.Image.Pixels.SequenceEqual(pair.Second.Image.Pixels))
            && loaded.RoomOffsets["A"] == offsets["A"], "Named restores must keep room geometry, camera anchors, offsets, and exact tile pixels.");

        var resumed = HackerAtlasPipeline.FromArchive(loaded);
        var restoredPreparer = new HackerFramePreparer();
        restoredPreparer.RestoreRoomAnchors(loaded.Rooms);
        var next = Prepare("A", 172, 4, restoredPreparer);
        Check(next.LiveBounds == Prepare("A", 172, 4, preparer).LiveBounds,
            "The next frame must use the saved camera anchor.");
        var composed = resumed.Process(next) ?? throw new InvalidOperationException("Expected a resumed image.");
        Check(composed.Rooms.Single().Id == expected.Id
            && resumed.CaptureArchive(loaded.RoomOffsets).Archive.RegistrationCount == 4
            && workspace.Archives.LoadState(saved).RegistrationCount == 3, "Resuming capture must keep the same room and leave its named state unchanged.");
    }

    public static void TestLegacyArchiveOmitsUnregisteredRoomsAndAllowsMissingOffsets()
    {
        using var workspace = new Fixture();
        var pipeline = new HackerAtlasPipeline();
        _ = Integrate(pipeline, "A", 10, 1);
        var original = pipeline.CaptureArchive(new Dictionary<string, AtlasPoint> { ["A"] = new(7, -3) }).Archive;
        workspace.Archives.Write(null, original);
        var archivePath = Path.Combine(workspace.ArchiveRoot, "unassigned.zip");
        var valid = original.Rooms.Single();
        var pendingId = Guid.NewGuid();
        ChangeManifest(archivePath, manifest =>
        {
            var rooms = manifest["rooms"]!.AsArray();
            var pending = rooms[0]!.DeepClone();
            pending["id"] = pendingId;
            pending["sceneName"] = "B";
            pending["tiles"] = new JsonArray();
            rooms.Add(pending);
            manifest["connections"]!.AsArray().Add(new JsonObject
            {
                ["id"] = Guid.NewGuid(),
                ["fromRoomId"] = valid.Id,
                ["toRoomId"] = pendingId,
                ["fromSide"] = (int)HackerRoomSide.Right,
                ["toSide"] = (int)HackerRoomSide.Left,
                ["fromCoordinate"] = 20,
                ["toCoordinate"] = 18
            });
            var offsets = manifest["roomOffsets"]!.AsObject();
            offsets["B"] = offsets["A"]!.DeepClone();
        });
        var recovered = workspace.Archives.Read(null)!;
        Check(recovered.Rooms.Count == 1 && recovered.Rooms[0].Id == valid.Id && recovered.Connections.Count == 0
            && recovered.RoomOffsets.Count == 1 && recovered.RoomOffsets["A"] == original.RoomOffsets["A"],
            "An older archive with an unregistered room must still load its committed room and remove the pending geometry.");

        ChangeManifest(archivePath, manifest => manifest.Remove("roomOffsets"));
        Check(workspace.Archives.Read(null) is { Rooms.Count: 1, RoomOffsets.Count: 0 },
            "An archive without room offsets must keep its committed room and use automatic placement.");
    }

    private static void ChangeManifest(string path, Action<JsonObject> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("atlas.json")!;
        JsonObject manifest;
        using (var original = entry.Open()) manifest = JsonNode.Parse(original)!.AsObject();
        change(manifest);
        entry.Delete();
        using var updated = new StreamWriter(zip.CreateEntry("atlas.json").Open());
        updated.Write(manifest.ToJsonString());
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
            prepareProfile: (previous, profile, adopt) =>
            {
                Check(adopt && previous is null && SaveProfileAtlas.Read(active) is null,
                    "Legacy adoption must be staged before either active writer changes.");
                workspace.Archives.Write(profile, legacy);
            });
        Check(adopted.Replacement is null && SaveProfileAtlas.Read(active) == adopted.Profile,
            "Adoption should keep the previous Gameplay Atlas in place.");
        byte[] priorArchive = File.ReadAllBytes(Path.Combine(workspace.ArchiveRoot, $"{adopted.Profile.Id:N}.zip"));
        Throws<IOException>(() => VisionResetController.ActivateProfileAtlas(states, active, new(2, true), 32, 1,
            prepareProfile: (_, _, _) => throw new IOException("Fixture Hacker archive unavailable.")));
        Check(SaveProfileAtlas.Read(active) == adopted.Profile
            && File.ReadAllBytes(Path.Combine(workspace.ArchiveRoot, $"{adopted.Profile.Id:N}.zip")).SequenceEqual(priorArchive),
            "A failed Hacker preflight must preserve the previously selected save in both writers.");
        var changed = VisionResetController.ActivateProfileAtlas(states, active, new(2, true), 32, 1,
            prepareProfile: (previous, profile, adopt) =>
            {
                Check(!adopt && previous == adopted.Profile && workspace.Archives.Read(profile) is null,
                    "A fresh profile needs an empty Hacker map and the stable departing owner.");
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

    private static HackerPreparedFrame Prepare(string scene, double x, int index, HackerFramePreparer preparer, bool compose = true)
    {
        const int width = 192;
        const int height = 128;
        const double scale = 20;
        var image = TexturedFrame(width, height, index);
        var telemetry = new ReceiverGroundTruthSample(ReceiverProtocol.ControlVersion, "groundTruth", Guid.Empty,
            (ulong)index, index, index, scene, true, x / scale + 2, 2, 0, 1, 0, true, true, true,
            x / scale, 2, 0, x / scale, 2, 0, height / (2 * scale), scale, scale,
            width - 12, height / 2, width, height, width, height) { AtlasGameplayActive = true };
        return preparer.Process(new(new HackerCapturedFrame(index, image, index, compose), telemetry, index))
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
