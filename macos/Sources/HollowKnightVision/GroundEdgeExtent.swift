/// The 32-column filter finds a ridge reliably but blurs its ends. Around each
/// accepted endpoint, follow the same vertical response through a narrower
/// seven-column window, up to the coarse filter's half-width.
final class GroundEdgeExtent {
    private let analysis: GroundComparisonAnalysis
    private var cachedRow = -1
    private var prefix: [Int] = []

    init(_ analysis: GroundComparisonAnalysis) {
        self.analysis = analysis
    }

    func refined(_ ranges: [ClosedRange<Int>], row y: Int, threshold: Int) -> [ClosedRange<Int>] {
        let width = analysis.width
        guard let first = ranges.first, let last = ranges.last,
              let luma = analysis.sourceLuma, y >= 4, y + 4 < analysis.height else { return ranges }
        if cachedRow != y {
            cachedRow = y
            prefix = [Int](repeating: 0, count: width + 1)
            for x in 0..<width {
                var value = 0
                for (dy, weight) in GroundLineDetector.comparisonWeights.enumerated() {
                    let index = (y + dy - 4) * width + x
                    if analysis.validSourcePixels?[index] == 0 || analysis.validSourcePixels?[index + width] == 0 {
                        value = 0
                        break
                    }
                    value += weight * abs(Int(luma[index]) - Int(luma[index + width]))
                }
                prefix[x + 1] = prefix[x] + value
            }
        }
        func strong(_ x: Int) -> Bool {
            guard analysis.validSourcePixels?[y * width + x] != 0 else { return false }
            let a = max(0, x - 3), b = min(width, x + 4)
            return prefix[b] - prefix[a] >= 10 * threshold * (b - a)
        }
        let radius = GroundLineDetector.comparisonKernelSpan / 2
        func edge(_ range: ClosedRange<Int>, left: Bool) -> Int {
            let end = left ? range.lowerBound : range.upperBound
            let dx = left ? -1 : 1
            var x = end
            while !strong(x) {
                if x == (left ? range.upperBound : range.lowerBound) || abs(x - end) == radius { return end }
                x -= dx
            }
            while abs(x + dx - end) <= radius, (0..<width).contains(x + dx), strong(x + dx) { x += dx }
            // The narrow window can still extend beyond a sharp drop into empty
            // space. Snap those last few pixels back to measured edge response.
            for offset in 0...3 {
                let column = x - dx * offset
                guard (0..<width).contains(column), abs(column - end) <= radius,
                      left ? column <= range.upperBound : column >= range.lowerBound else { break }
                if prefix[column + 1] - prefix[column] >= 5 * threshold { return column }
            }
            return x
        }
        let start = edge(first, left: true)
        let end = edge(last, left: false)
        guard start <= end else { return ranges }
        var result = ranges
        if result.count == 1 { return [start...end] }
        result[0] = start...first.upperBound
        result[result.count - 1] = last.lowerBound...end
        return result
    }
}
