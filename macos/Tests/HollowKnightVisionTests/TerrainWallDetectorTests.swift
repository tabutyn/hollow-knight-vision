import XCTest
@testable import HollowKnightVision

final class TerrainWallDetectorTests: XCTestCase {
    func testGameplayOverlayHidesDetectedWallsButKeepsFloorAndCorners() {
        let wall = DetectedWall(x: 20, top: 5, bottom: 30, solidOnRight: true)
        let corner = DetectedTerrainCorner(wallIndex: 0, floorIndex: 0, x: 20, y: 30, floorEndpoint: 24)
        let terrain = TerrainWallDetection(walls: [wall], corners: [corner],
            supportedFloor: CleanFloorLine(row: 30, xRange: 24...50))
        let overlay = terrain.overlay { $0 }
        XCTAssertEqual(terrain.walls, [wall])
        XCTAssertEqual(overlay.lines.count, 2)
        XCTAssertTrue(overlay.lines.allSatisfy { $0.start.y == $0.end.y })
        XCTAssertEqual(overlay.lines.map(\.kind), [.currentGround, .detectedTerrain])
        XCTAssertEqual(overlay.markers.map(\.kind), [.detectedTerrainCorner])
    }
    func testPlatformFaceAndMaskedPixels() {
        let width = 160, height = 90
        var luma = [UInt8](repeating: 90, count: width * height)
        for y in 30..<65 {
            for x in 40..<110 { luma[y * width + x] = 8 }
        }
        var analysis = GroundEdgeAnalysis(width: width, height: height, groundEdgePixels: [],
            sourceLuma: luma, validSourcePixels: Array(repeating: 1, count: luma.count))
        let floors = [CleanFloorLine(row: 30, xRange: 41...109)]
        let result = TerrainWallDetector.detect(analysis, floors: floors)
        XCTAssertTrue(result.corners.contains { abs($0.x - 40) <= 4 && $0.y == 30 })
        XCTAssertTrue(result.corners.contains { abs($0.x - 110) <= 4 && $0.y == 30 })
        analysis.validSourcePixels = Array(repeating: 0, count: luma.count)
        XCTAssertTrue(TerrainWallDetector.detect(analysis, floors: floors).walls.isEmpty)
    }

    func testCornerExtrapolationRespectsEndpointDistanceOcclusionAndSide() {
        let wall = DetectedWall(x: 32, top: 39, bottom: 75, solidOnRight: true)
        let top = CleanFloorLine(row: 35, xRange: 36...82)
        let bottom = CleanFloorLine(row: 78, xRange: 8...30)
        let crosses = CleanFloorLine(row: 51, xRange: 20...60)
        let distant = CleanFloorLine(row: 25, xRange: 32...72)
        let joined = TerrainWallDetector.join(walls: [wall], floors: [top, bottom, crosses, distant],
                                               tolerance: 8) { _,_ in true }
        XCTAssertEqual(joined.corners.map(\.floorIndex).sorted(), [0,1])
        XCTAssertEqual(joined.walls.first?.top, 35)
        XCTAssertEqual(joined.walls.first?.bottom, 78)
        XCTAssertTrue(TerrainWallDetector.join(walls: [wall], floors: [top], tolerance: 8) { x,_ in x != 34 }.corners.isEmpty)
    }

