import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingObjectAtlasTests: XCTestCase {
    func testBuildsAndCachesFixedSizeAtlasWithStableSlots() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-object-atlas-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let examplesRoot = root.appendingPathComponent("examples", isDirectory: true)
        let exampleStore = LabelingExampleStore(rootURL: examplesRoot)
        let example = try exampleStore.saveDraft(
            image: solidImage(width: 20, height: 10),
            context: .game,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: "game.mana",
                normalizedRect: CGRect(x: 0, y: 0, width: 1, height: 1)
            )]
        )
        let input = LabelingObjectAtlasBuildInput(
            examples: [example],
            classIdentifiers: ["game.health", "game.mana"],
            preferredExampleIdentifiers: ["game.mana": example.id]
        )
        let store = LabelingObjectAtlasStore(rootURL: root)

        let built = try store.loadOrBuild(input: input)
        let cached = try store.loadOrBuild(input: input)

        XCTAssertEqual(built.image.width, 2048)
        XCTAssertEqual(built.image.height, 2048)
        XCTAssertEqual(built.classIdentifiers, ["game.health", "game.mana"])
        let manaIcon = try XCTUnwrap(built.icon(for: "game.mana"))
        XCTAssertEqual(manaIcon.width, 128)
        XCTAssertEqual(manaIcon.height, 64)
        XCTAssertGreaterThan(averageAlpha(manaIcon), 0)
        // Redrawing the legend must reuse the crop, not allocate a new image
        // (and trigger another texture upload) for every mouse movement.
        XCTAssertTrue(manaIcon === built.icon(for: "game.mana"))
        XCTAssertNil(built.icon(for: "game.health"))
        let cachedManaIcon = try XCTUnwrap(cached.icon(for: "game.mana"))
        XCTAssertTrue(cachedManaIcon === cached.icon(for: "game.mana"))
        XCTAssertEqual(cachedManaIcon.width, manaIcon.width)
        XCTAssertEqual(cachedManaIcon.height, manaIcon.height)
        XCTAssertEqual(cached.classIdentifiers, built.classIdentifiers)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: root.appendingPathComponent(LabelingObjectAtlasStore.imageFilename).path
        ))
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: root.appendingPathComponent(LabelingObjectAtlasStore.metadataFilename).path
        ))
    }

    private func solidImage(width: Int, height: Int) throws -> CGImage {
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return try XCTUnwrap(context.makeImage())
    }

    private func averageAlpha(_ image: CGImage) -> UInt8 {
        var pixel = [UInt8](repeating: 0, count: 4)
        let context = CGContext(
            data: &pixel,
            width: 1,
            height: 1,
            bitsPerComponent: 8,
            bytesPerRow: 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        return pixel[3]
    }
}
