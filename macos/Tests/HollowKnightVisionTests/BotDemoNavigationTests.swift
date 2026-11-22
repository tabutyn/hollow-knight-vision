import XCTest
@testable import HollowKnightVision

final class BotDemoNavigationTests: XCTestCase {
    private func sample(_ context: LabelingContext?, _ selected: String?, at time: Double,
                        gameplay: Bool = false, occupied: Set<Int> = [4], generation: UInt64 = 1) -> BotDemoMenuObservation {
        .init(context: context, selectedIdentifier: selected, gameplay: gameplay, occupiedSlots: occupied,
              frameTimestamp: time, capturedAt: time, availableAt: time, generation: generation,
              directionToYes: .up)
    }
    private func stable(_ machine: inout BotDemoNavigation, _ context: LabelingContext?, _ selected: String?,
                        time: inout Double, gameplay: Bool = false, occupied: Set<Int> = [4],
                        activeSave: Bool = true) -> BotDemoNavigation.Effect? {
        let first = machine.observe(sample(context, selected, at: time, gameplay: gameplay, occupied: occupied), hasActiveSave: activeSave, now: time)
        time += 0.3
        let second = machine.observe(sample(context, selected, at: time, gameplay: gameplay, occupied: occupied), hasActiveSave: activeSave, now: time)
        time += 0.4
        return second ?? first
    }

    func testFullNewSaveRouteArchivesBeforeClearingAndStartsOnlyAfterFreshGameplay() {
        var machine = BotDemoNavigation(), time = 100.0
        machine.begin(at: time)
        XCTAssertEqual(stable(&machine, .mainTitle, LabelingClassIdentity.options, time: &time), .press(.up))
        XCTAssertEqual(stable(&machine, .mainTitle, "main-title.start-game", time: &time), .press(.z))
        for slot in 1...3 {
            XCTAssertEqual(stable(&machine, .selectProfile, "select-profile.slot-\(slot)", time: &time), .press(.down))
        }
        XCTAssertEqual(stable(&machine, .selectProfile, "select-profile.slot-4", time: &time), .archiveSlot)
        XCTAssertFalse(machine.archiveVerified)
        XCTAssertNil(stable(&machine, .selectProfile, "select-profile.clear-save-4", time: &time))
        machine.archived(hasActiveSave: true, at: time)
        XCTAssertEqual(stable(&machine, .selectProfile, "select-profile.slot-4", time: &time), .press(.right))
        XCTAssertEqual(stable(&machine, .selectProfile, "select-profile.clear-save-4", time: &time), .press(.z))
        XCTAssertEqual(stable(&machine, .clearSave, LabelingClassIdentity.no, time: &time), .press(.up))
        XCTAssertEqual(stable(&machine, .clearSave, LabelingClassIdentity.yes, time: &time), .press(.z))
        XCTAssertNil(stable(&machine, .selectProfile, "select-profile.slot-4", time: &time, occupied: [], activeSave: false))
        XCTAssertEqual(stable(&machine, .selectProfile, "select-profile.slot-4", time: &time, occupied: [], activeSave: false), .press(.z))
        XCTAssertEqual(machine.phase, .waitingForProfileExit)
        XCTAssertEqual(stable(&machine, .clearSave, LabelingClassIdentity.yes, time: &time, activeSave: false), .press(.x))
        XCTAssertEqual(machine.phase, .waitingForGameplay)
        XCTAssertEqual(stable(&machine, .brightness, "shared.brightness", time: &time, activeSave: false), .press(.z))
        XCTAssertEqual(stable(&machine, nil, nil, time: &time, gameplay: true, activeSave: true), .startBot)
        XCTAssertEqual(machine.phase, .waitingForBot)
    }

