import Foundation

/// Complete labeling vocabulary. Menu labels remain available to build and
/// inspect stencils even though they are no longer object-model classes.
enum LabelingCatalogPolicy {
    static var classIdentifiers: Set<String> {
        Set(LabelingContext.allCases.flatMap(\.labels).map(\.id))
            .union(UserObjectCatalogStore.classIdentifiers)
    }
}

/// Fixed screen-space HUD art is recognized by `HUDStencilTracker`, not by
/// the learned object detector. Keep these identifiers in the labeling
/// catalog so the source examples and stencil authoring workflow remain
/// available.
enum LabelingHUDStencilPolicy {
    static let classIdentifiers: Set<String> = [
        "game.health",
        "game.mana",
        "game.geo",
    ]
}

enum LabelingSharedModelPolicy {
    static let modelIdentifier = LabelingModelIdentity.sharedObjectModel
    static let modelName = LabelingModelIdentity.sharedObjectModelName

    static var classIdentifiers: Set<String> {
        Set(LabelingContext.game.labels.map(\.id))
            .subtracting(LabelingHUDStencilPolicy.classIdentifiers)
            .union(UserObjectCatalogStore.classIdentifiers)
    }
}
