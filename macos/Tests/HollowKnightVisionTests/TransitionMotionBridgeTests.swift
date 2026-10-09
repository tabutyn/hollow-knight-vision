import CoreGraphics
import CoreImage
import XCTest
@testable import HollowKnightVision

final class TransitionMotionBridgeTests: XCTestCase {
    func testDarkeningRemainsTrackableAndStableRoomResumesAfterBlackout() {
        var gate = TransitionFrameGate()
        XCTAssertTrue(gate.observe(profile(mean: 100, visible: 0.8)).admitsTracking)

        let fade = gate.observe(profile(mean: 18, visible: 0.2))
        XCTAssertTrue(fade.admitsTracking)
        XCTAssertFalse(gate.observe(profile(mean: 0, signaled: 0, visible: 0)).admitsTracking)

        XCTAssertFalse(gate.observe(profile(mean: 12, visible: 0.15)).admitsTracking)
        XCTAssertFalse(gate.observe(profile(mean: 12, visible: 0.15)).admitsTracking)
        XCTAssertFalse(gate.observe(profile(mean: 12, visible: 0.15)).admitsTracking)
        let resumed = gate.observe(profile(mean: 12, visible: 0.15))
        XCTAssertTrue(resumed.admitsTracking)
        XCTAssertTrue(resumed.resumedAfterTransition)
        XCTAssertTrue(gate.observe(profile(mean: 11.5, visible: 0.14)).admitsTracking)
    }

    func testResumeAfterBlackoutDoesNotInventRoomOffset() throws {
        let image = try XCTUnwrap(solidImage(red: 64, green: 80, blue: 96))
        let bridge = TransitionMotionBridge()
        bridge.anchor(frame: image, excluding: [], cameraPosition: CGPoint(x: 10, y: 4))
        bridge.suspendForTransition()

        let position = try XCTUnwrap(bridge.resumeAfterTransition(
            frame: image,
            excluding: []
        ))
        XCTAssertEqual(position.x, 10, accuracy: 0.000_001)
        XCTAssertEqual(position.y, 4, accuracy: 0.000_001)
    }

    func testResumeRequiresSuspension() throws {
        let image = try XCTUnwrap(solidImage(red: 64, green: 80, blue: 96))
        let bridge = TransitionMotionBridge()
        bridge.anchor(frame: image, excluding: [], cameraPosition: CGPoint(x: 10, y: 4))
        XCTAssertNil(bridge.resumeAfterTransition(frame: image, excluding: []))
    }

    func testFallbackTracksHorizontalTranslationWithoutVerticalDrift() throws {
        let reference = try asymmetricTexture(horizontalShift: 0)
        let movedLeft = try asymmetricTexture(horizontalShift: -8)
        let bridge = TransitionMotionBridge()
        bridge.anchor(frame: reference, excluding: [], cameraPosition: .zero)

        let result = try XCTUnwrap(bridge.track(
            frame: movedLeft,
            excluding: [],
            solveWidth: CGFloat(reference.width),
            lockVertical: true
        ))
        XCTAssertEqual(result.cameraPosition.x, 8, accuracy: 1.5)
        XCTAssertEqual(result.cameraPosition.y, 0, accuracy: 0.001)
        XCTAssertGreaterThanOrEqual(result.confidence, TransitionMotionBridge.minimumConfidence)
        XCTAssertEqual(result.source, .maskedRegistration)
    }

    func testFallbackIgnoresMovingExcludedForeground() throws {
        let background = try asymmetricTexture(horizontalShift: 0)
        let firstRect = CGRect(x: 45, y: 55, width: 100, height: 90)
        let secondRect = CGRect(x: 235, y: 55, width: 100, height: 90)
        let reference = try foregroundImage(background: background, rect: firstRect)
        let current = try foregroundImage(background: background, rect: secondRect)
        let bridge = TransitionMotionBridge()
        bridge.anchor(frame: reference, excluding: [firstRect], cameraPosition: .zero)

        let result = try XCTUnwrap(bridge.track(
            frame: current,
            excluding: [secondRect],
            solveWidth: CGFloat(reference.width),
            lockVertical: true
        ))
        XCTAssertEqual(result.cameraPosition.x, 0, accuracy: 0.5)
        XCTAssertEqual(result.cameraPosition.y, 0, accuracy: 0.001)
    }

    func testTransitionBlackBandsBecomeStraightRectangularOmissions() {
        let bands = FrameEdgeBlackBands(
            left: FrameEdgeBlackBand(
                widthFraction: 0.10,
                contentYRangeFraction: 0.35...0.65
            ),
            right: FrameEdgeBlackBand(
                widthFraction: 0.20,
                contentYRangeFraction: 0.35...0.65
            )
        )
        XCTAssertEqual(
            bands.rectangularOmissions(frameSize: CGSize(width: 640, height: 360)),
            [
                CGRect(x: 0, y: 0, width: 65, height: 360),
                CGRect(x: 511, y: 0, width: 129, height: 360),
            ]
        )
    }

