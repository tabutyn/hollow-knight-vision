namespace HollowKnightVision.Windows.Core;

public sealed record BotRecoveredWallFrame(long Generation, double CaptureTimestamp, IReadOnlyList<double> Features);

/// <summary>Recovers visual wall inputs only from a clip's uniquely matched, verified source image and masks.</summary>
public static class BotWallReplay
{
    public static IReadOnlyList<BotRecoveredWallFrame> Recover(string clip, string archive)
    {
        var capture = BotRecordedAtlasPath.ReadCapture(clip);
        if (capture.Domain != BotCaptureDomain.Gameplay) return [];
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archive));
        var root = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Expected a visual replay archive directory.");
        var replayId = capture.Manifest.ReplayId ?? capture.Manifest.Id;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFileName(path), $"frames-{replayId:D}", comparison))
            throw new InvalidDataException("The wall replay archive does not belong to this clip.");
        var reader = VisualReplayArchiveReader.OpenClipFrames(root, replayId);
        var frames = reader.Manifest.Frames.Where(frame => frame.FileName is not null)
            .ToLookup(frame => (frame.CaptureGeneration, BitConverter.DoubleToInt64Bits(frame.CaptureTimestamp)));
        if (reader.Manifest.GroundInputs.Any(input => input is null || !double.IsFinite(input.CaptureTimestamp)))
            throw new InvalidDataException("Visual replay ground input has invalid timing.");
        var inputs = reader.Manifest.GroundInputs
            .ToLookup(input => (input.CaptureGeneration, BitConverter.DoubleToInt64Bits(input.CaptureTimestamp)));

        var recovered = new List<BotRecoveredWallFrame>();
        foreach (var row in capture.Observations)
        {
            if (row.Generation < 0 || !double.IsFinite(row.CaptureTimestamp) || row.Issues is not { Count: 0 }
                || row.TerrainSource is not (null or "detected") || row.Features.Take(4).Any(value => !double.IsFinite(value) || Math.Abs(value) > 4))
                continue;
            var key = ((ulong)row.Generation, BitConverter.DoubleToInt64Bits(row.CaptureTimestamp));
            if (frames[key].Take(2).ToArray() is not [var match]
                || inputs[key].Take(2).ToArray() is not [{ ExcludingRects: not null, SemanticTuning: not null } input])
                continue;

            var image = reader.ReadImage(match);
            var masks = VisualReplayGroundInput.RestoreMasks(input, image);
            // The Mac wall recovery command uses the saved scene exclusions,
            // including its HUD shield. The live Windows solver splits that shield.
            if (input.WindowsAtlasExcludingRects is null)
                masks = masks with { GroundExclusions = masks.AtlasExclusions };
            var analysis = GameplayTerrainAnalysis.Analyze(image, masks, input.Tuning, input.SemanticTuning);
            var walls = TerrainWallDetector.Detect(analysis.Edges, analysis.Floors).Walls;
            var terrain = new BotScreenTerrain(image.Width, image.Height, [], BotObservationBuilder.ProjectDetectedWalls(image, walls));
            var features = new double[BotRecordingContract.FeatureNames.Count];
            BotObservationBuilder.WriteWallFeatures(features, terrain, row.Features[0] * image.Width, row.Features[1] * image.Width);
            recovered.Add(new(row.Generation, row.CaptureTimestamp,
                features.Skip(BotRecordingContract.LegacyWidth).Select(value => Math.Clamp(value, -4, 4)).ToArray()));
        }
        return recovered;
    }
}
