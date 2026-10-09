import CoreGraphics
import Foundation

struct LiveObjectDetectionTracker {
    private struct Track {
        let identifier: UInt64
        let classIdentifier: String
        var rect: CGRect
        var confidence: Double
        var hits: Int
        var misses: Int
        var sourceFrameIdentifier: UInt64
        var modelVersion: LabelingModelSemanticVersion
    }

    private var tracks = [Track]()
    private var generation: UInt64?
    private var nextIdentifier: UInt64 = 1

    mutating func reset() {
        tracks.removeAll(keepingCapacity: true)
        generation = nil
    }

    mutating func update(
        detections: [LiveObjectDetection],
        captureGeneration: UInt64
    ) -> [LiveObjectDetection] {
        if generation != captureGeneration {
            tracks.removeAll(keepingCapacity: true)
            generation = captureGeneration
        }

        var unmatchedTracks = Set(tracks.indices)
        var nextTracks = tracks
        let ordered = detections.sorted { $0.confidence > $1.confidence }

        for detection in ordered {
            let canonical = LabelingClassIdentity.canonicalIdentifier(
                detection.classIdentifier
            )
            let match = unmatchedTracks.compactMap { index -> (Int, CGFloat)? in
                let track = tracks[index]
                guard track.classIdentifier == canonical else { return nil }
                let iou = RectangleOverlap.intersectionOverUnion(track.rect, detection.normalizedRect)
                let distance = hypot(
                    track.rect.midX - detection.normalizedRect.midX,
                    track.rect.midY - detection.normalizedRect.midY
                )
                let scale = max(
                    0.045,
                    hypot(track.rect.width, track.rect.height) * 1.6
                )
                guard iou >= 0.08 || distance <= scale else { return nil }
                return (index, iou * 2 - distance)
            }.max { $0.1 < $1.1 }?.0

            if let match {
                unmatchedTracks.remove(match)
                let previous = tracks[match]
                let alpha: CGFloat = previous.hits < 2 ? 0.58 : 0.34
                nextTracks[match].rect = interpolate(
                    previous.rect, detection.normalizedRect, alpha: alpha
                )
                nextTracks[match].confidence = previous.confidence * 0.35
                    + detection.confidence * 0.65
                nextTracks[match].hits += 1
                nextTracks[match].misses = 0
                nextTracks[match].sourceFrameIdentifier = detection.sourceFrameIdentifier
                nextTracks[match].modelVersion = detection.modelVersion
            } else {
                nextTracks.append(Track(
                    identifier: nextIdentifier,
                    classIdentifier: canonical,
                    rect: detection.normalizedRect,
                    confidence: detection.confidence,
                    hits: 1,
                    misses: 0,
                    sourceFrameIdentifier: detection.sourceFrameIdentifier,
                    modelVersion: detection.modelVersion
                ))
                nextIdentifier &+= 1
            }
        }

        for index in unmatchedTracks {
            nextTracks[index].misses += 1
            nextTracks[index].confidence *= 0.82
        }
        tracks = nextTracks.filter { $0.misses <= 4 && $0.confidence >= 0.18 }

        return tracks.compactMap { track in
            // A very strong first observation is useful immediately; ordinary
            // candidates must agree with the next inference before publishing.
            guard track.hits >= 2 || track.confidence >= 0.88 else { return nil }
            return LiveObjectDetection(
                classIdentifier: track.classIdentifier,
                normalizedRect: track.rect,
                confidence: track.confidence,
                sourceFrameIdentifier: track.sourceFrameIdentifier,
                modelVersion: track.modelVersion
            )
        }
    }

    private func interpolate(_ first: CGRect, _ second: CGRect, alpha: CGFloat) -> CGRect {
        CGRect(
            x: first.minX + (second.minX - first.minX) * alpha,
            y: first.minY + (second.minY - first.minY) * alpha,
            width: first.width + (second.width - first.width) * alpha,
            height: first.height + (second.height - first.height) * alpha
        )
    }

}

struct LiveObjectPixelRefinement {
    let detections: [LiveObjectDetection]
    let diagnosticImage: CGImage?
}

