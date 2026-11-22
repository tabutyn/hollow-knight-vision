import CoreGraphics
import XCTest
@testable import HollowKnightVision

@MainActor
final class GroundTruthCornerTests: XCTestCase {
    // The room projection is the same as Hacker's on-screen editor.
    private let room = GroundTruthRoomPlacement(id: UUID(), sceneName: "Tutorial_01",
        position: .zero, bounds: CGRect(x: 0, y: 0, width: 300, height: 200))
    private let store = GroundTruthLineStore(url: FileManager.default.temporaryDirectory
        .appendingPathComponent("hkv-wall-corner-\(UUID()).json"))

    private func editor() -> GroundTruthLabelController {
        let editor = GroundTruthLabelController(store: store)
        let t = GroundTruthCameraTransform(sceneName: room.sceneName,
            cameraWorld: CGPoint(x: 150, y: 100), pixelsPerWorldUnitX: 1, pixelsPerWorldUnitY: 1,
            frameSize: room.bounds.size)
        editor.registerRoom(room, transform: t, liveBounds: room.atlasBounds)
        return editor
    }

    private func pointer(_ editor: GroundTruthLabelController, _ phase: LayerSceneGroundLabelPointerPhase,
                         _ x: CGFloat, _ y: CGFloat, radius: CGFloat = 6) {
        editor.handle(phase: phase, atlasPoint: CGPoint(x: x, y: y), atlasTolerance: radius, rooms: [room])
    }

    private func drag(_ editor: GroundTruthLabelController, _ x: CGFloat, _ y: CGFloat,
                      _ endX: CGFloat, _ endY: CGFloat) {
        pointer(editor, .began, x, y)
        pointer(editor, .changed, endX, endY)
        pointer(editor, .ended, endX, endY)
    }

    private func twoFloors(_ editor: GroundTruthLabelController) {
        drag(editor, 40, 20, 100, 20)
        drag(editor, 10, 100, 43, 100)
        editor.selectAxis(.vertical)
    }

    func testWallStartSnapsAndFinishPullsFloorEndpointToItsTip() throws {
        let e = editor()
        twoFloors(e)
        let before = e.document
        pointer(e, .began, 42, 21)
        pointer(e, .changed, 41, 103)
        let live = e.overlay(rooms: [room])
        XCTAssertEqual(live.markers.filter { $0.kind == .joinedTerrainCorner }.map(\.worldPosition),
                       [CGPoint(x: 40, y: 20), CGPoint(x: 40, y: 103)])
        XCTAssertEqual(e.document, before)
        XCTAssertEqual(try store.load(), before)
        pointer(e, .ended, 41, 103)

        let wall = try XCTUnwrap(e.document.walls?.first)
        XCTAssertEqual(wall.start, CGPoint(x: 40, y: 20))
        XCTAssertEqual(wall.end, CGPoint(x: 40, y: 103))
        XCTAssertEqual(e.document.lines[1].start, CGPoint(x: 10, y: 103))
        XCTAssertEqual(e.document.lines[1].end, wall.end)
        XCTAssertNotNil(wall.startCornerID)
        XCTAssertNotNil(wall.endCornerID)
        XCTAssertNotEqual(wall.startCornerID, wall.endCornerID)
        XCTAssertEqual(wall.startCornerID, e.document.lines[0].startCornerID)
        XCTAssertEqual(wall.endCornerID, e.document.lines[1].endCornerID)
        XCTAssertEqual(try store.load(), e.document)
        XCTAssertEqual(live.markers.count, 4)
        e.undo()
        XCTAssertEqual(try store.load(), before)
    }

