import CoreGraphics
import Foundation

/// Builds one sparse menu-text stencil from repeated human-labeled captures.
/// Captures are registered with integer translation before consensus so label
/// box jitter and small render offsets do not turn glyph edges into background.
struct MenuStencilConsensus {
    struct Capture {
        let pixels: ImageStencilPixels
        let annotationBounds: CGRect
    }

    struct Result {
        let bounds: CGRect
        let reference: ImageStencilReference
        let alignedOffsets: [CGPoint]
        let includedPixelCount: Int
    }

    static func build(
        captures: [Capture],
        nominalBounds: CGRect,
        alignmentRadius: Int = 6,
        preservesAnnotationBounds: Bool = false
    ) -> Result? {
        guard !captures.isEmpty,
              nominalBounds.width > 0,
              nominalBounds.height > 0 else { return nil }
        let width = captures[0].pixels.width
        let height = captures[0].pixels.height
        guard captures.allSatisfy({
            $0.pixels.width == width
                && $0.pixels.height == height
                && $0.pixels.originY == 0
        }) else { return nil }

        let referenceIndex = captures.indices.min { left, right in
            annotationDistance(captures[left].annotationBounds, nominalBounds)
                < annotationDistance(captures[right].annotationBounds, nominalBounds)
        } ?? 0
        let alignmentBounds = clampedIntegralBounds(
            nominalBounds.insetBy(dx: -2, dy: -2),
            width: width,
            height: height,
            insetForSearch: alignmentRadius
        )
        guard let alignmentBounds else { return nil }

        let reference = captures[referenceIndex].pixels
        var offsets = captures.map { capture in
            bestOffset(
                reference: reference,
                candidate: capture.pixels,
                bounds: alignmentBounds,
                radius: alignmentRadius
            )
        }
        offsets[referenceIndex] = .zero
        let medianX = offsets.map { Int($0.x) }.median ?? 0
        let medianY = offsets.map { Int($0.y) }.median ?? 0
        let centeredOffsets = offsets.map {
            CGPoint(x: Int($0.x) - medianX, y: Int($0.y) - medianY)
        }
        let samplingMargin = centeredOffsets.reduce(0) { margin, offset in
            max(margin, max(abs(Int(offset.x)), abs(Int(offset.y))))
        }

        let annotationEnvelope = captures.map(\.annotationBounds).reduce(nominalBounds) {
            $0.union($1)
        }
        // Text/menu-object labels retain their complete human box. Selector
        // decorations use verified-pixel bounds because their calibrated
        // rectangles and animated kernels were measured with that geometry.
        let requestedBounds = preservesAnnotationBounds
            ? nominalBounds
            : annotationEnvelope.insetBy(dx: -2, dy: -2)
        guard let consensusBounds = clampedIntegralBounds(
            requestedBounds.offsetBy(
                dx: CGFloat(medianX),
                dy: CGFloat(medianY)
            ),
            width: width,
            height: height,
            insetForSearch: samplingMargin
        ) else { return nil }
        let x0 = Int(consensusBounds.minX)
        let y0 = Int(consensusBounds.minY)
        let workWidth = Int(consensusBounds.width)
        let workHeight = Int(consensusBounds.height)
        let minimumSupport = captures.count == 1
            ? 1
            : max(2, Int(ceil(Double(captures.count) * 0.70)))
        var included = [Bool](repeating: false, count: workWidth * workHeight)
        var colors = [(UInt8, UInt8, UInt8)](
            repeating: (0, 0, 0),
            count: workWidth * workHeight
        )

        for localY in 0..<workHeight {
            for localX in 0..<workWidth {
                let screenX = x0 + localX
                let screenY = y0 + localY
                var samples = [(UInt8, UInt8, UInt8)]()
                for (index, capture) in captures.enumerated() {
                    let offset = centeredOffsets[index]
                    if let rgb = capture.pixels.rgb(
                        atX: screenX + Int(offset.x),
                        y: screenY + Int(offset.y)
                    ) {
                        samples.append((rgb.red, rgb.green, rgb.blue))
                    }
                }
                let textSamples = samples.filter(isLikelyMenuText)
                guard textSamples.count >= minimumSupport else { continue }
                let red = textSamples.map { Int($0.0) }.median ?? 0
                let green = textSamples.map { Int($0.1) }.median ?? 0
                let blue = textSamples.map { Int($0.2) }.median ?? 0
                let medianColor = (UInt8(red), UInt8(green), UInt8(blue))
                guard isLikelyMenuText(medianColor) else { continue }
                let lumas = textSamples.map(luma)
                let medianLuma = lumas.median ?? 0
                let deviation = lumas.map { abs($0 - medianLuma) }.median ?? 0
                guard deviation <= max(42, medianLuma * 0.35) else { continue }
                let index = localY * workWidth + localX
                included[index] = true
                colors[index] = medianColor
            }
        }

        // Animated dust occasionally occupies one pixel in several captures.
        // Glyph pixels have neighbours, so discard isolated consensus points.
        let unfilteredIncluded = included
        included = unfilteredIncluded.enumerated().map { index, value in
            guard value else { return false }
            let x = index % workWidth
            let y = index / workWidth
            for dy in -1...1 {
                for dx in -1...1 where dx != 0 || dy != 0 {
                    let nx = x + dx
                    let ny = y + dy
                    if nx >= 0, nx < workWidth, ny >= 0, ny < workHeight,
                       unfilteredIncluded[ny * workWidth + nx] {
                        return true
                    }
                }
            }
            return false
        }

        let includedIndices = included.indices.filter { included[$0] }
        guard includedIndices.count >= 8 else { return nil }
        let minLocalX = preservesAnnotationBounds
            ? 0 : includedIndices.map { $0 % workWidth }.min()!
        let maxLocalX = preservesAnnotationBounds
            ? workWidth - 1 : includedIndices.map { $0 % workWidth }.max()!
        let minLocalY = preservesAnnotationBounds
            ? 0 : includedIndices.map { $0 / workWidth }.min()!
        let maxLocalY = preservesAnnotationBounds
            ? workHeight - 1 : includedIndices.map { $0 / workWidth }.max()!
        let outputWidth = maxLocalX - minLocalX + 1
        let outputHeight = maxLocalY - minLocalY + 1
        var rgb = Data(count: outputWidth * outputHeight * 3)
        var mask = Data(count: outputWidth * outputHeight)
        rgb.withUnsafeMutableBytes { rgbRaw in
            mask.withUnsafeMutableBytes { maskRaw in
                guard let rgbBytes = rgbRaw.bindMemory(to: UInt8.self).baseAddress,
                      let maskBytes = maskRaw.bindMemory(to: UInt8.self).baseAddress
                else { return }
                for outputY in 0..<outputHeight {
                    for outputX in 0..<outputWidth {
                        let sourceX = minLocalX + outputX
                        let sourceY = minLocalY + outputY
                        let sourceIndex = sourceY * workWidth + sourceX
                        guard included[sourceIndex] else { continue }
                        let outputIndex = outputY * outputWidth + outputX
                        let color = colors[sourceIndex]
                        rgbBytes[outputIndex * 3] = color.0
                        rgbBytes[outputIndex * 3 + 1] = color.1
                        rgbBytes[outputIndex * 3 + 2] = color.2
                        maskBytes[outputIndex] = 255
                    }
                }
            }
        }
        let outputBounds = CGRect(
            x: x0 + minLocalX,
            y: y0 + minLocalY,
            width: outputWidth,
            height: outputHeight
        )
        return Result(
            bounds: outputBounds,
            reference: ImageStencilReference(
                width: outputWidth,
                height: outputHeight,
                rgb: rgb,
                mask: mask
            ),
            alignedOffsets: centeredOffsets,
            includedPixelCount: includedIndices.count
        )
    }

