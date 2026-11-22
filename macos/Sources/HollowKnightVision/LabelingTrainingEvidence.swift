import Foundation

/// Catalog membership and a model output head do not establish training.
/// Older models use their original training split, never today's edited labels.
enum LabelingTrainingEvidence {
    private struct Run: Decodable {
        let datasetIdentifier: UUID
        let trainedClassIdentifiers: [String]?
    }
    private struct Image: Decodable {
        let annotations: [Annotation]
    }
    private struct Annotation: Decodable {
        let label: String
        let coordinates: CreateMLObjectCoordinates
        let isNegative: Bool?
    }

    static func trainedClasses(runIdentifier: UUID, trainingRootURL: URL) -> Set<String> {
        let summaryURL = trainingRootURL.appendingPathComponent("runs", isDirectory: true)
            .appendingPathComponent(runIdentifier.uuidString.lowercased(), isDirectory: true)
            .appendingPathComponent("training.json")
        guard let data = try? Data(contentsOf: summaryURL),
              let run = try? JSONDecoder().decode(Run.self, from: data) else { return [] }
        if let identifiers = run.trainedClassIdentifiers {
            return Set(identifiers.map(LabelingClassIdentity.canonicalIdentifier))
        }
        return positiveClasses(in: trainingRootURL.appendingPathComponent("datasets", isDirectory: true)
            .appendingPathComponent(run.datasetIdentifier.uuidString.lowercased(), isDirectory: true))
    }

    static func positiveClasses(in datasetURL: URL) -> Set<String> {
        let url = datasetURL.appendingPathComponent("training", isDirectory: true)
            .appendingPathComponent(LabelingDatasetExporter.annotationsFilename)
        guard let data = try? Data(contentsOf: url),
              let images = try? JSONDecoder().decode([Image].self, from: data) else { return [] }
        return Set(images.flatMap(\.annotations).filter {
            $0.isNegative != true && $0.coordinates.width.isFinite && $0.coordinates.height.isFinite
                && $0.coordinates.width > 0 && $0.coordinates.height > 0
        }.map { LabelingClassIdentity.canonicalIdentifier($0.label) })
    }
}
