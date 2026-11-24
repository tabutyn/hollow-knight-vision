namespace HollowKnightVision.Windows.Core;

/// <summary>Tracks which measured images share a local coordinate system before Atlas write throttling.</summary>
internal sealed class CaptureIslandLineage
{
    private const int ReferenceCapacity = 16_384;
    private readonly Dictionary<long, long> references = [];
    private readonly Queue<long> referenceOrder = [];
    private (long Generation, string Room, long Timestamp)? lastCapture;
    private long nextLineage;

    internal long? Observe(long generation, string room, double timestamp, bool measured,
        double? referenceTimestamp, bool discontinuity = false)
    {
        if (!TryTimestampKey(timestamp, out var current)) return null;
        if (discontinuity || lastCapture is { } last
            && (generation != last.Generation || room != last.Room || current < last.Timestamp))
            Reset();
        lastCapture = (generation, room, current);
        if (!measured) return null;

        long referenceKey = 0;
        var hasEarlierReference = !discontinuity && referenceTimestamp is { } reference
            && reference < timestamp && TryTimestampKey(reference, out referenceKey);
        if (hasEarlierReference && (references.TryGetValue(referenceKey, out var owner)
            || references.TryGetValue(referenceKey - 1, out owner)
            || references.TryGetValue(referenceKey + 1, out owner)))
        {
            Record(current, owner);
            return owner;
        }

        var lineage = ++nextLineage;
        Record(current, lineage);
        // Repeated matches to an unknown seed agree on a new local frame. Its
        // own earlier screenshot never becomes a measured member of this island.
        if (hasEarlierReference) Record(referenceKey, lineage);
        return lineage;
    }

    internal void Reset()
    {
        // Reset references, keeping IDs unique throughout this capture worker.
        references.Clear();
        referenceOrder.Clear();
        lastCapture = null;
    }

    private void Record(long key, long lineage)
    {
        if (references.TryAdd(key, lineage))
        {
            referenceOrder.Enqueue(key);
            if (referenceOrder.Count > ReferenceCapacity)
                references.Remove(referenceOrder.Dequeue());
        }
        else references[key] = lineage;
    }

    private static bool TryTimestampKey(double timestamp, out long key)
    {
        key = 0;
        if (!(Math.Abs(timestamp) < long.MaxValue / 1_000_000d - 2)) return false;
        key = TimestampKey(timestamp);
        return true;
    }

    private static long TimestampKey(double timestamp) => (long)Math.Round(timestamp * 1_000_000);
}
