import CoreGraphics
import Foundation

struct GroundLabelParameters: Codable, Equatable {
    var threshold: Int
    var minimumSegment: Int
    var lineSeparation: Int
    var occlusionGap: Int
    static let currentSurfaceAlgorithm = "hysteresis"
    // A string keeps historical evaluation reports readable after retiring ablations.
    var surfaceArchitecture: String? = nil
    init(_ tuning: GroundTheoryTuning = .semanticDefault) {
        threshold = tuning.groundThreshold; minimumSegment = tuning.minimumSegmentLength
        lineSeparation = tuning.lineSeparation; occlusionGap = tuning.occlusionMergeGap
        self.surfaceArchitecture = Self.currentSurfaceAlgorithm
    }
    var tuning: GroundTheoryTuning {
        GroundTheoryTuning(groundThreshold: threshold, minimumSegmentLength: minimumSegment,
                           lineSeparation: lineSeparation, occlusionMergeGap: occlusionGap)
    }
}

struct GroundEdgeScore: Codable, Equatable {
    var truePositiveLength: Int
    var falsePositiveLength: Int?
    var falseNegativeLength: Int
    var precision: Double? {
        guard let fp = falsePositiveLength else { return nil }
        return truePositiveLength + fp == 0 ? 1 : Double(truePositiveLength) / Double(truePositiveLength + fp)
    }
    var recall: Double? {
        let truth = truePositiveLength + falseNegativeLength
        return truth == 0 ? nil : Double(truePositiveLength) / Double(truth)
    }
    var f1: Double? {
        guard let fp = falsePositiveLength, recall != nil else { return nil }
        return Double(2 * truePositiveLength) / Double(2 * truePositiveLength + fp + falseNegativeLength)
    }
    // Explicitly encode derived metrics, so exported reports need no app code.
    enum CodingKeys: String, CodingKey {
        case truePositiveLength, falsePositiveLength, falseNegativeLength, precision, recall, f1
    }
    init(truePositiveLength: Int, falsePositiveLength: Int?, falseNegativeLength: Int) {
        self.truePositiveLength = truePositiveLength; self.falsePositiveLength = falsePositiveLength
        self.falseNegativeLength = falseNegativeLength
    }
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        truePositiveLength = try c.decode(Int.self, forKey: .truePositiveLength)
        falsePositiveLength = try c.decodeIfPresent(Int.self, forKey: .falsePositiveLength)
        falseNegativeLength = try c.decode(Int.self, forKey: .falseNegativeLength)
    }
    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(truePositiveLength, forKey: .truePositiveLength)
        try c.encode(falsePositiveLength, forKey: .falsePositiveLength)
        try c.encode(falseNegativeLength, forKey: .falseNegativeLength)
        try c.encode(precision, forKey: .precision); try c.encode(recall, forKey: .recall); try c.encode(f1, forKey: .f1)
    }
    static func total(_ values: [Self]) -> Self {
        guard !values.isEmpty else {
            return Self(truePositiveLength: 0, falsePositiveLength: nil, falseNegativeLength: 0)
        }
        return Self(truePositiveLength: values.reduce(0) { $0 + $1.truePositiveLength },
             falsePositiveLength: values.allSatisfy { $0.falsePositiveLength != nil }
                ? values.reduce(0) { $0 + ($1.falsePositiveLength ?? 0) } : nil,
             falseNegativeLength: values.reduce(0) { $0 + $1.falseNegativeLength })
    }
}

