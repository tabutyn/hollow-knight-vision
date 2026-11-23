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
                var repeated = BotTrainingJob.Plan(temporary, domain, library);
                Require(!repeated.CanTrain && repeated.PolicyName == selected.Policy.Name,
                    "A completed run should select its policy and request fresh clips before another comparison.");
                Console.WriteLine($"PASS installed Windows {plan.Domain} trained from {plan.Clips.Count} captured clips; "
                    + $"{training.Length} training and {held.Length} validation sessions stayed separate; native playback selected the export.");
            }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static Guid[] SessionIds(JsonElement root, string name) =>
        root.GetProperty(name).EnumerateArray().Select(id => id.GetGuid()).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
