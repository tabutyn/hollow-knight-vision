using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public enum GroundLabelSplit
{
    Train,
    Check
}

public sealed record GroundLabelEdge
{
    public GroundLabelEdge(int x0, int x1, int y)
        : this(Guid.NewGuid(), x0, x1, y) { }

    [JsonConstructor]
    public GroundLabelEdge(Guid id, int x0, int x1, int y)
    {
        Id = id;
        X0 = x0;
        X1 = x1;
        Y = y;
    }

    public Guid Id { get; init; }
    public int X0 { get; init; }
    public int X1 { get; init; }
    public int Y { get; init; }
}

public sealed record GroundLabelRect
{
    public GroundLabelRect(double x, double y, double width, double height)
        : this(Guid.NewGuid(), x, y, width, height) { }

    [JsonConstructor]
    public GroundLabelRect(Guid id, double x, double y, double width, double height)
    {
        Id = id;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public Guid Id { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public StencilRect BottomLeft(int imageHeight) =>
        new(X, imageHeight - Y - Height, Width, Height);

    public static GroundLabelRect FromBottomLeft(StencilRect rect, int imageHeight) =>
        new(rect.X, imageHeight - rect.Y - rect.Height, rect.Width, rect.Height);

    internal bool ContainsPixelCenter(int x, int y) =>
        x + .5 >= X && x + .5 < X + Width
        && y + .5 >= Y && y + .5 < Y + Height;
}

public sealed class GroundLabelDocument : IEquatable<GroundLabelDocument>
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ImageFile { get; set; } = "";
    public string ImageSHA256 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Source { get; set; } = "";
    public string Group { get; set; } = "";
    public GroundLabelSplit Split { get; set; } = GroundLabelSplit.Train;
    public bool FullyReviewed { get; set; }
    public List<GroundLabelEdge> Edges { get; set; } = [];
    public List<GroundLabelRect> IgnoredRegions { get; set; } = [];
    public List<GroundLabelRect> RuntimeForeground { get; set; } = [];
    public GroundLabelRect? RuntimeKnight { get; set; }
    public bool RuntimeMasksKnown { get; set; }
    public bool RuntimeKnightKnown { get; set; }

    public void Validate()
    {
        if (SchemaVersion != 1
            || Width is < 2 or > 4096
            || Height is < 2 or > 4096
            || ImageFile is null
            || !string.Equals(ImageFile, GroundLabelStore.UpperId(Id) + ".png", StringComparison.Ordinal)
            || ImageSHA256 is null
            || ImageSHA256.Length != 64
            || ImageSHA256.Any(value => !Uri.IsHexDigit(value))
            || Source is null
            || Group is null
            || string.IsNullOrWhiteSpace(Group)
            || Group.Length > 120
            || Edges is null
            || IgnoredRegions is null
            || RuntimeForeground is null
            || Edges.Count > 10_000
            || IgnoredRegions.Count > 500
            || RuntimeForeground.Count > 500)
        {
            throw new GroundLabelException("Invalid frame metadata");
        }

        if (Edges.Select(edge => edge.Id).Distinct().Count() != Edges.Count
            || Edges.Any(edge => edge.X0 < 0
                || edge.X1 < edge.X0
                || edge.X1 >= Width
                || edge.Y < 0
                || edge.Y >= Height))
        {
            throw new GroundLabelException("Ground edges must lie inside the image");
        }

        foreach (var region in IgnoredRegions
                     .Concat(RuntimeForeground)
                     .Concat(RuntimeKnight is null ? [] : [RuntimeKnight]))
        {
            if (!double.IsFinite(region.X)
                || !double.IsFinite(region.Y)
                || !double.IsFinite(region.Width)
                || !double.IsFinite(region.Height)
                || region.Width <= 0
                || region.Height <= 0
                || Math.Abs(region.X) > 8192
                || Math.Abs(region.Y) > 8192
                || region.Width > 8192
                || region.Height > 8192)
            {
                throw new GroundLabelException("Invalid ignore region or capture mask");
            }
        }
    }

    public bool Equals(GroundLabelDocument? other) => other is not null
        && SchemaVersion == other.SchemaVersion
        && Id == other.Id
        && ImageFile == other.ImageFile
        && ImageSHA256 == other.ImageSHA256
        && Width == other.Width
        && Height == other.Height
        && CreatedAt.Equals(other.CreatedAt)
        && Source == other.Source
        && Group == other.Group
        && Split == other.Split
        && FullyReviewed == other.FullyReviewed
        && Edges.SequenceEqual(other.Edges)
        && IgnoredRegions.SequenceEqual(other.IgnoredRegions)
        && RuntimeForeground.SequenceEqual(other.RuntimeForeground)
        && Equals(RuntimeKnight, other.RuntimeKnight)
        && RuntimeMasksKnown == other.RuntimeMasksKnown
        && RuntimeKnightKnown == other.RuntimeKnightKnown;

    public override bool Equals(object? obj) => Equals(obj as GroundLabelDocument);

    public override int GetHashCode() => HashCode.Combine(
        SchemaVersion,
        Id,
        ImageFile,
        ImageSHA256,
        Width,
        Height,
        CreatedAt,
        Group);
}

public sealed class GroundLabelException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class GroundLabelStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public GroundLabelStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public static string Digest(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public IReadOnlyList<GroundLabelDocument> Documents()
    {
        Directory.CreateDirectory(RootDirectory);
        var documents = new List<GroundLabelDocument>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            GroundLabelDocument document;
            try
            {
                document = JsonSerializer.Deserialize<GroundLabelDocument>(
                    File.ReadAllBytes(path),
                    JsonOptions) ?? throw new JsonException("Ground-label document is empty.");
                document.Validate();
            }
            catch (GroundLabelException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException
                or IOException or NotSupportedException)
            {
                throw new GroundLabelException("Ground-label document cannot be decoded", exception);
            }

            if (!string.Equals(
                    Path.GetFileName(path),
                    UpperId(document.Id) + ".json",
                    StringComparison.Ordinal))
            {
                throw new GroundLabelException("Frame filename does not match its identifier");
            }
            documents.Add(document);
        }
        return documents.OrderBy(document => document.CreatedAt).ToArray();
    }

