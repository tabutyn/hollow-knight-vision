using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public sealed class ScreenshotReviewRow(SavedLabelingExample example, LabelingScreenshotReviewSession session) : INotifyPropertyChanged
{
    public SavedLabelingExample Example { get; } = example;
    public Brush Outline => session.Outline(Example.Id) switch
    { LabelingScreenshotOutline.Breach => Brushes.IndianRed, LabelingScreenshotOutline.Exempt => Brushes.DodgerBlue,
        LabelingScreenshotOutline.Selected => Brushes.Cyan, _ => Brushes.Transparent };
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Outline)));
}

// Created only for realized ListBox items; recycling releases old decoded images.
public sealed class ScreenshotReviewThumbnail : Border
{
    private ScreenshotReviewRow? row;
    public ScreenshotReviewThumbnail()
    {
        Padding = new Thickness(3); BorderThickness = new Thickness(3); Margin = new Thickness(3); Height = 144;
        DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
    }
    private void Detach() { if (row is not null) row.PropertyChanged -= Changed; row = null; Child = null; }
    private void Attach()
    {
        if (ReferenceEquals(row, DataContext)) return;
        Detach(); row = DataContext as ScreenshotReviewRow;
        if (row is null) return;
        row.PropertyChanged += Changed; BorderBrush = row.Outline;
        var image = ObjectReferenceImages.Load(row.Example.ImagePath);
        Child = image is null ? new TextBlock { Text = "Image unavailable", Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap }
            : new Image { Source = image, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(this, $"Screenshot {row.Example.Id:D}");
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (row is not null) BorderBrush = row.Outline; }
}

public sealed class ScreenshotReviewPanel : Grid
{
    private readonly LabelingScreenshotReviewSession session;
    private readonly Func<string, string> text;
    private readonly Func<SavedLabelingExample, bool> confirmDeletion;
    private readonly Action<LabelingScreenshotDeletionResult> deleted;
    private readonly Action changed;
    private readonly ListBox thumbnails = new();
    private readonly UniformGrid terms = new() { Columns = 3, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
    private readonly Button delete = new() { Foreground = Brushes.Salmon, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 6, 12, 6) };
    private readonly ReviewedLabelImage preview;
    private readonly TextBlock heading;
    private IReadOnlyList<ScreenshotReviewRow> rows = [];
    private string? selectedClass;
    private string[] customClasses = [];
    private LabelingObjectAtlasSnapshot? atlas;
    private bool updating;
    private static readonly ControlTemplate ContractTemplate = CreateContractTemplate();
    public LabelingScreenshotReviewSession Session => session;

