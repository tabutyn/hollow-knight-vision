import CoreGraphics
import CoreImage
import ImageIO
import XCTest
@testable import HollowKnightVision

final class HackerAtlasTests: XCTestCase {
    private struct HUDFixture: Decodable {
        let width: Int
        let height: Int
        let hudPNG: Data
    }

    func testSynchronizerRequiresMatchingUnityFrameInEitherArrivalOrder() throws {
        let synchronizer = HackerFrameSynchronizer(capacity: 4)
        let image = try solidImage(width: 64, height: 36)
        let firstSample = sample(frame: 99, cameraX: 0)

        XCTAssertNil(synchronizer.append(sample: firstSample, observedAt: 1))
        let first = try XCTUnwrap(synchronizer.append(capture: HackerCapturedFrame(
            unityFrame: 99, image: image, capturedAt: 1.1, integratesAtlas: false
        )))
        XCTAssertEqual(first.sample.unityFrame, 99)
        XCTAssertEqual(first.capture.unityFrame, 99)

        XCTAssertNil(synchronizer.append(capture: HackerCapturedFrame(
            unityFrame: 100, image: image, capturedAt: 2, integratesAtlas: true
        )))
        XCTAssertNil(synchronizer.append(sample: sample(frame: 101, cameraX: 1), observedAt: 2.1))
        let second = try XCTUnwrap(synchronizer.append(
            sample: sample(frame: 100, cameraX: 1), observedAt: 2.2
        ))
        XCTAssertEqual(second.capture.unityFrame, 100)
        XCTAssertEqual(second.sample.unityFrame, 100)
    }

    func testDirectCameraPlacementUsesIndependentXYProjectionScale() {
        let transform = GroundTruthCameraTransform(
            sceneName: "Room",
            cameraWorld: CGPoint(x: 12, y: -3),
            pixelsPerWorldUnitX: 4,
            pixelsPerWorldUnitY: 6,
            frameSize: CGSize(width: 640, height: 360)
        )
        XCTAssertEqual(
            HackerAtlasCameraPlacement.cameraPosition(transform),
            CGPoint(x: 48, y: -18)
        )
    }

    func testRoomLayoutKeepsTreeDoorwaysRectilinearWithAGap() {
        let a = room(name: "A", position: .zero, size: CGSize(width: 100, height: 80))
        let b = room(name: "B", position: .zero, size: CGSize(width: 120, height: 60))
        let c = room(name: "C", position: .zero, size: CGSize(width: 90, height: 100))
        let ab = connection(
            from: a, to: b, fromSide: .right,
            fromCoordinate: 40, toCoordinate: 30
        )
        let bc = connection(
            from: b, to: c, fromSide: .right,
            fromCoordinate: 30, toCoordinate: 50
        )

        let layout = HackerRoomLayout.make(rooms: [a, b, c], connections: [ab, bc])

        XCTAssertEqual(layout.positions[a.id], .zero)
        XCTAssertEqual(layout.positions[b.id], CGPoint(x: 124, y: 10))
        XCTAssertEqual(layout.positions[c.id], CGPoint(x: 268, y: -10))
        XCTAssertTrue(layout.freeformConnectionIDs.isEmpty)
    }

    func testRoomLayoutLeavesContradictoryLoopAsFreeformConnection() throws {
        let a = room(name: "A", position: .zero, size: CGSize(width: 100, height: 100))
        let b = room(name: "B", position: .zero, size: CGSize(width: 100, height: 100))
        let c = room(name: "C", position: .zero, size: CGSize(width: 100, height: 100))
        let ab = connection(from: a, to: b, fromSide: .right)
        let bc = connection(from: b, to: c, fromSide: .top)
        let impossibleClosure = connection(
            from: c, to: a, fromSide: .left, toSide: .right
        )

        let layout = HackerRoomLayout.make(
            rooms: [a, b, c],
            connections: [ab, bc, impossibleClosure]
        )

        XCTAssertFalse(layout.freeformConnectionIDs.contains(ab.id))
        XCTAssertFalse(layout.freeformConnectionIDs.contains(bc.id))
        XCTAssertTrue(layout.freeformConnectionIDs.contains(impossibleClosure.id))
        XCTAssertEqual(layout.positions[b.id], CGPoint(x: 124, y: 0))
        XCTAssertEqual(layout.positions[c.id], CGPoint(x: 124, y: 124))
    }

