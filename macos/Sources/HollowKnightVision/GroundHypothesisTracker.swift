import CoreGraphics
import Foundation
import OSLog

enum GroundHypothesisClassification: Equatable {
    case candidate
    case groundPlane
    case globalMatch
    case occluded
    case depthError
}

struct GroundHypothesisFeature: Equatable {
    let id: Int
    let segmentID: Int
    let sequenceIndex: Int
    /// Pixel coordinates with a top-left origin, matching the detector buffers.
    let imageRect: CGRect
    let classification: GroundHypothesisClassification
    let motionResidual: CGFloat?
    let photometricError: CGFloat?

    init(
        id: Int,
        segmentID: Int,
        sequenceIndex: Int,
        imageRect: CGRect,
        classification: GroundHypothesisClassification,
        motionResidual: CGFloat?,
        photometricError: CGFloat?
    ) {
        self.id = id
        self.segmentID = segmentID
        self.sequenceIndex = sequenceIndex
        self.imageRect = imageRect
        self.classification = classification
        self.motionResidual = motionResidual
        self.photometricError = photometricError
    }
}

struct GroundHypothesisAtlasFeature: Equatable {
    let segmentID: Int
    let sequenceIndex: Int
    /// Immutable bottom-left atlas coordinates. Camera movement must not move
    /// persistent ground review geometry across the already-painted atlas.
    let atlasPosition: CGPoint
    /// Original 16x12 grayscale patch used to create the loop-back descriptor.
    let referencePixels: [UInt8]
    /// Per-pixel confidence. Zero is unknown/unstable; 255 is repeatedly
    /// camera-consistent ground evidence.
    let referenceOpacity: [UInt8]

    init(
        segmentID: Int,
        sequenceIndex: Int,
        atlasPosition: CGPoint,
        referencePixels: [UInt8] = [],
        referenceOpacity: [UInt8] = []
    ) {
        self.segmentID = segmentID
        self.sequenceIndex = sequenceIndex
        self.atlasPosition = atlasPosition
        self.referencePixels = referencePixels
        self.referenceOpacity = referenceOpacity
    }
}

struct GroundHypothesisAtlasLine: Equatable {
    let segmentID: Int
    /// Immutable bottom-left atlas coordinates.
    let atlasStart: CGPoint
    let atlasEnd: CGPoint
}

struct GroundHypothesisTrackingResult: Equatable {
    let features: [GroundHypothesisFeature]
    let cameraTranslation: CGVector?
    let inlierCount: Int
    let residualRMS: CGFloat?
    let globalFeatureCount: Int
    let globalMatchCount: Int
    let globalCameraPosition: CGPoint?
    let globalCorrection: CGVector?
    /// Full-resolution vertical correction applied from persistent clean-line IDs.
    let verticalLineCorrection: CGFloat?
    let groundSegmentCount: Int
    let atlasFeatures: [GroundHypothesisAtlasFeature]
    var atlasLines: [GroundHypothesisAtlasLine]
    var lineReviews: [GroundLinePresenceReview] = []
    var cameraPosition: CGPoint? = nil
    var poseVerified = false
    var hasConfirmedGround = false
    var localTextureSupport = 0
    var localTextureError: CGFloat? = nil
    var localMatchSource: String? = nil
    var provisionalContinuityActive = false
    var localReferenceTimestamp: Double? = nil
    var localReferencePosition: CGVector? = nil
    var localMatchedImageDisplacement: CGVector? = nil

    static let empty = GroundHypothesisTrackingResult(
        features: [],
        cameraTranslation: nil,
        inlierCount: 0,
        residualRMS: nil,
        globalFeatureCount: 0,
        globalMatchCount: 0,
        globalCameraPosition: nil,
        globalCorrection: nil,
        verticalLineCorrection: nil,
        groundSegmentCount: 0,
        atlasFeatures: [],
        atlasLines: []
    )
}

struct GroundFeatureMotion: Equatable {
    let id: Int
    let imageTranslation: CGVector
    let photometricError: CGFloat
}

struct GroundTranslationFit: Equatable {
    let imageTranslation: CGVector
    let inlierIDs: Set<Int>
    let residualRMS: CGFloat
}

/// A translation-only RANSAC. Every observed feature motion is a deterministic
/// hypothesis; the winning consensus is then refined with weighted least
/// squares, which is the exact error-minimizing solve for X/Y translation.
enum GroundTranslationRANSAC {
    static let inlierRadius: CGFloat = 2.5

    static func fit(
        _ motions: [GroundFeatureMotion],
        minimumInliers: Int = 3
    ) -> GroundTranslationFit? {
        guard motions.count >= minimumInliers else { return nil }
        var bestInliers = [GroundFeatureMotion]()
        var bestSeedError = CGFloat.infinity

        for seed in motions {
            let inliers = motions.filter {
                distance($0.imageTranslation, seed.imageTranslation) <= inlierRadius
            }
            let error = inliers.reduce(CGFloat.zero) {
                $0 + distance($1.imageTranslation, seed.imageTranslation)
            }
            if inliers.count > bestInliers.count
                || (inliers.count == bestInliers.count && error < bestSeedError) {
                bestInliers = inliers
                bestSeedError = error
            }
        }
        guard bestInliers.count >= minimumInliers else { return nil }

        var translation = weightedMean(bestInliers)
        for _ in 0..<3 {
            let refined = motions.filter {
                distance($0.imageTranslation, translation) <= inlierRadius
            }
            guard refined.count >= minimumInliers else { break }
            bestInliers = refined
            translation = weightedMean(refined)
        }
        let squaredError = bestInliers.reduce(CGFloat.zero) { total, motion in
            let error = distance(motion.imageTranslation, translation)
            return total + error * error
        }
        return GroundTranslationFit(
            imageTranslation: translation,
            inlierIDs: Set(bestInliers.map(\.id)),
            residualRMS: sqrt(squaredError / CGFloat(bestInliers.count))
        )
    }

    private static func weightedMean(_ motions: [GroundFeatureMotion]) -> CGVector {
        var sumX: CGFloat = 0
        var sumY: CGFloat = 0
        var sumWeight: CGFloat = 0
        for motion in motions {
            let weight = 1 / (1 + max(0, motion.photometricError) / 20)
            sumX += motion.imageTranslation.dx * weight
            sumY += motion.imageTranslation.dy * weight
            sumWeight += weight
        }
        return CGVector(
            dx: sumX / max(.leastNonzeroMagnitude, sumWeight),
            dy: sumY / max(.leastNonzeroMagnitude, sumWeight)
        )
    }

    private static func distance(_ left: CGVector, _ right: CGVector) -> CGFloat {
        hypot(left.dx - right.dx, left.dy - right.dy)
    }
}

private struct GroundGlobalPoseObservation {
    let featureID: Int
    let currentSegmentID: Int
    let globalID: Int
    let globalSegmentID: Int
    let globalWorldX: CGFloat
    let imageX: CGFloat
    let cameraPosition: CGPoint
    let descriptorError: CGFloat
}

private struct GroundGlobalPoseFit {
    let cameraPosition: CGPoint
    let featureIDs: Set<Int>
    let globalIDs: Set<Int>
    let inliers: [GroundGlobalPoseObservation]
    // Fixed before descriptor matching, never narrowed to winning inliers.
    var verificationFeatureIDs: Set<Int>? = nil
}

/// A deterministic absolute-pose consensus solve. Descriptor matches each
/// propose camera X/Y; requiring several matches spread along a floor rejects
/// repeated decorations and animated foreground patches.
private enum GroundGlobalPoseRANSAC {
    static let inlierRadius: CGFloat = 3

    static func fit(
        _ observations: [GroundGlobalPoseObservation],
        minimumInliers: Int = 4,
        minimumHorizontalSpread: CGFloat = 48
    ) -> GroundGlobalPoseFit? {
        guard observations.count >= minimumInliers else { return nil }
        var best = [GroundGlobalPoseObservation]()
        var bestError = CGFloat.infinity
        for seed in observations {
            let inliers = uniqueOrderedInliers(
                observations,
                around: seed.cameraPosition
            )
            guard horizontalSpread(inliers) >= minimumHorizontalSpread,
                  hasLineSequenceAnchor(inliers)
            else { continue }
            let error = inliers.reduce(CGFloat.zero) {
                $0 + hypot(
                    $1.cameraPosition.x - seed.cameraPosition.x,
                    $1.cameraPosition.y - seed.cameraPosition.y
                )
            }
            if inliers.count > best.count || (inliers.count == best.count && error < bestError) {
                best = inliers
                bestError = error
            }
        }
        guard best.count >= minimumInliers else { return nil }

        var position = weightedMean(best)
        for _ in 0..<3 {
            let refined = uniqueOrderedInliers(observations, around: position)
            guard refined.count >= minimumInliers,
                  horizontalSpread(refined) >= minimumHorizontalSpread,
                  hasLineSequenceAnchor(refined)
            else { break }
            best = refined
            position = weightedMean(refined)
        }
        return GroundGlobalPoseFit(
            cameraPosition: position,
            featureIDs: Set(best.map(\.featureID)),
            globalIDs: Set(best.map(\.globalID)),
            inliers: best
        )
    }

    private struct SegmentPair: Hashable {
        let current: Int
        let global: Int
    }

    private static func uniqueOrderedInliers(
        _ observations: [GroundGlobalPoseObservation],
        around position: CGPoint
    ) -> [GroundGlobalPoseObservation] {
        let nearby = observations.filter {
            hypot(
                $0.cameraPosition.x - position.x,
                $0.cameraPosition.y - position.y
            ) <= inlierRadius
        }.sorted { $0.descriptorError < $1.descriptorError }
        var featureIDs = Set<Int>()
        var globalIDs = Set<Int>()
        var unique = [GroundGlobalPoseObservation]()
        for observation in nearby where
            !featureIDs.contains(observation.featureID)
                && !globalIDs.contains(observation.globalID) {
            featureIDs.insert(observation.featureID)
            globalIDs.insert(observation.globalID)
            unique.append(observation)
        }
        let grouped = Dictionary(grouping: unique) {
            SegmentPair(current: $0.currentSegmentID, global: $0.globalSegmentID)
        }
        return grouped.values.flatMap(orderPreservingSequence)
    }

    private static func orderPreservingSequence(
        _ observations: [GroundGlobalPoseObservation]
    ) -> [GroundGlobalPoseObservation] {
        let ordered = observations.sorted { $0.imageX < $1.imageX }
        guard ordered.count > 1 else { return ordered }
        var counts = [Int](repeating: 1, count: ordered.count)
        var errors = ordered.map(\.descriptorError)
        var previous = [Int?](repeating: nil, count: ordered.count)
        for index in ordered.indices {
            for earlier in 0..<index
                where ordered[earlier].globalWorldX < ordered[index].globalWorldX {
                let proposedCount = counts[earlier] + 1
                let proposedError = errors[earlier] + ordered[index].descriptorError
                if proposedCount > counts[index]
                    || (proposedCount == counts[index] && proposedError < errors[index]) {
                    counts[index] = proposedCount
                    errors[index] = proposedError
                    previous[index] = earlier
                }
            }
        }
        guard var cursor = ordered.indices.max(by: {
            counts[$0] == counts[$1]
                ? errors[$0] > errors[$1]
                : counts[$0] < counts[$1]
        }) else { return [] }
        var sequence = [GroundGlobalPoseObservation]()
        while true {
            sequence.append(ordered[cursor])
            guard let prior = previous[cursor] else { break }
            cursor = prior
        }
        return Array(sequence.reversed())
    }

    private static func hasLineSequenceAnchor(
        _ observations: [GroundGlobalPoseObservation]
    ) -> Bool {
        Dictionary(grouping: observations) {
            SegmentPair(current: $0.currentSegmentID, global: $0.globalSegmentID)
        }.values.contains { group in
            group.count >= 3 && horizontalSpread(group) >= 32
        }
    }

    private static func weightedMean(_ observations: [GroundGlobalPoseObservation]) -> CGPoint {
        var x: CGFloat = 0
        var y: CGFloat = 0
        var weightSum: CGFloat = 0
        for observation in observations {
            let weight = 1 / (1 + observation.descriptorError / 8)
            x += observation.cameraPosition.x * weight
            y += observation.cameraPosition.y * weight
            weightSum += weight
        }
        return CGPoint(
            x: x / max(.leastNonzeroMagnitude, weightSum),
            y: y / max(.leastNonzeroMagnitude, weightSum)
        )
    }

    private static func horizontalSpread(_ observations: [GroundGlobalPoseObservation]) -> CGFloat {
        guard let minimum = observations.map(\.imageX).min(),
              let maximum = observations.map(\.imageX).max()
        else { return 0 }
        return maximum - minimum
    }
}

/// Tracks 16x12 floor patches, verifies them against the shared ground-motion
/// solve, and promotes only stable patches to a persistent world feature set.
/// Every current clean line is tiled at 16-pixel intervals without overlap.
final class GroundHypothesisTracker {
    static let featureWidth = 16
    static let featureHeight = 12
    /// Horizontal lattice spacing retained for existing atlas indexing.
    static let featureSize = featureWidth
    static let featurePixelCount = featureWidth * featureHeight
    private static let horizontalSearchRadius = 4
    private static let verticalSearchRadius = 3
    /// Paid only after the primary ground solver misses. Clean rows bound Y,
    /// and an evenly spread feature budget bounds the wider texture search.
    private static let recoveryHorizontalSearchRadius = 16
    private static let recoveryVerticalSearchRadius = 24
    private static let maximumRecoveryHorizontalSearchRadius = 64
    private static let maximumRecoveryVerticalSearchRadius = 48
    private static let recoveryFeatureLimit = 48
    private static let maximumPhotometricError: CGFloat = 64
    private static let verticalRefinementRadius = 3
    private static let promotionPhotometricError: CGFloat = 28
    private static let descriptorMatchThreshold: CGFloat = 18
    private static let evidenceRowTolerance = 5
    private static let segmentAssociationRowTolerance: CGFloat = 6
    private static let missingLineRejectionFrames = 5
    private static let missingCellRejectionFrames = 12
    private static let reliableCellRejectionFrames = 30
    private static let minimumRejectedRunLength = 2
    private static let maximumAmbiguousVerticalLineCorrection: CGFloat = 48
    private static let verticalLineConsensusTolerance: CGFloat = 2

    private struct ActiveFeature {
        var id: Int
        var segmentID: Int
        var sequenceIndex: Int
        var x: Int
        var y: Int
        var patch: [UInt8]
        var age: Int
        var groundScore: CGFloat
        var classification: GroundHypothesisClassification
        var motionResidual: CGFloat?
        var photometricError: CGFloat?
        var consecutiveErrorFrames: Int
        var consecutiveGroundFrames: Int
        var globalFeatureID: Int?
        var referenceValid: Bool
    }

    private struct GlobalFeature {
        var id: Int
        var segmentID: Int
        var sequenceIndex: Int
        var worldX: CGFloat
        var worldTopY: CGFloat
        var descriptor: [Int16]
        var matchingPatch: [UInt8]
        var referencePatch: [UInt8]
        var referenceOpacity: [UInt8]
        var observationCount: Int
        var lastSeenFrame: Int
    }

    private struct Match {
        var feature: ActiveFeature
        var x: Int
        var y: Int
        var photometricError: CGFloat
        var imageTranslation: CGVector
        var detectedLineRow: Int
    }

    private struct RecoveryAttempt {
        var matches: [Match]
        var fit: GroundTranslationFit?
        var translationsTested: Int
        var bestInlierCount: Int
        var ambiguous: Bool
    }

    private struct GroundSegment {
        var id: Int
        var originWorldX: CGFloat
        var minimumWorldX: CGFloat
        var maximumWorldX: CGFloat
        var worldTopY: CGFloat
        var cellIDs: [Int: Int]
        var cellEvidence: [Int: CellEvidence]
    }

    private struct CellEvidence {
        var visibleObservations: Int
        var supportingObservations: Int
        var consecutiveMisses: Int
        var referencePatch: [UInt8]
        var referenceOpacity: [UInt8]
    }

    private struct PendingLine {
        var id: Int
        var minimumWorldX: CGFloat
        var maximumWorldX: CGFloat
        var worldTopY: CGFloat
        var consecutiveObservations: Int
        var lastSeenFrame: Int
    }

    private struct PendingLinePatch {
        let worldX: CGFloat
        let pixels: [UInt8]
    }

    /// Temporary texture evidence for a line that is not part of the atlas.
    /// It must follow the already-solved camera motion before it may become a
    /// persistent line; screen-fixed foreground cannot pass this test.
    private struct LineMotionCandidate {
        var minimumWorldX: CGFloat
        var maximumWorldX: CGFloat
        let worldTopY: CGFloat
        var lastSeenFrame: Int
        var referenceCamera: CGPoint
        var referencePatches: [PendingLinePatch]
        var successfulMotionChecks: Int
        var failedMotionChecks: Int
        var accumulatedCameraTravel: CGFloat
        var verified: Bool
    }

    private struct CellProjection {
        var id: Int
        var segmentID: Int
        var sequenceIndex: Int
        var x: Int
        var y: Int
        var worldX: CGFloat
        var worldY: CGFloat
        var hasCurrentEvidence: Bool
    }

    /// Value snapshot for explicitly enabled raw-pixel diagnostics. Called
    /// on the tracker owner; JSON encoding and disk writes happen elsewhere.
    func auditGlobalFeatureBank() -> [String: Any] {
        ["camera": [Double(localCameraPosition.x), Double(localCameraPosition.y)],
         "anchorValid": globalAnchorValid, "frameIndex": frameIndex,
         "canonicalIDs": canonicalAnchorFeatures.keys.sorted(),
         "firstViewIDs": canonicalFirstViewIDs.sorted(),
         "features": relocalizationFeatures.map { feature in
            ["id": feature.id, "segment": feature.segmentID,
             "sequence": feature.sequenceIndex,
             "x": Double(feature.worldX), "y": Double(feature.worldTopY),
             "descriptor": feature.descriptor.map(Int.init),
             "pixels": Data(feature.matchingPatch).base64EncodedString()]
                as [String: Any]
         }]
    }