    private static ControlTemplate CreateContractTemplate()
    {
        // A satisfied check is non-clickable, but its green status must remain readable.
        // Do not inherit native white disabled chrome or the toolbar's faded opacity.
        var border = new FrameworkElementFactory(typeof(Border), "ContractTermBorder");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(38, 55, 67)), "ContractTermBorder"));
        template.Triggers.Add(hover); return template;
    }

    public ScreenshotReviewPanel(LabelingScreenshotReviewSession session, Func<string, string> text,
        Action<SavedLabelingExample> open, Func<SavedLabelingExample, bool> confirmDeletion,
        Action changed, Action<LabelingScreenshotDeletionResult> deleted)
    {
        this.session = session; this.text = text; this.confirmDeletion = confirmDeletion; this.changed = changed; this.deleted = deleted;
        ColumnDefinitions.Add(new() { Width = new GridLength(270) }); ColumnDefinitions.Add(new());
        Background = new SolidColorBrush(Color.FromRgb(21, 24, 28));
        thumbnails.Background = new SolidColorBrush(Color.FromRgb(29, 34, 40)); thumbnails.BorderThickness = new Thickness(0);
        thumbnails.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(thumbnails, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(thumbnails, true); VirtualizingPanel.SetIsVirtualizing(thumbnails, true);
        VirtualizingPanel.SetVirtualizationMode(thumbnails, VirtualizationMode.Recycling);
        thumbnails.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        thumbnails.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(ScreenshotReviewThumbnail)) };
        AutomationProperties.SetAutomationId(thumbnails, "ReviewScreenshots"); Children.Add(thumbnails);
        thumbnails.SelectionChanged += (_, _) =>
        {
            if (updating || thumbnails.SelectedItem is not ScreenshotReviewRow row) return;
            session.Select(row.Example.Id); ShowSelection();
        };
        var detail = new Grid { Margin = new Thickness(12) }; Grid.SetColumn(detail, 1); Children.Add(detail);
        detail.RowDefinitions.Add(new() { Height = new GridLength(3, GridUnitType.Star), MaxHeight = 390 });
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto }); detail.RowDefinitions.Add(new() { Height = new GridLength(2, GridUnitType.Star) });
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto }); detail.RowDefinitions.Add(new() { Height = GridLength.Auto });
        preview = new(text, open, "ReviewOpenScreenshot"); detail.Children.Add(preview);
        heading = new TextBlock { Text = text("Contracts"), Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) };
        Grid.SetRow(heading, 1); detail.Children.Add(heading);
        var scroll = new ScrollViewer { Content = terms, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); detail.Children.Add(scroll);
        AutomationProperties.SetAutomationId(error, "ReviewError"); Grid.SetRow(error, 3); detail.Children.Add(error);
        delete.Content = text("Delete Screenshot"); AutomationProperties.SetAutomationId(delete, "ReviewDeleteScreenshot");
        delete.Click += (_, _) => DeleteSelected(); Grid.SetRow(delete, 4); detail.Children.Add(delete);
    }
    public void Refresh(string? selected, IEnumerable<string>? custom = null, LabelingObjectAtlasSnapshot? iconAtlas = null)
    {
        selectedClass = selected; customClasses = custom?.ToArray() ?? []; atlas = iconAtlas;
        heading.Text = text("Contracts"); delete.Content = text("Delete Screenshot");
        preview.OpenButton.ToolTip = text("Open screenshot in Label");
        rows = session.Examples.Select(example => new ScreenshotReviewRow(example, session)).ToArray();
        updating = true;
        try { thumbnails.ItemsSource = rows; thumbnails.SelectedItem = rows.FirstOrDefault(row => row.Example.Id == session.Selected?.Id); }
        finally { updating = false; }
        ShowSelection();
    }
    public void SetAtlas(LabelingObjectAtlasSnapshot? value) { atlas = value; preview.SetAtlas(value); }
    public void Move(int offset)
    {
        session.Move(offset); updating = true;
        try { thumbnails.SelectedItem = rows.FirstOrDefault(row => row.Example.Id == session.Selected?.Id); }
        finally { updating = false; }
        ShowSelection();
    }
    private void ShowSelection()
    {
        foreach (var row in rows) row.Refresh();
        if (thumbnails.SelectedItem is { } selected) thumbnails.ScrollIntoView(selected);
        var example = session.Selected; preview.SetExample(example, selectedClass, customClasses, atlas);
        delete.IsEnabled = example is not null; terms.Children.Clear();
        if (example is null) return;
        foreach (var evaluation in session.Evaluations(example.Id))
        {
            var label = new TextBlock { Text = (evaluation.IsExempt ? "◆ " : evaluation.IsSatisfied ? "✓ " : "✕ ") + evaluation.Title,
                TextWrapping = TextWrapping.Wrap, Foreground = evaluation.IsExempt ? Brushes.DodgerBlue : evaluation.IsSatisfied ? Brushes.LightGreen : Brushes.Salmon };
            var button = new Button { Content = label, Padding = new Thickness(6), Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Left,
                Template = ContractTemplate,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), IsEnabled = !evaluation.IsSatisfied || evaluation.IsExempt,
                ToolTip = evaluation.IsExempt ? "Remove exemption" : evaluation.IsSatisfied ? evaluation.Title : "Exempt this check" };
            AutomationProperties.SetAutomationId(button, "Contract_" + evaluation.Id); AutomationProperties.SetName(button, evaluation.Title);
            button.Click += (_, _) => Toggle(example.Id, evaluation.Id); terms.Children.Add(button);
        }
    }
    private void Toggle(Guid example, string rule)
    {
        try { if (!session.ToggleExemption(example, rule)) return; error.Text = ""; ShowSelection(); changed(); }
        catch (Exception failure) { error.Text = $"Exemption failed: {failure.Message}"; }
    }
    private void DeleteSelected()
    {
        if (session.Selected is not { } example || !confirmDeletion(example)) return;
        var result = session.DeleteConfirmed([example.Id]);
        error.Text = result.Failures.Count == 0 ? "" : $"Delete screenshot failed: {result.Failures.Values.First()}";
        Refresh(selectedClass, customClasses, atlas); changed(); deleted(result);
    }
}
