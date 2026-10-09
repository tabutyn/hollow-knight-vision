import CoreGraphics
import Foundation
import OSLog

/// Pose acceptance and reference promotion have different costs. A marginal
/// match can bridge one frame, but copying it into the reference chain makes
/// that uncertainty permanent. Hacker-truth cohorts show sparse/high-error
/// promotions have more than twice the p95 handoff error of this subset.
struct GroundReferencePromotionPolicy {
    static func permits(support: Int, error: CGFloat) -> Bool {
        (support >= 6 && error <= 18) || (support >= 4 && error <= 10)
    }
}

struct GroundRecentReferenceCadence {
    static let minimumAge = 0.08
    static let minimumTravel: CGFloat = 16

    static func shouldRefresh(age: Double, travel: CGFloat) -> Bool {
        age >= minimumAge || travel >= minimumTravel
    }
}

struct GroundPoseAcceptancePolicy {
    static func rejects(support: Int, error: CGFloat) -> Bool {
        (support <= 4 && error > 14)
            || (support < 8 && error > 18)
            || (support < 12 && error > 20)
    }
}

struct GroundPoseInnovationPolicy {
    static func rejects(
        support: Int, error: CGFloat, innovation: CGFloat
    ) -> Bool {
        support < 12 && error > 19 && innovation > 8
    }
}

struct GroundProvisionalPromotionPolicy {
    static let minimumDuration = 0.20
    static let minimumConsecutiveMatches = 12

    static func permits(
        elapsed: Double, consecutiveMatches: Int, support: Int, error: CGFloat
    ) -> Bool {
        elapsed >= minimumDuration
            && consecutiveMatches >= minimumConsecutiveMatches
            && GroundReferencePromotionPolicy.permits(
                support: support, error: error
            )
    }
}

