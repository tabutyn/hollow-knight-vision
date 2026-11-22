import CoreGraphics
import CoreImage
import XCTest
@testable import HollowKnightVision

final class GameplayTutorialPromptMaskTests: XCTestCase {
    private func frame(prompt: Bool) throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360,
            bitsPerComponent: 8, bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 0.1, green: 0.2, blue: 0.3, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 640, height: 360))
        if prompt {
            context.setFillColor(CGColor(gray: 1, alpha: 1))
            context.fillEllipse(in: CGRect(x: 282, y: 30, width: 15, height: 15))
            for x in stride(from: 309, through: 339, by: 6) {
                context.fill(CGRect(x: x, y: 34, width: 3, height: 7))
            }
        }
        return try XCTUnwrap(context.makeImage())
    }

    private func alpha(_ image: CGImage, x: Int, y: Int) -> UInt8 {
        var pixel = [UInt8](repeating: 0, count: 4)
        CIContext().render(CIImage(cgImage: image), toBitmap: &pixel, rowBytes: 4,
            bounds: CGRect(x: x, y: y, width: 1, height: 1), format: .RGBA8,
            colorSpace: CGColorSpaceCreateDeviceRGB())
        return pixel[3]
    }

    func testPromptIsRemovedWhileUnrelatedPixelsRemain() throws {
        let image = try frame(prompt: true)
        let rect = try XCTUnwrap(GameplayTutorialPromptMask.rect(in: image))
        XCTAssertTrue(rect.contains(CGPoint(x: 289, y: 37)))
        XCTAssertTrue(rect.contains(CGPoint(x: 335, y: 37)))
        let result = GameplayTutorialPromptMask.removing(from: image, context: CIContext())
        XCTAssertEqual(alpha(result, x: 289, y: 37), 0)
        XCTAssertEqual(alpha(result, x: 335, y: 37), 0)
        XCTAssertEqual(alpha(result, x: 400, y: 37), 255)
    }

    func testPromptBandRemainsOpaqueWhenPromptIsAbsent() throws {
        let image = try frame(prompt: false)
        XCTAssertNil(GameplayTutorialPromptMask.rect(in: image))
        let result = GameplayTutorialPromptMask.removing(from: image, context: CIContext())
        XCTAssertEqual(alpha(result, x: 289, y: 37), 255)
        XCTAssertEqual(alpha(result, x: 335, y: 37), 255)
    }
}
