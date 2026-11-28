using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed record ThinkingOptions(bool Automatic, LocalThinkingModel Model, bool SeparateLlama);
internal sealed record ThinkingCandidateRow(string Id, string Policy, string Target, int? Score);

public partial class ThinkingContextPanel : UserControl
{
    internal event Action<ThinkingOptions>? OptionsEdited;
    internal event Action? ShowDecision;
    private void RevealDecision(object sender, RoutedEventArgs e) => ShowDecision?.Invoke();
    private bool updating;
    private readonly Dictionary<ThinkingRequestKind, ThinkingRequestView> requests = [];
    private ThinkingRequestKind? latestKind;
    private GameTextLogEntry[]? displayedEntries;
    private bool? displayedReaderEnabled;
    internal bool IsExpanded => Expanded.Visibility == Visibility.Visible;

    public ThinkingContextPanel()
    {
        InitializeComponent();
        ModelCombo.ItemsSource = LocalThinkingModel.All;
        Messages.ItemsSource = new[] { "Latest request", "Gameplay", "OCR transcription", "Quest notes", "Menu" };
        Messages.SelectedIndex = 0;
        Expand(false);
    }

    internal void Expand(bool expand)
    {
        Width = expand ? 1100 : 280;
        Height = expand ? 490 : 66;
        Compact.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
        Expanded.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TogglePanel(object sender, RoutedEventArgs e) => Expand(!IsExpanded);

    internal void ShowNotebook(GameTextReader reader)
    {
        if (ReferenceEquals(displayedEntries, reader.Entries) && displayedReaderEnabled == reader.Enabled) return;
        displayedEntries = reader.Entries;
        displayedReaderEnabled = reader.Enabled;
        var text = reader.Entries.Length == 0 ? (reader.Enabled ? "Waiting for text…" : "Text reading is off")
            : string.Join('\n', reader.Entries.Select(entry => entry.Observation.DisplayText));
        CompactLog.Text = FullLog.Text = text;
        LogProfile.Text = reader.Profile is { } profile ? $"Save slot {profile.Slot}" : "Before save selection";
    }

    internal void Present(ThinkingOptions options, bool enabled, string status, string live, string brief,
        BitmapSource? image, string? caption, BitmapSource? attachment, string reply, string? handoff, bool savedDecision = false)
    {
        updating = true;
        Options.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AutomaticCheck.IsChecked = options.Automatic;
        CompatibilityCheck.IsChecked = options.SeparateLlama;
        ModelCombo.SelectedItem = options.Model;
        StatusText.Text = enabled ? status : "Thinking is off";
        LiveEvidence.Text = live.Length == 0 ? "Waiting for game evidence." : live;
        Reasoning.Text = brief.Length == 0 ? StatusText.Text : brief;
        FrameCaption.Text = caption ?? "Waiting for the first decision frame.";
        Screenshot.Source = image;
        Attachment.Source = attachment;
        AttachmentPanel.Visibility = attachment is null ? Visibility.Collapsed : Visibility.Visible;
        ReplyText.Text = reply.Length == 0 ? "Waiting for the first JSON reply." : reply;
        DecisionFile.Visibility = savedDecision ? Visibility.Visible : Visibility.Collapsed;
        HandoffText.Text = handoff ?? "";
        updating = false;
    }

    internal void Record(ThinkingRequestView request)
    {
        requests[request.Kind] = request;
        latestKind = request.Kind;
        ShowMessage();
    }

    internal void ClearMessages() { requests.Clear(); latestKind = null; ShowMessage(); }
    private void SelectMessage(object sender, SelectionChangedEventArgs e) => ShowMessage();
    private void ShowMessage()
    {
        if (Messages.SelectedIndex < 0) return;
        var kind = Messages.SelectedIndex == 0 ? latestKind ?? ThinkingRequestKind.Policy : (ThinkingRequestKind)(Messages.SelectedIndex - 1);
        CopyRequest.IsEnabled = requests.TryGetValue(kind, out var request);
        if (request is not null)
            MessageDetails.Text = $"System message\n{request.System}\n\nUser message\n{request.User}\n\nExpected response schema\n{request.ResponseSchema}";
        else
        {
            object schema = kind switch
            {
                ThinkingRequestKind.Policy => ThinkingDecisionOffer.GameplayContract,
                ThinkingRequestKind.Menu => ThinkingDecisionOffer.MenuContract,
                ThinkingRequestKind.Transcription => GameTextCleanup.ResponseSchema(false, true, 1),
                _ => AtlasQuestReview.ResponseSchema([1])
            };
            MessageDetails.Text = "Example response schema · awaiting a request\n" +
                JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private void CopySelectedRequest(object sender, RoutedEventArgs e)
    {
        var start = MessageDetails.SelectionStart;
        var length = MessageDetails.SelectionLength;
        try
        {
            MessageDetails.SelectAll();
            MessageDetails.Copy();
        }
        finally { MessageDetails.Select(start, length); }
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !IsLoaded || ModelCombo.SelectedItem is not LocalThinkingModel model) return;
        OptionsEdited?.Invoke(new(AutomaticCheck.IsChecked == true, model, CompatibilityCheck.IsChecked == true));
    }
}
