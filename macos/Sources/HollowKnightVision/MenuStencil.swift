import CoreGraphics
import Foundation

struct MenuStencilMatch: Equatable {
    let classIdentifier: String
    let name: String
    let rect: CGRect // Core Image bottom-origin output pixels
    let confidence: Double
    let reference: ImageStencilReference?
}

struct MenuStencilSelectorSearchRegion: Equatable {
    enum Side: Hashable {
        case left
        case right
    }

    let optionIdentifier: String
    let optionName: String
    let side: Side
    let rect: CGRect // Core Image bottom-origin output pixels
    let isSolved: Bool
}

struct MenuStencilResult: Equatable {
    enum Phase: String, Equatable {
        case searching
        case tracking
    }

    let context: LabelingContext
    let isMatch: Bool
    let confidence: Double
    let selectedOption: String?
    let anchors: [MenuStencilMatch]
    let selectorCandidates: [MenuStencilMatch]
    let selectorSearchRegions: [MenuStencilSelectorSearchRegion]
    let selectorLanguageEvidenceCount: Int
    let selectorForegroundEvidenceCount: Int
    let sceneEvidenceRatio: Double
    let phase: Phase
    let comparisonCount: Int
    let sourceTimestamp: Double
    /// Language that supplied the text evidence. Nil is reserved for scenes
    /// established only by language-independent decoration pixels.
    let languageIdentifier: String?
}

/// A menu catalog derived from the user's human annotations and the latest
/// clean live capture for each selected row. Human consensus supplies the
/// fallback; live consensus supplies the exact current glyph and decoration
/// pixels used by the fast path.
struct MenuStencilCatalog {
    struct Variant {
        let kernel: ImageStencilKernel
        let reference: ImageStencilReference
        /// Absolute top-origin placement learned for this language. Text
        /// variants use it so a translated word is neither clipped nor scaled
        /// into the English human box.
        let nominalBounds: CGRect?
        /// Nil means the pixels are language-independent, as with selector
        /// decorations. Menu text variants always carry a language.
        let languageIdentifier: String?

        init(
            kernel: ImageStencilKernel,
            reference: ImageStencilReference,
            languageIdentifier: String? = nil,
            nominalBounds: CGRect? = nil
        ) {
            self.kernel = kernel
            self.reference = reference
            self.languageIdentifier = languageIdentifier
            self.nominalBounds = nominalBounds
        }
    }

    struct Anchor {
        let classIdentifier: String
        let name: String
        let bounds: CGRect // top-origin reference pixels
        let calibratedBounds: CGRect?
        let variants: [Variant]
        let probe: Variant
        let probeVariants: [Variant]

        init(
            classIdentifier: String,
            name: String,
            bounds: CGRect,
            calibratedBounds: CGRect?,
            variants: [Variant],
            probe: Variant,
            probeVariants: [Variant] = []
        ) {
            self.classIdentifier = classIdentifier
            self.name = name
            self.bounds = bounds
            self.calibratedBounds = calibratedBounds
            self.variants = variants
            self.probe = probe
            self.probeVariants = probeVariants
        }

        var searchProbeVariants: [Variant] {
            probeVariants.isEmpty ? [probe] : probeVariants
        }
    }

    struct Option {
        let classIdentifier: String
        let name: String
        let bounds: CGRect
        let selectorGeometry: SelectorGeometry?
        let leftSelector: PositionedSelector?
        let rightSelector: PositionedSelector?
    }

    struct PositionedSelector {
        let bounds: CGRect
        let variants: [Variant]
        let localizedBounds: [String: CGRect]
        let idleForegroundMasks: [String: ImageStencilForegroundMask]

        init(
            bounds: CGRect,
            variants: [Variant],
            localizedBounds: [String: CGRect] = [:],
            idleForegroundMasks: [String: ImageStencilForegroundMask] = [:]
        ) {
            self.bounds = bounds
            self.variants = variants
            self.localizedBounds = localizedBounds
            self.idleForegroundMasks = idleForegroundMasks
        }

        func bounds(for languageIdentifier: String?) -> CGRect {
            languageIdentifier.flatMap { localizedBounds[$0] } ?? bounds
        }

        func idleForegroundMask(for languageIdentifier: String?)
            -> ImageStencilForegroundMask? {
            languageIdentifier.flatMap { idleForegroundMasks[$0] }
        }
    }

    struct SelectorGeometry {
        let leftWidth: CGFloat
        let leftHeight: CGFloat
        let leftGap: CGFloat
        let leftCenterYOffset: CGFloat
        let rightWidth: CGFloat
        let rightHeight: CGFloat
        let rightGap: CGFloat
        let rightCenterYOffset: CGFloat
    }

    struct SelectorCorrectionPlacement {
        let context: LabelingContext
        let optionIdentifier: String
        let languageIdentifier: String
        let left: CGRect
        let right: CGRect
        let sourceExampleIdentifier: UUID
    }

    struct Scene {
        let context: LabelingContext
        let referenceWidth: Int
        let referenceHeight: Int
        let probe: Anchor
        let anchors: [Anchor]
        let options: [Option]
        let selectorVariants: [String: [Variant]]
        let selectorGeometry: SelectorGeometry?
    }

    let scenes: [Scene]
    let referenceWidth: Int
    let referenceHeight: Int
    let pixelBand: Range<Int>
    let selectorCorrectionPlacements: [SelectorCorrectionPlacement]

    static let humanLabeled: MenuStencilCatalog? = try? MenuStencilCatalog(
        examplesRootURL: LabelingExampleStore.defaultRootURL(),
        calibration: .loadDefault()
    )

    init(
        examplesRootURL: URL,
        calibration: MenuStencilCalibration? = .loadBundled(),
        liveCapturesOverride: [(
            capture: MenuStencilLiveCapture,
            imageURL: URL
        )]? = nil,
        positiveCaptureHoldoutIDs: Set<UUID> = [],
        includedContexts: Set<LabelingContext>? = nil
    ) throws {
        let store = LabelingExampleStore(rootURL: examplesRootURL)
        let examples = try store.loadExamples()
        let selectorCorrectionExamples = examples.filter {
            Self.isSelectorCorrectionExample($0)
        }
        let liveCaptures = calibration == nil
            ? []
            : (liveCapturesOverride
                ?? ((try? MenuStencilLiveCaptureStore().load()) ?? []))
        var scenes = [Scene]()
        for context in LabelingContext.contractSetCases where
            context != .game && (includedContexts?.contains(context) ?? true) {
            let allContextExamples = examples.filter {
                $0.manifest.contextIdentifier == context.storageIdentifier
                    && !Self.isSelectorCorrectionExample($0)
            }
            let contextExamples = allContextExamples.filter {
                if context == .inventory {
                    return $0.manifest.annotations.contains {
                        !$0.isHardNegative
                            && LabelingClassIdentity.matches(
                                $0.classIdentifier,
                                "inventory.inventory"
                            )
                    }
                }
                return LabelingContractEvaluator.evaluate($0).allSatisfy {
                    $0.isSatisfied
                }
            }
            if let scene = Self.makeScene(
                context: context,
                examples: contextExamples,
                supplementalExamples: allContextExamples.filter { example in
                    !contextExamples.contains { $0.id == example.id }
                },
                store: store,
                calibration: calibration?.scene(context),
                liveCaptures: liveCaptures.filter {
                    $0.capture.contextIdentifier == context.storageIdentifier
                },
                positiveCaptureHoldoutIDs: positiveCaptureHoldoutIDs
            ) {
                scenes.append(scene)
            }
        }
        guard let first = scenes.first else {
            throw MenuStencilCatalogError.noScenes
        }
        let orderedScenes = scenes.sorted {
            $0.context.storageIdentifier < $1.context.storageIdentifier
        }
        referenceWidth = first.referenceWidth
        referenceHeight = first.referenceHeight
        let anchorBounds = scenes.flatMap(\.anchors).map(\.bounds)
        let low = max(0, Int((anchorBounds.map(\.minY).min() ?? 0).rounded(.down)) - 14)
        let high = min(
            referenceHeight,
            Int((anchorBounds.map(\.maxY).max() ?? CGFloat(referenceHeight)).rounded(.up)) + 14
        )
        pixelBand = low..<max(low + 1, high)
        let correctedScenes = Self.applyingSelectorCorrections(
            selectorCorrectionExamples,
            to: orderedScenes,
            store: store,
            referenceWidth: referenceWidth,
            referenceHeight: referenceHeight
        )
        self.scenes = correctedScenes
        selectorCorrectionPlacements = Self.correctionPlacements(
            selectorCorrectionExamples,
            in: correctedScenes
        )
    }

    init(
        scenes: [Scene],
        referenceWidth: Int,
        referenceHeight: Int,
        pixelBand: Range<Int>,
        selectorCorrectionPlacements: [SelectorCorrectionPlacement] = []
    ) {
        self.scenes = scenes
        self.referenceWidth = referenceWidth
        self.referenceHeight = referenceHeight
        self.pixelBand = pixelBand
        self.selectorCorrectionPlacements = selectorCorrectionPlacements
    }

    func containingOnly(_ context: LabelingContext) -> MenuStencilCatalog {
        MenuStencilCatalog(
            scenes: scenes.filter { $0.context == context },
            referenceWidth: referenceWidth,
            referenceHeight: referenceHeight,
            pixelBand: pixelBand,
            selectorCorrectionPlacements: selectorCorrectionPlacements.filter {
                $0.context == context
            }
        )
    }

    /// A user can deliberately save one or two Select Decoration rectangles
    /// in the Main Title set to report a bad runtime selector position. Those
    /// screenshots are geometry corrections, not incomplete Main Title scene
    /// definitions. Keeping them out of scene consensus also prevents a pair
    /// of correction rectangles from removing every Main Title text anchor.
    private static func isSelectorCorrectionExample(
        _ example: SavedLabelingExample
    ) -> Bool {
        let positives = example.manifest.annotations.filter { !$0.isHardNegative }
        return (1...2).contains(positives.count) && positives.allSatisfy {
            LabelingClassIdentity.matches(
                $0.classIdentifier,
                LabelingClassIdentity.selectDecoration
            )
        }
    }

    private static func applyingSelectorCorrections(
        _ examples: [SavedLabelingExample],
        to sourceScenes: [Scene],
        store: LabelingExampleStore,
        referenceWidth: Int,
        referenceHeight: Int
    ) -> [Scene] {
        guard !examples.isEmpty else { return sourceScenes }
        var scenes = sourceScenes
        // Apply old-to-new so a later human correction wins for the same
        // scene, language, option, and side.
        for example in examples.sorted(by: {
            $0.manifest.createdAt < $1.manifest.createdAt
        }) {
            guard let target = selectorCorrectionTargets[example.id] else {
                continue
            }
            guard example.manifest.imageWidth == referenceWidth,
                  example.manifest.imageHeight == referenceHeight,
                  let image = try? store.loadImage(for: example),
                  let sceneIndex = scenes.firstIndex(where: {
                    $0.context == target.context
                  }), let optionIndex = scenes[sceneIndex].options.firstIndex(where: {
                    $0.classIdentifier == target.optionIdentifier
                  })
            else { continue }
            let annotations = example.manifest.annotations.filter {
                !$0.isHardNegative
                    && LabelingClassIdentity.matches(
                        $0.classIdentifier,
                        LabelingClassIdentity.selectDecoration
                    )
            }.map {
                pixelBounds(
                    $0,
                    width: referenceWidth,
                    height: referenceHeight
                )
            }.sorted { $0.midX < $1.midX }
            guard !annotations.isEmpty else { continue }

            let oldScene = scenes[sceneIndex]
            var options = oldScene.options
            let oldOption = options[optionIndex]
            let corrected = correctedOption(
                oldOption,
                annotations: annotations,
                languageIdentifier: target.languageIdentifier,
                image: image,
                referenceWidth: referenceWidth,
                referenceHeight: referenceHeight
            )
            options[optionIndex] = corrected
            scenes[sceneIndex] = Scene(
                context: oldScene.context,
                referenceWidth: oldScene.referenceWidth,
                referenceHeight: oldScene.referenceHeight,
                probe: oldScene.probe,
                anchors: oldScene.anchors,
                options: options,
                selectorVariants: oldScene.selectorVariants,
                selectorGeometry: oldScene.selectorGeometry
            )
        }
        return scenes
    }

    private struct SelectorCorrectionTarget {
        let context: LabelingContext
        let optionIdentifier: String
        let languageIdentifier: String
    }

    private static func correctionPlacements(
        _ examples: [SavedLabelingExample],
        in scenes: [Scene]
    ) -> [SelectorCorrectionPlacement] {
        var placements = [String: SelectorCorrectionPlacement]()
        for example in examples.sorted(by: {
            $0.manifest.createdAt < $1.manifest.createdAt
        }) {
            guard let target = selectorCorrectionTargets[example.id],
                  let option = scenes.first(where: {
                    $0.context == target.context
                  })?.options.first(where: {
                    $0.classIdentifier == target.optionIdentifier
                  }), let left = option.leftSelector?.bounds(
                    for: target.languageIdentifier
                  ), let right = option.rightSelector?.bounds(
                    for: target.languageIdentifier
                  ) else { continue }
            let key = target.context.storageIdentifier + ":"
                + target.optionIdentifier + ":" + target.languageIdentifier
            placements[key] = SelectorCorrectionPlacement(
                context: target.context,
                optionIdentifier: target.optionIdentifier,
                languageIdentifier: target.languageIdentifier,
                left: left,
                right: right,
                sourceExampleIdentifier: example.id
            )
        }
        return placements.sorted { $0.key < $1.key }.map(\.value)
    }

