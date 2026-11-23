using System.Text.Json;
using System.Text.Json.Nodes;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class NativeAtlasImportTests
{
    private const int SourceRoom = 5;
    private const int TargetRoom = 9;
    private const int SourceIsland = 7;
    private const int TargetIsland = 12;
    private static readonly StencilRect ForegroundMask = new(2.5, 3.125, 7, 4);
    private static readonly StencilRect BottomOriginMask = new(2.5, 12.875, 7, 4);

    internal static void TestNativeAtlasPersistsMasksIslandsAndDoorway()
    {
        WithFixture((root, store, payload) =>
        {
            var original = LiveWorldSessionStore.ValidateExisting(root).Snapshot;
            Check(original.SchemaVersion == 3 && original.Observations.Count == 6, "native graph");
            Check(original.CompositionMasks(original.Observations[0]).SequenceEqual([ForegroundMask]),
                "restore must flip masks to top-origin presentation exactly once");
            Check(original.Observations[0].ExcludedRects[0].Y == BottomOriginMask.Y, "immutable source mask");
            Check(original.TrackingIslands.Select(island => island.Id).SequenceEqual([SourceIsland, TargetIsland]),
                "both rigid islands");
            Check(original.DoorwayFloorConstraints.Single().ArrivalSupport.SequenceEqual([3, 4, 5]),
                "doorway support survives import");
            Check(original.Observations[0].CaptureTimestamp == 0.99
                && original.Observations[3].ObservedFloorCandidates?.Lines[0].SegmentId == 3
                && original.Observations[4].ObservedPlayerBounds?.Y == 12,
                "optional native capture geometry");
            var ground = LiveWorldWalkedGround.ProjectRoom(original, SourceRoom).Single();
            Check(ground.AtlasStart == new AtlasPoint(-30, 90) && ground.AtlasEnd == new AtlasPoint(50, 90),
                "walked span uses its island coordinates");

            var horizontal = original.TranslateRoom(SourceRoom, new(8, 0));
            Check(horizontal.TrackingIslands[0].WorldFromLocal == new AtlasPoint(8, 70)
                && horizontal.TrackingIslands[0].Members.SequenceEqual(original.TrackingIslands[0].Members),
                "room translation preserves rigid member poses");
            Check(LiveWorldWalkedGround.ProjectRoom(horizontal, SourceRoom).Single().AtlasStart == new AtlasPoint(-22, 90),
                "walked geometry moves with its island");
            Check(store.Commit(horizontal, original.MapRevision, horizontal.MapRevision), "commit rigid translation");
            var evidence = store.Append(Frame(), 12, new(100, 9), 640, [ForegroundMask]);
            Check(evidence.ExcludedRects.SequenceEqual([BottomOriginMask]), "new evidence uses native masks");

            var tracker = new LiveWorldTracker(horizontal);
            var update = tracker.Ingest(evidence.Id, evidence.Timestamp, Frame(), evidence.CameraPosition,
                evidence.SolveWidth, exclusions: evidence.ExcludedRects, searchForClosure: false, roomId: TargetRoom);
            Check(store.Commit(update.Snapshot, horizontal.MapRevision, update.Revision), "resume native graph");
            var reopened = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(reopened.Observations.Count == 7 && reopened.TrackingIslands.Count == 3,
                "appended observation owns one additional island");
            Check(reopened.TrackingIslands[^1].Placement == LiveIslandPlacement.LegacyUnknown
                && !reopened.TrackingIslands[^1].Members.Single().Measured, "new Windows capture has unknown island lineage");
            Check(reopened.DoorwayFloorConstraints.Count == 1, "saved doorway survives the new capture");
            Check(reopened.Observations[0].GroundGeometry!.WalkedIslands![0].Spans[0].SegmentId == long.MaxValue,
                "the original walked segment ID is preserved");

            using var envelope = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "scene.snapshot")));
            using var saved = JsonDocument.Parse(envelope.RootElement.GetProperty("data").GetBytesFromBase64());
            Check(saved.RootElement.GetProperty("anchoredObservationID").GetInt32() == 0
                && saved.RootElement.GetProperty("trackingIslands")[1].GetProperty("roomID").GetInt32() == TargetRoom,
                "Swift ID key spelling");
            var exclusions = saved.RootElement.GetProperty("observations")[6].GetProperty("excludedRects");
            Check(exclusions[0][0][1].GetDouble() == BottomOriginMask.Y, "Swift nested rectangle shape");
            Check(new SceneSessionStore(root).Manifest.UsesSwiftWireFormat, "manifest keeps Swift spelling");
        });
    }

    internal static void TestCorruptNativeAtlasAndLegacyOwners()
    {
        WithFixture((root, store, payload) =>
        {
            var shear = JsonNode.Parse(payload)!;
            shear["observations"]![0]!["optimizedPose"]![0] = 1;
            Reject(shear, LiveWorldModelError.InvalidIslandMembership);
            var brokenDoor = JsonNode.Parse(payload)!;
            brokenDoor["doorwayFloorConstraints"]![0]!["arrivalSupport"]![0] = 4;
            Reject(brokenDoor, LiveWorldModelError.InvalidDoorwayFloorConstraint);
            var brokenMask = JsonNode.Parse(payload)!;
            brokenMask["observations"]![0]!["excludedRects"]![0] = JsonNode.Parse("[[2.5],[7,4]]");
            try
            {
                _ = LiveWorldJson.ReadSnapshot(JsonSerializer.SerializeToUtf8Bytes(brokenMask));
                throw new InvalidOperationException("A malformed native mask was accepted.");
            }
            catch (JsonException) { }

            var legacyNative = JsonNode.Parse(payload)!.AsObject();
            legacyNative["schemaVersion"] = 2;
            legacyNative.Remove("trackingIslands");
            legacyNative.Remove("doorwayFloorConstraints");
            var migrated = LiveWorldJson.ReadSnapshot(JsonSerializer.SerializeToUtf8Bytes(legacyNative));
            Check(migrated.TrackingIslands.Count == 6 && migrated.TrackingIslands.All(island =>
                island.Placement == LiveIslandPlacement.LegacyUnknown
                && !island.Members.Single().Measured
                && island.WorldFromLocal == migrated.Observations[island.Id].OptimizedPose),
                "older Mac capture without explicit ownership preserves each pose");

            var windows = new LiveWorldSnapshot();
            var unchanged = LiveWorldJson.ReadSnapshot(LiveWorldJson.WriteSnapshot(windows));
            Check(unchanged.SchemaVersion == 1 && !unchanged.UsesSwiftWireFormat
                && unchanged.TrackingIslands.Count == 0, "legacy Windows format");
        });
    }

    internal static void TestNamedAtlasRestoreRebuildsTheWindowsMosaic()
    {
        WithFixture((root, store, payload) =>
        {
            var temporary = Directory.GetParent(root)!.FullName;
            var states = new AtlasStateStore(Path.Combine(temporary, "states"));
            var saved = states.Save("Mac Atlas", destination => Directory.Move(root, destination));
            var original = states.WorldPath(saved);
            var originalGraph = File.ReadAllBytes(Path.Combine(original, "scene.snapshot"));
            var active = Path.Combine(temporary, "active");
            var loaded = VisionResetController.LoadNamedAtlasForCurrentProfile(states, saved, active,
                activateWorld: folder =>
                {
                    var pipeline = new WindowsLiveWorldPipeline(Path.Combine(folder, "live-world"));
                    Check(pipeline.Reopen(), "the desktop can resume the imported live world");
                },
                saveTile: HollowKnightVision.Windows.Host.BmpWriter.Write);

            Check(loaded.ValidatedWorld.Mosaic.TileCount > 0
                && loaded.ValidatedWorld.Metadata.RegistrationCount == 6, "rebuilt a registered tile mosaic");
            var restored = LiveWorldSessionStore.ValidateExisting(Path.Combine(active, "live-world")).Snapshot;
            Check(restored.TrackingIslands.Count == 2 && restored.DoorwayFloorConstraints.Count == 1,
                "restore preserved the native graph under the active atlas");
            Check(File.ReadAllBytes(Path.Combine(original, "scene.snapshot")).SequenceEqual(originalGraph)
                && !File.Exists(Path.Combine(original, "atlas.json")), "the source named state is immutable");
        });
    }

    private static void Reject(JsonNode payload, LiveWorldModelError expected)
    {
        try
        {
            _ = LiveWorldJson.ReadSnapshot(JsonSerializer.SerializeToUtf8Bytes(payload));
        }
        catch (LiveWorldModelException error) when (error.Error == expected)
        {
            return;
        }
        throw new InvalidOperationException($"Expected native atlas error {expected}.");
    }

    private static void WithFixture(Action<string, LiveWorldSessionStore, byte[]> body)
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-native-atlas-");
        try
        {
            // The fixture uses Swift's wire names and arrays independently of the Windows snapshot writer.
            var root = Path.Combine(temporary.FullName, "world");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                """
                {"schemaVersion":1,"sessionID":"C14FB382-EF87-4797-8624-FBEE1DD4B0DA",
                 "observations":[],"nextObservationID":0}
                """);
            var store = new LiveWorldSessionStore(root);
            Check(store.Load().Snapshot.UsesSwiftWireFormat, "empty Swift manifest");
            var rows = Enumerable.Range(0, 6).Select(id =>
            {
                var source = id < 3;
                var startedAt = source ? 1.0 : 10.0;
                var timestamp = startedAt + (id % 3) * 0.2;
                var room = source ? SourceRoom : TargetRoom;
                var offset = (id % 3) * 10.0;
                var origin = source ? new[] { 0.0, 70 } : new[] { 150.0, 40 };
                var floorY = source ? 20.0 : 50.0;
                double[] raw = [offset, 3];
                var stored = store.Append(Frame(), timestamp, new(raw[0], raw[1]), 640, [ForegroundMask]);
                var line = new { segmentID = (long)id, start = new[] { 0.0, floorY }, end = new[] { 640.0, floorY } };
                return new
                {
                    id, sourceObservationID = stored.Id, timestamp, captureTimestamp = timestamp - 0.01,
                    sourceWidth = 32, sourceHeight = 20, solveWidth = 640, sampleWidth = 160,
                    excludedRects = new[] { new[] { new[] { 2.5, 12.875 }, new[] { 7.0, 4 } } },
                    localPose = new[] { offset, 0.0 }, roomID = room, rawPose = raw,
                    optimizedPose = new[] { offset + origin[0], origin[1] },
                    groundGeometry = new
                    {
                        lines = new[] { line },
                        walkedIslands = id != 0 ? null : new[]
                        {
                            new
                            {
                                owner = new
                                {
                                    id = SourceIsland, roomID = room, startedAt, worldFromLocal = origin,
                                    localPose = new[] { 0.0, 0 }, placement = "reference",
                                    uncertainty = new { x = 0, y = 0 }, breakReason = "origin", measured = true
                                },
                                spans = new[]
                                {
                                    new { segmentID = long.MaxValue, start = new[] { -30.0, 20 }, end = new[] { 50.0, 20 } }
                                }
                            }
                        }
                    },
                    observedFloorCandidates = new { lines = new[] { line } },
                    observedPlayerBounds = new[] { new[] { 2, 12 }, new[] { 3, 6 } }
                };
            }).ToArray();
            var islands = rows.GroupBy(row => row.roomID).Select(group =>
            {
                var source = group.Key == SourceRoom;
                var first = group.First();
                return new
                {
                    id = source ? SourceIsland : TargetIsland, roomID = group.Key, startedAt = first.timestamp,
                    worldFromLocal = first.optimizedPose, placement = source ? "reference" : "estimated",
                    uncertainty = source ? new { x = (int?)0, y = (int?)0 } : new { x = (int?)null, y = (int?)2 },
                    breakReason = source ? "origin" : "room-cut",
                    members = group.Select(row => new { observationID = row.id, row.localPose, measured = true })
                };
            }).ToArray();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 3, mapRevision = 1, anchoredObservationID = 0, observations = rows,
                keyframes = Array.Empty<int>(), landmarks = Array.Empty<int>(),
                relativeMotionEdges = Array.Empty<int>(), loopClosureEdges = Array.Empty<int>(),
                trackingIslands = islands,
                doorwayFloorConstraints = new[]
                {
                    new
                    {
                        id = 0, departureObservationID = 2, arrivalObservationID = 5,
                        departureFloor = new[] { 550, 20 }, arrivalFloor = new[] { 30, 50 }, direction = 1,
                        departureSupport = new[] { 0, 1, 2 }, arrivalSupport = new[] { 3, 4, 5 }
                    }
                }
            });
            Check(new SceneSessionStore(root).SaveScene(payload, 0, 1), "write fixture envelope");
            body(root, store, payload);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static BgraFrame Frame()
    {
        var pixels = new byte[32 * 20 * 4];
        Array.Fill(pixels, byte.MaxValue);
        return new(32, 20, 128, pixels, 0, DateTimeOffset.UnixEpoch);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Native Atlas fixture failed: {message}.");
    }
}
