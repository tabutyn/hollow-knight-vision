using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private CancellationTokenSource? botPreviewCancellation;
    private BotClipTimeline? botClip;
    private DispatcherTimer? botClipTimer;
    private readonly Stopwatch botClipClock = new();
    private double botClipStartTime;
    private bool updatingBotClipScrubber;
    private long botPreviewGeneration;
    private int botRequestedFrame = -1, botDecodedFrame = -1;
    private bool botImageLoading;

    private async void BotCaptureClips_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BotPreviewImage is null) return;
        ResetBotClipPreview();
        if (isClosing || BotCaptureClips.SelectedItem is not BotClipRow selected) return;
        using var cancellation = new CancellationTokenSource();
        botPreviewCancellation = cancellation;
        try
        {
            var timeline = await Task.Run(() => BotClipTimeline.Open(selected.Folder, InputPathRoot, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || isClosing) return;
            botClip = timeline;
            if (timeline.Count == 0)
            {
                BotPreviewPosition.Text = "No preview frames";
                return;
            }
            SetBotClipRange(timeline.Duration);
            BotPlayClip.IsEnabled = timeline.Duration > 0 && timeline.Count > 1;
            BotClipScrubber.IsEnabled = timeline.Duration > 0 && timeline.Count > 1;
            SeekBotClip(0);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            if (!cancellation.IsCancellationRequested) SetStatus($"Capture preview could not be read. {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(botPreviewCancellation, cancellation)) botPreviewCancellation = null;
        }
    }

    private void ResetBotClipPreview()
    {
        botPreviewCancellation?.Cancel();
        StopBotClipClock();
        botClip = null;
        botPreviewGeneration++;
        botRequestedFrame = botDecodedFrame = -1;
        BotPreviewImage.Source = null;
        BotPreviewOverlay.Children.Clear();
        BotPreviewPosition.Text = string.Empty;
        BotPlayClip.IsEnabled = BotPreviousFrame.IsEnabled = BotNextFrame.IsEnabled = BotClipScrubber.IsEnabled = false;
        SetBotClipRange(0);
    }

    private void SetBotClipRange(double duration)
    {
        updatingBotClipScrubber = true;
        BotClipScrubber.Maximum = duration;
        BotClipScrubber.Value = 0;
        updatingBotClipScrubber = false;
    }

    private void BotPlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (botClip is not { Count: > 1, Duration: > 0 } timeline) return;
        if (botClipClock.IsRunning)
        {
            StopBotClipClock();
            return;
        }
        if (BotClipScrubber.Value >= timeline.Duration) SeekBotClip(0);
        botClipStartTime = BotClipScrubber.Value;
        botClipTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1000d / 30), DispatcherPriority.Background,
            (_, _) => AdvanceBotClip(), Dispatcher);
        botClipClock.Restart();
        botClipTimer.Start();
        BotPlayClip.Content = "Pause";
    }

    private void StopBotClipClock()
    {
        botClipTimer?.Stop();
        botClipClock.Stop();
        BotPlayClip.Content = "Play";
    }

    private void AdvanceBotClip()
    {
        if (!botClipClock.IsRunning || botClip is not { } timeline) return;
        var offset = botClipStartTime + botClipClock.Elapsed.TotalSeconds;
        SeekBotClip(offset);
        if (offset >= timeline.Duration) StopBotClipClock();
    }

    private void BotClipScrubber_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingBotClipScrubber || botClip is null) return;
        StopBotClipClock();
        SeekBotClip(e.NewValue);
    }

    private void BotPreviousFrame_Click(object sender, RoutedEventArgs e) => StepBotClip(-1);
    private void BotNextFrame_Click(object sender, RoutedEventArgs e) => StepBotClip(1);

    private void StepBotClip(int step)
    {
        if (botClip is not { } timeline || botRequestedFrame + step < 0 || botRequestedFrame + step >= timeline.Count) return;
        StopBotClipClock();
        SeekBotClip(timeline.Offset(botRequestedFrame + step));
    }

    private void SeekBotClip(double offset)
    {
        if (botClip is not { } timeline) return;
        offset = Math.Clamp(offset, 0, timeline.Duration);
        updatingBotClipScrubber = true;
        BotClipScrubber.Value = offset;
        updatingBotClipScrubber = false;
        botRequestedFrame = timeline.FrameAt(offset);
        BotPreviousFrame.IsEnabled = botRequestedFrame > 0;
        BotNextFrame.IsEnabled = botRequestedFrame + 1 < timeline.Count;
        RequestBotClipImage();
    }

    private async void RequestBotClipImage()
    {
        if (isClosing || botImageLoading || botClip is not { } timeline || botRequestedFrame < 0
            || botRequestedFrame == botDecodedFrame) return;
        var index = botRequestedFrame;
        var generation = botPreviewGeneration;
        botDecodedFrame = index;
        botImageLoading = true;
        try
        {
            var image = await Task.Run(() => DecodeBotClipImage(timeline, index));
            if (isClosing || generation != botPreviewGeneration) return;
            BotPreviewImage.Width = BotPreviewOverlay.Width = image.PixelWidth;
            BotPreviewImage.Height = BotPreviewOverlay.Height = image.PixelHeight;
            BotPreviewImage.Source = image;
            if (timeline.Overlay(index) is { } overlay)
                BotCapturePreview.Draw(BotPreviewOverlay, overlay, new(image.PixelWidth, image.PixelHeight));
            else
                BotPreviewOverlay.Children.Clear();
            BotPreviewPosition.Text = $"{timeline.Offset(index):0.00} s · {index + 1} / {timeline.Count}";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or NotSupportedException or FileFormatException)
        {
            if (!isClosing && generation == botPreviewGeneration && index == botRequestedFrame)
                SetStatus($"Capture image could not be read. {error.Message}");
        }
        finally
        {
            botImageLoading = false;
            RequestBotClipImage();
        }
    }

    private static BitmapSource DecodeBotClipImage(BotClipTimeline timeline, int index)
    {
        BitmapSource image;
        if (timeline.UsesSourceFrames)
        {
            var frame = timeline.ReadSourceFrame(index);
            image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        }
        else
        {
            using var stream = File.OpenRead(timeline.StillPath(index));
            image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        image.Freeze();
        return image;
    }
}
