using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class GameTextDebugWindow : Window
{
    private readonly Action<bool> setEnabled;
    private GameTextScan? scan;
    private GameTextLogEntry[]? entries;
    private bool updating;

    internal GameTextDebugWindow(Action<bool> setEnabled)
    {
        this.setEnabled = setEnabled;
        InitializeComponent();
    }

    internal void Present(GameTextReader reader)
    {
        updating = true;
        ReadCheck.IsChecked = reader.Enabled;
        IssueText.Text = reader.Issue ?? "";
        IssueText.Visibility = reader.Issue is null ? Visibility.Collapsed : Visibility.Visible;
        LogTab.Header = $"Log ({reader.Entries.Length})";
        ProfileText.Text = reader.Profile is { } profile ? $"Save slot {profile.Slot}" : "Before save selection";
        EmptyLog.Visibility = reader.Entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(entries, reader.Entries))
        {
            var selected = (LogRows.SelectedItem as GameTextLogEntry)?.Observation.Id;
            LogRows.ItemsSource = entries = reader.Entries;
            LogRows.SelectedItem = entries.FirstOrDefault(entry => entry.Observation.Id == selected);
        }

        FrameStatus.Visibility = reader.Scan is null ? Visibility.Visible : Visibility.Collapsed;
        FrameStatus.Text = reader.Enabled ? "Waiting for a game frame" : "Text reading is off";
        PreviewView.Visibility = FrameReadings.Visibility = reader.Scan is null ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(scan, reader.Scan))
        {
            scan = reader.Scan;
            if (scan is null)
            {
                FrameImage.Source = null;
                VisibleLines.ItemsSource = null;
            }
            else
            {
                FrameGrid.Width = scan.Image.Width;
                FrameGrid.Height = scan.Image.Height;
                FrameImage.Source = MainWindow.FrameBitmap(scan.Image);
                TimingText.Text = scan.Lines.Count == 0
                    ? $"No text detected · {scan.Milliseconds:F0} ms"
                    : $"{scan.Lines.Count} lines · {scan.Milliseconds:F0} ms";
                var selected = (VisibleLines.SelectedItem as GameTextLine)?.Id;
                VisibleLines.ItemsSource = scan.Lines;
                VisibleLines.SelectedItem = scan.Lines.FirstOrDefault(line => line.Id == selected);
            }
            DrawLineBoxes();
        }
        updating = false;
    }

    private void DrawLineBoxes()
    {
        LineBoxes.Children.Clear();
        if (scan is not { } value) return;
        var selected = (VisibleLines.SelectedItem as GameTextLine)?.Id;
        foreach (var line in value.Lines)
        {
            var box = line.Bounds;
            var x = box.X * value.Image.Width;
            var y = box.Y * value.Image.Height;
            var brush = line.Id == selected ? Brushes.Orange : Brushes.Yellow;
            var outline = new Rectangle
            {
                Width = box.Width * value.Image.Width, Height = box.Height * value.Image.Height,
                Stroke = brush, StrokeThickness = 2
            };
            Canvas.SetLeft(outline, x);
            Canvas.SetTop(outline, y);
            LineBoxes.Children.Add(outline);
            var number = new TextBlock
            {
                Text = line.Id.ToString(), FontSize = 10, Foreground = Brushes.Black, Background = brush
            };
            Canvas.SetLeft(number, Math.Max(0, x - 14));
            Canvas.SetTop(number, Math.Max(0, y - 10));
            LineBoxes.Children.Add(number);
        }
    }

    private void LineSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!updating) DrawLineBoxes();
    }

    private void ReadingChanged(object sender, RoutedEventArgs e)
    {
        if (!updating && IsLoaded) setEnabled(ReadCheck.IsChecked == true);
    }
}
