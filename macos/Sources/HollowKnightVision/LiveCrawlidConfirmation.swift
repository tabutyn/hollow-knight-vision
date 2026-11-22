import CoreGraphics
import Foundation

/// An optional calibration for one recorded training run of the shared detector.
struct LiveCrawlidConfirmation: Decodable {
    static let identifier = "enemies.crawlid"
    let sourceRunIdentifier: UUID
    let weakConfidence: Double
    let referenceConfidence: Double
    let minimumOverlap: Double
    var localizer: LiveActorHeatmap? = nil

    static func receiptURL(for artifact: LabelingActiveModelArtifact, override: URL?) -> URL? {
        LiveActorHeatmap.receipt(for: artifact, identifier: identifier, filename: "crawlid", override: override)
    }

    static func load(for artifact: LabelingActiveModelArtifact, override: URL?) -> Self? {
        guard let run = artifact.sourceRunIdentifier, let url = receiptURL(for: artifact, override: override),
              let data = try? Data(contentsOf: url), let check = try? JSONDecoder().decode(Self.self, from: data),
              check.sourceRunIdentifier == run, (0.05..<0.5).contains(check.weakConfidence),
              (0.5...1).contains(check.referenceConfidence), (0.2...1).contains(check.minimumOverlap),
              check.localizer.map({ $0.valid(minimumConfidence: check.referenceConfidence) }) ?? true else { return nil }
        return check
    }

    func supplement(_ base: [LiveObjectDetection], weak: [LiveObjectDetection],
                    reference: [LiveObjectDetection]) -> [LiveObjectDetection] {
        var result = base
        var existing = base.filter { $0.classIdentifier == Self.identifier }
        for candidate in reference where candidate.classIdentifier == Self.identifier && candidate.confidence >= referenceConfidence {
            guard existing.count < LiveObjectDetectionPostprocessor.maximumDetectionsPerClass,
                  result.count < LiveObjectDetectionPostprocessor.maximumDetections else { break }
            let standalone = localizer.map { candidate.confidence >= $0.standaloneConfidence } ?? false
            guard standalone || weak.contains(where: {
                $0.classIdentifier == Self.identifier && $0.confidence >= weakConfidence && $0.confidence < 0.5 &&
                RectangleOverlap.intersectionOverUnion($0.normalizedRect, candidate.normalizedRect) >= minimumOverlap
            }), existing.allSatisfy({
                RectangleOverlap.intersectionOverUnion($0.normalizedRect, candidate.normalizedRect) < (localizer == nil ? 0.35 : 0.33)
            }) else { continue }
            existing.append(candidate)
            result.append(candidate)
        }
        return result
    }
}