    /// Reviewed Contract Breach screenshots where Select Decoration was used
    /// intentionally as a correction marker. Explicit IDs make the deployment
    /// deterministic and avoid running a full 23-scene search at every launch.
    private static let selectorCorrectionTargets: [
        UUID: SelectorCorrectionTarget
    ] = [
        UUID(uuidString: "5EF5867D-16EC-4202-B9A7-0B273A4CCE12")!:
            .init(context: .options, optionIdentifier: "options.game", languageIdentifier: "fr"),
        UUID(uuidString: "A343B40F-10B9-4992-808E-A4DF90DD60DE")!:
            .init(context: .gameOptions, optionIdentifier: LabelingClassIdentity.back, languageIdentifier: "fr"),
        UUID(uuidString: "7BDBE122-1CF4-48A0-AA47-0DF9DA22E8BD")!:
            .init(context: .gameOptions, optionIdentifier: LabelingClassIdentity.resetDefaults, languageIdentifier: "fr"),
        UUID(uuidString: "9AF0F76E-969F-4A39-BE1F-85DCBFC43FDA")!:
            .init(context: .extras, optionIdentifier: "extras.lifeblood", languageIdentifier: "es"),
        UUID(uuidString: "BA6281F5-61BD-4FDE-A88F-84AF957A0668")!:
            .init(context: .extras, optionIdentifier: "extras.the-grimm-troupe", languageIdentifier: "es"),
        UUID(uuidString: "62D3E3C4-A574-4946-B26D-A646EE6666AE")!:
            .init(context: .extras, optionIdentifier: "extras.hidden-dreams", languageIdentifier: "es"),
        UUID(uuidString: "F44121EB-E362-4ADB-A314-48ECAAE61E28")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.attack, languageIdentifier: "es"),
        UUID(uuidString: "3ABEEF76-A4E8-4BDA-9C0C-5B7F6B503AE2")!:
            .init(context: .options, optionIdentifier: LabelingClassIdentity.controller, languageIdentifier: "es"),
        UUID(uuidString: "8F82F042-B421-4FC4-9BC4-A7CD23D30A52")!:
            .init(context: .video, optionIdentifier: "video.full-screen", languageIdentifier: "es"),
        UUID(uuidString: "F1EE8587-0830-4A26-A3DA-C526267A98CE")!:
            .init(context: .gameOptions, optionIdentifier: LabelingClassIdentity.resetDefaults, languageIdentifier: "es"),
        UUID(uuidString: "FCFC1ADD-C60D-4B30-AC31-E9E42E5C020F")!:
            .init(context: .quitGame, optionIdentifier: LabelingClassIdentity.no, languageIdentifier: "pt-BR"),
        UUID(uuidString: "6E4C3D9F-73CA-49E4-B2F0-C30D3A48DBEE")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.superDash, languageIdentifier: "pt-BR"),
        UUID(uuidString: "997CB3D1-0546-4B2A-9FD4-396DF6CDFC6D")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.attack, languageIdentifier: "pt-BR"),
        UUID(uuidString: "7B18DA08-52B2-40DD-904D-E68412C5DE10")!:
            .init(context: .keyboard, optionIdentifier: "inventory.inventory", languageIdentifier: "pt-BR"),
        UUID(uuidString: "804959CD-CEDE-47EC-B5EC-99B5C1DB24BB")!:
            .init(context: .video, optionIdentifier: LabelingClassIdentity.advancedSettings, languageIdentifier: "pt-BR"),
        UUID(uuidString: "08B7D411-2B22-4BA1-83DD-D35519E8CC21")!:
            .init(context: .video, optionIdentifier: "video.full-screen", languageIdentifier: "pt-BR"),
        UUID(uuidString: "23177404-3D79-4AD0-A2CE-67F6C8CA69D1")!:
            .init(context: .options, optionIdentifier: "options.game", languageIdentifier: "pt-BR"),
        UUID(uuidString: "35F5BDEE-F9C8-4B35-95B2-40AAE1E4EB24")!:
            .init(context: .extras, optionIdentifier: "extras.lifeblood", languageIdentifier: "ko"),
        UUID(uuidString: "577B177F-29D8-4D26-AA5C-EAD1A325841C")!:
            .init(context: .extras, optionIdentifier: "extras.hidden-dreams", languageIdentifier: "ko"),
        UUID(uuidString: "69FA0F82-93F4-49C8-8BE1-2CDDD89748A9")!:
            .init(context: .extras, optionIdentifier: "extras.the-grimm-troupe", languageIdentifier: "ko"),
        UUID(uuidString: "E8FC52A8-3E99-407C-9A83-1D88E7143BF5")!:
            .init(context: .options, optionIdentifier: "options.game", languageIdentifier: "es"),
        UUID(uuidString: "04D40700-0C53-4E5D-B2D8-0DA5C134774D")!:
            .init(context: .options, optionIdentifier: "options.game", languageIdentifier: "fr"),
        UUID(uuidString: "0D565524-8EC3-442C-B8F3-3152A0A0382A")!:
            .init(context: .options, optionIdentifier: "options.game", languageIdentifier: "it"),
        UUID(uuidString: "01A82890-192D-4CD7-BB55-E0D4B27C3CB9")!:
            .init(context: .options, optionIdentifier: LabelingClassIdentity.keyboard, languageIdentifier: "it"),
        UUID(uuidString: "FC571AB9-9AB2-4EE9-BEC0-7257EFF93C1B")!:
            .init(context: .video, optionIdentifier: LabelingClassIdentity.advancedSettings, languageIdentifier: "it"),
        UUID(uuidString: "973AB0CA-EEF2-4902-8B53-E0765479567E")!:
            .init(context: .options, optionIdentifier: "options.game", languageIdentifier: "it"),
        UUID(uuidString: "911E9057-3FE0-452C-BE9C-2D68AD922154")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.attack, languageIdentifier: "it"),
        UUID(uuidString: "AE1F5C8D-6D41-427C-B6BE-DDCC911964BD")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.superDash, languageIdentifier: "it"),
        UUID(uuidString: "D57CC45F-DE6A-4BA6-A25F-FDB67B3ADEEA")!:
            .init(context: .keyboard, optionIdentifier: "inventory.inventory", languageIdentifier: "it"),
        UUID(uuidString: "FA0753AB-7E1F-4024-A1EF-F5E651F51B93")!:
            .init(context: .options, optionIdentifier: "options.game", languageIdentifier: "ja"),
        UUID(uuidString: "152AE0A8-198B-4596-A705-DED146B7883E")!:
            .init(context: .extras, optionIdentifier: "extras.hidden-dreams", languageIdentifier: "ja"),
        UUID(uuidString: "E5616482-3FF6-4842-B313-DFB50C7783BC")!:
            .init(context: .extras, optionIdentifier: "extras.the-grimm-troupe", languageIdentifier: "ja"),
        UUID(uuidString: "6D028D16-970D-4755-845B-34DCBF91A247")!:
            .init(context: .extras, optionIdentifier: "extras.lifeblood", languageIdentifier: "ja"),
        UUID(uuidString: "34021979-272E-4680-84E7-2D5BDD35555C")!:
            .init(context: .video, optionIdentifier: LabelingClassIdentity.advancedSettings, languageIdentifier: "ja"),
        UUID(uuidString: "5393B426-F329-4E86-80D2-5E104C36A746")!:
            .init(context: .extras, optionIdentifier: "extras.hidden-dreams", languageIdentifier: "ja"),
        UUID(uuidString: "7B00F837-FC48-46FC-9AF2-9C19C471ED3E")!:
            .init(context: .extras, optionIdentifier: "extras.the-grimm-troupe", languageIdentifier: "ja"),
        UUID(uuidString: "07A600CE-FC9A-42FD-AA72-6E54D0412971")!:
            .init(context: .extras, optionIdentifier: "extras.lifeblood", languageIdentifier: "ja"),
        UUID(uuidString: "D3BF03A8-98EC-4CEA-9696-B647C674221E")!:
            .init(context: .quitGame, optionIdentifier: LabelingClassIdentity.no, languageIdentifier: "ja"),
        UUID(uuidString: "00B9FE92-B22D-400A-947C-03848590DBBF")!:
            .init(context: .video, optionIdentifier: LabelingClassIdentity.advancedSettings, languageIdentifier: "ja"),
        UUID(uuidString: "AA6813FA-8B96-4B2C-B499-0A3F46F745C6")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.attack, languageIdentifier: "ko"),
        UUID(uuidString: "3224CB73-47A1-4F4B-B4AD-21EE272B4B81")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.attack, languageIdentifier: "ko"),
        UUID(uuidString: "16A25BAA-201A-40AD-ABC2-28F97683D505")!:
            .init(context: .extras, optionIdentifier: "extras.lifeblood", languageIdentifier: "ko"),
        UUID(uuidString: "144A8CFB-9C77-4D92-A621-D7780B96335F")!:
            .init(context: .extras, optionIdentifier: "extras.the-grimm-troupe", languageIdentifier: "ko"),
        UUID(uuidString: "66CCC048-781A-4D48-8D80-6D58A1CA9530")!:
            .init(context: .video, optionIdentifier: LabelingClassIdentity.advancedSettings, languageIdentifier: "ko"),
        UUID(uuidString: "F17C0BF8-9346-47B7-B0F2-CBCD9436E61F")!:
            .init(context: .keyboard, optionIdentifier: "keyboard.right", languageIdentifier: "ko"),
        UUID(uuidString: "BB7C33FD-0000-41D3-A25F-525B663772D2")!:
            .init(context: .keyboard, optionIdentifier: LabelingClassIdentity.superDash, languageIdentifier: "ko"),
    ]

    private static func correctedOption(
        _ option: Option,
        annotations: [CGRect],
        languageIdentifier: String,
        image: CGImage,
        referenceWidth: Int,
        referenceHeight: Int
    ) -> Option {
        guard let pixels = ImageStencilPixels(
            image,
            referenceWidth: referenceWidth,
            referenceHeight: referenceHeight,
            band: 0..<referenceHeight
        ) else { return option }

        func correctedSelector(
            _ selector: PositionedSelector?,
            bounds: CGRect?
        ) -> PositionedSelector? {
            guard let selector, let bounds else { return selector }
            var localized = selector.localizedBounds
            localized[languageIdentifier] = bounds
            var variants = selector.variants
            if let reference = pixels.reference(in: bounds) {
                variants.append(makeVariant(
                    kind: "human-selector-correction.\(languageIdentifier)",
                    reference: reference,
                    sampleStride: 1,
                    languageIdentifier: languageIdentifier
                ))
            }
            return PositionedSelector(
                bounds: selector.bounds,
                variants: variants,
                localizedBounds: localized,
                idleForegroundMasks: selector.idleForegroundMasks
            )
        }

        let leftBounds: CGRect?
        let rightBounds: CGRect?
        if annotations.count >= 2 {
            leftBounds = annotations.first
            rightBounds = annotations.last
        } else if let annotation = annotations.first,
                  let left = option.leftSelector?.bounds(for: languageIdentifier),
                  let right = option.rightSelector?.bounds(for: languageIdentifier) {
            if abs(annotation.midX - left.midX) <= abs(annotation.midX - right.midX) {
                leftBounds = annotation
                rightBounds = nil
            } else {
                leftBounds = nil
                rightBounds = annotation
            }
        } else {
            leftBounds = nil
            rightBounds = nil
        }
        return Option(
            classIdentifier: option.classIdentifier,
            name: option.name,
            bounds: option.bounds,
            selectorGeometry: option.selectorGeometry,
            leftSelector: correctedSelector(option.leftSelector, bounds: leftBounds),
            rightSelector: correctedSelector(option.rightSelector, bounds: rightBounds)
        )
    }

    private static func expandedStencilBounds(
        _ bounds: CGRect,
        context: LabelingContext,
        classIdentifier: String
    ) -> CGRect {
        guard context == .quitGame,
              classIdentifier == "quit-game.quit-game" else { return bounds }
        // The repeated draft annotations ended immediately after "GAME".
        // The current 640x360 source pixels extend seven columns farther
        // through the question mark (x 278..<362). Include the complete title
        // in both the consensus stencil and its visible verification box.
        return CGRect(
            x: bounds.minX,
            y: bounds.minY,
            width: bounds.width + 7,
            height: bounds.height
        )
    }

    private static func makeScene(
        context: LabelingContext,
        examples: [SavedLabelingExample],
        supplementalExamples: [SavedLabelingExample],
        store: LabelingExampleStore,
        calibration: MenuStencilCalibration.Scene?,
        liveCaptures: [(capture: MenuStencilLiveCapture, imageURL: URL)],
        positiveCaptureHoldoutIDs: Set<UUID>
    ) -> Scene? {
        guard !examples.isEmpty,
              let width = examples.map(\.manifest.imageWidth).mode,
              let height = examples.map(\.manifest.imageHeight).mode,
              width > 0, height > 0 else { return nil }
        let compatible = examples.filter {
            $0.manifest.imageWidth == width && $0.manifest.imageHeight == height
        }
        let compatibleSupplemental = supplementalExamples.filter {
            $0.manifest.imageWidth == width && $0.manifest.imageHeight == height
        }
        guard !compatible.isEmpty else { return nil }

        var liveByLanguageAndSelection = [String: [String: [ImageStencilPixels]]]()
        var allLiveByLanguageAndSelection = [String: [String: [ImageStencilPixels]]]()
        var livePixelsByLanguage = [String: [ImageStencilPixels]]()
        for source in liveCaptures where
            source.capture.imageWidth == width
                && source.capture.imageHeight == height {
            // load() is newest-first. The catalog consumes at most eight
            // frames for either a language-wide text consensus or a selected
            // row's animated decoration. Discard older duplicates before PNG
            // decoding and consensus work; long calibration sweeps otherwise
            // make launch cost grow forever without changing runtime evidence.
            let language = source.capture.resolvedLanguageIdentifier
            let selectedIdentifier = source.capture.selectedIdentifier
            let languageCount = livePixelsByLanguage[language]?.count ?? 0
            let selectionCount = liveByLanguageAndSelection[language]?[
                selectedIdentifier
            ]?.count ?? 0
            let allSelectionCount = allLiveByLanguageAndSelection[language]?[
                selectedIdentifier
            ]?.count ?? 0
            let isPositiveEvidence = !positiveCaptureHoldoutIDs.contains(
                source.capture.id
            )
            guard allSelectionCount < 8
                || (isPositiveEvidence
                    && (languageCount < 8 || selectionCount < 8))
            else { continue }
            guard let image = ImageFileIO.load(source.imageURL),
                  let pixels = ImageStencilPixels(
                    image,
                    referenceWidth: width,
                    referenceHeight: height,
                    band: 0..<height
                  ) else { continue }
            if allSelectionCount < 8 {
                allLiveByLanguageAndSelection[language, default: [:]][
                    selectedIdentifier,
                    default: []
                ].append(pixels)
            }
            if isPositiveEvidence, selectionCount < 8 {
                liveByLanguageAndSelection[language, default: [:]][
                    selectedIdentifier,
                    default: []
                ].append(pixels)
            }
            if isPositiveEvidence, languageCount < 8 {
                livePixelsByLanguage[language, default: []].append(pixels)
            }
        }

        let positiveByExample = compatible.map { example in
            example.manifest.annotations.filter { !$0.isHardNegative }
        }
        let allIdentifiers = Set(positiveByExample.flatMap { annotations in
            annotations.map {
                LabelingClassIdentity.canonicalIdentifier($0.classIdentifier)
            }
        })
        let selectorID = LabelingClassIdentity.canonicalIdentifier(
            LabelingClassIdentity.selectDecoration
        )
        var medianBounds = [String: CGRect]()
        for identifier in allIdentifiers where identifier != selectorID {
            let perExample = positiveByExample.compactMap { annotations -> CGRect? in
                let matches = annotations.filter {
                    LabelingClassIdentity.matches($0.classIdentifier, identifier)
                }
                guard matches.count == 1, let annotation = matches.first else { return nil }
                return pixelBounds(annotation, width: width, height: height)
            }
            guard perExample.count == compatible.count else { continue }
            medianBounds[identifier] = medianRect(perExample)
        }

        let definitions = Dictionary(uniqueKeysWithValues: context.labels.map {
            (LabelingClassIdentity.canonicalIdentifier($0.id), $0.name)
        })
        let usableIdentifiers = medianBounds.keys.filter { identifier in
            guard let bounds = medianBounds[identifier] else { return false }
            return bounds.width * bounds.height <= 5_000
                && !identifier.contains(".poster")
                && !identifier.contains(".diagram")
                && !identifier.contains(".screen-corner")
                && !identifier.contains(".mod-decoration")
                && !identifier.contains(".hollow-knight-logo")
        }
        guard !usableIdentifiers.isEmpty else { return nil }

        let selectedIdentifiers = Dictionary(uniqueKeysWithValues: compatible.map {
            ($0.id, selectedIdentifier(in: $0, width: width, height: height))
        })
        let probeIdentifier: String
        if context == .mainTitle,
           usableIdentifiers.contains("main-title.start-game") {
            probeIdentifier = "main-title.start-game"
        } else {
            // Prefer a word unique to this screen. Shared headers such as
            // "Advanced Settings" look identical in multiple menu sets and
            // cannot be a useful first-stage discriminator.
            let definitionOrder = context.labels.map {
                LabelingClassIdentity.canonicalIdentifier($0.id)
            }
            let probeCandidates = usableIdentifiers.filter {
                guard let bounds = medianBounds[$0] else { return false }
                return bounds.width * bounds.height >= 150
            }
            probeIdentifier = (probeCandidates.isEmpty
                ? usableIdentifiers
                : probeCandidates).min { left, right in
                let leftMembership = screenMembershipCount(left)
                let rightMembership = screenMembershipCount(right)
                if leftMembership != rightMembership {
                    return leftMembership < rightMembership
                }
                return (definitionOrder.firstIndex(of: left) ?? Int.max)
                    < (definitionOrder.firstIndex(of: right) ?? Int.max)
            }!
        }

        let ranked = compatible.sorted { left, right in
            let leftUnselected = selectedIdentifiers[left.id] != probeIdentifier
            let rightUnselected = selectedIdentifiers[right.id] != probeIdentifier
            if leftUnselected != rightUnselected { return leftUnselected }
            if left.manifest.annotations.count != right.manifest.annotations.count {
                return left.manifest.annotations.count > right.manifest.annotations.count
            }
            return left.manifest.createdAt > right.manifest.createdAt
        }
        guard let primary = ranked.first else { return nil }
        let probeBounds = medianBounds[probeIdentifier]!
        let geometricallyCompatible = ranked.filter { example in
            guard let annotation = annotation(probeIdentifier, in: example) else {
                return false
            }
            let bounds = pixelBounds(annotation, width: width, height: height)
            return abs(bounds.midX - probeBounds.midX) <= 12
                && abs(bounds.midY - probeBounds.midY) <= 12
                && bounds.width >= probeBounds.width * 0.85
                && bounds.width <= probeBounds.width * 1.15
                && bounds.height >= probeBounds.height * 0.75
                && bounds.height <= probeBounds.height * 1.25
        }
        var sources = [SavedLabelingExample]()
        var coveredSelections = Set<String>()
        for example in geometricallyCompatible {
            let selection = (selectedIdentifiers[example.id] ?? nil) ?? "none"
            if coveredSelections.insert(selection).inserted {
                sources.append(example)
            }
            if sources.count == 8 { break }
        }
        for example in geometricallyCompatible where sources.count < 8 {
            if !sources.contains(where: { $0.id == example.id }) {
                sources.append(example)
            }
        }
        if sources.isEmpty { sources = [primary] }
        var sourcePixels = [UUID: ImageStencilPixels]()
        for example in sources {
            guard let image = try? store.loadImage(for: example),
                  let pixels = ImageStencilPixels(
                    image,
                    referenceWidth: width,
                    referenceHeight: height,
                    band: 0..<height
                  ) else { continue }
            sourcePixels[example.id] = pixels
        }
        var supplementalPixels = [UUID: ImageStencilPixels]()
        for example in compatibleSupplemental {
            guard let image = try? store.loadImage(for: example),
                  let pixels = ImageStencilPixels(
                    image,
                    referenceWidth: width,
                    referenceHeight: height,
                    band: 0..<height
                  ) else { continue }
            supplementalPixels[example.id] = pixels
        }
        guard let firstSource = sources.first,
              sourcePixels[firstSource.id] != nil else { return nil }

        var anchors = [Anchor]()
        for identifier in usableIdentifiers.sorted() {
            guard let bounds = medianBounds[identifier] else { continue }
            let consensusBounds = expandedStencilBounds(
                bounds,
                context: context,
                classIdentifier: identifier
            )
            let humanCaptures = sources.compactMap { example -> MenuStencilConsensus.Capture? in
                guard let pixels = sourcePixels[example.id],
                      let annotation = annotation(identifier, in: example)
                else { return nil }
                let annotationBounds = expandedStencilBounds(
                    pixelBounds(annotation, width: width, height: height),
                    context: context,
                    classIdentifier: identifier
                )
                return MenuStencilConsensus.Capture(
                    pixels: pixels,
                    annotationBounds: annotationBounds
                )
            }
            guard let humanConsensus = MenuStencilConsensus.build(
                captures: humanCaptures,
                nominalBounds: consensusBounds,
                preservesAnnotationBounds: true
            ) else { continue }
            var focusedVariants = [Variant]()
            var focusedProbeReference: ImageStencilReference?
            var latestHumanBounds: CGRect?
            for example in compatibleSupplemental {
                guard focusedVariants.count < 8,
                      let pixels = supplementalPixels[example.id],
                      let annotation = annotation(identifier, in: example)
                else { continue }
                let annotationBounds = expandedStencilBounds(
                    pixelBounds(annotation, width: width, height: height),
                    context: context,
                    classIdentifier: identifier
                )
                guard abs(annotationBounds.midX - bounds.midX) <= 24,
                      abs(annotationBounds.midY - bounds.midY) <= 16,
                      annotationBounds.width >= bounds.width * 0.65,
                      annotationBounds.width <= bounds.width * 1.35,
                      annotationBounds.height >= bounds.height * 0.60,
                      annotationBounds.height <= bounds.height * 1.60,
                      let focused = MenuStencilConsensus.build(
                        captures: [.init(
                            pixels: pixels,
                            annotationBounds: annotationBounds
                        )],
                        nominalBounds: annotationBounds,
                        alignmentRadius: 0,
                        preservesAnnotationBounds: true
                      )
                else { continue }
                // loadExamples() is newest-first. A focused incomplete draft
                // is the user's latest placement correction for this object.
                if latestHumanBounds == nil {
                    latestHumanBounds = annotationBounds
                    focusedProbeReference = focused.reference
                }
                focusedVariants.append(makeVariant(
                    kind: "\(identifier).human-box.\(example.id.uuidString.lowercased())",
                    reference: focused.reference,
                    sampleStride: 1,
                    languageIdentifier: HollowKnightMenuLanguage.english.rawValue
                ))
            }
            let anchorBounds = latestHumanBounds ?? humanConsensus.bounds
            let calibratedBounds = calibration?.anchors.first {
                $0.classIdentifier == identifier && $0.evidenceCount > 0
            }?.rect.cgRect
            var liveConsensusByLanguage = [String: MenuStencilConsensus.Result]()
            for language in livePixelsByLanguage.keys.sorted() {
                    let captures = (livePixelsByLanguage[language] ?? []).prefix(8).map {
                        MenuStencilConsensus.Capture(
                            pixels: $0,
                            annotationBounds: anchorBounds
                        )
                    }
                    guard let consensus = MenuStencilConsensus.buildLocalizedText(
                        captures: Array(captures),
                        nominalBounds: anchorBounds
                    ) else { continue }
                    liveConsensusByLanguage[language] = consensus
            }
            var variants = [makeVariant(
                kind: identifier,
                reference: humanConsensus.reference,
                sampleStride: 1,
                languageIdentifier: HollowKnightMenuLanguage.english.rawValue
            )]
            variants.append(contentsOf: focusedVariants)
            // A user may leave a focused draft with only the object that is
            // failing. These drafts intentionally omit selector annotations,
            // so they cannot define a scene alone. Once the scene exists,
            // their box and masked pixels are authoritative for this object.
            for language in liveConsensusByLanguage.keys.sorted() {
                guard let liveConsensus = liveConsensusByLanguage[language],
                      language != HollowKnightMenuLanguage.english.rawValue
                        || liveConsensus.reference != humanConsensus.reference
                else { continue }
                variants.append(makeVariant(
                    kind: "\(identifier).live.\(language)",
                    reference: liveConsensus.reference,
                    sampleStride: 1,
                    languageIdentifier: language,
                    nominalBounds: liveConsensus.bounds
                ))
            }
            // Quit Game and Quit To Menu share Yes/No and nearly identical
            // decoration geometry. Their title must remain the discriminator.
            // A focused incomplete correction can be a valid object variant,
            // but must not replace the multi-capture title consensus used to
            // choose between these two scenes.
            let englishLiveConsensus = liveConsensusByLanguage[
                HollowKnightMenuLanguage.english.rawValue
            ]
            let probeReference = context == .quitGame || context == .quitToMenu
                ? (englishLiveConsensus?.reference ?? humanConsensus.reference)
                : (focusedProbeReference ?? humanConsensus.reference)
            let probe = makeVariant(
                kind: identifier,
                reference: probeReference,
                sampleStride: 2,
                languageIdentifier: HollowKnightMenuLanguage.english.rawValue
            )
            let foreignProbeVariants = liveConsensusByLanguage.keys.sorted().compactMap {
                language -> Variant? in
                guard language != HollowKnightMenuLanguage.english.rawValue,
                      let consensus = liveConsensusByLanguage[language]
                else { return nil }
                return makeVariant(
                    kind: "\(identifier).probe.\(language)",
                    reference: consensus.reference,
                    sampleStride: 2,
                    languageIdentifier: language,
                    nominalBounds: consensus.bounds
                )
            }
            anchors.append(Anchor(
                classIdentifier: identifier,
                name: definitions[identifier] ?? identifier,
                bounds: anchorBounds,
                calibratedBounds: calibratedBounds,
                variants: variants,
                probe: probe,
                probeVariants: [probe] + foreignProbeVariants
            ))
        }
        guard let probe = anchors.first(where: {
            $0.classIdentifier == probeIdentifier
        }) else { return nil }

        let optionAnchors = anchors.filter {
            isSelectable(
                $0,
                context: context,
                firstDefinitionIdentifier: context.labels.first.map {
                    LabelingClassIdentity.canonicalIdentifier($0.id)
                }
            )
        }
        let verifiedBounds = Dictionary(uniqueKeysWithValues: anchors.map {
            ($0.classIdentifier, $0.bounds)
        })
        let globalSelectorGeometry = selectorGeometry(
            examples: compatible,
            width: width,
            height: height,
            anchorBoundsByIdentifier: verifiedBounds
        )
        var selectorVariants = [String: [Variant]]()
        for side in ["left", "right"] {
            var variants = [Variant]()
            for example in sources {
                let selectors = selectorAnnotations(in: example).sorted { $0.x < $1.x }
                let sideIndex = side == "left" ? 0 : selectors.count - 1
                guard selectors.indices.contains(sideIndex),
                      let pixels = sourcePixels[example.id],
                      let reference = pixels.reference(in: pixelBounds(
                        selectors[sideIndex], width: width, height: height
                      )) else { continue }
                variants.append(makeVariant(
                    kind: side,
                    reference: reference,
                    sampleStride: 2
                ))
            }
            selectorVariants[side] = variants
        }

        // Selector decorations move with the selected row, so consensus must
        // be built separately at every captured row. This removes animated
        // background pixels while preserving the exact screen-space position.
        var allSourcePixels = sourcePixels
        for example in compatible where allSourcePixels[example.id] == nil {
            guard let image = try? store.loadImage(for: example),
                  let pixels = ImageStencilPixels(
                    image,
                    referenceWidth: width,
                    referenceHeight: height,
                    band: 0..<height
                  ) else { continue }
            allSourcePixels[example.id] = pixels
        }
        func localizedSelectorBounds(
            in pixels: ImageStencilPixels,
            around bounds: CGRect,
            optionBounds: CGRect,
            side: String,
            horizontalRadius: Int,
            idleForegroundMask: ImageStencilForegroundMask? = nil
        ) -> CGRect? {
            let variants = Array((selectorVariants[side] ?? []).prefix(8))
            guard !variants.isEmpty else { return nil }
            var best: (
                confidence: Double,
                foregroundCount: Int,
                distance: Int,
                rect: CGRect
            )?
            for variant in variants {
                for y in -12...12 {
                    for x in -horizontalRadius...horizontalRadius {
                        let candidate = bounds.offsetBy(
                            dx: CGFloat(x), dy: CGFloat(y)
                        )
                        if side == "left", candidate.midX >= optionBounds.midX {
                            continue
                        }
                        if side == "right", candidate.midX <= optionBounds.midX {
                            continue
                        }
                        if context == .controller,
                           !(175...465).contains(candidate.midX) {
                            // The controller diagram and its callout lines are
                            // strong false stencil targets near both screen
                            // edges. Every selectable controller row is in the
                            // centered footer, so its paired decorations stay
                            // inside this measured band in every language.
                            continue
                        }
                        guard let comparison = pixels.compare(
                            variant.kernel,
                            at: candidate
                        ) else { continue }
                        let confidence = comparison.confidence(
                            correlationWeight: 0.82,
                            colorErrorScale: 105
                        )
                        let foregroundCount = idleForegroundMask == nil ? 0
                            : pixels.likelyMenuTextPixelCount(
                                in: candidate,
                                excludingStableForeground: idleForegroundMask
                            )
                        let distance = abs(x) + abs(y)
                        let hasNovelForeground = foregroundCount >= 8
                        let bestHasNovelForeground =
                            (best?.foregroundCount ?? 0) >= 8
                        if hasNovelForeground != bestHasNovelForeground
                            ? hasNovelForeground
                            : confidence > (best?.confidence ?? -.infinity)
                            || (confidence == (best?.confidence ?? -.infinity)
                                && distance < (best?.distance ?? .max)) {
                            best = (
                                confidence,
                                foregroundCount,
                                distance,
                                candidate
                            )
                        }
                    }
                }
            }
            guard let best,
                  best.confidence >= 0.45
                    || (best.foregroundCount >= 24 && best.confidence >= 0.25)
            else { return nil }
            return best.rect
        }
        func liveNegativeFrames(
            excluding identifier: String,
            language: String
        ) -> [ImageStencilPixels] {
            Array(
                (allLiveByLanguageAndSelection[language] ?? [:])
                    .filter { $0.key != identifier }
                    .sorted { $0.key < $1.key }
                    .compactMap { $0.value.first }
                    .prefix(24)
            )
        }
        func makeIdleForegroundMasks(
            excluding identifier: String,
            baseBounds: CGRect,
            localizedBounds: [String: CGRect] = [:]
        ) -> [String: ImageStencilForegroundMask] {
            var masks = [String: ImageStencilForegroundMask]()
            for language in allLiveByLanguageAndSelection.keys {
                let searchBounds = (
                    localizedBounds[language] ?? baseBounds
                ).insetBy(dx: -48, dy: -16)
                let negatives = liveNegativeFrames(
                    excluding: identifier,
                    language: language
                )
                if let mask = ImageStencilForegroundMask(
                    frames: negatives,
                    bounds: searchBounds
                ) {
                    masks[language] = mask
                }
                if ProcessInfo.processInfo.environment[
                    "HKV_MENU_IDLE_MASK_LOG"
                ] == "1", identifier == LabelingClassIdentity.resetDefaults {
                    print(
                        "MENU_IDLE_BUILD \(context.storageIdentifier)"
                            + "[\(language)] sideBounds=\(searchBounds)"
                            + " negatives=\(negatives.count)"
                            + " built=\(masks[language] != nil)"
                    )
                }
            }
            return masks
        }
        func positionedSelector(
            for identifier: String,
            side: String,
            fallbackBounds: CGRect? = nil
        ) -> PositionedSelector? {
            let sideIndex: (Int) -> Int = { count in side == "left" ? 0 : count - 1 }
            var captures = compatible.compactMap { example -> (
                capture: MenuStencilConsensus.Capture,
                languageIdentifier: String?
            )? in
                guard selectedIdentifiers[example.id] == identifier,
                      let pixels = allSourcePixels[example.id] else { return nil }
                let selectors = selectorAnnotations(in: example).sorted { $0.x < $1.x }
                let index = sideIndex(selectors.count)
                guard selectors.indices.contains(index) else { return nil }
                return (
                    MenuStencilConsensus.Capture(
                        pixels: pixels,
                        annotationBounds: pixelBounds(
                            selectors[index], width: width, height: height
                        )
                    ),
                    nil
                )
            }
            let measuredBounds = calibration?.selectors.first(where: {
                $0.selectedIdentifier == identifier
            }).map {
                side == "left" ? $0.leftRect.cgRect : $0.rightRect.cgRect
            }
            let calibratedSelector = calibration?.selectors.first {
                $0.selectedIdentifier == identifier
            }
            var localizedBounds = [String: CGRect]()
            if let liveBounds = measuredBounds ?? fallbackBounds {
                for language in allLiveByLanguageAndSelection.keys.sorted().reversed() {
                    // Fixed screen-space geometry is deployed calibration, not
                    // positive pixel evidence. Keep learning it from every
                    // completed sweep while the holdout test removes the
                    // target frame only from the stencil variants below.
                    let geometryFrames =
                        allLiveByLanguageAndSelection[language]?[identifier] ?? []
                    let positiveFrames =
                        liveByLanguageAndSelection[language]?[identifier] ?? []
                    let horizontalRadius = language
                        == HollowKnightMenuLanguage.english.rawValue ? 12 : 120
                    let localizationIdleMask = context == .controller
                        ? ImageStencilForegroundMask(
                            frames: liveNegativeFrames(
                                excluding: identifier,
                                language: language
                            ),
                            bounds: liveBounds.insetBy(
                                dx: -CGFloat(horizontalRadius + 8),
                                dy: -16
                            )
                        ) : nil
                    let aligned = geometryFrames.compactMap {
                        localizedSelectorBounds(
                            in: $0,
                            around: liveBounds,
                            optionBounds: verifiedBounds[identifier]
                                ?? CGRect(
                                    x: CGFloat(width) / 2,
                                    y: liveBounds.minY,
                                    width: 1,
                                    height: liveBounds.height
                                ),
                            side: side,
                            // Bundled English calibration already comes from
                            // these exact screen rows. A broad relocalization
                            // can jump from the small decoration to a nearby
                            // static keycap. Translated labels legitimately
                            // move their flanking decorations much farther.
                            horizontalRadius: horizontalRadius,
                            idleForegroundMask: localizationIdleMask
                        )
                    }
                    let languageBounds = aligned.isEmpty
                        ? liveBounds : medianRect(aligned)
                    localizedBounds[language] = languageBounds
                    for livePixels in positiveFrames.reversed() {
                        captures.insert((
                            MenuStencilConsensus.Capture(
                                pixels: livePixels,
                                annotationBounds: languageBounds
                            ),
                            language
                        ), at: 0)
                    }
                }
            }
            for (language, placement) in
                calibratedSelector?.localizedPlacements ?? [:] {
                localizedBounds[language] = side == "left"
                    ? placement.leftRect.cgRect : placement.rightRect.cgRect
            }
            guard !captures.isEmpty else { return nil }
            let nominal = medianRect(captures.map(\.capture.annotationBounds))
            guard let consensus = MenuStencilConsensus.build(
                captures: captures.map(\.capture),
                nominalBounds: nominal,
                alignmentRadius: 8
            ) else { return nil }
            func negativeFrames(for languageIdentifier: String?)
                -> [ImageStencilPixels] {
                if let languageIdentifier {
                    return liveNegativeFrames(
                        excluding: identifier,
                        language: languageIdentifier
                    )
                }
                return Array(sources.compactMap { example in
                    guard selectedIdentifiers[example.id] != identifier else {
                        return nil
                    }
                    return allSourcePixels[example.id]
                }.prefix(24))
            }
            var variants = [Variant]()
            for (index, source) in captures.enumerated() {
                guard let individual = MenuStencilConsensus.build(
                    captures: [source.capture],
                    nominalBounds: source.capture.annotationBounds,
                    alignmentRadius: 0
                ) else { continue }
                let negatives = negativeFrames(for: source.languageIdentifier)
                let reference: ImageStencilReference
                if negatives.count >= 2 {
                    guard let contrasted =
                        MenuStencilConsensus.removingStableBackground(
                            from: individual.reference,
                            at: individual.bounds,
                            negativeFrames: negatives
                        ) else { continue }
                    reference = contrasted
                } else {
                    reference = individual.reference
                }
                variants.append(makeVariant(
                    kind: "\(identifier).selector.\(side).\(index)",
                    reference: reference,
                    sampleStride: 1,
                    languageIdentifier: source.languageIdentifier
                ))
            }
            let consensusNegatives = Array(
                allLiveByLanguageAndSelection.values
                    .flatMap { $0.filter { $0.key != identifier } }
                    .sorted { $0.key < $1.key }
                    .compactMap { $0.value.first }
                    .prefix(24)
            )
            if let consensusReference =
                MenuStencilConsensus.removingStableBackground(
                    from: consensus.reference,
                    at: consensus.bounds,
                    negativeFrames: consensusNegatives
                ) {
                variants.append(makeVariant(
                    kind: "\(identifier).selector.\(side).consensus",
                    reference: consensusReference,
                    sampleStride: 1
                ))
            } else if variants.isEmpty, consensusNegatives.count < 2 {
                // Legacy catalogs without enough unselected frames retain one
                // usable selector reference.
                variants.append(makeVariant(
                    kind: "\(identifier).selector.\(side).legacy",
                    reference: consensus.reference,
                    sampleStride: 1
                ))
            }
            if variants.isEmpty {
                variants = selectorVariants[side] ?? []
            }
            guard !variants.isEmpty else { return nil }
            let idleForegroundMasks = makeIdleForegroundMasks(
                excluding: identifier,
                baseBounds: measuredBounds ?? fallbackBounds ?? consensus.bounds,
                localizedBounds: localizedBounds
            )
            return PositionedSelector(
                bounds: consensus.bounds,
                variants: variants,
                localizedBounds: localizedBounds,
                idleForegroundMasks: idleForegroundMasks
            )
        }
        var options = optionAnchors.map { anchor in
            let calibratedSelector = calibration?.selectors.first {
                $0.selectedIdentifier == anchor.classIdentifier
            }
            let capturedLeft = positionedSelector(
                for: anchor.classIdentifier, side: "left"
            )
            let capturedRight = positionedSelector(
                for: anchor.classIdentifier, side: "right"
            )
            let geometry = selectorGeometry(
                examples: compatible,
                width: width,
                height: height,
                selectedIdentifier: anchor.classIdentifier,
                anchorBoundsByIdentifier: verifiedBounds
            ) ?? globalSelectorGeometry
            func inferredSelector(side: String) -> PositionedSelector? {
                guard let geometry else { return nil }
                let variants = selectorVariants[side] ?? []
                guard !variants.isEmpty else { return nil }
                let bounds: CGRect
                if side == "left" {
                    bounds = CGRect(
                        x: anchor.bounds.minX - geometry.leftGap - geometry.leftWidth,
                        y: anchor.bounds.midY + geometry.leftCenterYOffset
                            - geometry.leftHeight * 0.5,
                        width: geometry.leftWidth,
                        height: geometry.leftHeight
                    )
                } else {
                    bounds = CGRect(
                        x: anchor.bounds.maxX + geometry.rightGap,
                        y: anchor.bounds.midY + geometry.rightCenterYOffset
                            - geometry.rightHeight * 0.5,
                        width: geometry.rightWidth,
                        height: geometry.rightHeight
                    )
                }
                return PositionedSelector(
                    bounds: bounds,
                    variants: variants,
                    idleForegroundMasks: makeIdleForegroundMasks(
                        excluding: anchor.classIdentifier,
                        baseBounds: bounds
                    )
                )
            }
            return Option(
                classIdentifier: anchor.classIdentifier,
                name: anchor.name,
                bounds: anchor.bounds,
                selectorGeometry: geometry,
                leftSelector: calibratedSelector.map {
                    PositionedSelector(
                        bounds: $0.leftRect.cgRect,
                        variants: capturedLeft?.variants ?? selectorVariants["left"] ?? [],
                        localizedBounds: capturedLeft?.localizedBounds ?? [:],
                        idleForegroundMasks: makeIdleForegroundMasks(
                            excluding: anchor.classIdentifier,
                            baseBounds: $0.leftRect.cgRect,
                            localizedBounds:
                                capturedLeft?.localizedBounds ?? [:]
                        ).merging(
                            capturedLeft?.idleForegroundMasks ?? [:]
                        ) { _, captured in captured }
                    )
                } ?? capturedLeft ?? inferredSelector(side: "left"),
                rightSelector: calibratedSelector.map {
                    PositionedSelector(
                        bounds: $0.rightRect.cgRect,
                        variants: capturedRight?.variants ?? selectorVariants["right"] ?? [],
                        localizedBounds: capturedRight?.localizedBounds ?? [:],
                        idleForegroundMasks: makeIdleForegroundMasks(
                            excluding: anchor.classIdentifier,
                            baseBounds: $0.rightRect.cgRect,
                            localizedBounds:
                                capturedRight?.localizedBounds ?? [:]
                        ).merging(
                            capturedRight?.idleForegroundMasks ?? [:]
                        ) { _, captured in captured }
                    )
                } ?? capturedRight ?? inferredSelector(side: "right")
            )
        }

        if context == .selectProfile {
            let clearSaveIdentifier = "select-profile.clear-save"
            let clearSaveBounds = compatible.flatMap { example in
                example.manifest.annotations.compactMap { annotation -> CGRect? in
                    guard !annotation.isHardNegative,
                          LabelingClassIdentity.matches(
                            annotation.classIdentifier,
                            clearSaveIdentifier
                          ) else { return nil }
                    return pixelBounds(annotation, width: width, height: height)
                }
            }
            if let labelBounds = clearSaveBounds.isEmpty
                ? nil : Optional(medianRect(clearSaveBounds)),
               let firstSlot = options.first(where: {
                   $0.classIdentifier == "select-profile.slot-1"
               }),
               let firstLeft = firstSlot.leftSelector?.bounds,
               let firstRight = firstSlot.rightSelector?.bounds {
                let firstCenter = (firstLeft.midY + firstRight.midY) * 0.5
                for index in 1...4 {
                    let slotIdentifier = "select-profile.slot-\(index)"
                    guard let slot = options.first(where: {
                        $0.classIdentifier == slotIdentifier
                    }), let slotLeft = slot.leftSelector?.bounds,
                       let slotRight = slot.rightSelector?.bounds else { continue }
                    let rowCenter = (slotLeft.midY + slotRight.midY) * 0.5
                    let rowOffset = rowCenter - firstCenter
                    let optionIdentifier = "select-profile.clear-save-\(index)"
                    let defaultLeft = CGRect(
                        x: labelBounds.minX - 24,
                        y: labelBounds.minY + rowOffset,
                        width: 15,
                        height: 12
                    )
                    let defaultRight = CGRect(
                        x: labelBounds.maxX + 11,
                        y: labelBounds.minY + rowOffset,
                        width: 15,
                        height: 12
                    )
                    let calibrated = calibration?.selectors.first {
                        $0.selectedIdentifier == optionIdentifier
                    }
                    let leftBounds = calibrated?.leftRect.cgRect ?? defaultLeft
                    let rightBounds = calibrated?.rightRect.cgRect ?? defaultRight
                    let capturedLeft = positionedSelector(
                        for: optionIdentifier,
                        side: "left",
                        fallbackBounds: leftBounds
                    )
                    let capturedRight = positionedSelector(
                        for: optionIdentifier,
                        side: "right",
                        fallbackBounds: rightBounds
                    )
                    options.append(Option(
                        classIdentifier: optionIdentifier,
                        name: "Clear Save \(index)",
                        bounds: labelBounds.offsetBy(dx: 0, dy: rowOffset),
                        selectorGeometry: nil,
                        leftSelector: PositionedSelector(
                            bounds: leftBounds,
                            variants: capturedLeft?.variants
                                ?? selectorVariants["left"] ?? [],
                            localizedBounds: capturedLeft?.localizedBounds ?? [:],
                            idleForegroundMasks: makeIdleForegroundMasks(
                                excluding: optionIdentifier,
                                baseBounds: leftBounds,
                                localizedBounds:
                                    capturedLeft?.localizedBounds ?? [:]
                            ).merging(
                                capturedLeft?.idleForegroundMasks ?? [:]
                            ) { _, captured in captured }
                        ),
                        rightSelector: PositionedSelector(
                            bounds: rightBounds,
                            variants: capturedRight?.variants
                                ?? selectorVariants["right"] ?? [],
                            localizedBounds: capturedRight?.localizedBounds ?? [:],
                            idleForegroundMasks: makeIdleForegroundMasks(
                                excluding: optionIdentifier,
                                baseBounds: rightBounds,
                                localizedBounds:
                                    capturedRight?.localizedBounds ?? [:]
                            ).merging(
                                capturedRight?.idleForegroundMasks ?? [:]
                            ) { _, captured in captured }
                        )
                    ))
                }
            }
        }

        return Scene(
            context: context,
            referenceWidth: width,
            referenceHeight: height,
            probe: probe,
            anchors: anchors,
            options: options,
            selectorVariants: selectorVariants,
            selectorGeometry: globalSelectorGeometry
        )
    }

    private static func isSelectable(
        _ anchor: Anchor,
        context: LabelingContext,
        firstDefinitionIdentifier: String?
    ) -> Bool {
        let identifier = anchor.classIdentifier
        if identifier.contains(".header")
            || identifier.contains(".heading")
            || identifier.hasSuffix(".pointer")
            || identifier == "controller-advanced.mfi" {
            return false
        }
        if context == .quitToMenu || context == .quitGame {
            return identifier != firstDefinitionIdentifier
        }
        if context != .mainTitle,
           anchor.bounds.minY < 80,
           identifier == firstDefinitionIdentifier {
            return false
        }
        return true
    }

    private static func makeVariant(
        kind: String,
        reference: ImageStencilReference,
        sampleStride: Int,
        languageIdentifier: String? = nil,
        nominalBounds: CGRect? = nil
    ) -> Variant {
        let mask = reference.mask.map { [UInt8]($0) }
        return Variant(
            kernel: ImageStencilKernel(
                kind: kind,
                bounds: CGRect(
                    x: 0,
                    y: 0,
                    width: reference.width,
                    height: reference.height
                ),
                rgb: reference.rgb,
                sampleStride: sampleStride,
                includes: { x, y, width, _ in
                    mask.map { $0[y * width + x] != 0 } ?? true
                }
            ),
            reference: reference,
            languageIdentifier: languageIdentifier,
            nominalBounds: nominalBounds
        )
    }

    fileprivate static func screenMembershipCount(_ classIdentifier: String) -> Int {
        LabelingContext.contractSetCases.count { context in
            context.labels.contains {
                LabelingClassIdentity.matches($0.id, classIdentifier)
            }
        }
    }

    private static func selectorGeometry(
        examples: [SavedLabelingExample],
        width: Int,
        height: Int,
        selectedIdentifier requiredIdentifier: String? = nil,
        anchorBoundsByIdentifier: [String: CGRect] = [:]
    ) -> SelectorGeometry? {
        var leftWidths = [CGFloat]()
        var leftHeights = [CGFloat]()
        var leftGaps = [CGFloat]()
        var leftY = [CGFloat]()
        var rightWidths = [CGFloat]()
        var rightHeights = [CGFloat]()
        var rightGaps = [CGFloat]()
        var rightY = [CGFloat]()
        for example in examples {
            let selectors = selectorAnnotations(in: example).sorted { $0.x < $1.x }
            guard selectors.count >= 2,
                  let selected = selectedIdentifier(in: example, width: width, height: height),
                  requiredIdentifier == nil || selected == requiredIdentifier,
                  let selectedAnnotation = annotation(selected, in: example) else { continue }
            let anchor = anchorBoundsByIdentifier[selected]
                ?? pixelBounds(selectedAnnotation, width: width, height: height)
            let left = pixelBounds(selectors.first!, width: width, height: height)
            let right = pixelBounds(selectors.last!, width: width, height: height)
            leftWidths.append(left.width)
            leftHeights.append(left.height)
            leftGaps.append(anchor.minX - left.maxX)
            leftY.append(left.midY - anchor.midY)
            rightWidths.append(right.width)
            rightHeights.append(right.height)
            rightGaps.append(right.minX - anchor.maxX)
            rightY.append(right.midY - anchor.midY)
        }
        guard let leftWidth = leftWidths.median,
              let leftHeight = leftHeights.median,
              let leftGap = leftGaps.median,
              let leftCenterYOffset = leftY.median,
              let rightWidth = rightWidths.median,
              let rightHeight = rightHeights.median,
              let rightGap = rightGaps.median,
              let rightCenterYOffset = rightY.median else { return nil }
        return SelectorGeometry(
            leftWidth: leftWidth,
            leftHeight: leftHeight,
            leftGap: leftGap,
            leftCenterYOffset: leftCenterYOffset,
            rightWidth: rightWidth,
            rightHeight: rightHeight,
            rightGap: rightGap,
            rightCenterYOffset: rightCenterYOffset
        )
    }

    private static func selectedIdentifier(
        in example: SavedLabelingExample,
        width: Int,
        height: Int
    ) -> String? {
        let selectors = selectorAnnotations(in: example)
        guard selectors.count >= 2 else { return nil }
        let selectorCenterY = selectors.map {
            pixelBounds($0, width: width, height: height).midY
        }.reduce(0, +) / CGFloat(selectors.count)
        let selectorBounds = selectors.map {
            pixelBounds($0, width: width, height: height)
        }.sorted { $0.midX < $1.midX }
        let leftX = selectorBounds.first!.midX
        let rightX = selectorBounds.last!.midX
        return example.manifest.annotations.filter {
            !$0.isHardNegative
                && !LabelingClassIdentity.matches(
                    $0.classIdentifier,
                    LabelingClassIdentity.selectDecoration
                )
        }.compactMap { annotation -> (String, CGFloat)? in
            let bounds = pixelBounds(annotation, width: width, height: height)
            guard bounds.width * bounds.height <= 5_000,
                  bounds.minX > leftX,
                  bounds.maxX < rightX else { return nil }
            return (
                LabelingClassIdentity.canonicalIdentifier(annotation.classIdentifier),
                abs(bounds.midY - selectorCenterY)
            )
        }.min { $0.1 < $1.1 }.map(\.0)
    }

    private static func selectorAnnotations(
        in example: SavedLabelingExample
    ) -> [LabelingExampleAnnotation] {
        example.manifest.annotations.filter {
            !$0.isHardNegative
                && LabelingClassIdentity.matches(
                    $0.classIdentifier,
                    LabelingClassIdentity.selectDecoration
                )
        }
    }

    private static func annotation(
        _ canonicalIdentifier: String,
        in example: SavedLabelingExample
    ) -> LabelingExampleAnnotation? {
        let matches = example.manifest.annotations.filter {
            !$0.isHardNegative
                && LabelingClassIdentity.matches(
                    $0.classIdentifier,
                    canonicalIdentifier
                )
        }
        return matches.count == 1 ? matches[0] : nil
    }

    private static func pixelBounds(
        _ annotation: LabelingExampleAnnotation,
        width: Int,
        height: Int
    ) -> CGRect {
        CGRect(
            x: annotation.x * Double(width),
            y: annotation.y * Double(height),
            width: annotation.width * Double(width),
            height: annotation.height * Double(height)
        )
    }

    private static func medianRect(_ rects: [CGRect]) -> CGRect {
        CGRect(
            x: rects.map(\.minX).median ?? 0,
            y: rects.map(\.minY).median ?? 0,
            width: rects.map(\.width).median ?? 0,
            height: rects.map(\.height).median ?? 0
        )
    }
}

