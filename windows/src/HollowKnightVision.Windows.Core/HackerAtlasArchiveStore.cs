using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record HackerAtlasArchive(
    IReadOnlyList<HackerAtlasArchivedRoom> Rooms,
    IReadOnlyList<HackerRoomConnection> Connections,
    IReadOnlyDictionary<string, AtlasPoint> RoomOffsets,
    long RegistrationCount,
    DateTimeOffset CreatedAt)
{
    internal HackerAtlasArchive RetainRoomsWithPixels()
    {
        var retained = Rooms.Where(room => room.Tiles.Count > 0).ToArray();
        var ids = retained.Select(room => room.Id).ToHashSet();
        var scenes = retained.Select(room => room.SceneName).ToHashSet(StringComparer.Ordinal);
        return this with
        {
            Rooms = retained,
            Connections = Connections.Where(edge => ids.Contains(edge.FromRoomId) && ids.Contains(edge.ToRoomId)).ToArray(),
            RoomOffsets = RoomOffsets.Where(offset => scenes.Contains(offset.Key))
                .ToDictionary(offset => offset.Key, offset => offset.Value, StringComparer.Ordinal)
        };
    }
}

public sealed record HackerAtlasArchivedRoom(
    Guid Id,
    string SceneName,
    AtlasPoint Position,
    AtlasPoint CameraAnchor,
    HackerTerrainProjection TerrainProjection,
    StencilRect ContentBounds,
    IReadOnlyList<HackerAtlasArchivedTile> Tiles);

public sealed record HackerAtlasArchivedTile(AtlasTileCoordinate Coordinate, BgraFrame Image);

/// <summary>Stores the composited pixels and room coordinates for one playthrough.</summary>
public sealed class HackerAtlasArchiveStore(string root)
{
    private const int Version = 1;
    private const int TileSize = LiveTiledAtlas.TileSize;
    private const int TileStride = TileSize * BgraFrame.BytesPerPixel;
    private const int TileBytes = TileStride * TileSize;

    private sealed record Manifest(
        int Version,
        SaveProfileAtlas? Profile,
        Room[] Rooms,
        HackerRoomConnection[] Connections,
        Dictionary<string, AtlasPoint>? RoomOffsets,
        long RegistrationCount,
        DateTimeOffset CreatedAt);

    private sealed record Room(
        Guid Id,
        string SceneName,
        AtlasPoint Position,
        [property: JsonRequired] AtlasPoint CameraAnchor,
        HackerTerrainProjection TerrainProjection,
        StencilRect ContentBounds,
        AtlasTileCoordinate[] Tiles);

    private readonly AtlasStateStore namedStates = new(Path.Combine(root, "states"));
    private const string NamedArchiveFilename = "hacker.zip";

    public IReadOnlyList<AtlasSavedState> ListStates() => namedStates.List();
    public long NamedBytes(AtlasSavedState state) => AtlasStateStore.FileByteCount(namedStates.WorldPath(state));
    public long ActiveBytes(SaveProfileAtlas? profile) =>
        new FileInfo(ArchivePath(profile)) is { Exists: true } file ? file.Length : 0;

    public AtlasSavedState SaveState(string name, SaveProfileAtlas? profile, HackerAtlasArchive archive) =>
        namedStates.Save(name, world =>
        {
            Directory.CreateDirectory(world);
            WriteArchive(Path.Combine(world, NamedArchiveFilename), profile, archive);
            profile?.Write(world);
        });

    public HackerAtlasArchive LoadState(AtlasSavedState state)
    {
        var world = namedStates.WorldPath(state);
        if (SaveProfileAtlas.Read(world) != state.Profile)
            throw new InvalidDataException("The archived Hacker Atlas belongs to a different playthrough.");
        return ReadArchive(Path.Combine(world, NamedArchiveFilename), state.Profile);
    }

    public void DeleteState(AtlasSavedState state) => namedStates.Delete(state);

