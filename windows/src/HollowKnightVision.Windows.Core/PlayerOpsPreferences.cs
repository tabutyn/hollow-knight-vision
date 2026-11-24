using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed class PlayerOpsPreferences(string root)
{
    private const string Filename = "player-ops-v1.json";
    private sealed record Settings(bool? RandomizeAfterLabel);

    public bool LoadRandomizeAfterLabel()
    {
        try
        {
            return ModelStoreJson.Read<Settings>(ModelStoreJson.Child(root, Filename)).RandomizeAfterLabel ?? true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return true;
        }
    }

    public void SaveRandomizeAfterLabel(bool enabled)
    {
        var path = ModelStoreJson.Child(root, Filename);
        Directory.CreateDirectory(root);
        ModelStoreJson.Write(path, new Settings(enabled));
    }
}
