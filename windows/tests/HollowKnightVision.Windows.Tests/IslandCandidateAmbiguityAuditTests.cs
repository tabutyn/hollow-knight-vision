using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class IslandCandidateAmbiguityAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Reference(LiveWorldObservation Observation, int Island, GlobalFeatureReference Features);
    private sealed record Proposal(int CurrentId, int SourceId, int CurrentIsland, int SourceIsland,
        AtlasPoint Delta, int Support, double Span, double Correlation);

    internal static void TestInspectSavedIslandCandidateAmbiguity()
    {
        var root = Path.Combine(Path.GetFullPath(Setting("HKV_ISLAND_CANDIDATE_AUDIT_INPUT")), "world");
        var snapshotPath = Path.Combine(root, "scene.snapshot");
        var original = File.ReadAllBytes(snapshotPath);
        var world = LiveWorldSessionStore.ValidateExisting(root).Snapshot;
        using var reader = SceneSessionStore.OpenReadOnly(root);
        var observations = world.Observations.ToDictionary(observation => observation.Id);
        var savedFrames = reader.Manifest.Observations.ToDictionary(frame => frame.Id);
        var owners = world.TrackingIslands.SelectMany(island => island.Members.Where(member => member.Measured)
            .Select(member => (member.ObservationId, island.Id))).ToDictionary(owner => owner.ObservationId, owner => owner.Id);
        var images = new Dictionary<int, BgraFrame>();
        var order = new Queue<int>();
        var referenceBuilder = new GlobalFeatureMatcher();
        var references = new List<Reference>();
        foreach (var keyframe in world.Keyframes)
        {
            var observation = observations[keyframe.ObservationId];
            if (!owners.TryGetValue(observation.Id, out var island) || observation.GroundGeometry is not { Lines.Count: > 0 }) continue;
            if (referenceBuilder.MakeReference(Image(observation), observation.OptimizedPose, observation.SolveWidth,
                    id: new Guid(observation.Id, 0, 0, new byte[8]), excluding: Masks(observation)) is { } features)
                references.Add(new(observation, island, features));
        }
        var pooledMisses = 0;
        var partitionProposals = 0;
        var proposals = new List<Proposal>();
        foreach (var current in references)
        {
            var observation = current.Observation;
            var eligible = references.Where(reference => reference.Observation.Id < observation.Id
                && reference.Observation.RoomId == observation.RoomId && reference.Island != current.Island
                && reference.Observation.SolveWidth == observation.SolveWidth).ToArray();
            if (eligible.Length == 0) continue;
            var pixels = Image(observation);
            var exclusions = Masks(observation);
            if (Find(eligible) is not null) continue;
            pooledMisses++;
            foreach (var partition in eligible.GroupBy(reference => reference.Island).OrderBy(group => group.Key))
            {
                if (Find(partition) is not { } match) continue;
                partitionProposals++;
                var source = partition.Single(reference => reference.Features.Id == match.ReferenceId).Observation;
                if (GroundPlaneClosureVerifier.Verify(Image(source), source.GroundGeometry!.Lines, source.OptimizedPose,
                        pixels, observation.GroundGeometry!.Lines, match.ProposedCameraPose, observation.SolveWidth) is not { } floor)
                    continue;
                proposals.Add(new(observation.Id, source.Id, current.Island, partition.Key,
                    new(floor.CameraPose.X - source.OptimizedPose.X, floor.CameraPose.Y - source.OptimizedPose.Y),
                    floor.SupportedPatches, floor.HorizontalSpan, floor.MeanCorrelation));
            }

            GlobalFeatureMatch? Find(IEnumerable<Reference> candidates) => new GlobalFeatureMatcher().Match(
                pixels, candidates.Select(reference => reference.Features).ToArray(),
                currentSolveWidth: observation.SolveWidth, excluding: exclusions);
        }
        if (references.Count == 0 || !File.ReadAllBytes(snapshotPath).AsSpan().SequenceEqual(original))
            throw new InvalidDataException("The diagnostic requires a read-only world with measured, grounded keyframes.");
        AtomicFile.Write(Setting("HKV_ISLAND_CANDIDATE_AUDIT_OUTPUT"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            references = references.Count,
            pooledMisses,
            partitionProposals,
            groundVerifiedProposals = proposals.Count,
            proposals
        }, Json), overwrite: false);
        Console.WriteLine($"Island match audit: {references.Count} references, {pooledMisses} pooled misses, "
            + $"{partitionProposals} per-island proposals, {proposals.Count} verified against floor texture.");

        BgraFrame Image(LiveWorldObservation observation)
        {
            var id = observation.SourceObservationId;
            if (images.TryGetValue(id, out var pixels)) return pixels;
            pixels = reader.Source(savedFrames[id]);
            if (order.Count == 12) images.Remove(order.Dequeue());
            order.Enqueue(id);
            images.Add(id, pixels);
            return pixels;
        }

        StencilRect[] Masks(LiveWorldObservation observation) => world.CompositionMasks(observation)
            .Select(mask => LiveWorldJson.FlipMask(mask, observation.SourceHeight)).ToArray();
    }

    private static string Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} to run the saved-island candidate audit.");
}
