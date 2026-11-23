using HollowKnightVision.Windows.Core;

internal static class BotDemoTests
{
    public static void TestArchiveCopiesOnlySlotFourAndVerifiesSourceAndBackup()
    {
        using var fixture = new Fixture();
        fixture.Write("user1.dat", "other save");
        fixture.Write("user4.dat", "version one");
        fixture.Write("user1_previous.dat", "previous base");
        fixture.Write("user4_previous.dat", "previous slot 4");
        fixture.Write("user4.dat.bak1", "sidecar");
        fixture.Write("session.json", "settings");
        Check(fixture.Store.HasActiveSave(9), "The matching current or versioned family should be accepted.");
        Throws<BotDemoSaveProfileMismatchException>(() => fixture.Store.HasActiveSave(1));
        var backup = fixture.Store.Create(9);
        Check(backup.HadActiveSave
            && Directory.GetFiles(backup.Directory).Select(Path.GetFileName).Order().SequenceEqual(
                new[] { "manifest.json", "user4.dat", "user4.dat.bak1", "user4_previous.dat" }.Order()),
            "Archive should include all slot 4 files and only its associated manifest.");
        Check(File.ReadAllText(Path.Combine(backup.Directory, "user4.dat")) == "version one"
            && File.ReadAllText(Path.Combine(fixture.Saves, "user1.dat")) == "other save",
            "Original game files must stay intact.");
        fixture.Store.VerifyBeforeClear(backup);
        fixture.Write("user4.dat", "new data");
        Throws<IOException>(() => fixture.Store.VerifyBeforeClear(backup));
        fixture.Write("user4.dat", "version one");
        File.WriteAllText(Path.Combine(backup.Directory, "user4.dat.bak1"), "damaged backup");
        Throws<IOException>(() => fixture.Store.VerifyBeforeClear(backup));
        File.WriteAllText(Path.Combine(backup.Directory, "user4.dat.bak1"), "sidecar");
        fixture.Write("user4_added.bak", "new sidecar");
        Throws<IOException>(() => fixture.Store.VerifyBeforeClear(backup));
        Throws<ArgumentException>(() => new BotDemoSaveArchive(fixture.Saves, Path.Combine(fixture.Saves, "nested")));
    }

    public static void TestEmptySaveAndMismatchedFamiliesCannotAuthorizeClear()
    {
        using var fixture = new Fixture();
        Check(!fixture.Store.HasActiveSave(0), "An empty measured family should be accepted.");
        var empty = fixture.Store.Create(0);
        Check(!empty.HadActiveSave && Directory.GetFiles(empty.Directory).Length == 1, "An empty backup needs a manifest.");
        Throws<IOException>(() => fixture.Store.VerifyBeforeClear(empty));
        fixture.Write("user2.dat", "current family");
        fixture.Write("user1_old.dat", "legacy family");
        fixture.Write("user4_old.dat", "legacy slot 4");
        Check(!fixture.Store.HasActiveSave(2) && fixture.Store.HasActiveSave(9),
            "The selected family must be reconciled against the actual profiles.");
        Throws<BotDemoSaveProfileMismatchException>(() => fixture.Store.Create(10));
        Directory.CreateDirectory(Path.Combine(fixture.Saves, "user4.bad"));
        Throws<InvalidDataException>(() => fixture.Store.Create(2));
        Check(Directory.GetDirectories(Path.GetDirectoryName(empty.Directory)!).Length == 1,
            "An unsafe archive must leave no published or staged backup.");
    }

