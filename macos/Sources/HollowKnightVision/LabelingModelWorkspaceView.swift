import CoreGraphics
import ImageIO
import SwiftUI

enum LabelingModelTab: String, CaseIterable, Identifiable {
    case summary = "Summary"
    case objects = "Objects"
    case screenshots = "Screenshots"
    var id: String { rawValue }

    var shortcut: Character {
        switch self {
        case .summary: return "1"
        case .objects: return "2"
        case .screenshots: return "3"
        }
    }
}

struct LabelingModelWorkspaceSelection: Equatable {
    var tab = LabelingModelTab.summary
    var objectExampleIdentifier: UUID?
    var screenshotIdentifier: UUID?
    var screenshotGroup = "all"
    var contractExampleIdentifier: UUID?
    var summaryReviewClassIdentifier: String?
    var summaryFailureIdentifier: UUID?
}

private struct LabelingObjectReference: Identifiable {
    let example: SavedLabelingExample
    let annotation: LabelingExampleAnnotation
    var id: UUID { annotation.id }
}

struct LabelingModelWorkspaceView: View {
    let candidate: LabelingModelCandidate
    @Binding var context: LabelingContext
    @Binding var selectedClassIdentifier: String
    let activeVersionName: String
    let canTrain: Bool
    let hasUntrainedLabelChanges: Bool
    let untrainedLabeledImageCount: Int
    let isTraining: Bool
    let trainingProgress: String?
    let trainingHelp: String
    let onTrain: () -> Void
    let onStopTraining: () -> Void
    let onOpenExample: (UUID, LabelingEditorMode) -> Void
    @Binding var selection: LabelingModelWorkspaceSelection

    @State private var examples = [SavedLabelingExample]()
    @State private var examplesError: String?
    @State private var exemptions = [UUID: Set<String>]()
    @State private var referenceRevision = 0
    @State private var objectAtlas: LabelingObjectAtlasSnapshot?
    @State private var objectAtlasIsLoading = false
    @State private var selectedContractDeletionIdentifiers = Set<UUID>()
    @State private var pendingDeletionIdentifiers = Set<UUID>()

    private let exampleStore = LabelingExampleStore()
    private let exemptionStore = LabelingContractExemptionStore()
    private let referenceStore = LabelingObjectReferenceStore()

    var body: some View {
        VStack(spacing: 0) {
            controls
            Divider()
            tabContent
        }
        .background(Color.black)
        .onAppear(perform: reloadExamples)
        .onChange(of: selectedClassIdentifier) { _, _ in
            selectDefaultObjectReference()
        }
        .confirmationDialog(
            deletionConfirmationTitle,
            isPresented: Binding(
                get: { !pendingDeletionIdentifiers.isEmpty },
                set: { if !$0 { pendingDeletionIdentifiers.removeAll() } }
            ),
            titleVisibility: .visible
        ) {
            Button(deletionButtonTitle, role: .destructive) {
                let identifiers = pendingDeletionIdentifiers
                pendingDeletionIdentifiers.removeAll()
                deleteScreenshots(identifiers: identifiers)
            }
            Button("Cancel", role: .cancel) {
                pendingDeletionIdentifiers.removeAll()
            }
        } message: {
            Text("Images, labels, and exemptions will be removed. This cannot be undone.")
        }
    }

    private var deletionConfirmationTitle: String {
        pendingDeletionIdentifiers.count == 1
            ? "Permanently delete this screenshot?"
            : "Permanently delete \(pendingDeletionIdentifiers.count) screenshots?"
    }

    private var deletionButtonTitle: String {
        pendingDeletionIdentifiers.count == 1
            ? "Delete Screenshot"
            : "Delete \(pendingDeletionIdentifiers.count) Screenshots"
    }

