import Foundation

struct LabelingClassReviewSummary: Equatable, Identifiable {
    let classIdentifier: String
    let truePositives: Int
    let falsePositives: Int
    let falseNegatives: Int
    let meanIntersectionOverUnion: Double
    let failingExampleIdentifiers: Set<UUID>

    var id: String { classIdentifier }
    var hasFailures: Bool { falsePositives > 0 || falseNegatives > 0 }
}

struct LabelingObjectPrioritySummary: Equatable, Identifiable {
    let classIdentifier: String
    let registrationCount: Int
    let falsePositives: Int
    let falseNegatives: Int
    let failingFrameCount: Int
    let hasModelReview: Bool

    var id: String { classIdentifier }
    var errorCount: Int { falsePositives + falseNegatives }
}

enum LabelingObjectPriorityAnalyzer {
    static func summaries(
        examples: [SavedLabelingExample],
        review: LabelingPredictionReview?,
        classIdentifiers: Set<String>,
        reviewedClassIdentifiers: Set<String>
    ) -> [LabelingObjectPrioritySummary] {
        var registrationCounts = Dictionary(uniqueKeysWithValues: classIdentifiers.map {
            (LabelingClassIdentity.canonicalIdentifier($0), 0)
        })
        for annotation in examples.flatMap(\.manifest.annotations) {
            guard !annotation.isHardNegative else { continue }
            let identifier = LabelingClassIdentity.canonicalIdentifier(
                annotation.classIdentifier
            )
            registrationCounts[identifier, default: 0] += 1
        }

        let reviewed = review.map {
            LabelingPredictionReviewAnalyzer.summaries(
                review: $0,
                classIdentifiers: Array(classIdentifiers)
            )
        } ?? []
        let reviewedByIdentifier = Dictionary(uniqueKeysWithValues: reviewed.map {
            ($0.classIdentifier, $0)
        })

        return classIdentifiers.map(LabelingClassIdentity.canonicalIdentifier)
            .uniqued()
            .map { identifier in
                let model = reviewedByIdentifier[identifier]
                return LabelingObjectPrioritySummary(
                    classIdentifier: identifier,
                    registrationCount: registrationCounts[identifier] ?? 0,
                    falsePositives: model?.falsePositives ?? 0,
                    falseNegatives: model?.falseNegatives ?? 0,
                    failingFrameCount: model?.failingExampleIdentifiers.count ?? 0,
                    hasModelReview: reviewedClassIdentifiers.contains(identifier)
                )
            }
    }

    static func leastRegistered(
        _ summaries: [LabelingObjectPrioritySummary]
    ) -> [LabelingObjectPrioritySummary] {
        summaries.sorted {
            if $0.registrationCount != $1.registrationCount {
                return $0.registrationCount < $1.registrationCount
            }
            if $0.errorCount != $1.errorCount { return $0.errorCount > $1.errorCount }
            return $0.classIdentifier < $1.classIdentifier
        }
    }

    static func mostErrors(
        _ summaries: [LabelingObjectPrioritySummary]
    ) -> [LabelingObjectPrioritySummary] {
        summaries.filter { $0.errorCount > 0 }.sorted {
            if $0.errorCount != $1.errorCount { return $0.errorCount > $1.errorCount }
            if $0.falseNegatives != $1.falseNegatives {
                return $0.falseNegatives > $1.falseNegatives
            }
            if $0.registrationCount != $1.registrationCount {
                return $0.registrationCount < $1.registrationCount
            }
            return $0.classIdentifier < $1.classIdentifier
        }
    }
}

private extension Sequence where Element: Hashable {
    func uniqued() -> [Element] {
        var seen = Set<Element>()
        return filter { seen.insert($0).inserted }
    }
}

enum LabelingPredictionReviewAnalyzer {
    static func summaries(
        review: LabelingPredictionReview,
        classIdentifiers: [String] = [],
        confidenceThreshold: Double? = nil,
        intersectionOverUnionThreshold: Double? = nil
    ) -> [LabelingClassReviewSummary] {
        let confidenceThreshold = confidenceThreshold ?? review.metrics.confidenceThreshold
        let overlapThreshold = intersectionOverUnionThreshold
            ?? review.metrics.intersectionOverUnionThreshold
        let observedIdentifiers = review.examples.flatMap { example in
            (example.human.map(\.label) + example.predictions.map(\.label)).map(
                LabelingClassIdentity.canonicalIdentifier
            )
        }
        let identifiers = Set(
            classIdentifiers.map(LabelingClassIdentity.canonicalIdentifier)
                + observedIdentifiers
        ).sorted()

        return identifiers.map { classIdentifier in
            var truePositives = 0
            var falsePositives = 0
            var falseNegatives = 0
            var overlaps = [Double]()
            var failingExampleIdentifiers = Set<UUID>()

            for example in review.examples {
                let human = example.human.filter {
                    LabelingClassIdentity.matches($0.label, classIdentifier)
                }
                let predictions = example.predictions.filter {
                    LabelingClassIdentity.matches($0.label, classIdentifier)
                        && $0.confidence >= confidenceThreshold
                }.sorted { $0.confidence > $1.confidence }
                var unmatchedHuman = Set(human.indices)
                var exampleFalsePositives = 0

                for prediction in predictions {
                    let match = unmatchedHuman.map { index in
                        (index: index, overlap: RectangleOverlap.intersectionOverUnion(
                            prediction.normalizedRect,
                            human[index].normalizedRect
                        ))
                    }.max { $0.overlap < $1.overlap }
                    if let match, match.overlap >= overlapThreshold {
                        truePositives += 1
                        overlaps.append(match.overlap)
                        unmatchedHuman.remove(match.index)
                    } else {
                        falsePositives += 1
                        exampleFalsePositives += 1
                    }
                }
                falseNegatives += unmatchedHuman.count
                if exampleFalsePositives > 0 || !unmatchedHuman.isEmpty {
                    failingExampleIdentifiers.insert(example.id)
                }
            }

            return LabelingClassReviewSummary(
                classIdentifier: classIdentifier,
                truePositives: truePositives,
                falsePositives: falsePositives,
                falseNegatives: falseNegatives,
                meanIntersectionOverUnion: overlaps.isEmpty
                    ? 0
                    : overlaps.reduce(0, +) / Double(overlaps.count),
                failingExampleIdentifiers: failingExampleIdentifiers
            )
        }.sorted {
            if $0.hasFailures != $1.hasFailures { return $0.hasFailures }
            let firstFailures = $0.falseNegatives + $0.falsePositives
            let secondFailures = $1.falseNegatives + $1.falsePositives
            if firstFailures != secondFailures { return firstFailures > secondFailures }
            return $0.classIdentifier < $1.classIdentifier
        }
    }

}

enum LabelingFailureReviewNavigator {
    static func orderedExampleIdentifiers(
        for classIdentifier: String,
        review: LabelingPredictionReview
    ) -> [UUID] {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        let failures = LabelingPredictionReviewAnalyzer.summaries(
            review: review,
            classIdentifiers: [canonical]
        ).first { $0.classIdentifier == canonical }?.failingExampleIdentifiers ?? []
        return review.examples.map(\.id).filter { failures.contains($0) }.uniqued()
    }

    static func movedIdentifier(
        from current: UUID?,
        by offset: Int,
        in identifiers: [UUID]
    ) -> UUID? {
        guard !identifiers.isEmpty else { return nil }
        let currentIndex = current.flatMap { identifiers.firstIndex(of: $0) } ?? 0
        let count = identifiers.count
        return identifiers[(currentIndex + offset % count + count) % count]
    }
}
