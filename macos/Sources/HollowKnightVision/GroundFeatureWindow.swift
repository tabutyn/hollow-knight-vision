import AppKit
import CoreGraphics
import SwiftUI

struct GroundViewerSlot: Equatable, Identifiable {
    let sequenceIndex: Int
    let feature: LayerSceneGroundFeatureDetails?

    var id: Int { sequenceIndex }
}

/// Builds one spatially faithful row. Missing sequence indices remain blank
/// slots, so stitched pixels never collapse a real gap in the atlas edge.
enum GroundViewerRowAssembler {
    static func slots(
        from features: [LayerSceneGroundFeatureDetails],
        segmentID: Int
    ) -> [GroundViewerSlot] {
        let row = features.filter { $0.segmentID == segmentID }
        guard let minimum = row.map(\.sequenceIndex).min(),
              let maximum = row.map(\.sequenceIndex).max()
        else { return [] }
        let byIndex = Dictionary(
            row.map { ($0.sequenceIndex, $0) },
            uniquingKeysWith: { first, _ in first }
        )
        return (minimum...maximum).map {
            GroundViewerSlot(sequenceIndex: $0, feature: byIndex[$0])
        }
    }

    static func stitchedPixels(from slots: [GroundViewerSlot]) -> [UInt8] {
        let tileWidth = GroundHypothesisTracker.featureWidth
        let tileHeight = GroundHypothesisTracker.featureHeight
        let width = slots.count * tileWidth
        guard width > 0 else { return [] }
        var pixels = [UInt8](repeating: 0, count: width * tileHeight)
        for (slotIndex, slot) in slots.enumerated() {
            for y in 0..<tileHeight {
                for x in 0..<tileWidth {
                    let destination = y * width + slotIndex * tileWidth + x
                    let background: UInt8 = 0
                    if let source = slot.feature?.referencePixels,
                       source.count == tileWidth * tileHeight {
                        let sourceIndex = y * tileWidth + x
                        let opacity = slot.feature?.referenceOpacity.count
                            == tileWidth * tileHeight
                            ? Int(slot.feature!.referenceOpacity[sourceIndex])
                            : 255
                        pixels[destination] = UInt8(
                            (Int(source[sourceIndex]) * opacity
                                + Int(background) * (255 - opacity) + 127) / 255
                        )
                    } else {
                        pixels[destination] = background
                    }
                }
            }
        }
        return pixels
    }

