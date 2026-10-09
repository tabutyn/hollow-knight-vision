import CoreGraphics
import Foundation

struct MenuStencilCalibration: Codable {
    static let schemaVersion = 1

    struct Rect: Codable, Equatable {
        let x: Double
        let y: Double
        let width: Double
        let height: Double

        init(_ rect: CGRect) {
            x = rect.minX
            y = rect.minY
            width = rect.width
            height = rect.height
        }

        var cgRect: CGRect {
            CGRect(x: x, y: y, width: width, height: height)
        }
    }

    struct Match: Codable {
        let classIdentifier: String
        let role: String
        let rect: Rect
        let confidence: Double
        let centerError: Double
        let accepted: Bool
    }

    struct State: Codable {
        let selectedIdentifier: String
        let sourceExampleIdentifier: UUID?
        let measured: Bool
        let matches: [Match]
    }

    struct Anchor: Codable {
        let classIdentifier: String
        let rect: Rect
        let evidenceCount: Int
    }

    struct Selector: Codable {
        struct LocalizedPlacement: Codable {
            let leftRect: Rect
            let rightRect: Rect
            let measured: Bool
            let evidenceCount: Int
        }

        let selectedIdentifier: String
        let leftRect: Rect
        let rightRect: Rect
        let measured: Bool
        let evidenceCount: Int
        /// Stable runtime placements keyed by HollowKnightMenuLanguage raw value.
        /// Optional preserves decoding of the original deployed schema.
        let localizedPlacements: [String: LocalizedPlacement]?

        init(
            selectedIdentifier: String,
            leftRect: Rect,
            rightRect: Rect,
            measured: Bool,
            evidenceCount: Int,
            localizedPlacements: [String: LocalizedPlacement]? = nil
        ) {
            self.selectedIdentifier = selectedIdentifier
            self.leftRect = leftRect
            self.rightRect = rightRect
            self.measured = measured
            self.evidenceCount = evidenceCount
            self.localizedPlacements = localizedPlacements
        }

        func placement(for languageIdentifier: String) -> LocalizedPlacement? {
            localizedPlacements?[languageIdentifier]
        }
    }

    struct Scene: Codable {
        let contextIdentifier: String
        let anchors: [Anchor]
        let selectors: [Selector]
        let states: [State]
    }

    let schemaVersion: Int
    let referenceWidth: Int
    let referenceHeight: Int
    let generatedAt: Date
    let scenes: [Scene]
    /// Human correction screenshots already incorporated into selector
    /// geometry. Optional keeps deployed schema-1 files readable.
    let reviewedCorrectionIdentifiers: [UUID]?

    init(
        schemaVersion: Int,
        referenceWidth: Int,
        referenceHeight: Int,
        generatedAt: Date,
        scenes: [Scene],
        reviewedCorrectionIdentifiers: [UUID]? = nil
    ) {
        self.schemaVersion = schemaVersion
        self.referenceWidth = referenceWidth
        self.referenceHeight = referenceHeight
        self.generatedAt = generatedAt
        self.scenes = scenes
        self.reviewedCorrectionIdentifiers = reviewedCorrectionIdentifiers
    }

    func scene(_ context: LabelingContext) -> Scene? {
        scenes.first { $0.contextIdentifier == context.storageIdentifier }
    }

    static func writableURL(fileManager: FileManager = .default) -> URL {
        LabelingExampleStore.defaultRootURL(fileManager: fileManager)
            .deletingLastPathComponent()
            .appendingPathComponent("menu-stencil-positions.json")
    }

    private static let projectMirrorArgumentPrefix =
        "--menu-stencil-project-calibration="

    static func projectMirrorURL(
        arguments: [String] = ProcessInfo.processInfo.arguments
    ) -> URL? {
        guard let value = arguments.first(where: {
            $0.hasPrefix(projectMirrorArgumentPrefix)
        })?.dropFirst(projectMirrorArgumentPrefix.count), !value.isEmpty else {
            return nil
        }
        return URL(fileURLWithPath: String(value)).standardizedFileURL
    }

