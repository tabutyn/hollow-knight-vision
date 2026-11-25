using HollowKnightVision.Windows.Core;

internal static class HackerTerrainEditingTests
{
    private const string Room = "Tutorial_01";
    private const string OtherRoom = "Tutorial_02";
    private static readonly AtlasPoint Scale = new(1, 1);

    public static void TestMacRoomCalibrationsSurviveTerrainEditAndUndo()
    {
        WithStore((store, _) =>
        {
            const string input = """
                {
                  "schemaVersion": 2, "seededScenes": [], "lines": [],
                  "roomProjections": {
                    "Tutorial_01": {
                      "roomID": "B29F0F1D-D208-4130-BE90-21ED38976D83",
                      "pixelsPerWorldUnitX": 21.75, "pixelsPerWorldUnitY": 22.5,
                      "localOrigin": [-30.25, 14.5]
                    },
                    "Tutorial_02": {
                      "roomID": "1976C33E-5E02-4398-A19A-7F37C233D464",
                      "pixelsPerWorldUnitX": 20, "pixelsPerWorldUnitY": 20,
                      "localOrigin": [1, -2]
                    }
                  }
                }
                """;
            File.WriteAllText(store.FilePath, input);
            var editor = new GroundTruthLabelController(store);
            var original = editor.Document.Clone();
            Check(original.RoomProjections?[Room] is { PixelsPerWorldUnitX: 21.75, PixelsPerWorldUnitY: 22.5 }
                && original.RoomProjections[Room].LocalOrigin == new AtlasPoint(-30.25, 14.5),
                "Windows could not read the Mac room calibration.");

            Drag(editor, 20, 60, 90, 60);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
            using var source = System.Text.Json.JsonDocument.Parse(input);
            var actual = saved.RootElement.GetProperty("roomProjections");
            var expected = source.RootElement.GetProperty("roomProjections");
            foreach (var room in new[] { Room, OtherRoom })
            {
                var before = expected.GetProperty(room);
                var after = actual.GetProperty(room);
                Check(before.GetProperty("roomID").GetGuid() == after.GetProperty("roomID").GetGuid()
                    && before.GetProperty("pixelsPerWorldUnitX").GetDouble() == after.GetProperty("pixelsPerWorldUnitX").GetDouble()
                    && before.GetProperty("pixelsPerWorldUnitY").GetDouble() == after.GetProperty("pixelsPerWorldUnitY").GetDouble()
                    && before.GetProperty("localOrigin").EnumerateArray().Select(value => value.GetDouble())
                        .SequenceEqual(after.GetProperty("localOrigin").EnumerateArray().Select(value => value.GetDouble())),
                    "Editing a floor changed the room's cross-platform calibration.");
            }
            editor.Undo();
            Check(store.Load().Equals(original), "Undo did not preserve all calibrations.");
        });
    }

    public static void TestWallSnapsBothCornersAndCommitsOnRelease()
    {
        WithStore((store, editor) =>
        {
            DrawTwoFloors(editor);
            var before = editor.Document.Clone();
            Point(editor, GroundTruthPointerPhase.Began, 42, 21);
            Point(editor, GroundTruthPointerPhase.Changed, 41, 103);
            var draft = editor.VisibleLines.Single(line => line.Kind == GroundTruthLineKind.Wall);
            WallCorners(draft, editor.VisibleLines.Where(line => line.Kind == GroundTruthLineKind.Positive).ToArray());
            Check(editor.Document.Equals(before) && store.Load().Equals(before), "A preview changed persisted terrain.");
            Point(editor, GroundTruthPointerPhase.Ended, 41, 103);
            WallCorners(editor.Document.Walls!.Single(), editor.Document.Lines);
            Check(store.Load().Equals(editor.Document), "The wall and its two corners did not persist.");
            editor.Undo();
            Check(store.Load().Equals(before), "Undo did not restore both floors.");
        });
    }

