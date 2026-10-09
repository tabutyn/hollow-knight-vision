import CoreGraphics
import CoreImage
import CoreVideo
import Vision
import XCTest
@testable import HollowKnightVision

final class CameraSolverTests: XCTestCase {
    func testRejectedFastFallCannotReceiveAtlasWriteAdmission() {
        let rejected = CameraUpdate(
            state: .heldSceneChange,
            rawStep: CGVector(dx: 0, dy: 120),
            acceptedStep: .zero,
            position: CGPoint(x: 40, y: 10)
        )
        let accepted = CameraUpdate(
            state: .accepted,
            rawStep: CGVector(dx: 2, dy: 1),
            acceptedStep: CGVector(dx: 2, dy: 1),
            position: CGPoint(x: 42, y: 11)
        )

        XCTAssertTrue(LiveRegistrationAdmission.requiresRecovery(rejected))
        XCTAssertFalse(LiveRegistrationAdmission.canWriteAtlas(rejected, worldState: .tracking))
        XCTAssertFalse(LiveRegistrationAdmission.canWriteAtlas(accepted, worldState: .recovering))
        XCTAssertTrue(LiveRegistrationAdmission.canWriteAtlas(accepted, worldState: .tracking))
    }
    func testIsolatedWeakRegistrationDoesNotDiscardVerifiedAtlasPlacement() {
        var continuity = LiveRegistrationContinuity()
        let weak = CameraUpdate(
            state: .heldLowConfidence, rawStep: .zero, acceptedStep: .zero,
            position: .zero
        )
        let accepted = CameraUpdate(
            state: .accepted, rawStep: .zero, acceptedStep: .zero,
            position: .zero
        )
        XCTAssertFalse(continuity.shouldEnterRecovery(after: weak, hasGameplaySignal: true))
        XCTAssertFalse(continuity.shouldEnterRecovery(after: nil, hasGameplaySignal: true))
        XCTAssertFalse(continuity.shouldEnterRecovery(after: accepted, hasGameplaySignal: true))
        XCTAssertEqual(continuity.consecutiveSoftFailures, 0)
        XCTAssertFalse(continuity.shouldEnterRecovery(after: weak, hasGameplaySignal: true))
        XCTAssertFalse(continuity.shouldEnterRecovery(after: weak, hasGameplaySignal: true))
        XCTAssertTrue(continuity.shouldEnterRecovery(after: weak, hasGameplaySignal: true))
    }
    func testHardSceneJumpStillEntersRecoveryImmediately() {
        var continuity = LiveRegistrationContinuity()
        let rejected = CameraUpdate(
            state: .heldSceneChange, rawStep: CGVector(dx: 0, dy: 120),
            acceptedStep: .zero, position: .zero
        )
        XCTAssertTrue(continuity.shouldEnterRecovery(after: rejected, hasGameplaySignal: true))
        continuity.reset()
        XCTAssertFalse(continuity.shouldEnterRecovery(after: nil, hasGameplaySignal: false))
        XCTAssertFalse(continuity.shouldEnterRecovery(after: nil, hasGameplaySignal: false))
        XCTAssertTrue(continuity.shouldEnterRecovery(after: nil, hasGameplaySignal: false))
    }
    func testFreshAtlasWaitsForContinuousGroundPoseBeforeFirstWrite() {
        var gate = FreshAtlasBootstrapGate()
        for index in 0..<(FreshAtlasBootstrapGate.requiredStableSamples - 1) {
            XCTAssertFalse(gate.allowsFirstAtlasWrite(
                position: CGPoint(x: CGFloat(index % 3) - 1, y: CGFloat(index % 2)),
                registrationAccepted: true,
                groundVerified: true,
                hasCommittedEvidence: false
            ))
        }
        XCTAssertTrue(gate.allowsFirstAtlasWrite(
            position: .zero,
            registrationAccepted: true,
            groundVerified: true,
            hasCommittedEvidence: false
        ))
        XCTAssertTrue(gate.isReady)
    }
    func testFreshAtlasBootstrapJumpRestartsStabilityWindow() {
        var gate = FreshAtlasBootstrapGate()
        for index in 0..<12 {
            _ = gate.allowsFirstAtlasWrite(
                position: CGPoint(x: CGFloat(index % 2), y: 0),
                registrationAccepted: true,
                groundVerified: true,
                hasCommittedEvidence: false
            )
        }
        XCTAssertFalse(gate.allowsFirstAtlasWrite(
            position: CGPoint(x: 80, y: 30),
            registrationAccepted: true,
            groundVerified: true,
            hasCommittedEvidence: false
        ))
        XCTAssertEqual(gate.stableSampleCount, 1)
        for index in 1..<FreshAtlasBootstrapGate.requiredStableSamples {
            let allowed = gate.allowsFirstAtlasWrite(
                position: CGPoint(x: 80 + CGFloat(index % 2), y: 30),
                registrationAccepted: true,
                groundVerified: true,
                hasCommittedEvidence: false
            )
            XCTAssertEqual(allowed, index == FreshAtlasBootstrapGate.requiredStableSamples - 1)
        }
    }
    func testFreshAtlasContinuousDriftNeverBecomesInitialEvidence() {
        var gate = FreshAtlasBootstrapGate()
        for index in 0..<(FreshAtlasBootstrapGate.requiredStableSamples * 2) {
            XCTAssertFalse(gate.allowsFirstAtlasWrite(
                position: CGPoint(x: CGFloat(index), y: 0),
                registrationAccepted: true,
                groundVerified: true,
                hasCommittedEvidence: false
            ))
        }
        XCTAssertFalse(gate.isReady)
        XCTAssertLessThanOrEqual(
            gate.stableSampleCount,
            Int(FreshAtlasBootstrapGate.maximumBootstrapDisplacement) + 1
        )
    }
    func testCommittedAtlasBypassesBootstrapDelay() {
        var gate = FreshAtlasBootstrapGate()
        XCTAssertTrue(gate.allowsFirstAtlasWrite(
            position: nil,
            registrationAccepted: false,
            groundVerified: false,
            hasCommittedEvidence: true
        ))
    }
    func testGameWindowSelectionPrefersGameplayWindowOverUnityHelper() {
        let candidates = [
            GameWindowCandidate(title: "Window", size: CGSize(width: 66, height: 20)),
            GameWindowCandidate(title: "Hollow Knight", size: CGSize(width: 735, height: 838)),
        ]

        XCTAssertEqual(GameWindowSelection.bestIndex(in: candidates), 1)
    }

