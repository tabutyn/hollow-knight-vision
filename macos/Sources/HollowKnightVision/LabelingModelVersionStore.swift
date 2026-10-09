import Foundation

struct LabelingModelSemanticVersion: Codable, Equatable, Hashable {
    let major: Int
    let minor: Int

    var displayName: String { "v\(major).\(minor)" }
}

enum LabelingActiveModelSelection: Hashable, Identifiable {
    case defaultModel
    case promoted(Int)

    var id: String {
        switch self {
        case .defaultModel: return "default"
        case .promoted(let major): return "v\(major).0"
        }
    }

    var displayName: String {
        switch self {
        case .defaultModel: return "Default"
        case .promoted(let major): return "v\(major).0"
        }
    }
}

struct LabelingModelRestorePoint: Codable, Equatable, Identifiable {
    let classIdentifier: String
    let version: LabelingModelSemanticVersion
    let sourceRunIdentifier: UUID
    let createdAt: Date
    let modelFilename: String
    var checkpointFilename: String? = nil

    var id: String { version.displayName }
}

struct LabelingActiveModelArtifact: Equatable {
    let classIdentifier: String
    let version: LabelingModelSemanticVersion
    let modelURL: URL
}

enum LabelingDevelopmentCandidatePolicy {
    static let maximumPerClassRegression = 0.10
    static let maximumMeanRegression = 0.02

    static func preservesBase(
        candidate: LabelingModelCandidate,
        base: LabelingModelCandidate
    ) -> Bool {
        guard let candidateMetrics = candidate.summary.validationMetrics,
              let baseMetrics = base.summary.validationMetrics,
              candidateMetrics.isValid,
              baseMetrics.isValid else { return true }
        let previousClasses = base.summary.classIdentifiers
            ?? Array(baseMetrics.averagePrecisionAt50PercentIOUByClass.keys)
        let comparisons = previousClasses.compactMap { identifier -> (Double, Double)? in
            guard let previous = baseMetrics.averagePrecisionAt50PercentIOUByClass[identifier],
                  let current = candidateMetrics.averagePrecisionAt50PercentIOUByClass[identifier]
            else { return nil }
            return (previous, current)
        }
        guard !comparisons.isEmpty else { return true }
        guard comparisons.allSatisfy({ comparison in
            comparison.1 + maximumPerClassRegression >= comparison.0
        }) else { return false }
        let previousMean = comparisons.map(\.0).reduce(0, +) / Double(comparisons.count)
        let currentMean = comparisons.map(\.1).reduce(0, +) / Double(comparisons.count)
        return currentMean + maximumMeanRegression >= previousMean
    }
}

enum LabelingModelVersionStoreError: LocalizedError {
    case missingCandidateVersion(UUID)
    case missingModel(String)
    case unavailableRestorePoint(String)
    case restorePointAlreadyExists(String)

    var errorDescription: String? {
        switch self {
        case .missingCandidateVersion(let id):
            return "Candidate version is missing for run \(id.uuidString)."
        case .missingModel(let path):
            return "Candidate model is missing: \(path)"
        case .unavailableRestorePoint(let version):
            return "Model restore point \(version) is unavailable."
        case .restorePointAlreadyExists(let version):
            return "Model restore point \(version) already exists."
        }
    }
}

final class LabelingModelVersionStore {
    private struct Registry: Codable {
        static let currentSchemaVersion = 1

        var schemaVersion = currentSchemaVersion
        var objects = [String: ObjectRecord]()
    }

    private struct ObjectRecord: Codable {
        var candidateVersions = [String: LabelingModelSemanticVersion]()
        var restorePoints = [LabelingModelRestorePoint]()
        var activeMajorVersion: Int?
        var developmentRunIdentifier: UUID?
    }

    static let registryFilename = "registry.json"
    static let restoreManifestFilename = "restore-point.json"

    let rootURL: URL
    private let fileManager: FileManager

