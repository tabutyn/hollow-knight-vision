using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace HollowKnightVision.Windows.Core;

public readonly record struct NormalizedBox(double X, double Y, double Width, double Height)
{
    public double IntersectionOverUnion(NormalizedBox other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = Width * Height + other.Width * other.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }
}

public sealed record ObjectDetection(
    string ClassIdentifier,
    NormalizedBox Box,
    float Confidence);

public static class DetectionDecoder
{
    public const float DefaultConfidenceFloor = 0.05f;
    public const double DefaultNmsOverlap = 0.45;
    public const int DefaultMaximumPerClass = 12;
    public const int DefaultMaximumDetections = 100;

    public static IReadOnlyList<ObjectDetection> Decode(
        ReadOnlySpan<float> scores,
        IReadOnlyList<long> scoreShape,
        ReadOnlySpan<float> boxes,
        IReadOnlyList<long> boxShape,
        IReadOnlyList<string> classIdentifiers,
        float confidenceFloor = DefaultConfidenceFloor,
        double nmsOverlap = DefaultNmsOverlap,
        int maximumPerClass = DefaultMaximumPerClass,
        int maximumDetections = DefaultMaximumDetections)
    {
        ArgumentNullException.ThrowIfNull(scoreShape);
        ArgumentNullException.ThrowIfNull(boxShape);
        ArgumentNullException.ThrowIfNull(classIdentifiers);
        if (scoreShape.Count != 4 || scoreShape[0] != 1
            || boxShape.Count != 4 || boxShape[0] != 1 || boxShape[1] != 4
            || scoreShape[1] != classIdentifiers.Count
            || scoreShape[2] != boxShape[2] || scoreShape[3] != boxShape[3])
        {
            throw new InvalidDataException("Detector outputs do not match [1,C,H,W] scores and [1,4,H,W] boxes.");
        }
        if (confidenceFloor is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidenceFloor));
        if (nmsOverlap is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(nmsOverlap));
        if (maximumPerClass <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPerClass));
        if (maximumDetections <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDetections));

        var classes = checked((int)scoreShape[1]);
        var height = checked((int)scoreShape[2]);
        var width = checked((int)scoreShape[3]);
        var plane = checked(height * width);
        if (scores.Length != checked(classes * plane) || boxes.Length != checked(4 * plane))
        {
            throw new InvalidDataException("Detector output storage does not match its declared shape.");
        }

        var candidates = new List<ObjectDetection>();
        for (var classIndex = 0; classIndex < classes; classIndex++)
        {
            var classOffset = classIndex * plane;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = y * width + x;
                    var confidence = scores[classOffset + cell];
                    if (!float.IsFinite(confidence) || confidence < confidenceFloor
                        || !IsLocalMaximum(scores, classOffset, height, width, x, y, confidence))
                    {
                        continue;
                    }
                    var box = new NormalizedBox(
                        boxes[cell],
                        boxes[plane + cell],
                        boxes[2 * plane + cell],
                        boxes[3 * plane + cell]);
                    if (!Valid(box)) continue;
                    candidates.Add(new ObjectDetection(
                        classIdentifiers[classIndex], box, confidence));
                }
            }
        }

        candidates.Sort((left, right) => right.Confidence.CompareTo(left.Confidence));
        var selected = new List<ObjectDetection>(Math.Min(candidates.Count, maximumDetections));
        foreach (var candidate in candidates)
        {
            var sameClass = selected.Where(value =>
                value.ClassIdentifier.Equals(candidate.ClassIdentifier, StringComparison.Ordinal));
            if (sameClass.Count() >= maximumPerClass) continue;
            if (sameClass.Any(value => candidate.Box.IntersectionOverUnion(value.Box) >= nmsOverlap))
            {
                continue;
            }
            selected.Add(candidate);
            if (selected.Count == maximumDetections) break;
        }
        return selected;
    }

    public static IReadOnlyList<ObjectDetection> DecodeRaw(
        ReadOnlySpan<float> scores,
        IReadOnlyList<long> scoreShape,
        ReadOnlySpan<float> boxes,
        IReadOnlyList<long> boxShape,
        IReadOnlyList<string> classIdentifiers,
        float confidenceFloor = DefaultConfidenceFloor,
        int maximumDetections = 400)
    {
        ArgumentNullException.ThrowIfNull(scoreShape);
        ArgumentNullException.ThrowIfNull(boxShape);
        ArgumentNullException.ThrowIfNull(classIdentifiers);
        if (scoreShape.Count != 4 || scoreShape[0] != 1
            || boxShape.Count != 4 || boxShape[0] != 1 || boxShape[1] != 4
            || scoreShape[1] != classIdentifiers.Count
            || scoreShape[2] != boxShape[2] || scoreShape[3] != boxShape[3])
        {
            throw new InvalidDataException("Detector outputs do not match [1,C,H,W] scores and [1,4,H,W] boxes.");
        }
        if (confidenceFloor is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidenceFloor));
        if (maximumDetections <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDetections));
        var classes = checked((int)scoreShape[1]);
        var height = checked((int)scoreShape[2]);
        var width = checked((int)scoreShape[3]);
        var plane = checked(height * width);
        if (scores.Length != checked(classes * plane) || boxes.Length != checked(4 * plane))
        {
            throw new InvalidDataException("Detector output storage does not match its declared shape.");
        }
        var result = new List<ObjectDetection>();
        for (var classIndex = 0; classIndex < classes; classIndex++)
        {
            for (var cell = 0; cell < plane; cell++)
            {
                var confidence = scores[classIndex * plane + cell];
                if (!float.IsFinite(confidence) || confidence < confidenceFloor) continue;
                var box = new NormalizedBox(
                    boxes[cell], boxes[plane + cell], boxes[2 * plane + cell], boxes[3 * plane + cell]);
                if (!Valid(box)) continue;
                result.Add(new ObjectDetection(classIdentifiers[classIndex], box, confidence));
            }
        }
        return result
            .OrderByDescending(item => item.Confidence)
            .Take(maximumDetections)
            .ToArray();
    }

    private static bool IsLocalMaximum(
        ReadOnlySpan<float> scores,
        int classOffset,
        int height,
        int width,
        int x,
        int y,
        float value)
    {
        for (var adjacentY = Math.Max(0, y - 1); adjacentY <= Math.Min(height - 1, y + 1); adjacentY++)
        {
            for (var adjacentX = Math.Max(0, x - 1); adjacentX <= Math.Min(width - 1, x + 1); adjacentX++)
            {
                if (scores[classOffset + adjacentY * width + adjacentX] > value) return false;
            }
        }
        return true;
    }

    private static bool Valid(NormalizedBox box) =>
        double.IsFinite(box.X) && double.IsFinite(box.Y)
        && double.IsFinite(box.Width) && double.IsFinite(box.Height)
        && box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0
        && box.X <= 1 && box.Y <= 1 && box.Width <= 1 && box.Height <= 1
        && box.X + box.Width <= 1.000_001 && box.Y + box.Height <= 1.000_001;
}

