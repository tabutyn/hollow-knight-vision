namespace HollowKnightVision.Windows.Core;

// Port of macos/Sources/HollowKnightVision/HUDStencil.swift and the RGB
// comparison in ImageStencil.swift. Windows rectangles stay top-origin.
public readonly record struct StencilRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);
    public StencilRect Intersect(StencilRect other) => new(Math.Max(X, other.X), Math.Max(Y, other.Y),
        Math.Max(0, Math.Min(Right, other.Right) - Math.Max(X, other.X)),
        Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y)));
    public StencilRect Offset(double x, double y) => this with { X = X + x, Y = Y + y };
    public StencilRect Expand(double margin) => new(X - margin, Y - margin, Width + 2 * margin, Height + 2 * margin);
    public StencilRect Union(StencilRect other) => new(Math.Min(X, other.X), Math.Min(Y, other.Y),
        Math.Max(Right, other.Right) - Math.Min(X, other.X), Math.Max(Bottom, other.Bottom) - Math.Min(Y, other.Y));
}

public sealed record HudStencilAtom(string Kind, double[] Rect, byte[] Rgb)
{
    public StencilRect Bounds => new(Rect[0], Rect[1], Rect[2], Rect[3]);
    internal bool IsValid => Rect is { Length: 4 } && Rect.All(double.IsFinite)
        && Rect[2] is > 0 and <= 640 && Rect[3] is > 0 and <= 360
        && Rgb is not null && Rgb.Length == (int)Rect[2] * (int)Rect[3] * 3;
}

public sealed record HudStencilTemplate(int SchemaVersion, string SourceExampleID,
    int ReferenceWidth, int ReferenceHeight, double HealthPitch,
    HudStencilAtom[] Health, HudStencilAtom[] ManaMain, double[] ManaContainer,
    HudStencilAtom[] ManaReserves, double[] Geo)
{
    public bool IsValid => SchemaVersion == 1 && ReferenceWidth is > 0 and <= 4096
        && ReferenceHeight is > 0 and <= 4096 && double.IsFinite(HealthPitch) && HealthPitch > 4
        && Health is { Length: > 0 } && ManaMain is not null && ManaReserves is not null
        && ManaContainer is { Length: 4 } && Geo is { Length: 4 }
        && ManaContainer.All(double.IsFinite) && Geo.All(double.IsFinite)
        && Health.Concat(ManaMain).Concat(ManaReserves).All(atom => atom is not null && atom.IsValid);

    public static HudStencilTemplate? Captured(BgraFrame frame,
        IEnumerable<LabelingExampleAnnotation> annotations, string sourceExampleID)
    {
        var boxes = annotations.Where(item => !item.IsHardNegative && item.ClassIdentifier == "game.health")
            .Select(item => new StencilRect(Math.Floor(item.X * 640), Math.Floor(item.Y * 360),
                Math.Ceiling((item.X + item.Width) * 640) - Math.Floor(item.X * 640),
                Math.Ceiling((item.Y + item.Height) * 360) - Math.Floor(item.Y * 360)))
            .OrderBy(box => box.X).ToArray();
        if (boxes.Length != 2 || boxes.Any(box => box.IsEmpty || box.X < 0 || box.Y < 0 || box.Bottom > 80 || box.Right > 320)
            || boxes[0].Right > boxes[1].X || Math.Abs(boxes[0].MidY - boxes[1].MidY) >= Math.Max(boxes[0].Height, boxes[1].Height) / 2)
            return null;
        var pixels = new HudStencilPixels(frame, 640, 360);
        var atoms = boxes.Select(box => new HudStencilAtom("full", [box.X, box.Y, box.Width, box.Height], pixels.Reference(box))).ToArray();
        var template = new HudStencilTemplate(1, sourceExampleID, 640, 360, boxes[1].MidX - boxes[0].MidX,
            atoms, [], [0, 0, 0, 0], [], [0, 0, 0, 0]);
        if (!template.IsValid
            || new HudStencilTracker(template).Observe(frame, true, 0, 0, allowRowRecovery: false)?.EstablishesGameplay != true)
            return null;
        return template;
    }
}

