import CoreGraphics
import Foundation

/// Detects a loading cut when object inference cannot see the Knight at a door.
/// This establishes new visual ownership only: neither room adjacency, identity
/// on a return trip, nor a camera/floor displacement is inferred from the cut.
struct VisualSceneDiscontinuityTracker {
    struct Cut: Equatable {
        let departureTimestamp: TimeInterval
        let arrivalTimestamp: TimeInterval
        let departureWorldPose: CGPoint
        let direction: VisualRoomDirection
    }

    private struct VisibleReference {
        let grid: LowResolutionMotionGrid
        let timestamp: TimeInterval
        let worldPose: CGPoint
        let direction: VisualRoomDirection
    }
    private struct Loading {
        let source: VisibleReference
        var startedAt: TimeInterval
        var lastBlackAt: TimeInterval
        var sustainedBlackoutObserved = false
        var arrival: LowResolutionMotionGrid?
        var arrivalStartedAt: TimeInterval?
        var stableFrames = 0
    }
    private var reference: VisibleReference?
    private var loading: Loading?
    private var previousTimestamp: TimeInterval?
    private var intent: (direction: VisualRoomDirection, timestamp: TimeInterval)?

    var isPending: Bool { loading != nil }
    var pendingDirection: VisualRoomDirection? { loading?.source.direction }
    var departureWorldPose: CGPoint? { loading?.source.worldPose }

    struct Diagnostic: Codable {
        let referenceTimestamp: Double?
        let referenceDirection: Int?
        let intentTimestamp: Double?
        let intentDirection: Int?
        let loadingSourceTimestamp: Double?
        let loadingStartedAt: Double?
        let loadingLastBlackAt: Double?
        let sustainedBlackoutObserved: Bool
        let completeBlackout: Bool
        let recoveredImage: Bool
        let similarity: Double?
        let bothSidesChanged: Bool
    }

    /// Explicit audit access; live capture does not construct these extra fits.
    func diagnostic(for grid: LowResolutionMotionGrid) -> Diagnostic {
        let source = loading?.source.grid ?? reference?.grid
        return Diagnostic(referenceTimestamp: reference?.timestamp,
            referenceDirection: reference?.direction.rawValue,
            intentTimestamp: intent?.timestamp, intentDirection: intent?.direction.rawValue,
            loadingSourceTimestamp: loading?.source.timestamp,
            loadingStartedAt: loading?.startedAt, loadingLastBlackAt: loading?.lastBlackAt,
            sustainedBlackoutObserved: loading?.sustainedBlackoutObserved ?? false,
            completeBlackout: Self.isCompleteBlackout(grid), recoveredImage: Self.hasRecoveredImage(grid),
            similarity: source.flatMap { Self.similarity($0, grid) },
            bothSidesChanged: source.map { Self.bothSidesChanged($0, grid) } ?? false)
    }

    mutating func reset() { self = Self() }

