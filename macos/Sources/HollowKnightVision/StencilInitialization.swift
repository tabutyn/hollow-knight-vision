import Combine
import Foundation
import SwiftUI

struct StencilInitializationStep: Identifiable, Equatable {
    let context: LabelingContext
    let classIdentifier: String
    let className: String
    let occurrence: Int
    let requiredCount: Int

    var id: String {
        "\(context.storageIdentifier):\(classIdentifier):\(occurrence)"
    }

    var instruction: String {
        requiredCount == 1
            ? "Draw a box around \(className)."
            : "Draw box \(occurrence) of \(requiredCount) around \(className)."
    }
}

enum StencilInitializationPolicy {
    static let deferredProfileClass = "select-profile.clear-save"

    static func isDeferred(_ step: StencilInitializationStep) -> Bool {
        step.classIdentifier == deferredProfileClass
    }

    static func isFirstPhaseStep(_ step: StencilInitializationStep) -> Bool {
        firstPhaseContexts.contains(step.context) && !isDeferred(step)
    }

    static func phase(for step: StencilInitializationStep) -> Int {
        isDeferred(step) ? 3 : phase(for: step.context)
    }

    static let firstPhaseContexts: [LabelingContext] = [.mainTitle, .selectProfile, .gameplayHUD]

    static func phase(for context: LabelingContext) -> Int {
        firstPhaseContexts.contains(context) ? 1 : context == .clearSave ? 3 : 2
    }

    // Keep the complete contract catalog, ordered by campaign phase.
    static let requiredContexts: [LabelingContext] = {
        let contexts = LabelingContractCatalog.bundled.labelSets.compactMap { contract -> LabelingContext? in
            guard contract.requiresAllContextObjects, contract.contextIdentifier != "game" else { return nil }
            return LabelingContext(storageIdentifier: contract.contextIdentifier)
        }
        let first = firstPhaseContexts
        return first.filter(contexts.contains) + contexts.filter { !first.contains($0) && $0 != .clearSave }
            + contexts.filter { $0 == .clearSave }
    }()

    static func contexts(for mode: HollowKnightRuntimeMode) -> [LabelingContext] {
        requiredContexts.filter { mode == .modded || $0 != .mods }
    }

    static func requiredCounts(
        context: LabelingContext,
        mode: HollowKnightRuntimeMode,
        catalog: LabelingContractCatalog = .bundled
    ) -> [(definition: LabelingClassDefinition, count: Int)] {
        let contract = catalog.setContract(for: context.storageIdentifier)
        return context.labels.compactMap { definition in
            let identifier = LabelingClassIdentity.canonicalIdentifier(definition.id)
            if mode == .normal && identifier == LabelingClassIdentity.mods {
                return nil
            }
            let override = contract?.occurrenceOverrides?[identifier]
            // Optional for a fresh-save recognition contract, required later in the campaign.
            let minimum = identifier == deferredProfileClass ? 1
                : override?.minimum ?? (contract?.requiresAllContextObjects == true ? 1 : 0)
            return minimum > 0 ? (definition, minimum) : nil
        }
    }

    static func missingSteps(
        examples: [SavedLabelingExample],
        languageIdentifier: String,
        mode: HollowKnightRuntimeMode,
        requiredSize: (width: Int, height: Int) = (640, 360)
    ) -> [StencilInitializationStep] {
        contexts(for: mode).flatMap { context -> [StencilInitializationStep] in
            let requirements = requiredCounts(context: context, mode: mode)
            let bestCounts = representativeExample(
                examples: examples, context: context, languageIdentifier: languageIdentifier,
                mode: mode, requiredSize: requiredSize
            ).map(positiveCounts) ?? [:]
            return requirements.flatMap { requirement -> [StencilInitializationStep] in
                let identifier = LabelingClassIdentity.canonicalIdentifier(
                    requirement.definition.id
                )
                let present = min(requirement.count, bestCounts[identifier, default: 0])
                guard present < requirement.count else { return [] }
                return ((present + 1)...requirement.count).map { occurrence in
                    StencilInitializationStep(
                        context: context,
                        classIdentifier: identifier,
                        className: requirement.definition.name,
                        occurrence: occurrence,
                        requiredCount: requirement.count
                    )
                }
            }
        }.enumerated().sorted {
            let left = phase(for: $0.element), right = phase(for: $1.element)
            return left == right ? $0.offset < $1.offset : left < right
        }.map(\.element)
    }