enum MenuStencilCatalogError: Error {
    case noScenes
}

private extension Array where Element == CGFloat {
    var median: CGFloat? {
        guard !isEmpty else { return nil }
        let sorted = sorted()
        let middle = sorted.count / 2
        return sorted.count.isMultiple(of: 2)
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle]
    }
}

private extension Array where Element == Int {
    var mode: Int? {
        reduce(into: [Int: Int]()) { $0[$1, default: 0] += 1 }
            .max { left, right in
                left.value == right.value ? left.key > right.key : left.value < right.value
            }?.key
    }
}

/// Stateful two-stage menu matcher. Searching evaluates one sparse probe from
/// every scene. Once a scene wins, only that scene's probe is checked each
/// frame; its remaining objects are refreshed periodically. Selection scans
/// every measured pair in the tracked scene so a stale local match cannot
/// suppress a newly selected row.
struct MenuStencilSearchCadence {
    let minimumInterval: Double
    private(set) var lastSearchTimestamp: Double?

    init(minimumInterval: Double) {
        self.minimumInterval = max(0, minimumInterval)
    }

    mutating func shouldSearch(at timestamp: Double) -> Bool {
        guard timestamp.isFinite else { return true }
        if let lastSearchTimestamp,
           timestamp >= lastSearchTimestamp,
           timestamp - lastSearchTimestamp < minimumInterval {
            return false
        }
        lastSearchTimestamp = timestamp
        return true
    }

