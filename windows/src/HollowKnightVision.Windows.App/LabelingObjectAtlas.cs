using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public sealed record LabelingObjectAtlasBuildInput(IReadOnlyList<SavedLabelingExample> Examples,
    IReadOnlyList<string> ClassIdentifiers, IReadOnlyDictionary<string, Guid> PreferredExampleIdentifiers);

public sealed class LabelingObjectAtlasSnapshot
{
    public const int ImageSize = 2048, SlotSize = 128, Columns = ImageSize / SlotSize, Capacity = Columns * Columns;
    public BitmapSource Image { get; }
    public IReadOnlyList<string> ClassIdentifiers { get; }
    public IReadOnlyList<Int32Rect> IconRects { get; }
    private readonly Dictionary<string, BitmapSource> icons = new(StringComparer.Ordinal);

    internal LabelingObjectAtlasSnapshot(BitmapSource image, IReadOnlyList<string> identifiers, IReadOnlyList<Int32Rect> rects)
    {
        Image = image; ClassIdentifiers = identifiers.ToArray(); IconRects = rects.ToArray();
        for (var index = 0; index < identifiers.Count; index++)
        {
            var rect = rects[index]; if (rect.IsEmpty || rect.Width == 0 || rect.Height == 0) continue;
            rect.X += index % Columns * SlotSize; rect.Y += index / Columns * SlotSize;
            var icon = new CroppedBitmap(image, rect); icon.Freeze(); icons[identifiers[index]] = icon;
        }
    }
    public BitmapSource? Icon(string identifier) => icons.GetValueOrDefault(LabelingClassIdentity.CanonicalIdentifier(identifier));
}

public static class ObjectReferenceImages
{
    public static BitmapSource? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze(); return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }

    public static BitmapSource? Crop(BitmapSource image, LabelingExampleAnnotation annotation)
    {
        if (!double.IsFinite(annotation.X) || !double.IsFinite(annotation.Y) || !double.IsFinite(annotation.Width) || !double.IsFinite(annotation.Height)) return null;
        var left = Math.Clamp(Math.Floor(Math.Min(annotation.X, annotation.X + annotation.Width) * image.PixelWidth), 0, image.PixelWidth);
        var top = Math.Clamp(Math.Floor(Math.Min(annotation.Y, annotation.Y + annotation.Height) * image.PixelHeight), 0, image.PixelHeight);
        var right = Math.Clamp(Math.Ceiling(Math.Max(annotation.X, annotation.X + annotation.Width) * image.PixelWidth), 0, image.PixelWidth);
        var bottom = Math.Clamp(Math.Ceiling(Math.Max(annotation.Y, annotation.Y + annotation.Height) * image.PixelHeight), 0, image.PixelHeight);
        if (right <= left || bottom <= top || annotation.Width == 0 || annotation.Height == 0) return null;
        var crop = new CroppedBitmap(image, new((int)left, (int)top, (int)(right - left), (int)(bottom - top)));
        crop.Freeze(); return crop;
    }
}

/// <summary>Mac's stable 2048px icon sheet with top-origin, aspect-fit 128px slots.</summary>
public sealed class LabelingObjectAtlasStore(string rootDirectory)
{
    public const string ImageFilename = "object-atlas.png", MetadataFilename = "object-atlas.json";
    private readonly string root = Path.GetFullPath(rootDirectory);
    private readonly object gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Metadata(int SchemaVersion, string Fingerprint, string[] ClassIdentifiers, int[][][] IconRects, string ImageSha256);

