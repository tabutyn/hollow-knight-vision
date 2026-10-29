using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

public sealed partial class MenuStencilCatalogBuilder
{
    private static IReadOnlyList<Option> BuildOptions(MenuStencilContext context, IReadOnlyList<SavedLabelingExample> compatible,
        IReadOnlyList<SavedLabelingExample> sources, int width, int height, MenuStencilCalibration.Scene? calibration,
        IReadOnlyList<Anchor> anchors, IReadOnlyDictionary<string, StencilRect> verifiedBounds, SelectorGeometry? globalGeometry,
        IReadOnlyDictionary<string, IReadOnlyList<Variant>> selectorVariants, IReadOnlyDictionary<Guid, string?> selectedIds,
        IReadOnlyDictionary<Guid, ImageStencilPixels> allPixels,
        IReadOnlyDictionary<string, Dictionary<string, List<ImageStencilPixels>>> liveBySelection,
        IReadOnlyDictionary<string, Dictionary<string, List<ImageStencilPixels>>> allLiveBySelection)
    {
        // Clear Save moves with the profile slot. Persisted absolute positions
        // may belong to another slot; derive this modal from its labeled reference.
        // The tracker then translates the whole reference to the detected row.
        if (context.Identifier == "clear-save") calibration = null;
        IReadOnlyList<Variant> Variants(string side) => selectorVariants.GetValueOrDefault(side) ?? [];
        ImageStencilPixels[] NegativeFrames(string identifier, string language) => allLiveBySelection.TryGetValue(language, out var selections)
            ? selections.Where(pair => pair.Key != identifier).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.FirstOrDefault()).Where(pixels => pixels is not null).Cast<ImageStencilPixels>().Take(24).ToArray() : [];
        Dictionary<string, ImageStencilForegroundMask> IdleMasks(string identifier, StencilRect baseBounds, IReadOnlyDictionary<string, StencilRect>? localized = null)
        {
            var masks = new Dictionary<string, ImageStencilForegroundMask>(StringComparer.Ordinal);
            foreach (var language in allLiveBySelection.Keys)
            {
                var bounds = localized?.GetValueOrDefault(language, baseBounds) ?? baseBounds;
                if (ImageStencilForegroundMask.Create(NegativeFrames(identifier, language), Expand(bounds, 48, 16)) is { } mask) masks[language] = mask;
            }
            return masks;
        }
        StencilRect? LocalizedBounds(ImageStencilPixels pixels, StencilRect nominal, StencilRect optionBounds, string side, int radiusX, ImageStencilForegroundMask? idleMask)
        {
            (double Confidence, int Foreground, int Distance, StencilRect Rect)? best = null;
            foreach (var variant in Variants(side).Take(8)) for (var y = -12; y <= 12; y++) for (var x = -radiusX; x <= radiusX; x++)
            {
                var candidate = nominal.Offset(x, y);
                if (side == "left" && candidate.MidX >= optionBounds.MidX || side == "right" && candidate.MidX <= optionBounds.MidX) continue;
                if (context.Identifier == "controller" && (candidate.MidX < 175 || candidate.MidX > 465)) continue;
                if (pixels.Compare(variant.Kernel, candidate) is not { } comparison) continue;
                var confidence = comparison.Confidence(.82, 105);
                var foreground = idleMask is null ? 0 : pixels.LikelyMenuTextPixelCount(candidate, idleMask);
                var distance = Math.Abs(x) + Math.Abs(y);
                var novel = foreground >= 8; var bestNovel = (best?.Foreground ?? 0) >= 8;
                if (novel != bestNovel ? novel : confidence > (best?.Confidence ?? double.NegativeInfinity)
                    || confidence == (best?.Confidence ?? double.NegativeInfinity) && distance < (best?.Distance ?? int.MaxValue))
                    best = (confidence, foreground, distance, candidate);
            }
            return best is { } result && (result.Confidence >= .45 || result.Foreground >= 24 && result.Confidence >= .25) ? result.Rect : null;
        }
        PositionedSelector? Positioned(string identifier, string side, StencilRect? fallback = null)
        {
            var captures = new List<(MenuStencilConsensus.Capture Capture, string? Language)>();
            foreach (var example in compatible)
            {
                if (selectedIds[example.Id] != identifier || !allPixels.TryGetValue(example.Id, out var pixels)) continue;
                var annotations = Selectors(example).OrderBy(annotation => annotation.X).ToArray();
                if (annotations.Length == 0) continue;
                var annotation = side == "left" ? annotations[0] : annotations[^1];
                captures.Add((new(pixels, PixelBounds(annotation, width, height)), example.Manifest.ResolvedLanguageIdentifier));
            }
            var calibrated = calibration?.Selectors.FirstOrDefault(selector => selector.SelectedIdentifier == identifier);
            StencilRect? measured = calibrated is null ? null : side == "left" ? calibrated.LeftRect.Bounds : calibrated.RightRect.Bounds;
            var localized = new Dictionary<string, StencilRect>(StringComparer.Ordinal);
            if ((measured ?? fallback) is { } liveBounds)
            {
                foreach (var language in allLiveBySelection.Keys.OrderDescending(StringComparer.Ordinal))
                {
                    var geometryFrames = allLiveBySelection[language].GetValueOrDefault(identifier) ?? [];
                    var positiveFrames = liveBySelection.GetValueOrDefault(language)?.GetValueOrDefault(identifier) ?? [];
                    var radiusX = language == "en" ? 12 : 120;
                    var idleMask = context.Identifier == "controller"
                        ? ImageStencilForegroundMask.Create(NegativeFrames(identifier, language), Expand(liveBounds, radiusX + 8, 16)) : null;
                    var optionBounds = verifiedBounds.GetValueOrDefault(identifier, new(width / 2.0, liveBounds.Y, 1, liveBounds.Height));
                    var aligned = geometryFrames.Select(pixels => LocalizedBounds(pixels, liveBounds, optionBounds, side, radiusX, idleMask))
                        .Where(bounds => bounds.HasValue).Select(bounds => bounds!.Value).ToArray();
                    var languageBounds = aligned.Length == 0 ? liveBounds : MedianRect(aligned);
                    localized[language] = languageBounds;
                    foreach (var pixels in positiveFrames.AsEnumerable().Reverse()) captures.Insert(0, (new(pixels, languageBounds), language));
                }
            }
            if (calibrated?.LocalizedPlacements is { } placements)
                foreach (var pair in placements) localized[pair.Key] = side == "left" ? pair.Value.LeftRect.Bounds : pair.Value.RightRect.Bounds;
            if (captures.Count == 0) return null;
            var consensus = MenuStencilConsensus.Build(captures.Select(source => source.Capture).ToArray(), MedianRect(captures.Select(source => source.Capture.AnnotationBounds)), alignmentRadius: 8);
            if (consensus is null) return null;
            var variants = new List<Variant>();
            for (var index = 0; index < captures.Count; index++)
            {
                var source = captures[index];
                var individual = MenuStencilConsensus.Build([source.Capture], source.Capture.AnnotationBounds, alignmentRadius: 0);
                if (individual is null) continue;
                var negatives = source.Language is { } language ? NegativeFrames(identifier, language)
                    : sources.Where(example => selectedIds[example.Id] != identifier && allPixels.ContainsKey(example.Id)).Select(example => allPixels[example.Id]).Take(24).ToArray();
                var reference = negatives.Length >= 2 ? MenuStencilConsensus.RemovingStableBackground(individual.Reference, individual.Bounds, negatives) : individual.Reference;
                if (reference is not null) variants.Add(MakeVariant($"{identifier}.selector.{side}.{index}", reference, language: source.Language));
            }
            var consensusNegatives = allLiveBySelection.Values.SelectMany(selections => selections.Where(pair => pair.Key != identifier))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.FirstOrDefault()).Where(pixels => pixels is not null).Cast<ImageStencilPixels>().Take(24).ToArray();
            var consensusReference = MenuStencilConsensus.RemovingStableBackground(consensus.Reference, consensus.Bounds, consensusNegatives);
            if (consensusReference is not null) variants.Add(MakeVariant($"{identifier}.selector.{side}.consensus", consensusReference));
            else if (variants.Count == 0 && consensusNegatives.Length < 2) variants.Add(MakeVariant($"{identifier}.selector.{side}.legacy", consensus.Reference));
            if (variants.Count == 0) variants.AddRange(Variants(side));
            return variants.Count == 0 ? null : new(consensus.Bounds, variants, localized, IdleMasks(identifier, measured ?? fallback ?? consensus.Bounds, localized));
        }
        PositionedSelector Calibrated(string identifier, string side, StencilRect bounds, PositionedSelector? captured)
        {
            var masks = IdleMasks(identifier, bounds, captured?.LocalizedBounds);
            foreach (var pair in captured?.IdleForegroundMasks ?? new Dictionary<string, ImageStencilForegroundMask>()) masks[pair.Key] = pair.Value;
            return new(bounds, captured?.Variants ?? Variants(side), captured?.LocalizedBounds, masks);
        }
        var options = new List<Option>();
        var firstDefinition = context.Labels.FirstOrDefault()?.Identifier;
        foreach (var anchor in anchors.Where(anchor => IsSelectable(anchor, context.Identifier, firstDefinition is null ? null : Canonical(firstDefinition))))
        {
            var calibrated = calibration?.Selectors.FirstOrDefault(selector => selector.SelectedIdentifier == anchor.ClassIdentifier);
            var capturedLeft = Positioned(anchor.ClassIdentifier, "left"); var capturedRight = Positioned(anchor.ClassIdentifier, "right");
            var geometry = Geometry(compatible, width, height, anchor.ClassIdentifier, verifiedBounds) ?? globalGeometry;
            PositionedSelector? Inferred(string side)
            {
                if (geometry is null || Variants(side).Count == 0) return null;
                var bounds = side == "left" ? new StencilRect(anchor.Bounds.X - geometry.LeftGap - geometry.LeftWidth,
                    anchor.Bounds.MidY + geometry.LeftCenterYOffset - geometry.LeftHeight / 2, geometry.LeftWidth, geometry.LeftHeight)
                    : new(anchor.Bounds.Right + geometry.RightGap, anchor.Bounds.MidY + geometry.RightCenterYOffset - geometry.RightHeight / 2, geometry.RightWidth, geometry.RightHeight);
                return new(bounds, Variants(side), IdleForegroundMasks: IdleMasks(anchor.ClassIdentifier, bounds));
            }
            options.Add(new(anchor.ClassIdentifier, anchor.Name, anchor.Bounds, geometry,
                calibrated is not null ? Calibrated(anchor.ClassIdentifier, "left", calibrated.LeftRect.Bounds, capturedLeft) : capturedLeft ?? Inferred("left"),
                calibrated is not null ? Calibrated(anchor.ClassIdentifier, "right", calibrated.RightRect.Bounds, capturedRight) : capturedRight ?? Inferred("right")));
        }
        if (context.Identifier == "select-profile")
        {
            var clearSaveBounds = compatible.SelectMany(example => example.Manifest.Annotations)
                .Where(annotation => !annotation.IsHardNegative && Canonical(annotation.ClassIdentifier) == "select-profile.clear-save")
                .Select(annotation => PixelBounds(annotation, width, height)).ToArray();
            var firstSlot = options.FirstOrDefault(option => option.ClassIdentifier == "select-profile.slot-1");
            if (clearSaveBounds.Length != 0 && firstSlot?.LeftSelector is { } firstLeft && firstSlot.RightSelector is { } firstRight)
            {
                var firstCenter = (firstLeft.Bounds.MidY + firstRight.Bounds.MidY) / 2;
                // Normalize each labeled Clear Save to slot one before combining
                // captures. Users may label the action beside any occupied slot.
                var slotCenters = options.Where(option => option.ClassIdentifier.StartsWith("select-profile.slot-", StringComparison.Ordinal))
                    .Where(option => option.LeftSelector is not null && option.RightSelector is not null)
                    .Select(option => (option.LeftSelector!.Bounds.MidY + option.RightSelector!.Bounds.MidY) / 2).ToArray();
                var label = MedianRect(clearSaveBounds.Select(bounds =>
                    bounds.Offset(0, firstCenter - slotCenters.MinBy(center => Math.Abs(center - bounds.MidY)))));
                for (var index = 1; index <= 4; index++)
                {
                    var slot = options.FirstOrDefault(option => option.ClassIdentifier == $"select-profile.slot-{index}");
                    if (slot?.LeftSelector is not { } left || slot.RightSelector is not { } right) continue;
                    var dy = (left.Bounds.MidY + right.Bounds.MidY) / 2 - firstCenter;
                    var id = $"select-profile.clear-save-{index}";
                    var calibrated = calibration?.Selectors.FirstOrDefault(selector => selector.SelectedIdentifier == id);
                    var leftBounds = calibrated?.LeftRect.Bounds ?? new(label.X - 24, label.Y + dy, 15, 12);
                    var rightBounds = calibrated?.RightRect.Bounds ?? new(label.Right + 11, label.Y + dy, 15, 12);
                    options.Add(new(id, $"Clear Save {index}", label.Offset(0, dy), null,
                        Calibrated(id, "left", leftBounds, Positioned(id, "left", leftBounds)),
                        Calibrated(id, "right", rightBounds, Positioned(id, "right", rightBounds))));
                }
            }
        }
        return options;
    }

    private static StencilRect Expand(StencilRect bounds, double x, double y) => new(bounds.X - x, bounds.Y - y, bounds.Width + 2 * x, bounds.Height + 2 * y);
    private static bool IsSelectable(Anchor anchor, string context, string? firstDefinition)
    {
        var id = anchor.ClassIdentifier;
        // The shared Clear Save label expands into one option per occupied-row
        // location below. A second generic option would tie its own selector pair.
        if (context == "select-profile" && id == "select-profile.clear-save") return false;
        if (id.Contains(".header", StringComparison.Ordinal) || id.Contains(".heading", StringComparison.Ordinal) || id.EndsWith(".pointer", StringComparison.Ordinal) || id == "controller-advanced.mfi") return false;
        if (context is "quit-to-menu" or "quit-game") return id != firstDefinition;
        return context == "main-title" || anchor.Bounds.Y >= 80 || id != firstDefinition;
    }
}
