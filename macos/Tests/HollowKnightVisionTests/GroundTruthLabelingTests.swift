import CoreGraphics
import XCTest
@testable import HollowKnightVision

@MainActor
final class GroundTruthLabelingTests: XCTestCase {
    func testWorldLineReprojectsWithCameraMotion() {
        let first = transform(camera: CGPoint(x: 100, y: 20))
        let moved = transform(camera: CGPoint(x: 105, y: 20))
        let firstBounds = CGRect(x: 1_000, y: 2_000, width: 640, height: 360)
        let movedBounds = firstBounds.offsetBy(dx: 50, dy: 0)
        let world = CGPoint(x: 110, y: 22)

        XCTAssertEqual(first.screenToWorld(CGPoint(x: 320, y: 180)), first.cameraWorld)
        XCTAssertEqual(first.atlasToWorld(first.worldToAtlas(world, liveBounds: firstBounds),
                                          liveBounds: firstBounds).x, world.x, accuracy: 0.000_001)
        XCTAssertEqual(first.worldToAtlas(world, liveBounds: firstBounds).x,
                       moved.worldToAtlas(world, liveBounds: movedBounds).x,
                       accuracy: 0.000_001)
        XCTAssertEqual(first.worldToAtlas(world, liveBounds: firstBounds).y,
                       moved.worldToAtlas(world, liveBounds: movedBounds).y,
                       accuracy: 0.000_001)
    }

    func testStoreRoundTripsWorldLines() throws {
        let url = temporaryURL()
        let store = GroundTruthLineStore(url: url)
        let line = GroundTruthWorldLine(
            sceneName: "Tutorial_01", minimumWorldX: 1, maximumWorldX: 12,
            worldY: 3, kind: .negative, seededFromDetector: true
        )
        let document = GroundTruthLineDocument(
            seededScenes: ["Tutorial_01"], lines: [line]
        )

        try store.save(document)
        XCTAssertEqual(try store.load(), document)
    }

    func testAddNegativeModifyDeleteAndUndoKeepHorizontalWorldLines() {
        let controller = controller()
        let transform = transform(camera: .zero)
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        let start = transform.worldToAtlas(CGPoint(x: 1, y: 2), liveBounds: bounds)
        let end = transform.worldToAtlas(CGPoint(x: 6, y: 9), liveBounds: bounds)

        drag(controller, from: start, to: end, transform: transform, bounds: bounds)
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertEqual(controller.document.lines[0].minimumWorldX, 1, accuracy: 0.000_001)
        XCTAssertEqual(controller.document.lines[0].maximumWorldX, 6, accuracy: 0.000_001)
        XCTAssertEqual(controller.document.lines[0].worldY, 2, accuracy: 0.000_001)

        controller.mode = .negative
        click(controller, at: transform.worldToAtlas(CGPoint(x: 3, y: 2), liveBounds: bounds),
              transform: transform, bounds: bounds)
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertEqual(controller.document.lines[0].kind, .negative)

        controller.mode = .modify
        drag(
            controller,
            from: transform.worldToAtlas(CGPoint(x: 3, y: 2), liveBounds: bounds),
            to: transform.worldToAtlas(CGPoint(x: 4, y: 3), liveBounds: bounds),
            transform: transform,
            bounds: bounds
        )
        XCTAssertEqual(controller.document.lines[0].minimumWorldX, 2, accuracy: 0.000_001)
        XCTAssertEqual(controller.document.lines[0].maximumWorldX, 7, accuracy: 0.000_001)
        XCTAssertEqual(controller.document.lines[0].worldY, 3, accuracy: 0.000_001)

        controller.mode = .delete
        click(controller, at: transform.worldToAtlas(CGPoint(x: 4, y: 3), liveBounds: bounds),
              transform: transform, bounds: bounds)
        XCTAssertTrue(controller.document.lines.isEmpty)
        controller.undo()
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertEqual(controller.document.lines[0].worldY, 3, accuracy: 0.000_001)
    }

    func testOverlayUsesPositiveNegativeAndSelectedKinds() {
        let positive = GroundTruthWorldLine(
            sceneName: "Tutorial_01", minimumWorldX: 1, maximumWorldX: 3,
            worldY: 2, kind: .positive, seededFromDetector: false
        )
        let negative = GroundTruthWorldLine(
            sceneName: "Tutorial_01", minimumWorldX: 4, maximumWorldX: 6,
            worldY: 2, kind: .negative, seededFromDetector: false
        )
        let overlay = GroundTruthLineProjection.overlayLines(
            document: GroundTruthLineDocument(lines: [positive, negative]),
            transform: transform(camera: .zero),
            liveBounds: CGRect(x: 100, y: 200, width: 640, height: 360),
            selectedID: negative.id,
            draft: nil
        )

        XCTAssertEqual(overlay.map(\.kind), [.positiveGroundTruth, .selectedGroundTruth])
        XCTAssertTrue(overlay.allSatisfy { $0.start.y == $0.end.y })
    }

    func testEnteringSceneSeedsOnlyOnce() {
        let controller = controller()
        let transform = transform(camera: .zero)
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        var tracking = GroundHypothesisTrackingResult.empty
        tracking.lineReviews = [GroundLinePresenceReview(
            id: 1,
            line: CleanFloorLine(row: 240, xRange: 80...500),
            state: .confirmed,
            visibleSeconds: 3,
            detectedFraction: 1
        )]

        controller.enter(tracking: tracking, transform: transform, liveBounds: bounds)
        controller.enter(tracking: tracking, transform: transform, liveBounds: bounds)

        XCTAssertEqual(controller.document.seededScenes, ["Tutorial_01"])
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertTrue(controller.document.lines[0].seededFromDetector)
    }

    private func controller() -> GroundTruthLabelController {
        GroundTruthLabelController(store: GroundTruthLineStore(url: temporaryURL()))
    }

    private func transform(camera: CGPoint) -> GroundTruthCameraTransform {
        GroundTruthCameraTransform(
            sceneName: "Tutorial_01",
            cameraWorld: camera,
            pixelsPerWorldUnitX: 10,
            pixelsPerWorldUnitY: 10,
            frameSize: CGSize(width: 640, height: 360)
        )
    }

    private func drag(
        _ controller: GroundTruthLabelController,
        from start: CGPoint,
        to end: CGPoint,
        transform: GroundTruthCameraTransform,
        bounds: CGRect
    ) {
        controller.handle(phase: .began, atlasPoint: start, atlasTolerance: 0,
                          transform: transform, liveBounds: bounds)
        controller.handle(phase: .changed, atlasPoint: end, atlasTolerance: 0,
                          transform: transform, liveBounds: bounds)
        controller.handle(phase: .ended, atlasPoint: end, atlasTolerance: 0,
                          transform: transform, liveBounds: bounds)
    }

    private func click(
        _ controller: GroundTruthLabelController,
        at point: CGPoint,
        transform: GroundTruthCameraTransform,
        bounds: CGRect
    ) {
        controller.handle(phase: .began, atlasPoint: point, atlasTolerance: 0,
                          transform: transform, liveBounds: bounds)
        controller.handle(phase: .ended, atlasPoint: point, atlasTolerance: 0,
                          transform: transform, liveBounds: bounds)
    }

    private func temporaryURL() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-ground-truth-\(UUID().uuidString).json")
    }
}
