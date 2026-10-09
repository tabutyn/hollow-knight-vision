import CoreGraphics
import XCTest

@testable import HollowKnightVision

final class PoseCorrectionInterpolationTests: XCTestCase {
    private let visit = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    private let otherVisit = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!

    func testExactTimestampReturnsMatchingCorrection() {
        let samples = [sample(2, time: 2, point: CGPoint(x: 4, y: -3))]
        XCTAssertEqual(PoseCorrectionInterpolator.correction(forTimestamp: 2, visitID: visit, samples: samples), CGPoint(x: 4, y: -3))
    }

    func testMidpointLinearlyInterpolatesCorrectionAndRefinesRawPose() {
        let samples = [sample(1, time: 0, point: .zero), sample(2, time: 4, point: CGPoint(x: 8, y: -4))]
        XCTAssertEqual(PoseCorrectionInterpolator.correction(forTimestamp: 2, visitID: visit, samples: samples), CGPoint(x: 4, y: -2))
        XCTAssertEqual(PoseCorrectionInterpolator.refinedPose(raw: CGPoint(x: 10, y: 5), forTimestamp: 2, visitID: visit, samples: samples), CGPoint(x: 14, y: 3))
    }

    func testOutsideIntervalUsesNearestEndpointWithinVisit() {
        let samples = [sample(1, time: 10, point: CGPoint(x: 1, y: 2)), sample(2, time: 20, point: CGPoint(x: 3, y: 4))]
        XCTAssertEqual(PoseCorrectionInterpolator.correction(forTimestamp: 1, visitID: visit, samples: samples), CGPoint(x: 1, y: 2))
        XCTAssertEqual(PoseCorrectionInterpolator.correction(forTimestamp: 30, visitID: visit, samples: samples), CGPoint(x: 3, y: 4))
    }

    func testVisitIsolationAndEmptyVisitReturnIdentity() {
        let samples = [sample(1, time: 0, point: CGPoint(x: 99, y: 99), visit: otherVisit)]
        XCTAssertEqual(PoseCorrectionInterpolator.correction(forTimestamp: 0, visitID: visit, samples: samples), .zero)
    }

    func testInvalidInputsAreIgnored() {
        let samples = [
            sample(1, time: .infinity, point: CGPoint(x: 10, y: 10)),
            sample(2, time: 1, point: CGPoint(x: CGFloat.nan, y: 10)),
            sample(3, time: 2, point: CGPoint(x: 6, y: 7)),
        ]
        XCTAssertEqual(PoseCorrectionInterpolator.correction(forTimestamp: 2, visitID: visit, samples: samples), CGPoint(x: 6, y: 7))
        XCTAssertEqual(PoseCorrectionInterpolator.correction(forTimestamp: .nan, visitID: visit, samples: samples), .zero)
    }

    func testOrderingIsDeterministicIncludingSameTimestamp() {
        let early = sample(4, time: 1, point: CGPoint(x: 2, y: 4))
        let laterAtSameTime = sample(8, time: 1, point: CGPoint(x: 8, y: 16))
        let end = sample(9, time: 3, point: CGPoint(x: 10, y: 20))
        let forward = PoseCorrectionInterpolator.correction(forTimestamp: 1, visitID: visit, samples: [laterAtSameTime, end, early])
        let reverse = PoseCorrectionInterpolator.correction(forTimestamp: 1, visitID: visit, samples: [early, end, laterAtSameTime])
        XCTAssertEqual(forward, CGPoint(x: 2, y: 4))
        XCTAssertEqual(forward, reverse)
    }

    private func sample(_ id: Int, time: Double, point: CGPoint, visit: UUID? = nil) -> PoseCorrectionSample {
        PoseCorrectionSample(observationID: id, visitID: visit ?? self.visit, timestamp: time, correction: point)
    }
}
