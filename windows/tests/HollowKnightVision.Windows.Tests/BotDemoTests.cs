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

    public static void TestSaveArchiveRejectsFileLinks()
    {
        using var fixture = new Fixture();
        fixture.Write("user4.dat", "slot four");
        var target = Path.Combine(Path.GetDirectoryName(fixture.Saves)!, "other.dat");
        File.WriteAllText(target, "original");
        var backup = fixture.Store.Create(8);

        foreach (var name in new[] { "user4.dat.bak1", "user4.dat" })
        {
            var linked = Path.Combine(fixture.Saves, name);
            File.Delete(linked);
            File.CreateSymbolicLink(linked, target);
            Throws<InvalidDataException>(() => fixture.Store.Create(8));
            Throws<InvalidDataException>(() => fixture.Store.VerifyBeforeClear(backup));
            Check(File.ReadAllText(target) == "original"
                && File.ReadAllText(Path.Combine(backup.Directory, "user4.dat")) == "slot four"
                && Directory.EnumerateFileSystemEntries(fixture.Archives).SequenceEqual([backup.Directory]),
                "A linked save or sidecar must leave the original target and the existing backup intact.");
            File.Delete(linked);
        }
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

    public static void TestFullNewSaveRouteWaitsForArchiveAndFreshGameplay()
    {
        var nav = new BotDemoNavigation(0);
        var time = 0d;
        BotDemoDecision At(double delay, string? context, string? selection, int occupied = 8, bool active = true, bool gameplay = false)
        {
            time += delay;
            return nav.Observe(new(time, gameplay, context, selection, context is null ? null : time,
                15, occupied, InputButtons.Up), time, active);
        }
        BotDemoDecision Stable(string context, string selection, bool occupied = true)
        {
            _ = At(.8, context, selection, occupied ? 8 : 0, occupied);
            return At(.35, context, selection, occupied ? 8 : 0, occupied);
        }

        Expect(Stable("main-title", "main-title.options"), BotDemoPhase.Navigating, InputButtons.Up);
        Expect(Stable("main-title", "main-title.start-game"), BotDemoPhase.Navigating, InputButtons.ActionZ);
        for (var slot = 1; slot < 4; slot++)
            Expect(Stable("select-profile", $"select-profile.slot-{slot}"), BotDemoPhase.Navigating, InputButtons.Down);
        Expect(Stable("select-profile", "select-profile.slot-4"), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
        Expect(Stable("select-profile", "select-profile.clear-save-4"), BotDemoPhase.Archiving);
        nav.ArchiveCompleted(true, time);
        Expect(Stable("select-profile", "select-profile.slot-4"), BotDemoPhase.SelectingClear, InputButtons.Right);
        Expect(Stable("select-profile", "select-profile.clear-save-4"), BotDemoPhase.ConfirmingClear, InputButtons.ActionZ);
        Expect(Stable("clear-save", "shared.no"), BotDemoPhase.ConfirmingClear, InputButtons.Up);
        Expect(Stable("clear-save", "shared.yes"), BotDemoPhase.WaitingForEmptySlot,
            InputButtons.ActionZ, BotDemoSaveAction.VerifyClear);
        Expect(Stable("select-profile", "select-profile.slot-4", occupied: false), BotDemoPhase.StartingSave);
        Expect(Stable("select-profile", "select-profile.slot-4", occupied: false), BotDemoPhase.LeavingProfile, InputButtons.ActionZ);
        Expect(At(.8, "clear-save", "shared.yes"), BotDemoPhase.Opening, InputButtons.ActionX);
        Expect(At(.8, "brightness", "shared.no"), BotDemoPhase.Opening, InputButtons.ActionZ);
        Expect(At(.8, null, null, gameplay: true), BotDemoPhase.Opening);
        Expect(At(.15, null, null, gameplay: true), BotDemoPhase.Opening);
        Expect(At(.15, null, null, gameplay: true), BotDemoPhase.Gameplay);
    }

    public static void TestGameplayAndNestedMenusReturnToTitle()
    {
        var nav = new BotDemoNavigation(0);
        var time = 0d;
        BotDemoDecision Stable(string? context, string? selection, bool gameplay = false)
        {
            time += .8;
            _ = nav.Observe(Frame(), time, null);
            time += .35;
            return nav.Observe(Frame(), time, null);
            BotDemoFrame Frame() => new(time, gameplay, context, selection, context is null ? null : time, 0, 0, InputButtons.Up);
        }

        Expect(Stable(null, null, gameplay: true), BotDemoPhase.Navigating, InputButtons.PauseMenu);
        Expect(Stable("pause", "pause.continue"), BotDemoPhase.Navigating, InputButtons.Down);
        Expect(Stable("pause", "shared.quit-to-menu"), BotDemoPhase.Navigating, InputButtons.ActionZ);
        Expect(Stable("quit-to-menu", "shared.no"), BotDemoPhase.Navigating, InputButtons.Up);
        Expect(Stable("quit-to-menu", "shared.yes"), BotDemoPhase.Navigating, InputButtons.ActionZ);
        Expect(Stable(null, null), BotDemoPhase.Navigating);
        Expect(Stable("main-title", "main-title.start-game"), BotDemoPhase.Navigating, InputButtons.ActionZ);

        nav = new BotDemoNavigation(time);
        foreach (var context in new[] { "audio", "options", "inventory", "credits", "clear-save", "quit-game" })
            Expect(Stable(context, "shared.no"), BotDemoPhase.Navigating,
                context == "inventory" ? InputButtons.Inventory : InputButtons.ActionA);
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

    public static void TestLongOpeningRetainsSkipActionsUntilHud()
    {
        var nav = new BotDemoNavigation(0);
        BotDemoDecision At(double t, bool profile = false, bool gameplay = false) =>
            nav.Observe(new(t, gameplay, profile ? "select-profile" : null,
                profile ? "select-profile.slot-4" : null, profile ? t : null, 15, 0, InputButtons.None), t, false);
        _ = At(.1, profile: true);
        Expect(At(.4, profile: true), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
        nav.ArchiveCompleted(false, .5);
        _ = At(.6, profile: true);
        Expect(At(.9, profile: true), BotDemoPhase.LeavingProfile, InputButtons.ActionZ);
        for (var index = 0; index < 150; index++)
            Expect(At(2 + index * .7), BotDemoPhase.Opening, index % 2 == 0 ? InputButtons.ActionX : InputButtons.ActionZ);
        Expect(At(108, gameplay: true), BotDemoPhase.Opening);
        Expect(At(108.14, gameplay: true), BotDemoPhase.Opening);
        Expect(At(108.28, gameplay: true), BotDemoPhase.Gameplay);
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
        Expect(WaitForArchive(session, SlotFour(clock, 8)), BotDemoPhase.SelectingClear);
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

    public static void TestSessionStopsWhenArchiveCannotBePublished()
    {
        using var fixture = new Fixture();
        fixture.Write("user4.dat", "original save");
        File.WriteAllText(fixture.Archives, "archive destination is a file");
        var session = new BotDemoSession(fixture.Store, 0);
        var profile = SlotFour(.4, 8);

        Expect(session.Observe(SlotFour(.1, 8), .1), BotDemoPhase.Navigating);
        Expect(session.Observe(profile, .4), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
        var failed = WaitForArchive(session, profile);
        Expect(failed, BotDemoPhase.Stopped);
        Check(!string.IsNullOrEmpty(failed.Issue) && session.Backup is null,
            "The bot needs a verified backup before it can clear a save.");
        Expect(session.Observe(SlotFour(.8, 8), .8), BotDemoPhase.Stopped);
        Check(File.ReadAllText(Path.Combine(fixture.Saves, "user4.dat")) == "original save"
            && File.ReadAllText(fixture.Archives) == "archive destination is a file",
            "A failed archive must preserve the original files and send no controls.");
    }

    public static void TestSessionRechecksSaveBeforeStartingOrRetrying()
    {
        foreach (var retry in new[] { false, true })
        {
            using var fixture = new Fixture();
            var session = new BotDemoSession(fixture.Store, 0);
            BotDemoDecision Observe(double time, int occupied = 0) =>
                session.Observe(SlotFour(time, occupied), time);

            Expect(Observe(.1), BotDemoPhase.Navigating);
            Expect(Observe(.4), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
            Expect(WaitForArchive(session, SlotFour(.4, 0)), BotDemoPhase.StartingSave);
            Expect(Observe(.5), BotDemoPhase.StartingSave);

            if (retry)
            {
                Expect(Observe(.83), BotDemoPhase.LeavingProfile, InputButtons.ActionZ);
                Expect(Observe(2.72), BotDemoPhase.LeavingProfile);
            }
            else Expect(Observe(.72), BotDemoPhase.StartingSave);

            fixture.Write("user4.dat", "save appeared after the last empty observation");
            Expect(Observe(retry ? 2.86 : .77), retry ? BotDemoPhase.LeavingProfile : BotDemoPhase.StartingSave);

            if (!retry)
            {
                Expect(Observe(.83, 8), BotDemoPhase.StartingSave);
                Expect(Observe(1.12, 8), BotDemoPhase.Stopped);
            }
            Check(File.ReadAllText(Path.Combine(fixture.Saves, "user4.dat")) ==
                "save appeared after the last empty observation", "The replacement save must remain intact.");
        }
    }

    public static void TestLegacySaveIsArchivedOnlyAfterFullMatchingProfileEvidence()
    {
        using var fixture = new Fixture();
        const string foreign = "user4_1.5.12620.dat";
        fixture.Write("user1.dat", "current slot one");
        fixture.Write(foreign, "different game version");
        var session = new BotDemoSession(fixture.Store, 0);

        foreach (var time in new[] { .1, .4 })
            Expect(session.Observe(SlotFour(time, 0) with { CheckedSlots = 7 }, time), BotDemoPhase.Navigating);
        foreach (var time in new[] { .7, 1.0 })
            Expect(session.Observe(SlotFour(time, 0), time), BotDemoPhase.Navigating);
        Check(session.Backup is null && !Directory.Exists(fixture.Archives),
            "Incomplete or mismatched profile observations cannot archive or select slot 4.");
        Expect(session.Observe(SlotFour(1.3, 1), 1.3), BotDemoPhase.Navigating);
        var measured = SlotFour(1.6, 1);
        Expect(session.Observe(measured, 1.6), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
        Expect(session.Observe(measured, double.PositiveInfinity), BotDemoPhase.Archiving);
        Check(session.Backup is null, "An invalid observation clock cannot finalize a pending archive.");
        Expect(WaitForArchive(session, measured), BotDemoPhase.StartingSave);
        var backup = session.Backup;
        Check(backup is { HadActiveSave: false } && Directory.GetFiles(backup.Directory).Select(Path.GetFileName).Order()
            .SequenceEqual(new[] { "manifest.json", foreign }.Order())
            && File.ReadAllText(Path.Combine(backup.Directory, foreign)) == "different game version",
            "A foreign slot-four file belongs in the backup while the matching current slot remains empty.");
        Throws<IOException>(() => fixture.Store.VerifyBeforeClear(backup!));
        Expect(session.Observe(SlotFour(1.9, 1), 1.9), BotDemoPhase.StartingSave);
        Expect(session.Observe(SlotFour(2.2, 1), 2.2), BotDemoPhase.LeavingProfile, InputButtons.ActionZ);
        Check(File.ReadAllText(Path.Combine(fixture.Saves, "user1.dat")) == "current slot one"
            && File.ReadAllText(Path.Combine(fixture.Saves, foreign)) == "different game version"
            && !File.Exists(Path.Combine(fixture.Saves, "user4.dat")),
            "Starting the empty slot must preserve the current and foreign save files.");
    }

    public static void TestNonfiniteProfileClocksCannotCheckSlotFiles()
    {
        var navigation = new BotDemoNavigation(0);
        Expect(navigation.Observe(SlotFour(.1, 8), double.PositiveInfinity, true), BotDemoPhase.Navigating);
        foreach (var (capture, observed) in new[] { (double.NaN, .1), (.1, double.NaN), (.1, double.PositiveInfinity) })
        {
            using var fixture = new Fixture();
            Directory.CreateDirectory(Path.Combine(fixture.Saves, "user4_unsafe.dat"));
            var session = new BotDemoSession(fixture.Store, 0);
            Expect(session.Observe(SlotFour(capture, 8), observed), BotDemoPhase.Navigating);
            Check(session.Backup is null && !Directory.Exists(fixture.Archives),
                "A nonfinite profile clock must have no filesystem consequences.");
            var admitted = session.Observe(SlotFour(.4, 8), .4);
            Expect(admitted, BotDemoPhase.Stopped);
            Check(admitted.Issue is not null && session.Backup is null,
                "Fresh evidence must still reject an unsafe profile file.");
        }
    }

    private static BotDemoFrame SlotFour(double time, int occupied) =>
        new(time, false, "select-profile", "select-profile.slot-4", time, 15, occupied, InputButtons.None);

    private static BotDemoDecision WaitForArchive(BotDemoSession session, BotDemoFrame lastProfile)
    {
        BotDemoDecision decision = default;
        // Poll completion without making the same profile frame advance navigation.
        Check(SpinWait.SpinUntil(() =>
        {
            decision = session.Observe(lastProfile, lastProfile.CapturedAt);
            return decision.Phase != BotDemoPhase.Archiving;
        }, TimeSpan.FromSeconds(10)), "The archive background job never completed.");
        return decision;
    }

    public static void TestStaleDuplicateAndPreStartFramesCannotAdvanceNavigation()
    {
        var navigation = new BotDemoNavigation(100);
        BotDemoDecision Profile(double capture, double now, double menu) =>
            navigation.Observe(new(capture, false, "select-profile", "select-profile.slot-4",
                menu, 15, 8, InputButtons.None), now, activeSave: true);

        Expect(Profile(99.9, 100, 99.9), BotDemoPhase.Navigating);
        Expect(Profile(100.1, 100.1, 100.1), BotDemoPhase.Navigating);
        Expect(Profile(100.1, 100.4, 100.1), BotDemoPhase.Navigating);
        Expect(Profile(100.4, 101, 100.4), BotDemoPhase.Navigating);
        Expect(Profile(101.1, 101.1, 100.1), BotDemoPhase.Navigating);
        Expect(Profile(101.4, 101.4, 101.4), BotDemoPhase.Archiving, action: BotDemoSaveAction.Archive);
    }

    public static void TestIgnoredTitleSelectRetriesOnlyOnTheSameRecognizedMenu()
    {
        var navigation = new BotDemoNavigation(100);
        const string start = "main-title.start-game";
        BotDemoDecision Menu(double time, string? context, string? option) =>
            navigation.Observe(new(time, false, context, option, context is null ? null : time,
                15, 0, InputButtons.None), time, activeSave: false);

        Expect(Menu(100.1, "main-title", start), BotDemoPhase.Navigating);
        Expect(Menu(100.4, "main-title", start), BotDemoPhase.Navigating, InputButtons.ActionZ);
        Expect(Menu(102.1, "main-title", "main-title.extras"), BotDemoPhase.Navigating);
        Expect(Menu(102.4, "main-title", "main-title.extras"), BotDemoPhase.Navigating);
        Expect(Menu(102.7, null, null), BotDemoPhase.Navigating);
        Expect(Menu(103, null, null), BotDemoPhase.Navigating);
        Expect(Menu(103.3, "main-title", start), BotDemoPhase.Navigating);
        Expect(Menu(103.6, "main-title", start), BotDemoPhase.Navigating, InputButtons.ActionZ);
        Expect(Menu(104.4, "select-profile", "select-profile.slot-4"), BotDemoPhase.Navigating);
        Expect(Menu(104.7, "select-profile", "select-profile.slot-4"), BotDemoPhase.Archiving,
            action: BotDemoSaveAction.Archive);
    }

    public static void TestUnrecognizedTransitionTimesOutWithoutBlindConfirmations()
    {
        var navigation = new BotDemoNavigation(100);
        BotDemoDecision At(double time, bool title) => navigation.Observe(new(time, false,
            title ? "main-title" : null, title ? "main-title.start-game" : null, title ? time : null,
            0, 0, InputButtons.None), time, activeSave: null);
        Expect(At(100.1, title: true), BotDemoPhase.Navigating);
        Expect(At(100.4, title: true), BotDemoPhase.Navigating, InputButtons.ActionZ);
        Expect(At(101.3, title: false), BotDemoPhase.Navigating);
        Expect(At(103.3, title: false), BotDemoPhase.Navigating);
        var timedOut = At(116, title: false);
        Expect(timedOut, BotDemoPhase.Stopped);
        Check(timedOut.Issue?.Contains("expected menu", StringComparison.Ordinal) == true,
            "A missing menu after selection should stop before more inputs.");
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
        Check(available.ProfilesMeasured && available.OccupiedSlots == 8 && available.CapturedAt == 3 && available.MenuTimestamp == 1
            && available.Selection == "select-profile.clear-save-4" && available.DirectionToYes == InputButtons.Down,
            "Visual observations must carry exact profile and dialog evidence.");
        foreach (var ignored in new[] { BotDemoFrame.FromVision(4, 1.31, true, menu),
                     BotDemoFrame.FromVision(4, .99, true, menu),
                     BotDemoFrame.FromVision(4, 1.1, true, menu with { IsMatch = false }) })
            Check(ignored.Context is null && ignored.Selection is null && ignored.OccupiedSlots == 0 && ignored.FreshGameplay,
                "Stale, future, or rejected menus must not supply selection or occupancy.");
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

    public static void TestConfirmationFollowsMatchedAnchorGeometry()
    {
        (StencilRect Yes, InputButtons Direction)[] layouts =
        [
            (new(40, 20, 20, 10), InputButtons.Up),
            (new(40, 100, 20, 10), InputButtons.Down),
            (new(20, 60, 20, 10), InputButtons.Left),
            (new(80, 60, 20, 10), InputButtons.Right)
        ];
        foreach (var (yes, direction) in layouts)
        {
            MenuStencilMatch[] anchors =
            [
                new("shared.yes", "Yes", yes, 1, null),
                new("shared.no", "No", new(40, 60, 20, 10), 1, null)
            ];
            var menu = new MenuStencilResult("quit-to-menu", true, 1, "No", anchors, [], [],
                0, 0, 1, MenuStencilPhase.Tracking, 1, 1, "en");
            Check(BotDemoFrame.FromVision(2, 1.1, false, menu).DirectionToYes == direction,
                "The confirmation direction must follow the label geometry in Windows image coordinates.");
        }
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
        public string Archives => Path.Combine(root, "archives");
        public BotDemoSaveArchive Store { get; }

        public Fixture()
        {
            Saves = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName;
            Store = new(Saves, Archives);
        }

        public void Write(string name, string contents) => File.WriteAllText(Path.Combine(Saves, name), contents);
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
