import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundLabelTests: XCTestCase {
    func testSurfaceSupportRejectsBrightDecorationAndKeepsDarkPlatformBody() {
        let width = 80, height = 40, row = 20
        var response = [UInt8](repeating: 0, count: width * height)
        for x in 0..<width { response[row * width + x] = 255 }
        let tuning = GroundTheoryTuning(
            groundThreshold: 8, minimumSegmentLength: 48,
            lineSeparation: 36, occlusionMergeGap: 180
        )

        let decoration = GroundComparisonAnalysis(
            width: width, height: height, groundPixels: response,
            sourceLuma: [UInt8](repeating: 100, count: width * height)
        )
        let decorationTheory = GroundLineDetector.semanticTheory(
            from: decoration, tuning: tuning
        )
        XCTAssertTrue(decorationTheory.map(
            GroundLineDetector.cleanFloorLines)?.isEmpty == true)

        var platformLuma = [UInt8](repeating: 100, count: width * height)
        for y in (row + 1)..<height {
            for x in 0..<width { platformLuma[y * width + x] = 20 }
        }
        let platform = GroundComparisonAnalysis(
            width: width, height: height, groundPixels: response,
            sourceLuma: platformLuma
        )
        let platformTheory = GroundLineDetector.semanticTheory(from: platform, tuning: tuning)
        XCTAssertEqual(platformTheory.map(
            GroundLineDetector.cleanFloorLines)?.map(\.row), [row])
    }

    private func document(reviewed: Bool = true) -> GroundLabelDocument {
        let id = UUID()
        var document = GroundLabelDocument(imageFile: "\(id.uuidString).png", imageSHA256: String(repeating: "a", count: 64),
            width: 100, height: 60, createdAt: Date(), source: "Test fixture", group: "route-A",
            fullyReviewed: reviewed, runtimeMasksKnown: true, runtimeKnightKnown: true)
        document.id = id
        return document
    }
    private func image(background: UInt8 = 24) throws -> CGImage {
        let width = 100, height = 60
        var pixels = [UInt8](repeating: background, count: width * height)
        for y in 30..<height { for x in 0..<width { pixels[y * width + x] = 180 } }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 8,
            bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: 0),
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }
    private func temporaryStore() throws -> GroundLabelStore {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("ground-label-test-\(UUID().uuidString)")
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return GroundLabelStore(root: root)
    }
    func testCoverageIsOneToOneAndOverlappingLabelsDoNotDoubleCount() {
        var doc = document()
        doc.edges = [GroundLabelEdge(x0: 0, x1: 9, y: 20), GroundLabelEdge(x0: 5, x1: 14, y: 20)]
        let score = GroundLabelScoring.score(document: doc, predictions: [
            CleanFloorLine(row: 20, xRange: 0...14), CleanFloorLine(row: 21, xRange: 0...14)])
        XCTAssertEqual(score.truePositiveLength, 15)
        XCTAssertEqual(score.falsePositiveLength, 15)
        XCTAssertEqual(score.falseNegativeLength, 0)
        XCTAssertEqual(score.precision, 0.5)
        XCTAssertEqual(score.recall, 1)
    }
    func testVerticalToleranceAndHorizontalEndpointsCountMissesAndExtras() {
        var doc = document(); doc.edges = [GroundLabelEdge(x0: 10, x1: 19, y: 20)]
        let score = GroundLabelScoring.score(document: doc, predictions: [CleanFloorLine(row: 23, xRange: 15...24)])
        XCTAssertEqual(score.truePositiveLength, 5); XCTAssertEqual(score.falsePositiveLength, 5)
        XCTAssertEqual(score.falseNegativeLength, 5)
        let outside = GroundLabelScoring.score(document: doc, predictions: [CleanFloorLine(row: 24, xRange: 10...19)])
        XCTAssertEqual(outside.truePositiveLength, 0); XCTAssertEqual(outside.falseNegativeLength, 10)
    }
    func testIgnoreRegionsRemoveBothTruthAndPredictions() {
        var doc = document(); doc.edges = [GroundLabelEdge(x0: 0, x1: 19, y: 20)]
        doc.ignoredRegions = [GroundLabelRect(CGRect(x: 5, y: 18, width: 10, height: 6))]
        let score = GroundLabelScoring.score(document: doc, predictions: [CleanFloorLine(row: 20, xRange: 0...19)])
        XCTAssertEqual(score.truePositiveLength, 10); XCTAssertEqual(score.falsePositiveLength, 0)
        XCTAssertEqual(score.falseNegativeLength, 0)
    }
    func testPartialFramesCannotClaimPrecisionOrFalsePositives() {
        var doc = document(reviewed: false); doc.edges = [GroundLabelEdge(x0: 0, x1: 9, y: 20)]
        let score = GroundLabelScoring.score(document: doc, predictions: [CleanFloorLine(row: 20, xRange: 0...19)])
        XCTAssertNil(score.falsePositiveLength); XCTAssertNil(score.precision); XCTAssertNil(score.f1)
        XCTAssertEqual(score.recall, 1)
    }
    func testFullyReviewedEmptyFrameIsNegativeEvidenceNotPerfectRecall() {
        let score = GroundLabelScoring.score(document: document(), predictions: [CleanFloorLine(row: 20, xRange: 0...9)])
        XCTAssertEqual(score.falsePositiveLength, 10); XCTAssertEqual(score.precision, 0)
        XCTAssertNil(score.recall); XCTAssertNil(score.f1)
    }
    func testEvidenceCoverageDoesNotCreditInferredBridges() {
        var doc = document(); doc.edges = [GroundLabelEdge(x0: 0, x1: 29, y: 20)]
        let lines = [CleanFloorLine(row: 20, xRange: 0...29, evidenceRanges: [0...9, 20...29])]
        XCTAssertEqual(GroundLabelScoring.score(document: doc, predictions: lines).truePositiveLength, 30)
        let evidence = GroundLabelScoring.score(document: doc, predictions: lines, evidenceOnly: true)
        XCTAssertEqual(evidence.truePositiveLength, 20); XCTAssertEqual(evidence.falseNegativeLength, 10)
    }
    func testDuplicateIdenticalPredictionsAreDeduplicated() {
        var doc = document(); doc.edges = [GroundLabelEdge(x0: 0, x1: 19, y: 20)]
        let score = GroundLabelScoring.score(document: doc, predictions: [
            CleanFloorLine(row: 20, xRange: 0...14), CleanFloorLine(row: 20, xRange: 5...19)])
        XCTAssertEqual(score.truePositiveLength, 20); XCTAssertEqual(score.falsePositiveLength, 0)
    }
    func testSplitValidationRejectsGroupAndDuplicateImageLeakage() throws {
        let a = document(); var b = document(); b.split = .check; b.imageSHA256 = String(repeating: "b", count: 64)
        XCTAssertThrowsError(try GroundLabelStore.validateSplits([a, b]))
        b.group = "route-B"; b.imageSHA256 = a.imageSHA256
        XCTAssertThrowsError(try GroundLabelStore.validateSplits([a, b]))
        b.imageSHA256 = String(repeating: "b", count: 64)
        XCTAssertNoThrow(try GroundLabelStore.validateSplits([a, b]))
    }
    func testCoordinatesStayExactAtZoomAndRejectLetterboxOutsideImage() {
        XCTAssertEqual(GroundLabelCoordinates.pixel(CGPoint(x: 150, y: 60), displayedSize: CGSize(width: 200, height: 120), width: 100, height: 60), CGPoint(x: 75, y: 30))
        XCTAssertNil(GroundLabelCoordinates.pixel(CGPoint(x: -1, y: 30), displayedSize: CGSize(width: 200, height: 120), width: 100, height: 60))
        XCTAssertNil(GroundLabelCoordinates.pixel(CGPoint(x: 200, y: 30), displayedSize: CGSize(width: 200, height: 120), width: 100, height: 60))
        let rect = CGRect(x: 10, y: 12, width: 20, height: 8)
        let top = GroundLabelRect.fromBottomLeft(rect, imageHeight: 60)
        XCTAssertEqual(top.y, 40); XCTAssertEqual(top.bottomLeft(imageHeight: 60), rect)
    }
    func testSaveReopenAndSourceHashMismatch() throws {
        let store = try temporaryStore()
        var doc = try store.add(image: image(), source: "synthetic", group: "train-A")
        doc.edges = [GroundLabelEdge(x0: 10, x1: 90, y: 30)]; doc.fullyReviewed = true
        try store.save(doc)
        XCTAssertEqual(try store.documents(), [doc]); XCTAssertEqual(try store.image(for: doc).width, 100)
        try Data("changed".utf8).write(to: store.root.appendingPathComponent(doc.imageFile))
        XCTAssertThrowsError(try store.image(for: doc))
    }
    func testInvalidGeometryAndTraversalRejected() {
        var doc = document(); doc.edges = [GroundLabelEdge(x0: 10, x1: 100, y: 30)]
        XCTAssertThrowsError(try doc.validate())
        doc.edges = []; doc.imageFile = "../source.png"
        XCTAssertThrowsError(try doc.validate())
    }
    func testNewCaptureInExistingCheckGroupInheritsSplit() throws {
        let store = try temporaryStore()
        var first = try store.add(image: image(), source: "first", group: "independent-check")
        first.split = .check; try store.save(first)
        let second = try store.add(image: image(background: 27), source: "second", group: "independent-check")
        XCTAssertEqual(second.split, .check)
    }
    func testHumanIgnoreRegionsNeverChangeDetectorInput() throws {
        let store = try temporaryStore()
        var doc = try store.add(image: image(), source: "synthetic", group: "train-A")
        let before = try GroundLabelEvaluator.prepare(store: store)[0].comparison.groundPixels
        doc.ignoredRegions = [GroundLabelRect(CGRect(x: 0, y: 0, width: 100, height: 60))]
        try store.save(doc)
        let after = try GroundLabelEvaluator.prepare(store: store)[0].comparison.groundPixels
        XCTAssertEqual(before, after)
    }
    func testUnreviewedLabelsExcludedFromTuningAndDraftDatasetNotGeneralization() throws {
        let store = try temporaryStore()
        var doc = try store.add(image: image(), source: "synthetic", group: "train-A")
        doc.edges = [GroundLabelEdge(x0: 10, x1: 90, y: 30)]; try store.save(doc)
        let report = try GroundLabelEvaluator.run(store: store, sweep: false)
        XCTAssertEqual(report.reviewedTrainFrames, 0); XCTAssertNil(report.baseline.train.recall)
        XCTAssertNil(report.baseline.train.precision)
        XCTAssertTrue(report.provisional)
        XCTAssertThrowsError(try GroundLabelEvaluator.run(store: store, sweep: true))
    }
    func testCheckLabelsCannotChooseParameters() throws {
        let store = try temporaryStore()
        var train = try store.add(image: image(), source: "synthetic train", group: "train-A")
        train.edges = [GroundLabelEdge(x0: 10, x1: 90, y: 30)]; train.fullyReviewed = true
        try store.save(train)
        var check = try store.add(image: image(background: 25), source: "synthetic check", group: "check-B")
        check.split = .check; check.fullyReviewed = true
        check.edges = [GroundLabelEdge(x0: 10, x1: 90, y: 30)]; try store.save(check)
        let first = try GroundLabelEvaluator.run(store: store, sweep: true)
        check.edges = [GroundLabelEdge(x0: 0, x1: 99, y: 5)]; try store.save(check)
        let second = try GroundLabelEvaluator.run(store: store, sweep: true)
        XCTAssertEqual(first.selected.parameters, second.selected.parameters)
        XCTAssertEqual(first.selected.train, second.selected.train)
        // The selected 40 px semantic defaults are an additional candidate
        // outside the original 384-point parameter grid.
        XCTAssertEqual(first.candidatesTested, 385)
        XCTAssertNotEqual(first.datasetDigest, second.datasetDigest)
    }
}