    func testContactDuringCameraFollowAndBlockedMovement() {
        var t = KnightTerrainContactTracker()
        func observe(_ i: Int, x: Int, cameraX: Int, direction: Int, room: Int = 1, y: Int = 130) -> KnightTerrainSupport {
            t.observe(foot: CGPoint(x: x, y: y), camera: CGPoint(x: cameraX, y: 0), direction: direction,
                time: Double(i) * 0.04, room: room, generation: 1, width: 640, height: 360, walls: [])
        }
        for i in 0...7 {
            let value = observe(i, x: 200, cameraX: i * 3, direction: 1)
            XCTAssertNil(value.wall)
            if i == 7 { XCTAssertNotNil(value.floor) }
        }
        for i in 8...14 { _ = observe(i, x: 200, cameraX: 21, direction: 1) }
        XCTAssertEqual(observe(15, x: 200, cameraX: 21, direction: 1).wall?.x, 203)
        XCTAssertNil(observe(16, x: 200, cameraX: 21, direction: 0).wall)
        XCTAssertNil(observe(17, x: 200, cameraX: 21, direction: -1, room: 2).floor)
        for i in 18..<32 {
            let v = observe(i, x: 200, cameraX: 21, direction: 0, room: 2, y: 100 + (i - 23) * (i - 23))
            XCTAssertNil(v.floor)
            XCTAssertNil(v.wall)
        }
    }

    func testWalkedDarkGroundPersistsOnlyWithContinuousSafeEvidence() throws {
        func walk(jumpAt: Int? = nil, changingCameraY: Bool = false,
                  partialAt: Int? = nil, direction: Int = 1) -> KnightTerrainSupport {
            var tracker = KnightTerrainContactTracker()
            var result = KnightTerrainSupport()
            for i in 0...8 {
                let cameraY = changingCameraY ? CGFloat(i) : 0
                result = tracker.observe(foot: CGPoint(x: 200, y: 130 + cameraY),
                    camera: CGPoint(x: i * 3, y: Int(cameraY)), direction: direction,
                    time: Double(i) * 0.04, room: 1, generation: 1, width: 640, height: 360,
                    walls: [], partial: partialAt == i, jumpHeld: jumpAt == i)
            }
            return result
        }
        let support = walk()
        let analysis = GroundEdgeAnalysis(width: 640, height: 360, groundEdgePixels: [],
            sourceLuma: Array(repeating: 0, count: 640 * 360),
            validSourcePixels: Array(repeating: 1, count: 640 * 360))
        var terrain = TerrainWallDetection()
        terrain.apply(support, floors: [], analysis: analysis)
        let geometry = try XCTUnwrap(LiveWorldGroundGeometry.captured(reviews: [], detected: [],
            frameSize: CGSize(width: 640, height: 360), solveWidth: 640,
            measured: true, supportedFloor: terrain.supportedFloor))
        XCTAssertTrue(geometry.isValid(solveWidth: 640, sourceWidth: 640, sourceHeight: 360))
        XCTAssertEqual(try JSONDecoder().decode(LiveWorldGroundGeometry.self,
            from: JSONEncoder().encode(geometry)), geometry)
        XCTAssertNil(walk(jumpAt: 4).floor)
        XCTAssertNil(walk(changingCameraY: true).floor)
        XCTAssertNotNil(walk(partialAt: 4).floor, "Full measurements on both sides bracket the obscured frame")
        XCTAssertNil(walk(direction: 0).floor)
    }

    func testBriefLostMaskRequiresMatchingWalkOnBothSidesOfGap() {
        func resume(missing: Int = 1, jump: Bool = false, displacement: CGFloat = 0) -> CleanFloorLine? {
            var tracker = KnightTerrainContactTracker()
            func sample(_ i: Int, visible: Bool, jump: Bool = false, offset: CGFloat = 0) -> CleanFloorLine? {
                tracker.observe(foot: visible ? CGPoint(x: CGFloat(100 + 2*i) + offset, y: 130) : nil,
                    camera: .zero, direction: 1, time: 0.04 * Double(i), room: 0, generation: 0,
                    width: 640, height: 360, walls: [], jumpHeld: jump).floor
            }
            for i in 0...8 { _ = sample(i, visible: true) }
            for i in 9..<(9 + missing) { XCTAssertNil(sample(i, visible: false, jump: jump)) }
            return sample(9 + missing, visible: true, offset: displacement)
        }
        XCTAssertNotNil(resume())
        XCTAssertNotNil(resume(missing: 4))
        XCTAssertNil(resume(missing: 40))
        XCTAssertNil(resume(jump: true))
        XCTAssertNil(resume(displacement: 80))
    }

