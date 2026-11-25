namespace HollowKnightVision.Windows.Core;

/// <summary>Mac SceneRegions policies, expressed in top-origin capture pixels.</summary>
public sealed record SceneRegions(StencilRect? Knight, StencilRect Health, StencilRect Geo,
    StencilRect Mana, StencilRect? FocusPrompt = null, IReadOnlyList<StencilRect>? Particles = null,
    IReadOnlyList<StencilRect>? HudStencilRects = null, StencilRect? TutorialPrompt = null)
{
    public const double DefaultKnightConfidence = .5;
    public const double GroundKnightConfidence = .05;

    public static SceneRegions Startup() => new(null, default, default, default);

    public IReadOnlyList<StencilRect> OmittedRects =>
        new[] { Knight, FocusPrompt, TutorialPrompt }.OfType<StencilRect>()
            .Concat(HudStencilRects ?? [Health, Geo, Mana]).Concat(Particles ?? [])
            .Where(rect => rect.IsFinite && !rect.IsEmpty).ToArray();

    public static SceneRegions FromObjectDetections(IReadOnlyList<ObjectDetection> detections,
        StencilRect extent, double minimumConfidence = DefaultKnightConfidence,
        double? knightMinimumConfidence = null)
    {
        var threshold = knightMinimumConfidence ?? minimumConfidence;
        var knight = detections.Where(item => float.IsFinite(item.Confidence) && item.Confidence >= threshold
                && LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) == "game.playable-knight")
            .OrderByDescending(item => item.Confidence).FirstOrDefault();
        return Startup() with { Knight = knight is null ? null : ImageRect(knight, extent.Width, extent.Height) };
    }

    // Model boxes are already top-origin. Standardize and clip, but never flip
    // their Y coordinate a second time at the Windows rendering boundary.
    public static StencilRect? ImageRect(ObjectDetection detection, double width, double height)
    {
        var box = detection.Box;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || !double.IsFinite(box.X) || !double.IsFinite(box.Y)
            || !double.IsFinite(box.Width) || !double.IsFinite(box.Height)
            || !double.IsFinite(box.X + box.Width) || !double.IsFinite(box.Y + box.Height)) return null;
        var left = Math.Clamp(Math.Min(box.X, box.X + box.Width), 0, 1);
        var top = Math.Clamp(Math.Min(box.Y, box.Y + box.Height), 0, 1);
        var right = Math.Clamp(Math.Max(box.X, box.X + box.Width), 0, 1);
        var bottom = Math.Clamp(Math.Max(box.Y, box.Y + box.Height), 0, 1);
        return right > left && bottom > top
            ? new(left * width, top * height, (right - left) * width, (bottom - top) * height) : null;
    }

    public static SceneRegions AtlasGameplay(IReadOnlyList<ObjectDetection> detections, StencilRect extent,
        double? knightMinimumConfidence = null, HudStencilResult? hudStencil = null)
    {
        var knight = FromObjectDetections(detections, extent,
            knightMinimumConfidence: knightMinimumConfidence).Knight;
        var fallback = Hud(extent, knight);
        var health = Bounds(hudStencil?.HealthMasks);
        return new(knight, health.IsEmpty ? fallback.Health : health,
            hudStencil?.Geo ?? fallback.Geo,
            hudStencil is null ? fallback.Mana : Bounds(hudStencil.ManaMasks),
            HudStencilRects: AtlasHudMaskRects(extent, hudStencil));
    }

    public static IReadOnlyList<StencilRect> AtlasHudMaskRects(StencilRect extent, HudStencilResult? hudStencil) =>
        [AtlasHudShield(extent), .. hudStencil?.OmittedRects ?? []];

    internal static IReadOnlyList<StencilRect> GroundOmittedRects(IReadOnlyList<StencilRect> sceneRects, StencilRect extent)
    {
        var shield = AtlasHudShield(extent);
        if (!sceneRects.Contains(shield)) return sceneRects;
        var scaleX = extent.Width / 640;
        var scaleY = extent.Height / 360;
        StencilRect Footprint(double x, double y, double width, double height) =>
            Integral(new StencilRect(extent.X + x * scaleX, extent.Y + y * scaleY,
                width * scaleX, height * scaleY).Intersect(extent));
        return [.. sceneRects.Where(rect => rect != shield),
            Footprint(0, 0, 120, 6),    // Capture marker
            Footprint(29, 17, 83, 52),  // Soul and Geo
            Footprint(72, 22, 164, 23)]; // Health row
    }

    private static StencilRect AtlasHudShield(StencilRect extent) =>
        Integral(new StencilRect(extent.X, extent.Y, extent.Width * .40, extent.Height * .25).Intersect(extent));

    public static SceneRegions Hud(StencilRect extent, StencilRect? knight, int geoDigitCount = 1,
        StencilRect? focusPrompt = null, IReadOnlyList<StencilRect>? particles = null)
    {
        var digits = Math.Clamp(geoDigitCount, 1, 6);
        StencilRect Area(double x, double bottom, double width, double height) =>
            new StencilRect(extent.X + extent.Width * x, extent.Y + extent.Height * (1 - bottom - height),
                extent.Width * width, extent.Height * height).Intersect(extent);
        return new(knight, Area(.122, .883, .130, .047), Area(.120, .813, .058 + (digits - 1) * .016, .045),
            Area(.062, .815, .060, .120), focusPrompt, particles);
    }

    public static StencilRect FixedFocusPrompt(StencilRect extent) => Integral(new StencilRect(
        extent.X + extent.Width * .405, extent.Y + extent.Height * .870,
        extent.Width * .235, extent.Height * .105).Intersect(extent));

    /// <summary>Mac HackerAtlas uses receiver projection, never a visual guess, for its hero exclusion.</summary>
    public static SceneRegions HackerAtlas(ReceiverGroundTruthSample sample, int width, int height,
        HudStencilResult? hudStencil = null)
    {
        StencilRect? hero = null;
        if (sample.HeroAvailable && sample.HeroScreenX is { } x && sample.HeroScreenY is { } y
            && double.IsFinite(x) && double.IsFinite(y)
            && sample.ProjectionPixelWidth is > 0 && sample.ProjectionPixelHeight is > 0)
        {
            var heroWidth = 96d * width / 640; var heroHeight = 128d * height / 360;
            hero = new(x * width / sample.ProjectionPixelWidth.Value - heroWidth * .5,
                height - y * height / sample.ProjectionPixelHeight.Value - heroHeight * .58,
                heroWidth, heroHeight);
        }
        var extent = new StencilRect(0, 0, width, height);
        var fallback = Hud(extent, hero);
        return new(hero, hudStencil is null ? fallback.Health : Bounds(hudStencil.HealthMasks),
            hudStencil?.Geo ?? fallback.Geo, hudStencil is null ? fallback.Mana : Bounds(hudStencil.ManaMasks),
            HudStencilRects: AtlasHudMaskRects(extent, hudStencil));
    }

    public IReadOnlyList<PixelRect> PixelExclusions(int width, int height) => PixelRectangles(width, height, OmittedRects);

    public static IReadOnlyList<PixelRect> PixelRectangles(int width, int height, IEnumerable<StencilRect> rectangles)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        var extent = new StencilRect(0, 0, width, height);
        var result = new List<PixelRect>();
        foreach (var rect in rectangles)
        {
            if (!rect.IsFinite || rect.IsEmpty) continue;
            var clipped = rect.Intersect(extent);
            if (clipped.IsEmpty) continue;
            var left = (int)Math.Floor(clipped.X); var top = (int)Math.Floor(clipped.Y);
            result.Add(new(left, top, (int)Math.Ceiling(clipped.Right) - left, (int)Math.Ceiling(clipped.Bottom) - top));
        }
        return result;
    }

    private static StencilRect Bounds(IReadOnlyList<StencilRect>? rectangles)
    {
        StencilRect? result = null;
        foreach (var rect in rectangles ?? [])
            if (rect.IsFinite && !rect.IsEmpty) result = result?.Union(rect) ?? rect;
        return result ?? default;
    }
    private static StencilRect Integral(StencilRect rect) => !rect.IsFinite || rect.IsEmpty ? default
        : new(Math.Floor(rect.X), Math.Floor(rect.Y), Math.Ceiling(rect.Right) - Math.Floor(rect.X),
            Math.Ceiling(rect.Bottom) - Math.Floor(rect.Y));
}