    mutating func observe(grid: LowResolutionMotionGrid?, timestamp: TimeInterval,
                          direction: VisualRoomDirection?, worldPose: CGPoint) -> Cut? {
        guard timestamp.isFinite, worldPose.x.isFinite, worldPose.y.isFinite,
              let grid, grid.width >= 24, grid.height >= 18,
              grid.luma.count == grid.width * grid.height else {
            reset()
            return nil
        }
        let completeBlackout = Self.isCompleteBlackout(grid)
        if let previousTimestamp {
            if timestamp <= previousTimestamp {
                reset()
            } else if timestamp - previousTimestamp > 0.25 {
                // Loading can stop producing changed frames. Retain the actual
                // departure image across a bounded gap after black was seen,
                // but restart the observed-black duration at this capture.
                // Missing time cannot turn one black frame into a blackout.
                if var pending = loading, timestamp - previousTimestamp <= 3,
                   pending.source.grid.width == grid.width,
                   pending.source.grid.height == grid.height,
                   completeBlackout || pending.sustainedBlackoutObserved {
                    pending.startedAt = timestamp
                    pending.arrival = nil
                    pending.arrivalStartedAt = nil
                    pending.stableFrames = 0
                    loading = pending
                } else {
                    reset()
                }
            }
        }
        previousTimestamp = timestamp
        if let direction { intent = (direction, timestamp) }
        let recentDirection = intent.flatMap {
            timestamp - $0.timestamp <= 1.5 ? $0.direction : nil
        }
        if completeBlackout {
            if var loading {
                // Another blackout interrupts arrival confirmation.
                loading.lastBlackAt = timestamp
                loading.sustainedBlackoutObserved = loading.sustainedBlackoutObserved
                    || timestamp - loading.startedAt >= 0.15
                loading.arrival = nil
                loading.arrivalStartedAt = nil
                loading.stableFrames = 0
                // Memory stays constant during long loading screens. A gap
                // contributes no black duration; arrival must independently
                // establish stable, novel scenery.
                self.loading = loading
            } else if let reference, timestamp - reference.timestamp <= 1,
                      recentDirection != nil {
                // A late counter-steer during the fade does not invalidate the
                // last visible departure. Retain that image's direction; input
                // received after scenery vanished is not new doorway geometry.
                loading = Loading(source: reference, startedAt: timestamp,
                                  lastBlackAt: timestamp)
            }
            return nil
        }
        if var loading {
            guard loading.sustainedBlackoutObserved,
                  timestamp - loading.lastBlackAt <= 3 else {
                self.loading = nil
                reference = nil
                return nil
            }
            guard Self.hasRecoveredImage(grid) else { return nil }
            let similarity = Self.similarity(loading.source.grid, grid)
            // A fade returning to the same scenery has not discovered a room.
            if let similarity, similarity >= 0.80 {
                self.loading = nil
                reference = nil
                return nil
            }
            guard let similarity, similarity < 0.55,
                  Self.bothSidesChanged(loading.source.grid, grid) else {
                loading.arrival = nil
                loading.arrivalStartedAt = nil
                loading.stableFrames = 0
                self.loading = loading
                return nil
            }
            if let arrival = loading.arrival,
               Self.similarity(arrival, grid, requiringStructure: false).map({ $0 >= 0.90 }) == true {
                loading.stableFrames += 1
                // Confirm continuous overlap while the arrival camera moves.
                // The source-novelty gates above still verify every image;
                // elapsed time alone never establishes a new room.
                loading.arrival = grid
            } else {
                loading.arrival = grid
                loading.arrivalStartedAt = timestamp
                loading.stableFrames = 1
            }
            self.loading = loading
            guard loading.stableFrames >= 6,
                  timestamp - (loading.arrivalStartedAt ?? timestamp) >= 0.20 else {
                return nil
            }
            let cut = Cut(departureTimestamp: loading.source.timestamp,
                          arrivalTimestamp: timestamp,
                          departureWorldPose: loading.source.worldPose,
                          direction: loading.source.direction)
            reset()
            return cut
        }
        if let recentDirection, Self.hasRecoveredImage(grid) {
            reference = VisibleReference(grid: grid, timestamp: timestamp,
                                         worldPose: worldPose, direction: recentDirection)
        }
        return nil
    }

    static func isCompleteBlackout(_ grid: LowResolutionMotionGrid) -> Bool {
        guard grid.width > 0, grid.height > 1,
              grid.luma.count == grid.width * grid.height else { return false }
        // Ignore one top row: capture/evaluation markers are not game scenery.
        let pixels = grid.luma.dropFirst(grid.width)
        if pixels.count(where: { $0 >= 5 }) <= max(1, pixels.count / 1000) { return true }
        // The game can keep a small white Knight/loading sprite over black
        // after the HUD has disappeared. Require an empty header and one tiny,
        // compact body region; scattered scenery or retained HUD is not black.
        let headerEnd = grid.width * max(2, grid.height / 4)
        let header = grid.luma[grid.width..<headerEnd]
        guard header.count(where: { $0 >= 5 }) <= max(1, header.count / 1000) else { return false }
        let body = grid.luma[headerEnd...]
        let lit = body.indices.filter { body[$0] >= 5 }
        guard lit.count <= max(1, body.count / 100), let first = lit.first else { return false }
        var lowX = first % grid.width, highX = lowX
        var lowY = first / grid.width, highY = lowY
        for index in lit {
            lowX = min(lowX, index % grid.width); highX = max(highX, index % grid.width)
            lowY = min(lowY, index / grid.width); highY = max(highY, index / grid.width)
        }
        return highX - lowX + 1 <= max(3, grid.width / 8)
            && highY - lowY + 1 <= max(3, grid.height / 4)
    }

