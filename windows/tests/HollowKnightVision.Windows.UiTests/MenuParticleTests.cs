using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class MenuParticleTests
{
    internal static void Run()
    {
        var particles = new MenuStencilParticles();
        var window = new Window { Content = particles, Width = 600, Height = 360, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show(); window.UpdateLayout();
            var reference = new ImageStencilReference(16, 8, Enumerable.Repeat((byte)255, 16 * 8 * 3).ToArray());
            var match = new MenuStencilMatch("shared.options", "Options", new(20, 20, 16, 8), 1, reference);
            var result = new MenuStencilResult("options", true, 1, null, [match], [], [], 0, 0, 1, MenuStencilPhase.Tracking, 0, 0, "en");
            particles.SetScene("options", result);
            var frame = new DispatcherFrame();
            var finish = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            finish.Tick += (_, _) => { finish.Stop(); frame.Continue = false; };
            finish.Start(); Dispatcher.PushFrame(frame);
            if (!HasPixels(particles) || particles.IsHitTestVisible) throw new Exception("Menu stencils did not rise from the viewport bottom without blocking input.");
            particles.SetScene("main-title", result);
            if (HasPixels(particles)) throw new Exception("Previous context's particles leaked into another menu.");
            particles.SetScene("game", result);
            if (HasPixels(particles)) throw new Exception("Menu particles leaked into gameplay.");
            particles.SetScene(null, null);
            if (HasPixels(particles)) throw new Exception("Menu particles survived loss of detected context.");
            Console.WriteLine("PASS active menu stencil particles render and clear on context changes without intercepting input");
        }
        finally { window.Close(); }
    }

    private static bool HasPixels(FrameworkElement element)
    {
        element.UpdateLayout();
        // Force a render after SetScene invalidates the retained drawing.
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var image = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] > 0);
    }
}
