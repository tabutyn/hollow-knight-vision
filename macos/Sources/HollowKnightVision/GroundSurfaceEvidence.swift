import Foundation

/// Builds a horizontal prefix sum only for candidate rows. Each pixel below a
/// row is sampled once, then every overlapping candidate is scored in O(1).
/// Missing or masked pixels are unknown, never votes for a dark surface.
final class GroundSurfaceEvidence {
    private struct Band {
        let sum: [Int]
        let count: [Int]
    }
    private let analysis: GroundComparisonAnalysis
    private var bands = [Int: Band]()
    private let depth = 12
    private let maximumBodyLuma = 55

    init(_ analysis: GroundComparisonAnalysis) {
        self.analysis = analysis
    }

    func supports(row: Int, ranges: [ClosedRange<Int>]) -> Bool {
        guard row >= 0, row < analysis.height - 1,
              let luma = analysis.sourceLuma,
              luma.count == analysis.width * analysis.height else { return true }
        let width = analysis.width
        if bands[row] == nil {
            let valid = analysis.validSourcePixels
            var sums = [Int](repeating: 0, count: width + 1)
            var counts = sums
            for x in 0..<width {
                var sum = 0, count = 0
                if valid?[row * width + x] != 0 {
                    for offset in 1...min(depth, analysis.height - row - 1) {
                        let index = (row + offset) * width + x
                        if valid?[index] != 0 {
                            sum += Int(luma[index]); count += 1
                        }
                    }
                }
                sums[x + 1] = sums[x] + sum
                counts[x + 1] = counts[x] + count
            }
            bands[row] = Band(sum: sums, count: counts)
        }
        guard let band = bands[row] else { return true }
        var sum = 0, count = 0
        for range in ranges {
            let lower = max(0, range.lowerBound)
            let upper = min(width, range.upperBound + 1)
            if lower < upper {
                sum += band.sum[upper] - band.sum[lower]
                count += band.count[upper] - band.count[lower]
            }
        }
        return count == 0 || sum <= maximumBodyLuma * count
    }
}
