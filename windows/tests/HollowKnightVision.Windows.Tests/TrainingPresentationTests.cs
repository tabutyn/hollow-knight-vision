using System.Text;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class TrainingPresentationTests
{
    public static void TestNotificationDates()
    {
        var snapshot = DateTimeOffset.FromUnixTimeSeconds(100);
        DateTimeOffset[] none = [], old = [snapshot.AddSeconds(-10)], changed = [snapshot.AddSeconds(-10), snapshot, snapshot.AddSeconds(10), snapshot.AddSeconds(20)];
        Check(!LabelingTrainingNotificationPolicy.HasUntrainedChanges(none, null));
        Check(LabelingTrainingNotificationPolicy.HasUntrainedChanges(old, null));
        Check(!LabelingTrainingNotificationPolicy.HasUntrainedChanges(old, snapshot));
        Check(LabelingTrainingNotificationPolicy.UntrainedImageCount(changed, snapshot) == 2);
        Check(LabelingTrainingNotificationPolicy.UntrainedImageCount(changed, null) == 4);
    }
    public static void TestConfiguration()
    {
        Check(LabelingTrainingConfiguration.ForModel("shared-object-model") == new LabelingTrainingConfiguration(100, 13));
        Check(LabelingTrainingConfiguration.ForModel("game.mana") == new LabelingTrainingConfiguration(10, 13));
    }
    public static void TestManifestNotification()
    {
        var root = Directory.CreateTempSubdirectory("hkv-training-notification-").FullName;
        try
        {
            var time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            SavedLabelingExample Save(string context, string[] classes, bool negative = false, string[]? known = null)
            {
                var value = LabelingContractTests.Example(context, classes);
                var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
                var manifest = value.Manifest with { KnownClassIdentifiers = known,
                    Annotations = value.Manifest.Annotations.Select(box => box with { IsNegative = negative }).ToArray() };
                File.WriteAllText(Path.Combine(path, LabelingExampleStore.ManifestFilename), "{}");
                File.SetLastWriteTimeUtc(Path.Combine(path, LabelingExampleStore.ManifestFilename), time.AddSeconds(10).UtcDateTime);
                return value with { Manifest = manifest, DirectoryPath = path };
            }
            var old = Save("game", ["game.mana", "game.mana"]); var equal = Save("game", ["game.mana"]);
            var newer = Save("game", ["game.mana"]); var negative = Save("game", ["game.mana"], true);
            var known = Save("game", [], known: ["game.mana"]); var empty = Save("game", []); var menu = Save("main-title", ["game.mana"]);
            var missing = Save("game", ["game.mana"]); File.Delete(Path.Combine(missing.DirectoryPath, LabelingExampleStore.ManifestFilename));
            File.SetLastWriteTimeUtc(Path.Combine(old.DirectoryPath, LabelingExampleStore.ManifestFilename), time.AddSeconds(-10).UtcDateTime);
            File.SetLastWriteTimeUtc(Path.Combine(equal.DirectoryPath, LabelingExampleStore.ManifestFilename), time.UtcDateTime);
            SavedLabelingExample[] examples = [old, equal, newer, negative, known, empty, menu, missing];
            var dataset = Path.Combine(root, "dataset"); Directory.CreateDirectory(dataset);
            var data = new LabelingDatasetManifest(2, Guid.NewGuid(), "shared-object-model", ["game.mana"], time, [], true);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            var manifestPath = Path.Combine(dataset, "dataset.json"); File.WriteAllText(manifestPath, json);
            var candidate = new LabelingModelCandidate(root, dataset, new() { Id = Guid.NewGuid(), DatasetIdentifier = data.Id,
                ClassIdentifier = data.ClassIdentifier, CompletedAt = time.AddDays(1), ModelFilename = "Detector.onnx" }, null);
            Check(LabelingTrainingNotificationPolicy.CountExamples(examples, candidate) == 3, "Must count changed images, not boxes or model completion dates.");
            Check(LabelingTrainingNotificationPolicy.CountExamples(examples, null) == 5);
            File.WriteAllText(Path.Combine(old.DirectoryPath, "exemptions.json"), "{}");
            File.WriteAllText(Path.Combine(old.DirectoryPath, "image.png"), "not a real image");
            Check(LabelingTrainingNotificationPolicy.CountExamples(examples, candidate) == 3);
            foreach (var invalid in new[] { "{}", "{", json.Replace("\"items\": []", "\"items\": [{}]"), json.Replace("\"game.mana\"", "42") })
            {
                Check(invalid != json); File.WriteAllText(manifestPath, invalid);
                Check(LabelingTrainingNotificationPolicy.CountExamples(examples, candidate) == 5, "Invalid dataset cannot hide untrained images.");
            }
            File.Delete(manifestPath); Check(LabelingTrainingNotificationPolicy.CountExamples(examples, candidate) == 5);
        }
        finally { Directory.Delete(root, true); }
    }
    public static void TestProgressAndOutput()
    {
        Check(TrainingProgressUpdate.Parse("HKV_STAGE name=baseline") == "Measuring current model…");
        Check(TrainingProgressUpdate.Parse("HKV_STAGE name=training") == "Starting training…");
        Check(TrainingProgressUpdate.Parse("HKV_STAGE name=evaluating") == "Evaluating new model…");
        Check(TrainingProgressUpdate.Parse("HKV_STAGE name=exporting") == "Exporting new model…");
        Check(TrainingProgressUpdate.Parse("HKV_PROGRESS epoch=2 total=10 eta_seconds=60.01 loss=0.4") == "2/10 · about 2 min remaining");
        Check(TrainingProgressUpdate.Parse("HKV_PROGRESS epoch=0 total=10 eta_seconds=-2") == "0/10 · about 1 min remaining");
        Check(TrainingProgressUpdate.Parse("HKV_PROGRESS epoch=10 total=10 eta_seconds=0\r") == "10/10 · evaluating next…");
        foreach (var invalid in new[] { "", "HKV_STAGE name=unknown", "HKV_STAGE name=training name=exporting", "HKV_PROGRESS_more epoch=1 total=2 eta_seconds=2", "HKV_PROGRESS epoch=1 total=0 eta_seconds=1", "HKV_PROGRESS epoch=3 total=2 eta_seconds=1", "HKV_PROGRESS epoch=-1 total=2 eta_seconds=1", "HKV_PROGRESS epoch=1 total=2 eta_seconds=NaN", "HKV_PROGRESS epoch=1 total=2 eta_seconds=Infinity", "HKV_PROGRESS epoch=1 total=2", "HKV_PROGRESS epoch=no total=2 eta_seconds=1" })
            Check(TrainingProgressUpdate.Parse(invalid) is null, invalid);
        var buffer = new TrainingOutputBuffer();
        buffer.Append(Encoding.UTF8.GetBytes("HKV_PROG")); buffer.Append(Encoding.UTF8.GetBytes("stderr warning\r\n"), true);
        var value = buffer.Append(Encoding.UTF8.GetBytes("RESS epoch=2 total=10 eta_seconds=60.01\r\n"));
        Check(value.ProgressText == "2/10 · about 2 min remaining");
        var bytes = Encoding.UTF8.GetBytes("Completed 🐛");
        for (var index = 0; index < bytes.Length; index++) buffer.Append(bytes.AsSpan(index, 1));
        value = buffer.Complete(); Check(value.LastMessage == "Completed 🐛" && value.ProgressText == "2/10 · about 2 min remaining");
        value = buffer.Append(Encoding.UTF8.GetBytes(new string('x', TrainingOutputBuffer.MaximumBytes + 100) + "HKV_STAGE name=baseline\n"));
        Check(Encoding.UTF8.GetByteCount(value.Text) <= TrainingOutputBuffer.MaximumBytes && value.ProgressText != "Measuring current model…" && value.LastMessage == "Trainer output line exceeded the log limit.");
        value = buffer.Append(Encoding.UTF8.GetBytes("HKV_STAGE name=exporting\n")); Check(value.ProgressText == "Exporting new model…");
        Parallel.For(0, 20, _ => buffer.Append(Encoding.UTF8.GetBytes("parallel\n"), true));
        Check(buffer.Current.Sequence == value.Sequence + 20 && buffer.Current.ProgressText == value.ProgressText);
        var interleaved = new TrainingOutputBuffer(); var emoji = Encoding.UTF8.GetBytes("🐛");
        interleaved.Append(emoji.AsSpan(0, 2)); interleaved.Append(Encoding.UTF8.GetBytes("stderr\n"), true);
        value = interleaved.Append(emoji.AsSpan(2));
        Check(value.Text == "stderr\n🐛", "Combined stdout/stderr tore a split Unicode character.");
        value = interleaved.Append(Encoding.UTF8.GetBytes(new string('é', TrainingOutputBuffer.MaximumBytes / 2 + 2) + "🐛x"));
        Check(!value.Text.Contains('\ufffd') && value.Text.EndsWith("🐛x") && Encoding.UTF8.GetByteCount(value.Text) <= TrainingOutputBuffer.MaximumBytes,
            "Bounded log split its first UTF-8 code point or exceeded its byte limit.");
    }
    internal static void Check(bool condition, string message = "Training parity assertion failed.")
    { if (!condition) throw new Exception(message); }
}
