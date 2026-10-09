using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

var tests = new (string Name, Action Run)[]
{
    ("center crop exact", TestCenterCropExact),
    ("center crop wide", TestCenterCropWide),
    ("center crop tall", TestCenterCropTall),
    ("projection center", TestProjectionCenter),
    ("solid normalization", TestSolidNormalization),
    ("latest frame is bounded", TestLatestFrameSlot),
    ("semantic atlas approves stable ground", TestSemanticAtlasApproval),
    ("semantic atlas transfers reassociated evidence", TestSemanticAtlasReassociation),
    ("semantic atlas drops stale evidence", TestSemanticAtlasDropsStaleEvidence),
    ("atlas state save list load delete", TestAtlasStateSaveListLoadDelete),
    ("atlas archive import and trash purge", TestAtlasArchiveImportAndTrashPurge),
    ("atlas disk usage counts files", TestAtlasDiskUsage),
    ("png writer emits valid signature and dimensions", TestPngWriter),
    ("label example store round trips schema", TestLabelExampleStore),
    ("user object catalog persists portable schema", TestUserObjectCatalog),
    ("label dataset prevents leakage and trains negatives", TestLabelDatasetSplit),
    ("label dataset rejects negative-only corpus", TestLabelDatasetNeedsPositive),
    ("camera continuity delays soft recovery", TestCameraContinuity),
    ("fresh atlas waits for stable pose", TestFreshAtlasBootstrap),
    ("camera accumulator smooths and rejects jumps", TestCameraAccumulator),
    ("low resolution motion estimates direction", TestLowResolutionDirection),
    ("low resolution motion survives fade and subcell shift", TestLowResolutionFadeAndSubcell),
    ("low resolution motion rejects static frame", TestLowResolutionRejectsStatic),
    ("live world pose replacement preserves raw evidence", TestLiveWorldPoseReplacement),
    ("live world rejects invalid graph references", TestLiveWorldRejectsInvalidGraph),
    ("live world room translation moves optimized evidence", TestLiveWorldRoomTranslation),
    ("live world snapshot store compare and swap", TestLiveWorldSnapshotStore),
    ("translation pose graph distributes loop drift", TestTranslationPoseGraphLoop),
    ("translation pose graph is deterministic with exact anchor", TestTranslationPoseGraphOrder),
    ("translation pose graph rejects disconnected evidence", TestTranslationPoseGraphRejectsDisconnected),
    ("live world optimizer preserves raw evidence", TestLiveWorldPoseOptimizer),
    ("visual route replay repeats persisted camera truth", TestVisualRouteReplay),
    ("visual route builds persisted world without camera truth", TestVisualRouteWorld),
    ("visual route world retains stationary visual evidence", TestVisualRouteWorldStationary),
    ("visual route world separates scene transitions", TestVisualRouteWorldScenes),
    ("ground kernel preserves requested polarity", TestGroundKernelPolarity),
    ("ground theory bridges occlusion but preserves step", TestGroundOcclusionAndStep),
    ("semantic ground rejects bright decoration", TestSemanticGroundSurface),
    ("semantic ground keeps weak endpoints with strong core", TestSemanticGroundStrongCore),
    ("ground foreground rejection preserves long floor", TestGroundForegroundRejection),
    ("ground analysis masks expanded exclusions", TestGroundAnalysisMask),
    ("ground kernel respects capture padding rows", TestGroundCapturePadding),
    ("frame rejects short storage", TestShortFrameStorage),
    ("bmp header", TestBmpHeader),
    ("receiver hello wire contract", TestReceiverHello),
    ("receiver state wire contract", TestReceiverState),
    ("receiver checkpoint wire contract", TestReceiverCheckpoint),
    ("recorded input path round trips mac schema", TestRecordedInputPath),
    ("ground truth camera scale", TestGroundTruthCameraScale),
    ("receiver rejects multiline input", TestReceiverRejectsMultiline),
    ("startup coordinator stable navigation", TestStartupCoordinatorStableNavigation),
    ("startup coordinator closes gameplay gate on menu", TestStartupCoordinatorClosesGate),
    ("startup detector title selection", TestStartupDetectorTitleSelection),
    ("startup detector profile selection", TestStartupDetectorProfileSelection),
    ("startup detector gameplay hud", TestStartupDetectorGameplayHud),
    ("detection decoder local maximum", TestDetectionDecoderLocalMaximum),
    ("detection decoder classwise nms", TestDetectionDecoderClasswiseNms),
    ("detection decoder exposes raw candidates", TestDetectionDecoderRaw),
    ("bundled ONNX model loads", TestBundledOnnxModel),
    ("ground truth atlas projects negative world pixels", TestGroundTruthAtlasProjection),
    ("ground truth atlas blends observations", TestGroundTruthAtlasBlend),
    ("ground truth atlas emits dirty tiles and resets", TestGroundTruthAtlasDirtyReset),
    ("ground truth atlas imports restored tile", TestGroundTruthAtlasImport),
    ("receiver player ops wire contract", TestReceiverPlayerOps)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
await TestReceiverClientLoopback();
Console.WriteLine("PASS receiver client loopback");
Console.WriteLine($"Windows core tests passed: {tests.Length + 1}");
return;

static void TestCenterCropExact() => Equal(
    new PixelRect(0, 0, 640, 360),
    FrameGeometry.CenterCrop(640, 360));

static void TestCenterCropWide() => Equal(
    new PixelRect(160, 0, 1600, 900),
    FrameGeometry.CenterCrop(1920, 900));

static void TestCenterCropTall() => Equal(
    new PixelRect(0, 152, 1280, 720),
    FrameGeometry.CenterCrop(1280, 1024));

static void TestProjectionCenter()
{
    var projection = FrameGeometry.Projection(1920, 900);
    var point = projection.OutputToSource(320, 180);
    Close(960, point.X);
    Close(450, point.Y);
}

static void TestSolidNormalization()
{
    var pixels = new byte[3 * 2 * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = 7;
        pixels[index + 1] = 19;
        pixels[index + 2] = 211;
        pixels[index + 3] = 255;
    }
    var source = new BgraFrame(3, 2, 12, pixels, 42, DateTimeOffset.UnixEpoch);
    var output = CpuBgraNormalizer.Normalize(source, 8, 4);
    Equal(42L, output.CaptureId);
    for (var index = 0; index < output.Pixels.Length; index += 4)
    {
        Equal((byte)7, output.Pixels[index]);
        Equal((byte)19, output.Pixels[index + 1]);
        Equal((byte)211, output.Pixels[index + 2]);
        Equal((byte)255, output.Pixels[index + 3]);
    }
}

static void TestLatestFrameSlot()
{
    var slot = new LatestFrameSlot();
    slot.Publish(Frame(1));
    slot.Publish(Frame(2));
    True(slot.TryTake(out var newest));
    Equal(2L, newest!.CaptureId);
    True(!slot.TryTake(out _));
    Equal(new LatestFrameStatistics(2, 1, 1), slot.Statistics);
}

static void TestSemanticAtlasApproval()
{
    var atlas = new GroundSemanticAtlas();
    var ground = new GroundHypothesisAtlasLine(
        1, new AtlasPoint(20, 65), new AtlasPoint(140, 65));
    var decoration = new GroundHypothesisAtlasLine(
        2, new AtlasPoint(20, 25), new AtlasPoint(140, 25));
    IReadOnlyList<GroundHypothesisAtlasLine> approved = [];
    for (var index = 0; index < 6; index++)
    {
        approved = atlas.Update(
            [ground, decoration],
            [new CleanFloorLine(40, new PixelSpan(16, 143))],
            new AtlasPoint(10, 5),
            160,
            100,
            true);
    }

    Equal(1, approved.Count);
    Equal(1, approved[0].SegmentId);
    var persisted = atlas.Update(
        [ground, decoration],
        [],
        new AtlasPoint(500, 0),
        160,
        100,
        true);
    Equal(1, persisted.Count);
    Equal(1, persisted[0].SegmentId);
}

static void TestSemanticAtlasReassociation()
{
    var atlas = new GroundSemanticAtlas();
    var original = new GroundHypothesisAtlasLine(
        1, new AtlasPoint(20, 65), new AtlasPoint(140, 65));
    for (var index = 0; index < 6; index++)
    {
        atlas.Update(
            [original],
            [new CleanFloorLine(40, new PixelSpan(16, 143))],
            new AtlasPoint(10, 5),
            160,
            100,
            true);
    }

    var replacement = new GroundHypothesisAtlasLine(
        9, new AtlasPoint(24, 66), new AtlasPoint(144, 66));
    var approved = atlas.Update([replacement], [], null, 160, 100, false);
    Equal(1, approved.Count);
    Equal(9, approved[0].SegmentId);
}

static void TestSemanticAtlasDropsStaleEvidence()
{
    var atlas = new GroundSemanticAtlas();
    var original = new GroundHypothesisAtlasLine(
        1, new AtlasPoint(20, 65), new AtlasPoint(140, 65));
    for (var index = 0; index < 6; index++)
    {
        atlas.Update(
            [original],
            [new CleanFloorLine(40, new PixelSpan(16, 143))],
            new AtlasPoint(10, 5),
            160,
            100,
            true);
    }

    Equal(0, atlas.Update([], [], null, 160, 100, false).Count);
    var replacement = new GroundHypothesisAtlasLine(
        9, new AtlasPoint(24, 66), new AtlasPoint(144, 66));
    Equal(0, atlas.Update([replacement], [], null, 160, 100, false).Count);
}

static void TestAtlasStateSaveListLoadDelete()
{
    WithTemporaryDirectory(workspace =>
    {
        var stateId = Guid.Parse("79a55ae1-2d74-4f85-8099-91c249e6242c");
        var createdAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var store = new AtlasStateStore(
            Path.Combine(workspace, "states"),
            () => stateId,
            () => createdAt);
        var saved = store.Save("  First atlas  ", destination =>
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "world.json"), "{}");
        });

        Equal("First atlas", saved.Name);
        Equal(stateId, saved.Id);
        Equal(createdAt, saved.CreatedAt);
        var metadataPath = Path.Combine(
            store.RootDirectory,
            stateId.ToString("D").ToUpperInvariant(),
            "state.json");
        using (var metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath)))
        {
            Equal(
                stateId.ToString("D").ToUpperInvariant(),
                metadata.RootElement.GetProperty("id").GetString()!);
            Equal(JsonValueKind.Number, metadata.RootElement.GetProperty("createdAt").ValueKind);
        }
        var listed = store.List();
        Equal(1, listed.Count);
        Equal(saved, listed[0]);
        True(File.Exists(Path.Combine(store.WorldPath(saved), "world.json")));

        store.Delete(saved);
        Equal(0, store.List().Count);
    });
}

