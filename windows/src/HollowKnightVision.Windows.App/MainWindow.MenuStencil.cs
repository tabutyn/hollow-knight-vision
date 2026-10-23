using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private static void DrawMenuStencil(Canvas canvas, MenuStencilResult result)
    {
        void Stroke(StencilRect area, Color color, double thickness = 1, bool dashed = false)
        {
            if (!area.IsFinite || area.IsEmpty) return;
            // WPF draws inside its shape bounds. Keep the Mac inset centerline
            // (0.5px, even for the accepted 2px selector stroke).
            var rectangle = new Rectangle
            {
                Width = Math.Max(thickness, area.Width - 1 + thickness), Height = Math.Max(thickness, area.Height - 1 + thickness),
                Stroke = new SolidColorBrush(color), StrokeThickness = thickness,
                StrokeDashArray = dashed ? new DoubleCollection([4, 3]) : null
            };
            Canvas.SetLeft(rectangle, area.X + .5 - thickness / 2); Canvas.SetTop(rectangle, area.Y + .5 - thickness / 2);
            canvas.Children.Add(rectangle);
        }
        foreach (var region in result.SelectorSearchRegions)
        {
            if (!region.Rect.IsFinite || region.Rect.IsEmpty) continue;
            var fill = new Rectangle { Width = region.Rect.Width, Height = region.Rect.Height, Fill = new SolidColorBrush(Color.FromArgb(5, 26, 191, 255)) };
            Canvas.SetLeft(fill, region.Rect.X); Canvas.SetTop(fill, region.Rect.Y); canvas.Children.Add(fill);
            Stroke(region.Rect, Color.FromArgb(209, 26, 191, 255), dashed: true);
        }
        foreach (var match in result.Anchors)
        {
            if (match.Reference?.MakeFrame() is { } frame)
            {
                var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
                bitmap.Freeze();
                var image = new Image { Source = bitmap, Width = match.Rect.Width, Height = match.Rect.Height, Opacity = .5, Stretch = Stretch.Fill };
                Canvas.SetLeft(image, match.Rect.X); Canvas.SetTop(image, match.Rect.Y); canvas.Children.Add(image);
            }
            Stroke(match.Reference?.VerifiedBounds(match.Rect) ?? match.Rect,
                match.Confidence >= MenuStencilTracker.AnchorThreshold ? Color.FromArgb(242, 26, 255, 89) : Color.FromArgb(204, 255, 38, 38));
        }
        foreach (var match in result.SelectorCandidates)
            Stroke(match.Rect, result.SelectedPairAccepted ? Color.FromRgb(255, 204, 13) : Color.FromArgb(115, 255, 51, 51), result.SelectedPairAccepted ? 2 : 1);
    }
}
