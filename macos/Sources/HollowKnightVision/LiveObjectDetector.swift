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
        sourceFrameIdentifier: UInt64
    ) -> [LiveObjectDetection] {
        let unit = CGRect(x: 0, y: 0, width: 1, height: 1)
        let candidates = predictions.compactMap { prediction -> LiveObjectDetection? in
            let classIdentifier = LabelingClassIdentity.canonicalIdentifier(
                prediction.classIdentifier ?? artifact.classIdentifier
            )
            guard prediction.confidence.isFinite,
                  prediction.confidence >= confidenceThreshold(
                    for: classIdentifier
                  ) else { return nil }
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
        let scoresName: String
        let boxesName: String
    }

    private struct LoadedModel {
        let artifact: LabelingActiveModelArtifact
        let model: VNCoreMLModel
        let rawTensorModel: RawTensorModel?
    }

    private(set) var artifacts = [LabelingActiveModelArtifact]()
    private var models = [LoadedModel]()

    func load(_ artifacts: [LabelingActiveModelArtifact]) throws {
        var loaded = [LoadedModel]()
        for artifact in artifacts {
            let compiledURL: URL
            if artifact.modelURL.pathExtension == "mlmodelc" {
                compiledURL = artifact.modelURL
            } else {
                compiledURL = try MLModel.compileModel(at: artifact.modelURL)
            }
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
                    scoresName: "scores",
                    boxesName: "boxes"
                )
            } else {
                rawTensorModel = nil
            }
            loaded.append(LoadedModel(
                artifact: artifact,
                model: try VNCoreMLModel(for: coreModel),
                rawTensorModel: rawTensorModel
            ))
        }
        self.artifacts = artifacts
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
                    model: rawTensorModel
                )
            }
            detections += LiveObjectDetectionPostprocessor.detections(
                from: predictions,
                artifact: loaded.artifact,
                sourceFrameIdentifier: sourceFrameIdentifier
            )
        }
        return detections
    }

    private func rawPredictions(
        from observations: [VNObservation],
        model: RawTensorModel
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
            for y in 0..<height {
                for x in 0..<width {
                    let confidence = scoreTensor.value(channel: classIndex, y: y, x: x)
                    guard confidence >= LiveObjectDetectionPostprocessor.confidenceThreshold(
                        for: model.classNames[classIndex]
                    ),
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
