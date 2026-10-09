import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class LowResolutionTraceEvaluatorTests: XCTestCase {
    func testEvaluatesExactFrameMotionAndPlaceRecognition() throws {
        let session = UUID()
        let base = fractionalMotionGrid(horizontalShift: 0)
        let shifted = fractionalMotionGrid(horizontalShift: 1, gain: 0.4)
        let path = RecordedInputPath(
            id: UUID(),
            createdAt: Date(timeIntervalSince1970: 1_700_000_000),
            duration: 1,
            events: [],
            trackingSamples: [],
            groundTruthSamples: [
                truth(frame: 1, cameraX: 0, session: session, offset: 0),
                truth(frame: 2, cameraX: -10, session: session, offset: 0.05),
                truth(frame: 3, cameraX: -10, session: session, offset: 0.25),
            ],
            lowResolutionFrames: [
                frame(base, unityFrame: 1, timestamp: 100, reliable: true),
                frame(shifted, unityFrame: 2, timestamp: 100.05, reliable: false),
                frame(shifted, unityFrame: 3, timestamp: 100.25, reliable: false),
            ],
            runtimeMetadata: ["captureWidth": "640"]
        )

        let report = LowResolutionTraceEvaluator.evaluate(path)

        XCTAssertEqual(report.traceFrames, 3)
        XCTAssertEqual(report.validTraceFrames, 3)
        XCTAssertEqual(report.exactTruthFrames, 3)
        XCTAssertEqual(report.groundReliableFrames, 1)
        XCTAssertEqual(report.groundUnverifiedFrames, 2)
        XCTAssertEqual(
            report.motionMinimumImprovement,
            LowResolutionRoomMotionTracker.minimumImprovement
        )
        XCTAssertEqual(report.traceIntervalSeconds.count, 2)
        XCTAssertEqual(report.allMotion.comparisons, 1)
        XCTAssertEqual(report.allMotion.representableComparisons, 1)
        XCTAssertEqual(report.allMotion.acceptedRepresentableComparisons, 1)
        XCTAssertEqual(report.allMotion.representableCoveragePercent ?? .nan, 100)
        XCTAssertEqual(report.groundLossMotion.comparisons, 1)
        XCTAssertLessThan(report.groundLossMotion.vectorErrorPixels.maximum ?? .infinity, 3)
        XCTAssertLessThan(report.groundLossMotion.integratedDriftPixels.maximum ?? .infinity, 3)
        XCTAssertEqual(report.oracleSeededPlaceRecognition.matches, 1)
        XCTAssertEqual(report.oracleSeededPlaceRecognition.matchesOver24Pixels, 0)
        XCTAssertLessThan(
            report.oracleSeededPlaceRecognition.errorPixels.maximum ?? .infinity,
            3
        )
        XCTAssertTrue(report.evaluable)
        XCTAssertEqual(report.exactTruthCoveragePercent ?? .nan, 100)
    }

    func testClassifiesCameraWarpOutsideSearchAndDoesNotCrossSceneEpoch() {
        let session = UUID()
        let grid = fractionalMotionGrid(horizontalShift: 0)
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [], trackingSamples: [],
            groundTruthSamples: [
                truth(frame: 1, cameraX: 0, session: session, offset: 0),
                truth(frame: 2, cameraX: 1_000, session: session, offset: 0.05),
                truth(
                    frame: 3, cameraX: 0, session: session,
                    sceneName: "Town", offset: 0.10
                ),
            ],
            lowResolutionFrames: [
                frame(grid, unityFrame: 1, timestamp: 100, reliable: true),
                frame(grid, unityFrame: 2, timestamp: 100.05, reliable: false),
                frame(grid, unityFrame: 3, timestamp: 100.10, reliable: false),
            ]
        )

        let report = LowResolutionTraceEvaluator.evaluate(path)

        XCTAssertEqual(report.allMotion.comparisons, 1)
        XCTAssertEqual(report.allMotion.outOfRangeComparisons, 1)
        XCTAssertEqual(report.allMotion.acceptedOutOfRangeComparisons, 0)
        XCTAssertEqual(report.groundLossMotion.outOfRangeComparisons, 1)
    }

    func testEvaluatorAppliesProductionDirectionGate() {
        let session = UUID()
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [RecordedInputPathEvent(
                offset: 0,
                button: .right,
                transition: .pressed
            )],
            trackingSamples: [],
            groundTruthSamples: [
                truth(frame: 1, cameraX: 0, session: session, offset: 0),
                truth(frame: 2, cameraX: -20, session: session, offset: 0.05),
            ],
            lowResolutionFrames: [
                frame(
                    fractionalMotionGrid(horizontalShift: 0),
                    unityFrame: 1,
                    timestamp: 100,
                    reliable: true
                ),
                frame(
                    fractionalMotionGrid(horizontalShift: 2),
                    unityFrame: 2,
                    timestamp: 100.05,
                    reliable: false
                ),
            ]
        )

        let report = LowResolutionTraceEvaluator.evaluate(path)

        XCTAssertEqual(report.groundLossMotion.comparisons, 1)
        XCTAssertEqual(report.groundLossMotion.directionGateRejections, 1)
        XCTAssertEqual(report.groundLossMotion.acceptedComparisons, 0)
        XCTAssertEqual(report.groundLossMotion.rejectedRepresentableComparisons, 1)
    }

    func testEvaluatorRecordsExperimentalMotionThreshold() {
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [], trackingSamples: []
        )

        let report = LowResolutionTraceEvaluator.evaluate(
            path,
            minimumImprovement: 0.025
        )

        XCTAssertEqual(report.motionMinimumImprovement, 0.025)
    }

    func testRejectedMotionReportsHiddenHackerTravelByCause() {
        let session = UUID()
        let texturelessGrid = LowResolutionMotionGrid(
            width: 64,
            height: 36,
            luma: [UInt8](repeating: 80, count: 64 * 36)
        )
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [], trackingSamples: [],
            groundTruthSamples: [
                truth(frame: 1, cameraX: 0, session: session, offset: 0),
                truth(frame: 2, cameraX: 3, session: session, offset: 0.05),
            ],
            lowResolutionFrames: [
                frame(texturelessGrid, unityFrame: 1, timestamp: 100, reliable: true),
                frame(texturelessGrid, unityFrame: 2, timestamp: 100.05, reliable: false),
            ]
        )

        let report = LowResolutionTraceEvaluator.evaluate(path)
        let hidden = report.groundLossMotion.rejectedTravelByReason[
            LowResolutionTranslationRejection.insufficientTexture.rawValue
        ]

        XCTAssertEqual(hidden?.comparisons, 1)
        XCTAssertEqual(hidden?.signedExpectedX ?? .nan, 3, accuracy: 0.001)
        XCTAssertEqual(hidden?.absoluteExpectedX ?? .nan, 3, accuracy: 0.001)
        XCTAssertEqual(hidden?.expectedDistancePixels.maximum ?? .nan, 3, accuracy: 0.001)
    }

    func testConnectedCameraKeepsOneBaselineAcrossSceneJoin() {
        let session = UUID()
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [], trackingSamples: [],
            groundTruthSamples: [
                truth(
                    frame: 1, cameraX: 0, session: session,
                    heroScreenX: 600, offset: 0
                ),
                truth(
                    frame: 2, cameraX: 10, session: session,
                    heroScreenX: 600, offset: 0.05
                ),
                truth(
                    frame: 3, cameraX: 1_000, session: session,
                    sceneName: "Town", heroScreenX: 40, offset: 0.10
                ),
            ],
            coarseMotionSamples: [
                coarse(frame: 1, offset: 0, x: 50),
                coarse(frame: 2, offset: 0.05, x: 60),
                coarse(frame: 3, offset: 0.10, x: 630),
            ]
        )

        let report = LowResolutionTraceEvaluator.evaluate(path)

        XCTAssertEqual(report.connectedCamera.source, "capture-rate-coarse")
        XCTAssertEqual(report.connectedCamera.exactFramePairs, 3)
        XCTAssertEqual(report.connectedCamera.sceneNames, ["Town", "Tutorial_01"])
        XCTAssertEqual(report.connectedCamera.joinedSceneErrorPixels.count, 1)
        XCTAssertEqual(report.connectedCamera.endErrorX ?? .nan, 10, accuracy: 0.001)
        XCTAssertEqual(report.connectedCamera.endErrorY ?? .nan, 0, accuracy: 0.001)
        XCTAssertEqual(report.connectedTrackingCamera.source, "published-tracking")
        XCTAssertEqual(report.connectedTrackingCamera.exactFramePairs, 0)
    }

    func testConnectedCameraUsesHistoricalPublishedTrackingWhenCoarseTraceIsAbsent() {
        let session = UUID()
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [],
            trackingSamples: [
                tracking(frame: 1, offset: 0, x: 50),
                tracking(frame: 2, offset: 0.05, x: 60),
                tracking(frame: 3, offset: 0.10, x: 630),
            ],
            groundTruthSamples: [
                truth(
                    frame: 1, cameraX: 0, session: session,
                    heroScreenX: 600, offset: 0
                ),
                truth(
                    frame: 2, cameraX: 10, session: session,
                    heroScreenX: 600, offset: 0.05
                ),
                truth(
                    frame: 3, cameraX: 1_000, session: session,
                    sceneName: "Town", heroScreenX: 40, offset: 0.10
                ),
            ]
        )

        let report = LowResolutionTraceEvaluator.evaluate(path)

        XCTAssertEqual(report.connectedCamera.source, "published-tracking")
        XCTAssertEqual(report.connectedCamera.exactFramePairs, 3)
        XCTAssertEqual(report.connectedCamera.sceneNames, ["Town", "Tutorial_01"])
        XCTAssertEqual(report.connectedCamera.joinedSceneErrorPixels.count, 1)
        XCTAssertEqual(report.connectedCamera.endErrorX ?? .nan, 10, accuracy: 0.001)
        XCTAssertEqual(report.connectedCamera.endErrorY ?? .nan, 0, accuracy: 0.001)
        XCTAssertEqual(report.connectedTrackingCamera, report.connectedCamera)
    }

    func testInvalidPayloadIsExcludedWithoutLosingTraceCount() {
        let session = UUID()
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [], trackingSamples: [],
            groundTruthSamples: [
                truth(frame: 1, cameraX: 0, session: session, offset: 0),
            ],
            lowResolutionFrames: [RecordedLowResolutionFrame(
                offset: 0,
                captureTimestamp: 100,
                renderedGameFrame: 1,
                roomID: 0,
                width: 64,
                height: 36,
                luma: Data([1, 2, 3]),
                groundTrackingReliable: true
            )]
        )

        let report = LowResolutionTraceEvaluator.evaluate(path)

        XCTAssertEqual(report.traceFrames, 1)
        XCTAssertEqual(report.validTraceFrames, 0)
        XCTAssertEqual(report.exactTruthFrames, 0)
    }

    func testReconstructsTraceFromPersistedWorldEvidenceWindow() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(
            UUID().uuidString,
            isDirectory: true
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        let roomID = UUID()
        let visitID = UUID()
        let createdAt = Date(timeIntervalSinceReferenceDate: 1_000)
        let firstGrid = fractionalMotionGrid(horizontalShift: 0)
        let secondGrid = fractionalMotionGrid(horizontalShift: 1)
        _ = try store.append(
            frame: image(firstGrid, renderedFrame: 10),
            roomID: roomID, visitID: visitID,
            timestamp: 1_000.5, cameraPosition: .zero,
            solveWidth: 640, excluding: []
        )
        _ = try store.append(
            frame: image(secondGrid, renderedFrame: 11),
            roomID: roomID, visitID: visitID,
            timestamp: 1_000.7, cameraPosition: CGPoint(x: 10, y: 0),
            solveWidth: 640, excluding: []
        )
        _ = try store.append(
            frame: image(secondGrid, renderedFrame: 12),
            roomID: roomID, visitID: visitID,
            timestamp: 1_003, cameraPosition: CGPoint(x: 20, y: 0),
            solveWidth: 640, excluding: []
        )
        let path = RecordedInputPath(
            id: UUID(), createdAt: createdAt, duration: 1,
            events: [],
            trackingSamples: [
                tracking(frame: 10, offset: 0, x: 0),
                tracking(frame: 11, offset: 0.2, x: 10),
            ]
        )

        let frames = try LowResolutionTraceEvaluator.persistedWorldFrames(
            for: path,
            worldRootURL: root
        )

        XCTAssertEqual(frames.count, 2)
        XCTAssertEqual(frames.map(\.renderedGameFrame), [10, 11])
        XCTAssertEqual(frames.map(\.roomID), [0, 0])
        XCTAssertEqual(frames[0].offset, 0, accuracy: 0.001)
        XCTAssertEqual(frames[1].offset, 0.2, accuracy: 0.001)
        XCTAssertTrue(frames.allSatisfy { $0.width == 64 && $0.height == 36 })
        XCTAssertTrue(frames.allSatisfy(\.groundTrackingReliable))
    }

    private func frame(
        _ grid: LowResolutionMotionGrid,
        unityFrame: Int,
        timestamp: TimeInterval,
        reliable: Bool
    ) -> RecordedLowResolutionFrame {
        RecordedLowResolutionFrame(
            offset: timestamp - 100,
            captureTimestamp: timestamp,
            renderedGameFrame: unityFrame,
            roomID: 0,
            width: grid.width,
            height: grid.height,
            luma: Data(grid.luma),
            groundTrackingReliable: reliable
        )
    }

    private func truth(
        frame: Int64,
        cameraX: Double,
        session: UUID,
        sceneName: String = "Tutorial_01",
        heroScreenX: Double? = nil,
        offset: TimeInterval
    ) -> RecordedGroundTruthSample {
        RecordedGroundTruthSample(
            offset: offset,
            receivedTimestamp: 200 + offset,
            sample: ReceiverGroundTruthSample(
                version: 2,
                type: "groundTruth",
                sessionID: session,
                sequence: UInt64(frame),
                unityFrame: frame,
                unityRealtime: 10 + offset,
                sceneName: sceneName,
                heroAvailable: true,
                heroX: 0,
                heroY: 0,
                heroZ: 0,
                velocityX: 0,
                velocityY: 0,
                facingRight: true,
                grounded: true,
                cameraAvailable: true,
                cameraX: cameraX,
                cameraY: 0,
                cameraZ: -38.1,
                cameraTargetX: cameraX,
                cameraTargetY: 0,
                cameraTargetZ: 0,
                orthographicSize: 180,
                pixelsPerWorldUnitX: 1,
                pixelsPerWorldUnitY: 1,
                heroScreenX: heroScreenX,
                heroScreenY: heroScreenX.map { _ in 180 },
                projectionPixelWidth: 640,
                projectionPixelHeight: 360,
                screenWidth: 640,
                screenHeight: 360
            )
        )
    }

    private func coarse(
        frame: Int,
        offset: TimeInterval,
        x: Double
    ) -> RecordedCoarseMotionSample {
        RecordedCoarseMotionSample(
            offset: offset,
            captureTimestamp: 100 + offset,
            renderedGameFrame: frame,
            presentedCameraX: x,
            presentedCameraY: 25,
            coarseCameraX: x,
            coarseCameraY: 25,
            direction: nil,
            screenShift: nil,
            confidence: nil,
            placeMatchKeyframeID: nil,
            placeMatchScore: nil,
            placeMatchMargin: nil,
            isControlling: true,
            isTransitioning: false,
            roomID: 0,
            roomRevision: 0,
            signalMeanPeak: 50,
            signalVisibleFraction: 0.8
        )
    }

    private func tracking(
        frame: Int,
        offset: TimeInterval,
        x: Double
    ) -> RecordedTrackingSample {
        RecordedTrackingSample(
            offset: offset,
            captureTimestamp: 100 + offset,
            cameraX: x,
            cameraY: 25,
            publishedCameraX: x,
            publishedCameraY: 25,
            poseSource: "ground",
            motionBridgeConfidence: nil,
            motionBridgeSource: nil,
            poseVerified: true,
            hasConfirmedGround: true,
            globalCorrectionX: nil,
            globalCorrectionY: nil,
            globalMatchCount: 0,
            groundSegmentCount: 1,
            inlierCount: 1,
            residualRMS: 0,
            visibleLineIDs: [],
            atlasLineIDs: [],
            groundTrackingMilliseconds: 1,
            signalMeanPeak: 50,
            signalVisibleFraction: 0.8,
            roomID: 0,
            roomRevision: 0,
            captureOffset: offset,
            renderedGameFrame: frame
        )
    }

    private func fractionalMotionGrid(
        horizontalShift: Double,
        verticalShift: Double = 0,
        gain: Double = 1,
        width: Int = 64,
        height: Int = 36
    ) -> LowResolutionMotionGrid {
        func source(_ x: Int, _ y: Int) -> Double {
            guard x >= 0, x < width, y >= 0, y < height else { return 0 }
            return Double((x * 37 + y * 61 + x * y * 3) % 211 + 22)
        }
        var pixels = [UInt8](repeating: 0, count: width * height)
        for y in 0..<height {
            for x in 0..<width {
                let sourceX = Double(x) - horizontalShift
                let sourceY = Double(y) - verticalShift
                let x0 = Int(floor(sourceX))
                let y0 = Int(floor(sourceY))
                let tx = sourceX - Double(x0)
                let ty = sourceY - Double(y0)
                let upper = source(x0, y0) * (1 - tx)
                    + source(x0 + 1, y0) * tx
                let lower = source(x0, y0 + 1) * (1 - tx)
                    + source(x0 + 1, y0 + 1) * tx
                let value = (upper * (1 - ty) + lower * ty) * gain
                pixels[y * width + x] = UInt8(
                    max(0, min(255, Int(value.rounded())))
                )
            }
        }
        return LowResolutionMotionGrid(width: width, height: height, luma: pixels)
    }

    private func image(
        _ grid: LowResolutionMotionGrid,
        renderedFrame: Int
    ) -> CGImage {
        let width = 640, height = 360
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let sourceX = min(grid.width - 1, x * grid.width / width)
                let sourceY = min(grid.height - 1, y * grid.height / height)
                let value = grid.luma[sourceY * grid.width + sourceX]
                let index = (y * width + x) * 4
                pixels[index] = value
                pixels[index + 1] = value
                pixels[index + 2] = value
                pixels[index + 3] = 255
            }
        }
        let checksum = 0x5A ^ (renderedFrame & 255)
            ^ ((renderedFrame >> 8) & 255) ^ ((renderedFrame >> 16) & 255)
        let word = UInt64(0xD3) | (UInt64(renderedFrame) << 8)
            | (UInt64(checksum) << 32)
        for bit in 0..<40 {
            let value: UInt8 = word & (1 << bit) == 0 ? 0 : 255
            for y in 0..<6 {
                for x in (bit * 3)..<(bit * 3 + 3) {
                    let index = (y * width + x) * 4
                    let markerValue = y < 3 ? value : 255 - value
                    pixels[index] = markerValue
                    pixels[index + 1] = markerValue
                    pixels[index + 2] = markerValue
                }
            }
        }
        return CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!.makeImage()!
    }
}