    public static void TestMeasuredSlotAndVerifiedClear()
    {
        var nav = new BotDemoNavigation(0);
        BotDemoDecision At(double t, string? screen, string? option, int checkedSlots = 15, int occupied = 9,
            bool? active = true, InputButtons yes = InputButtons.None) =>
            nav.Observe(new(t, false, screen, option, t, checkedSlots, occupied, yes), t, active);

        _ = At(.1, "clear-save", "shared.yes");
        Expect(At(.4, "clear-save", "shared.yes"), BotDemoPhase.Navigating, InputButtons.ActionA);
        _ = At(1.2, "select-profile", "select-profile.slot-2");
        Expect(At(1.6, "select-profile", "select-profile.slot-2"), BotDemoPhase.Navigating, InputButtons.Down);
        _ = At(2.4, "select-profile", "select-profile.slot-4", checkedSlots: 7);
        Expect(At(2.8, "select-profile", "select-profile.slot-4", checkedSlots: 7), BotDemoPhase.Navigating);
        _ = At(3, "select-profile", "select-profile.slot-4", active: null);
        Expect(At(3.4, "select-profile", "select-profile.slot-4", active: null), BotDemoPhase.Navigating);
        Expect(At(3.8, "select-profile", "select-profile.slot-4"), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
        Expect(At(4.2, "clear-save", "shared.yes"), BotDemoPhase.Archiving);
        nav.ArchiveCompleted(true, 4.3);
        _ = At(4.4, "select-profile", "select-profile.slot-4");
        Expect(At(4.8, "select-profile", "select-profile.slot-4"), BotDemoPhase.SelectingClear, InputButtons.Right);
        _ = At(5.6, "select-profile", "select-profile.clear-save-3");
        Expect(At(6, "select-profile", "select-profile.clear-save-3"), BotDemoPhase.SelectingClear, InputButtons.Left);
        _ = At(6.8, "select-profile", "select-profile.clear-save-4");
        Expect(At(7.2, "select-profile", "select-profile.clear-save-4"), BotDemoPhase.ConfirmingClear, InputButtons.ActionZ);
        _ = At(8, "clear-save", "shared.no", yes: InputButtons.Up);
        Expect(At(8.4, "clear-save", "shared.no", yes: InputButtons.Up), BotDemoPhase.ConfirmingClear, InputButtons.Up);
        _ = At(9.2, "clear-save", "shared.yes");
        Expect(At(9.6, "clear-save", "shared.yes"), BotDemoPhase.WaitingForEmptySlot,
            InputButtons.ActionZ, BotDemoSaveAction.VerifyClear);
        _ = At(10.4, "clear-save", "shared.yes");
        Expect(At(12.4, "clear-save", "shared.yes"), BotDemoPhase.WaitingForEmptySlot);
        _ = At(13, "select-profile", "select-profile.slot-4", occupied: 1, active: null);
        Expect(At(13.4, "select-profile", "select-profile.slot-4", occupied: 1, active: null), BotDemoPhase.WaitingForEmptySlot);
        Expect(At(13.8, "select-profile", "select-profile.slot-4", occupied: 1, active: false), BotDemoPhase.StartingSave);
        _ = At(14, "select-profile", "select-profile.slot-4", occupied: 1, active: false);
        Expect(At(14.4, "select-profile", "select-profile.slot-4", occupied: 1, active: false),
            BotDemoPhase.LeavingProfile, InputButtons.ActionZ);
    }

    public static void TestEmptySlotSkipsOpeningAndRequiresFreshHud()
    {
        var nav = new BotDemoNavigation(0);
        BotDemoDecision At(double t, string? screen = null, string? option = null, bool gameplay = false) =>
            nav.Observe(new(t, gameplay, screen, option, screen is null ? null : t, 15, 0, InputButtons.None), t, false);
        _ = At(.1, "main-title", "main-title.start-game");
        Expect(At(.4, "main-title", "main-title.start-game"), BotDemoPhase.Navigating, InputButtons.ActionZ);
        _ = At(1.15, "select-profile", "select-profile.slot-4");
        Expect(At(1.5, "select-profile", "select-profile.slot-4"), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
        nav.ArchiveCompleted(false, 1.6);
        _ = At(1.7, "select-profile", "select-profile.slot-4");
        Expect(At(2.1, "select-profile", "select-profile.slot-4"), BotDemoPhase.LeavingProfile, InputButtons.ActionZ);
        Expect(At(2.5, "quit-game", "shared.yes"), BotDemoPhase.LeavingProfile);
        Expect(At(2.9, "quit-game", "shared.yes"), BotDemoPhase.Opening, InputButtons.ActionX);
        Expect(At(3.7, "brightness", "shared.no"), BotDemoPhase.Opening, InputButtons.ActionZ);
        Expect(At(4.4, gameplay: true), BotDemoPhase.Opening);
        Expect(At(4.5), BotDemoPhase.Opening, InputButtons.ActionX);
        Expect(At(4.7, gameplay: true), BotDemoPhase.Opening);
        Expect(At(4.9, gameplay: true), BotDemoPhase.Opening);
        Expect(At(5, gameplay: true), BotDemoPhase.Gameplay);
    }

    public static void TestSessionStopsIfGameSaveChangesBeforeConfirm()
    {
        using var fixture = new Fixture();
        fixture.Write("user4.dat", "original save");
        var session = new BotDemoSession(fixture.Store, 0);
        var clock = .1;
        BotDemoDecision Step(string screen, string option)
        {
            clock += .31;
            return session.Observe(new(clock, false, screen, option, clock, 15, 8, InputButtons.None), clock);
        }
        _ = Step("select-profile", "select-profile.slot-4");
        Expect(Step("select-profile", "select-profile.slot-4"), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
        var until = DateTime.UtcNow.AddSeconds(10);
        var entered = false;
        while (DateTime.UtcNow < until && !entered)
        {
            // Poll completion at a held capture timestamp. Navigation advances
            // only after a new frame and the archive validates independently.
            var result = session.Observe(new(clock, false, "select-profile", "select-profile.slot-4",
                clock, 15, 8, InputButtons.None), clock);
            Check(result.Phase != BotDemoPhase.Stopped, result.Issue ?? "Archive failed.");
            entered = session.Backup is not null;
            if (!entered) Thread.Sleep(5);
        }
        Check(entered, "The archive background job never completed.");
        _ = Step("select-profile", "select-profile.slot-4");
        Expect(Step("select-profile", "select-profile.slot-4"), BotDemoPhase.SelectingClear, InputButtons.Right);
        _ = Step("select-profile", "select-profile.clear-save-4");
        _ = Step("select-profile", "select-profile.clear-save-4");
        Expect(Step("select-profile", "select-profile.clear-save-4"), BotDemoPhase.ConfirmingClear, InputButtons.ActionZ);
        fixture.Write("user4.dat", "Steam changed the save");
        _ = Step("clear-save", "shared.yes");
        _ = Step("clear-save", "shared.yes");
        var decision = Step("clear-save", "shared.yes");
        Expect(decision, BotDemoPhase.Stopped);
        Check(decision.Issue?.Contains("changed", StringComparison.OrdinalIgnoreCase) == true
            && File.ReadAllText(Path.Combine(fixture.Saves, "user4.dat")) == "Steam changed the save"
            && File.ReadAllText(Path.Combine(session.Backup!.Directory, "user4.dat")) == "original save",
            "A racing save must stop the bot before sending confirm and preserve the archive.");
    }

    public static void TestVisionAdmitsOnlyFreshMeasuredEvidence()
    {
        MenuStencilMatch[] pair =
        [
            new("selector", "Selector", new(10, 20, 8, 8), 1, null),
            new("selector", "Selector", new(50, 20, 8, 8), 1, null)
        ];
        MenuStencilMatch[] anchors =
        [
            new("shared.yes", "Yes", new(10, 40, 10, 8), 1, null),
            new("shared.no", "No", new(10, 20, 10, 8), 1, null)
        ];
        var menu = new MenuStencilResult("select-profile", true, 1, "Profile", anchors, pair, [],
            0, 0, 1, MenuStencilPhase.Tracking, 0, 1, "en")
        {
            SelectedOptionIdentifier = "select-profile.clear-save-4",
            CheckedProfileSlots = [1, 2, 3, 4, 6],
            OccupiedProfileSlots = [4]
        };
        var available = BotDemoFrame.FromVision(3, 1.2, false, menu);
        Check(available.ProfilesMeasured && available.Slot4Occupied
            && available.Selection == "select-profile.clear-save-4" && available.DirectionToYes == InputButtons.Down,
            "Visual observations must carry exact profile and dialog evidence.");
        var stale = BotDemoFrame.FromVision(4, 1.31, true, menu);
        Check(stale.Context is null && stale.FreshGameplay, "Stale menus must not replace fresh Gameplay evidence.");
        var guard = new BotDemoNavigation(0);
        _ = guard.Observe(available with { Context = "pause", Selection = "quit-to-menu.quit-to-menu" }, 3, null);
        var pause = available with { CapturedAt = 3.4, MenuTimestamp = 1.4, Context = "pause", Selection = "quit-to-menu.quit-to-menu" };
        Expect(guard.Observe(pause, 3.4, null), BotDemoPhase.Navigating, InputButtons.ActionZ);
        Expect(guard.Observe(pause, 3.6, null), BotDemoPhase.Navigating);
        var after = pause with { CapturedAt = 3.9, MenuTimestamp = 1.9, Context = "quit-to-menu", Selection = "shared.no" };
        _ = guard.Observe(after, 3.9, null);
        Expect(guard.Observe(after with { CapturedAt = 4.3, MenuTimestamp = 2.3 }, 4.3, null),
            BotDemoPhase.Navigating, InputButtons.Down);
        Expect(guard.Observe(after, 95, null), BotDemoPhase.Stopped);
    }

    private static void Expect(BotDemoDecision decision, BotDemoPhase phase, InputButtons buttons = InputButtons.None,
        BotDemoSaveAction action = BotDemoSaveAction.None) =>
        Check(decision.Phase == phase && decision.Buttons == buttons && decision.SaveAction == action,
            $"Expected {phase}/{buttons}/{action}. Received {decision}.");

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("hkv-bot-demo-").FullName;
        public string Saves { get; }
        public BotDemoSaveArchive Store { get; }

        public Fixture()
        {
            Saves = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName;
            Store = new(Saves, Path.Combine(root, "archives"));
        }

        public void Write(string name, string contents) => File.WriteAllText(Path.Combine(Saves, name), contents);
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
