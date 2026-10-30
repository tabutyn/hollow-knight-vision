import Foundation

/// The game hides its diagram and remapping entry until a controller is active.
/// Use the human's positive labels, not OS device discovery: a connected device
/// need not be active in Hollow Knight, and saved screenshots must stay stable.
enum ControllerStencilPolicy {
    static let noActiveController = "controller.no-active-controller"
    static let diagram = "controller.controller-diagram"
    static let remapControls = "controller.remap-controls"
    static let connectedIdentifiers: Set<String> = [diagram, remapControls]

    static func requiredLabels(
        in context: LabelingContext, positiveIdentifiers: Set<String>
    ) -> [LabelingClassDefinition] {
        guard context == .controller else { return context.labels }
        let inactive = positiveIdentifiers.contains(noActiveController)
            || positiveIdentifiers.isDisjoint(with: connectedIdentifiers)
        return context.labels.filter {
            inactive ? !connectedIdentifiers.contains($0.id) : $0.id != noActiveController
        }
    }

    static func isAvailable(_ identifier: String, positiveIdentifiers: Set<String>) -> Bool {
        if positiveIdentifiers.contains(noActiveController) {
            return !connectedIdentifiers.contains(identifier)
        }
        if !positiveIdentifiers.isDisjoint(with: connectedIdentifiers) {
            return identifier != noActiveController
        }
        // Both layouts can be chosen before drawing. Remap becomes relevant
        // only after labeling the visible diagram.
        return identifier != remapControls
    }
}
