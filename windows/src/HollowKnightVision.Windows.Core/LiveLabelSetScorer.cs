namespace HollowKnightVision.Windows.Core;

public sealed record LiveLabelSetScore(string ContextIdentifier, double Score, int MatchedObjectCount,
    int DistinctiveObjectCount, int MissingObjectCount, int UnexpectedObjectCount);

public static class LiveLabelSetScorer
{
    public const double MinimumScore = 2, MinimumWinningMargin = .75, MinimumDetectionConfidence = .5;
    private const double UniqueWeight = 4, CommonWeightFloor = .25, MissingPenaltyScale = .20;
    private static readonly IReadOnlyDictionary<string, int> Memberships = LabelingContractCatalog.ScreenContexts
        .SelectMany(context => context.Labels.Select(label => Canonical(label.Identifier)).Distinct(StringComparer.Ordinal))
        .GroupBy(identifier => identifier, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, LabelingOccurrenceBounds>> Expectations =
        LabelingContractCatalog.ScreenContexts.Where(context => context.Identifier != "credits")
            .ToDictionary(context => context.Identifier, Expectation, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> GameplayModelClasses = LabelingContractCatalog.ScreenContexts
        .Single(context => context.Identifier == "game").Labels.Select(label => Canonical(label.Identifier))
        .Where(id => id is not ("game.health" or "game.mana" or "game.geo")).ToHashSet(StringComparer.Ordinal);
    private static string Canonical(string id) => LabelingClassIdentity.CanonicalIdentifier(id);
    public static double EvidenceWeight(string identifier) => Memberships.TryGetValue(Canonical(identifier), out var count) && count > 0
        ? Math.Max(CommonWeightFloor, UniqueWeight / count) : CommonWeightFloor;
    public static IReadOnlyList<LiveLabelSetScore> Scores(IReadOnlyList<ObjectDetection> detections)
    {
        var observed = detections.Where(item => item.Confidence >= MinimumDetectionConfidence)
            .GroupBy(item => Canonical(item.ClassIdentifier), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var results = new List<LiveLabelSetScore>();
        foreach (var (context, expected) in Expectations)
        {
            var matched = 0; var distinctive = 0; var missing = 0; var unexpected = 0; var score = 0d;
            foreach (var (id, bounds) in expected)
            {
                var count = observed.GetValueOrDefault(id); var minimum = bounds.Minimum ?? 0;
                var accepted = bounds.Maximum is { } maximum ? Math.Min(count, maximum) : count;
                var missingCount = Math.Max(0, minimum - count); var surplus = bounds.Maximum is { } cap ? Math.Max(0, count - cap) : 0;
                var weight = EvidenceWeight(id); matched += accepted; missing += missingCount; unexpected += surplus;
                score += accepted * weight - missingCount * weight * MissingPenaltyScale - surplus * weight;
                if (Memberships.GetValueOrDefault(id, int.MaxValue) <= 2) distinctive += accepted;
            }
            foreach (var (id, count) in observed.Where(pair => !expected.ContainsKey(pair.Key)))
            { unexpected += count; score -= count * EvidenceWeight(id); }
            results.Add(new(context, score, matched, distinctive, missing, unexpected));
        }
        return results.OrderByDescending(result => result.Score).ThenByDescending(result => result.DistinctiveObjectCount)
            .ThenByDescending(result => result.MatchedObjectCount).ToArray();
    }
    public static LiveLabelSetScore? BestMatch(IReadOnlyList<ObjectDetection> detections)
    {
        var ranked = Scores(detections); var best = ranked.FirstOrDefault();
        return Qualifies(best) && (ranked.Count < 2 || best!.Score - ranked[1].Score >= MinimumWinningMargin) ? best : null;
    }
    public static LiveLabelSetScore? GameplayMatch(IReadOnlyList<ObjectDetection> detections, IReadOnlySet<string>? customClassIdentifiers = null)
    {
        var gameplay = Scores(detections.Where(item => GameplayModelClasses.Contains(Canonical(item.ClassIdentifier))
            || customClassIdentifiers?.Contains(Canonical(item.ClassIdentifier)) == true).ToArray()).Single(item => item.ContextIdentifier == "game");
        return Qualifies(gameplay) ? gameplay : null;
    }
    private static bool Qualifies(LiveLabelSetScore? score) => score is not null && score.Score >= MinimumScore
        && score.MatchedObjectCount >= 2 && score.DistinctiveObjectCount >= 1;
    private static IReadOnlyDictionary<string, LabelingOccurrenceBounds> Expectation(MenuStencilContext context)
    {
        var catalog = LabelingContractCatalog.Bundled; var overrides = catalog.SetContract(context.Identifier)?.OccurrenceOverrides;
        var result = new Dictionary<string, LabelingOccurrenceBounds>(StringComparer.Ordinal);
        foreach (var label in context.Labels)
        {
            var id = Canonical(label.Identifier); var bounds = overrides?.GetValueOrDefault(id);
            var minimum = context.Identifier == "game" ? 0 : bounds?.Minimum ?? 1;
            var occurrence = catalog.Occurrence(id);
            var maximum = bounds?.Maximum ?? (occurrence is not null ? occurrence.AllowedCounts?.Select(value => (int?)value).Max() : catalog.DefaultMaximum);
            result[id] = new(Math.Max(result.GetValueOrDefault(id)?.Minimum ?? 0, minimum), maximum);
        }
        return result;
    }
}
