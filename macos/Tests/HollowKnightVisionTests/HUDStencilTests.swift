import CoreGraphics
import CoreImage
import Foundation
import ImageIO
import ScreenCaptureKit
import XCTest
@testable import HollowKnightVision

// Photographic reference pixels belong to the test target, never the app.
enum HUDStencilTestReference {
    static func load() throws -> HUDStencilTemplate {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "hud-stencil-reference", withExtension: "json"))
        return try JSONDecoder().decode(HUDStencilTemplate.self, from: Data(contentsOf: url))
    }
}

final class HUDStencilTests: XCTestCase {
    func testCurrentMenuCapturesDoNotSupplyAtlasPathEvidenceWhenRequested() throws {
        guard ProcessInfo.processInfo.environment["HKV_CURRENT_STENCIL_AUDIT"] == "1" else {
            throw XCTSkip("Opt-in audit of the user's captured menu and HUD references")
        }
        let store = LabelingExampleStore()
        let examples = try store.loadExamples()
        let template = try XCTUnwrap(HUDStencilTemplate.captured(examples: examples, languageIdentifier: "en"))
        let recentKnight = LiveObjectDetection(classIdentifier: "game.playable-knight",
            normalizedRect: CGRect(x: 0.5, y: 0.4, width: 0.05, height: 0.1), confidence: 0.9,
            sourceFrameIdentifier: 1, modelVersion: .init(major: 1, minor: 0))
        for context in [LabelingContext.pause, .inventory] {
            let example = try XCTUnwrap(examples.first { $0.manifest.contextIdentifier == context.storageIdentifier })
            let image = try store.loadImage(for: example)
            let hud = HUDStencilTracker(template: template).observe(image, gameplay: true, timestamp: 1, generation: 1)
            let contextOnFrame = LiveGameStatusResolver.resolve([recentKnight], hudStencil: hud)?.context
            XCTAssertFalse(LiveAtlasPresentationPolicy.admitsPathFrame(
                hasSceneSignal: true, recognizedContext: contextOnFrame
            ), "\(context.rawValue) HUD matches: \(hud?.health.count ?? 0)")
        }
    }

    func testCurrentCapturedHUDRecognizesLowHealthLifebloodWhenRequested() throws {
        guard ProcessInfo.processInfo.environment["HKV_CURRENT_STENCIL_AUDIT"] == "1" else {
            throw XCTSkip("Opt-in audit of the user's captured health reference")
        }
        let template = try XCTUnwrap(HUDStencilTemplate.captured(examples: LabelingExampleStore().loadExamples(),
            languageIdentifier: "en"))
        for fixture in try fixtures() where (fixture.health?.first == 1 && (fixture.health?.last ?? 0) > 0) {
            let result = try XCTUnwrap(HUDStencilTracker(template: template).observe(try frame(fixture),
                gameplay: true, timestamp: 1, generation: 1))
            XCTAssertTrue(result.establishesGameplay,
                "\(fixture.id): \(result.health.map { ($0.index, $0.kind, $0.confidence) })")
        }
    }

    func testFilledOnlyReferenceRecognizesLifebloodAfterUnlabeledEmptyMasks() throws {
        let template = try HUDStencilTestReference.load()
        let captured = HUDStencilTemplate(schemaVersion: template.schemaVersion, sourceExampleID: "filled-only",
            referenceWidth: template.referenceWidth, referenceHeight: template.referenceHeight,
            healthPitch: template.healthPitch, health: template.health.filter { $0.kind == "full" },
            manaMain: [], manaContainer: [0, 0, 0, 0], manaReserves: [], geo: [0, 0, 0, 0])
        for fixture in try fixtures() where (fixture.health?.first == 1 && (fixture.health?.last ?? 0) > 0) {
            let result = try XCTUnwrap(HUDStencilTracker(template: captured).observe(try frame(fixture),
                gameplay: true, timestamp: 1, generation: 1))
            XCTAssertTrue(result.establishesGameplay, "\(fixture.id): \(result.health.map { ($0.index, $0.kind, $0.confidence) })")
            XCTAssertTrue(result.health.contains { $0.kind == "lifeblood" }, fixture.id)
            XCTAssertFalse(result.health.contains { $0.kind == "empty" }, "Missing references must not invent empty-mask counts.")
        }
        XCTAssertFalse(try XCTUnwrap(HUDStencilTracker(template: captured).observe(
            context().makeImage()!, gameplay: true, timestamp: 1, generation: 1)).establishesGameplay)
    }

