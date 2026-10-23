namespace HollowKnightVision.Windows.Core;

/// <summary>
/// A camera-pose correction estimated from a training observation.
/// This value deliberately carries no image or atlas data. Held-out frames use
/// only the timestamp and visit to interpolate corrections fitted from training
/// observations.
/// </summary>
public readonly record struct PoseCorrectionSample(
    int ObservationId,
    Guid VisitId,
    double Timestamp,
    AtlasPoint Correction)
{
    public bool IsFinite =>
        double.IsFinite(Timestamp)
        && double.IsFinite(Correction.X)
        && double.IsFinite(Correction.Y);
}

public static class PoseCorrectionInterpolator
{
    /// <summary>
    /// Returns a correction derived solely from finite samples in
    /// <paramref name="visitId"/>. Samples are ordered by timestamp,
    /// observation ID, and visit ID so results do not depend on collection
    /// order. A query outside the sampled interval holds the nearest endpoint
    /// correction.
    /// </summary>
    public static AtlasPoint Correction(
        double timestamp,
        Guid visitId,
        IReadOnlyList<PoseCorrectionSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(timestamp)) return new AtlasPoint(0, 0);

        var ordered = samples
            .Where(sample => sample.VisitId == visitId && sample.IsFinite)
            .OrderBy(sample => sample.Timestamp)
            .ThenBy(sample => sample.ObservationId)
            .ThenBy(sample => sample.VisitId)
            .ToArray();
        if (ordered.Length == 0) return new AtlasPoint(0, 0);

        var first = ordered[0];
        var last = ordered[^1];
        if (timestamp <= first.Timestamp) return first.Correction;
        if (timestamp >= last.Timestamp) return last.Correction;

        for (var index = 1; index < ordered.Length; index++)
        {
            var upper = ordered[index];
            if (timestamp > upper.Timestamp) continue;

            var lower = ordered[index - 1];
            var span = upper.Timestamp - lower.Timestamp;
            // Equal timestamps are resolved by deterministic ordering. The
            // endpoint check above selects the first ordered correction.
            if (span <= 0) return upper.Correction;

            var progress = (timestamp - lower.Timestamp) / span;
            return new AtlasPoint(
                lower.Correction.X
                    + (upper.Correction.X - lower.Correction.X) * progress,
                lower.Correction.Y
                    + (upper.Correction.Y - lower.Correction.Y) * progress);
        }

        return last.Correction;
    }

    public static AtlasPoint RefinedPose(
        AtlasPoint raw,
        double timestamp,
        Guid visitId,
        IReadOnlyList<PoseCorrectionSample> samples)
    {
        if (!double.IsFinite(raw.X) || !double.IsFinite(raw.Y)) return raw;
        var offset = Correction(timestamp, visitId, samples);
        return new AtlasPoint(raw.X + offset.X, raw.Y + offset.Y);
    }
}
