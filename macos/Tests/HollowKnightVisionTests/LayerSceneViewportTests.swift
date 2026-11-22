import AppKit
import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class LayerSceneViewportTests: XCTestCase {
    @MainActor
    func testHackerDoorDragRoutesThroughViewportToLiveRoomAndPersists() throws {
        let store = GroundTruthLineStore(url: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString))
        let editor = GroundTruthLabelController(store: store)
        let bounds = HackerTerrainRoomGeometry.editingBounds(roomBounds: CGRect(x: 0, y: 0, width: 50, height: 50),
            position: .zero, isCurrent: true, liveBounds: CGRect(x: 0, y: 0, width: 200, height: 200))
        let room = GroundTruthRoomPlacement(id: UUID(), sceneName: "room", position: .zero, bounds: bounds)
        let camera = GroundTruthCameraTransform(sceneName: "room", cameraWorld: .zero,
            pixelsPerWorldUnitX: 10, pixelsPerWorldUnitY: 10, frameSize: CGSize(width: 200, height: 200))
        editor.registerRoom(room, transform: camera, liveBounds: bounds)
        editor.selectDoor()
        let coordinator = LayerSceneViewport.Coordinator()
        coordinator.mode = .world
        coordinator.groundLabelEditingEnabled = HackerEditingTarget.door.editsTerrain
        coordinator.onGroundLabelPointer = { phase, point, tolerance in
            editor.handle(phase: phase, atlasPoint: point, atlasTolerance: tolerance, rooms: [room])
        }
        let view = LayerMetalView(frame: CGRect(x: 0, y: 0, width: 400, height: 200), device: nil)
        view.coordinator = coordinator
        func event(_ type: NSEvent.EventType, _ point: CGPoint) -> NSEvent {
            NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: 1,
                               windowNumber: 0, context: nil, eventNumber: 1, clickCount: 1, pressure: 1)!
        }
        view.mouseDown(with: event(.leftMouseDown, CGPoint(x: 100, y: 100)))
        view.mouseDragged(with: event(.leftMouseDragged, CGPoint(x: 150, y: 100)))
        view.mouseUp(with: event(.leftMouseUp, CGPoint(x: 150, y: 100)))
        XCTAssertEqual(editor.document.doors?.count, 1)
        XCTAssertEqual(try store.load().doors?.first?.kind, .door)
        XCTAssertEqual(editor.overlay(rooms: [room]).lines.last?.kind, .selectedGroundTruth)
        editor.selectAxis(.horizontal)
        XCTAssertEqual(editor.overlay(rooms: [room]).lines.last?.kind, .doorGroundTruth)
        let exitBounds = HackerTerrainRoomGeometry.editingBounds(roomBounds: bounds, position: .zero, isCurrent: true,
            liveBounds: bounds, pathBounds: CGRect(x: 30, y: 100, width: 240, height: 12))
        let exitRoom = GroundTruthRoomPlacement(id: room.id, sceneName: room.sceneName, position: .zero, bounds: exitBounds)
        editor.selectDoor()
        coordinator.onGroundLabelPointer = { phase, point, tolerance in
            editor.handle(phase: phase, atlasPoint: point, atlasTolerance: tolerance, rooms: [exitRoom])
        }
        view.mouseDown(with: event(.leftMouseDown, CGPoint(x: 270, y: 100)))
        view.mouseDragged(with: event(.leftMouseDragged, CGPoint(x: 270, y: 140)))
        view.mouseUp(with: event(.leftMouseUp, CGPoint(x: 270, y: 140)))
        XCTAssertEqual(try store.load().doors?.map(\.kind), [.door, .doorVertical])
    }

    @MainActor
    func testObjectiveViewportClickHitsPathBeyondVisitedImage() {
        let paths = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        paths.setBase(room: "room", floors: [.init(left: 0, right: 300, y: 50)], fallbackHeight: 20)
        let coordinator = LayerSceneViewport.Coordinator()
        coordinator.mode = .world
        var selected: String?
        coordinator.onPathSelect = { point, tolerance in
            selected = paths.selectObjective(at: point, tolerance: tolerance, in: [("room", .zero)])
            return selected != nil
        }
        let view = LayerMetalView(frame: CGRect(x: 0, y: 0, width: 400, height: 200), device: nil)
        view.coordinator = coordinator
        let click = NSEvent.mouseEvent(with: .leftMouseDown, location: CGPoint(x: 250, y: 70),
            modifierFlags: [], timestamp: 1, windowNumber: 0, context: nil, eventNumber: 1, clickCount: 1, pressure: 1)!
        view.mouseDown(with: click)
        XCTAssertEqual(selected, "room")
        XCTAssertTrue(paths.hasUnroutedObjective(room: "room"))
    }

    func testProvisionalLinesAreDashedInScreenSpaceAndBoundedAtExtremeZoom() {
        let line = LayerSceneFeatureReviewOverlay.Line(start: .zero,
            end: CGPoint(x: 120, y: 0), kind: .provisionalIsland)
        let overlay = LayerSceneFeatureReviewOverlay(markers: [], lines: [line])
        let quads = LayerSceneFeatureReviewGeometry.lineQuads(overlay, zoom: 1, pan: .zero)
        XCTAssertEqual(quads.count, 10)
        XCTAssertEqual(quads[0].points[2].x, 7.2, accuracy: 0.001)
        XCTAssertEqual(quads[1].points[0].x, 12, accuracy: 0.001)
        XCTAssertEqual(LayerSceneFeatureReviewGeometry.lineQuads(overlay, zoom: 2, pan: .zero).count, 20)
        XCTAssertEqual(LayerSceneFeatureReviewGeometry.lineQuads(overlay, zoom: 1_000_000, pan: .zero).count, 64)
        let many = LayerSceneFeatureReviewOverlay(markers: [], lines: Array(repeating: line, count: 10_000))
        XCTAssertEqual(LayerSceneFeatureReviewGeometry.lineQuads(many, zoom: 1_000_000, pan: .zero).count,
                       LayerSceneFeatureReviewGeometry.maximumLines)
    }

    func testInvisibleIslandsCannotExhaustVisibleOutlineBudget() {
        let hidden = LayerSceneFeatureReviewOverlay.Line(start: CGPoint(x: -10_000, y: 40),
            end: CGPoint(x: -8_000, y: 40), kind: .provisionalIsland)
        let visible = LayerSceneFeatureReviewOverlay.Line(start: CGPoint(x: 10, y: 40),
            end: CGPoint(x: 130, y: 40), kind: .doorwayFloor)
        let overlay = LayerSceneFeatureReviewOverlay(markers: [], lines: Array(repeating: hidden, count: 200) + [visible])
        let quads = LayerSceneFeatureReviewGeometry.lineQuads(overlay, zoom: 1, pan: .zero,
            viewport: CGRect(x: 0, y: 0, width: 400, height: 300))
        XCTAssertEqual(quads.count, 10)
        XCTAssertTrue(quads.allSatisfy { $0.lineKind == .doorwayFloor })
    }

    func testHugeCrossingEdgeIsClippedBeforeDashGeneration() {
        let crossing = LayerSceneFeatureReviewOverlay.Line(start: CGPoint(x: -1_000_000_000, y: 40),
            end: CGPoint(x: 1_000_000_000, y: 40), kind: .provisionalIsland)
        let missesCorner = LayerSceneFeatureReviewOverlay.Line(start: CGPoint(x: -100, y: 10),
            end: CGPoint(x: 10, y: -100), kind: .doorwayFloor)
        let overlay = LayerSceneFeatureReviewOverlay(markers: [], lines: [crossing, missesCorner])
        let quads = LayerSceneFeatureReviewGeometry.lineQuads(overlay, zoom: 1, pan: .zero,
            viewport: CGRect(x: 0, y: 0, width: 320, height: 180))
        XCTAssertFalse(quads.isEmpty)
        XCTAssertLessThanOrEqual(quads.count, 28)
        XCTAssertTrue(quads.allSatisfy { $0.lineKind == .provisionalIsland })
        XCTAssertTrue(quads.flatMap(\.points).allSatisfy { $0.x >= -2 && $0.x <= 322 })
    }

    func testFreeFlyOwnsPointerDragInsteadOfForwardingItToGame() {
        XCTAssertFalse(LayerScenePointerPolicy.forwardsGamePointer(framingMode: .freeFly))
        XCTAssertTrue(LayerScenePointerPolicy.forwardsGamePointer(framingMode: .fit))
        XCTAssertTrue(LayerScenePointerPolicy.forwardsGamePointer(framingMode: .current))
    }
    func testRoomBoundaryUsesThinCyanLine() {
        let cyan = LayerSceneFeatureReviewPalette.rgba(for: .roomBoundary)
        XCTAssertGreaterThan(cyan.y, cyan.x)
        XCTAssertGreaterThan(cyan.z, cyan.x)
        XCTAssertEqual(LayerSceneFeatureReviewGeometry.lineWidth, 1)
    }
    private func tile(factor: Double = 2, order: Int = 3, width: CGFloat = 100) -> LayerSceneDrawTile { LayerSceneDrawTile(roomID: UUID(), layerID: UUID(), image: CGImage(width: 1, height: 1, bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: 1, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: [], provider: CGDataProvider(data: Data([255]) as CFData)!, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!, bounds: CGRect(x: 10,y:20,width:width,height:50), factor: factor, order: order, roomPosition: CGPoint(x: 200,y:300), roomScale: 2) }
    func testRoomProjectionUsesNegativeFactorCameraShift() { let t=tile(); XCTAssertEqual(LayerSceneProjection.project(t,mode:.room,camera:CGPoint(x:4,y:5)).origin,CGPoint(x:2,y:10)) }
    func testSavedAndCurrentFloorsShareGreenWhileReferencePatchesUsePurple() {
        let tile = LayerSceneFeatureReviewPalette.rgba(for: .groundFeature)
        let selection = LayerSceneFeatureReviewPalette.rgba(for: .selectedGroundFeature)
        let line = LayerSceneFeatureReviewPalette.rgba(for: .persistentGround)
        XCTAssertNotEqual(tile, selection)
        XCTAssertGreaterThan(tile.z, tile.x)
        XCTAssertGreaterThan(tile.x, tile.y)
        XCTAssertEqual(line, LayerSceneFeatureReviewPalette.rgba(for: .currentGround))
        XCTAssertGreaterThan(line.y, line.z)
    }
    func testLiveGroundReviewPaletteMatchesLegend() {
        let green = LayerSceneFeatureReviewPalette.rgba(for: .cameraConsistentGround)
        let orange = LayerSceneFeatureReviewPalette.rgba(for: .occludedGroundFeature)
        let red = LayerSceneFeatureReviewPalette.rgba(for: .groundFeatureDepthError)
        let currentLine = LayerSceneFeatureReviewPalette.rgba(for: .currentGround)
        XCTAssertGreaterThan(green.y, green.x)
        XCTAssertGreaterThan(orange.x, orange.y)
        XCTAssertGreaterThan(red.x, red.y)
        XCTAssertEqual(green.x, currentLine.x)
        XCTAssertEqual(green.y, currentLine.y)
        XCTAssertEqual(green.z, currentLine.z)
    }
    func testWorldProjectionDoesNotUseCamera() { let t=tile(); XCTAssertEqual(LayerSceneProjection.project(t,mode:.world,camera:CGPoint(x:99,y:99)).origin,CGPoint(x:220,y:340)) }
    func testUnprojectReturnsAtlasQ() { let t=tile(); XCTAssertEqual(LayerSceneProjection.unproject(CGPoint(x:60,y:45),tile:t,mode:.room,camera:.zero),CGPoint(x:60,y:45)) }
    func testProjectionBoundsIgnoresNonFiniteTile() {
        let invalid = LayerSceneDrawTile(roomID: UUID(), layerID: UUID(), image: tile().image, bounds: CGRect(x: CGFloat.infinity, y: 0, width: 10, height: 10), factor: 1, order: 0, roomPosition: .zero, roomScale: 1)
        XCTAssertEqual(LayerSceneProjection.bounds(for: [invalid, tile()], mode: .world, camera: .zero), CGRect(x: 220, y: 340, width: 200, height: 100))
        XCTAssertNil(LayerSceneProjection.bounds(for: [invalid], mode: .world, camera: .zero))
    }
    func testProjectionRejectsRectWhoseExtentOverflows() {
        XCTAssertFalse(LayerSceneProjection.isFinite(CGRect(x: CGFloat.greatestFiniteMagnitude, y: 0, width: CGFloat.greatestFiniteMagnitude, height: 10)))
    }
    func testFreeFlyKeepsViewportWhenAtlasBoundsGrow() {
        let coordinator = LayerSceneViewport.Coordinator()
        coordinator.mode = .world
        coordinator.tiles = [tile()]
        coordinator.zoom = 2
        coordinator.pan = CGPoint(x: 40, y: 50)
        coordinator.setFraming(.freeFly, request: 0, in: CGSize(width: 800, height: 600), at: 0)
        coordinator.tiles = [tile(width: 240)]
        coordinator.setFraming(.freeFly, request: 0, in: CGSize(width: 800, height: 600), at: 1)
        XCTAssertEqual(coordinator.transform,
            LayerSceneViewTransform(zoom: 2, pan: CGPoint(x: 40, y: 50)))
    }
    func testTranslationSettlerInterpolatesMonotonicallyWithoutOvershoot() {
        var settler = LayerSceneTranslationSettler()
        settler.settle(to: CGPoint(x: 20, y: -10), at: 0)

        XCTAssertEqual(settler.offset(at: 0), .zero)
        XCTAssertEqual(settler.offset(at: 0.1), CGPoint(x: 10, y: -5))
        XCTAssertEqual(settler.offset(at: 0.2), CGPoint(x: 20, y: -10))
        XCTAssertEqual(settler.offset(at: 11), CGPoint(x: 20, y: -10))
    }
    func testTranslationSettlerInterruptsFromCurrentlyDisplayedOffset() {
        var settler = LayerSceneTranslationSettler()
        settler.settle(to: CGPoint(x: 20, y: 0), at: 0)
        XCTAssertEqual(settler.offset(at: 0.1), CGPoint(x: 10, y: 0))

        settler.settle(to: CGPoint(x: 30, y: 0), at: 0.1)
        XCTAssertEqual(settler.currentOffset, CGPoint(x: 10, y: 0))
        XCTAssertEqual(settler.offset(at: 0.2), CGPoint(x: 20, y: 0))
        XCTAssertEqual(settler.offset(at: 0.31), CGPoint(x: 30, y: 0))
    }
    func testTranslationSettlerSupportsZeroDuration() {
        var settler = LayerSceneTranslationSettler(duration: 0)
        settler.settle(to: CGPoint(x: 8, y: -3), at: 4)
        XCTAssertEqual(settler.offset(at: 4), CGPoint(x: 8, y: -3))
    }
    func testBasisCorrectionSettlesAtlasWhileLivePanRemainsAuthoritative() {
        let coordinator = LayerSceneViewport.Coordinator()
        coordinator.pan = CGPoint(x: 300, y: 200)
        coordinator.zoom = 2
        coordinator.setInitialWorldBasisOffset(.zero)

        coordinator.updateWorldBasisOffset(CGPoint(x: 20, y: -10), at: 1)

        XCTAssertEqual(coordinator.pan, CGPoint(x: 300, y: 200))
        XCTAssertEqual(coordinator.atlasPan(at: 1), CGPoint(x: 260, y: 220))
        XCTAssertEqual(coordinator.atlasPan(at: 1.1), CGPoint(x: 280, y: 210))
        XCTAssertEqual(coordinator.atlasPan(at: 1.2), CGPoint(x: 300, y: 200))
    }
    func testZoomKeepsWorldPointUnderMouseCursor() {
        let coordinator = LayerSceneViewport.Coordinator()
        coordinator.zoom = 2
        coordinator.pan = CGPoint(x: 10, y: 20)
        let cursor = CGPoint(x: 110, y: 220)

        coordinator.zoom(by: 2, around: cursor)

        XCTAssertEqual(coordinator.zoom, 4)
        XCTAssertEqual(coordinator.pan, CGPoint(x: -90, y: -180))
        XCTAssertEqual(
            CGPoint(x: 50 * coordinator.zoom + coordinator.pan.x,
                    y: 100 * coordinator.zoom + coordinator.pan.y),
            cursor
        )
    }
    func testCurrentZoomStaysCenteredAndTracksMovingViewport() throws {
        let coordinator = LayerSceneViewport.Coordinator()
        let size = CGSize(width: 1_280, height: 720)
        coordinator.focusPoint = CGPoint(x: 340, y: 220)
        coordinator.followBounds = CGRect(x: 20, y: 40, width: 640, height: 360)
        coordinator.setFraming(.current, request: 0, in: size, at: 0)
        let cursor = CGPoint(x: 300, y: 260)
        coordinator.zoomCurrent(by: 0.5, around: cursor, in: size)

        XCTAssertEqual(coordinator.framingMode, .current)
        XCTAssertEqual(
            CGPoint(
                x: coordinator.focusPoint!.x * coordinator.zoom + coordinator.pan.x,
                y: coordinator.focusPoint!.y * coordinator.zoom + coordinator.pan.y
            ),
            CGPoint(x: size.width * 0.5, y: size.height * 0.5)
        )
        let zoomed = coordinator.zoom

        coordinator.focusPoint = CGPoint(x: 440, y: 220)
        coordinator.setFraming(.current, request: 0, in: size, at: 1)

        XCTAssertEqual(coordinator.zoom, zoomed)
        XCTAssertEqual(
            CGPoint(
                x: coordinator.focusPoint!.x * coordinator.zoom + coordinator.pan.x,
                y: coordinator.focusPoint!.y * coordinator.zoom + coordinator.pan.y
            ),
            CGPoint(x: size.width * 0.5, y: size.height * 0.5)
        )
    }
    func testCurrentButtonResetsCustomZoomAndCentersViewport() {
        let coordinator = LayerSceneViewport.Coordinator()
        let size = CGSize(width: 1_280, height: 720)
        let focus = CGPoint(x: 340, y: 220)
        coordinator.focusPoint = focus
        coordinator.followBounds = CGRect(x: 20, y: 40, width: 640, height: 360)
        coordinator.setFraming(.current, request: 0, in: size, at: 0)
        coordinator.zoomCurrent(by: 0.5, around: CGPoint(x: 300, y: 260), in: size)

        coordinator.setFraming(.current, request: 1, in: size, at: 1)
        coordinator.advanceViewportTransition(at: 1 + LayerSceneViewportTransition.duration)

        XCTAssertEqual(coordinator.zoom, 1.76, accuracy: 0.000_001)
        XCTAssertEqual(
            CGPoint(
                x: focus.x * coordinator.zoom + coordinator.pan.x,
                y: focus.y * coordinator.zoom + coordinator.pan.y
            ),
            CGPoint(x: size.width * 0.5, y: size.height * 0.5)
        )
    }
    func testGamePointerUsesDrawnLiveFrameInsteadOfWholeVisionWindow() throws {
        let normalized = try XCTUnwrap(GamePointerProjection.normalizedPoint(
            viewPoint: CGPoint(x: 300, y: 275),
            displayedLiveFrame: CGRect(x: 100, y: 50, width: 800, height: 450)
        ))
        XCTAssertEqual(normalized, CGPoint(x: 0.25, y: 0.5))
        XCTAssertNil(GamePointerProjection.normalizedPoint(
            viewPoint: CGPoint(x: 50, y: 275),
            displayedLiveFrame: CGRect(x: 100, y: 50, width: 800, height: 450)
        ))

        let command = ReceiverPointerCommand(
            sessionID: UUID(),
            sequence: 7,
            event: GamePointerEvent(kind: .moved, normalizedPoint: normalized, clickCount: 1)
        )
        XCTAssertEqual(command.kind, "moved")
        XCTAssertEqual(command.normalizedX, 0.25, accuracy: 0.001)
        XCTAssertEqual(command.normalizedY, 0.5, accuracy: 0.001)
    }
    func testFitButtonStartsSmoothTransitionFromFreeFly() {
        let coordinator = LayerSceneViewport.Coordinator()
        coordinator.mode = .world
        coordinator.tiles = [tile()]
        coordinator.zoom = 2
        coordinator.pan = CGPoint(x: 40, y: 50)
        coordinator.didReceiveTiles = true
        coordinator.setFraming(.freeFly, request: 0, in: CGSize(width: 800, height: 600), at: 0)
        coordinator.setFraming(.fit, request: 1, in: CGSize(width: 800, height: 600), at: 1)
        XCTAssertEqual(coordinator.zoom, 2)
        XCTAssertEqual(coordinator.pan, CGPoint(x: 40, y: 50))
        coordinator.advanceViewportTransition(at: 1 + LayerSceneViewportTransition.duration / 2)
        XCTAssertNotEqual(coordinator.zoom, 2)
        XCTAssertNotEqual(coordinator.zoom, coordinator.targetTransform(for: .fit, in: CGSize(width: 800, height: 600))?.zoom)
        coordinator.advanceViewportTransition(at: 1 + LayerSceneViewportTransition.duration)
        XCTAssertEqual(coordinator.transform,
            coordinator.targetTransform(for: .fit, in: CGSize(width: 800, height: 600)))
    }
    func testFitFollowsGrowingAtlasBoundsWithoutStepping() throws {
        let coordinator = LayerSceneViewport.Coordinator()
        let size = CGSize(width: 800, height: 600)
        coordinator.mode = .world
        coordinator.tiles = [tile(width: 100)]
        coordinator.setFraming(.fit, request: 0, in: size, at: 0)
        let initial = coordinator.transform

        coordinator.tiles = [tile(width: 400)]
        coordinator.setFraming(.fit, request: 0, in: size, at: 1)
        let target = try XCTUnwrap(coordinator.targetTransform(for: .fit, in: size))
        XCTAssertEqual(coordinator.transform, initial)

        coordinator.advanceViewportTransition(at: 1 + 1.0 / 60.0)
        XCTAssertLessThan(coordinator.zoom, initial.zoom)
        XCTAssertGreaterThan(coordinator.zoom, target.zoom)
        let firstStep = initial.zoom - coordinator.zoom
        XCTAssertLessThan(firstStep, (initial.zoom - target.zoom) * 0.1)

        for frame in 2...90 {
            coordinator.advanceViewportTransition(at: 1 + Double(frame) / 60.0)
        }
        XCTAssertEqual(coordinator.zoom, target.zoom, accuracy: 0.01)
        XCTAssertEqual(coordinator.pan.x, target.pan.x, accuracy: 2)
        XCTAssertEqual(coordinator.pan.y, target.pan.y, accuracy: 2)
    }
    func testFitUsesExactContentBoundsBeforeStorageTileChanges() throws {
        let coordinator = LayerSceneViewport.Coordinator()
        let size = CGSize(width: 800, height: 600)
        coordinator.mode = .world
        coordinator.tiles = [tile(width: 256)]
        coordinator.fitBounds = CGRect(x: 0, y: 0, width: 200, height: 100)
        coordinator.setFraming(.fit, request: 0, in: size, at: 0)
        let initial = coordinator.transform

        coordinator.fitBounds = CGRect(x: 0, y: 0, width: 201, height: 100)
        coordinator.setFraming(.fit, request: 0, in: size, at: 1)
        let target = try XCTUnwrap(coordinator.targetTransform(for: .fit, in: size))
        XCTAssertLessThan(target.zoom, initial.zoom)
        XCTAssertEqual(coordinator.transform, initial)

        coordinator.advanceViewportTransition(at: 1 + 1.0 / 60.0)
        XCTAssertLessThan(coordinator.zoom, initial.zoom)
        XCTAssertGreaterThan(coordinator.zoom, target.zoom)
    }
    func testTransitionInterruptsAtDisplayedTransform() {
        let first = LayerSceneViewTransform(zoom: 1, pan: .zero)
        let destination = LayerSceneViewTransform(zoom: 3, pan: CGPoint(x: 100, y: -60))
        let transition = LayerSceneViewportTransition(start: first, target: destination, startedAt: 0)
        let midpoint = transition.value(at: LayerSceneViewportTransition.duration / 2)
        XCTAssertEqual(midpoint.zoom, 2)
        XCTAssertEqual(midpoint.pan, CGPoint(x: 50, y: -30))
        let interrupted = LayerSceneViewportTransition(start: midpoint, target: first, startedAt: 1)
        XCTAssertEqual(interrupted.value(at: 1), midpoint)
    }
    func testFollowFrameCentersViewportWithSmallSurroundingMargin() {
        let coordinator = LayerSceneViewport.Coordinator()
        let liveFrame = CGRect(x: 20, y: 40, width: 640, height: 360)
        let viewportCenter = CGPoint(x: liveFrame.midX, y: liveFrame.midY)

        coordinator.frame(
            liveFrame,
            centeredOn: viewportCenter,
            in: CGSize(width: 1_280, height: 720)
        )

        XCTAssertEqual(coordinator.zoom, 1.76, accuracy: 0.000_001)
        XCTAssertEqual(
            CGPoint(
                x: viewportCenter.x * coordinator.zoom + coordinator.pan.x,
                y: viewportCenter.y * coordinator.zoom + coordinator.pan.y
            ),
            CGPoint(x: 640, y: 360)
        )
    }
    func testFeatureOverlayUsesSamePanAndZoomProjection() {
        let overlay = LayerSceneFeatureReviewOverlay(markers: [.init(worldPosition: CGPoint(x: 10, y: 20), kind: .groundFeature)], lines: [])
        let quad = LayerSceneFeatureReviewGeometry.markerQuads(overlay, zoom: 3, pan: CGPoint(x: 5, y: 7))[0]
        XCTAssertEqual(quad.points[0], CGPoint(x: 31, y: 63))
        XCTAssertEqual(quad.points[3], CGPoint(x: 39, y: 64))
    }
    func testFeatureMarkersRemainScreenConstantAcrossZoom() {
        let overlay = LayerSceneFeatureReviewOverlay(markers: [.init(worldPosition: CGPoint(x: 10, y: 20), kind: .groundFeature)], lines: [])
        let near = LayerSceneFeatureReviewGeometry.markerQuads(overlay, zoom: 1, pan: .zero)[0]
        let far = LayerSceneFeatureReviewGeometry.markerQuads(overlay, zoom: 10, pan: .zero)[0]
        XCTAssertEqual(near.points[1].x - near.points[0].x, 8)
        XCTAssertEqual(far.points[1].x - far.points[0].x, 8)
    }
    func testGroundFeatureUsesOnePixelOutlinedPurpleSquare() {
        let overlay = LayerSceneFeatureReviewOverlay(
            markers: [.init(worldPosition: CGPoint(x: 10, y: 20), kind: .groundFeature)],
            lines: []
        )
        let quads = LayerSceneFeatureReviewGeometry.markerQuads(
            overlay, zoom: 1, pan: .zero
        )
        XCTAssertEqual(quads.count, 4)
        let purple = LayerSceneFeatureReviewPalette.rgba(for: .groundFeature)
        XCTAssertGreaterThan(purple.z, purple.x)
        XCTAssertGreaterThan(purple.x, purple.y)
        XCTAssertEqual(LayerSceneFeatureReviewGeometry.lineWidth, 1)
    }
    func testPersistentGroundCellKeepsSixteenByTwelveWorldSize() {
        let overlay = LayerSceneFeatureReviewOverlay(
            markers: [.init(
                worldPosition: CGPoint(x: 32, y: 48),
                kind: .groundFeature,
                worldSize: CGSize(width: 16, height: 12)
            )],
            lines: []
        )
        let near = LayerSceneFeatureReviewGeometry.markerQuads(
            overlay, zoom: 1, pan: .zero
        )
        let zoomed = LayerSceneFeatureReviewGeometry.markerQuads(
            overlay, zoom: 2, pan: .zero
        )

        XCTAssertEqual(near[0].points[1].x - near[0].points[0].x, 16)
        XCTAssertEqual(zoomed[0].points[1].x - zoomed[0].points[0].x, 32)
        XCTAssertEqual(near[2].points[3].y - near[2].points[0].y, 10)
    }
    func testGroundFeatureHitTestingReturnsRegisteredCellDetails() throws {
        let details = LayerSceneGroundFeatureDetails(
            segmentID: 12,
            sequenceIndex: 4,
            worldPosition: CGPoint(x: 32, y: 48),
            referencePixels: [UInt8](
                repeating: 80,
                count: GroundHypothesisTracker.featurePixelCount
            )
        )
        let overlay = LayerSceneFeatureReviewOverlay(
            markers: [.init(
                worldPosition: details.worldPosition,
                kind: .groundFeature,
                worldSize: CGSize(width: 16, height: 12),
                groundFeatureDetails: details
            )],
            lines: []
        )

        let selected = LayerSceneFeatureReviewGeometry.groundFeature(
            at: CGPoint(x: 69, y: 101),
            in: overlay,
            zoom: 2,
            atlasPan: CGPoint(x: 5, y: 5),
            livePan: .zero
        )
        XCTAssertEqual(try XCTUnwrap(selected), details)
        XCTAssertNil(LayerSceneFeatureReviewGeometry.groundFeature(
            at: CGPoint(x: 100, y: 140),
            in: overlay,
            zoom: 2,
            atlasPan: CGPoint(x: 5, y: 5),
            livePan: .zero
        ))
    }
    func testInvisibleLiveGroundFeatureUsesLivePanForHitTesting() throws {
        let details = LayerSceneGroundFeatureDetails(
            segmentID: 7,
            sequenceIndex: 3,
            worldPosition: CGPoint(x: 400, y: 500),
            referencePixels: [UInt8](
                repeating: 90,
                count: GroundHypothesisTracker.featurePixelCount
            )
        )
        let overlay = LayerSceneFeatureReviewOverlay(
            markers: [.init(
                worldPosition: CGPoint(x: 20, y: 30),
                kind: .groundFeature,
                worldSize: CGSize(width: 16, height: 12),
                groundFeatureDetails: details,
                coordinateSpace: .live,
                isVisible: false
            )],
            lines: []
        )

        XCTAssertTrue(LayerSceneFeatureReviewGeometry.markerQuads(
            overlay, zoom: 2, pan: CGPoint(x: 100, y: 200)
        ).isEmpty)
        XCTAssertEqual(try XCTUnwrap(LayerSceneFeatureReviewGeometry.groundFeature(
            at: CGPoint(x: 140, y: 260),
            in: overlay,
            zoom: 2,
            atlasPan: CGPoint(x: -500, y: -500),
            livePan: CGPoint(x: 100, y: 200)
        )), details)
    }
    func testFeatureOverlayFiltersNonFiniteAndCapsDrawWork() {
        let finiteMarkers = (0..<(LayerSceneFeatureReviewGeometry.maximumMarkers + 3)).map {
            LayerSceneFeatureReviewOverlay.Marker(worldPosition: CGPoint(x: CGFloat($0), y: 1), kind: .groundFeature)
        }
        let markers = [.init(worldPosition: CGPoint(x: CGFloat.infinity, y: 1), kind: .groundFeature)] + finiteMarkers
        let finiteLines = (0..<(LayerSceneFeatureReviewGeometry.maximumLines + 3)).map {
            LayerSceneFeatureReviewOverlay.Line(start: CGPoint(x: CGFloat($0), y: 0), end: CGPoint(x: CGFloat($0), y: 4), kind: .persistentGround)
        }
        let lines = [.init(start: CGPoint(x: CGFloat.nan, y: 0), end: CGPoint(x: 1, y: 1), kind: .persistentGround)] + finiteLines
        let overlay = LayerSceneFeatureReviewOverlay(markers: markers, lines: lines)
        XCTAssertEqual(LayerSceneFeatureReviewGeometry.markerQuads(overlay, zoom: 1, pan: .zero).count, LayerSceneFeatureReviewGeometry.maximumMarkers * 4)
        XCTAssertEqual(LayerSceneFeatureReviewGeometry.lineQuads(overlay, zoom: 1, pan: .zero).count, LayerSceneFeatureReviewGeometry.maximumLines)
        XCTAssertTrue(LayerSceneFeatureReviewGeometry.markerQuads(nil, zoom: 1, pan: .zero).isEmpty)
    }
}