    /// Learns the full localized word width around a human English box. The
    /// ordinary builder deliberately preserves that box; translated glyphs
    /// can extend beyond it. Here we inspect a wider strip, split stable text
    /// at large horizontal gaps, and retain the cluster nearest the human box
    /// so a value column on the same row is not merged into the label.
    static func buildLocalizedText(
        captures: [Capture],
        nominalBounds: CGRect,
        horizontalPadding: CGFloat = 80
    ) -> Result? {
        let expanded = nominalBounds.insetBy(
            dx: -horizontalPadding,
            dy: -3
        )
        let expandedCaptures = captures.map {
            Capture(pixels: $0.pixels, annotationBounds: expanded)
        }
        guard let raw = build(
            captures: expandedCaptures,
            nominalBounds: expanded,
            // Menus are fixed in screen space. Matching already tolerates
            // capture jitter; repeating an 81-position registration for every
            // object and language made catalog construction unbounded.
            alignmentRadius: 0,
            preservesAnnotationBounds: true
        ), let mask = raw.reference.mask else { return nil }
        let bytes = [UInt8](mask)
        let width = raw.reference.width
        let height = raw.reference.height
        var activeColumns = [Bool](repeating: false, count: width)
        for y in 0..<height {
            for x in 0..<width where bytes[y * width + x] != 0 {
                activeColumns[x] = true
            }
        }
        let active = activeColumns.indices.filter { activeColumns[$0] }
        guard let first = active.first else { return nil }
        var clusters = [ClosedRange<Int>]()
        var start = first
        var previous = first
        for x in active.dropFirst() {
            if x - previous > 10 {
                clusters.append(start...previous)
                start = x
            }
            previous = x
        }
        clusters.append(start...previous)
        let nominalLocal = nominalBounds.offsetBy(
            dx: -raw.bounds.minX,
            dy: -raw.bounds.minY
        )
        guard let selected = clusters.min(by: { left, right in
            clusterDistance(left, to: nominalLocal)
                < clusterDistance(right, to: nominalLocal)
        }) else { return nil }
        let x0 = max(0, selected.lowerBound - 1)
        let x1 = min(width - 1, selected.upperBound + 1)
        var minY = height
        var maxY = -1
        for y in 0..<height {
            for x in x0...x1 where bytes[y * width + x] != 0 {
                minY = min(minY, y)
                maxY = max(maxY, y)
            }
        }
        guard maxY >= minY else { return nil }
        let y0 = max(0, minY - 1)
        let y1 = min(height - 1, maxY + 1)
        let outputWidth = x1 - x0 + 1
        let outputHeight = y1 - y0 + 1
        let sourceRGB = [UInt8](raw.reference.rgb)
        var rgb = Data(count: outputWidth * outputHeight * 3)
        var croppedMask = Data(count: outputWidth * outputHeight)
        rgb.withUnsafeMutableBytes { rgbRaw in
            croppedMask.withUnsafeMutableBytes { maskRaw in
                guard let rgbOut = rgbRaw.bindMemory(to: UInt8.self).baseAddress,
                      let maskOut = maskRaw.bindMemory(to: UInt8.self).baseAddress
                else { return }
                for y in 0..<outputHeight {
                    for x in 0..<outputWidth {
                        let sourcePixel = (y0 + y) * width + x0 + x
                        let outputPixel = y * outputWidth + x
                        rgbOut[outputPixel * 3] = sourceRGB[sourcePixel * 3]
                        rgbOut[outputPixel * 3 + 1] = sourceRGB[sourcePixel * 3 + 1]
                        rgbOut[outputPixel * 3 + 2] = sourceRGB[sourcePixel * 3 + 2]
                        maskOut[outputPixel] = bytes[sourcePixel]
                    }
                }
            }
        }
        let included = [UInt8](croppedMask).count { $0 != 0 }
        guard included >= 8 else { return nil }
        return Result(
            bounds: CGRect(
                x: raw.bounds.minX + CGFloat(x0),
                y: raw.bounds.minY + CGFloat(y0),
                width: CGFloat(outputWidth),
                height: CGFloat(outputHeight)
            ),
            reference: ImageStencilReference(
                width: outputWidth,
                height: outputHeight,
                rgb: rgb,
                mask: croppedMask
            ),
            alignedOffsets: raw.alignedOffsets,
            includedPixelCount: included
        )
    }

