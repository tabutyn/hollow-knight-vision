import XCTest
@testable import HollowKnightVision

final class BotRecoveryManeuverTests: XCTestCase {
    func testSplitLedgesMatchAndFlatViewsDoNotStartTheCorrection() {
        let a = [[0.0, 0.32, 0.14], [0.32, 0.41, 0.07], [0.46, 0.96, 0.14]]
        let b = [[0.03, 0.3, 0.14], [0.33, 0.39, 0.07], [0.47, 0.64, 0.14], [0.68, 0.89, 0.14]]
        XCTAssertTrue(BotRecoveryDriver.matchesFloors(a, b, requireRelief: true))
        XCTAssertFalse(BotRecoveryDriver.matchesFloors(a, [[0.0, 1.0, 0.14]], requireRelief: true))
        XCTAssertFalse(BotRecoveryDriver.matchesFloors(a, [], requireRelief: false))
    }

    func testPrivateRecordedRecoveryStartsOnlyWithAMatchingHiddenFrame() throws {
        let env = ProcessInfo.processInfo.environment
        guard let model = env["HKV_RECOVERY_MODEL"], let session = env["HKV_RECOVERY_CLIP"] else {
            throw XCTSkip("Set the private correction model and clip paths to test captured pixels.")
        }
        let clip = try BotSnippet.load(URL(fileURLWithPath: session))
        let policy = try BotNativePolicy.load(from: URL(fileURLWithPath: model))
        let doc = try XCTUnwrap(policy.document.recoveryManeuver)
        var driver = try XCTUnwrap(BotRecoveryDriver(doc))
        let row = try XCTUnwrap(clip.observations.first)
        let file = try XCTUnwrap(row.imagePath)
        let image = try XCTUnwrap(ImageFileIO.load(clip.directory.appendingPathComponent(file)))
        if let other = env["HKV_RECOVERY_OTHER_CLIP"] {
            let otherClip = try BotSnippet.load(URL(fileURLWithPath: other))
            let otherRow = try XCTUnwrap(otherClip.observations.first)
            let otherImage = try XCTUnwrap(ImageFileIO.load(otherClip.directory.appendingPathComponent(try XCTUnwrap(otherRow.imagePath))))
            // Even a missing detection must not start this maneuver on the other side of the pit.
            XCTAssertNil(driver.action(otherRow, image: otherImage, masked: true, now: otherRow.availableAt))
        }
        XCTAssertNil(driver.action(row, image: image, masked: false, now: row.availableAt))
        XCTAssertNil(driver.action(row, image: nil, masked: true, now: row.availableAt))
        XCTAssertNotNil(driver.action(row, image: image, masked: true, now: row.availableAt))
        if clip.id == doc.session {
            for observation in clip.observations.dropFirst() where observation.availableAt - row.availableAt < doc.duration {
                let elapsed = observation.availableAt - row.availableAt
                let expected = doc.controls.last { $0.time <= elapsed }
                XCTAssertEqual(driver.action(observation, image: nil, masked: false, now: observation.availableAt)?.values,
                               expected?.values, "Lost the recorded trajectory at \(elapsed) seconds")
            }
        }
        driver.cancel()
        XCTAssertNil(driver.action(row, image: image, masked: true, now: row.availableAt + 3))
    }
}