    func testBothAxesFollowAfterReloadAndUndoRestoresTheEntireCorner() throws {
        let first = editor()
        twoFloors(first)
        drag(first, 42, 21, 41, 103)
        let e = editor()
        let original = e.document
        e.selectAxis(.vertical)
        e.mode = .modify
        drag(e, 40, 60, 50, 69)
        XCTAssertEqual(e.document.lines[0].start, CGPoint(x: 50, y: 20))
        XCTAssertEqual(e.document.lines[0].end, CGPoint(x: 100, y: 20))
        XCTAssertEqual(e.document.lines[1].end, CGPoint(x: 50, y: 103))
        XCTAssertEqual(e.document.walls?.first?.end, CGPoint(x: 50, y: 103))
        e.selectAxis(.horizontal)
        drag(e, 75, 20, 89, 30)
        XCTAssertEqual(e.document.lines[0].end, CGPoint(x: 100, y: 30))
        XCTAssertEqual(e.document.walls?.first?.start, CGPoint(x: 50, y: 30))
        XCTAssertEqual(e.document.walls?.first?.end, CGPoint(x: 50, y: 103))
        let beforeCornerDrag = e.document
        e.selectAxis(.vertical)
        drag(e, 50, 30, 54, 33)
        XCTAssertEqual(e.document.lines[0].start, CGPoint(x: 54, y: 33))
        XCTAssertEqual(e.document.lines[0].end, CGPoint(x: 100, y: 33))
        XCTAssertEqual(e.document.lines[1].end, CGPoint(x: 54, y: 103))
        XCTAssertEqual(e.document.walls?.first?.start, CGPoint(x: 54, y: 33))
        e.undo()
        XCTAssertEqual(try store.load(), beforeCornerDrag)
        e.undo()
        e.undo()
        XCTAssertEqual(e.document, original)
    }

    func testWallModifyCanGrabExistingGroundEndAndSnapItToWall() throws {
        let e = editor()
        drag(e, 30, 25, 90, 25)
        e.selectAxis(.vertical)
        drag(e, 110, 70, 110, 35) // Draw downwards.
        e.mode = .modify
        drag(e, 90, 25, 108, 37)
        let result = e.document
        let wall = try XCTUnwrap(result.walls?.first)
        XCTAssertEqual(wall.start, CGPoint(x: 110, y: 35))
        XCTAssertEqual(result.lines[0].end, wall.start)
        XCTAssertEqual(result.lines[0].start, CGPoint(x: 30, y: 35))
        XCTAssertNotNil(wall.startCornerID)
        XCTAssertEqual(wall.startCornerID, result.lines[0].endCornerID)
        e.mode = .delete
        drag(e, 110, 52, 110, 52)
        XCTAssertNil(e.document.walls)
        XCTAssertNil(e.document.lines[0].endCornerID)
        XCTAssertEqual(e.document.lines[0].end, wall.start)
        e.undo()
        XCTAssertEqual(try store.load(), result)
    }

    func testCancelClickAndNearbyInteriorDoNotAuthorCorners() throws {
        let e = editor()
        twoFloors(e)
        let original = e.document
        pointer(e, .began, 40, 25)
        pointer(e, .ended, 40, 25)
        XCTAssertEqual(e.document, original)
        pointer(e, .began, 42, 21)
        pointer(e, .changed, 41, 103)
        e.selectAxis(.horizontal)
        XCTAssertEqual(try store.load(), original)
        e.selectAxis(.vertical)
        drag(e, 70, 20, 70, 103)
        XCTAssertNil(e.document.walls?.first?.startCornerID)
        XCTAssertNil(e.document.walls?.first?.endCornerID)
        XCTAssertEqual(e.document.lines, original.lines)
    }

