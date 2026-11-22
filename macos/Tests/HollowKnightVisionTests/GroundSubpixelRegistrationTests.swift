import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundSubpixelRegistrationTests: XCTestCase {
    private let width = 320
    private let height = 180
    private let rows = [30, 85, 130]
    private let columns = [24, 72, 128, 184, 240]

    func testRecoversFractionalTranslationOnIndependentValidationPixels() throws {
        let source = frame()
        let target = frame(dx: 4, dy: -2, offsetX: 0.4, offsetY: -0.3)
        let result = try XCTUnwrap(GroundSubpixelRegistration.refine(
            patches: patches(source), pixels: target,
            width: width, height: height, dx: 4, dy: -2
        ))
        XCTAssertEqual(result.offset.dx, 0.4, accuracy: 0.07)
        XCTAssertEqual(result.offset.dy, -0.3, accuracy: 0.07)
        XCTAssertLessThanOrEqual(result.iterations, 6)
    }

    func testNormalizedPatchJacobianRecoversMotionAcrossExposureChanges() throws {
        // Broad illumination gradients dominate the raw image derivative but
        // their patch means are removed from the residual. Differentiate the
        // same normalized signal that is actually being minimized.
        func pixels(offset: CGPoint, gain: Double, mean: Double) -> [UInt8] {
            (0..<(width * height)).map { index in
                let x = Double(index % width) - offset.x
                let y = Double(index / width) - offset.y
                let signal = 45 * sin(0.08 * x + 0.07 * y)
                    + 35 * cos(0.04 * x - 0.08 * y)
                    + 7 * sin(0.47 * x + 0.2 * y)
                    + 6 * cos(0.17 * x - 0.31 * y)
                return UInt8(clamping: Int((mean + gain * signal).rounded()))
            }
        }
        let source = pixels(offset: .zero, gain: 1, mean: 110)
        for gain in [0.55, 1.0, 1.3] {
            let target = pixels(offset: CGPoint(x: 0.8, y: 0.7), gain: gain, mean: 120)
            let result = try XCTUnwrap(GroundSubpixelRegistration.refine(
                patches: patches(source), pixels: target,
                width: width, height: height, dx: 0, dy: 0))
            XCTAssertEqual(result.offset.dx, 0.8, accuracy: 0.07, "gain=\(gain)")
            XCTAssertEqual(result.offset.dy, 0.7, accuracy: 0.07, "gain=\(gain)")
            XCTAssertLessThanOrEqual(result.iterations, 6)
        }
    }

    func testIntegratedSolverPublishesFractionalTranslation() throws {
        let solver = GroundCameraSolver(subpixelEnabled: true)
        _ = solver.solve(
            pixels: frame(), width: width, height: height,
            lines: lines(dx: 0, dy: 0), timestamp: 0, exclusions: []
        )
        let result = try XCTUnwrap(solver.solve(
            pixels: frame(dx: -3, dy: 5, offsetX: 0.35, offsetY: -0.25),
            width: width, height: height, lines: lines(dx: -3, dy: 5),
            timestamp: 1.0 / 60, exclusions: []
        ))
        let displacement = try XCTUnwrap(result.matchedImageDisplacement)
        XCTAssertEqual(result.imageTranslation.dx, -2.65, accuracy: 0.12)
        XCTAssertEqual(result.imageTranslation.dy, 4.75, accuracy: 0.12)
        XCTAssertEqual(displacement.dx, -2.65, accuracy: 0.12)
        XCTAssertEqual(displacement.dy, 4.75, accuracy: 0.12)
    }

    func testProductionTrackerMeasuresFractionalMotionBetweenKeyframes() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0,
            productionCameraSolver: true)
        // A horizontal pan stays level even between reference promotions.
        // Check actual movement too, so holding the pose cannot pass.
        for index in 0...18 {
            let movement = Double(index) * 0.35
            let pixels = frame(offsetX: movement)
            let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
            let image = try XCTUnwrap(CGImage(width: width, height: height,
                bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: width,
                space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: 0),
                provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
            let result = tracker.update(frame: image, floorLines: lines(dx: 0, dy: 0),
                timestamp: Double(index) / 60, sourceLuma: pixels)
            guard index > 0 else { continue }
            let camera = try XCTUnwrap(result.cameraPosition)
            XCTAssertEqual(camera.x, -movement * GroundCameraSolver.hackerCalibratedAnchorScale.dx,
                accuracy: 0.15, "capture \(index)")
            XCTAssertEqual(camera.y, 0, accuracy: 0.12, "capture \(index)")
            XCTAssertTrue(result.poseVerified, "capture \(index)")
        }
    }

    func testSceneryCalibrationReceivesProjectedReferenceDisplacement() throws {
        let solver = GroundCameraSolver(imageMotionScale: CGVector(dx: 0.8, dy: 1.2))
        _ = solver.solve(pixels: frame(), width: width, height: height,
            lines: lines(dx: 0, dy: 0), timestamp: 0, exclusions: [])
        let moved = try XCTUnwrap(solver.solve(pixels: frame(dx: -4, dy: 5),
            width: width, height: height, lines: lines(dx: -4, dy: 5),
            timestamp: 1.0 / 60, exclusions: []))
        let raw = try XCTUnwrap(moved.matchedImageDisplacement)
        let projected = try XCTUnwrap(moved.projectedImageDisplacement)
        XCTAssertEqual(raw.dx, -4, accuracy: 1e-8)
        XCTAssertEqual(raw.dy, 5, accuracy: 1e-8)
        XCTAssertEqual(projected.dx, -3.2, accuracy: 1e-8)
        XCTAssertEqual(projected.dy, 6, accuracy: 1e-8)
        XCTAssertEqual(moved.imageTranslation, projected)
        XCTAssertEqual(moved.referenceTimestamp, 0)
    }

    func testRefinedProvisionalMotionRetainsCameraScaleWithoutIntegerPhaseAdjustment() throws {
        for customScale in [false, true] {
            let solver = GroundCameraSolver(keyframeSubpixelEnabled: true,
                calibratedMotionEnabled: true,
                imageMotionScale: customScale ? CGVector(dx: 0.9, dy: 0.8) : nil)
            _ = solver.solve(pixels: [UInt8](repeating: 80, count: width * height),
                width: width, height: height, lines: lines(dx: 0, dy: 0), timestamp: 0, exclusions: [])
            solver.followMeasuredCamera(correction: .zero, pixels: frame(),
                width: width, height: height, lines: lines(dx: 0, dy: 0), timestamp: 0.1, exclusions: [])
            let moved = try XCTUnwrap(solver.solve(pixels: frame(dx: 16, offsetX: 0.4),
                width: width, height: height, lines: lines(dx: 16, dy: 0), timestamp: 0.2, exclusions: []))
            XCTAssertEqual(moved.matchSource, "provisional")
            XCTAssertFalse(moved.referenceTrusted)
            let raw = try XCTUnwrap(moved.matchedImageDisplacement)
            XCTAssertEqual(raw.dx, 16.4, accuracy: 0.07)
            // Fixture image displacement is 16.4 px; the frozen camera-plane
            // projection is 16.28356 px. A refined match no longer has the
            // integer-phase bias that the coarse recent-motion adjustment models.
            let projected = try XCTUnwrap(moved.projectedImageDisplacement)
            XCTAssertEqual(projected.dx, customScale ? 14.76 : 16.28356, accuracy: 0.10)
        }
    }

    func testExactIntegerMotionStaysExactAndFlatFrameIsRejected() throws {
        let solver = GroundCameraSolver(subpixelEnabled: true)
        _ = solver.solve(
            pixels: frame(), width: width, height: height,
            lines: lines(dx: 0, dy: 0), timestamp: 0, exclusions: []
        )
        let moved = try XCTUnwrap(solver.solve(
            pixels: frame(dx: -4, dy: 5), width: width, height: height,
            lines: lines(dx: -4, dy: 5), timestamp: 1.0 / 60, exclusions: []
        ))
        XCTAssertEqual(moved.imageTranslation, CGVector(dx: -4, dy: 5))
        XCTAssertNil(GroundSubpixelRegistration.refine(
            patches: patches(frame()),
            pixels: [UInt8](repeating: 80, count: width * height),
            width: width, height: height, dx: 0, dy: 0
        ))
    }

    private func texture(_ x: Double, _ y: Double) -> UInt8 {
        UInt8(clamping: Int((110 + 18 * sin(0.47 * x + 0.2 * y)
            + 22 * cos(0.17 * x - 0.31 * y)
            + 20 * sin(0.11 * x + 0.57 * y)
            + 16 * cos(0.63 * x - 0.23 * y)).rounded()))
    }

    private func frame(
        dx: Int = 0,
        dy: Int = 0,
        offsetX: Double = 0,
        offsetY: Double = 0
    ) -> [UInt8] {
        (0..<(width * height)).map { index in
            texture(
                Double(index % width - dx) - offsetX,
                Double(index / width - dy) - offsetY
            )
        }
    }

    private func patches(_ source: [UInt8]) -> [GroundSubpixelRegistration.Patch] {
        rows.flatMap { y in
            columns.map { x in
                var values = [UInt8]()
                for row in 0..<12 {
                    let start = (y + row) * width + x
                    values.append(contentsOf: source[start..<(start + 16)])
                }
                return GroundSubpixelRegistration.Patch(x: x, y: y, pixels: values)
            }
        }
    }

    private func lines(dx: Int, dy: Int) -> [CleanFloorLine] {
        rows.map { row in
            CleanFloorLine(row: row + dy, xRange: 16...303)
        }
    }
}
