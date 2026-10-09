import CoreGraphics
import XCTest

@testable import HollowKnightVision

final class PlaybackDivergenceMonitorTests: XCTestCase {
    private let frameSize = CGSize(width: 640, height: 360)

    func testConnectedTruthJoinsNewSceneAtKnightDoorwayAndReusesKnownAnchor() throws {
        var tracker = ConnectedGroundTruthCameraTracker()
        let first = try XCTUnwrap(tracker.observe(
            sample(frame: 1, scene: "RoomA", cameraX: 10, heroScreenX: 600),
            observedAt: 1, frameSize: frameSize
        ))
        XCTAssertEqual(first.cameraPosition.x, 0, accuracy: 0.001)

        let departure = try XCTUnwrap(tracker.observe(
            sample(frame: 2, scene: "RoomA", cameraX: 20, heroScreenX: 600),
            observedAt: 2, frameSize: frameSize
        ))
        XCTAssertEqual(departure.cameraPosition.x, 10, accuracy: 0.001)

        let arrival = try XCTUnwrap(tracker.observe(
            sample(frame: 3, scene: "RoomB", cameraX: 1_000, heroScreenX: 40),
            observedAt: 3, frameSize: frameSize
        ))
        XCTAssertEqual(arrival.cameraPosition.x, 570, accuracy: 0.001)

        let revisit = try XCTUnwrap(tracker.observe(
            sample(frame: 4, scene: "RoomA", cameraX: 30, heroScreenX: 50),
            observedAt: 4, frameSize: frameSize
        ))
        XCTAssertEqual(revisit.cameraPosition.x, 20, accuracy: 0.001)
    }

    func testConnectedTruthKeepsKnownSceneCameraWhileHeroUnavailable() throws {
        var tracker = ConnectedGroundTruthCameraTracker()
        _ = try XCTUnwrap(tracker.observe(
            sample(frame: 1, cameraX: 10),
            observedAt: 1,
            frameSize: frameSize
        ))
        let hiddenHero = try XCTUnwrap(tracker.observe(
            sample(frame: 2, cameraX: 16, heroAvailable: false),
            observedAt: 2,
            frameSize: frameSize
        ))
        XCTAssertEqual(hiddenHero.cameraPosition.x, 6, accuracy: 0.001)
    }

    func testConnectedTruthCannotInitializeNewSceneWhileHeroUnavailable() throws {
        var tracker = ConnectedGroundTruthCameraTracker()
        _ = try XCTUnwrap(tracker.observe(
            sample(frame: 1, scene: "Tutorial_01", cameraX: 10),
            observedAt: 1,
            frameSize: frameSize
        ))
        XCTAssertNil(tracker.observe(
            sample(
                frame: 2, scene: "Town", cameraX: 1_000,
                heroAvailable: false
            ),
            observedAt: 2,
            frameSize: frameSize
        ))
    }

    func testDivergenceRequiresSustainedExactFrameErrorAfterGrace() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        _ = monitor.appendGroundTruth(
            sample(frame: 1, cameraX: 0), observedAt: 0, frameSize: frameSize
        )
        monitor.beginEvaluation(at: 1)

