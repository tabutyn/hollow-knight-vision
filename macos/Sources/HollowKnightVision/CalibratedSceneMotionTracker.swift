import CoreGraphics
import Foundation

/// Image-only fallback: scenery patches learn motion scale while observed ground
/// fixes camera motion. Static parallax layers can then carry a floorless frame.
/// New patches need two distinct, consistent scale observations before voting.
/// This estimates local motion only; it cannot establish a global island origin.
final class CalibratedSceneMotionTracker {
    struct Grid {
        let width: Int
        let height: Int
        let pixels: [Double]
        let exclusions: [CGRect]
    }
    struct GroundCalibration {
        let position: CGPoint
        let referenceTimestamp: TimeInterval
        /// Image displacement in solve units (X right, Y down).
        let displacement: CGVector
    }
    struct Measurement {
        let position: CGPoint
        let referenceTimestamp: TimeInterval
        let timestamp: TimeInterval
        let supportedPatches: Int
        let horizontalMeasured: Bool
        let verticalMeasured: Bool
        let residualX: CGFloat
        let residualY: CGFloat
    }
    private struct Sample { let grid: Grid; let timestamp: TimeInterval }
    private struct Patch {
        let point: CGPoint
        let gain: CGVector
        let calibratedX: Bool
        let calibratedY: Bool
        let votes: Int
        func moved(_ motion: CGVector, gain: CGVector? = nil, votes: Int? = nil) -> Self {
            Self(point: CGPoint(x: point.x + motion.dx, y: point.y + motion.dy), gain: gain ?? self.gain,
                 calibratedX: calibratedX, calibratedY: calibratedY, votes: votes ?? self.votes)
        }
    }
    struct Fit { let motion: CGVector; let correlation: Double }
    private let backend: GroundComputeBackend
    init(backend: GroundComputeBackend = .automatic) { self.backend = backend }
    private var history = [TimeInterval: Grid]()
    private var historyOrder = [TimeInterval]()
    private var reference: Sample?
    private var patches = [Patch]()
    private var pending = [Patch]()
    private var position: CGPoint?
    private var lastSample: TimeInterval?
    private var lastCalibration: TimeInterval?
    private var lastTimestamp: TimeInterval?
    private var lastSolveWidth: CGFloat?
    static let gridWidth = 160
    static let maximumPatches = 64
    static let maximumHistory = 64

    func reset() {
        history.removeAll(keepingCapacity: true); historyOrder.removeAll(keepingCapacity: true)
        clearMotion(); lastSample = nil; lastCalibration = nil; lastTimestamp = nil; lastSolveWidth = nil
    }
    private func clearMotion() { reference = nil; position = nil; patches = []; pending = [] }

    /// Area averages preserve dim texture while reducing work. HUD/actor masks
    /// remain image-local; matching rejects patches touching either image's mask.
    static func grid(pixels: [UInt8], width: Int, height: Int, excluding: [CGRect] = []) -> Grid? {
        guard width >= gridWidth, height >= 24, width <= Int.max / height,
              pixels.count == width * height else { return nil }
        let outputHeight = max(12, Int((Double(height) * Double(gridWidth) / Double(width)).rounded()))
        var output = [Double](repeating: 0, count: gridWidth * outputHeight)
        for y in 0..<outputHeight {
            let lowY = y * height / outputHeight, highY = max(lowY + 1, (y + 1) * height / outputHeight)
            for x in 0..<gridWidth {
                let lowX = x * width / gridWidth, highX = max(lowX + 1, (x + 1) * width / gridWidth)
                var sum = 0
                for sy in lowY..<highY { for sx in lowX..<highX { sum += Int(pixels[sy * width + sx]) } }
                output[y * gridWidth + x] = Double(sum) / Double((highX - lowX) * (highY - lowY))
            }
        }
        let scaleX = CGFloat(gridWidth) / CGFloat(width), scaleY = CGFloat(outputHeight) / CGFloat(height)
        let masks = excluding.filter { !$0.isNull && !$0.isEmpty }.map {
            CGRect(x: $0.minX * scaleX, y: (CGFloat(height) - $0.maxY) * scaleY,
                   width: $0.width * scaleX, height: $0.height * scaleY)
        }
        return Grid(width: gridWidth, height: outputHeight, pixels: output, exclusions: masks)
    }

