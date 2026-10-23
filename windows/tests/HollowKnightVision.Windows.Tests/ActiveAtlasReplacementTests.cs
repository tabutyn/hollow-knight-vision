using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;
using System.Text.Json;

internal static class ActiveAtlasReplacementTests
{
    private sealed record Fixture(
        AtlasStateStore States,
        AtlasSavedState Saved,
        string ActiveDirectory,
        string SavedDirectory);

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static void TestValidatesStagingBeforeReplacingActive()
    {
        WithTemporaryDirectory(workspace =>
        {
            var fixture = CreateFixture(workspace);
            var validated = false;

            var result = VisionResetController.ReplaceActiveAtlas(
                fixture.States,
                fixture.Saved,
                fixture.ActiveDirectory,
                staging =>
                {
                    Check(Path.GetFileName(staging).StartsWith(
                            "active.staging-load-", StringComparison.Ordinal),
                        "Validation must run against a sibling staging directory.");
                    Check(File.ReadAllText(Path.Combine(
                            fixture.ActiveDirectory, "value.txt")) == "previous",
                        "The active atlas must remain untouched during staging validation.");
                    validated = true;
                    return File.ReadAllText(Path.Combine(staging, "value.txt"));
                });

            Check(validated, "The staged copy was not validated.");
            Check(result.ValidatedWorld == "replacement", "Unexpected validated world.");
            Check(File.ReadAllText(Path.Combine(
                    fixture.ActiveDirectory, "value.txt")) == "replacement",
                "The validated staged world did not become active.");
            Check(!StagingDirectories(workspace).Any(), "Successful replacement left staging data behind.");
        });
    }

    public static void TestSuccessfulSwapReturnsPreviousArchive()
    {
        WithTemporaryDirectory(workspace =>
        {
            var fixture = CreateFixture(workspace);

            var result = VisionResetController.ReplaceActiveAtlas(
                fixture.States,
                fixture.Saved,
                fixture.ActiveDirectory,
                ReadWorld);

            var archive = result.ArchivedPreviousAtlasDirectory;
            Check(archive is not null && Directory.Exists(archive),
                "The successful swap did not return its previous active archive.");
            Check(File.ReadAllText(Path.Combine(archive!, "value.txt")) == "previous",
                "The returned archive does not contain the previous active atlas.");
            Check(File.ReadAllText(Path.Combine(
                    fixture.ActiveDirectory, "value.txt")) == "replacement",
                "The saved atlas did not become active.");

            File.WriteAllText(Path.Combine(fixture.ActiveDirectory, "value.txt"), "changed active");
            Check(File.ReadAllText(Path.Combine(fixture.SavedDirectory, "value.txt")) == "replacement",
                "The named saved state became the active writer.");
        });
    }

    public static void TestPipelineLoadRestoresSavedSnapshotAndLeavesStateImmutable()
    {
        WithTemporaryDirectory(workspace =>
        {
            const int tileSize = 32;
            var active = Path.Combine(workspace, "active");
            var states = new AtlasStateStore(Path.Combine(workspace, "states"));
            var previousMetadata = new VisionResetController.PersistedAtlasMetadata(
                3,
                new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                tileSize,
                8);
            var previousCoordinate = new AtlasTileCoordinate(-1, 2);
            var previousTile = SolidTile(tileSize, blue: 11, green: 22, red: 33);
            WritePersistedAtlas(active, previousMetadata, previousCoordinate, previousTile);
            Directory.CreateDirectory(Path.Combine(active, "evidence"));
            File.WriteAllBytes(Path.Combine(active, "evidence", "previous.bin"), [1, 3, 5, 7]);
            var previousTree = SnapshotTree(active);

            var savedMetadata = new VisionResetController.PersistedAtlasMetadata(
                17,
                new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
                tileSize,
                12.5);
            var savedCoordinate = new AtlasTileCoordinate(4, -3);
            var savedTile = SolidTile(tileSize, blue: 77, green: 88, red: 99);
            var saved = states.Save("Saved Snapshot", destination =>
            {
                WritePersistedAtlas(destination, savedMetadata, savedCoordinate, savedTile);
                Directory.CreateDirectory(Path.Combine(destination, "evidence"));
                File.WriteAllBytes(Path.Combine(destination, "evidence", "saved.bin"), [2, 4, 6, 8]);
            });
            var savedDirectory = states.WorldPath(saved);
            var savedTree = SnapshotTree(savedDirectory);

            var loaded = VisionResetController.ReplaceActiveAtlas(
                states,
                saved,
                active,
                VisionResetController.ReadPersistedAtlas);

            CheckAtlas(
                loaded.ValidatedWorld,
                savedMetadata,
                savedCoordinate,
                savedTile,
                "validated saved snapshot");
            CheckAtlas(
                VisionResetController.ReadPersistedAtlas(active),
                savedMetadata,
                savedCoordinate,
                savedTile,
                "active restored snapshot");
            CheckTree(savedTree, SnapshotTree(savedDirectory),
                "Loading modified the immutable saved source.");
            CheckTree(savedTree, SnapshotTree(active),
                "The active tree is not an exact copy of the saved source.");

            var archive = loaded.ArchivedPreviousAtlasDirectory;
            Check(archive is not null && Directory.Exists(archive),
                "Loading did not safely archive the previous active atlas.");
            CheckTree(previousTree, SnapshotTree(archive!),
                "The previous active archive changed during replacement.");
            CheckAtlas(
                VisionResetController.ReadPersistedAtlas(archive!),
                previousMetadata,
                previousCoordinate,
                previousTile,
                "archived previous snapshot");

            var recovered = VisionResetController.ImportPreviousActiveAtlas(
                states,
                archive!,
                "Recovered Previous Active");
            var recoveredDirectory = states.WorldPath(recovered);
            CheckTree(previousTree, SnapshotTree(recoveredDirectory),
                "The archived previous atlas was not recoverable without mutation.");
            CheckAtlas(
                VisionResetController.ReadPersistedAtlas(recoveredDirectory),
                previousMetadata,
                previousCoordinate,
                previousTile,
                "recovered previous snapshot");
        });
    }