    static func representativeExample(
        examples: [SavedLabelingExample], context: LabelingContext,
        languageIdentifier: String, mode: HollowKnightRuntimeMode,
        requiredSize: (width: Int, height: Int) = (640, 360)
    ) -> SavedLabelingExample? {
        let requirements = requiredCounts(context: context, mode: mode)
        return examples.filter {
            $0.manifest.contextIdentifier == context.storageIdentifier
                && $0.manifest.resolvedLanguageIdentifier == languageIdentifier
                && $0.manifest.imageWidth == requiredSize.width
                && $0.manifest.imageHeight == requiredSize.height
        }.sorted {
            let lhs = score(positiveCounts($0), requirements: requirements)
            let rhs = score(positiveCounts($1), requirements: requirements)
            if lhs != rhs { return lhs > rhs }
            if $0.manifest.createdAt != $1.manifest.createdAt {
                return $0.manifest.createdAt > $1.manifest.createdAt
            }
            return $0.id.uuidString < $1.id.uuidString
        }.first
    }

    static func acceptsMenuExample(
        _ example: SavedLabelingExample,
        catalog: LabelingContractCatalog = .bundled
    ) -> Bool {
        guard let context = LabelingContext(
            storageIdentifier: example.manifest.contextIdentifier
        ) else { return false }
        if LabelingContractEvaluator.evaluate(example, catalog: catalog).allSatisfy({
            $0.isSatisfied
        }) {
            return true
        }
        guard context == .options else { return false }
        let counts = positiveCounts(example)
        return requiredCounts(context: context, mode: .normal, catalog: catalog).allSatisfy {
            counts[LabelingClassIdentity.canonicalIdentifier($0.definition.id), default: 0]
                >= $0.count
        }
    }

    static func positiveCounts(
        _ example: SavedLabelingExample
    ) -> [String: Int] {
        var counts = [String: Int]()
        for annotation in example.manifest.annotations where !annotation.isHardNegative {
            let identifier = LabelingClassIdentity.canonicalIdentifier(
                annotation.classIdentifier
            )
            counts[identifier, default: 0] += 1
        }
        return counts
    }

    static func score(
        _ counts: [String: Int],
        requirements: [(definition: LabelingClassDefinition, count: Int)]
    ) -> Int {
        requirements.reduce(0) { total, requirement in
            let identifier = LabelingClassIdentity.canonicalIdentifier(
                requirement.definition.id
            )
            return total + min(requirement.count, counts[identifier, default: 0])
        }
    }
}

@MainActor
final class StencilInitializationCoordinator: ObservableObject {
    private static let languageKey = "HollowKnightVision.initialization.language.v1"
    private let store: LabelingExampleStore
    private let defaults: UserDefaults

    @Published var language: HollowKnightMenuLanguage {
        didSet {
            defaults.set(language.rawValue, forKey: Self.languageKey)
            refresh(mode: mode)
        }
    }
    @Published private(set) var examples = [SavedLabelingExample]()
    @Published private(set) var steps = [StencilInitializationStep]()
    @Published private(set) var issue: String?
    @Published private(set) var hasUsableHUD = false
    private var mode: HollowKnightRuntimeMode = .normal

    init(
        store: LabelingExampleStore = LabelingExampleStore(),
        defaults: UserDefaults = .standard
    ) {
        self.store = store
        self.defaults = defaults
        language = defaults.string(forKey: Self.languageKey)
            .flatMap(HollowKnightMenuLanguage.init(rawValue:)) ?? .english
    }

    var isFirstPhaseComplete: Bool {
        issue == nil && !steps.contains(where: StencilInitializationPolicy.isFirstPhaseStep)
            && hasUsableHUD
    }
    var currentPhase: Int { currentStep.map { StencilInitializationPolicy.phase(for: $0) } ?? 3 }