    func replacingSelector(
        contextIdentifier: String,
        selectedIdentifier: String,
        languageIdentifier: String = HollowKnightMenuLanguage.english.rawValue,
        leftRect: CGRect,
        rightRect: CGRect,
        now: Date = Date()
    ) -> MenuStencilCalibration? {
        var changed = false
        var foundScene = false
        var updatedScenes = scenes.map { scene -> Scene in
            guard scene.contextIdentifier == contextIdentifier else { return scene }
            foundScene = true
            var selectors = scene.selectors
            let replacement: Selector
            if let index = selectors.firstIndex(where: {
                $0.selectedIdentifier == selectedIdentifier
            }) {
                let existing = selectors[index]
                var localized = existing.localizedPlacements ?? [:]
                let existingLanguageEvidence = localized[languageIdentifier]?
                    .evidenceCount
                    ?? (languageIdentifier == HollowKnightMenuLanguage.english.rawValue
                        ? existing.evidenceCount : 0)
                localized[languageIdentifier] = Selector.LocalizedPlacement(
                    leftRect: Rect(leftRect),
                    rightRect: Rect(rightRect),
                    measured: true,
                    evidenceCount: existingLanguageEvidence + 1
                )
                let updatesBase = languageIdentifier
                    == HollowKnightMenuLanguage.english.rawValue
                replacement = Selector(
                    selectedIdentifier: selectedIdentifier,
                    leftRect: updatesBase ? Rect(leftRect) : existing.leftRect,
                    rightRect: updatesBase ? Rect(rightRect) : existing.rightRect,
                    measured: true,
                    evidenceCount: updatesBase
                        ? existing.evidenceCount + 1 : existing.evidenceCount,
                    localizedPlacements: localized
                )
                selectors[index] = replacement
            } else {
                let placement = Selector.LocalizedPlacement(
                    leftRect: Rect(leftRect),
                    rightRect: Rect(rightRect),
                    measured: true,
                    evidenceCount: 1
                )
                replacement = Selector(
                    selectedIdentifier: selectedIdentifier,
                    leftRect: Rect(leftRect),
                    rightRect: Rect(rightRect),
                    measured: true,
                    evidenceCount: languageIdentifier
                        == HollowKnightMenuLanguage.english.rawValue ? 1 : 0,
                    localizedPlacements: [languageIdentifier: placement]
                )
                selectors.append(replacement)
                selectors.sort { $0.selectedIdentifier < $1.selectedIdentifier }
            }
            let updatedStates = scene.states.map { state -> State in
                guard languageIdentifier == HollowKnightMenuLanguage.english.rawValue,
                      state.selectedIdentifier == selectedIdentifier else { return state }
                let matches = state.matches.map { match -> Match in
                    let rect: Rect
                    switch match.role {
                    case "selector-left": rect = Rect(leftRect)
                    case "selector-right": rect = Rect(rightRect)
                    default: return match
                    }
                    return Match(
                        classIdentifier: match.classIdentifier,
                        role: match.role,
                        rect: rect,
                        confidence: match.confidence,
                        centerError: 0,
                        accepted: true
                    )
                }
                return State(
                    selectedIdentifier: state.selectedIdentifier,
                    sourceExampleIdentifier: state.sourceExampleIdentifier,
                    measured: true,
                    matches: matches
                )
            }
            changed = true
            return Scene(
                contextIdentifier: scene.contextIdentifier,
                anchors: scene.anchors,
                selectors: selectors,
                states: updatedStates
            )
        }
        if !foundScene {
            updatedScenes.append(Scene(
                contextIdentifier: contextIdentifier,
                anchors: [],
                selectors: [Selector(
                    selectedIdentifier: selectedIdentifier,
                    leftRect: Rect(leftRect),
                    rightRect: Rect(rightRect),
                    measured: true,
                    evidenceCount: languageIdentifier
                        == HollowKnightMenuLanguage.english.rawValue ? 1 : 0,
                    localizedPlacements: [
                        languageIdentifier: Selector.LocalizedPlacement(
                            leftRect: Rect(leftRect),
                            rightRect: Rect(rightRect),
                            measured: true,
                            evidenceCount: 1
                        )
                    ]
                )],
                states: []
            ))
            updatedScenes.sort { $0.contextIdentifier < $1.contextIdentifier }
            changed = true
        }
        guard changed else { return nil }
        return MenuStencilCalibration(
            schemaVersion: schemaVersion,
            referenceWidth: referenceWidth,
            referenceHeight: referenceHeight,
            generatedAt: now,
            scenes: updatedScenes,
            reviewedCorrectionIdentifiers: reviewedCorrectionIdentifiers
        )
    }

