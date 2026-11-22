// Locate rows of thin bright upright strokes ending on a common baseline.
// A credit over gameplay has this structure regardless of its wording or position.
final class GroundCaptionEvidence {
    private let analysis: GroundComparisonAnalysis
    private var rangesByBaseline: [Int: [ClosedRange<Int>]] = [:]

    init(_ analysis: GroundComparisonAnalysis) {
        self.analysis = analysis
    }

    func removingText(from runs: [ClosedRange<Int>], row: Int) -> [ClosedRange<Int>] {
        let firstBaseline = max(2, row - 2)
        let lastBaseline = min(analysis.height - 4, row + 12)
        guard analysis.width >= 48, firstBaseline <= lastBaseline else { return runs }
        var result = runs
        for baseline in firstBaseline...lastBaseline {
            let ranges: [ClosedRange<Int>]
            if let cached = rangesByBaseline[baseline] {
                ranges = cached
            } else {
                ranges = captionRanges(endingAt: baseline)
                rangesByBaseline[baseline] = ranges
            }
            for mask in ranges {
                result = result.flatMap { run in
                    guard run.overlaps(mask) else { return [run] }
                    var parts: [ClosedRange<Int>] = []
                    if run.lowerBound < mask.lowerBound { parts.append(run.lowerBound...(mask.lowerBound - 1)) }
                    if mask.upperBound < run.upperBound { parts.append((mask.upperBound + 1)...run.upperBound) }
                    return parts
                }
            }
        }
        return result
    }

    private func captionRanges(endingAt y: Int) -> [ClosedRange<Int>] {
        guard let luma = analysis.sourceLuma else { return [] }
        let width = analysis.width
        func brightStroke(_ i: Int) -> Bool {
            let center = Int(luma[i])
            if center < 64 { return false }
            let sides = max(min(luma[i - 2], luma[i - 3]), min(luma[i + 2], luma[i + 3]))
            return center - Int(sides) > 24
        }
        var masks: [ClosedRange<Int>] = []
        var start = 0, prior = -32, count = 0
        func finish() {
            if count >= 10 && prior - start >= 32 {
                let pad = GroundLineDetector.comparisonKernelSpan / 2
                masks.append(max(0, start - pad)...min(width - 1, prior + pad))
            }
        }
        for x in 3..<(width - 3) {
            let i = y * width + x
            guard brightStroke(i), brightStroke(i - width), brightStroke(i - 2 * width),
                  !brightStroke(i + 2 * width), !brightStroke(i + 3 * width),
                  analysis.validSourcePixels?[i] != 0 else { continue }
            if x - prior > 16 {
                finish()
                start = x
                count = 1
            } else if x - prior > 1 {
                count += 1
            }
            prior = x
        }
        finish()
        return masks
    }
}
