import CoreGraphics
import CoreImage
import XCTest

@testable import HollowKnightVision

final class RegistrationFrameMaskTests: XCTestCase {
    private let context = CIContext(options: [.useSoftwareRenderer: true])

    func testMasksPaddedForegroundAndPreservesOutsidePixels() {
        let source = testImage(extent: CGRect(x: 0, y: 0, width: 200, height: 100))
        let output = RegistrationFrameMask.applying(
            to: source,
            foregroundRect: CGRect(x: 40, y: 20, width: 20, height: 10),
            presentationSize: CGSize(width: 100, height: 50),
            padding: 5
        )

        XCTAssertEqual(output.extent, source.extent)
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 72, y: 40)), [0, 0, 0, 255],
                       "Padding is measured in presentation pixels before scaling.")
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 70, y: 40)), [0, 0, 0, 255])
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 69, y: 25)),
                       pixel(in: source, at: CGPoint(x: 69, y: 25)),
                       "A pixel outside the padded rectangle must be untouched.")
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 150, y: 70)),
                       pixel(in: source, at: CGPoint(x: 150, y: 70)))
    }

    func testScalesAndClipsPresentationRectToSolveExtent() {
        let extent = CGRect(x: 4, y: 6, width: 200, height: 100)
        let source = testImage(extent: extent)
        let output = RegistrationFrameMask.applying(
            to: source,
            foregroundRect: CGRect(x: 90, y: 45, width: 20, height: 10),
            presentationSize: CGSize(width: 100, height: 50),
            padding: 5
        )

        // Padded presentation rect (85, 40, 30, 20) becomes (174, 86, 60, 40)
        // in the solve image and clips to the non-zero source extent.
        XCTAssertEqual(output.extent, extent)
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 190, y: 96)), [0, 0, 0, 255])
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 173, y: 96)),
                       pixel(in: source, at: CGPoint(x: 173, y: 96)))
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 10, y: 10)),
                       pixel(in: source, at: CGPoint(x: 10, y: 10)))
    }

    func testNilOrInvalidForegroundPreservesInput() {
        let source = testImage(extent: CGRect(x: 0, y: 0, width: 80, height: 40))
        let nilOutput = RegistrationFrameMask.applying(
            to: source, foregroundRect: nil, presentationSize: CGSize(width: 40, height: 20))
        let invalidOutput = RegistrationFrameMask.applying(
            to: source,
            foregroundRect: CGRect(x: CGFloat.infinity, y: 0, width: 4, height: 4),
            presentationSize: CGSize(width: 40, height: 20)
        )

        for point in [CGPoint(x: 4, y: 4), CGPoint(x: 39, y: 20), CGPoint(x: 75, y: 35)] {
            XCTAssertEqual(pixel(in: nilOutput, at: point), pixel(in: source, at: point))
            XCTAssertEqual(pixel(in: invalidOutput, at: point), pixel(in: source, at: point))
        }
    }

    func testMasksMultipleForegroundRects() {
        let source = testImage(extent: CGRect(x: 0, y: 0, width: 100, height: 50))
        let output = RegistrationFrameMask.applying(
            to: source,
            foregroundRects: [
                CGRect(x: 5, y: 5, width: 5, height: 5),
                CGRect(x: 80, y: 35, width: 5, height: 5),
            ],
            presentationSize: CGSize(width: 100, height: 50),
            padding: 0
        )

        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 7, y: 7)), [0, 0, 0, 255])
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 82, y: 37)), [0, 0, 0, 255])
        XCTAssertEqual(pixel(in: output, at: CGPoint(x: 50, y: 25)),
                       pixel(in: source, at: CGPoint(x: 50, y: 25)))
    }

    private func testImage(extent: CGRect) -> CIImage {
        let base = CIImage(color: CIColor(red: 0.14, green: 0.32, blue: 0.71, alpha: 1))
            .cropped(to: extent)
        return CIImage(color: CIColor(red: 0.93, green: 0.17, blue: 0.24, alpha: 1))
            .cropped(to: CGRect(x: extent.midX - 15, y: extent.midY - 10, width: 30, height: 20))
            .composited(over: base)
    }

    private func pixel(in image: CIImage, at point: CGPoint) -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: 4)
        context.render(
            image,
            toBitmap: &bytes,
            rowBytes: 4,
            bounds: CGRect(x: point.x, y: point.y, width: 1, height: 1),
            format: .RGBA8,
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        return bytes
    }
}
