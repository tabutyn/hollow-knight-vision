import CoreGraphics
import CoreImage

/// Replaces a moving foreground region before translational registration.
///
/// The foreground rectangle is measured in the presentation CGImage's
/// bottom-left coordinate space. It is padded there, then scaled into the
/// solve image so a caller can use the same detector output for presentation,
/// feature tracking, and registration.
enum RegistrationFrameMask {
    static let defaultPresentationPadding: CGFloat = 12

    static func applying(
        to image: CIImage,
        foregroundRect: CGRect?,
        presentationSize: CGSize,
        padding: CGFloat = defaultPresentationPadding
    ) -> CIImage {
        applying(
            to: image,
            foregroundRects: foregroundRect.map { [$0] } ?? [],
            presentationSize: presentationSize,
            padding: padding
        )
    }

    static func applying(
        to image: CIImage,
        foregroundRects: [CGRect],
        presentationSize: CGSize,
        padding: CGFloat = defaultPresentationPadding
    ) -> CIImage {
        let extent = image.extent
        guard isFiniteAndNonEmpty(extent),
              presentationSize.width.isFinite,
              presentationSize.height.isFinite,
              presentationSize.width > 0,
              presentationSize.height > 0,
              padding.isFinite,
              padding >= 0
        else {
            return image
        }

        let scaleX = extent.width / presentationSize.width
        let scaleY = extent.height / presentationSize.height
        return foregroundRects.reduce(image) { result, foregroundRect in
            guard isFiniteAndNonEmpty(foregroundRect) else { return result }
            let paddedPresentationRect = foregroundRect.insetBy(dx: -padding, dy: -padding)
            guard isFiniteAndNonEmpty(paddedPresentationRect) else { return result }
            let solveRect = CGRect(
                x: extent.minX + paddedPresentationRect.minX * scaleX,
                y: extent.minY + paddedPresentationRect.minY * scaleY,
                width: paddedPresentationRect.width * scaleX,
                height: paddedPresentationRect.height * scaleY
            )
            let clipped = solveRect.intersection(extent)
            guard isFiniteAndNonEmpty(clipped) else { return result }
            return CIImage(color: .black)
                .cropped(to: clipped)
                .composited(over: result)
                .cropped(to: extent)
        }
    }

    private static func isFiniteAndNonEmpty(_ rect: CGRect) -> Bool {
        !rect.isNull && !rect.isEmpty
            && rect.minX.isFinite && rect.minY.isFinite
            && rect.width.isFinite && rect.height.isFinite
            && rect.width > 0 && rect.height > 0
    }
}
