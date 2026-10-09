import CoreGraphics
import CoreImage
import XCTest

@testable import HollowKnightVision

final class GameplayFrameProcessorTests: XCTestCase {
    func testObjectDetectionIconSitsOutsideBoxLikeLabelMode() throws {
        let extent = CGRect(x: 0, y: 0, width: 640, height: 360)
        let box = CGRect(x: 120, y: 100, width: 80, height: 40)

        let destination = try XCTUnwrap(FrameRegionRenderer.objectIconDestination(
            box: box,
            iconSize: CGSize(width: 64, height: 32),
            extent: extent
        ))

        XCTAssertFalse(destination.intersects(box))
        XCTAssertEqual(destination.minX, box.minX)
        XCTAssertEqual(destination.minY, box.maxY + 4)
        XCTAssertEqual(destination.height, 20)
        XCTAssertEqual(destination.width, 40)
    }

    func testObjectDetectionIconMovesBelowTopEdgeBox() throws {
        let extent = CGRect(x: 0, y: 0, width: 640, height: 360)
        let box = CGRect(x: 120, y: 330, width: 80, height: 30)

        let destination = try XCTUnwrap(FrameRegionRenderer.objectIconDestination(
            box: box,
            iconSize: CGSize(width: 32, height: 32),
            extent: extent
        ))

        XCTAssertFalse(destination.intersects(box))
        XCTAssertEqual(destination.maxY, box.minY - 4)
    }

    func testTallWindowRemovesTitleBarFromTopEdge() {
        let window = CGRect(x: 0, y: 0, width: 1_600, height: 930)

        XCTAssertEqual(
            GameplayFrameProcessor.gameplayCrop(in: window),
            CGRect(x: 0, y: 0, width: 1_600, height: 900)
        )
    }

    func testWideWindowKeepsHorizontalCropCentered() {
        let window = CGRect(x: 10, y: 20, width: 1_700, height: 900)

        XCTAssertEqual(
            GameplayFrameProcessor.gameplayCrop(in: window),
            CGRect(x: 60, y: 20, width: 1_600, height: 900)
        )
    }

    func testObjectDetectionsOnlySupplyMovingKnightRegion() throws {
        let extent = CGRect(x: 0, y: 0, width: 640, height: 360)
        let regions = SceneRegions.fromObjectDetections([
            detection("game.playable-knight", rect: CGRect(x: 0.50, y: 0.40, width: 0.05, height: 0.10)),
            detection("game.mana", rect: CGRect(x: 0.03, y: 0.04, width: 0.05, height: 0.10)),
            detection("game.health", rect: CGRect(x: 0.12, y: 0.04, width: 0.10, height: 0.04)),
            detection("game.geo", rect: CGRect(x: 0.10, y: 0.16, width: 0.07, height: 0.04)),
        ], in: extent)

        assertRect(try XCTUnwrap(regions.knight), CGRect(x: 320, y: 180, width: 32, height: 36))
        XCTAssertTrue(regions.mana.isEmpty)
        XCTAssertTrue(regions.health.isEmpty)
        XCTAssertTrue(regions.geo.isEmpty)
        XCTAssertEqual(regions.omittedRects, [try XCTUnwrap(regions.knight)])
        XCTAssertTrue(SceneRegions.fromObjectDetections([], in: extent).omittedRects.isEmpty)
    }

    func testAtlasGameplayAlwaysMasksFixedHUDAndIgnoresRetiredHUDDetections() throws {
        let extent = CGRect(x: 0, y: 0, width: 640, height: 360)
        let regionsWithoutDetections = SceneRegions.atlasGameplay(
            fromObjectDetections: [],
            in: extent
        )
        // Live atlas writes use one continuous conservative HUD footprint so
        // gaps between independently detected HUD objects cannot persist as
        // false room texture when an inference batch is incomplete.
        XCTAssertEqual(regionsWithoutDetections.omittedRects.count, 1)
        XCTAssertTrue(regionsWithoutDetections.health.contains(CGPoint(x: 80, y: 325)))
        XCTAssertTrue(regionsWithoutDetections.mana.contains(CGPoint(x: 45, y: 310)))
        XCTAssertTrue(regionsWithoutDetections.geo.contains(CGPoint(x: 80, y: 300)))

        let detections = [
            detection(
                "game.health",
                rect: CGRect(x: 0.08, y: 0.03, width: 0.20, height: 0.08)
            ),
            detection(
                "game.mana",
                rect: CGRect(x: 0.02, y: 0.03, width: 0.10, height: 0.16)
            ),
            detection(
                "game.geo",
                rect: CGRect(x: 0.08, y: 0.13, width: 0.13, height: 0.08)
            ),
        ]
        let withRetiredDetections = SceneRegions.atlasGameplay(
            fromObjectDetections: detections,
            in: extent
        )
        XCTAssertEqual(withRetiredDetections.omittedRects, regionsWithoutDetections.omittedRects)
    }

