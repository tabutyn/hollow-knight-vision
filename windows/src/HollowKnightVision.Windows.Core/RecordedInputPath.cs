using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public enum RecordedGameButton
{
    Left,
    Right,
    Down,
    Up,
    A,
    Z,
    X,
    Inventory,
    Pause
}

public enum RecordedInputTransition
{
    Pressed,
    Released
}

public sealed record RecordedInputPathEvent(
    double Offset,
    RecordedGameButton Button,
    RecordedInputTransition Transition);

public sealed record RecordedInputPath(
    int SchemaVersion,
    Guid Id,
    DateTimeOffset CreatedAt,
    double Duration,
    [property: JsonPropertyName("sourcePathID")] Guid? SourcePathId,
    int? ReplayIteration,
    RecordedGameCheckpoint? StartCheckpoint,
    IReadOnlyList<RecordedInputPathEvent> Events,
    IReadOnlyDictionary<string, string>? RuntimeMetadata)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Id == Guid.Empty
            || !double.IsFinite(Duration) || Duration < 0 || Duration > 86_400)
        {
            throw new InvalidDataException("Recorded input path header is invalid.");
        }
        if (StartCheckpoint is not null && !StartCheckpoint.HasFiniteCoordinates())
        {
            throw new InvalidDataException("Recorded input checkpoint is invalid.");
        }
        var priorOffset = 0.0;
        var held = InputButtons.None;
        for (var index = 0; index < Events.Count; index++)
        {
            var item = Events[index];
            if (!double.IsFinite(item.Offset) || item.Offset < priorOffset
                || item.Offset < 0 || item.Offset > Duration)
            {
                throw new InvalidDataException($"Recorded input event {index} has invalid timing.");
            }
            var button = item.Button.ToInputButton();
            if (item.Transition == RecordedInputTransition.Pressed)
            {
                if ((held & button) != 0)
                {
                    throw new InvalidDataException(
                        $"Recorded input event {index} presses an already-held button.");
                }
                held |= button;
            }
            else
            {
                if ((held & button) == 0)
                {
                    throw new InvalidDataException(
                        $"Recorded input event {index} releases a button that is not held.");
                }
                held &= ~button;
            }
            priorOffset = item.Offset;
        }
        if (held != InputButtons.None)
        {
            throw new InvalidDataException("Recorded input path ends with held buttons.");
        }
    }
}

public readonly record struct InputPathStateChange(double Offset, InputButtons HeldButtons);

public static class RecordedInputPathExtensions
{
    public static InputButtons ToInputButton(this RecordedGameButton button) => button switch
    {
        RecordedGameButton.Left => InputButtons.Left,
        RecordedGameButton.Right => InputButtons.Right,
        RecordedGameButton.Down => InputButtons.Down,
        RecordedGameButton.Up => InputButtons.Up,
        RecordedGameButton.A => InputButtons.ActionA,
        RecordedGameButton.Z => InputButtons.ActionZ,
        RecordedGameButton.X => InputButtons.ActionX,
        RecordedGameButton.Inventory => InputButtons.Inventory,
        RecordedGameButton.Pause => InputButtons.PauseMenu,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    public static IReadOnlyList<InputPathStateChange> StateChanges(this RecordedInputPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.Validate();
        var held = InputButtons.None;
        var result = new List<InputPathStateChange>(path.Events.Count);
        foreach (var item in path.Events)
        {
            var button = item.Button.ToInputButton();
            held = item.Transition == RecordedInputTransition.Pressed
                ? held | button
                : held & ~button;
            result.Add(new InputPathStateChange(item.Offset, held));
        }
        return result;
    }
}

public sealed class RecordedInputPathStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public RecordedInputPath Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var value = JsonSerializer.Deserialize<RecordedInputPath>(
                File.ReadAllBytes(Path.GetFullPath(path)),
                JsonOptions)
                ?? throw new InvalidDataException("Recorded input path is empty.");
            value.Validate();
            return value;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Recorded input path JSON is corrupt.", error);
        }
    }

    public void Save(RecordedInputPath path, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        path.Validate();
        AtomicFile.Write(
            Path.GetFullPath(outputPath),
            JsonSerializer.SerializeToUtf8Bytes(path, JsonOptions));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        return LabelingExampleStore.CreateJsonOptions();
    }
}
