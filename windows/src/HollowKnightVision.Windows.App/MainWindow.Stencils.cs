using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private SavedLabelingExample? stencilPreviewExample;
    private string? stencilSelectedContext;

    private void RefreshStencilsWorkspace()
    {
        initialization.Refresh();
        StencilSidebarTitle.Text = initialization.Text("stencil");
        StencilProgressText.Text = $"{initialization.CompletedScreenCount} / {initialization.ScreenCount}";
        StencilResetButton.Content = initialization.Text("reset");
        StencilEditButton.Content = initialization.Text("edit");
        StencilDeleteButton.Content = initialization.Text("deleteScreenshot");
        StencilContextList.Items.Clear();
        var lastPhase = 0;
        foreach (var context in initialization.Contexts)
        {
            var phase = StencilInitializationPolicy.Phase(context.Identifier);
            if (phase != lastPhase)
            {
                StencilContextList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = initialization.Text($"phase{phase}"), FontSize = 11,
                        FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xAA, 0xBB))
                    },
                    IsEnabled = false, Focusable = false, Padding = new Thickness(8, 14, 8, 4)
                });
                lastPhase = phase;
            }
            var complete = !initialization.Steps.Any(step => step.ContextIdentifier == context.Identifier)
                && (context.Identifier != "gameplay-hud" || initialization.HasUsableHUD);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var mark = new Grid { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center };
            var color = new SolidColorBrush(complete ? Color.FromRgb(0x68, 0xC9, 0x91) : Color.FromRgb(0x84, 0x92, 0xA4));
            mark.Children.Add(new Ellipse { Stroke = color, StrokeThickness = 1,
                Fill = complete ? color : Brushes.Transparent });
            if (complete) mark.Children.Add(new Path { Data = Geometry.Parse("M 3,7 L 6,10 L 11,4"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x1B)), StrokeThickness = 1.5 });
            row.Children.Add(mark);
            var title = new TextBlock { Text = initialization.ScreenName(context.Name), TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            var item = new ListBoxItem { Content = row, Tag = context.Identifier };
            AutomationProperties.SetName(item, title.Text);
            AutomationProperties.SetAutomationId(item, $"StencilContext_{context.Identifier}");
            AutomationProperties.SetHelpText(item, initialization.Guidance(context.Identifier, null) ?? "");
            StencilContextList.Items.Add(item);
        }
        var target = stencilSelectedContext ?? initializationTargetContext ?? initialization.Steps.FirstOrDefault()?.ContextIdentifier;
        StencilContextList.SelectedItem = StencilContextList.Items.Cast<ListBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, target))
            ?? StencilContextList.Items.Cast<ListBoxItem>().FirstOrDefault(item => item.Tag is string);
        if (StencilContextList.SelectedItem is { } selected) StencilContextList.ScrollIntoView(selected);
    }

    private void StencilContextList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StencilContextList.SelectedItem is not ListBoxItem { Tag: string context }) return;
        initializationTargetContext = context;
        stencilSelectedContext = context;
        RefreshGuidance();
        StencilContextTitle.Text = initialization.ScreenName(
            initialization.Contexts.First(item => item.Identifier == context).Name);
        stencilPreviewExample = null;
        StencilPreviewImage.Source = null;
        StencilPreviewOverlay.Children.Clear();
        StencilPreviewActions.Visibility = Visibility.Collapsed;
        StencilPreviewEmpty.Visibility = Visibility.Visible;
        StencilEmptyTitle.Text = initialization.Text("No labeled registrations.");
        StencilEmptyGuidance.Text = initialization.Guidance(null, null, context) ?? "";
        try
        {
            if (initialization.ExampleFor(context) is not { } example) return;
            StencilPreviewImage.Source = LoadBitmap(example.ImagePath);
            stencilPreviewExample = example;
            StencilPreviewEmpty.Visibility = Visibility.Collapsed;
            StencilPreviewActions.Visibility = Visibility.Visible;
            RedrawStencilPreview();
        }
        catch (Exception error)
        {
            StencilEmptyGuidance.Text = error.Message;
        }
    }

    private void StencilPreview_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawStencilPreview();

    private void RedrawStencilPreview()
    {
        if (StencilPreviewOverlay is null) return;
        StencilPreviewOverlay.Children.Clear();
        if (stencilPreviewExample is null || StencilPreviewImage.Source is not BitmapSource image) return;
        // The overlay is in viewport DIPs, not image pixels. Use the same
        // aspect-fit rectangle as Image.Stretch so letterboxing never shifts labels.
        var fitted = StencilPreviewPresentation.FitImage(image.Width, image.Height,
            StencilPreviewViewport.ActualWidth, StencilPreviewViewport.ActualHeight);
        if (fitted.Width <= 0 || fitted.Height <= 0) return;
        foreach (var annotation in stencilPreviewExample.Manifest.Annotations)
        {
            var bounds = StencilPreviewPresentation.Project(annotation, fitted);
            var outer = bounds.OutsideStroke(1);
            var brush = annotation.IsHardNegative ? Brushes.OrangeRed : Brushes.DeepSkyBlue;
            var rectangle = new Rectangle
            {
                Width = outer.Width, Height = outer.Height, Stroke = brush, StrokeThickness = 1,
                Fill = new SolidColorBrush(annotation.IsHardNegative
                    ? Color.FromArgb(18, 255, 69, 0) : Color.FromArgb(14, 0, 191, 255))
            };
            Canvas.SetLeft(rectangle, outer.X);
            Canvas.SetTop(rectangle, outer.Y);
            StencilPreviewOverlay.Children.Add(rectangle);
            var name = StencilPreviewPresentation.LabelName(annotation);
            name = customObjectChoices.FirstOrDefault(item => item.Identifier == name)?.Name ?? name;
            var label = new TextBlock { Text = initialization.Text(name), FontSize = 11,
                FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            AutomationProperties.SetAutomationId(label, $"StencilAnnotation_{annotation.Id:N}");
            var caption = new Border { Background = annotation.IsHardNegative ? Brushes.OrangeRed : Brushes.DodgerBlue,
                Padding = new Thickness(3, 1, 3, 1), MaxWidth = fitted.Width, Child = label };
            caption.Measure(new Size(fitted.Width, double.PositiveInfinity));
            var decoration = LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier) == "shared.select-decoration";
            var y = !decoration && bounds.Y - fitted.Y >= 20 ? bounds.Y - caption.DesiredSize.Height - 3
                : bounds.Y + bounds.Height + 3;
            Canvas.SetLeft(caption, Math.Clamp(bounds.X, fitted.X, Math.Max(fitted.X, fitted.X + fitted.Width - caption.DesiredSize.Width)));
            Canvas.SetTop(caption, Math.Clamp(y, fitted.Y, Math.Max(fitted.Y, fitted.Y + fitted.Height - caption.DesiredSize.Height)));
            StencilPreviewOverlay.Children.Add(caption);
        }
    }

    private void StencilEdit_Click(object sender, RoutedEventArgs e)
    {
        if (stencilPreviewExample is not { } example) return;
        if (!SelectWorkspace("Label", capture: false)) return;
        OpenLabelExample(example);
    }

    private void StencilDelete_Click(object sender, RoutedEventArgs e)
    {
        if (stencilPreviewExample is not { } example) return;
        if (MessageBox.Show(this, initialization.Text("deleteScreenshotsTitle", ("count", "1")),
            initialization.Text("deleteScreenshot"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            new LabelingExampleStore(ExamplesRootText.Text).Delete(example);
            if (currentExample?.Id == example.Id)
            {
                currentExample = null;
                currentFrame = null;
                boxes.Clear();
                LabelImage.Source = null;
                RedrawBoxes();
                ResetLabelViewport();
            }
            RefreshExamples();
            menuStencilRecognizer.Refresh(initialization.LanguageIdentifier);
            RefreshInitializationStatus();
            RefreshStencilsWorkspace();
        }
        catch (Exception error) { ShowError(error); }
    }
}
