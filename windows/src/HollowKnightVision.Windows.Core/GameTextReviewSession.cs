using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record GameTextReviewOptions(bool Enabled = false, string Model = "gemma3:4b", bool UseCompatibilityService = false)
{
    [JsonIgnore]
    public LocalThinkingModel PreferredModel =>
        LocalThinkingModel.All.FirstOrDefault(option => option.Identifier == Model) ?? LocalThinkingModel.Gemma;
}

/// <summary>Processes resident-model work in order on the reader's dispatcher.</summary>
public sealed class GameTextReviewSession(GameTextReader reader, LocalThinkingClient client)
{
    private readonly GameTextCleanup cleanup = new(client);
    private readonly HashSet<string> attempted = new(StringComparer.Ordinal);
    private long notebookGeneration = -1;
    private int batchSize = 4;

    public async Task<TimeSpan> ReviewAsync(GameTextReviewOptions options, Action<string?> status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Enabled || !reader.Enabled)
        {
            status(null);
            return TimeSpan.FromSeconds(3);
        }
        var generation = reader.NotebookGeneration;
        if (generation != notebookGeneration)
        {
            attempted.Clear();
            batchSize = 4;
            notebookGeneration = generation;
        }

        try
        {
            var pending = reader.Observations.Where(row => row.Interpretation is null).ToArray();
            var needsNotes = reader.QuestContext.HasUnprocessed(reader.QuestLog);
            if (pending.Length == 0 && !needsNotes)
            {
                status(null);
                return TimeSpan.FromSeconds(3);
            }

            var batch = SelectBatch(pending);
            if (pending.Length > 0 && batch.Count == 0)
            {
                attempted.Clear();
                if (!needsNotes) return TimeSpan.FromSeconds(30);
            }

            var resident = await client.FindResidentAsync(options.PreferredModel, options.UseCompatibilityService, token);
            RequireCurrentNotebook();
            if (resident is null)
            {
                status("OCR captured. Waiting for a loaded Ollama model.");
                return TimeSpan.FromSeconds(12);
            }
            if (batch.Count != 0)
            {
                status($"Proofreading with {resident.Model.Title}…");
                attempted.UnionWith(batch.Select(row => row.Id));
                var edits = await cleanup.CorrectAsync(batch, reader.Entries, resident, token);
                RequireCurrentNotebook();
                var current = reader.Observations.ToDictionary(row => row.Id, StringComparer.Ordinal);
                var unchanged = batch.Where(row => current.TryGetValue(row.Id, out var latest) && latest.DisplayText == row.DisplayText)
                    .ToDictionary(row => row.Id, row => edits[row.Id], StringComparer.Ordinal);
                foreach (var id in edits.Keys.Except(unchanged.Keys)) attempted.Remove(id);
                reader.ApplyCorrections(generation, unchanged);
            }

            var log = reader.QuestLog;
            var context = reader.QuestContext;
            if (context.HasUnprocessed(log))
            {
                status($"Updating quest notes with {resident.Model.Title}…");
                await context.PrepareAsync(log,
                    (prior, page, cancellation) => AtlasQuestReview.ReviseAsync(client, resident, prior, page, cancellation), token);
                RequireCurrentNotebook();
            }
            status(null);
            return TimeSpan.FromSeconds(batch.Count == 0 ? 3 : 1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or HttpRequestException or OperationCanceledException)
        {
            RequireCurrentNotebook();
            batchSize = 1;
            status(error is OperationCanceledException
                ? "Proofreading paused. The local Ollama request timed out."
                : "Proofreading paused. " + error.Message);
            return TimeSpan.FromSeconds(20);
        }

        void RequireCurrentNotebook()
        {
            token.ThrowIfCancellationRequested();
            if (generation != reader.NotebookGeneration) throw new OperationCanceledException();
        }
    }

    private List<GameTextObservation> SelectBatch(IEnumerable<GameTextObservation> pending)
    {
        var batch = new List<GameTextObservation>();
        var characters = 0;
        foreach (var row in pending)
        {
            if (attempted.Contains(row.Id)) continue;
            var length = GameTextObservation.Length(row.DisplayText);
            if (batch.Count == batchSize || (batch.Count != 0 && characters + length > 1000)) break;
            batch.Add(row);
            characters += length;
        }
        return batch;
    }
}
