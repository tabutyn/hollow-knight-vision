using System.IO;
using System.Text;
using HollowKnightVision.Windows.Core;
using Tesseract;

namespace HollowKnightVision.Windows.App;

internal sealed class WindowsGameTextRecognition : IDisposable
{
    private readonly string dataRoot = Path.Combine(AppContext.BaseDirectory, "game-text", "tessdata");
    private TesseractEngine? engine;
    private string? loadedLanguage;

    internal static string DataLanguage(string language) => language switch
    {
        "en" => "eng",
        "es" => "spa",
        "fr" => "fra",
        "it" => "ita",
        "ja" => "jpn",
        "ko" => "kor",
        "pt-BR" => "por",
        "ru" => "rus",
        "zh-Hans" => "chi_sim",
        "zh-Hant" => "chi_tra",
        "de" => "deu",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported game text language.")
    };

    internal IReadOnlyList<GameTextLine> Read(BgraFrame frame, string language)
    {
        var dataLanguage = DataLanguage(language);
        if (loadedLanguage != dataLanguage || engine is null)
        {
            Dispose();
            if (!File.Exists(Path.Combine(dataRoot, dataLanguage + ".traineddata")))
                throw new FileNotFoundException("Game text language data is missing from the Vision installation.");
            engine = new(dataRoot, dataLanguage, EngineMode.LstmOnly, [], new Dictionary<string, object>
            {
                ["load_system_dawg"] = false,
                ["load_freq_dawg"] = false
            }, false);
            engine.SetVariable("user_defined_dpi", 150);
            loadedLanguage = dataLanguage;
        }

        using var image = Pix.LoadFromMemory(EncodeImage(frame));
        using var page = engine.Process(image, PageSegMode.SparseText);
        using var iterator = page.GetIterator();
        iterator.Begin();
        var lines = new List<GameTextLine>();
        do
        {
            const PageIteratorLevel level = PageIteratorLevel.TextLine;
            var text = iterator.GetText(level)?.Trim();
            if (string.IsNullOrEmpty(text) || !iterator.TryGetBoundingBox(level, out var bounds)) continue;
            var confidence = iterator.GetConfidence(level) / 100d;
            var normalized = new StencilRect(bounds.X1 / (double)image.Width, bounds.Y1 / (double)image.Height,
                bounds.Width / (double)image.Width, bounds.Height / (double)image.Height);
            lines.Add(new(lines.Count + 1, text, normalized, confidence));
        } while (iterator.Next(PageIteratorLevel.TextLine));
        return lines;
    }

    private static byte[] EncodeImage(BgraFrame frame)
    {
        var factor = Math.Min(2, 1280d / Math.Max(frame.Width, frame.Height));
        var width = Math.Max(1, (int)Math.Round(frame.Width * factor));
        var height = Math.Max(1, (int)Math.Round(frame.Height * factor));
        var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"P5\n{width} {height}\n255\n"));
        var pixels = new byte[header.Length + width * height];
        header.CopyTo(pixels, 0);
        for (var y = 0; y < height; y++)
        {
            var row = frame.Row((int)((long)y * frame.Height / height));
            var target = pixels.AsSpan(header.Length + y * width, width);
            for (var x = 0; x < width; x++)
            {
                var source = (int)((long)x * frame.Width / width) * BgraFrame.BytesPerPixel;
                // Game menus and dialogue put light glyphs on dark backgrounds.
                var luminance = (29 * row[source] + 150 * row[source + 1] + 77 * row[source + 2] + 128) >> 8;
                target[x] = (byte)(255 - luminance);
            }
        }
        return pixels;
    }

    public void Dispose()
    {
        engine?.Dispose();
        engine = null;
        loadedLanguage = null;
    }
}
