import CoreGraphics
import Foundation
import SwiftUI

enum LabelingFrameOpeningPolicy {
    static func classIdentifierForOpening(
        context: LabelingContext,
        annotationClassIdentifiers: [String],
        preferredClassIdentifier: String? = nil
    ) -> String {
        let validClassIdentifiers = Set(context.labels.map(\.id))
        if let preferredClassIdentifier,
           validClassIdentifiers.contains(preferredClassIdentifier) {
            return preferredClassIdentifier
        }
        if let annotatedClassIdentifier = annotationClassIdentifiers.first(where: {
            validClassIdentifiers.contains($0)
        }) {
            return annotatedClassIdentifier
        }
        return context.labels[0].id
    }

    static func stateForOpening(
        _ manifest: LabelingExampleManifest,
        preferredClassIdentifier: String? = nil
    ) -> LabelingFrameOpenState? {
        guard let savedContext = LabelingContext(
            storageIdentifier: manifest.contextIdentifier
        ) else { return nil }
        let normalizedSavedContext: LabelingContext
        if LabelingContext.selectableCases.contains(savedContext) {
            normalizedSavedContext = savedContext
        } else {
            let anchor = preferredClassIdentifier ?? manifest.annotations.first?.classIdentifier
            normalizedSavedContext = anchor.flatMap {
                LabelingContext.containing(classIdentifier: $0)
            } ?? .game
        }
        let context: LabelingContext
        if let preferredClassIdentifier,
           normalizedSavedContext.labels.contains(where: { $0.id == preferredClassIdentifier }) {
            context = normalizedSavedContext
        } else {
            context = preferredClassIdentifier.flatMap {
                LabelingContext.containing(classIdentifier: $0)
            } ?? normalizedSavedContext
        }
        return LabelingFrameOpenState(
            context: context,
            selectedClassIdentifier: classIdentifierForOpening(
                context: context,
                annotationClassIdentifiers: manifest.annotations.map(\.classIdentifier),
                preferredClassIdentifier: preferredClassIdentifier
            ),
            draft: LabelingDraftState(
                rectangles: manifest.annotations.map {
                    LabelingDraftRectangle(
                        id: $0.id,
                        classID: $0.classIdentifier,
                        normalizedRect: $0.normalizedRect,
                        isNegative: $0.isHardNegative
                    )
                }
            )
        )
    }
}

struct LabelingFrameOpenState: Equatable {
    let context: LabelingContext
    let selectedClassIdentifier: String
    let draft: LabelingDraftState
}

enum RecentCapturedFrameLabelingPolicy {
    static func stateForOpening(
        _ frame: RecentCapturedFrame,
        fallbackContext: LabelingContext,
        fallbackClassIdentifier: String
    ) -> LabelingFrameOpenState {
        let observedIdentifiers = Set(frame.detections.map {
            LabelingClassIdentity.canonicalIdentifier($0.classIdentifier)
        })
        let context = recognizedScreenContext(
            observedIdentifiers: observedIdentifiers,
            fallback: fallbackContext
        )
        let allKnownClassIdentifiers = LabelingCatalogPolicy.classIdentifiers
        let rectangles = frame.detections.compactMap { detection -> LabelingDraftRectangle? in
            let classIdentifier = LabelingClassIdentity.canonicalIdentifier(
                detection.classIdentifier
            )
            guard allKnownClassIdentifiers.contains(classIdentifier) else { return nil }
            return LabelingDraftRectangle(
                id: UUID(),
                classID: classIdentifier,
                normalizedRect: detection.normalizedRect
            )
        }
        let validClassIdentifiers = Set(context.labels.map(\.id))
        let canonicalFallbackClassIdentifier = LabelingClassIdentity.canonicalIdentifier(
            fallbackClassIdentifier
        )
        let selectedClassIdentifier = rectangles.first(where: {
            validClassIdentifiers.contains($0.classID)
        })?.classID
            ?? (validClassIdentifiers.contains(canonicalFallbackClassIdentifier)
                ? canonicalFallbackClassIdentifier
                : context.labels[0].id)
        return LabelingFrameOpenState(
            context: context,
            selectedClassIdentifier: selectedClassIdentifier,
            draft: LabelingDraftState(rectangles: rectangles)
        )
    }

    private static func recognizedScreenContext(
        observedIdentifiers: Set<String>,
        fallback: LabelingContext
    ) -> LabelingContext {
        if observedIdentifiers.contains("main-title.hollow-knight-logo") {
            return .mainTitle
        }
        if observedIdentifiers.contains("select-profile.heading") {
            return .selectProfile
        }
        if observedIdentifiers.isSuperset(of: ["game.health", "game.mana", "game.geo"]) {
            return .game
        }
        return fallback
    }
}

