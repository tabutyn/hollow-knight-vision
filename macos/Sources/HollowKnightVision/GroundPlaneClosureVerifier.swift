import CoreGraphics
import Foundation

/// Broad scenery proposes a loop; observed ground must independently verify its
/// camera-plane displacement. Background parallax cannot establish a map link.
enum GroundPlaneClosureVerifier {
    struct Measurement: Equatable {
        let cameraPose: CGPoint
        let supportedPatches: Int
        let horizontalSpan: CGFloat
        let meanCorrelation: Double
    }
    private struct Span { let y: CGFloat; var lower: CGFloat; var upper: CGFloat }
    private struct Candidate {
        let dx: CGFloat
        let dy: CGFloat
        let step: Int
        let count: Int
        let span: CGFloat
        let correlation: Double
        let score: Double
    }

    static func verify(reference: CGImage, referenceGround: LiveWorldGroundGeometry,
                       referencePose: CGPoint, current: CGImage,
                       currentGround: LiveWorldGroundGeometry, proposedPose: CGPoint,
                       solveWidth: CGFloat) -> Measurement? {
        guard solveWidth.isFinite, solveWidth > 0,
              [referencePose.x, referencePose.y, proposedPose.x, proposedPose.y].allSatisfy(\.isFinite),
              referenceGround.isValid(solveWidth: solveWidth, sourceWidth: reference.width,
                                      sourceHeight: reference.height),
              currentGround.isValid(solveWidth: solveWidth, sourceWidth: current.width,
                                    sourceHeight: current.height) else { return nil }
        let width = min(640, reference.width, current.width)
        guard width >= 160 else { return nil }
        let height = Int((CGFloat(reference.height) * CGFloat(width) / CGFloat(reference.width)).rounded())
        let currentHeight = Int((CGFloat(current.height) * CGFloat(width) / CGFloat(current.width)).rounded())
        guard height == currentHeight,
              let a = GroundPixels.luma(reference, width: width, height: height),
              let b = GroundPixels.luma(current, width: width, height: height) else { return nil }
        let pixelsPerSolve = CGFloat(width) / solveWidth
        let unit = solveWidth / 640
        let referenceSpans = merged(referenceGround), currentSpans = merged(currentGround)
        let proposedDX = proposedPose.x - referencePose.x
        let proposedDY = proposedPose.y - referencePose.y
        var vertical = [CGFloat]()
        for source in referenceSpans {
            for target in currentSpans {
                let dy = source.y - target.y
                if abs(dy - proposedDY) <= 8 * unit,
                   !vertical.contains(where: { abs($0 - dy) < 0.25 * unit }) {
                    vertical.append(dy)
                }
            }
        }
        vertical.sort {
            let a = abs($0 - proposedDY), b = abs($1 - proposedDY)
            return a == b ? $0 < $1 : a < b
        }
        let radius = max(2, Int((8 * unit * pixelsPerSolve).rounded()))
        var candidates = [Candidate]()
        for dy in vertical.prefix(6) {
            for step in -radius...radius {
                let dx = proposedDX + CGFloat(step) / pixelsPerSolve
                if let candidate = evaluate(a: a, b: b, width: width, height: height,
                    source: referenceSpans, target: currentSpans, dx: dx, dy: dy,
                    step: step, scale: pixelsPerSolve, unit: unit) {
                    candidates.append(candidate)
                }
            }
        }
        candidates.sort {
            if $0.score != $1.score { return $0.score > $1.score }
            if abs($0.step) != abs($1.step) { return abs($0.step) < abs($1.step) }
            if $0.dy != $1.dy { return $0.dy < $1.dy }
            return $0.dx < $1.dx
        }
        guard let best = candidates.first, abs(best.step) < radius else { return nil }
        // Nearby samples describe the same fit. Another distinct placement with
        // comparable support is ambiguity, even when both floors look textured.
        if let alternative = candidates.dropFirst().first(where: {
            hypot($0.dx - best.dx, $0.dy - best.dy) > 3 * unit
        }), best.score - alternative.score < 0.025 { return nil }
        return Measurement(cameraPose: CGPoint(x: referencePose.x + best.dx,
                                               y: referencePose.y + best.dy),
            supportedPatches: best.count, horizontalSpan: best.span,
            meanCorrelation: best.correlation)
    }