    private struct Fixture: Decodable {
        let id: String
        let width: Int, height: Int
        let hudPNG: Data
        let health: [Int]?
        let mana: Int?
    }
    private func fixtures() throws -> [Fixture] {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "hud-stencil-cases", withExtension: "json"))
        return try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: url))
    }
    private func frame(_ fixture: Fixture, scale: Int = 1) throws -> CGImage {
        let source = try XCTUnwrap(CGImageSourceCreateWithData(fixture.hudPNG as CFData, nil))
        let hud = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        let c = try context(width: fixture.width * scale, height: fixture.height * scale)
        c.interpolationQuality = .none
        c.draw(hud, in: CGRect(x: 0, y: (fixture.height - hud.height) * scale,
                              width: hud.width * scale, height: hud.height * scale))
        return try XCTUnwrap(c.makeImage())
    }
    private func context(width: Int = 640, height: Int = 360) throws -> CGContext {
        try XCTUnwrap(CGContext(data: nil, width: width, height: height,
            bitsPerComponent: 8, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    }
    private func counts(_ result: HUDStencilResult) -> [Int] {
        ["full", "empty", "lifeblood"].map { kind in result.health.filter { $0.kind == kind }.count }
    }

    func testNoRecognitionWithoutCapturedStencil() throws {
        let tracker = HUDStencilTracker()
        for fixture in try fixtures() {
            XCTAssertNil(tracker.observe(try frame(fixture), gameplay: true, timestamp: 1, generation: 1))
        }
        XCTAssertNil(HUDStencilTemplate.captured(examples: [], languageIdentifier: "en"))
    }

    func testRealGameplayHUDsAcrossCountsBackgroundsAndOffsets() throws {
        XCTAssertTrue(try XCTUnwrap(HUDStencilTestReference.load()).isValid)
        for fixture in try fixtures() {
            let image = try frame(fixture)
            let result = try XCTUnwrap(HUDStencilTracker(template: try HUDStencilTestReference.load()).observe(image, gameplay: true, timestamp: 1, generation: 1))
            XCTAssertNotNil(result.manaMain, fixture.id)
            if let health = fixture.health {
                XCTAssertEqual(counts(result), health, fixture.id)
                XCTAssertEqual(result.healthMasks.count, health.reduce(0, +), fixture.id)
                XCTAssertEqual(result.establishesGameplay, health.reduce(0, +) >= 2, fixture.id)
            }
            if let mana = fixture.mana { XCTAssertEqual(result.manaReserves.count, mana, fixture.id) }
        }
    }

    @MainActor func testCapturedHUDAndOccupiedProfileUnlockFirstCampaignWithoutModel() throws {
        let image = try frame(XCTUnwrap(fixtures().first { $0.health == [9, 0, 0] }))
        let bundled = try XCTUnwrap(HUDStencilTestReference.load())
        let full = try XCTUnwrap(bundled.health.first { $0.kind == "full" }).bounds
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = LabelingExampleStore(rootURL: root)
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "HUDCampaignTests.\(UUID().uuidString)"))
        let coordinator = StencilInitializationCoordinator(store: store, defaults: defaults)
        coordinator.refresh(mode: .normal)
        XCTAssertFalse(coordinator.isFirstPhaseComplete)
        for context in [LabelingContext.mainTitle, .selectProfile] {
            let boxes = StencilInitializationPolicy.requiredCounts(context: context, mode: .normal)
                .filter { $0.definition.id != StencilInitializationPolicy.profileClearSaveClass }.flatMap { requirement in
                (0..<requirement.count).map { _ in LabelingDraftRectangle(id: UUID(), classID: requirement.definition.id,
                    normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.04)) }
            }
            _ = try store.saveDraft(image: image, context: context, rectangles: boxes)
        }
        coordinator.refresh(mode: .normal)
        XCTAssertFalse(coordinator.isFirstPhaseComplete)
        XCTAssertEqual(coordinator.currentStep?.context, .selectProfile)
        XCTAssertTrue(coordinator.guidance(labelingContext: .selectProfile, selectedClassIdentifier: nil)?.contains("Clear Save") == true)
        let boxes = (0..<2).map { offset in
            LabelingDraftRectangle(id: UUID(), classID: "game.health", normalizedRect: CGRect(
                x: (full.minX + CGFloat(offset) * bundled.healthPitch) / 640, y: full.minY / 360,
                width: full.width / 640, height: full.height / 360))
        }
        let saved = try store.saveDraft(image: image, context: .gameplayHUD, rectangles: boxes)
        coordinator.refresh(mode: .normal)
        let custom = try XCTUnwrap(HUDStencilTemplate.captured(examples: try store.loadExamples(), languageIdentifier: "en"))
        XCTAssertEqual(custom.sourceExampleID, saved.id.uuidString)
        XCTAssertEqual(custom.health.count, 2, "Only the two user-labeled patches may supply recognition pixels.")
        XCTAssertTrue(custom.manaMain.isEmpty && custom.manaReserves.isEmpty)
        XCTAssertTrue(try XCTUnwrap(HUDStencilTracker(template: custom).observe(image, gameplay: true,
            timestamp: 1, generation: 1)).establishesGameplay)
        for fixture in try fixtures() where (fixture.health?.first ?? 0) >= 2 {
            XCTAssertTrue(HUDStencilTracker(template: custom).observe(try frame(fixture), gameplay: true,
                timestamp: 2, generation: 2)?.establishesGameplay == true, fixture.id)
        }
        XCTAssertFalse(coordinator.isFirstPhaseComplete, "HUD cannot bypass the missing Clear Save label.")
        let profileBoxes = StencilInitializationPolicy.requiredCounts(context: .selectProfile, mode: .normal).flatMap { requirement in
            (0..<requirement.count).map { _ in LabelingDraftRectangle(id: UUID(), classID: requirement.definition.id,
                normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.04)) }
        }
        _ = try store.saveDraft(image: image, context: .selectProfile, rectangles: profileBoxes)
        coordinator.refresh(mode: .normal)
        XCTAssertTrue(coordinator.isFirstPhaseComplete)
        XCTAssertFalse(coordinator.steps.contains { $0.classIdentifier == StencilInitializationPolicy.profileClearSaveClass })
        XCTAssertEqual(coordinator.labelContextForCapture(recognized: .game, navigation: .options, fallback: .game), .options)
        XCTAssertEqual(coordinator.labelContextForCapture(recognized: .game, navigation: nil, fallback: .game),
                       coordinator.currentStep?.context)
        XCTAssertFalse(coordinator.isComplete, "Later menus must not block Phase 1.")
        XCTAssertNil(HUDStencilTemplate.captured(examples: [], languageIdentifier: "en"))
        XCTAssertNil(HUDStencilTemplate.captured(examples: try store.loadExamples(), languageIdentifier: "fr"))
        // Finish later screens to verify the campaign-to-model handoff.
        for context in coordinator.contexts where context != .gameplayHUD {
            let boxes = StencilInitializationPolicy.requiredCounts(context: context, mode: .normal).flatMap { requirement in
                (0..<requirement.count).map { _ in LabelingDraftRectangle(id: UUID(), classID: requirement.definition.id,
                    normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.04)) }
            }
            _ = try store.saveDraft(image: image, context: context, rectangles: boxes)
        }
        coordinator.refresh(mode: .normal)
        XCTAssertTrue(coordinator.isComplete)
        for context in [LabelingContext.brightness, .clearSave] {
            func prompt(for target: LabelingContext? = nil) -> String? {
                coordinator.guidance(labelingContext: nil, selectedClassIdentifier: nil, navigationContext: target)
            }
            XCTAssertNil(prompt(for: context))
            let reference = try XCTUnwrap(coordinator.example(for: context))
            let replacement = try XCTUnwrap(LabelingFrameOpeningPolicy.stateForOpening(reference.manifest))
            try store.delete(reference)
            coordinator.refresh(mode: .normal)

            XCTAssertTrue(coordinator.isFirstPhaseComplete)
            let instruction = context == .clearSave ? coordinator.text("navigateClearSave")
                : coordinator.text("navigateShort", ["screen": coordinator.screenName(context)])
            XCTAssertEqual(prompt(for: context), instruction)
            XCTAssertEqual(prompt(), instruction)
            XCTAssertNil(prompt(for: .mainTitle))
            XCTAssertEqual(coordinator.labelContextForCapture(recognized: .game, navigation: context, fallback: .game), context)

            _ = try store.saveDraft(image: image, context: context, rectangles: replacement.draft.rectangles)
            coordinator.refresh(mode: .normal)
            XCTAssertNil(prompt(for: context))
            XCTAssertNil(prompt())
        }
        XCTAssertEqual(coordinator.labelContextForCapture(recognized: .game, navigation: nil, fallback: .mainTitle), .game)
        XCTAssertEqual(coordinator.labelContextForCapture(recognized: nil, navigation: nil, fallback: .options), .options)
        for (workspace, labels, ready, training, key) in [
            (DashboardWorkspace.gameplay, false, false, false, "modelLabelGameplay"),
            (.gameplay, true, false, false, "modelOpen"),
            (.model, true, false, false, "modelTrain"),
            (.gameplay, true, true, false, "modelReady"),
            (.model, true, false, true, "modelTraining")
        ] {
            XCTAssertEqual(coordinator.modelGuidance(workspace: workspace, hasGameplayLabels: labels,
                modelReady: ready, training: training),
                coordinator.text(key, ["count": "2"]))
        }
        XCTAssertNil(coordinator.modelGuidance(workspace: .label, hasGameplayLabels: true,
            modelReady: true, training: false))
        coordinator.recordObjectDetectionEnabled()
        for workspace in [DashboardWorkspace.gameplay, .model] {
            XCTAssertNil(coordinator.modelGuidance(workspace: workspace, hasGameplayLabels: true,
                modelReady: true, training: false))
        }
        let reopened = StencilInitializationCoordinator(store: store, defaults: defaults)
        reopened.refresh(mode: .normal)
        XCTAssertTrue(reopened.hasEnabledObjectDetection)
        XCTAssertNil(reopened.modelGuidance(workspace: .gameplay, hasGameplayLabels: true,
            modelReady: true, training: false))
        XCTAssertEqual(reopened.modelGuidance(workspace: .gameplay, hasGameplayLabels: true,
            modelReady: true, training: false, campaignGuidance: "Next batch"), "Next batch")
        // Detection being enabled does not hide unfinished training.
        XCTAssertEqual(coordinator.modelGuidance(workspace: .model, hasGameplayLabels: true,
            modelReady: true, training: true),
            coordinator.text("modelTraining"))
        coordinator.language = .french
        XCTAssertFalse(coordinator.isFirstPhaseComplete, "A language change must relock incomplete references.")
    }

    func testScaledCaptureRetainsCountsAndScalesStencil() throws {
        let fixture = try XCTUnwrap(fixtures().first)
        let tracker = HUDStencilTracker(template: try HUDStencilTestReference.load())
        let one = try XCTUnwrap(tracker.observe(frame(fixture), gameplay: true, timestamp: 1, generation: 1))
        let two = try XCTUnwrap(tracker.observe(frame(fixture, scale: 2), gameplay: true, timestamp: 2, generation: 2))
        XCTAssertEqual(counts(two), fixture.health)
        XCTAssertEqual(two.geo.width, one.geo.width * 2, accuracy: 0.01)
        XCTAssertEqual(two.geo.minY, one.geo.minY * 2, accuracy: 0.01)
    }

    func testReacquiresHealthAfterChangingDisplayDuringCapture() throws {
        let fixture = try XCTUnwrap(fixtures().first { $0.health == [9, 0, 0] })
        let reference = try frame(fixture)
        let template = try HUDStencilTestReference.load()
        let tracker = HUDStencilTracker(template: template)
        let ci = CIContext()
        // Each source contains a 640px-wide game. On a 4:3 projector the
        // game centers its 640x400 viewport inside a 640x480 window.
        // Independently, the stream can leave padding outside that window.
        let layouts: [(height: Int, fit: CGFloat)] = [(360, 1), (402, 1), (480, 1), (480, 0.75), (360, 1)]
        for (step, layout) in layouts.enumerated() {
            let (height, fit) = layout
            let windowTop = 12
            let windowLeft = 8
            let bufferHeight = 504
            let viewportTop = max(0, (height - 400) / 2)
            let game = CIImage(cgImage: reference)
                .transformed(by: CGAffineTransform(scaleX: fit, y: fit))
                .transformed(by: CGAffineTransform(translationX: CGFloat(windowLeft),
                    y: CGFloat(bufferHeight - windowTop) - CGFloat(viewportTop + 360) * fit))
            var optionalBuffer: CVPixelBuffer?
            XCTAssertEqual(CVPixelBufferCreate(nil, 656, bufferHeight, kCVPixelFormatType_32BGRA,
                [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &optionalBuffer), kCVReturnSuccess)
            let buffer = try XCTUnwrap(optionalBuffer)
            ci.render(game.composited(over: CIImage(color: .black)), to: buffer,
                      bounds: CGRect(x: 0, y: 0, width: 656, height: bufferHeight),
                      colorSpace: CGColorSpaceCreateDeviceRGB())
            let scale: CGFloat = step.isMultiple(of: 2) ? 2 : 1
            let rect = CGRect(x: CGFloat(windowLeft) / scale, y: CGFloat(windowTop) / scale,
                              width: 640 * fit / scale, height: CGFloat(height) * fit / scale)
            let converted = try XCTUnwrap(CaptureFrameConverter.convert(buffer, info: [
                .contentRect: rect.dictionaryRepresentation, .scaleFactor: NSNumber(value: Double(scale))
            ], context: ci))
            XCTAssertEqual(converted.image.width, 640)
            XCTAssertEqual(converted.image.height, 360)
            let hud = try XCTUnwrap(tracker.observe(converted.image, gameplay: true,
                                                    timestamp: Double(step), generation: 1))
            if fit == 1 { XCTAssertEqual(counts(hud), [9, 0, 0], "Window height \(height)") }
            XCTAssertTrue(hud.establishesGameplay)
            XCTAssertNil(LiveGameStatusResolver.resolve([], hudStencil: hud))
        }
    }

    func testUltrawideWindowReacquiresInsetHealthRowAndRestoresOriginalLayout() throws {
        let template = try HUDStencilTestReference.load()
        let reference = try frame(XCTUnwrap(fixtures().first { $0.health == [9, 0, 0] }))
        let tracker = HUDStencilTracker(template: template)
        let original = try XCTUnwrap(tracker.observe(reference, gameplay: true, timestamp: 0, generation: 1))
        // In the 2688×1152 window test, chrome occupied 17 of the 360
        // presentation pixels. The game is bottom aligned and centered.
        let scale: CGFloat = 343.0 / 360
        let left = 640 * (1 - scale) / 2
        let c = try context()
        c.interpolationQuality = .high
        c.draw(reference, in: CGRect(x: left, y: 0, width: 640 * scale, height: 360 * scale))
        let wide = try XCTUnwrap(c.makeImage())
        for time in [1.0, 1.05] {
            let recovered = try XCTUnwrap(tracker.observe(wide, gameplay: true, timestamp: time, generation: 1))
            XCTAssertEqual(recovered.health.map(\.index), Array(0..<9))
            XCTAssertEqual(counts(recovered), [9, 0, 0])
            for (match, expected) in zip(recovered.health, original.health) {
                XCTAssertEqual(match.rect.midX, left + expected.rect.midX * scale, accuracy: 2)
                XCTAssertEqual(match.rect.midY, expected.rect.midY * scale, accuracy: 2)
            }
            XCTAssertNil(LiveGameStatusResolver.resolve([], hudStencil: recovered))
        }
        let restored = try XCTUnwrap(tracker.observe(reference, gameplay: true, timestamp: 2, generation: 1))
        XCTAssertEqual(restored.health, original.health)

        // A player can also save their two labels while the game is ultrawide.
        // Returning to 16:9 must still start at mask zero, even though mask one
        // now falls near the original first label.
        let pixels = try XCTUnwrap(ImageStencilPixels(wide, referenceWidth: 640, referenceHeight: 360, band: 0..<80))
        let atoms = try original.health.prefix(2).map { match -> HUDStencilTemplate.Atom in
            let box = CGRect(x: left + match.rect.minX * scale, y: 360 - match.rect.maxY * scale,
                             width: match.rect.width * scale, height: match.rect.height * scale).integral
            return .init(kind: "full", rect: [box.minX, box.minY, box.width, box.height],
                         rgb: try XCTUnwrap(pixels.reference(in: box)).rgb)
        }
        let wideTemplate = HUDStencilTemplate(schemaVersion: 1, sourceExampleID: "windowed-calibration",
            referenceWidth: 640, referenceHeight: 360, healthPitch: atoms[1].bounds.midX - atoms[0].bounds.midX,
            health: atoms, manaMain: [], manaContainer: [0, 0, 0, 0], manaReserves: [], geo: [0, 0, 0, 0])
        let wideTracker = HUDStencilTracker(template: wideTemplate)
        let normal = try XCTUnwrap(wideTracker.observe(reference, gameplay: true, timestamp: 1, generation: 1))
        XCTAssertEqual(normal.health.map(\.index), Array(0..<9))
        XCTAssertEqual(try XCTUnwrap(normal.health.first).rect.midX,
                       try XCTUnwrap(original.health.first).rect.midX, accuracy: 2)
        let tall = try context()
        tall.draw(reference, in: CGRect(x: 0, y: 21, width: 640, height: 360))
        let fromWideToTall = try XCTUnwrap(wideTracker.observe(try XCTUnwrap(tall.makeImage()),
                                                               gameplay: true, timestamp: 2, generation: 1))
        XCTAssertEqual(fromWideToTall.health.map(\.index), Array(0..<9))
    }

    func testSingleDisplacedIconCannotEstablishNewRow() throws {
        let fixture = try XCTUnwrap(fixtures().first { $0.health == [9, 0, 0] })
        let reference = try frame(fixture)
        let template = try HUDStencilTestReference.load()
        let firstMask = template.health[0].bounds
        let icon = try XCTUnwrap(reference.cropping(to: firstMask))
        let c = try context()
        c.draw(icon, in: CGRect(x: firstMask.minX, y: 360 - firstMask.maxY - 20,
                                width: firstMask.width, height: firstMask.height))
        let tracker = HUDStencilTracker(template: template)
        let result = try XCTUnwrap(tracker.observe(try XCTUnwrap(c.makeImage()), gameplay: true,
                                                   timestamp: 1, generation: 1))
        XCTAssertTrue(result.health.isEmpty)
        XCTAssertFalse(result.establishesGameplay)
    }

    func testGameplayGateAndGenerationResetNeverReuseOldStencil() throws {
        let image = try frame(XCTUnwrap(fixtures().first))
        let blank = try XCTUnwrap(context().makeImage())
        let tracker = HUDStencilTracker(template: try HUDStencilTestReference.load())
        XCTAssertNotNil(tracker.observe(image, gameplay: true, timestamp: 1, generation: 1))
        XCTAssertNil(tracker.observe(image, gameplay: false, timestamp: 1.1, generation: 1))
        let blankResult = tracker.observe(blank, gameplay: true, timestamp: 1.2, generation: 1)
        XCTAssertEqual(blankResult?.healthMasks.count, 0)
        XCTAssertNil(blankResult?.manaMain)
        XCTAssertFalse(blankResult?.establishesGameplay ?? true)
        _ = tracker.observe(image, gameplay: true, timestamp: 2, generation: 1)
        XCTAssertEqual(tracker.observe(blank, gameplay: true, timestamp: 2.1, generation: 2)?.healthMasks.count, 0)
    }

    func testTwoHealthMatchesEstablishGameplayWithoutMana() {
        let health = (0..<2).map {
            HUDStencilMatch(index: $0, kind: "full", rect: .zero, confidence: 0.9)
        }
        let result = HUDStencilResult(
            health: health,
            manaMain: nil,
            manaReserves: [],
            healthMasks: [],
            manaMasks: [],
            geo: .zero,
            holdsHealthMask: false,
            sourceTimestamp: 1
        )
        XCTAssertTrue(result.establishesGameplay)
        XCTAssertFalse(HUDStencilResult(
            health: [health[0]],
            manaMain: nil,
            manaReserves: [],
            healthMasks: [],
            manaMasks: [],
            geo: .zero,
            holdsHealthMask: false,
            sourceTimestamp: 1
        ).establishesGameplay)
    }

    func testAnimationHoldsMaskBrieflyWithoutInventingHealthCount() throws {
        let image = try frame(XCTUnwrap(fixtures().first))
        let blank = try XCTUnwrap(context().makeImage())
        let tracker = HUDStencilTracker(template: try HUDStencilTestReference.load())
        let initial = try XCTUnwrap(tracker.observe(image, gameplay: true, timestamp: 1, generation: 1))
        let held = try XCTUnwrap(tracker.observe(blank, gameplay: true, timestamp: 1.2, generation: 1))
        XCTAssertTrue(held.health.isEmpty)
        XCTAssertTrue(held.holdsHealthMask)
        XCTAssertEqual(held.healthMasks, initial.healthMasks)
        let expired = try XCTUnwrap(tracker.observe(blank, gameplay: true, timestamp: 1.7, generation: 1))
        XCTAssertTrue(expired.healthMasks.isEmpty)
        XCTAssertFalse(expired.holdsHealthMask)
    }

    func testStencilAugmentsPersistentHUDShieldAndActuallyClearsAtlasPixels() throws {
        let fixture = try XCTUnwrap(fixtures().first)
        let image = try frame(fixture)
        let stencil = try XCTUnwrap(HUDStencilTracker(template: try HUDStencilTestReference.load()).observe(image, gameplay: true, timestamp: 1, generation: 1))
        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let regions = SceneRegions.atlasGameplay(fromObjectDetections: [], in: extent, hudStencil: stencil)
        XCTAssertEqual(Array(regions.omittedRects.dropFirst()), stencil.omittedRects)
        XCTAssertEqual(regions.omittedRects.first, CGRect(x: 0, y: 270, width: 256, height: 90))
        let cleared = try XCTUnwrap(FrameRegionRenderer.mapFrame(from: image, omitting: regions, context: CIContext()))
        let overlay = try XCTUnwrap(HUDStencilRenderer.overlay(stencil, width: image.width, height: image.height))
        func alpha(_ image: CGImage, at rect: CGRect) throws -> UInt8 {
            let c = try context()
            c.draw(image, in: extent)
            let bytes = try XCTUnwrap(c.data).assumingMemoryBound(to: UInt8.self)
            let x = Int(rect.midX), y = image.height - 1 - Int(rect.midY)
            return bytes[(y * image.width + x) * 4 + 3]
        }
        for rect in stencil.omittedRects {
            XCTAssertEqual(try alpha(cleared, at: rect), 0)
            XCTAssertGreaterThan(try alpha(overlay, at: rect), 0)
        }
        XCTAssertEqual(try alpha(overlay, at: CGRect(x: 400, y: 150, width: 10, height: 10)), 0)
    }

    func testGeoBoundsStayFixedAsHealthCountChanges() throws {
        let examples = try fixtures()
        let first = try XCTUnwrap(HUDStencilTracker(template: try HUDStencilTestReference.load()).observe(frame(examples[0]), gameplay: true, timestamp: 1, generation: 1))
        let many = try XCTUnwrap(HUDStencilTracker(template: try HUDStencilTestReference.load()).observe(frame(examples[1]), gameplay: true, timestamp: 1, generation: 1))
        XCTAssertEqual(first.geo, many.geo)
        XCTAssertGreaterThan(many.health.count, first.health.count)
    }
}
