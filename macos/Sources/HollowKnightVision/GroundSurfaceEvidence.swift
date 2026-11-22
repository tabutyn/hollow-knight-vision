import Foundation

/// Builds a horizontal prefix sum only for candidate rows. Each pixel below a
/// row contributes once to the shallow body sum. A bright cap can also be
/// supported by a darker silhouette farther below its edge.
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
        if count == 0 || sum <= maximumBodyLuma * count { return true }
        // Fog can brighten a platform cap. Verify the deeper silhouette
        // against air in the same unmasked columns.
        guard row >= 5, row + 24 < analysis.height else { return false }
        let valid = analysis.validSourcePixels
        var measuredColumns = 0
        var solidColumns = 0
        for range in ranges {
            for x in stride(from: max(0, range.lowerBound), through: min(width - 1, range.upperBound), by: 3) {
                let i = row * width + x
                if let valid, valid[i] == 0 || valid[i - 5 * width] == 0 || valid[i - 2 * width] == 0
                    || valid[i + 13 * width] == 0 || valid[i + 18 * width] == 0 || valid[i + 24 * width] == 0 {
                    continue
                }
                measuredColumns += 1
                let air = (Int(luma[i - 5 * width]) + Int(luma[i - 2 * width])) / 2
                let deep = (Int(luma[i + 13 * width]) + Int(luma[i + 18 * width]) + Int(luma[i + 24 * width])) / 3
                if deep <= maximumBodyLuma && air - deep >= 12 { solidColumns += 1 }
            }
        }
        return solidColumns >= 8 && solidColumns * 2 >= measuredColumns
    }
}