static void TestAtlasArchiveImportAndTrashPurge()
{
    WithTemporaryDirectory(workspace =>
    {
        var archive = Path.Combine(workspace, "active-archive");
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(archive, "world.json"), "{}");
        var store = new AtlasStateStore(Path.Combine(workspace, "states"));
        var imported = store.ImportArchive(archive, "Before New");

        True(!Directory.Exists(archive));
        True(File.Exists(Path.Combine(store.WorldPath(imported), "world.json")));
        var trash = Path.Combine(store.RootDirectory, "Trash");
        Directory.CreateDirectory(trash);
        File.WriteAllBytes(Path.Combine(trash, "old-atlas"), new byte[64]);
        store.PurgeLegacyTrash();
        True(!Directory.Exists(trash));
        True(Directory.Exists(store.WorldPath(imported)));
    });
}

static void TestAtlasDiskUsage()
{
    WithTemporaryDirectory(workspace =>
    {
        var frames = Path.Combine(workspace, "frames");
        Directory.CreateDirectory(frames);
        File.WriteAllBytes(Path.Combine(frames, "one.png"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(frames, "two.png"), new byte[4096]);
        Equal(12288L, AtlasStateStore.FileByteCount(workspace));
    });
}

static void TestPngWriter()
{
    WithTemporaryDirectory(workspace =>
    {
        var path = Path.Combine(workspace, "frame.png");
        PngWriter.Write(Frame(7), path);
        var data = File.ReadAllBytes(path);
        True(data.AsSpan(0, 8).SequenceEqual(
            new byte[] { 137, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }));
        Equal(2, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)));
        Equal(2, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        Equal((byte)6, data[25]);
    });
}

static void TestLabelExampleStore()
{
    WithTemporaryDirectory(workspace =>
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("10000000-0000-4000-8000-000000000001"),
            Guid.Parse("10000000-0000-4000-8000-000000000002"),
            Guid.Parse("10000000-0000-4000-8000-000000000003")
        ]);
        var createdAt = new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
        var store = new LabelingExampleStore(
            Path.Combine(workspace, "examples"),
            () => ids.Dequeue(),
            () => createdAt);
        var example = store.Save(
            Frame(9),
            "game",
            [new LabelingExampleAnnotation(
                Guid.Parse("20000000-0000-4000-8000-000000000001"),
                "main-title.options",
                0.1,
                0.2,
                0.3,
                0.4)],
            ["enemies.crawlid"]);

        Equal(LabelingExampleManifest.CurrentSchemaVersion, example.Manifest.SchemaVersion);
        Equal("shared.options", example.Manifest.Annotations[0].ClassIdentifier);
        True(example.Manifest.EffectiveKnownClassIdentifiers.Contains("enemies.crawlid"));
        True(File.Exists(example.ImagePath));
        var loaded = store.Load();
        Equal(1, loaded.Count);
        Equal(example.Id, loaded[0].Id);
        Equal(createdAt, loaded[0].Manifest.CreatedAt);
        using var json = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(example.DirectoryPath, LabelingExampleStore.ManifestFilename)));
        Equal(JsonValueKind.Number, json.RootElement.GetProperty("schemaVersion").ValueKind);
        Equal("draft", json.RootElement.GetProperty("completion").GetString()!);

        var updated = store.Update(
            loaded[0],
            "game",
            [new LabelingExampleAnnotation(Guid.NewGuid(), "enemies.crawlid", 0, 0, 1, 1, true)]);
        True(updated.Manifest.Annotations[0].IsHardNegative);
        store.Delete(updated);
        Equal(0, store.Load().Count);
    });
}

static void TestLabelDatasetSplit()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new LabelingExampleStore(Path.Combine(workspace, "examples"));
        const string classId = "enemies.crawlid";
        var group = Guid.Parse("30000000-0000-4000-8000-000000000001");
        LabelingExampleAnnotation Positive(double x = 0.1) => new(
            Guid.NewGuid(), classId, x, 0.2, 0.25, 0.3);
        var groupedPositive = store.Save(Frame(1), "game", [Positive()], captureGroupIdentifier: group);
        var groupedEmpty = store.Save(
            Frame(2), "game", [], [classId], captureGroupIdentifier: group);
        var duplicateOne = store.Save(Frame(3), "game", [Positive(0.2)]);
        var duplicateTwo = store.Save(Frame(3), "game", [Positive(0.3)]);
        var independent = store.Save(Frame(4), "game", [Positive(0.4)]);
        var hardNegative = store.Save(
            Frame(5),
            "game",
            [new LabelingExampleAnnotation(Guid.NewGuid(), classId, 0.1, 0.1, 0.2, 0.2, true)]);
        var datasetId = Guid.Parse("40000000-0000-4000-8000-000000000001");
        var exporter = new LabelingDatasetExporter(
            Path.Combine(workspace, "training-v1", "datasets"),
            () => datasetId,
            () => new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero));
        var snapshot = exporter.Export(
            LabelingDatasetExporter.SharedObjectModelIdentifier,
            [classId],
            store.Load());

        Equal(6, snapshot.Manifest.Items.Count);
        var byId = snapshot.Manifest.Items.ToDictionary(item => item.ExampleIdentifier);
        Equal(byId[groupedPositive.Id].Split, byId[groupedEmpty.Id].Split);
        Equal(byId[duplicateOne.Id].Split, byId[duplicateTwo.Id].Split);
        Equal(LabelingDatasetSplit.Training, byId[hardNegative.Id].Split);
        True(snapshot.Manifest.Items.Any(item => item.Split == LabelingDatasetSplit.Validation));
        True(snapshot.Manifest.Items.Any(item => item.Split == LabelingDatasetSplit.Training
            && item.AnnotationCount > 0));
        True(File.Exists(Path.Combine(
            workspace,
            "training-v1",
            "split-assignments.json")));

        var item = byId[independent.Id];
        var annotationsPath = Path.Combine(
            snapshot.DirectoryPath,
            item.Split == LabelingDatasetSplit.Training ? "training" : "validation",
            LabelingDatasetExporter.AnnotationsFilename);
        using var annotations = JsonDocument.Parse(File.ReadAllBytes(annotationsPath));
        var image = annotations.RootElement.EnumerateArray().Single(element =>
            element.GetProperty("image").GetString() == item.ImageFilename);
        var coordinates = image.GetProperty("annotations")[0].GetProperty("coordinates");
        Close(1.05, coordinates.GetProperty("x").GetDouble());
        Close(0.7, coordinates.GetProperty("y").GetDouble());
        Close(0.5, coordinates.GetProperty("width").GetDouble());
        Close(0.6, coordinates.GetProperty("height").GetDouble());
    });
}