    static func stitchedImage(from slots: [GroundViewerSlot]) -> CGImage? {
        let tileWidth = GroundHypothesisTracker.featureWidth
        let tileHeight = GroundHypothesisTracker.featureHeight
        let width = slots.count * tileWidth
        let pixels = stitchedPixels(from: slots)
        guard width > 0,
              pixels.count == width * tileHeight,
              let provider = CGDataProvider(data: Data(pixels) as CFData)
        else { return nil }
        return CGImage(
            width: width,
            height: tileHeight,
            bitsPerComponent: 8,
            bitsPerPixel: 8,
            bytesPerRow: width,
            space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

    static func nearestFeature(
        toSlot slotIndex: Int,
        in slots: [GroundViewerSlot]
    ) -> LayerSceneGroundFeatureDetails? {
        guard !slots.isEmpty else { return nil }
        let clamped = min(max(0, slotIndex), slots.count - 1)
        if let feature = slots[clamped].feature { return feature }
        for distance in 1..<slots.count {
            let left = clamped - distance
            if slots.indices.contains(left), let feature = slots[left].feature {
                return feature
            }
            let right = clamped + distance
            if slots.indices.contains(right), let feature = slots[right].feature {
                return feature
            }
        }
        return nil
    }
}

struct GroundViewerRowState: Equatable {
    let features: [LayerSceneGroundFeatureDetails]
    let slots: [GroundViewerSlot]
    let selected: LayerSceneGroundFeatureDetails

    static func refreshed(
        selected: LayerSceneGroundFeatureDetails,
        available: [LayerSceneGroundFeatureDetails]
    ) -> GroundViewerRowState {
        let matching = available.filter { $0.segmentID == selected.segmentID } + [selected]
        var features = Array(Dictionary(
            matching.map { ($0.sequenceIndex, $0) },
            uniquingKeysWith: { first, _ in first }
        ).values).sorted { $0.sequenceIndex < $1.sequenceIndex }
        if features.isEmpty { features = [selected] }
        return GroundViewerRowState(
            features: features,
            slots: GroundViewerRowAssembler.slots(
                from: features,
                segmentID: selected.segmentID
            ),
            selected: features.first {
                $0.sequenceIndex == selected.sequenceIndex
            } ?? selected
        )
    }
}

/// Owns exactly one movable viewer panel and publishes its centered tile so
/// the live game overlay can highlight the same world feature in real time.
final class GroundFeatureWindowController: NSObject, ObservableObject, NSWindowDelegate {
    @Published private(set) var selectedFeature: LayerSceneGroundFeatureDetails?
    @Published private(set) var rowSlots = [GroundViewerSlot]()

    private var panel: NSPanel?
    private var features = [LayerSceneGroundFeatureDetails]()

    func show(
        selected: LayerSceneGroundFeatureDetails,
        available: [LayerSceneGroundFeatureDetails]
    ) {
        dispatchPrecondition(condition: .onQueue(.main))
        apply(GroundViewerRowState.refreshed(
            selected: selected,
            available: available
        ))

        let panel = panel ?? makePanel()
        self.panel = panel
        panel.contentView = NSHostingView(
            rootView: GroundViewerContent(controller: self)
                .preferredColorScheme(.dark)
        )
        position(panel, above: NSEvent.mouseLocation)
        panel.orderFrontRegardless()
    }

    func refresh(available: [LayerSceneGroundFeatureDetails]) {
        dispatchPrecondition(condition: .onQueue(.main))
        guard let selectedFeature else { return }
        apply(GroundViewerRowState.refreshed(
            selected: selectedFeature,
            available: available
        ))
    }

    func select(sequenceIndex: Int) {
        guard let feature = features.first(where: {
            $0.sequenceIndex == sequenceIndex
        }) else { return }
        selectedFeature = feature
    }

    func select(offset: Int) {
        guard let selectedFeature,
              let current = features.firstIndex(where: {
                  $0.sequenceIndex == selectedFeature.sequenceIndex
              })
        else { return }
        let proposed = current + offset
        guard features.indices.contains(proposed) else { return }
        self.selectedFeature = features[proposed]
    }

    func close() {
        selectedFeature = nil
        panel?.orderOut(nil)
    }

    func windowWillClose(_ notification: Notification) {
        selectedFeature = nil
    }

    private func apply(_ state: GroundViewerRowState) {
        features = state.features
        rowSlots = state.slots
        selectedFeature = state.selected
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(
            contentRect: CGRect(x: 0, y: 0, width: 720, height: 255),
            styleMask: [.titled, .closable, .utilityWindow],
            backing: .buffered,
            defer: false
        )
        panel.title = "Ground Viewer"
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.delegate = self
        return panel
    }

    private func position(_ panel: NSPanel, above cursor: CGPoint) {
        var frame = panel.frame
        frame.origin = CGPoint(x: cursor.x + 12, y: cursor.y + 14)
        if let visible = NSScreen.screens.first(where: { $0.frame.contains(cursor) })?.visibleFrame {
            frame.origin.x = min(max(visible.minX, frame.origin.x), visible.maxX - frame.width)
            frame.origin.y = min(max(visible.minY, frame.origin.y), visible.maxY - frame.height)
        }
        panel.setFrame(frame, display: true)
    }
}

private struct GroundViewerContent: View {
    @ObservedObject var controller: GroundFeatureWindowController

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Text("Ground Viewer")
                    .font(.headline)
                if let selected = controller.selectedFeature {
                    Text("Line \(selected.segmentID) · \(controller.rowSlots.compactMap(\.feature).count) features")
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Text("Drag left/right to review")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            GroundStripScroller(
                slots: controller.rowSlots,
                selectedSequenceIndex: controller.selectedFeature?.sequenceIndex,
                onSelect: { [weak controller] feature in
                    controller?.select(sequenceIndex: feature.sequenceIndex)
                }
            )
            .frame(height: 112)
            .background(.black)
            .overlay(Rectangle().stroke(.white.opacity(0.22)))

            HStack(alignment: .center) {
                Button {
                    controller.select(offset: -1)
                } label: {
                    Image(systemName: "arrow.left")
                }
                .disabled(!hasPrevious)
                .help("Previous feature on this line")

                if let feature = controller.selectedFeature {
                    Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 2) {
                        GridRow { Text("World X"); Text(format(feature.worldPosition.x)) }
                        GridRow { Text("World Y"); Text(format(feature.worldPosition.y)) }
                        GridRow { Text("Line ID"); Text("\(feature.segmentID)") }
                        GridRow { Text("Feature"); Text("\(feature.sequenceIndex)") }
                    }
                    .font(.caption.monospaced())
                }

                Spacer()

                Button {
                    controller.select(offset: 1)
                } label: {
                    Image(systemName: "arrow.right")
                }
                .disabled(!hasNext)
                .help("Next feature on this line")
            }
        }
        .padding(12)
        .frame(width: 720, height: 255)
    }

