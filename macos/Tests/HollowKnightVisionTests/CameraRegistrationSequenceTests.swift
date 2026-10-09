import CoreGraphics
import CoreImage
import Vision
import XCTest
@testable import HollowKnightVision

final class CameraRegistrationSequenceTests: XCTestCase {
    private let context = CIContext(options: [.useSoftwareRenderer: true])

    func testPersistentVisionRegistrationReportsAdjacentFrameTransforms() throws {
        let offsets: [(x: CGFloat, y: CGFloat)] = [
            (0, 0), (-8, 0), (-16, -4), (-12, -4), (0, 0),
        ]
        let expected: [(x: CGFloat, y: CGFloat)] = [
            (8, 0), (8, 4), (-4, 0), (-12, -4),
        ]
        for useLiveROI in [false, true] {
            let request = VNTrackTranslationalImageRegistrationRequest()
            if useLiveROI {
                request.regionOfInterest = CGRect(x: 0.06, y: 0.07, width: 0.88, height: 0.70)
            }
            var transforms = [CGAffineTransform]()
            for offset in offsets {
                let image = try asymmetricTexture(offsetX: offset.x, offsetY: offset.y)
                try VNImageRequestHandler(cgImage: image, orientation: .up).perform([request])
                if let result = request.results?.first {
                    transforms.append(result.alignmentTransform)
                }
            }
            print("Vision sequence ROI=\(useLiveROI): \(transforms.map { "(\($0.tx), \($0.ty))" }.joined(separator: ", "))")
            XCTAssertEqual(transforms.count, expected.count, "first request is warmup; each later frame must report one transform")
            for (transform, target) in zip(transforms, expected) {
                XCTAssertEqual(transform.tx, target.x, accuracy: 1.5)
                XCTAssertEqual(transform.ty, target.y, accuracy: 1.5)
            }
        }
    }

    /// CI's source coordinate system is bottom-left. A negative source Y shift
    /// moves the current content down, so Vision must return positive Y to
    /// align it back to the prior frame.
    private func asymmetricTexture(offsetX: CGFloat, offsetY: CGFloat) throws -> CGImage {
        let extent = CGRect(x: 0, y: 0, width: 384, height: 216)
        var image = CIImage(color: CIColor(red: 0.03, green: 0.04, blue: 0.06)).cropped(to: extent)
        // Fixed LCG texture: irregular locations, sizes, and colors avoid a
        // repeating grid or a single broad edge that could alias vertically.
        var state: UInt64 = 0xD1CE_BA5E_5EED
        func next() -> UInt64 {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return state
        }
        for index in 0..<180 {
            let x = CGFloat(next() % 350) + 12
            let y = CGFloat(next() % 182) + 12
            let width = CGFloat(2 + next() % 13)
            let height = CGFloat(2 + next() % 11)
            let red = CGFloat((next() >> 8) % 220 + 25) / 255
            let green = CGFloat((next() >> 16) % 220 + 25) / 255
            let blue = CGFloat((next() >> 24) % 220 + 25) / 255
            let color = CIColor(red: red, green: green, blue: blue, alpha: index.isMultiple(of: 5) ? 0.7 : 1)
            let rect = CGRect(x: x, y: y, width: width, height: height)
            image = CIImage(color: color)
                .cropped(to: rect.offsetBy(dx: offsetX, dy: offsetY))
                .composited(over: image)
        }
        return try XCTUnwrap(context.createCGImage(image, from: extent))
    }
}
