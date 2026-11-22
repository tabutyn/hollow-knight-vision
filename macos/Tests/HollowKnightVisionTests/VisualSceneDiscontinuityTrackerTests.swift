import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class VisualSceneDiscontinuityTrackerTests: XCTestCase {
    func testSimilarBroadShadingDoesNotHideDifferentRoomTexture() {
        func shaded(_ seed: UInt64) -> LowResolutionMotionGrid {
            let detail = texture(seed)
            let values = (0..<(64 * 36)).map { i -> UInt8 in
                let x = Double(i % 64), y = Double(i / 64)
                let broad = 110 + 65 * sin(x / 15) + 35 * cos(y / 11)
                return UInt8(max(0, min(255, broad + (Double(detail.luma[i]) - 128) * 0.16)))
            }
            return LowResolutionMotionGrid(width: 64, height: 36, luma: values)
        }
        var tracker = VisualSceneDiscontinuityTracker()
        XCTAssertEqual(sequence(&tracker, departure: shaded(1), blackout: black,
            arrival: shaded(2)).count, 1)
        tracker.reset()
        let source = shaded(1)
        let dimmed = LowResolutionMotionGrid(width: 64, height: 36,
            luma: source.luma.map { UInt8(Double($0) * 0.8) })
        XCTAssertTrue(sequence(&tracker, departure: source, blackout: black,
            arrival: dimmed).isEmpty)
    }

    func testCounterSteeringAfterSceneryVanishesKeepsVisibleDeparture() {
        var tracker = VisualSceneDiscontinuityTracker()
        for i in 0..<8 {
            _ = tracker.observe(grid: texture(1), timestamp: Double(i) * 0.05,
                direction: .right, worldPose: .zero)
        }
        var cuts = [VisualSceneDiscontinuityTracker.Cut]()
        for i in 0..<30 {
            if let cut = tracker.observe(grid: i < 10 ? black : texture(2),
                timestamp: 0.4 + Double(i) * 0.05, direction: .left,
                worldPose: .zero) { cuts.append(cut) }
        }
        XCTAssertEqual(cuts.count, 1)
        XCTAssertEqual(cuts.first?.direction, .right)
    }

    func testObservedLoadingSurvivesBoundedCaptureGapAndStillRejectsSameScenery() {
        for arrival in [texture(1), texture(2)] {
            for arrivalStartsBlack in [false, true] {
                var tracker = VisualSceneDiscontinuityTracker()
                _ = tracker.observe(grid: texture(1), timestamp: 0, direction: .right, worldPose: .zero)
                for i in 1...6 {
                    _ = tracker.observe(grid: black, timestamp: Double(i) * 0.05,
                        direction: nil, worldPose: .zero)
                }
                var cuts = [VisualSceneDiscontinuityTracker.Cut]()
                for i in 0..<20 {
                    let grid = arrivalStartsBlack && i < 4 ? black : arrival
                    if let cut = tracker.observe(grid: grid, timestamp: 1 + Double(i) * 0.05,
                        direction: nil, worldPose: .zero) { cuts.append(cut) }
                }
                XCTAssertEqual(cuts.count, arrival == texture(1) ? 0 : 1)
            }
        }
    }

    func testBlackFramesAfterGapMustIndependentlyConfirmBlackout() {
        for trailingBlackFrames in [1, 6] {
            var tracker = VisualSceneDiscontinuityTracker()
            _ = tracker.observe(grid: texture(1), timestamp: 0, direction: .right, worldPose: .zero)
            _ = tracker.observe(grid: black, timestamp: 0.05, direction: nil, worldPose: .zero)
            var cuts = [VisualSceneDiscontinuityTracker.Cut]()
            for i in 0..<20 {
                if let cut = tracker.observe(grid: i < trailingBlackFrames ? black : texture(2),
                    timestamp: 0.8 + Double(i) * 0.05, direction: nil,
                    worldPose: .zero) { cuts.append(cut) }
            }
            XCTAssertEqual(cuts.count, trailingBlackFrames == 1 ? 0 : 1)
        }
    }

    func testGapWithoutLoadingEvidenceOrLongGapCannotDiscoverRoom() {
        for hadBlackEvidence in [false, true] {
            var tracker = VisualSceneDiscontinuityTracker()
            _ = tracker.observe(grid: texture(1), timestamp: 0, direction: .right, worldPose: .zero)
            if hadBlackEvidence {
                for i in 1...6 {
                    _ = tracker.observe(grid: black, timestamp: Double(i) * 0.05,
                        direction: nil, worldPose: .zero)
                }
            }
            let start = hadBlackEvidence ? 4.0 : 0.8
            for i in 0..<20 {
                XCTAssertNil(tracker.observe(grid: i < 6 ? black : texture(2),
                    timestamp: start + Double(i) * 0.05, direction: .right, worldPose: .zero))
            }
        }
    }

    func testLoadingCutRequiresSustainedBlackoutAndStableNovelScenery() {
        var tracker = VisualSceneDiscontinuityTracker()
        let cuts = sequence(&tracker, departure: texture(1), blackout: black,
                            arrival: texture(2))
        XCTAssertEqual(cuts.count, 1)
        XCTAssertEqual(cuts.first?.direction, .right)
        XCTAssertEqual(cuts.first?.departureWorldPose, CGPoint(x: 12, y: 30))
        XCTAssertGreaterThan(cuts.first?.arrivalTimestamp ?? 0, 0.7)
    }

    func testSlowContinuousLoadingStillRequiresNovelStableArrival() {
        for arrival in [texture(1), texture(2)] {
            var tracker = VisualSceneDiscontinuityTracker()
            var cuts = [VisualSceneDiscontinuityTracker.Cut]()
            for index in 0..<200 {
                let grid = index < 8 ? texture(1) : (index < 180 ? black : arrival)
                if let cut = tracker.observe(grid: grid, timestamp: Double(index) * 0.05,
                    direction: index < 8 ? .right : nil, worldPose: .zero) { cuts.append(cut) }
                if index >= 8 && index < 180 { XCTAssertTrue(tracker.isPending) }
            }
            XCTAssertEqual(cuts.count, arrival == texture(1) ? 0 : 1)
        }
    }

    func testDarkGameplayWithPersistentHUDisNotLoading() {
        var tracker = VisualSceneDiscontinuityTracker()
        var pixels = black.luma
        for y in 2..<5 { for x in 5..<13 { pixels[y * 64 + x] = 230 } }
        let dark = LowResolutionMotionGrid(width: 64, height: 36, luma: pixels)
        XCTAssertFalse(VisualSceneDiscontinuityTracker.isCompleteBlackout(dark))
        XCTAssertTrue(sequence(&tracker, departure: texture(1), blackout: dark,
                               arrival: texture(2)).isEmpty)
    }

    func testMovingArrivalConfirmsThroughConsecutiveOverlappingImages() {
        for (stepX, stepY) in [(2, 0), (-2, 1), (0, 2)] {
            var tracker = VisualSceneDiscontinuityTracker()
            var cuts = [VisualSceneDiscontinuityTracker.Cut]()
            let destination = texture(2)
            for index in 0..<45 {
                let grid: LowResolutionMotionGrid
                if index < 8 { grid = texture(1) }
                else if index < 18 { grid = black }
                else {
                    let dx = stepX * (index - 18), dy = stepY * (index - 18)
                    let pixels = (0..<(64 * 36)).map { offset -> UInt8 in
                        let x = ((offset % 64 + dx) % 64 + 64) % 64
                        let y = ((offset / 64 + dy) % 36 + 36) % 36
                        return destination.luma[y * 64 + x]
                    }
                    grid = LowResolutionMotionGrid(width: 64, height: 36, luma: pixels)
                }
                if let cut = tracker.observe(grid: grid, timestamp: Double(index) * 0.05,
                    direction: .right, worldPose: .zero) { cuts.append(cut) }
            }
            XCTAssertEqual(cuts.count, 1, "Arrival motion must not require the camera to stop")
        }
    }

    func testFlickeringNovelArrivalDoesNotFormAStableChain() {
        var tracker = VisualSceneDiscontinuityTracker()
        var cuts = [VisualSceneDiscontinuityTracker.Cut]()
        for index in 0..<45 {
            let grid = index < 8 ? texture(1) : (index < 18 ? black : texture(UInt64(index + 100)))
            if let cut = tracker.observe(grid: grid, timestamp: Double(index) * 0.05,
                direction: .right, worldPose: .zero) { cuts.append(cut) }
        }
        XCTAssertTrue(cuts.isEmpty)
    }

    func testTinyLoadingSpriteKeepsObservedBlackoutWithoutHidingHUDOrScenery() {
        var pixels = black.luma
        for y in 25..<28 { for x in 54..<57 { pixels[y * 64 + x] = 230 } }
        let glyph = LowResolutionMotionGrid(width: 64, height: 36, luma: pixels)
        XCTAssertTrue(VisualSceneDiscontinuityTracker.isCompleteBlackout(glyph))
        var tracker = VisualSceneDiscontinuityTracker()
        XCTAssertEqual(sequence(&tracker, departure: texture(1), blackout: glyph,
            arrival: texture(2), blackFrames: 140).count, 1)
        tracker.reset()
        XCTAssertTrue(sequence(&tracker, departure: texture(1), blackout: glyph,
            arrival: texture(1), blackFrames: 140).isEmpty)
        for y in 2..<4 { for x in 5..<9 { pixels[y * 64 + x] = 230 } }
        XCTAssertFalse(VisualSceneDiscontinuityTracker.isCompleteBlackout(
            LowResolutionMotionGrid(width: 64, height: 36, luma: pixels)))
        var scattered = black.luma
        for x in stride(from: 2, to: 60, by: 6) { scattered[25 * 64 + x] = 230 }
        XCTAssertFalse(VisualSceneDiscontinuityTracker.isCompleteBlackout(
            LowResolutionMotionGrid(width: 64, height: 36, luma: scattered)))
    }

    func testFadeBackToSameSceneryDoesNotChangeOwnership() {
        var tracker = VisualSceneDiscontinuityTracker()
        let source = texture(1)
        let exposure = LowResolutionMotionGrid(width: 64, height: 36,
            luma: source.luma.map { UInt8(Double($0) * 0.8) })
        XCTAssertTrue(sequence(&tracker, departure: source, blackout: black,
                               arrival: exposure).isEmpty)
    }

    func testBriefCaptureGlitchDoesNotDiscoverRoom() {
        var tracker = VisualSceneDiscontinuityTracker()
        XCTAssertTrue(sequence(&tracker, departure: texture(1), blackout: black,
                               arrival: texture(2), blackFrames: 2).isEmpty)
    }

    func testCaptureGapCannotStandInForObservedBlackout() {
        var tracker = VisualSceneDiscontinuityTracker()
        _ = tracker.observe(grid: texture(1), timestamp: 0, direction: .right,
                            worldPose: .zero)
        _ = tracker.observe(grid: black, timestamp: 0.05, direction: .right,
                            worldPose: .zero)
        _ = tracker.observe(grid: black, timestamp: 0.8, direction: .right,
                            worldPose: .zero)
        for index in 0..<12 {
            XCTAssertNil(tracker.observe(grid: texture(2), timestamp: 0.85 + Double(index) * 0.05,
                direction: .right, worldPose: .zero))
        }
    }

    func testStartupBlacknessAndNoDirectionalDepartureDoNotDiscoverRoom() {
        var tracker = VisualSceneDiscontinuityTracker()
        XCTAssertTrue(sequence(&tracker, departure: black, blackout: black,
                               arrival: texture(2)).isEmpty)
        tracker.reset()
        XCTAssertTrue(sequence(&tracker, departure: texture(1), blackout: black,
                               arrival: texture(2), direction: nil).isEmpty)
    }

    func testOneChangingSideDoesNotDiscoverRoom() {
        var tracker = VisualSceneDiscontinuityTracker()
        let source = texture(1)
        var pixels = texture(2).luma
        for y in 0..<36 { for x in 0..<32 { pixels[y * 64 + x] = source.luma[y * 64 + x] } }
        let arrival = LowResolutionMotionGrid(width: 64, height: 36, luma: pixels)
        XCTAssertTrue(sequence(&tracker, departure: source, blackout: black,
                               arrival: arrival).isEmpty)
    }

    func testRoomOwnershipCutCreatesNoPortalOrForcedPlacementAndPersists() throws {
        let tracker = VisualRoomTransitionTracker()
        let pose = CGPoint(x: 120, y: -80)
        for index in 0..<40 {
            let grid = index < 8 ? texture(1) : (index < 18 ? black : texture(2))
            _ = tracker.observe(profile: profile(grid),
                baseDecision: TransitionFrameDecision(admitsTracking: index < 8 || index >= 18,
                    resumedAfterTransition: false),
                horizontalInput: .right, knightRect: nil,
                frameSize: CGSize(width: 640, height: 360), currentWorldPose: pose,
                solveWidth: 640, timestamp: Double(index) * 0.05)
            if index >= 8 && index < 18 {
                XCTAssertTrue(tracker.snapshot.isTransitioning,
                              "Unresolved loading must withhold atlas ownership")
            }
        }
        XCTAssertFalse(tracker.snapshot.isTransitioning)
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.revision, 1)
        XCTAssertEqual(tracker.snapshot.entryWorldPose, pose)
        XCTAssertTrue(tracker.snapshot.portals.isEmpty)
        XCTAssertEqual(tracker.snapshot.doorwayTransition?.sourceRoomID, 0)
        XCTAssertEqual(tracker.snapshot.doorwayTransition?.targetRoomID, 1)
        XCTAssertEqual(tracker.snapshot.doorwayTransition?.direction, .right)
        XCTAssertEqual(tracker.snapshot.doorwayTransition?.departureTimestamp ?? -1, 0.35, accuracy: 0.001)
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        try tracker.saveTopology(to: url)
        let reopened = VisualRoomTransitionTracker()
        let restored = try reopened.restoreTopology(from: url)
        XCTAssertEqual(restored.roomID, 1)
        XCTAssertEqual(restored.entryWorldPose, pose)
        XCTAssertTrue(restored.portals.isEmpty)
        XCTAssertNil(restored.doorwayTransition, "A saved room is not a fresh loading cut")
        XCTAssertNil(tracker.reset().doorwayTransition)
    }

    func testLegacyTwoRoomsRestoreWithoutInventedPortalOrPlacementChange() throws {
        let poses = [CGPoint(x: 301, y: -73), CGPoint(x: -990, y: 420)]
        let observations = poses.enumerated().map { index, pose in
            LiveWorldObservation(id: index, sourceObservationID: index, timestamp: Double(index),
                sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
                excludedRects: [], roomID: index, rawPose: pose, optimizedPose: pose)
        }
        let world = try LiveWorldSnapshot(schemaVersion: 1, anchoredObservationID: 0, observations: observations)
        let before = try JSONEncoder().encode(world)
        let tracker = VisualRoomTransitionTracker()
        let result = tracker.restoreDisconnectedRooms(from: world)
        XCTAssertEqual(result.roomID, 1)
        XCTAssertEqual(result.entryWorldPose, poses[1])
        XCTAssertTrue(result.portals.isEmpty)
        XCTAssertEqual(world.observations.map(\.optimizedPose), poses)
        XCTAssertEqual(try JSONDecoder().decode(LiveWorldSnapshot.self, from: before), world)
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        try tracker.saveTopology(to: url)
        let reopened = try VisualRoomTransitionTracker().restoreTopology(from: url)
        XCTAssertEqual(reopened.roomID, result.roomID)
        XCTAssertEqual(reopened.entryWorldPose, result.entryWorldPose)
        XCTAssertTrue(reopened.portals.isEmpty)
    }

    func testDisconnectedRoomIDsAreNotReusedAndDurableRoomNeedsNoPortal() throws {
        let observation = LiveWorldObservation(id: 1, sourceObservationID: 1, timestamp: 1,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
            excludedRects: [], roomID: 42, rawPose: .zero, optimizedPose: .zero)
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.restoreDisconnectedRooms(from: try LiveWorldSnapshot(anchoredObservationID: 1, observations: [observation]))
        for index in 0..<40 {
            let grid = index < 8 ? texture(1) : (index < 18 ? black : texture(2))
            _ = tracker.observe(profile: profile(grid),
                baseDecision: TransitionFrameDecision(admitsTracking: index < 8 || index >= 18,
                    resumedAfterTransition: false), horizontalInput: .right, knightRect: nil,
                frameSize: CGSize(width: 640, height: 360), currentWorldPose: .zero,
                solveWidth: 640, timestamp: 2 + Double(index) * 0.05)
        }
        XCTAssertEqual(tracker.snapshot.roomID, 43)
        XCTAssertTrue(tracker.snapshot.portals.isEmpty)
        let restored = tracker.restoreActiveRoomFromDurableEvidence(42, entryWorldPose: CGPoint(x: 5, y: 7))
        XCTAssertEqual(restored.roomID, 42)
        XCTAssertEqual(restored.entryWorldPose, CGPoint(x: 5, y: 7))
        XCTAssertTrue(restored.portals.isEmpty)
    }

    /// Optional image-only audit against a developer's immutable recording.
    /// Reference telemetry is neither decoded nor supplied to the detector.
    func testArchivedVisualCuts() throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_SCENE_CUT_AUDIT_INPUT"] else {
            throw XCTSkip("Set HKV_SCENE_CUT_AUDIT_INPUT to audit archived low-resolution images")
        }
        struct GridFrame: Decodable {
            let captureTimestamp: Double
            let width: Int
            let height: Int
            let luma: Data
        }
        struct Input: Decodable { let horizontalInput: Int? }
        struct InputFrame: Decodable { let captureTimestamp: Double; let input: Input? }
        struct Archive: Decodable { let lowResolutionFrames: [GridFrame]; let coarseMotionSamples: [InputFrame] }
        let archive = try JSONDecoder().decode(Archive.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
        let inputs = Dictionary(archive.coarseMotionSamples.map { ($0.captureTimestamp, $0.input?.horizontalInput) },
                                uniquingKeysWith: { _, latest in latest })
        var tracker = VisualSceneDiscontinuityTracker()
        var cuts = [VisualSceneDiscontinuityTracker.Cut]()
        for frame in archive.lowResolutionFrames {
            let rawDirection = inputs[frame.captureTimestamp] ?? nil
            if let cut = tracker.observe(grid: LowResolutionMotionGrid(width: frame.width,
                height: frame.height, luma: Array(frame.luma)), timestamp: frame.captureTimestamp,
                direction: rawDirection.flatMap(VisualRoomDirection.init(rawValue:)), worldPose: .zero) {
                cuts.append(cut)
            }
        }
        let start = try XCTUnwrap(archive.lowResolutionFrames.first?.captureTimestamp)
        print("Visual cuts: \(cuts.map { $0.arrivalTimestamp - start })")
        XCTAssertEqual(cuts.count, 1, "Fixed first-bench images contain one complete loading cut")
    }

    private func sequence(_ tracker: inout VisualSceneDiscontinuityTracker,
                          departure: LowResolutionMotionGrid, blackout: LowResolutionMotionGrid,
                          arrival: LowResolutionMotionGrid, blackFrames: Int = 10,
                          direction: VisualRoomDirection? = .right) -> [VisualSceneDiscontinuityTracker.Cut] {
        var cuts = [VisualSceneDiscontinuityTracker.Cut]()
        for index in 0..<(8 + blackFrames + 16) {
            let grid = index < 8 ? departure : (index < 8 + blackFrames ? blackout : arrival)
            if let cut = tracker.observe(grid: grid, timestamp: Double(index) * 0.05,
                direction: direction, worldPose: CGPoint(x: 12, y: 30)) { cuts.append(cut) }
        }
        return cuts
    }
    private var black: LowResolutionMotionGrid {
        var luma = [UInt8](repeating: 0, count: 64 * 36)
        for x in 0..<64 { luma[x] = UInt8((x * 31) % 256) } // Non-game marker strip.
        return LowResolutionMotionGrid(width: 64, height: 36, luma: luma)
    }
    private func texture(_ seed: UInt64) -> LowResolutionMotionGrid {
        var state = seed
        let values: [UInt8] = (0..<(64 * 36)).map { _ in
            state = state &* 6364136223846793005 &+ 1
            return UInt8(20 + ((state >> 32) % 220))
        }
        return LowResolutionMotionGrid(width: 64, height: 36, luma: values)
    }
    private func profile(_ grid: LowResolutionMotionGrid) -> FrameSignalProfile {
        FrameSignalProfile(sampleCount: grid.luma.count,
            signaledCount: grid.luma.count(where: { $0 >= 5 }),
            visibleCount: grid.luma.count(where: { $0 >= 16 }),
            meanPeak: grid.luma.reduce(0.0) { $0 + Double($1) } / Double(grid.luma.count),
            motionGrid: grid)
    }
}
