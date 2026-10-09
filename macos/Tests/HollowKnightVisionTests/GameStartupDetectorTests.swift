import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GameStartupDetectorTests: XCTestCase {
    func testAutomaticNavigationRequiresExplicitLaunchFlag() {
        let ordinary = HollowKnightVisionLaunchOptions(arguments: ["HollowKnightVision"])
        XCTAssertFalse(ordinary.autoNavigateToGameplay)
        XCTAssertFalse(ordinary.automationControlEnabled)

        let automated = HollowKnightVisionLaunchOptions(arguments: [
            "HollowKnightVision", "--auto-navigate-gameplay",
            "--enable-automation-control",
        ])
        XCTAssertTrue(automated.autoNavigateToGameplay)
        XCTAssertTrue(automated.automationControlEnabled)
        XCTAssertEqual(HollowKnightVisionLaunchOptions.retiredArgument(in: [
            "HollowKnightVision", "--ground-texture-subpixel",
        ]), "--ground-texture-subpixel")
        XCTAssertNil(HollowKnightVisionLaunchOptions.retiredArgument(in: [
            "HollowKnightVision", "--ground-keyframe-subpixel",
        ]))
        XCTAssertEqual(HollowKnightVisionLaunchOptions.retiredArgument(in: [
            "HollowKnightVision", "--ground-texture-affine",
        ]), "--ground-texture-affine")
    }

    func testAutomationCommandParsingBoundsDurations() throws {
        XCTAssertEqual(
            GameAutomationCommand(serialized: "up:2.25"),
            GameAutomationCommand(button: .up, duration: 2.25)
        )
        XCTAssertEqual(
            GameAutomationCommand(serialized: "left"),
            GameAutomationCommand(button: .left, duration: 1.5)
        )
        XCTAssertNil(GameAutomationCommand(serialized: "up:0"))
        XCTAssertNil(GameAutomationCommand(serialized: "up:11"))
        XCTAssertNil(GameAutomationCommand(serialized: "unknown:1"))
    }

    func testProductionStabilityWindowRequiresEightFramesBeforeSelection() {
        var coordinator = GameStartupCoordinator()
        let title = GameStartupEvidence(
            screen: .title, gameplayLikely: false, selectedOption: "Start Game"
        )
        for _ in 0..<7 {
            XCTAssertNil(coordinator.observe(title).action)
        }
        XCTAssertEqual(coordinator.observe(title).action, .selectStartGame)
    }

    func testCoordinatorMovesToStartGameBeforeSelecting() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 2)
        let options = GameStartupEvidence(
            screen: .title,
            gameplayLikely: false,
            selectedOption: "Options"
        )
        XCTAssertNil(coordinator.observe(options).action)
        XCTAssertEqual(coordinator.observe(options).action, .moveSelectionUp)
        XCTAssertEqual(coordinator.phase, .awaitingTitle)

        let start = GameStartupEvidence(
            screen: .title,
            gameplayLikely: false,
            selectedOption: "Start Game"
        )
        for _ in 0..<17 {
            XCTAssertNil(coordinator.observe(start).action)
        }
        XCTAssertEqual(coordinator.observe(start).action, .selectStartGame)
    }

    func testCoordinatorMovesToProfileOneBeforeSelecting() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 1)
        let slotTwo = GameStartupEvidence(
            screen: .profileOne,
            gameplayLikely: false,
            selectedOption: "2."
        )
        XCTAssertEqual(coordinator.observe(slotTwo).action, .moveSelectionUp)
        XCTAssertEqual(coordinator.phase, .awaitingTitle)

        let slotOne = GameStartupEvidence(
            screen: .profileOne,
            gameplayLikely: false,
            selectedOption: "1."
        )
        for _ in 0..<16 {
            XCTAssertNil(coordinator.observe(slotOne).action)
        }
        XCTAssertEqual(coordinator.observe(slotOne).action, .selectProfileOne)
    }

    func testCoordinatorWaitsForStencilSelectionBeforeSelecting() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 1)
        let unresolvedTitle = GameStartupEvidence(
            screen: .title,
            gameplayLikely: false,
            selectedOption: nil
        )
        XCTAssertNil(coordinator.observe(unresolvedTitle).action)
        XCTAssertNil(coordinator.observe(unresolvedTitle).action)
        XCTAssertEqual(coordinator.phase, .awaitingTitle)

        let resolvedTitle = GameStartupEvidence(
            screen: .title,
            gameplayLikely: false,
            selectedOption: "Start Game"
        )
        XCTAssertEqual(coordinator.observe(resolvedTitle).action, .selectStartGame)
    }

    func testCoordinatorSelectsBothMenusThenAdmitsGameplay() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 3)
        let title = GameStartupEvidence(
            screen: .title, gameplayLikely: false, selectedOption: "Start Game"
        )
        XCTAssertNil(coordinator.observe(title).action)
        XCTAssertNil(coordinator.observe(title).action)
        XCTAssertEqual(coordinator.observe(title).action, .selectStartGame)
        XCTAssertNil(coordinator.observe(title).action)

        let profile = GameStartupEvidence(
            screen: .profileOne, gameplayLikely: false, selectedOption: "1."
        )
        XCTAssertNil(coordinator.observe(profile).action)
        XCTAssertNil(coordinator.observe(profile).action)
        XCTAssertEqual(coordinator.observe(profile).action, .selectProfileOne)

        let gameplay = GameStartupEvidence(screen: nil, gameplayLikely: true)
        XCTAssertFalse(coordinator.observe(gameplay).admitsWorldFrames)
        XCTAssertFalse(coordinator.observe(gameplay).admitsWorldFrames)
        XCTAssertTrue(coordinator.observe(gameplay).admitsWorldFrames)
    }

    func testBackFromProfileReturnsToTitlePhase() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 2)
        let profile = GameStartupEvidence(
            screen: .profileOne, gameplayLikely: false, selectedOption: "1."
        )
        XCTAssertNil(coordinator.observe(profile).action)
        XCTAssertEqual(coordinator.observe(profile).action, .selectProfileOne)
        XCTAssertEqual(coordinator.phase, .awaitingGameplay)

        let title = GameStartupEvidence(
            screen: .title, gameplayLikely: false, selectedOption: "Start Game"
        )
        XCTAssertNil(coordinator.observe(title).action)
        XCTAssertEqual(coordinator.phase, .awaitingTitle)
        XCTAssertEqual(coordinator.observe(title).action, .selectStartGame)
    }

    func testRejectedSelectionCanBeRetriedButNeverRepeatsWhileTransitioning() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 2)
        let title = GameStartupEvidence(
            screen: .title, gameplayLikely: false, selectedOption: "Start Game"
        )
        XCTAssertNil(coordinator.observe(title).action)
        XCTAssertEqual(coordinator.observe(title).action, .selectStartGame)
        XCTAssertNil(coordinator.observe(title).action)
        coordinator.retry(.selectStartGame)
        XCTAssertNil(coordinator.observe(title).action)
        XCTAssertEqual(coordinator.observe(title).action, .selectStartGame)
    }

    func testGameplayGateClosesWhenTitleIsRecognized() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 1)
        let gameplay = GameStartupEvidence(screen: nil, gameplayLikely: true)
        XCTAssertTrue(coordinator.observe(gameplay).admitsWorldFrames)
        let title = GameStartupEvidence(screen: .title, gameplayLikely: false)
        XCTAssertFalse(coordinator.observe(title).admitsWorldFrames)
        XCTAssertNotEqual(coordinator.phase, .gameplay)
    }

    func testCaptureEpochResetPreservesEstablishedGameplay() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 1)
        let gameplay = GameStartupEvidence(screen: nil, gameplayLikely: true)
        XCTAssertTrue(coordinator.observe(gameplay).admitsWorldFrames)

        coordinator.reset(preservingGameplay: true)

        XCTAssertEqual(coordinator.phase, .gameplay)
        XCTAssertTrue(coordinator.observe(
            GameStartupEvidence(screen: nil, gameplayLikely: false)
        ).admitsWorldFrames)
    }

    func testConfirmedCheckpointRestoreReopensGameplayGate() {
        var coordinator = GameStartupCoordinator(requiredStableFrames: 8)

        coordinator.restoreGameplaySession()

        XCTAssertEqual(coordinator.phase, .gameplay)
        XCTAssertTrue(coordinator.observe(
            GameStartupEvidence(screen: nil, gameplayLikely: false)
        ).admitsWorldFrames)
    }

    func testBrightHudArtEstablishesGameplay() throws {
        let image = try makeImage(brightTopOriginRects: [
            CGRect(x: 0.06, y: 0.04, width: 0.06, height: 0.11),
            CGRect(x: 0.12, y: 0.06, width: 0.10, height: 0.05),
        ])
        let evidence = GameStartupDetector().detect(in: image)
        XCTAssertNil(evidence.screen)
        XCTAssertTrue(evidence.gameplayLikely)
    }

    func testDetectorUsesMenuStencil() throws {
        let image = try makeImage(brightTopOriginRects: [
            CGRect(x: 0.46, y: 0.57, width: 0.088, height: 0.055),
        ])
        let evidence = GameStartupDetector().detect(
            in: image,
            menuStencil: matchedMenuStencil(.mainTitle)
        )
        XCTAssertEqual(evidence.screen, .title)
        XCTAssertFalse(evidence.gameplayLikely)
    }

    func testDetectorPreservesStencilSelectionForNavigation() throws {
        let image = try makeImage(brightTopOriginRects: [])
        let evidence = GameStartupDetector().detect(
            in: image,
            menuStencil: matchedMenuStencil(.mainTitle, selectedOption: "Options")
        )
        XCTAssertEqual(evidence.selectedOption, "Options")
    }

    func testPixelMenuLookalikeIsRejectedAndStencilReleasesGameplayLatch() throws {
        let image = try makeImage(brightTopOriginRects: [
            CGRect(x: 0.412, y: 0.57, width: 0.032, height: 0.055),
            CGRect(x: 0.46, y: 0.57, width: 0.088, height: 0.055),
            CGRect(x: 0.568, y: 0.57, width: 0.032, height: 0.055),
        ])
        let detector = GameStartupDetector()
        XCTAssertNil(detector.detect(in: image).screen)
        XCTAssertNil(detector.detect(in: image, gameplayIsLatched: true).screen)
        XCTAssertEqual(detector.detect(
            in: image,
            menuStencil: matchedMenuStencil(.mainTitle),
            gameplayIsLatched: true
        ).screen, .title)
    }

    func testDetectorFindsSelectedProfileOne() throws {
        let image = try makeImage(brightTopOriginRects: [
            CGRect(x: 0.10, y: 0.285, width: 0.035, height: 0.055),
            CGRect(x: 0.17, y: 0.285, width: 0.025, height: 0.055),
            CGRect(x: 0.675, y: 0.285, width: 0.035, height: 0.055),
        ])
        let evidence = GameStartupDetector().detect(in: image)
        XCTAssertEqual(evidence.screen, .profileOne)
    }

    func testDetectorUsesGenericMenuStencilForBothStartupScreens() throws {
        let blank = try makeImage(brightTopOriginRects: [])
        XCTAssertEqual(GameStartupDetector().detect(
            in: blank,
            menuStencil: matchedMenuStencil(.mainTitle),
            gameplayIsLatched: true
        ).screen, .title)
        XCTAssertEqual(GameStartupDetector().detect(
            in: blank,
            menuStencil: matchedMenuStencil(.selectProfile),
            gameplayIsLatched: true
        ).screen, .profileOne)
        XCTAssertNil(GameStartupDetector().detect(
            in: blank,
            menuStencil: matchedMenuStencil(.options),
            gameplayIsLatched: true
        ).screen)
    }

    func testGameplayNeedsBothHudElementsButNotKnight() throws {
        let hud = try makeImage(brightTopOriginRects: [
            CGRect(x: 0.06, y: 0.06, width: 0.05, height: 0.11),
            CGRect(x: 0.13, y: 0.06, width: 0.11, height: 0.06),
        ])
        XCTAssertTrue(GameStartupDetector().detect(in: hud).gameplayLikely)
        let manaOnly = try makeImage(brightTopOriginRects: [
            CGRect(x: 0.06, y: 0.06, width: 0.05, height: 0.11),
        ])
        XCTAssertFalse(GameStartupDetector().detect(in: manaOnly).gameplayLikely)
    }

    func testVerifiedHealthReleasesStaleProfileStencilWithoutSoul() throws {
        let blank = try makeImage(brightTopOriginRects: [])
        let staleProfile = matchedMenuStencil(.selectProfile, selectedOption: "1.")

        let gameplay = GameStartupDetector().detect(
            in: blank,
            menuStencil: staleProfile,
            hudStencil: matchedHUDStencil(includesMana: true)
        )
        XCTAssertTrue(gameplay.gameplayLikely)
        XCTAssertNil(gameplay.screen)

        let healthOnlyGameplay = GameStartupDetector().detect(
            in: blank,
            menuStencil: staleProfile,
            hudStencil: matchedHUDStencil(includesMana: false)
        )
        XCTAssertTrue(healthOnlyGameplay.gameplayLikely)
        XCTAssertNil(healthOnlyGameplay.screen)
    }

    func testHudPairWinsWhenGameplaySceneryResemblesTitle() throws {
        let image = try makeImage(brightTopOriginRects: [
            CGRect(x: 0.06, y: 0.06, width: 0.05, height: 0.11),
            CGRect(x: 0.13, y: 0.06, width: 0.11, height: 0.06),
            CGRect(x: 0.412, y: 0.57, width: 0.032, height: 0.055),
            CGRect(x: 0.46, y: 0.57, width: 0.088, height: 0.055),
            CGRect(x: 0.568, y: 0.57, width: 0.032, height: 0.055),
        ])
        let evidence = GameStartupDetector().detect(in: image)
        XCTAssertNil(evidence.screen)
        XCTAssertTrue(evidence.gameplayLikely)
    }

    private func matchedHUDStencil(includesMana: Bool) -> HUDStencilResult {
        HUDStencilResult(
            health: [
                HUDStencilMatch(index: 0, kind: "full", rect: .zero, confidence: 0.9),
                HUDStencilMatch(index: 1, kind: "full", rect: .zero, confidence: 0.9),
            ],
            manaMain: includesMana
                ? HUDStencilMatch(index: 0, kind: "main", rect: .zero, confidence: 0.9)
                : nil,
            manaReserves: [],
            healthMasks: [],
            manaMasks: [],
            geo: .zero,
            holdsHealthMask: false,
            sourceTimestamp: 1
        )
    }

    private func matchedMenuStencil(
        _ context: LabelingContext,
        selectedOption: String? = nil
    ) -> MenuStencilResult {
        MenuStencilResult(
            context: context,
            isMatch: true,
            confidence: 0.9,
            selectedOption: selectedOption,
            anchors: [],
            selectorCandidates: [],
            selectorSearchRegions: [],
            selectorLanguageEvidenceCount: 0,
            selectorForegroundEvidenceCount: 0,
            sceneEvidenceRatio: 1,
            phase: .tracking,
            comparisonCount: 1,
            sourceTimestamp: 1,
            languageIdentifier: nil
        )
    }

    private func makeImage(
        width: Int = 640,
        height: Int = 360,
        brightTopOriginRects: [CGRect]
    ) throws -> CGImage {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for pixel in 0..<(width * height) { pixels[pixel * 4 + 3] = 255 }
        for normalized in brightTopOriginRects {
            let x0 = max(0, Int((normalized.minX * CGFloat(width)).rounded(.down)))
            let x1 = min(width, Int((normalized.maxX * CGFloat(width)).rounded(.up)))
            let y0 = max(0, Int((normalized.minY * CGFloat(height)).rounded(.down)))
            let y1 = min(height, Int((normalized.maxY * CGFloat(height)).rounded(.up)))
            for y in y0..<y1 {
                for x in x0..<x1 {
                    let index = (y * width + x) * 4
                    pixels[index] = 245
                    pixels[index + 1] = 245
                    pixels[index + 2] = 245
                }
            }
        }
        let data = Data(pixels) as CFData
        let provider = try XCTUnwrap(CGDataProvider(data: data))
        return try XCTUnwrap(CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        ))
    }
}
