import CoreGraphics
import Foundation
import ImageIO
import SwiftUI

struct LabelingReviewBox: Decodable, Equatable {
    let label: String
    let x: Double
    let y: Double
    let width: Double
    let height: Double
    let confidence: Double

    var normalizedRect: CGRect {
        CGRect(x: x, y: y, width: width, height: height).standardized
    }
}

struct LabelingReviewExample: Decodable, Equatable, Identifiable {
    let exampleIdentifier: UUID
    let imageFilename: String
    let split: String
    let human: [LabelingReviewBox]
    let predictions: [LabelingReviewBox]

    var id: UUID { exampleIdentifier }
}

struct LabelingPredictionReview: Decodable, Equatable {
    let examples: [LabelingReviewExample]
    let metrics: LabelingReviewMetrics
    var baselineExamples: [LabelingReviewExample]? = nil
    var baselineMetrics: LabelingReviewMetrics? = nil
}

struct LabelingModelCandidate: Identifiable, Equatable {
    let runURL: URL
    let datasetURL: URL
    let summary: LabelingTrainingRunSummary
    let predictionReview: LabelingPredictionReview?

    var id: UUID { summary.id }
}

final class LabelingModelReviewStore {
    let runsRootURL: URL
    let datasetsRootURL: URL
    private let fileManager: FileManager

    init(
        runsRootURL: URL = LabelingTrainingController.defaultRunsRootURL(),
        datasetsRootURL: URL = LabelingDatasetExporter.defaultRootURL(),
        fileManager: FileManager = .default
    ) {
        self.runsRootURL = runsRootURL
        self.datasetsRootURL = datasetsRootURL
        self.fileManager = fileManager
    }

    func loadLatestCandidate(classIdentifier: String? = nil) throws -> LabelingModelCandidate? {
        try loadCandidates(classIdentifier: classIdentifier).last
    }

    func loadCandidates(classIdentifier: String? = nil) throws -> [LabelingModelCandidate] {
        guard fileManager.fileExists(atPath: runsRootURL.path) else { return [] }
        let directories = try fileManager.contentsOfDirectory(
            at: runsRootURL,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        let candidates = try directories.compactMap(loadCandidate(at:)).filter { candidate in
            classIdentifier == nil || candidate.summary.classIdentifier == classIdentifier
        }
        return candidates.sorted { $0.summary.completedAt < $1.summary.completedAt }
    }

    func loadCandidate(at runURL: URL) throws -> LabelingModelCandidate? {
        let values = try runURL.resourceValues(forKeys: [.isDirectoryKey])
        guard values.isDirectory == true else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let summary = try decoder.decode(
            LabelingTrainingRunSummary.self,
            from: Data(contentsOf: runURL.appendingPathComponent("training.json"))
        )
        let review: LabelingPredictionReview?
        if let predictionsFilename = summary.predictionsFilename {
            review = try decoder.decode(
                LabelingPredictionReview.self,
                from: Data(contentsOf: runURL.appendingPathComponent(predictionsFilename))
            )
        } else {
            review = nil
        }
        return LabelingModelCandidate(
            runURL: runURL,
            datasetURL: datasetsRootURL.appendingPathComponent(
                summary.datasetIdentifier.uuidString.lowercased(),
                isDirectory: true
            ),
            summary: summary,
            predictionReview: review
        )
    }
}