    mutating func reset() {
        lastSearchTimestamp = nil
    }
}

final class MenuStencilTracker {
    static let probeThreshold = 0.42
    static let anchorThreshold = 0.40
    static let sceneThreshold = 0.40
    static let selectorThreshold = 0.48
    static let selectorEvidenceThreshold = 0.38
    static let selectorMinimumForegroundPixels = 24
    static let fadedSelectorEvidenceThreshold = 0.22
    static let fadedSelectorMinimumForegroundPixels = 36
    static let selectorWinningMargin = 0.06
    static let selectorSearchRadiusX = 4
    static let selectorSearchRadiusY = 4
    static let selectorRecoveryRadiusX = 8
    static let selectorRecoveryRadiusY = 10
    static let selectorMaximumPairRowDelta: CGFloat = 5
    // At the menu capture cadence, eight frames keeps stale-scene recovery
    // close to one second while leaving ordinary frames on the narrow path.
    static let validationInterval = 8
    static let lossGraceFrames = 2
    static let sceneChallengeWinningMargin = 0.12
    // Limiting checkpoints to eight let a high-scoring shared row (especially
    // Video Advanced Settings) exclude Audio, Video, Remap, or Keyboard. Full
    // acquisition below admits every eligible scene; periodic checks stay at
    // sixteen and invoke that full path when ownership appears stale.
    static let sceneChallengeCandidateLimit = 16

    private static func probeThreshold(for context: LabelingContext) -> Double {
        switch context {
        case .controller:
            // The controller diagram occupies most of this screen and its
            // animated background crosses the small Remap Controls probe.
            // Admit the probe slightly earlier, then require the ordinary
            // multi-anchor validation below before accepting the scene.
            return 0.31
        case .selectProfile:
            // Dust can cross the thin Select Profile heading. The failing
            // held-out Clear Save frame scored 0.356; admit it, then require
            // the full profile-row anchors before the scene can win
            // acquisition.
            return 0.34
        case .options:
            // The Options probe is its first selectable row. When Game is
            // selected, the animated decoration can cross the localized text
            // mask and depress only that one probe. Admit the weak language
            // hypothesis, then require the unchanged multi-row anchors in
            // validate() before the scene can own the frame.
            return 0.36
        case .quitGame:
            // The live Quit Game dialog is drawn over animated gameplay.
            // Its thin English title probe scored 0.198 while both dialog
            // rows scored about 0.98. Admit the weak title hypothesis, then
            // require the Yes/No pair during full validation below.
            return 0.10
        case .quitToMenu:
            // Quit Game and Quit to Menu share their Yes/No rows and selector
            // geometry. Keep both dialog hypotheses in fresh acquisition even
            // when the longer Quit to Menu title has a weak sparse probe; the
            // full distinct title anchor then decides which dialog owns the
            // frame.
            return 0.10
        case .gameOptions, .video, .videoAdvancedSettings,
             .remapController,
             .controllerAdvancedSettings, .keyboard:
            return 0.36
        default:
            return probeThreshold
        }
    }

    private static func validationThreshold(for context: LabelingContext) -> Double {
        switch context {
        case .options, .gameOptions, .video, .videoAdvancedSettings,
             .controller, .remapController,
             .controllerAdvancedSettings, .keyboard,
             .quitGame, .quitToMenu:
            return 0.36
        default:
            return anchorThreshold
        }
    }

    private static func isConfirmationDialog(_ context: LabelingContext) -> Bool {
        context == .quitGame || context == .quitToMenu
    }

    private static func selectorRecoveryRadiusX(
        for context: LabelingContext
    ) -> Int {
        // Localized labels change width, moving their flanking decorations by
        // much more than calibration jitter. This wider pass runs only after
        // the cheap row search has no strong side; stable live matches are
        // persisted and return subsequent frames to the narrow path.
        40
    }

    static func qualifiedSelectorPairConfidence(
        left: Double,
        right: Double,
        leftForegroundPixelCount: Int = 0,
        rightForegroundPixelCount: Int = 0
    ) -> Double? {
        func qualifies(_ confidence: Double, foregroundPixelCount: Int) -> Bool {
            confidence >= selectorThreshold
                || (
                    confidence >= selectorEvidenceThreshold
                        && foregroundPixelCount >= selectorMinimumForegroundPixels
                )
                || (
                    confidence >= fadedSelectorEvidenceThreshold
                        && foregroundPixelCount
                            >= fadedSelectorMinimumForegroundPixels
                )
        }
        guard qualifies(left, foregroundPixelCount: leftForegroundPixelCount),
              qualifies(right, foregroundPixelCount: rightForegroundPixelCount) else {
            return nil
        }
        return (left + right) / 2
    }

    private struct ScoredVariant {
        let confidence: Double
        let foregroundPixelCount: Int
        let brightForegroundPixelCount: Int
        let rect: CGRect
        let reference: ImageStencilReference
        let languageIdentifier: String?
        let nominalBounds: CGRect?
    }

    private struct ProbeCandidate {
        let scene: MenuStencilCatalog.Scene
        let score: ScoredVariant
    }

    private struct SelectorBounds {
        let left: CGRect
        let right: CGRect
    }

    private struct SelectorCalibrationSample {
        let left: CGRect
        let right: CGRect
        let timestamp: Double
    }

    private struct PendingSelectorCalibration {
        let key: String
        var samples: [SelectorCalibrationSample]
    }

    private var catalog: MenuStencilCatalog?
    private let selectorCalibrationURL: URL?
    private let selectorCalibrationMirrorURL: URL?
    private var writableCalibration: MenuStencilCalibration?
    private var trackedContext: LabelingContext?
    private var trackedLanguageIdentifier: String?
    private var trackedOffset = CGPoint.zero
    private var missingProbeFrames = 0
    private var languageReacquisitionMissFrames = 0
    private var framesUntilValidation = 0
    private var lastValidatedResult: MenuStencilResult?
    private var searchCadence: MenuStencilSearchCadence
    private var learnedSelectorBounds = [String: SelectorBounds]()
    private var pendingSelectorCalibration: PendingSelectorCalibration?
    private(set) var selectorCalibrationDiagnostic: String?

    static func resolvedSelectorCalibrationURL(
        usesDeployedCatalog: Bool,
        explicitURL: URL?
    ) -> URL? {
        explicitURL
    }

    init(
        catalog: MenuStencilCatalog? = nil,
        loadsDeployedCatalog: Bool = true,
        minimumSearchInterval: Double = 0,
        selectorCalibrationURL: URL? = nil,
        selectorCalibrationMirrorURL: URL? = nil
    ) {
        let usesDeployedCatalog = catalog == nil && loadsDeployedCatalog
        let resolvedCatalog = usesDeployedCatalog ? .humanLabeled : catalog
        self.catalog = resolvedCatalog
        self.selectorCalibrationURL = Self.resolvedSelectorCalibrationURL(
            usesDeployedCatalog: usesDeployedCatalog,
            explicitURL: selectorCalibrationURL
        )
        self.selectorCalibrationMirrorURL = selectorCalibrationMirrorURL
        self.writableCalibration = self.selectorCalibrationURL.flatMap {
            MenuStencilCalibration.load($0)
        } ?? (self.selectorCalibrationURL == nil
            ? nil : MenuStencilCalibration.loadDefault())
        self.searchCadence = MenuStencilSearchCadence(
            minimumInterval: minimumSearchInterval
        )
        if let resolvedCatalog {
            synchronizeSelectorCorrections(from: resolvedCatalog)
        }
    }

    func reset(preservingLanguage: Bool = false) {
        let retainedLanguageIdentifier = preservingLanguage
            ? trackedLanguageIdentifier : nil
        trackedContext = nil
        trackedLanguageIdentifier = retainedLanguageIdentifier
        trackedOffset = .zero
        missingProbeFrames = 0
        if !preservingLanguage {
            languageReacquisitionMissFrames = 0
        }
        framesUntilValidation = 0
        lastValidatedResult = nil
        pendingSelectorCalibration = nil
        selectorCalibrationDiagnostic = nil
        searchCadence.reset()
    }

    /// Called on the owning capture queue after Label autosaves a correction.
    /// Replaces launch-time catalog so focused drafts affect the deployed
    /// stencil without requiring an application restart.
    func replaceCatalog(_ catalog: MenuStencilCatalog) {
        self.catalog = catalog
        // Rebuilding references does not change the language selected in the
        // running game. Retaining it prevents one transitional frame from
        // reopening the expensive, ambiguous all-language search.
        reset(preservingLanguage: true)
        synchronizeSelectorCorrections(from: catalog)
    }

    private func synchronizeSelectorCorrections(
        from catalog: MenuStencilCatalog
    ) {
        guard let selectorCalibrationURL,
              var calibration = writableCalibration,
              !catalog.selectorCorrectionPlacements.isEmpty else { return }
        var importedCorrectionIdentifiers = Set(
            calibration.reviewedCorrectionIdentifiers ?? []
        )
        var updateCount = 0
        for placement in catalog.selectorCorrectionPlacements {
            guard !importedCorrectionIdentifiers.contains(
                placement.sourceExampleIdentifier
            ) else { continue }
            if let persisted = Self.persistedSelectorBounds(
                in: calibration,
                contextIdentifier: placement.context.storageIdentifier,
                selectedIdentifier: placement.optionIdentifier,
                languageIdentifier: placement.languageIdentifier
            ), Self.sameRect(persisted.left, placement.left),
               Self.sameRect(persisted.right, placement.right) {
                importedCorrectionIdentifiers.insert(
                    placement.sourceExampleIdentifier
                )
                updateCount += 1
                continue
            }
            guard let updated = calibration.replacingSelector(
                contextIdentifier: placement.context.storageIdentifier,
                selectedIdentifier: placement.optionIdentifier,
                languageIdentifier: placement.languageIdentifier,
                leftRect: placement.left,
                rightRect: placement.right
            ) else { continue }
            calibration = updated
            importedCorrectionIdentifiers.insert(
                placement.sourceExampleIdentifier
            )
            updateCount += 1
        }
        guard updateCount > 0 else { return }
        calibration = calibration.recordingReviewedCorrections(
            importedCorrectionIdentifiers
        )
        do {
            try calibration.write(to: selectorCalibrationURL)
            if let selectorCalibrationMirrorURL,
               selectorCalibrationMirrorURL != selectorCalibrationURL {
                try calibration.write(to: selectorCalibrationMirrorURL)
            }
            writableCalibration = calibration
            selectorCalibrationDiagnostic = "imported \(updateCount) human corrections"
            print("MENU_SELECTOR_CORRECTIONS_IMPORTED \(updateCount)")
        } catch {
            selectorCalibrationDiagnostic = "correction-write-failed"
            print("MENU_SELECTOR_CORRECTIONS_FAILED \(error)")
        }
    }

    private static func sameRect(
        _ lhs: CGRect,
        _ rhs: CGRect,
        tolerance: CGFloat = 0.001
    ) -> Bool {
        abs(lhs.minX - rhs.minX) <= tolerance
            && abs(lhs.minY - rhs.minY) <= tolerance
            && abs(lhs.width - rhs.width) <= tolerance
            && abs(lhs.height - rhs.height) <= tolerance
    }

