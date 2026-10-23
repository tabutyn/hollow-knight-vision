namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Mac's legacy image-based region detector. Like the current Mac application,
/// live Windows capture uses model/stencil-owned SceneRegions instead.
/// Rectangles and camera displacement here use top-origin image coordinates.
/// </summary>
public sealed partial class SceneRegionDetector
{
    private const int SampleWidth = 320;
    private SourcePoint? previousCenter;
    private SourcePoint headVelocity;
    private int missedFrames;
    private int focusPromptHoldFrames;
    private (int Width, int Height)? imageSize;

    public void Reset()
    {
        previousCenter = null; headVelocity = default; missedFrames = 0; focusPromptHoldFrames = 0;
        intensityHistory.Clear(); particleTracks.Clear();
        imageSize = null;
    }

    public SceneRegions Detect(BgraFrame image, SourcePoint cameraPosition = default,
        double? solveWidth = null, bool includeParticles = true)
    {
        if (imageSize != (image.Width, image.Height)) Reset();
        imageSize = (image.Width, image.Height);
        var height = Math.Max(1, Round((double)image.Height * SampleWidth / image.Width));
        var pixels = Sample(image, SampleWidth, height);
        var extent = new StencilRect(0, 0, image.Width, image.Height);
        var knight = DetectKnight(image, pixels, height);
        var digits = EstimateGeoDigits(pixels, SampleWidth, height);
        var prompt = DetectFocusPrompt(pixels, height, extent);
        var hud = SceneRegions.Hud(extent, knight, digits, prompt);
        var scale = image.Width / Math.Max(1, solveWidth ?? image.Width);
        var particles = includeParticles ? DetectParticles(image, hud.OmittedRects,
            new(cameraPosition.X * scale, cameraPosition.Y * scale)) : [];
        return hud with { Particles = particles };
    }