    public static void TestWallAndFloorMoveOnTheirOwnAxesAfterReload()
    {
        WithStore((store, editor) =>
        {
            DrawTwoFloors(editor);
            Drag(editor, 42, 21, 41, 103);
            editor = new(store) { Tool = GroundTruthTerrainTool.Wall, Mode = GroundTruthEditingMode.Modify };
            var original = editor.Document.Clone();
            Drag(editor, 40, 60, 50, 69);
            Check(editor.Document.Lines[0].Start == new AtlasPoint(50, 20), "The wall did not carry the first floor left endpoint.");
            Check(editor.Document.Lines[1].End == new AtlasPoint(50, 103), "The wall did not carry the second floor right endpoint.");
            editor.Tool = GroundTruthTerrainTool.Ground;
            Drag(editor, 75, 20, 89, 30);
            Check(editor.Document.Lines[0].End == new AtlasPoint(100, 30), "The connected floor moved along X when dragged by its center.");
            Check(editor.Document.Walls!.Single().Start == new AtlasPoint(50, 30), "Moving the floor did not move the wall endpoint.");
            Check(editor.Document.Walls!.Single().End == new AtlasPoint(50, 103), "Moving the floor changed the free wall elevation.");
            editor.Tool = GroundTruthTerrainTool.Wall;
            var beforeEndpointDrag = editor.Document.Clone();
            Drag(editor, 50, 30, 54, 33);
            Check(editor.Document.Walls!.Single().Start == new AtlasPoint(54, 33)
                && editor.Document.Lines[0].Start == new AtlasPoint(54, 33)
                && editor.Document.Lines[1].End == new AtlasPoint(54, 103),
                "Dragging a joint did not propagate to the attached floor endpoints on both axes.");
            editor.Undo();
            Check(store.Load().Equals(beforeEndpointDrag), "Undo did not restore the entire corner.");
            editor.Undo();
            editor.Undo();
            Check(store.Load().Equals(original), "Undo did not restore the initial floor and wall.");
        });
    }

    public static void TestWallModifyCanAttachAGroundEndpoint()
    {
        WithStore((store, editor) =>
        {
            Drag(editor, 30, 25, 90, 25);
            editor.Tool = GroundTruthTerrainTool.Wall;
            Drag(editor, 110, 70, 110, 35);
            editor.Mode = GroundTruthEditingMode.Modify;
            Drag(editor, 90, 25, 108, 37);
            var wall = editor.Document.Walls!.Single();
            var floor = editor.Document.Lines.Single();
            Check(wall.Start == new AtlasPoint(110, 35) && floor.End == wall.Start
                && floor.Start == new AtlasPoint(30, 35)
                && wall.StartCornerId is not null && floor.EndCornerId == wall.StartCornerId,
                "The floor handle did not attach to the vertical wall.");
            var joined = editor.Document.Clone();
            editor.Mode = GroundTruthEditingMode.Delete;
            Drag(editor, 110, 52, 110, 52);
            Check(editor.Document.Walls is null or { Count: 0 } && editor.Document.Lines.Single().EndCornerId is null,
                "Deleting the wall left a stale floor corner.");
            editor.Undo();
            Check(store.Load().Equals(joined), "Undo did not restore the removed wall and attachment.");
        });
    }

    public static void TestCornerRequiresNearbyEndpointInSameRoom()
    {
        WithStore((store, editor) =>
        {
            Drag(editor, 40, 20, 100, 20);
            editor.Tool = GroundTruthTerrainTool.Wall;
            var original = editor.Document.Clone();
            Point(editor, GroundTruthPointerPhase.Began, 42, 21);
            Point(editor, GroundTruthPointerPhase.Changed, 42, 103);
            editor.CancelGesture();
            Check(store.Load().Equals(original) && editor.VisibleLines.Count() == 1, "Cancel saved a preview.");
            Point(editor, GroundTruthPointerPhase.Began, 40, 25);
            Point(editor, GroundTruthPointerPhase.Ended, 40, 40, OtherRoom);
            Check(store.Load().Equals(original), "A pointer in another room committed the gesture.");
            editor.CancelGesture();
            Drag(editor, 40, 25, 40, 25);
            Check(store.Load().Equals(original), "A click snapped into a new wall.");
            Drag(editor, 40, 20, 40, 90, OtherRoom);
            Drag(editor, 44, 24, 44, 90, pixelsPerUnit: new(1, 2));
            Drag(editor, 70, 20, 70, 90);
            Drag(editor, 40, 22, 41, 90, pixelsPerUnit: new(1, 2));
            var walls = editor.Document.Walls!;
            Check(walls.Take(3).All(wall => wall.StartCornerId is null && wall.EndCornerId is null)
                && walls[3].StartCornerId is not null && walls[3].Start == new AtlasPoint(40, 20),
                "Room, endpoint, or screen-radius restrictions failed.");
        });
    }

