import CoreGraphics
import Foundation

/// Capture-local detections for independent image verification. These are not
/// confirmed ground, do not render in the atlas, and never supply doorway floors.
struct LiveObservedFloorCandidates: Codable, Equatable, Sendable {
    let lines: [LiveGroundLineEvidence]
    static let maximumSpans = 128

    var verificationGeometry: LiveWorldGroundGeometry { .init(lines: lines) }

    func isValid(solveWidth: Double, sourceWidth: Int, sourceHeight: Int) -> Bool {
        !lines.isEmpty && lines.count <= Self.maximumSpans
            && verificationGeometry.isValid(solveWidth: solveWidth,
                sourceWidth: sourceWidth, sourceHeight: sourceHeight)
    }

    static func captured(detected: [CleanFloorLine], frameSize: CGSize,
                         solveWidth: CGFloat, measured: Bool) -> Self? {
        guard measured, frameSize.width.isFinite, frameSize.height.isFinite,
              frameSize.width > 0, frameSize.height > 0,
              frameSize.width < CGFloat(Int.max), frameSize.height < CGFloat(Int.max),
              solveWidth.isFinite, solveWidth > 0 else { return nil }
        let scale = solveWidth / frameSize.width
        var evidence = [LiveGroundLineEvidence]()
        for (index, line) in detected.enumerated() {
            guard line.row >= 0, CGFloat(line.row) < frameSize.height else { continue }
            for range in line.evidenceRanges {
                let low = max(0, range.lowerBound)
                let high = min(Int(frameSize.width) - 1, range.upperBound)
                guard high - low >= 16 else { continue }
                let y = (frameSize.height - CGFloat(line.row)) * scale
                evidence.append(.init(segmentID: index,
                    start: CGPoint(x: CGFloat(low) * scale, y: y),
                    end: CGPoint(x: CGFloat(high + 1) * scale, y: y)))
                if evidence.count == maximumSpans { return Self(lines: evidence) }
            }
        }
        return evidence.isEmpty ? nil : Self(lines: evidence)
    }
}