    func observe(_ image: CGImage, timestamp: Double) -> MenuStencilResult? {
        guard let catalog else { return nil }
        if trackedContext == nil,
           !searchCadence.shouldSearch(at: timestamp) {
            return nil
        }
        guard let pixels = ImageStencilPixels(
                image,
                referenceWidth: catalog.referenceWidth,
                referenceHeight: catalog.referenceHeight,
                band: catalog.pixelBand
              ) else { return nil }
        var comparisons = 0
        if let context = trackedContext,
           let scene = catalog.scenes.first(where: { $0.context == context }) {
            // Clear Save is a modal drawn over Select Profile. The parent
            // heading remains perfectly visible, so normal loss-based
            // reacquisition cannot notice the transition. Check the one modal
            // probe while its parent is tracked and let the validated modal
            // take ownership immediately.
            if context == .selectProfile,
               let modal = catalog.scenes.first(where: {
                   $0.context == .clearSave
               }),
               let modalProbe = bestReacquisitionProbe(
                   for: modal,
                   catalog: catalog,
                   pixels: pixels,
                   comparisons: &comparisons
               ),
               modalProbe.confidence >= Self.probeThreshold(for: .clearSave) {
                let parentOffset = trackedOffset
                trackedOffset = CGPoint(
                    x: modalProbe.rect.minX
                        - (modalProbe.nominalBounds ?? modal.probe.bounds).minX,
                    y: modalProbe.rect.minY
                        - (modalProbe.nominalBounds ?? modal.probe.bounds).minY
                )
                if let result = validate(
                    modal,
                    probe: modalProbe,
                    pixels: pixels,
                    image: image,
                    timestamp: timestamp,
                    phase: .searching,
                    comparisons: &comparisons
                ) {
                    trackedContext = .clearSave
                    trackedLanguageIdentifier = modalProbe.languageIdentifier
                        ?? trackedLanguageIdentifier
                    missingProbeFrames = 0
                    framesUntilValidation = Self.validationInterval
                    lastValidatedResult = result
                    return result
                }
                trackedOffset = parentOffset
            }
            let expected = scene.probe.bounds.offsetBy(
                dx: trackedOffset.x,
                dy: trackedOffset.y
            )
            let probe = bestScore(
                variants: variants(
                    scene.probe.searchProbeVariants,
                    matching: trackedLanguageIdentifier
                ),
                pixels: pixels,
                around: expected,
                referenceBounds: scene.probe.bounds,
                deltaX: -4...4,
                deltaY: -6...6,
                comparisons: &comparisons
            )
            if let probe,
               Self.isConfirmationDialog(scene.context),
               let validated = validate(
                   scene,
                   probe: probe,
                   pixels: pixels,
                   image: image,
                   timestamp: timestamp,
                   phase: .tracking,
                   comparisons: &comparisons
               ) {
                // Confirmation-dialog pose comes from the stable Yes/No pair
                // inside validate(), never from the weak animated title.
                missingProbeFrames = 0
                trackedLanguageIdentifier = probe.languageIdentifier
                    ?? trackedLanguageIdentifier
                lastValidatedResult = validated
                framesUntilValidation = Self.validationInterval
                return validated
            }
            if let probe,
               !Self.isConfirmationDialog(scene.context),
               probe.confidence >= Self.probeThreshold(for: scene.context) {
                missingProbeFrames = 0
                trackedLanguageIdentifier = probe.languageIdentifier
                    ?? trackedLanguageIdentifier
                trackedOffset = CGPoint(
                    x: probe.rect.minX
                        - (probe.nominalBounds ?? scene.probe.bounds).minX,
                    y: probe.rect.minY
                        - (probe.nominalBounds ?? scene.probe.bounds).minY
                )
                framesUntilValidation -= 1
                if framesUntilValidation <= 0 || lastValidatedResult == nil {
                    if let checkpoint = validatedSceneCheckpoint(
                        against: scene,
                        currentProbe: probe,
                        catalog: catalog,
                        pixels: pixels,
                        image: image,
                        timestamp: timestamp,
                        comparisons: &comparisons
                    ) {
                        return checkpoint
                    }
                    if let validated = validate(
                        scene,
                        probe: probe,
                        pixels: pixels,
                        image: image,
                        timestamp: timestamp,
                        phase: .tracking,
                        comparisons: &comparisons
                    ) {
                        lastValidatedResult = validated
                        framesUntilValidation = Self.validationInterval
                        return validated
                    }
                    let preferredLanguageIdentifier = trackedLanguageIdentifier
                    reset(preservingLanguage: true)
                    _ = searchCadence.shouldSearch(at: timestamp)
                    return reacquire(
                        pixels: pixels,
                        image: image,
                        timestamp: timestamp,
                        preferredLanguageIdentifier: preferredLanguageIdentifier,
                        comparisons: &comparisons
                    )
                }
                return trackedResult(
                    scene,
                    probe: probe,
                    pixels: pixels,
                    image: image,
                    timestamp: timestamp,
                    comparisons: &comparisons
                )
            }
            missingProbeFrames += 1
            if missingProbeFrames < Self.lossGraceFrames,
               let previous = lastValidatedResult {
                // A brief heading/probe miss can be background animation while
                // the selector has already moved. Keep scene ownership during
                // grace, but refresh the cheap row solve so UI state never
                // freezes on the previous selection.
                let selection = detectSelection(
                    scene,
                    languageIdentifier: trackedLanguageIdentifier,
                    pixels: pixels,
                    image: image,
                    timestamp: timestamp,
                    comparisons: &comparisons,
                    recordsCalibration: false
                )
                let result = MenuStencilResult(
                    context: previous.context,
                    isMatch: true,
                    confidence: probe?.confidence ?? 0,
                    selectedOption: selection.name ?? previous.selectedOption,
                    anchors: previous.anchors,
                    selectorCandidates: selection.matches.isEmpty
                        ? previous.selectorCandidates : selection.matches,
                    selectorSearchRegions: selection.searchRegions,
                    selectorLanguageEvidenceCount: selection.languageEvidenceCount,
                    selectorForegroundEvidenceCount: selection.name == nil
                        ? previous.selectorForegroundEvidenceCount
                        : selection.foregroundEvidenceCount,
                    sceneEvidenceRatio: previous.sceneEvidenceRatio,
                    phase: .tracking,
                    comparisonCount: comparisons,
                    sourceTimestamp: timestamp,
                    languageIdentifier: trackedLanguageIdentifier
                )
                lastValidatedResult = result
                return result
            }
            let preferredLanguageIdentifier = trackedLanguageIdentifier
            reset(preservingLanguage: true)
            return reacquire(
                pixels: pixels,
                image: image,
                timestamp: timestamp,
                preferredLanguageIdentifier: preferredLanguageIdentifier,
                comparisons: &comparisons
            )
        }
        return reacquire(
            pixels: pixels,
            image: image,
            timestamp: timestamp,
            preferredLanguageIdentifier: trackedLanguageIdentifier,
            comparisons: &comparisons
        )
    }

    private func reacquire(
        pixels: ImageStencilPixels,
        image: CGImage,
        timestamp: Double,
        preferredLanguageIdentifier: String? = nil,
        comparisons: inout Int
    ) -> MenuStencilResult? {
        guard let catalog else { return nil }
        let candidates = catalog.scenes.flatMap { scene in
            reacquisitionProbeCandidates(
                for: scene,
                catalog: catalog,
                pixels: pixels,
                preferredLanguageIdentifier: preferredLanguageIdentifier,
                comparisons: &comparisons
            ).map { ProbeCandidate(scene: scene, score: $0) }
        }.sorted { $0.score.confidence > $1.score.confidence }
        if ProcessInfo.processInfo.environment["HKV_MENU_STENCIL_PROBE_LOG"] == "1" {
            var loggedContexts = Set<LabelingContext>()
            print(
                "MENU_PROBE_SCORES " + candidates.compactMap { candidate in
                    guard loggedContexts.insert(candidate.scene.context).inserted
                    else { return nil }
                    let language = candidate.score.languageIdentifier ?? "shared"
                    return "\(candidate.scene.context.storageIdentifier)[\(language)]="
                        + String(format: "%.3f", candidate.score.confidence)
                }.joined(separator: " ")
            )
        }
        let eligible = candidates.filter {
            $0.score.confidence >= Self.probeThreshold(for: $0.scene.context)
        }
        guard let winner = eligible.first else {
            if preferredLanguageIdentifier != nil {
                languageReacquisitionMissFrames += 1
                if languageReacquisitionMissFrames >= Self.validationInterval {
                    languageReacquisitionMissFrames = 0
                    return reacquire(
                        pixels: pixels,
                        image: image,
                        timestamp: timestamp,
                        comparisons: &comparisons
                    )
                }
            }
            return candidates.first.map {
                failedSearchResult($0, image: image, timestamp: timestamp, comparisons: comparisons)
            }
        }
        // Probe ties are expected across both related dialogs and languages.
        // Every language probe has already paid its sparse comparison cost;
        // retain all eligible language hypotheses for plausible scenes so a
        // translated short/shared probe cannot discard the correct language
        // before full same-language anchors resolve it. Tracking then checks
        // only the winning language.
        var plausibleContexts = [LabelingContext]()
        for candidate in eligible {
            if !plausibleContexts.contains(candidate.scene.context) {
                plausibleContexts.append(candidate.scene.context)
            }
            // Compact translated labels can correlate strongly with unrelated
            // short words. During fresh acquisition, retain every scene whose
            // one sparse probe clears threshold, then let full multi-anchor
            // validation choose ownership. Two-row screens such as Screen
            // Scale can otherwise fall just outside an arbitrary top-N cut.
            // Steady tracking still checks only the winning scene/language.
        }
        // These screens have strong full-scene evidence but sparse probes that
        // can rank below compact menu words while their backgrounds animate.
        // Always let eligible hypotheses reach full validation; this adds at
        // most two scenes only during fresh acquisition.
        for reservedContext in [LabelingContext.selectProfile, .controller] {
            if !plausibleContexts.contains(reservedContext),
               eligible.contains(where: {
                   $0.scene.context == reservedContext
               }) {
                plausibleContexts.append(reservedContext)
            }
        }
        let plausible = plausibleContexts.flatMap { context in
            candidates.filter { $0.scene.context == context }
        }
        var validated = [(ProbeCandidate, MenuStencilResult, CGPoint)]()
        for candidate in plausible {
            let offset = CGPoint(
                x: candidate.score.rect.minX
                    - (candidate.score.nominalBounds
                        ?? candidate.scene.probe.bounds).minX,
                y: candidate.score.rect.minY
                    - (candidate.score.nominalBounds
                        ?? candidate.scene.probe.bounds).minY
            )
            trackedOffset = offset
            if let result = validate(
                candidate.scene,
                probe: candidate.score,
                pixels: pixels,
                image: image,
                timestamp: timestamp,
                phase: .searching,
                comparisons: &comparisons
            ) {
                validated.append((candidate, result, trackedOffset))
            }
        }
        if let loggedContext = ProcessInfo.processInfo.environment[
            "HKV_MENU_STENCIL_SCORE_LOG"
        ] {
            let matching = ProcessInfo.processInfo.environment[
                "HKV_MENU_VALIDATED_LOG"
            ] == "1" ? validated : validated.filter {
                $0.0.scene.context.storageIdentifier == loggedContext
            }
            if !matching.isEmpty {
                print("MENU_VALIDATED " + matching.map {
                    let language = $0.0.score.languageIdentifier ?? "shared"
                    return "\($0.0.scene.context.storageIdentifier)[\(language)]="
                        + String(
                            format: "v%.3f,p%.3f,r%.3f",
                            $0.1.confidence,
                            $0.0.score.confidence,
                            $0.1.sceneEvidenceRatio
                        )
                        + "/\($0.1.selectedOption ?? "nil")"
                }.joined(separator: " "))
            }
        }
        func selectorConfidence(_ result: MenuStencilResult) -> Double {
            guard result.selectedOption != nil,
                  !result.selectorCandidates.isEmpty else { return 0 }
            return result.selectorCandidates.map(\.confidence).reduce(0, +)
                / Double(result.selectorCandidates.count)
        }
        func selectorHorizontalSpan(_ result: MenuStencilResult) -> CGFloat {
            guard result.selectorCandidates.count >= 2 else { return 0 }
            let centers = result.selectorCandidates.map { $0.rect.midX }
            guard let minimum = centers.min(),
                  let maximum = centers.max() else { return 0 }
            return maximum - minimum
        }
        let rankedSceneContext = validated.max(by: {
            let leftScore = $0.1.confidence
                + $0.0.score.confidence * 0.20
                + $0.1.sceneEvidenceRatio * 0.35
                + selectorConfidence($0.1) * 0.25
                    * $0.1.sceneEvidenceRatio
            let rightScore = $1.1.confidence
                + $1.0.score.confidence * 0.20
                + $1.1.sceneEvidenceRatio * 0.35
                + selectorConfidence($1.1) * 0.25
                    * $1.1.sceneEvidenceRatio
            return leftScore < rightScore
        })?.0.scene.context
        let modalContext = validated.contains(where: {
            $0.0.scene.context == .clearSave
                && $0.0.score.confidence >= 0.90
                && $0.1.confidence >= 0.80
                && $0.1.sceneEvidenceRatio >= 0.90
        }) ? LabelingContext.clearSave : nil
        let gameOptionsContext = eligible.contains(where: {
            $0.scene.context == .gameOptions
                && $0.score.confidence >= 0.65
        }) && validated.contains(where: {
            $0.0.scene.context == .gameOptions
                && $0.1.sceneEvidenceRatio >= 0.50
                && selectorHorizontalSpan($0.1) >= 240
        }) ? LabelingContext.gameOptions : nil
        let bestSceneContext = modalContext
            ?? gameOptionsContext
            ?? rankedSceneContext
        let sameScene = bestSceneContext.map { context in
            validated.filter { $0.0.scene.context == context }
        } ?? []
        let resolved = sameScene.max(by: {
            if $0.1.confidence == $1.1.confidence {
                return $0.0.score.confidence < $1.0.score.confidence
            }
            return $0.1.confidence < $1.1.confidence
        })
        guard let resolved else {
            if preferredLanguageIdentifier != nil {
                languageReacquisitionMissFrames += 1
                if languageReacquisitionMissFrames >= Self.validationInterval {
                    languageReacquisitionMissFrames = 0
                    return reacquire(
                        pixels: pixels,
                        image: image,
                        timestamp: timestamp,
                        comparisons: &comparisons
                    )
                }
            }
            return failedSearchResult(
                winner,
                image: image,
                timestamp: timestamp,
                comparisons: comparisons
            )
        }
        if preferredLanguageIdentifier != nil,
           (resolved.1.sceneEvidenceRatio < 0.90
                || resolved.1.confidence < 0.75) {
            // A menu-language change can leave a weak scene from the previous
            // language looking plausible indefinitely. Once preferred-language
            // anchors are this incomplete, pay for one unrestricted search so
            // a strong scene in the new language can take ownership.
            return reacquire(
                pixels: pixels,
                image: image,
                timestamp: timestamp,
                comparisons: &comparisons
            )
        }
        let baseResult = resolved.1
        func bracketsResolvedAnchor(
            _ candidate: MenuStencilResult
        ) -> Bool {
            guard let selected = candidate.selectedOption,
                  candidate.selectorCandidates.count == 2 else {
                return false
            }
            let namedAnchor = baseResult.anchors.first(where: {
                $0.name == selected
            })?.rect
            let anchorRect = namedAnchor ?? resolved.0.scene.options.first(where: {
                $0.name == selected
            }).map {
                outputRect(
                    $0.bounds.offsetBy(
                        dx: resolved.2.x,
                        dy: resolved.2.y
                    ),
                    image: image,
                    referenceWidth: resolved.0.scene.referenceWidth,
                    referenceHeight: resolved.0.scene.referenceHeight
                )
            }
            guard let anchorRect else { return false }
            let selectors = candidate.selectorCandidates.sorted {
                $0.rect.midX < $1.rect.midX
            }
            let solvedRegions = candidate.selectorSearchRegions.filter {
                $0.isSolved
            }
            let verticallyAligned =
                abs(selectors[0].rect.midY - anchorRect.midY) <= 16
                && abs(selectors[1].rect.midY - anchorRect.midY) <= 16
            let insideSearchedRegions = selectors.allSatisfy { selector in
                    solvedRegions.contains { $0.rect.intersects(selector.rect) }
                }
            guard verticallyAligned, insideSearchedRegions else { return false }
            guard namedAnchor != nil else {
                // Clear Save 1...4 are generated from one shared source label,
                // so there is no independent green anchor per row. Their
                // deployed row bounds and solved blue regions are the visible
                // geometry contract.
                return selectors[1].rect.midX - selectors[0].rect.midX >= 24
            }
            // Decorations can touch the first/last antialiased glyph pixels;
            // compare centers so that small human boxes remain valid while a
            // false hit centered inside a translated word is still rejected.
            return selectors[0].rect.midX < anchorRect.minX
                && selectors[1].rect.midX > anchorRect.maxX
        }
        let baseMinimumConfidence = baseResult.selectorCandidates
            .map(\.confidence).min() ?? 0
        let baseIsStrong = baseResult.selectedOption != nil
            && bracketsResolvedAnchor(baseResult)
            && (baseMinimumConfidence >= 0.65
                || baseResult.selectorForegroundEvidenceCount >= 12
                || baseResult.context == .selectProfile
                    && baseResult.selectorLanguageEvidenceCount > 0)
        let geometricallyValid = sameScene.filter {
            bracketsResolvedAnchor($0.1)
        }
        let foregroundThreshold = bestSceneContext == .mainTitle
            ? 12 : Self.selectorMinimumForegroundPixels
        let foregroundSupported = geometricallyValid.filter {
            $0.1.selectorForegroundEvidenceCount >= foregroundThreshold
        }
        let selectorPool = foregroundSupported.isEmpty
            ? geometricallyValid : foregroundSupported
        // Full-scene anchors own the language. Keep a strong solve from that
        // hypothesis. A weak/absent solve may use the language-independent
        // cursor stencil at another measured position only when its yellow
        // pair visibly brackets the resolved green text and lies inside the
        // blue regions that were actually searched.
        let selectorResolved = baseIsStrong
            ? resolved
            : selectorPool.max {
                selectorConfidence($0.1) < selectorConfidence($1.1)
            }
        let result = selectorResolved.map { selected in
            MenuStencilResult(
                context: baseResult.context,
                isMatch: baseResult.isMatch,
                confidence: baseResult.confidence,
                selectedOption: selected.1.selectedOption,
                anchors: baseResult.anchors,
                selectorCandidates: selected.1.selectorCandidates,
                selectorSearchRegions: selected.1.selectorSearchRegions,
                selectorLanguageEvidenceCount:
                    selected.1.selectorLanguageEvidenceCount,
                selectorForegroundEvidenceCount:
                    selected.1.selectorForegroundEvidenceCount,
                sceneEvidenceRatio: baseResult.sceneEvidenceRatio,
                phase: baseResult.phase,
                comparisonCount: comparisons,
                sourceTimestamp: baseResult.sourceTimestamp,
                languageIdentifier: resolved.0.score.languageIdentifier
                    ?? preferredLanguageIdentifier
                    ?? trackedLanguageIdentifier
            )
        } ?? baseResult
        trackedOffset = resolved.2
        trackedContext = resolved.0.scene.context
        trackedLanguageIdentifier = resolved.0.score.languageIdentifier
            ?? preferredLanguageIdentifier
            ?? trackedLanguageIdentifier
        missingProbeFrames = 0
        languageReacquisitionMissFrames = 0
        framesUntilValidation = Self.validationInterval
        lastValidatedResult = result
        return result
    }

