namespace HollowKnightVision.Windows.Core;

public sealed record LiveObjectDetectionBatch(IReadOnlyList<ObjectDetection> Detections,
    long SourceFrameIdentifier, double SourceTimestamp, long CaptureGeneration, double InferenceDuration);

public static class LiveObjectDetectionFreshness
{
    public const double MaximumAge = .4;
    public static IReadOnlyList<ObjectDetection> Detections(LiveObjectDetectionBatch? batch,
        long captureGeneration, double timestamp) => batch is not null
        && batch.CaptureGeneration == captureGeneration && double.IsFinite(timestamp)
        && timestamp >= batch.SourceTimestamp && timestamp - batch.SourceTimestamp <= MaximumAge
            ? batch.Detections : [];
}

/// <summary>Mac live-model policy, applied before tracking. All Windows boxes are top-origin.</summary>
public static class LiveObjectDetectionPostprocessor
{
    public const string Knight = "game.playable-knight";
    public static float ConfidenceThreshold(string classIdentifier) =>
        LabelingClassIdentity.CanonicalIdentifier(classIdentifier) == Knight ? .05f : .5f;

    public static IReadOnlyList<ObjectDetection> Process(IEnumerable<ObjectDetection> predictions) =>
        Suppress(predictions.Select(Prepare).OfType<ObjectDetection>(), .45, 12, 100);

    internal static ObjectDetection? Prepare(ObjectDetection prediction)
    {
        var id = LabelingClassIdentity.CanonicalIdentifier(prediction.ClassIdentifier);
        if (!float.IsFinite(prediction.Confidence) || prediction.Confidence < ConfidenceThreshold(id)) return null;
        var box = ObjectDetectionGeometry.UnitRect(prediction.Box);
        var area = box.Width * box.Height;
        if (box.IsEmpty || area < .00025 || area > .5) return null;
        return prediction with { ClassIdentifier = id, Box = ObjectDetectionGeometry.Box(box) };
    }

    internal static IReadOnlyList<ObjectDetection> Suppress(IEnumerable<ObjectDetection> candidates,
        double overlap, int maximumPerClass, int maximumDetections)
    {
        var selected = new List<ObjectDetection>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Confidence))
        {
            var sameClass = selected.Where(item => item.ClassIdentifier == candidate.ClassIdentifier).ToArray();
            if (sameClass.Length >= maximumPerClass
                || sameClass.Any(item => candidate.Box.IntersectionOverUnion(item.Box) >= overlap)) continue;
            selected.Add(candidate);
            if (selected.Count == maximumDetections) break;
        }
        return selected;
    }
}

internal static class ObjectDetectionGeometry
{
    internal static readonly StencilRect Unit = new(0, 0, 1, 1);
    internal static StencilRect Rect(NormalizedBox box) => new(box.X, box.Y, box.Width, box.Height);
    internal static NormalizedBox Box(StencilRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
    internal static StencilRect UnitRect(NormalizedBox box)
    {
        var rect = Rect(box);
        if (!rect.IsFinite || !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom)) return default;
        return new StencilRect(Math.Min(rect.X, rect.Right), Math.Min(rect.Y, rect.Bottom),
            Math.Abs(rect.Width), Math.Abs(rect.Height)).Intersect(Unit);
    }
    internal static double Distance(StencilRect first, StencilRect second) =>
        Length(first.MidX - second.MidX, first.MidY - second.MidY);
    internal static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
}

/// <summary>Class-aware smoothing with two-hit admission and four missed-inference grace.</summary>
public sealed class LiveObjectDetectionTracker
{
    private sealed record Track(ObjectDetection Detection, double Confidence, int Hits, int Misses);
    private List<Track> tracks = [];
    private long? generation;
    public void Reset() { tracks.Clear(); generation = null; }
    public IReadOnlyList<ObjectDetection> Update(IReadOnlyList<ObjectDetection> detections, long captureGeneration)
    {
        if (generation != captureGeneration) { tracks.Clear(); generation = captureGeneration; }
        var unmatched = Enumerable.Range(0, tracks.Count).ToHashSet();
        var next = tracks.ToList();
        foreach (var detection in detections.OrderByDescending(item => item.Confidence))
        {
            var id = LabelingClassIdentity.CanonicalIdentifier(detection.ClassIdentifier);
            var current = ObjectDetectionGeometry.Rect(detection.Box);
            var match = -1; var best = double.NegativeInfinity;
            foreach (var index in unmatched)
            {
                var prior = tracks[index].Detection;
                if (prior.ClassIdentifier != id) continue;
                var rect = ObjectDetectionGeometry.Rect(prior.Box);
                var iou = prior.Box.IntersectionOverUnion(detection.Box);
                var distance = ObjectDetectionGeometry.Distance(rect, current);
                var scale = Math.Max(.045, ObjectDetectionGeometry.Length(rect.Width, rect.Height) * 1.6);
                if (iou < .08 && distance > scale) continue;
                var score = iou * 2 - distance;
                if (score > best) { best = score; match = index; }
            }
            if (match < 0) next.Add(new(detection with { ClassIdentifier = id }, detection.Confidence, 1, 0));
            else
            {
                unmatched.Remove(match);
                var previous = tracks[match]; var box = previous.Detection.Box;
                var alpha = previous.Hits < 2 ? .58 : .34;
                var confidence = previous.Confidence * .35 + detection.Confidence * .65;
                var blended = new NormalizedBox(box.X + (current.X - box.X) * alpha,
                    box.Y + (current.Y - box.Y) * alpha, box.Width + (current.Width - box.Width) * alpha,
                    box.Height + (current.Height - box.Height) * alpha);
                next[match] = new(detection with { ClassIdentifier = id, Box = blended }, confidence, previous.Hits + 1, 0);
            }
        }
        foreach (var index in unmatched)
            next[index] = next[index] with { Misses = next[index].Misses + 1, Confidence = next[index].Confidence * .82 };
        tracks = next.Where(item => item.Misses <= 4 && item.Confidence >= .18).ToList();
        return tracks.Where(item => item.Hits >= 2 || item.Confidence >= .88)
            .Select(item => item.Detection with { Confidence = (float)item.Confidence }).ToArray();
    }
}
