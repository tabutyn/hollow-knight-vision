using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public static class BotClipDeletion
{
    public static void Delete(string captureRoot, string replayRoot, BotCaptureManifest selected)
    {
        var library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(captureRoot));
        EnsureOwnedEntry(library, directory: true);
        var key = OperatingSystem.IsWindows() ? library.ToUpperInvariant() : library;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        using var gate = new Mutex(false, $"HollowKnightVision.BotClipDeletion.{hash}");
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another clip deletion is still in progress.");
            DeleteOwnedClip(library, replayRoot, selected);
        }
        finally
        {
            if (acquired) gate.ReleaseMutex();
        }
    }

    private static void DeleteOwnedClip(string library, string replayRoot, BotCaptureManifest selected)
    {
        var clip = Path.Combine(library, BotCaptureSession.DirectoryName(selected.Id));
        EnsureOwnedEntry(clip, directory: true);
        var current = ReadManifest(clip);
        if (current is not { Status: "completed", SchemaVersion: BotRecordingContract.SchemaVersion }
            || current.Id != selected.Id || current.CreatedAt.ToUnixTimeSeconds() != selected.CreatedAt.ToUnixTimeSeconds()
            || current.ReplayId != selected.ReplayId || current.PairedClipId != selected.PairedClipId)
            throw new InvalidDataException("Only an unchanged, completed capture can be deleted.");

        var replay = current.ReplayId ?? current.Id;
        // A modded run can publish only its Game Play side. Its replay then uses the
        // original run ID even though the corresponding Hacker clip was discarded.
        var singleVisualFromPair = current is { BotDomain: "gameplay", GameMode: "modded",
            InputSource: "receiver-sent", PairedClipId: null };
        if (replay == Guid.Empty || current.PairedClipId == current.Id
            || replay != current.Id && replay != current.PairedClipId && !singleVisualFromPair)
            throw new InvalidDataException("The capture has an invalid replay identity.");

        var entries = new List<(string Path, bool Directory)> { (clip, true) };
        var replays = Path.GetFullPath(replayRoot);
        if (Directory.Exists(replays) && !OtherClipUsesReplay(library, current, replay))
        {
            EnsureOwnedEntry(replays, directory: true);
            var framesName = $"frames-{replay:D}";
            var pathSuffix = $"-{replay:D}.json";
            foreach (var path in Directory.EnumerateFileSystemEntries(replays))
            {
                var name = Path.GetFileName(path);
                var frames = string.Equals(name, framesName, StringComparison.OrdinalIgnoreCase);
                if (!frames && !(RecordedInputPathStore.IsSafeFileName(name)
                    && name.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase))) continue;
                EnsureOwnedEntry(path, directory: frames);
                entries.Add((path, frames));
            }
        }

        // Move every owned entry out of the live library before removing its contents.
        // A locked replay can then be retried with its clip and all original paths intact.
        var staged = new List<(string Original, string Temporary, bool Directory)>();
        try
        {
            foreach (var (path, directory) in entries)
            {
                var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".deleted-{Guid.NewGuid():N}");
                Move(path, temporary, directory);
                staged.Add((path, temporary, directory));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            List<Exception> rollbackFailures = [error];
            foreach (var (original, temporary, directory) in staged.AsEnumerable().Reverse())
            {
                try { Move(temporary, original, directory); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(new IOException($"Could not restore {original}. Its files remain at {temporary}.", rollbackError));
                }
            }
            if (rollbackFailures.Count > 1)
                throw new IOException(string.Join(" ", rollbackFailures.Select(failure => failure.Message)),
                    new AggregateException(rollbackFailures));
            throw;
        }

        List<string> cleanupFailures = [];
        foreach (var (_, temporary, directory) in staged)
        {
            try
            {
                if (directory) Directory.Delete(temporary, recursive: true);
                else File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                cleanupFailures.Add(temporary);
            }
        }
        if (cleanupFailures.Count > 0)
            throw new IOException($"The clip was removed from the library, but some files could not be erased. {string.Join(", ", cleanupFailures)}");
    }

    private static bool OtherClipUsesReplay(string library, BotCaptureManifest clip, Guid replay)
    {
        var namedPartner = clip.PairedClipId is { } partner ? BotCaptureSession.DirectoryName(partner) : null;
        foreach (var folder in Directory.EnumerateDirectories(library, "demo-*"))
        {
            var name = Path.GetFileName(folder);
            if (string.Equals(name, namedPartner, StringComparison.OrdinalIgnoreCase)) return true;
            if (!Guid.TryParseExact(name.AsSpan(5), "D", out var id) || id == clip.Id) continue;
            try
            {
                EnsureOwnedEntry(folder, directory: true);
                if (ReadManifest(folder) is { } other && other.Id == id && (other.ReplayId ?? other.Id) == replay)
                    return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        }
        return false;
    }

    private static BotCaptureManifest? ReadManifest(string folder)
    {
        var file = Path.Combine(folder, "manifest.json");
        EnsureOwnedEntry(file, directory: false);
        return JsonSerializer.Deserialize<BotCaptureManifest>(File.ReadAllBytes(file), BotCaptureSession.Json);
    }

    private static void EnsureOwnedEntry(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new InvalidDataException("A capture or linked replay points outside the recording library or has the wrong file type.");
    }

    private static void Move(string source, string target, bool directory)
    {
        if (directory) Directory.Move(source, target);
        else File.Move(source, target);
    }
}