    func testGroundPoseContinuityRejectsStaleLocalSnapWithoutGlobalSupport() {
        XCTAssertFalse(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: -121, y: 4),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 0,
            hasGlobalCorrection: false,
            localInlierCount: 4
        ))
        XCTAssertFalse(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: -324, y: -18),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 0,
            hasGlobalCorrection: false,
            localInlierCount: 3
        ))
        XCTAssertTrue(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: -361, y: 1),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 0,
            hasGlobalCorrection: false,
            localInlierCount: 9
        ))
        XCTAssertTrue(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: -319, y: 2),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 0,
            hasGlobalCorrection: false,
            localInlierCount: 4
        ))
        XCTAssertTrue(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: -324, y: -18),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 0,
            hasGlobalCorrection: false,
            localInlierCount: 4
        ))
        XCTAssertFalse(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: -324, y: -32),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 0,
            hasGlobalCorrection: false
        ))
        XCTAssertTrue(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: 48, y: -20),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 4,
            hasGlobalCorrection: true
        ))
        XCTAssertFalse(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: 48, y: -20),
            current: CGPoint(x: -324, y: 0),
            solveWidth: 640,
            globalMatchCount: 3,
            hasGlobalCorrection: true
        ))
    }

    func testMovingForegroundRejectsOnlySmallRegistrationResidual() {
        let previous = CGRect(x: 100, y: 40, width: 24, height: 32)
        let current = previous.offsetBy(dx: 4, dy: 0)
        let residual = TransitionForegroundResidualGate.adjusted(
            CGVector(dx: -1, dy: 0),
            previousForeground: previous,
            currentForeground: current,
            frameWidth: 640,
            solveWidth: 640
        )
        XCTAssertEqual(residual.dx, 0)

        let sceneMotion = TransitionForegroundResidualGate.adjusted(
            CGVector(dx: -5, dy: 0),
            previousForeground: previous,
            currentForeground: current,
            frameWidth: 640,
            solveWidth: 640
        )
        XCTAssertEqual(sceneMotion.dx, -5)
    }

    func testSmallMotionRequiresSustainedDirectionalEvidence() {
        var accumulator = TransitionSmallMotionAccumulator()
        XCTAssertEqual(accumulator.ingest(-1), 0)
        XCTAssertEqual(accumulator.ingest(-1), 0)
        XCTAssertEqual(accumulator.ingest(-1), 0)
        XCTAssertEqual(accumulator.ingest(0), 0)
        XCTAssertEqual(accumulator.ingest(0), 0)
        XCTAssertEqual(accumulator.ingest(0), 0)

        XCTAssertEqual(accumulator.ingest(1), 0)
        XCTAssertEqual(accumulator.ingest(1), 0)
        XCTAssertEqual(accumulator.ingest(1), 0)
        XCTAssertEqual(accumulator.ingest(1), 4)
        XCTAssertEqual(accumulator.ingest(-5), -5)
    }

    private func profile(
        mean: Double,
        signaled: Double = 0.2,
        visible: Double
    ) -> FrameSignalProfile {
        FrameSignalProfile(
            sampleCount: 100,
            signaledCount: Int(signaled * 100),
            visibleCount: Int(visible * 100),
            meanPeak: mean
        )
    }

    private func solidImage(red: UInt8, green: UInt8, blue: UInt8) -> CGImage? {
        let pixels = [UInt8](repeating: 0, count: 16 * 16 * 4).enumerated().map {
            switch $0.offset % 4 {
            case 0: return red
            case 1: return green
            case 2: return blue
            default: return 255
            }
        }
        return image(width: 16, height: 16, pixels: pixels)
    }

    private func asymmetricTexture(horizontalShift: CGFloat) throws -> CGImage {
        let extent = CGRect(x: 0, y: 0, width: 384, height: 216)
        var image = CIImage(color: CIColor(red: 0.03, green: 0.04, blue: 0.06))
            .cropped(to: extent)
        var state: UInt64 = 0xD1CE_BA5E_5EED
        func next() -> UInt64 {
            state = state &* 6_364_136_223_846_793_005
                &+ 1_442_695_040_888_963_407
            return state
        }
        for index in 0..<180 {
            let x = CGFloat(next() % 350) + 12
            let y = CGFloat(next() % 182) + 12
            let width = CGFloat(2 + next() % 13)
            let height = CGFloat(2 + next() % 11)
            let red = CGFloat((next() >> 8) % 220 + 25) / 255
            let green = CGFloat((next() >> 16) % 220 + 25) / 255
            let blue = CGFloat((next() >> 24) % 220 + 25) / 255
            let color = CIColor(
                red: red,
                green: green,
                blue: blue,
                alpha: index.isMultiple(of: 5) ? 0.7 : 1
            )
            let rect = CGRect(x: x, y: y, width: width, height: height)
            image = CIImage(color: color)
                .cropped(to: rect.offsetBy(dx: horizontalShift, dy: 0))
                .composited(over: image)
        }
        return try XCTUnwrap(CIContext(options: [.useSoftwareRenderer: true])
            .createCGImage(image, from: extent))
    }

    private func foregroundImage(background: CGImage, rect: CGRect) throws -> CGImage {
        let source = CIImage(cgImage: background)
        let foreground = CIImage(color: CIColor(red: 0.95, green: 0.1, blue: 0.2))
            .cropped(to: rect)
            .composited(over: source)
        return try XCTUnwrap(CIContext(options: [.useSoftwareRenderer: true])
            .createCGImage(foreground, from: source.extent))
    }

    private func image(width: Int, height: Int, pixels: [UInt8]) -> CGImage? {
        guard let provider = CGDataProvider(data: Data(pixels) as CFData) else { return nil }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

}
