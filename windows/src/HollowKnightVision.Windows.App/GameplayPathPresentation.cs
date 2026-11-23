using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed record GameplayPathPresentation(long CaptureId, int Width, int Height, GameplayPathView View)
{
    internal GameplayPathTracker? Tracker { get; init; }
    internal double CapturedAt { get; init; }

    internal bool IsCurrent(bool enabled, string? context, long latestCaptureId, bool registered) =>
        enabled && context is "game" or "Gameplay" && CaptureId <= latestCaptureId
        && (View.Camera is not null ? registered : CaptureId == latestCaptureId);

    internal void Draw(Canvas canvas, ViewportRect live)
    {
        if (Projection(live) is not { } projection) return;
        AtlasPathPresentation.Draw(canvas, View.Strokes, View.KnightWidth, projection.Origin,
            View.SelectedObjective ?? View.Hint?.Objective, View.SelectedRoute, projection.Scale,
            View.SelectedObjective.HasValue ? null : View.Hint);
    }

    internal GameplayPathPresentation? SelectObjective(ViewportPoint pointer, ViewportRect live, double zoom)
    {
        if (Tracker is not { } tracker || !pointer.IsFinite || zoom <= 0
            || !double.IsFinite(zoom) || Projection(live) is not { } projection)
            return null;
        var (origin, scale) = projection;
        var point = new AtlasPoint((pointer.X - origin.X) / scale.Width, (origin.Y - pointer.Y) / scale.Height);
        var tolerance = Math.Min(10_000, 10 / (zoom * Math.Min(scale.Width, scale.Height)));
        return tracker.SelectObjective(View, point, tolerance) is { } selected ? this with { View = selected } : null;
    }

    private (AtlasPoint Origin, ViewportSize Scale)? Projection(ViewportRect live)
    {
        if (!live.IsUsable || Width <= 0 || Height <= 0) return null;
        var sx = live.Width / Width;
        var sy = live.Height / Height;
        var camera = View.Camera ?? View.LocalOrigin;
        var origin = new AtlasPoint(live.X - camera.X * sx, live.Y + (Height + camera.Y) * sy);
        return (origin, new(sx, sy));
    }
}
