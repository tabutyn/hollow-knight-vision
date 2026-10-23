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

public sealed class ObjectReferencePanel : Grid
{
    private readonly Func<string, string> text;
    private readonly Action<string> selectClass;
    private readonly Action<SavedLabelingExample> openExample;
    private readonly LabelingObjectReferenceStore references;
    private readonly LabelingObjectAtlasStore atlasStore;
    private readonly ListBox catalog = new();
    private readonly StackPanel[] columns = [new(), new()];
    private readonly TextBlock title = new(), empty = new(), errorText = new();
    private readonly ReviewedLabelImage preview;
    private readonly Dictionary<string, BitmapSource?> images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContentControl> catalogContent = new(StringComparer.Ordinal);
    private IReadOnlyList<SavedLabelingExample> examples = [];
    private IReadOnlyList<MenuStencilLabel> definitions = [];
    private LabelingObjectAtlasSnapshot? atlas;
    private string? selectedClass;
    private Guid? selectedExample;
    private Task<LabelingObjectAtlasSnapshot?> buildTail = Task.FromResult<LabelingObjectAtlasSnapshot?>(null);
    private int revision;
    private volatile bool stopped;
    private bool updating;
    public string RootDirectory { get; }
    public string? SelectedClass => selectedClass;
    public Guid? SelectedExample => selectedExample;
    public Task PendingBuild => buildTail;
    public LabelingObjectAtlasSnapshot? Atlas => atlas;
    public event Action<LabelingObjectAtlasSnapshot?>? AtlasChanged;