    private var active = [ActiveFeature]()
    private var groundSegments = [GroundSegment]()
    private var pendingLines = [PendingLine]()
    private var lineMotionCandidates = [LineMotionCandidate]()
    private var globalFeatures = [Int: GlobalFeature]() { didSet { cachedRelocalizationFeatures = nil } }
    /// Immutable first-observation cohorts. Visible line merging and duplicate
    /// cleanup must never rewrite the place signature used for loop closure.
    private var canonicalAnchorFeatures = [Int: GlobalFeature]() { didSet { cachedRelocalizationFeatures = nil } }
    private var canonicalFirstViewIDs = Set<Int>()
    private var canonicalViewOrigins = [Int: CGPoint]()
    private var closedCanonicalViews = Set<Int>()
    /// Descriptors from formerly confirmed lines that were later rejected by
    /// visible-map cleanup. They remain hidden from atlas review and local
    /// projection, but preserve place memory for strict loop relocalization.
    private var retiredGlobalFeatures = [Int: GlobalFeature]() { didSet { cachedRelocalizationFeatures = nil } }
    private var cachedRelocalizationFeatures: [GlobalFeature]?
    private var nextID = 1
    private var nextSegmentID = 1
    private var nextPendingLineID = 1
    private var nextGlobalID = 1
    private var frameIndex = 0
    private var frameSize = CGSize.zero
    private var lastImageTranslation = CGVector.zero
    private var localCameraPosition = CGPoint.zero
    private var globalAnchorValid = true
    private(set) var latestStabilizedFloorLines = [CleanFloorLine]()
    /// Atlas-wide X phase. Every ground-cell left edge is this value plus an
    /// integer multiple of featureSize, across every platform and line ID.
    private var globalTilePhaseX: CGFloat?
    private let cameraSolver: GroundCameraSolver
    private let linePresence: GroundLinePresence
    private var lastTimestamp: Double?
    private var lastGlobalSearchTimestamp: Double?
    private var lastAlternativeGlobalSearchTimestamp: Double?
    private var lastCanonicalPhaseSearchTimestamp: Double?
    private var latestPoseVerified = false
    private var localTextureSupport = 0
    private var localTextureError: CGFloat?
    private var localMatchSource: String?
    private var lastProvisionalTextureTimestamp: Double?
    private var provisionalPromotionStartedAt: Double?
    private var provisionalPromotionAllowed = false
    private var consecutiveStrongProvisionalMatches = 0
    private var localReferenceTimestamp: Double?
    private var localReferencePosition: CGVector?
    private var localMatchedImageDisplacement: CGVector?
    private var latestLineReviews = [GroundLinePresenceReview]()
    private var consecutiveTrackingMisses = 0
    private let traceEnabled = ProcessInfo.processInfo.arguments.contains("--trace-ground-tracking")
    private let traceLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "ground-trace"
    )

    private struct SearchResult {
        let fit: GroundGlobalPoseFit?
        let timestamp: Double
        let localPose: CGPoint
        let duration: Double
    }
    private let backgroundGlobalSearch: Bool
    private let globalSearchWorker = BoundedBackgroundSearch<SearchResult>()
    private var correctionConfirmation = GroundCorrectionConfirmation()
    private var lastLocalFailureTimestamp = -Double.infinity
    private var bootstrapFailureStartedAt: Double?
    private(set) var completedGlobalSearchMilliseconds: Double?

    init(minimumObservationSeconds: Double = 2, backgroundGlobalSearch: Bool = false,
         confirmedLossGraceSeconds: Double = 0,
         productionCameraSolver: Bool = false) {
        self.backgroundGlobalSearch = backgroundGlobalSearch
        cameraSolver = productionCameraSolver
            ? GroundCameraSolver(
                keyframeSubpixelEnabled: true,
                calibratedMotionEnabled: true
            )
            : GroundCameraSolver()
        linePresence = GroundLinePresence(minimumObservationSeconds: minimumObservationSeconds,
            confirmedLossGraceSeconds: confirmedLossGraceSeconds)
    }

    func reset(keepingGlobalFeatures: Bool = false) {
        if traceEnabled {
            traceLog.notice(
                "reset keepingGlobalFeatures=\(keepingGlobalFeatures, privacy: .public)"
            )
        }
        cameraSolver.reset()
        globalSearchWorker.invalidate()
        correctionConfirmation.reset()
        lastLocalFailureTimestamp = -Double.infinity
        bootstrapFailureStartedAt = nil
        lastTimestamp = nil
        lastGlobalSearchTimestamp = nil
        lastAlternativeGlobalSearchTimestamp = nil
        lastCanonicalPhaseSearchTimestamp = nil
        latestPoseVerified = false
        lastProvisionalTextureTimestamp = nil
        provisionalPromotionStartedAt = nil
        provisionalPromotionAllowed = false
        consecutiveStrongProvisionalMatches = 0
        latestLineReviews = []
        consecutiveTrackingMisses = 0
        if !keepingGlobalFeatures { linePresence.reset() }
        active.removeAll(keepingCapacity: true)
        pendingLines.removeAll(keepingCapacity: true)
        lineMotionCandidates.removeAll(keepingCapacity: true)
        latestStabilizedFloorLines.removeAll(keepingCapacity: true)
        frameSize = .zero
        lastImageTranslation = .zero
        if !keepingGlobalFeatures {
            groundSegments.removeAll(keepingCapacity: false)
            globalFeatures.removeAll(keepingCapacity: false)
            canonicalAnchorFeatures.removeAll(keepingCapacity: false)
            canonicalFirstViewIDs.removeAll(keepingCapacity: false)
            canonicalViewOrigins.removeAll(keepingCapacity: false)
            closedCanonicalViews.removeAll(keepingCapacity: false)
            retiredGlobalFeatures.removeAll(keepingCapacity: false)
            nextID = 1
            nextSegmentID = 1
            nextPendingLineID = 1
            nextGlobalID = 1
            frameIndex = 0
            localCameraPosition = .zero
            globalAnchorValid = true
            globalTilePhaseX = nil
        } else if relocalizationFeatures.isEmpty {
            localCameraPosition = .zero
            globalAnchorValid = true
        } else {
            // Tracking may resume after the camera moved while this diagnostic
            // was disabled. Do not add features at a stale world pose.
            globalAnchorValid = false
        }
    }

    /// Reconciles the private ground odometry epoch with the camera pose that
    /// was actually published while the floor was absent. A stale local line
    /// match must not snap the atlas back to the last ground-only pose.
    func reanchorLocalCamera(
        to position: CGPoint,
        frame: CGImage? = nil,
        floorLines: [CleanFloorLine] = [],
        timestamp: Double? = nil,
        sourceLuma: [UInt8]? = nil,
        protectedOcclusions: [CGRect] = [],
        allowsProvisionalPromotion: Bool = false
    ) {
        guard position.x.isFinite, position.y.isFinite else { return }
        bootstrapFailureStartedAt = nil
        let correction = CGVector(dx: position.x - localCameraPosition.x,
                                  dy: position.y - localCameraPosition.y)
        // This is a local-odometry handoff, not a change of room identity.
        // Keep an already running persistent-place search and its first
        // correction vote. The immutable search result is still checked
        // against current pixels before publication; cancelling it here lets
        // repeated floorless fallback starve relocalization indefinitely.
        localCameraPosition = position
        lastImageTranslation = .zero
        latestPoseVerified = false
        active.removeAll(keepingCapacity: true)
        pendingLines.removeAll(keepingCapacity: true)
        lineMotionCandidates.removeAll(keepingCapacity: true)
        globalAnchorValid = true
        if let frame, let timestamp, !floorLines.isEmpty,
           let pixels = sourceLuma ?? GroundPixels.luma(frame, width: frame.width, height: frame.height) {
            cameraSolver.followMeasuredCamera(correction: correction,
                pixels: pixels, width: frame.width, height: frame.height,
                lines: floorLines, timestamp: timestamp, exclusions: protectedOcclusions,
                preservesTrustedHistory: !groundSegments.isEmpty
                    || !relocalizationFeatures.isEmpty)
            if cameraSolver.hasProvisionalReference {
                lastProvisionalTextureTimestamp = timestamp
                provisionalPromotionStartedAt = timestamp
                provisionalPromotionAllowed = allowsProvisionalPromotion
                consecutiveStrongProvisionalMatches = 0
            }
        } else {
            cameraSolver.reset()
            lastProvisionalTextureTimestamp = nil
            provisionalPromotionStartedAt = nil
            provisionalPromotionAllowed = false
            consecutiveStrongProvisionalMatches = 0
        }
    }

    func update(
        frame: CGImage,
        floorLines: [CleanFloorLine],
        lineSeparation: Int = 40,
        occlusionMergeGap: Int = 300,
        timestamp: Double? = nil,
        sourceLuma: [UInt8]? = nil,
        protectedOcclusions: [CGRect] = [],
        placementValidator: ((GroundPlacementRecovery.Proposal) -> Bool)? = nil
    ) -> GroundHypothesisTrackingResult {
        localTextureSupport = 0
        localTextureError = nil
        localMatchSource = nil
        localReferenceTimestamp = nil
        localReferencePosition = nil
        localMatchedImageDisplacement = nil
        completedGlobalSearchMilliseconds = nil
        let width = frame.width
        let height = frame.height
        guard width >= Self.featureWidth, height >= Self.featureHeight,
              let luma = sourceLuma ?? GroundPixels.luma(frame, width: frame.width, height: frame.height), luma.count == width * height
        else {
            reset(keepingGlobalFeatures: true)
            return result(cameraFit: nil, globalFit: nil, globalCorrection: nil)
        }
        let newSize = CGSize(width: width, height: height)
        if frameSize != .zero, frameSize != newSize {
            // Pixel-space world coordinates and descriptors are scale-specific.
            reset()
        }
        frameSize = newSize
        frameIndex += 1

        let now = timestamp ?? ((lastTimestamp ?? 0) + 1.0 / 60)
        lastTimestamp = now
        var solution = cameraSolver.solve(
            pixels: luma, width: width, height: height, lines: floorLines,
            // World-space negative line evidence governs map admission below.
            // It cannot be projected with the PREVIOUS pose into the CURRENT
            // image and used as an exclusion while solving that unknown pose.
            timestamp: now, exclusions: protectedOcclusions
        )
        // Before any ground or landmark has been established, a stale first
        // image is only a bootstrap candidate. Replace it after sustained
        // failure, then require the ordinary presence/stability gates again.
        // Once evidence exists, recovery must preserve that world identity.
        if solution == nil, groundSegments.isEmpty, relocalizationFeatures.isEmpty,
           active.isEmpty, !floorLines.isEmpty {
            if let began = bootstrapFailureStartedAt, now - began >= 0.25 {
                cameraSolver.reset()
                linePresence.reset()
                pendingLines.removeAll(keepingCapacity: true)
                lineMotionCandidates.removeAll(keepingCapacity: true)
                latestLineReviews = []
                lastImageTranslation = .zero
                solution = cameraSolver.solve(pixels: luma, width: width, height: height,
                    lines: floorLines, timestamp: now, exclusions: protectedOcclusions)
                bootstrapFailureStartedAt = nil
                if traceEnabled {
                    traceLog.notice("bootstrap-reference-reseed t=\(now, privacy: .public)")
                }
            } else if bootstrapFailureStartedAt == nil {
                bootstrapFailureStartedAt = now
            }
        } else {
            bootstrapFailureStartedAt = nil
        }
        if solution == nil { lastLocalFailureTimestamp = now }
        if let solution {
            lastImageTranslation = solution.imageTranslation
            localMatchSource = solution.matchSource
            localReferenceTimestamp = solution.referenceTimestamp
            localReferencePosition = solution.referencePosition
            localMatchedImageDisplacement = solution.matchedImageDisplacement
            if !solution.referenceTrusted {
                lastProvisionalTextureTimestamp = now
                if provisionalPromotionAllowed,
                   solution.matchSource == "provisional",
                   GroundReferencePromotionPolicy.permits(
                       support: solution.support, error: solution.error
                   ) {
                    consecutiveStrongProvisionalMatches += 1
                } else {
                    consecutiveStrongProvisionalMatches = 0
                }
            }
        }
        let previousActive = active
        let matchCandidates = solution == nil
            ? recoveryFeatureCandidates(from: active)
            : active
        let recoveryAttempt = solution == nil ? recoveryConsensus(
            features: matchCandidates,
            in: luma,
            width: width,
            height: height,
            floorLines: floorLines,
            exclusions: protectedOcclusions,
            horizontalSearchRadius: min(
                Self.maximumRecoveryHorizontalSearchRadius,
                Self.recoveryHorizontalSearchRadius + consecutiveTrackingMisses * 4
            ),
            verticalSearchRadius: min(
                Self.maximumRecoveryVerticalSearchRadius,
                Self.recoveryVerticalSearchRadius + consecutiveTrackingMisses * 2
            )
        ) : nil
        let rawMatches = recoveryAttempt?.matches ?? matchCandidates.compactMap { feature -> Match? in
            guard feature.referenceValid,
                  descriptorEnergy(normalizedDescriptor(feature.patch)) >= 6
            else { return nil }
            guard let match = bestMatch(
                for: feature,
                in: luma,
                width: width,
                height: height,
                floorLines: floorLines
            ), match.photometricError <= Self.maximumPhotometricError else { return nil }
            let rect = CGRect(x: match.x, y: match.y,
                width: Self.featureWidth, height: Self.featureHeight)
            guard !protectedOcclusions.contains(where: { $0.intersects(rect) }) else { return nil }
            return match
        }
        let matches = recoveryAttempt == nil
            ? orderPreservingMatches(rawMatches)
            : rawMatches
        let motions = matches.map {
            GroundFeatureMotion(
                id: $0.feature.id,
                imageTranslation: $0.imageTranslation,
                photometricError: $0.photometricError
            )
        }
        var cameraFit: GroundTranslationFit?
        if let solve = solution, !cameraSolver.seeded {
            let inliers = motions.filter {
                hypot($0.imageTranslation.dx - solve.imageTranslation.dx,
                      $0.imageTranslation.dy - solve.imageTranslation.dy) <= 2.5
            }
            cameraFit = GroundTranslationFit(
                imageTranslation: solve.imageTranslation,
                inlierIDs: Set(inliers.map(\.id)), residualRMS: solve.error
            )
        } else if solution == nil,
                  let recovered = recoveryAttempt?.fit {
            let recoveredMatches = matches.filter {
                recovered.inlierIDs.contains($0.feature.id)
            }
            let spread = (recoveredMatches.map(\.x).max() ?? 0)
                - (recoveredMatches.map(\.x).min() ?? 0)
            let photometricMean = recoveredMatches.reduce(CGFloat.zero) {
                $0 + $1.photometricError
            } / CGFloat(max(1, recoveredMatches.count))
            let acceleration = hypot(
                recovered.imageTranslation.dx - lastImageTranslation.dx,
                recovered.imageTranslation.dy - lastImageTranslation.dy
            )
            if recovered.residualRMS <= 1.5, spread >= 48,
               photometricMean <= Self.promotionPhotometricError,
               acceleration <= 24 {
                cameraSolver.acceptFeatureRecovery(
                    pixels: luma, width: width, height: height,
                    lines: floorLines, timestamp: now,
                    exclusions: protectedOcclusions,
                    imageTranslation: recovered.imageTranslation
                )
                lastImageTranslation = recovered.imageTranslation
                solution = GroundCameraSolver.Solution(
                    imageTranslation: recovered.imageTranslation,
                    support: recovered.inlierIDs.count,
                    // Spatial consensus residual and pixel mismatch use
                    // different units. Publish the measured texture cost.
                    error: photometricMean
                )
                cameraFit = recovered
            } else {
                if traceEnabled {
                    traceLog.info(
                        "feature-recovery-rejected t=\(now, privacy: .public) candidates=\(matchCandidates.count, privacy: .public) raw=\(rawMatches.count, privacy: .public) ordered=\(matches.count, privacy: .public) inliers=\(recovered.inlierIDs.count, privacy: .public) residual=\(recovered.residualRMS, privacy: .public) spread=\(spread, privacy: .public) photo=\(photometricMean, privacy: .public) accel=\(acceleration, privacy: .public)"
                    )
                }
                cameraFit = nil
            }
        } else {
            if solution == nil, traceEnabled {
                traceLog.info(
                    "feature-recovery-no-fit t=\(now, privacy: .public) candidates=\(matchCandidates.count, privacy: .public) translations=\(recoveryAttempt?.translationsTested ?? 0, privacy: .public) best-inliers=\(recoveryAttempt?.bestInlierCount ?? 0, privacy: .public) ambiguous=\(recoveryAttempt?.ambiguous ?? false, privacy: .public)"
                )
            }
            cameraFit = nil
        }
        // Local tracking can be outside its bounded search radius precisely
        // when an old ground view returns. Run the phase-independent absolute
        // matcher before giving up; the former early return made loop closure
        // unreachable whenever it was most needed.
        var recoveryGlobalFit: GroundGlobalPoseFit?
        var recoveryGlobalCorrection: CGVector?
        if solution == nil || solution?.referenceTrusted == false {
            let shouldSearchGlobally = lastGlobalSearchTimestamp.map {
                now - $0 >= 0.25
            } ?? true
            if shouldSearchGlobally && !backgroundGlobalSearch {
                let boundedScan = phaseIndependentGlobalFit(
                    luma: luma,
                    width: width,
                    height: height,
                    floorLines: floorLines,
                    exclusions: protectedOcclusions
                )
                let boundedFit = verifiedGlobalFit(
                    boundedScan,
                    luma: luma,
                    width: width,
                    height: height,
                    floorLines: floorLines,
                    exclusions: protectedOcclusions
                )
                // A long route can accumulate more than the bounded 64-pixel
                // Y error before the original ground returns. At that point
                // the local solver has no solution, so the normal canonical
                // challenge below is unreachable. Search only the oldest
                // persistent Line IDs without a predicted-row gate as a
                // bounded recovery fallback. Full-strip pixel verification
                // still has to accept the absolute pose before it can move
                // the camera.
                let shouldSearchCanonical = lastCanonicalPhaseSearchTimestamp.map {
                        now - $0 >= 0.75
                    } ?? true
                var canonicalFit: GroundGlobalPoseFit?
                if shouldSearchCanonical {
                    lastCanonicalPhaseSearchTimestamp = now
                    let canonicalIDs = Set(
                        relocalizationFeatures.map(\.segmentID).sorted().prefix(4)
                    )
                    let canonicalFeatureIDs = canonicalAnchorFeatureIDs(
                        segmentIDs: canonicalIDs
                    )
                    canonicalFit = canonicalIDs.isEmpty ? nil
                        : verifiedCanonicalPhaseFit(
                            luma: luma,
                            width: width,
                            height: height,
                            floorLines: floorLines,
                            exclusions: protectedOcclusions,
                            allowedSegmentIDs: canonicalIDs,
                            allowedFeatureIDs: canonicalFeatureIDs,
                            maximumPredictedRowDistance: nil
                        )
                }
                // A drift-created duplicate can verify the current pose while
                // an older canonical strip independently identifies the true
                // return. Prefer the oldest verified identity; support and
                // descriptor error break ties within the same Line ID.
                recoveryGlobalFit = [boundedFit, canonicalFit]
                    .compactMap { $0 }
                    .min { left, right in
                        let leftID = left.inliers.map(\.globalSegmentID).min() ?? .max
                        let rightID = right.inliers.map(\.globalSegmentID).min() ?? .max
                        if leftID != rightID { return leftID < rightID }
                        if left.inliers.count != right.inliers.count {
                            return left.inliers.count > right.inliers.count
                        }
                        return meanDescriptorError(left) < meanDescriptorError(right)
                    }
                lastGlobalSearchTimestamp = now
            }
            if backgroundGlobalSearch {
                recoveryGlobalFit = backgroundFit(luma: luma, width: width, height: height,
                    floorLines: floorLines, exclusions: protectedOcclusions, timestamp: now,
                    recovering: true)
            }
            if let fit = recoveryGlobalFit {
                let position = CGPoint(
                    x: abs(fit.cameraPosition.x - fit.cameraPosition.x.rounded())
                        < 0.000_001 ? fit.cameraPosition.x.rounded() : fit.cameraPosition.x,
                    y: abs(fit.cameraPosition.y - fit.cameraPosition.y.rounded())
                        < 0.000_001 ? fit.cameraPosition.y.rounded() : fit.cameraPosition.y
                )
                let correction = CGVector(
                    dx: position.x - localCameraPosition.x,
                    dy: position.y - localCameraPosition.y
                )
                globalSearchWorker.invalidate()
                correctionConfirmation.reset()
                cameraSolver.applyGlobalCorrection(
                    correction,
                    currentPixels: luma,
                    lines: floorLines,
                    timestamp: now,
                    exclusions: protectedOcclusions
                )
                localCameraPosition = position
                globalAnchorValid = true
                applyGlobalFit(fit)
                reassociateSegments(using: fit)
                recoveryGlobalCorrection = correction
                // Absolute texture evidence establishes this frame's pose and
                // the call above seeds the next local solve at that pose.
                solution = GroundCameraSolver.Solution(
                    imageTranslation: .zero,
                    support: fit.featureIDs.count,
                    error: 0
                )
                cameraFit = GroundTranslationFit(
                    imageTranslation: .zero,
                    inlierIDs: fit.featureIDs,
                    residualRMS: 0
                )
            }
        }
        guard solution != nil else {
            // Never refresh atlas patches or run pose corrections on pixels
            // whose world location is unknown. Keep the last accepted map.
            latestPoseVerified = false
            linePresence.update(lines: [], camera: localCameraPosition,
                width: width, height: height, timestamp: now,
                poseVerified: false, exclusions: protectedOcclusions)
            latestLineReviews = linePresence.reviews(
                camera: localCameraPosition, width: width, height: height)
            consecutiveTrackingMisses += 1
            return result(cameraFit: nil, globalFit: nil, globalCorrection: nil)
        }
        consecutiveTrackingMisses = 0
        localTextureSupport = solution?.support ?? 0
        localTextureError = solution?.error
        if let cameraFit {
            localCameraPosition.x -= cameraFit.imageTranslation.dx
            localCameraPosition.y += cameraFit.imageTranslation.dy
        }
        let stabilizedFloorLines = stabilizeFloorLines(
            floorLines, matches: matches, cameraFit: cameraFit
        )
        if var provisional = solution,
           !provisional.referenceTrusted,
           let started = provisionalPromotionStartedAt,
           provisionalPromotionAllowed,
           GroundProvisionalPromotionPolicy.permits(
               elapsed: now - started,
               consecutiveMatches: consecutiveStrongProvisionalMatches,
               support: provisional.support,
               error: provisional.error
           ) {
            cameraSolver.promoteProvisionalReference(
                pixels: luma, width: width, height: height,
                lines: stabilizedFloorLines, timestamp: now,
                exclusions: protectedOcclusions
            )
            provisional.referenceTrusted = true
            solution = provisional
        }
        // A loop challenge needs an observation independent of local pose.
        // Locally refined rows can inherit the drift that it must correct.
        let globalFloorLines = floorLines
        if traceEnabled, backgroundGlobalSearch,
           lastGlobalSearchTimestamp.map({ now - $0 >= 0.25 }) ?? true {
            let rawRows = floorLines.map { String($0.row) }.joined(separator: ",")
            let localRows = stabilizedFloorLines.map { String($0.row) }.joined(separator: ",")
            traceLog.info("global-search-rows t=\(now, privacy: .public) raw=\(rawRows, privacy: .public) local=\(localRows, privacy: .public) independent=true")
        }
        let tracked = matches.compactMap {
            updatedFeature(from: $0, fit: cameraFit, luma: luma, width: width)
        }
        // Match existing geometry before committing line votes or reference pixels.
        active = projectGroundSegments(floorLines: stabilizedFloorLines, tracked: tracked,
            previous: previousActive, luma: luma, width: width, height: height,
            protectedOcclusions: protectedOcclusions)
        let shouldSearchGlobally = !backgroundGlobalSearch && recoveryGlobalFit == nil
            && (lastGlobalSearchTimestamp.map { now - $0 >= 0.25 } ?? true)
        let observations = shouldSearchGlobally ? globalMatchObservations() : []
        let descriptorFit = shouldSearchGlobally ? GroundGlobalPoseRANSAC.fit(observations) : nil
        // A direct proposal can exist but fail full-strip verification. It
        // must not suppress the all-phase search: drift-created active tiles
        // tend to nominate their nearby duplicate instead of the old origin.
        let directGlobalFit = shouldSearchGlobally ? verifiedGlobalFit(descriptorFit,
            luma: luma, width: width, height: height, floorLines: globalFloorLines,
            exclusions: protectedOcclusions) : nil
        let directSegmentIDs = Set(
            directGlobalFit?.inliers.map(\.globalSegmentID) ?? []
        )
        // Reuse the already-computed descriptor observations to challenge a
        // locally self-consistent duplicate Line ID. This is much cheaper than
        // rescanning every image X phase, and lets an older ordered strip
        // propose its absolute pose even when the duplicate fits perfectly.
        let shouldChallengeAlternative = shouldSearchGlobally
            && !directSegmentIDs.isEmpty
            && (lastAlternativeGlobalSearchTimestamp.map {
                now - $0 >= 1
            } ?? true)
        if shouldChallengeAlternative {
            lastAlternativeGlobalSearchTimestamp = now
        }
        var alternativeGlobalFit: GroundGlobalPoseFit?
        if shouldChallengeAlternative {
            let alternatives = Dictionary(grouping: observations.filter {
                !directSegmentIDs.contains($0.globalSegmentID)
            }, by: \.globalSegmentID)
            // A loop closure establishes canonical identity: try older Line
            // IDs first instead of allowing many recent drift duplicates to
            // outvote the original in one mixed RANSAC pool. Wide-strip
            // verification still has to accept the proposed absolute pose.
            for segmentID in alternatives.keys.sorted().prefix(4) {
                guard let segmentObservations = alternatives[segmentID],
                      let fit = GroundGlobalPoseRANSAC.fit(segmentObservations),
                      let verified = verifiedGlobalFit(
                        fit,
                        luma: luma, width: width, height: height,
                        floorLines: globalFloorLines,
                        exclusions: protectedOcclusions
                      ) else { continue }
                alternativeGlobalFit = verified
                break
            }
        }
        let storedSegmentIDs = Set(relocalizationFeatures.map(\.segmentID))
        let canonicalSegmentIDs = Set(storedSegmentIDs.sorted().prefix(4))
        let canonicalFeatureIDs = canonicalAnchorFeatureIDs(
            segmentIDs: canonicalSegmentIDs
        )
        let shouldScanCanonicalPhase = shouldSearchGlobally
            && !canonicalSegmentIDs.isEmpty
            && (lastCanonicalPhaseSearchTimestamp.map {
                now - $0 >= 2
            } ?? true)
        if shouldScanCanonicalPhase {
            lastCanonicalPhaseSearchTimestamp = now
        }
        // Active cells inherit the current (possibly drifted) 16-pixel phase.
        // Search every image X phase against only the oldest canonical lines,
        // with no predicted-Y gate, so a true loop closure can recover both a
        // different lattice phase and large accumulated vertical drift.
        let canonicalGlobalFit = shouldScanCanonicalPhase
            ? verifiedCanonicalPhaseFit(
                luma: luma, width: width, height: height,
                floorLines: globalFloorLines,
                exclusions: protectedOcclusions,
                allowedSegmentIDs: canonicalSegmentIDs,
                allowedFeatureIDs: canonicalFeatureIDs,
                maximumPredictedRowDistance: nil
            ) : nil
        // Active tiles are projected on the current 16-pixel world lattice.
        // If odometry has drifted to another phase, their descriptors cannot
        // nominate the original cells. Scan every image-space lattice phase
        // after the cheap path fails verification, then verify that proposal.
        let scannedFit = shouldSearchGlobally
            && directGlobalFit == nil && alternativeGlobalFit == nil
            && canonicalGlobalFit == nil
            ? phaseIndependentGlobalFit(
                luma: luma, width: width, height: height,
                floorLines: globalFloorLines,
                exclusions: protectedOcclusions
            ) : nil
        if shouldSearchGlobally { lastGlobalSearchTimestamp = now }
        let scannedGlobalFit = shouldSearchGlobally
            && directGlobalFit == nil && alternativeGlobalFit == nil
            && canonicalGlobalFit == nil
            ? verifiedGlobalFit(scannedFit,
            luma: luma, width: width, height: height, floorLines: globalFloorLines,
            exclusions: protectedOcclusions) : nil
        // When two independently verified poses explain the pixels, retain
        // the oldest persistent line as canonical. Newer IDs can be artifacts
        // created while odometry was drifting around the same room.
        let verifiedFits = [
            directGlobalFit, alternativeGlobalFit,
            canonicalGlobalFit, scannedGlobalFit,
        ]
            .compactMap { $0 }
        let canonicalFit = verifiedFits.min { left, right in
            let leftID = left.inliers.map(\.globalSegmentID).min() ?? .max
            let rightID = right.inliers.map(\.globalSegmentID).min() ?? .max
            if leftID != rightID { return leftID < rightID }
            if left.inliers.count != right.inliers.count {
                return left.inliers.count > right.inliers.count
            }
            return meanDescriptorError(left) < meanDescriptorError(right)
        }
        let globalFit = recoveryGlobalFit ?? canonicalFit ?? (backgroundGlobalSearch
            ? backgroundFit(luma: luma, width: width, height: height,
                floorLines: globalFloorLines, exclusions: protectedOcclusions,
                timestamp: now, recovering: false) : nil)
        var globalCorrection = recoveryGlobalCorrection
        if recoveryGlobalFit == nil, let globalFit {
            func snapRoundoff(_ x: CGFloat) -> CGFloat {
                abs(x - x.rounded()) < 0.000_001 ? x.rounded() : x
            }
            let position = CGPoint(x: snapRoundoff(globalFit.cameraPosition.x),
                                   y: snapRoundoff(globalFit.cameraPosition.y))
            globalCorrection = CGVector(dx: position.x - localCameraPosition.x,
                                        dy: position.y - localCameraPosition.y)
            globalSearchWorker.invalidate()
            correctionConfirmation.reset()
            cameraSolver.applyGlobalCorrection(
                globalCorrection!,
                currentPixels: luma,
                lines: stabilizedFloorLines,
                timestamp: now,
                exclusions: protectedOcclusions
            )
            localCameraPosition = position
            globalAnchorValid = true
            applyGlobalFit(globalFit)
            reassociateSegments(using: globalFit)
        }
        latestPoseVerified = solution?.referenceTrusted == true && globalAnchorValid
        if !latestPoseVerified {
            active = previousActive
            // Provisional texture can carry relative motion through a ground
            // dropout, but its fallback-derived absolute coordinate cannot
            // modify floor presence, reference patches, or atlas geometry.
            linePresence.update(lines: [], camera: localCameraPosition,
                width: width, height: height, timestamp: now,
                poseVerified: false, exclusions: protectedOcclusions)
            latestLineReviews = linePresence.reviews(
                camera: localCameraPosition, width: width, height: height)
            return result(cameraFit: cameraFit, globalFit: globalFit,
                          globalCorrection: globalCorrection)
        }
        if latestPoseVerified, placementValidator?(GroundPlacementRecovery.Proposal(
            position: localCameraPosition, textureSupport: localTextureSupport,
            textureError: localTextureError, inlierCount: cameraFit?.inlierIDs.count ?? 0,
            globalMatchCount: globalFit?.featureIDs.count ?? 0,
            hasGlobalCorrection: globalCorrection != nil,
            distinctFrame: cameraSolver.distinctFrame,
            hasEstablishedGround: !groundSegments.isEmpty || !relocalizationFeatures.isEmpty)) == false {
            latestPoseVerified = false
            cameraSolver.deferReferenceUpdates(at: now)
            active = previousActive
            // Preserve the accepted line ledger and pixels until the outer
            // placement policy verifies this origin. Missing evidence cannot
            // erase floor or teach a rejected position to the feature bank.
            linePresence.update(lines: [], camera: localCameraPosition,
                width: width, height: height, timestamp: now,
                poseVerified: false, exclusions: protectedOcclusions)
            latestLineReviews = linePresence.reviews(
                camera: localCameraPosition, width: width, height: height)
            return result(cameraFit: cameraFit, globalFit: globalFit,
                          globalCorrection: globalCorrection)
        }
        provisionalPromotionStartedAt = nil
        provisionalPromotionAllowed = false
        consecutiveStrongProvisionalMatches = 0
        let admissionLines = atlasAdmissionLines(
            from: preservingKnownFloorGaps(stabilizedFloorLines),
            luma: luma,
            width: width,
            height: height,
            lineSeparation: lineSeparation,
            protectedOcclusions: protectedOcclusions
        )
        linePresence.update(
            lines: admissionLines, camera: localCameraPosition,
            width: width, height: height, timestamp: now,
            poseVerified: latestPoseVerified, exclusions: protectedOcclusions
        )
        latestLineReviews = linePresence.reviews(
            camera: localCameraPosition, width: width, height: height
        )
        let confirmedLines = latestLineReviews.filter { $0.state == .confirmed }.map { review in
            CleanFloorLine(row: review.line.row, xRange: review.line.xRange,
                evidenceRanges: admissionLines.filter {
                    abs($0.row - review.line.row) <= 4
                }.flatMap(\.evidenceRanges))
        }
        latestStabilizedFloorLines = confirmedLines
        let rejectedSegments = Set(groundSegments.filter {
            linePresence.state(minimumX: $0.minimumWorldX, maximumX: $0.maximumWorldX,
                               worldY: $0.worldTopY) == .rejected
        }.map(\.id))
        for (id, feature) in globalFeatures
            where rejectedSegments.contains(feature.segmentID) {
            retiredGlobalFeatures[id] = feature
        }
        groundSegments.removeAll { rejectedSegments.contains($0.id) }
        active.removeAll { rejectedSegments.contains($0.segmentID) }
        globalFeatures = globalFeatures.filter { !rejectedSegments.contains($0.value.segmentID) }
        // Row identity was already checked by spaced texture matches. There
        // is no second overlap-only vertical correction.
        let verticalLineCorrection: CGFloat? = nil

        if latestPoseVerified {
            integrateGroundSegments(
                confirmedLines,
                lineSeparation: lineSeparation,
                occlusionMergeGap: occlusionMergeGap,
                width: width,
                height: height,
                trackedFeatureIDs: cameraFit?.inlierIDs ?? [],
                // Ground texture has verified the row identity and pose.
                cameraPoseConfirmed: latestPoseVerified,
                protectedOcclusions: protectedOcclusions
            )
        }
        active = projectGroundSegments(
            floorLines: confirmedLines,
            tracked: active,
            previous: previousActive,
            luma: luma,
            width: width,
            height: height,
            protectedOcclusions: protectedOcclusions
        )
        recordReferencePatches()

        promoteVerifiedFeatures()
        return result(
            cameraFit: cameraFit,
            globalFit: globalFit,
            globalCorrection: globalCorrection,
            verticalLineCorrection: verticalLineCorrection
        )
    }

    static func overlayImage(
        for result: GroundHypothesisTrackingResult,
        width: Int,
        height: Int
    ) -> CGImage? {
        guard width > 0, height > 0 else { return nil }
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for feature in result.features {
            let color: (UInt8, UInt8, UInt8, UInt8)
            switch feature.classification {
            case .candidate:
                color = (0, 200, 255, 255)
            case .groundPlane:
                color = (70, 255, 120, 255)
            case .globalMatch:
                color = (190, 80, 255, 255)
            case .occluded:
                color = (255, 128, 0, 255)
            case .depthError:
                color = (255, 60, 60, 255)
            }
            let minimumX = feature.imageRect.minX
            let minimumY = feature.imageRect.minY
            let maximumX = minimumX + CGFloat(Self.featureWidth - 1)
            let maximumY = minimumY + CGFloat(Self.featureHeight - 1)
            // Anchor the world coordinate to the ground surface, not the
            // middle of a below-ground patch whose depth/parallax differs.
            let topLeft = CGPoint(x: minimumX, y: minimumY)
            let topRight = CGPoint(x: maximumX, y: minimumY)
            let bottomLeft = CGPoint(x: minimumX, y: maximumY)
            let bottomRight = CGPoint(x: maximumX, y: maximumY)
            paintSegment(&pixels, width: width, height: height, from: topLeft, to: topRight, color: color)
            paintSegment(&pixels, width: width, height: height, from: topRight, to: bottomRight, color: color)
            paintSegment(&pixels, width: width, height: height, from: bottomRight, to: bottomLeft, color: color)
            paintSegment(&pixels, width: width, height: height, from: bottomLeft, to: topLeft, color: color)
        }
        // Ground lines are deliberately absent here. Feature Hypotheses and
        // Cleaned share the one GroundLineDetector.cleanedImage layer, so a
        // tracker-stabilized row cannot appear as a second green line.
        guard let provider = CGDataProvider(data: Data(pixels) as CFData) else { return nil }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
                    | CGBitmapInfo.byteOrder32Big.rawValue
            ),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

    private func result(
        cameraFit: GroundTranslationFit?,
        globalFit: GroundGlobalPoseFit?,
        globalCorrection: CGVector?,
        verticalLineCorrection: CGFloat? = nil
    ) -> GroundHypothesisTrackingResult {
        let activeIDs = Set(active.map(\.id))
        let atlasGeometry = atlasReviewGeometry()
        return GroundHypothesisTrackingResult(
            features: (globalAnchorValid ? active : []).map(featureOutput).sorted {
                $0.imageRect.minY == $1.imageRect.minY
                    ? $0.imageRect.minX < $1.imageRect.minX
                    : $0.imageRect.minY < $1.imageRect.minY
            },
            cameraTranslation: cameraFit.map {
                CGVector(dx: -$0.imageTranslation.dx, dy: $0.imageTranslation.dy)
            },
            inlierCount: cameraFit.map {
                $0.inlierIDs.intersection(activeIDs).count
            } ?? 0,
            residualRMS: cameraFit?.residualRMS,
            globalFeatureCount: relocalizationFeatures.count,
            globalMatchCount: globalFit?.featureIDs.count ?? 0,
            globalCameraPosition: globalFit?.cameraPosition,
            globalCorrection: globalCorrection,
            verticalLineCorrection: verticalLineCorrection,
            groundSegmentCount: groundSegments.count,
            atlasFeatures: atlasGeometry.features,
            atlasLines: atlasGeometry.lines,
            lineReviews: latestLineReviews,
            cameraPosition: localCameraPosition,
            poseVerified: latestPoseVerified,
            hasConfirmedGround: !groundSegments.isEmpty,
            localTextureSupport: localTextureSupport,
            localTextureError: localTextureError,
            localMatchSource: localMatchSource,
            provisionalContinuityActive: cameraSolver.hasProvisionalReference
                && lastProvisionalTextureTimestamp.map { provisionalTimestamp in
                    (lastTimestamp ?? provisionalTimestamp) - provisionalTimestamp <= 0.35
                } ?? false,
            localReferenceTimestamp: localReferenceTimestamp,
            localReferencePosition: localReferencePosition,
            localMatchedImageDisplacement: localMatchedImageDisplacement
        )
    }

    private func atlasReviewGeometry() -> (
        features: [GroundHypothesisAtlasFeature],
        lines: [GroundHypothesisAtlasLine]
    ) {
        let height = frameSize.height
        guard height > 0 else { return ([], []) }
        var features = [GroundHypothesisAtlasFeature]()
        var lines = [GroundHypothesisAtlasLine]()
        for segment in groundSegments {
            let indices = segment.cellIDs.keys.sorted()
            for index in indices {
                let worldX = segment.originWorldX + CGFloat(index * Self.featureSize)
                let globalID = globalFeatureID(
                    nearWorldX: worldX,
                    worldY: segment.worldTopY
                )
                let global = globalID.flatMap { globalFeatures[$0] }
                let referencePatch = global?.referencePatch
                    ?? segment.cellEvidence[index]?.referencePatch
                let referenceOpacity = global?.referenceOpacity
                    ?? segment.cellEvidence[index]?.referenceOpacity
                features.append(GroundHypothesisAtlasFeature(
                    segmentID: segment.id,
                    sequenceIndex: index,
                    atlasPosition: CGPoint(
                        x: worldX + CGFloat(Self.featureWidth) * 0.5,
                        y: height - segment.worldTopY
                            - CGFloat(Self.featureHeight) * 0.5
                    ),
                    referencePixels: referencePatch ?? [],
                    referenceOpacity: referenceOpacity ?? []
                ))
            }
            var start: Int?
            var prior: Int?
            func appendRun(endingAt end: Int) {
                guard let start else { return }
                let atlasStartX = segment.originWorldX
                    + CGFloat(start * Self.featureSize)
                let atlasEndX = segment.originWorldX
                    + CGFloat(end * Self.featureSize + Self.featureSize)
                let atlasY = height - segment.worldTopY
                lines.append(GroundHypothesisAtlasLine(
                    segmentID: segment.id,
                    atlasStart: CGPoint(x: atlasStartX, y: atlasY),
                    atlasEnd: CGPoint(x: atlasEndX, y: atlasY)
                ))
            }
            for index in indices {
                if let prior, index != prior + 1 {
                    appendRun(endingAt: prior)
                    start = index
                } else if start == nil {
                    start = index
                }
                prior = index
            }
            if let prior { appendRun(endingAt: prior) }
        }
        return (features, lines)
    }

    private func updatedFeature(
        from match: Match,
        fit: GroundTranslationFit?,
        luma: [UInt8],
        width: Int
    ) -> ActiveFeature? {
        let residual = fit.map {
            hypot(
                match.imageTranslation.dx - $0.imageTranslation.dx,
                match.imageTranslation.dy - $0.imageTranslation.dy
            )
        }
        let isGround = fit?.inlierIDs.contains(match.feature.id) == true
            && (residual ?? .infinity) <= GroundTranslationRANSAC.inlierRadius
        let score = isGround
            ? match.feature.groundScore + (1 - match.feature.groundScore) * 0.30
            : match.feature.groundScore * 0.65
        let classification: GroundHypothesisClassification = fit == nil
            ? .candidate
            : (isGround ? .groundPlane : .depthError)
        let errorFrames = isGround || fit == nil
            ? 0
            : match.feature.consecutiveErrorFrames + 1
        guard errorFrames < 3 else { return nil }
        return ActiveFeature(
            id: match.feature.id,
            segmentID: match.feature.segmentID,
            sequenceIndex: match.feature.sequenceIndex,
            x: match.x,
            y: match.y,
            patch: extractPatch(
                from: luma,
                width: width,
                x: match.x,
                y: match.y
            ),
            age: match.feature.age + 1,
            groundScore: score,
            classification: classification,
            motionResidual: residual,
            photometricError: match.photometricError,
            consecutiveErrorFrames: errorFrames,
            consecutiveGroundFrames: isGround
                ? match.feature.consecutiveGroundFrames + 1
                : 0,
            globalFeatureID: match.feature.globalFeatureID,
            referenceValid: true
        )
    }

    /// Keeps only the best incomplete left-to-right subsequence. Individual
    /// patch searches may be ambiguous, but feature N can never pass N+1.
    private func orderPreservingMatches(_ matches: [Match]) -> [Match] {
        var selected = [Match]()
        for group in Dictionary(grouping: matches, by: { $0.feature.segmentID }).values {
            let ordered = group.sorted {
                $0.feature.sequenceIndex < $1.feature.sequenceIndex
            }
            guard !ordered.isEmpty else { continue }
            var counts = [Int](repeating: 1, count: ordered.count)
            var errors = ordered.map(\.photometricError)
            var previous = [Int?](repeating: nil, count: ordered.count)
            for index in ordered.indices {
                for earlier in 0..<index
                    where ordered[earlier].x + Self.featureSize / 2 < ordered[index].x {
                    let proposedCount = counts[earlier] + 1
                    let proposedError = errors[earlier] + ordered[index].photometricError
                    if proposedCount > counts[index]
                        || (proposedCount == counts[index] && proposedError < errors[index]) {
                        counts[index] = proposedCount
                        errors[index] = proposedError
                        previous[index] = earlier
                    }
                }
            }
            guard var cursor = ordered.indices.max(by: {
                counts[$0] == counts[$1]
                    ? errors[$0] > errors[$1]
                    : counts[$0] < counts[$1]
            }) else { continue }
            var sequence = [Match]()
            while true {
                sequence.append(ordered[cursor])
                guard let prior = previous[cursor] else { break }
                cursor = prior
            }
            selected.append(contentsOf: sequence.reversed())
        }
        return selected
    }

    /// Keeps detector evidence available to the camera solver, but admits a
    /// new persistent line only after its below-line texture follows the
    /// solved camera motion. The first atlas line bootstraps from the strongest
    /// detector response; every later independent line must pass motion checks.
    private func atlasAdmissionLines(
        from floorLines: [CleanFloorLine],
        luma: [UInt8],
        width: Int,
        height: Int,
        lineSeparation: Int,
        protectedOcclusions: [CGRect]
    ) -> [CleanFloorLine] {
        // Zero-second trackers are deterministic unit-test fixtures and retain
        // their immediate-admission contract.
        guard linePresence.minimumObservationSeconds > 0 else { return floorLines }

        func evidenceCount(_ line: CleanFloorLine) -> Int {
            line.evidenceRanges.reduce(0) { $0 + $1.count }
        }
        let ordered = floorLines.sorted {
            let left = evidenceCount($0), right = evidenceCount($1)
            if left != right { return left > right }
            if $0.xRange.count != $1.xRange.count { return $0.xRange.count > $1.xRange.count }
            return $0.row < $1.row
        }
        let bootstrap = groundSegments.isEmpty ? ordered.first : nil
        var admitted = [CleanFloorLine]()
        var seenCandidateIndices = Set<Int>()

        for line in ordered {
            let minimumX = localCameraPosition.x + CGFloat(line.xRange.lowerBound)
            let maximumX = localCameraPosition.x + CGFloat(line.xRange.upperBound)
            let worldY = CGFloat(line.row) - localCameraPosition.y
            let matchesPersistent = groundSegments.contains { segment in
                abs(segment.worldTopY - worldY) <= Self.segmentAssociationRowTolerance
                    && min(segment.maximumWorldX, maximumX)
                        - max(segment.minimumWorldX, minimumX) + 1
                        >= CGFloat(Self.featureSize)
            }
            if matchesPersistent || line == bootstrap {
                admitted.append(line)
                continue
            }

            let candidateIndex = lineMotionCandidates.indices.filter {
                !seenCandidateIndices.contains($0)
                    && abs(lineMotionCandidates[$0].worldTopY - worldY)
                        <= Self.segmentAssociationRowTolerance
                    && min(lineMotionCandidates[$0].maximumWorldX, maximumX)
                        - max(lineMotionCandidates[$0].minimumWorldX, minimumX) + 1
                        >= CGFloat(Self.featureSize)
            }.min {
                abs(lineMotionCandidates[$0].worldTopY - worldY)
                    < abs(lineMotionCandidates[$1].worldTopY - worldY)
            }

            let index: Int
            if let candidateIndex {
                index = candidateIndex
                updateLineMotionCandidate(
                    at: index,
                    with: line,
                    luma: luma,
                    width: width,
                    height: height,
                    protectedOcclusions: protectedOcclusions
                )
            } else {
                let patches = pendingLinePatches(
                    for: line,
                    luma: luma,
                    width: width,
                    height: height,
                    protectedOcclusions: protectedOcclusions
                )
                lineMotionCandidates.append(LineMotionCandidate(
                    minimumWorldX: minimumX,
                    maximumWorldX: maximumX,
                    worldTopY: worldY,
                    lastSeenFrame: frameIndex,
                    referenceCamera: localCameraPosition,
                    referencePatches: patches,
                    successfulMotionChecks: 0,
                    failedMotionChecks: 0,
                    accumulatedCameraTravel: 0,
                    verified: false
                ))
                index = lineMotionCandidates.count - 1
            }
            seenCandidateIndices.insert(index)
            if lineMotionCandidates[index].verified { admitted.append(line) }
        }

        lineMotionCandidates.removeAll {
            !$0.verified
                && $0.lastSeenFrame < frameIndex - Self.missingLineRejectionFrames
        }

        // A newly verified line near established ground still competes with
        // that ground. Motion evidence permits a real platform, while short
        // unverified foreground edges never reached this list.
        return admitted.filter { candidate in
            guard !groundSegments.isEmpty else { return true }
            let candidateWorldY = CGFloat(candidate.row) - localCameraPosition.y
            let candidateMinimumX = localCameraPosition.x + CGFloat(candidate.xRange.lowerBound)
            let candidateMaximumX = localCameraPosition.x + CGFloat(candidate.xRange.upperBound)
            return !groundSegments.contains { segment in
                let rowDistance = abs(segment.worldTopY - candidateWorldY)
                guard rowDistance > Self.segmentAssociationRowTolerance,
                      rowDistance <= CGFloat(max(0, lineSeparation)) else { return false }
                let gap = horizontalGap(
                    segment.minimumWorldX...segment.maximumWorldX,
                    candidateMinimumX...candidateMaximumX
                )
                guard gap <= 0 else { return false }
                return !lineMotionCandidates.contains { motion in
                    motion.verified
                        && abs(motion.worldTopY - candidateWorldY)
                            <= Self.segmentAssociationRowTolerance
                        && min(motion.maximumWorldX, candidateMaximumX)
                            - max(motion.minimumWorldX, candidateMinimumX) + 1
                            >= CGFloat(Self.featureSize)
                }
            }
        }
    }

    private func updateLineMotionCandidate(
        at index: Int,
        with line: CleanFloorLine,
        luma: [UInt8],
        width: Int,
        height: Int,
        protectedOcclusions: [CGRect]
    ) {
        var candidate = lineMotionCandidates[index]
        let movement = hypot(
            localCameraPosition.x - candidate.referenceCamera.x,
            localCameraPosition.y - candidate.referenceCamera.y
        )
        // A new floor can enter with less than one complete texture patch.
        // Refresh as it becomes visible; an empty entrance reference must not
        // permanently prevent the two independent motion checks.
        if candidate.referencePatches.count < 2 {
            candidate.referenceCamera = localCameraPosition
            candidate.referencePatches = pendingLinePatches(
                for: line, luma: luma, width: width, height: height,
                protectedOcclusions: protectedOcclusions)
        } else if movement >= 1.5 {
            let expectedRow = Int((candidate.worldTopY + localCameraPosition.y).rounded())
            var comparisons = [(worldX: CGFloat, error: CGFloat)]()
            if abs(expectedRow - line.row) <= 2,
               expectedRow >= 0, expectedRow + Self.featureHeight <= height {
                for reference in candidate.referencePatches {
                    let x = Int((reference.worldX - localCameraPosition.x).rounded())
                    let featureRange = x...(x + Self.featureWidth - 1)
                    guard x >= 0, x + Self.featureWidth <= width,
                          line.evidenceRanges.contains(where: {
                              min($0.upperBound, featureRange.upperBound)
                                  - max($0.lowerBound, featureRange.lowerBound) + 1 >= 8
                          })
                    else { continue }
                    let rect = CGRect(
                        x: x, y: expectedRow,
                        width: Self.featureWidth, height: Self.featureHeight
                    )
                    guard !protectedOcclusions.contains(where: { $0.intersects(rect) })
                    else { continue }
                    let observed = extractPatch(
                        from: luma, width: width, x: x, y: expectedRow
                    )
                    guard observed.count == Self.featurePixelCount else { continue }
                    comparisons.append((
                        reference.worldX,
                        GroundCameraSolver.patchError(reference.pixels, observed)
                    ))
                }
            }
            let inliers = comparisons.filter { $0.error <= 18 }
            let spread = inliers.map(\.worldX).max().flatMap { maximum in
                inliers.map(\.worldX).min().map { maximum - $0 }
            } ?? 0
            let passed = comparisons.count >= 2
                && inliers.count * 2 > comparisons.count
                && spread >= 8
            if passed {
                candidate.successfulMotionChecks += 1
                candidate.failedMotionChecks = 0
                candidate.accumulatedCameraTravel += movement
                candidate.referenceCamera = localCameraPosition
                candidate.referencePatches = pendingLinePatches(
                    for: line,
                    luma: luma,
                    width: width,
                    height: height,
                    protectedOcclusions: protectedOcclusions
                )
            } else if comparisons.count < 2 {
                // Old patches may leave the view or become masked before a
                // second comparison. Start again from currently usable pixels.
                candidate.referenceCamera = localCameraPosition
                candidate.referencePatches = pendingLinePatches(
                    for: line, luma: luma, width: width, height: height,
                    protectedOcclusions: protectedOcclusions)
            } else if comparisons.count >= 2 || abs(expectedRow - line.row) > 2 {
                candidate.failedMotionChecks += 1
                candidate.successfulMotionChecks = max(0, candidate.successfulMotionChecks - 1)
                if candidate.failedMotionChecks >= 3 {
                    candidate.referenceCamera = localCameraPosition
                    candidate.referencePatches = pendingLinePatches(
                        for: line,
                        luma: luma,
                        width: width,
                        height: height,
                        protectedOcclusions: protectedOcclusions
                    )
                    candidate.failedMotionChecks = 0
                    candidate.accumulatedCameraTravel = 0
                }
            }
        }
        candidate.minimumWorldX = min(
            candidate.minimumWorldX,
            localCameraPosition.x + CGFloat(line.xRange.lowerBound)
        )
        candidate.maximumWorldX = max(
            candidate.maximumWorldX,
            localCameraPosition.x + CGFloat(line.xRange.upperBound)
        )
        candidate.lastSeenFrame = frameIndex
        candidate.verified = candidate.successfulMotionChecks >= 2
            && candidate.accumulatedCameraTravel >= 4
        lineMotionCandidates[index] = candidate
    }

    private func pendingLinePatches(
        for line: CleanFloorLine,
        luma: [UInt8],
        width: Int,
        height: Int,
        protectedOcclusions: [CGRect]
    ) -> [PendingLinePatch] {
        guard line.row >= 0, line.row + Self.featureHeight <= height else { return [] }
        var positions = Set<Int>()
        for evidence in line.evidenceRanges where evidence.count >= Self.featureWidth {
            var x = evidence.lowerBound
            while x + Self.featureWidth - 1 <= evidence.upperBound {
                positions.insert(x)
                x += Self.featureSize
            }
            positions.insert(evidence.upperBound - Self.featureWidth + 1)
        }
        let ordered = positions.sorted()
        let stride = max(1, Int(ceil(Double(ordered.count) / 16)))
        return ordered.enumerated().compactMap { offset, x in
            guard offset.isMultiple(of: stride),
                  x >= 0, x + Self.featureWidth <= width else { return nil }
            let rect = CGRect(
                x: x, y: line.row,
                width: Self.featureWidth, height: Self.featureHeight
            )
            guard !protectedOcclusions.contains(where: { $0.intersects(rect) })
            else { return nil }
            let pixels = extractPatch(
                from: luma, width: width, x: x, y: line.row
            )
            guard pixels.count == Self.featurePixelCount,
                  Int(pixels.max() ?? 0) - Int(pixels.min() ?? 0) >= 12
            else { return nil }
            return PendingLinePatch(
                worldX: localCameraPosition.x + CGFloat(x),
                pixels: pixels
            )
        }
    }

    /// A line must recur before it can create or extend permanent ground.
    /// Confirmed segments then accumulate per-cell positive and negative
    /// evidence; momentary lines disappear with their pending candidates.
    private func integrateGroundSegments(
        _ floorLines: [CleanFloorLine],
        lineSeparation _: Int = 40,
        occlusionMergeGap: Int = 300,
        width: Int,
        height: Int,
        trackedFeatureIDs: Set<Int>,
        cameraPoseConfirmed: Bool,
        protectedOcclusions: [CGRect]
    ) {
        var seenPendingIDs = Set<Int>()
        for line in floorLines.sorted(by: {
            if $0.xRange.count != $1.xRange.count {
                return $0.xRange.count > $1.xRange.count
            }
            if $0.xRange.lowerBound != $1.xRange.lowerBound {
                return $0.xRange.lowerBound < $1.xRange.lowerBound
            }
            return $0.row < $1.row
        }) {
            let minimumX = localCameraPosition.x + CGFloat(line.xRange.lowerBound)
            let maximumX = localCameraPosition.x + CGFloat(line.xRange.upperBound)
            let worldY = CGFloat(line.row) - localCameraPosition.y
            let stableMatches = groundSegments.filter { segment in
                abs(segment.worldTopY - worldY) <= Self.segmentAssociationRowTolerance
                    && horizontalGap(
                        segment.minimumWorldX...segment.maximumWorldX,
                        minimumX...maximumX
                    ) <= 0
            }
            let extendsStableGround = stableMatches.contains {
                minimumX < $0.minimumWorldX - 8 || maximumX > $0.maximumWorldX + 8
            }
            // Without a texture solve or accepted camera delta, a vertically
            // shifted line over known columns is ambiguous camera motion—not
            // evidence for a second platform. Keep it pending only after pose
            // becomes known; this prevents duplicate Line 1 rows.
            if stableMatches.isEmpty, !cameraPoseConfirmed,
               groundSegments.contains(where: { segment in
                   let projectedRow = segment.worldTopY + localCameraPosition.y
                   let isExpectedOnscreen = projectedRow >= 0
                       && projectedRow < CGFloat(height)
                   let overlap = min(segment.maximumWorldX, maximumX)
                       - max(segment.minimumWorldX, minimumX) + 1
                   return isExpectedOnscreen && overlap >= CGFloat(Self.featureSize)
               }) {
                continue
            }
            guard stableMatches.isEmpty || stableMatches.count > 1 || extendsStableGround
            else { continue }

            let pendingIndex = pendingLines.indices.min { left, right in
                pendingMatchCost(
                    pendingLines[left], minimumX: minimumX, maximumX: maximumX, worldY: worldY, floorLines: floorLines
                ) < pendingMatchCost(
                    pendingLines[right], minimumX: minimumX, maximumX: maximumX, worldY: worldY, floorLines: floorLines
                )
            }.flatMap { index in
                pendingMatchCost(
                    pendingLines[index], minimumX: minimumX, maximumX: maximumX, worldY: worldY, floorLines: floorLines
                ) <= CGFloat(occlusionMergeGap) ? index : nil
            }
            if let pendingIndex {
                var pending = pendingLines[pendingIndex]
                pending.minimumWorldX = min(pending.minimumWorldX, minimumX)
                pending.maximumWorldX = max(pending.maximumWorldX, maximumX)
                pending.consecutiveObservations = pending.lastSeenFrame == frameIndex - 1
                    ? pending.consecutiveObservations + 1
                    : 1
                pending.lastSeenFrame = frameIndex
                pendingLines[pendingIndex] = pending
                seenPendingIDs.insert(pending.id)
            } else {
                let pending = PendingLine(
                    id: nextPendingLineID,
                    minimumWorldX: minimumX,
                    maximumWorldX: maximumX,
                    worldTopY: worldY,
                    consecutiveObservations: 1,
                    lastSeenFrame: frameIndex
                )
                nextPendingLineID += 1
                pendingLines.append(pending)
                seenPendingIDs.insert(pending.id)
            }
        }
        pendingLines.removeAll {
            $0.lastSeenFrame < frameIndex - 1 && !seenPendingIDs.contains($0.id)
        }

        // The elapsed-time presence policy has already confirmed these lines.
        let confirmed = pendingLines
        pendingLines.removeAll { pending in confirmed.contains { $0.id == pending.id } }
        for line in confirmed {
            mergeConfirmedLine(line, occlusionMergeGap: occlusionMergeGap, floorLines: floorLines)
        }

        updateCellEvidence(
            floorLines: floorLines,
            width: width,
            height: height,
            trackedFeatureIDs: trackedFeatureIDs,
            cameraPoseConfirmed: cameraPoseConfirmed,
            protectedOcclusions: protectedOcclusions
        )
        groundSegments.sort { $0.id < $1.id }
    }

    private func pendingMatchCost(
        _ pending: PendingLine,
        minimumX: CGFloat,
        maximumX: CGFloat,
        worldY: CGFloat,
        floorLines: [CleanFloorLine]
    ) -> CGFloat {
        let rowDistance = abs(pending.worldTopY - worldY)
        guard rowDistance <= Self.segmentAssociationRowTolerance,
              !hasLowerFloorInGap(pending.minimumWorldX...pending.maximumWorldX,
                minimumX...maximumX, worldY: worldY, floorLines: floorLines)
        else { return .infinity }
        return horizontalGap(
            pending.minimumWorldX...pending.maximumWorldX,
            minimumX...maximumX
        ) + rowDistance
    }

    /// A temporarily masked lower edge must not turn its known dip back into
    /// an upper-floor occlusion bridge. Keep both upper IDs and the lower floor.
    private func preservingKnownFloorGaps(_ lines: [CleanFloorLine]) -> [CleanFloorLine] {
        lines.flatMap { line -> [CleanFloorLine] in
            var pieces = [CleanFloorLine]()
            var group = [ClosedRange<Int>]()
            func finish() {
                guard let first = group.first, let last = group.last else { return }
                pieces.append(CleanFloorLine(row: line.row,
                    xRange: first.lowerBound...last.upperBound, evidenceRanges: group))
            }
            for evidence in line.evidenceRanges {
                if let previous = group.last,
                   hasLowerFloorInGap(
                    (localCameraPosition.x + CGFloat(previous.lowerBound)) ... (localCameraPosition.x + CGFloat(previous.upperBound)),
                    (localCameraPosition.x + CGFloat(evidence.lowerBound)) ... (localCameraPosition.x + CGFloat(evidence.upperBound)),
                    worldY: CGFloat(line.row) - localCameraPosition.y, floorLines: lines) {
                    finish(); group.removeAll(keepingCapacity: true)
                }
                group.append(evidence)
            }
            finish()
            return pieces
        }
    }

    /// Do not merge two co-planar ends over a positively observed lower floor.
    /// Persistent geometry retains the boundary when its evidence is occluded.
    private func hasLowerFloorInGap(
        _ left: ClosedRange<CGFloat>, _ right: ClosedRange<CGFloat>,
        worldY: CGFloat, floorLines: [CleanFloorLine]
    ) -> Bool {
        guard !left.overlaps(right) else { return false }
        let lower = min(left.upperBound, right.upperBound)
        let upper = max(left.lowerBound, right.lowerBound)
        guard upper - lower >= CGFloat(Self.featureSize) else { return false }
        func crosses(_ x0: CGFloat, _ x1: CGFloat, _ y: CGFloat) -> Bool {
            y > worldY + Self.segmentAssociationRowTolerance
                && min(upper, x1) - max(lower, x0) >= CGFloat(Self.featureSize)
        }
        return groundSegments.contains {
            crosses($0.minimumWorldX, $0.maximumWorldX, $0.worldTopY)
        } || floorLines.contains {
            crosses(localCameraPosition.x + CGFloat($0.xRange.lowerBound),
                localCameraPosition.x + CGFloat($0.xRange.upperBound),
                CGFloat($0.row) - localCameraPosition.y)
        }
    }

    /// Edge detection proposes a row; already tracked ground texture refines
    /// it independently. Median inlier offset removes one- and two-pixel edge
    /// flicker without hiding real camera motion shared by line and features.
    private func stabilizeFloorLines(
        _ floorLines: [CleanFloorLine],
        matches: [Match],
        cameraFit: GroundTranslationFit?
    ) -> [CleanFloorLine] {
        guard let cameraFit else { return floorLines }
        let inliers = matches.filter { cameraFit.inlierIDs.contains($0.feature.id) }
        return floorLines.map { line in
            let offsets = inliers.compactMap { match -> Int? in
                guard match.detectedLineRow == line.row,
                      line.xRange.contains(match.x),
                      line.xRange.contains(match.x + Self.featureSize - 1)
                else { return nil }
                return match.y - line.row
            }.sorted()
            guard offsets.count >= 2 else { return line }
            let offset = min(
                Self.verticalRefinementRadius,
                max(-Self.verticalRefinementRadius, offsets[offsets.count / 2])
            )
            guard offset != 0 else { return line }
            return CleanFloorLine(
                row: line.row + offset,
                xRange: line.xRange,
                evidenceRanges: line.evidenceRanges
            )
        }
    }

    private func mergeConfirmedLine(
        _ line: PendingLine,
        occlusionMergeGap: Int,
        floorLines: [CleanFloorLine]
    ) {
        let candidates = groundSegments.indices.filter { index in
            abs(groundSegments[index].worldTopY - line.worldTopY)
                <= Self.segmentAssociationRowTolerance
                && horizontalGap(
                    groundSegments[index].minimumWorldX...groundSegments[index].maximumWorldX,
                    line.minimumWorldX...line.maximumWorldX
                ) <= CGFloat(occlusionMergeGap)
                && !hasLowerFloorInGap(
                    groundSegments[index].minimumWorldX...groundSegments[index].maximumWorldX,
                    line.minimumWorldX...line.maximumWorldX,
                    worldY: line.worldTopY, floorLines: floorLines)
        }
        guard !candidates.isEmpty else {
            guard let bounds = rectifiedCellBounds(
                minimumWorldX: line.minimumWorldX,
                maximumWorldX: line.maximumWorldX
            ) else { return }
            var segment = GroundSegment(
                id: nextSegmentID,
                originWorldX: bounds.lowerBound,
                minimumWorldX: bounds.lowerBound,
                maximumWorldX: bounds.upperBound,
                worldTopY: line.worldTopY,
                cellIDs: [:],
                cellEvidence: [:]
            )
            nextSegmentID += 1
            ensureCellIDs(in: &segment)
            groundSegments.append(segment)
            return
        }

        let targetIndex = candidates.min {
            groundSegments[$0].id < groundSegments[$1].id
        }!
        let candidateSegments = candidates.map { groundSegments[$0] }
        let unionMinimum = candidateSegments.reduce(line.minimumWorldX) {
            min($0, $1.minimumWorldX)
        }
        let unionMaximum = candidateSegments.reduce(line.maximumWorldX) {
            max($0, $1.maximumWorldX)
        }
        guard let bounds = rectifiedCellBounds(
            minimumWorldX: unionMinimum,
            maximumWorldX: unionMaximum
        ) else { return }

        var target = groundSegments[targetIndex]
        target.originWorldX = bounds.lowerBound
        target.minimumWorldX = bounds.lowerBound
        target.maximumWorldX = bounds.upperBound
        target.cellIDs.removeAll(keepingCapacity: true)
        target.cellEvidence.removeAll(keepingCapacity: true)

        // Rebuild one absolute left-to-right sequence. Existing IDs and their
        // evidence move by canonical world position; local indices never
        // survive a merge as accidental phase offsets.
        var retainedIndexByID = [Int: Int]()
        let orderedSegments = candidateSegments.sorted { $0.id < $1.id }
        for segment in orderedSegments {
            for (oldIndex, id) in segment.cellIDs.sorted(by: { $0.key < $1.key }) {
                let oldWorldX = segment.originWorldX
                    + CGFloat(oldIndex * Self.featureSize)
                let newIndex = Int(
                    ((oldWorldX - target.originWorldX)
                        / CGFloat(Self.featureSize)).rounded()
                )
                let newWorldX = target.originWorldX
                    + CGFloat(newIndex * Self.featureSize)
                guard newWorldX >= bounds.lowerBound - 0.5,
                      newWorldX + CGFloat(Self.featureSize - 1)
                        <= bounds.upperBound + 0.5,
                      target.cellIDs[newIndex] == nil
                else { continue }
                target.cellIDs[newIndex] = id
                target.cellEvidence[newIndex] = segment.cellEvidence[oldIndex]
                retainedIndexByID[id] = newIndex
            }
        }
        ensureCellIDs(in: &target)
        let mergedSegmentIDs = Set(candidateSegments.map(\.id))
        groundSegments = groundSegments.enumerated().compactMap { index, segment in
            if index == targetIndex { return target }
            return candidates.contains(index) ? nil : segment
        }
        active = active.compactMap { feature in
            guard mergedSegmentIDs.contains(feature.segmentID) else { return feature }
            guard let newIndex = retainedIndexByID[feature.id] else { return nil }
            var moved = feature
            moved.segmentID = target.id
            moved.sequenceIndex = newIndex
            return moved
        }
        for id in Array(globalFeatures.keys) {
            guard var feature = globalFeatures[id],
                  mergedSegmentIDs.contains(feature.segmentID)
            else { continue }
            let newIndex = Int(((feature.worldX - target.originWorldX)
                / CGFloat(Self.featureSize)).rounded())
            feature.segmentID = target.id
            feature.sequenceIndex = newIndex
            globalFeatures[id] = feature
        }
    }

    /// Returns the largest complete-cell range inside detected line bounds,
    /// snapped to one atlas-wide phase. End fragments under 16 pixels remain
    /// line evidence but never become partial feature tiles.
    private func rectifiedCellBounds(
        minimumWorldX: CGFloat,
        maximumWorldX: CGFloat
    ) -> ClosedRange<CGFloat>? {
        guard minimumWorldX.isFinite, maximumWorldX.isFinite,
              maximumWorldX >= minimumWorldX
        else { return nil }
        if globalTilePhaseX == nil { globalTilePhaseX = minimumWorldX }
        guard let phase = globalTilePhaseX else { return nil }
        let size = CGFloat(Self.featureSize)
        let firstIndex = Int(ceil((minimumWorldX - phase) / size))
        let lastIndex = Int(floor(
            (maximumWorldX - CGFloat(Self.featureSize - 1) - phase) / size
        ))
        guard lastIndex >= firstIndex else { return nil }
        let first = phase + CGFloat(firstIndex) * size
        let lastEnd = phase + CGFloat(lastIndex) * size
            + CGFloat(Self.featureSize - 1)
        return first...lastEnd
    }

    private func updateCellEvidence(
        floorLines: [CleanFloorLine], width: Int, height: Int,
        trackedFeatureIDs: Set<Int>, cameraPoseConfirmed: Bool,
        protectedOcclusions: [CGRect]
    ) {
        // Line lifetime is controlled only by elapsed visible-time evidence.
        // Missing tiles remain in the lattice for future unoccluded samples.
        guard cameraPoseConfirmed else { return }
        for index in groundSegments.indices {
            var segment = groundSegments[index]
            for sequence in segment.cellIDs.keys {
                let x = Int((segment.originWorldX + CGFloat(sequence * Self.featureSize)
                    - localCameraPosition.x).rounded())
                let y = Int((segment.worldTopY + localCameraPosition.y).rounded())
                let rect = CGRect(x: x, y: y, width: Self.featureWidth, height: Self.featureHeight)
                guard x >= 0, y >= 0, x + Self.featureWidth <= width,
                      y + Self.featureHeight <= height,
                      !protectedOcclusions.contains(where: { $0.intersects(rect) }) else { continue }
                var evidence = segment.cellEvidence[sequence] ?? CellEvidence(
                    visibleObservations: 0, supportingObservations: 0,
                    consecutiveMisses: 0, referencePatch: [], referenceOpacity: []
                )
                evidence.visibleObservations += 1
                if isUnderCleanLine(x: x, y: y, floorLines: floorLines) {
                    evidence.supportingObservations += 1
                }
                segment.cellEvidence[sequence] = evidence
            }
            groundSegments[index] = segment
        }
    }

    private static func lineIsMostlyForeground(
        minimumX: CGFloat,
        maximumX: CGFloat,
        row: CGFloat,
        regions: [CGRect]
    ) -> Bool {
        guard minimumX.isFinite, maximumX.isFinite, row.isFinite,
              maximumX > minimumX else { return false }
        let span = maximumX - minimumX
        return regions.contains { region in
            guard !region.isNull, !region.isEmpty else { return false }
            let expanded = region.insetBy(dx: -8, dy: -4)
            guard row >= expanded.minY, row <= expanded.maxY else { return false }
            let overlap = max(
                0,
                min(maximumX, expanded.maxX) - max(minimumX, expanded.minX)
            )
            return overlap / span >= 0.55
        }
    }

    private func ensureCellIDs(in segment: inout GroundSegment) {
        let size = CGFloat(Self.featureSize)
        let minimumIndex = Int(ceil((segment.minimumWorldX - segment.originWorldX) / size))
        let maximumIndex = Int(floor(
            (segment.maximumWorldX - CGFloat(Self.featureSize - 1) - segment.originWorldX) / size
        ))
        guard maximumIndex >= minimumIndex else { return }
        for index in minimumIndex...maximumIndex where segment.cellIDs[index] == nil {
            segment.cellIDs[index] = nextID
            segment.cellEvidence[index] = CellEvidence(
                visibleObservations: 0,
                supportingObservations: 0,
                consecutiveMisses: 0,
                referencePatch: [],
                referenceOpacity: []
            )
            nextID += 1
        }
    }

    private func horizontalGap(
        _ left: ClosedRange<CGFloat>,
        _ right: ClosedRange<CGFloat>
    ) -> CGFloat {
        if left.overlaps(right) { return 0 }
        return left.upperBound < right.lowerBound
            ? right.lowerBound - left.upperBound - 1
            : left.lowerBound - right.upperBound - 1
    }

    private func projectGroundSegments(
        floorLines: [CleanFloorLine],
        tracked: [ActiveFeature],
        previous: [ActiveFeature],
        luma: [UInt8],
        width: Int,
        height: Int,
        protectedOcclusions: [CGRect]
    ) -> [ActiveFeature] {
        let trackedByID = Dictionary(uniqueKeysWithValues: tracked.map { ($0.id, $0) })
        let previousByID = Dictionary(uniqueKeysWithValues: previous.map { ($0.id, $0) })
        var projected = [ActiveFeature]()
        for segment in groundSegments {
            for (sequenceIndex, id) in segment.cellIDs.sorted(by: { $0.key < $1.key }) {
                let worldX = segment.originWorldX
                    + CGFloat(sequenceIndex * Self.featureSize)
                let x = Int((worldX - localCameraPosition.x).rounded())
                let y = Int((segment.worldTopY + localCameraPosition.y).rounded())
                guard x >= 0, y >= 0,
                      x + Self.featureWidth <= width,
                      y + Self.featureHeight <= height
                else { continue }
                let projection = CellProjection(
                    id: id,
                    segmentID: segment.id,
                    sequenceIndex: sequenceIndex,
                    x: x,
                    y: y,
                    worldX: worldX,
                    worldY: segment.worldTopY,
                    hasCurrentEvidence: !protectedOcclusions.contains(where: {
                        $0.intersects(CGRect(x: x, y: y,
                            width: Self.featureWidth, height: Self.featureHeight))
                    }) && hasGroundEvidence(
                        x: x,
                        y: y,
                        floorLines: floorLines
                    )
                )
                if projection.hasCurrentEvidence, var feature = trackedByID[id] {
                    feature.segmentID = segment.id
                    feature.sequenceIndex = sequenceIndex
                    feature.x = x
                    feature.y = y
                    feature.patch = extractPatch(
                        from: luma,
                        width: width,
                        x: x,
                        y: y
                    )
                    feature.referenceValid = true
                    projected.append(feature)
                } else if !projection.hasCurrentEvidence,
                          var feature = trackedByID[id] ?? previousByID[id] {
                    feature.segmentID = segment.id
                    feature.sequenceIndex = sequenceIndex
                    feature.x = x
                    feature.y = y
                    feature.classification = .occluded
                    feature.motionResidual = nil
                    feature.photometricError = nil
                    projected.append(feature)
                } else {
                    projected.append(makeFeature(
                        projection,
                        luma: luma,
                        width: width
                    ))
                }
            }
        }
        return projected
    }

    private func recordReferencePatches() {
        for feature in active where
            feature.referenceValid
                && feature.classification != .occluded
                && feature.patch.count == Self.featurePixelCount {
            guard let segmentIndex = groundSegments.firstIndex(where: {
                $0.id == feature.segmentID
            }), var evidence = groundSegments[segmentIndex].cellEvidence[
                feature.sequenceIndex
            ] else { continue }
            updateReferenceEvidence(&evidence, with: feature)
            groundSegments[segmentIndex].cellEvidence[feature.sequenceIndex] = evidence
            if let globalID = feature.globalFeatureID,
               var global = globalFeatures[globalID] {
                global.referencePatch = evidence.referencePatch
                global.referenceOpacity = evidence.referenceOpacity
                globalFeatures[globalID] = global
            }
        }
    }

    private func updateReferenceEvidence(
        _ evidence: inout CellEvidence,
        with feature: ActiveFeature
    ) {
        let pixelCount = Self.featurePixelCount
        let cameraConsistent = feature.classification == .groundPlane
            || feature.classification == .globalMatch
        if evidence.referencePatch.count != pixelCount {
            evidence.referencePatch = feature.patch
            let initial: UInt8 = cameraConsistent ? 96 : 32
            evidence.referenceOpacity = [UInt8](repeating: initial, count: pixelCount)
            return
        }
        if evidence.referenceOpacity.count != pixelCount {
            evidence.referenceOpacity = [UInt8](repeating: 32, count: pixelCount)
        }
        for index in 0..<pixelCount {
            let current = feature.patch[index]
            let prior = evidence.referencePatch[index]
            var confidence = Int(evidence.referenceOpacity[index])
            if feature.classification == .depthError {
                confidence = max(0, confidence - 48)
            } else if abs(Int(current) - Int(prior)) <= 14 {
                let gain = cameraConsistent ? 24 : 5
                let ceiling = cameraConsistent ? 255 : 96
                confidence = min(ceiling, confidence + gain)
            } else if cameraConsistent, confidence <= 8 {
                // Old evidence has decayed away. A newly revealed surface may
                // now establish this pixel without replacing reliable peers.
                confidence = 48
            } else {
                confidence = max(0, confidence - (cameraConsistent ? 32 : 12))
            }
            // Viewer displays one coherent current-frame strip. Confidence,
            // not stale color, carries temporal stability across observations.
            evidence.referencePatch[index] = current
            evidence.referenceOpacity[index] = UInt8(confidence)
        }
    }

    private func hasGroundEvidence(
        x: Int,
        y: Int,
        floorLines: [CleanFloorLine]
    ) -> Bool {
        let featureRange = x...(x + Self.featureSize - 1)
        return floorLines.contains { line in
            guard abs(line.row - y) <= Self.evidenceRowTolerance else { return false }
            return line.evidenceRanges.contains { evidence in
                max(featureRange.lowerBound, evidence.lowerBound)
                    <= min(featureRange.upperBound, evidence.upperBound)
            }
        }
    }

    private func isUnderCleanLine(
        x: Int,
        y: Int,
        floorLines: [CleanFloorLine]
    ) -> Bool {
        let featureRange = x...(x + Self.featureSize - 1)
        return floorLines.contains { line in
            guard abs(line.row - y) <= Self.evidenceRowTolerance else { return false }
            // A merged xRange is only a bridge hypothesis. It must not keep
            // orange cells alive forever where raw clean-line evidence is
            // usually absent.
            return line.evidenceRanges.contains { $0.overlaps(featureRange) }
        }
    }

    private func makeFeature(
        _ projection: CellProjection,
        luma: [UInt8],
        width: Int
    ) -> ActiveFeature {
        let patch = projection.hasCurrentEvidence
            ? extractPatch(
                from: luma,
                width: width,
                x: projection.x,
                y: projection.y
            )
            : []
        return ActiveFeature(
            id: projection.id,
            segmentID: projection.segmentID,
            sequenceIndex: projection.sequenceIndex,
            x: projection.x,
            y: projection.y,
            patch: patch,
            age: 1,
            groundScore: 0.5,
            classification: projection.hasCurrentEvidence ? .candidate : .occluded,
            motionResidual: nil,
            photometricError: nil,
            consecutiveErrorFrames: 0,
            consecutiveGroundFrames: 0,
            globalFeatureID: globalFeatureID(
                nearWorldX: projection.worldX,
                worldY: projection.worldY
            ),
            referenceValid: projection.hasCurrentEvidence
        )
    }

    private func globalFeatureID(nearWorldX worldX: CGFloat, worldY: CGFloat) -> Int? {
        globalFeatures.values.first {
            hypot($0.worldX - worldX, $0.worldTopY - worldY) <= 2
        }?.id
    }

    private var relocalizationFeatures: [GlobalFeature] {
        if let cachedRelocalizationFeatures { return cachedRelocalizationFeatures }
        var byID = retiredGlobalFeatures
        for (id, feature) in globalFeatures { byID[id] = feature }
        // Prefer the immutable creation-time copy when an ID is part of the
        // canonical bank. Merging may have reassigned the live copy's Line ID.
        for (id, feature) in canonicalAnchorFeatures { byID[id] = feature }
        let result = byID.values.sorted { $0.id < $1.id }
        cachedRelocalizationFeatures = result
        return result
    }

    private func removeGlobalFeatures(nearWorldX worldX: CGFloat, worldY: CGFloat) {
        let removedIDs = Set(globalFeatures.values.compactMap { feature in
            hypot(feature.worldX - worldX, feature.worldTopY - worldY) <= 2
                ? feature.id : nil
        })
        guard !removedIDs.isEmpty else { return }
        for id in removedIDs { globalFeatures[id] = nil }
        for index in active.indices where
            active[index].globalFeatureID.map(removedIDs.contains) == true {
            active[index].globalFeatureID = nil
        }
    }

    private func promoteVerifiedFeatures() {
        guard relocalizationFeatures.isEmpty || globalAnchorValid else { return }
        // A line can span a room. Freeze a separate first-view population
        // before longer travel can mix accumulated odometry into its geometry.
        // The ordinary, broader canonical bank remains available as before.
        for (segmentID, origin) in canonicalViewOrigins where
            hypot(localCameraPosition.x - origin.x, localCameraPosition.y - origin.y) > 32 {
            closedCanonicalViews.insert(segmentID)
        }
        for index in active.indices {
            guard active[index].globalFeatureID == nil,
                  active[index].classification == .groundPlane,
                  active[index].age >= 3,
                  active[index].consecutiveGroundFrames >= 2,
                  (active[index].photometricError ?? .infinity)
                    <= Self.promotionPhotometricError
            else { continue }
            let descriptor = normalizedDescriptor(active[index].patch)
            guard descriptorEnergy(descriptor) >= 6 else { continue }
            guard let canonical = canonicalWorldPosition(for: active[index]) else {
                continue
            }
            let worldX = canonical.x
            let worldY = canonical.y
            if let existing = globalFeatures.values.min(by: {
                worldDistance($0, x: worldX, y: worldY)
                    < worldDistance($1, x: worldX, y: worldY)
            }), worldDistance(existing, x: worldX, y: worldY) <= 4,
               descriptorDistance(descriptor, existing.descriptor) <= 12 {
                active[index].globalFeatureID = existing.id
                active[index].classification = .globalMatch
                observeGlobalFeature(existing.id)
            } else {
                let id = nextGlobalID
                nextGlobalID += 1
                let promoted = GlobalFeature(
                    id: id,
                    segmentID: active[index].segmentID,
                    sequenceIndex: active[index].sequenceIndex,
                    worldX: worldX,
                    worldTopY: worldY,
                    descriptor: descriptor,
                    matchingPatch: active[index].patch,
                    referencePatch: active[index].patch,
                    referenceOpacity: groundSegments.first(where: {
                        $0.id == active[index].segmentID
                    })?.cellEvidence[active[index].sequenceIndex]?.referenceOpacity
                        ?? [UInt8](repeating: 96, count: Self.featurePixelCount),
                    observationCount: 1,
                    lastSeenFrame: frameIndex
                )
                globalFeatures[id] = promoted
                let anchorCount = canonicalAnchorFeatures.values.reduce(0) {
                    $0 + ($1.segmentID == promoted.segmentID ? 1 : 0)
                }
                if anchorCount < 64 {
                    canonicalAnchorFeatures[id] = promoted
                    if !closedCanonicalViews.contains(promoted.segmentID) {
                        if canonicalViewOrigins[promoted.segmentID] == nil {
                            canonicalViewOrigins[promoted.segmentID] = localCameraPosition
                        }
                        canonicalFirstViewIDs.insert(id)
                    }
                }
                active[index].globalFeatureID = id
                active[index].classification = .globalMatch
            }
        }
    }

    private func canonicalWorldPosition(for feature: ActiveFeature) -> CGPoint? {
        guard let segment = groundSegments.first(where: { $0.id == feature.segmentID }),
              segment.cellIDs[feature.sequenceIndex] == feature.id
        else { return nil }
        return CGPoint(
            x: segment.originWorldX
                + CGFloat(feature.sequenceIndex * Self.featureSize),
            y: segment.worldTopY
        )
    }

    private func backgroundFit(
        luma: [UInt8], width: Int, height: Int,
        floorLines: [CleanFloorLine], exclusions: [CGRect],
        timestamp: Double, recovering: Bool
    ) -> GroundGlobalPoseFit? {
        let completed = globalSearchWorker.take()
        if lastGlobalSearchTimestamp.map({ timestamp - $0 >= 0.25 }) ?? true {
            // Only value data is copied. The background tracker never owns live
            // camera, line-ledger, or atlas state, and only returns a proposal.
            let snapshot = GroundHypothesisTracker()
            snapshot.active = active
            snapshot.globalFeatures = globalFeatures
            snapshot.retiredGlobalFeatures = retiredGlobalFeatures
            snapshot.canonicalAnchorFeatures = canonicalAnchorFeatures
            snapshot.canonicalFirstViewIDs = canonicalFirstViewIDs
            snapshot.cachedRelocalizationFeatures = relocalizationFeatures
            snapshot.localCameraPosition = localCameraPosition
            snapshot.globalAnchorValid = globalAnchorValid
            let canonicalInterval = recovering ? 0.75 : 2.0
            let scanCanonical = correctionConfirmation.needsConfirmation || (lastCanonicalPhaseSearchTimestamp.map {
                timestamp - $0 >= canonicalInterval
            } ?? true)
            let scanAlternatives = lastAlternativeGlobalSearchTimestamp.map {
                timestamp - $0 >= 1
            } ?? true
            let submitted = globalSearchWorker.submit {
                let started = ProcessInfo.processInfo.systemUptime
                let fit = snapshot.searchSnapshot(luma: luma, width: width, height: height,
                    floorLines: floorLines, exclusions: exclusions,
                    scanCanonical: scanCanonical, scanAlternatives: scanAlternatives)
                return SearchResult(fit: fit, timestamp: timestamp,
                    localPose: snapshot.localCameraPosition,
                    duration: ProcessInfo.processInfo.systemUptime - started)
            }
            if submitted {
                lastGlobalSearchTimestamp = timestamp
                if scanCanonical { lastCanonicalPhaseSearchTimestamp = timestamp }
                if scanAlternatives { lastAlternativeGlobalSearchTimestamp = timestamp }
            }
        }
        guard let completed else { return nil }
        completedGlobalSearchMilliseconds = completed.duration * 1_000
        if traceEnabled {
            let pose = completed.fit.map { "\($0.cameraPosition.x),\($0.cameraPosition.y)" } ?? "none"
            traceLog.info("global-search-result t=\(timestamp, privacy: .public) source=\(completed.timestamp, privacy: .public) milliseconds=\(completed.duration * 1000, privacy: .public) fit=\(pose, privacy: .public)")
        }
        guard timestamp >= completed.timestamp, timestamp - completed.timestamp <= 0.5,
              let fit = completed.fit else { return nil }
        // Transport a delayed correction only across uninterrupted odometry.
        // During a loss, its absolute placement must verify on current pixels.
        let continuous = lastLocalFailureTimestamp < completed.timestamp
        let dx = continuous ? localCameraPosition.x - completed.localPose.x : 0
        let dy = continuous ? localCameraPosition.y - completed.localPose.y : 0
        let transported = GroundGlobalPoseFit(
            cameraPosition: CGPoint(x: fit.cameraPosition.x + dx, y: fit.cameraPosition.y + dy),
            featureIDs: fit.featureIDs, globalIDs: fit.globalIDs, inliers: fit.inliers,
            verificationFeatureIDs: fit.verificationFeatureIDs)
        guard let verified = verifiedGlobalFit(transported, luma: luma,
            width: width, height: height, floorLines: floorLines,
            exclusions: exclusions, refinementRadius: 0) else {
            if traceEnabled { traceLog.info("global-search-rejected t=\(timestamp, privacy: .public) reason=current-pixels") }
            return nil
        }
        let correction = CGVector(dx: verified.cameraPosition.x - localCameraPosition.x,
                                  dy: verified.cameraPosition.y - localCameraPosition.y)
        guard correctionConfirmation.accepts(correction, timestamp: completed.timestamp) else {
            if traceEnabled { traceLog.info("global-search-rejected t=\(timestamp, privacy: .public) reason=awaiting-confirmation") }
            return nil
        }
        if traceEnabled { traceLog.info("global-search-accepted t=\(timestamp, privacy: .public) correction=\(correction.dx, privacy: .public),\(correction.dy, privacy: .public)") }
        return verified
    }

    private func searchSnapshot(
        luma: [UInt8], width: Int, height: Int,
        floorLines: [CleanFloorLine], exclusions: [CGRect],
        scanCanonical: Bool, scanAlternatives: Bool
    ) -> GroundGlobalPoseFit? {
        let observations = globalMatchObservations()
        var fits = [GroundGlobalPoseFit]()
        if let direct = verifiedGlobalFit(GroundGlobalPoseRANSAC.fit(observations),
            luma: luma, width: width, height: height, floorLines: floorLines, exclusions: exclusions) {
            fits.append(direct)
        }
        if scanAlternatives {
            let represented = Set(fits.flatMap { $0.inliers.map(\.globalSegmentID) })
            let alternatives = Dictionary(grouping: observations.filter {
                !represented.contains($0.globalSegmentID)
            }, by: \.globalSegmentID)
            for segment in alternatives.keys.sorted().prefix(4) {
                guard let fit = verifiedGlobalFit(GroundGlobalPoseRANSAC.fit(alternatives[segment]!),
                    luma: luma, width: width, height: height,
                    floorLines: floorLines, exclusions: exclusions) else { continue }
                fits.append(fit)
            }
        }
        if scanCanonical {
            let segments = Set(Set(relocalizationFeatures.map(\.segmentID)).sorted().prefix(4))
            if let canonical = verifiedCanonicalPhaseFit(luma: luma, width: width, height: height,
                floorLines: floorLines, exclusions: exclusions, allowedSegmentIDs: segments,
                allowedFeatureIDs: canonicalAnchorFeatureIDs(segmentIDs: segments),
                maximumPredictedRowDistance: nil) { fits.append(canonical) }
        }
        if fits.isEmpty, let scanned = verifiedGlobalFit(phaseIndependentGlobalFit(
            luma: luma, width: width, height: height, floorLines: floorLines, exclusions: exclusions),
            luma: luma, width: width, height: height, floorLines: floorLines, exclusions: exclusions) {
            fits.append(scanned)
        }
        return fits.min { a, b in
            let aID = a.inliers.map(\.globalSegmentID).min() ?? .max
            let bID = b.inliers.map(\.globalSegmentID).min() ?? .max
            if aID != bID { return aID < bID }
            if a.inliers.count != b.inliers.count { return a.inliers.count > b.inliers.count }
            return meanDescriptorError(a) < meanDescriptorError(b)
        }
    }

    private func globalMatchObservations() -> [GroundGlobalPoseObservation] {
        let storedFeatures = relocalizationFeatures
        guard storedFeatures.count >= 4 else { return [] }
        let canonicalSegmentIDs = Array(Set(storedFeatures.map(\.segmentID)))
            .sorted().prefix(4)
        var observations = [GroundGlobalPoseObservation]()
        for feature in active {
            guard feature.referenceValid,
                  feature.classification != .occluded
            else { continue }
            let descriptor = normalizedDescriptor(feature.patch)
            guard descriptorEnergy(descriptor) >= 6 else { continue }
            let ranked = storedFeatures.map {
                ($0, descriptorDistance(descriptor, $0.descriptor))
            }.sorted { $0.1 < $1.1 }
            // Preserve the established local hypotheses, then add one best
            // candidate from each otherwise-unrepresented segment. The latter
            // gives an older line a bounded chance to challenge a drifted
            // duplicate without weakening normal same-line reacquisition.
            let primary = ranked.prefix(6).filter {
                $0.1 <= Self.descriptorMatchThreshold
            }
            var seenSegmentIDs = Set(primary.map { $0.0.segmentID })
            var alternatives = [(GlobalFeature, CGFloat)]()
            for candidate in ranked where alternatives.count < 4 {
                guard candidate.1 <= Self.descriptorMatchThreshold else { break }
                if seenSegmentIDs.insert(candidate.0.segmentID).inserted {
                    alternatives.append(candidate)
                }
            }
            var canonical = [(GlobalFeature, CGFloat)]()
            for segmentID in canonicalSegmentIDs where !seenSegmentIDs.contains(segmentID) {
                guard let candidate = ranked.first(where: {
                    $0.0.segmentID == segmentID
                        && $0.1 <= Self.descriptorMatchThreshold
                }) else { continue }
                seenSegmentIDs.insert(segmentID)
                canonical.append(candidate)
            }
            observations.append(contentsOf: (primary + alternatives + canonical).map {
                candidate in
                self.poseObservation(
                    feature: feature,
                    global: candidate.0,
                    descriptorError: candidate.1
                )
            })
        }
        return observations
    }

    /// Searches current clean-line pixels independently of the predicted
    /// world lattice. Each pixel X belongs to one of 16 phases; only a phase
    /// with an ordered, wide sequence of matching immutable tiles can propose
    /// an absolute pose. This is the loop-closure path for accumulated X drift.
    private func phaseIndependentGlobalFit(
        luma: [UInt8], width: Int, height: Int,
        floorLines: [CleanFloorLine], exclusions: [CGRect],
        allowedSegmentIDs: Set<Int>? = nil,
        allowedFeatureIDs: Set<Int>? = nil,
        maximumPredictedRowDistance: CGFloat? = 64
    ) -> GroundGlobalPoseFit? {
        let fits = phaseIndependentGlobalCandidates(
            luma: luma,
            width: width,
            height: height,
            floorLines: floorLines,
            exclusions: exclusions,
            allowedSegmentIDs: allowedSegmentIDs,
            allowedFeatureIDs: allowedFeatureIDs,
            maximumPredictedRowDistance: maximumPredictedRowDistance
        )
        guard let best = fits.first else { return nil }
        let bestError = meanDescriptorError(best)
        let ambiguous = fits.dropFirst().contains { rival in
            hypot(rival.cameraPosition.x - best.cameraPosition.x,
                  rival.cameraPosition.y - best.cameraPosition.y) > 4
                && rival.inliers.count >= best.inliers.count - 1
                && meanDescriptorError(rival) <= bestError * 1.15 + 1
        }
        if traceEnabled {
            if ambiguous {
                traceLog.notice(
                    "global-phase-scan rejected=ambiguous fits=\(fits.count, privacy: .public) best-inliers=\(best.inliers.count, privacy: .public)"
                )
            } else {
                traceLog.notice(
                    "global-phase-scan accepted pose=\(best.cameraPosition.x, privacy: .public),\(best.cameraPosition.y, privacy: .public) inliers=\(best.inliers.count, privacy: .public) error=\(bestError, privacy: .public)"
                )
            }
        }
        return ambiguous ? nil : best
    }

    /// Returns the independently solved lattice phases in descriptor rank
    /// order. Ordinary global matching consumes only an unambiguous first
    /// result. Canonical recovery may pixel-verify a bounded set because
    /// repeated ground texture can rank the correct phase second or third.
    private func phaseIndependentGlobalCandidates(
        luma: [UInt8], width: Int, height: Int,
        floorLines: [CleanFloorLine], exclusions: [CGRect],
        allowedSegmentIDs: Set<Int>? = nil,
        allowedFeatureIDs: Set<Int>? = nil,
        maximumPredictedRowDistance: CGFloat? = 64
    ) -> [GroundGlobalPoseFit] {
        let storedFeatures = relocalizationFeatures.filter {
            (allowedSegmentIDs?.contains($0.segmentID) ?? true)
                && (allowedFeatureIDs?.contains($0.id) ?? true)
        }
        guard storedFeatures.count >= 6, !floorLines.isEmpty else { return [] }
        var observationsByPhase = [[GroundGlobalPoseObservation]](
            repeating: [], count: Self.featureSize
        )
        var seenPositions = Set<Int>()
        for (lineIndex, line) in floorLines.enumerated() {
            guard line.row >= 0, line.row + Self.featureHeight <= height else { continue }
            let candidates = storedFeatures.filter { feature in
                maximumPredictedRowDistance.map {
                    abs((feature.worldTopY + localCameraPosition.y)
                        - CGFloat(line.row)) <= $0
                } ?? true
            }
            guard candidates.count >= 6 else { continue }
            // Edge evidence can be interrupted by vegetation while the
            // texture below the established floor remains visible. Search
            // the complete detected span to nominate poses; masks, ordered
            // consensus, and independent full-pixel verification still decide
            // whether a proposal is accepted.
            for evidence in [line.xRange] where evidence.count >= Self.featureWidth {
                let lower = max(0, evidence.lowerBound)
                let upper = min(width - Self.featureWidth, evidence.upperBound - Self.featureWidth + 1)
                guard upper >= lower else { continue }
                for x in lower...upper {
                    let positionKey = lineIndex * width + x
                    guard seenPositions.insert(positionKey).inserted else { continue }
                    let rect = CGRect(x: x, y: line.row,
                        width: Self.featureWidth, height: Self.featureHeight)
                    guard !exclusions.contains(where: { $0.intersects(rect) }) else { continue }
                    let patch = extractPatch(from: luma, width: width, x: x, y: line.row)
                    let descriptor = normalizedDescriptor(patch)
                    guard descriptorEnergy(descriptor) >= 6 else { continue }
                    var ranked = [(feature: GlobalFeature, error: CGFloat)]()
                    for candidate in candidates {
                        let error = descriptorDistance(descriptor, candidate.descriptor)
                        guard error <= Self.descriptorMatchThreshold + 4 else { continue }
                        ranked.append((candidate, error))
                        ranked.sort { $0.error < $1.error }
                        if ranked.count > 3 { ranked.removeLast() }
                    }
                    let featureID = -(positionKey + 1)
                    observationsByPhase[x % Self.featureSize].append(contentsOf:
                        ranked.map { candidate in
                            GroundGlobalPoseObservation(
                                featureID: featureID,
                                currentSegmentID: -(lineIndex + 1),
                                globalID: candidate.feature.id,
                                globalSegmentID: candidate.feature.segmentID,
                                globalWorldX: candidate.feature.worldX,
                                imageX: CGFloat(x),
                                cameraPosition: CGPoint(
                                    x: candidate.feature.worldX - CGFloat(x),
                                    y: CGFloat(line.row) - candidate.feature.worldTopY
                                ),
                                descriptorError: candidate.error
                            )
                        }
                    )
                }
            }
        }
        let fits = observationsByPhase.compactMap {
            GroundGlobalPoseRANSAC.fit($0, minimumInliers: 6, minimumHorizontalSpread: 96)
        }.sorted {
            if $0.inliers.count != $1.inliers.count {
                return $0.inliers.count > $1.inliers.count
            }
            return meanDescriptorError($0) < meanDescriptorError($1)
        }
        return fits
    }

    private func verifiedCanonicalPhaseFit(
        luma: [UInt8], width: Int, height: Int,
        floorLines: [CleanFloorLine], exclusions: [CGRect],
        allowedSegmentIDs: Set<Int>, allowedFeatureIDs: Set<Int>,
        maximumPredictedRowDistance: CGFloat?
    ) -> GroundGlobalPoseFit? {
        let firstViewIDs = allowedFeatureIDs.intersection(canonicalFirstViewIDs)
        if firstViewIDs.count >= 6, firstViewIDs != allowedFeatureIDs {
            let firstViewCandidates = phaseIndependentGlobalCandidates(luma: luma,
                width: width, height: height, floorLines: floorLines, exclusions: exclusions,
                allowedSegmentIDs: allowedSegmentIDs, allowedFeatureIDs: firstViewIDs,
                maximumPredictedRowDistance: maximumPredictedRowDistance)
            for candidate in firstViewCandidates.prefix(6) {
                var fixed = candidate
                fixed.verificationFeatureIDs = firstViewIDs
                if let verified = verifiedGlobalFit(fixed, luma: luma, width: width,
                    height: height, floorLines: floorLines, exclusions: exclusions) {
                    return verified
                }
            }
        }
        let candidates = phaseIndependentGlobalCandidates(
            luma: luma,
            width: width,
            height: height,
            floorLines: floorLines,
            exclusions: exclusions,
            allowedSegmentIDs: allowedSegmentIDs,
            allowedFeatureIDs: allowedFeatureIDs,
            maximumPredictedRowDistance: maximumPredictedRowDistance
        )
        let candidateLimit = 6
        if traceEnabled, !candidates.isEmpty {
            let summary = candidates.prefix(candidateLimit).map {
                "\(Int($0.cameraPosition.x)),\(Int($0.cameraPosition.y)):\($0.inliers.count)"
            }.joined(separator: ";")
            traceLog.notice(
                "canonical-phase-candidates total=\(candidates.count, privacy: .public) ranked=\(summary, privacy: .public)"
            )
        }
        for candidate in candidates.prefix(candidateLimit) {
            if let verified = verifiedGlobalFit(
                candidate,
                luma: luma,
                width: width,
                height: height,
                floorLines: floorLines,
                exclusions: exclusions
            ) {
                return verified
            }
        }
        return nil
    }

    /// A persistent line may extend through an entire room. Later odometry
    /// drift can therefore add duplicate cells to the same Line ID, making
    /// "oldest line" insufficient as a stable place signature. Preserve a
    /// bounded cohort of the first immutable features recorded on each old
    /// line for canonical loop challenges; ordinary global search still uses
    /// the complete feature set.
    private func canonicalAnchorFeatureIDs(segmentIDs: Set<Int>) -> Set<Int> {
        guard !segmentIDs.isEmpty else { return [] }
        return Set(canonicalAnchorFeatures.values.compactMap {
            segmentIDs.contains($0.segmentID) ? $0.id : nil
        })
    }

    private func meanDescriptorError(_ fit: GroundGlobalPoseFit) -> CGFloat {
        guard !fit.inliers.isEmpty else { return .infinity }
        return fit.inliers.reduce(0) { $0 + $1.descriptorError }
            / CGFloat(fit.inliers.count)
    }

    private func verifiedGlobalFit(
        _ fit: GroundGlobalPoseFit?, luma: [UInt8], width: Int, height: Int,
        floorLines: [CleanFloorLine], exclusions: [CGRect],
        refinementRadius: Int = 8
    ) -> GroundGlobalPoseFit? {
        // A fine X/Y phase can be wrong even when descriptors cannot nominate
        // a different whole cell. Known segment identity permits an independent
        // texture search around the current pose in that case.
        let seed = fit?.cameraPosition ?? localCameraPosition
        let storedFeatures = relocalizationFeatures.filter {
            fit?.verificationFeatureIDs?.contains($0.id) ?? true
        }
        let segments = fit.map { Set($0.inliers.map(\.globalSegmentID)) }
            ?? Set(active.filter { $0.globalFeatureID != nil }.map(\.segmentID))
        func quality(at pose: CGPoint) -> GroundGlobalCorrectionPolicy.Quality? {
            var errors = [String: (x: CGFloat, error: CGFloat)]()
            for feature in storedFeatures where segments.contains(feature.segmentID) {
                let x = Int((feature.worldX - pose.x).rounded())
                let y = Int((feature.worldTopY + pose.y).rounded())
                let rect = CGRect(x: x, y: y, width: Self.featureWidth, height: Self.featureHeight)
                guard x >= 8, x + Self.featureWidth + 8 <= width, y >= 0,
                      y + Self.featureHeight <= height,
                      floorLines.contains(where: { abs($0.row - y) <= 4 && $0.xRange.contains(x) }),
                      !exclusions.contains(where: { $0.intersects(rect) }) else { continue }
                let patch = extractPatch(from: luma, width: width, x: x, y: y)
                let error = GroundCameraSolver.patchError(feature.matchingPatch, patch)
                // Multiple appearances of one cell count as one physical vote.
                let key = "\(feature.worldX),\(feature.worldTopY)"
                if error < (errors[key]?.error ?? .infinity) || errors[key] == nil {
                    errors[key] = (CGFloat(x), error)
                }
            }
            guard !errors.isEmpty else { return nil }
            let inliers = errors.values.filter { $0.error <= 12 }
            let spread = (inliers.map(\.x).max() ?? 0) - (inliers.map(\.x).min() ?? 0)
            return GroundGlobalCorrectionPolicy.Quality(tested: errors.count, inliers: inliers.count,
                horizontalSpread: spread,
                error: errors.values.reduce(CGFloat.zero) { $0 + min(32, $1.error) } / CGFloat(errors.count))
        }
        // Keep one visibility set across all candidate offsets. A candidate
        // cannot improve its score by moving a difficult patch behind a mask
        // or just beyond a detected line. The broad line gate admits a seed
        // whose Y is wrong by the full refinement radius; each candidate then
        // has to land the tile on a current clean line.
        let eligible = storedFeatures.filter { feature in
            guard segments.contains(feature.segmentID) else { return false }
            let x = Int((feature.worldX - seed.x).rounded())
            let y = Int((feature.worldTopY + seed.y).rounded())
            let band = CGRect(x: x - refinementRadius, y: y - refinementRadius,
                width: Self.featureWidth + refinementRadius * 2,
                height: Self.featureHeight + refinementRadius * 2)
            let insideImage = x >= refinementRadius
                && x + Self.featureWidth + refinementRadius <= width
                && y >= refinementRadius
                && y + Self.featureHeight + refinementRadius <= height
            let possibleRange = (x - refinementRadius)...(x + Self.featureWidth + refinementRadius - 1)
            let nearLine = floorLines.contains {
                abs($0.row - y) <= refinementRadius + 4
                    && $0.xRange.overlaps(possibleRange)
            }
            let clear = !exclusions.contains { $0.intersects(band) }
            return insideImage && nearLine && clear
        }
        func refinementQuality(_ dx: Int, _ dy: Int) -> GroundGlobalCorrectionPolicy.Quality? {
            var errors = [String: (x: CGFloat, error: CGFloat)]()
            for feature in eligible {
                let x = Int((feature.worldX - seed.x).rounded()) - dx
                let y = Int((feature.worldTopY + seed.y).rounded()) + dy
                let supported = floorLines.contains {
                    abs($0.row - y) <= 4
                        && $0.xRange.contains(x)
                        && $0.xRange.contains(x + Self.featureWidth - 1)
                }
                let error: CGFloat
                if supported {
                    let patch = extractPatch(from: luma, width: width, x: x, y: y)
                    error = GroundCameraSolver.patchError(feature.matchingPatch, patch)
                } else {
                    // Missing line support is evidence against this candidate,
                    // not permission to silently reduce its tested tile count.
                    error = 32
                }
                let key = "\(feature.worldX),\(feature.worldTopY)"
                if error < (errors[key]?.error ?? .infinity) || errors[key] == nil {
                    errors[key] = (CGFloat(x), error)
                }
            }
            guard !errors.isEmpty else { return nil }
            var inlierCount = 0
            var minimumInlierX = CGFloat.greatestFiniteMagnitude
            var maximumInlierX = -CGFloat.greatestFiniteMagnitude
            var totalError = CGFloat.zero
            for value in errors.values {
                totalError += min(CGFloat(32), value.error)
                guard value.error <= 12 else { continue }
                inlierCount += 1
                minimumInlierX = min(minimumInlierX, value.x)
                maximumInlierX = max(maximumInlierX, value.x)
            }
            let horizontalSpread = inlierCount > 0 ? maximumInlierX - minimumInlierX : 0
            let averageError = totalError / CGFloat(errors.count)
            return GroundGlobalCorrectionPolicy.Quality(
                tested: errors.count,
                inliers: inlierCount,
                horizontalSpread: horizontalSpread,
                error: averageError
            )
        }
        let refinement = GroundPoseRefinementSearch.offset(radius: refinementRadius,
            quality: refinementQuality) ?? .init(dx: 0, dy: 0)
        let refined = CGPoint(x: seed.x + CGFloat(refinement.dx),
                              y: seed.y + CGFloat(refinement.dy))
        let displacement = hypot(refined.x - localCameraPosition.x,
                                 refined.y - localCameraPosition.y)
        let candidateQuality = quality(at: refined)
        let currentQuality = quality(at: localCameraPosition)
        let accepted = GroundGlobalCorrectionPolicy.accepts(
            candidate: candidateQuality,
            current: currentQuality,
            displacement: displacement,
            hasTrustedPose: globalAnchorValid
        )
        if traceEnabled, fit != nil, displacement > 4 {
            func summary(_ value: GroundGlobalCorrectionPolicy.Quality?) -> String {
                guard let value else { return "nil" }
                return "\(value.inliers)/\(value.tested):\(value.horizontalSpread):\(value.error)"
            }
            let candidateSummary = summary(candidateQuality)
            let currentSummary = summary(currentQuality)
            traceLog.notice(
                "global-verify seed=\(seed.x, privacy: .public),\(seed.y, privacy: .public) refined=\(refined.x, privacy: .public),\(refined.y, privacy: .public) local=\(self.localCameraPosition.x, privacy: .public),\(self.localCameraPosition.y, privacy: .public) eligible=\(eligible.count, privacy: .public) candidate=\(candidateSummary, privacy: .public) current=\(currentSummary, privacy: .public) accepted=\(accepted, privacy: .public)"
            )
        }
        guard fit != nil || ((refinement.dx != 0 || refinement.dy != 0) && eligible.count >= 6),
              accepted else { return nil }
        return GroundGlobalPoseFit(cameraPosition: refined,
            featureIDs: fit?.featureIDs ?? Set(active.filter { $0.globalFeatureID != nil }.map(\.id)),
            globalIDs: fit?.globalIDs ?? Set(eligible.map { $0.id }), inliers: fit?.inliers ?? [],
            verificationFeatureIDs: fit?.verificationFeatureIDs)
    }

    private func poseObservation(
        feature: ActiveFeature,
        global: GlobalFeature,
        descriptorError: CGFloat
    ) -> GroundGlobalPoseObservation {
        GroundGlobalPoseObservation(
            featureID: feature.id,
            currentSegmentID: feature.segmentID,
            globalID: global.id,
            globalSegmentID: global.segmentID,
            globalWorldX: global.worldX,
            imageX: CGFloat(feature.x),
            cameraPosition: CGPoint(
                x: global.worldX - CGFloat(feature.x),
                y: CGFloat(feature.y) - global.worldTopY
            ),
            descriptorError: descriptorError
        )
    }

    private func applyGlobalFit(_ fit: GroundGlobalPoseFit) {
        let globalByFeature = Dictionary(
            uniqueKeysWithValues: fit.inliers.map { ($0.featureID, $0.globalID) }
        )
        for index in active.indices {
            guard let globalID = globalByFeature[active[index].id] else { continue }
            active[index].globalFeatureID = globalID
            active[index].classification = .globalMatch
            observeGlobalFeature(globalID)
        }
    }

    /// A pose-consistent ordered sequence identifies its original persistent
    /// Line ID. Duplicate drift-created lines are discarded before the next
    /// projection, restoring original cell IDs and atlas origin.
    private func reassociateSegments(using fit: GroundGlobalPoseFit) {
        struct SegmentPair: Hashable {
            let current: Int
            let global: Int
        }
        let groups = Dictionary(grouping: fit.inliers) {
            SegmentPair(current: $0.currentSegmentID, global: $0.globalSegmentID)
        }
        let confirmed = groups.filter { pair, observations in
            pair.current != pair.global
                && observations.count >= 3
                && groundSegments.contains { $0.id == pair.global }
        }.sorted { $0.value.count > $1.value.count }
        var removedSegmentIDs = Set<Int>()
        for (pair, _) in confirmed where !removedSegmentIDs.contains(pair.current) {
            removedSegmentIDs.insert(pair.current)
        }
        guard !removedSegmentIDs.isEmpty else { return }
        let removedCellIDs = Set(groundSegments.filter {
            removedSegmentIDs.contains($0.id)
        }.flatMap { $0.cellIDs.values })
        groundSegments.removeAll { removedSegmentIDs.contains($0.id) }
        active.removeAll {
            removedSegmentIDs.contains($0.segmentID) || removedCellIDs.contains($0.id)
        }
        for id in Array(globalFeatures.keys) where
            globalFeatures[id].map({ removedSegmentIDs.contains($0.segmentID) }) == true {
            // Reassociation removes duplicate geometry from the visible map,
            // not the immutable place memory. A mistaken intermediate Line
            // pairing must not erase the original cohort needed to close the
            // route later. Hidden archived features still need full-strip
            // verification before they can move the camera.
            if let feature = globalFeatures[id] {
                retiredGlobalFeatures[id] = feature
            }
            globalFeatures[id] = nil
        }
    }

    private func observeGlobalFeature(_ id: Int) {
        if var feature = globalFeatures[id] {
            feature.observationCount += 1
            feature.lastSeenFrame = frameIndex
            globalFeatures[id] = feature
        } else if var feature = retiredGlobalFeatures[id] {
            feature.observationCount += 1
            feature.lastSeenFrame = frameIndex
            retiredGlobalFeatures[id] = feature
        }
    }

    private func featureOutput(_ feature: ActiveFeature) -> GroundHypothesisFeature {
        GroundHypothesisFeature(
            id: feature.id,
            segmentID: feature.segmentID,
            sequenceIndex: feature.sequenceIndex,
            imageRect: CGRect(
                x: feature.x,
                y: feature.y,
                width: Self.featureWidth,
                height: Self.featureHeight
            ),
            classification: feature.classification,
            motionResidual: feature.motionResidual,
            photometricError: feature.photometricError
        )
    }

    private func bestMatch(
        for feature: ActiveFeature,
        in luma: [UInt8],
        width: Int,
        height: Int,
        floorLines: [CleanFloorLine]
    ) -> Match? {
        let expectedX = feature.x + Int(lastImageTranslation.dx.rounded())
        let expectedY = feature.y + Int(lastImageTranslation.dy.rounded())
        var bestX = 0
        var bestY = 0
        var bestError = Int.max

        for line in floorLines where abs(line.row - expectedY) <= Self.verticalSearchRadius {
            let y = line.row
            guard y >= 0, y + Self.featureHeight <= height else { continue }
            let lowerX = max(
                0,
                max(line.xRange.lowerBound, expectedX - Self.horizontalSearchRadius)
            )
            let upperX = min(
                width - Self.featureSize,
                min(
                    line.xRange.upperBound - Self.featureSize + 1,
                    expectedX + Self.horizontalSearchRadius
                )
            )
            guard upperX >= lowerX else { continue }
            for x in stride(from: lowerX, through: upperX, by: 2) {
                let error = patchError(
                    feature.patch,
                    in: luma,
                    width: width,
                    height: height,
                    x: x,
                    y: y,
                    stoppingAt: bestError
                )
                if error < bestError {
                    bestError = error
                    bestX = x
                    bestY = y
                }
            }
        }
        guard bestError < Int.max else { return nil }
        let coarseX = bestX
        let coarseY = bestY
        guard let matchedLine = floorLines.first(where: {
            $0.row == coarseY && $0.xRange.contains(coarseX)
        }) else { return nil }
        let refinedLowerX = max(
            0,
            max(
                matchedLine.xRange.lowerBound,
                min(coarseX - 2, expectedX - 4)
            )
        )
        let refinedUpperX = min(
            width - Self.featureSize,
            min(
                matchedLine.xRange.upperBound - Self.featureSize + 1,
                max(coarseX + 2, expectedX + 4)
            )
        )
        let refinedLowerY = max(0, coarseY - Self.verticalRefinementRadius)
        let refinedUpperY = min(
            height - Self.featureHeight,
            coarseY + Self.verticalRefinementRadius
        )
        if refinedUpperX >= refinedLowerX, refinedUpperY >= refinedLowerY {
            for y in refinedLowerY...refinedUpperY {
                for x in refinedLowerX...refinedUpperX {
                    let error = patchError(
                        feature.patch,
                        in: luma,
                        width: width,
                        height: height,
                        x: x,
                        y: y,
                        stoppingAt: bestError
                    )
                    if error < bestError {
                        bestError = error
                        bestX = x
                        bestY = y
                    }
                }
            }
        }
        return Match(
            feature: feature,
            x: bestX,
            y: bestY,
            photometricError: CGFloat(bestError) / CGFloat(Self.featurePixelCount),
            imageTranslation: CGVector(dx: bestX - feature.x, dy: bestY - feature.y),
            detectedLineRow: coarseY
        )
    }

    /// Preserve representation from every visible line while limiting the
    /// expensive recovery search. Within each line, candidates are sampled
    /// across the full left-to-right span rather than taking the first tiles.
    private func recoveryFeatureCandidates(
        from features: [ActiveFeature]
    ) -> [ActiveFeature] {
        let eligible = features.filter {
            $0.referenceValid && descriptorEnergy(normalizedDescriptor($0.patch)) >= 6
        }
        guard eligible.count > Self.recoveryFeatureLimit else { return eligible }
        let groups = Dictionary(grouping: eligible, by: \.segmentID)
            .sorted { $0.key < $1.key }
            .map { $0.value.sorted { $0.sequenceIndex < $1.sequenceIndex } }
        guard !groups.isEmpty else { return [] }

        let quota = max(1, Self.recoveryFeatureLimit / groups.count)
        var selected = [ActiveFeature]()
        for group in groups {
            let count = min(quota, group.count)
            guard count > 0 else { continue }
            if count == 1 {
                selected.append(group[group.count / 2])
                continue
            }
            for slot in 0..<count {
                let index = slot * (group.count - 1) / (count - 1)
                selected.append(group[index])
            }
        }
        if selected.count < Self.recoveryFeatureLimit {
            let selectedIDs = Set(selected.map(\.id))
            let remainder = eligible
                .filter { !selectedIDs.contains($0.id) }
                .sorted { $0.x < $1.x }
            let available = Self.recoveryFeatureLimit - selected.count
            if remainder.count <= available {
                selected.append(contentsOf: remainder)
            } else if available > 0 {
                for slot in 0..<available {
                    let index = available == 1
                        ? remainder.count / 2
                        : slot * (remainder.count - 1) / (available - 1)
                    selected.append(remainder[index])
                }
            }
        }
        return Array(selected.prefix(Self.recoveryFeatureLimit))
    }

    /// Recover one camera transform, not one unrelated minimum per tile.
    /// Clean rows propose Y; the persistent, ordered texture tiles vote on X.
    private func recoveryConsensus(
        features: [ActiveFeature],
        in luma: [UInt8],
        width: Int,
        height: Int,
        floorLines: [CleanFloorLine],
        exclusions: [CGRect],
        horizontalSearchRadius: Int,
        verticalSearchRadius: Int
    ) -> RecoveryAttempt {
        guard features.count >= 4, !floorLines.isEmpty else {
            return RecoveryAttempt(
                matches: [], fit: nil, translationsTested: 0,
                bestInlierCount: 0, ambiguous: false
            )
        }
        let predictedX = Int(lastImageTranslation.dx.rounded())
        let predictedY = Int(lastImageTranslation.dy.rounded())
        var verticalTranslations = Set<Int>()
        for feature in features {
            let expectedY = feature.y + predictedY
            for line in floorLines where abs(line.row - expectedY) <= verticalSearchRadius {
                for refinement in (-Self.verticalRefinementRadius)...Self.verticalRefinementRadius {
                    let translation = line.row + refinement - feature.y
                    if abs(translation - predictedY) <= verticalSearchRadius {
                        verticalTranslations.insert(translation)
                    }
                }
            }
        }
        guard !verticalTranslations.isEmpty else {
            return RecoveryAttempt(
                matches: [], fit: nil, translationsTested: 0,
                bestInlierCount: 0, ambiguous: false
            )
        }

        struct Candidate {
            var dx: Int
            var dy: Int
            var matches: [Match]
            var meanError: CGFloat
        }
        let maximumError = Int(
            (Self.promotionPhotometricError * CGFloat(Self.featurePixelCount)).rounded()
        )
        func evaluate(dx: Int, dy: Int) -> Candidate? {
            var matches = [Match]()
            var totalError: CGFloat = 0
            for feature in features {
                let x = feature.x + dx
                let y = feature.y + dy
                guard x >= 0, x + Self.featureWidth <= width,
                      y >= 0, y + Self.featureHeight <= height
                else { continue }
                let rect = CGRect(x: x, y: y, width: Self.featureWidth, height: Self.featureHeight)
                guard !exclusions.contains(where: { $0.intersects(rect) }) else { continue }
                let coveringLines = floorLines.filter {
                    abs($0.row - y) <= Self.verticalRefinementRadius
                        && x >= $0.xRange.lowerBound
                        && x + Self.featureWidth - 1 <= $0.xRange.upperBound
                }
                guard let line = coveringLines.min(by: {
                    abs($0.row - y) < abs($1.row - y)
                }) else { continue }
                let error = patchError(
                    feature.patch,
                    in: luma,
                    width: width,
                    height: height,
                    x: x,
                    y: y,
                    stoppingAt: maximumError + 1
                )
                guard error <= maximumError else { continue }
                let normalized = CGFloat(error) / CGFloat(Self.featurePixelCount)
                totalError += normalized
                matches.append(Match(
                    feature: feature,
                    x: x,
                    y: y,
                    photometricError: normalized,
                    imageTranslation: CGVector(dx: dx, dy: dy),
                    detectedLineRow: line.row
                ))
            }
            guard matches.count >= 4,
                  (matches.map(\.x).max() ?? 0) - (matches.map(\.x).min() ?? 0) >= 48
            else { return nil }
            return Candidate(
                dx: dx,
                dy: dy,
                matches: matches,
                meanError: totalError / CGFloat(matches.count)
            )
        }
        func ranksBefore(_ left: Candidate, _ right: Candidate) -> Bool {
            if left.matches.count != right.matches.count {
                return left.matches.count > right.matches.count
            }
            return left.meanError < right.meanError
        }

        var candidates = [Candidate]()
        var translationsTested = 0
        let lowerX = predictedX - horizontalSearchRadius
        let upperX = predictedX + horizontalSearchRadius
        for dy in verticalTranslations.sorted() {
            for dx in stride(from: lowerX, through: upperX, by: 2) {
                translationsTested += 1
                if let candidate = evaluate(dx: dx, dy: dy) {
                    candidates.append(candidate)
                }
            }
        }
        guard let coarse = candidates.min(by: ranksBefore) else {
            return RecoveryAttempt(
                matches: [], fit: nil, translationsTested: translationsTested,
                bestInlierCount: 0, ambiguous: false
            )
        }
        for dy in (coarse.dy - 1)...(coarse.dy + 1) {
            for dx in (coarse.dx - 2)...(coarse.dx + 2)
                where dx >= lowerX && dx <= upperX {
                translationsTested += 1
                if let candidate = evaluate(dx: dx, dy: dy) {
                    candidates.append(candidate)
                }
            }
        }
        candidates.sort(by: ranksBefore)
        guard let best = candidates.first else {
            return RecoveryAttempt(
                matches: [], fit: nil, translationsTested: translationsTested,
                bestInlierCount: 0, ambiguous: false
            )
        }
        let ambiguous = candidates.dropFirst().contains { rival in
            (abs(rival.dx - best.dx) > 2 || abs(rival.dy - best.dy) > 2)
                && rival.matches.count >= best.matches.count - 1
                && rival.meanError <= best.meanError * 1.08 + 0.5
        }
        let fit = ambiguous ? nil : GroundTranslationFit(
            imageTranslation: CGVector(dx: best.dx, dy: best.dy),
            inlierIDs: Set(best.matches.map { $0.feature.id }),
            residualRMS: 0
        )
        return RecoveryAttempt(
            matches: best.matches,
            fit: fit,
            translationsTested: translationsTested,
            bestInlierCount: best.matches.count,
            ambiguous: ambiguous
        )
    }

    /// An 8x6, mean- and contrast-normalized descriptor. Pooling each 2x2
    /// source block suppresses single-pixel noise. Normalizing mean absolute
    /// contrast keeps the global proposal stable when one ground tile moves
    /// through the game's screen-space radial lighting field.
    private func normalizedDescriptor(_ patch: [UInt8]) -> [Int16] {
        guard patch.count == Self.featurePixelCount else { return [] }
        let pooledHeight = Self.featureHeight / 2
        var pooled = [Int](repeating: 0, count: 8 * pooledHeight)
        for cellY in 0..<pooledHeight {
            for cellX in 0..<8 {
                var sum = 0
                for y in 0..<2 {
                    let source = (cellY * 2 + y) * Self.featureWidth + cellX * 2
                    sum += Int(patch[source]) + Int(patch[source + 1])
                }
                pooled[cellY * 8 + cellX] = sum / 4
            }
        }
        let mean = pooled.reduce(0, +) / max(1, pooled.count)
        let centered = pooled.map { $0 - mean }
        let meanAbsoluteContrast = CGFloat(centered.reduce(0) { $0 + abs($1) })
            / CGFloat(max(1, centered.count))
        guard meanAbsoluteContrast >= 1 else { return centered.map(Int16.init) }
        let scale = 32 / meanAbsoluteContrast
        return centered.map {
            Int16(max(-127, min(127, Int((CGFloat($0) * scale).rounded()))))
        }
    }

    private func descriptorDistance(_ left: [Int16], _ right: [Int16]) -> CGFloat {
        guard left.count == right.count, !left.isEmpty else { return .infinity }
        let total = zip(left, right).reduce(0) {
            $0 + abs(Int($1.0) - Int($1.1))
        }
        return CGFloat(total) / CGFloat(left.count)
    }

    private func descriptorEnergy(_ descriptor: [Int16]) -> CGFloat {
        guard !descriptor.isEmpty else { return 0 }
        return CGFloat(descriptor.reduce(0) { $0 + abs(Int($1)) })
            / CGFloat(descriptor.count)
    }

    private func worldDistance(_ feature: GlobalFeature, x: CGFloat, y: CGFloat) -> CGFloat {
        hypot(feature.worldX - x, feature.worldTopY - y)
    }


    private func extractPatch(from luma: [UInt8], width: Int, x: Int, y: Int) -> [UInt8] {
        GroundPixels.patch(luma, width: width, height: luma.count / max(1, width), x: x, y: y) ?? []
    }

    private func patchError(
        _ patch: [UInt8],
        in luma: [UInt8],
        width: Int,
        height: Int,
        x: Int,
        y: Int,
        stoppingAt best: Int
    ) -> Int {
        guard patch.count == Self.featurePixelCount,
              x >= 0, x + Self.featureWidth <= width,
              y >= 0, y + Self.featureHeight <= height
        else { return .max }
        var error = 0
        for row in 0..<Self.featureHeight {
            let source = row * Self.featureWidth
            let target = (y + row) * width + x
            for column in 0..<Self.featureWidth {
                error += abs(
                    Int(patch[source + column]) - Int(luma[target + column])
                )
            }
            if error >= best { return error }
        }
        return error
    }

    private static func paint(
        _ pixels: inout [UInt8],
        index: Int,
        color: (UInt8, UInt8, UInt8, UInt8)
    ) {
        let offset = index * 4
        pixels[offset] = color.0
        pixels[offset + 1] = color.1
        pixels[offset + 2] = color.2
        pixels[offset + 3] = color.3
    }

    private static func paintSegment(
        _ pixels: inout [UInt8],
        width: Int,
        height: Int,
        from start: CGPoint,
        to end: CGPoint,
        color: (UInt8, UInt8, UInt8, UInt8)
    ) {
        let steps = max(
            1,
            Int(max(abs(end.x - start.x), abs(end.y - start.y)).rounded(.up))
        )
        for step in 0...steps {
            let t = CGFloat(step) / CGFloat(steps)
            let x = Int((start.x + (end.x - start.x) * t).rounded())
            let y = Int((start.y + (end.y - start.y) * t).rounded())
            guard (0..<width).contains(x), (0..<height).contains(y) else { continue }
            paint(&pixels, index: y * width + x, color: color)
        }
    }
}
