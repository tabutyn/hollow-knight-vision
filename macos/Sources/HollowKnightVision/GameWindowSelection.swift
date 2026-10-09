import CoreGraphics

struct GameWindowCandidate: Equatable {
    let title: String
    let size: CGSize
}

enum GameWindowSelection {
    static func bestIndex(in candidates: [GameWindowCandidate]) -> Int? {
        candidates.indices.max { lhs, rhs in
            score(candidates[lhs]) < score(candidates[rhs])
        }
    }

    private static func score(_ candidate: GameWindowCandidate) -> (Int, CGFloat) {
        let normalizedTitle = candidate.title.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let titleRank = normalizedTitle == "hollow knight" ? 1 : 0
        return (titleRank, max(0, candidate.size.width) * max(0, candidate.size.height))
    }
}
