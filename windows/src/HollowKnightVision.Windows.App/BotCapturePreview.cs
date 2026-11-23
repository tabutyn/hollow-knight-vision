using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class BotCapturePreview
{
    internal static void Draw(Canvas canvas, BotCaptureReviewFrame frame, ViewportSize size)
    {
        canvas.Children.Clear();
        if (frame.Terrain is not { Width: > 0, Height: > 0 } terrain || !size.IsUsable) return;
        var scaleX = size.Width / terrain.Width;
        var scaleY = size.Height / terrain.Height;
        var brush = frame.Authored ? Brushes.SpringGreen : Brushes.DeepSkyBlue;
        foreach (var floor in terrain.Floors) Add(floor.MinimumX, floor.Y, floor.MaximumX, floor.Y, brush);
        foreach (var wall in terrain.Walls) Add(wall.X, wall.BottomY, wall.X, wall.TopY, brush);
        foreach (var door in terrain.Doors ?? []) Add(door.X1, door.Y1, door.X2, door.Y2, brush);
        foreach (var spike in terrain.Spikes ?? []) Add(spike.X1, spike.Y1, spike.X2, spike.Y2, brush);

        var inputs = frame.Features;
        if (inputs.Count != BotRecordingContract.FeatureNames.Count || inputs[4] <= 0) return;
        AddActor(inputs[0], inputs[1], inputs[2], inputs[3], Brushes.Gold);
        for (var i = 96; i < BotRecordingContract.LegacyWidth; i += 6)
            if (inputs[i + 5] > 0)
                AddActor(inputs[0] + inputs[i], inputs[1] + inputs[i + 1], inputs[i + 2], inputs[i + 3], Brushes.Orange);

        void Add(double x1, double y1, double x2, double y2, Brush color)
        {
            if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2)) return;
            canvas.Children.Add(new Line
            {
                X1 = x1 * scaleX, Y1 = size.Height - y1 * scaleY,
                X2 = x2 * scaleX, Y2 = size.Height - y2 * scaleY,
                Stroke = color, StrokeThickness = 2, IsHitTestVisible = false
            });
        }

        void AddActor(double x, double y, double width, double height, Brush color)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
                || width <= 0 || height <= 0) return;
            var w = width * terrain.Width * scaleX;
            var h = height * terrain.Width * scaleY;
            var box = new Rectangle { Width = w, Height = h, Stroke = color, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(box, x * terrain.Width * scaleX - w / 2);
            Canvas.SetTop(box, size.Height - y * terrain.Width * scaleY - h / 2);
            canvas.Children.Add(box);
        }
    }
}