    func testOpeningIgnoresFlickeringMenusUntilFreshGameplayPersists() {
        var machine = BotDemoNavigation(), time = 100.0
        machine.begin(at: time)
        let slot = "select-profile.slot-4"
        XCTAssertEqual(stable(&machine, .selectProfile, slot, time: &time, occupied: [], activeSave: false), .archiveSlot)
        machine.archived(hasActiveSave: false, at: time)
        XCTAssertEqual(stable(&machine, .selectProfile, slot, time: &time, occupied: [], activeSave: false), .press(.z))
        time += 2
        XCTAssertEqual(stable(&machine, .selectProfile, slot, time: &time, occupied: [], activeSave: false), .press(.z))
        XCTAssertEqual(machine.phase, .waitingForProfileExit)

        let contexts: [LabelingContext?] = [.quitGame, .brightness, .selectProfile, nil]
        var buttons: [GameAutomationCommand.Button] = []
        var lastTap = -Double.infinity
        for index in 0..<800 {
            time += 0.1
            let context = contexts[index % contexts.count]
            if let effect = machine.observe(sample(context, nil, at: time), hasActiveSave: true, now: time) {
                guard case .press(let button) = effect else { return XCTFail("Unexpected cutscene effect: \(effect)") }
                XCTAssertEqual(button, buttons.count.isMultiple(of: 2) ? .x : .z)
                XCTAssertGreaterThanOrEqual(time - lastTap, 0.65 - 1e-8)
                buttons.append(button)
                lastTap = time
            }
        }
        XCTAssertGreaterThan(buttons.count, 70)
        XCTAssertEqual(machine.phase, .waitingForGameplay)

        time += 1
        let singleHUD = sample(nil, nil, at: time, gameplay: true)
        XCTAssertNil(machine.observe(singleHUD, hasActiveSave: true, now: time))
        XCTAssertNil(machine.observe(singleHUD, hasActiveSave: true, now: time + 0.25))
        time += 0.3
        XCTAssertEqual(machine.observe(sample(.brightness, nil, at: time), hasActiveSave: true, now: time),
                       .press(buttons.count.isMultiple(of: 2) ? .x : .z))
        time += 0.1
        XCTAssertNil(machine.observe(sample(nil, nil, at: time, gameplay: true), hasActiveSave: true, now: time))
        time += 0.3
        XCTAssertEqual(machine.observe(sample(nil, nil, at: time, gameplay: true), hasActiveSave: true, now: time), .startBot)
        time += 1
        XCTAssertNil(machine.observe(sample(.quitGame, nil, at: time), hasActiveSave: true, now: time))
    }

    func testGameplayAndNestedMenusReturnToTitleBeforeSelectingAProfile() {
        var machine = BotDemoNavigation(), time = 100.0
        machine.begin(at: time)
        XCTAssertEqual(stable(&machine, nil, nil, time: &time, gameplay: true), .press(.pause))
        XCTAssertEqual(stable(&machine, .pause, "pause.continue", time: &time), .press(.down))
        XCTAssertEqual(stable(&machine, .pause, LabelingClassIdentity.quitToMenu, time: &time), .press(.z))
        XCTAssertEqual(stable(&machine, .quitToMenu, LabelingClassIdentity.no, time: &time), .press(.up))
        XCTAssertEqual(stable(&machine, .quitToMenu, LabelingClassIdentity.yes, time: &time), .press(.z))
        XCTAssertNil(stable(&machine, nil, nil, time: &time))
        XCTAssertEqual(stable(&machine, .mainTitle, "main-title.start-game", time: &time), .press(.z))
        machine.begin(at: time)
        for context in [LabelingContext.audio, .options, .inventory, .credits, .clearSave, .quitGame] {
            let effect = stable(&machine, context, LabelingClassIdentity.no, time: &time)
            XCTAssertEqual(effect, .press(context == .inventory ? .inventory : .a))
        }
    }

    func testEmptySlotSkipsClearAndOccupiedFileNeverLoadsAsNewSave() {
        var machine = BotDemoNavigation(), time = 100.0
        machine.begin(at: time)
        XCTAssertEqual(stable(&machine, .selectProfile, "select-profile.slot-4", time: &time, occupied: [], activeSave: false), .archiveSlot)
        machine.archived(hasActiveSave: false, at: time)
        let result = stable(&machine, .selectProfile, "select-profile.slot-4", time: &time, occupied: [], activeSave: true)
        guard case .fail = result else { return XCTFail("An occupied save must never be loaded as fresh") }
    }

