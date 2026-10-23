using HollowKnightVision.Windows.Core;

internal static class AtlasStateStoreParityTests
{
    private static void Check(bool value, string message = "Atlas state-store parity assertion failed.")
    { if (!value) throw new Exception(message); }

    public static void TestSaveListLoadAndPermanentDelete()
    {
        WithTemporaryDirectory(workspace =>
        {
            var activeDirectory = Path.Combine(workspace, "active");
            Directory.CreateDirectory(activeDirectory);
            var activeStore = new LiveWorldSnapshotStore(activeDirectory);
            var store = new AtlasStateStore(Path.Combine(workspace, "states"));

            var saved = store.Save("First atlas", destination =>
                CopyDirectory(activeStore.RootDirectory, destination));
            Check(store.List().Select(state => state.Id).SequenceEqual([saved.Id]));
            Check(new LiveWorldSnapshotStore(store.WorldPath(saved)).Load().MapRevision == 0);

            store.Delete(saved);
            Check(store.List().Count == 0);
            Check(!Directory.Exists(Path.Combine(store.RootDirectory, saved.Id.ToString("D"))));
        });
    }

    public static void TestNewArchiveCanBecomeLoadableWithoutDuplicatingWorld()
    {
        WithTemporaryDirectory(workspace =>
        {
            var activeDirectory = Path.Combine(workspace, "active");
            Directory.CreateDirectory(activeDirectory);
            var (archiveDirectory, freshStore) = ArchiveAndReset(activeDirectory);
            var states = new AtlasStateStore(Path.Combine(workspace, "states"));

            var imported = states.ImportArchive(archiveDirectory, "Before New");
            Check(!Directory.Exists(archiveDirectory));
            Check(states.List().Select(state => state.Id).SequenceEqual([imported.Id]));
            Check(new LiveWorldSnapshotStore(states.WorldPath(imported)).Load().MapRevision == 0);
            Check(freshStore.Load().MapRevision == 0);
        });
    }

    public static void TestDeletingAutoSaveArchivePermanentlyRemovesIt()
    {
        WithTemporaryDirectory(workspace =>
        {
            var activeDirectory = Path.Combine(workspace, "active");
            Directory.CreateDirectory(activeDirectory);
            var (archiveDirectory, freshStore) = ArchiveAndReset(activeDirectory);
            var states = new AtlasStateStore(Path.Combine(workspace, "states"));

            states.DeleteActiveArchive(archiveDirectory);

            Check(!Directory.Exists(archiveDirectory));
            Check(!Directory.Exists(Path.Combine(states.RootDirectory, "Trash")));
            Check(freshStore.Load().Observations.Count == 0);
        });
    }

