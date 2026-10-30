import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class ControllerStencilPolicyTests: XCTestCase {
    private let inactive = ControllerStencilPolicy.noActiveController
    private let diagram = ControllerStencilPolicy.diagram
    private let remap = ControllerStencilPolicy.remapControls
    private var common: [String] {
        [LabelingClassIdentity.controller, LabelingClassIdentity.advancedSettings,
         LabelingClassIdentity.back, LabelingClassIdentity.selectDecoration, LabelingClassIdentity.selectDecoration]
    }

    func testInactiveControllerCompletesWithoutDiagramOrRemapping() {
        let complete = example(common + [inactive])
        XCTAssertFalse(LabelingContractEvaluator.evaluate(complete).contains(where: \.isBreach))
        XCTAssertTrue(StencilInitializationPolicy.acceptsMenuExample(complete))
        let steps = StencilInitializationPolicy.missingSteps(examples: [complete], languageIdentifier: "en", mode: .normal)
        XCTAssertFalse(steps.contains { $0.context == .controller || $0.context == .remapController })
        XCTAssertTrue(steps.contains { $0.context == .controllerAdvancedSettings })
        XCTAssertEqual(LabelingExampleContextInference.inferredContextIdentifier(
            current: "options", annotations: complete.manifest.annotations), "controller")

        XCTAssertTrue(LabelingContractEvaluator.evaluate(example(common)).contains(where: \.isBreach))
        XCTAssertFalse(StencilInitializationPolicy.acceptsMenuExample(example(Array(common.dropLast()) + [inactive])))
        XCTAssertFalse(StencilInitializationPolicy.acceptsMenuExample(example(common + [inactive, diagram])))
    }

    func testConnectedControllerRetainsItsExistingContractAndRemappingCampaign() {
        let connected = example(common + [diagram, remap])
        XCTAssertTrue(StencilInitializationPolicy.acceptsMenuExample(connected))
        XCTAssertFalse(StencilInitializationPolicy.acceptsMenuExample(example(common + [diagram])))
        XCTAssertTrue(StencilInitializationPolicy.contexts(for: .normal, examples: [connected]).contains(.remapController))
        XCTAssertFalse(StencilInitializationPolicy.missingSteps(examples: [connected], languageIdentifier: "en", mode: .normal)
            .contains { $0.context == .controller })
    }

    func testObjectPickerFollowsCapturedLayoutAndUndoRestoresChoice() {
        func available(_ labels: [String], hidesCompleted: Bool = true) -> [String] {
            StencilLabelSelectionPolicy.availableLabels(context: .controller, rectangles: labels.map(box),
                mode: .normal, hidesCompleted: hidesCompleted).map(\.id)
        }
        XCTAssertEqual(Array(available([]).prefix(2)), [LabelingClassIdentity.controller, inactive])
        XCTAssertTrue(available([]).contains(diagram))
        XCTAssertFalse(available([]).contains(remap))
        XCTAssertEqual(available([LabelingClassIdentity.controller, inactive]), [
            LabelingClassIdentity.advancedSettings, LabelingClassIdentity.back, LabelingClassIdentity.selectDecoration])
        XCTAssertTrue(available([diagram]).contains(remap))
        XCTAssertFalse(available([diagram]).contains(inactive))
        XCTAssertTrue(available([inactive], hidesCompleted: false).contains(diagram), "Modify can correct a mistaken layout label.")
        let negative = LabelingDraftRectangle(id: UUID(), classID: inactive,
            normalizedRect: CGRect(x: 0.2, y: 0.3, width: 0.3, height: 0.05), isNegative: true)
        XCTAssertTrue(StencilLabelSelectionPolicy.availableLabels(context: .controller, rectangles: [negative],
            mode: .normal, hidesCompleted: true).contains { $0.id == inactive })
    }

    func testCampaignUsesLatestCompatibleControllerReference() {
        let inactiveReference = example(common + [inactive], createdAt: Date(timeIntervalSince1970: 2))
        let oldConnected = example(common + [diagram, remap], createdAt: Date(timeIntervalSince1970: 1))
        let newConnected = example([diagram], createdAt: Date(timeIntervalSince1970: 3))
        XCTAssertFalse(StencilInitializationPolicy.contexts(for: .normal,
            examples: [oldConnected, inactiveReference]).contains(.remapController))
        XCTAssertTrue(StencilInitializationPolicy.contexts(for: .normal,
            examples: [inactiveReference, newConnected]).contains(.remapController))
        XCTAssertTrue(StencilInitializationPolicy.contexts(for: .normal,
            examples: [inactiveReference], languageIdentifier: "fr").contains(.remapController))
        XCTAssertTrue(StencilInitializationPolicy.contexts(for: .normal,
            examples: [inactiveReference], requiredSize: (1280, 720)).contains(.remapController))
    }

    @MainActor func testGuidanceAdvancesToVisibleAdvancedSettingsAndProgressUpdates() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "ControllerStencilPolicyTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360, bitsPerComponent: 8,
            bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        let image = try XCTUnwrap(bitmap.makeImage())
        let store = LabelingExampleStore(rootURL: root)
        let coordinator = StencilInitializationCoordinator(store: store, defaults: defaults)
        coordinator.language = .english
        coordinator.refresh(mode: .normal)
        XCTAssertEqual(coordinator.guidance(labelingContext: .controller, selectedClassIdentifier: inactive),
            "Draw a box around No Active Controller.")
        XCTAssertEqual(coordinator.guidance(labelingContext: .controller, selectedClassIdentifier: diagram),
            "Draw a box around Controller Diagram.")
        let saved = try store.saveDraft(image: image, context: .controller,
            rectangles: [LabelingClassIdentity.controller, inactive].map(box), languageIdentifier: "en")
        coordinator.refresh(mode: .normal)
        XCTAssertEqual(coordinator.guidance(labelingContext: .controller, selectedClassIdentifier: nil),
            "Draw a box around Advanced Settings.")
        XCTAssertEqual(coordinator.screenCount, 24)
        XCTAssertFalse(coordinator.isScreenComplete(.controller))
        _ = try store.updateDraft(saved, context: .controller, rectangles: (common + [inactive]).map(box))
        coordinator.refresh(mode: .normal)
        XCTAssertTrue(coordinator.isScreenComplete(.controller))
        XCTAssertEqual(coordinator.completedScreenCount, 1)
        XCTAssertEqual(coordinator.completedCount, common.count + 1)
        XCTAssertEqual(coordinator.guidance(labelingContext: .controller, selectedClassIdentifier: nil),
            coordinator.text("screenDone", ["screen": "Controller"]))
    }

    func testInactiveTextBuildsRecognitionAnchorButIsNotSelectable() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let definitions: [(String, CGRect)] = [
            (LabelingClassIdentity.controller, CGRect(x: 264, y: 39, width: 114, height: 15)),
            (inactive, CGRect(x: 248, y: 111, width: 146, height: 11)),
            (LabelingClassIdentity.advancedSettings, CGRect(x: 262, y: 296, width: 116, height: 14)),
            (LabelingClassIdentity.back, CGRect(x: 306, y: 324, width: 28, height: 12)),
            (LabelingClassIdentity.selectDecoration, CGRect(x: 239, y: 296, width: 13, height: 14)),
            (LabelingClassIdentity.selectDecoration, CGRect(x: 389, y: 296, width: 13, height: 14)),
        ]
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360, bitsPerComponent: 8,
            bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        bitmap.setFillColor(CGColor(gray: 0, alpha: 1))
        bitmap.fill(CGRect(x: 0, y: 0, width: 640, height: 360))
        bitmap.setFillColor(CGColor(gray: 1, alpha: 1))
        for (_, bounds) in definitions {
            for x in stride(from: bounds.minX + 1, to: bounds.maxX - 1, by: 4) {
                bitmap.fill(CGRect(x: x, y: 360 - bounds.maxY + 1, width: 2, height: bounds.height - 2))
            }
        }
        let store = LabelingExampleStore(rootURL: root)
        let saved = try store.saveDraft(image: XCTUnwrap(bitmap.makeImage()), context: .controller,
            rectangles: definitions.map { identifier, rect in
                .init(id: UUID(), classID: identifier, normalizedRect: CGRect(
                    x: rect.minX / 640, y: rect.minY / 360, width: rect.width / 640, height: rect.height / 360))
            }, languageIdentifier: "en")
        XCTAssertTrue(StencilInitializationPolicy.acceptsMenuExample(saved))
        for calibration in [nil, MenuStencilCalibration.loadBundled()] {
            let catalog = try MenuStencilCatalog(examplesRootURL: root, calibration: calibration,
                liveCapturesOverride: [], includedContexts: [.controller])
            let scene = try XCTUnwrap(catalog.scenes.first)
            let anchor = try XCTUnwrap(scene.anchors.first { $0.classIdentifier == inactive })
            XCTAssertEqual(anchor.bounds, definitions[1].1, "Use the drawn crop, not the full screenshot.")
            XCTAssertEqual(Set(scene.options.map(\.classIdentifier)),
                [LabelingClassIdentity.advancedSettings, LabelingClassIdentity.back])
        }
    }

    private func box(_ identifier: String) -> LabelingDraftRectangle {
        .init(id: UUID(), classID: identifier, normalizedRect: CGRect(x: 0.2, y: 0.3, width: 0.3, height: 0.05))
    }

    private func example(_ labels: [String], createdAt: Date = Date()) -> SavedLabelingExample {
        .init(directoryURL: URL(fileURLWithPath: "/unused"), manifest: .init(
            schemaVersion: LabelingExampleManifest.currentSchemaVersion, id: UUID(), imageIdentifier: UUID(),
            imageFilename: "image.png", imageWidth: 640, imageHeight: 360,
            captureGroupIdentifier: UUID(), contextIdentifier: "controller",
            annotations: labels.map { LabelingExampleAnnotation(box($0)) }, knownClassIdentifiers: nil,
            languageIdentifier: "en", completion: .draft, createdAt: createdAt))
    }
}
