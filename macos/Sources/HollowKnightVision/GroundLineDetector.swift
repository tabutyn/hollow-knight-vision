import CoreGraphics
import CoreImage
import Foundation

struct GroundLineSegment: Equatable {
    let frameY: CGFloat
    let minimumFrameX: CGFloat
    let maximumFrameX: CGFloat
    let confidence: CGFloat
}

struct GroundLineDetection: Equatable {
    let frameY: CGFloat
    let segments: [GroundLineSegment]
    let notchFrameXs: [CGFloat]
    let confidence: CGFloat
}

struct GroundEdgeAnalysis {
    let width: Int
    let height: Int
    let groundEdgePixels: [UInt8]
    var sourceLuma: [UInt8]? = nil
    var validSourcePixels: [UInt8]? = nil
    /// Opaque capture letterboxing is not a world edge. Nil preserves the
    /// full domain for callers supplying an already computed edge image.
    var validRows: Range<Int>? = nil
}

struct GroundComparisonAnalysis {
    let width: Int
    let height: Int
    let groundPixels: [UInt8]
    var sourceLuma: [UInt8]? = nil
    var validSourcePixels: [UInt8]? = nil
}

struct GroundTheoryTuning: Equatable {
    var groundThreshold: Int
    var minimumSegmentLength: Int
    var lineSeparation: Int
    var occlusionMergeGap: Int

    init(
        groundThreshold: Int,
        minimumSegmentLength: Int,
        lineSeparation: Int = 4,
        occlusionMergeGap: Int = 0
    ) {
        self.groundThreshold = groundThreshold
        self.minimumSegmentLength = minimumSegmentLength
        self.lineSeparation = lineSeparation
        self.occlusionMergeGap = occlusionMergeGap
    }

    static let `default` = GroundTheoryTuning(
        groundThreshold: 10,
        minimumSegmentLength: 48,
        lineSeparation: 36,
        occlusionMergeGap: 180
    )

    /// Semantic ground can use weaker edge evidence after the source image
    /// independently shows a solid platform body. Camera tracking keeps the
    /// conservative default so semantic tuning cannot move the pose.
    static let semanticDefault = GroundTheoryTuning(
        groundThreshold: 8,
        minimumSegmentLength: 40,
        lineSeparation: 40,
        occlusionMergeGap: 180
    )

    /// Developer-only launch overrides make repeatable path sweeps possible
    /// without changing the values presented to ordinary users between runs.
    static func launchTuning(
        arguments: [String], base: GroundTheoryTuning = .default
    ) -> GroundTheoryTuning {
        func value(_ name: String, fallback: Int, range: ClosedRange<Int>) -> Int {
            let prefix = "--\(name)="
            guard let raw = arguments.last(where: { $0.hasPrefix(prefix) })?
                .dropFirst(prefix.count),
                  let parsed = Int(raw)
            else { return fallback }
            return min(range.upperBound, max(range.lowerBound, parsed))
        }
        return GroundTheoryTuning(
            groundThreshold: value(
                "ground-threshold", fallback: base.groundThreshold, range: 1...255
            ),
            minimumSegmentLength: value(
                "ground-minimum-segment",
                fallback: base.minimumSegmentLength,
                range: 1...2048
            ),
            lineSeparation: value(
                "ground-line-separation", fallback: base.lineSeparation, range: 0...512
            ),
            occlusionMergeGap: value(
                "ground-occlusion-gap", fallback: base.occlusionMergeGap, range: 0...4096
            )
        )
    }
}

fileprivate struct GroundCandidateRun {
    let range: ClosedRange<Int>
    let evidenceRanges: [ClosedRange<Int>]
}

fileprivate struct GroundTheoryStages {
    let acceptedRunsByRow: [[GroundCandidateRun]]
    let selectedRows: [Int]
}

/// One frame's line extraction, shared by tracking and all debug renderers.
struct GroundTheoryAnalysis {
    let comparison: GroundComparisonAnalysis
    let tuning: GroundTheoryTuning
    fileprivate let stages: GroundTheoryStages
}

struct CleanFloorLine: Equatable {
    let row: Int
    let xRange: ClosedRange<Int>
    let evidenceRanges: [ClosedRange<Int>]

