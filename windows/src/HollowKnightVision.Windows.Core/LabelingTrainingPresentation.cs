using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record LabelingTrainingConfiguration(int MaximumIterations, int GridSize)
{
    public static LabelingTrainingConfiguration ForModel(string identifier) => new(identifier == LabelingDatasetExporter.SharedObjectModelIdentifier ? 100 : 10, 13);
}

public static class LabelingTrainingNotificationPolicy
{
    public static int UntrainedImageCount(IEnumerable<DateTimeOffset> modificationDates, DateTimeOffset? datasetCreatedAt) =>
        modificationDates.Count(date => datasetCreatedAt is null || date > datasetCreatedAt);
    public static bool HasUntrainedChanges(IEnumerable<DateTimeOffset> modificationDates, DateTimeOffset? datasetCreatedAt) =>
        UntrainedImageCount(modificationDates, datasetCreatedAt) > 0;

    public static int CountExamples(IEnumerable<SavedLabelingExample> examples, LabelingModelCandidate? latestCandidate)
    {
        var dates = new List<DateTimeOffset>();
        foreach (var example in examples.Where(item => item.Manifest.ContextIdentifier == "game"
            && (item.Manifest.Annotations.Count > 0 || item.Manifest.EffectiveKnownClassIdentifiers.Count > 0)))
        {
            try
            {
                var file = new FileInfo(Path.Combine(example.DirectoryPath, LabelingExampleStore.ManifestFilename)); file.Refresh();
                if (file.Exists) dates.Add(file.LastWriteTimeUtc);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return UntrainedImageCount(dates, latestCandidate is null ? null : DatasetCreatedAt(latestCandidate.DatasetDirectory));
    }

    private static DateTimeOffset? DatasetCreatedAt(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, LabelingDatasetExporter.ManifestFilename)));
            var root = document.RootElement;
            // A truncated/invalid manifest is not proof that any image was trained.
            _ = root.GetProperty("schemaVersion").GetInt32(); _ = root.GetProperty("id").GetGuid();
            if (root.GetProperty("classIdentifier").GetString() is null) return null;
            ValidateOptionalClasses(root, "classIdentifiers");
            _ = root.GetProperty("isPreliminary").GetBoolean();
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                _ = item.GetProperty("exampleIdentifier").GetGuid(); _ = item.GetProperty("imageIdentifier").GetGuid();
                _ = item.GetProperty("captureGroupIdentifier").GetGuid(); _ = item.GetProperty("annotationCount").GetInt32();
                ValidateOptionalClasses(item, "knownClassIdentifiers");
                if (item.GetProperty("imageDigest").GetString() is null || item.GetProperty("imageFilename").GetString() is null
                    || item.GetProperty("split").GetString()?.ToLowerInvariant() is not ("training" or "validation")) return null;
            }
            return root.GetProperty("createdAt").GetDateTimeOffset();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }

    private static void ValidateOptionalClasses(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        foreach (var item in value.EnumerateArray())
            if (item.GetString() is null) throw new JsonException("Invalid dataset class identifier.");
    }
}

