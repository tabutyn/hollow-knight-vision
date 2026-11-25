using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        window.Present(new(true, LocalThinkingModel.Gemma, false), "Model ready", image, image,
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
            ((ComboBox)window.FindName("ModelCombo")).SelectedItem = LocalThinkingModel.Llama;
            ((CheckBox)window.FindName("CompatibilityCheck")).IsChecked = true;
            ((CheckBox)window.FindName("AutomaticCheck")).IsChecked = false;
            Require(choices.Last() is { Automatic: false, SeparateLlama: true, Model.Identifier: "llama3.2-vision:11b" },
                "Thinking model choice, service and policy pause");
            ((Button)window.FindName("OpenLog")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(logs == 1, "Thinking can open its notebook");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Thinking inspector presents the bound screenshot, request and scores");
    }

    internal static void TestDisabledThinkingLeavesModelIdleAndSkipsCaptureAndInference()
    {
        using var service = new LocalThinkingFixture();
        using var transport = new HttpClient(service);
        using var capture = new CancellationTokenSource();
        var root = Directory.CreateTempSubdirectory("hkv-thinking-disabled-");
        var window = new MainWindow(GameRuntimeMode.Normal, null, root.FullName, root.FullName, policyClient: new(transport));
        try
        {
            Field("visionCancellation").SetValue(window, capture);
            Method("StartThinkingPreload").Invoke(window, null);
            Method("OpenThinkingInspector").Invoke(window, null);
            var shortcut = Method("HandleLocalShortcut").Invoke(window, [Key.T, false, ModifierKeys.None]);
            var capturedAt = (double)Method("BotCaptureClock").Invoke(null, null)!;
            Method("ObserveThinkingFrame").Invoke(window,
                [RenderText("INSPECT"), 1L, capturedAt, 1, 1L, "game", null, null, null, null, true, null, 0d]);
            Method("StartThinkingQuestion").Invoke(window, null);
            Require(((Button)window.FindName("ThinkingButton")).Visibility == Visibility.Collapsed
                && shortcut is false && Field("thinkingWindow").GetValue(window) is null
                && Field("latestThinkingFrame").GetValue(window) is null
                && Field("thinkingPreloadTask").GetValue(window) is null
                && Field("thinkingQuestionTask").GetValue(window) is null
                && service.InventoryCount == 0 && service.LoadCount == 0 && service.RequestCount == 0,
                "a launch without consent must expose no Thinking shortcuts, capture, or model traffic");
        }
        finally
        {
            capture.Cancel();
            Field("isClosing").SetValue(window, true);
            Await((Task)Method("PrepareShutdownAsync").Invoke(window, null)!);
            root.Delete(recursive: true);
        }
        Console.WriteLine("PASS disabled Thinking exposes no inspector, captures, or model traffic");
    }

    internal static void TestThinkingInspectorReportsLoadProgressUntilReceipt()
    {
        var root = Directory.CreateTempSubdirectory("hkv-thinking-load-progress-");
        var log = System.IO.Path.Combine(root.FullName, "server.log");
        File.WriteAllText(log, "");
        var digest = new string('a', 64);
        var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new LocalThinkingFixture
        {
            Modelfile = "FROM C:\\models\\sha256-" + digest,
            BeforeLoadReceipt = async token =>
            {
                await File.AppendAllTextAsync(log, $"srv load_model: loading model 'sha256-{digest}'\n"
                    + "load_tensors: CUDA0 model buffer size = 2048.00 MiB\n"
                    + "load_tensors: offloaded 19/24 layers to GPU\nclip: warmup with image size 224\n", token);
                await confirm.Task.WaitAsync(token);
            }
        };
        using var transport = new HttpClient(service);
        var owner = new MainWindow(GameRuntimeMode.Normal, null, root.FullName, root.FullName,
            policyClient: new(transport, _ => log), enableThinking: true);
        var inspector = new ThinkingWindow(_ => { }, () => { });
        try
        {
            Field("thinkingWindow").SetValue(owner, inspector);
            var label = (TextBlock)inspector.FindName("StatusText");
            Method("StartThinkingPreload").Invoke(owner, null);
            var preload = (Task)Field("thinkingPreloadTask").GetValue(owner)!;
            var deadline = Stopwatch.StartNew();
            while (!label.Text.Contains("Warming up the vision encoder", StringComparison.Ordinal)
                && deadline.Elapsed < TimeSpan.FromSeconds(8))
            {
                Program.Pump();
                Thread.Sleep(5);
            }
            Require(!preload.IsCompleted && label.Text.Contains("Warming up the vision encoder")
                && label.Text.Contains("19/24 layers") && label.Text.Contains("GiB GPU") && label.Text.Contains(" s"),
                "show elapsed time, verified model stages, offloaded layers and device buffers before the load receipt");
            confirm.TrySetResult();
            Await(preload);
            Require(label.Text.StartsWith("Model ready in ", StringComparison.Ordinal)
                && !label.Text.Contains("Warming up", StringComparison.Ordinal),
                "the confirmed model receipt must replace load progress with readiness and elapsed time");
        }
        finally
        {
            confirm.TrySetResult();
            Field("thinkingWindow").SetValue(owner, null);
            inspector.Close();
            Field("isClosing").SetValue(owner, true);
            Await((Task)Method("PrepareShutdownAsync").Invoke(owner, null)!);
            root.Delete(recursive: true);
        }
        Console.WriteLine("PASS Thinking displays the selected Ollama model's load stages until the receipt");
    }

    internal static void TestThinkingRequestUsesBoundFrameAndNotebook()
    {
        using var service = new LocalThinkingFixture();
        using var transport = new HttpClient(service);
        using var capture = new CancellationTokenSource();
        var root = Directory.CreateTempSubdirectory("hkv-thinking-request-");
        var window = new MainWindow(GameRuntimeMode.Normal, null, root.FullName, root.FullName,
            policyClient: new(transport), enableThinking: true);
        try
        {
            Field("visionCancellation").SetValue(window, capture);
            var reader = (GameTextReader)Field("gameTextReader").GetValue(window)!;
            reader.SetEnabled(true);
            reader.SetGoal("Read the King's Pass tablet");
            Invoke("StartThinkingPreload");
            Await((Task)Field("thinkingPreloadTask").GetValue(window)!);
            Require(((Button)window.FindName("ThinkingButton")).Visibility == Visibility.Visible
                && Method("HandleLocalShortcut").Invoke(window, [Key.T, true, ModifierKeys.None]) is true
                && service.InventoryCount > 0 && service.LoadCount == 1 && service.RequestCount == 0
                && Field("latestThinkingFrame").GetValue(window) is null,
                "an opted-in launch loads its model and exposes T before any screenshot");
            CollectFrame();
            var rows = (IReadOnlyList<ThinkingCandidateRow>)Field("thinkingRows").GetValue(window)!;
            Require(service.RequestCount == 1 && rows is [{ Id: "c0", Target: "Inspect", Score: 96 }]
                && (string?)Field("thinkingHandoff").GetValue(window) == "Inspection only · Normal mode.",
                "the real request resolves into the scored inspector row without taking a Normal control lease");

            service.Reply = """{"scores":{"c7":96}}""";
            Field("nextThinkingQuestion").SetValue(window, 0d);
            CollectFrame();
            Require(service.RequestCount == 2 && (string?)Field("thinkingHandoff").GetValue(window) == "Reply rejected."
                && ((string)Field("thinkingReply").GetValue(window)!).Contains("c7", StringComparison.Ordinal),
                "the inspector preserves a model reply that tried to score an unavailable candidate");

            void CollectFrame()
            {
                const string context = "Gameplay";
                var frame = RenderText("INSPECT");
                var capturedAt = (double)Method("BotCaptureClock").Invoke(null, null)!;
                reader.ResetFrame();
                var work = reader.TryStart(frame, "en", context, capturedAt)!;
                Require(reader.Complete(work, [new(1, "INSPECT", new(.2, .3, .4, .1), .95)], 25, capturedAt),
                    "publish the fixture OCR result on the captured frame");
                Field("latestThinkingReading").SetValue(window, reader.LatestReading);
                Invoke("ObserveThinkingFrame", frame, 1L, capturedAt, 1, 1L, "game", null, null, null, null, true, null, 0d);
                Invoke("StartThinkingQuestion");
                if (Field("thinkingQuestionTask").GetValue(window) is Task question) Await(question);
            }
        }
        finally
        {
            capture.Cancel();
            Field("isClosing").SetValue(window, true);
            Await((Task)Method("PrepareShutdownAsync").Invoke(window, null)!);
            root.Delete(recursive: true);
        }
        Console.WriteLine("PASS Windows Thinking request attaches its captured PNG, reads notebook context and displays the validated reply");

        void Invoke(string name, params object?[] arguments) => Method(name).Invoke(window, arguments);
    }

    private static void Await(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Program.Pump();
            Thread.Sleep(5);
        }
        Require(task.IsCompleted, "Thinking request or cleanup did not complete");
        task.GetAwaiter().GetResult();
    }

    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    private sealed class LocalThinkingFixture : HttpMessageHandler
    {
        public string Reply { get; set; } = """{"scores":{"c0":96}}""";
        public string? Modelfile { get; init; }
        public Func<CancellationToken, Task>? BeforeLoadReceipt { get; init; }
        public int InventoryCount { get; private set; }
        public int LoadCount { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                InventoryCount++;
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadCount == 0 ? """{"models":[]}"""
                        : """{"models":[{"name":"gemma3:4b","context_length":4096}]}""")
                };
            }
            using var content = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = content.RootElement;
            if (request.RequestUri?.AbsolutePath == "/api/show" && Modelfile is not null)
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { modelfile = Modelfile })) };
            if (request.RequestUri?.AbsolutePath == "/api/generate")
            {
                Require(body.GetProperty("model").GetString() == LocalThinkingModel.Gemma.Identifier
                    && body.GetProperty("options").GetProperty("num_ctx").GetInt32() == 4096,
                    "startup must use the selected default model and Thinker's context");
                LoadCount++;
                if (BeforeLoadReceipt is { } before) await before(cancellationToken);
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"done":true,"done_reason":"load"}""") };
            }
            var user = body.GetProperty("messages")[1];
            var image = Convert.FromBase64String(user.GetProperty("images")[0].GetString()!);
            Require(request.RequestUri?.AbsolutePath == "/api/chat" && image.Length > 8
                && image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                && user.GetProperty("content").GetString()!.Contains("Read the King's Pass tablet", StringComparison.Ordinal)
                && body.GetProperty("format").GetProperty("properties").GetProperty("scores")
                    .GetProperty("properties").TryGetProperty("c0", out _), "native request must use the captured image and bound cue");
            RequestCount++;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { message = new { content = Reply } })) };
        }
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
