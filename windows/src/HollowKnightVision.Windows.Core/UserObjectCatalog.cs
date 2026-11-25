using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record UserObjectDefinition(string Identifier, string Name, string Group);

public sealed record UserObjectCatalogDocument(
    int SchemaVersion,
    IReadOnlyList<UserObjectDefinition> Objects)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class UserObjectCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public UserObjectCatalog(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Catalog path is required.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public IReadOnlyList<UserObjectDefinition> Load() => Load(forEdit: false);

    private IReadOnlyList<UserObjectDefinition> Load(bool forEdit)
    {
        try
        {
            var document = JsonSerializer.Deserialize<UserObjectCatalogDocument>(File.ReadAllBytes(Path), JsonOptions);
            if (document is null || document.SchemaVersion != UserObjectCatalogDocument.CurrentSchemaVersion
                || document.Objects is null)
            {
                // An optional catalog must not block startup. Editing still
                // requires a compatible document before replacing its file.
                if (!forEdit) return [];
                throw new InvalidDataException("The object catalog does not use the supported schema.");
            }
            return document.Objects.Select(Validated).OfType<UserObjectDefinition>()
                .DistinctBy(item => item.Identifier, StringComparer.Ordinal)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException
            || !forEdit && error is (IOException or UnauthorizedAccessException or JsonException))
        {
            return [];
        }
    }

    public UserObjectDefinition Add(string name, string group)
    {
        var cleanName = name.Trim();
        if (cleanName.Length == 0) throw new ArgumentException("Object name is required.", nameof(name));
        var cleanGroup = NormalizeGroup(group)
            ?? throw new ArgumentException("Object group must be game, enemies, or world.", nameof(group));
        var slug = Slug(cleanName);
        if (slug.Length == 0) throw new ArgumentException("Object name needs a letter or number.", nameof(name));
        var identifier = $"{cleanGroup}.{slug}";
        var existing = Load(forEdit: true).ToList();
        var match = existing.FirstOrDefault(item => item.Identifier == identifier);
        if (match is not null) return match;
        var added = new UserObjectDefinition(identifier, cleanName, cleanGroup);
        existing.Add(added);
        Save(existing);
        return added;
    }

    private void Save(IReadOnlyList<UserObjectDefinition> objects)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Catalog has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = $"{Path}.tmp-{Guid.NewGuid():N}";
        var document = new UserObjectCatalogDocument(
            UserObjectCatalogDocument.CurrentSchemaVersion,
            objects.OrderBy(item => item.Identifier, StringComparer.Ordinal).ToArray());
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
            File.Move(temporary, Path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static UserObjectDefinition? Validated(UserObjectDefinition? item)
    {
        var group = NormalizeGroup(item?.Group);
        var name = item?.Name?.Trim();
        var identifier = item?.Identifier?.Trim().ToLowerInvariant();
        if (group is null || string.IsNullOrEmpty(name) || identifier is null) return null;
        var slug = Slug(name);
        return slug.Length > 0 && identifier == $"{group}.{slug}" ? new(identifier, name, group) : null;
    }

    private static string? NormalizeGroup(string? group) => group?.Trim().ToLowerInvariant() switch
    {
        "game" or "gameplay" => "game",
        "enemy" or "enemies" => "enemies",
        "world" => "world",
        _ => null
    };

    private static string Slug(string value)
    {
        var characters = value.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return string.Join('-', new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