    init(
        row: Int,
        xRange: ClosedRange<Int>,
        evidenceRanges: [ClosedRange<Int>]? = nil
    ) {
        self.row = row
        self.xRange = xRange
        self.evidenceRanges = evidenceRanges ?? [xRange]
    }
}

/// Builds a full-resolution vertical one-pixel difference, correlates it with
    /// the horizontal ground kernel, then turns strong response islands into
/// dominant, occlusion-bridged ground theories.
enum GroundLineDetector {
    static let comparisonKernelSpan = 32
    static let comparisonWeights = [-1, -2, -3, -4, 4, 3, 2, 1]

    static func analyze(
        _ image: CGImage,
        excluding excludedRects: [CGRect] = []
    ) -> GroundEdgeAnalysis? {
        let width = image.width
        let height = image.height
        guard width > 1, height > 1,
              let luma = GroundPixels.luma(image, width: width, height: height)
        else { return nil }

        // Pad beyond the comparison-kernel footprint so convolution cannot
        // smear valid response back into a Knight/HUD exclusion.
        let exclusions = excludedRects.filter {
            !$0.isNull && $0.minX.isFinite && $0.maxX.isFinite
                && $0.minY.isFinite && $0.maxY.isFinite
        }.map {
            $0.insetBy(
                dx: -CGFloat(8 + comparisonKernelSpan / 2),
                dy: -CGFloat(8 + comparisonWeights.count / 2)
            )
        }
        var groundEdges = [UInt8](repeating: 0, count: width * height)
        var validSourcePixels = [UInt8](repeating: 1, count: width * height)
        luma.withUnsafeBufferPointer { input in
            groundEdges.withUnsafeMutableBufferPointer { output in
                for index in 0..<(width * (height - 1)) {
                    output[index] = UInt8(abs(Int(input[index]) - Int(input[index + width])))
                }
                // Rasterize each rectangle once. Pixel-center rounding keeps
                // exactly the same mask as the old per-pixel CGRect checks.
                for rect in exclusions {
                    let x0 = Int(max(0, min(CGFloat(width), ceil(rect.minX - 0.5))))
                    let x1 = Int(max(-1, min(CGFloat(width - 1), floor(rect.maxX - 0.5))))
                    let y0 = Int(max(0, min(CGFloat(height), ceil(CGFloat(height) - 0.5 - rect.maxY))))
                    let y1 = Int(max(-1, min(CGFloat(height - 1), floor(CGFloat(height) - 0.5 - rect.minY))))
                    guard x0 <= x1, y0 <= y1 else { continue }
                    for row in y0...y1 {
                        output.baseAddress!.advanced(by: row * width + x0)
                            .update(repeating: 0, count: x1 - x0 + 1)
                    }
                }
            }
        }
        for rect in exclusions {
            let x0 = Int(max(0, min(CGFloat(width), ceil(rect.minX - 0.5))))
            let x1 = Int(max(-1, min(CGFloat(width - 1), floor(rect.maxX - 0.5))))
            let y0 = Int(max(0, min(CGFloat(height), ceil(CGFloat(height) - 0.5 - rect.maxY))))
            let y1 = Int(max(-1, min(CGFloat(height - 1), floor(CGFloat(height) - 0.5 - rect.minY))))
            guard x0 <= x1, y0 <= y1 else { continue }
            for row in y0...y1 {
                validSourcePixels.replaceSubrange(
                    (row * width + x0)...(row * width + x1),
                    with: repeatElement(0, count: x1 - x0 + 1)
                )
            }
        }
        var contentTop = 0, contentBottom = height
        func blackRow(_ row: Int) -> Bool {
            luma[(row * width)..<((row + 1) * width)].allSatisfy { $0 <= 1 }
        }
        while contentTop < min(12, height), blackRow(contentTop) { contentTop += 1 }
        while contentBottom > max(0, height - 12), blackRow(contentBottom - 1) { contentBottom -= 1 }
        // Only trim narrow boundary bands; a large black scene region is
        // ordinary content, not evidence of capture padding.
        if contentTop == 12 { contentTop = 0 }
        if height - contentBottom == 12 { contentBottom = height }
        if contentTop > 0 {
            validSourcePixels.replaceSubrange(
                0..<(contentTop * width),
                with: repeatElement(0, count: contentTop * width)
            )
        }
        if contentBottom < height {
            validSourcePixels.replaceSubrange(
                (contentBottom * width)..<(height * width),
                with: repeatElement(0, count: (height - contentBottom) * width)
            )
        }
        return GroundEdgeAnalysis(
            width: width,
            height: height,
            groundEdgePixels: groundEdges,
            sourceLuma: luma,
            validSourcePixels: validSourcePixels,
            validRows: contentTop == 0 && contentBottom == height
                ? nil : contentTop..<max(contentTop, contentBottom)
        )
    }

