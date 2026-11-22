import Foundation

struct LabelingCampaignProgress: Identifiable, Equatable {
    let id: String
    let count: Int
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
