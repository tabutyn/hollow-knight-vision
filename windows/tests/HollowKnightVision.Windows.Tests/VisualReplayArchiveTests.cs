using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class VisualReplayArchiveTests
{
    public static void TestInspectionKeepsPixelsTimingAndExactCaptureIdentity()
    {
        using var fixture = new Fixture();
        var first = fixture.SaveImage(0, 90.1, marker: 392);
        var second = fixture.SaveImage(1, 90.2);
        var manifest = fixture.Write([first, second], [new(90.1, 9), new(90.2, 9)],
            [new("visionStarted", 90.1, 9), new("visionCompleted", 90.1, 9)]);
        var reader = new VisualReplayArchiveReader(fixture.Root, manifest.Summary, fixture.Id);

        var image = reader.ReadImage(second);
        Check(image.Width == 2 && image.Height == 1 && image.CaptureId == 1);
        Check(image.Row(0).SequenceEqual(Fixture.Pixels), "The source BGRA pixels changed.");
        Check(reader.Manifest.StageEvents.Select(item => item.Stage).SequenceEqual(
            new[] { "visionStarted", "visionCompleted" }));
        Check(reader.Manifest.Frames.Select(item => item.CapturedAt).SequenceEqual(
            new[] { first.CapturedAt, second.CapturedAt }));
        var report = VisualReplayArchiveReader.Inspect(fixture.PathFile);
        Check(report.Complete && report.ArchiveAvailable && report.ReadableFrames == 2);
        Check(report.GroundInputsWithImages == 2 && report.ExactFrameMarkers == 1
            && report.MarkerCoveragePercent == 50 && report.FirstCaptureTimestamp == 90.1
            && report.LastCaptureTimestamp == 90.2);

        var stored = new RecordedInputPathStore().Load(fixture.PathFile);
        Check(stored.VisualFrameArchive == manifest.Summary);
        fixture.Write([first, second], [new(90.1, 8), new(Math.BitIncrement(90.2), 9)]);
        report = VisualReplayArchiveReader.Inspect(fixture.PathFile);
        Check(!report.Complete && report.ReadableFrames == 2 && report.GroundInputsWithImages == 0,
            "Ground inputs borrowed the wrong capture generation or a nearby timestamp.");
    }

    public static void TestPartialArchiveAndLegacyPathRemainReadable()
    {
        using var fixture = new Fixture();
        fixture.SavePath(null);
        var legacy = VisualReplayArchiveReader.Inspect(fixture.PathFile);
        Check(legacy.PathId == fixture.Id && !legacy.ArchiveAvailable && !legacy.Complete);

        var first = fixture.SaveImage(0, 6);
        var dropped = first with { Index = 1, CaptureTimestamp = 7, FileName = null, Sha256 = null,
            Issue = "writerBackpressure" };
        var failed = dropped with { Index = 2, CaptureTimestamp = 8, Issue = "imageWriteFailed" };
        var manifest = fixture.Write([first, dropped, failed], [new(7, 9)]);
        Reject(() => new VisualReplayArchiveReader(fixture.Root, manifest.Summary, fixture.Id), "incomplete");
        var report = VisualReplayArchiveReader.Inspect(fixture.PathFile);
        Check(report.ArchiveAvailable && !report.Complete && report.ReadableFrames == 1
            && report.CapturedFrames == 3 && report.DroppedFrames == 1 && report.FailedFrames == 1
            && report.LastCaptureTimestamp == 8 && report.GroundInputsWithImages == 0);
    }

    public static void TestCorruptedOrSwappedFrameEvidenceIsRejected()
    {
        using var fixture = new Fixture();
        var original = fixture.SaveImage(0, 7);
        var manifest = fixture.Write([original], []);
        var reader = new VisualReplayArchiveReader(fixture.Root, manifest.Summary, fixture.Id);
        Reject(() => reader.ReadImage(original with { CaptureGeneration = 8 }), "invalid evidence");
        Reject(() => new VisualReplayArchiveReader(fixture.Root, manifest.Summary, Guid.NewGuid()), "does not match");
        Reject(() => new VisualReplayArchiveReader(fixture.Root,
            manifest.Summary with { DirectoryName = "../external" }, fixture.Id), "does not match");
        fixture.Write([original with { Width = 1 }], []);
        Reject(() => VisualReplayArchiveReader.Inspect(fixture.PathFile), "dimensions");
        fixture.Write([original with { FileName = "../outside.png" }], []);
        Reject(() => VisualReplayArchiveReader.Inspect(fixture.PathFile), "invalid evidence");
        fixture.Write([original with { Index = 2 }], []);
        Reject(() => VisualReplayArchiveReader.Inspect(fixture.PathFile), "inconsistent");
        fixture.Write([original], [], schema: 2);
        Reject(() => VisualReplayArchiveReader.Inspect(fixture.PathFile), "unsupported");
        fixture.Write([original], []);
        File.WriteAllText(Path.Combine(fixture.Frames, original.FileName!), "replaced");
        Reject(() => VisualReplayArchiveReader.Inspect(fixture.PathFile), "checksum");
    }

    public static void TestRecorderLinksLosslessArchiveToTheSavedPath() => VerifyRecording().GetAwaiter().GetResult();

    private static async Task VerifyRecording()
    {
        using var fixture = new Fixture();
        var recorder = new InputPathRecorder();
        Check(recorder.Start(timestamp: 100, id: fixture.Id, visualArchiveRoot: fixture.Root));
        var image = new BgraFrame(2, 3, 8, [.. Fixture.Pixels, .. Fixture.Pixels, .. Fixture.Pixels], 12, DateTimeOffset.UnixEpoch);
        var masks = new GameplaySceneMasks(new(null, new(0, 0, 1, 1), default, default),
            new(new(1, 0, 1, 1), default, default, default), [new(0, 0, 1, 1)], [new(1, 0, 1, 1)], new(1, 0, 1, 1))
        { ObservedPlayerBounds = new(.25, .5, .5, 1.25) };
        recorder.RecordVisualFrame(image, 99.9, 100.1, 7, 390);
        recorder.RecordVisualStage("visionCompleted", 99.9, 7);
        recorder.RecordVisualGroundInput(99.9, 7, image.Height, masks, InputButtons.Left);
        recorder.RecordVisualFrame(image, 100.1, 100.103, 8, 391);
        recorder.RecordVisualStage("visionStarted", 100.1, 8);
        recorder.RecordVisualGroundInput(100.1, 8, image.Height, masks, InputButtons.Right | InputButtons.ActionX,
            recentJump: true);
        recorder.RecordVisualStage("visionCompleted", 100.1, 8);
        recorder.RecordInput(RecordedGameButton.Right, true, 100.15);

        var stopped = recorder.StopAsync(100.2);
        Check(!recorder.IsRecording);
        recorder.RecordVisualFrame(image, 100.3, 100.303, 8, 392);
        var path = await stopped ?? throw new InvalidOperationException("Missing recorded input path.");
        Check(path.VisualFrameArchive is { Complete: true, CapturedFrameCount: 1, SavedFrameCount: 1 });
        Check(path.Events.Count == 2 && path.Events[^1].Transition == RecordedInputTransition.Released);
        new RecordedInputPathStore(fixture.Root).Save(path, fixture.PathFile);
        var inspection = VisualReplayArchiveReader.Inspect(fixture.PathFile);
        Check(inspection.Complete && inspection.GroundInputsWithImages == 1 && inspection.ExactFrameMarkers == 1);

        var archive = new VisualReplayArchiveReader(fixture.Root, path.VisualFrameArchive!, fixture.Id);
        Check(archive.Manifest.Configuration["platform"] == "windows");
        var frame = archive.Manifest.Frames.Single();
        Check(frame is { CaptureGeneration: 8, CaptureTimestamp: 100.1, CapturedAt: 100.103, RenderedGameFrame: 391, SolveWidth: 2 });
        Check(archive.ReadImage(frame).Row(0).SequenceEqual(Fixture.Pixels));
        var ground = archive.Manifest.GroundInputs.Single();
        Check(ground.Tuning == GroundTheoryTuning.Default && ground.SemanticTuning == GroundTheoryTuning.SemanticDefault);
        var originalBounds = ground.ObservedPlayerBounds!;
        Check(ground.WindowsObservedPlayerBoundsMeasured == true
            && originalBounds[0].SequenceEqual(new double[] { .25, 1.25 })
            && originalBounds[1].SequenceEqual(new double[] { .5, 1.25 }));
        var restored = VisualReplayGroundInput.RestoreMasks(ground, image);
        Check(restored.ObservedPlayerBounds == masks.ObservedPlayerBounds && restored.GroundKnight == masks.GroundKnight);
        Check(VisualReplayGroundInput.RestoreMasks(ground, image, exactInput: false).ObservedPlayerBounds is null,
            "A borrowed mask cannot carry measured feet into the next capture.");
        var legacy = ground with { WindowsObservedPlayerBoundsMeasured = null, ObservedPlayerBounds = ground.KnightRect };
        Check(VisualReplayGroundInput.RestoreMasks(legacy, image).ObservedPlayerBounds is null,
            "Legacy Windows archives recorded the swept mask in the player field.");
        Check(ground.HorizontalDrive == 1 && ground.WindowsMovementButtons == (InputButtons.Right | InputButtons.ActionX)
            && ground.RecentJump == true);
        Check(ground.ExcludingRects!.Single()[0][0] == 1 && ground.WindowsAtlasExcludingRects!.Single()[0][0] == 0);
        Check(archive.Manifest.StageEvents.Select(stage => stage.Stage).SequenceEqual(new[] { "visionStarted", "visionCompleted" }));
    }

    public static void TestWriterBoundsRetainedFramesAndDrainsInCaptureOrder() => VerifyWriterBounds().GetAwaiter().GetResult();

    private static async Task VerifyWriterBounds()
    {
        using var fixture = new Fixture();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var writer = new VisualReplayArchiveWriter(fixture.Root, fixture.Id, new Dictionary<string, string>(), 2,
            (image, path) => {
                writing.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Test encoder timed out.");
                PngWriter.Write(image, path);
            });
        var frame = new BgraFrame(2, 1, 8, Fixture.Pixels, 1, DateTimeOffset.UnixEpoch);
        writer.Append(frame, 1, 1.01, 7, 2, 11);
        Task<RecordedVisualFrameArchive>? completed = null;
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writer.Append(frame, 2, 2.01, 7, 2, 12);
            writer.Append(frame, 3, 3.01, 7, 2, 13);
            writer.Append(frame, double.PositiveInfinity, 4, 7, 2, null);
            completed = writer.FinishAsync();
            Check(!completed.IsCompleted && ReferenceEquals(completed, writer.FinishAsync(cancelled: true)),
                "An unfinished image was overtaken by archive finalization.");
            writer.Append(frame, 5, 5.01, 7, 2, 15);
            writer.RecordStage(new("visionCompleted", 5, 7));
        }
        finally
        {
            resume.Set();
            if (completed is null) await writer.FinishAsync(cancelled: true);
        }

        var summary = await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Check(summary is { Complete: false, PeakPendingImages: 2, CapturedFrameCount: 4, SavedFrameCount: 2, DroppedFrameCount: 1, FailedFrameCount: 1 });
        var reader = new VisualReplayArchiveReader(fixture.Root, summary, fixture.Id, requireComplete: false);
        Check(reader.Manifest.Frames.Select(item => item.Index).SequenceEqual(new[] { 0, 1, 2, 3 }));
        Check(reader.Manifest.Frames[2] is { CaptureTimestamp: 3, RenderedGameFrame: 13, Issue: "writerBackpressure" });
        Check(reader.Manifest.Frames[3] is { CaptureTimestamp: 0, CapturedAt: 0, Issue: "invalidCapture" });
        Check(reader.Manifest.StageEvents.Count == 0);
        Check(reader.ReadImage(reader.Manifest.Frames[0]).Row(0).SequenceEqual(Fixture.Pixels));
        Check(reader.ReadImage(reader.Manifest.Frames[1]).Row(0).SequenceEqual(Fixture.Pixels));
    }

    public static void TestWriterRecordsCancellationAndWriteFailures() => VerifyWriterFailures().GetAwaiter().GetResult();

    private static async Task VerifyWriterFailures()
    {
        using var fixture = new Fixture();
        var invalidRoot = Path.Combine(fixture.Root, "file-instead-of-folder");
        File.WriteAllText(invalidRoot, "file");
        var blocked = new VisualReplayArchiveWriter(invalidRoot, fixture.Id, new Dictionary<string, string>());
        var frame = new BgraFrame(2, 1, 8, Fixture.Pixels, 1, DateTimeOffset.UnixEpoch);
        blocked.Append(frame, 1, 1.01, 7, 2, 1);
        var failed = await blocked.FinishAsync();
        Check(failed is { Complete: false, CapturedFrameCount: 1, FailedFrameCount: 1 }
            && failed.Issue?.StartsWith("manifestWriteFailed:", StringComparison.Ordinal) == true);

        var writer = new VisualReplayArchiveWriter(fixture.Root, fixture.Id, new Dictionary<string, string>(),
            writeImage: (_, _) => throw new IOException("Test encoder failure."));
        writer.Append(frame, 1, 1.01, 7, 2, 1);
        var cancelled = await writer.FinishAsync(cancelled: true);
        Check(cancelled is { Complete: false, Issue: "cancelled", SavedFrameCount: 0, FailedFrameCount: 1 });
        var reader = new VisualReplayArchiveReader(fixture.Root, cancelled, fixture.Id, requireComplete: false);
        Check(reader.Manifest.Frames.Single().Issue?.StartsWith("imageWriteFailed:", StringComparison.Ordinal) == true);
    }

    private static void Check(bool result, string message = "Visual replay archive assertion failed.")
    {
        if (!result) throw new InvalidOperationException(message);
    }

    private static void Reject(Action attempt, string errorText)
    {
        try { attempt(); }
        catch (InvalidDataException error) when (error.Message.Contains(errorText, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"Expected rejection containing {errorText}.");
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] Pixels = [4, 9, 24, 92, 12, 31, 50, 255];
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"hkv-visual-replay-{Guid.NewGuid():N}");
        internal Guid Id { get; } = Guid.NewGuid();
        internal string PathFile => Path.Combine(Root, "path-recorded.json");
        internal string Frames => Path.Combine(Root, $"frames-{Id:D}");

        internal Fixture() => Directory.CreateDirectory(Frames);

        internal VisualReplayFrame SaveImage(int index, double timestamp, int? marker = null)
        {
            var name = FormattableString.Invariant($"{index:D8}.png");
            var imagePath = Path.Combine(Frames, name);
            PngWriter.Write(new(2, 1, 8, Pixels, index, DateTimeOffset.UnixEpoch), imagePath);
            var checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))).ToLowerInvariant();
            return new(index, timestamp, timestamp + .03, 9, 640, 2, 1, marker, name, checksum, null);
        }

        internal VisualReplayManifest Write(IReadOnlyList<VisualReplayFrame> frames,
            IReadOnlyList<VisualReplayGroundInput> ground, IReadOnlyList<VisualReplayStageEvent>? events = null,
            int schema = 1)
        {
            var saved = frames.Count(frame => frame.FileName is not null);
            var dropped = frames.Count(frame => frame.Issue == "writerBackpressure");
            var summary = new RecordedVisualFrameArchive(Path.GetFileName(Frames), frames.Count, saved, dropped,
                frames.Count - saved - dropped, 1, saved > 0 && saved == frames.Count);
            var manifest = new VisualReplayManifest(schema, Id, new Dictionary<string, string>(), frames,
                events ?? [], ground, summary);
            File.WriteAllText(Path.Combine(Frames, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            SavePath(summary);
            return manifest;
        }

        internal void SavePath(RecordedVisualFrameArchive? summary) =>
            new RecordedInputPathStore(Root).Save(new RecordedInputPath(RecordedInputPath.CurrentSchemaVersion,
                Id, DateTimeOffset.UnixEpoch, 10, null, null, null, [], trackingSamples: [], visualFrameArchive: summary),
                PathFile);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
