import CoreGraphics
import Foundation

enum VisualRoomDirection: Int, Equatable, Hashable, Sendable {
    case left = -1
    case right = 1

    var opposite: Self { self == .left ? .right : .left }
    var label: String { self == .left ? "left" : "right" }
}

struct VisualRoomSnapshot: Equatable, Sendable {
    let roomID: Int
    let roomName: String
    let revision: UInt64
    let entryWorldPose: CGPoint
    let isTransitioning: Bool
    let transitionDirection: VisualRoomDirection?
    let transitionWorldPose: CGPoint?
    /// The continuous camera pose at the instant room ownership changed.
    /// Consumers use this instead of snapping to a separately inferred room
    /// placement; the room change itself must never move the camera.
    let activationWorldPose: CGPoint?
    /// Camera pose carried by low-resolution image motion while confirmed
    /// ground is unavailable. Room ownership does not change merely because
    /// this fallback is active.
    let coarseWorldPose: CGPoint?
    /// Changes whenever the floorless trajectory is rebased. A delayed
    /// full-resolution correction may apply only to the revision it measured.
    let coarsePoseRevision: UInt64
    let coarseMotionGrid: LowResolutionMotionGrid?
    let coarseMotionEstimate: LowResolutionRoomMotionEstimate?
    let coarsePlaceMatch: LowResolutionPlaceMatch?
    let coarseMotionIsControlling: Bool
    let portals: [VisualRoomPortal]
    let compositionBounds: VisualRoomCompositionBounds

    var statusText: String {
        guard isTransitioning, let transitionDirection else { return roomName }
        return "\(roomName) → \(transitionDirection.label)"
    }
}

struct VisualRoomPortal: Equatable, Sendable {
    let id: Int
    let leftRoomID: Int
    let rightRoomID: Int
    let leftDoorWorldX: CGFloat
    let rightDoorWorldX: CGFloat
    let leftDoorYRange: ClosedRange<CGFloat>
    let rightDoorYRange: ClosedRange<CGFloat>

    var connectorLength: CGFloat {
        max(0, rightDoorWorldX - leftDoorWorldX)
    }
}

struct VisualRoomCompositionBounds: Equatable, Sendable {
    var minimumWorldX: CGFloat?
    var maximumWorldX: CGFloat?

    static let unbounded = Self(minimumWorldX: nil, maximumWorldX: nil)
}

enum VisualRoomCompositionMask {
    static func omittedRects(
        bounds: VisualRoomCompositionBounds,
        cameraPosition: CGPoint,
        solveWidth: CGFloat,
        frameSize: CGSize
    ) -> [CGRect] {
        guard cameraPosition.x.isFinite,
              solveWidth.isFinite, solveWidth > 0,
              frameSize.width.isFinite, frameSize.width > 0,
              frameSize.height.isFinite, frameSize.height > 0 else { return [] }
        let pixelsPerWorldUnit = frameSize.width / solveWidth
        var result = [CGRect]()
        if let minimum = bounds.minimumWorldX, minimum.isFinite {
            let x = min(frameSize.width, max(0,
                (minimum - cameraPosition.x) * pixelsPerWorldUnit
            ))
            if x > 0 {
                result.append(CGRect(x: 0, y: 0, width: x, height: frameSize.height))
            }
        }
        if let maximum = bounds.maximumWorldX, maximum.isFinite {
            let x = min(frameSize.width, max(0,
                (maximum - cameraPosition.x) * pixelsPerWorldUnit
            ))
            if x < frameSize.width {
                result.append(CGRect(
                    x: x, y: 0,
                    width: frameSize.width - x,
                    height: frameSize.height
                ))
            }
        }
        return result
    }
}

struct VisualRoomPortalLayout: Equatable, Sendable {
    let targetEntryWorldPose: CGPoint
    let sourceDoorWorldX: CGFloat
    let targetDoorWorldX: CGFloat
    let sourceDoorYRange: ClosedRange<CGFloat>
    let targetDoorYRange: ClosedRange<CGFloat>

    /// A horizontal doorway cannot move the camera farther than the adjacent
    /// room in the nominated direction or by most of a screen vertically.
    /// Floorless odometry occasionally survives a transition with a large false Y
    /// shift; treating that sample as room geometry permanently separates the
    /// two atlas rooms. Reject it and let the edge-band fallback join them.
    static func plausibleContinuousArrival(
        _ candidate: CGPoint?,
        direction: VisualRoomDirection,
        departureWorldPose: CGPoint,
        solveWidth: CGFloat,
        solveHeight: CGFloat
    ) -> CGPoint? {
        guard let candidate else { return nil }
        let deltaX = candidate.x - departureWorldPose.x
        let signedTravel = deltaX * CGFloat(direction.rawValue)
        guard signedTravel >= 0,
              signedTravel <= solveWidth * 1.5,
              abs(candidate.y - departureWorldPose.y) <= solveHeight * 0.75 else {
            return nil
        }
        return candidate
    }

    static func make(
        direction: VisualRoomDirection,
        departureWorldPose: CGPoint,
        solveWidth: CGFloat,
        solveHeight: CGFloat,
        sourceBand: FrameEdgeBlackBand?,
        targetBand: FrameEdgeBlackBand?,
        measuredTravel: LowResolutionTransitionTravel?,
        continuousArrivalWorldPose: CGPoint? = nil
    ) -> Self {
        let sourceBandWidth = min(solveWidth * 0.72, max(
            0, (sourceBand?.widthFraction ?? 0) * solveWidth
        ))
        let targetBandWidth = min(solveWidth * 0.72, max(
            0, (targetBand?.widthFraction ?? 0) * solveWidth
        ))
        let sourceScreenX = direction == .left
            ? sourceBandWidth : solveWidth - sourceBandWidth
        let targetScreenX = direction == .left
            ? solveWidth - targetBandWidth : targetBandWidth
        let visibleTravel = max(0, solveWidth - sourceBandWidth - targetBandWidth)
        let measuredConnector: CGFloat?
        if let measuredTravel, measuredTravel.sampleCount >= 2,
           measuredTravel.meanConfidence >= 0.12 {
            measuredConnector = max(0, abs(measuredTravel.worldDeltaX) - visibleTravel)
        } else {
            measuredConnector = nil
        }
        let sourceDoorWorldX = departureWorldPose.x + sourceScreenX
        let targetEntry: CGPoint
        let targetDoorWorldX: CGFloat
        if let continuousArrivalWorldPose {
            targetEntry = continuousArrivalWorldPose
            targetDoorWorldX = continuousArrivalWorldPose.x + targetScreenX
        } else {
            let connector = min(
                solveWidth * 0.30,
                max(8, measuredConnector ?? solveWidth * 0.025)
            )
            targetDoorWorldX = sourceDoorWorldX
                + CGFloat(direction.rawValue) * connector
            targetEntry = CGPoint(
                x: targetDoorWorldX - targetScreenX,
                y: departureWorldPose.y
            )
        }
        func yRange(
            _ band: FrameEdgeBlackBand?,
            cameraY: CGFloat
        ) -> ClosedRange<CGFloat> {
            let normalized = band?.contentYRangeFraction ?? 0.38...0.68
            let lower = cameraY + normalized.lowerBound * solveHeight
            let upper = cameraY + normalized.upperBound * solveHeight
            return lower...upper
        }
        let alignedRanges = VisualRoomDoorGeometry.aligned(
            yRange(sourceBand, cameraY: departureWorldPose.y),
            yRange(targetBand, cameraY: targetEntry.y)
        )
        return Self(
            targetEntryWorldPose: targetEntry,
            sourceDoorWorldX: sourceDoorWorldX,
            targetDoorWorldX: targetDoorWorldX,
            sourceDoorYRange: alignedRanges.first,
            targetDoorYRange: alignedRanges.second
        )
    }
}

enum VisualRoomDoorGeometry {
    /// Door diagnostics mark the traversable opening, not the complete bright
    /// edge beside a black band. Both sides of one horizontal portal therefore
    /// share a center and a compact height even when surrounding artwork gives
    /// the two edge scans very different vertical spans.
    static let maximumHeight: CGFloat = 120

    static func aligned(
        _ first: ClosedRange<CGFloat>,
        _ second: ClosedRange<CGFloat>
    ) -> (first: ClosedRange<CGFloat>, second: ClosedRange<CGFloat>) {
        let firstCenter = (first.lowerBound + first.upperBound) * 0.5
        let secondCenter = (second.lowerBound + second.upperBound) * 0.5
        let center = min(firstCenter, secondCenter)
        let measuredHeight = min(
            first.upperBound - first.lowerBound,
            second.upperBound - second.lowerBound
        )
        let height = min(maximumHeight, max(24, measuredHeight))
        let range = (center - height * 0.5)...(center + height * 0.5)
        return (range, range)
    }
}

struct VisualRoomBoundaryEvidence: Equatable, Sendable {
    let leftRoomID: Int
    let rightRoomID: Int
    let leftOriginalEntryWorldPose: CGPoint
    let rightDepartureWorldPose: CGPoint
    let solveWidth: CGFloat
    let solveHeight: CGFloat
    let rightRoomLeftBand: FrameEdgeBlackBand?
    let leftRoomRightBand: FrameEdgeBlackBand?
}

struct LowResolutionRoomMotionEstimate: Equatable, Sendable {
    let direction: VisualRoomDirection
    let confidence: Double
    let screenShift: Int
}

struct LowResolutionMotionVector: Equatable, Sendable {
    let screenShiftX: Double
    let screenShiftY: Double
    let confidence: Double
}

enum LowResolutionTranslationRejection: String, Codable, Equatable, Sendable {
    case incompatibleGrid
    case insufficientTexture
    case insufficientOverlap
    case insufficientImprovement
    case insignificantSubcellMotion
}

struct LowResolutionTranslationDiagnostic: Equatable, Sendable {
    let motion: LowResolutionMotionVector?
    let narrowRejection: LowResolutionTranslationRejection?
    let wideRejection: LowResolutionTranslationRejection?

    var finalRejection: LowResolutionTranslationRejection? {
        motion == nil ? wideRejection ?? narrowRejection : nil
    }
}

struct LowResolutionTransitionTravel: Equatable, Sendable {
    let worldDeltaX: CGFloat
    let sampleCount: Int
    let meanConfidence: Double
}

struct LowResolutionCameraVelocitySeed: Equatable, Sendable {
    let velocity: CGVector
    let timestamp: TimeInterval
}

struct LowResolutionPlaceMatch: Equatable, Sendable {
    let keyframeID: Int
    let cameraPosition: CGPoint
    let score: Double
    let margin: Double
}

/// Sparse 64x36 place memory for recovering a known camera view after ground
/// tracking disappears. It is intentionally stricter than odometry: two
/// independently timed, room-local matches must agree before it can reanchor
/// the fallback pose.
struct LowResolutionPlaceMemory {
    private struct Entry {
        let id: Int
        let cameraPosition: CGPoint
        let width: Int
        let height: Int
        let normalizedLuma: [Double]
        let visible: [Bool]?
    }

    private struct Candidate {
        let entry: Entry
        let cameraPosition: CGPoint
        let score: Double
        let margin: Double
    }

    private struct Pending {
        let keyframeID: Int
        let cameraPosition: CGPoint
        let timestamp: TimeInterval
    }

    static let maximumEntriesPerRoom = 24
    static let maximumFullAlignmentCandidates = 3
    static let minimumEntryDistance: CGFloat = 80
    static let minimumEntryInterval: TimeInterval = 0.25
    // The one-way Tutorial -> Town Hacker trace crosses a remembered view at
    // about 0.92 cells per 50 ms capture. Searching at 10 Hz gives the strict
    // two-observation verifier two in-range views without asking gameplay to
    // pause at the return point.
    // Leave one 20 Hz timestamp's tolerance below 100 ms so floating-point
    // capture timestamps do not silently turn this back into a 150 ms search.
    static let searchInterval: TimeInterval = 0.09
    static let maximumScore = 0.34
    static let maximumSingleEntryScore = 0.20
    // Local contrast across a small visibility circle retains identity but
    // carries boundary residual. Two timed confirmations and minimum overlap
    // still apply; unrelated-circle regressions score above 1.0.
    static let maximumMaskedSingleEntryScore = 0.38
    static let minimumMargin = 0.08
    // Searches are already separated by `searchInterval`; an 80 ms lower
    // bound accepts adjacent nominal 100 ms samples despite binary timestamp
    // rounding while preserving independent-capture confirmation.
    static let confirmationInterval: ClosedRange<TimeInterval> = 0.08...0.60
    static let confirmationDistance: CGFloat = 24
    static let maximumAlignmentShift = 2
    // Place recognition is stricter than short-baseline motion. Reject the
    // dim feather around the gameplay vision circle; its spatial gain is not
    // stable scene texture and otherwise dominates a small overlap.
    private static let minimumVisibleLuma: UInt8 = 20
    private static let visibilityMaskDarkFraction = 0.12

    private var entries = [Int: [Entry]]()
    private var nextID = 1
    private var lastRememberedAt = [Int: TimeInterval]()
    private var lastSearchAt: TimeInterval?
    private var pending: Pending?

    mutating func reset() {
        entries.removeAll(keepingCapacity: true)
        nextID = 1
        lastRememberedAt.removeAll(keepingCapacity: true)
        lastSearchAt = nil
        pending = nil
    }

