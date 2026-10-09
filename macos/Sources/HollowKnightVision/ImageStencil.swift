import CoreGraphics
import Foundation

/// Exact RGB pixels used by a stencil. Keeping this beside the kernel lets the
/// debug renderer place the actual photographic reference over the live frame.
struct ImageStencilReference: Equatable {
    let width: Int
    let height: Int
    let rgb: Data
    let mask: Data?

    init(width: Int, height: Int, rgb: Data, mask: Data? = nil) {
        self.width = width
        self.height = height
        self.rgb = rgb
        self.mask = mask
    }

    var isValid: Bool {
        width > 0 && height > 0
            && rgb.count == width * height * 3
            && (mask == nil || mask?.count == width * height)
    }

    func makeImage() -> CGImage? {
        guard isValid else { return nil }
        let source = [UInt8](rgb)
        let alpha = mask.map { [UInt8]($0) }
        var rgba = [UInt8](repeating: 255, count: width * height * 4)
        for pixel in 0..<(width * height) {
            rgba[pixel * 4] = source[pixel * 3]
            rgba[pixel * 4 + 1] = source[pixel * 3 + 1]
            rgba[pixel * 4 + 2] = source[pixel * 3 + 2]
            rgba[pixel * 4 + 3] = alpha?[pixel] ?? 255
        }
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else {
            return nil
        }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
            ),
            provider: provider,
            decode: nil,
            shouldInterpolate: true,
            intent: .defaultIntent
        )
    }

    /// Bounds of the pixels that actually participate in matching, projected
    /// into the destination rectangle. Debug outlines use this instead of the
    /// larger crop so translated words are shown at their verified width.
    func verifiedBounds(in destination: CGRect, padding: CGFloat = 1) -> CGRect {
        guard isValid, let mask else { return destination }
        let bytes = [UInt8](mask)
        var minX = width
        var minY = height
        var maxX = -1
        var maxY = -1
        for y in 0..<height {
            for x in 0..<width where bytes[y * width + x] != 0 {
                minX = min(minX, x)
                minY = min(minY, y)
                maxX = max(maxX, x)
                maxY = max(maxY, y)
            }
        }
        guard maxX >= minX, maxY >= minY else { return destination }
        let scaleX = destination.width / CGFloat(width)
        let scaleY = destination.height / CGFloat(height)
        let projected = CGRect(
            x: destination.minX + CGFloat(minX) * scaleX,
            y: destination.minY + CGFloat(minY) * scaleY,
            width: CGFloat(maxX - minX + 1) * scaleX,
            height: CGFloat(maxY - minY + 1) * scaleY
        )
        return projected.insetBy(dx: -padding, dy: -padding)
            .intersection(destination)
    }
}

/// A reusable RGB image stencil. Callers choose which reference pixels are
/// meaningful; comparison itself is normalized correlation plus color error.
struct ImageStencilKernel {
    struct Sample {
        let x: Int
        let y: Int
        let red: Double
        let green: Double
        let blue: Double
        let luma: Double
    }

    let kind: String
    let bounds: CGRect
    let width: Int
    let height: Int
    let samples: [Sample]
    let sum: Double
    let energy: Double

    init(
        kind: String,
        bounds: CGRect,
        rgb: Data,
        sampleStride: Int = 1,
        includes: (Int, Int, Int, Int) -> Bool = { _, _, _, _ in true }
    ) {
        self.kind = kind
        self.bounds = bounds
        width = Int(bounds.width)
        height = Int(bounds.height)
        let bytes = [UInt8](rgb)
        let stride = max(1, sampleStride)
        var samples = [Sample]()
        if bytes.count == width * height * 3 {
            for y in Swift.stride(from: 0, to: height, by: stride) {
                for x in Swift.stride(from: 0, to: width, by: stride) {
                    guard includes(x, y, width, height) else { continue }
                    let index = (y * width + x) * 3
                    let red = Double(bytes[index])
                    let green = Double(bytes[index + 1])
                    let blue = Double(bytes[index + 2])
                    samples.append(Sample(
                        x: x,
                        y: y,
                        red: red,
                        green: green,
                        blue: blue,
                        luma: (red + 2 * green + blue) / 4
                    ))
                }
            }
        }
        self.samples = samples
        sum = samples.reduce(0) { $0 + $1.luma }
        energy = samples.reduce(0) { $0 + $1.luma * $1.luma }
            - sum * sum / Double(max(1, samples.count))
    }
}

struct ImageStencilComparison {
    let normalizedCorrelation: Double
    let meanAbsoluteColorError: Double
    let meanRed: Double
    let meanBlue: Double
    let supportsCorrelation: Bool

    func confidence(correlationWeight: Double, colorErrorScale: Double) -> Double {
        let colorSimilarity = max(0, 1 - meanAbsoluteColorError / colorErrorScale)
        guard supportsCorrelation else { return colorSimilarity }
        return max(0, min(1,
            correlationWeight * normalizedCorrelation
                + (1 - correlationWeight) * colorSimilarity
        ))
    }
}

