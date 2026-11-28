using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record GameTextLine(int Id, string Text, StencilRect Bounds, double Confidence);
public sealed record GameTextScan(BgraFrame Image, string Language, string Context,
    IReadOnlyList<GameTextLine> Lines, double Milliseconds);

public sealed record GameTextObservation(string Id, string Text, string Context, string Language,
    DateTimeOffset Date, bool? IsPassage = null, JsonElement? Interpretation = null,
    string? BestText = null, double? Confidence = null, int? MatchingVersion = null, string[]? OtherContexts = null)
{
    [JsonIgnore]
    public string DisplayText => BestText ?? Text;

    [JsonIgnore]
    public string Reading => InterpretationString("text") ?? DisplayText;

    [JsonIgnore]
    public string Kind => IsPassage == true ? "Recorded passage" : "Notebook text";

    [JsonIgnore]
    public string? Annotation => InterpretationString("text");

    [JsonIgnore]
    public string DisplayLabel => InterpretationString("detail") ?? (Label switch
    {
        "lore" => "Lore tablet", "dialogue" => "NPC dialogue", "hud" => "HUD", "unknown" => "Unclear",
        { Length: > 0 } label => char.ToUpperInvariant(label[0]) + label[1..], _ => "Awaiting review"
    });

    internal string? SameAs => InterpretationString("sameAs");
    internal string? Label => InterpretationString("label");

    private string? InterpretationString(string property) =>
        Interpretation is { ValueKind: JsonValueKind.Object } edit
        && edit.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string Key(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool IsTitleOcrFragment(string text, string language, string context)
    {
        var primary = language.Split(['-', '_'], 2)[0];
        if (!primary.Equals("en", StringComparison.OrdinalIgnoreCase)
            && !primary.Equals("English", StringComparison.OrdinalIgnoreCase)) return false;
        var screen = context.Split([" · ", " - "], 2, StringSplitOptions.None)[0].Trim().ToLowerInvariant();
        if (screen is not ("game capture" or "main title" or "main-title")) return false;
        var token = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
            if (!Rune.IsPunctuation(rune) && !Rune.IsWhiteSpace(rune)) token.Append(Rune.ToLowerInvariant(rune).ToString());
        return token.ToString() is "ce" or "ces" or "cei" or "ceis" or "coi";
    }

    internal bool IsTitleFragment => IsTitleOcrFragment(DisplayText, Language, Context)
        && (OtherContexts is null || OtherContexts.All(context => IsTitleOcrFragment(DisplayText, Language, context)));

    internal static string LogKey(string text)
    {
        var letters = new StringBuilder(text.Length);
        foreach (var scalar in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(scalar)) letters.Append(scalar.ToString());
            else letters.Append(' ');
        }
        return Key(letters.ToString());
    }

    internal static int Length(string text) => new StringInfo(text).LengthInTextElements;
    internal static string Prefix(string text, int length)
    {
        if (text.Length <= length) return text;
        var value = new StringInfo(text);
        return value.LengthInTextElements <= length ? text : value.SubstringByTextElements(0, length);
    }

}

public sealed record GameTextLogEntry(GameTextObservation Observation, IReadOnlyList<string> Contexts, DateTimeOffset Updated)
{
    public static GameTextLogEntry[] Current(IReadOnlyList<GameTextObservation> rows) => rows
        .OrderByDescending(row => row.Date).ThenBy(row => row.Id, StringComparer.Ordinal)
        .Select(row => new GameTextLogEntry(row, [row.Context, .. row.OtherContexts ?? []], row.Date)).ToArray();

    public string ContextText => string.Join(" · ", Contexts);

