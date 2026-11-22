import CoreGraphics
import CoreImage
import Foundation

/// The game draws controller tutorials at a fixed screen position. They are
/// not world texture and must not travel with an old camera observation.
enum GameplayTutorialPromptMask {
    static func rect(in image: CGImage) -> CGRect? {
        let width = 640, height = 360, bandHeight = 60
        var pixels = [UInt8](repeating: 0, count: width * bandHeight * 4)
        guard let context = CGContext(data: &pixels, width: width, height: bandHeight,
            bitsPerComponent: 8, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        context.interpolationQuality = .medium
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        func bright(_ x: Int, _ y: Int, threshold: UInt8 = 140) -> Bool {
            let i = (y * width + x) * 4
            return pixels[i + 3] > 200 && min(pixels[i], pixels[i + 1], pixels[i + 2]) > threshold
        }
        // Match a bright round controller-button glyph with adjacent lettering,
        // within the tutorial prompt's screen band. The glyph and lettering
        // must both be present, so an empty screen retains all its room pixels.
        for y in 8...33 {
            for x in 240...320 {
                guard bright(x, y - 6), bright(x, y + 6),
                      bright(x - 6, y), bright(x + 6, y) else { continue }
                var circle = 0, corners = 0
                for dy in -8...8 {
                    for dx in -8...8 where bright(x + dx, y + dy) {
                        if dx * dx + dy * dy <= 49 { circle += 1 }
                        else if abs(dx) >= 7 && abs(dy) >= 7 { corners += 1 }
                    }
                }
                guard circle >= 85, corners <= 4 else { continue }
                var letters = 0
                for ty in (y - 7)...(y + 7) {
                    for tx in (x + 17)...(x + 70) where bright(tx, ty, threshold: 100) {
                        letters += 1
                    }
                }
                guard letters >= 20 else { continue }
                let sx = CGFloat(image.width) / CGFloat(width)
                let sy = CGFloat(image.height) / CGFloat(height)
                // Text is to the right; allow room for longer localized labels.
                return CGRect(x: CGFloat(x - 12) * sx,
                              y: CGFloat(bandHeight - (y + 12)) * sy,
                              width: 112 * sx, height: 24 * sy).integral
                    .intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
            }
        }
        return nil
    }

    static func removing(from image: CGImage, context: CIContext) -> CGImage {
        guard let rect = rect(in: image) else { return image }
        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let mask = CIImage(color: .white).cropped(to: rect)
            .composited(over: CIImage(color: .black).cropped(to: extent))
        let cleared = CIImage(color: .clear).cropped(to: extent).applyingFilter("CIBlendWithMask",
            parameters: [kCIInputBackgroundImageKey: CIImage(cgImage: image), kCIInputMaskImageKey: mask])
        return context.createCGImage(cleared, from: extent) ?? image
    }
}