    func testDownwardDrawKeepsGrabbedGroundAtUpperWallHandleAndClampsConnectedFloor() throws {
        let e = editor()
        twoFloors(e)
        drag(e, 43, 100, 43, 21)
        let wall = try XCTUnwrap(e.document.walls?.first)
        XCTAssertEqual(e.document.lines[1].end, wall.end)
        XCTAssertEqual(e.document.lines[0].start, wall.start)
        e.mode = .modify
        // The short top floor cannot extend past its own left endpoint.
        drag(e, 43, 60, 0, 60)
        XCTAssertEqual(e.document.walls?.first?.end, CGPoint(x: 10, y: 100))
        XCTAssertEqual(e.document.lines[0].start, CGPoint(x: 10, y: 21))
        XCTAssertEqual(e.document.lines[1].end, e.document.lines[1].start)
        XCTAssertNoThrow(try e.document.validate())
    }
    func testASecondWallOnTheSameFloorFollowsElevationWithoutMovingSideways() throws {
        let e = editor()
        drag(e, 40, 30, 120, 30)
        e.selectAxis(.vertical)
        drag(e, 41, 29, 41, 70)
        drag(e, 119, 31, 119, 80)
        e.mode = .modify
        drag(e, 40, 30, 49, 40)
        let walls = try XCTUnwrap(e.document.walls)
        XCTAssertEqual(walls[0].start, CGPoint(x: 49, y: 40))
        XCTAssertEqual(walls[0].end, CGPoint(x: 49, y: 70))
        XCTAssertEqual(walls[1].start, CGPoint(x: 120, y: 40))
        XCTAssertEqual(walls[1].end, CGPoint(x: 120, y: 80))
        XCTAssertEqual(e.document.lines[0].start, walls[0].start)
        XCTAssertEqual(e.document.lines[0].end, walls[1].start)
        XCTAssertEqual(try store.load(), e.document)
    }

    func testOnlyEndpointsInsideTheScreenRadiusInTheSameRoomCanLatch() throws {
        let e = editor()
        let b = GroundTruthRoomPlacement(id: UUID(), sceneName: "B", position: CGPoint(x: 300, y: 0), bounds: room.bounds)
        let t = GroundTruthCameraTransform(sceneName: b.sceneName, cameraWorld: CGPoint(x: 150, y: 100),
            pixelsPerWorldUnitX: 2, pixelsPerWorldUnitY: 4, frameSize: b.bounds.size)
        e.registerRoom(b, transform: t, liveBounds: b.atlasBounds)
        func stroke(_ from: CGPoint, _ to: CGPoint) {
            for (phase, p) in [(LayerSceneGroundLabelPointerPhase.began, from), (.changed, to), (.ended, to)] {
                e.handle(phase: phase, atlasPoint: p, atlasTolerance: 6, rooms: [room, b])
            }
        }
        stroke(CGPoint(x: 304, y: 90), CGPoint(x: 350, y: 90))
        e.selectAxis(.vertical)
        stroke(CGPoint(x: 299, y: 20), CGPoint(x: 299, y: 90))
        XCTAssertEqual(e.document.walls?.first?.sceneName, room.sceneName)
        XCTAssertNil(e.document.walls?.first?.endCornerID)
        // Five pixels away on both axes lies outside a six-pixel circle.
        stroke(CGPoint(x: 309, y: 95), CGPoint(x: 309, y: 140))
        XCTAssertNil(e.document.walls?.last?.startCornerID)
        // The on-screen distance controls the hit despite the unequal world scales.
        stroke(CGPoint(x: 304, y: 95), CGPoint(x: 304, y: 115))
        XCTAssertNotNil(e.document.walls?.last?.startCornerID)
        XCTAssertEqual(e.document.walls?.last?.start, e.document.lines[0].start)
    }

    func testCoincidentLegacyEndpointsStayIndependentUntilAnEditorGestureJoinsThem() throws {
        let floor = GroundTruthWorldLine(sceneName: room.sceneName, minimumWorldX: 40, maximumWorldX: 100,
            worldY: 30, kind: .positive, seededFromDetector: false)
        let wall = GroundTruthWorldLine(sceneName: room.sceneName, minimumWorldX: 40, maximumWorldX: 40,
            worldY: 30, kind: .wall, seededFromDetector: false, endWorldY: 80)
        try store.save(.init(lines: [floor], walls: [wall]))
        let e = editor()
        XCTAssertTrue(e.overlay(rooms: [room]).markers.allSatisfy { $0.kind != .joinedTerrainCorner })
        e.selectAxis(.vertical)
        e.mode = .modify
        drag(e, 40, 50, 45, 50)
        XCTAssertEqual(try store.load().lines, [floor])
    }

}
