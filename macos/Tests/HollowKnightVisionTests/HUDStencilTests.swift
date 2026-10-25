import CoreGraphics
import CoreImage
import Foundation
import ImageIO
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

    @MainActor func testCapturedHUDUnlocksFirstCampaignWithoutModelOrClearSave() throws {
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
            let boxes = StencilInitializationPolicy.requiredCounts(context: context, mode: .normal).flatMap { requirement in
                (0..<requirement.count).map { _ in LabelingDraftRectangle(id: UUID(), classID: requirement.definition.id,
                    normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.04)) }
            }
            _ = try store.saveDraft(image: image, context: context, rectangles: boxes)
        }
        coordinator.refresh(mode: .normal)
        XCTAssertFalse(coordinator.isFirstPhaseComplete)
        XCTAssertEqual(coordinator.currentStep?.context, .gameplayHUD)
        XCTAssertEqual(coordinator.guidance(labelingContext: nil, selectedClassIdentifier: nil),
                       "Enter gameplay, make health icons visible, press L.")
        XCTAssertTrue(coordinator.guidance(labelingContext: .selectProfile, selectedClassIdentifier: nil)?.contains("Press L") == true)
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
        XCTAssertTrue(coordinator.isFirstPhaseComplete)
        XCTAssertFalse(coordinator.isComplete, "Later menus must not block Phase 1.")
        XCTAssertNil(HUDStencilTemplate.captured(examples: [], languageIdentifier: "en"))
        XCTAssertNil(HUDStencilTemplate.captured(examples: try store.loadExamples(), languageIdentifier: "fr"))
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