/// Compact screen-space occupancy learned from frames where a menu row is not
/// selected. It lets selector matching distinguish the newly visible cursor
/// from text that is always bright at the same location.
struct ImageStencilForegroundMask {
    let bounds: CGRect
    let width: Int
    let height: Int
    let mask: Data

    init?(frames: [ImageStencilPixels], bounds requestedBounds: CGRect) {
        guard frames.count >= 2, let first = frames.first else { return nil }
        let x0 = max(0, Int(floor(requestedBounds.minX)))
        let y0 = max(0, Int(floor(requestedBounds.minY)))
        let x1 = min(first.width, Int(ceil(requestedBounds.maxX)))
        let y1 = min(first.originY + first.height, Int(ceil(requestedBounds.maxY)))
        guard x1 > x0, y1 > y0 else { return nil }
        width = x1 - x0
        height = y1 - y0
        bounds = CGRect(x: x0, y: y0, width: width, height: height)
        let requiredSupport = max(2, Int(ceil(Double(frames.count) * 0.50)))
        var bytes = [UInt8](repeating: 0, count: width * height)
        for y in 0..<height {
            for x in 0..<width {
                let support = frames.reduce(into: 0) { count, frame in
                    if frame.isLikelyMenuText(atX: x0 + x, y: y0 + y) {
                        count += 1
                    }
                }
                if support >= requiredSupport { bytes[y * width + x] = 255 }
            }
        }
        mask = Data(bytes)
    }

    func contains(x: Int, y: Int) -> Bool {
        let localX = x - Int(bounds.minX)
        let localY = y - Int(bounds.minY)
        guard localX >= 0, localX < width,
              localY >= 0, localY < height else { return false }
        return mask[localY * width + localX] != 0
    }
}

/// Converts only the vertical band used by a stencil into reference-sized RGBA.
/// Coordinates accepted by compare are top-origin reference pixels.
struct ImageStencilPixels {
    let bytes: [UInt8]
    let width: Int
    let height: Int
    let originY: Int