static void TestLabelDatasetNeedsPositive()
{
    WithTemporaryDirectory(workspace =>
    {
        const string classId = "enemies.vengfly";
        var store = new LabelingExampleStore(Path.Combine(workspace, "examples"));
        store.Save(
            Frame(8),
            "game",
            [new LabelingExampleAnnotation(Guid.NewGuid(), classId, 0, 0, 1, 1, true)]);
        var exporter = new LabelingDatasetExporter(Path.Combine(workspace, "datasets"));
        var threw = false;
        try
        {
            _ = exporter.Export("shared-object-model", [classId], store.Load());
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        True(threw);
    });
}

static void TestCameraContinuity()
{
    var continuity = new LiveRegistrationContinuity();
    var weak = new CameraUpdate(
        CameraUpdateState.HeldLowConfidence,
        CameraVector.Zero,
        CameraVector.Zero,
        new AtlasPoint(0, 0));
    True(!continuity.ShouldEnterRecovery(weak, true));
    True(!continuity.ShouldEnterRecovery(null, true));
    True(continuity.ShouldEnterRecovery(weak, true));
    Equal(3, continuity.ConsecutiveSoftFailures);
    continuity.Reset();
    var jump = weak with { State = CameraUpdateState.HeldSceneChange };
    True(continuity.ShouldEnterRecovery(jump, true));
}

static void TestFreshAtlasBootstrap()
{
    var gate = new FreshAtlasBootstrapGate();
    for (var index = 0; index < FreshAtlasBootstrapGate.RequiredStableSamples - 1; index++)
    {
        True(!gate.AllowsFirstAtlasWrite(
            new AtlasPoint(index % 3 - 1, index % 2),
            true,
            true,
            false));
    }
    True(gate.AllowsFirstAtlasWrite(new AtlasPoint(0, 0), true, true, false));
    True(gate.IsReady);

    gate.Reset();
    for (var index = 0; index < FreshAtlasBootstrapGate.RequiredStableSamples * 2; index++)
    {
        True(!gate.AllowsFirstAtlasWrite(new AtlasPoint(index, 0), true, true, false));
    }
    True(!gate.IsReady);
    True(gate.StableSampleCount <= (int)FreshAtlasBootstrapGate.MaximumBootstrapDisplacement + 1);
}

static void TestCameraAccumulator()
{
    var accumulator = new CameraAccumulator { Smoothing = 1 };
    var first = accumulator.Ingest(4, -2, 0.8f, 960);
    var second = accumulator.Ingest(6, 1, 0.9f, 960);
    Equal(CameraUpdateState.Accepted, first.State);
    Close(10, second.Position.X);
    Close(-1, second.Position.Y);
    Equal(3, accumulator.Samples.Count);
    var jump = accumulator.Ingest(0, -150, 1, 960);
    Equal(CameraUpdateState.HeldSceneChange, jump.State);
    Close(10, jump.Position.X);
    Close(-1, jump.Position.Y);
    accumulator.ApplyGlobalOffset(new CameraVector(-7, 3));
    Close(3, accumulator.Position.X);
    Close(2, accumulator.Position.Y);
}

static void TestLowResolutionDirection()
{
    var reference = MotionGrid(0);
    var artworkMovedRight = MotionGrid(2);
    var left = LowResolutionRoomMotionTracker.Estimate(reference, artworkMovedRight);
    Equal(VisualRoomDirection.Left, left!.Direction);
    Equal(2, left.ScreenShift);
    var right = LowResolutionRoomMotionTracker.Estimate(reference, MotionGrid(-2));
    Equal(VisualRoomDirection.Right, right!.Direction);
    Equal(-2, right.ScreenShift);
}

static void TestLowResolutionFadeAndSubcell()
{
    var reference = MotionGrid(0);
    var faded = MotionGrid(2, gain: 0.32);
    var estimate = LowResolutionRoomMotionTracker.Estimate(reference, faded);
    Equal(VisualRoomDirection.Left, estimate!.Direction);
    Equal(2, estimate.ScreenShift);

    var fractionalReference = FractionalMotionGrid(0);
    var shifted = FractionalMotionGrid(0.35);
    var vector = LowResolutionRoomMotionTracker.Translation(fractionalReference, shifted);
    True(vector is not null);
    CloseWithin(0.35, vector!.ScreenShiftX, 0.16);
    CloseWithin(0, vector.ScreenShiftY, 0.05);
}

static void TestLowResolutionRejectsStatic()
{
    var grid = FractionalMotionGrid(0);
    True(LowResolutionRoomMotionTracker.Translation(grid, grid) is null);
    var pixels = Enumerable.Repeat((byte)40, 64 * 36).ToArray();
    var textureless = new LowResolutionMotionGrid(64, 36, pixels);
    var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(
        textureless,
        textureless);
    Equal(LowResolutionTranslationRejection.InsufficientTexture,
        diagnostic.FinalRejection!.Value);
}

static LowResolutionMotionGrid MotionGrid(
    int shift,
    int verticalShift = 0,
    double gain = 1,
    int width = 32,
    int height = 18)
{
    var pixels = new byte[width * height];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var sourceX = x - shift;
            var sourceY = y - verticalShift;
            if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height) continue;
            var value = (sourceX * 37 + sourceY * 61 + sourceX * sourceY * 3) % 211 + 22;
            pixels[y * width + x] = (byte)Math.Clamp(
                (int)Math.Round(value * gain, MidpointRounding.AwayFromZero), 0, 255);
        }
    }
    return new LowResolutionMotionGrid(width, height, pixels);
}

static LowResolutionMotionGrid FractionalMotionGrid(
    double horizontalShift,
    double verticalShift = 0,
    double gain = 1,
    int width = 64,
    int height = 36)
{
    double Source(int x, int y) => x < 0 || x >= width || y < 0 || y >= height
        ? 0
        : (x * 37 + y * 61 + x * y * 3) % 211 + 22;
    var pixels = new byte[width * height];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var sourceX = x - horizontalShift;
            var sourceY = y - verticalShift;
            var x0 = (int)Math.Floor(sourceX);
            var y0 = (int)Math.Floor(sourceY);
            var tx = sourceX - x0;
            var ty = sourceY - y0;
            var upper = Source(x0, y0) * (1 - tx) + Source(x0 + 1, y0) * tx;
            var lower = Source(x0, y0 + 1) * (1 - tx) + Source(x0 + 1, y0 + 1) * tx;
            pixels[y * width + x] = (byte)Math.Clamp(
                (int)Math.Round((upper * (1 - ty) + lower * ty) * gain,
                    MidpointRounding.AwayFromZero),
                0,
                255);
        }
    }
    return new LowResolutionMotionGrid(width, height, pixels);
}

static void TestLiveWorldPoseReplacement()
{
    var original = LiveWorldFixture();
    var replaced = original.ReplaceOptimizedPoses(
        new Dictionary<int, AtlasPoint>
        {
            [10] = new AtlasPoint(0, 0),
            [11] = new AtlasPoint(5, -2)
        },
        7);
    Equal(8, replaced.MapRevision);
    Equal(original.Observations[1].RawPose, replaced.Observations[1].RawPose);
    Equal(original.Observations[1].LocalPose, replaced.Observations[1].LocalPose);
    Equal(new AtlasPoint(5, -2), replaced.Observations[1].OptimizedPose);
    Equal(new AtlasPoint(40, 20), replaced.Landmarks[2].WorldPoint);
    Equal(new AtlasPoint(44.5, 18), replaced.Landmarks[3].WorldPoint);

    var stale = false;
    try
    {
        _ = original.ReplaceOptimizedPoses(
            new Dictionary<int, AtlasPoint>
            {
                [10] = new AtlasPoint(0, 0),
                [11] = new AtlasPoint(0, 0)
            },
            6);
    }
    catch (LiveWorldModelException error)
    {
        stale = error.Error == LiveWorldModelError.StaleRevision
            && error.ExpectedRevision == 7
            && error.ActualRevision == 6;
    }
    True(stale);
}

static void TestLiveWorldRejectsInvalidGraph()
{
    var observation = WorldObservation(10, 100);
    var unanchored = false;
    try
    {
        _ = new LiveWorldSnapshot(observations: [observation]);
    }
    catch (LiveWorldModelException error)
    {
        unanchored = error.Error == LiveWorldModelError.UnanchoredWorld;
    }
    True(unanchored);

    var duplicate = false;
    try
    {
        _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation, observation]);
    }
    catch (LiveWorldModelException error)
    {
        duplicate = error.Error == LiveWorldModelError.DuplicateObservationId
            && error.RelatedId == 10;
    }
    True(duplicate);

    var dangling = false;
    try
    {
        _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation],
            keyframes: [new LiveWorldKeyframe(1, 99, 100)]);
    }
    catch (LiveWorldModelException error)
    {
        dangling = error.Error == LiveWorldModelError.DanglingObservationId
            && error.RelatedId == 99;
    }
    True(dangling);
}

