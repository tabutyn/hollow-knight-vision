import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class BroadRecentConsensusTests: XCTestCase {
    private let width = 256, height = 120

    private func pixels(seed: UInt64 = 0) -> [UInt8] {
        (0..<(width * height)).map { index in
            let x = index % width, y = index / width
            var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663) ^ seed
            hash ^= hash >> 13
            hash &*= 1_274_126_177
            return UInt8(truncatingIfNeeded: hash >> 11)
        }
    }

    private func fixture(falsePatches: Int, oldCells: Int = 8, recentCells: Int = 8,
                         rows: [Int] = [40, 80], oldNoise: Int, recentNoise: Int,
                         solveTime: Double = 2.0 / 60) -> (GroundCameraSolver, GroundCameraSolver.Solution?, [UInt8], [CleanFloorLine]) {
        let original = pixels()
        var current = pixels(seed: 99_887)
        // Surviving old tiles repeat at the wrong phase. The wider current
        // surface has moved left four pixels since the recent observation.
        for y in 40..<52 { for x in 24..<(24 + falsePatches * 16) {
            let noise = (x + y).isMultiple(of: 2) ? oldNoise : -oldNoise
            current[y * width + x] = UInt8(clamping: Int(original[y * width + x - 8]) + noise)
        } }
        var recent = pixels(seed: 777)
        var continued = pixels(seed: 999)
        for y in 0..<height {
            for x in 4..<width {
                let noise = (x + y).isMultiple(of: 2) ? recentNoise : -recentNoise
                recent[y * width + x] = UInt8(clamping: Int(current[y * width + x - 4]) + noise)
            }
            for x in 0..<(width - 4) { continued[y * width + x] = current[y * width + x + 4] }
        }
        let solver = GroundCameraSolver()
        let lines = rows.map { CleanFloorLine(row: $0, xRange: 16...(16 + recentCells * 16 - 1)) }
        _ = solver.solve(pixels: original, width: width, height: height,
            lines: [CleanFloorLine(row: 40, xRange: 16...(16 + oldCells * 16 - 1))],
            timestamp: 0, exclusions: [])
        solver.followMeasuredCamera(correction: CGVector(dx: 4, dy: 0), pixels: recent,
            width: width, height: height, lines: lines, timestamp: 1.0 / 60, exclusions: [])
        return (solver, solver.solve(pixels: current, width: width, height: height,
            lines: lines, timestamp: solveTime, exclusions: []), continued, lines)
    }

    func testFivePreciseRecentPatchesOverrideFourRepeatedOldTiles() throws {
        let (_, result, _, _) = fixture(falsePatches: 4, oldCells: 7, recentCells: 5,
            rows: [40], oldNoise: 4, recentNoise: 0)
        XCTAssertEqual(try XCTUnwrap(result).imageTranslation, CGVector(dx: -4, dy: 0))
    }

    func testEquallySupportedPreciseRecentSurfaceWinsAndStaysAnchored() throws {
        for count in [5, 6] {
            let (solver, result, continued, lines) = fixture(falsePatches: count,
                recentCells: count, rows: [40], oldNoise: 4, recentNoise: 2)
            XCTAssertEqual(try XCTUnwrap(result).imageTranslation, CGVector(dx: -4, dy: 0))
            let next = try XCTUnwrap(solver.solve(pixels: continued, width: width, height: height,
                lines: lines, timestamp: 3.0 / 60, exclusions: []))
            XCTAssertEqual(next.imageTranslation, CGVector(dx: -4, dy: 0))
        }
    }

    func testEqualRecentSupportStillRequiresPrecisionFreshnessAndFivePatches() throws {
        for (count, noise, time) in [(4, 0, 2.0 / 60), (5, 8, 2.0 / 60), (5, 0, 0.2)] {
            let (_, result, _, _) = fixture(falsePatches: 5, recentCells: count,
                rows: [40], oldNoise: 4, recentNoise: noise, solveTime: time)
            XCTAssertEqual(try XCTUnwrap(result).imageTranslation, CGVector(dx: 12, dy: 0))
        }
    }

    func testBroadNoisyRecentSurfaceOverridesSparseOldPhaseAndStaysAnchored() throws {
        for (falsePatches, oldNoise, recentNoise) in [(5, 16, 10), (6, 12, 8)] {
            let (solver, result, continued, lines) = fixture(falsePatches: falsePatches,
                oldNoise: oldNoise, recentNoise: recentNoise)
            XCTAssertEqual(try XCTUnwrap(result).imageTranslation, CGVector(dx: -4, dy: 0))
            let next = try XCTUnwrap(solver.solve(pixels: continued, width: width, height: height,
                lines: lines, timestamp: 3.0 / 60, exclusions: []))
            XCTAssertEqual(next.imageTranslation, CGVector(dx: -4, dy: 0))
        }
    }

    func testBroadRecentEvidenceMustBeFreshAndClearlyBetter() throws {
        let (_, noisy, _, _) = fixture(falsePatches: 6, oldNoise: 4, recentNoise: 12)
        let (_, stale, _, _) = fixture(falsePatches: 6, oldNoise: 4, recentNoise: 0, solveTime: 0.2)
        XCTAssertEqual(try XCTUnwrap(noisy).support, 6)
        XCTAssertEqual(try XCTUnwrap(stale).support, 6)
    }
}
