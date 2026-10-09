import CoreGraphics
import Foundation

/// A camera-pose correction estimated from a training observation.
///
/// This value deliberately carries no image or atlas data.  Held-out frames use
/// only the timestamp and visit to interpolate corrections fitted from training
/// observations.
struct PoseCorrectionSample: Equatable {
    let observationID: Int
    let visitID: UUID
    let timestamp: Double
    let correction: CGPoint

    var isFinite: Bool {
        timestamp.isFinite && correction.x.isFinite && correction.y.isFinite
    }
}

enum PoseCorrectionInterpolator {
    /// Returns a correction derived solely from finite samples in `visitID`.
    /// Samples are ordered by timestamp, observation ID, and UUID so results do
    /// not depend on collection order.  A query outside the sampled interval
    /// holds the nearest endpoint correction.
    static func correction(
        forTimestamp timestamp: Double,
        visitID: UUID,
        samples: [PoseCorrectionSample]
    ) -> CGPoint {
        guard timestamp.isFinite else { return .zero }

        let ordered = samples
            .filter { $0.visitID == visitID && $0.isFinite }
            .sorted(by: isOrderedBefore)
        guard let first = ordered.first, let last = ordered.last else { return .zero }
        if timestamp <= first.timestamp { return first.correction }
        if timestamp >= last.timestamp { return last.correction }

        for index in 1..<ordered.count {
            let upper = ordered[index]
            guard timestamp <= upper.timestamp else { continue }
            let lower = ordered[index - 1]
            let span = upper.timestamp - lower.timestamp
            // Equal timestamps are resolved by deterministic ordering. The
            // endpoint check above selects the first ordered correction.
            guard span > 0 else { return upper.correction }
            let progress = CGFloat((timestamp - lower.timestamp) / span)
            return CGPoint(
                x: lower.correction.x + (upper.correction.x - lower.correction.x) * progress,
                y: lower.correction.y + (upper.correction.y - lower.correction.y) * progress
            )
        }
        return last.correction
    }

    static func refinedPose(
        raw: CGPoint,
        forTimestamp timestamp: Double,
        visitID: UUID,
        samples: [PoseCorrectionSample]
    ) -> CGPoint {
        guard raw.x.isFinite, raw.y.isFinite else { return raw }
        let offset = correction(forTimestamp: timestamp, visitID: visitID, samples: samples)
        return CGPoint(x: raw.x + offset.x, y: raw.y + offset.y)
    }

    private static func isOrderedBefore(_ lhs: PoseCorrectionSample, _ rhs: PoseCorrectionSample) -> Bool {
        if lhs.timestamp != rhs.timestamp { return lhs.timestamp < rhs.timestamp }
        if lhs.observationID != rhs.observationID { return lhs.observationID < rhs.observationID }
        return lhs.visitID.uuidString < rhs.visitID.uuidString
    }
}