    public static void TestCopyFailurePreservesActive()
    {
        WithTemporaryDirectory(workspace =>
        {
            var fixture = CreateFixture(workspace);
            var error = Expect<IOException>(() => VisionResetController.ReplaceActiveAtlas(
                fixture.States,
                fixture.Saved,
                fixture.ActiveDirectory,
                ReadWorld,
                copyDirectory: (_, staging) =>
                {
                    Directory.CreateDirectory(staging);
                    File.WriteAllText(Path.Combine(staging, "partial.txt"), "partial");
                    throw new IOException("copy failed");
                }));

            Check(error.Message == "copy failed", "The copy failure was not preserved.");
            CheckActiveIsPrevious(fixture);
            Check(!StagingDirectories(workspace).Any(), "Failed copy left staging data behind.");
            Check(!ArchiveDirectories(workspace).Any(), "Copy failure moved the active atlas.");
        });
    }

    public static void TestValidationFailurePreservesActive()
    {
        WithTemporaryDirectory(workspace =>
        {
            var fixture = CreateFixture(workspace);
            var error = Expect<InvalidDataException>(() =>
                VisionResetController.ReplaceActiveAtlas<string>(
                    fixture.States,
                    fixture.Saved,
                    fixture.ActiveDirectory,
                    _ => throw new InvalidDataException("invalid staged world")));

            Check(error.Message == "invalid staged world", "The validation failure was not preserved.");
            CheckActiveIsPrevious(fixture);
            Check(!StagingDirectories(workspace).Any(), "Failed validation left staging data behind.");
            Check(!ArchiveDirectories(workspace).Any(), "Validation failure moved the active atlas.");
        });
    }

    public static void TestSwapFailureRollsBackActive()
    {
        WithTemporaryDirectory(workspace =>
        {
            var fixture = CreateFixture(workspace);
            var error = Expect<IOException>(() => VisionResetController.ReplaceActiveAtlas(
                fixture.States,
                fixture.Saved,
                fixture.ActiveDirectory,
                ReadWorld,
                moveDirectory: (source, destination) =>
                {
                    if (IsStaging(source)) throw new IOException("swap failed");
                    Directory.Move(source, destination);
                }));

            Check(error.Message == "swap failed", "The swap failure was not preserved.");
            CheckActiveIsPrevious(fixture);
            Check(!StagingDirectories(workspace).Any(), "Failed swap left staging data behind.");
            Check(!ArchiveDirectories(workspace).Any(), "Successful rollback left an archive behind.");
        });
    }

    public static void TestRollbackFailureReportsRecoverableArchive()
    {
        WithTemporaryDirectory(workspace =>
        {
            var fixture = CreateFixture(workspace);
            var error = Expect<VisionResetController.ActiveAtlasReplacementException>(() =>
                VisionResetController.ReplaceActiveAtlas(
                    fixture.States,
                    fixture.Saved,
                    fixture.ActiveDirectory,
                    ReadWorld,
                    moveDirectory: (source, destination) =>
                    {
                        if (IsStaging(source)) throw new IOException("swap failed");
                        if (IsArchive(source)) throw new IOException("rollback failed");
                        Directory.Move(source, destination);
                    }));

            Check(error.Error == VisionResetController.ActiveAtlasReplacementError.LoadRollbackFailed,
                "Rollback failure did not use the explicit error path.");
            Check(Directory.Exists(error.ArchivedPreviousAtlasDirectory),
                "Rollback failure did not preserve the recoverable previous archive.");
            Check(File.ReadAllText(Path.Combine(
                    error.ArchivedPreviousAtlasDirectory, "value.txt")) == "previous",
                "The reported recovery archive does not contain the previous atlas.");
            Check(!Directory.Exists(fixture.ActiveDirectory),
                "A failed rollback unexpectedly reported an active atlas.");
            Check(error.InnerException is AggregateException aggregate
                  && aggregate.InnerExceptions.Count == 2,
                "Rollback failure did not retain both swap and rollback errors.");
            Check(!StagingDirectories(workspace).Any(),
                "Rollback failure left the replacement staging directory behind.");
        });
    }

