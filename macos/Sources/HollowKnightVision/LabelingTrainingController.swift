import Combine
import Foundation

struct LabelingTrainingMetricSummary: Decodable, Equatable {
    let isValid: Bool
    let meanAveragePrecision: Double?
    let meanAveragePrecisionAt50PercentIOU: Double?
    let averagePrecisionByClass: [String: Double]
    let averagePrecisionAt50PercentIOUByClass: [String: Double]
    let error: String?
}

struct LabelingReviewMetrics: Codable, Equatable {
    let confidenceThreshold: Double
    let intersectionOverUnionThreshold: Double
    let truePositives: Int
    let falsePositives: Int
    let falseNegatives: Int
    let precision: Double
    let recall: Double
    let meanIntersectionOverUnion: Double
}

struct LabelingTrainingRunSummary: Decodable, Equatable {
    let id: UUID
    let datasetIdentifier: UUID
    let classIdentifier: String
    let completedAt: Date
    let maximumIterations: Int
    let gridSize: Int?
    let trainingAnnotationCount: Int
    let validationAnnotationCount: Int
    let isPreliminary: Bool
    let modelFilename: String
    let predictionsFilename: String?
    let trainingMetrics: LabelingTrainingMetricSummary
    let validationMetrics: LabelingTrainingMetricSummary?
    let reviewMetrics: LabelingReviewMetrics?
    var algorithm: String? = nil
    var checkpointFilename: String? = nil
    var baseRunIdentifier: String? = nil
    var newTrainingImageCount: Int? = nil
    var replayTrainingImageCount: Int? = nil
    var fullTrainingSet: Bool? = nil
    var trainingImageCount: Int? = nil
    var changedTrainingImageCount: Int? = nil
    var retainedTrainingImageCount: Int? = nil
    var trainingEpochCount: Int? = nil
    var trainingBatchSize: Int? = nil
    var trainingBatchCountPerEpoch: Int? = nil
    var optimizerStepCount: Int? = nil
    var trainingDevice: String? = nil
    var trainingDurationSeconds: Double? = nil
    var epochLosses: [Double]? = nil
    var classIdentifiers: [String]? = nil
}

struct LabelingTrainingConfiguration: Equatable {
    let maximumIterations: Int
    let gridSize: Int

    static func forModel(identifier: String) -> LabelingTrainingConfiguration {
        if identifier == LabelingModelIdentity.sharedObjectModel {
            return LabelingTrainingConfiguration(maximumIterations: 100, gridSize: 13)
        }
        return LabelingTrainingConfiguration(maximumIterations: 10, gridSize: 13)
    }
}

enum LabelingTrainingControllerError: LocalizedError {
    case helperMissing(String)

    var errorDescription: String? {
        switch self {
        case .helperMissing(let path):
            return "Training helper is missing: \(path)"
        }
    }
}

@MainActor
final class LabelingTrainingController: ObservableObject {
    @Published private(set) var isRunning = false
    @Published private(set) var message: String?
    @Published private(set) var progressText: String?
    @Published private(set) var hasError = false
    @Published private(set) var completedRunURL: URL?

    private var process: Process?
    private var wasCancelled = false

    nonisolated static func defaultRunsRootURL(fileManager: FileManager = .default) -> URL {
        LabelingDatasetExporter.defaultRootURL(fileManager: fileManager)
            .deletingLastPathComponent()
            .appendingPathComponent("runs", isDirectory: true)
    }

    func start(
        snapshot: LabelingDatasetSnapshot,
        className: String,
        maximumIterations: Int = 10,
        gridSize: Int = 13,
        baseCheckpointURL: URL? = nil,
        bundle: Bundle = .main,
        fileManager: FileManager = .default
    ) throws {
        guard !isRunning else { return }
        let helperURL = bundle.bundleURL
            .appendingPathComponent("Contents/Helpers/HollowKnightVisionTrainer")
        guard fileManager.isExecutableFile(atPath: helperURL.path) else {
            throw LabelingTrainingControllerError.helperMissing(helperURL.path)
        }

        let runsRootURL = Self.defaultRunsRootURL(fileManager: fileManager)
        try fileManager.createDirectory(at: runsRootURL, withIntermediateDirectories: true)
        let runIdentifier = UUID()
        let outputURL = runsRootURL.appendingPathComponent(
            runIdentifier.uuidString.lowercased(),
            isDirectory: true
        )
        let stagingURL = runsRootURL.appendingPathComponent(
            ".staging-\(runIdentifier.uuidString.lowercased())",
            isDirectory: true
        )
        let output = TrainingOutputBuffer()
        let pipe = Pipe()
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else { return }
            let lines = output.append(data)
            guard let update = lines.compactMap(TrainingProgressUpdate.init).last else {
                return
            }
            DispatchQueue.main.async {
                guard self?.isRunning == true else { return }
                self?.progressText = update.displayText
            }
        }

