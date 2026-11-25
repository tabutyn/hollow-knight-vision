using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class SameViewClosureAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly (int Source, int Target)[] PriorityPairs =
    [
        (146, 647), (146, 648), (204, 691), (204, 692), (353, 775), (353, 776), (407, 791), (407, 792),
        (171, 641), (171, 645), (171, 646), (146, 646), (153, 646), (64, 65), (64, 66), (68, 69), (68, 70),
        (71, 72), (71, 73), (74, 75), (74, 76), (84, 85), (84, 86), (175, 176), (175, 177), (327, 328),
        (327, 329), (339, 340), (339, 341), (342, 343), (342, 344), (342, 345)
    ];
    private sealed record Match(AtlasPoint Delta, int Support, double Correlation);
    private sealed record Variant(double RowOffset, Match? Evidence);
    private sealed record Comparison(int SourceId, int TargetId, AtlasPoint? ProposalDelta, int? SceneInliers,
        int ConfirmedSpans, int CandidateSpans, Match? Confirmed, Match? Candidate, Variant[] SubpixelFloorVariants);

    internal static void TestInspectSavedSameViewReturn()
    {
        var results = Compare(Setting("HKV_SAME_VIEW_AUDIT_INPUT"),
            new[] { 1, 3, 13, 42, 46, 47, 132, 343, 622 }.Select(target => (0, target)), [], patchesOnly: true);
        Write("HKV_SAME_VIEW_AUDIT_OUTPUT", results);
    }

    internal static void TestCompareConfirmedAndRawFloorEvidenceWhenRequested()
    {
        var pairs = PriorityPairs;
        if (Environment.GetEnvironmentVariable("HKV_FLOOR_PRIORITY_PAIRS") is { Length: > 0 } selection)
        {
            var requested = JsonSerializer.Deserialize<int[][]>(selection, Json);
            if (requested is not { Length: > 0 } || requested.Any(pair => pair is not [_, _]))
                throw new InvalidDataException("Supply at least one pair of source and target observations.");
            pairs = requested.Take(256).Select(pair => (pair[0], pair[1])).ToArray();
        }
        var offsets = Environment.GetEnvironmentVariable("HKV_FLOOR_PRIORITY_Y_OFFSETS") is { Length: > 0 } values
            ? JsonSerializer.Deserialize<double[]>(values, Json) ?? [] : [];
        var results = Compare(Setting("HKV_FLOOR_PRIORITY_AUDIT_INPUT"), pairs,
            offsets.Take(21).Where(value => double.IsFinite(value) && Math.Abs(value) <= 1).ToArray(), patchesOnly: false);
        Write("HKV_FLOOR_PRIORITY_AUDIT_OUTPUT", results);
    }

    private static Comparison[] Compare(string input, IEnumerable<(int Source, int Target)> pairs,
        double[] offsets, bool patchesOnly)
    {
        var root = Path.Combine(Path.GetFullPath(input), "world");
        var snapshotPath = Path.Combine(root, "scene.snapshot");
        var before = File.ReadAllBytes(snapshotPath);
        var world = LiveWorldSessionStore.ValidateExisting(root).Snapshot;
        using var archive = SceneSessionStore.OpenReadOnly(root);
        var records = archive.Manifest.Observations.ToDictionary(observation => observation.Id);
        var observations = world.Observations.ToDictionary(observation => observation.Id);
        var owned = observations.Values.ToDictionary(observation => observation.SourceObservationId);
        var matcher = new GlobalFeatureMatcher();
        var patchMatcher = new LiveGroundClosureSearch();
        var comparisons = new List<Comparison>();
        foreach (var (sourceId, targetId) in pairs)
        {
            var source = observations[sourceId];
            var target = observations[targetId];
            var reference = archive.Source(records[source.SourceObservationId]);
            var current = archive.Source(records[target.SourceObservationId]);
            var targetMasks = world.CompositionMasks(target);
            var confirmed = target.GroundGeometry?.Lines ?? [];
            var candidate = target.ObservedFloorCandidates?.Lines ?? [];
            var sourceFeatures = matcher.MakeReference(reference, source.OptimizedPose, source.SolveWidth,
                id: new Guid(sourceId, 0, 0, new byte[8]), excluding: FeatureMasks(source));
            GlobalFeatureMatch? proposal = null;
            if (sourceFeatures is not null && source.SolveWidth == target.SolveWidth)
            {
                proposal = patchesOnly ? null : matcher.Match(current, [sourceFeatures], currentSolveWidth: target.SolveWidth,
                    excluding: FeatureMasks(target));
                proposal ??= patchMatcher.ProposePatchClosure([(new(sourceId, source), sourceFeatures)], current,
                    targetMasks, target.SolveWidth, id => (archive.Source(records[id]), WorldMasks(owned[id])));
            }
            var variants = new List<Variant>();
            foreach (var offset in offsets)
            {
                var shift = offset * source.SolveWidth / 640;
                var lines = (candidate.Count != 0 ? candidate : confirmed).Select(line => line with
                {
                    Start = line.Start with { Y = line.Start.Y + shift },
                    End = line.End with { Y = line.End.Y + shift }
                }).ToArray();
                variants.Add(new(offset, Verify(lines)));
            }
            comparisons.Add(new(sourceId, targetId, proposal is null ? null : Delta(proposal.ProposedCameraPose),
                proposal?.InlierReferenceFeatureIds.Count, confirmed.Count, candidate.Count, Verify(confirmed), Verify(candidate),
                variants.ToArray()));

            Match? Verify(IReadOnlyList<LiveGroundLineEvidence> lines)
            {
                if (proposal is null || source.GroundGeometry is not { Lines.Count: > 0 } floor || lines.Count == 0)
                    return null;
                var result = GroundPlaneClosureVerifier.Verify(reference, floor.Lines, source.OptimizedPose, current,
                    lines, proposal.ProposedCameraPose, source.SolveWidth);
                return result is null ? null : new(Delta(result.CameraPose), result.SupportedPatches, result.MeanCorrelation);
            }

            AtlasPoint Delta(AtlasPoint pose) => new(pose.X - source.OptimizedPose.X, pose.Y - source.OptimizedPose.Y);
        }
        if (!File.ReadAllBytes(snapshotPath).AsSpan().SequenceEqual(before))
            throw new InvalidDataException("A read-only diagnostic changed the saved graph.");
        return comparisons.ToArray();

        StencilRect[] FeatureMasks(LiveWorldObservation observation) => world.CompositionMasks(observation)
            .Select(mask => LiveWorldJson.FlipMask(mask, observation.SourceHeight)).ToArray();

        WorldRect[] WorldMasks(LiveWorldObservation observation) => world.CompositionMasks(observation)
            .Select(mask => new WorldRect(new(mask.X, mask.Y), new(mask.Width, mask.Height))).ToArray();
    }

    private static string Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} to run the same-view audit.");

    private static void Write(string setting, Comparison[] comparisons)
    {
        AtomicFile.Write(Setting(setting), JsonSerializer.SerializeToUtf8Bytes(comparisons, Json), overwrite: false);
        Console.WriteLine($"Same-view audit: {comparisons.Length} pairs, "
            + $"{comparisons.Count(comparison => comparison.ProposalDelta.HasValue)} scene proposals, "
            + $"{comparisons.Count(comparison => comparison.Confirmed is not null)} verified by confirmed ground, "
            + $"{comparisons.Count(comparison => comparison.Candidate is not null)} verified by captured candidates.");
    }
}
