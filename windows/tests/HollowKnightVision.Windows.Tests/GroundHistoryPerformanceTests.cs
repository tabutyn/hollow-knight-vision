using System.Collections;
using System.Reflection;
using HollowKnightVision.Windows.Core;

internal static class GroundHistoryPerformanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type TrackerType = typeof(GroundHypothesisTracker);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static readonly DetectedFloorLine[] Lines = [new(40, new PixelSpan(0, 319))];

    private static (GroundHypothesisTracker Tracker, byte[] Pixels) LearnedBusyTracker()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0, backgroundGlobalSearch: true);
        // Deterministically model an occupied worker so the large-history test
        // measures foreground behavior independently of machine scheduling.
        var worker = Field(tracker, "globalSearchWorker");
        worker.GetType().GetField("busy", Private)!.SetValue(worker, true);
        var pixels = new byte[320 * 100];
        new Random(8371).NextBytes(pixels);
        for (var frame = 0; frame < 10; frame++)
            tracker.Update(pixels, 320, 100, Lines, timestamp: frame / 60d);
        return (tracker, pixels);
    }

    public static void TestBusyHistoryDoesNotAllocatePerStoredFeature()
    {
        var (tracker, pixels) = LearnedBusyTracker();
        var live = (IDictionary)Field(tracker, "globalFeatures");
        var bank = (IDictionary)Field(tracker, "retiredGlobalFeatures");
        var seed = live.Values.Cast<object>().First();
        var type = seed.GetType();
        for (var index = 0; index < 20_000; index++)
        {
            var feature = type.GetMethod("Copy")!.Invoke(seed, null)!;
            var id = index + 100_000;
            type.GetProperty("Id")!.SetValue(feature, id);
            type.GetProperty("WorldX")!.SetValue(feature, id * 16d);
            bank.Add(id, feature);
        }
        // Fixture population bypasses normal promotion and its invalidation.
        TrackerType.GetField("cachedRelocalizationFeatures", Private)!.SetValue(tracker, null);
        for (var frame = 0; frame < 5; frame++)
            tracker.Update(pixels, 320, 100, Lines, timestamp: 1 + frame / 60d);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = GroundHypothesisTrackingResult.Empty;
        for (var frame = 0; frame < 5; frame++)
            result = tracker.Update(pixels, 320, 100, Lines, timestamp: 2 + frame / 60d);
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 5;
        Check(perFrame < 2_000_000, $"Busy foreground allocated {perFrame:N0} bytes per frame.");
        Check(result.GlobalFeatureCount == 20_000 + live.Count, "History was dropped to reduce work.");
        Check(result.PoseVerified && result.CameraPosition == new AtlasPoint(0, 0), "Cached history changed camera pose.");
        tracker.Reset();
        var empty = tracker.Update(pixels, 320, 100, Lines, timestamp: 3);
        Check(empty.GlobalFeatureCount == 0, "Reset retained the cached history population.");
    }

    public static void TestSnapshotMetadataAndEvidenceRemainFrozen()
    {
        var (tracker, pixels) = LearnedBusyTracker();
        var live = (IDictionary)Field(tracker, "globalFeatures");
        var feature = live.Values.Cast<object>().First();
        var type = feature.GetType();
        var snapshot = type.GetMethod("Copy")!.Invoke(feature, null)!;
        var evidenceProperty = type.GetProperty("ReferencePatch")!;
        var frozen = (byte[])evidenceProperty.GetValue(snapshot)!;
        var expected = frozen.ToArray();
        var segmentProperty = type.GetProperty("SegmentId")!;
        var originalSegment = (int)segmentProperty.GetValue(snapshot)!;
        // Real evidence refresh replaces arrays on the mutable live record.
        tracker.Update(pixels, 320, 100, Lines, timestamp: 1);
        Check(!ReferenceEquals(frozen, evidenceProperty.GetValue(feature)), "Fixture did not refresh live evidence.");
        segmentProperty.SetValue(feature, originalSegment + 1);
        Check((int)segmentProperty.GetValue(snapshot)! == originalSegment, "Live metadata mutated a search snapshot.");
        Check(frozen.SequenceEqual(expected), "Live evidence mutated immutable search pixels.");
    }

    public static void TestVectorDescriptorDistanceMatchesScalarExactly()
    {
        var distance = TrackerType.GetMethod("DescriptorDistance", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<short[], short[], double>>();
        var random = new Random(4096);
        foreach (var length in new[] { 0, 1, 7, 15, 16, 17, 31, 32, 33, 48, 65, 127, 1024 })
        for (var sample = 0; sample < 20; sample++)
        {
            var left = new short[length];
            var right = new short[length];
            for (var index = 0; index < length; index++)
            {
                left[index] = sample == 0 ? short.MinValue : (short)random.Next(short.MinValue, short.MaxValue + 1);
                right[index] = sample == 0 ? short.MaxValue : (short)random.Next(short.MinValue, short.MaxValue + 1);
            }
            var expected = length == 0 ? double.PositiveInfinity
                : left.Zip(right).Sum(pair => (long)Math.Abs((int)pair.First - pair.Second)) / (double)length;
            Check(distance(left, right) == expected, $"Descriptor score changed at length {length}, sample {sample}.");
            if (length > 0) Check(distance(left, left) == 0, "Identical descriptor changed score.");
        }
        Check(double.IsPositiveInfinity(distance([1], [1, 2])), "Mismatched descriptor lengths were accepted.");
    }
}