    static func compare(_ analysis: GroundEdgeAnalysis) -> GroundComparisonAnalysis {
        GroundComparisonAnalysis(
            width: analysis.width,
            height: analysis.height,
            groundPixels: groundDetectPixels(from: analysis),
            sourceLuma: analysis.sourceLuma,
            validSourcePixels: analysis.validSourcePixels
        )
    }

    static func edgeImage(from analysis: GroundEdgeAnalysis) -> CGImage? {
        grayscaleImage(
            analysis.groundEdgePixels,
            width: analysis.width,
            height: analysis.height
        )
    }

    static func groundDetectImage(from analysis: GroundComparisonAnalysis) -> CGImage? {
        grayscaleImage(analysis.groundPixels, width: analysis.width, height: analysis.height)
    }

    /// Progressive ground review. Cyan is raw threshold evidence and green is
    /// evidence belonging to a contiguous island that survives Minimum Segment.
    static func groundStageImage(
        from analysis: GroundComparisonAnalysis,
        tuning: GroundTheoryTuning
    ) -> CGImage? {
        theory(from: analysis, tuning: tuning).flatMap(groundStageImage)
    }

    static func groundStageImage(from theory: GroundTheoryAnalysis) -> CGImage? {
        let analysis = theory.comparison
        let tuning = theory.tuning
        let stages = theory.stages
        let threshold = UInt8(clamping: tuning.groundThreshold)
        var pixels = [UInt8](repeating: 0, count: analysis.width * analysis.height * 4)
        for index in analysis.groundPixels.indices
            where analysis.groundPixels[index] >= threshold {
            paint(&pixels, index: index, color: (0, 230, 255, 255))
        }
        for row in stages.acceptedRunsByRow.indices {
            let offset = row * analysis.width
            for run in stages.acceptedRunsByRow[row] {
                for x in run.range where analysis.groundPixels[offset + x] >= threshold {
                    paint(&pixels, index: offset + x, color: (70, 255, 120, 255))
                }
            }
        }
        return rgbaImage(pixels, width: analysis.width, height: analysis.height)
    }

    /// Converts accepted evidence into one clean horizontal line per floor.
    static func cleanedImage(
        from analysis: GroundComparisonAnalysis,
        tuning: GroundTheoryTuning
    ) -> CGImage? {
        guard let stages = theoryStages(in: analysis, tuning: tuning) else { return nil }
        return cleanedImage(
            from: cleanFloorLines(from: stages),
            width: analysis.width,
            height: analysis.height
        )
    }

    static func cleanedImage(
        from floors: [CleanFloorLine],
        width: Int,
        height: Int
    ) -> CGImage? {
        guard width > 0, height > 0 else { return nil }
        var pixels = [UInt8](repeating: 0, count: width * height * 4)

        for floor in floors {
            guard floor.row >= 0, floor.row < height else { continue }
            for x in floor.xRange where x >= 0 && x < width {
                paint(
                    &pixels,
                    index: floor.row * width + x,
                    color: (70, 255, 120, 255)
                )
            }
        }

        return rgbaImage(pixels, width: width, height: height)
    }

    static func presenceImage(
        from reviews: [GroundLinePresenceReview], width: Int, height: Int
    ) -> CGImage? {
        guard width > 0, height > 0 else { return nil }
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for review in displayPresenceReviews(reviews) {
            let color: (UInt8, UInt8, UInt8, UInt8)
            switch review.state {
            case .observing: color = (0, 200, 255, 255)
            case .confirmed: color = (70, 255, 120, 255)
            case .rejected: color = (255, 230, 0, 255)
            }
            guard review.line.row >= 0, review.line.row < height else { continue }
            for x in review.line.xRange where x >= 0 && x < width {
                paint(&pixels, index: review.line.row * width + x, color: color)
            }
        }
        return rgbaImage(pixels, width: width, height: height)
    }