    /// A tracked heading can remain visible on a different menu and keep a
    /// stale scene alive indefinitely. At the existing validation cadence,
    /// compare one same-language probe per scene, fully validate only the
    /// strongest few, and rank them against the tracked scene. This keeps the
    /// ordinary frame path narrow while allowing low-probe, high-anchor scenes
    /// such as Controller and Select Profile to take ownership.
    private func validatedSceneCheckpoint(
        against currentScene: MenuStencilCatalog.Scene,
        currentProbe: ScoredVariant,
        catalog: MenuStencilCatalog,
        pixels: ImageStencilPixels,
        image: CGImage,
        timestamp: Double,
        comparisons: inout Int
    ) -> MenuStencilResult? {
        typealias ValidatedCandidate = (
            candidate: ProbeCandidate,
            result: MenuStencilResult,
            offset: CGPoint
        )
        let currentOffset = trackedOffset
        var validated = [ValidatedCandidate]()
        if let result = validate(
            currentScene,
            probe: currentProbe,
            pixels: pixels,
            image: image,
            timestamp: timestamp,
            phase: .tracking,
            comparisons: &comparisons
        ) {
            validated.append((
                ProbeCandidate(scene: currentScene, score: currentProbe),
                result,
                currentOffset
            ))
        }

        var candidates = catalog.scenes.compactMap { scene -> ProbeCandidate? in
            guard scene.context != currentScene.context else { return nil }
            var best: ScoredVariant?
            for offset in probeSearchOffsets(for: scene, catalog: catalog) {
                guard let score = bestScore(
                    variants: variants(
                        scene.probe.searchProbeVariants,
                        matching: trackedLanguageIdentifier
                    ),
                    pixels: pixels,
                    around: scene.probe.bounds.offsetBy(
                        dx: offset.x, dy: offset.y
                    ),
                    referenceBounds: scene.probe.bounds,
                    deltaX: -3...3,
                    deltaY: -10...10,
                    comparisons: &comparisons
                ) else { continue }
                if score.confidence > (best?.confidence ?? -.infinity) {
                    best = score
                }
            }
            guard let best,
                  best.confidence >= Self.probeThreshold(for: scene.context)
            else { return nil }
            return ProbeCandidate(scene: scene, score: best)
        }.sorted { $0.score.confidence > $1.score.confidence }

        let reservedContexts: Set<LabelingContext> = [
            .controller, .selectProfile, .clearSave,
        ]
        for scene in catalog.scenes where
            scene.context != currentScene.context
                && reservedContexts.contains(scene.context) {
            guard let score = bestReacquisitionProbe(
                for: scene,
                catalog: catalog,
                pixels: pixels,
                comparisons: &comparisons
            ), score.confidence >= 0.25 else { continue }
            candidates.removeAll { $0.scene.context == scene.context }
            candidates.append(ProbeCandidate(scene: scene, score: score))
        }
        candidates.sort { $0.score.confidence > $1.score.confidence }
        var selectedCandidates = [ProbeCandidate]()
        var selectedContexts = Set<LabelingContext>()
        for candidate in candidates where
            selectedCandidates.count < Self.sceneChallengeCandidateLimit {
            if selectedContexts.insert(candidate.scene.context).inserted {
                selectedCandidates.append(candidate)
            }
        }
        for context in reservedContexts where !selectedContexts.contains(context) {
            if let candidate = candidates.first(where: {
                $0.scene.context == context
            }) {
                selectedContexts.insert(context)
                selectedCandidates.append(candidate)
            }
        }

        for candidate in selectedCandidates {
            let candidateOffset = CGPoint(
                x: candidate.score.rect.minX
                    - (candidate.score.nominalBounds
                        ?? candidate.scene.probe.bounds).minX,
                y: candidate.score.rect.minY
                    - (candidate.score.nominalBounds
                        ?? candidate.scene.probe.bounds).minY
            )
            trackedOffset = candidateOffset
            guard let result = validate(
                candidate.scene,
                probe: candidate.score,
                pixels: pixels,
                image: image,
                timestamp: timestamp,
                phase: .searching,
                comparisons: &comparisons
            ) else { continue }
            validated.append((candidate, result, trackedOffset))
        }

        func selectorConfidence(_ result: MenuStencilResult) -> Double {
            guard result.selectedOption != nil,
                  !result.selectorCandidates.isEmpty else { return 0 }
            return result.selectorCandidates.map(\.confidence).reduce(0, +)
                / Double(result.selectorCandidates.count)
        }
        func sceneScore(_ entry: ValidatedCandidate) -> Double {
            entry.result.confidence
                + entry.candidate.score.confidence * 0.20
                + entry.result.sceneEvidenceRatio * 0.35
                + selectorConfidence(entry.result) * 0.25
                    * entry.result.sceneEvidenceRatio
        }
        let current = validated.first {
            $0.candidate.scene.context == currentScene.context
        }
        // Once the tracked scene itself fails full validation, do not promote
        // a challenger from this deliberately bounded checkpoint. Returning
        // nil makes observe() retry the current scene and then use the proven
        // full reacquisition ranking when it still fails. This transition is
        // rare, and avoids a partial candidate set latching onto a correlated
        // page such as Video Advanced Settings while entering Audio.
        guard let current else {
            trackedOffset = currentOffset
            return nil
        }
        if current.result.sceneEvidenceRatio < 0.90
            || current.result.confidence < 0.75 {
            // A wrong-language scene can keep a correlated heading and most
            // of its sparse anchors. Weak current ownership must not prevent
            // the unrestricted language search from seeing a complete scene.
            trackedOffset = currentOffset
            return reacquire(
                pixels: pixels,
                image: image,
                timestamp: timestamp,
                comparisons: &comparisons
            )
        }
        let challenger = validated.filter {
            $0.candidate.scene.context != currentScene.context
        }.max { sceneScore($0) < sceneScore($1) }
        let resolved: ValidatedCandidate
        if let challenger,
           sceneScore(challenger) >= sceneScore(current)
                    + Self.sceneChallengeWinningMargin {
            // The checkpoint intentionally evaluates a bounded candidate set.
            // Treat a winning challenger as evidence that scene ownership is
            // stale, then let full acquisition choose among every eligible
            // scene/language. Direct promotion here allowed correlated shared
            // rows to latch Video Advanced Settings onto unrelated menus.
            trackedOffset = currentOffset
            return reacquire(
                pixels: pixels,
                image: image,
                timestamp: timestamp,
                preferredLanguageIdentifier: trackedLanguageIdentifier,
                comparisons: &comparisons
            )
        } else {
            resolved = current
        }

        let base = resolved.result
        let result = MenuStencilResult(
            context: base.context,
            isMatch: base.isMatch,
            confidence: base.confidence,
            selectedOption: base.selectedOption,
            anchors: base.anchors,
            selectorCandidates: base.selectorCandidates,
            selectorSearchRegions: base.selectorSearchRegions,
            selectorLanguageEvidenceCount: base.selectorLanguageEvidenceCount,
            selectorForegroundEvidenceCount: base.selectorForegroundEvidenceCount,
            sceneEvidenceRatio: base.sceneEvidenceRatio,
            phase: base.phase,
            comparisonCount: comparisons,
            sourceTimestamp: base.sourceTimestamp,
            languageIdentifier: resolved.candidate.score.languageIdentifier
                ?? trackedLanguageIdentifier
        )
        trackedOffset = resolved.offset
        trackedContext = resolved.candidate.scene.context
        trackedLanguageIdentifier = resolved.candidate.score.languageIdentifier
            ?? trackedLanguageIdentifier
        missingProbeFrames = 0
        framesUntilValidation = Self.validationInterval
        lastValidatedResult = result
        return result
    }

    private func bestReacquisitionProbe(
        for scene: MenuStencilCatalog.Scene,
        catalog: MenuStencilCatalog,
        pixels: ImageStencilPixels,
        comparisons: inout Int
    ) -> ScoredVariant? {
        reacquisitionProbeCandidates(
            for: scene,
            catalog: catalog,
            pixels: pixels,
            comparisons: &comparisons
        ).max { $0.confidence < $1.confidence }
    }

    private func reacquisitionProbeCandidates(
        for scene: MenuStencilCatalog.Scene,
        catalog: MenuStencilCatalog,
        pixels: ImageStencilPixels,
        preferredLanguageIdentifier: String? = nil,
        comparisons: inout Int
    ) -> [ScoredVariant] {
        var scores = [ScoredVariant]()
        for variant in variants(
            scene.probe.searchProbeVariants,
            matching: preferredLanguageIdentifier
        ) {
        var best: ScoredVariant?
        for offset in probeSearchOffsets(for: scene, catalog: catalog) {
            guard let score = bestScore(
                    variants: [variant],
                pixels: pixels,
                around: scene.probe.bounds.offsetBy(dx: offset.x, dy: offset.y),
                referenceBounds: scene.probe.bounds,
                deltaX: -3...3,
                deltaY: -10...10,
                comparisons: &comparisons
            ) else { continue }
            if score.confidence > (best?.confidence ?? -.infinity) {
                best = score
            }
        }
            if let best { scores.append(best) }
        }
        return scores
    }

    static func clearSaveVerticalProbeOffsets(
        referenceY: CGFloat,
        profileRowCenters: [CGFloat]
    ) -> [CGFloat] {
        let centers = profileRowCenters.sorted()
        guard let source = centers.min(by: {
            abs($0 - referenceY) < abs($1 - referenceY)
        }) else { return [0] }
        return centers.map { $0 - source }
    }

    private func probeSearchOffsets(
        for scene: MenuStencilCatalog.Scene,
        catalog: MenuStencilCatalog
    ) -> [CGPoint] {
        guard scene.context == .clearSave,
              let profile = catalog.scenes.first(where: {
                  $0.context == .selectProfile
              }) else { return [.zero] }
        let rowCenters = profile.options.compactMap { option -> CGFloat? in
            guard option.classIdentifier.hasPrefix("select-profile.slot-") else {
                return nil
            }
            return option.bounds.midY
        }
        return Self.clearSaveVerticalProbeOffsets(
            referenceY: scene.probe.bounds.midY,
            profileRowCenters: rowCenters
        ).map { CGPoint(x: 0, y: $0) }
    }

    private func validate(
        _ scene: MenuStencilCatalog.Scene,
        probe: ScoredVariant,
        pixels: ImageStencilPixels,
        image: CGImage,
        timestamp: Double,
        phase: MenuStencilResult.Phase,
        comparisons: inout Int
    ) -> MenuStencilResult? {
        var scored = [(MenuStencilCatalog.Anchor, ScoredVariant)]()
        let validationThreshold = Self.validationThreshold(for: scene.context)
        let anchorDeltaX: ClosedRange<Int> = Self.isConfirmationDialog(scene.context)
            ? -8...8 : -4...4
        let anchorDeltaY: ClosedRange<Int> = Self.isConfirmationDialog(scene.context)
            ? -12...12 : -6...6
        for anchor in scene.anchors {
            if anchor.classIdentifier == scene.probe.classIdentifier {
                if let fullProbe = bestScore(
                    variants: variants(
                        anchor.variants,
                        matching: probe.languageIdentifier
                    ),
                    pixels: pixels,
                    around: probe.rect,
                    deltaX: -2...2,
                    deltaY: -2...2,
                    comparisons: &comparisons
                ) {
                    scored.append((anchor, fullProbe))
                }
                continue
            }
            let expected = anchor.bounds.offsetBy(
                dx: trackedOffset.x,
                dy: trackedOffset.y
            )
            var score = bestScore(
                variants: variants(
                    anchor.variants,
                    matching: probe.languageIdentifier
                ),
                pixels: pixels,
                around: expected,
                referenceBounds: anchor.bounds,
                deltaX: anchorDeltaX,
                deltaY: anchorDeltaY,
                comparisons: &comparisons
            )
            if (score?.confidence ?? -.infinity) < Self.anchorThreshold,
               let calibratedBounds = anchor.calibratedBounds {
                let calibrated = bestScore(
                    variants: variants(
                        anchor.variants,
                        matching: probe.languageIdentifier
                    ),
                    pixels: pixels,
                    around: calibratedBounds.offsetBy(
                        dx: trackedOffset.x, dy: trackedOffset.y
                    ),
                    referenceBounds: anchor.bounds,
                    deltaX: anchorDeltaX,
                    deltaY: anchorDeltaY,
                    comparisons: &comparisons
                )
                if let calibrated,
                   calibrated.confidence > (score?.confidence ?? -.infinity) {
                    score = calibrated
                }
            }
            if let score {
                scored.append((anchor, score))
            }
        }
        if Self.isConfirmationDialog(scene.context) {
            let rowIdentifiers: Set<String> = [
                LabelingClassIdentity.yes,
                LabelingClassIdentity.no,
            ]
            let stableRows = scored.filter {
                rowIdentifiers.contains($0.0.classIdentifier)
                    && $0.1.confidence >= validationThreshold
            }
            if stableRows.count == 2,
               let offsetX = stableRows.map({ anchor, score in
                   score.rect.midX
                       - (score.nominalBounds ?? anchor.bounds).midX
               }).median,
               let offsetY = stableRows.map({ anchor, score in
                   score.rect.midY
                       - (score.nominalBounds ?? anchor.bounds).midY
               }).median {
                // The title sits over animated gameplay and is a poor pose
                // estimator. Both fixed dialog rows agree on screen pose and
                // keep inactive selector search boxes aligned with their row.
                trackedOffset = CGPoint(x: offsetX, y: offsetY)
                if let titleIndex = scored.firstIndex(where: {
                    $0.0.classIdentifier == scene.probe.classIdentifier
                }), let correctedTitle = bestScore(
                    variants: variants(
                        scene.probe.variants,
                        matching: probe.languageIdentifier
                    ),
                    pixels: pixels,
                    around: scene.probe.bounds.offsetBy(
                        dx: trackedOffset.x,
                        dy: trackedOffset.y
                    ),
                    referenceBounds: scene.probe.bounds,
                    deltaX: -3...3,
                    deltaY: -3...3,
                    comparisons: &comparisons
                ) {
                    scored[titleIndex] = (scene.probe, correctedTitle)
                }
            }
        }
        let acceptedCount = scored.count {
            $0.1.confidence >= validationThreshold
        }
        let weighted = scored.map { anchor, score -> (Double, Double) in
            let weight: Double
            if anchor.classIdentifier == scene.probe.classIdentifier {
                // The probe already admitted this scene to full validation.
                // Reusing it as dominant evidence lets one short translated
                // word validate the wrong page. Give independent anchors the
                // larger vote when discriminating otherwise similar menus.
                weight = 1.0
            } else if MenuStencilCatalog.screenMembershipCount(
                anchor.classIdentifier
            ) == 1 {
                weight = 2.0
            } else {
                weight = 0.4
            }
            return (score.confidence * weight, weight)
        }
        let confidence = weighted.reduce(0) { $0 + $1.0 }
            / max(1, weighted.reduce(0) { $0 + $1.1 })
        // Options has eight independent, fixed text rows. Its first row is
        // also the probe and can be crossed by the selected decoration, so a
        // lower per-anchor threshold is useful only when several other rows
        // agree. Requiring four rejected the strongest wrong-language
        // hypothesis (3/8) while admitting the Japanese holdout (4/8).
        let requiredCount = scene.context == .options
            ? min(4, scene.anchors.count)
            : min(2, scene.anchors.count)
        if ProcessInfo.processInfo.environment["HKV_MENU_ANCHOR_DETAIL_LOG"]
            == scene.context.storageIdentifier {
            let language = probe.languageIdentifier ?? "shared"
            print(
                "MENU_ANCHOR_DETAILS \(scene.context.storageIdentifier)[\(language)] "
                    + scored.map {
                        "\($0.0.classIdentifier)="
                            + String(format: "%.3f", $0.1.confidence)
                            + "[\($0.1.languageIdentifier ?? "shared")]"
                    }.joined(separator: " ")
            )
        }
        if ProcessInfo.processInfo.environment["HKV_MENU_ANCHOR_LOG"]
            == scene.context.storageIdentifier {
            let language = probe.languageIdentifier ?? "shared"
            let independentAccepted = scored.count {
                $0.0.classIdentifier != scene.probe.classIdentifier
                    && $0.1.confidence >= validationThreshold
            }
            print(
                "MENU_ANCHORS \(scene.context.storageIdentifier)[\(language)] "
                    + "accepted=\(acceptedCount)/\(scored.count) "
                    + "independent=\(independentAccepted) "
                    + "confidence=\(String(format: "%.3f", confidence))"
            )
        }
        guard confidence >= validationThreshold,
              acceptedCount >= requiredCount else { return nil }
        let anchorMatches = scored.map { anchor, score in
            MenuStencilMatch(
                classIdentifier: anchor.classIdentifier,
                name: anchor.name,
                rect: outputRect(
                    score.rect,
                    image: image,
                    referenceWidth: scene.referenceWidth,
                    referenceHeight: scene.referenceHeight
                ),
                confidence: score.confidence,
                reference: score.reference
            )
        }
        let currentAnchorBounds = Dictionary(
            uniqueKeysWithValues: scored.map {
                ($0.0.classIdentifier, $0.1.rect)
            }
        )
        var selection = detectSelection(
            scene,
            languageIdentifier: probe.languageIdentifier,
            pixels: pixels,
            image: image,
            timestamp: timestamp,
            comparisons: &comparisons,
            recordsCalibration: false,
            anchorBoundsByIdentifier: currentAnchorBounds
        )
        if let selectedName = selection.name,
           let selectedOption = scene.options.first(where: {
               $0.name == selectedName
           }),
           let selectedAnchor = anchorMatches.first(where: {
               $0.name == selectedName
           }), selection.matches.contains(where: {
               $0.rect.intersects(selectedAnchor.rect)
           }) {
            // A broad recovery can find selector-shaped strokes inside a long
            // translated word. Remove that row and rank the remaining actual
            // flanking pairs; never publish a selection whose yellow boxes do
            // not bracket its current green text box.
            selection = detectSelection(
                scene,
                languageIdentifier: probe.languageIdentifier,
                pixels: pixels,
                image: image,
                timestamp: timestamp,
                comparisons: &comparisons,
                recordsCalibration: false,
                anchorBoundsByIdentifier: currentAnchorBounds,
                excludingOptionIdentifiers: [selectedOption.classIdentifier]
            )
            if selection.name == selectedName {
                selection = (nil, [], selection.searchRegions, 0, 0)
            }
        }
        return MenuStencilResult(
            context: scene.context,
            isMatch: true,
            confidence: confidence,
            selectedOption: selection.name,
            anchors: anchorMatches,
            selectorCandidates: selection.matches,
            selectorSearchRegions: selection.searchRegions,
            selectorLanguageEvidenceCount: selection.languageEvidenceCount,
            selectorForegroundEvidenceCount: selection.foregroundEvidenceCount,
            sceneEvidenceRatio: Double(acceptedCount) / Double(max(1, scored.count)),
            phase: phase,
            comparisonCount: comparisons,
            sourceTimestamp: timestamp,
            languageIdentifier: probe.languageIdentifier
                ?? trackedLanguageIdentifier
        )
    }

