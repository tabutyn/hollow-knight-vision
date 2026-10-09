import CoreImage
import XCTest
@testable import HollowKnightVision

final class GroundAtlasFootprintTests: XCTestCase {
    func testCountsPaintedPixelsNotTilePaddingAndAllowsVerticalGrowth() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let image = try XCTUnwrap(context.createCGImage(
            CIImage(color: .white).cropped(to: CGRect(x: 0, y: 0, width: 16, height: 12)),
            from: CGRect(x: 0, y: 0, width: 16, height: 12)))
        let atlas = LiveTiledAtlas(context: context, anchorPosition: .zero)
        XCTAssertTrue(atlas.insert(observationID: 0, maskedImage: image,
            solveWidth: 16, cameraPosition: .zero))
        XCTAssertTrue(atlas.insert(observationID: 1, maskedImage: image,
            solveWidth: 16, cameraPosition: CGPoint(x: 0, y: 30)))
        let clean = GroundAtlasFootprint.measure(atlas.tiles, viewX: 0, viewWidth: 16)
        XCTAssertEqual(clean.minimumX, 0)
        XCTAssertEqual(clean.maximumX, 16)
        XCTAssertEqual(clean.leftPixels, 0)
        XCTAssertEqual(clean.rightPixels, 0)
        XCTAssertEqual(clean.paintedPixels, 384)
        XCTAssertTrue(atlas.insert(observationID: 2, maskedImage: image,
            solveWidth: 16, cameraPosition: CGPoint(x: -4, y: 0)))
        XCTAssertTrue(atlas.insert(observationID: 3, maskedImage: image,
            solveWidth: 16, cameraPosition: CGPoint(x: 5, y: 0)))
        let drift = GroundAtlasFootprint.measure(atlas.tiles, viewX: 0, viewWidth: 16)
        XCTAssertEqual(drift.minimumX, -4)
        XCTAssertEqual(drift.maximumX, 21)
        XCTAssertEqual(drift.leftPixels, 48)
        XCTAssertEqual(drift.rightPixels, 60)
    }
}
