import Foundation
import XCTest
@testable import HollowKnightVision

final class AtlasPathInspectionTests: XCTestCase {
    func testInspectionBuildsGroundBasedPathFromSavedAtlas() throws {
        guard let source = ProcessInfo.processInfo.environment["HKV_PATH_ATLAS"] else {
            throw XCTSkip("Set HKV_PATH_ATLAS to a saved Hacker world to inspect its ground path.")
        }
        let data = try AtlasRoadInspection.data(world: URL(fileURLWithPath: source))
        let rooms = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [[String: Any]])
        let room = try XCTUnwrap(rooms.first { $0["scene"] as? String == "Tutorial_01" })
        XCTAssertNil(room["issue"], "\(room)")
        let floors = try XCTUnwrap(room["floors"] as? [[[Int]]])
        let strokes = try XCTUnwrap(room["strokes"] as? [[String: Any]])
        let nodes = try XCTUnwrap(room["nodes"] as? [[String: Any]])
        XCTAssertFalse(floors.isEmpty)
        XCTAssertFalse(nodes.isEmpty)
        XCTAssertTrue(strokes.contains { $0["kind"] as? String == "walk" })
        for stroke in strokes {
            let a = try XCTUnwrap(stroke["from"] as? [Int])
            let b = try XCTUnwrap(stroke["to"] as? [Int])
            XCTAssertEqual(a.count, 2)
            XCTAssertEqual(b.count, 2)
            XCTAssertTrue(a[0] == b[0] || a[1] == b[1])
        }
    }
}
