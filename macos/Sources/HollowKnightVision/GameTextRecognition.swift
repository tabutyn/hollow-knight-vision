import Foundation
import Vision

struct GameTextLine: Identifiable {
    let id: Int
    let text: String
    let bounds: CGRect  // Normalized Vision coordinates, origin at the lower left.
    let confidence: Float

    func displayRect(in size: CGSize) -> CGRect {
        CGRect(x: bounds.minX * size.width, y: (1 - bounds.maxY) * size.height,
               width: bounds.width * size.width, height: bounds.height * size.height)
    }
}

enum GameTextRecognition {
    static func read(_ image: CGImage, language: String) throws -> [GameTextLine] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false  // Preserve Hallownest's names and button labels.
        let supported = try request.supportedRecognitionLanguages()
        let base = language.split(separator: "-").first.map(String.init) ?? language
        if let locale = supported.first(where: { $0 == language })
                ?? supported.first(where: { $0 == base || $0.hasPrefix(base + "-") }) {
            request.recognitionLanguages = [locale]
        }
        try VNImageRequestHandler(cgImage: image, orientation: .up).perform([request])
        // Use Vision's reading order. Keep uncertain lines visible in the inspector.
        return (request.results ?? []).enumerated().compactMap { index, observation in
            guard let candidate = observation.topCandidates(1).first,
                  !candidate.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            return GameTextLine(id: index + 1, text: candidate.string, bounds: observation.boundingBox,
                                confidence: candidate.confidence)
        }
    }
}