    mutating func remember(
        grid: LowResolutionMotionGrid?,
        roomID: Int,
        cameraPosition: CGPoint,
        timestamp: TimeInterval
    ) {
        guard let grid, timestamp.isFinite,
              cameraPosition.x.isFinite, cameraPosition.y.isFinite,
              lastRememberedAt[roomID].map({ timestamp - $0 >= Self.minimumEntryInterval }) ?? true
        else { return }
        let normalized = Self.normalized(grid)
        guard normalized.deviation
                >= LowResolutionRoomMotionTracker.minimumTextureDeviation else { return }
        var roomEntries = entries[roomID, default: []]
        guard roomEntries.allSatisfy({
            hypot(
                $0.cameraPosition.x - cameraPosition.x,
                $0.cameraPosition.y - cameraPosition.y
            ) >= Self.minimumEntryDistance
        }) else { return }
        roomEntries.append(Entry(
            id: nextID,
            cameraPosition: cameraPosition,
            width: grid.width,
            height: grid.height,
            normalizedLuma: normalized.values,
            visible: normalized.visible
        ))
        nextID += 1
        if roomEntries.count > Self.maximumEntriesPerRoom {
            // Keep the first room view as a durable return anchor. Retire the
            // oldest later keyframe when the bounded bank fills.
            roomEntries.remove(at: roomEntries.count > 1 ? 1 : 0)
        }
        entries[roomID] = roomEntries
        lastRememberedAt[roomID] = timestamp
    }

    mutating func observe(
        grid: LowResolutionMotionGrid?,
        roomID: Int,
        solveWidth: CGFloat,
        timestamp: TimeInterval
    ) -> LowResolutionPlaceMatch? {
        guard let grid, timestamp.isFinite, solveWidth.isFinite, solveWidth > 0,
              lastSearchAt.map({ timestamp - $0 >= Self.searchInterval }) ?? true,
              let roomEntries = entries[roomID], !roomEntries.isEmpty else {
            return nil
        }
        lastSearchAt = timestamp
        let normalizedCurrent = Self.normalized(grid)
        guard normalizedCurrent.deviation
                >= LowResolutionRoomMotionTracker.minimumTextureDeviation else {
            pending = nil
            return nil
        }
        let pixelScale = solveWidth / CGFloat(max(1, grid.width))
        let shortlist = roomEntries.compactMap { entry -> (Entry, Double)? in
            guard entry.width == grid.width, entry.height == grid.height else {
                return nil
            }
            return (
                entry,
                Self.quickScore(
                    reference: entry.normalizedLuma,
                    current: normalizedCurrent.values,
                    referenceVisible: entry.visible,
                    currentVisible: normalizedCurrent.visible,
                    width: grid.width,
                    height: grid.height
                )
            )
        }.sorted { $0.1 < $1.1 }
            .prefix(Self.maximumFullAlignmentCandidates)
        let ranked = shortlist.compactMap { shortlisted -> (Entry, Alignment)? in
            let entry = shortlisted.0
            return Self.alignment(
                from: entry,
                to: normalizedCurrent.values,
                currentVisible: normalizedCurrent.visible
            ).map { (entry, $0) }
        }.sorted { $0.1.score < $1.1.score }
        guard let best = ranked.first else {
            pending = nil
            return nil
        }
        let secondScore = ranked.dropFirst().first?.1.score
        let margin = secondScore.map { $0 - best.1.score } ?? .infinity
        let scoreLimit = secondScore == nil
            ? (normalizedCurrent.visible == nil
                ? Self.maximumSingleEntryScore
                : Self.maximumMaskedSingleEntryScore)
            : Self.maximumScore
        guard best.1.score <= scoreLimit,
              secondScore == nil || margin >= Self.minimumMargin else {
            pending = nil
            return nil
        }
        let position = CGPoint(
            x: best.0.cameraPosition.x
                - CGFloat(best.1.shiftX) * pixelScale,
            y: best.0.cameraPosition.y
                + CGFloat(best.1.shiftY) * pixelScale
        )
        let candidate = Candidate(
            entry: best.0,
            cameraPosition: position,
            score: best.1.score,
            margin: margin
        )
        guard let prior = pending,
              prior.keyframeID == candidate.entry.id,
              Self.confirmationInterval.contains(timestamp - prior.timestamp),
              hypot(
                prior.cameraPosition.x - candidate.cameraPosition.x,
                prior.cameraPosition.y - candidate.cameraPosition.y
              ) <= Self.confirmationDistance else {
            pending = Pending(
                keyframeID: candidate.entry.id,
                cameraPosition: candidate.cameraPosition,
                timestamp: timestamp
            )
            return nil
        }
        pending = nil
        return LowResolutionPlaceMatch(
            keyframeID: candidate.entry.id,
            cameraPosition: candidate.cameraPosition,
            score: candidate.score,
            margin: candidate.margin
        )
    }

    mutating func clearPending() {
        lastSearchAt = nil
        pending = nil
    }

    private struct Alignment {
        let shiftX: Int
        let shiftY: Int
        let score: Double
    }

    private static func alignment(
        from reference: Entry,
        to current: [Double],
        currentVisible: [Bool]?
    ) -> Alignment? {
        guard reference.normalizedLuma.count == current.count,
              reference.width > maximumAlignmentShift * 2 + 8,
              reference.height > maximumAlignmentShift * 2 + 8 else { return nil }
        let xRange = (maximumAlignmentShift + 2)..<(reference.width - maximumAlignmentShift - 2)
        let yRange = max(maximumAlignmentShift, reference.height / 6)..<min(
            reference.height - maximumAlignmentShift,
            reference.height * 5 / 6
        )
        func score(shiftX: Int, shiftY: Int) -> Double {
            var referenceSum = 0.0
            var currentSum = 0.0
            var referenceSquareSum = 0.0
            var currentSquareSum = 0.0
            var count = 0
            var available = 0
            for y in yRange {
                for x in xRange {
                    available += 1
                    let referenceIndex = y * reference.width + x
                    let currentIndex = (y + shiftY) * reference.width + x + shiftX
                    if reference.visible?[referenceIndex] == false
                        || currentVisible?[currentIndex] == false {
                        continue
                    }
                    let referenceValue = reference.normalizedLuma[referenceIndex]
                    let currentValue = current[currentIndex]
                    referenceSum += referenceValue
                    currentSum += currentValue
                    referenceSquareSum += referenceValue * referenceValue
                    currentSquareSum += currentValue * currentValue
                    count += 1
                }
            }
            guard count >= max(24, available / 10) else { return .infinity }
            let divisor = Double(count)
            let referenceMean = referenceSum / divisor
            let currentMean = currentSum / divisor
            let referenceDeviation = sqrt(max(
                0, referenceSquareSum / divisor - referenceMean * referenceMean
            ))
            let currentDeviation = sqrt(max(
                0, currentSquareSum / divisor - currentMean * currentMean
            ))
            guard referenceDeviation >= 0.1, currentDeviation >= 0.1 else {
                return .infinity
            }
            var error = 0.0
            for y in yRange {
                for x in xRange {
                    let referenceIndex = y * reference.width + x
                    let currentIndex = (y + shiftY) * reference.width + x + shiftX
                    if reference.visible?[referenceIndex] == false
                        || currentVisible?[currentIndex] == false {
                        continue
                    }
                    error += abs(
                        (reference.normalizedLuma[referenceIndex] - referenceMean)
                            / referenceDeviation
                            - (current[currentIndex] - currentMean) / currentDeviation
                    )
                }
            }
            return error / divisor
        }
        var best: Alignment?
        for shiftY in -maximumAlignmentShift...maximumAlignmentShift {
            for shiftX in -maximumAlignmentShift...maximumAlignmentShift {
                let candidateScore = score(shiftX: shiftX, shiftY: shiftY)
                guard candidateScore.isFinite else { continue }
                let candidate = Alignment(
                    shiftX: shiftX,
                    shiftY: shiftY,
                    score: candidateScore
                )
                if best.map({ candidate.score < $0.score }) ?? true {
                    best = candidate
                }
            }
        }
        return best
    }

    private static func quickScore(
        reference: [Double],
        current: [Double],
        referenceVisible: [Bool]?,
        currentVisible: [Bool]?,
        width: Int,
        height: Int
    ) -> Double {
        guard reference.count == current.count,
              reference.count == width * height else { return .infinity }
        var referenceSum = 0.0
        var currentSum = 0.0
        var referenceSquareSum = 0.0
        var currentSquareSum = 0.0
        var count = 0
        var available = 0
        for y in stride(from: max(2, height / 6), to: min(height - 2, height * 5 / 6), by: 3) {
            for x in stride(from: 4, to: width - 4, by: 4) {
                available += 1
                let index = y * width + x
                if referenceVisible?[index] == false
                    || currentVisible?[index] == false {
                    continue
                }
                let referenceValue = reference[index]
                let currentValue = current[index]
                referenceSum += referenceValue
                currentSum += currentValue
                referenceSquareSum += referenceValue * referenceValue
                currentSquareSum += currentValue * currentValue
                count += 1
            }
        }
        guard count >= max(12, available / 10) else { return .infinity }
        let divisor = Double(count)
        let referenceMean = referenceSum / divisor
        let currentMean = currentSum / divisor
        let referenceDeviation = sqrt(max(
            0, referenceSquareSum / divisor - referenceMean * referenceMean
        ))
        let currentDeviation = sqrt(max(
            0, currentSquareSum / divisor - currentMean * currentMean
        ))
        guard referenceDeviation >= 0.1, currentDeviation >= 0.1 else {
            return .infinity
        }
        var error = 0.0
        for y in stride(from: max(2, height / 6), to: min(height - 2, height * 5 / 6), by: 3) {
            for x in stride(from: 4, to: width - 4, by: 4) {
                let index = y * width + x
                if referenceVisible?[index] == false
                    || currentVisible?[index] == false {
                    continue
                }
                error += abs(
                    (reference[index] - referenceMean) / referenceDeviation
                        - (current[index] - currentMean) / currentDeviation
                )
            }
        }
        return error / divisor
    }

    private struct NormalizedGrid {
        let values: [Double]
        let deviation: Double
        let visible: [Bool]?
    }

    private static func normalized(
        _ grid: LowResolutionMotionGrid
    ) -> NormalizedGrid {
        let values = grid.luma.map(Double.init)
        let darkCount = grid.luma.count { $0 <= minimumVisibleLuma }
        let usesVisibilityMask = Double(darkCount) / Double(max(1, grid.luma.count))
            >= visibilityMaskDarkFraction
        let visible = usesVisibilityMask
            ? grid.luma.map { $0 > minimumVisibleLuma } : nil
        let retained = visible.map { mask in
            zip(values, mask).compactMap { value, keep in keep ? value : nil }
        } ?? values
        guard !retained.isEmpty else {
            return NormalizedGrid(
                values: values.map { _ in 0 }, deviation: 0, visible: visible
            )
        }
        let mean = retained.reduce(0, +) / Double(retained.count)
        let variance = retained.reduce(0) { result, value in
            let delta = value - mean
            return result + delta * delta
        } / Double(retained.count)
        let deviation = sqrt(variance)
        guard deviation > 0.001 else {
            return NormalizedGrid(
                values: values.map { _ in 0 }, deviation: deviation, visible: visible
            )
        }
        return NormalizedGrid(
            // A gameplay visibility circle applies a spatially varying gain:
            // pixels in its feather are dimmer than pixels at its center.
            // Local contrast removes that slow gain while retaining the room
            // texture needed for a strict place match.
            values: locallyNormalized(
                values,
                width: grid.width,
                height: grid.height,
                visible: visible
            ),
            deviation: deviation,
            visible: visible
        )
    }

    private static func locallyNormalized(
        _ values: [Double],
        width: Int,
        height: Int,
        visible: [Bool]?
    ) -> [Double] {
        guard width > 0, height > 0, values.count == width * height else {
            return values.map { _ in 0 }
        }
        var output = [Double](repeating: 0, count: values.count)
        let radius = 2
        for y in 0..<height {
            for x in 0..<width {
                let index = y * width + x
                if visible?[index] == false { continue }
                var sum = 0.0
                var squareSum = 0.0
                var count = 0
                for sampleY in max(0, y - radius)...min(height - 1, y + radius) {
                    for sampleX in max(0, x - radius)...min(width - 1, x + radius) {
                        let sampleIndex = sampleY * width + sampleX
                        if visible?[sampleIndex] == false { continue }
                        let value = values[sampleIndex]
                        sum += value
                        squareSum += value * value
                        count += 1
                    }
                }
                guard count >= 6 else { continue }
                let divisor = Double(count)
                let mean = sum / divisor
                let localDeviation = sqrt(max(
                    0, squareSum / divisor - mean * mean
                ))
                guard localDeviation >= 1 else { continue }
                output[index] = (values[index] - mean) / localDeviation
            }
        }
        return output
    }

}

/// Cheap floorless odometry. It registers 64x36 normalized luma frames at a
/// bounded cadence, so broad artwork can carry 2D camera motion after ground
/// evidence disappears without becoming a global solver.
struct LowResolutionTransitionOdometry {
    private struct Sample {
        let grid: LowResolutionMotionGrid
        let timestamp: TimeInterval
    }

    private struct HorizontalVelocitySample {
        let worldPixelsPerSecond: CGFloat
        let confidence: Double
    }

    private struct VerticalVelocitySample {
        let worldPixelsPerSecond: CGFloat
        let confidence: Double
    }

