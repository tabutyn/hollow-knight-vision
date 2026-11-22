import CoreGraphics

/// Horizontal extents are half-open. Only geometry competing for the same
/// columns within the Knight's height can constrain another floor.
enum GroundClearance {
    struct Span {
        let row: CGFloat
        let extent: Range<CGFloat>
        let evidence: [Range<CGFloat>]

        var width: CGFloat { extent.upperBound - extent.lowerBound }

        func excludes(_ shorter: Range<CGFloat>, row otherRow: CGFloat, height: CGFloat,
                      rowTolerance: CGFloat = 0, endpointTolerance: CGFloat = 0) -> Bool {
            let dy = abs(row - otherRow), shorterWidth = shorter.upperBound - shorter.lowerBound
            return dy > rowTolerance && dy < height && width > shorterWidth
                && extent.lowerBound <= shorter.lowerBound + endpointTolerance
                && extent.upperBound >= shorter.upperBound - endpointTolerance
                && support(in: shorter) >= shorterWidth / 4
        }

        func support(in interval: Range<CGFloat>) -> CGFloat {
            evidence.reduce(0) { count, part in
                count + max(0, min(interval.upperBound, part.upperBound) - max(interval.lowerBound, part.lowerBound))
            }
        }
    }

    static func height(frameHeight: CGFloat, knight: CGRect?) -> CGFloat {
        // When no usable sprite box is available, the foreground
        // classifier uses the same 12% frame-height estimate for the Knight.
        let fallback = frameHeight * 0.12
        guard let knight, !knight.isNull, knight.height.isFinite,
              knight.height > 0, knight.height < frameHeight / 2 else { return fallback }
        return knight.height
    }

    static func extents(for spans: [Span], height: CGFloat, endpointTolerance: CGFloat = 0) -> [Range<CGFloat>?] {
        var result = spans.map { Optional($0.extent) }
        guard height > 0, spans.count > 1 else { return result }
        let byWidth = spans.indices.sorted {
            spans[$0].width == spans[$1].width ? $0 < $1 : spans[$0].width > spans[$1].width
        }
        var kept = [Int]()
        for index in byWidth {
            let candidate = spans[index]
            let enclosed = kept.contains { other in
                // A bridge inferred over a hole is weak evidence against a measured ledge.
                spans[other].excludes(candidate.extent, row: candidate.row, height: height, endpointTolerance: endpointTolerance)
            }
            if enclosed { result[index] = nil } else { kept.append(index) }
        }

        let byX = kept.sorted { spans[$0].extent.lowerBound < spans[$1].extent.lowerBound }
        for (position, i) in byX.enumerated() {
            let left = spans[i]
            for j in byX[(position + 1)...] {
                let right = spans[j]
                let begin = right.extent.lowerBound, end = left.extent.upperBound
                if begin >= end { break }
                let dy = abs(left.row - right.row)
                guard dy > 0, dy < height, left.extent.lowerBound < begin, end < right.extent.upperBound,
                      end - begin <= min(left.width, right.width) / 4 else { continue }
                // Pick the cut that removes the fewest measured columns. A midpoint tie
                // divides equally supported endpoints without introducing an empty column.
                let midpoint = ((begin + end) / 2).rounded(.down)
                var cut = min(end, max(begin, midpoint))
                func cost(_ x: CGFloat) -> CGFloat {
                    left.support(in: x..<end) + right.support(in: begin..<x)
                }
                var best = cost(cut)
                for part in left.evidence + right.evidence {
                    for x in [part.lowerBound, part.upperBound] where x >= begin && x <= end {
                        let lost = cost(x)
                        if lost < best || (lost == best && abs(x - midpoint) < abs(cut - midpoint)) {
                            cut = x
                            best = lost
                        }
                    }
                }
                if let a = result[i], let b = result[j] {
                    result[i] = a.lowerBound..<min(a.upperBound, cut)
                    result[j] = max(b.lowerBound, cut)..<b.upperBound
                }
            }
        }
        return result
    }

    static func span(_ line: CleanFloorLine) -> Span {
        func interval(_ range: ClosedRange<Int>) -> Range<CGFloat> {
            CGFloat(range.lowerBound)..<(CGFloat(range.upperBound) + 1)
        }
        return Span(row: CGFloat(line.row), extent: interval(line.xRange), evidence: line.evidenceRanges.map(interval))
    }

    static func floors(_ lines: [CleanFloorLine], imageHeight: Int, knight: CGRect?) -> [CleanFloorLine] {
        guard lines.count > 1 else { return lines }
        let spans = lines.map(span)
        let ranges = extents(for: spans, height: height(frameHeight: CGFloat(imageHeight), knight: knight))
        return zip(lines, ranges).compactMap { line, extent in
            guard let extent else { return nil }
            let range = Int(extent.lowerBound)...(Int(extent.upperBound) - 1)
            guard range != line.xRange else { return line }
            return CleanFloorLine(row: line.row, xRange: range, evidenceRanges: line.evidenceRanges.compactMap {
                $0.overlaps(range) ? $0.clamped(to: range) : nil
            })
        }
    }
}
