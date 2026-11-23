using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed record GameplayPathPresentation(long CaptureId, int Width, int Height, GameplayPathView View)
{
    internal bool IsCurrent(bool enabled, string? context, long latestCaptureId, bool registered) =>
        enabled && context is "game" or "Gameplay" && CaptureId <= latestCaptureId
        && (View.Camera is not null ? registered : CaptureId == latestCaptureId);

    internal void Draw(Canvas canvas, ViewportRect live)
    {
        if (!live.IsUsable || Width <= 0 || Height <= 0) return;
        var sx = live.Width / Width;
        var sy = live.Height / Height;
        var camera = View.Camera ?? View.LocalOrigin;
        var origin = new AtlasPoint(live.X - camera.X * sx, live.Y + (Height + camera.Y) * sy);
        AtlasPathPresentation.Draw(canvas, View.Strokes, View.KnightWidth, origin,
            View.Hint?.Objective, [], new(sx, sy), View.Hint);
    }
}
