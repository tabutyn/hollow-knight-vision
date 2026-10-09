import CoreGraphics
import CoreImage
import Foundation
import ImageIO
import XCTest
@testable import HollowKnightVision

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

    func testRealGameplayHUDsAcrossCountsBackgroundsAndOffsets() throws {
        XCTAssertTrue(try XCTUnwrap(HUDStencilTemplate.bundled).isValid)
        for fixture in try fixtures() {
            let image = try frame(fixture)
            let result = try XCTUnwrap(HUDStencilTracker().observe(image, gameplay: true, timestamp: 1, generation: 1))
            XCTAssertNotNil(result.manaMain, fixture.id)
            if let health = fixture.health {
                XCTAssertEqual(counts(result), health, fixture.id)
                XCTAssertEqual(result.healthMasks.count, health.reduce(0, +), fixture.id)
                XCTAssertEqual(result.establishesGameplay, health.reduce(0, +) >= 2, fixture.id)
            }
            if let mana = fixture.mana { XCTAssertEqual(result.manaReserves.count, mana, fixture.id) }
        }
    }

    func testScaledCaptureRetainsCountsAndScalesStencil() throws {
        let fixture = try XCTUnwrap(fixtures().first)
        let tracker = HUDStencilTracker()
        let one = try XCTUnwrap(tracker.observe(frame(fixture), gameplay: true, timestamp: 1, generation: 1))
        let two = try XCTUnwrap(tracker.observe(frame(fixture, scale: 2), gameplay: true, timestamp: 2, generation: 2))
        XCTAssertEqual(counts(two), fixture.health)
        XCTAssertEqual(two.geo.width, one.geo.width * 2, accuracy: 0.01)
        XCTAssertEqual(two.geo.minY, one.geo.minY * 2, accuracy: 0.01)
    }

    func testGameplayGateAndGenerationResetNeverReuseOldStencil() throws {
        let image = try frame(XCTUnwrap(fixtures().first))
        let blank = try XCTUnwrap(context().makeImage())
        let tracker = HUDStencilTracker()
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
        let tracker = HUDStencilTracker()
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
        let stencil = try XCTUnwrap(HUDStencilTracker().observe(image, gameplay: true, timestamp: 1, generation: 1))
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
        let first = try XCTUnwrap(HUDStencilTracker().observe(frame(examples[0]), gameplay: true, timestamp: 1, generation: 1))
        let many = try XCTUnwrap(HUDStencilTracker().observe(frame(examples[1]), gameplay: true, timestamp: 1, generation: 1))
        XCTAssertEqual(first.geo, many.geo)
        XCTAssertGreaterThan(many.health.count, first.health.count)
    }
}
