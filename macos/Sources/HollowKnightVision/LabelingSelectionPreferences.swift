import Foundation

struct LabelingSelectionPreferences {
    static let contextKey = "labeling.last-context"
    static let classIdentifierKey = "labeling.last-class-identifier"

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> (context: LabelingContext, classIdentifier: String) {
        let persistedContext = defaults.string(forKey: Self.contextKey)
            .flatMap(LabelingContext.init(storageIdentifier:)) ?? .game
        let savedClassIdentifier = defaults.string(forKey: Self.classIdentifierKey).map(
            LabelingClassIdentity.canonicalIdentifier
        ).flatMap { LabelingHUDStencilPolicy.isLabelable($0, in: persistedContext) ? $0 : nil }
        func labels(_ context: LabelingContext) -> [LabelingClassDefinition] {
            (context.labels + UserObjectCatalogStore.definitions(for: context)).filter {
                LabelingHUDStencilPolicy.isLabelable($0.id, in: context)
            }
        }
        func containing(_ identifier: String) -> LabelingContext? {
            LabelingContext.selectableCases.first { context in labels(context).contains { $0.id == identifier } }
        }
        let savedContext = !LabelingContext.selectableCases.contains(persistedContext)
            ? savedClassIdentifier.flatMap(containing) ?? .game
            : persistedContext
        let context = savedClassIdentifier.flatMap { identifier -> LabelingContext? in
            if labels(savedContext).contains(where: { $0.id == identifier }) {
                return savedContext
            }
            return containing(identifier)
        } ?? savedContext
        let classIdentifier = savedClassIdentifier.flatMap { savedIdentifier in
            labels(context).first(where: { $0.id == savedIdentifier })?.id
        } ?? labels(context).first?.id ?? ""
        return (context, classIdentifier)
    }

    func saveContext(_ context: LabelingContext) {
        defaults.set(context.storageIdentifier, forKey: Self.contextKey)
    }

    func saveClassIdentifier(_ classIdentifier: String) {
        defaults.set(
            LabelingClassIdentity.canonicalIdentifier(classIdentifier),
            forKey: Self.classIdentifierKey
        )
    }
}

enum LabelingModelIdentity {
    static let sharedObjectModel = "shared-object-model"
    static let sharedObjectModelName = "Shared Object Model"
}
