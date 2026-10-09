import Foundation

struct LabelingOccurrenceBounds: Codable, Equatable {
    let minimum: Int?
    let maximum: Int?
}

struct LabelingOccurrenceContract: Codable, Equatable {
    let classIdentifier: String
    let allowedCounts: [Int]?
}

struct LabelingPairContract: Codable, Equatable {
    let identifier: String
    let classIdentifiers: [String]
}

struct LabelingSetContract: Codable, Equatable {
    let contextIdentifier: String
    let requiresAllContextObjects: Bool
    let occurrenceOverrides: [String: LabelingOccurrenceBounds]?
}

struct LabelingContractCatalog: Codable, Equatable {
    let schemaVersion: Int
    let defaultMaximum: Int
    let occurrences: [LabelingOccurrenceContract]
    let pairs: [LabelingPairContract]
    let labelSets: [LabelingSetContract]

    static func load(from url: URL) throws -> Self {
        try JSONDecoder().decode(Self.self, from: Data(contentsOf: url))
    }

    static let bundled: Self = {
        let candidates = [
            Bundle.main.url(forResource: "labeling-contracts", withExtension: "json"),
            Bundle.module.url(forResource: "labeling-contracts", withExtension: "json"),
        ].compactMap { $0 }
        for url in candidates {
            if let catalog = try? load(from: url) { return catalog }
        }
        preconditionFailure("Missing labeling-contracts.json")
    }()

    func occurrence(for classIdentifier: String) -> LabelingOccurrenceContract? {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        return occurrences.first {
            LabelingClassIdentity.canonicalIdentifier($0.classIdentifier) == canonical
        }
    }

    func setContract(for contextIdentifier: String) -> LabelingSetContract? {
        labelSets.first { $0.contextIdentifier == contextIdentifier }
    }

    func sets(containing classIdentifier: String) -> [LabelingSetContract] {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        return labelSets.filter { contract in
            guard let context = LabelingContext(storageIdentifier: contract.contextIdentifier) else {
                return false
            }
            return context.labels.contains {
                LabelingClassIdentity.matches($0.id, canonical)
            }
        }
    }
}

struct LabelingContractEvaluation: Identifiable, Equatable {
    let id: String
    let title: String
    let isSatisfied: Bool
    let isExempt: Bool

    var isBreach: Bool { !isSatisfied && !isExempt }
}

enum LabelingContractEvaluator {
    static func evaluate(
        _ example: SavedLabelingExample,
        catalog: LabelingContractCatalog = .bundled,
        exemptions: Set<String> = []
    ) -> [LabelingContractEvaluation] {
        let counts = positiveCounts(example.manifest.annotations)
        var result = [LabelingContractEvaluation]()
        let context = LabelingContext(storageIdentifier: example.manifest.contextIdentifier)
        let setContract = catalog.setContract(for: example.manifest.contextIdentifier)
        let setClassIdentifiers = Set(context?.labels.map {
            LabelingClassIdentity.canonicalIdentifier($0.id)
        } ?? [])

        if example.manifest.annotations.isEmpty {
            result.append(evaluation(
                id: "screenshot.has-label",
                title: "Screenshot has at least one label",
                satisfied: false,
                exemptions: exemptions
            ))
        }

        if let setContract, setContract.requiresAllContextObjects, let context {
            for definition in context.labels {
                let classIdentifier = LabelingClassIdentity.canonicalIdentifier(definition.id)
                let override = setContract.occurrenceOverrides?[classIdentifier]
                let minimum = override?.minimum ?? 1
                let maximum = override?.maximum ?? requiredMaximum(
                    for: classIdentifier,
                    catalog: catalog
                )
                let count = counts[classIdentifier, default: 0]
                let satisfied = count >= minimum && maximum.map { count <= $0 } != false
                let range = expectedText(minimum: minimum, maximum: maximum)
                result.append(evaluation(
                    id: "set.\(setContract.contextIdentifier).\(classIdentifier)",
                    title: "\(definition.name): \(count), expected \(range)",
                    satisfied: satisfied,
                    exemptions: exemptions
                ))
            }
        }

        for (classIdentifier, count) in counts.sorted(by: { $0.key < $1.key })
        where !setClassIdentifiers.contains(classIdentifier) {
            let occurrence = catalog.occurrence(for: classIdentifier)
            let satisfied: Bool
            let expected: String
            if let allowed = occurrence?.allowedCounts {
                satisfied = allowed.contains(count)
                expected = allowed.map(String.init).joined(separator: " or ")
            } else if occurrence != nil {
                satisfied = true
                expected = "any"
            } else {
                satisfied = count <= catalog.defaultMaximum
                expected = "0…\(catalog.defaultMaximum)"
            }
            result.append(evaluation(
                id: "occurrence.\(classIdentifier)",
                title: "\(objectName(classIdentifier)): \(count), expected \(expected)",
                satisfied: satisfied,
                exemptions: exemptions
            ))
        }

        for pair in catalog.pairs {
            let pairCounts = pair.classIdentifiers.map {
                counts[LabelingClassIdentity.canonicalIdentifier($0), default: 0]
            }
            let anyPresent = pairCounts.contains(where: { $0 > 0 })
            let allPresent = pairCounts.allSatisfy { $0 > 0 }
            guard anyPresent || setClassIdentifiers.contains(where: { identifier in
                pair.classIdentifiers.contains(where: {
                    LabelingClassIdentity.matches($0, identifier)
                })
            }) else { continue }
            result.append(evaluation(
                id: "pair.\(pair.identifier)",
                title: pair.classIdentifiers.map(objectName).joined(separator: " + "),
                satisfied: !anyPresent || allPresent,
                exemptions: exemptions
            ))
        }
        return result
    }