public sealed record HudStencilMatch(int Index, string Kind, StencilRect Rect, double Confidence);
public sealed record HudStencilResult(IReadOnlyList<HudStencilMatch> Health, HudStencilMatch? ManaMain,
    IReadOnlyList<HudStencilMatch> ManaReserves, IReadOnlyList<StencilRect> HealthMasks,
    IReadOnlyList<StencilRect> ManaMasks, StencilRect Geo, bool HoldsHealthMask, double SourceTimestamp)
{
    public bool EstablishesGameplay => Health.Count >= 2;
    public IReadOnlyList<StencilRect> OmittedRects => [.. HealthMasks, .. ManaMasks, Geo];
}

public sealed class HudStencilTracker
{
    private const int LocalRowRadius = 4;
    private const int MaximumRowOffset = 32;
    private static readonly double[] ViewportScales = [1, .95, 1 / .95, .90, 1 / .90];
    private readonly HudStencilTemplate? template;
    private readonly HudStencilKernel[] healthKernels, mainKernels, reserveKernels;
    private long? generation;
    private IReadOnlyList<StencilRect> previousHealth = [], previousMana = [];
    private double lastHealthSeen = double.NegativeInfinity, lastManaSeen = double.NegativeInfinity;
    private double viewportScale = 1, rowOffsetY, lastRowSearch = double.NegativeInfinity;

    public HudStencilTracker() : this(null) { }
    public HudStencilTracker(HudStencilTemplate? template)
    {
        this.template = template?.IsValid == true ? template : null;
        healthKernels = HealthVariants(this.template?.Health ?? [])
            .Select(atom => new HudStencilKernel(atom)).ToArray();
        mainKernels = this.template?.ManaMain.Select(atom => new HudStencilKernel(atom, mainManaOnly: true)).ToArray() ?? [];
        reserveKernels = ReserveVariants(this.template?.ManaReserves ?? [])
            .SelectMany(atom => new[] { new HudStencilKernel(atom), new HudStencilKernel(atom, opaqueManaOnly: true) }).ToArray();
    }

    public void Reset()
    {
        generation = null; previousHealth = []; previousMana = [];
        lastHealthSeen = double.NegativeInfinity; lastManaSeen = double.NegativeInfinity;
        viewportScale = 1; rowOffsetY = 0; lastRowSearch = double.NegativeInfinity;
    }

