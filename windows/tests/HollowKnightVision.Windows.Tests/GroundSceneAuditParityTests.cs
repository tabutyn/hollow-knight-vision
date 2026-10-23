using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class GroundSceneAuditParityTests
{
    public static void TestSceneAuditCoordinateAndAlphaConversion()
    {
        var hudAt360 = ToTopOrigin([new PixelRect(40, 290, 130, 65)], 640, 360).Single();
        var hudAt480 = ToTopOrigin([new PixelRect(40, 290, 130, 65)], 640, 480).Single();
        Check(hudAt360 == new PixelRect(40, 5, 130, 65),
            "Scene audit bottom-origin HUD mask was not converted for a 360-pixel image.");
        Check(hudAt480 == new PixelRect(40, 125, 130, 65),
            "Scene audit bottom-origin HUD mask was not converted from the actual image height.");

        const int width = 4;
        const int height = 58;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        Array.Fill(pixels, byte.MaxValue);
        var retainedOffset = 56 * width * BgraFrame.BytesPerPixel;
        pixels[retainedOffset] = 200;
        pixels[retainedOffset + 1] = 100;
        pixels[retainedOffset + 2] = 50;
        pixels[retainedOffset + 3] = 128;
        var topRight = retainedOffset + (width - 1) * BgraFrame.BytesPerPixel;
        pixels[topRight] = 40;
        pixels[topRight + 1] = 80;
        pixels[topRight + 2] = 120;
        pixels[topRight + 3] = 255;
        var bottomLeft = retainedOffset + width * BgraFrame.BytesPerPixel;
        pixels[bottomLeft] = 0;
        pixels[bottomLeft + 1] = 0;
        pixels[bottomLeft + 2] = 255;
        pixels[bottomLeft + 3] = 64;
        var bottomRight = bottomLeft + (width - 1) * BgraFrame.BytesPerPixel;
        pixels[bottomRight] = 255;
        pixels[bottomRight + 1] = 255;
        pixels[bottomRight + 2] = 255;
        pixels[bottomRight + 3] = 0;
        var source = new BgraFrame(width, height, width * BgraFrame.BytesPerPixel,
            pixels, 1, DateTimeOffset.UnixEpoch);
        var canvas = CropAndScaleScene(source, topCrop: 56);
        Check(canvas.Width == 640 && canvas.Height == 360,
            "Scene audit scaling did not produce the fixed 640×360 canvas.");

        var topRow = canvas.Row(0);
        CheckPixel(topRow, 0, blue: 100, green: 50, red: 25,
            "Scene audit scaling did not alpha-composite the top-left content pixel.");
        CheckPixel(topRow, 639, blue: 40, green: 80, red: 120,
            "Scene audit scaling did not preserve the top-right content column.");
        var lastContentRow = canvas.Row(319);
        CheckPixel(lastContentRow, 0, blue: 0, green: 0, red: 64,
            "Scene audit scaling did not alpha-composite the last content row.");
        CheckPixel(lastContentRow, 639, blue: 0, green: 0, red: 0,
            "Scene audit scaling did not turn transparent content opaque black.");
        CheckPixel(canvas.Row(320), 0, blue: 0, green: 0, red: 0,
            "Scene audit scaled content extended below its expected 320 rows.");
        CheckPixel(canvas.Row(359), 639, blue: 0, green: 0, red: 0,
            "Scene audit unused canvas boundary was not opaque black.");
    }

    public static void TestRecordedReturnFrameRelocalizesFromDriftedPose()
    {
        var startPath = Environment.GetEnvironmentVariable("HKV_GROUND_RETURN_START");
        var returnPath = Environment.GetEnvironmentVariable("HKV_GROUND_RETURN_END");
        if (startPath is null || returnPath is null)
        {
            Console.WriteLine("SKIP recorded return ground audit: set HKV_GROUND_RETURN_START and HKV_GROUND_RETURN_END.");
            return;
        }

        var start = BgraPngReader.Read(startPath);
        var returned = BgraPngReader.Read(returnPath);
        var macExclusions = new[]
        {
            new PixelRect(284, 84, 26, 64),
            new PixelRect(74, 313, 92, 26),
            new PixelRect(68, 288, 60, 31),
            new PixelRect(28, 289, 76, 60),
        };
        var startExclusions = ToTopOrigin(macExclusions, start.Width, start.Height);
        var returnedExclusions = ToTopOrigin(macExclusions, returned.Width, returned.Height);
        var startAnalysis = GroundLineDetector.Analyze(start, startExclusions);
        var returnedAnalysis = GroundLineDetector.Analyze(returned, returnedExclusions);
        CheckAnalysisDimensions(startAnalysis, start);
        CheckAnalysisDimensions(returnedAnalysis, returned);
        var startLines = GroundLineDetector.CleanFloorLines(
            GroundLineDetector.Compare(startAnalysis), GroundTheoryTuning.Default);
        var returnedLines = GroundLineDetector.CleanFloorLines(
            GroundLineDetector.Compare(returnedAnalysis), GroundTheoryTuning.Default);
        Console.WriteLine($"GROUND_RETURN start={Describe(startLines)}");
        Console.WriteLine($"GROUND_RETURN end={Describe(returnedLines)}");

        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        GroundHypothesisTrackingResult seeded = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 8; index++)
        {
            seeded = tracker.Update(
                startAnalysis.SourceLuma ?? throw new InvalidDataException("Start capture has no luma plane."),
                start.Width,
                start.Height,
                startLines,
                timestamp: index / 60d,
                protectedOcclusions: startExclusions);
        }
        Check(seeded.GlobalFeatureCount > 0, "Recorded start frame did not seed global ground features.");

        tracker.ReanchorLocalCamera(new AtlasPoint(276, 83));
        var result = seeded;
        for (var index = 0; index < 12; index++)
        {
            result = tracker.Update(
                returnedAnalysis.SourceLuma ?? throw new InvalidDataException("Return capture has no luma plane."),
                returned.Width,
                returned.Height,
                returnedLines,
                timestamp: 1 + index * .8,
                protectedOcclusions: returnedExclusions);
            Console.WriteLine(
                $"GROUND_RETURN step={index} pose={Point(result.CameraPosition)} verified={result.PoseVerified} " +
                $"matches={result.GlobalMatchCount} correction={Point(result.GlobalCorrection)}");
        }

        var camera = result.CameraPosition
            ?? throw new InvalidOperationException("Recorded return frame did not produce a camera position.");
        var distance = Math.Sqrt(camera.X * camera.X + camera.Y * camera.Y);
        Check(distance < 32,
            $"Recorded return frame remained {distance:0.###} pixels from origin.");
    }

    public static void TestLabeledGameplayGroundDetectionCostAndEvidence()
    {
        var root = Environment.GetEnvironmentVariable("HKV_LABELING_EXAMPLES_DIRECTORY");
        if (root is null)
        {
            Console.WriteLine("SKIP labeled gameplay ground audit: set HKV_LABELING_EXAMPLES_DIRECTORY.");
            return;
        }
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        int[] thresholds = [4, 6, 8, 10, 12, 16, 20, 24, 30, 40, 60];
        var frameCounts = thresholds.ToDictionary(value => value, _ => 0);
        var lineCounts = thresholds.ToDictionary(value => value, _ => 0);
        var evidenceCounts = thresholds.ToDictionary(value => value, _ => 0);
        var elapsedMilliseconds = new List<double>();
        var peakResponses = new List<byte>();
        var frameCount = 0;
        var totalEvidence = 0;

        foreach (var directory in Directory.EnumerateDirectories(root)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, "example.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Labeling example is missing example.json.", manifestPath);
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (!document.RootElement.TryGetProperty("contextIdentifier", out var contextElement)
                || contextElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(contextElement.GetString()))
                throw new InvalidDataException($"Labeling manifest has no valid contextIdentifier: {manifestPath}");
            if (!string.Equals(contextElement.GetString(), "game", StringComparison.Ordinal)) continue;

            var imagePath = Path.Combine(directory, "image.png");
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Gameplay labeling example is missing image.png.", imagePath);
            var image = BgraPngReader.Read(imagePath, frameCount);
            var stopwatch = Stopwatch.StartNew();
            var comparison = GroundLineDetector.Compare(GroundLineDetector.Analyze(image));
            var defaultLines = GroundLineDetector.CleanFloorLines(comparison, GroundTheoryTuning.Default);
            stopwatch.Stop();
            elapsedMilliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
            peakResponses.Add(comparison.GroundPixels.Max());
            totalEvidence += EvidenceCount(defaultLines);

            foreach (var threshold in thresholds)
            {
                var lines = GroundLineDetector.CleanFloorLines(
                    comparison, new GroundTheoryTuning(threshold, 30, 40, 300));
                if (lines.Count > 0) frameCounts[threshold]++;
                lineCounts[threshold] += lines.Count;
                evidenceCounts[threshold] += EvidenceCount(lines);
            }
            frameCount++;
            Console.WriteLine($"GROUND_FRAME {Path.GetFileName(directory)} rows={Describe(defaultLines)}");
        }

        Check(frameCount > 0, "No immediate gameplay labeling examples were found.");
        elapsedMilliseconds.Sort();
        peakResponses.Sort();
        Console.WriteLine(
            $"GROUND_AUDIT frames={frameCount} medianMs={elapsedMilliseconds[elapsedMilliseconds.Count / 2]:0.###} " +
            $"evidence={totalEvidence}");
        Console.WriteLine($"GROUND_PEAKS {string.Join(",", peakResponses)}");
        foreach (var threshold in thresholds)
        {
            Console.WriteLine(
                $"GROUND_THRESHOLD threshold={threshold} frames={frameCounts[threshold]} " +
                $"lines={lineCounts[threshold]} evidence={evidenceCounts[threshold]}");
        }
    }

    public static void TestCapturedPlatformScene()
    {
        var root = Environment.GetEnvironmentVariable("HKV_SCENE_AUDIT_DIRECTORY");
        if (root is null)
        {
            Console.WriteLine("SKIP captured platform scene audit: set HKV_SCENE_AUDIT_DIRECTORY.");
            return;
        }
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        string[] names = ["baseline-game.png", "01-held-game.png", "01-returned-game.png"];
        foreach (var (name, captureId) in names.Select((name, index) => (name, index)))
        {
            var sourcePath = Path.Combine(root, name);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Scene audit capture is missing.", sourcePath);
            var source = BgraPngReader.Read(sourcePath, captureId);
            var frame = CropAndScaleScene(source, topCrop: 56);
            foreach (var masked in new[] { false, true })
            {
                IReadOnlyList<PixelRect> exclusions = masked
                    ? [new PixelRect(40, 5, 130, 65)]
                    : [];
                var comparison = GroundLineDetector.Compare(GroundLineDetector.Analyze(frame, exclusions));
                Check(comparison.Width == 640 && comparison.Height == 360,
                    $"Scene audit detector dimensions changed for {name}.");
                Check(comparison.GroundPixels.Length == 640 * 360,
                    $"Scene audit detector output is incomplete for {name}.");
                var lines = GroundLineDetector.CleanFloorLines(comparison, GroundTheoryTuning.Default);
                Console.WriteLine($"SCENE_AUDIT {name} masked={masked.ToString().ToLowerInvariant()} lines={Describe(lines)}");
                var rows = Enumerable.Range(0, comparison.Height)
                    .Select(row => (Row: row, Peak: comparison.GroundPixels
                        .AsSpan(row * comparison.Width, comparison.Width).ToArray().Max()))
                    .Where(item => item.Peak >= 60)
                    .Select(item => $"{item.Row}:{item.Peak}");
                Console.WriteLine($"SCENE_ROWS {name} masked={masked.ToString().ToLowerInvariant()} {string.Join(",", rows)}");
            }
        }

    }

    private static BgraFrame CropAndScaleScene(BgraFrame source, int topCrop)
    {
        if (source.Height <= topCrop) throw new InvalidDataException("Scene audit capture is shorter than its top crop.");
        var croppedHeight = source.Height - topCrop;
        var croppedPixels = new byte[checked(source.Width * croppedHeight * BgraFrame.BytesPerPixel)];
        for (var y = 0; y < croppedHeight; y++)
        {
            var sourceRow = source.Row(y + topCrop);
            var targetRow = croppedPixels.AsSpan(y * source.Width * BgraFrame.BytesPerPixel);
            for (var x = 0; x < source.Width; x++)
            {
                var offset = x * BgraFrame.BytesPerPixel;
                var alpha = sourceRow[offset + 3];
                targetRow[offset] = CompositeOverBlack(sourceRow[offset], alpha);
                targetRow[offset + 1] = CompositeOverBlack(sourceRow[offset + 1], alpha);
                targetRow[offset + 2] = CompositeOverBlack(sourceRow[offset + 2], alpha);
                targetRow[offset + 3] = byte.MaxValue;
            }
        }
        var cropped = new BgraFrame(source.Width, croppedHeight, source.Width * BgraFrame.BytesPerPixel,
            croppedPixels, source.CaptureId, source.ObservedAt);

        const int targetWidth = 640;
        const int canvasHeight = 360;
        var targetHeight = Math.Max(1, (int)Math.Round(
            cropped.Height * targetWidth / (double)cropped.Width, MidpointRounding.AwayFromZero));
        var scaled = new ImageStencilPixels(cropped, targetWidth, targetHeight);
        var canvas = new byte[checked(targetWidth * canvasHeight * BgraFrame.BytesPerPixel)];
        for (var index = 3; index < canvas.Length; index += BgraFrame.BytesPerPixel) canvas[index] = byte.MaxValue;
        for (var y = 0; y < Math.Min(targetHeight, canvasHeight); y++)
        for (var x = 0; x < targetWidth; x++)
        {
            var color = scaled.Rgb(x, y) ?? throw new InvalidDataException("Scaled scene pixel is unavailable.");
            var offset = (y * targetWidth + x) * BgraFrame.BytesPerPixel;
            canvas[offset] = color.Blue;
            canvas[offset + 1] = color.Green;
            canvas[offset + 2] = color.Red;
        }
        return new BgraFrame(targetWidth, canvasHeight, targetWidth * BgraFrame.BytesPerPixel,
            canvas, source.CaptureId, source.ObservedAt);
    }

    private static int EvidenceCount(IEnumerable<DetectedFloorLine> lines) =>
        lines.Sum(line => line.EvidenceSpans.Sum(span => span.EndInclusive - span.Start + 1));

    private static PixelRect[] ToTopOrigin(
        IEnumerable<PixelRect> bottomOrigin,
        int imageWidth,
        int imageHeight) =>
        bottomOrigin.Select(rect =>
        {
            var top = checked(imageHeight - rect.Bottom);
            if (rect.X < 0 || top < 0 || rect.Right > imageWidth || rect.Bottom > imageHeight)
                throw new InvalidDataException("Ground return exclusion lies outside the loaded capture.");
            return new PixelRect(rect.X, top, rect.Width, rect.Height);
        }).ToArray();

    private static byte CompositeOverBlack(byte component, byte alpha) =>
        (byte)((component * alpha + 127) / 255);

    private static void CheckPixel(
        ReadOnlySpan<byte> row,
        int x,
        byte blue,
        byte green,
        byte red,
        string message)
    {
        var offset = checked(x * BgraFrame.BytesPerPixel);
        Check(row[offset] == blue
              && row[offset + 1] == green
              && row[offset + 2] == red
              && row[offset + 3] == byte.MaxValue,
            message);
    }

    private static void CheckAnalysisDimensions(GroundEdgeAnalysis analysis, BgraFrame frame)
    {
        Check(analysis.Width == frame.Width && analysis.Height == frame.Height,
            "Ground return detector dimensions do not match the loaded capture.");
        Check(analysis.SourceLuma?.Length == checked(frame.Width * frame.Height),
            "Ground return detector did not retain a complete luma plane.");
    }

    private static string Describe(IEnumerable<DetectedFloorLine> lines) => string.Join(",",
        lines.Select(line => $"{line.Row}:{line.XSpan.Start}-{line.XSpan.EndInclusive}"));

    private static string Point(AtlasPoint? point) => point is null
        ? "nil"
        : $"({point.Value.X.ToString("0.###", CultureInfo.InvariantCulture)}," +
          $"{point.Value.Y.ToString("0.###", CultureInfo.InvariantCulture)})";

    private static string Point(CameraVector? point) => point is null
        ? "nil"
        : $"({point.Value.X.ToString("0.###", CultureInfo.InvariantCulture)}," +
          $"{point.Value.Y.ToString("0.###", CultureInfo.InvariantCulture)})";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