    public LabelingObjectAtlasSnapshot LoadOrBuild(LabelingObjectAtlasBuildInput input, bool force = false)
    {
        lock (gate)
        {
            var identifiers = input.ClassIdentifiers.Select(LabelingClassIdentity.CanonicalIdentifier).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (identifiers.Length > LabelingObjectAtlasSnapshot.Capacity) throw new InvalidDataException("Object atlas supports at most 256 classes.");
            var references = identifiers.Select(identifier => LabelingObjectReferencePolicy.Preferred(input.Examples, identifier,
                input.PreferredExampleIdentifiers.TryGetValue(identifier, out var id) ? id : null)).ToArray();
            // Include edited bounds and selected image bytes. Identity-only fingerprints
            // can silently reuse an obsolete crop after modifying a label in place.
            var fingerprint = Fingerprint(identifiers, references);
            if (!force && Load(fingerprint, identifiers) is { } cached) return cached;
            var rects = new Int32Rect[identifiers.Length]; var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            var images = new Dictionary<string, BitmapSource?>(StringComparer.OrdinalIgnoreCase);
            using (var drawing = visual.RenderOpen())
                for (var index = 0; index < identifiers.Length; index++)
                {
                    var reference = references[index]; if (reference is null) continue;
                    if (!images.TryGetValue(reference.Example.ImagePath, out var source))
                        images[reference.Example.ImagePath] = source = ObjectReferenceImages.Load(reference.Example.ImagePath);
                    if (source is null || ObjectReferenceImages.Crop(source, reference.Annotation) is not { } crop) continue;
                    var slotX = index % LabelingObjectAtlasSnapshot.Columns * LabelingObjectAtlasSnapshot.SlotSize;
                    var slotY = index / LabelingObjectAtlasSnapshot.Columns * LabelingObjectAtlasSnapshot.SlotSize;
                    var scale = Math.Min(128.0 / crop.PixelWidth, 128.0 / crop.PixelHeight);
                    var width = crop.PixelWidth * scale; var height = crop.PixelHeight * scale;
                    var x = (128 - width) / 2; var y = (128 - height) / 2;
                    drawing.DrawImage(crop, new(slotX + x, slotY + y, width, height));
                    rects[index] = new((int)Math.Floor(x), (int)Math.Floor(y),
                        Math.Min(128, (int)Math.Ceiling(x + width)) - (int)Math.Floor(x),
                        Math.Min(128, (int)Math.Ceiling(y + height)) - (int)Math.Floor(y));
                }
            var image = new RenderTargetBitmap(2048, 2048, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
            var snapshot = new LabelingObjectAtlasSnapshot(image, identifiers, rects);
            Save(snapshot, fingerprint); return snapshot;
        }
    }

    private static string FileHash(string path)
    {
        try { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }
    private static string Fingerprint(string[] identifiers, LabelingObjectReference?[] references)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var records = references.Select(reference =>
        {
            if (reference is null) return null;
            var path = reference.Example.ImagePath;
            if (!hashes.TryGetValue(path, out var hash)) hashes[path] = hash = FileHash(path);
            return new { reference.Example.Id, reference.Example.Manifest.ImageIdentifier, reference.Annotation, ImageSha256 = hash };
        }).ToArray();
        return "windows-v3:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { identifiers, records }, JsonOptions)));
    }
    private LabelingObjectAtlasSnapshot? Load(string fingerprint, string[] identifiers)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllBytes(Path.Combine(root, MetadataFilename)), JsonOptions);
            if (metadata is null || metadata.SchemaVersion != 2 || metadata.Fingerprint != fingerprint
                || metadata.ClassIdentifiers is null || !metadata.ClassIdentifiers.SequenceEqual(identifiers)
                || metadata.IconRects is null || metadata.IconRects.Length != identifiers.Length
                || FileHash(Path.Combine(root, ImageFilename)) != metadata.ImageSha256) return null;
            var rects = new List<Int32Rect>();
            foreach (var item in metadata.IconRects)
            {
                if (item is not { Length: 2 } || item[0] is not { Length: 2 } || item[1] is not { Length: 2 }) return null;
                var x = item[0][0]; var y = item[0][1]; var w = item[1][0]; var h = item[1][1];
                if (x < 0 || y < 0 || w < 0 || h < 0 || x > 128 - w || y > 128 - h) return null;
                rects.Add(new(x, y, w, h));
            }
            var image = ObjectReferenceImages.Load(Path.Combine(root, ImageFilename));
            return image is { PixelWidth: 2048, PixelHeight: 2048 } ? new(image, identifiers, rects) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    private void Save(LabelingObjectAtlasSnapshot snapshot, string fingerprint)
    {
        Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".object-atlas-" + Guid.NewGuid().ToString("N"));
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot.Image));
            using (var stream = File.Create(stage)) encoder.Save(stream);
            var metadata = new Metadata(2, fingerprint, snapshot.ClassIdentifiers.ToArray(), snapshot.IconRects
                .Select(rect => new[] { new[] { rect.X, rect.Y }, new[] { rect.Width, rect.Height } }).ToArray(), FileHash(stage));
            File.Move(stage, Path.Combine(root, ImageFilename), overwrite: true);
            File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions));
            // A crash between the two commits cannot validate an old metadata/new image pair.
            File.Move(stage, Path.Combine(root, MetadataFilename), overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
