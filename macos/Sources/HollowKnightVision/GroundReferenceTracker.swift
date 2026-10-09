import CoreGraphics
import Foundation

struct GroundReferenceSegment: Equatable {
    let minimumWorldX: CGFloat
    let maximumWorldX: CGFloat
    let worldY: CGFloat?

    init(
        minimumWorldX: CGFloat,
        maximumWorldX: CGFloat,
        worldY: CGFloat? = nil
    ) {
        self.minimumWorldX = minimumWorldX
        self.maximumWorldX = maximumWorldX
        self.worldY = worldY
    }
}

struct GroundReferenceEstimate: Equatable {
    let worldY: CGFloat
    let minimumWorldX: CGFloat
    let maximumWorldX: CGFloat
    let segments: [GroundReferenceSegment]
    let notchWorldXs: [CGFloat]
    let supportingObservationCount: Int
    let confidence: CGFloat
}

/// Tracks the currently relevant horizontal navigation surface. Image evidence
/// owns the line position; the Knight is only a hint for which edge is ground.
final class GroundReferenceTracker: @unchecked Sendable {
    static let minimumStableObservations = 3
    static let maximumCandidateObservations = 6
    static let maximumMissedObservations = 12

    private let lock = NSLock()
    private var candidateWorldYs = [CGFloat]()
    private var switchWorldYs = [CGFloat]()
    private var estimate: GroundReferenceEstimate?
    private var missedObservations = 0

    func observe(
        frame: CGImage,
        cameraPosition: CGPoint,
        solveWidth: CGFloat,
        knightRect: CGRect?,
        excluding excludedRects: [CGRect],
        edgeAnalysis: GroundEdgeAnalysis? = nil,
        comparisonAnalysis: GroundComparisonAnalysis? = nil,
        theoryAnalysis: GroundTheoryAnalysis? = nil,
        tuning: GroundTheoryTuning = .default
    ) -> GroundReferenceEstimate? {
        lock.lock()
        defer { lock.unlock() }

        let presentationWidth = CGFloat(frame.width)
        let presentationHeight = CGFloat(frame.height)
        guard cameraPosition.x.isFinite, cameraPosition.y.isFinite,
              solveWidth.isFinite, solveWidth > 0,
              presentationWidth > 0, presentationHeight > 0
        else { return estimate }
        let scale = presentationWidth / solveWidth
        let predictedFrameY = estimate.map { ($0.worldY - cameraPosition.y) * scale }
        let anchorY = knightRect?.minY ?? predictedFrameY ?? presentationHeight * 0.24
        let lowerRadius = presentationHeight * (knightRect == nil ? 0.12 : 0.18)
        let upperRadius = presentationHeight * (knightRect == nil ? 0.12 : 0.04)
        let searchRange = (anchorY - lowerRadius)...(anchorY + upperRadius)
        let detection: GroundLineDetection?
        if let theoryAnalysis {
            detection = GroundLineDetector.detect(in: theoryAnalysis, searchFrameYRange: searchRange)
        } else if let comparisonAnalysis {
            detection = GroundLineDetector.detect(
                in: comparisonAnalysis,
                searchFrameYRange: searchRange,
                tuning: tuning
            )
        } else if let edgeAnalysis {
            detection = GroundLineDetector.detect(
                in: GroundLineDetector.compare(edgeAnalysis),
                searchFrameYRange: searchRange,
                tuning: tuning
            )
        } else {
            detection = GroundLineDetector.detect(
                in: frame,
                searchFrameYRange: searchRange,
                excluding: excludedRects,
                tuning: tuning
            )
        }
        guard let detection else {
            missedObservations += 1
            if missedObservations > Self.maximumMissedObservations { estimate = nil }
            return estimate
        }
        missedObservations = 0

        let detectedWorldY = cameraPosition.y + detection.frameY / scale
        let detectedNotches = detection.notchFrameXs.map {
            cameraPosition.x + $0 / scale
        }
        let detectedSegments = detection.segments.map {
            GroundReferenceSegment(
                minimumWorldX: cameraPosition.x + $0.minimumFrameX / scale,
                maximumWorldX: cameraPosition.x + $0.maximumFrameX / scale,
                worldY: cameraPosition.y + $0.frameY / scale
            )
        }
        guard detectedWorldY.isFinite,
              detectedNotches.allSatisfy(\.isFinite),
              !detectedNotches.isEmpty,
              !detectedSegments.isEmpty
        else { return estimate }

        if let current = estimate {
            let directTrackingTolerance = max(5, solveWidth * 0.025)
            if abs(detectedWorldY - current.worldY) <= directTrackingTolerance {
                switchWorldYs.removeAll(keepingCapacity: true)
                candidateWorldYs.append(detectedWorldY)
                trim(&candidateWorldYs)
                return publish(
                    worldY: median(candidateWorldYs),
                    cameraPosition: cameraPosition,
                    solveWidth: solveWidth,
                    segments: detectedSegments,
                    notches: detectedNotches,
                    detectedPrimaryWorldY: detectedWorldY,
                    confidence: detection.confidence
                )
            }

            switchWorldYs.append(detectedWorldY)
            trim(&switchWorldYs)
            let recent = Array(switchWorldYs.suffix(Self.minimumStableObservations))
            let switchTolerance = max(5, solveWidth * 0.018)
            guard recent.count == Self.minimumStableObservations,
                  (recent.max() ?? 0) - (recent.min() ?? 0) <= switchTolerance
            else { return current }
            candidateWorldYs = recent
            switchWorldYs.removeAll(keepingCapacity: true)
            return publish(
                worldY: median(recent),
                cameraPosition: cameraPosition,
                solveWidth: solveWidth,
                segments: detectedSegments,
                notches: detectedNotches,
                detectedPrimaryWorldY: detectedWorldY,
                confidence: detection.confidence
            )
        }

        candidateWorldYs.append(detectedWorldY)
        trim(&candidateWorldYs)
        let recent = Array(candidateWorldYs.suffix(Self.minimumStableObservations))
        let acquisitionTolerance = max(5, solveWidth * 0.018)
        guard recent.count == Self.minimumStableObservations,
              (recent.max() ?? 0) - (recent.min() ?? 0) <= acquisitionTolerance
        else { return nil }
        return publish(
            worldY: median(recent),
            cameraPosition: cameraPosition,
            solveWidth: solveWidth,
            segments: detectedSegments,
            notches: detectedNotches,
            detectedPrimaryWorldY: detectedWorldY,
            confidence: detection.confidence
        )
    }