    func testRoomLayoutFansOverlappingBranchIntoNewRectifiedIsland() throws {
        let a = room(name: "A", position: .zero, size: CGSize(width: 100, height: 100))
        let b = room(name: "B", position: .zero, size: CGSize(width: 100, height: 100))
        let c = room(name: "C", position: .zero, size: CGSize(width: 100, height: 100))
        let ab = connection(from: a, to: b, fromSide: .right)
        let ac = connection(from: a, to: c, fromSide: .right)

        let layout = HackerRoomLayout.make(rooms: [a, b, c], connections: [ab, ac])
        let bPosition = try XCTUnwrap(layout.positions[b.id])
        let cPosition = try XCTUnwrap(layout.positions[c.id])

        XCTAssertEqual(bPosition, CGPoint(x: 124, y: 0))
        XCTAssertEqual(cPosition.x, 124)
        XCTAssertNotEqual(cPosition.y, 0)
        XCTAssertTrue(layout.freeformConnectionIDs.contains(ac.id))
        XCTAssertFalse(
            b.bounds.offsetBy(dx: bPosition.x, dy: bPosition.y).intersects(
                c.bounds.offsetBy(dx: cPosition.x, dy: cPosition.y)
            )
        )
    }

    func testPipelineConnectsRoomsAndRestoresOriginalSceneAnchor() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let image = try solidImage(width: 64, height: 36)

        let firstPrepared = try XCTUnwrap(preparer.process(match(
            image: image, sample: sample(frame: 1, cameraX: 10, scene: "A")
        )))
        XCTAssertNotNil(pipeline.process(firstPrepared))
        XCTAssertEqual(firstPrepared.liveBounds.origin, .zero)

        let movedPrepared = try XCTUnwrap(preparer.process(match(
            image: image, sample: sample(frame: 2, cameraX: 18, scene: "A")
        )))
        let moved = try XCTUnwrap(pipeline.process(movedPrepared))
        XCTAssertEqual(movedPrepared.liveBounds.minX, 8, accuracy: 0.001)
        XCTAssertEqual(movedPrepared.liveBounds.minY, 0, accuracy: 0.001)
        XCTAssertGreaterThan(try XCTUnwrap(moved.atlasBounds).width, 64)

        let newScenePrepared = try XCTUnwrap(preparer.process(match(
            image: image, sample: sample(frame: 3, cameraX: 50, scene: "B")
        )))
        let newScene = try XCTUnwrap(pipeline.process(newScenePrepared))
        XCTAssertEqual(newScenePrepared.liveBounds.minX, 66, accuracy: 0.001)
        XCTAssertEqual(newScenePrepared.liveBounds.minY, 0, accuracy: 0.001)
        XCTAssertEqual(newScene.atlasBounds, CGRect(x: 0, y: 0, width: 130, height: 36))
        XCTAssertEqual(newScene.roomCount, 2)
        XCTAssertEqual(newScene.knownScenes, ["A", "B"])
        XCTAssertEqual(Set(newScene.atlasTiles.map(\.id)).count, newScene.atlasTiles.count)
        XCTAssertEqual(newScene.rooms.map(\.sceneName), ["A", "B"])
        XCTAssertEqual(newScene.rooms[0].position, .zero)
        XCTAssertEqual(newScene.rooms[0].bounds, CGRect(x: 0, y: 0, width: 72, height: 36))
        XCTAssertEqual(newScene.rooms[1].position, CGPoint(x: 66, y: 0))
        XCTAssertEqual(newScene.rooms[1].bounds, CGRect(x: 0, y: 0, width: 64, height: 36))
        XCTAssertEqual(newScene.connections.count, 1)
        XCTAssertEqual(newScene.connections[0].fromSide, .right)
        XCTAssertEqual(newScene.connections[0].toSide, .left)