    public BgraFrame Image(GroundLabelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        try
        {
            var data = File.ReadAllBytes(Path.Combine(RootDirectory, document.ImageFile));
            if (!string.Equals(Digest(data), document.ImageSHA256, StringComparison.OrdinalIgnoreCase))
                throw new GroundLabelException("Source image changed or cannot be decoded");
            var image = GroundLabelPngDecoder.Decode(data, document.CreatedAt);
            if (image.Width != document.Width || image.Height != document.Height)
                throw new GroundLabelException("Source image changed or cannot be decoded");
            return image;
        }
        catch (GroundLabelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or NotSupportedException or OverflowException)
        {
            throw new GroundLabelException("Source image changed or cannot be decoded", exception);
        }
    }

    public void Save(GroundLabelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var others = Documents().Where(candidate => candidate.Id != document.Id).ToArray();
        ValidateSplits([.. others, document]);
        AtomicFile.Write(
            Path.Combine(RootDirectory, UpperId(document.Id) + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
    }

    public static void ValidateSplits(IEnumerable<GroundLabelDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var groups = new Dictionary<string, GroundLabelSplit>(StringComparer.Ordinal);
        var images = new Dictionary<string, GroundLabelSplit>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var group = document.Group.Trim();
            if (groups.TryGetValue(group, out var groupSplit) && groupSplit != document.Split)
            {
                throw new GroundLabelException(
                    $"Group ‘{group}’ cannot appear in both Train and Check. Use a separate capture session for Check.");
            }
            if (images.TryGetValue(document.ImageSHA256, out var imageSplit)
                && imageSplit != document.Split)
            {
                throw new GroundLabelException("The same source image cannot appear in both Train and Check");
            }
            groups[group] = document.Split;
            images[document.ImageSHA256] = document.Split;
        }
    }

    public GroundLabelDocument Add(
        BgraFrame image,
        string source,
        string group,
        IReadOnlyList<GroundLabelRect>? foreground = null,
        GroundLabelRect? knight = null,
        bool masksKnown = false,
        bool knightKnown = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width is < 2 or > 4096 || image.Height is < 2 or > 4096)
            throw new GroundLabelException("Images must be between 2 and 4096 pixels on each side");

        Directory.CreateDirectory(RootDirectory);
        var id = Guid.NewGuid();
        var finalFile = UpperId(id) + ".png";
        var finalPath = Path.Combine(RootDirectory, finalFile);
        PngWriter.Write(image, finalPath);
        try
        {
            var data = File.ReadAllBytes(finalPath);
            var normalizedGroup = group?.Trim() ?? "";
            var split = Documents().FirstOrDefault(document =>
                    string.Equals(document.Group.Trim(), normalizedGroup, StringComparison.Ordinal))
                ?.Split ?? GroundLabelSplit.Train;
            var document = new GroundLabelDocument
            {
                Id = id,
                ImageFile = finalFile,
                ImageSHA256 = Digest(data),
                Width = image.Width,
                Height = image.Height,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Source = source ?? "",
                Group = group ?? "",
                Split = split,
                RuntimeForeground = foreground?.ToList() ?? [],
                RuntimeKnight = knight,
                RuntimeMasksKnown = masksKnown,
                RuntimeKnightKnown = knightKnown
            };
            Save(document);
            return document;
        }
        catch
        {
            try { File.Delete(finalPath); } catch (IOException) { }
            throw;
        }
    }

