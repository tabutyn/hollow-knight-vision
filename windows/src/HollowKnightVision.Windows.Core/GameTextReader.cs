using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>Owns the current reading session. Call from one dispatcher.</summary>
public sealed class GameTextReader(string notebookDirectory)
{
    public sealed record Work(BgraFrame Image, string Language, string Context, long Version);
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
        return inFlight = new(image, language, context, version);
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
                Refresh(notebook.Remember(scan, previous));
            }
            catch (Exception error) when (IsNotebookError(error)) { Issue = error.Message; }
        }
        return true;
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