    /// A stale rejected projection must not cover a presently confirmed
    /// surface. Eight pixels matches the detector kernel height: records this
    /// close with shared columns describe the same rendered edge, not two
    /// independently reviewable platforms. Confirmed lines paint last too.
    static func displayPresenceReviews(
        _ reviews: [GroundLinePresenceReview]
    ) -> [GroundLinePresenceReview] {
        let confirmed = reviews.filter { $0.state == .confirmed }
        return reviews.filter { review in
            guard review.state == .rejected else { return true }
            return !confirmed.contains { accepted in
                abs(accepted.line.row - review.line.row) <= 8
                    && min(accepted.line.xRange.upperBound, review.line.xRange.upperBound)
                        - max(accepted.line.xRange.lowerBound, review.line.xRange.lowerBound) + 1 >= 8
            }
        }.sorted {
            func priority(_ state: GroundLinePresenceState) -> Int {
                switch state {
                case .rejected: return 0
                case .observing: return 1
                case .confirmed: return 2
                }
            }
            return priority($0.state) < priority($1.state)
        }
    }

    /// Feature review shows only accepted world structure. Observing and
    /// rejected records remain active in lifecycle decisions but are not
    /// rendered as cyan/yellow atlas diagnostics.
    static func featureHypothesisPresenceReviews(
        _ reviews: [GroundLinePresenceReview]
    ) -> [GroundLinePresenceReview] {
        displayPresenceReviews(reviews).filter { $0.state == .confirmed }
    }

    static func cleanFloorLines(
        from analysis: GroundComparisonAnalysis,
        tuning: GroundTheoryTuning
    ) -> [CleanFloorLine] {
        theory(from: analysis, tuning: tuning).map(cleanFloorLines) ?? []
    }

    static func theory(from analysis: GroundComparisonAnalysis,
                       tuning: GroundTheoryTuning) -> GroundTheoryAnalysis? {
        theoryStages(in: analysis, tuning: tuning).map {
            GroundTheoryAnalysis(comparison: analysis, tuning: tuning, stages: $0)
        }
    }

    static func cleanFloorLines(from theory: GroundTheoryAnalysis) -> [CleanFloorLine] {
        cleanFloorLines(from: theory.stages)
    }

    static func semanticTheory(
        from analysis: GroundComparisonAnalysis, tuning: GroundTheoryTuning
    ) -> GroundTheoryAnalysis? {
        theoryStages(in: analysis, tuning: tuning, validatesSurface: true).map {
            GroundTheoryAnalysis(comparison: analysis, tuning: tuning, stages: $0)
        }
    }

    static func semanticFloorLines(
        from analysis: GroundComparisonAnalysis, tuning: GroundTheoryTuning
    ) -> [CleanFloorLine] {
        guard let theory = semanticTheory(from: analysis, tuning: tuning)
        else { return [] }
        return cleanFloorLines(from: theory)
    }