    private var selectedArrayIndex: Int? {
        guard let selected = controller.selectedFeature else { return nil }
        return controller.rowSlots.compactMap(\.feature).firstIndex {
            $0.sequenceIndex == selected.sequenceIndex
        }
    }

    private var hasPrevious: Bool { (selectedArrayIndex ?? 0) > 0 }

    private var hasNext: Bool {
        guard let selectedArrayIndex else { return false }
        return selectedArrayIndex + 1 < controller.rowSlots.compactMap(\.feature).count
    }

    private func format(_ value: CGFloat) -> String {
        String(format: "%.1f", Double(value))
    }
}

private struct GroundStripScroller: NSViewRepresentable {
    let slots: [GroundViewerSlot]
    let selectedSequenceIndex: Int?
    let onSelect: (LayerSceneGroundFeatureDetails) -> Void

    func makeNSView(context: Context) -> GroundStripScrollView {
        GroundStripScrollView()
    }

    func updateNSView(_ view: GroundStripScrollView, context: Context) {
        view.configure(
            slots: slots,
            selectedSequenceIndex: selectedSequenceIndex,
            onSelect: onSelect
        )
    }
}

private final class GroundStripScrollView: NSScrollView {
    static let displayedTileSize: CGFloat = 96

    private let stripView = GroundStripDocumentView()
    private var slots = [GroundViewerSlot]()
    private var selectedSequenceIndex: Int?
    private var onSelect: ((LayerSceneGroundFeatureDetails) -> Void)?
    private var dragStartWindowX: CGFloat?
    private var dragStartOriginX: CGFloat = 0
    private var pendingCenter = false

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        drawsBackground = true
        backgroundColor = .black
        borderType = .noBorder
        hasHorizontalScroller = true
        hasVerticalScroller = false
        horizontalScrollElasticity = .allowed
        verticalScrollElasticity = .none
        autohidesScrollers = false
        documentView = stripView
        stripView.owner = self
        contentView.postsBoundsChangedNotifications = true
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(clipBoundsChanged),
            name: NSView.boundsDidChangeNotification,
            object: contentView
        )
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    func configure(
        slots: [GroundViewerSlot],
        selectedSequenceIndex: Int?,
        onSelect: @escaping (LayerSceneGroundFeatureDetails) -> Void
    ) {
        let wasEmpty = self.slots.isEmpty
        let priorFirstSequence = self.slots.first?.sequenceIndex
        let slotsChanged = self.slots != slots
        let selectionChanged = self.selectedSequenceIndex != selectedSequenceIndex
        self.slots = slots
        self.onSelect = onSelect
        self.selectedSequenceIndex = selectedSequenceIndex
        stripView.configure(slots: slots, selectedSequenceIndex: selectedSequenceIndex)
        updateDocumentSize()
        if slotsChanged,
           !wasEmpty,
           let priorFirstSequence,
           let currentFirstSequence = slots.first?.sequenceIndex,
           currentFirstSequence < priorFirstSequence {
            // New cells inserted to the left must not move the ground already
            // under the user's cursor while live evidence refreshes.
            let added = priorFirstSequence - currentFirstSequence
            scroll(toX: contentView.bounds.origin.x
                + CGFloat(added) * Self.displayedTileSize)
        }
        if wasEmpty || selectionChanged {
            pendingCenter = true
            DispatchQueue.main.async { [weak self] in
                self?.centerPendingSelection()
            }
        }
    }

    override func layout() {
        super.layout()
        updateDocumentSize()
        centerPendingSelection()
    }

    fileprivate func beginDrag(_ event: NSEvent) {
        dragStartWindowX = event.locationInWindow.x
        dragStartOriginX = contentView.bounds.origin.x
    }

    fileprivate func continueDrag(_ event: NSEvent) {
        guard let dragStartWindowX else { return }
        let delta = event.locationInWindow.x - dragStartWindowX
        scroll(toX: dragStartOriginX - delta)
    }

    fileprivate func endDrag() {
        dragStartWindowX = nil
        notifyCenteredFeature()
    }

    private func updateDocumentSize() {
        let padding = max(
            0,
            contentSize.width * 0.5 - Self.displayedTileSize * 0.5
        )
        stripView.leadingPadding = padding
        let width = max(
            1,
            CGFloat(slots.count) * Self.displayedTileSize + padding * 2
        )
        let height = max(Self.displayedTileSize, contentSize.height)
        if stripView.frame.size != CGSize(width: width, height: height) {
            stripView.frame = CGRect(x: 0, y: 0, width: width, height: height)
        }
    }

    private func centerPendingSelection() {
        guard pendingCenter,
              contentView.bounds.width > 0,
              let selectedSequenceIndex,
              let index = slots.firstIndex(where: {
                  $0.sequenceIndex == selectedSequenceIndex
              })
        else { return }
        pendingCenter = false
        let target = stripView.leadingPadding
            + CGFloat(index) * Self.displayedTileSize
            + Self.displayedTileSize * 0.5 - contentView.bounds.width * 0.5
        scroll(toX: target)
    }

    private func scroll(toX x: CGFloat) {
        let maximum = max(0, stripView.frame.width - contentView.bounds.width)
        let point = CGPoint(
            x: min(max(0, x), maximum),
            y: contentView.bounds.origin.y
        )
        contentView.scroll(to: point)
        reflectScrolledClipView(contentView)
    }

    @objc private func clipBoundsChanged() {
        notifyCenteredFeature()
    }

    private func notifyCenteredFeature() {
        guard !slots.isEmpty else { return }
        let centerX = contentView.bounds.midX - stripView.leadingPadding
        let slotIndex = Int(floor(centerX / Self.displayedTileSize))
        guard let feature = GroundViewerRowAssembler.nearestFeature(
            toSlot: slotIndex,
            in: slots
        ) else { return }
        stripView.selectedSequenceIndex = feature.sequenceIndex
        stripView.needsDisplay = true
        guard feature.sequenceIndex != selectedSequenceIndex else { return }
        selectedSequenceIndex = feature.sequenceIndex
        onSelect?(feature)
    }
}

