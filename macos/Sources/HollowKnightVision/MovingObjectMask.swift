import CoreGraphics
import Foundation

/// Mask the measured body and its predicted sweep. Display box smoothing is
/// independent; delayed inference must not expose the moving Knight to mapping.
enum MovingObjectMask {
    static func detections(
        displayed: [LiveObjectDetection], current: LiveObjectDetectionBatch?,
        previous: LiveObjectDetectionBatch?, generation: UInt64, timestamp: Double
    ) -> [LiveObjectDetection] {
        guard let current, current.captureGeneration == generation,
              timestamp >= current.sourceTimestamp,
              timestamp - current.sourceTimestamp <= 0.25 else { return displayed }
        let knight = "game.playable-knight"
        let bodies = current.detections.filter { $0.classIdentifier == knight }
        guard !bodies.isEmpty else { return displayed }
        let age = min(0.15, timestamp - current.sourceTimestamp)
        let masks = bodies.map { body -> LiveObjectDetection in
            var predicted = body.normalizedRect
            if let previous, previous.captureGeneration == generation {
                let dt = current.sourceTimestamp - previous.sourceTimestamp
                if dt >= 1.0 / 120, dt <= 0.25,
                   let prior = previous.detections.filter({ $0.classIdentifier == knight }).min(by: {
                       hypot($0.normalizedRect.midX - body.normalizedRect.midX,
                             $0.normalizedRect.midY - body.normalizedRect.midY)
                           < hypot($1.normalizedRect.midX - body.normalizedRect.midX,
                                   $1.normalizedRect.midY - body.normalizedRect.midY)
                   }) {
                    let dx = body.normalizedRect.midX - prior.normalizedRect.midX
                    let dy = body.normalizedRect.midY - prior.normalizedRect.midY
                    if hypot(dx, dy) <= 0.15 {
                        predicted = predicted.offsetBy(
                            dx: max(-0.08, min(0.08, dx / dt * age)),
                            dy: max(-0.10, min(0.10, dy / dt * age)))
                    }
                }
            }
            let margin = CGFloat(0.004 + age * 0.025)
            let rect = body.normalizedRect.union(predicted)
                .insetBy(dx: -margin, dy: -margin)
                .intersection(CGRect(x: 0, y: 0, width: 1, height: 1))
            return LiveObjectDetection(classIdentifier: body.classIdentifier,
                normalizedRect: rect, confidence: body.confidence,
                sourceFrameIdentifier: body.sourceFrameIdentifier, modelVersion: body.modelVersion)
        }
        return displayed.filter { $0.classIdentifier != knight } + masks
    }
}
