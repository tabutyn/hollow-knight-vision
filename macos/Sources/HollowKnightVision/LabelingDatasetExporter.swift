import CoreGraphics
import CryptoKit
import Foundation

struct CreateMLObjectCoordinates: Codable, Equatable {
    let x: Double
    let y: Double
    let width: Double
    let height: Double
}

struct CreateMLObjectAnnotation: Codable, Equatable {
    let label: String
    let coordinates: CreateMLObjectCoordinates
    let isNegative: Bool?

    init(
        label: String,
        coordinates: CreateMLObjectCoordinates,
        isNegative: Bool? = nil
    ) {
        self.label = label
        self.coordinates = coordinates
        self.isNegative = isNegative
    }
}

struct CreateMLImageAnnotations: Codable, Equatable {
    let image: String
    let annotations: [CreateMLObjectAnnotation]
}

enum LabelingDatasetSplit: String, Codable, Equatable {
    case training
    case validation
}

struct LabelingDatasetItem: Codable, Equatable, Identifiable {
    let exampleIdentifier: UUID
    let imageIdentifier: UUID
    let captureGroupIdentifier: UUID
    let imageDigest: String
    let imageFilename: String
    let annotationCount: Int
    let knownClassIdentifiers: [String]?
    let split: LabelingDatasetSplit

    var id: UUID { exampleIdentifier }
}

struct LabelingDatasetManifest: Codable, Equatable {
    static let currentSchemaVersion = 2

    let schemaVersion: Int
    let id: UUID
    let classIdentifier: String
    let classIdentifiers: [String]?
    let createdAt: Date
    let items: [LabelingDatasetItem]
    let isPreliminary: Bool

    var trainingItems: [LabelingDatasetItem] { items.filter { $0.split == .training } }
    var validationItems: [LabelingDatasetItem] { items.filter { $0.split == .validation } }
    var trainingAnnotationCount: Int { trainingItems.reduce(0) { $0 + $1.annotationCount } }
    var validationAnnotationCount: Int { validationItems.reduce(0) { $0 + $1.annotationCount } }
}

struct LabelingDatasetSnapshot: Equatable, Identifiable {
    let directoryURL: URL
    let manifest: LabelingDatasetManifest

    var id: UUID { manifest.id }
}

enum LabelingDatasetExporterError: Error {
    case noInstances(String)
    case invalidImage(String)
}

extension LabelingDatasetExporterError: LocalizedError {
    var errorDescription: String? {
        switch self {
        case .noInstances(let classIdentifier):
            return "No labeled instances exist for \(classIdentifier)."
        case .invalidImage(let path):
            return "The saved image is empty: \(path)"
        }
    }
}

final class LabelingDatasetExporter {
    static let manifestFilename = "dataset.json"
    static let annotationsFilename = "annotations.json"

    let rootURL: URL
    private let fileManager: FileManager

    init(
        rootURL: URL = LabelingDatasetExporter.defaultRootURL(),
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL.standardizedFileURL
        self.fileManager = fileManager
    }

    static func defaultRootURL(fileManager: FileManager = .default) -> URL {
        LabelingExampleStore.defaultRootURL(fileManager: fileManager)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("training-v1", isDirectory: true)
            .appendingPathComponent("datasets", isDirectory: true)
    }

    func export(
        classIdentifier: String,
        examples: [SavedLabelingExample],
        identifier: UUID = UUID(),
        now: Date = Date()
    ) throws -> LabelingDatasetSnapshot {
        try export(
            modelIdentifier: classIdentifier,
            includedClassIdentifiers: [classIdentifier],
            examples: examples,
            identifier: identifier,
            now: now
        )
    }

    func exportSharedModel(
        modelIdentifier: String = LabelingModelIdentity.sharedObjectModel,
        classIdentifiers: Set<String>,
        examples: [SavedLabelingExample],
        identifier: UUID = UUID(),
        now: Date = Date()
    ) throws -> LabelingDatasetSnapshot {
        try export(
            modelIdentifier: modelIdentifier,
            includedClassIdentifiers: classIdentifiers,
            examples: examples,
            identifier: identifier,
            now: now
        )
    }