    func observe(grid: Grid, timestamp: TimeInterval, solveWidth: CGFloat,
                 ground: GroundCalibration?) -> Measurement? {
        guard timestamp.isFinite, solveWidth.isFinite, solveWidth > 0,
              grid.width == Self.gridWidth, grid.height >= 24,
              grid.pixels.count == grid.width * grid.height else { reset(); return nil }
        if let ground, ![ground.position.x, ground.position.y, ground.displacement.dx, ground.displacement.dy].allSatisfy(\.isFinite) {
            reset(); return nil
        }
        if let previous = lastTimestamp, timestamp <= previous || timestamp - previous > 0.25 { reset() }
        if let lastSolveWidth, lastSolveWidth != solveWidth { reset() }
        lastTimestamp = timestamp; lastSolveWidth = solveWidth
        history[timestamp] = grid; historyOrder.append(timestamp)
        if historyOrder.count > Self.maximumHistory {
            for time in historyOrder.prefix(historyOrder.count - Self.maximumHistory) { history.removeValue(forKey: time) }
            historyOrder.removeFirst(historyOrder.count - Self.maximumHistory)
        }
        guard lastSample.map({ timestamp - $0 >= 0.025 }) ?? true else { return nil }
        lastSample = timestamp
        let scale = solveWidth / CGFloat(grid.width)
        if let ground, ground.position.x.isFinite, ground.position.y.isFinite,
           let prior = history[ground.referenceTimestamp],
           timestamp - ground.referenceTimestamp > 0, timestamp - ground.referenceTimestamp <= 0.5,
           max(abs(ground.displacement.dx), abs(ground.displacement.dy)) >= 5 * solveWidth / 640,
           lastCalibration.map({ timestamp - $0 >= 0.04 }) ?? true {
            let expected = CGVector(dx: ground.displacement.dx / scale, dy: ground.displacement.dy / scale)
            let candidates = candidatePoints(in: grid)
            let fits = Self.fitBatch(prior, grid, points: candidates, near: expected, backend: backend)
            let calibrated = zip(candidates, fits).compactMap { point, match -> Patch? in
                guard let fit = match,
                      hypot(fit.motion.dx - expected.dx, fit.motion.dy - expected.dy) < 0.6 else { return nil }
                let xKnown = abs(expected.dx) >= 1.25, yKnown = abs(expected.dy) >= 2
                let gain = CGVector(dx: xKnown ? fit.motion.dx / expected.dx : 1,
                                    dy: yKnown ? fit.motion.dy / expected.dy : 1)
                guard gain.dx > 0.8, gain.dx < 1.4, gain.dy > 0.8, gain.dy < 1.4 else { return nil }
                return Patch(point: CGPoint(x: point.x + fit.motion.dx, y: point.y + fit.motion.dy),
                             gain: gain, calibratedX: xKnown, calibratedY: yKnown, votes: 3)
            }
            if calibrated.count >= 3 {
                patches = Array(calibrated.prefix(Self.maximumPatches)); pending = []
                reference = Sample(grid: grid, timestamp: timestamp)
                position = ground.position; lastCalibration = timestamp
                return nil
            }
        }
        guard let reference, let priorPosition = position else { return nil }
        let age = timestamp - reference.timestamp
        guard age >= 0.025 else { return nil }
        guard age <= 0.12, reference.grid.width == grid.width, reference.grid.height == grid.height else {
            clearMotion(); return nil
        }
        let trackedFits = Self.fitBatch(reference.grid, grid, points: patches.map(\.point), backend: backend)
        let matched = zip(patches, trackedFits).compactMap { patch, match -> (Patch, Fit)? in
            match.map { (patch, $0) }
        }
        guard matched.count >= 3 else { return nil }
        let medianX = median(axisMotions(matched, horizontal: true))
        let medianY = median(axisMotions(matched, horizontal: false))
        let agreeing = matched.filter {
            hypot($0.1.motion.dx / $0.0.gain.dx - medianX, $0.1.motion.dy / $0.0.gain.dy - medianY) <= 0.65
        }
        guard agreeing.count >= 3 else { return nil }
        let xs = agreeing.map { $0.0.point.x }, ys = agreeing.map { $0.0.point.y }
        guard xs.max()! - xs.min()! >= 20, ys.max()! - ys.min()! >= 8 else { return nil }
        let horizontalMotions = axisMotions(agreeing, horizontal: true)
        let verticalMotions = axisMotions(agreeing, horizontal: false)
        let delta = CGVector(dx: median(horizontalMotions), dy: median(verticalMotions))
        // Preserve independent axis confidence. An uncalibrated axis can be an
        // estimate, but cannot authorize a rigid 2D island connection.
        let horizontalMeasured = agreeing.filter({ $0.0.calibratedX }).count >= 3
        let verticalMeasured = agreeing.filter({ $0.0.calibratedY }).count >= 3
        let nextPosition = ground?.position ?? CGPoint(x: priorPosition.x - delta.dx * scale,
                                                        y: priorPosition.y + delta.dy * scale)
        let residualX = median(horizontalMotions.map { abs($0 - delta.dx) }) * scale
        let residualY = median(verticalMotions.map { abs($0 - delta.dy) }) * scale
        var nextPatches = agreeing.map { $0.0.moved($0.1.motion) }
        var nextPending = [Patch]()
        let pendingFits = Self.fitBatch(reference.grid, grid, points: pending.map(\.point), backend: backend)
        for (patch, match) in zip(pending, pendingFits) {
            // Both calibration votes must have measured source axes. A later
            // estimate cannot independently confirm the first scale sample.
            guard let fit = match,
                  !patch.calibratedX || horizontalMeasured,
                  !patch.calibratedY || verticalMeasured,
                  let gain = renewedGain(patch.gain, motion: fit.motion, camera: delta) else { continue }
            let updated = patch.moved(fit.motion, gain: gain, votes: patch.votes + 1)
            if updated.votes >= 2 { nextPatches.append(updated) } else { nextPending.append(updated) }
        }
        patches = Array(nextPatches.prefix(Self.maximumPatches))
        pending = nextPending
        let candidates = candidatePoints(in: grid).filter { point in
            (patches + pending).allSatisfy({ hypot($0.point.x - point.x, $0.point.y - point.y) >= 9 })
        }
        let candidateFits = Self.fitBatch(reference.grid, grid, points: candidates, backend: backend)
        for (point, match) in zip(candidates, candidateFits) {
            guard patches.count + pending.count < Self.maximumPatches,
                  (patches + pending).allSatisfy({ hypot($0.point.x - point.x, $0.point.y - point.y) >= 9 }),
                  let fit = match,
                  let gain = initialGain(motion: fit.motion, camera: delta) else { continue }
            // Replenishment transfers an existing camera-plane calibration.
            // Agreement with an uncalibrated axis cannot create that evidence,
            // even after several generations of internally consistent patches.
            pending.append(Patch(point: CGPoint(x: point.x + fit.motion.dx, y: point.y + fit.motion.dy),
                gain: gain, calibratedX: horizontalMeasured && abs(delta.dx) >= 1,
                calibratedY: verticalMeasured && abs(delta.dy) >= 1, votes: 1))
        }
        position = nextPosition; self.reference = Sample(grid: grid, timestamp: timestamp)
        return Measurement(position: nextPosition, referenceTimestamp: reference.timestamp,
            timestamp: timestamp, supportedPatches: agreeing.count,
            horizontalMeasured: horizontalMeasured, verticalMeasured: verticalMeasured,
            residualX: residualX, residualY: residualY)
    }

