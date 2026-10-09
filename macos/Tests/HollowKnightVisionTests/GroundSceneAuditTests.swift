import CoreGraphics
import ImageIO
import XCTest
@testable import HollowKnightVision

/// Optional read-only audit of original game-window screenshots. No fixture
/// paths or scene-specific thresholds enter the live tracking algorithm.
final class GroundSceneAuditTests: XCTestCase {
    func testRecordedReturnFrameRelocalizesFromDriftedPose() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let startPath = environment["HKV_GROUND_RETURN_START"],
              let returnPath = environment["HKV_GROUND_RETURN_END"] else {
            throw XCTSkip(
                "Set HKV_GROUND_RETURN_START and HKV_GROUND_RETURN_END to audit a recorded return"
            )
        }
        func load(_ path: String) throws -> CGImage {
            let url = URL(fileURLWithPath: path)
            let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
            return try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        }
        let exclusions = [
            CGRect(x: 284, y: 84, width: 26, height: 64),
            CGRect(x: 74, y: 313, width: 92, height: 26),
            CGRect(x: 68, y: 288, width: 60, height: 31),
            CGRect(x: 28, y: 289, width: 76, height: 60),
        ]
        func lines(_ image: CGImage) throws -> (GroundEdgeAnalysis, [CleanFloorLine]) {
            let edge = try XCTUnwrap(GroundLineDetector.analyze(
                image,
                excluding: exclusions
            ))
            let comparison = GroundLineDetector.compare(edge)
            return (
                edge,
                GroundLineDetector.cleanFloorLines(from: comparison, tuning: .default)
            )
        }

        let start = try load(startPath)
        let returned = try load(returnPath)
        let (startEdge, startLines) = try lines(start)
        let (returnEdge, returnLines) = try lines(returned)
        let startDescription = startLines.map { "\($0.row):\($0.xRange)" }
        let returnDescription = returnLines.map { "\($0.row):\($0.xRange)" }
        print("GROUND_RETURN start=\(startDescription)")
        print("GROUND_RETURN end=\(returnDescription)")

        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let trackerExclusions = exclusions.map {
            CGRect(
                x: $0.minX,
                y: CGFloat(start.height) - $0.maxY,
                width: $0.width,
                height: $0.height
            )
        }
        var seeded = GroundHypothesisTrackingResult.empty
        for index in 0..<8 {
            seeded = tracker.update(
                frame: start,
                floorLines: startLines,
                timestamp: Double(index) / 60,
                sourceLuma: startEdge.sourceLuma,
                protectedOcclusions: trackerExclusions
            )
        }
        XCTAssertGreaterThan(seeded.globalFeatureCount, 0)
        tracker.reanchorLocalCamera(to: CGPoint(x: 276, y: 83))

