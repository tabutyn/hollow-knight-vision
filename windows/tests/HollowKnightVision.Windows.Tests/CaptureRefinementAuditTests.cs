using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class CaptureRefinementAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Source(Guid Id, RecordedVisualFrameArchive? VisualFrameArchive);
    private sealed record Pair(int Id, double Timestamp, double ReferenceTimestamp, int Dx, int Dy, string Source);
    private sealed record Pixels(int Width, int Height, byte[] Luma, IReadOnlyList<DetectedFloorLine> Floors,
        IReadOnlyList<StencilRect> Exclusions);
    private sealed record Consensus(int Dx, int Dy, double Error, int Support);
    private sealed record Refinement(int Id, double Timestamp, double ReferenceTimestamp, string Source, int Dx, int Dy,
        bool Refined, int Patches, CameraVector? Offset, double? ValidationError, Consensus? IndependentConsensus, string? Issue);

    internal static void TestInspectIndependentCaptureRefinementWhenRequested()
    {
        var recording = Path.GetFullPath(Setting("HKV_CAPTURE_REFINEMENT_RECORDING"));
        var source = JsonSerializer.Deserialize<Source>(File.ReadAllBytes(recording), Json);
        if (source?.VisualFrameArchive is not { } archive)
            throw new InvalidDataException("Capture refinement requires an archived recording.");
        var pairs = JsonSerializer.Deserialize<Pair[]>(File.ReadAllBytes(Setting("HKV_CAPTURE_REFINEMENT_PAIRS")), Json);
        if (pairs is not { Length: >= 1 and <= 4_000 } || pairs.Any(pair =>
                !double.IsFinite(pair.Timestamp) || !double.IsFinite(pair.ReferenceTimestamp)))
            throw new InvalidDataException("Provide up to 4,000 explicit capture pairs with finite timestamps.");
        var includeConsensus = Environment.GetEnvironmentVariable("HKV_CAPTURE_CONSENSUS") == "1";
        var reader = new VisualReplayArchiveReader(Path.GetDirectoryName(recording)!, archive, source.Id);
        var frames = reader.Manifest.Frames.Where(frame => frame.FileName is not null).ToLookup(frame => frame.CaptureTimestamp);
        var inputs = reader.Manifest.GroundInputs.ToLookup(input => (input.CaptureGeneration, input.CaptureTimestamp));
        var results = new List<Refinement>();
        foreach (var pair in pairs)
        {
            var old = Load(pair.ReferenceTimestamp);
            var current = Load(pair.Timestamp);
            if (old is null || current is null)
            {
                results.Add(Unavailable(pair, "Archived ground inputs unavailable for this reference pair."));
                continue;
            }
            if (old.Width != current.Width || old.Height != current.Height
                || Math.Abs((long)pair.Dx) > old.Width || Math.Abs((long)pair.Dy) > old.Height)
            {
                results.Add(Unavailable(pair, "The images or proposed translation have incompatible dimensions."));
                continue;
            }
            var patches = ExtractPatches(old);
            var usable = patches.Where(patch => IsUsable(patch, current, pair.Dx, pair.Dy)).ToArray();
            var result = GroundSubpixelRegistration.Refine(usable.Select(patch =>
                new GroundSubpixelRegistration.Patch(patch.X, patch.Y, patch.Pixels)).ToArray(), current.Luma,
                current.Width, current.Height, pair.Dx, pair.Dy);
            var consensus = includeConsensus ? GroundPatchConsensus.Refine(patches, current.Luma,
                current.Width, current.Height, pair.Dx, pair.Dy, current.Exclusions) : null;
            results.Add(new(pair.Id, pair.Timestamp, pair.ReferenceTimestamp, pair.Source, pair.Dx, pair.Dy,
                result is not null, usable.Length, result?.Offset, result?.ValidationError,
                consensus is null ? null : new(consensus.Dx, consensus.Dy, consensus.Error, consensus.Patches.Length), null));
        }
        AtomicFile.Write(Setting("HKV_CAPTURE_REFINEMENT_OUTPUT"), JsonSerializer.SerializeToUtf8Bytes(results, Json), overwrite: false);
        Console.WriteLine($"Capture refinement: {results.Count} pairs, {results.Count(row => row.Issue is null)} with exact owned masks, "
            + $"{results.Count(row => row.Refined)} refined, {results.Count(row => row.IndependentConsensus is not null)} with patch consensus.");

        Pixels? Load(double timestamp)
        {
            if (frames[timestamp].Take(2).ToArray() is not [var frame]
                || inputs[(frame.CaptureGeneration, timestamp)].LastOrDefault() is not { } input) return null;
            var image = reader.ReadImage(frame);
            var masks = VisualReplayGroundInput.RestoreMasks(input, image);
            var exclusions = input.WindowsAtlasExcludingRects is null ? masks.AtlasExclusions : masks.GroundExclusions;
            var edges = GroundLineDetector.Analyze(image, exclusions);
            var floors = GroundLineDetector.RejectKnownForegroundLines(GroundLineDetector.CleanFloorLines(
                GroundLineDetector.Theory(GroundLineDetector.Compare(edges), input.Tuning ?? GroundTheoryTuning.Default)),
                image.Height, exclusions, masks.GroundKnight);
            return new(image.Width, image.Height, edges.SourceLuma ?? throw new InvalidDataException("Missing luma pixels."), floors,
                exclusions.Select(rect => new StencilRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray());
        }
    }

    private static GroundCameraSolver.Patch[] ExtractPatches(Pixels reference)
    {
        const int width = GroundPixels.PatchWidth, height = GroundPixels.PatchHeight;
        var groups = new List<List<GroundCameraSolver.Patch>>();
        foreach (var floor in reference.Floors)
        {
            var group = new List<GroundCameraSolver.Patch>();
            var cells = (floor.XSpan.EndInclusive - floor.XSpan.Start + 1) / width;
            var stride = Math.Max(1, (cells + 7) / 8);
            for (var cell = 0; cell < cells; cell += stride)
            {
                var x = floor.XSpan.Start + cell * width;
                var rectangle = new StencilRect(x, floor.Row, width, height);
                if (!floor.EvidenceSpans.Any(span => span.Start < x + width && span.EndInclusive >= x)
                    || reference.Exclusions.Any(mask => !mask.Intersect(rectangle).IsEmpty)
                    || GroundPixels.Patch(reference.Luma, reference.Width, reference.Height, x, floor.Row) is not { } pixels
                    || pixels.Max() - pixels.Min() < 12) continue;
                group.Add(new(x, floor.Row, pixels));
            }
            if (group.Count != 0) groups.Add(group);
        }
        return Enumerable.Range(0, groups.Count == 0 ? 0 : groups.Max(group => group.Count))
            .SelectMany(index => groups.Where(group => index < group.Count).Select(group => group[index])).Take(32).ToArray();
    }

    private static bool IsUsable(GroundCameraSolver.Patch patch, Pixels current, int dx, int dy)
    {
        var x = patch.X + dx;
        var y = patch.Y + dy;
        var localSearch = new StencilRect(x - 5, y - 5, GroundPixels.PatchWidth + 10, GroundPixels.PatchHeight + 10);
        return localSearch.X >= 0 && localSearch.Y >= 0 && localSearch.Right < current.Width && localSearch.Bottom < current.Height
            && !current.Exclusions.Any(exclusion => !exclusion.Intersect(localSearch).IsEmpty)
            && (current.Floors.Count == 0 || current.Floors.Any(floor => Math.Abs(floor.Row - y) <= 3))
            && GroundPixels.Patch(current.Luma, current.Width, current.Height, x, y) is { } target
            && GroundCameraSolver.PatchError(patch.Pixels, target) <= 18;
    }

    private static Refinement Unavailable(Pair pair, string issue) => new(pair.Id, pair.Timestamp, pair.ReferenceTimestamp,
        pair.Source, pair.Dx, pair.Dy, false, 0, null, null, null, issue);

    private static string Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} to run the capture refinement audit.");
}
