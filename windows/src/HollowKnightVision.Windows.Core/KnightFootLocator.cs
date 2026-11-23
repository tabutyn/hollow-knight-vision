namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Measures the Knight's foot from the bright mask in the current image. A
/// previous position narrows the search during occlusion but cannot supply a measurement.
/// </summary>
public sealed class KnightFootLocator
{
    public readonly record struct Measurement(AtlasPoint Foot, bool Partial);
    private bool[] visited = [];
    private int[] queue = [];
    private Measurement? last;
    private double lastTime = double.NegativeInfinity;
    private double lastFullTime = double.NegativeInfinity;

    public void Reset()
    {
        last = null;
        lastTime = double.NegativeInfinity;
        lastFullTime = double.NegativeInfinity;
    }

    public Measurement? Locate(GroundEdgeAnalysis analysis, PixelRect? box, int direction, double time)
    {
        if (!double.IsFinite(time)) return null;
        var scale = analysis.Width / 640d;
        var prior = time > lastTime && time - lastTime <= .13 && time - lastFullTime <= .55
            ? last?.Foot : null;
        var priorBox = prior is { } foot
            ? new StencilRect(foot.X - 18 * scale, foot.Y - 38 * scale, 36 * scale, 38 * scale)
            : (StencilRect?)null;
        var observedBox = box is { Width: > 0, Height: > 0 } pixels
            ? new StencilRect(pixels.X, pixels.Y, pixels.Width, pixels.Height) : priorBox;
        var found = Find(analysis, observedBox, direction, prior);
        if (found is not { Partial: false } && box is not null && prior is { } previous
            && Find(analysis, priorBox, direction, prior) is { Partial: false } face
            && Math.Abs(face.Foot.X - previous.X) <= 18 * scale
            && Math.Abs(face.Foot.Y - previous.Y) <= 18 * scale) found = face;
        if (found is not { } measurement) return null;
        last = measurement;
        lastTime = time;
        if (!measurement.Partial) lastFullTime = time;
        return measurement;
    }

    private Measurement? Find(GroundEdgeAnalysis analysis, StencilRect? box, int direction, AtlasPoint? prior)
    {
        var width = analysis.Width;
        var height = analysis.Height;
        if (box is not { IsFinite: true, IsEmpty: false } region || width <= 0 || height <= 0
            || analysis.SourceLuma is not { } luma || luma.LongLength != (long)width * height) return null;
        var scale = width / 640d;
        var x0 = (int)Math.Clamp(Math.Floor(region.X - 9 * scale), 0, width);
        var y0 = (int)Math.Clamp(Math.Floor(region.Y - 24 * scale), 0, height);
        var sw = (int)Math.Clamp(Math.Ceiling(region.Right + 9 * scale), 0, width) - x0;
        var sh = (int)Math.Clamp(Math.Ceiling(region.Bottom + 24 * scale), 0, height) - y0;
        if (sw <= 0 || sh <= 0 || sw > 105 * scale || sh > 155 * scale) return null;

        var length = checked(sw * sh);
        if (visited.Length < length)
        {
            visited = new bool[length];
            queue = new int[length];
        }
        else Array.Clear(visited, 0, length);
        AtlasPoint? match = null;
        AtlasPoint? partial = null;
        var partialCount = 0;
        for (var sy = 0; sy < sh; sy++)
        for (var sx = 0; sx < sw; sx++)
        {
            var seed = sy * sw + sx;
            if (visited[seed] || Pixel(sx, sy) < 190) continue;
            var first = 0;
            var count = 0;
            Add(sx, sy);
            var left = sx;
            var right = sx;
            var top = sy;
            var bottom = sy;
            while (first < count)
            {
                var position = queue[first++];
                var x = position % sw;
                var y = position / sw;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                Add(x - 1, y);
                Add(x + 1, y);
                Add(x, y - 1);
                Add(x, y + 1);
            }

            var bodyWidth = right - left + 1;
            var bodyHeight = bottom - top + 1;
            if (left == 0 || right + 1 == sw || top == 0 || bottom + 1 == sh
                || bodyWidth < 4 * scale || bodyWidth > 24 * scale
                || bodyHeight < 7 * scale || bodyHeight > 30 * scale
                || count * 4 < bodyWidth * bodyHeight) continue;
            var eye = 0;
            for (var y = top + bodyHeight / 3; y <= bottom; y++)
            {
                var bright = false;
                var dark = 0;
                for (var x = left; x <= right; x++)
                {
                    var value = Pixel(x, y);
                    if (value >= 190)
                    {
                        if (bright) eye += dark;
                        bright = true;
                        dark = 0;
                    }
                    else if (bright && value <= 75) dark++;
                }
            }
            var footY = y0 + bottom + 15 * scale;
            if (footY >= height) continue;
            if (eye >= 2 && bodyWidth >= 8 * scale)
            {
                if (match is not null) return null;
                match = new(x0 + (left + right) / 2d, footY);
            }
            else if (prior is { } previous && direction is -1 or 1 && bodyWidth <= 16 * scale
                && bodyHeight <= 29 * scale && count * 3 >= bodyWidth * bodyHeight)
            {
                var x = x0 + (direction < 0 ? right : left) + direction * 7 * scale;
                if (Math.Abs(x - previous.X) <= 15 * scale && Math.Abs(footY - previous.Y) <= 27 * scale)
                {
                    partial = new(x, footY);
                    partialCount++;
                }
            }

            void Add(int x, int y)
            {
                if ((uint)x >= (uint)sw || (uint)y >= (uint)sh) return;
                var index = y * sw + x;
                if (visited[index] || Pixel(x, y) < 190) return;
                visited[index] = true;
                queue[count++] = index;
            }
        }
        return match is { } full ? new(full, false)
            : partialCount == 1 && partial is { } hidden ? new(hidden, true) : null;

        byte Pixel(int x, int y) => luma[(y + y0) * width + x + x0];
    }
}