        XCTAssertNil(pair(
            monitor, frame: 2, timestamp: 1,
            truthX: 0, visualX: 100
        ))
        for index in 0..<PlaybackDivergenceMonitor.requiredConsecutiveSamples - 1 {
            XCTAssertNil(pair(
                monitor,
                frame: Int64(3 + index),
                timestamp: 6.1 + Double(index) * 0.02,
                truthX: CGFloat(index),
                visualX: CGFloat(index) + 500
            ))
        }
        let decision = pair(
            monitor,
            frame: Int64(2 + PlaybackDivergenceMonitor.requiredConsecutiveSamples),
            timestamp: 6.4,
            truthX: 20,
            visualX: 520
        )
        XCTAssertEqual(
            decision?.consecutiveSamples,
            PlaybackDivergenceMonitor.requiredConsecutiveSamples
        )
        XCTAssertEqual(decision?.errorPixels ?? .nan, 400, accuracy: 0.001)
        XCTAssertEqual(decision?.sceneName, "Tutorial_01")
    }

    func testAlignedMotionDoesNotTriggerDivergence() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        for index in 0..<40 {
            let truthX = CGFloat(index * 12)
            XCTAssertNil(pair(
                monitor,
                frame: Int64(index + 1),
                timestamp: 6 + Double(index) * 0.02,
                truthX: truthX,
                visualX: truthX + 75
            ))
        }
    }

    func testSubthresholdOffsetDoesNotAbortPlayback() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        XCTAssertNil(pair(
            monitor, frame: 1, timestamp: 0,
            truthX: 0, visualX: 0
        ))
        for index in 0..<PlaybackDivergenceMonitor.requiredConsecutiveSamples * 2 {
            XCTAssertNil(pair(
                monitor,
                frame: Int64(index + 2),
                timestamp: 6 + Double(index) * 0.02,
                truthX: CGFloat(index),
                visualX: CGFloat(index) + 150
            ))
        }
    }

    func testTransitionFramesResetDivergenceEvidence() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        XCTAssertNil(pair(
            monitor, frame: 1, timestamp: 0,
            truthX: 0, visualX: 0
        ))
        for index in 0..<PlaybackDivergenceMonitor.requiredConsecutiveSamples * 2 {
            XCTAssertNil(pair(
                monitor,
                frame: Int64(index + 2),
                timestamp: 6 + Double(index) * 0.02,
                truthX: 0,
                visualX: 500,
                isTransitioning: true
            ))
        }
    }

    func testEvaluationDiscardsPreviousEndpointSceneAnchor() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        _ = monitor.appendGroundTruth(
            sample(
                frame: 1, scene: "Town", cameraX: 0,
                heroScreenX: 320
            ),
            observedAt: 0,
            frameSize: frameSize
        )
        monitor.beginEvaluation(at: 1)
        XCTAssertNil(pair(
            monitor, frame: 2, timestamp: 1,
            truthX: 0, visualX: 0,
            scene: "Tutorial_01", heroScreenX: 600
        ))
        XCTAssertNil(pair(
            monitor, frame: 3, timestamp: 6,
            truthX: 10, visualX: 10,
            scene: "Tutorial_01", heroScreenX: 600
        ))
        XCTAssertNil(pair(
            monitor, frame: 4, timestamp: 6.1,
            truthX: 1_000, visualX: 570,
            scene: "Town", heroScreenX: 40
        ))
        for index in 0..<PlaybackDivergenceMonitor.requiredConsecutiveSamples {
            XCTAssertNil(pair(
                monitor,
                frame: Int64(index + 5),
                timestamp: 8.2 + Double(index) * 0.02,
                truthX: CGFloat(1_001 + index),
                visualX: CGFloat(571 + index),
                scene: "Town",
                heroScreenX: 40
            ))
        }
    }

    func testNewSceneDivergenceStopsSoonAfterShortGrace() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        XCTAssertNil(pair(
            monitor, frame: 1, timestamp: 0,
            truthX: 0, visualX: 0,
            scene: "Tutorial_01", heroScreenX: 600
        ))
        XCTAssertNil(pair(
            monitor, frame: 2, timestamp: 6,
            truthX: 10, visualX: 10,
            scene: "Tutorial_01", heroScreenX: 600
        ))
        XCTAssertNil(pair(
            monitor, frame: 3, timestamp: 6.1,
            truthX: 1_000, visualX: 1_070,
            scene: "Town", heroScreenX: 40
        ))

        var decision: PlaybackDivergenceDecision?
        var decisionTime: TimeInterval?
        for index in 0..<40 where decision == nil {
            let timestamp = 6.12 + Double(index) * 0.02
            decision = pair(
                monitor,
                frame: Int64(index + 4),
                timestamp: timestamp,
                truthX: CGFloat(1_001 + index),
                visualX: CGFloat(1_071 + index),
                scene: "Town",
                heroScreenX: 40
            )
            if decision != nil { decisionTime = timestamp }
        }

        XCTAssertEqual(
            decision?.consecutiveSamples,
            PlaybackDivergenceMonitor.requiredConsecutiveSamples
        )
        XCTAssertLessThan(decisionTime ?? .infinity, 6.7)
    }

    func testMovingHackerTruthAbortsWhenVisionStopsPublishing() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        XCTAssertNil(pair(
            monitor, frame: 1, timestamp: 0,
            truthX: 0, visualX: 0
        ))

        XCTAssertNil(monitor.appendGroundTruth(
            sample(frame: 2, cameraX: 30),
            observedAt: 5.6,
            frameSize: frameSize
        ))
        let decision = monitor.appendGroundTruth(
            sample(frame: 3, cameraX: 60),
            observedAt: 5.8,
            frameSize: frameSize
        )

        XCTAssertEqual(decision?.cause, .visionStall)
        XCTAssertEqual(decision?.errorPixels ?? .nan, 60, accuracy: 0.001)
        XCTAssertGreaterThanOrEqual(
            decision?.silenceSeconds ?? 0,
            PlaybackDivergenceMonitor.maximumVisionSilence
        )
    }

    func testStationaryHackerTruthDoesNotTreatMissingVisionAsDivergence() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        XCTAssertNil(pair(
            monitor, frame: 1, timestamp: 0,
            truthX: 0, visualX: 0
        ))

        for index in 0..<20 {
            XCTAssertNil(monitor.appendGroundTruth(
                sample(frame: Int64(index + 2), cameraX: 0),
                observedAt: 6 + Double(index),
                frameSize: frameSize
            ))
        }
    }

    func testExactVisionPairResetsMissingVisionWatchdog() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        XCTAssertNil(pair(
            monitor, frame: 1, timestamp: 0,
            truthX: 0, visualX: 0
        ))
        XCTAssertNil(pair(
            monitor, frame: 2, timestamp: 6,
            truthX: 50, visualX: 50
        ))
        XCTAssertNil(monitor.appendGroundTruth(
            sample(frame: 3, cameraX: 90),
            observedAt: 6.6,
            frameSize: frameSize
        ))
        XCTAssertNil(monitor.appendGroundTruth(
            sample(frame: 4, cameraX: 100),
            observedAt: 6.7,
            frameSize: frameSize
        ))
    }

    func testMissingVisionWatchdogRebasesAtSceneChange() {
        let monitor = PlaybackDivergenceMonitor()
        monitor.prepare()
        monitor.beginEvaluation(at: 0)
        XCTAssertNil(pair(
            monitor, frame: 1, timestamp: 0,
            truthX: 0, visualX: 0,
            scene: "Tutorial_01", heroScreenX: 600
        ))

        XCTAssertNil(monitor.appendGroundTruth(
            sample(
                frame: 2, scene: "Town", cameraX: 1_000,
                heroScreenX: 40
            ),
            observedAt: 6,
            frameSize: frameSize
        ))
        XCTAssertNil(monitor.appendGroundTruth(
            sample(
                frame: 3, scene: "Town", cameraX: 1_030,
                heroScreenX: 40
            ),
            observedAt: 6.8,
            frameSize: frameSize
        ))
        let decision = monitor.appendGroundTruth(
            sample(
                frame: 4, scene: "Town", cameraX: 1_060,
                heroScreenX: 40
            ),
            observedAt: 6.9,
            frameSize: frameSize
        )
        XCTAssertEqual(decision?.cause, .visionStall)
        XCTAssertEqual(decision?.sceneName, "Town")
        XCTAssertEqual(decision?.errorPixels ?? .nan, 60, accuracy: 0.001)
    }

    private func pair(
        _ monitor: PlaybackDivergenceMonitor,
        frame: Int64,
        timestamp: TimeInterval,
        truthX: CGFloat,
        visualX: CGFloat,
        isTransitioning: Bool = false,
        scene: String = "Tutorial_01",
        heroScreenX: Double = 320
    ) -> PlaybackDivergenceDecision? {
        let first = monitor.appendVision(
            frameKey: HackerFrameSynchronizer.key(for: frame),
            cameraPosition: CGPoint(x: visualX, y: 0),
            observedAt: timestamp,
            isTransitioning: isTransitioning
        )
        let second = monitor.appendGroundTruth(
            sample(
                frame: frame,
                scene: scene,
                cameraX: Double(truthX),
                heroScreenX: heroScreenX
            ),
            observedAt: timestamp,
            frameSize: frameSize
        )
        return first ?? second
    }

    private func sample(
        frame: Int64,
        scene: String = "Tutorial_01",
        cameraX: Double,
        cameraY: Double = 0,
        heroScreenX: Double = 320,
        heroScreenY: Double = 180,
        heroAvailable: Bool = true
    ) -> ReceiverGroundTruthSample {
        ReceiverGroundTruthSample(
            version: ReceiverControlProtocol.version,
            type: "groundTruth",
            sessionID: UUID(),
            sequence: UInt64(frame),
            unityFrame: frame,
            unityRealtime: Double(frame),
            sceneName: scene,
            heroAvailable: heroAvailable,
            heroX: 0,
            heroY: 0,
            heroZ: 0,
            velocityX: 1,
            velocityY: 0,
            facingRight: true,
            grounded: true,
            cameraAvailable: true,
            cameraX: cameraX,
            cameraY: cameraY,
            cameraZ: 0,
            cameraTargetX: cameraX,
            cameraTargetY: cameraY,
            cameraTargetZ: 0,
            orthographicSize: 180,
            pixelsPerWorldUnitX: 1,
            pixelsPerWorldUnitY: 1,
            heroScreenX: heroScreenX,
            heroScreenY: heroScreenY,
            projectionPixelWidth: 640,
            projectionPixelHeight: 360,
            screenWidth: 640,
            screenHeight: 360
        )
    }
}
