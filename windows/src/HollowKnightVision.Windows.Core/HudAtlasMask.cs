namespace HollowKnightVision.Windows.Core;

public static class HudAtlasMask
{
    // Mac SceneRegions.atlasHUDMaskRects: the persistent shield protects
    // animated/temporarily missing sprites; exact masks cover longer rows.
    public static IReadOnlyList<PixelRect> Rectangles(int width, int height, HudStencilResult? hud)
    {
        return SceneRegions.PixelRectangles(width, height,
            SceneRegions.AtlasHudMaskRects(new(0, 0, width, height), hud));
    }

    internal static bool[]? Rasterize(int width, int height, IReadOnlyList<PixelRect>? rectangles)
    {
        if (rectangles is not { Count: > 0 }) return null;
        var mask = new bool[checked(width * height)];
        foreach (var rect in rectangles)
        {
            var left = Math.Clamp(rect.X, 0, width); var top = Math.Clamp(rect.Y, 0, height);
            var right = Math.Clamp((long)rect.X + rect.Width, left, width);
            var bottom = Math.Clamp((long)rect.Y + rect.Height, top, height);
            for (var y = top; y < bottom; y++) Array.Fill(mask, true, y * width + left, (int)right - left);
        }
        return mask;
    }
}
