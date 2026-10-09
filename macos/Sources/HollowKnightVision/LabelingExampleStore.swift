import CoreGraphics
import Foundation

enum LabelingExampleCompletion: String, Codable, Equatable {
    // Retained for schema-v1 compatibility. Training eligibility is based on annotations.
    case draft
    case ready
}

enum LabelingExampleContextInference {
    private struct SetFit: Equatable {
        let contextIdentifier: String
        let missingRequiredOccurrences: Int
        let unexpectedOccurrences: Int
        let excessOccurrences: Int
        let matchedClassCount: Int

        var isExact: Bool {
            missingRequiredOccurrences == 0
                && unexpectedOccurrences == 0
                && excessOccurrences == 0
        }

        func isBetter(than other: SetFit) -> Bool {
            let violations = missingRequiredOccurrences
                + unexpectedOccurrences + excessOccurrences
            let otherViolations = other.missingRequiredOccurrences
                + other.unexpectedOccurrences + other.excessOccurrences
            if violations != otherViolations { return violations < otherViolations }
            if unexpectedOccurrences != other.unexpectedOccurrences {
                return unexpectedOccurrences < other.unexpectedOccurrences
            }
            if missingRequiredOccurrences != other.missingRequiredOccurrences {
                return missingRequiredOccurrences < other.missingRequiredOccurrences
            }
            if excessOccurrences != other.excessOccurrences {
                return excessOccurrences < other.excessOccurrences
            }
            return matchedClassCount > other.matchedClassCount
        }

        func ties(_ other: SetFit) -> Bool {
            !isBetter(than: other) && !other.isBetter(than: self)
        }
    }

    /// Repairs screenshots whose saved set disagrees with their completed
    /// labeling contract. Shared objects such as Back and Select Decoration
    /// occur in many sets, so raw overlap is ambiguous. Compare the complete
    /// required occurrence pattern instead, and migrate only to one unique,
    /// exact, strictly better set. Incomplete work keeps its explicitly chosen
    /// set until enough labels exist to identify the screen without guessing.
    static func inferredContextIdentifier(
        current: String,
        annotations: [LabelingExampleAnnotation],
        catalog: LabelingContractCatalog = .bundled
    ) -> String {
        let counts = annotations.reduce(into: [String: Int]()) { result, annotation in
            guard !annotation.isHardNegative else { return }
            result[LabelingClassIdentity.canonicalIdentifier(
                annotation.classIdentifier
            ), default: 0] += 1
        }
        guard counts.values.reduce(0, +) >= 2 else { return current }

        let fits = catalog.labelSets.compactMap { contract -> SetFit? in
            guard contract.requiresAllContextObjects,
                  let context = LabelingContext(storageIdentifier: contract.contextIdentifier)
            else { return nil }
            let members = Set(context.labels.map {
                LabelingClassIdentity.canonicalIdentifier($0.id)
            })
            var missing = 0
            var excess = 0
            var matched = 0
            for identifier in members {
                let count = counts[identifier, default: 0]
                let override = contract.occurrenceOverrides?[identifier]
                let minimum = override?.minimum ?? 1
                let maximum: Int?
                if let explicitMaximum = override?.maximum {
                    maximum = explicitMaximum
                } else if let occurrence = catalog.occurrence(for: identifier) {
                    maximum = occurrence.allowedCounts?.filter { $0 > 0 }.max()
                } else {
                    maximum = catalog.defaultMaximum
                }
                missing += max(0, minimum - count)
                if let maximum { excess += max(0, count - maximum) }
                if count > 0 { matched += 1 }
            }
            let unexpected = counts.reduce(0) { total, pair in
                total + (members.contains(pair.key) ? 0 : pair.value)
            }
            return SetFit(
                contextIdentifier: contract.contextIdentifier,
                missingRequiredOccurrences: missing,
                unexpectedOccurrences: unexpected,
                excessOccurrences: excess,
                matchedClassCount: matched
            )
        }
        guard let best = fits.reduce(Optional<SetFit>.none, { currentBest, candidate in
            guard let currentBest else { return candidate }
            return candidate.isBetter(than: currentBest) ? candidate : currentBest
        }), best.isExact,
        fits.filter({ $0.ties(best) }).count == 1 else { return current }

        if let currentFit = fits.first(where: { $0.contextIdentifier == current }),
           !best.isBetter(than: currentFit) {
            return current
        }
        return best.contextIdentifier
    }
}

