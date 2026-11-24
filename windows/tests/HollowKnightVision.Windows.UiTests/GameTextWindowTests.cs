using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class GameTextWindowTests
{
    internal static void TestNativeGameTextRecognitionAndOverlay()
    {
        foreach (var (language, _) in StencilInitializationCoordinator.SupportedLanguages)
        {
            var name = WindowsGameTextRecognition.DataLanguage(language);
            Require(File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "game-text", "tessdata", name + ".traineddata")),
                "packaged recognition data for " + language);
        }

        using var recognizer = new WindowsGameTextRecognition();
        var frame = RenderText("CLEAR SAVE");
        var lines = recognizer.Read(frame, "en");
        var clear = lines.FirstOrDefault(line => line.Text.Contains("CLEAR SAVE", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Native game text result: " + string.Join(" | ", lines.Select(line => line.Text)));
        Require(clear.Confidence > .6 && clear.Bounds.X is > .17 and < .24 && clear.Bounds.Y is > .36 and < .5
            && clear.Bounds.Width is > .2 and < .5, "line coordinates relative to the original capture");
        Require(recognizer.Read(RenderText(""), "en").Count == 0, "blank frame");

        var reader = new GameTextReader("unused");
        var work = reader.TryStart(frame, "en", "Select Profile", 1)!;
        Require(reader.Complete(work, lines, 25, 1.05), "publish native scan");
        var window = new GameTextDebugWindow(_ => { }, _ => { }, _ => { });
        window.Present(reader, new(), null);
        var boxes = (Canvas)window.FindName("LineBoxes");
        var preview = (Image)window.FindName("FrameImage");
        var readings = (ListBox)window.FindName("VisibleLines");
        Require(boxes.Children.Count == lines.Count * 2 && preview.Source is BitmapSource bitmap && bitmap.PixelWidth == frame.Width,
            "inspector shows one rectangle and matching number for each line on its own frame");
        readings.SelectedItem = clear;
        var selected = boxes.Children.OfType<Rectangle>().FirstOrDefault(rectangle => rectangle.Stroke == Brushes.Orange);
        Require(selected is not null && Math.Abs(Canvas.GetTop(selected) - clear.Bounds.Y * frame.Height) < .01,
            "selected row highlights the same source rectangle");
        reader.SetEnabled(false);
        window.Present(reader, new(), null);
        Require(preview.Source is null && boxes.Children.Count == 0, "disabling releases the inspector image and boxes");
        window.Close();
        Console.WriteLine("PASS native game text recognition and inspector");
    }

    internal static void TestProofreadingControlAndQuestView()
    {
        var reader = new GameTextReader("unused");
        var work = reader.TryStart(RenderText("GO WEST"), "en", GameTextNotebook.OpeningContext, 1)!;
        Require(reader.Complete(work, [new(1, "Go west to the gate.", new(.2, .3, .4, .1), .96)], 30, 1.03),
            "quest view source");
        var changes = new List<GameTextReviewOptions>();
        var window = new GameTextDebugWindow(_ => { }, changes.Add, reader.SetGoal)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000
        };
        window.Present(reader, new(), null);
        window.Show();
        try
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var toggle = (CheckBox)window.FindName("ReviewCheck");
            var model = (ComboBox)window.FindName("ProofreadingModel");
            var compat = (CheckBox)window.FindName("CompatibilityServiceCheck");
            Require(changes.Count == 0 && toggle.IsChecked == false && model.SelectedItem is LocalThinkingModel selected && selected == LocalThinkingModel.Gemma,
                "showing the notebook never opts into proofreading");
            toggle.IsChecked = true;
            model.SelectedItem = LocalThinkingModel.Llama;
            compat.IsChecked = true;
            Require(changes.Last() is { Enabled: true, Model: "llama3.2-vision:11b", UseCompatibilityService: true },
                "review checkbox and service selection route to the parent");

            var tabs = (TabControl)window.FindName("SourceTabs");
            tabs.SelectedItem = window.FindName("QuestTab");
            var evidence = (TextBox)window.FindName("QuestEvidenceText");
            Require(evidence.Text.Contains("[R1 · Opening cutscene] Go west to the gate.", StringComparison.Ordinal),
                "quest view shows the numbered notebook source");
            var goal = (TextBox)window.FindName("GoalText");
            goal.Text = "  Reach the gate  ";
            window.Present(reader, changes.Last(), "Waiting for resident model");
            Require(goal.Text == "  Reach the gate  " && ((TextBlock)window.FindName("ReviewStatus")).Text == "Waiting for resident model",
                "periodic updates preserve the edited goal and report model availability");
            ((Button)window.FindName("SaveGoalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(reader.Goal == "Reach the gate" && goal.Text == reader.Goal, "goal commits through the notebook");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS game text proofreading controls and quest evidence");
    }

    internal static void TestThinkingInspectorShowsItsCapturedOffer()
    {
        var choices = new List<ThinkingOptions>();
        var logs = 0;
        var window = new ThinkingWindow(choices.Add, () => logs++)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000
        };
        var image = MainWindow.FrameBitmap(RenderText("INSPECT"));
        var captured = new[] { new ThinkingCandidateRow("c0", "interact", "Inspect", 96) };
        window.Present(new(false, true, LocalThinkingModel.Gemma, false), "Model ready", image, image,
            "System message and captured evidence", captured, "{\"scores\":{\"c0\":96}}", "Dispatched");
        window.Show();
        try
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)window.FindName("Candidates");
            Require(ReferenceEquals(grid.ItemsSource, captured)
                && ReferenceEquals(((Image)window.FindName("Screenshot")).Source, image)
                && ((TextBox)window.FindName("InputText")).Text.Contains("captured evidence")
                && ((TextBox)window.FindName("ReplyText")).Text.Contains("96")
                && ((TextBlock)window.FindName("HandoffText")).Text == "Dispatched", "Thinking shows the offered action and its screenshot");
            Require(choices.Count == 0, "opening Thinking must not start model work");
            ((CheckBox)window.FindName("EnabledCheck")).IsChecked = true;
            ((ComboBox)window.FindName("ModelCombo")).SelectedItem = LocalThinkingModel.Llama;
            ((CheckBox)window.FindName("CompatibilityCheck")).IsChecked = true;
            ((CheckBox)window.FindName("AutomaticCheck")).IsChecked = false;
            Require(choices.Last() is { Enabled: true, Automatic: false, SeparateLlama: true, Model.Identifier: "llama3.2-vision:11b" },
                "Thinking opt-in, model choice, service and policy pause");
            ((Button)window.FindName("OpenLog")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(logs == 1, "Thinking can open its notebook");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Thinking inspector presents the bound screenshot, request and scores");
    }

    private static BgraFrame RenderText(string text)
    {
        const int width = 640, height = 360, stride = width * 4;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            if (text.Length > 0)
                drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 38, Brushes.White, 1), new Point(120, 132));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        return new(width, height, stride, pixels, 1, DateTimeOffset.UtcNow);
    }

    private static void Require(bool condition, string operation)
    {
        if (!condition) throw new InvalidOperationException("Game text inspector: " + operation);
    }
}
