import CoreGraphics
import Foundation

struct LiveGameStatus: Equatable {
    enum Context: Equatable {
        case mainTitle
        case selectProfile
        case gameplay
        case labelSet(LabelingContext)

        var labelingContext: LabelingContext? {
            switch self {
            case .mainTitle: return .mainTitle
            case .selectProfile: return .selectProfile
            case .gameplay: return .game
            case .labelSet(let context): return context
            }
        }

        var isGameplay: Bool { self == .gameplay }
    }

    enum SelectionPresentation: Equatable {
        case selectedSuffix
        case hyphen
    }

    let state: String
    let context: Context
    let selectedMenuOption: String?
    let selectionPresentation: SelectionPresentation

    var displayText: String {
        guard let selectedMenuOption else { return state }
        switch selectionPresentation {
        case .selectedSuffix:
            return "\(state) · \(selectedMenuOption) selected"
        case .hyphen:
            return "\(state) - \(selectedMenuOption)"
        }
    }
}

struct LiveGameStatusTracker {
    static let missingCueGrace: TimeInterval = 0.8

    private(set) var current: LiveGameStatus?
    private var lastRecognitionTimestamp: Double?
    private var lastObservedTimestamp: Double?

    mutating func reset(preservingGameplay: Bool = false) {
        // A new capture/inference generation invalidates timestamps, not the
        // fact that gameplay was already established in this game session.
        if !preservingGameplay || current?.context.isGameplay != true {
            current = nil
        }
        lastRecognitionTimestamp = nil
        lastObservedTimestamp = nil
    }

    mutating func observe(_ recognized: LiveGameStatus?, at timestamp: Double)
        -> LiveGameStatus? {
        guard timestamp.isFinite,
              timestamp >= (lastObservedTimestamp ?? -Double.infinity) else {
            return current
        }
        lastObservedTimestamp = timestamp
        let acceptedRecognition: LiveGameStatus?
        if recognized?.context == .labelSet(.credits),
           current?.context != .labelSet(.extras),
           current?.context != .labelSet(.credits) {
            // The Credits pixel signature is deliberately cheap and can also
            // match dark gameplay with a bright center. Credits is reachable
            // from Extras, so require that navigation context before allowing
            // the weak visual cue to establish or replace a screen state.
            acceptedRecognition = nil
        } else {
            acceptedRecognition = recognized
        }
        if let recognized = acceptedRecognition {
            current = recognized
            lastRecognitionTimestamp = timestamp
        } else if current?.context.isGameplay == true {
            // Missing HUD detections are not evidence that gameplay ended.
            // A positively scored screen state can release this latch.
            return current
        } else if let lastRecognitionTimestamp,
                  timestamp - lastRecognitionTimestamp > Self.missingCueGrace {
            current = nil
            self.lastRecognitionTimestamp = nil
        }
        return current
    }
}

/// Prevents a one-frame menu stencil false positive from replacing verified
/// gameplay presentation. Real menus remain after the HUD disappears and are
/// admitted once the same scene is seen on two consecutive sampled frames.
struct GameplayMenuEvidenceGate {
    static let hudHoldDuration: TimeInterval = 0.35
    static let requiredConsecutiveMenuFrames = 2

    private var lastHUDTimestamp = -Double.infinity
    private var candidateContext: LabelingContext?
    private var candidateCount = 0
    private var admittedContext: LabelingContext?

    mutating func reset() {
        lastHUDTimestamp = -.infinity
        candidateContext = nil
        candidateCount = 0
        admittedContext = nil
    }

    mutating func admits(
        menuContext: LabelingContext?,
        gameplayLatched: Bool,
        hudHealthMatchCount: Int,
        timestamp: TimeInterval
    ) -> Bool {
        guard timestamp.isFinite else { return false }
        guard gameplayLatched else {
            reset()
            return menuContext != nil
        }
        if hudHealthMatchCount >= 2 {
            lastHUDTimestamp = timestamp
            candidateContext = nil
            candidateCount = 0
            admittedContext = nil
            return false
        }
        if timestamp - lastHUDTimestamp <= Self.hudHoldDuration {
            candidateContext = nil
            candidateCount = 0
            admittedContext = nil
            return false
        }
        guard let menuContext else {
            candidateContext = nil
            candidateCount = 0
            admittedContext = nil
            return false
        }
        if admittedContext == menuContext { return true }
        if candidateContext == menuContext {
            candidateCount += 1
        } else {
            candidateContext = menuContext
            candidateCount = 1
        }
        guard candidateCount >= Self.requiredConsecutiveMenuFrames else {
            return false
        }
        admittedContext = menuContext
        return true
    }
}

enum LiveGameStatusEvidencePolicy {
    /// Menu stencils are strong enough to suspend the slower object model.
    /// The coarse Credits signature is not: gameplay objects must remain able
    /// to disprove it, otherwise a false positive becomes self-sustaining.
    static func shouldRunObjectInference(for status: LiveGameStatus?) -> Bool {
        guard let status else { return true }
        return status.context.isGameplay || status.context == .labelSet(.credits)
    }
}

