using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

[Flags]
public enum InputButtons : ushort
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Down = 1 << 2,
    Up = 1 << 3,
    ActionA = 1 << 4,
    ActionZ = 1 << 5,
    ActionX = 1 << 6,
    Inventory = 1 << 7,
    PauseMenu = 1 << 8
}

public static class ReceiverProtocol
{
    public const int Port = 36752;
    public const int InputVersion = 1;
    public const int ControlVersion = 2;
    public const int MaximumLineLength = 4096;

    public const string PauseCapability = "pause-lease-v1";
    public const string MenuShortcutsCapability = "menu-shortcuts-v1";
    public const string PointerCapability = "pointer-events-v1";
    public const string PlayerCheckpointCapability = "player-checkpoint-v1";
    public const string PlayerPosePlaybackCapability = "player-pose-playback-v1";
    public const string GroundTruthCapability = "ground-truth-telemetry-v1";
    public const string PlayerOpsCapability = "player-ops-v1";
}

public sealed record ReceiverCapabilityRequest(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    bool? RenderFrameMarker)
{
    public static ReceiverCapabilityRequest Create(Guid sessionId, bool renderFrameMarker = false) =>
        new(ReceiverProtocol.ControlVersion, "hello", sessionId, renderFrameMarker ? true : null);
}

public sealed record ReceiverCapabilitiesAcknowledgement(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    IReadOnlyList<string> Capabilities,
    int PauseLeaseMilliseconds);

public sealed record ReceiverInputSnapshot(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    ulong Sequence,
    bool Enabled,
    InputButtons HeldButtons)
{
    public static ReceiverInputSnapshot Create(
        Guid sessionId,
        ulong sequence,
        bool enabled,
        InputButtons heldButtons) =>
        new(
            ReceiverProtocol.InputVersion,
            "state",
            sessionId,
            sequence,
            enabled,
            enabled ? heldButtons : InputButtons.None);
}

public sealed record ReceiverInputAcknowledgement(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    ulong Sequence,
    InputButtons AppliedButtons,
    bool Enabled);

public sealed record ReceiverPointerCommand(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    ulong Sequence,
    string Kind,
    double NormalizedX,
    double NormalizedY,
    int ClickCount)
{
    private static readonly HashSet<string> Kinds =
    [
        "moved", "leftDown", "leftDragged", "leftUp", "exited"
    ];

    public static ReceiverPointerCommand Create(
        Guid sessionId,
        ulong sequence,
        string kind,
        double normalizedX,
        double normalizedY,
        int clickCount = 1)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind != "exited"
            && (!double.IsFinite(normalizedX) || normalizedX is < 0 or > 1
                || !double.IsFinite(normalizedY) || normalizedY is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedX));
        }
        return new ReceiverPointerCommand(
            ReceiverProtocol.ControlVersion,
            "pointer",
            sessionId,
            sequence,
            kind,
            kind == "exited" ? 0 : normalizedX,
            kind == "exited" ? 0 : normalizedY,
            Math.Max(1, clickCount));
    }
}

public sealed record RecordedEnemyCheckpoint(
    string Name,
    double X,
    double Y,
    double Z,
    int Hp,
    bool Active);

public sealed record RecordedGameCheckpoint(
    string SceneName,
    string? EntryGateName,
    double HeroX,
    double HeroY,
    double HeroZ,
    double VelocityX,
    double VelocityY,
    bool FacingRight,
    bool Grounded,
    double CameraX,
    double CameraY,
    double CameraZ,
    double CameraTargetX,
    double CameraTargetY,
    double CameraTargetZ,
    IReadOnlyList<RecordedEnemyCheckpoint>? Enemies,
    bool? IsFirstGame,
    bool? EnteredTutorialFirstTime,
    bool? VisitedDirtmouth,
    bool? VisitedCrossroads,
    bool? OpenedTown,
    bool? OpenedCrossroads,
    IReadOnlyList<string>? ScenesVisited)
{
    public bool HasFiniteCoordinates()
    {
        var values = new[]
        {
            HeroX, HeroY, HeroZ, VelocityX, VelocityY,
            CameraX, CameraY, CameraZ,
            CameraTargetX, CameraTargetY, CameraTargetZ
        };
        return !string.IsNullOrWhiteSpace(SceneName)
            && values.All(value => double.IsFinite(value) && Math.Abs(value) <= 1_000_000)
            && (Enemies?.All(enemy => !string.IsNullOrWhiteSpace(enemy.Name)
                && double.IsFinite(enemy.X) && double.IsFinite(enemy.Y)
                && double.IsFinite(enemy.Z) && enemy.Hp >= 0) ?? true);
    }
}

public sealed record ReceiverCheckpointCommand(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("commandID")] Guid CommandId,
    RecordedGameCheckpoint? Checkpoint)
{
    public static ReceiverCheckpointCommand Capture(Guid sessionId, Guid? commandId = null) =>
        new(ReceiverProtocol.ControlVersion, "captureCheckpoint", sessionId,
            commandId ?? Guid.NewGuid(), null);

    public static ReceiverCheckpointCommand Restore(
        Guid sessionId,
        RecordedGameCheckpoint checkpoint,
        Guid? commandId = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!checkpoint.HasFiniteCoordinates())
        {
            throw new ArgumentException("Checkpoint coordinates are invalid.", nameof(checkpoint));
        }
        return new ReceiverCheckpointCommand(
            ReceiverProtocol.ControlVersion,
            "restoreCheckpoint",
            sessionId,
            commandId ?? Guid.NewGuid(),
            checkpoint);
    }
}