    func testAtlasGameplayMasksAllHUDPixelsBeforePersistence() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 640, height: 360)
        let source = try XCTUnwrap(context.createCGImage(
            CIImage(color: .white).cropped(to: extent),
            from: extent
        ))
        let regions = SceneRegions.atlasGameplay(
            fromObjectDetections: [],
            in: extent
        )
        let masked = try XCTUnwrap(FrameRegionRenderer.mapFrame(
            from: source,
            omitting: regions,
            context: context
        ))

        for point in [
            CGPoint(x: 80, y: 325),
            CGPoint(x: 45, y: 310),
            CGPoint(x: 80, y: 300),
        ] {
            XCTAssertLessThan(alpha(in: masked, at: point, context: context), 10)
        }
        XCTAssertGreaterThan(
            alpha(in: masked, at: CGPoint(x: 320, y: 180), context: context),
            240
        )
    }

    func testWeakKnightCanBeUsedOnlyWhenGroundTrackingRequestsIt() throws {
        let extent = CGRect(x: 0, y: 0, width: 640, height: 360)
        let weakKnight = detection(
            "game.playable-knight",
            rect: CGRect(x: 0.45, y: 0.82, width: 0.05, height: 0.04),
            confidence: 0.08
        )

        XCTAssertNil(SceneRegions.fromObjectDetections([weakKnight], in: extent).knight)
        XCTAssertNotNil(SceneRegions.fromObjectDetections(
            [weakKnight],
            in: extent,
            knightMinimumConfidence: 0.05
        ).knight)
    }

    func testLegacyRegionBoxesAreNotPaintedOnLiveFrame() throws {
        let extent = CGRect(x: 0, y: 0, width: 100, height: 100)
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let black = CIImage(color: CIColor(red: 0, green: 0, blue: 0, alpha: 1))
            .cropped(to: extent)
        let frame = try XCTUnwrap(context.createCGImage(black, from: extent))
        let regions = SceneRegions(
            knight: CGRect(x: 45, y: 30, width: 10, height: 20),
            health: CGRect(x: 50, y: 80, width: 20, height: 10),
            geo: CGRect(x: 10, y: 60, width: 15, height: 8),
            mana: CGRect(x: 5, y: 75, width: 10, height: 15)
        )
        let output = try XCTUnwrap(FrameRegionRenderer.annotatedFrame(
            from: frame, regions: regions, context: context
        ))
        var pixels = [UInt8](repeating: 0, count: 100 * 100 * 4)
        let bitmap = try XCTUnwrap(CGContext(
            data: &pixels, width: 100, height: 100, bitsPerComponent: 8,
            bytesPerRow: 400, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        bitmap.draw(output, in: extent)
        for index in stride(from: 0, to: pixels.count, by: 4) {
            XCTAssertEqual(pixels[index], 0)
            XCTAssertEqual(pixels[index + 1], 0)
            XCTAssertEqual(pixels[index + 2], 0)
        }
    }

    func testCaptureSourceRectExcludesTitleBarBeforeRendering() {
        let sourceRect = HollowKnightCaptureConfiguration.gameplaySourceRect(
            windowSize: CGSize(width: 1_600, height: 930)
        )

        XCTAssertEqual(sourceRect, CGRect(x: 0, y: 28, width: 1_600, height: 902))
        let configuration = HollowKnightCaptureConfiguration.make(
            windowSize: CGSize(width: 1_600, height: 930)
        )
        XCTAssertEqual(configuration.sourceRect, sourceRect)
        XCTAssertEqual(configuration.width, 640)
        XCTAssertEqual(configuration.height, 360)
    }

    func testCaptureSourceRectDoesNotCropGenuinelyTallSource() {
        XCTAssertNil(HollowKnightCaptureConfiguration.gameplaySourceRect(
            windowSize: CGSize(width: 960, height: 1_080)
        ))
    }

    private func assertRect(_ actual: CGRect, _ expected: CGRect, accuracy: CGFloat = 0.000_001) {
        XCTAssertEqual(actual.minX, expected.minX, accuracy: accuracy)
        XCTAssertEqual(actual.minY, expected.minY, accuracy: accuracy)
        XCTAssertEqual(actual.width, expected.width, accuracy: accuracy)
        XCTAssertEqual(actual.height, expected.height, accuracy: accuracy)
    }

    private func alpha(in image: CGImage, at point: CGPoint, context: CIContext) -> UInt8 {
        var pixel = [UInt8](repeating: 0, count: 4)
        context.render(
            CIImage(cgImage: image),
            toBitmap: &pixel,
            rowBytes: 4,
            bounds: CGRect(x: point.x, y: point.y, width: 1, height: 1),
            format: .RGBA8,
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        return pixel[3]
    }

    private func detection(
        _ classIdentifier: String,
        rect: CGRect,
        confidence: Double = 0.9
    ) -> LiveObjectDetection {
        LiveObjectDetection(
            classIdentifier: classIdentifier,
            normalizedRect: rect,
            confidence: confidence,
            sourceFrameIdentifier: 1,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )
    }
}