    /// Object boxes use bottom-left image coordinates; detector rows use a
    /// top-left origin. Reject only lines mostly covered by known foreground,
    /// preserving long real floors that continue behind an occlusion.
    static func rejectingKnownForegroundLines(
        _ lines: [CleanFloorLine],
        imageHeight: Int,
        foregroundRects: [CGRect],
        knightRect: CGRect? = nil
    ) -> [CleanFloorLine] {
        guard imageHeight > 0,
              !foregroundRects.isEmpty || knightRect != nil
        else { return lines }
        func topLeftRect(_ rect: CGRect) -> CGRect? {
            guard !rect.isNull, !rect.isEmpty,
                  rect.minX.isFinite, rect.maxX.isFinite,
                  rect.minY.isFinite, rect.maxY.isFinite
            else { return nil }
            return CGRect(
                x: rect.minX,
                y: CGFloat(imageHeight) - rect.maxY,
                width: rect.width,
                height: rect.height
            ).insetBy(dx: -8, dy: -4)
        }
        let topLeftRects = foregroundRects.compactMap { rect -> CGRect? in
            topLeftRect(rect)
        }
        let topLeftKnight = knightRect.flatMap(topLeftRect).map { detected in
            // Sparse training can localize only a small high-contrast part of
            // the Knight. Keep its detected bottom edge, but infer enough
            // vertical body height to suppress head/torso floor responses.
            let inferredHeight = max(detected.height, CGFloat(imageHeight) * 0.12)
            return CGRect(
                x: detected.minX,
                y: detected.maxY - inferredHeight,
                width: detected.width,
                height: inferredHeight
            )
        }
        guard !topLeftRects.isEmpty || topLeftKnight != nil else { return lines }
        return lines.filter { line in
            let row = CGFloat(line.row) + 0.5
            let lower = CGFloat(line.xRange.lowerBound)
            let upper = CGFloat(line.xRange.upperBound + 1)
            let span = max(1, upper - lower)
            // Reject a Knight-derived response only when most of its actual
            // edge evidence belongs to the body. A small, jittering object box
            // must not erase a room-wide floor and turn mask changes into
            // negative line-presence votes. Merged gaps are not evidence.
            if let knight = topLeftKnight {
                let upperBodyBottom = knight.minY + knight.height * 0.82
                if row >= knight.minY, row < upperBodyBottom {
                    var total: CGFloat = 0
                    var covered: CGFloat = 0
                    for evidence in line.evidenceRanges {
                        let start = max(lower, CGFloat(evidence.lowerBound))
                        let end = min(upper, CGFloat(evidence.upperBound + 1))
                        total += max(0, end - start)
                        covered += max(0, min(end, knight.maxX) - max(start, knight.minX))
                    }
                    if total > 0, covered / total >= 0.55 { return false }
                }
            }
            return !topLeftRects.contains { rect in
                guard row >= rect.minY, row <= rect.maxY else { return false }
                let overlap = max(0, min(upper, rect.maxX) - max(lower, rect.minX))
                return overlap / span >= 0.55
            }
        }
    }

    /// Applies the requested 32-column by 8-row kernel. Integer weights are
    /// the requested quarter-unit weights multiplied by four. Positive matches
    /// retain their fixed 0...255 contrast scale; the opposite polarity is black.
    static func groundDetectPixels(from analysis: GroundEdgeAnalysis) -> [UInt8] {
        let width = analysis.width
        let height = analysis.height
        let kernelWidth = comparisonKernelSpan
        let halfWidth = kernelWidth / 2
        var output = [UInt8](repeating: 0, count: width * height)
        guard width >= kernelWidth, height >= comparisonWeights.count,
              analysis.groundEdgePixels.count == width * height
        else { return output }

        var horizontalSums = [Int32](repeating: 0, count: width * height)
        for row in 0..<height {
            let offset = row * width
            var sum = analysis.groundEdgePixels[offset..<(offset + kernelWidth)]
                .reduce(0) { $0 + Int($1) }
            for x in halfWidth...(width - halfWidth) {
                horizontalSums[offset + x] = Int32(sum)
                if x < width - halfWidth {
                    sum -= Int(analysis.groundEdgePixels[offset + x - halfWidth])
                    sum += Int(analysis.groundEdgePixels[offset + x + halfWidth])
                }
            }
        }

        let halfHeight = comparisonWeights.count / 2
        // Horizontal sums cover 32 pixels and the positive half of the
        // vertical kernel sums to 10. Dividing by 320 converts the response
        // back to average luma contrast. Scale directly into the final image:
        // retaining a second full-resolution Int32 response buffer and then
        // scanning it again cost memory bandwidth without changing a result.
        let fixedResponseScale = Int32(comparisonKernelSpan * 10)
        horizontalSums.withUnsafeBufferPointer { rows in
            output.withUnsafeMutableBufferPointer { result in
                for row in halfHeight...(height - halfHeight) {
                    if let valid = analysis.validRows,
                       row - halfHeight < valid.lowerBound || row + halfHeight >= valid.upperBound {
                        continue
                    }
                    let a = rows.baseAddress!.advanced(by: (row - 4) * width)
                    let b = a.advanced(by: width)
                    let c = b.advanced(by: width)
                    let d = c.advanced(by: width)
                    let e = d.advanced(by: width)
                    let f = e.advanced(by: width)
                    let g = f.advanced(by: width)
                    let h = g.advanced(by: width)
                    for x in halfWidth...(width - halfWidth) {
                        // Same signed 8-row kernel, paired symmetrically.
                        let response = (e[x] - d[x]) * 4 + (f[x] - c[x]) * 3
                            + (g[x] - b[x]) * 2 + h[x] - a[x]
                        if response > 0 {
                            result[row * width + x] = UInt8(clamping: Int(
                                response / fixedResponseScale
                            ))
                        }
                    }
                }
            }
        }
        return output
    }

