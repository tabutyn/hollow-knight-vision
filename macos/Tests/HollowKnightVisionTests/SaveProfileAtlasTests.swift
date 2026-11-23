import Combine
import CoreImage
import XCTest
@testable import HollowKnightVision

final class SaveProfileAtlasTests: XCTestCase {
    private func frame(_ time: Double, _ slot: Int? = nil, occupied: Set<Int> = [],
                       checked: Set<Int> = [1, 2, 3, 4], context: LabelingContext? = .selectProfile,
                       hud: Bool = false, generation: UInt64 = 1, selected: String? = nil) -> BotDemoMenuObservation {
        .init(context: context, selectedIdentifier: selected ?? slot.map { "select-profile.slot-\($0)" }, gameplay: hud,
              occupiedSlots: occupied, frameTimestamp: time, capturedAt: time, availableAt: time,
              generation: generation, menuTimestamp: context == nil ? nil : time, checkedProfileSlots: checked)
    }

    func testBrowsingThenStartingAnEmptyCardCreatesExactlyOneLaunchForTheLastSlot() {
        for slot in 1...4 {
            var detector = ProfileLaunchDetector()
            for time in [0.0, 0.2] { XCTAssertNil(detector.observe(frame(time, 1))) }
            for time in [1.0, 1.2] { XCTAssertNil(detector.observe(frame(time, slot))) }
            XCTAssertTrue(detector.awaitingGameplay)
            XCTAssertNil(detector.observe(frame(30, context: nil))) // opening movie
            XCTAssertNil(detector.observe(frame(60, context: nil, hud: true)))
            XCTAssertEqual(detector.observe(frame(60.2, context: nil, hud: true)), .init(slot: slot, isNew: true))
            XCTAssertNil(detector.observe(frame(61, context: nil, hud: true)))
        }
    }

    func testOccupiedCardAndClearConfirmation() {
        var detector = ProfileLaunchDetector()
        XCTAssertNil(detector.observe(frame(1, 4, occupied: [4])))
        XCTAssertNil(detector.observe(frame(1.2, 4, occupied: [4])))
        XCTAssertNil(detector.observe(frame(1.4, 4))) // disappearing text during fade
        XCTAssertNil(detector.observe(frame(2, context: nil, hud: true)))
        XCTAssertEqual(detector.observe(frame(2.2, context: nil, hud: true)), .init(slot: 4, isNew: false))
        _ = detector.observe(frame(3, 4, occupied: [4]))
        _ = detector.observe(frame(3.2, 4, occupied: [4]))
        _ = detector.observe(frame(4, context: .clearSave))
        _ = detector.observe(frame(5, 4, occupied: [1, 2]))
        _ = detector.observe(frame(5.2, 4, occupied: [1, 2]))
        _ = detector.observe(frame(6, context: nil, hud: true))
        XCTAssertEqual(detector.observe(frame(6.2, context: nil, hud: true)), .init(slot: 4, isNew: true))
    }

    func testBackStaleFramesUnsearchedCardsAndCaptureChangesCannotCreateANewAtlas() {
        var detector = ProfileLaunchDetector()
        for time in [1.0, 1.2] { _ = detector.observe(frame(time, 4, checked: [])) }
        XCTAssertFalse(detector.awaitingGameplay)
        for _ in 0..<5 { _ = detector.observe(frame(2, 4)) }
        XCTAssertFalse(detector.awaitingGameplay)
        _ = detector.observe(frame(2.2, 4))
        _ = detector.observe(frame(2.3, selected: LabelingClassIdentity.back))
        XCTAssertFalse(detector.awaitingGameplay)
        for time in [3.0, 3.2] { _ = detector.observe(frame(time, 3)) }
        _ = detector.observe(frame(4, context: .mainTitle))
        XCTAssertFalse(detector.awaitingGameplay)
        for time in [5.0, 5.2] { _ = detector.observe(frame(time, 3)) }
        _ = detector.observe(frame(6, context: nil, hud: true, generation: 2))
        XCTAssertNil(detector.observe(frame(6.2, context: nil, hud: true, generation: 2)))
    }

