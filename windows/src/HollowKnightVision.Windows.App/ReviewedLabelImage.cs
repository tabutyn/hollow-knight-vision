using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>Shared model-review screenshot, class colors, selected-box emphasis and icon legend.</summary>
public sealed class ReviewedLabelImage : Grid
{
    private readonly Canvas canvas = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly StackPanel legend = new() { Margin = new Thickness(7), IsHitTestVisible = false };
    private readonly Func<string, string> text;
    private readonly Action<SavedLabelingExample> open;
    private SavedLabelingExample? example;
    private BitmapSource? image;
    private string? selectedClass;
    private LabelingVisualIdentity identity = new();
    private LabelingObjectAtlasSnapshot? atlas;
    private IReadOnlyList<LabelingReviewBox> predictions = [];
    private LabelingReviewErrors? reviewErrors;
    public Button OpenButton { get; } = new() { Background = Brushes.Black, Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    public ReviewedLabelImage(Func<string, string> text, Action<SavedLabelingExample> open, string automationId)
    {
        this.text = text; this.open = open;
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = new GridLength(150) });
        // MainWindow's ordinary button template centers content. A canvas has
        // no desired size, so it needs its own stretching presenter to receive
        // the available preview area under both the app and native themes.
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
        OpenButton.Template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
        OpenButton.Content = canvas; OpenButton.ToolTip = text("Open screenshot in Label");
        AutomationProperties.SetAutomationId(OpenButton, automationId);
        AutomationProperties.SetName(OpenButton, text("Open screenshot in Label")); Children.Add(OpenButton);
        OpenButton.Click += (_, _) => { if (image is not null && example is not null) this.open(example); };
        var scroll = new ScrollViewer { Content = legend, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(scroll, "Object color legend"); Grid.SetColumn(scroll, 1); Children.Add(scroll);
        canvas.SizeChanged += (_, _) => Draw();
    }
    public void SetExample(SavedLabelingExample? value, string? selected, IEnumerable<string>? customClasses = null,
        LabelingObjectAtlasSnapshot? iconAtlas = null, IReadOnlyList<LabelingReviewBox>? reviewPredictions = null,
        LabelingReviewErrors? errors = null)
    {
        example = value; selectedClass = string.IsNullOrWhiteSpace(selected) ? null : LabelingClassIdentity.CanonicalIdentifier(selected);
        image = value is null ? null : ObjectReferenceImages.Load(value.ImagePath);
        identity = new(customClasses); atlas = iconAtlas; predictions = reviewPredictions ?? []; reviewErrors = errors;
        OpenButton.IsEnabled = image is not null; Draw(); DrawLegend();
    }
    public void SetAtlas(LabelingObjectAtlasSnapshot? value) { atlas = value; DrawLegend(); }
    private SolidColorBrush ColorFor(string identifier)
    {
        var color = identity.Rgb(identifier);
        return new(Color.FromRgb((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255)));
    }
    private void Draw()
    {
        canvas.Children.Clear();
        if (image is null || example is null)
        {
            canvas.Children.Add(new TextBlock { Text = text(example is null ? "Select a screenshot." : "Image unavailable."),
                Foreground = Brushes.LightSlateGray, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap }); return;
        }
        var fitted = StencilPreviewPresentation.FitImage(image.PixelWidth, image.PixelHeight, canvas.ActualWidth, canvas.ActualHeight);
        var picture = new Image { Source = image, Width = fitted.Width, Height = fitted.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        Canvas.SetLeft(picture, fitted.X); Canvas.SetTop(picture, fitted.Y); canvas.Children.Add(picture);
        if (reviewErrors is { } errors)
        {
            foreach (var box in errors.FalsePositives) DrawError(box, fitted, Brushes.Red, "FP", dashed: false);
            foreach (var box in errors.FalseNegatives) DrawError(box, fitted, Brushes.Orange, "FN", dashed: true);
            return;
        }
        foreach (var annotation in example.Manifest.Annotations)
        {
            if (StencilPreviewPresentation.ProjectClipped(annotation.X, annotation.Y, annotation.Width, annotation.Height, fitted) is not { } rect) continue;
            var identifier = LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier);
            var box = new Rectangle { Width = Math.Max(0, rect.Width), Height = Math.Max(0, rect.Height),
                Stroke = annotation.IsHardNegative ? Brushes.HotPink : ColorFor(identifier), StrokeThickness = identifier == selectedClass ? 3 : 2, IsHitTestVisible = false };
            Canvas.SetLeft(box, rect.X); Canvas.SetTop(box, rect.Y); canvas.Children.Add(box);
        }
        foreach (var prediction in predictions)
        {
            if (StencilPreviewPresentation.ProjectClipped(prediction.X, prediction.Y, prediction.Width, prediction.Height, fitted) is not { } rect) continue;
            var box = new Rectangle { Width = rect.Width, Height = rect.Height,
                Stroke = Brushes.Yellow, StrokeThickness = 2, StrokeDashArray = new([3, 2]), IsHitTestVisible = false };
            Canvas.SetLeft(box, rect.X); Canvas.SetTop(box, rect.Y); canvas.Children.Add(box);
        }
    }

    private void DrawError(LabelingReviewBox error, LabelBoxBounds fitted, Brush color, string label, bool dashed)
    {
        if (StencilPreviewPresentation.ProjectClipped(error.X, error.Y, error.Width, error.Height, fitted) is not { } rect)
            return;
        var box = new Rectangle
        {
            Width = rect.Width, Height = rect.Height, Stroke = color, StrokeThickness = 2.5, IsHitTestVisible = false
        };
        if (dashed) box.StrokeDashArray = new([6, 4]);
        Canvas.SetLeft(box, rect.X);
        Canvas.SetTop(box, rect.Y);
        canvas.Children.Add(box);
        var tag = new TextBlock
        {
            Text = label, FontSize = 10, FontWeight = FontWeights.Bold, Padding = new Thickness(3, 0, 3, 0),
            Foreground = Brushes.Black, Background = color, IsHitTestVisible = false
        };
        Canvas.SetLeft(tag, rect.X);
        Canvas.SetTop(tag, rect.Y);
        canvas.Children.Add(tag);
    }
    private void DrawLegend()
    {
        legend.Children.Clear();
        var identifiers = (reviewErrors is null ? example?.Manifest.Annotations ?? [] : [])
            .Select(annotation => LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier))
            .Distinct(StringComparer.Ordinal).OrderBy(LabelingVisualIdentity.DisplayName, StringComparer.Ordinal).ToArray();
        ColumnDefinitions[1].Width = new GridLength(identifiers.Length == 0 ? 0 : 150);
        foreach (var identifier in identifiers)
        {
            var color = ColorFor(identifier); var fill = color.Clone(); fill.Opacity = .35;
            var swatch = new Border { Width = 24, Height = 18, BorderThickness = new Thickness(2), BorderBrush = color, Background = fill, Margin = new Thickness(0, 0, 6, 0) };
            if (atlas?.Icon(identifier) is { } icon) swatch.Child = new Image { Source = icon, Stretch = Stretch.Uniform, Margin = new Thickness(2) };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) }; DockPanel.SetDock(swatch, Dock.Left); row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = text(LabelingVisualIdentity.DisplayName(identifier)), Foreground = Brushes.White,
                FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = identifier }); legend.Children.Add(row);
        }
    }
}
