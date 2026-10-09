import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LiveGameStatusResolverTests: XCTestCase {
    func testObjectDetectionsDoNotResolveMainTitle() {
        XCTAssertNil(LiveGameStatusResolver.resolve(
            completeSetDetections(.mainTitle)
        ))
    }

    func testMenuStencilIdentifiesSelectedMenuOption() throws {
        let status = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [],
            menuStencil: matchedMenuStencil(.mainTitle, selected: "Options")
        ))

        XCTAssertEqual(status.displayText, "Main Title - Options")
    }

    func testMenuObjectSelectorPairDoesNotClaimSelection() {
        XCTAssertNil(LiveGameStatusResolver.resolve(
            completeSetDetections(
                .mainTitle,
                selected: "main-title.start-game",
                selectorCount: 2
            )
        ))
    }

    func testSpecificStartGameAndSelectorPairDoNotIdentifyStartScreen() {
        XCTAssertNil(LiveGameStatusResolver.resolve([
            detection("main-title.start-game", x: 0.45, y: 0.55, width: 0.10),
            detection("main-title.select-decoration", x: 0.42, y: 0.55, width: 0.02),
            detection("main-title.select-decoration", x: 0.56, y: 0.55, width: 0.02),
        ]))
    }

    func testSharedBackAndSelectorsCannotChooseAScreen() {
        XCTAssertNil(LiveGameStatusResolver.resolve([
            detection(LabelingClassIdentity.back, x: 0.45, y: 0.85, width: 0.10),
            detection(LabelingClassIdentity.selectDecoration, x: 0.42, y: 0.85, width: 0.02),
            detection(LabelingClassIdentity.selectDecoration, x: 0.56, y: 0.85, width: 0.02),
        ]))
    }

    func testTitleSpecificObjectsAreIgnoredByRuntimeResolver() {
        XCTAssertNil(LiveGameStatusResolver.resolve([
            detection("main-title.hollow-knight-logo", x: 0.2, y: 0.15, width: 0.6),
            detection("main-title.start-game", x: 0.45, y: 0.55, width: 0.10),
            detection(LabelingClassIdentity.options, x: 0.45, y: 0.65, width: 0.10),
        ]))
    }

    func testLowConfidenceMenuCueCannotReleaseGameplayLatch() throws {
        let weakTitle = LiveObjectDetection(
            classIdentifier: "main-title.hollow-knight-logo",
            normalizedRect: CGRect(x: 0.2, y: 0.15, width: 0.6, height: 0.1),
            confidence: 0.49,
            sourceFrameIdentifier: 1,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )
        XCTAssertNil(LiveGameStatusResolver.resolve([weakTitle]))
        let gameplay = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], hudStencil: matchedHUDStencil()
        ))
        var tracker = LiveGameStatusTracker()
        XCTAssertEqual(tracker.observe(gameplay, at: 1)?.context, .gameplay)
        XCTAssertEqual(tracker.observe(
            LiveGameStatusResolver.resolve([weakTitle]), at: 2
        )?.context, .gameplay)
    }

    func testSelectProfileStencilReportsSlotsClearSaveAndBack() throws {
        let selections = ["1", "4", "Clear Save", "Back"]
        for name in selections {
            let status = try XCTUnwrap(LiveGameStatusResolver.resolve(
                [], menuStencil: matchedMenuStencil(.selectProfile, selected: name)
            ))
            XCTAssertEqual(status.displayText, "Select Profile - \(name)")
            XCTAssertEqual(status.context, .selectProfile)
        }
    }

    func testMenuStencilWinsOverGameplayHUDStencil() throws {
        let status = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [],
            menuStencil: matchedMenuStencil(.selectProfile, selected: nil),
            hudStencil: matchedHUDStencil()
        ))
        XCTAssertEqual(status.displayText, "Select Profile")
        XCTAssertEqual(status.context, .selectProfile)
    }

    func testHealthStencilEstablishesGameplayWithoutManaMatch() {
        let healthOnly = HUDStencilResult(
            health: [
                HUDStencilMatch(index: 0, kind: "full", rect: .zero, confidence: 0.9),
                HUDStencilMatch(index: 1, kind: "full", rect: .zero, confidence: 0.9),
            ],
            manaMain: nil,
            manaReserves: [],
            healthMasks: [],
            manaMasks: [],
            geo: .zero,
            holdsHealthMask: false,
            sourceTimestamp: 1
        )
        XCTAssertEqual(
            LiveGameStatusResolver.resolve([], hudStencil: healthOnly)?.context,
            .gameplay
        )
    }

    func testRetiredHUDObjectDetectionsCannotEstablishGameplay() throws {
        let health = detection("game.health", x: 0.12, y: 0.05, width: 0.12)
        let mana = detection("game.mana", x: 0.05, y: 0.05, width: 0.08)
        XCTAssertNil(LiveGameStatusResolver.resolve([health]))
        XCTAssertNil(LiveGameStatusResolver.resolve([health, mana]))
        XCTAssertEqual(LiveGameStatusResolver.resolve(
            [health, mana], hudStencil: matchedHUDStencil()
        )?.context, .gameplay)
        XCTAssertTrue(SceneRegions.fromObjectDetections(
            [health, mana],
            in: CGRect(x: 0, y: 0, width: 1000, height: 562)
        ).geo.isEmpty)
    }

    func testKnightEnemyAndWorldObjectsSupportGameplay() {
        let pairs = [
            ["game.playable-knight", "enemies.crawlid"],
            ["enemies.crawlid", "world.geo-deposit"],
            ["enemies.vengfly", "world.sign"],
        ]
        for identifiers in pairs {
            let detections = identifiers.enumerated().map {
                detection($0.element, x: CGFloat($0.offset) * 0.2, y: 0.2, width: 0.1)
            }
            XCTAssertEqual(LiveGameStatusResolver.resolve(detections)?.context, .gameplay)
        }
    }

    func testMainTitleStencilWinsOverStaleGameplayDetections() throws {
        let status = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [],
            menuStencil: matchedMenuStencil(.mainTitle, selected: nil),
            hudStencil: matchedHUDStencil()
        ))
        XCTAssertEqual(status.context, .mainTitle)
    }

    func testStencilProfileStateAndStatusGraceExpires() throws {
        let profile = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], menuStencil: matchedMenuStencil(.selectProfile, selected: nil)
        ))
        XCTAssertEqual(profile.context, .selectProfile)
        var tracker = LiveGameStatusTracker()
        XCTAssertEqual(tracker.observe(profile, at: 1)?.context, .selectProfile)
        XCTAssertEqual(tracker.observe(nil, at: 1.5)?.context, .selectProfile)
        XCTAssertNil(tracker.observe(nil, at: 1.9))
        let title = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], menuStencil: matchedMenuStencil(.mainTitle, selected: nil)
        ))
        XCTAssertEqual(tracker.observe(title, at: 2)?.context, .mainTitle)
    }

    func testGameplayStaysLatchedWithoutHudUntilTitleIsDetected() throws {
        let gameplay = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], hudStencil: matchedHUDStencil()
        ))
        var tracker = LiveGameStatusTracker()
        XCTAssertEqual(tracker.observe(gameplay, at: 1)?.context, .gameplay)
        XCTAssertEqual(tracker.observe(nil, at: 2)?.context, .gameplay)
        XCTAssertEqual(tracker.observe(nil, at: 100)?.context, .gameplay)
        tracker.reset(preservingGameplay: true)
        XCTAssertEqual(tracker.observe(nil, at: 0)?.context, .gameplay)

        let title = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], menuStencil: matchedMenuStencil(.mainTitle, selected: nil)
        ))
        XCTAssertEqual(tracker.observe(title, at: 1)?.context, .mainTitle)
        XCTAssertEqual(tracker.observe(nil, at: 2)?.context, nil)
    }

    func testCreditsVisualCueCannotEstablishStateWithoutExtras() throws {
        let credits = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], creditsLikely: true
        ))
        var tracker = LiveGameStatusTracker()

        XCTAssertNil(tracker.observe(credits, at: 1))
    }

    func testCreditsVisualCueCannotReleaseGameplayLatch() throws {
        let gameplay = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], hudStencil: matchedHUDStencil()
        ))
        let credits = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], creditsLikely: true
        ))
        var tracker = LiveGameStatusTracker()

        XCTAssertEqual(tracker.observe(gameplay, at: 1)?.context, .gameplay)
        XCTAssertEqual(tracker.observe(credits, at: 2)?.context, .gameplay)
    }

    func testCreditsVisualCueIsAcceptedAfterExtras() throws {
        let extras = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], menuStencil: matchedMenuStencil(.extras, selected: nil)
        ))
        let credits = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], creditsLikely: true
        ))
        var tracker = LiveGameStatusTracker()

        XCTAssertEqual(tracker.observe(extras, at: 1)?.context, .labelSet(.extras))
        XCTAssertEqual(tracker.observe(credits, at: 2)?.context, .labelSet(.credits))
    }

    func testProvisionalCreditsKeepsObjectInferenceRunning() throws {
        let credits = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], creditsLikely: true
        ))
        let title = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], menuStencil: matchedMenuStencil(.mainTitle, selected: nil)
        ))

        XCTAssertTrue(LiveGameStatusEvidencePolicy.shouldRunObjectInference(for: nil))
        XCTAssertTrue(LiveGameStatusEvidencePolicy.shouldRunObjectInference(for: credits))
        XCTAssertTrue(LiveGameStatusEvidencePolicy.shouldRunObjectInference(for:
            LiveGameStatusResolver.resolve([], hudStencil: matchedHUDStencil())
        ))
        XCTAssertFalse(LiveGameStatusEvidencePolicy.shouldRunObjectInference(for: title))
    }

    func testProfileStencilReleasesGameplayLatch() throws {
        let gameplay = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], hudStencil: matchedHUDStencil()
        ))
        let profile = try XCTUnwrap(LiveGameStatusResolver.resolve(
            [], menuStencil: matchedMenuStencil(.selectProfile, selected: nil)
        ))
        var tracker = LiveGameStatusTracker()
        XCTAssertEqual(tracker.observe(gameplay, at: 1)?.context, .gameplay)
        XCTAssertEqual(tracker.observe(profile, at: 2)?.context, .selectProfile)
        XCTAssertEqual(tracker.observe(nil, at: 3)?.context, nil)
    }

    func testCompleteOptionsObjectSetDoesNotIdentifyOptionsContext() {
        let identifiers = [
            LabelingClassIdentity.options,
            "options.game",
            LabelingClassIdentity.audio,
            LabelingClassIdentity.video,
            LabelingClassIdentity.controller,
            LabelingClassIdentity.keyboard,
            LabelingClassIdentity.mods,
            LabelingClassIdentity.back,
            LabelingClassIdentity.selectDecoration,
            LabelingClassIdentity.selectDecoration,
        ]
        XCTAssertNil(LiveGameStatusResolver.resolve(
            identifiers.enumerated().map {
                detection($0.element, x: 0.2, y: CGFloat($0.offset) * 0.05, width: 0.2)
            }
        ))
    }

    func testMenuStencilOverridesStaleObjectsForEveryMenuContext() throws {
        let staleGameplay = [
            detection("game.health", x: 0.12, y: 0.05, width: 0.12),
            detection("game.mana", x: 0.05, y: 0.05, width: 0.08),
        ]
        let options = try XCTUnwrap(LiveGameStatusResolver.resolve(
            staleGameplay,
            menuStencil: matchedMenuStencil(.options, selected: "Audio")
        ))
        XCTAssertEqual(options.context, .labelSet(.options))
        XCTAssertEqual(options.displayText, "Options - Audio")

        let title = try XCTUnwrap(LiveGameStatusResolver.resolve(
            staleGameplay,
            menuStencil: matchedMenuStencil(.mainTitle, selected: "Start Game")
        ))
        XCTAssertEqual(title.context, .mainTitle)
        XCTAssertEqual(title.displayText, "Main Title - Start Game")

        let profile = try XCTUnwrap(LiveGameStatusResolver.resolve(
            staleGameplay,
            menuStencil: matchedMenuStencil(.selectProfile, selected: "1.")
        ))
        XCTAssertEqual(profile.context, .selectProfile)
        XCTAssertEqual(profile.displayText, "Select Profile - 1.")
    }

    func testLabelSetScorePenalizesObjectsOutsideContract() throws {
        let expected = LabelingContext.audio.labels.map {
            detection($0.id, x: 0.2, y: 0.2, width: 0.2)
        } + [detection(LabelingClassIdentity.selectDecoration, x: 0.3, y: 0.2, width: 0.1)]
        let clean = try XCTUnwrap(
            LiveLabelSetScorer.scores(expected).first { $0.context == .audio }
        )
        let noisy = try XCTUnwrap(
            LiveLabelSetScorer.scores(expected + [
                detection("game.health", x: 0.1, y: 0.1, width: 0.1),
            ]).first { $0.context == .audio }
        )

        XCTAssertEqual(clean.matchedObjectCount, 8)
        XCTAssertGreaterThan(clean.distinctiveObjectCount, 0)
        XCTAssertEqual(clean.missingObjectCount, 0)
        XCTAssertEqual(clean.unexpectedObjectCount, 0)
        XCTAssertLessThan(noisy.score, clean.score)
        XCTAssertEqual(
            clean.score - noisy.score,
            LiveLabelSetScorer.evidenceWeight(for: "game.health"),
            accuracy: 0.0001
        )
        XCTAssertEqual(noisy.unexpectedObjectCount, 1)
    }

    func testLabelSetScoreSubtractsMissingAndSurplusExpectedObjects() throws {
        let complete = LabelingContext.audio.labels.map {
            detection($0.id, x: 0.2, y: 0.2, width: 0.2)
        } + [detection(LabelingClassIdentity.selectDecoration, x: 0.3, y: 0.2, width: 0.1)]
        let withoutMusic = complete.filter { $0.classIdentifier != "audio.music-volume" }
        let missing = try XCTUnwrap(
            LiveLabelSetScorer.scores(withoutMusic).first { $0.context == .audio }
        )
        let surplusSelector = try XCTUnwrap(
            LiveLabelSetScorer.scores(complete + [
                detection(LabelingClassIdentity.selectDecoration, x: 0.4, y: 0.2, width: 0.1),
            ]).first { $0.context == .audio }
        )

        XCTAssertEqual(missing.matchedObjectCount, 7)
        XCTAssertEqual(missing.missingObjectCount, 1)
        XCTAssertLessThan(missing.score, completeScore(for: .audio))
        XCTAssertEqual(surplusSelector.unexpectedObjectCount, 1)
        XCTAssertLessThan(surplusSelector.score, completeScore(for: .audio))
    }

    func testSpecificityWeightsUniqueObjectsAboveSharedFurniture() {
        XCTAssertGreaterThan(
            LiveLabelSetScorer.evidenceWeight(for: "main-title.start-game"),
            LiveLabelSetScorer.evidenceWeight(for: LabelingClassIdentity.back)
        )
        XCTAssertGreaterThan(
            LiveLabelSetScorer.evidenceWeight(for: "main-title.hollow-knight-logo"),
            LiveLabelSetScorer.evidenceWeight(for: LabelingClassIdentity.selectDecoration)
        )
    }

    func testQuitGameYesClickTerminatesOnlyAfterAcceptedMouseUp() {
        XCTAssertTrue(QuitGameTerminationPolicy.recognizesExitIntent(
            context: .labelSet(.quitGame),
            selectedOption: "YES"
        ))
        XCTAssertFalse(QuitGameTerminationPolicy.recognizesExitIntent(
            context: .labelSet(.quitToMenu),
            selectedOption: "Yes"
        ))
        XCTAssertTrue(QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: .labelSet(.quitGame),
            selectedOption: "Yes",
            event: .leftUp,
            accepted: true
        ))
        XCTAssertFalse(QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: .labelSet(.quitGame),
            selectedOption: "No",
            event: .leftUp,
            accepted: true
        ))
        XCTAssertFalse(QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: .labelSet(.quitGame),
            selectedOption: "Yes",
            event: .leftDown,
            accepted: true
        ))
    }

    func testQuitGameYesZPressWaitsForGameExit() {
        XCTAssertTrue(QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: .labelSet(.quitGame),
            selectedOption: "Yes",
            button: .z,
            isPressed: true,
            accepted: true
        ))
        XCTAssertFalse(QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: .labelSet(.quitGame),
            selectedOption: "Yes",
            button: .z,
            isPressed: false,
            accepted: true
        ))
        XCTAssertFalse(QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: .labelSet(.quitGame),
            selectedOption: "No",
            button: .z,
            isPressed: true,
            accepted: true
        ))
        XCTAssertFalse(QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: .labelSet(.quitGame),
            selectedOption: "Yes",
            button: .x,
            isPressed: true,
            accepted: true
        ))
    }

    func testGameplayHUDRejectsFlashingKeyboardStencil() {
        var gate = GameplayMenuEvidenceGate()

        XCTAssertFalse(gate.admits(
            menuContext: .keyboard,
            gameplayLatched: true,
            hudHealthMatchCount: 5,
            timestamp: 1
        ))
        XCTAssertFalse(gate.admits(
            menuContext: nil,
            gameplayLatched: true,
            hudHealthMatchCount: 0,
            timestamp: 1.1
        ))
        XCTAssertFalse(gate.admits(
            menuContext: .keyboard,
            gameplayLatched: true,
            hudHealthMatchCount: 0,
            timestamp: 1.2
        ))
        XCTAssertFalse(gate.admits(
            menuContext: nil,
            gameplayLatched: true,
            hudHealthMatchCount: 0,
            timestamp: 1.5
        ))
        XCTAssertFalse(gate.admits(
            menuContext: .keyboard,
            gameplayLatched: true,
            hudHealthMatchCount: 0,
            timestamp: 1.6
        ))
    }

    func testStableMenuIsAdmittedAfterGameplayHUDDisappears() {
        var gate = GameplayMenuEvidenceGate()
        XCTAssertFalse(gate.admits(
            menuContext: nil,
            gameplayLatched: true,
            hudHealthMatchCount: 5,
            timestamp: 1
        ))
        XCTAssertFalse(gate.admits(
            menuContext: .pause,
            gameplayLatched: true,
            hudHealthMatchCount: 0,
            timestamp: 1.4
        ))
        XCTAssertTrue(gate.admits(
            menuContext: .pause,
            gameplayLatched: true,
            hudHealthMatchCount: 0,
            timestamp: 1.45
        ))
        XCTAssertTrue(gate.admits(
            menuContext: .pause,
            gameplayLatched: true,
            hudHealthMatchCount: 0,
            timestamp: 1.5
        ))
    }

    func testStartupMenuDoesNotWaitForGameplayGate() {
        var gate = GameplayMenuEvidenceGate()
        XCTAssertTrue(gate.admits(
            menuContext: .mainTitle,
            gameplayLatched: false,
            hudHealthMatchCount: 0,
            timestamp: 1
        ))
    }

    private func completeSetDetections(
        _ context: LabelingContext,
        selected selectedIdentifier: String? = nil,
        selectorCount: Int = 2
    ) -> [LiveObjectDetection] {
        var result = context.labels.filter {
            !LabelingClassIdentity.matches(
                $0.id, LabelingClassIdentity.selectDecoration
            )
        }.enumerated().map { index, definition in
            let isSelected = selectedIdentifier.map {
                LabelingClassIdentity.matches(definition.id, $0)
            } ?? false
            return detection(
                definition.id,
                x: isSelected ? 0.40 : 0.20,
                y: isSelected ? 0.50 : 0.08 + CGFloat(index) * 0.05,
                width: 0.20
            )
        }
        for index in 0..<selectorCount {
            result.append(detection(
                LabelingClassIdentity.selectDecoration,
                x: index == 0 ? 0.35 : 0.63,
                y: selectedIdentifier == nil ? 0.90 : 0.50,
                width: 0.02
            ))
        }
        return result
    }

    private func completeScore(for context: LabelingContext) -> Double {
        LiveLabelSetScorer.scores(completeSetDetections(context))
            .first { $0.context == context }?.score ?? -.infinity
    }

    private func matchedMenuStencil(
        _ context: LabelingContext,
        selected: String?
    ) -> MenuStencilResult {
        MenuStencilResult(
            context: context,
            isMatch: true,
            confidence: 0.9,
            selectedOption: selected,
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

    private func matchedHUDStencil() -> HUDStencilResult {
        let match = HUDStencilMatch(
            index: 0,
            kind: "main",
            rect: CGRect(x: 40, y: 300, width: 58, height: 40),
            confidence: 0.9
        )
        return HUDStencilResult(
            health: [
                HUDStencilMatch(index: 0, kind: "full", rect: .zero, confidence: 0.9),
                HUDStencilMatch(index: 1, kind: "full", rect: .zero, confidence: 0.9),
            ],
            manaMain: match,
            manaReserves: [],
            healthMasks: [],
            manaMasks: [],
            geo: .zero,
            holdsHealthMask: false,
            sourceTimestamp: 1
        )
    }

    private func detection(
        _ classIdentifier: String,
        x: CGFloat,
        y: CGFloat,
        width: CGFloat,
        height: CGFloat = 0.025
    ) -> LiveObjectDetection {
        LiveObjectDetection(
            classIdentifier: classIdentifier,
            normalizedRect: CGRect(x: x, y: y, width: width, height: height),
            confidence: 0.9,
            sourceFrameIdentifier: 1,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )
    }
}