    private static func hasRecoveredImage(_ grid: LowResolutionMotionGrid) -> Bool {
        let header = grid.luma[grid.width..<(grid.width * max(2, grid.height / 4))]
        guard header.count(where: { $0 >= 128 }) >= 4 else { return false }
        let body = grid.luma.dropFirst(grid.width * (grid.height / 3))
        let mean = body.reduce(0.0) { $0 + Double($1) } / Double(body.count)
        let variance = body.reduce(0.0) { $0 + pow(Double($1) - mean, 2) }
            / Double(body.count)
        return variance >= 16
    }

    private static func bothSidesChanged(_ first: LowResolutionMotionGrid,
                                         _ second: LowResolutionMotionGrid) -> Bool {
        guard let left = similarity(first, second, half: 0),
              let right = similarity(first, second, half: 1) else { return false }
        return left < 0.70 && right < 0.70
    }

    /// Exposure-invariant intensity and edge correlation over scenery, allowing
    /// modest camera movement. Similar dark silhouettes cannot establish a
    /// same-room match without corresponding structure inside them.
    private static func similarity(_ first: LowResolutionMotionGrid,
                                   _ second: LowResolutionMotionGrid,
                                   half: Int? = nil,
                                   requiringStructure: Bool = true) -> Double? {
        guard first.width == second.width, first.height == second.height else { return nil }
        let width = first.width, height = first.height
        let xMargin = max(2, width / 12), yMargin = max(2, height / 9)
        let xLow = half == 1 ? width / 2 : xMargin
        let xHigh = half == 0 ? width / 2 : width - xMargin
        let yLow = height / 3, yHigh = height - yMargin - 1
        guard xHigh > xLow, yHigh > yLow else { return nil }
        var best: Double?
        for dy in -min(3, yMargin)...min(3, yMargin) {
            let searchX = min(4, xMargin - 1) // Reserve one pixel for gradients.
            for dx in -searchX...searchX {
                var sumA = 0.0, sumB = 0.0, aa = 0.0, bb = 0.0, ab = 0.0
                var gradientAA = 0.0, gradientBB = 0.0, gradientAB = 0.0
                var count = 0.0
                for y in yLow..<yHigh {
                    for x in xLow..<xHigh {
                        let ai = y * width + x, bi = (y + dy) * width + x + dx
                        let a = Double(first.luma[ai]), b = Double(second.luma[bi])
                        sumA += a; sumB += b; aa += a * a; bb += b * b; ab += a * b
                        if requiringStructure {
                            let ax = Double(first.luma[ai + 1]) - Double(first.luma[ai - 1])
                            let ay = Double(first.luma[ai + width]) - Double(first.luma[ai - width])
                            let bx = Double(second.luma[bi + 1]) - Double(second.luma[bi - 1])
                            let by = Double(second.luma[bi + width]) - Double(second.luma[bi - width])
                            gradientAA += ax * ax + ay * ay
                            gradientBB += bx * bx + by * by
                            gradientAB += ax * bx + ay * by
                        }
                        count += 1
                    }
                }
                let varianceA = aa - sumA * sumA / count
                let varianceB = bb - sumB * sumB / count
                guard varianceA >= count * 4, varianceB >= count * 4 else { continue }
                let correlation = (ab - sumA * sumB / count) / sqrt(varianceA * varianceB)
                if requiringStructure {
                    guard gradientAA >= count * 4, gradientBB >= count * 4 else { continue }
                    let structure = gradientAB / sqrt(gradientAA * gradientBB)
                    best = max(best ?? -1, min(correlation, structure))
                } else {
                    best = max(best ?? -1, correlation)
                }
            }
        }
        return best
    }
}
