using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>Owns the current reading session. Call from one dispatcher.</summary>
public sealed class GameTextReader(string notebookDirectory)
{
    public sealed record Work(BgraFrame Image, string Language, string Context, long Version, double CapturedAt);
    public sealed record Reading(string Context, double CapturedAt, IReadOnlyList<string> Lines,
        IReadOnlyList<ThinkingCue> Cues, double? SatisfiedInspection);
    private sealed record Inspection(string Context, double StartedAt, IReadOnlySet<string> Prior, string? Candidate = null);
    private Inspection? inspection;
    private readonly HashSet<string> passageLines = new(StringComparer.Ordinal);
    private GameTextNotebook notebook = new();
    private GameTextNotebook? opening;
    private string? goalPath;
    private AtlasQuestLog? questLog;
    private Work? inFlight;
    private (string Language, string Context)? scope;
    private long? latestCapture;
    private long version;
    private double nextAt = double.NegativeInfinity;
    private IReadOnlyList<GameTextObservation> rows = [];

    public bool Enabled { get; private set; } = true;
    public SaveProfileAtlas? Profile { get; private set; }
    public GameTextScan? Scan { get; private set; }
    public Reading? LatestReading { get; private set; }

    public void BeginInspection(string context, double startedAt, IReadOnlyList<string> visible) =>
        inspection = new(context, startedAt, visible.Select(GameTextObservation.Key).ToHashSet(StringComparer.Ordinal));
    public GameTextLogEntry[] Entries { get; private set; } = [];
    public string? Issue { get; private set; }
    public string Goal { get; private set; } = "";
    public string DefaultGoal => AtlasQuestReview.DefaultObjective;
    public string QuestEvidence => QuestLog.SelectionEvidence(QuestContext.NotesFor(QuestLog),
        string.IsNullOrEmpty(Goal) ? AtlasQuestReview.DefaultObjective : Goal);
    internal long NotebookGeneration { get; private set; }
    internal IReadOnlyList<GameTextObservation> Observations => rows;
    internal AtlasQuestContext QuestContext { get; private set; } = new(null);
    internal AtlasQuestLog QuestLog => questLog ??= new(rows);

    public void SetGoal(string value)
    {
        var next = GameTextObservation.Prefix(value.Trim(), 400);
        if (Goal == next) return;
        if (goalPath is not null) AtomicFile.Write(goalPath, JsonSerializer.SerializeToUtf8Bytes(next));
        Goal = next;
    }

