namespace HollowKnightVision.Windows.Core;

/// <summary>Exact RGB reference and sparse matching mask. Coordinates remain top-origin on Windows.</summary>
public sealed record ImageStencilReference(int Width, int Height, byte[] Rgb, byte[]? Mask = null)
{
    public bool IsValid => Width > 0 && Height > 0 && Rgb is not null
        && Rgb.LongLength == (long)Width * Height * 3 && (Mask is null || Mask.LongLength == (long)Width * Height);

    public BgraFrame? MakeFrame()
    {
        if (!IsValid) return null;
        var bytes = new byte[checked(Width * Height * 4)];
        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            bytes[pixel * 4] = Rgb[pixel * 3 + 2];
            bytes[pixel * 4 + 1] = Rgb[pixel * 3 + 1];
            bytes[pixel * 4 + 2] = Rgb[pixel * 3];
            bytes[pixel * 4 + 3] = Mask?[pixel] ?? 255;
        }
        return new(Width, Height, Width * 4, bytes, 0, DateTimeOffset.UnixEpoch);
    }

    public StencilRect VerifiedBounds(StencilRect destination, double padding = 1)
    {
        if (!IsValid || Mask is null) return destination;
        var minX = Width; var minY = Height; var maxX = -1; var maxY = -1;
        for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++)
        {
            if (Mask[y * Width + x] == 0) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        if (maxX < minX || maxY < minY) return destination;
        return new StencilRect(destination.X + minX * destination.Width / Width,
            destination.Y + minY * destination.Height / Height, (maxX - minX + 1) * destination.Width / Width,
            (maxY - minY + 1) * destination.Height / Height).Expand(padding).Intersect(destination);
    }
}

public sealed class ImageStencilKernel
{
    public readonly record struct Sample(int X, int Y, double Red, double Green, double Blue)
    {
        public double Luma => (Red + 2 * Green + Blue) / 4;
    }
    public string Kind { get; }
    public StencilRect Bounds { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<Sample> Samples { get; }
    public double Sum { get; }
    public double Energy { get; }

    public ImageStencilKernel(string kind, StencilRect bounds, byte[] rgb, int sampleStride = 1,
        Func<int, int, int, int, bool>? includes = null)
    {
        Kind = kind; Bounds = bounds;
        Width = bounds.IsFinite && bounds.Width is > 0 and <= int.MaxValue ? (int)bounds.Width : 0;
        Height = bounds.IsFinite && bounds.Height is > 0 and <= int.MaxValue ? (int)bounds.Height : 0;
        var samples = new List<Sample>();
        var stride = Math.Max(1, sampleStride);
        if (Width > 0 && Height > 0 && rgb.LongLength == (long)Width * Height * 3)
            for (var y = 0; y < Height; y += stride) for (var x = 0; x < Width; x += stride)
            {
                if (includes?.Invoke(x, y, Width, Height) == false) continue;
                var index = (y * Width + x) * 3;
                samples.Add(new(x, y, rgb[index], rgb[index + 1], rgb[index + 2]));
            }
        Samples = samples.ToArray();
        Sum = samples.Sum(sample => sample.Luma);
        Energy = samples.Sum(sample => sample.Luma * sample.Luma) - Sum * Sum / Math.Max(1, samples.Count);
    }

    public static ImageStencilKernel FromReference(string kind, ImageStencilReference reference, int sampleStride = 1) =>
        new(kind, new(0, 0, reference.Width, reference.Height), reference.Rgb, sampleStride,
            (x, y, width, _) => reference.Mask is null || reference.Mask[y * width + x] != 0);
}

public sealed record ImageStencilComparison(double NormalizedCorrelation, double MeanAbsoluteColorError,
    double MeanRed, double MeanBlue, bool SupportsCorrelation)
{
    public double Confidence(double correlationWeight, double colorErrorScale)
    {
        var colorSimilarity = Math.Max(0, 1 - MeanAbsoluteColorError / colorErrorScale);
        return SupportsCorrelation ? Math.Clamp(correlationWeight * NormalizedCorrelation
            + (1 - correlationWeight) * colorSimilarity, 0, 1) : colorSimilarity;
    }
}

public sealed class ImageStencilForegroundMask
{
    public StencilRect Bounds { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Mask { get; }
    private ImageStencilForegroundMask(StencilRect bounds, byte[] mask)
    {
        Bounds = bounds; Width = (int)bounds.Width; Height = (int)bounds.Height; Mask = mask;
    }
    public static ImageStencilForegroundMask? Create(IReadOnlyList<ImageStencilPixels> frames, StencilRect bounds)
    {
        if (frames.Count < 2 || !bounds.IsFinite) return null;
        var first = frames[0];
        var x0 = Math.Max(0, (int)Math.Floor(bounds.X)); var y0 = Math.Max(0, (int)Math.Floor(bounds.Y));
        var x1 = Math.Min(first.Width, (int)Math.Ceiling(bounds.Right));
        var y1 = Math.Min(first.OriginY + first.Height, (int)Math.Ceiling(bounds.Bottom));
        if (x1 <= x0 || y1 <= y0) return null;
        var required = Math.Max(2, (int)Math.Ceiling(frames.Count * .5));
        var width = x1 - x0; var height = y1 - y0; var mask = new byte[checked(width * height)];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            if (frames.Count(frame => frame.IsLikelyMenuText(x0 + x, y0 + y)) >= required) mask[y * width + x] = 255;
        return new(new(x0, y0, width, height), mask);
    }
    public bool Contains(int x, int y)
    {
        var localX = x - (int)Bounds.X; var localY = y - (int)Bounds.Y;
        return localX >= 0 && localX < Width && localY >= 0 && localY < Height && Mask[localY * Width + localX] != 0;
    }
}

/// <summary>A reference-sized vertical band, with Mac's RGB/color-correlation matching rules.</summary>
public sealed class ImageStencilPixels
{
    private readonly byte[] bytes; // RGB, not capture-native BGRA
    public int Width { get; }
    public int Height { get; }
    public int OriginY { get; }