    internal static string UpperId(Guid id) => id.ToString("D").ToUpperInvariant();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new UpperGuidConverter());
        options.Converters.Add(new AppleReferenceDateConverter());
        return options;
    }

    private sealed class UpperGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Guid.Parse(reader.GetString() ?? throw new JsonException("Missing UUID."));

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(UpperId(value));
    }

    private sealed class AppleReferenceDateConverter : JsonConverter<DateTimeOffset>
    {
        private static readonly DateTimeOffset ReferenceDate =
            new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return ReferenceDate.AddSeconds(reader.GetDouble());
            if (reader.TokenType == JsonTokenType.String
                && DateTimeOffset.TryParse(
                    reader.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var value))
            {
                return value;
            }
            throw new JsonException("Invalid ground-label creation date.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue((value.ToUniversalTime() - ReferenceDate).TotalSeconds);
    }
}

public readonly record struct GroundLabelSize(double Width, double Height);

public static class GroundLabelCoordinates
{
    public static SourcePoint? Pixel(
        SourcePoint point,
        GroundLabelSize displayedSize,
        int width,
        int height)
    {
        if (displayedSize.Width <= 0
            || displayedSize.Height <= 0
            || point.X < 0
            || point.Y < 0
            || point.X >= displayedSize.Width
            || point.Y >= displayedSize.Height)
        {
            return null;
        }
        return new SourcePoint(
            Math.Min(width - 1, (int)(point.X * width / displayedSize.Width)),
            Math.Min(height - 1, (int)(point.Y * height / displayedSize.Height)));
    }
}

public sealed record GroundEdgeScore(
    int TruePositiveLength,
    int? FalsePositiveLength,
    int FalseNegativeLength)
{
    public double? Precision => FalsePositiveLength is not { } falsePositive
        ? null
        : TruePositiveLength + falsePositive == 0
            ? 1
            : (double)TruePositiveLength / (TruePositiveLength + falsePositive);

    public double? Recall => TruePositiveLength + FalseNegativeLength == 0
        ? null
        : (double)TruePositiveLength / (TruePositiveLength + FalseNegativeLength);

    public double? F1 => FalsePositiveLength is not { } falsePositive || Recall is null
        ? null
        : (double)(2 * TruePositiveLength)
            / (2 * TruePositiveLength + falsePositive + FalseNegativeLength);

    public static GroundEdgeScore Total(IReadOnlyList<GroundEdgeScore> values) => values.Count == 0
        ? new GroundEdgeScore(0, null, 0)
        : new GroundEdgeScore(
            values.Sum(value => value.TruePositiveLength),
            values.All(value => value.FalsePositiveLength is not null)
                ? values.Sum(value => value.FalsePositiveLength ?? 0)
                : null,
            values.Sum(value => value.FalseNegativeLength));
}

public static class GroundLabelScoring
{
    public static GroundEdgeScore Score(
        GroundLabelDocument document,
        IReadOnlyList<DetectedFloorLine> predictions,
        int tolerance = 3,
        bool evidenceOnly = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(predictions);
        var truthByColumn = Enumerable.Range(0, document.Width)
            .Select(_ => new List<int>()).ToArray();
        foreach (var edge in document.Edges)
        {
            var lower = Math.Max(0, edge.X0);
            var upper = Math.Min(document.Width - 1, edge.X1);
            if (lower > upper) continue;
            for (var x = lower; x <= upper; x++)
            {
                if (Visible(document, x, edge.Y)) truthByColumn[x].Add(edge.Y);
            }
        }

        var predictionsByColumn = Enumerable.Range(0, document.Width)
            .Select(_ => new List<int>()).ToArray();
        foreach (var line in predictions)
        {
            if (line.Row < 0 || line.Row >= document.Height) continue;
            foreach (var span in evidenceOnly ? line.EvidenceSpans : [line.XSpan])
            {
                var lower = Math.Max(0, span.Start);
                var upper = Math.Min(document.Width - 1, span.EndInclusive);
                if (lower > upper) continue;
                for (var x = lower; x <= upper; x++)
                {
                    if (Visible(document, x, line.Row)) predictionsByColumn[x].Add(line.Row);
                }
            }
        }

        var truePositive = 0;
        var falsePositive = 0;
        var falseNegative = 0;
        for (var x = 0; x < document.Width; x++)
        {
            var truth = truthByColumn[x].Distinct().Order().ToArray();
            var rows = predictionsByColumn[x].Distinct().Order().ToArray();
            var truthIndex = 0;
            var rowIndex = 0;
            while (truthIndex < truth.Length && rowIndex < rows.Length)
            {
                if (Math.Abs(truth[truthIndex] - rows[rowIndex]) <= Math.Max(0, tolerance))
                {
                    truePositive++;
                    truthIndex++;
                    rowIndex++;
                }
                else if (rows[rowIndex] < truth[truthIndex])
                {
                    falsePositive++;
                    rowIndex++;
                }
                else
                {
                    falseNegative++;
                    truthIndex++;
                }
            }
            falsePositive += rows.Length - rowIndex;
            falseNegative += truth.Length - truthIndex;
        }
        return new GroundEdgeScore(
            truePositive,
            document.FullyReviewed ? falsePositive : null,
            falseNegative);
    }

