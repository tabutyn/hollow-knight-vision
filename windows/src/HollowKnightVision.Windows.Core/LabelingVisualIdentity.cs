using System.Text;

namespace HollowKnightVision.Windows.Core;

public readonly record struct LabelingRgb(double Red, double Green, double Blue);

/// <summary>Mac's canonical equal-hue class palette and 64-bit UTF-8 fallback hash.</summary>
public sealed class LabelingVisualIdentity
{
    private readonly Dictionary<string, int> indices;
    private static readonly IReadOnlyDictionary<string, string> Names = LabelingContractCatalog.Contexts.SelectMany(context => context.Labels)
        .DistinctBy(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier), StringComparer.Ordinal)
        .ToDictionary(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier), label => label.Name, StringComparer.Ordinal);
    public LabelingVisualIdentity(IEnumerable<string>? customClasses = null)
    {
        indices = Names.Keys.Concat(customClasses ?? []).Select(LabelingClassIdentity.CanonicalIdentifier).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select((identifier, index) => (identifier, index)).ToDictionary(pair => pair.identifier, pair => pair.index, StringComparer.Ordinal);
    }
    public static string DisplayName(string identifier)
    { var canonical = LabelingClassIdentity.CanonicalIdentifier(identifier); return Names.GetValueOrDefault(canonical, canonical); }
    public LabelingRgb Rgb(string identifier)
    {
        var canonical = LabelingClassIdentity.CanonicalIdentifier(identifier);
        ulong index;
        if (indices.TryGetValue(canonical, out var registered)) index = (ulong)registered;
        else
        {
            index = 5381;
            foreach (var value in Encoding.UTF8.GetBytes(canonical)) index = unchecked(index * 33 + value);
            index &= long.MaxValue;
        }
        var hue = (double)(index % (ulong)Math.Max(indices.Count, 1)) / Math.Max(indices.Count, 1);
        var h = (hue - Math.Floor(hue)) * 6; var sector = (int)Math.Floor(h); var fraction = h - Math.Floor(h);
        const double p = 1 - .78; var q = 1 - .78 * fraction; var t = 1 - .78 * (1 - fraction);
        return (sector % 6) switch { 0 => new(1, t, p), 1 => new(q, 1, p), 2 => new(p, 1, t), 3 => new(p, q, 1), 4 => new(t, p, 1), _ => new(1, p, q) };
    }
}
