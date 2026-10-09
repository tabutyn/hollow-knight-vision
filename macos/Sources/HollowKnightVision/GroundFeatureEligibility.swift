import CoreGraphics

/// Converts segmented ground evidence into an exclusion mask. A feature center
/// is eligible only when its entire descriptor square fits inside the narrow
/// band immediately below the line and inside a supported horizontal segment.
enum GroundFeatureEligibility {
    static let descriptorHalfSize: CGFloat = 12
    static let trackingBandDepth: CGFloat = 32

    static func allowedRects(
        frameSize: CGSize,
        cameraPosition: CGPoint,
        solveWidth: CGFloat,
        ground: GroundReferenceEstimate
    ) -> [CGRect] {
        guard frameSize.width.isFinite, frameSize.height.isFinite,
              frameSize.width > 0, frameSize.height > 0,
              cameraPosition.x.isFinite, cameraPosition.y.isFinite,
              solveWidth.isFinite, solveWidth > 0,
              ground.worldY.isFinite,
              ground.segments.allSatisfy({ ($0.worldY ?? ground.worldY).isFinite })
        else { return [] }
        let scale = frameSize.width / solveWidth
        let bounds = CGRect(origin: .zero, size: frameSize)
        return ground.segments.compactMap { segment in
            let lineWorldY = segment.worldY ?? ground.worldY
            let lineFrameY = (lineWorldY - cameraPosition.y) * scale
            let minimumFeatureY = lineFrameY - trackingBandDepth + descriptorHalfSize
            let maximumFeatureY = lineFrameY - descriptorHalfSize
            guard maximumFeatureY > minimumFeatureY else { return nil }
            let minimumX = (segment.minimumWorldX - cameraPosition.x) * scale
                + descriptorHalfSize
            let maximumX = (segment.maximumWorldX - cameraPosition.x) * scale
                - descriptorHalfSize
            guard minimumX.isFinite, maximumX.isFinite, maximumX > minimumX else { return nil }
            let rect = CGRect(
                x: minimumX,
                y: minimumFeatureY,
                width: maximumX - minimumX,
                height: maximumFeatureY - minimumFeatureY
            ).intersection(bounds)
            return rect.isNull || rect.isEmpty ? nil : rect
        }.sorted {
            $0.minY == $1.minY ? $0.minX < $1.minX : $0.minY < $1.minY
        }
    }

    static func alignmentExclusions(
        frameSize: CGSize,
        cameraPosition: CGPoint,
        solveWidth: CGFloat,
        ground: GroundReferenceEstimate?,
        existing: [CGRect]
    ) -> [CGRect] {
        let bounds = CGRect(origin: .zero, size: frameSize)
        guard let ground else { return [bounds] }
        let allowed = allowedRects(
            frameSize: frameSize,
            cameraPosition: cameraPosition,
            solveWidth: solveWidth,
            ground: ground
        )
        guard !allowed.isEmpty else { return [bounds] }
        return (existing + complement(of: allowed, in: bounds))
            .filter { !$0.isNull && !$0.isEmpty }
    }

    /// Exact axis-aligned complement of all permitted bands. Horizontal strips
    /// let separate platform heights remain eligible without opening the space
    /// between them to feature extraction.
    private static func complement(of rects: [CGRect], in bounds: CGRect) -> [CGRect] {
        let clipped = rects.map { $0.intersection(bounds) }
            .filter { !$0.isNull && !$0.isEmpty }
        guard !clipped.isEmpty else { return [bounds] }
        let yCoordinates = Set(
            [bounds.minY, bounds.maxY] + clipped.flatMap { [$0.minY, $0.maxY] }
        ).sorted()
        var result = [CGRect]()
        for index in 0..<(yCoordinates.count - 1) {
            let minimumY = yCoordinates[index]
            let maximumY = yCoordinates[index + 1]
            guard maximumY > minimumY else { continue }
            let middleY = (minimumY + maximumY) * 0.5
            let intervals = clipped.filter { $0.minY <= middleY && $0.maxY >= middleY }
                .sorted { $0.minX < $1.minX }
            var merged = [ClosedRange<CGFloat>]()
            for rect in intervals {
                let interval = rect.minX...rect.maxX
                if let last = merged.last, interval.lowerBound <= last.upperBound {
                    merged[merged.count - 1] = last.lowerBound...max(
                        last.upperBound,
                        interval.upperBound
                    )
                } else {
                    merged.append(interval)
                }
            }
            var cursor = bounds.minX
            for interval in merged {
                if interval.lowerBound > cursor {
                    result.append(CGRect(
                        x: cursor,
                        y: minimumY,
                        width: interval.lowerBound - cursor,
                        height: maximumY - minimumY
                    ))
                }
                cursor = max(cursor, interval.upperBound)
            }
            if cursor < bounds.maxX {
                result.append(CGRect(
                    x: cursor,
                    y: minimumY,
                    width: bounds.maxX - cursor,
                    height: maximumY - minimumY
                ))
            }
        }
        return result
    }
}