static void TestLiveWorldRoomTranslation()
{
    var roomOne = WorldObservation(
        1, 1, roomId: 0,
        raw: new AtlasPoint(-180, 0), optimized: new AtlasPoint(-180, 0));
    var roomTwo = WorldObservation(
        2, 2, roomId: 1,
        raw: new AtlasPoint(-820, 0), optimized: new AtlasPoint(-820, 0));
    var world = new LiveWorldSnapshot(
        anchoredObservationId: 1,
        observations: [roomOne, roomTwo]);
    var translated = world.TranslateRoom(1, new CameraVector(320, 5));
    Equal(1, translated.MapRevision);
    Equal(roomOne, translated.Observations[0]);
    Equal(roomTwo.RawPose, translated.Observations[1].RawPose);
    Equal(new AtlasPoint(-500, 5), translated.Observations[1].OptimizedPose);
    Equal(roomTwo.LocalPose, translated.Observations[1].LocalPose);
}

static void TestLiveWorldSnapshotStore()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new LiveWorldSnapshotStore(Path.Combine(workspace, "world"));
        Equal(0, store.Load().MapRevision);
        var initial = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [WorldObservation(10, 100)]);
        var revisionOne = initial.ReplaceOptimizedPoses(
            new Dictionary<int, AtlasPoint> { [10] = new AtlasPoint(1, 2) },
            0);
        True(store.Commit(revisionOne, 0));
        var loaded = store.Load();
        Equal(1, loaded.MapRevision);
        Equal(new AtlasPoint(1, 2), loaded.Observations[0].OptimizedPose);
        True(!store.Commit(revisionOne, 0));
        using var json = JsonDocument.Parse(File.ReadAllBytes(store.SnapshotPath));
        Equal(1, json.RootElement.GetProperty("mapRevision").GetInt32());
    });
}

static void TestTranslationPoseGraphLoop()
{
    var graph = new TranslationPoseGraph(
        [
            PoseNode(0, 100, 200, 0, 0),
            PoseNode(1, 110, 205, 10, 5),
            PoseNode(2, 120, 210, 20, 10),
            PoseNode(3, 130, 215, 30, 15)
        ],
        [
            PoseEdge(0, 0, 1, 10, 5, 1, TranslationPoseConstraintKind.Motion),
            PoseEdge(1, 1, 2, 10, 5, 1, TranslationPoseConstraintKind.Motion),
            PoseEdge(2, 2, 3, 10, 5, 1, TranslationPoseConstraintKind.Motion),
            PoseEdge(3, 3, 0, -24, -12, 80, TranslationPoseConstraintKind.LoopClosure)
        ],
        anchorId: 0,
        baseRevision: 41);
    var solution = graph.Solve();
    Equal(41, solution.BaseRevision);
    Equal(42, solution.Revision);
    ClosePoint(new AtlasPoint(0, 0), solution.Positions[0]);
    ClosePoint(new AtlasPoint(8, 4), solution.Positions[1], 0.03);
    ClosePoint(new AtlasPoint(16, 8), solution.Positions[2], 0.03);
    ClosePoint(new AtlasPoint(24, 12), solution.Positions[3], 0.03);
    Equal(new AtlasPoint(120, 210), graph.Nodes.Single(node => node.Id == 2).Raw);
}

static void TestTranslationPoseGraphOrder()
{
    var nodes = new[]
    {
        PoseNode(7, 0, 0, 3.25, -7.5),
        PoseNode(3, 0, 0, 0, 0),
        PoseNode(11, 0, 0, 0, 0)
    };
    var edges = new[]
    {
        PoseEdge(20, 7, 3, 4, 1, 2, TranslationPoseConstraintKind.Motion),
        PoseEdge(10, 3, 11, -2, 8, 3, TranslationPoseConstraintKind.Motion),
        PoseEdge(30, 11, 7, -2, -9, 2, TranslationPoseConstraintKind.LoopClosure)
    };
    var first = new TranslationPoseGraph(nodes, edges, 7).Solve();
    var second = new TranslationPoseGraph(nodes.AsEnumerable().Reverse().ToArray(), edges.AsEnumerable().Reverse().ToArray(), 7)
        .Solve();
    ClosePoint(new AtlasPoint(3.25, -7.5), first.Positions[7]);
    foreach (var id in first.Positions.Keys)
    {
        ClosePoint(first.Positions[id], second.Positions[id], 0.0000001);
    }
}

static void TestTranslationPoseGraphRejectsDisconnected()
{
    try
    {
        _ = new TranslationPoseGraph(
            [PoseNode(0, 0, 0, 0, 0), PoseNode(1, 0, 0, 0, 0)],
            [],
            0);
        throw new InvalidOperationException("Expected disconnected pose graph rejection.");
    }
    catch (TranslationPoseGraphException error)
    {
        Equal(TranslationPoseGraphError.DisconnectedGraph, error.Error);
    }
}

static void TestLiveWorldPoseOptimizer()
{
    var observations = Enumerable.Range(0, 4).Select(id => LiveWorldObservation.Create(
        id,
        id,
        id,
        640,
        360,
        640,
        64,
        [],
        new AtlasPoint(id * 10, id * 5),
        new AtlasPoint(id * 10, id * 5))).ToArray();
    var keyframes = Enumerable.Range(0, 4)
        .Select(id => new LiveWorldKeyframe(id, id, id)).ToArray();
    var landmarks = Enumerable.Range(0, 3).Select(index => new LiveWorldLandmark(
        100 + index,
        3,
        3,
        new AtlasPoint(20 + index, 30),
        new AtlasPoint(50 + index, 40),
        [0.1f + index],
        1,
        0.8)).ToArray();
    var snapshot = new LiveWorldSnapshot(
        mapRevision: 7,
        anchoredObservationId: 0,
        observations: observations,
        keyframes: keyframes,
        landmarks: landmarks,
        relativeMotionEdges:
        [
            new LiveWorldRelativeMotionEdge(0, 0, 1, 10, 5, 1),
            new LiveWorldRelativeMotionEdge(1, 1, 2, 10, 5, 1),
            new LiveWorldRelativeMotionEdge(2, 2, 3, 10, 5, 1)
        ],
        loopClosureEdges:
        [
            new LiveWorldLoopClosureEdge(
                0,
                3,
                0,
                [100, 101, 102],
                -24,
                -12,
                10,
                0.1)
        ]);
    var optimized = LiveWorldPoseOptimizer.Optimize(snapshot);
    Equal(8, optimized.MapRevision);
    ClosePoint(new AtlasPoint(8, 4), optimized.Observations[1].OptimizedPose, 0.03);
    ClosePoint(new AtlasPoint(16, 8), optimized.Observations[2].OptimizedPose, 0.03);
    ClosePoint(new AtlasPoint(24, 12), optimized.Observations[3].OptimizedPose, 0.03);
    for (var index = 0; index < observations.Length; index++)
    {
        Equal(observations[index].RawPose, optimized.Observations[index].RawPose);
    }
}

static TranslationPoseNode PoseNode(
    int id,
    double rawX,
    double rawY,
    double initialX,
    double initialY) => new(
        id,
        new AtlasPoint(rawX, rawY),
        new AtlasPoint(initialX, initialY));

static TranslationPoseConstraint PoseEdge(
    int id,
    int from,
    int to,
    double x,
    double y,
    double weight,
    TranslationPoseConstraintKind kind) => new(
        id,
        from,
        to,
        new AtlasPoint(x, y),
        weight,
        kind);

static LiveWorldSnapshot LiveWorldFixture() => new(
    mapRevision: 7,
    anchoredObservationId: 10,
    observations:
    [
        WorldObservation(10, 100),
        WorldObservation(11, 101, raw: new AtlasPoint(4, 0),
            optimized: new AtlasPoint(4.5, 0))
    ],
    keyframes:
    [
        new LiveWorldKeyframe(20, 10, 100),
        new LiveWorldKeyframe(21, 11, 101)
    ],
    landmarks:
    [
        new LiveWorldLandmark(30, 20, 100, new AtlasPoint(20, 30),
            new AtlasPoint(20, 30), [0.1f, 0.2f], 1, 0.8),
        new LiveWorldLandmark(31, 20, 100, new AtlasPoint(25, 30),
            new AtlasPoint(25, 30), [0.3f, 0.4f], 1, 0.8),
        new LiveWorldLandmark(32, 20, 100, new AtlasPoint(40, 20),
            new AtlasPoint(40, 20), [0.5f, 0.6f], 1, 0.8),
        new LiveWorldLandmark(33, 21, 101, new AtlasPoint(40, 20),
            new AtlasPoint(44, 20), [0.7f, 0.8f], 1, 0.8)
    ],
    relativeMotionEdges:
    [
        new LiveWorldRelativeMotionEdge(40, 10, 11, 4, 0, 8)
    ],
    loopClosureEdges:
    [
        new LiveWorldLoopClosureEdge(50, 20, 21, [30, 31, 32], 4, 0, 3, 0.1)
    ]);

