import CoreGraphics
import CoreImage
import CoreVideo
import ScreenCaptureKit

struct ConvertedCaptureFrame {
    let image: CGImage
    let solveWidth: CGFloat
}

/// Preserve Core Image color management, gameplay crop, and the 640px output.
enum CaptureFrameConverter {
    /// ScreenCaptureKit reports the visible window in top-origin surface
    /// points. contentScale is already reflected in this rectangle.
    static func windowExtent(in buffer: CGRect, info: [SCStreamFrameInfo: Any]?) -> CGRect {
        guard let dict = info?[.contentRect] as? NSDictionary,
              let rect = CGRect(dictionaryRepresentation: dict),
              let scale = info?[.scaleFactor] as? NSNumber else { return buffer }
        let factor = CGFloat(scale.doubleValue)
        guard factor.isFinite, factor > 0, rect.origin.x.isFinite, rect.origin.y.isFinite,
              rect.size.width.isFinite, rect.size.height.isFinite, !rect.isEmpty else { return buffer }
        let left = (rect.minX * factor).rounded()
        let top = (rect.minY * factor).rounded()
        let right = (rect.maxX * factor).rounded()
        let bottom = (rect.maxY * factor).rounded()
        let window = CGRect(x: buffer.minX + left, y: buffer.maxY - bottom,
                            width: right - left, height: bottom - top)
        guard !window.isEmpty, buffer.contains(window) else { return buffer }
        return window
    }

    static func convert(_ buffer: CVPixelBuffer, info: [SCStreamFrameInfo: Any]?,
                        context: CIContext) -> ConvertedCaptureFrame? {
        let source = CIImage(cvPixelBuffer: buffer)
        let window = source.cropped(to: windowExtent(in: source.extent, info: info))
        let gameplay = GameplayFrameProcessor.croppedGameplay(from: window)
        let presented = GameplayFrameProcessor.presentationImage(from: gameplay)
        let extent = CGRect(x: 0, y: 0, width: GameplayFrameProcessor.presentationWidth,
                            height: GameplayFrameProcessor.presentationWidth / GameplayFrameProcessor.aspectRatio)
        guard let image = context.createCGImage(presented, from: extent) else { return nil }
        return ConvertedCaptureFrame(image: image, solveWidth: gameplay.extent.width)
    }
}