enum GroundLabelScoring {
    /// One-to-one matching in each image column. Identical overlapping spans
    /// are deduplicated, but a second parallel row remains an extra detection.
    static func score(document: GroundLabelDocument, predictions: [CleanFloorLine],
                      tolerance: Int = 3, evidenceOnly: Bool = false) -> GroundEdgeScore {
        var tp = 0, fp = 0, fn = 0
        let width = document.width
        func visible(x: Int, y: Int) -> Bool {
            !document.ignoredRegions.contains { $0.rect.contains(CGPoint(x: CGFloat(x) + 0.5, y: CGFloat(y) + 0.5)) }
        }
        var truthByColumn = [[Int]](repeating: [], count: width)
        for edge in document.edges {
            let lower = max(0, edge.x0), upper = min(width - 1, edge.x1)
            guard lower <= upper else { continue }
            for x in lower...upper where visible(x: x, y: edge.y) {
                truthByColumn[x].append(edge.y)
            }
        }
        var predictionsByColumn = [[Int]](repeating: [], count: width)
        for line in predictions where line.row >= 0 && line.row < document.height {
            for range in evidenceOnly ? line.evidenceRanges : [line.xRange] {
                let lower = max(0, range.lowerBound), upper = min(width - 1, range.upperBound)
                guard lower <= upper else { continue }
                for x in lower...upper where visible(x: x, y: line.row) {
                    predictionsByColumn[x].append(line.row)
                }
            }
        }
        for x in 0..<width {
            let truth = Array(Set(truthByColumn[x])).sorted()
            let rows = Array(Set(predictionsByColumn[x])).sorted()
            var i = 0, j = 0
            while i < truth.count && j < rows.count {
                if abs(truth[i] - rows[j]) <= max(0, tolerance) { tp += 1; i += 1; j += 1 }
                else if rows[j] < truth[i] { fp += 1; j += 1 }
                else { fn += 1; i += 1 }
            }
            fp += rows.count - j; fn += truth.count - i
        }
        return GroundEdgeScore(truePositiveLength: tp,
            falsePositiveLength: document.fullyReviewed ? fp : nil, falseNegativeLength: fn)
    }
}

struct GroundLabelFrameResult: Codable {
    let id: UUID
    let source: String
    let group: String
    let split: GroundLabelSplit
    let fullyReviewed: Bool
    let runtimeMasksKnown: Bool
    let runtimeKnightKnown: Bool
    let spans: GroundEdgeScore
    let evidence: GroundEdgeScore
    let predictedSpans: [GroundLabelEdge]
    let predictedEvidence: [GroundLabelEdge]
}

struct GroundLabelCandidateResult: Codable {
    let parameters: GroundLabelParameters
    let train: GroundEdgeScore
    let check: GroundEdgeScore
    let trainEvidence: GroundEdgeScore
    let checkEvidence: GroundEdgeScore
    let frames: [GroundLabelFrameResult]
    var predictionMillisecondsP50: Double? = nil
    var predictionMillisecondsP95: Double? = nil
}

struct GroundLabelCandidateSummary: Codable {
    let parameters: GroundLabelParameters
    let train: GroundEdgeScore
    let check: GroundEdgeScore
    let trainEvidence: GroundEdgeScore
    let checkEvidence: GroundEdgeScore

    init(_ result: GroundLabelCandidateResult) {
        parameters = result.parameters
        train = result.train
        check = result.check
        trainEvidence = result.trainEvidence
        checkEvidence = result.checkEvidence
    }
}

struct GroundLabelEvaluationReport: Codable {
    let schemaVersion: Int
    let createdAt: Date
    let datasetDigest: String
    let detectorBuild: [String: String]
    let frameCount: Int
    let reviewedTrainFrames: Int
    let reviewedCheckFrames: Int
    let tolerancePixels: Int
    let candidatesTested: Int
    let candidateSummaries: [GroundLabelCandidateSummary]
    let baseline: GroundLabelCandidateResult
    let selected: GroundLabelCandidateResult
    let provisional: Bool
    let notes: [String]
}

