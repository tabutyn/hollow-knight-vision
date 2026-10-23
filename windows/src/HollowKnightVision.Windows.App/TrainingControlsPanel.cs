using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HollowKnightVision.Windows.App;

/// <summary>Native Windows training controls with the Mac notification/progress lifecycle.</summary>
public sealed class TrainingControlsPanel : Grid
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(90, 167, 255));
    private static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(153, 153, 153));
    public TextBlock CountText { get; } = new() { FontFamily = new("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) };
    public Button TrainButton { get; } = new() { Margin = new(0), Padding = new(10, 6, 10, 6) };
    public TextBlock ButtonText { get; } = new();
    public Ellipse NotificationDot { get; } = new() { Width = 7, Height = 7, Fill = Accent, Margin = new(0, 0, 5, 0) };
    public TextBlock VersionText { get; } = new() { Text = "Default", FontFamily = new("Consolas"), Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0), MaxWidth = 100, TextTrimming = TextTrimming.CharacterEllipsis };
    public TextBlock ProgressText { get; } = new() { Foreground = Secondary, FontFamily = new("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    public Grid ProgressRow { get; } = new() { Margin = new(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    public event RoutedEventHandler? ActionRequested;

    public TrainingControlsPanel()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var content = new StackPanel { Orientation = Orientation.Horizontal }; content.Children.Add(NotificationDot); content.Children.Add(ButtonText);
        TrainButton.Content = content;
        TrainButton.Click += (sender, args) => { if (TrainButton.IsEnabled) ActionRequested?.Invoke(this, args); };
        AutomationProperties.SetAutomationId(TrainButton, "ModelTrainingAction");
        AutomationProperties.SetName(CountText, "Untrained screenshots");
        controls.Children.Add(CountText); controls.Children.Add(TrainButton); controls.Children.Add(VersionText); Children.Add(controls);
        ProgressRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ProgressRow.ColumnDefinitions.Add(new());
        var spinner = new ProgressBar { IsIndeterminate = true, Width = 32, Height = 3, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        ProgressRow.Children.Add(spinner); SetColumn(ProgressText, 1); ProgressRow.Children.Add(ProgressText); SetRow(ProgressRow, 1); Children.Add(ProgressRow);
        Update(0, false, false, false, false, null, "", key => key);
    }
    public void Update(int untrained, bool canTrain, bool running, bool stopping, bool finalizing, string? progress, string help, Func<string, string> text)
    {
        CountText.Text = $"+{untrained}"; CountText.Foreground = untrained > 0 ? Accent : Secondary;
        CountText.ToolTip = text("Untrained screenshots");
        NotificationDot.Visibility = untrained > 0 && !running ? Visibility.Visible : Visibility.Collapsed;
        ButtonText.Text = text(finalizing ? "Applying model…" : stopping ? "Stopping training…" : running ? "Stop Training" : "Train");
        TrainButton.Foreground = running ? Brushes.Salmon : Brushes.WhiteSmoke;
        ButtonText.Foreground = TrainButton.Foreground;
        TrainButton.IsEnabled = running ? !stopping && !finalizing : canTrain;
        TrainButton.ToolTip = running ? ButtonText.Text : help;
        AutomationProperties.SetName(TrainButton, ButtonText.Text);
        ProgressRow.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = stopping ? text("Stopping training…") : finalizing ? text("Applying model…") : progress ?? text("Preparing…");
        ProgressText.ToolTip = ProgressText.Text;
    }
}
