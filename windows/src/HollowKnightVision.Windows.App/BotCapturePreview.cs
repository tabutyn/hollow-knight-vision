using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class BotCapturePreview
{
    internal static (bool Route, int? Blockade) Draw(Canvas canvas, BotCaptureReviewFrame frame, ViewportSize size)
    {
        canvas.Children.Clear();
        if (!size.IsUsable) return default;

        var inputs = frame.Features;
        var validFeatures = inputs.Count == BotRecordingContract.LegacyWidth || inputs.Count == BotRecordingContract.FeatureNames.Count;
        var brush = frame.Authored ? Brushes.SpringGreen : Brushes.DeepSkyBlue;
        if (frame.Terrain is { Width: > 0 and < double.PositiveInfinity, Height: > 0 and < double.PositiveInfinity } terrain)
        {
            var scaleX = size.Width / terrain.Width;
            var scaleY = size.Height / terrain.Height;
            foreach (var floor in terrain.Floors) PixelStroke(floor.MinimumX, floor.Y, floor.MaximumX, floor.Y, brush);
            foreach (var wall in terrain.Walls) PixelStroke(wall.X, wall.BottomY, wall.X, wall.TopY, brush);
            foreach (var door in terrain.Doors ?? []) PixelStroke(door.X1, door.Y1, door.X2, door.Y2, Brushes.DeepSkyBlue);
            foreach (var spike in terrain.Spikes ?? []) PixelStroke(spike.X1, spike.Y1, spike.X2, spike.Y2, Brushes.Red);

            void PixelStroke(double x1, double y1, double x2, double y2, Brush color) =>
                Stroke(x1 * scaleX, size.Height - y1 * scaleY, x2 * scaleX, size.Height - y2 * scaleY, color);
        }
        else if (validFeatures)
        {
            for (var i = 16; i < 96; i += 5)
                if (inputs[i + 4] > 0)
                    NormalizedStroke(inputs[0] + inputs[i], inputs[1] + inputs[i + 2],
                        inputs[0] + inputs[i + 1], inputs[1] + inputs[i + 2], brush);
            if (!frame.Authored && inputs.Count == BotRecordingContract.FeatureNames.Count)
                for (var i = 145; i <= 150; i += 5)
                    if (inputs[i + 4] == 1)
                        NormalizedStroke(inputs[0] + inputs[i], inputs[1] + inputs[i + 1],
                            inputs[0] + inputs[i], inputs[1] + inputs[i + 2], brush);
        }

        foreach (var cache in frame.GeoCaches) AddRing(cache, Brushes.Yellow);
        foreach (var cocoon in frame.LifebloodCocoons) AddRing(cocoon, Brushes.DeepSkyBlue);
        if (!validFeatures) return default;
        var hasKnight = inputs[4] > 0 && double.IsFinite(inputs[0]) && double.IsFinite(inputs[1]);
        if (hasKnight)
        {
            AddBox(inputs[0], inputs[1], inputs[2], inputs[3], Brushes.Gold);
            for (var i = 96; i < BotRecordingContract.LegacyWidth; i += 6)
                if (inputs[i + 5] > 0)
                    AddBox(inputs[0] + inputs[i], inputs[1] + inputs[i + 1], inputs[i + 2], inputs[i + 3], Brushes.Orange);
        }

        return (DrawPath(), DrawBlockade());

        bool DrawPath()
        {
            if (frame.PathHint is not { } hint) return false;
            var length = Math.Sqrt(hint.Dx * hint.Dx + hint.Dy * hint.Dy);
            if (!double.IsFinite(length) || length <= .001) return false;
            var dx = hint.Dx / length;
            var dy = -hint.Dy / length;
            var x = hasKnight ? inputs[0] * size.Width : size.Width / 2;
            var y = hasKnight ? size.Height - inputs[1] * size.Width : size.Height / 2;
            var endX = x + dx * size.Width * .12;
            var endY = y + dy * size.Width * .12;
            var head = Math.Max(5, size.Width * .018);
            if (!Stroke(x, y, endX, endY, Brushes.MediumOrchid, 3)) return false;
            Stroke(endX - head * dx + head * .55 * dy, endY - head * dy - head * .55 * dx, endX, endY, Brushes.MediumOrchid, 3);
            Stroke(endX - head * dx - head * .55 * dy, endY - head * dy + head * .55 * dx, endX, endY, Brushes.MediumOrchid, 3);
            return true;
        }

        int? DrawBlockade()
        {
            if (frame.Blockade is not { Mark.Direction: -1 or 1 } contact) return null;
            var x = inputs[0] + contact.Dx;
            var y = inputs[1] + contact.Dy;
            if (!NormalizedStroke(x, y - .035, x, y + .035, Brushes.Orange)) return null;
            NormalizedStroke(x - .012, y + .035, x + .012, y + .035, Brushes.Orange);
            return contact.Mark.Direction;
        }

        bool NormalizedStroke(double x1, double y1, double x2, double y2, Brush color) =>
            Stroke(x1 * size.Width, size.Height - y1 * size.Width, x2 * size.Width, size.Height - y2 * size.Width, color);

        bool Stroke(double x1, double y1, double x2, double y2, Brush color, double thickness = 2)
        {
            if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2)) return false;
            canvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = color,
                StrokeThickness = thickness, IsHitTestVisible = false
            });
            return true;
        }

        void AddRing(BotObjectBounds bounds, Brush color)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            AddShape(new Ellipse(), bounds.X, bounds.Y, Math.Max(7, bounds.Width * size.Width),
                Math.Max(7, bounds.Height * size.Width), color);
        }

        void AddBox(double x, double y, double width, double height, Brush color) =>
            AddShape(new Rectangle(), x, y, width * size.Width, height * size.Width, color);

        void AddShape(Shape shape, double x, double y, double width, double height, Brush color)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
                || width <= 0 || height <= 0) return;
            var left = x * size.Width - width / 2;
            var top = size.Height - y * size.Width - height / 2;
            if (!double.IsFinite(left) || !double.IsFinite(top)) return;
            shape.Width = width;
            shape.Height = height;
            shape.Stroke = color;
            shape.StrokeThickness = 2;
            shape.IsHitTestVisible = false;
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            canvas.Children.Add(shape);
        }
    }
}