        var result = GroundHypothesisTrackingResult.empty
        for index in 0..<12 {
            result = tracker.update(
                frame: returned,
                floorLines: returnLines,
                timestamp: 1 + Double(index) * 0.8,
                sourceLuma: returnEdge.sourceLuma,
                protectedOcclusions: trackerExclusions
            )
            let pose = result.cameraPosition.map { "\($0.x),\($0.y)" } ?? "nil"
            print(
                "GROUND_RETURN step=\(index) pose=\(pose) verified=\(result.poseVerified) "
                    + "matches=\(result.globalMatchCount) correction=\(String(describing: result.globalCorrection))"
            )
        }
        let pose = try XCTUnwrap(result.cameraPosition)
        XCTAssertLessThan(hypot(pose.x, pose.y), 32)
    }

    func testLabeledGameplayGroundDetectionCostAndEvidence() throws {
        guard let directory = ProcessInfo.processInfo.environment[
            "HKV_LABELING_EXAMPLES_DIRECTORY"
        ] else {
            throw XCTSkip("Set HKV_LABELING_EXAMPLES_DIRECTORY to audit labeled Gameplay frames")
        }
        let root = URL(fileURLWithPath: directory, isDirectory: true)
        let entries = try FileManager.default.contentsOfDirectory(
            at: root,
            includingPropertiesForKeys: nil
        )
        var milliseconds = [Double]()
        var totalEvidence = 0
        var frameCount = 0
        let auditThresholds = [4, 6, 8, 10, 12, 16, 20, 24, 30, 40, 60]
        var thresholdFrameCounts = [Int: Int]()
        var thresholdLineCounts = [Int: Int]()
        var thresholdEvidenceCounts = [Int: Int]()
        var framePeakResponses = [Int]()

        for entry in entries.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            let metadataURL = entry.appendingPathComponent("example.json")
            let imageURL = entry.appendingPathComponent("image.png")
            guard let metadata = try? String(contentsOf: metadataURL, encoding: .utf8),
                  metadata.contains("\"contextIdentifier\" : \"game\""),
                  let source = CGImageSourceCreateWithURL(imageURL as CFURL, nil),
                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil)
            else { continue }

            let started = ProcessInfo.processInfo.systemUptime
            let analysis = try XCTUnwrap(GroundLineDetector.analyze(image))
            let comparison = GroundLineDetector.compare(analysis)
            let lines = GroundLineDetector.cleanFloorLines(
                from: comparison,
                tuning: .default
            )
            milliseconds.append(
                (ProcessInfo.processInfo.systemUptime - started) * 1_000
            )

            func evidence(_ lines: [CleanFloorLine]) -> Int {
                lines.reduce(0) { total, line in
                    total + line.evidenceRanges.reduce(0) {
                        $0 + $1.upperBound - $1.lowerBound + 1
                    }
                }
            }
            totalEvidence += evidence(lines)
            framePeakResponses.append(Int(comparison.groundPixels.max() ?? 0))
            for threshold in auditThresholds {
                let candidateLines = GroundLineDetector.cleanFloorLines(
                    from: comparison,
                    tuning: GroundTheoryTuning(
                        groundThreshold: threshold,
                        minimumSegmentLength: 30,
                        lineSeparation: 40,
                        occlusionMergeGap: 300
                    )
                )
                if !candidateLines.isEmpty {
                    thresholdFrameCounts[threshold, default: 0] += 1
                }
                thresholdLineCounts[threshold, default: 0] += candidateLines.count
                thresholdEvidenceCounts[threshold, default: 0] += evidence(candidateLines)
            }
            frameCount += 1
            let rows = lines.map { "\($0.row):\($0.xRange)" }
            print(
                "GROUND_FRAME \(entry.lastPathComponent) rows=\(rows)"
            )
        }

        XCTAssertGreaterThan(frameCount, 0)
        func median(_ values: [Double]) -> Double {
            values.sorted()[values.count / 2]
        }
        print(
            "GROUND_AUDIT frames=\(frameCount) "
                + "medianMS=\(median(milliseconds)) evidence=\(totalEvidence)"
        )
        print("GROUND_PEAKS \(framePeakResponses.sorted())")
        for threshold in auditThresholds {
            print(
                "GROUND_THRESHOLD value=\(threshold) "
                    + "frames=\(thresholdFrameCounts[threshold, default: 0]) "
                    + "lines=\(thresholdLineCounts[threshold, default: 0]) "
                    + "evidence=\(thresholdEvidenceCounts[threshold, default: 0])"
            )
        }
    }

    func testCapturedPlatformScene() throws {
        guard let directory = ProcessInfo.processInfo.environment["HKV_SCENE_AUDIT_DIRECTORY"] else {
            throw XCTSkip("Set HKV_SCENE_AUDIT_DIRECTORY to audit a captured scene")
        }
        for name in ["baseline-game.png", "01-held-game.png", "01-returned-game.png"] {
            let url = URL(fileURLWithPath: directory).appendingPathComponent(name)
            let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
            let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
            let content = try XCTUnwrap(image.cropping(to: CGRect(x: 0, y: 56,
                width: image.width, height: image.height - 56)))
            let canvas = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360,
                bitsPerComponent: 8, bytesPerRow: 640 * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            canvas.setFillColor(CGColor(gray: 0, alpha: 1))
            canvas.fill(CGRect(x: 0, y: 0, width: 640, height: 360))
            let scaledHeight = CGFloat(content.height) * 640 / CGFloat(content.width)
            canvas.draw(content, in: CGRect(x: 0, y: 360 - scaledHeight,
                width: 640, height: scaledHeight))
            let frame = try XCTUnwrap(canvas.makeImage())
            // Independently known HUD bounds for the audit only. Compare with
            // no mask to expose normalization and foreground sensitivity.
            for masked in [false, true] {
                let edges = try XCTUnwrap(GroundLineDetector.analyze(
                    frame,
                    excluding: masked
                        ? [CGRect(x: 40, y: 290, width: 130, height: 65)] : []
                ))
                let comparison = GroundLineDetector.compare(edges)
                let lines = GroundLineDetector.cleanFloorLines(from: comparison, tuning: .default)
                print("SCENE_AUDIT \(name) masked=\(masked) lines=\(lines.map { "\($0.row):\($0.xRange)" }.joined(separator: ","))")
                let maxima = (0..<360).map { row in
                    comparison.groundPixels[(row * 640)..<((row + 1) * 640)].max() ?? 0
                }
                print("SCENE_ROWS \(name) masked=\(masked) \(maxima.enumerated().filter { $0.element >= 60 }.map { "\($0.offset):\($0.element)" }.joined(separator: ","))")
            }
        }
    }
}
