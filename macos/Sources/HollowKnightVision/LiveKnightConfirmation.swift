import CoreGraphics
import Foundation

/// Recover a missing Knight from an independently trained image model. The
/// separately calibrated HUD exception requires a second, distant sprite match.
struct LiveKnightConfirmation: Decodable {
    static let identifier = "game.playable-knight"
    let sourceRunIdentifier: UUID
    let weakConfidence: Double
    let referenceConfidence: Double
    let correctHUDMatches: Bool
    let localizer: LiveActorHeatmap

    static func load(from url: URL, run: UUID?) -> Self? {
        guard let data = try? Data(contentsOf: url), let recipe = try? JSONDecoder().decode(Self.self, from: data),
              recipe.sourceRunIdentifier == run, (0.05..<0.5).contains(recipe.weakConfidence),
              (0.5...1).contains(recipe.referenceConfidence),
              recipe.localizer.valid(minimumConfidence: recipe.referenceConfidence) else { return nil }
        return recipe
    }

    private func inHUD(_ detection: LiveObjectDetection) -> Bool {
        detection.normalizedRect.midX * 640 < 174 && detection.normalizedRect.midY * 360 < 64
    }

    private func distance(_ a: LiveObjectDetection, _ b: LiveObjectDetection) -> Double {
        hypot((a.normalizedRect.midX - b.normalizedRect.midX) * 640,
              (a.normalizedRect.midY - b.normalizedRect.midY) * 360)
    }

    func needsReference(_ base: [LiveObjectDetection]) -> Bool {
        guard let strong = base.filter({ $0.classIdentifier == Self.identifier && $0.confidence >= 0.5 })
            .max(by: { $0.confidence < $1.confidence }) else { return true }
        return correctHUDMatches && inHUD(strong)
    }

    func supplement(_ base: [LiveObjectDetection], reference: [LiveObjectDetection]) -> [LiveObjectDetection] {
        guard needsReference(base) else { return base }
        let knights = base.filter { $0.classIdentifier == Self.identifier }
        let strong = knights.filter { $0.confidence >= 0.5 }.max { $0.confidence < $1.confidence }
        guard let candidate = reference.first(where: { candidate in
            candidate.classIdentifier == Self.identifier && candidate.confidence >= referenceConfidence &&
                (candidate.confidence >= localizer.standaloneConfidence ||
                 knights.contains { $0.confidence >= weakConfidence && distance($0, candidate) <= 16 })
        }) else { return base }
        if let strong, inHUD(candidate) || distance(strong, candidate) <= 35 { return base }
        return base.filter { $0.classIdentifier != Self.identifier || ($0.confidence < 0.5 && distance($0, candidate) > 16) } + [candidate]
    }
}
