import CoreGraphics
import CoreML
import CryptoKit
import Foundation
import Vision

struct LiveGeoDepositConfirmation: Decodable {
    static let identifier = "world.geo-deposit"
    let sourceRunIdentifier: UUID
    let localizer: LiveActorHeatmap

    static func visibleDeposit(_ peak: LiveObjectDetection) -> Bool {
        // The bright rim of the opaque soul vessel can resemble a deposit.
        // These radii cover its interior in normalized, top-left image coordinates.
        let center = peak.normalizedRect
        let dx = (center.midX - 0.091) / 0.027
        let dy = (center.midY - 0.111) / 0.045
        return dx * dx + dy * dy > 1
    }

    static func load(from url: URL, run: UUID?) -> Self? {
        guard let run, let data = try? Data(contentsOf: url),
              let recipe = try? JSONDecoder().decode(Self.self, from: data),
              recipe.sourceRunIdentifier == run, recipe.localizer.valid(minimumConfidence: 0.9) else { return nil }
        return recipe
    }
}

/// A separately trained center heatmap. The active detector's training receipt
/// binds the companion's weights, class and acceptance thresholds.
struct LiveActorHeatmap: Decodable {
    let modelFilename: String
    let sha256: String
    let standaloneConfidence: Double
    let boxWidth: Double
    let boxHeight: Double

    static func receipt(for artifact: LabelingActiveModelArtifact, identifier: String, filename: String,
                        override: URL?) -> URL? {
        guard artifact.trainedClassIdentifiers.contains(identifier), let run = artifact.sourceRunIdentifier else { return nil }
        let family = artifact.modelURL.deletingLastPathComponent().deletingLastPathComponent()
        return override ?? family.appendingPathComponent("calibrations/\(filename)-\(run.uuidString.lowercased()).json")
    }

    func valid(minimumConfidence: Double) -> Bool {
        URL(fileURLWithPath: modelFilename).lastPathComponent == modelFilename
            && (modelFilename as NSString).pathExtension == "mlmodel" && sha256.count == 64
            && (minimumConfidence...1).contains(standaloneConfidence)
            && (0.005...0.3).contains(boxWidth) && (0.005...0.3).contains(boxHeight)
    }

    func verifiedModel(beside receipt: URL) -> URL? {
        let url = receipt.deletingLastPathComponent().appendingPathComponent(modelFilename)
        guard let data = try? Data(contentsOf: url),
              SHA256.hash(data: data).map({ String(format: "%02x", $0) }).joined() == sha256 else { return nil }
        return url
    }

    func detect(in image: CGImage, using model: VNCoreMLModel, identifier: String, threshold: Double,
                frame: UInt64, version: LabelingModelSemanticVersion) throws -> [LiveObjectDetection] {
        let request = VNCoreMLRequest(model: model)
        request.imageCropAndScaleOption = .scaleFill
        try VNImageRequestHandler(cgImage: image, orientation: .up).perform([request])
        guard let scores = (request.results ?? []).compactMap({ $0 as? VNCoreMLFeatureValueObservation })
            .first(where: { $0.featureName == "heatmap" })?.featureValue.multiArrayValue else { return [] }
        return detections(scores, identifier: identifier, threshold: threshold, frame: frame, version: version)
    }

    func detections(_ scores: MLMultiArray, identifier: String, threshold: Double, frame: UInt64,
                    version: LabelingModelSemanticVersion) -> [LiveObjectDetection] {
        let shape = scores.shape.map(\.intValue), strides = scores.strides.map(\.intValue)
        guard shape.suffix(2).elementsEqual([90, 160]), shape.dropLast(2).allSatisfy({ $0 == 1 }),
              let sx = strides.last, let sy = strides.dropLast().last else { return [] }
        func value(_ x: Int, _ y: Int) -> Double {
            let index = y * sy + x * sx
            switch scores.dataType {
            case .float32: return Double(scores.dataPointer.assumingMemoryBound(to: Float.self)[index])
            case .float16: return Double(scores.dataPointer.assumingMemoryBound(to: Float16.self)[index])
            case .double: return scores.dataPointer.assumingMemoryBound(to: Double.self)[index]
            default: return 0
            }
        }
        var peaks = [LiveObjectDetection]()
        for y in 1..<90 {
            for x in 1..<160 {
                let confidence = value(x, y)
                guard (threshold...1).contains(confidence) else { continue }
                let higherNeighbor = (max(0, y - 2)...min(89, y + 2)).contains { row in
                    (max(0, x - 2)...min(159, x + 2)).contains { col in value(col, row) > confidence }
                }
                guard !higherNeighbor else { continue }
                let rect = CGRect(x: Double(x) / 160 - boxWidth / 2, y: Double(y) / 90 - boxHeight / 2,
                    width: boxWidth, height: boxHeight).intersection(CGRect(x: 0, y: 0, width: 1, height: 1))
                peaks.append(.init(classIdentifier: identifier, normalizedRect: rect,
                                   confidence: confidence, sourceFrameIdentifier: frame, modelVersion: version))
            }
        }
        return Array(peaks.sorted { $0.confidence > $1.confidence }.prefix(12))
    }
}