    private var controls: some View {
        ZStack {
            HStack(spacing: 4) {
                if selection.tab != .screenshots,
                   let icon = objectAtlas?.icon(for: selectedClassIdentifier) {
                    LabelingToolbarObjectIcon(
                        image: icon,
                        classIdentifier: selectedClassIdentifier
                    )
                }
                Spacer(minLength: 8)
                HStack(spacing: 3) {
                    Text("+\(untrainedLabeledImageCount)")
                    Image(systemName: "photo")
                }
                .font(.caption.monospacedDigit())
                .foregroundStyle(untrainedLabeledImageCount > 0 ? .blue : .secondary)
                if isTraining {
                    Button("Stop Training", action: onStopTraining).tint(.red)
                    ProgressView().controlSize(.small)
                    Text(trainingProgress ?? "Preparing…")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                } else {
                    Button(action: onTrain) {
                        HStack(spacing: 5) {
                            if hasUntrainedLabelChanges {
                                Circle().fill(.blue).frame(width: 7, height: 7)
                            }
                            Text("Train")
                        }
                    }
                    .disabled(!canTrain || !breachedExamples.isEmpty)
                    .help(
                        breachedExamples.isEmpty
                            ? trainingHelp
                            : "Fix or exempt \(breachedExamples.count) contract-breaching screenshot(s) before training"
                    )
                }
                Text(activeVersionName)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }

            StableSegmentedPicker(
                label: "Model section",
                choices: LabelingModelTab.allCases,
                title: { $0.rawValue },
                selection: $selection.tab
            )
            .frame(width: 300)
            .overlay {
                ZStack {
                    ForEach(LabelingModelTab.allCases) { tab in
                        Button("Open \(tab.rawValue)") { selection.tab = tab }
                            .keyboardShortcut(KeyEquivalent(tab.shortcut), modifiers: [])
                    }
                }
                .frame(width: 1, height: 1)
                .opacity(0)
                .accessibilityHidden(true)
            }
        }
        .font(.caption)
        .buttonStyle(.bordered)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, minHeight: 46, alignment: .leading)
        .background(.regularMaterial)
    }

    @ViewBuilder private var tabContent: some View {
        if let examplesError {
            Text(examplesError).foregroundStyle(.red).padding()
        } else {
            switch selection.tab {
            case .summary: summary
            case .objects: objects
            case .screenshots: screenshots
            }
        }
    }

    private var summary: some View {
        HStack(alignment: .top, spacing: 10) {
            priorityPanel(title: "Least Registrations", values: leastRegistered) { value in
                Text("\(value.registrationCount)")
                    .foregroundStyle(value.registrationCount == 0 ? .orange : .secondary)
            }
            priorityPanel(title: "Most Errors", values: mostErrors) { value in
                Text("\(truePositiveCount(for: value.classIdentifier)) TP")
                    .foregroundStyle(.secondary)
            }
            contractBreachPanel.frame(width: 230)
            summaryPreview
        }
        .padding(14)
    }

    private func priorityPanel<Trailing: View>(
        title: String,
        values: [LabelingObjectPrioritySummary],
        @ViewBuilder trailing: @escaping (LabelingObjectPrioritySummary) -> Trailing
    ) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.headline)
            Divider()
            ScrollView {
                LazyVStack(spacing: 3) {
                    ForEach(values) { value in
                        Button { selectPriority(value.classIdentifier) } label: {
                            HStack(spacing: 8) {
                                Text(objectName(value.classIdentifier))
                                    .foregroundStyle(.primary)
                                    .lineLimit(1)
                                Spacer(minLength: 5)
                                trailing(value)
                            }
                            .font(.caption.monospacedDigit())
                            .padding(.horizontal, 8)
                            .padding(.vertical, 7)
                            .frame(maxWidth: .infinity)
                            .contentShape(Rectangle())
                            .background(
                                selectedClassIdentifier == value.classIdentifier
                                    ? Color.accentColor.opacity(0.18) : Color.clear,
                                in: RoundedRectangle(cornerRadius: 6)
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .padding(10)
        .frame(width: 230)
        .frame(maxHeight: .infinity, alignment: .topLeading)
        .modelPanelBackground()
    }

    private var contractBreachPanel: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text("Contract Breach").font(.headline)
                Spacer()
                Button(role: .destructive) {
                    pendingDeletionIdentifiers = selectedContractDeletionIdentifiers
                } label: {
                    Label(
                        "\(selectedContractDeletionIdentifiers.count)",
                        systemImage: "trash"
                    )
                }
                .disabled(selectedContractDeletionIdentifiers.isEmpty)
                .help("Delete selected screenshots")
            }
            Divider()
            ScrollView {
                LazyVStack(spacing: 4) {
                    if breachedExamples.isEmpty {
                        Text("No unexempted breaches.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .padding(.vertical, 8)
                    }
                    ForEach(breachedExamples, id: \.example.id) { item in
                        contractBreachRow(item, color: .red)
                    }
                    if !exemptedBreachExamples.isEmpty {
                        Divider().padding(.vertical, 5)
                        ForEach(exemptedBreachExamples, id: \.example.id) { item in
                            contractBreachRow(item, color: .blue)
                        }
                    }
                }
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modelPanelBackground()
    }

    private func contractBreachRow(
        _ item: (example: SavedLabelingExample, evaluations: [LabelingContractEvaluation]),
        color: Color
    ) -> some View {
        HStack(spacing: 6) {
            Button {
                if selectedContractDeletionIdentifiers.contains(item.example.id) {
                    selectedContractDeletionIdentifiers.remove(item.example.id)
                } else {
                    selectedContractDeletionIdentifiers.insert(item.example.id)
                }
            } label: {
                Image(systemName: selectedContractDeletionIdentifiers.contains(item.example.id)
                    ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(
                        selectedContractDeletionIdentifiers.contains(item.example.id)
                            ? Color.accentColor : Color.secondary
                    )
                    .frame(width: 18, height: 18)
            }
            .buttonStyle(.plain)
            .help("Select for deletion")

            Button {
                selection.summaryReviewClassIdentifier = nil
                selection.summaryFailureIdentifier = nil
                selection.contractExampleIdentifier = item.example.id
            } label: {
                HStack(spacing: 8) {
                    Text(contextName(item.example))
                        .foregroundStyle(.primary)
                        .lineLimit(1)
                    Spacer()
                    Text("\(item.evaluations.filter { !$0.isSatisfied }.count)")
                        .foregroundStyle(color)
                }
                .font(.caption.monospacedDigit())
                .padding(.horizontal, 6)
                .padding(.vertical, 7)
                .frame(maxWidth: .infinity)
                .contentShape(Rectangle())
                .background(
                    selection.contractExampleIdentifier == item.example.id
                        ? Color.accentColor.opacity(0.18) : Color.clear,
                    in: RoundedRectangle(cornerRadius: 6)
                )
            }
            .buttonStyle(.plain)
        }
    }

    @ViewBuilder private var summaryPreview: some View {
        if selection.summaryReviewClassIdentifier != nil {
            priorityFailurePreview
        } else {
            contractBreachPreview
        }
    }

    private var priorityFailurePreview: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text(selection.summaryReviewClassIdentifier.map(objectName) ?? "Errors")
                    .font(.headline)
                    .lineLimit(1)
                Spacer()
                Button { moveSummaryFailure(by: -1) } label: {
                    Image(systemName: "chevron.left")
                }
                .disabled(selectedFailureExamples.count < 2)
                Text(summaryFailurePositionText)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                Button { moveSummaryFailure(by: 1) } label: {
                    Image(systemName: "chevron.right")
                }
                .disabled(selectedFailureExamples.count < 2)
            }
            if let example = selectedFailureExample,
               let image = try? exampleStore.loadImage(for: example) {
                reviewedImage(
                    image: image,
                    example: example,
                    maximumHeight: 390,
                    predictions: selectedFailurePredictions
                ) {
                    openForRepair(example)
                }
            } else {
                Text("No failed screenshots for this object.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modelPanelBackground()
    }

    private var contractBreachPreview: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let example = selectedContractExample,
               let image = try? exampleStore.loadImage(for: example) {
                reviewedImage(image: image, example: example, maximumHeight: 330) {
                    openForRepair(example)
                }
                Divider()
                ScrollView {
                    contractTermsGrid {
                        ForEach(evaluations(for: example)) { evaluation in
                            contractTerm(evaluation, example: example)
                        }
                    }
                }
                Divider()
                Button("Delete Screenshot", role: .destructive) {
                    pendingDeletionIdentifiers = [example.id]
                }
                .frame(maxWidth: .infinity, alignment: .trailing)
            } else {
                Text("Select a contract breach.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modelPanelBackground()
    }

    private var objects: some View {
        HStack(spacing: 0) {
            objectCatalog.frame(width: 180)
            Divider()
            GeometryReader { proxy in
                HStack(spacing: 0) {
                    objectReferences.frame(width: proxy.size.width / 2)
                    Divider()
                    objectFullScreenshot.frame(width: proxy.size.width / 2)
                }
            }
        }
    }

    private var objectCatalog: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(LabelingContext.objectCatalogCases) { group in
                    Text(group.rawValue)
                        .font(.caption.weight(.bold))
                        .padding(.top, 9)
                        .padding(.horizontal, 8)
                    Divider()
                    ForEach(group.labels) { definition in
                        Button {
                            context = LabelingContext.contractSetCases.contains(group)
                                ? group
                                : LabelingContext.containing(classIdentifier: definition.id) ?? .game
                            selectedClassIdentifier = definition.id
                        } label: {
                            objectCatalogEntry(definition)
                            .padding(.leading, 7)
                            .padding(.trailing, 3)
                            .padding(.vertical, 3)
                            .frame(maxWidth: .infinity, minHeight: 34, alignment: .leading)
                            .contentShape(Rectangle())
                            .background(
                                LabelingClassIdentity.matches(
                                        selectedClassIdentifier,
                                        definition.id
                                    )
                                    ? Color.accentColor.opacity(0.18) : Color.clear
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .background(.white.opacity(0.035))
    }

    private var objectReferences: some View {
        VStack(alignment: .leading, spacing: 7) {
            Text(objectName(selectedClassIdentifier)).font(.headline).padding(.horizontal, 9)
            Divider()
            if selectedObjectReferences.isEmpty {
                Text("No positive reference boxes.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                GeometryReader { proxy in
                    ScrollView {
                        HStack(alignment: .top, spacing: 4) {
                            objectReferenceColumn(
                                parity: 0,
                                maximumImageHeight: proxy.size.height / 4
                            )
                            objectReferenceColumn(
                                parity: 1,
                                maximumImageHeight: proxy.size.height / 4
                            )
                        }
                        .padding(5)
                    }
                }
            }
        }
        .padding(.top, 10)
    }

    private func objectReferenceColumn(
        parity: Int,
        maximumImageHeight: CGFloat
    ) -> some View {
        LazyVStack(spacing: 4) {
            ForEach(Array(selectedObjectReferences.enumerated()).filter {
                $0.offset % 2 == parity
            }, id: \.element.id) { _, reference in
                Button {
                    selection.objectExampleIdentifier = reference.example.id
                } label: {
                    ZStack(alignment: .topTrailing) {
                        croppedReference(reference)
                            .frame(maxHeight: maximumImageHeight)
                        if effectiveReferenceIdentifier == reference.example.id {
                            Image(systemName: "star.fill")
                                .foregroundStyle(.yellow)
                                .padding(5)
                                .draggable(starDragValue)
                        }
                    }
                    .padding(3)
                    .frame(maxWidth: .infinity)
                    .background(
                        selection.objectExampleIdentifier == reference.example.id
                            ? Color.accentColor.opacity(0.18)
                            : Color.white.opacity(0.035),
                        in: RoundedRectangle(cornerRadius: 6)
                    )
                }
                .buttonStyle(.plain)
                .dropDestination(for: String.self) { values, _ in
                    guard values.contains(starDragValue) else { return false }
                                referenceStore.setExampleIdentifier(
                        reference.example.id,
                        for: selectedClassIdentifier
                    )
                                referenceRevision &+= 1
                                rebuildObjectAtlas(force: true)
                                return true
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .top)
    }

    private var objectFullScreenshot: some View {
        Group {
            if let example = selectedObjectExample,
               let image = try? exampleStore.loadImage(for: example) {
                reviewedImage(image: image, example: example) {
                    onOpenExample(example.id, .delete)
                }
            } else {
                Text("Select a reference screenshot.").foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.black)
    }

    private var screenshots: some View {
        HStack(spacing: 0) {
            screenshotGroups.frame(width: 190)
            Divider()
            screenshotList.frame(width: 270)
            Divider()
            screenshotContracts
        }
        .overlay {
            ZStack {
                Button("Previous Screenshot") { moveScreenshot(by: -1) }
                    .keyboardShortcut(.upArrow, modifiers: [])
                Button("Next Screenshot") { moveScreenshot(by: 1) }
                    .keyboardShortcut(.downArrow, modifiers: [])
            }
            .frame(width: 1, height: 1)
            .opacity(0)
            .accessibilityHidden(true)
        }
    }

    private var screenshotGroups: some View {
        ScrollView {
            LazyVStack(spacing: 3) {
                screenshotGroupRow(identifier: "all", title: "All")
                ForEach(LabelingContext.contractSetCases) { group in
                    screenshotGroupRow(
                        identifier: group.storageIdentifier,
                        title: group.rawValue
                    )
                }
                screenshotGroupRow(identifier: "other", title: "Other")
            }
            .padding(8)
        }
        .background(.white.opacity(0.035))
    }

    private func screenshotGroupRow(identifier: String, title: String) -> some View {
        Button {
            selection.screenshotGroup = identifier
            selection.screenshotIdentifier = filteredScreenshots.first?.id
        } label: {
            HStack {
                Text(title).lineLimit(1)
                Spacer()
                Text("\(screenshotCount(group: identifier))").foregroundStyle(.secondary)
            }
            .font(.caption.monospacedDigit())
            .padding(.horizontal, 8)
            .padding(.vertical, 7)
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
            .background(
                selection.screenshotGroup == identifier
                    ? Color.accentColor.opacity(0.18) : Color.clear,
                in: RoundedRectangle(cornerRadius: 6)
            )
        }
        .buttonStyle(.plain)
    }

    private var screenshotList: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 9) {
                    ForEach(filteredScreenshots) { example in
                        if let image = try? exampleStore.loadImage(for: example) {
                            Button { selection.screenshotIdentifier = example.id } label: {
                                Image(decorative: image, scale: 1)
                                    .resizable()
                                    .scaledToFit()
                                    .overlay(
                                        RoundedRectangle(cornerRadius: 5)
                                            .stroke(screenshotOutline(example), lineWidth: 3)
                                    )
                                    .padding(4)
                            }
                            .buttonStyle(.plain)
                            .id(example.id)
                        }
                    }
                }
                .padding(8)
            }
            .onAppear {
                scrollToSelectedScreenshot(using: proxy, animated: false)
            }
            .onChange(of: selection.screenshotIdentifier) { _, _ in
                scrollToSelectedScreenshot(using: proxy, animated: true)
            }
        }
    }

    private var screenshotContracts: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let example = selectedScreenshot,
               let image = try? exampleStore.loadImage(for: example) {
                reviewedImage(image: image, example: example, maximumHeight: 390) {
                    openForRepair(example)
                }
                Divider()
                Text("Contracts").font(.headline)
                ScrollView {
                    contractTermsGrid {
                        ForEach(evaluations(for: example)) { evaluation in
                            contractTerm(evaluation, example: example)
                        }
                    }
                }
                Divider()
                Button("Delete Screenshot", role: .destructive) {
                    pendingDeletionIdentifiers = [example.id]
                }
                .frame(maxWidth: .infinity, alignment: .trailing)
            } else {
                Text("Select a screenshot.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func reviewedImage(
        image: CGImage,
        example: SavedLabelingExample,
        maximumHeight: CGFloat = .infinity,
        predictions: [LabelingReviewBox] = [],
        onOpen: @escaping () -> Void
    ) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Button(action: onOpen) {
                annotatedImage(image: image, example: example, predictions: predictions)
                    .frame(maxWidth: .infinity, maxHeight: maximumHeight)
            }
            .buttonStyle(.plain)
            .help("Open screenshot in Label")
            if !example.manifest.annotations.isEmpty {
                LabelingObjectLegend(
                    classIdentifiers: example.manifest.annotations.map(\.classIdentifier),
                    objectAtlas: objectAtlas
                )
                .frame(width: 150, alignment: .topLeading)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func contractTermsGrid<Content: View>(
        @ViewBuilder content: () -> Content
    ) -> some View {
        LazyVGrid(
            columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 3),
            alignment: .leading,
            spacing: 3,
            content: content
        )
    }

    @ViewBuilder private func contractIcon(_ evaluation: LabelingContractEvaluation) -> some View {
        if evaluation.isExempt {
            Image(systemName: "doc.fill").foregroundStyle(.blue)
        } else if evaluation.isSatisfied {
            Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        } else {
            Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
        }
    }

    private func contractTerm(
        _ evaluation: LabelingContractEvaluation,
        example: SavedLabelingExample
    ) -> some View {
        Button { toggleExemption(evaluation.id, for: example) } label: {
            HStack(spacing: 7) {
                contractIcon(evaluation)
                Text(evaluation.title)
                    .font(.caption)
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                Spacer()
            }
            .padding(.vertical, 4)
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(evaluation.isSatisfied && !evaluation.isExempt)
    }

    private func annotatedImage(
        image: CGImage,
        example: SavedLabelingExample,
        predictions: [LabelingReviewBox] = []
    ) -> some View {
        GeometryReader { proxy in
            let layout = LabelingImageLayout(
                imageSize: CGSize(width: image.width, height: image.height),
                containerSize: proxy.size
            )
            ZStack(alignment: .topLeading) {
                Color.black
                Image(decorative: image, scale: 1)
                    .resizable()
                    .frame(width: layout.fittedRect.width, height: layout.fittedRect.height)
                    .position(x: layout.fittedRect.midX, y: layout.fittedRect.midY)
                ForEach(example.manifest.annotations) { annotation in
                    if let rect = layout.displayRect(for: annotation.normalizedRect) {
                        Rectangle()
                            .stroke(
                                annotation.isHardNegative
                                    ? Color.pink
                                    : LabelingVisualIdentity.color(
                                        for: annotation.classIdentifier
                                    ),
                                lineWidth: LabelingClassIdentity.matches(
                                    annotation.classIdentifier,
                                    selectedClassIdentifier
                                ) ? 3 : 2
                            )
                            .frame(width: rect.width, height: rect.height)
                            .position(x: rect.midX, y: rect.midY)
                    }
                }
                ForEach(Array(predictions.enumerated()), id: \.offset) { _, prediction in
                    if let rect = layout.displayRect(for: prediction.normalizedRect) {
                        Rectangle()
                            .stroke(
                                Color.yellow,
                                style: StrokeStyle(lineWidth: 2, dash: [6, 4])
                            )
                            .frame(width: rect.width, height: rect.height)
                            .position(x: rect.midX, y: rect.midY)
                    }
                }
            }
            .clipped()
        }
        .aspectRatio(CGFloat(image.width) / CGFloat(image.height), contentMode: .fit)
    }

    @ViewBuilder private func objectCatalogEntry(
        _ definition: LabelingClassDefinition
    ) -> some View {
        if let image = objectAtlas?.icon(for: definition.id) {
            Image(decorative: image, scale: 1)
                .resizable()
                .scaledToFit()
                .frame(maxWidth: .infinity, maxHeight: 44, alignment: .leading)
        } else if objectAtlasIsLoading {
            ProgressView().controlSize(.mini)
        } else {
            Text(definition.name)
                .font(.caption)
                .lineLimit(1)
        }
    }

    @ViewBuilder private func croppedReference(_ reference: LabelingObjectReference) -> some View {
        if let image = crop(reference) {
            Image(decorative: image, scale: 1).resizable().scaledToFit()
        } else {
            Color.black
        }
    }

    private func crop(_ reference: LabelingObjectReference) -> CGImage? {
        guard let image = try? exampleStore.loadImage(for: reference.example) else { return nil }
        let normalized = reference.annotation.normalizedRect.standardized
        let pixelRect = CGRect(
            x: normalized.minX * CGFloat(image.width),
            y: normalized.minY * CGFloat(image.height),
            width: normalized.width * CGFloat(image.width),
            height: normalized.height * CGFloat(image.height)
        ).integral.intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard !pixelRect.isEmpty else { return nil }
        return image.cropping(to: pixelRect)
    }

    private var priorities: [LabelingObjectPrioritySummary] {
        LabelingObjectPriorityAnalyzer.summaries(
            examples: examples,
            review: candidate.predictionReview,
            classIdentifiers: LabelingSharedModelPolicy.classIdentifiers,
            reviewedClassIdentifiers: Set(
                (candidate.summary.classIdentifiers ?? []).map(
                    LabelingClassIdentity.canonicalIdentifier
                )
            )
        )
    }

    private var leastRegistered: [LabelingObjectPrioritySummary] {
        LabelingObjectPriorityAnalyzer.leastRegistered(priorities)
    }

    private var mostErrors: [LabelingObjectPrioritySummary] {
        LabelingObjectPriorityAnalyzer.mostErrors(priorities)
    }

    private var reviewSummaries: [String: LabelingClassReviewSummary] {
        guard let review = candidate.predictionReview else { return [:] }
        return Dictionary(uniqueKeysWithValues: LabelingPredictionReviewAnalyzer.summaries(
            review: review,
            classIdentifiers: Array(LabelingSharedModelPolicy.classIdentifiers)
        ).map { ($0.classIdentifier, $0) })
    }

    private var selectedFailureExampleIdentifiers: [UUID] {
        guard let classIdentifier = selection.summaryReviewClassIdentifier,
              let review = candidate.predictionReview else { return [] }
        return LabelingFailureReviewNavigator.orderedExampleIdentifiers(
            for: classIdentifier,
            review: review
        ).filter { identifier in examples.contains { $0.id == identifier } }
    }

    private var selectedFailureExamples: [SavedLabelingExample] {
        let examplesByIdentifier = Dictionary(uniqueKeysWithValues: examples.map { ($0.id, $0) })
        return selectedFailureExampleIdentifiers.compactMap { examplesByIdentifier[$0] }
    }

    private var selectedFailureExample: SavedLabelingExample? {
        if let identifier = selection.summaryFailureIdentifier,
           let selected = selectedFailureExamples.first(where: { $0.id == identifier }) {
            return selected
        }
        return selectedFailureExamples.first
    }

    private var selectedFailurePredictions: [LabelingReviewBox] {
        guard let example = selectedFailureExample,
              let classIdentifier = selection.summaryReviewClassIdentifier,
              let review = candidate.predictionReview,
              let reviewed = review.examples.first(where: { $0.id == example.id })
        else { return [] }
        return reviewed.predictions.filter {
            LabelingClassIdentity.matches($0.label, classIdentifier)
                && $0.confidence >= review.metrics.confidenceThreshold
        }
    }

    private var summaryFailurePositionText: String {
        guard let selectedFailureExample,
              let index = selectedFailureExamples.firstIndex(where: {
                  $0.id == selectedFailureExample.id
              }) else { return "0 / 0" }
        return "\(index + 1) / \(selectedFailureExamples.count)"
    }

    private func truePositiveCount(for classIdentifier: String) -> Int {
        reviewSummaries[LabelingClassIdentity.canonicalIdentifier(classIdentifier)]?.truePositives ?? 0
    }

    private var breachedExamples: [(example: SavedLabelingExample, evaluations: [LabelingContractEvaluation])] {
        examples.compactMap { example in
            let values = evaluations(for: example)
            return values.contains(where: \.isBreach) ? (example, values) : nil
        }.sorted { $0.example.manifest.createdAt > $1.example.manifest.createdAt }
    }

    private var exemptedBreachExamples: [(example: SavedLabelingExample, evaluations: [LabelingContractEvaluation])] {
        examples.compactMap { example in
            let values = evaluations(for: example)
            let failed = values.filter { !$0.isSatisfied }
            guard !failed.isEmpty, failed.allSatisfy(\.isExempt) else { return nil }
            return (example, values)
        }.sorted { $0.example.manifest.createdAt > $1.example.manifest.createdAt }
    }

    private var selectedContractExample: SavedLabelingExample? {
        if let identifier = selection.contractExampleIdentifier,
           let selected = examples.first(where: { $0.id == identifier }) {
            return selected
        }
        return breachedExamples.first?.example ?? exemptedBreachExamples.first?.example
    }

    private var selectedObjectReferences: [LabelingObjectReference] {
        references(for: selectedClassIdentifier)
    }

    private func references(for classIdentifier: String) -> [LabelingObjectReference] {
        examples.flatMap { example in
            example.manifest.annotations.compactMap { annotation in
                guard !annotation.isHardNegative,
                      LabelingClassIdentity.matches(annotation.classIdentifier, classIdentifier)
                else { return nil }
                return LabelingObjectReference(example: example, annotation: annotation)
            }
        }.sorted { $0.example.manifest.createdAt < $1.example.manifest.createdAt }
    }

    private var selectedObjectExample: SavedLabelingExample? {
        let identifier = selection.objectExampleIdentifier ?? effectiveReferenceIdentifier
        return examples.first { $0.id == identifier }
    }

    private var effectiveReferenceIdentifier: UUID? {
        effectiveReferenceIdentifier(for: selectedClassIdentifier)
    }

    private func effectiveReferenceIdentifier(for classIdentifier: String) -> UUID? {
        let _ = referenceRevision
        let available = references(for: classIdentifier)
        if let stored = referenceStore.exampleIdentifier(for: classIdentifier),
           available.contains(where: { $0.example.id == stored }) {
            return stored
        }
        return available.first?.example.id
    }

    private var starDragValue: String {
        "reference-star:\(LabelingClassIdentity.canonicalIdentifier(selectedClassIdentifier))"
    }

    private var filteredScreenshots: [SavedLabelingExample] {
        examples.filter { example in
            switch selection.screenshotGroup {
            case "all": return true
            case "other": return screenshotGroupIdentifier(for: example) == "other"
            default: return screenshotGroupIdentifier(for: example) == selection.screenshotGroup
            }
        }.sorted { $0.manifest.createdAt < $1.manifest.createdAt }
    }

    private var selectedScreenshot: SavedLabelingExample? {
        filteredScreenshots.first { $0.id == selection.screenshotIdentifier }
            ?? filteredScreenshots.first
    }

    private func moveScreenshot(by offset: Int) {
        guard !filteredScreenshots.isEmpty else { return }
        let currentIndex = filteredScreenshots.firstIndex {
            $0.id == selection.screenshotIdentifier
        } ?? 0
        let nextIndex = min(max(currentIndex + offset, 0), filteredScreenshots.count - 1)
        selection.screenshotIdentifier = filteredScreenshots[nextIndex].id
    }

    private func scrollToSelectedScreenshot(
        using proxy: ScrollViewProxy,
        animated: Bool
    ) {
        guard let identifier = selection.screenshotIdentifier else { return }
        if animated {
            withAnimation(.easeInOut(duration: 0.16)) {
                proxy.scrollTo(identifier, anchor: .center)
            }
        } else {
            proxy.scrollTo(identifier, anchor: .center)
        }
    }

    private func screenshotCount(group: String) -> Int {
        switch group {
        case "all": return examples.count
        default: return examples.filter { screenshotGroupIdentifier(for: $0) == group }.count
        }
    }

    private func screenshotGroupIdentifier(for example: SavedLabelingExample) -> String {
        guard !example.manifest.annotations.isEmpty,
              LabelingContext(storageIdentifier: example.manifest.contextIdentifier) != nil else {
            return "other"
        }
        return example.manifest.contextIdentifier
    }

    private func evaluations(for example: SavedLabelingExample) -> [LabelingContractEvaluation] {
        LabelingContractEvaluator.evaluate(
            example,
            exemptions: exemptions[example.id, default: []]
        )
    }

    private func screenshotOutline(_ example: SavedLabelingExample) -> Color {
        let values = evaluations(for: example)
        if values.contains(where: \.isBreach) { return .red }
        if !exemptions[example.id, default: []].isEmpty { return .blue }
        return selection.screenshotIdentifier == example.id ? .cyan : .clear
    }

    private func toggleExemption(_ ruleIdentifier: String, for example: SavedLabelingExample) {
        var values = exemptions[example.id, default: []]
        if values.contains(ruleIdentifier) {
            values.remove(ruleIdentifier)
        } else {
            values.insert(ruleIdentifier)
        }
        do {
            try exemptionStore.save(values, for: example)
            exemptions[example.id] = values
        } catch {
            examplesError = "Exemption failed: \(error.localizedDescription)"
        }
    }

    private func deleteScreenshots(identifiers: Set<UUID>) {
        let targets = examples.filter { identifiers.contains($0.id) }
        guard !targets.isEmpty else { return }
        var deleted = Set<UUID>()
        var firstFailure: Error?
        for example in targets {
            do {
                try exampleStore.delete(example)
                deleted.insert(example.id)
            } catch {
                if firstFailure == nil { firstFailure = error }
            }
        }
        examples.removeAll { deleted.contains($0.id) }
        for identifier in deleted {
            exemptions.removeValue(forKey: identifier)
        }
        selectedContractDeletionIdentifiers.subtract(deleted)
        reconcileSelection()
        selectDefaultObjectReference()
        rebuildObjectAtlas(force: true)
        if let firstFailure {
            examplesError = "Delete screenshot failed: \(firstFailure.localizedDescription)"
        }
    }

    private func select(_ classIdentifier: String) {
        if !context.labels.contains(where: { $0.id == classIdentifier }),
           let group = LabelingContext.containing(classIdentifier: classIdentifier) {
            context = group
        }
        selectedClassIdentifier = classIdentifier
    }

    private func selectPriority(_ classIdentifier: String) {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        select(canonical)
        selection.summaryReviewClassIdentifier = canonical
        selection.summaryFailureIdentifier = selectedFailureExampleIdentifiers.first
    }

    private func moveSummaryFailure(by offset: Int) {
        selection.summaryFailureIdentifier = LabelingFailureReviewNavigator.movedIdentifier(
            from: selectedFailureExample?.id,
            by: offset,
            in: selectedFailureExampleIdentifiers
        )
    }

    private func selectDefaultObjectReference() {
        selection.objectExampleIdentifier = effectiveReferenceIdentifier
    }

    private func openForRepair(_ example: SavedLabelingExample) {
        if let group = LabelingContext(storageIdentifier: example.manifest.contextIdentifier) {
            context = group
            if !group.labels.contains(where: { $0.id == selectedClassIdentifier }),
               let first = group.labels.first {
                selectedClassIdentifier = first.id
            }
        }
        onOpenExample(example.id, .add)
    }

    private func objectName(_ classIdentifier: String) -> String {
        LabelingContext.allCases.lazy.flatMap(\.labels).first {
            LabelingClassIdentity.matches($0.id, classIdentifier)
        }?.name ?? classIdentifier
    }

    private func contextName(_ example: SavedLabelingExample) -> String {
        guard !example.manifest.annotations.isEmpty else { return "Other" }
        return LabelingContext(storageIdentifier: example.manifest.contextIdentifier)?.rawValue
            ?? example.manifest.contextIdentifier
    }

    private func reloadExamples() {
        do {
            examples = try exampleStore.loadExamples()
            exemptions = Dictionary(uniqueKeysWithValues: examples.map {
                ($0.id, exemptionStore.load(for: $0))
            })
            examplesError = nil
            reconcileSelection()
            rebuildObjectAtlas()
        } catch {
            examples = []
            examplesError = "Examples failed: \(error.localizedDescription)"
        }
    }

    private func reconcileSelection() {
        selectedContractDeletionIdentifiers.formIntersection(
            Set(examples.map(\.id))
        )
        if selectedObjectExample == nil {
            selectDefaultObjectReference()
        }
        if !filteredScreenshots.contains(where: { $0.id == selection.screenshotIdentifier }) {
            selection.screenshotIdentifier = filteredScreenshots.first?.id
        }
        let contractExamples = breachedExamples.map(\.example)
            + exemptedBreachExamples.map(\.example)
        if !contractExamples.contains(where: { $0.id == selection.contractExampleIdentifier }) {
            selection.contractExampleIdentifier = contractExamples.first?.id
        }
        if selection.summaryReviewClassIdentifier != nil,
           !selectedFailureExampleIdentifiers.contains(selection.summaryFailureIdentifier ?? UUID()) {
            selection.summaryFailureIdentifier = selectedFailureExampleIdentifiers.first
        }
    }

    private func rebuildObjectAtlas(force: Bool = false) {
        let identifiers = Array(LabelingSharedModelPolicy.classIdentifiers).sorted()
        let input = LabelingObjectAtlasBuildInput(
            examples: examples,
            classIdentifiers: identifiers,
            preferredExampleIdentifiers: referenceStore.exampleIdentifiers(for: identifiers)
        )
        objectAtlasIsLoading = true
        Task {
            let result = await Task.detached(priority: .utility) {
                Result {
                    try LabelingObjectAtlasStore().loadOrBuild(input: input, force: force)
                }
            }.value
            objectAtlasIsLoading = false
            switch result {
            case .success(let snapshot): objectAtlas = snapshot
            case .failure(let error):
                objectAtlas = nil
                examplesError = "Object atlas failed: \(error.localizedDescription)"
            }
        }
    }
}

private extension View {
    func modelPanelBackground() -> some View {
        background(.white.opacity(0.055), in: RoundedRectangle(cornerRadius: 8))
    }
}