static LiveWorldObservation WorldObservation(
    int id,
    int sourceId,
    int roomId = 0,
    AtlasPoint? raw = null,
    AtlasPoint? optimized = null) => LiveWorldObservation.Create(
        id,
        sourceId,
        id,
        640,
        360,
        640,
        240,
        [new WorldRect(new AtlasPoint(1, 2), new WorldSize(3, 4))],
        raw ?? new AtlasPoint(0, 0),
        optimized ?? new AtlasPoint(0, 0),
        roomId: roomId);

static void TestVisualRouteReplay()
{
    WithTemporaryDirectory(workspace =>
    {
        var samples = new[] { 0.0, 0.8, 1.6, 2.4 }.Select((shift, index) =>
            new VisualRouteSample(
                index,
                index * 0.1,
                "Tutorial_01",
                -shift,
                0,
                10,
                10,
                FractionalMotionGrid(shift))).ToArray();
        var sessionId = Guid.Parse("50000000-0000-4000-8000-000000000001");
        var session = new VisualRouteSession(
            VisualRouteSession.CurrentSchemaVersion,
            sessionId,
            new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero),
            640,
            360,
            samples);
        var store = new VisualRouteStore(Path.Combine(workspace, "route"));
        store.Save(session);
        var reportPath = Path.Combine(workspace, "route", "report.json");
        var report = new VisualRouteReplayEvaluator(store).Evaluate(
            new VisualRouteReplayOptions(
                RepeatCount: 3,
                MaximumMeanErrorPixels: 3,
                MaximumStepErrorPixels: 6,
                ReopenSessionBetweenPasses: true),
            reportPath);
        True(report.Passed);
        Equal(sessionId, report.SessionId);
        Equal(3, report.Passes.Count);
        True(report.Passes.All(pass => pass.Passed && pass.AcceptedSteps == 3));
        True(report.Passes.All(pass => pass.MeanErrorPixels < 3));
        True(File.Exists(reportPath));
        var reopened = store.Load();
        Equal(4, reopened.Samples.Count);
        Equal(samples[3].Grid.Luma[500], reopened.Samples[3].Grid.Luma[500]);
    });
}

static void TestVisualRouteWorld()
{
    WithTemporaryDirectory(workspace =>
    {
        var samples = new[] { 0.0, 0.8, 1.6, 2.4 }.Select((shift, index) =>
            new VisualRouteSample(
                index,
                index * 0.1,
                "Tutorial_01",
                1000 + index * 500,
                -700 + index * 300,
                10,
                10,
                FractionalMotionGrid(shift))).ToArray();
        var session = new VisualRouteSession(
            VisualRouteSession.CurrentSchemaVersion,
            Guid.Parse("50000000-0000-4000-8000-000000000002"),
            new DateTimeOffset(2026, 10, 8, 18, 30, 0, TimeSpan.Zero),
            640,
            360,
            samples);
        var route = new VisualRouteStore(Path.Combine(workspace, "route"));
        route.Save(session);
        var world = new LiveWorldSnapshotStore(Path.Combine(workspace, "world"));
        var report = VisualRouteWorldBuilder.BuildAndSave(route, world, keyframeInterval: 2);
        Equal(1, report.Rooms);
        Equal(4, report.Observations);
        Equal(3, report.RelativeMotionEdges);
        Equal(2, report.Keyframes);
        Equal(0, report.RejectedSteps);
        var persisted = world.Load();
        Equal(1, persisted.MapRevision);
        Equal(4, persisted.Observations.Count);
        True(persisted.Observations.All(observation => observation.RoomId == 0));

        var changedTruth = new VisualRouteSession(
            session.SchemaVersion,
            session.Id,
            session.CreatedAt,
            session.FrameWidth,
            session.FrameHeight,
            samples.Select(sample => sample with
            {
                CameraX = sample.CameraX * -9,
                CameraY = sample.CameraY + 12345
            }).ToArray());
        var rebuilt = VisualRouteWorldBuilder.Build(changedTruth, keyframeInterval: 2);
        Equal(
            persisted.Observations[^1].RawPose,
            rebuilt.Snapshot.Observations[^1].RawPose);
    });
}

static void TestVisualRouteWorldStationary()
{
    var grid = FractionalMotionGrid(0);
    var session = new VisualRouteSession(
        VisualRouteSession.CurrentSchemaVersion,
        Guid.Parse("50000000-0000-4000-8000-000000000003"),
        DateTimeOffset.UnixEpoch,
        640,
        360,
        [
            new VisualRouteSample(0, 0, "Tutorial_01", 0, 0, 10, 10, grid),
            new VisualRouteSample(1, 0.1, "Tutorial_01", 999, -999, 10, 10, grid)
        ]);
    var report = VisualRouteWorldBuilder.Build(session);
    Equal(2, report.Observations);
    Equal(1, report.RelativeMotionEdges);
    Equal(0, report.RejectedSteps);
    Equal(0.0, report.Snapshot.RelativeMotionEdges[0].DeltaX);
    Equal(0.0, report.Snapshot.RelativeMotionEdges[0].DeltaY);
}

static void TestVisualRouteWorldScenes()
{
    var grid = FractionalMotionGrid(0);
    var session = new VisualRouteSession(
        VisualRouteSession.CurrentSchemaVersion,
        Guid.Parse("50000000-0000-4000-8000-000000000004"),
        DateTimeOffset.UnixEpoch,
        640,
        360,
        [
            new VisualRouteSample(0, 0, "Room_A", 0, 0, 10, 10, grid),
            new VisualRouteSample(1, 0.1, "Room_B", 0, 0, 10, 10, grid),
            new VisualRouteSample(2, 0.2, "Room_A", 0, 0, 10, 10, grid)
        ]);
    var report = VisualRouteWorldBuilder.Build(session);
    Equal(2, report.Rooms);
    Equal(3, report.Observations);
    Equal(0, report.RelativeMotionEdges);
    Equal(3, report.Keyframes);
    Equal(report.Snapshot.Observations[0].RoomId, report.Snapshot.Observations[2].RoomId);
    True(report.Snapshot.Observations[0].RoomId != report.Snapshot.Observations[1].RoomId);
}

static void TestGroundKernelPolarity()
{
    const int width = 40;
    const int height = 12;
    var matching = new byte[width * height];
    for (var row = 4; row <= 7; row++)
    {
        for (var x = 0; x < width; x++) matching[row * width + x] = 255;
    }
    var result = GroundLineDetector.GroundDetectPixels(
        new GroundEdgeAnalysis(width, height, matching));
    Equal((byte)255, result[4 * width + 20]);

    var opposite = new byte[width * height];
    for (var row = 0; row <= 3; row++)
    {
        for (var x = 0; x < width; x++) opposite[row * width + x] = 255;
    }
    result = GroundLineDetector.GroundDetectPixels(
        new GroundEdgeAnalysis(width, height, opposite));
    Equal((byte)0, result[4 * width + 20]);
}

static void TestGroundOcclusionAndStep()
{
    const int width = 220;
    const int height = 30;
    var pixels = new byte[width * height];
    for (var x = 0; x <= 39; x++) pixels[10 * width + x] = 220;
    for (var x = 160; x <= 199; x++) pixels[10 * width + x] = 220;
    for (var x = 40; x <= 159; x++) pixels[14 * width + x] = 255;
    var lines = GroundLineDetector.CleanFloorLines(
        new GroundComparisonAnalysis(width, height, pixels),
        new GroundTheoryTuning(128, 30, 6, 120));
    var upper = lines.Single(line => line.Row == 10);
    Equal(new PixelSpan(0, 199), upper.XSpan);
    Equal(2, upper.EvidenceSpans.Count);
    Equal(new PixelSpan(0, 39), upper.EvidenceSpans[0]);
    Equal(new PixelSpan(160, 199), upper.EvidenceSpans[1]);
    Equal(new PixelSpan(40, 159), lines.Single(line => line.Row == 14).XSpan);
}