    private func export(
        modelIdentifier: String,
        includedClassIdentifiers: Set<String>,
        examples: [SavedLabelingExample],
        identifier: UUID,
        now: Date
    ) throws -> LabelingDatasetSnapshot {
        let canonicalIncludedClassIdentifiers = Set(includedClassIdentifiers.map(
            LabelingClassIdentity.canonicalIdentifier
        ))
        let candidates = examples.filter { example in
            if example.manifest.annotations.contains(where: {
                canonicalIncludedClassIdentifiers.contains(
                    LabelingClassIdentity.canonicalIdentifier($0.classIdentifier)
                )
            }) {
                return true
            }
            guard let context = LabelingContext(
                storageIdentifier: example.manifest.contextIdentifier
            ) else { return false }
            let contextClassIdentifiers = Set(context.labels.map {
                LabelingClassIdentity.canonicalIdentifier($0.id)
            })
            guard !contextClassIdentifiers.isDisjoint(
                with: canonicalIncludedClassIdentifiers
            ) else { return false }
            return example.manifest.effectiveKnownClassIdentifiers.contains {
                canonicalIncludedClassIdentifiers.contains(
                    LabelingClassIdentity.canonicalIdentifier($0)
                )
            }
        }
        guard !candidates.isEmpty else {
            throw LabelingDatasetExporterError.noInstances(modelIdentifier)
        }
        guard candidates.contains(where: { example in
            example.manifest.annotations.contains {
                !$0.isHardNegative && canonicalIncludedClassIdentifiers.contains(
                    LabelingClassIdentity.canonicalIdentifier($0.classIdentifier)
                )
            }
        }) else {
            throw LabelingDatasetExporterError.noInstances(modelIdentifier)
        }

        let records = try candidates.map { example -> SourceRecord in
            let sourceURL = example.directoryURL
                .appendingPathComponent(example.manifest.imageFilename)
            let data = try Data(contentsOf: sourceURL)
            guard !data.isEmpty else {
                throw LabelingDatasetExporterError.invalidImage(sourceURL.path)
            }
            let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            let annotations = example.manifest.annotations.map(\.canonicalized).filter {
                canonicalIncludedClassIdentifiers.contains($0.classIdentifier)
            }
            return SourceRecord(
                example: example,
                sourceURL: sourceURL,
                imageDigest: digest,
                annotations: annotations
            )
        }

        let validationIDs = try validationExampleIdentifiers(
            records: records,
            modelIdentifier: modelIdentifier
        )
        let items = records.map { record in
            LabelingDatasetItem(
                exampleIdentifier: record.example.id,
                imageIdentifier: record.example.manifest.imageIdentifier,
                captureGroupIdentifier: record.example.manifest.captureGroupIdentifier,
                imageDigest: record.imageDigest,
                imageFilename: "\(record.example.id.uuidString.lowercased()).png",
                annotationCount: record.annotations.count { !$0.isHardNegative },
                knownClassIdentifiers: record.example.manifest.effectiveKnownClassIdentifiers,
                split: validationIDs.contains(record.example.id) ? .validation : .training
            )
        }
        let manifest = LabelingDatasetManifest(
            schemaVersion: LabelingDatasetManifest.currentSchemaVersion,
            id: identifier,
            classIdentifier: modelIdentifier,
            classIdentifiers: canonicalIncludedClassIdentifiers.sorted(),
            createdAt: now,
            items: items,
            isPreliminary: items.count < 10 || validationIDs.isEmpty
        )

        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        let finalURL = rootURL.appendingPathComponent(
            identifier.uuidString.lowercased(),
            isDirectory: true
        )
        let stagingURL = rootURL.appendingPathComponent(
            ".staging-\(identifier.uuidString.lowercased())-\(UUID().uuidString.lowercased())",
            isDirectory: true
        )

        do {
            try fileManager.createDirectory(at: stagingURL, withIntermediateDirectories: false)
            try writeSplit(.training, records: records, items: items, rootURL: stagingURL)
            try writeSplit(.validation, records: records, items: items, rootURL: stagingURL)
            try encode(manifest).write(
                to: stagingURL.appendingPathComponent(Self.manifestFilename),
                options: .atomic
            )
            try fileManager.moveItem(at: stagingURL, to: finalURL)
        } catch {
            try? fileManager.removeItem(at: stagingURL)
            throw error
        }

        return LabelingDatasetSnapshot(directoryURL: finalURL, manifest: manifest)
    }

