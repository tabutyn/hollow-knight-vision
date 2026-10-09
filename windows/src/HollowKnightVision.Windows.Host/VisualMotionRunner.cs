using System.Diagnostics;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

public static class VisualMotionRunner
{
    public static async Task<int> RunAsync(int durationSeconds, int intervalMilliseconds)
    {
        var window = HollowKnightWindowLocator.FindBest();
        if (window is null)
        {
            Console.Error.WriteLine("Hollow Knight window not found; visual tracking does not launch it.");
            return 3;
        }
        var capture = new GdiWindowCapture();
        var camera = new CameraAccumulator { Smoothing = 0.65 };
        var timer = Stopwatch.StartNew();
        LowResolutionMotionGrid? previous = null;
        long frameId = 0;
        var measured = 0;
        var accepted = 0;
        var rejections = new Dictionary<LowResolutionTranslationRejection, int>();
        LowResolutionMotionVector? latestMotion = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            var latestWindow = HollowKnightWindowLocator.FindBest();
            if (latestWindow is null)
            {
                Console.Error.WriteLine("Hollow Knight window closed during visual tracking.");
                return 3;
            }
            var frame = CpuBgraNormalizer.Normalize(capture.Capture(latestWindow, ++frameId));
            var grid = LowResolutionMotionGrid.FromFrame(frame);
            if (previous is not null)
            {
                measured++;
                var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(previous, grid);
                if (diagnostic.Motion is { } motion)
                {
                    latestMotion = motion;
                    var pixelsPerCellX = (double)frame.Width / grid.Width;
                    var pixelsPerCellY = (double)frame.Height / grid.Height;
                    var update = camera.Ingest(
                        -motion.ScreenShiftX * pixelsPerCellX,
                        -motion.ScreenShiftY * pixelsPerCellY,
                        (float)motion.Confidence,
                        frame.Width);
                    if (update.State == CameraUpdateState.Accepted) accepted++;
                }
                else if (diagnostic.FinalRejection is { } rejection)
                {
                    rejections[rejection] = rejections.GetValueOrDefault(rejection) + 1;
                }
            }
            previous = grid;
            if (intervalMilliseconds > 0)
            {
                await Task.Delay(intervalMilliseconds).ConfigureAwait(false);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            mode = "visual-track",
            elapsedSeconds = timer.Elapsed.TotalSeconds,
            frames = frameId,
            comparisons = measured,
            accepted,
            position = new { x = camera.Position.X, y = camera.Position.Y },
            latestMotion,
            rejections = rejections.ToDictionary(
                pair => pair.Key.ToString(), pair => pair.Value)
        }, new JsonSerializerOptions { WriteIndented = true }));
        return accepted > 0 ? 0 : 6;
    }
}
