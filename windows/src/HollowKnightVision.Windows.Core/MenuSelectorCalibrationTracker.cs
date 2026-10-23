namespace HollowKnightVision.Windows.Core;

public readonly record struct MenuSelectorBounds(StencilRect Left, StencilRect Right);
public sealed record MenuSelectorCorrection(string ContextIdentifier, string OptionIdentifier, string LanguageIdentifier,
    StencilRect Left, StencilRect Right, Guid SourceExampleIdentifier);

/// <summary>
/// Persistence portion of MenuStencilTracker.swift. Feed only the resolved scene's
/// winning selector pair, never speculative acquisition/language hypotheses.
/// Owned by the same serial capture queue as the menu tracker.
/// </summary>
public sealed class MenuSelectorCalibrationTracker
{
    private readonly string? calibrationPath, mirrorPath;
    private sealed record Sample(MenuSelectorBounds Bounds, double Timestamp);
    private readonly Dictionary<string, MenuSelectorBounds> learnedBounds = new(StringComparer.Ordinal);
    private string? pendingKey;
    private List<Sample> pending = [];
    public MenuStencilCalibration? Calibration { get; private set; }
    public string? Diagnostic { get; private set; }
    public int PendingSampleCount => pending.Count;

    public static string? ResolveCalibrationPath(bool usesDeployedCatalog, string? explicitPath) =>
        explicitPath is null ? null : Path.GetFullPath(explicitPath);

    public MenuSelectorCalibrationTracker(string? calibrationPath = null, string? mirrorPath = null,
        MenuStencilCalibration? fallbackCalibration = null)
    {
        this.calibrationPath = ResolveCalibrationPath(false, calibrationPath);
        this.mirrorPath = mirrorPath is null ? null : Path.GetFullPath(mirrorPath);
        Calibration = this.calibrationPath is null ? null
            : MenuStencilCalibration.Load(this.calibrationPath) ?? fallbackCalibration ?? MenuStencilCalibration.LoadDefault();
    }

    public void Reset()
    {
        pending.Clear(); pendingKey = null; Diagnostic = null;
        // A capture/context reset does not discard positions already learned on disk.
    }

    public void WaitingForMatch() => Diagnostic = "waiting-match";

    public MenuSelectorBounds? CurrentBounds(string contextIdentifier, string selectedIdentifier, string? languageIdentifier,
        MenuSelectorBounds? configuredBounds) => learnedBounds.TryGetValue(Key(contextIdentifier, selectedIdentifier, languageIdentifier), out var learned)
            ? learned : configuredBounds;

    public static MenuSelectorBounds? PersistedBounds(MenuStencilCalibration calibration, string contextIdentifier,
        string selectedIdentifier, string languageIdentifier)
    {
        var selector = calibration.FindScene(contextIdentifier)?.Selectors.FirstOrDefault(item => item.SelectedIdentifier == selectedIdentifier);
        if (selector?.Placement(languageIdentifier) is { } localized)
            return new(localized.LeftRect.Bounds, localized.RightRect.Bounds);
        return selector is not null && languageIdentifier == "en" ? new(selector.LeftRect.Bounds, selector.RightRect.Bounds) : null;
    }

    public void RecordMatch(string contextIdentifier, string selectedIdentifier, string? languageIdentifier,
        MenuSelectorBounds? configuredBounds, MenuSelectorBounds matchedBounds, double timestamp, DateTimeOffset? now = null)
    {
        if (calibrationPath is null || Calibration is null) { Diagnostic = "disabled"; return; }
        if (CurrentBounds(contextIdentifier, selectedIdentifier, languageIdentifier, configuredBounds) is not { } current)
        { Diagnostic = "missing-bounds"; return; }
        if (!double.IsFinite(timestamp) || !Valid(matchedBounds) || !Valid(current))
        { Diagnostic = "invalid-sample"; return; }
        if (!WithinRecovery(current.Left, matchedBounds.Left) || !WithinRecovery(current.Right, matchedBounds.Right))
        { Diagnostic = "outside-recovery"; return; }
        var language = languageIdentifier ?? "en";
        var key = Key(contextIdentifier, selectedIdentifier, language);
        var sample = new Sample(matchedBounds, timestamp);
        // Keep absolute screen coordinates: subtracting a jittering probe offset
        // would prevent an otherwise motionless selector from ever settling.
        if (pendingKey != key || pending.Count == 0 || timestamp - pending[^1].Timestamp > 1.5 || timestamp < pending[^1].Timestamp)
        {
            pendingKey = key; pending = [sample]; Diagnostic = "collecting 1"; return;
        }
        if (timestamp - pending[^1].Timestamp < .08)
        { Diagnostic = $"collecting {pending.Count}"; return; }
        pending.Add(sample);
        pending = pending.Where(item => timestamp - item.Timestamp <= 3).TakeLast(48).ToList();
        Diagnostic = $"collecting {pending.Count}";
        if (pending.Count < 8 || timestamp - pending[0].Timestamp < 2) return;
        var coherent = DominantStableCluster(pending);
        if (coherent.Count < 8 || coherent.Count * 3 < pending.Count * 2 || coherent[^1].Timestamp - coherent[0].Timestamp < 2)
        { Diagnostic = $"unstable {coherent.Count}/{pending.Count}"; return; }
        var learned = new MenuSelectorBounds(MedianRect(coherent.Select(item => item.Bounds.Left)), MedianRect(coherent.Select(item => item.Bounds.Right)));
        if (PersistedBounds(Calibration, contextIdentifier, selectedIdentifier, language) is { } persisted
            && CenterDistance(persisted.Left, learned.Left) < 1 && CenterDistance(persisted.Right, learned.Right) < 1
            && File.Exists(calibrationPath))
        { pending.Clear(); pendingKey = null; Diagnostic = "stored"; return; }
        var updated = Calibration.ReplacingSelector(contextIdentifier, selectedIdentifier, learned.Left, learned.Right, language, now);
        if (!Commit(updated, "write-failed")) return;
        learnedBounds[key] = learned;
        pending.Clear(); pendingKey = null; Diagnostic = $"saved {key}";
    }

