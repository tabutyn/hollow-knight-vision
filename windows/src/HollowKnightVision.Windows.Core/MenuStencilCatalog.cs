namespace HollowKnightVision.Windows.Core;

public sealed class MenuStencilCatalogUnavailableException() : InvalidOperationException("No usable menu stencil scenes.");

public sealed record MenuStencilLabel(string Identifier, string Name);
public sealed record MenuStencilContext(string Identifier, IReadOnlyList<MenuStencilLabel> Labels);
public sealed record MenuStencilLiveSource(Guid Id, string ContextIdentifier, string SelectedIdentifier,
    string LanguageIdentifier, int ImageWidth, int ImageHeight, string ImagePath);

/// <summary>Mac menu catalog model. All Windows geometry is top-origin reference pixels.</summary>
public sealed record MenuStencilCatalog(IReadOnlyList<MenuStencilCatalog.Scene> Scenes, int ReferenceWidth,
    int ReferenceHeight, int BandStart, int BandEnd, IReadOnlyList<MenuSelectorCorrection> SelectorCorrections,
    IReadOnlyDictionary<string, int>? ScreenMembershipCounts = null)
{
    public sealed record Variant(ImageStencilKernel Kernel, ImageStencilReference Reference,
        string? LanguageIdentifier = null, StencilRect? NominalBounds = null);
    public sealed record Anchor(string ClassIdentifier, string Name, StencilRect Bounds, StencilRect? CalibratedBounds,
        IReadOnlyList<Variant> Variants, Variant Probe, IReadOnlyList<Variant>? ProbeVariants = null)
    {
        public IReadOnlyList<Variant> SearchProbeVariants => ProbeVariants is { Count: > 0 } ? ProbeVariants : [Probe];
    }
    public sealed record PositionedSelector(StencilRect Bounds, IReadOnlyList<Variant> Variants,
        IReadOnlyDictionary<string, StencilRect>? LocalizedBounds = null,
        IReadOnlyDictionary<string, ImageStencilForegroundMask>? IdleForegroundMasks = null)
    {
        public StencilRect BoundsFor(string? language) => language is null ? Bounds : LocalizedBounds?.GetValueOrDefault(language, Bounds) ?? Bounds;
        public ImageStencilForegroundMask? IdleMaskFor(string? language) => language is null ? null : IdleForegroundMasks?.GetValueOrDefault(language);
    }
    public sealed record SelectorGeometry(double LeftWidth, double LeftHeight, double LeftGap, double LeftCenterYOffset,
        double RightWidth, double RightHeight, double RightGap, double RightCenterYOffset);
    public sealed record Option(string ClassIdentifier, string Name, StencilRect Bounds, SelectorGeometry? Geometry,
        PositionedSelector? LeftSelector, PositionedSelector? RightSelector);
    public sealed record Scene(string ContextIdentifier, int ReferenceWidth, int ReferenceHeight, Anchor Probe,
        IReadOnlyList<Anchor> Anchors, IReadOnlyList<Option> Options,
        IReadOnlyDictionary<string, IReadOnlyList<Variant>> SelectorVariants, SelectorGeometry? Geometry);

    public MenuStencilCatalog ContainingOnly(string context) => this with
    {
        Scenes = Scenes.Where(scene => scene.ContextIdentifier == context).ToArray(),
        SelectorCorrections = SelectorCorrections.Where(correction => correction.ContextIdentifier == context).ToArray()
    };

    public static Variant MakeVariant(string kind, ImageStencilReference reference, int sampleStride = 1,
        string? language = null, StencilRect? nominalBounds = null) =>
        new(ImageStencilKernel.FromReference(kind, reference, sampleStride), reference, language, nominalBounds);
}