    init(
        rootURL: URL = LabelingModelVersionStore.defaultRootURL(),
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
            .appendingPathComponent("models-v1", isDirectory: true)
    }

    func synchronizeCandidates(_ candidates: [LabelingModelCandidate]) throws {
        var registry = try loadRegistry()
        var changed = false
        let grouped = Dictionary(grouping: candidates, by: { $0.summary.classIdentifier })

        for (classIdentifier, classCandidates) in grouped {
            var record = registry.objects[classIdentifier] ?? ObjectRecord()
            let currentMajor = record.restorePoints.map(\.version.major).max() ?? 0
            var nextMinor = (record.candidateVersions.values
                .filter { $0.major == currentMajor }
                .map(\.minor)
                .max() ?? 0) + 1

            for candidate in classCandidates.sorted(by: {
                $0.summary.completedAt < $1.summary.completedAt
            }) where record.candidateVersions[candidate.id.uuidString] == nil {
                record.candidateVersions[candidate.id.uuidString] = LabelingModelSemanticVersion(
                    major: currentMajor,
                    minor: nextMinor
                )
                nextMinor += 1
                changed = true
            }
            registry.objects[classIdentifier] = record
        }

        if changed { try saveRegistry(registry) }
    }

    func candidateVersion(for candidate: LabelingModelCandidate) throws
        -> LabelingModelSemanticVersion {
        let registry = try loadRegistry()
        guard let version = registry.objects[candidate.summary.classIdentifier]?
            .candidateVersions[candidate.id.uuidString] else {
            throw LabelingModelVersionStoreError.missingCandidateVersion(candidate.id)
        }
        return version
    }

    func restorePoints(for classIdentifier: String) throws -> [LabelingModelRestorePoint] {
        let registry = try loadRegistry()
        return (registry.objects[classIdentifier]?.restorePoints ?? [])
            .sorted { $0.version.major < $1.version.major }
    }

    func restorePoint(
        promotedFrom candidate: LabelingModelCandidate
    ) throws -> LabelingModelRestorePoint? {
        try restorePoints(for: candidate.summary.classIdentifier).first {
            $0.sourceRunIdentifier == candidate.id
        }
    }

    func activeSelection(for classIdentifier: String) throws -> LabelingActiveModelSelection {
        let registry = try loadRegistry()
        guard let record = registry.objects[classIdentifier],
              let activeMajor = record.activeMajorVersion,
              record.restorePoints.contains(where: { $0.version.major == activeMajor }) else {
            return .defaultModel
        }
        return .promoted(activeMajor)
    }

    func activate(
        _ selection: LabelingActiveModelSelection,
        for classIdentifier: String
    ) throws {
        var registry = try loadRegistry()
        var record = registry.objects[classIdentifier] ?? ObjectRecord()
        switch selection {
        case .defaultModel:
            record.activeMajorVersion = nil
            record.developmentRunIdentifier = nil
        case .promoted(let major):
            guard let restorePoint = record.restorePoints.first(where: {
                $0.version.major == major
            }) else {
                throw LabelingModelVersionStoreError.unavailableRestorePoint("v\(major).0")
            }
            record.activeMajorVersion = major
            record.developmentRunIdentifier = restorePoint.sourceRunIdentifier
        }
        registry.objects[classIdentifier] = record
        try saveRegistry(registry)
    }

