using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>Mac's two capture sources and box previews, with native Windows chrome.</summary>
internal sealed class RecentFramesWindow : Window
{
    private readonly Action resume;
    internal RecentFramesWindow(IReadOnlyList<SavedLabelingExample> examples, IReadOnlyList<RecentCapturedFrame> captures,
        Func<string, string> text, Action<SavedLabelingExample> openSaved, Action<RecentCapturedFrame> openCapture, Action onResume)
    {
        resume = onResume;
        Title = text("Recent Frames"); Width = Math.Min(1280, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(760, SystemParameters.WorkArea.Height - 40); MinWidth = Math.Min(1040, Width); MinHeight = Math.Min(620, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(24, 27, 31)); Foreground = Brushes.White;
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(40, 44, 49))));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(80, 85, 92))));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        Resources.Add(typeof(Button), buttonStyle);
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var resumeButton = new Button { Content = text("Resume (L)"), Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(resumeButton, "RecentResume"); resumeButton.Click += (_, _) => Resume(); actions.Children.Add(resumeButton);
        var close = new Button { Content = text("Close") }; AutomationProperties.SetAutomationId(close, "RecentClose");
        close.Click += (_, _) => Close(); actions.Children.Add(close); DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        header.Children.Add(new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new());
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(17) }); columns.ColumnDefinitions.Add(new());
        var separator = new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(62, 66, 73)) };
        Grid.SetColumn(separator, 1); columns.Children.Add(separator);
        var savedButtons = RecentCapturedFrameLabelingPolicy.RecentLabeledFrames(examples).Select(example =>
        {
            var label = $"Open recent frame with {example.Manifest.Annotations.Count} labeled objects";
            return ThumbnailButton(LoadImage(example.ImagePath), example.Manifest.Annotations, false, label,
                "RecentSaved_" + example.Id.ToString("N"), () => { openSaved(example); Close(); });
        }).ToArray();
        var capturedButtons = captures.Select(frame => ThumbnailButton(Bitmap(frame.Image), frame.Detections.Select(item =>
                new LabelingExampleAnnotation(Guid.NewGuid(), item.ClassIdentifier, item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height)).ToArray(),
            true, $"Open captured frame with {frame.Detections.Count} predicted boxes",
            $"RecentCapture_{frame.CaptureGeneration}_{frame.SourceFrameIdentifier}", () => { openCapture(frame); Close(); })).ToArray();
        columns.Children.Add(Column(text("Recent Labeled Frames"), savedButtons, text("No Recent Labeled Frames"), text("Label an object to save a frame.")));
        var capturedColumn = Column(text("Recent Captured Frames"), capturedButtons, text("No Recent Captured Frames"), text("Resume gameplay briefly to fill the capture buffer."));
        Grid.SetColumn(capturedColumn, 2); columns.Children.Add(capturedColumn); root.Children.Add(columns); Content = root;
        PreviewKeyDown += (_, e) => { if (HandleShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat)) e.Handled = true; };
    }
    private bool HandleShortcut(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (repeat || (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        if (key is Key.L or Key.X) { Resume(); return true; }
        if (key == Key.Escape) { Close(); return true; }
        return false;
    }
    private void Resume() { Close(); resume(); }
    private static FrameworkElement Column(string title, IReadOnlyList<Button> buttons, string emptyTitle, string emptyDetail)
    {
        var pane = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); pane.Children.Add(heading);
        if (buttons.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18) };
            empty.Children.Add(new TextBlock { Text = emptyTitle, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = emptyDetail, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            pane.Children.Add(empty);
        }
        else
        {
            var thumbnails = new WrapPanel(); foreach (var button in buttons) thumbnails.Children.Add(button);
            pane.Children.Add(new ScrollViewer { Content = thumbnails, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false });
        }
        return pane;
    }
    private static Button ThumbnailButton(BitmapSource? image, IReadOnlyList<LabelingExampleAnnotation> boxes,
        bool predicted, string label, string identifier, Action open)
    {
        var button = new Button { Content = Thumbnail(image, boxes, predicted), Margin = new Thickness(4), Padding = new Thickness(1),
            ToolTip = label, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (predicted) button.BorderBrush = Brushes.MediumOrchid;
        AutomationProperties.SetName(button, label); AutomationProperties.SetAutomationId(button, identifier);
        button.Click += (_, _) => open(); return button;
    }
    internal static Canvas Thumbnail(BitmapSource? image, IReadOnlyList<LabelingExampleAnnotation> annotations, bool predicted)
    {
        var canvas = new Canvas { Width = 190, Height = 107, Background = Brushes.Black, ClipToBounds = true, IsHitTestVisible = false };
        if (image is null)
        {
            canvas.Children.Add(new TextBlock { Text = "⚠", Foreground = Brushes.LightSlateGray, FontSize = 24, Margin = new Thickness(82, 35, 0, 0) });
            return canvas;
        }
        var fitted = StencilPreviewPresentation.FitImage(image.PixelWidth, image.PixelHeight, canvas.Width, canvas.Height);
        var preview = new Image { Source = image, Width = fitted.Width, Height = fitted.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(preview, fitted.X); Canvas.SetTop(preview, fitted.Y); canvas.Children.Add(preview);
        foreach (var annotation in annotations)
        {
            var rect = StencilPreviewPresentation.Project(annotation, fitted);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var box = new Rectangle { Width = rect.Width, Height = rect.Height, StrokeThickness = 1.5,
                Stroke = predicted ? Brushes.MediumOrchid : annotation.IsHardNegative ? Brushes.HotPink : Brushes.Cyan };
            Canvas.SetLeft(box, rect.X); Canvas.SetTop(box, rect.Y); canvas.Children.Add(box);
        }
        return canvas;
    }
    private static BitmapSource Bitmap(BgraFrame frame)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        image.Freeze(); return image;
    }
    private static BitmapSource? LoadImage(string path)
    {
        try
        {
            var image = BitmapDecoder.Create(new Uri(path, UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze(); return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException)
        { return null; }
    }
}