    private func trackedResult(
        _ scene: MenuStencilCatalog.Scene,
        probe: ScoredVariant,
        pixels: ImageStencilPixels,
        image: CGImage,
        timestamp: Double,
        comparisons: inout Int
    ) -> MenuStencilResult? {
        guard let previous = lastValidatedResult else { return nil }
        let selection = detectSelection(
            scene,
            languageIdentifier: trackedLanguageIdentifier,
            pixels: pixels,
            image: image,
            timestamp: timestamp,
            comparisons: &comparisons,
            recordsCalibration: true
        )
        let anchors = previous.anchors.map { match in
            guard match.classIdentifier == scene.probe.classIdentifier else { return match }
            return MenuStencilMatch(
                classIdentifier: match.classIdentifier,
                name: match.name,
                rect: outputRect(
                    probe.rect,
                    image: image,
                    referenceWidth: scene.referenceWidth,
                    referenceHeight: scene.referenceHeight
                ),
                confidence: probe.confidence,
                reference: probe.reference
            )
        }
        let result = MenuStencilResult(
            context: scene.context,
            isMatch: true,
            confidence: probe.confidence,
            selectedOption: selection.name,
            anchors: anchors,
            selectorCandidates: selection.matches,
            selectorSearchRegions: selection.searchRegions,
            selectorLanguageEvidenceCount: selection.languageEvidenceCount,
            selectorForegroundEvidenceCount: selection.foregroundEvidenceCount,
            sceneEvidenceRatio: previous.sceneEvidenceRatio,
            phase: .tracking,
            comparisonCount: comparisons,
            sourceTimestamp: timestamp,
            languageIdentifier: trackedLanguageIdentifier
        )
        lastValidatedResult = result
        return result
    }

    private func selectorKey(
        scene: MenuStencilCatalog.Scene,
        option: MenuStencilCatalog.Option,
        languageIdentifier: String?
    ) -> String {
        let language = languageIdentifier
            ?? HollowKnightMenuLanguage.english.rawValue
        return "\(scene.context.storageIdentifier):\(option.classIdentifier):\(language)"
    }

    private func selectorBounds(
        scene: MenuStencilCatalog.Scene,
        option: MenuStencilCatalog.Option,
        languageIdentifier: String? = nil
    ) -> SelectorBounds? {
        if let learned = learnedSelectorBounds[selectorKey(
            scene: scene,
            option: option,
            languageIdentifier: languageIdentifier
        )] {
            return learned
        }
        guard let left = option.leftSelector?.bounds,
              let right = option.rightSelector?.bounds else { return nil }
        return SelectorBounds(
            left: option.leftSelector?.bounds(for: languageIdentifier) ?? left,
            right: option.rightSelector?.bounds(for: languageIdentifier) ?? right
        )
    }

    private func selectorRowBand(
        scene: MenuStencilCatalog.Scene,
        option: MenuStencilCatalog.Option,
        languageIdentifier: String?
    ) -> ClosedRange<CGFloat>? {
        let rows = scene.options.compactMap { candidate -> (
            identifier: String,
            centerY: CGFloat
        )? in
            guard let bounds = selectorBounds(
                scene: scene,
                option: candidate,
                languageIdentifier: languageIdentifier
            ) else {
                return nil
            }
            return (
                candidate.classIdentifier,
                (bounds.left.midY + bounds.right.midY) * 0.5
            )
        }.sorted { $0.centerY < $1.centerY }
        guard let optionCenter = rows.first(where: {
            $0.identifier == option.classIdentifier
        })?.centerY else { return nil }

        // Keyboard and remapping screens have two controls on the same visual
        // row. Treat their nearly identical Y coordinates as one row; sorting
        // every control independently creates a zero-width band that rejects
        // one column's valid selector before it can be scored.
        var rowCenters = [CGFloat]()
        for row in rows {
            if let last = rowCenters.last,
               abs(last - row.centerY) <= 4 {
                rowCenters[rowCenters.count - 1] = (last + row.centerY) * 0.5
            } else {
                rowCenters.append(row.centerY)
            }
        }
        guard let index = rowCenters.indices.min(by: {
            abs(rowCenters[$0] - optionCenter)
                < abs(rowCenters[$1] - optionCenter)
        }) else { return nil }
        let center = rowCenters[index]
        let lower = index > 0
            ? (rowCenters[index - 1] + center) * 0.5
            : CGFloat.zero
        let upper = index + 1 < rowCenters.count
            ? (center + rowCenters[index + 1]) * 0.5
            : CGFloat(scene.referenceHeight)
        return (lower + trackedOffset.y)...(upper + trackedOffset.y)
    }

    private func recordSelectorCalibration(
        scene: MenuStencilCatalog.Scene,
        option: MenuStencilCatalog.Option,
        left: CGRect,
        right: CGRect,
        timestamp: Double
    ) {
        guard let selectorCalibrationURL,
              let writableCalibration else {
            selectorCalibrationDiagnostic = "disabled"
            return
        }
        guard let current = selectorBounds(
                scene: scene,
                option: option,
                languageIdentifier: trackedLanguageIdentifier
              ) else {
            selectorCalibrationDiagnostic = "missing-bounds"
            return
        }
        guard abs(current.left.midX - left.midX)
                <= CGFloat(Self.selectorRecoveryRadiusX(for: scene.context)),
              abs(current.left.midY - left.midY)
                <= CGFloat(Self.selectorRecoveryRadiusY),
              abs(current.right.midX - right.midX)
                <= CGFloat(Self.selectorRecoveryRadiusX(for: scene.context)),
              abs(current.right.midY - right.midY)
                <= CGFloat(Self.selectorRecoveryRadiusY) else {
            selectorCalibrationDiagnostic = "outside-recovery"
            return
        }
        let languageIdentifier = trackedLanguageIdentifier
            ?? HollowKnightMenuLanguage.english.rawValue
        let key = selectorKey(
            scene: scene,
            option: option,
            languageIdentifier: languageIdentifier
        )
        // Menu positions are absolute screen-space coordinates. The scene
        // probe offset is useful for searching a moving hypothesis, but it is
        // also noisy by a few pixels as animated text is rescored. Subtracting
        // it here made a motionless, correctly solved decoration appear to
        // move forever and prevented permanent calibration.
        let normalized = SelectorCalibrationSample(
            left: left,
            right: right,
            timestamp: timestamp
        )
        if pendingSelectorCalibration?.key != key
            || timestamp - (pendingSelectorCalibration?.samples.last?.timestamp
                ?? -Double.infinity) > 1.5
            || timestamp < (pendingSelectorCalibration?.samples.last?.timestamp
                ?? -Double.infinity) {
            pendingSelectorCalibration = PendingSelectorCalibration(
                key: key,
                samples: [normalized]
            )
            selectorCalibrationDiagnostic = "collecting 1"
            return
        }
        if let previousTimestamp = pendingSelectorCalibration?.samples.last?.timestamp,
           timestamp - previousTimestamp < 0.08 {
            selectorCalibrationDiagnostic = "collecting "
                + "\(pendingSelectorCalibration?.samples.count ?? 0)"
            return
        }
        var pendingSamples = pendingSelectorCalibration?.samples ?? []
        pendingSamples.append(normalized)
        pendingSamples = Array(
            pendingSamples.filter {
                timestamp - $0.timestamp <= 3
            }.suffix(48)
        )
        pendingSelectorCalibration = PendingSelectorCalibration(
            key: key,
            samples: pendingSamples
        )
        selectorCalibrationDiagnostic = "collecting \(pendingSamples.count)"
        guard pendingSamples.count >= 8,
              timestamp - (pendingSamples.first?.timestamp ?? timestamp) >= 2 else {
            return
        }

        // Selector artwork animates, and different animation-phase stencils
        // can occasionally land a pixel or two apart. Persist the dominant
        // stable cluster instead of requiring every observation—including a
        // single outlier—to be identical.
        let coherent = Self.dominantStableSelectorCluster(pendingSamples)
        guard coherent.count >= 8,
              coherent.count * 3 >= pendingSamples.count * 2,
              (coherent.last?.timestamp ?? timestamp)
                - (coherent.first?.timestamp ?? timestamp) >= 2 else {
            selectorCalibrationDiagnostic = "unstable \(coherent.count)/\(pendingSamples.count)"
            return
        }
        let learned = SelectorBounds(
            left: Self.medianRect(coherent.map(\.left)),
            right: Self.medianRect(coherent.map(\.right))
        )
        // Catalog geometry may already be correct because it was derived from
        // a live capture or a human correction. That does not mean this
        // language has been written to the deployed JSON. Compare against the
        // writable calibration itself before deciding there is nothing to do.
        if let persisted = Self.persistedSelectorBounds(
            in: writableCalibration,
            contextIdentifier: scene.context.storageIdentifier,
            selectedIdentifier: option.classIdentifier,
            languageIdentifier: languageIdentifier
        ), Self.centerDistance(persisted.left, learned.left) < 1,
           Self.centerDistance(persisted.right, learned.right) < 1,
           FileManager.default.fileExists(
                atPath: selectorCalibrationURL.path
           ) {
            pendingSelectorCalibration = nil
            selectorCalibrationDiagnostic = "stored"
            return
        }
        guard let updated = writableCalibration.replacingSelector(
                contextIdentifier: scene.context.storageIdentifier,
                selectedIdentifier: option.classIdentifier,
                languageIdentifier: languageIdentifier,
                leftRect: learned.left,
                rightRect: learned.right
              ) else { return }
        do {
            try updated.write(to: selectorCalibrationURL)
            if let selectorCalibrationMirrorURL,
               selectorCalibrationMirrorURL != selectorCalibrationURL {
                try updated.write(to: selectorCalibrationMirrorURL)
            }
            self.writableCalibration = updated
            learnedSelectorBounds[key] = learned
            pendingSelectorCalibration = nil
            selectorCalibrationDiagnostic = "saved \(key)"
            print("MENU_SELECTOR_CALIBRATION_SAVED \(key)")
        } catch {
            selectorCalibrationDiagnostic = "write-failed"
            print("MENU_SELECTOR_CALIBRATION_FAILED \(key) \(error)")
        }
    }

    private static func dominantStableSelectorCluster(
        _ samples: [SelectorCalibrationSample]
    ) -> [SelectorCalibrationSample] {
        samples.map { pivot in
            samples.filter {
                centerDistance($0.left, pivot.left) <= 2.5
                    && centerDistance($0.right, pivot.right) <= 2.5
            }
        }.max { left, right in
            if left.count != right.count { return left.count < right.count }
            return (left.last?.timestamp ?? -Double.infinity)
                < (right.last?.timestamp ?? -Double.infinity)
        } ?? []
    }

    private static func persistedSelectorBounds(
        in calibration: MenuStencilCalibration,
        contextIdentifier: String,
        selectedIdentifier: String,
        languageIdentifier: String
    ) -> SelectorBounds? {
        guard let selector = calibration.scenes.first(where: {
            $0.contextIdentifier == contextIdentifier
        })?.selectors.first(where: {
            $0.selectedIdentifier == selectedIdentifier
        }) else { return nil }
        if let placement = selector.placement(for: languageIdentifier) {
            return SelectorBounds(
                left: placement.leftRect.cgRect,
                right: placement.rightRect.cgRect
            )
        }
        guard languageIdentifier == HollowKnightMenuLanguage.english.rawValue else {
            return nil
        }
        return SelectorBounds(
            left: selector.leftRect.cgRect,
            right: selector.rightRect.cgRect
        )
    }

    private static func medianRect(_ rects: [CGRect]) -> CGRect {
        CGRect(
            x: rects.map(\.minX).median ?? 0,
            y: rects.map(\.minY).median ?? 0,
            width: rects.map(\.width).median ?? 0,
            height: rects.map(\.height).median ?? 0
        )
    }

    private static func centerDistance(_ lhs: CGRect, _ rhs: CGRect) -> CGFloat {
        hypot(lhs.midX - rhs.midX, lhs.midY - rhs.midY)
    }

