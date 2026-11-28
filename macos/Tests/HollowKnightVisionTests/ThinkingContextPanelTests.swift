import SwiftUI
import XCTest
@testable import HollowKnightVision

@MainActor
final class ThinkingContextPanelTests: XCTestCase {
    func testNativeTranscriptCopiesTextAcrossLogEntriesAndKeepsItsSelection() throws {
        let host = NSHostingView(rootView: SelectableTranscript(text: "Yes\n\nStart Game", label: "Text log"))
        host.frame = CGRect(x: 0, y: 0, width: 260, height: 140)
        func textView(in view: NSView) -> NSTextView? {
            if let text = view as? NSTextView { return text }
            return view.subviews.lazy.compactMap { textView(in: $0) }.first
        }
        host.layoutSubtreeIfNeeded()
        let text = try XCTUnwrap(textView(in: host))
        XCTAssertTrue(text.isSelectable)
        XCTAssertFalse(text.isEditable)
        text.setSelectedRange(NSRange(location: 1, length: 9))
        let board = NSPasteboard.withUniqueName()
        defer { board.releaseGlobally() }
        XCTAssertTrue(text.writeSelection(to: board, types: text.writablePasteboardTypes))
        XCTAssertEqual(board.string(forType: .string), "es\n\nStart")
        host.rootView = SelectableTranscript(text: "Options\n\nYes\n\nStart Game", label: "Text log")
        host.layoutSubtreeIfNeeded()
        XCTAssertEqual((text.string as NSString).substring(with: text.selectedRange()), "es\n\nStart")
    }

    func testNotebookContextRendersCompactAndExpandedWithInferenceDisabled() async throws {
        let notebook = GameTextNotebook()
        let texts = ["Dirtmouth", "Forgotten Crossroads", "City of Tears", "Crystal Peak", "Queen's Gardens", "Resting Grounds"]
        let lines = texts.enumerated().map { GameTextLine(id: $0.offset, text: $0.element, bounds: .zero, confidence: 1) }
        _ = try await notebook.remember(lines, context: "Map", language: "en", confirmed: [])
        let reader = GameTextReader(notebook: notebook)
        _ = try await reader.policyContext()
        XCTAssertEqual(reader.entries.count, texts.count)
        let session = ThinkingModelSession(isEnabled: false)
        for expanded in [false, true] {
            let size = expanded ? CGSize(width: 900, height: 460) : CGSize(width: 280, height: 66)
            let panel = ThinkingContextPanel(reader: reader, session: session, inspector: ThinkingInspector(),
                player: BotPlaybackController(loadModel: false), expanded: .constant(expanded), readsText: true)
            let host = NSHostingView(rootView: panel.frame(width: size.width, height: size.height))
            host.frame = CGRect(origin: .zero, size: size)
            host.layoutSubtreeIfNeeded()
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            XCTAssertGreaterThanOrEqual(bitmap.pixelsWide, Int(size.width))
            if let dir = ProcessInfo.processInfo.environment["HKV_CONTEXT_PANEL_RENDER"] {
                let url = URL(fileURLWithPath: dir).appendingPathComponent(expanded ? "expanded.png" : "compact.png")
                try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: url)
            }
        }
        XCTAssertFalse(session.isEnabled)
    }
}
