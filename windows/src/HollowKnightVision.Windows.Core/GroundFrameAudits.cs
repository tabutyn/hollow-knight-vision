using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

/// <summary>Opt-in capture of the exact inputs and results of the live ground tracker.</summary>
public sealed class GroundFrameAudits
{
    private const string PngOption = "--ground-audit-directory=";
    private const string RawOption = "--ground-raw-audit-directory=";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly AuditStream? png;
    private readonly AuditStream? raw;
    private double? lastBankAt;

    private GroundFrameAudits(AuditStream? png, AuditStream? raw)
    {
        this.png = png;
        this.raw = raw;
    }

    public bool RecordsRawFrames => raw is not null;

    public static GroundFrameAudits? Launch(IReadOnlyList<string> arguments) =>
        Launch(arguments, Environment.GetEnvironmentVariable("HKV_GROUND_AUDIT_DIRECTORY"));

    internal static GroundFrameAudits? Launch(IReadOnlyList<string> arguments, string? pngEnvironment,
        Action<string>? beforeWrite = null)
    {
        var pngPath = Normalize(LastValue(PngOption) ?? pngEnvironment);
        var rawPath = Normalize(LastValue(RawOption));
        if (rawPath is not null && string.Equals(pngPath, rawPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            rawPath = Path.Combine(rawPath, "raw");
        var images = AuditStream.Open(pngPath, .1, int.MaxValue, beforeWrite);
        var pixels = AuditStream.Open(rawPath, 0, 6_000, beforeWrite);
        return images is null && pixels is null ? null : new(images, pixels);

        string? LastValue(string option) =>
            arguments.LastOrDefault(value => value.StartsWith(option, StringComparison.Ordinal))?[option.Length..];

        static string? Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value)) return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
        }
    }

    internal void Record(BgraFrame frame, double timestamp, long generation, byte[] luma,
        IReadOnlyList<DetectedFloorLine> floors, IReadOnlyList<PixelRect> exclusions, GroundTheoryTuning tuning,
        GroundHypothesisTrackingResult tracking, IReadOnlyList<PixelRect> patches, GroundHypothesisTracker tracker)
    {
        var width = frame.Width;
        var height = frame.Height;
        var captureId = frame.CaptureId;
        png?.Offer(timestamp, _ => path =>
        {
            var payload = new
            {
                timestamp, captureId, generation, width, height,
                tracking.PoseVerified, camera = Point(tracking.CameraPosition),
                tuning = new[] { tuning.GroundThreshold, tuning.MinimumSegmentLength, tuning.LineSeparation, tuning.OcclusionMergeGap },
                surfaceArchitecture = GroundLabelParameters.CurrentSurfaceAlgorithm,
                exclusions = exclusions.Select(Rect),
                lines = floors.Select(Describe),
                reviews = tracking.LineReviews.Select(item => new
                {
                    item.Id, item.Line.Row, x0 = item.Line.XSpan.Start, x1 = item.Line.XSpan.EndInclusive,
                    item.State, item.VisibleSeconds, item.DetectedFraction
                }),
                matchedPatchRects = patches.Select(Rect),
                features = tracking.Features.Select(item => new
                {
                    item.Id, segment = item.SegmentId, index = item.SequenceIndex,
                    rect = Rect(item.ImageRect), item.Classification
                }),
                atlasLines = tracking.AtlasLines.Select(item => new
                {
                    id = item.SegmentId, x0 = item.AtlasStart.X, x1 = item.AtlasEnd.X, y = item.AtlasStart.Y
                }),
                atlasFeatures = tracking.AtlasFeatures.Select(item => new
                {
                    segment = item.SegmentId, index = item.SequenceIndex, item.AtlasPosition.X, item.AtlasPosition.Y,
                    supportedPixels = item.ReferenceOpacity.Count(alpha => alpha > 0)
                })
            };
            WriteSample(path, ".png", payload, filename => PngWriter.Write(frame, filename));
        });

        if (raw is null || luma.LongLength != (long)width * height) return;
        raw.Offer(timestamp, skipped =>
        {
            object? bank = null;
            if (lastBankAt is null || timestamp - lastBankAt.Value >= 1)
            {
                bank = tracker.SnapshotAuditFeatureBank();
                lastBankAt = timestamp;
            }
            return path =>
            {
                var payload = new
                {
                    timestamp, captureId, generation, width, height, skippedWrites = skipped,
                    tracking.PoseVerified, camera = Point(tracking.CameraPosition),
                    delta = Vector(tracking.CameraTranslation), correction = Vector(tracking.GlobalCorrection),
                    exclusions = exclusions.Select(Rect), lines = floors.Select(Describe),
                    featureBank = bank is null ? null : Path.GetFileName(path + ".bank")
                };
                WriteSample(path, ".luma", payload, filename => AtomicFile.Write(filename, luma, overwrite: false), bank);
            };
        });

        int[] Rect(PixelRect rect) => [rect.X, height - rect.Bottom, rect.Width, rect.Height];
    }

    // Snapshot the worker tasks only after the capture loop has stopped supplying frames.
    public Task DrainAsync() => Task.WhenAll(png?.Pending ?? Task.CompletedTask, raw?.Pending ?? Task.CompletedTask);

    private static double[] Point(AtlasPoint? point) => point is { } value ? [value.X, value.Y] : [];

    private static double[] Vector(CameraVector? vector) => vector is { } value ? [value.X, value.Y] : [];

    private static object Describe(DetectedFloorLine line) => new
    {
        line.Row, x0 = line.XSpan.Start, x1 = line.XSpan.EndInclusive,
        evidence = line.EvidenceSpans.Select(span => new[] { span.Start, span.EndInclusive })
    };

    private static void WriteSample(string path, string extension, object payload, Action<string> writeImage, object? bank = null)
    {
        var image = path + extension;
        var bankPath = path + ".bank";
        var imageWritten = false;
        var bankWritten = false;
        try
        {
            writeImage(image);
            imageWritten = true;
            if (bank is not null)
            {
                AtomicFile.Write(bankPath, JsonSerializer.SerializeToUtf8Bytes(bank, Json), overwrite: false);
                bankWritten = true;
            }
            // Metadata commits a complete sample. Never remove a pre-existing file after a collision.
            AtomicFile.Write(path + ".json", JsonSerializer.SerializeToUtf8Bytes(payload, Json), overwrite: false);
        }
        catch (Exception error) when (IsAuditFailure(error))
        {
            if (imageWritten) DeleteSampleFile(image);
            if (bankWritten) DeleteSampleFile(bankPath);
        }
    }

    private static void DeleteSampleFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (IsAuditFailure(error)) { }
    }

    private static bool IsAuditFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;

    private sealed class AuditStream(string directory, double interval, int maximumRecords, long startIndex, Action<string>? beforeWrite)
    {
        private readonly object gate = new();
        private Task pending = Task.CompletedTask;
        private double lastTimestamp = double.NegativeInfinity;
        private long fileIndex = startIndex;
        private long skipped;
        private int submitted;

        internal Task Pending { get { lock (gate) return pending; } }

        internal static AuditStream? Open(string? directory, double interval, int maximumRecords, Action<string>? beforeWrite)
        {
            if (directory is null) return null;
            try
            {
                Directory.CreateDirectory(directory);
                var highest = Directory.EnumerateFiles(directory).Select(path =>
                    long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        ? index : 0).DefaultIfEmpty().Max();
                return new(directory, interval, maximumRecords, highest, beforeWrite);
            }
            catch (Exception error) when (IsAuditFailure(error)) { return null; }
        }

        internal void Offer(double timestamp, Func<long, Action<string>> prepare)
        {
            lock (gate)
            {
                if (!double.IsFinite(timestamp) || timestamp <= lastTimestamp || timestamp - lastTimestamp < interval
                    || submitted >= maximumRecords || fileIndex == long.MaxValue) return;
                if (!pending.IsCompleted)
                {
                    skipped++;
                    return;
                }
                var write = prepare(skipped);
                var path = Path.Combine(directory, (++fileIndex).ToString("D8", CultureInfo.InvariantCulture));
                submitted++;
                lastTimestamp = timestamp;
                pending = Task.Run(() =>
                {
                    beforeWrite?.Invoke(path);
                    write(path);
                });
            }
        }
    }
}