    public HudStencilResult? Observe(BgraFrame frame, bool gameplay, double timestamp, long generation,
        bool allowRowRecovery = true)
    {
        if (!gameplay || template is null || !double.IsFinite(timestamp)) { Reset(); return null; }
        if (this.generation != generation) { Reset(); this.generation = generation; }
        var pixels = new HudStencilPixels(frame, template.ReferenceWidth, template.ReferenceHeight)
            { ViewportScale = viewportScale };
        var sx = (double)frame.Width / template.ReferenceWidth;
        var sy = (double)frame.Height / template.ReferenceHeight;
        StencilRect Output(StencilRect rect, double margin = 0)
        {
            rect = pixels.FrameRect(rect.Expand(margin));
            var x = Math.Clamp(rect.X * sx, 0, frame.Width);
            var y = Math.Clamp(rect.Y * sy, 0, frame.Height);
            return new(x, y, Math.Max(0, Math.Min(frame.Width, rect.Right * sx) - x),
                Math.Max(0, Math.Min(frame.Height, rect.Bottom * sy) - y));
        }
        static StencilRect Rect(double[] values) => new(values[0], values[1], values[2], values[3]);
        var localLimit = MaximumRowOffset - LocalRowRadius;
        var row = MatchHealthRow(pixels, template, Math.Clamp(rowOffsetY, -localLimit, localLimit),
            LocalRowRadius, Output);
        if (allowRowRecovery && row.Health.Count < 2 && timestamp - lastRowSearch >= .25)
        {
            lastRowSearch = timestamp;
            foreach (var scale in ViewportScales)
            {
                pixels.ViewportScale = scale;
                var candidate = MatchHealthRow(pixels, template, 0,
                    scale >= 1 ? MaximumRowOffset : LocalRowRadius * 2, Output);
                if (candidate.Health.Count < 2) continue;
                // After a resize, a later mask can occupy the first mask's old position.
                var precedingCenter = template.Health[0].Bounds.MidX + candidate.OffsetX - template.HealthPitch;
                if (BestHealth(pixels, precedingCenter, candidate.OffsetY, 2, 1) is { } preceding
                    && preceding.Score >= HealthThreshold(preceding.Kernel.Kind)) continue;
                row = candidate;
                if (viewportScale != scale) { previousHealth = []; previousMana = []; }
                viewportScale = scale;
                break;
            }
            pixels.ViewportScale = viewportScale;
        }
        var (health, freshHealthMasks, offsetX, offsetY) = row;
        if (health.Count >= 2) rowOffsetY = offsetY;
        var holding = freshHealthMasks.Count < previousHealth.Count && timestamp >= lastHealthSeen && timestamp - lastHealthSeen <= .5;
        var healthMasks = holding ? previousHealth : freshHealthMasks;
        if (!holding) { previousHealth = healthMasks; lastHealthSeen = timestamp; }

        HudStencilMatch? main = null;
        if (template.ManaMain.FirstOrDefault() is { } mainAtom)
        {
            var expected = mainAtom.Bounds.Offset(offsetX, offsetY);
            Candidate? best = null;
            for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                    foreach (var atom in mainKernels) Best(ref best, atom, expected.Offset(dx, dy), pixels);
            if (best?.Score >= .70) main = new(0, best.Kernel.Kind, Output(best.Rect), best.Score);
        }
        var reserves = new List<HudStencilMatch>();
        var freshManaMasks = new List<StencilRect>();
        for (var index = 0; index < template.ManaReserves.Length; index++)
        {
            var expected = template.ManaReserves[index].Bounds.Offset(offsetX, offsetY);
            Candidate? best = null;
            foreach (var scale in new[] { .8, 1, 1.2 })
            {
                var w = Round(expected.Width * scale); var h = Round(expected.Height * scale);
                var scaled = new StencilRect(Round(expected.MidX - w / 2), Round(expected.MidY - h / 2), w, h);
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        foreach (var atom in reserveKernels) Best(ref best, atom, scaled.Offset(dx, dy), pixels);
            }
            if (best?.Score >= .72)
            {
                reserves.Add(new(index, best.Kernel.Kind, Output(best.Rect), best.Score));
                freshManaMasks.Add(Output(expected.Union(best.Rect), 2));
            }
        }
        IReadOnlyList<StencilRect> reserveMasks;
        if (freshManaMasks.Count < previousMana.Count && timestamp >= lastManaSeen && timestamp - lastManaSeen <= .5)
            reserveMasks = previousMana;
        else { reserveMasks = freshManaMasks; previousMana = reserveMasks; lastManaSeen = timestamp; }
        var container = Rect(template.ManaContainer);
        IReadOnlyList<StencilRect> manaMasks = container.IsEmpty ? reserveMasks
            : [Output(container.Offset(offsetX, offsetY), 2), .. reserveMasks];
        var geo = Rect(template.Geo);
        return new(health, main, reserves, healthMasks, manaMasks,
            geo.IsEmpty ? default : Output(geo.Offset(offsetX, offsetY), 1), holding, timestamp);
    }

    private sealed record HealthRow(IReadOnlyList<HudStencilMatch> Health, IReadOnlyList<StencilRect> Masks,
        double OffsetX, double OffsetY);