    public void SynchronizeCorrections(IEnumerable<MenuSelectorCorrection> corrections, DateTimeOffset? now = null)
    {
        if (calibrationPath is null || Calibration is null) return;
        var updated = Calibration;
        var reviewed = (updated.ReviewedCorrectionIdentifiers ?? []).ToHashSet();
        var count = 0;
        foreach (var correction in corrections)
        {
            if (reviewed.Contains(correction.SourceExampleIdentifier)) continue;
            if (!Valid(new(correction.Left, correction.Right))) { Diagnostic = "correction-invalid"; return; }
            var persisted = PersistedBounds(updated, correction.ContextIdentifier, correction.OptionIdentifier, correction.LanguageIdentifier);
            if (persisted is null || !SameRect(persisted.Value.Left, correction.Left) || !SameRect(persisted.Value.Right, correction.Right))
                updated = updated.ReplacingSelector(correction.ContextIdentifier, correction.OptionIdentifier, correction.Left,
                    correction.Right, correction.LanguageIdentifier, now);
            reviewed.Add(correction.SourceExampleIdentifier);
            count++;
        }
        if (count == 0) return;
        updated = updated.RecordingReviewedCorrections(reviewed, now);
        if (Commit(updated, "correction-write-failed")) Diagnostic = $"imported {count} human corrections";
    }

    private bool Commit(MenuStencilCalibration value, string failureDiagnostic)
    {
        try
        {
            value.Write(calibrationPath!);
            if (mirrorPath is not null && !string.Equals(mirrorPath, calibrationPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) value.Write(mirrorPath);
            Calibration = value;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Diagnostic = failureDiagnostic; return false; }
    }

    private static List<Sample> DominantStableCluster(IReadOnlyList<Sample> samples)
    {
        List<Sample> best = [];
        foreach (var pivot in samples)
        {
            var cluster = samples.Where(item => CenterDistance(item.Bounds.Left, pivot.Bounds.Left) <= 2.5
                && CenterDistance(item.Bounds.Right, pivot.Bounds.Right) <= 2.5).ToList();
            if (cluster.Count > best.Count || cluster.Count == best.Count && cluster[^1].Timestamp > best[^1].Timestamp) best = cluster;
        }
        return best;
    }

    private static string Key(string context, string selected, string? language) => $"{context}:{selected}:{language ?? "en"}";
    private static bool Valid(MenuSelectorBounds pair) => pair.Left.IsFinite && !pair.Left.IsEmpty && pair.Right.IsFinite && !pair.Right.IsEmpty;
    private static bool WithinRecovery(StencilRect nominal, StencilRect actual) => Math.Abs(nominal.MidX - actual.MidX) <= 40 && Math.Abs(nominal.MidY - actual.MidY) <= 10;
    private static bool SameRect(StencilRect a, StencilRect b) => Math.Abs(a.X - b.X) <= .001 && Math.Abs(a.Y - b.Y) <= .001
        && Math.Abs(a.Width - b.Width) <= .001 && Math.Abs(a.Height - b.Height) <= .001;
    private static double CenterDistance(StencilRect a, StencilRect b) => Math.Sqrt(Math.Pow(a.MidX - b.MidX, 2) + Math.Pow(a.MidY - b.MidY, 2));
    private static StencilRect MedianRect(IEnumerable<StencilRect> rectangles)
    {
        var values = rectangles.ToArray();
        static double Median(IEnumerable<double> source)
        {
            var sorted = source.Order().ToArray(); var middle = sorted.Length / 2;
            return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        }
        return new(Median(values.Select(item => item.X)), Median(values.Select(item => item.Y)),
            Median(values.Select(item => item.Width)), Median(values.Select(item => item.Height)));
    }
}
