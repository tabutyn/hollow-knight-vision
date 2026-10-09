import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingDatasetExporterTests: XCTestCase {
    func testCatalogExportKeepsMenuClassesAndRepeatedInstances() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-shared-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let example = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.2),
            context: .mainTitle,
            rectangles: [
                rectangle(
                    classID: "main-title.select-decoration",
                    normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.1, height: 0.1)
                ),
                rectangle(
                    classID: "main-title.select-decoration",
                    normalizedRect: CGRect(x: 0.3, y: 0.1, width: 0.1, height: 0.1)
                ),
                rectangle(
                    classID: "main-title.start-game",
                    normalizedRect: CGRect(x: 0.4, y: 0.5, width: 0.2, height: 0.1)
                ),
            ]
        )

        let snapshot = try exporter.exportSharedModel(
            classIdentifiers: LabelingCatalogPolicy.classIdentifiers,
            examples: [example]
        )
        let annotations = try decodeAnnotations(
            at: snapshot.directoryURL.appendingPathComponent("training", isDirectory: true)
        )

        XCTAssertEqual(snapshot.manifest.classIdentifier, LabelingModelIdentity.sharedObjectModel)
        XCTAssertEqual(
            snapshot.manifest.classIdentifiers,
            LabelingCatalogPolicy.classIdentifiers.sorted()
        )
        XCTAssertEqual(snapshot.manifest.trainingAnnotationCount, 3)
        XCTAssertEqual(
            snapshot.manifest.trainingItems.first?.knownClassIdentifiers,
            [
                "main-title.start-game",
                "shared.select-decoration",
            ]
        )
        XCTAssertEqual(annotations.first?.annotations.map(\.label), [
            "shared.select-decoration",
            "shared.select-decoration",
            "main-title.start-game",
        ])
    }

    func testSharedExportCombinesObjectsFromDifferentContexts() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-cross-context-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let title = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.2),
            context: .mainTitle,
            rectangles: [rectangle(
                classID: "main-title.start-game",
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
            )]
        )
        let game = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.4),
            context: .game,
            rectangles: [rectangle(
                classID: "game.mana",
                normalizedRect: CGRect(x: 0.3, y: 0.3, width: 0.2, height: 0.2)
            )]
        )

        let snapshot = try exporter.exportSharedModel(
            classIdentifiers: Set(LabelingContext.allCases.flatMap(\.labels).map(\.id)),
            examples: [title, game]
        )
        let labels = try ["training", "validation"].flatMap { split in
            try decodeAnnotations(
                at: snapshot.directoryURL.appendingPathComponent(split, isDirectory: true)
            ).flatMap { $0.annotations.map(\.label) }
        }

        XCTAssertEqual(Set(labels), ["main-title.start-game", "game.mana"])
        XCTAssertEqual(snapshot.manifest.classIdentifier, LabelingModelIdentity.sharedObjectModel)
    }

    func testSharedExportIncludesKnownEmptyNegativeFrame() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-negative-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let positive = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.2),
            context: .game,
            rectangles: [rectangle(
                classID: "game.mana",
                normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.3, height: 0.4)
            )]
        )
        let negative = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.8),
            context: .game,
            rectangles: []
        )

        let snapshot = try exporter.exportSharedModel(
            classIdentifiers: ["game.mana"],
            examples: try store.loadTrainingExamples()
        )
        let items = Dictionary(uniqueKeysWithValues: snapshot.manifest.items.map {
            ($0.exampleIdentifier, $0)
        })
        let negativeItem = try XCTUnwrap(items[negative.id])
        let negativeAnnotations = try decodeAnnotations(
            at: snapshot.directoryURL.appendingPathComponent(
                negativeItem.split.rawValue,
                isDirectory: true
            )
        ).first { $0.image == negativeItem.imageFilename }

        XCTAssertEqual(Set(items.keys), [positive.id, negative.id])
        XCTAssertEqual(negativeItem.annotationCount, 0)
        XCTAssertEqual(negativeItem.knownClassIdentifiers, ["game.mana"])
        XCTAssertEqual(negativeAnnotations?.annotations, [])
    }

    func testGameplayExportExcludesMenuFrameThatOnlyKnowsGameplayCatalog() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-context-scope-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let gameplay = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.2),
            context: .game,
            rectangles: [rectangle(
                classID: "game.mana",
                normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.3, height: 0.4)
            )]
        )
        let menu = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.8),
            context: .mainTitle,
            rectangles: []
        )
        XCTAssertTrue(menu.manifest.effectiveKnownClassIdentifiers.contains("game.mana"))

        let snapshot = try exporter.exportSharedModel(
            classIdentifiers: ["game.mana"],
            examples: [gameplay, menu]
        )

        XCTAssertEqual(snapshot.manifest.items.map(\.exampleIdentifier), [gameplay.id])
    }

    func testExplicitHardNegativeExportsWithoutCountingAsRegistration() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-hard-negative-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let example = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.5),
            context: .mainTitle,
            rectangles: [
                rectangle(
                    classID: LabelingClassIdentity.selectDecoration,
                    normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
                ),
                rectangle(
                    classID: LabelingClassIdentity.selectDecoration,
                    normalizedRect: CGRect(x: 0.6, y: 0.2, width: 0.2, height: 0.3),
                    isNegative: true
                ),
            ]
        )
        let ordinary = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.7),
            context: .mainTitle,
            rectangles: [rectangle(
                classID: LabelingClassIdentity.selectDecoration,
                normalizedRect: CGRect(x: 0.2, y: 0.2, width: 0.2, height: 0.2)
            )]
        )

        let snapshot = try exporter.exportSharedModel(
            classIdentifiers: [LabelingClassIdentity.selectDecoration],
            examples: [example, ordinary]
        )
        let annotations = try decodeAnnotations(
            at: snapshot.directoryURL.appendingPathComponent("training", isDirectory: true)
        ).flatMap(\.annotations)

        XCTAssertEqual(snapshot.manifest.trainingAnnotationCount, 1)
        XCTAssertEqual(snapshot.manifest.trainingItems.map(\.exampleIdentifier), [example.id])
        XCTAssertEqual(annotations.count, 2)
        XCTAssertNil(annotations[0].isNegative)
        XCTAssertEqual(annotations[1].isNegative, true)
    }

    func testOneInstanceExportsTrainingOnlyPreliminaryCreateMLDataset() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-one-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let example = try store.saveDraft(
            image: makeImage(width: 100, height: 50, red: 0.2),
            context: .game,
            rectangles: [rectangle(
                classID: "game.mana",
                normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.3, height: 0.4)
            )]
        )
        let snapshotID = UUID()
        let snapshot = try exporter.export(
            classIdentifier: "game.mana",
            examples: [example],
            identifier: snapshotID,
            now: Date(timeIntervalSince1970: 1_700_000_000)
        )

        XCTAssertEqual(snapshot.id, snapshotID)
        XCTAssertTrue(snapshot.manifest.isPreliminary)
        XCTAssertEqual(snapshot.manifest.trainingItems.map(\.exampleIdentifier), [example.id])
        XCTAssertTrue(snapshot.manifest.validationItems.isEmpty)
        XCTAssertEqual(snapshot.manifest.trainingAnnotationCount, 1)
        XCTAssertEqual(snapshot.manifest.validationAnnotationCount, 0)

        let trainingAnnotations = try decodeAnnotations(
            at: snapshot.directoryURL.appendingPathComponent("training", isDirectory: true)
        )
        XCTAssertEqual(trainingAnnotations.count, 1)
        XCTAssertEqual(trainingAnnotations[0].annotations, [
            CreateMLObjectAnnotation(
                label: "game.mana",
                coordinates: CreateMLObjectCoordinates(
                    x: 25,
                    y: 20,
                    width: 30,
                    height: 20
                )
            ),
        ])
        XCTAssertTrue(try decodeAnnotations(
            at: snapshot.directoryURL.appendingPathComponent("validation", isDirectory: true)
        ).isEmpty)
        XCTAssertTrue(FileManager.default.fileExists(atPath: snapshot.directoryURL
            .appendingPathComponent("training")
            .appendingPathComponent(snapshot.manifest.trainingItems[0].imageFilename).path))
        XCTAssertEqual(
            try FileManager.default.contentsOfDirectory(
                at: exporter.rootURL,
                includingPropertiesForKeys: nil
            ).map(\.lastPathComponent),
            [snapshotID.uuidString.lowercased()]
        )
    }

    func testCaptureGroupsAndDuplicateImagesStayTogetherAcrossSplit() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-groups-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let sharedCaptureGroup = UUID()
        let duplicatedImage = try makeImage(width: 20, height: 10, red: 0.4)
        let first = try save(
            store: store,
            image: makeImage(width: 20, height: 10, red: 0.2),
            captureGroup: sharedCaptureGroup
        )
        let second = try save(
            store: store,
            image: duplicatedImage,
            captureGroup: sharedCaptureGroup
        )
        let duplicate = try save(
            store: store,
            image: duplicatedImage,
            captureGroup: UUID()
        )
        let independent = try save(
            store: store,
            image: makeImage(width: 20, height: 10, red: 0.8),
            captureGroup: UUID()
        )

        let snapshot = try exporter.export(
            classIdentifier: "game.mana",
            examples: [first, second, duplicate, independent]
        )
        let splits = Dictionary(uniqueKeysWithValues: snapshot.manifest.items.map {
            ($0.exampleIdentifier, $0.split)
        })

        XCTAssertEqual(splits[first.id], splits[second.id])
        XCTAssertEqual(splits[second.id], splits[duplicate.id])
        XCTAssertNotEqual(splits[first.id], splits[independent.id])
        XCTAssertEqual(snapshot.manifest.trainingAnnotationCount, 3)
        XCTAssertEqual(snapshot.manifest.validationAnnotationCount, 1)
        XCTAssertEqual(snapshot.manifest.trainingItems.count, 3)
        XCTAssertEqual(snapshot.manifest.validationItems.count, 1)
    }

    func testValidationAssignmentStaysFixedWhenNewExamplesArrive() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-stable-split-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let first = try save(
            store: store,
            image: makeImage(width: 20, height: 10, red: 0.2),
            captureGroup: UUID()
        )
        let second = try save(
            store: store,
            image: makeImage(width: 20, height: 10, red: 0.4),
            captureGroup: UUID()
        )
        let initial = try exporter.export(
            classIdentifier: "game.mana",
            examples: [first, second]
        )
        let initialSplits = Dictionary(uniqueKeysWithValues: initial.manifest.items.map {
            ($0.exampleIdentifier, $0.split)
        })

        let third = try save(
            store: store,
            image: makeImage(width: 20, height: 10, red: 0.6),
            captureGroup: UUID()
        )
        let updated = try exporter.export(
            classIdentifier: "game.mana",
            examples: [third, second, first]
        )
        let updatedSplits = Dictionary(uniqueKeysWithValues: updated.manifest.items.map {
            ($0.exampleIdentifier, $0.split)
        })

        XCTAssertEqual(updatedSplits[first.id], initialSplits[first.id])
        XCTAssertEqual(updatedSplits[second.id], initialSplits[second.id])
        XCTAssertEqual(updatedSplits[third.id], .training)
    }

    func testValidationLedgerCannotPutEveryPositiveOfNewClassInValidation() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-dataset-class-coverage-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let store = LabelingExampleStore(
            rootURL: temporaryRoot.appendingPathComponent("examples", isDirectory: true)
        )
        let exporter = LabelingDatasetExporter(
            rootURL: temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        )
        let examples = try (0..<3).map { index in
            try store.saveDraft(
                image: makeImage(width: 20, height: 10, red: Double(index + 1) / 4),
                context: .quitToMenu,
                rectangles: [
                    rectangle(
                        classID: LabelingClassIdentity.yes,
                        normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
                    ),
                    rectangle(
                        classID: LabelingClassIdentity.no,
                        normalizedRect: CGRect(x: 0.5, y: 0.1, width: 0.2, height: 0.2)
                    ),
                ],
                captureGroupIdentifier: UUID()
            )
        }
        let assignments = Dictionary(uniqueKeysWithValues: examples.map {
            ($0.id.uuidString.lowercased(), LabelingDatasetSplit.validation.rawValue)
        })
        let ledger: [String: Any] = [
            "schemaVersion": 1,
            "assignments": [LabelingModelIdentity.sharedObjectModel: assignments],
        ]
        let ledgerURL = temporaryRoot.appendingPathComponent("split-assignments.json")
        let ledgerData = try JSONSerialization.data(withJSONObject: ledger, options: [])
        try ledgerData.write(to: ledgerURL)

        let snapshot = try exporter.exportSharedModel(
            classIdentifiers: [LabelingClassIdentity.yes, LabelingClassIdentity.no],
            examples: examples
        )
        let trainingLabels = try decodeAnnotations(
            at: snapshot.directoryURL.appendingPathComponent("training", isDirectory: true)
        ).flatMap { $0.annotations.map(\.label) }

        XCTAssertTrue(trainingLabels.contains(LabelingClassIdentity.yes))
        XCTAssertTrue(trainingLabels.contains(LabelingClassIdentity.no))
        XCTAssertEqual(snapshot.manifest.trainingItems.count, 2)
        XCTAssertEqual(snapshot.manifest.validationItems.count, 1)
    }

    private func save(
        store: LabelingExampleStore,
        image: CGImage,
        captureGroup: UUID
    ) throws -> SavedLabelingExample {
        try store.saveDraft(
            image: image,
            context: .game,
            rectangles: [rectangle(
                classID: "game.mana",
                normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2)
            )],
            captureGroupIdentifier: captureGroup
        )
    }

    private func rectangle(
        classID: String,
        normalizedRect: CGRect,
        isNegative: Bool = false
    ) -> LabelingDraftRectangle {
        LabelingDraftRectangle(
            id: UUID(),
            classID: classID,
            normalizedRect: normalizedRect,
            isNegative: isNegative
        )
    }

    private func decodeAnnotations(at directoryURL: URL) throws -> [CreateMLImageAnnotations] {
        let data = try Data(contentsOf: directoryURL
            .appendingPathComponent(LabelingDatasetExporter.annotationsFilename))
        return try JSONDecoder().decode([CreateMLImageAnnotations].self, from: data)
    }

    private func makeImage(
        width: Int,
        height: Int,
        red: CGFloat
    ) throws -> CGImage {
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: red, green: 0.3, blue: 0.7, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return try XCTUnwrap(context.makeImage())
    }
}