static void TestSemanticGroundSurface()
{
    const int width = 200;
    const int height = 80;
    var pixels = new byte[width * height];
    for (var x = 10; x <= 189; x++) pixels[20 * width + x] = 180;
    for (var x = 60; x <= 139; x++) pixels[40 * width + x] = 150;
    var luma = Enumerable.Repeat((byte)120, width * height).ToArray();
    for (var row = 41; row <= 55; row++)
    {
        for (var x = 60; x <= 139; x++) luma[row * width + x] = 20;
    }
    var lines = GroundLineDetector.SemanticFloorLines(
        new GroundComparisonAnalysis(width, height, pixels, luma),
        GroundTheoryTuning.SemanticDefault);
    Equal(1, lines.Count);
    Equal(40, lines[0].Row);
    Equal(new PixelSpan(60, 139), lines[0].XSpan);
}

static void TestSemanticGroundStrongCore()
{
    const int width = 200;
    const int height = 100;
    var pixels = new byte[width * height];
    for (var x = 20; x <= 119; x++) pixels[20 * width + x] = 8;
    for (var x = 45; x <= 94; x++) pixels[20 * width + x] = 9;
    for (var x = 20; x <= 119; x++) pixels[70 * width + x] = 8;
    var lines = GroundLineDetector.SemanticFloorLines(
        new GroundComparisonAnalysis(
            width,
            height,
            pixels,
            Enumerable.Repeat((byte)20, width * height).ToArray()),
        GroundTheoryTuning.SemanticDefault);
    Equal(1, lines.Count);
    Equal(20, lines[0].Row);
    Equal(new PixelSpan(20, 119), lines[0].XSpan);
}

static void TestGroundForegroundRejection()
{
    var shortObject = new DetectedFloorLine(30, new PixelSpan(40, 79));
    var longFloor = new DetectedFloorLine(30, new PixelSpan(0, 199));
    var filtered = GroundLineDetector.RejectKnownForegroundLines(
        [shortObject, longFloor],
        100,
        [new PixelRect(38, 20, 44, 40)]);
    Equal(1, filtered.Count);
    Equal(longFloor, filtered[0]);

    var knightLine = new DetectedFloorLine(
        35,
        new PixelSpan(20, 180),
        [new PixelSpan(85, 115)]);
    filtered = GroundLineDetector.RejectKnownForegroundLines(
        [knightLine, longFloor],
        100,
        [new PixelRect(85, 25, 30, 40)],
        new PixelRect(85, 25, 30, 40));
    True(!filtered.Contains(knightLine));
}

static void TestGroundAnalysisMask()
{
    var frame = SolidFrame(100, 80, 200, 200, 200);
    var analysis = GroundLineDetector.Analyze(frame, [new PixelRect(40, 30, 10, 10)]);
    Equal((byte)0, analysis.ValidSourcePixels![18 * 100 + 20]);
    Equal((byte)0, analysis.ValidSourcePixels[51 * 100 + 73]);
    Equal((byte)1, analysis.ValidSourcePixels[10 * 100 + 10]);
    Equal((byte)1, analysis.ValidSourcePixels[60 * 100 + 80]);
}

static void TestGroundCapturePadding()
{
    const int width = 100;
    const int height = 80;
    var edges = new byte[width * height];
    for (var x = 0; x < width; x++)
    {
        for (var row = 30; row <= 33; row++) edges[row * width + x] = 30;
        edges[71 * width + x] = 255;
    }
    var values = GroundLineDetector.GroundDetectPixels(
        new GroundEdgeAnalysis(width, height, edges, ValidRows: new Range(0, 72)));
    True(values[30 * width + 50] > 0);
    True(values[(68 * width)..].All(value => value == 0));
}

static void WithTemporaryDirectory(Action<string> run)
{
    var path = Path.Combine(Path.GetTempPath(), $"hkv-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    try
    {
        run(path);
    }
    finally
    {
        Directory.Delete(path, recursive: true);
    }
}

static void TestShortFrameStorage()
{
    var threw = false;
    try
    {
        _ = new BgraFrame(2, 2, 8, new byte[15], 1, DateTimeOffset.UnixEpoch);
    }
    catch (ArgumentException)
    {
        threw = true;
    }
    True(threw);
}

static void TestBmpHeader()
{
    var path = Path.Combine(Path.GetTempPath(), $"hkv-{Guid.NewGuid():N}.bmp");
    try
    {
        BmpWriter.Write(Frame(7), path);
        var data = File.ReadAllBytes(path);
        Equal((byte)'B', data[0]);
        Equal((byte)'M', data[1]);
        Equal(2, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4)));
        Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4)));
    }
    finally
    {
        File.Delete(path);
    }
}

static void TestReceiverHello()
{
    var session = Guid.Parse("193f132f-ae84-423b-ac82-1bcd65dc1fe1");
    var line = ReceiverWireCodec.EncodeLine(ReceiverCapabilityRequest.Create(session));
    True(line.EndsWith('\n'));
    Equal("hello", ReceiverWireCodec.MessageType(line));
    using var document = JsonDocument.Parse(line);
    Equal(2, document.RootElement.GetProperty("version").GetInt32());
    Equal(session, document.RootElement.GetProperty("sessionID").GetGuid());
    True(!document.RootElement.TryGetProperty("renderFrameMarker", out _));
}

static void TestReceiverState()
{
    var session = Guid.Parse("96fce736-b15e-4627-97ec-aa5f1fbdb14e");
    var buttons = InputButtons.Up | InputButtons.ActionZ
        | InputButtons.Inventory | InputButtons.PauseMenu;
    var state = ReceiverInputSnapshot.Create(session, 9, true, buttons);
    Equal((InputButtons)424, state.HeldButtons);
    var line = ReceiverWireCodec.EncodeLine(state);
    var decoded = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(line);
    Equal(state, decoded);

    var disabled = ReceiverInputSnapshot.Create(session, 10, false, InputButtons.Left);
    Equal(InputButtons.None, disabled.HeldButtons);
}

static void TestGroundTruthCameraScale()
{
    const string line = "{\"version\":2,\"type\":\"groundTruth\",\"sessionID\":\"193f132f-ae84-423b-ac82-1bcd65dc1fe1\",\"sequence\":7,\"unityFrame\":99,\"unityRealtime\":12.5,\"sceneName\":\"Tutorial_01\",\"heroAvailable\":true,\"heroX\":36.25,\"heroY\":11.5,\"heroZ\":0.004,\"velocityX\":-2,\"velocityY\":0,\"facingRight\":false,\"grounded\":true,\"cameraAvailable\":true,\"cameraX\":37.25,\"cameraY\":14.1,\"cameraZ\":-38.1,\"cameraTargetX\":36.25,\"cameraTargetY\":14.1,\"cameraTargetZ\":0.004,\"orthographicSize\":4.8,\"pixelsPerWorldUnitX\":200,\"pixelsPerWorldUnitY\":200,\"heroScreenX\":1000,\"heroScreenY\":420,\"projectionPixelWidth\":1920,\"projectionPixelHeight\":1080,\"screenWidth\":1920,\"screenHeight\":1080}";
    var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
    True(sample.HasFiniteCoordinates());
    Close(66.6666667, sample.PixelsPerWorldUnit(360)!.Value);
}

static void TestReceiverRejectsMultiline()
{
    var threw = false;
    try
    {
        _ = ReceiverWireCodec.MessageType("{\"type\":\"hello\"}\n{\"type\":\"state\"}");
    }
    catch (InvalidDataException)
    {
        threw = true;
    }
    True(threw);
}

static void TestReceiverCheckpoint()
{
    var session = Guid.Parse("96fce736-b15e-4627-97ec-aa5f1fbdb14e");
    var commandId = Guid.Parse("9ac417b0-9b42-4817-89a9-0ac004d1255d");
    var command = ReceiverCheckpointCommand.Restore(session, Checkpoint(), commandId);
    var line = ReceiverWireCodec.EncodeLine(command);
    using var json = JsonDocument.Parse(line);
    Equal("restoreCheckpoint", json.RootElement.GetProperty("type").GetString()!);
    Equal(session, json.RootElement.GetProperty("sessionID").GetGuid());
    Equal(commandId, json.RootElement.GetProperty("commandID").GetGuid());
    Equal("Tutorial_01", json.RootElement.GetProperty("checkpoint")
        .GetProperty("sceneName").GetString()!);
}