    private func detectSelection(
        _ scene: MenuStencilCatalog.Scene,
        languageIdentifier: String?,
        pixels: ImageStencilPixels,
        image: CGImage,
        timestamp: Double,
        comparisons: inout Int,
        recordsCalibration: Bool,
        anchorBoundsByIdentifier: [String: CGRect] = [:],
        excludingOptionIdentifiers: Set<String> = []
    ) -> (
        name: String?,
        matches: [MenuStencilMatch],
        searchRegions: [MenuStencilSelectorSearchRegion],
        languageEvidenceCount: Int,
        foregroundEvidenceCount: Int
    ) {
        guard !scene.options.isEmpty else { return (nil, [], [], 0, 0) }

        func configuredSearchRegions(
            solvedIdentifier: String?,
            solvedMatches _: [MenuStencilMatch] = []
        ) -> [MenuStencilSelectorSearchRegion] {
            return selectorSearchRegions(
                scene,
                image: image,
                offset: trackedOffset,
                languageIdentifier: languageIdentifier
            ).map { region in
                return MenuStencilSelectorSearchRegion(
                    optionIdentifier: region.optionIdentifier,
                    optionName: region.optionName,
                    side: region.side,
                    rect: region.rect,
                    isSolved: region.optionIdentifier == solvedIdentifier
                )
            }
        }

        typealias ScoredRow = (
            option: MenuStencilCatalog.Option,
            confidence: Double,
            matches: [MenuStencilMatch],
            leftRect: CGRect,
            rightRect: CGRect,
            leftForegroundCount: Int,
            rightForegroundCount: Int,
            leftBrightForegroundCount: Int,
            rightBrightForegroundCount: Int,
            languageEvidenceCount: Int
        )

        func pair(
            _ option: MenuStencilCatalog.Option,
            radiusX: Int,
            radiusY: Int
        )
            -> (
                confidence: Double,
                matches: [MenuStencilMatch],
                leftRect: CGRect,
                rightRect: CGRect,
                leftForegroundCount: Int,
                rightForegroundCount: Int,
                leftBrightForegroundCount: Int,
                rightBrightForegroundCount: Int,
                languageEvidenceCount: Int
            )? {
            guard let leftSelector = option.leftSelector,
                  let rightSelector = option.rightSelector,
                  let configured = selectorBounds(
                    scene: scene,
                    option: option,
                    languageIdentifier: languageIdentifier
                  ),
                  let rowBand = selectorRowBand(
                    scene: scene,
                    option: option,
                    languageIdentifier: languageIdentifier
                  ),
                  !leftSelector.variants.isEmpty || !(scene.selectorVariants["left"] ?? []).isEmpty,
                  !rightSelector.variants.isEmpty || !(scene.selectorVariants["right"] ?? []).isEmpty
            else {
                return nil
            }
            let localizedOptionBounds = anchorBoundsByIdentifier[
                option.classIdentifier
            ] ?? scene.anchors.first(where: {
                $0.classIdentifier == option.classIdentifier
            }).flatMap { anchor in
                anchor.variants.first(where: {
                    $0.languageIdentifier == languageIdentifier
                        && $0.nominalBounds != nil
                })?.nominalBounds
            } ?? option.bounds
            func scoreSelector(
                _ variants: [MenuStencilCatalog.Variant],
                around bounds: CGRect,
                usesLiveFallback: Bool,
                side: MenuStencilSelectorSearchRegion.Side,
                idleForegroundMask: ImageStencilForegroundMask?
            ) -> ScoredVariant? {
                let searchBounds = bounds.offsetBy(
                    dx: trackedOffset.x, dy: trackedOffset.y
                )
                let textBounds = localizedOptionBounds.offsetBy(
                    dx: trackedOffset.x, dy: trackedOffset.y
                )
                let accepts: (CGRect) -> Bool = { candidate in
                    guard rowBand.contains(candidate.midY) else { return false }
                    switch side {
                    case .left: return candidate.midX < textBounds.minX
                    case .right: return candidate.midX > textBounds.maxX
                    }
                }
                // The aligned consensus is the fast path. Distinct animation
                // phases are checked only to recover a plausible weak hit, so
                // ordinary rows still cost one stencil per side.
                let primaryVariantCount = usesLiveFallback ? 1 : variants.count
                guard let primary = bestScore(
                    variants: usesLiveFallback
                        ? Array(variants.prefix(primaryVariantCount)) : variants,
                    pixels: pixels,
                    around: searchBounds,
                    deltaX: -radiusX...radiusX,
                    deltaY: -radiusY...radiusY,
                    accepts: accepts,
                    comparisons: &comparisons,
                    idleForegroundMask: idleForegroundMask
                ) else { return nil }
                guard usesLiveFallback,
                      variants.count > 1,
                      primary.confidence < Self.selectorThreshold,
                      (radiusX > Self.selectorSearchRadiusX
                        || radiusY > Self.selectorSearchRadiusY
                        || primary.foregroundPixelCount
                            >= Self.selectorMinimumForegroundPixels),
                      let fallback = bestScore(
                        // The decoration animates. Consensus is the fast path;
                        // on a plausible weak hit, check a few distinct
                        // captured phases instead of depending on one cached
                        // frame. Non-selected rows have no foreground here and
                        // never pay this recovery cost.
                        variants: Array(
                            variants.dropFirst(primaryVariantCount).prefix(4)
                        ),
                        pixels: pixels,
                        around: searchBounds,
                        deltaX: -radiusX...radiusX,
                        deltaY: -radiusY...radiusY,
                        accepts: accepts,
                        comparisons: &comparisons,
                        idleForegroundMask: idleForegroundMask
                      ),
                      fallback.confidence > primary.confidence
                else { return primary }
                return fallback
            }

            let positionedLeft = !leftSelector.variants.isEmpty
            let positionedRight = !rightSelector.variants.isEmpty
            func orderedSelectorVariants(
                _ variants: [MenuStencilCatalog.Variant],
                positioned: Bool
            ) -> [MenuStencilCatalog.Variant] {
                guard positioned else {
                    return variants
                }
                // The decoration itself is language-independent. Use the
                // aligned cross-language consensus as the fast path so one
                // contaminated localized crop cannot turn nearby text into a
                // selector. Current-language animation phases remain the
                // first recovery evidence at the localized screen position.
                return variants.filter { $0.languageIdentifier == nil }
                    + variants.filter {
                        $0.languageIdentifier == languageIdentifier
                    }
                    + variants.filter {
                        $0.languageIdentifier != nil
                            && $0.languageIdentifier != languageIdentifier
                    }
            }
            let leftVariants = orderedSelectorVariants(
                positionedLeft
                    ? leftSelector.variants
                    : (scene.selectorVariants["left"] ?? []),
                positioned: positionedLeft
            )
            let rightVariants = orderedSelectorVariants(
                positionedRight
                    ? rightSelector.variants
                    : (scene.selectorVariants["right"] ?? []),
                positioned: positionedRight
            )
            let leftIdleForegroundMask = leftSelector.idleForegroundMask(
                for: languageIdentifier
            )
            let rightIdleForegroundMask = rightSelector.idleForegroundMask(
                for: languageIdentifier
            )
            if ProcessInfo.processInfo.environment[
                "HKV_MENU_IDLE_MASK_LOG"
            ] == "1", option.classIdentifier == LabelingClassIdentity.resetDefaults {
                print(
                    "MENU_IDLE_MASK \(scene.context.storageIdentifier)"
                        + "[\(languageIdentifier ?? "shared")]"
                        + " left=\(leftIdleForegroundMask != nil)"
                        + " right=\(rightIdleForegroundMask != nil)"
                )
            }
            func scoreLocalizedSelector(
                _ variants: [MenuStencilCatalog.Variant],
                configuredBounds: CGRect,
                deployedBounds: CGRect,
                usesLiveFallback: Bool,
                side: MenuStencilSelectorSearchRegion.Side,
                idleForegroundMask: ImageStencilForegroundMask?
            ) -> ScoredVariant? {
                guard var score = scoreSelector(
                    variants,
                    around: configuredBounds,
                    usesLiveFallback: usesLiveFallback,
                    side: side,
                    idleForegroundMask: idleForegroundMask
                ) else { return nil }
                let localizedDistance = Self.centerDistance(
                    configuredBounds, deployedBounds
                )
                if (scene.context == .video
                    || option.classIdentifier == LabelingClassIdentity.resetDefaults),
                   score.confidence < 0.65,
                   localizedDistance > CGFloat(radiusX),
                   let deployed = scoreSelector(
                       variants,
                       around: deployedBounds,
                       usesLiveFallback: usesLiveFallback,
                       side: side,
                       idleForegroundMask: idleForegroundMask
                   ), deployed.confidence > score.confidence + 0.03 {
                    score = deployed
                }
                return score
            }
            guard let left = scoreLocalizedSelector(
                leftVariants,
                configuredBounds: configured.left,
                deployedBounds: leftSelector.bounds,
                usesLiveFallback: positionedLeft,
                side: .left,
                idleForegroundMask: leftIdleForegroundMask
            ), let right = scoreLocalizedSelector(
                rightVariants,
                configuredBounds: configured.right,
                deployedBounds: rightSelector.bounds,
                usesLiveFallback: positionedRight,
                side: .right,
                idleForegroundMask: rightIdleForegroundMask
            ) else { return nil }
            let matches = [
                MenuStencilMatch(
                    classIdentifier: LabelingClassIdentity.selectDecoration,
                    name: "\(option.name) left",
                    rect: outputRect(
                        left.rect,
                        image: image,
                        referenceWidth: scene.referenceWidth,
                        referenceHeight: scene.referenceHeight
                    ),
                    confidence: left.confidence,
                    reference: left.reference
                ),
                MenuStencilMatch(
                    classIdentifier: LabelingClassIdentity.selectDecoration,
                    name: "\(option.name) right",
                    rect: outputRect(
                        right.rect,
                        image: image,
                        referenceWidth: scene.referenceWidth,
                        referenceHeight: scene.referenceHeight
                    ),
                    confidence: right.confidence,
                    reference: right.reference
                ),
            ]
            return (
                (left.confidence + right.confidence) / 2,
                matches,
                left.rect,
                right.rect,
                left.foregroundPixelCount,
                right.foregroundPixelCount,
                left.brightForegroundPixelCount,
                right.brightForegroundPixelCount,
                [left, right].count {
                    $0.languageIdentifier != nil
                        && $0.languageIdentifier == languageIdentifier
                }
            )
        }

        func rankedRows(
            radiusX: Int,
            radiusY: Int
        ) -> [ScoredRow] {
            scene.options.compactMap { option -> ScoredRow? in
                guard !excludingOptionIdentifiers.contains(
                    option.classIdentifier
                ) else { return nil }
                guard let score = pair(
                    option,
                    radiusX: radiusX,
                    radiusY: radiusY
                ) else { return nil }
                return (
                    option,
                    score.confidence,
                    score.matches,
                    score.leftRect,
                    score.rightRect,
                    score.leftForegroundCount,
                    score.rightForegroundCount,
                    score.leftBrightForegroundCount,
                    score.rightBrightForegroundCount,
                    score.languageEvidenceCount
                )
            }.sorted { $0.confidence > $1.confidence }
        }

        func winningRow(in rows: [ScoredRow]) -> ScoredRow? {
            let qualified = rows.filter {
                guard $0.matches.count == 2 else { return false }
                guard abs($0.leftRect.midY - $0.rightRect.midY)
                    <= Self.selectorMaximumPairRowDelta else { return false }
                if let verifiedText = anchorBoundsByIdentifier[
                    $0.option.classIdentifier
                ], !($0.leftRect.midX < verifiedText.minX
                    && $0.rightRect.midX > verifiedText.maxX) {
                    return false
                }
                if scene.context == .selectProfile,
                   ($0.matches.map(\.confidence).max() ?? 0) >= 0.90,
                   ($0.matches.map(\.confidence).min() ?? 0) >= 0.30,
                   $0.languageEvidenceCount > 0 {
                    return true
                }
                if scene.context == .mainTitle,
                   min($0.leftForegroundCount, $0.rightForegroundCount) < 12 {
                    return false
                }
                if let configured = selectorBounds(
                    scene: scene,
                    option: $0.option,
                    languageIdentifier: languageIdentifier
                   ) {
                    let expectedSpan = configured.right.midX
                        - configured.left.midX
                    // A translated short label can legitimately narrow the
                    // pair. A hit inside the text itself is much narrower and
                    // must not beat the two flanking decorations.
                    let minimumSpan = max(24, expectedSpan * 0.30)
                    if $0.rightRect.midX - $0.leftRect.midX < minimumSpan {
                        return false
                    }
                }
                let isFaintResetPair =
                    $0.option.classIdentifier == LabelingClassIdentity.resetDefaults
                    && min($0.leftForegroundCount, $0.rightForegroundCount) >= 32
                    && ($0.matches.map(\.confidence).min() ?? 0) >= 0.18
                return isFaintResetPair || Self.qualifiedSelectorPairConfidence(
                    left: $0.matches[0].confidence,
                    right: $0.matches[1].confidence,
                    leftForegroundPixelCount: $0.leftBrightForegroundCount,
                    rightForegroundPixelCount: $0.rightBrightForegroundCount
                ) != nil
            }
            let bilateralBright = qualified.filter {
                min(
                    $0.leftBrightForegroundCount,
                    $0.rightBrightForegroundCount
                ) >= 8
            }
            let sameLanguageExact = qualified.filter {
                scene.context == .selectProfile
                    && ($0.matches.map(\.confidence).max() ?? 0) >= 0.90
                    && $0.languageEvidenceCount > 0
            }
            let bilateralForeground = qualified.filter {
                min($0.leftForegroundCount, $0.rightForegroundCount)
                    >= Self.selectorMinimumForegroundPixels
            }
            let bilateralNormal = qualified.filter {
                ($0.matches.map(\.confidence).min() ?? 0)
                    >= Self.selectorThreshold
            }
            let contenders: [ScoredRow]
            let requiredMargin: Double
            let prefersNormalPair: Bool = switch scene.context {
            case .video, .keyboard, .controller, .controllerAdvancedSettings:
                true
            default:
                false
            }
            if !sameLanguageExact.isEmpty {
                contenders = sameLanguageExact
                requiredMargin = 0.005
            } else if !bilateralBright.isEmpty {
                // Prefer the row whose two search boxes contain the visible
                // white cursor. Gray animated background can correlate with a
                // stencil, but it cannot displace a visibly occupied row.
                contenders = bilateralBright
                requiredMargin = 0.005
            } else if !bilateralForeground.isEmpty {
                // Bright menu text can give a weak selector-shaped response.
                // Require foreground on both sides before allowing the faded
                // decoration path to decide a row.
                contenders = bilateralForeground
                requiredMargin = 0.005
            } else if prefersNormalPair, !bilateralNormal.isEmpty {
                contenders = bilateralNormal
                requiredMargin = 0.005
            } else if !bilateralNormal.isEmpty {
                contenders = bilateralNormal
                requiredMargin = 0.005
            } else {
                contenders = qualified
                requiredMargin = Self.selectorWinningMargin
            }
            guard let winner = contenders.first,
                  winner.confidence
                    - (contenders.dropFirst().first?.confidence ?? 0)
                    >= requiredMargin else { return nil }
            return winner
        }

        var rows = rankedRows(
            radiusX: Self.selectorSearchRadiusX,
            radiusY: Self.selectorSearchRadiusY
        )
        var winner = winningRow(in: rows)
        let narrowWinnerHasStrongPair = winner.map {
            ($0.matches.map(\.confidence).min() ?? 0) >= Self.selectorThreshold
        } ?? false
        if winner == nil || !narrowWinnerHasStrongPair {
            rows = rankedRows(
                radiusX: Self.selectorRecoveryRadiusX(for: scene.context),
                radiusY: Self.selectorRecoveryRadiusY
            )
            winner = winningRow(in: rows)
        }
        let selectorScoreLog = ProcessInfo.processInfo.environment[
            "HKV_MENU_STENCIL_SCORE_LOG"
        ]
        if selectorScoreLog == "*"
            || selectorScoreLog == scene.context.storageIdentifier {
            print(
                "MENU_SELECTOR_SCORES \(scene.context.storageIdentifier)"
                    + "[\(languageIdentifier ?? "shared")] "
                    + rows.map {
                        let sideScores = $0.matches.map {
                            String(format: "%.3f", $0.confidence)
                        }.joined(separator: ",")
                        let positions = [
                            "\(Int($0.leftRect.midX)),\(Int($0.leftRect.midY))",
                            "\(Int($0.rightRect.midX)),\(Int($0.rightRect.midY))",
                        ].joined(separator: "/")
                        return "\($0.option.name)=\(String(format: "%.3f", $0.confidence))[\(sideScores)]@\(positions)#\($0.leftForegroundCount),\($0.rightForegroundCount)!\($0.leftBrightForegroundCount),\($0.rightBrightForegroundCount)~\($0.languageEvidenceCount)"
                    }.joined(separator: " ")
            )
        }
        guard let winner else {
            // A single weak animation phase must not erase the preceding
            // stable samples. The timestamp gap and option key still discard
            // stale evidence before any position can be committed.
            selectorCalibrationDiagnostic = "waiting-match"
            return (
                nil,
                [],
                configuredSearchRegions(solvedIdentifier: nil),
                0,
                0
            )
        }
        if ProcessInfo.processInfo.environment["HKV_MENU_SELECTION_LOG"] == "1" {
            print(
                "MENU_SELECTION \(scene.context.storageIdentifier)/"
                    + "\(winner.option.name) confidence="
                    + String(format: "%.3f", winner.confidence)
                    + " foreground=\(winner.leftForegroundCount),"
                    + "\(winner.rightForegroundCount)"
            )
        }
        if recordsCalibration {
            recordSelectorCalibration(
                scene: scene,
                option: winner.option,
                left: winner.leftRect,
                right: winner.rightRect,
                timestamp: timestamp
            )
        }
        return (
            winner.option.name,
            winner.matches,
            configuredSearchRegions(
                solvedIdentifier: winner.option.classIdentifier,
                solvedMatches: winner.matches
            ),
            winner.languageEvidenceCount,
            min(winner.leftForegroundCount, winner.rightForegroundCount)
        )
    }

    private func selectorSearchRegions(
        _ scene: MenuStencilCatalog.Scene,
        image: CGImage,
        offset: CGPoint,
        languageIdentifier: String? = nil
    ) -> [MenuStencilSelectorSearchRegion] {
        scene.options.flatMap { option -> [MenuStencilSelectorSearchRegion] in
            guard let configured = selectorBounds(
                scene: scene,
                option: option,
                languageIdentifier: languageIdentifier
            ) else {
                return []
            }
            return [
                (
                    MenuStencilSelectorSearchRegion.Side.left,
                    configured.left,
                    option.leftSelector!.bounds
                ),
                (.right, configured.right, option.rightSelector!.bounds),
            ].map { side, bounds, baseBounds in
                // Corrections provide a more accurate center. Their hand-drawn
                // rectangle can be deliberately loose, but the decoration's
                // physical size is language-independent. Keep the overlay at
                // the deployed selector footprint so it remains a useful
                // position guide instead of covering the entire menu row.
                let visualBounds = CGRect(
                    x: bounds.midX - baseBounds.width / 2,
                    y: bounds.midY - baseBounds.height / 2,
                    width: baseBounds.width,
                    height: baseBounds.height
                )
                return MenuStencilSelectorSearchRegion(
                    optionIdentifier: option.classIdentifier,
                    optionName: option.name,
                    side: side,
                    rect: outputRect(
                        visualBounds.offsetBy(
                            dx: offset.x, dy: offset.y
                        ).insetBy(
                            dx: -CGFloat(Self.selectorSearchRadiusX),
                            dy: -CGFloat(Self.selectorSearchRadiusY)
                        ),
                        image: image,
                        referenceWidth: scene.referenceWidth,
                        referenceHeight: scene.referenceHeight
                    ),
                    isSolved: false
                )
            }
        }
    }

    private func bestScore(
        variants: [MenuStencilCatalog.Variant],
        pixels: ImageStencilPixels,
        around rect: CGRect,
        referenceBounds: CGRect? = nil,
        deltaX: ClosedRange<Int>,
        deltaY: ClosedRange<Int>,
        accepts: ((CGRect) -> Bool)? = nil,
        comparisons: inout Int,
        idleForegroundMask: ImageStencilForegroundMask? = nil
    ) -> ScoredVariant? {
        var best: (
            confidence: Double,
            rect: CGRect,
            reference: ImageStencilReference,
            languageIdentifier: String?,
            nominalBounds: CGRect?
        )?
        for variant in variants {
            let variantRect: CGRect
            if let referenceBounds, let nominalBounds = variant.nominalBounds {
                variantRect = nominalBounds.offsetBy(
                    dx: rect.minX - referenceBounds.minX,
                    dy: rect.minY - referenceBounds.minY
                )
            } else {
                variantRect = rect
            }
            for y in deltaY {
                for x in deltaX {
                    let candidate = variantRect.offsetBy(
                        dx: CGFloat(x), dy: CGFloat(y)
                    )
                    if accepts?(candidate) == false { continue }
                    comparisons += 1
                    guard let comparison = pixels.compare(variant.kernel, at: candidate) else {
                        continue
                    }
                    let confidence = comparison.confidence(
                        correlationWeight: 0.82,
                        colorErrorScale: 105
                    )
                    if confidence > (best?.confidence ?? -.infinity) {
                        best = (
                            confidence,
                            candidate,
                            variant.reference,
                            variant.languageIdentifier,
                            variant.nominalBounds
                        )
                    }
                }
            }
        }
        guard let best else { return nil }
        return ScoredVariant(
            confidence: best.confidence,
            foregroundPixelCount: pixels.likelyMenuTextPixelCount(
                in: best.rect,
                excludingStableForeground: idleForegroundMask
            ),
            brightForegroundPixelCount: pixels.brightMenuPixelCount(
                in: best.rect,
                excludingStableForeground: idleForegroundMask
            ),
            rect: best.rect,
            reference: best.reference,
            languageIdentifier: best.languageIdentifier,
            nominalBounds: best.nominalBounds
        )
    }

    private func variants(
        _ variants: [MenuStencilCatalog.Variant],
        matching languageIdentifier: String?
    ) -> [MenuStencilCatalog.Variant] {
        guard let languageIdentifier else { return variants }
        let matched = variants.filter {
            $0.languageIdentifier == languageIdentifier
        } + variants.filter {
            $0.languageIdentifier == nil
        }
        if !matched.isEmpty { return matched }
        // A scene-language hypothesis must not cherry-pick missing anchors
        // from several unrelated translations. Human stencils are English,
        // so they are the only coherent fallback when a translated anchor
        // consensus is unavailable.
        let englishFallback = variants.filter {
            $0.languageIdentifier == HollowKnightMenuLanguage.english.rawValue
                || $0.languageIdentifier == nil
        }
        return englishFallback.isEmpty
            ? Array(variants.prefix(1)) : englishFallback
    }

    private func failedSearchResult(
        _ candidate: ProbeCandidate,
        image: CGImage,
        timestamp: Double,
        comparisons: Int
    ) -> MenuStencilResult {
        let offset = CGPoint(
            x: candidate.score.rect.minX
                - (candidate.score.nominalBounds
                    ?? candidate.scene.probe.bounds).minX,
            y: candidate.score.rect.minY
                - (candidate.score.nominalBounds
                    ?? candidate.scene.probe.bounds).minY
        )
        return MenuStencilResult(
            context: candidate.scene.context,
            isMatch: false,
            confidence: candidate.score.confidence,
            selectedOption: nil,
            anchors: [MenuStencilMatch(
                classIdentifier: candidate.scene.probe.classIdentifier,
                name: candidate.scene.probe.name,
                rect: outputRect(
                    candidate.score.rect,
                    image: image,
                    referenceWidth: candidate.scene.referenceWidth,
                    referenceHeight: candidate.scene.referenceHeight
                ),
                confidence: candidate.score.confidence,
                reference: candidate.score.reference
            )],
            selectorCandidates: [],
            selectorSearchRegions: selectorSearchRegions(
                candidate.scene,
                image: image,
                offset: offset,
                languageIdentifier: candidate.score.languageIdentifier
            ),
            selectorLanguageEvidenceCount: 0,
            selectorForegroundEvidenceCount: 0,
            sceneEvidenceRatio: 0,
            phase: .searching,
            comparisonCount: comparisons,
            sourceTimestamp: timestamp,
            languageIdentifier: candidate.score.languageIdentifier
        )
    }

    private func outputRect(
        _ topOriginRect: CGRect,
        image: CGImage,
        referenceWidth: Int,
        referenceHeight: Int
    ) -> CGRect {
        let scaleX = CGFloat(image.width) / CGFloat(referenceWidth)
        let scaleY = CGFloat(image.height) / CGFloat(referenceHeight)
        return CGRect(
            x: topOriginRect.minX * scaleX,
            y: CGFloat(image.height) - topOriginRect.maxY * scaleY,
            width: topOriginRect.width * scaleX,
            height: topOriginRect.height * scaleY
        ).intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
    }
}

enum MenuStencilRenderer {
    static func shouldRenderSelectedPairAsAccepted(
        _ result: MenuStencilResult
    ) -> Bool {
        result.selectedOption != nil && result.selectorCandidates.count == 2
    }

    static func overlay(
        _ result: MenuStencilResult,
        width: Int,
        height: Int
    ) -> CGImage? {
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        for region in result.selectorSearchRegions {
            context.setFillColor(
                CGColor(red: 0.1, green: 0.75, blue: 1, alpha: 0.02)
            )
            context.fill(region.rect)
            context.setStrokeColor(
                CGColor(red: 0.1, green: 0.75, blue: 1, alpha: 0.82)
            )
            context.setLineWidth(1)
            context.setLineDash(phase: 0, lengths: [4, 3])
            context.stroke(region.rect.insetBy(dx: 0.5, dy: 0.5))
        }
        context.setLineDash(phase: 0, lengths: [])
        for match in result.anchors {
            if let reference = match.reference?.makeImage() {
                context.saveGState()
                context.setAlpha(0.5)
                context.draw(reference, in: match.rect)
                context.restoreGState()
            }
            let accepted = match.confidence >= MenuStencilTracker.anchorThreshold
            context.setStrokeColor(accepted
                ? CGColor(red: 0.1, green: 1, blue: 0.35, alpha: 0.95)
                : CGColor(red: 1, green: 0.15, blue: 0.15, alpha: 0.8))
            context.setLineWidth(1)
            let verified = match.reference?.verifiedBounds(in: match.rect)
                ?? match.rect
            context.stroke(verified.insetBy(dx: 0.5, dy: 0.5))
        }
        for match in result.selectorCandidates {
            // Selection acceptance is decided from the two-sided pair. A
            // faded side may be below the normal single-side threshold while
            // the pair still passes the foreground-aware recovery rule.
            let accepted = shouldRenderSelectedPairAsAccepted(result)
            context.setStrokeColor(accepted
                ? CGColor(red: 1, green: 0.8, blue: 0.05, alpha: 1)
                : CGColor(red: 1, green: 0.2, blue: 0.2, alpha: 0.45))
            context.setLineWidth(accepted ? 2 : 1)
            context.stroke(match.rect.insetBy(dx: 0.5, dy: 0.5))
        }
        return context.makeImage()
    }
}
