import CoreGraphics
import XCTest
@testable import HollowKnightVision

@MainActor
final class GroundTruthLabelingTests: XCTestCase {
    func testDoorSnapsToGroundAndFollowsGroundAfterReload() throws {
        let store = GroundTruthLineStore(url: temporaryURL())
        var editor = GroundTruthLabelController(store: store)
        let camera = transform(camera: .zero)
        let bounds = CGRect(x: 0, y: 0, width: 640, height: 360)
        func dragWorld(_ a: CGPoint, _ b: CGPoint) {
            drag(editor, from: camera.worldToAtlas(a, liveBounds: bounds), to: camera.worldToAtlas(b, liveBounds: bounds),
                 transform: camera, bounds: bounds)
        }
        dragWorld(.init(x: 0, y: 2), .init(x: 10, y: 2))
        editor.selectDoor()
        dragWorld(.init(x: 10.1, y: 8), .init(x: 10.1, y: 2.1))
        dragWorld(.init(x: 3, y: 2.1), .init(x: 5, y: 2.1))
        let doors = try XCTUnwrap(editor.document.doors)
        XCTAssertEqual(doors[0].start, CGPoint(x: 10, y: 2))
        XCTAssertEqual(doors[1].start, CGPoint(x: 3, y: 2))
        XCTAssertTrue(doors.allSatisfy { $0.groundLatch?.groundID == editor.document.lines[0].id })
        editor = GroundTruthLabelController(store: store)
        editor.mode = .modify
        dragWorld(.init(x: 7, y: 2), .init(x: 9, y: 4))
        XCTAssertEqual(try store.load().doors?.map(\.start), [.init(x: 12, y: 4), .init(x: 5, y: 4)])
        editor.mode = .delete
        dragWorld(.init(x: 7, y: 4), .init(x: 7, y: 4))
        XCTAssertTrue(try XCTUnwrap(store.load().doors).allSatisfy { $0.groundLatch == nil })
    }
    func testDoorsDrawBothAxesAndSurviveEditingAndReload() throws {
        let store = GroundTruthLineStore(url: temporaryURL())
        let controller = GroundTruthLabelController(store: store)
        let camera = transform(camera: .zero)
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
            camera.worldToAtlas(CGPoint(x: x, y: y), liveBounds: bounds)
        }
        controller.selectDoor()
        drag(controller, from: point(1, 2), to: point(6, 2), transform: camera, bounds: bounds)
        drag(controller, from: point(8, 1), to: point(8, 6), transform: camera, bounds: bounds)
        XCTAssertEqual(controller.document.doors?.map(\.kind), [.door, .doorVertical])
        XCTAssertEqual(try store.load(), controller.document)
        XCTAssertEqual(controller.document.doors?.first?.overlayKind(selected: nil), .doorGroundTruth)
        controller.mode = .delete
        click(controller, at: point(8, 3), transform: camera, bounds: bounds)
        XCTAssertEqual(controller.document.doors?.count, 1)
        controller.undo()
        XCTAssertEqual(try store.load().doors?.count, 2)
    }

    func testSpikeEditingPersistenceAndIsolationFromGround() throws {
        let store = GroundTruthLineStore(url: temporaryURL())
        let controller = GroundTruthLabelController(store: store)
        let camera = transform(camera: .zero)
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
            camera.worldToAtlas(CGPoint(x: x, y: y), liveBounds: bounds)
        }
        drag(controller, from: point(1, 2), to: point(6, 2), transform: camera, bounds: bounds)
        controller.selectSpike()
        drag(controller, from: point(2, 2), to: point(5, 2), transform: camera, bounds: bounds)
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertEqual(controller.document.spikes?.count, 1)
        XCTAssertEqual(try store.load(), controller.document)
        XCTAssertEqual(controller.document.spikes?.first?.overlayKind(selected: nil), .spikeGroundTruth)

        controller.mode = .modify
        drag(controller, from: point(3.5, 2), to: point(4.5, 3), transform: camera, bounds: bounds)
        XCTAssertEqual(controller.document.spikes?.first?.worldY ?? 0, 3, accuracy: 0.000_001)
        XCTAssertEqual(controller.document.lines.first?.worldY ?? 0, 2, accuracy: 0.000_001)
        controller.mode = .delete
        click(controller, at: point(4.5, 3), transform: camera, bounds: bounds)
        XCTAssertTrue(controller.document.spikes?.isEmpty == true)
        controller.undo()
        XCTAssertEqual(try store.load().spikes?.count, 1)
    }
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
        let legacyJSON = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        XCTAssertNil(legacyJSON["roomProjections"])
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

    func testRoomRegistrationNeverCreatesOrRestoresAuthoredLines() throws {
        let store = GroundTruthLineStore(url: temporaryURL())
        let controller = GroundTruthLabelController(store: store)
        let transform = transform(camera: .zero)
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        let room = GroundTruthRoomPlacement(id: UUID(), sceneName: transform.sceneName,
            position: .zero, bounds: bounds)
        controller.registerRoom(room, transform: transform, liveBounds: bounds)
        XCTAssertTrue(controller.document.lines.isEmpty)
        let start = transform.worldToAtlas(CGPoint(x: 1, y: 2), liveBounds: bounds)
        let end = transform.worldToAtlas(CGPoint(x: 6, y: 2), liveBounds: bounds)
        drag(controller, from: start, to: end, transform: transform, bounds: bounds)
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertFalse(controller.document.lines[0].seededFromDetector)
        controller.mode = .delete
        click(controller, at: start, transform: transform, bounds: bounds)
        for _ in 0..<12 {
            controller.registerRoom(room, transform: transform, liveBounds: bounds)
        }
        XCTAssertTrue(controller.document.lines.isEmpty)
        XCTAssertTrue(try store.load().lines.isEmpty, "Deleted lines cannot be regenerated")
        XCTAssertTrue(controller.document.seededScenes.isEmpty)
    }

    func testRoomLabelsStayWithTheirOwnerAcrossEntryCameraMotionMoveAndReload() throws {
        let store = GroundTruthLineStore(url: temporaryURL())
        let controller = GroundTruthLabelController(store: store)
        let a = GroundTruthRoomPlacement(id: UUID(), sceneName: "Tutorial_01",
            position: .zero, bounds: CGRect(x: 0, y: 0, width: 640, height: 360))
        let b = GroundTruthRoomPlacement(id: UUID(), sceneName: "Room_B",
            position: CGPoint(x: 700, y: 0), bounds: a.bounds)
        let ta = transform(camera: .zero)
        let tb = GroundTruthCameraTransform(sceneName: b.sceneName,
            cameraWorld: CGPoint(x: 50, y: 20), pixelsPerWorldUnitX: 20,
            pixelsPerWorldUnitY: 15, frameSize: a.bounds.size)
        controller.registerRoom(a, transform: ta, liveBounds: a.atlasBounds)
        func gesture(_ from: CGPoint, _ to: CGPoint, rooms: [GroundTruthRoomPlacement]) {
            controller.handle(phase: .began, atlasPoint: from, atlasTolerance: 0, rooms: rooms)
            controller.handle(phase: .ended, atlasPoint: to, atlasTolerance: 0, rooms: rooms)
        }
        gesture(CGPoint(x: 330, y: 200), CGPoint(x: 380, y: 200), rooms: [a])
        let originalA = controller.document.lines[0]
        let originalOverlay = controller.overlay(rooms: [a]).lines[0]
        controller.registerRoom(b, transform: tb, liveBounds: b.atlasBounds)
        XCTAssertEqual(controller.document.lines, [originalA], "Entering another room must not seed copied lines")
        gesture(CGPoint(x: 1040, y: 210), CGPoint(x: 1100, y: 210), rooms: [a, b])
        XCTAssertEqual(controller.document.lines.map(\.sceneName), [a.sceneName, b.sceneName])
        XCTAssertEqual(controller.lineCount(in: a.sceneName), 1)
        XCTAssertEqual(controller.lineCount(in: b.sceneName), 1)
        XCTAssertEqual(controller.lineCount(in: "Unknown"), 0)
        XCTAssertEqual(controller.document.lines.count, 2)
        XCTAssertEqual(controller.overlay(rooms: [a, b]).lines[0].start, originalOverlay.start)
        let beforeMove = controller.overlay(rooms: [a, b]).lines
        let movedB = GroundTruthRoomPlacement(id: b.id, sceneName: b.sceneName,
            position: CGPoint(x: 900, y: 30), bounds: b.bounds)
        let afterMove = controller.overlay(rooms: [a, movedB]).lines
        XCTAssertEqual(afterMove[0].start, beforeMove[0].start)
        XCTAssertEqual(afterMove[1].start.x - beforeMove[1].start.x, 200, accuracy: 0.001)
        XCTAssertEqual(afterMove[1].start.y - beforeMove[1].start.y, 30, accuracy: 0.001)
        let movedCamera = GroundTruthCameraTransform(sceneName: b.sceneName,
            cameraWorld: CGPoint(x: 52, y: 22), pixelsPerWorldUnitX: 20,
            pixelsPerWorldUnitY: 15, frameSize: a.bounds.size)
        controller.registerRoom(movedB, transform: movedCamera,
            liveBounds: movedB.atlasBounds.offsetBy(dx: 40, dy: 30))
        XCTAssertEqual(controller.overlay(rooms: [a, movedB]).lines[1].start, afterMove[1].start)
        let reloaded = GroundTruthLabelController(store: store)
        XCTAssertEqual(reloaded.document, controller.document)
        XCTAssertEqual(reloaded.overlay(rooms: [a, movedB]).lines.map(\.start), afterMove.map(\.start))
        reloaded.mode = .modify
        reloaded.handle(phase: .began, atlasPoint: originalOverlay.start, atlasTolerance: 0, rooms: [a, movedB])
        reloaded.handle(phase: .ended, atlasPoint: CGPoint(x: 320, y: 200), atlasTolerance: 0, rooms: [a, movedB])
        XCTAssertEqual(reloaded.document.lines[0].sceneName, a.sceneName)
        XCTAssertEqual(reloaded.document.lines[0].minimumWorldX, 0, accuracy: 0.001)
        XCTAssertEqual(reloaded.document.lines[1], controller.document.lines[1])
        let beforeOverlap = reloaded.document.lines[0]
        let overlappingB = GroundTruthRoomPlacement(id: b.id, sceneName: b.sceneName,
            position: CGPoint(x: 0, y: -10), bounds: b.bounds)
        reloaded.handle(phase: .began, atlasPoint: CGPoint(x: 340, y: 200),
            atlasTolerance: 0, rooms: [a, overlappingB])
        reloaded.handle(phase: .ended, atlasPoint: CGPoint(x: 350, y: 200),
            atlasTolerance: 0, rooms: [a, overlappingB])
        XCTAssertEqual(reloaded.document.lines[0], beforeOverlap,
                       "An overlapping room's body cannot steal another room's endpoint")
        XCTAssertEqual(reloaded.document.lines[1].minimumWorldX, 51.5, accuracy: 0.001)
    }

    func testGroundDragKeepsItsStartingRoomAndUndoRetainsNewRoomCalibration() {
        let controller = controller()
        let a = GroundTruthRoomPlacement(id: UUID(), sceneName: "Tutorial_01",
            position: .zero, bounds: CGRect(x: 0, y: 0, width: 640, height: 360))
        let b = GroundTruthRoomPlacement(id: UUID(), sceneName: "Room_B",
            position: CGPoint(x: 700, y: 0), bounds: a.bounds)
        controller.registerRoom(a, transform: transform(camera: .zero), liveBounds: a.atlasBounds)
        controller.handle(phase: .began, atlasPoint: CGPoint(x: 630, y: 200), atlasTolerance: 0, rooms: [a, b])
        controller.handle(phase: .ended, atlasPoint: CGPoint(x: 750, y: 200), atlasTolerance: 0, rooms: [a, b])
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertEqual(controller.document.lines[0].sceneName, a.sceneName)
        XCTAssertEqual(controller.document.lines[0].maximumWorldX, 43, accuracy: 0.001)
        let tb = GroundTruthCameraTransform(sceneName: b.sceneName, cameraWorld: .zero,
            pixelsPerWorldUnitX: 10, pixelsPerWorldUnitY: 10, frameSize: a.bounds.size)
        controller.registerRoom(b, transform: tb, liveBounds: b.atlasBounds)
        controller.undo()
        XCTAssertTrue(controller.document.lines.isEmpty)
        XCTAssertEqual(controller.document.roomProjections?.count, 2)
    }

    func testEndpointHandlesFollowVisibleLinesAndRemainReadableWhenZoomed() {
        let positive = GroundTruthWorldLine(sceneName: "Tutorial_01", minimumWorldX: 1,
            maximumWorldX: 3, worldY: 2, kind: .positive, seededFromDetector: false)
        var negative = positive
        negative.id = UUID(); negative.minimumWorldX = 4; negative.maximumWorldX = 6
        negative.kind = .negative
        var otherScene = positive
        otherScene.id = UUID(); otherScene.sceneName = "Other"
        var draft = positive
        draft.id = UUID(); draft.minimumWorldX = 7; draft.maximumWorldX = 9
        let overlay = GroundTruthLineProjection.overlay(
            document: GroundTruthLineDocument(lines: [positive, negative, otherScene]),
            transform: transform(camera: .zero),
            liveBounds: CGRect(x: 100, y: 200, width: 640, height: 360),
            selectedID: positive.id, draft: draft)

        XCTAssertEqual(overlay.lines.count, 3)
        XCTAssertEqual(overlay.markers.map(\.worldPosition), overlay.lines.flatMap { [$0.start, $0.end] })
        XCTAssertEqual(overlay.markers.map(\.kind), [
            .selectedGroundTruthEndpoint, .selectedGroundTruthEndpoint,
            .negativeGroundTruthEndpoint, .negativeGroundTruthEndpoint,
            .positiveGroundTruthEndpoint, .positiveGroundTruthEndpoint
        ])
        for zoom: CGFloat in [0.25, 1, 8] {
            let quads = LayerSceneFeatureReviewGeometry.markerQuads(overlay, zoom: zoom, pan: .zero)
            for index in overlay.markers.indices {
                let points = quads[(index * 4)..<(index * 4 + 4)].flatMap(\.points)
                XCTAssertEqual(points.map(\.x).max()! - points.map(\.x).min()!, 8, accuracy: 0.001)
                XCTAssertEqual(points.map(\.y).max()! - points.map(\.y).min()!, 8, accuracy: 0.001)
            }
        }
    }

    func testModifyResizesEachEndpointAndPrefersHandleOverOverlappingBody() {
        let controller = controller(), transform = transform(camera: .zero)
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        func point(_ x: CGFloat, _ y: CGFloat = 2) -> CGPoint {
            transform.worldToAtlas(CGPoint(x: x, y: y), liveBounds: bounds)
        }
        drag(controller, from: point(0), to: point(10), transform: transform, bounds: bounds)
        drag(controller, from: point(2), to: point(6), transform: transform, bounds: bounds)
        let original = controller.document
        controller.mode = .modify
        drag(controller, from: point(6), to: point(8, 5), transform: transform, bounds: bounds)
        XCTAssertEqual(controller.document.lines[0], original.lines[0])
        XCTAssertEqual(controller.document.lines[1].minimumWorldX, 2, accuracy: 0.000001)
        XCTAssertEqual(controller.document.lines[1].maximumWorldX, 8, accuracy: 0.000001)
        XCTAssertEqual(controller.document.lines[1].worldY, 2, accuracy: 0.000001)
        drag(controller, from: point(2), to: point(1, -3), transform: transform, bounds: bounds)
        XCTAssertEqual(controller.document.lines[1].minimumWorldX, 1, accuracy: 0.000001)
        XCTAssertEqual(controller.document.lines[1].maximumWorldX, 8, accuracy: 0.000001)
        XCTAssertEqual(controller.document.lines[1].worldY, 2, accuracy: 0.000001)
        controller.undo(); controller.undo()
        XCTAssertEqual(controller.document, original)
    }

    func testShortLineUsesNearestEndpointWhenHandleHitAreasOverlap() {
        let controller = controller(), transform = transform(camera: .zero)
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        func point(_ x: CGFloat) -> CGPoint {
            transform.worldToAtlas(CGPoint(x: x, y: 2), liveBounds: bounds)
        }
        // Five screen pixels separate these endpoints; both are inside the
        // viewport's eight-pixel grab tolerance when clicking the right end.
        drag(controller, from: point(1), to: point(1.5), transform: transform, bounds: bounds)
        controller.mode = .modify
        for (phase, position) in [(LayerSceneGroundLabelPointerPhase.began, point(1.5)),
                                  (.changed, point(2.5)), (.ended, point(2.5))] {
            controller.handle(phase: phase, atlasPoint: position, atlasTolerance: 8,
                transform: transform, liveBounds: bounds)
        }
        XCTAssertEqual(controller.document.lines.count, 1)
        XCTAssertEqual(controller.document.lines[0].minimumWorldX, 1, accuracy: 0.000001)
        XCTAssertEqual(controller.document.lines[0].maximumWorldX, 2.5, accuracy: 0.000001)
    }

    func testWallEditingUsesVerticalAxisAndPreservesIntersectingGroundOnReload() throws {
        let store = GroundTruthLineStore(url: temporaryURL())
        let editor = GroundTruthLabelController(store: store)
        let room = GroundTruthRoomPlacement(id: UUID(), sceneName: "Tutorial_01", position: .zero,
            bounds: CGRect(x: 0, y: 0, width: 640, height: 360))
        let camera = GroundTruthCameraTransform(sceneName: room.sceneName, cameraWorld: .zero,
            pixelsPerWorldUnitX: 4, pixelsPerWorldUnitY: 10, frameSize: room.bounds.size)
        editor.registerRoom(room, transform: camera, liveBounds: room.atlasBounds)
        func stroke(_ from: CGPoint, _ to: CGPoint) {
            for (phase, point) in [(LayerSceneGroundLabelPointerPhase.began, from), (.changed, to), (.ended, to)] {
                editor.handle(phase: phase, atlasPoint: point, atlasTolerance: 2, rooms: [room])
            }
        }
        stroke(CGPoint(x: 310, y: 180), CGPoint(x: 360, y: 180))
        let ground = try XCTUnwrap(editor.document.lines.first)
        editor.selectAxis(.vertical)
        stroke(CGPoint(x: 320, y: 210), CGPoint(x: 350, y: 208))
        XCTAssertEqual(editor.lineCount(), 0)
        stroke(CGPoint(x: 320, y: 210), CGPoint(x: 350, y: 150))
        let wall = try XCTUnwrap(editor.document.walls?.last)
        XCTAssertEqual(wall.kind, .wall)
        XCTAssertEqual(wall.start, CGPoint(x: 0, y: -3))
        XCTAssertEqual(wall.end, CGPoint(x: 0, y: 3))
        XCTAssertEqual(editor.lineCount(in: room.sceneName), 1)
        editor.mode = .modify
        stroke(CGPoint(x: 320, y: 150), CGPoint(x: 370, y: 140))
        stroke(CGPoint(x: 320, y: 210), CGPoint(x: 275, y: 220))
        stroke(CGPoint(x: 320, y: 180), CGPoint(x: 324, y: 190))
        let saved = try store.load()
        XCTAssertEqual(saved.lines, [ground])
        XCTAssertEqual(saved.walls?.last?.start, CGPoint(x: 1, y: -3))
        XCTAssertEqual(saved.walls?.last?.end, CGPoint(x: 1, y: 5))
        let movedRoom = GroundTruthRoomPlacement(id: room.id, sceneName: room.sceneName,
            position: CGPoint(x: 500, y: 20), bounds: room.bounds)
        let overlay = GroundTruthLabelController(store: store).overlay(rooms: [movedRoom])
        XCTAssertEqual(overlay.lines.last?.start, CGPoint(x: 824, y: 170))
        XCTAssertEqual(overlay.lines.last?.end, CGPoint(x: 824, y: 250))
        XCTAssertEqual(overlay.lines.last?.kind, .positiveGroundTruth)
        XCTAssertEqual(overlay.markers.suffix(2).map(\.kind), [.positiveGroundTruthEndpoint, .positiveGroundTruthEndpoint])
        XCTAssertEqual(BotHumanGroundProjection.segments(lines: saved.lines, transform: camera,
            frameSize: camera.frameSize).count, 1)
        editor.mode = .delete
        stroke(CGPoint(x: 324, y: 180), CGPoint(x: 324, y: 180))
        XCTAssertEqual(editor.document.lines, [ground])
        XCTAssertNil(editor.document.walls)
        editor.undo()
        XCTAssertEqual(editor.document, saved)
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