static void TestRecordedInputPath()
{
    WithTemporaryDirectory(workspace =>
    {
        var value = new RecordedInputPath(
            RecordedInputPath.CurrentSchemaVersion,
            Guid.Parse("bc4eddf6-0152-45f7-88d1-247a1f2489d2"),
            new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero),
            0.5,
            Guid.Parse("de753820-f6bd-4a15-a3e3-f6bbb3dab1d7"),
            2,
            Checkpoint(),
            [
                new RecordedInputPathEvent(0.1, RecordedGameButton.Left,
                    RecordedInputTransition.Pressed),
                new RecordedInputPathEvent(0.2, RecordedGameButton.A,
                    RecordedInputTransition.Pressed),
                new RecordedInputPathEvent(0.3, RecordedGameButton.Left,
                    RecordedInputTransition.Released),
                new RecordedInputPathEvent(0.4, RecordedGameButton.A,
                    RecordedInputTransition.Released)
            ],
            new Dictionary<string, string> { ["capturePixelPath"] = "core-image" });
        var file = Path.Combine(workspace, "path-test.json");
        var store = new RecordedInputPathStore();
        store.Save(value, file);
        var raw = File.ReadAllText(file);
        True(raw.Contains("\"sourcePathID\"", StringComparison.Ordinal));
        True(raw.Contains("\"button\": \"left\"", StringComparison.Ordinal));
        True(raw.Contains("\"transition\": \"pressed\"", StringComparison.Ordinal));
        var loaded = store.Load(file);
        Equal(value.Id, loaded.Id);
        Equal(value.SourcePathId!.Value, loaded.SourcePathId!.Value);
        var changes = loaded.StateChanges();
        Equal(4, changes.Count);
        Equal(InputButtons.Left, changes[0].HeldButtons);
        Equal(InputButtons.Left | InputButtons.ActionA, changes[1].HeldButtons);
        Equal(InputButtons.ActionA, changes[2].HeldButtons);
        Equal(InputButtons.None, changes[3].HeldButtons);
    });
}

static RecordedGameCheckpoint Checkpoint() => new(
    "Tutorial_01",
    "left1",
    3,
    4,
    0,
    0,
    0,
    true,
    true,
    3,
    6,
    -38,
    3,
    6,
    0,
    [new RecordedEnemyCheckpoint("Crawler", 7, 2, 0, 10, true)],
    true,
    true,
    false,
    false,
    false,
    false,
    ["Tutorial_01"]);

static void TestStartupCoordinatorStableNavigation()
{
    var coordinator = new GameStartupCoordinator(2);
    var options = new GameStartupEvidence(GameStartupScreen.Title, false, "Options");
    True(coordinator.Observe(options).Action is null);
    Equal(GameStartupAction.MoveSelectionUp, coordinator.Observe(options).Action!.Value);
    var start = new GameStartupEvidence(GameStartupScreen.Title, false, "Start Game");
    for (var index = 0; index < 17; index++)
    {
        True(coordinator.Observe(start).Action is null);
    }
    Equal(GameStartupAction.SelectStartGame, coordinator.Observe(start).Action!.Value);

    var profile = new GameStartupEvidence(GameStartupScreen.ProfileOne, false, "1.");
    True(coordinator.Observe(profile).Action is null);
    Equal(GameStartupAction.SelectProfileOne, coordinator.Observe(profile).Action!.Value);
    Equal(GameStartupPhase.AwaitingGameplay, coordinator.Phase);
    var gameplay = new GameStartupEvidence(null, true);
    Equal(GameStartupPhase.AwaitingGameplay, coordinator.Observe(gameplay).Phase);
    Equal(GameStartupPhase.Gameplay, coordinator.Observe(gameplay).Phase);
}

static void TestStartupCoordinatorClosesGate()
{
    var coordinator = new GameStartupCoordinator(1);
    coordinator.RestoreGameplaySession();
    True(coordinator.Observe(GameStartupEvidence.Unknown).AdmitsWorldFrames);
    var menu = coordinator.Observe(
        new GameStartupEvidence(GameStartupScreen.Title, false, "Start Game"));
    Equal(GameStartupPhase.AwaitingProfile, menu.Phase);
    Equal(GameStartupAction.SelectStartGame, menu.Action!.Value);
    True(!menu.AdmitsWorldFrames);
}

static void TestStartupDetectorTitleSelection()
{
    var frame = BlankFrame();
    foreach (var rect in new[]
    {
        (289, 209, 61, 9), (297, 231, 45, 8), (283, 254, 75, 7),
        (267, 201, 14, 11), (359, 202, 13, 11)
    }) Paint(frame, rect.Item1, rect.Item2, rect.Item3, rect.Item4);
    var evidence = new GameStartupDetector().Detect(frame);
    Equal(GameStartupScreen.Title, evidence.Screen!.Value);
    Equal("Start Game", evidence.SelectedOption!);
}

static void TestStartupDetectorProfileSelection()
{
    var frame = BlankFrame();
    foreach (var rect in new[]
    {
        (252, 26, 139, 15), (101, 102, 13, 14), (100, 156, 14, 14),
        (57, 154, 15, 13), (434, 154, 16, 13)
    }) Paint(frame, rect.Item1, rect.Item2, rect.Item3, rect.Item4);
    var evidence = new GameStartupDetector().Detect(frame);
    Equal(GameStartupScreen.ProfileOne, evidence.Screen!.Value);
    Equal("2.", evidence.SelectedOption!);
}

static void TestStartupDetectorGameplayHud()
{
    var frame = BlankFrame();
    Paint(frame, 30, 15, 10, 10);
    Paint(frame, 80, 20, 10, 10);
    var evidence = new GameStartupDetector().Detect(frame);
    True(evidence.GameplayLikely);
    True(evidence.Screen is null);
}

static void TestDetectionDecoderLocalMaximum()
{
    var scores = new float[2 * 3 * 3];
    scores[4] = 0.9f;
    scores[5] = 0.8f;
    scores[9 + 0] = 0.7f;
    var boxes = Enumerable.Repeat(0.1f, 4 * 3 * 3).ToArray();
    var detections = DetectionDecoder.Decode(
        scores, [1, 2, 3, 3], boxes, [1, 4, 3, 3], ["knight", "enemy"]);
    Equal(2, detections.Count);
    Equal("knight", detections[0].ClassIdentifier);
    Close(0.9, detections[0].Confidence);
    Equal("enemy", detections[1].ClassIdentifier);
}

static void TestDetectionDecoderClasswiseNms()
{
    var scores = new float[2 * 1 * 3];
    scores[0] = 0.9f;
    scores[2] = 0.8f;
    scores[3] = 0.85f;
    var boxes = new float[4 * 1 * 3];
    for (var index = 0; index < 3; index++)
    {
        boxes[index] = 0.1f;
        boxes[3 + index] = 0.1f;
        boxes[6 + index] = 0.5f;
        boxes[9 + index] = 0.5f;
    }
    var detections = DetectionDecoder.Decode(
        scores, [1, 2, 1, 3], boxes, [1, 4, 1, 3], ["knight", "enemy"]);
    Equal(2, detections.Count);
    Equal("knight", detections[0].ClassIdentifier);
    Equal("enemy", detections[1].ClassIdentifier);
}

static void TestDetectionDecoderRaw()
{
    var scores = new[] { 0.9f, 0.8f, 0.7f };
    var boxes = new float[12];
    for (var index = 0; index < 3; index++)
    {
        boxes[index] = 0.1f;
        boxes[3 + index] = 0.1f;
        boxes[6 + index] = 0.5f;
        boxes[9 + index] = 0.5f;
    }
    var raw = DetectionDecoder.DecodeRaw(
        scores, [1, 1, 1, 3], boxes, [1, 4, 1, 3], ["knight"]);
    Equal(3, raw.Count);
    Equal(0.9f, raw[0].Confidence);
}

static void TestGroundTruthAtlasProjection()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 64, 0);
    var frame = SolidFrame(640, 360, blue: 7, green: 19, red: 211);
    True(mosaic.AddFrame(frame, AtlasTelemetry(cameraX: 0, cameraY: 0)));
    True(mosaic.TileCount > 1);
    True(mosaic.TryReadPixel(-1, -1, out var blue, out var green, out var red, out var observations));
    Equal((byte)7, blue);
    Equal((byte)19, green);
    Equal((byte)211, red);
    Equal((ushort)1, observations);
    True(mosaic.Snapshot().Any(tile => tile.Coordinate.X < 0 && tile.Coordinate.Y < 0));
}

static void TestGroundTruthAtlasBlend()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 128, 0);
    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 0, green: 0, red: 200),
        AtlasTelemetry(cameraX: 0, cameraY: 0)));
    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 200, green: 0, red: 0),
        AtlasTelemetry(cameraX: 0, cameraY: 0)));
    True(mosaic.TryReadPixel(0, 0, out var blue, out var green, out var red, out var observations));
    Equal((byte)100, blue);
    Equal((byte)0, green);
    Equal((byte)100, red);
    Equal((ushort)2, observations);
    Equal(2L, mosaic.AcceptedFrameCount);
}