    private StencilRect? DetectFocusPrompt(byte[] pixels, int height, StencilRect extent)
    {
        var prompt = SceneRegions.FixedFocusPrompt(extent);
        var scaleX = SampleWidth / extent.Width; var scaleY = height / extent.Height;
        var minX = Math.Max(1, (int)Math.Floor(prompt.X * scaleX));
        var maxX = Math.Min(SampleWidth - 1, (int)Math.Ceiling(prompt.Right * scaleX));
        var minY = Math.Max(1, (int)Math.Floor(prompt.Y * scaleY));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(prompt.Bottom * scaleY));
        if (minX >= maxX || minY >= maxY) return null;
        var text = new bool[SampleWidth * height];
        for (var y = minY; y < maxY; y++) for (var x = minX; x < maxX; x++)
        {
            var (red, green, blue) = Color(pixels, SampleWidth, x, y);
            var brightest = Math.Max(red, Math.Max(green, blue)); var darkest = Math.Min(red, Math.Min(green, blue));
            if (darkest < 72 || brightest - darkest > 58) continue;
            var luma = Luma(pixels, SampleWidth, x, y);
            var horizontal = Math.Max(Math.Abs(luma - Luma(pixels, SampleWidth, x - 1, y)),
                Math.Abs(luma - Luma(pixels, SampleWidth, x + 1, y)));
            var vertical = Math.Max(Math.Abs(luma - Luma(pixels, SampleWidth, x, y - 1)),
                Math.Abs(luma - Luma(pixels, SampleWidth, x, y + 1)));
            text[y * SampleWidth + x] = Math.Max(horizontal, vertical) >= 12;
        }
        var glyphs = Components(text, SampleWidth, height, minX, minY, maxX, maxY)
            .Where(item => item.Count is >= 2 and <= 150 && item.Bounds.Width <= 18
                && item.Bounds.Height is >= 2 and <= 14).Select(item => item.Bounds).ToArray();
        var visible = glyphs.Length >= 3 && glyphs.Max(rect => rect.Right) - glyphs.Min(rect => rect.X) >= 14;
        if (visible) focusPromptHoldFrames = 2;
        else if (focusPromptHoldFrames > 0) focusPromptHoldFrames--;
        return focusPromptHoldFrames > 0 ? prompt : null;
    }

    private sealed record Candidate(StencilRect Bounds, int BrightPixels, double DarkBodyRatio, double BrightHaloRatio);
    private StencilRect? DetectKnight(BgraFrame image, byte[] pixels, int height)
    {
        var mask = new bool[SampleWidth * height];
        var excludedTop = (int)(height * (previousCenter is null ? .30 : .18));
        for (var y = excludedTop; y < height; y++) for (var x = 0; x < SampleWidth; x++)
        {
            var (r, g, b) = Color(pixels, SampleWidth, x, y);
            mask[y * SampleWidth + x] = pixels[(y * SampleWidth + x) * 4 + 3] > 180
                && r > 170 && g > 170 && b > 170 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 65;
        }
        var candidates = new List<Candidate>();
        foreach (var component in Components(mask, SampleWidth, height, 0, excludedTop, SampleWidth, height))
        {
            var bounds = component.Bounds; var aspect = bounds.Height / Math.Max(1, bounds.Width);
            if (component.Count < 8 || bounds.Width is < 4 or > 34 || bounds.Height is < 7 or > 42
                || aspect is < .72 or > 2.4) continue;
            var appearance = Appearance(bounds, pixels, SampleWidth, height);
            if (appearance.DarkBodyRatio >= .22)
                candidates.Add(new(bounds, component.Count, appearance.DarkBodyRatio, appearance.BrightHaloRatio));
        }
        var winner = candidates.Where(WithinTrackGate).OrderByDescending(item => Score(item, height)).FirstOrDefault();
        if (winner is null)
        {
            missedFrames++;
            if (missedFrames <= 12 && previousCenter is { } center)
            {
                var predicted = new SourcePoint(center.X + headVelocity.X, center.Y + headVelocity.Y);
                previousCenter = predicted;
                headVelocity = new(headVelocity.X * .82, headVelocity.Y * .82);
                return KnightRect(predicted, height, image);
            }
            previousCenter = null; headVelocity = default;
            return null;
        }
        missedFrames = 0;
        var measured = new SourcePoint(winner.Bounds.MidX, winner.Bounds.MidY);
        SourcePoint filtered;
        if (previousCenter is { } prior)
        {
            var stableX = Math.Abs(measured.X - prior.X) < 1.25;
            var stableY = Math.Abs(measured.Y - prior.Y) < 1;
            var predicted = new SourcePoint(prior.X + headVelocity.X, prior.Y + headVelocity.Y);
            var innovation = new SourcePoint(measured.X - predicted.X, measured.Y - predicted.Y);
            var verticalAlpha = Math.Abs(innovation.Y) > 5 ? .82 : .58;
            filtered = new(stableX ? prior.X : predicted.X + innovation.X * .52,
                stableY ? prior.Y : predicted.Y + innovation.Y * verticalAlpha);
            headVelocity = new(stableX ? 0 : headVelocity.X * .52 + innovation.X * .24,
                stableY ? 0 : headVelocity.Y * .48 + innovation.Y * .34);
        }
        else { filtered = measured; headVelocity = default; }
        previousCenter = filtered;
        return KnightRect(filtered, height, image);
    }

    private double Score(Candidate candidate, int height)
    {
        var x = candidate.Bounds.MidX; var y = candidate.Bounds.MidY;
        var value = Math.Min(3, candidate.BrightPixels / 20d) + candidate.DarkBodyRatio * 5 - candidate.BrightHaloRatio * 4;
        if (previousCenter is { } prior)
            value += Math.Max(0, 1 - Distance(x - prior.X - headVelocity.X, (y - prior.Y - headVelocity.Y) * .72) / 54) * 14;
        else
        {
            value += Math.Max(0, 1 - Math.Abs(y - height * .68) / height) * 4;
            value += Math.Max(0, 1 - Math.Abs(x - SampleWidth * .5) / SampleWidth);
        }
        return value + Math.Max(0, 1 - Math.Abs(candidate.Bounds.Height / Math.Max(1, candidate.Bounds.Width) - 1.2) / 1.5);
    }
    private bool WithinTrackGate(Candidate candidate) => previousCenter is not { } prior
        || Distance(candidate.Bounds.MidX - prior.X - headVelocity.X,
            (candidate.Bounds.MidY - prior.Y - headVelocity.Y) * .78) <= 54;

    private static (double DarkBodyRatio, double BrightHaloRatio) Appearance(StencilRect bounds, byte[] pixels, int width, int height)
    {
        var centerX = Round(bounds.MidX); var half = Math.Max(3, Round(bounds.Width * .58));
        var top = Math.Min(height, (int)Math.Ceiling(bounds.Bottom));
        var bottom = Math.Min(height, top + Math.Max(8, Round(bounds.Height * .95)));
        var dark = 0; var body = 0;
        for (var y = top; y < bottom; y++) for (var x = Math.Max(0, centerX - half); x < Math.Min(width, centerX + half + 1); x++)
        { body++; if (Luma(pixels, width, x, y) < 74) dark++; }
        var padding = Math.Max(4, Round(Math.Max(bounds.Width, bounds.Height) * .55));
        var bright = 0; var halo = 0;
        for (var y = Math.Max(0, (int)bounds.Y - padding); y < Math.Min(height, (int)Math.Ceiling(bounds.Bottom) + padding); y++)
            for (var x = Math.Max(0, (int)bounds.X - padding); x < Math.Min(width, (int)Math.Ceiling(bounds.Right) + padding); x++)
            {
                if (Contains(bounds, x, y)) continue;
                halo++; if (Luma(pixels, width, x, y) > 112) bright++;
            }
        return ((double)dark / Math.Max(1, body), (double)bright / Math.Max(1, halo));
    }

    private static int EstimateGeoDigits(byte[] pixels, int width, int height)
    {
        var minX = Math.Max(0, (int)(width * .15)); var maxX = Math.Min(width, (int)(width * .27));
        var minY = Math.Max(0, (int)(height * .118)); var maxY = Math.Min(height, (int)(height * .19));
        var rightmost = -1;
        for (var y = minY; y < maxY; y++) for (var x = minX; x < maxX; x++)
        {
            var (r, g, b) = Color(pixels, width, x, y);
            if (Math.Min(r, Math.Min(g, b)) > 145 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 55)
                rightmost = Math.Max(rightmost, x);
        }
        return rightmost < minX ? 1 : Math.Clamp((int)Math.Ceiling(((double)(rightmost + 1) / width - .158) / .016), 1, 6);
    }

    private static StencilRect KnightRect(SourcePoint head, int sampleHeight, BgraFrame image)
    {
        var width = Round(image.Width * .036); var height = Round(image.Height * .125);
        var centerX = head.X * image.Width / SampleWidth + image.Width * .004;
        var macTop = image.Height - head.Y * image.Height / sampleHeight + image.Height * .0585;
        var x = Round(Math.Clamp(centerX - width / 2d, 0, Math.Max(0, image.Width - width)));
        var bottomY = Round(Math.Clamp(macTop - height, 0, Math.Max(0, image.Height - height)));
        return new(x, image.Height - bottomY - height, width, height);
    }

    private sealed record Component(StencilRect Bounds, int Count);
    private static IEnumerable<Component> Components(bool[] mask, int width, int height, int minX, int minY, int maxX, int maxY)
    {
        var visited = new bool[mask.Length];
        for (var y = minY; y < maxY; y++) for (var x = minX; x < maxX; x++)
        {
            var start = y * width + x;
            if (!mask[start] || visited[start]) continue;
            var queue = new List<(int X, int Y)> { (x, y) }; visited[start] = true;
            var lowX = x; var highX = x; var lowY = y; var highY = y;
            for (var cursor = 0; cursor < queue.Count; cursor++)
            {
                var point = queue[cursor];
                lowX = Math.Min(lowX, point.X); highX = Math.Max(highX, point.X);
                lowY = Math.Min(lowY, point.Y); highY = Math.Max(highY, point.Y);
                for (var ny = Math.Max(minY, point.Y - 1); ny <= Math.Min(maxY - 1, point.Y + 1); ny++)
                    for (var nx = Math.Max(minX, point.X - 1); nx <= Math.Min(maxX - 1, point.X + 1); nx++)
                    {
                        var i = ny * width + nx;
                        if (!mask[i] || visited[i]) continue;
                        visited[i] = true; queue.Add((nx, ny));
                    }
            }
            yield return new(new(lowX, lowY, highX - lowX + 1, highY - lowY + 1), queue.Count);
        }
    }

    private static int Round(double value) => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
    private static bool Contains(StencilRect rect, double x, double y) => x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;
    private static (int Red, int Green, int Blue) Color(byte[] pixels, int width, int x, int y)
    { var i = (y * width + x) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2]); }
    private static int Luma(byte[] pixels, int width, int x, int y)
    { var (r, g, b) = Color(pixels, width, x, y); return (54 * r + 183 * g + 19 * b) / 256; }

    // Full-aspect premultiplied RGBA sampling from straight BGRA pixels.
    // Native CoreGraphics non-integral interpolation
    // still requires cross-platform verification; Windows uses centered bilinear samples.
    private static byte[] Sample(BgraFrame image, int width, int height)
    {
        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + .5) * image.Height / height - .5, 0, image.Height - 1);
            var y0 = (int)Math.Floor(sy); var y1 = Math.Min(y0 + 1, image.Height - 1); var fy = sy - y0;
            var top = image.Row(y0); var bottom = image.Row(y1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + .5) * image.Width / width - .5, 0, image.Width - 1);
                var x0 = (int)Math.Floor(sx); var x1 = Math.Min(x0 + 1, image.Width - 1); var fx = sx - x0;
                for (var c = 0; c < 4; c++)
                {
                    var channel = c == 3 ? 3 : 2 - c;
                    var a0 = top[x0 * 4 + channel] * (c == 3 ? 1 : top[x0 * 4 + 3] / 255d);
                    var a1 = top[x1 * 4 + channel] * (c == 3 ? 1 : top[x1 * 4 + 3] / 255d);
                    var b0 = bottom[x0 * 4 + channel] * (c == 3 ? 1 : bottom[x0 * 4 + 3] / 255d);
                    var b1 = bottom[x1 * 4 + channel] * (c == 3 ? 1 : bottom[x1 * 4 + 3] / 255d);
                    var a = a0 * (1 - fx) + a1 * fx;
                    var b = b0 * (1 - fx) + b1 * fx;
                    output[(y * width + x) * 4 + c] = (byte)Round(a * (1 - fy) + b * fy);
                }
            }
        }
        return output;
    }
}
