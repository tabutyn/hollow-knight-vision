using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class BotWallContactPresentation
{
    private static readonly Brush Orange = ContactBrush();

    internal static void DrawGameplay(Canvas canvas, IReadOnlyList<BotBlockadeMark> marks, LiveWorldSnapshot graph,
        int room, GroundFeatureOverlayPresentation.AtlasProjection atlas)
    {
        if (!double.IsFinite(atlas.Scale) || atlas.Scale <= 0) { Clear(canvas); return; }
        Draw(canvas, marks, mark => BotAtlasBlockadeProjection.PlacedOrigin(graph, room, mark.Space) is { } origin
            ? (atlas.ToCanvas(new(origin.X + mark.X, origin.Y + mark.Y)), mark.ViewWidth * atlas.Scale) : null);
    }

    internal static void DrawHacker(Canvas canvas, IReadOnlyList<BotBlockadeMark> marks,
        GroundTruthCameraTransform camera, ViewportRect live)
    {
        var width = camera.FrameSize.Width;
        var height = camera.FrameSize.Height;
        if (!live.IsUsable || width <= 0 || height <= 0) { Clear(canvas); return; }
        var scope = "hacker/" + camera.SceneName;
        var image = new GroundTruthRect(0, 0, width, height);
        Draw(canvas, marks, mark =>
        {
            if (mark.Space != scope) return null;
            var center = camera.WorldToAtlas(new(mark.X, mark.Y), image);
            return (new Point(live.X + center.X * live.Width / width, live.Bottom - center.Y * live.Height / height),
                mark.ViewWidth * camera.PixelsPerWorldUnitX * live.Width / width);
        });
    }

    internal static void Clear(Canvas canvas)
    {
        if (canvas.Children.Count > 0) canvas.Children.Clear();
        canvas.Visibility = Visibility.Collapsed;
    }

    private static void Draw(Canvas canvas, IReadOnlyList<BotBlockadeMark> marks,
        Func<BotBlockadeMark, (Point Center, double Width)?> project)
    {
        canvas.Children.Clear();
        foreach (var mark in marks)
        {
            if (mark.Crossed || project(mark) is not { } stroke
                || !double.IsFinite(stroke.Center.X) || !double.IsFinite(stroke.Center.Y)
                || !double.IsFinite(stroke.Width) || stroke.Width <= 0) continue;
            var halfHeight = .035 * stroke.Width;
            canvas.Children.Add(new Line
            {
                X1 = stroke.Center.X, X2 = stroke.Center.X,
                Y1 = stroke.Center.Y - halfHeight, Y2 = stroke.Center.Y + halfHeight,
                Stroke = Orange, StrokeThickness = 1.5, IsHitTestVisible = false
            });
        }
        canvas.Visibility = canvas.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush ContactBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 133, 31));
        brush.Freeze();
        return brush;
    }
}
