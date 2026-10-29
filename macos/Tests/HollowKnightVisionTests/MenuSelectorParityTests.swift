import XCTest
@testable import HollowKnightVision

final class MenuSelectorParityTests: XCTestCase {
    @discardableResult private func save(_ context: LabelingContext, boxes: [(String, CGRect)],
        paint: [(String, CGRect)]? = nil, store: LabelingExampleStore) throws -> CGImage {
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360, bitsPerComponent: 8,
            bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        bitmap.setFillColor(CGColor(gray: 0, alpha: 1))
        bitmap.fill(CGRect(x: 0, y: 0, width: 640, height: 360))
        // Distinct foreground strokes provide real sampled pixels, without a private screenshot corpus.
        for item in paint ?? boxes {
            let index = item.0.utf8.reduce(0) { ($0 + Int($1)) % 97 }
            for x in stride(from: Int(item.1.minX), to: Int(item.1.maxX), by: 3) {
                bitmap.setFillColor(CGColor(gray: CGFloat(150 + (x + index * 17) % 106) / 255, alpha: 1))
                bitmap.fill(CGRect(x: CGFloat(x), y: 360 - item.1.maxY, width: 2, height: item.1.height))
            }
        }
        let rectangles = boxes.map { item in
            LabelingDraftRectangle(id: UUID(), classID: item.0, normalizedRect: CGRect(
                x: item.1.minX / 640, y: item.1.minY / 360, width: item.1.width / 640, height: item.1.height / 360))
        }
        let image = try XCTUnwrap(bitmap.makeImage())
        _ = try store.saveDraft(image: image, context: context, rectangles: rectangles)
        return image
    }

    func testClearSaveCapturedBesideSecondSlotExpandsToAllRowsWithoutDuplicate() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = LabelingExampleStore(rootURL: root)
        var boxes: [(String, CGRect)] = [
            ("select-profile.heading", CGRect(x: 240, y: 40, width: 160, height: 16)),
            ("shared.back", CGRect(x: 290, y: 330, width: 60, height: 12)),
            ("select-profile.clear-save", CGRect(x: 440, y: 160, width: 90, height: 12)),
            ("shared.select-decoration", CGRect(x: 255, y: 160, width: 15, height: 12)),
            ("shared.select-decoration", CGRect(x: 380, y: 160, width: 15, height: 12))
        ]
        for slot in 1...4 {
            boxes.append(("select-profile.slot-\(slot)", CGRect(x: 290, y: 40 + slot * 60, width: 60, height: 12)))
        }
        try save(.selectProfile, boxes: boxes, store: store)
        let catalog = try MenuStencilCatalog(examplesRootURL: root, calibration: nil, liveCapturesOverride: [])
        let scene = try XCTUnwrap(catalog.scenes.first { $0.context == .selectProfile })
        XCTAssertFalse(scene.options.contains { $0.classIdentifier == "select-profile.clear-save" })
        let rows = scene.options.filter { $0.classIdentifier.hasPrefix("select-profile.clear-save-") }
        XCTAssertEqual(rows.count, 4)
        for (index, row) in rows.enumerated() {
            let slot = try XCTUnwrap(scene.options.first { $0.classIdentifier == "select-profile.slot-\(index + 1)" })
            let center = (try XCTUnwrap(slot.leftSelector).bounds.midY + XCTUnwrap(slot.rightSelector).bounds.midY) / 2
            XCTAssertEqual(row.bounds.midY, center, accuracy: 0.01)
            XCTAssertEqual(try XCTUnwrap(row.leftSelector).bounds.midY, center, accuracy: 0.01)
        }
    }

    func testRepeatedClearSaveUsesPixelsForEachOccupiedRowAndPreservesFractionalCrop() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = LabelingExampleStore(rootURL: root)
        let clear = "select-profile.clear-save"
        var labels: [(String, CGRect)] = [
            ("select-profile.heading", CGRect(x: 240, y: 40, width: 160, height: 16)),
            ("shared.back", CGRect(x: 290, y: 330, width: 60, height: 12)),
            ("shared.select-decoration", CGRect(x: 255, y: 160, width: 15, height: 12)),
            ("shared.select-decoration", CGRect(x: 380, y: 160, width: 15, height: 12))
        ]
        for slot in 1...4 {
            labels.append(("select-profile.slot-\(slot)", CGRect(x: 290, y: 40 + slot * 60, width: 60, height: 12)))
        }
        let copies = [160, 220, 280].map { (clear, CGRect(x: 440, y: $0, width: 90, height: 12)) }
        _ = try save(.selectProfile, boxes: labels, paint: labels + copies, store: store)
        let image = try save(.selectProfile, boxes: labels + [(clear, CGRect(x: 440.4, y: 160.2, width: 89.2, height: 11.4))],
            paint: labels + copies, store: store)
        let catalog = try MenuStencilCatalog(examplesRootURL: root, calibration: nil, liveCapturesOverride: [])
        let tracker = MenuStencilTracker(catalog: catalog)
        let result = try XCTUnwrap(tracker.observe(image, timestamp: 0))
        XCTAssertTrue(result.isMatch)
        let matches = result.anchors.filter { $0.classIdentifier == clear }
        XCTAssertEqual(matches.count, 3)
        XCTAssertTrue(matches.allSatisfy { $0.rect.width == 90 && $0.rect.height == 12 })
        let changed = try save(.game, boxes: [], paint: labels + [copies[0], copies[2]], store: store)
        let second = try XCTUnwrap(tracker.observe(changed, timestamp: 1.0 / 30))
        XCTAssertEqual(second.anchors.filter { $0.classIdentifier == clear }.count, 2)
    }

    func testClearSaveDialogIgnoresCalibrationFromAnotherSlot() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = LabelingExampleStore(rootURL: root)
        try save(.clearSave, boxes: [
            ("clear-save.heading", CGRect(x: 180, y: 160, width: 100, height: 12)),
            ("shared.yes", CGRect(x: 320, y: 160, width: 30, height: 12)),
            ("shared.no", CGRect(x: 440, y: 160, width: 30, height: 12)),
            ("shared.select-decoration", CGRect(x: 410, y: 160, width: 15, height: 12)),
            ("shared.select-decoration", CGRect(x: 485, y: 160, width: 15, height: 12))
        ], store: store)
        let stale = MenuStencilCalibration(schemaVersion: 1, referenceWidth: 640, referenceHeight: 360,
            generatedAt: Date(), scenes: [.init(contextIdentifier: "clear-save", anchors: [], selectors: [
                .init(selectedIdentifier: "shared.no", leftRect: .init(CGRect(x: 410, y: 100, width: 15, height: 12)),
                      rightRect: .init(CGRect(x: 485, y: 100, width: 15, height: 12)), measured: true, evidenceCount: 5)
            ], states: [])])
        let catalog = try MenuStencilCatalog(examplesRootURL: root, calibration: stale, liveCapturesOverride: [])
        let scene = try XCTUnwrap(catalog.scenes.first { $0.context == .clearSave })
        let no = try XCTUnwrap(scene.options.first { $0.classIdentifier == "shared.no" })
        XCTAssertEqual(try XCTUnwrap(no.leftSelector).bounds.midY, 166, accuracy: 0.01)
        XCTAssertEqual(try XCTUnwrap(no.rightSelector).bounds.midY, 166, accuracy: 0.01)
    }
}