    func testWalkingBehindOccluderRequiresLevelReappearanceAndHeldTravel() {
        func cross(_ drive: Int, jump: Bool = false, changedHeight: CGFloat = 0,
                   cameraExcursion: CGFloat = 0) -> CleanFloorLine? {
            var tracker = KnightTerrainContactTracker()
            var last: CleanFloorLine?
            for i in 0...35 {
                let hidden = (10...30).contains(i)
                let camera = CGPoint(x: CGFloat(i * 3), y: i == 21 ? cameraExcursion : 0)
                let foot = hidden ? nil : CGPoint(x: 200, y: 130 + (i > 30 ? changedHeight : 0))
                last = tracker.observe(foot: foot, camera: camera, direction: hidden ? drive : 1,
                    time: Double(i) / 40, room: 0, generation: 1, width: 640, height: 360, walls: [],
                    jumpHeld: jump && i == 22).floor
                if hidden { XCTAssertNil(last) }
            }
            return last
        }
        XCTAssertEqual(cross(1)?.xRange, 91...204)
        XCTAssertNil(cross(0))
        XCTAssertNil(cross(1, jump: true))
        XCTAssertNil(cross(1, changedHeight: 15))
        XCTAssertNil(cross(1, cameraExcursion: 8))
    }

    func testLateAtlasSampleRetainsTheConfirmedWalkStillOnScreen() throws {
        var tracker = KnightTerrainContactTracker()
        var floor: CleanFloorLine?
        for i in 0...40 {
            floor = tracker.observe(foot: .init(x: 250, y: 130), camera: .init(x: i * 3, y: 0),
                direction: 1, time: Double(i) * 0.04, room: 0, generation: 1,
                width: 640, height: 360, walls: []).floor
        }
        let span = try XCTUnwrap(floor?.xRange)
        XCTAssertLessThanOrEqual(span.lowerBound, 130)
        XCTAssertGreaterThanOrEqual(span.upperBound, 250)
    }

    func testShortUnmeasuredCameraGapRequiresTheSameMeasuredIslandToResume() {
        func resume(on island: Int, gap: Int) -> CleanFloorLine? {
            var tracker = KnightTerrainContactTracker()
            func tick(_ i: Int, measured: Bool, owner: Int) -> CleanFloorLine? {
                tracker.observe(foot: .init(x: 200, y: 130), camera: measured ? .init(x: i * 2, y: 0) : nil,
                    direction: 1, time: Double(i)/40, room: 1, generation: 1, width: 640, height: 360,
                    walls: [], measuredIsland: owner).floor
            }
            for i in 0..<20 { _ = tick(i, measured: true, owner: 1) }
            for i in 20..<(20 + gap) { XCTAssertNil(tick(i, measured: false, owner: 2)) }
            return tick(20 + gap, measured: true, owner: island)
        }
        XCTAssertNotNil(resume(on: 1, gap: 3))
        XCTAssertNil(resume(on: 2, gap: 3))
        XCTAssertNil(resume(on: 1, gap: 12))
    }

