import Foundation

/// Offline English spell checking. Preserve punctuation and casing from the chosen observation.
enum NotebookSpelling {
    private static let words: Set<String> = {
        var result = Set<String>()
        for name in ["notebook-english", "notebook-hallownest"] {
            let url = Bundle.main.url(forResource: name, withExtension: "txt")
                ?? Bundle.module.url(forResource: name, withExtension: "txt")
            guard let url, let list = try? String(contentsOf: url, encoding: .utf8) else { continue }
            result.formUnion(list.split(whereSeparator: \.isNewline).map(String.init))
        }
        return result
    }()
    private static let wordPattern = try! NSRegularExpression(pattern: #"[\p{L}\p{M}\p{N}]+"#)

    static func usesEnglish(_ language: String) -> Bool {
        language.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first == "en"
    }

    static func known(_ word: String) -> Bool { words.contains(word.lowercased()) }

    private static func ranges(_ text: String) -> [Range<String.Index>] {
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        return wordPattern.matches(in: text, range: range).compactMap { Range($0.range, in: text) }
    }

    private static func misspellings(_ text: String) -> Int {
        ranges(text).count { range in
            let word = String(text[range])
            return word.allSatisfy(\.isLetter) && !known(word)
        }
    }

    private static func suggestions(_ word: String) -> Set<String> {
        let letters = Array(word)
        let alphabet = "abcdefghijklmnopqrstuvwxyz"
        var result = Set<String>()
        for i in 0...letters.count {
            let left = String(letters.prefix(i)), right = String(letters.dropFirst(i))
            for ch in alphabet {
                let added = left + String(ch) + right
                if words.contains(added) { result.insert(added) }
                if i < letters.count {
                    let substituted = left + String(ch) + right.dropFirst()
                    if words.contains(substituted) { result.insert(substituted) }
                }
            }
            if i < letters.count {
                let deleted = left + right.dropFirst()
                if words.contains(deleted) { result.insert(deleted) }
                if i + 1 < letters.count {
                    let swapped = left + String(letters[i + 1]) + String(letters[i]) + right.dropFirst(2)
                    if words.contains(swapped) { result.insert(swapped) }
                }
            }
        }
        return result
    }

    /// Select a whole reading first. Only combine words in independently matched, aligned lines.
    static func preferred(_ old: String, confidence: Float, over new: String, confidence newConfidence: Float,
                          language: String) -> (String, Float) {
        guard old != new else { return (old, max(confidence, newConfidence)) }
        guard usesEnglish(language) else { return newConfidence > confidence ? (new, newConfidence) : (old, confidence) }
        let oldMisses = misspellings(old), newMisses = misspellings(new)
        let useNew: Bool
        if newMisses != oldMisses { useNew = newMisses < oldMisses }
        else if newConfidence != confidence { useNew = newConfidence > confidence }
        else {
            // Prefer an observed clean ending when the recognized words are identical.
            useNew = NotebookTextMatch.key(old) == NotebookTextMatch.key(new) &&
                ((old.hasSuffix("|") && !new.hasSuffix("|")) || (old.hasSuffix(",") && new.hasSuffix(".")) ||
                 (old.filter { $0 == "'" }.count % 2 == 1 && new.filter { $0 == "'" }.count == 2))
        }
        let base = useNew ? new : old, alternate = useNew ? old : new
        let preferredConfidence = useNew ? newConfidence : confidence
        let baseRanges = ranges(base), other = ranges(alternate)
        guard baseRanges.count == other.count, min(oldMisses, newMisses) > 0 else { return (base, preferredConfidence) }
        let result = NSMutableString(string: base)
        for (a, b) in zip(baseRanges, other).reversed() {
            let word = String(base[a]), variant = String(alternate[b])
            guard word != variant, !known(word), word.allSatisfy(\.isLowercase),
                  variant.allSatisfy(\.isLowercase), (4...24).contains(word.count),
                  (4...24).contains(variant.count) else { continue }
            if known(variant) {
                result.replaceCharacters(in: NSRange(a, in: base), with: variant)
            } else {
                let common = suggestions(word).intersection(suggestions(variant))
                if common.count == 1, let spelling = common.first {
                    result.replaceCharacters(in: NSRange(a, in: base), with: spelling)
                }
            }
        }
        return (result as String, preferredConfidence)
    }
}