    init?(
        _ image: CGImage,
        referenceWidth: Int,
        referenceHeight: Int,
        band requestedBand: Range<Int>
    ) {
        guard referenceWidth > 0, referenceHeight > 0 else { return nil }
        let lower = max(0, min(referenceHeight - 1, requestedBand.lowerBound))
        let upper = max(lower + 1, min(referenceHeight, requestedBand.upperBound))
        let sourceMinY = Int(floor(
            Double(image.height) * Double(lower) / Double(referenceHeight)
        ))
        let sourceMaxY = Int(ceil(
            Double(image.height) * Double(upper) / Double(referenceHeight)
        ))
        guard let crop = image.cropping(to: CGRect(
            x: 0,
            y: sourceMinY,
            width: image.width,
            height: max(1, sourceMaxY - sourceMinY)
        )) else { return nil }

        width = referenceWidth
        height = upper - lower
        originY = lower
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        guard let context = CGContext(
            data: &bytes,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.interpolationQuality = .high
        context.draw(crop, in: CGRect(x: 0, y: 0, width: width, height: height))
        self.bytes = bytes
    }

    func compare(_ kernel: ImageStencilKernel, at rect: CGRect) -> ImageStencilComparison? {
        guard !kernel.samples.isEmpty,
              rect.minX >= 0,
              rect.minY >= CGFloat(originY),
              rect.maxX <= CGFloat(width),
              rect.maxY <= CGFloat(originY + height) else { return nil }
        let count = Double(kernel.samples.count)
        let originX = Int(rect.minX)
        let localOriginY = Int(rect.minY) - originY
        let scaleX = Double(rect.width) / Double(kernel.width)
        let scaleY = Double(rect.height) / Double(kernel.height)
        let usesNativeSize = abs(rect.width - CGFloat(kernel.width)) < 0.001
            && abs(rect.height - CGFloat(kernel.height)) < 0.001
        var sum = 0.0
        var squaredSum = 0.0
        var crossProduct = 0.0
        var colorError = 0.0
        var redSum = 0.0
        var blueSum = 0.0
        for sample in kernel.samples {
            let pixelX = originX + (usesNativeSize
                ? sample.x
                : Int((Double(sample.x) + 0.5) * scaleX))
            let pixelY = localOriginY + (usesNativeSize
                ? sample.y
                : Int((Double(sample.y) + 0.5) * scaleY))
            let index = (pixelY * width + pixelX) * 4
            let red = Double(bytes[index])
            let green = Double(bytes[index + 1])
            let blue = Double(bytes[index + 2])
            let luma = (red + 2 * green + blue) / 4
            redSum += red
            blueSum += blue
            sum += luma
            squaredSum += luma * luma
            crossProduct += sample.luma * luma
            colorError += abs(sample.red - red)
                + abs(sample.green - green)
                + abs(sample.blue - blue)
        }
        let variance = squaredSum - sum * sum / count
        let supportsCorrelation = kernel.energy > count * 3 && variance > count * 3
        return ImageStencilComparison(
            normalizedCorrelation: supportsCorrelation
                ? (crossProduct - kernel.sum * sum / count)
                    / sqrt(kernel.energy * variance)
                : 0,
            meanAbsoluteColorError: colorError / (count * 3),
            meanRed: redSum / count,
            meanBlue: blueSum / count,
            supportsCorrelation: supportsCorrelation
        )
    }


    /// Copies a top-origin reference rectangle from the converted image band.
    /// Rectangles are rounded out so every human annotation pixel is retained.
    func reference(in rect: CGRect) -> ImageStencilReference? {
        let x0 = Int(rect.minX.rounded(.down))
        let y0 = Int(rect.minY.rounded(.down))
        let x1 = Int(rect.maxX.rounded(.up))
        let y1 = Int(rect.maxY.rounded(.up))
        guard x0 >= 0, y0 >= originY, x1 <= width,
              y1 <= originY + height, x1 > x0, y1 > y0 else { return nil }
        let outputWidth = x1 - x0
        let outputHeight = y1 - y0
        var rgb = Data(count: outputWidth * outputHeight * 3)
        rgb.withUnsafeMutableBytes { output in
            guard let outputBytes = output.bindMemory(to: UInt8.self).baseAddress else { return }
            for y in 0..<outputHeight {
                for x in 0..<outputWidth {
                    let source = (((y0 - originY + y) * width) + x0 + x) * 4
                    let destination = (y * outputWidth + x) * 3
                    outputBytes[destination] = bytes[source]
                    outputBytes[destination + 1] = bytes[source + 1]
                    outputBytes[destination + 2] = bytes[source + 2]
                }
            }
        }
        return ImageStencilReference(
            width: outputWidth,
            height: outputHeight,
            rgb: rgb
        )
    }

    func rgb(atX x: Int, y: Int) -> (red: UInt8, green: UInt8, blue: UInt8)? {
        let localY = y - originY
        guard x >= 0, x < width, localY >= 0, localY < height else { return nil }
        let index = (localY * width + x) * 4
        return (bytes[index], bytes[index + 1], bytes[index + 2])
    }

    func isLikelyMenuText(atX x: Int, y: Int) -> Bool {
        let localY = y - originY
        guard x >= 0, x < width, localY >= 0, localY < height else {
            return false
        }
        let index = (localY * width + x) * 4
        let channels = [
            Int(bytes[index]),
            Int(bytes[index + 1]),
            Int(bytes[index + 2]),
        ]
        let minimum = channels.min() ?? 0
        let chroma = (channels.max() ?? 0) - minimum
        let weight = minimum - max(0, chroma - 18) * 3 / 2 - 52
        return minimum >= 66 && chroma <= 58 && weight >= 12
    }

    func likelyMenuTextPixelCount(
        in rect: CGRect,
        excludingStableForeground baseline: ImageStencilForegroundMask? = nil
    ) -> Int {
        let x0 = max(0, Int(rect.minX.rounded(.down)))
        let y0 = max(originY, Int(rect.minY.rounded(.down)))
        let x1 = min(width, Int(rect.maxX.rounded(.up)))
        let y1 = min(originY + height, Int(rect.maxY.rounded(.up)))
        guard x1 > x0, y1 > y0 else { return 0 }
        var count = 0
        for y in y0..<y1 {
            for x in x0..<x1 {
                if isLikelyMenuText(atX: x, y: y),
                   baseline?.contains(x: x, y: y) != true {
                    count += 1
                }
            }
        }
        return count
    }

    /// Counts pixels bright enough to be the white menu cursor. The broader
    /// menu-text predicate intentionally admits the gray animated background
    /// for faded-glyph recovery; that makes it unsuitable for deciding which
    /// selector row is visibly occupied right now.
    func brightMenuPixelCount(
        in rect: CGRect,
        excludingStableForeground baseline: ImageStencilForegroundMask? = nil
    ) -> Int {
        let x0 = max(0, Int(rect.minX.rounded(.down)))
        let y0 = max(originY, Int(rect.minY.rounded(.down)))
        let x1 = min(width, Int(rect.maxX.rounded(.up)))
        let y1 = min(originY + height, Int(rect.maxY.rounded(.up)))
        guard x1 > x0, y1 > y0 else { return 0 }
        var count = 0
        for y in y0..<y1 {
            for x in x0..<x1 {
                guard baseline?.contains(x: x, y: y) != true,
                      let color = rgb(atX: x, y: y) else { continue }
                let channels = [
                    Int(color.red), Int(color.green), Int(color.blue),
                ]
                let minimum = channels.min() ?? 0
                let chroma = (channels.max() ?? 0) - minimum
                if minimum >= 100 && chroma <= 58 { count += 1 }
            }
        }
        return count
    }
}