enum LiveObjectPixelRefiner {
    static func refine(
        _ detections: [LiveObjectDetection],
        in image: CGImage,
        extensionFraction: CGFloat,
        createsDiagnosticImage: Bool = true
    ) -> LiveObjectPixelRefinement {
        guard !detections.isEmpty,
              image.width > 2,
              image.height > 2 else {
            return LiveObjectPixelRefinement(
                detections: detections,
                diagnosticImage: nil
            )
        }
        let width = image.width
        let height = image.height
        let bytesPerRow = width * 4
        var pixels = [UInt8](repeating: 0, count: height * bytesPerRow)
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: bytesPerRow,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            return LiveObjectPixelRefinement(detections: detections, diagnosticImage: nil)
        }
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))

        let unit = CGRect(x: 0, y: 0, width: 1, height: 1)
        var mask = createsDiagnosticImage
            ? [UInt8](repeating: 0, count: height * bytesPerRow)
            : []
        var refined = [LiveObjectDetection]()
        refined.reserveCapacity(detections.count)

        for detection in detections {
            let original = detection.normalizedRect.standardized.intersection(unit)
            let dx = original.width * max(0, extensionFraction)
            let dy = original.height * max(0, extensionFraction)
            let search = original.insetBy(dx: -dx, dy: -dy).intersection(unit)
            let extended = LiveObjectDetection(
                classIdentifier: detection.classIdentifier,
                normalizedRect: search,
                confidence: detection.confidence,
                sourceFrameIdentifier: detection.sourceFrameIdentifier,
                modelVersion: detection.modelVersion
            )
            let minX = max(1, Int(floor(search.minX * CGFloat(width))))
            let maxX = min(width - 2, Int(ceil(search.maxX * CGFloat(width))))
            let minY = max(1, Int(floor(search.minY * CGFloat(height))))
            let maxY = min(height - 2, Int(ceil(search.maxY * CGFloat(height))))
            guard minX <= maxX, minY <= maxY else {
                refined.append(extended)
                continue
            }

            var supportMinX = width
            var supportMaxX = 0
            var supportMinY = height
            var supportMaxY = 0
            var supportCount = 0
            for y in minY...maxY {
                for x in minX...maxX {
                    let center = (y * width + x) * 4
                    let left = center - 4
                    let right = center + 4
                    let above = center - bytesPerRow
                    let below = center + bytesPerRow
                    let gradient = channelDistance(pixels, center, left)
                        + channelDistance(pixels, center, right)
                        + channelDistance(pixels, center, above)
                        + channelDistance(pixels, center, below)
                    guard gradient >= 84 else { continue }
                    supportCount += 1
                    supportMinX = min(supportMinX, x)
                    supportMaxX = max(supportMaxX, x)
                    supportMinY = min(supportMinY, y)
                    supportMaxY = max(supportMaxY, y)
                    if createsDiagnosticImage {
                        mask[center] = 255
                        mask[center + 1] = 38
                        mask[center + 2] = 196
                        mask[center + 3] = 128
                    }
                }
            }

            guard supportCount >= max(10, Int(search.width * search.height
                * CGFloat(width * height) * 0.006)) else {
                refined.append(extended)
                continue
            }
            let support = CGRect(
                x: CGFloat(supportMinX) / CGFloat(width),
                y: CGFloat(supportMinY) / CGFloat(height),
                width: CGFloat(supportMaxX - supportMinX + 1) / CGFloat(width),
                height: CGFloat(supportMaxY - supportMinY + 1) / CGFloat(height)
            ).intersection(search)
            let intersection = support.intersection(original)
            guard !intersection.isNull, !intersection.isEmpty else {
                refined.append(extended)
                continue
            }
            refined.append(LiveObjectDetection(
                classIdentifier: detection.classIdentifier,
                normalizedRect: support,
                confidence: detection.confidence,
                sourceFrameIdentifier: detection.sourceFrameIdentifier,
                modelVersion: detection.modelVersion
            ))
        }

        let maskImage = createsDiagnosticImage
            ? mask.withUnsafeMutableBytes { pointer -> CGImage? in
            guard let maskContext = CGContext(
                data: pointer.baseAddress,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: bytesPerRow,
                space: colorSpace,
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else { return nil }
            return maskContext.makeImage()
        } : nil
        return LiveObjectPixelRefinement(
            detections: refined,
            diagnosticImage: maskImage
        )
    }

    private static func channelDistance(
        _ pixels: [UInt8],
        _ first: Int,
        _ second: Int
    ) -> Int {
        (0..<3).reduce(0) {
            $0 + abs(Int(pixels[first + $1]) - Int(pixels[second + $1]))
        } / 3
    }
}
