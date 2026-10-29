import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingSelectionPreferencesTests: XCTestCase {
    func testLastValidContextAndObjectSurviveReload() throws {
        let suiteName = "hkv-labeling-selection-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let preferences = LabelingSelectionPreferences(defaults: defaults)

        XCTAssertEqual(preferences.load().context, .game)
        preferences.saveContext(.mainTitle)
        preferences.saveClassIdentifier("main-title.start-game")

        let restored = LabelingSelectionPreferences(defaults: defaults).load()
        XCTAssertEqual(restored.context, .mainTitle)
        XCTAssertEqual(restored.classIdentifier, "main-title.start-game")
    }

    func testLegacySelectDecorationStaysInSavedSetWhenSetContainsIt() throws {
        let suiteName = "hkv-labeling-selection-shared-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        defaults.set("main-title", forKey: LabelingSelectionPreferences.contextKey)
        defaults.set(
            "main-title.select-decoration",
            forKey: LabelingSelectionPreferences.classIdentifierKey
        )

        let restored = LabelingSelectionPreferences(defaults: defaults).load()

        XCTAssertEqual(restored.context, .mainTitle)
        XCTAssertEqual(restored.classIdentifier, LabelingClassIdentity.selectDecoration)
    }

    func testRetiredGameplayHUDSelectionFallsBackToTrainableObject() throws {
        let suite = "hkv-hud-selection-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let preferences = LabelingSelectionPreferences(defaults: defaults)
        preferences.saveContext(.game)
        preferences.saveClassIdentifier("game.health")
        XCTAssertEqual(preferences.load().context, .game)
        XCTAssertEqual(preferences.load().classIdentifier, "game.playable-knight")
        XCTAssertEqual(LabelingFrameOpeningPolicy.classIdentifierForOpening(context: .game,
            annotationClassIdentifiers: ["game.health"], preferredClassIdentifier: "game.mana"), "game.playable-knight")
    }

    func testObjectFromAnotherSetMovesToASetContainingIt() throws {
        let suiteName = "hkv-labeling-selection-invalid-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let preferences = LabelingSelectionPreferences(defaults: defaults)
        preferences.saveContext(.mainTitle)
        preferences.saveClassIdentifier("game.playable-knight")

        let restored = preferences.load()
        XCTAssertEqual(restored.context, .game)
        XCTAssertEqual(restored.classIdentifier, "game.playable-knight")
    }
}