private final class GroundStripDocumentView: NSView {
    weak var owner: GroundStripScrollView?
    var selectedSequenceIndex: Int?
    var leadingPadding: CGFloat = 0 {
        didSet { if oldValue != leadingPadding { needsDisplay = true } }
    }

    private var slots = [GroundViewerSlot]()
    private var image: CGImage?

    override var isFlipped: Bool { true }

    func configure(slots: [GroundViewerSlot], selectedSequenceIndex: Int?) {
        if self.slots != slots {
            self.slots = slots
            image = GroundViewerRowAssembler.stitchedImage(from: slots)
        }
        self.selectedSequenceIndex = selectedSequenceIndex
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        NSColor.black.setFill()
        dirtyRect.fill()
        let tileSize = GroundStripScrollView.displayedTileSize
        let stripHeight = tileSize
        let y = max(0, (bounds.height - stripHeight) * 0.5)
        let destination = CGRect(
            x: leadingPadding,
            y: y,
            width: CGFloat(slots.count) * tileSize,
            height: stripHeight
        )
        if let image {
            NSGraphicsContext.current?.imageInterpolation = .none
            NSImage(cgImage: image, size: CGSize(
                width: image.width,
                height: image.height
            )).draw(
                in: destination,
                from: .zero,
                operation: .copy,
                fraction: 1,
                respectFlipped: true,
                hints: nil
            )
        }
        if let selectedSequenceIndex,
           let index = slots.firstIndex(where: {
               $0.sequenceIndex == selectedSequenceIndex
           }) {
            let highlight = CGRect(
                x: leadingPadding + CGFloat(index) * tileSize + 2,
                y: y + 2,
                width: tileSize - 4,
                height: tileSize - 4
            )
            NSColor.white.setStroke()
            let path = NSBezierPath(rect: highlight)
            path.lineWidth = 4
            path.stroke()
        }
    }

    override func mouseDown(with event: NSEvent) {
        owner?.beginDrag(event)
    }

    override func mouseDragged(with event: NSEvent) {
        owner?.continueDrag(event)
    }

    override func mouseUp(with event: NSEvent) {
        owner?.endDrag()
    }
}