    public static void TestClickAndToolChangeDoNotCommitCornerDrafts()
    {
        WithStore((store, editor) =>
        {
            DrawTwoFloors(editor);
            var original = editor.Document.Clone();
            Drag(editor, 40, 25, 40, 25);
            Check(editor.Document.Equals(original) && store.Load().Equals(original),
                "A click beside an endpoint persisted a wall.");
            Point(editor, GroundTruthPointerPhase.Began, 42, 21);
            Point(editor, GroundTruthPointerPhase.Changed, 41, 103);
            Check(editor.HasActiveGesture, "The fixture did not start the wall draft.");
            editor.Tool = GroundTruthTerrainTool.Ground;
            Check(!editor.HasActiveGesture && store.Load().Equals(original),
                "Changing tools committed a draft corner.");
            editor.Tool = GroundTruthTerrainTool.Wall;
            Drag(editor, 70, 20, 70, 103);
            var wall = store.Load().Walls!.Single();
            Check(wall.StartCornerId is null && wall.EndCornerId is null
                && editor.Document.Lines.SequenceEqual(original.Lines),
                "Drawing across a floor interior attached a corner or displaced the floor.");
        });
    }

    public static void TestCornerSnapRespectsTheScreenRadiusAtHighZoom()
    {
        WithStore((store, editor) =>
        {
            Drag(editor, 40, 20, 100, 20);
            editor.Tool = GroundTruthTerrainTool.Wall;
            // At 8x zoom one atlas unit is the full eight-unit pointer radius.
            Drag(editor, 41.5, 20, 41.5, 70, tolerance: 1);
            Drag(editor, 40.5, 20, 40.5, 80, tolerance: 1);
            var walls = store.Load().Walls!;
            Check(walls[0].Start == new AtlasPoint(41.5, 20) && walls[0].StartCornerId is null
                && walls[1].Start == new AtlasPoint(40, 20) && walls[1].StartCornerId is not null,
                "At maximum zoom, twelve display units must miss and four must attach.");
        });
    }

    public static void TestDownwardWallKeepsCornerIdentityAndClampsAttachedFloor()
    {
        WithStore((store, editor) =>
        {
            DrawTwoFloors(editor);
            Drag(editor, 43, 100, 43, 21);
            var wall = editor.Document.Walls!.Single();
            var lower = editor.Document.Lines[0];
            var upper = editor.Document.Lines[1];
            Check(lower.Start == wall.Start && upper.End == wall.End
                && lower.StartCornerId is not null && lower.StartCornerId == wall.StartCornerId
                && upper.EndCornerId is not null && upper.EndCornerId == wall.EndCornerId,
                "Drawing down swapped the corners belonging to the upper and lower floors.");
            editor.Mode = GroundTruthEditingMode.Modify;
            Drag(editor, 43, 60, 0, 60);
            var saved = store.Load();
            Check(saved.Walls!.Single().End == new AtlasPoint(10, 100)
                && saved.Lines[0].Start == new AtlasPoint(10, 21)
                && saved.Lines[1].Start == saved.Lines[1].End,
                "The moving wall should stop when the short floor reaches its other endpoint.");
        });
    }

    public static void TestSharedFloorMovesFarWallVerticallyWithoutMovingItSideways()
    {
        WithStore((store, editor) =>
        {
            Drag(editor, 40, 30, 120, 30);
            editor.Tool = GroundTruthTerrainTool.Wall;
            Drag(editor, 41, 29, 41, 70);
            Drag(editor, 119, 31, 119, 80);
            editor.Mode = GroundTruthEditingMode.Modify;
            Drag(editor, 40, 30, 49, 40);
            var saved = store.Load();
            var walls = saved.Walls!;
            Check(walls[0].Start == new AtlasPoint(49, 40) && walls[0].End == new AtlasPoint(49, 70)
                && walls[1].Start == new AtlasPoint(120, 40) && walls[1].End == new AtlasPoint(120, 80)
                && saved.Lines.Single().Start == walls[0].Start && saved.Lines.Single().End == walls[1].Start,
                "The first wall should move both floor ends up while the second wall stays at its own X.");
        });
    }

    public static void TestCoincidentLegacyEndpointsRemainIndependent()
    {
        WithStore((store, _) =>
        {
            var floor = new GroundTruthWorldLine(Room, 40, 100, 30, GroundTruthLineKind.Positive, false);
            var wall = new GroundTruthWorldLine(Room, 40, 40, 30, GroundTruthLineKind.Wall, false) { EndWorldY = 80 };
            store.Save(new() { Lines = [floor], Walls = [wall] });
            var editor = new GroundTruthLabelController(store) { Tool = GroundTruthTerrainTool.Wall, Mode = GroundTruthEditingMode.Modify };
            Drag(editor, 40, 50, 45, 50);
            var saved = store.Load();
            Check(saved.Lines.Single() == floor && saved.Walls!.Single().Start == new AtlasPoint(45, 30)
                && saved.Walls!.Single().StartCornerId is null,
                "The editor joined coincident legacy endpoints without an endpoint gesture.");
        });
    }