/// <summary>One tested policy shared by the live atlas, ground solver and motion sampler.</summary>
public sealed record GameplaySceneMasks(SceneRegions AtlasRegions, SceneRegions GroundRegions,
    IReadOnlyList<PixelRect> AtlasExclusions, IReadOnlyList<PixelRect> GroundExclusions, PixelRect? GroundKnight)
{
    public StencilRect? ObservedPlayerBounds { get; init; }

    internal PixelRect? ContactKnight(int width, int height) =>
        ObservedPlayerBounds is { } bounds && SceneRegions.PixelRectangles(width, height, [bounds]) is [var pixels]
            ? pixels : GroundKnight;

    public static GameplaySceneMasks Create(BgraFrame frame, LiveObjectDetectionSnapshot detections,
        HudStencilResult? hudStencil, long generation) =>
        Create(frame, detections.Tracked, hudStencil, detections.Masks) with
        {
            ObservedPlayerBounds = LiveObjectDetectionFreshness.CaptureMatchedKnightBounds(
                detections.MeasuredBatch, generation, frame)
        };

    public static GameplaySceneMasks Create(BgraFrame frame, IReadOnlyList<ObjectDetection> detections,
        HudStencilResult? hudStencil, IReadOnlyList<ObjectDetection>? movingMasks = null) =>
        Create(frame.Width, frame.Height, detections, hudStencil, movingMasks, GameplayTutorialPromptMask.Find(frame));

    public static GameplaySceneMasks Create(int width, int height, IReadOnlyList<ObjectDetection> detections,
        HudStencilResult? hudStencil, IReadOnlyList<ObjectDetection>? movingMasks = null) =>
        Create(width, height, detections, hudStencil, movingMasks, tutorialPrompt: null);

    private static GameplaySceneMasks Create(int width, int height, IReadOnlyList<ObjectDetection> detections,
        HudStencilResult? hudStencil, IReadOnlyList<ObjectDetection>? movingMasks, StencilRect? tutorialPrompt)
    {
        var extent = new StencilRect(0, 0, width, height);
        // Mac's live mapper and ground solver share unsmoothed motion masks,
        // including weak Knight evidence. Standalone display regions keep .5.
        var atlas = movingMasks is null ? SceneRegions.AtlasGameplay(detections, extent, hudStencil: hudStencil)
            : SceneRegions.AtlasGameplay(movingMasks, extent, SceneRegions.GroundKnightConfidence, hudStencil);
        var ground = SceneRegions.AtlasGameplay(movingMasks ?? detections, extent, SceneRegions.GroundKnightConfidence, hudStencil);
        atlas = atlas with { TutorialPrompt = tutorialPrompt };
        ground = ground with { TutorialPrompt = tutorialPrompt };
        var knight = ground.Knight is { } rect ? SceneRegions.PixelRectangles(width, height, [rect]) : [];
        var groundOmissions = SceneRegions.GroundOmittedRects(ground.OmittedRects, extent);
        return new(atlas, ground, atlas.PixelExclusions(width, height), SceneRegions.PixelRectangles(width, height, groundOmissions),
            knight.Count > 0 ? knight[0] : null);
    }
}
