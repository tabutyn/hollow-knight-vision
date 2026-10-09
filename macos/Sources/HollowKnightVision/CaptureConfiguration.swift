import CoreMedia
import ScreenCaptureKit

enum HollowKnightCaptureConfiguration {
    // The interactive preview is already presented at 640 points. Capturing
    // directly at that size avoids a redundant 960 -> 640 conversion and
    // leaves enough budget to deliver one frame per 60 Hz display refresh.
    static let outputWidth = 640
    static let framesPerSecond: Int32 = 60
    static let queueDepth = 3
    static let motionSampleStride = 1
    static let objectInferenceStride = 4
    static let atlasSampleStride = 8
    static let gameplayAspectRatio: CGFloat = 16.0 / 9.0
    static let titleBarHeight: CGFloat = 28

    /// Returns a window-relative source rect only when the excess height is
    /// consistent with macOS title-bar chrome. ScreenCaptureKit applies this
    /// before producing a pixel buffer, so chrome never reaches rendering,
    /// registration, feature detection, or atlas persistence.
    static func gameplaySourceRect(windowSize: CGSize) -> CGRect? {
        guard windowSize.width.isFinite, windowSize.height.isFinite,
              windowSize.width > 0, windowSize.height > 0 else { return nil }
        // A native fullscreen game surface is exactly 16:9. The current
        // windowed game is 1470x833 points: only six points taller than 16:9,
        // although its actual macOS title bar is 28 points and its drawable is
        // correspondingly wider. Do not infer chrome height from aspect error.
        let aspectExcess = windowSize.height - windowSize.width / gameplayAspectRatio
        guard aspectExcess > 0.5,
              aspectExcess <= titleBarHeight * 1.5,
              windowSize.height > titleBarHeight * 2 else { return nil }
        return CGRect(
            x: 0,
            y: titleBarHeight,
            width: windowSize.width,
            height: windowSize.height - titleBarHeight
        )
    }

    static func make(windowSize: CGSize) -> SCStreamConfiguration {
        let configuration = SCStreamConfiguration()
        let sourceRect = gameplaySourceRect(windowSize: windowSize)
        let captureSize = sourceRect?.size ?? windowSize
        let ratio = max(0.45, min(1.4, captureSize.height / max(1, captureSize.width)))
        configuration.width = outputWidth
        configuration.height = max(
            360,
            Int((CGFloat(outputWidth) * ratio).rounded()) / 2 * 2
        )
        // Native cadence avoids duplicate/skipped updates from a strict 1/60 interval.
        configuration.minimumFrameInterval = .zero
        configuration.queueDepth = queueDepth
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.scalesToFit = true
        configuration.preservesAspectRatio = true
        configuration.showsCursor = false
        configuration.capturesAudio = false
        if let sourceRect {
            configuration.sourceRect = sourceRect
        }
        return configuration
    }
}