    public HackerAtlasArchive? Read(SaveProfileAtlas? profile)
    {
        try { return ReadArchive(ArchivePath(profile), profile); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static HackerAtlasArchive ReadArchive(string path, SaveProfileAtlas? profile)
    {
        using var stream = File.OpenRead(path);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var entries = zip.Entries.ToLookup(entry => entry.FullName, StringComparer.Ordinal);
            using var metadata = Entry(entries, "atlas.json").Open();
            var manifest = JsonSerializer.Deserialize<Manifest>(metadata, ModelStoreJson.Options)
                ?? throw new InvalidDataException("Missing Hacker Atlas manifest.");
            Validate(manifest, profile);
            var expectedEntries = manifest.Rooms.Sum(room => (long)room.Tiles.Length) + 1;
            if (expectedEntries != zip.Entries.Count)
                throw new InvalidDataException("Unexpected tiles in the Hacker Atlas archive.");

            var rooms = new List<HackerAtlasArchivedRoom>(manifest.Rooms.Length);
            foreach (var room in manifest.Rooms)
            {
                var tiles = new List<HackerAtlasArchivedTile>(room.Tiles.Length);
                foreach (var coordinate in room.Tiles)
                {
                    var entry = Entry(entries, TilePath(room.Id, coordinate));
                    if (entry.Length != TileBytes)
                        throw new InvalidDataException("Invalid Hacker Atlas tile size.");
                    var pixels = new byte[TileBytes];
                    using var tile = entry.Open();
                    tile.ReadExactly(pixels);
                    if (tile.ReadByte() != -1)
                        throw new InvalidDataException("Unexpected data in the Hacker Atlas tile.");
                    tiles.Add(new(coordinate, new BgraFrame(
                        TileSize, TileSize, TileStride, pixels, 0, DateTimeOffset.UnixEpoch)));
                }
                rooms.Add(new(room.Id, room.SceneName, room.Position, room.CameraAnchor, room.TerrainProjection, room.ContentBounds, tiles));
            }
            // Older Windows versions could save a room before the first tile arrived.
            return new HackerAtlasArchive(rooms, manifest.Connections, manifest.RoomOffsets ?? new(),
                manifest.RegistrationCount, manifest.CreatedAt).RetainRoomsWithPixels();
        }
    }

    public void Write(SaveProfileAtlas? profile, HackerAtlasArchive archive) =>
        WriteArchive(ArchivePath(profile), profile, archive);

    public AtlasSavedState? Replace(SaveProfileAtlas? profile, HackerAtlasArchive? previous,
        HackerAtlasArchive next, string? backupName)
    {
        var backup = previous is not null && backupName is not null && (profile is not null || previous.Rooms.Count > 0)
            ? SaveState(backupName, profile, previous) : null;
        Write(profile, next);
        return backup;
    }

    private static void WriteArchive(string destination, SaveProfileAtlas? profile, HackerAtlasArchive archive)
    {
        var manifest = new Manifest(Version, profile,
            archive.Rooms.Select(room => new Room(room.Id, room.SceneName, room.Position, room.CameraAnchor,
                room.TerrainProjection, room.ContentBounds, room.Tiles.Select(tile => tile.Coordinate).ToArray())).ToArray(),
            archive.Connections.ToArray(),
            new Dictionary<string, AtlasPoint>(archive.RoomOffsets, StringComparer.Ordinal),
            archive.RegistrationCount, archive.CreatedAt);
        Validate(manifest, profile);
        if (manifest.Rooms.Any(room => room.Tiles.Length == 0))
            throw new InvalidDataException("A Hacker Atlas archive cannot save a room without registered tiles.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var metadata = zip.CreateEntry("atlas.json").Open())
                        JsonSerializer.Serialize(metadata, manifest, ModelStoreJson.Options);
                    foreach (var room in archive.Rooms)
                    foreach (var tile in room.Tiles)
                    {
                        if (tile.Image.Width != TileSize || tile.Image.Height != TileSize)
                            throw new InvalidDataException("Invalid Hacker Atlas tile dimensions.");
                        using var output = zip.CreateEntry(TilePath(room.Id, tile.Coordinate), CompressionLevel.Fastest).Open();
                        for (var y = 0; y < TileSize; y++) output.Write(tile.Image.Row(y));
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string ArchivePath(SaveProfileAtlas? profile)
    {
        if (profile is { IsValid: false }) throw new InvalidDataException("Invalid Hacker Atlas profile.");
        var name = profile is null ? "unassigned" : profile.Id.ToString("N");
        return Path.Combine(root, name + ".zip");
    }

    private static ZipArchiveEntry Entry(ILookup<string, ZipArchiveEntry> entries, string name)
    {
        var group = entries[name].Take(2).ToArray();
        return group.Length == 1 ? group[0] : throw new InvalidDataException("Missing or repeated Hacker Atlas entry.");
    }

    private static string TilePath(Guid room, AtlasTileCoordinate tile) =>
        FormattableString.Invariant($"{room:N}/{tile.X}_{tile.Y}.bgra");

    private static void Validate(Manifest manifest, SaveProfileAtlas? profile)
    {
        if (manifest.Version != Version || manifest.Profile != profile || manifest.RegistrationCount < 0
            || manifest.Rooms is null || manifest.Connections is null)
            throw new InvalidDataException("Invalid Hacker Atlas manifest or save profile.");

        var roomIds = new HashSet<Guid>();
        var sceneNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in manifest.Rooms)
        {
            if (room is null || room.Id == Guid.Empty || !roomIds.Add(room.Id)
                || string.IsNullOrWhiteSpace(room.SceneName) || !sceneNames.Add(room.SceneName)
                || !Finite(room.Position) || !Finite(room.CameraAnchor) || room.TerrainProjection is not { IsValid: true }
                || !room.ContentBounds.IsFinite || room.ContentBounds.Width < 0 || room.ContentBounds.Height < 0
                || room.Tiles is null || room.Tiles.Distinct().Count() != room.Tiles.Length
                || room.Tiles.Any(tile => !ValidCoordinate(tile.X) || !ValidCoordinate(tile.Y)))
                throw new InvalidDataException("Invalid room geometry in the Hacker Atlas archive.");
        }
        if (manifest.RoomOffsets?.Any(offset => string.IsNullOrWhiteSpace(offset.Key) || !Finite(offset.Value)) == true)
            throw new InvalidDataException("Invalid room position in the Hacker Atlas archive.");
        var connectionIds = new HashSet<Guid>();
        foreach (var connection in manifest.Connections)
        {
            if (connection is null || connection.Id == Guid.Empty || !connectionIds.Add(connection.Id)
                || connection.FromRoomId == connection.ToRoomId
                || !roomIds.Contains(connection.FromRoomId) || !roomIds.Contains(connection.ToRoomId)
                || !Enum.IsDefined(connection.FromSide) || !Enum.IsDefined(connection.ToSide)
                || connection.HasGroundedAlignment && (!connection.FromSide.IsHorizontal() || connection.ToSide != connection.FromSide.Opposite())
                || !double.IsFinite(connection.FromCoordinate) || !double.IsFinite(connection.ToCoordinate))
                throw new InvalidDataException("Invalid doorway in the Hacker Atlas archive.");
        }
    }

    private static bool ValidCoordinate(int value) =>
        value is >= int.MinValue / TileSize and < int.MaxValue / TileSize;

    private static bool Finite(AtlasPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
