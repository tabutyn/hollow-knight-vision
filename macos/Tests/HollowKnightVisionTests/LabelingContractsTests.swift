import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingContractsTests: XCTestCase {
    func testBundledCatalogDefinesOccurrencePairsAndMultipleSetMemberships() {
        let catalog = LabelingContractCatalog.bundled

        XCTAssertEqual(
            catalog.occurrence(for: LabelingClassIdentity.selectDecoration)?.allowedCounts,
            [0, 2]
        )
        XCTAssertEqual(
            catalog.pairs.first?.classIdentifiers,
            [LabelingClassIdentity.yes, LabelingClassIdentity.no]
        )
        XCTAssertTrue(catalog.sets(containing: LabelingClassIdentity.options).contains {
            $0.contextIdentifier == LabelingContext.mainTitle.storageIdentifier
        })
        XCTAssertTrue(catalog.sets(containing: LabelingClassIdentity.options).contains {
            $0.contextIdentifier == LabelingContext.options.storageIdentifier
        })
    }

    func testMainTitleContractFindsMissingObjectsAndOneSelector() {
        let example = savedExample(
            context: .mainTitle,
            classes: ["main-title.hollow-knight-logo", LabelingClassIdentity.selectDecoration]
        )
        let evaluations = LabelingContractEvaluator.evaluate(example)

        XCTAssertTrue(evaluations.first {
            $0.id == "set.main-title.main-title.start-game"
        }?.isBreach == true)
        XCTAssertTrue(evaluations.first {
            $0.id == "set.main-title.\(LabelingClassIdentity.selectDecoration)"
        }?.isBreach == true)
    }

    func testScreenScaleRequiresTwoCornersAndTwoSelectors() {
        let complete = savedExample(
            context: .screenScale,
            classes: [
                "screen-scale.screen-corner",
                "screen-scale.screen-corner",
                "screen-scale.scale",
                LabelingClassIdentity.selectDecoration,
                LabelingClassIdentity.selectDecoration,
                LabelingClassIdentity.back,
            ]
        )
        XCTAssertFalse(LabelingContractEvaluator.evaluate(complete).contains {
            $0.isBreach
        })

        let missingCorner = savedExample(
            context: .screenScale,
            classes: [
                "screen-scale.screen-corner",
                "screen-scale.scale",
                LabelingClassIdentity.selectDecoration,
                LabelingClassIdentity.selectDecoration,
                LabelingClassIdentity.back,
            ]
        )
        XCTAssertTrue(LabelingContractEvaluator.evaluate(missingCorner).contains {
            $0.id == "set.screen-scale.screen-scale.screen-corner" && $0.isBreach
        })

        let missingSelector = savedExample(
            context: .screenScale,
            classes: [
                "screen-scale.screen-corner",
                "screen-scale.screen-corner",
                "screen-scale.scale",
                LabelingClassIdentity.selectDecoration,
                LabelingClassIdentity.back,
            ]
        )
        XCTAssertTrue(LabelingContractEvaluator.evaluate(missingSelector).contains {
            $0.id == "set.screen-scale.\(LabelingClassIdentity.selectDecoration)"
                && $0.isBreach
        })
    }

    func testGameplayGeodeIsOptional() {
        let withoutGeode = savedExample(
            context: .game,
            classes: ["game.playable-knight", "game.mana", "game.health"]
        )

        XCTAssertFalse(LabelingContractEvaluator.evaluate(withoutGeode).contains {
            $0.id == "set.game.game.geo" && $0.isBreach
        })
    }

    func testLoadingExemptionsRemovesObsoleteGameplayGeodeMark() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-contract-exemption-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let example = savedExample(context: .game, classes: ["game.health"])
        let storedExample = SavedLabelingExample(
            directoryURL: root,
            manifest: example.manifest
        )
        let store = LabelingContractExemptionStore()
        try store.save(["set.game.game.geo", "screenshot.has-label"], for: storedExample)

        XCTAssertEqual(store.load(for: storedExample), ["screenshot.has-label"])
        let persisted = try JSONDecoder().decode(
            [String].self,
            from: Data(contentsOf: root.appendingPathComponent(
                LabelingContractExemptionStore.filename
            ))
        )
        XCTAssertEqual(persisted, ["screenshot.has-label"])
    }

    func testExemptionSuppressesBreach() throws {
        let example = savedExample(context: .quitToMenu, classes: [LabelingClassIdentity.yes])
        let initial = LabelingContractEvaluator.evaluate(example)
        let pair = try XCTUnwrap(initial.first { $0.id == "pair.yes-no" })
        XCTAssertTrue(pair.isBreach)

        let exempted = LabelingContractEvaluator.evaluate(example, exemptions: [pair.id])
        let exemptedPair = try XCTUnwrap(exempted.first { $0.id == pair.id })
        XCTAssertFalse(exemptedPair.isBreach)
        XCTAssertTrue(exemptedPair.isExempt)
    }

    func testAutoAdvanceWaitsForRequiredCountAndRepeatableObjectsStaySelected() {
        var rectangles = [rectangle(LabelingClassIdentity.selectDecoration)]
        XCTAssertFalse(LabelingContractEvaluator.shouldAdvance(
            afterAdding: LabelingClassIdentity.selectDecoration,
            in: .mainTitle,
            rectangles: rectangles
        ))
        rectangles.append(rectangle(LabelingClassIdentity.selectDecoration))
        XCTAssertTrue(LabelingContractEvaluator.shouldAdvance(
            afterAdding: LabelingClassIdentity.selectDecoration,
            in: .mainTitle,
            rectangles: rectangles
        ))
        XCTAssertTrue(LabelingContractEvaluator.shouldAdvance(
            afterAdding: "main-title.start-game",
            in: .mainTitle,
            rectangles: [rectangle("main-title.start-game")]
        ))
        XCTAssertFalse(LabelingContractEvaluator.shouldAdvance(
            afterAdding: "enemies.crawlid",
            in: .enemies,
            rectangles: [rectangle("enemies.crawlid")]
        ))
    }

    func testLegacyContextInferenceRepairsOnlyClearMultiObjectMismatch() {
        let gameplay = ["game.playable-knight", "game.mana", "game.health", "game.geo"]
            .map { LabelingExampleAnnotation(rectangle($0)) }
        XCTAssertEqual(
            LabelingExampleContextInference.inferredContextIdentifier(
                current: LabelingContext.options.storageIdentifier,
                annotations: gameplay
            ),
            LabelingContext.game.storageIdentifier
        )

        let ambiguous = [LabelingClassIdentity.yes, LabelingClassIdentity.no]
            .map { LabelingExampleAnnotation(rectangle($0)) }
        XCTAssertEqual(
            LabelingExampleContextInference.inferredContextIdentifier(
                current: LabelingContext.shared.storageIdentifier,
                annotations: ambiguous
            ),
            LabelingContext.shared.storageIdentifier
        )
    }

    func testContextInferenceUsesCompleteContractInsteadOfSharedObjectOverlap() {
        let brightness = [
            LabelingClassIdentity.brightness,
            "brightness.pointer",
            LabelingClassIdentity.back,
            LabelingClassIdentity.selectDecoration,
            LabelingClassIdentity.selectDecoration,
        ].map { LabelingExampleAnnotation(rectangle($0)) }

        XCTAssertEqual(
            LabelingExampleContextInference.inferredContextIdentifier(
                current: LabelingContext.screenScale.storageIdentifier,
                annotations: brightness
            ),
            LabelingContext.brightness.storageIdentifier
        )

        let incomplete = [
            LabelingClassIdentity.back,
            LabelingClassIdentity.selectDecoration,
            LabelingClassIdentity.selectDecoration,
        ].map { LabelingExampleAnnotation(rectangle($0)) }
        XCTAssertEqual(
            LabelingExampleContextInference.inferredContextIdentifier(
                current: LabelingContext.screenScale.storageIdentifier,
                annotations: incomplete
            ),
            LabelingContext.screenScale.storageIdentifier
        )
    }

    func testEmptyScreenshotIsContractBreach() {
        let example = savedExample(context: .mainTitle, classes: [])
        XCTAssertTrue(LabelingContractEvaluator.evaluate(example).contains {
            $0.id == "screenshot.has-label" && $0.isBreach
        })
    }

    private func savedExample(
        context: LabelingContext,
        classes: [String]
    ) -> SavedLabelingExample {
        let id = UUID()
        return SavedLabelingExample(
            directoryURL: URL(fileURLWithPath: "/tmp/\(id.uuidString)"),
            manifest: LabelingExampleManifest(
                schemaVersion: LabelingExampleManifest.currentSchemaVersion,
                id: id,
                imageIdentifier: UUID(),
                imageFilename: "image.png",
                imageWidth: 640,
                imageHeight: 360,
                captureGroupIdentifier: UUID(),
                contextIdentifier: context.storageIdentifier,
                annotations: classes.map { LabelingExampleAnnotation(rectangle($0)) },
                knownClassIdentifiers: classes,
                completion: .draft,
                createdAt: Date()
            )
        )
    }

    private func rectangle(_ classIdentifier: String) -> LabelingDraftRectangle {
        LabelingDraftRectangle(
            id: UUID(),
            classID: classIdentifier,
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
        )
    }
}