    private func writeSplit(
        _ split: LabelingDatasetSplit,
        records: [SourceRecord],
        items: [LabelingDatasetItem],
        rootURL: URL
    ) throws {
        let splitURL = rootURL.appendingPathComponent(split.rawValue, isDirectory: true)
        try fileManager.createDirectory(at: splitURL, withIntermediateDirectories: false)
        let splitItems = items.filter { $0.split == split }
        let recordsByID = Dictionary(uniqueKeysWithValues: records.map { ($0.example.id, $0) })
        var createMLAnnotations = [CreateMLImageAnnotations]()

        for item in splitItems {
            guard let record = recordsByID[item.exampleIdentifier] else { continue }
            try fileManager.copyItem(
                at: record.sourceURL,
                to: splitURL.appendingPathComponent(item.imageFilename)
            )
            let imageWidth = Double(record.example.manifest.imageWidth)
            let imageHeight = Double(record.example.manifest.imageHeight)
            createMLAnnotations.append(CreateMLImageAnnotations(
                image: item.imageFilename,
                annotations: record.annotations.map { annotation in
                    let rectangle = annotation.normalizedRect.standardized
                    return CreateMLObjectAnnotation(
                        label: annotation.classIdentifier,
                        coordinates: CreateMLObjectCoordinates(
                            x: rectangle.midX * imageWidth,
                            y: rectangle.midY * imageHeight,
                            width: rectangle.width * imageWidth,
                            height: rectangle.height * imageHeight
                        ),
                        isNegative: annotation.isHardNegative ? true : nil
                    )
                }
            ))
        }

        try encode(createMLAnnotations).write(
            to: splitURL.appendingPathComponent(Self.annotationsFilename),
            options: .atomic
        )
    }