    func testStaleDuplicateAndPreStartFramesCannotAdvanceNavigation() {
        var machine = BotDemoNavigation()
        machine.begin(at: 100)
        let old = sample(.selectProfile, "select-profile.slot-4", at: 99.9)
        XCTAssertNil(machine.observe(old, hasActiveSave: true, now: 100))
        let first = sample(.selectProfile, "select-profile.slot-4", at: 100.1)
        XCTAssertNil(machine.observe(first, hasActiveSave: true, now: 100.1))
        XCTAssertNil(machine.observe(first, hasActiveSave: true, now: 100.4))
        XCTAssertNil(machine.observe(sample(.selectProfile, "select-profile.slot-4", at: 100.4), hasActiveSave: true, now: 101))
        XCTAssertFalse(machine.archiveVerified)
    }

    func testUnrecognizedTransitionTimesOutWithoutBlindConfirmations() {
        var machine = BotDemoNavigation(), time = 100.0
        machine.begin(at: time)
        XCTAssertEqual(stable(&machine, .mainTitle, "main-title.start-game", time: &time), .press(.z))
        time = 117
        let result = stable(&machine, nil, nil, time: &time)
        guard case .fail = result else { return XCTFail("Failed transitions must stop") }
    }

    func testIgnoredTitleSelectRetriesOnlyOnTheSameRecognizedMenu() {
        var machine = BotDemoNavigation(), time = 100.0
        machine.begin(at: time)
        XCTAssertEqual(stable(&machine, .mainTitle, "main-title.start-game", time: &time), .press(.z))
        time += 2
        XCTAssertEqual(stable(&machine, .mainTitle, "main-title.start-game", time: &time), .press(.z))
        XCTAssertEqual(stable(&machine, .selectProfile, "select-profile.slot-4", time: &time), .archiveSlot)
    }