    // Compare nearby captures so fast camera travel remains inside the bounded
    // image search. On the current one-way Tutorial -> Town Hacker trace, the
    // former 120 ms baseline exceeds four horizontal cells in 74 same-room
    // frames around the known failures at 5, 8, and 87 seconds. At 50 ms,
    // ordinary motion peaks at 2.26 cells. Sub-cell refinement still retains
    // slow travel at this shorter cadence.
    static let minimumSampleAge: TimeInterval = 0.025
    static let maximumSampleAge: TimeInterval = 0.12
    /// Camera zones can ease briefly against held input. Hacker truth on the
    /// one-way route peaks below 1.3 opposite cells; larger horizontal-
    /// dominant contradictions remain rejected as likely mismatches.
    static let maximumOppositeDirectionSlackCells = 1.5
    /// A collapsing visibility circle can leave too few jointly visible
    /// pixels for direct registration even though the camera is still moving.
    /// Carry only a short, trusted horizontal camera velocity through that
    /// gap. The input direction must remain unchanged and the velocity must
    /// agree, so a scene cut or turnaround cannot inherit stale travel.
    static let predictionHorizon: TimeInterval = 0.48
    static let maximumPredictedSteps = 9
    static let requiredVelocitySamples = 1
    private static let retainedVelocitySamples = 3
    static let verticalPredictionHorizon: TimeInterval = 0.25
    static let maximumConsecutiveVerticalPredictions = 2

    private var reference: Sample?
    private(set) var worldDeltaX: CGFloat = 0
    private(set) var worldDeltaY: CGFloat = 0
    private(set) var sampleCount = 0
    private var confidenceTotal = 0.0
    private var horizontalVelocities = [HorizontalVelocitySample]()
    private var lastMeasuredAt: TimeInterval?
    private var lastExpectedDirection: VisualRoomDirection?
    private var predictedSteps = 0
    private var verticalVelocities = [VerticalVelocitySample]()
    private var lastVerticalMeasuredAt: TimeInterval?
    private var predictedVerticalSteps = 0
    /// Trust lineage for this floorless episode. Direction changes clear the
    /// velocity samples, but a later measured low-resolution step may seed a
    /// new direction only when the episode began from fresh ground velocity.
    private var hasReliableCameraVelocityLineage = false

    var isArmed: Bool { reference != nil }

    var travel: LowResolutionTransitionTravel {
        LowResolutionTransitionTravel(
            worldDeltaX: worldDeltaX,
            sampleCount: sampleCount,
            meanConfidence: sampleCount > 0
                ? confidenceTotal / Double(sampleCount) : 0
        )
    }

    mutating func reset() {
        reference = nil
        worldDeltaX = 0
        worldDeltaY = 0
        sampleCount = 0
        confidenceTotal = 0
        horizontalVelocities.removeAll(keepingCapacity: true)
        lastMeasuredAt = nil
        lastExpectedDirection = nil
        predictedSteps = 0
        verticalVelocities.removeAll(keepingCapacity: true)
        lastVerticalMeasuredAt = nil
        predictedVerticalSteps = 0
        hasReliableCameraVelocityLineage = false
    }

    mutating func arm(
        grid: LowResolutionMotionGrid?,
        timestamp: TimeInterval
    ) {
        reset()
        guard let grid, timestamp.isFinite else { return }
        reference = Sample(grid: grid, timestamp: timestamp)
    }

    mutating func primeHorizontalVelocity(
        _ seed: LowResolutionCameraVelocitySeed?,
        expectedDirection: VisualRoomDirection?
    ) {
        guard let seed,
              seed.timestamp.isFinite,
              seed.velocity.dx.isFinite,
              seed.velocity.dy.isFinite else { return }
        var seeded = false
        if let expectedDirection,
           abs(seed.velocity.dx) >= 5,
           abs(seed.velocity.dx) <= 1_000,
           Self.step(seed.velocity.dx, agreesWith: expectedDirection) {
            horizontalVelocities = [HorizontalVelocitySample(
                worldPixelsPerSecond: seed.velocity.dx,
                confidence: 0.5
            )]
            lastMeasuredAt = seed.timestamp
            lastExpectedDirection = expectedDirection
            predictedSteps = 0
            seeded = true
        }
        if abs(seed.velocity.dy) >= 5, abs(seed.velocity.dy) <= 1_000 {
            verticalVelocities = [VerticalVelocitySample(
                worldPixelsPerSecond: seed.velocity.dy,
                confidence: 0.5
            )]
            lastVerticalMeasuredAt = seed.timestamp
            predictedVerticalSteps = 0
            seeded = true
        }
        if seeded { lastExpectedDirection = expectedDirection }
        hasReliableCameraVelocityLineage = seeded
    }

    @discardableResult
    mutating func observe(
        grid: LowResolutionMotionGrid?,
        timestamp: TimeInterval,
        solveWidth: CGFloat,
        expectedDirection: VisualRoomDirection?
    ) -> CGFloat? {
        guard timestamp.isFinite, solveWidth.isFinite, solveWidth > 0,
              let grid else { return nil }
        guard let reference else {
            self.reference = Sample(grid: grid, timestamp: timestamp)
            return nil
        }
        let age = timestamp - reference.timestamp
        guard age >= Self.minimumSampleAge else { return nil }
        defer { self.reference = Sample(grid: grid, timestamp: timestamp) }
        guard age <= Self.maximumSampleAge else {
            clearVelocityPrediction()
            clearVerticalVelocityPrediction()
            return nil
        }
        if expectedDirection != lastExpectedDirection {
            clearVelocityPrediction()
            if let prior = lastExpectedDirection,
               let current = expectedDirection,
               prior != current {
                clearVerticalVelocityPrediction()
            }
            lastExpectedDirection = expectedDirection
        }
        let pixelScale = solveWidth / CGFloat(max(1, grid.width))
        let diagnostic = LowResolutionRoomMotionTracker.diagnoseTranslation(
            from: reference.grid,
            to: grid
        )
        guard let motion = diagnostic.motion else {
            guard diagnostic.finalRejection == .insufficientOverlap
                    || diagnostic.finalRejection == .insufficientImprovement else {
                return nil
            }
            let predictedHorizontal = predictedHorizontalStep(
                age: age,
                timestamp: timestamp,
                pixelScale: pixelScale,
                expectedDirection: expectedDirection
            )
            let predictedVertical = predictedVerticalStep(
                age: age,
                timestamp: timestamp,
                pixelScale: pixelScale
            )
            guard predictedHorizontal != nil || predictedVertical != nil else {
                return nil
            }
            let stepX = predictedHorizontal?.step ?? 0
            let stepY = predictedVertical?.step ?? 0
            worldDeltaX += stepX
            worldDeltaY += stepY
            sampleCount += 1
            confidenceTotal += max(
                predictedHorizontal?.confidence ?? 0,
                predictedVertical?.confidence ?? 0
            )
            if predictedHorizontal != nil { predictedSteps += 1 }
            if predictedVertical != nil { predictedVerticalSteps += 1 }
            return stepX
        }
        let stepX = -CGFloat(motion.screenShiftX) * pixelScale
        let stepY = CGFloat(motion.screenShiftY) * pixelScale
        guard stepX.isFinite, stepY.isFinite else { return nil }
        let predictedVertical = predictedVerticalStep(
            age: age,
            timestamp: timestamp,
            pixelScale: pixelScale
        )
        let usesPredictedVertical = predictedVertical.map {
            shouldUsePredictedVertical(measured: stepY, predicted: $0.step)
        } ?? false
        let acceptedStepY = usesPredictedVertical
            ? predictedVertical!.step : stepY
        // The first collapsing-fade match can retain just enough overlap to
        // report a small, wrong-direction displacement. The ordinary easing
        // allowance accepts that value, which would erase the fresh verified
        // ground velocity before the subsequent no-overlap frames. A trusted,
        // bounded lineage outranks only this contradictory measurement.
        if let expectedDirection,
           hasReliableCameraVelocityLineage,
           !Self.step(stepX, agreesWith: expectedDirection),
           let predicted = predictedHorizontalStep(
               age: age,
               timestamp: timestamp,
               pixelScale: pixelScale,
               expectedDirection: expectedDirection
            ) {
            worldDeltaX += predicted.step
            worldDeltaY += acceptedStepY
            sampleCount += 1
            confidenceTotal += max(
                predicted.confidence,
                usesPredictedVertical
                    ? predictedVertical?.confidence ?? 0 : motion.confidence
            )
            predictedSteps += 1
            if usesPredictedVertical { predictedVerticalSteps += 1 }
            else {
                rememberMeasuredVerticalVelocity(
                    stepY: stepY,
                    age: age,
                    timestamp: timestamp,
                    confidence: motion.confidence
                )
            }
            return predicted.step
        }
        guard Self.acceptsDirection(
            motion,
            expectedDirection: expectedDirection
        ) else {
            if let predicted = predictedHorizontalStep(
                age: age,
                timestamp: timestamp,
                pixelScale: pixelScale,
                expectedDirection: expectedDirection
            ) {
                worldDeltaX += predicted.step
                worldDeltaY += acceptedStepY
                sampleCount += 1
                confidenceTotal += max(
                    predicted.confidence,
                    usesPredictedVertical
                        ? predictedVertical?.confidence ?? 0 : motion.confidence
                )
                predictedSteps += 1
                if usesPredictedVertical { predictedVerticalSteps += 1 }
                else {
                    rememberMeasuredVerticalVelocity(
                        stepY: stepY,
                        age: age,
                        timestamp: timestamp,
                        confidence: motion.confidence
                    )
                }
                return predicted.step
            }
            clearVelocityPrediction()
            lastExpectedDirection = expectedDirection
            return nil
        }
        let predictedHorizontal = predictedHorizontalStep(
            age: age,
            timestamp: timestamp,
            pixelScale: pixelScale,
            expectedDirection: expectedDirection
        )
        let usesPredictedHorizontal = predictedHorizontal.map {
            shouldUsePredictedHorizontal(measured: stepX, predicted: $0.step)
        } ?? false
        let acceptedStepX = usesPredictedHorizontal
            ? predictedHorizontal!.step : stepX
        worldDeltaX += acceptedStepX
        worldDeltaY += acceptedStepY
        sampleCount += 1
        confidenceTotal += max(
            usesPredictedHorizontal
                ? predictedHorizontal?.confidence ?? 0 : motion.confidence,
            usesPredictedVertical
                ? predictedVertical?.confidence ?? 0 : motion.confidence
        )
        if usesPredictedHorizontal {
            predictedSteps += 1
        } else {
            rememberMeasuredVelocity(
                stepX: stepX,
                age: age,
                timestamp: timestamp,
                confidence: motion.confidence,
                expectedDirection: expectedDirection
            )
        }
        if usesPredictedVertical {
            predictedVerticalSteps += 1
        } else {
            rememberMeasuredVerticalVelocity(
                stepY: stepY,
                age: age,
                timestamp: timestamp,
                confidence: motion.confidence
            )
        }
        return acceptedStepX
    }

    private mutating func rememberMeasuredVerticalVelocity(
        stepY: CGFloat,
        age: TimeInterval,
        timestamp: TimeInterval,
        confidence: Double
    ) {
        guard hasReliableCameraVelocityLineage, age > 0,
              stepY.isFinite, abs(stepY) >= 0.25 else {
            if abs(stepY) < 0.25 { predictedVerticalSteps += 1 }
            return
        }
        let velocity = stepY / age
        if let prior = medianVerticalVelocity(), velocity * prior < 0 {
            verticalVelocities.removeAll(keepingCapacity: true)
        }
        verticalVelocities.append(VerticalVelocitySample(
            worldPixelsPerSecond: velocity,
            confidence: confidence
        ))
        if verticalVelocities.count > Self.retainedVelocitySamples {
            verticalVelocities.removeFirst(
                verticalVelocities.count - Self.retainedVelocitySamples
            )
        }
        lastVerticalMeasuredAt = timestamp
        predictedVerticalSteps = 0
    }

    private mutating func rememberMeasuredVelocity(
        stepX: CGFloat,
        age: TimeInterval,
        timestamp: TimeInterval,
        confidence: Double,
        expectedDirection: VisualRoomDirection?
    ) {
        guard hasReliableCameraVelocityLineage,
              let expectedDirection, age > 0,
              Self.step(stepX, agreesWith: expectedDirection),
              abs(stepX) >= 0.25 else {
            clearVelocityPrediction()
            lastExpectedDirection = expectedDirection
            return
        }
        horizontalVelocities.append(HorizontalVelocitySample(
            worldPixelsPerSecond: stepX / age,
            confidence: confidence
        ))
        if horizontalVelocities.count > Self.retainedVelocitySamples {
            horizontalVelocities.removeFirst(
                horizontalVelocities.count - Self.retainedVelocitySamples
            )
        }
        lastMeasuredAt = timestamp
        predictedSteps = 0
    }

    private mutating func clearVelocityPrediction() {
        horizontalVelocities.removeAll(keepingCapacity: true)
        lastMeasuredAt = nil
        predictedSteps = 0
    }

    private mutating func clearVerticalVelocityPrediction() {
        verticalVelocities.removeAll(keepingCapacity: true)
        lastVerticalMeasuredAt = nil
        predictedVerticalSteps = 0
    }

    private func predictedVerticalStep(
        age: TimeInterval,
        timestamp: TimeInterval,
        pixelScale: CGFloat
    ) -> (step: CGFloat, confidence: Double)? {
        guard hasReliableCameraVelocityLineage,
              verticalVelocities.count >= Self.requiredVelocitySamples,
              let lastVerticalMeasuredAt,
              timestamp - lastVerticalMeasuredAt <= Self.verticalPredictionHorizon,
              predictedVerticalSteps < Self.maximumConsecutiveVerticalPredictions,
              let velocity = verticalVelocities.last?.worldPixelsPerSecond else {
            return nil
        }
        // Vertical camera zones reverse and accelerate over only a few frames.
        // Sign changes already clear the history, so the newest direct image
        // measurement is the faithful handoff velocity; a median of two samples
        // selects the older, slower magnitude and recreates the freeze.
        let maximumStep = CGFloat(
            LowResolutionRoomMotionTracker.maximumVerticalShift
        ) * pixelScale
        let rawStep = velocity * CGFloat(age)
        let step = max(-maximumStep, min(maximumStep, rawStep))
        guard step.isFinite, abs(step) >= 0.25 else { return nil }
        let confidence = verticalVelocities.map(\.confidence).reduce(0, +)
            / Double(verticalVelocities.count) * 0.35
        return (step, confidence)
    }

