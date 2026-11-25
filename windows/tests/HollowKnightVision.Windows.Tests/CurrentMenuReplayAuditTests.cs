using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class CurrentMenuReplayAuditTests
{
    internal static void TestCurrentUserPlainMenuRowsWhenRequested()
    {
        var root = Environment.GetEnvironmentVariable("HKV_CURRENT_STENCIL_AUDIT");
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Set the current stencil examples directory.");
        var examples = new LabelingExampleStore(root).Load();
        var contexts = new HashSet<string>(StringComparer.Ordinal) { "main-title", "extras", "inventory" };
        var calibration = MenuStencilCalibration.LoadDefault(MenuStencilCalibration.WritablePath(root));
        var catalog = new MenuStencilCatalogBuilder(LabelingContractCatalog.ScreenContexts, path => BgraPngReader.Read(path),
            example => LabelingContractEvaluator.AcceptsMenuExample(example))
            .Build(examples, calibration, includedContexts: contexts);
        Check(contexts.SetEquals(catalog.Scenes.Select(scene => scene.ContextIdentifier)), "All three captures must have catalog scenes.");

        var checkedRegions = 0;
        foreach (var context in new[] { "main-title", "extras" })
        {
            var example = Reference(context);
            var image = BgraPngReader.Read(example.ImagePath);
            var tracker = new MenuStencilTracker(catalog.ContainingOnly(context));
            for (var index = 0; index < 12; index++)
            {
                var result = tracker.Observe(image, 1 + index * .25);
                if (result is not { IsMatch: true } || result.ContextIdentifier != context)
                    throw new InvalidOperationException($"{context} frame {index} was not recognized.");
                Check(context != "extras" || result.SelectedOption == "Menu Style",
                    $"The Extras capture selects Menu Style. Frame {index} reported {result.SelectedOption ?? "no selection"}.");
                var regions = result.SelectorSearchRegions
                    .Where(region => MenuSelectorPlacement.AlignsWithText(context, region.OptionIdentifier)).ToArray();
                Check(regions.Length > 0, $"{context} did not produce text-aligned selector regions.");
                foreach (var region in regions)
                {
                    var anchor = result.Anchors.Single(match => match.ClassIdentifier == region.OptionIdentifier);
                    var text = anchor.Reference?.VerifiedBounds(anchor.Rect) ?? anchor.Rect;
                    Check(Math.Abs(region.Rect.MidY - text.MidY) <= 1
                        && (region.Side == MenuSelectorSide.Left ? region.Rect.MidX < text.X : region.Rect.MidX > text.Right),
                        $"{context}/{region.OptionName} frame {index} selector is outside its text row.");
                    checkedRegions++;
                }
            }
        }

        var inventory = Reference("inventory");
        var source = BgraPngReader.Read(inventory.ImagePath);
        var inventoryCatalog = catalog.ContainingOnly("inventory");
        Check(new MenuStencilTracker(inventoryCatalog).Observe(source, 1) is { IsMatch: true },
            "The complete Inventory reference should establish the scene.");
        var heading = inventory.Manifest.Annotations.Single(label => !label.IsHardNegative
            && label.ClassIdentifier == "inventory.inventory");
        var onlyHeading = CropOverBlack(source, heading);
        Check(new MenuStencilTracker(inventoryCatalog).Observe(onlyHeading, 1)?.IsMatch != true,
            "Inventory must require the corners as well as its heading.");
        Console.WriteLine($"Current menu replay checked 24 menu frames, {checkedRegions} selector regions, and the Inventory corner requirement.");

        SavedLabelingExample Reference(string context) =>
            StencilInitializationPolicy.RepresentativeExample(examples, context, "en", GameRuntimeMode.Modded)
            ?? throw new InvalidDataException($"The current English {context} reference is missing.");
    }

    private static BgraFrame CropOverBlack(BgraFrame source, LabelingExampleAnnotation label)
    {
        var pixels = new byte[source.Pixels.Length];
        const int bpp = BgraFrame.BytesPerPixel;
        for (var index = bpp - 1; index < pixels.Length; index += bpp) pixels[index] = 255;
        var left = (int)Math.Floor(label.X * source.Width);
        var right = Math.Min(source.Width, (int)Math.Ceiling((label.X + label.Width) * source.Width));
        var top = (int)Math.Floor(label.Y * source.Height);
        var bottom = Math.Min(source.Height, (int)Math.Ceiling((label.Y + label.Height) * source.Height));
        for (var y = top; y < bottom; y++)
        {
            var offset = y * source.Stride + left * bpp;
            source.Pixels.AsSpan(offset, (right - left) * bpp).CopyTo(pixels.AsSpan(offset));
        }
        return new(source.Width, source.Height, source.Stride, pixels, source.CaptureId, source.ObservedAt);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