    public ImageStencilPixels(BgraFrame frame, int referenceWidth, int referenceHeight, int bandStart = 0, int? bandEnd = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceHeight);
        Width = referenceWidth;
        OriginY = Math.Clamp(bandStart, 0, referenceHeight - 1);
        var upper = Math.Clamp(bandEnd ?? referenceHeight, OriginY + 1, referenceHeight);
        Height = upper - OriginY;
        bytes = new byte[checked(Width * Height * 3)];
        var sourceMinY = (int)Math.Floor((double)frame.Height * OriginY / referenceHeight);
        var sourceMaxY = (int)Math.Ceiling((double)frame.Height * upper / referenceHeight);
        var sourceHeight = Math.Max(1, sourceMaxY - sourceMinY);
        for (var y = 0; y < Height; y++)
        {
            var sy = Math.Clamp((y + .5) * sourceHeight / Height - .5, 0, sourceHeight - 1);
            var y0 = (int)Math.Floor(sy); var y1 = Math.Min(y0 + 1, sourceHeight - 1); var fy = sy - y0;
            var top = frame.Row(sourceMinY + y0); var bottom = frame.Row(sourceMinY + y1);
            for (var x = 0; x < Width; x++)
            {
                var sx = Math.Clamp((x + .5) * frame.Width / Width - .5, 0, frame.Width - 1);
                var x0 = (int)Math.Floor(sx); var x1 = Math.Min(x0 + 1, frame.Width - 1); var fx = sx - x0;
                for (var c = 0; c < 3; c++)
                {
                    var channel = 2 - c;
                    var a = top[x0 * 4 + channel] * (1 - fx) + top[x1 * 4 + channel] * fx;
                    var b = bottom[x0 * 4 + channel] * (1 - fx) + bottom[x1 * 4 + channel] * fx;
                    bytes[(y * Width + x) * 3 + c] = (byte)Math.Round(a * (1 - fy) + b * fy, MidpointRounding.AwayFromZero);
                }
            }
        }
    }

    public (byte Red, byte Green, byte Blue)? Rgb(int x, int y)
    {
        var localY = y - OriginY;
        if (x < 0 || x >= Width || localY < 0 || localY >= Height) return null;
        var index = (localY * Width + x) * 3;
        return (bytes[index], bytes[index + 1], bytes[index + 2]);
    }

