import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class MenuStencilTests: XCTestCase {
    private var examplesRoot: URL? {
        ProcessInfo.processInfo.environment["HKV_MENU_STENCIL_AUDIT"].map {
            URL(fileURLWithPath: $0, isDirectory: true)
        }
    }

    private func bundledCalibration() throws -> MenuStencilCalibration {
        try XCTUnwrap(MenuStencilCalibration.loadBundled())
    }

    func testBundledFullScreenCalibrationCoversEveryMenuOption() throws {
        let calibration = try bundledCalibration()
        XCTAssertEqual(calibration.scenes.count, 23)
        XCTAssertEqual(calibration.referenceWidth, 640)
        XCTAssertEqual(calibration.referenceHeight, 360)
        let states = calibration.scenes.flatMap(\.states)
        let selectors = calibration.scenes.flatMap(\.selectors)
        XCTAssertEqual(states.count, selectors.count)
        XCTAssertEqual(states.count, 104)
        XCTAssertEqual(states.count { $0.measured }, 104)
        XCTAssertEqual(states.count { !$0.measured }, 0)
        XCTAssertEqual(selectors.count { $0.measured }, 104)
        XCTAssertEqual(selectors.count { !$0.measured }, 0)
        XCTAssertFalse(selectors.contains {
            $0.selectedIdentifier == "brightness.pointer"
                || $0.selectedIdentifier == "controller-advanced.mfi"
        })
        XCTAssertTrue(selectors.allSatisfy {
            $0.leftRect.cgRect.width > 0 && $0.rightRect.cgRect.width > 0
        })
    }

    func testSelectProfileIncludesFourClearSaveSelectorRows() throws {
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: []
        )
        let scene = try XCTUnwrap(catalog.scenes.first {
            $0.context == .selectProfile
        })
        let clearSaveOptions = scene.options.filter {
            $0.classIdentifier.hasPrefix("select-profile.clear-save-")
        }

        XCTAssertEqual(clearSaveOptions.map(\.classIdentifier), [
            "select-profile.clear-save-1",
            "select-profile.clear-save-2",
            "select-profile.clear-save-3",
            "select-profile.clear-save-4",
        ])
        XCTAssertTrue(clearSaveOptions.allSatisfy {
            $0.leftSelector != nil && $0.rightSelector != nil
        })
    }

    func testReviewedContractBreachSelectorsCorrectActualLocalizedRows() throws {
        let expected: [(String, LabelingContext, String, String)] = [
            ("5EF5867D-16EC-4202-B9A7-0B273A4CCE12", .options, "options.game", "fr"),
            ("A343B40F-10B9-4992-808E-A4DF90DD60DE", .gameOptions, LabelingClassIdentity.back, "fr"),
            ("7BDBE122-1CF4-48A0-AA47-0DF9DA22E8BD", .gameOptions, LabelingClassIdentity.resetDefaults, "fr"),
            ("9AF0F76E-969F-4A39-BE1F-85DCBFC43FDA", .extras, "extras.lifeblood", "es"),
            ("BA6281F5-61BD-4FDE-A88F-84AF957A0668", .extras, "extras.the-grimm-troupe", "es"),
            ("62D3E3C4-A574-4946-B26D-A646EE6666AE", .extras, "extras.hidden-dreams", "es"),
            ("F44121EB-E362-4ADB-A314-48ECAAE61E28", .keyboard, LabelingClassIdentity.attack, "es"),
            ("3ABEEF76-A4E8-4BDA-9C0C-5B7F6B503AE2", .options, LabelingClassIdentity.controller, "es"),
            ("8F82F042-B421-4FC4-9BC4-A7CD23D30A52", .video, "video.full-screen", "es"),
            ("F1EE8587-0830-4A26-A3DA-C526267A98CE", .gameOptions, LabelingClassIdentity.resetDefaults, "es"),
            ("FCFC1ADD-C60D-4B30-AC31-E9E42E5C020F", .quitGame, LabelingClassIdentity.no, "pt-BR"),
            ("6E4C3D9F-73CA-49E4-B2F0-C30D3A48DBEE", .keyboard, LabelingClassIdentity.superDash, "pt-BR"),
            ("997CB3D1-0546-4B2A-9FD4-396DF6CDFC6D", .keyboard, LabelingClassIdentity.attack, "pt-BR"),
            ("7B18DA08-52B2-40DD-904D-E68412C5DE10", .keyboard, "inventory.inventory", "pt-BR"),
            ("804959CD-CEDE-47EC-B5EC-99B5C1DB24BB", .video, LabelingClassIdentity.advancedSettings, "pt-BR"),
            ("08B7D411-2B22-4BA1-83DD-D35519E8CC21", .video, "video.full-screen", "pt-BR"),
            ("23177404-3D79-4AD0-A2CE-67F6C8CA69D1", .options, "options.game", "pt-BR"),
            ("35F5BDEE-F9C8-4B35-95B2-40AAE1E4EB24", .extras, "extras.lifeblood", "ko"),
            ("577B177F-29D8-4D26-AA5C-EAD1A325841C", .extras, "extras.hidden-dreams", "ko"),
            ("69FA0F82-93F4-49C8-8BE1-2CDDD89748A9", .extras, "extras.the-grimm-troupe", "ko"),
            ("E8FC52A8-3E99-407C-9A83-1D88E7143BF5", .options, "options.game", "es"),
            ("04D40700-0C53-4E5D-B2D8-0DA5C134774D", .options, "options.game", "fr"),
            ("0D565524-8EC3-442C-B8F3-3152A0A0382A", .options, "options.game", "it"),
            ("01A82890-192D-4CD7-BB55-E0D4B27C3CB9", .options, LabelingClassIdentity.keyboard, "it"),
            ("FC571AB9-9AB2-4EE9-BEC0-7257EFF93C1B", .video, LabelingClassIdentity.advancedSettings, "it"),
            ("973AB0CA-EEF2-4902-8B53-E0765479567E", .options, "options.game", "it"),
            ("911E9057-3FE0-452C-BE9C-2D68AD922154", .keyboard, LabelingClassIdentity.attack, "it"),
            ("AE1F5C8D-6D41-427C-B6BE-DDCC911964BD", .keyboard, LabelingClassIdentity.superDash, "it"),
            ("D57CC45F-DE6A-4BA6-A25F-FDB67B3ADEEA", .keyboard, "inventory.inventory", "it"),
            ("FA0753AB-7E1F-4024-A1EF-F5E651F51B93", .options, "options.game", "ja"),
            ("5393B426-F329-4E86-80D2-5E104C36A746", .extras, "extras.hidden-dreams", "ja"),
            ("7B00F837-FC48-46FC-9AF2-9C19C471ED3E", .extras, "extras.the-grimm-troupe", "ja"),
            ("07A600CE-FC9A-42FD-AA72-6E54D0412971", .extras, "extras.lifeblood", "ja"),
            ("D3BF03A8-98EC-4CEA-9696-B647C674221E", .quitGame, LabelingClassIdentity.no, "ja"),
            ("00B9FE92-B22D-400A-947C-03848590DBBF", .video, LabelingClassIdentity.advancedSettings, "ja"),
            ("AA6813FA-8B96-4B2C-B499-0A3F46F745C6", .keyboard, LabelingClassIdentity.attack, "ko"),
            ("3224CB73-47A1-4F4B-B4AD-21EE272B4B81", .keyboard, LabelingClassIdentity.attack, "ko"),
            ("16A25BAA-201A-40AD-ABC2-28F97683D505", .extras, "extras.lifeblood", "ko"),
            ("144A8CFB-9C77-4D92-A621-D7780B96335F", .extras, "extras.the-grimm-troupe", "ko"),
            ("66CCC048-781A-4D48-8D80-6D58A1CA9530", .video, LabelingClassIdentity.advancedSettings, "ko"),
            ("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F", .keyboard, "keyboard.right", "ko"),
            ("BB7C33FD-0000-41D3-A25F-525B663772D2", .keyboard, LabelingClassIdentity.superDash, "ko"),
        ]
        let store = LabelingExampleStore()
        let examples = try store.loadExamples()
        let available = expected.filter { item in
            examples.contains { $0.id == UUID(uuidString: item.0) }
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try bundledCalibration(),
            liveCapturesOverride: []
        )
        for item in available {
            let example = try XCTUnwrap(examples.first {
                $0.id == UUID(uuidString: item.0)
            })
            let annotations = example.manifest.annotations.filter {
                !$0.isHardNegative && LabelingClassIdentity.matches(
                    $0.classIdentifier,
                    LabelingClassIdentity.selectDecoration
                )
            }.map {
                CGRect(
                    x: $0.x * Double(example.manifest.imageWidth),
                    y: $0.y * Double(example.manifest.imageHeight),
                    width: $0.width * Double(example.manifest.imageWidth),
                    height: $0.height * Double(example.manifest.imageHeight)
                )
            }.sorted { $0.midX < $1.midX }
            let scene = try XCTUnwrap(catalog.scenes.first { $0.context == item.1 })
            let option = try XCTUnwrap(scene.options.first {
                $0.classIdentifier == item.2
            })
            let deployed = [
                option.leftSelector?.localizedBounds[item.3],
                option.rightSelector?.localizedBounds[item.3],
            ].compactMap { $0 }
            for annotation in annotations {
                XCTAssertTrue(deployed.contains { candidate in
                    abs(candidate.midX - annotation.midX) < 0.001
                        && abs(candidate.midY - annotation.midY) < 0.001
                }, "\(item.0) \(item.1)/\(item.2)")
            }
        }
    }

    func testClearSaveProbeOffsetsFollowAllFourProfileRows() {
        XCTAssertEqual(
            MenuStencilTracker.clearSaveVerticalProbeOffsets(
                referenceY: 103,
                profileRowCenters: [103, 156, 208, 261]
            ),
            [0, 53, 105, 158]
        )
        XCTAssertEqual(
            MenuStencilTracker.clearSaveVerticalProbeOffsets(
                referenceY: 207,
                profileRowCenters: [103, 156, 208, 261]
            ),
            [-105, -52, 0, 53]
        )
    }

    func testFirstLearnedClearSaveSelectorCreatesCalibrationScene() throws {
        let updated = try XCTUnwrap(try bundledCalibration().replacingSelector(
            contextIdentifier: LabelingContext.clearSave.storageIdentifier,
            selectedIdentifier: LabelingClassIdentity.yes,
            leftRect: CGRect(x: 280, y: 100, width: 12, height: 11),
            rightRect: CGRect(x: 336, y: 100, width: 12, height: 11)
        ))
        let scene = try XCTUnwrap(updated.scene(.clearSave))
        XCTAssertEqual(scene.selectors.count, 1)
        XCTAssertEqual(scene.selectors.first?.selectedIdentifier, LabelingClassIdentity.yes)
        XCTAssertEqual(scene.selectors.first?.evidenceCount, 1)
    }

    func testLearnedSelectorPositionsRemainIndependentByLanguage() throws {
        let original = try bundledCalibration()
        let context = LabelingContext.options
        let identifier = "options.game"
        let originalSelector = try XCTUnwrap(original.scene(context)?.selectors.first {
            $0.selectedIdentifier == identifier
        })
        let russianLeft = CGRect(x: 240, y: 100, width: 13, height: 11)
        let russianRight = CGRect(x: 387, y: 100, width: 13, height: 11)
        let japaneseLeft = CGRect(x: 274, y: 100, width: 13, height: 11)
        let japaneseRight = CGRect(x: 353, y: 100, width: 13, height: 11)

        let russian = try XCTUnwrap(original.replacingSelector(
            contextIdentifier: context.storageIdentifier,
            selectedIdentifier: identifier,
            languageIdentifier: HollowKnightMenuLanguage.russian.rawValue,
            leftRect: russianLeft,
            rightRect: russianRight
        ))
        let updated = try XCTUnwrap(russian.replacingSelector(
            contextIdentifier: context.storageIdentifier,
            selectedIdentifier: identifier,
            languageIdentifier: HollowKnightMenuLanguage.japanese.rawValue,
            leftRect: japaneseLeft,
            rightRect: japaneseRight
        ))
        let selector = try XCTUnwrap(updated.scene(context)?.selectors.first {
            $0.selectedIdentifier == identifier
        })

        XCTAssertEqual(selector.leftRect, originalSelector.leftRect)
        XCTAssertEqual(selector.rightRect, originalSelector.rightRect)
        XCTAssertEqual(
            selector.placement(for: HollowKnightMenuLanguage.russian.rawValue)?.leftRect,
            .init(russianLeft)
        )
        XCTAssertEqual(
            selector.placement(for: HollowKnightMenuLanguage.japanese.rawValue)?.rightRect,
            .init(japaneseRight)
        )
        XCTAssertEqual(
            selector.placement(for: HollowKnightMenuLanguage.russian.rawValue)?.evidenceCount,
            1
        )
    }

    func testProjectCalibrationMirrorRequiresExplicitLaunchArgument() {
        XCTAssertNil(MenuStencilCalibration.projectMirrorURL(arguments: []))
        XCTAssertEqual(
            MenuStencilCalibration.projectMirrorURL(arguments: [
                "HollowKnightVision",
                "--menu-stencil-project-calibration=/tmp/project-menu.json",
            ]),
            URL(fileURLWithPath: "/tmp/project-menu.json")
        )
    }

    func testHumanClearSaveCaptureBuildsBothSelectorRows() throws {
        let store = LabelingExampleStore()
        guard let source = try store.loadExamples().first(where: {
            $0.manifest.contextIdentifier == LabelingContext.clearSave.storageIdentifier
        }) else {
            throw XCTSkip("No human Clear Save capture")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: nil,
            liveCapturesOverride: []
        )
        let scene = try XCTUnwrap(catalog.scenes.first {
            $0.context == .clearSave
        })
        XCTAssertEqual(scene.options.map(\.name), ["No", "Yes"])
        XCTAssertTrue(scene.options.allSatisfy {
            $0.leftSelector != nil && $0.rightSelector != nil
        })

        let image = try XCTUnwrap(try store.loadImage(for: source))
        let result = try XCTUnwrap(MenuStencilTracker(catalog: catalog).observe(
            image,
            timestamp: 0
        ))
        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.context, .clearSave)
        XCTAssertEqual(result.selectedOption, "Yes")
        XCTAssertEqual(result.selectorSearchRegions.count, 4)
    }

    func testHumanInventoryCaptureBuildsStaticSceneWithoutSelectors() throws {
        let store = LabelingExampleStore()
        guard let source = try store.loadExamples().first(where: {
            $0.manifest.contextIdentifier == LabelingContext.inventory.storageIdentifier
        }) else {
            throw XCTSkip("No human Inventory capture")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: nil,
            liveCapturesOverride: []
        )
        let scene = try XCTUnwrap(catalog.scenes.first {
            $0.context == .inventory
        })
        XCTAssertEqual(scene.probe.classIdentifier, "inventory.inventory")
        XCTAssertTrue(scene.options.isEmpty)

        let image = try XCTUnwrap(try store.loadImage(for: source))
        let result = try XCTUnwrap(MenuStencilTracker(catalog: catalog).observe(
            image, timestamp: 0
        ))
        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.context, .inventory)
        XCTAssertNil(result.selectedOption)
        XCTAssertTrue(result.selectorSearchRegions.isEmpty)
    }

    func testSearchCadenceLimitsUntrackedWorkAndResetsImmediately() {
        var cadence = MenuStencilSearchCadence(minimumInterval: 0.1)
        XCTAssertTrue(cadence.shouldSearch(at: 1))
        XCTAssertFalse(cadence.shouldSearch(at: 1.05))
        XCTAssertTrue(cadence.shouldSearch(at: 1.101))
        XCTAssertFalse(cadence.shouldSearch(at: 1.15))

        cadence.reset()
        XCTAssertTrue(cadence.shouldSearch(at: 1.151))
        XCTAssertTrue(cadence.shouldSearch(at: 0.5))
    }

    func testDeployedTrackerRequiresExplicitWritableCalibration() {
        XCTAssertNil(MenuStencilTracker.resolvedSelectorCalibrationURL(
            usesDeployedCatalog: true,
            explicitURL: nil
        ))
        XCTAssertNil(MenuStencilTracker.resolvedSelectorCalibrationURL(
            usesDeployedCatalog: false,
            explicitURL: nil
        ))
        let explicitURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("explicit-menu-calibration.json")
        XCTAssertEqual(
            MenuStencilTracker.resolvedSelectorCalibrationURL(
                usesDeployedCatalog: true,
                explicitURL: explicitURL
            ),
            explicitURL
        )
    }

    func testSelectorPairRequiresBothSidesAndRanksCombinedEvidence() throws {
        let extras = try XCTUnwrap(
            MenuStencilTracker.qualifiedSelectorPairConfidence(
                left: 0.787,
                right: 0.605
            )
        )
        let falseAchievements = try XCTUnwrap(
            MenuStencilTracker.qualifiedSelectorPairConfidence(
                left: 0.587,
                right: 0.656
            )
        )

        XCTAssertEqual(extras, 0.696, accuracy: 0.0001)
        XCTAssertEqual(falseAchievements, 0.6215, accuracy: 0.0001)
        XCTAssertGreaterThan(
            extras - falseAchievements,
            MenuStencilTracker.selectorWinningMargin
        )
        XCTAssertNil(MenuStencilTracker.qualifiedSelectorPairConfidence(
            left: 0.900,
            right: MenuStencilTracker.selectorThreshold - 0.001
        ))
        XCTAssertNotNil(MenuStencilTracker.qualifiedSelectorPairConfidence(
            left: 0.459,
            right: 0.599,
            leftForegroundPixelCount: 59,
            rightForegroundPixelCount: 59
        ))
        XCTAssertNotNil(MenuStencilTracker.qualifiedSelectorPairConfidence(
            left: 0.390,
            right: 0.430,
            leftForegroundPixelCount: 56,
            rightForegroundPixelCount: 56
        ))
        XCTAssertNotNil(MenuStencilTracker.qualifiedSelectorPairConfidence(
            left: 0.299,
            right: 0.243,
            leftForegroundPixelCount: 39,
            rightForegroundPixelCount: 45
        ))
        XCTAssertNil(MenuStencilTracker.qualifiedSelectorPairConfidence(
            left: 0.299,
            right: 0.243,
            leftForegroundPixelCount: 35,
            rightForegroundPixelCount: 45
        ))
        XCTAssertNil(MenuStencilTracker.qualifiedSelectorPairConfidence(
            left: 0.459,
            right: 0.599,
            leftForegroundPixelCount: 0,
            rightForegroundPixelCount: 59
        ))
    }

    func testAcceptedFadedSelectorPairRendersAsAccepted() {
        let candidate = MenuStencilMatch(
            classIdentifier: LabelingClassIdentity.selectDecoration,
            name: "Back left",
            rect: CGRect(x: 10, y: 10, width: 12, height: 10),
            confidence: MenuStencilTracker.fadedSelectorEvidenceThreshold,
            reference: nil
        )
        let result = MenuStencilResult(
            context: .selectProfile,
            isMatch: true,
            confidence: 1,
            selectedOption: "Back",
            anchors: [],
            selectorCandidates: [candidate, candidate],
            selectorSearchRegions: [],
            selectorLanguageEvidenceCount: 0,
            selectorForegroundEvidenceCount: 0,
            sceneEvidenceRatio: 1,
            phase: .tracking,
            comparisonCount: 0,
            sourceTimestamp: 1,
            languageIdentifier: nil
        )

        XCTAssertTrue(MenuStencilRenderer.shouldRenderSelectedPairAsAccepted(result))
    }

    func testFocusedDraftBoxesBecomeObjectStencilVariantsWhenAvailable() throws {
        let expected: [(UUID, LabelingContext, String)] = [
            (UUID(uuidString: "F43481C4-3F4E-4E02-B52A-15B5BE20803F")!,
             .gameOptions, "game-options.show-achievements"),
            (UUID(uuidString: "F43481C4-3F4E-4E02-B52A-15B5BE20803F")!,
             .gameOptions, "game-options.backer-credits"),
            (UUID(uuidString: "90E5D3BE-F45F-488A-B376-B63664A7D75E")!,
             .audio, "audio.sound-volume"),
            (UUID(uuidString: "E7B8D5DC-BA90-472F-875B-66A728435EF8")!,
             .video, LabelingClassIdentity.advancedSettings),
            (UUID(uuidString: "AA8CCF3B-0069-4DBD-8638-E051DF298529")!,
             .controller, LabelingClassIdentity.advancedSettings),
        ]
        let store = LabelingExampleStore(rootURL: LabelingExampleStore.defaultRootURL())
        let available = Set(try store.loadExamples().map(\.id))
        guard expected.allSatisfy({ available.contains($0.0) }) else {
            throw XCTSkip("Focused menu regression drafts are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: []
        )

        for (exampleID, context, classIdentifier) in expected {
            let scene = try XCTUnwrap(catalog.scenes.first { $0.context == context })
            let anchor = try XCTUnwrap(scene.anchors.first {
                $0.classIdentifier == classIdentifier
            })
            XCTAssertTrue(anchor.variants.contains {
                $0.kernel.kind.hasSuffix(
                    ".human-box.\(exampleID.uuidString.lowercased())"
                )
            }, "\(context.storageIdentifier)/\(classIdentifier)")
        }
    }

    func testBundledGeometryContainsLatestReportedSelectorCorrections() throws {
        let calibration = try bundledCalibration()
        func selector(
            _ context: LabelingContext,
            _ identifier: String
        ) throws -> MenuStencilCalibration.Selector {
            try XCTUnwrap(calibration.scene(context)?.selectors.first {
                $0.selectedIdentifier == identifier
            })
        }

        let profileBack = try selector(.selectProfile, LabelingClassIdentity.back)
        XCTAssertEqual(profileBack.leftRect.cgRect, CGRect(x: 282, y: 294, width: 16, height: 14))
        XCTAssertEqual(profileBack.rightRect.cgRect, CGRect(x: 345, y: 293, width: 15, height: 14))

        let masterVolume = try selector(.audio, "audio.master-volume")
        XCTAssertEqual(masterVolume.rightRect.cgRect, CGRect(x: 459, y: 101, width: 12, height: 10))

        let controllerAdvanced = try selector(
            .controller, LabelingClassIdentity.advancedSettings
        )
        XCTAssertEqual(controllerAdvanced.leftRect.cgRect, CGRect(x: 240, y: 297, width: 11, height: 8))
        XCTAssertEqual(controllerAdvanced.rightRect.cgRect, CGRect(x: 387, y: 297, width: 11, height: 8))

        let resetDefaults = try selector(
            .controllerAdvancedSettings, LabelingClassIdentity.resetDefaults
        )
        XCTAssertEqual(resetDefaults.leftRect.cgRect, CGRect(x: 252, y: 282, width: 11, height: 10))
        XCTAssertEqual(resetDefaults.rightRect.cgRect, CGRect(x: 373, y: 282, width: 12, height: 10))
    }

    func testCurrentReportedMenuStatesRecognizeWithoutLiveSelfReference() throws {
        let expected: [UUID: (LabelingContext, String)] = [
            UUID(uuidString: "0C12A259-3910-4CA4-BF43-B1EC73738FF8")!:
                (.selectProfile, "Back"),
            UUID(uuidString: "A5D1697C-189B-4F73-BFED-70ED4392C1EA")!:
                (.gameOptions, "Show Achievements"),
            UUID(uuidString: "BB033DA5-3C96-4ABF-A0F2-73ADC29465EF")!:
                (.gameOptions, "Backer Credits"),
            UUID(uuidString: "CFD659AC-19A6-48E2-9929-9F87CE29D059")!:
                (.audio, "Sound Volume"),
            UUID(uuidString: "C46E9CB8-7434-4AEB-9D47-C95BED51C91A")!:
                (.audio, "Master Volume"),
            UUID(uuidString: "F676CB9B-42A6-4266-9019-602DD3552EEE")!:
                (.controllerAdvancedSettings, "Reset Defaults"),
            UUID(uuidString: "47AC5304-6490-4B92-B121-F4EDF05655C8")!:
                (.controller, "Advanced Settings"),
        ]
        let captures = try MenuStencilLiveCaptureStore().load()
        let audited = captures.filter { expected[$0.capture.id] != nil }
        guard audited.count == expected.count else {
            throw XCTSkip("Current reported menu captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: []
        )

        for source in audited {
            let wanted = try XCTUnwrap(expected[source.capture.id])
            let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
            let result = try XCTUnwrap(
                MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
            )
            XCTAssertTrue(result.isMatch, source.capture.id.uuidString)
            XCTAssertEqual(result.context, wanted.0, source.capture.id.uuidString)
            XCTAssertEqual(
                result.selectedOption,
                wanted.1,
                source.capture.id.uuidString
            )
            let scene = try XCTUnwrap(catalog.scenes.first {
                $0.context == wanted.0
            })
            XCTAssertEqual(
                result.selectorSearchRegions.count,
                scene.options.count * 2,
                source.capture.id.uuidString
            )
        }
    }

    func testEveryCapturedLanguageRecognizesGameOptionsAndLanguageSelection() throws {
        let captures = try MenuStencilLiveCaptureStore().load()
        var latestByLanguage = [String: (
            capture: MenuStencilLiveCapture, imageURL: URL
        )]()
        for source in captures where
            source.capture.contextIdentifier == LabelingContext.gameOptions.storageIdentifier
                && source.capture.selectedIdentifier == "game-options.language"
                && latestByLanguage[source.capture.resolvedLanguageIdentifier] == nil {
            latestByLanguage[source.capture.resolvedLanguageIdentifier] = source
        }
        let expectedLanguages = Set(HollowKnightMenuLanguage.allCases.map(\.rawValue))
        guard Set(latestByLanguage.keys) == expectedLanguages else {
            throw XCTSkip("Complete live language-cycle captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures,
            includedContexts: [.gameOptions]
        )
        let scene = try XCTUnwrap(catalog.scenes.first { $0.context == .gameOptions })
        XCTAssertEqual(
            Set(scene.probe.searchProbeVariants.compactMap(\.languageIdentifier)),
            expectedLanguages
        )

        for language in expectedLanguages.sorted() {
            let source = try XCTUnwrap(latestByLanguage[language])
            let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
            let result = try XCTUnwrap(
                MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
            )
            XCTAssertTrue(result.isMatch, language)
            XCTAssertEqual(result.context, .gameOptions, language)
            XCTAssertEqual(result.selectedOption, "Language", language)
        }
    }

    func testRequestedLanguagesRecognizeEveryCapturedFrontEndScene() throws {
        let allExpectedContexts: Set<LabelingContext> = [
            .mainTitle, .options, .gameOptions, .audio, .video, .screenScale,
            .brightness, .videoAdvancedSettings, .controller, .remapController,
            .controllerAdvancedSettings, .keyboard, .mods, .achievements,
            .extras, .hiddenDreams, .grimmTroupe, .lifeblood, .godmaster,
            .selectProfile, .clearSave, .quitGame,
        ]
        let requestedContextIdentifiers = ProcessInfo.processInfo.environment[
            "HKV_MENU_FRONT_END_CONTEXTS"
        ]?.split(separator: ",").map(String.init)
        let expectedContexts = requestedContextIdentifiers.map { identifiers in
            allExpectedContexts.filter {
                identifiers.contains($0.storageIdentifier)
            }
        } ?? allExpectedContexts
        let captures = try MenuStencilLiveCaptureStore().load()
        let requestedLanguages = ProcessInfo.processInfo.environment[
            "HKV_MENU_FRONT_END_LANGUAGES"
        ]?.split(separator: ",").map(String.init)
            ?? [HollowKnightMenuLanguage.spanish.rawValue]
        let requestedCaptureIDs = ProcessInfo.processInfo.environment[
            "HKV_MENU_CAPTURE_IDS"
        ].map {
            Set($0.split(separator: ",").compactMap {
                UUID(uuidString: String($0))
            })
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures,
            includedContexts: expectedContexts
        )

        for language in requestedLanguages {
            var capturesByContext = [LabelingContext: [(
                capture: MenuStencilLiveCapture, imageURL: URL
            )]]()
            for source in captures where
                source.capture.resolvedLanguageIdentifier == language
                    && (requestedCaptureIDs?.contains(source.capture.id) ?? true) {
                guard let context = LabelingContext(
                    storageIdentifier: source.capture.contextIdentifier
                ), expectedContexts.contains(context)
                else { continue }
                capturesByContext[context, default: []].append(source)
            }
            XCTAssertEqual(
                Set(capturesByContext.keys),
                expectedContexts,
                "Incomplete front-end capture route for \(language)"
            )
            for context in expectedContexts.sorted(by: {
                $0.storageIdentifier < $1.storageIdentifier
            }) {
                let availableSources = try XCTUnwrap(
                    capturesByContext[context],
                    "Missing \(language) \(context.storageIdentifier)"
                )
                // Each language route records three adjacent animation phases.
                // Older one-off captures can contain stale selection metadata
                // from automation key latency, so they are calibration input,
                // not members of the current route acceptance corpus.
                let sources = requestedCaptureIDs == nil
                    ? Array(availableSources.prefix(3)) : availableSources
                XCTAssertGreaterThanOrEqual(
                    sources.count,
                    requestedCaptureIDs == nil ? 3 : 1
                )
                for source in sources {
                    let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
                    let result = try XCTUnwrap(
                        MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
                    )
                    let label = "\(language) \(context.storageIdentifier) "
                        + source.capture.id.uuidString
                    XCTAssertTrue(result.isMatch, label)
                    XCTAssertEqual(result.context, context, label)
                    XCTAssertEqual(
                        result.selectedOption,
                        source.capture.selectedName,
                        label
                    )
                }
            }
        }
    }

    func testRequestedLanguagesRecognizeEveryFrontEndSelection() throws {
        let allFrontEndContexts: Set<LabelingContext> = [
            .mainTitle, .options, .gameOptions, .audio, .video, .screenScale,
            .brightness, .videoAdvancedSettings, .controller, .remapController,
            .controllerAdvancedSettings, .keyboard, .mods, .achievements,
            .extras, .hiddenDreams, .grimmTroupe, .lifeblood, .godmaster,
            .selectProfile, .clearSave, .quitGame,
        ]
        let requestedContextIdentifiers = ProcessInfo.processInfo.environment[
            "HKV_MENU_FRONT_END_CONTEXTS"
        ]?.split(separator: ",").map(String.init)
        let frontEndContexts = requestedContextIdentifiers.map { identifiers in
            allFrontEndContexts.filter {
                identifiers.contains($0.storageIdentifier)
            }
        } ?? allFrontEndContexts
        let captures = try MenuStencilLiveCaptureStore().load()
        let requestedLanguages = ProcessInfo.processInfo.environment[
            "HKV_MENU_FRONT_END_LANGUAGES"
        ]?.split(separator: ",").map(String.init)
            ?? [HollowKnightMenuLanguage.english.rawValue]
        let requestedCaptureIDs = ProcessInfo.processInfo.environment[
            "HKV_MENU_CAPTURE_IDS"
        ].map {
            Set($0.split(separator: ",").compactMap {
                UUID(uuidString: String($0))
            })
        }
        let fullCatalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures,
            includedContexts: frontEndContexts
        )
        let expectedKeys = Set(fullCatalog.scenes
            .filter { frontEndContexts.contains($0.context) }
            .flatMap { scene in
                scene.options.map {
                    scene.context.storageIdentifier + ":" + $0.classIdentifier
                }
            })

        typealias CaptureSource = (
            capture: MenuStencilLiveCapture, imageURL: URL
        )
        var holdoutLatestByLanguage = [String: [String: CaptureSource]]()
        var latestByLanguage = [String: [String: CaptureSource]]()
        for language in requestedLanguages {
            var latest = [String: (
                capture: MenuStencilLiveCapture, imageURL: URL
            )]()
            for source in captures where
                source.capture.resolvedLanguageIdentifier == language {
                let key = source.capture.contextIdentifier + ":"
                    + source.capture.selectedIdentifier
                if expectedKeys.contains(key), latest[key] == nil {
                    latest[key] = source
                }
            }
            holdoutLatestByLanguage[language] = latest
            if requestedCaptureIDs == nil {
                XCTAssertEqual(
                    Set(latest.keys), expectedKeys,
                    "Incomplete front-end selection sweep for \(language)"
                )
                latestByLanguage[language] = latest
            } else {
                latestByLanguage[language] = latest.filter {
                    requestedCaptureIDs?.contains($0.value.capture.id) == true
                }
            }
        }

        // Validate a new animation phase rather than asking each capture to
        // match a stencil built from its own pixels. Fixed screen positions
        // remain deployed calibration learned by the completed sweep.
        let holdoutIDs = Set(holdoutLatestByLanguage.values.flatMap { latest in
            latest.values.map(\.capture.id)
        })
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures,
            positiveCaptureHoldoutIDs: holdoutIDs,
            includedContexts: frontEndContexts
        )

        for language in requestedLanguages {
            let latest = latestByLanguage[language] ?? [:]
            let requestedKeys = requestedCaptureIDs == nil
                ? expectedKeys : Set(latest.keys)
            for key in requestedKeys.sorted() {
                let source = try XCTUnwrap(latest[key], "Missing \(language) \(key)")
                let context = try XCTUnwrap(LabelingContext(
                    storageIdentifier: source.capture.contextIdentifier
                ))
                let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
                let result = try XCTUnwrap(MenuStencilTracker(
                    catalog: catalog.containingOnly(context)
                ).observe(image, timestamp: 1))
                let label = "\(language) \(key) \(source.capture.id.uuidString)"
                let selectionEvidence = result.selectorCandidates.map {
                    "\($0.name)=\(String(format: "%.3f", $0.confidence))"
                }.joined(separator: ",")
                let detail = "\(label) actual=\(result.selectedOption ?? "nil") "
                    + "language=\(result.languageIdentifier ?? "nil") "
                    + "selectors=[\(selectionEvidence)]"
                XCTAssertTrue(result.isMatch, detail)
                XCTAssertEqual(result.context, context, label)
                XCTAssertEqual(
                    result.selectedOption,
                    source.capture.selectedName,
                    detail
                )
            }
        }
    }

    func testConsensusAlignsCapturesAndKeepsOnlyStableTextPixels() throws {
        let shifts = [CGPoint(x: 0, y: 0), CGPoint(x: 2, y: 1), CGPoint(x: -1, y: -1)]
        let captures = try shifts.enumerated().map { index, shift in
            let image = try XCTUnwrap(Self.consensusFixture(
                shift: shift,
                textValue: UInt8(170 + index * 30),
                noise: CGPoint(x: 17 - index * 2, y: 4 + index)
            ))
            return MenuStencilConsensus.Capture(
                pixels: try XCTUnwrap(ImageStencilPixels(
                    image,
                    referenceWidth: 32,
                    referenceHeight: 18,
                    band: 0..<18
                )),
                annotationBounds: CGRect(
                    x: 8 + (index == 1 ? -1 : 0),
                    y: 3 + (index == 2 ? 1 : 0),
                    width: 12,
                    height: 10
                )
            )
        }
        let result = try XCTUnwrap(MenuStencilConsensus.build(
            captures: captures,
            nominalBounds: CGRect(x: 8, y: 3, width: 12, height: 10),
            preservesAnnotationBounds: true
        ))
        XCTAssertEqual(result.bounds, CGRect(x: 8, y: 3, width: 12, height: 10))
        XCTAssertEqual(result.alignedOffsets, shifts)
        XCTAssertGreaterThan(result.includedPixelCount, 8)
        XCTAssertLessThan(result.includedPixelCount, Int(result.bounds.width * result.bounds.height))
        XCTAssertEqual(
            result.reference.mask.map { [UInt8]($0).filter { $0 != 0 }.count },
            result.includedPixelCount
        )
        let mask = try XCTUnwrap(result.reference.mask).map { $0 }
        let kernel = ImageStencilKernel(
            kind: "synthetic-text",
            bounds: CGRect(
                x: 0,
                y: 0,
                width: result.reference.width,
                height: result.reference.height
            ),
            rgb: result.reference.rgb,
            includes: { x, y, width, _ in mask[y * width + x] != 0 }
        )
        let comparison = try XCTUnwrap(captures[0].pixels.compare(
            kernel,
            at: result.bounds
        ))
        XCTAssertFalse(comparison.supportsCorrelation)
        XCTAssertGreaterThan(
            comparison.confidence(correlationWeight: 0.82, colorErrorScale: 105),
            MenuStencilTracker.anchorThreshold
        )

        let selectorResult = try XCTUnwrap(MenuStencilConsensus.build(
            captures: captures,
            nominalBounds: CGRect(x: 8, y: 3, width: 12, height: 10)
        ))
        XCTAssertEqual(
            selectorResult.bounds,
            CGRect(x: 10, y: 5, width: 7, height: 5)
        )
    }

    func testLocalizedTextConsensusRecoversGlyphsOutsideNominalBox() throws {
        let captures = try [
            CGPoint(x: 2, y: 1),
            CGPoint(x: 27, y: 15),
            CGPoint(x: 3, y: 14),
        ].map { noise in
            let image = try XCTUnwrap(Self.consensusFixture(
                shift: .zero,
                textValue: 220,
                noise: noise
            ))
            return MenuStencilConsensus.Capture(
                pixels: try XCTUnwrap(ImageStencilPixels(
                    image,
                    referenceWidth: 32,
                    referenceHeight: 18,
                    band: 0..<18
                )),
                annotationBounds: CGRect(x: 12, y: 5, width: 3, height: 5)
            )
        }
        let nominal = CGRect(x: 12, y: 5, width: 3, height: 5)
        let result = try XCTUnwrap(MenuStencilConsensus.buildLocalizedText(
            captures: captures,
            nominalBounds: nominal
        ))

        XCTAssertLessThan(result.bounds.minX, nominal.minX)
        XCTAssertGreaterThan(result.bounds.maxX, nominal.maxX)
        XCTAssertGreaterThan(result.includedPixelCount, 8)
    }

    func testVerifiedBoundsFollowOnlyPixelsUsedByGreenStencil() {
        var mask = [UInt8](repeating: 0, count: 10 * 6)
        for y in 1...4 {
            for x in 2...7 { mask[y * 10 + x] = 255 }
        }
        let reference = ImageStencilReference(
            width: 10,
            height: 6,
            rgb: Data(repeating: 180, count: 10 * 6 * 3),
            mask: Data(mask)
        )

        XCTAssertEqual(
            reference.verifiedBounds(
                in: CGRect(x: 100, y: 200, width: 200, height: 60),
                padding: 0
            ),
            CGRect(x: 140, y: 210, width: 120, height: 40)
        )
    }

    func testSelectorContrastRemovesIdleTextAndKeepsDecoration() throws {
        let bounds = CGRect(x: 3, y: 3, width: 11, height: 5)
        let selectedPixels = try XCTUnwrap(ImageStencilPixels(
            try XCTUnwrap(Self.selectorContrastFixture(showsCursor: true)),
            referenceWidth: 20,
            referenceHeight: 12,
            band: 0..<12
        ))
        let idlePixels = try XCTUnwrap(ImageStencilPixels(
            try XCTUnwrap(Self.selectorContrastFixture(showsCursor: false)),
            referenceWidth: 20,
            referenceHeight: 12,
            band: 0..<12
        ))
        let positive = try XCTUnwrap(MenuStencilConsensus.build(
            captures: [.init(
                pixels: selectedPixels,
                annotationBounds: bounds
            )],
            nominalBounds: bounds,
            alignmentRadius: 0,
            preservesAnnotationBounds: true
        ))
        let filtered = try XCTUnwrap(
            MenuStencilConsensus.removingStableBackground(
                from: positive.reference,
                at: positive.bounds,
                negativeFrames: [idlePixels, idlePixels]
            )
        )
        let mask = try XCTUnwrap(filtered.mask).map { $0 }
        for y in 1...3 {
            for x in 1...3 {
                XCTAssertEqual(mask[y * filtered.width + x], 0)
            }
            for x in 7...9 {
                XCTAssertEqual(mask[y * filtered.width + x], 255)
            }
        }
    }

    func testLatestFocusedDraftDefinesFullHumanStencilBounds() throws {
        let exampleID = UUID(uuidString: "EC3C8664-2AD6-4BBB-BC5B-549392476E39")!
        let store = LabelingExampleStore(rootURL: LabelingExampleStore.defaultRootURL())
        guard let example = try store.loadExamples().first(where: { $0.id == exampleID })
        else { throw XCTSkip("Latest focused menu draft is unavailable") }
        let annotation = try XCTUnwrap(example.manifest.annotations.first {
            LabelingClassIdentity.matches(
                $0.classIdentifier,
                "controller-advanced.native-input"
            )
        })
        let expected = CGRect(
            x: annotation.x * Double(example.manifest.imageWidth),
            y: annotation.y * Double(example.manifest.imageHeight),
            width: annotation.width * Double(example.manifest.imageWidth),
            height: annotation.height * Double(example.manifest.imageHeight)
        )
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: []
        )
        let scene = try XCTUnwrap(catalog.scenes.first {
            $0.context == .controllerAdvancedSettings
        })
        let anchor = try XCTUnwrap(scene.anchors.first {
            $0.classIdentifier == "controller-advanced.native-input"
        })

        XCTAssertEqual(anchor.bounds, expected)
        let kindSuffix = ".human-box.\(exampleID.uuidString.lowercased())"
        let variant = try XCTUnwrap(anchor.variants.first {
            $0.kernel.kind.hasSuffix(kindSuffix)
        })
        let sampledWidth = Int(ceil(expected.maxX) - floor(expected.minX))
        let sampledHeight = Int(ceil(expected.maxY) - floor(expected.minY))
        XCTAssertEqual(variant.reference.width, sampledWidth)
        XCTAssertEqual(variant.reference.height, sampledHeight)
        XCTAssertLessThan(
            variant.reference.mask.map { [UInt8]($0).filter { $0 != 0 }.count }
                ?? Int.max,
            sampledWidth * sampledHeight
        )
    }

    func testAllHumanMenuExamplesWhenRequested() throws {
        guard let examplesRoot else {
            throw XCTSkip("Set HKV_MENU_STENCIL_AUDIT to the examples directory")
        }
        let store = LabelingExampleStore(rootURL: examplesRoot)
        // Audit the historical captures against their own measured geometry.
        // Current runtime geometry is covered separately by all live
        // calibration states and their ordered transition tests below.
        let catalog = try MenuStencilCatalog(
            examplesRootURL: examplesRoot,
            calibration: nil
        )
        XCTAssertGreaterThanOrEqual(catalog.scenes.count, 20)
        let contexts = Set(catalog.scenes.map { $0.context.storageIdentifier })
        var truePositive = 0
        var falseNegative = 0
        var falseContext = 0
        var trueNegative = 0
        var falsePositive = 0
        var selectionMismatch = 0
        var comparisonCounts = [Int]()
        let requestedIDs = ProcessInfo.processInfo.environment[
            "HKV_MENU_STENCIL_ONLY"
        ].map { Set($0.lowercased().split(separator: ",").map(String.init)) }
        for example in try store.loadExamples() {
            if let requestedIDs,
               !requestedIDs.contains(example.id.uuidString.lowercased()) {
                continue
            }
            // These two manifests say Quit to Menu, but the captured pixels
            // and their human boxes visibly contain "QUIT GAME?".
            let visuallyQuitGame = [
                "d958e9f5-da0d-40f5-a8e7-07f4931f5abf",
                "dc544c8b-448c-4ce7-8d88-f9ba0b68b032",
            ].contains(example.id.uuidString.lowercased())
            let expected = visuallyQuitGame
                ? LabelingContext.quitGame.storageIdentifier
                : example.manifest.contextIdentifier
            let image = try store.loadImage(for: example)
            let result = MenuStencilTracker(catalog: catalog).observe(
                image,
                timestamp: 1
            )
            if contexts.contains(expected) {
                if result?.isMatch == true, result?.context.storageIdentifier == expected {
                    truePositive += 1
                    if let expectedSelection = expectedSelection(
                        for: example,
                        contextIdentifier: expected
                    ), result?.selectedOption != expectedSelection {
                        selectionMismatch += 1
                        let selectorScores = result?.selectorCandidates.map {
                            "\($0.name)=\(String(format: "%.3f", $0.confidence))"
                        }.joined(separator: ",") ?? ""
                        print("MENU_STENCIL selection=\(example.id) context=\(expected) expected=\(expectedSelection) actual=\(result?.selectedOption ?? "nil") candidates=[\(selectorScores)]")
                    }
                } else if result?.isMatch == true {
                    falseContext += 1
                    let scores = result!.anchors.map {
                        "\($0.name)=\(String(format: "%.3f", $0.confidence))"
                    }.joined(separator: ",")
                    print("MENU_STENCIL wrong=\(example.id) expected=\(expected) actual=\(result!.context.storageIdentifier) confidence=\(result!.confidence) anchors=[\(scores)]")
                    if let expectedContext = LabelingContext(storageIdentifier: expected),
                       let expectedResult = MenuStencilTracker(
                        catalog: catalog.containingOnly(expectedContext)
                       ).observe(image, timestamp: 1) {
                        let expectedScores = expectedResult.anchors.map {
                            "\($0.name)=\(String(format: "%.3f", $0.confidence))"
                        }.joined(separator: ",")
                        print("MENU_STENCIL expectedCandidate confidence=\(expectedResult.confidence) matched=\(expectedResult.isMatch) anchors=[\(expectedScores)]")
                    }
                } else {
                    falseNegative += 1
                    print("MENU_STENCIL missed=\(example.id) expected=\(expected) best=\(result?.context.storageIdentifier ?? "nil") confidence=\(result?.confidence ?? 0)")
                    if let expectedContext = LabelingContext(storageIdentifier: expected),
                       let expectedResult = MenuStencilTracker(
                        catalog: catalog.containingOnly(expectedContext)
                       ).observe(image, timestamp: 1) {
                        let expectedScores = expectedResult.anchors.map {
                            "\($0.name)=\(String(format: "%.3f", $0.confidence))"
                        }.joined(separator: ",")
                        print("MENU_STENCIL expectedCandidate confidence=\(expectedResult.confidence) matched=\(expectedResult.isMatch) anchors=[\(expectedScores)]")
                    }
                }
            } else if result?.isMatch == true {
                falsePositive += 1
                print("MENU_STENCIL falsePositive=\(example.id) expected=\(expected) actual=\(result!.context.storageIdentifier) confidence=\(result!.confidence)")
            } else {
                trueNegative += 1
            }
            if let count = result?.comparisonCount { comparisonCounts.append(count) }
        }
        print(
            "MENU_STENCIL scenes=\(catalog.scenes.count) tp=\(truePositive) "
                + "fn=\(falseNegative) wrong=\(falseContext) tn=\(trueNegative) "
                + "fp=\(falsePositive) selectionWrong=\(selectionMismatch) "
                + "maxComparisons=\(comparisonCounts.max() ?? 0)"
        )
        if requestedIDs == nil {
            XCTAssertEqual(falseNegative, 0)
            XCTAssertEqual(falseContext, 0)
            XCTAssertEqual(falsePositive, 0)
            XCTAssertEqual(selectionMismatch, 0)
        }
    }

    func testLatestLiveCaptureForEveryMeasuredSelectionWhenAvailable() throws {
        let captures = try MenuStencilLiveCaptureStore().load()
        guard !captures.isEmpty else {
            throw XCTSkip("No live menu calibration captures")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let validKeys = Set(catalog.scenes.flatMap { scene in
            scene.options.map {
                scene.context.storageIdentifier + ":" + $0.classIdentifier
            }
        })
        var latest = [String: (capture: MenuStencilLiveCapture, imageURL: URL)]()
        for source in captures {
            let key = source.capture.contextIdentifier + ":"
                + source.capture.selectedIdentifier
            if validKeys.contains(key), latest[key] == nil { latest[key] = source }
        }
        var failures = [String]()
        for source in latest.values {
            guard let context = LabelingContext(
                storageIdentifier: source.capture.contextIdentifier
            ) else { continue }
            let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
            let result = MenuStencilTracker(
                catalog: catalog.containingOnly(context)
            ).observe(image, timestamp: 1)
            if result?.isMatch != true
                || result?.selectedOption != source.capture.selectedName {
                let scores = result?.selectorCandidates.map {
                    "\($0.name)=\(String(format: "%.3f", $0.confidence))"
                }.joined(separator: ",") ?? ""
                failures.append(
                    "\(source.capture.contextIdentifier)/"
                        + "\(source.capture.selectedName) actual="
                        + "\(result?.selectedOption ?? "nil") [\(scores)]"
                )
            } else if let result,
                      let scene = catalog.scenes.first(where: { $0.context == context }) {
                XCTAssertEqual(
                    result.selectorSearchRegions.count,
                    scene.options.count * 2,
                    source.capture.contextIdentifier
                )
                XCTAssertEqual(
                    Set(result.selectorSearchRegions.map {
                        "\($0.side):\($0.rect.midX):\($0.rect.midY)"
                    }).count,
                    result.selectorSearchRegions.count,
                    source.capture.contextIdentifier
                )
                let solved = result.selectorSearchRegions.filter(\.isSolved)
                XCTAssertEqual(solved.count, 2, source.capture.selectedName)
                for region in solved {
                    XCTAssertTrue(result.selectorCandidates.contains {
                        region.rect.contains($0.rect)
                    }, source.capture.selectedName)
                }
                let scaleX = CGFloat(image.width) / CGFloat(scene.referenceWidth)
                let scaleY = CGFloat(image.height) / CGFloat(scene.referenceHeight)
                let offsets = try result.selectorSearchRegions.filter {
                    !$0.isSolved
                }.map { region in
                    let option = try XCTUnwrap(scene.options.first {
                        $0.classIdentifier == region.optionIdentifier
                    })
                    let configured = try XCTUnwrap(
                        region.side == .left
                            ? option.leftSelector?.bounds(
                                for: source.capture.resolvedLanguageIdentifier
                            )
                            : option.rightSelector?.bounds(
                                for: source.capture.resolvedLanguageIdentifier
                            )
                    )
                    return CGPoint(
                        x: region.rect.midX / scaleX - configured.midX,
                        y: (CGFloat(image.height) - region.rect.midY) / scaleY
                            - configured.midY
                    )
                }
                if let commonOffset = offsets.first {
                    for (index, offset) in offsets.dropFirst().enumerated() {
                        let geometryLabel = source.capture.contextIdentifier + "/"
                            + source.capture.selectedName + " region=\(index + 1)"
                        XCTAssertEqual(
                            offset.x, commonOffset.x, accuracy: 0.001,
                            geometryLabel
                        )
                        XCTAssertEqual(
                            offset.y, commonOffset.y, accuracy: 0.001,
                            geometryLabel
                        )
                    }
                }
                if ProcessInfo.processInfo.environment[
                    "HKV_MENU_STENCIL_GEOMETRY_LOG"
                ] == "1",
                   let option = scene.options.first(where: {
                       $0.classIdentifier == source.capture.selectedIdentifier
                   }) {
                    let expected = [option.leftSelector, option.rightSelector]
                        .compactMap { $0?.bounds }
                    let found = result.selectorCandidates.prefix(2).map {
                        CGPoint(
                            x: $0.rect.midX,
                            y: CGFloat(image.height) - $0.rect.midY
                        )
                    }
                    let offsets = zip(expected, found).map {
                        CGPoint(
                            x: $1.x - $0.midX,
                            y: $1.y - $0.midY
                        )
                    }
                    print(
                        "MENU_SELECTOR_GEOMETRY \(source.capture.contextIdentifier)/"
                            + "\(source.capture.selectedIdentifier) offsets=\(offsets)"
                    )
                }
            }
        }
        if !failures.isEmpty {
            print("LIVE_MENU_STENCIL failures=\(failures.joined(separator: " | "))")
        }
        XCTAssertEqual(latest.count, validKeys.count)
        XCTAssertTrue(failures.isEmpty)
    }

    func testItalianKeyboardCorrectionBeatsEmptyAudioPair() throws {
        let exampleID = UUID(
            uuidString: "01A82890-192D-4CD7-BB55-E0D4B27C3CB9"
        )!
        let store = LabelingExampleStore()
        guard let example = try store.loadExamples().first(where: {
            $0.id == exampleID
        }) else { throw XCTSkip("Italian Keyboard correction is unavailable") }
        let image = try store.loadImage(for: example)
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try XCTUnwrap(MenuStencilCalibration.loadDefault()),
            includedContexts: [.options]
        )
        let result = try XCTUnwrap(
            MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
        )

        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.context, .options)
        XCTAssertEqual(result.languageIdentifier, "it")
        XCTAssertEqual(result.selectedOption, "Keyboard")
    }

    func testItalianAdvancedSettingsCorrectionSelectsLabeledRow() throws {
        let exampleID = UUID(
            uuidString: "FC571AB9-9AB2-4EE9-BEC0-7257EFF93C1B"
        )!
        let store = LabelingExampleStore()
        guard let example = try store.loadExamples().first(where: {
            $0.id == exampleID
        }) else { throw XCTSkip("Italian Advanced Settings correction is unavailable") }
        let image = try store.loadImage(for: example)
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try XCTUnwrap(MenuStencilCalibration.loadDefault()),
            includedContexts: [.video]
        )
        let result = try XCTUnwrap(
            MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
        )

        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.context, .video)
        XCTAssertEqual(result.languageIdentifier, "it")
        XCTAssertEqual(result.selectedOption, "Advanced Settings")
    }

    func testItalianGameCorrectionSelectsLabeledRow() throws {
        let exampleID = UUID(
            uuidString: "973AB0CA-EEF2-4902-8B53-E0765479567E"
        )!
        let store = LabelingExampleStore()
        guard let example = try store.loadExamples().first(where: {
            $0.id == exampleID
        }) else { throw XCTSkip("Italian Game correction is unavailable") }
        let image = try store.loadImage(for: example)
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try XCTUnwrap(MenuStencilCalibration.loadDefault()),
            includedContexts: [.options]
        )
        let result = try XCTUnwrap(
            MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
        )

        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.context, .options)
        XCTAssertEqual(result.languageIdentifier, "it")
        XCTAssertEqual(result.selectedOption, "Game")
    }

    func testKoreanGrimmCorrectionRejectsEmptyCreditsPair() throws {
        let exampleID = UUID(
            uuidString: "144A8CFB-9C77-4D92-A621-D7780B96335F"
        )!
        let store = LabelingExampleStore()
        guard let example = try store.loadExamples().first(where: {
            $0.id == exampleID
        }) else { throw XCTSkip("Korean Grimm correction is unavailable") }
        let image = try store.loadImage(for: example)
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try XCTUnwrap(MenuStencilCalibration.loadDefault()),
            includedContexts: [.extras]
        )
        let result = try XCTUnwrap(
            MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
        )

        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.context, .extras)
        XCTAssertEqual(result.languageIdentifier, "ko")
        XCTAssertEqual(result.selectedOption, "The Grimm Troupe")
    }

    func testLatestMultilingualSelectorCorrectionsSelectLabeledRows() throws {
        let expected: [(String, LabelingContext, String)] = [
            ("911E9057-3FE0-452C-BE9C-2D68AD922154", .keyboard, "Attack"),
            ("AE1F5C8D-6D41-427C-B6BE-DDCC911964BD", .keyboard, "Super Dash"),
            ("D57CC45F-DE6A-4BA6-A25F-FDB67B3ADEEA", .keyboard, "Inventory"),
            ("FA0753AB-7E1F-4024-A1EF-F5E651F51B93", .options, "Game"),
            ("152AE0A8-198B-4596-A705-DED146B7883E", .extras, "Hidden Dreams"),
            ("E5616482-3FF6-4842-B313-DFB50C7783BC", .extras, "The Grimm Troupe"),
            ("6D028D16-970D-4755-845B-34DCBF91A247", .extras, "Lifeblood"),
            ("34021979-272E-4680-84E7-2D5BDD35555C", .video, "Advanced Settings"),
            ("5393B426-F329-4E86-80D2-5E104C36A746", .extras, "Hidden Dreams"),
            ("7B00F837-FC48-46FC-9AF2-9C19C471ED3E", .extras, "The Grimm Troupe"),
            ("07A600CE-FC9A-42FD-AA72-6E54D0412971", .extras, "Lifeblood"),
            ("D3BF03A8-98EC-4CEA-9696-B647C674221E", .quitGame, "No"),
            ("00B9FE92-B22D-400A-947C-03848590DBBF", .video, "Advanced Settings"),
            ("AA6813FA-8B96-4B2C-B499-0A3F46F745C6", .keyboard, "Attack"),
            ("3224CB73-47A1-4F4B-B4AD-21EE272B4B81", .keyboard, "Attack"),
            ("16A25BAA-201A-40AD-ABC2-28F97683D505", .extras, "Lifeblood"),
            ("144A8CFB-9C77-4D92-A621-D7780B96335F", .extras, "The Grimm Troupe"),
            ("66CCC048-781A-4D48-8D80-6D58A1CA9530", .video, "Advanced Settings"),
            ("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F", .keyboard, "Right"),
            ("BB7C33FD-0000-41D3-A25F-525B663772D2", .keyboard, "Super Dash"),
        ]
        let store = LabelingExampleStore()
        let examples = try store.loadExamples()
        let available = expected.compactMap { item in
            examples.first(where: { $0.id == UUID(uuidString: item.0) }).map {
                ($0, item.1, item.2)
            }
        }
        guard !available.isEmpty else {
            throw XCTSkip("Latest multilingual corrections are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try XCTUnwrap(MenuStencilCalibration.loadDefault())
        )

        for (example, context, selectedName) in available {
            let image = try store.loadImage(for: example)
            let result = try XCTUnwrap(
                MenuStencilTracker(catalog: catalog.containingOnly(context))
                    .observe(image, timestamp: 1),
                example.id.uuidString
            )
            XCTAssertTrue(result.isMatch, example.id.uuidString)
            XCTAssertEqual(result.context, context, example.id.uuidString)
            XCTAssertEqual(
                result.selectedOption,
                selectedName,
                example.id.uuidString
            )
        }
    }

    func testNewestKoreanSelectorCorrectionsSelectLabeledRows() throws {
        let expected: [(String, LabelingContext, String)] = [
            ("66CCC048-781A-4D48-8D80-6D58A1CA9530", .video, "Advanced Settings"),
            ("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F", .keyboard, "Right"),
            ("BB7C33FD-0000-41D3-A25F-525B663772D2", .keyboard, "Super Dash"),
        ]
        let store = LabelingExampleStore()
        let examples = try store.loadExamples()
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try XCTUnwrap(MenuStencilCalibration.loadDefault())
        )
        let correctionKeys = Set(catalog.selectorCorrectionPlacements.map {
            $0.context.storageIdentifier + ":" + $0.optionIdentifier + ":"
                + $0.languageIdentifier
        })
        XCTAssertTrue(correctionKeys.contains("video:shared.advanced-settings:ko"))
        XCTAssertTrue(correctionKeys.contains("keyboard:keyboard.right:ko"))
        XCTAssertTrue(correctionKeys.contains("keyboard:shared.super-dash:ko"))

        for (identifier, context, selectedName) in expected {
            let id = try XCTUnwrap(UUID(uuidString: identifier))
            let example = try XCTUnwrap(examples.first { $0.id == id })
            let image = try store.loadImage(for: example)
            let result = try XCTUnwrap(
                MenuStencilTracker(catalog: catalog.containingOnly(context))
                    .observe(image, timestamp: 1),
                identifier
            )
            XCTAssertTrue(result.isMatch, identifier)
            XCTAssertEqual(result.context, context, identifier)
            XCTAssertEqual(result.selectedOption, selectedName, identifier)
            XCTAssertEqual(result.selectorCandidates.count, 2, identifier)
            if result.selectorCandidates.count == 2 {
                XCTAssertEqual(
                    result.selectorCandidates[0].rect.midY,
                    result.selectorCandidates[1].rect.midY,
                    accuracy: 3,
                    identifier
                )
            }
        }
    }

    func testNewestKoreanCorrectionsPersistIntoCalibration() throws {
        let store = LabelingExampleStore()
        let catalog = try MenuStencilCatalog(
            examplesRootURL: store.rootURL,
            calibration: try XCTUnwrap(MenuStencilCalibration.loadDefault())
        )
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-newest-korean-corrections-\(UUID().uuidString)", isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        let mirrorURL = directory.appendingPathComponent("project-menu-stencils.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        let stale = try XCTUnwrap(
            MenuStencilCalibration.loadDefault()?.replacingSelector(
                contextIdentifier: LabelingContext.keyboard.storageIdentifier,
                selectedIdentifier: LabelingClassIdentity.superDash,
                languageIdentifier: "ko",
                leftRect: CGRect(x: 303, y: 173, width: 10, height: 8),
                rightRect: CGRect(x: 487.5, y: 182.5, width: 10, height: 8)
            )
        )
        try stale.write(to: outputURL)

        _ = MenuStencilTracker(
            catalog: catalog,
            selectorCalibrationURL: outputURL,
            selectorCalibrationMirrorURL: mirrorURL
        )

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let persisted = try decoder.decode(
            MenuStencilCalibration.self,
            from: Data(contentsOf: outputURL)
        )
        let keyboard = try XCTUnwrap(persisted.scene(.keyboard))
        let right = try XCTUnwrap(keyboard.selectors.first {
            $0.selectedIdentifier == "keyboard.right"
        }?.localizedPlacements?["ko"])
        let superDash = try XCTUnwrap(keyboard.selectors.first {
            $0.selectedIdentifier == LabelingClassIdentity.superDash
        }?.localizedPlacements?["ko"])
        XCTAssertEqual(right.leftRect.cgRect.midY, right.rightRect.cgRect.midY, accuracy: 3)
        XCTAssertEqual(
            superDash.leftRect.cgRect.midY,
            superDash.rightRect.cgRect.midY,
            accuracy: 3
        )
        XCTAssertEqual(
            try Data(contentsOf: mirrorURL),
            try Data(contentsOf: outputURL)
        )
    }

    func testKeyboardTwoColumnRowRecognizesFadedFocusCaptureWithoutSelfReference() throws {
        let captureID = UUID(
            uuidString: "B3126000-F082-4D1A-A5F2-DBF97CA8BF30"
        )!
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let source = captures.first(where: { $0.capture.id == captureID }) else {
            throw XCTSkip("Faded Keyboard regression capture is unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures.filter { $0.capture.id != captureID }
        ).containingOnly(.keyboard)
        let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
        let result = try XCTUnwrap(
            MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
        )

        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.selectedOption, "Focus / Cast")
    }

    func testReportedMenuFailuresRecognizeWithoutSelfReference() throws {
        let expected: [UUID: (LabelingContext, String)] = [
            UUID(uuidString: "A5D1697C-189B-4F73-BFED-70ED4392C1EA")!:
                (.gameOptions, "Show Achievements"),
            UUID(uuidString: "C2BF6275-0A46-4F57-9BF8-0F889C2DD88F")!:
                (.video, "Advanced Settings"),
            UUID(uuidString: "3E46B7D6-5670-4824-B4A2-1515973F9F27")!:
                (.videoAdvancedSettings, "Particle Effects"),
            UUID(uuidString: "D87C6496-EA21-4E98-9222-E4EF4F309444")!:
                (.achievements, "Back"),
            UUID(uuidString: "B3126000-F082-4D1A-A5F2-DBF97CA8BF30")!:
                (.keyboard, "Focus / Cast"),
            UUID(uuidString: "7F7B6D9F-0112-4BAC-875E-83C2FBB0F647")!:
                (.selectProfile, "Clear Save 1"),
            UUID(uuidString: "CF3E3014-A724-4BCB-A8EF-B7CBFE4A7D10")!:
                (.controller, "Advanced Settings"),
        ]
        let captures = try MenuStencilLiveCaptureStore().load()
        let audited = captures.filter { expected[$0.capture.id] != nil }
        guard audited.count == expected.count else {
            throw XCTSkip("Reported menu regression captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures.filter { expected[$0.capture.id] == nil }
        )

        for source in audited {
            let wanted = try XCTUnwrap(expected[source.capture.id])
            let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
            let result = try XCTUnwrap(
                MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
            )
            XCTAssertTrue(result.isMatch, source.capture.id.uuidString)
            XCTAssertEqual(result.context, wanted.0, source.capture.id.uuidString)
            XCTAssertEqual(
                result.selectedOption,
                wanted.1,
                source.capture.id.uuidString
            )
            let scene = try XCTUnwrap(catalog.scenes.first {
                $0.context == wanted.0
            })
            XCTAssertEqual(
                result.selectorSearchRegions.count,
                scene.options.count * 2,
                source.capture.id.uuidString
            )
        }
    }

    func testQuitGameDialogIsNotConfusedWithQuitToMenuAndKeepsSearchBoxes() throws {
        let expected: [UUID: LabelingContext] = [
            UUID(uuidString: "17D7F253-8BB2-43D3-8054-258E902FF9D8")!: .quitGame,
            UUID(uuidString: "AB625546-6B69-41BC-AF99-83E70238A7F1")!: .quitToMenu,
        ]
        let captures = try MenuStencilLiveCaptureStore().load()
        let audited = captures.filter { expected[$0.capture.id] != nil }
        guard audited.count == expected.count else {
            throw XCTSkip("Quit dialog regression captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures.filter { expected[$0.capture.id] == nil },
            includedContexts: [.quitGame, .quitToMenu]
        )
        func assertInactiveYesSearchesAlign(
            _ result: MenuStencilResult,
            file: StaticString = #filePath,
            line: UInt = #line
        ) throws {
            let yesAnchor = try XCTUnwrap(result.anchors.first {
                $0.classIdentifier == LabelingClassIdentity.yes
            }, file: file, line: line)
            let yesSearches = result.selectorSearchRegions.filter {
                $0.optionIdentifier == LabelingClassIdentity.yes
            }
            XCTAssertEqual(yesSearches.count, 2, file: file, line: line)
            for search in yesSearches {
                XCTAssertEqual(
                    search.rect.midY,
                    yesAnchor.rect.midY,
                    accuracy: 3,
                    file: file,
                    line: line
                )
            }
        }
        func assertCompleteQuitGameTitle(
            _ result: MenuStencilResult,
            file: StaticString = #filePath,
            line: UInt = #line
        ) throws {
            guard result.context == .quitGame else { return }
            let title = try XCTUnwrap(result.anchors.first {
                $0.classIdentifier == "quit-game.quit-game"
            }, file: file, line: line)
            let verified = try XCTUnwrap(
                title.reference?.verifiedBounds(in: title.rect),
                file: file,
                line: line
            )
            XCTAssertGreaterThanOrEqual(title.rect.maxX, 361, file: file, line: line)
            XCTAssertGreaterThanOrEqual(verified.maxX, 360, file: file, line: line)
        }
        for source in audited {
            let wanted = try XCTUnwrap(expected[source.capture.id])
            let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
            let tracker = MenuStencilTracker(catalog: catalog)
            let result = try XCTUnwrap(
                tracker.observe(image, timestamp: 1)
            )
            print(
                "QUIT_DIALOG_REGRESSION context=\(result.context.storageIdentifier) "
                    + "match=\(result.isMatch) selected=\(result.selectedOption ?? "nil") "
                    + "confidence=\(result.confidence) regions=\(result.selectorSearchRegions.count)"
            )

            XCTAssertTrue(result.isMatch)
            XCTAssertEqual(result.context, wanted)
            XCTAssertEqual(result.selectedOption, "No")
            XCTAssertEqual(result.selectorSearchRegions.count, 4)
            try assertInactiveYesSearchesAlign(result)
            try assertCompleteQuitGameTitle(result)
            for frame in 2...12 {
                let tracked = try XCTUnwrap(tracker.observe(
                    image,
                    timestamp: 1 + Double(frame - 1) / 30
                ))
                XCTAssertTrue(tracked.isMatch, "frame \(frame)")
                XCTAssertEqual(tracked.context, wanted, "frame \(frame)")
                XCTAssertEqual(tracked.selectedOption, "No", "frame \(frame)")
                try assertInactiveYesSearchesAlign(tracked)
                try assertCompleteQuitGameTitle(tracked)
            }
        }
    }

    func testStableSelectorMatchPersistsProductCalibration() throws {
        let source = try XCTUnwrap(try MenuStencilLiveCaptureStore().load().first {
            $0.capture.contextIdentifier == LabelingContext.mainTitle.storageIdentifier
                && $0.capture.resolvedLanguageIdentifier
                    == HollowKnightMenuLanguage.english.rawValue
        })
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        ).containingOnly(.mainTitle)
        let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-menu-calibration-\(UUID().uuidString)", isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true
        )
        let previous = try XCTUnwrap(MenuStencilCalibration.loadDefault())
        let previousSelector = try XCTUnwrap(
            previous.scene(.mainTitle)?.selectors.first {
                $0.selectedIdentifier == source.capture.selectedIdentifier
            }
        )
        let tracker = MenuStencilTracker(
            catalog: catalog,
            selectorCalibrationURL: outputURL
        )
        // Acquisition evaluates multiple language hypotheses. Calibration
        // begins on the first resolved tracking frame, so include nine tracked
        // observations spanning the required two-second stability window.
        for index in 0...9 {
            let timestamp = Double(index) * 0.25
            let result = try XCTUnwrap(tracker.observe(image, timestamp: timestamp))
            XCTAssertEqual(result.selectedOption, source.capture.selectedName)
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let persisted = try decoder.decode(
            MenuStencilCalibration.self,
            from: Data(contentsOf: outputURL)
        )
        let persistedSelector = try XCTUnwrap(
            persisted.scene(.mainTitle)?.selectors.first {
                $0.selectedIdentifier == source.capture.selectedIdentifier
            }
        )
        XCTAssertTrue(persistedSelector.measured)
        XCTAssertEqual(
            persistedSelector.evidenceCount,
            previousSelector.evidenceCount + 1
        )
    }

    func testSettledSelectorCanRecalibrateAgain() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-menu-recalibration-\(UUID().uuidString)", isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        let mirrorURL = directory.appendingPathComponent("project-menu-stencils.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        let tracker = MenuStencilTracker(
            catalog: Self.selectorRecalibrationCatalog(),
            selectorCalibrationURL: outputURL,
            selectorCalibrationMirrorURL: mirrorURL
        )
        let firstImage = try XCTUnwrap(Self.selectorRecalibrationImage(
            left: CGPoint(x: 12, y: 20),
            right: CGPoint(x: 50, y: 20)
        ))
        let secondImage = try XCTUnwrap(Self.selectorRecalibrationImage(
            left: CGPoint(x: 15, y: 23),
            right: CGPoint(x: 53, y: 23)
        ))

        var firstResult: MenuStencilResult?
        // The acquisition frame is intentionally excluded from calibration.
        // Nine resolved samples span two seconds; the following frame exposes
        // the newly persisted search bounds in the returned debug regions.
        for index in 0...10 {
            firstResult = tracker.observe(
                firstImage,
                timestamp: Double(index) * 0.25
            )
        }
        let firstLeft = try XCTUnwrap(firstResult?.selectorSearchRegions.first {
            $0.optionIdentifier == "fixture-option" && $0.side == .left
        })

        var secondResult: MenuStencilResult?
        for index in 0...9 {
            secondResult = tracker.observe(
                secondImage,
                timestamp: 3 + Double(index) * 0.25
            )
        }
        let secondLeft = try XCTUnwrap(secondResult?.selectorSearchRegions.first {
            $0.optionIdentifier == "fixture-option" && $0.side == .left
        })
        XCTAssertEqual(secondLeft.rect.midX - firstLeft.rect.midX, 3, accuracy: 0.001)
        XCTAssertEqual(secondLeft.rect.midY - firstLeft.rect.midY, -3, accuracy: 0.001)

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let persisted = try decoder.decode(
            MenuStencilCalibration.self,
            from: Data(contentsOf: outputURL)
        )
        let selector = try XCTUnwrap(
            persisted.scene(.mainTitle)?.selectors.first {
                $0.selectedIdentifier == "fixture-option"
            }
        )
        XCTAssertEqual(selector.evidenceCount, 2)
        XCTAssertEqual(selector.leftRect.x, 15, accuracy: 0.001)
        XCTAssertEqual(selector.leftRect.y, 23, accuracy: 0.001)
        XCTAssertEqual(
            try Data(contentsOf: mirrorURL),
            try Data(contentsOf: outputURL)
        )
    }

    func testReviewedHumanCorrectionPersistsWithoutWaitingForLiveMatch() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-menu-human-correction-\(UUID().uuidString)", isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        let mirrorURL = directory.appendingPathComponent("project-menu-stencils.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        let base = Self.selectorRecalibrationCatalog()
        let left = CGRect(x: 12, y: 18, width: 4, height: 4)
        let right = CGRect(x: 50, y: 18, width: 4, height: 4)
        let correctionID = UUID()
        let catalog = MenuStencilCatalog(
            scenes: base.scenes,
            referenceWidth: base.referenceWidth,
            referenceHeight: base.referenceHeight,
            pixelBand: base.pixelBand,
            selectorCorrectionPlacements: [.init(
                context: .mainTitle,
                optionIdentifier: "fixture-option",
                languageIdentifier: "en",
                left: left,
                right: right,
                sourceExampleIdentifier: correctionID
            )]
        )

        _ = MenuStencilTracker(
            catalog: catalog,
            selectorCalibrationURL: outputURL,
            selectorCalibrationMirrorURL: mirrorURL
        )

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let persisted = try decoder.decode(
            MenuStencilCalibration.self,
            from: Data(contentsOf: outputURL)
        )
        let placement = try XCTUnwrap(
            persisted.scene(.mainTitle)?.selectors.first {
                $0.selectedIdentifier == "fixture-option"
            }?.localizedPlacements?["en"]
        )
        XCTAssertEqual(placement.leftRect.cgRect, left)
        XCTAssertEqual(placement.rightRect.cgRect, right)
        XCTAssertEqual(
            try Data(contentsOf: mirrorURL),
            try Data(contentsOf: outputURL)
        )
    }

    func testReviewedHumanCorrectionDoesNotOverwriteNewerLiveCalibration() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-menu-human-correction-newer-live-\(UUID().uuidString)",
            isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        let base = Self.selectorRecalibrationCatalog()
        let correctionID = UUID()
        let correctedLeft = CGRect(x: 12, y: 18, width: 4, height: 4)
        let correctedRight = CGRect(x: 50, y: 18, width: 4, height: 4)
        let catalog = MenuStencilCatalog(
            scenes: base.scenes,
            referenceWidth: base.referenceWidth,
            referenceHeight: base.referenceHeight,
            pixelBand: base.pixelBand,
            selectorCorrectionPlacements: [.init(
                context: .mainTitle,
                optionIdentifier: "fixture-option",
                languageIdentifier: "en",
                left: correctedLeft,
                right: correctedRight,
                sourceExampleIdentifier: correctionID
            )]
        )

        _ = MenuStencilTracker(
            catalog: catalog,
            selectorCalibrationURL: outputURL
        )
        let imported = try XCTUnwrap(MenuStencilCalibration.load(outputURL))
        let liveLeft = correctedLeft.offsetBy(dx: 2, dy: 1)
        let liveRight = correctedRight.offsetBy(dx: 2, dy: 1)
        let refined = try XCTUnwrap(imported.replacingSelector(
            contextIdentifier: LabelingContext.mainTitle.storageIdentifier,
            selectedIdentifier: "fixture-option",
            languageIdentifier: "en",
            leftRect: liveLeft,
            rightRect: liveRight,
            now: Date().addingTimeInterval(10)
        ))
        try refined.write(to: outputURL)

        _ = MenuStencilTracker(
            catalog: catalog,
            selectorCalibrationURL: outputURL
        )
        let persisted = try XCTUnwrap(MenuStencilCalibration.load(outputURL))
        let placement = try XCTUnwrap(
            persisted.scene(.mainTitle)?.selectors.first {
                $0.selectedIdentifier == "fixture-option"
            }?.localizedPlacements?["en"]
        )
        XCTAssertEqual(placement.leftRect.cgRect, liveLeft)
        XCTAssertEqual(placement.rightRect.cgRect, liveRight)
    }

    func testCatalogGeometryDoesNotMasqueradeAsPersistedCalibration() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-menu-unpersisted-catalog-\(UUID().uuidString)", isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true
        )
        // Reproduces a deployed calibration file that exists but has no entry
        // for geometry already known by the runtime catalog (for example, a
        // localized live capture or a human correction).
        try Data("stale".utf8).write(to: outputURL)
        let tracker = MenuStencilTracker(
            catalog: Self.selectorRecalibrationCatalog(),
            selectorCalibrationURL: outputURL
        )
        let image = try XCTUnwrap(Self.selectorRecalibrationImage(
            left: CGPoint(x: 10, y: 20),
            right: CGPoint(x: 48, y: 20)
        ))
        for index in 0...9 {
            let result = try XCTUnwrap(tracker.observe(
                image,
                timestamp: Double(index) * 0.25
            ))
            XCTAssertEqual(result.selectedOption, "Fixture Option")
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let persisted = try decoder.decode(
            MenuStencilCalibration.self,
            from: Data(contentsOf: outputURL)
        )
        XCTAssertNotNil(
            persisted.scene(.mainTitle)?.selectors.first {
                $0.selectedIdentifier == "fixture-option"
            }
        )
    }

    func testHighFrameRateCalibrationStillSpansStabilityWindow() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-menu-high-rate-calibration-\(UUID().uuidString)", isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        let tracker = MenuStencilTracker(
            catalog: Self.selectorRecalibrationCatalog(),
            selectorCalibrationURL: outputURL
        )
        let image = try XCTUnwrap(Self.selectorRecalibrationImage(
            left: CGPoint(x: 10, y: 20),
            right: CGPoint(x: 48, y: 20)
        ))
        for frame in 0...150 {
            _ = tracker.observe(image, timestamp: Double(frame) / 60)
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let persisted = try decoder.decode(
            MenuStencilCalibration.self,
            from: Data(contentsOf: outputURL)
        )
        XCTAssertNotNil(
            persisted.scene(.mainTitle)?.selectors.first {
                $0.selectedIdentifier == "fixture-option"
            }
        )
    }

    func testDashedSelectorRegionsStayNarrowAndCoverSolvedCursor() throws {
        let image = try XCTUnwrap(Self.selectorRecalibrationImage(
            left: CGPoint(x: 12, y: 20),
            right: CGPoint(x: 50, y: 20)
        ))
        let result = try XCTUnwrap(MenuStencilTracker(
            catalog: Self.selectorRecalibrationCatalog()
        ).observe(image, timestamp: 1))

        XCTAssertEqual(result.selectorSearchRegions.count, 2)
        XCTAssertEqual(result.selectorCandidates.count, 2)
        for region in result.selectorSearchRegions {
            XCTAssertTrue(region.isSolved)
            XCTAssertEqual(region.rect.width, 12, accuracy: 0.001)
            XCTAssertEqual(region.rect.height, 12, accuracy: 0.001)
            XCTAssertTrue(result.selectorCandidates.contains {
                region.rect.contains($0.rect)
            })
        }
    }

    func testSelectorPairRejectsCandidatesOnDifferentVisualRows() throws {
        let image = try XCTUnwrap(Self.selectorRecalibrationImage(
            left: CGPoint(x: 12, y: 17),
            right: CGPoint(x: 50, y: 24)
        ))
        let result = try XCTUnwrap(MenuStencilTracker(
            catalog: Self.selectorRecalibrationCatalog()
        ).observe(image, timestamp: 1))

        XCTAssertNil(result.selectedOption)
        XCTAssertTrue(result.selectorCandidates.isEmpty)
        XCTAssertTrue(result.selectorSearchRegions.allSatisfy { !$0.isSolved })
    }

    func testIntermittentSelectorMatchesDoNotPersistCalibration() throws {
        let sources = try MenuStencilLiveCaptureStore().load().filter {
            $0.capture.contextIdentifier == LabelingContext.mainTitle.storageIdentifier
        }
        var latestBySelection = [String: (
            capture: MenuStencilLiveCapture,
            imageURL: URL
        )]()
        for source in sources where
            latestBySelection[source.capture.selectedIdentifier] == nil {
            latestBySelection[source.capture.selectedIdentifier] = source
        }
        let alternating = Array(latestBySelection.values.prefix(2))
        XCTAssertEqual(alternating.count, 2)
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        ).containingOnly(.mainTitle)
        let images = try alternating.map { source in
            try XCTUnwrap(ImageFileIO.load(source.imageURL))
        }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "hkv-menu-calibration-\(UUID().uuidString)", isDirectory: true
        )
        let outputURL = directory.appendingPathComponent("menu-stencil-positions.json")
        defer { try? FileManager.default.removeItem(at: directory) }
        let tracker = MenuStencilTracker(
            catalog: catalog,
            selectorCalibrationURL: outputURL
        )
        for index in 0..<20 {
            let sourceIndex = index % 2
            let result = try XCTUnwrap(tracker.observe(
                images[sourceIndex],
                timestamp: Double(index) * 0.25
            ))
            XCTAssertEqual(
                result.selectedOption,
                alternating[sourceIndex].capture.selectedName
            )
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: outputURL.path))
    }

    func testLatestLiveCaptureAcquiresItsSceneFromTheFullCatalog() throws {
        let captures = try MenuStencilLiveCaptureStore().load()
        guard !captures.isEmpty else {
            throw XCTSkip("No live menu calibration captures")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let validKeys = Set(catalog.scenes.flatMap { scene in
            scene.options.map {
                scene.context.storageIdentifier + ":" + $0.classIdentifier
            }
        })
        var latest = [String: (capture: MenuStencilLiveCapture, imageURL: URL)]()
        for source in captures {
            let key = source.capture.contextIdentifier + ":"
                + source.capture.selectedIdentifier
            if validKeys.contains(key), latest[key] == nil { latest[key] = source }
        }
        var failures = [String]()
        for source in latest.values {
            let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
            let result = MenuStencilTracker(catalog: catalog).observe(
                image,
                timestamp: 1
            )
            if result?.isMatch != true
                || result?.context.storageIdentifier != source.capture.contextIdentifier
                || result?.selectedOption != source.capture.selectedName {
                failures.append(
                    "\(source.capture.contextIdentifier)/"
                        + "\(source.capture.selectedName) actual="
                        + "\(result?.context.storageIdentifier ?? "nil")/"
                        + "\(result?.selectedOption ?? "nil")"
                )
            }
        }
        if !failures.isEmpty {
            print("LIVE_MENU_ACQUISITION failures=\(failures.joined(separator: " | "))")
        }
        XCTAssertEqual(latest.count, validKeys.count)
        XCTAssertTrue(failures.isEmpty)
    }

    func testLiveSelectorTransitionsCannotRemainStuckOnPreviousRow() throws {
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let defaultAuditedContexts: Set<LabelingContext> = [
            .mainTitle, .gameOptions, .controller, .remapController,
            .selectProfile,
        ]
        let requestedContexts = ProcessInfo.processInfo.environment[
            "HKV_MENU_TRANSITION_CONTEXTS"
        ]?.split(separator: ",").compactMap {
            LabelingContext(storageIdentifier: String($0))
        }
        let auditedContexts = requestedContexts.map(Set.init)
            ?? defaultAuditedContexts
        let requestedTargets = ProcessInfo.processInfo.environment[
            "HKV_MENU_TRANSITION_TARGETS"
        ].map { Set($0.split(separator: ",").map(String.init)) }
        let validKeys = Set(catalog.scenes.flatMap { scene in
            scene.options.map {
                scene.context.storageIdentifier + ":" + $0.classIdentifier
            }
        })
        var latest = [String: (capture: MenuStencilLiveCapture, imageURL: URL)]()
        for source in try MenuStencilLiveCaptureStore().load() {
            let selectionKey = source.capture.contextIdentifier + ":"
                + source.capture.selectedIdentifier
            let key = source.capture.resolvedLanguageIdentifier + ":"
                + selectionKey
            if validKeys.contains(selectionKey), latest[key] == nil {
                latest[key] = source
            }
        }
        var images = [String: CGImage]()
        for (key, source) in latest {
            images[key] = try XCTUnwrap(ImageFileIO.load(source.imageURL))
        }
        var failures = [String]()
        var transitionCount = 0
        for context in auditedContexts {
            let contextSources = latest.filter {
                $0.value.capture.contextIdentifier == context.storageIdentifier
            }
            let byLanguage = Dictionary(grouping: contextSources) {
                $0.value.capture.resolvedLanguageIdentifier
            }
            for languageSources in byLanguage.values {
                let sources = languageSources.sorted { $0.key < $1.key }
                for from in sources {
                    for to in sources where to.key != from.key
                        && (requestedTargets?.contains(
                            to.value.capture.selectedIdentifier
                        ) ?? true) {
                        transitionCount += 1
                        if ProcessInfo.processInfo.environment[
                            "HKV_MENU_TRANSITION_LOG"
                        ] == "1" {
                            print(
                                "MENU_TRANSITION \(context.storageIdentifier) "
                                    + "\(from.value.capture.selectedName)->"
                                    + "\(to.value.capture.selectedName)"
                            )
                        }
                        let tracker = MenuStencilTracker(
                            catalog: catalog.containingOnly(context)
                        )
                        _ = tracker.observe(
                            try XCTUnwrap(images[from.key]), timestamp: 1
                        )
                        let result = tracker.observe(
                            try XCTUnwrap(images[to.key]), timestamp: 2
                        )
                        if result?.selectedOption != to.value.capture.selectedName {
                            failures.append(
                                "\(context.storageIdentifier):"
                                    + "\(from.value.capture.selectedName)->"
                                    + "\(to.value.capture.selectedName) actual="
                                    + "\(result?.selectedOption ?? "nil")"
                            )
                        }
                    }
                }
            }
        }
        if !failures.isEmpty {
            print("LIVE_MENU_TRANSITIONS failures=\(failures.joined(separator: " | "))")
        }
        if requestedContexts == nil, requestedTargets == nil {
            XCTAssertGreaterThanOrEqual(transitionCount, 214)
        } else {
            XCTAssertGreaterThan(transitionCount, 0)
        }
        XCTAssertTrue(failures.isEmpty)
    }

    func testFindingStateKeepsBestCandidateSearchBoxesVisible() throws {
        let capture = try XCTUnwrap(try MenuStencilLiveCaptureStore().load().first {
            $0.capture.contextIdentifier == LabelingContext.mainTitle.storageIdentifier
        })
        let fullCatalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL()
        )
        let catalog = fullCatalog.containingOnly(.options)
        let scene = try XCTUnwrap(catalog.scenes.first)
        let image = try XCTUnwrap(ImageFileIO.load(capture.imageURL))
        let result = try XCTUnwrap(MenuStencilTracker(catalog: catalog).observe(
            image,
            timestamp: 1
        ))

        XCTAssertFalse(result.isMatch)
        XCTAssertEqual(result.phase, .searching)
        XCTAssertEqual(result.selectorSearchRegions.count, scene.options.count * 2)
        XCTAssertTrue(result.selectorSearchRegions.allSatisfy { !$0.isSolved })
        XCTAssertEqual(
            Set(result.selectorSearchRegions.map(\.optionIdentifier)),
            Set(scene.options.map(\.classIdentifier))
        )
    }

    func testTrackingUsesFarFewerComparisonsWhenRequested() throws {
        guard let examplesRoot else {
            throw XCTSkip("Set HKV_MENU_STENCIL_AUDIT to the examples directory")
        }
        let store = LabelingExampleStore(rootURL: examplesRoot)
        let catalog = try MenuStencilCatalog(examplesRootURL: examplesRoot)
        let example = try XCTUnwrap(try store.loadExamples().first {
            $0.manifest.contextIdentifier == LabelingContext.options.storageIdentifier
        })
        let image = try store.loadImage(for: example)
        let acquisitionIterations = 60
        let acquisitionStarted = ProcessInfo.processInfo.systemUptime
        for index in 0..<acquisitionIterations {
            let result = try XCTUnwrap(MenuStencilTracker(catalog: catalog).observe(
                image,
                timestamp: Double(index)
            ))
            XCTAssertTrue(result.isMatch)
        }
        let acquisitionMilliseconds = (
            ProcessInfo.processInfo.systemUptime - acquisitionStarted
        ) * 1_000 / Double(acquisitionIterations)
        let tracker = MenuStencilTracker(catalog: catalog)
        let acquired = try XCTUnwrap(tracker.observe(image, timestamp: 1))
        let tracked = try XCTUnwrap(tracker.observe(image, timestamp: 2))
        XCTAssertTrue(acquired.isMatch)
        XCTAssertTrue(tracked.isMatch)
        XCTAssertEqual(tracked.context, .options)
        XCTAssertLessThan(tracked.comparisonCount, acquired.comparisonCount)
        let iterations = 240
        var maximumTrackedComparisons = tracked.comparisonCount
        let started = ProcessInfo.processInfo.systemUptime
        for index in 0..<iterations {
            let result = try XCTUnwrap(tracker.observe(
                image,
                timestamp: Double(index + 3)
            ))
            XCTAssertTrue(result.isMatch)
            maximumTrackedComparisons = max(
                maximumTrackedComparisons,
                result.comparisonCount
            )
        }
        let averageMilliseconds = (ProcessInfo.processInfo.systemUptime - started)
            * 1_000 / Double(iterations)
        XCTAssertLessThan(averageMilliseconds, 3)
        print(
            "MENU_STENCIL acquireComparisons=\(acquired.comparisonCount) "
                + "trackComparisons=\(tracked.comparisonCount) "
                + "maxTrackedComparisons=\(maximumTrackedComparisons) "
                + "averageAcquireMS=\(acquisitionMilliseconds) "
                + "averageTrackedMS=\(averageMilliseconds)"
        )
    }

    func testProblematicExtrasReferenceWhenRequested() throws {
        guard let examplesRoot else {
            throw XCTSkip("Set HKV_MENU_STENCIL_AUDIT to the examples directory")
        }
        let store = LabelingExampleStore(rootURL: examplesRoot)
        let catalog = try MenuStencilCatalog(examplesRootURL: examplesRoot)
        let example = try XCTUnwrap(try store.loadExamples().first {
            $0.id.uuidString.lowercased() == "11766cc0-fbca-4349-9415-47740f1efbcd"
        })
        let image = try store.loadImage(for: example)
        let result = try XCTUnwrap(MenuStencilTracker(
            catalog: catalog.containingOnly(.extras)
        ).observe(image, timestamp: 1))
        print("EXTRAS_DIAGNOSTIC match=\(result.isMatch) confidence=\(result.confidence) anchors=\(result.anchors.map { ($0.name, $0.confidence) })")
    }

    func testExternalMenuFrameWhenRequested() throws {
        guard let path = ProcessInfo.processInfo.environment[
            "HKV_MENU_STENCIL_FRAME"
        ] else {
            throw XCTSkip("Set HKV_MENU_STENCIL_FRAME to audit an external frame")
        }
        let frameURL = URL(fileURLWithPath: path)
        let image = try XCTUnwrap(ImageFileIO.load(frameURL))
        let excludesAuditedFrame = ProcessInfo.processInfo.environment[
            "HKV_MENU_STENCIL_EXCLUDE_AUDITED_CAPTURE"
        ] == "1"
        let captureOverride = excludesAuditedFrame
            ? try MenuStencilLiveCaptureStore().load().filter {
                $0.imageURL.standardizedFileURL != frameURL.standardizedFileURL
            }
            : nil
        let fullCatalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: ProcessInfo.processInfo.environment[
                "HKV_MENU_STENCIL_HUMAN_ONLY"
            ] == "1" ? nil : .loadBundled(),
            liveCapturesOverride: captureOverride
        )
        let expectedContext = ProcessInfo.processInfo.environment[
            "HKV_MENU_STENCIL_EXPECTED_CONTEXT"
        ].flatMap(LabelingContext.init(storageIdentifier:))
        let catalog = expectedContext.map(fullCatalog.containingOnly) ?? fullCatalog
        let tracker = MenuStencilTracker(catalog: catalog)
        if let expectedContext,
           let prime = try MenuStencilLiveCaptureStore().load().first(where: {
               $0.capture.contextIdentifier == expectedContext.storageIdentifier
           }),
           let primeImage = ImageFileIO.load(prime.imageURL) {
            _ = tracker.observe(primeImage, timestamp: 1)
        }
        let result = try XCTUnwrap(tracker.observe(
            image,
            timestamp: 2
        ))
        print(
            "EXTERNAL_MENU_STENCIL context=\(result.context.storageIdentifier) "
                + "match=\(result.isMatch) confidence=\(result.confidence) "
                + "selected=\(result.selectedOption ?? "nil") anchors="
                + "\(result.anchors.map { ($0.name, $0.confidence) }) selectors="
                + "\(result.selectorCandidates.map { ($0.name, $0.confidence) })"
        )
        if let expected = expectedContext?.storageIdentifier {
            XCTAssertTrue(result.isMatch)
            XCTAssertEqual(result.context.storageIdentifier, expected)
        }
    }

    func testTrackerReacquiresOnlyAfterTrackedProbeDisappearsWhenRequested() throws {
        guard let examplesRoot else {
            throw XCTSkip("Set HKV_MENU_STENCIL_AUDIT to the examples directory")
        }
        let store = LabelingExampleStore(rootURL: examplesRoot)
        let catalog = try MenuStencilCatalog(examplesRootURL: examplesRoot)
        let examples = try store.loadExamples()
        let options = try XCTUnwrap(examples.first {
            $0.manifest.contextIdentifier == LabelingContext.options.storageIdentifier
        })
        let audio = try XCTUnwrap(examples.first {
            $0.manifest.contextIdentifier == LabelingContext.audio.storageIdentifier
        })
        let optionsImage = try store.loadImage(for: options)
        let audioImage = try store.loadImage(for: audio)
        let tracker = MenuStencilTracker(catalog: catalog)
        let acquired = try XCTUnwrap(tracker.observe(optionsImage, timestamp: 1))
        let tracked = try XCTUnwrap(tracker.observe(optionsImage, timestamp: 2))
        let grace = try XCTUnwrap(tracker.observe(audioImage, timestamp: 3))
        let reacquired = try XCTUnwrap(tracker.observe(audioImage, timestamp: 4))

        XCTAssertEqual(acquired.context, .options)
        XCTAssertEqual(tracked.context, .options)
        XCTAssertEqual(grace.context, .options)
        XCTAssertEqual(reacquired.context, .audio)
        XCTAssertEqual(reacquired.phase, .searching)
        XCTAssertLessThan(tracked.comparisonCount, reacquired.comparisonCount)
        XCTAssertTrue(reacquired.anchors.allSatisfy { $0.reference?.isValid == true })
        XCTAssertFalse(reacquired.selectorSearchRegions.isEmpty)
        let solvedSearches = reacquired.selectorSearchRegions.filter(\.isSolved)
        XCTAssertFalse(solvedSearches.isEmpty)
        XCTAssertEqual(
            Set(solvedSearches.map(\.optionName)),
            Set([try XCTUnwrap(reacquired.selectedOption)])
        )
        XCTAssertTrue(solvedSearches.contains { $0.side == .left })
        XCTAssertTrue(solvedSearches.contains { $0.side == .right })
        let overlay = try XCTUnwrap(MenuStencilRenderer.overlay(
            reacquired,
            width: audioImage.width,
            height: audioImage.height
        ))
        if let path = ProcessInfo.processInfo.environment[
            "HKV_MENU_STENCIL_OVERLAY_PATH"
        ] {
            XCTAssertTrue(ImageFileIO.writePNG(
                overlay,
                to: URL(fileURLWithPath: path)
            ))
        }
    }

    func testTrackedSceneYieldsToStrongerValidatedSceneProbe() throws {
        let videoAdvancedID = UUID(
            uuidString: "26797919-3AFE-4C8A-9C8A-93EF6FBF3417"
        )!
        let gameOptionsID = UUID(
            uuidString: "6C7B6F5F-5B71-46AB-B8E1-65CF08BBBE1A"
        )!
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let videoAdvanced = captures.first(where: {
            $0.capture.id == videoAdvancedID
        }), let gameOptions = captures.first(where: {
            $0.capture.id == gameOptionsID
        }) else {
            throw XCTSkip("Cross-scene live regression captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let tracker = MenuStencilTracker(catalog: catalog)
        let videoAdvancedImage = try XCTUnwrap(
            ImageFileIO.load(videoAdvanced.imageURL)
        )
        let gameOptionsImage = try XCTUnwrap(
            ImageFileIO.load(gameOptions.imageURL)
        )

        let primed = try XCTUnwrap(tracker.observe(
            videoAdvancedImage,
            timestamp: 1
        ))
        XCTAssertEqual(primed.context, .videoAdvancedSettings)

        var result: MenuStencilResult?
        for frame in 1...MenuStencilTracker.validationInterval {
            result = tracker.observe(
                gameOptionsImage,
                timestamp: 1 + Double(frame) / 60
            )
        }
        let released = try XCTUnwrap(result)
        XCTAssertTrue(released.isMatch)
        XCTAssertEqual(released.context, .gameOptions)
        XCTAssertEqual(released.selectedOption, "Language")
    }

    func testTrackedSceneCheckpointReleasesAcrossWeakProbeScreens() throws {
        let cases: [(UUID, LabelingContext, UUID, LabelingContext, String)] = [
            (
                UUID(uuidString: "AF81A715-EFB2-443F-960A-B98700581647")!,
                .gameOptions,
                UUID(uuidString: "8560A12C-6D21-4C09-9173-2546A5BBF66C")!,
                .options,
                "Game"
            ),
            (
                UUID(uuidString: "A1E776E4-FE62-4B09-A691-1C70F4C98EC9")!,
                .options,
                UUID(uuidString: "F78FC289-5517-4190-B1F3-734DE76F3F24")!,
                .audio,
                "Master Volume"
            ),
            (
                UUID(uuidString: "82BBAADE-8D85-4234-B7E2-78FED96FB415")!,
                .options,
                UUID(uuidString: "A422BC1D-B429-4181-9F0A-70E8FA97E151")!,
                .video,
                "Resolution"
            ),
            (
                UUID(uuidString: "5A6BD765-512D-44D0-97C9-629D8AD605AE")!,
                .controller,
                UUID(uuidString: "5CAFC0C3-413B-45FA-B896-A994693D613B")!,
                .remapController,
                "Done"
            ),
            (
                UUID(uuidString: "A05F46CE-4852-4862-8A20-178CF4503BD2")!,
                .controllerAdvancedSettings,
                UUID(uuidString: "C99245E7-D952-4409-8745-7A979E2403CA")!,
                .keyboard,
                "Back"
            ),
            (
                UUID(uuidString: "A339AD39-39D5-4419-8042-226ACE228078")!,
                .videoAdvancedSettings,
                UUID(uuidString: "59429608-C1EF-48FC-B1C5-2B8EDCC6E11E")!,
                .controller,
                "Remap Controlls"
            ),
            (
                UUID(uuidString: "CF5D5BAA-67F9-4273-BF97-315036F56BD9")!,
                .controllerAdvancedSettings,
                UUID(uuidString: "4DE9B45C-3CDF-4BD0-BBD5-80783EA55050")!,
                .selectProfile,
                "1."
            ),
            (
                UUID(uuidString: "B64A498A-D03A-40C4-9CFD-9BBA2969E1A0")!,
                .mainTitle,
                UUID(uuidString: "823FB4A4-76AB-4E1F-92B3-8D635210D027")!,
                .achievements,
                "Back"
            ),
        ]
        let captures = try MenuStencilLiveCaptureStore().load()
        let byID = Dictionary(uniqueKeysWithValues: captures.map {
            ($0.capture.id, $0)
        })
        let requestedTargets = ProcessInfo.processInfo.environment[
            "HKV_MENU_CHECKPOINT_TARGETS"
        ].map {
            Set($0.split(separator: ",").compactMap {
                UUID(uuidString: String($0))
            })
        }
        let auditedCases = requestedTargets.map { targets in
            cases.filter { targets.contains($0.2) }
        } ?? cases
        guard !auditedCases.isEmpty,
              auditedCases.allSatisfy({ byID[$0.0] != nil && byID[$0.2] != nil })
        else {
            throw XCTSkip("Weak-probe transition captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )

        for (primeID, primeContext, targetID, targetContext, targetSelection) in auditedCases {
            let prime = try XCTUnwrap(byID[primeID])
            let target = try XCTUnwrap(byID[targetID])
            let primeImage = try XCTUnwrap(ImageFileIO.load(prime.imageURL))
            let targetImage = try XCTUnwrap(ImageFileIO.load(target.imageURL))
            let tracker = MenuStencilTracker(catalog: catalog)
            let primed = try XCTUnwrap(tracker.observe(primeImage, timestamp: 1))
            XCTAssertEqual(primed.context, primeContext, primeID.uuidString)

            var result: MenuStencilResult?
            for frame in 1...MenuStencilTracker.validationInterval {
                result = tracker.observe(
                    targetImage,
                    timestamp: 1 + Double(frame) / 60
                )
            }
            let released = try XCTUnwrap(result)
            XCTAssertTrue(released.isMatch, targetID.uuidString)
            XCTAssertEqual(released.context, targetContext, targetID.uuidString)
            XCTAssertEqual(
                released.selectedOption,
                targetSelection,
                targetID.uuidString
            )
        }
    }

    func testLostSceneReacquiresUsingTrackedLanguageWithinGrace() throws {
        let optionsID = UUID(
            uuidString: "C12C4637-6B37-48B9-B633-7EC5A5B1F144"
        )!
        let audioID = UUID(
            uuidString: "D705CBDB-01F4-4FA7-8345-527F1656B3D5"
        )!
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let options = captures.first(where: {
            $0.capture.id == optionsID
        }), let audio = captures.first(where: {
            $0.capture.id == audioID
        }) else {
            throw XCTSkip("Known-language reacquisition captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let tracker = MenuStencilTracker(catalog: catalog)
        let optionsImage = try XCTUnwrap(ImageFileIO.load(options.imageURL))
        let audioImage = try XCTUnwrap(ImageFileIO.load(audio.imageURL))

        let primed = try XCTUnwrap(tracker.observe(optionsImage, timestamp: 1))
        XCTAssertEqual(primed.context, .options)
        _ = tracker.observe(audioImage, timestamp: 2)
        let reacquired = try XCTUnwrap(tracker.observe(audioImage, timestamp: 3))

        XCTAssertTrue(reacquired.isMatch)
        XCTAssertEqual(reacquired.context, .audio)
        XCTAssertEqual(reacquired.selectedOption, "Master Volume")
        print(
            "KNOWN_LANGUAGE_REACQUIRE comparisons="
                + "\(reacquired.comparisonCount)"
        )
        XCTAssertLessThan(reacquired.comparisonCount, 150_000)
    }

    func testCaptureResetPreservesLanguageForNextScene() throws {
        let optionsID = UUID(
            uuidString: "C12C4637-6B37-48B9-B633-7EC5A5B1F144"
        )!
        let audioID = UUID(
            uuidString: "D705CBDB-01F4-4FA7-8345-527F1656B3D5"
        )!
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let options = captures.first(where: {
            $0.capture.id == optionsID
        }), let audio = captures.first(where: {
            $0.capture.id == audioID
        }) else {
            throw XCTSkip("Known-language reset captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let tracker = MenuStencilTracker(catalog: catalog)
        let optionsImage = try XCTUnwrap(ImageFileIO.load(options.imageURL))
        let audioImage = try XCTUnwrap(ImageFileIO.load(audio.imageURL))

        let primed = try XCTUnwrap(tracker.observe(optionsImage, timestamp: 1))
        XCTAssertEqual(primed.languageIdentifier, "en")
        tracker.reset(preservingLanguage: true)
        let reacquired = try XCTUnwrap(tracker.observe(audioImage, timestamp: 2))

        XCTAssertTrue(reacquired.isMatch)
        XCTAssertEqual(reacquired.context, .audio)
        XCTAssertEqual(reacquired.languageIdentifier, "en")
        XCTAssertLessThan(reacquired.comparisonCount, 150_000)
    }

    func testFailedTransitionFrameDoesNotDiscardTrackedLanguage() throws {
        let optionsID = UUID(
            uuidString: "C12C4637-6B37-48B9-B633-7EC5A5B1F144"
        )!
        let audioID = UUID(
            uuidString: "D705CBDB-01F4-4FA7-8345-527F1656B3D5"
        )!
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let options = captures.first(where: {
            $0.capture.id == optionsID
        }), let audio = captures.first(where: {
            $0.capture.id == audioID
        }) else {
            throw XCTSkip("Known-language transition captures are unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let tracker = MenuStencilTracker(catalog: catalog)
        let optionsImage = try XCTUnwrap(ImageFileIO.load(options.imageURL))
        let audioImage = try XCTUnwrap(ImageFileIO.load(audio.imageURL))
        let blankImage = try XCTUnwrap(Self.blankMenuFrame())

        let primed = try XCTUnwrap(tracker.observe(optionsImage, timestamp: 1))
        XCTAssertEqual(primed.languageIdentifier, "en")
        _ = tracker.observe(blankImage, timestamp: 2)
        let failed = tracker.observe(blankImage, timestamp: 3)
        XCTAssertNotEqual(failed?.isMatch, true)
        let reacquired = try XCTUnwrap(tracker.observe(audioImage, timestamp: 4))

        XCTAssertTrue(reacquired.isMatch)
        XCTAssertEqual(reacquired.context, .audio)
        XCTAssertEqual(reacquired.languageIdentifier, "en")
        XCTAssertLessThan(reacquired.comparisonCount, 150_000)
    }

    func testLanguageChangeReleasesTrackedForeignAdvancedScene() throws {
        let quitID = UUID(
            uuidString: "47C2E923-6E64-4217-8620-1CEF34B81231"
        )!
        let italianLanguageID = UUID(
            uuidString: "B642B8C7-BCAF-4E85-BC84-EA6395579B82"
        )!
        let japaneseLanguageID = UUID(
            uuidString: "00920E73-0796-4D32-A91F-EA789887333C"
        )!
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let prime = captures.first(where: {
            $0.capture.contextIdentifier
                == LabelingContext.videoAdvancedSettings.storageIdentifier
                && $0.capture.resolvedLanguageIdentifier == "zh-Hans"
                && $0.capture.selectedIdentifier
                    == "video-advanced.particle-effects"
        }), let quit = captures.first(where: {
            $0.capture.id == quitID
        }), let italianLanguage = captures.first(where: {
            $0.capture.id == italianLanguageID
        }), let japaneseLanguage = captures.first(where: {
            $0.capture.id == japaneseLanguageID
        }) else {
            throw XCTSkip("Live language-change regression captures unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            includedContexts: [
                .videoAdvancedSettings, .quitGame, .quitToMenu, .gameOptions,
            ]
        )
        let tracker = MenuStencilTracker(catalog: catalog)
        let primeImage = try XCTUnwrap(ImageFileIO.load(prime.imageURL))
        let quitImage = try XCTUnwrap(ImageFileIO.load(quit.imageURL))
        let italianImage = try XCTUnwrap(ImageFileIO.load(italianLanguage.imageURL))

        let primed = try XCTUnwrap(tracker.observe(primeImage, timestamp: 1))
        XCTAssertEqual(primed.context, .videoAdvancedSettings)
        for frame in 1...16 {
            _ = tracker.observe(
                quitImage,
                timestamp: 1 + Double(frame) / 30
            )
        }
        var result: MenuStencilResult?
        for frame in 1...24 {
            result = tracker.observe(
                italianImage,
                timestamp: 2 + Double(frame) / 30
            )
        }

        let released = try XCTUnwrap(result)
        XCTAssertTrue(released.isMatch)
        XCTAssertEqual(released.context, .gameOptions)
        XCTAssertEqual(released.selectedOption, "Language")
        XCTAssertEqual(released.languageIdentifier, "it")

        let japaneseTracker = MenuStencilTracker(catalog: catalog)
        _ = japaneseTracker.observe(primeImage, timestamp: 1)
        let japaneseImage = try XCTUnwrap(ImageFileIO.load(
            japaneseLanguage.imageURL
        ))
        var japaneseResult: MenuStencilResult?
        for frame in 1...24 {
            japaneseResult = japaneseTracker.observe(
                japaneseImage,
                timestamp: 2 + Double(frame) / 30
            )
        }
        let japaneseReleased = try XCTUnwrap(japaneseResult)
        XCTAssertTrue(japaneseReleased.isMatch)
        XCTAssertEqual(japaneseReleased.context, .gameOptions)
        XCTAssertEqual(japaneseReleased.selectedOption, "Language")
        XCTAssertEqual(japaneseReleased.languageIdentifier, "ja")

    }

    func testItalianOptionsWinsFullCatalog() throws {
        let captureID = UUID(
            uuidString: "DA25905D-4E14-41D7-9B38-4471B78492EB"
        )!
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let source = captures.first(where: {
            $0.capture.id == captureID
        }) else {
            throw XCTSkip("Italian Options regression capture unavailable")
        }
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration()
        )
        let image = try XCTUnwrap(ImageFileIO.load(source.imageURL))
        let result = try XCTUnwrap(
            MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
        )

        XCTAssertTrue(result.isMatch)
        XCTAssertEqual(result.context, .options)
        XCTAssertEqual(result.selectedOption, "Game")
        XCTAssertEqual(result.languageIdentifier, "it")
    }

    func testVideoAdvancedReleasesToParentMenusAcrossLiveFrames() throws {
        let primeID = UUID(
            uuidString: "F5C494F9-BC35-4638-824A-8CD0E58E20ED"
        )!
        let targets: [(UUID, LabelingContext, String)] = [
            (
                UUID(uuidString: "74D0B4A0-47F6-4228-A9EC-023BBCACA063")!,
                .options,
                "Back"
            ),
            (
                UUID(uuidString: "E731F8C6-0323-4059-B103-5FB828CFEA88")!,
                .gameOptions,
                "Language"
            ),
        ]
        let captures = try MenuStencilLiveCaptureStore().load()
        guard let prime = captures.first(where: { $0.capture.id == primeID }),
              targets.allSatisfy({ target in
                  captures.contains { $0.capture.id == target.0 }
              })
        else {
            throw XCTSkip("Latest live transition captures are unavailable")
        }
        let targetIDs = Set(targets.map(\.0))
        let catalog = try MenuStencilCatalog(
            examplesRootURL: LabelingExampleStore.defaultRootURL(),
            calibration: try bundledCalibration(),
            liveCapturesOverride: captures.filter {
                !targetIDs.contains($0.capture.id)
            }
        )
        let primeImage = try XCTUnwrap(ImageFileIO.load(prime.imageURL))

        for (targetID, expectedContext, expectedSelection) in targets {
            let target = try XCTUnwrap(captures.first {
                $0.capture.id == targetID
            })
            let targetImage = try XCTUnwrap(ImageFileIO.load(target.imageURL))
            let tracker = MenuStencilTracker(catalog: catalog)
            let primed = try XCTUnwrap(tracker.observe(primeImage, timestamp: 1))
            XCTAssertEqual(primed.context, .videoAdvancedSettings)
            var result: MenuStencilResult?
            for frame in 1...16 {
                result = tracker.observe(
                    targetImage,
                    timestamp: 1 + Double(frame) / 60
                )
            }
            let released = try XCTUnwrap(result)
            XCTAssertTrue(released.isMatch, targetID.uuidString)
            XCTAssertEqual(released.context, expectedContext, targetID.uuidString)
            XCTAssertEqual(
                released.selectedOption,
                expectedSelection,
                targetID.uuidString
            )
        }
    }

    private func expectedSelection(
        for example: SavedLabelingExample,
        contextIdentifier: String
    ) -> String? {
        guard let context = LabelingContext(storageIdentifier: contextIdentifier) else {
            return nil
        }
        let selectors = example.manifest.annotations.filter {
            !$0.isHardNegative
                && LabelingClassIdentity.matches(
                    $0.classIdentifier,
                    LabelingClassIdentity.selectDecoration
                )
        }
        guard selectors.count >= 2 else { return nil }
        let selectorY = selectors.map { $0.y + $0.height / 2 }.reduce(0, +)
            / Double(selectors.count)
        let selectorX = selectors.map { $0.x + $0.width / 2 }.sorted()
        let leftX = selectorX.first!
        let rightX = selectorX.last!
        let nearest = example.manifest.annotations.filter {
            !$0.isHardNegative
                && !LabelingClassIdentity.matches(
                    $0.classIdentifier,
                    LabelingClassIdentity.selectDecoration
                )
                && $0.width * $0.height <= 5_000.0 / Double(
                    example.manifest.imageWidth * example.manifest.imageHeight
                )
                && $0.x > leftX
                && $0.x + $0.width < rightX
        }.min {
            abs($0.y + $0.height / 2 - selectorY)
                < abs($1.y + $1.height / 2 - selectorY)
        }
        guard let nearest else { return nil }
        return context.labels.first {
            LabelingClassIdentity.matches($0.id, nearest.classIdentifier)
        }?.name
    }

    private static func selectorRecalibrationCatalog() -> MenuStencilCatalog {
        let variant = selectorRecalibrationVariant()
        let probe = MenuStencilCatalog.Anchor(
            classIdentifier: "fixture-probe",
            name: "Fixture Probe",
            bounds: CGRect(x: 2, y: 2, width: 4, height: 4),
            calibratedBounds: nil,
            variants: [variant],
            probe: variant
        )
        let option = MenuStencilCatalog.Option(
            classIdentifier: "fixture-option",
            name: "Fixture Option",
            bounds: CGRect(x: 22, y: 20, width: 24, height: 4),
            selectorGeometry: nil,
            leftSelector: MenuStencilCatalog.PositionedSelector(
                bounds: CGRect(x: 10, y: 20, width: 4, height: 4),
                variants: [variant]
            ),
            rightSelector: MenuStencilCatalog.PositionedSelector(
                bounds: CGRect(x: 48, y: 20, width: 4, height: 4),
                variants: [variant]
            )
        )
        let scene = MenuStencilCatalog.Scene(
            context: .mainTitle,
            referenceWidth: 72,
            referenceHeight: 40,
            probe: probe,
            anchors: [probe],
            options: [option],
            selectorVariants: [:],
            selectorGeometry: nil
        )
        return MenuStencilCatalog(
            scenes: [scene],
            referenceWidth: 72,
            referenceHeight: 40,
            pixelBand: 0..<40
        )
    }

    private static func selectorRecalibrationVariant() -> MenuStencilCatalog.Variant {
        let values: [UInt8] = [
            238, 86, 214, 104,
            72, 226, 98, 242,
            206, 116, 232, 80,
            94, 244, 122, 218,
        ]
        let rgb = Data(values.flatMap { [$0, $0, $0] })
        let reference = ImageStencilReference(width: 4, height: 4, rgb: rgb)
        return MenuStencilCatalog.Variant(
            kernel: ImageStencilKernel(
                kind: "fixture-selector",
                bounds: CGRect(x: 0, y: 0, width: 4, height: 4),
                rgb: rgb
            ),
            reference: reference
        )
    }

    private static func selectorRecalibrationImage(
        left: CGPoint,
        right: CGPoint
    ) -> CGImage? {
        let width = 72
        let height = 40
        var rgba = [UInt8](repeating: 255, count: width * height * 4)
        for pixel in 0..<(width * height) {
            rgba[pixel * 4] = 12
            rgba[pixel * 4 + 1] = 24
            rgba[pixel * 4 + 2] = 62
        }
        let values: [UInt8] = [
            238, 86, 214, 104,
            72, 226, 98, 242,
            206, 116, 232, 80,
            94, 244, 122, 218,
        ]
        func draw(at origin: CGPoint) {
            for y in 0..<4 {
                for x in 0..<4 {
                    let index = (
                        (Int(origin.y) + y) * width + Int(origin.x) + x
                    ) * 4
                    let value = values[y * 4 + x]
                    rgba[index] = value
                    rgba[index + 1] = value
                    rgba[index + 2] = value
                }
            }
        }
        draw(at: CGPoint(x: 2, y: 2))
        draw(at: left)
        draw(at: right)
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else {
            return nil
        }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
            ),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

    private static func blankMenuFrame() -> CGImage? {
        let width = 640
        let height = 360
        var rgba = [UInt8](repeating: 0, count: width * height * 4)
        for pixel in 0..<(width * height) {
            rgba[pixel * 4 + 3] = 255
        }
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else {
            return nil
        }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
            ),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

    private static func consensusFixture(
        shift: CGPoint,
        textValue: UInt8,
        noise: CGPoint
    ) -> CGImage? {
        let width = 32
        let height = 18
        var rgba = [UInt8](repeating: 255, count: width * height * 4)
        for pixel in 0..<(width * height) {
            rgba[pixel * 4] = 12
            rgba[pixel * 4 + 1] = 24
            rgba[pixel * 4 + 2] = 62
        }
        let glyph = (0..<5).flatMap { [(0, $0), (1, $0)] }
            + (0..<7).flatMap { [($0, 2), ($0, 3)] }
        for (x, y) in glyph {
            let targetX = 10 + x + Int(shift.x)
            let targetY = 5 + y + Int(shift.y)
            let pixel = (targetY * width + targetX) * 4
            rgba[pixel] = textValue
            rgba[pixel + 1] = textValue
            rgba[pixel + 2] = textValue
        }
        let noiseIndex = (Int(noise.y) * width + Int(noise.x)) * 4
        rgba[noiseIndex] = 240
        rgba[noiseIndex + 1] = 240
        rgba[noiseIndex + 2] = 240
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else { return nil }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
            ),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

    private static func selectorContrastFixture(showsCursor: Bool) -> CGImage? {
        let width = 20
        let height = 12
        var rgba = [UInt8](repeating: 255, count: width * height * 4)
        for pixel in 0..<(width * height) {
            rgba[pixel * 4] = 12
            rgba[pixel * 4 + 1] = 24
            rgba[pixel * 4 + 2] = 62
        }
        func drawSquare(x: Int, y: Int) {
            for row in y..<(y + 3) {
                for column in x..<(x + 3) {
                    let index = (row * width + column) * 4
                    rgba[index] = 230
                    rgba[index + 1] = 230
                    rgba[index + 2] = 230
                }
            }
        }
        drawSquare(x: 4, y: 4)
        if showsCursor { drawSquare(x: 10, y: 4) }
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else {
            return nil
        }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
            ),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }
}
