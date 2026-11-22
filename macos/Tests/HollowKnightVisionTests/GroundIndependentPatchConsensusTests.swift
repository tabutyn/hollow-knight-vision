import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundIndependentPatchConsensusTests: XCTestCase {
    private let width = 320, height = 180
    private let rows = [30, 85, 130], columns = [24, 72, 128, 184, 240]

    func testIndependentFloorMajorityRejectsMovingForegroundCompromise() throws {
        let source = frame()
        let patches = makePatches(source)
        var target = frame(dx: 36, dy: 6)
        insertForeground(into: &target, patches: Array(patches.prefix(5)), dx: 40, dy: 6)
        let result = try XCTUnwrap(GroundIndependentPatchConsensus.refine(
            patches: patches, pixels: target, width: width, height: height,
            dx: 38, dy: 6, exclusions: []))
        XCTAssertEqual(result.dx, 36)
        XCTAssertEqual(result.dy, 6)
        XCTAssertGreaterThanOrEqual(result.patches.count, 8)
        XCTAssertLessThan(result.error, 0.001)
        let solver = GroundCameraSolver(keyframeSubpixelEnabled: true,
            independentPatchConsensusEnabled: true)
        _ = solver.solve(pixels: source, width: width, height: height,
            lines: rows.map { CleanFloorLine(row: $0, xRange: 16...303) },
            timestamp: 0, exclusions: [])
        let moved = try XCTUnwrap(solver.solve(pixels: target, width: width, height: height,
            lines: rows.map { CleanFloorLine(row: $0 + 6, xRange: 52...303) },
            timestamp: 0.1, exclusions: []))
        XCTAssertEqual(moved.imageTranslation.dx, 36, accuracy: 0.2)
        XCTAssertEqual(moved.imageTranslation.dy, 6, accuracy: 0.2)
        let pooledSolver = GroundCameraSolver(keyframeSubpixelEnabled: true)
        _ = pooledSolver.solve(pixels: source, width: width, height: height,
            lines: rows.map { CleanFloorLine(row: $0, xRange: 16...303) },
            timestamp: 0, exclusions: [])
        let pooled = try XCTUnwrap(pooledSolver.solve(pixels: target, width: width, height: height,
            lines: rows.map { CleanFloorLine(row: $0 + 6, xRange: 52...303) },
            timestamp: 0.1, exclusions: []))
        // Selecting a cleaner subset must not upgrade the original population's
        // confidence and silently change reference admission or source ranking.
        XCTAssertGreaterThanOrEqual(moved.error, pooled.error)
        XCTAssertLessThanOrEqual(moved.support, pooled.support)
    }

    func testTieDarknessMasksAndRepeatedTextureDoNotEstablishConsensus() {
        let source = frame(), patches = Array(makePatches(frame()).prefix(14))
        var tied = frame(dx: 36, dy: 6)
        insertForeground(into: &tied, patches: Array(patches.prefix(7)), dx: 40, dy: 6)
        func fit(_ pixels: [UInt8], excluding: [CGRect] = []) -> GroundIndependentPatchConsensus.Result? {
            GroundIndependentPatchConsensus.refine(patches: patches, pixels: pixels,
                width: width, height: height, dx: 38, dy: 6, exclusions: excluding)
        }
        XCTAssertNil(fit(tied))
        XCTAssertNil(fit([UInt8](repeating: 0, count: source.count)))
        XCTAssertNil(fit(frame(dx: 36, dy: 6),
            excluding: [CGRect(x: 0, y: 0, width: width, height: height)]))
        let periodic = (0..<(width * height)).map { UInt8(($0 % width) % 4 < 2 ? 60 : 180) }
        XCTAssertNil(GroundIndependentPatchConsensus.refine(patches: makePatches(periodic),
            pixels: periodic, width: width, height: height, dx: 0, dy: 0, exclusions: []))
    }

    func testMedianRevalidatesEveryPatchBeforePublishingItsQuality() throws {
        var source = frame(), target = frame(dx: 1)
        let x = columns[0], y = rows[0]
        let edge: [UInt8] = [35, 201, 81, 171, 15, 230, 45, 153, 72, 210, 95, 126]
        for row in 0..<12 {
            for column in 0..<16 { source[(y + row) * width + x + column] = 0 }
            source[(y + row) * width + x] = edge[row]
        }
        for row in (y - 4)..<(y + 16) {
            for column in (x - 4)..<(x + 20) { target[row * width + column] = 0 }
        }
        for row in 0..<12 { target[(y + row) * width + x] = edge[row] }
        // Fourteen patches move one pixel. One thin foreground edge stays
        // still and individually verifies at zero; at the majority's median
        // displacement its target window is flat and has infinite error.
        let result = try XCTUnwrap(GroundIndependentPatchConsensus.refine(
            patches: makePatches(source), pixels: target, width: width, height: height,
            dx: 0, dy: 0, exclusions: []))
        XCTAssertEqual(result.dx, 1)
        XCTAssertEqual(result.dy, 0)
        XCTAssertTrue(result.error.isFinite)
        XCTAssertLessThan(result.error, 0.001)
        XCTAssertEqual(result.patches.count, 14)
        XCTAssertFalse(result.patches.contains { $0.x == x && $0.y == y })
    }

    func testInsufficientUniqueOrSpatialEvidenceAndInvalidBoundsAreRejected() {
        let source = frame(), patches = makePatches(frame())
        func fit(_ selected: [GroundSubpixelRegistration.Patch], dx: Int = 0) -> GroundIndependentPatchConsensus.Result? {
            GroundIndependentPatchConsensus.refine(patches: selected, pixels: source,
                width: width, height: height, dx: dx, dy: 0, exclusions: [])
        }
        XCTAssertNil(fit(Array(patches.prefix(3))))
        XCTAssertNil(fit([GroundSubpixelRegistration.Patch](repeating: patches[0], count: 4)))
        XCTAssertNil(fit([GroundSubpixelRegistration.Patch](repeating: patches[0], count: 33)))
        XCTAssertNil(fit(patches, dx: .max))
        XCTAssertNil(fit(patches, dx: .min))
        let narrow = [24, 28, 32, 36].map { x in
            GroundSubpixelRegistration.Patch(x: x, y: 30,
                pixels: GroundPixels.patch(source, width: width, height: height, x: x, y: 30)!)
        }
        XCTAssertNil(fit(narrow))
        XCTAssertNil(GroundIndependentPatchConsensus.refine(patches: patches, pixels: [],
            width: .max, height: 180, dx: 0, dy: 0, exclusions: []))
    }

    private func frame(dx: Int = 0, dy: Int = 0) -> [UInt8] {
        (0..<(width * height)).map { index in
            let x = Double(index % width - dx), y = Double(index / width - dy)
            let value = 110 + 18 * sin(0.47 * x + 0.2 * y)
                + 22 * cos(0.17 * x - 0.31 * y)
                + 20 * sin(0.11 * x + 0.57 * y)
                + 16 * cos(0.63 * x - 0.23 * y)
            return UInt8(clamping: Int(value.rounded()))
        }
    }

    private func makePatches(_ pixels: [UInt8]) -> [GroundSubpixelRegistration.Patch] {
        rows.flatMap { y in columns.map { x in
            .init(x: x, y: y, pixels: GroundPixels.patch(pixels,
                width: width, height: height, x: x, y: y)!)
        } }
    }

    private func insertForeground(into pixels: inout [UInt8],
        patches: [GroundSubpixelRegistration.Patch], dx: Int, dy: Int) {
        let foreground = frame(dx: dx, dy: dy)
        for patch in patches {
            for y in (patch.y + dy - 4)..<(patch.y + dy + 16) {
                let start = y * width + patch.x + 32
                pixels.replaceSubrange(start..<(start + 24), with: foreground[start..<(start + 24)])
            }
        }
    }
}