    func recordingReviewedCorrections(
        _ identifiers: Set<UUID>,
        now: Date = Date()
    ) -> MenuStencilCalibration {
        MenuStencilCalibration(
            schemaVersion: schemaVersion,
            referenceWidth: referenceWidth,
            referenceHeight: referenceHeight,
            generatedAt: now,
            scenes: scenes,
            reviewedCorrectionIdentifiers: identifiers.sorted {
                $0.uuidString < $1.uuidString
            }
        )
    }

    func write(to url: URL, fileManager: FileManager = .default) throws {
        try fileManager.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try JSONEncoder.menuCalibration.encode(self).write(to: url, options: .atomic)
    }

    static func loadDefault() -> MenuStencilCalibration? {
        if let writable = load(writableURL()) { return writable }
        return loadBundled()
    }

    static func loadBundled() -> MenuStencilCalibration? {
        let candidates = [
            Bundle.main.url(
                forResource: "menu-stencil-positions", withExtension: "json"
            ),
            Bundle.module.url(
                forResource: "menu-stencil-positions", withExtension: "json"
            ),
        ].compactMap { $0 }
        for url in candidates {
            if let value = load(url) { return value }
        }
        return nil
    }

    static func load(_ url: URL) -> MenuStencilCalibration? {
        guard let data = try? Data(contentsOf: url),
              let value = try? JSONDecoder.menuCalibration.decode(Self.self, from: data),
              value.schemaVersion == schemaVersion else { return nil }
        return value
    }
}

struct MenuStencilCalibrationReport {
    let outputURL: URL
    let sceneCount: Int
    let measuredStateCount: Int
    let derivedStateCount: Int
    let acceptedMatchCount: Int
    let rejectedMatchCount: Int
    let fullScreenCandidateCount: Int
}

enum MenuStencilCalibrationGenerator {
    private struct SearchResult {
        let rect: CGRect
        let confidence: Double
        let candidateCount: Int
    }

    private struct ShortlistedCandidate {
        let x: Int
        let y: Int
        let error: Double
    }

