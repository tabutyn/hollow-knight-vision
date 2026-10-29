using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>Cached stencil sprites rising from the viewport bottom, independent of atlas zoom.</summary>
public sealed class MenuStencilParticles : FrameworkElement
{
    private readonly DispatcherTimer timer;
    private readonly Stopwatch elapsed = new();
    private string? context;
    private ImageStencilReference[] references = [];
    private BitmapSource[] icons = [];

    public MenuStencilParticles()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 24) };
        timer.Tick += (_, _) => InvalidateVisual();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    public void SetScene(string? recognizedContext, MenuStencilResult? result)
    {
        if (recognizedContext is null or "game" or "gameplay-hud" or "credits")
        {
            context = null; references = []; icons = [];
            elapsed.Reset(); UpdateTimer(); InvalidateVisual(); return;
        }
        var changed = context != recognizedContext;
        if (changed) { context = recognizedContext; references = []; icons = []; elapsed.Restart(); }
        // A skipped recognition tick may reuse the last accepted scene, but never another context.
        if (result is { IsMatch: true } && result.ContextIdentifier == recognizedContext)
        {
            var next = result.Anchors.Concat(result.SelectorCandidates).Where(match => match.Reference is { IsValid: true })
                .Select(match => match.Reference!).Distinct().Take(12).ToArray();
            if (!next.SequenceEqual(references))
            {
                references = next;
                icons = next.Select(reference =>
                {
                    var frame = reference.MakeFrame()!;
                    var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
                    bitmap.Freeze(); return bitmap;
                }).ToArray();
            }
        }
        UpdateTimer(); InvalidateVisual();
    }

    private void UpdateTimer()
    {
        if (IsVisible && icons.Length > 0) { elapsed.Start(); timer.Start(); }
        else { elapsed.Stop(); timer.Stop(); }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (icons.Length == 0) return;
        var count = Math.Max(MenuObjectParticleMotion.DefaultParticleCount, icons.Length);
        foreach (var index in MenuObjectParticleMotion.RenderOrder(count))
        {
            var placement = MenuObjectParticleMotion.Placement(elapsed.Elapsed.TotalSeconds, index, count, new(ActualWidth, ActualHeight));
            if (placement is null || placement.Opacity <= 0) continue;
            var icon = icons[index % icons.Length];
            var scale = MenuObjectParticleMotion.IconSize * placement.Scale / Math.Max(icon.PixelWidth, icon.PixelHeight);
            drawing.PushOpacity(placement.Opacity);
            drawing.PushTransform(new RotateTransform(placement.RotationDegrees, placement.Position.X, placement.Position.Y));
            drawing.DrawImage(icon, new Rect(placement.Position.X - icon.PixelWidth * scale / 2,
                placement.Position.Y - icon.PixelHeight * scale / 2, icon.PixelWidth * scale, icon.PixelHeight * scale));
            drawing.Pop(); drawing.Pop();
        }
    }
}
