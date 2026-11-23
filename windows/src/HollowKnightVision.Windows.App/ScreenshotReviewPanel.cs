using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public sealed class ScreenshotReviewRow(SavedLabelingExample example, LabelingScreenshotReviewSession session) : INotifyPropertyChanged
{
    public SavedLabelingExample Example { get; } = example;
    public Brush Outline => session.SelectedIdentifier == Example.Id ? Brushes.Cyan : Brushes.Transparent;
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
    private readonly ListBox thumbnails = new();
    private readonly TextBlock error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
    private readonly Button delete = new() { Foreground = Brushes.Salmon, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 6, 12, 6) };
    private readonly ReviewedLabelImage preview;
    private IReadOnlyList<ScreenshotReviewRow> rows = [];
    private string? selectedClass;
    private string[] customClasses = [];
    private LabelingObjectAtlasSnapshot? atlas;
    private bool updating;
    public LabelingScreenshotReviewSession Session => session;

    public ScreenshotReviewPanel(LabelingScreenshotReviewSession session, Func<string, string> text,
        Action<SavedLabelingExample> open, Func<SavedLabelingExample, bool> confirmDeletion,
        Action<LabelingScreenshotDeletionResult> deleted)
    {
        this.session = session; this.text = text; this.confirmDeletion = confirmDeletion; this.deleted = deleted;
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
        detail.RowDefinitions.Add(new());
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto });
        preview = new(text, open, "ReviewOpenScreenshot"); detail.Children.Add(preview);
        AutomationProperties.SetAutomationId(error, "ReviewError"); Grid.SetRow(error, 1); detail.Children.Add(error);
        delete.Content = text("Delete Screenshot"); AutomationProperties.SetAutomationId(delete, "ReviewDeleteScreenshot");
        delete.Click += (_, _) => DeleteSelected(); Grid.SetRow(delete, 2); detail.Children.Add(delete);
    }
    public void Refresh(string? selected, IEnumerable<string>? custom = null, LabelingObjectAtlasSnapshot? iconAtlas = null)
    {
        selectedClass = selected; customClasses = custom?.ToArray() ?? []; atlas = iconAtlas;
        delete.Content = text("Delete Screenshot");
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
        delete.IsEnabled = example is not null;
    }

    private void DeleteSelected()
    {
        if (session.Selected is not { } example || !confirmDeletion(example)) return;
        var result = session.DeleteConfirmed([example.Id]);
        error.Text = result.Failures.Count == 0 ? "" : $"Delete screenshot failed: {result.Failures.Values.First()}";
        Refresh(selectedClass, customClasses, atlas); deleted(result);
    }
}