    private func medianVerticalVelocity() -> CGFloat? {
        guard !verticalVelocities.isEmpty else { return nil }
        let ordered = verticalVelocities.sorted {
            $0.worldPixelsPerSecond < $1.worldPixelsPerSecond
        }
        return ordered[ordered.count / 2].worldPixelsPerSecond
    }

    private func shouldUsePredictedVertical(
        measured: CGFloat,
        predicted: CGFloat
    ) -> Bool {
        guard abs(predicted) >= 4 else { return false }
        let contradicts = measured * predicted < 0
        let collapses = abs(measured) < abs(predicted) * 0.35
        return contradicts || collapses
    }

    private func shouldUsePredictedHorizontal(
        measured: CGFloat,
        predicted: CGFloat
    ) -> Bool {
        guard abs(predicted) >= 4, measured * predicted > 0 else { return false }
        // During a fast fade or fall the remaining visible texture can report
        // the correct direction but only a fraction of the actual travel. For
        // the short ground-velocity handoff window, keep the independently
        // measured velocity when image motion collapses this far. A true stop
        // remains rejected as insignificant motion before reaching this path.
        return abs(measured) < abs(predicted) * 0.85
    }

    private func predictedHorizontalStep(
        age: TimeInterval,
        timestamp: TimeInterval,
        pixelScale: CGFloat,
        expectedDirection: VisualRoomDirection?
    ) -> (step: CGFloat, confidence: Double)? {
        guard let expectedDirection,
              hasReliableCameraVelocityLineage,
              expectedDirection == lastExpectedDirection,
              horizontalVelocities.count >= Self.requiredVelocitySamples,
              let lastMeasuredAt,
              timestamp - lastMeasuredAt <= Self.predictionHorizon,
              predictedSteps < Self.maximumPredictedSteps else { return nil }
        let ordered = horizontalVelocities.sorted {
            $0.worldPixelsPerSecond < $1.worldPixelsPerSecond
        }
        let velocity = ordered[ordered.count / 2].worldPixelsPerSecond
        guard Self.step(velocity, agreesWith: expectedDirection) else { return nil }
        let maximumStep = CGFloat(LowResolutionRoomMotionTracker.maximumShift)
            * pixelScale
        let rawStep = velocity * CGFloat(age)
        let step = max(-maximumStep, min(maximumStep, rawStep))
        guard step.isFinite, abs(step) >= 0.25 else { return nil }
        let confidence = horizontalVelocities.map(\.confidence).reduce(0, +)
            / Double(horizontalVelocities.count) * 0.35
        return (step, confidence)
    }

    private static func step(
        _ step: CGFloat,
        agreesWith direction: VisualRoomDirection
    ) -> Bool {
        direction == .left ? step < 0 : step > 0
    }

    static func acceptsDirection(
        _ motion: LowResolutionMotionVector,
        expectedDirection: VisualRoomDirection?
    ) -> Bool {
        guard let expectedDirection,
              abs(motion.screenShiftX) >= 0.0001 else { return true }
        let cameraStepX = -motion.screenShiftX
        let opposesIntent = expectedDirection == .left
            ? cameraStepX >= 0 : cameraStepX <= 0
        guard opposesIntent else { return true }
        let horizontalDominant = abs(motion.screenShiftX)
            >= abs(motion.screenShiftY)
        return !horizontalDominant
            || abs(motion.screenShiftX) <= maximumOppositeDirectionSlackCells
    }
}

/// Measures only coarse horizontal image travel. The 64-wide luma grid comes
/// from the signal-profile render already performed by the capture path. Each
/// grid is normalized before matching so a room-wide fade does not look like
/// motion. Several recent estimates vote, making this a direction hint rather
/// than a second camera solver.
struct LowResolutionRoomMotionTracker {
    private struct TranslationKey: Hashable {
        let x: Int
        let y: Int
    }

    private struct Sample {
        let timestamp: TimeInterval
        let grid: LowResolutionMotionGrid
    }

    private struct Vote {
        let timestamp: TimeInterval
        let estimate: LowResolutionRoomMotionEstimate
    }

    static let comparisonAge: ClosedRange<TimeInterval> = 0.025...0.12
    static let preferredComparisonAge: TimeInterval = 0.05
    static let voteLifetime: TimeInterval = 0.65
    // Four horizontal cells cover every ordinary same-scene Hacker sample at
    // the shorter cadence while rejecting the Town cut-scene camera warp.
    static let maximumShift = 4
    // At the retained 64x36 resolution one cell is ten solve pixels. Exact
    // Hacker comparisons show real descent regularly exceeds the old
    // three-cell bound over the 75-220 ms comparison window (p99 ~= 5.6
    // cells). Eight cells covers those frames while retaining twenty rows of
    // overlap for scoring.
    static let maximumVerticalShift = 8
    static let maximumFullScoreCandidates = 6
    static let minimumImprovement = 0.055
    static let minimumTextureDeviation = 4.0
    private static let minimumVisibleLuma: UInt8 = 8
    private static let visibilityMaskDarkFraction = 0.12

    private var history = [Sample]()
    private var votes = [Vote]()

    mutating func reset() {
        history.removeAll(keepingCapacity: true)
        votes.removeAll(keepingCapacity: true)
    }

    mutating func observe(
        _ grid: LowResolutionMotionGrid?,
        timestamp: TimeInterval
    ) -> LowResolutionRoomMotionEstimate? {
        guard timestamp.isFinite, let grid else {
            expire(at: timestamp)
            return consensus(at: timestamp)
        }
        history.append(Sample(timestamp: timestamp, grid: grid))
        history.removeAll { timestamp - $0.timestamp > Self.comparisonAge.upperBound }
        let reference = history
            .filter { Self.comparisonAge.contains(timestamp - $0.timestamp) }
            .min { lhs, rhs in
                abs((timestamp - lhs.timestamp) - Self.preferredComparisonAge)
                    < abs((timestamp - rhs.timestamp) - Self.preferredComparisonAge)
            }
        if let reference,
           let estimate = Self.estimate(from: reference.grid, to: grid) {
            votes.append(Vote(timestamp: timestamp, estimate: estimate))
        }
        expire(at: timestamp)
        return consensus(at: timestamp)
    }

    private mutating func expire(at timestamp: TimeInterval) {
        history.removeAll { timestamp - $0.timestamp > Self.comparisonAge.upperBound }
        votes.removeAll { timestamp - $0.timestamp > Self.voteLifetime }
    }

    private func consensus(at timestamp: TimeInterval) -> LowResolutionRoomMotionEstimate? {
        guard !votes.isEmpty else { return nil }
        var signedWeight = 0.0
        var totalWeight = 0.0
        var signedShift = 0.0
        for vote in votes {
            let age = max(0, timestamp - vote.timestamp)
            let freshness = max(0.2, 1 - age / Self.voteLifetime)
            let weight = vote.estimate.confidence * freshness
            signedWeight += Double(vote.estimate.direction.rawValue) * weight
            signedShift += Double(vote.estimate.screenShift) * weight
            totalWeight += weight
        }
        guard totalWeight > 0.12,
              abs(signedWeight) / totalWeight >= 0.30 else { return nil }
        let direction: VisualRoomDirection = signedWeight < 0 ? .left : .right
        return LowResolutionRoomMotionEstimate(
            direction: direction,
            confidence: min(1, abs(signedWeight) / totalWeight),
            screenShift: Int((signedShift / totalWeight).rounded())
        )
    }

    static func estimate(
        from reference: LowResolutionMotionGrid,
        to current: LowResolutionMotionGrid
    ) -> LowResolutionRoomMotionEstimate? {
        guard let motion = translation(from: reference, to: current),
              abs(motion.screenShiftX) >= 0.10 else { return nil }
        // If artwork moved right on screen, the camera/room traversal moved
        // left. Report traversal direction, not raw pixel-shift direction.
        let direction: VisualRoomDirection = motion.screenShiftX > 0 ? .left : .right
        return LowResolutionRoomMotionEstimate(
            direction: direction,
            confidence: motion.confidence,
            screenShift: motion.screenShiftX > 0
                ? max(1, Int(motion.screenShiftX.rounded()))
                : min(-1, Int(motion.screenShiftX.rounded()))
        )
    }

    /// Estimate a coarse 2D screen translation. The public room-direction
    /// vote still consumes only X, but floorless odometry carries Y as well so
    /// simultaneous vertical camera travel cannot invalidate every horizontal
    /// comparison and freeze the fallback pose.
    static func translation(
        from reference: LowResolutionMotionGrid,
        to current: LowResolutionMotionGrid,
        requiredImprovement: Double = LowResolutionRoomMotionTracker.minimumImprovement
    ) -> LowResolutionMotionVector? {
        diagnoseTranslation(
            from: reference,
            to: current,
            requiredImprovement: requiredImprovement
        ).motion
    }

    static func diagnoseTranslation(
        from reference: LowResolutionMotionGrid,
        to current: LowResolutionMotionGrid,
        requiredImprovement: Double = LowResolutionRoomMotionTracker.minimumImprovement
    ) -> LowResolutionTranslationDiagnostic {
        // Preserve the proven three-row matcher for X. Expanding its Y search
        // reduced the scoring band and under-counted horizontal travel even
        // on level camera sections. A second wider pass contributes only Y
        // when motion exceeds the narrow pass's range.
        let narrow = bestTranslationAttempt(
            from: reference, to: current, verticalShiftLimit: 3,
            requiredImprovement: requiredImprovement
        )
        let wideLimit = min(maximumVerticalShift, max(3, reference.height / 3))
        // The wide pass costs roughly twice the ordinary search. Run it only
        // when the narrow result reaches its vertical boundary (or cannot
        // score at all); ordinary horizontal travel stays on the cheap pass.
        let needsWideVerticalSearch = narrow.motion == nil
            || abs(narrow.motion?.screenShiftY ?? 0) >= 2.75
        let wide = wideLimit > 3 && needsWideVerticalSearch
            ? bestTranslationAttempt(
                from: reference, to: current,
                verticalShiftLimit: wideLimit,
                requiredImprovement: requiredImprovement
            )
            : nil
        guard let fallback = narrow.motion ?? wide?.motion else {
            return LowResolutionTranslationDiagnostic(
                motion: nil,
                narrowRejection: narrow.rejection,
                wideRejection: wide?.rejection
            )
        }
        let vertical = wide?.motion.flatMap {
            abs($0.screenShiftY) > 3 ? $0 : nil
        } ?? narrow.motion ?? fallback
        let horizontal = narrow.motion ?? fallback
        return LowResolutionTranslationDiagnostic(
            motion: LowResolutionMotionVector(
                screenShiftX: horizontal.screenShiftX,
                screenShiftY: vertical.screenShiftY,
                confidence: min(horizontal.confidence, vertical.confidence)
            ),
            narrowRejection: narrow.rejection,
            wideRejection: wide?.rejection
        )
    }

    private struct TranslationAttempt {
        let motion: LowResolutionMotionVector?
        let rejection: LowResolutionTranslationRejection?

        static func rejected(
            _ reason: LowResolutionTranslationRejection
        ) -> TranslationAttempt {
            TranslationAttempt(motion: nil, rejection: reason)
        }
    }