    public ObjectReferencePanel(string rootDirectory, Func<string, string> text,
        Action<string> selectClass, Action<SavedLabelingExample> openExample)
    {
        RootDirectory = System.IO.Path.GetFullPath(rootDirectory); this.text = text;
        this.selectClass = selectClass; this.openExample = openExample;
        references = new(System.IO.Path.Combine(RootDirectory, "object-references.json")); atlasStore = new(RootDirectory);
        ColumnDefinitions.Add(new() { Width = new GridLength(180) }); ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new());
        Background = Brush("#15181C");
        catalog.Background = Brush("#1D2228"); catalog.Foreground = Brushes.White;
        catalog.BorderThickness = new Thickness(0); catalog.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(catalog, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(catalog, "ModelObjectCatalog"); Children.Add(catalog);
        catalog.SelectionChanged += (_, _) =>
        {
            if (updating || catalog.SelectedItem is not ListBoxItem { Tag: string identifier }) return;
            selectedClass = identifier; selectedExample = null; selectClass(identifier); ShowReferences();
        };
        var referencePane = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        Grid.SetColumn(referencePane, 1); Children.Add(referencePane);
        title.FontSize = 16; title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 10);
        title.TextWrapping = TextWrapping.Wrap; title.Foreground = Brushes.White;
        DockPanel.SetDock(title, Dock.Top); referencePane.Children.Add(title);
        errorText.Foreground = Brushes.Salmon; errorText.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(errorText, "ObjectAtlasError");
        DockPanel.SetDock(errorText, Dock.Bottom); referencePane.Children.Add(errorText);
        var content = new Grid(); content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new());
        for (var index = 0; index < 2; index++) { Grid.SetColumn(columns[index], index); content.Children.Add(columns[index]); }
        empty.Foreground = Brushes.LightSlateGray; empty.TextWrapping = TextWrapping.Wrap;
        empty.Margin = new Thickness(8); empty.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumnSpan(empty, 2); content.Children.Add(empty);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "ModelObjectReferences"); referencePane.Children.Add(scroll);
        preview = new(text, openExample, "ModelObjectScreenshot");
        Grid.SetColumn(preview, 2); Children.Add(preview);
        SizeChanged += (_, _) =>
        {
            foreach (var column in columns)
                foreach (var card in column.Children.OfType<Border>()) card.MaxHeight = Math.Max(64, ActualHeight / 4);
        };
    }

    public void SetExamples(IReadOnlyList<SavedLabelingExample> values, IReadOnlyList<MenuStencilLabel> classes, string? preferredClass = null)
    {
        if (stopped) return;
        examples = values.ToArray(); definitions = classes.ToArray(); images.Clear(); atlas = null;
        AtlasChanged?.Invoke(null);
        var desired = string.IsNullOrWhiteSpace(preferredClass) ? selectedClass : LabelingClassIdentity.CanonicalIdentifier(preferredClass);
        var chosen = definitions.FirstOrDefault(item => item.Identifier == desired)?.Identifier ?? definitions.FirstOrDefault()?.Identifier;
        if (chosen != selectedClass) selectedExample = null;
        selectedClass = chosen;
        updating = true;
        try
        {
            catalog.Items.Clear(); catalogContent.Clear();
            foreach (var definition in definitions)
            {
                var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Left };
                catalogContent[definition.Identifier] = content;
                var item = new ListBoxItem { Content = content, Tag = definition.Identifier, Padding = new Thickness(7), MinHeight = 36 };
                AutomationProperties.SetName(item, text(definition.Name));
                AutomationProperties.SetAutomationId(item, "ModelObject_" + definition.Identifier);
                catalog.Items.Add(item); if (definition.Identifier == selectedClass) catalog.SelectedItem = item;
            }
        }
        finally { updating = false; }
        RenderCatalog(); ShowReferences(); QueueAtlasBuild();
    }

    private void RenderCatalog()
    {
        foreach (var definition in definitions)
            catalogContent[definition.Identifier].Content = atlas?.Icon(definition.Identifier) is { } icon
                ? new Image { Source = icon, MaxHeight = 44, Stretch = Stretch.Uniform, ToolTip = text(definition.Name) }
                : new TextBlock { Text = text(definition.Name), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    }
    private BitmapSource? ImageFor(SavedLabelingExample example)
    {
        if (!images.TryGetValue(example.ImagePath, out var image)) images[example.ImagePath] = image = ObjectReferenceImages.Load(example.ImagePath);
        return image;
    }
    private void ShowReferences()
    {
        foreach (var column in columns) column.Children.Clear();
        title.Text = text(definitions.FirstOrDefault(item => item.Identifier == selectedClass)?.Name ?? "Objects");
        var available = selectedClass is null ? [] : LabelingObjectReferencePolicy.References(examples, selectedClass);
        var preferred = selectedClass is null ? null : LabelingObjectReferencePolicy.Preferred(examples, selectedClass, references.ExampleIdentifier(selectedClass))?.Example.Id;
        if (!available.Any(item => item.Example.Id == selectedExample)) selectedExample = preferred;
        empty.Text = text("No positive reference boxes."); empty.Visibility = available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < available.Count; index++)
        {
            var reference = available[index]; var card = new Border { Margin = new Thickness(2), Padding = new Thickness(3),
                BorderThickness = new Thickness(1), BorderBrush = reference.Example.Id == selectedExample ? Brush("#4A9DD8") : Brush("#3C424B"),
                Background = reference.Example.Id == selectedExample ? Brush("#245B86") : Brush("#20252B"), MaxHeight = Math.Max(64, ActualHeight / 4) };
            var layout = new Grid(); card.Child = layout;
            var crop = ImageFor(reference.Example) is { } image ? ObjectReferenceImages.Crop(image, reference.Annotation) : null;
            var select = new Button { Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = crop is null ? new TextBlock { Text = "⚠", Foreground = Brushes.LightSlateGray, Margin = new Thickness(16) }
                    : new Image { Source = crop, Stretch = Stretch.Uniform, MinHeight = 28 } };
            AutomationProperties.SetAutomationId(select, "ObjectReference_" + reference.Annotation.Id.ToString("N"));
            AutomationProperties.SetName(select, text("Select a reference screenshot."));
            select.Click += (_, _) => { selectedExample = reference.Example.Id; ShowReferences(); };
            layout.Children.Add(select);
            var star = new Button { Content = reference.Example.Id == preferred ? "★" : "☆", Foreground = Brushes.Gold,
                Background = Brush("#BB15181C"), Padding = new Thickness(4, 0, 4, 0), FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Use as object reference" };
            AutomationProperties.SetAutomationId(star, "ObjectReferenceStar_" + reference.Annotation.Id.ToString("N"));
            AutomationProperties.SetName(star, "Use as object reference");
            star.Click += (_, _) => SetReference(reference.Example.Id);
            Point? dragStart = null;
            star.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(star);
            star.PreviewMouseMove += (_, e) =>
            {
                if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || selectedClass is null) return;
                var delta = e.GetPosition(star) - start;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragStart = null; DragDrop.DoDragDrop(star, "reference-star:" + selectedClass, DragDropEffects.Move); e.Handled = true;
            };
            star.PreviewMouseLeftButtonUp += (_, _) => dragStart = null; layout.Children.Add(star);
            card.AllowDrop = true;
            card.DragOver += (_, e) => { e.Effects = IsReferencePayload(e.Data.GetData(DataFormats.StringFormat) as string) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            card.Drop += (_, e) => { e.Handled = AcceptReferenceDrop(e.Data.GetData(DataFormats.StringFormat) as string, reference.Example.Id); };
            columns[index % 2].Children.Add(card);
        }
        DrawPreview();
    }
    private bool IsReferencePayload(string? payload) => selectedClass is not null && payload == "reference-star:" + selectedClass;
    public bool AcceptReferenceDrop(string? payload, Guid example) => IsReferencePayload(payload) && SetReference(example);
    private bool SetReference(Guid example)
    {
        if (stopped || selectedClass is null || !LabelingObjectReferencePolicy.References(examples, selectedClass).Any(item => item.Example.Id == example)) return false;
        try { references.SetExampleIdentifier(selectedClass, example); ShowReferences(); QueueAtlasBuild(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errorText.Text = error.Message; return false; }
    }
    private void DrawPreview()
    {
        var example = examples.FirstOrDefault(item => item.Id == selectedExample);
        preview.SetExample(example, selectedClass, definitions.Select(item => item.Identifier), atlas);
    }

    private void QueueAtlasBuild()
    {
        var generation = ++revision;
        var identifiers = definitions.Select(item => item.Identifier).ToArray();
        var input = new LabelingObjectAtlasBuildInput(examples.ToArray(), identifiers, references.ExampleIdentifiers(identifiers));
        buildTail = buildTail.ContinueWith(previous =>
        {
            _ = previous.Exception;
            if (stopped || generation != Volatile.Read(ref revision)) return Task.FromResult<LabelingObjectAtlasSnapshot?>(null);
            var completion = new TaskCompletionSource<LabelingObjectAtlasSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => { try { completion.SetResult(atlasStore.LoadOrBuild(input)); } catch (Exception error) { completion.SetException(error); } })
                { IsBackground = true, Name = "Hollow Knight Vision object atlas" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _ = ApplyAtlasAsync(buildTail, generation);
    }
    private async Task ApplyAtlasAsync(Task<LabelingObjectAtlasSnapshot?> task, int generation)
    {
        try
        {
            var result = await task;
            if (stopped || generation != revision) return;
            atlas = result; errorText.Text = ""; RenderCatalog(); preview.SetAtlas(atlas); AtlasChanged?.Invoke(atlas);
        }
        catch (Exception error) { if (!stopped && generation == revision) errorText.Text = $"Object atlas failed: {error.Message}"; }
    }
    public void StopAndDrain()
    {
        stopped = true; ++revision;
        try { buildTail.GetAwaiter().GetResult(); } catch (Exception) { /* Observed by ApplyAtlasAsync; shutdown still drains writes. */ }
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