enum LiveGameStatusResolver {
    static func resolve(
        _ detections: [LiveObjectDetection],
        menuStencil: MenuStencilResult? = nil,
        hudStencil: HUDStencilResult? = nil,
        creditsLikely: Bool = false
    ) -> LiveGameStatus? {
        if let menuStencil, menuStencil.isMatch {
            let context: LiveGameStatus.Context
            let presentation: LiveGameStatus.SelectionPresentation
            switch menuStencil.context {
            case .mainTitle:
                context = .mainTitle
                presentation = .hyphen
            case .selectProfile:
                context = .selectProfile
                presentation = .hyphen
            case .game:
                context = .gameplay
                presentation = .hyphen
            default:
                context = .labelSet(menuStencil.context)
                presentation = .hyphen
            }
            return LiveGameStatus(
                state: menuStencil.context.rawValue,
                context: context,
                selectedMenuOption: menuStencil.selectedOption,
                selectionPresentation: presentation
            )
        }
        if hudStencil?.establishesGameplay == true
            || LiveLabelSetScorer.gameplayMatch(detections) != nil {
            return LiveGameStatus(
                state: LabelingContext.game.rawValue,
                context: .gameplay,
                selectedMenuOption: nil,
                selectionPresentation: .hyphen
            )
        }
        if creditsLikely {
            return LiveGameStatus(
                state: LabelingContext.credits.rawValue,
                context: .labelSet(.credits),
                selectedMenuOption: nil,
                selectionPresentation: .hyphen
            )
        }
        return nil
    }

}

struct LiveLabelSetScore: Equatable {
    let context: LabelingContext
    let score: Double
    let matchedObjectCount: Int
    let distinctiveObjectCount: Int
    let missingObjectCount: Int
    let unexpectedObjectCount: Int
}

enum LiveLabelSetScorer {
    /// A detection unique to one screen is worth four points. Objects reused
    /// by multiple screens contribute proportionally less; very common UI
    /// furniture (notably Back and Select Decoration) bottoms out at 0.25.
    private static let uniqueObjectWeight = 4.0
    private static let commonObjectWeightFloor = 0.25
    private static let missingObjectPenaltyScale = 0.20
    static let minimumScore = 2.0
    static let minimumWinningMargin = 0.75
    static let minimumDetectionConfidence = 0.5

    private static let screenMembershipCounts: [String: Int] = {
        var memberships: [String: Set<LabelingContext>] = [:]
        for context in LabelingContext.contractSetCases {
            for definition in context.labels {
                let canonical = LabelingClassIdentity.canonicalIdentifier(definition.id)
                memberships[canonical, default: []].insert(context)
            }
        }
        return memberships.mapValues(\.count)
    }()

    static func evidenceWeight(for classIdentifier: String) -> Double {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        guard let membershipCount = screenMembershipCounts[canonical],
              membershipCount > 0 else {
            // Unknown model output should not overpower known screen evidence.
            return commonObjectWeightFloor
        }
        return max(
            commonObjectWeightFloor,
            uniqueObjectWeight / Double(membershipCount)
        )
    }

    static func scores(_ detections: [LiveObjectDetection]) -> [LiveLabelSetScore] {
        let observed = detections.filter {
            $0.confidence >= minimumDetectionConfidence
        }.reduce(into: [String: Int]()) { result, detection in
            result[LabelingClassIdentity.canonicalIdentifier(
                detection.classIdentifier
            ), default: 0] += 1
        }

        return LabelingContext.contractSetCases.compactMap { context in
            // Credits retains a dedicated visual signature because its sparse
            // labels are weak. Every other screen competes through this score.
            guard context != .credits else { return nil }
            let expectation = expectation(for: context)
            var matched = 0
            var distinctive = 0
            var missing = 0
            var unexpected = 0
            var weightedScore = 0.0

            for (identifier, bounds) in expectation {
                let count = observed[identifier, default: 0]
                let minimum = bounds.minimum ?? 0
                let accepted = bounds.maximum.map { min(count, $0) } ?? count
                let missingCount = max(0, minimum - count)
                let surplusCount = bounds.maximum.map { max(0, count - $0) } ?? 0
                let weight = evidenceWeight(for: identifier)
                matched += accepted
                missing += missingCount
                unexpected += surplusCount
                weightedScore += Double(accepted) * weight
                weightedScore -= Double(missingCount) * weight
                    * missingObjectPenaltyScale
                weightedScore -= Double(surplusCount) * weight
                if screenMembershipCounts[identifier, default: Int.max] <= 2 {
                    distinctive += accepted
                }
            }
            for (identifier, count) in observed where expectation[identifier] == nil {
                unexpected += count
                weightedScore -= Double(count) * evidenceWeight(for: identifier)
            }

            return LiveLabelSetScore(
                context: context,
                score: weightedScore,
                matchedObjectCount: matched,
                distinctiveObjectCount: distinctive,
                missingObjectCount: missing,
                unexpectedObjectCount: unexpected
            )
        }
        .sorted {
            if $0.score == $1.score {
                if $0.distinctiveObjectCount == $1.distinctiveObjectCount {
                    return $0.matchedObjectCount > $1.matchedObjectCount
                }
                return $0.distinctiveObjectCount > $1.distinctiveObjectCount
            }
            return $0.score > $1.score
        }
    }