    private func validationExampleIdentifiers(
        records: [SourceRecord],
        modelIdentifier: String
    ) throws -> Set<UUID> {
        guard records.count > 1 else { return [] }
        var parents = Array(records.indices)

        func root(_ index: Int, parents: inout [Int]) -> Int {
            var current = index
            while parents[current] != current {
                parents[current] = parents[parents[current]]
                current = parents[current]
            }
            return current
        }

        func join(_ first: Int, _ second: Int, parents: inout [Int]) {
            let firstRoot = root(first, parents: &parents)
            let secondRoot = root(second, parents: &parents)
            if firstRoot != secondRoot { parents[secondRoot] = firstRoot }
        }

        for first in records.indices {
            for second in records.indices where second > first {
                if records[first].example.manifest.captureGroupIdentifier
                    == records[second].example.manifest.captureGroupIdentifier
                    || records[first].imageDigest == records[second].imageDigest {
                    join(first, second, parents: &parents)
                }
            }
        }

        var groupedIndices = [Int: [Int]]()
        for index in records.indices {
            groupedIndices[root(index, parents: &parents), default: []].append(index)
        }
        guard groupedIndices.count > 1 else { return [] }

        let groups = groupedIndices.values.map { indices in
            SplitGroup(
                indices: indices,
                annotationCount: indices.reduce(0) {
                    $0 + records[$1].annotations.filter { !$0.isHardNegative }.count
                },
                hasHardNegative: indices.contains { index in
                    records[index].annotations.contains(where: \.isHardNegative)
                },
                stableKey: indices.map {
                    records[$0].example.id.uuidString.lowercased()
                }.min() ?? ""
            )
        }.sorted {
            if $0.annotationCount != $1.annotationCount {
                return $0.annotationCount < $1.annotationCount
            }
            return $0.stableKey < $1.stableKey
        }

        var ledger = try loadSplitLedger()
        let previousAssignments = ledger.assignments[modelIdentifier] ?? [:]
        var validationGroups = [SplitGroup]()
        var unassignedGroups = [SplitGroup]()
        for group in groups {
            // Explicit hard negatives are user-directed training corrections.
            // Keep their entire capture/duplicate group in training even if an
            // earlier export assigned it to validation.
            guard !group.hasHardNegative else { continue }
            let assignments = group.indices.compactMap {
                previousAssignments[records[$0].example.id.uuidString.lowercased()]
            }
            if assignments.contains(.validation) {
                validationGroups.append(group)
            } else if !assignments.contains(.training) {
                unassignedGroups.append(group)
            }
        }

        let totalAnnotations = groups.reduce(0) { $0 + $1.annotationCount }
        let target = max(1, Int((Double(totalAnnotations) * 0.2).rounded()))
        var selectedCount = validationGroups.reduce(0) { $0 + $1.annotationCount }
        let maximumValidationGroups = max(1, groups.count - 1)
        for group in unassignedGroups where selectedCount < target
            && validationGroups.count < maximumValidationGroups {
            validationGroups.append(group)
            selectedCount += group.annotationCount
        }
        if validationGroups.isEmpty, let first = groups.first(where: { !$0.hasHardNegative }) {
            validationGroups = [first]
        }

        // A persistent validation ledger prevents examples from silently moving
        // between splits as the dataset grows. It must not, however, leave a
        // newly introduced class with most (or all) of its positives in
        // validation. That makes the review report false negatives even though
        // the trainer had little or no opportunity to learn the class. Preserve
        // capture/image groups to avoid leakage, but repair the ledger toward
        // an 80/20 split for every positive class, never leaving zero training
        // positives.
        var validationGroupKeys = Set(validationGroups.map(\.stableKey))
        let positiveCountsByGroup = Dictionary(uniqueKeysWithValues: groups.map { group in
            (
                group.stableKey,
                group.indices.reduce(into: [String: Int]()) { counts, index in
                    for annotation in records[index].annotations where !annotation.isHardNegative {
                        counts[annotation.classIdentifier, default: 0] += 1
                    }
                }
            )
        })
        let positiveClasses = Set(positiveCountsByGroup.values.flatMap(\.keys))
        for classIdentifier in positiveClasses.sorted() {
            let classGroups = groups.filter {
                positiveCountsByGroup[$0.stableKey, default: [:]][classIdentifier, default: 0] > 0
            }
            let totalPositiveCount = classGroups.reduce(0) {
                $0 + positiveCountsByGroup[$1.stableKey, default: [:]][classIdentifier, default: 0]
            }
            // With one positive there is no meaningful held-out example. With
            // more, keep no more than the normal 20% target in validation.
            let targetValidationCount = min(
                max(1, Int((Double(totalPositiveCount) * 0.2).rounded())),
                max(0, totalPositiveCount - 1)
            )
            var validationPositiveCount = classGroups.reduce(0) { count, group in
                guard validationGroupKeys.contains(group.stableKey) else { return count }
                return count + positiveCountsByGroup[group.stableKey, default: [:]][
                    classIdentifier,
                    default: 0
                ]
            }

            while validationPositiveCount > targetValidationCount {
                let groupToTrain = classGroups.filter {
                    validationGroupKeys.contains($0.stableKey)
                }.min {
                    let firstCount = positiveCountsByGroup[$0.stableKey, default: [:]][
                        classIdentifier,
                        default: 0
                    ]
                    let secondCount = positiveCountsByGroup[$1.stableKey, default: [:]][
                        classIdentifier,
                        default: 0
                    ]
                    if firstCount != secondCount { return firstCount < secondCount }
                    if $0.annotationCount != $1.annotationCount {
                        return $0.annotationCount < $1.annotationCount
                    }
                    return $0.stableKey < $1.stableKey
                }
                guard let groupToTrain else { break }
                validationGroupKeys.remove(groupToTrain.stableKey)
                validationPositiveCount -= positiveCountsByGroup[
                    groupToTrain.stableKey,
                    default: [:]
                ][classIdentifier, default: 0]
            }
        }
        validationGroups = validationGroups.filter {
            validationGroupKeys.contains($0.stableKey)
        }

        let validationIDs = Set(validationGroups.flatMap(\.indices).map {
            records[$0].example.id
        })
        var updatedAssignments = previousAssignments
        for record in records {
            updatedAssignments[record.example.id.uuidString.lowercased()] = validationIDs.contains(
                record.example.id
            ) ? .validation : .training
        }
        ledger.assignments[modelIdentifier] = updatedAssignments
        try saveSplitLedger(ledger)
        return validationIDs
    }

    private struct SplitLedger: Codable {
        static let currentSchemaVersion = 1

        var schemaVersion = currentSchemaVersion
        var assignments = [String: [String: LabelingDatasetSplit]]()
    }

    private var splitLedgerURL: URL {
        rootURL.deletingLastPathComponent().appendingPathComponent("split-assignments.json")
    }

    private func loadSplitLedger() throws -> SplitLedger {
        guard fileManager.fileExists(atPath: splitLedgerURL.path) else { return SplitLedger() }
        return try JSONDecoder().decode(SplitLedger.self, from: Data(contentsOf: splitLedgerURL))
    }

    private func saveSplitLedger(_ ledger: SplitLedger) throws {
        try fileManager.createDirectory(
            at: splitLedgerURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try encode(ledger).write(to: splitLedgerURL, options: .atomic)
    }

    private func encode<T: Encodable>(_ value: T) throws -> Data {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(value)
    }

    private struct SourceRecord {
        let example: SavedLabelingExample
        let sourceURL: URL
        let imageDigest: String
        let annotations: [LabelingExampleAnnotation]
    }

    private struct SplitGroup {
        let indices: [Int]
        let annotationCount: Int
        let hasHardNegative: Bool
        let stableKey: String
    }
}