    @discardableResult
    func promote(_ candidate: LabelingModelCandidate, now: Date = Date()) throws
        -> LabelingModelRestorePoint {
        var registry = try loadRegistry()
        var record = registry.objects[candidate.summary.classIdentifier] ?? ObjectRecord()
        guard record.candidateVersions[candidate.id.uuidString] != nil else {
            throw LabelingModelVersionStoreError.missingCandidateVersion(candidate.id)
        }
        if let existing = record.restorePoints.first(where: {
            $0.sourceRunIdentifier == candidate.id
        }) {
            record.activeMajorVersion = existing.version.major
            record.developmentRunIdentifier = candidate.id
            registry.objects[candidate.summary.classIdentifier] = record
            try saveRegistry(registry)
            return existing
        }

        let nextMajor = (record.restorePoints.map(\.version.major).max() ?? 0) + 1
        let version = LabelingModelSemanticVersion(major: nextMajor, minor: 0)
        let sourceModelURL = candidate.runURL.appendingPathComponent(candidate.summary.modelFilename)
        guard fileManager.fileExists(atPath: sourceModelURL.path) else {
            throw LabelingModelVersionStoreError.missingModel(sourceModelURL.path)
        }

        let objectURL = rootURL.appendingPathComponent(
            candidate.summary.classIdentifier,
            isDirectory: true
        )
        let destinationURL = objectURL.appendingPathComponent(version.displayName, isDirectory: true)
        guard !fileManager.fileExists(atPath: destinationURL.path) else {
            throw LabelingModelVersionStoreError.restorePointAlreadyExists(version.displayName)
        }
        let stagingURL = objectURL.appendingPathComponent(
            ".staging-\(version.displayName)-\(UUID().uuidString.lowercased())",
            isDirectory: true
        )
        let restorePoint = LabelingModelRestorePoint(
            classIdentifier: candidate.summary.classIdentifier,
            version: version,
            sourceRunIdentifier: candidate.id,
            createdAt: now,
            modelFilename: candidate.summary.modelFilename,
            checkpointFilename: candidate.summary.checkpointFilename
        )

        try fileManager.createDirectory(at: objectURL, withIntermediateDirectories: true)
        do {
            try fileManager.createDirectory(at: stagingURL, withIntermediateDirectories: false)
            try fileManager.copyItem(
                at: sourceModelURL,
                to: stagingURL.appendingPathComponent(candidate.summary.modelFilename)
            )
            if let checkpointFilename = candidate.summary.checkpointFilename {
                let checkpointURL = candidate.runURL.appendingPathComponent(checkpointFilename)
                if fileManager.fileExists(atPath: checkpointURL.path) {
                    try fileManager.copyItem(
                        at: checkpointURL,
                        to: stagingURL.appendingPathComponent(checkpointFilename)
                    )
                }
            }
            try write(restorePoint, to: stagingURL.appendingPathComponent(
                Self.restoreManifestFilename
            ))
            try fileManager.moveItem(at: stagingURL, to: destinationURL)
        } catch {
            try? fileManager.removeItem(at: stagingURL)
            throw error
        }

        record.restorePoints.append(restorePoint)
        record.activeMajorVersion = nextMajor
        record.developmentRunIdentifier = candidate.id
        registry.objects[candidate.summary.classIdentifier] = record
        do {
            try saveRegistry(registry)
        } catch {
            try? fileManager.removeItem(at: destinationURL)
            throw error
        }
        return restorePoint
    }

    func recordDevelopmentCandidate(_ candidate: LabelingModelCandidate) throws {
        guard candidate.summary.checkpointFilename != nil else { return }
        var registry = try loadRegistry()
        var record = registry.objects[candidate.summary.classIdentifier] ?? ObjectRecord()
        record.developmentRunIdentifier = candidate.id
        registry.objects[candidate.summary.classIdentifier] = record
        try saveRegistry(registry)
    }

    /// Keeps failed candidates reviewable/versioned without using them as the
    /// next warm-start. A later sibling can still pass and advance the lineage.
    func reconcileDevelopmentCandidates(_ candidates: [LabelingModelCandidate]) throws {
        var registry = try loadRegistry()
        let grouped = Dictionary(grouping: candidates, by: { $0.summary.classIdentifier })
        var changed = false
        for (classIdentifier, values) in grouped {
            var record = registry.objects[classIdentifier] ?? ObjectRecord()
            guard record.activeMajorVersion == nil else { continue }
            let checkpointCandidates = values.filter {
                $0.summary.checkpointFilename != nil
            }.sorted { $0.summary.completedAt < $1.summary.completedAt }
            guard var accepted = checkpointCandidates.first(where: {
                $0.summary.baseRunIdentifier == nil
            }) else { continue }
            for candidate in checkpointCandidates where candidate.id != accepted.id {
                guard candidate.summary.baseRunIdentifier?.lowercased()
                        == accepted.id.uuidString.lowercased(),
                      LabelingDevelopmentCandidatePolicy.preservesBase(
                        candidate: candidate,
                        base: accepted
                      ) else { continue }
                accepted = candidate
            }
            if record.developmentRunIdentifier != accepted.id {
                record.developmentRunIdentifier = accepted.id
                registry.objects[classIdentifier] = record
                changed = true
            }
        }
        if changed { try saveRegistry(registry) }
    }

