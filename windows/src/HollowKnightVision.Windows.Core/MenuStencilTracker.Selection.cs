using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

public sealed partial class MenuStencilTracker
{
    private sealed record Row(Option Option, Score Left, Score Right, IReadOnlyList<MenuStencilMatch> Matches, int LanguageEvidence)
    {
        public double Confidence => (Left.Confidence + Right.Confidence) / 2;
        public double MinimumConfidence => Math.Min(Left.Confidence, Right.Confidence);
        public int Foreground => Math.Min(Left.Foreground, Right.Foreground);
        public int BrightForeground => Math.Min(Left.BrightForeground, Right.BrightForeground);
    }

    private Selection DetectSelection(Scene scene, string? language, ImageStencilPixels pixels, BgraFrame image, double timestamp,
        bool recordsCalibration, IReadOnlyDictionary<string, StencilRect>? anchorBounds = null, IReadOnlySet<string>? excluded = null)
    {
        if (scene.Options.Count == 0) return new(null, [], [], 0, 0);
        Row? Pair(Option option, int radiusX, int radiusY)
        {
            if (option.LeftSelector is not { } leftSelector || option.RightSelector is not { } rightSelector
                || SelectorBounds(scene, option, language) is not { } configured || RowBand(scene, option, language) is not { } rowBand) return null;
            var genericLeft = scene.SelectorVariants.GetValueOrDefault("left") ?? [];
            var genericRight = scene.SelectorVariants.GetValueOrDefault("right") ?? [];
            if (leftSelector.Variants.Count == 0 && genericLeft.Count == 0 || rightSelector.Variants.Count == 0 && genericRight.Count == 0) return null;
            StencilRect? verifiedText = anchorBounds?.TryGetValue(option.ClassIdentifier, out var currentText) == true ? currentText : null;
            var textBounds = verifiedText ?? scene.Anchors.FirstOrDefault(anchor => anchor.ClassIdentifier == option.ClassIdentifier)?.Variants
                    .FirstOrDefault(variant => variant.LanguageIdentifier == language && variant.NominalBounds is not null)?.NominalBounds ?? option.Bounds;
            Score? ScoreSelector(IReadOnlyList<Variant> variants, StencilRect bounds, bool usesFallback, MenuSelectorSide side, ImageStencilForegroundMask? mask)
            {
                var expected = bounds.Offset(trackedOffset.X, trackedOffset.Y);
                var text = verifiedText ?? textBounds.Offset(trackedOffset.X, trackedOffset.Y);
                bool Accepts(StencilRect candidate) => candidate.MidY >= rowBand.Lower && candidate.MidY <= rowBand.Upper
                    && (side == MenuSelectorSide.Left ? candidate.MidX < text.X : candidate.MidX > text.Right);
                var primary = BestScore(usesFallback ? variants.Take(1).ToArray() : variants, pixels, expected, radiusX, radiusY, accepts: Accepts, idleMask: mask);
                if (primary is null) return null;
                if (!usesFallback || variants.Count <= 1 || primary.Confidence >= SelectorThreshold
                    || !(radiusX > SelectorSearchRadiusX || radiusY > SelectorSearchRadiusY || primary.Foreground >= SelectorMinimumForegroundPixels)) return primary;
                var fallback = BestScore(variants.Skip(1).Take(4).ToArray(), pixels, expected, radiusX, radiusY, accepts: Accepts, idleMask: mask);
                return fallback is not null && fallback.Confidence > primary.Confidence ? fallback : primary;
            }
            IReadOnlyList<Variant> OrderedVariants(IReadOnlyList<Variant> variants, bool positioned) => !positioned ? variants
                : variants.Where(variant => variant.LanguageIdentifier is null)
                    .Concat(variants.Where(variant => variant.LanguageIdentifier == language))
                    .Concat(variants.Where(variant => variant.LanguageIdentifier is not null && variant.LanguageIdentifier != language)).ToArray();
            Score? LocalizedScore(PositionedSelector selector, StencilRect current, IReadOnlyList<Variant> generic, MenuSelectorSide side)
            {
                var positioned = selector.Variants.Count > 0;
                var variants = OrderedVariants(positioned ? selector.Variants : generic, positioned);
                var mask = selector.IdleMaskFor(language);
                var score = ScoreSelector(variants, current, positioned, side, mask);
                if (score is null) return null;
                if ((scene.ContextIdentifier == "video" || option.ClassIdentifier == "shared.reset-defaults")
                    && score.Confidence < .65 && CenterDistance(current, selector.Bounds) > radiusX
                    && ScoreSelector(variants, selector.Bounds, positioned, side, mask) is { } deployed && deployed.Confidence > score.Confidence + .03) return deployed;
                return score;
            }
            var left = LocalizedScore(leftSelector, configured.Left, genericLeft, MenuSelectorSide.Left);
            var right = LocalizedScore(rightSelector, configured.Right, genericRight, MenuSelectorSide.Right);
            if (left is null || right is null) return null;
            var matches = new MenuStencilMatch[]
            {
                new("shared.select-decoration", option.Name + " left", OutputRect(left.Rect, image, scene), left.Confidence, left.Reference),
                new("shared.select-decoration", option.Name + " right", OutputRect(right.Rect, image, scene), right.Confidence, right.Reference)
            };
            return new(option, left, right, matches, new[] { left, right }.Count(score => score.Language is not null && score.Language == language));
        }
        IReadOnlyList<Row> RankedRows(int radiusX, int radiusY) => scene.Options.Where(option => excluded?.Contains(option.ClassIdentifier) != true)
            .Select(option => Pair(option, radiusX, radiusY)).Where(row => row is not null).Cast<Row>().OrderByDescending(row => row.Confidence).ToArray();
        Row? WinningRow(IReadOnlyList<Row> rows)
        {
            var qualified = rows.Where(row =>
            {
                if (Math.Abs(row.Left.Rect.MidY - row.Right.Rect.MidY) > SelectorMaximumPairRowDelta) return false;
                if (anchorBounds?.TryGetValue(row.Option.ClassIdentifier, out var verified) == true
                    && !(row.Left.Rect.MidX < verified.X && row.Right.Rect.MidX > verified.Right)) return false;
                if (scene.ContextIdentifier == "select-profile" && Math.Max(row.Left.Confidence, row.Right.Confidence) >= .90
                    && row.MinimumConfidence >= .30 && row.LanguageEvidence > 0) return true;
                if (scene.ContextIdentifier == "main-title" && row.Foreground < 12) return false;
                if (SelectorBounds(scene, row.Option, language) is { } configured
                    && row.Right.Rect.MidX - row.Left.Rect.MidX < Math.Max(24, (configured.Right.MidX - configured.Left.MidX) * .30)) return false;
                var faintReset = row.Option.ClassIdentifier == "shared.reset-defaults" && row.Foreground >= 32 && row.MinimumConfidence >= .18;
                return faintReset || QualifiedSelectorPairConfidence(row.Left.Confidence, row.Right.Confidence, row.Left.BrightForeground, row.Right.BrightForeground) is not null;
            }).ToArray();
            var sameLanguage = qualified.Where(row => scene.ContextIdentifier == "select-profile" && Math.Max(row.Left.Confidence, row.Right.Confidence) >= .90 && row.LanguageEvidence > 0).ToArray();
            var bright = qualified.Where(row => row.BrightForeground >= 8).ToArray();
            var foreground = qualified.Where(row => row.Foreground >= SelectorMinimumForegroundPixels).ToArray();
            var normal = qualified.Where(row => row.MinimumConfidence >= SelectorThreshold).ToArray();
            var contenders = sameLanguage.Length > 0 ? sameLanguage : bright.Length > 0 ? bright : foreground.Length > 0 ? foreground : normal.Length > 0 ? normal : qualified;
            var margin = sameLanguage.Length > 0 || bright.Length > 0 || foreground.Length > 0 || normal.Length > 0 ? .005 : SelectorWinningMargin;
            return contenders.Length > 0 && contenders[0].Confidence - (contenders.Length > 1 ? contenders[1].Confidence : 0) >= margin ? contenders[0] : null;
        }
        var winner = WinningRow(RankedRows(SelectorSearchRadiusX, SelectorSearchRadiusY));
        if (winner is null || winner.MinimumConfidence < SelectorThreshold) winner = WinningRow(RankedRows(SelectorRecoveryRadiusX, SelectorRecoveryRadiusY));
        if (winner is null)
        {
            calibration.WaitingForMatch();
            return new(null, [], SearchRegions(scene, image, trackedOffset, language), 0, 0);
        }
        if (recordsCalibration)
            calibration.RecordMatch(scene.ContextIdentifier, winner.Option.ClassIdentifier, trackedLanguage,
                SelectorBounds(scene, winner.Option, trackedLanguage), new(winner.Left.Rect, winner.Right.Rect), timestamp);
        var regions = SearchRegions(scene, image, trackedOffset, language).Select(region => region with { IsSolved = region.OptionIdentifier == winner.Option.ClassIdentifier }).ToArray();
        return new(winner.Option.Name, winner.Matches, regions, winner.LanguageEvidence, winner.Foreground);
    }

