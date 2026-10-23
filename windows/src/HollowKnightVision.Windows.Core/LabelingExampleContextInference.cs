namespace HollowKnightVision.Windows.Core;

/// <summary>Repair only a unique, exact, strictly better complete contract. Shared overlap is not evidence.</summary>
public static class LabelingExampleContextInference
{
    private sealed record SetFit(string Context, int Missing, int Unexpected, int Excess, int Matched)
    {
        public bool IsExact => Missing == 0 && Unexpected == 0 && Excess == 0;
        public (int Violations, int Unexpected, int Missing, int Excess, int NegativeMatched) Rank =>
            (Missing + Unexpected + Excess, Unexpected, Missing, Excess, -Matched);
    }

    public static string Infer(string current, IEnumerable<LabelingExampleAnnotation> annotations,
        LabelingContractCatalog? catalog = null)
    {
        catalog ??= LabelingContractCatalog.Bundled;
        var counts = LabelingContractEvaluator.PositiveCounts(annotations);
        if (counts.Values.Sum() < 2) return current;
        var fits = new List<SetFit>();
        foreach (var contract in catalog.LabelSets)
        {
            var context = LabelingContractCatalog.Contexts.FirstOrDefault(item => item.Identifier == contract.ContextIdentifier);
            if (!contract.RequiresAllContextObjects || context is null) continue;
            var members = context.Labels.Select(item => LabelingClassIdentity.CanonicalIdentifier(item.Identifier)).ToHashSet(StringComparer.Ordinal);
            var missing = 0; var excess = 0; var matched = 0;
            foreach (var identifier in members)
            {
                var count = counts.GetValueOrDefault(identifier);
                var bounds = contract.OccurrenceOverrides?.GetValueOrDefault(identifier);
                var minimum = bounds?.Minimum ?? 1;
                var maximum = bounds?.Maximum ?? (catalog.Occurrence(identifier) is { } occurrence
                    ? occurrence.AllowedCounts?.Where(value => value > 0).Select(value => (int?)value).Max()
                    : catalog.DefaultMaximum);
                missing += Math.Max(0, minimum - count);
                if (maximum is { } limit) excess += Math.Max(0, count - limit);
                if (count > 0) matched++;
            }
            fits.Add(new(context.Identifier, missing, counts.Where(pair => !members.Contains(pair.Key)).Sum(pair => pair.Value), excess, matched));
        }
        var best = fits.OrderBy(fit => fit.Rank).FirstOrDefault();
        if (best is null || !best.IsExact || fits.Count(fit => fit.Rank == best.Rank) != 1) return current;
        var currentFit = fits.FirstOrDefault(fit => fit.Context == current);
        return currentFit is null || best.Rank.CompareTo(currentFit.Rank) < 0 ? best.Context : current;
    }
}