    private HealthRow MatchHealthRow(HudStencilPixels pixels, HudStencilTemplate reference,
        double searchOffsetY, int verticalRadius, Func<StencilRect, double, StencilRect> output)
    {
        var firstCenter = reference.Health[0].Bounds.MidX;
        var pitch = reference.HealthPitch;
        double offsetX = 0, offsetY = 0;
        var health = new List<HudStencilMatch>();
        var masks = new List<StencilRect>();
        var misses = 0;
        for (var index = 0; index < 32; index++)
        {
            var center = firstCenter + index * pitch + offsetX;
            if (center >= reference.ReferenceWidth - 8) break;
            var first = index == 0;
            var best = BestHealth(pixels, center, first ? searchOffsetY : offsetY,
                first ? 7 : 2, first ? verticalRadius : 1);
            if (best is null || best.Score < HealthThreshold(best.Kernel.Kind))
            {
                misses++;
                if (health.Count == 0 || misses > (health.Count < 2 ? 9 : 1)) break;
                continue;
            }
            misses = 0;
            if (first)
            {
                offsetX = best.Rect.MidX - firstCenter;
                offsetY = best.Rect.Y - best.Kernel.Bounds.Y;
            }
            else if (pixels.ViewportScale != 1)
            {
                var measuredPitch = (best.Rect.MidX - firstCenter - offsetX) / index;
                pitch = Math.Clamp(measuredPitch, reference.HealthPitch - 1, reference.HealthPitch + 1);
            }
            var cellCenter = first ? best.Rect.MidX : center;
            var mask = output(new(cellCenter - pitch / 2, best.Rect.Y - 1, pitch, best.Rect.Height + 2), 2);
            if (health.Count > 0 && index - health[^1].Index > 1) mask = masks[^1].Union(mask);
            health.Add(new(index, best.Kernel.Kind, output(best.Rect, 0), best.Score));
            masks.Add(mask);
        }
        return new(health, masks, offsetX, offsetY);
    }

    private Candidate? BestHealth(HudStencilPixels pixels, double center, double offsetY,
        int radius, int verticalRadius)
    {
        Candidate? best = null;
        foreach (var atom in healthKernels)
        for (var dy = -verticalRadius; dy <= verticalRadius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var bounds = new StencilRect(Round(center - atom.Bounds.Width / 2) + dx,
                atom.Bounds.Y + offsetY + dy, atom.Bounds.Width, atom.Bounds.Height);
            Best(ref best, atom, bounds, pixels);
        }
        return best;
    }

    private static double HealthThreshold(string kind) => kind == "empty" ? .78 : .75;
    private sealed record Candidate(HudStencilKernel Kernel, StencilRect Rect, double Score);
    private static void Best(ref Candidate? best, HudStencilKernel atom, StencilRect bounds, HudStencilPixels pixels)
    {
        var score = pixels.Score(atom, bounds);
        if (score > (best?.Score ?? 0)) best = new(atom, bounds, score);
    }
    internal static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private static IEnumerable<HudStencilAtom> HealthVariants(HudStencilAtom[] atoms)
    {
        foreach (var atom in atoms) yield return atom;
        if (atoms.Any(atom => atom.Kind == "lifeblood")) yield break;
        // Lifeblood uses the same mask shape under a blue tint.
        foreach (var atom in atoms.Where(atom => atom.Kind == "full"))
        {
            var rgb = atom.Rgb.ToArray();
            for (var index = 0; index < rgb.Length; index += 3)
            {
                rgb[index] = (byte)(rgb[index] * .45);
                rgb[index + 1] = (byte)(rgb[index + 1] * .75);
            }
            yield return new("lifeblood", atom.Rect, rgb);
        }
    }

    private static IEnumerable<HudStencilAtom> ReserveVariants(HudStencilAtom[] atoms)
    {
        foreach (var atom in atoms) yield return atom;
        if (atoms.FirstOrDefault(atom => atom.Kind == "full") is not { } full) yield break;
        foreach (var empty in atoms.Where(atom => atom.Kind == "empty"))
        {
            var w = (int)empty.Bounds.Width; var h = (int)empty.Bounds.Height;
            var fw = (int)full.Bounds.Width; var fh = (int)full.Bounds.Height;
            foreach (var fraction in new[] { .25, .5, .75 })
            {
                var rgb = empty.Rgb.ToArray();
                var boundary = (1 - fraction) * h;
                for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
                {
                    var fx = Math.Min(fw - 1, (int)((x + .5) * fw / w));
                    var fy = Math.Min(fh - 1, (int)((y + .5) * fh / h));
                    var weight = Math.Clamp(y + 1 - boundary, 0, 1);
                    for (var c = 0; c < 3; c++)
                    {
                        var i = (y * w + x) * 3 + c; var j = (fy * fw + fx) * 3 + c;
                        rgb[i] = (byte)Round(empty.Rgb[i] * (1 - weight) + full.Rgb[j] * weight);
                    }
                }
                yield return new("partial", empty.Rect, rgb);
            }
        }
    }
}

