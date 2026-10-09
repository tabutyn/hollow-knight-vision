import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class RenderedFrameMarkerTests: XCTestCase {
    func testPixelMarkerRoundTripAndRejectsCorruption() {
        for frame in [0, 1, 255, 256, 65535, 123456, 0xFFFFFF] {
            let checksum = 0x5A ^ (frame & 255) ^ ((frame >> 8) & 255) ^ ((frame >> 16) & 255)
            let word = UInt64(0xD3) | (UInt64(frame) << 8) | (UInt64(checksum) << 32)
            var pixels = [UInt8](repeating: 127, count: 640 * 360)
            for bit in 0..<40 {
                let value: UInt8 = word & (1 << bit) == 0 ? 0 : 255
                for y in 0..<6 { for x in (bit * 3)..<(bit * 3 + 3) {
                    pixels[y * 640 + x] = y < 3 ? value : 255 - value
                } }
            }
            XCTAssertEqual(RenderedFrameMarker.decode(pixels: pixels, width: 640, height: 360), frame)
            pixels[640 + 1] = 127
            XCTAssertNil(RenderedFrameMarker.decode(pixels: pixels, width: 640, height: 360))
        }
        XCTAssertNil(RenderedFrameMarker.decode(pixels: [UInt8](repeating: 0, count: 640 * 360), width: 640, height: 360))
    }

    func testHelloMarkerIsExplicitlyOptInAndOldHelloStillDecodes() throws {
        let id = UUID()
        let request = ReceiverCapabilityRequest(sessionID: id)
        XCTAssertNil(request.renderFrameMarker)
        let old = "{\"version\":2,\"type\":\"hello\",\"sessionID\":\"\(id.uuidString)\"}"
        XCTAssertEqual(try JSONDecoder().decode(ReceiverCapabilityRequest.self, from: Data(old.utf8)), request)
        XCTAssertEqual(ReceiverCapabilityRequest(sessionID: id, renderFrameMarker: true).renderFrameMarker, true)
    }

    func testMarkerDecodesDirectlyFromCapturedImage() throws {
        let frame = 123_456
        let checksum = 0x5A ^ (frame & 255) ^ ((frame >> 8) & 255) ^ ((frame >> 16) & 255)
        let word = UInt64(0xD3) | (UInt64(frame) << 8) | (UInt64(checksum) << 32)
        var pixels = [UInt8](repeating: 127, count: 640 * 360)
        for bit in 0..<40 {
            let value: UInt8 = word & (1 << bit) == 0 ? 0 : 255
            for y in 0..<6 { for x in (bit * 3)..<(bit * 3 + 3) {
                pixels[y * 640 + x] = y < 3 ? value : 255 - value
            } }
        }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        let image = try XCTUnwrap(CGImage(
            width: 640, height: 360,
            bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: 640,
            space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false,
            intent: .defaultIntent
        ))
        XCTAssertEqual(RenderedFrameMarker.decode(image), frame)
    }
}