static void TestGroundTruthAtlasDirtyReset()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 128, 0);
    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 10, green: 20, red: 30),
        AtlasTelemetry(0, 0)));
    var first = mosaic.SnapshotSince(-1);
    True(first.Tiles.Count > 0);
    Equal(0, mosaic.SnapshotSince(first.Revision).Tiles.Count);
    mosaic.Reset();
    Equal(0, mosaic.TileCount);
    Equal(0L, mosaic.AcceptedFrameCount);
    True(mosaic.Revision > first.Revision);
}

static void TestGroundTruthAtlasImport()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 32, 0);
    mosaic.ImportTile(
        new AtlasTileCoordinate(-2, 3),
        SolidFrame(32, 32, blue: 9, green: 8, red: 7));
    Equal(1, mosaic.TileCount);
    True(mosaic.TryReadPixel(-64, 96, out var blue, out var green, out var red, out var count));
    Equal((byte)9, blue);
    Equal((byte)8, green);
    Equal((byte)7, red);
    Equal((ushort)1, count);
    mosaic.RestoreAcceptedFrameCount(912);
    Equal(912L, mosaic.AcceptedFrameCount);
}

static void TestReceiverPlayerOps()
{
    var session = Guid.Parse("b5ca0c11-2e8e-4132-b119-a7c091150780");
    var state = new PlayerTestState(50, -1, 20, 500, 7, -5, true).Normalized();
    Equal(9, state.MaxHealth);
    Equal(1, state.Health);
    Equal(9, state.LifebloodSeed);
    Equal(198, state.Mana);
    Equal(3, state.ExtraManaSlots);
    Equal(0, state.Geo);
    var line = ReceiverWireCodec.EncodeLine(ReceiverPlayerOpsCommand.Apply(session, state));
    Equal("playerOps", ReceiverWireCodec.MessageType(line));
    var decoded = ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsCommand>(line);
    Equal(session, decoded.SessionId);
    Equal("apply", decoded.Operation);
    True(decoded.State == state);
}

static BgraFrame SolidFrame(int width, int height, byte blue, byte green, byte red)
{
    var pixels = new byte[width * height * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
        pixels[index + 3] = 255;
    }
    return new BgraFrame(width, height, width * 4, pixels, 1, DateTimeOffset.UnixEpoch);
}

static ReceiverGroundTruthSample AtlasTelemetry(double cameraX, double cameraY) => new(
    2,
    "groundTruth",
    Guid.Parse("193f132f-ae84-423b-ac82-1bcd65dc1fe1"),
    1,
    1,
    1,
    "Tutorial_01",
    true,
    0,
    0,
    0,
    0,
    0,
    true,
    true,
    true,
    cameraX,
    cameraY,
    -38,
    cameraX,
    cameraY,
    0,
    2.8125,
    64,
    64,
    320,
    180,
    640,
    360,
    640,
    360);

static BgraFrame BlankFrame() => new(
    640, 360, 640 * 4, new byte[640 * 360 * 4], 1, DateTimeOffset.UnixEpoch);

static void Paint(BgraFrame frame, int x, int y, int width, int height)
{
    for (var row = y; row < y + height; row++)
    {
        for (var column = x; column < x + width; column++)
        {
            var offset = row * frame.Stride + column * 4;
            frame.Pixels[offset] = 220;
            frame.Pixels[offset + 1] = 220;
            frame.Pixels[offset + 2] = 220;
            frame.Pixels[offset + 3] = 255;
        }
    }
}

static async Task TestReceiverClientLoopback()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(1);
    try
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var session = Guid.Parse("993813d8-9bfd-47b0-a30e-083b99392966");
        var accept = listener.AcceptTcpClientAsync();
        await using var receiver = new ReceiverClient(session, port: port);
        await receiver.ConnectAsync();
        using var server = await accept;
        using var reader = new StreamReader(
            server.GetStream(), new UTF8Encoding(false), false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(
            server.GetStream(), new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true
        };

        var helloLine = await reader.ReadLineAsync()
            ?? throw new InvalidOperationException("Client did not send hello.");
        var hello = ReceiverWireCodec.DecodeLine<ReceiverCapabilityRequest>(helloLine);
        Equal(session, hello.SessionId);

        var acknowledgement = new ReceiverCapabilitiesAcknowledgement(
            2, "capabilitiesAck", session,
            ["input-state-v1", ReceiverProtocol.GroundTruthCapability], 2000);
        await writer.WriteAsync(ReceiverWireCodec.EncodeLine(acknowledgement));
        var receivedLine = await receiver.ReceiveLineAsync()
            ?? throw new InvalidOperationException("Client did not receive acknowledgement.");
        Equal("capabilitiesAck", ReceiverWireCodec.MessageType(receivedLine));

        await receiver.SendStateAsync(true, InputButtons.Left | InputButtons.ActionA);
        var stateLine = await reader.ReadLineAsync()
            ?? throw new InvalidOperationException("Client did not send input state.");
        var state = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(stateLine);
        Equal(0UL, state.Sequence);
        Equal(InputButtons.Left | InputButtons.ActionA, state.HeldButtons);

        await receiver.PulseAsync(InputButtons.Up, TimeSpan.FromMilliseconds(1));
        var pulsePress = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(
            await reader.ReadLineAsync() ?? throw new InvalidOperationException("No pulse press."));
        var pulseRelease = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(
            await reader.ReadLineAsync() ?? throw new InvalidOperationException("No pulse release."));
        Equal(1UL, pulsePress.Sequence);
        Equal(InputButtons.Up, pulsePress.HeldButtons);
        Equal(2UL, pulseRelease.Sequence);
        Equal(InputButtons.None, pulseRelease.HeldButtons);

        await receiver.SendPointerAsync("leftDown", 0.25, 0.75, 2);
        var pointerLine = await reader.ReadLineAsync()
            ?? throw new InvalidOperationException("No pointer command.");
        var pointer = ReceiverWireCodec.DecodeLine<ReceiverPointerCommand>(pointerLine);
        Equal(0UL, pointer.Sequence);
        Equal("leftDown", pointer.Kind);
        Equal(0.25, pointer.NormalizedX);
        Equal(0.75, pointer.NormalizedY);
        Equal(2, pointer.ClickCount);
    }
    finally
    {
        listener.Stop();
    }
}

static void TestBundledOnnxModel()
{
    DirectoryInfo? cursor = new(AppContext.BaseDirectory);
    string? run = null;
    while (cursor is not null)
    {
        var candidate = System.IO.Path.Combine(cursor.FullName, "models", "shared-object-model");
        if (File.Exists(System.IO.Path.Combine(candidate, "Detector.onnx")))
        {
            run = candidate;
            break;
        }
        cursor = cursor.Parent;
    }
    if (run is null) throw new FileNotFoundException("Bundled Detector.onnx was not found.");
    using var detector = new OnnxObjectDetector(run);
    Equal(7, detector.ClassIdentifiers.Count);
    True(detector.ClassIdentifiers.Contains("game.playable-knight", StringComparer.Ordinal));
    var pixels = new byte[640 * 360 * BgraFrame.BytesPerPixel];
    for (var index = 3; index < pixels.Length; index += BgraFrame.BytesPerPixel) pixels[index] = 255;
    var detections = detector.Detect(new BgraFrame(
        640, 360, 640 * BgraFrame.BytesPerPixel, pixels, 1, DateTimeOffset.UnixEpoch));
    True(detections.All(item => detector.ClassIdentifiers.Contains(
        item.ClassIdentifier, StringComparer.Ordinal)));
}

static void TestUserObjectCatalog()
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hkv-catalog-{Guid.NewGuid():N}");
    try
    {
        var path = System.IO.Path.Combine(root, "object-catalog-v1.json");
        var catalog = new UserObjectCatalog(path);
        var first = catalog.Add("Moss Charger", "Enemies");
        Equal("enemies.moss-charger", first.Identifier);
        Equal(first, catalog.Add("Moss Charger", "enemy"));
        var second = catalog.Add("Breakable Wall", "world");
        Equal("world.breakable-wall", second.Identifier);
        var loaded = new UserObjectCatalog(path).Load();
        Equal(2, loaded.Count);
        True(File.ReadAllText(path).Contains("\"schemaVersion\": 1", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static BgraFrame Frame(long id) => new(
    2,
    2,
    8,
    Enumerable.Repeat((byte)id, 16).ToArray(),
    id,
    DateTimeOffset.UnixEpoch);

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }
}

static void Close(double expected, double actual)
{
    CloseWithin(expected, actual, 0.0001);
}

static void CloseWithin(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }
}

static void ClosePoint(AtlasPoint expected, AtlasPoint actual, double tolerance = 0.000001)
{
    CloseWithin(expected.X, actual.X, tolerance);
    CloseWithin(expected.Y, actual.Y, tolerance);
}
