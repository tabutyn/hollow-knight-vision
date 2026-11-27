import XCTest
@testable import HollowKnightVision

final class BotDemoControllerTests: XCTestCase {
    func testStartHelpFollowsCurrentBlockerAndOffersStopDuringPlayback() {
        func issue(_ mode: HollowKnightRuntimeMode = .modded, capturing: Bool = true,
                   training: Bool = false, stencils: Bool = true, controls: Bool = true) -> String? {
            BotControlPresentationPolicy.startIssue(mode: mode, capturing: capturing, training: training,
                                                   stencilsReady: stencils, controlsReady: controls)
        }
        XCTAssertEqual(issue(.normal, capturing: false), "Choose Modded game mode to start the bot.")
        XCTAssertEqual(issue(capturing: false, training: true), "Start a modded game capture before starting the bot.")
        XCTAssertEqual(issue(training: true, stencils: false), "Wait for training to finish before starting the bot.")
        let stencilIssue = issue(stencils: false, controls: false)
        XCTAssertEqual(stencilIssue, "Complete stencil setup before starting the bot.")
        XCTAssertEqual(BotControlPresentationPolicy.help(active: false, startIssue: stencilIssue, lastIssue: "Old error"), stencilIssue)
        XCTAssertEqual(BotControlPresentationPolicy.help(active: true, startIssue: stencilIssue, lastIssue: "Old error"), "Press B to stop the bot.")
        XCTAssertTrue(issue(controls: false)?.contains("recording") == true)
        XCTAssertNil(issue())
        XCTAssertEqual(BotControlPresentationPolicy.help(active: false, startIssue: nil, lastIssue: "Archive failed"), "Archive failed")
        XCTAssertTrue(BotControlPresentationPolicy.help(active: false, startIssue: nil, lastIssue: nil).contains("archives slot 4"))
    }

