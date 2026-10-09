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