    public ImageStencilComparison? Compare(ImageStencilKernel kernel, StencilRect rect)
    {
        if (kernel.Samples.Count == 0 || !rect.IsFinite || rect.IsEmpty || rect.X < 0 || rect.Y < OriginY
            || rect.Right > Width || rect.Bottom > OriginY + Height) return null;
        var count = kernel.Samples.Count;
        var originX = (int)rect.X; var originY = (int)rect.Y - OriginY;
        var scaleX = rect.Width / kernel.Width; var scaleY = rect.Height / kernel.Height;
        var native = Math.Abs(rect.Width - kernel.Width) < .001 && Math.Abs(rect.Height - kernel.Height) < .001;
        double sum = 0, squaredSum = 0, cross = 0, error = 0, redSum = 0, blueSum = 0;
        foreach (var sample in kernel.Samples)
        {
            var x = originX + (native ? sample.X : (int)((sample.X + .5) * scaleX));
            var y = originY + (native ? sample.Y : (int)((sample.Y + .5) * scaleY));
            if (x < 0 || x >= Width || y < 0 || y >= Height) return null;
            var index = (y * Width + x) * 3;
            double red = bytes[index], green = bytes[index + 1], blue = bytes[index + 2];
            var luma = (red + 2 * green + blue) / 4;
            redSum += red; blueSum += blue; sum += luma; squaredSum += luma * luma; cross += sample.Luma * luma;
            error += Math.Abs(sample.Red - red) + Math.Abs(sample.Green - green) + Math.Abs(sample.Blue - blue);
        }
        var variance = squaredSum - sum * sum / count;
        var correlation = kernel.Energy > count * 3 && variance > count * 3;
        return new(correlation ? (cross - kernel.Sum * sum / count) / Math.Sqrt(kernel.Energy * variance) : 0,
            error / (count * 3), redSum / count, blueSum / count, correlation);
    }

    public ImageStencilReference? Reference(StencilRect rect)
    {
        if (!rect.IsFinite) return null;
        var x0 = (int)Math.Floor(rect.X); var y0 = (int)Math.Floor(rect.Y);
        var x1 = (int)Math.Ceiling(rect.Right); var y1 = (int)Math.Ceiling(rect.Bottom);
        if (x0 < 0 || y0 < OriginY || x1 > Width || y1 > OriginY + Height || x1 <= x0 || y1 <= y0) return null;
        var width = x1 - x0; var height = y1 - y0; var rgb = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
            Array.Copy(bytes, ((y0 - OriginY + y) * Width + x0) * 3, rgb, y * width * 3, width * 3);
        return new(width, height, rgb);
    }

    public bool IsLikelyMenuText(int x, int y)
    {
        if (Rgb(x, y) is not { } color) return false;
        var minimum = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        var chroma = Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - minimum;
        var weight = minimum - Math.Max(0, chroma - 18) * 3 / 2 - 52; // integer division matches ImageStencil.swift
        return minimum >= 66 && chroma <= 58 && weight >= 12;
    }
    public int LikelyMenuTextPixelCount(StencilRect rect, ImageStencilForegroundMask? excluding = null) => PixelCount(rect, excluding, false);
    public int BrightMenuPixelCount(StencilRect rect, ImageStencilForegroundMask? excluding = null) => PixelCount(rect, excluding, true);
    private int PixelCount(StencilRect rect, ImageStencilForegroundMask? excluding, bool bright)
    {
        if (!rect.IsFinite) return 0;
        var x0 = Math.Max(0, (int)Math.Floor(rect.X)); var y0 = Math.Max(OriginY, (int)Math.Floor(rect.Y));
        var x1 = Math.Min(Width, (int)Math.Ceiling(rect.Right)); var y1 = Math.Min(OriginY + Height, (int)Math.Ceiling(rect.Bottom));
        var count = 0;
        for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++)
        {
            if (excluding?.Contains(x, y) == true) continue;
            if (!bright) { if (IsLikelyMenuText(x, y)) count++; continue; }
            if (Rgb(x, y) is not { } color) continue;
            var minimum = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
            if (minimum >= 100 && Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - minimum <= 58) count++;
        }
        return count;
    }
}