    // Opt-in replay of a labeled profile screenshot with slot 1 occupied and slot 4 empty.
    // Only move the two selection decorations. Slot artwork and Clear Save evidence stay original.
    func testProfileStencilThroughGameplayOnReferenceFrame() throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_PROFILE_ATLAS_REFERENCE"] else {
            throw XCTSkip("Provide a reference profile screenshot directory to replay")
        }
        let directory = URL(fileURLWithPath: path)
        let store = LabelingExampleStore(rootURL: directory.deletingLastPathComponent())
        let example = try XCTUnwrap(store.loadExamples().first { $0.directoryURL.lastPathComponent == directory.lastPathComponent })
        let source = try store.loadImage(for: example)
        let width = CGFloat(source.width), height = CGFloat(source.height)
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: source.width, height: source.height,
            bitsPerComponent: 8, bytesPerRow: source.width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        bitmap.draw(source, in: CGRect(x: 0, y: 0, width: width, height: height))
        func rect(_ label: LabelingExampleAnnotation) -> CGRect {
            CGRect(x: label.x * width, y: label.y * height, width: label.width * width, height: label.height * height)
        }
        let labels = example.manifest.annotations
        let first = try XCTUnwrap(labels.first { $0.classIdentifier == "select-profile.slot-1" })
        let fourth = try XCTUnwrap(labels.first { $0.classIdentifier == "select-profile.slot-4" })
        let offset = (rect(fourth).midY - rect(first).midY).rounded()
        for selector in labels where selector.classIdentifier == LabelingClassIdentity.selectDecoration {
            let top = rect(selector).insetBy(dx: -3, dy: -3).integral
            let cursor = try XCTUnwrap(source.cropping(to: top))
            let bottom = CGRect(x: top.minX, y: height - top.maxY, width: top.width, height: top.height)
            bitmap.setFillColor(CGColor(gray: 0.05, alpha: 1))
            bitmap.fill(bottom)
            bitmap.draw(cursor, in: bottom.offsetBy(dx: 0, dy: -offset))
        }
        let fourthFrame = try XCTUnwrap(bitmap.makeImage())
        let catalog = try MenuStencilCatalog(examplesRootURL: store.rootURL, calibration: .loadDefault())
        let tracker = MenuStencilTracker(catalog: catalog)
        var detector = ProfileLaunchDetector()
        for (index, image) in [source, source, fourthFrame, fourthFrame, fourthFrame].enumerated() {
            let t = Double(index) * 0.25
            let menu = try XCTUnwrap(tracker.observe(image, timestamp: t))
            let input = BotDemoMenuObservation.make(menu: menu, freshGameplay: false, frameTimestamp: t,
                                                   capturedAt: t, availableAt: t, generation: 1)
            XCTAssertEqual(input.checkedProfileSlots, [1, 2, 3, 4])
            XCTAssertEqual(input.occupiedSlots, [1])
            XCTAssertEqual(input.selectedIdentifier, index < 2 ? "select-profile.slot-1" : "select-profile.slot-4")
            XCTAssertNil(detector.observe(input))
        }
        let examples = try store.loadExamples()
        let hudExample = try XCTUnwrap(examples.first { $0.manifest.contextIdentifier == "gameplay-hud" })
        let gameplay = try store.loadImage(for: hudExample)
        let hudTracker = HUDStencilTracker(template: HUDStencilTemplate.captured(examples: examples, languageIdentifier: "en"))
        let knight = LiveObjectDetection(
            classIdentifier: "game.playable-knight",
            normalizedRect: CGRect(x: 0.5, y: 0.4, width: 0.05, height: 0.1),
            confidence: 0.9, sourceFrameIdentifier: 1,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )
        for t in [70.0, 70.2] {
            let hud = try XCTUnwrap(hudTracker.observe(gameplay, gameplay: true, timestamp: t, generation: 1))
            let fresh = hud.establishesGameplay && LiveGameStatusResolver.resolve([knight], hudStencil: hud)?.context == .gameplay
            XCTAssertTrue(fresh)
            let input = BotDemoMenuObservation.make(menu: nil, freshGameplay: fresh, frameTimestamp: t,
                                                    capturedAt: t, availableAt: t, generation: 1)
            XCTAssertEqual(detector.observe(input), t == 70 ? nil : .init(slot: 4, isNew: true))
        }
    }

    @MainActor
    func testCaptureModelSwitchesBothActiveWritersAndKeepsAnUnloadedHackerAutosave() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let world = root.appendingPathComponent("live-world-v3")
        let hackerRoot = root.appendingPathComponent("hacker-atlas-v1")
        let hackerActive = hackerRoot.appendingPathComponent("active")
        var previous = HackerAtlasArchive.empty
        previous.profile = SaveProfileAtlas(slot: 1)
        try HackerAtlasArchiveStore.write(previous, to: hackerActive)
        let model = LiveCaptureModel(visualReplayMode: true, worldRootOverride: world)
        let ready = expectation(description: "Both profile autosaves ready")
        let changes = model.$hackerAtlasAutoSave.sink { if $0.profile?.slot == 4 { ready.fulfill() } }
        model.switchToProfileAtlas(.init(slot: 4, isNew: true))
        await fulfillment(of: [ready], timeout: 15)
        changes.cancel()
        let identifier = try XCTUnwrap(model.atlasAutoSave.profile)
        XCTAssertEqual(identifier.name, "Atlas4")
        XCTAssertEqual(model.notebookProfile, identifier)
        XCTAssertEqual(model.hackerAtlasAutoSave.profile, identifier)
        XCTAssertEqual(try SaveProfileAtlas.read(from: world), identifier)
        XCTAssertEqual(try HackerAtlasArchiveStore.read(from: hackerActive).profile, identifier)
        XCTAssertEqual(model.atlasAutoSave.registrationCount, 0)
        XCTAssertEqual(model.hackerAtlasAutoSave.registrationCount, 0)
        let library = AtlasStateStore(rootURL: hackerRoot.appendingPathComponent("states"))
        let archived = try XCTUnwrap(library.list().first)
        XCTAssertEqual(archived.profile, previous.profile)
        XCTAssertEqual(try HackerAtlasArchiveStore.read(from: library.worldURL(for: archived)).createdAt, previous.createdAt)
    }

    func testResumingLegacySaveAdoptsTheExistingMapWithoutAnArchiveOrReset() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let world = root.appendingPathComponent("active")
        let library = AtlasStateStore(rootURL: root.appendingPathComponent("states"))
        let pipeline = LiveAtlasPipeline(context: CIContext(), worldRootURL: world)
        let marker = world.appendingPathComponent("visual-rooms.json")
        let legacy = Data("legacy topology".utf8)
        try legacy.write(to: marker)
        let profile = SaveProfileAtlas.target(for: .init(slot: 2, isNew: false), active: nil, saved: [])
        XCTAssertNil(try pipeline.activateProfile(profile, in: library, adoptingUnassigned: true))
        XCTAssertEqual(pipeline.activeAutoSaveSummary().profile, profile)
        XCTAssertEqual(try Data(contentsOf: marker), legacy)
        var hacker = HackerAtlasArchive.empty
        let date = hacker.createdAt
        hacker = try XCTUnwrap(hacker.switching(to: profile, library: library, adoptingUnassigned: true))
        XCTAssertEqual(hacker.createdAt, date)
        XCTAssertEqual(hacker.profile, profile)
        XCTAssertTrue(try library.list().isEmpty)
    }

    func testFreshSlotBecomesDurableAutosaveAndReturningToAnotherSlotRestoresItsOwnData() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let active = root.appendingPathComponent("live-world")
        let library = AtlasStateStore(rootURL: root.appendingPathComponent("states"))
        let pipeline = LiveAtlasPipeline(context: CIContext(), worldRootURL: active)
        func activate(_ profile: SaveProfileAtlas) throws {
            if let archive = try pipeline.activateProfile(profile, in: library)?.archivedPreviousWorldURL {
                _ = try library.importArchive(at: archive, name: "Prior Atlas")
            }
        }
        func start(_ slot: Int, new: Bool) throws -> SaveProfileAtlas? {
            SaveProfileAtlas.target(for: .init(slot: slot, isNew: new), active: try SaveProfileAtlas.read(from: active),
                                    saved: try library.list())
        }
        let old = Data("prior world".utf8)
        let evidence = active.appendingPathComponent("visual-rooms.json")
        try old.write(to: evidence)
        let four = try XCTUnwrap(start(4, new: true))
        try activate(four)
        XCTAssertEqual(pipeline.activeAutoSaveSummary().profile?.name, "Atlas4")
        XCTAssertFalse(FileManager.default.fileExists(atPath: evidence.path))
        let legacy = try XCTUnwrap(library.list().first)
        XCTAssertEqual(try Data(contentsOf: library.worldURL(for: legacy).appendingPathComponent("visual-rooms.json")), old)
        try Data("four".utf8).write(to: evidence)
        let two = try XCTUnwrap(start(2, new: true))
        try activate(two)
        XCTAssertEqual(try start(4, new: false), four)
        try activate(four)
        XCTAssertEqual(try Data(contentsOf: evidence), Data("four".utf8))
        XCTAssertNil(try pipeline.activateProfile(four, in: library))
        let restarted = try XCTUnwrap(start(4, new: true))
        XCTAssertNotEqual(restarted, four)
        try activate(restarted)
        XCTAssertFalse(FileManager.default.fileExists(atPath: evidence.path))
        let reopened = LiveAtlasPipeline(context: CIContext(), worldRootURL: active)
        XCTAssertEqual(reopened.activeAutoSaveSummary().profile, restarted)

        let hackerLib = AtlasStateStore(rootURL: root.appendingPathComponent("hacker-states"))
        let path = root.appendingPathComponent("hacker-active")
        var hacker = try XCTUnwrap(HackerAtlasArchive.empty.switching(to: four, library: hackerLib))
        let created = hacker.createdAt
        try HackerAtlasArchiveStore.write(hacker, to: path)
        hacker = try HackerAtlasArchiveStore.read(from: path)
        XCTAssertEqual(hacker.profile, four)
        hacker = try XCTUnwrap(hacker.switching(to: two, library: hackerLib))
        hacker = try XCTUnwrap(hacker.switching(to: four, library: hackerLib))
        XCTAssertEqual(hacker.createdAt, created)
        XCTAssertNil(try hacker.switching(to: four, library: hackerLib))
        hacker = try XCTUnwrap(hacker.switching(to: restarted, library: hackerLib))
        XCTAssertEqual(hacker.autoSaveSummary.profile, restarted)
        XCTAssertEqual(hacker.registrationCount, 0)
    }
}