    private MenuSelectorBounds? SelectorBounds(Scene scene, Option option, string? language)
    {
        MenuSelectorBounds? configured = option.LeftSelector is not null && option.RightSelector is not null
            ? new(option.LeftSelector.BoundsFor(language), option.RightSelector.BoundsFor(language)) : null;
        return calibration.CurrentBounds(scene.ContextIdentifier, option.ClassIdentifier, language, configured);
    }
    private (double Lower, double Upper)? RowBand(Scene scene, Option option, string? language)
    {
        var rows = scene.Options.Select(candidate => (candidate.ClassIdentifier, Bounds: SelectorBounds(scene, candidate, language)))
            .Where(row => row.Bounds.HasValue).Select(row => (row.ClassIdentifier, Center: (row.Bounds!.Value.Left.MidY + row.Bounds.Value.Right.MidY) / 2))
            .OrderBy(row => row.Center).ToArray();
        var selected = Array.FindIndex(rows, row => row.ClassIdentifier == option.ClassIdentifier); if (selected < 0) return null;
        var centers = new List<double>();
        foreach (var row in rows)
        {
            if (centers.Count > 0 && Math.Abs(centers[^1] - row.Center) <= 4) centers[^1] = (centers[^1] + row.Center) / 2;
            else centers.Add(row.Center);
        }
        var index = Enumerable.Range(0, centers.Count).MinBy(index => Math.Abs(centers[index] - rows[selected].Center));
        var lower = index > 0 ? (centers[index - 1] + centers[index]) / 2 : 0;
        var upper = index + 1 < centers.Count ? (centers[index] + centers[index + 1]) / 2 : scene.ReferenceHeight;
        return (lower + trackedOffset.Y, upper + trackedOffset.Y);
    }
    private IReadOnlyList<MenuStencilSelectorSearchRegion> SearchRegions(Scene scene, BgraFrame image, Offset offset, string? language)
    {
        var regions = new List<MenuStencilSelectorSearchRegion>();
        foreach (var option in scene.Options)
        {
            if (SelectorBounds(scene, option, language) is not { } configured || option.LeftSelector is null || option.RightSelector is null) continue;
            foreach (var (side, bounds, baseBounds) in new[] { (MenuSelectorSide.Left, configured.Left, option.LeftSelector.Bounds), (MenuSelectorSide.Right, configured.Right, option.RightSelector.Bounds) })
            {
                var footprint = new StencilRect(bounds.MidX - baseBounds.Width / 2, bounds.MidY - baseBounds.Height / 2, baseBounds.Width, baseBounds.Height);
                var visual = footprint.Offset(offset.X, offset.Y).Expand(SelectorSearchRadiusX);
                regions.Add(new(option.ClassIdentifier, option.Name, side, OutputRect(visual, image, scene), false));
            }
        }
        return regions;
    }
}
