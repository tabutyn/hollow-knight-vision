import CoreGraphics
import Foundation

/// Diagnostic association between Unity rendering and captured frames.
/// This value is recorded only; camera/feature algorithms must not consume it.
enum RenderedFrameMarker {
    static let enabled = ProcessInfo.processInfo.arguments.contains("--render-frame-marker")

    static func decode(_ analysis: GroundEdgeAnalysis) -> Int? {
        guard let pixels = analysis.sourceLuma else { return nil }
        return decode(pixels: pixels, width: analysis.width, height: analysis.height)
    }

    /// Reads the diagnostic barcode without running any visual tracker. Hacker
    /// uses this only to join a capture to telemetry from the same Unity frame.
    static func decode(_ image: CGImage) -> Int? {
        let width = image.width, height = image.height
        guard width >= 320, height >= 180 else { return nil }
        let bandWidth = max(120, Int((Double(width) * 120 / 640).rounded(.up)))
        let bandHeight = max(6, Int((Double(height) * 6 / 360).rounded(.up)))
        guard let band = image.cropping(to: CGRect(
            x: 0, y: 0, width: bandWidth, height: bandHeight
        )) else { return nil }
        var pixels = [UInt8](repeating: 0, count: bandWidth * bandHeight)
        let drew = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(
                data: bytes.baseAddress,
                width: bandWidth,
                height: bandHeight,
                bitsPerComponent: 8,
                bytesPerRow: bandWidth,
                space: CGColorSpaceCreateDeviceGray(),
                bitmapInfo: CGImageAlphaInfo.none.rawValue
            ) else { return false }
            context.interpolationQuality = .none
            context.draw(band, in: CGRect(x: 0, y: 0, width: bandWidth, height: bandHeight))
            return true
        }
        return drew ? decodeMarkerBand(pixels: pixels, width: bandWidth, height: bandHeight) : nil
    }

    private static func decodeMarkerBand(pixels: [UInt8], width: Int, height: Int) -> Int? {
        guard width >= 120, height >= 6, pixels.count == width * height else { return nil }
        let sx = Double(width) / 120, sy = Double(height) / 6
        var word: UInt64 = 0
        for bit in 0..<40 {
            let x = min(width - 1, Int((Double(bit) * 3 + 1.5) * sx))
            let top = pixels[min(height - 1, Int(1.5 * sy)) * width + x]
            let bottom = pixels[min(height - 1, Int(4.5 * sy)) * width + x]
            guard (top > 210 && bottom < 45) || (top < 45 && bottom > 210) else { return nil }
            if top > bottom { word |= 1 << bit }
        }
        return validatedFrame(in: word)
    }

    static func decode(pixels: [UInt8], width: Int, height: Int) -> Int? {
        guard width >= 320, height >= 180, pixels.count == width * height else { return nil }
        let sx = Double(width) / 640, sy = Double(height) / 360
        var word: UInt64 = 0
        for bit in 0..<40 {
            let x = Int((Double(bit) * 3 + 1.5) * sx)
            let top = pixels[Int(1.5 * sy) * width + x]
            let bottom = pixels[Int(4.5 * sy) * width + x]
            guard (top > 210 && bottom < 45) || (top < 45 && bottom > 210) else { return nil }
            if top > bottom { word |= 1 << bit }
        }
        return validatedFrame(in: word)
    }

    private static func validatedFrame(in word: UInt64) -> Int? {
        guard word & 255 == 0xD3 else { return nil }
        let frame = Int((word >> 8) & 0xFFFFFF)
        let checksum = 0x5A ^ (frame & 255) ^ ((frame >> 8) & 255) ^ ((frame >> 16) & 255)
        guard Int(word >> 32) == checksum else { return nil }
        return frame
    }
}
