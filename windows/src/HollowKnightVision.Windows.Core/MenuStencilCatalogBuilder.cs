using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Reference construction from MenuStencil.swift. File decoding and contract
/// evaluation are injected so the same catalog can be tested without WPF or user data.
/// Supply examples and live captures newest-first, as their stores do on Mac.
/// </summary>
public sealed partial class MenuStencilCatalogBuilder
{
    private const string SelectorId = "shared.select-decoration";
    private readonly IReadOnlyList<MenuStencilContext> contexts;
    private readonly Func<string, BgraFrame?> loadImage;
    private readonly Func<SavedLabelingExample, bool> acceptsMenuExample;
    public MenuStencilCatalogBuilder(IReadOnlyList<MenuStencilContext> contexts, Func<string, BgraFrame?> loadImage,
        Func<SavedLabelingExample, bool> acceptsMenuExample)
    { this.contexts = contexts; this.loadImage = loadImage; this.acceptsMenuExample = acceptsMenuExample; }

    public MenuStencilCatalog Build(IReadOnlyList<SavedLabelingExample> examples, MenuStencilCalibration? calibration,
        IReadOnlyList<MenuStencilLiveSource>? liveCaptures = null, IReadOnlySet<Guid>? positiveCaptureHoldoutIds = null,
        IReadOnlySet<string>? includedContexts = null)
    {
        var corrections = examples.Where(IsSelectorCorrection).ToArray();
        var scenes = new List<Scene>();
        foreach (var context in contexts.Where(item => item.Identifier is not ("game" or "gameplay-hud")
                     && (includedContexts is null || includedContexts.Contains(item.Identifier))))
        {
            var all = examples.Where(example => example.Manifest.ContextIdentifier == context.Identifier && !IsSelectorCorrection(example)).ToArray();
            var accepted = all.Where(example => context.Identifier == "inventory"
                ? example.Manifest.Annotations.Any(annotation => !annotation.IsHardNegative && Canonical(annotation.ClassIdentifier) == "inventory.inventory")
                : acceptsMenuExample(example)).ToArray();
            var ids = accepted.Select(example => example.Id).ToHashSet();
            var live = calibration is null ? [] : (liveCaptures ?? []).Where(item => item.ContextIdentifier == context.Identifier).ToArray();
            var scene = MakeScene(context, accepted, all.Where(example => !ids.Contains(example.Id)).ToArray(),
                calibration?.FindScene(context.Identifier), live, positiveCaptureHoldoutIds ?? new HashSet<Guid>());
            if (scene is not null) scenes.Add(scene);
        }
        if (scenes.Count == 0) throw new MenuStencilCatalogUnavailableException();
        var width = scenes[0].ReferenceWidth; var height = scenes[0].ReferenceHeight;
        var bounds = scenes.SelectMany(scene => scene.Anchors).Select(anchor => anchor.Bounds).ToArray();
        var low = Math.Max(0, (int)Math.Floor(bounds.Min(rect => rect.Y)) - 14);
        var high = Math.Min(height, (int)Math.Ceiling(bounds.Max(rect => rect.Bottom)) + 14);
        var corrected = ApplyCorrections(corrections, scenes.OrderBy(scene => scene.ContextIdentifier, StringComparer.Ordinal).ToArray(), width, height);
        var memberships = contexts.SelectMany(context => context.Labels.Select(label => Canonical(label.Identifier)).Distinct(StringComparer.Ordinal))
            .GroupBy(identifier => identifier, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new(corrected, width, height, low, Math.Max(low + 1, high), CorrectionPlacements(corrections, corrected), memberships);
    }

    private Scene? MakeScene(MenuStencilContext context, IReadOnlyList<SavedLabelingExample> examples,
        IReadOnlyList<SavedLabelingExample> supplemental, MenuStencilCalibration.Scene? calibration,
        IReadOnlyList<MenuStencilLiveSource> liveCaptures, IReadOnlySet<Guid> holdouts)
    {
        if (examples.Count == 0) return null;
        var width = Mode(examples.Select(example => example.Manifest.ImageWidth));
        var height = Mode(examples.Select(example => example.Manifest.ImageHeight));
        if (width <= 0 || height <= 0) return null;
        var compatible = examples.Where(example => example.Manifest.ImageWidth == width && example.Manifest.ImageHeight == height).ToArray();
        var extra = supplemental.Where(example => example.Manifest.ImageWidth == width && example.Manifest.ImageHeight == height).ToArray();
        if (compatible.Length == 0) return null;
        var liveBySelection = new Dictionary<string, Dictionary<string, List<ImageStencilPixels>>>(StringComparer.Ordinal);
        var allLiveBySelection = new Dictionary<string, Dictionary<string, List<ImageStencilPixels>>>(StringComparer.Ordinal);
        var liveByLanguage = new Dictionary<string, List<ImageStencilPixels>>(StringComparer.Ordinal);
        static List<ImageStencilPixels> SelectionFrames(Dictionary<string, Dictionary<string, List<ImageStencilPixels>>> source, string language, string selected)
        {
            if (!source.TryGetValue(language, out var selections)) source[language] = selections = new(StringComparer.Ordinal);
            if (!selections.TryGetValue(selected, out var frames)) selections[selected] = frames = [];
            return frames;
        }
        foreach (var source in liveCaptures.Where(item => item.ImageWidth == width && item.ImageHeight == height))
        {
            var positive = !holdouts.Contains(source.Id);
            var allCount = allLiveBySelection.GetValueOrDefault(source.LanguageIdentifier)?.GetValueOrDefault(source.SelectedIdentifier)?.Count ?? 0;
            var selectionCount = liveBySelection.GetValueOrDefault(source.LanguageIdentifier)?.GetValueOrDefault(source.SelectedIdentifier)?.Count ?? 0;
            var languageCount = liveByLanguage.GetValueOrDefault(source.LanguageIdentifier)?.Count ?? 0;
            if (allCount >= 8 && (!positive || languageCount >= 8 && selectionCount >= 8)) continue;
            if (LoadPixels(source.ImagePath, width, height) is not { } pixels) continue;
            if (allCount < 8) SelectionFrames(allLiveBySelection, source.LanguageIdentifier, source.SelectedIdentifier).Add(pixels);
            if (positive && selectionCount < 8) SelectionFrames(liveBySelection, source.LanguageIdentifier, source.SelectedIdentifier).Add(pixels);
            if (positive && languageCount < 8)
            {
                if (!liveByLanguage.TryGetValue(source.LanguageIdentifier, out var languageFrames)) liveByLanguage[source.LanguageIdentifier] = languageFrames = [];
                languageFrames.Add(pixels);
            }
        }
        var identifiers = compatible.SelectMany(example => example.Manifest.Annotations).Where(annotation => !annotation.IsHardNegative)
            .Select(annotation => Canonical(annotation.ClassIdentifier)).Distinct(StringComparer.Ordinal).Where(id => id != SelectorId);
        var medianBounds = new Dictionary<string, StencilRect>(StringComparer.Ordinal);
        foreach (var identifier in identifiers)
        {
            var perExample = compatible.Select(example => Annotation(identifier, example)).Where(annotation => annotation is not null)
                .Select(annotation => PixelBounds(annotation!, width, height)).ToArray();
            if (perExample.Length == compatible.Length) medianBounds[identifier] = MedianRect(perExample);
        }
        var definitions = context.Labels.ToDictionary(label => Canonical(label.Identifier), label => label.Name, StringComparer.Ordinal);
        var usable = medianBounds.Keys.Where(id => medianBounds[id].Width * medianBounds[id].Height <= 5000
            && !new[] { ".poster", ".diagram", ".screen-corner", ".mod-decoration", ".hollow-knight-logo" }.Any(part => id.Contains(part, StringComparison.Ordinal))).ToArray();
        if (usable.Length == 0) return null;
        var selectedIds = compatible.ToDictionary(example => example.Id, example => SelectedIdentifier(example, width, height));
        var definitionOrder = context.Labels.Select(label => Canonical(label.Identifier)).ToList();
        var probeCandidates = usable.Where(id => medianBounds[id].Width * medianBounds[id].Height >= 150).ToArray();
        var probeIdentifier = context.Identifier == "main-title" && usable.Contains("main-title.start-game") ? "main-title.start-game"
            : (probeCandidates.Length == 0 ? usable : probeCandidates).OrderBy(ScreenMembershipCount)
                .ThenBy(id => definitionOrder.Contains(id) ? definitionOrder.IndexOf(id) : int.MaxValue).First();
        var languageCounts = compatible.GroupBy(example => example.Manifest.ResolvedLanguageIdentifier).ToDictionary(group => group.Key, group => group.Count());
        var baseLanguage = languageCounts.ContainsKey("en") ? "en" : languageCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;
        var ranked = compatible.Where(example => example.Manifest.ResolvedLanguageIdentifier == baseLanguage)
            .OrderByDescending(example => selectedIds[example.Id] != probeIdentifier).ThenByDescending(example => example.Manifest.Annotations.Count)
            .ThenByDescending(example => example.Manifest.CreatedAt).ToArray();
        var probeBounds = medianBounds[probeIdentifier];
        var geometricallyCompatible = ranked.Where(example => Annotation(probeIdentifier, example) is { } annotation
            && Compatible(PixelBounds(annotation, width, height), probeBounds, 12, 12, .85, 1.15, .75, 1.25)).ToArray();
        var sources = new List<SavedLabelingExample>(); var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var example in geometricallyCompatible)
        {
            if (covered.Add(selectedIds[example.Id] ?? "none")) sources.Add(example);
            if (sources.Count == 8) break;
        }
        foreach (var example in geometricallyCompatible.Where(example => !sources.Any(item => item.Id == example.Id)))
        { if (sources.Count == 8) break; sources.Add(example); }
        if (sources.Count == 0) sources.Add(ranked[0]);
        Dictionary<Guid, ImageStencilPixels> LoadExamples(IEnumerable<SavedLabelingExample> input)
        {
            var result = new Dictionary<Guid, ImageStencilPixels>();
            foreach (var example in input) if (LoadPixels(example.ImagePath, width, height) is { } pixels) result[example.Id] = pixels;
            return result;
        }
        var sourcePixels = LoadExamples(sources); var extraPixels = LoadExamples(extra);
        var allPixels = new Dictionary<Guid, ImageStencilPixels>(sourcePixels);
        foreach (var item in LoadExamples(compatible.Where(example => !allPixels.ContainsKey(example.Id)))) allPixels[item.Key] = item.Value;
        if (!sourcePixels.ContainsKey(sources[0].Id)) return null;
        var anchors = new List<Anchor>();
        foreach (var identifier in usable.Order(StringComparer.Ordinal))
        {
            var bounds = medianBounds[identifier]; var consensusBounds = ExpandedBounds(bounds, context.Identifier, identifier);
            MenuStencilConsensus.Capture[] Captures(IEnumerable<SavedLabelingExample> input, IReadOnlyDictionary<Guid, ImageStencilPixels> pixels) =>
                input.Where(example => pixels.ContainsKey(example.Id) && Annotation(identifier, example) is not null)
                    .Select(example => new MenuStencilConsensus.Capture(pixels[example.Id], ExpandedBounds(PixelBounds(Annotation(identifier, example)!, width, height), context.Identifier, identifier))).ToArray();
            var human = MenuStencilConsensus.Build(Captures(sources, sourcePixels), consensusBounds, preservesAnnotationBounds: true);
            if (human is null) continue;
            var focused = new List<Variant>(); ImageStencilReference? focusedProbe = null; StencilRect? latestHumanBounds = null;
            foreach (var example in extra)
            {
                if (focused.Count >= 8 || !extraPixels.TryGetValue(example.Id, out var pixels) || Annotation(identifier, example) is not { } annotation) continue;
                var annotationBounds = ExpandedBounds(PixelBounds(annotation, width, height), context.Identifier, identifier);
                if (!Compatible(annotationBounds, bounds, 24, 16, .65, 1.35, .60, 1.60)) continue;
                var individual = MenuStencilConsensus.Build([new(pixels, annotationBounds)], annotationBounds, alignmentRadius: 0, preservesAnnotationBounds: true);
                if (individual is null) continue;
                if (latestHumanBounds is null) { latestHumanBounds = annotationBounds; focusedProbe = individual.Reference; }
                focused.Add(MakeVariant($"{identifier}.human-box.{example.Id:D}", individual.Reference, language: example.Manifest.ResolvedLanguageIdentifier));
            }
            var anchorBounds = latestHumanBounds ?? human.Bounds;
            var calibratedBounds = calibration?.Anchors.FirstOrDefault(anchor => anchor.ClassIdentifier == identifier && anchor.EvidenceCount > 0)?.Rect.Bounds;
            var liveConsensus = new Dictionary<string, MenuStencilConsensus.Result>(StringComparer.Ordinal);
            foreach (var language in liveByLanguage.Keys.Order(StringComparer.Ordinal))
            {
                var result = MenuStencilConsensus.BuildLocalizedText(liveByLanguage[language].Take(8).Select(pixels => new MenuStencilConsensus.Capture(pixels, anchorBounds)).ToArray(), anchorBounds);
                if (result is not null) liveConsensus[language] = result;
            }
            var variants = new List<Variant> { MakeVariant(identifier, human.Reference, language: baseLanguage) }; variants.AddRange(focused);
            var humanByLanguage = new Dictionary<string, MenuStencilConsensus.Result>(StringComparer.Ordinal);
            foreach (var language in languageCounts.Keys.Where(language => language != baseLanguage).Order(StringComparer.Ordinal))
            {
                var result = MenuStencilConsensus.BuildLocalizedText(Captures(compatible.Where(example => example.Manifest.ResolvedLanguageIdentifier == language).Take(8), allPixels), bounds);
                if (result is null) continue;
                humanByLanguage[language] = result;
                variants.Add(MakeVariant($"{identifier}.human.{language}", result.Reference, language: language, nominalBounds: result.Bounds));
            }
            foreach (var language in liveConsensus.Keys.Order(StringComparer.Ordinal))
            {
                var live = liveConsensus[language];
                if (language == "en" && SameReference(live.Reference, human.Reference)) continue;
                variants.Add(MakeVariant($"{identifier}.live.{language}", live.Reference, language: language, nominalBounds: live.Bounds));
            }
            var probeReference = context.Identifier is "quit-game" or "quit-to-menu"
                ? liveConsensus.GetValueOrDefault(baseLanguage)?.Reference ?? human.Reference : focusedProbe ?? human.Reference;
            var probe = MakeVariant(identifier, probeReference, 2, baseLanguage);
            var probes = new List<Variant> { probe };
            foreach (var language in liveConsensus.Keys.Union(humanByLanguage.Keys).Where(language => language != baseLanguage).Order(StringComparer.Ordinal))
            {
                var consensus = liveConsensus.GetValueOrDefault(language) ?? humanByLanguage[language];
                probes.Add(MakeVariant($"{identifier}.probe.{language}", consensus.Reference, 2, language, consensus.Bounds));
            }
            anchors.Add(new(identifier, definitions.GetValueOrDefault(identifier, identifier), anchorBounds, calibratedBounds, variants, probe, probes));
        }
        var sceneProbe = anchors.FirstOrDefault(anchor => anchor.ClassIdentifier == probeIdentifier);
        if (sceneProbe is null) return null;
        var verifiedBounds = anchors.ToDictionary(anchor => anchor.ClassIdentifier, anchor => anchor.Bounds, StringComparer.Ordinal);
        var geometry = Geometry(compatible, width, height, null, verifiedBounds);
        var selectorVariants = new Dictionary<string, IReadOnlyList<Variant>>(StringComparer.Ordinal);
        foreach (var side in new[] { "left", "right" })
        {
            var variants = new List<Variant>();
            foreach (var example in sources)
            {
                var selectors = Selectors(example).OrderBy(annotation => annotation.X).ToArray();
                if (selectors.Length == 0 || !sourcePixels.TryGetValue(example.Id, out var pixels)) continue;
                var annotation = side == "left" ? selectors[0] : selectors[^1];
                if (pixels.Reference(PixelBounds(annotation, width, height)) is { } reference)
                    variants.Add(MakeVariant(side, reference, 2, example.Manifest.ResolvedLanguageIdentifier));
            }
            selectorVariants[side] = variants;
        }
        var options = BuildOptions(context, compatible, sources, width, height, calibration, anchors, verifiedBounds, geometry,
            selectorVariants, selectedIds, allPixels, liveBySelection, allLiveBySelection);
        return new(context.Identifier, width, height, sceneProbe, anchors, options, selectorVariants, geometry);
    }