    static func detect(
        in image: CGImage,
        searchFrameYRange: ClosedRange<CGFloat>,
        excluding excludedRects: [CGRect],
        tuning: GroundTheoryTuning = .default
    ) -> GroundLineDetection? {
        guard let edges = analyze(image, excluding: excludedRects) else { return nil }
        return detect(
            in: compare(edges),
            searchFrameYRange: searchFrameYRange,
            tuning: tuning
        )
    }

    static func detect(
        in analysis: GroundComparisonAnalysis,
        searchFrameYRange: ClosedRange<CGFloat>,
        tuning: GroundTheoryTuning
    ) -> GroundLineDetection? {
        theory(from: analysis, tuning: tuning).flatMap {
            detect(in: $0, searchFrameYRange: searchFrameYRange)
        }
    }

    static func detect(in theory: GroundTheoryAnalysis,
                       searchFrameYRange: ClosedRange<CGFloat>) -> GroundLineDetection? {
        let analysis = theory.comparison
        let tuning = theory.tuning
        guard searchFrameYRange.lowerBound.isFinite,
              searchFrameYRange.upperBound.isFinite,
              analysis.width > 0, analysis.height > 0,
              analysis.groundPixels.count == analysis.width * analysis.height
        else { return nil }

        let stages = theory.stages
        let width = analysis.width
        let height = analysis.height
        let minimumLength = max(1, tuning.minimumSegmentLength)

        var segments = [GroundLineSegment]()
        for row in stages.selectedRows {
            for candidate in stages.acceptedRunsByRow[row] {
                guard candidate.range.count >= minimumLength else { continue }
                let values = candidate.range.map {
                    analysis.groundPixels[row * width + $0]
                }
                let confidence = CGFloat(values.reduce(0) { $0 + Int($1) })
                    / CGFloat(max(1, values.count * 255))
                segments.append(GroundLineSegment(
                    frameY: CGFloat(height) - CGFloat(row) - 0.5,
                    minimumFrameX: CGFloat(candidate.range.lowerBound),
                    maximumFrameX: CGFloat(candidate.range.upperBound + 1),
                    confidence: confidence
                ))
            }
        }
        guard !segments.isEmpty else { return nil }

        let clippedLower = max(0, min(CGFloat(height), searchFrameYRange.lowerBound))
        let clippedUpper = max(0, min(CGFloat(height), searchFrameYRange.upperBound))
        let anchor = (clippedLower + clippedUpper) * 0.5
        let inSearchRange = segments.filter {
            $0.frameY >= clippedLower && $0.frameY <= clippedUpper
        }
        let primaryPool = inSearchRange.isEmpty ? segments : inSearchRange
        guard let primary = primaryPool.min(by: {
            let leftDistance = abs($0.frameY - anchor)
            let rightDistance = abs($1.frameY - anchor)
            return leftDistance == rightDistance
                ? $0.confidence > $1.confidence
                : leftDistance < rightDistance
        }) else { return nil }

        var notchXs = [CGFloat]()
        for segment in segments {
            let length = max(1, Int(segment.maximumFrameX - segment.minimumFrameX))
            let stride = max(4, length / 6)
            var x = Int(segment.minimumFrameX)
            while x < Int(segment.maximumFrameX) {
                notchXs.append(CGFloat(x))
                x += stride
            }
            notchXs.append(segment.maximumFrameX - 1)
        }
        return GroundLineDetection(
            frameY: primary.frameY,
            segments: segments,
            notchFrameXs: notchXs,
            confidence: primary.confidence
        )
    }

