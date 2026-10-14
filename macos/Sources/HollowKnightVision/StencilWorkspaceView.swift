import SwiftUI

/// Menu stencils are authored once on a single screenshot; learned gameplay
/// objects deliberately remain repeatable training examples.
enum StencilLabelSelectionPolicy {
    static func availableLabels(
        context: LabelingContext, custom: [LabelingClassDefinition] = [],
        rectangles: [LabelingDraftRectangle], mode: HollowKnightRuntimeMode,
        hidesCompleted: Bool, includesDeferredLabels: Bool = false
    ) -> [LabelingClassDefinition] {
        var seen = Set<String>()
        return (context.labels + custom).filter { definition in
            let identifier = LabelingClassIdentity.canonicalIdentifier(definition.id)
            guard seen.insert(identifier).inserted else { return false }
            if mode == .normal && identifier == LabelingClassIdentity.mods { return false }
            // Clear Save is absent on fresh profiles and never blocks the first campaign.
            if hidesCompleted && !includesDeferredLabels && identifier == "select-profile.clear-save" { return false }
            guard hidesCompleted, context != .game else { return true }
            return !LabelingContractEvaluator.shouldAdvance(
                afterAdding: identifier, in: context, rectangles: rectangles
            )
        }.map { definition in
            guard definition.id == LabelingClassIdentity.selectDecoration else { return definition }
            let count = rectangles.filter {
                !$0.isNegative && LabelingClassIdentity.canonicalIdentifier($0.classID) == definition.id
            }.count
            return .init(id: definition.id, name: count == 0 ? "Left Select Decoration" : "Right Select Decoration")
        }
    }
}

struct StencilWorkspaceView: View {
    @ObservedObject var coordinator: StencilInitializationCoordinator
    let onOpen: (SavedLabelingExample) -> Void
    let onSelect: (LabelingContext) -> Void
    let onReset: () -> Void
    let onDelete: (SavedLabelingExample) -> Void
    @State private var selection = LabelingContext.mainTitle
    private let store = LabelingExampleStore()

    var body: some View {
        HStack(spacing: 0) {
            VStack(spacing: 0) {
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(1...3, id: \.self) { phase in
                            Text(coordinator.text("phase\(phase)"))
                                .font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 10)
                            ForEach(coordinator.contexts.filter { StencilInitializationPolicy.phase(for: $0) == phase }) { context in
                            let complete = coordinator.isScreenComplete(context)
                            Button { selection = context } label: {
                                HStack {
                                    Image(systemName: complete ? "checkmark.circle.fill" : "circle")
                                        .foregroundStyle(complete ? Color.green : .secondary)
                                    Text(coordinator.screenName(context))
                                    Spacer()
                                }
                                .padding(9)
                                .background(selection == context ? Color.blue.opacity(0.2) : .clear)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            }
                        }
                    }
                    .padding(8)
                }
                Divider()
                Button(coordinator.text("reset"), role: .destructive, action: onReset)
                    .foregroundStyle(.red)
                    .buttonStyle(.bordered)
                    .padding(12)
            }
            .frame(width: 240)
            Divider()
            VStack(alignment: .leading, spacing: 12) {
                if let issue = coordinator.issue {
                    Text(issue).foregroundStyle(.red)
                } else if let example = coordinator.example(for: selection),
                          let image = try? store.loadImage(for: example) {
                    Image(decorative: image, scale: 1)
                        .resizable().aspectRatio(contentMode: .fit)
                        .overlay {
                            GeometryReader { proxy in
                                ForEach(example.manifest.annotations) { annotation in
                                    let rect = annotation.normalizedRect
                                    Rectangle().stroke(.blue, lineWidth: 1)
                                        .frame(width: rect.width * proxy.size.width,
                                               height: rect.height * proxy.size.height)
                                        .overlay(alignment: .topLeading) {
                                            let name = annotation.classIdentifier == LabelingClassIdentity.selectDecoration
                                                ? (rect.midX < 0.5 ? "Left Select Decoration" : "Right Select Decoration")
                                                : selection.labels.first { $0.id == annotation.classIdentifier }?.name ?? annotation.classIdentifier
                                            Text(coordinator.text(name)).font(.system(size: 11, weight: .semibold))
                                                .foregroundStyle(.white).padding(.horizontal, 3).padding(.vertical, 1)
                                                .background(.blue).fixedSize()
                                                .offset(y: rect.minY * proxy.size.height >= 20 ? -20 : rect.height * proxy.size.height + 2)
                                        }
                                        .position(x: rect.midX * proxy.size.width,
                                                  y: rect.midY * proxy.size.height)
                                }
                            }
                        }
                    HStack {
                        Button(coordinator.text("edit")) { onOpen(example) }
                        Button(coordinator.text("deleteScreenshot"), role: .destructive) { onDelete(example) }
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(16)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .onAppear {
            selection = coordinator.currentStep?.context ?? .mainTitle
            onSelect(selection)
        }
        .onChange(of: selection) { _, context in onSelect(context) }
        .onChange(of: coordinator.contexts) { _, contexts in
            if !contexts.contains(selection) { selection = contexts.first ?? .mainTitle }
        }
    }
}