    public static void TestGroundHandlesTakePriorityOverOverlappingLineBodies()
    {
        WithStore((store, editor) =>
        {
            var projection = new AtlasPoint(10, 10);
            Drag(editor, 0, 2, 10, 2, pixelsPerUnit: projection);
            Drag(editor, 2, 2, 6, 2, pixelsPerUnit: projection);
            var original = editor.Document.Clone();
            editor.Mode = GroundTruthEditingMode.Modify;
            Drag(editor, 6, 2, 8, 5, pixelsPerUnit: projection);
            Check(editor.Document.Lines[0] == original.Lines[0]
                && editor.Document.Lines[1].Start == new AtlasPoint(2, 2)
                && editor.Document.Lines[1].End == new AtlasPoint(8, 2),
                "The right handle should win over the overlapping line and stay horizontal.");
            Drag(editor, 2, 2, 1, -3, pixelsPerUnit: projection);
            Check(store.Load().Lines[1].Start == new AtlasPoint(1, 2)
                && store.Load().Lines[1].End == new AtlasPoint(8, 2),
                "The left handle should resize the same line without dragging its elevation.");
            editor.Undo();
            editor.Undo();
            Check(store.Load().Equals(original), "Undo did not restore both original lines.");
        });
    }

    public static void TestShortLineResizesTheNearestOverlappingHandle()
    {
        WithStore((store, editor) =>
        {
            var projection = new AtlasPoint(10, 10);
            Drag(editor, 1, 2, 1.5, 2, pixelsPerUnit: projection);
            editor.Mode = GroundTruthEditingMode.Modify;
            Drag(editor, 1.5, 2, 2.5, 2, pixelsPerUnit: projection, tolerance: 8);
            var line = store.Load().Lines.Single();
            Check(line.Start == new AtlasPoint(1, 2) && line.End == new AtlasPoint(2.5, 2),
                "The right endpoint should win even when both handles are within the hit radius.");
        });
    }

    public static void TestRoomHitRankingUsesProjectedEndpointsBeforeBodies()
    {
        WithStore((_, editor) =>
        {
            var scale = new AtlasPoint(4, 4);
            var otherScale = new AtlasPoint(2, 4);
            Drag(editor, 17, 16, 55, 16, pixelsPerUnit: scale);
            Drag(editor, 50, 16, 70, 16, OtherRoom, otherScale);
            var body = new AtlasPoint(27, 16);
            var endpoint = new AtlasPoint(50, 16);
            editor.Mode = GroundTruthEditingMode.Modify;
            Check(editor.RoomHitRank(body, 8, Room, scale) == 3
                && editor.RoomHitRank(endpoint, 8, OtherRoom, otherScale) == 0,
                "The projected endpoint should outrank a body in another room.");
            editor.Mode = GroundTruthEditingMode.Add;
            Check(editor.RoomHitRank(endpoint, 8, OtherRoom, otherScale) is null,
                "A ground stroke should only prefer an opposite-axis corner over room bounds.");
            editor.Tool = GroundTruthTerrainTool.Wall;
            Check(editor.RoomHitRank(body, 8, Room, scale) is null
                && editor.RoomHitRank(endpoint, 8, OtherRoom, otherScale) == 0
                && editor.RoomHitRank(new(53, 16), 8, OtherRoom, otherScale) is > 0 and < 1,
                "Wall Add should favor ground endpoints within the normalized screen radius.");
        });
    }