/// <summary>CPU-only inference for trainer-produced Detector.onnx runs.</summary>
public sealed class OnnxObjectDetector : IDisposable
{
    private const int InputWidth = FrameGeometry.ReferenceWidth;
    private const int InputHeight = FrameGeometry.ReferenceHeight;
    private readonly InferenceSession session;

    public OnnxObjectDetector(string runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory)) throw new ArgumentException("Run directory is required.", nameof(runDirectory));
        RunDirectory = Path.GetFullPath(runDirectory);
        var manifestPath = Path.Combine(RunDirectory, "training.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;
        if (!root.TryGetProperty("modelFormat", out var format)
            || !string.Equals(format.GetString(), "onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Training run is not an ONNX export.");
        }
        var filename = root.GetProperty("modelFilename").GetString();
        if (string.IsNullOrWhiteSpace(filename)) throw new InvalidDataException("Training manifest has no model filename.");
        ClassIdentifiers = root.GetProperty("classIdentifiers")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        if (ClassIdentifiers.Count == 0) throw new InvalidDataException("Training manifest has no classes.");
        ModelPath = Path.Combine(RunDirectory, filename);
        if (!File.Exists(ModelPath)) throw new FileNotFoundException("ONNX model is missing.", ModelPath);

        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1
        };
        session = new InferenceSession(ModelPath, options);
        if (!session.InputNames.Contains("image")
            || !session.OutputNames.Contains("scores")
            || !session.OutputNames.Contains("boxes"))
        {
            session.Dispose();
            throw new InvalidDataException("ONNX model does not expose image, scores, and boxes.");
        }
    }

    public string RunDirectory { get; }
    public string ModelPath { get; }
    public IReadOnlyList<string> ClassIdentifiers { get; }

    public IReadOnlyList<ObjectDetection> Detect(BgraFrame source, bool raw = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var frame = source.Width == InputWidth && source.Height == InputHeight
            ? source
            : CpuBgraNormalizer.Normalize(source, InputWidth, InputHeight);
        var input = ToRgbTensor(frame);
        using var inputValue = OrtValue.CreateTensorValueFromMemory(
            input,
            [1, 3, InputHeight, InputWidth]);
        var inputs = new Dictionary<string, OrtValue> { ["image"] = inputValue };
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, inputs, ["scores", "boxes"]);
        var scoreShape = outputs[0].GetTensorTypeAndShape().Shape;
        var boxShape = outputs[1].GetTensorTypeAndShape().Shape;
        return raw
            ? DetectionDecoder.DecodeRaw(
                outputs[0].GetTensorDataAsSpan<float>(), scoreShape,
                outputs[1].GetTensorDataAsSpan<float>(), boxShape,
                ClassIdentifiers)
            : DetectionDecoder.Decode(
                outputs[0].GetTensorDataAsSpan<float>(), scoreShape,
                outputs[1].GetTensorDataAsSpan<float>(), boxShape,
                ClassIdentifiers);
    }

    public void Dispose() => session.Dispose();

    private static float[] ToRgbTensor(BgraFrame frame)
    {
        var plane = checked(frame.Width * frame.Height);
        var tensor = new float[checked(3 * plane)];
        for (var y = 0; y < frame.Height; y++)
        {
            var row = frame.Row(y);
            for (var x = 0; x < frame.Width; x++)
            {
                var pixel = x * BgraFrame.BytesPerPixel;
                var index = y * frame.Width + x;
                tensor[index] = row[pixel + 2] / 255f;
                tensor[plane + index] = row[pixel + 1] / 255f;
                tensor[2 * plane + index] = row[pixel] / 255f;
            }
        }
        return tensor;
    }
}
