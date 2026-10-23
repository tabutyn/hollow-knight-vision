using System.Text.Json;
using System.Diagnostics;
using HollowKnightVision.Windows.Core;

internal static class SceneSessionStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static void Check(bool value, string message = "Scene session store parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    public static void TestReopenPreservesObservationIDs()
    {
        WithTemporaryDirectory(root =>
        {
            var store = new SceneSessionStore(root);
            var first = store.Append(
                Image(0, 0, 0, 255), Guid.NewGuid(), Guid.NewGuid(),
                1, new AtlasPoint(0, 0), 640, []);
            var reopened = new SceneSessionStore(root);
            var second = reopened.Append(
                Image(1, 2, 3, 255), Guid.NewGuid(), Guid.NewGuid(),
                2, new AtlasPoint(0, 0), 640, []);
            Check(first.Id == 0);
            Check(second.Id == 1);
            Check(reopened.Manifest.Observations.Select(item => item.Id).SequenceEqual([0, 1]));
        });
    }

    public static void TestPNGSourcePreservesBlackAndAlphaPixels()
    {
        WithTemporaryDirectory(root =>
        {
            var store = new SceneSessionStore(root);
            var observation = store.Append(
                Image(0, 0, 0, 96), Guid.NewGuid(), Guid.NewGuid(),
                1, new AtlasPoint(0, 0), 640, []);
            Check(store.Source(observation).Pixels.AsSpan(0, 4)
                .SequenceEqual(new byte[] { 0, 0, 0, 96 }));
        });
    }

    public static void TestAtomicSceneRevisionRejectsStaleWriter()
    {
        WithTemporaryDirectory(root =>
        {
            var writer = new SceneSessionStore(root);
            var staleWriter = new SceneSessionStore(root);
            Check(writer.SaveScene([1, 2], expectedRevision: 0, newRevision: 1));
            Check(!staleWriter.SaveScene([9], expectedRevision: 0, newRevision: 2));
            Check((writer.LoadScene() ?? []).SequenceEqual(new byte[] { 1, 2 }));
            Check(writer.SceneRevision == 1);
        });
    }

    public static void TestConcurrentSceneCompareAndSwapCommitsExactlyOneWriter()
    {
        WithTemporaryDirectory(root =>
        {
            _ = new SceneSessionStore(root);
            using var ready = new CountdownEvent(2);
            using var start = new ManualResetEventSlim(false);
            var results = new bool?[2];
            var errors = new Exception?[2];
            byte[][] payloads = [[1], [2]];
            var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                try
                {
                    var store = new SceneSessionStore(root);
                    results[index] = store.SaveScene(
                        payloads[index], expectedRevision: 0, newRevision: 1);
                }
                catch (Exception exception)
                {
                    errors[index] = exception;
                }
            })).ToArray();

            Check(ready.Wait(TimeSpan.FromSeconds(1)));
            start.Set();
            Check(Task.WaitAll(tasks, TimeSpan.FromSeconds(5)));
            Check(errors.All(error => error is null),
                string.Join(Environment.NewLine, errors.Where(error => error is not null)));
            Check(results.Count(value => value == true) == 1);
            Check(results.Count(value => value == false) == 1);

            var committedIndex = Array.IndexOf(results, true);
            Check(committedIndex >= 0);
            var verifier = new SceneSessionStore(root);
            Check((verifier.LoadScene() ?? []).SequenceEqual(payloads[committedIndex]));
            Check(verifier.SceneRevision == 1);
            ExpectError(
                () => verifier.SaveScene([], expectedRevision: 1, newRevision: 1),
                SceneSessionStoreError.InvalidRevision);
        });
    }

    public static void TestMissingSourceThrowsExplicitError()
    {
        WithTemporaryDirectory(root =>
        {
            var store = new SceneSessionStore(root);
            var observation = store.Append(
                Image(0, 0, 0, 255), Guid.NewGuid(), Guid.NewGuid(),
                1, new AtlasPoint(0, 0), 640, []);
            File.Delete(Path.Combine(root, observation.ImagePath));
            var error = ExpectError(
                () => store.Source(observation),
                SceneSessionStoreError.MissingSource);
            Check(error.ImagePath == observation.ImagePath);
        });
    }

    public static void TestUnsupportedManifestAndPathTraversalAreRejected()
    {
        WithTemporaryDirectory(root =>
        {
            var unsupported = new SessionManifest(schemaVersion: 99);
            File.WriteAllBytes(
                Path.Combine(root, "manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(unsupported, JsonOptions));
            var schemaError = ExpectError(
                () => _ = new SceneSessionStore(root),
                SceneSessionStoreError.UnsupportedSchema);
            Check(schemaError.SchemaVersion == 99);

            var unsafeManifest = new SessionManifest(
                observations:
                [
                    new StoredFrameObservation(
                        0, Guid.NewGuid(), Guid.NewGuid(), 0, "../escape.png",
                        new AtlasPoint(0, 0), 1, [], 0)
                ],
                nextObservationId: 1);
            File.WriteAllBytes(
                Path.Combine(root, "manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(unsafeManifest, JsonOptions));
            var pathError = ExpectError(
                () => _ = new SceneSessionStore(root),
                SceneSessionStoreError.UnsafeImagePath);
            Check(pathError.ImagePath == "../escape.png");
        });
    }

    public static void TestTmpSymlinkRootAcceptsCanonicalEvidencePath()
    {
        WithTemporaryDirectory(workspace =>
        {
            var target = Path.Combine(workspace, "canonical");
            Directory.CreateDirectory(target);
            var alias = Path.Combine(workspace, "tmp-link");
            CreateDirectoryAlias(alias, target);
            try
            {
                var root = Path.Combine(alias, $"hkv-session-{Guid.NewGuid():D}");
                var store = new SceneSessionStore(root);
                var observation = store.Append(
                    Image(0, 0, 0, 255), Guid.NewGuid(), Guid.NewGuid(),
                    1, new AtlasPoint(0, 0), 160, []);
                Check(observation.ImagePath == "frames/00000000.png");
                Check(store.Source(observation) is not null);
                Check(Path.GetRelativePath(target, store.RootDirectory)
                    .StartsWith("hkv-session-", StringComparison.Ordinal));
            }
            finally
            {
                if (Directory.Exists(alias))
                {
                    Directory.Delete(alias);
                }
            }
        });
    }

    private static void CreateDirectoryAlias(string alias, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", alias, target }
        }) ?? throw new Exception("Could not start the Windows junction creator.");
        process.WaitForExit();
        Check(process.ExitCode == 0, "Could not create a Windows directory junction.");
    }

    private static SceneSessionStoreException ExpectError(
        Action action,
        SceneSessionStoreError expected)
    {
        try
        {
            action();
        }
        catch (SceneSessionStoreException error)
        {
            Check(error.Error == expected,
                $"Expected {expected}, got {error.Error}.");
            return error;
        }
        throw new Exception($"Expected {expected}.");
    }

    private static BgraFrame Image(byte red, byte green, byte blue, byte alpha) => new(
        1,
        1,
        4,
        [blue, green, red, alpha],
        0,
        DateTimeOffset.UnixEpoch);

    private static void WithTemporaryDirectory(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            body(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