    internal void ApplyCorrections(long generation, IReadOnlyDictionary<string, GameTextCorrection> corrections)
    {
        if (generation != NotebookGeneration) throw new OperationCanceledException();
        Refresh(notebook.Interpret(corrections));
    }


    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        ResetFrame();
    }

    public void SelectProfile(SaveProfileAtlas? next)
    {
        if (next == Profile) return;
        opening = Profile is null && next is not null ? notebook : null;
        Profile = next;
        var filename = next?.Id.ToString("D").ToUpperInvariant() + ".json";
        notebook = next is null ? new() : new(Path.Combine(notebookDirectory, filename));
        QuestContext.Invalidate();
        QuestContext = new(next is null ? null : Path.Combine(notebookDirectory, "QuestMemory", filename));
        NotebookGeneration++;
        questLog = null;
        goalPath = next is null ? null : Path.Combine(notebookDirectory, "Goals", filename);
        Goal = "";
        ResetFrame();
        rows = [];
        Entries = [];
        try
        {
            Refresh(ReadNotebook());
            if (goalPath is not null && File.Exists(goalPath))
                Goal = JsonSerializer.Deserialize<string>(File.ReadAllBytes(goalPath)) ?? "";
        }
        catch (Exception error) when (IsNotebookError(error)) { Issue = error.Message; }
    }

    public void ResetFrame()
    {
        version++;
        scope = null;
        Scan = null;
        LatestReading = null;
        inspection = null;
        passageLines.Clear();
        Issue = null;
        latestCapture = null;
        nextAt = double.NegativeInfinity;
    }

    public Work? TryStart(BgraFrame image, string language, string context, double capturedAt)
    {
        if (!Enabled || !double.IsFinite(capturedAt)) return null;
        if (scope != (language, context))
        {
            ResetFrame();
            scope = (language, context);
        }
        if (inFlight is not null || latestCapture == image.CaptureId || capturedAt < nextAt) return null;
        latestCapture = image.CaptureId;
        return inFlight = new(image, language, context, version, capturedAt);
    }

    public bool Complete(Work work, IReadOnlyList<GameTextLine> lines, double milliseconds, double completedAt, string? issue = null)
    {
        if (!ReferenceEquals(inFlight, work)) return false;
        inFlight = null;
        if (work.Version != version) return false;

        nextAt = completedAt + (work.Context == GameTextNotebook.OpeningContext ? .45 : .85);
        var previous = Scan?.Lines ?? [];
        Scan = issue is null ? new(work.Image, work.Language, work.Context, lines, milliseconds) : null;
        Issue = issue;
        if (Scan is { } scan)
        {
            try
            {
                _ = ReadNotebook();
                StoreReading(work, scan, previous);
            }
            catch (Exception error) when (IsNotebookError(error))
            {
                LatestReading = null;
                Issue = error.Message;
            }
        }
        else LatestReading = null;
        return true;
    }

    private void StoreReading(Work work, GameTextScan scan, IReadOnlyList<GameTextLine> previous)
    {
        var visible = scan.Lines.Where(line => line.Confidence >= .45).Select(line => line.Text).ToArray();
        var keys = visible.Select(GameTextObservation.Key).ToArray();
        passageLines.IntersectWith(keys);
        var stable = LatestReading is { } reading && reading.Context == work.Context
            && work.CapturedAt - reading.CapturedAt is >= 0 and <= 2.5 ? reading : null;
        var cues = new[] { "inspect", "listen" }.Where(cue => keys.Count(key => key == cue) == 1)
            .Select(cue => new ThinkingCue(cue, stable?.Cues.FirstOrDefault(value => value.Text == cue)?.FirstSeen
                ?? work.CapturedAt)).ToArray();
        var satisfied = stable?.SatisfiedInspection;
        if (inspection is { } pending && (pending.Context != work.Context || work.CapturedAt - pending.StartedAt > 12))
            inspection = null;
        if (inspection is { } current)
        {
            if (work.CapturedAt > current.StartedAt)
            {
                var fresh = visible.Where(text => !current.Prior.Contains(GameTextObservation.Key(text))
                    && text.Count(char.IsLetter) >= 4).ToArray();
                var passage = string.Join(' ', fresh);
                if (passage.Length != 0 && current.Candidate == passage)
                {
                    var id = string.Join('\u001f', work.Language, work.Context, GameTextObservation.Key(passage), "passage");
                    Refresh(notebook.Import([new(id, passage, work.Context, work.Language, work.Image.ObservedAt, IsPassage: true)]));
                    passageLines.UnionWith(fresh.Select(GameTextObservation.Key));
                    satisfied = current.StartedAt;
                    inspection = null;
                }
                else inspection = current with { Candidate = passage };
            }
        }
        else
        {
            var independent = passageLines.Count == 0 ? scan : scan with
            {
                Lines = scan.Lines.Where(line => !passageLines.Contains(GameTextObservation.Key(line.Text))).ToArray()
            };
            Refresh(notebook.Remember(independent, previous));
        }
        LatestReading = new(work.Context, work.CapturedAt, visible, cues, satisfied);
    }

    private IReadOnlyList<GameTextObservation> ReadNotebook()
    {
        if (opening is not { } source) return notebook.Read();
        var imported = notebook.Import(source.Read().Where(row => row.Context == GameTextNotebook.OpeningContext));
        opening = null;
        return imported;
    }

    private void Refresh(IReadOnlyList<GameTextObservation> current)
    {
        if (ReferenceEquals(rows, current)) return;
        rows = current;
        questLog = null;
        Entries = GameTextLogEntry.Group(current);
    }

    private static bool IsNotebookError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
}