    func testGameWindowSelectionFallsBackToLargestWindow() {
        let candidates = [
            GameWindowCandidate(title: "", size: CGSize(width: 320, height: 240)),
            GameWindowCandidate(title: "Game", size: CGSize(width: 1280, height: 720)),
        ]

        XCTAssertEqual(GameWindowSelection.bestIndex(in: candidates), 1)
    }

    func testGameControlForwarderAcceptsOnlyRequestedKeys() {
        XCTAssertEqual(GameControlForwarder.supportedKeyCodes, [0, 6, 7, 34, 35, 123, 124, 125, 126])
        XCTAssertTrue(GameControlForwarder.supportedKeyCodes.contains(0))
        XCTAssertTrue(GameControlForwarder.menuShortcutKeyCodes.contains(34))
        XCTAssertTrue(GameControlForwarder.menuShortcutKeyCodes.contains(35))
        XCTAssertFalse(GameControlForwarder.supportedKeyCodes.contains(49))
    }

    func testGameKeyRelayForwardsSupportedPressAndRelease() {
        XCTAssertEqual(GameKeyRelay.action(type: .keyDown, keyCode: 124), .forward)
        XCTAssertEqual(GameKeyRelay.action(type: .keyUp, keyCode: 124), .forward)
        XCTAssertEqual(GameKeyRelay.action(type: .keyDown, keyCode: 0), .forward)
        XCTAssertEqual(GameKeyRelay.action(type: .keyDown, keyCode: 34), .forward)
        XCTAssertEqual(GameKeyRelay.action(type: .keyDown, keyCode: 35), .forward)
        XCTAssertEqual(
            GameKeyRelay.action(
                type: .keyDown,
                keyCode: 34,
                supportedKeyCodes: GameControlForwarder.gameplayKeyCodes
            ),
            .pass
        )
        XCTAssertEqual(
            GameKeyRelay.action(type: .keyDown, keyCode: 35, inputSuppressed: true),
            .pass
        )
        XCTAssertEqual(
            GameKeyRelay.action(type: .keyDown, keyCode: 7, modifierFlags: .command),
            .pass
        )
        XCTAssertEqual(
            GameKeyRelay.action(type: .keyUp, keyCode: 7, modifierFlags: .command),
            .forward
        )
    }

    func testModelReviewNavigationAndExitAreReservedFromGameRelay() {
        XCTAssertEqual(
            GameControlForwarder.reviewNavigationKeyCodes,
            [7, 123, 124, 125, 126]
        )
        let reviewSupported = GameControlForwarder.supportedKeyCodes.subtracting(
            GameControlForwarder.reviewNavigationKeyCodes
        )

        for keyCode in GameControlForwarder.reviewNavigationKeyCodes {
            XCTAssertEqual(
                GameKeyRelay.action(
                    type: .keyDown,
                    keyCode: keyCode,
                    supportedKeyCodes: reviewSupported
                ),
                .pass
            )
        }
    }

    func testViewportModeChangesOnlyThroughExplicitFraming() {
        let coordinator = LayerSceneViewport.Coordinator()
        coordinator.setFraming(.freeFly, request: 0, in: CGSize(width: 800, height: 600), at: 0)
        XCTAssertEqual(coordinator.framingMode, .freeFly)
        coordinator.advanceViewportTransition(at: 1)
        XCTAssertEqual(coordinator.framingMode, .freeFly)
        coordinator.setFraming(.current, request: 1, in: CGSize(width: 800, height: 600), at: 2)
        XCTAssertEqual(coordinator.framingMode, .current)
    }

    func testHUDRegionsDoNotOmitAbsentPrompt() {
        let regions = SceneRegions.hud(
            in: CGRect(x: 0, y: 0, width: 640, height: 360),
            knight: CGRect(x: 280, y: 120, width: 34, height: 48)
        )
        XCTAssertEqual(regions.omittedRects.count, 4)
        XCTAssertNil(regions.focusPrompt)
        XCTAssertEqual(regions.health.minX, 78.08, accuracy: 0.001)
        XCTAssertEqual(regions.health.minY, 317.88, accuracy: 0.001)
        XCTAssertEqual(regions.health.width, 83.2, accuracy: 0.001)
        XCTAssertEqual(regions.health.height, 16.92, accuracy: 0.001)
        XCTAssertEqual(regions.geo.minX, 76.8, accuracy: 0.001)
        XCTAssertEqual(regions.geo.minY, 292.68, accuracy: 0.001)
        XCTAssertEqual(regions.geo.width, 37.12, accuracy: 0.001)
        XCTAssertEqual(regions.geo.height, 16.2, accuracy: 0.001)
        XCTAssertEqual(regions.mana.minX, 39.68, accuracy: 0.001)
        XCTAssertEqual(regions.mana.minY, 293.4, accuracy: 0.001)
        XCTAssertEqual(regions.mana.width, 38.4, accuracy: 0.001)
        XCTAssertEqual(regions.mana.height, 43.2, accuracy: 0.001)
    }