    func testArchivePreservesAllOriginalFilesAndVerifiesBeforeClear() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let saves = root.appendingPathComponent("saves")
        try FileManager.default.createDirectory(at: saves, withIntermediateDirectories: true)
        for name in ["user1.dat", "user4_1.5.12620.dat", "user4.dat.bak1", "user4.modded.json", "user40.dat", "shared.dat"] {
            try Data(name.utf8).write(to: saves.appendingPathComponent(name))
        }
        let archive = BotDemoSaveArchive(savesURL: saves, rootURL: root.appendingPathComponent("archives"))
        let result = try archive.create()
        XCTAssertTrue(result.hadActiveSave)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: saves.path).count, 6)
        let manifest = try JSONDecoder().decode(BotDemoSaveArchive.Manifest.self,
            from: Data(contentsOf: result.url.appendingPathComponent("manifest.json")))
        XCTAssertEqual(Set(manifest.files.keys), ["user4_1.5.12620.dat", "user4.dat.bak1", "user4.modded.json"])
        XCTAssertNoThrow(try archive.verifySourceMatchesArchive(at: result.url))
        let another = try archive.create()
        XCTAssertNotEqual(result.url, another.url)
        try Data("changed".utf8).write(to: saves.appendingPathComponent("user4_1.5.12620.dat"))
        XCTAssertThrowsError(try archive.verifySourceMatchesArchive(at: result.url))
    }

    func testArchiveRejectsSymlinks() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let saves = root.appendingPathComponent("saves")
        try FileManager.default.createDirectory(at: saves, withIntermediateDirectories: true)
        let target = root.appendingPathComponent("other.dat")
        try Data("original".utf8).write(to: target)
        try FileManager.default.createSymbolicLink(at: saves.appendingPathComponent("user4.dat"), withDestinationURL: target)
        XCTAssertThrowsError(try BotDemoSaveArchive(savesURL: saves, rootURL: root.appendingPathComponent("archives")).create())
        XCTAssertEqual(try Data(contentsOf: target), Data("original".utf8))
    }

    func testSaveFamiliesFollowLiveProfileOccupancyAndArchiveEveryVersion() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let saves = root.appendingPathComponent("saves")
        try FileManager.default.createDirectory(at: saves, withIntermediateDirectories: true)
        for slot in 1...4 {
            try Data("newer version \(slot)".utf8).write(to: saves.appendingPathComponent("user\(slot)_1.5.12620.dat"))
        }
        try Data("current game".utf8).write(to: saves.appendingPathComponent("user1.dat"))
        let archive = BotDemoSaveArchive(savesURL: saves, rootURL: root.appendingPathComponent("archives"))
        XCTAssertFalse(try archive.hasActiveSave(matching: [1]))
        let copy = try archive.create(activeSavePresent: false)
        XCTAssertFalse(copy.hadActiveSave)
        XCTAssertTrue(FileManager.default.fileExists(atPath: copy.url.appendingPathComponent("user4_1.5.12620.dat").path))
        try Data("fresh slot 4".utf8).write(to: saves.appendingPathComponent("user4.dat"))
        XCTAssertTrue(try archive.hasActiveSave(matching: [1, 4]))
        try FileManager.default.removeItem(at: saves.appendingPathComponent("user4.dat"))
        XCTAssertFalse(try archive.hasActiveSave(matching: [1]))
        XCTAssertThrowsError(try archive.hasActiveSave(matching: [1, 2]))
    }

    func testObservationUsesFreshStencilSelectionAndOccupiedSlotEvidence() {
        func menu(timestamp: Double, match: Bool = true) -> MenuStencilResult {
            MenuStencilResult(context: .selectProfile, isMatch: match, confidence: 0.95,
                selectedOption: "Clear Save 4", anchors: [
                    .init(classIdentifier: "select-profile.clear-save", name: "Clear Save 4",
                          rect: .zero, confidence: 0.95, reference: nil)
                ], selectorCandidates: [], selectorSearchRegions: [], selectorLanguageEvidenceCount: 2,
                selectorForegroundEvidenceCount: 2, sceneEvidenceRatio: 1, phase: .tracking,
                comparisonCount: 1, sourceTimestamp: timestamp, languageIdentifier: "en")
        }
        let fresh = BotDemoMenuObservation.make(menu: menu(timestamp: 100), freshGameplay: false,
            frameTimestamp: 100.1, capturedAt: 200, availableAt: 200.1, generation: 1)
        XCTAssertEqual(fresh.selectedIdentifier, "select-profile.clear-save-4")
        XCTAssertEqual(fresh.occupiedSlots, [4])
        XCTAssertEqual(fresh.frameTimestamp, 100.1)
        XCTAssertEqual(fresh.menuTimestamp, 100)
        for value in [menu(timestamp: 99), menu(timestamp: 100, match: false)] {
            let ignored = BotDemoMenuObservation.make(menu: value, freshGameplay: false,
                frameTimestamp: 100.1, capturedAt: 200, availableAt: 200.1, generation: 1)
            XCTAssertNil(ignored.context); XCTAssertNil(ignored.selectedIdentifier)
            XCTAssertTrue(ignored.occupiedSlots.isEmpty)
        }
    }

    func testConfirmationNavigationFollowsMatchedAnchorGeometry() {
        for (yesRect, noRect, expected) in [
            (CGRect(x: 40, y: 100, width: 20, height: 10), CGRect(x: 40, y: 60, width: 20, height: 10), GameAutomationCommand.Button.up),
            (CGRect(x: 20, y: 60, width: 20, height: 10), CGRect(x: 80, y: 60, width: 20, height: 10), .left),
        ] {
            let menu = MenuStencilResult(context: .quitToMenu, isMatch: true, confidence: 1,
                selectedOption: "No", anchors: [
                    .init(classIdentifier: LabelingClassIdentity.yes, name: "Yes", rect: yesRect, confidence: 1, reference: nil),
                    .init(classIdentifier: LabelingClassIdentity.no, name: "No", rect: noRect, confidence: 1, reference: nil),
                ], selectorCandidates: [], selectorSearchRegions: [], selectorLanguageEvidenceCount: 2,
                selectorForegroundEvidenceCount: 2, sceneEvidenceRatio: 1, phase: .tracking,
                comparisonCount: 1, sourceTimestamp: 100, languageIdentifier: "en")
            let observation = BotDemoMenuObservation.make(menu: menu, freshGameplay: false,
                frameTimestamp: 100.1, capturedAt: 100.1, availableAt: 100.1, generation: 1)
            XCTAssertEqual(observation.directionToYes, expected)
        }
    }
}