    private static func theoryStages(
        in analysis: GroundComparisonAnalysis,
        tuning: GroundTheoryTuning,
        validatesSurface: Bool = false
    ) -> GroundTheoryStages? {
        guard analysis.width > 0, analysis.height > 0,
              analysis.groundPixels.count == analysis.width * analysis.height
        else { return nil }

        let threshold = UInt8(clamping: tuning.groundThreshold)
        let minimumLength = max(1, tuning.minimumSegmentLength)
        let maximumOcclusionGap = max(0, tuning.occlusionMergeGap)
        let surface = validatesSurface ? GroundSurfaceEvidence(analysis) : nil
        var accepted = Array(repeating: [GroundCandidateRun](), count: analysis.height)

        for row in 0..<analysis.height {
            let offset = row * analysis.width
            var start: Int?
            var rawRuns = [ClosedRange<Int>]()
            func finishRun(at end: Int) {
                guard let start, end >= start else { return }
                rawRuns.append(start...end)
            }
            for x in 0..<analysis.width {
                if analysis.groundPixels[offset + x] >= threshold {
                    if start == nil { start = x }
                } else if start != nil {
                    finishRun(at: x - 1)
                    start = nil
                }
            }
            if start != nil { finishRun(at: analysis.width - 1) }
            accepted[row] = mergeGroundRuns(
                rawRuns,
                maximumGap: maximumOcclusionGap
            ).filter {
                $0.range.count >= minimumLength
                    && $0.evidenceRanges.reduce(0, { $0 + $1.count }) >= minimumLength
                    && (!validatesSurface || $0.evidenceRanges.reduce(0, { total, range in
                        total + range.reduce(0) { count, x in
                            count + (Int(analysis.groundPixels[offset + x]) >= min(255, tuning.groundThreshold + 1) ? 1 : 0)
                        }
                    }) >= minimumLength)

            }
        }

        // Non-maximum suppression is line-local rather than row-global. Raw
        // overlap handles ordinary duplicate edges. A dominant, bridged floor
        // also owns its occluded footprint: a much shorter edge wholly inside
        // that footprint is usually the top of the foreground object hiding
        // the floor. A substantial platform inside the gap remains independent.
        struct ScoredRun {
            let row: Int
            let run: GroundCandidateRun
            let strength: UInt64
        }
        let candidates = accepted.indices.flatMap { row in
            accepted[row].map { run in
                ScoredRun(
                    row: row,
                    run: run,
                    strength: run.range.reduce(UInt64.zero) {
                        $0 + UInt64(analysis.groundPixels[row * analysis.width + $1])
                    }
                )
            }
        }
        let ordered = candidates.sorted {
            let leftEvidence = $0.run.evidenceRanges.reduce(0) { $0 + $1.count }
            let rightEvidence = $1.run.evidenceRanges.reduce(0) { $0 + $1.count }
            if leftEvidence != rightEvidence {
                return leftEvidence > rightEvidence
            }
            if $0.strength != $1.strength { return $0.strength > $1.strength }
            if $0.row != $1.row { return $0.row < $1.row }
            return $0.run.range.lowerBound < $1.run.range.lowerBound
        }
        let separation = max(0, tuning.lineSeparation)
        var surviving = [ScoredRun]()
        for candidate in ordered {
            let candidateEvidence = candidate.run.evidenceRanges.reduce(0) { $0 + $1.count }
            let suppressed = surviving.contains { stronger in
                guard abs(stronger.row - candidate.row) <= separation else { return false }
                let directEvidenceOverlap = stronger.run.evidenceRanges.contains { strong in
                    candidate.run.evidenceRanges.contains { weak in
                        min(strong.upperBound, weak.upperBound)
                            - max(strong.lowerBound, weak.lowerBound) + 1 >= min(16, weak.count)
                    }
                }
                if directEvidenceOverlap { return true }
                let strongerEvidence = stronger.run.evidenceRanges.reduce(0) { $0 + $1.count }
                let insideOccludedFootprint = candidate.run.range.lowerBound
                        >= stronger.run.range.lowerBound
                    && candidate.run.range.upperBound <= stronger.run.range.upperBound
                return insideOccludedFootprint
                    && strongerEvidence >= max(candidateEvidence * 2, minimumLength * 2)
            }
            if !suppressed {
                if surface?.supports(row: candidate.row, ranges: [candidate.run.range]) == false {
                    continue
                }
                surviving.append(candidate)
            }
        }
        accepted = Array(repeating: [], count: analysis.height)
        for candidate in surviving {
            // An observed lower surface inside an unsupported interval is a
            // step/gap, not an occluder over one continuous upper floor.
            // Preserve ordinary occlusion bridges when no such evidence exists.
            var group = [ClosedRange<Int>]()
            func appendGroup() {
                guard let first = group.first, let last = group.last else { return }
                if surface?.supports(row: candidate.row,
                       ranges: [first.lowerBound...last.upperBound]) == false { return }
                accepted[candidate.row].append(GroundCandidateRun(
                    range: first.lowerBound...last.upperBound, evidenceRanges: group))
            }
            for evidence in candidate.run.evidenceRanges {
                if let prior = group.last {
                    let gapStart = prior.upperBound + 1
                    let gapEnd = evidence.lowerBound - 1
                    let lowerSurface = gapEnd - gapStart + 1 >= minimumLength
                        && surviving.contains { lower in
                            lower.row > candidate.row + 4
                                && lower.run.evidenceRanges.reduce(0) { count, range in
                                    count + max(0, min(gapEnd, range.upperBound)
                                        - max(gapStart, range.lowerBound) + 1)
                                } >= minimumLength
                        }
                    if lowerSurface { appendGroup(); group.removeAll(keepingCapacity: true) }
                }
                group.append(evidence)
            }
            appendGroup()
        }
        for row in accepted.indices {
            accepted[row].sort { $0.range.lowerBound < $1.range.lowerBound }
        }
        let selectedRows = accepted.indices.filter { !accepted[$0].isEmpty }
        return GroundTheoryStages(
            acceptedRunsByRow: accepted,
            selectedRows: selectedRows
        )
    }