    private static func clusterDistance(
        _ cluster: ClosedRange<Int>,
        to nominal: CGRect
    ) -> CGFloat {
        let clusterMin = CGFloat(cluster.lowerBound)
        let clusterMax = CGFloat(cluster.upperBound + 1)
        let overlap = max(0, min(clusterMax, nominal.maxX) - max(clusterMin, nominal.minX))
        if overlap > 0 { return -overlap }
        if clusterMax < nominal.minX { return nominal.minX - clusterMax }
        return clusterMin - nominal.maxX
    }

    /// Removes bright pixels that remain present while another row is
    /// selected. Menu text is stable in those negative frames; the flanking
    /// cursor decoration is not. The returned mask therefore represents the
    /// selector itself rather than nearby translated glyphs or background.
    static func removingStableBackground(
        from reference: ImageStencilReference,
        at bounds: CGRect,
        negativeFrames: [ImageStencilPixels]
    ) -> ImageStencilReference? {
        guard reference.isValid,
              negativeFrames.count >= 2,
              Int(bounds.width) == reference.width,
              Int(bounds.height) == reference.height else { return nil }
        let originalMask = reference.mask.map { [UInt8]($0) }
            ?? [UInt8](repeating: 255, count: reference.width * reference.height)
        let requiredNegativeSupport = max(
            2,
            Int(ceil(Double(negativeFrames.count) * 0.50))
        )
        let originX = Int(bounds.minX)
        let originY = Int(bounds.minY)
        var included = originalMask.map { $0 != 0 }
        for y in 0..<reference.height {
            for x in 0..<reference.width {
                let index = y * reference.width + x
                guard included[index] else { continue }
                let stableForegroundCount = negativeFrames.reduce(into: 0) {
                    count, frame in
                    guard let color = frame.rgb(
                        atX: originX + x,
                        y: originY + y
                    ) else { return }
                    if isLikelyMenuText((color.red, color.green, color.blue)) {
                        count += 1
                    }
                }
                if stableForegroundCount >= requiredNegativeSupport {
                    included[index] = false
                }
            }
        }

        // A cursor is a connected ornament. Discard isolated remnants left by
        // particles or antialiased text edges after negative subtraction.
        let unfiltered = included
        included = unfiltered.enumerated().map { index, value in
            guard value else { return false }
            let x = index % reference.width
            let y = index / reference.width
            for dy in -1...1 {
                for dx in -1...1 where dx != 0 || dy != 0 {
                    let nx = x + dx
                    let ny = y + dy
                    if nx >= 0, nx < reference.width,
                       ny >= 0, ny < reference.height,
                       unfiltered[ny * reference.width + nx] {
                        return true
                    }
                }
            }
            return false
        }
        guard included.filter({ $0 }).count >= 8 else { return nil }
        return ImageStencilReference(
            width: reference.width,
            height: reference.height,
            rgb: reference.rgb,
            mask: Data(included.map { $0 ? UInt8(255) : UInt8(0) })
        )
    }

