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
