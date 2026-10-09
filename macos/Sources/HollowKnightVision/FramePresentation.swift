import CoreGraphics
import CoreImage
import Foundation

enum GameplayFrameProcessor {
    static let aspectRatio: CGFloat = 16.0 / 9.0
    static let presentationWidth: CGFloat = 640

    static func gameplayCrop(in extent: CGRect) -> CGRect {
        guard extent.width > 0, extent.height > 0 else { return .zero }
        let inputRatio = extent.width / extent.height
        if inputRatio > aspectRatio {
            let width = extent.height * aspectRatio
            return CGRect(x: extent.midX - width / 2, y: extent.minY, width: width, height: extent.height)
        }
        let height = extent.width / aspectRatio
        let verticalExcess = extent.height - height
        // A desktop-independent ScreenCaptureKit window includes its title bar.
        // Hollow Knight's 16:9 drawable is anchored at the bottom of that
        // window. Treat a small vertical excess as window chrome; a genuinely
        // tall source still receives the ordinary centered letterbox crop.
        let isWindowChrome = verticalExcess <= extent.width * 0.05
        let originY = isWindowChrome ? extent.minY : extent.midY - height / 2
        return CGRect(x: extent.minX, y: originY, width: extent.width, height: height)
    }

    static func croppedGameplay(from source: CIImage) -> CIImage {
        let crop = gameplayCrop(in: source.extent)
        return source
            .cropped(to: crop)
            .transformed(by: CGAffineTransform(translationX: -crop.minX, y: -crop.minY))
    }

    static func presentationImage(from gameplay: CIImage) -> CIImage {
        let scale = presentationWidth / max(1, gameplay.extent.width)
        return gameplay.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
    }
}

enum WorldPlacement {
    static func offset(
        cameraPosition: CGPoint,
        anchorPosition: CGPoint,
        presentationWidth: CGFloat,
        solveWidth: CGFloat,
        gain: CGFloat
    ) -> CGPoint {
        let scale = presentationWidth / max(1, solveWidth)
        return CGPoint(
            x: (cameraPosition.x - anchorPosition.x) * scale * gain,
            y: (cameraPosition.y - anchorPosition.y) * scale * gain
        )
    }
}