    public static void TestDoorsDrawOnEitherAxisAndFollowGround()
    {
        WithStore((store, editor) =>
        {
            Drag(editor, 40, 20, 100, 20);
            editor.Tool = GroundTruthTerrainTool.Door;
            Drag(editor, 100, 21, 104, 57);
            Drag(editor, 69, 22, 87, 23);
            var floor = editor.Document.Lines.Single();
            var doors = editor.Document.Doors!;
            Check(doors[0].Kind == GroundTruthLineKind.DoorVertical && doors[0].Start == floor.End
                && doors[0].End == new AtlasPoint(100, 56) && doors[0].GroundLatch == new GroundTruthDoorLatch(floor.Id, TerrainLineEnd.Start, 1),
                "The vertical door did not attach its foot to the platform edge.");
            Check(doors[1].Kind == GroundTruthLineKind.Door && doors[1].Start.Y == 20 && doors[1].End.Y == 20
                && doors[1].GroundLatch?.GroundId == floor.Id, "The horizontal door did not attach to the floor.");
            Check(store.Load().Equals(editor.Document), "Door axes and latches did not round-trip.");
            editor = new(store) { Mode = GroundTruthEditingMode.Modify, Tool = GroundTruthTerrainTool.Door };
            Drag(editor, 100, 56, 100, 63);
            Check(editor.Document.Lines.Single() == floor && editor.Document.Doors![0].End == new AtlasPoint(100, 63),
                "Editing a door handle moved the floor or detached the other door handle.");
            editor.Tool = GroundTruthTerrainTool.Ground;
            Drag(editor, 60, 20, 66, 29);
            doors = editor.Document.Doors!;
            Check(doors[0].Start == new AtlasPoint(106, 29) && doors[0].End == new AtlasPoint(106, 72)
                && doors[1].Start.Y == 29 && doors[1].End.Y == 29, "The doors did not follow the modified floor.");
            editor.Mode = GroundTruthEditingMode.Delete;
            Drag(editor, 60, 29, 60, 29);
            Check(editor.Document.Lines.Count == 0 && editor.Document.Doors!.Count == 2
                && editor.Document.Doors!.All(door => door.GroundLatch is null)
                && store.Load().Equals(editor.Document), "Deleting a floor removed doors or retained their old latches.");
        });
    }

    public static void TestSpikesAreHorizontalAndIndependentOfGround()
    {
        WithStore((store, editor) =>
        {
            Drag(editor, 10, 20, 80, 20);
            editor.Tool = GroundTruthTerrainTool.Spike;
            Drag(editor, 72, 20, 32, 42);
            var spike = editor.Document.Spikes!.Single();
            Check(spike.Start == new AtlasPoint(32, 20) && spike.End == new AtlasPoint(72, 20)
                && spike.StartCornerId is null && spike.EndCornerId is null, "The spike was not constrained to the original row.");
            editor.Mode = GroundTruthEditingMode.Delete;
            Drag(editor, 54, 20, 54, 20);
            Check(editor.Document.Spikes is null or { Count: 0 } && editor.Document.Lines.Count == 1,
                "Deleting a spike changed overlapping ground.");
            editor.Undo();
            Check(store.Load().Spikes!.Single() == spike, "Undo did not restore the authored spike.");
        });
    }

    public static void TestInvalidTerrainCannotReplaceSavedLabels()
    {
        WithStore((store, editor) =>
        {
            DrawTwoFloors(editor);
            Drag(editor, 42, 21, 41, 103);
            editor.Tool = GroundTruthTerrainTool.Door;
            Drag(editor, 100, 20, 100, 64);
            var original = editor.Document;

            var invalid = original.Clone();
            invalid.Walls![0] = invalid.Walls[0] with { EndWorldY = null };
            AssertRejected(store, invalid);
            invalid = original.Clone();
            invalid.Walls![0] = invalid.Walls[0] with { MaximumWorldX = 43 };
            AssertRejected(store, invalid);
            invalid = original.Clone();
            invalid.Walls![0] = invalid.Walls[0] with { SceneName = OtherRoom };
            AssertRejected(store, invalid);
            invalid = original.Clone();
            invalid.Walls!.Clear();
            AssertRejected(store, invalid);
            invalid = original.Clone();
            invalid.Doors![0] = invalid.Doors[0] with { SceneName = OtherRoom };
            AssertRejected(store, invalid);
            Check(store.Load().Equals(original), "A failed validation replaced the saved terrain.");
        });
    }

