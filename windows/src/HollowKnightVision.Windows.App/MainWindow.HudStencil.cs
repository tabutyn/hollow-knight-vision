using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private static void DrawHudStencil(Canvas canvas, HudStencilResult hud)
    {
        void Mark(StencilRect area, Color color, bool fill = false, double thickness = 1)
        {
            if (area.IsEmpty) return;
            var rectangle = new Rectangle
            {
                Width = area.Width, Height = area.Height, Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                Fill = fill ? new SolidColorBrush(Color.FromArgb(89, color.R, color.G, color.B)) : null
            };
            Canvas.SetLeft(rectangle, area.X); Canvas.SetTop(rectangle, area.Y);
            canvas.Children.Add(rectangle);
        }
        foreach (var mask in hud.HealthMasks) Mark(mask, Color.FromRgb(255, 64, 64), fill: true);
        foreach (var mask in hud.ManaMasks) Mark(mask, Color.FromRgb(64, 204, 255), fill: true);
        if (hud.ManaMain is { } main) Mark(main.Rect, Color.FromRgb(64, 204, 255), thickness: 2);
        Mark(hud.Geo, Color.FromRgb(255, 217, 26), fill: true);
        foreach (var match in hud.Health)
            Mark(match.Rect, match.Kind == "lifeblood" ? Colors.Cyan : match.Kind == "empty" ? Colors.Orange : Colors.White);
    }
}
