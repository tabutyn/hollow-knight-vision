import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundViewerTests: XCTestCase {
    func testRowAssemblerStitchesFeaturesAndPreservesDeletedGap() {
        let features = [
            feature(sequence: 2, value: 20),
            feature(sequence: 3, value: 30),
            feature(sequence: 5, value: 50),
        ]
        let slots = GroundViewerRowAssembler.slots(from: features, segmentID: 7)

        XCTAssertEqual(slots.map(\.sequenceIndex), [2, 3, 4, 5])
        XCTAssertNil(slots[2].feature)
        let pixels = GroundViewerRowAssembler.stitchedPixels(from: slots)
        let width = 4 * GroundHypothesisTracker.featureSize
        XCTAssertEqual(pixels[0], 20)
        XCTAssertEqual(pixels[GroundHypothesisTracker.featureSize], 30)
        XCTAssertEqual(pixels[2 * GroundHypothesisTracker.featureSize], 0)
        XCTAssertEqual(pixels[3 * GroundHypothesisTracker.featureSize], 50)
        XCTAssertEqual(pixels.count, width * GroundHypothesisTracker.featureHeight)
        XCTAssertEqual(
            GroundViewerRowAssembler.nearestFeature(toSlot: 2, in: slots)?.sequenceIndex,
            3
        )
    }

    func testStitchedViewerCompositesPerPixelOpacityOverBlack() {
        let transparent = feature(sequence: 0, value: 100, opacity: 0)
        let partial = feature(sequence: 1, value: 100, opacity: 128)
        let opaque = feature(sequence: 2, value: 100, opacity: 255)
        let pixels = GroundViewerRowAssembler.stitchedPixels(from: [
            GroundViewerSlot(sequenceIndex: 0, feature: transparent),
            GroundViewerSlot(sequenceIndex: 1, feature: partial),
            GroundViewerSlot(sequenceIndex: 2, feature: opaque),
        ])

        XCTAssertEqual(pixels[0], 0)
        XCTAssertEqual(pixels[16], 50)
        XCTAssertEqual(pixels[32], 100)
    }

    func testLiveRowStateAddsNewFeaturesAndRefreshesSelectedEvidence() {
        let selected = feature(sequence: 2, value: 20, opacity: 32)
        let state = GroundViewerRowState.refreshed(
            selected: selected,
            available: [
                feature(sequence: 0, value: 10),
                feature(sequence: 1, value: 15),
                feature(sequence: 2, value: 90, opacity: 180),
                feature(sequence: 3, value: 30),
            ]
        )

        XCTAssertEqual(state.slots.map(\.sequenceIndex), [0, 1, 2, 3])
        XCTAssertEqual(state.selected.referencePixels.first, 90)
        XCTAssertEqual(state.selected.referenceOpacity.first, 180)
    }

    private func feature(
        sequence: Int,
        value: UInt8,
        opacity: UInt8? = nil
    ) -> LayerSceneGroundFeatureDetails {
        LayerSceneGroundFeatureDetails(
            segmentID: 7,
            sequenceIndex: sequence,
            worldPosition: CGPoint(x: CGFloat(sequence * 16), y: 40),
            referencePixels: [UInt8](
                repeating: value,
                count: GroundHypothesisTracker.featurePixelCount
            ),
            referenceOpacity: opacity.map {
                [UInt8](
                    repeating: $0,
                    count: GroundHypothesisTracker.featurePixelCount
                )
            } ?? []
        )
    }
}