struct LabelingExampleAnnotation: Codable, Equatable, Identifiable {
    let id: UUID
    let classIdentifier: String
    let x: Double
    let y: Double
    let width: Double
    let height: Double
    let isNegative: Bool?

    init(_ rectangle: LabelingDraftRectangle) {
        let normalized = rectangle.normalizedRect.standardized
        id = rectangle.id
        classIdentifier = rectangle.classID
        x = normalized.minX
        y = normalized.minY
        width = normalized.width
        height = normalized.height
        isNegative = rectangle.isNegative ? true : nil
    }

    init(
        id: UUID,
        classIdentifier: String,
        x: Double,
        y: Double,
        width: Double,
        height: Double,
        isNegative: Bool? = nil
    ) {
        self.id = id
        self.classIdentifier = classIdentifier
        self.x = x
        self.y = y
        self.width = width
        self.height = height
        self.isNegative = isNegative
    }

    var canonicalized: LabelingExampleAnnotation {
        LabelingExampleAnnotation(
            id: id,
            classIdentifier: LabelingClassIdentity.canonicalIdentifier(classIdentifier),
            x: x,
            y: y,
            width: width,
            height: height,
            isNegative: isNegative
        )
    }

    var normalizedRect: CGRect {
        CGRect(x: x, y: y, width: width, height: height)
    }

    var isHardNegative: Bool { isNegative == true }
}

struct LabelingExampleManifest: Codable, Equatable, Identifiable {
    static let currentSchemaVersion = 4

    let schemaVersion: Int
    let id: UUID
    let imageIdentifier: UUID
    let imageFilename: String
    let imageWidth: Int
    let imageHeight: Int
    let captureGroupIdentifier: UUID
    let contextIdentifier: String
    let annotations: [LabelingExampleAnnotation]
    let knownClassIdentifiers: [String]?
    let completion: LabelingExampleCompletion
    let createdAt: Date

    var effectiveKnownClassIdentifiers: [String] {
        // Schema 1 briefly wrote the entire catalog, including objects the user
        // had never introduced. Treat those legacy frames conservatively.
        guard schemaVersion >= 2, let knownClassIdentifiers else {
            return Set(annotations.map {
                LabelingClassIdentity.canonicalIdentifier($0.classIdentifier)
            }).sorted()
        }
        return Set(knownClassIdentifiers.map(LabelingClassIdentity.canonicalIdentifier)).sorted()
    }

    var canonicalized: LabelingExampleManifest {
        let canonicalAnnotations = annotations.map { annotation in
            let canonical = annotation.canonicalized
            guard schemaVersion < 4,
                  contextIdentifier == LabelingContext.options.storageIdentifier,
                  canonical.classIdentifier == "game-options.game-options"
            else { return canonical }
            return canonical.withClassIdentifier("options.game")
        }
        var canonicalKnownClassIdentifiers = Set(
            (knownClassIdentifiers ?? []).map(LabelingClassIdentity.canonicalIdentifier)
        )
        if schemaVersion < 4,
           canonicalKnownClassIdentifiers.contains("game-options.game-options") {
            canonicalKnownClassIdentifiers.insert("options.game")
        }
        return LabelingExampleManifest(
            schemaVersion: Self.currentSchemaVersion,
            id: id,
            imageIdentifier: imageIdentifier,
            imageFilename: imageFilename,
            imageWidth: imageWidth,
            imageHeight: imageHeight,
            captureGroupIdentifier: captureGroupIdentifier,
            contextIdentifier: LabelingExampleContextInference.inferredContextIdentifier(
                current: contextIdentifier,
                annotations: canonicalAnnotations
            ),
            annotations: canonicalAnnotations,
            knownClassIdentifiers: knownClassIdentifiers == nil
                ? nil : canonicalKnownClassIdentifiers.sorted(),
            completion: completion,
            createdAt: createdAt
        )
    }
}

private extension LabelingExampleAnnotation {
    func withClassIdentifier(_ classIdentifier: String) -> Self {
        LabelingExampleAnnotation(
            id: id,
            classIdentifier: classIdentifier,
            x: x,
            y: y,
            width: width,
            height: height,
            isNegative: isNegative
        )
    }
}