    public static void TestPurgingLegacyTrashDoesNotTouchActiveOrNamedStates()
    {
        WithTemporaryDirectory(workspace =>
        {
            var states = new AtlasStateStore(Path.Combine(workspace, "states"));
            var trash = Path.Combine(states.RootDirectory, "Trash");
            Directory.CreateDirectory(trash);
            File.WriteAllBytes(Path.Combine(trash, "old-atlas"), new byte[64]);
            var active = Path.Combine(workspace, "active");
            Directory.CreateDirectory(active);
            var named = Path.Combine(states.RootDirectory, Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(named);

            states.PurgeLegacyTrash();

            Check(!Directory.Exists(trash));
            Check(Directory.Exists(active));
            Check(Directory.Exists(named));
        });
    }

    public static void TestAtlasDiskUsageCountsRegisteredImageStorage()
    {
        WithTemporaryDirectory(workspace =>
        {
            var frames = Path.Combine(workspace, "frames");
            Directory.CreateDirectory(frames);
            File.WriteAllBytes(Path.Combine(frames, "one.png"), new byte[8_192]);
            File.WriteAllBytes(Path.Combine(frames, "two.png"), new byte[4_096]);

            Check(AtlasStateStore.FileByteCount(workspace) >= 12_288);
        });
    }

    public static void TestPipelineLoadRestoresSavedSnapshotAndLeavesStateImmutable()
    {
        WithTemporaryDirectory(workspace =>
        {
            var activeDirectory = Path.Combine(workspace, "active");
            Directory.CreateDirectory(activeDirectory);
            var writer = new LiveWorldSnapshotStore(activeDirectory);
            var snapshot = new LiveWorldSnapshot(
                mapRevision: 1,
                anchoredObservationId: 10,
                observations:
                [
                    LiveWorldObservation.Create(
                        10, 1, 1, 32, 18, 32, 16, [],
                        new AtlasPoint(0, 0), new AtlasPoint(0, 0))
                ]);
            Check(writer.Commit(snapshot, expectedRevision: 0));
            var frames = Path.Combine(activeDirectory, "frames");
            Directory.CreateDirectory(frames);
            File.WriteAllBytes(Path.Combine(frames, "1.png"), [1, 2, 3, 4]);
            var states = new AtlasStateStore(Path.Combine(workspace, "states"));
            var saved = states.Save("Saved", destination =>
                CopyDirectory(activeDirectory, destination));
            var savedDirectory = states.WorldPath(saved);

            Directory.Delete(activeDirectory, recursive: true);
            Directory.CreateDirectory(activeDirectory);
            Check(new LiveWorldSnapshotStore(activeDirectory).Load().MapRevision == 0);
            var loaded = LoadSavedWorld(savedDirectory, activeDirectory);

            CheckSnapshotsEqual(loaded, snapshot);
            Check(File.Exists(Path.Combine(activeDirectory, "frames", "1.png")));
            CheckSnapshotsEqual(new LiveWorldSnapshotStore(activeDirectory).Load(), snapshot);
            CheckSnapshotsEqual(new LiveWorldSnapshotStore(savedDirectory).Load(), snapshot);

            var changed = snapshot.ReplaceOptimizedPoses(
                new Dictionary<int, AtlasPoint> { [10] = new(5, 3) },
                snapshot.MapRevision);
            Check(new LiveWorldSnapshotStore(activeDirectory).Commit(changed, snapshot.MapRevision));
            CheckSnapshotsEqual(new LiveWorldSnapshotStore(savedDirectory).Load(), snapshot);
        });
    }

    private static (string ArchiveDirectory, LiveWorldSnapshotStore FreshStore) ArchiveAndReset(
        string activeDirectory)
    {
        var archiveDirectory = Path.Combine(
            Directory.GetParent(activeDirectory)?.FullName
                ?? throw new Exception("Active atlas needs a parent directory."),
            $"{Path.GetFileName(activeDirectory)}.archive-{Guid.NewGuid():D}");
        Directory.Move(activeDirectory, archiveDirectory);
        Directory.CreateDirectory(activeDirectory);
        return (archiveDirectory, new LiveWorldSnapshotStore(activeDirectory));
    }

    private static LiveWorldSnapshot LoadSavedWorld(string savedDirectory, string activeDirectory)
    {
        var parent = Directory.GetParent(activeDirectory)?.FullName
            ?? throw new Exception("Active atlas needs a parent directory.");
        var archiveDirectory = Path.Combine(
            parent, $"{Path.GetFileName(activeDirectory)}.archive-load-{Guid.NewGuid():D}");
        Directory.Move(activeDirectory, archiveDirectory);
        try
        {
            CopyDirectory(savedDirectory, activeDirectory);
            return new LiveWorldSnapshotStore(activeDirectory).Load();
        }
        catch
        {
            if (Directory.Exists(activeDirectory)) Directory.Delete(activeDirectory, recursive: true);
            Directory.Move(archiveDirectory, activeDirectory);
            throw;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(
                     sourceDirectory, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(
                destinationDirectory, Path.GetRelativePath(sourceDirectory, file)));
        }
    }

    private static void CheckSnapshotsEqual(LiveWorldSnapshot actual, LiveWorldSnapshot expected)
    {
        Check(actual.SchemaVersion == expected.SchemaVersion);
        Check(actual.MapRevision == expected.MapRevision);
        Check(actual.AnchoredObservationId == expected.AnchoredObservationId);
        Check(actual.Observations.Count == expected.Observations.Count);
        for (var index = 0; index < actual.Observations.Count; index++)
        {
            var left = actual.Observations[index];
            var right = expected.Observations[index];
            Check(left.Id == right.Id && left.SourceObservationId == right.SourceObservationId);
            Check(left.Timestamp == right.Timestamp);
            Check(left.SourceWidth == right.SourceWidth && left.SourceHeight == right.SourceHeight);
            Check(left.SolveWidth == right.SolveWidth && left.SampleWidth == right.SampleWidth);
            Check(left.ExcludedRects.SequenceEqual(right.ExcludedRects));
            Check(left.LocalPose == right.LocalPose && left.RawPose == right.RawPose);
            Check(left.OptimizedPose == right.OptimizedPose);
            Check(left.CaptureGeneration == right.CaptureGeneration);
            Check(left.LocalEpoch == right.LocalEpoch && left.BasisRevision == right.BasisRevision);
            Check(left.RoomId == right.RoomId);
        }
        Check(actual.Keyframes.SequenceEqual(expected.Keyframes));
        Check(actual.Landmarks.SequenceEqual(expected.Landmarks));
        Check(actual.RelativeMotionEdges.SequenceEqual(expected.RelativeMotionEdges));
        Check(actual.LoopClosureEdges.SequenceEqual(expected.LoopClosureEdges));
    }

    private static void WithTemporaryDirectory(Action<string> body)
    {
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(workspace);
        try { body(workspace); }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