        let process = Process()
        process.executableURL = helperURL
        var arguments = [
            "--dataset", snapshot.directoryURL.path,
            "--output", outputURL.path,
            "--iterations", String(maximumIterations),
            "--grid-size", String(gridSize),
        ]
        if let baseCheckpointURL {
            arguments += ["--base-checkpoint", baseCheckpointURL.path]
        }
        process.arguments = arguments
        process.standardOutput = pipe
        process.standardError = pipe
        process.terminationHandler = { [weak self] process in
            pipe.fileHandleForReading.readabilityHandler = nil
            let remainder = pipe.fileHandleForReading.readDataToEndOfFile()
            if !remainder.isEmpty { output.append(remainder) }
            DispatchQueue.main.async {
                self?.finish(
                    process: process,
                    output: output.text,
                    outputURL: outputURL,
                    stagingURL: stagingURL,
                    className: className
                )
            }
        }

        self.process = process
        wasCancelled = false
        completedRunURL = nil
        isRunning = true
        hasError = false
        progressText = "Preparing data…"
        message = "Training \(className)…"
        do {
            try process.run()
        } catch {
            self.process = nil
            isRunning = false
            progressText = nil
            hasError = true
            message = "Training failed: \(error.localizedDescription)"
            throw error
        }
    }

    func cancel() {
        guard let process, process.isRunning else { return }
        wasCancelled = true
        message = "Stopping training…"
        process.terminate()
    }

    private func finish(
        process: Process,
        output: String,
        outputURL: URL,
        stagingURL: URL,
        className: String
    ) {
        let fileManager = FileManager.default
        self.process = nil
        isRunning = false
        progressText = nil
        if wasCancelled {
            try? fileManager.removeItem(at: stagingURL)
            hasError = false
            message = "Training stopped"
            return
        }

        guard process.terminationStatus == EXIT_SUCCESS else {
            try? fileManager.removeItem(at: stagingURL)
            hasError = true
            let detail = output
                .split(whereSeparator: \.isNewline)
                .last
                .map(String.init) ?? "Helper exited with code \(process.terminationStatus)"
            message = detail.hasPrefix("Training failed:")
                ? detail
                : "Training failed: \(detail)"
            return
        }

        do {
            let data = try Data(contentsOf: outputURL.appendingPathComponent("training.json"))
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            let summary = try decoder.decode(LabelingTrainingRunSummary.self, from: data)
            let preliminary = summary.isPreliminary ? " · preliminary" : ""
            hasError = false
            completedRunURL = outputURL
            message = "Trained \(className) · \(summary.trainingAnnotationCount) instances\(preliminary)"
        } catch {
            hasError = true
            message = "Training output failed: \(error.localizedDescription)"
        }
    }
}

private final class TrainingOutputBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()
    private var partialLine = ""
    private let maximumBytes = 128 * 1024

    @discardableResult
    func append(_ newData: Data) -> [String] {
        lock.lock()
        defer { lock.unlock() }
        data.append(newData)
        if data.count > maximumBytes {
            data.removeFirst(data.count - maximumBytes)
        }
        partialLine += String(decoding: newData, as: UTF8.self)
        let parts = partialLine.split(
            separator: "\n",
            omittingEmptySubsequences: false
        )
        partialLine = String(parts.last ?? "")
        return parts.dropLast().map(String.init)
    }

    var text: String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: data, as: UTF8.self)
    }
}

private struct TrainingProgressUpdate {
    let displayText: String

    init?(line: String) {
        let fields = Dictionary(uniqueKeysWithValues: line.split(separator: " ").compactMap {
            field -> (String, String)? in
            let parts = field.split(separator: "=", maxSplits: 1)
            guard parts.count == 2 else { return nil }
            return (String(parts[0]), String(parts[1]))
        })
        if line.hasPrefix("HKV_PROGRESS"),
           let epochText = fields["epoch"],
           let totalText = fields["total"],
           let etaText = fields["eta_seconds"],
           let epoch = Int(epochText),
           let total = Int(totalText),
           let etaSeconds = Double(etaText) {
            if epoch == total {
                displayText = "\(epoch)/\(total) · evaluating next…"
            } else {
                let minutes = max(1, Int(ceil(etaSeconds / 60)))
                displayText = "\(epoch)/\(total) · about \(minutes) min remaining"
            }
            return
        }
        guard line.hasPrefix("HKV_STAGE"), let stage = fields["name"] else {
            return nil
        }
        switch stage {
        case "baseline": displayText = "Measuring current model…"
        case "training": displayText = "Starting training…"
        case "evaluating": displayText = "Evaluating new model…"
        case "exporting": displayText = "Exporting new model…"
        default: return nil
        }
    }
}
