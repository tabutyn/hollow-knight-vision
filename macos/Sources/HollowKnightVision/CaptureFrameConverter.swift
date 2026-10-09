import CoreGraphics
import CoreImage
import CoreVideo

struct ConvertedCaptureFrame {
    let image: CGImage
    let solveWidth: CGFloat
}

/// Preserve Core Image color management, gameplay crop, and the 640px output.
enum CaptureFrameConverter {
    static func convert(_ buffer: CVPixelBuffer, context: CIContext) -> ConvertedCaptureFrame? {
        let gameplay = GameplayFrameProcessor.croppedGameplay(from: CIImage(cvPixelBuffer: buffer))
        let presented = GameplayFrameProcessor.presentationImage(from: gameplay)
        guard let image = context.createCGImage(presented, from: presented.extent) else { return nil }
        return ConvertedCaptureFrame(image: image, solveWidth: gameplay.extent.width)
    }
}
