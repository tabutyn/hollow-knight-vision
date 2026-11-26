using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record AtlasDebugPreferences
{
    private const string Filename = "atlas-debug-v1.json";

    public bool AtlasPaths { get; init; } = true;
    public bool EstimatedRoomOutlines { get; init; }
    public bool GroundTrackingPatches { get; init; } = true;

    public static AtlasDebugPreferences Load(string root)
    {
        try
        {
            return ModelStoreJson.Read<AtlasDebugPreferences>(ModelStoreJson.Child(root, Filename));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new();
        }
    }

    public void Save(string root)
    {
        var path = ModelStoreJson.Child(root, Filename);
        Directory.CreateDirectory(root);
        ModelStoreJson.Write(path, this);
    }
}
