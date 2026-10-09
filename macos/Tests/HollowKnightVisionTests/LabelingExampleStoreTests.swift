import CoreGraphics
import Foundation
import ImageIO
import XCTest
@testable import HollowKnightVision

final class LabelingExampleStoreTests: XCTestCase {
    func testDeleteRemovesOnlySelectedExampleDirectory() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-delete-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let first = try store.saveDraft(
            image: makeImage(width: 4, height: 4),
            context: .game,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: "game.mana",
                normalizedRect: CGRect(x: 0, y: 0, width: 1, height: 1)
            )]
        )
        let second = try store.saveDraft(
            image: makeImage(width: 4, height: 4),
            context: .game,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: "game.health",
                normalizedRect: CGRect(x: 0, y: 0, width: 1, height: 1)
            )]
        )

        try store.delete(first)

        XCTAssertFalse(FileManager.default.fileExists(atPath: first.directoryURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.directoryURL.path))
        XCTAssertEqual(try store.loadExamples().map(\.id), [second.id])
    }

    func testLegacyFullCatalogSnapshotFallsBackToActuallyAnnotatedClasses() {
        let rectangle = LabelingDraftRectangle(
            id: UUID(),
            classID: "game.mana",
            normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.3, height: 0.4)
        )
        let manifest = LabelingExampleManifest(
            schemaVersion: 1,
            id: UUID(),
            imageIdentifier: UUID(),
            imageFilename: "image.png",
            imageWidth: 100,
            imageHeight: 50,
            captureGroupIdentifier: UUID(),
            contextIdentifier: "game",
            annotations: [LabelingExampleAnnotation(rectangle)],
            knownClassIdentifiers: LabelingSharedModelPolicy.classIdentifiers.sorted(),
            completion: .draft,
            createdAt: Date()
        )

        XCTAssertEqual(manifest.effectiveKnownClassIdentifiers, ["game.mana"])
    }

    func testSaveDraftAtomicallyStoresCleanImageAndNormalizedAnnotations() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-store-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let image = try makeImage(width: 7, height: 5)
        let rectangle = LabelingDraftRectangle(
            id: UUID(),
            classID: "game.mana",
            normalizedRect: CGRect(x: 0.125, y: 0.25, width: 0.5, height: 0.375)
        )
        let captureGroup = UUID()
        let saved = try store.saveDraft(
            image: image,
            context: .game,
            rectangles: [rectangle],
            captureGroupIdentifier: captureGroup,
            now: Date(timeIntervalSince1970: 1_700_000_000)
        )

        let manifest = try store.loadManifest(at: saved.directoryURL)
        XCTAssertEqual(manifest.schemaVersion, LabelingExampleManifest.currentSchemaVersion)
        XCTAssertEqual(manifest.imageWidth, 7)
        XCTAssertEqual(manifest.imageHeight, 5)
        XCTAssertEqual(manifest.captureGroupIdentifier, captureGroup)
        XCTAssertEqual(manifest.contextIdentifier, "game")
        XCTAssertEqual(
            manifest.knownClassIdentifiers,
            ["game.mana"]
        )
        XCTAssertEqual(manifest.completion, .draft)
        XCTAssertEqual(manifest.annotations.first?.id, rectangle.id)
        XCTAssertEqual(manifest.annotations.first?.classIdentifier, "game.mana")
        XCTAssertEqual(manifest.annotations.first?.normalizedRect, rectangle.normalizedRect)

        let imageURL = saved.directoryURL.appendingPathComponent(LabelingExampleStore.imageFilename)
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(imageURL as CFURL, nil))
        let decoded = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(decoded.width, 7)
        XCTAssertEqual(decoded.height, 5)

        var rootItems = try FileManager.default.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: nil
        )
        XCTAssertEqual(rootItems.count, 1)
        XCTAssertFalse(rootItems.contains(where: { $0.lastPathComponent.hasPrefix(".staging-") }))

        let loadedExamples = try store.loadExamples()
        XCTAssertEqual(loadedExamples.map(\.id), [saved.id])
        let reopenedImage = try store.loadImage(for: try XCTUnwrap(loadedExamples.first))
        XCTAssertEqual(reopenedImage.width, 7)
        XCTAssertEqual(reopenedImage.height, 5)

        let updatedRectangle = LabelingDraftRectangle(
            id: UUID(),
            classID: "game.health",
            normalizedRect: CGRect(x: 0.2, y: 0.3, width: 0.4, height: 0.5)
        )
        let updated = try store.updateDraft(
            saved,
            context: .game,
            rectangles: [rectangle, updatedRectangle]
        )
        XCTAssertEqual(updated.id, saved.id)
        XCTAssertEqual(
            updated.manifest.annotations.map(\.classIdentifier),
            ["game.mana", "game.health"]
        )
        XCTAssertEqual(updated.manifest.completion, .draft)
        XCTAssertEqual(
            updated.manifest.knownClassIdentifiers,
            ["game.health", "game.mana"]
        )

        rootItems = try FileManager.default.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: nil
        )
        XCTAssertEqual(rootItems.count, 1)
        XCTAssertEqual(try store.loadExamples().first?.manifest, updated.manifest)
    }

    func testHardNegativeRoundTripsWithItsTargetClass() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-negative-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let rectangle = LabelingDraftRectangle(
            id: UUID(),
            classID: LabelingClassIdentity.selectDecoration,
            normalizedRect: CGRect(x: 0.2, y: 0.3, width: 0.25, height: 0.2),
            isNegative: true
        )

        let saved = try store.saveDraft(
            image: makeImage(width: 16, height: 9),
            context: .mainTitle,
            rectangles: [rectangle]
        )
        let loaded = try store.loadManifest(at: saved.directoryURL)

        XCTAssertEqual(loaded.annotations.first?.classIdentifier, rectangle.classID)
        XCTAssertEqual(loaded.annotations.first?.normalizedRect, rectangle.normalizedRect)
        XCTAssertTrue(loaded.annotations.first?.isHardNegative == true)
    }

    func testCompletedContractRepairsPreviouslyFrozenScreenshotSet() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-context-repair-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let saved = try store.saveDraft(
            image: makeImage(width: 16, height: 9),
            context: .screenScale,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: LabelingClassIdentity.brightness,
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.1)
            )]
        )
        let completed = [
            LabelingClassIdentity.brightness,
            "brightness.pointer",
            LabelingClassIdentity.back,
            LabelingClassIdentity.selectDecoration,
            LabelingClassIdentity.selectDecoration,
        ].enumerated().map { index, identifier in
            LabelingDraftRectangle(
                id: UUID(),
                classID: identifier,
                normalizedRect: CGRect(
                    x: 0.1,
                    y: 0.1 + Double(index) * 0.15,
                    width: 0.2,
                    height: 0.1
                )
            )
        }

        let updated = try store.updateDraft(
            saved,
            context: .screenScale,
            rectangles: completed
        )

        XCTAssertEqual(
            updated.manifest.contextIdentifier,
            LabelingContext.brightness.storageIdentifier
        )
        XCTAssertEqual(
            try store.loadManifest(at: saved.directoryURL).contextIdentifier,
            LabelingContext.brightness.storageIdentifier
        )
    }

    func testLoadingMigratesLegacySelectDecorationToSharedCategory() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-shared-migration-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let saved = try store.saveDraft(
            image: makeImage(width: 16, height: 9),
            context: .mainTitle,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: LabelingClassIdentity.selectDecoration,
                normalizedRect: CGRect(x: 0.2, y: 0.3, width: 0.25, height: 0.2)
            )]
        )
        let manifestURL = saved.directoryURL.appendingPathComponent(
            LabelingExampleStore.manifestFilename
        )
        let legacyData = try Data(contentsOf: manifestURL)
        let legacyJSON = try XCTUnwrap(String(data: legacyData, encoding: .utf8))
            .replacingOccurrences(
                of: LabelingClassIdentity.selectDecoration,
                with: "main-title.select-decoration"
            )
        try Data(legacyJSON.utf8).write(to: manifestURL, options: .atomic)

        let migrated = try store.loadManifest(at: saved.directoryURL)
        let persisted = try XCTUnwrap(String(data: Data(contentsOf: manifestURL), encoding: .utf8))

        XCTAssertEqual(
            migrated.annotations.map(\.classIdentifier),
            [LabelingClassIdentity.selectDecoration]
        )
        XCTAssertEqual(
            migrated.knownClassIdentifiers,
            [LabelingClassIdentity.selectDecoration]
        )
        XCTAssertFalse(persisted.contains("main-title.select-decoration"))
        XCTAssertTrue(persisted.contains(LabelingClassIdentity.selectDecoration))
    }

    func testLoadingSplitsLegacyMergedGameLabelByOptionsContext() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-game-split-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let exampleURL = rootURL.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: exampleURL, withIntermediateDirectories: true)
        let manifest = LabelingExampleManifest(
            schemaVersion: 3,
            id: UUID(),
            imageIdentifier: UUID(),
            imageFilename: "image.png",
            imageWidth: 640,
            imageHeight: 360,
            captureGroupIdentifier: UUID(),
            contextIdentifier: LabelingContext.options.storageIdentifier,
            annotations: [LabelingExampleAnnotation(
                id: UUID(),
                classIdentifier: "game-options.game-options",
                x: 0.1,
                y: 0.2,
                width: 0.3,
                height: 0.1
            )],
            knownClassIdentifiers: ["game-options.game-options"],
            completion: .draft,
            createdAt: Date()
        )
        let manifestURL = exampleURL.appendingPathComponent(LabelingExampleStore.manifestFilename)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(manifest).write(to: manifestURL)

        let migrated = try LabelingExampleStore(rootURL: rootURL).loadManifest(at: exampleURL)

        XCTAssertEqual(migrated.schemaVersion, 4)
        XCTAssertEqual(migrated.annotations.map(\.classIdentifier), ["options.game"])
        XCTAssertEqual(
            migrated.knownClassIdentifiers,
            ["game-options.game-options", "options.game"]
        )
    }

    func testNewFrameSnapshotsOnlyIntroducedAndCurrentClasses() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-known-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let image = try makeImage(width: 7, height: 5)
        let mana = try store.saveDraft(
            image: image,
            context: .game,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: "game.mana",
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
            )]
        )
        let knight = try store.saveDraft(
            image: image,
            context: .game,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: "game.playable-knight",
                normalizedRect: CGRect(x: 0.4, y: 0.4, width: 0.2, height: 0.2)
            )]
        )

        XCTAssertEqual(mana.manifest.knownClassIdentifiers, ["game.mana"])
        XCTAssertEqual(
            knight.manifest.knownClassIdentifiers,
            ["game.mana", "game.playable-knight"]
        )
        XCTAssertFalse(knight.manifest.knownClassIdentifiers?.contains("game.health") == true)
    }

    func testTrainingExamplesRequireOnlyTheSelectedObject() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-ready-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let image = try makeImage(width: 7, height: 5)
        let visibleObjects = [
            LabelingDraftRectangle(
                id: UUID(),
                classID: "game.playable-knight",
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
            ),
        ]

        let ready = try store.saveDraft(
            image: image,
            context: .game,
            rectangles: visibleObjects,
            completion: .ready
        )
        let draft = try store.saveDraft(
            image: image,
            context: .game,
            rectangles: visibleObjects
        )

        XCTAssertEqual(
            Set(try store.loadTrainingExamples(
                containingClassIdentifier: "game.playable-knight"
            ).map(\.id)),
            [ready.id, draft.id]
        )
        let manifest = try store.loadManifest(at: ready.directoryURL)
        XCTAssertEqual(manifest.completion, .ready)
        XCTAssertEqual(manifest.annotations.map(\.classIdentifier), ["game.playable-knight"])

        let updated = try store.updateDraft(
            draft,
            context: .game,
            rectangles: visibleObjects,
            completion: .ready
        )
        XCTAssertEqual(updated.id, draft.id)
        XCTAssertEqual(try store.loadManifest(at: draft.directoryURL).completion, .ready)
        XCTAssertEqual(
            Set(try store.loadTrainingExamples(
                containingClassIdentifier: "game.playable-knight"
            ).map(\.id)),
            [ready.id, draft.id]
        )

        try store.updateDraft(updated, context: .game, rectangles: visibleObjects)
        XCTAssertEqual(try store.loadManifest(at: draft.directoryURL).completion, .draft)
        XCTAssertEqual(
            Set(try store.loadTrainingExamples(
                containingClassIdentifier: "game.playable-knight"
            ).map(\.id)),
            [ready.id, draft.id]
        )
    }

    func testFrameWithNoVisibleTargetsCanBeSavedReady() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-empty-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let ready = try store.saveDraft(
            image: makeImage(width: 7, height: 5),
            context: .game,
            rectangles: [],
            completion: .ready
        )

        let manifest = try store.loadManifest(at: ready.directoryURL)
        XCTAssertEqual(manifest.completion, .ready)
        XCTAssertTrue(manifest.annotations.isEmpty)
        XCTAssertEqual(
            try store.loadTrainingExamples(
                containingClassIdentifier: "game.playable-knight"
            ).map(\.id),
            []
        )
    }

    func testKnownEmptyFrameRemainsTrainingEvidence() throws {
        let rootURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-label-negative-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: rootURL) }
        let store = LabelingExampleStore(rootURL: rootURL)
        let image = try makeImage(width: 7, height: 5)
        _ = try store.saveDraft(
            image: image,
            context: .game,
            rectangles: [LabelingDraftRectangle(
                id: UUID(),
                classID: "game.mana",
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
            )]
        )
        let negative = try store.saveDraft(
            image: image,
            context: .game,
            rectangles: []
        )

        XCTAssertEqual(negative.manifest.knownClassIdentifiers, ["game.mana"])
        XCTAssertTrue(try store.loadTrainingExamples().contains { $0.id == negative.id })
        XCTAssertTrue(try store.loadTrainingExamples(
            containingClassIdentifier: "game.mana"
        ).contains { $0.id == negative.id })
        XCTAssertTrue(try store.loadTrainingExamples(in: .game).contains {
            $0.id == negative.id
        })
    }

    private func makeImage(width: Int, height: Int) throws -> CGImage {
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.8, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return try XCTUnwrap(context.makeImage())
    }
}
