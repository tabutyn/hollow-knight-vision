using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record GameTextLine(int Id, string Text, StencilRect Bounds, double Confidence);
public sealed record GameTextScan(BgraFrame Image, string Language, string Context,
    IReadOnlyList<GameTextLine> Lines, double Milliseconds);

public sealed record GameTextObservation(string Id, string Text, string Context, string Language,
    DateTimeOffset Date, bool? IsPassage = null, JsonElement? Interpretation = null)
{
    [JsonIgnore]
    public string Reading => Interpretation is { ValueKind: JsonValueKind.Object } edit
        && edit.TryGetProperty("text", out var reading) && reading.ValueKind == JsonValueKind.String
        ? reading.GetString() ?? Text : Text;

    internal string? SameAs => Interpretation is { ValueKind: JsonValueKind.Object } edit
        && edit.TryGetProperty("sameAs", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    internal static string Key(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed record GameTextLogEntry(GameTextObservation Observation, IReadOnlyList<string> Contexts, DateTimeOffset Updated)
{
    public string ContextText => string.Join(" · ", Contexts);

    public static GameTextLogEntry[] Group(IReadOnlyList<GameTextObservation> rows)
    {
        var entries = new Dictionary<string, GameTextLogEntry>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new Dictionary<(string Language, string Text), string>();
        foreach (var row in rows)
        {
            var punctuationAsSpaces = new string(row.Reading.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
            var key = (row.Language, GameTextObservation.Key(punctuationAsSpaces));
            var id = (row.SameAs is { } other ? aliases.GetValueOrDefault(other) : null)
                ?? keys.GetValueOrDefault(key) ?? row.Id;
            aliases[row.Id] = id;
            keys[key] = id;
            if (!entries.TryGetValue(id, out var entry))
            {
                entries[id] = new(row, [row.Context], row.Date);
                continue;
            }

            var preferred = row.Interpretation is not null
                && (entry.Observation.Interpretation is null || entry.Observation.Reading.Length <= row.Reading.Length || row.SameAs is not null)
                ? row : entry.Observation;
            var contexts = entry.Contexts.Contains(row.Context) ? entry.Contexts : [.. entry.Contexts, row.Context];
            entries[id] = new(preferred, contexts, row.Date > entry.Updated ? row.Date : entry.Updated);
        }
        return entries.Values.OrderByDescending(entry => entry.Updated)
            .ThenBy(entry => entry.Observation.Id, StringComparer.Ordinal).ToArray();
    }
}

public sealed class GameTextNotebook(string? path = null)
{
    public const string OpeningContext = "Opening cutscene";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SwiftDateConverter() }
    };

    private IReadOnlyList<GameTextObservation>? rows;
    private HashSet<string>? ids;

    public IReadOnlyList<GameTextObservation> Read()
    {
        if (rows is not null) return rows;
        var saved = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<GameTextObservation[]>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("The game text notebook is empty JSON.")
            : [];
        if (saved.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id) || row.Text is null
            || row.Context is null || row.Language is null))
            throw new InvalidDataException("The game text notebook contains an incomplete observation.");
        ids = saved.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        return rows = saved;
    }

    public IReadOnlyList<GameTextObservation> Remember(GameTextScan scan, IReadOnlyList<GameTextLine> previous)
    {
        var confirmed = previous.Select(line => GameTextObservation.Key(line.Text)).ToHashSet(StringComparer.Ordinal);
        var transient = scan.Context is OpeningContext or "Game capture" or "Credits";
        var observations = new List<GameTextObservation>();
        foreach (var line in scan.Lines)
        {
            var key = GameTextObservation.Key(line.Text);
            var repeat = confirmed.Contains(key);
            if (key.Length < 2 || !key.Any(char.IsLetter) || !double.IsFinite(line.Confidence)
                || (repeat ? line.Confidence < .45 : !transient || line.Confidence < .8)) continue;
            var id = string.Join('\u001f', scan.Language, scan.Context, key);
            observations.Add(new(id, line.Text, scan.Context, scan.Language, scan.Image.ObservedAt));
        }
        return Import(observations);
    }

    public IReadOnlyList<GameTextObservation> Import(IEnumerable<GameTextObservation> observations)
    {
        var existing = Read();
        var newIds = new HashSet<string>(StringComparer.Ordinal);
        var additions = observations.Where(row => !ids!.Contains(row.Id) && newIds.Add(row.Id)).ToArray();
        if (additions.Length == 0) return existing;
        GameTextObservation[] updated = [.. existing, .. additions];
        if (path is not null)
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(updated, Json));
        ids!.UnionWith(newIds);
        return rows = updated;
    }

    // macOS Foundation's default JSON Date uses seconds since 2001-01-01.
    private sealed class SwiftDateConverter : JsonConverter<DateTimeOffset>
    {
        private static readonly DateTimeOffset Epoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var timestamp)) return timestamp;
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var seconds) || !double.IsFinite(seconds))
                throw new JsonException("Invalid game text timestamp.");
            try { return Epoch.AddSeconds(seconds); }
            catch (ArgumentOutOfRangeException error) { throw new JsonException("Invalid game text timestamp.", error); }
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((value - Epoch).TotalSeconds);
    }
}
