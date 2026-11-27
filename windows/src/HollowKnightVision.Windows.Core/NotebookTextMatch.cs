using System.Text;
using System.Text.RegularExpressions;

namespace HollowKnightVision.Windows.Core;

internal static partial class NotebookTextMatch
{
    private static readonly HashSet<string> Protected = new(StringComparer.Ordinal)
    {
        "north", "south", "east", "west", "left", "right", "up", "down", "open", "closed", "enter", "exit",
        "hold", "press", "yes", "no", "not", "never", "buy", "sell", "on", "off", "without"
    };
    private static readonly Lazy<HashSet<string>> Dictionary = new(() =>
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "notebook-english.txt", "notebook-hallownest.txt" })
        {
            using var stream = typeof(NotebookTextMatch).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("Missing notebook dictionary " + name);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } word) words.Add(word);
        }
        return words;
    });

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+")]
    private static partial Regex Words();
    internal static bool English(string language) =>
        language.Split('-', '_')[0].Equals("en", StringComparison.OrdinalIgnoreCase);
    internal static bool Known(string word) => Dictionary.Value.Contains(word);
    internal static string Key(string text) => GameTextObservation.LogKey(text.Normalize());
    private static bool ProtectedToken(string token) => Protected.Contains(token) || token.Any(char.IsNumber);

    public static bool Same(string first, string second) => SameKeys(Key(first), Key(second));

    internal static bool SameKeys(string first, string second)
    {
        if (first == second) return first.Length != 0;
        var left = first.Split(' ');
        var right = second.Split(' ');
        if (left.Length != right.Length) return false;
        var differences = 0;
        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a == b) continue;
            if (++differences > Math.Max(1, (left.Length + 2) / 3) || ProtectedToken(a) || ProtectedToken(b)
                || Math.Min(a.Length, b.Length) < (left.Length == 1 ? 7 : 4) || !OneEdit(a, b)) return false;
        }
        return true;
    }

    private static bool OneEdit(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        while (!a.IsEmpty && !b.IsEmpty && a[0] == b[0]) { a = a[1..]; b = b[1..]; }
        if (a.Length == b.Length)
            return a.IsEmpty || a[1..].SequenceEqual(b[1..])
                || a.Length >= 2 && a[0] == b[1] && a[1] == b[0] && a[2..].SequenceEqual(b[2..]);
        return a.Length > b.Length ? a[1..].SequenceEqual(b) : b[1..].SequenceEqual(a);
    }

    internal enum Relation { Variant, Longer, Shorter }

    internal static Relation? CompareNearby(string[] old, string[] next, bool fragments, bool located)
    {
        if (!old.Where(ProtectedToken).SequenceEqual(next.Where(ProtectedToken)))
        {
            if (old.Length != next.Length || old.Length < 8 || old.Zip(next).Count(p => p.First != p.Second) < 2)
                return null;
            var restored = old.Zip(next, (a, b) =>
            {
                if (a.Length != b.Length || Protected.Contains(a) == Protected.Contains(b) ||
                    a.Any(char.IsNumber) || b.Any(char.IsNumber)) return b;
                var different = a.Zip(b).Where(p => p.First != p.Second).ToArray();
                return different is [(var x, var y)] && ("vw".Contains(x) && "vw".Contains(y)
                    || "il".Contains(x) && "il".Contains(y)) ? a : b;
            });
            if (!old.Where(ProtectedToken).SequenceEqual(restored.Where(ProtectedToken))) return null;
        }
        if (old.Length != next.Length)
        {
            if (!fragments) return null;
            var (shorter, longer) = old.Length < next.Length ? (old, next) : (next, old);
            if (shorter.Length < 2 || shorter.Sum(word => word.Length) < 5
                || !(Edge(shorter, longer, true, located) || Edge(shorter, longer, false, located))) return null;
            return old.Length < next.Length ? Relation.Longer : Relation.Shorter;
        }
        if (old.Length < 6) return null;
        int anchors = 0, errors = 0, letters = 0;
        foreach (var (a, b) in old.Zip(next))
        {
            var width = Math.Max(a.Length, b.Length);
            letters += width;
            if (a == b) { anchors++; continue; }
            var limit = width >= 4 ? 3 : 1;
            var cost = Distance(a, b, limit);
            if (cost > limit) return null;
            errors += cost;
        }
        var minimum = Math.Max(4, (old.Length + 1) / 2);
        if (located && old.Length >= 8) minimum = Math.Min(minimum, Math.Max(5, old.Length * 2 / 5));
        return anchors >= minimum && errors * 5 <= letters ? Relation.Variant : null;
    }

    private static bool Edge(string[] shortLine, string[] longLine, bool start, bool located)
    {
        var offset = start ? 0 : longLine.Length - shortLine.Length;
        var exact = 0;
        for (var i = 0; i < shortLine.Length; i++)
        {
            var a = shortLine[i];
            var b = longLine[offset + i];
            if (a == b) { exact++; continue; }
            if (i == (start ? shortLine.Length - 1 : 0))
            {
                var common = 0;
                while (common < Math.Min(a.Length, b.Length) &&
                    (start ? a[common] == b[common] : a[^(common + 1)] == b[^(common + 1)])) common++;
                if (common == a.Length || shortLine.Length >= 3 && common >= 2 && a.Length - common <= 1
                    || a.Length == 1 && shortLine.Length >= 4) continue;
            }
            var limit = located && shortLine.Length >= 5 && Math.Min(a.Length, b.Length) >= 5 ? 2 : 1;
            if (Math.Min(a.Length, b.Length) < 4 || Distance(a, b, limit) > limit) return false;
        }
        return exact >= Math.Max(1, shortLine.Length / 2);
    }

    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var next = new int[prev.Length];
        for (var i = 0; i < a.Length; i++)
        {
            next[0] = i + 1;
            for (var j = 0; j < b.Length; j++)
                next[j + 1] = Math.Min(Math.Min(prev[j + 1], next[j]) + 1, prev[j] + (a[i] == b[j] ? 0 : 1));
            if (next.Min() > limit) return limit + 1;
            (prev, next) = (next, prev);
        }
        return prev[^1];
    }

    internal static (string Text, double Confidence) Preferred(string old, double confidence,
        string next, double nextConfidence, string language)
    {
        if (old == next) return (old, Math.Max(confidence, nextConfidence));
        if (!English(language)) return nextConfidence > confidence ? (next, nextConfidence) : (old, confidence);
        var a = Words().Matches(old);
        var b = Words().Matches(next);
        static int Misses(MatchCollection words) => words.Count(m => m.Value.All(char.IsLetter) && !Known(m.Value));
        var oldMisses = Misses(a);
        var nextMisses = Misses(b);
        var useNext = nextMisses != oldMisses ? nextMisses < oldMisses : nextConfidence != confidence
            ? nextConfidence > confidence : Key(old) == Key(next) && (old.EndsWith('|') && !next.EndsWith('|')
                || old.EndsWith(',') && next.EndsWith('.') || old.Count(c => c == '\'') % 2 == 1 && next.Count(c => c == '\'') == 2);
        var text = useNext ? next : old;
        if (a.Count == b.Count && Math.Min(oldMisses, nextMisses) > 0)
        {
            var result = new StringBuilder(text);
            foreach (var (word, other) in (useNext ? b.Zip(a) : a.Zip(b)).Reverse())
            {
                if (word.Value == other.Value || Known(word.Value) || word.Length is < 4 or > 24 || other.Length is < 4 or > 24
                    || !word.Value.All(char.IsLower) || !other.Value.All(char.IsLower)) continue;
                var replacements = Known(other.Value) ? [other.Value] : Suggestions(word.Value)
                    .Where(s => OneEdit(s, other.Value)).Distinct().Take(2).ToArray();
                if (replacements is [var replacement]) result.Remove(word.Index, word.Length).Insert(word.Index, replacement);
            }
            text = result.ToString();
        }
        return (text, useNext ? nextConfidence : confidence);
    }

    private static IEnumerable<string> Suggestions(string word)
    {
        for (var i = 0; i <= word.Length; i++)
        {
            var left = word[..i];
            var right = word[i..];
            if (right.Length > 0 && Known(left + right[1..])) yield return left + right[1..];
            if (right.Length > 1 && Known(left + right[1] + right[0] + right[2..])) yield return left + right[1] + right[0] + right[2..];
            for (var c = 'a'; c <= 'z'; c++)
            {
                var inserted = left + c + right;
                if (Known(inserted)) yield return inserted;
                if (right.Length == 0) continue;
                var changed = left + c + right[1..];
                if (Known(changed)) yield return changed;
            }
        }
    }
}