    func applyWorldCorrection(_ correction: CGVector) -> GroundReferenceEstimate? {
        lock.lock()
        defer { lock.unlock() }
        guard correction.dx.isFinite, correction.dy.isFinite, let current = estimate else {
            return estimate
        }
        candidateWorldYs = candidateWorldYs.map { $0 + correction.dy }
        switchWorldYs = switchWorldYs.map { $0 + correction.dy }
        estimate = GroundReferenceEstimate(
            worldY: current.worldY + correction.dy,
            minimumWorldX: current.minimumWorldX + correction.dx,
            maximumWorldX: current.maximumWorldX + correction.dx,
            segments: current.segments.map {
                GroundReferenceSegment(
                    minimumWorldX: $0.minimumWorldX + correction.dx,
                    maximumWorldX: $0.maximumWorldX + correction.dx,
                    worldY: $0.worldY.map { $0 + correction.dy }
                )
            },
            notchWorldXs: current.notchWorldXs.map { $0 + correction.dx },
            supportingObservationCount: current.supportingObservationCount,
            confidence: current.confidence
        )
        return estimate
    }

    func currentEstimate() -> GroundReferenceEstimate? {
        lock.lock()
        defer { lock.unlock() }
        return estimate
    }

    func reset() {
        lock.lock()
        candidateWorldYs.removeAll(keepingCapacity: true)
        switchWorldYs.removeAll(keepingCapacity: true)
        estimate = nil
        missedObservations = 0
        lock.unlock()
    }

    private func publish(
        worldY: CGFloat,
        cameraPosition: CGPoint,
        solveWidth: CGFloat,
        segments: [GroundReferenceSegment],
        notches: [CGFloat],
        detectedPrimaryWorldY: CGFloat,
        confidence: CGFloat
    ) -> GroundReferenceEstimate {
        let margin = solveWidth * 0.02
        let primaryTolerance = max(3, solveWidth * 0.01)
        let stabilizedSegments = segments.map { segment in
            guard let segmentY = segment.worldY,
                  abs(segmentY - detectedPrimaryWorldY) <= primaryTolerance
            else { return segment }
            return GroundReferenceSegment(
                minimumWorldX: segment.minimumWorldX,
                maximumWorldX: segment.maximumWorldX,
                worldY: worldY
            )
        }
        let next = GroundReferenceEstimate(
            worldY: worldY,
            minimumWorldX: max(cameraPosition.x, (notches.min() ?? cameraPosition.x) - margin),
            maximumWorldX: min(
                cameraPosition.x + solveWidth,
                (notches.max() ?? cameraPosition.x + solveWidth) + margin
            ),
            segments: stabilizedSegments,
            notchWorldXs: notches,
            supportingObservationCount: notches.count,
            confidence: confidence
        )
        estimate = next
        return next
    }

    private func trim(_ values: inout [CGFloat]) {
        if values.count > Self.maximumCandidateObservations {
            values.removeFirst(values.count - Self.maximumCandidateObservations)
        }
    }

    private func median(_ values: [CGFloat]) -> CGFloat {
        let sorted = values.sorted()
        guard !sorted.isEmpty else { return 0 }
        let middle = sorted.count / 2
        if sorted.count.isMultiple(of: 2) {
            return (sorted[middle - 1] + sorted[middle]) * 0.5
        }
        return sorted[middle]
    }
}