enum GroundLabelEvaluator {
    static let matchingTolerance = 4
    struct PreparedFrame {
        let document: GroundLabelDocument
        let comparison: GroundComparisonAnalysis
    }
    static func prepare(store: GroundLabelStore) throws -> [PreparedFrame] {
        let documents = try store.documents()
        try GroundLabelStore.validateSplits(documents)
        return try documents.map { document in
            let image = try store.image(for: document)
            let masks = document.runtimeForeground.map { $0.bottomLeft(imageHeight: document.height) }
            guard let analysis = GroundLineDetector.analyze(image, excluding: masks) else {
                throw GroundLabelError.invalid("Ground analysis failed for \(document.imageFile)")
            }
            return PreparedFrame(document: document, comparison: GroundLineDetector.compare(analysis))
        }
    }
    static func predictions(frame: PreparedFrame, parameters: GroundLabelParameters) -> [CleanFloorLine] {
        let document = frame.document
        return GroundLineDetector.rejectingKnownForegroundLines(
            GroundLineDetector.semanticFloorLines(from: frame.comparison, tuning: parameters.tuning), imageHeight: document.height,
            foregroundRects: document.runtimeForeground.map { $0.bottomLeft(imageHeight: document.height) },
            knightRect: document.runtimeKnight?.bottomLeft(imageHeight: document.height))
    }
    static func evaluate(frames: [PreparedFrame], parameters: GroundLabelParameters,
                         tolerance: Int = matchingTolerance) -> GroundLabelCandidateResult {
        var parameters = parameters
        parameters.surfaceArchitecture = GroundLabelParameters.currentSurfaceAlgorithm
        var durations = [Double]()
        let results = frames.map { frame in
            let document = frame.document
            let start = ProcessInfo.processInfo.systemUptime
            let lines = predictions(frame: frame, parameters: parameters)
            durations.append((ProcessInfo.processInfo.systemUptime - start) * 1_000)
            return GroundLabelFrameResult(id: document.id, source: document.source,
                group: document.group, split: document.split, fullyReviewed: document.fullyReviewed,
                runtimeMasksKnown: document.runtimeMasksKnown, runtimeKnightKnown: document.runtimeKnightKnown,
                spans: GroundLabelScoring.score(document: document, predictions: lines, tolerance: tolerance),
                evidence: GroundLabelScoring.score(document: document, predictions: lines, tolerance: tolerance, evidenceOnly: true),
                predictedSpans: lines.map {
                    GroundLabelEdge(x0: $0.xRange.lowerBound, x1: $0.xRange.upperBound, y: $0.row)
                },
                predictedEvidence: lines.flatMap { line in
                    line.evidenceRanges.map {
                        GroundLabelEdge(x0: $0.lowerBound, x1: $0.upperBound, y: line.row)
                    }
                })
        }
        let train = results.filter { $0.fullyReviewed && $0.split == .train }
        let check = results.filter { $0.fullyReviewed && $0.split == .check }
        durations.sort()
        var result = GroundLabelCandidateResult(parameters: parameters,
            train: .total(train.map(\.spans)), check: .total(check.map(\.spans)),
            trainEvidence: .total(train.map(\.evidence)), checkEvidence: .total(check.map(\.evidence)), frames: results)
        if !durations.isEmpty {
            result.predictionMillisecondsP50 = durations[Int(Double(durations.count - 1) * 0.5)]
            result.predictionMillisecondsP95 = durations[Int(Double(durations.count - 1) * 0.95)]
        }
        return result
    }
    static func grid() -> [GroundLabelParameters] {
        var result = [GroundLabelParameters()]
        for threshold in [6, 8, 10, 12, 16, 20] {
            for minimum in [24, 32, 48, 64] {
                for separation in [12, 24, 36, 48] {
                    for gap in [0, 60, 120, 180] {
                        let parameters = GroundLabelParameters(GroundTheoryTuning(groundThreshold: threshold,
                            minimumSegmentLength: minimum, lineSeparation: separation, occlusionMergeGap: gap))
                        if parameters != result[0] { result.append(parameters) }
                    }
                }
            }
        }
        return result
    }
    static func run(store: GroundLabelStore, sweep: Bool,
                    baselineParameters: GroundLabelParameters = GroundLabelParameters(),
                    progress: @escaping (Int, Int) -> Void = { _, _ in }) throws -> GroundLabelEvaluationReport {
        let frames = try prepare(store: store)
        guard !frames.isEmpty else { throw GroundLabelError.invalid("Capture or import a frame first") }
        let baseline = evaluate(frames: frames, parameters: baselineParameters)
        let trainFrames = frames.filter { $0.document.fullyReviewed && $0.document.split == .train }
        let checkFrames = frames.filter { $0.document.fullyReviewed && $0.document.split == .check }
        if sweep && baseline.train.recall == nil {
            throw GroundLabelError.invalid("Mark true ground and fully review at least one Train frame before tuning")
        }
        let candidates = sweep ? grid() : [baselineParameters]
        var selected = baseline
        var candidateSummaries = [GroundLabelCandidateSummary]()
        for (index, parameters) in candidates.enumerated() {
            // Held-out Check labels never participate in parameter selection.
            let candidate = parameters == baseline.parameters
                ? baseline : evaluate(frames: frames, parameters: parameters)
            candidateSummaries.append(GroundLabelCandidateSummary(candidate))
            if (candidate.train.f1 ?? -1) > (selected.train.f1 ?? -1) + 0.000_000_1 {
                selected = candidate
            }
            progress(index + 1, candidates.count)
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let digest = GroundLabelStore.digest(try encoder.encode(frames.map(\.document)))
        let buildURL = Bundle.main.url(forResource: "runtime-build-info", withExtension: "json")
        let build = buildURL.flatMap { try? Data(contentsOf: $0) }.flatMap {
            try? JSONDecoder().decode([String: String].self, from: $0)
        } ?? ["buildIdentity": "unpackaged executable"]
        var notes = [
            "All coordinates are top-left source pixels; matching tolerance is 4 px vertically (half the detector's 8-row kernel).",
            "Scores measure edge-pixel length. Partial frames show recall only and do not select parameters.",
            "Spans include inferred occlusion bridges; evidence scores include only directly detected ranges.",
            "Human ignore regions affect scoring only; detector input uses captured runtime masks.",
            "Selection maximizes Train span F1. Ties keep the first candidate (defaults first). Check labels never choose parameters.",
            "One generalized parameter set is proposed. No live settings are changed. Replay and additional independent areas are still required."
        ]
        let provisional = trainFrames.isEmpty || selected.train.recall == nil
            || checkFrames.isEmpty || selected.check.recall == nil
        if provisional { notes.append("Training-only or no positive Check labels: generalization is unmeasured.") }
        if frames.contains(where: { !$0.document.runtimeMasksKnown || !$0.document.runtimeKnightKnown }) {
            notes.append("Some imported frames lack runtime masks or separate Knight metadata; their output does not reproduce the complete live masking stage.")
        }
        return GroundLabelEvaluationReport(schemaVersion: 3, createdAt: Date(), datasetDigest: digest, detectorBuild: build,
            frameCount: frames.count, reviewedTrainFrames: trainFrames.count, reviewedCheckFrames: checkFrames.count,
            tolerancePixels: matchingTolerance, candidatesTested: candidates.count,
            candidateSummaries: candidateSummaries, baseline: baseline,
            selected: selected, provisional: provisional, notes: notes)
    }
    static func save(report: GroundLabelEvaluationReport, store: GroundLabelStore) throws -> URL {
        let directory = store.root.appendingPathComponent("evaluations", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("evaluation-\(UUID().uuidString).json")
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(report).write(to: url, options: .atomic)
        return url
    }
    static func runCommand(arguments: [String]) throws -> URL {
        guard let index = arguments.firstIndex(of: "--evaluate-ground-labels"), index + 1 < arguments.count else {
            throw GroundLabelError.invalid("Usage: --evaluate-ground-labels DATASET_DIRECTORY [--sweep-ground-labels]")
        }
        let store = GroundLabelStore(root: URL(fileURLWithPath: arguments[index + 1], isDirectory: true))
        guard !arguments.contains(where: { $0.hasPrefix("--ground-surface-architecture") }) else {
            throw GroundLabelError.invalid("Surface architecture experiments are retired; evaluation uses hysteresis")
        }
        let parameters = GroundLabelParameters(GroundTheoryTuning.launchTuning(
            arguments: arguments, base: .semanticDefault
        ))
        let report = try run(store: store, sweep: arguments.contains("--sweep-ground-labels"),
                             baselineParameters: parameters) { completed, total in
            if completed == total || completed % 16 == 0 {
                fputs("Ground-label evaluation: \(completed)/\(total)\n", stderr)
            }
        }
        return try save(report: report, store: store)
    }
}