public sealed record ReceiverCheckpointAcknowledgement(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("commandID")] Guid CommandId,
    bool Accepted,
    RecordedGameCheckpoint? Checkpoint,
    string? Failure);

public sealed record ReceiverGroundTruthSample(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    ulong Sequence,
    long UnityFrame,
    double UnityRealtime,
    string SceneName,
    bool HeroAvailable,
    double HeroX,
    double HeroY,
    double HeroZ,
    double VelocityX,
    double VelocityY,
    bool FacingRight,
    bool Grounded,
    bool CameraAvailable,
    double CameraX,
    double CameraY,
    double CameraZ,
    double CameraTargetX,
    double CameraTargetY,
    double CameraTargetZ,
    double OrthographicSize,
    double? PixelsPerWorldUnitX,
    double? PixelsPerWorldUnitY,
    double? HeroScreenX,
    double? HeroScreenY,
    int? ProjectionPixelWidth,
    int? ProjectionPixelHeight,
    int ScreenWidth,
    int ScreenHeight)
{
    public bool HasFiniteCoordinates()
    {
        var values = new[]
        {
            UnityRealtime, HeroX, HeroY, HeroZ, VelocityX, VelocityY,
            CameraX, CameraY, CameraZ, CameraTargetX, CameraTargetY,
            CameraTargetZ, OrthographicSize,
            PixelsPerWorldUnitX ?? 0, PixelsPerWorldUnitY ?? 0,
            HeroScreenX ?? 0, HeroScreenY ?? 0
        };
        return values.All(value => double.IsFinite(value) && Math.Abs(value) <= 1_000_000)
            && UnityFrame >= 0
            && OrthographicSize >= 0
            && ScreenWidth >= 0
            && ScreenHeight >= 0
            && (ProjectionPixelWidth ?? 0) >= 0
            && (ProjectionPixelHeight ?? 0) >= 0;
    }

    public double? PixelsPerWorldUnit(int frameHeight)
    {
        if (!CameraAvailable || frameHeight <= 0) return null;
        if (PixelsPerWorldUnitY is > 0 && ProjectionPixelHeight is > 0)
        {
            return PixelsPerWorldUnitY.Value * frameHeight / ProjectionPixelHeight.Value;
        }
        return OrthographicSize > 0 ? frameHeight / (2 * OrthographicSize) : null;
    }
}

public sealed record PlayerTestState(
    int MaxHealth,
    int Health,
    int LifebloodSeed,
    int Mana,
    int ExtraManaSlots,
    int Geo,
    bool Invincible)
{
    public static PlayerTestState Default { get; } = new(5, 5, 0, 99, 0, 0, true);

    public PlayerTestState Normalized()
    {
        var maxHealth = Math.Clamp(MaxHealth, 1, 9);
        var slots = Math.Clamp(ExtraManaSlots, 0, 3);
        return this with
        {
            MaxHealth = maxHealth,
            Health = Math.Clamp(Health, 1, maxHealth),
            LifebloodSeed = Math.Clamp(LifebloodSeed, 0, 9),
            Mana = Math.Clamp(Mana, 0, 99 + slots * 33),
            ExtraManaSlots = slots,
            Geo = Math.Clamp(Geo, 0, 9_999_999)
        };
    }
}

public sealed record ReceiverPlayerOpsCommand(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("commandID")] Guid CommandId,
    string Operation,
    PlayerTestState? State)
{
    public static ReceiverPlayerOpsCommand Query(Guid sessionId) =>
        new(ReceiverProtocol.ControlVersion, "playerOps", sessionId, Guid.NewGuid(), "query", null);

    public static ReceiverPlayerOpsCommand Apply(Guid sessionId, PlayerTestState state) =>
        new(ReceiverProtocol.ControlVersion, "playerOps", sessionId, Guid.NewGuid(), "apply", state.Normalized());

    public static ReceiverPlayerOpsCommand RestoreEnemies(Guid sessionId) =>
        new(ReceiverProtocol.ControlVersion, "playerOps", sessionId, Guid.NewGuid(), "restoreEnemies", null);
}

public sealed record ReceiverPlayerOpsAcknowledgement(
    int Version,
    string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("commandID")] Guid CommandId,
    bool Accepted,
    PlayerTestState? State,
    int? EnemiesRestored,
    string? Failure);

public static class ReceiverWireCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string EncodeLine<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value, Options);
        if (json.Length > ReceiverProtocol.MaximumLineLength)
        {
            throw new InvalidDataException("Receiver message exceeds the protocol line limit.");
        }
        return json + "\n";
    }

    public static T DecodeLine<T>(string line)
    {
        var json = ValidateLine(line);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException("Receiver message decoded to null.");
    }

    public static string MessageType(string line)
    {
        var json = ValidateLine(line);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Receiver message has no string type.");
        }
        return type.GetString()!;
    }

    private static string ValidateLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var json = line.EndsWith('\n') ? line[..^1] : line;
        if (json.EndsWith('\r')) json = json[..^1];
        if (json.Length == 0 || json.Length > ReceiverProtocol.MaximumLineLength
            || json.Contains('\n') || json.Contains('\r'))
        {
            throw new InvalidDataException("Receiver message is not one bounded JSON line.");
        }
        return json;
    }
}