    public static GameTextLogEntry[] Group(IReadOnlyList<GameTextObservation> rows)
    {
        var entries = new Dictionary<string, GameTextLogEntry>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new Dictionary<(string Language, string Text), string>();
        foreach (var row in rows)
        {
            var key = (row.Language, GameTextObservation.LogKey(row.Reading));
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

public sealed record NotebookMatch(string Input, string EntryID, string Result, string[]? AbsorbedEntryIDs = null);

public sealed class GameTextNotebook(string? path = null)
{
    public const string OpeningContext = "Opening cutscene";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SwiftDateConverter() }
    };

    private IReadOnlyList<GameTextObservation>? rows;
    internal static byte[] EncodeTrace<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private readonly Dictionary<(string Language, string Key), int> exact = [];
    private readonly Dictionary<(string Language, int Count), List<int>> buckets = [];
    private readonly Dictionary<string, int> positions = new(StringComparer.Ordinal);
    private sealed record Sighting(string Context, DateTimeOffset At, StencilRect? Bounds);
    private readonly Dictionary<string, Sighting> recent = new(StringComparer.Ordinal);
    internal List<NotebookMatch>? Trace { get; set; }

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
        saved = saved.Where(row => !row.IsTitleFragment).ToArray();
        if (saved.All(row => row.MatchingVersion == 2))
        {
            Rebuild(saved);
            return rows = saved;
        }
        rows = Array.Empty<GameTextObservation>();
        try { return Import(saved); }
        finally { recent.Clear(); }
    }

    public IReadOnlyList<GameTextObservation> Remember(GameTextScan scan, IReadOnlyList<GameTextLine> previous)
    {
        var transient = scan.Context is OpeningContext or "Game capture" or "Credits";
        var observations = new List<(GameTextObservation, StencilRect?)>();
        foreach (var line in scan.Lines)
        {
            var key = GameTextObservation.Key(line.Text);
            var repeat = previous.Any(old => NotebookTextMatch.Same(old.Text, line.Text));
            if (key.Length < 2 || !key.Any(char.IsLetter) || !double.IsFinite(line.Confidence)
                || (repeat ? line.Confidence < .45 : !transient || line.Confidence < .8)) continue;
            var id = string.Join('\u001f', scan.Language, scan.Context, key);
            observations.Add((new(id, line.Text, scan.Context, scan.Language, scan.Image.ObservedAt, Confidence: line.Confidence), line.Bounds));
        }
        return Store(observations);
    }

    public IReadOnlyList<GameTextObservation> Import(IEnumerable<GameTextObservation> observations) =>
        Store(observations.Select(row => (row, (StencilRect?)null)));

