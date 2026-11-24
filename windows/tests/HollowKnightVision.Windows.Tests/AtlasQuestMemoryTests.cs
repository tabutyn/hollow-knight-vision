using System.Text;
using HollowKnightVision.Windows.Core;
using static GameTextReviewTests;

internal static class AtlasQuestMemoryTests
{
    internal static void TestIncrementalMemoryAndCorrections()
    {
        Run(async () =>
        {
            var root = TemporaryRoot();
            try
            {
                var first = Row("one", "A ferry travels east.");
                var second = Row("two", "The eastern ferry is closed.");
                var path = Path.Combine(root, "quest.json");
                var initial = new AtlasQuestContext(path);
                var lead = new AtlasQuestNote("Investigate the eastern ferry.", [1]);
                await initial.PrepareAsync(new([first]), (old, page, _) =>
                {
                    Check(!old.All.Any() && page.Select(row => row.Source).SequenceEqual([1]), "first page");
                    return Task.FromResult(new AtlasQuestNotes([], [lead], []));
                }, default);
                var reopened = new AtlasQuestContext(path);
                var full = new AtlasQuestLog([first, second]);
                var blocked = new AtlasQuestNote("Find when the eastern ferry reopens.", [1, 2]);
                await reopened.PrepareAsync(full, (old, page, _) =>
                {
                    Check(old.Destinations.Single().Text == lead.Text && page.Select(row => row.Source).SequenceEqual([2]),
                        "second request uses the previous notes and only newly appended evidence");
                    return Task.FromResult(new AtlasQuestNotes([], [], [blocked]));
                }, default);
                await reopened.PrepareAsync(full, (_, _, _) => throw new InvalidOperationException("Repeated inference."), default);
                Check(reopened.NotesFor(full).Unknowns.Single().Text == blocked.Text, "saved updates");
                var corrected = new AtlasQuestLog([first with { Text = "A lift travels west." }, second]);
                await reopened.PrepareAsync(corrected, (old, page, _) =>
                {
                    Check(!old.All.Any() && page.Select(row => row.Source).SequenceEqual([1, 2]),
                        "a changed source invalidates the digest and rebuilds the notes");
                    return Task.FromResult(AtlasQuestNotes.Empty);
                }, default);
                File.WriteAllText(path, "incomplete checkpoint");
                Check(new AtlasQuestContext(path).HasUnprocessed(full), "damaged derived checkpoint can be rebuilt from original evidence");
            }
            finally { Remove(root); }
        });
    }

    internal static void TestPagedEvidenceAndRejectedCitations()
    {
        Run(async () =>
        {
            var log = new AtlasQuestLog([Row("long", new string('古', 1700), "ja")]);
            var context = new AtlasQuestContext(null);
            await RejectAsync<InvalidDataException>(() => context.PrepareAsync(log, (_, _, _) =>
                Task.FromResult(new AtlasQuestNotes([new("Go east.", [999])], [], [])), default));
            var pages = new List<string>();
            var count = 0;
            await context.PrepareAsync(log, (_, page, _) =>
            {
                count++;
                Check(page.Sum(row => Encoding.UTF8.GetByteCount(row.Line) + 1) <= 1350, "bounded page bytes");
                pages.AddRange(page.Select(row => row.Line));
                return Task.FromResult(AtlasQuestNotes.Empty);
            }, default);
            Check(count > 1 && pages.SequenceEqual(log.Excerpts.Select(row => row.Line)), "invalid results never advance the checkpoint");
            var evidence = log.SelectionEvidence(context.NotesFor(log), "");
            Check(Encoding.UTF8.GetByteCount(evidence) < 1500 && evidence.Contains("/" + log.Excerpts.Count + " excerpts"),
                "selection is chronologically ordered and bounded");

            var oldest = Row("old", "The stag station is beyond the lever.");
            var rows = new[] { oldest }.Concat(Enumerable.Range(1, 45)
                .Select(idx => Row("row" + idx, "A passage numbered " + idx))).ToArray();
            var selected = new AtlasQuestLog(rows).SelectionEvidence(AtlasQuestNotes.Empty, "Find the stag station");
            Check(selected.Contains("[R1 · King's Pass] The stag station", StringComparison.Ordinal), "goal ranks old evidence first");
        });
    }

    internal static void TestPromptsDestinationsAndCancellation()
    {
        Run(async () =>
        {
            var cue = Row("prompt", "INSPECT");
            var context = new AtlasQuestContext(null);
            var onlyPrompt = new AtlasQuestLog([cue]);
            await context.PrepareAsync(onlyPrompt, (_, _, _) =>
                throw new InvalidOperationException("A bare cue should not ask Ollama to change quest notes."), default);
            Check(onlyPrompt.SelectionEvidence(context.NotesFor(onlyPrompt), "").Contains("INSPECT", StringComparison.Ordinal),
                "raw prompt remains visible in evidence");
            var clue = Row("clue", "A gate leads east.", "en", "Dirtmouth");
            var partial = new AtlasQuestLog([cue, clue]);
            await context.PrepareAsync(partial, (_, page, _) =>
            {
                Check(page.Count == 1 && page[0].Source == 2, "prompt is skipped in quest generation");
                return Task.FromResult(new AtlasQuestNotes([], [new("Dirtmouth", [2])], [new("Find where the gate leads.", [2])]));
            }, default);
            Check(context.NotesFor(partial).Destinations.Length == 0, "a room label alone cannot support a destination");
            var third = Row("gate", "Return to Dirtmouth.");
            var appended = new AtlasQuestLog([cue, clue, third]);
            await RejectAsync<InvalidDataException>(() => context.PrepareAsync(appended, (_, _, _) =>
                Task.FromResult(new AtlasQuestNotes([], [new("Invented gate", [2])], [])), default));
            await RejectAsync<OperationCanceledException>(() => context.PrepareAsync(appended, (_, _, _) =>
            {
                context.Invalidate();
                return Task.FromResult(new AtlasQuestNotes([], [new("Dirtmouth", [3])], []));
            }, default));
            Check(context.HasUnprocessed(appended), "superseded response must leave new evidence available");
            await context.PrepareAsync(appended, (_, _, _) =>
                Task.FromResult(new AtlasQuestNotes([], [new("Dirtmouth", [3])], [])), default);
            Check(context.NotesFor(appended).Destinations.Single().Text == "Dirtmouth", "new passage supports the destination");
        });
    }
}