    static func run(examplesRootURL: URL, outputURL: URL) throws
        -> MenuStencilCalibrationReport {
        let store = LabelingExampleStore(rootURL: examplesRootURL)
        let examples = try store.loadExamples()
        let liveCaptures = try MenuStencilLiveCaptureStore().load()
        let catalog = try MenuStencilCatalog(
            examplesRootURL: examplesRootURL,
            calibration: nil
        )
        var outputScenes = [MenuStencilCalibration.Scene]()
        var acceptedCount = 0
        var rejectedCount = 0
        var candidateCount = 0
        var measuredStateCount = 0
        var derivedStateCount = 0

        for scene in catalog.scenes {
            let compatible = examples.filter {
                $0.manifest.contextIdentifier == scene.context.storageIdentifier
                    && $0.manifest.imageWidth == scene.referenceWidth
                    && $0.manifest.imageHeight == scene.referenceHeight
            }
            var exampleBySelection = [String: SavedLabelingExample]()
            for example in compatible {
                guard let selected = selectedIdentifier(
                    in: example,
                    scene: scene
                ) else { continue }
                if let current = exampleBySelection[selected],
                   current.manifest.createdAt >= example.manifest.createdAt {
                    continue
                }
                exampleBySelection[selected] = example
            }
            var liveBySelection = [String: (
                capture: MenuStencilLiveCapture, imageURL: URL
            )]()
            for source in liveCaptures where
                source.capture.contextIdentifier == scene.context.storageIdentifier
                    && source.capture.resolvedLanguageIdentifier
                        == HollowKnightMenuLanguage.english.rawValue
                    && source.capture.imageWidth == scene.referenceWidth
                    && source.capture.imageHeight == scene.referenceHeight
                    && liveBySelection[source.capture.selectedIdentifier] == nil {
                liveBySelection[source.capture.selectedIdentifier] = source
            }

            var states = [MenuStencilCalibration.State]()
            var anchorEvidence = [String: [CGRect]]()
            var selectorEvidence = [String: (left: [CGRect], right: [CGRect])]()
            for option in scene.options {
                let live = liveBySelection[option.classIdentifier]
                let example = exampleBySelection[option.classIdentifier]
                let image: CGImage
                let sourceExampleIdentifier: UUID?
                let selectorAnnotations: [CGRect]
                if let live, let loaded = ImageFileIO.load(live.imageURL) {
                    image = loaded
                    sourceExampleIdentifier = nil
                    let expected = derivedSelectorRects(
                        option: option,
                        scene: scene,
                        anchorBounds: option.bounds
                    )
                    selectorAnnotations = [expected?.left, expected?.right].compactMap { $0 }
                } else if let example {
                    image = try store.loadImage(for: example)
                    sourceExampleIdentifier = example.id
                    selectorAnnotations = selectorBounds(
                        in: example,
                        width: scene.referenceWidth,
                        height: scene.referenceHeight
                    )
                } else {
                    continue
                }
                guard let pixels = ImageStencilPixels(
                    image,
                    referenceWidth: scene.referenceWidth,
                    referenceHeight: scene.referenceHeight,
                    band: 0..<scene.referenceHeight
                ) else { continue }
                var matches = [MenuStencilCalibration.Match]()
                let selectorCenterErrorLimit = live == nil ? 16.0 : 24.0
                for anchor in scene.anchors {
                    guard let found = fullScreenSearch(
                        variants: anchor.variants,
                        pixels: pixels,
                        expected: anchor.bounds
                    ) else { continue }
                    candidateCount += found.candidateCount
                    let error = centerDistance(found.rect, anchor.bounds)
                    let accepted = found.confidence >= MenuStencilTracker.anchorThreshold
                        && error <= 16
                    accepted ? (acceptedCount += 1) : (rejectedCount += 1)
                    if accepted {
                        anchorEvidence[anchor.classIdentifier, default: []].append(found.rect)
                    }
                    matches.append(.init(
                        classIdentifier: anchor.classIdentifier,
                        role: "menu-object",
                        rect: .init(found.rect),
                        confidence: found.confidence,
                        centerError: error,
                        accepted: accepted
                    ))
                }

                let leftVariants = (option.leftSelector?.variants ?? [])
                    + (scene.selectorVariants["left"] ?? [])
                var foundLeftSelector: SearchResult?
                if let expected = selectorAnnotations.first,
                   let found = fullScreenSearch(
                    variants: leftVariants,
                    pixels: pixels,
                    expected: expected
                   ) {
                    foundLeftSelector = found
                    candidateCount += found.candidateCount
                    let error = centerDistance(found.rect, expected)
                    let accepted = found.confidence >= MenuStencilTracker.selectorThreshold
                        && error <= selectorCenterErrorLimit
                    accepted ? (acceptedCount += 1) : (rejectedCount += 1)
                    if accepted {
                        selectorEvidence[option.classIdentifier, default: ([], [])]
                            .left.append(found.rect)
                    }
                    matches.append(.init(
                        classIdentifier: LabelingClassIdentity.selectDecoration,
                        role: "selector-left",
                        rect: .init(found.rect),
                        confidence: found.confidence,
                        centerError: error,
                        accepted: accepted
                    ))
                }
                let rightVariants = (option.rightSelector?.variants ?? [])
                    + (scene.selectorVariants["right"] ?? [])
                if let expected = selectorAnnotations.last,
                   let found = fullScreenSearch(
                    variants: rightVariants,
                    pixels: pixels,
                    expected: expected,
                    alignedWith: foundLeftSelector?.rect
                   ) {
                    candidateCount += found.candidateCount
                    let error = centerDistance(found.rect, expected)
                    let rowAligned = foundLeftSelector.map {
                        abs(found.rect.midY - $0.rect.midY) <= 8
                            && found.rect.minX > $0.rect.maxX
                            && found.rect.maxX - $0.rect.minX <= 450
                    } ?? false
                    let accepted = found.confidence >= MenuStencilTracker.selectorThreshold
                        && (error <= selectorCenterErrorLimit || rowAligned)
                    accepted ? (acceptedCount += 1) : (rejectedCount += 1)
                    if accepted {
                        selectorEvidence[option.classIdentifier, default: ([], [])]
                            .right.append(found.rect)
                    }
                    matches.append(.init(
                        classIdentifier: LabelingClassIdentity.selectDecoration,
                        role: "selector-right",
                        rect: .init(found.rect),
                        confidence: found.confidence,
                        centerError: error,
                        accepted: accepted
                    ))
                }
                states.append(.init(
                    selectedIdentifier: option.classIdentifier,
                    sourceExampleIdentifier: sourceExampleIdentifier,
                    measured: true,
                    matches: matches
                ))
            }

            let anchors = scene.anchors.map { anchor in
                let evidence = anchorEvidence[anchor.classIdentifier] ?? []
                return MenuStencilCalibration.Anchor(
                    classIdentifier: anchor.classIdentifier,
                    rect: .init(medianRect(evidence) ?? anchor.bounds),
                    evidenceCount: evidence.count
                )
            }
            let anchorBounds = Dictionary(uniqueKeysWithValues: anchors.map {
                ($0.classIdentifier, $0.rect.cgRect)
            })
            var selectors = [MenuStencilCalibration.Selector]()
            for option in scene.options {
                let evidence = selectorEvidence[option.classIdentifier] ?? ([], [])
                let measuredLeft = medianRect(evidence.left)
                let measuredRight = medianRect(evidence.right)
                let fallback = derivedSelectorRects(
                    option: option,
                    scene: scene,
                    anchorBounds: anchorBounds[option.classIdentifier] ?? option.bounds
                )
                guard let left = measuredLeft ?? fallback?.left,
                      let right = measuredRight ?? fallback?.right else { continue }
                let measured = measuredLeft != nil && measuredRight != nil
                if measured {
                    measuredStateCount += 1
                }
                selectors.append(.init(
                    selectedIdentifier: option.classIdentifier,
                    leftRect: .init(left),
                    rightRect: .init(right),
                    measured: measured,
                    evidenceCount: min(evidence.left.count, evidence.right.count)
                ))
                if !measured {
                    derivedStateCount += 1
                    if let index = states.firstIndex(where: {
                        $0.selectedIdentifier == option.classIdentifier
                    }) {
                        let rejectedAudit = states[index]
                        states[index] = .init(
                            selectedIdentifier: rejectedAudit.selectedIdentifier,
                            sourceExampleIdentifier: rejectedAudit.sourceExampleIdentifier,
                            measured: false,
                            matches: rejectedAudit.matches
                        )
                    } else {
                        states.append(.init(
                            selectedIdentifier: option.classIdentifier,
                            sourceExampleIdentifier: nil,
                            measured: false,
                            matches: anchors.map {
                                MenuStencilCalibration.Match(
                                    classIdentifier: $0.classIdentifier,
                                    role: "menu-object",
                                    rect: $0.rect,
                                    confidence: 1,
                                    centerError: 0,
                                    accepted: true
                                )
                            } + [
                                .init(
                                    classIdentifier: LabelingClassIdentity.selectDecoration,
                                    role: "selector-left", rect: .init(left),
                                    confidence: 0, centerError: 0, accepted: false
                                ),
                                .init(
                                    classIdentifier: LabelingClassIdentity.selectDecoration,
                                    role: "selector-right", rect: .init(right),
                                    confidence: 0, centerError: 0, accepted: false
                                ),
                            ]
                        ))
                    }
                }
            }
            outputScenes.append(.init(
                contextIdentifier: scene.context.storageIdentifier,
                anchors: anchors,
                selectors: selectors,
                states: states.sorted { $0.selectedIdentifier < $1.selectedIdentifier }
            ))
        }

        let calibration = MenuStencilCalibration(
            schemaVersion: MenuStencilCalibration.schemaVersion,
            referenceWidth: catalog.referenceWidth,
            referenceHeight: catalog.referenceHeight,
            generatedAt: Date(),
            scenes: outputScenes.sorted { $0.contextIdentifier < $1.contextIdentifier }
        )
        let encoder = JSONEncoder.menuCalibration
        let data = try encoder.encode(calibration)
        try FileManager.default.createDirectory(
            at: outputURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try data.write(to: outputURL, options: .atomic)
        return MenuStencilCalibrationReport(
            outputURL: outputURL,
            sceneCount: outputScenes.count,
            measuredStateCount: measuredStateCount,
            derivedStateCount: derivedStateCount,
            acceptedMatchCount: acceptedCount,
            rejectedMatchCount: rejectedCount,
            fullScreenCandidateCount: candidateCount
        )
    }

    private static func fullScreenSearch(
        variants: [MenuStencilCatalog.Variant],
        pixels: ImageStencilPixels,
        expected: CGRect,
        alignedWith: CGRect? = nil
    ) -> SearchResult? {
        var best: SearchResult?
        var bestInsideExpectedRegion: SearchResult?
        var bestAlignedWithPair: SearchResult?
        var totalCandidates = 0
        for variant in variants where !variant.kernel.samples.isEmpty {
            let kernel = variant.kernel
            let maxX = pixels.width - kernel.width
            let maxY = pixels.height - kernel.height
            guard maxX >= 0, maxY >= 0 else { continue }
            let signature = signatureSamples(kernel)
            guard !signature.isEmpty else { continue }
            var shortlist = [ShortlistedCandidate]()
            let limit = 192
            var worstError = Double.infinity
            var worstIndex = 0
            for y in 0...maxY {
                for x in 0...maxX {
                    totalCandidates += 1
                    var error = 0.0
                    let rejectionLimit = shortlist.count == limit
                        ? worstError * Double(signature.count * 3)
                        : .infinity
                    var rejectedEarly = false
                    for sample in signature {
                        let index = ((y + sample.y) * pixels.width + x + sample.x) * 4
                        error += abs(sample.red - Double(pixels.bytes[index]))
                            + abs(sample.green - Double(pixels.bytes[index + 1]))
                            + abs(sample.blue - Double(pixels.bytes[index + 2]))
                        if error >= rejectionLimit {
                            rejectedEarly = true
                            break
                        }
                    }
                    if rejectedEarly { continue }
                    error /= Double(signature.count * 3)
                    if shortlist.count < limit {
                        shortlist.append(.init(x: x, y: y, error: error))
                        if shortlist.count == limit {
                            (worstIndex, worstError) = worst(in: shortlist)
                        }
                    } else if error < worstError {
                        shortlist[worstIndex] = .init(x: x, y: y, error: error)
                        (worstIndex, worstError) = worst(in: shortlist)
                    }
                }
            }
            var visited = Set<Int>()
            for candidate in shortlist {
                for dy in -3...3 {
                    for dx in -3...3 {
                        let x = candidate.x + dx
                        let y = candidate.y + dy
                        guard x >= 0, x <= maxX, y >= 0, y <= maxY else { continue }
                        let key = y * pixels.width + x
                        guard visited.insert(key).inserted else { continue }
                        totalCandidates += 1
                        let rect = CGRect(
                            x: x, y: y, width: kernel.width, height: kernel.height
                        )
                        guard let comparison = pixels.compare(kernel, at: rect) else { continue }
                        let confidence = comparison.confidence(
                            correlationWeight: 0.82,
                            colorErrorScale: 105
                        )
                        let currentConfidence = best?.confidence ?? -.infinity
                        let isClearWinner = confidence > currentConfidence + 0.02
                        let isComparableButCloser = confidence >= currentConfidence - 0.02
                            && best.map {
                                centerDistance(rect, expected)
                                    < centerDistance($0.rect, expected)
                            } ?? true
                        if isClearWinner || isComparableButCloser {
                            best = SearchResult(
                                rect: rect,
                                confidence: confidence,
                                candidateCount: 0
                            )
                        }
                        if centerDistance(rect, expected) <= 24 {
                            let current = bestInsideExpectedRegion
                            let betterConfidence = confidence > (current?.confidence ?? -.infinity)
                            let comparableButCloser = confidence
                                >= (current?.confidence ?? -.infinity) - 0.02
                                && current.map {
                                    centerDistance(rect, expected)
                                        < centerDistance($0.rect, expected)
                                } ?? true
                            if betterConfidence || comparableButCloser {
                                bestInsideExpectedRegion = SearchResult(
                                    rect: rect,
                                    confidence: confidence,
                                    candidateCount: 0
                                )
                            }
                        }
                        if let alignedWith,
                           abs(rect.midY - alignedWith.midY) <= 8,
                           rect.minX > alignedWith.maxX,
                           rect.maxX - alignedWith.minX <= 450,
                           confidence > (bestAlignedWithPair?.confidence ?? -.infinity) {
                            bestAlignedWithPair = SearchResult(
                                rect: rect,
                                confidence: confidence,
                                candidateCount: 0
                            )
                        }
                    }
                }
            }
            // The complete signature pass still determines the global result.
            // Human geometry is a validation prior: fully score its small
            // neighborhood even when background lookalikes displaced the true
            // decoration from the 192-candidate global shortlist.
            let expectedX = Int(expected.minX.rounded())
            let expectedY = Int(expected.minY.rounded())
            let localMinX = max(0, expectedX - 24)
            let localMaxX = min(maxX, expectedX + 24)
            let localMinY = max(0, expectedY - 24)
            let localMaxY = min(maxY, expectedY + 24)
            if localMinX <= localMaxX, localMinY <= localMaxY {
                for y in localMinY...localMaxY {
                    for x in localMinX...localMaxX {
                        let key = y * pixels.width + x
                        guard visited.insert(key).inserted else { continue }
                        totalCandidates += 1
                        let rect = CGRect(
                            x: x, y: y, width: kernel.width, height: kernel.height
                        )
                        guard let comparison = pixels.compare(kernel, at: rect) else {
                            continue
                        }
                        let confidence = comparison.confidence(
                            correlationWeight: 0.82,
                            colorErrorScale: 105
                        )
                        let current = bestInsideExpectedRegion
                        let betterConfidence = confidence
                            > (current?.confidence ?? -.infinity)
                        let comparableButCloser = confidence
                            >= (current?.confidence ?? -.infinity) - 0.02
                            && current.map {
                                centerDistance(rect, expected)
                                    < centerDistance($0.rect, expected)
                            } ?? true
                        if betterConfidence || comparableButCloser {
                            bestInsideExpectedRegion = SearchResult(
                                rect: rect,
                                confidence: confidence,
                                candidateCount: 0
                            )
                        }
                        if let alignedWith,
                           abs(rect.midY - alignedWith.midY) <= 8,
                           rect.minX > alignedWith.maxX,
                           rect.maxX - alignedWith.minX <= 450,
                           confidence > (bestAlignedWithPair?.confidence ?? -.infinity) {
                            bestAlignedWithPair = SearchResult(
                                rect: rect,
                                confidence: confidence,
                                candidateCount: 0
                            )
                        }
                    }
                }
            }
        }
        guard let best = bestAlignedWithPair
            ?? bestInsideExpectedRegion.flatMap({ $0.confidence >= 0.4 ? $0 : nil })
            ?? best else { return nil }
        return SearchResult(
            rect: best.rect,
            confidence: best.confidence,
            candidateCount: totalCandidates
        )
    }

    private static func signatureSamples(
        _ kernel: ImageStencilKernel
    ) -> [ImageStencilKernel.Sample] {
        let mean = kernel.sum / Double(max(1, kernel.samples.count))
        let ranked = kernel.samples.sorted {
            abs($0.luma - mean) > abs($1.luma - mean)
        }
        var selected = [ImageStencilKernel.Sample]()
        for sample in ranked {
            if selected.allSatisfy({
                abs($0.x - sample.x) + abs($0.y - sample.y) >= 3
            }) {
                selected.append(sample)
                if selected.count == 24 { break }
            }
        }
        return selected.isEmpty ? Array(kernel.samples.prefix(24)) : selected
    }

    private static func worst(
        in candidates: [ShortlistedCandidate]
    ) -> (index: Int, error: Double) {
        candidates.enumerated().max { $0.element.error < $1.element.error }
            .map { ($0.offset, $0.element.error) } ?? (0, .infinity)
    }

    private static func selectedIdentifier(
        in example: SavedLabelingExample,
        scene: MenuStencilCatalog.Scene
    ) -> String? {
        let selectors = example.manifest.annotations.filter {
            !$0.isHardNegative && LabelingClassIdentity.matches(
                $0.classIdentifier,
                LabelingClassIdentity.selectDecoration
            )
        }
        guard selectors.count >= 2 else { return nil }
        let centerY = selectors.map { $0.y + $0.height * 0.5 }
            .reduce(0, +) / Double(selectors.count)
        let selectorX = selectors.map { $0.x + $0.width * 0.5 }.sorted()
        let leftX = selectorX.first!
        let rightX = selectorX.last!
        let selected = example.manifest.annotations.filter {
            !$0.isHardNegative
                && !LabelingClassIdentity.matches(
                    $0.classIdentifier,
                    LabelingClassIdentity.selectDecoration
                )
                && $0.width * $0.height <= 5_000.0 / Double(
                    scene.referenceWidth * scene.referenceHeight
                )
                && $0.x > leftX
                && $0.x + $0.width < rightX
        }.min {
            abs($0.y + $0.height * 0.5 - centerY)
                < abs($1.y + $1.height * 0.5 - centerY)
        }.map {
            LabelingClassIdentity.canonicalIdentifier($0.classIdentifier)
        }
        guard let selected,
              scene.options.contains(where: { $0.classIdentifier == selected })
        else { return nil }
        return selected
    }

    private static func selectorBounds(
        in example: SavedLabelingExample,
        width: Int,
        height: Int
    ) -> [CGRect] {
        example.manifest.annotations.filter {
            !$0.isHardNegative && LabelingClassIdentity.matches(
                $0.classIdentifier,
                LabelingClassIdentity.selectDecoration
            )
        }.map {
            CGRect(
                x: $0.x * Double(width),
                y: $0.y * Double(height),
                width: $0.width * Double(width),
                height: $0.height * Double(height)
            )
        }.sorted { $0.midX < $1.midX }
    }

    private static func derivedSelectorRects(
        option: MenuStencilCatalog.Option,
        scene: MenuStencilCatalog.Scene,
        anchorBounds: CGRect
    ) -> (left: CGRect, right: CGRect)? {
        if let left = option.leftSelector?.bounds,
           let right = option.rightSelector?.bounds {
            return (left, right)
        }
        guard let geometry = option.selectorGeometry ?? scene.selectorGeometry else {
            return nil
        }
        return (
            CGRect(
                x: anchorBounds.minX - geometry.leftGap - geometry.leftWidth,
                y: anchorBounds.midY + geometry.leftCenterYOffset
                    - geometry.leftHeight * 0.5,
                width: geometry.leftWidth,
                height: geometry.leftHeight
            ),
            CGRect(
                x: anchorBounds.maxX + geometry.rightGap,
                y: anchorBounds.midY + geometry.rightCenterYOffset
                    - geometry.rightHeight * 0.5,
                width: geometry.rightWidth,
                height: geometry.rightHeight
            )
        )
    }

    private static func centerDistance(_ left: CGRect, _ right: CGRect) -> Double {
        hypot(Double(left.midX - right.midX), Double(left.midY - right.midY))
    }

    private static func medianRect(_ rects: [CGRect]) -> CGRect? {
        guard !rects.isEmpty else { return nil }
        return CGRect(
            x: median(rects.map(\.minX)),
            y: median(rects.map(\.minY)),
            width: median(rects.map(\.width)),
            height: median(rects.map(\.height))
        )
    }

    private static func median(_ values: [CGFloat]) -> CGFloat {
        let sorted = values.sorted()
        let middle = sorted.count / 2
        return sorted.count.isMultiple(of: 2)
            ? (sorted[middle - 1] + sorted[middle]) * 0.5
            : sorted[middle]
    }
}

private extension JSONEncoder {
    static var menuCalibration: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return encoder
    }
}

private extension JSONDecoder {
    static var menuCalibration: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}