    @MainActor
    func testStartRejectionMatchesButtonHelpBeforeFirstCapture() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = LiveCaptureModel(gameMode: .modded, visualReplayMode: true,
                                     worldRootOverride: root.appendingPathComponent("world"))
        XCTAssertNil(model.botDemo.issue)
        let issue = try XCTUnwrap(model.botDemoStartIssue())
        XCTAssertFalse(model.canStartBotDemo)
        let help = BotControlPresentationPolicy.help(active: false, startIssue: issue, lastIssue: model.botDemo.issue)
        model.startBotDemo()
        XCTAssertEqual(model.botDemo.issue, help)
        XCTAssertFalse(model.botDemo.isActive)
    }

    func testActiveControlCanStopAndReleaseItsSessionWhileAStartIsBlocked() {
        let demo = BotDemoController(hasFocus: { true })
        let session = UUID()
        var released: [UUID] = []
        demo.configure(canContinue: { true }, begin: { session }, press: { _, _ in true },
                       end: { released.append($0) }, botIsPlaying: { false })
        XCTAssertTrue(BotControlPresentationPolicy.canToggle(active: demo.isActive, startIssue: nil))
        demo.start()
        let issue = BotControlPresentationPolicy.startIssue(mode: .modded, capturing: true, training: true,
                                                           stencilsReady: true, controlsReady: true)
        XCTAssertNotNil(issue)
        XCTAssertTrue(BotControlPresentationPolicy.canToggle(active: demo.isActive, startIssue: issue))
        XCTAssertEqual(BotControlPresentationPolicy.help(active: demo.isActive, startIssue: issue, lastIssue: demo.issue), "Press B to stop the bot.")
        demo.stop(reason: "Stopped with B")
        XCTAssertEqual(released, [session])
        XCTAssertFalse(BotControlPresentationPolicy.canToggle(active: demo.isActive, startIssue: issue))
    }

    func testBFromGameplayHandsOffWithoutArchivingOrMenuNavigation() throws {
        var playing = false, presses = 0, releases = 0
        let demo = BotDemoController(now: { 100 }, hasFocus: { true })
        demo.configure(canContinue: { true }, begin: { UUID() }, press: { _, _ in presses += 1; return true },
            end: { _ in releases += 1 }, botIsPlaying: { playing })
        demo.start(inGameplay: true)
        XCTAssertTrue(demo.isHandingOff); XCTAssertEqual(presses, 0); XCTAssertEqual(releases, 1)
        XCTAssertNil(demo.lastArchiveURL)
        let request = try XCTUnwrap(demo.handoffRequest)
        demo.completeHandoff(request) { playing = true; return true }
        XCTAssertTrue(demo.isPlaying)
        demo.stop(reason: "Stopped with B")
        XCTAssertFalse(demo.isActive)
    }
    func testUntrainedHandoffReleasesControlsAndReportsManualPlay() throws {
        var releases = 0
        let demo = BotDemoController(now: { 100 }, hasFocus: { true })
        demo.configure(canContinue: { true }, begin: { UUID() }, press: { _, _ in true },
            end: { _ in releases += 1 }, botIsPlaying: { false })
        demo.start(inGameplay: true, domain: .gameplay)
        let request = try XCTUnwrap(demo.handoffRequest)
        demo.finishWithoutPolicy(UUID())
        XCTAssertTrue(demo.isActive)
        demo.finishWithoutPolicy(request)
        XCTAssertFalse(demo.isActive)
        XCTAssertFalse(demo.isPlaying)
        XCTAssertNil(demo.handoffRequest)
        XCTAssertEqual(releases, 1)
        XCTAssertTrue(demo.status.contains("Train or select"))
        XCTAssertEqual(demo.issue, demo.status)
    }

    func testStartFailureIsVisibleAndDoesNotAcquireControls() {
        var acquisitions = 0
        let unfocused = BotDemoController(now: { 100 }, hasFocus: { false })
        unfocused.configure(canContinue: { true }, begin: { acquisitions += 1; return UUID() },
            press: { _, _ in true }, end: { _ in }, botIsPlaying: { false })
        unfocused.start()
        XCTAssertFalse(unfocused.isActive)
        XCTAssertEqual(unfocused.issue, "Focus the Vision window to start the bot.")
        XCTAssertEqual(unfocused.status, unfocused.issue)
        let unavailable = BotDemoController(now: { 100 }, hasFocus: { true })
        unavailable.start()
        XCTAssertFalse(unavailable.isActive)
        XCTAssertNotNil(unavailable.issue)
        XCTAssertEqual(acquisitions, 0)
    }

    private func observe(_ controller: BotDemoController, context: LabelingContext?, selected: String?,
                         at time: Double, gameplay: Bool = false, occupied: Set<Int> = []) {
        controller.observe(.init(context: context, selectedIdentifier: selected, gameplay: gameplay,
            occupiedSlots: occupied, frameTimestamp: time, capturedAt: time, availableAt: time, generation: 1))
    }
    private func drain(until predicate: @escaping () -> Bool) {
        let done = expectation(description: "Asynchronous Demo stage")
        let timer = Timer.scheduledTimer(withTimeInterval: 0.01, repeats: true) { timer in
            if predicate() { timer.invalidate(); done.fulfill() }
        }
        wait(for: [done], timeout: 2)
        timer.invalidate()
    }

    func testFreshGameHandoffReleasesMenuOwnershipAndRequiresMatchingRequest() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let saves = root.appendingPathComponent("saves")
        try FileManager.default.createDirectory(at: saves, withIntermediateDirectories: true)
        var time = 100.0, botPlaying = false
        let session = UUID()
        var presses = [GameAutomationCommand.Button](), ended = [UUID]()
        let demo = BotDemoController(archive: .init(savesURL: saves, rootURL: root.appendingPathComponent("archives")),
            now: { time }, hasFocus: { true })
        demo.configure(canContinue: { true }, begin: { session }, press: { button, id in
            XCTAssertEqual(id, session); presses.append(button); return true
        }, end: { ended.append($0) }, botIsPlaying: { botPlaying })
        var deliveredRequests = [UUID]()
        let handoffSubscription = demo.handoffRequests.sink { request in
            XCTAssertEqual(demo.handoffRequest, request, "The UI must see an already-stored token")
            deliveredRequests.append(request)
        }
        defer { handoffSubscription.cancel() }
        demo.start()
        observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time)
        time += 0.3; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time)
        drain { demo.lastArchiveURL != nil }
        time += 0.4; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time)
        time += 0.3; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time)
        XCTAssertEqual(presses, [.z])
        time += 0.7; observe(demo, context: .quitGame, selected: LabelingClassIdentity.yes, at: time)
        time += 0.7; observe(demo, context: .brightness, selected: nil, at: time)
        time += 0.7; observe(demo, context: .selectProfile, selected: "select-profile.clear-save-4", at: time, occupied: [1, 2, 4])
        XCTAssertEqual(presses, [.z, .x, .z, .x])
        XCTAssertNil(demo.handoffRequest)
        time += 0.7; observe(demo, context: nil, selected: nil, at: time, gameplay: true)
        time += 0.3; observe(demo, context: nil, selected: nil, at: time, gameplay: true)
        XCTAssertEqual(presses, [.z, .x, .z, .x])
        let request = try XCTUnwrap(demo.handoffRequest)
        drain { deliveredRequests.count == 1 }
        XCTAssertEqual(deliveredRequests, [request])
        XCTAssertEqual(ended, [session]); XCTAssertTrue(demo.isHandingOff)
        demo.completeHandoff(UUID()) { XCTFail("Stale handoff must not start bot"); return true }
        demo.completeHandoff(request) { botPlaying = true; return true }
        XCTAssertTrue(demo.isPlaying)
        demo.interrupted(sessionID: session, reason: "Delayed menu release")
        XCTAssertTrue(demo.isActive)
        demo.stop()
        XCTAssertFalse(demo.isActive); XCTAssertEqual(ended.count, 1)
    }

    func testArchiveFailureStopsWithoutSendingClear() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        var time = 100.0, presses = 0, releases = 0
        let demo = BotDemoController(archive: .init(savesURL: root.appendingPathComponent("missing"), rootURL: root),
            now: { time }, hasFocus: { true })
        demo.configure(canContinue: { true }, begin: { UUID() }, press: { _, _ in presses += 1; return true },
            end: { _ in releases += 1 }, botIsPlaying: { false })
        demo.start()
        observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time)
        time += 0.3; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time)
        XCTAssertFalse(demo.isActive); XCTAssertNotNil(demo.issue)
        XCTAssertEqual(presses, 0); XCTAssertEqual(releases, 1)
    }

    func testFocusLossCancelsRoutineAndReleasesOwnership() {
        var focused = true, releases = 0
        let demo = BotDemoController(now: { 100 }, hasFocus: { focused })
        demo.configure(canContinue: { true }, begin: { UUID() }, press: { _, _ in true },
            end: { _ in releases += 1 }, botIsPlaying: { false })
        demo.start(); focused = false
        drain { !demo.isActive }
        XCTAssertEqual(releases, 1)
        demo.stop(); XCTAssertEqual(releases, 1)
    }

    func testForeignVersionSaveDoesNotBlockAnEmptySlotAndPartialStencilsWait() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let saves = root.appendingPathComponent("saves")
        try FileManager.default.createDirectory(at: saves, withIntermediateDirectories: true)
        try Data("current slot one".utf8).write(to: saves.appendingPathComponent("user1.dat"))
        try Data("different game version".utf8).write(to: saves.appendingPathComponent("user4_1.5.12620.dat"))
        var time = 100.0
        var presses = [GameAutomationCommand.Button]()
        let demo = BotDemoController(archive: .init(savesURL: saves, rootURL: root.appendingPathComponent("archives")),
            now: { time }, hasFocus: { true })
        demo.configure(canContinue: { true }, begin: { UUID() }, press: { button, _ in presses.append(button); return true },
            end: { _ in }, botIsPlaying: { false })
        demo.start()
        observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time, occupied: [])
        XCTAssertTrue(demo.isActive); XCTAssertNil(demo.issue)
        time += 0.3; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time, occupied: [1])
        time += 0.3; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time, occupied: [1])
        drain { demo.lastArchiveURL != nil }
        time += 0.4; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time, occupied: [1])
        time += 0.3; observe(demo, context: .selectProfile, selected: "select-profile.slot-4", at: time, occupied: [1])
        XCTAssertEqual(presses, [.z])
        let manifest = try JSONDecoder().decode(BotDemoSaveArchive.Manifest.self,
            from: Data(contentsOf: XCTUnwrap(demo.lastArchiveURL).appendingPathComponent("manifest.json")))
        XCTAssertFalse(manifest.hadActiveSave)
        XCTAssertEqual(manifest.files.count, 1)
        demo.stop()
    }
}
