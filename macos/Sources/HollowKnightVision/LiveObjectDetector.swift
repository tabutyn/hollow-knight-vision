import CoreGraphics
import CoreML
import Foundation
import Vision

struct LiveTensor4D {
    let values: MLMultiArray
    let shape: [Int]
    private let strides: [Int]

    init?(_ values: MLMultiArray) {
        let shape = values.shape.map(\.intValue)
        guard shape.count == 4 else { return nil }
        self.values = values
        self.shape = shape
        strides = values.strides.map(\.intValue)
    }

    func value(channel: Int, y: Int, x: Int) -> Double {
        let offset = channel * strides[1] + y * strides[2] + x * strides[3]
        switch values.dataType {
        case .float16:
            let pointer = values.dataPointer.assumingMemoryBound(to: UInt16.self)
            return Double(Float16(bitPattern: pointer[offset]))
        case .float32:
            let pointer = values.dataPointer.assumingMemoryBound(to: Float.self)
            return Double(pointer[offset])
        case .double:
            let pointer = values.dataPointer.assumingMemoryBound(to: Double.self)
            return pointer[offset]
        case .int32:
            let pointer = values.dataPointer.assumingMemoryBound(to: Int32.self)
            return Double(pointer[offset])
        default:
            return values[[0, channel, y, x].map(NSNumber.init(value:))].doubleValue
        }
    }
}

struct LiveObjectDetection: Equatable, Identifiable {
    var trackIdentifier: UInt64? = nil
    let classIdentifier: String
    let normalizedRect: CGRect
    let confidence: Double
    let sourceFrameIdentifier: UInt64
    let modelVersion: LabelingModelSemanticVersion

    var id: String {
        "\(sourceFrameIdentifier)-\(classIdentifier)-\(normalizedRect.minX)-\(normalizedRect.minY)"
    }

    func imageRect(width: Int, height: Int) -> CGRect? {
        let imageBounds = CGRect(x: 0, y: 0, width: width, height: height)
        let normalized = normalizedRect.standardized.intersection(
            CGRect(x: 0, y: 0, width: 1, height: 1)
        )
        guard !normalized.isNull, !normalized.isEmpty else { return nil }
        // Labeling rectangles use a top-left origin. Core Image drawing uses
        // a bottom-left origin, so flip Y only at this rendering boundary.
        return CGRect(
            x: normalized.minX * CGFloat(width),
            y: (1 - normalized.maxY) * CGFloat(height),
            width: normalized.width * CGFloat(width),
            height: normalized.height * CGFloat(height)
        ).intersection(imageBounds)
    }
}

struct LiveObjectDetectionBatch: Equatable {
    let detections: [LiveObjectDetection]
    let sourceFrameIdentifier: UInt64
    let sourceTimestamp: Double
    let captureGeneration: UInt64
    let inferenceDuration: TimeInterval
}

struct LiveRawObjectPrediction: Equatable {
    let classIdentifier: String?
    let normalizedVisionRect: CGRect
    let confidence: Double

    init(
        classIdentifier: String? = nil,
        normalizedVisionRect: CGRect,
        confidence: Double
    ) {
        self.classIdentifier = classIdentifier
        self.normalizedVisionRect = normalizedVisionRect
        self.confidence = confidence
    }
}

enum LiveObjectDetectionFreshness {
    static let maximumAge: TimeInterval = 0.4

    /// Contact evidence belongs to this exact image. A swept exclusion mask,
    /// smoothed display box or older inference cannot prove where its feet are.
    static func captureMatchedKnightBounds(
        from batch: LiveObjectDetectionBatch?, captureGeneration: UInt64,
        timestamp: Double, width: Int, height: Int
    ) -> CGRect? {
        guard let batch, batch.captureGeneration == captureGeneration,
              batch.sourceTimestamp == timestamp else { return nil }
        return batch.detections.filter {
            $0.sourceFrameIdentifier == batch.sourceFrameIdentifier
                && $0.confidence >= LiveObjectDetectionPostprocessor.confidenceThreshold
                && LabelingClassIdentity.matches($0.classIdentifier, "game.playable-knight")
        }.max(by: { $0.confidence < $1.confidence })?.imageRect(width: width, height: height)
    }