        let revisitedPrepared = try XCTUnwrap(preparer.process(match(
            image: image, sample: sample(frame: 4, cameraX: 18, scene: "A")
        )))
        let revisited = try XCTUnwrap(pipeline.process(revisitedPrepared))
        XCTAssertEqual(revisitedPrepared.liveBounds.minX, 8, accuracy: 0.001)
        XCTAssertEqual(try XCTUnwrap(revisited.atlasBounds).width, 130, accuracy: 0.001)
        XCTAssertEqual(revisited.roomCount, 2)
        XCTAssertEqual(revisited.connections.count, 1)
    }

    func testNewRoomAlignsKnightsDoorwayPositionAcrossSceneCoordinates() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let image = try solidImage(width: 64, height: 36)

        let leaving = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(
                frame: 1, cameraX: 10, scene: "A",
                heroScreenX: 60, heroScreenY: 18
            )
        )))
        XCTAssertTrue(leaving.roomPlacementReady)
        XCTAssertNotNil(pipeline.process(leaving))
        let cameraSnap = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(
                frame: 2, cameraX: -150, scene: "B",
                heroScreenX: -100, heroScreenY: 18
            )
        )))
        XCTAssertFalse(cameraSnap.roomPlacementReady)
        XCTAssertNil(pipeline.process(cameraSnap))

        let entering = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(
                frame: 3, cameraX: -100, scene: "B",
                heroScreenX: 4, heroScreenY: 18
            )
        )))

        XCTAssertEqual(leaving.liveBounds.origin, .zero)
        XCTAssertTrue(entering.roomPlacementReady)
        XCTAssertEqual(entering.liveBounds.minX, 56, accuracy: 0.001)
        XCTAssertEqual(entering.liveBounds.minY, 0, accuracy: 0.001)
        XCTAssertEqual(try XCTUnwrap(pipeline.process(entering)).roomCount, 2)
    }

    func testPipelineReinsertsLastBrightFrameWhenRoomFadesToBlack() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let bright = try solidImage(
            width: 64, height: 36, red: 0.6, green: 0.7, blue: 0.8
        )
        let black = try solidImage(
            width: 64, height: 36, red: 0, green: 0, blue: 0
        )
        let preFade = try XCTUnwrap(preparer.process(match(
            image: bright,
            sample: sample(frame: 10, cameraX: 10),
            integratesAtlas: false
        )))
        XCTAssertNil(pipeline.process(preFade))

        let faded = try XCTUnwrap(preparer.process(match(
            image: black,
            sample: sample(frame: 11, cameraX: 10),
            integratesAtlas: true
        )))
        let output = try XCTUnwrap(pipeline.process(faded))
        XCTAssertEqual(output.unityFrame, 10)
        XCTAssertEqual(output.roomCount, 1)
        let tile = try XCTUnwrap(output.atlasTiles.first)
        let pixels = try rgbaPixels(tile.image)
        let center = ((tile.image.height - 1 - 18) * tile.image.width + 32) * 4
        XCTAssertGreaterThan(pixels[center], 100)
        XCTAssertGreaterThan(pixels[center + 1], 100)
        XCTAssertGreaterThan(pixels[center + 2], 100)
    }

    func testNewestHackerFrameOverwritesOverlapAndAddsDarkRoomPixels() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let bright = try solidImage(
            width: 64, height: 36, red: 0.6, green: 0.7, blue: 0.8
        )
        let first = try XCTUnwrap(preparer.process(match(
            image: bright,
            sample: sample(frame: 30, cameraX: 0)
        )))
        XCTAssertNotNil(pipeline.process(first))

        let iris = try irisImage(width: 64, height: 36, radius: 8)
        let narrowed = try XCTUnwrap(preparer.process(match(
            image: iris,
            sample: sample(frame: 31, cameraX: 30)
        )))
        let output = try XCTUnwrap(pipeline.process(narrowed))

        let overwritten = try atlasPixel(
            at: CGPoint(x: 35, y: 18),
            tiles: output.atlasTiles
        )
        XCTAssertEqual(overwritten.0, 0)
        XCTAssertEqual(overwritten.1, 0)
        XCTAssertEqual(overwritten.2, 0)
        XCTAssertEqual(overwritten.3, 255)
        let newlyObservedBlack = try atlasPixel(
            at: CGPoint(x: 80, y: 18),
            tiles: output.atlasTiles
        )
        XCTAssertEqual(newlyObservedBlack.0, 0)
        XCTAssertEqual(newlyObservedBlack.1, 0)
        XCTAssertEqual(newlyObservedBlack.2, 0)
        XCTAssertEqual(newlyObservedBlack.3, 255)
    }

    func testHackerAtlasUsesPixelStencilToRemoveHUD() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let image = try gameplayFrameWithRealHUD()
        let prepared = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(
                frame: 12, cameraX: 0,
                heroScreenX: 32, heroScreenY: 18
            )
        )))
        let stencil = try XCTUnwrap(prepared.hudStencil)
        XCTAssertFalse(stencil.omittedRects.isEmpty)
        let output = try XCTUnwrap(pipeline.process(prepared))

        XCTAssertEqual(
            try atlasAlpha(at: CGPoint(x: 25, y: 320), tiles: output.atlasTiles),
            0,
            "the soul-vessel ornament lies outside the matched sprite rectangles"
        )
        XCTAssertEqual(
            try atlasAlpha(at: CGPoint(x: 220, y: 330), tiles: output.atlasTiles),
            0,
            "the atlas mask must cover the maximum health-row footprint"
        )
        for rect in stencil.omittedRects {
            XCTAssertEqual(
                try atlasAlpha(at: CGPoint(x: rect.midX, y: rect.midY), tiles: output.atlasTiles),
                0
            )
        }
        XCTAssertEqual(
            try atlasAlpha(at: CGPoint(x: 500, y: 100), tiles: output.atlasTiles),
            255
        )
    }

    func testCutsceneWithoutHeroDoesNotBecomeARoom() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let image = try solidImage(width: 64, height: 36)
        let first = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(frame: 20, cameraX: 0, scene: "Room A")
        )))
        XCTAssertEqual(try XCTUnwrap(pipeline.process(first)).roomCount, 1)

        let cutscene = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(
                frame: 21, cameraX: 0, scene: "Opening Cutscene",
                heroAvailable: false
            )
        )))
        XCTAssertNil(pipeline.process(cutscene))

        let second = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(frame: 22, cameraX: 0, scene: "Room B")
        )))
        let output = try XCTUnwrap(pipeline.process(second))
        XCTAssertEqual(output.roomCount, 2)
        XCTAssertEqual(output.knownScenes, ["Room A", "Room B"])
    }

    func testPipelineClearsRenderedMarkerAtCapturedTopEdge() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let image = try markerBandImage()
        let prepared = try XCTUnwrap(preparer.process(match(
            image: image, sample: sample(frame: 4, cameraX: 0)
        )))
        let output = try XCTUnwrap(pipeline.process(prepared))
        let pixels = try rgbaPixels(prepared.liveImage)
        XCTAssertEqual(pixels[3], 255, "captured top-left marker pixel must be repaired")
        XCTAssertEqual(Array(pixels[0..<3]), [0, 255, 0])
        XCTAssertEqual(pixels[((359 * 640) + 0) * 4 + 3], 255)
        XCTAssertEqual(
            try atlasAlpha(at: CGPoint(x: 0, y: 359), tiles: output.atlasTiles),
            0,
            "the repaired marker lies inside the persistent HUD atlas shield"
        )
        XCTAssertEqual(
            try atlasAlpha(at: CGPoint(x: 200, y: 200), tiles: output.atlasTiles),
            255
        )
    }

    func testEveryMatchedFramePreparesWhileOnlyCadencedFramesIntegrate() throws {
        let preparer = HackerFramePreparer(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [
            .useSoftwareRenderer: true,
        ]))
        let image = try solidImage(width: 64, height: 36)
        let liveOnly = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(frame: 5, cameraX: 0),
            integratesAtlas: false
        )))

        XCTAssertFalse(liveOnly.integratesAtlas)
        XCTAssertEqual(liveOnly.unityFrame, 5)
        XCTAssertNil(pipeline.process(liveOnly))

        let atlasFrame = try XCTUnwrap(preparer.process(match(
            image: image,
            sample: sample(frame: 6, cameraX: 1),
            integratesAtlas: true
        )))
        XCTAssertNotNil(pipeline.process(atlasFrame))
    }

    private func match(
        image: CGImage,
        sample: ReceiverGroundTruthSample,
        integratesAtlas: Bool = true
    ) -> HackerMatchedFrame {
        HackerMatchedFrame(
            capture: HackerCapturedFrame(
                unityFrame: HackerFrameSynchronizer.key(for: sample.unityFrame),
                image: image,
                capturedAt: sample.unityRealtime,
                integratesAtlas: integratesAtlas
            ),
            sample: sample,
            sampleObservedAt: sample.unityRealtime
        )
    }

    private func room(
        name: String,
        position: CGPoint,
        size: CGSize
    ) -> HackerAtlasRoom {
        HackerAtlasRoom(
            id: UUID(),
            sceneName: name,
            position: position,
            bounds: CGRect(origin: .zero, size: size),
            atlasTiles: []
        )
    }

    private func connection(
        from: HackerAtlasRoom,
        to: HackerAtlasRoom,
        fromSide: HackerRoomSide,
        toSide: HackerRoomSide? = nil,
        fromCoordinate: CGFloat = 50,
        toCoordinate: CGFloat = 50
    ) -> HackerRoomConnection {
        HackerRoomConnection(
            id: UUID(),
            fromRoomID: from.id,
            toRoomID: to.id,
            fromSide: fromSide,
            toSide: toSide ?? fromSide.opposite,
            fromCoordinate: fromCoordinate,
            toCoordinate: toCoordinate
        )
    }

    private func sample(
        frame: Int64,
        cameraX: Double,
        scene: String = "A",
        heroAvailable: Bool = true,
        heroScreenX: Double? = nil,
        heroScreenY: Double? = nil
    ) -> ReceiverGroundTruthSample {
        ReceiverGroundTruthSample(
            version: 2, type: "groundTruth", sessionID: UUID(), sequence: UInt64(frame),
            unityFrame: frame, unityRealtime: Double(frame), sceneName: scene,
            heroAvailable: heroAvailable, heroX: 0, heroY: 0, heroZ: 0,
            velocityX: 0, velocityY: 0, facingRight: true, grounded: true,
            cameraAvailable: true, cameraX: cameraX, cameraY: 4, cameraZ: -38,
            cameraTargetX: cameraX, cameraTargetY: 4, cameraTargetZ: 0,
            orthographicSize: 18,
            pixelsPerWorldUnitX: 1, pixelsPerWorldUnitY: 1,
            heroScreenX: heroScreenX, heroScreenY: heroScreenY,
            projectionPixelWidth: 64, projectionPixelHeight: 36,
            screenWidth: 64, screenHeight: 36
        )
    }

    private func solidImage(
        width: Int,
        height: Int,
        red: CGFloat = 0.2,
        green: CGFloat = 0.4,
        blue: CGFloat = 0.6
    ) throws -> CGImage {
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: red, green: green, blue: blue, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return try XCTUnwrap(context.makeImage())
    }

    private func gameplayFrameWithRealHUD() throws -> CGImage {
        let fixtureURL = try XCTUnwrap(Bundle.module.url(
            forResource: "hud-stencil-cases",
            withExtension: "json"
        ))
        let fixtures = try JSONDecoder().decode(
            [HUDFixture].self,
            from: Data(contentsOf: fixtureURL)
        )
        let fixture = try XCTUnwrap(fixtures.first)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(
            fixture.hudPNG as CFData,
            nil
        ))
        let hud = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: fixture.width,
            height: fixture.height,
            bitsPerComponent: 8,
            bytesPerRow: fixture.width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: 0.15, green: 0.2, blue: 0.25, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: fixture.width, height: fixture.height))
        context.draw(
            hud,
            in: CGRect(
                x: 0,
                y: fixture.height - hud.height,
                width: hud.width,
                height: hud.height
            )
        )
        return try XCTUnwrap(context.makeImage())
    }

    private func irisImage(width: Int, height: Int, radius: CGFloat) throws -> CGImage {
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(gray: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.setFillColor(CGColor(gray: 0.8, alpha: 1))
        context.fillEllipse(in: CGRect(
            x: CGFloat(width) * 0.5 - radius,
            y: CGFloat(height) * 0.5 - radius,
            width: radius * 2,
            height: radius * 2
        ))
        return try XCTUnwrap(context.makeImage())
    }

    private func markerBandImage() throws -> CGImage {
        let width = 640, height = 360
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height { for x in 0..<width {
            let offset = (y * width + x) * 4
            if y < 6 && x < 120 {
                pixels[offset] = 255
            } else {
                pixels[offset + 1] = 255
            }
            pixels[offset + 3] = 255
        } }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        return try XCTUnwrap(CGImage(
            width: width, height: height,
            bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false,
            intent: .defaultIntent
        ))
    }

    private func rgbaPixels(_ image: CGImage) throws -> [UInt8] {
        var pixels = [UInt8](repeating: 0, count: image.width * image.height * 4)
        let drew = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(
                data: bytes.baseAddress,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: image.width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
            return true
        }
        XCTAssertTrue(drew)
        return pixels
    }

    private func atlasAlpha(
        at point: CGPoint,
        tiles: [LiveAtlasOutput.AtlasLayer]
    ) throws -> UInt8 {
        try atlasPixel(at: point, tiles: tiles).3
    }

    private func atlasPixel(
        at point: CGPoint,
        tiles: [LiveAtlasOutput.AtlasLayer]
    ) throws -> (UInt8, UInt8, UInt8, UInt8) {
        let tile = try XCTUnwrap(tiles.first { $0.bounds.contains(point) })
        let localX = Int(point.x - tile.bounds.minX)
        let localY = Int(point.y - tile.bounds.minY)
        let pixels = try rgbaPixels(tile.image)
        let topRow = tile.image.height - 1 - localY
        let index = (topRow * tile.image.width + localX) * 4
        return (
            pixels[index], pixels[index + 1],
            pixels[index + 2], pixels[index + 3]
        )
    }
}
