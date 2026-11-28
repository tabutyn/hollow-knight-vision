import SwiftUI

/// A single native text selection can span several log entries or request sections.
struct SelectableTranscript: NSViewRepresentable {
    let text: String
    let label: String
    var color: NSColor = .labelColor

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.drawsBackground = false
        scroll.borderType = .noBorder
        scroll.autohidesScrollers = true
        let view = scroll.documentView as! NSTextView
        view.isEditable = false
        view.isSelectable = true
        view.isRichText = false
        view.drawsBackground = false
        view.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
        view.textColor = color
        view.setAccessibilityLabel(label)
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let view = scroll.documentView as? NSTextView, view.string != text else { return }
        let old = view.string as NSString
        let selection = view.selectedRange()
        let updated = text as NSString
        var retained: NSRange?
        if selection.length > 0, NSMaxRange(selection) <= old.length {
            // A live notebook prepends new entries. Compute the offset before changing text storage.
            if updated.hasSuffix(old as String) {
                retained = NSRange(location: selection.location + updated.length - old.length, length: selection.length)
            } else if NSMaxRange(selection) <= updated.length,
                      updated.substring(with: selection) == old.substring(with: selection) {
                retained = selection
            }
        }
        view.string = text
        if let retained { view.setSelectedRange(retained) }
    }
}

struct CopyTextButton: View {
    let title: String
    let text: String

    var body: some View {
        Button(title) { copy(to: .general) }.disabled(text.isEmpty)
    }

    func copy(to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }
}