public static class TrainingProgressUpdate
{
    public static string? Parse(string line)
    {
        var tokens = line.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0] is not ("HKV_PROGRESS" or "HKV_STAGE")) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens.Skip(1))
        {
            var separator = token.IndexOf('='); if (separator <= 0 || separator == token.Length - 1) continue;
            if (!fields.TryAdd(token[..separator], token[(separator + 1)..])) return null;
        }
        if (tokens[0] == "HKV_STAGE") return fields.GetValueOrDefault("name") switch
        { "baseline" => "Measuring current model…", "training" => "Starting training…", "evaluating" => "Evaluating new model…", "exporting" => "Exporting new model…", _ => null };
        if (!int.TryParse(fields.GetValueOrDefault("epoch"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            || !int.TryParse(fields.GetValueOrDefault("total"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || !double.TryParse(fields.GetValueOrDefault("eta_seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var eta)
            || total <= 0 || epoch < 0 || epoch > total || !double.IsFinite(eta)) return null;
        if (epoch == total) return $"{epoch}/{total} · evaluating next…";
        var minutes = Math.Max(1, Math.Ceiling(eta / 60));
        return $"{epoch}/{total} · about {minutes.ToString("0", CultureInfo.InvariantCulture)} min remaining";
    }
}

public sealed record TrainingOutputSnapshot(long Sequence, string Text, string? ProgressText, string? LastMessage);

/// <summary>Bounded combined log with independent, incremental UTF-8 line decoders for stdout/stderr.</summary>
public sealed class TrainingOutputBuffer
{
    public const int MaximumBytes = 128 * 1024;
    private sealed class Channel { public readonly Decoder Decoder = Encoding.UTF8.GetDecoder(); public readonly StringBuilder Line = new(); public bool Oversized; }
    private readonly object gate = new(); private readonly byte[] tail = new byte[MaximumBytes];
    private readonly Channel[] channels = [new(), new()]; private int start, count; private long sequence;
    private string? progress, lastMessage;
    public TrainingOutputSnapshot Append(ReadOnlySpan<byte> bytes, bool standardError = false)
    {
        lock (gate)
        {
            var channel = channels[standardError ? 1 : 0]; var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var length = channel.Decoder.GetChars(bytes, chars, false);
            AppendTail(chars.AsSpan(0, length)); Consume(channel, chars.AsSpan(0, length));
            sequence++; return Snapshot();
        }
    }
    public TrainingOutputSnapshot Complete(bool standardError = false)
    {
        lock (gate)
        {
            var channel = channels[standardError ? 1 : 0]; Span<char> remainder = stackalloc char[2];
            var length = channel.Decoder.GetChars([], remainder, true);
            AppendTail(remainder[..length]); Consume(channel, remainder[..length]);
            if (channel.Line.Length > 0 || channel.Oversized) FinishLine(channel);
            sequence++; return Snapshot();
        }
    }
    public TrainingOutputSnapshot Current { get { lock (gate) return Snapshot(); } }
    private void AppendTail(ReadOnlySpan<char> chars)
    {
        // Decode each channel before combining: stderr may arrive between bytes of a stdout code point.
        var bytes = new byte[Encoding.UTF8.GetByteCount(chars)]; Encoding.UTF8.GetBytes(chars, bytes);
        foreach (var value in bytes)
        {
            if (count == tail.Length) { tail[start] = value; start = (start + 1) % tail.Length; }
            else { tail[(start + count) % tail.Length] = value; count++; }
        }
    }
    private void Consume(Channel channel, ReadOnlySpan<char> chars)
    {
        foreach (var value in chars)
        {
            if (value == '\n') { FinishLine(channel); continue; }
            if (channel.Oversized) continue;
            if (channel.Line.Length >= MaximumBytes) { channel.Oversized = true; channel.Line.Clear(); }
            else channel.Line.Append(value);
        }
    }
    private void FinishLine(Channel channel)
    {
        var line = channel.Oversized ? "Trainer output line exceeded the log limit." : channel.Line.ToString().TrimEnd('\r');
        channel.Line.Clear(); channel.Oversized = false;
        if (line.Length == 0) return;
        lastMessage = line; progress = TrainingProgressUpdate.Parse(line) ?? progress;
    }
    private TrainingOutputSnapshot Snapshot()
    {
        var bytes = new byte[count]; var first = Math.Min(count, tail.Length - start);
        Array.Copy(tail, start, bytes, 0, first); Array.Copy(tail, 0, bytes, first, count - first);
        var offset = 0;
        while (offset < bytes.Length && (bytes[offset] & 0xc0) == 0x80) offset++;
        return new(sequence, Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset), progress, lastMessage);
    }
}
