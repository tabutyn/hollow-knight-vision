import Foundation

/// Compare nearby observations of the same part of the screen. Global matches use the stricter
/// NotebookTextMatch comparison. Short, unrelated UI labels must not absorb one another.
enum NotebookLineMatch {
    enum Relation { case variant, shorter, longer }
    static let protected: Set<Substring> = [
        "north", "south", "east", "west", "left", "right", "up", "down", "open", "closed", "enter", "exit",
        "hold", "press", "yes", "no", "not", "never", "buy", "sell", "on", "off", "without"
    ]

    static func scopeAllows(_ a: CGRect?, _ b: CGRect?) -> Bool {
        guard let a, let b, !a.isEmpty, !b.isEmpty else { return true }
        let overlap = min(a.maxY, b.maxY) - max(a.minY, b.minY)
        return overlap >= 0.5 * min(a.height, b.height) && min(a.maxX, b.maxX) > max(a.minX, b.minX)
    }

    static func relation(_ old: [Substring], _ new: [Substring], allowFragments: Bool, samePosition: Bool = false) -> Relation? {
        guard !old.isEmpty, !new.isEmpty else { return nil }
        func protectedWords(_ words: [Substring]) -> [Substring] {
            words.filter { protected.contains($0) || $0.contains(where: \.isNumber) }
        }
        if protectedWords(old) != protectedWords(new) {
            // OCR confuses v and w. Only tolerate this polarity ambiguity on an otherwise
            // independently degraded version of the same long sentence.
            guard old.count == new.count, old.count >= 8,
                  zip(old, new).filter({ $0 != $1 }).count >= 2 else { return nil }
            let restored = zip(old, new).map { a, b -> Substring in
                guard a != b, a.count == b.count, protected.contains(a) != protected.contains(b),
                      !a.contains(where: \.isNumber), !b.contains(where: \.isNumber) else { return b }
                let differences = zip(a, b).filter { $0 != $1 }
                guard differences.count == 1, let (x, y) = differences.first,
                      ["vw", "il"].contains(where: { $0.contains(x) && $0.contains(y) }) else { return b }
                return a
            }
            guard protectedWords(old) == protectedWords(restored) else { return nil }
        }
        if old.count != new.count {
            guard allowFragments else { return nil }
            let (short, long) = old.count < new.count ? (old, new) : (new, old)
            guard short.count >= 2, short.reduce(0, { $0 + $1.count }) >= 5 else { return nil }
            if edge(short, of: long, atStart: true, located: samePosition) || edge(short, of: long, atStart: false, located: samePosition) {
                return old.count < new.count ? .longer : .shorter
            }
            return nil
        }
        guard old.count >= 6 else { return nil }
        var anchors = 0, errors = 0, letters = 0
        for (a, b) in zip(old, new) {
            let width = max(a.count, b.count)
            letters += width
            if a == b { anchors += 1; continue }
            let limit = width >= 4 ? 3 : 1
            let cost = distance(a, b, limit: limit)
            guard cost <= limit else { return nil }
            errors += cost
        }
        let usualAnchors = max(4, (old.count + 1) / 2)
        let requiredAnchors = samePosition && old.count >= 8 ? min(usualAnchors, max(5, old.count * 2 / 5)) : usualAnchors
        return anchors >= requiredAnchors && errors * 5 <= letters ? .variant : nil
    }

    private static func edge(_ short: [Substring], of long: [Substring], atStart: Bool, located: Bool) -> Bool {
        let offset = atStart ? 0 : long.count - short.count
        var exact = 0
        for (i, token) in short.enumerated() {
            let other = long[offset + i]
            if token == other { exact += 1; continue }
            if i == (atStart ? short.count - 1 : 0) {
                let left = atStart ? String(token) : String(token.reversed())
                let right = atStart ? String(other) : String(other.reversed())
                let common = zip(left, right).prefix { $0 == $1 }.count
                if right.hasPrefix(left) || (short.count >= 3 && common >= 2 && left.count - common <= 1) ||
                    (token.count == 1 && short.count >= 4) { continue }
            }
            let limit = located && short.count >= 5 && min(token.count, other.count) >= 5 ? 2 : 1
            guard min(token.count, other.count) >= 4, distance(token, other, limit: limit) <= limit else { return false }
        }
        return exact >= max(1, short.count / 2)
    }

    private static func distance(_ lhs: Substring, _ rhs: Substring, limit: Int) -> Int {
        guard abs(lhs.count - rhs.count) <= limit else { return limit + 1 }
        let right = Array(rhs)
        var previous = Array(0...right.count)
        for (i, ch) in lhs.enumerated() {
            var current = [i + 1]
            for (j, other) in right.enumerated() {
                current.append(min(previous[j + 1] + 1, current[j] + 1, previous[j] + (ch == other ? 0 : 1)))
            }
            if current.min()! > limit { return limit + 1 }
            previous = current
        }
        return previous.last!
    }
}