    public static void TestFailedTerrainSaveKeepsTheCommittedDocument()
    {
        WithStore((store, editor) =>
        {
            DrawTwoFloors(editor);
            var original = editor.Document;
            var backup = store.FilePath + ".backup";
            File.Move(store.FilePath, backup);
            Directory.CreateDirectory(store.FilePath);
            Point(editor, GroundTruthPointerPhase.Began, 42, 21);
            Point(editor, GroundTruthPointerPhase.Changed, 41, 103);
            Point(editor, GroundTruthPointerPhase.Ended, 41, 103);
            Check(editor.Error is not null && editor.Document.Equals(original) && !editor.HasActiveGesture
                && editor.VisibleLines.Count() == 2, "A failed save left uncommitted terrain in the editor.");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp-*").Any(),
                "A failed save left a temporary terrain file.");
            Directory.Delete(store.FilePath);
            File.Move(backup, store.FilePath);
            Check(store.Load().Equals(original), "A failed save changed the previous document.");
            Drag(editor, 42, 21, 41, 103);
            Check(store.Load().Equals(editor.Document), "The wall could not be retried after the failed save.");
        });
    }

    public static void TestTerrainProjectionFollowsRoomPosition()
    {
        var camera = new GroundTruthCameraTransform(Room, new(100, 75), 2, 3, new(60, 30));
        var bounds = new HackerAtlasRect(10, 30, 60, 30);
        // Registration uses the live frame's calibrated camera and bounds only.
        var frame = new HackerPreparedFrame(null!, bounds, default, camera, null!, default, Room, 0, 0, true, true);
        var roomPosition = new AtlasPoint(-20, 5);
        var projection = HackerTerrainProjection.FromFrame(frame, roomPosition);
        Check(projection.IsValid && projection.WorldToAtlas(camera.CameraWorld, roomPosition) == new AtlasPoint(40, 45),
            "Terrain registration did not use the live frame's midpoint.");
        var world = new AtlasPoint(102, 81);
        var p = projection.WorldToAtlas(world, roomPosition);
        Check(p == new AtlasPoint(44, 63) && projection.AtlasToWorld(p, roomPosition) == world,
            "The atlas projection did not preserve coordinates with unequal scales.");
        var moved = new AtlasPoint(80, -45);
        var relocated = projection.WorldToAtlas(world, moved);
        Check(relocated == new AtlasPoint(144, 13) && projection.AtlasToWorld(relocated, moved) == world,
            "Relocating a room changed the world-space label.");
    }

    private static void DrawTwoFloors(GroundTruthLabelController editor)
    {
        Drag(editor, 40, 20, 100, 20);
        Drag(editor, 10, 100, 43, 100);
        editor.Tool = GroundTruthTerrainTool.Wall;
    }

    private static void WallCorners(GroundTruthWorldLine wall, IReadOnlyList<GroundTruthWorldLine> floors)
    {
        Check(wall.Start == new AtlasPoint(40, 20) && wall.End == new AtlasPoint(40, 103), "The wall did not stay vertical or snap its start.");
        Check(floors[1].Start == new AtlasPoint(10, 103) && floors[1].End == wall.End, "The far floor did not move to the wall's end.");
        Check(wall.StartCornerId is not null && wall.EndCornerId is not null
            && wall.StartCornerId != wall.EndCornerId && wall.StartCornerId == floors[0].StartCornerId
            && wall.EndCornerId == floors[1].EndCornerId, "The wall did not retain its two distinct corners.");
    }

    private static void Point(GroundTruthLabelController editor, GroundTruthPointerPhase phase, double x, double y,
        string scene = Room, AtlasPoint? pixelsPerUnit = null, double tolerance = 6) =>
        editor.HandleWorld(phase, new(x, y), tolerance, scene, pixelsPerUnit ?? Scale);

    private static void Drag(GroundTruthLabelController editor, double x1, double y1, double x2, double y2,
        string scene = Room, AtlasPoint? pixelsPerUnit = null, double tolerance = 6)
    {
        Point(editor, GroundTruthPointerPhase.Began, x1, y1, scene, pixelsPerUnit, tolerance);
        Point(editor, GroundTruthPointerPhase.Changed, x2, y2, scene, pixelsPerUnit, tolerance);
        Point(editor, GroundTruthPointerPhase.Ended, x2, y2, scene, pixelsPerUnit, tolerance);
        Check(editor.Error is null, editor.Error ?? "Could not save terrain.");
    }

    private static void WithStore(Action<GroundTruthLineStore, GroundTruthLabelController> test)
    {
        var directory = Directory.CreateTempSubdirectory("hkv-terrain-test-");
        try
        {
            var store = new GroundTruthLineStore(Path.Combine(directory.FullName, "ground-labels.json"));
            test(store, new(store));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void AssertRejected(GroundTruthLineStore store, GroundTruthLineDocument invalid)
    {
        try { store.Save(invalid); }
        catch (GroundTruthLineStoreException) { return; }
        throw new InvalidOperationException("The store accepted invalid terrain.");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
