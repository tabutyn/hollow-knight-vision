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
    internal static void TestNativeGameTextRecognitionAndOverlay(string? screenshot = null)
    {
        foreach (var (language, _) in StencilInitializationCoordinator.SupportedLanguages)
        {
            var name = WindowsGameTextRecognition.DataLanguage(language);
            Require(File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "game-text", "tessdata", name + ".traineddata")),
                "packaged recognition data for " + language);
        }

        using var recognizer = new WindowsGameTextRecognition();
        var frame = RenderText("CLEAR SAVE");
        var started = Stopwatch.GetTimestamp();
        var lines = recognizer.Read(frame, "en");
        var processingTime = Stopwatch.GetElapsedTime(started);
        var clear = lines.FirstOrDefault(line => line.Text.Contains("CLEAR SAVE", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Native game text result: " + string.Join(" | ", lines.Select(line => line.Text)));
        Require(clear.Confidence > .6 && clear.Bounds.X is > .17 and < .24 && clear.Bounds.Y is > .36 and < .5
            && clear.Bounds.Width is > .2 and < .5, "line coordinates relative to the original capture");
        Require(recognizer.Read(RenderText(""), "en").Count == 0, "blank frame");

        var reader = new GameTextReader("unused");
        var work = reader.TryStart(frame, "en", "Select Profile", 1)!;
        Require(reader.Complete(work, lines, processingTime.TotalMilliseconds, 1.05), "publish native scan");
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
        if (screenshot is not null)
        {
            ((TabControl)window.FindName("SourceTabs")).SelectedIndex = 0;
            window.ShowActivated = window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = window.Top = -10000;
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(screenshot);
            png.Save(file);
        }
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
            var log = (ListBox)window.FindName("LogRows");
            var reading = FindReading(log.ItemContainerGenerator.ContainerFromIndex(0))
                ?? throw new InvalidOperationException("No selectable reading in the log row");
            Require(reading is { IsReadOnly: true, Text: "Go west to the gate." }, "visible notebook text is selectable");
            reading.Select(3, 4);
            Require(CopyText(reading, reading.Copy) == "west", "the native Copy command copies only the highlighted words");
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
        var panel = new ThinkingContextPanel();
        panel.OptionsEdited += choices.Add;
        var window = new Window { Content = panel, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        var image = MainWindow.FrameBitmap(RenderText("INSPECT"));
        panel.Present(new(true, LocalThinkingModel.Gemma, false), true, "Model ready", "captured evidence", "Reach the frontier",
            image, "Captured frame 1", image, "{\"scores\":{\"c0\":96}}", "Dispatched");
        panel.Record(new(ThinkingRequestKind.Policy, "system", "user", "{\"type\":\"object\"}"));
        window.Show();
        try
        {
            Require(!panel.IsExpanded, "Thinking starts compact");
            ((Button)panel.FindName("Compact")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(panel.IsExpanded && ((TabControl)panel.FindName("Tabs")).Items.Count == 3, "click to expand three tabs");
            Require(ReferenceEquals(((Image)panel.FindName("Screenshot")).Source, image)
                && ((TextBox)panel.FindName("LiveEvidence")).Text == "captured evidence"
                && ((TextBox)panel.FindName("MessageDetails")).Text.Contains("{\"type\":\"object\"}")
                && ((TextBox)panel.FindName("Reasoning")).Text == "Reach the frontier"
                && ((TextBox)panel.FindName("ReplyText")).Text.Contains("96")
                && ((TextBlock)panel.FindName("HandoffText")).Text == "Dispatched", "context uses the captured message, schema and reply");
            panel.Record(new(ThinkingRequestKind.Menu, "menu system", "menu user", "menu grammar"));
            var requests = (ComboBox)panel.FindName("Messages");
            requests.SelectedIndex = 1;
            var copy = (Button)panel.FindName("CopyRequest");
            var details = (TextBox)panel.FindName("MessageDetails");
            details.Select(2, 3);
            var copied = CopyText(details, () => copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Require(copy.IsEnabled && copied == details.Text && copied.Contains("User message\nuser")
                && !copied.Contains("menu user") && details.SelectionStart == 2 && details.SelectionLength == 3,
                "one click copies the selected request and schema, preserving the highlighted text");
            requests.SelectedIndex = 2;
            Require(!copy.IsEnabled, "an example schema is not a captured request");
            Require(choices.Count == 0, "opening the context must not start model work");
            ((ComboBox)panel.FindName("ModelCombo")).SelectedItem = LocalThinkingModel.Llama;
            ((CheckBox)panel.FindName("CompatibilityCheck")).IsChecked = true;
            ((CheckBox)panel.FindName("AutomaticCheck")).IsChecked = false;
            Require(choices.Last() is { Automatic: false, SeparateLlama: true, Model.Identifier: "llama3.2-vision:11b" },
                "model choice, service and policy pause");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS collapsible Thinking context presents the captured request and reply");
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
                [RenderText("INSPECT"), 1L, capturedAt, 1, 1L, "game", null, null, null, null, true, null, 0d, 0L, null]);
            Method("StartThinkingQuestion").Invoke(window, null);
            Require(shortcut is true && !((ThinkingContextPanel)window.FindName("ContextPanel")).IsExpanded
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
        var inspector = (ThinkingContextPanel)owner.FindName("ContextPanel");
        try
        {
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
            Require(((Grid)window.FindName("ThinkingBounds")).Visibility == Visibility.Visible
                && Method("HandleLocalShortcut").Invoke(window, [Key.T, true, ModifierKeys.None]) is true
                && service.InventoryCount > 0 && service.LoadCount == 1 && service.RequestCount == 0
                && Field("latestThinkingFrame").GetValue(window) is null,
                "an opted-in launch loads its model and exposes T before any screenshot");
            CollectFrame();
            var rows = (IReadOnlyList<ThinkingCandidateRow>)Field("thinkingRows").GetValue(window)!;
            Require(service.RequestCount == 1 && rows is [{ Id: "c0", Target: "Inspect", Score: 80 }]
                && (string?)Field("thinkingHandoff").GetValue(window) == "Inspection only · Normal mode.",
                "the validated choice appears in the inspector and Normal mode keeps control with the player");
            var decision = Directory.GetFiles(root.FullName, "latest-policy.json", SearchOption.AllDirectories).Single();
            using var saved = JsonDocument.Parse(File.ReadAllBytes(decision));
            Require(saved.RootElement.GetProperty("policy").GetString() == "interact", "save the validated current decision");

            var panel = (ThinkingContextPanel)window.FindName("ContextPanel");
            Require(((TextBox)panel.FindName("FullLog")).Text.Contains("INSPECT")
                && ((TextBlock)panel.FindName("CompactLog")).Text.Contains("INSPECT")
                && ((TextBox)panel.FindName("MessageDetails")).Text.Contains("Expected response schema")
                && ((TextBox)panel.FindName("LiveEvidence")).Text.Contains("INSPECT"),
                "both log modes, captured evidence and the schema explorer use the actual request");
            var showContext = (CheckBox)window.FindName("ShowThinkingContext");
            showContext.IsChecked = false;
            Require(((Grid)window.FindName("ThinkingBounds")).Visibility == Visibility.Collapsed
                && Method("HandleLocalShortcut").Invoke(window, [Key.T, false, ModifierKeys.None]) is false,
                "Debug View hides the entire context and its shortcut");
            showContext.IsChecked = true;

            CollectFrame();
            Require(service.RequestCount == 1, "unchanged observations must not generate more requests");
            service.Reply = """{"policy":"traverse","parameters":{"waypointId":7}}""";
            CollectFrame(generation: 2);
            Require(service.RequestCount == 2 && (string?)Field("thinkingHandoff").GetValue(window) == "Reply rejected."
                && ((string)Field("thinkingReply").GetValue(window)!).Contains("waypointId", StringComparison.Ordinal),
                "the inspector preserves a model reply that selected an unavailable waypoint");

            void CollectFrame(long generation = 1)
            {
                const string context = "Gameplay";
                var frame = RenderText("INSPECT");
                var capturedAt = (double)Method("BotCaptureClock").Invoke(null, null)!;
                reader.ResetFrame();
                var prior = new BgraFrame(frame.Width, frame.Height, frame.Stride, frame.Pixels,
                    frame.CaptureId - 1, frame.ObservedAt.AddSeconds(-1));
                var first = reader.TryStart(prior, "en", context, capturedAt - 1)!;
                Require(reader.Complete(first, [new(1, "INSPECT", new(.2, .3, .4, .1), .95)], 25, capturedAt - 1),
                    "first OCR reading of the same prompt");
                var work = reader.TryStart(frame, "en", context, capturedAt)!;
                Require(reader.Complete(work, [new(1, "INSPECT", new(.2, .3, .4, .1), .95)], 25, capturedAt),
                    "publish the fixture OCR result on the captured frame");
                Invoke("PresentGameText");
                Field("latestThinkingReading").SetValue(window, reader.LatestReading);
                Invoke("ObserveThinkingFrame", frame, generation, capturedAt, 1, 1L, "game", null, null, null, null, true, null, 0d, 0L, null);
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

    internal static void TestFastMenusKeepSourceImagesAndExpireOlderReplies()
    {
        var contexts = new[] { "main-title", "options", "select-profile" };
        var frames = contexts.Select(RenderText).ToArray();
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        using var service = new LocalThinkingFixture
        {
            Chat = async (body, token) =>
            {
                var index = Interlocked.Increment(ref received) - 1;
                var user = body.GetProperty("messages")[1];
                var prompt = user.GetProperty("content").GetString()!;
                var id = contexts[index] + ".item";
                Require(prompt.StartsWith("Choose the next menu action") && prompt.Contains("Goal on " + contexts[index])
                    && prompt.Contains("Item [highlighted] — " + id) && prompt.Split(id).Length == 2
                    && !prompt.Contains("Unique OCR readings") && !prompt.Contains("schema")
                    && !body.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains(id)
                    && body.GetProperty("format").GetRawText().Contains(id),
                    "the sent menu prompt presents each selectable ID once and retains the goal and enforced JSON grammar");
                using var image = new MemoryStream(Convert.FromBase64String(user.GetProperty("images")[0].GetString()!));
                var source = new PngBitmapDecoder(image, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var frame = frames[index];
                var pixels = new byte[frame.Pixels.Length];
                bitmap.CopyPixels(pixels, frame.Stride, 0);
                Require(pixels.AsSpan().SequenceEqual(frame.Pixels), "each menu question attaches its recognized source image in order");
                if (index == 0) { firstRequest.SetResult(); await release.Task.WaitAsync(token); }
                return JsonSerializer.Serialize(new { menu = "select", elementId = contexts[index] + ".item" });
            }
        };
        using var transport = new HttpClient(service);
        using var capture = new CancellationTokenSource();
        var root = Directory.CreateTempSubdirectory("hkv-thinking-menus-");
        var window = new MainWindow(GameRuntimeMode.Normal, null, root.FullName, root.FullName,
            policyClient: new(transport), enableThinking: true);
        try
        {
            Field("visionCancellation").SetValue(window, capture);
            var reader = (GameTextReader)Field("gameTextReader").GetValue(window)!;
            reader.SetEnabled(false);
            var laterFrame = RenderText("next capture");
            void Observe(int index)
            {
                var state = contexts[index];
                reader.SetGoal("Goal on " + state);
                var id = state + ".item";
                var rect = new StencilRect(5, 5, 40, 10);
                var option = new MenuStencilMatch(id, "Item", rect, 1, null);
                var time = (double)Method("BotCaptureClock").Invoke(null, null)!;
                var menu = new MenuStencilResult(state, true, 1, "Item", [option], [option, option],
                    [new(id, "Item", MenuSelectorSide.Left, rect, true)], 1, 1, 1, MenuStencilPhase.Tracking, 1,
                    (frames[index].ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds, "en") { SelectedOptionIdentifier = id };
                Method("ObserveThinkingFrame").Invoke(window,
                    [laterFrame, 1L, time, 1, 1L, state, menu, null, null, null, false, null, 0d, 0L, frames[index]]);
            }
            Task Ask()
            {
                Method("StartThinkingQuestion").Invoke(window, null);
                return (Task)Field("thinkingQuestionTask").GetValue(window)!;
            }
            Observe(0);
            var first = Ask();
            Await(firstRequest.Task);
            Observe(1);
            Observe(2);
            release.SetResult();
            Await(first);
            Await(Ask());
            Require(!Directory.EnumerateFiles(root.FullName, "latest-policy.json", SearchOption.AllDirectories).Any(),
                "superseded menu decisions may be inspected but cannot replace the current decision file");
            Await(Ask());
            var path = Directory.GetFiles(root.FullName, "latest-policy.json", SearchOption.AllDirectories).Single();
            using var saved = JsonDocument.Parse(File.ReadAllBytes(path));
            Require(received == contexts.Length && saved.RootElement.GetProperty("elementId").GetString() == "select-profile.item",
                "only the current menu reply becomes the saved decision");
        }
        finally
        {
            release.TrySetResult();
            capture.Cancel();
            Field("isClosing").SetValue(window, true);
            Await((Task)Method("PrepareShutdownAsync").Invoke(window, null)!);
            root.Delete(recursive: true);
        }
        Console.WriteLine("PASS fast menu transitions submit each source image and goal in order and expire older replies");
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
        public string Reply { get; set; } = """{"policy":"interact","parameters":{"verb":"inspect","targetId":0}}""";
        public string? Modelfile { get; init; }
        public Func<CancellationToken, Task>? BeforeLoadReceipt { get; init; }
        public Func<JsonElement, CancellationToken, Task<string>>? Chat { get; init; }
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
            if (Chat is { } chat && request.RequestUri?.AbsolutePath == "/api/chat")
            {
                RequestCount++;
                var reply = await chat(body, cancellationToken);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { message = new { content = reply } })) };
            }
            var user = body.GetProperty("messages")[1];
            var image = Convert.FromBase64String(user.GetProperty("images")[0].GetString()!);
            Require(request.RequestUri?.AbsolutePath == "/api/chat" && image.Length > 8
                && image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                && user.GetProperty("content").GetString()!.Contains("Read the King's Pass tablet", StringComparison.Ordinal)
                && user.GetProperty("content").GetString()!.Contains("[R1 · Gameplay] INSPECT", StringComparison.Ordinal)
                && body.GetProperty("format").GetProperty("properties").GetProperty("parameters")
                    .GetProperty("properties").GetProperty("verb").GetProperty("const").GetString() == "inspect",
                "native request must use the unique notebook reading, captured image and bound cue");
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

    private static TextBox? FindReading(DependencyObject root)
    {
        if (root is TextBox box) return box;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindReading(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    private static string? CopyText(TextBox box, Action copy)
    {
        string? copied = null;
        void Capture(object sender, DataObjectCopyingEventArgs e)
        {
            copied = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            e.CancelCommand(); // Inspect the native clipboard payload without replacing the user's clipboard.
        }
        DataObject.AddCopyingHandler(box, Capture);
        try { copy(); }
        finally { DataObject.RemoveCopyingHandler(box, Capture); }
        return copied;
    }

    private static void Require(bool condition, string operation)
    {
        if (!condition) throw new InvalidOperationException("Game text inspector: " + operation);
    }
}
