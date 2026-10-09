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

    public IReadOnlyList<UserObjectDefinition> Load()
    {
        if (!File.Exists(Path)) return [];
        var document = JsonSerializer.Deserialize<UserObjectCatalogDocument>(
            File.ReadAllBytes(Path), JsonOptions)
            ?? throw new InvalidDataException("Object catalog is empty.");
        if (document.SchemaVersion != UserObjectCatalogDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported object catalog schema {document.SchemaVersion}.");
        }
        return document.Objects
            .Select(Validate)
            .DistinctBy(item => item.Identifier, StringComparer.Ordinal)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public UserObjectDefinition Add(string name, string group)
    {
        var cleanName = name.Trim();
        if (cleanName.Length == 0) throw new ArgumentException("Object name is required.", nameof(name));
        var cleanGroup = NormalizeGroup(group);
        var identifier = $"{cleanGroup}.{Slug(cleanName)}";
        var existing = Load().ToList();
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
            objects.Select(Validate).OrderBy(item => item.Identifier, StringComparer.Ordinal).ToArray());
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

    private static UserObjectDefinition Validate(UserObjectDefinition item)
    {
        var group = NormalizeGroup(item.Group);
        var name = item.Name.Trim();
        var identifier = item.Identifier.Trim().ToLowerInvariant();
        if (name.Length == 0 || identifier != $"{group}.{Slug(name)}")
        {
            throw new InvalidDataException($"Invalid object catalog entry '{item.Identifier}'.");
        }
        return new UserObjectDefinition(identifier, name, group);
    }

    private static string NormalizeGroup(string group) => group.Trim().ToLowerInvariant() switch
    {
        "game" or "gameplay" => "game",
        "enemy" or "enemies" => "enemies",
        "world" => "world",
        _ => throw new ArgumentException("Object group must be game, enemies, or world.", nameof(group))
    };

    private static string Slug(string value)
    {
        var characters = value.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var slug = string.Join('-', new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 0 ? slug : throw new ArgumentException("Object name needs a letter or number.", nameof(value));
    }
}
