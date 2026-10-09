import CoreGraphics

/// Shared detector/tracker pixels: DeviceGray, no interpolation, original orientation.
enum GroundPixels {
    static func luma(_ image: CGImage, width: Int, height: Int) -> [UInt8]? {
        var pixels = [UInt8](repeating: 0, count: width * height)
        let drew = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(data: bytes.baseAddress, width: width, height: height,
                bitsPerComponent: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            context.interpolationQuality = .none
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        return drew ? pixels : nil
    }

    static func patch(_ pixels: [UInt8], width: Int, height: Int,
                      x: Int, y: Int) -> [UInt8]? {
        guard width > 0, height > 0, pixels.count >= width * height,
              x >= 0, x + 16 <= width, y >= 0, y + 12 <= height else { return nil }
        var values = [UInt8]()
        values.reserveCapacity(192)
        for row in 0..<12 {
            let start = (y + row) * width + x
            values.append(contentsOf: pixels[start..<(start + 16)])
        }
        return values
    }
}
