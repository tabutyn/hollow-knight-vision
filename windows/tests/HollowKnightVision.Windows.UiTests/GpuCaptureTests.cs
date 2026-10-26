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
    internal static void TestMappedBounds()
    {
        const int width = 7, height = 5, pitch = 32;
        var source = Enumerable.Range(0, pitch * height).Select(i => (byte)i).ToArray();
        var data = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, data, source.Length);
            var result = WindowsGameCapture.CopyMappedClient(data, pitch, width, height, new(2, 1, 5, 4));
            var expected = Enumerable.Range(1, 4).SelectMany(y => source.Skip(y * pitch + 8).Take(20));
            if (result is null || !result.SequenceEqual(expected))
                throw new Exception("Mapped crop did not preserve padded row data through the final pixel.");
            // A resized client can fit ContentSize while exceeding an older texture.
            foreach (var crop in new PixelRect[] { new(0, 0, 8, 5), new(0, 0, 7, 6),
                new(-1, 0, 1, 1), new(0, -1, 1, 1), new(0, 0, 0, 1), new(int.MaxValue, 0, 2, 1) })
                if (WindowsGameCapture.CopyMappedClient(data, pitch, width, height, crop) is not null)
                    throw new Exception($"Out-of-texture crop accepted: {crop}");
            if (WindowsGameCapture.CopyMappedClient(data, 16, width, height, new(0, 0, 1, 1)) is not null
                || WindowsGameCapture.CopyMappedClient(0, pitch, width, height, new(0, 0, 1, 1)) is not null
                || WindowsGameCapture.CopyMappedClient(data, uint.MaxValue, width, height, new(0, 0, 1, 2)) is not null)
                throw new Exception("Invalid mapped pointer/pitch accepted.");
        }
        finally { Marshal.FreeHGlobal(data); }
        Console.WriteLine("PASS GPU readback rejects stale texture bounds and preserves padded client pixels");
    }

    internal static void RunIfRequested()
    {
        // Real compositor coverage is opt-in on interactive Windows hosts.
        // CI's offscreen WPF tests do not have an interactive capture session.
        if (Environment.GetEnvironmentVariable("HKV_REQUIRE_GPU_CAPTURE") != "1") return;
        var surface = new Border { Background = Brushes.Black };
        // WGC may suppress unchanged frames after pool recreation. Keep a small
        // marker moving through colors outside all sampled pixels, as a live
        // game does, so resize tests do not depend on static-frame publication.
        var marker = new Border { Width = 1, Height = 1, Margin = new Thickness(16, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var content = new Grid(); content.Children.Add(surface); content.Children.Add(marker);
        var markerLight = false;
        var window = new Window { Title = "Vision capture regression", Content = content,
            Width = 250, Height = 180, Left = 30, Top = 30, ShowActivated = false,
            // Ensure WPF presents the initial surface even if the game covers
            // the desktop. Occluded capture is tested after initial rendering.
            Topmost = true, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        Window? cover = null;
        using var capture = new WindowsGameCapture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
            for (var resize = 0; resize < 12; resize++)
            {
                window.Width = resize % 2 == 0 ? 321 : 247;
                window.Height = resize % 2 == 0 ? 243 : 177;
                surface.Background = resize % 2 == 0 ? Brushes.Black : Brushes.White;
                window.UpdateLayout(); Pump(); AwaitColor(resize % 2 == 0 ? (byte)0 : (byte)255);
            }
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
                markerLight = !markerLight;
                marker.Background = markerLight ? Brushes.White : Brushes.Black;
                Pump();
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