    private static func bestTranslationAttempt(
        from reference: LowResolutionMotionGrid,
        to current: LowResolutionMotionGrid,
        verticalShiftLimit: Int,
        requiredImprovement: Double
    ) -> TranslationAttempt {
        guard reference.width == current.width,
              reference.height == current.height,
              reference.width > maximumShift * 2 + 4,
              reference.height > verticalShiftLimit * 2 + 1 else {
            return .rejected(.incompatibleGrid)
        }
        // A shrinking gameplay visibility circle changes where darkness lies,
        // so whole-frame normalization cannot remove it. When either frame has
        // a substantial near-black area, normalize and compare only pixels
        // that remain visible in both frames. Ordinary frames retain the
        // cheaper whole-grid path.
        let usesVisibilityMask = requiresVisibilityMask(reference)
            || requiresVisibilityMask(current)
        let normalizedReference = normalized(
            reference, excludingDarkPixels: usesVisibilityMask
        )
        let normalizedCurrent = normalized(
            current, excludingDarkPixels: usesVisibilityMask
        )
        guard normalizedReference.deviation >= minimumTextureDeviation,
              normalizedCurrent.deviation >= minimumTextureDeviation else {
            return .rejected(.insufficientTexture)
        }

        let yStart = max(verticalShiftLimit, reference.height / 6)
        let yEnd = min(
            reference.height - verticalShiftLimit,
            reference.height * 5 / 6
        )
        let xStart = maximumShift
        let xEnd = reference.width - maximumShift
        func score(
            shiftX: Int,
            shiftY: Int,
            sampleStride: Int
        ) -> Double {
            var error = 0.0
            var count = 0
            var available = 0
            for y in stride(from: yStart, to: yEnd, by: sampleStride) {
                for x in stride(from: xStart, to: xEnd, by: sampleStride) {
                    available += 1
                    let referenceIndex = y * reference.width + x
                    let currentIndex = (y + shiftY) * current.width + x + shiftX
                    if let visible = normalizedReference.visible,
                       (!visible[referenceIndex]
                        || normalizedCurrent.visible?[currentIndex] != true) {
                        continue
                    }
                    let referenceValue = normalizedReference.values[referenceIndex]
                    let currentValue = normalizedCurrent.values[currentIndex]
                    error += abs(referenceValue - currentValue)
                    count += 1
                }
            }
            guard count >= max(12, available / 10) else { return .infinity }
            return error / Double(count)
        }

        // Rank every bounded translation on one quarter of the pixels, then
        // fully score only the strongest candidates. This keeps the Hacker-
        // calibrated fast-motion range cheaper than the former narrow search.
        var coarseScores = [(key: TranslationKey, score: Double)]()
        coarseScores.reserveCapacity(
            (maximumShift * 2 + 1) * (verticalShiftLimit * 2 + 1)
        )
        for shiftY in -verticalShiftLimit...verticalShiftLimit {
            for shiftX in -maximumShift...maximumShift {
                coarseScores.append((
                    TranslationKey(x: shiftX, y: shiftY),
                    score(
                        shiftX: shiftX,
                        shiftY: shiftY,
                        sampleStride: 2
                    )
                ))
            }
        }
        coarseScores.sort { $0.score < $1.score }
        var fullKeys = Set(
            coarseScores.prefix(maximumFullScoreCandidates).map(\.key)
        )
        let zeroKey = TranslationKey(x: 0, y: 0)
        fullKeys.insert(zeroKey)
        if let coarseBest = coarseScores.first?.key {
            for y in max(-verticalShiftLimit, coarseBest.y - 1)...min(
                verticalShiftLimit, coarseBest.y + 1
            ) {
                for x in max(-maximumShift, coarseBest.x - 1)...min(
                    maximumShift, coarseBest.x + 1
                ) {
                    fullKeys.insert(TranslationKey(x: x, y: y))
                }
            }
        }
        let scores = Dictionary(uniqueKeysWithValues: fullKeys.map { key in
            (
                key,
                score(shiftX: key.x, shiftY: key.y, sampleStride: 1)
            )
        })
        guard let best = scores.min(by: { $0.value < $1.value }),
              let zero = scores[zeroKey], zero.isFinite, zero > 0,
              best.value.isFinite else {
            return .rejected(.insufficientOverlap)
        }
        let improvement = (zero - best.value) / zero
        let alternatives = scores.filter { $0.key != best.key }.map(\.value)
        let second = alternatives.min() ?? zero
        let uniqueness = max(0, (second - best.value) / max(0.0001, second))
        let residual = subcellResidual(
            reference: normalizedReference.values,
            current: normalizedCurrent.values,
            width: reference.width,
            xRange: xStart..<xEnd,
            yRange: yStart..<yEnd,
            integerX: best.key.x,
            integerY: best.key.y,
            referenceVisible: normalizedReference.visible,
            currentVisible: normalizedCurrent.visible
        )
        let refinedX = Double(best.key.x) + residual.x
        let refinedY = Double(best.key.y) + residual.y
        let integerMotion = best.key.x != 0 || best.key.y != 0
        if integerMotion {
            guard improvement >= requiredImprovement else {
                return .rejected(.insufficientImprovement)
            }
        } else {
            // A unique zero-cell minimum can still contain real sub-cell
            // travel. At exactly half a cell, adjacent integer candidates tie
            // even though the gradient residual is strong; accept that case
            // while continuing to reject static frames whose residual is zero.
            let residualMagnitude = hypot(refinedX, refinedY)
            guard residualMagnitude >= 0.10,
                  uniqueness >= 0.002 || residualMagnitude >= 0.20 else {
                return .rejected(.insignificantSubcellMotion)
            }
        }
        let confidence = min(
            1,
            max(0, improvement) * 3.5 + uniqueness * 2
                + min(0.25, hypot(refinedX, refinedY) * 0.25)
        )

        return TranslationAttempt(
            motion: LowResolutionMotionVector(
                screenShiftX: refinedX,
                screenShiftY: refinedY,
                confidence: confidence
            ),
            rejection: nil
        )
    }

    /// Lucas-Kanade residual around the best integer cell. Integer SAD is
    /// robust for large motion; this least-squares refinement recovers the
    /// sub-cell component that SAD otherwise rounds to zero. Normalized luma
    /// makes the residual insensitive to a room-wide exposure fade.
    private static func subcellResidual(
        reference: [Double],
        current: [Double],
        width: Int,
        xRange: Range<Int>,
        yRange: Range<Int>,
        integerX: Int,
        integerY: Int,
        referenceVisible: [Bool]?,
        currentVisible: [Bool]?
    ) -> (x: Double, y: Double) {
        var xx = 0.0, xy = 0.0, yy = 0.0
        var xb = 0.0, yb = 0.0
        for y in yRange {
            for x in xRange {
                let referenceIndex = y * width + x
                let currentIndex = (y + integerY) * width + x + integerX
                if let referenceVisible,
                   (!referenceVisible[referenceIndex]
                    || !referenceVisible[referenceIndex - 1]
                    || !referenceVisible[referenceIndex + 1]
                    || !referenceVisible[referenceIndex - width]
                    || !referenceVisible[referenceIndex + width]
                    || currentVisible?[currentIndex] != true) {
                    continue
                }
                let gradientX = (reference[referenceIndex + 1]
                    - reference[referenceIndex - 1]) * 0.5
                let gradientY = (reference[referenceIndex + width]
                    - reference[referenceIndex - width]) * 0.5
                let difference = current[currentIndex] - reference[referenceIndex]
                xx += gradientX * gradientX
                xy += gradientX * gradientY
                yy += gradientY * gradientY
                xb += gradientX * difference
                yb += gradientY * difference
            }
        }
        let determinant = xx * yy - xy * xy
        guard determinant.isFinite,
              determinant > max(0.000_001, xx * yy * 0.001) else {
            return (0, 0)
        }
        let x = (xy * yb - yy * xb) / determinant
        let y = (xy * xb - xx * yb) / determinant
        let stableX = abs(x) < 0.01 ? 0 : x
        let stableY = abs(y) < 0.01 ? 0 : y
        return (
            min(0.5, max(-0.5, stableX.isFinite ? stableX : 0)),
            min(0.5, max(-0.5, stableY.isFinite ? stableY : 0))
        )
    }

    private static func requiresVisibilityMask(
        _ grid: LowResolutionMotionGrid
    ) -> Bool {
        guard !grid.luma.isEmpty else { return false }
        let dark = grid.luma.count { $0 <= minimumVisibleLuma }
        return Double(dark) / Double(grid.luma.count)
            >= visibilityMaskDarkFraction
    }

    private static func normalized(
        _ grid: LowResolutionMotionGrid,
        excludingDarkPixels: Bool = false
    ) -> (values: [Double], deviation: Double, visible: [Bool]?) {
        let values = grid.luma.map(Double.init)
        let visible = excludingDarkPixels
            ? grid.luma.map { $0 > minimumVisibleLuma } : nil
        let retained = visible.map { mask in
            zip(values, mask).compactMap { value, keep in keep ? value : nil }
        } ?? values
        guard !retained.isEmpty else { return (values.map { _ in 0 }, 0, visible) }
        let mean = retained.reduce(0, +) / Double(retained.count)
        let variance = retained.reduce(0) { partial, value in
            let delta = value - mean
            return partial + delta * delta
        } / Double(retained.count)
        let deviation = sqrt(variance)
        guard deviation > 0.001 else {
            return (values.map { _ in 0 }, deviation, visible)
        }
        return (values.map { ($0 - mean) / deviation }, deviation, visible)
    }
}

/// Vision-only room ownership. Darkness by itself never changes rooms. Coarse
/// image motion, directional input, and Knight edge evidence nominate a
/// traversal; once discovered, the doorway's world position is authoritative.
/// The reverse edge retains the old portal pose and the shared composition
/// boundary, so returning reuses the same room instead of creating a third.
final class VisualRoomTransitionTracker: @unchecked Sendable {
    private enum RoomAppearance: String, Codable, Equatable {
        case normal
        case dark
    }

    private struct PersistedState: Codable {
        // v3 retries the one-time two-room placement after v2 could move raw
        // capture poses and therefore fail persistence validation.
        static let currentSchemaVersion = 3

        struct Appearance: Codable {
            let roomID: Int
            let value: RoomAppearance
        }
        struct Edge: Codable {
            let sourceRoomID: Int
            let direction: Int
            let targetRoomID: Int
            let entryX: Double
            let entryY: Double
            let departureX: Double
            let departureY: Double
            let portalID: Int
        }
        struct Connection: Codable {
            let id: Int
            let leftRoomID: Int
            let rightRoomID: Int
            let leftDoorWorldX: Double
            let rightDoorWorldX: Double
            let leftMinimumWorldY: Double
            let leftMaximumWorldY: Double
            let rightMinimumWorldY: Double
            let rightMaximumWorldY: Double
        }

        let schemaVersion: Int
        let activeRoomID: Int
        let activeEntryX: Double
        let activeEntryY: Double
        let revision: UInt64
        let nextRoomID: Int
        let nextPortalID: Int
        let activeAppearance: RoomAppearance?
        let appearances: [Appearance]
        let edges: [Edge]
        let connections: [Connection]
    }

    private enum PendingKind: Equatable {
        case blackout
        case appearance(RoomAppearance)
        /// Directional near-black fade whose source-side Knight detection was
        /// missed. It becomes a room transition only after the source
        /// appearance returns and the Knight is seen at the opposite edge.
        case fade(RoomAppearance)
    }

    private struct EdgeKey: Hashable {
        let roomID: Int
        let direction: VisualRoomDirection
    }

    private struct PortalTarget {
        let roomID: Int
        let entryWorldPose: CGPoint
        let departureWorldPose: CGPoint
        let portalID: Int
    }

    private struct PortalConnection {
        let id: Int
        let leftRoomID: Int
        let rightRoomID: Int
        let leftDoorWorldX: CGFloat
        let rightDoorWorldX: CGFloat
        let leftDoorYRange: ClosedRange<CGFloat>
        let rightDoorYRange: ClosedRange<CGFloat>
    }

    private struct TimedDirection {
        let direction: VisualRoomDirection
        let timestamp: TimeInterval
    }

    /// Camera-space Knight evidence retained across a loading fade. Camera
    /// position plus screen position describes the same world point on both
    /// sides of a doorway, so this can place an unrelated arrival texture
    /// without trying to image-register the two rooms.
    private struct TimedKnightObservation {
        let screenCenter: CGPoint
        let worldPose: CGPoint
        let timestamp: TimeInterval
    }

    private struct PendingTransition {
        let sourceRoomID: Int
        var direction: VisualRoomDirection
        let departureWorldPose: CGPoint
        let solveWidth: CGFloat
        let solveHeight: CGFloat
        let kind: PendingKind
        let sourceEdgeBand: FrameEdgeBlackBand?
        var arrivalEdgeBand: FrameEdgeBlackBand?
        let sourceKnightObservation: TimedKnightObservation?
        var arrivalKnightCenter: CGPoint?
        var hasScreenMotionEvidence: Bool
        var consecutiveTargetFrames: Int
        var sawOppositeArrivalEdge: Bool
        var oppositeDirectionFrames: Int
        var totalFrames: Int
    }

    static let evidenceLifetime: TimeInterval = 1.5
    static let arrivalStableFrames = 3
    static let maximumArrivalFrames = 45
    static let maximumFadeFrames = 600
    static let appearanceStableFrames = 12
    static let edgeFraction: CGFloat = 0.22
    static let portalReentryToleranceInScreens: CGFloat = 0.30
    /// Measured from the exact Hacker transform at the Tutorial -> Town exit.
    /// Used only when object inference misses the departing Knight; the
    /// detected arrival center still supplies the other side of the equation.
    static let fadeFallbackDepartureFraction: CGFloat = 0.855

    private let lock = NSLock()
    private var activeRoomID = 0
    private var activeEntryWorldPose = CGPoint.zero
    private var revision: UInt64 = 0
    private var nextRoomID = 1
    private var nextPortalID = 1
    private var portals = [EdgeKey: PortalTarget]()
    private var portalConnections = [Int: PortalConnection]()
    private var roomAppearances = [Int: RoomAppearance]()
    private var activeAppearance: RoomAppearance?
    private var lastIntent: TimedDirection?
    private var lastBoundary: TimedDirection?
    private var lastKnightObservation: TimedKnightObservation?
    private var pending: PendingTransition?
    private var motionTracker = LowResolutionRoomMotionTracker()
    private var transitionOdometry = LowResolutionTransitionOdometry()
    private var floorlessOdometry = LowResolutionTransitionOdometry()
    private var placeMemory = LowResolutionPlaceMemory()
    private var floorlessAnchorWorldPose: CGPoint?
    private var floorlessWorldPose: CGPoint?
    private var floorlessPoseMeasured = false
    private var floorlessPoseRevision: UInt64 = 0
    private var previousMotionSample: (grid: LowResolutionMotionGrid, timestamp: TimeInterval)?
    private var previousEdgeBlackBands: FrameEdgeBlackBands?
    private var latestCoarseMotionGrid: LowResolutionMotionGrid?
    private var latestCoarseMotionEstimate: LowResolutionRoomMotionEstimate?
    private var latestCoarsePlaceMatch: LowResolutionPlaceMatch?
    private var activationWorldPose: CGPoint?

    var snapshot: VisualRoomSnapshot {
        lock.withLock { snapshotLocked() }
    }