    private static func bestOffset(
        reference: ImageStencilPixels,
        candidate: ImageStencilPixels,
        bounds: CGRect,
        radius: Int
    ) -> CGPoint {
        var keypoints = Swift.stride(
            from: Int(bounds.minY),
            to: Int(bounds.maxY),
            by: 1
        ).flatMap { y in
            Swift.stride(
                from: Int(bounds.minX),
                to: Int(bounds.maxX),
                by: 1
            ).compactMap { x -> (x: Int, y: Int, weight: Double)? in
                guard let color = reference.rgb(atX: x, y: y) else { return nil }
                let weight = textWeight((color.red, color.green, color.blue))
                return weight > 0 ? (x, y, weight) : nil
            }
        }
        if keypoints.count > 1_200 {
            keypoints = Array(keypoints.sorted { $0.weight > $1.weight }.prefix(1_200))
        }
        guard !keypoints.isEmpty else { return .zero }
        let referenceWeight = keypoints.reduce(0) { $0 + $1.weight }
        var best = (score: -Double.infinity, distance: Int.max, x: 0, y: 0)
        func consider(_ dx: Int, _ dy: Int) {
            var overlap = 0.0
            for point in keypoints {
                guard let color = candidate.rgb(atX: point.x + dx, y: point.y + dy)
                else { continue }
                overlap += min(
                    point.weight,
                    textWeight((color.red, color.green, color.blue))
                )
            }
            let score = overlap / max(1, referenceWeight)
            let distance = abs(dx) + abs(dy)
            if score > best.score || (score == best.score && distance < best.distance) {
                best = (score, distance, dx, dy)
            }
        }
        for dy in Swift.stride(from: -radius, through: radius, by: 2) {
            for dx in Swift.stride(from: -radius, through: radius, by: 2) {
                consider(dx, dy)
            }
        }
        let coarse = best
        for dy in max(-radius, coarse.y - 1)...min(radius, coarse.y + 1) {
            for dx in max(-radius, coarse.x - 1)...min(radius, coarse.x + 1) {
                consider(dx, dy)
            }
        }
        return CGPoint(x: best.x, y: best.y)
    }