    func testGeoBoxExpandsWithCurrencyDigitCount() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 640, height: 360)
        let background = CIImage(color: CIColor(red: 0.02, green: 0.03, blue: 0.05)).cropped(to: extent)
        func frame(digits: Int) throws -> CGImage {
            var image = background
            for digit in 0..<digits {
                let glyph = CIImage(color: .white).cropped(to: CGRect(
                    x: 100 + CGFloat(digit) * 12,
                    y: 300,
                    width: 7,
                    height: 17
                ))
                image = glyph.composited(over: image)
            }
            return try XCTUnwrap(context.createCGImage(image, from: extent))
        }
        let oneDigit = SceneRegionDetector().detect(in: try frame(digits: 1)).geo
        let threeDigits = SceneRegionDetector().detect(in: try frame(digits: 3)).geo
        XCTAssertEqual(oneDigit.width, 37.12, accuracy: 0.001)
        XCTAssertEqual(threeDigits.width, 57.6, accuracy: 0.001)
    }

    func testKnightDetectorFindsBrightMaskAwayFromHUD() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 320, height: 180)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let head = CIImage(color: .white).cropped(to: CGRect(x: 68, y: 71, width: 11, height: 14))
        let tinyEffect = CIImage(color: .white).cropped(to: CGRect(x: 240, y: 92, width: 2, height: 2))
        let blueGlow = CIImage(color: CIColor(red: 0.62, green: 0.78, blue: 1.0))
            .cropped(to: CGRect(x: 145, y: 105, width: 18, height: 24))
        let frame = try XCTUnwrap(context.createCGImage(
            tinyEffect.composited(over: head.composited(over: blueGlow.composited(over: background))),
            from: extent
        ))
        let regions = SceneRegionDetector().detect(in: frame)
        let knight = try XCTUnwrap(regions.knight)
        XCTAssertTrue(knight.contains(CGPoint(x: 73.5, y: 78)))
        XCTAssertEqual(knight.height, 23)
        XCTAssertEqual(knight.width, 12)
    }

    func testKnightBoxWidthStaysFixedAcrossAnimationFrames() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 320, height: 180)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let detector = SceneRegionDetector()
        var widths = [CGFloat]()
        for width in [9, 13, 10, 12] {
            let head = CIImage(color: .white).cropped(to: CGRect(x: 68, y: 71, width: width, height: 14))
            let frame = try XCTUnwrap(context.createCGImage(head.composited(over: background), from: extent))
            widths.append(try XCTUnwrap(detector.detect(in: frame).knight).width)
        }
        XCTAssertEqual(Set(widths).count, 1)
    }

    func testKnightTrackerStopsInsteadOfDriftingPastStableMeasurement() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 320, height: 180)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let detector = SceneRegionDetector()
        var centers = [CGPoint]()
        for x: CGFloat in [68, 73, 73, 73, 73, 73, 73] {
            let head = CIImage(color: .white).cropped(to: CGRect(x: x, y: 71, width: 11, height: 14))
            let frame = try XCTUnwrap(context.createCGImage(head.composited(over: background), from: extent))
            centers.append(try XCTUnwrap(detector.detect(in: frame).knight).center)
        }
        XCTAssertEqual(centers[5].x, centers[6].x, accuracy: 0.001)
        XCTAssertEqual(centers[5].y, centers[6].y, accuracy: 0.001)
    }

    func testKnightPredictionTracksFastVerticalJump() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 320, height: 180)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let detector = SceneRegionDetector()
        var boxes = [CGRect]()
        for y: CGFloat in [58, 82, 112] {
            let head = CIImage(color: .white).cropped(to: CGRect(x: 142, y: y, width: 11, height: 14))
            let frame = try XCTUnwrap(context.createCGImage(head.composited(over: background), from: extent))
            boxes.append(try XCTUnwrap(detector.detect(in: frame).knight))
        }
        XCTAssertGreaterThan(boxes[2].midY - boxes[0].midY, 35)
        XCTAssertEqual(Set(boxes.map(\.width)).count, 1)
        XCTAssertTrue(boxes[2].contains(CGPoint(x: 147.5, y: 119)))
    }

    func testKnightTrackerDoesNotJumpToDistantLightWhenKnightIsHidden() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 320, height: 180)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let head = CIImage(color: .white).cropped(to: CGRect(x: 68, y: 71, width: 11, height: 14))
        let light = CIImage(color: .white).cropped(to: CGRect(x: 238, y: 91, width: 11, height: 14))
        let detector = SceneRegionDetector()
        _ = detector.detect(in: try XCTUnwrap(context.createCGImage(head.composited(over: background), from: extent)))
        let hidden = detector.detect(
            in: try XCTUnwrap(context.createCGImage(light.composited(over: background), from: extent))
        )
        let tracked = try XCTUnwrap(hidden.knight)
        XCTAssertTrue(tracked.contains(CGPoint(x: 73.5, y: 78)))
        XCTAssertFalse(tracked.contains(CGPoint(x: 243.5, y: 98)))
    }

    func testKnightDetectorDoesNotInitializeFromBrightHalo() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 320, height: 180)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let halo = CIImage(color: CIColor(red: 0.58, green: 0.62, blue: 0.66))
            .cropped(to: CGRect(x: 233, y: 78, width: 22, height: 34))
        let core = CIImage(color: .white).cropped(to: CGRect(x: 238, y: 91, width: 11, height: 14))
        let frame = try XCTUnwrap(context.createCGImage(
            core.composited(over: halo.composited(over: background)),
            from: extent
        ))
        XCTAssertNil(SceneRegionDetector().detect(in: frame).knight)
    }

    func testFocusPromptUsesConditionalLowerFixedScreenBox() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 320, height: 180)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let blank = try XCTUnwrap(context.createCGImage(background, from: extent))
        let detector = SceneRegionDetector()
        XCTAssertNil(detector.detect(in: blank).focusPrompt)

        var promptFrame = background
        for x: CGFloat in [140, 147, 154, 168, 175] {
            promptFrame = CIImage(color: .white)
                .cropped(to: CGRect(x: x, y: 10, width: 2, height: 9))
                .composited(over: promptFrame)
        }
        let frame = try XCTUnwrap(context.createCGImage(promptFrame, from: extent))
        let regions = detector.detect(in: frame)
        let prompt = try XCTUnwrap(regions.focusPrompt)
        XCTAssertEqual(prompt, CGRect(x: 129, y: 4, width: 76, height: 20))
        XCTAssertNil(regions.knight)
        XCTAssertEqual(regions.omittedRects.count, 4)

        let mapFrame = try XCTUnwrap(FrameRegionRenderer.mapFrame(
            from: frame,
            omitting: regions,
            context: context
        ))
        XCTAssertLessThan(alpha(in: mapFrame, at: CGPoint(x: 150, y: 14), context: context), 10)

        // Detection remains available for exclusion, but the approved live
        // presentation no longer paints legacy region outlines over the game.
        XCTAssertEqual(prompt, regions.focusPrompt)

        XCTAssertNotNil(detector.detect(in: blank).focusPrompt)
        XCTAssertNil(detector.detect(in: blank).focusPrompt)
    }

    func testParticleLifecycleProducesTrackedExclusion() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 160, height: 90)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let detector = SceneRegionDetector()
        var regions = SceneRegions.hud(in: extent, knight: nil)
        var firstEligibleRegions: SceneRegions?
        for (index, x) in [CGFloat(103), 105, 107, 109, 111].enumerated() {
            let halo = CIImage(color: CIColor(red: 0.34, green: 0.34, blue: 0.34))
                .cropped(to: CGRect(x: x - 2, y: 59, width: 5, height: 5))
            let core = CIImage(color: .white)
                .cropped(to: CGRect(x: x, y: 61, width: 1, height: 1))
            let frame = try XCTUnwrap(context.createCGImage(
                core.composited(over: halo.composited(over: background)),
                from: extent
            ))
            regions = detector.detect(in: frame)
            if index == 2 { firstEligibleRegions = regions }
        }
        XCTAssertFalse(try XCTUnwrap(firstEligibleRegions).particles.isEmpty)
        XCTAssertFalse(regions.particles.isEmpty)
        XCTAssertTrue(regions.particles.contains { $0.contains(CGPoint(x: 111, y: 61)) })
        XCTAssertEqual(regions.omittedRects.count, 3 + regions.particles.count)
    }

    func testCameraCompensationRejectsStaticGaussianRoomLight() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 160, height: 90)
        let background = CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent)
        let detector = SceneRegionDetector()
        var regions = SceneRegions.hud(in: extent, knight: nil)
        for step in 0..<5 {
            let screenX = CGFloat(112 - step * 2)
            let halo = CIImage(color: CIColor(red: 0.34, green: 0.34, blue: 0.34))
                .cropped(to: CGRect(x: screenX - 2, y: 59, width: 5, height: 5))
            let core = CIImage(color: .white)
                .cropped(to: CGRect(x: screenX, y: 61, width: 1, height: 1))
            let frame = try XCTUnwrap(context.createCGImage(
                core.composited(over: halo.composited(over: background)),
                from: extent
            ))
            regions = detector.detect(
                in: frame,
                cameraPosition: CGPoint(x: CGFloat(step * 2), y: 0),
                solveWidth: 160
            )
        }
        XCTAssertTrue(regions.particles.isEmpty)
    }

    func testMapFrameClearsOnlyDetectedRectangles() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 80, height: 50)
        let source = try XCTUnwrap(context.createCGImage(
            CIImage(color: CIColor(red: 0.2, green: 0.8, blue: 0.4)).cropped(to: extent),
            from: extent
        ))
        let regions = SceneRegions(
            knight: CGRect(x: 5, y: 5, width: 8, height: 8),
            health: CGRect(x: 20, y: 35, width: 10, height: 8),
            geo: CGRect(x: 35, y: 35, width: 10, height: 8),
            mana: CGRect(x: 50, y: 35, width: 10, height: 8),
            particles: [CGRect(x: 64, y: 7, width: 8, height: 8)]
        )
        let masked = try XCTUnwrap(FrameRegionRenderer.mapFrame(
            from: source,
            omitting: regions,
            context: context
        ))

        for point in [CGPoint(x: 8, y: 8), CGPoint(x: 24, y: 38), CGPoint(x: 39, y: 38), CGPoint(x: 54, y: 38), CGPoint(x: 68, y: 11)] {
            XCTAssertLessThan(alpha(in: masked, at: point, context: context), 10)
        }
        XCTAssertGreaterThan(alpha(in: masked, at: CGPoint(x: 40, y: 20), context: context), 240)
        XCTAssertGreaterThan(alpha(in: masked, at: CGPoint(x: 15, y: 20), context: context), 240)
        XCTAssertGreaterThan(alpha(in: masked, at: CGPoint(x: 65, y: 20), context: context), 240)
    }

    func testMapFrameKeepsBlackPixelsOpaqueAcrossFullSubmission() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 80, height: 50)
        let color = CIImage(color: CIColor(red: 0.2, green: 0.7, blue: 0.4))
            .cropped(to: CGRect(x: 40, y: 0, width: 40, height: 50))
        let source = try XCTUnwrap(context.createCGImage(
            color.composited(over: CIImage(color: .black).cropped(to: extent)),
            from: extent
        ))
        let emptyRegions = SceneRegions(
            knight: nil,
            health: .zero,
            geo: .zero,
            mana: .zero
        )
        let result = try XCTUnwrap(FrameRegionRenderer.mapFrame(
            from: source,
            omitting: emptyRegions,
            context: context
        ))
        XCTAssertGreaterThan(alpha(in: result, at: CGPoint(x: 10, y: 25), context: context), 240)
        XCTAssertGreaterThan(alpha(in: result, at: CGPoint(x: 38, y: 25), context: context), 240)
        XCTAssertGreaterThan(alpha(in: result, at: CGPoint(x: 60, y: 25), context: context), 240)
    }

    func testWholeBlackTransitionIsRejectedWithoutRejectingBlackSceneObjects() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 80, height: 50)
        let black = try XCTUnwrap(context.createCGImage(
            CIImage(color: .black).cropped(to: extent),
            from: extent
        ))
        let dimRoom = CIImage(color: CIColor(red: 0.025, green: 0.03, blue: 0.035))
            .cropped(to: extent)
        let blackSilhouette = CIImage(color: .black)
            .cropped(to: CGRect(x: 28, y: 8, width: 24, height: 30))
        let hudOnly = try XCTUnwrap(context.createCGImage(
            CIImage(color: .white)
                .cropped(to: CGRect(x: 3, y: 42, width: 18, height: 6))
                .composited(over: CIImage(color: .black).cropped(to: extent)),
            from: extent
        ))
        let roomWithBlackObject = try XCTUnwrap(context.createCGImage(
            blackSilhouette.composited(over: dimRoom),
            from: extent
        ))
        XCTAssertFalse(FrameRegionRenderer.containsGameplaySignal(in: black))
        XCTAssertFalse(FrameRegionRenderer.containsGameplaySignal(in: hudOnly))
        XCTAssertTrue(FrameRegionRenderer.containsGameplaySignal(in: roomWithBlackObject))
    }

    func testGameplayCropRemovesTallWindowLetterbox() {
        let crop = GameplayFrameProcessor.gameplayCrop(in: CGRect(x: 0, y: 0, width: 960, height: 1_080))
        XCTAssertEqual(crop.minX, 0, accuracy: 0.001)
        XCTAssertEqual(crop.minY, 270, accuracy: 0.001)
        XCTAssertEqual(crop.width, 960, accuracy: 0.001)
        XCTAssertEqual(crop.height, 540, accuracy: 0.001)
    }

    func testGameplayCropRemovesWideWindowPillars() {
        let crop = GameplayFrameProcessor.gameplayCrop(in: CGRect(x: 0, y: 0, width: 1_200, height: 540))
        XCTAssertEqual(crop.minX, 120, accuracy: 0.001)
        XCTAssertEqual(crop.minY, 0, accuracy: 0.001)
        XCTAssertEqual(crop.width, 960, accuracy: 0.001)
        XCTAssertEqual(crop.height, 540, accuracy: 0.001)
    }

    func testParallaxGainScalesInverseWorldPlacement() {
        let offset = WorldPlacement.offset(
            cameraPosition: CGPoint(x: 30, y: -12),
            anchorPosition: CGPoint(x: 10, y: -2),
            presentationWidth: 640,
            solveWidth: 960,
            gain: 1.5
        )
        XCTAssertEqual(offset.x, 20, accuracy: 0.001)
        XCTAssertEqual(offset.y, -10, accuracy: 0.001)
    }

    func testFeatureTrackerCapsAndMaturesStrongFeatures() throws {
        let tracker = PersistentFeatureTracker()
        let result = try matureFeatures(in: tracker)
        XCTAssertEqual(result.features.count, PersistentFeatureTracker.targetFeatureCount)
        XCTAssertGreaterThan(result.landmarkCount, 20)
        XCTAssertTrue(result.features.contains { $0.kind == .landmark })
        XCTAssertTrue(result.features.filter { $0.kind == .landmark }.allSatisfy {
            abs($0.depth - 1) < 0.2 && $0.depthConfidence >= 0.12
        })
    }

    func testStableFeaturesEnterWorldAtlasBeforeDepthMotion() throws {
        let tracker = PersistentFeatureTracker()
        let frame = try patternImage(horizontalShift: 0)
        let initial = tracker.update(
            frame: frame,
            cameraPosition: .zero,
            solveWidth: 256,
            excluding: []
        )
        XCTAssertFalse(initial.didCorrectCameraPose)
        XCTAssertEqual(initial.cameraPoseCorrection, .zero)
        XCTAssertEqual(initial.cameraPoseCorrectionSupport, 0)
        var result = FeatureTrackingResult.empty
        for _ in 0..<5 {
            result = tracker.update(
                frame: frame,
                cameraPosition: .zero,
                solveWidth: 256,
                excluding: []
            )
        }
        XCTAssertEqual(result.features.count, PersistentFeatureTracker.targetFeatureCount)
        XCTAssertGreaterThan(result.landmarkCount, 30)
        XCTAssertTrue(result.features.allSatisfy { $0.kind == .landmark })
        XCTAssertTrue(result.features.allSatisfy { $0.depthConfidence == 0 })
    }

    func testFeatureAtlasRelocalizesAndCorrectsBacktrackingDrift() throws {
        let tracker = PersistentFeatureTracker()
        let mapped = try matureFeatures(in: tracker)
        let mappedLandmarks = mapped.landmarkCount
        _ = tracker.update(
            frame: try patternImage(horizontalShift: 0),
            cameraPosition: CGPoint(x: 300, y: 0),
            solveWidth: 256,
            excluding: []
        )
        let returned = tracker.update(
            frame: try patternImage(horizontalShift: -2),
            cameraPosition: CGPoint(x: 14, y: 0),
            solveWidth: 256,
            excluding: []
        )
        XCTAssertEqual(returned.features.count, PersistentFeatureTracker.targetFeatureCount)
        XCTAssertGreaterThan(returned.relocalizedCount, 2)
        XCTAssertGreaterThanOrEqual(returned.landmarkCount, mappedLandmarks)
        XCTAssertEqual(returned.cameraPosition.x, 2, accuracy: 2)
        XCTAssertTrue(returned.didCorrectCameraPose)
        XCTAssertLessThan(returned.cameraPoseCorrection.dx, -8)
        XCTAssertGreaterThanOrEqual(returned.cameraPoseCorrectionSupport, 3)
    }

    func testLoopClosureNormalizesCameraCorrectionByFeatureDepth() throws {
        let tracker = PersistentFeatureTracker()
        var mapped = FeatureTrackingResult.empty
        for position in [0, 2, 4, 2, 0, 2, 4, 2, 0, 2] {
            mapped = tracker.update(
                frame: try patternImage(horizontalShift: -position * 2),
                cameraPosition: CGPoint(x: CGFloat(position), y: 0),
                solveWidth: 256,
                excluding: []
            )
        }
        XCTAssertGreaterThan(mapped.landmarkCount, 20)
        XCTAssertGreaterThan(mapped.features.filter {
            $0.kind == .landmark
                && $0.depthConfidence >= 0.05
                && abs($0.depth - 2) < 0.25
        }.count, 20)

        _ = tracker.update(
            frame: try patternImage(horizontalShift: 0),
            cameraPosition: CGPoint(x: 300, y: 0),
            solveWidth: 256,
            excluding: []
        )
        let returned = tracker.update(
            frame: try patternImage(horizontalShift: -4),
            cameraPosition: CGPoint(x: 8, y: 0),
            solveWidth: 256,
            excluding: []
        )
        XCTAssertGreaterThan(returned.relocalizedCount, 2)
        XCTAssertEqual(returned.cameraPosition.x, 2, accuracy: 2)
    }

    func testFeatureLandmarksFollowSolvedCameraMotion() throws {
        let tracker = PersistentFeatureTracker()
        let mapped = try matureFeatures(in: tracker)
        let moved = tracker.update(
            frame: try patternImage(horizontalShift: -4),
            cameraPosition: CGPoint(x: 4, y: 0),
            solveWidth: 256,
            excluding: []
        )
        XCTAssertEqual(moved.features.count, PersistentFeatureTracker.targetFeatureCount)
        XCTAssertGreaterThanOrEqual(moved.landmarkCount, mapped.landmarkCount)
        XCTAssertEqual(moved.cameraPosition.x, 4, accuracy: 1.5)
        XCTAssertGreaterThan(moved.features.filter { $0.kind == .landmark }.count, 20)
    }

    func testFeatureDestroyedInsideViewIsRemovedFromAtlas() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let tracker = PersistentFeatureTracker()
        let mapped = try matureFeatures(in: tracker)
        let frame = try patternImage(horizontalShift: -2)
        let feature = try XCTUnwrap(mapped.features.first { $0.kind == .landmark })
        let erased = CIImage(color: .black).cropped(to: CGRect(
            x: feature.point.x - 12,
            y: feature.point.y - 12,
            width: 24,
            height: 24
        )).composited(over: CIImage(cgImage: frame))
        let destroyedFrame = try XCTUnwrap(context.createCGImage(
            erased,
            from: CGRect(x: 0, y: 0, width: 256, height: 128)
        ))
        var after = mapped
        for _ in 0..<7 {
            after = tracker.update(
                frame: destroyedFrame,
                cameraPosition: CGPoint(x: 2, y: 0),
                solveWidth: 256,
                excluding: []
            )
        }
        XCTAssertFalse(after.features.contains { $0.id == feature.id })
        XCTAssertFalse(after.worldFeatures.contains { $0.id == feature.id })
    }

    func testLocallyAnimatedFeatureIsRejectedAsMotionOutlier() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let tracker = PersistentFeatureTracker()
        let mapped = try matureFeatures(in: tracker)
        let frame = try patternImage(horizontalShift: -2)
        let feature = try XCTUnwrap(mapped.features.first {
            $0.kind == .landmark && $0.point.x > 24 && $0.point.x < 228
        })
        let source = CIImage(cgImage: frame)
        let patch = CGRect(
            x: feature.point.x - 9,
            y: feature.point.y - 9,
            width: 18,
            height: 18
        )
        let cleared = CIImage(color: .black).cropped(to: patch).composited(over: source)
        let movedPatch = source.cropped(to: patch).transformed(
            by: CGAffineTransform(translationX: 4, y: 0)
        )
        let animatedFrame = try XCTUnwrap(context.createCGImage(
            movedPatch.composited(over: cleared),
            from: CGRect(x: 0, y: 0, width: 256, height: 128)
        ))
        var after = mapped
        for _ in 0..<7 {
            after = tracker.update(
                frame: animatedFrame,
                cameraPosition: CGPoint(x: 2, y: 0),
                solveWidth: 256,
                excluding: []
            )
        }
        XCTAssertFalse(after.features.contains { $0.id == feature.id })
    }

    func testLiveFeatureMarkerIsHiddenInsideKnightBox() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 80, height: 50)
        let frame = try XCTUnwrap(context.createCGImage(
            CIImage(color: CIColor(red: 0.03, green: 0.05, blue: 0.08)).cropped(to: extent),
            from: extent
        ))
        let regions = SceneRegions(
            knight: CGRect(x: 30, y: 15, width: 20, height: 20),
            health: .zero,
            geo: .zero,
            mana: .zero
        )
        let tracking = FeatureTrackingResult(
            features: [MapFeature(
                id: 1,
                point: CGPoint(x: 40, y: 25),
                confidence: 1,
                depth: 1,
                depthConfidence: 1,
                kind: .landmark
            )],
            worldFeatures: [],
            cameraPosition: .zero,
            landmarkCount: 1,
            relocalizedCount: 0
        )
        let annotated = try XCTUnwrap(FrameRegionRenderer.annotatedFrame(
            from: frame,
            regions: regions,
            featureTracking: tracking,
            showFeatures: true,
            context: context
        ))
        let pixel = rgba(in: annotated, at: CGPoint(x: 36, y: 25), context: context)
        XCTAssertLessThan(pixel.green, 80)
        XCTAssertLessThan(pixel.blue, 80)
    }

    func testAccumulatesSmoothedTranslation() {
        var accumulator = CameraAccumulator()
        accumulator.smoothing = 1
        let first = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 4, y: -2),
            confidence: 0.8,
            frameWidth: 960
        )
        let second = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 6, y: 1),
            confidence: 0.9,
            frameWidth: 960
        )
        XCTAssertEqual(first.state, .accepted)
        XCTAssertEqual(second.position.x, 10, accuracy: 0.001)
        XCTAssertEqual(second.position.y, -1, accuracy: 0.001)
        XCTAssertEqual(accumulator.samples.count, 3)
    }

    func testRejectsCutSizedJumpWithoutMovingPath() {
        var accumulator = CameraAccumulator()
        let update = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 400, y: 0),
            confidence: 1,
            frameWidth: 960
        )
        XCTAssertEqual(update.state, .heldSceneChange)
        XCTAssertEqual(update.position, .zero)
        XCTAssertEqual(accumulator.samples.count, 1)
    }

    func testRejectsLargeVerticalRegistrationJumpAtLiveFrameWidth() {
        var accumulator = CameraAccumulator()
        accumulator.smoothing = 1

        let stable = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 24, y: -8),
            confidence: 1,
            frameWidth: 960
        )
        let jump = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 0, y: -150),
            confidence: 1,
            frameWidth: 960
        )

        XCTAssertEqual(stable.state, .accepted)
        XCTAssertEqual(jump.state, .heldSceneChange)
        XCTAssertEqual(jump.position.x, 24, accuracy: 0.001)
        XCTAssertEqual(jump.position.y, -8, accuracy: 0.001)
        XCTAssertEqual(accumulator.samples.count, 2)
    }

    func testAcceptsOrdinaryLiveCameraMovementAtLiveFrameWidth() {
        var accumulator = CameraAccumulator()
        accumulator.smoothing = 1

        let update = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 48, y: -24),
            confidence: 1,
            frameWidth: 960
        )

        XCTAssertEqual(update.state, .accepted)
        XCTAssertEqual(update.position.x, 48, accuracy: 0.001)
        XCTAssertEqual(update.position.y, -24, accuracy: 0.001)
    }

    func testAcceptsRecordedSparseLiveHallwayPanWithoutAdmittingVerticalJump() {
        var accumulator = CameraAccumulator()
        accumulator.smoothing = 1

        let update = accumulator.ingest(
            // The live held-right capture produced a 153px horizontal delta
            // after depth processing skipped source frames.
            alignment: CGAffineTransform(translationX: -153, y: 0),
            confidence: 1,
            frameWidth: 960
        )

        XCTAssertEqual(update.state, .accepted)
        XCTAssertEqual(update.position.x, -153, accuracy: 0.001)

        let verticalJump = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 0, y: -150),
            confidence: 1,
            frameWidth: 960
        )
        XCTAssertEqual(verticalJump.state, .heldSceneChange)
        XCTAssertEqual(verticalJump.position.x, -153, accuracy: 0.001)
        XCTAssertEqual(verticalJump.position.y, 0, accuracy: 0.001)
    }

    func testGlobalOffsetPreservesMotionAccumulatedAfterQueuedPose() {
        var accumulator = CameraAccumulator()
        accumulator.smoothing = 1
        _ = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 30, y: 4),
            confidence: 1,
            frameWidth: 960
        )

        // A slow reconstruction of an older frame reports this relative
        // correction after registration has already reached (30, 4).
        accumulator.applyGlobalOffset(CGVector(dx: -7, dy: 3))

        XCTAssertEqual(accumulator.position.x, 23, accuracy: 0.001)
        XCTAssertEqual(accumulator.position.y, 7, accuracy: 0.001)
    }

    func testInvertMotionFlipsCameraDirection() {
        var accumulator = CameraAccumulator()
        accumulator.smoothing = 1
        accumulator.invertMotion = true
        let update = accumulator.ingest(
            alignment: CGAffineTransform(translationX: 12, y: -3),
            confidence: 1,
            frameWidth: 960
        )
        XCTAssertEqual(update.position.x, -12, accuracy: 0.001)
        XCTAssertEqual(update.position.y, 3, accuracy: 0.001)
    }

    func testVisionRegistrationMovesShiftedFrameBackToReference() throws {
        let tracker = VNTrackTranslationalImageRegistrationRequest()
        let reference = makePatternBuffer(horizontalShift: 0)
        let movedLeft = makePatternBuffer(horizontalShift: -8)
        try VNImageRequestHandler(cvPixelBuffer: reference).perform([tracker])
        try VNImageRequestHandler(cvPixelBuffer: movedLeft).perform([tracker])

        let observation = try XCTUnwrap(tracker.results?.first)
        XCTAssertEqual(observation.alignmentTransform.tx, 8, accuracy: 1.5)
        XCTAssertEqual(observation.alignmentTransform.ty, 0, accuracy: 1.5)
    }

    func testVisionRegistrationReportsPositiveYForDownwardTopLeftPixelShift() throws {
        let tracker = VNTrackTranslationalImageRegistrationRequest()
        let reference = makePatternBuffer(horizontalShift: 0, verticalShift: 0)
        let movedDownInTopLeftPixels = makePatternBuffer(
            horizontalShift: 0,
            verticalShift: 8
        )
        try VNImageRequestHandler(cvPixelBuffer: reference).perform([tracker])
        try VNImageRequestHandler(cvPixelBuffer: movedDownInTopLeftPixels).perform([tracker])

        let observation = try XCTUnwrap(tracker.results?.first)
        XCTAssertEqual(observation.alignmentTransform.tx, 0, accuracy: 1.5)
        XCTAssertEqual(observation.alignmentTransform.ty, 8, accuracy: 1.5)
    }

    private func patternImage(horizontalShift: Int) throws -> CGImage {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        return try XCTUnwrap(context.createCGImage(
            CIImage(cvPixelBuffer: makePatternBuffer(horizontalShift: horizontalShift)),
            from: CGRect(x: 0, y: 0, width: 256, height: 128)
        ))
    }

    private func matureFeatures(in tracker: PersistentFeatureTracker) throws -> FeatureTrackingResult {
        var result = FeatureTrackingResult.empty
        // Exercise parallax in both directions without making otherwise durable
        // features leave the synthetic viewport during their probation period.
        for position in [0, 2, 4, 2, 0, 2, 4, 2, 0, 2] {
            result = tracker.update(
                frame: try patternImage(horizontalShift: -position),
                cameraPosition: CGPoint(x: CGFloat(position), y: 0),
                solveWidth: 256,
                excluding: []
            )
        }
        return result
    }

    private func makePatternBuffer(
        horizontalShift: Int,
        verticalShift: Int = 0
    ) -> CVPixelBuffer {
        var optionalBuffer: CVPixelBuffer?
        CVPixelBufferCreate(
            kCFAllocatorDefault,
            256,
            128,
            kCVPixelFormatType_32BGRA,
            [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary,
            &optionalBuffer
        )
        let buffer = optionalBuffer!
        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }
        let bytes = CVPixelBufferGetBaseAddress(buffer)!.assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(buffer)
        memset(bytes, 0, stride * 128)
        for y in 0..<128 {
            for x in 0..<256 {
                let sourceX = x - horizontalShift
                let sourceY = y - verticalShift
                let offset = y * stride + x * 4
                bytes[offset + 3] = 255
                let firstShape = (28..<83).contains(sourceX) && (19..<54).contains(sourceY)
                let secondShape = (126..<211).contains(sourceX) && (67..<108).contains(sourceY)
                let narrowEdge = (96..<104).contains(sourceX) && (35..<119).contains(sourceY)
                if firstShape {
                    bytes[offset] = 44
                    bytes[offset + 1] = 192
                    bytes[offset + 2] = 238
                } else if secondShape {
                    bytes[offset] = 219
                    bytes[offset + 1] = 91
                    bytes[offset + 2] = 63
                } else if narrowEdge {
                    bytes[offset] = 242
                    bytes[offset + 1] = 231
                    bytes[offset + 2] = 116
                } else {
                    let texture = UInt8(
                        (sourceX * 7 + sourceY * 13 + sourceX * sourceY) & 31
                    )
                    bytes[offset] = 8 + texture
                    bytes[offset + 1] = 12 + texture
                    bytes[offset + 2] = 18 + texture
                }
            }
        }
        return buffer
    }

    private func alpha(in image: CGImage, at point: CGPoint, context: CIContext) -> UInt8 {
        var pixel = [UInt8](repeating: 0, count: 4)
        context.render(
            CIImage(cgImage: image),
            toBitmap: &pixel,
            rowBytes: 4,
            bounds: CGRect(x: point.x, y: point.y, width: 1, height: 1),
            format: .RGBA8,
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        return pixel[3]
    }

    private func rgba(
        in image: CGImage,
        at point: CGPoint,
        context: CIContext
    ) -> (red: UInt8, green: UInt8, blue: UInt8, alpha: UInt8) {
        var pixel = [UInt8](repeating: 0, count: 4)
        context.render(
            CIImage(cgImage: image),
            toBitmap: &pixel,
            rowBytes: 4,
            bounds: CGRect(x: point.x, y: point.y, width: 1, height: 1),
            format: .RGBA8,
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        return (pixel[0], pixel[1], pixel[2], pixel[3])
    }

}

private extension CGRect {
    var center: CGPoint { CGPoint(x: midX, y: midY) }
}
