namespace HollowKnightVision.Windows.Core;

/// <summary>Finds a controller tutorial in the bottom band of the game capture.</summary>
public static class GameplayTutorialPromptMask
{
    private const int ReferenceWidth = 640;
    private const int ReferenceHeight = 360;
    private const int BandTop = 300;

    public static StencilRect? Find(BgraFrame frame)
    {
        var scaleX = frame.Width / (double)ReferenceWidth;
        var scaleY = frame.Height / (double)ReferenceHeight;

        bool Bright(int x, int y, byte threshold = 140)
        {
            var sampleX = (int)((x + .5) * scaleX);
            var sampleY = (int)((BandTop + y + .5) * scaleY);
            var i = sampleY * frame.Stride + sampleX * BgraFrame.BytesPerPixel;
            var pixels = frame.Pixels;
            return pixels[i + 3] > 200 && pixels[i] > threshold
                && pixels[i + 1] > threshold && pixels[i + 2] > threshold;
        }

        // The prompt is anchored below screen center. Leave room for scaling and font differences.
        for (var y = 16; y <= 30; y++)
        {
            for (var x = 280; x <= 300; x++)
            {
                if (!Bright(x, y - 6) || !Bright(x, y + 6) || !Bright(x - 6, y) || !Bright(x + 6, y))
                    continue;

                if (Bright(x, y - 9) || Bright(x, y + 9) || Bright(x - 9, y) || Bright(x + 9, y))
                    continue;

                var circle = 0;
                var corners = 0;
                for (var dy = -8; dy <= 8; dy++)
                {
                    for (var dx = -8; dx <= 8; dx++)
                    {
                        if (!Bright(x + dx, y + dy)) continue;
                        if (dx * dx + dy * dy <= 49)
                            circle++;
                        else if (Math.Abs(dx) >= 7 && Math.Abs(dy) >= 7)
                            corners++;
                    }
                }
                if (circle < 85 || corners > 3) continue;

                // Require separate strokes to the right so scenery around the Knight cannot stand in for a label.
                var ink = 0;
                var strokes = 0;
                var priorStroke = false;
                for (var tx = x + 17; tx <= x + 70; tx++)
                {
                    var columnInk = 0;
                    for (var ty = y - 7; ty <= y + 7; ty++)
                        if (Bright(tx, ty, threshold: 100)) columnInk++;
                    ink += columnInk;
                    var stroke = columnInk >= 2;
                    if (stroke && !priorStroke) strokes++;
                    priorStroke = stroke;
                }
                if (ink >= 20 && strokes >= 3)
                    return new((x - 12) * scaleX, (BandTop + y - 12) * scaleY, 112 * scaleX, 24 * scaleY);
            }
        }
        return null;
    }
}
