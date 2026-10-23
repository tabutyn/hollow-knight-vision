using System.Text.Json;

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

    public static HudStencilTemplate? Bundled { get; } = LoadBundled();
    private static HudStencilTemplate? LoadBundled()
    {
        using var stream = typeof(HudStencilTemplate).Assembly.GetManifestResourceStream("hud-stencil-template.json");
        if (stream is null) return null;
        var template = JsonSerializer.Deserialize<HudStencilTemplate>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return template?.IsValid == true ? template : null;
    }

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
        if (!template.IsValid || new HudStencilTracker(template).Observe(frame, true, 0, 0)?.EstablishesGameplay != true) return null;
        // Match Mac: captured full masks set position/pitch, stock variants
        // keep damage/lifeblood recognizable without object-model training.
        return template with { Health = [.. atoms, .. Bundled?.Health ?? []] };
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
    private readonly HudStencilTemplate? template;
    private readonly HudStencilKernel[] healthKernels, mainKernels, reserveKernels;
    private long? generation;
    private IReadOnlyList<StencilRect> previousHealth = [], previousMana = [];
    private double lastHealthSeen = double.NegativeInfinity, lastManaSeen = double.NegativeInfinity;

    public HudStencilTracker() : this(HudStencilTemplate.Bundled) { }
    public HudStencilTracker(HudStencilTemplate? template)
    {
        this.template = template?.IsValid == true ? template : null;
        healthKernels = this.template?.Health.Select(atom => new HudStencilKernel(atom)).ToArray() ?? [];
        mainKernels = this.template?.ManaMain.Select(atom => new HudStencilKernel(atom, mainManaOnly: true)).ToArray() ?? [];
        reserveKernels = ReserveVariants(this.template?.ManaReserves ?? [])
            .SelectMany(atom => new[] { new HudStencilKernel(atom), new HudStencilKernel(atom, opaqueManaOnly: true) }).ToArray();
    }

    public void Reset()
    {
        generation = null; previousHealth = []; previousMana = [];
        lastHealthSeen = double.NegativeInfinity; lastManaSeen = double.NegativeInfinity;
    }

    public HudStencilResult? Observe(BgraFrame frame, bool gameplay, double timestamp, long generation)
    {
        if (!gameplay || template is null || !double.IsFinite(timestamp)) { Reset(); return null; }
        if (this.generation != generation) { Reset(); this.generation = generation; }
        var pixels = new HudStencilPixels(frame, template.ReferenceWidth, template.ReferenceHeight);
        var sx = (double)frame.Width / template.ReferenceWidth;
        var sy = (double)frame.Height / template.ReferenceHeight;
        StencilRect Output(StencilRect rect, double margin = 0)
        {
            rect = rect.Expand(margin);
            var x = Math.Clamp(rect.X * sx, 0, frame.Width);
            var y = Math.Clamp(rect.Y * sy, 0, frame.Height);
            return new(x, y, Math.Max(0, Math.Min(frame.Width, rect.Right * sx) - x),
                Math.Max(0, Math.Min(frame.Height, rect.Bottom * sy) - y));
        }
        static StencilRect Rect(double[] values) => new(values[0], values[1], values[2], values[3]);
        var firstCenter = template.Health[0].Bounds.MidX;
        double offsetX = 0, offsetY = 0;
        var health = new List<HudStencilMatch>();
        IReadOnlyList<StencilRect> healthMasks;
        var freshHealthMasks = new List<StencilRect>();
        for (var index = 0; index < 32; index++)
        {
            var center = firstCenter + index * template.HealthPitch + offsetX;
            if (center >= template.ReferenceWidth - 8) break;
            Candidate? best = null;
            var radius = index == 0 ? 7 : 2;
            var verticalRadius = index == 0 ? 4 : 1;
            foreach (var atom in healthKernels)
                for (var dy = -verticalRadius; dy <= verticalRadius; dy++)
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var bounds = new StencilRect(Round(center - atom.Bounds.Width / 2) + dx,
                            atom.Bounds.Y + offsetY + dy, atom.Bounds.Width, atom.Bounds.Height);
                        Best(ref best, atom, bounds, pixels);
                    }
            if (best is null || best.Score < (best.Kernel.Kind == "empty" ? .78 : .75)) break;
            if (index == 0)
            {
                offsetX = best.Rect.MidX - firstCenter;
                offsetY = best.Rect.Y - best.Kernel.Bounds.Y;
            }
            health.Add(new(index, best.Kernel.Kind, Output(best.Rect), best.Score));
            var cellCenter = index == 0 ? best.Rect.MidX : center;
            freshHealthMasks.Add(Output(new(cellCenter - template.HealthPitch / 2,
                best.Rect.Y - 1, template.HealthPitch, best.Rect.Height + 2), 2));
        }
        var holding = freshHealthMasks.Count < previousHealth.Count && timestamp >= lastHealthSeen && timestamp - lastHealthSeen <= .5;
        if (holding) healthMasks = previousHealth;
        else { healthMasks = freshHealthMasks; previousHealth = healthMasks; lastHealthSeen = timestamp; }

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

    private sealed record Candidate(HudStencilKernel Kernel, StencilRect Rect, double Score);
    private static void Best(ref Candidate? best, HudStencilKernel atom, StencilRect bounds, HudStencilPixels pixels)
    {
        var score = pixels.Score(atom, bounds);
        if (score > (best?.Score ?? 0)) best = new(atom, bounds, score);
    }
    internal static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

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
    internal HudStencilPixels(BgraFrame frame, int referenceWidth, int referenceHeight)
    {
        pixels = new(frame, referenceWidth, referenceHeight, 0, Math.Min(referenceHeight, 80));
    }

    internal byte[] Reference(StencilRect rect)
    {
        return pixels.Reference(rect)?.Rgb ?? throw new ArgumentOutOfRangeException(nameof(rect));
    }

    internal double Score(HudStencilKernel kernel, StencilRect rect)
    {
        if (pixels.Compare(kernel.ImageKernel, rect) is not { SupportsCorrelation: true } comparison) return 0;
        var colorDifference = comparison.MeanBlue - comparison.MeanRed;
        if (kernel.Kind == "lifeblood" && colorDifference < 30 || kernel.Kind == "full" && colorDifference > 24) return 0;
        if (comparison.NormalizedCorrelation < .6 || comparison.MeanAbsoluteColorError > (kernel.Kind == "empty" ? 18 : 58)) return 0;
        return comparison.Confidence(.75, 80);
    }
}