    /// An unknown scale may support an estimate when no calibrated consensus
    /// survives. It must never outvote a measured axis or narrow its residual.
    private func axisMotions(_ matches: [(Patch, Fit)], horizontal: Bool) -> [CGFloat] {
        let calibrated = matches.filter { horizontal ? $0.0.calibratedX : $0.0.calibratedY }
        let voters = calibrated.count >= 3 ? calibrated : matches
        return voters.map {
            horizontal ? $0.1.motion.dx / $0.0.gain.dx : $0.1.motion.dy / $0.0.gain.dy
        }
    }

    private func candidatePoints(in grid: Grid) -> [CGPoint] {
        Array(stride(from: 14, to: grid.height - 11, by: 12).flatMap { y in
            stride(from: 12, to: grid.width - 11, by: 12).map { CGPoint(x: $0, y: y) }
        }.prefix(80))
    }
    private func initialGain(motion: CGVector, camera: CGVector) -> CGVector? {
        func axis(_ motion: CGFloat, _ camera: CGFloat) -> CGFloat? {
            if abs(camera) >= 1 { let value = motion / camera; return (0.4..<2.5).contains(value) ? value : nil }
            return abs(motion - camera) <= 0.5 ? 1 : nil
        }
        guard let x = axis(motion.dx, camera.dx), let y = axis(motion.dy, camera.dy) else { return nil }
        return CGVector(dx: x, dy: y)
    }
    private func renewedGain(_ previous: CGVector, motion: CGVector, camera: CGVector) -> CGVector? {
        func axis(_ prior: CGFloat, _ motion: CGFloat, _ camera: CGFloat) -> CGFloat? {
            if abs(camera) >= 1 {
                let value = motion / camera
                return (0.4..<2.5).contains(value) && abs(value - prior) <= 0.2 ? (prior + value) * 0.5 : nil
            }
            return abs(motion - prior * camera) <= 0.5 ? prior : nil
        }
        guard let x = axis(previous.dx, motion.dx, camera.dx), let y = axis(previous.dy, motion.dy, camera.dy) else { return nil }
        return CGVector(dx: x, dy: y)
    }
    private func median(_ values: [CGFloat]) -> CGFloat {
        let ordered = values.sorted(); let mid = ordered.count / 2
        return ordered.count % 2 == 0 ? (ordered[mid - 1] + ordered[mid]) * 0.5 : ordered[mid]
    }