struct SavedLabelingExample: Equatable, Identifiable {
    let directoryURL: URL
    let manifest: LabelingExampleManifest

    var id: UUID { manifest.id }
}

enum LabelingExampleStoreError: Error {
    case imageEncodingFailed
    case imageDecodingFailed
    case unsupportedSchema(Int)
    case unsupportedContext(String)
}

final class LabelingExampleStore {
    static let manifestFilename = "example.json"
    static let imageFilename = "image.png"

    let rootURL: URL
    private let fileManager: FileManager

    init(
        rootURL: URL = LabelingExampleStore.defaultRootURL(),
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL.standardizedFileURL
        self.fileManager = fileManager
    }

    static func defaultRootURL(fileManager: FileManager = .default) -> URL {
        let applicationSupport = fileManager.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first ?? fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support", isDirectory: true)
        return applicationSupport
            .appendingPathComponent("HollowKnightVision", isDirectory: true)
            .appendingPathComponent("labeling-v1", isDirectory: true)
            .appendingPathComponent("examples", isDirectory: true)
    }

    @discardableResult
    func saveDraft(
        image: CGImage,
        context: LabelingContext,
        rectangles: [LabelingDraftRectangle],
        completion: LabelingExampleCompletion = .draft,
        captureGroupIdentifier: UUID? = nil,
        now: Date = Date()
    ) throws -> SavedLabelingExample {
        let exampleIdentifier = UUID()
        let knownClassIdentifiers = try introducedClassIdentifiers(including: rectangles)
        let manifest = LabelingExampleManifest(
            schemaVersion: LabelingExampleManifest.currentSchemaVersion,
            id: exampleIdentifier,
            imageIdentifier: UUID(),
            imageFilename: Self.imageFilename,
            imageWidth: image.width,
            imageHeight: image.height,
            captureGroupIdentifier: captureGroupIdentifier ?? UUID(),
            contextIdentifier: context.storageIdentifier,
            annotations: rectangles.map(LabelingExampleAnnotation.init).map(\.canonicalized),
            knownClassIdentifiers: knownClassIdentifiers.sorted(),
            completion: completion,
            createdAt: now
        )

        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        let finalURL = rootURL.appendingPathComponent(
            exampleIdentifier.uuidString.lowercased(),
            isDirectory: true
        )
        let stagingURL = rootURL.appendingPathComponent(
            ".staging-\(exampleIdentifier.uuidString.lowercased())-\(UUID().uuidString.lowercased())",
            isDirectory: true
        )

        do {
            try fileManager.createDirectory(at: stagingURL, withIntermediateDirectories: false)
            try writePNG(image, to: stagingURL.appendingPathComponent(Self.imageFilename))

            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(manifest).write(
                to: stagingURL.appendingPathComponent(Self.manifestFilename),
                options: .atomic
            )

            // Directory rename is commit point: readers see complete example or nothing.
            try fileManager.moveItem(at: stagingURL, to: finalURL)
        } catch {
            try? fileManager.removeItem(at: stagingURL)
            throw error
        }

        return SavedLabelingExample(directoryURL: finalURL, manifest: manifest)
    }

    func loadManifest(at directoryURL: URL) throws -> LabelingExampleManifest {
        let manifestURL = directoryURL.appendingPathComponent(Self.manifestFilename)
        let data = try Data(contentsOf: manifestURL)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let manifest = try decoder.decode(LabelingExampleManifest.self, from: data)
        guard (1...LabelingExampleManifest.currentSchemaVersion).contains(
            manifest.schemaVersion
        ) else {
            throw LabelingExampleStoreError.unsupportedSchema(manifest.schemaVersion)
        }
        let migrated = manifest.canonicalized
        if migrated != manifest {
            try writeManifest(migrated, to: manifestURL)
        }
        return migrated
    }

    func loadExamples() throws -> [SavedLabelingExample] {
        guard fileManager.fileExists(atPath: rootURL.path) else { return [] }
        let directories = try fileManager.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        return try directories.compactMap { directoryURL in
            let values = try directoryURL.resourceValues(forKeys: [.isDirectoryKey])
            guard values.isDirectory == true else { return nil }
            return SavedLabelingExample(
                directoryURL: directoryURL,
                manifest: try loadManifest(at: directoryURL)
            )
        }
        .sorted { $0.manifest.createdAt > $1.manifest.createdAt }
    }