    func testWalkingBackAcrossDarkGroundRetainsContinuousOwnedSpanAfterKnightLoss() {
        var tracker = KnightTerrainContactTracker()
        var owned = [LiveWorldGroundLine]()
        for i in 0...50 {
            let x = i <= 20 ? 200 + i * 3 : 260 - (i - 20) * 3
            let direction = i <= 20 ? 1 : -1
            let support = tracker.observe(foot: CGPoint(x: x, y: 130), camera: .zero,
                direction: direction, time: Double(i) * 0.04, room: 1, generation: 1,
                width: 640, height: 360, walls: [])
            if let floor = support.floor {
                owned.append(.init(observationID: i, islandID: 1, roomID: 1, segmentID: Int.max,
                    start: CGPoint(x: floor.xRange.lowerBound, y: 360 - floor.row),
                    end: CGPoint(x: floor.xRange.upperBound + 1, y: 360 - floor.row), placement: .reference))
            }
        }
        XCTAssertNil(tracker.observe(foot: nil, camera: .zero, direction: -1, time: 2.04,
            room: 1, generation: 1, width: 640, height: 360, walls: []).floor)
        let retained = LiveGroundOverlayGeometry.make(owned, scale: 1, anchor: .zero)
        XCTAssertEqual(retained.count, 1)
        XCTAssertLessThanOrEqual(retained[0].atlasStart.x, 174)
        XCTAssertGreaterThanOrEqual(retained[0].atlasEnd.x, 260)
        let overlay = LiveFeatureReviewOverlay.make(groundHypotheses: nil,
            persistentGroundLines: retained, liveBounds: CGRect(x: 0, y: 0, width: 640, height: 360))
        XCTAssertEqual(overlay.lines.count, 1)
        XCTAssertEqual(overlay.lines[0].start, retained[0].atlasStart)
        XCTAssertEqual(overlay.lines[0].end, retained[0].atlasEnd)
    }

    func testWalkingAcrossDetectedEdgeSavesOnlyWalkedSpanAtDetectedElevation() throws {
        let analysis = GroundEdgeAnalysis(width: 640, height: 360, groundEdgePixels: [])
        let detected = CleanFloorLine(row: 131, xRange: 0...360)
        let contact = KnightTerrainSupport(floor: .init(row: 130, xRange: 100...132))
        var terrain = TerrainWallDetection()
        terrain.apply(contact, floors: [detected], analysis: analysis)
        XCTAssertNil(terrain.supportedFloor, "The current edge already covers the footprint")
        let geometry = try XCTUnwrap(LiveWorldGroundGeometry.captured(reviews: [], detected: [detected],
            frameSize: .init(width: 640, height: 360), solveWidth: 640, measured: true,
            supportedFloor: terrain.walkedFloor))
        XCTAssertEqual(geometry.lines.map(\.start), [.init(x: 100, y: 229)])
        XCTAssertEqual(geometry.lines.map(\.end), [.init(x: 133, y: 229)])
    }

    func testTabCycleUsesCurrentScaleAndReturnsFromFit() {
        XCTAssertEqual(LayerSceneFramingMode.freeFly.nextTabPreset, .currentNear)
        let near = LayerSceneFramingMode.current.nextTabPreset
        XCTAssertEqual(near.initialCurrentZoom, 1 / 1.1, accuracy: 0.0001)
        XCTAssertEqual(near.nextTabPreset.initialCurrentZoom, 0.5)
        XCTAssertEqual(near.nextTabPreset.nextTabPreset, .fit)
        XCTAssertEqual(LayerSceneFramingMode.fit.nextTabPreset, near)
        let c = LayerSceneViewport.Coordinator()
        let size = CGSize(width: 1280, height: 720)
        c.followBounds = CGRect(x: 100, y: 10, width: 640, height: 360)
        c.focusPoint = CGPoint(x: 420, y: 190)
        c.fitBounds = CGRect(x: -80, y: 0, width: 1800, height: 720)
        for (request, mode) in [near, near.nextTabPreset, .fit, near].enumerated() {
            c.setFraming(mode, request: request, in: size, at: Double(request))
            c.advanceViewportTransition(at: Double(request) + LayerSceneViewportTransition.duration + 0.001)
            if mode == .fit {
                XCTAssertEqual(c.zoom, 1280.0 / 1800 * 0.9, accuracy: 0.0001)
            } else {
                XCTAssertEqual(c.zoom, 1.76 * mode.initialCurrentZoom, accuracy: 0.0001)
                XCTAssertEqual(c.focusPoint!.x * c.zoom + c.pan.x, size.width / 2, accuracy: 0.001)
                XCTAssertEqual(c.focusPoint!.y * c.zoom + c.pan.y, size.height / 2, accuracy: 0.001)
            }
        }
    }
}
