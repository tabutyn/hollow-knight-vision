using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>A playthrough identity stays distinct when a slot is cleared and reused.</summary>
public sealed record SaveProfileAtlas(int Slot, Guid Id)
{
    private const string Filename = "save-profile.json";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Name => $"Atlas{Slot}";

    public static SaveProfileAtlas? Read(string world)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<SaveProfileAtlas>(
                File.ReadAllBytes(Path.Combine(world, Filename)), ModelStoreJson.Options);
            return profile is { IsValid: true } ? profile : throw new InvalidDataException("Invalid Atlas save profile.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Write(string world)
    {
        if (!IsValid) throw new InvalidDataException("Invalid Atlas save profile.");
        var destination = Path.Combine(world, Filename);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, this, ModelStoreJson.Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static SaveProfileAtlas Target(ProfileLaunch launch, SaveProfileAtlas? active, IReadOnlyList<AtlasSavedState> saved)
    {
        if (!launch.IsNew && active?.Slot == launch.Slot) return active;
        if (!launch.IsNew && saved.FirstOrDefault(state => state.Profile?.Slot == launch.Slot)?.Profile is { } previous)
            return previous;
        return new(launch.Slot, Guid.NewGuid());
    }

    internal bool IsValid => Slot is >= 1 and <= 4 && Id != Guid.Empty;
}

public readonly record struct ProfileLaunch(int Slot, bool IsNew);

/// <summary>Commits the last stable, measured card only after a fresh Gameplay HUD.</summary>
public sealed class ProfileLaunchDetector
{
    private const double StableDuration = .12;
    private long? generation;
    private ProfileLaunch? candidate, armed;
    private double firstSeen;
    private double lastMenu = double.NegativeInfinity;
    private double? firstHud;
    private readonly HashSet<int> occupied = [];

    public bool AwaitingGameplay => armed.HasValue;

    public ProfileLaunch? Observe(MenuStencilResult? menu, bool freshGameplay, double timestamp, long captureGeneration)
    {
        if (generation != captureGeneration)
        {
            ClearSelection();
            generation = captureGeneration;
        }
        if (freshGameplay)
        {
            if (armed is not { } launch || !double.IsFinite(timestamp)) return null;
            firstHud ??= timestamp;
            if (timestamp - firstHud < StableDuration) return null;
            ClearSelection();
            return launch;
        }
        firstHud = null;
        if (menu is not { IsMatch: true }) return null;
        if (menu.ContextIdentifier != "select-profile")
        {
            if (menu.ContextIdentifier is "main-title" or "clear-save" or "quit-to-menu" or "quit-game" or "pause")
                ClearSelection();
            return null;
        }
        var time = menu.SourceTimestamp;
        if (!double.IsFinite(time) || time <= lastMenu) return null;
        var gap = time - lastMenu;
        lastMenu = time;
        occupied.UnionWith(menu.OccupiedProfileSlots);
        if (armed is { IsNew: true } empty && occupied.Contains(empty.Slot)) armed = null;
        if (!menu.SelectedPairAccepted) return null;
        var slot = ProfileSlot(menu);
        if (slot is null)
        {
            candidate = null;
            armed = null;
            return null;
        }
        if (armed?.Slot != slot) armed = null;
        if (!menu.CheckedProfileSlots.Contains(slot.Value)) return null;
        var next = new ProfileLaunch(slot.Value, !occupied.Contains(slot.Value));
        if (candidate != next || gap > .75)
        {
            candidate = next;
            firstSeen = time;
        }
        else if (time - firstSeen >= StableDuration) armed = next;
        return null;
    }

    private static int? ProfileSlot(MenuStencilResult menu) =>
        ParseSlot(menu.SelectedOptionIdentifier, "select-profile.slot-");

    internal static int? ParseSlot(string? identifier, string prefix) =>
        identifier is not null && identifier.StartsWith(prefix, StringComparison.Ordinal)
        && identifier.Length == prefix.Length + 1 && identifier[^1] is >= '1' and <= '4'
            ? identifier[^1] - '0' : null;

    private void ClearSelection()
    {
        candidate = armed = null;
        lastMenu = double.NegativeInfinity;
        firstHud = null;
        occupied.Clear();
    }
}