/// Ground rows propose Y first. Ordered, disjoint 16x12 patches then measure
/// X and verify the proposed row identity. No whole-image registration.
final class GroundCameraSolver {
    private let traceEnabled = ProcessInfo.processInfo.arguments.contains("--trace-ground-tracking")
    private let traceLog = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "ground-trace")
    private let subpixelEnabled: Bool
    private let keyframeSubpixelEnabled: Bool
    private let anchorImageMotionScale: CGVector
    private let recentImageMotionScale: CGVector
    private let usesAdaptiveRecentMotionScale: Bool

    static let hackerCalibratedAnchorScale = CGVector(dx: 0.9929, dy: 0.9947)
    static let hackerCalibratedRecentScale = CGVector(dx: 0.9809, dy: 0.9923)

    init(
        subpixelEnabled: Bool = ProcessInfo.processInfo.arguments.contains(
            "--ground-texture-subpixel"
        ),
        keyframeSubpixelEnabled: Bool = ProcessInfo.processInfo.arguments.contains(
            "--ground-keyframe-subpixel"
        ),
        calibratedMotionEnabled: Bool = ProcessInfo.processInfo.arguments.contains(
            "--ground-hacker-calibrated-motion"
        ),
        imageMotionScale: CGVector? = nil,
        recentImageMotionScale: CGVector? = nil
    ) {
        self.subpixelEnabled = subpixelEnabled
        self.keyframeSubpixelEnabled = keyframeSubpixelEnabled
        let calibrated = calibratedMotionEnabled
        usesAdaptiveRecentMotionScale = calibrated
            && imageMotionScale == nil && recentImageMotionScale == nil
        anchorImageMotionScale = imageMotionScale
            ?? (calibrated ? Self.hackerCalibratedAnchorScale : CGVector(dx: 1, dy: 1))
        self.recentImageMotionScale = recentImageMotionScale ?? imageMotionScale
            ?? (calibrated ? Self.hackerCalibratedRecentScale : CGVector(dx: 1, dy: 1))
    }

    struct Solution {
        let imageTranslation: CGVector
        let support: Int
        let error: CGFloat
        /// False when motion is measured from pixels whose absolute camera
        /// coordinate came from an unverified fallback. Relative motion may
        /// continue, but this pose cannot write or display the atlas yet.
        var referenceTrusted = true
        /// Development provenance for exact-frame Hacker comparison. These
        /// values describe the visual reference used by this solve; they never
        /// participate in matching or acceptance.
        var referenceTimestamp: Double? = nil
        var referencePosition: CGVector? = nil
        var matchedImageDisplacement: CGVector? = nil
        var matchSource: String? = nil
    }

    private struct Reference {
        let pixels: [UInt8]
        let lines: [CleanFloorLine]
        let exclusions: [CGRect]
        let timestamp: Double
        let position: CGVector
        let poseTrusted: Bool

        init(
            pixels: [UInt8], lines: [CleanFloorLine], exclusions: [CGRect],
            timestamp: Double, position: CGVector, poseTrusted: Bool = true
        ) {
            self.pixels = pixels
            self.lines = lines
            self.exclusions = exclusions
            self.timestamp = timestamp
            self.position = position
            self.poseTrusted = poseTrusted
        }
    }
    private struct Patch {
        let x: Int
        let y: Int
        let pixels: [UInt8]
        let mean: CGFloat
        let energy: CGFloat

        init(x: Int, y: Int, pixels: [UInt8]) {
            self.x = x; self.y = y; self.pixels = pixels
            var sum: CGFloat = 0, squared: CGFloat = 0
            for pixel in pixels {
                let value = CGFloat(pixel)
                sum += value; squared += value * value
            }
            let count = CGFloat(max(1, pixels.count))
            mean = sum / count
            energy = max(0, squared / count - mean * mean)
        }
    }
    private var reference: Reference?
    private var recentReference: Reference?
    private var provisionalReference: Reference?
    private var spatialReferences = [Reference]()
    private var velocity = CGVector.zero
    private var imagePosition = CGVector.zero
    private var lastAcceptedTimestamp: Double?
    private var size = CGSize.zero
    private(set) var seeded = false
    private var lastObservedPixels: [UInt8]?
    private(set) var distinctFrame = true
    /// A provisional texture epoch may outlive one failed frame. The outer
    /// camera owner uses this to avoid replacing that independent trajectory
    /// with a coarse pose before local texture has had time to reacquire.
    var hasProvisionalReference: Bool { provisionalReference != nil }

    private struct State {
        let reference: Reference?
        let recent: Reference?
        let provisional: Reference?
        let velocity: CGVector
        let position: CGVector
        let timestamp: Double?
        let spatial: [Reference]
    }
    private var lastAttempt: (timestamp: Double, state: State)?
    private var state: State {
        get { State(reference: reference, recent: recentReference,
                    provisional: provisionalReference, velocity: velocity,
                    position: imagePosition, timestamp: lastAcceptedTimestamp, spatial: spatialReferences) }
        set {
            reference = newValue.reference; recentReference = newValue.recent
            provisionalReference = newValue.provisional
            velocity = newValue.velocity; imagePosition = newValue.position
            lastAcceptedTimestamp = newValue.timestamp
            spatialReferences = newValue.spatial
        }
    }

    func reset() {
        reference = nil
        recentReference = nil
        provisionalReference = nil
        spatialReferences.removeAll()
        velocity = .zero
        imagePosition = .zero
        lastAcceptedTimestamp = nil
        size = .zero
        seeded = false
        lastAttempt = nil
        lastObservedPixels = nil
        distinctFrame = true
    }

    /// Global verification changes this capture's world coordinate. Re-anchor
    /// the current pixels at that coordinate so the next local solve measures
    /// only new camera motion instead of undoing the accepted closure against
    /// a drifted pre-closure reference. Historical anchors remain immutable.
    func applyGlobalCorrection(
        _ cameraCorrection: CGVector,
        currentPixels: [UInt8],
        lines: [CleanFloorLine],
        timestamp: Double,
        exclusions: [CGRect]
    ) {
        guard currentPixels.count == Int(size.width * size.height),
              timestamp.isFinite else { return }
        let imageCorrection = CGVector(dx: -cameraCorrection.dx, dy: cameraCorrection.dy)
        imagePosition.dx += imageCorrection.dx
        imagePosition.dy += imageCorrection.dy
        if hypot(cameraCorrection.dx, cameraCorrection.dy) > 4 {
            velocity = .zero
        }
        let anchor = Reference(
            pixels: currentPixels,
            lines: lines,
            exclusions: exclusions,
            timestamp: timestamp,
            position: imagePosition
        )
        reference = anchor
        recentReference = anchor
        provisionalReference = nil
        lastAcceptedTimestamp = timestamp
        spatialReferences.removeAll {
            hypot(
                $0.position.dx - imagePosition.dx,
                $0.position.dy - imagePosition.dy
            ) <= 2
        }
        spatialReferences.append(anchor)
        if spatialReferences.count > 12 { spatialReferences.removeFirst() }
    }

    /// Lets the ordered persistent ground tiles bridge a local anchor miss.
    /// The caller has already required a low-residual, horizontally spread
    /// consensus. Refreshing the recent anchor here prevents one dropped solve
    /// from expanding forever around a frozen camera pose.
    func acceptFeatureRecovery(
        pixels: [UInt8], width: Int, height: Int,
        lines: [CleanFloorLine], timestamp: Double,
        exclusions: [CGRect], imageTranslation: CGVector
    ) {
        guard pixels.count == width * height, timestamp.isFinite,
              imageTranslation.dx.isFinite, imageTranslation.dy.isFinite,
              size == CGSize(width: width, height: height)
        else { return }
        let elapsed = max(1.0 / 240, timestamp - (lastAcceptedTimestamp ?? timestamp))
        velocity = CGVector(
            dx: velocity.dx * 0.5 + imageTranslation.dx / elapsed * 0.5,
            dy: velocity.dy * 0.5 + imageTranslation.dy / elapsed * 0.5
        )
        imagePosition.dx += imageTranslation.dx
        imagePosition.dy += imageTranslation.dy
        lastAcceptedTimestamp = timestamp
        let recovered = Reference(
            pixels: pixels,
            lines: lines,
            exclusions: exclusions,
            timestamp: timestamp,
            position: imagePosition
        )
        reference = recovered
        recentReference = recovered
        provisionalReference = nil
        if let nearby = spatialReferences.indices.min(by: {
            hypot(spatialReferences[$0].position.dx - imagePosition.dx,
                  spatialReferences[$0].position.dy - imagePosition.dy)
                < hypot(spatialReferences[$1].position.dx - imagePosition.dx,
                        spatialReferences[$1].position.dy - imagePosition.dy)
        }), hypot(spatialReferences[nearby].position.dx - imagePosition.dx,
                   spatialReferences[nearby].position.dy - imagePosition.dy) <= 2 {
            spatialReferences[nearby] = recovered
        } else {
            spatialReferences.append(recovered)
            if spatialReferences.count > 12 { spatialReferences.removeFirst() }
        }
        seeded = false
        if traceEnabled {
            traceLog.notice(
                "feature-recovery t=\(timestamp, privacy: .public) delta=\(imageTranslation.dx, privacy: .public),\(imageTranslation.dy, privacy: .public)"
            )
        }
    }

    /// A measured fallback updates the prediction, not the immutable ground
    /// history. A biased fallback must remain correctable by the next direct
    /// comparison with a trusted reference. An externally rejected solve must
    /// not leave its newly written anchors behind either.
    func followMeasuredCamera(
        correction: CGVector, pixels: [UInt8], width: Int, height: Int,
        lines: [CleanFloorLine], timestamp: Double, exclusions: [CGRect],
        preservesTrustedHistory: Bool = true
    ) {
        guard pixels.count == width * height, timestamp.isFinite,
              correction.dx.isFinite, correction.dy.isFinite else { return }
        guard size == CGSize(width: width, height: height), reference != nil else {
            reset()
            _ = solve(pixels: pixels, width: width, height: height,
                lines: lines, timestamp: timestamp, exclusions: exclusions)
            return
        }
        let measured = CGVector(dx: imagePosition.dx - correction.dx,
                                dy: imagePosition.dy + correction.dy)
        let rejectedSameFrameAttempt = lastAttempt.map {
            abs($0.timestamp - timestamp) < 0.000_001
        } ?? false
        if let attempt = lastAttempt, rejectedSameFrameAttempt {
            state = attempt.state
        }
        let elapsed = timestamp - (lastAcceptedTimestamp ?? timestamp)
        if elapsed > 0 {
            velocity = CGVector(
                dx: velocity.dx * 0.5 + (measured.dx - imagePosition.dx) / elapsed * 0.5,
                dy: velocity.dy * 0.5 + (measured.dy - imagePosition.dy) / elapsed * 0.5)
        }
        imagePosition = measured
        lastAcceptedTimestamp = timestamp
        if !preservesTrustedHistory {
            // Before any persistent ground exists, the initial zero coordinate
            // is arbitrary. Adopt the restored/coarse pose as the epoch origin
            // instead of permanently challenging it from that empty origin.
            let initial = Reference(
                pixels: pixels, lines: lines, exclusions: exclusions,
                timestamp: timestamp, position: measured
            )
            reference = initial
            recentReference = initial
            provisionalReference = nil
            spatialReferences = [initial]
            seeded = false
            lastAttempt = nil
            return
        }
        // Fallback pixels preserve relative texture continuity, but their
        // absolute coordinate came from coarse/bridge motion. Keep that
        // provenance until a persistent ground anchor or global fit agrees.
        provisionalReference = Reference(
            pixels: pixels, lines: lines, exclusions: exclusions,
            timestamp: timestamp, position: measured, poseTrusted: false
        )
        seeded = false
        lastAttempt = nil
    }

    /// Converts a motion-bridge-seeded provisional texture epoch into a local
    /// trusted anchor after the tracker has independently observed a sustained
    /// sequence of strong texture matches. Placement validation may still
    /// reject this frame and restore the provisional references via
    /// `deferReferenceUpdates`.
    func promoteProvisionalReference(
        pixels: [UInt8], width: Int, height: Int,
        lines: [CleanFloorLine], timestamp: Double, exclusions: [CGRect]
    ) {
        guard provisionalReference != nil,
              pixels.count == width * height,
              size == CGSize(width: width, height: height) else { return }
        let trusted = Reference(
            pixels: pixels, lines: lines, exclusions: exclusions,
            timestamp: timestamp, position: imagePosition, poseTrusted: true
        )
        reference = trusted
        recentReference = trusted
        provisionalReference = nil
        spatialReferences.append(trusted)
        if spatialReferences.count > 12 { spatialReferences.removeFirst() }
        seeded = false
    }

    /// Keep the tentative pose for the next prediction, but do not let a
    /// rejected measurement become its own reference/independent confirmation.
    func deferReferenceUpdates(at timestamp: Double) {
        guard let attempt = lastAttempt, abs(attempt.timestamp - timestamp) < 0.000_001 else { return }
        reference = attempt.state.reference
        recentReference = attempt.state.recent
        provisionalReference = attempt.state.provisional
        spatialReferences = attempt.state.spatial
    }

    func solve(
        pixels: [UInt8], width: Int, height: Int,
        lines: [CleanFloorLine], timestamp: Double,
        exclusions: [CGRect]
    ) -> Solution? {
        distinctFrame = lastObservedPixels != pixels
        lastObservedPixels = pixels
        lastAttempt = (timestamp, state)
        // Return to a nearby historical view before discarding its texture.
        // Hysteresis prevents switching back and forth at keyframe boundaries.
        func distance(_ value: Reference) -> CGFloat {
            abs(value.position.dx - imagePosition.dx) + abs(value.position.dy - imagePosition.dy)
        }
        var anchorSource = "anchor"
        if let current = reference, let nearby = spatialReferences.min(by: { distance($0) < distance($1) }),
           distance(nearby) + min(CGFloat(width), CGFloat(height)) * 0.04 < distance(current) {
            reference = nearby
            anchorSource = "spatial-anchor"
        }
        let before = state
        func solveProvisional() -> Solution? {
            guard let provisional = before.provisional else {
                state = before
                return nil
            }
            state = before
            reference = provisional
            guard let result = solveReference(
                pixels: pixels, width: width, height: height,
                lines: lines, timestamp: timestamp, exclusions: exclusions,
                matchSource: "provisional"
            ) else {
                state = before
                return nil
            }
            // The provisional solve may advance its own recent pixels and the
            // prediction, but persistent ground references stay unchanged.
            reference = before.reference
            recentReference = before.recent
            spatialReferences = before.spatial
            return result
        }
        let anchored = solveReference(pixels: pixels, width: width, height: height,
            lines: lines, timestamp: timestamp, exclusions: exclusions,
            matchSource: anchorSource)
        if let anchored, seeded || anchored.error < 10 || anchored.support >= 10 {
            provisionalReference = nil
            return anchored
        }
        let anchoredState = state
        func alternativeDecision(
            _ result: Solution?, over anchored: Solution,
            reference alternative: Reference
        ) -> (use: Bool, stronger: Bool) {
            let stronger = result.map {
                let preciseSparse = anchored.support <= 6
                    && $0.support >= max(5, anchored.support)
                    && $0.error <= 4 && $0.error * 3 + 1 < anchored.error
                let broadConsensus = anchored.support <= 8
                    && $0.support >= max(10, anchored.support * 2)
                    && $0.error <= 10 && $0.error * 2 + 1 < anchored.error
                return (preciseSparse || broadConsensus)
                    && timestamp - alternative.timestamp <= 0.1
                    && hypot($0.imageTranslation.dx - anchored.imageTranslation.dx,
                             $0.imageTranslation.dy - anchored.imageTranslation.dy) <= 32
            } ?? false
            let use = result.map {
                ($0.support >= 6 || stronger) && $0.error + 1 < anchored.error * 0.65
                    && (stronger || hypot(
                        $0.imageTranslation.dx - anchored.imageTranslation.dx,
                        $0.imageTranslation.dy - anchored.imageTranslation.dy
                    ) <= 6)
            } ?? false
            return (use, stronger)
        }
        // Probe recent evidence before a weak, sparsely supported anchor fails.
        // Both proposals start from exactly the same pose/time. Failed probes
        // cannot mutate the accepted anchor or count the frame twice.
        guard let anchor = before.reference, let recent = before.recent,
              anchor.timestamp != recent.timestamp else {
            if let anchored {
                let provisional = before.provisional
                let result = solveProvisional()
                let useProvisional = provisional.map {
                    alternativeDecision(result, over: anchored, reference: $0).use
                } ?? false
                if useProvisional { return result }
                state = anchoredState
                provisionalReference = nil
                return anchored
            }
            return solveProvisional()
        }
        state = before
        reference = recent
        let result = solveReference(pixels: pixels, width: width, height: height,
            lines: lines, timestamp: timestamp, exclusions: exclusions,
            matchSource: "recent")
        if let anchored {
            // A stale strip can match a repeated texture at the wrong phase.
            // Do not let its displacement veto substantially stronger recent
            // evidence: equal or broader independent support, lower pixel error,
            // fresh pixels, and a bounded disagreement are all required.
            let decision = alternativeDecision(result, over: anchored, reference: recent)
            let strongerRecent = decision.stronger
            let useRecent = decision.use
            if !useRecent {
                state = anchoredState
                provisionalReference = nil
                if traceEnabled {
                    traceLog.info("solve t=\(timestamp, privacy: .public) age=\(timestamp - anchor.timestamp, privacy: .public) outcome=accepted source=selected-anchor")
                }
                return anchored
            }
            if strongerRecent {
                if traceEnabled {
                    traceLog.info("solve t=\(timestamp, privacy: .public) outcome=accepted source=stronger-recent")
                }
                reference = recent.poseTrusted ? recentReference : anchor
                provisionalReference = nil
                return result
            }
        }
        if let result {
            reference = recent.poseTrusted
                && abs(imagePosition.dx - anchor.position.dx) > CGFloat(width) * 0.35
                ? recentReference : anchor
            provisionalReference = nil
            return result
        }
        return solveProvisional()
    }

    private func solveReference(
        pixels: [UInt8], width: Int, height: Int,
        lines: [CleanFloorLine], timestamp: Double,
        exclusions: [CGRect],
        matchSource: String
    ) -> Solution? {
        // Opt-in diagnostics only. Never participate in match selection.
        var traceOutcome = "invalid-input"
        var traceDetail = ""
        let traceReference = reference
        let diagnostics = traceEnabled
        defer {
            if traceEnabled {
                let oldRows = traceReference?.lines.map { String($0.row) }.joined(separator: ",") ?? "none"
                let newRows = lines.map { String($0.row) }.joined(separator: ",")
                let age = traceReference.map { timestamp - $0.timestamp } ?? 0
                traceLog.info("solve t=\(timestamp, privacy: .public) age=\(age, privacy: .public) old=\(oldRows, privacy: .public) rows=\(newRows, privacy: .public) exclusions=\(exclusions.count, privacy: .public) outcome=\(traceOutcome, privacy: .public) \(traceDetail, privacy: .public)")
            }
        }
        guard pixels.count == width * height, timestamp.isFinite else { return nil }
        if size != CGSize(width: width, height: height) { reset() }
        size = CGSize(width: width, height: height)
        seeded = false
        guard let prior = reference else {
            traceOutcome = "no-seed-lines"
            guard !lines.isEmpty else { return nil }
            reference = Reference(pixels: pixels, lines: lines,
                                  exclusions: exclusions, timestamp: timestamp, position: .zero)
            recentReference = reference
            spatialReferences = [reference!]
            lastAcceptedTimestamp = timestamp
            seeded = true
            traceOutcome = "seeded"
            return Solution(imageTranslation: .zero, support: 0, error: 0)
        }
        let elapsed = timestamp - (lastAcceptedTimestamp ?? prior.timestamp)
        traceOutcome = "nonpositive-time"
        guard elapsed > 0 else { return nil }
        let predictedX = Int((imagePosition.dx - prior.position.dx + velocity.dx * min(elapsed, 0.1)).rounded())
        let predictedY = Int((imagePosition.dy - prior.position.dy + velocity.dy * min(elapsed, 0.1)).rounded())
        // Duplicate captures followed by direction look-ahead can jump beyond
        // eight pixels even at 60 Hz. Keep that acceleration inside the search.
        let radiusX = min(width, max(16, min(96, Int(ceil(elapsed * 400)))))
        // Hollow Knight can advance its camera by more than eight pixels after
        // a repeated capture, but a different platform hundreds of pixels
        // away is never one-frame camera motion. Keep fast pans while bounding
        // row identity; recovery handles motion outside this local band.
        let radiusY = min(height, max(32, min(64, Int(ceil(elapsed * 240)))))
        // A brief edge dropout does not remove an established search band.
        // Texture must still verify the predicted translation before use.
        let searchLines = lines.isEmpty ? prior.lines.map {
            CleanFloorLine(row: $0.row + predictedY,
                           xRange: ($0.xRange.lowerBound + predictedX)...($0.xRange.upperBound + predictedX))
        } : lines
        var verticalVotes = [Int: Int]()
        for old in prior.lines {
            for line in searchLines {
                let dy = line.row - old.row
                // A repeated capture must not suppress a strong row proposal
                // when the following game frame starts a fast vertical pan.
                // Texture, not a velocity-only gate, verifies row identity.
                let overlap = min(old.xRange.upperBound + predictedX, line.xRange.upperBound)
                    - max(old.xRange.lowerBound + predictedX, line.xRange.lowerBound) + 1
                if overlap >= 32 { verticalVotes[dy, default: 0] += overlap }
            }
        }
        var plausibleVerticalVotes = verticalVotes.filter {
            abs($0.key - predictedY) <= radiusY || abs($0.key) <= radiusY
        }
        // A landing camera snap can leave no row inside the ordinary local
        // band. Only then nominate a bounded, broadly overlapping row. The
        // entire texture population and ambiguity check still verify it.
        let longRowOnly = plausibleVerticalVotes.isEmpty
        if longRowOnly {
            plausibleVerticalVotes = verticalVotes.filter {
                abs($0.key) <= 96 && $0.value >= 256
            }
        }
        let hypotheses = Array(plausibleVerticalVotes.keys.sorted {
            plausibleVerticalVotes[$0] == plausibleVerticalVotes[$1]
                ? abs($0 - predictedY) < abs($1 - predictedY)
                : plausibleVerticalVotes[$0]! > plausibleVerticalVotes[$1]!
        }.prefix(4))
        traceOutcome = "no-row-hypothesis"
        if diagnostics {
            traceDetail = "pred=\(predictedX),\(predictedY) radius=\(radiusX),\(radiusY) longRow=\(longRowOnly) hypotheses=\(hypotheses.map(String.init).joined(separator: ","))"
        }
        guard !hypotheses.isEmpty else { return nil }
        var candidates = [(dx: Int, dy: Int, error: CGFloat, support: Int)]()
        // All hypotheses see the same reference evidence. Restricting source
        // patches to each proposed row pairing allowed a tiny accidental match
        // to beat the motion supported by the rest of the visible platforms.
        func makePatches() -> [Patch] {
          var patchGroups = [[Patch]]()
          for old in prior.lines {
            guard old.row >= 0, old.row + 12 <= height else { continue }
            let count = max(0, old.xRange.count / 16)
            let strideCells = max(1, Int(ceil(Double(count) / 8)))
            var group = [Patch]()
            for cell in stride(from: 0, to: count, by: strideCells) {
                let x = old.xRange.lowerBound + cell * 16
                let rect = CGRect(x: x, y: old.row, width: 16, height: 12)
                guard old.evidenceRanges.contains(where: { $0.overlaps(x...(x + 15)) }),
                      !prior.exclusions.contains(where: { $0.intersects(rect) }),
                      let values = GroundPixels.patch(prior.pixels, width: width, height: height,
                                         x: x, y: old.row),
                      (values.max() ?? 0) - (values.min() ?? 0) >= 12
                else { continue }
                group.append(Patch(x: x, y: old.row, pixels: values))
            }
            if !group.isEmpty { patchGroups.append(group) }
          }
        // Balance rows before filling the budget. A long dominant floor must
        // not crowd every smaller platform out of the vertical vote.
          var patches = [Patch]()
        var groupOffset = 0
        while patches.count < 32 {
            var added = false
            for group in patchGroups where groupOffset < group.count {
                patches.append(group[groupOffset])
                added = true
                if patches.count == 32 { break }
            }
            if !added { break }
            groupOffset += 1
        }
          return patches
        }
        let patches = makePatches()
        if diagnostics { traceDetail += " patches=\(patches.count)" }
        guard patches.count >= 3 else { return nil }
        func scan(_ hypotheses: [Int], horizontalRange: ClosedRange<Int>) {
          var verticalScanCache = [Int: [(dx: Int, dy: Int, error: CGFloat, support: Int)]]()
          for proposedY in hypotheses {
            for dy in (proposedY - 3)...(proposedY + 3) {
                // Adjacent edge rows propose overlapping search bands. Their
                // absolute dy scans use identical evidence and search bounds.
                // Reuse the results, retaining candidate order and duplicates
                // so ranking and ambiguity decisions remain exactly the same.
                if let cached = verticalScanCache[dy] {
                    candidates.append(contentsOf: cached)
                    continue
                }
                let firstCandidate = candidates.count
                // A target patch is addressed only by (x, y). Neighboring
                // translation candidates request many of the same targets.
                // Rectify each target once per proposed row.
                var targetCache = [Int: Patch]()
                var unavailableTargets = Set<Int>()
                func evaluate(_ dx: Int) -> (Int, Int, CGFloat, Int)? {
                    var errors = [(x: Int, error: CGFloat)]()
                    for source in patches {
                        let x = source.x + dx
                        let y = source.y + dy
                        let rect = CGRect(x: x, y: y, width: 16, height: 12)
                        guard x >= 0, x + 16 <= width, y >= 0, y + 12 <= height,
                              !exclusions.contains(where: { $0.intersects(rect) }) else { continue }
                        // A partially detected row can still verify previously
                        // observed ground texture beyond today's visible edge
                        // islands. This never creates ground or features.
                        guard searchLines.contains(where: { abs($0.row - y) <= 3 }) else {
                            errors.append((source.x, 32))
                            continue
                        }
                        let key = y * width + x
                        let target: Patch
                        if let cached = targetCache[key] {
                            target = cached
                        } else if unavailableTargets.contains(key) {
                            errors.append((source.x, 32))
                            continue
                        } else if let sampled = GroundPixels.patch(
                            pixels, width: width, height: height,
                            x: x, y: y
                        ) {
                            target = Patch(x: x, y: y, pixels: sampled)
                            targetCache[key] = target
                        } else {
                            unavailableTargets.insert(key)
                            errors.append((source.x, 32))
                            continue
                        }
                        errors.append((source.x, Self.patchError(source, target)))
                    }
                    guard errors.count >= 3 else { return nil }
                    let inliers = errors.filter { $0.error <= 18 }
                    guard inliers.count >= 3,
                          inliers.count * 2 > errors.count,
                          (inliers.map(\.x).max()! - inliers.map(\.x).min()!) >= 32
                    else { return nil }
                    let cost = errors.reduce(CGFloat.zero) { $0 + min(32, $1.error) }
                        / CGFloat(errors.count)
                    return (dx, dy, cost, inliers.count)
                }
                // Edges propose a band, not an exact row. +/-3 includes zero
                // displacement when the detector wobbles by three pixels.
                for dx in horizontalRange {
                    if let value = evaluate(dx) { candidates.append(value) }
                }
                verticalScanCache[dy] = Array(candidates[firstCandidate...])
            }
          }
        }
        typealias Candidate = (dx: Int, dy: Int, error: CGFloat, support: Int)
        func winner(requiringBroadSupport: Bool) -> Candidate? {
            let ranked = candidates.sorted { $0.error < $1.error }
            traceOutcome = "no-texture-consensus"
            guard let best = ranked.first else { return nil }
            if diagnostics { traceDetail += " best=\(best.dx),\(best.dy) error=\(best.error) support=\(best.support)" }
            if let rival = ranked.first(where: {
                abs($0.dx - best.dx) > 2 || abs($0.dy - best.dy) > 2
            }), rival.error <= best.error * 1.12 + 0.25 {
                traceOutcome = "ambiguous"
                if diagnostics { traceDetail += " rival=\(rival.dx),\(rival.dy),\(rival.error)" }
                return nil
            }
            if requiringBroadSupport && (best.support < 8 || best.error > 6)
                && (best.support < 12 || best.error > 10) {
                traceOutcome = "long-row-insufficient-texture"
                return nil
            }
            return best
        }
        scan(hypotheses, horizontalRange: (predictedX - radiusX)...(predictedX + radiusX))
        guard let best = winner(requiringBroadSupport: longRowOnly) else { return nil }
        // Exact-frame Hacker audits exposed rare large-displacement false
        // matches behind sparse three- and four-patch floors. Weak support
        // plus high population cost is not a verified camera pose. Broad
        // matches retain the existing tolerance for lighting and animation.
        guard !GroundPoseAcceptancePolicy.rejects(
            support: best.support,
            error: best.error
        ) else {
            traceOutcome = "weak-high-error"
            return nil
        }
        let coarseDisplacement = CGVector(dx: CGFloat(best.dx), dy: CGFloat(best.dy))
        let coarseMotionScale = motionScale(
            source: matchSource,
            displacement: coarseDisplacement
        )
        let coarseNextPosition = CGVector(
            dx: prior.position.dx + coarseDisplacement.dx * coarseMotionScale.dx,
            dy: prior.position.dy + coarseDisplacement.dy * coarseMotionScale.dy
        )
        let cadenceReference = prior.poseTrusted
            ? recentReference : provisionalReference
        let coarseRecentAge = timestamp - (cadenceReference?.timestamp ?? -.infinity)
        let coarseRecentTravel = cadenceReference.map {
            hypot(
                $0.position.dx - coarseNextPosition.dx,
                $0.position.dy - coarseNextPosition.dy
            )
        } ?? .infinity
        let reachesReferenceCadence = GroundRecentReferenceCadence.shouldRefresh(
            age: coarseRecentAge,
            travel: coarseRecentTravel
        )
            || abs(coarseNextPosition.dx - prior.position.dx) > CGFloat(width) * 0.18
            || abs(coarseNextPosition.dy - prior.position.dy) > CGFloat(height) * 0.12
        let promotesReference = reachesReferenceCadence
            && GroundReferencePromotionPolicy.permits(
                support: best.support,
                error: best.error
            )
        let subpixel = (subpixelEnabled
            || (keyframeSubpixelEnabled && promotesReference))
            ? GroundSubpixelRegistration.refine(
            patches: patches.compactMap { source in
                let x = source.x + best.dx
                let y = source.y + best.dy
                let margin = 5
                let rect = CGRect(
                    x: x - margin,
                    y: y - margin,
                    width: 16 + margin * 2,
                    height: 12 + margin * 2
                )
                guard rect.minX >= 0, rect.minY >= 0,
                      rect.maxX < CGFloat(width), rect.maxY < CGFloat(height),
                      !exclusions.contains(where: { $0.intersects(rect) }),
                      searchLines.contains(where: { abs($0.row - y) <= 3 }),
                      let values = GroundPixels.patch(
                        pixels, width: width, height: height, x: x, y: y
                      ), Self.patchError(
                        source,
                        Patch(x: x, y: y, pixels: values)
                      ) <= 18
                else { return nil }
                return GroundSubpixelRegistration.Patch(
                    x: source.x,
                    y: source.y,
                    pixels: source.pixels
                )
            },
            pixels: pixels,
            width: width,
            height: height,
            dx: best.dx,
            dy: best.dy
        ) : nil
        let matchedDisplacement = CGVector(
            dx: CGFloat(best.dx) + (subpixel?.offset.dx ?? 0),
            dy: CGFloat(best.dy) + (subpixel?.offset.dy ?? 0)
        )
        // Hacker exact-frame cohorts show a stable source-dependent texture
        // motion gain. Keep the raw match above for diagnostics, but express
        // the accepted camera coordinate in projected-ground pixels. The held
        // recent baseline has more integer-phase bias than a broad anchor.
        let motionScale = motionScale(source: matchSource, displacement: matchedDisplacement)
        let projectedDisplacement = CGVector(
            dx: matchedDisplacement.dx * motionScale.dx,
            dy: matchedDisplacement.dy * motionScale.dy
        )
        let nextPosition = CGVector(
            dx: prior.position.dx + projectedDisplacement.dx,
            dy: prior.position.dy + projectedDisplacement.dy)
        let translation = CGVector(dx: nextPosition.dx - imagePosition.dx,
                                   dy: nextPosition.dy - imagePosition.dy)
        let expectedStep = CGVector(
            dx: velocity.dx * min(elapsed, 0.1),
            dy: velocity.dy * min(elapsed, 0.1)
        )
        let innovation = hypot(
            translation.dx - expectedStep.dx,
            translation.dy - expectedStep.dy
        )
        // Weak, high-cost anchor matches occasionally select a nearby repeated
        // texture phase and create a one-frame 10+ pixel pose spike. Preserve
        // genuine fast motion when either texture or population is strong;
        // otherwise prefer one unverified frame to a visibly wrong atlas pose.
        guard !GroundPoseInnovationPolicy.rejects(
            support: best.support, error: best.error, innovation: innovation
        ) else {
            traceOutcome = "weak-motion-innovation"
            return nil
        }
        // A repeated screen capture is not evidence the game camera decelerated.
        if cadenceReference?.pixels != pixels {
            velocity = CGVector(dx: velocity.dx * 0.5 + translation.dx / elapsed * 0.5,
                                dy: velocity.dy * 0.5 + translation.dy / elapsed * 0.5)
        }
        imagePosition = nextPosition
        lastAcceptedTimestamp = timestamp
        let stabilizedLines = searchLines.map { line -> CleanFloorLine in
            let row = prior.lines.map { $0.row + best.dy }.min {
                abs($0 - line.row) < abs($1 - line.row)
            }
            return CleanFloorLine(row: row.flatMap { abs($0 - line.row) <= 3 ? $0 : nil } ?? line.row,
                                  xRange: line.xRange, evidenceRanges: line.evidenceRanges)
        }
        // Keep stable anchors rather than integrating every one-pixel detector
        // wobble. Refresh at real camera displacement in either axis, however:
        // a horizontal-only policy forced a vertical look to compare against a
        // several-minute-old frame until all shared texture left the screen.
        // A strong stationary solve may also refresh pixels at the exact same
        // coordinate so animation/lighting cannot age an otherwise good anchor.
        let currentReference = Reference(
            pixels: pixels,
            lines: stabilizedLines,
            exclusions: exclusions,
            timestamp: timestamp,
            position: nextPosition,
            poseTrusted: prior.poseTrusted
        )
        // A reference rewritten every frame turns integer quantization into
        // odometry drift: at ~3.2 px/frame, repeated recent matches report
        // three pixels and permanently lose the fraction. Hold a short
        // baseline long enough to measure several pixels before refreshing.
        // Persistent anchors below remain independent of this fallback cadence.
        let recentAge = timestamp - (cadenceReference?.timestamp ?? -.infinity)
        let recentTravel = cadenceReference.map {
            hypot($0.position.dx - nextPosition.dx, $0.position.dy - nextPosition.dy)
        } ?? .infinity
        let promotionPermitted = GroundReferencePromotionPolicy.permits(
            support: best.support,
            error: best.error
        )
        if promotionPermitted && GroundRecentReferenceCadence.shouldRefresh(
            age: recentAge,
            travel: recentTravel
        ) {
            if prior.poseTrusted {
                recentReference = currentReference
            } else {
                provisionalReference = currentReference
            }
        }
        let horizontalTravel = abs(nextPosition.dx - prior.position.dx)
        let verticalTravel = abs(nextPosition.dy - prior.position.dy)
        let stationaryRefresh = hypot(translation.dx, translation.dy) <= 0.5
            && timestamp - prior.timestamp >= 1
            && best.support >= 6 && best.error <= 8
        if prior.poseTrusted && promotionPermitted
            && (horizontalTravel > CGFloat(width) * 0.18
            || verticalTravel > CGFloat(height) * 0.12
            || stationaryRefresh) {
            reference = currentReference
            let fresh = currentReference
                // Retain immutable nearby anchors without assigning fresh
                // pixels to a different camera coordinate.
                let replacementDistance: CGFloat = 2
                if let nearbyIndex = spatialReferences.indices.min(by: {
                    hypot(spatialReferences[$0].position.dx - fresh.position.dx,
                          spatialReferences[$0].position.dy - fresh.position.dy)
                        < hypot(spatialReferences[$1].position.dx - fresh.position.dx,
                                spatialReferences[$1].position.dy - fresh.position.dy)
                }), hypot(spatialReferences[nearbyIndex].position.dx - fresh.position.dx,
                           spatialReferences[nearbyIndex].position.dy - fresh.position.dy)
                    <= replacementDistance {
                    // Nearby camera locations are not the same view. The
                    // texture solve measured this offset; assigning today's
                    // pixels to yesterday's nearby coordinate invents motion.
                    // Refresh only an identical coordinate. Keep the old
                    // spatial anchor otherwise, and use the correctly placed
                    // fresh reference for subsequent local motion.
                    if hypot(spatialReferences[nearbyIndex].position.dx - fresh.position.dx,
                             spatialReferences[nearbyIndex].position.dy - fresh.position.dy) < 0.000_001 {
                        spatialReferences[nearbyIndex] = fresh
                    }
                } else {
                    spatialReferences.append(fresh)
                    // Preserve a bounded chain of recent two-dimensional
                    // anchors. Nearby revisits update an existing anchor.
                    if spatialReferences.count > 12 { spatialReferences.removeFirst() }
                }
        }
        traceOutcome = "accepted"
        return Solution(
            imageTranslation: translation,
            support: best.support,
            error: best.error,
            referenceTrusted: prior.poseTrusted,
            referenceTimestamp: prior.timestamp,
            referencePosition: prior.position,
            matchedImageDisplacement: matchedDisplacement,
            matchSource: matchSource
        )
    }

    private func motionScale(source: String, displacement: CGVector) -> CGVector {
        guard source == "recent" || source == "provisional" else {
            return anchorImageMotionScale
        }
        guard usesAdaptiveRecentMotionScale else { return recentImageMotionScale }
        switch hypot(displacement.dx, displacement.dy) {
        case ..<8:
            return CGVector(dx: 0.9920, dy: 0.9982)
        case ..<16:
            return CGVector(dx: 0.9800, dy: 0.9916)
        case ..<32:
            return CGVector(dx: 0.9773, dy: 0.9934)
        default:
            return CGVector(dx: 0.9900, dy: 0.9951)
        }
    }

    /// Mean and bounded contrast normalization accommodate screen-space radial
    /// lighting. No spatial warp or direction assumption is hidden in this score.
    static func patchError(_ reference: [UInt8], _ target: [UInt8]) -> CGFloat {
        patchError(Patch(x: 0, y: 0, pixels: reference), Patch(x: 0, y: 0, pixels: target))
    }

    private static func patchError(_ reference: Patch, _ target: Patch) -> CGFloat {
        guard !reference.pixels.isEmpty, reference.pixels.count == target.pixels.count,
              target.energy >= max(1, reference.energy * 0.1) else { return .infinity }
        let gain = min(2, max(0.5, sqrt(reference.energy / target.energy)))
        var error: CGFloat = 0
        for i in reference.pixels.indices {
            error += abs(CGFloat(reference.pixels[i]) - reference.mean
                - (CGFloat(target.pixels[i]) - target.mean) * gain)
        }
        return error / CGFloat(reference.pixels.count)
    }

}