    static func bestMatch(_ detections: [LiveObjectDetection]) -> LiveLabelSetScore? {
        let ranked = scores(detections)
        guard let best = ranked.first,
              best.score >= minimumScore,
              best.matchedObjectCount >= 2,
              best.distinctiveObjectCount >= 1 else { return nil }
        if ranked.count > 1,
           best.score - ranked[1].score < minimumWinningMargin {
            return nil
        }
        return best
    }

    /// Runtime object inference now owns gameplay only. Menu detections from an
    /// older installed model are ignored instead of competing with stencils.
    static func gameplayMatch(_ detections: [LiveObjectDetection]) -> LiveLabelSetScore? {
        let gameplayDetections = detections.filter {
            LabelingSharedModelPolicy.classIdentifiers.contains(
                LabelingClassIdentity.canonicalIdentifier($0.classIdentifier)
            )
        }
        guard let gameplay = scores(gameplayDetections).first(where: {
            $0.context == .game
        }), gameplay.score >= minimumScore,
        gameplay.matchedObjectCount >= 2,
        gameplay.distinctiveObjectCount >= 1 else { return nil }
        return gameplay
    }

    private static func expectation(
        for context: LabelingContext
    ) -> [String: LabelingOccurrenceBounds] {
        let catalog = LabelingContractCatalog.bundled
        let overrides = catalog.setContract(for: context.storageIdentifier)?
            .occurrenceOverrides ?? [:]
        return context.labels.reduce(into: [:]) { result, definition in
            let canonical = LabelingClassIdentity.canonicalIdentifier(definition.id)
            let override = overrides[canonical]
            let minimum = context == .game ? 0 : (override?.minimum ?? 1)
            let maximum: Int?
            if let overrideMaximum = override?.maximum {
                maximum = overrideMaximum
            } else if let occurrence = catalog.occurrence(for: canonical) {
                maximum = occurrence.allowedCounts?.max()
            } else {
                maximum = catalog.defaultMaximum
            }
            result[canonical] = LabelingOccurrenceBounds(
                minimum: max(result[canonical]?.minimum ?? 0, minimum),
                maximum: maximum
            )
        }
    }
}

enum CreditsVisualSignature {
    /// Credits are a centered bright Gaussian-like field on black and do not
    /// provide enough object labels to score like normal menu sets.
    static func isLikelyCredits(_ image: CGImage) -> Bool {
        let width = 16
        let height = 9
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        guard let context = CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return false }
        context.interpolationQuality = .low
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        func luma(_ x: Int, _ y: Int) -> Double {
            let offset = (y * width + x) * 4
            return (0.2126 * Double(pixels[offset])
                + 0.7152 * Double(pixels[offset + 1])
                + 0.0722 * Double(pixels[offset + 2])) / 255
        }
        let center = (3...5).flatMap { y in (6...9).map { luma($0, y) } }
            .reduce(0, +) / 12
        let corners = [luma(0, 0), luma(15, 0), luma(0, 8), luma(15, 8)]
            .reduce(0, +) / 4
        let middleRing = [luma(3, 2), luma(12, 2), luma(3, 6), luma(12, 6)]
            .reduce(0, +) / 4
        return corners < 0.10 && center > 0.20 && center > middleRing * 1.25
            && middleRing > corners * 1.4
    }
}

enum QuitGameTerminationPolicy {
    static func recognizesExitIntent(
        context: LiveGameStatus.Context?,
        selectedOption: String?
    ) -> Bool {
        context == .labelSet(.quitGame)
            && selectedOption?.caseInsensitiveCompare("Yes") == .orderedSame
    }

    static func shouldAwaitGameExit(
        context: LiveGameStatus.Context?,
        selectedOption: String?,
        event: GamePointerEventKind,
        accepted: Bool
    ) -> Bool {
        accepted
            && recognizesExitIntent(context: context, selectedOption: selectedOption)
            && event == .leftUp
    }

    static func shouldAwaitGameExit(
        context: LiveGameStatus.Context?,
        selectedOption: String?,
        button: RecordedGameButton,
        isPressed: Bool,
        accepted: Bool
    ) -> Bool {
        accepted
            && recognizesExitIntent(context: context, selectedOption: selectedOption)
            && button == .z
            && isPressed
    }
}