    private static func merged(_ geometry: LiveWorldGroundGeometry) -> [Span] {
        let lines = geometry.lines.sorted {
            $0.start.y == $1.start.y ? $0.start.x < $1.start.x : $0.start.y < $1.start.y
        }
        var spans = [Span]()
        for line in lines {
            if let last = spans.last, last.y == line.start.y, line.start.x <= last.upper {
                spans[spans.count - 1].upper = max(last.upper, line.end.x)
            } else { spans.append(Span(y: line.start.y, lower: line.start.x, upper: line.end.x)) }
        }
        return spans
    }

    private static func evaluate(a: [UInt8], b: [UInt8], width: Int, height: Int,
                                 source: [Span], target: [Span], dx: CGFloat, dy: CGFloat,
                                 step: Int, scale: CGFloat, unit: CGFloat) -> Candidate? {
        var points = [CGPoint]()
        let halfWidth = 8 / scale
        for first in source {
            for second in target where abs(first.y - dy - second.y) <= unit {
                let lower = max(first.lower, second.lower + dx) + halfWidth
                let upper = min(first.upper, second.upper + dx) - halfWidth
                guard upper >= lower else { continue }
                let stride = 16 / scale
                var x = (lower / stride).rounded(.up) * stride
                while x <= upper {
                    points.append(CGPoint(x: x, y: first.y)); x += stride
                }
            }
        }
        guard points.count >= 4 else { return nil }
        // Bound work while keeping support distributed across the whole overlap.
        let selected = points.count <= 16 ? points : (0..<16).map {
            points[Int((Double($0) * Double(points.count - 1) / 15).rounded())]
        }
        var correlations = [Double](), xs = [CGFloat]()
        for point in selected {
            let ax = point.x * scale - 8
            let ay = CGFloat(height) - point.y * scale - 2
            let bx = (point.x - dx) * scale - 8
            let by = CGFloat(height) - (point.y - dy) * scale - 2
            guard let correlation = correlation(a: a, b: b, width: width, height: height,
                ax: ax, ay: ay, bx: bx, by: by), correlation >= 0.84 else { continue }
            correlations.append(correlation); xs.append(point.x)
        }
        guard correlations.count >= 4,
              Double(correlations.count) / Double(selected.count) >= 0.65,
              let low = xs.min(), let high = xs.max(), high - low >= 48 * unit else { return nil }
        let mean = correlations.reduce(0, +) / Double(correlations.count)
        guard mean >= 0.88 else { return nil }
        return Candidate(dx: dx, dy: dy, step: step, count: correlations.count,
            span: high - low + 16 / scale, correlation: mean,
            score: mean * Double(correlations.count) / Double(selected.count))
    }

    private static func correlation(a: [UInt8], b: [UInt8], width: Int, height: Int,
                                    ax: CGFloat, ay: CGFloat, bx: CGFloat, by: CGFloat) -> Double? {
        guard min(ax, bx) >= 0, min(ay, by) >= 0,
              max(ax, bx) + 16 < CGFloat(width), max(ay, by) + 12 < CGFloat(height) else { return nil }
        func value(_ pixels: [UInt8], _ x: CGFloat, _ y: CGFloat) -> Double {
            let ix = Int(x), iy = Int(y), fx = Double(x - CGFloat(ix)), fy = Double(y - CGFloat(iy))
            let lower = Double(pixels[iy * width + ix]) * (1 - fx) + Double(pixels[iy * width + ix + 1]) * fx
            let upper = Double(pixels[(iy + 1) * width + ix]) * (1 - fx) + Double(pixels[(iy + 1) * width + ix + 1]) * fx
            return lower * (1 - fy) + upper * fy
        }
        var aa = 0.0, bb = 0.0, ab = 0.0, sumA = 0.0, sumB = 0.0
        for y in 0..<12 {
            for x in 0..<16 {
                let av = value(a, ax + CGFloat(x), ay + CGFloat(y))
                let bv = value(b, bx + CGFloat(x), by + CGFloat(y))
                sumA += av; sumB += bv; aa += av * av; bb += bv * bv; ab += av * bv
            }
        }
        let count = 192.0
        let varianceA = aa - sumA * sumA / count, varianceB = bb - sumB * sumB / count
        guard varianceA >= count * 4, varianceB >= count * 4 else { return nil }
        return (ab - sumA * sumB / count) / sqrt(varianceA * varianceB)
    }
}
