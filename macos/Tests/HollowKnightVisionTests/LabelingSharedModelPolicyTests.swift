import XCTest
@testable import HollowKnightVision

final class LabelingSharedModelPolicyTests: XCTestCase {
    func testSharedModelOwnsDynamicGameplayClassesOnly() {
        XCTAssertEqual(
            LabelingSharedModelPolicy.modelIdentifier,
            LabelingModelIdentity.sharedObjectModel
        )
        XCTAssertEqual(
            LabelingSharedModelPolicy.classIdentifiers,
            Set(LabelingContext.game.labels.map(\.id))
                .subtracting(LabelingHUDStencilPolicy.classIdentifiers)
                .union(UserObjectCatalogStore.classIdentifiers)
        )
        XCTAssertFalse(LabelingSharedModelPolicy.classIdentifiers.contains("game.mana"))
        XCTAssertFalse(LabelingSharedModelPolicy.classIdentifiers.contains("game.health"))
        XCTAssertFalse(LabelingSharedModelPolicy.classIdentifiers.contains("game.geo"))
        XCTAssertTrue(LabelingSharedModelPolicy.classIdentifiers.contains("game.playable-knight"))
        XCTAssertTrue(LabelingSharedModelPolicy.classIdentifiers.contains("enemies.crawlid"))
        XCTAssertTrue(LabelingSharedModelPolicy.classIdentifiers.contains("world.sign"))
        XCTAssertFalse(LabelingSharedModelPolicy.classIdentifiers.contains("main-title.start-game"))
        XCTAssertFalse(LabelingSharedModelPolicy.classIdentifiers.contains("inventory.inventory"))
    }

    func testLabelingCatalogKeepsMenuClassesForStencils() {
        XCTAssertEqual(
            LabelingCatalogPolicy.classIdentifiers,
            Set(LabelingContext.allCases.flatMap(\.labels).map(\.id))
                .union(UserObjectCatalogStore.classIdentifiers)
        )
        XCTAssertTrue(LabelingCatalogPolicy.classIdentifiers.contains("main-title.start-game"))
        XCTAssertTrue(LabelingCatalogPolicy.classIdentifiers.contains("inventory.inventory"))
        XCTAssertTrue(LabelingCatalogPolicy.classIdentifiers.isSuperset(
            of: LabelingHUDStencilPolicy.classIdentifiers
        ))
    }
}