    private static func mergeGroundRuns(
        _ runs: [ClosedRange<Int>],
        maximumGap: Int
    ) -> [GroundCandidateRun] {
        guard var current = runs.first else { return [] }
        var currentEvidence = [current]
        var merged = [GroundCandidateRun]()
        for run in runs.dropFirst() {
            let gap = run.lowerBound - current.upperBound - 1
            if gap <= maximumGap {
                current = current.lowerBound...max(current.upperBound, run.upperBound)
                currentEvidence.append(run)
            } else {
                merged.append(GroundCandidateRun(
                    range: current,
                    evidenceRanges: currentEvidence
                ))
                current = run
                currentEvidence = [run]
            }
        }
        merged.append(GroundCandidateRun(
            range: current,
            evidenceRanges: currentEvidence
        ))
        return merged
    }

    private static func rangesOverlap(
        _ left: ClosedRange<Int>,
        _ right: ClosedRange<Int>
    ) -> Bool {
        left.lowerBound <= right.upperBound && right.lowerBound <= left.upperBound
    }

    private static func cleanFloorLines(
        from stages: GroundTheoryStages
    ) -> [CleanFloorLine] {
        stages.selectedRows.flatMap { row in
            stages.acceptedRunsByRow[row].map {
                CleanFloorLine(
                    row: row,
                    xRange: $0.range,
                    evidenceRanges: $0.evidenceRanges
                )
            }
        }
    }

    static func debugImage(
        in image: CGImage,
        excluding excludedRects: [CGRect] = []
    ) -> CGImage? {
        analyze(image, excluding: excludedRects).flatMap(edgeImage)
    }

    static func groundDetectDebugImage(
        in image: CGImage,
        excluding excludedRects: [CGRect] = []
    ) -> CGImage? {
        analyze(image, excluding: excludedRects).map(compare).flatMap(groundDetectImage)
    }

    private static func paint(
        _ pixels: inout [UInt8],
        index: Int,
        color: (UInt8, UInt8, UInt8, UInt8)
    ) {
        let pixel = index * 4
        pixels[pixel] = color.0
        pixels[pixel + 1] = color.1
        pixels[pixel + 2] = color.2
        pixels[pixel + 3] = color.3
    }


    private static func grayscaleImage(
        _ pixels: [UInt8],
        width: Int,
        height: Int
    ) -> CGImage? {
        guard let provider = CGDataProvider(data: Data(pixels) as CFData) else { return nil }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 8,
            bytesPerRow: width,
            space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

    private static func rgbaImage(
        _ pixels: [UInt8],
        width: Int,
        height: Int
    ) -> CGImage? {
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
}
