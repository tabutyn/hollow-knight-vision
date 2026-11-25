using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>Captured examples and independent validation results.</summary>
internal sealed class LabelingCampaignPanel : Grid
{
    internal void Update(IReadOnlyList<LabelingCampaignProgress> progress, IReadOnlyList<LabelingCampaignPoint> history,
        Func<string, string> text, Func<string, string> name)
    {
        Children.Clear(); ColumnDefinitions.Clear();
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new());
        Margin = new(0, 0, 0, 14);
        AutomationProperties.SetAutomationId(this, "labeling-campaign-progress");
        var left = new StackPanel { Margin = new(0, 0, 16, 0) };
        var right = new StackPanel(); SetColumn(right, 1); Children.Add(left); Children.Add(right);
        left.Children.Add(Label(text("campaignExamples"), true));
        var maximum = Math.Max(10d, progress.Select(item => item.Count).DefaultIfEmpty(10).Max()) + 5;
        foreach (var item in progress)
        {
            var row = new Grid { Margin = new(0, 3, 0, 3), Height = 19 };
            row.ColumnDefinitions.Add(new() { Width = new(115) }); row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = new(52) });
            var caption = Label(name(item.Identifier)); caption.TextTrimming = TextTrimming.CharacterEllipsis;
            caption.ToolTip = caption.Text; row.Children.Add(caption);
            var canvas = new Canvas { Width = 160, Height = 15 };
            canvas.Children.Add(new Rectangle { Width = 160.0 * item.Count / maximum, Height = 12, Fill = Brushes.DodgerBlue });
            var graph = new Viewbox { Child = canvas, Stretch = Stretch.Fill }; SetColumn(graph, 1); row.Children.Add(graph);
            var count = Label(item.Count.ToString()); SetColumn(count, 2); count.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(count); left.Children.Add(row);
        }
        right.Children.Add(Label(text("campaignQuality"), true));
        if (history.Count == 0) right.Children.Add(new TextBlock { Text = text("campaignNoValidation"),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver, MinHeight = 135, Margin = new(0, 12, 0, 0) });
        else
        {
            var plot = new Canvas { Width = 350, Height = 160 };
            foreach (var level in new[] { 0.0, 0.5, 1.0 })
            {
                var y = 130 - level * 115;
                plot.Children.Add(new Line { X1 = 35, X2 = 340, Y1 = y, Y2 = y, Stroke = Brushes.DimGray });
                var label = Label($"{level:P0}"); Canvas.SetTop(label, y - 8); plot.Children.Add(label);
            }
            var line = new Polyline { Stroke = Brushes.LimeGreen, StrokeThickness = 2 };
            var maxRun = Math.Max(2, history.Max(point => point.Run));
            foreach (var point in history)
            {
                var x = 35 + point.Run * 300.0 / maxRun; var y = 130 - point.Accuracy * 115;
                line.Points.Add(new(x, y));
                var dot = new Ellipse { Width = 7, Height = 7, Fill = Brushes.LimeGreen,
                    ToolTip = $"{text("campaignRun")} {point.Run}: {point.Accuracy:P1}" };
                Canvas.SetLeft(dot, x - 3.5); Canvas.SetTop(dot, y - 3.5); plot.Children.Add(dot);
            }
            plot.Children.Insert(0, line);
            var axis = Label($"{text("campaignRun")}  1–{maxRun}"); Canvas.SetLeft(axis, 100); Canvas.SetTop(axis, 140); plot.Children.Add(axis);
            right.Children.Add(new Viewbox { Child = plot, Stretch = Stretch.Uniform, MaxHeight = 165 });
        }
    }
    private static TextBlock Label(string text, bool heading = false) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = heading ? Brushes.WhiteSmoke : Brushes.Silver, FontSize = heading ? 14 : 11,
        FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
}