    func loadExamples(containingClassIdentifier classIdentifier: String) throws
        -> [SavedLabelingExample] {
        try loadExamples().filter { example in
            example.manifest.annotations.contains(where: {
                LabelingClassIdentity.matches($0.classIdentifier, classIdentifier)
            })
        }
    }

    func loadTrainingExamples(containingClassIdentifier classIdentifier: String) throws
        -> [SavedLabelingExample] {
        try loadExamples().filter { example in
            example.manifest.annotations.contains(where: {
                LabelingClassIdentity.matches($0.classIdentifier, classIdentifier)
            }) || example.manifest.effectiveKnownClassIdentifiers.contains(
                LabelingClassIdentity.canonicalIdentifier(classIdentifier)
            )
        }
    }

    func loadTrainingExamples() throws -> [SavedLabelingExample] {
        try loadExamples().filter {
            !$0.manifest.annotations.isEmpty
                || !$0.manifest.effectiveKnownClassIdentifiers.isEmpty
        }
    }

    func loadTrainingExamples(in context: LabelingContext) throws -> [SavedLabelingExample] {
        try loadExamples().filter {
            $0.manifest.contextIdentifier == context.storageIdentifier
                && (!$0.manifest.annotations.isEmpty
                    || !$0.manifest.effectiveKnownClassIdentifiers.isEmpty)
        }
    }

    func loadImage(for example: SavedLabelingExample) throws -> CGImage {
        let imageURL = example.directoryURL.appendingPathComponent(example.manifest.imageFilename)
        guard let image = ImageFileIO.load(imageURL) else {
            throw LabelingExampleStoreError.imageDecodingFailed
        }
        return image
    }

    func delete(_ example: SavedLabelingExample) throws {
        let target = example.directoryURL.standardizedFileURL
        guard target.deletingLastPathComponent() == rootURL else {
            throw CocoaError(.fileWriteInvalidFileName)
        }
        try fileManager.removeItem(at: target)
    }

    @discardableResult
    func updateDraft(
        _ example: SavedLabelingExample,
        context: LabelingContext,
        rectangles: [LabelingDraftRectangle],
        completion: LabelingExampleCompletion = .draft
    ) throws -> SavedLabelingExample {
        let previous = example.manifest
        let knownClassIdentifiers = try introducedClassIdentifiers(including: rectangles)
        let annotations = rectangles.map(LabelingExampleAnnotation.init).map(\.canonicalized)
        let storedContext = previous.annotations.isEmpty
            ? context.storageIdentifier
            : previous.contextIdentifier
        let updated = LabelingExampleManifest(
            schemaVersion: LabelingExampleManifest.currentSchemaVersion,
            id: previous.id,
            imageIdentifier: previous.imageIdentifier,
            imageFilename: previous.imageFilename,
            imageWidth: previous.imageWidth,
            imageHeight: previous.imageHeight,
            captureGroupIdentifier: previous.captureGroupIdentifier,
            contextIdentifier: LabelingExampleContextInference.inferredContextIdentifier(
                current: storedContext,
                annotations: annotations
            ),
            annotations: annotations,
            knownClassIdentifiers: knownClassIdentifiers.sorted(),
            completion: completion,
            createdAt: previous.createdAt
        )
        try writeManifest(
            updated,
            to: example.directoryURL.appendingPathComponent(Self.manifestFilename)
        )
        return SavedLabelingExample(directoryURL: example.directoryURL, manifest: updated)
    }

    private func introducedClassIdentifiers(
        including rectangles: [LabelingDraftRectangle]
    ) throws -> Set<String> {
        var identifiers = Set(rectangles.map {
            LabelingClassIdentity.canonicalIdentifier($0.classID)
        })
        for example in try loadExamples() {
            identifiers.formUnion(example.manifest.annotations.map(\.classIdentifier))
        }
        return identifiers
    }

    private func writePNG(_ image: CGImage, to url: URL) throws {
        guard ImageFileIO.writePNG(image, to: url) else {
            throw LabelingExampleStoreError.imageEncodingFailed
        }
    }

    private func writeManifest(_ manifest: LabelingExampleManifest, to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(manifest).write(to: url, options: .atomic)
    }
}
