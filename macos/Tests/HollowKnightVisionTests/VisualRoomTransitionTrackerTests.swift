import CoreGraphics
import XCTest

@testable import HollowKnightVision

final class VisualRoomTransitionTrackerTests: XCTestCase {
    private let size = CGSize(width: 640, height: 360)

    func testBlackoutWhileStandingResumesSameRoom() {
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.observe(
            profile: black, baseDecision: held,
            horizontalInput: nil, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1
        )
        for index in 0..<3 {
            _ = tracker.observe(
                profile: visible, baseDecision: index == 2 ? resumed : admitted,
                horizontalInput: nil, knightRect: nil, frameSize: size,
                currentWorldPose: .zero, solveWidth: 640,
                timestamp: 2 + Double(index) * 0.1
            )
        }
        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.revision, 0)
    }

    func testLeftDepartureCreatesRoomToLeftAfterStableArrival() {
        let tracker = VisualRoomTransitionTracker()
        let departure = CGPoint(x: -120, y: 8)
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: departure, solveWidth: 640, timestamp: 1
        )
        let pending = tracker.observe(
            profile: doorwayProfile(black, shift: 4), baseDecision: held,
            horizontalInput: .left, knightRect: nil, frameSize: size,
            currentWorldPose: departure, solveWidth: 640, timestamp: 1.1
        )
        XCTAssertTrue(pending.isTransitioning)
        for index in 0..<3 {
            _ = tracker.observe(
                profile: doorwayProfile(visible, shift: 4),
                baseDecision: index == 2 ? resumed : held,
                horizontalInput: nil, knightRect: rightKnight, frameSize: size,
                currentWorldPose: departure, solveWidth: 640,
                timestamp: 2 + Double(index) * 0.1
            )
        }
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.roomName, "Room 2")
        XCTAssertEqual(tracker.snapshot.entryWorldPose.x, departure.x - 80)
        XCTAssertEqual(tracker.snapshot.entryWorldPose.y, departure.y)
        XCTAssertEqual(tracker.snapshot.portals.count, 1)
        XCTAssertEqual(tracker.snapshot.portals.first?.leftRoomID, 1)
        XCTAssertEqual(tracker.snapshot.portals.first?.rightRoomID, 0)
        XCTAssertEqual(tracker.snapshot.portals.first?.leftDoorWorldX, departure.x + 272)
        XCTAssertEqual(tracker.snapshot.portals.first?.rightDoorWorldX, departure.x + 288)
        XCTAssertNil(tracker.snapshot.compositionBounds.minimumWorldX)
        XCTAssertEqual(
            tracker.snapshot.compositionBounds.maximumWorldX,
            departure.x + 272
        )
    }

    func testKnownRoomCannotReactivateAwayFromRecordedDoorway() {
        let tracker = VisualRoomTransitionTracker()
        let doorway = CGPoint(x: -120, y: 0)
        transitionAppearance(
            tracker, from: visible, to: nearBlack,
            heldDirection: .left, pose: doorway, start: 1
        )
        transitionAppearance(
            tracker, from: nearBlack, to: visible,
            heldDirection: .right, pose: tracker.snapshot.entryWorldPose, start: 2
        )
        XCTAssertEqual(tracker.snapshot.roomID, 0)

        transitionAppearance(
            tracker, from: visible, to: nearBlack,
            heldDirection: .right, pose: CGPoint(x: 800, y: 0), start: 3
        )
        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.revision, 2)
    }

    func testDoorwayTopologyPersistsAndRestores() throws {
        let tracker = VisualRoomTransitionTracker()
        let doorway = CGPoint(x: -120, y: 8)
        traverse(
            tracker, direction: .left, edge: leftKnight,
            pose: doorway, start: 1
        )
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(
            at: directory, withIntermediateDirectories: true
        )
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("visual-rooms.json")
        try tracker.saveTopology(to: url)

        let restored = VisualRoomTransitionTracker()
        let snapshot = try restored.restoreTopology(from: url)
        XCTAssertEqual(snapshot.roomID, 1)
        XCTAssertEqual(snapshot.entryWorldPose, tracker.snapshot.entryWorldPose)
        XCTAssertEqual(snapshot.portals, tracker.snapshot.portals)
        XCTAssertEqual(snapshot.compositionBounds, tracker.snapshot.compositionBounds)
    }

    func testCompositionMaskCropsPixelsAcrossDoorway() {
        let leftCrop = VisualRoomCompositionMask.omittedRects(
            bounds: .init(minimumWorldX: 0, maximumWorldX: nil),
            cameraPosition: CGPoint(x: -100, y: 0),
            solveWidth: 640,
            frameSize: size
        )
        XCTAssertEqual(leftCrop, [CGRect(x: 0, y: 0, width: 100, height: 360)])

        let rightCrop = VisualRoomCompositionMask.omittedRects(
            bounds: .init(minimumWorldX: nil, maximumWorldX: 0),
            cameraPosition: CGPoint(x: -500, y: 0),
            solveWidth: 640,
            frameSize: size
        )
        XCTAssertEqual(rightCrop, [CGRect(x: 500, y: 0, width: 140, height: 360)])
    }

    func testLowResolutionImageTravelVotesForOppositeCameraDirection() {
        let reference = motionGrid(shift: 0)
        let artworkMovedRight = motionGrid(shift: 2)
        let left = LowResolutionRoomMotionTracker.estimate(
            from: reference,
            to: artworkMovedRight
        )
        XCTAssertEqual(left?.direction, .left)
        XCTAssertEqual(left?.screenShift, 2)

        let artworkMovedLeft = motionGrid(shift: -2)
        let right = LowResolutionRoomMotionTracker.estimate(
            from: reference,
            to: artworkMovedLeft
        )
        XCTAssertEqual(right?.direction, .right)
        XCTAssertEqual(right?.screenShift, -2)
    }

    func testLowResolutionMotionSurvivesRoomWideDarkening() {
        let reference = motionGrid(shift: 0)
        let darkerArtworkMovedRight = motionGrid(shift: 2, gain: 0.32)
        let estimate = LowResolutionRoomMotionTracker.estimate(
            from: reference,
            to: darkerArtworkMovedRight
        )
        XCTAssertEqual(estimate?.direction, .left)
        XCTAssertEqual(estimate?.screenShift, 2)
    }

    func testLowResolutionMotionRetainsSubcellTravel() {
        let reference = fractionalMotionGrid(horizontalShift: 0)
        let translated = fractionalMotionGrid(horizontalShift: 0.35)
        let vector = LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: translated
        )
        XCTAssertEqual(vector?.screenShiftX ?? .nan, 0.35, accuracy: 0.16)
        XCTAssertEqual(vector?.screenShiftY ?? .nan, 0, accuracy: 0.05)
    }

    func testLowResolutionMotionRejectsStaticFrame() {
        let frame = fractionalMotionGrid(horizontalShift: 0)
        XCTAssertNil(LowResolutionRoomMotionTracker.translation(
            from: frame,
            to: frame
        ))
    }

    func testLowResolutionSubcellMotionSurvivesRoomWideFade() {
        let reference = fractionalMotionGrid(horizontalShift: 0)
        let faded = fractionalMotionGrid(
            horizontalShift: 0.45,
            gain: 0.28
        )
        let vector = LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: faded
        )
        XCTAssertEqual(vector?.screenShiftX ?? .nan, 0.45, accuracy: 0.18)
    }

    func testLowResolutionMotionSurvivesShrinkingVisibilityCircle() {
        let reference = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 0),
            radius: 30
        )
        let moved = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 1.2, verticalShift: 0.6),
            radius: 14
        )
        let vector = LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: moved
        )
        XCTAssertEqual(vector?.screenShiftX ?? .nan, 1.2, accuracy: 0.35)
        XCTAssertEqual(vector?.screenShiftY ?? .nan, 0.6, accuracy: 0.35)
    }

    func testLowResolutionMotionIgnoresMovingVisibilityCircle() {
        let reference = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 0),
            radius: 28,
            centerX: 27,
            centerY: 19
        )
        let moved = visibilityCircle(
            fractionalMotionGrid(horizontalShift: -1.4, verticalShift: 0.7),
            radius: 15,
            centerX: 36,
            centerY: 15
        )
        let vector = LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: moved
        )
        XCTAssertEqual(vector?.screenShiftX ?? .nan, -1.4, accuracy: 0.35)
        XCTAssertEqual(vector?.screenShiftY ?? .nan, 0.7, accuracy: 0.35)
    }

    func testMovingVisibilityCircleCannotCreateFalseMotion() {
        let texture = fractionalMotionGrid(horizontalShift: 0)
        let reference = visibilityCircle(
            texture, radius: 22, centerX: 25, centerY: 18
        )
        let movedCircle = visibilityCircle(
            texture, radius: 15, centerX: 38, centerY: 14
        )
        XCTAssertNil(LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: movedCircle
        ))
    }

    func testTinyVisibilityCircleCannotCreateMotion() {
        let reference = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 0),
            radius: 3
        )
        let moved = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 2, verticalShift: 1),
            radius: 2,
            centerX: 34,
            centerY: 17
        )
        XCTAssertNil(LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: moved
        ))
    }

    func testFloorlessOdometryAccumulatesThroughMovingFadeCircle() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: visibilityCircle(
                fractionalMotionGrid(horizontalShift: 0),
                radius: 29,
                centerX: 28,
                centerY: 18
            ),
            timestamp: 0
        )

        for index in 1...20 {
            let progress = Double(index)
            let step = odometry.observe(
                grid: visibilityCircle(
                    fractionalMotionGrid(
                        horizontalShift: -0.55 * progress,
                        verticalShift: 0.22 * progress
                    ),
                    radius: 29 - 0.8 * progress,
                    centerX: 28 + 0.5 * progress,
                    centerY: 18 - 0.15 * progress
                ),
                timestamp: 0.05 * progress,
                solveWidth: 640,
                expectedDirection: .right
            )
            XCTAssertNotNil(step, "lost fade odometry frame \(index)")
        }

        XCTAssertEqual(odometry.sampleCount, 20)
        XCTAssertEqual(odometry.worldDeltaX, 110, accuracy: 18)
        XCTAssertEqual(odometry.worldDeltaY, 44, accuracy: 18)
    }

    func testFloorlessOdometryBridgesOnlyShortSameDirectionOverlapLoss() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        odometry.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 200, dy: 0),
                timestamp: 0
            ),
            expectedDirection: .right
        )
        XCTAssertNotNil(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: -1),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .right
        ))
        XCTAssertNotNil(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: -2),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: .right
        ))
        let beforeOverlapLoss = odometry.worldDeltaX
        XCTAssertNotNil(odometry.observe(
            grid: visibilityCircle(
                fractionalMotionGrid(horizontalShift: -3), radius: 2
            ),
            timestamp: 0.15,
            solveWidth: 640,
            expectedDirection: .right
        ))
        let afterOverlapLoss = odometry.worldDeltaX
        XCTAssertGreaterThan(afterOverlapLoss, beforeOverlapLoss + 5)
        XCTAssertLessThan(afterOverlapLoss, beforeOverlapLoss + 20)

        XCTAssertNil(odometry.observe(
            grid: visibilityCircle(
                fractionalMotionGrid(horizontalShift: -4), radius: 2
            ),
            timestamp: 0.20,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertEqual(odometry.worldDeltaX, afterOverlapLoss, accuracy: 0.001)
    }

    func testReliableVelocityHandoffBridgesInitialFadeMismatch() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: motionGrid(shift: 0),
            timestamp: 0
        )
        odometry.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 200, dy: 0),
                timestamp: 0
            ),
            expectedDirection: .right
        )

        XCTAssertEqual(odometry.observe(
            // Direct image registration points strongly left, so the trusted
            // pre-fade velocity must carry this first corrupted sample right.
            grid: motionGrid(shift: 2),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .right
        ) ?? .nan, 10, accuracy: 0.001)
        XCTAssertEqual(odometry.observe(
            grid: visibilityCircle(
                motionGrid(shift: 3), radius: 2
            ),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: .right
        ) ?? .nan, 10, accuracy: 0.001)
        XCTAssertEqual(odometry.worldDeltaX, 20, accuracy: 0.001)

        var smallContradiction = LowResolutionTransitionOdometry()
        smallContradiction.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        smallContradiction.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: -360, dy: 0),
                timestamp: 0
            ),
            expectedDirection: .left
        )
        XCTAssertEqual(smallContradiction.observe(
            // The ordinary easing policy would accept this +5 px camera
            // estimate. Fresh verified velocity must carry the fade left.
            grid: fractionalMotionGrid(horizontalShift: -0.5),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .left
        ) ?? .nan, -18, accuracy: 0.001)

        var expired = LowResolutionTransitionOdometry()
        expired.arm(grid: motionGrid(shift: 0), timestamp: 0.45)
        expired.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 200, dy: 0),
                timestamp: 0
            ),
            expectedDirection: .right
        )
        XCTAssertNil(expired.observe(
            grid: motionGrid(shift: 2),
            timestamp: 0.50,
            solveWidth: 640,
            expectedDirection: .right
        ))
        XCTAssertEqual(expired.worldDeltaX, 0, accuracy: 0.001)
    }

    func testReliableVelocityHandoffBridgesCollapsedSameDirectionMotion() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        odometry.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 200, dy: 0),
                timestamp: 0
            ),
            expectedDirection: .right
        )

        XCTAssertEqual(odometry.observe(
            // Fading texture retains the right sign but reports only one
            // quarter of the independently measured pre-fade camera speed.
            grid: fractionalMotionGrid(horizontalShift: -0.25),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .right
        ) ?? .nan, 10, accuracy: 0.5)
        XCTAssertEqual(odometry.worldDeltaX, 10, accuracy: 0.5)
    }

    func testReliableVelocityHandoffKeepsStrongSameDirectionMeasurement() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        odometry.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 200, dy: 0),
                timestamp: 0
            ),
            expectedDirection: .right
        )

        XCTAssertEqual(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: -0.95),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .right
        ) ?? .nan, 9.5, accuracy: 1)
    }

    func testReliableVelocityHandoffBridgesWeakVerticalFadeMotion() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(
                horizontalShift: 0,
                verticalShift: 0
            ),
            timestamp: 0
        )
        odometry.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 0, dy: -560),
                timestamp: 0
            ),
            expectedDirection: .left
        )

        XCTAssertNotNil(odometry.observe(
            // The tiny visible circle reports only -2.5 px while the fresh
            // verified trajectory predicts -28 px.
            grid: fractionalMotionGrid(
                horizontalShift: 0,
                verticalShift: -0.25
            ),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertEqual(odometry.worldDeltaY, -28, accuracy: 2)

        XCTAssertNotNil(odometry.observe(
            // One contradictory weak vector is also bridged, but the bounded
            // policy cannot continue doing this indefinitely.
            grid: fractionalMotionGrid(
                horizontalShift: 0,
                verticalShift: -0.10
            ),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertEqual(odometry.worldDeltaY, -56, accuracy: 3)

        XCTAssertNotNil(odometry.observe(
            grid: fractionalMotionGrid(
                horizontalShift: 0,
                verticalShift: -2.46
            ),
            timestamp: 0.15,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertEqual(odometry.worldDeltaY, -79.6, accuracy: 4)
    }

    func testVerticalHandoffUsesLatestMeasuredAcceleration() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0, verticalShift: 0),
            timestamp: 0
        )
        odometry.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 0, dy: -80),
                timestamp: 0
            ),
            expectedDirection: .left
        )

        XCTAssertNotNil(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: 0, verticalShift: -0.4),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertNotNil(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: 0, verticalShift: -1.4),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertNotNil(odometry.observe(
            // Direct overlap collapses to about -1 px. Carry the newest
            // measured -10 px step rather than the older -4 px median.
            grid: fractionalMotionGrid(horizontalShift: 0, verticalShift: -1.5),
            timestamp: 0.15,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertEqual(odometry.worldDeltaY, -24, accuracy: 3)
    }

    func testVerticalHandoffSurvivesInputBeginningMidFall() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0, verticalShift: 0),
            timestamp: 0
        )
        odometry.primeHorizontalVelocity(
            LowResolutionCameraVelocitySeed(
                velocity: CGVector(dx: 0, dy: -560),
                timestamp: 0
            ),
            expectedDirection: nil
        )

        XCTAssertNotNil(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: 0, verticalShift: -0.25),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: nil
        ))
        XCTAssertNotNil(odometry.observe(
            // Horizontal intent appears after the vertical camera motion began.
            // This is not a left/right reversal and must not erase the fall.
            grid: fractionalMotionGrid(horizontalShift: 0, verticalShift: -0.10),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertEqual(odometry.worldDeltaY, -56, accuracy: 3)
    }

    func testFloorlessTurnaroundRecognizesRoomOneBeforePlacingNextRoomLeft() {
        let tracker = VisualRoomTransitionTracker()
        let start = CGPoint(x: 100, y: 40)
        let initial = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 0),
            radius: 29,
            centerX: 32,
            centerY: 18
        )
        _ = tracker.observe(
            profile: motionProfile(initial),
            baseDecision: admitted,
            horizontalInput: nil,
            knightRect: CGRect(x: 300, y: 150, width: 40, height: 40),
            frameSize: size,
            currentWorldPose: start,
            solveWidth: 640,
            timestamp: 0,
            groundTrackingReliable: true
        )

        var final = tracker.snapshot
        var returnPlaceMatch: LowResolutionPlaceMatch?
        for index in 1...26 {
            let outwardProgress = index <= 12
                ? Double(index) : Double(24 - index)
            let direction: VisualRoomDirection = index <= 12 ? .left : .right
            final = tracker.observe(
                profile: motionProfile(visibilityCircle(
                    // Hacker truth crosses the initial view at about 0.92
                    // low-resolution cells per 50 ms capture. Keep moving
                    // through that view: the real route does not pause there.
                    fractionalMotionGrid(horizontalShift: 0.9 * outwardProgress),
                    radius: 29 - 0.45 * max(0, outwardProgress),
                    centerX: 32 - 0.25 * max(0, outwardProgress),
                    centerY: 18
                )),
                baseDecision: admitted,
                horizontalInput: direction,
                knightRect: CGRect(x: 300, y: 150, width: 40, height: 40),
                frameSize: size,
                currentWorldPose: start,
                solveWidth: 640,
                timestamp: Double(index) * 0.05,
                groundTrackingReliable: false
            )
            XCTAssertEqual(final.roomID, 0)
            XCTAssertFalse(final.isTransitioning)
            if index > 12, let match = final.coarsePlaceMatch {
                returnPlaceMatch = match
            }
        }

        XCTAssertEqual(final.revision, 0)
        XCTAssertTrue(final.portals.isEmpty)
        XCTAssertEqual(returnPlaceMatch?.keyframeID, 1)
        XCTAssertEqual(returnPlaceMatch?.cameraPosition.x ?? .nan, 110, accuracy: 0.001)
        // Recognition happens while the camera keeps moving. The reanchored
        // pose must therefore include the following 50 ms of measured travel,
        // instead of freezing or snapping back to the saved keyframe.
        XCTAssertEqual(final.coarseWorldPose?.x ?? .nan, 118, accuracy: 2)
        XCTAssertEqual(final.coarseWorldPose?.y ?? .nan, start.y, accuracy: 2)

        traverse(
            tracker,
            direction: .left,
            edge: leftKnight,
            pose: start,
            start: 2
        )
        let placed = tracker.snapshot
        XCTAssertEqual(placed.roomID, 1)
        XCTAssertLessThan(placed.entryWorldPose.x, start.x)
        XCTAssertEqual(placed.portals.count, 1)
        XCTAssertEqual(placed.portals.first?.leftRoomID, 1)
        XCTAssertEqual(placed.portals.first?.rightRoomID, 0)
    }

    func testLowResolutionPlaceMemoryReanchorsRepeatedKnownViewWithinRoom() {
        var memory = LowResolutionPlaceMemory()
        memory.remember(
            grid: fractionalMotionGrid(horizontalShift: 0),
            roomID: 0,
            cameraPosition: CGPoint(x: 100, y: 40),
            timestamp: 0
        )
        let returned = fractionalMotionGrid(
            horizontalShift: 1,
            gain: 0.28
        )
        XCTAssertNil(memory.observe(
            grid: returned,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1
        ))
        let match = memory.observe(
            grid: returned,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1.2
        )
        XCTAssertEqual(match?.keyframeID, 1)
        XCTAssertEqual(match?.cameraPosition.x ?? .nan, 90, accuracy: 0.001)
        XCTAssertEqual(match?.cameraPosition.y ?? .nan, 40, accuracy: 0.001)
        XCTAssertLessThan(match?.score ?? .infinity, 0.20)
    }

    func testLowResolutionPlaceMemoryReanchorsBrightViewThroughSmallVisibilityCircle() {
        var memory = LowResolutionPlaceMemory()
        memory.remember(
            grid: fractionalMotionGrid(horizontalShift: 0),
            roomID: 0,
            cameraPosition: CGPoint(x: 100, y: 40),
            timestamp: 0
        )
        XCTAssertNil(memory.observe(
            grid: visibilityCircle(
                fractionalMotionGrid(horizontalShift: 0.9),
                radius: 10
            ),
            roomID: 0,
            solveWidth: 640,
            timestamp: 1
        ))
        let match = memory.observe(
            grid: visibilityCircle(
                fractionalMotionGrid(horizontalShift: -0.9),
                radius: 10
            ),
            roomID: 0,
            solveWidth: 640,
            timestamp: 1.1
        )

        XCTAssertEqual(match?.keyframeID, 1)
        XCTAssertEqual(match?.cameraPosition.x ?? .nan, 110, accuracy: 0.001)
        XCTAssertEqual(match?.cameraPosition.y ?? .nan, 40, accuracy: 0.001)
    }

    func testLowResolutionPlaceMemoryRejectsTinyVisibilityCircle() {
        var memory = LowResolutionPlaceMemory()
        memory.remember(
            grid: fractionalMotionGrid(horizontalShift: 0),
            roomID: 0,
            cameraPosition: .zero,
            timestamp: 0
        )
        let tiny = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 0),
            radius: 2
        )
        XCTAssertNil(memory.observe(
            grid: tiny,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1
        ))
        XCTAssertNil(memory.observe(
            grid: tiny,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1.1
        ))
    }

    func testLowResolutionPlaceMemoryRejectsUnrelatedSmallVisibilityCircle() {
        var memory = LowResolutionPlaceMemory()
        memory.remember(
            grid: fractionalMotionGrid(horizontalShift: 0),
            roomID: 0,
            cameraPosition: .zero,
            timestamp: 0
        )
        let unrelated = visibilityCircle(unrelatedMotionGrid(), radius: 10)
        XCTAssertNil(memory.observe(
            grid: unrelated,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1
        ))
        XCTAssertNil(memory.observe(
            grid: unrelated,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1.1
        ))
    }

    func testLowResolutionPlaceMemoryRejectsAmbiguousSmallVisibilityCircle() {
        var memory = LowResolutionPlaceMemory()
        let repeatedTexture = fractionalMotionGrid(horizontalShift: 0)
        memory.remember(
            grid: repeatedTexture,
            roomID: 0,
            cameraPosition: .zero,
            timestamp: 0
        )
        memory.remember(
            grid: repeatedTexture,
            roomID: 0,
            cameraPosition: CGPoint(x: 160, y: 0),
            timestamp: 0.3
        )
        let partialView = visibilityCircle(
            fractionalMotionGrid(horizontalShift: 0.9),
            radius: 10
        )

        XCTAssertNil(memory.observe(
            grid: partialView,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1
        ))
        XCTAssertNil(memory.observe(
            grid: partialView,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1.1
        ))
    }

    func testLowResolutionPlaceMemoryDoesNotCrossRoomOwnership() {
        var memory = LowResolutionPlaceMemory()
        let view = fractionalMotionGrid(horizontalShift: 0)
        memory.remember(
            grid: view,
            roomID: 0,
            cameraPosition: .zero,
            timestamp: 0
        )
        XCTAssertNil(memory.observe(
            grid: view,
            roomID: 1,
            solveWidth: 640,
            timestamp: 1
        ))
        XCTAssertNil(memory.observe(
            grid: view,
            roomID: 1,
            solveWidth: 640,
            timestamp: 1.2
        ))
    }

    func testLowResolutionPlaceMemoryRejectsUnrelatedViewAndSelectsUniqueKeyframe() {
        var memory = LowResolutionPlaceMemory()
        let original = fractionalMotionGrid(horizontalShift: 0)
        let unrelated = unrelatedMotionGrid()
        memory.remember(
            grid: original,
            roomID: 0,
            cameraPosition: .zero,
            timestamp: 0
        )
        memory.remember(
            grid: unrelated,
            roomID: 0,
            cameraPosition: CGPoint(x: 160, y: 0),
            timestamp: 0.3
        )
        XCTAssertNil(memory.observe(
            grid: unrelatedMotionGrid(seed: 97),
            roomID: 0,
            solveWidth: 640,
            timestamp: 1
        ))
        XCTAssertNil(memory.observe(
            grid: original,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1.2
        ))
        let match = memory.observe(
            grid: original,
            roomID: 0,
            solveWidth: 640,
            timestamp: 1.4
        )
        XCTAssertEqual(match?.keyframeID, 1)
        XCTAssertEqual(match?.cameraPosition, .zero)
        XCTAssertGreaterThan(match?.margin ?? 0, LowResolutionPlaceMemory.minimumMargin)
    }

    func testFloorlessTraversalPublishesRecognizedRoomOnePoseImmediately() {
        let tracker = VisualRoomTransitionTracker()
        let remembered = fractionalMotionGrid(horizontalShift: 0)
        _ = tracker.observe(
            profile: motionProfile(remembered),
            baseDecision: admitted,
            horizontalInput: nil,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 100, y: 40),
            solveWidth: 640,
            timestamp: 0,
            groundTrackingReliable: true
        )
        let returned = fractionalMotionGrid(horizontalShift: 1, gain: 0.28)
        _ = tracker.observe(
            profile: motionProfile(returned),
            baseDecision: admitted,
            horizontalInput: .right,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 500, y: 40),
            solveWidth: 640,
            timestamp: 1,
            groundTrackingReliable: false
        )
        let recognized = tracker.observe(
            profile: motionProfile(returned),
            baseDecision: admitted,
            horizontalInput: .right,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 500, y: 40),
            solveWidth: 640,
            timestamp: 1.2,
            groundTrackingReliable: false
        )
        XCTAssertEqual(recognized.roomID, 0)
        XCTAssertEqual(recognized.coarsePlaceMatch?.keyframeID, 1)
        XCTAssertEqual(recognized.coarseWorldPose?.x ?? .nan, 90, accuracy: 0.001)
        XCTAssertEqual(recognized.coarseWorldPose?.y ?? .nan, 40, accuracy: 0.001)
        XCTAssertTrue(recognized.coarseMotionIsControlling)
    }

    func testMaskedCorrectionRebasesContinuingFloorlessPoseAndRejectsStaleRepeat() {
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.observe(
            profile: motionProfile(motionGrid(shift: 0)),
            baseDecision: admitted,
            horizontalInput: nil,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: .zero,
            solveWidth: 640,
            timestamp: 0,
            groundTrackingReliable: true
        )
        let measured = tracker.observe(
            profile: motionProfile(motionGrid(shift: 2)),
            baseDecision: admitted,
            horizontalInput: .left,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: .zero,
            solveWidth: 640,
            timestamp: 0.1,
            groundTrackingReliable: false
        )
        let continued = tracker.observe(
            profile: motionProfile(motionGrid(shift: 4)),
            baseDecision: admitted,
            horizontalInput: .left,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: -40, y: 0),
            solveWidth: 640,
            timestamp: 0.2,
            groundTrackingReliable: false
        )
        XCTAssertEqual(measured.coarseWorldPose?.x ?? .nan, -40, accuracy: 0.001)
        XCTAssertEqual(continued.coarseWorldPose?.x ?? .nan, -80, accuracy: 0.001)

        XCTAssertTrue(tracker.applyFloorlessCorrection(
            CGVector(dx: 12, dy: 5),
            roomID: measured.roomID,
            coarsePoseRevision: measured.coarsePoseRevision
        ))
        XCTAssertEqual(tracker.snapshot.coarseWorldPose?.x ?? .nan, -68, accuracy: 0.001)
        XCTAssertEqual(tracker.snapshot.coarseWorldPose?.y ?? .nan, 5, accuracy: 0.001)
        XCTAssertFalse(tracker.applyFloorlessCorrection(
            CGVector(dx: 12, dy: 5),
            roomID: measured.roomID,
            coarsePoseRevision: measured.coarsePoseRevision
        ))
        XCTAssertEqual(tracker.snapshot.coarseWorldPose?.x ?? .nan, -68, accuracy: 0.001)
    }

    func testRoomOnePlaceMatchInvalidatesOlderMaskedCorrection() {
        let tracker = VisualRoomTransitionTracker()
        let remembered = fractionalMotionGrid(horizontalShift: 0)
        _ = tracker.observe(
            profile: motionProfile(remembered),
            baseDecision: admitted,
            horizontalInput: nil,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 100, y: 40),
            solveWidth: 640,
            timestamp: 0,
            groundTrackingReliable: true
        )
        let returned = fractionalMotionGrid(horizontalShift: 1, gain: 0.28)
        let beforeMatch = tracker.observe(
            profile: motionProfile(returned),
            baseDecision: admitted,
            horizontalInput: .right,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 500, y: 40),
            solveWidth: 640,
            timestamp: 1,
            groundTrackingReliable: false
        )
        let recognized = tracker.observe(
            profile: motionProfile(returned),
            baseDecision: admitted,
            horizontalInput: .right,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 500, y: 40),
            solveWidth: 640,
            timestamp: 1.2,
            groundTrackingReliable: false
        )
        XCTAssertEqual(recognized.coarseWorldPose?.x ?? .nan, 90, accuracy: 0.001)
        XCTAssertFalse(tracker.applyFloorlessCorrection(
            CGVector(dx: 100, dy: 0),
            roomID: beforeMatch.roomID,
            coarsePoseRevision: beforeMatch.coarsePoseRevision
        ))
        XCTAssertEqual(tracker.snapshot.coarseWorldPose?.x ?? .nan, 90, accuracy: 0.001)
    }

    func testFloorlessOdometryAccumulatesSlowSubcellTravelWithoutFreezing() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        for index in 1...20 {
            XCTAssertNotNil(odometry.observe(
                grid: fractionalMotionGrid(
                    horizontalShift: -0.45 * Double(index)
                ),
                timestamp: Double(index) * 0.1,
                solveWidth: 640,
                expectedDirection: .right
            ), "missing subcell step \(index)")
        }
        XCTAssertEqual(odometry.sampleCount, 20)
        XCTAssertEqual(odometry.worldDeltaX, 90, accuracy: 12)
    }

    func testLowResolutionMotionSeparatesSimultaneousVerticalTravel() {
        let reference = motionGrid(shift: 0)
        let translated = motionGrid(shift: 2, verticalShift: 2)
        let vector = LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: translated
        )
        XCTAssertEqual(vector?.screenShiftX, 2)
        XCTAssertEqual(vector?.screenShiftY, 2)
        XCTAssertEqual(
            LowResolutionRoomMotionTracker.estimate(
                from: reference,
                to: translated
            )?.direction,
            .left
        )

        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(grid: reference, timestamp: 0)
        XCTAssertEqual(odometry.observe(
            grid: translated, timestamp: 0.1,
            solveWidth: 640, expectedDirection: .left
        ), -40)
        XCTAssertEqual(odometry.worldDeltaX, -40)
        XCTAssertEqual(odometry.worldDeltaY, 40)
    }

    func testFloorlessOdometryRetainsVerticalTravelWithoutHorizontalDirection() {
        let reference = fractionalMotionGrid(horizontalShift: 0)
        let translated = fractionalMotionGrid(
            horizontalShift: 0,
            verticalShift: 2
        )
        XCTAssertNil(
            LowResolutionRoomMotionTracker.estimate(
                from: reference,
                to: translated
            ),
            "horizontal direction voting must remain absent"
        )

        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(grid: reference, timestamp: 0)
        XCTAssertNotNil(odometry.observe(
            grid: translated,
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: nil
        ))
        XCTAssertEqual(odometry.worldDeltaX, 0, accuracy: 2)
        XCTAssertEqual(odometry.worldDeltaY, 20, accuracy: 2)
    }

    func testFloorlessOdometryRetainsSmallCameraEasingAgainstHeldInput() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        XCTAssertEqual(
            odometry.observe(
                grid: fractionalMotionGrid(horizontalShift: 1.25),
                timestamp: 0.05,
                solveWidth: 640,
                expectedDirection: .right
            ) ?? .nan,
            -12.5,
            accuracy: 2
        )
    }

    func testFloorlessOdometryRejectsStrongHorizontalContradiction() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        XCTAssertNil(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: 2),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .right
        ))
        XCTAssertEqual(odometry.sampleCount, 0)
    }

    func testFloorlessOdometryRetainsVerticalDominantMotionAgainstHeldInput() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        XCTAssertNotNil(odometry.observe(
            grid: fractionalMotionGrid(
                horizontalShift: 2,
                verticalShift: 3
            ),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .right
        ))
        XCTAssertEqual(odometry.worldDeltaX, -20, accuracy: 2)
        XCTAssertEqual(odometry.worldDeltaY, 30, accuracy: 2)
    }

    func testFloorlessTraversalPublishesVerticalTravelWithoutHorizontalInput() {
        let tracker = VisualRoomTransitionTracker()
        let reference = fractionalMotionGrid(horizontalShift: 0)
        _ = tracker.observe(
            profile: motionProfile(reference),
            baseDecision: admitted,
            horizontalInput: nil,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 100, y: 40),
            solveWidth: 640,
            timestamp: 0,
            groundTrackingReliable: true
        )
        let moved = tracker.observe(
            profile: motionProfile(fractionalMotionGrid(
                horizontalShift: 0,
                verticalShift: 2
            )),
            baseDecision: admitted,
            horizontalInput: nil,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: CGPoint(x: 100, y: 40),
            solveWidth: 640,
            timestamp: 0.05,
            groundTrackingReliable: false
        )

        XCTAssertNil(moved.coarseMotionEstimate)
        XCTAssertEqual(moved.coarseWorldPose?.x ?? .nan, 100, accuracy: 2)
        XCTAssertEqual(moved.coarseWorldPose?.y ?? .nan, 60, accuracy: 2)
        XCTAssertTrue(moved.coarseMotionIsControlling)
    }

    func testRuntimeResolutionMotionRetainsFastVerticalTravel() {
        let reference = motionGrid(
            shift: 0, width: 64, height: 36
        )
        let translated = motionGrid(
            shift: -3, verticalShift: -7, width: 64, height: 36
        )
        let vector = LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: translated
        )
        XCTAssertEqual(vector?.screenShiftX, -3)
        XCTAssertEqual(vector?.screenShiftY, -7)
    }

    func testRouteCalibratedShortBaselineRetainsFastHorizontalTravel() {
        let reference = fractionalMotionGrid(horizontalShift: 0)
        let translated = fractionalMotionGrid(
            horizontalShift: -2.25,
            verticalShift: 3
        )
        let vector = LowResolutionRoomMotionTracker.translation(
            from: reference,
            to: translated
        )
        XCTAssertEqual(vector?.screenShiftX ?? .nan, -2.25, accuracy: 0.18)
        XCTAssertEqual(vector?.screenShiftY ?? .nan, 3, accuracy: 0.18)

        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(grid: reference, timestamp: 0)
        XCTAssertEqual(
            odometry.observe(
                grid: translated,
                timestamp: 0.05,
                solveWidth: 640,
                expectedDirection: .right
            ) ?? .nan,
            22.5,
            accuracy: 2
        )
        XCTAssertEqual(odometry.worldDeltaY, 30, accuracy: 2)
    }

    func testShortBaselineStillAccumulatesSlowSubcellTravel() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        for index in 1...20 {
            XCTAssertNotNil(odometry.observe(
                grid: fractionalMotionGrid(
                    horizontalShift: -0.45 * Double(index)
                ),
                timestamp: Double(index) * 0.05,
                solveWidth: 640,
                expectedDirection: .right
            ), "missing short-baseline subcell step \(index)")
        }
        XCTAssertEqual(odometry.sampleCount, 20)
        XCTAssertEqual(odometry.worldDeltaX, 90, accuracy: 12)
    }

    func testCutsceneCameraWarpIsRejectedBeforeLocalMotionResumes() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(
            grid: fractionalMotionGrid(horizontalShift: 0),
            timestamp: 0
        )
        XCTAssertNil(odometry.observe(
            grid: fractionalMotionGrid(horizontalShift: 20),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: .left
        ))
        XCTAssertEqual(odometry.sampleCount, 0)
        XCTAssertEqual(odometry.worldDeltaX, 0)

        XCTAssertEqual(
            odometry.observe(
                grid: fractionalMotionGrid(horizontalShift: 18.5),
                timestamp: 0.10,
                solveWidth: 640,
                expectedDirection: .right
            ) ?? .nan,
            15,
            accuracy: 2
        )
        XCTAssertEqual(odometry.worldDeltaX, 15, accuracy: 2)
    }

    func testDoorwayOdometryAccumulatesLowResolutionSilhouetteTravel() {
        var odometry = LowResolutionTransitionOdometry()
        odometry.arm(grid: motionGrid(shift: 0), timestamp: 0)
        XCTAssertEqual(odometry.observe(
            grid: motionGrid(shift: 2), timestamp: 0.1,
            solveWidth: 640, expectedDirection: .left
        ), -40)
        XCTAssertEqual(odometry.observe(
            grid: motionGrid(shift: 4), timestamp: 0.2,
            solveWidth: 640, expectedDirection: .left
        ), -40)
        XCTAssertEqual(odometry.travel.worldDeltaX, -80)
        XCTAssertEqual(odometry.travel.sampleCount, 2)
    }

    func testBlackBandsDefineSeparateDoorEdgesAndShorterDoorHeights() throws {
        let profile = FrameRegionRenderer.signalProfile(in: try doorwayImage())
        XCTAssertEqual(profile.motionGrid?.width, 64)
        XCTAssertEqual(profile.motionGrid?.height, 36)
        let left = try XCTUnwrap(profile.edgeBlackBands?.left)
        let right = try XCTUnwrap(profile.edgeBlackBands?.right)
        XCTAssertEqual(left.widthFraction, 0.09375, accuracy: 0.02)
        XCTAssertEqual(right.widthFraction, 0.453125, accuracy: 0.02)
        XCTAssertLessThan(
            left.contentYRangeFraction.upperBound
                - left.contentYRangeFraction.lowerBound,
            0.5
        )

        let layout = VisualRoomPortalLayout.make(
            direction: .left,
            departureWorldPose: CGPoint(x: -186, y: 0),
            solveWidth: 640,
            solveHeight: 360,
            sourceBand: left,
            targetBand: right,
            measuredTravel: nil
        )
        XCTAssertEqual(layout.sourceDoorWorldX, -126, accuracy: 13)
        XCTAssertEqual(layout.targetDoorWorldX, -142, accuracy: 13)
        XCTAssertEqual(layout.targetEntryWorldPose.x, -492, accuracy: 20)
        XCTAssertLessThan(
            layout.sourceDoorYRange.upperBound
                - layout.sourceDoorYRange.lowerBound,
            121
        )
        XCTAssertEqual(layout.sourceDoorYRange, layout.targetDoorYRange)
    }

    func testMeasuredDoorwayTravelControlsConnectorLength() {
        let source = FrameEdgeBlackBand(
            widthFraction: 0.10,
            contentYRangeFraction: 0.4...0.7
        )
        let target = FrameEdgeBlackBand(
            widthFraction: 0.45,
            contentYRangeFraction: 0.45...0.7
        )
        let layout = VisualRoomPortalLayout.make(
            direction: .left,
            departureWorldPose: .zero,
            solveWidth: 640,
            solveHeight: 360,
            sourceBand: source,
            targetBand: target,
            measuredTravel: LowResolutionTransitionTravel(
                worldDeltaX: -400,
                sampleCount: 4,
                meanConfidence: 0.7
            )
        )
        XCTAssertEqual(
            layout.sourceDoorWorldX - layout.targetDoorWorldX,
            112,
            accuracy: 0.001
        )
        XCTAssertEqual(layout.targetEntryWorldPose.x, -400, accuracy: 0.001)
    }

    func testHorizontalDoorRejectsImplausibleContinuousVerticalArrival() {
        let departure = CGPoint(x: 2_000, y: 1_000)
        let rejected = VisualRoomPortalLayout.plausibleContinuousArrival(
            CGPoint(x: 2_356, y: 2_070),
            direction: .right,
            departureWorldPose: departure,
            solveWidth: 640,
            solveHeight: 360
        )
        XCTAssertNil(rejected)

        let layout = VisualRoomPortalLayout.make(
            direction: .right,
            departureWorldPose: departure,
            solveWidth: 640,
            solveHeight: 360,
            sourceBand: nil,
            targetBand: nil,
            measuredTravel: nil,
            continuousArrivalWorldPose: rejected
        )
        XCTAssertEqual(layout.targetEntryWorldPose.y, departure.y, accuracy: 0.001)
        XCTAssertGreaterThan(layout.targetEntryWorldPose.x, departure.x)
    }

    func testHorizontalDoorAcceptsRouteCalibratedContinuousArrival() {
        let departure = CGPoint(x: 2_000, y: 1_000)
        let expected = CGPoint(x: 2_541.308, y: 944.448)
        XCTAssertEqual(
            VisualRoomPortalLayout.plausibleContinuousArrival(
                expected,
                direction: .right,
                departureWorldPose: departure,
                solveWidth: 640,
                solveHeight: 360
            ),
            expected
        )
    }

    func testKnownAppearancePortalRequiresItsRecordedDirection() {
        let tracker = VisualRoomTransitionTracker()
        let departure = CGPoint(x: -120, y: 0)
        transitionAppearance(
            tracker,
            from: visible,
            to: nearBlack,
            heldDirection: .left,
            pose: departure,
            start: 1
        )
        XCTAssertEqual(tracker.snapshot.roomID, 1)

        // A stale left input cannot traverse Room 2's recorded right exit.
        transitionAppearance(
            tracker,
            from: nearBlack,
            to: visible,
            heldDirection: .left,
            pose: tracker.snapshot.entryWorldPose,
            start: 2
        )
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.revision, 1)

        // The recorded rightward traversal returns to Room 1.
        transitionAppearance(
            tracker,
            from: nearBlack,
            to: visible,
            heldDirection: .right,
            pose: tracker.snapshot.entryWorldPose,
            start: 3
        )
        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.revision, 2)
    }

    func testReverseTraversalReusesFirstRoomAndPortalPose() {
        let tracker = VisualRoomTransitionTracker()
        let departure = CGPoint(x: -120, y: 8)
        traverse(
            tracker, direction: .left, edge: leftKnight,
            pose: departure, start: 1
        )
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        let secondRoomPose = tracker.snapshot.entryWorldPose
        let portalGeometry = tracker.snapshot.portals
        traverse(
            tracker, direction: .right, edge: rightKnight,
            pose: secondRoomPose, start: 4
        )
        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.entryWorldPose, departure)
        XCTAssertEqual(tracker.snapshot.revision, 2)
        XCTAssertEqual(tracker.snapshot.portals, portalGeometry)
    }

    func testRestartPrefersOnlyRoomRepresentedByDurableEvidence() {
        let tracker = VisualRoomTransitionTracker()
        let departure = CGPoint(x: -120, y: 8)
        traverse(
            tracker, direction: .left, edge: leftKnight,
            pose: departure, start: 1
        )
        let portalGeometry = tracker.snapshot.portals
        XCTAssertEqual(tracker.snapshot.roomID, 1)

        let restored = tracker.restoreActiveRoomFromDurableEvidence(0)

        XCTAssertEqual(restored.roomID, 0)
        XCTAssertEqual(restored.entryWorldPose, departure)
        XCTAssertEqual(restored.portals, portalGeometry)
        XCTAssertEqual(restored.revision, 1)
    }

    func testCoarseMotionDiagnosticRunsContinuouslyAwayFromDoorway() throws {
        let tracker = VisualRoomTransitionTracker()
        func profile(_ grid: LowResolutionMotionGrid) -> FrameSignalProfile {
            return FrameSignalProfile(
                sampleCount: 1_000, signaledCount: 800,
                visibleCount: 700, meanPeak: 80,
                motionGrid: grid
            )
        }
        _ = tracker.observe(
            profile: profile(motionGrid(shift: 0)), baseDecision: admitted,
            horizontalInput: nil, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1
        )
        let snapshot = tracker.observe(
            profile: profile(motionGrid(shift: 2)), baseDecision: admitted,
            horizontalInput: nil, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1
        )

        XCTAssertEqual(snapshot.coarseMotionGrid, motionGrid(shift: 2))
        XCTAssertEqual(snapshot.coarseMotionEstimate?.direction, .left)
        XCTAssertFalse(snapshot.coarseMotionIsControlling)
        let image = try XCTUnwrap(LowResolutionMotionDiagnosticRenderer.image(
            grid: try XCTUnwrap(snapshot.coarseMotionGrid),
            estimate: snapshot.coarseMotionEstimate,
            isControlling: false
        ))
        XCTAssertEqual(image.width, 32)
        XCTAssertEqual(image.height, 18)
        XCTAssertEqual(
            LowResolutionMotionDiagnosticPresentation.text(
                estimate: snapshot.coarseMotionEstimate,
                isControlling: false,
                solveWidth: 640,
                gridWidth: 32
            ),
            "Coarse Motion · ready · left 40 px · \(Int(((snapshot.coarseMotionEstimate?.confidence ?? 0) * 100).rounded()))%"
        )
    }

    func testFloorlessTraversalCarriesPoseWithoutChangingRoomAndHandsBackToGround() {
        let tracker = VisualRoomTransitionTracker()
        func profile(_ grid: LowResolutionMotionGrid) -> FrameSignalProfile {
            FrameSignalProfile(
                sampleCount: 1_000, signaledCount: 800,
                visibleCount: 700, meanPeak: 80,
                motionGrid: grid
            )
        }
        _ = tracker.observe(
            profile: profile(motionGrid(shift: 0)), baseDecision: admitted,
            horizontalInput: nil, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1,
            groundTrackingReliable: true
        )
        let travelledLeft = tracker.observe(
            profile: profile(motionGrid(shift: 2)), baseDecision: admitted,
            horizontalInput: .left, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1,
            groundTrackingReliable: false
        )

        XCTAssertEqual(travelledLeft.roomID, 0)
        XCTAssertFalse(travelledLeft.isTransitioning)
        XCTAssertTrue(travelledLeft.coarseMotionIsControlling)
        XCTAssertEqual(travelledLeft.coarseWorldPose?.x ?? .nan, -40, accuracy: 0.001)
        XCTAssertEqual(travelledLeft.coarseWorldPose?.y ?? .nan, 0, accuracy: 0.001)

        let returned = tracker.observe(
            profile: profile(motionGrid(shift: 0)), baseDecision: admitted,
            horizontalInput: .right, knightRect: nil, frameSize: size,
            currentWorldPose: CGPoint(x: -40, y: 0), solveWidth: 640, timestamp: 1.2,
            groundTrackingReliable: false
        )
        XCTAssertEqual(returned.roomID, 0)
        XCTAssertEqual(returned.coarseWorldPose?.x ?? .nan, 0, accuracy: 0.001)

        let reacquired = tracker.observe(
            profile: profile(motionGrid(shift: 0)), baseDecision: admitted,
            horizontalInput: nil, knightRect: nil, frameSize: size,
            currentWorldPose: CGPoint(x: 1, y: 3), solveWidth: 640, timestamp: 1.3,
            groundTrackingReliable: true
        )
        XCTAssertNil(reacquired.coarseWorldPose)
        XCTAssertFalse(reacquired.coarseMotionIsControlling)
        XCTAssertEqual(reacquired.roomID, 0)
    }

    func testFloorlessTraversalKeepsDoorwayDepartureAtLastGroundAnchor() {
        let tracker = VisualRoomTransitionTracker()
        func profile(
            mean: Double,
            visibleCount: Int,
            grid: LowResolutionMotionGrid
        ) -> FrameSignalProfile {
            let band = FrameEdgeBlackBand(
                widthFraction: 0.45,
                contentYRangeFraction: 0.35...0.65
            )
            return FrameSignalProfile(
                sampleCount: 1_000,
                signaledCount: mean < 18 ? 80 : 800,
                visibleCount: visibleCount,
                meanPeak: mean,
                motionGrid: grid,
                edgeBlackBands: FrameEdgeBlackBands(left: band, right: band)
            )
        }
        _ = tracker.observe(
            profile: profile(mean: 80, visibleCount: 700, grid: motionGrid(shift: 0)),
            baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1,
            groundTrackingReliable: true
        )
        _ = tracker.observe(
            profile: profile(mean: 80, visibleCount: 700, grid: motionGrid(shift: 2)),
            baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1,
            groundTrackingReliable: false
        )
        let pending = tracker.observe(
            profile: profile(mean: 8, visibleCount: 60, grid: motionGrid(shift: 4)),
            baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: CGPoint(x: -40, y: 0), solveWidth: 640, timestamp: 1.2,
            groundTrackingReliable: false
        )
        XCTAssertTrue(pending.isTransitioning)

        for index in 1..<VisualRoomTransitionTracker.appearanceStableFrames {
            _ = tracker.observe(
                profile: profile(mean: 8, visibleCount: 60, grid: motionGrid(shift: 4)),
                baseDecision: admitted,
                horizontalInput: .left, knightRect: rightKnight, frameSize: size,
                currentWorldPose: CGPoint(x: -80, y: 0), solveWidth: 640,
                timestamp: 1.2 + Double(index) / 60,
                groundTrackingReliable: false
            )
        }

        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.entryWorldPose.x, -80, accuracy: 0.001)
        XCTAssertEqual(tracker.snapshot.activationWorldPose?.x ?? .nan, -80, accuracy: 0.001)
    }

    func testOppositeTraversalCannotUseKnownLeftPortalToEnterRoomTwo() {
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.bootstrapTwoRoomTopology(
            leftRoomID: 1,
            rightRoomID: 0,
            leftDoorWorldX: -8,
            rightDoorWorldX: 0,
            leftDoorYRange: 100...180,
            rightDoorYRange: 100...180,
            leftEntryWorldPose: CGPoint(x: -648, y: 0),
            rightEntryWorldPose: .zero,
            activeRoomID: 0
        )
        _ = tracker.observe(
            profile: visible, baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1
        )
        _ = tracker.observe(
            profile: nearBlack, baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1
        )
        for index in 0..<VisualRoomTransitionTracker.appearanceStableFrames {
            _ = tracker.observe(
                profile: nearBlack, baseDecision: admitted,
                horizontalInput: .right, knightRect: rightKnight, frameSize: size,
                currentWorldPose: .zero, solveWidth: 640,
                timestamp: 1.2 + Double(index) / 60
            )
        }

        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.revision, 1)
        XCTAssertFalse(tracker.snapshot.isTransitioning)
    }

    func testKnownRoomActivationKeepsContinuousPoseInsteadOfSnappingToStoredEntry() {
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.bootstrapTwoRoomTopology(
            leftRoomID: 1,
            rightRoomID: 0,
            leftDoorWorldX: -8,
            rightDoorWorldX: 0,
            leftDoorYRange: 100...180,
            rightDoorYRange: 100...180,
            leftEntryWorldPose: CGPoint(x: -648, y: 0),
            rightEntryWorldPose: .zero,
            activeRoomID: 0
        )
        let band = FrameEdgeBlackBand(
            widthFraction: 0.45,
            contentYRangeFraction: 0.35...0.65
        )
        func profile(
            mean: Double,
            visibleCount: Int,
            shift: Int
        ) -> FrameSignalProfile {
            FrameSignalProfile(
                sampleCount: 1_000,
                signaledCount: mean < 18 ? 80 : 800,
                visibleCount: visibleCount,
                meanPeak: mean,
                motionGrid: motionGrid(shift: shift),
                edgeBlackBands: FrameEdgeBlackBands(left: band, right: band)
            )
        }
        _ = tracker.observe(
            profile: profile(mean: 80, visibleCount: 700, shift: 0),
            baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1,
            groundTrackingReliable: true
        )
        _ = tracker.observe(
            profile: profile(mean: 80, visibleCount: 700, shift: 2),
            baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1,
            groundTrackingReliable: false
        )
        _ = tracker.observe(
            profile: profile(mean: 8, visibleCount: 60, shift: 4),
            baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: CGPoint(x: -40, y: 0), solveWidth: 640, timestamp: 1.2,
            groundTrackingReliable: false
        )
        for index in 1...2 {
            _ = tracker.observe(
                profile: profile(mean: 8, visibleCount: 60, shift: 4),
                baseDecision: admitted,
                horizontalInput: .left, knightRect: rightKnight, frameSize: size,
                currentWorldPose: CGPoint(x: -80, y: 0), solveWidth: 640,
                timestamp: 1.2 + Double(index) * 0.1,
                groundTrackingReliable: false
            )
        }

        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.entryWorldPose.x, -648, accuracy: 0.001)
        XCTAssertEqual(tracker.snapshot.activationWorldPose?.x ?? .nan, -80, accuracy: 0.001)
    }

    func testNearBlackFadeCanTransitionWithoutZeroSignalFrame() {
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1
        )
        let pending = tracker.observe(
            profile: doorwayProfile(nearBlack, shift: 4), baseDecision: admitted,
            horizontalInput: .left, knightRect: leftKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1
        )
        XCTAssertTrue(pending.isTransitioning)
        for index in 1..<VisualRoomTransitionTracker.appearanceStableFrames {
            _ = tracker.observe(
                profile: doorwayProfile(nearBlack, shift: 4), baseDecision: admitted,
                horizontalInput: .left, knightRect: rightKnight, frameSize: size,
                currentWorldPose: .zero, solveWidth: 640,
                timestamp: 1.1 + Double(index) / 60
            )
        }
        XCTAssertEqual(tracker.snapshot.roomID, 1)
    }

    func testRemainingInDarkRoomCannotCreateAnotherRoom() {
        let tracker = VisualRoomTransitionTracker()
        transitionAppearance(
            tracker, from: visible, to: nearBlack,
            heldDirection: .left, pose: .zero, start: 1
        )
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        for index in 0..<120 {
            _ = tracker.observe(
                profile: nearBlack, baseDecision: admitted,
                horizontalInput: .left, knightRect: leftKnight, frameSize: size,
                currentWorldPose: CGPoint(x: -700, y: 0), solveWidth: 640,
                timestamp: 2 + Double(index) / 60
            )
        }
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.revision, 1)
    }

    func testBrightDarkBoundaryReusesRoomOnReturn() {
        let tracker = VisualRoomTransitionTracker()
        let departure = CGPoint(x: -180, y: 0)
        transitionAppearance(
            tracker, from: visible, to: nearBlack,
            heldDirection: .left, pose: departure, start: 1
        )
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        let darkPose = tracker.snapshot.entryWorldPose
        transitionAppearance(
            tracker, from: nearBlack, to: visible,
            heldDirection: .right, pose: darkPose, start: 2
        )
        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.entryWorldPose, departure)
        XCTAssertEqual(tracker.snapshot.revision, 2)
    }

    func testOrdinaryCameraPanAtBoundaryDoesNotSplitRoom() {
        let tracker = VisualRoomTransitionTracker()
        for index in 0..<30 {
            _ = tracker.observe(
                profile: visible, baseDecision: admitted,
                horizontalInput: index < 15 ? .left : .right,
                knightRect: index < 15 ? leftKnight : rightKnight,
                frameSize: size,
                currentWorldPose: CGPoint(x: CGFloat(-index * 12), y: 0),
                solveWidth: 640, timestamp: Double(index) / 30
            )
        }
        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.revision, 0)
    }

    func testDirectionalDarkeningWithoutDepartureEdgeRemainsCurrentRoom() {
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: .right, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1
        )
        let pending = tracker.observe(
            profile: doorwayProfile(nearBlack, shift: -4),
            baseDecision: admitted,
            horizontalInput: .right, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1
        )
        XCTAssertFalse(pending.isTransitioning)

        for index in 0..<VisualRoomTransitionTracker.arrivalStableFrames {
            _ = tracker.observe(
                profile: doorwayProfile(visible, shift: -4),
                baseDecision: admitted,
                horizontalInput: .right, knightRect: leftKnight,
                frameSize: size, currentWorldPose: .zero, solveWidth: 640,
                timestamp: 1.2 + Double(index) / 60
            )
        }

        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.revision, 0)
        XCTAssertTrue(tracker.snapshot.portals.isEmpty)
    }

    func testShrinkingVisibilityWhileKnightCenteredNeverSuspendsRoomTracking() {
        let tracker = VisualRoomTransitionTracker()
        let centeredKnight = CGRect(x: 230, y: 100, width: 40, height: 50)
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: .right, knightRect: centeredKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1
        )

        for index in 0..<120 {
            let snapshot = tracker.observe(
                profile: doorwayProfile(nearBlack, shift: -4),
                baseDecision: admitted,
                horizontalInput: .right, knightRect: centeredKnight,
                frameSize: size,
                currentWorldPose: CGPoint(x: CGFloat(index * 3), y: 0),
                solveWidth: 640,
                timestamp: 1.1 + Double(index) / 60
            )
            XCTAssertFalse(snapshot.isTransitioning)
            XCTAssertEqual(snapshot.roomID, 0)
        }
        XCTAssertEqual(tracker.snapshot.revision, 0)
        XCTAssertTrue(tracker.snapshot.portals.isEmpty)
    }

    func testFadeAndOppositeArrivalCanCreateRoomWithoutTextureMotion() {
        let tracker = VisualRoomTransitionTracker()
        let departureKnight = CGRect(x: 480, y: 100, width: 40, height: 50)
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: .right, knightRect: departureKnight, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1
        )
        let pending = tracker.observe(
            profile: doorwayProfile(nearBlack, shift: 0),
            baseDecision: admitted,
            horizontalInput: .right, knightRect: nil, frameSize: size,
            currentWorldPose: .zero, solveWidth: 640, timestamp: 1.1
        )
        XCTAssertTrue(pending.isTransitioning)

        for index in 0..<VisualRoomTransitionTracker.arrivalStableFrames {
            _ = tracker.observe(
                profile: doorwayProfile(visible, shift: 0),
                baseDecision: admitted,
                horizontalInput: .right, knightRect: leftKnight,
                frameSize: size, currentWorldPose: .zero, solveWidth: 640,
                timestamp: 1.2 + Double(index) / 60
            )
        }

        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.revision, 1)
        XCTAssertEqual(tracker.snapshot.portals.count, 1)
        XCTAssertEqual(
            tracker.snapshot.activationWorldPose?.x ?? .nan,
            640 * VisualRoomTransitionTracker.fadeFallbackDepartureFraction
                - leftKnight.midX,
            accuracy: 0.001
        )
    }

    func testFadePlacesArrivalFromKnightScreenPositionAcrossRooms() {
        let tracker = VisualRoomTransitionTracker()
        let sourcePose = CGPoint(x: 100, y: 200)
        let sourceKnight = CGRect(x: 480, y: 60, width: 40, height: 40)
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: .right, knightRect: sourceKnight, frameSize: size,
            currentWorldPose: sourcePose, solveWidth: 640, timestamp: 1
        )
        _ = tracker.observe(
            profile: doorwayProfile(nearBlack, shift: 0),
            baseDecision: admitted,
            horizontalInput: .right, knightRect: nil, frameSize: size,
            currentWorldPose: sourcePose, solveWidth: 640, timestamp: 1.1
        )

        for index in 0..<VisualRoomTransitionTracker.arrivalStableFrames {
            _ = tracker.observe(
                profile: doorwayProfile(visible, shift: 0),
                baseDecision: admitted,
                horizontalInput: .right, knightRect: leftKnight,
                frameSize: size, currentWorldPose: sourcePose, solveWidth: 640,
                timestamp: 1.2 + Double(index) / 60
            )
        }

        let expected = CGPoint(
            x: sourcePose.x
                + 640 * VisualRoomTransitionTracker.fadeFallbackDepartureFraction
                - leftKnight.midX,
            y: sourcePose.y + sourceKnight.midY - leftKnight.midY
        )
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.entryWorldPose.x, expected.x, accuracy: 0.001)
        XCTAssertEqual(tracker.snapshot.entryWorldPose.y, expected.y, accuracy: 0.001)
        XCTAssertEqual(
            tracker.snapshot.activationWorldPose?.x ?? .nan,
            expected.x, accuracy: 0.001
        )
        XCTAssertEqual(
            tracker.snapshot.activationWorldPose?.y ?? .nan,
            expected.y, accuracy: 0.001
        )
    }

    func testRouteCalibratedFadeJoinsTownAtHackerProjectedOffset() {
        let tracker = VisualRoomTransitionTracker()
        let sourcePose = CGPoint(x: 2_000, y: 1_000)
        // Exact projections immediately around the Tutorial_01 -> Town scene
        // change in replay 8, scaled from 1920x1080 to the 640x360 solve.
        let departure = CGPoint(
            x: 1_643.8387451171875 / 3,
            y: 200.51010131835938 / 3
        )
        let arrival = CGPoint(
            x: 19.91363525390625 / 3,
            y: 367.1662292480469 / 3
        )
        func knight(at point: CGPoint) -> CGRect {
            CGRect(x: point.x - 20, y: point.y - 20, width: 40, height: 40)
        }
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0),
            baseDecision: admitted,
            horizontalInput: .right,
            knightRect: knight(at: departure),
            frameSize: size,
            currentWorldPose: sourcePose,
            solveWidth: 640,
            timestamp: 1
        )
        _ = tracker.observe(
            profile: doorwayProfile(nearBlack, shift: 0),
            baseDecision: admitted,
            horizontalInput: .right,
            knightRect: nil,
            frameSize: size,
            currentWorldPose: sourcePose,
            solveWidth: 640,
            timestamp: 1.1
        )
        for index in 0..<VisualRoomTransitionTracker.arrivalStableFrames {
            _ = tracker.observe(
                profile: doorwayProfile(visible, shift: 0),
                baseDecision: admitted,
                horizontalInput: .right,
                knightRect: knight(at: arrival),
                frameSize: size,
                currentWorldPose: sourcePose,
                solveWidth: 640,
                timestamp: 1.2 + Double(index) / 60
            )
        }

        let expected = CGPoint(
            x: sourcePose.x + departure.x - arrival.x,
            y: sourcePose.y + departure.y - arrival.y
        )
        XCTAssertEqual(tracker.snapshot.roomID, 1)
        XCTAssertEqual(tracker.snapshot.entryWorldPose.x, expected.x, accuracy: 0.001)
        XCTAssertEqual(tracker.snapshot.entryWorldPose.y, expected.y, accuracy: 0.001)
        XCTAssertEqual(expected.x - sourcePose.x, 541.308, accuracy: 0.01)
        XCTAssertEqual(expected.y - sourcePose.y, -55.552, accuracy: 0.01)
    }

    func testStableDarkeningDuringSameRoomTravelCannotCreateDoorway() {
        let tracker = VisualRoomTransitionTracker()
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: .right, knightRect: rightKnight, frameSize: size,
            currentWorldPose: CGPoint(x: 877, y: -352),
            solveWidth: 640, timestamp: 1
        )
        for index in 0..<(VisualRoomTransitionTracker.appearanceStableFrames + 8) {
            _ = tracker.observe(
                profile: doorwayProfile(nearBlack, shift: -4),
                baseDecision: admitted,
                horizontalInput: .right,
                // Continuous same-room travel never makes the Knight reappear
                // at the opposite edge, even if lighting and edge bands change.
                knightRect: rightKnight,
                frameSize: size,
                currentWorldPose: CGPoint(x: 880 + CGFloat(index * 3), y: -352),
                solveWidth: 640,
                timestamp: 1.1 + Double(index) / 60
            )
        }

        XCTAssertEqual(tracker.snapshot.roomID, 0)
        XCTAssertEqual(tracker.snapshot.revision, 0)
        XCTAssertTrue(tracker.snapshot.portals.isEmpty)
    }

    private func traverse(
        _ tracker: VisualRoomTransitionTracker,
        direction: VisualRoomDirection,
        edge: CGRect,
        pose: CGPoint,
        start: Double
    ) {
        _ = tracker.observe(
            profile: doorwayProfile(visible, shift: 0), baseDecision: admitted,
            horizontalInput: direction, knightRect: edge, frameSize: size,
            currentWorldPose: pose, solveWidth: 640, timestamp: start
        )
        _ = tracker.observe(
            profile: doorwayProfile(
                black, shift: direction == .left ? 4 : -4
            ), baseDecision: held,
            horizontalInput: direction, knightRect: nil, frameSize: size,
            currentWorldPose: pose, solveWidth: 640, timestamp: start + 0.1
        )
        for index in 0..<3 {
            _ = tracker.observe(
                profile: doorwayProfile(
                    visible, shift: direction == .left ? 4 : -4
                ), baseDecision: index == 2 ? resumed : held,
                horizontalInput: nil,
                knightRect: direction == .left ? rightKnight : leftKnight,
                frameSize: size,
                currentWorldPose: pose, solveWidth: 640,
                timestamp: start + 0.2 + Double(index) * 0.1
            )
        }
    }

    private func transitionAppearance(
        _ tracker: VisualRoomTransitionTracker,
        from source: FrameSignalProfile,
        to target: FrameSignalProfile,
        heldDirection: VisualRoomDirection,
        pose: CGPoint,
        start: Double
    ) {
        _ = tracker.observe(
            profile: doorwayProfile(source, shift: 0), baseDecision: admitted,
            horizontalInput: heldDirection,
            knightRect: heldDirection == .left ? leftKnight : rightKnight,
            frameSize: size,
            currentWorldPose: pose, solveWidth: 640, timestamp: start
        )
        for index in 0..<VisualRoomTransitionTracker.appearanceStableFrames {
            let departureEdge = heldDirection == .left ? leftKnight : rightKnight
            let arrivalEdge = heldDirection == .left ? rightKnight : leftKnight
            _ = tracker.observe(
                profile: doorwayProfile(
                    target, shift: heldDirection == .left ? 4 : -4
                ), baseDecision: admitted,
                horizontalInput: heldDirection,
                knightRect: index == 0 ? departureEdge : arrivalEdge,
                frameSize: size,
                currentWorldPose: pose, solveWidth: 640,
                timestamp: start + 0.1 + Double(index) / 60
            )
        }
    }

    private func motionGrid(
        shift: Int,
        verticalShift: Int = 0,
        gain: Double = 1,
        width: Int = 32,
        height: Int = 18
    ) -> LowResolutionMotionGrid {
        var pixels = [UInt8](repeating: 0, count: width * height)
        for y in 0..<height {
            for x in 0..<width {
                let sourceX = x - shift
                let sourceY = y - verticalShift
                guard sourceX >= 0, sourceX < width,
                      sourceY >= 0, sourceY < height else { continue }
                let value = (sourceX * 37 + sourceY * 61 + sourceX * sourceY * 3)
                    % 211 + 22
                pixels[y * width + x] = UInt8(
                    max(0, min(255, Int((Double(value) * gain).rounded())))
                )
            }
        }
        return LowResolutionMotionGrid(width: width, height: height, luma: pixels)
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
                pixels[y * width + x] = UInt8(max(0, min(255, Int(value.rounded()))))
            }
        }
        return LowResolutionMotionGrid(width: width, height: height, luma: pixels)
    }

    private func unrelatedMotionGrid(
        seed: Int = 31,
        width: Int = 64,
        height: Int = 36
    ) -> LowResolutionMotionGrid {
        let pixels = (0..<(width * height)).map { index -> UInt8 in
            let x = index % width
            let y = index / width
            return UInt8((x * seed + y * 73 + x * y * 11 + seed * 3) & 0xff)
        }
        return LowResolutionMotionGrid(width: width, height: height, luma: pixels)
    }

    private func visibilityCircle(
        _ grid: LowResolutionMotionGrid,
        radius: Double,
        centerX: Double? = nil,
        centerY: Double? = nil
    ) -> LowResolutionMotionGrid {
        let centerX = centerX ?? Double(grid.width) * 0.5
        let centerY = centerY ?? Double(grid.height) * 0.5
        let feather = 3.0
        let pixels = grid.luma.enumerated().map { index, value -> UInt8 in
            let x = Double(index % grid.width) + 0.5
            let y = Double(index / grid.width) + 0.5
            let distance = hypot(x - centerX, y - centerY)
            let visibility = max(0.04, min(1, (radius + feather - distance) / feather))
            return UInt8((Double(value) * visibility).rounded())
        }
        return LowResolutionMotionGrid(
            width: grid.width,
            height: grid.height,
            luma: pixels
        )
    }

    private func doorwayProfile(
        _ base: FrameSignalProfile,
        shift: Int
    ) -> FrameSignalProfile {
        let band = FrameEdgeBlackBand(
            widthFraction: 0.45,
            contentYRangeFraction: 0.35...0.65
        )
        return FrameSignalProfile(
            sampleCount: base.sampleCount,
            signaledCount: base.signaledCount,
            visibleCount: base.visibleCount,
            meanPeak: base.meanPeak,
            motionGrid: motionGrid(shift: shift),
            edgeBlackBands: FrameEdgeBlackBands(left: band, right: band)
        )
    }

    private func motionProfile(
        _ grid: LowResolutionMotionGrid
    ) -> FrameSignalProfile {
        FrameSignalProfile(
            sampleCount: grid.luma.count,
            signaledCount: grid.luma.count,
            visibleCount: grid.luma.count,
            meanPeak: 50,
            motionGrid: grid
        )
    }

    private func doorwayImage() throws -> CGImage {
        let width = 640, height = 360
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 70..<290 {
            for x in 60..<350 {
                let offset = (y * width + x) * 4
                pixels[offset] = 24
                pixels[offset + 1] = 52
                pixels[offset + 2] = 96
                pixels[offset + 3] = 255
            }
        }
        return try XCTUnwrap(CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )?.makeImage())
    }

    private var leftKnight: CGRect { CGRect(x: 10, y: 100, width: 40, height: 50) }
    private var rightKnight: CGRect { CGRect(x: 590, y: 100, width: 40, height: 50) }
    private var visible: FrameSignalProfile {
        FrameSignalProfile(sampleCount: 1_000, signaledCount: 800, visibleCount: 700, meanPeak: 80)
    }
    private var nearBlack: FrameSignalProfile {
        FrameSignalProfile(sampleCount: 1_000, signaledCount: 80, visibleCount: 60, meanPeak: 8)
    }
    private var black: FrameSignalProfile {
        FrameSignalProfile(sampleCount: 1_000, signaledCount: 0, visibleCount: 0, meanPeak: 0)
    }
    private var admitted: TransitionFrameDecision {
        TransitionFrameDecision(admitsTracking: true, resumedAfterTransition: false)
    }
    private var held: TransitionFrameDecision {
        TransitionFrameDecision(admitsTracking: false, resumedAfterTransition: false)
    }
    private var resumed: TransitionFrameDecision {
        TransitionFrameDecision(admitsTracking: true, resumedAfterTransition: true)
    }
}