    func trainingCheckpointURL(
        for classIdentifier: String,
        candidates: [LabelingModelCandidate]
    ) throws -> URL? {
        let registry = try loadRegistry()
        guard let record = registry.objects[classIdentifier],
              let runIdentifier = record.developmentRunIdentifier else { return nil }
        if let candidate = candidates.first(where: { $0.id == runIdentifier }),
           let checkpointFilename = candidate.summary.checkpointFilename {
            let url = candidate.runURL.appendingPathComponent(checkpointFilename)
            if fileManager.fileExists(atPath: url.path) { return url }
        }
        if let restorePoint = record.restorePoints.first(where: {
            $0.sourceRunIdentifier == runIdentifier
        }), let checkpointFilename = restorePoint.checkpointFilename {
            let url = rootURL
                .appendingPathComponent(classIdentifier, isDirectory: true)
                .appendingPathComponent(restorePoint.version.displayName, isDirectory: true)
                .appendingPathComponent(checkpointFilename)
            if fileManager.fileExists(atPath: url.path) { return url }
        }
        return nil
    }

    func modelURL(
        for selection: LabelingActiveModelSelection,
        classIdentifier: String
    ) throws -> URL? {
        guard case .promoted(let major) = selection else { return nil }
        let restorePoint = try restorePoints(for: classIdentifier).first {
            $0.version.major == major
        }
        guard let restorePoint else {
            throw LabelingModelVersionStoreError.unavailableRestorePoint("v\(major).0")
        }
        return rootURL
            .appendingPathComponent(classIdentifier, isDirectory: true)
            .appendingPathComponent(restorePoint.version.displayName, isDirectory: true)
            .appendingPathComponent(restorePoint.modelFilename)
    }

    func activeModelArtifacts() throws -> [LabelingActiveModelArtifact] {
        let registry = try loadRegistry()
        let artifacts: [LabelingActiveModelArtifact] = registry.objects.compactMap {
            classIdentifier, record -> LabelingActiveModelArtifact? in
            guard let activeMajor = record.activeMajorVersion,
                  let restorePoint = record.restorePoints.first(where: {
                      $0.version.major == activeMajor
                  }) else { return nil }
            return LabelingActiveModelArtifact(
                classIdentifier: classIdentifier,
                version: restorePoint.version,
                modelURL: rootURL
                    .appendingPathComponent(classIdentifier, isDirectory: true)
                    .appendingPathComponent(restorePoint.version.displayName, isDirectory: true)
                    .appendingPathComponent(restorePoint.modelFilename)
            )
        }
        .sorted { $0.classIdentifier < $1.classIdentifier }
        if let shared = artifacts.first(where: {
            $0.classIdentifier == LabelingModelIdentity.sharedObjectModel
        }) {
            return [shared]
        }
        return artifacts
    }

    private func loadRegistry() throws -> Registry {
        let url = rootURL.appendingPathComponent(Self.registryFilename)
        guard fileManager.fileExists(atPath: url.path) else { return Registry() }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(Registry.self, from: Data(contentsOf: url))
    }

    private func saveRegistry(_ registry: Registry) throws {
        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        try write(registry, to: rootURL.appendingPathComponent(Self.registryFilename))
    }

    private func write<Value: Encodable>(_ value: Value, to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(value).write(to: url, options: .atomic)
    }
}