    private static bool Visible(GroundLabelDocument document, int x, int y) =>
        !document.IgnoredRegions.Any(region => region.ContainsPixelCenter(x, y));
}

internal static class GroundLabelPngDecoder
{
    private static readonly byte[] Signature =
        [137, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];

    internal static BgraFrame Decode(byte[] encoded, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length < Signature.Length
            || !encoded.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("PNG signature is invalid.");
        }

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var compressed = new MemoryStream();
        while (offset < encoded.Length)
        {
            if (encoded.Length - offset < 12)
                throw new InvalidDataException("PNG chunk is truncated.");
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset, 4));
            if (lengthValue > int.MaxValue)
                throw new InvalidDataException("PNG chunk is too large.");
            var length = (int)lengthValue;
            offset += 4;
            if (length > encoded.Length - offset - 8)
                throw new InvalidDataException("PNG chunk data is truncated.");
            var type = encoded.AsSpan(offset, 4);
            offset += 4;
            var data = encoded.AsSpan(offset, length);
            offset += length;
            var actualCrc = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset, 4));
            offset += 4;
            var crc = UpdateCrc(0xffffffffu, type);
            if ((UpdateCrc(crc, data) ^ 0xffffffffu) != actualCrc)
                throw new InvalidDataException("PNG chunk checksum is invalid.");

            var chunkType = Encoding.ASCII.GetString(type);
            if (chunkType == "IHDR")
            {
                if (sawHeader || data.Length != 13)
                    throw new InvalidDataException("PNG header is invalid.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                if (width is < 2 or > 4096
                    || height is < 2 or > 4096
                    || data[8] != 8
                    || data[9] != 6
                    || data[10] != 0
                    || data[11] != 0
                    || data[12] != 0)
                {
                    throw new InvalidDataException("Only bounded non-interlaced RGBA8 PNG is supported.");
                }
                sawHeader = true;
            }
            else if (chunkType == "IDAT")
            {
                if (!sawHeader || sawEnd)
                    throw new InvalidDataException("PNG data ordering is invalid.");
                compressed.Write(data);
            }
            else if (chunkType == "IEND")
            {
                if (data.Length != 0)
                    throw new InvalidDataException("PNG end chunk is invalid.");
                sawEnd = true;
                break;
            }
        }
        if (!sawHeader || !sawEnd || compressed.Length == 0)
            throw new InvalidDataException("PNG is incomplete.");

        var stride = checked(width * BgraFrame.BytesPerPixel);
        var expectedLength = checked((stride + 1) * height);
        var filtered = new byte[expectedLength];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            inflater.ReadExactly(filtered);
            if (inflater.ReadByte() != -1)
                throw new InvalidDataException("PNG pixel data length is invalid.");
        }

        var rgba = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * (stride + 1);
            var filter = filtered[sourceRow];
            if (filter > 4) throw new InvalidDataException("PNG filter is invalid.");
            var targetRow = y * stride;
            for (var x = 0; x < stride; x++)
            {
                var encodedByte = filtered[sourceRow + 1 + x];
                var left = x >= 4 ? rgba[targetRow + x - 4] : (byte)0;
                var above = y > 0 ? rgba[targetRow - stride + x] : (byte)0;
                var upperLeft = y > 0 && x >= 4 ? rgba[targetRow - stride + x - 4] : (byte)0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Paeth(left, above, upperLeft),
                    _ => throw new InvalidDataException("PNG filter is invalid.")
                };
                rgba[targetRow + x] = unchecked((byte)(encodedByte + predictor));
            }
        }

        var bgra = new byte[rgba.Length];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            bgra[index] = rgba[index + 2];
            bgra[index + 1] = rgba[index + 1];
            bgra[index + 2] = rgba[index];
            bgra[index + 3] = rgba[index + 3];
        }
        return new BgraFrame(width, height, stride, bgra, 0, observedAt);
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var estimate = left + above - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var aboveDistance = Math.Abs(estimate - above);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return crc;
    }
}