    private IReadOnlyList<GameTextObservation> Store(IEnumerable<(GameTextObservation Row, StencilRect? Bounds)> observations)
    {
        var existing = Read();
        List<GameTextObservation>? updated = null;
        try
        {
            var changed = false;
            foreach (var (row, bounds) in observations)
            {
                if (row.IsTitleFragment) continue;
                updated ??= existing.ToList();
                changed |= Merge(updated, row, bounds);
            }
            if (!changed) return existing;
            if (path is not null) AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(updated, Json));
            return rows = updated!;
        }
        catch
        {
            rows = null;
            Rebuild([]);
            recent.Clear();
            throw;
        }
    }

    private void Index(GameTextObservation row, int i)
    {
        var key = NotebookTextMatch.Key(row.DisplayText);
        exact[(row.Language, key)] = i;
        var bucket = (row.Language, key.Split(' ').Length);
        if (!buckets.TryGetValue(bucket, out var entries)) buckets[bucket] = entries = [];
        entries.Add(i);
        positions[row.Id] = i;
    }

    private void Rebuild(IReadOnlyList<GameTextObservation> entries)
    {
        positions.Clear();
        exact.Clear();
        buckets.Clear();
        for (var i = 0; i < entries.Count; i++) Index(entries[i], i);
    }

    private bool Merge(List<GameTextObservation> entries, GameTextObservation input, StencilRect? bounds)
    {
        var key = NotebookTextMatch.Key(input.DisplayText);
        var tokens = key.Split(' ');
        var strict = exact.GetValueOrDefault((input.Language, key), -1);
        if (strict < 0 && buckets.TryGetValue((input.Language, tokens.Length), out var candidates))
            strict = candidates.FirstOrDefault(i => recent.GetValueOrDefault(entries[i].Id)?.At != input.Date &&
                NotebookTextMatch.SameKeys(NotebookTextMatch.Key(entries[i].DisplayText), key), -1);
        var fragments = new List<int>();
        var variants = new List<int>();
        var complete = new List<int>();
        foreach (var (id, seen) in recent.ToArray())
        {
            var elapsed = (input.Date - seen.At).TotalSeconds;
            if (elapsed > 30) { recent.Remove(id); continue; }
            if (elapsed <= 0) continue;
            var index = positions[id];
            var old = entries[index];
            if (old.Language != input.Language) continue;
            var located = false;
            if (seen.Bounds is { Width: > 0, Height: > 0 } a && bounds is { Width: > 0, Height: > 0 } b)
            {
                located = true;
                if (Math.Min(a.X + a.Width, b.X + b.Width) <= Math.Max(a.X, b.X)
                    || Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) < .5 * Math.Min(a.Height, b.Height)) continue;
            }
            var prior = NotebookTextMatch.Key(old.DisplayText).Split(' ');
            if (located && elapsed <= 2 && prior.Length != tokens.Length && NotebookTextMatch.English(input.Language))
            {
                var joined = string.Concat(tokens);
                if (joined.Length >= 6 && joined == string.Concat(prior) && joined.All(char.IsLetter) && !NotebookTextMatch.Known(joined))
                {
                    variants.Add(index);
                    continue;
                }
            }
            if (seen.Context != input.Context || strict >= 0 && prior.Length == tokens.Length) continue;
            switch (NotebookTextMatch.CompareNearby(prior, tokens, elapsed <= 3, located && elapsed <= 3))
            {
                case NotebookTextMatch.Relation.Longer: fragments.Add(index); break;
                case NotebookTextMatch.Relation.Shorter: complete.Add(index); break;
                case NotebookTextMatch.Relation.Variant: variants.Add(index); break;
            }
        }
        var whole = strict >= 0 ? strict : variants.DefaultIfEmpty(-1).Min();
        var match = whole >= 0 ? whole : fragments.DefaultIfEmpty(complete.Count == 1 ? complete[0] : -1).Min();
        var sight = new Sighting(input.Context, input.Date, bounds);
        if (match < 0)
        {
            var added = input with { MatchingVersion = 2, Id = positions.ContainsKey(input.Id) ? Guid.NewGuid().ToString() : input.Id };
            Index(added, entries.Count);
            entries.Add(added);
            recent[added.Id] = sight;
            Trace?.Add(new(input.Text, added.Id, "inserted"));
            return true;
        }

        var original = entries[match];
        var row = original;
        if (whole >= 0)
        {
            var (preferred, confidence) = NotebookTextMatch.Preferred(row.DisplayText, row.Confidence ?? 0,
                input.DisplayText, input.Confidence ?? 0, input.Language);
            if (row.DisplayText != preferred || confidence > (row.Confidence ?? 0))
                row = row with { BestText = preferred, Confidence = confidence };
        }
        else if (fragments.Contains(match)) row = row with { BestText = input.DisplayText, Confidence = input.Confidence };

        var retired = fragments.Where(i => i != match).OrderDescending().ToArray();
        foreach (var source in retired.Select(i => entries[i]).Prepend(input))
        {
            foreach (var context in (source.OtherContexts ?? []).Prepend(source.Context))
                if (context != row.Context && !(row.OtherContexts?.Contains(context) ?? false))
                    row = row with { OtherContexts = [.. row.OtherContexts ?? [], context] };
            if (source.IsPassage == true) row = row with { IsPassage = true };
            if (row.Interpretation is null && source.Interpretation is not null) row = row with { Interpretation = source.Interpretation };
        }
        entries[match] = row;
        var changed = row != original || retired.Length > 0;
        Trace?.Add(new(input.Text, row.Id, changed ? "duplicate-updated" : "duplicate",
            retired.Length == 0 ? null : retired.Select(i => entries[i].Id).ToArray()));
        foreach (var index in retired)
        {
            recent.Remove(entries[index].Id);
            entries.RemoveAt(index);
        }
        if (retired.Length > 0 || NotebookTextMatch.Key(original.DisplayText) != NotebookTextMatch.Key(row.DisplayText)) Rebuild(entries);
        recent[row.Id] = sight;
        return changed;
    }

    internal IReadOnlyList<GameTextObservation> Interpret(IReadOnlyDictionary<string, GameTextCorrection> edits)
    {
        var current = Read();
        if (edits.Count == 0) return current;
        var updated = current.ToArray();
        var changed = false;
        for (var i = 0; i < updated.Length; i++)
        {
            var row = updated[i];
            if (row.Interpretation is not null || !edits.TryGetValue(row.Id, out var edit)) continue;
            updated[i] = row with { Interpretation = JsonSerializer.SerializeToElement(edit, Json) };
            changed = true;
        }
        if (!changed) return current;
        if (path is not null)
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(updated, Json));
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
