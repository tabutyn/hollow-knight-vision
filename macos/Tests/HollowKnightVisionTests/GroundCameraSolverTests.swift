import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundCameraSolverTests: XCTestCase {
    func testReferencePromotionRequiresCleanSparseOrBroadMatch() {
        XCTAssertTrue(GroundReferencePromotionPolicy.permits(support: 4, error: 10))
        XCTAssertFalse(GroundReferencePromotionPolicy.permits(support: 4, error: 10.01))
        XCTAssertFalse(GroundReferencePromotionPolicy.permits(support: 5, error: 14.5))
        XCTAssertTrue(GroundReferencePromotionPolicy.permits(support: 6, error: 14.5))
        XCTAssertTrue(GroundReferencePromotionPolicy.permits(support: 12, error: 18))
        XCTAssertFalse(GroundReferencePromotionPolicy.permits(support: 12, error: 18.01))
    }

    func testSparseHighCostPoseIsRejectedBeforeItCanJump() {
        XCTAssertFalse(GroundPoseAcceptancePolicy.rejects(support: 4, error: 14))
        XCTAssertTrue(GroundPoseAcceptancePolicy.rejects(support: 4, error: 14.01))
        XCTAssertFalse(GroundPoseAcceptancePolicy.rejects(support: 5, error: 14.01))
        XCTAssertTrue(GroundPoseAcceptancePolicy.rejects(support: 7, error: 18.01))
        XCTAssertFalse(GroundPoseAcceptancePolicy.rejects(support: 8, error: 18.01))
    }

    func testSparseHighCostMotionInnovationIsRejected() {
        XCTAssertTrue(GroundPoseInnovationPolicy.rejects(
            support: 9, error: 19.45, innovation: 11.48
        ))
        XCTAssertFalse(GroundPoseInnovationPolicy.rejects(
            support: 12, error: 19.45, innovation: 11.48
        ))
        XCTAssertFalse(GroundPoseInnovationPolicy.rejects(
            support: 9, error: 19, innovation: 11.48
        ))
        XCTAssertFalse(GroundPoseInnovationPolicy.rejects(
            support: 9, error: 19.45, innovation: 8
        ))
    }

    func testProvisionalPromotionRequiresSustainedStrongTexture() {
        XCTAssertFalse(GroundProvisionalPromotionPolicy.permits(
            elapsed: 0.199, consecutiveMatches: 12, support: 6, error: 18
        ))
        XCTAssertFalse(GroundProvisionalPromotionPolicy.permits(
            elapsed: 0.2, consecutiveMatches: 11, support: 6, error: 18
        ))
        XCTAssertFalse(GroundProvisionalPromotionPolicy.permits(
            elapsed: 0.2, consecutiveMatches: 12, support: 5, error: 10.01
        ))
        XCTAssertFalse(GroundProvisionalPromotionPolicy.permits(
            elapsed: 0.2, consecutiveMatches: 12, support: 6, error: 18.01
        ))
        XCTAssertTrue(GroundProvisionalPromotionPolicy.permits(
            elapsed: 0.2, consecutiveMatches: 12, support: 6, error: 18
        ))
        XCTAssertTrue(GroundProvisionalPromotionPolicy.permits(
            elapsed: 0.2, consecutiveMatches: 12, support: 4, error: 10
        ))
    }

    func testRecentReferenceCadenceAvoidsPerFrameQuantization() {
        XCTAssertFalse(GroundRecentReferenceCadence.shouldRefresh(age: 0.079, travel: 15.99))
        XCTAssertTrue(GroundRecentReferenceCadence.shouldRefresh(age: 0.08, travel: 0))
        XCTAssertTrue(GroundRecentReferenceCadence.shouldRefresh(age: 0, travel: 16))
    }

    func testImageMotionScaleChangesCoordinateWithoutChangingRawMatch() throws {
        let scale = CGVector(dx: 0.9, dy: 0.8)
        let solver = GroundCameraSolver(imageMotionScale: scale)
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        let moved = try XCTUnwrap(solve(
            solver, dx: -12, dy: 10, row: 50, time: 1.0 / 60
        ))
        XCTAssertEqual(moved.matchedImageDisplacement, CGVector(dx: -12, dy: 10))
        XCTAssertEqual(moved.imageTranslation.dx, -10.8, accuracy: 0.000_001)
        XCTAssertEqual(moved.imageTranslation.dy, 8, accuracy: 0.000_001)
    }

    func testStationaryRefreshNearOldAnchorDoesNotInventMotion() throws {
        let width = 320, height = 160
        for (dx, dy) in [(1, 0), (2, 0), (-2, 0), (0, 1), (0, -2), (1, 1)] {
            let solver = GroundCameraSolver()
            var position = CGVector.zero
            let captures = [(0, 0, 0.0), (dx, dy, 0.016), (dx, dy, 1.2),
                            (dx, dy, 1.22), (0, 0, 1.24)]
            for (x, y, timestamp) in captures {
                let result = try XCTUnwrap(solver.solve(
                    pixels: pixels(width: width, height: height, dx: x, dy: y),
                    width: width, height: height,
                    lines: [CleanFloorLine(row: 40 + y, xRange: 16...303)],
                    timestamp: timestamp, exclusions: []))
                position.dx += result.imageTranslation.dx
                position.dy += result.imageTranslation.dy
                XCTAssertEqual(position, CGVector(dx: x, dy: y),
                    "Reference refresh must preserve a measured nearby position")
            }
        }
    }

    func testStrongRecentStripOverridesWrongPhaseInWeakOldAnchor() throws {
        let width = 160, height = 100
        let original = pixels()
        func distinctPixels(seed: UInt64) -> [UInt8] {
            (0..<(width * height)).map { index in
                let x = index % width, y = index / width
                var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663) ^ seed
                hash ^= hash >> 13
                hash &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: hash >> 11)
            }
        }
        var current = distinctPixels(seed: 99_887)
        // A few old repeating tiles suggest the wrong (+8) image phase.
        // The broader recently observed surface moves left by four pixels.
        for y in 40..<52 { for x in 24..<72 {
            let noise = (x + y).isMultiple(of: 2) ? 4 : -4
            current[y * width + x] = UInt8(clamping: Int(original[y * width + x - 8]) + noise)
        } }
        var recent = distinctPixels(seed: 777)
        var continued = distinctPixels(seed: 999)
        for y in 0..<height {
            for x in 4..<width { recent[y * width + x] = current[y * width + x - 4] }
            for x in 0..<(width - 4) { continued[y * width + x] = current[y * width + x + 4] }
        }
        for count in [5, 8] {
            let solver = GroundCameraSolver()
            let broad = [CleanFloorLine(row: 40, xRange: 16...(16 + count * 16 - 1))]
            _ = solver.solve(pixels: original, width: width, height: height,
                lines: [CleanFloorLine(row: 40, xRange: 16...95)], timestamp: 0, exclusions: [])
            solver.followMeasuredCamera(correction: CGVector(dx: 4, dy: 0), pixels: recent,
                width: width, height: height, lines: broad, timestamp: 1.0 / 60, exclusions: [])
            let measured = try XCTUnwrap(solver.solve(pixels: current, width: width,
                height: height, lines: broad, timestamp: 2.0 / 60, exclusions: []))
            XCTAssertEqual(measured.imageTranslation, CGVector(dx: -4, dy: 0))
            let next = try XCTUnwrap(solver.solve(pixels: continued, width: width,
                height: height, lines: broad, timestamp: 3.0 / 60, exclusions: []))
            XCTAssertEqual(next.imageTranslation, CGVector(dx: -4, dy: 0),
                "An obsolete weak anchor must not undo the stronger current reference")
        }
    }

    func testRejectedFrameFallbackTracksProvisionallyUntilTrustedAnchorReturns() throws {
        let width = 160, height = 100
        let original = pixels()
        func distinctPixels(seed: UInt64) -> [UInt8] {
            (0..<(width * height)).map { index in
                let x = index % width, y = index / width
                var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663) ^ seed
                hash ^= hash >> 13
                hash &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: hash >> 11)
            }
        }
        var current = distinctPixels(seed: 99_887)
        for y in 40..<52 { for x in 24..<72 {
            let noise = (x + y).isMultiple(of: 2) ? 4 : -4
            current[y * width + x] = UInt8(clamping: Int(original[y * width + x - 8]) + noise)
        } }
        var fallback = distinctPixels(seed: 777)
        for y in 0..<height {
            for x in 4..<width { fallback[y * width + x] = current[y * width + x - 4] }
        }
        let solver = GroundCameraSolver()
        let lines = [CleanFloorLine(row: 40, xRange: 16...95)]
        _ = solver.solve(pixels: original, width: width, height: height,
            lines: lines, timestamp: 0, exclusions: [])
        XCTAssertNil(solver.solve(pixels: fallback, width: width, height: height,
            lines: [], timestamp: 1.0 / 60, exclusions: []))
        solver.followMeasuredCamera(correction: CGVector(dx: 4, dy: 0), pixels: fallback,
            width: width, height: height, lines: lines, timestamp: 1.0 / 60, exclusions: [])

        let provisional = try XCTUnwrap(solver.solve(pixels: current, width: width,
            height: height, lines: lines, timestamp: 2.0 / 60, exclusions: []))
        XCTAssertFalse(provisional.referenceTrusted)
        XCTAssertEqual(provisional.imageTranslation, CGVector(dx: -4, dy: 0))

        let recovered = try XCTUnwrap(solver.solve(pixels: original, width: width,
            height: height, lines: lines, timestamp: 3.0 / 60, exclusions: []))
        XCTAssertTrue(recovered.referenceTrusted)
        XCTAssertEqual(recovered.matchSource, "anchor")
    }

    func testSmallStaticDistractorCannotBeatSeveralMovingPlatforms() throws {
        let solver = GroundCameraSolver()
        let width = 160, height = 160
        let reference = pixels(width: width, height: height)
        let old = [CleanFloorLine(row: 20, xRange: 16...63),
                   CleanFloorLine(row: 80, xRange: 16...143),
                   CleanFloorLine(row: 120, xRange: 16...143)]
        _ = solver.solve(pixels: reference, width: width, height: height,
            lines: old, timestamp: 0, exclusions: [])
        var current = pixels(width: width, height: height, dy: 10)
        // Real ground changes brightness slightly; a screen-fixed distractor
        // is pixel-perfect but must not win using only its own small subset.
        for index in current.indices { current[index] = UInt8(clamping: Int(current[index]) + index % 3) }
        for y in 20..<32 { for x in 16...63 { current[y * width + x] = reference[y * width + x] } }
        let result = try XCTUnwrap(solver.solve(pixels: current, width: width, height: height,
            lines: [old[0], CleanFloorLine(row: 90, xRange: 16...143),
                    CleanFloorLine(row: 130, xRange: 16...143)],
            timestamp: 1.0 / 60, exclusions: []))
        XCTAssertEqual(result.imageTranslation, CGVector(dx: 0, dy: 10))
    }

    func testGlobalCorrectionReanchorsCurrentFrameWithoutNextFrameSnapBack() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        _ = solve(solver, dx: -4, dy: 0, row: 40, time: 1.0 / 60)
        solver.applyGlobalCorrection(
            CGVector(dx: 3, dy: 25),
            currentPixels: pixels(dx: -4, dy: 0),
            lines: [CleanFloorLine(row: 40, xRange: 16...143)],
            timestamp: 1.0 / 60,
            exclusions: []
        )
        let verified = try XCTUnwrap(solve(solver, dx: -4, dy: 0, row: 40, time: 2.0 / 60))
        XCTAssertEqual(verified.imageTranslation, .zero)
    }

    func testRepeatedCaptureThenHorizontalAccelerationRemainsInSearch() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        _ = solve(solver, dx: -4, dy: 0, row: 40, time: 1.0 / 60)
        _ = solve(solver, dx: -4, dy: 0, row: 40, time: 2.0 / 60)
        let accelerated = try XCTUnwrap(solve(solver, dx: -17, dy: 0, row: 40, time: 3.0 / 60))
        XCTAssertEqual(accelerated.imageTranslation, CGVector(dx: -13, dy: 0))
    }

    func testHorizontalKeyframeHandoffsReturnToOriginWithoutVerticalDrift() throws {
        let solver = GroundCameraSolver()
        var position = CGVector.zero
        let shifts = Array(stride(from: 0, through: -280, by: -4))
            + Array(stride(from: -276, through: 0, by: 4))
        for (index, dx) in shifts.enumerated() {
            let result = try XCTUnwrap(solver.solve(
                pixels: pixels(width: 640, height: 360, dx: dx), width: 640, height: 360,
                lines: [CleanFloorLine(row: 260, xRange: 16...623)],
                timestamp: Double(index) / 60, exclusions: []))
            position.dx += result.imageTranslation.dx
            position.dy += result.imageTranslation.dy
            XCTAssertEqual(position.dx, CGFloat(dx))
            XCTAssertEqual(position.dy, 0)
        }
        XCTAssertEqual(position, .zero)
    }

    func testVerticalKeyframeHandoffsFollowLongPanAndReturn() throws {
        let solver = GroundCameraSolver()
        var position = CGVector.zero
        let shifts = Array(stride(from: 0, through: 96, by: 4))
            + Array(stride(from: 92, through: 0, by: -4))
        for (index, dy) in shifts.enumerated() {
            let result = try XCTUnwrap(solver.solve(
                pixels: pixels(width: 320, height: 200, dy: dy), width: 320, height: 200,
                lines: [CleanFloorLine(row: 40 + dy, xRange: 16...303)],
                timestamp: Double(index) / 60, exclusions: []))
            position.dx += result.imageTranslation.dx
            position.dy += result.imageTranslation.dy
            XCTAssertEqual(position.dx, 0)
            XCTAssertEqual(position.dy, CGFloat(dy))
        }
        XCTAssertEqual(position, .zero)
    }

    private func pixels(width: Int = 160, height: Int = 100,
                        dx: Int = 0, dy: Int = 0) -> [UInt8] {
        (0..<(width * height)).map { index in
            let x = index % width - dx, y = index / width - dy
            guard x >= 0, x < width, y >= 0, y < height else { return 0 }
            var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663)
            hash ^= hash >> 13
            hash &*= 1_274_126_177
            return UInt8(truncatingIfNeeded: hash >> 11)
        }
    }

    private func solve(_ solver: GroundCameraSolver, dx: Int, dy: Int,
                       row: Int?, time: Double) -> GroundCameraSolver.Solution? {
        solver.solve(pixels: pixels(dx: dx, dy: dy), width: 160, height: 100,
            lines: row.map { [CleanFloorLine(row: $0, xRange: 16...143)] } ?? [],
            timestamp: time, exclusions: [])
    }

    func testOddHorizontalPhaseAndVerticalRoundTrip() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        let moved = try XCTUnwrap(solve(solver, dx: -3, dy: 5, row: 45, time: 1.0 / 60))
        XCTAssertEqual(moved.imageTranslation, CGVector(dx: -3, dy: 5))
        let returned = try XCTUnwrap(solve(solver, dx: 0, dy: 0, row: 40, time: 2.0 / 60))
        XCTAssertEqual(returned.imageTranslation, CGVector(dx: 3, dy: -5))
    }

    func testOnePixelDetectorJitterDoesNotMoveWorld() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        for frame in 1...8 {
            let result = try XCTUnwrap(solve(solver, dx: 0, dy: 0,
                row: frame.isMultiple(of: 2) ? 40 : 41, time: Double(frame) / 60))
            XCTAssertEqual(result.imageTranslation, .zero)
        }
    }

    func testBriefEdgeDropoutStillRequiresTextureVerification() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        let result = try XCTUnwrap(solve(solver, dx: -3, dy: 0, row: nil, time: 1.0 / 60))
        XCTAssertEqual(result.imageTranslation.dx, -3)
        XCTAssertNil(solver.solve(pixels: [UInt8](repeating: 90, count: 16_000),
            width: 160, height: 100, lines: [], timestamp: 2.0 / 60,
            exclusions: []))
    }

    func testFeatureRecoveryRefreshesFrozenLocalAnchor() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        _ = solve(solver, dx: -4, dy: 4, row: 44, time: 1.0 / 60)

        let recoveredPixels = pixels(dx: -14, dy: 14)
        solver.acceptFeatureRecovery(
            pixels: recoveredPixels,
            width: 160,
            height: 100,
            lines: [CleanFloorLine(row: 54, xRange: 16...143)],
            timestamp: 2.0 / 60,
            exclusions: [],
            imageTranslation: CGVector(dx: -10, dy: 10)
        )

        let continued = try XCTUnwrap(solve(
            solver, dx: -18, dy: 18, row: 58, time: 3.0 / 60
        ))
        XCTAssertEqual(continued.imageTranslation, CGVector(dx: -4, dy: 4))
    }

    func testThreePixelEdgeJitterKeepsStationaryTextureAtZero() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        for (index, row) in [42, 43, 38, 37, 40].enumerated() {
            let result = try XCTUnwrap(solve(solver, dx: 0, dy: 0, row: row,
                time: Double(index + 1) / 60))
            XCTAssertEqual(result.imageTranslation, .zero)
        }
    }

    func testRepeatedFrameThenFastVerticalPanAndReturn() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 1.0 / 60)
        let pan = try XCTUnwrap(solve(solver, dx: 0, dy: 18, row: 60, time: 2.0 / 60))
        XCTAssertEqual(pan.imageTranslation, CGVector(dx: 0, dy: 18))
        let back = try XCTUnwrap(solve(solver, dx: 0, dy: 0, row: 39, time: 3.0 / 60))
        XCTAssertEqual(back.imageTranslation, CGVector(dx: 0, dy: -18))
    }

    func testTwentyRoundTripsHaveNoAccumulatedDrift() throws {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        var position = CGVector.zero
        for frame in 1...40 {
            let y = frame.isMultiple(of: 2) ? 0 : 25
            let result = try XCTUnwrap(solve(solver, dx: 0, dy: y,
                row: 40 + y + (frame % 5 - 2), time: Double(frame) / 30))
            position.dx += result.imageTranslation.dx
            position.dy += result.imageTranslation.dy
            XCTAssertEqual(position.dx, 0)
            XCTAssertEqual(position.dy, CGFloat(y))
        }
    }

    func testPatchScoreHandlesBrightnessAndContrastButRejectsUnrelatedTexture() {
        let source = (0..<192).map { UInt8(40 + ($0 * 37 % 80)) }
        let brighter = source.map { UInt8(Int($0) * 3 / 2 + 10) }
        XCTAssertLessThan(GroundCameraSolver.patchError(source, brighter), 1)
        XCTAssertGreaterThan(GroundCameraSolver.patchError(source, Array(source.reversed())), 12)
        XCTAssertEqual(GroundCameraSolver.patchError(source, [UInt8](repeating: 80, count: 192)), .infinity)
    }

    func testWrongParallelRowCannotMoveCameraWithoutMatchingPixels() {
        let solver = GroundCameraSolver()
        _ = solve(solver, dx: 0, dy: 0, row: 40, time: 0)
        XCTAssertNil(solve(solver, dx: 0, dy: 0, row: 47, time: 1.0 / 60))
    }

    func testMatchingDistantPlatformCannotTeleportOneFrameCameraPose() {
        let solver = GroundCameraSolver()
        let width = 160, height = 120
        let reference = pixels(width: width, height: height)
        _ = solver.solve(
            pixels: reference, width: width, height: height,
            lines: [CleanFloorLine(row: 40, xRange: 16...143)],
            timestamp: 0, exclusions: []
        )
        var distantCopy = [UInt8](repeating: 0, count: width * height)
        for y in 0..<12 {
            for x in 0..<width {
                distantCopy[(80 + y) * width + x] = reference[(40 + y) * width + x]
            }
        }

        XCTAssertNil(solver.solve(
            pixels: distantCopy, width: width, height: height,
            lines: [CleanFloorLine(row: 80, xRange: 16...143)],
            timestamp: 1.0 / 60, exclusions: []
        ))
    }

    func testReleaseTimingAtCaptureResolution() throws {
        let solver = GroundCameraSolver()
        let frame = pixels(width: 640, height: 360)
        let lines = [CleanFloorLine(row: 260, xRange: 16...623)]
        var elapsed = [Double]()
        for index in 0..<20 {
            let start = ProcessInfo.processInfo.systemUptime
            let result = solver.solve(pixels: frame, width: 640, height: 360,
                lines: lines, timestamp: Double(index) / 60,
                exclusions: [])
            elapsed.append((ProcessInfo.processInfo.systemUptime - start) * 1_000)
            XCTAssertNotNil(result)
        }
        print("GROUND_SOLVER_640x360_MS median=\(elapsed.sorted()[10]) max=\(elapsed.max()!)")
    }
}
