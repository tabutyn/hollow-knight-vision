import CoreGraphics
import Foundation

/// Identifies the running Hollow Knight process from its owning application.
/// Window titles are deliberately excluded because browsers can display game
/// names in tab titles.
enum SourceWindowIdentity {
    private static let hollowKnightApplication = "hollowknight"
    private static let hollowKnightBundle = "unityteamcherryhollowknight"

    static func isHollowKnight(
        applicationName: String?,
        bundleIdentifier: String?
    ) -> Bool {
        normalized(applicationName) == hollowKnightApplication
            && normalized(bundleIdentifier) == hollowKnightBundle
    }

    /// Capture discovery intentionally does not depend on `isOnScreen`.
    /// A valid game window can be fully occluded by Vision or live on another
    /// Space and ScreenCaptureKit can still capture it independently.
    static func isCaptureWindow(
        applicationName: String?,
        bundleIdentifier: String?,
        windowLayer: Int,
        windowSize: CGSize
    ) -> Bool {
        windowLayer == 0
            && windowSize.width.isFinite
            && windowSize.height.isFinite
            && windowSize.width >= 320
            && windowSize.height >= 180
            && isHollowKnight(
            applicationName: applicationName,
            bundleIdentifier: bundleIdentifier
        )
    }

    private static func normalized(_ value: String?) -> String {
        guard let value else { return "" }
        return value
            .folding(
                options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive],
                locale: Locale(identifier: "en_US_POSIX")
            )
            .unicodeScalars
            .filter { CharacterSet.alphanumerics.contains($0) }
            .map(String.init)
            .joined()
            .lowercased()
    }
}
