import AppKit
import SwiftUI

/// Text-only native segments with a fixed layout. SwiftUI's segmented Picker
/// hosts each label in a separate view graph; live frame updates otherwise
/// repeatedly measure those graphs, including while the editor is frozen.
struct StableSegmentedPicker<Selection: Hashable>: NSViewRepresentable {
    let label: String
    let choices: [Selection]
    let title: (Selection) -> String
    @Binding var selection: Selection

    func makeCoordinator() -> Coordinator { Coordinator(selection: $selection, choices: choices) }

    func makeNSView(context: Context) -> NSSegmentedControl {
        let control = NSSegmentedControl(
            labels: choices.map(title), trackingMode: .selectOne,
            target: context.coordinator, action: #selector(Coordinator.select(_:))
        )
        control.controlSize = .small
        control.segmentStyle = .rounded
        control.segmentDistribution = .fillEqually
        control.setAccessibilityLabel(label)
        control.selectedSegment = choices.firstIndex(of: selection) ?? -1
        return control
    }

    func updateNSView(_ control: NSSegmentedControl, context: Context) {
        context.coordinator.selection = $selection
        context.coordinator.choices = choices
        if control.segmentCount != choices.count { control.segmentCount = choices.count }
        for (index, choice) in choices.enumerated() {
            let text = title(choice)
            if control.label(forSegment: index) != text { control.setLabel(text, forSegment: index) }
        }
        let index = choices.firstIndex(of: selection) ?? -1
        if control.selectedSegment != index { control.selectedSegment = index }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSSegmentedControl, context: Context) -> CGSize? {
        CGSize(width: proposal.width ?? 300, height: 24)
    }

    final class Coordinator: NSObject {
        var selection: Binding<Selection>
        var choices: [Selection]

        init(selection: Binding<Selection>, choices: [Selection]) {
            self.selection = selection
            self.choices = choices
        }

        @objc func select(_ control: NSSegmentedControl) {
            guard choices.indices.contains(control.selectedSegment) else { return }
            selection.wrappedValue = choices[control.selectedSegment]
        }
    }
}