    static func shouldAdvance(
        afterAdding classIdentifier: String,
        in context: LabelingContext,
        rectangles: [LabelingDraftRectangle],
        catalog: LabelingContractCatalog = .bundled
    ) -> Bool {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        let positiveCount = rectangles.filter {
            !$0.isNegative && LabelingClassIdentity.matches($0.classID, canonical)
        }.count
        if let override = catalog.setContract(for: context.storageIdentifier)?
            .occurrenceOverrides?[canonical] {
            guard let maximum = override.maximum else { return false }
            return positiveCount >= maximum
        }
        if let occurrence = catalog.occurrence(for: canonical) {
            guard let target = occurrence.allowedCounts?.filter({ $0 > 0 }).max() else {
                return false
            }
            return positiveCount >= target
        }
        return positiveCount >= catalog.defaultMaximum
    }

    private static func positiveCounts(
        _ annotations: [LabelingExampleAnnotation]
    ) -> [String: Int] {
        annotations.reduce(into: [:]) { counts, annotation in
            guard !annotation.isHardNegative else { return }
            counts[LabelingClassIdentity.canonicalIdentifier(annotation.classIdentifier), default: 0] += 1
        }
    }

    private static func requiredMaximum(
        for classIdentifier: String,
        catalog: LabelingContractCatalog
    ) -> Int? {
        guard let occurrence = catalog.occurrence(for: classIdentifier) else {
            return catalog.defaultMaximum
        }
        return occurrence.allowedCounts?.filter { $0 > 0 }.max()
    }

    private static func expectedText(minimum: Int, maximum: Int?) -> String {
        guard let maximum else { return "\(minimum)+" }
        return minimum == maximum ? "\(minimum)" : "\(minimum)…\(maximum)"
    }

    private static func evaluation(
        id: String,
        title: String,
        satisfied: Bool,
        exemptions: Set<String>
    ) -> LabelingContractEvaluation {
        LabelingContractEvaluation(
            id: id,
            title: title,
            isSatisfied: satisfied,
            isExempt: exemptions.contains(id)
        )
    }

    private static func objectName(_ classIdentifier: String) -> String {
        LabelingContext.allCases.lazy.flatMap(\.labels).first {
            LabelingClassIdentity.matches($0.id, classIdentifier)
        }?.name ?? classIdentifier
    }
}

final class LabelingContractExemptionStore {
    static let filename = "contract-exemptions.json"
    private static let obsoleteIdentifiers = Set(["set.game.game.geo"])

    func load(for example: SavedLabelingExample) -> Set<String> {
        let url = example.directoryURL.appendingPathComponent(Self.filename)
        guard let data = try? Data(contentsOf: url),
              let identifiers = try? JSONDecoder().decode([String].self, from: data) else {
            return []
        }
        let stored = Set(identifiers)
        let current = stored.subtracting(Self.obsoleteIdentifiers)
        if current != stored {
            try? save(current, for: example)
        }
        return current
    }

    func save(_ exemptions: Set<String>, for example: SavedLabelingExample) throws {
        let url = example.directoryURL.appendingPathComponent(Self.filename)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(exemptions.sorted()).write(to: url, options: .atomic)
    }
}

final class LabelingObjectReferenceStore {
    private let defaults: UserDefaults
    private let key = "hollowKnightVision.labeling.objectReferenceExamples.v1"

    init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    func exampleIdentifier(for classIdentifier: String) -> UUID? {
        guard let value = (defaults.dictionary(forKey: key) as? [String: String])?[
            LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        ] else { return nil }
        return UUID(uuidString: value)
    }

    func setExampleIdentifier(_ identifier: UUID, for classIdentifier: String) {
        var values = defaults.dictionary(forKey: key) as? [String: String] ?? [:]
        values[LabelingClassIdentity.canonicalIdentifier(classIdentifier)] = identifier.uuidString
        defaults.set(values, forKey: key)
    }

    func exampleIdentifiers(for classIdentifiers: [String]) -> [String: UUID] {
        let values = defaults.dictionary(forKey: key) as? [String: String] ?? [:]
        return Dictionary(uniqueKeysWithValues: classIdentifiers.compactMap { identifier in
            let canonical = LabelingClassIdentity.canonicalIdentifier(identifier)
            guard let value = values[canonical], let uuid = UUID(uuidString: value) else {
                return nil
            }
            return (canonical, uuid)
        })
    }
}