    static func detections(
        from batch: LiveObjectDetectionBatch?,
        captureGeneration: UInt64,
        timestamp: Double
    ) -> [LiveObjectDetection] {
        guard let batch,
              batch.captureGeneration == captureGeneration,
              timestamp >= batch.sourceTimestamp,
              timestamp - batch.sourceTimestamp <= maximumAge else { return [] }
        return batch.detections
    }
}

enum LiveObjectDetectionPostprocessor {
    static let confidenceThreshold = 0.5
    static let groundKnightConfidenceThreshold = 0.05
    static let overlapThreshold = 0.45
    static let maximumDetectionsPerClass = 12
    static let maximumDetections = 100

    static func confidenceThreshold(for classIdentifier: String?) -> Double {
        guard let classIdentifier,
              LabelingClassIdentity.matches(
                classIdentifier,
                "game.playable-knight"
              )
        else { return confidenceThreshold }
        // Weak Knight observations are useful as exclusion masks even when
        // they are not trustworthy enough to publish as normal detections.
        return groundKnightConfidenceThreshold
    }

    static func detections(
        from predictions: [LiveRawObjectPrediction],
        artifact: LabelingActiveModelArtifact,
        sourceFrameIdentifier: UInt64, minimumConfidence: Double? = nil
    ) -> [LiveObjectDetection] {
        let unit = CGRect(x: 0, y: 0, width: 1, height: 1)
        let candidates = predictions.compactMap { prediction -> LiveObjectDetection? in
            let classIdentifier = LabelingClassIdentity.canonicalIdentifier(
                prediction.classIdentifier ?? artifact.classIdentifier
            )
            guard artifact.trainedClassIdentifiers.contains(classIdentifier),
                  prediction.confidence.isFinite,
                  prediction.confidence >= (minimumConfidence ?? confidenceThreshold(
                    for: classIdentifier
                  )) else { return nil }
            let visionRect = prediction.normalizedVisionRect.standardized.intersection(unit)
            let area = visionRect.width * visionRect.height
            guard !visionRect.isNull, area >= 0.00025, area <= 0.5 else { return nil }
            let topLeftRect = CGRect(
                x: visionRect.minX,
                y: 1 - visionRect.maxY,
                width: visionRect.width,
                height: visionRect.height
            )
            return LiveObjectDetection(
                classIdentifier: classIdentifier,
                normalizedRect: topLeftRect,
                confidence: prediction.confidence,
                sourceFrameIdentifier: sourceFrameIdentifier,
                modelVersion: artifact.version
            )
        }
        .sorted { $0.confidence > $1.confidence }

        var selected = [LiveObjectDetection]()
        for candidate in candidates {
            guard selected.lazy.filter({
                $0.classIdentifier == candidate.classIdentifier
            }).count < maximumDetectionsPerClass else { continue }
            guard selected.allSatisfy({ existing in
                existing.classIdentifier != candidate.classIdentifier
                    || RectangleOverlap.intersectionOverUnion(
                        candidate.normalizedRect,
                        existing.normalizedRect
                    ) < overlapThreshold
            }) else { continue }
            selected.append(candidate)
            if selected.count == maximumDetections { break }
        }
        return selected
    }

}

final class LiveObjectDetector {
    private struct RawTensorModel {
        let classNames: [String]
        let trainedClassIdentifiers: Set<String>
        let scoresName: String
        let boxesName: String
    }

    private struct LoadedModel {
        let artifact: LabelingActiveModelArtifact
        let model: VNCoreMLModel
        let rawTensorModel: RawTensorModel?
        let confirmation: LiveCrawlidConfirmation?
        let reference: VNCoreMLModel?
        let knightRecipe: LiveKnightConfirmation?
        let knightReference: VNCoreMLModel?
        let deposit: (recipe: LiveActorHeatmap, model: VNCoreMLModel)?
    }

