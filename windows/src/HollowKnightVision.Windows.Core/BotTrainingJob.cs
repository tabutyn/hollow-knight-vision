using System.Diagnostics;
using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record BotTrainingPlan(string Domain, string? PolicyName, IReadOnlyList<Guid> Clips,
    bool UpgradeObservations, string? Unavailable)
{
    public bool CanTrain => Unavailable is null;
}

public static class BotTrainingJob
{
    private const double HeldAttackCost = .05;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Session(Guid Id);
    private sealed record SourceReport(Session[]? Sessions);
    private sealed record ValidationStrategy(Guid? SourceSession);
    private sealed record ParentEvidence(Guid[]? TrainingSessions, Guid[]? ValidationSessions,
        Guid[]? AncestorSessions, SourceReport? SourceReport, ValidationStrategy? ValidationStrategy, double? HeldAttackCost);
    private sealed record Receipt(Guid Id, string BotDomain, string ModelDirectory, bool NativeParity, DateTimeOffset CreatedAt);

    public static BotTrainingPlan Plan(string root, BotCaptureDomain domain, IReadOnlyList<BotCaptureManifest> library)
    {
        var name = domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay";
        var clips = library.Where(clip => clip.BotDomain == name && clip.Status == "completed" && clip.ObservationCount > 0)
            .Select(clip => clip.Id).Distinct().ToArray();
        var active = new BotModelStore(root, domain).LoadPlayback();
        var upgrade = active is not null && active.Policy.Version < BotPolicyContract.CurrentVersion;
        var unavailable = clips.Length < 2 ? "Record two separate clips for this bot." : null;
        if (unavailable is null && active is not null && !upgrade)
        {
            var parent = Read<ParentEvidence>(Path.Combine(active.Directory, "manifest.json"));
            var known = (parent.TrainingSessions ?? []).Concat(parent.ValidationSessions ?? [])
                .Concat(parent.AncestorSessions ?? []).Concat(parent.SourceReport?.Sessions?.Select(row => row.Id) ?? [])
                .ToHashSet();
            if (parent.ValidationStrategy?.SourceSession is { } source) known.Add(source);
            var fresh = clips.Count(id => !known.Contains(id));
            var held = (parent.ValidationSessions ?? []).ToHashSet();
            held.ExceptWith(parent.TrainingSessions ?? []);
            var canRescore = parent.HeldAttackCost != HeldAttackCost && clips.Any(held.Contains)
                && clips.Any(id => !held.Contains(id));
            if (fresh < 2 && !canRescore) unavailable = "Record two new clips for a held-out comparison.";
        }
        return new(name, active?.Policy.Name, clips, upgrade, unavailable);
    }

    public static ProcessStartInfo Prepare(string root, string launcher, string host, Guid id, BotTrainingPlan plan)
    {
        if (!plan.CanTrain) throw new InvalidOperationException(plan.Unavailable);
        if (!File.Exists(launcher) || !File.Exists(host))
            throw new FileNotFoundException("Install the current Windows build to train the bot.");
        var run = Path.Combine(root, "training-runs", id.ToString("D"));
        var selection = Path.Combine(run, "selection.json");
        AtomicFile.Write(selection, JsonSerializer.SerializeToUtf8Bytes(plan.Clips));
        var start = new ProcessStartInfo("powershell.exe");
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", launcher,
            "-BotRoot", root, "-RunId", id.ToString("D"), "-Selection", selection, "-Domain", plan.Domain, "-Vision", host })
            start.ArgumentList.Add(argument);
        if (plan.UpgradeObservations) start.ArgumentList.Add("-UpgradeObservations");
        return start;
    }

    public static BotPlaybackModel VerifyCompleted(string root, BotCaptureDomain domain, Guid id)
    {
        var name = domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay";
        var version = $"bot-{id:D}";
        var path = CompletionPath(root, domain, id);
        var receipt = Read<Receipt>(path);
        if (receipt.Id != id || receipt.BotDomain != name || receipt.ModelDirectory != version || !receipt.NativeParity
            || receipt.CreatedAt == default)
            throw new InvalidDataException("The completed run's verified bot export is missing.");
        _ = BotNativePolicy.Load(Path.GetDirectoryName(path)!);
        return new BotModelStore(root, domain).UseLatestVerified()
            ?? throw new InvalidDataException("No verified playback checkpoint was selected.");
    }

    public static string CompletionPath(string root, BotCaptureDomain domain, Guid id) =>
        Path.Combine(root, "models", domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay",
            $"bot-{id:D}", "training-result.json");

    private static T Read<T>(string path) where T : class
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(file, Json) ?? throw new InvalidDataException("Bot training metadata is missing.");
    }
}