    func isScreenComplete(_ context: LabelingContext) -> Bool {
        issue == nil && !steps.contains { $0.context == context }
            && (context != .gameplayHUD || hasUsableHUD)
    }
    var completedScreenCount: Int {
        guard issue == nil else { return 0 }
        return contexts.filter(isScreenComplete).count
    }
    var contexts: [LabelingContext] { StencilInitializationPolicy.contexts(for: mode) }
    var screenCount: Int { contexts.count }
    var buttonTitle: String {
        isComplete ? text("stencil") : "\(text("stencil")) (\(completedScreenCount) / \(screenCount))"
    }
    func text(_ key: String, _ values: [String: String] = [:]) -> String {
        StencilGuidanceText(language: language).text(key, values)
    }
    func screenName(_ context: LabelingContext) -> String {
        StencilGuidanceText(language: language).screenName(context)
    }
    func example(for context: LabelingContext) -> SavedLabelingExample? {
        StencilInitializationPolicy.representativeExample(
            examples: examples, context: context, languageIdentifier: language.rawValue, mode: mode
        )
    }
    func guidance(labelingContext: LabelingContext?, selectedClassIdentifier: String?,
                  navigationContext: LabelingContext? = nil) -> String? {
        if let issue { return issue }
        if labelingContext == .game {
            let identifier = LabelingFrameOpeningPolicy.classIdentifierForOpening(
                context: .game, annotationClassIdentifiers: [], preferredClassIdentifier: selectedClassIdentifier)
            return text("gameplayLabel", ["object": text(LabelingVisualIdentity.displayName(for: identifier))])
        }
        let words = StencilGuidanceText(language: language)
        if let context = labelingContext,
           contexts.contains(context) {
            if context == .selectProfile && isFirstPhaseComplete && selectedClassIdentifier == "select-profile.clear-save",
               let example = example(for: context),
               StencilInitializationPolicy.positiveCounts(example)["select-profile.clear-save", default: 0] == 0 {
                return text("draw", ["object": text("Clear Save")])
            }
            let remaining = steps.filter {
                $0.context == context && (isFirstPhaseComplete || !StencilInitializationPolicy.isDeferred($0))
            }
            if let step = remaining.first(where: { $0.classIdentifier == selectedClassIdentifier }) ?? remaining.first {
                return words.instruction(step)
            }
            if context == .gameplayHUD && !hasUsableHUD { return text("invalidHUD") }
            return context == .selectProfile && !isScreenComplete(context) ? text("profilePhaseDone")
                : text("screenDone", ["screen": screenName(context)])
        }
        if let context = navigationContext, contexts.contains(context),
           !steps.contains(where: { $0.context == context }) {
            return text("screenReady", ["screen": screenName(context)])
        }
        guard let next = navigationStep(context: navigationContext) else { return nil }
        if StencilInitializationPolicy.isDeferred(next) { return text("profileClearSave") }
        if example(for: next.context) != nil {
            return text("resumeStencil", ["screen": screenName(next.context)])
        }
        if next.context == .gameplayHUD { return text("navigateHUD") }
        if next.context == .clearSave { return text("navigateClearSave") }
        if next.context == .selectProfile {
            return text("selectProfile")
        }
        return text("navigateShort", ["screen": screenName(next.context)])
    }

    var isComplete: Bool { steps.isEmpty && issue == nil && hasUsableHUD }
    var currentStep: StencilInitializationStep? { navigationStep() }

    func navigationStep(context: LabelingContext? = nil) -> StencilInitializationStep? {
        if context == nil && !hasUsableHUD && !steps.contains(where: {
            !StencilInitializationPolicy.isDeferred($0) && ($0.context == .mainTitle || $0.context == .selectProfile)
        }) {
            return steps.first(where: { $0.context == .gameplayHUD }) ?? StencilInitializationStep(
                context: .gameplayHUD, classIdentifier: "game.health", className: "Health", occurrence: 1, requiredCount: 2)
        }
        return steps.first(where: { $0.context == context }) ?? steps.first
    }

    func labelContextForCapture(recognized: LabelingContext?, navigation: LabelingContext?,
                                fallback: LabelingContext) -> LabelingContext {
        // The HUD can remain visible behind an untrained menu. Follow the
        // instruction shown beside L before trusting that incidental match.
        if let navigation, contexts.contains(navigation) { return navigation }
        return navigationStep(context: navigation)?.context ?? recognized ?? fallback
    }

    func modelGuidance(workspace: DashboardWorkspace, hasGameplayLabels: Bool, breaches: Int,
                       modelReady: Bool, training: Bool) -> String? {
        guard isComplete, workspace != .label else { return nil }
        if training { return text("modelTraining") }
        if !hasGameplayLabels { return text("modelLabelGameplay") }
        if breaches > 0 { return text("modelReview", ["count": String(breaches)]) }
        if modelReady { return text("modelReady") }
        return text(workspace == .model ? "modelTrain" : "modelOpen")
    }

    var completedCount: Int {
        max(0, totalRequiredCount - steps.count)
    }
    var totalRequiredCount: Int {
        contexts.reduce(0) { total, context in
            total + StencilInitializationPolicy.requiredCounts(
                context: context,
                mode: mode
            ).reduce(0) { $0 + $1.count }
        }
    }

    func refresh(mode: HollowKnightRuntimeMode) {
        self.mode = mode
        do {
            examples = try store.loadExamples()
            steps = StencilInitializationPolicy.missingSteps(
                examples: examples,
                languageIdentifier: language.rawValue,
                mode: mode
            )
            hasUsableHUD = HUDStencilTemplate.captured(examples: examples, languageIdentifier: language.rawValue) != nil
            issue = nil
        } catch {
            hasUsableHUD = false
            examples = []
            steps = []
            issue = error.localizedDescription
        }
    }
}