    /// Rebase a continuing capture-rate trajectory from a more precise masked
    /// registration measured on an older frame. Applying the delta to both the
    /// anchor and current pose preserves motion accumulated after that frame.
    /// The revision guard prevents a delayed solve from moving a newer place
    /// match, room activation, or ground reacquisition.
    @discardableResult
    func applyFloorlessCorrection(
        _ correction: CGVector,
        roomID: Int,
        coarsePoseRevision: UInt64
    ) -> Bool {
        lock.withLock {
            guard roomID == activeRoomID,
                  coarsePoseRevision == floorlessPoseRevision,
                  correction.dx.isFinite, correction.dy.isFinite,
                  var anchor = floorlessAnchorWorldPose,
                  var position = floorlessWorldPose,
                  floorlessPoseMeasured else { return false }
            anchor.x += correction.dx
            anchor.y += correction.dy
            position.x += correction.dx
            position.y += correction.dy
            floorlessAnchorWorldPose = anchor
            floorlessWorldPose = position
            floorlessPoseRevision &+= 1
            return true
        }
    }

    func compositionBounds(for roomID: Int) -> VisualRoomCompositionBounds {
        lock.withLock { compositionBoundsLocked(for: roomID) }
    }

    @discardableResult
    func restoreTopology(from url: URL) throws -> VisualRoomSnapshot {
        let data = try Data(contentsOf: url)
        let state = try JSONDecoder().decode(PersistedState.self, from: data)
        guard state.schemaVersion == PersistedState.currentSchemaVersion,
              state.activeRoomID >= 0,
              state.nextRoomID > state.activeRoomID,
              state.nextPortalID > 0,
              state.activeEntryX.isFinite, state.activeEntryY.isFinite,
              state.edges.allSatisfy({ edge in
                  edge.sourceRoomID >= 0 && edge.targetRoomID >= 0
                      && VisualRoomDirection(rawValue: edge.direction) != nil
                      && edge.entryX.isFinite && edge.entryY.isFinite
                      && edge.departureX.isFinite && edge.departureY.isFinite
              }),
              state.connections.allSatisfy({ connection in
                  connection.id > 0 && connection.leftRoomID >= 0
                      && connection.rightRoomID >= 0
                      && connection.leftDoorWorldX.isFinite
                      && connection.rightDoorWorldX.isFinite
                      && connection.leftDoorWorldX <= connection.rightDoorWorldX
                      && connection.leftMinimumWorldY.isFinite
                      && connection.leftMaximumWorldY.isFinite
                      && connection.leftMaximumWorldY > connection.leftMinimumWorldY
                      && connection.rightMinimumWorldY.isFinite
                      && connection.rightMaximumWorldY.isFinite
                      && connection.rightMaximumWorldY > connection.rightMinimumWorldY
              }) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return lock.withLock {
            activeRoomID = state.activeRoomID
            activeEntryWorldPose = CGPoint(x: state.activeEntryX, y: state.activeEntryY)
            revision = state.revision
            nextRoomID = state.nextRoomID
            nextPortalID = state.nextPortalID
            roomAppearances = Dictionary(uniqueKeysWithValues: state.appearances.map {
                ($0.roomID, $0.value)
            })
            activeAppearance = state.activeAppearance
            portalConnections = Dictionary(uniqueKeysWithValues: state.connections.map {
                let ranges = VisualRoomDoorGeometry.aligned(
                    CGFloat($0.leftMinimumWorldY)...CGFloat($0.leftMaximumWorldY),
                    CGFloat($0.rightMinimumWorldY)...CGFloat($0.rightMaximumWorldY)
                )
                return ($0.id, PortalConnection(
                    id: $0.id,
                    leftRoomID: $0.leftRoomID,
                    rightRoomID: $0.rightRoomID,
                    leftDoorWorldX: $0.leftDoorWorldX,
                    rightDoorWorldX: $0.rightDoorWorldX,
                    leftDoorYRange: ranges.first,
                    rightDoorYRange: ranges.second
                ))
            })
            portals = Dictionary(uniqueKeysWithValues: state.edges.compactMap { edge in
                guard let direction = VisualRoomDirection(rawValue: edge.direction),
                      portalConnections[edge.portalID] != nil else { return nil }
                return (EdgeKey(roomID: edge.sourceRoomID, direction: direction),
                    PortalTarget(
                        roomID: edge.targetRoomID,
                        entryWorldPose: CGPoint(x: edge.entryX, y: edge.entryY),
                        departureWorldPose: CGPoint(
                            x: edge.departureX, y: edge.departureY
                        ),
                        portalID: edge.portalID
                    ))
            })
            lastIntent = nil
            lastBoundary = nil
            lastKnightObservation = nil
            pending = nil
            motionTracker.reset()
            transitionOdometry.reset()
            placeMemory.reset()
            resetFloorlessTraversal()
            previousMotionSample = nil
            previousEdgeBlackBands = nil
            latestCoarseMotionGrid = nil
            latestCoarseMotionEstimate = nil
            latestCoarsePlaceMatch = nil
            activationWorldPose = nil
            return snapshotLocked()
        }
    }

    func saveTopology(to url: URL) throws {
        let data = try lock.withLock { () throws -> Data in
            let state = PersistedState(
                schemaVersion: PersistedState.currentSchemaVersion,
                activeRoomID: activeRoomID,
                activeEntryX: activeEntryWorldPose.x,
                activeEntryY: activeEntryWorldPose.y,
                revision: revision,
                nextRoomID: nextRoomID,
                nextPortalID: nextPortalID,
                activeAppearance: activeAppearance,
                appearances: roomAppearances.sorted { $0.key < $1.key }.map {
                    PersistedState.Appearance(roomID: $0.key, value: $0.value)
                },
                edges: portals.sorted {
                    if $0.key.roomID != $1.key.roomID {
                        return $0.key.roomID < $1.key.roomID
                    }
                    return $0.key.direction.rawValue < $1.key.direction.rawValue
                }.map { key, target in
                    PersistedState.Edge(
                        sourceRoomID: key.roomID,
                        direction: key.direction.rawValue,
                        targetRoomID: target.roomID,
                        entryX: target.entryWorldPose.x,
                        entryY: target.entryWorldPose.y,
                        departureX: target.departureWorldPose.x,
                        departureY: target.departureWorldPose.y,
                        portalID: target.portalID
                    )
                },
                connections: portalConnections.values.sorted { $0.id < $1.id }.map {
                    PersistedState.Connection(
                        id: $0.id,
                        leftRoomID: $0.leftRoomID,
                        rightRoomID: $0.rightRoomID,
                        leftDoorWorldX: $0.leftDoorWorldX,
                        rightDoorWorldX: $0.rightDoorWorldX,
                        leftMinimumWorldY: $0.leftDoorYRange.lowerBound,
                        leftMaximumWorldY: $0.leftDoorYRange.upperBound,
                        rightMinimumWorldY: $0.rightDoorYRange.lowerBound,
                        rightMaximumWorldY: $0.rightDoorYRange.upperBound
                    )
                }
            )
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            return try encoder.encode(state)
        }
        try data.write(to: url, options: .atomic)
    }

    @discardableResult
    func bootstrapTwoRoomTopology(
        leftRoomID: Int,
        rightRoomID: Int,
        leftDoorWorldX: CGFloat,
        rightDoorWorldX: CGFloat,
        leftDoorYRange: ClosedRange<CGFloat>,
        rightDoorYRange: ClosedRange<CGFloat>,
        leftEntryWorldPose: CGPoint,
        rightEntryWorldPose: CGPoint,
        activeRoomID: Int? = nil
    ) -> VisualRoomSnapshot {
        lock.withLock {
            guard portals.isEmpty, portalConnections.isEmpty,
                  leftRoomID >= 0, rightRoomID >= 0,
                  leftRoomID != rightRoomID,
                  leftDoorWorldX.isFinite, rightDoorWorldX.isFinite,
                  leftDoorWorldX <= rightDoorWorldX,
                  Self.valid(leftDoorYRange), Self.valid(rightDoorYRange) else {
                return snapshotLocked()
            }
            let portalID = nextPortalID
            nextPortalID += 1
            let ranges = VisualRoomDoorGeometry.aligned(
                leftDoorYRange, rightDoorYRange
            )
            portalConnections[portalID] = PortalConnection(
                id: portalID,
                leftRoomID: leftRoomID,
                rightRoomID: rightRoomID,
                leftDoorWorldX: leftDoorWorldX,
                rightDoorWorldX: rightDoorWorldX,
                leftDoorYRange: ranges.first,
                rightDoorYRange: ranges.second
            )
            portals[EdgeKey(roomID: rightRoomID, direction: .left)] = PortalTarget(
                roomID: leftRoomID,
                entryWorldPose: leftEntryWorldPose,
                departureWorldPose: rightEntryWorldPose,
                portalID: portalID
            )
            portals[EdgeKey(roomID: leftRoomID, direction: .right)] = PortalTarget(
                roomID: rightRoomID,
                entryWorldPose: rightEntryWorldPose,
                departureWorldPose: leftEntryWorldPose,
                portalID: portalID
            )
            self.activeRoomID = activeRoomID == leftRoomID ? leftRoomID : rightRoomID
            activeEntryWorldPose = self.activeRoomID == leftRoomID
                ? leftEntryWorldPose : rightEntryWorldPose
            nextRoomID = max(nextRoomID, max(leftRoomID, rightRoomID) + 1)
            revision &+= 1
            activeAppearance = nil
            roomAppearances.removeAll(keepingCapacity: true)
            resetFloorlessTraversal()
            activationWorldPose = nil
            return snapshotLocked()
        }
    }

    func entryWorldPose(for roomID: Int) -> CGPoint? {
        lock.withLock {
            if activeRoomID == roomID { return activeEntryWorldPose }
            return portals.values.first(where: { $0.roomID == roomID })?.entryWorldPose
        }
    }

    /// A discovered room can be saved before its first atlas observation is
    /// committed. After a restart, prefer the only room actually represented
    /// by durable evidence while retaining the portal for the next traversal.
    @discardableResult
    func restoreActiveRoomFromDurableEvidence(_ roomID: Int) -> VisualRoomSnapshot {
        lock.withLock {
            guard roomID >= 0, roomID != activeRoomID,
                  let target = portals.values.first(where: { $0.roomID == roomID })
            else { return snapshotLocked() }
            activeRoomID = roomID
            activeEntryWorldPose = target.entryWorldPose
            activeAppearance = roomAppearances[roomID]
            pending = nil
            lastIntent = nil
            lastBoundary = nil
            lastKnightObservation = nil
            transitionOdometry.reset()
            resetFloorlessTraversal()
            activationWorldPose = nil
            return snapshotLocked()
        }
    }

    @discardableResult
    func reset() -> VisualRoomSnapshot {
        lock.withLock {
            activeRoomID = 0
            activeEntryWorldPose = .zero
            revision = 0
            nextRoomID = 1
            nextPortalID = 1
            portals.removeAll(keepingCapacity: true)
            portalConnections.removeAll(keepingCapacity: true)
            roomAppearances.removeAll(keepingCapacity: true)
            activeAppearance = nil
            lastIntent = nil
            lastBoundary = nil
            lastKnightObservation = nil
            pending = nil
            motionTracker.reset()
            transitionOdometry.reset()
            resetFloorlessTraversal()
            previousMotionSample = nil
            previousEdgeBlackBands = nil
            latestCoarseMotionGrid = nil
            latestCoarseMotionEstimate = nil
            activationWorldPose = nil
            return snapshotLocked()
        }
    }