internal sealed class HudStencilKernel
{
    internal string Kind { get; }
    internal StencilRect Bounds { get; }
    internal ImageStencilKernel ImageKernel { get; }

    internal HudStencilKernel(HudStencilAtom atom, bool opaqueManaOnly = false, bool mainManaOnly = false)
    {
        Kind = atom.Kind; Bounds = atom.Bounds;
        ImageKernel = new ImageStencilKernel(Kind, Bounds, atom.Rgb, includes: (x, y, w, h) =>
        {
            var i = (y * w + x) * 3;
            var r = atom.Rgb[i]; var g = atom.Rgb[i + 1]; var b = atom.Rgb[i + 2];
            if (mainManaOnly)
            {
                var edge = x < 15 || x >= w - 15 || y < 10 || y >= h - 10;
                if (!edge || Math.Min(r, Math.Min(g, b)) < 70 || Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60) return false;
            }
            else
            {
                if (h >= 14)
                {
                    if (Kind == "empty")
                    {
                        var nx = (x - w * .5) / (w * .27); var ny = (y - h * .44) / (h * .30);
                        if (nx * nx + ny * ny > 1) return false;
                    }
                    else if (x < 2 || x >= w - 2 || y < 3 || y >= h - 2) return false;
                }
                if (h < 14 && opaqueManaOnly)
                {
                    var nx = (x + .5 - w * .5) / (w * .40); var ny = (y + .5 - h * .5) / (h * .40);
                    if (nx * nx + ny * ny > 1) return false;
                }
            }
            return true;
        });
    }
}

internal sealed class HudStencilPixels
{
    private readonly ImageStencilPixels pixels;
    private readonly int referenceHeight;
    internal double ViewportScale { get; set; } = 1;

    internal HudStencilPixels(BgraFrame frame, int referenceWidth, int referenceHeight)
    {
        this.referenceHeight = referenceHeight;
        pixels = new(frame, referenceWidth, referenceHeight, 0, Math.Min(referenceHeight, 80));
    }

    internal StencilRect FrameRect(StencilRect rect)
    {
        var scale = ViewportScale;
        return new(rect.X * scale + pixels.Width * (1 - scale) / 2,
            rect.Y * scale + referenceHeight * (1 - scale), rect.Width * scale, rect.Height * scale);
    }

    internal byte[] Reference(StencilRect rect)
    {
        return pixels.Reference(rect)?.Rgb ?? throw new ArgumentOutOfRangeException(nameof(rect));
    }

    internal double Score(HudStencilKernel kernel, StencilRect rect)
    {
        if (ViewportScale != 1)
        {
            var target = FrameRect(rect);
            rect = new(HudStencilTracker.Round(target.X), HudStencilTracker.Round(target.Y),
                HudStencilTracker.Round(target.Width), HudStencilTracker.Round(target.Height));
        }
        if (pixels.Compare(kernel.ImageKernel, rect) is not { SupportsCorrelation: true } comparison) return 0;
        var colorDifference = comparison.MeanBlue - comparison.MeanRed;
        if (kernel.Kind == "lifeblood" && colorDifference < 30 || kernel.Kind == "full" && colorDifference > 24) return 0;
        if (comparison.NormalizedCorrelation < .6 || comparison.MeanAbsoluteColorError > (kernel.Kind == "empty" ? 18 : 58)) return 0;
        return comparison.Confidence(.75, 80);
    }
}
