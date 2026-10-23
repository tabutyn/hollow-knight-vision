using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public sealed class ModelSummaryReviewPanel : Grid
{
    private readonly LabelingModelSummarySession session;
    private readonly Func<string, string> text;
    private readonly Action<string> selectClass;
    private readonly Grid body = new();
    private readonly ListBox least = new(), errors = new();
    private readonly TextBlock leastHeading = new(), errorsHeading = new(), title = new(), position = new(), empty = new(), failure = new();
    private readonly Button previous = new() { Content = "‹" }, next = new() { Content = "›" };
    private readonly ReviewedLabelImage preview;
    private string[] customClasses = [];
    private LabelingObjectAtlasSnapshot? atlas;
    private bool updating;
    public LabelingModelSummarySession Session => session;

    public ModelSummaryReviewPanel(LabelingModelSummarySession session, Func<string, string> text,
        Action<string> selectClass, Action<SavedLabelingExample> openExample)
    {
        this.session = session; this.text = text; this.selectClass = selectClass;
        Background = Brushes.Black; Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(230) }); body.ColumnDefinitions.Add(new());
        AddPriorities(least, leastHeading, "SummaryLeastRegistered", 0);
        AddPriorities(errors, errorsHeading, "SummaryMostErrors", 1);
        var detail = new Grid { Margin = new Thickness(10), Background = PanelBrush };
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto }); detail.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MaxHeight = 390 });
        detail.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(detail, 2); body.Children.Add(detail);
        var heading = new DockPanel { Margin = new Thickness(10) };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(navigation, Dock.Right);
        position.VerticalAlignment = VerticalAlignment.Center; position.Foreground = Brushes.LightSlateGray; position.Margin = new Thickness(8, 0, 8, 0);
        AutomationProperties.SetAutomationId(position, "SummaryFailurePosition");
        AutomationProperties.SetAutomationId(previous, "SummaryPreviousFailure"); AutomationProperties.SetAutomationId(next, "SummaryNextFailure");
        AutomationProperties.SetName(previous, "Previous failed screenshot"); AutomationProperties.SetName(next, "Next failed screenshot");
        previous.ToolTip = "Previous failed screenshot"; next.ToolTip = "Next failed screenshot";
        previous.Click += (_, _) => Move(-1); next.Click += (_, _) => Move(1);
        navigation.Children.Add(previous); navigation.Children.Add(position); navigation.Children.Add(next); heading.Children.Add(navigation);
        title.Foreground = Brushes.White; title.FontWeight = FontWeights.SemiBold; title.FontSize = 15;
        title.VerticalAlignment = VerticalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(title, "SummaryObjectTitle"); heading.Children.Add(title); detail.Children.Add(heading);
        preview = new(text, openExample, "SummaryOpenScreenshot") { Margin = new Thickness(10, 0, 10, 0) };
        Grid.SetRow(preview, 1); detail.Children.Add(preview);
        empty.Foreground = Brushes.LightSlateGray; empty.TextWrapping = TextWrapping.Wrap;
        empty.Margin = new Thickness(10); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(empty, "SummaryEmptyReview"); Grid.SetRow(empty, 1); detail.Children.Add(empty);
        failure.Foreground = Brushes.Salmon; failure.TextWrapping = TextWrapping.Wrap; failure.Margin = new Thickness(12); failure.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(failure, "SummaryReviewError"); Children.Add(failure);
    }
    private static Brush PanelBrush => new SolidColorBrush(Color.FromRgb(21, 24, 28));
    private void AddPriorities(ListBox list, TextBlock heading, string id, int column)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 10, 0), Background = PanelBrush };
        heading.FontSize = 15; heading.FontWeight = FontWeights.SemiBold; heading.Foreground = Brushes.White; heading.Margin = new Thickness(10);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        list.Background = Brushes.Transparent; list.BorderThickness = new Thickness(0); list.Margin = new Thickness(2);
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch; list.ItemContainerStyle = PriorityStyle();
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(list, id); panel.Children.Add(list); Grid.SetColumn(panel, column); body.Children.Add(panel);
        list.SelectionChanged += (_, _) =>
        {
            if (updating || list.SelectedItem is not ListBoxItem { Tag: LabelingObjectPrioritySummary value }) return;
            if (!session.SelectPriority(value.ClassIdentifier)) return;
            selectClass(value.ClassIdentifier); ShowSelection();
        };
    }
    private static Style PriorityStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "PriorityBorder");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty)); border.AppendChild(content);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 7, 8, 7)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(48, 52, 58)))); style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(36, 91, 134))));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(117, 190, 245)))); style.Triggers.Add(selected);
        return style;
    }
    public void Refresh(IEnumerable<string>? custom = null, LabelingObjectAtlasSnapshot? iconAtlas = null)
    {
        customClasses = custom?.ToArray() ?? []; atlas = iconAtlas;
        failure.Text = ""; failure.Visibility = Visibility.Collapsed; body.Visibility = Visibility.Visible;
        leastHeading.Text = text("Least Registrations"); errorsHeading.Text = text("Most Errors");
        updating = true;
        try { Populate(least, session.LeastRegistered, false); Populate(errors, session.MostErrors, true); }
        finally { updating = false; }
        ShowSelection();
    }
    private void Populate(ListBox list, IReadOnlyList<LabelingObjectPrioritySummary> values, bool isErrors)
    {
        list.Items.Clear();
        foreach (var value in values)
        {
            var name = text(LabelingVisualIdentity.DisplayName(value.ClassIdentifier));
            var count = isErrors ? $"{session.TruePositiveCount(value.ClassIdentifier)} TP" : value.RegistrationCount.ToString();
            var row = new DockPanel();
            var number = new TextBlock { Text = count, Foreground = !isErrors && value.RegistrationCount == 0 ? Brushes.Orange : Brushes.LightSlateGray,
                Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
            row.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var details = $"{name}: {value.RegistrationCount} labels; {session.TruePositiveCount(value.ClassIdentifier)} TP, {value.FalsePositives} FP, {value.FalseNegatives} FN; {value.FailingFrameCount} failed screenshots";
            var item = new ListBoxItem { Tag = value, Content = row, ToolTip = details, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(item, (isErrors ? "SummaryErrors_" : "SummaryLabels_") + value.ClassIdentifier);
            AutomationProperties.SetName(item, details); list.Items.Add(item);
        }
    }
    public void SetSelectedClass(string? identifier) { session.SetSelectedClass(identifier); ShowSelection(); }
    public void SetAtlas(LabelingObjectAtlasSnapshot? value) { atlas = value; preview.SetAtlas(value); }
    public void Move(int offset) { session.Move(offset); ShowSelection(); }
    public void ShowError(string message)
    {
        preview.SetExample(null, null); body.Visibility = Visibility.Collapsed;
        failure.Text = message; failure.Visibility = Visibility.Visible;
    }
    private void ShowSelection()
    {
        updating = true;
        try
        {
            foreach (var list in new[] { least, errors })
                list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(item => ((LabelingObjectPrioritySummary)item.Tag).ClassIdentifier == session.SelectedClassIdentifier);
        }
        finally { updating = false; }
        title.Text = session.ReviewClassIdentifier is { } identifier ? text(LabelingVisualIdentity.DisplayName(identifier)) : text("Errors");
        position.Text = session.PositionText; previous.IsEnabled = next.IsEnabled = session.FailureExamples.Count >= 2;
        preview.SetExample(session.SelectedFailure, session.SelectedClassIdentifier, customClasses, atlas, session.SelectedPredictions);
        preview.Visibility = session.SelectedFailure is null ? Visibility.Collapsed : Visibility.Visible;
        empty.Text = text("No failed screenshots for this object."); empty.Visibility = session.SelectedFailure is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