    func observe(
        profile: FrameSignalProfile,
        baseDecision: TransitionFrameDecision,
        horizontalInput: VisualRoomDirection?,
        knightRect: CGRect?,
        frameSize: CGSize,
        currentWorldPose: CGPoint,
        reliableCameraVelocity: LowResolutionCameraVelocitySeed? = nil,
        solveWidth: CGFloat,
        timestamp: TimeInterval,
        groundTrackingReliable: Bool = true
    ) -> VisualRoomSnapshot {
        lock.withLock {
            guard timestamp.isFinite, solveWidth.isFinite, solveWidth > 0,
                  frameSize.width.isFinite, frameSize.width > 0,
                  frameSize.height.isFinite, frameSize.height > 0,
                  currentWorldPose.x.isFinite, currentWorldPose.y.isFinite else {
                return snapshotLocked()
            }
            let priorMotionSample = previousMotionSample
            let priorEdgeBlackBands = previousEdgeBlackBands
            defer {
                if let grid = profile.motionGrid {
                    previousMotionSample = (grid, timestamp)
                }
                previousEdgeBlackBands = profile.edgeBlackBands
            }
            if let horizontalInput {
                lastIntent = TimedDirection(direction: horizontalInput, timestamp: timestamp)
            }
            let knightScreenCenter = screenCenter(
                for: knightRect,
                frameSize: frameSize,
                solveWidth: solveWidth
            )
            if let knightScreenCenter {
                lastKnightObservation = TimedKnightObservation(
                    screenCenter: knightScreenCenter,
                    worldPose: currentWorldPose,
                    timestamp: timestamp
                )
            }
            let currentBoundary = boundaryDirection(for: knightRect, frameSize: frameSize)
            if let boundary = currentBoundary {
                lastBoundary = TimedDirection(direction: boundary, timestamp: timestamp)
            }
            expireEvidence(at: timestamp)
            let screenMotion = motionTracker.observe(profile.motionGrid, timestamp: timestamp)
            latestCoarseMotionGrid = profile.motionGrid
            latestCoarseMotionEstimate = screenMotion
            updateFloorlessTraversal(
                groundTrackingReliable: groundTrackingReliable,
                horizontalInput: horizontalInput,
                screenMotion: screenMotion,
                priorMotionSample: priorMotionSample,
                currentGrid: profile.motionGrid,
                currentWorldPose: currentWorldPose,
                reliableCameraVelocity: reliableCameraVelocity,
                solveWidth: solveWidth,
                timestamp: timestamp
            )
            let solveHeight = frameSize.height / frameSize.width * solveWidth
            let entranceDirection = pending?.direction
                ?? eligibleDepartureDirection(
                    at: timestamp,
                    requiresBoundary: true,
                    screenMotion: screenMotion
                )
                ?? entranceTrackingDirection(
                    horizontalInput: horizontalInput,
                    currentBoundary: currentBoundary,
                    currentWorldPose: currentWorldPose,
                    solveWidth: solveWidth
                )
            if let entranceDirection {
                if !transitionOdometry.isArmed {
                    transitionOdometry.arm(
                        grid: priorMotionSample?.grid ?? profile.motionGrid,
                        timestamp: priorMotionSample?.timestamp ?? timestamp
                    )
                }
                _ = transitionOdometry.observe(
                    grid: profile.motionGrid,
                    timestamp: timestamp,
                    solveWidth: solveWidth,
                    expectedDirection: entranceDirection
                )
            } else {
                transitionOdometry.reset()
            }

            if activeAppearance == nil, profile.hasGameplaySignal {
                let initial = Self.initialAppearance(profile)
                activeAppearance = initial
                roomAppearances[activeRoomID] = initial
            }
            guard let activeAppearance else { return snapshotLocked() }

            if !profile.hasGameplaySignal {
                if pending?.kind != .blackout,
                   let direction = eligibleDepartureDirection(
                    at: timestamp,
                    requiresBoundary: true,
                    screenMotion: screenMotion
                   ) {
                    pending = PendingTransition(
                        sourceRoomID: activeRoomID,
                        direction: direction,
                        departureWorldPose: floorlessAnchorWorldPose
                            ?? currentWorldPose,
                        solveWidth: solveWidth,
                        solveHeight: solveHeight,
                        kind: .blackout,
                        sourceEdgeBand: priorEdgeBlackBands?.band(at: direction),
                        arrivalEdgeBand: nil,
                        sourceKnightObservation: lastKnightObservation,
                        arrivalKnightCenter: nil,
                        hasScreenMotionEvidence: screenMotion?.direction == direction,
                        consecutiveTargetFrames: 0,
                        sawOppositeArrivalEdge: false,
                        oppositeDirectionFrames: 0,
                        totalFrames: 0
                    )
                } else if pending?.kind == .blackout {
                    pending?.consecutiveTargetFrames = 0
                }
                return snapshotLocked()
            }

            let appearance = Self.appearance(profile, relativeTo: activeAppearance)
            if var pending {
                pending.totalFrames += 1
                if horizontalInput == pending.direction.opposite {
                    self.pending = nil
                    transitionOdometry.reset()
                    return snapshotLocked()
                }
                let coarseMotionOpposesDeparture = screenMotion?.direction
                    == pending.direction.opposite
                pending.oppositeDirectionFrames = coarseMotionOpposesDeparture
                    ? pending.oppositeDirectionFrames + 1 : 0
                if pending.oppositeDirectionFrames >= 3,
                   !Self.isFade(pending.kind) {
                    self.pending = nil
                    transitionOdometry.reset()
                    return snapshotLocked()
                }
                if screenMotion?.direction == pending.direction {
                    pending.hasScreenMotionEvidence = true
                }
                if currentBoundary == pending.direction.opposite {
                    pending.sawOppositeArrivalEdge = true
                    if pending.arrivalKnightCenter == nil {
                        pending.arrivalKnightCenter = knightScreenCenter
                    }
                }
                if let arrival = profile.edgeBlackBands?.band(
                    at: pending.direction.opposite
                ) {
                    pending.arrivalEdgeBand = arrival
                }
                switch pending.kind {
                case .appearance(let target):
                    guard appearance == target else {
                        self.pending = nil
                        transitionOdometry.reset()
                        return snapshotLocked()
                    }
                    pending.consecutiveTargetFrames += 1
                    self.pending = pending
                    let isKnownDirectedPortal = portals[EdgeKey(
                        roomID: pending.sourceRoomID,
                        direction: pending.direction
                    )] != nil
                    let requiredFrames = isKnownDirectedPortal
                        ? Self.arrivalStableFrames : Self.appearanceStableFrames
                    guard pending.consecutiveTargetFrames >= requiredFrames else {
                        return snapshotLocked()
                    }
                    activateRoom(after: pending, arrivalAppearance: target)
                case .blackout:
                    pending.consecutiveTargetFrames += 1
                    self.pending = pending
                    let arrivalIsStable = pending.consecutiveTargetFrames
                        >= Self.arrivalStableFrames || baseDecision.resumedAfterTransition
                    guard arrivalIsStable,
                          pending.sawOppositeArrivalEdge || appearance != activeAppearance else {
                        if pending.consecutiveTargetFrames >= Self.maximumArrivalFrames {
                            self.pending = nil
                            lastIntent = nil
                            lastBoundary = nil
                            transitionOdometry.reset()
                        }
                        return snapshotLocked()
                    }
                    activateRoom(after: pending, arrivalAppearance: appearance)
                case .fade(let sourceAppearance):
                    guard appearance == sourceAppearance else {
                        pending.consecutiveTargetFrames = 0
                        self.pending = pending
                        if pending.totalFrames >= Self.maximumFadeFrames {
                            self.pending = nil
                            lastIntent = nil
                            lastBoundary = nil
                            transitionOdometry.reset()
                        }
                        return snapshotLocked()
                    }
                    pending.consecutiveTargetFrames += 1
                    self.pending = pending
                    guard pending.consecutiveTargetFrames >= Self.arrivalStableFrames,
                          pending.sawOppositeArrivalEdge else {
                        if pending.totalFrames >= Self.maximumFadeFrames {
                            self.pending = nil
                            lastIntent = nil
                            lastBoundary = nil
                            transitionOdometry.reset()
                        }
                        return snapshotLocked()
                    }
                    activateRoom(
                        after: pending,
                        arrivalAppearance: sourceAppearance
                    )
                }
                self.pending = nil
                lastIntent = nil
                lastBoundary = nil
                transitionOdometry.reset()
                resetFloorlessTraversal()
                return snapshotLocked()
            }

            guard appearance != activeAppearance else {
                return snapshotLocked()
            }
            let strictDirection = eligibleDepartureDirection(
                    at: timestamp,
                    requiresBoundary: true,
                    screenMotion: screenMotion
                  )
            if appearance == .dark, knightRect == nil,
               let direction = strictDirection {
                // Held input plus image motion is common while the room's
                // circle of visibility shrinks. It is not doorway evidence.
                // Begin a fade traversal only after the Knight was observed at
                // the nominated edge; the Tutorial -> Town route supplies that
                // evidence immediately before the loading fade.
                if !transitionOdometry.isArmed {
                    transitionOdometry.arm(
                        grid: priorMotionSample?.grid ?? profile.motionGrid,
                        timestamp: priorMotionSample?.timestamp ?? timestamp
                    )
                }
                _ = transitionOdometry.observe(
                    grid: profile.motionGrid,
                    timestamp: timestamp,
                    solveWidth: solveWidth,
                    expectedDirection: direction
                )
                pending = PendingTransition(
                    sourceRoomID: activeRoomID,
                    direction: direction,
                    departureWorldPose: floorlessAnchorWorldPose
                        ?? currentWorldPose,
                    solveWidth: solveWidth,
                    solveHeight: solveHeight,
                    kind: .fade(activeAppearance),
                    sourceEdgeBand: priorEdgeBlackBands?.band(at: direction),
                    arrivalEdgeBand: nil,
                    sourceKnightObservation: lastKnightObservation,
                    arrivalKnightCenter: currentBoundary == direction.opposite
                        ? knightScreenCenter : nil,
                    hasScreenMotionEvidence: screenMotion?.direction == direction,
                    consecutiveTargetFrames: 0,
                    sawOppositeArrivalEdge: currentBoundary == direction.opposite,
                    oppositeDirectionFrames: 0,
                    totalFrames: 0
                )
                return snapshotLocked()
            }
            guard let direction = strictDirection,
                  appearanceTransitionIsSpatiallyEligible(
                    from: activeRoomID,
                    direction: direction,
                    arrivalAppearance: appearance,
                    departureWorldPose: currentWorldPose,
                    solveWidth: solveWidth
                  ) else {
                return snapshotLocked()
            }
            pending = PendingTransition(
                sourceRoomID: activeRoomID,
                direction: direction,
                departureWorldPose: floorlessAnchorWorldPose
                    ?? currentWorldPose,
                solveWidth: solveWidth,
                solveHeight: solveHeight,
                kind: .appearance(appearance),
                sourceEdgeBand: priorEdgeBlackBands?.band(at: direction),
                arrivalEdgeBand: profile.edgeBlackBands?.band(at: direction.opposite),
                sourceKnightObservation: lastKnightObservation,
                arrivalKnightCenter: currentBoundary == direction.opposite
                    ? knightScreenCenter : nil,
                hasScreenMotionEvidence: screenMotion?.direction == direction,
                consecutiveTargetFrames: 1,
                sawOppositeArrivalEdge: currentBoundary == direction.opposite,
                oppositeDirectionFrames: 0,
                totalFrames: 0
            )
            return snapshotLocked()
        }
    }

    static func isNearBlack(_ profile: FrameSignalProfile) -> Bool {
        initialAppearance(profile) == .dark
    }

    private static func initialAppearance(_ profile: FrameSignalProfile) -> RoomAppearance {
        profile.meanPeak <= 18 && profile.visibleFraction <= 0.40 ? .dark : .normal
    }

    private static func isFade(_ kind: PendingKind) -> Bool {
        if case .fade = kind { return true }
        return false
    }

    private static func appearance(
        _ profile: FrameSignalProfile,
        relativeTo current: RoomAppearance
    ) -> RoomAppearance {
        if profile.meanPeak <= 18 && profile.visibleFraction <= 0.40 { return .dark }
        if profile.meanPeak >= 24 || profile.visibleFraction >= 0.46 { return .normal }
        return current
    }

    private func snapshotLocked() -> VisualRoomSnapshot {
        let publicPortals = portalConnections.values.sorted { $0.id < $1.id }.map {
            VisualRoomPortal(
                id: $0.id,
                leftRoomID: $0.leftRoomID,
                rightRoomID: $0.rightRoomID,
                leftDoorWorldX: $0.leftDoorWorldX,
                rightDoorWorldX: $0.rightDoorWorldX,
                leftDoorYRange: $0.leftDoorYRange,
                rightDoorYRange: $0.rightDoorYRange
            )
        }
        let floorlessPublishedPose = floorlessPoseMeasured
            ? floorlessWorldPose : nil
        let transitionWorldPose = pending.map {
            if let floorlessPublishedPose { return floorlessPublishedPose }
            return CGPoint(
                x: $0.departureWorldPose.x + transitionOdometry.worldDeltaX,
                y: $0.departureWorldPose.y + transitionOdometry.worldDeltaY
            )
        }
        return VisualRoomSnapshot(
            roomID: activeRoomID,
            roomName: "Room \(activeRoomID + 1)",
            revision: revision,
            entryWorldPose: activeEntryWorldPose,
            isTransitioning: pending != nil,
            transitionDirection: pending?.direction,
            transitionWorldPose: transitionWorldPose,
            activationWorldPose: activationWorldPose,
            coarseWorldPose: floorlessPublishedPose,
            coarsePoseRevision: floorlessPoseRevision,
            coarseMotionGrid: latestCoarseMotionGrid,
            coarseMotionEstimate: latestCoarseMotionEstimate,
            coarsePlaceMatch: latestCoarsePlaceMatch,
            coarseMotionIsControlling: floorlessPublishedPose != nil
                || (pending != nil && transitionOdometry.sampleCount > 0),
            portals: publicPortals,
            compositionBounds: compositionBoundsLocked(for: activeRoomID)
        )
    }

    private func updateFloorlessTraversal(
        groundTrackingReliable: Bool,
        horizontalInput: VisualRoomDirection?,
        screenMotion: LowResolutionRoomMotionEstimate?,
        priorMotionSample: (grid: LowResolutionMotionGrid, timestamp: TimeInterval)?,
        currentGrid: LowResolutionMotionGrid?,
        currentWorldPose: CGPoint,
        reliableCameraVelocity: LowResolutionCameraVelocitySeed?,
        solveWidth: CGFloat,
        timestamp: TimeInterval
    ) {
        guard !groundTrackingReliable else {
            placeMemory.remember(
                grid: currentGrid,
                roomID: activeRoomID,
                cameraPosition: currentWorldPose,
                timestamp: timestamp
            )
            latestCoarsePlaceMatch = nil
            resetFloorlessTraversal()
            return
        }
        if floorlessAnchorWorldPose == nil {
            floorlessPoseRevision &+= 1
            floorlessAnchorWorldPose = currentWorldPose
            floorlessWorldPose = currentWorldPose
            floorlessOdometry.arm(
                grid: priorMotionSample?.grid ?? currentGrid,
                timestamp: priorMotionSample?.timestamp ?? timestamp
            )
            floorlessOdometry.primeHorizontalVelocity(
                reliableCameraVelocity,
                expectedDirection: horizontalInput ?? screenMotion?.direction
            )
        }
        // Camera travel continues during jumps, falls, and scripted pans when
        // neither horizontal key is held. Hacker truth on the one-way Tutorial
        // -> Town route contains 79 ground-unreliable, vertical-only 50 ms
        // comparisons (about 574 px of travel). The former optional invocation
        // silently discarded all of them. Always run broad-image registration;
        // retain the horizontal sign gate whenever input or image consensus
        // supplies a direction.
        if floorlessOdometry.observe(
            grid: currentGrid,
            timestamp: timestamp,
            solveWidth: solveWidth,
            expectedDirection: horizontalInput ?? screenMotion?.direction
        ) != nil {
            floorlessPoseMeasured = true
        }
        if let anchor = floorlessAnchorWorldPose {
            floorlessWorldPose = CGPoint(
                x: anchor.x + floorlessOdometry.worldDeltaX,
                y: anchor.y + floorlessOdometry.worldDeltaY
            )
        }
        latestCoarsePlaceMatch = placeMemory.observe(
            grid: currentGrid,
            roomID: activeRoomID,
            solveWidth: solveWidth,
            timestamp: timestamp
        )
        if let match = latestCoarsePlaceMatch {
            floorlessPoseRevision &+= 1
            floorlessAnchorWorldPose = match.cameraPosition
            floorlessWorldPose = match.cameraPosition
            floorlessPoseMeasured = true
            floorlessOdometry.arm(grid: currentGrid, timestamp: timestamp)
        }
    }

