using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotTrainingTests
{
    internal static void TestTrainingPlanSeparatesDomains()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-plan-").FullName;
        try
        {
            var captures = Path.Combine(temporary, "demonstrations");
            var first = BotCaptureTests.WriteFixture(captures);
            var hacker = first.Published.Single(clip => clip.BotDomain == "hacker");
            var gameplay = first.Published.Single(clip => clip.BotDomain == "gameplay");
            var repeats = new[] { hacker, hacker, gameplay };
            var rejected = BotTrainingJob.Plan(temporary, BotCaptureDomain.Hacker, repeats);
            Require(!rejected.CanTrain && rejected.Clips.SequenceEqual(new[] { hacker.Id }),
                "A duplicate or Game Play partner may not form a second Hacker training clip.");
            BotCaptureTests.WriteFixture(captures);
            var library = BotCaptureSession.Library(captures);
            foreach (var domain in new[] { BotCaptureDomain.Hacker, BotCaptureDomain.Gameplay })
            {
                var plan = BotTrainingJob.Plan(temporary, domain, library);
                Require(plan.CanTrain && plan.Clips.SequenceEqual(library.Where(row => row.BotDomain == plan.Domain).Select(row => row.Id))
                    && plan.Clips.Count == 2 && plan.PolicyName is null && !plan.UpgradeObservations,
                    "Each domain should start from its own two completed clips without a prior policy.");
            }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    internal static async Task TrainFromWindowsCaptures(string installed)
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-training-probe-").FullName;
        try
        {
            var captures = Path.Combine(temporary, "demonstrations");
            for (var index = 0; index < 4; index++) BotCaptureTests.WriteFixture(captures);
            var library = BotCaptureSession.Library(captures);
            var launcher = Path.Combine(installed, "training", "train-bot-windows.ps1");
            var host = Path.Combine(installed, "HollowKnightVision.Windows.Host.exe");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            foreach (var domain in new[] { BotCaptureDomain.Hacker, BotCaptureDomain.Gameplay })
            {
                var plan = BotTrainingJob.Plan(temporary, domain, library);
                var run = Guid.NewGuid();
                var start = BotTrainingJob.Prepare(temporary, launcher, host, run, plan);
                start.ArgumentList.Add("-Epochs");
                start.ArgumentList.Add("2");
                var result = await WindowsTrainingProcess.RunAsync(start, timeout.Token);
                Require(result.ExitCode == 0 && !result.Cancelled, result.Output.Text);
                Require(result.Output.Text.Contains("Verified ", StringComparison.Ordinal), "The training job did not invoke native inference.");
                var selected = BotTrainingJob.VerifyCompleted(temporary, domain, run);
                Require(Path.GetFileName(selected.Directory) == $"bot-{run:D}"
                    && selected.Policy.Version == BotPolicyContract.CurrentVersion,
                    "The Windows playback selector did not load the newly trained native policy.");
                using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(selected.Directory, "training-result.json")));
                var training = SessionIds(receipt.RootElement, "trainingSessions");
                var held = SessionIds(receipt.RootElement, "validationSessions");
                Require(training.Length != 0 && held.Length != 0 && !training.Intersect(held).Any()
                    && training.Concat(held).All(plan.Clips.Contains), "The trainer leaked a holdout or trained the other domain.");
                Require(receipt.RootElement.GetProperty("wallEvidence").GetProperty("withWall").GetInt32() > 0,
                    "The Windows recording's saved wall geometry did not enter the trained policy inputs.");
                if (domain == BotCaptureDomain.Gameplay)
                    Require(receipt.RootElement.GetProperty("blockadeTraining").GetProperty("validationClips").GetInt32() > 0,
                        "The legacy Game Play capture's verified pose did not recover blockade inputs during training.");
                var repeated = BotTrainingJob.Plan(temporary, domain, library);
                Require(!repeated.CanTrain && repeated.PolicyName == selected.Policy.Name
                    && repeated.LatestIncludedClips?.SetEquals(plan.Clips) == true,
                    "A completed run should select its policy and account for every training and validation clip.");
                Console.WriteLine($"PASS installed Windows {plan.Domain} trained from {plan.Clips.Count} captured clips; "
                    + $"{training.Length} training and {held.Length} validation sessions stayed separate; native playback selected the export.");
            }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    internal static void TestNewCaptureCountsTrackCompletionAndIgnoreRollback()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-new-captures-");
        try
        {
            var root = temporary.FullName;
            var library = Path.Combine(root, "demonstrations");
            var paired = BotCaptureTests.WriteFixture(library).Published;
            var hacker = paired.Single(clip => clip.BotDomain == "hacker");
            var gameplay = paired.Single(clip => clip.BotDomain == "gameplay");
            Require(Pending(paired) == new BotNewCaptureCounts(1, 1, 1), "The two sides of a paired capture are one new run.");

            Publish("hacker", "bot-earlier", 1, null, heldOut: false);
            _ = new BotModelStore(root, BotCaptureDomain.Hacker).LoadPlayback();
            Publish("hacker", "bot-latest", 2, hacker.Id, heldOut: true);
            Publish("gameplay", "bot-gameplay", 3, gameplay.Id, heldOut: false, completed: false);
            Require(Pending(paired) == new BotNewCaptureCounts(1, 0, 1),
                "A validation clip is included, and incomplete training leaves the other side pending.");
            Publish("gameplay", "bot-gameplay", 3, gameplay.Id, heldOut: false);
            Require(Pending(paired) == default, "Completed exports should account for the included clips in both domains.");

            var store = new BotModelStore(root, BotCaptureDomain.Hacker);
            _ = store.RestorePrevious();
            Require(store.Catalog().ActiveId == "bot-earlier" && Pending(paired) == default,
                "Changing playback to an older export must not make trained captures new.");

            var next = BotCaptureTests.WriteFixture(library).Published;
            var unpaired = gameplay with { Id = Guid.NewGuid(), ReplayId = null, PairedClipId = null };
            var rejected = new[]
            {
                gameplay with { Id = Guid.NewGuid(), Status = "interrupted" },
                gameplay with { Id = Guid.NewGuid(), ExpertDemonstration = false },
                gameplay with { Id = Guid.NewGuid(), ObservationCount = 0 },
                gameplay with { Id = Guid.NewGuid(), BotDomain = "unknown" }
            };
            Require(Pending([.. paired, .. next, .. next, unpaired, .. rejected]) == new BotNewCaptureCounts(2, 1, 2),
                "Pending must deduplicate paired runs and clips, including unpaired eligible recordings independently.");

            BotNewCaptureCounts Pending(IReadOnlyList<BotCaptureManifest> clips)
            {
                var hackerPlan = BotTrainingJob.Plan(root, BotCaptureDomain.Hacker, clips);
                var gameplayPlan = BotTrainingJob.Plan(root, BotCaptureDomain.Gameplay, clips);
                return BotTrainingJob.CountNewCaptures(clips, hackerPlan.LatestIncludedClips!, gameplayPlan.LatestIncludedClips!);
            }

            void Publish(string domain, string id, int order, Guid? clip, bool heldOut, bool completed = true)
            {
                var directory = Directory.CreateDirectory(Path.Combine(root, "models", domain, id)).FullName;
                BotPolicyTests.WriteRuntime(directory);
                File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
                {
                    botDomain = domain,
                    trainingSessions = !heldOut && clip.HasValue ? new[] { clip.Value } : Array.Empty<Guid>(),
                    validationSessions = heldOut && clip.HasValue ? new[] { clip.Value } : Array.Empty<Guid>()
                }));
                if (completed)
                    File.WriteAllText(Path.Combine(directory, "training-result.json"), JsonSerializer.Serialize(new
                    {
                        modelDirectory = id, botDomain = domain, nativeParity = true, eligibleForActivation = false,
                        createdAt = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(order)
                    }));
            }
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static Guid[] SessionIds(JsonElement root, string name) =>
        root.GetProperty(name).EnumerateArray().Select(id => id.GetGuid()).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