    static func fitBatch(_ a: Grid, _ b: Grid, points: [CGPoint], near expected: CGVector? = nil,
                         backend: GroundComputeBackend = .automatic) -> [Fit?] {
        let scores = backend == .cpu ? nil : ScenePatchMetalCorrelator.shared.scores(a, b, points: points, near: expected)
        return points.enumerated().map { index, point in
            fit(a, b, point: point, near: expected, precomputed: scores?[index])
        }
    }

    /// Mean-centered correlation and a two-axis gradient refinement. Searches
    /// are bounded; ambiguous repeats, black patches and boundary optima fail.
    static func fit(_ a: Grid, _ b: Grid, point: CGPoint, near expected: CGVector? = nil,
                    precomputed: [ScenePatchMetalCorrelator.Score]? = nil) -> Fit? {
        guard a.width == b.width, a.height == b.height, point.x.isFinite, point.y.isFinite,
              a.width > 0, a.height > 0, a.pixels.count == a.width * a.height,
              b.pixels.count == b.width * b.height,
              point.x >= 0, point.x < CGFloat(a.width), point.y >= 0, point.y < CGFloat(a.height) else { return nil }
        if let expected {
            guard expected.dx.isFinite, expected.dy.isFinite,
                  abs(expected.dx) <= CGFloat(a.width), abs(expected.dy) <= CGFloat(a.height) else { return nil }
        }
        let width = a.width, height = a.height, x = Int(point.x.rounded()), y = Int(point.y.rounded())
        guard x >= 9, x < width - 9, y >= 7, y < height - 7 else { return nil }
        let rect = CGRect(x: x - 8, y: y - 6, width: 16, height: 12)
        guard !a.exclusions.contains(where: { $0.intersects(rect) }) else { return nil }
        var values = [Double](), gx = [Double](), gy = [Double]()
        for row in (y - 6)..<(y + 6) { for col in (x - 8)..<(x + 8) {
            let i = row * width + col
            values.append(a.pixels[i]); gx.append((a.pixels[i + 1] - a.pixels[i - 1]) * 0.5)
            gy.append((a.pixels[i + width] - a.pixels[i - width]) * 0.5)
        } }
        let mean = values.reduce(0, +) / 192
        let normalized = values.map { $0 - mean }, aa = normalized.reduce(0) { $0 + $1 * $1 }
        guard aa >= 192 * 4 else { return nil }
        let centerX = expected.map { Int($0.dx.rounded()) } ?? 0
        let centerY = expected.map { Int($0.dy.rounded()) } ?? 0
        let radiusX = expected == nil ? 8 : 2, radiusY = expected == nil ? 16 : 2
        var scores = precomputed ?? []
        if precomputed == nil {
        b.pixels.withUnsafeBufferPointer { pixels in
            normalized.withUnsafeBufferPointer { reference in
                for dy in (centerY - radiusY)...(centerY + radiusY) {
                    for dx in (centerX - radiusX)...(centerX + radiusX) {
                        guard x + dx >= 8, x + dx < width - 8, y + dy >= 6, y + dy < height - 6,
                              !b.exclusions.contains(where: { $0.intersects(rect.offsetBy(dx: CGFloat(dx), dy: CGFloat(dy))) }) else { continue }
                        var sum = 0.0, bb = 0.0, ab = 0.0, index = 0
                        for row in (y - 6 + dy)..<(y + 6 + dy) {
                            let start = row * width + x - 8 + dx
                            for col in 0..<16 {
                                let value = pixels[start + col]
                                sum += value; bb += value * value; ab += reference[index] * value; index += 1
                            }
                        }
                        let variance = bb - sum * sum / 192
                        guard variance >= 192 * 4 else { continue }
                        scores.append((ab / sqrt(aa * variance), dx, dy))
                    }
                }
            }
        }
        }
        scores.sort { $0.0 == $1.0 ? ($0.1 == $1.1 ? $0.2 > $1.2 : $0.1 > $1.1) : $0.0 > $1.0 }
        guard let best = scores.first, best.0 >= 0.88,
              abs(best.1 - centerX) < radiusX, abs(best.2 - centerY) < radiusY else { return nil }
        let alternative = scores.dropFirst().first { abs($0.1 - best.1) + abs($0.2 - best.2) >= 2 }
        guard best.0 - (alternative?.0 ?? -1) >= 0.02 else { return nil }
        var current = [Double]()
        for row in (y - 6 + best.2)..<(y + 6 + best.2) {
            for col in (x - 8 + best.1)..<(x + 8 + best.1) { current.append(b.pixels[row * width + col]) }
        }
        let currentMean = current.reduce(0, +) / 192
        let currentSigma = sqrt(current.reduce(0) { $0 + pow($1 - currentMean, 2) } / 192)
        let sigma = sqrt(aa / 192)
        var xx = 0.0, xy = 0.0, yy = 0.0, bx = 0.0, by = 0.0
        for i in 0..<192 {
            let x = gx[i] / sigma, y = gy[i] / sigma
            let residual = (current[i] - currentMean) / currentSigma - normalized[i] / sigma
            xx += x * x; xy += x * y; yy += y * y; bx -= x * residual; by -= y * residual
        }
        let determinant = xx * yy - xy * xy
        let separation = sqrt(pow(xx - yy, 2) + 4 * xy * xy)
        guard determinant > 0, xx + yy - separation > 0,
              (xx + yy + separation) / (xx + yy - separation) <= 100 else { return nil }
        let refinedX = max(-0.5, min(0.5, (yy * bx - xy * by) / determinant))
        let refinedY = max(-0.5, min(0.5, (xx * by - xy * bx) / determinant))
        return Fit(motion: CGVector(dx: CGFloat(best.1) + refinedX, dy: CGFloat(best.2) + refinedY), correlation: best.0)
    }
}
