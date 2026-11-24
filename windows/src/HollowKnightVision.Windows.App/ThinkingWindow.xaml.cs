using System.Windows;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed record ThinkingOptions(bool Enabled, bool Automatic, LocalThinkingModel Model, bool SeparateLlama);
internal sealed record ThinkingCandidateRow(string Id, string Policy, string Target, int? Score);

public partial class ThinkingWindow : Window
{
    private readonly Action<ThinkingOptions> changeOptions;
    private readonly Action openLog;
    private bool updating;

    internal ThinkingWindow(Action<ThinkingOptions> changeOptions, Action openLog)
    {
        this.changeOptions = changeOptions;
        this.openLog = openLog;
        InitializeComponent();
        ModelCombo.ItemsSource = LocalThinkingModel.All;
    }

    internal void Present(ThinkingOptions options, string status, BitmapSource? image, BitmapSource? attachment, string input,
        IReadOnlyList<ThinkingCandidateRow> candidates, string reply, string? handoff)
    {
        updating = true;
        EnabledCheck.IsChecked = options.Enabled;
        AutomaticCheck.IsChecked = options.Automatic;
        CompatibilityCheck.IsChecked = options.SeparateLlama;
        ModelCombo.SelectedItem = options.Model;
        StatusText.Text = status;
        Screenshot.Source = image;
        Attachment.Source = attachment;
        AttachmentPanel.Visibility = attachment is null ? Visibility.Collapsed : Visibility.Visible;
        InputText.Text = input;
        Candidates.ItemsSource = candidates;
        ReplyText.Text = reply;
        HandoffText.Text = handoff ?? "";
        updating = false;
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !IsLoaded || ModelCombo.SelectedItem is not LocalThinkingModel model) return;
        changeOptions(new(EnabledCheck.IsChecked == true, AutomaticCheck.IsChecked == true,
            model, CompatibilityCheck.IsChecked == true));
    }

    private void OpenLogClicked(object sender, RoutedEventArgs e) => openLog();
}
