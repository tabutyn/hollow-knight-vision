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
        )
        let savedContext = !LabelingContext.selectableCases.contains(persistedContext)
            ? savedClassIdentifier.flatMap(LabelingContext.containing(classIdentifier:)) ?? .game
            : persistedContext
        let context = savedClassIdentifier.flatMap { identifier -> LabelingContext? in
            if savedContext.labels.contains(where: { $0.id == identifier }) {
                return savedContext
            }
            return LabelingContext.containing(classIdentifier: identifier)
        } ?? savedContext
        let classIdentifier = savedClassIdentifier.flatMap { savedIdentifier in
            context.labels.first(where: { $0.id == savedIdentifier })?.id
        } ?? context.labels[0].id
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
