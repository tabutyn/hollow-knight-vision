import Foundation

struct LabelingCampaignProgress: Identifiable, Equatable {
    let id: String
    let count: Int
    let completedCount: Int
    var target: Int { max(10, (completedCount / 10 + 1) * 10) }
    var remaining: Int { max(0, target - count) }
    var needsTraining: Bool { count >= target }

    func guidanceKey(labeling: Bool, inModel: Bool) -> String {
        if needsTraining { return inModel ? "campaignTrainHere" : "campaignTrain" }
        return labeling ? "campaignDraw" : "campaignCollect"
    }
}

struct LabelingCampaignPoint: Identifiable, Equatable {
    let id: UUID
    let run: Int
    let accuracy: Double
    let validationCount: Int
}

/// A capture contributes at most one positive example per class. Hard negatives,
/// repeated boxes and multiple edits of the same capture cannot inflate a batch.
enum LabelingCampaignPolicy {
    static func counts(_ examples: [SavedLabelingExample]) -> [String: Int] {
        var groups = [String: Set<UUID>]()
        for example in examples where example.manifest.contextIdentifier == "game" {
            for annotation in example.manifest.annotations where !annotation.isHardNegative {
                let identifier = LabelingClassIdentity.canonicalIdentifier(annotation.classIdentifier)
                groups[identifier, default: []].insert(example.manifest.captureGroupIdentifier)
            }
        }
        return groups.mapValues(\.count)
    }

    /// Count the immutable dataset used by a completed run, never today's labels.
    /// Both splits belong to the collection milestone; only the training split
    /// is fitted by the trainer, and only validation metrics enter the chart.
    static func completedCounts(datasetURL: URL) throws -> [String: Int] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let manifest = try decoder.decode(LabelingDatasetManifest.self,
            from: Data(contentsOf: datasetURL.appendingPathComponent(LabelingDatasetExporter.manifestFilename)))
        var groups = [String: Set<UUID>]()
        for split in [LabelingDatasetSplit.training, .validation] {
            let items = manifest.items.filter { $0.split == split }
            guard !items.isEmpty else { continue }
            let images = try decoder.decode([CreateMLImageAnnotations].self, from: Data(contentsOf:
                datasetURL.appendingPathComponent(split.rawValue).appendingPathComponent("annotations.json")))
            let byName = Dictionary(uniqueKeysWithValues: items.map { ($0.imageFilename, $0) })
            for image in images {
                guard let item = byName[image.image] else { continue }
                for annotation in image.annotations where annotation.isNegative != true {
                    let identifier = LabelingClassIdentity.canonicalIdentifier(annotation.label)
                    groups[identifier, default: []].insert(item.captureGroupIdentifier)
                }
            }
        }
        return groups.mapValues(\.count)
    }

    static func history(_ candidates: [LabelingModelCandidate]) -> [LabelingCampaignPoint] {
        candidates.filter { $0.summary.classIdentifier == LabelingSharedModelPolicy.modelIdentifier }
            .sorted { $0.summary.completedAt < $1.summary.completedAt }.enumerated().compactMap { index, candidate in
                let summary = candidate.summary
                guard summary.validationAnnotationCount > 0,
                      let metrics = summary.validationMetrics, metrics.isValid,
                      let accuracy = metrics.meanAveragePrecisionAt50PercentIOU,
                      accuracy.isFinite, (0...1).contains(accuracy) else { return nil }
                return LabelingCampaignPoint(id: candidate.id, run: index + 1, accuracy: accuracy,
                                             validationCount: summary.validationAnnotationCount)
            }
    }
}
