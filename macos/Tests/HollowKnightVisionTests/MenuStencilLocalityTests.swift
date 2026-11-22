import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class MenuStencilLocalityTests: XCTestCase {
    private let width = 160
    private let height = 120
    private let bounds = [CGRect(x: 24, y: 24, width: 8, height: 6),
                          CGRect(x: 70, y: 48, width: 8, height: 6)]
    private var values: [UInt8] {
        (0..<48).map { (index: Int) -> UInt8 in
            let mixed: Int = index * 71 + index * index * 13
            return UInt8(50 + mixed % 195)
        }
    }

    private func catalog(context: LabelingContext = .brightness) -> MenuStencilCatalog {
        let rgb = Data(values.flatMap { [$0, $0, $0] })
        let reference = ImageStencilReference(width: 8, height: 6, rgb: rgb)
        let variant = MenuStencilCatalog.Variant(kernel: ImageStencilKernel(
            kind: "locality-text", bounds: CGRect(x: 0, y: 0, width: 8, height: 6), rgb: rgb),
            reference: reference, languageIdentifier: "en")
        let identifiers = context == .quitGame || context == .quitToMenu
            ? [LabelingClassIdentity.yes, LabelingClassIdentity.no]
            : [LabelingClassIdentity.brightness, LabelingClassIdentity.back]
        let anchors = zip(identifiers, bounds).map {
            MenuStencilCatalog.Anchor(classIdentifier: $0.0, name: $0.0, bounds: $0.1,
                calibratedBounds: nil, variants: [variant], probe: variant)
        }
        let scene = MenuStencilCatalog.Scene(context: context, referenceWidth: width,
            referenceHeight: height, probe: anchors[0], anchors: anchors, options: [],
            selectorVariants: [:], selectorGeometry: nil)
        return MenuStencilCatalog(scenes: [scene], referenceWidth: width,
            referenceHeight: height, pixelBand: 0..<height)
    }

    private func frame(offset: CGPoint = .zero, probeOnly: Bool = false) throws -> CGImage {
        var rgba = [UInt8](repeating: 12, count: width * height * 4)
        for index in 0..<(width * height) { rgba[index * 4 + 3] = 255 }
        for rect in probeOnly ? [bounds[0]] : bounds {
            for y in 0..<6 {
                for x in 0..<8 {
                    let index = ((Int(rect.minY + offset.y) + y) * width
                        + Int(rect.minX + offset.x) + x) * 4
                    for channel in 0..<3 { rgba[index + channel] = values[y * 8 + x] }
                }
            }
        }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(rgba) as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 32, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }

    func testTrackingCannotWalkAwayFromReferenceThroughSmallFrameShifts() throws {
        for context in [LabelingContext.brightness, .clearSave, .quitGame, .quitToMenu] {
            for direction in [CGPoint(x: 2, y: 0), CGPoint(x: 0, y: 2)] {
                let tracker = MenuStencilTracker(catalog: catalog(context: context))
                XCTAssertTrue(tracker.observe(try frame(), timestamp: 0)?.isMatch == true)
                var result: MenuStencilResult?
                for step in 1...24 {
                    result = tracker.observe(try frame(offset: CGPoint(
                        x: direction.x * CGFloat(step), y: direction.y * CGFloat(step))),
                        timestamp: Double(step) / 10)
                }
                XCTAssertFalse(result?.isMatch == true,
                    "\(context): fixed menu text must not follow scenery away from its reference position")
                XCTAssertTrue(tracker.observe(try frame(offset: CGPoint(x: 2, y: 2)),
                    timestamp: 3)?.isMatch == true, "Small capture jitter remains supported")
            }
        }
    }

    func testRejectedContextDoesNotDrawAcceptedLookingStencils() throws {
        let result = try XCTUnwrap(MenuStencilTracker(catalog: catalog()).observe(
            try frame(probeOnly: true), timestamp: 0))
        XCTAssertFalse(result.isMatch, "One matching word cannot establish a menu context")
        XCTAssertNil(MenuStencilRenderer.overlay(result, width: width, height: height))
    }

    func testCurrentUserMenuReferencesWhenRequested() throws {
        guard ProcessInfo.processInfo.environment["HKV_CURRENT_MENU_LOCALITY_AUDIT"] == "1" else {
            throw XCTSkip("Opt-in audit of the user's menu references")
        }
        let store = LabelingExampleStore()
        let examples = try store.loadExamples()
        let contexts: [LabelingContext] = [.brightness, .clearSave, .selectProfile, .video]
        let catalog = try MenuStencilCatalog(examplesRootURL: store.rootURL,
            includedContexts: Set(contexts))
        for context in contexts {
            let example = try XCTUnwrap(StencilInitializationPolicy.representativeExample(
                examples: examples, context: context, languageIdentifier: "en", mode: .modded))
            let image = try store.loadImage(for: example)
            let tracker = MenuStencilTracker(catalog: catalog.containingOnly(context))
            for frame in 0..<12 {
                let result = tracker.observe(image, timestamp: Double(frame) / 10)
                XCTAssertTrue(result?.isMatch == true, "\(context) frame \(frame)")
                XCTAssertEqual(result?.context, context)
            }
        }
    }
}