    private(set) var artifacts = [LabelingActiveModelArtifact]()
    private var models = [LoadedModel]()
    var usesCrawlidLocalizer: Bool {
        models.contains { $0.confirmation?.localizer != nil && $0.reference != nil }
    }
    var usesKnightLocalizer: Bool { models.contains { $0.knightReference != nil } }
    var usesDepositLocalizer: Bool { models.contains { $0.deposit != nil } }

    private func compiledModel(_ source: URL, in directory: URL?) throws -> URL {
        if source.pathExtension == "mlmodelc" { return source }
        let temporary = try MLModel.compileModel(at: source)
        guard let directory else { return temporary }
        // A Gym episode owns the compiled bundles and removes them at shutdown.
        defer { try? FileManager.default.removeItem(at: temporary) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent(temporary.lastPathComponent)
        try FileManager.default.moveItem(at: temporary, to: destination)
        return destination
    }

    private func loadHeatmap(_ recipe: LiveActorHeatmap, beside receipt: URL, identifier: String,
                             configuration: MLModelConfiguration, compiledDirectory: URL?) -> VNCoreMLModel? {
        guard let source = recipe.verifiedModel(beside: receipt),
              let compiled = try? compiledModel(source, in: compiledDirectory),
              let model = try? MLModel(contentsOf: compiled, configuration: configuration),
              model.modelDescription.outputDescriptionsByName["heatmap"] != nil,
              let metadata = model.modelDescription.metadata[.creatorDefinedKey] as? [String: String],
              metadata["hkv.classIdentifier"] == identifier else { return nil }
        return try? VNCoreMLModel(for: model)
    }

    func load(_ artifacts: [LabelingActiveModelArtifact], compiledDirectory: URL? = nil,
              confirmationURL: URL? = nil, knightURL: URL? = nil, depositURL: URL? = nil) throws {
        var loaded = [LoadedModel]()
        for artifact in artifacts {
            guard !artifact.trainedClassIdentifiers.isEmpty else { continue }
            let compiledURL = try compiledModel(artifact.modelURL, in: compiledDirectory)
            let configuration = MLModelConfiguration()
            configuration.computeUnits = .all
            let coreModel = try MLModel(contentsOf: compiledURL, configuration: configuration)
            let creatorDefined = coreModel.modelDescription.metadata[
                .creatorDefinedKey
            ] as? [String: String]
            let rawTensorModel: RawTensorModel?
            if let encodedClassNames = creatorDefined?["hkv.classNames"],
               let data = encodedClassNames.data(using: .utf8),
               let classNames = try? JSONDecoder().decode([String].self, from: data),
               !classNames.isEmpty {
                rawTensorModel = RawTensorModel(
                    classNames: classNames,
                    trainedClassIdentifiers: artifact.trainedClassIdentifiers,
                    scoresName: "scores",
                    boxesName: "boxes"
                )
            } else {
                rawTensorModel = nil
            }
            let confirmation = rawTensorModel.flatMap { _ in
                LiveCrawlidConfirmation.load(for: artifact, override: confirmationURL)
            }
            var reference: VNCoreMLModel?
            if let localizer = confirmation?.localizer,
               let receipt = LiveCrawlidConfirmation.receiptURL(for: artifact, override: confirmationURL) {
                reference = loadHeatmap(localizer, beside: receipt, identifier: LiveCrawlidConfirmation.identifier,
                                        configuration: configuration, compiledDirectory: compiledDirectory)
            } else if confirmation != nil {
                let cpu = MLModelConfiguration()
                cpu.computeUnits = .cpuOnly
                reference = try? VNCoreMLModel(for: MLModel(contentsOf: compiledURL, configuration: cpu))
            }
            let knightReceipt = LiveActorHeatmap.receipt(for: artifact, identifier: LiveKnightConfirmation.identifier,
                                                         filename: "knight", override: knightURL)
            let knight = knightReceipt.flatMap { LiveKnightConfirmation.load(from: $0, run: artifact.sourceRunIdentifier) }
            var knightReference: VNCoreMLModel?
            if let knight, let knightReceipt {
                knightReference = loadHeatmap(knight.localizer, beside: knightReceipt, identifier: LiveKnightConfirmation.identifier,
                                              configuration: configuration, compiledDirectory: compiledDirectory)
            }
            var deposit: (recipe: LiveActorHeatmap, model: VNCoreMLModel)?
            if let receipt = LiveActorHeatmap.receipt(for: artifact, identifier: LiveGeoDepositConfirmation.identifier,
                                                      filename: "geo-deposit", override: depositURL),
               let recipe = LiveGeoDepositConfirmation.load(from: receipt, run: artifact.sourceRunIdentifier)?.localizer,
               let detector = loadHeatmap(recipe, beside: receipt, identifier: LiveGeoDepositConfirmation.identifier,
                                          configuration: configuration, compiledDirectory: compiledDirectory) {
                deposit = (recipe, detector)
            }
            loaded.append(LoadedModel(
                artifact: artifact,
                model: try VNCoreMLModel(for: coreModel),
                rawTensorModel: rawTensorModel, confirmation: confirmation, reference: reference,
                knightRecipe: knight, knightReference: knightReference, deposit: deposit
            ))
        }
        self.artifacts = loaded.map(\.artifact)
        models = loaded
    }

    func detect(in image: CGImage, sourceFrameIdentifier: UInt64) throws
        -> [LiveObjectDetection] {
        var detections = [LiveObjectDetection]()
        for loaded in models {
            let request = VNCoreMLRequest(model: loaded.model)
            request.imageCropAndScaleOption = .scaleFill
            try VNImageRequestHandler(cgImage: image, orientation: .up).perform([request])
            var predictions = (request.results ?? []).compactMap {
                observation -> LiveRawObjectPrediction? in
                guard let recognized = observation as? VNRecognizedObjectObservation,
                      let label = recognized.labels.first else { return nil }
                return LiveRawObjectPrediction(
                    classIdentifier: label.identifier,
                    normalizedVisionRect: recognized.boundingBox,
                    confidence: Double(label.confidence)
                )
            }
            if predictions.isEmpty, let rawTensorModel = loaded.rawTensorModel {
                predictions = rawPredictions(
                    from: request.results ?? [],
                    model: rawTensorModel, crawlidFloor: loaded.confirmation?.weakConfidence
                )
            }
            var base = LiveObjectDetectionPostprocessor.detections(
                from: predictions,
                artifact: loaded.artifact,
                sourceFrameIdentifier: sourceFrameIdentifier
            )
            if let knight = loaded.knightRecipe, let reference = loaded.knightReference, knight.needsReference(base),
               let measured = try? knight.localizer.detect(in: image, using: reference, identifier: LiveKnightConfirmation.identifier,
                    threshold: knight.referenceConfidence, frame: sourceFrameIdentifier, version: loaded.artifact.version) {
                base = knight.supplement(base, reference: measured)
            }
            // The deposit sprite changes during nail hits. Retain an existing box
            // and fill a missing sighting with the strongest trained deposit peak.
            if let deposit = loaded.deposit, base.count < LiveObjectDetectionPostprocessor.maximumDetections,
               !base.contains(where: { $0.classIdentifier == LiveGeoDepositConfirmation.identifier }),
               let peaks = try? deposit.recipe.detect(in: image, using: deposit.model, identifier: LiveGeoDepositConfirmation.identifier,
                   threshold: deposit.recipe.standaloneConfidence, frame: sourceFrameIdentifier, version: loaded.artifact.version),
               let peak = peaks.first(where: LiveGeoDepositConfirmation.visibleDeposit) {
                base.append(peak)
            }
            guard let confirmation = loaded.confirmation, let reference = loaded.reference,
                  let raw = loaded.rawTensorModel else { detections += base; continue }
            let weak = LiveObjectDetectionPostprocessor.detections(
                from: predictions.filter { $0.classIdentifier == LiveCrawlidConfirmation.identifier },
                artifact: loaded.artifact, sourceFrameIdentifier: sourceFrameIdentifier, minimumConfidence: confirmation.weakConfidence
            ).filter { $0.confidence < 0.5 }
            guard !weak.isEmpty || confirmation.localizer != nil else { detections += base; continue }
            do {
                let verified: [LiveObjectDetection]
                if let localizer = confirmation.localizer {
                    verified = try localizer.detect(in: image, using: reference, identifier: LiveCrawlidConfirmation.identifier,
                        threshold: confirmation.referenceConfidence, frame: sourceFrameIdentifier, version: loaded.artifact.version)
                } else {
                    let check = VNCoreMLRequest(model: reference)
                    check.imageCropAndScaleOption = .scaleFill
                    try VNImageRequestHandler(cgImage: image, orientation: .up).perform([check])
                    let candidates = rawPredictions(from: check.results ?? [], model: raw, onlyCrawlid: true,
                                                    crawlidFloor: confirmation.referenceConfidence)
                    verified = LiveObjectDetectionPostprocessor.detections(from: candidates, artifact: loaded.artifact,
                        sourceFrameIdentifier: sourceFrameIdentifier, minimumConfidence: confirmation.referenceConfidence)
                }
                detections += confirmation.supplement(base, weak: weak, reference: verified)
            } catch {
                detections += base
            }
        }
        return detections
    }

    private func rawPredictions(
        from observations: [VNObservation],
        model: RawTensorModel, onlyCrawlid: Bool = false, crawlidFloor: Double? = nil
    ) -> [LiveRawObjectPrediction] {
        let features = observations.compactMap {
            $0 as? VNCoreMLFeatureValueObservation
        }
        guard let scores = features.first(where: {
            $0.featureName == model.scoresName
        })?.featureValue.multiArrayValue,
        let boxes = features.first(where: {
            $0.featureName == model.boxesName
        })?.featureValue.multiArrayValue,
        let scoreTensor = LiveTensor4D(scores),
        let boxTensor = LiveTensor4D(boxes) else { return [] }
        let scoreShape = scoreTensor.shape
        let boxShape = boxTensor.shape
        guard
              scoreShape[0] == 1,
              boxShape[0] == 1,
              scoreShape[1] == model.classNames.count,
              boxShape[1] == 4,
              scoreShape[2] == boxShape[2],
              scoreShape[3] == boxShape[3] else { return [] }

        let height = scoreShape[2]
        let width = scoreShape[3]
        var predictions = [LiveRawObjectPrediction]()
        for classIndex in model.classNames.indices {
            let name = LabelingClassIdentity.canonicalIdentifier(model.classNames[classIndex])
            guard model.trainedClassIdentifiers.contains(name), !onlyCrawlid || name == LiveCrawlidConfirmation.identifier else { continue }
            let floor = (name == LiveCrawlidConfirmation.identifier ? crawlidFloor : nil) ??
                LiveObjectDetectionPostprocessor.confidenceThreshold(for: name)
            for y in 0..<height {
                for x in 0..<width {
                    let confidence = scoreTensor.value(channel: classIndex, y: y, x: x)
                    guard confidence >= floor,
                          isLocalMaximum(
                            scoreTensor,
                            channel: classIndex,
                            y: y,
                            x: x,
                            height: height,
                            width: width,
                            value: confidence
                          ) else { continue }
                    let topLeftX = boxTensor.value(channel: 0, y: y, x: x)
                    let topLeftY = boxTensor.value(channel: 1, y: y, x: x)
                    let boxWidth = boxTensor.value(channel: 2, y: y, x: x)
                    let boxHeight = boxTensor.value(channel: 3, y: y, x: x)
                    predictions.append(LiveRawObjectPrediction(
                        classIdentifier: model.classNames[classIndex],
                        normalizedVisionRect: CGRect(
                            x: topLeftX,
                            y: 1 - topLeftY - boxHeight,
                            width: boxWidth,
                            height: boxHeight
                        ),
                        confidence: confidence
                    ))
                }
            }
        }
        return predictions
    }

    private func isLocalMaximum(
        _ values: LiveTensor4D,
        channel: Int,
        y: Int,
        x: Int,
        height: Int,
        width: Int,
        value candidate: Double
    ) -> Bool {
        for neighborY in max(0, y - 1)...min(height - 1, y + 1) {
            for neighborX in max(0, x - 1)...min(width - 1, x + 1) {
                if values.value(channel: channel, y: neighborY, x: neighborX) > candidate {
                    return false
                }
            }
        }
        return true
    }

}
