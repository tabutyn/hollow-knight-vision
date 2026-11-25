using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Text.Json;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class TrainingIntegrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public static void Run()
    {
        TestSharedDatasetExport(); TestPackagedTrainingEnvironment(); TestControlsAndLayout(); TestOutputGenerationsAndShutdown();
        TestPowerShellFailureMessage();
        var scope = new DispatcherFrame();
        var objectFailure = VerifyObjectTrainerFailureAsync();
        objectFailure.GetAwaiter().OnCompleted(() => scope.Continue = false);
        Dispatcher.PushFrame(scope);
        objectFailure.GetAwaiter().GetResult();
        Console.WriteLine("PASS native training count/dot, Stop/progress states, narrow toolbar, coalesced output and nonblocking shutdown");
    }
    private static void TestSharedDatasetExport()
    {
        var root = Directory.CreateTempSubdirectory("hkv-training-start-");
        try
        {
            var examples = Path.Combine(root.FullName, "examples");
            var store = new LabelingExampleStore(examples);
            var frame = new BgraFrame(2, 2, 8, new byte[16], 0, DateTimeOffset.UnixEpoch);
            LabelingExampleAnnotation Box(string id, bool negative = false) => new(Guid.NewGuid(), id, .1, .1, .2, .2, negative);
            var menu = store.Save(frame, "main-title", [Box("world.sign")]);
            var game = store.Save(frame, "game", [Box("world.sign"), Box("enemies.shade", negative: true)]);
            var empty = store.Save(frame, "game", [], knownClassIdentifiers: ["world.sign"]);
            var negative = store.Save(frame, "game", [Box("world.sign", negative: true)]);
            var modelClasses = LabelingSharedModelPolicy.ClassIdentifiers();
            var classes = modelClasses.Order(StringComparer.Ordinal).ToArray();
            Check(store.Load().Single(example => example.Id == menu.Id).Manifest.ContextIdentifier == "main-title",
                "The mislabeled menu fixture must retain its original context.");
            Check(LabelingSharedModelPolicy.HasPositiveGameplayEvidence(store.Load(), modelClasses),
                "A Game Play sign without Knight labels should enable shared training.");
            // Exercise the same export boundary used by Train, without constructing
            // the live capture window or providing any model-name UI state.
            var snapshot = MainWindow.ExportSharedTrainingDataset(examples, Path.Combine(root.FullName, "datasets"), classes);
            Check(snapshot.Manifest.ClassIdentifier == LabelingSharedModelPolicy.ModelIdentifier
                && snapshot.Manifest.ClassIdentifiers!.SequenceEqual(["world.sign"]),
                "Train must retain the shared model identity and export only classes with positive Gameplay labels.");
            Check(snapshot.Manifest.Items.Select(item => item.ExampleIdentifier).ToHashSet().SetEquals([game.Id, empty.Id, negative.Id])
                && snapshot.Manifest.TrainingAnnotationCount == 1, "Shared training must use just the Game Play sign, known-empty frame, and hard negative.");
            var annotations = File.ReadAllText(Path.Combine(snapshot.DirectoryPath, "training", LabelingDatasetExporter.AnnotationsFilename));
            Check(!annotations.Contains("enemies.shade", StringComparison.Ordinal),
                "A negative-only class must not enter the training annotations.");
            Check(LabelingTrainingConfiguration.ForModel(snapshot.Manifest.ClassIdentifier).MaximumIterations == 100,
                "Shared model export selected the short per-object training preset.");
        }
        finally { root.Delete(recursive: true); }
    }

    private static void TestPackagedTrainingEnvironment()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "training");
        foreach (var file in new[] { "train-model-windows.ps1", "setup-training-windows.ps1", "training-environment-windows.ps1",
            "TrainingPython/requirements-windows.txt", "TrainingPython/hkv_incremental_trainer.py" })
            Check(File.Exists(Path.Combine(packaged, file)), "Installed training asset missing: " + file);
        var root = Directory.CreateTempSubdirectory("hkv-training-runtime-");
        try
        {
            var probe = Path.Combine(root.FullName, "probe.ps1");
            File.WriteAllText(probe, "param([string]$Tools)\n$ErrorActionPreference='Stop'\n. (Join-Path $Tools 'training-environment-windows.ps1')\nGet-HkvTrainingEnvironment -WindowsRoot $Tools | ConvertTo-Json");
            JsonDocument Resolve(string? overridePython = null)
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", probe, "-Tools", packaged }) start.ArgumentList.Add(argument);
                start.Environment["LOCALAPPDATA"] = root.FullName;
                start.Environment["PATH"] = ""; // Installed training must not require Git or a checkout.
                start.Environment["HKV_TRAINER_PYTHON"] = overridePython ?? "";
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new Exception("Training environment probe timed out."); }
                Check(process.ExitCode == 0, "Installed runtime resolution failed: " + stderr.GetAwaiter().GetResult());
                return JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            }
            using var fresh = Resolve();
            Check(fresh.RootElement.GetProperty("IsPackaged").GetBoolean()
                && fresh.RootElement.GetProperty("Trainer").GetString() == Path.Combine(packaged, "TrainingPython/hkv_incremental_trainer.py").Replace('/', '\\'),
                "Installed launcher still depends on source paths.");
            Check(fresh.RootElement.GetProperty("Python").GetString()!.StartsWith(root.FullName, StringComparison.Ordinal), "Fresh runtime escaped user storage.");
            var settings = fresh.RootElement.GetProperty("SettingsPath").GetString()!;
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            var registered = Path.Combine(root.FullName, "existing environment", "python.exe");
            File.WriteAllText(settings, JsonSerializer.Serialize(new { pythonPath = registered }));
            using var saved = Resolve();
            Check(saved.RootElement.GetProperty("Python").GetString() == registered, "Installed launcher ignored registered training Python.");
            var explicitPython = Path.Combine(root.FullName, "override", "python.exe");
            using var overridden = Resolve(explicitPython);
            Check(overridden.RootElement.GetProperty("Python").GetString() == explicitPython, "Explicit interpreter did not override registered/default paths.");
        }
        finally { root.Delete(recursive: true); }
    }

    private static void TestControlsAndLayout()
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        var element = xml.Descendants().Single(item => (string?)item.Attribute(x + "Name") == "ModelPanel").Elements(ns + "Border").First();
        var toolbar = new XElement(element); toolbar.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        XNamespace local = "clr-namespace:HollowKnightVision.Windows.App;assembly=HollowKnightVision.Windows";
        toolbar.SetAttributeValue(XNamespace.Xmlns + "local", local.NamespaceName);
        foreach (var control in toolbar.Descendants().Where(item => item.Name.LocalName == "TrainingControlsPanel"))
        {
            Check((string?)control.Attribute("ActionRequested") == "MacTrain_Click", "Production training action is not wired.");
            control.Attribute("ActionRequested")!.Remove(); control.Name = local + "TrainingControlsPanel";
        }
        foreach (var handler in toolbar.Descendants().Attributes("Checked").ToArray()) handler.Remove();
        // Parse in a resource scope without constructing the live MainWindow.
        var wrapper = new XElement(ns + "Grid", new XElement(ns + "Grid.Resources", xml.Descendants(ns + "Window.Resources").Single().Elements().Select(item => new XElement(item))), toolbar);
        wrapper.SetAttributeValue(XNamespace.Xmlns + "shell", "clr-namespace:System.Windows.Shell;assembly=PresentationFramework");
        foreach (var item in wrapper.Descendants().Attributes(x + "Class").ToArray()) item.Remove();
        var content = (Grid)System.Windows.Markup.XamlReader.Parse(wrapper.ToString());
        var window = new Window { Content = content, Width = 1100, Height = 110, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, FontFamily = new("Segoe UI") };
        var panel = (TrainingControlsPanel)content.FindName("TrainingControls");
        var center = (RadioButton)content.FindName("ModelScreenshotsButton");
        var clicks = 0; panel.ActionRequested += (_, _) => clicks++;
        window.Show(); Pump();
        try
        {
            panel.Update(3, true, false, false, false, null, "Update model", key => key); Pump();
            Check(panel.CountText.Text == "+3" && panel.NotificationDot.Visibility == Visibility.Visible && panel.TrainButton.IsEnabled && panel.ButtonText.Text == "Train", "Idle training notification differs from Mac.");
            Check(((SolidColorBrush)panel.CountText.Foreground).Color == Color.FromRgb(90, 167, 255), "Untrained count is not blue.");
            panel.TrainButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(clicks == 1, "Train action did not fire exactly once.");
            panel.Update(3, false, true, false, false, "7/100 · about 6 min remaining", "Blocked labels", key => key); Pump();
            Check(panel.TrainButton.IsEnabled && panel.ButtonText.Text == "Stop Training" && panel.NotificationDot.Visibility == Visibility.Collapsed && panel.ProgressRow.Visibility == Visibility.Visible,
                "A running trainer must remain stoppable even if labels become ineligible.");
            Check(((SolidColorBrush)panel.ButtonText.Foreground).Color == Colors.Salmon, "The global TextBlock theme hid the destructive Stop color.");
            panel.TrainButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(clicks == 2, "Stop action did not fire exactly once.");
            foreach (var width in new[] { 1100d, 720d })
            {
                window.Width = width; Pump(); content.UpdateLayout();
                var centerRight = center.TranslatePoint(new Point(center.ActualWidth, 0), content).X;
                var buttonLeft = panel.CountText.TranslatePoint(new Point(0, 0), content).X;
                Check(buttonLeft >= centerRight, $"Training controls overlap mode tabs at {width}px.");
                Check(panel.ProgressText.ActualWidth > 20 && panel.ProgressText.Text == "7/100 · about 6 min remaining", "Progress lost available width or its full tooltip.");
            }
            window.Width = 1100; Pump();
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "training-toolbar-preview.png"))) encoder.Save(file);
            panel.Update(3, true, true, true, false, null, "", key => key); Pump();
            Check(!panel.TrainButton.IsEnabled && panel.ButtonText.Text == "Stopping training…", "Repeated Stop can re-enter cancellation.");
            panel.TrainButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(clicks == 2, "Disabled training action still invoked callback.");
            panel.Update(0, true, true, false, true, null, "", key => key); Check(!panel.TrainButton.IsEnabled && panel.ButtonText.Text == "Applying model…", "Activation still exposes Stop.");
            panel.Update(0, false, false, false, false, null, "Fix labels", key => "localized " + key); Pump();
            Check(panel.NotificationDot.Visibility == Visibility.Collapsed && panel.ProgressRow.Visibility == Visibility.Collapsed && !panel.TrainButton.IsEnabled
                && panel.ButtonText.Text == "localized Train" && (string)panel.TrainButton.ToolTip == "Fix labels", "Idle/error completion did not restore controls or translation.");
        }
        finally { window.Close(); Pump(); }
    }
    private static void TestOutputGenerationsAndShutdown()
    {
        var applied = new List<TrainingOutputSnapshot>();
        var dispatcher = Dispatcher.CurrentDispatcher; var updates = new TrainingUiOutputPump(dispatcher, value => applied.Add(value));
        var first = updates.Begin();
        Task.Run(() => { for (var i = 1; i <= 100; i++) updates.Post(first, new(i, i.ToString(), null, null)); }).GetAwaiter().GetResult();
        Check(applied.Count == 0, "Background output blocked on the UI."); Pump();
        Check(applied.Count == 1 && applied[0].Sequence == 100, "Output was not coalesced to newest snapshot.");
        updates.Post(first, new(99, "older", null, null)); Pump(); Check(applied.Count == 1, "Output moved backwards after a dispatch.");
        updates.Post(first, new(101, "discard", null, null)); updates.Stop(); Pump(); Check(applied.Count == 1, "Stopped output reached the UI.");
        var second = updates.Begin(); updates.Post(first, new(102, "old run", null, null)); updates.Post(second, new(1, "new run", null, null)); Pump();
        Check(applied.Count == 2 && applied[^1].Text == "new run", "Output crossed run generations.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = ContainedTrainingCommand("Write-Output 'HKV_STAGE name=training'; Start-Sleep -Seconds 60");
        var token = updates.Begin();
        var task = WindowsTrainingProcess.RunAsync(start, cancellation.Token, value =>
        { updates.Post(token, value); if (value.ProgressText is not null) ready.TrySetResult(); });
        try
        {
            // Deliberately wait without pumping the dispatcher, as MainWindow.OnClosed does.
            ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            updates.Stop(); cancellation.Cancel();
            var result = task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Check(result.Cancelled && result.Output.ProgressText == "Starting training…", "UI-thread shutdown did not stop/drain its actual process.");
            Pump(); Check(applied.Count == 2, "Queued shutdown output reached a closed context.");
        }
        finally { cancellation.Cancel(); task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); updates.Stop(); }
    }

    private static void TestPowerShellFailureMessage()
    {
        const string issue = "Training environment missing. Run setup-training-windows.ps1.";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = WindowsTrainingProcess.RunAsync(ContainedTrainingCommand($"throw '{issue}'"), timeout.Token)
            .WaitAsync(timeout.Token).GetAwaiter().GetResult();
        Check(result.ExitCode != 0 && !result.Cancelled && result.FailureMessage.Contains(issue, StringComparison.Ordinal)
            && !result.FailureMessage.Contains("FullyQualifiedErrorId", StringComparison.Ordinal)
            && result.Output.Text.Contains(issue, StringComparison.Ordinal),
            $"The native helper should report the diagnostic. Received {result.FailureMessage}\n{result.Output.Text}");
        Console.WriteLine("PASS Windows training failure displays the PowerShell diagnostic and retains its log");
    }

    private static async Task VerifyObjectTrainerFailureAsync()
    {
        const string environment = "HKV_TRAINER_PYTHON";
        const string diagnostic = "Fixture trainer rejected the exported examples.";
        var previousPython = Environment.GetEnvironmentVariable(environment);
        var root = Directory.CreateTempSubdirectory("hkv-object-train-failed-");
        MainWindow? window = null;
        try
        {
            var examples = new LabelingExampleStore(Path.Combine(root.FullName, "labeling-v1", "examples"));
            var frame = new BgraFrame(64, 36, 256, new byte[64 * 36 * 4], 0, DateTimeOffset.UnixEpoch);
            examples.Save(frame, "game", [new LabelingExampleAnnotation(Guid.NewGuid(), "world.sign", .1, .1, .2, .2)]);
            // Run a native subprocess through the shipped script so its stderr and exit code cross the PowerShell boundary.
            var worker = Path.Combine(root.FullName, "fixture-trainer.cmd");
            File.WriteAllText(worker, $"@echo off\r\n>&2 echo {diagnostic}\r\nexit /b 23\r\n");
            window = new MainWindow(GameRuntimeMode.Modded, null, root.FullName, root.FullName);
            var controls = (TrainingControlsPanel)window.FindName("TrainingControls");
            var status = (TextBlock)window.FindName("StatusText");
            var log = (TextBox)window.FindName("TrainingLogText");
            var missing = Path.Combine(root.FullName, "missing-fixture-trainer.exe");
            await VerifyFailure(missing, 1, $"Training environment missing at '{missing}'.");
            await VerifyFailure(worker, 23, diagnostic);
            Console.WriteLine("PASS Model Train keeps missing-runtime and worker diagnostics, exit codes, and logs and allows retry");

            async Task VerifyFailure(string python, int exitCode, string expected)
            {
                Environment.SetEnvironmentVariable(environment, python);
                Check(controls.TrainButton.IsEnabled, $"The saved Game Play label should enable model training. {status.Text}");
                controls.TrainButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var process = typeof(MainWindow).GetField("trainingProcessTask", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(window) as Task<TrainingProcessResult>
                    ?? throw new InvalidOperationException($"The Model Train button did not launch the packaged trainer. {status.Text}");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await process.WaitAsync(deadline.Token);
                while (controls.ProgressRow.Visibility == Visibility.Visible)
                    await Task.Delay(20, deadline.Token);
                Check(result.ExitCode == exitCode && !result.Cancelled && status.Text.StartsWith("Training failed.", StringComparison.Ordinal)
                    && status.Text.Contains(expected, StringComparison.Ordinal) && controls.TrainButton.IsEnabled
                    && log.Text.Contains(expected, StringComparison.Ordinal),
                    $"Object training must preserve the worker error and let the user retry. Exit {result.ExitCode}, status {status.Text}\n{log.Text}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environment, previousPython);
            if (window is not null)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            root.Delete(recursive: true);
        }
    }

    private static ProcessStartInfo ContainedTrainingCommand(string command)
    {
        var start = new ProcessStartInfo("powershell.exe");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$signal=[Threading.EventWaitHandle]::OpenExisting($env:HKV_TRAINER_START_SIGNAL); "
            + "try { if (-not $signal.WaitOne(10000)) { throw 'Probe gate timeout' } } finally { $signal.Dispose() }; " + command);
        return start;
    }
}
