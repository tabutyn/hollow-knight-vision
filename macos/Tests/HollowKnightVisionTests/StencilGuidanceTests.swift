import XCTest
@testable import HollowKnightVision

final class StencilGuidanceTests: XCTestCase {
    private func box(_ identifier: String, negative: Bool = false) -> LabelingDraftRectangle {
        .init(id: UUID(), classID: identifier,
              normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.1),
              isNegative: negative)
    }

    func testMenuOrderAndCompletedObjectsDisappearUntilUndone() {
        let first = LabelingContext.mainTitle.labels.first!
        let remaining = StencilLabelSelectionPolicy.availableLabels(
            context: .mainTitle, rectangles: [box(first.id)], mode: .modded, hidesCompleted: true
        )
        XCTAssertEqual(remaining.map(\.name), [
            "Start Game", "Options", "Achievements", "Extras", "Quit Game", "Left Select Decoration"
        ])
        XCTAssertEqual(StencilLabelSelectionPolicy.availableLabels(
            context: .mainTitle, rectangles: [], mode: .modded, hidesCompleted: true
        ).first?.name, "Hollow Knight Title")
    }

    func testDecorationsRequireTwoPositiveBoxesAndRemainEditable() {
        let identifier = LabelingClassIdentity.selectDecoration
        for count in 0...2 {
            let boxes = (0..<count).map { _ in box(identifier) } + [box(identifier, negative: true)]
            let available = StencilLabelSelectionPolicy.availableLabels(
                context: .mainTitle, rectangles: boxes, mode: .modded, hidesCompleted: true
            )
            XCTAssertEqual(available.contains { $0.id == identifier }, count < 2)
            if count < 2 { XCTAssertEqual(available.first { $0.id == identifier }?.name,
                count == 0 ? "Left Select Decoration" : "Right Select Decoration") }
        }
        XCTAssertTrue(StencilLabelSelectionPolicy.availableLabels(
            context: .mainTitle, rectangles: [box(identifier), box(identifier)],
            mode: .modded, hidesCompleted: false
        ).contains { $0.id == identifier })
    }

    func testGameplayObjectsRemainRepeatableAndNormalModeOmitsMods() {
        let boxes = LabelingContext.game.labels.map { box($0.id) }
        XCTAssertEqual(StencilLabelSelectionPolicy.availableLabels(
            context: .game, rectangles: boxes, mode: .normal, hidesCompleted: true
        ), LabelingContext.game.labels)
        XCTAssertFalse(StencilLabelSelectionPolicy.availableLabels(
            context: .options, rectangles: [], mode: .normal, hidesCompleted: true
        ).contains { $0.id == LabelingClassIdentity.mods })
    }

    func testNegativeModeAndFreshProfileChoices() {
        for context in LabelingContext.contractSetCases {
            XCTAssertEqual(LabelingEditorMode.available(in: context).contains(.negative), context == .game)
        }
        XCTAssertFalse(StencilLabelSelectionPolicy.availableLabels(context: .selectProfile,
            rectangles: [], mode: .normal, hidesCompleted: true).contains { $0.id == "select-profile.clear-save" })
        XCTAssertTrue(StencilLabelSelectionPolicy.availableLabels(context: .selectProfile,
            rectangles: [], mode: .normal, hidesCompleted: true, includesDeferredLabels: true)
            .contains { $0.id == "select-profile.clear-save" })
        XCTAssertEqual(StencilInitializationPolicy.requiredContexts.prefix(3), [.mainTitle, .selectProfile, .gameplayHUD])
        XCTAssertEqual(StencilInitializationPolicy.requiredContexts.last, .clearSave)
    }

    func testDeferredClearSaveDisappearsAfterOnePositiveBox() {
        let identifier = StencilInitializationPolicy.deferredProfileClass
        for negative in [false, true] {
            let available = StencilLabelSelectionPolicy.availableLabels(
                context: .selectProfile, rectangles: [box(identifier, negative: negative)],
                mode: .normal, hidesCompleted: true, includesDeferredLabels: true)
            XCTAssertEqual(available.contains { $0.id == identifier }, negative)
        }
    }

    func testDeferredClearSaveFollowsPhaseTwoMenus() {
        let steps = StencilInitializationPolicy.missingSteps(examples: [], languageIdentifier: "en", mode: .normal)
        let deferred = steps.firstIndex(where: StencilInitializationPolicy.isDeferred)!
        let options = steps.lastIndex { $0.context == .options }!
        let confirm = steps.firstIndex { $0.context == .clearSave }!
        XCTAssertLessThan(options, deferred)
        XCTAssertLessThan(deferred, confirm)
        XCTAssertFalse(StencilInitializationPolicy.isFirstPhaseStep(steps[deferred]))
    }

    func testVisibleButtonsAndCampaignInstructionsHaveTranslations() {
        let keys = ["Gameplay", "Label", "Model", "Add", "Modify", "Negative", "Save State", "New", "Delete", "Load",
            "Free Fly", "Current", "Fit", "Object Detection", "Stencil Detection", "Left Select Decoration",
            "Right Select Decoration", "Summary", "Objects", "Screenshots", "Train", "Stop Training", "Recent Frames",
            "profileClearSave", "modelLabelGameplay", "modelOpen", "modelReview", "modelTrain", "modelTraining", "modelReady",
            "profilePhaseDone", "navigateHUD", "drawHealthFirst", "drawHealthSecond", "phase1", "phase2", "phase3",
            "stencilLocked", "modelLocked", "resumeStencil", "deleteScreenshot", "runtimeHelp", "Max Health"]
        for language in HollowKnightMenuLanguage.allCases {
            for key in keys {
                XCTAssertNotNil(StencilGuidanceText.catalog[language.rawValue]?[key], "\(language):\(key)")
            }
        }
    }

    func testAllLanguagesHaveGuidanceAndMatchingPlaceholders() {
        let catalog = StencilGuidanceText.catalog
        let english = catalog["en"]!
        let pattern = try! NSRegularExpression(pattern: "\\{[^}]+\\}")
        func placeholders(_ value: String) -> Set<String> {
            Set(pattern.matches(in: value, range: NSRange(value.startIndex..., in: value)).map {
                (value as NSString).substring(with: $0.range)
            })
        }
        for language in HollowKnightMenuLanguage.allCases {
            let localized = catalog[language.rawValue] ?? [:]
            XCTAssertEqual(Set(localized.keys), Set(english.keys), language.rawValue)
            for (key, value) in english {
                XCTAssertFalse(localized[key, default: ""].isEmpty, "\(language):\(key)")
                XCTAssertEqual(placeholders(localized[key, default: ""]), placeholders(value))
            }
        }
    }

    func testProgressUsesOneReferenceAndRejectsWrongLanguageOrSize() {
        let requirements = StencilInitializationPolicy.requiredCounts(context: .mainTitle, mode: .modded)
        let labels = requirements.flatMap { requirement in
            (0..<requirement.count).map { _ in box(requirement.definition.id) }
        }
        func example(_ rectangles: [LabelingDraftRectangle], language: String = "en", width: Int = 640) -> SavedLabelingExample {
            SavedLabelingExample(directoryURL: URL(fileURLWithPath: "/unused"), manifest: .init(
                schemaVersion: LabelingExampleManifest.currentSchemaVersion, id: UUID(),
                imageIdentifier: UUID(), imageFilename: "image.png", imageWidth: width, imageHeight: 360,
                captureGroupIdentifier: UUID(), contextIdentifier: "main-title",
                annotations: rectangles.map(LabelingExampleAnnotation.init), knownClassIdentifiers: nil,
                languageIdentifier: language, completion: .draft, createdAt: Date()
            ))
        }
        let first = example(Array(labels.prefix(3)))
        let second = example(Array(labels.dropFirst(3)))
        let completeFrench = example(labels, language: "fr")
        let wrongSize = example(labels, width: 1280)
        let examples = [first, second, completeFrench, wrongSize]
        XCTAssertEqual(StencilInitializationPolicy.representativeExample(
            examples: examples, context: .mainTitle, languageIdentifier: "en", mode: .modded
        )?.id, second.id)
        let missing = StencilInitializationPolicy.missingSteps(examples: examples, languageIdentifier: "en", mode: .modded)
        XCTAssertEqual(missing.filter { $0.context == .mainTitle }.count, 3,
                       "Partial screenshots must not combine into a completed stencil.")
        let completed = StencilInitializationPolicy.missingSteps(
            examples: examples + [example(labels)], languageIdentifier: "en", mode: .modded
        )
        XCTAssertEqual(completed.first?.context, .selectProfile)
        XCTAssertFalse(completed.contains { $0.context == .mainTitle })
    }

    func testRequiredScreensFollowContractsAndWindowsCatalogMatches() throws {
        let expected = LabelingContractCatalog.bundled.labelSets.filter {
            $0.requiresAllContextObjects && $0.contextIdentifier != "game"
        }.map(\.contextIdentifier)
        XCTAssertEqual(Set(StencilInitializationPolicy.requiredContexts.map(\.storageIdentifier)), Set(expected))
        XCTAssertEqual(StencilInitializationPolicy.contexts(for: .normal).count, 25)
        XCTAssertFalse(StencilInitializationPolicy.contexts(for: .normal).contains(.mods))
        struct Object: Decodable { let id: String; let name: String }
        struct Context: Decodable { let contextIdentifier: String; let name: String; let objects: [Object] }
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Sources/HollowKnightVision/Resources/labeling-contexts.json")
        let contexts = try JSONDecoder().decode([Context].self, from: Data(contentsOf: url))
        XCTAssertEqual(Set(contexts.map(\.contextIdentifier)), Set(LabelingContext.contractSetCases.map(\.storageIdentifier)))
        for item in contexts {
            let context = try XCTUnwrap(LabelingContext(storageIdentifier: item.contextIdentifier))
            XCTAssertEqual(item.name, context.rawValue)
            XCTAssertEqual(item.objects.map(\.id), context.labels.map(\.id))
            XCTAssertEqual(item.objects.map(\.name), context.labels.map(\.name))
            if StencilInitializationPolicy.requiredContexts.contains(context) {
                for language in HollowKnightMenuLanguage.allCases {
                    let words = StencilGuidanceText.catalog[language.rawValue]!
                    XCTAssertNotNil(words[context.rawValue])
                    for object in context.labels { XCTAssertNotNil(words[object.name]) }
                }
            }
        }
    }

    func testStencilResetArchivesMenuScreensAndPreservesGameplayData() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let support = root.appendingPathComponent("HollowKnightVision")
        let store = LabelingExampleStore(rootURL: support.appendingPathComponent("labeling-v1/examples"))
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360, bitsPerComponent: 8,
            bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        let image = try XCTUnwrap(bitmap.makeImage())
        let menu = try store.saveDraft(image: image, context: .mainTitle, rectangles: [box("main-title.start-game")])
        let game = try store.saveDraft(image: image, context: .game, rectangles: [box("game.playable-knight")])
        let model = support.appendingPathComponent("labeling-v1/model.txt")
        try Data("trained model".utf8).write(to: model)
        let backup = try XCTUnwrap(VisionResetController.resetStencils(supportURL: support))
        XCTAssertFalse(FileManager.default.fileExists(atPath: menu.directoryURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: backup.appendingPathComponent(menu.directoryURL.lastPathComponent).path))
        XCTAssertEqual(try store.loadExamples().map(\.id), [game.id])
        XCTAssertEqual(try String(contentsOf: model, encoding: .utf8), "trained model")
        XCTAssertNil(try VisionResetController.resetStencils(supportURL: support))
    }

    @MainActor func testPartialReferenceResumesEditingAndSavedBackCounts() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "StencilGuidanceTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = LabelingExampleStore(rootURL: root)
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360, bitsPerComponent: 8,
            bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        let image = try XCTUnwrap(bitmap.makeImage())
        let example = try store.saveDraft(image: image, context: .selectProfile, rectangles: [box("shared.back")])
        let coordinator = StencilInitializationCoordinator(store: store, defaults: defaults)
        coordinator.refresh(mode: .modded)
        XCTAssertEqual(coordinator.guidance(labelingContext: nil, selectedClassIdentifier: nil, navigationContext: .selectProfile),
            "Click Stencil and Edit Labels for Select Profile.")
        XCTAssertFalse(coordinator.steps.contains { $0.context == .selectProfile && $0.classIdentifier == "shared.back" })
        XCTAssertNotEqual(coordinator.guidance(labelingContext: .selectProfile, selectedClassIdentifier: "shared.back"),
            "Draw a box around Back.")
        try store.delete(example)
        coordinator.refresh(mode: .modded)
        XCTAssertEqual(coordinator.guidance(labelingContext: nil, selectedClassIdentifier: nil, navigationContext: .selectProfile),
            "Select Start Game to navigate to Select Profile and press L.")
    }

    @MainActor func testEmptySetupIncludesEveryRequiredScreen() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "StencilGuidanceTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let coordinator = StencilInitializationCoordinator(store: LabelingExampleStore(rootURL: root), defaults: defaults)
        coordinator.refresh(mode: .modded)
        XCTAssertEqual(coordinator.labelContextForCapture(recognized: .game, navigation: nil, fallback: .game), .mainTitle)
        XCTAssertEqual(coordinator.labelContextForCapture(recognized: .game, navigation: .options, fallback: .game), .options)
        XCTAssertEqual(coordinator.totalRequiredCount, 190)
        XCTAssertEqual(coordinator.screenCount, 26)
        XCTAssertEqual(coordinator.buttonTitle, "Stencil (0 / 26)")
        XCTAssertEqual(coordinator.completedScreenCount, 0)
        XCTAssertEqual(coordinator.guidance(labelingContext: nil, selectedClassIdentifier: nil),
                       "Navigate to Main Title and press L.")
        XCTAssertEqual(coordinator.guidance(labelingContext: .mainTitle, selectedClassIdentifier: "main-title.start-game"),
                       "Draw a box around Start Game.")
        XCTAssertEqual(coordinator.guidance(labelingContext: nil, selectedClassIdentifier: nil, navigationContext: .options),
                       "Navigate to Options and press L.")
    }
}
