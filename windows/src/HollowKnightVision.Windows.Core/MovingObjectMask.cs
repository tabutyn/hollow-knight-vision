namespace HollowKnightVision.Windows.Core;

/// <summary>Use the measured Knight and its predicted sweep, never its lagging display box.</summary>
public static class MovingObjectMask
{
    public static IReadOnlyList<ObjectDetection> Detections(IReadOnlyList<ObjectDetection> displayed,
        LiveObjectDetectionBatch? current, LiveObjectDetectionBatch? previous, long generation, double timestamp)
    {
        if (current is null || current.CaptureGeneration != generation || !double.IsFinite(timestamp)
            || timestamp < current.SourceTimestamp || timestamp - current.SourceTimestamp > .25) return displayed;
        var bodies = current.Detections.Where(item => item.ClassIdentifier == LiveObjectDetectionPostprocessor.Knight).ToArray();
        if (bodies.Length == 0) return displayed;
        var age = Math.Min(.15, timestamp - current.SourceTimestamp);
        var masks = new List<ObjectDetection>();
        foreach (var body in bodies)
        {
            var rect = ObjectDetectionGeometry.Rect(body.Box); var predicted = rect;
            if (previous is not null && previous.CaptureGeneration == generation)
            {
                var dt = current.SourceTimestamp - previous.SourceTimestamp;
                if (dt >= 1d / 120 && dt <= .25)
                {
                    var prior = previous.Detections.Where(item => item.ClassIdentifier == LiveObjectDetectionPostprocessor.Knight)
                        .OrderBy(item => ObjectDetectionGeometry.Distance(ObjectDetectionGeometry.Rect(item.Box), rect)).FirstOrDefault();
                    if (prior is not null)
                    {
                        var priorRect = ObjectDetectionGeometry.Rect(prior.Box);
                        var dx = rect.MidX - priorRect.MidX; var dy = rect.MidY - priorRect.MidY;
                        if (ObjectDetectionGeometry.Length(dx, dy) <= .15)
                            predicted = rect.Offset(Math.Clamp(dx / dt * age, -.08, .08), Math.Clamp(dy / dt * age, -.10, .10));
                    }
                }
            }
            masks.Add(body with { Box = ObjectDetectionGeometry.Box(rect.Union(predicted)
                .Expand(.004 + age * .025).Intersect(ObjectDetectionGeometry.Unit)) });
        }
        return displayed.Where(item => item.ClassIdentifier != LiveObjectDetectionPostprocessor.Knight).Concat(masks).ToArray();
    }
}
