using HollowKnightVision.Windows.Core;

internal static class GameTextReaderTests
{
    private static readonly StencilRect Bounds = new(.1, .2, .3, .1);
    private static BgraFrame Frame(int id) => new(2, 2, 8, new byte[16], id, DateTimeOffset.UnixEpoch.AddSeconds(id));
    private static GameTextLine Line(string text, double confidence) => new(1, text, Bounds, confidence);

    internal static void TestTitleOcrFragments()
    {
        var reader = new GameTextReader("unused");
        string[] noise = ["Ce", "Ces", "Ce.", "Ce s", "Ce.s", "Ce's", "Cei", "Ceis", "Coi"];
        string[] useful = ["Yes", "No", "Go", "Ces 2", "Ces gates"];
        var scan = noise.Concat(useful).Select(text => Line(text, .99)).ToArray();
        var work = reader.TryStart(Frame(1), "en-US", "Game capture", 1)!;
        reader.Complete(work, scan, 20, 1.1);
        Require(reader.Scan!.Lines.Count == scan.Length && reader.LatestReading!.Lines.SequenceEqual(useful),
            "raw OCR retains the title fragments while frame evidence retains meaningful lines");
        Require(!reader.Entries.Any(e => noise.Contains(e.Observation.DisplayText))
            && !reader.PolicyContext().Contains("] Ce.s", StringComparison.Ordinal), "noise stays out of notebook evidence");

        var root = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(root.FullName, "notebook.json");
            GameTextObservation Row(string id, string text, string context, string language) =>
                new(id, text, context, language, DateTimeOffset.UnixEpoch, MatchingVersion: 2);
            var saved = new[] { Row("noise", "Ce.s", "main-title · Start Game", "en"), Row("suffix", "Coi", "Main Title - Quit Game", "en"),
                Row("french", "Ces", "Main Title", "fr"), Row("room", "Ces", "Dirtmouth", "en"),
                Row("corrected", "Ces", "Main Title", "en") with { BestText = "Yes" },
                Row("mixed", "Ce", "Main Title", "en") with { OtherContexts = ["Town"] } };
            File.WriteAllBytes(path, GameTextNotebook.EncodeTrace(saved));
            Require(new GameTextNotebook(path).Read().Select(row => row.Id).SequenceEqual(["french", "room", "corrected", "mixed"]),
                "old title-only fragments are hidden while other languages, rooms and corrected readings survive");
        }
        finally { root.Delete(true); }
    }

    internal static void TestReaderCadenceAndInvalidation()
    {
        var reader = new GameTextReader("unused");
        Require(reader.Enabled && reader.TryStart(Frame(1), "en", "Gameplay", double.NaN) is null, "invalid capture time");
        var first = reader.TryStart(Frame(1), "en", "Gameplay", 1)!;
        Require(first is not null && reader.TryStart(Frame(2), "en", "Gameplay", 5) is null, "one in-flight frame");

        reader.SetEnabled(false);
        Require(!reader.Complete(first!, [Line("Stale gameplay", 1)], 50, 5) && reader.Scan is null,
            "disabling should discard an unfinished scan");
        Require(reader.TryStart(Frame(3), "en", "Gameplay", 5) is null, "off should not start work");

        reader.SetEnabled(true);
        var second = reader.TryStart(Frame(4), "en", "Gameplay", 5)!;
        Require(reader.Complete(second, [Line("Eastward", .8)], 20, 5.5) && reader.Scan is { Language: "en" }, "published scan");
        Require(reader.TryStart(Frame(4), "en", "Gameplay", 7) is null, "deduplicate the same capture");
        Require(reader.TryStart(Frame(5), "en", "Gameplay", 6.3) is null, "throttle from completion");
        var third = reader.TryStart(Frame(6), "en", "Gameplay", 6.4)!;
        Require(third is not null && reader.TryStart(Frame(7), "es", "Gameplay", 6.5) is null && reader.Scan is null,
            "language changes immediately clear the old scan and preserve the one-worker limit");
        Require(!reader.Complete(third!, [], 100, 7), "discard a superseded language");
        var failed = reader.TryStart(Frame(8), "es", "Options", 7)!;
        Require(reader.Complete(failed, [], 40, 7.04, "unavailable") && reader.Issue == "unavailable" && reader.Scan is null,
            "publish a recognition error without retaining pixels");
        reader.ResetFrame();
        Require(reader.Issue is null, "capture shutdown clears the transient issue");
    }

    internal static void TestOpeningTransferAndNotebookReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reader = new GameTextReader(root);
            Publish(1, "Select Profile", 1, Line("Ordinary menu", .95));
            Publish(2, "Select Profile", 2, Line("Ordinary   menu", .65));
            Publish(3, GameTextNotebook.OpeningContext, 3,
                Line("Beyond this place", .88), Line("caption noise", .63), Line("100", 1));
            Require(reader.TryStart(Frame(4), "en", GameTextNotebook.OpeningContext, 3.2) is null, "opening cadence");
            var unfinished = reader.TryStart(Frame(4), "en", GameTextNotebook.OpeningContext, 3.5)!;
            var firstSave = new SaveProfileAtlas(1, Guid.NewGuid());
            reader.SelectProfile(firstSave);
            Require(!reader.Complete(unfinished, [Line("Old frame", 1)], 10, 3.7), "profile transfer discards late output");
            Require(reader.Entries is [{ Observation.Text: "Beyond this place" }], "only confirmed opening text follows save selection");

            Publish(5, "Room A", 4, Line("Wait in Darkness!", .78));
            Require(reader.Entries.Length == 1, "stable gameplay requires a repeat");
            Publish(6, "Room A", 5, Line("wait  in darkness!", .71));
            Publish(7, "Room B", 6, Line("Wait in Darkness", .91));
            Publish(8, "Room B", 7, Line("Wait in Darkness", .72));
            Require(reader.Entries.Length == 2 && reader.Entries[0].Contexts.SequenceEqual(new[] { "Room A", "Room B" }),
                "group a repeated reading observed in two rooms");
            var reopened = new GameTextReader(root);
            reopened.SelectProfile(firstSave);
            Require(reopened.Entries.Length == 2 && reopened.Entries[0].Updated == Frame(6).ObservedAt, "retain the original sighting date across rooms");
            reader.SelectProfile(new SaveProfileAtlas(1, Guid.NewGuid()));
            Require(reader.Entries.Length == 0, "a reused slot has its own notebook");
            reader.SelectProfile(firstSave);
            Require(reader.Entries.Length == 2, "switching back restores earlier observations");

            void Publish(int id, string context, double timestamp, params GameTextLine[] lines)
            {
                var work = reader.TryStart(Frame(id), "en", context, timestamp)
                    ?? throw new InvalidOperationException($"No work for frame {id}.");
                Require(reader.Complete(work, lines, 25, timestamp + .025), "complete scan");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal static void TestSwiftNotebookCompatibility()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-swift-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new SaveProfileAtlas(2, Guid.NewGuid());
            var path = Path.Combine(root, profile.Id.ToString("D").ToUpperInvariant() + ".json");
            const string swiftJson = """
            [
              {"id":"first","text":"Quen's Path","context":"Room A","language":"en","date":60,
               "interpretation":{"text":"Queen's Path","label":"clue","model":"resident"}},
              {"id":"second","text":"Queens Path","context":"Room B","language":"en","date":70,"isPassage":true,
               "interpretation":{"text":"Queen's Path","sameAs":"first","label":"clue","model":"resident"}}
            ]
            """;
            File.WriteAllText(path, swiftJson);
            var reader = new GameTextReader(root);
            reader.SelectProfile(profile);
            Require(reader.Entries is [{ Observation: { Annotation: "Queen's Path", DisplayText: "Queens Path", IsPassage: true } }, _],
                "retain earlier annotations separately from observed text");
            Require(reader.Entries[0].Contexts.SequenceEqual(new[] { "Room B" })
                && reader.Entries[0].Updated == new DateTimeOffset(2001, 1, 1, 0, 1, 10, TimeSpan.Zero),
                "Mac Date and original contexts");
            File.WriteAllText(path, "[{\"date\":\"broken\"}]");
            var damaged = new GameTextReader(root);
            damaged.SelectProfile(profile);
            Require(damaged.Entries.Length == 0 && damaged.Issue is not null
                && File.ReadAllText(path) == "[{\"date\":\"broken\"}]", "malformed evidence should remain untouched");
            var work = damaged.TryStart(Frame(1), "en", GameTextNotebook.OpeningContext, 1)!;
            Require(damaged.Complete(work, [Line("Opening Credits", .95)], 25, 1.025)
                && damaged.Scan is not null && damaged.Issue is not null
                && File.ReadAllText(path) == "[{\"date\":\"broken\"}]",
                "an unreadable notebook should preserve both its contents and the on-screen result");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static void TestOpeningFragmentsAndSpelling()
    {
        const string first = "In wilds beyond they speak your name with reverence and regret,";
        const string second = "For none could tame our savage souls yet you the challenge met,";
        const string third = "Under palest watch, you taught, we changed, base instincts were redeemed,";
        const string fourth = "A world you gave to bug and beast as they had never dreamed.";
        var root = Directory.CreateTempSubdirectory("hkv-poem-");
        try
        {
            var path = Path.Combine(root.FullName, "book.json");
            File.WriteAllText(path, """
                [{"id":"T9","text":"In wild","language":"en","context":"Opening cutscene","date":0,"confidence":1},
                 {"id":"T10","text":"In wilds beyond they l","language":"en","context":"Opening cutscene","date":0.5,"confidence":1},
                 {"id":"T11","text":"our name with rererence and regret,","language":"en","context":"Opening cutscene","date":0.5,"confidence":1}]
                """);
            var book = new GameTextNotebook(path);
            // Reopen migration without recent sightings, then replay the first live scans.
            Require(book.Read().Count == 2, "migration joins adjacent opening fragments");
            void See(double t, string text, double confidence = 1, StencilRect bounds = default, string context = GameTextNotebook.OpeningContext)
            {
                var date = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(t);
                var image = new BgraFrame(2, 2, 8, new byte[16], (long)(t * 10), date);
                var line = new GameTextLine(1, text, bounds, confidence);
                book.Remember(new(image, "en", context, [line], 1), [line]);
            }
            See(1, "In wilds beyond they l");
            See(1, "our name with rererence and regret,");
            See(1.5, first.Replace("reverence", "rererence"));
            See(2, first, .9);
            See(3, "For none could tame our svare souls fet vou the challengc mek");
            See(3.5, second.Replace("savage", "sarage"));
            See(4, "Under palest wal");
            See(4.5, third.Replace("watch", "watçh"));
            See(5, third);
            See(6, "For none could tame out sarage souls yet you the challenge met,");
            See(7, second.Replace("savage", "sayage"));
            See(8, fourth);
            See(17, third.Replace("we changed", "wre changed"));
            See(17.5, third.Replace("we changed", "me changed"));
            See(17.5, "A world you gonc to bug and beast as they had newer drcamod!");
            var bytes = File.ReadAllBytes(path);
            var rows = new GameTextNotebook(path).Read();
            Require(rows.Select(r => r.DisplayText).SequenceEqual([first, second, third, fourth])
                && rows[0] is { Id: "T9", Text: "In wild", MatchingVersion: 2 }, "four stable poem lines with original evidence");
            Require(bytes.SequenceEqual(File.ReadAllBytes(path)), "a version-two notebook reopens without rewriting");
            See(19, "In Hall", bounds: new(1, 1, 10, 1));
            See(20, "In Hallownest there is a gate", bounds: new(1, 7, 10, 1));
            See(21, "Pay 35 Geo to open the gate");
            See(22, "Pay 36 Geo to open the gate");
            Require(book.Read().Count == 8 && !NotebookTextMatch.Same("They had never dreamed", "They had ever dreamed"),
                "vertical scope, amounts and protected words prevent false matches");
        }
        finally { root.Delete(true); }
    }

    private static void Require(bool condition, string operation)
    {
        if (!condition) throw new InvalidOperationException("Game text: " + operation);
    }
}
