using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>
/// Matches the neutral-bright text and selector pixels inside the user's own
/// label boxes. Templates remain local and work without receiver telemetry.
/// </summary>
internal sealed class WindowsMenuStencilRecognizer
{
    private sealed record Patch(int X, int Y, int Width, int Height, bool[] Bright, int BrightCount);
    private sealed record Template(string ContextIdentifier, Patch[] Patches);

    private readonly string examplesRoot;
    private Template[] templates = [];
    private Rect[] matchedBounds = [];
    private string language;
    internal bool HasGameplayReference => Volatile.Read(ref templates).Any(item => item.ContextIdentifier == "gameplay-hud");
    internal IReadOnlyList<Rect> MatchedBounds => Volatile.Read(ref matchedBounds);

    internal WindowsMenuStencilRecognizer(string examplesRoot, string language = "en")
    {
        this.examplesRoot = examplesRoot;
        this.language = language;
        Refresh();
    }

    internal void Refresh(string? language = null)
    {
        if (language is not null) this.language = language;
        var next = new List<Template>();
        foreach (var example in new LabelingExampleStore(examplesRoot).Load())
        {
            if (example.Manifest.ResolvedLanguageIdentifier != this.language) continue;
            var context = StencilInitializationPolicy.NormalizeContext(
                example.Manifest.ContextIdentifier);
            if (!StencilInitializationPolicy.RequiredContexts.Any(item => item.Identifier == context)) continue;
            if (example.Manifest.ImageWidth != 640 || example.Manifest.ImageHeight != 360
                || example.Manifest.Annotations.Count < (context == "gameplay-hud" ? 2 : 3)) continue;
            try
            {
                var image = LoadFrame(example.ImagePath);
                var patches = example.Manifest.Annotations
                    .Where(annotation => !annotation.IsHardNegative)
                    .Select(annotation => MakePatch(image, annotation))
                    .Where(patch => patch.BrightCount >= 3)
                    .ToArray();
                if (context == "gameplay-hud") {
                    var ordered = patches.OrderBy(patch => patch.X).ToArray();
                    if (ordered.Length != 2 || ordered.Any(patch => patch.Y < 0 || patch.Y + patch.Height > 80
                        || patch.X + patch.Width > 320) || ordered[0].X + ordered[0].Width > ordered[1].X
                        || Math.Abs(ordered[0].Y + ordered[0].Height / 2.0 - ordered[1].Y - ordered[1].Height / 2.0)
                            >= Math.Max(ordered[0].Height, ordered[1].Height) / 2.0) continue;
                }
                if (patches.Length >= (context == "gameplay-hud" ? 2 : 3)) next.Add(new Template(context, patches));
            }
            catch (Exception error) when (error is IOException
                or NotSupportedException or ArgumentException)
            {
            }
        }
        Volatile.Write(ref templates, next.ToArray());
    }

    internal string? Detect(BgraFrame frame)
    {
        var candidates = Volatile.Read(ref templates);
        string? bestContext = null;
        var bestScore = 0.0;
        Rect[] bounds = [];
        foreach (var template in candidates)
        {
            var scores = template.Patches.Select(patch => Compare(frame, patch)).ToArray();
            var matched = scores.Count(score => score >= 0.42);
            var minimum = template.ContextIdentifier == "gameplay-hud" ? 2 : 3;
            if (matched < Math.Max(minimum, (int)Math.Ceiling(scores.Length * 0.6))) continue;
            var score = scores.OrderByDescending(value => value)
                .Take(Math.Max(minimum, (int)Math.Ceiling(scores.Length * 0.75)))
                .Average();
            if (score > bestScore)
            {
                bestScore = score;
                bounds = template.Patches.Where((_, index) => scores[index] >= 0.42)
                    .Select(patch => new Rect(patch.X, patch.Y, patch.Width, patch.Height)).ToArray();
                bestContext = template.ContextIdentifier == "gameplay-hud" ? "game" : template.ContextIdentifier;
            }
        }
        Volatile.Write(ref matchedBounds, bestScore >= 0.48 ? bounds : []);
        return bestScore >= 0.48 ? bestContext : null;
    }

    private static Patch MakePatch(BgraFrame frame, LabelingExampleAnnotation annotation)
    {
        var x = Math.Clamp((int)Math.Floor(annotation.X * frame.Width), 0, frame.Width - 1);
        var y = Math.Clamp((int)Math.Floor(annotation.Y * frame.Height), 0, frame.Height - 1);
        var width = Math.Clamp((int)Math.Ceiling(annotation.Width * frame.Width), 1, frame.Width - x);
        var height = Math.Clamp((int)Math.Ceiling(annotation.Height * frame.Height), 1, frame.Height - y);
        var bright = new bool[width * height];
        var count = 0;
        for (var row = 0; row < height; row++)
        {
            var source = frame.Row(y + row);
            for (var column = 0; column < width; column++)
            {
                if (!IsNeutralBright(source, (x + column) * BgraFrame.BytesPerPixel)) continue;
                bright[row * width + column] = true;
                count++;
            }
        }
        return new Patch(x, y, width, height, bright, count);
    }

    private static double Compare(BgraFrame frame, Patch patch)
    {
        var width = Math.Min(patch.Width, frame.Width - patch.X);
        var height = Math.Min(patch.Height, frame.Height - patch.Y);
        if (width <= 0 || height <= 0) return 0;
        var liveCount = 0;
        var overlap = 0;
        for (var row = 0; row < height; row++)
        {
            var source = frame.Row(patch.Y + row);
            for (var column = 0; column < width; column++)
            {
                if (!IsNeutralBright(source, (patch.X + column) * BgraFrame.BytesPerPixel)) continue;
                liveCount++;
                if (patch.Bright[row * patch.Width + column]) overlap++;
            }
        }
        return patch.BrightCount + liveCount == 0
            ? 0
            : 2.0 * overlap / (patch.BrightCount + liveCount);
    }

    private static bool IsNeutralBright(ReadOnlySpan<byte> row, int offset)
    {
        var blue = row[offset];
        var green = row[offset + 1];
        var red = row[offset + 2];
        var minimum = Math.Min(red, Math.Min(green, blue));
        var maximum = Math.Max(red, Math.Max(green, blue));
        return minimum >= 115 && maximum - minimum <= 90;
    }

    private static BgraFrame LoadFrame(string path)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }
        var stride = source.PixelWidth * BgraFrame.BytesPerPixel;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return new BgraFrame(
            source.PixelWidth, source.PixelHeight, stride, pixels, 0,
            DateTimeOffset.UnixEpoch);
    }
}