struct RecentFramesView: View {
    let examples: [SavedLabelingExample]
    let capturedFrames: [RecentCapturedFrame]
    let onOpen: (SavedLabelingExample) -> Void
    let onOpenCaptured: (RecentCapturedFrame) -> Void
    let onResume: () -> Void

    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Recent Frames")
                    .font(.headline)
                Spacer()
                Button("Resume (L)") {
                    onResume()
                    dismiss()
                }
                .keyboardShortcut("l", modifiers: [])
                Button("Close") {
                    dismiss()
                }
            }
            recentFrameColumns
        }
        .padding(16)
        .frame(
            minWidth: 1_040,
            idealWidth: 1_280,
            maxWidth: .infinity,
            minHeight: 620,
            idealHeight: 760,
            maxHeight: .infinity
        )
    }

    private var recentFrameColumns: some View {
        HStack(alignment: .top, spacing: 16) {
            recentLabeledFrames
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            Divider()
            recentCapturedFrames
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var recentLabeledFrames: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Recent Labeled Frames")
                .font(.headline)
            if examples.isEmpty {
                ContentUnavailableView(
                    "No Recent Labeled Frames",
                    systemImage: "rectangle.and.pencil.and.ellipsis",
                    description: Text("Label an object to save a frame.")
                )
            } else {
                ScrollView {
                    LazyVGrid(columns: thumbnailColumns, spacing: 12) {
                        ForEach(examples) { example in
                            Button {
                                onOpen(example)
                                dismiss()
                            } label: {
                                LabelingExampleThumbnail(
                                    example: example,
                                    selectedClassID: nil
                                )
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(accessibilityLabel(for: example))
                        }
                    }
                    .padding(2)
                }
            }
        }
    }

    private var recentCapturedFrames: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Recent Captured Frames")
                .font(.headline)
            if capturedFrames.isEmpty {
                ContentUnavailableView(
                    "No Recent Captured Frames",
                    systemImage: "photo.stack",
                    description: Text("Resume gameplay briefly to fill the capture buffer.")
                )
            } else {
                ScrollView {
                    LazyVGrid(columns: thumbnailColumns, spacing: 12) {
                        ForEach(capturedFrames) { frame in
                            Button {
                                onOpenCaptured(frame)
                                dismiss()
                            } label: {
                                RecentCapturedFrameThumbnail(frame: frame)
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(
                                "Open captured frame with \(frame.detections.count) predicted boxes"
                            )
                        }
                    }
                    .padding(2)
                }
            }
        }
    }

    private var thumbnailColumns: [GridItem] {
        [GridItem(.adaptive(minimum: 155, maximum: 230), spacing: 12)]
    }

    private func accessibilityLabel(for example: SavedLabelingExample) -> String {
        let count = example.manifest.annotations.count
        return "Open recent frame with \(count) labeled object\(count == 1 ? "" : "s")"
    }
}

private struct RecentCapturedFrameThumbnail: View {
    let frame: RecentCapturedFrame

    var body: some View {
        GeometryReader { proxy in
            let layout = LabelingImageLayout(
                imageSize: CGSize(width: frame.image.width, height: frame.image.height),
                containerSize: proxy.size
            )
            ZStack(alignment: .topLeading) {
                Color.black
                Image(decorative: frame.image, scale: 1)
                    .resizable()
                    .frame(width: layout.fittedRect.width, height: layout.fittedRect.height)
                    .position(x: layout.fittedRect.midX, y: layout.fittedRect.midY)

                ForEach(frame.detections) { detection in
                    if let rectangle = layout.displayRect(for: detection.normalizedRect) {
                        Rectangle()
                            .stroke(.purple, lineWidth: 1.5)
                            .frame(width: rectangle.width, height: rectangle.height)
                            .position(x: rectangle.midX, y: rectangle.midY)
                    }
                }
            }
        }
        .aspectRatio(
            CGFloat(frame.image.width) / CGFloat(max(1, frame.image.height)),
            contentMode: .fit
        )
        .clipShape(RoundedRectangle(cornerRadius: 7))
        .overlay {
            RoundedRectangle(cornerRadius: 7)
                .stroke(.purple.opacity(0.55), lineWidth: 1)
        }
        .contentShape(RoundedRectangle(cornerRadius: 7))
    }
}

private struct LabelingExampleThumbnail: View {
    let example: SavedLabelingExample
    let selectedClassID: String?

    var body: some View {
        GeometryReader { proxy in
            if let image {
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

                    ForEach(selectedAnnotations) { annotation in
                        if let rectangle = layout.displayRect(for: annotation.normalizedRect) {
                            Rectangle()
                                .stroke(
                                    annotation.isHardNegative ? Color.pink : Color.cyan,
                                    lineWidth: 1.5
                                )
                                .frame(width: rectangle.width, height: rectangle.height)
                                .position(x: rectangle.midX, y: rectangle.midY)
                        }
                    }
                }
            } else {
                ZStack {
                    Color.black
                    Image(systemName: "exclamationmark.triangle")
                        .foregroundStyle(.secondary)
                }
            }
        }
        .aspectRatio(imageAspectRatio, contentMode: .fit)
        .clipShape(RoundedRectangle(cornerRadius: 7))
        .overlay {
            RoundedRectangle(cornerRadius: 7)
                .stroke(.white.opacity(0.18), lineWidth: 1)
        }
        .contentShape(RoundedRectangle(cornerRadius: 7))
    }

    private var selectedAnnotations: [LabelingExampleAnnotation] {
        guard let selectedClassID else { return example.manifest.annotations }
        return example.manifest.annotations.filter {
            $0.classIdentifier == selectedClassID
        }
    }

    private var imageAspectRatio: CGFloat {
        guard example.manifest.imageHeight > 0 else { return 16 / 9 }
        return CGFloat(example.manifest.imageWidth) / CGFloat(example.manifest.imageHeight)
    }

    private var image: CGImage? {
        let imageURL = example.directoryURL.appendingPathComponent(example.manifest.imageFilename)
        return ImageFileIO.load(imageURL)
    }
}