    private static func textWeight(_ color: (UInt8, UInt8, UInt8)) -> Double {
        let channels = [Double(color.0), Double(color.1), Double(color.2)]
        let minimum = channels.min() ?? 0
        let chroma = (channels.max() ?? 0) - minimum
        return max(0, minimum - max(0, chroma - 18) * 1.5 - 52)
    }

    private static func isLikelyMenuText(_ color: (UInt8, UInt8, UInt8)) -> Bool {
        let channels = [Int(color.0), Int(color.1), Int(color.2)]
        let minimum = channels.min() ?? 0
        let chroma = (channels.max() ?? 0) - minimum
        return minimum >= 66 && chroma <= 58 && textWeight(color) >= 12
    }

    private static func luma(_ color: (UInt8, UInt8, UInt8)) -> Double {
        (Double(color.0) + 2 * Double(color.1) + Double(color.2)) / 4
    }

    private static func annotationDistance(_ rect: CGRect, _ nominal: CGRect) -> CGFloat {
        abs(rect.midX - nominal.midX) + abs(rect.midY - nominal.midY)
            + abs(rect.width - nominal.width) + abs(rect.height - nominal.height)
    }

    private static func clampedIntegralBounds(
        _ rect: CGRect,
        width: Int,
        height: Int,
        insetForSearch: Int
    ) -> CGRect? {
        let margin = max(0, insetForSearch)
        let x0 = max(margin, Int(floor(rect.minX)))
        let y0 = max(margin, Int(floor(rect.minY)))
        let x1 = min(width - margin, Int(ceil(rect.maxX)))
        let y1 = min(height - margin, Int(ceil(rect.maxY)))
        guard x1 > x0, y1 > y0 else { return nil }
        return CGRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)
    }
}

private extension Array where Element == Int {
    var median: Int? {
        guard !isEmpty else { return nil }
        let sorted = sorted()
        let middle = sorted.count / 2
        return sorted.count.isMultiple(of: 2)
            ? Int((Double(sorted[middle - 1]) + Double(sorted[middle])) / 2.0)
            : sorted[middle]
    }
}

private extension Array where Element == Double {
    var median: Double? {
        guard !isEmpty else { return nil }
        let sorted = sorted()
        let middle = sorted.count / 2
        return sorted.count.isMultiple(of: 2)
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle]
    }
}