    private static Fixture CreateFixture(string workspace)
    {
        var active = Path.Combine(workspace, "active");
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(active, "value.txt"), "previous");
        var states = new AtlasStateStore(Path.Combine(workspace, "states"));
        var saved = states.Save("Saved", destination =>
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "value.txt"), "replacement");
        });
        return new Fixture(states, saved, active, states.WorldPath(saved));
    }

    private static string ReadWorld(string directory) =>
        File.ReadAllText(Path.Combine(directory, "value.txt"));

    private static void WritePersistedAtlas(
        string directory,
        VisionResetController.PersistedAtlasMetadata metadata,
        AtlasTileCoordinate coordinate,
        BgraFrame tile)
    {
        Directory.CreateDirectory(directory);
        BmpWriter.Write(tile, Path.Combine(
            directory,
            $"tile_{coordinate.X}_{coordinate.Y}.bmp"));
        File.WriteAllBytes(
            Path.Combine(directory, "atlas.json"),
            JsonSerializer.SerializeToUtf8Bytes(metadata));
    }

    private static BgraFrame SolidTile(int size, byte blue, byte green, byte red)
    {
        var pixels = new byte[checked(size * size * BgraFrame.BytesPerPixel)];
        for (var index = 0; index < size * size; index++)
        {
            var offset = index * BgraFrame.BytesPerPixel;
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(
            size,
            size,
            checked(size * BgraFrame.BytesPerPixel),
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static IReadOnlyDictionary<string, byte[]> SnapshotTree(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(directory, path),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static void CheckTree(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual,
        string message)
    {
        Check(expected.Keys.SequenceEqual(actual.Keys, StringComparer.Ordinal), message);
        foreach (var item in expected)
        {
            Check(actual.TryGetValue(item.Key, out var bytes)
                  && item.Value.SequenceEqual(bytes), message);
        }
    }

    private static void CheckAtlas(
        VisionResetController.ValidatedPersistedAtlas actual,
        VisionResetController.PersistedAtlasMetadata expectedMetadata,
        AtlasTileCoordinate expectedCoordinate,
        BgraFrame expectedTile,
        string description)
    {
        Check(actual.Metadata == expectedMetadata,
            $"The {description} metadata was not restored exactly.");
        Check(actual.Mosaic.AcceptedFrameCount == expectedMetadata.RegistrationCount,
            $"The {description} registration count was not restored.");
        var tiles = actual.Mosaic.Snapshot();
        Check(tiles.Count == 1 && tiles[0].Coordinate == expectedCoordinate,
            $"The {description} tile coordinate was not restored.");
        Check(tiles[0].Frame.Pixels.SequenceEqual(expectedTile.Pixels),
            $"The {description} tile bytes were not restored.");
    }

    private static void CheckActiveIsPrevious(Fixture fixture)
    {
        Check(Directory.Exists(fixture.ActiveDirectory), "The previous active atlas was lost.");
        Check(File.ReadAllText(Path.Combine(
                fixture.ActiveDirectory, "value.txt")) == "previous",
            "The previous active atlas was not preserved exactly.");
        Check(File.ReadAllText(Path.Combine(
                fixture.SavedDirectory, "value.txt")) == "replacement",
            "A failed replacement modified the named saved state.");
    }

    private static bool IsStaging(string path) => Path.GetFileName(path).StartsWith(
        "active.staging-load-", StringComparison.Ordinal);

    private static bool IsArchive(string path) => Path.GetFileName(path).StartsWith(
        "active.archive-load-", StringComparison.Ordinal);

    private static IEnumerable<string> StagingDirectories(string workspace) =>
        Directory.EnumerateDirectories(workspace, "active.staging-load-*");

    private static IEnumerable<string> ArchiveDirectories(string workspace) =>
        Directory.EnumerateDirectories(workspace, "active.archive-load-*");

    private static TException Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void WithTemporaryDirectory(Action<string> body)
    {
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(workspace);
        try
        {
            body(workspace);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
