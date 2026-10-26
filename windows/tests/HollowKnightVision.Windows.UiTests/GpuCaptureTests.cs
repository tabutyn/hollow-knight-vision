using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

internal static class GpuCaptureTests
{
    internal static void RunIfRequested()
    {
        // Real compositor coverage is opt-in on interactive Windows hosts.
        // CI's offscreen WPF tests do not have an interactive capture session.
        if (Environment.GetEnvironmentVariable("HKV_REQUIRE_GPU_CAPTURE") != "1") return;
        var surface = new Border { Background = Brushes.Black };
        var window = new Window { Title = "Vision capture regression", Content = surface,
            Width = 250, Height = 180, Left = 30, Top = 30, ShowActivated = false,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        Window? cover = null;
        using var capture = new WindowsGameCapture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        long id = 0;
        window.Show(); Pump();
        var target = new HollowKnightWindow(new WindowInteropHelper(window).Handle,
            Environment.ProcessId, "capture-test", window.Title, 250, 180, false);
        // Windows 11 rounds the bottom client corners in compositor output.
        // Use a rectangular test surface so uniform-pixel assertions test crop,
        // not those legitimate antialiased corner pixels.
        var noRounding = 1;
        _ = DwmSetWindowAttribute(target.Handle, 33, ref noRounding, sizeof(int));
        try
        {
            foreach (var value in new byte[] { 0, 4, 64, 255 })
            {
                surface.Background = new SolidColorBrush(Color.FromRgb(value, value, value));
                window.UpdateLayout(); Pump();
                AwaitColor(value);
            }
            window.Width = 320; window.Height = 240; window.UpdateLayout(); Pump();
            surface.Background = Brushes.Black; Pump(); AwaitColor(0);
            // A red covering window must never enter capture. The game's pixels
            // remain available while Vision itself covers the game in production.
            cover = new Window { Background = Brushes.Red, Width = 360, Height = 280,
                Left = 20, Top = 20, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
            cover.Show(); Pump();
            surface.Background = Brushes.White; Pump(); AwaitColor(255);
            if (capture.Backend != "Windows Graphics Capture")
                throw new Exception($"GPU capture fell back: {capture.FallbackReason}");
            using var stopped = new CancellationTokenSource(); stopped.Cancel();
            try
            {
                capture.CaptureAsync(target, ++id, stopped.Token).GetAwaiter().GetResult();
                throw new Exception("Canceled capture returned pixels.");
            }
            catch (OperationCanceledException) { }
            window.Close(); Pump();
            var closedTask = Task.Run(() => capture.CaptureAsync(target, ++id, cancellation.Token));
            while (!closedTask.IsCompleted) Pump();
            if (closedTask.GetAwaiter().GetResult() is not null) throw new Exception("Closed capture returned stale pixels.");
            Console.WriteLine("PASS GPU window capture: fades, client crop, resize, occlusion, cancellation, close");
        }
        finally { cover?.Close(); window.Close(); }

        void AwaitColor(byte value)
        {
            var deadline = Stopwatch.StartNew();
            var received = 0;
            string lastPixels = "none";
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                var task = Task.Run(() => capture.CaptureAsync(target, ++id, cancellation.Token));
                while (!task.IsCompleted) Pump();
                var frame = task.GetAwaiter().GetResult();
                if (frame is null) continue;
                received++;
                if (capture.CaptureTimestamp > Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)
                    throw new Exception("Capture published a future timestamp.");
                lastPixels = string.Join(",", new[] { frame.Pixels[2 * frame.Stride + 8], frame.Pixels[180 * frame.Stride + 1280], frame.Pixels[357 * frame.Stride + 2548] });
                if (frame.Width != 640 || frame.Height != 360) throw new Exception("Unexpected capture dimensions.");
                var matches = true;
                foreach (var (x, y) in new[] { (2, 2), (637, 2), (2, 357), (637, 357), (320, 180) })
                for (var channel = 0; channel < 3; channel++)
                    matches &= Math.Abs(frame.Pixels[y * frame.Stride + x * 4 + channel] - value) <= 1;
                if (matches) return;
            }
            throw new Exception($"GPU capture did not receive uniform client color {value}; backend={capture.Backend}, fallback={capture.FallbackReason}; frames={received}, pixels={lastPixels}, issue={capture.LastFrameIssue}.");
        }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Thread.Sleep(1);
    }
}
