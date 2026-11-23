using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

/// <summary>The Windows v1 and Swift Atlas wire formats share a pose graph but use different JSON for coordinates.</summary>
internal static class LiveWorldJson
{
    private static readonly JsonSerializerOptions LegacySnapshot = LabelingExampleStore.CreateJsonOptions();
    private static readonly JsonSerializerOptions LegacyManifest = new(LegacySnapshot)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions Native = CreateNativeOptions();

    public static LiveWorldSnapshot ReadSnapshot(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var schemaVersion))
        {
            throw new JsonException("Live-world snapshot has no valid schema version.");
        }
        if (schemaVersion < 1 || schemaVersion > LiveWorldSnapshot.CurrentSchemaVersion)
            throw new LiveWorldModelException(LiveWorldModelError.UnsupportedSchema,
                $"Unsupported live-world schema {schemaVersion}.", schemaVersion);
        return root.Deserialize<LiveWorldSnapshot>(schemaVersion >= 2 ? Native : LegacySnapshot)
            ?? throw new JsonException("Live-world snapshot is empty.");
    }

    public static byte[] WriteSnapshot(LiveWorldSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, snapshot.UsesSwiftWireFormat ? Native : LegacySnapshot);

    public static SessionManifest ReadManifest(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Scene session manifest is empty.");
        // This key also distinguishes an empty Swift manifest, where there are
        // no points or rectangles whose representation can identify the format.
        var native = root.TryGetProperty("sessionID", out _);
        var manifest = root.Deserialize<SessionManifest>(native ? Native : LegacyManifest)
            ?? throw new JsonException("Scene session manifest is empty.");
        return manifest with { UsesSwiftWireFormat = native };
    }

    public static byte[] WriteManifest(SessionManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, manifest.UsesSwiftWireFormat ? Native : LegacyManifest);

    internal static StencilRect FlipMask(StencilRect mask, int sourceHeight) =>
        new(mask.X, sourceHeight - mask.Bottom, mask.Width, mask.Height);

    private static JsonSerializerOptions CreateNativeOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = new SwiftNames(),
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new PointConverter());
        options.Converters.Add(new WorldRectConverter());
        options.Converters.Add(new MaskConverter());
        return options;
    }

    private sealed class SwiftNames : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (name.Length > 3 && name.EndsWith("Ids", StringComparison.Ordinal))
                name = name[..^3] + "IDs";
            else if (name.Length > 2 && name.EndsWith("Id", StringComparison.Ordinal))
                name = name[..^2] + "ID";
            return CamelCase.ConvertName(name);
        }
    }

    private sealed class PointConverter : JsonConverter<AtlasPoint>
    {
        public override AtlasPoint Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            ReadPair(ref reader);

        public override void Write(Utf8JsonWriter writer, AtlasPoint value, JsonSerializerOptions options) =>
            WritePair(writer, value.X, value.Y);
    }

    private sealed class WorldRectConverter : JsonConverter<WorldRect>
    {
        public override WorldRect Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var (origin, size) = ReadRectangle(ref reader);
            return new(origin, new WorldSize(size.X, size.Y));
        }

        public override void Write(Utf8JsonWriter writer, WorldRect value, JsonSerializerOptions options) =>
            WriteRectangle(writer, value.X, value.Y, value.Width, value.Height);
    }

    private sealed class MaskConverter : JsonConverter<StencilRect>
    {
        public override StencilRect Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var (origin, size) = ReadRectangle(ref reader);
            return new(origin.X, origin.Y, size.X, size.Y);
        }

        public override void Write(Utf8JsonWriter writer, StencilRect value, JsonSerializerOptions options) =>
            WriteRectangle(writer, value.X, value.Y, value.Width, value.Height);
    }

    private static AtlasPoint ReadPair(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray
            || !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var x)
            || !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var y)
            || !reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Expected a two-coordinate Atlas point or size.");
        }
        return new AtlasPoint(x, y);
    }

    private static (AtlasPoint Origin, AtlasPoint Size) ReadRectangle(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
            throw new JsonException("Expected an Atlas rectangle with an origin and size.");
        var origin = ReadPair(ref reader);
        if (!reader.Read()) throw new JsonException("Atlas rectangle is missing its size.");
        var size = ReadPair(ref reader);
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("Expected an Atlas rectangle with an origin and size.");
        return (origin, size);
    }

    private static void WritePair(Utf8JsonWriter writer, double x, double y)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(x);
        writer.WriteNumberValue(y);
        writer.WriteEndArray();
    }

    private static void WriteRectangle(Utf8JsonWriter writer, double x, double y, double width, double height)
    {
        writer.WriteStartArray();
        WritePair(writer, x, y);
        WritePair(writer, width, height);
        writer.WriteEndArray();
    }
}
