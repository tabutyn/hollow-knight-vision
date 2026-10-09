import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingReviewAnalysisTests: XCTestCase {
    func testSummariesMatchByClassAndExposeFailureFrames() {
        let firstID = UUID()
        let secondID = UUID()
        let review = LabelingPredictionReview(
            examples: [
                example(
                    id: firstID,
                    human: [box("decoration", x: 0.1), box("decoration", x: 0.7)],
                    predictions: [box("decoration", x: 0.1, confidence: 0.9)]
                ),
                example(
                    id: secondID,
                    human: [box("title", x: 0.2)],
                    predictions: [
                        box("title", x: 0.2, confidence: 0.8),
                        box("title", x: 0.6, confidence: 0.7),
                        box("decoration", x: 0.7, confidence: 0.2),
                    ]
                ),
            ],
            metrics: LabelingReviewMetrics(
                confidenceThreshold: 0.5,
                intersectionOverUnionThreshold: 0.5,
                truePositives: 2,
                falsePositives: 1,
                falseNegatives: 1,
                precision: 2.0 / 3.0,
                recall: 2.0 / 3.0,
                meanIntersectionOverUnion: 1
            )
        )

        let summaries = LabelingPredictionReviewAnalyzer.summaries(
            review: review,
            classIdentifiers: ["known-but-absent"]
        )
        XCTAssertEqual(summaries.map(\.classIdentifier), [
            "decoration", "title", "known-but-absent",
        ])
        XCTAssertEqual(summaries[0].truePositives, 1)
        XCTAssertEqual(summaries[0].falseNegatives, 1)
        XCTAssertEqual(summaries[0].falsePositives, 0)
        XCTAssertEqual(summaries[0].failingExampleIdentifiers, [firstID])
        XCTAssertEqual(summaries[1].truePositives, 1)
        XCTAssertEqual(summaries[1].falsePositives, 1)
        XCTAssertEqual(summaries[1].failingExampleIdentifiers, [secondID])
        XCTAssertFalse(summaries[2].hasFailures)
    }

    func testPrioritySummaryRanksFewLabelsAndMostErrors() {
        let exampleID = UUID()
        let saved = SavedLabelingExample(
            directoryURL: URL(fileURLWithPath: "/tmp/priority-example"),
            manifest: LabelingExampleManifest(
                schemaVersion: LabelingExampleManifest.currentSchemaVersion,
                id: exampleID,
                imageIdentifier: UUID(),
                imageFilename: "image.png",
                imageWidth: 640,
                imageHeight: 360,
                captureGroupIdentifier: UUID(),
                contextIdentifier: LabelingContext.enemies.storageIdentifier,
                annotations: [
                    LabelingExampleAnnotation(LabelingDraftRectangle(
                        id: UUID(),
                        classID: "enemies.crawlid",
                        normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.1)
                    )),
                    LabelingExampleAnnotation(LabelingDraftRectangle(
                        id: UUID(),
                        classID: "enemies.crawlid",
                        normalizedRect: CGRect(x: 0.4, y: 0.2, width: 0.1, height: 0.1)
                    )),
                ],
                knownClassIdentifiers: ["enemies.crawlid"],
                completion: .draft,
                createdAt: Date()
            )
        )
        let review = LabelingPredictionReview(
            examples: [example(
                id: exampleID,
                human: [box("enemies.crawlid", x: 0.1)],
                predictions: [
                    box("enemies.crawlid", x: 0.6),
                    box("enemies.vengfly", x: 0.7),
                ]
            )],
            metrics: emptyMetrics
        )

        let values = LabelingObjectPriorityAnalyzer.summaries(
            examples: [saved],
            review: review,
            classIdentifiers: ["enemies.crawlid", "enemies.vengfly", "enemies.shade"],
            reviewedClassIdentifiers: ["enemies.crawlid", "enemies.vengfly"]
        )

        XCTAssertEqual(
            LabelingObjectPriorityAnalyzer.leastRegistered(values).map(\.classIdentifier),
            ["enemies.vengfly", "enemies.shade", "enemies.crawlid"]
        )
        XCTAssertEqual(
            LabelingObjectPriorityAnalyzer.mostErrors(values).map(\.classIdentifier),
            ["enemies.crawlid", "enemies.vengfly"]
        )
        XCTAssertEqual(
            values.first(where: { $0.classIdentifier == "enemies.crawlid" })?.registrationCount,
            2
        )
        XCTAssertFalse(
            values.first(where: { $0.classIdentifier == "enemies.shade" })?.hasModelReview
                ?? true
        )
    }

    func testFailureNavigatorUsesReviewOrderAndWrapsAcrossScreenshots() {
        let first = UUID(), passing = UUID(), last = UUID()
        let review = LabelingPredictionReview(
            examples: [
                example(
                    id: first,
                    human: [box("target", x: 0.1)],
                    predictions: []
                ),
                example(
                    id: passing,
                    human: [box("target", x: 0.2)],
                    predictions: [box("target", x: 0.2)]
                ),
                example(
                    id: last,
                    human: [],
                    predictions: [box("target", x: 0.7)]
                ),
            ],
            metrics: emptyMetrics
        )
        let identifiers = LabelingFailureReviewNavigator.orderedExampleIdentifiers(
            for: "target",
            review: review
        )
        XCTAssertEqual(identifiers, [first, last])
        XCTAssertEqual(
            LabelingFailureReviewNavigator.movedIdentifier(
                from: first, by: 1, in: identifiers
            ),
            last
        )
        XCTAssertEqual(
            LabelingFailureReviewNavigator.movedIdentifier(
                from: first, by: -1, in: identifiers
            ),
            last
        )
    }

    private func example(
        id: UUID,
        human: [LabelingReviewBox],
        predictions: [LabelingReviewBox]
    ) -> LabelingReviewExample {
        LabelingReviewExample(
            exampleIdentifier: id,
            imageFilename: "\(id).png",
            split: "validation",
            human: human,
            predictions: predictions
        )
    }

    private func box(
        _ label: String,
        x: Double,
        confidence: Double = 1
    ) -> LabelingReviewBox {
        LabelingReviewBox(
            label: label,
            x: x,
            y: 0.2,
            width: 0.1,
            height: 0.1,
            confidence: confidence
        )
    }

    private var emptyMetrics: LabelingReviewMetrics {
        LabelingReviewMetrics(
            confidenceThreshold: 0.5,
            intersectionOverUnionThreshold: 0.5,
            truePositives: 0,
            falsePositives: 0,
            falseNegatives: 0,
            precision: 0,
            recall: 0,
            meanIntersectionOverUnion: 0
        )
    }
}