    private func resetFloorlessTraversal() {
        if floorlessAnchorWorldPose != nil || floorlessPoseMeasured {
            floorlessPoseRevision &+= 1
        }
        floorlessOdometry.reset()
        placeMemory.clearPending()
        floorlessAnchorWorldPose = nil
        floorlessWorldPose = nil
        floorlessPoseMeasured = false
    }

    private func boundaryDirection(
        for knightRect: CGRect?,
        frameSize: CGSize
    ) -> VisualRoomDirection? {
        guard let knightRect, !knightRect.isNull, !knightRect.isEmpty,
              frameSize.width.isFinite, frameSize.width > 0 else { return nil }
        if knightRect.midX <= frameSize.width * Self.edgeFraction { return .left }
        if knightRect.midX >= frameSize.width * (1 - Self.edgeFraction) { return .right }
        return nil
    }

    private func screenCenter(
        for knightRect: CGRect?,
        frameSize: CGSize,
        solveWidth: CGFloat
    ) -> CGPoint? {
        guard let knightRect, !knightRect.isNull, !knightRect.isEmpty,
              frameSize.width.isFinite, frameSize.width > 0,
              frameSize.height.isFinite, frameSize.height > 0 else { return nil }
        let scale = solveWidth / frameSize.width
        return CGPoint(x: knightRect.midX * scale, y: knightRect.midY * scale)
    }

    private func entranceTrackingDirection(
        horizontalInput: VisualRoomDirection?,
        currentBoundary: VisualRoomDirection?,
        currentWorldPose: CGPoint,
        solveWidth: CGFloat
    ) -> VisualRoomDirection? {
        if let horizontalInput, currentBoundary == horizontalInput {
            return horizontalInput
        }
        guard let horizontalInput,
              let portal = portals[EdgeKey(
                roomID: activeRoomID, direction: horizontalInput
              )],
              portalIsNearby(
                portal,
                departureWorldPose: currentWorldPose,
                solveWidth: solveWidth
              ) else { return nil }
        return horizontalInput
    }

    private static func valid(_ range: ClosedRange<CGFloat>) -> Bool {
        range.lowerBound.isFinite && range.upperBound.isFinite
            && range.upperBound > range.lowerBound
    }

    private func expireEvidence(at timestamp: TimeInterval) {
        if let lastIntent,
           timestamp - lastIntent.timestamp > Self.evidenceLifetime {
            self.lastIntent = nil
        }
        if let lastBoundary,
           timestamp - lastBoundary.timestamp > Self.evidenceLifetime {
            self.lastBoundary = nil
        }
        if let lastKnightObservation,
           timestamp - lastKnightObservation.timestamp > Self.evidenceLifetime {
            self.lastKnightObservation = nil
        }
    }

    private func eligibleDepartureDirection(
        at timestamp: TimeInterval,
        requiresBoundary: Bool,
        screenMotion: LowResolutionRoomMotionEstimate?
    ) -> VisualRoomDirection? {
        if let screenMotion {
            if !requiresBoundary { return screenMotion.direction }
            if let boundary = lastBoundary,
               timestamp - boundary.timestamp <= Self.evidenceLifetime,
               boundary.direction == screenMotion.direction {
                return screenMotion.direction
            }
        }
        guard let intent = lastIntent,
              timestamp - intent.timestamp <= Self.evidenceLifetime else { return nil }
        guard requiresBoundary else { return intent.direction }
        guard let boundary = lastBoundary,
              timestamp - boundary.timestamp <= Self.evidenceLifetime,
              intent.direction == boundary.direction else { return nil }
        return intent.direction
    }

    private func activateRoom(
        after transition: PendingTransition,
        arrivalAppearance: RoomAppearance
    ) {
        let measuredContinuousArrival = continuousArrivalWorldPose(after: transition)
        let continuousArrival = VisualRoomPortalLayout.plausibleContinuousArrival(
            measuredContinuousArrival,
            direction: transition.direction,
            departureWorldPose: transition.departureWorldPose,
            solveWidth: transition.solveWidth,
            solveHeight: transition.solveHeight
        )
        let outgoingKey = EdgeKey(
            roomID: transition.sourceRoomID,
            direction: transition.direction
        )
        let target: PortalTarget
        if let known = matchingPortal(
            from: transition.sourceRoomID,
            preferredDirection: transition.direction,
            arrivalAppearance: arrivalAppearance,
            departureWorldPose: transition.departureWorldPose,
            solveWidth: transition.solveWidth
        ) {
            target = known
        } else {
            // A room that already has a portal to this appearance may only
            // use that portal near its recorded camera pose. Darkening while
            // walking elsewhere in the room is not a room transition.
            guard !hasPortal(
                from: transition.sourceRoomID,
                toAppearance: arrivalAppearance
            ) else { return }
            // A new room is topology, not a brightness classification. Create
            // it only from one complete doorway traversal: directional image
            // motion, Knight leaving/reappearing on opposite frame edges,
            // black loading-matte bands on both room sides, and continuous
            // coarse odometry. If any component is absent, stay in the source
            // room. In particular, never manufacture the old fixed-width jump.
            let acceptsFadeGeometry = Self.isFade(transition.kind)
            guard (transition.hasScreenMotionEvidence || acceptsFadeGeometry),
                  transition.sawOppositeArrivalEdge,
                  let sourceBand = transition.sourceEdgeBand,
                  let arrivalBand = transition.arrivalEdgeBand,
                  continuousArrival != nil || acceptsFadeGeometry else {
                activeAppearance = roomAppearances[activeRoomID] ?? activeAppearance
                return
            }
            let newRoomID = nextRoomID
            nextRoomID += 1
            let portalID = nextPortalID
            nextPortalID += 1
            let layout = VisualRoomPortalLayout.make(
                direction: transition.direction,
                departureWorldPose: transition.departureWorldPose,
                solveWidth: transition.solveWidth,
                solveHeight: transition.solveHeight,
                sourceBand: sourceBand,
                targetBand: arrivalBand,
                measuredTravel: measuredTransitionTravel(),
                continuousArrivalWorldPose: continuousArrival
            )
            let directionIsConsistent = transition.direction == .left
                ? layout.targetDoorWorldX <= layout.sourceDoorWorldX
                : layout.targetDoorWorldX >= layout.sourceDoorWorldX
            // The accepted measured arrival or edge fallback must still place
            // the target doorway on the nominated side of the source room.
            guard directionIsConsistent else { return }
            let connection = PortalConnection(
                id: portalID,
                leftRoomID: transition.direction == .left
                    ? newRoomID : transition.sourceRoomID,
                rightRoomID: transition.direction == .left
                    ? transition.sourceRoomID : newRoomID,
                leftDoorWorldX: transition.direction == .left
                    ? layout.targetDoorWorldX : layout.sourceDoorWorldX,
                rightDoorWorldX: transition.direction == .left
                    ? layout.sourceDoorWorldX : layout.targetDoorWorldX,
                leftDoorYRange: transition.direction == .left
                    ? layout.targetDoorYRange : layout.sourceDoorYRange,
                rightDoorYRange: transition.direction == .left
                    ? layout.sourceDoorYRange : layout.targetDoorYRange
            )
            portalConnections[portalID] = connection
            target = PortalTarget(
                roomID: newRoomID,
                entryWorldPose: layout.targetEntryWorldPose,
                departureWorldPose: transition.departureWorldPose,
                portalID: portalID
            )
            portals[outgoingKey] = target
            portals[EdgeKey(roomID: newRoomID, direction: transition.direction.opposite)] =
                PortalTarget(
                    roomID: transition.sourceRoomID,
                    entryWorldPose: transition.departureWorldPose,
                    departureWorldPose: layout.targetEntryWorldPose,
                    portalID: portalID
                )
        }
        activeRoomID = target.roomID
        activeEntryWorldPose = target.entryWorldPose
        activationWorldPose = continuousArrival ?? target.entryWorldPose
        let rememberedAppearance = roomAppearances[target.roomID] ?? arrivalAppearance
        roomAppearances[target.roomID] = rememberedAppearance
        activeAppearance = rememberedAppearance
        revision &+= 1
    }

    private func continuousArrivalWorldPose(
        after transition: PendingTransition
    ) -> CGPoint? {
        if Self.isFade(transition.kind),
           let arrival = transition.arrivalKnightCenter {
            let fallbackSourceX = transition.direction == .right
                ? transition.solveWidth * Self.fadeFallbackDepartureFraction
                : transition.solveWidth * (1 - Self.fadeFallbackDepartureFraction)
            if let source = transition.sourceKnightObservation {
                // The detector commonly loses the Knight before it reaches
                // the exit matte. Its last X is therefore a directional lower
                // bound, while its Y still identifies the correct doorway
                // level. Clamp X to the departure boundary measured from the
                // exact Hacker projection instead of shortening the room jump.
                let sourceX = transition.direction == .right
                    ? max(source.screenCenter.x, fallbackSourceX)
                    : min(source.screenCenter.x, fallbackSourceX)
                return CGPoint(
                    x: transition.departureWorldPose.x + sourceX - arrival.x,
                    y: transition.departureWorldPose.y
                        + source.screenCenter.y - arrival.y
                )
            }
            return CGPoint(
                x: transition.departureWorldPose.x + fallbackSourceX - arrival.x,
                y: transition.departureWorldPose.y
            )
        }
        if floorlessOdometry.sampleCount > 0, let floorlessWorldPose {
            return floorlessWorldPose
        }
        guard transitionOdometry.sampleCount > 0 else { return nil }
        return CGPoint(
            x: transition.departureWorldPose.x + transitionOdometry.worldDeltaX,
            y: transition.departureWorldPose.y
        )
    }

    /// Prefer the odometry interval that retained the greatest signed travel.
    /// The entrance tracker usually starts first; the floorless tracker is the
    /// continuity source when ground disappears before transition recognition.
    private func measuredTransitionTravel() -> LowResolutionTransitionTravel? {
        let candidates = [transitionOdometry.travel, floorlessOdometry.travel]
            .filter { $0.sampleCount >= 2 && $0.meanConfidence >= 0.12 }
        return candidates.max {
            abs($0.worldDeltaX) < abs($1.worldDeltaX)
        }
    }

    private func matchingPortal(
        from sourceRoomID: Int,
        preferredDirection: VisualRoomDirection,
        arrivalAppearance: RoomAppearance,
        departureWorldPose: CGPoint,
        solveWidth: CGFloat
    ) -> PortalTarget? {
        let exact = portals[EdgeKey(roomID: sourceRoomID, direction: preferredDirection)]
        if let exact,
           portalIsNearby(
            exact,
            departureWorldPose: departureWorldPose,
            solveWidth: solveWidth
           ),
           roomAppearances[exact.roomID].map({ $0 == arrivalAppearance }) ?? true {
            return exact
        }
        return nil
    }

    private func appearanceTransitionIsSpatiallyEligible(
        from sourceRoomID: Int,
        direction: VisualRoomDirection,
        arrivalAppearance: RoomAppearance,
        departureWorldPose: CGPoint,
        solveWidth: CGFloat
    ) -> Bool {
        let sourceHasKnownPortal = portals.keys.contains {
            $0.roomID == sourceRoomID
        }
        let matching = portals.filter { key, target in
            key.roomID == sourceRoomID
                && roomAppearances[target.roomID] == arrivalAppearance
        }.map(\.value)
        guard let directed = portals[EdgeKey(
            roomID: sourceRoomID, direction: direction
        )] else {
            // Once a room has topology, an appearance change in an unrecorded
            // direction cannot silently invent another copy of a known room.
            return !sourceHasKnownPortal
        }
        if !matching.isEmpty,
           !matching.contains(where: { $0.portalID == directed.portalID }) {
            return false
        }
        return portalIsNearby(
            directed,
            departureWorldPose: departureWorldPose,
            solveWidth: solveWidth
        )
    }

    private func hasPortal(
        from sourceRoomID: Int,
        toAppearance appearance: RoomAppearance
    ) -> Bool {
        portals.contains { key, target in
            key.roomID == sourceRoomID
                && roomAppearances[target.roomID] == appearance
        }
    }

    private func portalIsNearby(
        _ portal: PortalTarget,
        departureWorldPose: CGPoint,
        solveWidth: CGFloat
    ) -> Bool {
        abs(portal.departureWorldPose.x - departureWorldPose.x)
            <= max(48, solveWidth * Self.portalReentryToleranceInScreens)
    }

    private func compositionBoundsLocked(for roomID: Int) -> VisualRoomCompositionBounds {
        var bounds = VisualRoomCompositionBounds.unbounded
        for portal in portalConnections.values {
            if portal.leftRoomID == roomID {
                bounds.maximumWorldX = bounds.maximumWorldX.map {
                    min($0, portal.leftDoorWorldX)
                } ?? portal.leftDoorWorldX
            }
            if portal.rightRoomID == roomID {
                bounds.minimumWorldX = bounds.minimumWorldX.map {
                    max($0, portal.rightDoorWorldX)
                } ?? portal.rightDoorWorldX
            }
        }
        return bounds
    }
}
