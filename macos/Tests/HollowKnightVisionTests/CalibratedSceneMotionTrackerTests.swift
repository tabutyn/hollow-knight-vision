import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class CalibratedSceneMotionTrackerTests: XCTestCase {
    func testPatchFitRejectsBlackRepeatedTextureMasksAndSearchBoundary() throws {
        let reference = grid()
        let current = grid(dx: 2, dy: -3)
        let fit = try XCTUnwrap(CalibratedSceneMotionTracker.fit(reference, current, point: CGPoint(x: 60, y: 50)))
        XCTAssertEqual(fit.motion.dx, 2, accuracy: 0.01)
        XCTAssertEqual(fit.motion.dy, -3, accuracy: 0.01)
        XCTAssertNil(CalibratedSceneMotionTracker.fit(reference, grid(dx: 8), point: CGPoint(x: 60, y: 50)))
        let black = CalibratedSceneMotionTracker.Grid(width: 160, height: 90,
            pixels: Array(repeating: 0, count: 160 * 90), exclusions: [])
        XCTAssertNil(CalibratedSceneMotionTracker.fit(black, black, point: CGPoint(x: 60, y: 50)))
        var periodicPixels = [Double]()
        for index in 0..<(160 * 90) {
            let x = (index % 160) % 4
            let y = (index / 160) % 4
            periodicPixels.append(Double(x * 40 + y * 20))
        }
        let periodic = CalibratedSceneMotionTracker.Grid(width: 160, height: 90,
            pixels: periodicPixels, exclusions: [])
        XCTAssertNil(CalibratedSceneMotionTracker.fit(periodic, periodic, point: CGPoint(x: 60, y: 50)))
        let masked = CalibratedSceneMotionTracker.Grid(width: 160, height: 90,
            pixels: current.pixels, exclusions: [CGRect(x: 50, y: 35, width: 30, height: 30)])
        XCTAssertNil(CalibratedSceneMotionTracker.fit(reference, masked, point: CGPoint(x: 60, y: 50)))
    }

    func testGroundCalibratedMotionTracksExposureChangeAndDoesNotBridgeMissingCaptures() throws {
        let tracker = CalibratedSceneMotionTracker()
        XCTAssertNil(tracker.observe(grid: grid(), timestamp: 0, solveWidth: 640, ground: nil))
        XCTAssertNil(tracker.observe(grid: grid(dx: 2, dy: -3), timestamp: 0.05, solveWidth: 640,
            ground: .init(position: CGPoint(x: -8, y: -12), referenceTimestamp: 0,
                          displacement: CGVector(dx: 8, dy: -12))))
        let measurement = try XCTUnwrap(tracker.observe(grid: grid(dx: 3, dy: -5, exposure: 0.65),
            timestamp: 0.10, solveWidth: 640, ground: nil))
        XCTAssertEqual(measurement.position.x, -12, accuracy: 0.1)
        XCTAssertEqual(measurement.position.y, -20, accuracy: 0.1)
        XCTAssertTrue(measurement.horizontalMeasured)
        XCTAssertTrue(measurement.verticalMeasured)
        XCTAssertGreaterThanOrEqual(measurement.supportedPatches, 3)
        XCTAssertEqual(measurement.referenceTimestamp, 0.05)
        XCTAssertNil(tracker.observe(grid: grid(dx: 4, dy: -7), timestamp: 0.5, solveWidth: 640, ground: nil))
        XCTAssertNil(tracker.observe(grid: grid(dx: 5, dy: -9), timestamp: 0.55, solveWidth: 640, ground: nil))
    }

    func testMovingBlackOccluderLeavesDistributedVisibleSceneryTracking() throws {
        let tracker = CalibratedSceneMotionTracker()
        _ = tracker.observe(grid: grid(), timestamp: 0, solveWidth: 640, ground: nil)
        _ = tracker.observe(grid: grid(dx: 2, dy: -3), timestamp: 0.05, solveWidth: 640,
            ground: .init(position: CGPoint(x: -8, y: -12), referenceTimestamp: 0,
                          displacement: CGVector(dx: 8, dy: -12)))
        let current = grid(dx: 3, dy: -5)
        var occluded = current.pixels
        for y in 0..<90 { for x in 40..<120 { occluded[y * 160 + x] = 0 } }
        let measurement = try XCTUnwrap(tracker.observe(grid: .init(width: 160, height: 90,
            pixels: occluded, exclusions: []), timestamp: 0.1, solveWidth: 640, ground: nil))
        XCTAssertEqual(measurement.position.x, -12, accuracy: 0.1)
        XCTAssertEqual(measurement.position.y, -20, accuracy: 0.1)
        XCTAssertGreaterThanOrEqual(measurement.supportedPatches, 3)
        XCTAssertNil(tracker.observe(grid: .init(width: 160, height: 90,
            pixels: Array(repeating: 0, count: 160 * 90), exclusions: []),
            timestamp: 0.15, solveWidth: 640, ground: nil))
    }

    func testHorizontalCalibrationCannotClaimMeasuredVerticalMotion() throws {
        let tracker = CalibratedSceneMotionTracker()
        _ = tracker.observe(grid: grid(), timestamp: 0, solveWidth: 640, ground: nil)
        _ = tracker.observe(grid: grid(dx: 2), timestamp: 0.05, solveWidth: 640,
            ground: .init(position: CGPoint(x: -8, y: 0), referenceTimestamp: 0,
                          displacement: CGVector(dx: 8, dy: 0)))
        let result = try XCTUnwrap(tracker.observe(grid: grid(dx: 3, dy: -2), timestamp: 0.1,
            solveWidth: 640, ground: nil))
        XCTAssertTrue(result.horizontalMeasured)
        XCTAssertFalse(result.verticalMeasured)
    }

    func testPatchReplenishmentCannotInventAnUncalibratedAxis() throws {
        // New texture enters the frame while old ground-calibrated patches
        // leave. Repeated image motion alone cannot calibrate the missing axis.
        for calibratedX in [true, false] {
            let tracker = CalibratedSceneMotionTracker()
            let firstX = calibratedX ? 2 : 0
            let firstY = calibratedX ? 0 : -3
            _ = tracker.observe(grid: grid(), timestamp: 0, solveWidth: 640, ground: nil)
            _ = tracker.observe(grid: grid(dx: firstX, dy: firstY), timestamp: 0.05,
                solveWidth: 640, ground: .init(
                    position: CGPoint(x: -firstX * 4, y: firstY * 4), referenceTimestamp: 0,
                    displacement: CGVector(dx: firstX * 4, dy: firstY * 4)))
            var measurements = 0
            for step in 1...24 {
                if let result = tracker.observe(
                    grid: grid(dx: firstX + step * 2, dy: firstY - step),
                    timestamp: 0.05 + Double(step) * 0.05, solveWidth: 640, ground: nil) {
                    measurements += 1
                    if calibratedX {
                        XCTAssertFalse(result.verticalMeasured, "Missing Y calibration at step \(step)")
                    } else {
                        XCTAssertFalse(result.horizontalMeasured, "Missing X calibration at step \(step)")
                    }
                }
            }
            XCTAssertGreaterThan(measurements, 12, "Must exercise replacement patch generations")
        }
    }

    func testUncalibratedReplacementPatchesCannotOutvoteKnownAxisScale() throws {
        let tracker = CalibratedSceneMotionTracker()
        _ = tracker.observe(grid: grid(), timestamp: 0, solveWidth: 640, ground: nil)
        // This scenery moves 1.2 image pixels per camera pixel in X.
        // Integer image shifts keep the test independent of subpixel fitting.
        _ = tracker.observe(grid: grid(dx: 3, dy: -3), timestamp: 0.05,
            solveWidth: 640, ground: .init(position: CGPoint(x: -10, y: -12),
                referenceTimestamp: 0, displacement: CGVector(dx: 10, dy: -12)))
        var previous: CalibratedSceneMotionTracker.Measurement?
        for step in 1...22 {
            previous = try XCTUnwrap(tracker.observe(grid: grid(dx: 3, dy: -3 - step * 2),
                timestamp: 0.05 + Double(step) * 0.05, solveWidth: 640, ground: nil))
        }
        // Replacements saw only vertical movement. Their X scale is unknown,
        // even though they now outnumber surviving X-calibrated patches.
        let before = try XCTUnwrap(previous)
        let after = try XCTUnwrap(tracker.observe(grid: grid(dx: 6, dy: -47),
            timestamp: 1.2, solveWidth: 640, ground: nil))
        XCTAssertTrue(after.horizontalMeasured)
        XCTAssertEqual(after.position.x - before.position.x, -10, accuracy: 0.1)
        XCTAssertEqual(after.position.y, before.position.y, accuracy: 0.1)
    }

    func testMetalAndCPUAgreeForContrastMasksAndAmbiguity() throws {
        guard ScenePatchMetalCorrelator.shared.available else { throw XCTSkip("Metal unavailable") }
        let points = [CGPoint(x: 20, y: 20), CGPoint(x: 60, y: 50), CGPoint(x: 120, y: 70)]
        let a = grid()
        let base = grid(dx: 3, dy: -4, exposure: 0.7)
        let b = CalibratedSceneMotionTracker.Grid(width: base.width, height: base.height,
            pixels: base.pixels, exclusions: [CGRect(x: 0, y: 0, width: 40, height: 40)])
        for expected: CGVector? in [nil, CGVector(dx: 3, dy: -4)] {
            XCTAssertNotNil(ScenePatchMetalCorrelator.shared.scores(a, b, points: points, near: expected))
            let cpu = CalibratedSceneMotionTracker.fitBatch(a, b, points: points, near: expected, backend: .cpu)
            let gpu = CalibratedSceneMotionTracker.fitBatch(a, b, points: points, near: expected, backend: .metal)
            for (first, second) in zip(cpu, gpu) {
                XCTAssertEqual(first == nil, second == nil)
                if let first, let second {
                    XCTAssertEqual(first.motion.dx, second.motion.dx, accuracy: 0.001)
                    XCTAssertEqual(first.motion.dy, second.motion.dy, accuracy: 0.001)
                    XCTAssertEqual(first.correlation, second.correlation, accuracy: 0.001)
                }
            }
        }
        let black = CalibratedSceneMotionTracker.Grid(width: 160, height: 90,
            pixels: Array(repeating: 0, count: 14400), exclusions: [])
        XCTAssertTrue(CalibratedSceneMotionTracker.fitBatch(black, black, points: points, backend: .metal).allSatisfy { $0 == nil })
    }

    /// Fixed visual experiment. This audit never decodes Hacker telemetry. It
    /// freezes previous production ground solves to compare the new local matcher;
    /// a full production replay remains necessary before any delivery claim.
    func testArchivedFallWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_CALIBRATED_SCENE_AUDIT_INPUT"],
              let tracking = env["HKV_CALIBRATED_SCENE_AUDIT_TRACKING"],
              let output = env["HKV_CALIBRATED_SCENE_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set HKV_CALIBRATED_SCENE_AUDIT_INPUT/TRACKING/OUTPUT for native pixel audit")
        }
        struct Source: Decodable { let id: UUID; let visualFrameArchive: RecordedVisualFrameArchive }
        struct GroundSample: Decodable {
            let captureTimestamp: Double
            let poseSource: String?
            let publishedCameraX: Double?
            let publishedCameraY: Double?
            let localReferenceCaptureTimestamp: Double?
            let localMatchImageDX: Double?
            let localMatchImageDY: Double?
        }
        struct Samples: Decodable { let trackingSamples: [GroundSample] }
        let path = URL(fileURLWithPath: input)
        let source = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
        let samples = try JSONDecoder().decode(Samples.self, from: Data(contentsOf: URL(fileURLWithPath: tracking)))
        let byTime = Dictionary(samples.trackingSamples.map { ($0.captureTimestamp, $0) }, uniquingKeysWith: { _, last in last })
        let reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id)
        let start = try XCTUnwrap(reader.manifest.frames.first?.captureTimestamp)
        let masks = Dictionary(reader.manifest.groundInputs.map { ($0.captureTimestamp, $0.excludingRects) },
                               uniquingKeysWith: { _, last in last })
        let tracker = CalibratedSceneMotionTracker()
        var rows = [[String: Any]](), enteredFloorless = false
        let began = ProcessInfo.processInfo.systemUptime
        for frame in reader.manifest.frames where (156.5...158.61).contains(frame.captureTimestamp - start) {
            try autoreleasepool {
                guard let sample = byTime[frame.captureTimestamp] else { return }
                let image = try reader.image(for: frame)
                let pixels = try XCTUnwrap(GroundPixels.luma(image, width: image.width, height: image.height))
                let grid = try XCTUnwrap(CalibratedSceneMotionTracker.grid(pixels: pixels, width: image.width, height: image.height,
                    excluding: env["HKV_CALIBRATED_SCENE_AUDIT_MASKS"] == "1" ? masks[frame.captureTimestamp] ?? [] : []))
                var calibration: CalibratedSceneMotionTracker.GroundCalibration?
                if sample.poseSource != "ground" { enteredFloorless = true }
                if !enteredFloorless, let x = sample.publishedCameraX, let y = sample.publishedCameraY,
                   let ref = sample.localReferenceCaptureTimestamp,
                   let dx = sample.localMatchImageDX, let dy = sample.localMatchImageDY {
                    calibration = .init(position: CGPoint(x: x, y: y), referenceTimestamp: ref,
                                        displacement: CGVector(dx: dx, dy: dy))
                }
                if let result = tracker.observe(grid: grid, timestamp: frame.captureTimestamp,
                    solveWidth: 640, ground: calibration) {
                    rows.append(["offset": frame.captureTimestamp - start,
                        "pose": [result.position.x, result.position.y], "patches": result.supportedPatches,
                        "measuredX": result.horizontalMeasured, "measuredY": result.verticalMeasured,
                        "residual": [result.residualX, result.residualY], "ground": calibration != nil])
                }
            }
        }
        try JSONSerialization.data(withJSONObject: rows, options: [.prettyPrinted, .sortedKeys])
            .write(to: URL(fileURLWithPath: output), options: .atomic)
        XCTAssertFalse(rows.isEmpty)
        print("Native calibrated scene audit: \(rows.count) estimates, \(ProcessInfo.processInfo.systemUptime - began)s")
    }

    private func grid(dx: Int = 0, dy: Int = 0, exposure: Double = 1) -> CalibratedSceneMotionTracker.Grid {
        let pixels = (0..<90).flatMap { y in (0..<160).map { x -> Double in
            var value = UInt64(x - dx + 1000) &* 0x9e3779b185ebca87
            value ^= UInt64(y - dy + 1000) &* 0xc2b2ae3d27d4eb4f
            value ^= value >> 33; value = value &* 0xff51afd7ed558ccd
            return Double(UInt8(truncatingIfNeeded: value >> 24)) * exposure
        } }
        return .init(width: 160, height: 90, pixels: pixels, exclusions: [])
    }
}