    public int ScreenMembershipCount(string identifier) => contexts.Count(context => context.Labels.Any(label => Canonical(label.Identifier) == Canonical(identifier)));
    public static bool IsSelectorCorrection(SavedLabelingExample example)
    { var positive = example.Manifest.Annotations.Where(annotation => !annotation.IsHardNegative).ToArray(); return positive.Length is >= 1 and <= 2 && positive.All(annotation => Canonical(annotation.ClassIdentifier) == SelectorId); }
    private ImageStencilPixels? LoadPixels(string path, int width, int height)
    {
        try { return loadImage(path) is { } image ? new(image, width, height) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException) { return null; }
    }
    private static string Canonical(string identifier) => LabelingClassIdentity.CanonicalIdentifier(identifier);
    private static LabelingExampleAnnotation[] Selectors(SavedLabelingExample example) => example.Manifest.Annotations.Where(annotation => !annotation.IsHardNegative && Canonical(annotation.ClassIdentifier) == SelectorId).ToArray();
    private static LabelingExampleAnnotation? Annotation(string identifier, SavedLabelingExample example)
    { var matches = example.Manifest.Annotations.Where(annotation => !annotation.IsHardNegative && Canonical(annotation.ClassIdentifier) == identifier).Take(2).ToArray(); return matches.Length == 1 ? matches[0] : null; }
    private static StencilRect PixelBounds(LabelingExampleAnnotation annotation, int width, int height) => new(annotation.X * width, annotation.Y * height, annotation.Width * width, annotation.Height * height);
    private static StencilRect ExpandedBounds(StencilRect bounds, string context, string identifier) => context == "quit-game" && identifier == "quit-game.quit-game" ? bounds with { Width = bounds.Width + 7 } : bounds;
    private static bool Compatible(StencilRect bounds, StencilRect nominal, double dx, double dy, double minW, double maxW, double minH, double maxH) =>
        Math.Abs(bounds.MidX - nominal.MidX) <= dx && Math.Abs(bounds.MidY - nominal.MidY) <= dy && bounds.Width >= nominal.Width * minW && bounds.Width <= nominal.Width * maxW && bounds.Height >= nominal.Height * minH && bounds.Height <= nominal.Height * maxH;
    private static int Mode(IEnumerable<int> values) => values.GroupBy(value => value).OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First().Key;
    private static double Median(IEnumerable<double> values)
    { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2]; }
    private static StencilRect MedianRect(IEnumerable<StencilRect> source)
    { var rects = source.ToArray(); return new(Median(rects.Select(rect => rect.X)), Median(rects.Select(rect => rect.Y)), Median(rects.Select(rect => rect.Width)), Median(rects.Select(rect => rect.Height))); }
    private static bool SameReference(ImageStencilReference a, ImageStencilReference b) => a.Width == b.Width && a.Height == b.Height && a.Rgb.SequenceEqual(b.Rgb)
        && (a.Mask is null && b.Mask is null || a.Mask is not null && b.Mask is not null && a.Mask.SequenceEqual(b.Mask));

    public static string? SelectedIdentifier(SavedLabelingExample example, int width, int height)
    {
        var selectors = Selectors(example).Select(annotation => PixelBounds(annotation, width, height)).OrderBy(rect => rect.MidX).ToArray();
        if (selectors.Length < 2) return null;
        var centerY = selectors.Average(rect => rect.MidY);
        return example.Manifest.Annotations.Where(annotation => !annotation.IsHardNegative && Canonical(annotation.ClassIdentifier) != SelectorId)
            .Select(annotation => (Id: Canonical(annotation.ClassIdentifier), Bounds: PixelBounds(annotation, width, height)))
            .Where(item => item.Bounds.Width * item.Bounds.Height <= 5000 && item.Bounds.X > selectors[0].MidX && item.Bounds.Right < selectors[^1].MidX)
            .OrderBy(item => Math.Abs(item.Bounds.MidY - centerY)).Select(item => item.Id).FirstOrDefault();
    }
    private static SelectorGeometry? Geometry(IEnumerable<SavedLabelingExample> examples, int width, int height, string? required,
        IReadOnlyDictionary<string, StencilRect> anchorBounds)
    {
        var geometries = new List<SelectorGeometry>();
        foreach (var example in examples)
        {
            var selectors = Selectors(example).OrderBy(annotation => annotation.X).ToArray();
            var selected = SelectedIdentifier(example, width, height);
            if (selectors.Length < 2 || selected is null || required is not null && selected != required || Annotation(selected, example) is not { } annotation) continue;
            var anchor = anchorBounds.GetValueOrDefault(selected, PixelBounds(annotation, width, height));
            var left = PixelBounds(selectors[0], width, height); var right = PixelBounds(selectors[^1], width, height);
            geometries.Add(new(left.Width, left.Height, anchor.X - left.Right, left.MidY - anchor.MidY,
                right.Width, right.Height, right.X - anchor.Right, right.MidY - anchor.MidY));
        }
        return geometries.Count == 0 ? null : new(Median(geometries.Select(item => item.LeftWidth)), Median(geometries.Select(item => item.LeftHeight)),
            Median(geometries.Select(item => item.LeftGap)), Median(geometries.Select(item => item.LeftCenterYOffset)),
            Median(geometries.Select(item => item.RightWidth)), Median(geometries.Select(item => item.RightHeight)),
            Median(geometries.Select(item => item.RightGap)), Median(geometries.Select(item => item.RightCenterYOffset)));
    }
}
