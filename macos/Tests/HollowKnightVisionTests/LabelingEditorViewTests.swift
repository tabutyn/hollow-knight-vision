import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LabelingEditorViewTests: XCTestCase {
    func testAddModifyAndDeleteAreSeparateModes() {
        XCTAssertEqual(LabelingEditorMode.allCases.map(\.rawValue), [
            "Add", "Modify", "Delete", "Negative",
        ])
        XCTAssertEqual(LabelingEditorMode.allCases.map(\.shortcutCharacter), [
            "1", "2", "3", "4",
        ])
    }

    func testArrowSetCyclingWrapsThroughEverySet() {
        let sets = LabelingContext.selectableCases
        for (index, set) in sets.enumerated() {
            XCTAssertEqual(set.cycled(offset: 1), sets[(index + 1) % sets.count])
            XCTAssertEqual(set.cycled(offset: -1), sets[(index - 1 + sets.count) % sets.count])
        }
    }

    func testArrowCyclingWrapsThroughContextObjects() {
        let context = LabelingContext.mainTitle
        let first = context.labels[0].id
        let last = context.labels[context.labels.count - 1].id

        XCTAssertEqual(context.cycledClassIdentifier(from: first, offset: -1), last)
        XCTAssertEqual(context.cycledClassIdentifier(from: last, offset: 1), first)
        XCTAssertEqual(
            context.cycledClassIdentifier(from: context.labels[2].id, offset: 1),
            context.labels[3].id
        )
    }

    func testRecentFrameOpeningSelectsAnObjectFromTheFrameContext() {
        XCTAssertEqual(
            LabelingFrameOpeningPolicy.classIdentifierForOpening(
                context: .game,
                annotationClassIdentifiers: ["game.mana", "game.playable-knight"]
            ),
            "game.mana"
        )
        XCTAssertEqual(
            LabelingFrameOpeningPolicy.classIdentifierForOpening(
                context: .options,
                annotationClassIdentifiers: ["game.mana", LabelingClassIdentity.audio]
            ),
            LabelingClassIdentity.audio
        )
    }

    func testErrorFrameOpensSelectedUnlabeledObjectAndKeepsExistingBoxes() throws {
        let optionsID = UUID()
        let manifest = LabelingExampleManifest(
            schemaVersion: LabelingExampleManifest.currentSchemaVersion,
            id: UUID(),
            imageIdentifier: UUID(),
            imageFilename: "image.png",
            imageWidth: 640,
            imageHeight: 360,
            captureGroupIdentifier: UUID(),
            contextIdentifier: LabelingContext.mainTitle.storageIdentifier,
            annotations: [LabelingExampleAnnotation(LabelingDraftRectangle(
                id: optionsID,
                classID: "main-title.options",
                normalizedRect: CGRect(x: 0.4, y: 0.4, width: 0.2, height: 0.1)
            ))],
            knownClassIdentifiers: [
                "main-title.options", LabelingClassIdentity.selectDecoration,
            ],
            completion: .draft,
            createdAt: Date()
        )
        let state = try XCTUnwrap(LabelingFrameOpeningPolicy.stateForOpening(
            manifest,
            preferredClassIdentifier: LabelingClassIdentity.selectDecoration
        ))

        XCTAssertEqual(state.context, .mainTitle)
        XCTAssertEqual(state.selectedClassIdentifier, LabelingClassIdentity.selectDecoration)
        XCTAssertEqual(state.draft.rectangles.map(\.id), [optionsID])
    }

    func testRecentFrameOpenStateRestoresEverySavedRectangle() throws {
        let firstID = UUID()
        let secondID = UUID()
        let manifest = LabelingExampleManifest(
            schemaVersion: LabelingExampleManifest.currentSchemaVersion,
            id: UUID(),
            imageIdentifier: UUID(),
            imageFilename: "image.png",
            imageWidth: 640,
            imageHeight: 360,
            captureGroupIdentifier: UUID(),
            contextIdentifier: LabelingContext.game.storageIdentifier,
            annotations: [
                LabelingExampleAnnotation(LabelingDraftRectangle(
                    id: firstID,
                    classID: "game.playable-knight",
                    normalizedRect: CGRect(x: 0.3, y: 0.5, width: 0.1, height: 0.2)
                )),
                LabelingExampleAnnotation(LabelingDraftRectangle(
                    id: secondID,
                    classID: "game.mana",
                    normalizedRect: CGRect(x: 0.05, y: 0.05, width: 0.1, height: 0.1)
                )),
            ],
            knownClassIdentifiers: ["game.mana", "game.playable-knight"],
            completion: .draft,
            createdAt: Date()
        )

        let state = try XCTUnwrap(
            LabelingFrameOpeningPolicy.stateForOpening(manifest)
        )
        XCTAssertEqual(state.context, .game)
        XCTAssertEqual(state.selectedClassIdentifier, "game.playable-knight")
        XCTAssertEqual(state.draft.rectangles.map(\.id), [firstID, secondID])
    }

    func testSharedModelUsesFullTrainingRun() {
        XCTAssertEqual(
            LabelingTrainingConfiguration.forModel(
                identifier: LabelingModelIdentity.sharedObjectModel
            ),
            LabelingTrainingConfiguration(maximumIterations: 100, gridSize: 13)
        )
        XCTAssertEqual(
            LabelingTrainingConfiguration.forModel(identifier: "game.mana"),
            LabelingTrainingConfiguration(maximumIterations: 10, gridSize: 13)
        )
    }

    func testAutosaveStartsWithFirstBoxAndKeepsEmptySavedExampleEdits() {
        XCTAssertFalse(LabelingAutosavePolicy.shouldPersist(
            rectangles: [],
            isEditingSavedExample: false
        ))
        XCTAssertTrue(LabelingAutosavePolicy.shouldPersist(
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: LabelingClassIdentity.selectDecoration,
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
            )],
            isEditingSavedExample: false
        ))
        XCTAssertTrue(LabelingAutosavePolicy.shouldPersist(
            rectangles: [],
            isEditingSavedExample: true
        ))
    }

    func testLabelSetsContainTheirExpectedObjects() {
        XCTAssertEqual(LabelingContext.mainTitle.labels.map(\.name), [
            "Hallow Knight Title", "Start Game", "Options", "Achievements", "Extras",
            "Quit Game", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.options.labels.map(\.name), [
            "Options", "Game", "Audio", "Video", "Controller", "Keyboard", "Mods", "Back",
            "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.gameOptions.labels.map(\.name), [
            "Game Options", "Language", "Camera Shake", "HUD Apperance",
            "Show Achievements", "Backer Credits", "Reset Defaults", "Back",
            "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.audio.labels.map(\.name), [
            "Audio", "Master Volume", "Sound Volume", "Music Volume", "Reset Defaults", "Back",
            "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.video.labels.map(\.name), [
            "Video", "Resolution", "Full Screen", "V-Sync", "Frame Rate Cap", "Screen Scale",
            "Brightness", "Advanced Settings", "Reset Defaults", "Back",
            "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.screenScale.labels.map(\.name), [
            "Scale", "Screen Corner", "Select Decoration", "Back",
        ])
        XCTAssertEqual(LabelingContext.brightness.labels.map(\.name), [
            "Brightness", "Pointer", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.videoAdvancedSettings.labels.map(\.name), [
            "Advanced Settings", "Particle Effects", "Blur Quality", "Dithering",
            "Film Grain", "Reset Defaults", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.controller.labels.map(\.name), [
            "Controller", "Controller Diagram", "Remap Controlls", "Advanced Settings", "Back",
            "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.remapController.labels.map(\.name), [
            "Remap Controller", "Jump", "Attack", "Dash", "Focus / Cast", "Quick Map",
            "Super Dash", "Dream Nail", "Quick Cast", "Reset Defaults", "Done",
            "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.controllerAdvancedSettings.labels.map(\.name), [
            "Advanced Settings", "Vibration", "Native Controller Input", "MFI",
            "Reset Defaults", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.keyboard.labels.map(\.name), [
            "Keyboard", "Up", "Down", "Jump", "Attack", "Dash", "Focus / Cast", "Left",
            "Right", "Quick Map", "Super Dash", "Dream Nail", "Quick Cast", "Inventory",
            "Reset Defaults", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.mods.labels.map(\.name), [
            "Mods", "Mod Decoration", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.achievements.labels.map(\.name), [
            "Achievements", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.selectProfile.labels.map(\.name), [
            "Select Profile", "1.", "2.", "3.", "4.", "Clear Save", "Back",
            "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.clearSave.labels.map(\.name), [
            "Clear Save?", "Yes", "No", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.game.labels.map(\.name), [
            "Hallow Knight", "Mana", "Health", "Geode", "Crawlid", "Vengfly",
            "Shade", "Geo Deposit", "Lifeblood Cacoon", "Sign",
        ])
        XCTAssertEqual(LabelingContext.enemies.labels.map(\.name), [
            "Crawlid", "Vengfly", "Shade",
        ])
        XCTAssertEqual(LabelingContext.world.labels.map(\.name), [
            "Geo Deposit", "Lifeblood Cacoon", "Sign",
        ])
        XCTAssertEqual(LabelingContext.pause.labels.map(\.name), [
            "Header", "Continue", "Options", "Quit To Menu", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.quitToMenu.labels.map(\.name), [
            "Quit To Menu", "Yes", "No", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.extras.labels.map(\.name), [
            "Extras", "Menu Style", "Credits", "Hidden Dreams", "The Grimm Troupe",
            "Lifeblood", "Godmaster", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.hiddenDreams.labels.map(\.name), [
            "Hidden Dreams", "Hidden Dreams Poster", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.grimmTroupe.labels.map(\.name), [
            "The Grimm Troupe", "Grimm Troupe Poster", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.lifeblood.labels.map(\.name), [
            "Lifeblood", "Lifeblood Poster", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.godmaster.labels.map(\.name), [
            "Godmaster", "Godmaster Poster", "Back", "Select Decoration",
        ])
        XCTAssertEqual(LabelingContext.inventory.labels.map(\.name), [
            "Inventory", "Inventory Corner",
        ])
        XCTAssertEqual(LabelingContext.quitGame.labels.map(\.name), [
            "Quit Game", "Yes", "No", "Select Decoration",
        ])
    }

    func testSharedObjectsBelongToEveryApplicableSet() {
        XCTAssertEqual(LabelingContext.sets(containing: LabelingClassIdentity.options), [
            .mainTitle, .options, .pause,
        ])
        XCTAssertTrue(LabelingContext.sets(
            containing: LabelingClassIdentity.selectDecoration
        ).contains(.quitGame))
        XCTAssertEqual(LabelingContext.sets(containing: LabelingClassIdentity.yes), [
            .clearSave, .quitToMenu, .quitGame,
        ])
        XCTAssertFalse(LabelingContext.selectableCases.contains(.shared))
        XCTAssertFalse(LabelingContext.contractSetCases.contains(.enemies))
        XCTAssertFalse(LabelingContext.contractSetCases.contains(.world))
        XCTAssertEqual(
            Set(LabelingContext.allCases.flatMap(\.labels).map(\.id)),
            LabelingCatalogPolicy.classIdentifiers
        )
    }

    func testClassIdentifiersResolveToTheirCategories() {
        XCTAssertEqual(
            LabelingContext.containing(classIdentifier: "enemies.crawlid"),
            .game
        )
        XCTAssertEqual(
            LabelingContext.containing(classIdentifier: "world.geo-deposit"),
            .game
        )
    }

    func testLegacyMenuLabelsMigrateToTheirSingleCategoryOwners() {
        let migrations = [
            "select-profile.back": LabelingClassIdentity.back,
            "main-title.options": LabelingClassIdentity.options,
            "pause.options": LabelingClassIdentity.options,
            "options.options": LabelingClassIdentity.options,
            "main-title.achievements": LabelingClassIdentity.achievements,
            "main-title.extras": LabelingClassIdentity.extras,
            "options.audio": LabelingClassIdentity.audio,
            "options.video": LabelingClassIdentity.video,
            "options.controller": LabelingClassIdentity.controller,
            "options.keyboard": LabelingClassIdentity.keyboard,
            "options.mods": LabelingClassIdentity.mods,
            "quit-to-menu.yes": LabelingClassIdentity.yes,
            "quit-to-menu.no": LabelingClassIdentity.no,
            "pause.quit-to-menu": LabelingClassIdentity.quitToMenu,
            "main-title.quit-game": LabelingClassIdentity.quitGame,
        ]

        for (legacyIdentifier, canonicalIdentifier) in migrations {
            XCTAssertEqual(
                LabelingClassIdentity.canonicalIdentifier(legacyIdentifier),
                canonicalIdentifier
            )
        }
        XCTAssertEqual(
            LabelingClassIdentity.canonicalIdentifier("options.game"),
            "options.game"
        )
        XCTAssertNotEqual(
            LabelingContext.options.labels.first { $0.name == "Game" }?.id,
            LabelingContext.gameOptions.labels.first { $0.name == "Game Options" }?.id
        )
    }

    func testObjectExampleUsesSelectedObjectsCategoryWithoutDroppingOtherBoxes() throws {
        let enemyID = UUID()
        let worldID = UUID()
        let manifest = LabelingExampleManifest(
            schemaVersion: LabelingExampleManifest.currentSchemaVersion,
            id: UUID(),
            imageIdentifier: UUID(),
            imageFilename: "image.png",
            imageWidth: 640,
            imageHeight: 360,
            captureGroupIdentifier: UUID(),
            contextIdentifier: LabelingContext.world.storageIdentifier,
            annotations: [
                LabelingExampleAnnotation(LabelingDraftRectangle(
                    id: enemyID,
                    classID: "enemies.crawlid",
                    normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.1)
                )),
                LabelingExampleAnnotation(LabelingDraftRectangle(
                    id: worldID,
                    classID: "world.sign",
                    normalizedRect: CGRect(x: 0.6, y: 0.2, width: 0.1, height: 0.2)
                )),
            ],
            knownClassIdentifiers: ["enemies.crawlid", "world.sign"],
            completion: .draft,
            createdAt: Date()
        )
        let state = try XCTUnwrap(LabelingFrameOpeningPolicy.stateForOpening(
            manifest,
            preferredClassIdentifier: "enemies.crawlid"
        ))

        XCTAssertEqual(state.context, .game)
        XCTAssertEqual(state.selectedClassIdentifier, "enemies.crawlid")
        XCTAssertEqual(state.draft.rectangles.map(\.id), [enemyID, worldID])
    }

    func testOneCurrentOrSavedInstanceEnablesTrainingForSelectedObject() {
        let mana = LabelingDraftRectangle(
            id: UUID(),
            classID: "game.mana",
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
        )

        XCTAssertFalse(LabelingTrainingEligibility.canTrain(
            classIdentifier: "game.health",
            currentRectangles: [mana],
            savedExampleIdentifiers: [],
            editingExampleIdentifier: nil
        ))
        XCTAssertTrue(LabelingTrainingEligibility.canTrain(
            classIdentifier: "game.mana",
            currentRectangles: [mana],
            savedExampleIdentifiers: [],
            editingExampleIdentifier: nil
        ))
        let savedExampleID = UUID()
        XCTAssertTrue(LabelingTrainingEligibility.canTrain(
            classIdentifier: "game.health",
            currentRectangles: [],
            savedExampleIdentifiers: [savedExampleID],
            editingExampleIdentifier: nil
        ))
        XCTAssertFalse(LabelingTrainingEligibility.canTrain(
            classIdentifier: "game.health",
            currentRectangles: [],
            savedExampleIdentifiers: [savedExampleID],
            editingExampleIdentifier: savedExampleID
        ))
    }

    func testNegativeBoxDoesNotCountAsPositiveTrainingInstance() {
        let negative = LabelingDraftRectangle(
            id: UUID(),
            classID: "shared.select-decoration",
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2),
            isNegative: true
        )

        XCTAssertFalse(LabelingTrainingEligibility.canTrain(
            classIdentifier: "shared.select-decoration",
            currentRectangles: [negative],
            savedExampleIdentifiers: [],
            editingExampleIdentifier: nil
        ))
    }

    func testRepeatedClassInstancesCanBeSelectedOrAddedIndependently() throws {
        let firstID = UUID()
        let secondID = UUID()
        var draft = LabelingDraftState(rectangles: [
            LabelingDraftRectangle(
                id: firstID,
                classID: LabelingClassIdentity.selectDecoration,
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.1, height: 0.1)
            ),
            LabelingDraftRectangle(
                id: secondID,
                classID: LabelingClassIdentity.selectDecoration,
                normalizedRect: CGRect(x: 0.4, y: 0.1, width: 0.1, height: 0.1)
            ),
        ])

        XCTAssertEqual(
            draft.select(
                classID: LabelingClassIdentity.selectDecoration,
                at: CGPoint(x: 0.45, y: 0.15)
            )?.id,
            secondID
        )
        XCTAssertNil(draft.select(
                classID: LabelingClassIdentity.selectDecoration,
            at: CGPoint(x: 0.75, y: 0.75)
        ))
        let thirdID = draft.beginRectangle(
                classID: LabelingClassIdentity.selectDecoration,
            at: CGPoint(x: 0.75, y: 0.75)
        )
        draft.updateRectangle(
            id: thirdID,
            normalizedRect: CGRect(x: 0.75, y: 0.75, width: 0.1, height: 0.1)
        )
        draft.finishRectangle(id: thirdID)

        XCTAssertEqual(draft.rectangles.count, 3)
        XCTAssertEqual(draft.rectangles.filter {
            $0.classID == LabelingClassIdentity.selectDecoration
        }.count, 3)
    }

    func testAspectFitGeometryAccountsForLetterboxing() {
        let layout = LabelingImageLayout(
            imageSize: CGSize(width: 1920, height: 1080),
            containerSize: CGSize(width: 1000, height: 1000)
        )
        XCTAssertEqual(layout.fittedRect.origin.x, 0, accuracy: 0.0001)
        XCTAssertEqual(layout.fittedRect.origin.y, 218.75, accuracy: 0.0001)
        XCTAssertEqual(layout.fittedRect.width, 1000, accuracy: 0.0001)
        XCTAssertEqual(layout.fittedRect.height, 562.5, accuracy: 0.0001)
    }

    func testDragNormalizesAndClampsToCleanImage() throws {
        let layout = LabelingImageLayout(
            imageSize: CGSize(width: 640, height: 360),
            containerSize: CGSize(width: 1280, height: 800)
        )
        let normalized = try XCTUnwrap(layout.normalizedRect(
            from: CGPoint(x: 320, y: 220),
            to: CGPoint(x: 1500, y: 900)
        ))
        XCTAssertEqual(normalized.origin.x, 0.25, accuracy: 0.0001)
        XCTAssertEqual(normalized.origin.y, 0.25, accuracy: 0.0001)
        XCTAssertEqual(normalized.width, 0.75, accuracy: 0.0001)
        XCTAssertEqual(normalized.height, 0.75, accuracy: 0.0001)
    }

    func testNormalizedRectangleTracksWindowResize() throws {
        let normalized = CGRect(x: 0.2, y: 0.25, width: 0.3, height: 0.4)
        let compact = LabelingImageLayout(
            imageSize: CGSize(width: 640, height: 360),
            containerSize: CGSize(width: 640, height: 480)
        )
        let large = LabelingImageLayout(
            imageSize: CGSize(width: 640, height: 360),
            containerSize: CGSize(width: 1280, height: 960)
        )
        let compactRect = try XCTUnwrap(compact.displayRect(for: normalized))
        let largeRect = try XCTUnwrap(large.displayRect(for: normalized))
        XCTAssertEqual(largeRect.minX, compactRect.minX * 2, accuracy: 0.0001)
        XCTAssertEqual(largeRect.minY, compactRect.minY * 2, accuracy: 0.0001)
        XCTAssertEqual(largeRect.width, compactRect.width * 2, accuracy: 0.0001)
        XCTAssertEqual(largeRect.height, compactRect.height * 2, accuracy: 0.0001)
    }

    func testZoomAndPanTransformImageAndAnnotationsTogether() throws {
        let layout = LabelingImageLayout(
            imageSize: CGSize(width: 640, height: 360),
            containerSize: CGSize(width: 1280, height: 800),
            zoomScale: 2,
            panOffset: CGSize(width: 30, height: -20)
        )
        XCTAssertEqual(layout.fittedRect, CGRect(x: -610, y: -340, width: 2560, height: 1440))

        let display = try XCTUnwrap(layout.displayRect(
            for: CGRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5)
        ))
        XCTAssertEqual(display, CGRect(x: 30, y: 20, width: 1280, height: 720))
    }

    func testZoomKeepsExactImagePixelUnderCursorWithoutEdgeClamping() throws {
        let imageSize = CGSize(width: 640, height: 360)
        let containerSize = CGSize(width: 1280, height: 800)
        let oldZoom: CGFloat = 2
        let oldPan = CGSize(width: 500, height: -270)
        let cursor = CGPoint(x: 310, y: 260)
        let oldLayout = LabelingImageLayout(
            imageSize: imageSize,
            containerSize: containerSize,
            zoomScale: oldZoom,
            panOffset: oldPan
        )
        let imagePoint = try XCTUnwrap(oldLayout.normalizedPoint(for: cursor))
        let newZoom: CGFloat = 3.5
        let newPan = LabelingViewportTransform.zoomedPan(
            currentPan: oldPan,
            currentZoom: oldZoom,
            newZoom: newZoom,
            focus: cursor,
            containerSize: containerSize
        )
        let newLayout = LabelingImageLayout(
            imageSize: imageSize,
            containerSize: containerSize,
            zoomScale: newZoom,
            panOffset: newPan
        )
        let anchoredImagePoint = try XCTUnwrap(newLayout.normalizedPoint(for: cursor))

        XCTAssertEqual(anchoredImagePoint.x, imagePoint.x, accuracy: 0.000_001)
        XCTAssertEqual(anchoredImagePoint.y, imagePoint.y, accuracy: 0.000_001)
        XCTAssertGreaterThan(abs(newPan.width), containerSize.width / 2)
    }

    func testAnnotationOutlineCanRenderOutsideSavedBounds() {
        let savedBounds = CGRect(x: 100, y: 80, width: 200, height: 120)
        let lineWidth: CGFloat = 3
        let outsideOutlineFrame = savedBounds.insetBy(dx: -lineWidth, dy: -lineWidth)

        XCTAssertEqual(outsideOutlineFrame.minX + lineWidth, savedBounds.minX, accuracy: 0.0001)
        XCTAssertEqual(outsideOutlineFrame.minY + lineWidth, savedBounds.minY, accuracy: 0.0001)
        XCTAssertEqual(outsideOutlineFrame.maxX - lineWidth, savedBounds.maxX, accuracy: 0.0001)
        XCTAssertEqual(outsideOutlineFrame.maxY - lineWidth, savedBounds.maxY, accuracy: 0.0001)
    }

    func testDragCannotStartInLetterbox() {
        let layout = LabelingImageLayout(
            imageSize: CGSize(width: 1920, height: 1080),
            containerSize: CGSize(width: 1000, height: 1000)
        )
        XCTAssertNil(layout.normalizedRect(
            from: CGPoint(x: 500, y: 100),
            to: CGPoint(x: 700, y: 400)
        ))
    }

    func testDraftKeepsMultipleRectanglesAndSelectsTopmostHit() throws {
        var draft = LabelingDraftState()
        let firstID = draft.beginRectangle(
            classID: "game.playable-knight",
            at: CGPoint(x: 0.1, y: 0.1)
        )
        draft.updateRectangle(
            id: firstID,
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.4, height: 0.4)
        )
        draft.finishRectangle(id: firstID)

        let secondID = draft.beginRectangle(classID: "game.health", at: CGPoint(x: 0.2, y: 0.2))
        draft.updateRectangle(
            id: secondID,
            normalizedRect: CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.4)
        )
        draft.finishRectangle(id: secondID)

        XCTAssertEqual(draft.rectangles.count, 2)
        XCTAssertEqual(draft.select(at: CGPoint(x: 0.3, y: 0.3))?.id, secondID)
        XCTAssertEqual(draft.select(at: CGPoint(x: 0.15, y: 0.15))?.id, firstID)
        XCTAssertNil(draft.select(at: CGPoint(x: 0.9, y: 0.9)))
    }

    func testObjectSelectionUsesClassInsteadOfOverlappingGeometry() throws {
        var draft = LabelingDraftState()
        let manaID = draft.beginRectangle(classID: "game.mana", at: CGPoint(x: 0.1, y: 0.1))
        draft.updateRectangle(
            id: manaID,
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5)
        )
        draft.finishRectangle(id: manaID)

        XCTAssertNil(draft.select(classID: "game.health"))
        let healthID = draft.beginRectangle(classID: "game.health", at: CGPoint(x: 0.2, y: 0.2))
        draft.updateRectangle(
            id: healthID,
            normalizedRect: CGRect(x: 0.2, y: 0.2, width: 0.5, height: 0.5)
        )
        draft.finishRectangle(id: healthID)

        XCTAssertEqual(draft.select(classID: "game.mana")?.id, manaID)
        XCTAssertEqual(draft.select(classID: "game.health")?.id, healthID)
    }

    func testDeleteAndUndoRestoreRectangleAndSelection() throws {
        var draft = LabelingDraftState()
        let id = draft.beginRectangle(classID: "game.geo", at: CGPoint(x: 0.1, y: 0.1))
        draft.updateRectangle(
            id: id,
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
        )
        draft.finishRectangle(id: id)

        draft.deleteSelected()
        XCTAssertTrue(draft.rectangles.isEmpty)
        XCTAssertFalse(draft.canDelete)

        draft.undo()
        XCTAssertEqual(draft.rectangles.map(\.id), [id])
        XCTAssertEqual(draft.selectedID, id)
        XCTAssertTrue(draft.canDelete)
    }

    func testNegativeCorrectionConvertsTopmostPositiveBoxAndCanBeUndone() throws {
        var draft = LabelingDraftState()
        let lowerID = draft.beginRectangle(classID: "game.mana", at: CGPoint(x: 0.1, y: 0.1))
        draft.updateRectangle(
            id: lowerID,
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5)
        )
        draft.finishRectangle(id: lowerID)
        let upperID = draft.beginRectangle(classID: "enemies.crawlid", at: CGPoint(x: 0.2, y: 0.2))
        draft.updateRectangle(
            id: upperID,
            normalizedRect: CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.4)
        )
        draft.finishRectangle(id: upperID)

        let corrected = draft.markNegative(at: CGPoint(x: 0.3, y: 0.3))

        XCTAssertEqual(corrected?.id, upperID)
        XCTAssertEqual(corrected?.classID, "enemies.crawlid")
        XCTAssertTrue(corrected?.isNegative == true)
        XCTAssertFalse(draft.rectangles.first(where: { $0.id == lowerID })?.isNegative == true)
        XCTAssertEqual(draft.selectedID, upperID)

        draft.undo()
        XCTAssertFalse(draft.rectangles.first(where: { $0.id == upperID })?.isNegative == true)
    }

    func testResizeCanBeUndone() throws {
        var draft = LabelingDraftState()
        let id = draft.beginRectangle(
            classID: "game.playable-knight",
            at: CGPoint(x: 0.1, y: 0.1)
        )
        draft.updateRectangle(
            id: id,
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
        )
        draft.finishRectangle(id: id)

        XCTAssertTrue(draft.beginModification(id: id))
        draft.updateRectangle(
            id: id,
            normalizedRect: CGRect(x: 0.2, y: 0.25, width: 0.3, height: 0.4)
        )
        draft.finishRectangle(id: id)
        XCTAssertEqual(
            draft.selectedRectangle?.normalizedRect,
            CGRect(x: 0.2, y: 0.25, width: 0.3, height: 0.4)
        )

        draft.undo()
        XCTAssertEqual(
            draft.selectedRectangle?.normalizedRect,
            CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
        )
    }

    func testMovingRectangleKeepsSizeAndClampsInsideImage() {
        let original = CGRect(x: 0.2, y: 0.3, width: 0.25, height: 0.4)
        let translated = LabelingRectangleGeometry.translated(
            original,
            by: CGSize(width: 0.1, height: -0.2)
        )
        XCTAssertEqual(translated.minX, 0.3, accuracy: 0.0001)
        XCTAssertEqual(translated.minY, 0.1, accuracy: 0.0001)
        XCTAssertEqual(translated.width, 0.25, accuracy: 0.0001)
        XCTAssertEqual(translated.height, 0.4, accuracy: 0.0001)
        XCTAssertEqual(
            LabelingRectangleGeometry.translated(
                original,
                by: CGSize(width: 2, height: 2)
            ),
            CGRect(x: 0.75, y: 0.6, width: 0.25, height: 0.4)
        )
    }

    func testMoveCanBeUndone() {
        var draft = LabelingDraftState()
        let original = CGRect(x: 0.2, y: 0.3, width: 0.25, height: 0.4)
        let id = draft.beginRectangle(classID: "game.mana", at: original.origin)
        draft.updateRectangle(id: id, normalizedRect: original)
        draft.finishRectangle(id: id)

        XCTAssertTrue(draft.beginModification(id: id))
        draft.updateRectangle(
            id: id,
            normalizedRect: LabelingRectangleGeometry.translated(
                original,
                by: CGSize(width: 0.1, height: -0.2)
            )
        )
        draft.finishRectangle(id: id)
        draft.undo()

        XCTAssertEqual(draft.selectedRectangle?.normalizedRect, original)
    }

    func testTinyRectangleRollsBackWithoutLeavingUndoEntry() {
        var draft = LabelingDraftState()
        let id = draft.beginRectangle(classID: "game.health", at: CGPoint(x: 0.1, y: 0.1))
        draft.updateRectangle(
            id: id,
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.002, height: 0.2)
        )
        draft.finishRectangle(id: id)

        XCTAssertTrue(draft.rectangles.isEmpty)
        XCTAssertFalse(draft.canUndo)
    }

    func testControlPointPixelsStayOutsideRectangleInnerArea() {
        let rectangle = CGRect(x: 100, y: 80, width: 200, height: 120)
        let size = LabelingControlPointGeometry.markerSize
        let markers = Dictionary(uniqueKeysWithValues: LabelingResizeCorner.allCases.map {
            corner in
            let center = LabelingControlPointGeometry.center(
                for: corner,
                displayRect: rectangle
            )
            return (corner, CGRect(
                x: center.x - size / 2,
                y: center.y - size / 2,
                width: size,
                height: size
            ))
        })

        XCTAssertEqual(markers[.topLeft]!.maxX, rectangle.minX, accuracy: 0.0001)
        XCTAssertEqual(markers[.topLeft]!.maxY, rectangle.minY, accuracy: 0.0001)
        XCTAssertEqual(markers[.topRight]!.minX, rectangle.maxX, accuracy: 0.0001)
        XCTAssertEqual(markers[.topRight]!.maxY, rectangle.minY, accuracy: 0.0001)
        XCTAssertEqual(markers[.bottomLeft]!.maxX, rectangle.minX, accuracy: 0.0001)
        XCTAssertEqual(markers[.bottomLeft]!.minY, rectangle.maxY, accuracy: 0.0001)
        XCTAssertEqual(markers[.bottomRight]!.minX, rectangle.maxX, accuracy: 0.0001)
        XCTAssertEqual(markers[.bottomRight]!.minY, rectangle.maxY, accuracy: 0.0001)
    }
}
