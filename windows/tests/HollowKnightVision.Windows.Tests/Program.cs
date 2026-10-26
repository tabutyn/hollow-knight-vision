using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Host;

if (await TrainingProcessTests.TryRunProbeAsync(args)) return;
if (args.Contains("--gpu-only"))
{
    GroundComputeProcessingTests.TestCudaPatchParityAndReuse();
    GroundComputeProcessingTests.TestCudaFallbackAndInvalidInputs();
    GroundComputeProcessingTests.TestCudaConcurrentSearches();
    GroundComputeProcessingTests.TestCudaSolverAcrossMotionSequence();
    GroundComputeProcessingTests.TestWiderParallelSearchRecoversMotionOutsideFormerRadius();
    GroundComputeProcessingTests.TestReleaseParallelSearchCapacityBenchmark();
    Console.WriteLine("GPU tracking regressions passed: 6");
    return;
}
if (args is ["--export-dataset-probe", var probeRoot])
{
    Console.WriteLine(DatasetExporterTests.CreateTrainerFixture(probeRoot));
    return;
}

var tests = new List<(string Name, Action Run)>
{
    ("direct ground recovery uses four current visible cells", VisualGroundIntegrationTests.TestDirectRecoveryUsesFourCurrentVisibleCells),
    ("direct ground recovery rejects occluded and unrelated pixels", VisualGroundIntegrationTests.TestDirectRecoveryRejectsOccludedAndUnrelatedCells),
    ("atlas New retains previous autosave while Delete discards it", AtlasResetTransactionTests.TestNewRetainsPreviousButDeleteDiscardsIt),
    ("failed autosave Delete rolls back without creating a named copy", AtlasResetTransactionTests.TestFailedDeleteRestoresPreviousAutosave),
    ("capture normalization preserves bilinear pixels and frame ownership", CaptureNormalizationTests.TestPixelsMatchBilinearReference),
    ("CUDA ground patch scores match CPU and reuse resized buffers", GroundComputeProcessingTests.TestCudaPatchParityAndReuse),
    ("CUDA unavailable and invalid inputs fall back safely", GroundComputeProcessingTests.TestCudaFallbackAndInvalidInputs),
    ("CUDA concurrent searches do not share mutable results", GroundComputeProcessingTests.TestCudaConcurrentSearches),
    ("CUDA solver preserves motion and pose admission", GroundComputeProcessingTests.TestCudaSolverAcrossMotionSequence),
    ("ground parallel image processing matches CPU exactly", GroundComputeProcessingTests.TestParallelGroundImageProcessingMatchesCpuExactly),
    ("ground parallel patch search matches CPU selection", GroundComputeProcessingTests.TestParallelPatchSearchMatchesCpuSelection),
    ("ground widened search recovers motion outside former radius", GroundComputeProcessingTests.TestWiderParallelSearchRecoversMotionOutsideFormerRadius),
    ("ground parallel solver matches CPU across motion sequence", GroundComputeProcessingTests.TestParallelSolverMatchesCpuAcrossMotionSequence),
    ("ground release parallel search capacity benchmark", GroundComputeProcessingTests.TestReleaseParallelSearchCapacityBenchmark),
    ("ground release parallel image benchmark", GroundComputeProcessingTests.TestReleaseParallelGroundImageBenchmark),
    ("ground hypothesis overlay omits a duplicate live ground line", GroundHypothesisLocalParityTests.TestFeatureOverlayDoesNotDrawASecondLiveGroundLine),
    ("ground hypothesis overlay omits persistent atlas lines", GroundHypothesisLocalParityTests.TestFeatureOverlayDoesNotPaintPersistentAtlasLineIntoLiveRaster),
    ("ground hypothesis overlay draws an exact rectangular cell", GroundHypothesisLocalParityTests.TestFeatureOverlayDrawsRectangularGroundCell),
    ("ground hypothesis RANSAC selects dominant motion", GroundHypothesisLocalParityTests.TestRANSACSelectsDominantGroundMotionAndRejectsDepthOutliers),
    ("ground hypothesis tracker tiles and solves translation", GroundHypothesisLocalParityTests.TestTrackerTilesEverySixteenPixelsAndSolvesFrameTranslation),
    ("ground hypothesis tracked texture stabilizes row jitter", GroundHypothesisLocalParityTests.TestTrackedTextureStabilizesOnePixelDetectorRowJitter),
    ("ground hypothesis lifecycle fills newly visible area", GroundHypothesisLocalParityTests.TestLifecycleAddsUnlimitedTilesToFillNewlyVisibleScreenArea),
    ("ground hypothesis clean lines have no 32-feature cap", GroundHypothesisLocalParityTests.TestEveryCleanLineIsFullyTiledWithoutThirtyTwoFeatureLimit),
    ("ground hypothesis permanent segment keeps its phase", GroundHypothesisLocalParityTests.TestPermanentSegmentDoesNotRephaseWhenDetectedEndpointJitters),
    ("ground hypothesis sequence keeps occluded cells", GroundHypothesisLocalParityTests.TestPermanentSequenceKeepsOrangeCellsAcrossOcclusion),
    ("ground hypothesis interior occlusion persists in atlas", GroundHypothesisLocalParityTests.TestInteriorOccludedCellsRemainInPersistentLineLattice),
    ("ground hypothesis protected cells remain unobserved", GroundHypothesisLocalParityTests.TestKnightCoveredCellsAreUnobservedInsteadOfRejected),
    ("ground hypothesis unverified featureless frame preserves atlas patches", GroundHypothesisLocalParityTests.TestUnverifiedFeaturelessFrameCannotOverwriteAtlasPatches),
    ("ground hypothesis camera-consistent pixels gain opacity", GroundHypothesisLocalParityTests.TestCameraConsistentPixelsGainOpacity),
    ("ground hypothesis unsupported line expires atomically", GroundHypothesisLocalParityTests.TestEntireUnsupportedLineExpiresTogether),
    ("ground hypothesis short platform requires camera-consistent motion", GroundHypothesisLocalParityTests.TestStableShortPlatformNeedsCameraConsistentMotionBeforeAtlasAdmission),
    ("ground hypothesis merged segments rebuild one sequence", GroundHypothesisLocalParityTests.TestMergingSegmentsRebuildsOneOrderedSequence),
    ("ground hypothesis mature globals survive segment merge", GroundHypothesisLocalParityTests.TestMatureGlobalFeaturesReindexWhenSegmentsMerge),
    ("ground hypothesis disconnected segments share global phase", GroundHypothesisLocalParityTests.TestDisconnectedSegmentsShareGlobalPhaseThenMergeIntoOneRectifiedGrid),
    ("ground hypothesis featureless tiles never enter global set", GroundHypothesisLocalParityTests.TestFeaturelessTilesNeverEnterGlobalSet),
    ("ground hypothesis return view corrects global pose", GroundHypothesisLocalParityTests.TestReturnViewReacquiresPersistentFeaturesAndCorrectsGlobalPose),
    ("ground hypothesis background recovery survives reset", GroundHypothesisLocalParityTests.TestBackgroundRecoveryConfirmsReturnAndSurvivesReset),
    ("ground hypothesis background recovery survives reanchors", GroundHypothesisLocalParityTests.TestBackgroundRecoverySurvivesRepeatedFloorlessReanchors),
    ("ground hypothesis delayed recovery follows continuous camera motion", GroundHypothesisLocalParityTests.TestDelayedBackgroundRecoveryFollowsContinuousCameraMotion),
    ("ground hypothesis return search uses fragmented-floor texture", GroundHypothesisLocalParityTests.TestReturnSearchUsesVisibleTextureBelowFragmentedFloorEdge),
    ("ground hypothesis return search ignores lattice phase", GroundHypothesisLocalParityTests.TestGlobalReturnSearchIsIndependentOfSixteenPixelLatticePhase),
    ("ground hypothesis return search tolerates tone change", GroundHypothesisLocalParityTests.TestGlobalReturnSearchHandlesScreenSpaceToneChange),
    ("ground hypothesis one-frame extension does not grow persistent sequence", GroundHypothesisLocalParityTests.TestOneFrameExtensionDoesNotGrowPersistentSequence),
    ("ground hypothesis partial visibility keeps complete lattice", GroundHypothesisLocalParityTests.TestPartiallyVisibleLineKeepsCompleteLattice),
    ("ground hypothesis rejected line removes persistent sequence", GroundHypothesisLocalParityTests.TestRejectedLineRemovesItsEntirePersistentSequence),
    ("ground hypothesis rejected line retains hidden loop closure evidence", GroundHypothesisLocalParityTests.TestRejectedLineKeepsHiddenLoopClosureEvidence),
    ("ground hypothesis persistent line immediately corrects vertical pose", GroundHypothesisLocalParityTests.TestPersistentLineImmediatelyCorrectsVerticalCameraPose),
    ("ground hypothesis atlas line survives vertical round trip", GroundHypothesisLocalParityTests.TestPersistentAtlasLineDoesNotMoveDuringVerticalCameraRoundTrip),
    ("ground hypothesis line identity survives large vertical look", GroundHypothesisLocalParityTests.TestGroundOnlyTrackingKeepsLineIDAcrossLargeVerticalLook),
    ("ground hypothesis featureless vertical look holds pose", GroundHypothesisLocalParityTests.TestFeaturelessVerticalLookMustHoldPose),
    ("ground hypothesis unresolved look cannot duplicate line", GroundHypothesisLocalParityTests.TestUnresolvedVerticalLookCannotCreateOrDeleteDuplicateLineOne),
    ("ground hypothesis measured fallback seeds current capture", GroundHypothesisLocalParityTests.TestMeasuredFallbackSeedsCurrentCaptureWithoutLosingNextMotion),
    ("ground hypothesis measured fallback defines initial epoch", GroundHypothesisLocalParityTests.TestMeasuredFallbackDefinesEpochBeforeAnyGroundIsEstablished),
    ("ground hypothesis entering floor waits for complete moving patches", GroundHypothesisLocalParityTests.TestNewFloorEnteringWithoutCompletePatchEventuallyPassesMotionChecks),
    ("ground hypothesis provisional continuity expires after texture miss", GroundHypothesisLocalParityTests.TestProvisionalContinuitySurvivesBriefTextureMissThenExpires),
    ("ground hypothesis deferred placement preserves atlas evidence", GroundHypothesisLocalParityTests.TestDeferredPlacementKeepsAtlasEvidenceAndRecoversAgainstTrustedPixels),
    ("ground bootstrap ignores an untrusted first view", GroundBootstrapMaskCohortParityTests.TestUntrustedFirstViewCannotPreventStableNewGroundFromInitializing),
    ("ground bootstrap preserves established map against unrelated pixels", GroundBootstrapMaskCohortParityTests.TestEstablishedMapSurvivesUnrelatedPixelsWithoutReseedingThem),
    ("ground bootstrap rejects continually changing images", GroundBootstrapMaskCohortParityTests.TestContinuallyChangingBootstrapImagesCannotInventConfirmedGround),
    ("ground mask recovery waits for visible pixels", GroundBootstrapMaskCohortParityTests.TestFullyMaskedRecoveryWaitsForVisiblePixelsWithoutDiscardingGround),
    ("ground sparse recovery reports pixel cost", GroundBootstrapMaskCohortParityTests.TestSparseVisibleRecoveryReportsPixelCostInsteadOfZeroSpatialResidual),
    ("ground partial mask retains reference pixels and identity", GroundBootstrapMaskCohortParityTests.TestPartialMaskRetainsReferencePixelsAndFeatureIdentity),
    ("ground first view recovers after later template change", GroundBootstrapMaskCohortParityTests.TestOlderViewRecoversDespiteLaterChangedGroundTemplates),
    ("ground unrelated view cannot match frozen cohort", GroundBootstrapMaskCohortParityTests.TestUnrelatedViewCannotUseTheFrozenPopulationAsAFalseMatch),
    ("ground scene recorded return relocalizes from drifted pose", GroundSceneAuditParityTests.TestRecordedReturnFrameRelocalizesFromDriftedPose),
    ("ground scene labeled gameplay reports detection cost and evidence", GroundSceneAuditParityTests.TestLabeledGameplayGroundDetectionCostAndEvidence),
    ("ground scene captured platform audit runs masked and unmasked", GroundSceneAuditParityTests.TestCapturedPlatformScene),
    ("ground scene audit converts coordinates and alpha", GroundSceneAuditParityTests.TestSceneAuditCoordinateAndAlphaConversion),
    ("dashboard model workspace retains tab and row selection", DashboardPresentationParityTests.TestModelWorkspaceSelectionRetainsTabAndRowsAcrossViewRecreation),
    ("dashboard menu particles spawn below and recycle upward", DashboardPresentationParityTests.TestMenuObjectParticlesSpawnAtBottomAndRecycleUpward),
    ("dashboard status reports only recognized context and room", DashboardPresentationParityTests.TestBottomStatusReportsOnlyRecognizedContext),
    ("dashboard ground hypotheses produce review geometry only", DashboardPresentationParityTests.TestGroundHypothesesProduceOnlyGroundReviewGeometry),
    ("dashboard persistent clean lines stay in atlas coordinates", DashboardPresentationParityTests.TestPersistentCleanLineStaysAtFixedAtlasCoordinates),
    ("dashboard transition portal produces doors and connector", DashboardPresentationParityTests.TestTransitionPortalProducesTwoDoorsAndConnector),
    ("dashboard tile identity retains signed high ID bits", DashboardPresentationParityTests.TestTileUUIDRetainsHighBitsOfTileID),
    ("ground texture ReferencePromotionRequiresCleanSparseOrBroadMatch", GroundCameraSolverTests.TestReferencePromotion),
    ("ground texture SparseHighCostPoseIsRejectedBeforeItCanJump", GroundCameraSolverTests.TestPoseAcceptance),
    ("ground texture SparseHighCostMotionInnovationIsRejected", GroundCameraSolverTests.TestInnovation),
    ("ground texture ProvisionalPromotionRequiresSustainedStrongTexture", GroundCameraSolverTests.TestProvisionalPromotion),
    ("ground texture RecentReferenceCadenceAvoidsPerFrameQuantization", GroundCameraSolverTests.TestRecentCadence),
    ("ground texture ImageMotionScaleChangesCoordinateWithoutChangingRawMatch", GroundCameraSolverTests.TestMotionScale),
    ("ground texture StationaryRefreshNearOldAnchorDoesNotInventMotion", GroundCameraSolverTests.TestStationaryRefresh),
    ("ground texture StrongRecentStripOverridesWrongPhaseInWeakOldAnchor", GroundCameraSolverTests.TestStrongerRecentStrip),
    ("ground texture RejectedFrameFallbackTracksProvisionallyUntilTrustedAnchorReturns", GroundCameraSolverTests.TestProvisionalRecovery),
    ("ground texture SmallStaticDistractorCannotBeatSeveralMovingPlatforms", GroundCameraSolverTests.TestStaticDistractor),
    ("ground texture GlobalCorrectionReanchorsCurrentFrameWithoutNextFrameSnapBack", GroundCameraSolverTests.TestGlobalCorrection),
    ("ground texture RepeatedCaptureThenHorizontalAccelerationRemainsInSearch", GroundCameraSolverTests.TestRepeatedHorizontalAcceleration),
    ("ground texture HorizontalKeyframeHandoffsReturnToOriginWithoutVerticalDrift", GroundCameraSolverTests.TestHorizontalHandoffs),
    ("ground texture VerticalKeyframeHandoffsFollowLongPanAndReturn", GroundCameraSolverTests.TestVerticalHandoffs),
    ("ground texture OddHorizontalPhaseAndVerticalRoundTrip", GroundCameraSolverTests.TestOddPhaseRoundTrip),
    ("ground texture OnePixelDetectorJitterDoesNotMoveWorld", GroundCameraSolverTests.TestOnePixelJitter),
    ("ground texture BriefEdgeDropoutStillRequiresTextureVerification", GroundCameraSolverTests.TestEdgeDropout),
    ("ground texture FeatureRecoveryRefreshesFrozenLocalAnchor", GroundCameraSolverTests.TestFeatureRecovery),
    ("ground texture ThreePixelEdgeJitterKeepsStationaryTextureAtZero", GroundCameraSolverTests.TestThreePixelJitter),
    ("ground texture RepeatedFrameThenFastVerticalPanAndReturn", GroundCameraSolverTests.TestRepeatedVerticalPan),
    ("ground texture TwentyRoundTripsHaveNoAccumulatedDrift", GroundCameraSolverTests.TestTwentyRoundTrips),
    ("ground texture PatchScoreHandlesBrightnessAndContrastButRejectsUnrelatedTexture", GroundCameraSolverTests.TestPatchScore),
    ("ground texture WrongParallelRowCannotMoveCameraWithoutMatchingPixels", GroundCameraSolverTests.TestWrongParallelRow),
    ("ground texture MatchingDistantPlatformCannotTeleportOneFrameCameraPose", GroundCameraSolverTests.TestDistantPlatform),
    ("ground texture ReleaseTimingAtCaptureResolution", GroundCameraSolverTests.TestReleaseTiming),
    ("subpixel RecoversFractionalTranslationOnIndependentValidationPixels", GroundSubpixelTests.TestFractionalTranslation),
    ("subpixel IntegratedSolverPublishesFractionalTranslation", GroundSubpixelTests.TestIntegratedFractionalSolve),
    ("subpixel ExactIntegerMotionStaysExactAndFlatFrameIsRejected", GroundSubpixelTests.TestIntegerAndFlat),
    ("ground texture snapshots own capture bytes and preserve observed evidence islands", GroundCameraSolverTests.TestOwnedEvidenceAndMasks),
    ("ground texture fallback and rejected promotions retain reference trust", GroundCameraSolverTests.TestRollbackAndTrust),
    ("ground texture resize and invalid clocks cannot revive stale references", GroundCameraSolverTests.TestResizeClockAndReset),
    ("ground presence waits for elapsed evidence before confirmation", GroundLinePresenceTests.TestThreeFramesAreNotEnoughAndTwoSecondsConfirmStableLine),
    ("ground presence classifies strict majorities and waits on ties", GroundLinePresenceTests.TestAboveHalfConfirmsBelowHalfRejectsAndExactlyHalfWaits),
    ("ground presence rejected lines can recover from verified evidence", GroundLinePresenceTests.TestRejectedLineRecoversAfterEnoughVerifiedDetections),
    ("ground presence ignores occluded offscreen unverified and long gaps", GroundLinePresenceTests.TestOcclusionOffscreenUnverifiedAndLongGapDoNotCountAsMisses),
    ("ground presence votes measure observable time rather than frames", GroundLinePresenceTests.TestVotesMeasureTimeNotFrameCount),
    ("ground presence hides stale rejected projections under confirmed floor", GroundLinePresenceTests.TestConfirmedLineHidesOverlappingStaleRejectedProjection),
    ("ground feature review renders only confirmed presence", GroundLinePresenceTests.TestFeatureHypothesesRenderOnlyConfirmedLines),
    ("ground surface strong core retains weak endpoints", GroundSurfaceEvidenceTests.TestStrongCoreKeepsWeakEndpointsButWeakOnlyLineIsRejected),
    ("ground surface fragments cannot borrow support across a step", GroundSurfaceEvidenceTests.TestSplitFragmentCannotBorrowBodySupportAcrossStep),
    ("ground surface bottom band keeps clipped floor and rejects decoration", GroundSurfaceEvidenceTests.TestVisibleBottomBandRejectsDecorationAndKeepsClippedFloor),
    ("ground surface rejected decoration cannot suppress nearby floor", GroundSurfaceEvidenceTests.TestRejectedDecorationCannotSuppressNearbyRealFloor),
    ("ground surface dark gaps cannot approve bright edges", GroundSurfaceEvidenceTests.TestEvidenceScoringDoesNotLetDarkGapApproveBrightEdge),
    ("ground surface masked pixels do not change body support", GroundSurfaceEvidenceTests.TestMaskedPixelsDoNotChangeSupportedBodyVote),
    ("ground labels reject bright decoration and keep dark platform body", TestSurfaceSupportRejectsBrightDecorationAndKeepsDarkPlatformBody),
    ("ground labels score one-to-one overlap coverage", TestCoverageIsOneToOneAndOverlappingLabelsDoNotDoubleCount),
    ("ground labels count vertical tolerance endpoints misses and extras", TestVerticalToleranceAndHorizontalEndpointsCountMissesAndExtras),
    ("ground labels ignore regions remove truth and predictions", TestIgnoreRegionsRemoveBothTruthAndPredictions),
    ("ground labels partial frames cannot claim precision", TestPartialFramesCannotClaimPrecisionOrFalsePositives),
    ("ground labels reviewed empty frames are negative evidence", TestFullyReviewedEmptyFrameIsNegativeEvidenceNotPerfectRecall),
    ("ground labels direct evidence excludes inferred bridges", TestEvidenceCoverageDoesNotCreditInferredBridges),
    ("ground labels deduplicate identical predictions", TestDuplicateIdenticalPredictionsAreDeduplicated),
    ("ground labels reject split group and image leakage", TestSplitValidationRejectsGroupAndDuplicateImageLeakage),
    ("ground labels preserve zoom coordinates and reject letterbox", TestCoordinatesStayExactAtZoomAndRejectLetterboxOutsideImage),
    ("ground labels save reopen and detect source hash mismatch", TestSaveReopenAndSourceHashMismatch),
    ("ground labels reject invalid geometry and traversal", TestInvalidGeometryAndTraversalRejected),
    ("ground labels inherit existing group split", TestNewCaptureInExistingCheckGroupInheritsSplit),
    ("ground labels human ignores never change detector input", TestHumanIgnoreRegionsNeverChangeDetectorInput),
    ("ground labels unreviewed data cannot tune or generalize", TestUnreviewedLabelsExcludedFromTuningAndDraftDatasetNotGeneralization),
    ("ground labels Check data cannot choose parameters", TestCheckLabelsCannotChooseParameters),
    ("global correction rejects four wrong votes", GroundGlobalCorrectionPolicyTests.TestFourWrongVotesCannotOverrideWideStrip),
    ("global correction rejects repeated-texture phase jumps", GroundGlobalCorrectionPolicyTests.TestSimilarRepeatedTextureDoesNotAuthorizePhaseJump),
    ("global correction accepts a better verified strip", GroundGlobalCorrectionPolicyTests.TestBetterVerifiedStripCanCorrectDriftAndRecoverLostAnchor),
    ("global correction permits measurable small refinements", GroundGlobalCorrectionPolicyTests.TestSmallUnambiguousRefinementNeedsOnlyMeasurableImprovement),
    ("global correction accepts occluded wide-inlier loop closure", GroundGlobalCorrectionPolicyTests.TestSmallOccludedLoopClosureUsesWideInlierAdvantage),
    ("global correction closes from an unsupported drifted pose", GroundGlobalCorrectionPolicyTests.TestStrongGlobalStripClosesWhenDriftedLocalPoseHasNoSupport),
    ("pose correction returns exact timestamp samples", PoseCorrectionInterpolationTests.TestExactTimestampReturnsMatchingCorrection),
    ("pose correction interpolates and refines midpoint poses", PoseCorrectionInterpolationTests.TestMidpointLinearlyInterpolatesCorrectionAndRefinesRawPose),
    ("pose correction holds visit endpoints", PoseCorrectionInterpolationTests.TestOutsideIntervalUsesNearestEndpointWithinVisit),
    ("pose correction isolates visits and empty identity", PoseCorrectionInterpolationTests.TestVisitIsolationAndEmptyVisitReturnIdentity),
    ("pose correction ignores invalid inputs", PoseCorrectionInterpolationTests.TestInvalidInputsAreIgnored),
    ("pose correction ordering is deterministic", PoseCorrectionInterpolationTests.TestOrderingIsDeterministicIncludingSameTimestamp),
    ("world basis fast loss advances epoch without erasure", WorldBasisControllerTests.TestFastLossAdvancesEpochWithoutErasingWorldBasis),
    ("world basis rejects delayed old-epoch correction", WorldBasisControllerTests.TestDelayedCorrectionIsRejectedAfterLocalEpochChanges),
    ("world basis rebases queued same-epoch observations", WorldBasisControllerTests.TestQueuedSameEpochObservationRebasesThroughCorrectedBasis),
    ("world basis generation reset rejects old tickets", WorldBasisControllerTests.TestGenerationResetRejectsOldTickets),
    ("world basis historical corrections remap latest pose", WorldBasisControllerTests.TestHistoricalCorrectionUsesMatchedPoseAndRemapsLatestPose),
    ("live-world pipeline orders append ingest CAS publication and uses monotonic time", TestPipelineOrdersTransactionAndUsesMonotonicTimestamp),
    ("live-world pipeline gates unverified new worlds and reference-free loss", TestPipelineSuppressesUnverifiedNewWorldAndLossWithoutReference),
    ("live-world pipeline preserves ground-anchored capture pose across basis revisions", TestGroundAnchoredObservationKeepsCapturePoseAcrossLaterBasisRevision),
    ("live-world pipeline propagates ground-alignment exclusions", TestAtlasWorldTrackerReceivesGroundAlignmentExclusions),
    ("live-world pipeline append failure never falls back to transient state", TestPersistentAppendFailureDoesNotFallBackToTransientAtlasOrTracker),
    ("live-world pipeline corrupt persistent world blocks atlas", TestCorruptPersistentWorldBlocksAtlasInsteadOfFallingBackToMemory),
    ("live-world pipeline discards delayed world timestamp without rewinding visit", TestDelayedWorldTimestampIsDiscardedWithoutRewindingTrackerVisit),
    ("live-world pipeline recovery remains transient until a normal frame", TestRecoverySearchIsTransientAndReturnsConfirmedWorldPlacement),
    ("live-world pipeline enforces bounded persistence cadence", TestPipelineCadenceSuppressesDuplicatePersistence),
    ("live-world pipeline rejects stale capture generations", TestPipelineRejectsStaleCaptureGeneration),
    ("live-world pipeline rolls back stale CAS and suppresses publication", TestPipelineRollsBackAfterCasFailureAndSuppressesPublication),
    ("live-world pipeline rebuilds closures before commit and publication", TestPipelinePublishesClosureOnlyAfterRebuildAndCommit),
    ("LiveAtlasPipelineTests.testLiveRoomBoundaryAuditWhenRequested", TestLiveRoomBoundaryAuditWhenRequested),
    ("live room boundary audit selects chronological persisted evidence", TestLiveRoomBoundaryAuditSelectsChronologicalPersistedEvidence),
    ("feature correction rebases queued work and applies once", FeatureCorrectionPolicyTests.TestSlowCorrectionRebasesNewerQueuedWorkAndAppliesOnce),
    ("feature correction reset rejects slow old work", FeatureCorrectionPolicyTests.TestGenerationResetRejectsSlowOldCorrection),
    ("feature correction changes generation only after mutation", FeatureCorrectionPolicyTests.TestGenerationTransitionWaitsForAcceptedAccumulatorMutation),
    ("input player test state normalizes and randomizes", InputBridgeParityTests.TestPlayerTestStateNormalizesBoundsAndRandomizesValidValues),
    ("input player Ops wire round trips", InputBridgeParityTests.TestPlayerOpsWireRoundTrip),
    ("input snapshot wire codec preserves versioned state", InputBridgeParityTests.TestWireCodecRoundTripsVersionedSnapshot),
    ("input checkpoint control messages round trip", InputBridgeParityTests.TestCheckpointControlMessagesRoundTrip),
    ("input ground truth telemetry provides projection scale", InputBridgeParityTests.TestGroundTruthTelemetryDecodesAndProvidesProjectionScale),
    ("input player pose playback preserves recorded truth", InputBridgeParityTests.TestPlayerPosePlaybackEncodesRecordedGroundTruth),
    ("hacker atlas synchronizer requires matching Unity frame in either arrival order", TestSynchronizerRequiresMatchingUnityFrameInEitherArrivalOrder),
    ("hacker atlas direct camera placement uses independent XY projection scale", TestDirectCameraPlacementUsesIndependentXYProjectionScale),
    ("hacker atlas room layout keeps tree doorways rectilinear with a gap", TestRoomLayoutKeepsTreeDoorwaysRectilinearWithAGap),
    ("hacker atlas room layout leaves contradictory loop freeform", TestRoomLayoutLeavesContradictoryLoopAsFreeformConnection),
    ("hacker atlas room layout fans overlapping branch into a rectified island", TestRoomLayoutFansOverlappingBranchIntoNewRectifiedIsland),
    ("input acknowledgement measures only state changes", InputAcknowledgementTrackerTests.TestAcknowledgementTrackerMeasuresOnlyMatchedStateChanges),
    ("input acknowledgement rejects mismatch and stale identity", InputAcknowledgementTrackerTests.TestAcknowledgementTrackerRejectsMismatchAndOldOrUnknownAcknowledgements),
    ("input acknowledgement bounds pending state and calculates p95", InputAcknowledgementTrackerTests.TestAcknowledgementTrackerBoundsPendingStatesAndCalculatesRollingP95),
    ("input replay refreshes receiver lease during long holds", InputPathRecorderTests.TestPlaybackMaintainsLeaseAcrossLongHolds),
    ("input path records deduplicated transitions and closes held buttons", InputPathRecorderTests.TestRecordsDeduplicatedTransitionsAndClosesHeldButtons),
    ("input path reads recordings without capture diagnostics", InputPathRecorderTests.TestPathWithoutCaptureDiagnosticsRemainsReadable),
    ("input path caps low-resolution frame traces at twenty hertz", InputPathRecorderTests.TestLowResolutionFrameTraceIsCappedAtTwentyHertz),
    ("input path assigns reliability only to exact completed ground solves", InputPathRecorderTests.TestLowResolutionReliabilityUsesOnlyExactCompletedGroundSolve),
    ("input path reads coarse motion without place-match fields", InputPathRecorderTests.TestCoarseMotionWithoutPlaceMatchFieldsRemainsReadable),
    ("input path records unverified tracking for diagnosis", InputPathRecorderTests.TestRecordsUnverifiedTrackingForLaterFailureAnalysis),
    ("input path assigns atlas admission to the exact capture", InputPathRecorderTests.TestAtlasAdmissionBelongsToExactCaptureAndOlderSamplesRemainReadable),
    ("input path store round trips replay-ready paths", InputPathRecorderTests.TestStoreRoundTripsReplayReadyPath),
    ("input path recorded buttons round trip bridge bits", InputPathRecorderTests.TestRecordedButtonsRoundTripBridgeBits),
    ("input path playback validation allows balanced overlapping keys", InputPathRecorderTests.TestPlaybackValidationAllowsOverlappingBalancedKeys),
    ("input path playback validation accepts recorded world poses", InputPathRecorderTests.TestPlaybackValidationAcceptsRecordedWorldPoseTrace),
    ("input path playback validation accepts hero-less cutscenes", InputPathRecorderTests.TestPlaybackValidationAcceptsPlayableRoomsAcrossHeroLessCutscene),
    ("input path playback command accepts safe bounded requests", InputPathRecorderTests.TestPlaybackCommandAcceptsOnlySavedFileNamesAndBoundedIteration),
    ("low-resolution trace evaluates exact-frame motion and place recognition", TestEvaluatesExactFrameMotionAndPlaceRecognition),
    ("low-resolution trace isolates camera warps and scene epochs", TestClassifiesCameraWarpOutsideSearchAndDoesNotCrossSceneEpoch),
    ("low-resolution trace applies production direction gate", TestEvaluatorAppliesProductionDirectionGate),
    ("low-resolution trace records experimental motion threshold", TestEvaluatorRecordsExperimentalMotionThreshold),
    ("low-resolution trace reports hidden rejected travel by cause", TestRejectedMotionReportsHiddenHackerTravelByCause),
    ("low-resolution trace keeps connected baseline across scene join", TestConnectedCameraKeepsOneBaselineAcrossSceneJoin),
    ("low-resolution trace falls back to historical published tracking", TestConnectedCameraUsesHistoricalPublishedTrackingWhenCoarseTraceIsAbsent),
    ("low-resolution trace excludes invalid payload without losing count", TestInvalidPayloadIsExcludedWithoutLosingTraceCount),
    ("low-resolution trace reconstructs persisted world evidence window", TestReconstructsTraceFromPersistedWorldEvidenceWindow),
    ("input physical reconciliation preserves unobserved local press", InputBridgeStateTests.TestPhysicalReconciliationDoesNotCancelAVisibleLocalPressWhenGlobalPollingIsUnavailable),
    ("input physical reconciliation releases only observed key", InputBridgeStateTests.TestPhysicalReconciliationReleasesOnlyAfterObservingTheHeldKey),
    ("input sender emits ordered complete transitions", InputBridgeStateTests.TestSenderEmitsOrderedCompleteTransitionsAndNeutralRelease),
    ("input fresh press enables with held key", InputBridgeStateTests.TestFreshPressEnablesAndHoldsInOneSnapshot),
    ("semantic atlas approves only matching stable ground", GroundSemanticAtlasTests.TestSemanticEvidenceApprovesOnlyMatchingStableLineAndPersists),
    ("semantic atlas transfers approval by matching geometry", GroundSemanticAtlasTests.TestReassociatedIDInheritsApprovalFromSameGeometry),
    ("tracking descent requires independent texture support", GroundTrackingEdgeParityTests.TestFastDescentOntoNewPlatformUsesIndependentTextureSupport),
    ("tracking offscreen lines retain evidence without misses", GroundTrackingEdgeParityTests.TestLineIndexPreservesOffscreenEvidenceWithoutCountingMisses),
    ("tracking capture gaps require bounded broad evidence", GroundTrackingEdgeParityTests.TestMeasuredCaptureGapRequiresBroadPreciseBoundedEvidence),
    ("tracking retained pixels confirm recovery beyond held pose", GroundTrackingEdgeParityTests.TestRetainedPixelMotionCanConfirmRecoveryBeyondHeldPose),
    ("ground long rows recover precise sudden landings", GroundLongRowParityTests.TestSuddenLandingShiftRequiresBroadPreciseTexture),
    ("ground long rows reject unsafe alternatives", GroundLongRowParityTests.TestLongRowRejectsWrongTextureWrongRowNarrowMaskedAndOversized),
    ("ground broad texture recovers noisy landing", GroundLongRowParityTests.TestManyIndependentPatchesRecoverModeratelyNoisyLanding),
    ("ground repeated texture cannot choose an ambiguous phase", GroundLongRowParityTests.TestRepeatedTextureCannotChooseAnAmbiguousLongRowPhase),
    ("ground edge masks match the pixel oracle", GroundDetectionEfficiencyTests.TestRasterizedMasksArePixelExactIncludingClippedAndOffscreenRectangles),
    ("ground paired kernel matches the pixel oracle", GroundDetectionEfficiencyTests.TestPairedKernelIsPixelExactForNoiseAndBorders),
    ("ground optimized edge and kernel match at capture resolution", GroundDetectionEfficiencyTests.TestReleaseEdgeAndKernelTimingAtCaptureResolution),
    ("ground shared theory preserves lines, detection, and debug pixels", TestSharedLineAnalysisPreservesLinesDebugPixelsAndReferenceDetection),
    ("ground refinement finds sub-cell XY offset", GroundPoseRefinementSearchTests.TestFindsSubCellXYOffset),
    ("ground refinement rejects repeated-texture ambiguity", GroundPoseRefinementSearchTests.TestRepeatedTextureCannotAuthorizePoseChange),
    ("ground refinement rejects small occluded consensus", GroundPoseRefinementSearchTests.TestSmallOccludedConsensusIsRejected),
    ("broad recent five precise patches override repeated old tiles", BroadRecentConsensusParityTests.TestFivePreciseRecentPatchesOverrideFourRepeatedOldTiles),
    ("broad recent equal support stays anchored", BroadRecentConsensusParityTests.TestEquallySupportedPreciseRecentSurfaceWinsAndStaysAnchored),
    ("broad recent support still requires precision freshness and width", BroadRecentConsensusParityTests.TestEqualRecentSupportStillRequiresPrecisionFreshnessAndFivePatches),
    ("broad recent noisy surface overrides sparse old phase", BroadRecentConsensusParityTests.TestBroadNoisyRecentSurfaceOverridesSparseOldPhaseAndStaysAnchored),
    ("broad recent evidence must be fresh and clearly better", BroadRecentConsensusParityTests.TestBroadRecentEvidenceMustBeFreshAndClearlyBetter),
    ("floorless selector rejects stale coarse reversal", LiveTrackingTimingParityTests.TestFloorlessSelectorRejectsStaleCoarsePoseAfterDirectionReversal),
    ("floorless selector uses larger agreeing capture step", LiveTrackingTimingParityTests.TestFloorlessSelectorUsesLargerAgreeingCaptureRateStep),
    ("floorless selector preserves stop and vertical travel", LiveTrackingTimingParityTests.TestFloorlessSelectorKeepsMaskedHorizontalStopAndCoarseVerticalTravel),
    ("floorless selector accepts absolute place match", LiveTrackingTimingParityTests.TestFloorlessSelectorAlwaysAcceptsAbsolutePlaceMatch),
    ("ground motion evidence rejects coordinate-only seed", LiveTrackingTimingParityTests.TestGroundMotionEvidenceRejectsCoordinateSeedWithoutPixelMeasurement),
    ("presentation prediction advances caps and expires", LiveTrackingTimingParityTests.TestPredictionAdvancesCurrentFrameButIsBoundedAndExpires),
    ("presentation velocity rejects corrections and losses", LiveTrackingTimingParityTests.TestCorrectionAndLossNeverBecomePresentationVelocity),
    ("capture ground reliability requires fresh exact state", LiveTrackingTimingParityTests.TestCaptureGroundReliabilityRequiresFreshExactTrackingState),
    ("tentative transition preserves handoff origin", LiveTrackingTimingParityTests.TestTentativeTransitionPreservesVerifiedHandoffOrigin),
    ("floorless handoff advances delayed pose", LiveTrackingTimingParityTests.TestFloorlessHandoffAdvancesDelayedPoseFromRetainedGroundVelocity),
    ("floorless handoff expires and caps travel", LiveTrackingTimingParityTests.TestFloorlessHandoffExpiresAndCapsTravel),
    ("playback truth joins scenes and reuses anchors", PlaybackDivergenceMonitorTests.TestConnectedTruthJoinsNewSceneAtKnightDoorwayAndReusesKnownAnchor),
    ("playback truth retains known scene without hero", PlaybackDivergenceMonitorTests.TestConnectedTruthKeepsKnownSceneCameraWhileHeroUnavailable),
    ("playback truth cannot initialize scene without hero", PlaybackDivergenceMonitorTests.TestConnectedTruthCannotInitializeNewSceneWhileHeroUnavailable),
    ("playback divergence needs sustained exact-frame error", PlaybackDivergenceMonitorTests.TestDivergenceRequiresSustainedExactFrameErrorAfterGrace),
    ("playback aligned motion remains valid", PlaybackDivergenceMonitorTests.TestAlignedMotionDoesNotTriggerDivergence),
    ("playback subthreshold offset remains valid", PlaybackDivergenceMonitorTests.TestSubthresholdOffsetDoesNotAbortPlayback),
    ("playback transitions reset divergence evidence", PlaybackDivergenceMonitorTests.TestTransitionFramesResetDivergenceEvidence),
    ("playback evaluation drops endpoint anchor", PlaybackDivergenceMonitorTests.TestEvaluationDiscardsPreviousEndpointSceneAnchor),
    ("playback new-scene divergence uses short grace", PlaybackDivergenceMonitorTests.TestNewSceneDivergenceStopsSoonAfterShortGrace),
    ("playback moving truth detects vision silence", PlaybackDivergenceMonitorTests.TestMovingHackerTruthAbortsWhenVisionStopsPublishing),
    ("playback stationary truth tolerates vision silence", PlaybackDivergenceMonitorTests.TestStationaryHackerTruthDoesNotTreatMissingVisionAsDivergence),
    ("playback exact vision pair resets silence watchdog", PlaybackDivergenceMonitorTests.TestExactVisionPairResetsMissingVisionWatchdog),
    ("playback scene change rebases silence watchdog", PlaybackDivergenceMonitorTests.TestMissingVisionWatchdogRebasesAtSceneChange),
    ("early ground splits bridged floor around a lower surface", GroundEarlyRecognitionTests.TestLowerSurfaceSplitsBridgeAndRejectsDecorativeEdges),
    ("early ground persistent upper ends survive known lower floor", TestPersistentUpperEndsDoNotMergeAcrossKnownLowerFloor),
    ("early ground bridges ordinary occlusion without lower floor", GroundEarlyRecognitionTests.TestOrdinaryOcclusionStillBridgesWithoutLowerSurfaceEvidence),
    ("early ground confirms quickly and survives occlusion", GroundEarlyRecognitionTests.TestShortObservationAdmitsNewGroundAndPreservesItAcrossOcclusion),
    ("early ground grace survives brief misses then rejects absence", GroundEarlyRecognitionTests.TestConfirmedGroundSurvivesBriefMissButRejectsPersistentAbsence),
    ("bounded search does not queue and rejects invalidated results", BoundedBackgroundSearchTests.TestBusySearchDoesNotQueueAndInvalidatedResultCannotPublish),
    ("background correction requires independent consistent observations", BoundedBackgroundSearchTests.TestMaterialCorrectionRequiresIndependentConsistentObservations),
    ("busy background search never prepares rejected snapshots", BoundedBackgroundSearchTests.TestBusySearchNeverPreparesRejectedSnapshots),
    ("failed snapshot preparation releases worker slot", BoundedBackgroundSearchTests.TestSnapshotFailureReleasesWorkerSlot),
    ("busy ground history allocations stay bounded", GroundHistoryPerformanceTests.TestBusyHistoryDoesNotAllocatePerStoredFeature),
    ("ground snapshots freeze metadata and evidence", GroundHistoryPerformanceTests.TestSnapshotMetadataAndEvidenceRemainFrozen),
    ("vector ground descriptor scores equal scalar scores", GroundHistoryPerformanceTests.TestVectorDescriptorDistanceMatchesScalarExactly),
    ("background correction ignores the camera noise floor", BoundedBackgroundSearchTests.TestCorrectionNoiseFloorNeverMovesCamera),
    ("background correction requires close agreement for mid-sized motion", BoundedBackgroundSearchTests.TestMidSizedCorrectionsMustAgreeClosely),
    ("frame telemetry ring retains newest samples for p95", FramePipelineTelemetryTests.TestBoundedRingRetainsNewestSamplesForP95),
    ("frame telemetry rejects negative and nonfinite durations", FramePipelineTelemetryTests.TestRejectsNegativeAndNonFiniteDurations),
    ("live world round trip and optimization preserve raw evidence", LiveWorldModelParityTests.TestRoundTripAndPoseReplacementPreserveRawEvidence),
    ("live world legacy observations preserve raw pose in epoch zero", LiveWorldModelParityTests.TestLegacyObservationDecodesIntoEpochZeroWithoutMovingItsWorldPose),
    ("live world invalid models are rejected", LiveWorldModelParityTests.TestInvalidModelsAreRejected),
    ("live world rejects stale and incomplete pose replacement", LiveWorldModelParityTests.TestStaleOrIncompleteReplacementIsRejected),
    ("live world room translation moves only selected optimized evidence", LiveWorldModelParityTests.TestRoomTranslationMovesOnlySelectedRoomOptimizedEvidence),
    ("live world session restart preserves descriptors and optimized poses", TestAppendSnapshotAndRestartPreserveDescriptorsAndOptimizedPoses),
    ("live world session rejects stale disk revision", TestStaleDiskRevisionIsRejected),
    ("live world session room translation preserves manifest evidence", TestRoomPlacementTranslationPreservesManifestEvidenceAndCommits),
    ("live world session surfaces corrupt snapshot without overwrite", TestCorruptSnapshotIsSurfacedWithoutOverwrite),
    ("live world session archive preserves old world and resets fresh", TestArchivePreservesOldWorldAndFreshStoreIsEmpty),
    ("live world session failed reset restores original over partial root", TestFailedFreshStoreInitializationRestoresOriginalWorldOverPartialRoot),
    ("live world session concurrent appends allocate unique durable evidence", TestConcurrentAppendsAllocateUniqueDurableEvidence),
    ("live world session manifest failure rolls back and orphan retry recovers", TestManifestFailureRollsBackAndOrphanRetryRecovers),
    ("live world session concurrent first constructors share one generation", TestConcurrentFirstConstructorsShareOneGeneration),
    ("live world session constructor waits for reset generation", TestConstructorDuringResetOpensFreshGeneration),
    ("live world session stale instances cannot repopulate reset root", TestStaleInstancesCannotRepopulateResetRoot),
    ("live world session missing evidence cannot be committed", TestMissingEvidenceCannotBeCommitted),
    ("live world session rollback failure preserves recovery archive", TestRollbackFailurePreservesRecoveryArchive),
    ("live world session manifest failures are typed on every operation", TestManifestInvariantFailuresAreTyped),
    ("live world session root junction cannot rebase pinned generation", TestRootJunctionSwapCannotRebasePinnedGeneration),
    ("live world session malformed stores surface typed errors", TestMalformedStoreFilesSurfaceTypedErrors),
    ("live world session second manifest read remains typed", TestManifestMutationBetweenValidationAndOpenRemainsTyped),
    ("scene session simultaneous appends use the fresh manifest", TestDirectSceneSessionSimultaneousAppendsUseFreshManifest),
    ("scene session observation overflow writes no orphan", TestDirectSceneSessionOverflowWritesNoOrphan),
    ("scene session manifest failure rolls back evidence", TestDirectSceneSessionManifestFailureRollsBackEvidence),
    ("live world session reparse swaps cannot redirect handle reads or writes", TestLiveWorldSessionReparseSwapCannotRedirectHandleReadsOrWrites),
    ("live world session owned staging recovers without deleting unrelated files", TestOwnedStagingRecoversAndPreservesUnrelatedFiles),
    ("live world session durable publication does not overwrite and retries", TestDurablePublicationNoOverwriteFailureIsRetryable),
    ("live world tracker room boundaries isolate motion and appearance", TestRoomBoundaryPreventsCrossRoomMotionAndAppearanceEdges),
    ("live world tracker confirms outward loop and distributes drift", TestOutwardLoopNeedsTwoConfirmationsThenClosesAndDistributesDrift),
    ("live world tracker rejects one-frame and unrelated closure evidence", TestOneFrameCandidateDoesNotCloseAndUnrelatedFramesAreRejected),
    ("live world tracker reload relocalizes against saved landmarks", TestReloadedSnapshotRelocalizesAgainstSavedLandmarks),
    ("live world tracker new visit searches saved features without motion edge", TestReloadedVisitSearchesSavedFeaturesImmediatelyWithoutCrossVisitMotionEdge),
    ("live world tracker confirms correction while camera moves", TestConfirmationComparesDriftWhileCameraContinuesMoving),
    ("live world tracker rejects recent overlapping closure", TestRecentOverlapIsNotTreatedAsClosure),
    ("live world tracker closure requires another excursion", TestAcceptedClosureRequiresAnotherExcursionBeforeAddingMoreEdges),
    ("live world tracker rejects repeated appearance", TestRepeatedAppearanceIsRejectedAsAClosureCandidate),
    ("live world recovery reuses a closed reference without persistence", TestRecoveryReusesClosedReferenceWithoutPersistingFrames),
    ("live world recovery confirms across reference keyframes", TestRecoveryConfirmsAcrossDifferentReferenceKeyframes),
    ("live world recovery keeps tentative match across one miss", TestRecoveryKeepsTentativeMatchAcrossOneUnmatchedFrame),
    ("live world recovery expires tentative match after long gap", TestRecoveryExpiresTentativeMatchAfterLongUnmatchedGap),
    ("live world tracker motion edges retain raw coordinates", TestMotionEdgesUseRawCoordinatesAfterPoseOptimization),
    ("live world tracker motion edges never cross local epochs", TestMotionEdgesNeverBridgeLocalTrackingEpochs),
    ("live world replay three returning passes close after disk reopen", TestThreeReturningPassesCloseAfterReopeningWorldFromDisk),
    ("live world replay command arguments write report", TestCommandArgumentsWriteReport),
    ("live world replay returning route is explicit", TestReturningRouteMustBeExplicitOnCommandLine),
    ("live world replay serialized snapshot relocalizes PNG evidence", TestSerializedSnapshotRelocalizesStoredPNGEvidence),
    ("live world replay nonreturning route needs no closure", TestNonReturningRouteReportsNoClosureRequirement),
    ("live world replay internal revisit keeps one-way topology", TestInternalRevisitDoesNotMakeOneWayRouteAClosedLoop),
    ("live world replay matching endpoints across rooms do not imply loop", TestMatchingEndpointAppearanceAcrossRoomsDoesNotCreateLoopIntent),
    ("live world replay default one-way ignores vision endpoint coordinates", TestDefaultOneWayRouteCannotBeReclassifiedFromVisionCoordinates),
    ("live world replay wide intent bound requires tight optimized success", TestLoopIntentUsesWiderDetectionBoundButMustMeetTightOptimizedBound),
    ("global feature matcher recovers large offset without pose prior", TestRecoversLargeHorizontalAndVerticalOffsetWithoutPosePrior),
    ("global feature matcher rejects repeated placement ambiguity", TestRepeatPatternIsRejectedAsAmbiguous),
    ("global feature matcher excludes HUD evidence", TestHUDExclusionCannotProvideMatch),
    ("global feature matcher rejects unrelated frames", TestUnrelatedFrameIsRejected),
    ("global feature matcher ordering and equal ambiguity are deterministic", TestReferenceOrderingIsDeterministic),
    ("global feature matcher overlapping references corroborate world pose", TestOverlappingReferencesThatAgreeOnWorldPoseReinforceMatch),
    ("global feature matcher shortlist preserves spatial coverage and preferred confirmation", TestSpatiallyDiverseShortlistFindsTargetBeyondSignatureCap),
    ("global feature matcher self-placement search remains bounded", TestSelfPlacementCheckUsesBoundedDescriptorCandidates),
    ("global feature matcher rejects self-placement consensus across histogram boundaries", TestSelfPlacementRejectsConsensusSplitAcrossHistogramBoundary),
    ("global feature matcher current solve width scales recovered pose", TestCurrentSolveWidthUsesCurrentFrameScaleForCamera),
    ("translation pose graph distributes return drift in both axes", TranslationPoseGraphParityTests.TestStrongLoopClosureSmoothlyDistributesOutwardReturnDriftInBothCoordinates),
    ("translation pose graph anchor and ordering are deterministic", TranslationPoseGraphParityTests.TestAnchorRemainsExactAndConstraintOrderDoesNotChangeSolution),
    ("translation pose graph preserves a consistent motion chain", TranslationPoseGraphParityTests.TestConsistentMotionChainNeedsNoLoopClosure),
    ("translation pose graph rejects invalid and disconnected graphs", TranslationPoseGraphParityTests.TestInvalidDanglingDisconnectedAndNonFiniteGraphsAreRejected),
    ("broad ground rebound requires two independent measurements", BroadGroundRecoveryGateTests.TestLandingReboundNeedsTwoBroadIndependentMeasurements),
    ("broad ground recovery rejects unsafe evidence", BroadGroundRecoveryGateTests.TestBroadRecoveryRejectsWeakStaleDuplicateIncoherentOrExcessiveEvidence),
    ("capture restart requires a desired idle pipeline", LiveSessionPolicyTests.TestCaptureRestartRequiresDesiredIdlePipeline),
    ("capture restart cannot revive a stop or race a new capture", LiveSessionPolicyTests.TestCaptureRestartDoesNotReviveIntentionalStopOrRaceNewCapture),
    ("game loss countdown preserves the requested message", LiveSessionPolicyTests.TestGameLossCountdownUsesRequestedMessage),
    ("rendered frame marker round trips and rejects corruption", RenderedFrameMarkerTests.TestPixelMarkerRoundTripAndRejectsCorruption),
    ("rendered frame marker is opt-in and decodes legacy hello", RenderedFrameMarkerTests.TestHelloMarkerIsExplicitlyOptInAndOldHelloStillDecodes),
    ("rendered frame marker decodes from captured pixels", RenderedFrameMarkerTests.TestMarkerDecodesDirectlyFromCapturedImage),
    ("ground feature eligibility requires descriptor squares below support", GroundFeatureEligibilityTests.TestOnlyDescriptorSquaresFullyBelowSupportedSegmentsAreEligible),
    ("ground feature eligibility rejects frames without ground", GroundFeatureEligibilityTests.TestMissingGroundExcludesWholeFrame),
    ("ground feature eligibility separates platform-height bands", GroundFeatureEligibilityTests.TestSeparatePlatformHeightsCreateSeparateEligibleBands),
    ("slow refinement does not block registration publication", FeatureRefinementCoordinatorTests.TestSlowRefinementDoesNotBlockRegistrationPublication),
    ("refinement returns in generation then runs newest work", FeatureRefinementCoordinatorTests.TestSlowCorrectionReturnsInCurrentGenerationThenRunsNewestWork),
    ("refinement rejects out-of-order current-generation results", FeatureRefinementCoordinatorTests.TestOutOfOrderCurrentGenerationResultIsRejected),
    ("refinement generation invalidation rejects completed correction", FeatureRefinementCoordinatorTests.TestGenerationInvalidationRejectsCompletedCorrection),
    ("example deletion touches only the selected owned directory", ExampleStoreTests.TestDeleteOnlySelected),
    ("legacy full-catalog snapshots use actually annotated classes", ExampleStoreTests.TestLegacyKnownCatalogFallback),
    ("hard-negative examples retain their target and geometry", ExampleStoreTests.TestHardNegativeRoundTrip),
    ("example load durably migrates legacy shared decorations", ExampleStoreTests.TestLegacySelectMigration),
    ("legacy game labels split by options context", ExampleStoreTests.TestLegacyGameSplit),
    ("new examples snapshot only introduced and current classes", ExampleStoreTests.TestNewFrameKnownClasses),
    ("training queries include both Ready and Draft examples", ExampleStoreTests.TestTrainingIgnoresCompletion),
    ("unknown empty Ready examples are not training evidence", ExampleStoreTests.TestUnknownEmptyReady),
    ("known-empty examples remain class and context training evidence", ExampleStoreTests.TestKnownEmptyTrainingEvidence),
    ("editing refreshes introduced classes without stale remembered history", ExampleStoreTests.TestUpdateIntroducedClasses),
    ("saved draft boxes standardize reverse drags before validation", ExampleStoreTests.TestStandardizedDrafts),
    ("example JSON uses Mac-readable timestamps and optional fields", ExampleStoreTests.TestPortableManifest),
    ("canonical known snapshots persist without no-op rewrites", ExampleStoreTests.TestCanonicalSnapshotPersistence),
    ("example collisions and failed updates preserve stored data", ExampleStoreTests.TestAtomicFailurePreservation),
    ("dataset catalog preserves canonical menu classes and repeated instances", DatasetExporterTests.TestCatalogRepeatedInstances),
    ("shared dataset combines explicitly labeled objects across contexts", DatasetExporterTests.TestCrossContextExport),
    ("dataset exports eligible known-empty frames with zero registrations", DatasetExporterTests.TestKnownEmptyFrame),
    ("dataset excludes menus that only remember the gameplay catalog", DatasetExporterTests.TestMenuKnownCatalogExclusion),
    ("explicit hard negatives stay in training without counting as registrations", DatasetExporterTests.TestHardNegativeRegistration),
    ("one-instance dataset preserves pixel geometry and preliminary snapshot semantics", DatasetExporterTests.TestSinglePreliminarySnapshot),
    ("capture and duplicate-image groups remain transitively in the same split", DatasetExporterTests.TestCaptureAndDuplicateGroups),
    ("validation assignments survive new screenshots and caller reordering", DatasetExporterTests.TestStableValidationAssignment),
    ("validation ledger repair retains training examples for every positive class", DatasetExporterTests.TestRepairNewClassCoverage),
    ("dataset context gates distinguish explicit boxes from known custom negatives", DatasetExporterTests.TestContextAndCustomAdmission),
    ("dataset JSON uses Swift-compatible UTC dates splits canonical labels and standardized boxes", DatasetExporterTests.TestPortableMetadataAndReversedBoxes),
    ("hard-negative edits override old validation assignments for the entire duplicate group", DatasetExporterTests.TestHardNegativeLedgerOverride),
    ("failed dataset exports preserve committed snapshots and remove only their staging", DatasetExporterTests.TestFailureAtomicity),
    ("invalid or unwritable split history cannot silently reassign screenshots", DatasetExporterTests.TestInvalidLedgerPreservation),
    ("dataset grouping is order-independent and preliminary thresholds match Mac", DatasetExporterTests.TestDeterministicSplitsAndThresholds),
    ("dataset trailing separators cannot relocate persistent split history", DatasetExporterTests.TestTrailingRootLedgerLocation),
    ("training notification tracks only labels newer than the dataset snapshot", TrainingPresentationTests.TestNotificationDates),
    ("shared model uses full training configuration like Mac", TrainingPresentationTests.TestConfiguration),
    ("training notification reads eligible manifest dates and rejects invalid snapshots", TrainingPresentationTests.TestManifestNotification),
    ("training progress and bounded UTF-8 output survive fragmented independent streams", TrainingPresentationTests.TestProgressAndOutput),
    ("training process drains output and tolerates closing observers", TrainingProcessTests.TestSuccessAndObserverFailure),
    ("stop training and launcher exit terminate owned descendants and close pipes", TrainingProcessTests.TestCancellationAndDescendants),
    ("cancelled and failed training artifacts cannot become model candidates", TrainingProcessTests.TestInterruptedOutputIsolation),
    ("cancelling one training job cannot stop an independent run", TrainingProcessTests.TestIndependentJobs),
    ("prediction reviews match classes and expose failure screenshots", ReviewAnalysisTests.TestSummariesByClass),
    ("model priorities rank fewest labels and most errors like Mac", ReviewAnalysisTests.TestPriorityRanking),
    ("failure navigation preserves review order and wraps safely", ReviewAnalysisTests.TestFailureNavigation),
    ("review matching is confidence-ordered one-to-one with explicit thresholds", ReviewAnalysisTests.TestConfidenceOrderedOneToOne),
    ("review aliases reversed geometry and baseline data retain exact semantics", ReviewAnalysisTests.TestCanonicalGeometryAndBaselines),
    ("priority ties negatives canonical duplicates and unlabeled objects are deterministic", ReviewAnalysisTests.TestPriorityTiesAndNegatives),
    ("summary selection survives refreshes while filtering deleted and non-gameplay failures", ReviewAnalysisTests.TestSummarySession),
    ("persisted candidate review loads and failed analysis cannot partially replace state", ReviewAnalysisTests.TestPersistedReviewAndAtomicFailure),
    ("screenshot review filters gameplay and preserves ordered clamped selection", ScreenshotReviewTests.TestSelectionAndOrdering),
    ("contract review persists exemptions and blocks training across all breached screenshots", ScreenshotReviewTests.TestExemptionsAndTraining),
    ("failed or stale exemption writes preserve contract and training state", ScreenshotReviewTests.TestFailedAndStaleExemptionWrites),
    ("confirmed screenshot deletion confines targets and retains failed selections", ScreenshotReviewTests.TestConfirmedDeletionAndFailures),
    ("known-empty and negative labels cannot invent trainable positive examples", ScreenshotReviewTests.TestKnownEmptyNegativeAndCanonicalTraining),
    ("review colors match canonical equal-hue classes and UTF-8 fallback", ScreenshotReviewTests.TestVisualIdentity),
    ("object references use positive boxes in oldest-first order with preferred fallback", ObjectReferenceTests.TestPositiveOrderingAndFallback),
    ("preferred object references persist canonically without unchanged writes", ObjectReferenceTests.TestReferencePreferences),
    ("invalid object preferences recover and failed writes retain storage", ObjectReferenceTests.TestInvalidPreferencesAndWriteFailure),
    ("model catalog retains unlabeled gameplay and custom classes without HUD objects", ObjectReferenceTests.TestModelCatalogIncludesUnlabeledObjects),
    ("shared model owns only dynamic gameplay classes", LabelingSharedModelPolicyTests.TestSharedModelOwnsDynamicGameplayClassesOnly),
    ("labeling catalog retains menu stencil classes", LabelingSharedModelPolicyTests.TestLabelingCatalogKeepsMenuClassesForStencils),
    ("stencil choices preserve Mac menu order and restore undone labels", StencilSelectionTests.TestMenuOrderAndUndo),
    ("stencil selector pairs and HUD health need two positive boxes and remain editable", StencilSelectionTests.TestPositivePairsRemainEditable),
    ("gameplay labels remain repeatable and normal mode omits Mods", StencilSelectionTests.TestGameplayRepeatabilityAndMods),
    ("fresh profiles defer Clear Save and selector classes deduplicate canonically", StencilSelectionTests.TestDeferredAndCanonicalDuplicates),
    ("context inference repairs clear complete gameplay mismatches", ContextInferenceTests.TestClearMultiObjectMismatch),
    ("context inference requires a complete contract instead of shared overlap", ContextInferenceTests.TestCompleteContractNotOverlap),
    ("context repair rejects ties excess missing and unexpected positive labels", ContextInferenceTests.TestAmbiguityCountsAndNegatives),
    ("saved context repair is durable and preserves screenshot identity pixels and language", ContextInferenceTests.TestDurableStoreRepair),
    ("last labeling context and object survive reload without repeated writes", LabelingSelectionTests.TestSelectionReload),
    ("shared labels remain in a saved set that contains them", LabelingSelectionTests.TestSharedSelectionStaysInSavedSet),
    ("saved objects resolve to containing screen sets including custom classes", LabelingSelectionTests.TestSelectionMovesToContainingSet),
    ("invalid labeling preferences fall back and failed writes preserve data", LabelingSelectionTests.TestInvalidSelectionAndWriteRecovery),
    ("context switches restore the destination without rewriting prior screenshots", LabelingSelectionTests.TestContextSwitchPreservesSavedScreens),
    ("new contexts and gameplay start with empty drafts from the paused image", LabelingSelectionTests.TestMissingContextAndGameplayStartFresh),
    ("nested selector updates suppress callbacks and recover after failure", LabelingSelectionTests.TestSelectionUpdateGuard),
    ("pause and resume messages retain the Mac version-two wire contract", LabelPauseTests.TestWireMessages),
    ("label pause requires negotiated capability and confirmed acknowledgements", LabelPauseTests.TestCapabilityAdmission),
    ("label pause resolves late acknowledgements and disconnects safely", LabelPauseTests.TestAcknowledgementRaces),
    ("frozen labels own their clean image and invalidate pre-pause work", LabelPauseTests.TestFrozenFrameOwnership),
    ("normal labeling freezes locally and paused startup accepts only one image", LabelPauseTests.TestNormalAndMissingFirstFrame),
    ("Windows bridge renews pause leases blocks input and resumes on close", ForwarderPauseTests.TestLeaseTransport),
    ("pause capability and receiver disconnect gate labeling without breaking controls", ForwarderPauseTests.TestUnsupportedAndDisconnectedReceiver),
    ("closing a paused bridge is bounded even when sending is blocked", ForwarderPauseTests.TestBlockedShutdown),
    ("recent captures retain the newest 64 with unique identities", RecentFrameTests.TestCapacityAndOrdering),
    ("recent capture generations clear and reject previous sessions", RecentFrameTests.TestCaptureGeneration),
    ("exact predictions replace only their capture without reordering", RecentFrameTests.TestExactPredictionReplacement),
    ("captured predictions preserve canonical repeated and cross-context boxes", RecentFrameTests.TestCapturedRepeatedPredictions),
    ("captured screen recognition follows Mac priority and fallback", RecentFrameTests.TestCapturedFallbackAndPriority),
    ("saved screens cannot be reclassified by the previous object selection", RecentFrameTests.TestSavedScreenOwnsContext),
    ("saved frames select context-valid or explicitly requested objects", RecentFrameTests.TestSavedObjectSelection),
    ("saved frames restore every rectangle and normalize organizational groups", RecentFrameTests.TestSavedRectanglesAndLegacyGroups),
    ("recent labeled frames retain known-empty and negative examples", RecentFrameTests.TestLabeledFrameEligibility),
    ("opening captured predictions persists a draft but empty captures stay unsaved", RecentFrameTests.TestDraftPersistence),
    ("object inference returns immediately and retains only active/newest captures", ObjectInferenceWorkerTests.TestNonblockingLatestOnly),
    ("delayed inference uses capture age for predicted motion masks", ObjectInferenceWorkerTests.TestDelayedMotionMasks),
    ("object invalidation rejects active/pending old results and errors", ObjectInferenceWorkerTests.TestResetRejectsInFlightAndPending),
    ("labeling suspends object work and resumes without stale tracks", ObjectInferenceWorkerTests.TestLabelingSuspension),
    ("model failures expire old evidence and recover without stopping capture", ObjectInferenceWorkerTests.TestFailuresRecoverWithoutStoppingCapture),
    ("object worker preserves Mac nominal cadence and monotonic capture order", ObjectInferenceWorkerTests.TestLiveCadenceAndMonotonicity),
    ("refinement settings belong to their admitted capture", ObjectInferenceWorkerTests.TestCapturedRefinementSettings),
    ("capture shutdown drains active inference before model disposal", ObjectInferenceWorkerTests.TestDrainProtectsModelLifetime),
    ("delayed object publications update their exact capture independently of model generation", ObjectInferenceWorkerTests.TestExactCapturePublication),
    ("publication callbacks drain outside capture locks and recover from errors", ObjectInferenceWorkerTests.TestPublicationDrainAndRecovery),
    ("invalidated and paused inference cannot publish recent-frame predictions", ObjectInferenceWorkerTests.TestInvalidatedInferenceDoesNotPublish),
    ("live object policy filters class confidence and suppresses overlap", LiveObjectDetectionTests.TestPostprocessing),
    ("live boxes clip standardize canonicalize and enforce area and count limits", LiveObjectDetectionTests.TestPolicyBoundsAndCaps),
    ("ONNX live policy preserves independent class planes and local maxima", LiveObjectDetectionTests.TestLiveTensorDecode),
    ("object batches expire and cannot cross capture generations", LiveObjectDetectionTests.TestFreshness),
    ("object tracker smooths jitter and bridges four missed inferences", LiveObjectDetectionTests.TestTrackerJitterAndMisses),
    ("object tracker confirms ordinary candidates and resets on capture changes", LiveObjectDetectionTests.TestTrackerAdmissionAndGeneration),
    ("object tracker matches canonical classes and never reuses one track twice", LiveObjectDetectionTests.TestTrackerAssociations),
    ("pixel refinement finds exact contrasting edges without flipping or changing the frame", LiveObjectDetectionTests.TestPixelBoundary),
    ("pixel refinement handles clipped blank small and unsupported searches", LiveObjectDetectionTests.TestPixelFallbacks),
    ("moving Knight mask covers unsmoothed body and predicted sweep", LiveObjectDetectionTests.TestMotionSweep),
    ("moving masks reject stale impossible and cross-generation prediction", LiveObjectDetectionTests.TestMaskGuards),
    ("live object streams keep display debug and mapping evidence independent", LiveObjectDetectionTests.TestPipelineStreams),
    ("legacy Geo bounds expand with measured currency digits", SceneRegionDetectorTests.TestGeoDigits),
    ("legacy Knight detector separates pale head from HUD and glow", SceneRegionDetectorTests.TestBrightKnightNotHudOrGlow),
    ("legacy Knight box width stays fixed through animation", SceneRegionDetectorTests.TestFixedAnimationWidth),
    ("legacy Knight tracker stops without drift", SceneRegionDetectorTests.TestStoppedKnightDoesNotDrift),
    ("legacy Knight prediction follows a fast vertical jump", SceneRegionDetectorTests.TestFastVerticalJump),
    ("legacy Knight occlusion cannot jump to a distant light", SceneRegionDetectorTests.TestOcclusionRejectsDistantLight),
    ("legacy Knight detector rejects white cores inside bright halos", SceneRegionDetectorTests.TestHaloCannotInitializeKnight),
    ("focus prompt exclusion requires visible glyphs and expires after a fade", SceneRegionDetectorTests.TestConditionalFocusPrompt),
    ("temporal particles mask first eligible appearance and briefly survive dropout", SceneRegionDetectorTests.TestParticleLifecycle),
    ("camera compensation rejects static lights in both axes", SceneRegionDetectorTests.TestCameraCompensatedLight),
    ("disabled particle detection does not advance temporal history", SceneRegionDetectorTests.TestParticleHistoryCanBeSkipped),
    ("legacy detector clears predictions and history on reset or resized input", SceneRegionDetectorTests.TestResetAndPredictionExpiry),
    ("legacy detector rejects HUD transparent dim and colored heads", SceneRegionDetectorTests.TestAppearanceRejections),
    ("registration masks pad presentation pixels before solve scaling", RegistrationFrameMaskTests.TestPaddingBeforeScaling),
    ("registration masks clip against nonzero image extents", RegistrationFrameMaskTests.TestNonzeroExtentClipping),
    ("invalid registration masks leave the capture untouched", RegistrationFrameMaskTests.TestInvalidPreservesSource),
    ("registration replaces multiple foreground regions with opaque black", RegistrationFrameMaskTests.TestMultipleMasks),
    ("motion compares identical union masks and reseeds after reset or resize", RegistrationFrameMaskTests.TestUnionMaskMotion),
    ("testTensorAccessorReadsContiguousFloat32Output", PlatformPrimitiveParityTests.testTensorAccessorReadsContiguousFloat32Output),
    ("testPersistentVisionRegistrationReportsAdjacentFrameTransforms", PlatformPrimitiveParityTests.testPersistentVisionRegistrationReportsAdjacentFrameTransforms),
    ("CameraSolverTests.testVisionRegistrationMovesShiftedFrameBackToReference", PlatformPrimitiveParityTests.testVisionRegistrationMovesShiftedFrameBackToReference),
    ("CameraSolverTests.testVisionRegistrationReportsPositiveYForDownwardTopLeftPixelShift", PlatformPrimitiveParityTests.testVisionRegistrationReportsPositiveYForDownwardTopLeftPixelShift),
    ("frame signal rejects blackout and HUD-only images but keeps dark rooms", FrameSignalTests.TestWholeBlackVersusDarkRoom),
    ("frame signal preserves thresholds full aspect and 64-wide motion grid", FrameSignalTests.TestSignalThresholdsAndGeometry),
    ("darkening remains trackable and stable frames resume after blackout", FrameSignalTests.TestDarkeningAndBlackoutGate),
    ("transition settling resets on changing fades and capture restarts", FrameSignalTests.TestSettlingResetAndBoundaries),
    ("black-band detector preserves separate doorway edges and compact heights", FrameSignalTests.TestDoorwayBlackBands),
    ("black-band runs tolerate one gap but reject broad all-black omissions", FrameSignalTests.TestBlackBandRunRules),
    ("transition edge masks are straight rectangles with intact black interiors", FrameSignalTests.TestRectangularOmissions),
    ("live admission blocks HUD-only writes and reseeds after blackouts", FrameSignalTests.TestLiveAtlasAdmission),
    ("scene model regions own only the highest-confidence exact Knight class", SceneRegionTests.TestMovingKnightOnly),
    ("weak Knight masks require the explicit ground policy", SceneRegionTests.TestWeakKnightPolicy),
    ("legacy HUD geometry and conditional exclusions match Mac", SceneRegionTests.TestHudGeometry),
    ("atlas HUD shield supplements exact masks and ignores retired detections", SceneRegionTests.TestPersistentAtlasHud),
    ("map frame clears HUD pixels without changing source or row padding", SceneRegionTests.TestMapFrameHudPixels),
    ("map frame preserves opaque black scenery", SceneRegionTests.TestOpaqueBlackScenery),
    ("map frame clears only Knight HUD and particle regions", SceneRegionTests.TestMapFrameDetectedRectangles),
    ("scene geometry clips invalid boxes and follows actual capture dimensions", SceneRegionTests.TestGeometryClipping),
    ("live masks exclude Knight from atlas and ground without erasing arbitrary objects", SceneRegionTests.TestLiveMaskPersistence),
    ("modded atlas masks the receiver-projected hero with top-origin coordinates", SceneRegionTests.TestReceiverHeroMask),
    ("runtime status ignores legacy menu object detections", LiveGameStatusTests.TestIgnoresMenuModelObjects),
    ("menu stencils own screen selection and outrank stale gameplay objects", LiveGameStatusTests.TestMenuSelectionAndPriority),
    ("HUD stencils and current gameplay objects independently establish game state", LiveGameStatusTests.TestGameplayObjectEvidence),
    ("menu status grace expires after missing cues", LiveGameStatusTests.TestMissingCueGrace),
    ("gameplay remains latched through HUD loss and capture reset", LiveGameStatusTests.TestGameplayLatch),
    ("status tracker rejects nonfinite and older timestamps", LiveGameStatusTests.TestClockOrdering),
    ("Credits cue requires Extras and cannot replace gameplay", LiveGameStatusTests.TestCreditsNavigation),
    ("menu evidence suspends object inference but provisional Credits does not", LiveGameStatusTests.TestInferenceAdmission),
    ("label-set scoring matches Mac specificity and occurrence penalties", LiveGameStatusTests.TestLabelSetScoring),
    ("quit intent requires accepted Yes mouse-up or Z press", LiveGameStatusTests.TestQuitIntent),
    ("visible HUD rejects flashing menu evidence", LiveGameStatusTests.TestFlashingMenuGate),
    ("stable menus take ownership after HUD hold with immediate startup menus", LiveGameStatusTests.TestStableMenuGate),
    ("live pipeline separates latched presentation from fresh frame evidence", LiveGameStatusTests.TestPipelineHandoff),
    ("live Credits inference can recover genuine gameplay", LiveGameStatusTests.TestCreditsInferenceRecovery),
    ("Credits signature checks center ring and corners without aspect cropping", LiveGameStatusTests.TestCreditsPixels),
    ("label contracts share Mac resources and reject incomplete screens", LabelingContractTests.TestCatalogAndIncompleteScreens),
    ("label contracts enforce bounds and ignore hard negatives", LabelingContractTests.TestCountsAndNegatives),
    ("label auto-advance respects repeats and contract exemptions migrate", LabelingContractTests.TestAdvanceAndExemptions),
    ("menu admission uses contracts with normal Options exception", LabelingContractTests.TestMenuAdmission),
    ("label editor sets contain exact ordered objects", LabelingEditorPolicyParityTests.TestLabelSetsContainTheirExpectedObjects),
    ("label editor shared objects belong to every set", LabelingEditorPolicyParityTests.TestSharedObjectsBelongToEveryApplicableSet),
    ("label editor legacy menu labels migrate to category owners", LabelingEditorPolicyParityTests.TestLegacyMenuLabelsMigrateToTheirSingleCategoryOwners),
    ("bounded frame pump telemetry separates stale cancelled and superseded work", MenuWorkerTests.TestPumpTelemetry),
    ("bounded frame pump owns only active and newest pending work", MenuWorkerTests.TestPumpBounds),
    ("frame pump invalidation rejects previous generation results", MenuWorkerTests.TestPumpInvalidation),
    ("Hacker menu worker returns immediately and scans only latest pending frame", MenuWorkerTests.TestHackerLatestOnly),
    ("Gameplay menu worker returns exact frame and cannot rewind", MenuWorkerTests.TestExactAndMonotonic),
    ("menu worker rejects expired future and nonfinite evidence", MenuWorkerTests.TestFreshness),
    ("menu worker reset and catalog refresh invalidate evidence and clocks", MenuWorkerTests.TestResetAndMissingEvidence),
    ("menu worker reports asynchronous failures and recovers on reset", MenuWorkerTests.TestAsynchronousFailureRecovery),
    ("reset and catalog refresh reject running and pending old-generation scans", MenuWorkerTests.TestInFlightInvalidation),
    ("live menu capture store preserves portable language schema and bounds paths", MenuLiveCaptureTests.TestPortableStore),
    ("menu search cadence limits untracked work and resets immediately", MenuTrackerTests.TestCadence),
    ("menu selector pairs require bilateral evidence including faded arrows", MenuTrackerTests.TestPairEvidence),
    ("Clear Save probe searches every profile row", MenuTrackerTests.TestClearSaveOffsets),
    ("solved menu search regions stay narrow and scale with capture", MenuTrackerTests.TestSearchRegions),
    ("menu selector pairs reject different visual rows", MenuTrackerTests.TestDifferentRowsRejected),
    ("image-driven selectors can settle and recalibrate again", MenuTrackerTests.TestRecalibrationFromImages),
    ("high-rate image-driven calibration spans real stability time", MenuTrackerTests.TestHighRateCalibrationFromImages),
    ("tracker repairs stale calibration and preserves reviewed correction lineage", MenuTrackerTests.TestStaleCalibrationAndReviewedCorrections),
    ("menu tracking refreshes selection during one-frame probe grace", MenuTrackerTests.TestRowTransitionsAndProbeGrace),
    ("failed menu searches retain candidate search regions", MenuTrackerTests.TestFailedSearchKeepsRegions),
    ("menu ownership checkpoint releases a shared heading to the new scene", MenuTrackerTests.TestSceneCheckpoint),
    ("menu language changes reacquire and catalog refresh retains language", MenuTrackerTests.TestLanguageChangeAndCatalogRefresh),
    ("two-column menu rows share a nonzero selector band", MenuTrackerTests.TestTwoColumnRowBand),
    ("Clear Save modal takes ownership while its parent heading remains visible", MenuTrackerTests.TestModalTakesParentOwnership),
    ("dialog selector search pose follows stable rows rather than animated title", MenuTrackerTests.TestDialogPoseUsesStableRows),
    ("synthetic stable selector matches persist measured product calibration", MenuTrackerTests.TestStableSelectorMatchPersistsProductCalibration),
    ("synthetic intermittent selector matches do not persist calibration", MenuTrackerTests.TestIntermittentSelectorMatchesDoNotPersistCalibration),
    ("synthetic selector transitions cannot remain stuck on the previous row", MenuTrackerTests.TestLiveSelectorTransitionsCannotRemainStuckOnPreviousRow),
    ("synthetic tracked menu scans use fewer comparisons than acquisition", MenuTrackerTests.TestTrackingUsesFarFewerComparisonsWhenRequested),
    ("synthetic tracker reacquires only after the tracked probe disappears", MenuTrackerTests.TestTrackerReacquiresOnlyAfterTrackedProbeDisappearsWhenRequested),
    ("synthetic tracked scene yields to a stronger validated scene probe", MenuTrackerTests.TestTrackedSceneYieldsToStrongerValidatedSceneProbe),
    ("synthetic scene checkpoints release across weak shared probes", MenuTrackerTests.TestTrackedSceneCheckpointReleasesAcrossWeakProbeScreens),
    ("synthetic lost scene reacquires within tracked-language grace", MenuTrackerTests.TestLostSceneReacquiresUsingTrackedLanguageWithinGrace),
    ("synthetic capture reset preserves language for the next scene", MenuTrackerTests.TestCaptureResetPreservesLanguageForNextScene),
    ("synthetic failed transition frame retains tracked language", MenuTrackerTests.TestFailedTransitionFrameDoesNotDiscardTrackedLanguage),
    ("synthetic language change releases a tracked foreign advanced scene", MenuTrackerTests.TestLanguageChangeReleasesTrackedForeignAdvancedScene),
    ("synthetic advanced video scene releases to parent menus", MenuTrackerTests.TestVideoAdvancedReleasesToParentMenusAcrossLiveFrames),
    ("menu catalog builds masked text and inferred selector rows", MenuCatalogTests.TestHumanCatalog),
    ("focused labels augment scenes while correction markers stay separate", MenuCatalogTests.TestFocusedCorrections),
    ("menu catalog keeps foreign glyph widths and language probes", MenuCatalogTests.TestForeignText),
    ("menu live reference limits and holdouts prevent positive evidence leakage", MenuCatalogTests.TestLiveLimitsAndHoldouts),
    ("menu probe and selectable policies preserve dialog title discrimination", MenuCatalogTests.TestProbeAndSelectablePolicies),
    ("profile catalog derives four separate Clear Save selector pairs", MenuCatalogTests.TestClearSaveRows),
    ("reviewed menu corrections flow from pixels into portable calibration", MenuCatalogTests.TestReviewedSelectorCorrections),
    ("menu catalog rejects missing incomplete and excluded scenes", MenuCatalogTests.TestInvalidAndIncompleteScenes),
    ("reviewed contract breach UUIDs correct their actual localized selector rows", MenuCatalogTests.TestReviewedContractBreachSelectorsCorrectActualLocalizedRows),
    ("synthetic human Clear Save capture builds both selector rows", MenuCatalogTests.TestHumanClearSaveCaptureBuildsBothSelectorRows),
    ("synthetic human Inventory capture builds a static selector-free scene", MenuCatalogTests.TestHumanInventoryCaptureBuildsStaticSceneWithoutSelectors),
    ("synthetic focused drafts become object stencil variants", MenuCatalogTests.TestFocusedDraftBoxesBecomeObjectStencilVariantsWhenAvailable),
    ("synthetic latest focused draft defines full human stencil bounds", MenuCatalogTests.TestLatestFocusedDraftDefinesFullHumanStencilBounds),
    ("synthetic Italian Keyboard correction beats the empty Audio pair", MenuCatalogTests.TestItalianKeyboardCorrectionBeatsEmptyAudioPair),
    ("synthetic Italian Advanced Settings correction selects its labeled row", MenuCatalogTests.TestItalianAdvancedSettingsCorrectionSelectsLabeledRow),
    ("synthetic Italian Game correction selects its labeled row", MenuCatalogTests.TestItalianGameCorrectionSelectsLabeledRow),
    ("synthetic Korean Grimm correction rejects the empty Credits pair", MenuCatalogTests.TestKoreanGrimmCorrectionRejectsEmptyCreditsPair),
    ("synthetic newest Korean selector corrections select their labeled rows", MenuCatalogTests.TestNewestKoreanSelectorCorrectionsSelectLabeledRows),
    ("newest Korean corrections persist to primary and mirrored calibration", MenuCatalogTests.TestNewestKoreanCorrectionsPersistIntoCalibration),
    ("bundled geometry contains the latest reported selector corrections", MenuCatalogTests.TestBundledGeometryContainsLatestReportedSelectorCorrections),
    ("menu calibration loads the current Mac deployed scene/selector resource", MenuCalibrationTests.TestBundledCalibration),
    ("first learned selector creates sorted scene with retained evidence", MenuCalibrationTests.TestNewScene),
    ("selector calibration keeps language placements independent", MenuCalibrationTests.TestIndependentLanguages),
    ("selector calibration and project mirror require explicit write paths", MenuCalibrationTests.TestExplicitPaths),
    ("menu calibration round trips portable schema and rejects corrupt files", MenuCalibrationTests.TestPortableSerialization),
    ("selector calibration spans real time and can settle again", MenuCalibrationTests.TestStabilityAndRecalibration),
    ("unstable rows languages and discontinuous clocks never accumulate", MenuCalibrationTests.TestUnstableAndDiscontinuousEvidence),
    ("dominant selector cluster tolerates animation phases and outliers", MenuCalibrationTests.TestDominantClusterAndMissingPhases),
    ("reviewed selector corrections do not overwrite newer live calibration", MenuCalibrationTests.TestReviewedCorrections),
    ("failed calibration write preserves old data and recovers", MenuCalibrationTests.TestWriteFailureRecovery),
    ("menu consensus aligns captures and keeps stable glyph pixels", MenuStencilTests.TestConsensusAlignment),
    ("localized menu text recovers glyph width without a value column", MenuStencilTests.TestLocalizedText),
    ("stencil verified bounds follow sparse matched pixels", MenuStencilTests.TestVerifiedBounds),
    ("selector contrast removes idle text and preserves decorations", MenuStencilTests.TestSelectorContrast),
    ("RGB stencil correlation and color fallback match Mac", MenuStencilTests.TestPixelKernel),
    ("stencil bands and scaled matching preserve top-origin coordinates", MenuStencilTests.TestBandAndScaling),
    ("menu predicates reject colored background and isolated dust", MenuStencilTests.TestTextPredicatesAndInvalidConsensus),
    ("model versions promote durably and restore Default", ModelVersionTests.TestDurableVersions),
    ("model objects keep independent versions and selections", ModelVersionTests.TestIndependentObjects),
    ("active shared model replaces legacy per-object passes", ModelVersionTests.TestSharedModel),
    ("training checkpoint survives source run removal", ModelVersionTests.TestCheckpointRestore),
    ("regressed candidates remain reviewable without advancing lineage", ModelVersionTests.TestRegressionLineage),
    ("trained readiness requires active artifact and current labels", ModelVersionTests.TestReadiness),
    ("model review loads latest class candidate and baseline evidence", ModelVersionTests.TestReviewStore),
    ("model promotion rejects unsafe paths and preserves existing state", ModelVersionTests.TestPromotionSafety),
    ("dashboard model lifecycle restores and promotes without duplicating versions", ModelVersionTests.TestLifecycle),
    ("durable ONNX promotion preserves real inference after source removal", ModelVersionTests.TestDurableOnnxInference),
    ("failed model registry commit rolls back only the new restore point", ModelVersionTests.TestRegistryFailureRollback),
    ("HUD shield masks atlas and motion without cropping the whole top edge", TestHudAtlasExclusions),
    ("Current zoom is smooth, reversible and bounded", TestCurrentViewportZoom),
    ("viewport Free Fly owns pointer drag instead of forwarding it", LayerSceneViewportParityTests.TestFreeFlyOwnsPointerDragInsteadOfForwardingItToGame),
    ("viewport room projection uses negative factor camera shift", TestRoomProjectionUsesNegativeFactorCameraShift),
    ("viewport world projection ignores camera", TestWorldProjectionDoesNotUseCamera),
    ("viewport unproject returns atlas Q", TestUnprojectReturnsAtlasQ),
    ("viewport projection bounds ignore nonfinite tile", TestProjectionBoundsIgnoresNonFiniteTile),
    ("viewport projection rejects overflowing extent", TestProjectionRejectsRectWhoseExtentOverflows),
    ("viewport translation settler is monotonic without overshoot", TestTranslationSettlerInterpolatesMonotonicallyWithoutOvershoot),
    ("viewport translation settler interrupts from displayed offset", TestTranslationSettlerInterruptsFromCurrentlyDisplayedOffset),
    ("viewport translation settler supports zero duration", TestTranslationSettlerSupportsZeroDuration),
    ("viewport basis correction settles atlas while live pan remains authoritative", TestBasisCorrectionSettlesAtlasWhileLivePanRemainsAuthoritative),
    ("viewport room boundary uses a thin cyan line", DashboardPresentationParityTests.TestRoomBoundaryUsesThinCyanLine),
    ("viewport persistent ground palette uses purple", DashboardPresentationParityTests.TestGroundReviewPaletteUsesPurpleForPersistentTileAndLine),
    ("viewport live ground palette matches legend", DashboardPresentationParityTests.TestLiveGroundReviewPaletteMatchesLegend),
    ("viewport feature markers stay screen constant", DashboardPresentationParityTests.TestFeatureMarkersRemainScreenConstantAcrossZoom),
    ("viewport ground feature uses outlined purple square", DashboardPresentationParityTests.TestGroundFeatureUsesOnePixelOutlinedPurpleSquare),
    ("viewport persistent ground cell keeps world size", DashboardPresentationParityTests.TestPersistentGroundCellKeepsSixteenByTwelveWorldSize),
    ("viewport ground feature hit testing returns details", DashboardPresentationParityTests.TestGroundFeatureHitTestingReturnsRegisteredCellDetails),
    ("viewport invisible live ground uses live pan for hit testing", DashboardPresentationParityTests.TestInvisibleLiveGroundFeatureUsesLivePanForHitTesting),
    ("viewport feature geometry filters nonfinite and caps work", DashboardPresentationParityTests.TestFeatureOverlayFiltersNonFiniteAndCapsDrawWork),
    ("ground viewer stitches features and preserves deleted gaps", DashboardPresentationParityTests.TestRowAssemblerStitchesFeaturesAndPreservesDeletedGap),
    ("ground viewer composites feature opacity over black", DashboardPresentationParityTests.TestStitchedViewerCompositesPerPixelOpacityOverBlack),
    ("ground viewer live row refreshes selected evidence", DashboardPresentationParityTests.TestLiveRowStateAddsNewFeaturesAndRefreshesSelectedEvidence),
    ("ground atlas footprint counts painted pixels and vertical growth", DashboardPresentationParityTests.TestCountsPaintedPixelsNotTilePaddingAndAllowsVerticalGrowth),
    ("viewport Free Fly survives growing atlas bounds", LayerSceneViewportParityTests.TestFreeFlyKeepsViewportWhenAtlasBoundsGrow),
    ("viewport zoom keeps the world point under the cursor", LayerSceneViewportParityTests.TestZoomKeepsWorldPointUnderMouseCursor),
    ("viewport Current zoom stays centered on moving live frame", LayerSceneViewportParityTests.TestCurrentZoomStaysCenteredAndTracksMovingViewport),
    ("viewport Current button resets zoom with surrounding margin", LayerSceneViewportParityTests.TestCurrentButtonResetsCustomZoomAndCentersViewport),
    ("viewport game pointer uses the drawn live frame", LayerSceneViewportParityTests.TestGamePointerUsesDrawnLiveFrameInsteadOfWholeVisionWindow),
    ("viewport Fit starts a smooth transition from Free Fly", LayerSceneViewportParityTests.TestFitButtonStartsSmoothTransitionFromFreeFly),
    ("viewport Fit follows growing bounds without stepping", LayerSceneViewportParityTests.TestFitFollowsGrowingAtlasBoundsWithoutStepping),
    ("viewport Fit reacts to exact occupied bounds within a tile", LayerSceneViewportParityTests.TestFitUsesExactContentBoundsBeforeStorageTileChanges),
    ("viewport transition interruption starts at displayed transform", LayerSceneViewportParityTests.TestTransitionInterruptsAtDisplayedTransform),
    ("viewport Current follows with a small surrounding margin", LayerSceneViewportParityTests.TestFollowFrameCentersViewportWithSmallSurroundingMargin),
    ("viewport overlays use the canonical pan and zoom projection", LayerSceneViewportParityTests.TestFeatureOverlayUsesSamePanAndZoomProjection),
    ("active atlas reset archives only its world and keeps labels", TestActiveAtlasReset),
    ("HUD context survives autosave and guides the second health icon", TestHudGuidanceAfterSave),
    ("gameplay framing survives unknown frames and preserves manual fit", TestGameplayViewportPolicy),
    ("dated visual captures are accepted by the atlas", TestDatedVisualAtlas),
    ("stencil preview boxes follow the aspect-fit image", TestStencilPreviewProjection),
    ("stencil preview names resolve shared and legacy labels", TestStencilPreviewNames),
    ("label border interior matches selection in every drag direction", TestLabelBorderInterior),
    ("label border stays outside image-edge and tiny selections", TestLabelBorderEdgeCases),
    ("label zoom keeps image coordinates under the pointer", TestLabelZoomAnchor),
    ("label viewport limits zoom and fits after panning", TestLabelViewportLimits),
    ("label zoom supports smooth deltas and ignores invalid geometry", TestLabelViewportDeltas),
    ("normal keyboard keeps editor and text keys local", TestNormalGameInputPolicy),
    ("normal shortcuts own repeats and releases across focus changes", TestNormalGameShortcutState),
    ("normal input holds chords without duplicate down transitions", NormalGameKeyForwarderParityTests.TestHoldsMultipleKeysWithoutRepeatingDownTransitions),
    ("normal input focus loss drains keys and requires a fresh down", NormalGameKeyForwarderParityTests.TestFocusLossReleasesAllAndRequiresFreshDown),
    ("normal input replacement game cannot inherit held keys", NormalGameKeyForwarderParityTests.TestReplacementGameCannotInheritHeldKeys),
    ("normal input workspace and settings routing matches platform focus", NormalGameKeyForwarderParityTests.TestWorkspaceAndSettingsRouting),
    ("normal input overlay passes gameplay and reserves Vision shortcuts", NormalGameKeyForwarderParityTests.TestNativeOverlayPassesGameplayAndReservesVisionShortcuts),
    ("ordinary launch always requires mode choice", TestRuntimeModeLaunchPolicy),
    ("gameplay output waits for fresh game capture", TestGameplayOutputGate),
    ("menu framing ignores atlas coordinates", TestGameplayFrameCoordinatePolicy),
    ("game loss countdown uses requested message", TestGameLossCountdown),
    ("chooser enables only complete compatible installations", TestGameInstallationDiscovery),
    ("stencil contracts include all required menu screens", TestStencilContractCoverage),
    ("stencil visible controls and campaign instructions are translated", StencilGuidanceParityTests.TestVisibleButtonsAndCampaignInstructionsHaveTranslations),
    ("stencil languages have complete guidance and matching placeholders", StencilGuidanceParityTests.TestAllLanguagesHaveGuidanceAndMatchingPlaceholders),
    ("stencil progress uses one compatible reference screenshot", StencilGuidanceParityTests.TestProgressUsesOneReferenceAndRejectsWrongLanguageOrSize),
    ("stencil required screens follow contracts and the shipped catalog", StencilGuidanceParityTests.TestRequiredScreensFollowContractsAndWindowsCatalogMatches),
    ("stencil partial reference resumes and counts its saved Back label", StencilGuidanceParityTests.TestPartialReferenceResumesEditingAndSavedBackCounts),
    ("stencil empty setup includes every required screen", StencilGuidanceParityTests.TestEmptySetupIncludesEveryRequiredScreen),
    ("running game installation matches selected mode", TestGameInstallationMode),
    ("stencil reset preserves gameplay training data", TestStencilResetPreservesGameplay),
    ("partial stencil resumes editing and counts saved Back", TestPartialStencilGuidance),
    ("object detection requires training on current labels", TestTrainedModelReadiness),
    ("center crop exact", TestCenterCropExact),
    ("center crop wide", TestCenterCropWide),
    ("center crop tall", TestCenterCropTall),
    ("projection center", TestProjectionCenter),
    ("solid normalization", TestSolidNormalization),
    ("latest frame is bounded", TestLatestFrameSlot),
    ("semantic atlas approves stable ground", TestSemanticAtlasApproval),
    ("semantic atlas transfers reassociated evidence", TestSemanticAtlasReassociation),
    ("semantic atlas drops stale evidence", TestSemanticAtlasDropsStaleEvidence),
    ("atlas state save list load delete", TestAtlasStateSaveListLoadDelete),
    ("atlas parity saves lists loads and deletes permanently", AtlasStateStoreParityTests.TestSaveListLoadAndPermanentDelete),
    ("atlas parity imports an archive without duplicate world", AtlasStateStoreParityTests.TestNewArchiveCanBecomeLoadableWithoutDuplicatingWorld),
    ("atlas parity permanently deletes autosave archive", AtlasStateStoreParityTests.TestDeletingAutoSaveArchivePermanentlyRemovesIt),
    ("atlas parity legacy trash purge preserves active named states", AtlasStateStoreParityTests.TestPurgingLegacyTrashDoesNotTouchActiveOrNamedStates),
    ("atlas parity disk usage counts registered image storage", AtlasStateStoreParityTests.TestAtlasDiskUsageCountsRegisteredImageStorage),
    ("atlas replacement validates staging before active mutation", ActiveAtlasReplacementTests.TestValidatesStagingBeforeReplacingActive),
    ("atlas replacement returns the previous active archive", ActiveAtlasReplacementTests.TestSuccessfulSwapReturnsPreviousArchive),
    ("atlas pipeline load restores saved snapshot without mutating state", ActiveAtlasReplacementTests.TestPipelineLoadRestoresSavedSnapshotAndLeavesStateImmutable),
    ("atlas replacement copy failure preserves active state", ActiveAtlasReplacementTests.TestCopyFailurePreservesActive),
    ("atlas replacement validation failure preserves active state", ActiveAtlasReplacementTests.TestValidationFailurePreservesActive),
    ("atlas replacement swap failure rolls active state back", ActiveAtlasReplacementTests.TestSwapFailureRollsBackActive),
    ("atlas replacement rollback failure retains recovery archive", ActiveAtlasReplacementTests.TestRollbackFailureReportsRecoverableArchive),
    ("atlas archive import and trash purge", TestAtlasArchiveImportAndTrashPurge),
    ("atlas disk usage counts files", TestAtlasDiskUsage),
    ("png writer emits valid signature and dimensions", TestPngWriter),
    ("label example store round trips schema", TestLabelExampleStore),
    ("user object catalog persists portable schema", TestUserObjectCatalog),
    ("label dataset prevents leakage and trains negatives", TestLabelDatasetSplit),
    ("label dataset rejects negative-only corpus", TestLabelDatasetNeedsPositive),
    ("camera continuity delays soft recovery", TestCameraContinuity),
    ("fresh atlas waits for stable pose", TestFreshAtlasBootstrap),
    ("camera accumulator smooths and rejects jumps", TestCameraAccumulator),
    ("camera continuity preserves verified placement across isolated weak results", CameraSolverParityTests.TestIsolatedWeakRegistrationDoesNotDiscardVerifiedAtlasPlacement),
    ("camera continuity enters recovery immediately for a hard scene jump", CameraSolverParityTests.TestHardSceneJumpStillEntersRecoveryImmediately),
    ("fresh atlas bootstrap jump restarts stability window", CameraSolverParityTests.TestFreshAtlasBootstrapJumpRestartsStabilityWindow),
    ("committed atlas bypasses bootstrap delay", CameraSolverParityTests.TestCommittedAtlasBypassesBootstrapDelay),
    ("camera accumulator rejects cut-sized jump without moving path", CameraSolverParityTests.TestRejectsCutSizedJumpWithoutMovingPath),
    ("camera accumulator rejects large vertical live-width jump", CameraSolverParityTests.TestRejectsLargeVerticalRegistrationJumpAtLiveFrameWidth),
    ("camera accumulator accepts ordinary live movement", CameraSolverParityTests.TestAcceptsOrdinaryLiveCameraMovementAtLiveFrameWidth),
    ("camera accumulator accepts sparse hallway pan but rejects vertical jump", CameraSolverParityTests.TestAcceptsRecordedSparseLiveHallwayPanWithoutAdmittingVerticalJump),
    ("camera accumulator invert motion flips direction", CameraSolverParityTests.TestInvertMotionFlipsCameraDirection),
    ("rejected fast fall cannot receive atlas write admission", TestRejectedFastFallCannotReceiveAtlasWriteAdmission),
    ("viewport mode changes only through explicit framing", TestViewportModeChangesOnlyThroughExplicitFraming),
    ("parallax gain scales inverse world placement", TestParallaxGainScalesInverseWorldPlacement),
    ("feature tracker caps and matures strong features", TestFeatureTrackerCapsAndMaturesStrongFeatures),
    ("stable features enter world atlas before depth motion", TestStableFeaturesEnterWorldAtlasBeforeDepthMotion),
    ("feature atlas relocalizes and corrects backtracking drift", TestFeatureAtlasRelocalizesAndCorrectsBacktrackingDrift),
    ("loop closure normalizes camera correction by feature depth", TestLoopClosureNormalizesCameraCorrectionByFeatureDepth),
    ("feature landmarks follow solved camera motion", TestFeatureLandmarksFollowSolvedCameraMotion),
    ("feature destroyed inside view is removed from atlas", TestFeatureDestroyedInsideViewIsRemovedFromAtlas),
    ("locally animated feature is rejected as motion outlier", TestLocallyAnimatedFeatureIsRejectedAsMotionOutlier),
    ("live feature marker is hidden inside Knight box", TestLiveFeatureMarkerIsHiddenInsideKnightBox),
    ("visual rooms blackout while standing resumes same room", TestBlackoutWhileStandingResumesSameRoom),
    ("visual rooms left departure creates room to left after stable arrival", TestLeftDepartureCreatesRoomToLeftAfterStableArrival),
    ("visual rooms known room cannot reactivate away from recorded doorway", TestKnownRoomCannotReactivateAwayFromRecordedDoorway),
    ("visual rooms doorway topology persists and restores", TestDoorwayTopologyPersistsAndRestores),
    ("visual rooms composition mask crops pixels across doorway", TestCompositionMaskCropsPixelsAcrossDoorway),
    ("visual rooms black bands define separate door edges and shorter heights", TestBlackBandsDefineSeparateDoorEdgesAndShorterDoorHeights),
    ("visual rooms measured doorway travel controls connector length", TestMeasuredDoorwayTravelControlsConnectorLength),
    ("visual rooms horizontal door rejects implausible vertical arrival", TestHorizontalDoorRejectsImplausibleContinuousVerticalArrival),
    ("visual rooms horizontal door accepts route calibrated arrival", TestHorizontalDoorAcceptsRouteCalibratedContinuousArrival),
    ("visual rooms known appearance portal requires recorded direction", TestKnownAppearancePortalRequiresItsRecordedDirection),
    ("visual rooms reverse traversal reuses first room and portal pose", TestReverseTraversalReusesFirstRoomAndPortalPose),
    ("visual rooms restart prefers room represented by durable evidence", TestRestartPrefersOnlyRoomRepresentedByDurableEvidence),
    ("visual rooms opposite traversal cannot use known left portal", VisualRoomTransitionPortalFadeParityTests.TestOppositeTraversalCannotUseKnownLeftPortalToEnterRoomTwo),
    ("visual rooms known activation preserves continuous pose", VisualRoomTransitionPortalFadeParityTests.TestKnownRoomActivationKeepsContinuousPoseInsteadOfSnappingToStoredEntry),
    ("visual rooms near-black fade transitions without zero signal", VisualRoomTransitionPortalFadeParityTests.TestNearBlackFadeCanTransitionWithoutZeroSignalFrame),
    ("visual rooms remaining dark cannot create another room", VisualRoomTransitionPortalFadeParityTests.TestRemainingInDarkRoomCannotCreateAnotherRoom),
    ("visual rooms bright-dark return reuses room", VisualRoomTransitionPortalFadeParityTests.TestBrightDarkBoundaryReusesRoomOnReturn),
    ("visual rooms ordinary boundary pan does not split room", VisualRoomTransitionPortalFadeParityTests.TestOrdinaryCameraPanAtBoundaryDoesNotSplitRoom),
    ("visual rooms darkening without departure edge stays current", VisualRoomTransitionPortalFadeParityTests.TestDirectionalDarkeningWithoutDepartureEdgeRemainsCurrentRoom),
    ("visual rooms centered knight survives shrinking visibility", VisualRoomTransitionPortalFadeParityTests.TestShrinkingVisibilityWhileKnightCenteredNeverSuspendsRoomTracking),
    ("visual rooms fade with opposite arrival needs no texture motion", VisualRoomTransitionPortalFadeParityTests.TestFadeAndOppositeArrivalCanCreateRoomWithoutTextureMotion),
    ("visual rooms fade uses knight screen position", VisualRoomTransitionPortalFadeParityTests.TestFadePlacesArrivalFromKnightScreenPositionAcrossRooms),
    ("visual rooms route-calibrated fade joins town offset", VisualRoomTransitionPortalFadeParityTests.TestRouteCalibratedFadeJoinsTownAtHackerProjectedOffset),
    ("visual rooms stable same-room darkening creates no doorway", VisualRoomTransitionPortalFadeParityTests.TestStableDarkeningDuringSameRoomTravelCannotCreateDoorway),
    ("visual rooms floorless turnaround recognizes room one before placing next room left", TestFloorlessTurnaroundRecognizesRoomOneBeforePlacingNextRoomLeft),
    ("visual rooms place memory reanchors repeated known view", TestLowResolutionPlaceMemoryReanchorsRepeatedKnownViewWithinRoom),
    ("visual rooms place memory reanchors through a small visibility circle", TestLowResolutionPlaceMemoryReanchorsBrightViewThroughSmallVisibilityCircle),
    ("visual rooms place memory rejects a tiny visibility circle", TestLowResolutionPlaceMemoryRejectsTinyVisibilityCircle),
    ("visual rooms place memory rejects an unrelated small visibility circle", TestLowResolutionPlaceMemoryRejectsUnrelatedSmallVisibilityCircle),
    ("visual rooms place memory rejects an ambiguous small visibility circle", TestLowResolutionPlaceMemoryRejectsAmbiguousSmallVisibilityCircle),
    ("visual rooms place memory does not cross room ownership", TestLowResolutionPlaceMemoryDoesNotCrossRoomOwnership),
    ("visual rooms place memory rejects unrelated and selects unique keyframe", TestLowResolutionPlaceMemoryRejectsUnrelatedViewAndSelectsUniqueKeyframe),
    ("visual rooms floorless traversal publishes recognized pose immediately", TestFloorlessTraversalPublishesRecognizedRoomOnePoseImmediately),
    ("visual rooms masked correction rebases and rejects stale repeat", TestMaskedCorrectionRebasesContinuingFloorlessPoseAndRejectsStaleRepeat),
    ("visual rooms newer place match invalidates older correction", TestRoomOnePlaceMatchInvalidatesOlderMaskedCorrection),
    ("low resolution motion estimates direction", TestLowResolutionDirection),
    ("low resolution motion survives fade and subcell shift", TestLowResolutionFadeAndSubcell),
    ("low resolution motion rejects static frame", TestLowResolutionRejectsStatic),
    ("low resolution subcell motion survives room-wide fade", VisualRoomLowResolutionMotionParityTests.TestLowResolutionSubcellMotionSurvivesRoomWideFade),
    ("low resolution motion survives shrinking visibility circle", VisualRoomLowResolutionMotionParityTests.TestLowResolutionMotionSurvivesShrinkingVisibilityCircle),
    ("low resolution motion ignores moving visibility circle", VisualRoomLowResolutionMotionParityTests.TestLowResolutionMotionIgnoresMovingVisibilityCircle),
    ("moving visibility circle cannot create false motion", VisualRoomLowResolutionMotionParityTests.TestMovingVisibilityCircleCannotCreateFalseMotion),
    ("tiny visibility circle cannot create motion", VisualRoomLowResolutionMotionParityTests.TestTinyVisibilityCircleCannotCreateMotion),
    ("runtime-resolution motion retains fast vertical travel", VisualRoomLowResolutionMotionParityTests.TestRuntimeResolutionMotionRetainsFastVerticalTravel),
    ("floorless odometry accumulates through a moving fade circle", VisualRoomTransitionOdometryParityTests.TestFloorlessOdometryAccumulatesThroughMovingFadeCircle),
    ("floorless odometry bridges only short same-direction overlap loss", VisualRoomTransitionOdometryParityTests.TestFloorlessOdometryBridgesOnlyShortSameDirectionOverlapLoss),
    ("reliable velocity handoff bridges initial fade mismatch", VisualRoomTransitionOdometryParityTests.TestReliableVelocityHandoffBridgesInitialFadeMismatch),
    ("reliable velocity handoff bridges collapsed same-direction motion", VisualRoomTransitionOdometryParityTests.TestReliableVelocityHandoffBridgesCollapsedSameDirectionMotion),
    ("reliable velocity handoff keeps strong same-direction measurement", VisualRoomTransitionOdometryParityTests.TestReliableVelocityHandoffKeepsStrongSameDirectionMeasurement),
    ("reliable velocity handoff bridges weak vertical fade motion", VisualRoomTransitionOdometryParityTests.TestReliableVelocityHandoffBridgesWeakVerticalFadeMotion),
    ("vertical handoff uses latest measured acceleration", VisualRoomTransitionOdometryParityTests.TestVerticalHandoffUsesLatestMeasuredAcceleration),
    ("vertical handoff survives input beginning mid-fall", VisualRoomTransitionOdometryParityTests.TestVerticalHandoffSurvivesInputBeginningMidFall),
    ("floorless odometry accumulates slow subcell travel", VisualRoomTransitionOdometryParityTests.TestFloorlessOdometryAccumulatesSlowSubcellTravelWithoutFreezing),
    ("low-resolution motion separates simultaneous vertical travel", VisualRoomTransitionOdometryParityTests.TestLowResolutionMotionSeparatesSimultaneousVerticalTravel),
    ("floorless odometry retains vertical travel without horizontal direction", VisualRoomTransitionOdometryParityTests.TestFloorlessOdometryRetainsVerticalTravelWithoutHorizontalDirection),
    ("floorless odometry retains small camera easing against held input", VisualRoomTransitionOdometryParityTests.TestFloorlessOdometryRetainsSmallCameraEasingAgainstHeldInput),
    ("floorless odometry rejects strong horizontal contradiction", TestFloorlessOdometryRejectsStrongHorizontalContradiction),
    ("floorless odometry retains vertical-dominant motion against held input", TestFloorlessOdometryRetainsVerticalDominantMotionAgainstHeldInput),
    ("floorless traversal publishes vertical travel without horizontal input", TestFloorlessTraversalPublishesVerticalTravelWithoutHorizontalInput),
    ("route-calibrated short baseline retains fast horizontal travel", TestRouteCalibratedShortBaselineRetainsFastHorizontalTravel),
    ("short baseline still accumulates slow subcell travel", TestShortBaselineStillAccumulatesSlowSubcellTravel),
    ("cutscene camera warp is rejected before local motion resumes", TestCutsceneCameraWarpIsRejectedBeforeLocalMotionResumes),
    ("doorway odometry accumulates low-resolution silhouette travel", TestDoorwayOdometryAccumulatesLowResolutionSilhouetteTravel),
    ("coarse motion diagnostic runs continuously away from doorway", TestCoarseMotionDiagnosticRunsContinuouslyAwayFromDoorway),
    ("floorless traversal carries pose and hands back to ground", TestFloorlessTraversalCarriesPoseWithoutChangingRoomAndHandsBackToGround),
    ("floorless traversal keeps doorway departure at last ground anchor", TestFloorlessTraversalKeepsDoorwayDepartureAtLastGroundAnchor),
    ("ground hypothesis rejects a short foreground-owned line", GroundHypothesisProductionParityTests.TestShortPersistentLineInsideKnownForegroundIsRejected),
    ("ground hypothesis one-frame line cannot create persistent evidence", GroundHypothesisProductionParityTests.TestOneFrameLineNeverCreatesPersistentFeatures),
    ("ground hypothesis parallel lines require texture confirmation", GroundHypothesisProductionParityTests.TestParallelLinesCannotAlignWithoutFeatureConfirmation),
    ("ground hypothesis texture selects the correct parallel line", GroundHypothesisProductionParityTests.TestFeatureMatchesChooseCorrectParallelLineBeforeOverlap),
    ("ground hypothesis distant unidentified line cannot move pose", GroundHypothesisProductionParityTests.TestDistantUnidentifiedLineCannotMovePersistentGroundPose),
    ("ground hypothesis biased fallback preserves trusted reference", GroundHypothesisProductionParityTests.TestBiasedFallbackDoesNotReplaceTrustedGroundReference),
    ("visual ground recovery drives published camera and atlas", VisualGroundIntegrationTests.TestPersistentRecoveryDrivesPublishedCamera),
    ("visual ground provisional promotion requires bridge trust", VisualGroundIntegrationTests.TestProvisionalPromotionRequiresBridgeTrust),
    ("visual ground floorless reanchor clears stale texture", VisualGroundIntegrationTests.TestFloorlessReanchorClearsStaleTexture),
    ("visual gameplay tracker emits receiver-free atlas poses", TestVisualGameplayTracker),
    ("visual gameplay tracker admits only bootstrapped verified ground poses", TestVisualGameplayGroundAdmission),
    ("transition bridge resumes without inventing a room offset", TransitionMotionBridgeTests.TestResumeAfterBlackout),
    ("transition bridge resumes only after suspension", TransitionMotionBridgeTests.TestResumeRequiresSuspension),
    ("transition bridge tracks horizontal translation without vertical drift", TransitionMotionBridgeTests.TestFallbackTranslation),
    ("transition bridge union mask ignores moving foreground", TransitionMotionBridgeTests.TestFallbackIgnoresMovingForeground),
    ("ground pose continuity rejects stale unsupported snaps", TransitionMotionBridgeTests.TestContinuity),
    ("moving foreground rejects only small registration residuals", TransitionMotionBridgeTests.TestForegroundResidual),
    ("transition small motion requires sustained directional evidence", TransitionMotionBridgeTests.TestSmallMotion),
    ("landing correction requires a coherent second ground measurement", GroundPlacementRecoveryTests.TestLandingCorrection),
    ("ordinary measured fallback keeps continuity policy", GroundPlacementRecoveryTests.TestOrdinaryFallback),
    ("ground placement rejects duplicate stale weak and incoherent votes", GroundPlacementRecoveryTests.TestRejectsBadVotes),
    ("global ground verification resolves local loss", GroundPlacementRecoveryTests.TestGlobalVerification),
    ("provisional ground bootstrap can reseed its origin", GroundPlacementRecoveryTests.TestProvisionalBootstrap),
    ("normal ground motion and reset stay trusted", GroundPlacementRecoveryTests.TestNormalAndReset),
    ("sparse ground descent needs two coherent observations", GroundPlacementRecoveryTests.TestSparseDescent),
    ("sparse ground recovery rejects unsafe evidence", GroundPlacementRecoveryTests.TestSparseRejections),
    ("live world pose replacement preserves raw evidence", TestLiveWorldPoseReplacement),
    ("live world rejects invalid graph references", TestLiveWorldRejectsInvalidGraph),
    ("live world room translation moves optimized evidence", TestLiveWorldRoomTranslation),
    ("live world snapshot store compare and swap", TestLiveWorldSnapshotStore),
    ("translation pose graph distributes loop drift", TestTranslationPoseGraphLoop),
    ("translation pose graph is deterministic with exact anchor", TestTranslationPoseGraphOrder),
    ("translation pose graph rejects disconnected evidence", TestTranslationPoseGraphRejectsDisconnected),
    ("live world optimizer preserves raw evidence", TestLiveWorldPoseOptimizer),
    ("visual route replay repeats persisted camera truth", TestVisualRouteReplay),
    ("visual route builds persisted world without camera truth", TestVisualRouteWorld),
    ("visual route world retains stationary visual evidence", TestVisualRouteWorldStationary),
    ("visual route world separates scene transitions", TestVisualRouteWorldScenes),
    ("ground reference foreground at another row survives", GroundReferenceDetectorParityTests.TestKnownForegroundAtDifferentRowDoesNotRejectLine),
    ("ground reference knight upper body rejects false line", GroundReferenceDetectorParityTests.TestKnightUpperBodyRejectsLongFalseLineButKeepsFloorAtFeet),
    ("ground reference small knight patch infers body", GroundReferenceDetectorParityTests.TestSmallKnightDetectionInfersBodyAboveDetectedPatch),
    ("ground reference jittering knight preserves room floor", GroundReferenceDetectorParityTests.TestJitteringKnightBoxCannotEraseRoomWideFloorEvidence),
    ("ground reference requested tuning defaults", GroundReferenceDetectorParityTests.TestRequestedTuningDefaultsAreLockedIn),
    ("ground reference detector does not bridge threshold gaps", GroundReferenceDetectorParityTests.TestGroundDetectorDoesNotBridgeThresholdGaps),
    ("ground reference detector keeps long levels", GroundReferenceDetectorParityTests.TestTheoryDetectorKeepsMultipleLongLevelsAndRejectsShortIsland),
    ("ground reference separation preserves other columns", GroundReferenceDetectorParityTests.TestLineSeparationKeepsLongerOverlappingLineButPreservesOtherColumns),
    ("ground reference dominant floor suppresses foreground top", GroundReferenceDetectorParityTests.TestDominantFloorSuppressesShortForegroundTopInsideOcclusion),
    ("ground reference sparse bridge cannot outrank solid floor", GroundReferenceDetectorParityTests.TestSparseLongBridgeCannotOutrankSolidFloor),
    ("ground reference empty gap is not evidence", GroundReferenceDetectorParityTests.TestEmptyGapDoesNotCountTowardMinimumEvidenceLength),
    ("ground reference occlusion merge boundary joins evidence", GroundReferenceDetectorParityTests.TestOcclusionMergeGapJoinsDistantEvidenceIntoOneGroundLine),
    ("ground reference launch tuning overrides and clamps sweep values", TestLaunchTuningOverridesAndClampsDeveloperSweepValues),
    ("ground reference edge debug image keeps full-resolution difference", TestGroundEdgeDebugImageContainsFullResolutionOnePixelDifference),
    ("ground reference stage image requires contiguous minimum segment", TestGroundStageImageRequiresContiguousMinimumSegment),
    ("ground reference cleaned image draws accepted ground line", TestCleanedImageDrawsAcceptedGroundLine),
    ("ground reference default detector bridges known object occlusion", TestDefaultDetectorBridgesKnownObjectOcclusion),
    ("ground reference tracker acquires and follows vertical camera", TestTrackerAcquiresFromStableImageEvidenceAndFollowsVerticalCamera),
    ("ground reference tracker switches ground and accepts correction", TestTrackerSwitchesToSustainedHigherGroundAndAcceptsWorldCorrection),
    ("ground pixels preserve grayscale orientation and boundary patch", GroundPixelsParityTests.TestGrayscaleOrientationAndBoundaryPatchPreserveEveryPixel),
    ("ground pixels reject outside and incomplete texture", GroundPixelsParityTests.TestOutsideOrIncompleteTextureCannotBecomePatchEvidence),
    ("ground pixels retired options cannot run default experiment", GameStartupCliTailParityTests.TestRetiredOptionsCannotSilentlyRunDefaultExperiment),
    ("ground kernel preserves requested polarity", TestGroundKernelPolarity),
    ("ground theory bridges occlusion but preserves step", TestGroundOcclusionAndStep),
    ("semantic ground rejects bright decoration", TestSemanticGroundSurface),
    ("semantic ground keeps weak endpoints with strong core", TestSemanticGroundStrongCore),
    ("ground foreground rejection preserves long floor", TestGroundForegroundRejection),
    ("ground analysis masks expanded exclusions", TestGroundAnalysisMask),
    ("ground kernel respects capture padding rows", TestGroundCapturePadding),
    ("frame rejects short storage", TestShortFrameStorage),
    ("bmp header", TestBmpHeader),
    ("receiver hello wire contract", TestReceiverHello),
    ("receiver state wire contract", TestReceiverState),
    ("receiver checkpoint wire contract", TestReceiverCheckpoint),
    ("recorded input path round trips mac schema", TestRecordedInputPath),
    ("ground truth camera scale", TestGroundTruthCameraScale),
    ("ground truth world line reprojects with camera motion", TestWorldLineReprojectsWithCameraMotion),
    ("ground truth line store round trips world lines", TestGroundTruthLineStoreRoundTripsWorldLines),
    ("ground truth add negative modify delete and undo stay horizontal", TestGroundTruthAddNegativeModifyDeleteAndUndoKeepHorizontalWorldLines),
    ("ground truth overlay uses positive negative and selected kinds", TestGroundTruthOverlayUsesPositiveNegativeAndSelectedKinds),
    ("ground truth entering scene seeds only once", TestGroundTruthEnteringSceneSeedsOnlyOnce),
    ("ground truth store rejects malformed and missing document fields", TestGroundTruthStoreRejectsMalformedAndMissingDocumentFields),
    ("ground truth store rejects missing and invalid line fields", TestGroundTruthStoreRejectsMissingAndInvalidLineFields),
    ("ground truth failed atomic replace preserves old document", TestGroundTruthFailedAtomicReplacePreservesOldDocument),
    ("ground truth minimum length foreign scene and palette sanity", TestGroundTruthMinimumLengthForeignSceneAndPaletteSanity),
    ("receiver rejects multiline input", TestReceiverRejectsMultiline),
    ("startup coordinator stable navigation", TestStartupCoordinatorStableNavigation),
    ("startup automatic navigation requires explicit launch flag", GameStartupCliTailParityTests.TestAutomaticNavigationRequiresExplicitLaunchFlag),
    ("startup automation command parsing bounds durations", GameStartupCliTailParityTests.TestAutomationCommandParsingBoundsDurations),
    ("startup production stability requires eight frames", GameStartupDetectorTests.TestProductionStabilityWindowRequiresEightFramesBeforeSelection),
    ("startup moves to Start Game before selecting", GameStartupDetectorTests.TestCoordinatorMovesToStartGameBeforeSelecting),
    ("startup moves to profile one before selecting", GameStartupDetectorTests.TestCoordinatorMovesToProfileOneBeforeSelecting),
    ("startup waits for stencil selection", GameStartupDetectorTests.TestCoordinatorWaitsForStencilSelectionBeforeSelecting),
    ("startup selects both menus then admits gameplay", GameStartupDetectorTests.TestCoordinatorSelectsBothMenusThenAdmitsGameplay),
    ("startup back from profile returns to title", GameStartupDetectorTests.TestBackFromProfileReturnsToTitlePhase),
    ("startup rejected selection retries only after transition", GameStartupDetectorTests.TestRejectedSelectionCanBeRetriedButNeverRepeatsWhileTransitioning),
    ("startup gameplay gate closes when title is recognized", GameStartupDetectorTests.TestGameplayGateClosesWhenTitleIsRecognized),
    ("startup capture reset preserves gameplay", GameStartupDetectorTests.TestCaptureEpochResetPreservesEstablishedGameplay),
    ("startup checkpoint restore reopens gameplay", GameStartupDetectorTests.TestConfirmedCheckpointRestoreReopensGameplayGate),
    ("startup bright HUD art establishes gameplay", GameStartupDetectorTests.TestBrightHudArtEstablishesGameplay),
    ("startup detector uses menu stencil", TestDetectorUsesMenuStencil),
    ("startup detector preserves stencil selection", TestDetectorPreservesStencilSelectionForNavigation),
    ("startup pixel lookalike is rejected and stencil releases gameplay latch", TestPixelMenuLookalikeIsRejectedAndStencilReleasesGameplayLatch),
    ("startup detector finds profile one geometry", TestDetectorFindsSelectedProfileOne),
    ("startup detector uses generic stencil for both startup screens", TestDetectorUsesGenericMenuStencilForBothStartupScreens),
    ("startup verified health releases stale profile stencil without Soul", TestVerifiedHealthReleasesStaleProfileStencilWithoutSoul),
    ("startup HUD pair beats title-like scenery", GameStartupDetectorTests.TestHudPairWinsWhenGameplaySceneryResemblesTitle),
    ("startup gameplay requires both HUD elements", GameStartupDetectorTests.TestGameplayNeedsBothHudElementsButNotKnight),
    ("startup coordinator closes gameplay gate on menu", TestStartupCoordinatorClosesGate),
    ("startup detector title selection", TestStartupDetectorTitleSelection),
    ("startup detector profile selection", TestStartupDetectorProfileSelection),
    ("startup detector gameplay hud", TestStartupDetectorGameplayHud),
    ("detection decoder local maximum", TestDetectionDecoderLocalMaximum),
    ("detection decoder classwise nms", TestDetectionDecoderClasswiseNms),
    ("detection decoder exposes raw candidates", TestDetectionDecoderRaw),
    ("bundled ONNX model loads", TestBundledOnnxModel),
    ("ground truth atlas projects negative world pixels", TestGroundTruthAtlasProjection),
    ("ground truth atlas overwrites with newest pixels", TestGroundTruthAtlasBlend),
    ("gameplay presentation advances while analysis trails capture", GameplayPresentationTests.TestDelayedAnalysis),
    ("normal tracking advances during an unfinished autosave", GameplayPresentationTests.TestNormalTrackingDuringAutosave),
    ("modded world writes wait for autosave publication", GameplayPresentationTests.TestModdedTrackingWaitsForAutosave),
    ("newest atlas honors masks, black, restore and capture identity", GameplayPresentationTests.TestNewestPixels),
    ("newest atlas rollback preserves committed pixels", GameplayPresentationTests.TestNewestRollback),
    ("newest atlas preserves projection and coordinate bounds", GameplayPresentationTests.TestNewestProjectionBounds),
    ("live atlas default overwrites on the next observation", GameplayPresentationTests.TestLiveAtlasDefault),
    ("ground truth atlas emits dirty tiles and resets", TestGroundTruthAtlasDirtyReset),
    ("ground truth atlas imports restored tile", TestGroundTruthAtlasImport),
    ("live tiled atlas keeps large-world endpoints and stable tile identities", LiveTiledAtlasProductionParityTests.TestLargeWorldKeepsBothEndsAndStableTileIDs),
    ("live ground-truth atlas selects an observed medoid and rejects a one-frame particle", LiveTiledAtlasProductionParityTests.TestTemporalQuiltingAveragesTextureAndAttenuatesOneFrameParticle),
    ("live tiled atlas atomically replaces corrected placements", LiveTiledAtlasBatchOneTests.TestPoseCorrectionReplacesOldPlacementAtomically),
    ("live tiled atlas grows exact content bounds within a storage tile", LiveTiledAtlasBatchOneTests.TestContentBoundsGrowInsideOneStorageTile),
    ("live tiled atlas rejects stale and incomplete pose corrections", LiveTiledAtlasBatchOneTests.TestRejectsStaleAndIncompletePoseCorrections),
    ("live tiled atlas rolls back cancelled pose correction and retries", LiveTiledAtlasBatchOneTests.TestCancelledPoseCorrectionRollsBackAndCanBeRetried),
    ("live tiled atlas cancels pose correction inside a crowded tile", LiveTiledAtlasBatchOneTests.TestPoseCorrectionCanCancelInsideOneCrowdedTile),
    ("live tiled atlas rolls back insert cancelled inside pixel work", LiveTiledAtlasBatchOneTests.TestIncrementalInsertCanCancelInsidePixelWorkAndRollBack),
    ("live tiled atlas treats transparent pixels as absent and black as evidence", LiveTiledAtlasBatchOneTests.TestIncrementalOpaqueBlackWinsWhileTransparentLeavesPriorPixel),
    ("live tiled atlas preserve-brighter agrees incrementally and on rebuild", LiveTiledAtlasBatchOneTests.TestPreserveBrighterRejectsDarkOverwriteIncrementallyAndOnRebuild),
    ("live tiled atlas incremental insertion matches full rebuild", LiveTiledAtlasBatchOneTests.TestIncrementalInsertionMatchesFullRebuild),
    ("live tiled atlas unique insert rasterizes only its new source", LiveTiledAtlasBatchOneTests.TestUniqueInsertRasterizesOnlyNewSourcePerDirtyTile),
    ("live tiled atlas newest quilting overwrites interior and follows edge seam", LiveTiledAtlasBatchOneTests.TestNewestQuiltedOverwritesInteriorAndFollowsLowErrorEdgeSeam),
    ("live tiled atlas newest quilting incremental output matches cross-tile rebuild", LiveTiledAtlasBatchOneTests.TestNewestQuiltedIncrementalOutputMatchesCrossTileRebuild),
    ("live tiled atlas temporal agreement removes an initial bright attack", LiveTiledAtlasBatchOneTests.TestTemporalAgreementRemovesInitialBrightAttackAfterThreeBackgroundSamples),
    ("live tiled atlas temporal agreement rejects one dark occluder", LiveTiledAtlasBatchOneTests.TestTemporalAgreementRejectsOneDarkOccluderAndKeepsStaticWhiteScenery),
    ("live tiled atlas temporal agreement accepts stable dark scenery", LiveTiledAtlasBatchOneTests.TestTemporalAgreementLetsStableDarkSceneryReplaceBrightProvisionalPixels),
    ("live tiled atlas temporal agreement selects an observed medoid", LiveTiledAtlasBatchOneTests.TestTemporalAgreementSelectsAnObservedMedoidInsteadOfAveraging),
    ("live tiled atlas temporal agreement deduplicates capture identity", LiveTiledAtlasBatchOneTests.TestTemporalAgreementCountsDuplicateCaptureOnlyOnce),
    ("live tiled atlas temporal agreement bounds and clears its window", LiveTiledAtlasBatchOneTests.TestTemporalAgreementWindowIsBoundedAndClearsAcrossDiscontinuity),
    ("live tiled atlas temporal agreement aligns moving world evidence", LiveTiledAtlasBatchOneTests.TestTemporalAgreementAlignsMovingFramesInWorldCoordinates),
    ("live tiled atlas temporal agreement cancellation rolls back window and pixels", LiveTiledAtlasBatchOneTests.TestTemporalAgreementCancellationRollsBackWindowAndPublishedPixels),
    ("live tiled atlas temporal agreement distinguishes transparency from black", LiveTiledAtlasBatchOneTests.TestTemporalAgreementTreatsTransparentPixelsAsNoEvidenceAndBlackAsScenery),
    ("live tiled atlas temporal agreement crosses tile neighborhoods", LiveTiledAtlasBatchOneTests.TestTemporalAgreementUsesNeighborhoodAcrossTileBoundary),
    ("live tiled atlas temporal agreement incremental output matches pose-corrected rebuild", LiveTiledAtlasBatchOneTests.TestTemporalAgreementIncrementalOutputMatchesPoseCorrectedRebuild),
    ("live tiled atlas temporal quilting incremental output matches ordered rebuild", LiveTiledAtlasBatchOneTests.TestTemporalQuiltingIncrementalOutputMatchesOrderedRebuild),
    ("live tiled atlas temporal quilting window remains bounded", LiveTiledAtlasBatchOneTests.TestTemporalQuiltingWindowRemainsBounded),
    ("live atlas cadence presents at capture rate and writes atlas at five FPS", LiveAtlasPipelineRemainingParityTests.TestCadencePresentsAtCaptureRateAndWritesAtlasAtFiveFPS),
    ("live atlas write can be skipped while presentation continues", LiveAtlasPipelineRemainingParityTests.TestAtlasWriteCanBeSkippedWhilePresentationContinues),
    ("live atlas stale pose presents frame but cannot write atlas", LiveAtlasPipelineRemainingParityTests.TestStalePosePresentsFrameButCannotWriteAtlas),
    ("LiveAtlasPipelineTests.testDelayedExactPoseFrameCannotReplaceNewerLiveWindowOrEnterAtlas", LiveAtlasPipelineRemainingParityTests.TestDelayedExactPoseFrameCannotReplaceNewerLiveWindowOrEnterAtlas),
    ("robustness: rejected atlas submissions cannot poison cadence or anchor", LiveAtlasPipelineRemainingParityTests.TestRejectedAtlasSubmissionsCannotPoisonCadenceOrAnchor),
    ("LiveAtlasPipelineTests.testSlowAtlasWorkerDoesNotDelayLatestLivePresentation", LiveAtlasPipelineRemainingParityTests.TestSlowAtlasWorkerDoesNotDelayLatestLivePresentation),
    ("LiveAtlasPipelineTests.testResetInvalidatesActiveTicketBeforeItCanPersistOrPublish", LiveAtlasPipelineRemainingParityTests.TestResetInvalidatesActiveTicketBeforeItCanPersistOrPublish),
    ("LiveAtlasPipelineTests.testResetCancelsPersistedAtlasRestoration", LiveAtlasPipelineRemainingParityTests.TestResetCancelsPersistedAtlasRestoration),
    ("live atlas gameplay uses newest pixel immediately", LiveAtlasPipelineRemainingParityTests.TestGameplayAtlasUsesNewestPixelImmediately),
    ("LiveAtlasPipelineTests.testTransitionBridgeRectangularEdgeMaskPreservesMappedRoom", LiveAtlasPipelineRemainingParityTests.TestTransitionBridgeRectangularEdgeMaskPreservesMappedRoom),
    ("robustness: ground truth temporal agreement deduplicates capture identity", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalAgreementDeduplicatesCaptureIdentity),
    ("robustness: ground truth temporal agreement clears across context discontinuities", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalAgreementClearsAcrossContextDiscontinuities),
    ("robustness: ground truth temporal agreement honors masks and alpha", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalAgreementHonorsMasksAndAlpha),
    ("robustness: ground truth temporal agreement votes across tile boundaries", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalAgreementVotesAcrossTileBoundaries),
    ("robustness: ground truth temporal storage returns on reset and import", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalStorageReturnsOnResetAndImport),
    ("robustness: ground truth temporal commit failure rolls back atomically", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalCommitFailureRollsBackAtomically),
    ("robustness: ground truth reentrant checkpoint cannot commit stale state", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalReentrantCheckpointCannotCommitStaleState),
    ("robustness: ground truth full-resolution temporal history is bounded", LiveAtlasPipelineRemainingParityTests.TestGroundTruthTemporalFullResolutionHistoryIsBounded),
    ("live atlas solved camera pose places current frame over atlas", LiveAtlasPipelineRemainingParityTests.TestSolvedCameraPosePlacesCurrentFrameOverAtlas),
    ("live atlas stable gameplay frames refine atlas but menu does not", LiveAtlasPipelineRemainingParityTests.TestStableGameplayFramesRefineAtlasButMenuDoesNot),
    ("live atlas menu preview cannot anchor first gameplay placement", LiveAtlasPipelineRemainingParityTests.TestMenuPreviewCannotAnchorFirstGameplayAtlasPlacement),
    ("live atlas autosave age uses atlas creation instead of manifest write", LiveAtlasPipelineRemainingParityTests.TestAutoSaveAgeUsesAtlasCreationRatherThanLatestManifestWrite),
    ("LiveTiledAtlasTests.testTemporalAgreementAgainstSavedFullResolutionEvidence", LiveAtlasPipelineRemainingParityTests.TestTemporalAgreementAgainstSavedFullResolutionEvidence),
    ("receiver player ops wire contract", TestReceiverPlayerOps),
    ("scene session reopen preserves observation IDs", SceneSessionStoreTests.TestReopenPreservesObservationIDs),
    ("scene session PNG preserves black and alpha", SceneSessionStoreTests.TestPNGSourcePreservesBlackAndAlphaPixels),
    ("scene session rejects stale revision writer", SceneSessionStoreTests.TestAtomicSceneRevisionRejectsStaleWriter),
    ("scene session CAS commits exactly one writer", SceneSessionStoreTests.TestConcurrentSceneCompareAndSwapCommitsExactlyOneWriter),
    ("scene session missing source is explicit", SceneSessionStoreTests.TestMissingSourceThrowsExplicitError),
    ("scene session rejects schema and path traversal", SceneSessionStoreTests.TestUnsupportedManifestAndPathTraversalAreRejected),
    ("scene session canonical root accepts evidence", SceneSessionStoreTests.TestTmpSymlinkRootAcceptsCanonicalEvidencePath)
};

if (Environment.GetEnvironmentVariable("HKV_RUN_QUILT_BENCHMARKS") == "1")
{
    tests.Add((
        "live tiled atlas newest quilting stays within the full-resolution budget",
        LiveTiledAtlasBatchOneTests.TestNewestQuiltedFullResolutionFiveRunBenchmark));
    tests.Add((
        "live tiled atlas temporal quilting stays within the full-resolution budget",
        LiveTiledAtlasBatchOneTests.TestTemporalQuiltingFullResolutionFiveRunBenchmark));
}

if (Environment.GetEnvironmentVariable("HKV_RUN_ATLAS_BENCHMARKS") == "1")
{
    tests.Add((
        "live tiled atlas temporal agreement stays within the full-resolution budget",
        LiveAtlasPipelineRemainingParityTests.TestTemporalAgreementFullResolutionFiveRunBenchmark));
}

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
await TestReceiverClientLoopback();
Console.WriteLine("PASS receiver client loopback");
Console.WriteLine($"Windows core tests passed: {tests.Count + 1}");
return;

static void TestHudAtlasExclusions()
{
    var frame = SolidFrame(640, 360, blue: 100, green: 100, red: 100);
    var exclusions = HudAtlasMask.Rectangles(640, 360, null);
    Equal(new PixelRect(0, 0, 256, 90), exclusions.Single());
    var mosaic = new GroundTruthAtlasMosaic(1, 64, 0);
    True(mosaic.AddFrame(frame, AtlasTelemetry(0, 0) with
        { OrthographicSize = 180, PixelsPerWorldUnitX = 1, PixelsPerWorldUnitY = 1 }, exclusions));
    True(!mosaic.TryReadPixel(-300, -160, out _, out _, out _, out _));
    True(mosaic.TryReadPixel(80, -160, out _, out _, out _, out var count));
    Equal((ushort)1, count);
    var grid = LowResolutionMotionGrid.FromFrame(frame, excludedRectangles: exclusions);
    Equal((byte)0, grid.Luma[0]);
    Equal((byte)100, grid.Luma[40]);
    var hud = new HudStencilResult([], null, [], [new StencilRect(300, 20, 20, 20)], [], default, false, 0);
    var extended = HudAtlasMask.Rectangles(640, 360, hud);
    True(extended.Contains(new PixelRect(300, 20, 20, 20)));
    mosaic.Reset();
    True(mosaic.AddFrame(frame, AtlasTelemetry(0, 0) with
        { OrthographicSize = 180, PixelsPerWorldUnitX = 1, PixelsPerWorldUnitY = 1 }, extended));
    True(!mosaic.TryReadPixel(-10, -150, out _, out _, out _, out _));
    True(mosaic.TryReadPixel(10, -150, out _, out _, out _, out _));
}

static void TestCurrentViewportZoom()
{
    Close(1.12, GameplayViewportPolicy.CurrentZoom(1, 120));
    Close(1, GameplayViewportPolicy.CurrentZoom(1.12, -120));
    Close(Math.Sqrt(1.12), GameplayViewportPolicy.CurrentZoom(1, 60));
    Close(1, GameplayViewportPolicy.CurrentZoom(1, 0));
    Close(8, GameplayViewportPolicy.CurrentZoom(8, 120));
    Close(.1, GameplayViewportPolicy.CurrentZoom(.1, -120));
    Close(1, GameplayViewportPolicy.CurrentZoom(double.NaN, double.NaN));
    Close(2, GameplayViewportPolicy.CurrentZoom(2, double.PositiveInfinity));
}

static void TestActiveAtlasReset()
{
    WithTemporaryDirectory(root => {
        var support = Path.Combine(root, "HollowKnightVision");
        var active = Path.Combine(support, "atlas-v1", "active");
        var store = new AtlasStateStore(Path.Combine(support, "atlas-v1", "states"));
        var labels = Path.Combine(support, "labeling-v1", "examples");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(labels);
        File.WriteAllText(Path.Combine(active, "tile_0_0.bmp"), "old menu tile");
        File.WriteAllText(Path.Combine(active, "atlas.json"), "old metadata");
        File.WriteAllText(Path.Combine(labels, "label.json"), "health labels");
        var previous = store.Save("Keep this state", destination => {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "tile.bmp"), "saved world");
        });
        var backup = VisionResetController.ArchiveActiveAtlas(store, active);
        True(backup is not null);
        True(!Directory.Exists(active));
        Equal(2, store.List().Count);
        Equal("old menu tile", File.ReadAllText(Path.Combine(store.WorldPath(backup!), "tile_0_0.bmp")));
        Equal("old metadata", File.ReadAllText(Path.Combine(store.WorldPath(backup!), "atlas.json")));
        Equal("saved world", File.ReadAllText(Path.Combine(store.WorldPath(previous), "tile.bmp")));
        Equal("health labels", File.ReadAllText(Path.Combine(labels, "label.json")));
        True(VisionResetController.ArchiveActiveAtlas(store, active) is null);
        foreach (var wrongPath in new[] { support, labels, store.RootDirectory, store.WorldPath(previous) })
        {
            var rejected = false;
            try { VisionResetController.ArchiveActiveAtlas(store, wrongPath); }
            catch (ArgumentException) { rejected = true; }
            True(rejected);
            True(Directory.Exists(wrongPath));
        }
    });
}

static void TestHudGuidanceAfterSave()
{
    foreach (var context in StencilInitializationPolicy.AllContexts)
        Equal(context.Name, StencilInitializationPolicy.ContextDisplayName(context.Identifier));
    WithTemporaryDirectory(root => {
        var store = new LabelingExampleStore(Path.Combine(root, "labeling-v1", "examples"));
        var setup = new StencilInitializationCoordinator(store, GameRuntimeMode.Normal);
        Equal(setup.Text("drawHealthFirst"), setup.Guidance("gameplay-hud", "game.health")!);
        var annotation = new LabelingExampleAnnotation(Guid.NewGuid(), "game.health", .1, .04, .025, .05);
        var saved = store.Save(SolidFrame(640, 360, 0, 0, 0), "gameplay-hud", [annotation]);
        setup.Refresh();
        var display = StencilInitializationPolicy.ContextDisplayName(saved.Manifest.ContextIdentifier);
        Equal("Gameplay HUD", display);
        var reloadedContext = StencilInitializationPolicy.NormalizeContext(display);
        Equal(setup.Text("drawHealthSecond"), setup.Guidance(reloadedContext, "game.health")!);
        saved = store.Update(saved, reloadedContext, [annotation, annotation with { Id = Guid.NewGuid(), X = .14 }], ["game.health"]);
        setup.Refresh();
        True(!setup.Steps.Any(step => step.ContextIdentifier == "gameplay-hud"));
        Equal(setup.Text("invalidHUD"), setup.Guidance(reloadedContext, "game.health")!);
        setup.HasUsableHUD = true;
        Equal(setup.Text("screenDone", ("screen", "Gameplay HUD")), setup.Guidance(reloadedContext, "game.health")!);
    });
}

static void TestGameplayViewportPolicy()
{
    True(GameplayViewportPolicy.CanPan("Free Fly"));
    True(GameplayViewportPolicy.CanPan("Current"));
    True(!GameplayViewportPolicy.CanPan("Fit"));
    True(GameplayViewportPolicy.UsesAtlasCoordinates(null, true));
    True(!GameplayViewportPolicy.UsesAtlasCoordinates(null, false));
    True(!GameplayViewportPolicy.UsesAtlasCoordinates("main-title", true));
    True(GameplayViewportPolicy.UsesAtlasCoordinates("game", false));
    True(GameplayViewportPolicy.KeepTransform("Fit", true, true));
    True(!GameplayViewportPolicy.KeepTransform("Fit", false, true));
    True(GameplayViewportPolicy.KeepTransform("Free Fly", false, true));
    True(!GameplayViewportPolicy.KeepTransform("Current", true, true));
    True(!GameplayViewportPolicy.KeepTransform("Fit", true, false));
}

static ViewportProjectionTile ViewportProjectionFixture(
    double factor = 2,
    double width = 100) => new(
        new ViewportRect(10, 20, width, 50),
        factor,
        new ViewportPoint(200, 300),
        RoomScale: 2);

static void TestRoomProjectionUsesNegativeFactorCameraShift()
{
    var projected = GameplayViewportProjection.Project(
        ViewportProjectionFixture(),
        ViewportProjectionMode.Room,
        new ViewportPoint(4, 5));
    Equal(new ViewportPoint(2, 10), new ViewportPoint(projected.X, projected.Y));
}

static void TestWorldProjectionDoesNotUseCamera()
{
    var projected = GameplayViewportProjection.Project(
        ViewportProjectionFixture(),
        ViewportProjectionMode.World,
        new ViewportPoint(99, 99));
    Equal(new ViewportPoint(220, 340), new ViewportPoint(projected.X, projected.Y));
}

static void TestUnprojectReturnsAtlasQ()
{
    var point = GameplayViewportProjection.Unproject(
        new ViewportPoint(60, 45),
        ViewportProjectionFixture(),
        ViewportProjectionMode.Room,
        new ViewportPoint(0, 0));
    Equal(new ViewportPoint(60, 45), point!.Value);
}

static void TestProjectionBoundsIgnoresNonFiniteTile()
{
    var invalid = new ViewportProjectionTile(
        new ViewportRect(double.PositiveInfinity, 0, 10, 10),
        Factor: 1,
        RoomPosition: new ViewportPoint(0, 0),
        RoomScale: 1);
    var valid = ViewportProjectionFixture();
    var bounds = GameplayViewportProjection.Bounds(
        [invalid, valid],
        ViewportProjectionMode.World,
        new ViewportPoint(0, 0));
    Equal(new ViewportRect(220, 340, 200, 100), bounds!.Value);
    True(GameplayViewportProjection.Bounds(
        [invalid],
        ViewportProjectionMode.World,
        new ViewportPoint(0, 0)) is null);
}

static void TestProjectionRejectsRectWhoseExtentOverflows()
{
    True(!GameplayViewportProjection.IsFinite(new ViewportRect(
        double.MaxValue,
        0,
        double.MaxValue,
        10)));
}

static void TestTranslationSettlerInterpolatesMonotonicallyWithoutOvershoot()
{
    var settler = new ViewportTranslationSettler();
    settler.Settle(new ViewportPoint(20, -10), timestamp: 0);

    Equal(new ViewportPoint(0, 0), settler.Offset(0));
    Equal(new ViewportPoint(10, -5), settler.Offset(0.1));
    Equal(new ViewportPoint(20, -10), settler.Offset(0.2));
    Equal(new ViewportPoint(20, -10), settler.Offset(11));
}

static void TestTranslationSettlerInterruptsFromCurrentlyDisplayedOffset()
{
    var settler = new ViewportTranslationSettler();
    settler.Settle(new ViewportPoint(20, 0), timestamp: 0);
    Equal(new ViewportPoint(10, 0), settler.Offset(0.1));

    settler.Settle(new ViewportPoint(30, 0), timestamp: 0.1);
    Equal(new ViewportPoint(10, 0), settler.CurrentOffset);
    Equal(new ViewportPoint(20, 0), settler.Offset(0.2));
    Equal(new ViewportPoint(30, 0), settler.Offset(0.31));
}

static void TestTranslationSettlerSupportsZeroDuration()
{
    var settler = new ViewportTranslationSettler(duration: 0);
    settler.Settle(new ViewportPoint(8, -3), timestamp: 4);
    Equal(new ViewportPoint(8, -3), settler.Offset(4));
}

static void TestBasisCorrectionSettlesAtlasWhileLivePanRemainsAuthoritative()
{
    var controller = new GameplayViewportController();
    controller.SetFreeFlyTransform(new ViewportTransform(2, 300, 200));
    controller.SetInitialWorldBasisOffset(new ViewportPoint(0, 0));

    controller.UpdateWorldBasisOffset(new ViewportPoint(20, -10), timestamp: 1);

    Equal(new ViewportTransform(2, 300, 200), controller.Transform);
    Equal(new ViewportPoint(260, 220), controller.AtlasPan(1));
    Equal(new ViewportPoint(280, 210), controller.AtlasPan(1.1));
    Equal(new ViewportPoint(300, 200), controller.AtlasPan(1.2));
}

static void TestDatedVisualAtlas()
{
    var tracker = new VisualGameplayTracker();
    var atlas = new GroundTruthAtlasMosaic(pixelsPerWorldUnit: 1);
    var origin = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    BgraFrame Frame(int index)
    {
        var pixels = new byte[640 * 360 * BgraFrame.BytesPerPixel];
        for (var offset = 3; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
            pixels[offset] = 255;
        return new(640, 360, 640 * BgraFrame.BytesPerPixel, pixels, index,
            origin.AddSeconds(index));
    }
    var frame = Frame(0);
    var first = tracker.Observe(frame, null);
    Equal(0.0, first.UnityRealtime);
    True(first.HasFiniteCoordinates());
    True(atlas.AddFrame(frame, first));
    True(atlas.TileCount > 0);
    var second = tracker.Observe(Frame(1), null);
    Equal(1.0, second.UnityRealtime);
    True(atlas.AddFrame(Frame(1), second));
    tracker.Reset();
    Equal(0.0, tracker.Observe(Frame(10), null).UnityRealtime);
}

static void TestStencilPreviewProjection()
{
    var annotation = new LabelingExampleAnnotation(Guid.NewGuid(), "shared.back", 0.25, 0.25, 0.5, 0.5);
    var tall = StencilPreviewPresentation.FitImage(640, 360, 800, 800);
    Equal(new LabelBoxBounds(0, 175, 800, 450), tall);
    Equal(new LabelBoxBounds(200, 287.5, 400, 225), StencilPreviewPresentation.Project(annotation, tall));
    var wide = StencilPreviewPresentation.FitImage(640, 360, 1600, 450);
    Equal(new LabelBoxBounds(400, 0, 800, 450), wide);
    Equal(new LabelBoxBounds(600, 112.5, 400, 225), StencilPreviewPresentation.Project(annotation, wide));
    var small = StencilPreviewPresentation.FitImage(640, 360, 400, 225);
    Equal(new LabelBoxBounds(100, 56.25, 200, 112.5), StencilPreviewPresentation.Project(annotation, small));
    var wholeImage = annotation with { X = 0, Y = 0, Width = 1, Height = 1, IsNegative = true };
    Equal(wide, StencilPreviewPresentation.Project(wholeImage, wide));
    Equal(default(LabelBoxBounds), StencilPreviewPresentation.FitImage(640, 360, 0, 450));
    Equal(default(LabelBoxBounds), StencilPreviewPresentation.FitImage(640, 360, 800, double.NaN));
    Equal(default(LabelBoxBounds), StencilPreviewPresentation.FitImage(0, 360, 800, 450));
    Equal(0.25, annotation.X); // Preview never changes saved geometry.
}

static void TestStencilPreviewNames()
{
    var annotation = new LabelingExampleAnnotation(Guid.NewGuid(), "main-title.select-decoration", 0.1, 0.2, 0.05, 0.05);
    Equal("Left Select Decoration", StencilPreviewPresentation.LabelName(annotation));
    Equal("Right Select Decoration", StencilPreviewPresentation.LabelName(annotation with { X = 0.8 }));
    Equal("Back", StencilPreviewPresentation.LabelName(annotation with { ClassIdentifier = "select-profile.back" }));
    Equal("Options", StencilPreviewPresentation.LabelName(annotation with { ClassIdentifier = "main-title.options" }));
    Equal("custom.unknown", StencilPreviewPresentation.LabelName(annotation with { ClassIdentifier = "custom.unknown" }));
}

static void TestLabelBorderInterior()
{
    foreach (var (x1, y1, x2, y2) in new[]
    {
        (10.0, 20.0, 110.0, 80.0), (110.0, 80.0, 10.0, 20.0),
        (10.0, 80.0, 110.0, 20.0), (110.0, 20.0, 10.0, 80.0)
    })
    foreach (var displayScale in new[] { 0.5, 1.0, 2.55, 8.0, 24.0 })
    {
        var selection = LabelBoxBounds.FromCorners(x1, y1, x2, y2);
        var stroke = LabelBoxBounds.CanvasStrokeWidth(displayScale);
        var outer = selection.OutsideStroke(stroke);
        True(Math.Abs(stroke * displayScale - 2) < 1e-10);
        True(Math.Abs(outer.X + stroke - selection.X) < 1e-10);
        True(Math.Abs(outer.Y + stroke - selection.Y) < 1e-10);
        True(Math.Abs(outer.X + outer.Width - stroke - (selection.X + selection.Width)) < 1e-10);
        True(Math.Abs(outer.Y + outer.Height - stroke - (selection.Y + selection.Height)) < 1e-10);
        // Rendering must not expand or otherwise mutate saved label geometry.
        Equal(new LabelBoxBounds(10, 20, 100, 60), selection);
    }
}

static void TestLabelBorderEdgeCases()
{
    var fullImage = new LabelBoxBounds(0, 0, 640, 360);
    Equal(new LabelBoxBounds(-2, -2, 644, 364), fullImage.OutsideStroke(2));
    var tiny = LabelBoxBounds.FromCorners(10, 10, 10.25, 10.5);
    Equal(new LabelBoxBounds(8, 8, 4.25, 4.5), tiny.OutsideStroke(2));
    Equal(new LabelBoxBounds(8, 8, 4, 4), LabelBoxBounds.FromCorners(10, 10, 10, 10).OutsideStroke(2));
    Equal(2.0, LabelBoxBounds.CanvasStrokeWidth(0));
    Equal(2.0, LabelBoxBounds.CanvasStrokeWidth(double.NaN));
}

static void TestLabelZoomAnchor()
{
    foreach (var (width, height) in new[] { (1920.0, 920.0), (720.0, 450.0), (960.0, 960.0) })
    {
        var before = LabelingViewportTransform.Fit.PanBy(35, -18);
        var focusX = width * 0.7;
        var focusY = height * 0.35;
        var fittedWidth = Math.Min(width / 640, height / 360) * 640;
        var fittedHeight = fittedWidth * 360 / 640;
        var after = before.ZoomAt(0.12, focusX, focusY, width, height);
        True(after.Zoom > before.Zoom);
        var sourceX = (focusX - width / 2 - before.PanX) / (fittedWidth * before.Zoom) + 0.5;
        var sourceY = (focusY - height / 2 - before.PanY) / (fittedHeight * before.Zoom) + 0.5;
        True(Math.Abs(sourceX - ((focusX - width / 2 - after.PanX) / (fittedWidth * after.Zoom) + 0.5)) < 1e-10);
        True(Math.Abs(sourceY - ((focusY - height / 2 - after.PanY) / (fittedHeight * after.Zoom) + 0.5)) < 1e-10);
        var restored = after.ZoomAt(-0.12, focusX, focusY, width, height);
        True(Math.Abs(restored.Zoom - before.Zoom) < 1e-10);
        True(Math.Abs(restored.PanX - before.PanX) < 1e-10);
        True(Math.Abs(restored.PanY - before.PanY) < 1e-10);
    }
}

static void TestLabelViewportLimits()
{
    var state = LabelingViewportTransform.Fit;
    for (var i = 0; i < 100; i++) state = state.ZoomAt(0.12, 200, 100, 800, 600);
    Equal(8.0, state.Zoom);
    Equal(state, state.ZoomAt(0.12, 600, 400, 800, 600)); // No drift at limit.
    var panned = state.PanBy(75, -40);
    Equal(state.Zoom, panned.Zoom);
    Equal(state.PanX + 75, panned.PanX);
    Equal(state.PanY - 40, panned.PanY);
    for (var i = 0; i < 100; i++) state = state.ZoomAt(-0.12, 200, 100, 800, 600);
    Equal(1.0, state.Zoom);
    Equal(state, state.ZoomAt(-0.12, 600, 400, 800, 600));
    state = LabelingViewportTransform.Fit; // Right-click fit resets both axes.
    Equal(1.0, state.Zoom);
    Equal(0.0, state.PanX);
    Equal(0.0, state.PanY);
}

static void TestLabelViewportDeltas()
{
    var state = LabelingViewportTransform.Fit;
    var smooth = state.ZoomAt(0.001, 400, 300, 800, 600);
    True(smooth.Zoom > 1 && smooth.Zoom < state.ZoomAt(0.12, 400, 300, 800, 600).Zoom);
    Equal(state, state.ZoomAt(0, 400, 300, 800, 600));
    Equal(state.ZoomAt(0.35, 400, 300, 800, 600), state.ZoomAt(120, 400, 300, 800, 600));
    Equal(state, state.ZoomAt(double.NaN, 400, 300, 800, 600));
    Equal(state, state.ZoomAt(0.12, double.NaN, 300, 800, 600));
    Equal(state, state.ZoomAt(0.12, 400, 300, 0, 600));
    Equal(state, state.ZoomAt(0.12, 400, 300, 800, double.PositiveInfinity));
    Equal(state, state.PanBy(double.NaN, 0));
}

static void TestNormalGameInputPolicy()
{
    True(NormalGameInputPolicy.AllowsGameInput("Gameplay", false, false));
    foreach (var workspace in new[] { "Label", "Stencil", "Model" })
        True(!NormalGameInputPolicy.AllowsGameInput(workspace, false, false));
    True(!NormalGameInputPolicy.AllowsGameInput("Gameplay", true, false));
    True(!NormalGameInputPolicy.AllowsGameInput("Gameplay", false, true));
    foreach (var key in "GLSMHOK")
    {
        True(NormalGameInputPolicy.IsVisionShortcut(key));
        True(!NormalGameInputPolicy.IsGameKey(key));
    }
    // 1-4 belong to Vision's editors and stay reserved even while Hollow
    // Knight owns native focus. All other gameplay bindings pass through.
    foreach (var key in "1234")
    {
        True(NormalGameInputPolicy.IsVisionShortcut(key));
        True(!NormalGameInputPolicy.IsGameKey(key));
    }
    foreach (var key in new[] { 0x61, 0x62, 0x63, 0x64 })
    {
        True(NormalGameInputPolicy.IsVisionShortcut(key));
        True(!NormalGameInputPolicy.IsGameKey(key));
    }
    foreach (var key in "AZXCDIFQ") True(NormalGameInputPolicy.IsGameKey(key));
    foreach (var key in new[] { 0x25, 0x26, 0x27, 0x28, 0x1B, 0x0D, 0x20 })
        True(NormalGameInputPolicy.IsGameKey(key));
    foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
        True(!NormalGameInputPolicy.IsGameKey(key));
}

static void TestNormalGameShortcutState()
{
    var state = new NormalGameShortcutState();
    True(!state.Handle('L', true, false, false, out var invoke));
    True(!invoke); // Other applications are untouched.
    True(!state.Handle('L', true, true, true, out invoke));
    True(!invoke); // Ctrl/Alt/Win shortcuts are untouched.
    True(state.Handle('L', true, true, false, out invoke));
    True(invoke);
    True(state.Handle('L', true, false, false, out invoke));
    True(!invoke); // Held L cannot toggle back after editor focus changes.
    True(state.Handle('L', false, false, true, out invoke));
    True(!invoke); // Release follows the captured press even after Alt-Tab.
    True(!state.Handle('L', false, false, false, out _));
    True(!state.Handle('X', true, true, false, out _)); // Gameplay X passes.
    state.Capture('X'); // Editor X returns to Gameplay.
    True(state.Handle('X', true, true, false, out invoke));
    True(!invoke); // Holding editor X cannot also attack in the game.
    True(state.Handle('X', false, true, false, out _));
    True(!state.Handle('X', true, true, false, out _));
    state.Capture('1');
    state.Clear();
    True(!state.Handle('1', false, true, false, out _));
}

static void TestRuntimeModeLaunchPolicy()
{
    True(GameRuntimeModeLaunchPolicy.RequiresChoice(null));
    True(!GameRuntimeModeLaunchPolicy.RequiresChoice(GameRuntimeMode.Normal));
    True(!GameRuntimeModeLaunchPolicy.RequiresChoice(GameRuntimeMode.Modded));
}

static void TestGameplayOutputGate()
{
    True(!GameplayOutputGate.CanShowRestoredAtlas(
        hasFreshGameFrame: false,
        explicitlyRequested: false));
    True(GameplayOutputGate.CanShowRestoredAtlas(
        hasFreshGameFrame: true,
        explicitlyRequested: false));
    True(GameplayOutputGate.CanShowRestoredAtlas(
        hasFreshGameFrame: false,
        explicitlyRequested: true));
}

static void TestGameplayFrameCoordinatePolicy()
{
    True(!GameplayOutputGate.UsesAtlasCoordinates(null));
    True(!GameplayOutputGate.UsesAtlasCoordinates("main-title"));
    True(!GameplayOutputGate.UsesAtlasCoordinates("select-profile"));
    True(GameplayOutputGate.UsesAtlasCoordinates("game"));
}

static void TestGameLossCountdown()
{
    Equal(5, GameLossCountdown.InitialSeconds);
    Equal("Hollow Knight lost. Closing app in 5 seconds", GameLossCountdown.Message(5));
    Equal("Hollow Knight lost. Closing app in 0 seconds", GameLossCountdown.Message(0));
}

static void TestStencilContractCoverage()
{
    Equal(26, StencilInitializationPolicy.Contexts(GameRuntimeMode.Modded).Length);
    Equal(25, StencilInitializationPolicy.Contexts(GameRuntimeMode.Normal).Length);
    Equal(189, StencilInitializationPolicy.RequiredCount(GameRuntimeMode.Modded));
    Equal(183, StencilInitializationPolicy.RequiredCount(GameRuntimeMode.Normal));
    var normal = StencilInitializationPolicy.MissingSteps([], "en", GameRuntimeMode.Normal);
    True(!normal.Any(step => step.ContextIdentifier == "mods" || step.ClassIdentifier == "shared.mods"));
    Equal("main-title", normal[0].ContextIdentifier);
    Equal("gameplay-hud", normal.Select(step => step.ContextIdentifier).Distinct().ElementAt(2));
    Equal("clear-save", normal.Last().ContextIdentifier);
    True(!normal.Any(step => step.ContextIdentifier == "select-profile" && step.ClassIdentifier == "select-profile.clear-save"));
    True(normal.Where(step => StencilInitializationPolicy.Phase(step.ContextIdentifier) == 1)
        .All(step => step.ContextIdentifier is "main-title" or "select-profile" or "gameplay-hud"));
    foreach (var language in StencilInitializationCoordinator.SupportedLanguages) {
        foreach (var key in new[] { "phase1", "navigateHUD", "drawHealthFirst", "profilePhaseDone", "stencilLocked", "modelLocked" })
            True(StencilGuidanceText.Text(language.Identifier, key) != key);
    }
    True(normal.Any(step => step.ContextIdentifier == "screen-scale" && step.RequiredCount == 2));
    True(normal.Any(step => step.ContextIdentifier == "inventory" && step.RequiredCount == 4));
    True(!normal.Any(step => step.ContextIdentifier is "game" or "credits"));
}

static void TestGameInstallationMode()
{
    WithTemporaryDirectory(root => {
        var executable = Path.Combine(root, "hollow_knight.exe");
        File.WriteAllBytes(executable, []);
        var managed = Path.Combine(root, "hollow_knight_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), []);

        HollowKnightGameLauncher.ValidateInstallation(executable, requireModded: false);
        True(RejectsInstallation(executable, requireModded: true));

        var receiver = Path.Combine(
            managed,
            "Mods", "HollowKnightVisionInputReceiver",
            "HollowKnightVisionInputReceiver.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(receiver)!);
        File.WriteAllBytes(receiver, []);
        True(RejectsInstallation(executable, requireModded: false));
        True(RejectsInstallation(executable, requireModded: true));

        File.WriteAllBytes(Path.Combine(managed, "MMHOOK_Assembly-CSharp.dll"), []);
        HollowKnightGameLauncher.ValidateInstallation(executable, requireModded: true);
        True(RejectsInstallation(executable, requireModded: false));
    });
}

static void TestGameInstallationDiscovery()
{
    WithTemporaryDirectory(root => {
        var executable = Path.Combine(root, "hollow_knight.exe");
        File.WriteAllBytes(executable, []);
        var managed = Path.Combine(root, "hollow_knight_Data", "Managed");
        Directory.CreateDirectory(managed);

        True(HollowKnightGameLauncher.FindCompatibleExecutable(false, executable) is null);
        True(HollowKnightGameLauncher.FindCompatibleExecutable(true, executable) is null);

        File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), []);
        Equal(executable, HollowKnightGameLauncher.FindCompatibleExecutable(false, executable)!);
        True(HollowKnightGameLauncher.FindCompatibleExecutable(true, executable) is null);

        var receiver = Path.Combine(
            managed,
            "Mods", "HollowKnightVisionInputReceiver",
            "HollowKnightVisionInputReceiver.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(receiver)!);
        File.WriteAllBytes(receiver, []);
        File.WriteAllBytes(Path.Combine(managed, "MMHOOK_Assembly-CSharp.dll"), []);
        True(HollowKnightGameLauncher.FindCompatibleExecutable(false, executable) is null);
        Equal(executable, HollowKnightGameLauncher.FindCompatibleExecutable(true, executable)!);
    });
}

static bool RejectsInstallation(string executable, bool requireModded)
{
    try
    {
        HollowKnightGameLauncher.ValidateInstallation(executable, requireModded);
        return false;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

static void TestPartialStencilGuidance()
{
    WithTemporaryDirectory(root => {
        var store = new LabelingExampleStore(Path.Combine(root, "labeling-v1", "examples"));
        var example = store.Save(SolidFrame(640, 360, 0, 0, 0), "select-profile",
            [new LabelingExampleAnnotation(Guid.NewGuid(), "shared.back", .1, .1, .2, .1)]);
        var setup = new StencilInitializationCoordinator(store, GameRuntimeMode.Modded);
        Equal("Click Stencil and Edit Labels for Select Profile.", setup.Guidance(null, null, "select-profile")!);
        True(!setup.Steps.Any(step => step.ContextIdentifier == "select-profile" && step.ClassIdentifier == "shared.back"));
        store.Delete(example);
        setup.Refresh();
        Equal("Select Start Game to navigate to Select Profile and press L.", setup.Guidance(null, null, "select-profile")!);
    });
}

static void TestTrainedModelReadiness()
{
    WithTemporaryDirectory(root => {
        var id = Guid.NewGuid();
        True(!TrainedModelReadiness.IsReady(root, new HashSet<Guid> { id }));
        File.WriteAllText(Path.Combine(root, "Detector.onnx"), "model");
        True(!TrainedModelReadiness.IsReady(root, new HashSet<Guid> { id }));
        File.WriteAllText(Path.Combine(root, "training.json"), "{}");
        True(!TrainedModelReadiness.IsReady(root, new HashSet<Guid> { id }));
        File.WriteAllText(Path.Combine(root, "training.json"), JsonSerializer.Serialize(new { predictionsFilename = "predictions.json" }));
        File.WriteAllText(Path.Combine(root, "predictions.json"), JsonSerializer.Serialize(new { examples = new[] { new { exampleIdentifier = id } } }));
        True(TrainedModelReadiness.IsReady(root, new HashSet<Guid> { id }));
        True(!TrainedModelReadiness.IsReady(root, new HashSet<Guid>()));
        True(!TrainedModelReadiness.IsReady(root, new HashSet<Guid> { Guid.NewGuid() }));
        File.Delete(Path.Combine(root, "Detector.onnx"));
        True(!TrainedModelReadiness.IsReady(root, new HashSet<Guid> { id }));
    });
}

static void TestStencilResetPreservesGameplay()
{
    WithTemporaryDirectory(workspace =>
    {
        var support = Path.Combine(workspace, "HollowKnightVision");
        var store = new LabelingExampleStore(Path.Combine(support, "labeling-v1", "examples"));
        var frame = SolidFrame(640, 360, 0, 0, 0);
        var menu = store.Save(frame, "main-title",
            [new LabelingExampleAnnotation(Guid.NewGuid(), "main-title.start-game", .1, .1, .2, .1)]);
        var game = store.Save(frame, "game",
            [new LabelingExampleAnnotation(Guid.NewGuid(), "game.playable-knight", .1, .1, .2, .1)]);
        var model = Path.Combine(support, "labeling-v1", "model.txt");
        File.WriteAllText(model, "trained model");
        var backup = VisionResetController.ResetStencils(store.RootDirectory, support);
        True(backup is not null);
        True(!Directory.Exists(menu.DirectoryPath));
        True(Directory.Exists(Path.Combine(backup!, Path.GetFileName(menu.DirectoryPath))));
        Equal(1, store.Load().Count);
        Equal(game.Id, store.Load()[0].Id);
        Equal("trained model", File.ReadAllText(model));
        True(VisionResetController.ResetStencils(store.RootDirectory, support) is null);
    });
}

static void TestCenterCropExact() => Equal(
    new PixelRect(0, 0, 640, 360),
    FrameGeometry.CenterCrop(640, 360));

static void TestCenterCropWide() => Equal(
    new PixelRect(160, 0, 1600, 900),
    FrameGeometry.CenterCrop(1920, 900));

static void TestCenterCropTall() => Equal(
    new PixelRect(0, 152, 1280, 720),
    FrameGeometry.CenterCrop(1280, 1024));

static void TestProjectionCenter()
{
    var projection = FrameGeometry.Projection(1920, 900);
    var point = projection.OutputToSource(320, 180);
    Close(960, point.X);
    Close(450, point.Y);
}

static void TestSolidNormalization()
{
    var pixels = new byte[3 * 2 * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = 7;
        pixels[index + 1] = 19;
        pixels[index + 2] = 211;
        pixels[index + 3] = 255;
    }
    var source = new BgraFrame(3, 2, 12, pixels, 42, DateTimeOffset.UnixEpoch);
    var output = CpuBgraNormalizer.Normalize(source, 8, 4);
    Equal(42L, output.CaptureId);
    for (var index = 0; index < output.Pixels.Length; index += 4)
    {
        Equal((byte)7, output.Pixels[index]);
        Equal((byte)19, output.Pixels[index + 1]);
        Equal((byte)211, output.Pixels[index + 2]);
        Equal((byte)255, output.Pixels[index + 3]);
    }
}

static void TestLatestFrameSlot()
{
    var slot = new LatestFrameSlot();
    slot.Publish(Frame(1));
    slot.Publish(Frame(2));
    True(slot.TryTake(out var newest));
    Equal(2L, newest!.CaptureId);
    True(!slot.TryTake(out _));
    Equal(new LatestFrameStatistics(2, 1, 1), slot.Statistics);
}

static void TestSemanticAtlasApproval()
{
    var atlas = new GroundSemanticAtlas();
    var ground = new GroundHypothesisAtlasLine(
        1, new AtlasPoint(20, 65), new AtlasPoint(140, 65));
    var decoration = new GroundHypothesisAtlasLine(
        2, new AtlasPoint(20, 25), new AtlasPoint(140, 25));
    IReadOnlyList<GroundHypothesisAtlasLine> approved = [];
    for (var index = 0; index < 6; index++)
    {
        approved = atlas.Update(
            [ground, decoration],
            [new CleanFloorLine(40, new PixelSpan(16, 143))],
            new AtlasPoint(10, 5),
            160,
            100,
            true);
    }

    Equal(1, approved.Count);
    Equal(1, approved[0].SegmentId);
    var persisted = atlas.Update(
        [ground, decoration],
        [],
        new AtlasPoint(500, 0),
        160,
        100,
        true);
    Equal(1, persisted.Count);
    Equal(1, persisted[0].SegmentId);
}

static void TestSemanticAtlasReassociation()
{
    var atlas = new GroundSemanticAtlas();
    var original = new GroundHypothesisAtlasLine(
        1, new AtlasPoint(20, 65), new AtlasPoint(140, 65));
    for (var index = 0; index < 6; index++)
    {
        atlas.Update(
            [original],
            [new CleanFloorLine(40, new PixelSpan(16, 143))],
            new AtlasPoint(10, 5),
            160,
            100,
            true);
    }

    var replacement = new GroundHypothesisAtlasLine(
        9, new AtlasPoint(24, 66), new AtlasPoint(144, 66));
    var approved = atlas.Update([replacement], [], null, 160, 100, false);
    Equal(1, approved.Count);
    Equal(9, approved[0].SegmentId);
}

static void TestSemanticAtlasDropsStaleEvidence()
{
    var atlas = new GroundSemanticAtlas();
    var original = new GroundHypothesisAtlasLine(
        1, new AtlasPoint(20, 65), new AtlasPoint(140, 65));
    for (var index = 0; index < 6; index++)
    {
        atlas.Update(
            [original],
            [new CleanFloorLine(40, new PixelSpan(16, 143))],
            new AtlasPoint(10, 5),
            160,
            100,
            true);
    }

    Equal(0, atlas.Update([], [], null, 160, 100, false).Count);
    var replacement = new GroundHypothesisAtlasLine(
        9, new AtlasPoint(24, 66), new AtlasPoint(144, 66));
    Equal(0, atlas.Update([replacement], [], null, 160, 100, false).Count);
}

static void TestAtlasStateSaveListLoadDelete()
{
    WithTemporaryDirectory(workspace =>
    {
        var stateId = Guid.Parse("79a55ae1-2d74-4f85-8099-91c249e6242c");
        var createdAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var store = new AtlasStateStore(
            Path.Combine(workspace, "states"),
            () => stateId,
            () => createdAt);
        var saved = store.Save("  First atlas  ", destination =>
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "world.json"), "{}");
        });

        Equal("First atlas", saved.Name);
        Equal(stateId, saved.Id);
        Equal(createdAt, saved.CreatedAt);
        var metadataPath = Path.Combine(
            store.RootDirectory,
            stateId.ToString("D").ToUpperInvariant(),
            "state.json");
        using (var metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath)))
        {
            Equal(
                stateId.ToString("D").ToUpperInvariant(),
                metadata.RootElement.GetProperty("id").GetString()!);
            Equal(JsonValueKind.Number, metadata.RootElement.GetProperty("createdAt").ValueKind);
        }
        var listed = store.List();
        Equal(1, listed.Count);
        Equal(saved, listed[0]);
        True(File.Exists(Path.Combine(store.WorldPath(saved), "world.json")));

        store.Delete(saved);
        Equal(0, store.List().Count);
    });
}

static void TestAtlasArchiveImportAndTrashPurge()
{
    WithTemporaryDirectory(workspace =>
    {
        var archive = Path.Combine(workspace, "active-archive");
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(archive, "world.json"), "{}");
        var store = new AtlasStateStore(Path.Combine(workspace, "states"));
        var imported = store.ImportArchive(archive, "Before New");

        True(!Directory.Exists(archive));
        True(File.Exists(Path.Combine(store.WorldPath(imported), "world.json")));
        var trash = Path.Combine(store.RootDirectory, "Trash");
        Directory.CreateDirectory(trash);
        File.WriteAllBytes(Path.Combine(trash, "old-atlas"), new byte[64]);
        store.PurgeLegacyTrash();
        True(!Directory.Exists(trash));
        True(Directory.Exists(store.WorldPath(imported)));
    });
}

static void TestAtlasDiskUsage()
{
    WithTemporaryDirectory(workspace =>
    {
        var frames = Path.Combine(workspace, "frames");
        Directory.CreateDirectory(frames);
        File.WriteAllBytes(Path.Combine(frames, "one.png"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(frames, "two.png"), new byte[4096]);
        Equal(12288L, AtlasStateStore.FileByteCount(workspace));
    });
}

static void TestPngWriter()
{
    WithTemporaryDirectory(workspace =>
    {
        var path = Path.Combine(workspace, "frame.png");
        PngWriter.Write(Frame(7), path);
        var data = File.ReadAllBytes(path);
        True(data.AsSpan(0, 8).SequenceEqual(
            new byte[] { 137, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }));
        Equal(2, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)));
        Equal(2, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        Equal((byte)6, data[25]);
    });
}

static void TestLabelExampleStore()
{
    WithTemporaryDirectory(workspace =>
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("10000000-0000-4000-8000-000000000001"),
            Guid.Parse("10000000-0000-4000-8000-000000000002"),
            Guid.Parse("10000000-0000-4000-8000-000000000003")
        ]);
        var createdAt = new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
        var store = new LabelingExampleStore(
            Path.Combine(workspace, "examples"),
            () => ids.Dequeue(),
            () => createdAt);
        var example = store.Save(
            Frame(9),
            "game",
            [new LabelingExampleAnnotation(
                Guid.Parse("20000000-0000-4000-8000-000000000001"),
                "main-title.options",
                0.1,
                0.2,
                0.3,
                0.4)],
            ["enemies.crawlid"]);

        Equal(LabelingExampleManifest.CurrentSchemaVersion, example.Manifest.SchemaVersion);
        Equal("shared.options", example.Manifest.Annotations[0].ClassIdentifier);
        True(example.Manifest.EffectiveKnownClassIdentifiers.Contains("enemies.crawlid"));
        True(File.Exists(example.ImagePath));
        var loaded = store.Load();
        Equal(1, loaded.Count);
        Equal(example.Id, loaded[0].Id);
        Equal(createdAt, loaded[0].Manifest.CreatedAt);
        using var json = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(example.DirectoryPath, LabelingExampleStore.ManifestFilename)));
        Equal(JsonValueKind.Number, json.RootElement.GetProperty("schemaVersion").ValueKind);
        Equal("draft", json.RootElement.GetProperty("completion").GetString()!);

        var updated = store.Update(
            loaded[0],
            "game",
            [new LabelingExampleAnnotation(Guid.NewGuid(), "enemies.crawlid", 0, 0, 1, 1, true)]);
        True(updated.Manifest.Annotations[0].IsHardNegative);
        store.Delete(updated);
        Equal(0, store.Load().Count);
    });
}

static void TestLabelDatasetSplit()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new LabelingExampleStore(Path.Combine(workspace, "examples"));
        const string classId = "enemies.crawlid";
        var group = Guid.Parse("30000000-0000-4000-8000-000000000001");
        LabelingExampleAnnotation Positive(double x = 0.1) => new(
            Guid.NewGuid(), classId, x, 0.2, 0.25, 0.3);
        var groupedPositive = store.Save(Frame(1), "game", [Positive()], captureGroupIdentifier: group);
        var groupedEmpty = store.Save(
            Frame(2), "game", [], [classId], captureGroupIdentifier: group);
        var duplicateOne = store.Save(Frame(3), "game", [Positive(0.2)]);
        var duplicateTwo = store.Save(Frame(3), "game", [Positive(0.3)]);
        var independent = store.Save(Frame(4), "game", [Positive(0.4)]);
        var hardNegative = store.Save(
            Frame(5),
            "game",
            [new LabelingExampleAnnotation(Guid.NewGuid(), classId, 0.1, 0.1, 0.2, 0.2, true)]);
        var datasetId = Guid.Parse("40000000-0000-4000-8000-000000000001");
        var exporter = new LabelingDatasetExporter(
            Path.Combine(workspace, "training-v1", "datasets"),
            () => datasetId,
            () => new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero));
        var snapshot = exporter.Export(
            LabelingDatasetExporter.SharedObjectModelIdentifier,
            [classId],
            store.Load());

        Equal(6, snapshot.Manifest.Items.Count);
        var byId = snapshot.Manifest.Items.ToDictionary(item => item.ExampleIdentifier);
        Equal(byId[groupedPositive.Id].Split, byId[groupedEmpty.Id].Split);
        Equal(byId[duplicateOne.Id].Split, byId[duplicateTwo.Id].Split);
        Equal(LabelingDatasetSplit.Training, byId[hardNegative.Id].Split);
        True(snapshot.Manifest.Items.Any(item => item.Split == LabelingDatasetSplit.Validation));
        True(snapshot.Manifest.Items.Any(item => item.Split == LabelingDatasetSplit.Training
            && item.AnnotationCount > 0));
        True(File.Exists(Path.Combine(
            workspace,
            "training-v1",
            "split-assignments.json")));

        var item = byId[independent.Id];
        var annotationsPath = Path.Combine(
            snapshot.DirectoryPath,
            item.Split == LabelingDatasetSplit.Training ? "training" : "validation",
            LabelingDatasetExporter.AnnotationsFilename);
        using var annotations = JsonDocument.Parse(File.ReadAllBytes(annotationsPath));
        var image = annotations.RootElement.EnumerateArray().Single(element =>
            element.GetProperty("image").GetString() == item.ImageFilename);
        var coordinates = image.GetProperty("annotations")[0].GetProperty("coordinates");
        Close(1.05, coordinates.GetProperty("x").GetDouble());
        Close(0.7, coordinates.GetProperty("y").GetDouble());
        Close(0.5, coordinates.GetProperty("width").GetDouble());
        Close(0.6, coordinates.GetProperty("height").GetDouble());
    });
}

static void TestLabelDatasetNeedsPositive()
{
    WithTemporaryDirectory(workspace =>
    {
        const string classId = "enemies.vengfly";
        var store = new LabelingExampleStore(Path.Combine(workspace, "examples"));
        store.Save(
            Frame(8),
            "game",
            [new LabelingExampleAnnotation(Guid.NewGuid(), classId, 0, 0, 1, 1, true)]);
        var exporter = new LabelingDatasetExporter(Path.Combine(workspace, "datasets"));
        var threw = false;
        try
        {
            _ = exporter.Export("shared-object-model", [classId], store.Load());
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        True(threw);
    });
}

static void TestCameraContinuity()
{
    var continuity = new LiveRegistrationContinuity();
    var weak = new CameraUpdate(
        CameraUpdateState.HeldLowConfidence,
        CameraVector.Zero,
        CameraVector.Zero,
        new AtlasPoint(0, 0));
    True(!continuity.ShouldEnterRecovery(weak, true));
    True(!continuity.ShouldEnterRecovery(null, true));
    True(continuity.ShouldEnterRecovery(weak, true));
    Equal(3, continuity.ConsecutiveSoftFailures);
    continuity.Reset();
    var jump = weak with { State = CameraUpdateState.HeldSceneChange };
    True(continuity.ShouldEnterRecovery(jump, true));
}

static void TestFreshAtlasBootstrap()
{
    var gate = new FreshAtlasBootstrapGate();
    for (var index = 0; index < FreshAtlasBootstrapGate.RequiredStableSamples - 1; index++)
    {
        True(!gate.AllowsFirstAtlasWrite(
            new AtlasPoint(index % 3 - 1, index % 2),
            true,
            true,
            false));
    }
    True(gate.AllowsFirstAtlasWrite(new AtlasPoint(0, 0), true, true, false));
    True(gate.IsReady);

    gate.Reset();
    for (var index = 0; index < FreshAtlasBootstrapGate.RequiredStableSamples * 2; index++)
    {
        True(!gate.AllowsFirstAtlasWrite(new AtlasPoint(index, 0), true, true, false));
    }
    True(!gate.IsReady);
    True(gate.StableSampleCount <= (int)FreshAtlasBootstrapGate.MaximumBootstrapDisplacement + 1);
}

static void TestCameraAccumulator()
{
    var accumulator = new CameraAccumulator { Smoothing = 1 };
    var first = accumulator.Ingest(4, -2, 0.8f, 960);
    var second = accumulator.Ingest(6, 1, 0.9f, 960);
    Equal(CameraUpdateState.Accepted, first.State);
    Close(10, second.Position.X);
    Close(-1, second.Position.Y);
    Equal(3, accumulator.Samples.Count);
    var jump = accumulator.Ingest(0, -150, 1, 960);
    Equal(CameraUpdateState.HeldSceneChange, jump.State);
    Close(10, jump.Position.X);
    Close(-1, jump.Position.Y);
    accumulator.ApplyGlobalOffset(new CameraVector(-7, 3));
    Close(3, accumulator.Position.X);
    Close(2, accumulator.Position.Y);
}

static void TestRejectedFastFallCannotReceiveAtlasWriteAdmission()
{
    var rejected = new CameraUpdate(
        CameraUpdateState.HeldSceneChange,
        new CameraVector(0, 120),
        CameraVector.Zero,
        new AtlasPoint(40, 10));
    var accepted = new CameraUpdate(
        CameraUpdateState.Accepted,
        new CameraVector(2, 1),
        new CameraVector(2, 1),
        new AtlasPoint(42, 11));

    True(LiveRegistrationAdmission.RequiresRecovery(rejected));
    True(!LiveRegistrationAdmission.CanWriteAtlas(
        rejected,
        WorldBasisTrackingState.Tracking));
    True(!LiveRegistrationAdmission.CanWriteAtlas(
        accepted,
        WorldBasisTrackingState.Recovering));
    True(LiveRegistrationAdmission.CanWriteAtlas(
        accepted,
        WorldBasisTrackingState.Tracking));
}

static void TestViewportModeChangesOnlyThroughExplicitFraming()
{
    var coordinator = new GameplayViewportController();
    var viewport = new ViewportSize(800, 600);
    _ = coordinator.Configure(
        "Free Fly",
        viewport,
        fitBounds: null,
        followBounds: null,
        focus: null,
        timestamp: 0,
        explicitRequest: true);
    Equal("Free Fly", coordinator.Mode);
    _ = coordinator.Advance(1);
    Equal("Free Fly", coordinator.Mode);
    _ = coordinator.Configure(
        "Current",
        viewport,
        fitBounds: null,
        followBounds: new ViewportRect(0, 0, 640, 360),
        focus: new ViewportPoint(320, 180),
        timestamp: 2,
        explicitRequest: true);
    Equal("Current", coordinator.Mode);
}

static void TestParallaxGainScalesInverseWorldPlacement()
{
    var offset = WorldPlacement.Offset(
        new AtlasPoint(30, -12),
        new AtlasPoint(10, -2),
        presentationWidth: 640,
        solveWidth: 960,
        gain: 1.5);
    CloseWithin(20, offset.X, .001);
    CloseWithin(-10, offset.Y, .001);
}

static void TestFeatureTrackerCapsAndMaturesStrongFeatures()
{
    var tracker = new PersistentFeatureTracker();
    var result = MaturePersistentFeatures(tracker);
    Equal(PersistentFeatureTracker.TargetFeatureCount, result.Features.Count);
    True(result.LandmarkCount > 20);
    True(result.Features.Any(value => value.Kind == MapFeatureKind.Landmark));
    True(result.Features
        .Where(value => value.Kind == MapFeatureKind.Landmark)
        .All(value =>
            Math.Abs(value.Depth - 1) < .2
            && value.DepthConfidence >= .12));
}

static void TestStableFeaturesEnterWorldAtlasBeforeDepthMotion()
{
    var tracker = new PersistentFeatureTracker();
    var frame = PersistentFeaturePatternFrame(0);
    var initial = tracker.Update(frame, default, 256, []);
    True(!initial.DidCorrectCameraPose);
    Equal(CameraVector.Zero, initial.CameraPoseCorrection);
    Equal(0, initial.CameraPoseCorrectionSupport);

    var result = FeatureTrackingResult.Empty;
    for (var index = 0; index < 5; index++)
        result = tracker.Update(frame, default, 256, []);

    Equal(PersistentFeatureTracker.TargetFeatureCount, result.Features.Count);
    True(result.LandmarkCount > 30);
    True(result.Features.All(value => value.Kind == MapFeatureKind.Landmark));
    True(result.Features.All(value => value.DepthConfidence == 0));
}

static void TestFeatureAtlasRelocalizesAndCorrectsBacktrackingDrift()
{
    var tracker = new PersistentFeatureTracker();
    var mapped = MaturePersistentFeatures(tracker);
    var mappedLandmarks = mapped.LandmarkCount;
    _ = tracker.Update(
        PersistentFeaturePatternFrame(0),
        new AtlasPoint(300, 0),
        256,
        []);
    var returned = tracker.Update(
        PersistentFeaturePatternFrame(-2),
        new AtlasPoint(14, 0),
        256,
        []);

    Equal(PersistentFeatureTracker.TargetFeatureCount, returned.Features.Count);
    True(returned.RelocalizedCount > 2);
    True(returned.LandmarkCount >= mappedLandmarks);
    CloseWithin(2, returned.CameraPosition.X, 2);
    True(returned.DidCorrectCameraPose);
    True(returned.CameraPoseCorrection.X < -8);
    True(returned.CameraPoseCorrectionSupport >= 3);
}

static void TestLoopClosureNormalizesCameraCorrectionByFeatureDepth()
{
    var tracker = new PersistentFeatureTracker();
    var mapped = FeatureTrackingResult.Empty;
    foreach (var position in new[] { 0, 2, 4, 2, 0, 2, 4, 2, 0, 2 })
    {
        mapped = tracker.Update(
            PersistentFeaturePatternFrame(-position * 2),
            new AtlasPoint(position, 0),
            256,
            []);
    }
    True(mapped.LandmarkCount > 20);
    True(mapped.Features.Count(value =>
        value.Kind == MapFeatureKind.Landmark
        && value.DepthConfidence >= .05
        && Math.Abs(value.Depth - 2) < .25) > 20);

    _ = tracker.Update(
        PersistentFeaturePatternFrame(0),
        new AtlasPoint(300, 0),
        256,
        []);
    var returned = tracker.Update(
        PersistentFeaturePatternFrame(-4),
        new AtlasPoint(8, 0),
        256,
        []);
    True(returned.RelocalizedCount > 2);
    CloseWithin(2, returned.CameraPosition.X, 2);
}

static void TestFeatureLandmarksFollowSolvedCameraMotion()
{
    var tracker = new PersistentFeatureTracker();
    var mapped = MaturePersistentFeatures(tracker);
    var moved = tracker.Update(
        PersistentFeaturePatternFrame(-4),
        new AtlasPoint(4, 0),
        256,
        []);
    Equal(PersistentFeatureTracker.TargetFeatureCount, moved.Features.Count);
    True(moved.LandmarkCount >= mapped.LandmarkCount);
    CloseWithin(4, moved.CameraPosition.X, 1.5);
    True(moved.Features.Count(value => value.Kind == MapFeatureKind.Landmark) > 20);
}

static void TestFeatureDestroyedInsideViewIsRemovedFromAtlas()
{
    var tracker = new PersistentFeatureTracker();
    var mapped = MaturePersistentFeatures(tracker);
    var frame = PersistentFeaturePatternFrame(-2);
    var feature = mapped.Features.First(value => value.Kind == MapFeatureKind.Landmark);
    var destroyedFrame = PersistentFeatureBlackPatch(
        frame,
        new StencilRect(feature.Point.X - 12, feature.Point.Y - 12, 24, 24));
    var after = mapped;
    for (var index = 0; index < 7; index++)
    {
        after = tracker.Update(
            destroyedFrame,
            new AtlasPoint(2, 0),
            256,
            []);
    }
    True(!after.Features.Any(value => value.ID == feature.ID));
    True(!after.WorldFeatures.Any(value => value.ID == feature.ID));
}

static void TestLocallyAnimatedFeatureIsRejectedAsMotionOutlier()
{
    var tracker = new PersistentFeatureTracker();
    var mapped = MaturePersistentFeatures(tracker);
    var frame = PersistentFeaturePatternFrame(-2);
    var feature = mapped.Features.First(value =>
        value.Kind == MapFeatureKind.Landmark
        && value.Point.X > 24
        && value.Point.X < 228);
    var animatedFrame = PersistentFeatureMovedPatch(
        frame,
        new StencilRect(feature.Point.X - 9, feature.Point.Y - 9, 18, 18),
        deltaX: 4);
    var after = mapped;
    for (var index = 0; index < 7; index++)
    {
        after = tracker.Update(
            animatedFrame,
            new AtlasPoint(2, 0),
            256,
            []);
    }
    True(!after.Features.Any(value => value.ID == feature.ID));
}

static void TestLiveFeatureMarkerIsHiddenInsideKnightBox()
{
    const int width = 80;
    const int height = 50;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var offset = 0; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
    {
        pixels[offset] = 20;
        pixels[offset + 1] = 13;
        pixels[offset + 2] = 8;
        pixels[offset + 3] = 255;
    }
    var frame = new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        pixels,
        1,
        DateTimeOffset.UnixEpoch);
    var sourcePixels = frame.Pixels.ToArray();
    var regions = new SceneRegions(
        new StencilRect(30, 15, 20, 20),
        default,
        default,
        default);
    var tracking = new FeatureTrackingResult(
        [
            new MapFeature(1, new AtlasPoint(40, 25), 1, 1, 1, MapFeatureKind.Landmark),
            new MapFeature(2, new AtlasPoint(65, 25), 1, 1, 1, MapFeatureKind.Landmark)
        ],
        [],
        default,
        2,
        0,
        CameraVector.Zero,
        0);

    var annotated = FrameRegionRenderer.AnnotatedFrame(
        frame,
        regions,
        tracking,
        showFeatures: true);
    var hiddenMarkerPixel = 25 * annotated.Stride + 36 * BgraFrame.BytesPerPixel;
    True(annotated.Pixels[hiddenMarkerPixel + 1] < 80);
    True(annotated.Pixels[hiddenMarkerPixel] < 80);

    var visibleMarkerPixel = 25 * annotated.Stride + 61 * BgraFrame.BytesPerPixel;
    True(annotated.Pixels[visibleMarkerPixel + 1] > 200);
    True(annotated.Pixels[visibleMarkerPixel] > 200);
    True(frame.Pixels.SequenceEqual(sourcePixels));
}

static FeatureTrackingResult MaturePersistentFeatures(PersistentFeatureTracker tracker)
{
    var result = FeatureTrackingResult.Empty;
    foreach (var position in new[] { 0, 2, 4, 2, 0, 2, 4, 2, 0, 2 })
    {
        result = tracker.Update(
            PersistentFeaturePatternFrame(-position),
            new AtlasPoint(position, 0),
            256,
            []);
    }
    return result;
}

static BgraFrame PersistentFeaturePatternFrame(
    int horizontalShift,
    int verticalShift = 0)
{
    const int width = 256;
    const int height = 128;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var sourceX = x - horizontalShift;
        var sourceY = y - verticalShift;
        var offset = (y * width + x) * BgraFrame.BytesPerPixel;
        pixels[offset + 3] = 255;
        var firstShape = sourceX is >= 28 and < 83
            && sourceY is >= 19 and < 54;
        var secondShape = sourceX is >= 126 and < 211
            && sourceY is >= 67 and < 108;
        var narrowEdge = sourceX is >= 96 and < 104
            && sourceY is >= 35 and < 119;
        if (firstShape)
        {
            pixels[offset] = 44;
            pixels[offset + 1] = 192;
            pixels[offset + 2] = 238;
        }
        else if (secondShape)
        {
            pixels[offset] = 219;
            pixels[offset + 1] = 91;
            pixels[offset + 2] = 63;
        }
        else if (narrowEdge)
        {
            pixels[offset] = 242;
            pixels[offset + 1] = 231;
            pixels[offset + 2] = 116;
        }
        else
        {
            var texture = (byte)((
                sourceX * 7 + sourceY * 13 + sourceX * sourceY) & 31);
            pixels[offset] = (byte)(8 + texture);
            pixels[offset + 1] = (byte)(12 + texture);
            pixels[offset + 2] = (byte)(18 + texture);
        }
    }
    return new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        pixels,
        captureId: 0,
        observedAt: DateTimeOffset.UnixEpoch);
}

static BgraFrame PersistentFeatureBlackPatch(BgraFrame source, StencilRect patch)
{
    var pixels = source.Pixels.ToArray();
    PersistentFeaturePixelBounds(source, patch, (x0, y0, x1, y1) =>
    {
        for (var presentationY = y0; presentationY < y1; presentationY++)
        for (var x = x0; x < x1; x++)
        {
            var row = source.Height - 1 - presentationY;
            var offset = row * source.Stride + x * BgraFrame.BytesPerPixel;
            pixels[offset] = 0;
            pixels[offset + 1] = 0;
            pixels[offset + 2] = 0;
            pixels[offset + 3] = 255;
        }
    });
    return new BgraFrame(
        source.Width,
        source.Height,
        source.Stride,
        pixels,
        source.CaptureId,
        source.ObservedAt);
}

static BgraFrame PersistentFeatureMovedPatch(
    BgraFrame source,
    StencilRect patch,
    int deltaX)
{
    var original = source.Pixels.ToArray();
    var pixels = source.Pixels.ToArray();
    PersistentFeaturePixelBounds(source, patch, (x0, y0, x1, y1) =>
    {
        for (var presentationY = y0; presentationY < y1; presentationY++)
        for (var x = x0; x < x1; x++)
        {
            var row = source.Height - 1 - presentationY;
            var offset = row * source.Stride + x * BgraFrame.BytesPerPixel;
            pixels[offset] = 0;
            pixels[offset + 1] = 0;
            pixels[offset + 2] = 0;
            pixels[offset + 3] = 255;
        }
        for (var presentationY = y0; presentationY < y1; presentationY++)
        for (var x = x0; x < x1; x++)
        {
            var destinationX = x + deltaX;
            if ((uint)destinationX >= (uint)source.Width) continue;
            var row = source.Height - 1 - presentationY;
            var sourceOffset = row * source.Stride + x * BgraFrame.BytesPerPixel;
            var destinationOffset = row * source.Stride
                + destinationX * BgraFrame.BytesPerPixel;
            Array.Copy(
                original,
                sourceOffset,
                pixels,
                destinationOffset,
                BgraFrame.BytesPerPixel);
        }
    });
    return new BgraFrame(
        source.Width,
        source.Height,
        source.Stride,
        pixels,
        source.CaptureId,
        source.ObservedAt);
}

static void PersistentFeaturePixelBounds(
    BgraFrame frame,
    StencilRect patch,
    Action<int, int, int, int> apply)
{
    var x0 = Math.Clamp((int)Math.Floor(patch.X), 0, frame.Width);
    var x1 = Math.Clamp((int)Math.Ceiling(patch.Right), 0, frame.Width);
    var y0 = Math.Clamp((int)Math.Floor(patch.Y), 0, frame.Height);
    var y1 = Math.Clamp((int)Math.Ceiling(patch.Bottom), 0, frame.Height);
    apply(x0, y0, x1, y1);
}

static void TestBlackoutWhileStandingResumesSameRoom()
{
    var tracker = new VisualRoomTransitionTracker();
    _ = tracker.Observe(
        RoomBlack(), RoomHeld(), null, null, RoomFrameSize(),
        default, 640, 1);
    for (var index = 0; index < 3; index++)
    {
        _ = tracker.Observe(
            RoomVisible(), index == 2 ? RoomResumed() : RoomAdmitted(),
            null, null, RoomFrameSize(), default, 640, 2 + index * .1);
    }
    Equal(0, tracker.Snapshot.RoomID);
    Equal(0UL, tracker.Snapshot.Revision);
}

static void TestLeftDepartureCreatesRoomToLeftAfterStableArrival()
{
    var tracker = new VisualRoomTransitionTracker();
    var departure = new AtlasPoint(-120, 8);
    _ = tracker.Observe(
        RoomDoorwayProfile(RoomVisible(), 0), RoomAdmitted(),
        VisualRoomDirection.Left, RoomLeftKnight(), RoomFrameSize(),
        departure, 640, 1);
    var pending = tracker.Observe(
        RoomDoorwayProfile(RoomBlack(), 4), RoomHeld(),
        VisualRoomDirection.Left, null, RoomFrameSize(),
        departure, 640, 1.1);
    True(pending.IsTransitioning);
    for (var index = 0; index < 3; index++)
    {
        _ = tracker.Observe(
            RoomDoorwayProfile(RoomVisible(), 4),
            index == 2 ? RoomResumed() : RoomHeld(),
            null, RoomRightKnight(), RoomFrameSize(), departure,
            640, 2 + index * .1);
    }
    var snapshot = tracker.Snapshot;
    Equal(1, snapshot.RoomID);
    Equal("Room 2", snapshot.RoomName);
    Close(departure.X - 80, snapshot.EntryWorldPose.X);
    Close(departure.Y, snapshot.EntryWorldPose.Y);
    Equal(1, snapshot.Portals.Count);
    var portal = snapshot.Portals[0];
    Equal(1, portal.LeftRoomID);
    Equal(0, portal.RightRoomID);
    Close(departure.X + 272, portal.LeftDoorWorldX);
    Close(departure.X + 288, portal.RightDoorWorldX);
    True(snapshot.CompositionBounds.MinimumWorldX is null);
    Close(departure.X + 272, snapshot.CompositionBounds.MaximumWorldX!.Value);
}

static void TestKnownRoomCannotReactivateAwayFromRecordedDoorway()
{
    var tracker = new VisualRoomTransitionTracker();
    var doorway = new AtlasPoint(-120, 0);
    RoomTransitionAppearance(
        tracker, RoomVisible(), RoomNearBlack(),
        VisualRoomDirection.Left, doorway, 1);
    RoomTransitionAppearance(
        tracker, RoomNearBlack(), RoomVisible(),
        VisualRoomDirection.Right, tracker.Snapshot.EntryWorldPose, 2);
    Equal(0, tracker.Snapshot.RoomID);

    RoomTransitionAppearance(
        tracker, RoomVisible(), RoomNearBlack(),
        VisualRoomDirection.Right, new AtlasPoint(800, 0), 3);
    Equal(0, tracker.Snapshot.RoomID);
    Equal(2UL, tracker.Snapshot.Revision);
}

static void TestDoorwayTopologyPersistsAndRestores()
{
    WithTemporaryDirectory(directory =>
    {
        var tracker = new VisualRoomTransitionTracker();
        var doorway = new AtlasPoint(-120, 8);
        RoomTraverse(
            tracker, VisualRoomDirection.Left, RoomLeftKnight(), doorway, 1);
        var path = Path.Combine(directory, "visual-rooms.json");
        tracker.SaveTopology(path);

        var restored = new VisualRoomTransitionTracker();
        var snapshot = restored.RestoreTopology(path);
        Equal(1, snapshot.RoomID);
        Equal(tracker.Snapshot.EntryWorldPose, snapshot.EntryWorldPose);
        True(snapshot.Portals.SequenceEqual(tracker.Snapshot.Portals));
        Equal(tracker.Snapshot.CompositionBounds, snapshot.CompositionBounds);
    });
}

static void TestCompositionMaskCropsPixelsAcrossDoorway()
{
    var leftCrop = VisualRoomCompositionMask.OmittedRects(
        new VisualRoomCompositionBounds(0, null),
        new AtlasPoint(-100, 0),
        640,
        RoomFrameSize());
    True(leftCrop.SequenceEqual([new StencilRect(0, 0, 100, 360)]));

    var rightCrop = VisualRoomCompositionMask.OmittedRects(
        new VisualRoomCompositionBounds(null, 0),
        new AtlasPoint(-500, 0),
        640,
        RoomFrameSize());
    True(rightCrop.SequenceEqual([new StencilRect(500, 0, 140, 360)]));
}

static void TestBlackBandsDefineSeparateDoorEdgesAndShorterDoorHeights()
{
    var profile = FrameRegionRenderer.SignalProfile(RoomDoorwayImage());
    Equal(64, profile.MotionGrid!.Width);
    Equal(36, profile.MotionGrid.Height);
    var left = profile.EdgeBlackBands!.Left!;
    var right = profile.EdgeBlackBands.Right!;
    CloseWithin(.09375, left.WidthFraction, .02);
    CloseWithin(.453125, right.WidthFraction, .02);
    True(left.ContentYRangeFraction.Upper
        - left.ContentYRangeFraction.Lower < .5);

    var layout = VisualRoomPortalLayout.Make(
        VisualRoomDirection.Left,
        new AtlasPoint(-186, 0),
        640,
        360,
        left,
        right,
        null);
    CloseWithin(-126, layout.SourceDoorWorldX, 13);
    CloseWithin(-142, layout.TargetDoorWorldX, 13);
    CloseWithin(-492, layout.TargetEntryWorldPose.X, 20);
    True(layout.SourceDoorYRange.Upper
        - layout.SourceDoorYRange.Lower < 121);
    Equal(layout.SourceDoorYRange, layout.TargetDoorYRange);
}

static void TestMeasuredDoorwayTravelControlsConnectorLength()
{
    var source = new FrameEdgeBlackBand(
        .10, new FrameFractionRange(.4, .7));
    var target = new FrameEdgeBlackBand(
        .45, new FrameFractionRange(.45, .7));
    var layout = VisualRoomPortalLayout.Make(
        VisualRoomDirection.Left,
        default,
        640,
        360,
        source,
        target,
        new LowResolutionTransitionTravel(-400, 4, .7));
    CloseWithin(112,
        layout.SourceDoorWorldX - layout.TargetDoorWorldX, .001);
    CloseWithin(-400, layout.TargetEntryWorldPose.X, .001);
}

static void TestHorizontalDoorRejectsImplausibleContinuousVerticalArrival()
{
    var departure = new AtlasPoint(2_000, 1_000);
    var rejected = VisualRoomPortalLayout.PlausibleContinuousArrival(
        new AtlasPoint(2_356, 2_070),
        VisualRoomDirection.Right,
        departure,
        640,
        360);
    True(rejected is null);

    var layout = VisualRoomPortalLayout.Make(
        VisualRoomDirection.Right,
        departure,
        640,
        360,
        null,
        null,
        null,
        rejected);
    CloseWithin(departure.Y, layout.TargetEntryWorldPose.Y, .001);
    True(layout.TargetEntryWorldPose.X > departure.X);
}

static void TestHorizontalDoorAcceptsRouteCalibratedContinuousArrival()
{
    var departure = new AtlasPoint(2_000, 1_000);
    var expected = new AtlasPoint(2_541.308, 944.448);
    Equal(expected, VisualRoomPortalLayout.PlausibleContinuousArrival(
        expected,
        VisualRoomDirection.Right,
        departure,
        640,
        360)!.Value);
}

static void TestKnownAppearancePortalRequiresItsRecordedDirection()
{
    var tracker = new VisualRoomTransitionTracker();
    var departure = new AtlasPoint(-120, 0);
    RoomTransitionAppearance(
        tracker, RoomVisible(), RoomNearBlack(),
        VisualRoomDirection.Left, departure, 1);
    Equal(1, tracker.Snapshot.RoomID);

    RoomTransitionAppearance(
        tracker, RoomNearBlack(), RoomVisible(),
        VisualRoomDirection.Left, tracker.Snapshot.EntryWorldPose, 2);
    Equal(1, tracker.Snapshot.RoomID);
    Equal(1UL, tracker.Snapshot.Revision);

    RoomTransitionAppearance(
        tracker, RoomNearBlack(), RoomVisible(),
        VisualRoomDirection.Right, tracker.Snapshot.EntryWorldPose, 3);
    Equal(0, tracker.Snapshot.RoomID);
    Equal(2UL, tracker.Snapshot.Revision);
}

static void TestReverseTraversalReusesFirstRoomAndPortalPose()
{
    var tracker = new VisualRoomTransitionTracker();
    var departure = new AtlasPoint(-120, 8);
    RoomTraverse(
        tracker, VisualRoomDirection.Left, RoomLeftKnight(), departure, 1);
    Equal(1, tracker.Snapshot.RoomID);
    var secondRoomPose = tracker.Snapshot.EntryWorldPose;
    var portalGeometry = tracker.Snapshot.Portals.ToArray();
    RoomTraverse(
        tracker, VisualRoomDirection.Right, RoomRightKnight(), secondRoomPose, 4);
    Equal(0, tracker.Snapshot.RoomID);
    Equal(departure, tracker.Snapshot.EntryWorldPose);
    Equal(2UL, tracker.Snapshot.Revision);
    True(tracker.Snapshot.Portals.SequenceEqual(portalGeometry));
}

static void TestRestartPrefersOnlyRoomRepresentedByDurableEvidence()
{
    var tracker = new VisualRoomTransitionTracker();
    var departure = new AtlasPoint(-120, 8);
    RoomTraverse(
        tracker, VisualRoomDirection.Left, RoomLeftKnight(), departure, 1);
    var portalGeometry = tracker.Snapshot.Portals.ToArray();
    Equal(1, tracker.Snapshot.RoomID);

    var restored = tracker.RestoreActiveRoomFromDurableEvidence(0);
    Equal(0, restored.RoomID);
    Equal(departure, restored.EntryWorldPose);
    True(restored.Portals.SequenceEqual(portalGeometry));
    Equal(1UL, restored.Revision);
}

static void RoomTraverse(
    VisualRoomTransitionTracker tracker,
    VisualRoomDirection direction,
    StencilRect edge,
    AtlasPoint pose,
    double start)
{
    _ = tracker.Observe(
        RoomDoorwayProfile(RoomVisible(), 0), RoomAdmitted(),
        direction, edge, RoomFrameSize(), pose, 640, start);
    _ = tracker.Observe(
        RoomDoorwayProfile(
            RoomBlack(), direction == VisualRoomDirection.Left ? 4 : -4),
        RoomHeld(), direction, null, RoomFrameSize(), pose, 640, start + .1);
    for (var index = 0; index < 3; index++)
    {
        _ = tracker.Observe(
            RoomDoorwayProfile(
                RoomVisible(), direction == VisualRoomDirection.Left ? 4 : -4),
            index == 2 ? RoomResumed() : RoomHeld(),
            null,
            direction == VisualRoomDirection.Left
                ? RoomRightKnight()
                : RoomLeftKnight(),
            RoomFrameSize(),
            pose,
            640,
            start + .2 + index * .1);
    }
}

static void RoomTransitionAppearance(
    VisualRoomTransitionTracker tracker,
    FrameSignalProfile source,
    FrameSignalProfile target,
    VisualRoomDirection heldDirection,
    AtlasPoint pose,
    double start)
{
    _ = tracker.Observe(
        RoomDoorwayProfile(source, 0),
        RoomAdmitted(),
        heldDirection,
        heldDirection == VisualRoomDirection.Left
            ? RoomLeftKnight()
            : RoomRightKnight(),
        RoomFrameSize(),
        pose,
        640,
        start);
    for (var index = 0;
         index < VisualRoomTransitionTracker.AppearanceStableFrames;
         index++)
    {
        var departureEdge = heldDirection == VisualRoomDirection.Left
            ? RoomLeftKnight()
            : RoomRightKnight();
        var arrivalEdge = heldDirection == VisualRoomDirection.Left
            ? RoomRightKnight()
            : RoomLeftKnight();
        _ = tracker.Observe(
            RoomDoorwayProfile(
                target, heldDirection == VisualRoomDirection.Left ? 4 : -4),
            RoomAdmitted(),
            heldDirection,
            index == 0 ? departureEdge : arrivalEdge,
            RoomFrameSize(),
            pose,
            640,
            start + .1 + index / 60d);
    }
}

static FrameSignalProfile RoomDoorwayProfile(
    FrameSignalProfile profile,
    int shift)
{
    var band = new FrameEdgeBlackBand(
        .45, new FrameFractionRange(.35, .65));
    return new FrameSignalProfile(
        profile.SampleCount,
        profile.SignaledCount,
        profile.VisibleCount,
        profile.MeanPeak,
        MotionGrid(shift),
        new FrameEdgeBlackBands(band, band));
}

static BgraFrame RoomDoorwayImage()
{
    const int width = 640;
    const int height = 360;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var y = 70; y < 290; y++)
    for (var x = 60; x < 350; x++)
    {
        var offset = (y * width + x) * BgraFrame.BytesPerPixel;
        pixels[offset] = 96;
        pixels[offset + 1] = 52;
        pixels[offset + 2] = 24;
        pixels[offset + 3] = 255;
    }
    return new BgraFrame(
        width, height, width * BgraFrame.BytesPerPixel,
        pixels, 1, DateTimeOffset.UnixEpoch);
}

static WorldSize RoomFrameSize() => new(640, 360);
static StencilRect RoomLeftKnight() => new(10, 100, 40, 50);
static StencilRect RoomRightKnight() => new(590, 100, 40, 50);
static FrameSignalProfile RoomVisible() => new(1_000, 800, 700, 80);
static FrameSignalProfile RoomNearBlack() => new(1_000, 80, 60, 8);
static FrameSignalProfile RoomBlack() => new(1_000, 0, 0, 0);
static TransitionFrameDecision RoomAdmitted() => new(true, false);
static TransitionFrameDecision RoomHeld() => new(false, false);
static TransitionFrameDecision RoomResumed() => new(true, true);

static void TestFloorlessTurnaroundRecognizesRoomOneBeforePlacingNextRoomLeft()
{
    var tracker = new VisualRoomTransitionTracker();
    var start = new AtlasPoint(100, 40);
    var initial = VisibilityCircle(
        FractionalMotionGrid(0),
        radius: 29,
        centerX: 32,
        centerY: 18);
    _ = tracker.Observe(
        MotionProfile(initial),
        RoomAdmitted(),
        null,
        new StencilRect(300, 150, 40, 40),
        RoomFrameSize(),
        start,
        640,
        0,
        groundTrackingReliable: true);

    var final = tracker.Snapshot;
    LowResolutionPlaceMatch? returnPlaceMatch = null;
    for (var index = 1; index <= 26; index++)
    {
        var outwardProgress = index <= 12 ? index : 24 - index;
        var direction = index <= 12
            ? VisualRoomDirection.Left
            : VisualRoomDirection.Right;
        final = tracker.Observe(
            MotionProfile(VisibilityCircle(
                FractionalMotionGrid(.9 * outwardProgress),
                radius: 29 - .45 * Math.Max(0, outwardProgress),
                centerX: 32 - .25 * Math.Max(0, outwardProgress),
                centerY: 18)),
            RoomAdmitted(),
            direction,
            new StencilRect(300, 150, 40, 40),
            RoomFrameSize(),
            start,
            640,
            index * .05,
            groundTrackingReliable: false);
        Equal(0, final.RoomID);
        True(!final.IsTransitioning);
        if (index > 12 && final.CoarsePlaceMatch is { } match)
            returnPlaceMatch = match;
    }

    Equal(0UL, final.Revision);
    Equal(0, final.Portals.Count);
    True(returnPlaceMatch is not null);
    Equal(1, returnPlaceMatch!.KeyframeID);
    CloseWithin(110, returnPlaceMatch.CameraPosition.X, .001);
    True(final.CoarseWorldPose is not null);
    CloseWithin(118, final.CoarseWorldPose!.Value.X, 2);
    CloseWithin(start.Y, final.CoarseWorldPose.Value.Y, 2);

    RoomTraverse(
        tracker,
        VisualRoomDirection.Left,
        RoomLeftKnight(),
        start,
        2);
    var placed = tracker.Snapshot;
    Equal(1, placed.RoomID);
    True(placed.EntryWorldPose.X < start.X);
    Equal(1, placed.Portals.Count);
    Equal(1, placed.Portals[0].LeftRoomID);
    Equal(0, placed.Portals[0].RightRoomID);
}

static void TestLowResolutionPlaceMemoryReanchorsRepeatedKnownViewWithinRoom()
{
    var memory = new LowResolutionPlaceMemory();
    memory.Remember(
        FractionalMotionGrid(0),
        0,
        new AtlasPoint(100, 40),
        0);
    var returned = FractionalMotionGrid(1, gain: .28);
    True(memory.Observe(returned, 0, 640, 1) is null);
    var match = memory.Observe(returned, 0, 640, 1.2);
    True(match is not null);
    Equal(1, match!.KeyframeID);
    CloseWithin(90, match.CameraPosition.X, .001);
    CloseWithin(40, match.CameraPosition.Y, .001);
    True(match.Score < .20);
}

static void TestLowResolutionPlaceMemoryReanchorsBrightViewThroughSmallVisibilityCircle()
{
    var memory = new LowResolutionPlaceMemory();
    memory.Remember(
        FractionalMotionGrid(0),
        0,
        new AtlasPoint(100, 40),
        0);
    True(memory.Observe(
        VisibilityCircle(FractionalMotionGrid(.9), radius: 10),
        0,
        640,
        1) is null);
    var match = memory.Observe(
        VisibilityCircle(FractionalMotionGrid(-.9), radius: 10),
        0,
        640,
        1.1);
    True(match is not null);
    Equal(1, match!.KeyframeID);
    CloseWithin(110, match.CameraPosition.X, .001);
    CloseWithin(40, match.CameraPosition.Y, .001);
}

static void TestLowResolutionPlaceMemoryRejectsTinyVisibilityCircle()
{
    var memory = new LowResolutionPlaceMemory();
    memory.Remember(FractionalMotionGrid(0), 0, default, 0);
    var tiny = VisibilityCircle(FractionalMotionGrid(0), radius: 2);
    True(memory.Observe(tiny, 0, 640, 1) is null);
    True(memory.Observe(tiny, 0, 640, 1.1) is null);
}

static void TestLowResolutionPlaceMemoryRejectsUnrelatedSmallVisibilityCircle()
{
    var memory = new LowResolutionPlaceMemory();
    memory.Remember(FractionalMotionGrid(0), 0, default, 0);
    var unrelated = VisibilityCircle(UnrelatedMotionGrid(), radius: 10);
    True(memory.Observe(unrelated, 0, 640, 1) is null);
    True(memory.Observe(unrelated, 0, 640, 1.1) is null);
}

static void TestLowResolutionPlaceMemoryRejectsAmbiguousSmallVisibilityCircle()
{
    var memory = new LowResolutionPlaceMemory();
    var repeatedTexture = FractionalMotionGrid(0);
    memory.Remember(repeatedTexture, 0, default, 0);
    memory.Remember(repeatedTexture, 0, new AtlasPoint(160, 0), .3);
    var partialView = VisibilityCircle(FractionalMotionGrid(.9), radius: 10);
    True(memory.Observe(partialView, 0, 640, 1) is null);
    True(memory.Observe(partialView, 0, 640, 1.1) is null);
}

static void TestLowResolutionPlaceMemoryDoesNotCrossRoomOwnership()
{
    var memory = new LowResolutionPlaceMemory();
    var view = FractionalMotionGrid(0);
    memory.Remember(view, 0, default, 0);
    True(memory.Observe(view, 1, 640, 1) is null);
    True(memory.Observe(view, 1, 640, 1.2) is null);
}

static void TestLowResolutionPlaceMemoryRejectsUnrelatedViewAndSelectsUniqueKeyframe()
{
    var memory = new LowResolutionPlaceMemory();
    var original = FractionalMotionGrid(0);
    var unrelated = UnrelatedMotionGrid();
    memory.Remember(original, 0, default, 0);
    memory.Remember(unrelated, 0, new AtlasPoint(160, 0), .3);
    True(memory.Observe(UnrelatedMotionGrid(seed: 97), 0, 640, 1) is null);
    True(memory.Observe(original, 0, 640, 1.2) is null);
    var match = memory.Observe(original, 0, 640, 1.4);
    True(match is not null);
    Equal(1, match!.KeyframeID);
    Equal(default(AtlasPoint), match.CameraPosition);
    True(match.Margin > LowResolutionPlaceMemory.MinimumMargin);
}

static void TestFloorlessTraversalPublishesRecognizedRoomOnePoseImmediately()
{
    var tracker = new VisualRoomTransitionTracker();
    var remembered = FractionalMotionGrid(0);
    _ = tracker.Observe(
        MotionProfile(remembered),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        new AtlasPoint(100, 40),
        640,
        0,
        groundTrackingReliable: true);
    var returned = FractionalMotionGrid(1, gain: .28);
    _ = tracker.Observe(
        MotionProfile(returned),
        RoomAdmitted(),
        VisualRoomDirection.Right,
        null,
        RoomFrameSize(),
        new AtlasPoint(500, 40),
        640,
        1,
        groundTrackingReliable: false);
    var recognized = tracker.Observe(
        MotionProfile(returned),
        RoomAdmitted(),
        VisualRoomDirection.Right,
        null,
        RoomFrameSize(),
        new AtlasPoint(500, 40),
        640,
        1.2,
        groundTrackingReliable: false);
    Equal(0, recognized.RoomID);
    True(recognized.CoarsePlaceMatch is not null);
    Equal(1, recognized.CoarsePlaceMatch!.KeyframeID);
    True(recognized.CoarseWorldPose is not null);
    CloseWithin(90, recognized.CoarseWorldPose!.Value.X, .001);
    CloseWithin(40, recognized.CoarseWorldPose.Value.Y, .001);
    True(recognized.CoarseMotionIsControlling);
}

static void TestMaskedCorrectionRebasesContinuingFloorlessPoseAndRejectsStaleRepeat()
{
    var tracker = new VisualRoomTransitionTracker();
    _ = tracker.Observe(
        MotionProfile(MotionGrid(0)),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        default,
        640,
        0,
        groundTrackingReliable: true);
    var measured = tracker.Observe(
        MotionProfile(MotionGrid(2)),
        RoomAdmitted(),
        VisualRoomDirection.Left,
        null,
        RoomFrameSize(),
        default,
        640,
        .1,
        groundTrackingReliable: false);
    var continued = tracker.Observe(
        MotionProfile(MotionGrid(4)),
        RoomAdmitted(),
        VisualRoomDirection.Left,
        null,
        RoomFrameSize(),
        new AtlasPoint(-40, 0),
        640,
        .2,
        groundTrackingReliable: false);
    True(measured.CoarseWorldPose is not null);
    True(continued.CoarseWorldPose is not null);
    CloseWithin(-40, measured.CoarseWorldPose!.Value.X, .001);
    CloseWithin(-80, continued.CoarseWorldPose!.Value.X, .001);

    True(tracker.ApplyFloorlessCorrection(
        new CameraVector(12, 5),
        measured.RoomID,
        measured.CoarsePoseRevision));
    True(tracker.Snapshot.CoarseWorldPose is not null);
    CloseWithin(-68, tracker.Snapshot.CoarseWorldPose!.Value.X, .001);
    CloseWithin(5, tracker.Snapshot.CoarseWorldPose.Value.Y, .001);
    True(!tracker.ApplyFloorlessCorrection(
        new CameraVector(12, 5),
        measured.RoomID,
        measured.CoarsePoseRevision));
    CloseWithin(-68, tracker.Snapshot.CoarseWorldPose!.Value.X, .001);
}

static void TestRoomOnePlaceMatchInvalidatesOlderMaskedCorrection()
{
    var tracker = new VisualRoomTransitionTracker();
    var remembered = FractionalMotionGrid(0);
    _ = tracker.Observe(
        MotionProfile(remembered),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        new AtlasPoint(100, 40),
        640,
        0,
        groundTrackingReliable: true);
    var returned = FractionalMotionGrid(1, gain: .28);
    var beforeMatch = tracker.Observe(
        MotionProfile(returned),
        RoomAdmitted(),
        VisualRoomDirection.Right,
        null,
        RoomFrameSize(),
        new AtlasPoint(500, 40),
        640,
        1,
        groundTrackingReliable: false);
    var recognized = tracker.Observe(
        MotionProfile(returned),
        RoomAdmitted(),
        VisualRoomDirection.Right,
        null,
        RoomFrameSize(),
        new AtlasPoint(500, 40),
        640,
        1.2,
        groundTrackingReliable: false);
    True(recognized.CoarseWorldPose is not null);
    CloseWithin(90, recognized.CoarseWorldPose!.Value.X, .001);
    True(!tracker.ApplyFloorlessCorrection(
        new CameraVector(100, 0),
        beforeMatch.RoomID,
        beforeMatch.CoarsePoseRevision));
    CloseWithin(90, tracker.Snapshot.CoarseWorldPose!.Value.X, .001);
}

static void TestFloorlessOdometryRejectsStrongHorizontalContradiction()
{
    var odometry = new LowResolutionTransitionOdometry();
    odometry.Arm(FractionalMotionGrid(0), timestamp: 0);
    True(odometry.Observe(
        FractionalMotionGrid(2),
        timestamp: .05,
        solveWidth: 640,
        expectedDirection: VisualRoomDirection.Right) is null);
    Equal(0, odometry.SampleCount);
}

static void TestFloorlessOdometryRetainsVerticalDominantMotionAgainstHeldInput()
{
    var odometry = new LowResolutionTransitionOdometry();
    odometry.Arm(FractionalMotionGrid(0), timestamp: 0);
    True(odometry.Observe(
        FractionalMotionGrid(2, verticalShift: 3),
        timestamp: .05,
        solveWidth: 640,
        expectedDirection: VisualRoomDirection.Right) is not null);
    CloseWithin(-20, odometry.WorldDeltaX, 2);
    CloseWithin(30, odometry.WorldDeltaY, 2);
}

static void TestFloorlessTraversalPublishesVerticalTravelWithoutHorizontalInput()
{
    var tracker = new VisualRoomTransitionTracker();
    var reference = FractionalMotionGrid(0);
    _ = tracker.Observe(
        MotionProfile(reference),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        new AtlasPoint(100, 40),
        640,
        0,
        groundTrackingReliable: true);
    var moved = tracker.Observe(
        MotionProfile(FractionalMotionGrid(0, verticalShift: 2)),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        new AtlasPoint(100, 40),
        640,
        .05,
        groundTrackingReliable: false);

    True(moved.CoarseMotionEstimate is null);
    True(moved.CoarseWorldPose is not null);
    CloseWithin(100, moved.CoarseWorldPose!.Value.X, 2);
    CloseWithin(60, moved.CoarseWorldPose.Value.Y, 2);
    True(moved.CoarseMotionIsControlling);
}

static void TestRouteCalibratedShortBaselineRetainsFastHorizontalTravel()
{
    var reference = FractionalMotionGrid(0);
    var translated = FractionalMotionGrid(-2.25, verticalShift: 3);
    var vector = LowResolutionRoomMotionTracker.Translation(reference, translated);
    True(vector is not null);
    CloseWithin(-2.25, vector!.ScreenShiftX, .18);
    CloseWithin(3, vector.ScreenShiftY, .18);

    var odometry = new LowResolutionTransitionOdometry();
    odometry.Arm(reference, timestamp: 0);
    CloseWithin(22.5, odometry.Observe(
        translated,
        timestamp: .05,
        solveWidth: 640,
        expectedDirection: VisualRoomDirection.Right) ?? double.NaN, 2);
    CloseWithin(30, odometry.WorldDeltaY, 2);
}

static void TestShortBaselineStillAccumulatesSlowSubcellTravel()
{
    var odometry = new LowResolutionTransitionOdometry();
    odometry.Arm(FractionalMotionGrid(0), timestamp: 0);
    for (var index = 1; index <= 20; index++)
    {
        True(odometry.Observe(
            FractionalMotionGrid(-.45 * index),
            timestamp: index * .05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) is not null);
    }
    Equal(20, odometry.SampleCount);
    CloseWithin(90, odometry.WorldDeltaX, 12);
}

static void TestCutsceneCameraWarpIsRejectedBeforeLocalMotionResumes()
{
    var odometry = new LowResolutionTransitionOdometry();
    odometry.Arm(FractionalMotionGrid(0), timestamp: 0);
    True(odometry.Observe(
        FractionalMotionGrid(20),
        timestamp: .05,
        solveWidth: 640,
        expectedDirection: VisualRoomDirection.Left) is null);
    Equal(0, odometry.SampleCount);
    Close(0, odometry.WorldDeltaX);

    CloseWithin(15, odometry.Observe(
        FractionalMotionGrid(18.5),
        timestamp: .10,
        solveWidth: 640,
        expectedDirection: VisualRoomDirection.Right) ?? double.NaN, 2);
    CloseWithin(15, odometry.WorldDeltaX, 2);
}

static void TestDoorwayOdometryAccumulatesLowResolutionSilhouetteTravel()
{
    var odometry = new LowResolutionTransitionOdometry();
    odometry.Arm(MotionGrid(0), timestamp: 0);
    Close(-40, odometry.Observe(
        MotionGrid(2),
        timestamp: .1,
        solveWidth: 640,
        expectedDirection: VisualRoomDirection.Left) ?? double.NaN);
    Close(-40, odometry.Observe(
        MotionGrid(4),
        timestamp: .2,
        solveWidth: 640,
        expectedDirection: VisualRoomDirection.Left) ?? double.NaN);
    Close(-80, odometry.Travel.WorldDeltaX);
    Equal(2, odometry.Travel.SampleCount);
}

static void TestCoarseMotionDiagnosticRunsContinuouslyAwayFromDoorway()
{
    static FrameSignalProfile Profile(LowResolutionMotionGrid grid) => new(
        1_000,
        800,
        700,
        80,
        grid);

    var tracker = new VisualRoomTransitionTracker();
    _ = tracker.Observe(
        Profile(MotionGrid(0)),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        default,
        640,
        1);
    var snapshot = tracker.Observe(
        Profile(MotionGrid(2)),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        default,
        640,
        1.1);

    var expectedGrid = MotionGrid(2);
    True(snapshot.CoarseMotionGrid is not null);
    Equal(expectedGrid.Width, snapshot.CoarseMotionGrid!.Width);
    Equal(expectedGrid.Height, snapshot.CoarseMotionGrid.Height);
    True(expectedGrid.Luma.SequenceEqual(snapshot.CoarseMotionGrid.Luma));
    True(snapshot.CoarseMotionEstimate is not null);
    Equal(VisualRoomDirection.Left, snapshot.CoarseMotionEstimate!.Direction);
    True(!snapshot.CoarseMotionIsControlling);

    var image = LowResolutionMotionDiagnosticRenderer.Image(
        snapshot.CoarseMotionGrid,
        snapshot.CoarseMotionEstimate,
        isControlling: false);
    True(image is not null);
    Equal(32, image!.Width);
    Equal(18, image.Height);
    var confidence = (int)Math.Round(
        snapshot.CoarseMotionEstimate.Confidence * 100,
        MidpointRounding.AwayFromZero);
    Equal(
        $"Coarse Motion · ready · left 40 px · {confidence}%",
        LowResolutionMotionDiagnosticPresentation.Text(
            snapshot.CoarseMotionEstimate,
            isControlling: false,
            solveWidth: 640,
            gridWidth: 32));
}

static void TestFloorlessTraversalCarriesPoseWithoutChangingRoomAndHandsBackToGround()
{
    static FrameSignalProfile Profile(LowResolutionMotionGrid grid) => new(
        1_000,
        800,
        700,
        80,
        grid);

    var tracker = new VisualRoomTransitionTracker();
    _ = tracker.Observe(
        Profile(MotionGrid(0)),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        default,
        640,
        1,
        groundTrackingReliable: true);
    var travelledLeft = tracker.Observe(
        Profile(MotionGrid(2)),
        RoomAdmitted(),
        VisualRoomDirection.Left,
        null,
        RoomFrameSize(),
        default,
        640,
        1.1,
        groundTrackingReliable: false);

    Equal(0, travelledLeft.RoomID);
    True(!travelledLeft.IsTransitioning);
    True(travelledLeft.CoarseMotionIsControlling);
    True(travelledLeft.CoarseWorldPose is not null);
    CloseWithin(-40, travelledLeft.CoarseWorldPose!.Value.X, .001);
    CloseWithin(0, travelledLeft.CoarseWorldPose.Value.Y, .001);

    var returned = tracker.Observe(
        Profile(MotionGrid(0)),
        RoomAdmitted(),
        VisualRoomDirection.Right,
        null,
        RoomFrameSize(),
        new AtlasPoint(-40, 0),
        640,
        1.2,
        groundTrackingReliable: false);
    Equal(0, returned.RoomID);
    True(returned.CoarseWorldPose is not null);
    CloseWithin(0, returned.CoarseWorldPose!.Value.X, .001);

    var reacquired = tracker.Observe(
        Profile(MotionGrid(0)),
        RoomAdmitted(),
        null,
        null,
        RoomFrameSize(),
        new AtlasPoint(1, 3),
        640,
        1.3,
        groundTrackingReliable: true);
    True(reacquired.CoarseWorldPose is null);
    True(!reacquired.CoarseMotionIsControlling);
    Equal(0, reacquired.RoomID);
}

static void TestFloorlessTraversalKeepsDoorwayDepartureAtLastGroundAnchor()
{
    static FrameSignalProfile Profile(
        double mean,
        int visibleCount,
        LowResolutionMotionGrid grid)
    {
        var band = new FrameEdgeBlackBand(
            .45,
            new FrameFractionRange(.35, .65));
        return new FrameSignalProfile(
            1_000,
            mean < 18 ? 80 : 800,
            visibleCount,
            mean,
            grid,
            new FrameEdgeBlackBands(band, band));
    }

    var tracker = new VisualRoomTransitionTracker();
    _ = tracker.Observe(
        Profile(80, 700, MotionGrid(0)),
        RoomAdmitted(),
        VisualRoomDirection.Left,
        RoomLeftKnight(),
        RoomFrameSize(),
        default,
        640,
        1,
        groundTrackingReliable: true);
    _ = tracker.Observe(
        Profile(80, 700, MotionGrid(2)),
        RoomAdmitted(),
        VisualRoomDirection.Left,
        RoomLeftKnight(),
        RoomFrameSize(),
        default,
        640,
        1.1,
        groundTrackingReliable: false);
    var pending = tracker.Observe(
        Profile(8, 60, MotionGrid(4)),
        RoomAdmitted(),
        VisualRoomDirection.Left,
        RoomLeftKnight(),
        RoomFrameSize(),
        new AtlasPoint(-40, 0),
        640,
        1.2,
        groundTrackingReliable: false);
    True(pending.IsTransitioning);

    for (var index = 1;
         index < VisualRoomTransitionTracker.AppearanceStableFrames;
         index++)
    {
        _ = tracker.Observe(
            Profile(8, 60, MotionGrid(4)),
            RoomAdmitted(),
            VisualRoomDirection.Left,
            RoomRightKnight(),
            RoomFrameSize(),
            new AtlasPoint(-80, 0),
            640,
            1.2 + index / 60d,
            groundTrackingReliable: false);
    }

    Equal(1, tracker.Snapshot.RoomID);
    CloseWithin(-80, tracker.Snapshot.EntryWorldPose.X, .001);
    True(tracker.Snapshot.ActivationWorldPose is not null);
    CloseWithin(-80, tracker.Snapshot.ActivationWorldPose!.Value.X, .001);
}

static void TestLowResolutionDirection()
{
    var reference = MotionGrid(0);
    var artworkMovedRight = MotionGrid(2);
    var left = LowResolutionRoomMotionTracker.Estimate(reference, artworkMovedRight);
    Equal(VisualRoomDirection.Left, left!.Direction);
    Equal(2, left.ScreenShift);
    var right = LowResolutionRoomMotionTracker.Estimate(reference, MotionGrid(-2));
    Equal(VisualRoomDirection.Right, right!.Direction);
    Equal(-2, right.ScreenShift);
}

static void TestLowResolutionFadeAndSubcell()
{
    var reference = MotionGrid(0);
    var faded = MotionGrid(2, gain: 0.32);
    var estimate = LowResolutionRoomMotionTracker.Estimate(reference, faded);
    Equal(VisualRoomDirection.Left, estimate!.Direction);
    Equal(2, estimate.ScreenShift);

    var fractionalReference = FractionalMotionGrid(0);
    var shifted = FractionalMotionGrid(0.35);
    var vector = LowResolutionRoomMotionTracker.Translation(fractionalReference, shifted);
    True(vector is not null);
    CloseWithin(0.35, vector!.ScreenShiftX, 0.16);
    CloseWithin(0, vector.ScreenShiftY, 0.05);
}

static void TestLowResolutionRejectsStatic()
{
    var grid = FractionalMotionGrid(0);
    True(LowResolutionRoomMotionTracker.Translation(grid, grid) is null);
    var pixels = Enumerable.Repeat((byte)40, 64 * 36).ToArray();
    var textureless = new LowResolutionMotionGrid(64, 36, pixels);
    var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(
        textureless,
        textureless);
    Equal(LowResolutionTranslationRejection.InsufficientTexture,
        diagnostic.FinalRejection!.Value);
}

static void TestVisualGameplayTracker()
{
    var tracker = new VisualGameplayTracker();
    var frame = new BgraFrame(
        640,
        360,
        640 * BgraFrame.BytesPerPixel,
        new byte[640 * 360 * BgraFrame.BytesPerPixel],
        7,
        DateTimeOffset.UnixEpoch);
    var diagnostic = new LowResolutionTranslationDiagnostic(
        new LowResolutionMotionVector(2, -1, 0.8), null, null);
    var sample = tracker.Observe(frame, diagnostic);

    Equal("visualTracking", sample.Type);
    Equal(Guid.Empty, sample.SessionId);
    Equal(7L, sample.UnityFrame);
    Close(-13, sample.CameraX);
    Close(-6.5, sample.CameraY);
    tracker.Reset();
    var reset = tracker.Observe(frame, null);
    Close(0, reset.CameraX);
    Close(0, reset.CameraY);
}

static void TestVisualGameplayGroundAdmission()
{
    static BgraFrame Frame(long id, double seconds) => new(
        160, 100, 160 * BgraFrame.BytesPerPixel,
        new byte[160 * 100 * BgraFrame.BytesPerPixel],
        id, DateTimeOffset.UnixEpoch.AddSeconds(seconds));
    static DetectedFloorLine Line(int row) => new(row, new PixelSpan(16, 143));

    var tracker = new VisualGameplayTracker();
    var original = GroundCameraSolverTests.Pixels();
    var seeded = tracker.ObserveGroundAnchored(
        Frame(1, 0), null, original, [Line(40)], [], hasCommittedEvidence: false);
    True(!seeded.GroundVerified && !seeded.AllowsAtlasWrite);
    Equal("ground-seed", seeded.Source);
    var admittedSamples = 0;
    for (var index = 1; index <= 80; index++)
    {
        var observation = tracker.ObserveGroundAnchored(
            Frame(index + 1, index / 60.0), null, original, [Line(40)], [],
            hasCommittedEvidence: false);
        if (!observation.GroundVerified)
        {
            True(!observation.AllowsAtlasWrite);
            Equal("ground-observing", observation.Source);
            continue;
        }
        admittedSamples++;
        Equal(admittedSamples == FreshAtlasBootstrapGate.RequiredStableSamples,
            observation.AllowsAtlasWrite);
        if (observation.AllowsAtlasWrite) break;
    }
    Equal(FreshAtlasBootstrapGate.RequiredStableSamples, admittedSamples);

    tracker.Reset();
    _ = tracker.ObserveGroundAnchored(
        Frame(100, 0), null, original, [Line(40)], [], hasCommittedEvidence: true);
    VisualGameplayObservation? moved = null;
    for (var index = 1; index <= 30; index++)
    {
        var candidate = tracker.ObserveGroundAnchored(
            Frame(100 + index, index / 60.0), null,
            GroundCameraSolverTests.Pixels(dx: -4, dy: 3),
            [Line(43)], [], hasCommittedEvidence: true);
        if (candidate.GroundVerified)
        {
            moved = candidate;
            break;
        }
        True(!candidate.AllowsAtlasWrite);
    }
    True(moved is not null);
    var admittedMove = moved ?? throw new Exception("Ground presence never confirmed the restored atlas pose.");
    True(admittedMove.GroundVerified && admittedMove.AllowsAtlasWrite);
    True(Math.Abs(4 - admittedMove.Telemetry.CameraX) <= .25);
    True(Math.Abs(3 - admittedMove.Telemetry.CameraY) <= .25);

    var floorless = tracker.ObserveGroundAnchored(
        Frame(200, 1), new LowResolutionTranslationDiagnostic(
            new LowResolutionMotionVector(-2, 0, .8), null, null),
        GroundCameraSolverTests.Pixels(seed: 99), [], [], hasCommittedEvidence: true);
    True(floorless.AllowsAtlasWrite && floorless.Source == "transition-bridge");
    True(Math.Abs(9 - floorless.Telemetry.CameraX) <= .25);

    tracker.PauseGroundVerification();
    var resumed = tracker.ObserveGroundAnchored(
        Frame(201, 1 + 1.0 / 60), new LowResolutionTranslationDiagnostic(
            new LowResolutionMotionVector(-2, 0, .9), null, null),
        GroundCameraSolverTests.Pixels(seed: 100), [], [], hasCommittedEvidence: true);
    True(!resumed.GroundVerified && !resumed.AllowsAtlasWrite);
    True(Math.Abs(9 - resumed.Telemetry.CameraX) <= .25);

    var coarse = tracker.ObserveUnverified(Frame(202, 1 + 2.0 / 60),
        new LowResolutionTranslationDiagnostic(
            new LowResolutionMotionVector(1, 0, .9), null, null));
    True(!coarse.GroundVerified && !coarse.AllowsAtlasWrite);
    Equal("coarse-presentation", coarse.Source);
    tracker.Reset();
    var reset = tracker.Observe(Frame(203, 1 + 3.0 / 60), null);
    Close(0, reset.CameraX);
    Close(0, reset.CameraY);
}

static LowResolutionMotionGrid MotionGrid(
    int shift,
    int verticalShift = 0,
    double gain = 1,
    int width = 32,
    int height = 18)
{
    var pixels = new byte[width * height];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var sourceX = x - shift;
            var sourceY = y - verticalShift;
            if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height) continue;
            var value = (sourceX * 37 + sourceY * 61 + sourceX * sourceY * 3) % 211 + 22;
            pixels[y * width + x] = (byte)Math.Clamp(
                (int)Math.Round(value * gain, MidpointRounding.AwayFromZero), 0, 255);
        }
    }
    return new LowResolutionMotionGrid(width, height, pixels);
}

static LowResolutionMotionGrid FractionalMotionGrid(
    double horizontalShift,
    double verticalShift = 0,
    double gain = 1,
    int width = 64,
    int height = 36)
{
    double Source(int x, int y) => x < 0 || x >= width || y < 0 || y >= height
        ? 0
        : (x * 37 + y * 61 + x * y * 3) % 211 + 22;
    var pixels = new byte[width * height];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var sourceX = x - horizontalShift;
            var sourceY = y - verticalShift;
            var x0 = (int)Math.Floor(sourceX);
            var y0 = (int)Math.Floor(sourceY);
            var tx = sourceX - x0;
            var ty = sourceY - y0;
            var upper = Source(x0, y0) * (1 - tx) + Source(x0 + 1, y0) * tx;
            var lower = Source(x0, y0 + 1) * (1 - tx) + Source(x0 + 1, y0 + 1) * tx;
            pixels[y * width + x] = (byte)Math.Clamp(
                (int)Math.Round((upper * (1 - ty) + lower * ty) * gain,
                    MidpointRounding.AwayFromZero),
                0,
                255);
        }
    }
    return new LowResolutionMotionGrid(width, height, pixels);
}

static LowResolutionMotionGrid UnrelatedMotionGrid(
    int seed = 31,
    int width = 64,
    int height = 36)
{
    var pixels = new byte[width * height];
    for (var index = 0; index < pixels.Length; index++)
    {
        var x = index % width;
        var y = index / width;
        pixels[index] = (byte)((x * seed + y * 73 + x * y * 11 + seed * 3) & 0xff);
    }
    return new LowResolutionMotionGrid(width, height, pixels);
}

static LowResolutionMotionGrid VisibilityCircle(
    LowResolutionMotionGrid grid,
    double radius,
    double? centerX = null,
    double? centerY = null)
{
    var resolvedCenterX = centerX ?? grid.Width * .5;
    var resolvedCenterY = centerY ?? grid.Height * .5;
    const double feather = 3;
    var pixels = grid.Luma.Select((value, index) =>
    {
        var x = index % grid.Width + .5;
        var y = index / grid.Width + .5;
        var distance = Math.Sqrt(
            (x - resolvedCenterX) * (x - resolvedCenterX)
            + (y - resolvedCenterY) * (y - resolvedCenterY));
        var visibility = Math.Max(
            .04,
            Math.Min(1, (radius + feather - distance) / feather));
        return (byte)Math.Round(value * visibility, MidpointRounding.AwayFromZero);
    }).ToArray();
    return new LowResolutionMotionGrid(grid.Width, grid.Height, pixels);
}

static FrameSignalProfile MotionProfile(LowResolutionMotionGrid grid) => new(
    grid.Luma.Length,
    grid.Luma.Length,
    grid.Luma.Length,
    50,
    grid);

static void TestEvaluatesExactFrameMotionAndPlaceRecognition()
{
    var session = Guid.NewGuid();
    var baseline = FractionalMotionGrid(0);
    var shifted = FractionalMotionGrid(1, gain: .4);
    var path = TracePath(
        truth:
        [
            TraceTruth(1, 0, session, 0),
            TraceTruth(2, -10, session, .05),
            TraceTruth(3, -10, session, .25)
        ],
        frames:
        [
            TraceFrame(baseline, 1, 100, true),
            TraceFrame(shifted, 2, 100.05, false),
            TraceFrame(shifted, 3, 100.25, false)
        ],
        metadata: new Dictionary<string, string> { ["captureWidth"] = "640" });

    var report = LowResolutionTraceEvaluator.Evaluate(path);
    Equal(3, report.TraceFrames);
    Equal(3, report.ValidTraceFrames);
    Equal(3, report.ExactTruthFrames);
    Equal(1, report.GroundReliableFrames);
    Equal(2, report.GroundUnverifiedFrames);
    Close(LowResolutionRoomMotionTracker.MinimumImprovement,
        report.MotionMinimumImprovement);
    Equal(2, report.TraceIntervalSeconds.Count);
    Equal(1, report.AllMotion.Comparisons);
    Equal(1, report.AllMotion.RepresentableComparisons);
    Equal(1, report.AllMotion.AcceptedRepresentableComparisons);
    Close(100, report.AllMotion.RepresentableCoveragePercent ?? double.NaN);
    Equal(1, report.GroundLossMotion.Comparisons);
    True((report.GroundLossMotion.VectorErrorPixels.Maximum ?? double.PositiveInfinity) < 3);
    True((report.GroundLossMotion.IntegratedDriftPixels.Maximum ?? double.PositiveInfinity) < 3);
    Equal(1, report.OracleSeededPlaceRecognition.Matches);
    Equal(0, report.OracleSeededPlaceRecognition.MatchesOver24Pixels);
    True((report.OracleSeededPlaceRecognition.ErrorPixels.Maximum
        ?? double.PositiveInfinity) < 3);
    True(report.Evaluable);
    Close(100, report.ExactTruthCoveragePercent ?? double.NaN);
}

static void TestClassifiesCameraWarpOutsideSearchAndDoesNotCrossSceneEpoch()
{
    var session = Guid.NewGuid();
    var grid = FractionalMotionGrid(0);
    var report = LowResolutionTraceEvaluator.Evaluate(TracePath(
        truth:
        [
            TraceTruth(1, 0, session, 0),
            TraceTruth(2, 1000, session, .05),
            TraceTruth(3, 0, session, .10, "Town")
        ],
        frames:
        [
            TraceFrame(grid, 1, 100, true),
            TraceFrame(grid, 2, 100.05, false),
            TraceFrame(grid, 3, 100.10, false)
        ]));

    Equal(1, report.AllMotion.Comparisons);
    Equal(1, report.AllMotion.OutOfRangeComparisons);
    Equal(0, report.AllMotion.AcceptedOutOfRangeComparisons);
    Equal(1, report.GroundLossMotion.OutOfRangeComparisons);
}

static void TestEvaluatorAppliesProductionDirectionGate()
{
    var session = Guid.NewGuid();
    var report = LowResolutionTraceEvaluator.Evaluate(TracePath(
        events:
        [
            new RecordedInputPathEvent(0, RecordedGameButton.Right,
                RecordedInputTransition.Pressed)
        ],
        truth:
        [
            TraceTruth(1, 0, session, 0),
            TraceTruth(2, -20, session, .05)
        ],
        frames:
        [
            TraceFrame(FractionalMotionGrid(0), 1, 100, true),
            TraceFrame(FractionalMotionGrid(2), 2, 100.05, false)
        ]));

    Equal(1, report.GroundLossMotion.Comparisons);
    Equal(1, report.GroundLossMotion.DirectionGateRejections);
    Equal(0, report.GroundLossMotion.AcceptedComparisons);
    Equal(1, report.GroundLossMotion.RejectedRepresentableComparisons);
}

static void TestEvaluatorRecordsExperimentalMotionThreshold()
{
    var report = LowResolutionTraceEvaluator.Evaluate(TracePath(), .025);
    Close(.025, report.MotionMinimumImprovement);
}

static void TestRejectedMotionReportsHiddenHackerTravelByCause()
{
    var session = Guid.NewGuid();
    var textureless = new LowResolutionMotionGrid(
        64, 36, Enumerable.Repeat((byte)80, 64 * 36).ToArray());
    var report = LowResolutionTraceEvaluator.Evaluate(TracePath(
        truth:
        [
            TraceTruth(1, 0, session, 0),
            TraceTruth(2, 3, session, .05)
        ],
        frames:
        [
            TraceFrame(textureless, 1, 100, true),
            TraceFrame(textureless, 2, 100.05, false)
        ]));
    var hidden = report.GroundLossMotion.RejectedTravelByReason["insufficientTexture"];
    Equal(1, hidden.Comparisons);
    Close(3, hidden.SignedExpectedX);
    Close(3, hidden.AbsoluteExpectedX);
    Close(3, hidden.ExpectedDistancePixels.Maximum ?? double.NaN);
}

static void TestConnectedCameraKeepsOneBaselineAcrossSceneJoin()
{
    var session = Guid.NewGuid();
    var report = LowResolutionTraceEvaluator.Evaluate(TracePath(
        truth:
        [
            TraceTruth(1, 0, session, 0, heroScreenX: 600),
            TraceTruth(2, 10, session, .05, heroScreenX: 600),
            TraceTruth(3, 1000, session, .10, "Town", 40)
        ],
        coarse:
        [
            TraceCoarse(1, 0, 50),
            TraceCoarse(2, .05, 60),
            TraceCoarse(3, .10, 630)
        ]));

    Equal("capture-rate-coarse", report.ConnectedCamera.Source);
    Equal(3, report.ConnectedCamera.ExactFramePairs);
    True(report.ConnectedCamera.SceneNames.SequenceEqual(["Town", "Tutorial_01"]));
    Equal(1, report.ConnectedCamera.JoinedSceneErrorPixels.Count);
    Close(10, report.ConnectedCamera.EndErrorX ?? double.NaN);
    Close(0, report.ConnectedCamera.EndErrorY ?? double.NaN);
    Equal("published-tracking", report.ConnectedTrackingCamera.Source);
    Equal(0, report.ConnectedTrackingCamera.ExactFramePairs);
}

static void TestConnectedCameraUsesHistoricalPublishedTrackingWhenCoarseTraceIsAbsent()
{
    var session = Guid.NewGuid();
    var report = LowResolutionTraceEvaluator.Evaluate(TracePath(
        tracking:
        [
            TraceTracking(1, 0, 50),
            TraceTracking(2, .05, 60),
            TraceTracking(3, .10, 630)
        ],
        truth:
        [
            TraceTruth(1, 0, session, 0, heroScreenX: 600),
            TraceTruth(2, 10, session, .05, heroScreenX: 600),
            TraceTruth(3, 1000, session, .10, "Town", 40)
        ]));

    Equal("published-tracking", report.ConnectedCamera.Source);
    Equal(3, report.ConnectedCamera.ExactFramePairs);
    True(report.ConnectedCamera.SceneNames.SequenceEqual(["Town", "Tutorial_01"]));
    Equal(1, report.ConnectedCamera.JoinedSceneErrorPixels.Count);
    Close(10, report.ConnectedCamera.EndErrorX ?? double.NaN);
    Close(0, report.ConnectedCamera.EndErrorY ?? double.NaN);
    Equal(report.ConnectedCamera, report.ConnectedTrackingCamera);
}

static void TestInvalidPayloadIsExcludedWithoutLosingTraceCount()
{
    var session = Guid.NewGuid();
    var invalid = new RecordedLowResolutionFrame
    {
        Offset = 0,
        CaptureTimestamp = 100,
        RenderedGameFrame = 1,
        RoomId = 0,
        Width = 64,
        Height = 36,
        Luma = [1, 2, 3],
        GroundTrackingReliable = true
    };
    var report = LowResolutionTraceEvaluator.Evaluate(TracePath(
        truth: [TraceTruth(1, 0, session, 0)], frames: [invalid]));
    Equal(1, report.TraceFrames);
    Equal(0, report.ValidTraceFrames);
    Equal(0, report.ExactTruthFrames);
}

static void TestReconstructsTraceFromPersistedWorldEvidenceWindow()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        var roomId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var createdSeconds = createdAt.ToUnixTimeMilliseconds() / 1000d;
        var first = FractionalMotionGrid(0);
        var second = FractionalMotionGrid(1);
        _ = store.Append(TraceImage(first, 10), roomId, visitId,
            createdSeconds + .5, new AtlasPoint(0, 0), 640, []);
        _ = store.Append(TraceImage(second, 11), roomId, visitId,
            createdSeconds + .7, new AtlasPoint(10, 0), 640, []);
        _ = store.Append(TraceImage(second, 12), roomId, visitId,
            createdSeconds + 3, new AtlasPoint(20, 0), 640, []);
        var path = TracePath(
            tracking:
            [
                TraceTracking(10, 0, 0),
                TraceTracking(11, .2, 10)
            ],
            createdAt: createdAt);

        var frames = LowResolutionTraceEvaluator.PersistedWorldFrames(path, root);
        Equal(2, frames.Count);
        True(frames.Select(frame => frame.RenderedGameFrame)
            .SequenceEqual(new int?[] { 10, 11 }));
        True(frames.Select(frame => frame.RoomId).SequenceEqual([0, 0]));
        Close(0, frames[0].Offset);
        Close(.2, frames[1].Offset);
        True(frames.All(frame => frame.Width == 64 && frame.Height == 36));
        True(frames.All(frame => frame.GroundTrackingReliable));
    });
}

static RecordedInputPath TracePath(
    IReadOnlyList<RecordedInputPathEvent>? events = null,
    IReadOnlyList<RecordedTrackingSample>? tracking = null,
    IReadOnlyList<RecordedGroundTruthSample>? truth = null,
    IReadOnlyList<RecordedCoarseMotionSample>? coarse = null,
    IReadOnlyList<RecordedLowResolutionFrame>? frames = null,
    IReadOnlyDictionary<string, string>? metadata = null,
    DateTimeOffset? createdAt = null) => new(
        RecordedInputPath.CurrentSchemaVersion,
        Guid.NewGuid(),
        createdAt ?? DateTimeOffset.UtcNow,
        1,
        sourcePathId: null,
        replayIteration: null,
        startCheckpoint: null,
        events ?? [],
        tracking ?? [],
        truth,
        coarse,
        frames,
        loopClosures: null,
        metadata);

static RecordedLowResolutionFrame TraceFrame(LowResolutionMotionGrid grid,
    int unityFrame, double timestamp, bool reliable) => new()
    {
        Offset = timestamp - 100,
        CaptureTimestamp = timestamp,
        RenderedGameFrame = unityFrame,
        RoomId = 0,
        Width = grid.Width,
        Height = grid.Height,
        Luma = grid.Luma.ToArray(),
        GroundTrackingReliable = reliable
    };

static RecordedGroundTruthSample TraceTruth(int frame, double cameraX, Guid session,
    double offset, string sceneName = "Tutorial_01", double? heroScreenX = null) => new(
        offset,
        200 + offset,
        new ReceiverGroundTruthSample(
            2,
            "groundTruth",
            session,
            (ulong)frame,
            frame,
            10 + offset,
            sceneName,
            true,
            0, 0, 0,
            0, 0,
            true,
            true,
            true,
            cameraX, 0, -38.1,
            cameraX, 0, 0,
            180,
            1, 1,
            heroScreenX,
            heroScreenX is null ? null : 180,
            640, 360,
            640, 360));

static RecordedCoarseMotionSample TraceCoarse(int frame, double offset, double x) => new()
{
    Offset = offset,
    CaptureTimestamp = 100 + offset,
    RenderedGameFrame = frame,
    PresentedCameraX = x,
    PresentedCameraY = 25,
    CoarseCameraX = x,
    CoarseCameraY = 25,
    Direction = null,
    ScreenShift = null,
    Confidence = null,
    PlaceMatchKeyframeId = null,
    PlaceMatchScore = null,
    PlaceMatchMargin = null,
    IsControlling = true,
    IsTransitioning = false,
    RoomId = 0,
    RoomRevision = 0,
    SignalMeanPeak = 50,
    SignalVisibleFraction = .8
};

static RecordedTrackingSample TraceTracking(int frame, double offset, double x) => new()
{
    Offset = offset,
    CaptureTimestamp = 100 + offset,
    CameraX = x,
    CameraY = 25,
    PublishedCameraX = x,
    PublishedCameraY = 25,
    PoseSource = "ground",
    PoseVerified = true,
    HasConfirmedGround = true,
    GlobalMatchCount = 0,
    GroundSegmentCount = 1,
    InlierCount = 1,
    ResidualRms = 0,
    VisibleLineIds = [],
    AtlasLineIds = [],
    GroundTrackingMilliseconds = 1,
    SignalMeanPeak = 50,
    SignalVisibleFraction = .8,
    RoomId = 0,
    RoomRevision = 0,
    CaptureOffset = offset,
    RenderedGameFrame = frame
};

static BgraFrame TraceImage(LowResolutionMotionGrid grid, int renderedFrame)
{
    const int width = 640;
    const int height = 360;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var sourceX = Math.Min(grid.Width - 1, x * grid.Width / width);
        var sourceY = Math.Min(grid.Height - 1, y * grid.Height / height);
        var value = grid.Luma[sourceY * grid.Width + sourceX];
        var index = (y * width + x) * BgraFrame.BytesPerPixel;
        pixels[index] = value;
        pixels[index + 1] = value;
        pixels[index + 2] = value;
        pixels[index + 3] = 255;
    }
    var checksum = 0x5A ^ (renderedFrame & 255)
        ^ ((renderedFrame >> 8) & 255) ^ ((renderedFrame >> 16) & 255);
    var word = 0xD3UL | ((ulong)renderedFrame << 8) | ((ulong)checksum << 32);
    for (var bit = 0; bit < 40; bit++)
    {
        var value = (word & (1UL << bit)) == 0 ? (byte)0 : (byte)255;
        for (var y = 0; y < 6; y++) for (var x = bit * 3; x < bit * 3 + 3; x++)
        {
            var marker = y < 3 ? value : (byte)(255 - value);
            var index = (y * width + x) * BgraFrame.BytesPerPixel;
            pixels[index] = marker;
            pixels[index + 1] = marker;
            pixels[index + 2] = marker;
        }
    }
    return new BgraFrame(width, height, width * BgraFrame.BytesPerPixel,
        pixels, renderedFrame, DateTimeOffset.UnixEpoch);
}

static void TestLiveWorldPoseReplacement()
{
    var original = LiveWorldFixture();
    var replaced = original.ReplaceOptimizedPoses(
        new Dictionary<int, AtlasPoint>
        {
            [10] = new AtlasPoint(0, 0),
            [11] = new AtlasPoint(5, -2)
        },
        7);
    Equal(8, replaced.MapRevision);
    Equal(original.Observations[1].RawPose, replaced.Observations[1].RawPose);
    Equal(original.Observations[1].LocalPose, replaced.Observations[1].LocalPose);
    Equal(new AtlasPoint(5, -2), replaced.Observations[1].OptimizedPose);
    Equal(new AtlasPoint(40, 20), replaced.Landmarks[2].WorldPoint);
    Equal(new AtlasPoint(44.5, 18), replaced.Landmarks[3].WorldPoint);

    var stale = false;
    try
    {
        _ = original.ReplaceOptimizedPoses(
            new Dictionary<int, AtlasPoint>
            {
                [10] = new AtlasPoint(0, 0),
                [11] = new AtlasPoint(0, 0)
            },
            6);
    }
    catch (LiveWorldModelException error)
    {
        stale = error.Error == LiveWorldModelError.StaleRevision
            && error.ExpectedRevision == 7
            && error.ActualRevision == 6;
    }
    True(stale);
}

static void TestLiveWorldRejectsInvalidGraph()
{
    var observation = WorldObservation(10, 100);
    var unanchored = false;
    try
    {
        _ = new LiveWorldSnapshot(observations: [observation]);
    }
    catch (LiveWorldModelException error)
    {
        unanchored = error.Error == LiveWorldModelError.UnanchoredWorld;
    }
    True(unanchored);

    var duplicate = false;
    try
    {
        _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation, observation]);
    }
    catch (LiveWorldModelException error)
    {
        duplicate = error.Error == LiveWorldModelError.DuplicateObservationId
            && error.RelatedId == 10;
    }
    True(duplicate);

    var dangling = false;
    try
    {
        _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation],
            keyframes: [new LiveWorldKeyframe(1, 99, 100)]);
    }
    catch (LiveWorldModelException error)
    {
        dangling = error.Error == LiveWorldModelError.DanglingObservationId
            && error.RelatedId == 99;
    }
    True(dangling);
}

static void TestLiveWorldRoomTranslation()
{
    var roomOne = WorldObservation(
        1, 1, roomId: 0,
        raw: new AtlasPoint(-180, 0), optimized: new AtlasPoint(-180, 0));
    var roomTwo = WorldObservation(
        2, 2, roomId: 1,
        raw: new AtlasPoint(-820, 0), optimized: new AtlasPoint(-820, 0));
    var world = new LiveWorldSnapshot(
        anchoredObservationId: 1,
        observations: [roomOne, roomTwo]);
    var translated = world.TranslateRoom(1, new CameraVector(320, 5));
    Equal(1, translated.MapRevision);
    Equal(roomOne, translated.Observations[0]);
    Equal(roomTwo.RawPose, translated.Observations[1].RawPose);
    Equal(new AtlasPoint(-500, 5), translated.Observations[1].OptimizedPose);
    Equal(roomTwo.LocalPose, translated.Observations[1].LocalPose);
}

static void TestLiveWorldSnapshotStore()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new LiveWorldSnapshotStore(Path.Combine(workspace, "world"));
        Equal(0, store.Load().MapRevision);
        var initial = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [WorldObservation(10, 100)]);
        var revisionOne = initial.ReplaceOptimizedPoses(
            new Dictionary<int, AtlasPoint> { [10] = new AtlasPoint(1, 2) },
            0);
        True(store.Commit(revisionOne, 0));
        var loaded = store.Load();
        Equal(1, loaded.MapRevision);
        Equal(new AtlasPoint(1, 2), loaded.Observations[0].OptimizedPose);
        True(!store.Commit(revisionOne, 0));
        using var json = JsonDocument.Parse(File.ReadAllBytes(store.SnapshotPath));
        Equal(1, json.RootElement.GetProperty("mapRevision").GetInt32());
    });
}

static void TestSurfaceSupportRejectsBrightDecorationAndKeepsDarkPlatformBody()
{
    const int width = 80;
    const int height = 40;
    const int row = 20;
    var response = new byte[width * height];
    for (var x = 0; x < width; x++) response[row * width + x] = 255;
    var tuning = new GroundTheoryTuning(
        GroundThreshold: 8,
        MinimumSegmentLength: 48,
        LineSeparation: 36,
        OcclusionMergeGap: 180);

    var decoration = new GroundComparisonAnalysis(
        width,
        height,
        response,
        Enumerable.Repeat((byte)100, width * height).ToArray());
    True(GroundLineDetector.SemanticFloorLines(decoration, tuning).Count == 0);

    var platformLuma = Enumerable.Repeat((byte)100, width * height).ToArray();
    for (var y = row + 1; y < height; y++)
    {
        for (var x = 0; x < width; x++) platformLuma[y * width + x] = 20;
    }
    var platform = new GroundComparisonAnalysis(width, height, response, platformLuma);
    var rows = GroundLineDetector.SemanticFloorLines(platform, tuning)
        .Select(line => line.Row).ToArray();
    True(rows.SequenceEqual([row]));
}

static void TestCoverageIsOneToOneAndOverlappingLabelsDoNotDoubleCount()
{
    var document = GroundLabelDocumentFixture();
    document.Edges = [new GroundLabelEdge(0, 9, 20), new GroundLabelEdge(5, 14, 20)];
    var score = GroundLabelScoring.Score(document,
    [
        new DetectedFloorLine(20, new PixelSpan(0, 14)),
        new DetectedFloorLine(21, new PixelSpan(0, 14))
    ]);
    Equal(15, score.TruePositiveLength);
    Equal(15, score.FalsePositiveLength!.Value);
    Equal(0, score.FalseNegativeLength);
    Equal(.5, score.Precision!.Value);
    Equal(1d, score.Recall!.Value);
}

static void TestVerticalToleranceAndHorizontalEndpointsCountMissesAndExtras()
{
    var document = GroundLabelDocumentFixture();
    document.Edges = [new GroundLabelEdge(10, 19, 20)];
    var score = GroundLabelScoring.Score(document,
        [new DetectedFloorLine(23, new PixelSpan(15, 24))]);
    Equal(5, score.TruePositiveLength);
    Equal(5, score.FalsePositiveLength!.Value);
    Equal(5, score.FalseNegativeLength);
    var outside = GroundLabelScoring.Score(document,
        [new DetectedFloorLine(24, new PixelSpan(10, 19))]);
    Equal(0, outside.TruePositiveLength);
    Equal(10, outside.FalseNegativeLength);
}

static void TestIgnoreRegionsRemoveBothTruthAndPredictions()
{
    var document = GroundLabelDocumentFixture();
    document.Edges = [new GroundLabelEdge(0, 19, 20)];
    document.IgnoredRegions = [new GroundLabelRect(5, 18, 10, 6)];
    var score = GroundLabelScoring.Score(document,
        [new DetectedFloorLine(20, new PixelSpan(0, 19))]);
    Equal(10, score.TruePositiveLength);
    Equal(0, score.FalsePositiveLength!.Value);
    Equal(0, score.FalseNegativeLength);
}

static void TestPartialFramesCannotClaimPrecisionOrFalsePositives()
{
    var document = GroundLabelDocumentFixture(reviewed: false);
    document.Edges = [new GroundLabelEdge(0, 9, 20)];
    var score = GroundLabelScoring.Score(document,
        [new DetectedFloorLine(20, new PixelSpan(0, 19))]);
    True(score.FalsePositiveLength is null);
    True(score.Precision is null);
    True(score.F1 is null);
    Equal(1d, score.Recall!.Value);
}

static void TestFullyReviewedEmptyFrameIsNegativeEvidenceNotPerfectRecall()
{
    var score = GroundLabelScoring.Score(
        GroundLabelDocumentFixture(),
        [new DetectedFloorLine(20, new PixelSpan(0, 9))]);
    Equal(10, score.FalsePositiveLength!.Value);
    Equal(0d, score.Precision!.Value);
    True(score.Recall is null);
    True(score.F1 is null);
}

static void TestEvidenceCoverageDoesNotCreditInferredBridges()
{
    var document = GroundLabelDocumentFixture();
    document.Edges = [new GroundLabelEdge(0, 29, 20)];
    DetectedFloorLine[] lines =
    [
        new(20, new PixelSpan(0, 29),
            [new PixelSpan(0, 9), new PixelSpan(20, 29)])
    ];
    Equal(30, GroundLabelScoring.Score(document, lines).TruePositiveLength);
    var evidence = GroundLabelScoring.Score(document, lines, evidenceOnly: true);
    Equal(20, evidence.TruePositiveLength);
    Equal(10, evidence.FalseNegativeLength);
}

static void TestDuplicateIdenticalPredictionsAreDeduplicated()
{
    var document = GroundLabelDocumentFixture();
    document.Edges = [new GroundLabelEdge(0, 19, 20)];
    var score = GroundLabelScoring.Score(document,
    [
        new DetectedFloorLine(20, new PixelSpan(0, 14)),
        new DetectedFloorLine(20, new PixelSpan(5, 19))
    ]);
    Equal(20, score.TruePositiveLength);
    Equal(0, score.FalsePositiveLength!.Value);
}

static void TestSplitValidationRejectsGroupAndDuplicateImageLeakage()
{
    var first = GroundLabelDocumentFixture();
    var second = GroundLabelDocumentFixture();
    second.Split = GroundLabelSplit.Check;
    second.ImageSHA256 = new string('b', 64);
    GroundLabelThrows(() => GroundLabelStore.ValidateSplits([first, second]));
    second.Group = "route-B";
    second.ImageSHA256 = first.ImageSHA256;
    GroundLabelThrows(() => GroundLabelStore.ValidateSplits([first, second]));
    second.ImageSHA256 = new string('b', 64);
    GroundLabelStore.ValidateSplits([first, second]);
}

static void TestCoordinatesStayExactAtZoomAndRejectLetterboxOutsideImage()
{
    Equal(
        new SourcePoint(75, 30),
        GroundLabelCoordinates.Pixel(
            new SourcePoint(150, 60),
            new GroundLabelSize(200, 120),
            100,
            60)!.Value);
    True(GroundLabelCoordinates.Pixel(
        new SourcePoint(-1, 30), new GroundLabelSize(200, 120), 100, 60) is null);
    True(GroundLabelCoordinates.Pixel(
        new SourcePoint(200, 30), new GroundLabelSize(200, 120), 100, 60) is null);
    var rectangle = new StencilRect(10, 12, 20, 8);
    var top = GroundLabelRect.FromBottomLeft(rectangle, 60);
    Equal(40d, top.Y);
    Equal(rectangle, top.BottomLeft(60));
}

static void TestSaveReopenAndSourceHashMismatch()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "ground-labels");
        var store = new GroundLabelStore(root);
        var document = store.Add(GroundLabelImage(), "synthetic", "train-A");
        document.Edges = [new GroundLabelEdge(10, 90, 30)];
        document.FullyReviewed = true;
        store.Save(document);

        var reopened = new GroundLabelStore(root);
        var documents = reopened.Documents();
        Equal(1, documents.Count);
        True(document.Equals(documents[0]));
        Equal(100, reopened.Image(document).Width);
        File.WriteAllText(Path.Combine(root, document.ImageFile), "changed");
        GroundLabelThrows(() => reopened.Image(document));
    });
}

static void TestInvalidGeometryAndTraversalRejected()
{
    var document = GroundLabelDocumentFixture();
    document.Edges = [new GroundLabelEdge(10, 100, 30)];
    GroundLabelThrows(document.Validate);
    document.Edges = [];
    document.ImageFile = "../source.png";
    GroundLabelThrows(document.Validate);
}

static void TestNewCaptureInExistingCheckGroupInheritsSplit()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new GroundLabelStore(Path.Combine(workspace, "ground-labels"));
        var first = store.Add(
            GroundLabelImage(),
            "first",
            "independent-check");
        first.Split = GroundLabelSplit.Check;
        store.Save(first);
        var second = store.Add(
            GroundLabelImage(background: 27),
            "second",
            "independent-check");
        Equal(GroundLabelSplit.Check, second.Split);
    });
}

static void TestHumanIgnoreRegionsNeverChangeDetectorInput()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new GroundLabelStore(Path.Combine(workspace, "ground-labels"));
        var document = store.Add(GroundLabelImage(), "synthetic", "train-A");
        var before = GroundLabelEvaluator.Prepare(store)[0].Comparison.GroundPixels;
        document.IgnoredRegions = [new GroundLabelRect(0, 0, 100, 60)];
        store.Save(document);
        var after = GroundLabelEvaluator.Prepare(store)[0].Comparison.GroundPixels;
        True(before.SequenceEqual(after));
    });
}

static void TestUnreviewedLabelsExcludedFromTuningAndDraftDatasetNotGeneralization()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new GroundLabelStore(Path.Combine(workspace, "ground-labels"));
        var document = store.Add(GroundLabelImage(), "synthetic", "train-A");
        document.Edges = [new GroundLabelEdge(10, 90, 30)];
        store.Save(document);
        var report = GroundLabelEvaluator.Run(store, sweep: false);
        Equal(0, report.ReviewedTrainFrames);
        True(report.Baseline.Train.Recall is null);
        True(report.Baseline.Train.Precision is null);
        True(report.Provisional);
        GroundLabelThrows(() => GroundLabelEvaluator.Run(store, sweep: true));
    });
}

static void TestCheckLabelsCannotChooseParameters()
{
    WithTemporaryDirectory(workspace =>
    {
        var store = new GroundLabelStore(Path.Combine(workspace, "ground-labels"));
        var train = store.Add(GroundLabelImage(), "synthetic train", "train-A");
        train.Edges = [new GroundLabelEdge(10, 90, 30)];
        train.FullyReviewed = true;
        store.Save(train);
        var check = store.Add(GroundLabelImage(background: 25), "synthetic check", "check-B");
        check.Split = GroundLabelSplit.Check;
        check.FullyReviewed = true;
        check.Edges = [new GroundLabelEdge(10, 90, 30)];
        store.Save(check);

        var first = GroundLabelEvaluator.Run(store, sweep: true);
        check.Edges = [new GroundLabelEdge(0, 99, 5)];
        store.Save(check);
        var second = GroundLabelEvaluator.Run(store, sweep: true);

        Equal(first.Selected.Parameters, second.Selected.Parameters);
        Equal(first.Selected.Train, second.Selected.Train);
        Equal(385, first.CandidatesTested);
        True(!string.Equals(first.DatasetDigest, second.DatasetDigest, StringComparison.Ordinal));
    });
}

static GroundLabelDocument GroundLabelDocumentFixture(bool reviewed = true)
{
    var id = Guid.NewGuid();
    return new GroundLabelDocument
    {
        Id = id,
        ImageFile = id.ToString("D").ToUpperInvariant() + ".png",
        ImageSHA256 = new string('a', 64),
        Width = 100,
        Height = 60,
        CreatedAt = DateTimeOffset.UtcNow,
        Source = "Test fixture",
        Group = "route-A",
        FullyReviewed = reviewed,
        RuntimeMasksKnown = true,
        RuntimeKnightKnown = true
    };
}

static BgraFrame GroundLabelImage(byte background = 24)
{
    const int width = 100;
    const int height = 60;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var y = 0; y < height; y++)
    {
        var value = y >= 30 ? (byte)180 : background;
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * BgraFrame.BytesPerPixel;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
    }
    return new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        pixels,
        0,
        DateTimeOffset.UnixEpoch);
}

static void GroundLabelThrows(Action action)
{
    try
    {
        action();
    }
    catch (GroundLabelException)
    {
        return;
    }
    throw new InvalidOperationException("Expected GroundLabelException.");
}

static void TestAppendSnapshotAndRestartPreserveDescriptorsAndOptimizedPoses()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var writer = new LiveWorldSessionStore(root);
        var first = writer.Append(
            SolidFrame(32, 18, 255, 0, 0),
            1,
            new AtlasPoint(0, 0),
            32,
            [new StencilRect(1, 1, 2, 2)]);
        var second = writer.Append(
            SolidFrame(32, 18, 0, 0, 255),
            2,
            new AtlasPoint(4, 0),
            32);
        var snapshot = LiveWorldSessionFixture(first, second, revision: 1);
        True(writer.Commit(snapshot, expectedRevision: 0, newRevision: 1));

        var reopened = new LiveWorldSessionStore(root);
        var loaded = reopened.Load();
        EqualLiveWorldSnapshots(snapshot, loaded.Snapshot);
        Equal(1, loaded.SceneRevision);
        Equal(32, reopened.Source(first.Id).Width);
    });
}

static void TestStaleDiskRevisionIsRejected()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var writer = new LiveWorldSessionStore(root);
        var source = writer.Append(
            SolidFrame(32, 18, 255, 255, 255),
            1,
            new AtlasPoint(0, 0),
            32);
        var stale = new LiveWorldSessionStore(root);
        var first = OneObservationSessionWorld(source, revision: 1);
        True(writer.Commit(first, expectedRevision: 0, newRevision: 1));
        var second = OneObservationSessionWorld(
            source,
            revision: 2,
            optimized: new AtlasPoint(3, 0));
        True(!stale.Commit(second, expectedRevision: 0, newRevision: 2));
        Equal(1, writer.Load().SceneRevision);
    });
}

static void TestRoomPlacementTranslationPreservesManifestEvidenceAndCommits()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new LiveWorldSessionStore(root);
        var rightSource = store.Append(
            SolidFrame(32, 18, 0, 0, 255),
            1,
            new AtlasPoint(-180, 0),
            32);
        var leftSource = store.Append(
            SolidFrame(32, 18, 255, 0, 0),
            2,
            new AtlasPoint(-820, 0),
            32);
        var original = new LiveWorldSnapshot(
            mapRevision: 1,
            anchoredObservationId: 1,
            observations:
            [
                SessionObservation(1, rightSource, roomId: 0, rightSource.CameraPosition),
                SessionObservation(2, leftSource, roomId: 1, leftSource.CameraPosition)
            ]);
        True(store.Commit(original, expectedRevision: 0, newRevision: 1));

        var translated = original.TranslateRoom(1, new CameraVector(320, 5));
        True(store.Commit(translated, expectedRevision: 1, newRevision: 2));

        var reopened = new LiveWorldSessionStore(root).Load().Snapshot;
        var left = reopened.Observations.Single(value => value.RoomId == 1);
        Equal(leftSource.CameraPosition, left.RawPose);
        Equal(new AtlasPoint(-500, 5), left.OptimizedPose);
    });
}

static void TestCorruptSnapshotIsSurfacedWithoutOverwrite()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new SceneSessionStore(root);
        True(store.SaveScene(
            Encoding.UTF8.GetBytes("not a live world"),
            expectedRevision: 0,
            newRevision: 1));
        var snapshotPath = Path.Combine(root, "scene.snapshot");
        var before = File.ReadAllBytes(snapshotPath);

        var error = ExpectLiveWorldSessionError(
            () => _ = new LiveWorldSessionStore(root),
            LiveWorldSessionStoreError.CorruptSnapshot);
        Equal(LiveWorldSessionStoreError.CorruptSnapshot, error.Error);
        True(File.ReadAllBytes(snapshotPath).SequenceEqual(before));
    });
}

static void TestArchivePreservesOldWorldAndFreshStoreIsEmpty()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new LiveWorldSessionStore(root);
        var source = store.Append(
            SolidFrame(32, 18, 0, 255, 0),
            1,
            new AtlasPoint(0, 0),
            32);
        var world = OneObservationSessionWorld(source, revision: 1);
        True(store.Commit(world, expectedRevision: 0, newRevision: 1));

        var archived = store.ArchiveAndReset(DateTimeOffset.FromUnixTimeSeconds(123));
        var fresh = archived.FreshStore.Load();
        EqualLiveWorldSnapshots(new LiveWorldSnapshot(), fresh.Snapshot);
        Equal(0, fresh.SceneRevision);
        EqualLiveWorldSnapshots(
            world,
            new LiveWorldSessionStore(archived.ArchiveDirectory).Load().Snapshot);
        True(File.Exists(Path.Combine(
            archived.ArchiveDirectory,
            source.ImagePath.Replace('/', Path.DirectorySeparatorChar))));
        Equal(1, Directory.GetDirectories(workspace, "world.archive-*").Length);
    });
}

static void TestFailedFreshStoreInitializationRestoresOriginalWorldOverPartialRoot()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new LiveWorldSessionStore(root);
        var source = store.Append(
            SolidFrame(32, 18, 0, 255, 0),
            1,
            new AtlasPoint(0, 0),
            32);
        var world = OneObservationSessionWorld(source, revision: 1);
        True(store.Commit(world, expectedRevision: 0, newRevision: 1));
        var injectedFailure = new InvalidOperationException("fresh store");
        Exception? observedFailure = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _ = store.ArchiveAndReset(beforeFreshStoreCreation: destination =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "partial"), "partial");
                throw injectedFailure;
            });
        }
        catch (Exception error)
        {
            observedFailure = error;
        }
        elapsed.Stop();

        True(ReferenceEquals(injectedFailure, observedFailure));
        True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        var reopened = new LiveWorldSessionStore(root);
        EqualLiveWorldSnapshots(world, reopened.Load().Snapshot);
        Equal(32, reopened.Source(source.Id).Width);
        True(!File.Exists(Path.Combine(root, "partial")));
        Equal(0, Directory.GetDirectories(workspace, "world.archive-*").Length);
    });
}

static void TestManifestFailureRollsBackAndOrphanRetryRecovers()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        string? manifestBackup = null;
        var failOnce = false;
        var orphanPath = Path.Combine(root, "frames", "00000000.png");
        Action<string> beforeManifestWrite = manifestPath =>
        {
            if (failOnce) return;
            failOnce = true;
            True(File.Exists(orphanPath));
            using (var durableEvidence = new FileStream(
                       orphanPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                True(durableEvidence.Length > 8);
            }
            manifestBackup = manifestPath + ".injected-backup";
            File.Move(manifestPath, manifestBackup);
            Directory.CreateDirectory(manifestPath);
        };
        var constructor = typeof(LiveWorldSessionStore).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(Action<string>)],
            modifiers: null) ?? throw new Exception("Live-world fault-injection constructor is missing.");
        var store = (LiveWorldSessionStore)constructor.Invoke([root, beforeManifestWrite]);

        Exception? persistenceFailure = null;
        try
        {
            _ = store.Append(
                SolidFrame(32, 18, 0, 255, 0),
                1,
                new AtlasPoint(0, 0),
                32);
        }
        catch (Exception error)
        {
            persistenceFailure = error;
        }
        finally
        {
            var manifestPath = Path.Combine(root, "manifest.json");
            if (Directory.Exists(manifestPath)) Directory.Delete(manifestPath);
            if (manifestBackup is not null && File.Exists(manifestBackup))
                File.Move(manifestBackup, manifestPath);
        }
        True(persistenceFailure is IOException or UnauthorizedAccessException);

        var reopened = new SceneSessionStore(root);
        Equal(0, reopened.Manifest.Observations.Count);
        Equal(0, reopened.Manifest.NextObservationId);
        True(!File.Exists(orphanPath));

        PngWriter.Write(SolidFrame(32, 18, 0, 0, 255), orphanPath);
        using (var orphanBlocker = new FileStream(
                   orphanPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var cleanupFailure = ExpectLiveWorldSessionError(
                () => store.Append(
                    SolidFrame(32, 18, 0, 255, 0),
                    2,
                    new AtlasPoint(0, 0),
                    32),
                LiveWorldSessionStoreError.EvidenceRollbackFailed);
            Equal(LiveWorldSessionStoreError.EvidenceRollbackFailed, cleanupFailure.Error);
        }

        var recovered = store.Append(
            SolidFrame(32, 18, 0, 0, 255),
            3,
            new AtlasPoint(0, 0),
            32);
        Equal(0, recovered.Id);
        var pixels = store.Source(recovered.Id).Pixels;
        Equal((byte)255, pixels[2]);
    });
}

static void TestConcurrentFirstConstructorsShareOneGeneration()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);
        var observations = new StoredFrameObservation?[2];
        var errors = new Exception?[2];
        var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                var store = new LiveWorldSessionStore(root);
                observations[index] = store.Append(
                    SolidFrame(32, 18, (byte)(index * 255), 0, (byte)((1 - index) * 255)),
                    index + 1,
                    new AtlasPoint(index, 0),
                    32);
            }
            catch (Exception error)
            {
                errors[index] = error;
            }
        })).ToArray();

        True(ready.Wait(TimeSpan.FromSeconds(1)));
        start.Set();
        True(Task.WaitAll(tasks, TimeSpan.FromSeconds(5)));
        if (errors.Any(error => error is not null))
        {
            throw new AggregateException(errors.Where(error => error is not null)!);
        }
        Equal(2, observations.Select(value => value!.Id).Distinct().Count());
        var reopened = new SceneSessionStore(root);
        Equal(2, reopened.Manifest.Observations.Count);
        Equal(2, reopened.Manifest.NextObservationId);
    });
}

static void TestConstructorDuringResetOpensFreshGeneration()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var owner = new LiveWorldSessionStore(root);
        using var hookEntered = new ManualResetEventSlim(false);
        using var releaseHook = new ManualResetEventSlim(false);
        using var constructorStarted = new ManualResetEventSlim(false);
        LiveWorldSessionArchive? archive = null;
        LiveWorldSessionStore? concurrent = null;
        Exception? resetError = null;
        Exception? constructorError = null;

        var reset = Task.Run(() =>
        {
            try
            {
                archive = owner.ArchiveAndReset(
                    DateTimeOffset.FromUnixTimeSeconds(126),
                    _ =>
                    {
                        hookEntered.Set();
                        if (!releaseHook.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("Reset test hook timed out.");
                    });
            }
            catch (Exception error)
            {
                resetError = error;
            }
        });
        True(hookEntered.Wait(TimeSpan.FromSeconds(2)));
        var opening = Task.Run(() =>
        {
            constructorStarted.Set();
            try
            {
                concurrent = new LiveWorldSessionStore(root);
            }
            catch (Exception error)
            {
                constructorError = error;
            }
        });
        True(constructorStarted.Wait(TimeSpan.FromSeconds(1)));
        True(!opening.Wait(TimeSpan.FromMilliseconds(100)));
        releaseHook.Set();
        True(Task.WaitAll([reset, opening], TimeSpan.FromSeconds(5)));
        if (resetError is not null || constructorError is not null)
        {
            throw new AggregateException(
                new[] { resetError, constructorError }.Where(error => error is not null)!);
        }

        var first = archive!.FreshStore.Append(
            SolidFrame(32, 18, 0, 255, 0),
            1,
            new AtlasPoint(0, 0),
            32);
        var second = concurrent!.Append(
            SolidFrame(32, 18, 255, 0, 0),
            2,
            new AtlasPoint(1, 0),
            32);
        Equal(0, first.Id);
        Equal(1, second.Id);
    });
}

static void TestConcurrentAppendsAllocateUniqueDurableEvidence()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        const int writerCount = 6;
        const int appendsPerWriter = 8;
        var stores = Enumerable.Range(0, writerCount)
            .Select(_ => new LiveWorldSessionStore(root))
            .ToArray();
        using var ready = new CountdownEvent(writerCount);
        using var start = new ManualResetEventSlim(false);
        using var wave = new Barrier(writerCount);
        var observations = new StoredFrameObservation?[writerCount * appendsPerWriter];
        var errors = new Exception?[writerCount];
        // Blocking synchronization needs dedicated writers. Thread-pool ramp-up
        // can exceed the readiness timeout before the append code even runs.
        var tasks = Enumerable.Range(0, writerCount).Select(index => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                for (var append = 0; append < appendsPerWriter; append++)
                {
                    True(wave.SignalAndWait(TimeSpan.FromSeconds(5)));
                    observations[index * appendsPerWriter + append] = stores[index].Append(
                        SolidFrame(32, 18, (byte)(index * 31), 0, (byte)(255 - index * 31)),
                        timestamp: index * appendsPerWriter + append + 1,
                        rawCameraPose: new AtlasPoint(index, append),
                        solveWidth: 32);
                }
            }
            catch (Exception exception)
            {
                errors[index] = exception;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try { True(ready.Wait(TimeSpan.FromSeconds(5))); }
        finally { start.Set(); }
        True(Task.WaitAll(tasks, TimeSpan.FromSeconds(5)));
        if (errors.Any(error => error is not null))
        {
            throw new AggregateException(errors.Where(error => error is not null)!);
        }
        var expectedCount = writerCount * appendsPerWriter;
        Equal(expectedCount, observations.Select(value => value!.Id).Distinct().Count());
        Equal(expectedCount, observations.Select(value => value!.ImagePath).Distinct().Count());

        var reopened = new LiveWorldSessionStore(root);
        foreach (var observation in observations.Select(value => value!))
        {
            Equal(32, reopened.Source(observation.Id).Width);
            True(File.Exists(Path.Combine(
                root,
                observation.ImagePath.Replace('/', Path.DirectorySeparatorChar))));
        }
        var manifest = new SceneSessionStore(root).Manifest;
        Equal(expectedCount, manifest.Observations.Count);
        Equal(expectedCount, manifest.NextObservationId);
    });
}

static void TestStaleInstancesCannotRepopulateResetRoot()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var owner = new LiveWorldSessionStore(root);
        var source = owner.Append(
            SolidFrame(32, 18, 0, 255, 0),
            1,
            new AtlasPoint(0, 0),
            32);
        var stale = new LiveWorldSessionStore(root);
        var original = OneObservationSessionWorld(source, revision: 1);
        True(owner.Commit(original, expectedRevision: 0, newRevision: 1));
        var archived = owner.ArchiveAndReset(DateTimeOffset.FromUnixTimeSeconds(124));

        var ownerError = ExpectLiveWorldSessionError(
            () => owner.Append(
                SolidFrame(32, 18, 0, 0, 255),
                2,
                new AtlasPoint(1, 0),
                32),
            LiveWorldSessionStoreError.SessionInvalidated);
        Equal(LiveWorldSessionStoreError.SessionInvalidated, ownerError.Error);
        var staleError = ExpectLiveWorldSessionError(
            () => stale.Commit(
                OneObservationSessionWorld(source, revision: 2),
                expectedRevision: 0,
                newRevision: 2),
            LiveWorldSessionStoreError.SessionInvalidated);
        Equal(LiveWorldSessionStoreError.SessionInvalidated, staleError.Error);

        var fresh = archived.FreshStore.Load();
        EqualLiveWorldSnapshots(new LiveWorldSnapshot(), fresh.Snapshot);
        Equal(0, fresh.SceneRevision);
        var freshSource = archived.FreshStore.Append(
            SolidFrame(32, 18, 255, 255, 255),
            3,
            new AtlasPoint(0, 0),
            32);
        Equal(0, freshSource.Id);
    });
}

static void TestMissingEvidenceCannotBeCommitted()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new LiveWorldSessionStore(root);
        var source = store.Append(
            SolidFrame(32, 18, 0, 255, 0),
            1,
            new AtlasPoint(0, 0),
            32);
        File.Delete(Path.Combine(
            root,
            source.ImagePath.Replace('/', Path.DirectorySeparatorChar)));

        var error = ExpectLiveWorldSessionError(
            () => store.Commit(
                OneObservationSessionWorld(source, revision: 1),
                expectedRevision: 0,
                newRevision: 1),
            LiveWorldSessionStoreError.MissingSourceObservation);
        Equal(source.Id, error.ObservationId!.Value);
        True(!File.Exists(Path.Combine(root, "scene.snapshot")));
    });
}

static void TestRollbackFailurePreservesRecoveryArchive()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new LiveWorldSessionStore(root);
        var source = store.Append(
            SolidFrame(32, 18, 0, 255, 0),
            1,
            new AtlasPoint(0, 0),
            32);
        var world = OneObservationSessionWorld(source, revision: 1);
        True(store.Commit(world, expectedRevision: 0, newRevision: 1));

        FileStream? blocker = null;
        LiveWorldSessionStoreException? observed = null;
        try
        {
            _ = store.ArchiveAndReset(
                DateTimeOffset.FromUnixTimeSeconds(125),
                destination =>
                {
                    Directory.CreateDirectory(destination);
                    blocker = new FileStream(
                        Path.Combine(destination, "partial.locked"),
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    blocker.WriteByte(1);
                    blocker.Flush(flushToDisk: true);
                    throw new InvalidOperationException("fresh store");
                });
        }
        catch (LiveWorldSessionStoreException error)
        {
            observed = error;
        }
        finally
        {
            blocker?.Dispose();
        }

        True(observed is not null);
        Equal(LiveWorldSessionStoreError.ArchiveRollbackFailed, observed!.Error);
        var archives = Directory.GetDirectories(workspace, "world.archive-*");
        Equal(1, archives.Length);
        var recovery = new LiveWorldSessionStore(archives[0]);
        EqualLiveWorldSnapshots(world, recovery.Load().Snapshot);
        Equal(32, recovery.Source(source.Id).Width);
    });
}

static void TestManifestInvariantFailuresAreTyped()
{
    WithTemporaryDirectory(workspace =>
    {
        var malformedRoot = Path.Combine(workspace, "malformed");
        Directory.CreateDirectory(malformedRoot);
        File.WriteAllText(Path.Combine(malformedRoot, "manifest.json"), "{not-json");
        _ = ExpectLiveWorldSessionError(
            () => _ = new LiveWorldSessionStore(malformedRoot),
            LiveWorldSessionStoreError.CorruptSessionManifest);

        var duplicateRoot = Path.Combine(workspace, "duplicate");
        _ = new LiveWorldSessionStore(duplicateRoot);
        var duplicateSession = new SceneSessionStore(duplicateRoot).Manifest.SessionId;
        var duplicated = new StoredFrameObservation(
            0,
            duplicateSession,
            duplicateSession,
            1,
            "frames/00000000.png",
            new AtlasPoint(0, 0),
            32,
            [],
            0);
        File.WriteAllBytes(
            Path.Combine(duplicateRoot, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(new SessionManifest(
                SessionManifest.CurrentSchemaVersion,
                duplicateSession,
                [duplicated, duplicated],
                1)));
        _ = ExpectLiveWorldSessionError(
            () => _ = new LiveWorldSessionStore(duplicateRoot),
            LiveWorldSessionStoreError.CorruptSessionManifest);

        foreach (var operation in new[] { "load", "commit", "append", "source" })
        {
            var root = Path.Combine(workspace, operation);
            var store = new LiveWorldSessionStore(root);
            File.WriteAllBytes(
                Path.Combine(root, "manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(new SessionManifest(
                    SessionManifest.CurrentSchemaVersion,
                    Guid.Empty,
                    [],
                    0)));
            _ = ExpectLiveWorldSessionError(
                () =>
                {
                    switch (operation)
                    {
                        case "load":
                            _ = store.Load();
                            break;
                        case "commit":
                            _ = store.Commit(
                                new LiveWorldSnapshot(mapRevision: 1),
                                expectedRevision: 0,
                                newRevision: 1);
                            break;
                        case "append":
                            _ = store.Append(
                                SolidFrame(32, 18, 0, 255, 0),
                                1,
                                new AtlasPoint(0, 0),
                                32);
                            break;
                        case "source":
                            _ = store.Source(0);
                            break;
                    }
                },
                LiveWorldSessionStoreError.CorruptSessionManifest);
        }
    });
}

static void TestRootJunctionSwapCannotRebasePinnedGeneration()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var original = Path.Combine(workspace, "world-original");
        var replacement = Path.Combine(workspace, "replacement");
        var store = new LiveWorldSessionStore(root);
        Directory.CreateDirectory(replacement);
        File.Copy(
            Path.Combine(root, "manifest.json"),
            Path.Combine(replacement, "manifest.json"));
        Directory.Move(root, original);
        var junctionCreated = false;
        try
        {
            if (!TryCreateDirectoryJunction(root, replacement, out var reason))
            {
                Console.WriteLine($"SKIP live world session junction swap: {reason}");
                return;
            }
            junctionCreated = true;
            True((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0);

            _ = ExpectLiveWorldSessionError(
                () => _ = store.Commit(
                    new LiveWorldSnapshot(mapRevision: 1),
                    expectedRevision: 0,
                    newRevision: 1),
                LiveWorldSessionStoreError.SessionInvalidated);
            True(!File.Exists(Path.Combine(replacement, "scene.snapshot")));
        }
        finally
        {
            if (junctionCreated && Directory.Exists(root)) Directory.Delete(root);
            if (!Directory.Exists(root) && Directory.Exists(original))
                Directory.Move(original, root);
        }
    });
}

static void TestMalformedStoreFilesSurfaceTypedErrors()
{
    WithTemporaryDirectory(workspace =>
    {
        var malformedManifestRoot = Path.Combine(workspace, "malformed-manifest");
        Directory.CreateDirectory(malformedManifestRoot);
        File.WriteAllText(
            Path.Combine(malformedManifestRoot, "manifest.json"),
            "{not-json");
        _ = ExpectLiveWorldSessionError(
            () => _ = new LiveWorldSessionStore(malformedManifestRoot),
            LiveWorldSessionStoreError.CorruptSessionManifest);

        var malformedSnapshotRoot = Path.Combine(workspace, "malformed-snapshot");
        _ = new LiveWorldSessionStore(malformedSnapshotRoot);
        File.WriteAllText(
            Path.Combine(malformedSnapshotRoot, "scene.snapshot"),
            "{not-json");
        _ = ExpectLiveWorldSessionError(
            () => _ = new LiveWorldSessionStore(malformedSnapshotRoot),
            LiveWorldSessionStoreError.CorruptSnapshot);

        var unsupportedSnapshotRoot = Path.Combine(workspace, "unsupported-snapshot");
        _ = new LiveWorldSessionStore(unsupportedSnapshotRoot);
        File.WriteAllText(
            Path.Combine(unsupportedSnapshotRoot, "scene.snapshot"),
            "{\"schemaVersion\":999,\"revision\":1,\"data\":\"\"}");
        var unsupported = ExpectLiveWorldSessionError(
            () => _ = new LiveWorldSessionStore(unsupportedSnapshotRoot),
            LiveWorldSessionStoreError.UnsupportedSnapshotSchema);
        Equal(999, unsupported.SchemaVersion!.Value);
    });
}

static void TestManifestMutationBetweenValidationAndOpenRemainsTyped()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        _ = new LiveWorldSessionStore(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        var hookCalled = false;
        Action afterManifestValidation = () =>
        {
            hookCalled = true;
            File.WriteAllText(manifestPath, "{mutated-after-first-read");
        };
        var constructor = typeof(LiveWorldSessionStore).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(Action)],
            modifiers: null) ?? throw new Exception(
                "Live-world between-manifest-read constructor is missing.");
        Exception? observed = null;
        try
        {
            _ = constructor.Invoke([root, afterManifestValidation]);
        }
        catch (System.Reflection.TargetInvocationException exception)
        {
            observed = exception.InnerException;
        }

        True(hookCalled);
        True(observed is LiveWorldSessionStoreException);
        Equal(
            LiveWorldSessionStoreError.CorruptSessionManifest,
            ((LiveWorldSessionStoreException)observed!).Error);
    });
}

static void TestDirectSceneSessionSimultaneousAppendsUseFreshManifest()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "scene");
        const int writerCount = 4;
        const int appendsPerWriter = 4;
        var stores = Enumerable.Range(0, writerCount)
            .Select(_ => new SceneSessionStore(root))
            .ToArray();
        var sessionId = stores[0].Manifest.SessionId;
        using var ready = new CountdownEvent(writerCount);
        using var start = new ManualResetEventSlim(false);
        using var wave = new Barrier(writerCount);
        var observations = new StoredFrameObservation?[writerCount * appendsPerWriter];
        var errors = new Exception?[writerCount];
        var tasks = Enumerable.Range(0, writerCount).Select(writer => Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                for (var append = 0; append < appendsPerWriter; append++)
                {
                    True(wave.SignalAndWait(TimeSpan.FromSeconds(5)));
                    observations[writer * appendsPerWriter + append] = stores[writer].Append(
                        SolidFrame(
                            16,
                            9,
                            (byte)(writer * 50),
                            (byte)(append * 50),
                            (byte)(255 - writer * 50)),
                        sessionId,
                        sessionId,
                        timestamp: writer * appendsPerWriter + append + 1,
                        cameraPosition: new AtlasPoint(writer, append),
                        solveWidth: 16,
                        excludedRects: []);
                }
            }
            catch (Exception exception)
            {
                errors[writer] = exception;
            }
        })).ToArray();

        True(ready.Wait(TimeSpan.FromSeconds(2)));
        start.Set();
        True(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)));
        if (errors.Any(error => error is not null))
        {
            throw new AggregateException(errors.Where(error => error is not null)!);
        }

        const int expectedCount = writerCount * appendsPerWriter;
        var reopened = new SceneSessionStore(root);
        Equal(expectedCount, reopened.Manifest.Observations.Count);
        Equal(expectedCount, reopened.Manifest.NextObservationId);
        True(reopened.Manifest.Observations.Select(value => value.Id)
            .SequenceEqual(Enumerable.Range(0, expectedCount)));
        Equal(expectedCount, observations.Select(value => value!.Id).Distinct().Count());
        Equal(expectedCount, Directory.EnumerateFiles(
            Path.Combine(root, "frames"),
            "*.png",
            SearchOption.TopDirectoryOnly).Count());
        foreach (var observation in reopened.Manifest.Observations)
        {
            Equal(16, reopened.Source(observation).Width);
            Equal(9, reopened.Source(observation).Height);
        }
    });
}

static void TestDirectSceneSessionOverflowWritesNoOrphan()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "scene");
        var initial = new SceneSessionStore(root);
        File.WriteAllBytes(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(new SessionManifest(
                SessionManifest.CurrentSchemaVersion,
                initial.Manifest.SessionId,
                [],
                int.MaxValue)));
        var overflowed = new SceneSessionStore(root);

        Exception? observed = null;
        try
        {
            _ = overflowed.Append(
                SolidFrame(16, 9, 1, 2, 3),
                initial.Manifest.SessionId,
                initial.Manifest.SessionId,
                timestamp: 1,
                cameraPosition: new AtlasPoint(0, 0),
                solveWidth: 16,
                excludedRects: []);
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        True(observed is OverflowException);
        True(!Directory.EnumerateFiles(
            Path.Combine(root, "frames"),
            "*.png",
            SearchOption.TopDirectoryOnly).Any());
        var unchanged = new SceneSessionStore(root).Manifest;
        Equal(int.MaxValue, unchanged.NextObservationId);
        Equal(0, unchanged.Observations.Count);
    });
}

static void TestDirectSceneSessionManifestFailureRollsBackEvidence()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "scene");
        var store = new SceneSessionStore(root);
        var sessionId = store.Manifest.SessionId;
        Exception? observed = null;
        using (var manifestBlocker = new FileStream(
                   Path.Combine(root, "manifest.json"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            try
            {
                _ = store.Append(
                    SolidFrame(16, 9, 4, 5, 6),
                    sessionId,
                    sessionId,
                    timestamp: 1,
                    cameraPosition: new AtlasPoint(0, 0),
                    solveWidth: 16,
                    excludedRects: []);
            }
            catch (Exception exception)
            {
                observed = exception;
            }
        }

        True(observed is IOException or UnauthorizedAccessException);
        var afterFailure = new SceneSessionStore(root);
        Equal(0, afterFailure.Manifest.NextObservationId);
        Equal(0, afterFailure.Manifest.Observations.Count);
        True(!Directory.EnumerateFiles(
            Path.Combine(root, "frames"),
            "*.png",
            SearchOption.TopDirectoryOnly).Any());

        var recovered = afterFailure.Append(
            SolidFrame(16, 9, 7, 8, 9),
            sessionId,
            sessionId,
            timestamp: 2,
            cameraPosition: new AtlasPoint(1, 0),
            solveWidth: 16,
            excludedRects: []);
        Equal(0, recovered.Id);
        Equal(16, afterFailure.Source(recovered).Width);
    });
}

static void TestLiveWorldSessionReparseSwapCannotRedirectHandleReadsOrWrites()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var redirected = Path.Combine(workspace, "redirected-frames");
        var store = new LiveWorldSessionStore(root);
        var source = store.Append(
            SolidFrame(16, 9, 10, 20, 30),
            1,
            new AtlasPoint(0, 0),
            16);
        var frames = Path.Combine(root, "frames");
        var originalFrames = Path.Combine(root, "frames-original");
        Directory.Move(frames, originalFrames);
        Directory.CreateDirectory(redirected);
        var sourceName = Path.GetFileName(source.ImagePath);
        var redirectedSource = Path.Combine(redirected, sourceName);
        File.Copy(Path.Combine(originalFrames, sourceName), redirectedSource);
        var redirectedSourceBytes = File.ReadAllBytes(redirectedSource);
        var junctionCreated = false;
        try
        {
            if (!TryCreateDirectoryJunction(frames, redirected, out var reason))
            {
                Console.WriteLine($"SKIP live world evidence reparse swap: {reason}");
                return;
            }
            junctionCreated = true;

            var validateHandle = typeof(LiveWorldSessionStore).GetMethod(
                "ValidateOpenedHandlePath",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Live-world opened-handle validator is missing.");
            Exception? readHandleFailure = null;
            using (var redirectedHandle = new FileStream(
                       Path.Combine(frames, sourceName),
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read | FileShare.Delete))
            {
                try
                {
                    validateHandle.Invoke(null, [redirectedHandle, Path.Combine(frames, sourceName)]);
                }
                catch (System.Reflection.TargetInvocationException exception)
                {
                    readHandleFailure = exception.InnerException;
                }
            }
            True(readHandleFailure is IOException);

            var durableWrite = typeof(LiveWorldSessionStore).GetMethod(
                "DurablyWriteEvidence",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Live-world durable evidence writer is missing.");
            var redirectedWrite = Path.Combine(frames, "00000001.png");
            Exception? writeHandleFailure = null;
            try
            {
                durableWrite.Invoke(null, [SolidFrame(16, 9, 40, 50, 60), redirectedWrite]);
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                writeHandleFailure = exception.InnerException;
            }
            True(writeHandleFailure is IOException);
            True(!File.Exists(Path.Combine(redirected, "00000001.png")));
            True(!Directory.EnumerateFiles(redirected)
                .Any(path => Path.GetFileName(path).Contains(
                    ".durable-",
                    StringComparison.Ordinal)));

            _ = ExpectLiveWorldSessionError(
                () => _ = store.Source(source.Id),
                LiveWorldSessionStoreError.CorruptSessionManifest);
            _ = ExpectLiveWorldSessionError(
                () => _ = store.Append(
                    SolidFrame(16, 9, 70, 80, 90),
                    2,
                    new AtlasPoint(1, 0),
                    16),
                LiveWorldSessionStoreError.CorruptSessionManifest);
            True(redirectedSourceBytes.SequenceEqual(File.ReadAllBytes(redirectedSource)));
        }
        finally
        {
            if (junctionCreated && Directory.Exists(frames)) Directory.Delete(frames);
            if (!Directory.Exists(frames) && Directory.Exists(originalFrames))
                Directory.Move(originalFrames, frames);
        }
    });
}

static void TestOwnedStagingRecoversAndPreservesUnrelatedFiles()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new LiveWorldSessionStore(root);
        var source = store.Append(
            SolidFrame(32, 18, 9, 19, 29),
            1,
            new AtlasPoint(0, 0),
            32);
        var frames = Path.Combine(root, "frames");
        var evidence = Path.Combine(
            root,
            source.ImagePath.Replace('/', Path.DirectorySeparatorChar));
        var recoverable = evidence + $".durable-{Guid.NewGuid():N}";
        File.Move(evidence, recoverable);

        var manifestTemporary = Path.Combine(
            root,
            $"manifest.json.tmp-{Guid.NewGuid():N}");
        var sceneTemporary = Path.Combine(
            root,
            $"scene.snapshot.tmp-{Guid.NewGuid():N}");
        var directPngTemporary = Path.Combine(
            frames,
            $"00000001.png.tmp-{Guid.NewGuid():N}");
        var durableTemporary = Path.Combine(
            frames,
            $"00000001.png.durable-{Guid.NewGuid():N}.tmp-{Guid.NewGuid():N}");
        var unreferencedDurable = Path.Combine(
            frames,
            $"00000001.png.durable-{Guid.NewGuid():N}");
        foreach (var path in new[]
                 {
                     manifestTemporary,
                     sceneTemporary,
                     directPngTemporary,
                     durableTemporary,
                     unreferencedDurable
                 })
        {
            File.WriteAllText(path, "owned staging");
        }

        var unrelatedRoot = Path.Combine(root, $"notes.tmp-{Guid.NewGuid():N}");
        var unrelatedFrame = Path.Combine(frames, $"notes.durable-{Guid.NewGuid():N}");
        var invalidSuffix = Path.Combine(frames, "00000001.png.durable-not-a-guid");
        File.WriteAllText(unrelatedRoot, "keep root");
        File.WriteAllText(unrelatedFrame, "keep frame");
        File.WriteAllText(invalidSuffix, "keep suffix");

        var reopened = new LiveWorldSessionStore(root);
        Equal(32, reopened.Source(source.Id).Width);
        True(File.Exists(evidence));
        True(!File.Exists(recoverable));
        True(!File.Exists(manifestTemporary));
        True(!File.Exists(sceneTemporary));
        True(!File.Exists(directPngTemporary));
        True(!File.Exists(durableTemporary));
        True(!File.Exists(unreferencedDurable));
        Equal("keep root", File.ReadAllText(unrelatedRoot));
        Equal("keep frame", File.ReadAllText(unrelatedFrame));
        Equal("keep suffix", File.ReadAllText(invalidSuffix));
    });
}

static void TestDurablePublicationNoOverwriteFailureIsRetryable()
{
    WithTemporaryDirectory(workspace =>
    {
        var root = Path.Combine(workspace, "world");
        var store = new LiveWorldSessionStore(root);
        var frames = Path.Combine(root, "frames");
        var blockedEvidence = Path.Combine(frames, "00000000.png");
        Directory.CreateDirectory(blockedEvidence);
        var sentinel = Path.Combine(blockedEvidence, "sentinel.txt");
        File.WriteAllText(sentinel, "do not overwrite");

        Exception? publicationFailure = null;
        try
        {
            _ = store.Append(
                SolidFrame(32, 18, 1, 2, 3),
                1,
                new AtlasPoint(0, 0),
                32);
        }
        catch (Exception exception)
        {
            publicationFailure = exception;
        }
        True(publicationFailure is IOException);
        Equal("do not overwrite", File.ReadAllText(sentinel));
        Equal(0, new SceneSessionStore(root).Manifest.Observations.Count);
        True(!Directory.EnumerateFiles(frames)
            .Any(path => Path.GetFileName(path).Contains(
                ".durable-",
                StringComparison.Ordinal)));

        Directory.Delete(blockedEvidence, recursive: true);
        var recovered = store.Append(
            SolidFrame(32, 18, 4, 5, 6),
            2,
            new AtlasPoint(1, 0),
            32);
        Equal(0, recovered.Id);
        Equal(32, store.Source(recovered.Id).Width);
    });
}

static bool TryCreateDirectoryJunction(string junctionPath, string targetPath, out string reason)
{
    if (!OperatingSystem.IsWindows())
    {
        reason = "directory junctions are Windows-only";
        return false;
    }
    try
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junctionPath);
        start.ArgumentList.Add(targetPath);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            reason = "cmd.exe could not start";
            return false;
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0
            || !Directory.Exists(junctionPath)
            || (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) == 0)
        {
            reason = $"mklink /J failed ({process.ExitCode}): {(error + output).Trim()}";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    catch (Exception exception) when (exception is IOException
        or InvalidOperationException or UnauthorizedAccessException)
    {
        reason = $"junction creation unavailable: {exception.Message}";
        return false;
    }
}

static void TestRoomBoundaryPreventsCrossRoomMotionAndAppearanceEdges()
{
    var tracker = new LiveWorldTracker();
    _ = tracker.Ingest(
        0,
        0,
        GlobalFeatureTexture(),
        new AtlasPoint(0, 0),
        320,
        localPose: new AtlasPoint(0, 0),
        captureGeneration: 1,
        localEpoch: 0,
        roomId: 0);
    var entered = tracker.Ingest(
        1,
        1,
        GlobalFeatureTexture(),
        new AtlasPoint(-700, 0),
        320,
        localPose: new AtlasPoint(-700, 0),
        captureGeneration: 1,
        localEpoch: 1,
        roomId: 1);

    True(entered.Snapshot.Observations.Select(value => value.RoomId)
        .SequenceEqual([0, 1]));
    Equal(0, entered.Snapshot.RelativeMotionEdges.Count);
    Equal(0, entered.Snapshot.LoopClosureEdges.Count);
    True(entered.Review.Rejections.Contains(
        LiveWorldTrackerRejection.NoEligibleReference));
}

static void TestOutwardLoopNeedsTwoConfirmationsThenClosesAndDistributesDrift()
{
    var tracker = new LiveWorldTracker();
    _ = tracker.Ingest(0, 0, GlobalFeatureTexture(), new AtlasPoint(0, 0), 320);
    _ = tracker.Ingest(1, 1, LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0), 320);
    _ = tracker.Ingest(2, 2, LiveWorldUnrelatedTexture(2), new AtlasPoint(100, 100), 320);
    _ = tracker.Ingest(3, 3, LiveWorldUnrelatedTexture(3), new AtlasPoint(0, 100), 320);

    var first = tracker.Ingest(
        4,
        4,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    True(first.AcceptedClosure is null);
    True(first.Review.Rejections.Contains(
        LiveWorldTrackerRejection.ConfirmationRequired));

    var closed = tracker.Ingest(
        5,
        5,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    True(closed.AcceptedClosure is not null);
    var closedPose = closed.Snapshot.Observations.Single(value => value.Id == 5);
    True(Math.Sqrt(
        closedPose.OptimizedPose.X * closedPose.OptimizedPose.X
        + closedPose.OptimizedPose.Y * closedPose.OptimizedPose.Y) < 4);
    True(closed.Revision > 1);
    Equal(1, closed.Snapshot.LoopClosureEdges.Count);
    True(closed.Review.MatchLines.Any(value =>
        value.FromObservationId == 0 && value.ToObservationId == 5));
    var movedInterior = closed.Snapshot.Observations.Single(value => value.Id == 1);
    True(movedInterior.OptimizedPose != movedInterior.RawPose);
}

static void TestOneFrameCandidateDoesNotCloseAndUnrelatedFramesAreRejected()
{
    var tracker = LiveWorldLoopReadyTracker();
    var candidate = tracker.Ingest(
        4,
        4,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    True(candidate.AcceptedClosure is null);
    True(candidate.Review.Rejections.Contains(
        LiveWorldTrackerRejection.ConfirmationRequired));

    var unrelated = tracker.Ingest(
        5,
        5,
        LiveWorldUnrelatedTexture(9),
        new AtlasPoint(8, 0),
        320);
    True(unrelated.AcceptedClosure is null);
    True(unrelated.Review.Rejections.Contains(
        LiveWorldTrackerRejection.MatcherRejected));
}

static void TestReloadedSnapshotRelocalizesAgainstSavedLandmarks()
{
    var original = LiveWorldLoopReadyTracker();
    var reloaded = new LiveWorldTracker(original.Snapshot);
    var first = reloaded.Ingest(
        4,
        4,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    True(first.Review.Rejections.Contains(
        LiveWorldTrackerRejection.ConfirmationRequired));
    var second = reloaded.Ingest(
        5,
        5,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    True(second.AcceptedClosure is not null);
}

static void TestReloadedVisitSearchesSavedFeaturesImmediatelyWithoutCrossVisitMotionEdge()
{
    var original = LiveWorldLoopReadyTracker();
    var priorEdgeCount = original.Snapshot.RelativeMotionEdges.Count;
    var reloaded = new LiveWorldTracker(original.Snapshot);

    var first = reloaded.Ingest(
        4,
        40,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);

    True(first.Review.Rejections.Contains(
        LiveWorldTrackerRejection.ConfirmationRequired));
    Equal(priorEdgeCount, first.Snapshot.RelativeMotionEdges.Count);
}

static void TestConfirmationComparesDriftWhileCameraContinuesMoving()
{
    var tracker = LiveWorldLoopReadyTracker();
    var first = tracker.Ingest(
        4,
        4,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    True(first.Review.Rejections.Contains(
        LiveWorldTrackerRejection.ConfirmationRequired));

    var second = tracker.Ingest(
        5,
        5,
        GlobalFeatureTexture(offsetX: -12),
        new AtlasPoint(20, 0),
        320);
    True(second.AcceptedClosure is not null);
}

static void TestRecentOverlapIsNotTreatedAsClosure()
{
    var tracker = new LiveWorldTracker();
    _ = tracker.Ingest(0, 0, GlobalFeatureTexture(), new AtlasPoint(0, 0), 320);
    var update = tracker.Ingest(
        1,
        1,
        GlobalFeatureTexture(),
        new AtlasPoint(10, 0),
        320);
    True(update.AcceptedClosure is null);
    True(update.Review.Rejections.Contains(
        LiveWorldTrackerRejection.RecentOrOverlappingReference));
    Equal(0, update.Snapshot.LoopClosureEdges.Count);
}

static void TestAcceptedClosureRequiresAnotherExcursionBeforeAddingMoreEdges()
{
    var tracker = LiveWorldLoopReadyTracker();
    _ = tracker.Ingest(4, 4, GlobalFeatureTexture(), new AtlasPoint(8, 0), 320);
    var closed = tracker.Ingest(
        5,
        5,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    True(closed.AcceptedClosure is not null);

    var nearbyCandidate = tracker.Ingest(
        6,
        6,
        GlobalFeatureTexture(),
        new AtlasPoint(12, 0),
        320);
    var nearbyConfirmation = tracker.Ingest(
        7,
        7,
        GlobalFeatureTexture(),
        new AtlasPoint(16, 0),
        320);

    True(nearbyCandidate.AcceptedClosure is null);
    True(nearbyConfirmation.AcceptedClosure is null);
    Equal(1, nearbyConfirmation.Snapshot.LoopClosureEdges.Count);
    True(nearbyCandidate.Review.Rejections.Contains(
        LiveWorldTrackerRejection.RecentOrOverlappingReference));
}

static void TestRepeatedAppearanceIsRejectedAsAClosureCandidate()
{
    var tracker = new LiveWorldTracker();
    _ = tracker.Ingest(0, 0, GlobalFeatureTiledTexture(), new AtlasPoint(0, 0), 320);
    _ = tracker.Ingest(1, 1, LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0), 320);
    _ = tracker.Ingest(2, 2, LiveWorldUnrelatedTexture(2), new AtlasPoint(100, 100), 320);
    _ = tracker.Ingest(3, 3, LiveWorldUnrelatedTexture(3), new AtlasPoint(0, 100), 320);
    var update = tracker.Ingest(
        4,
        4,
        GlobalFeatureTiledTexture(),
        new AtlasPoint(8, 0),
        320);
    True(update.AcceptedClosure is null);
    True(update.Review.Rejections.Contains(
        LiveWorldTrackerRejection.MatcherRejected));
}

static void TestRecoveryReusesClosedReferenceWithoutPersistingFrames()
{
    var tracker = LiveWorldLoopReadyTracker();
    _ = tracker.Ingest(
        4,
        4,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    var closed = tracker.Ingest(
        5,
        5,
        GlobalFeatureTexture(),
        new AtlasPoint(8, 0),
        320);
    Equal(0, closed.AcceptedClosure!.FromKeyframeId);
    var before = tracker.Snapshot;

    tracker.BeginRecoveryEpoch();
    var first = tracker.Recover(
        GlobalFeatureTexture(),
        new AtlasPoint(80, 0),
        320);
    var confirmed = tracker.Recover(
        GlobalFeatureTexture(),
        new AtlasPoint(80, 0),
        320);

    Equal(0, first.MatchedReferenceKeyframeId!.Value);
    Equal(0, confirmed.MatchedReferenceKeyframeId!.Value);
    ClosePoint(new AtlasPoint(0, 0), confirmed.ConfirmedPlacement!.CameraPose);
    ClosePoint(new AtlasPoint(-80, 0), confirmed.ConfirmedPlacement.Correction);
    True(confirmed.ConfirmedPlacement.ConfirmationReferenceKeyframeIds
        .SequenceEqual([0, 0]));
    Equal(before.MapRevision, tracker.Snapshot.MapRevision);
    Equal(before.MapRevision, confirmed.Revision);
    EqualLiveWorldSnapshots(before, tracker.Snapshot);
    EqualLiveWorldSnapshots(before, confirmed.Snapshot);
    Equal(before.Observations.Count, confirmed.Snapshot.Observations.Count);
    Equal(before.Keyframes.Count, confirmed.Snapshot.Keyframes.Count);
}

static void TestRecoveryConfirmsAcrossDifferentReferenceKeyframes()
{
    var accepted = LiveWorldRecoveryConfirmation.ReferenceIds(
        0,
        new AtlasPoint(-8, 1),
        3,
        new AtlasPoint(-7, 0),
        4);
    True(accepted is not null && accepted.SequenceEqual([0, 3]));
    True(LiveWorldRecoveryConfirmation.ReferenceIds(
        0,
        new AtlasPoint(-8, 1),
        0,
        new AtlasPoint(30, 0),
        4) is null);
}

static void TestRecoveryKeepsTentativeMatchAcrossOneUnmatchedFrame()
{
    var tracker = LiveWorldLoopReadyTracker();
    var proposed = new AtlasPoint(80, 0);
    var first = tracker.Recover(GlobalFeatureTexture(), proposed, 320);
    True(first.Review.Rejections.Contains(
        LiveWorldTrackerRejection.ConfirmationRequired));

    var gap = tracker.Recover(LiveWorldUnrelatedTexture(9), proposed, 320);
    True(gap.Review.Rejections.Contains(
        LiveWorldTrackerRejection.MatcherRejected));

    var second = tracker.Recover(GlobalFeatureTexture(), proposed, 320);
    ClosePoint(new AtlasPoint(0, 0), second.ConfirmedPlacement!.CameraPose);
}

static void TestRecoveryExpiresTentativeMatchAfterLongUnmatchedGap()
{
    var tracker = LiveWorldLoopReadyTracker();
    var proposed = new AtlasPoint(80, 0);
    _ = tracker.Recover(GlobalFeatureTexture(), proposed, 320);
    for (var index = 0; index < 5; index++)
    {
        _ = tracker.Recover(
            LiveWorldUnrelatedTexture(20 + index),
            proposed,
            320);
    }
    var afterGap = tracker.Recover(GlobalFeatureTexture(), proposed, 320);
    True(afterGap.ConfirmedPlacement is null);
    True(afterGap.Review.Rejections.Contains(
        LiveWorldTrackerRejection.ConfirmationRequired));
}

static void TestMotionEdgesUseRawCoordinatesAfterPoseOptimization()
{
    var tracker = LiveWorldLoopReadyTracker();
    _ = tracker.Ingest(4, 4, GlobalFeatureTexture(), new AtlasPoint(8, 0), 320);
    _ = tracker.Ingest(5, 5, GlobalFeatureTexture(), new AtlasPoint(8, 0), 320);
    var optimized = tracker.Snapshot.Observations.Single(value => value.Id == 5);
    True(optimized.RawPose != optimized.OptimizedPose);

    var update = tracker.Ingest(
        6,
        6,
        LiveWorldUnrelatedTexture(8),
        new AtlasPoint(200, 0),
        320,
        searchForClosure: false);
    var edge = update.Snapshot.RelativeMotionEdges.Last();
    Equal(5, edge.FromObservationId);
    Equal(6, edge.ToObservationId);
    Equal(192.0, edge.DeltaX);
    Equal(0.0, edge.DeltaY);
}

static void TestMotionEdgesNeverBridgeLocalTrackingEpochs()
{
    var tracker = new LiveWorldTracker();
    _ = tracker.Ingest(
        0,
        0,
        GlobalFeatureTexture(),
        new AtlasPoint(0, 0),
        320,
        searchForClosure: false,
        localPose: new AtlasPoint(0, 0),
        captureGeneration: 9,
        localEpoch: 0);
    _ = tracker.Ingest(
        1,
        1,
        LiveWorldUnrelatedTexture(1),
        new AtlasPoint(100, 0),
        320,
        searchForClosure: false,
        localPose: new AtlasPoint(100, 0),
        captureGeneration: 9,
        localEpoch: 1);
    var sameEpoch = tracker.Ingest(
        2,
        2,
        LiveWorldUnrelatedTexture(2),
        new AtlasPoint(200, 0),
        320,
        searchForClosure: false,
        localPose: new AtlasPoint(200, 0),
        captureGeneration: 9,
        localEpoch: 1);

    Equal(1, sameEpoch.Snapshot.RelativeMotionEdges.Count);
    Equal(1, sameEpoch.Snapshot.RelativeMotionEdges[0].FromObservationId);
    Equal(2, sameEpoch.Snapshot.RelativeMotionEdges[0].ToObservationId);
}

static LiveWorldTracker LiveWorldLoopReadyTracker()
{
    var tracker = new LiveWorldTracker();
    _ = tracker.Ingest(0, 0, GlobalFeatureTexture(), new AtlasPoint(0, 0), 320);
    _ = tracker.Ingest(1, 1, LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0), 320);
    _ = tracker.Ingest(2, 2, LiveWorldUnrelatedTexture(2), new AtlasPoint(100, 100), 320);
    _ = tracker.Ingest(3, 3, LiveWorldUnrelatedTexture(3), new AtlasPoint(0, 100), 320);
    return tracker;
}

static BgraFrame LiveWorldUnrelatedTexture(int seed) =>
    GlobalFeatureImage((x, y) =>
    {
        var value = (((x * (7 + seed)) ^ (y * (53 + seed * 3))) + seed * 41) & 255;
        return (255 - value, value / 5, value, 255);
    });

static void TestThreeReturningPassesCloseAfterReopeningWorldFromDisk()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            // Repeated endpoint samples make cross-visit confirmation explicit.
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0)),
            (LiveWorldUnrelatedTexture(2), new AtlasPoint(100, 100)),
            (LiveWorldUnrelatedTexture(3), new AtlasPoint(0, 100)),
            (GlobalFeatureTexture(), new AtlasPoint(8, 0)),
            (GlobalFeatureTexture(), new AtlasPoint(8, 0)));
        var manifestBefore = File.ReadAllBytes(Path.Combine(root, "manifest.json"));
        var outputPath = Path.Combine(root, "replay-report.json");

        var report = new LiveWorldReplayEvaluator(store).Evaluate(
            new LiveWorldReplayOptions(
                RepeatCount: 3,
                ReopenSnapshotBetweenPasses: true,
                RouteMode: LiveWorldReplayRouteMode.Returning),
            outputPath);

        True(report.Passed);
        Equal(3, report.Passes.Count);
        True(report.ReopenedSnapshotBetweenPasses);
        True(report.Passes.All(pass => pass.RecordingReturnsToStart));
        True(report.Passes[0].NewLoopClosures >= 1);
        True(report.Passes.All(pass => pass.ClosureRequirementMet));
        True(report.Passes.All(pass => pass.Passed));
        True(report.Passes[1].SavedKeyframeCount > report.Passes[0].SavedKeyframeCount);
        True(report.Passes[2].SavedKeyframeCount > report.Passes[1].SavedKeyframeCount);
        True(report.Passes[1].SavedLandmarkCount > report.Passes[0].SavedLandmarkCount);
        True(report.Passes[2].SavedRevision > report.Passes[1].SavedRevision);
        Equal(0, report.FailureReasons.Count);
        True(report.Passes.All(pass => pass.GlobalSearchCount <= pass.ObservationCount));
        EqualLiveWorldReplayReports(ReadLiveWorldReplayReport(outputPath), report);
        True(File.ReadAllBytes(Path.Combine(root, "manifest.json"))
            .SequenceEqual(manifestBefore));
    });
}

static void TestCommandArgumentsWriteReport()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0)));
        var reportPath = Path.Combine(root, "command-report.json");

        var report = LiveWorldReplayEvaluator.Run([
            "--world-replay-session", root,
            "--world-replay-report", reportPath,
            "--world-replay-repeats", "1",
            "--world-replay-reopen"
        ]);

        Equal(1, report.RepeatCount);
        True(report.ReopenedSnapshotBetweenPasses);
        Equal(LiveWorldReplayRouteMode.OneWay, report.RouteMode);
        EqualLiveWorldReplayReports(ReadLiveWorldReplayReport(reportPath), report);
        var text = File.ReadAllText(reportPath);
        True(text.Contains(Environment.NewLine, StringComparison.Ordinal));
        True(text.IndexOf("\"failureReasons\"", StringComparison.Ordinal)
            < text.IndexOf("\"globalSearchHz\"", StringComparison.Ordinal));
    });
}

static void TestReturningRouteMustBeExplicitOnCommandLine()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)));

        var report = LiveWorldReplayEvaluator.Run([
            "--world-replay-session", root,
            "--world-replay-repeats", "1",
            "--world-replay-returning"
        ]);

        Equal(LiveWorldReplayRouteMode.Returning, report.RouteMode);
        True(report.Passes[0].RecordingReturnsToStart);
        True(report.Passes[0].ClosureRequired);
    });
}

static void TestSerializedSnapshotRelocalizesStoredPNGEvidence()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0)),
            (LiveWorldUnrelatedTexture(2), new AtlasPoint(100, 100)),
            (LiveWorldUnrelatedTexture(3), new AtlasPoint(0, 100)));
        var source = store.Manifest.Observations.ToArray();
        var frames = source.Select(store.Source).ToArray();
        var original = new LiveWorldTracker();
        for (var index = 0; index < source.Length; index++)
        {
            _ = original.Ingest(
                index,
                source[index].Timestamp,
                frames[index],
                source[index].CameraPosition,
                source[index].SolveWidth,
                sourceObservationId: source[index].Id);
        }

        var jsonOptions = LiveWorldReplayJsonOptions();
        var persisted = JsonSerializer.Deserialize<LiveWorldSnapshot>(
            JsonSerializer.SerializeToUtf8Bytes(original.Snapshot, jsonOptions),
            jsonOptions) ?? throw new InvalidOperationException(
                "Serialized live-world snapshot did not reopen.");
        var reopened = new LiveWorldTracker(persisted);
        var first = reopened.Ingest(
            4,
            10,
            frames[0],
            new AtlasPoint(8, 0),
            320,
            sourceObservationId: source[0].Id);
        var closed = reopened.Ingest(
            5,
            10.5,
            frames[0],
            new AtlasPoint(8, 0),
            320,
            sourceObservationId: source[0].Id);

        True(first.Review.Rejections.Contains(
            LiveWorldTrackerRejection.ConfirmationRequired));
        True(closed.AcceptedClosure is not null);
    });
}

static void TestNonReturningRouteReportsNoClosureRequirement()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0)),
            (LiveWorldUnrelatedTexture(2), new AtlasPoint(220, 0)));

        var report = new LiveWorldReplayEvaluator(store).Evaluate();

        True(report.Passed);
        True(report.Passes.All(pass => !pass.RecordingReturnsToStart));
        True(report.Passes.All(pass => !pass.ClosureRequired));
        True(report.Passes.All(pass =>
            pass.NewLoopClosures == 0 && !pass.UnexpectedClosure));
    });
}

static void TestInternalRevisitDoesNotMakeOneWayRouteAClosedLoop()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0)),
            (LiveWorldUnrelatedTexture(2), new AtlasPoint(100, 100)),
            (LiveWorldUnrelatedTexture(3), new AtlasPoint(0, 100)),
            (GlobalFeatureTexture(), new AtlasPoint(8, 0)),
            (GlobalFeatureTexture(), new AtlasPoint(8, 0)),
            (LiveWorldUnrelatedTexture(4), new AtlasPoint(220, 0)));

        var report = new LiveWorldReplayEvaluator(store).Evaluate(
            new LiveWorldReplayOptions(RepeatCount: 1));
        var pass = report.Passes[0];

        True(!pass.RecordingReturnsToStart);
        True(!pass.ClosureRequired);
        Equal(1, pass.NewLoopClosures);
        True(!pass.UnexpectedClosure);
        True(pass.Passed);
    });
}

static void TestMatchingEndpointAppearanceAcrossRoomsDoesNotCreateLoopIntent()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        var startRoom = Guid.NewGuid();
        var endRoom = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var route = new[]
        {
            (Frame: GlobalFeatureTexture(), Room: startRoom, Pose: new AtlasPoint(0, 0)),
            (Frame: LiveWorldUnrelatedTexture(1), Room: startRoom, Pose: new AtlasPoint(100, 0)),
            (Frame: GlobalFeatureTexture(), Room: endRoom, Pose: new AtlasPoint(220, 0)),
            (Frame: GlobalFeatureTexture(), Room: endRoom, Pose: new AtlasPoint(220, 0))
        };
        for (var index = 0; index < route.Length; index++)
        {
            _ = store.Append(
                route[index].Frame,
                route[index].Room,
                visitId,
                index * 0.5,
                route[index].Pose,
                320,
                []);
        }

        var report = new LiveWorldReplayEvaluator(store).Evaluate(
            new LiveWorldReplayOptions(RepeatCount: 1));
        var pass = report.Passes[0];

        True(pass.EndpointFeatureMatchFound);
        True(!pass.RecordingReturnsToStart);
        True(!pass.ClosureRequired);
        Equal(0, pass.NewLoopClosures);
        True(!pass.UnexpectedClosure);
    });
}

static void TestDefaultOneWayRouteCannotBeReclassifiedFromVisionCoordinates()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0)),
            (LiveWorldUnrelatedTexture(2), new AtlasPoint(4, 0)));

        var report = new LiveWorldReplayEvaluator(store).Evaluate(
            new LiveWorldReplayOptions(RepeatCount: 1));

        Equal(LiveWorldReplayRouteMode.OneWay, report.RouteMode);
        True(!report.Passes[0].RecordingReturnsToStart);
        True(!report.Passes[0].ClosureRequired);
    });
}

static void TestLoopIntentUsesWiderDetectionBoundButMustMeetTightOptimizedBound()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        AppendLiveWorldReplayRoute(
            store,
            (GlobalFeatureTexture(), new AtlasPoint(0, 0)),
            (LiveWorldUnrelatedTexture(1), new AtlasPoint(100, 0)),
            (LiveWorldUnrelatedTexture(2), new AtlasPoint(100, 100)),
            (LiveWorldUnrelatedTexture(3), new AtlasPoint(0, 100)),
            (GlobalFeatureTexture(), new AtlasPoint(18, 0)),
            (GlobalFeatureTexture(), new AtlasPoint(18, 0)));

        var report = new LiveWorldReplayEvaluator(store).Evaluate(
            new LiveWorldReplayOptions(
                RepeatCount: 1,
                ReturnThresholdPixels: 12,
                LoopDetectionThresholdPixels: 24,
                RouteMode: LiveWorldReplayRouteMode.Returning));
        var pass = report.Passes[0];

        True(pass.RecordingReturnsToStart);
        True(pass.ClosureRequired);
        Equal(24.0, report.LoopDetectionThresholdPixels);
        True(pass.Passed);
        True(pass.OptimizedReturnError <= 12);
    });
}

static void AppendLiveWorldReplayRoute(
    SceneSessionStore store,
    params (BgraFrame Frame, AtlasPoint Pose)[] route)
{
    var roomId = Guid.NewGuid();
    var visitId = Guid.NewGuid();
    for (var index = 0; index < route.Length; index++)
    {
        _ = store.Append(
            route[index].Frame,
            roomId,
            visitId,
            index * 0.5,
            route[index].Pose,
            320,
            []);
    }
}

static JsonSerializerOptions LiveWorldReplayJsonOptions() => new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    Converters =
    {
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase)
    }
};

static LiveWorldReplayReport ReadLiveWorldReplayReport(string path) =>
    JsonSerializer.Deserialize<LiveWorldReplayReport>(
        File.ReadAllBytes(path),
        LiveWorldReplayJsonOptions()) ?? throw new InvalidOperationException(
            "Live-world replay report did not decode.");

static void EqualLiveWorldReplayReports(
    LiveWorldReplayReport expected,
    LiveWorldReplayReport actual)
{
    Equal(expected.SchemaVersion, actual.SchemaVersion);
    Equal(expected.RepeatCount, actual.RepeatCount);
    Equal(expected.GlobalSearchHz, actual.GlobalSearchHz);
    Equal(expected.ReturnThresholdPixels, actual.ReturnThresholdPixels);
    Equal(expected.LoopDetectionThresholdPixels, actual.LoopDetectionThresholdPixels);
    Equal(expected.ReopenedSnapshotBetweenPasses, actual.ReopenedSnapshotBetweenPasses);
    Equal(expected.RouteMode, actual.RouteMode);
    True(expected.Passes.SequenceEqual(actual.Passes));
    Equal(expected.Passed, actual.Passed);
    True(expected.FailureReasons.SequenceEqual(actual.FailureReasons));
}

static void TestRecoversLargeHorizontalAndVerticalOffsetWithoutPosePrior()
{
    var matcher = new GlobalFeatureMatcher();
    var id = Guid.NewGuid();
    var reference = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(500, 900),
        320,
        id);
    True(reference is not null);

    var match = matcher.Match(
        GlobalFeatureTexture(offsetX: 72, offsetY: -36),
        [reference!]);

    True(match is not null);
    Equal(id, match!.ReferenceId);
    CloseWithin(428, match.ProposedCameraPose.X, 5);
    CloseWithin(864, match.ProposedCameraPose.Y, 5);
    True(match.InlierReferenceFeatureIds.Count >= 6);
    True(match.AmbiguityRatio < 0.8);
}

static void TestRepeatPatternIsRejectedAsAmbiguous()
{
    var matcher = new GlobalFeatureMatcher();
    var reference = matcher.MakeReference(
        GlobalFeatureTiledTexture(),
        new AtlasPoint(0, 0),
        320);
    True(reference is not null);
    True(matcher.Match(
        GlobalFeatureTiledTexture(offsetX: 32),
        [reference!]) is null);
}

static void TestHUDExclusionCannotProvideMatch()
{
    var matcher = new GlobalFeatureMatcher();
    var exclusion = new StencilRect(0, 128, 320, 52);
    var reference = matcher.MakeReference(
        GlobalFeatureHudOnly(world: false),
        new AtlasPoint(0, 0),
        320);
    True(reference is not null);
    True(matcher.MakeReference(
        GlobalFeatureHudOnly(world: false),
        new AtlasPoint(0, 0),
        320,
        excluding: [exclusion]) is null);
    True(matcher.Match(
        GlobalFeatureHudOnly(world: true),
        [reference!],
        excluding: [exclusion]) is null);
}

static void TestUnrelatedFrameIsRejected()
{
    var matcher = new GlobalFeatureMatcher();
    var reference = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(0, 0),
        320);
    True(reference is not null);
    True(matcher.Match(GlobalFeatureUnrelatedTexture(), [reference!]) is null);
}

static void TestReferenceOrderingIsDeterministic()
{
    var matcher = new GlobalFeatureMatcher();
    var a = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(100, 100),
        320,
        Guid.Parse("00000000-0000-0000-0000-000000000001"));
    var b = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(700, 700),
        320,
        Guid.Parse("00000000-0000-0000-0000-000000000002"));
    True(a is not null && b is not null);
    var frame = GlobalFeatureTexture(offsetX: 40, offsetY: 20);
    var first = matcher.Match(frame, [b!, a!]);
    var second = matcher.Match(frame, [a!, b!]);
    True(Equals(first, second));
    True(first is null);
}

static void TestOverlappingReferencesThatAgreeOnWorldPoseReinforceMatch()
{
    var matcher = new GlobalFeatureMatcher();
    var a = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(100, 100),
        320,
        Guid.Parse("00000000-0000-0000-0000-000000000001"));
    True(a is not null);
    var b = a! with
    {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
        CameraPose = new AtlasPoint(102, 101)
    };

    var match = matcher.Match(
        GlobalFeatureTexture(offsetX: 40, offsetY: 20),
        [a, b],
        currentSolveWidth: 320);

    True(match is not null);
    CloseWithin(60, match!.ProposedCameraPose.X, 5);
    CloseWithin(120, match.ProposedCameraPose.Y, 5);
}

static void TestSpatiallyDiverseShortlistFindsTargetBeyondSignatureCap()
{
    var matcher = new GlobalFeatureMatcher();
    var targetId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");
    var target = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(4_000, 4_000),
        320,
        targetId);
    True(target is not null);
    var distractors = Enumerable.Range(1, 32).Select(index =>
        target! with
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{index:x12}"),
            CameraPose = new AtlasPoint(0, 0),
            Features = target.Features.Select(feature => feature with
            {
                Descriptor = feature.Descriptor.Reverse().ToArray()
            }).ToArray()
        }).ToArray();
    var references = distractors.Append(target!).ToArray();
    var frame = GlobalFeatureTexture(offsetX: 24, offsetY: -12);

    var first = matcher.Match(frame, references);
    Equal(targetId, first?.ReferenceId ?? Guid.Empty);

    // The confirmation scan advances the normal rotation, but reserves the
    // accepted reference so a two-scan closure can still confirm it.
    var confirmation = matcher.Match(
        frame,
        references,
        preferredReferenceId: targetId);
    Equal(targetId, confirmation?.ReferenceId ?? Guid.Empty);
}

static void TestSelfPlacementCheckUsesBoundedDescriptorCandidates()
{
    var matcher = new GlobalFeatureMatcher();
    var reference = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(0, 0),
        320);
    True(reference is not null);

    _ = matcher.Match(
        GlobalFeatureTexture(offsetX: 18, offsetY: 9),
        [reference!]);

    var count = reference!.Features.Count;
    True(matcher.LastSelfPlacementPairChecks > 0);
    True(matcher.LastSelfPlacementPairChecks <= count * (count - 1));
}

static void TestSelfPlacementRejectsConsensusSplitAcrossHistogramBoundary()
{
    var matcher = new GlobalFeatureMatcher();
    var reference = matcher.MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(0, 0),
        320);
    True(reference is not null);
    reference = reference! with
    {
        Features = Enumerable.Range(0, 6).SelectMany(index =>
        {
            var descriptor = Enumerable.Repeat((byte)(index * 50), 25).ToArray();
            var source = new AtlasPoint(10 + index * 10, 30 + index * 8);
            var repeated = new AtlasPoint(source.X + 20 + index, source.Y);
            return new[]
            {
                new GlobalFeatureReference.Feature(
                    index * 2,
                    source,
                    descriptor,
                    100,
                    1,
                    0),
                new GlobalFeatureReference.Feature(
                    index * 2 + 1,
                    repeated,
                    descriptor,
                    100,
                    1,
                    0)
            };
        }).ToArray()
    };

    _ = matcher.Match(GlobalFeatureTexture(), [reference]);

    True(matcher.LastSelfPlacementWasAmbiguous);
}

static void TestCurrentSolveWidthUsesCurrentFrameScaleForCamera()
{
    var reference = new GlobalFeatureMatcher().MakeReference(
        GlobalFeatureTexture(),
        new AtlasPoint(500, 900),
        320);
    True(reference is not null);
    var frame = GlobalFeatureTexture(offsetX: 72, offsetY: -36);
    var referenceScaleMatch = new GlobalFeatureMatcher().Match(frame, [reference!]);
    var currentScaleMatch = new GlobalFeatureMatcher().Match(
        frame,
        [reference!],
        currentSolveWidth: 160);

    True(referenceScaleMatch is not null);
    True(currentScaleMatch is not null);
    True(currentScaleMatch!.ProposedCameraPose.X
        > referenceScaleMatch!.ProposedCameraPose.X + 80);
    True(currentScaleMatch.ProposedCameraPose.Y
        > referenceScaleMatch.ProposedCameraPose.Y + 20);
}

static BgraFrame GlobalFeatureTexture(int offsetX = 0, int offsetY = 0) =>
    GlobalFeatureImage((x, y) =>
    {
        var sourceX = x - offsetX;
        var sourceY = y - offsetY;
        var value = (sourceX * 17 + sourceY * 31 + sourceX * sourceY % 89) & 255;
        return (value, 255 - value, value / 3, 255);
    });

static BgraFrame GlobalFeatureUnrelatedTexture() =>
    GlobalFeatureImage((x, y) =>
    {
        var value = ((x * 7) ^ (y * 53) ^ ((x + y) * 19)) & 255;
        return (255 - value, value / 5, value, 255);
    });

static BgraFrame GlobalFeatureTiledTexture(int offsetX = 0, int offsetY = 0) =>
    GlobalFeatureImage((x, y) =>
    {
        var sourceX = ((x - offsetX) % 32 + 32) % 32;
        var sourceY = ((y - offsetY) % 32 + 32) % 32;
        var value = (sourceX * 23 + sourceY * 11 + sourceX * sourceY) & 255;
        return (value, 255 - value, value / 2, 255);
    });

static BgraFrame GlobalFeatureHudOnly(bool world) =>
    GlobalFeatureImage((x, y) =>
    {
        if (y < 52)
        {
            var value = (x * 17 + y * 29 + x * y) & 255;
            return (value, 255 - value, value / 3, 255);
        }
        return world
            ? ((x * 3 + y * 5) & 63, 0, 0, 255)
            : (0, (x * 7 + y * 11) & 63, 0, 255);
    });

static BgraFrame GlobalFeatureImage(Func<int, int, (int Red, int Green, int Blue, int Alpha)> color)
{
    const int width = 320;
    const int height = 180;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var topY = 0; topY < height; topY++)
    {
        // Mac fixture coordinates are CoreGraphics/world-up; BGRA rows are top-origin.
        var logicalY = height - 1 - topY;
        for (var x = 0; x < width; x++)
        {
            var value = color(x, logicalY);
            var index = (topY * width + x) * BgraFrame.BytesPerPixel;
            pixels[index] = (byte)value.Blue;
            pixels[index + 1] = (byte)value.Green;
            pixels[index + 2] = (byte)value.Red;
            pixels[index + 3] = (byte)value.Alpha;
        }
    }
    return new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        pixels,
        0,
        DateTimeOffset.UnixEpoch);
}

static LiveWorldSnapshot OneObservationSessionWorld(
    StoredFrameObservation source,
    int revision,
    AtlasPoint? optimized = null) => new(
        mapRevision: revision,
        anchoredObservationId: 10,
        observations:
        [
            SessionObservation(
                10,
                source,
                roomId: 0,
                optimized ?? new AtlasPoint(0, 0))
        ]);

static LiveWorldSnapshot LiveWorldSessionFixture(
    StoredFrameObservation first,
    StoredFrameObservation second,
    int revision) => new(
        mapRevision: revision,
        anchoredObservationId: 10,
        observations:
        [
            SessionObservation(10, first, roomId: 0, new AtlasPoint(0, 0)),
            SessionObservation(11, second, roomId: 0, new AtlasPoint(4.5, 0))
        ],
        keyframes:
        [
            new LiveWorldKeyframe(20, 10, first.Id),
            new LiveWorldKeyframe(21, 11, second.Id)
        ],
        landmarks:
        [
            new LiveWorldLandmark(30, 20, first.Id, new AtlasPoint(2, 3),
                new AtlasPoint(2, 3), [0.1f, 0.2f], 1, 0.8),
            new LiveWorldLandmark(31, 20, first.Id, new AtlasPoint(4, 3),
                new AtlasPoint(4, 3), [0.3f, 0.4f], 1, 0.8),
            new LiveWorldLandmark(32, 20, first.Id, new AtlasPoint(6, 3),
                new AtlasPoint(6, 3), [0.5f, 0.6f], 1, 0.8)
        ],
        relativeMotionEdges:
        [
            new LiveWorldRelativeMotionEdge(40, 10, 11, 4, 0, 8)
        ],
        loopClosureEdges:
        [
            new LiveWorldLoopClosureEdge(50, 20, 21, [30, 31, 32], 4, 0, 3, 0.1)
        ]);

static LiveWorldObservation SessionObservation(
    int id,
    StoredFrameObservation source,
    int roomId,
    AtlasPoint optimized) => LiveWorldObservation.Create(
        id,
        source.Id,
        source.Timestamp,
        32,
        18,
        source.SolveWidth,
        16,
        source.ExcludedRects.Select(rectangle => new WorldRect(
            new AtlasPoint(rectangle.X, rectangle.Y),
            new WorldSize(rectangle.Width, rectangle.Height))).ToArray(),
        source.CameraPosition,
        optimized,
        roomId: roomId);

static void EqualLiveWorldSnapshots(
    LiveWorldSnapshot expected,
    LiveWorldSnapshot actual) =>
    Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

static LiveWorldSessionStoreException ExpectLiveWorldSessionError(
    Action action,
    LiveWorldSessionStoreError expected)
{
    try
    {
        action();
    }
    catch (LiveWorldSessionStoreException error)
    {
        Equal(expected, error.Error);
        return error;
    }
    throw new Exception($"Expected {expected}.");
}

static void TestTranslationPoseGraphLoop()
{
    var graph = new TranslationPoseGraph(
        [
            PoseNode(0, 100, 200, 0, 0),
            PoseNode(1, 110, 205, 10, 5),
            PoseNode(2, 120, 210, 20, 10),
            PoseNode(3, 130, 215, 30, 15)
        ],
        [
            PoseEdge(0, 0, 1, 10, 5, 1, TranslationPoseConstraintKind.Motion),
            PoseEdge(1, 1, 2, 10, 5, 1, TranslationPoseConstraintKind.Motion),
            PoseEdge(2, 2, 3, 10, 5, 1, TranslationPoseConstraintKind.Motion),
            PoseEdge(3, 3, 0, -24, -12, 80, TranslationPoseConstraintKind.LoopClosure)
        ],
        anchorId: 0,
        baseRevision: 41);
    var solution = graph.Solve();
    Equal(41, solution.BaseRevision);
    Equal(42, solution.Revision);
    ClosePoint(new AtlasPoint(0, 0), solution.Positions[0]);
    ClosePoint(new AtlasPoint(8, 4), solution.Positions[1], 0.03);
    ClosePoint(new AtlasPoint(16, 8), solution.Positions[2], 0.03);
    ClosePoint(new AtlasPoint(24, 12), solution.Positions[3], 0.03);
    Equal(new AtlasPoint(120, 210), graph.Nodes.Single(node => node.Id == 2).Raw);
}

static void TestTranslationPoseGraphOrder()
{
    var nodes = new[]
    {
        PoseNode(7, 0, 0, 3.25, -7.5),
        PoseNode(3, 0, 0, 0, 0),
        PoseNode(11, 0, 0, 0, 0)
    };
    var edges = new[]
    {
        PoseEdge(20, 7, 3, 4, 1, 2, TranslationPoseConstraintKind.Motion),
        PoseEdge(10, 3, 11, -2, 8, 3, TranslationPoseConstraintKind.Motion),
        PoseEdge(30, 11, 7, -2, -9, 2, TranslationPoseConstraintKind.LoopClosure)
    };
    var first = new TranslationPoseGraph(nodes, edges, 7).Solve();
    var second = new TranslationPoseGraph(nodes.AsEnumerable().Reverse().ToArray(), edges.AsEnumerable().Reverse().ToArray(), 7)
        .Solve();
    ClosePoint(new AtlasPoint(3.25, -7.5), first.Positions[7]);
    foreach (var id in first.Positions.Keys)
    {
        ClosePoint(first.Positions[id], second.Positions[id], 0.0000001);
    }
}

static void TestTranslationPoseGraphRejectsDisconnected()
{
    try
    {
        _ = new TranslationPoseGraph(
            [PoseNode(0, 0, 0, 0, 0), PoseNode(1, 0, 0, 0, 0)],
            [],
            0);
        throw new InvalidOperationException("Expected disconnected pose graph rejection.");
    }
    catch (TranslationPoseGraphException error)
    {
        Equal(TranslationPoseGraphError.DisconnectedGraph, error.Error);
    }
}

static void TestLiveWorldPoseOptimizer()
{
    var observations = Enumerable.Range(0, 4).Select(id => LiveWorldObservation.Create(
        id,
        id,
        id,
        640,
        360,
        640,
        64,
        [],
        new AtlasPoint(id * 10, id * 5),
        new AtlasPoint(id * 10, id * 5))).ToArray();
    var keyframes = Enumerable.Range(0, 4)
        .Select(id => new LiveWorldKeyframe(id, id, id)).ToArray();
    var landmarks = Enumerable.Range(0, 3).Select(index => new LiveWorldLandmark(
        100 + index,
        3,
        3,
        new AtlasPoint(20 + index, 30),
        new AtlasPoint(50 + index, 40),
        [0.1f + index],
        1,
        0.8)).ToArray();
    var snapshot = new LiveWorldSnapshot(
        mapRevision: 7,
        anchoredObservationId: 0,
        observations: observations,
        keyframes: keyframes,
        landmarks: landmarks,
        relativeMotionEdges:
        [
            new LiveWorldRelativeMotionEdge(0, 0, 1, 10, 5, 1),
            new LiveWorldRelativeMotionEdge(1, 1, 2, 10, 5, 1),
            new LiveWorldRelativeMotionEdge(2, 2, 3, 10, 5, 1)
        ],
        loopClosureEdges:
        [
            new LiveWorldLoopClosureEdge(
                0,
                3,
                0,
                [100, 101, 102],
                -24,
                -12,
                10,
                0.1)
        ]);
    var optimized = LiveWorldPoseOptimizer.Optimize(snapshot);
    Equal(8, optimized.MapRevision);
    ClosePoint(new AtlasPoint(8, 4), optimized.Observations[1].OptimizedPose, 0.03);
    ClosePoint(new AtlasPoint(16, 8), optimized.Observations[2].OptimizedPose, 0.03);
    ClosePoint(new AtlasPoint(24, 12), optimized.Observations[3].OptimizedPose, 0.03);
    for (var index = 0; index < observations.Length; index++)
    {
        Equal(observations[index].RawPose, optimized.Observations[index].RawPose);
    }
}

static TranslationPoseNode PoseNode(
    int id,
    double rawX,
    double rawY,
    double initialX,
    double initialY) => new(
        id,
        new AtlasPoint(rawX, rawY),
        new AtlasPoint(initialX, initialY));

static TranslationPoseConstraint PoseEdge(
    int id,
    int from,
    int to,
    double x,
    double y,
    double weight,
    TranslationPoseConstraintKind kind) => new(
        id,
        from,
        to,
        new AtlasPoint(x, y),
        weight,
        kind);

static LiveWorldSnapshot LiveWorldFixture() => new(
    mapRevision: 7,
    anchoredObservationId: 10,
    observations:
    [
        WorldObservation(10, 100),
        WorldObservation(11, 101, raw: new AtlasPoint(4, 0),
            optimized: new AtlasPoint(4.5, 0))
    ],
    keyframes:
    [
        new LiveWorldKeyframe(20, 10, 100),
        new LiveWorldKeyframe(21, 11, 101)
    ],
    landmarks:
    [
        new LiveWorldLandmark(30, 20, 100, new AtlasPoint(20, 30),
            new AtlasPoint(20, 30), [0.1f, 0.2f], 1, 0.8),
        new LiveWorldLandmark(31, 20, 100, new AtlasPoint(25, 30),
            new AtlasPoint(25, 30), [0.3f, 0.4f], 1, 0.8),
        new LiveWorldLandmark(32, 20, 100, new AtlasPoint(40, 20),
            new AtlasPoint(40, 20), [0.5f, 0.6f], 1, 0.8),
        new LiveWorldLandmark(33, 21, 101, new AtlasPoint(40, 20),
            new AtlasPoint(44, 20), [0.7f, 0.8f], 1, 0.8)
    ],
    relativeMotionEdges:
    [
        new LiveWorldRelativeMotionEdge(40, 10, 11, 4, 0, 8)
    ],
    loopClosureEdges:
    [
        new LiveWorldLoopClosureEdge(50, 20, 21, [30, 31, 32], 4, 0, 3, 0.1)
    ]);

static LiveWorldObservation WorldObservation(
    int id,
    int sourceId,
    int roomId = 0,
    AtlasPoint? raw = null,
    AtlasPoint? optimized = null) => LiveWorldObservation.Create(
        id,
        sourceId,
        id,
        640,
        360,
        640,
        240,
        [new WorldRect(new AtlasPoint(1, 2), new WorldSize(3, 4))],
        raw ?? new AtlasPoint(0, 0),
        optimized ?? new AtlasPoint(0, 0),
        roomId: roomId);

static void TestVisualRouteReplay()
{
    WithTemporaryDirectory(workspace =>
    {
        var samples = new[] { 0.0, 0.8, 1.6, 2.4 }.Select((shift, index) =>
            new VisualRouteSample(
                index,
                index * 0.1,
                "Tutorial_01",
                -shift,
                0,
                10,
                10,
                FractionalMotionGrid(shift))).ToArray();
        var sessionId = Guid.Parse("50000000-0000-4000-8000-000000000001");
        var session = new VisualRouteSession(
            VisualRouteSession.CurrentSchemaVersion,
            sessionId,
            new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero),
            640,
            360,
            samples);
        var store = new VisualRouteStore(Path.Combine(workspace, "route"));
        store.Save(session);
        var reportPath = Path.Combine(workspace, "route", "report.json");
        var report = new VisualRouteReplayEvaluator(store).Evaluate(
            new VisualRouteReplayOptions(
                RepeatCount: 3,
                MaximumMeanErrorPixels: 3,
                MaximumStepErrorPixels: 6,
                ReopenSessionBetweenPasses: true),
            reportPath);
        True(report.Passed);
        Equal(sessionId, report.SessionId);
        Equal(3, report.Passes.Count);
        True(report.Passes.All(pass => pass.Passed && pass.AcceptedSteps == 3));
        True(report.Passes.All(pass => pass.MeanErrorPixels < 3));
        True(File.Exists(reportPath));
        var reopened = store.Load();
        Equal(4, reopened.Samples.Count);
        Equal(samples[3].Grid.Luma[500], reopened.Samples[3].Grid.Luma[500]);
    });
}

static void TestVisualRouteWorld()
{
    WithTemporaryDirectory(workspace =>
    {
        var samples = new[] { 0.0, 0.8, 1.6, 2.4 }.Select((shift, index) =>
            new VisualRouteSample(
                index,
                index * 0.1,
                "Tutorial_01",
                1000 + index * 500,
                -700 + index * 300,
                10,
                10,
                FractionalMotionGrid(shift))).ToArray();
        var session = new VisualRouteSession(
            VisualRouteSession.CurrentSchemaVersion,
            Guid.Parse("50000000-0000-4000-8000-000000000002"),
            new DateTimeOffset(2026, 10, 8, 18, 30, 0, TimeSpan.Zero),
            640,
            360,
            samples);
        var route = new VisualRouteStore(Path.Combine(workspace, "route"));
        route.Save(session);
        var world = new LiveWorldSnapshotStore(Path.Combine(workspace, "world"));
        var report = VisualRouteWorldBuilder.BuildAndSave(route, world, keyframeInterval: 2);
        Equal(1, report.Rooms);
        Equal(4, report.Observations);
        Equal(3, report.RelativeMotionEdges);
        Equal(2, report.Keyframes);
        Equal(0, report.RejectedSteps);
        var persisted = world.Load();
        Equal(1, persisted.MapRevision);
        Equal(4, persisted.Observations.Count);
        True(persisted.Observations.All(observation => observation.RoomId == 0));

        var changedTruth = new VisualRouteSession(
            session.SchemaVersion,
            session.Id,
            session.CreatedAt,
            session.FrameWidth,
            session.FrameHeight,
            samples.Select(sample => sample with
            {
                CameraX = sample.CameraX * -9,
                CameraY = sample.CameraY + 12345
            }).ToArray());
        var rebuilt = VisualRouteWorldBuilder.Build(changedTruth, keyframeInterval: 2);
        Equal(
            persisted.Observations[^1].RawPose,
            rebuilt.Snapshot.Observations[^1].RawPose);
    });
}

static void TestVisualRouteWorldStationary()
{
    var grid = FractionalMotionGrid(0);
    var session = new VisualRouteSession(
        VisualRouteSession.CurrentSchemaVersion,
        Guid.Parse("50000000-0000-4000-8000-000000000003"),
        DateTimeOffset.UnixEpoch,
        640,
        360,
        [
            new VisualRouteSample(0, 0, "Tutorial_01", 0, 0, 10, 10, grid),
            new VisualRouteSample(1, 0.1, "Tutorial_01", 999, -999, 10, 10, grid)
        ]);
    var report = VisualRouteWorldBuilder.Build(session);
    Equal(2, report.Observations);
    Equal(1, report.RelativeMotionEdges);
    Equal(0, report.RejectedSteps);
    Equal(0.0, report.Snapshot.RelativeMotionEdges[0].DeltaX);
    Equal(0.0, report.Snapshot.RelativeMotionEdges[0].DeltaY);
}

static void TestVisualRouteWorldScenes()
{
    var grid = FractionalMotionGrid(0);
    var session = new VisualRouteSession(
        VisualRouteSession.CurrentSchemaVersion,
        Guid.Parse("50000000-0000-4000-8000-000000000004"),
        DateTimeOffset.UnixEpoch,
        640,
        360,
        [
            new VisualRouteSample(0, 0, "Room_A", 0, 0, 10, 10, grid),
            new VisualRouteSample(1, 0.1, "Room_B", 0, 0, 10, 10, grid),
            new VisualRouteSample(2, 0.2, "Room_A", 0, 0, 10, 10, grid)
        ]);
    var report = VisualRouteWorldBuilder.Build(session);
    Equal(2, report.Rooms);
    Equal(3, report.Observations);
    Equal(0, report.RelativeMotionEdges);
    Equal(3, report.Keyframes);
    Equal(report.Snapshot.Observations[0].RoomId, report.Snapshot.Observations[2].RoomId);
    True(report.Snapshot.Observations[0].RoomId != report.Snapshot.Observations[1].RoomId);
}

static void TestLaunchTuningOverridesAndClampsDeveloperSweepValues()
{
    var tuning = GroundTheoryTuning.LaunchTuning(
    [
        "HollowKnightVision",
        "--ground-threshold=20",
        "--ground-minimum-segment=44",
        "--ground-line-separation=-2",
        "--ground-occlusion-gap=180"
    ]);
    Equal(20, tuning.GroundThreshold);
    Equal(44, tuning.MinimumSegmentLength);
    Equal(0, tuning.LineSeparation);
    Equal(180, tuning.OcclusionMergeGap);
}

static void TestGroundEdgeDebugImageContainsFullResolutionOnePixelDifference()
{
    var image = GroundReferencePatternFrame(320, 180, 54);
    var debugImage = GroundLineDetector.DebugImage(image);
    Equal(image.Width, debugImage.Width);
    Equal(image.Height, debugImage.Height);
    var grayscale = new byte[debugImage.Width * debugImage.Height];
    for (var row = 0; row < debugImage.Height; row++)
    {
        var pixels = debugImage.Row(row);
        for (var x = 0; x < debugImage.Width; x++)
            grayscale[row * debugImage.Width + x] = pixels[x * BgraFrame.BytesPerPixel];
    }
    True(grayscale.Max() > 100);
    True(grayscale.Contains((byte)0));
}

static void TestGroundStageImageRequiresContiguousMinimumSegment()
{
    const int width = 12;
    const int height = 2;
    var ground = new byte[width * height];
    ground[0] = 200;
    for (var x = 0; x <= 5; x++) ground[width + x] = 200;
    for (var x = 7; x <= 11; x++) ground[width + x] = 200;
    var image = GroundLineDetector.GroundStageImage(
        new GroundComparisonAnalysis(width, height, ground),
        new GroundTheoryTuning(128, 6));

    True(GroundReferenceRgbaPixel(image, 0, 0).SequenceEqual(
        new byte[] { 0, 230, 255, 255 }));
    True(GroundReferenceRgbaPixel(image, 1, 1).SequenceEqual(
        new byte[] { 70, 255, 120, 255 }));
    True(GroundReferenceRgbaPixel(image, 6, 1).SequenceEqual(
        new byte[] { 0, 0, 0, 0 }));
    True(GroundReferenceRgbaPixel(image, 8, 1).SequenceEqual(
        new byte[] { 0, 230, 255, 255 }));
}

static void TestSharedLineAnalysisPreservesLinesDebugPixelsAndReferenceDetection()
{
    const int width = 160;
    const int height = 100;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var index = 0; index < width * height; index++)
    {
        var value = unchecked((byte)((index * 73) ^ ((index / width) * 131)));
        var offset = index * BgraFrame.BytesPerPixel;
        pixels[offset] = value;
        pixels[offset + 1] = value;
        pixels[offset + 2] = value;
        pixels[offset + 3] = byte.MaxValue;
    }
    var frame = new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        pixels,
        0,
        DateTimeOffset.UnixEpoch);
    var comparison = GroundLineDetector.Compare(GroundLineDetector.Analyze(frame));
    var theory = GroundLineDetector.Theory(comparison, GroundTheoryTuning.Default);

    var sharedLines = GroundLineDetector.CleanFloorLines(theory);
    var directLines = GroundLineDetector.CleanFloorLines(
        comparison,
        GroundTheoryTuning.Default);
    True(theory.SelectedRows.SequenceEqual([42, 96]));
    Equal(2, sharedLines.Count);
    Equal(42, sharedLines[0].Row);
    Equal(new PixelSpan(16, 144), sharedLines[0].XSpan);
    True(sharedLines[0].EvidenceSpans.SequenceEqual(
        [new PixelSpan(16, 16), new PixelSpan(24, 144)]));
    Equal(96, sharedLines[1].Row);
    Equal(new PixelSpan(16, 142), sharedLines[1].XSpan);
    True(sharedLines[1].EvidenceSpans.SequenceEqual(
    [
        new PixelSpan(16, 100),
        new PixelSpan(103, 107),
        new PixelSpan(112, 113),
        new PixelSpan(120, 121),
        new PixelSpan(127, 128),
        new PixelSpan(134, 135),
        new PixelSpan(141, 142)
    ]));
    Equal(directLines.Count, sharedLines.Count);
    for (var index = 0; index < directLines.Count; index++)
    {
        Equal(directLines[index].Row, sharedLines[index].Row);
        Equal(directLines[index].XSpan, sharedLines[index].XSpan);
        True(directLines[index].EvidenceSpans.SequenceEqual(
            sharedLines[index].EvidenceSpans));
    }

    var sharedDetection = GroundLineDetector.Detect(theory, 0, height);
    var directDetection = GroundLineDetector.Detect(
        comparison,
        0,
        height,
        GroundTheoryTuning.Default);
    True(sharedDetection is not null);
    True(directDetection is not null);
    Equal(directDetection is null, sharedDetection is null);
    if (directDetection is not null && sharedDetection is not null)
    {
        Equal(57.5, sharedDetection.FrameY);
        CloseWithin(0.08438972488220094, sharedDetection.Confidence, 1e-15);
        Equal(2, sharedDetection.Segments.Count);
        Equal(
            new GroundLineSegment(57.5, 16, 145, 0.08438972488220094),
            sharedDetection.Segments[0]);
        Equal(3.5, sharedDetection.Segments[1].FrameY);
        Equal(16, sharedDetection.Segments[1].MinimumFrameX);
        Equal(143, sharedDetection.Segments[1].MaximumFrameX);
        True(sharedDetection.Segments[1].Confidence > 0);
        True(sharedDetection.NotchFrameXs.SequenceEqual(
            [16d, 37, 58, 79, 100, 121, 142, 144,
             16, 37, 58, 79, 100, 121, 142, 142]));
        Equal(directDetection.FrameY, sharedDetection.FrameY);
        Equal(directDetection.Confidence, sharedDetection.Confidence);
        True(directDetection.Segments.SequenceEqual(sharedDetection.Segments));
        True(directDetection.NotchFrameXs.SequenceEqual(sharedDetection.NotchFrameXs));
    }

    var sharedDebug = GroundLineDetector.GroundStageImage(theory);
    var directDebug = GroundLineDetector.GroundStageImage(
        comparison,
        GroundTheoryTuning.Default);
    True(comparison.GroundPixels[4 * width + 144]
        >= GroundTheoryTuning.Default.GroundThreshold);
    Equal(0, theory.AcceptedRunsByRow[4].Count);
    True(GroundReferenceRgbaPixel(sharedDebug, 16, 42).SequenceEqual(
        new byte[] { 70, 255, 120, 255 }));
    True(GroundReferenceRgbaPixel(sharedDebug, 144, 4).SequenceEqual(
        new byte[] { 0, 230, 255, 255 }));
    True(GroundReferenceRgbaPixel(sharedDebug, 0, 0).SequenceEqual(
        new byte[] { 0, 0, 0, 0 }));
    Equal(directDebug.Width, sharedDebug.Width);
    Equal(directDebug.Height, sharedDebug.Height);
    Equal(directDebug.Stride, sharedDebug.Stride);
    True(directDebug.Pixels.SequenceEqual(sharedDebug.Pixels));

    var tracker = new GroundReferenceTracker();
    var blankFrame = new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        new byte[width * height * BgraFrame.BytesPerPixel],
        1,
        DateTimeOffset.UnixEpoch);
    GroundReferenceEstimate? estimate = null;
    for (var observation = 0;
         observation < GroundReferenceTracker.MinimumStableObservations;
         observation++)
    {
        estimate = tracker.Observe(
            blankFrame,
            default,
            width,
            null,
            [],
            theory);
    }
    True(estimate is not null);
    Equal(3.5, estimate!.WorldY);
}

static void TestCleanedImageDrawsAcceptedGroundLine()
{
    const int width = 20;
    const int height = 20;
    var ground = new byte[width * height];
    for (var x = 2; x <= 17; x++) ground[5 * width + x] = 255;
    var image = GroundLineDetector.CleanedImage(
        new GroundComparisonAnalysis(width, height, ground),
        new GroundTheoryTuning(128, 10));

    True(GroundReferenceRgbaPixel(image, 3, 5).SequenceEqual(
        new byte[] { 70, 255, 120, 255 }));
    True(GroundReferenceRgbaPixel(image, 9, 5).SequenceEqual(
        new byte[] { 70, 255, 120, 255 }));
}

static void TestDefaultDetectorBridgesKnownObjectOcclusion()
{
    var image = GroundReferencePatternFrame(
        320,
        180,
        54,
        occludedMinimumX: 120,
        occludedMaximumX: 200);
    var exclusion = new PixelRect(120, 0, 80, 180);
    var detection = GroundLineDetector.Detect(
        GroundLineDetector.Compare(GroundLineDetector.Analyze(image, [exclusion])),
        30,
        80,
        GroundTheoryTuning.Default);

    True(detection is not null);
    True(Math.Abs(detection!.FrameY - 54) <= 3);
    True(detection.Segments.Any(segment =>
        segment.MinimumFrameX < 120 && segment.MaximumFrameX > 200));
}

static void TestTrackerAcquiresFromStableImageEvidenceAndFollowsVerticalCamera()
{
    var tracker = new GroundReferenceTracker();
    var first = GroundReferencePatternFrame(320, 180, 54);
    var firstKnight = GroundReferenceMacRect(180, 145, 55, 30, 50);
    GroundReferenceEstimate? result = null;
    for (var index = 0; index < GroundReferenceTracker.MinimumStableObservations; index++)
    {
        result = tracker.Observe(
            first,
            default,
            320,
            firstKnight,
            [firstKnight]);
    }
    True(result is not null && Math.Abs(result.WorldY - 54) <= 3);
    True(result!.SupportingObservationCount >= 8);

    var cameraMoved = GroundReferencePatternFrame(320, 180, 34);
    var movedKnight = GroundReferenceMacRect(180, 145, 35, 30, 50);
    for (var index = 0; index < 3; index++)
    {
        result = tracker.Observe(
            cameraMoved,
            new AtlasPoint(20, 20),
            320,
            movedKnight,
            [movedKnight]);
    }
    True(result is not null && Math.Abs(result.WorldY - 54) <= 3);
    True(result!.MinimumWorldX > 15);
}

static void TestTrackerSwitchesToSustainedHigherGroundAndAcceptsWorldCorrection()
{
    var tracker = new GroundReferenceTracker();
    var low = GroundReferencePatternFrame(320, 180, 45);
    var lowKnight = GroundReferenceMacRect(180, 145, 46, 30, 50);
    for (var index = 0; index < GroundReferenceTracker.MinimumStableObservations; index++)
        _ = tracker.Observe(low, default, 320, lowKnight, []);

    var high = GroundReferencePatternFrame(320, 180, 105);
    var highKnight = GroundReferenceMacRect(180, 145, 106, 30, 50);
    GroundReferenceEstimate? result = null;
    for (var index = 0; index < GroundReferenceTracker.MinimumStableObservations; index++)
        result = tracker.Observe(high, default, 320, highKnight, []);
    True(result is not null && Math.Abs(result.WorldY - 105) <= 3);

    var corrected = tracker.ApplyWorldCorrection(new CameraVector(20, -7));
    True(corrected is not null && Math.Abs(corrected.WorldY - 98) <= 3);
    True(corrected!.NotchWorldXs.All(value => value >= 20));
}

static BgraFrame GroundReferencePatternFrame(
    int width,
    int height,
    int groundFrameY,
    int? occludedMinimumX = null,
    int? occludedMaximumX = null)
{
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var topY = 0; topY < height; topY++)
    {
        var frameY = height - 1 - topY;
        for (var x = 0; x < width; x++)
        {
            var occluded = occludedMinimumX is { } minimum
                && occludedMaximumX is { } maximum
                && x >= minimum
                && x < maximum;
            var value = occluded ? (byte)0 : frameY >= groundFrameY ? (byte)185 : (byte)25;
            var offset = (topY * width + x) * BgraFrame.BytesPerPixel;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = byte.MaxValue;
        }
    }
    return new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        pixels,
        0,
        DateTimeOffset.UnixEpoch);
}

static PixelRect GroundReferenceMacRect(
    int imageHeight,
    int x,
    int y,
    int width,
    int height) => new(x, imageHeight - y - height, width, height);

static byte[] GroundReferenceRgbaPixel(BgraFrame image, int x, int y)
{
    var row = image.Row(y);
    var offset = x * BgraFrame.BytesPerPixel;
    return [row[offset + 2], row[offset + 1], row[offset], row[offset + 3]];
}

static void TestGroundKernelPolarity()
{
    const int width = 40;
    const int height = 12;
    var matching = new byte[width * height];
    for (var row = 4; row <= 7; row++)
    {
        for (var x = 0; x < width; x++) matching[row * width + x] = 255;
    }
    var result = GroundLineDetector.GroundDetectPixels(
        new GroundEdgeAnalysis(width, height, matching));
    Equal((byte)255, result[4 * width + 20]);

    var opposite = new byte[width * height];
    for (var row = 0; row <= 3; row++)
    {
        for (var x = 0; x < width; x++) opposite[row * width + x] = 255;
    }
    result = GroundLineDetector.GroundDetectPixels(
        new GroundEdgeAnalysis(width, height, opposite));
    Equal((byte)0, result[4 * width + 20]);
}

static void TestGroundOcclusionAndStep()
{
    const int width = 220;
    const int height = 30;
    var pixels = new byte[width * height];
    for (var x = 0; x <= 39; x++) pixels[10 * width + x] = 220;
    for (var x = 160; x <= 199; x++) pixels[10 * width + x] = 220;
    for (var x = 40; x <= 159; x++) pixels[14 * width + x] = 255;
    var lines = GroundLineDetector.CleanFloorLines(
        new GroundComparisonAnalysis(width, height, pixels),
        new GroundTheoryTuning(128, 30, 6, 120));
    var upper = lines.Single(line => line.Row == 10);
    Equal(new PixelSpan(0, 199), upper.XSpan);
    Equal(2, upper.EvidenceSpans.Count);
    Equal(new PixelSpan(0, 39), upper.EvidenceSpans[0]);
    Equal(new PixelSpan(160, 199), upper.EvidenceSpans[1]);
    Equal(new PixelSpan(40, 159), lines.Single(line => line.Row == 14).XSpan);
}

static void TestPersistentUpperEndsDoNotMergeAcrossKnownLowerFloor()
{
    const int width = 320;
    const int height = 140;
    var luma = new byte[width * height];
    for (var index = 0; index < luma.Length; index++)
    {
        var x = index % width;
        var y = index / width;
        var hash = (ulong)x * 73_856_093UL ^ (ulong)y * 19_349_663UL;
        hash ^= hash >> 13;
        hash = unchecked(hash * 1_274_126_177UL);
        luma[index] = unchecked((byte)(hash >> 11));
    }

    var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
    DetectedFloorLine[] lines =
    [
        new(40, new PixelSpan(0, 111)),
        new(40, new PixelSpan(208, 319)),
        new(85, new PixelSpan(128, 191)),
    ];
    var result = GroundHypothesisTrackingResult.Empty;
    for (var index = 0; index <= 8; index++)
    {
        result = tracker.Update(
            luma,
            width,
            height,
            lines,
            lineSeparation: 36,
            occlusionMergeGap: 180,
            timestamp: index / 60d);
    }

    Equal(3, result.GroundSegmentCount);
    var originalIds = result.AtlasLines.Select(line => line.SegmentId).ToHashSet();
    for (var index = 9; index <= 11; index++)
    {
        var bridged = new DetectedFloorLine(
            40,
            new PixelSpan(0, 319),
            [new PixelSpan(0, 111), new PixelSpan(208, 319)]);
        result = tracker.Update(
            luma,
            width,
            height,
            [bridged],
            lineSeparation: 36,
            occlusionMergeGap: 180,
            timestamp: index / 60d);
    }

    Equal(3, result.GroundSegmentCount);
    True(result.AtlasLines.Select(line => line.SegmentId).ToHashSet()
        .SetEquals(originalIds));
    Equal(2, result.AtlasLines.Count(line => Math.Abs(line.AtlasStart.Y - 100) < 1));
}

static void TestSemanticGroundSurface()
{
    const int width = 200;
    const int height = 80;
    var pixels = new byte[width * height];
    for (var x = 10; x <= 189; x++) pixels[20 * width + x] = 180;
    for (var x = 60; x <= 139; x++) pixels[40 * width + x] = 150;
    var luma = Enumerable.Repeat((byte)120, width * height).ToArray();
    for (var row = 41; row <= 55; row++)
    {
        for (var x = 60; x <= 139; x++) luma[row * width + x] = 20;
    }
    var lines = GroundLineDetector.SemanticFloorLines(
        new GroundComparisonAnalysis(width, height, pixels, luma),
        GroundTheoryTuning.SemanticDefault);
    Equal(1, lines.Count);
    Equal(40, lines[0].Row);
    Equal(new PixelSpan(60, 139), lines[0].XSpan);
}

static void TestSemanticGroundStrongCore()
{
    const int width = 200;
    const int height = 100;
    var pixels = new byte[width * height];
    for (var x = 20; x <= 119; x++) pixels[20 * width + x] = 8;
    for (var x = 45; x <= 94; x++) pixels[20 * width + x] = 9;
    for (var x = 20; x <= 119; x++) pixels[70 * width + x] = 8;
    var lines = GroundLineDetector.SemanticFloorLines(
        new GroundComparisonAnalysis(
            width,
            height,
            pixels,
            Enumerable.Repeat((byte)20, width * height).ToArray()),
        GroundTheoryTuning.SemanticDefault);
    Equal(1, lines.Count);
    Equal(20, lines[0].Row);
    Equal(new PixelSpan(20, 119), lines[0].XSpan);
}

static void TestGroundForegroundRejection()
{
    var shortObject = new DetectedFloorLine(30, new PixelSpan(40, 79));
    var longFloor = new DetectedFloorLine(30, new PixelSpan(0, 199));
    var filtered = GroundLineDetector.RejectKnownForegroundLines(
        [shortObject, longFloor],
        100,
        [new PixelRect(38, 20, 44, 40)]);
    Equal(1, filtered.Count);
    Equal(longFloor, filtered[0]);

    var knightLine = new DetectedFloorLine(
        35,
        new PixelSpan(20, 180),
        [new PixelSpan(85, 115)]);
    filtered = GroundLineDetector.RejectKnownForegroundLines(
        [knightLine, longFloor],
        100,
        [new PixelRect(85, 25, 30, 40)],
        new PixelRect(85, 25, 30, 40));
    True(!filtered.Contains(knightLine));
}

static void TestGroundAnalysisMask()
{
    var frame = SolidFrame(100, 80, 200, 200, 200);
    var analysis = GroundLineDetector.Analyze(frame, [new PixelRect(40, 30, 10, 10)]);
    Equal((byte)0, analysis.ValidSourcePixels![18 * 100 + 20]);
    Equal((byte)0, analysis.ValidSourcePixels[51 * 100 + 73]);
    Equal((byte)1, analysis.ValidSourcePixels[10 * 100 + 10]);
    Equal((byte)1, analysis.ValidSourcePixels[60 * 100 + 80]);
}

static void TestGroundCapturePadding()
{
    const int width = 100;
    const int height = 80;
    var edges = new byte[width * height];
    for (var x = 0; x < width; x++)
    {
        for (var row = 30; row <= 33; row++) edges[row * width + x] = 30;
        edges[71 * width + x] = 255;
    }
    var values = GroundLineDetector.GroundDetectPixels(
        new GroundEdgeAnalysis(width, height, edges, ValidRows: new Range(0, 72)));
    True(values[30 * width + 50] > 0);
    True(values[(68 * width)..].All(value => value == 0));
}

static void WithTemporaryDirectory(Action<string> run)
{
    var path = Path.Combine(Path.GetTempPath(), $"hkv-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    try
    {
        run(path);
    }
    finally
    {
        // Antivirus and offloaded-file providers can retain a just-closed lock
        // file for a few milliseconds. Cleanup is not the behavior under test,
        // so retry that transient Windows sharing violation within a tight bound.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}

static void TestShortFrameStorage()
{
    var threw = false;
    try
    {
        _ = new BgraFrame(2, 2, 8, new byte[15], 1, DateTimeOffset.UnixEpoch);
    }
    catch (ArgumentException)
    {
        threw = true;
    }
    True(threw);
}

static void TestBmpHeader()
{
    var path = Path.Combine(Path.GetTempPath(), $"hkv-{Guid.NewGuid():N}.bmp");
    try
    {
        BmpWriter.Write(Frame(7), path);
        var data = File.ReadAllBytes(path);
        Equal((byte)'B', data[0]);
        Equal((byte)'M', data[1]);
        Equal(2, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4)));
        Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4)));
    }
    finally
    {
        File.Delete(path);
    }
}

static void TestReceiverHello()
{
    var session = Guid.Parse("193f132f-ae84-423b-ac82-1bcd65dc1fe1");
    var line = ReceiverWireCodec.EncodeLine(ReceiverCapabilityRequest.Create(session));
    True(line.EndsWith('\n'));
    Equal("hello", ReceiverWireCodec.MessageType(line));
    using var document = JsonDocument.Parse(line);
    Equal(2, document.RootElement.GetProperty("version").GetInt32());
    Equal(session, document.RootElement.GetProperty("sessionID").GetGuid());
    True(!document.RootElement.TryGetProperty("renderFrameMarker", out _));
}

static void TestReceiverState()
{
    var session = Guid.Parse("96fce736-b15e-4627-97ec-aa5f1fbdb14e");
    var buttons = InputButtons.Up | InputButtons.ActionZ
        | InputButtons.Inventory | InputButtons.PauseMenu;
    var state = ReceiverInputSnapshot.Create(session, 9, true, buttons);
    Equal((InputButtons)424, state.HeldButtons);
    var line = ReceiverWireCodec.EncodeLine(state);
    var decoded = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(line);
    Equal(state, decoded);

    var disabled = ReceiverInputSnapshot.Create(session, 10, false, InputButtons.Left);
    Equal(InputButtons.None, disabled.HeldButtons);
}

static void TestGroundTruthCameraScale()
{
    const string line = "{\"version\":2,\"type\":\"groundTruth\",\"sessionID\":\"193f132f-ae84-423b-ac82-1bcd65dc1fe1\",\"sequence\":7,\"unityFrame\":99,\"unityRealtime\":12.5,\"sceneName\":\"Tutorial_01\",\"heroAvailable\":true,\"heroX\":36.25,\"heroY\":11.5,\"heroZ\":0.004,\"velocityX\":-2,\"velocityY\":0,\"facingRight\":false,\"grounded\":true,\"cameraAvailable\":true,\"cameraX\":37.25,\"cameraY\":14.1,\"cameraZ\":-38.1,\"cameraTargetX\":36.25,\"cameraTargetY\":14.1,\"cameraTargetZ\":0.004,\"orthographicSize\":4.8,\"pixelsPerWorldUnitX\":200,\"pixelsPerWorldUnitY\":200,\"heroScreenX\":1000,\"heroScreenY\":420,\"projectionPixelWidth\":1920,\"projectionPixelHeight\":1080,\"screenWidth\":1920,\"screenHeight\":1080}";
    var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
    True(sample.HasFiniteCoordinates());
    Close(66.6666667, sample.PixelsPerWorldUnit(360)!.Value);
}

static GroundTruthCameraTransform GroundTruthTransform(AtlasPoint camera) => new(
    "Tutorial_01",
    camera,
    PixelsPerWorldUnitX: 10,
    PixelsPerWorldUnitY: 10,
    new WorldSize(640, 360));

static void TestWorldLineReprojectsWithCameraMotion()
{
    var first = GroundTruthTransform(new AtlasPoint(100, 20));
    var moved = GroundTruthTransform(new AtlasPoint(105, 20));
    var firstBounds = new GroundTruthRect(1_000, 2_000, 640, 360);
    var movedBounds = new GroundTruthRect(1_050, 2_000, 640, 360);
    var world = new AtlasPoint(110, 22);

    ClosePoint(first.CameraWorld, first.ScreenToWorld(new AtlasPoint(320, 180)));
    CloseWithin(world.X, first.AtlasToWorld(
        first.WorldToAtlas(world, firstBounds), firstBounds).X, .000_001);
    var firstProjected = first.WorldToAtlas(world, firstBounds);
    var movedProjected = moved.WorldToAtlas(world, movedBounds);
    CloseWithin(firstProjected.X, movedProjected.X, .000_001);
    CloseWithin(firstProjected.Y, movedProjected.Y, .000_001);
}

static void TestGroundTruthLineStoreRoundTripsWorldLines()
{
    WithTemporaryDirectory(root =>
    {
        var store = new GroundTruthLineStore(Path.Combine(root, "world-lines.json"));
        var line = new GroundTruthWorldLine(
            "Tutorial_01",
            1,
            12,
            3,
            GroundTruthLineKind.Negative,
            seededFromDetector: true);
        var document = new GroundTruthLineDocument
        {
            SeededScenes = new HashSet<string>(["Tutorial_01"], StringComparer.Ordinal),
            Lines = [line]
        };

        store.Save(document);

        Equal(document, store.Load());
    });
}

static void TestGroundTruthAddNegativeModifyDeleteAndUndoKeepHorizontalWorldLines()
{
    WithTemporaryDirectory(root =>
    {
        var controller = new GroundTruthLabelController(
            new GroundTruthLineStore(Path.Combine(root, "world-lines.json")));
        var transform = GroundTruthTransform(new AtlasPoint(0, 0));
        var bounds = new GroundTruthRect(100, 200, 640, 360);
        var start = transform.WorldToAtlas(new AtlasPoint(1, 2), bounds);
        var end = transform.WorldToAtlas(new AtlasPoint(6, 9), bounds);

        GroundTruthDrag(controller, start, end, transform, bounds);
        Equal(1, controller.Document.Lines.Count);
        CloseWithin(1, controller.Document.Lines[0].MinimumWorldX, .000_001);
        CloseWithin(6, controller.Document.Lines[0].MaximumWorldX, .000_001);
        CloseWithin(2, controller.Document.Lines[0].WorldY, .000_001);

        controller.Mode = GroundTruthEditingMode.Negative;
        GroundTruthClick(controller,
            transform.WorldToAtlas(new AtlasPoint(3, 2), bounds),
            transform, bounds);
        Equal(1, controller.Document.Lines.Count);
        Equal(GroundTruthLineKind.Negative, controller.Document.Lines[0].Kind);

        controller.Mode = GroundTruthEditingMode.Modify;
        GroundTruthDrag(controller,
            transform.WorldToAtlas(new AtlasPoint(3, 2), bounds),
            transform.WorldToAtlas(new AtlasPoint(4, 3), bounds),
            transform, bounds);
        CloseWithin(2, controller.Document.Lines[0].MinimumWorldX, .000_001);
        CloseWithin(7, controller.Document.Lines[0].MaximumWorldX, .000_001);
        CloseWithin(3, controller.Document.Lines[0].WorldY, .000_001);

        controller.Mode = GroundTruthEditingMode.Delete;
        GroundTruthClick(controller,
            transform.WorldToAtlas(new AtlasPoint(4, 3), bounds),
            transform, bounds);
        True(controller.Document.Lines.Count == 0);
        controller.Undo();
        Equal(1, controller.Document.Lines.Count);
        CloseWithin(3, controller.Document.Lines[0].WorldY, .000_001);
    });
}

static void TestGroundTruthOverlayUsesPositiveNegativeAndSelectedKinds()
{
    var positive = new GroundTruthWorldLine(
        "Tutorial_01", 1, 3, 2,
        GroundTruthLineKind.Positive,
        seededFromDetector: false);
    var negative = new GroundTruthWorldLine(
        "Tutorial_01", 4, 6, 2,
        GroundTruthLineKind.Negative,
        seededFromDetector: false);
    var document = new GroundTruthLineDocument { Lines = [positive, negative] };

    var overlay = GroundTruthLineProjection.OverlayLines(
        document,
        GroundTruthTransform(new AtlasPoint(0, 0)),
        new GroundTruthRect(100, 200, 640, 360),
        negative.Id,
        draft: null);

    True(overlay.Select(line => line.Kind).SequenceEqual(
    [
        GroundTruthOverlayLineKind.PositiveGroundTruth,
        GroundTruthOverlayLineKind.SelectedGroundTruth
    ]));
    True(overlay.All(line => line.Start.Y == line.End.Y));
}

static void TestGroundTruthEnteringSceneSeedsOnlyOnce()
{
    WithTemporaryDirectory(root =>
    {
        var controller = new GroundTruthLabelController(
            new GroundTruthLineStore(Path.Combine(root, "world-lines.json")));
        var transform = GroundTruthTransform(new AtlasPoint(0, 0));
        var bounds = new GroundTruthRect(100, 200, 640, 360);
        GroundLinePresenceReview[] reviews =
        [
            new(
                1,
                new CleanFloorLine(240, new PixelSpan(80, 500)),
                GroundLinePresenceState.Confirmed,
                VisibleSeconds: 3,
                DetectedFraction: 1)
        ];

        controller.Enter([], reviews, transform, bounds);
        controller.Enter([], reviews, transform, bounds);

        True(controller.Document.SeededScenes.SetEquals(["Tutorial_01"]));
        Equal(1, controller.Document.Lines.Count);
        True(controller.Document.Lines[0].SeededFromDetector);
    });
}

static void TestGroundTruthStoreRejectsMalformedAndMissingDocumentFields()
{
    WithTemporaryDirectory(root =>
    {
        var path = Path.Combine(root, "world-lines.json");
        string[] invalidPayloads =
        [
            "not-json",
            "{\"schemaVersion\":2",
            "{}",
            "{\"seededScenes\":[],\"lines\":[]}",
            "{\"schemaVersion\":2,\"lines\":[]}",
            "{\"schemaVersion\":2,\"seededScenes\":[]}",
            "{\"schemaVersion\":2,\"seededScenes\":null,\"lines\":[]}",
            "{\"schemaVersion\":2,\"seededScenes\":[],\"lines\":null}",
            "{\"schemaVersion\":2,\"seededScenes\":[],\"lines\":[null]}"
        ];

        foreach (var payload in invalidPayloads)
        {
            File.WriteAllText(path, payload);
            ExpectGroundTruthStoreFailure(() => new GroundTruthLineStore(path).Load());
        }
    });
}

static void TestGroundTruthStoreRejectsMissingAndInvalidLineFields()
{
    WithTemporaryDirectory(root =>
    {
        var path = Path.Combine(root, "world-lines.json");
        string[] requiredFields =
        [
            "id",
            "sceneName",
            "minimumWorldX",
            "maximumWorldX",
            "worldY",
            "kind",
            "seededFromDetector"
        ];
        foreach (var omitted in requiredFields)
        {
            var line = GroundTruthJsonLine();
            line.Remove(omitted);
            File.WriteAllText(path, GroundTruthJsonDocument(line));
            ExpectGroundTruthStoreFailure(() => new GroundTruthLineStore(path).Load());
        }

        foreach (var invalid in new Dictionary<string, object?>[]
                 {
                     GroundTruthJsonLine(("id", Guid.Empty)),
                     GroundTruthJsonLine(("sceneName", null)),
                     GroundTruthJsonLine(("sceneName", "")),
                     GroundTruthJsonLine(("minimumWorldX", null)),
                     GroundTruthJsonLine(("maximumWorldX", null)),
                     GroundTruthJsonLine(("worldY", null)),
                     GroundTruthJsonLine(("kind", null)),
                     GroundTruthJsonLine(("kind", "unsupported")),
                     GroundTruthJsonLine(("seededFromDetector", null))
                 })
        {
            File.WriteAllText(path, GroundTruthJsonDocument(invalid));
            ExpectGroundTruthStoreFailure(() => new GroundTruthLineStore(path).Load());
        }

        var nonFinite = GroundTruthJsonDocument(GroundTruthJsonLine());
        File.WriteAllText(path, nonFinite.Replace(
            "\"minimumWorldX\":1",
            "\"minimumWorldX\":1e999",
            StringComparison.Ordinal));
        ExpectGroundTruthStoreFailure(() => new GroundTruthLineStore(path).Load());

        var store = new GroundTruthLineStore(path);
        foreach (var invalidLine in new[]
                 {
                     new GroundTruthWorldLine(
                         Guid.Empty, "Tutorial_01", 1, 12, 3,
                         GroundTruthLineKind.Positive, false),
                     new GroundTruthWorldLine(
                         Guid.NewGuid(), "Tutorial_01", double.NaN, 12, 3,
                         GroundTruthLineKind.Positive, false),
                     new GroundTruthWorldLine(
                         Guid.NewGuid(), "Tutorial_01", 1, 12, 3,
                         (GroundTruthLineKind)99, false)
                 })
        {
            var document = new GroundTruthLineDocument { Lines = [invalidLine] };
            ExpectGroundTruthStoreFailure(() => store.Save(document));
        }
    });
}

static void TestGroundTruthFailedAtomicReplacePreservesOldDocument()
{
    WithTemporaryDirectory(root =>
    {
        var path = Path.Combine(root, "world-lines.json");
        var store = new GroundTruthLineStore(path);
        var original = new GroundTruthLineDocument
        {
            SeededScenes = new HashSet<string>(["Tutorial_01"], StringComparer.Ordinal),
            Lines =
            [
                new GroundTruthWorldLine(
                    "Tutorial_01", 1, 12, 3,
                    GroundTruthLineKind.Positive,
                    seededFromDetector: false)
            ]
        };
        store.Save(original);
        var originalBytes = File.ReadAllBytes(path);
        var replacement = new GroundTruthLineDocument
        {
            Lines =
            [
                new GroundTruthWorldLine(
                    "Tutorial_01", 20, 30, 4,
                    GroundTruthLineKind.Negative,
                    seededFromDetector: true)
            ]
        };

        var failed = false;
        using (var blocker = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            try
            {
                store.Save(replacement);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failed = true;
            }
            True(failed);
            True(originalBytes.SequenceEqual(File.ReadAllBytes(path)));
        }

        Equal(original, store.Load());
        True(!Directory.EnumerateFiles(root, ".world-lines.json.tmp-*").Any());
    });
}

static void TestGroundTruthMinimumLengthForeignSceneAndPaletteSanity()
{
    WithTemporaryDirectory(root =>
    {
        var controller = new GroundTruthLabelController(
            new GroundTruthLineStore(Path.Combine(root, "world-lines.json")));
        var transform = GroundTruthTransform(new AtlasPoint(0, 0));
        var bounds = new GroundTruthRect(100, 200, 640, 360);
        var start = transform.WorldToAtlas(new AtlasPoint(1, 2), bounds);
        GroundTruthDrag(
            controller,
            start,
            start with { X = start.X + 3 },
            transform,
            bounds);
        Equal(0, controller.Document.Lines.Count);

        var positive = new GroundTruthWorldLine(
            "Tutorial_01", 1, 3, 2,
            GroundTruthLineKind.Positive, false);
        var negative = new GroundTruthWorldLine(
            "Tutorial_01", 4, 6, 2,
            GroundTruthLineKind.Negative, false);
        var foreign = new GroundTruthWorldLine(
            "Crossroads_01", 1, 6, 2,
            GroundTruthLineKind.Positive, false);
        var overlays = GroundTruthLineProjection.OverlayLines(
            new GroundTruthLineDocument { Lines = [positive, negative, foreign] },
            transform,
            bounds,
            selectedId: null,
            draft: null);

        Equal(2, overlays.Count);
        True(overlays.Select(line => line.Kind).SequenceEqual(
        [
            GroundTruthOverlayLineKind.PositiveGroundTruth,
            GroundTruthOverlayLineKind.NegativeGroundTruth
        ]));
    });
}

static Dictionary<string, object?> GroundTruthJsonLine(
    params (string Key, object? Value)[] replacements)
{
    var line = new Dictionary<string, object?>
    {
        ["id"] = Guid.Parse("3f689252-5236-4e89-960c-b7685ed39f10"),
        ["sceneName"] = "Tutorial_01",
        ["minimumWorldX"] = 1d,
        ["maximumWorldX"] = 12d,
        ["worldY"] = 3d,
        ["kind"] = "positive",
        ["seededFromDetector"] = false
    };
    foreach (var (key, value) in replacements) line[key] = value;
    return line;
}

static string GroundTruthJsonDocument(Dictionary<string, object?> line) =>
    JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["schemaVersion"] = GroundTruthLineDocument.CurrentSchemaVersion,
        ["seededScenes"] = Array.Empty<string>(),
        ["lines"] = new object?[] { line }
    });

static void ExpectGroundTruthStoreFailure(Action action)
{
    try
    {
        action();
    }
    catch (Exception error) when (error is JsonException or GroundTruthLineStoreException)
    {
        return;
    }
    throw new InvalidOperationException("Expected strict ground-truth store failure.");
}

static void GroundTruthDrag(
    GroundTruthLabelController controller,
    AtlasPoint start,
    AtlasPoint end,
    GroundTruthCameraTransform transform,
    GroundTruthRect bounds)
{
    controller.Handle(GroundTruthPointerPhase.Began, start, 0, transform, bounds);
    controller.Handle(GroundTruthPointerPhase.Changed, end, 0, transform, bounds);
    controller.Handle(GroundTruthPointerPhase.Ended, end, 0, transform, bounds);
}

static void GroundTruthClick(
    GroundTruthLabelController controller,
    AtlasPoint point,
    GroundTruthCameraTransform transform,
    GroundTruthRect bounds)
{
    controller.Handle(GroundTruthPointerPhase.Began, point, 0, transform, bounds);
    controller.Handle(GroundTruthPointerPhase.Ended, point, 0, transform, bounds);
}

static void TestReceiverRejectsMultiline()
{
    var threw = false;
    try
    {
        _ = ReceiverWireCodec.MessageType("{\"type\":\"hello\"}\n{\"type\":\"state\"}");
    }
    catch (InvalidDataException)
    {
        threw = true;
    }
    True(threw);
}

static void TestReceiverCheckpoint()
{
    var session = Guid.Parse("96fce736-b15e-4627-97ec-aa5f1fbdb14e");
    var commandId = Guid.Parse("9ac417b0-9b42-4817-89a9-0ac004d1255d");
    var command = ReceiverCheckpointCommand.Restore(session, Checkpoint(), commandId);
    var line = ReceiverWireCodec.EncodeLine(command);
    using var json = JsonDocument.Parse(line);
    Equal("restoreCheckpoint", json.RootElement.GetProperty("type").GetString()!);
    Equal(session, json.RootElement.GetProperty("sessionID").GetGuid());
    Equal(commandId, json.RootElement.GetProperty("commandID").GetGuid());
    Equal("Tutorial_01", json.RootElement.GetProperty("checkpoint")
        .GetProperty("sceneName").GetString()!);
}

static void TestRecordedInputPath()
{
    WithTemporaryDirectory(workspace =>
    {
        var value = new RecordedInputPath(
            RecordedInputPath.CurrentSchemaVersion,
            Guid.Parse("bc4eddf6-0152-45f7-88d1-247a1f2489d2"),
            new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero),
            0.5,
            Guid.Parse("de753820-f6bd-4a15-a3e3-f6bbb3dab1d7"),
            2,
            Checkpoint(),
            [
                new RecordedInputPathEvent(0.1, RecordedGameButton.Left,
                    RecordedInputTransition.Pressed),
                new RecordedInputPathEvent(0.2, RecordedGameButton.A,
                    RecordedInputTransition.Pressed),
                new RecordedInputPathEvent(0.3, RecordedGameButton.Left,
                    RecordedInputTransition.Released),
                new RecordedInputPathEvent(0.4, RecordedGameButton.A,
                    RecordedInputTransition.Released)
            ],
            new Dictionary<string, string> { ["capturePixelPath"] = "core-image" });
        var file = Path.Combine(workspace, "path-test.json");
        var store = new RecordedInputPathStore();
        store.Save(value, file);
        var raw = File.ReadAllText(file);
        True(raw.Contains("\"sourcePathID\"", StringComparison.Ordinal));
        True(raw.Contains("\"button\": \"left\"", StringComparison.Ordinal));
        True(raw.Contains("\"transition\": \"pressed\"", StringComparison.Ordinal));
        var loaded = store.Load(file);
        Equal(value.Id, loaded.Id);
        Equal(value.SourcePathId!.Value, loaded.SourcePathId!.Value);
        var changes = loaded.StateChanges();
        Equal(4, changes.Count);
        Equal(InputButtons.Left, changes[0].HeldButtons);
        Equal(InputButtons.Left | InputButtons.ActionA, changes[1].HeldButtons);
        Equal(InputButtons.ActionA, changes[2].HeldButtons);
        Equal(InputButtons.None, changes[3].HeldButtons);
    });
}

static RecordedGameCheckpoint Checkpoint() => new(
    "Tutorial_01",
    "left1",
    3,
    4,
    0,
    0,
    0,
    true,
    true,
    3,
    6,
    -38,
    3,
    6,
    0,
    [new RecordedEnemyCheckpoint("Crawler", 7, 2, 0, 10, true)],
    true,
    true,
    false,
    false,
    false,
    false,
    ["Tutorial_01"]);

static void TestStartupCoordinatorStableNavigation()
{
    var coordinator = new GameStartupCoordinator(2);
    var options = new GameStartupEvidence(GameStartupScreen.Title, false, "Options");
    True(coordinator.Observe(options).Action is null);
    Equal(GameStartupAction.MoveSelectionUp, coordinator.Observe(options).Action!.Value);
    var start = new GameStartupEvidence(GameStartupScreen.Title, false, "Start Game");
    for (var index = 0; index < 17; index++)
    {
        True(coordinator.Observe(start).Action is null);
    }
    Equal(GameStartupAction.SelectStartGame, coordinator.Observe(start).Action!.Value);

    var profile = new GameStartupEvidence(GameStartupScreen.ProfileOne, false, "1.");
    True(coordinator.Observe(profile).Action is null);
    Equal(GameStartupAction.SelectProfileOne, coordinator.Observe(profile).Action!.Value);
    Equal(GameStartupPhase.AwaitingGameplay, coordinator.Phase);
    var gameplay = new GameStartupEvidence(null, true);
    Equal(GameStartupPhase.AwaitingGameplay, coordinator.Observe(gameplay).Phase);
    Equal(GameStartupPhase.Gameplay, coordinator.Observe(gameplay).Phase);
}

static void TestStartupCoordinatorClosesGate()
{
    var coordinator = new GameStartupCoordinator(1);
    coordinator.RestoreGameplaySession();
    True(coordinator.Observe(GameStartupEvidence.Unknown).AdmitsWorldFrames);
    var menu = coordinator.Observe(
        new GameStartupEvidence(GameStartupScreen.Title, false, "Start Game"));
    Equal(GameStartupPhase.AwaitingProfile, menu.Phase);
    Equal(GameStartupAction.SelectStartGame, menu.Action!.Value);
    True(!menu.AdmitsWorldFrames);
}

static void TestStartupDetectorTitleSelection()
{
    var frame = BlankFrame();
    var evidence = new GameStartupDetector().Detect(
        frame,
        menuStencil: MatchedStartupMenuStencil("main-title", "Start Game"));
    Equal(GameStartupScreen.Title, evidence.Screen!.Value);
    Equal("Start Game", evidence.SelectedOption!);
}

static void TestStartupDetectorProfileSelection()
{
    var frame = BlankFrame();
    var evidence = new GameStartupDetector().Detect(
        frame,
        menuStencil: MatchedStartupMenuStencil("select-profile", "2."));
    Equal(GameStartupScreen.ProfileOne, evidence.Screen!.Value);
    Equal("2.", evidence.SelectedOption!);
}

static void TestStartupDetectorGameplayHud()
{
    var frame = BlankFrame();
    Paint(frame, 30, 15, 10, 10);
    Paint(frame, 80, 20, 10, 10);
    var evidence = new GameStartupDetector().Detect(frame);
    True(evidence.GameplayLikely);
    True(evidence.Screen is null);
}

static void TestDetectorUsesMenuStencil()
{
    var image = StartupDetectorFrame((0.46, 0.57, 0.088, 0.055));
    var evidence = new GameStartupDetector().Detect(
        image,
        menuStencil: MatchedStartupMenuStencil("main-title"));
    Equal(GameStartupScreen.Title, evidence.Screen!.Value);
    True(!evidence.GameplayLikely);
}

static void TestDetectorPreservesStencilSelectionForNavigation()
{
    var image = StartupDetectorFrame();
    var evidence = new GameStartupDetector().Detect(
        image,
        menuStencil: MatchedStartupMenuStencil("main-title", "Options"));
    Equal("Options", evidence.SelectedOption!);
}

static void TestPixelMenuLookalikeIsRejectedAndStencilReleasesGameplayLatch()
{
    var image = StartupDetectorFrame(
        (0.412, 0.57, 0.032, 0.055),
        (0.46, 0.57, 0.088, 0.055),
        (0.568, 0.57, 0.032, 0.055));
    var detector = new GameStartupDetector();
    True(detector.Detect(image).Screen is null);
    True(detector.Detect(image, gameplayIsLatched: true).Screen is null);
    Equal(
        GameStartupScreen.Title,
        detector.Detect(
            image,
            menuStencil: MatchedStartupMenuStencil("main-title"),
            gameplayIsLatched: true).Screen!.Value);
}

static void TestDetectorFindsSelectedProfileOne()
{
    var image = StartupDetectorFrame(
        (0.10, 0.285, 0.035, 0.055),
        (0.17, 0.285, 0.025, 0.055),
        (0.675, 0.285, 0.035, 0.055));
    var evidence = new GameStartupDetector().Detect(image);
    Equal(GameStartupScreen.ProfileOne, evidence.Screen!.Value);
}

static void TestDetectorUsesGenericMenuStencilForBothStartupScreens()
{
    var blank = StartupDetectorFrame();
    var detector = new GameStartupDetector();
    Equal(
        GameStartupScreen.Title,
        detector.Detect(
            blank,
            menuStencil: MatchedStartupMenuStencil("main-title"),
            gameplayIsLatched: true).Screen!.Value);
    Equal(
        GameStartupScreen.ProfileOne,
        detector.Detect(
            blank,
            menuStencil: MatchedStartupMenuStencil("select-profile"),
            gameplayIsLatched: true).Screen!.Value);
    True(detector.Detect(
        blank,
        menuStencil: MatchedStartupMenuStencil("options"),
        gameplayIsLatched: true).Screen is null);
}

static void TestVerifiedHealthReleasesStaleProfileStencilWithoutSoul()
{
    var blank = StartupDetectorFrame();
    var staleProfile = MatchedStartupMenuStencil("select-profile", "1.");
    var detector = new GameStartupDetector();

    var gameplay = detector.Detect(
        blank,
        menuStencil: staleProfile,
        hudStencil: MatchedStartupHudStencil(includesMana: true));
    True(gameplay.GameplayLikely);
    True(gameplay.Screen is null);

    var healthOnlyGameplay = detector.Detect(
        blank,
        menuStencil: staleProfile,
        hudStencil: MatchedStartupHudStencil(includesMana: false));
    True(healthOnlyGameplay.GameplayLikely);
    True(healthOnlyGameplay.Screen is null);
}

static MenuStencilResult MatchedStartupMenuStencil(
    string contextIdentifier,
    string? selectedOption = null) => new(
        contextIdentifier,
        IsMatch: true,
        Confidence: 0.9,
        selectedOption,
        Anchors: [],
        SelectorCandidates: [],
        SelectorSearchRegions: [],
        SelectorLanguageEvidenceCount: 0,
        SelectorForegroundEvidenceCount: 0,
        SceneEvidenceRatio: 1,
        Phase: MenuStencilPhase.Tracking,
        ComparisonCount: 1,
        SourceTimestamp: 1,
        LanguageIdentifier: null);

static HudStencilResult MatchedStartupHudStencil(bool includesMana) => new(
    Health:
    [
        new HudStencilMatch(0, "full", default, 0.9),
        new HudStencilMatch(1, "full", default, 0.9)
    ],
    ManaMain: includesMana
        ? new HudStencilMatch(0, "main", default, 0.9)
        : null,
    ManaReserves: [],
    HealthMasks: [],
    ManaMasks: [],
    Geo: default,
    HoldsHealthMask: false,
    SourceTimestamp: 1);

static BgraFrame StartupDetectorFrame(
    params (double X, double Y, double Width, double Height)[] brightTopOriginRects)
{
    const int width = 640;
    const int height = 360;
    var frame = SolidFrame(width, height, 0, 0, 0);
    foreach (var rect in brightTopOriginRects)
    {
        var x0 = Math.Max(0, (int)Math.Floor(rect.X * width));
        var x1 = Math.Min(width, (int)Math.Ceiling((rect.X + rect.Width) * width));
        var y0 = Math.Max(0, (int)Math.Floor(rect.Y * height));
        var y1 = Math.Min(height, (int)Math.Ceiling((rect.Y + rect.Height) * height));
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var offset = y * frame.Stride + x * BgraFrame.BytesPerPixel;
                frame.Pixels[offset] = 245;
                frame.Pixels[offset + 1] = 245;
                frame.Pixels[offset + 2] = 245;
            }
        }
    }
    return frame;
}

static void TestDetectionDecoderLocalMaximum()
{
    var scores = new float[2 * 3 * 3];
    scores[4] = 0.9f;
    scores[5] = 0.8f;
    scores[9 + 0] = 0.7f;
    var boxes = Enumerable.Repeat(0.1f, 4 * 3 * 3).ToArray();
    var detections = DetectionDecoder.Decode(
        scores, [1, 2, 3, 3], boxes, [1, 4, 3, 3], ["knight", "enemy"]);
    Equal(2, detections.Count);
    Equal("knight", detections[0].ClassIdentifier);
    Close(0.9, detections[0].Confidence);
    Equal("enemy", detections[1].ClassIdentifier);
}

static void TestDetectionDecoderClasswiseNms()
{
    var scores = new float[2 * 1 * 3];
    scores[0] = 0.9f;
    scores[2] = 0.8f;
    scores[3] = 0.85f;
    var boxes = new float[4 * 1 * 3];
    for (var index = 0; index < 3; index++)
    {
        boxes[index] = 0.1f;
        boxes[3 + index] = 0.1f;
        boxes[6 + index] = 0.5f;
        boxes[9 + index] = 0.5f;
    }
    var detections = DetectionDecoder.Decode(
        scores, [1, 2, 1, 3], boxes, [1, 4, 1, 3], ["knight", "enemy"]);
    Equal(2, detections.Count);
    Equal("knight", detections[0].ClassIdentifier);
    Equal("enemy", detections[1].ClassIdentifier);
}

static void TestDetectionDecoderRaw()
{
    var scores = new[] { 0.9f, 0.8f, 0.7f };
    var boxes = new float[12];
    for (var index = 0; index < 3; index++)
    {
        boxes[index] = 0.1f;
        boxes[3 + index] = 0.1f;
        boxes[6 + index] = 0.5f;
        boxes[9 + index] = 0.5f;
    }
    var raw = DetectionDecoder.DecodeRaw(
        scores, [1, 1, 1, 3], boxes, [1, 4, 1, 3], ["knight"]);
    Equal(3, raw.Count);
    Equal(0.9f, raw[0].Confidence);
}

static void TestGroundTruthAtlasProjection()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 64, 0);
    var frame = SolidFrame(640, 360, blue: 7, green: 19, red: 211);
    True(mosaic.AddFrame(frame, AtlasTelemetry(cameraX: 0, cameraY: 0)));
    True(mosaic.TileCount > 1);
    True(mosaic.TryReadPixel(-1, -1, out var blue, out var green, out var red, out var observations));
    Equal((byte)7, blue);
    Equal((byte)19, green);
    Equal((byte)211, red);
    Equal((ushort)1, observations);
    True(mosaic.Snapshot().Any(tile => tile.Coordinate.X < 0 && tile.Coordinate.Y < 0));
}

static void TestGroundTruthAtlasBlend()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 128, 0);
    var initialRevision = mosaic.Revision;
    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 0, green: 0, red: 200),
        AtlasTelemetry(cameraX: 0, cameraY: 0),
        captureIdentity: "red-1"));
    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 200, green: 0, red: 0),
        AtlasTelemetry(cameraX: 0, cameraY: 0),
        captureIdentity: "blue-1"));
    True(mosaic.TryReadPixel(0, 0, out var blue, out var green, out var red, out var observations));
    Equal((byte)200, blue);
    Equal((byte)0, green);
    Equal((byte)0, red);
    Equal((ushort)2, observations);
    Equal(initialRevision + 2, mosaic.Revision);

    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 200, green: 0, red: 0),
        AtlasTelemetry(cameraX: 0, cameraY: 0),
        captureIdentity: "blue-2"));
    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 200, green: 0, red: 0),
        AtlasTelemetry(cameraX: 0, cameraY: 0),
        captureIdentity: "blue-3"));
    True(mosaic.TryReadPixel(0, 0, out blue, out green, out red, out observations));
    Equal((byte)200, blue);
    Equal((byte)0, green);
    Equal((byte)0, red);
    Equal((ushort)4, observations);
    Equal(4L, mosaic.AcceptedFrameCount);
    Equal(initialRevision + 4, mosaic.Revision);
}

static void TestGroundTruthAtlasDirtyReset()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 128, 0);
    True(mosaic.AddFrame(
        SolidFrame(640, 360, blue: 10, green: 20, red: 30),
        AtlasTelemetry(0, 0)));
    var first = mosaic.SnapshotSince(-1);
    True(first.Tiles.Count > 0);
    Equal(0, mosaic.SnapshotSince(first.Revision).Tiles.Count);
    mosaic.Reset();
    Equal(0, mosaic.TileCount);
    Equal(0L, mosaic.AcceptedFrameCount);
    True(mosaic.Revision > first.Revision);
}

static void TestGroundTruthAtlasImport()
{
    var mosaic = new GroundTruthAtlasMosaic(64, 32, 0);
    mosaic.ImportTile(
        new AtlasTileCoordinate(-2, 3),
        SolidFrame(32, 32, blue: 9, green: 8, red: 7));
    Equal(1, mosaic.TileCount);
    True(mosaic.TryReadPixel(-64, 96, out var blue, out var green, out var red, out var count));
    Equal((byte)9, blue);
    Equal((byte)8, green);
    Equal((byte)7, red);
    Equal((ushort)1, count);
    mosaic.RestoreAcceptedFrameCount(912);
    Equal(912L, mosaic.AcceptedFrameCount);
}

static void TestReceiverPlayerOps()
{
    var session = Guid.Parse("b5ca0c11-2e8e-4132-b119-a7c091150780");
    var state = new PlayerTestState(50, -1, 20, 500, 7, -5, true).Normalized();
    Equal(9, state.MaxHealth);
    Equal(1, state.Health);
    Equal(9, state.LifebloodSeed);
    Equal(198, state.Mana);
    Equal(3, state.ExtraManaSlots);
    Equal(0, state.Geo);
    var line = ReceiverWireCodec.EncodeLine(ReceiverPlayerOpsCommand.Apply(session, state));
    Equal("playerOps", ReceiverWireCodec.MessageType(line));
    var decoded = ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsCommand>(line);
    Equal(session, decoded.SessionId);
    Equal("apply", decoded.Operation);
    True(decoded.State == state);
}

static BgraFrame SolidFrame(int width, int height, byte blue, byte green, byte red)
{
    var pixels = new byte[width * height * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
        pixels[index + 3] = 255;
    }
    return new BgraFrame(width, height, width * 4, pixels, 1, DateTimeOffset.UnixEpoch);
}

static ReceiverGroundTruthSample AtlasTelemetry(double cameraX, double cameraY) => new(
    2,
    "groundTruth",
    Guid.Parse("193f132f-ae84-423b-ac82-1bcd65dc1fe1"),
    1,
    1,
    1,
    "Tutorial_01",
    true,
    0,
    0,
    0,
    0,
    0,
    true,
    true,
    true,
    cameraX,
    cameraY,
    -38,
    cameraX,
    cameraY,
    0,
    2.8125,
    64,
    64,
    320,
    180,
    640,
    360,
    640,
    360);

static BgraFrame BlankFrame() => new(
    640, 360, 640 * 4, new byte[640 * 360 * 4], 1, DateTimeOffset.UnixEpoch);

static void Paint(BgraFrame frame, int x, int y, int width, int height)
{
    for (var row = y; row < y + height; row++)
    {
        for (var column = x; column < x + width; column++)
        {
            var offset = row * frame.Stride + column * 4;
            frame.Pixels[offset] = 220;
            frame.Pixels[offset + 1] = 220;
            frame.Pixels[offset + 2] = 220;
            frame.Pixels[offset + 3] = 255;
        }
    }
}

static async Task TestReceiverClientLoopback()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(1);
    try
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var session = Guid.Parse("993813d8-9bfd-47b0-a30e-083b99392966");
        var accept = listener.AcceptTcpClientAsync();
        await using var receiver = new ReceiverClient(session, port: port);
        await receiver.ConnectAsync();
        using var server = await accept;
        using var reader = new StreamReader(
            server.GetStream(), new UTF8Encoding(false), false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(
            server.GetStream(), new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true
        };

        var helloLine = await reader.ReadLineAsync()
            ?? throw new InvalidOperationException("Client did not send hello.");
        var hello = ReceiverWireCodec.DecodeLine<ReceiverCapabilityRequest>(helloLine);
        Equal(session, hello.SessionId);

        var acknowledgement = new ReceiverCapabilitiesAcknowledgement(
            2, "capabilitiesAck", session,
            ["input-state-v1", ReceiverProtocol.GroundTruthCapability], 2000);
        await writer.WriteAsync(ReceiverWireCodec.EncodeLine(acknowledgement));
        var receivedLine = await receiver.ReceiveLineAsync()
            ?? throw new InvalidOperationException("Client did not receive acknowledgement.");
        Equal("capabilitiesAck", ReceiverWireCodec.MessageType(receivedLine));

        await receiver.SendStateAsync(true, InputButtons.Left | InputButtons.ActionA);
        var stateLine = await reader.ReadLineAsync()
            ?? throw new InvalidOperationException("Client did not send input state.");
        var state = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(stateLine);
        Equal(0UL, state.Sequence);
        Equal(InputButtons.Left | InputButtons.ActionA, state.HeldButtons);

        await receiver.PulseAsync(InputButtons.Up, TimeSpan.FromMilliseconds(1));
        var pulsePress = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(
            await reader.ReadLineAsync() ?? throw new InvalidOperationException("No pulse press."));
        var pulseRelease = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(
            await reader.ReadLineAsync() ?? throw new InvalidOperationException("No pulse release."));
        Equal(1UL, pulsePress.Sequence);
        Equal(InputButtons.Up, pulsePress.HeldButtons);
        Equal(2UL, pulseRelease.Sequence);
        Equal(InputButtons.None, pulseRelease.HeldButtons);

        await receiver.SendPointerAsync("leftDown", 0.25, 0.75, 2);
        var pointerLine = await reader.ReadLineAsync()
            ?? throw new InvalidOperationException("No pointer command.");
        var pointer = ReceiverWireCodec.DecodeLine<ReceiverPointerCommand>(pointerLine);
        Equal(0UL, pointer.Sequence);
        Equal("leftDown", pointer.Kind);
        Equal(0.25, pointer.NormalizedX);
        Equal(0.75, pointer.NormalizedY);
        Equal(2, pointer.ClickCount);
    }
    finally
    {
        listener.Stop();
    }
}

static void TestBundledOnnxModel()
{
    DirectoryInfo? cursor = new(AppContext.BaseDirectory);
    string? run = null;
    while (cursor is not null)
    {
        var candidate = System.IO.Path.Combine(cursor.FullName, "models", "shared-object-model");
        if (File.Exists(System.IO.Path.Combine(candidate, "Detector.onnx")))
        {
            run = candidate;
            break;
        }
        cursor = cursor.Parent;
    }
    if (run is null) throw new FileNotFoundException("Bundled Detector.onnx was not found.");
    using var detector = new OnnxObjectDetector(run);
    Equal(7, detector.ClassIdentifiers.Count);
    True(detector.ClassIdentifiers.Contains("game.playable-knight", StringComparer.Ordinal));
    var pixels = new byte[640 * 360 * BgraFrame.BytesPerPixel];
    for (var index = 3; index < pixels.Length; index += BgraFrame.BytesPerPixel) pixels[index] = 255;
    var detections = detector.Detect(new BgraFrame(
        640, 360, 640 * BgraFrame.BytesPerPixel, pixels, 1, DateTimeOffset.UnixEpoch));
    True(detections.All(item => detector.ClassIdentifiers.Contains(
        item.ClassIdentifier, StringComparer.Ordinal)));
}

static void TestUserObjectCatalog()
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hkv-catalog-{Guid.NewGuid():N}");
    try
    {
        var path = System.IO.Path.Combine(root, "object-catalog-v1.json");
        var catalog = new UserObjectCatalog(path);
        var first = catalog.Add("Moss Charger", "Enemies");
        Equal("enemies.moss-charger", first.Identifier);
        Equal(first, catalog.Add("Moss Charger", "enemy"));
        var second = catalog.Add("Breakable Wall", "world");
        Equal("world.breakable-wall", second.Identifier);
        var loaded = new UserObjectCatalog(path).Load();
        Equal(2, loaded.Count);
        True(File.ReadAllText(path).Contains("\"schemaVersion\": 1", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestPipelineOrdersTransactionAndUsesMonotonicTimestamp()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);
        var result = pipeline.Process(
            PipelineTextureFrame(1, 17),
            wallTimestamp: 1_700_000_000,
            monotonicTimestamp: 12.5,
            captureGeneration: 1,
            roomName: "Tutorial_01");

        Equal("Committed", result.State);
        True(stages.SequenceEqual(["append", "ingest", "commit", "publish"]));
        var snapshot = new LiveWorldSessionStore(root).Load().Snapshot;
        Equal(result.Revision, snapshot.MapRevision);
        Equal(1, snapshot.Observations.Count);
        CloseWithin(12.5, snapshot.Observations[0].Timestamp, 0.000_001);
    });
}

static void TestPipelineSuppressesUnverifiedNewWorldAndLossWithoutReference()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);
        var unverified = pipeline.Process(
            PipelineSolidFrame(1), 100, 1, 1, "Tutorial_01",
            stableTrackingVerified: false);
        Equal("PendingRecovery", unverified.State);
        Equal(0, stages.Count);

        var initial = pipeline.Process(
            PipelineSolidFrame(2), 101, 1.2, 1, "Tutorial_01");
        Equal("Committed", initial.State);
        Equal(1, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);

        pipeline.NotifyTrackingLoss(1);
        var lost = pipeline.Process(
            PipelineSolidFrame(3), 102, 1.5, 1, "Tutorial_01");
        Equal("PendingRecovery", lost.State);
        True(lost.Diagnostic.Contains("new-world boundary", StringComparison.Ordinal));
        Equal(1, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);

        var roomUnverified = pipeline.Process(
            PipelineSolidFrame(4), 103, 1.8, 1, "Crossroads_01",
            stableTrackingVerified: false);
        Equal("PendingRecovery", roomUnverified.State);
        var roomVerified = pipeline.Process(
            PipelineSolidFrame(5), 104, 2.1, 1, "Crossroads_01");
        Equal("Committed", roomVerified.State);
        Equal(2, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);
    });
}

static void TestGroundAnchoredObservationKeepsCapturePoseAcrossLaterBasisRevision()
{
    WithTemporaryDirectory(root =>
    {
        var pixels = PipelineTexturePixels(71);
        var pipeline = new LiveWorldPipelineReflection(root, _ => { });
        var initial = pipeline.Process(
            PipelineTextureFrameFromPixels(1, pixels), 100, 1, 1, "Tutorial_01",
            localPose: new AtlasPoint(20, 10));
        Equal("Committed", initial.State);

        pipeline.NotifyTrackingLoss(1);
        Equal("PendingRecovery", pipeline.Process(
            PipelineTextureFrameFromPixels(2, pixels), 101, 1.3, 1, "Tutorial_01",
            localPose: new AtlasPoint(80, 10)).State);
        var recovered = pipeline.Process(
            PipelineTextureFrameFromPixels(3, pixels), 102, 1.6, 1, "Tutorial_01",
            localPose: new AtlasPoint(80, 10));
        True(recovered.ConfirmedRecoveryPose is not null);

        var later = pipeline.Process(
            PipelineTextureFrameFromPixels(4, pixels), 103, 1.9, 1, "Tutorial_01",
            localPose: new AtlasPoint(90, 10));
        Equal("Committed", later.State);
        var observations = new LiveWorldSessionStore(root).Load().Snapshot.Observations;
        Equal(2, observations.Count);
        Equal(new AtlasPoint(90, 10), observations[^1].RawPose);
        Equal(new AtlasPoint(90, 10), observations[^1].LocalPose);
        True(observations[^1].BasisRevision > observations[0].BasisRevision);
    });
}

static void TestAtlasWorldTrackerReceivesGroundAlignmentExclusions()
{
    WithTemporaryDirectory(root =>
    {
        var controlRoot = Path.Combine(root, "control");
        var splitRoot = Path.Combine(root, "split");
        var frame = PipelineTextureFrame(1, 71);
        var control = new LiveWorldPipelineReflection(controlRoot, _ => { });
        Equal("Committed", control.Process(
            frame, 100, 1, 1, "Tutorial_01").State);
        True(new LiveWorldSessionStore(controlRoot).Load().Snapshot.Landmarks.Count > 0);

        StencilRect[] compositionExclusions =
        [
            new(0, 0, 24, 18)
        ];
        StencilRect[] groundAlignmentExclusions =
        [
            new(0, 0, frame.Width, frame.Height)
        ];
        var pipeline = new LiveWorldPipelineReflection(splitRoot, _ => { });
        Equal("Committed", pipeline.Process(
            frame, 100, 1, 1, "Tutorial_01",
            exclusions: compositionExclusions,
            groundAlignmentExclusions: groundAlignmentExclusions).State);

        var snapshot = new LiveWorldSessionStore(splitRoot).Load().Snapshot;
        var observation = snapshot.Observations.Single();
        Equal(0, snapshot.Landmarks.Count);
        Equal(compositionExclusions.Length, observation.ExcludedRects.Count);
        for (var index = 0; index < compositionExclusions.Length; index++)
        {
            var expected = compositionExclusions[index];
            var actual = observation.ExcludedRects[index];
            Equal(new AtlasPoint(expected.X, expected.Y), actual.Origin);
            Equal(new WorldSize(expected.Width, expected.Height), actual.Size);
        }
        True(new SceneSessionStore(splitRoot).Manifest.Observations.Single()
            .ExcludedRects.SequenceEqual(compositionExclusions));
    });
}

static void TestPersistentAppendFailureDoesNotFallBackToTransientAtlasOrTracker()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var failAppend = true;
        var pipeline = new LiveWorldPipelineReflection(
            root,
            stages.Add,
            stage => failAppend && stage == "append");

        var failed = pipeline.Process(
            PipelineSolidFrame(1), 100, 1, 1, "Tutorial_01");
        Equal("Failed", failed.State);
        True(!failed.AllowsAtlasWrite && !failed.HasRebuiltAtlas);
        True(stages.Count == 0);
        Equal(0, new SceneSessionStore(root).Manifest.Observations.Count);
        Equal(0, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);

        failAppend = false;
        var retry = pipeline.Process(
            PipelineSolidFrame(2), 101, 1.3, 1, "Tutorial_01");
        Equal("Committed", retry.State);
        Equal(1, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);
    });
}

static void TestCorruptPersistentWorldBlocksAtlasInsteadOfFallingBackToMemory()
{
    WithTemporaryDirectory(root =>
    {
        var store = new SceneSessionStore(root);
        True(store.SaveScene(
            Encoding.UTF8.GetBytes("not a live world snapshot"),
            expectedRevision: 0,
            newRevision: 1));
        var snapshotPath = Path.Combine(root, "scene.snapshot");
        var original = File.ReadAllBytes(snapshotPath);
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);

        var failed = pipeline.Process(
            PipelineSolidFrame(1), 100, 1, 1, "Tutorial_01");
        Equal("Failed", failed.State);
        True(!failed.AllowsAtlasWrite && !failed.HasRebuiltAtlas);
        True(failed.Diagnostic.Contains("reopen failed", StringComparison.Ordinal));
        True(stages.Count == 0);
        True(File.ReadAllBytes(snapshotPath).SequenceEqual(original));
        Equal(0, new SceneSessionStore(root).Manifest.Observations.Count);
    });
}

static void TestDelayedWorldTimestampIsDiscardedWithoutRewindingTrackerVisit()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);
        Equal("Committed", pipeline.Process(
            PipelineSolidFrame(1), 1_000, 100, 7, "Tutorial_01",
            localPose: new AtlasPoint(0, 0)).State);
        var committedStages = stages.Count;

        var stale = pipeline.Process(
            PipelineSolidFrame(2), 999, 99, 7, "Tutorial_01",
            localPose: new AtlasPoint(300, 0));
        Equal("PendingRecovery", stale.State);
        True(stale.Diagnostic.Contains("stale world timestamp", StringComparison.Ordinal));
        Equal(committedStages, stages.Count);

        Equal("Committed", pipeline.Process(
            PipelineSolidFrame(3), 1_001, 101, 7, "Tutorial_01",
            localPose: new AtlasPoint(80, 0)).State);
        var snapshot = new LiveWorldSessionStore(root).Load().Snapshot;
        True(snapshot.Observations.Select(value => value.Timestamp).SequenceEqual([100d, 101d]));
        Equal(1, snapshot.RelativeMotionEdges.Count);
        CloseWithin(80, snapshot.RelativeMotionEdges.Single().DeltaX, 0.000_001);
    });
}

static void TestRecoverySearchIsTransientAndReturnsConfirmedWorldPlacement()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);
        var pixels = PipelineTexturePixels(71);
        var initial = pipeline.Process(
            PipelineTextureFrameFromPixels(1, pixels), 100, 1, 1, "Tutorial_01");
        Equal("Committed", initial.State);
        var committedCount = new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count;
        var publicationCount = stages.Count;

        pipeline.NotifyTrackingLoss(1);
        var firstSearch = pipeline.Process(
            PipelineTextureFrameFromPixels(2, pixels), 101, 1.3, 1, "Tutorial_01",
            localPose: new AtlasPoint(80, 0));
        Equal("PendingRecovery", firstSearch.State);
        True(firstSearch.ConfirmedRecoveryPose is null);
        var confirmed = pipeline.Process(
            PipelineTextureFrameFromPixels(3, pixels), 102, 1.6, 1, "Tutorial_01",
            localPose: new AtlasPoint(80, 0));
        Equal("PendingRecovery", confirmed.State);
        var recoveredPose = confirmed.ConfirmedRecoveryPose
            ?? throw new InvalidOperationException("Recovery did not return its confirmed placement.");
        ClosePoint(new AtlasPoint(0, 0), recoveredPose, .01);
        ClosePoint(recoveredPose, confirmed.CameraPose, .01);
        ClosePoint(new AtlasPoint(-80, 0), confirmed.AppliedCorrection, .01);
        ClosePoint(confirmed.AppliedCorrection, confirmed.WorldBasisOffset, .01);
        True(!confirmed.AllowsAtlasWrite && !confirmed.HasRebuiltAtlas);
        Equal(publicationCount, stages.Count);
        var afterRecovery = new LiveWorldSessionStore(root).Load().Snapshot;
        Equal(initial.Revision, afterRecovery.MapRevision);
        Equal(committedCount, afterRecovery.Observations.Count);
        Equal(committedCount, new SceneSessionStore(root).Manifest.Observations.Count);

        var later = pipeline.Process(
            PipelineTextureFrameFromPixels(4, pixels), 103, 1.9, 1, "Tutorial_01",
            localPose: new AtlasPoint(84, 0));
        Equal("Committed", later.State);
        ClosePoint(new AtlasPoint(4, 0), later.CameraPose, .01);
        True(stages.Skip(publicationCount).SequenceEqual(
            ["append", "ingest", "commit", "publish"]));
        var afterNormalFrame = new LiveWorldSessionStore(root).Load().Snapshot;
        True(afterNormalFrame.MapRevision > afterRecovery.MapRevision);
        Equal(committedCount + 1, afterNormalFrame.Observations.Count);
        Equal(new AtlasPoint(84, 0), afterNormalFrame.Observations[^1].RawPose);
    });
}

static void TestPipelineCadenceSuppressesDuplicatePersistence()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);
        Equal("Committed", pipeline.Process(
            PipelineSolidFrame(1), 100, 10, 1, "Tutorial_01").State);
        var afterFirst = stages.Count;
        Equal("Throttled", pipeline.Process(
            PipelineSolidFrame(2), 101, 10.05, 1, "Tutorial_01").State);
        Equal(afterFirst, stages.Count);
        Equal(1, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);

        Equal("Committed", pipeline.Process(
            PipelineSolidFrame(3), 102, 10.2, 1, "Tutorial_01").State);
        Equal(2, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);
    });
}

static void TestPipelineRejectsStaleCaptureGeneration()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);
        Equal("Committed", pipeline.Process(
            PipelineSolidFrame(1), 100, 1, 2, "Tutorial_01").State);
        var publishedStages = stages.Count;

        var stale = pipeline.Process(
            PipelineSolidFrame(2), 101, 1.3, 1, "Tutorial_01");
        Equal("PendingRecovery", stale.State);
        True(stale.Diagnostic.Contains("stale capture generation", StringComparison.Ordinal));
        Equal(publishedStages, stages.Count);

        var unverifiedNewGeneration = pipeline.Process(
            PipelineSolidFrame(1), 102, 1.6, 3, "Tutorial_01",
            stableTrackingVerified: false);
        Equal("PendingRecovery", unverifiedNewGeneration.State);
        Equal(publishedStages, stages.Count);
        Equal("Committed", pipeline.Process(
            PipelineSolidFrame(2), 103, 1.9, 3, "Tutorial_01").State);
        Equal(2, new LiveWorldSessionStore(root).Load().Snapshot.Observations.Count);
    });
}

static void TestPipelineRollsBackAfterCasFailureAndSuppressesPublication()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var contended = false;
        var pipeline = new LiveWorldPipelineReflection(root, stage =>
        {
            stages.Add(stage);
            if (stage != "ingest" || contended) return;
            contended = true;
            var competitor = new LiveWorldSessionStore(root);
            var state = competitor.Load();
            var replacement = new LiveWorldSnapshot(
                mapRevision: checked(state.Snapshot.MapRevision + 1));
            True(competitor.Commit(
                replacement,
                state.Snapshot.MapRevision,
                replacement.MapRevision));
        });

        var failed = pipeline.Process(
            PipelineSolidFrame(1), 100, 1, 1, "Tutorial_01");
        Equal("Failed", failed.State);
        True(failed.Diagnostic.Contains("CAS was stale", StringComparison.Ordinal));
        True(stages.SequenceEqual(["append", "ingest"]));
        var rolledBack = new LiveWorldSessionStore(root).Load().Snapshot;
        Equal(1, rolledBack.MapRevision);
        Equal(0, rolledBack.Observations.Count);

        var retry = pipeline.Process(
            PipelineSolidFrame(2), 101, 1.3, 1, "Tutorial_01");
        Equal("Committed", retry.State);
        var recovered = new LiveWorldSessionStore(root).Load().Snapshot;
        Equal(retry.Revision, recovered.MapRevision);
        True(recovered.MapRevision > rolledBack.MapRevision);
        Equal(1, recovered.Observations.Count);
    });
}

static void TestPipelinePublishesClosureOnlyAfterRebuildAndCommit()
{
    WithTemporaryDirectory(root =>
    {
        var stages = new List<string>();
        var pipeline = new LiveWorldPipelineReflection(root, stages.Add);
        var firstTexture = PipelineTexturePixels(191);
        var secondTexture = PipelineTexturePixels(811);
        Equal("Committed", pipeline.Process(
            PipelineTextureFrameFromPixels(1, firstTexture), 100, 1, 1, "Tutorial_01",
            localPose: new AtlasPoint(0, 0)).State);
        Equal("Committed", pipeline.Process(
            PipelineTextureFrameFromPixels(2, secondTexture), 101, 1.3, 1, "Tutorial_01",
            localPose: new AtlasPoint(160, 0)).State);
        Equal("Committed", pipeline.Process(
            PipelineTextureFrameFromPixels(3, firstTexture), 102, 1.6, 1, "Tutorial_01",
            localPose: new AtlasPoint(0, 0)).State);
        var closure = pipeline.Process(
            PipelineTextureFrameFromPixels(4, firstTexture), 103, 1.9, 1, "Tutorial_01",
            localPose: new AtlasPoint(0, 0));

        Equal("Committed", closure.State);
        True(closure.AcceptedClosure is not null);
        var rebuildIndex = stages.LastIndexOf("closure-rebuild");
        var commitIndex = stages.FindIndex(rebuildIndex + 1, value => value == "commit");
        var basisIndex = stages.FindIndex(commitIndex + 1, value => value == "closure-basis");
        var publishIndex = stages.FindIndex(basisIndex + 1, value => value == "publish");
        True(rebuildIndex >= 0
             && commitIndex > rebuildIndex
             && basisIndex > commitIndex
             && publishIndex > basisIndex);
    });
}

static void TestLiveRoomBoundaryAuditWhenRequested()
{
    if (Environment.GetEnvironmentVariable("HKV_LIVE_ROOM_AUDIT") != "1")
    {
        Console.WriteLine("SKIP live room boundary audit: set HKV_LIVE_ROOM_AUDIT=1.");
        return;
    }

    var configuredRoot = Environment.GetEnvironmentVariable("HKV_LIVE_WORLD_ROOT");
    var root = string.IsNullOrWhiteSpace(configuredRoot)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HollowKnightVision",
            "atlas-v1",
            "active",
            "live-world")
        : Path.GetFullPath(configuredRoot);
    if (!Directory.Exists(root))
    {
        throw new DirectoryNotFoundException(
            $"The requested live-world audit root does not exist: {root}");
    }

    var snapshot = new LiveWorldSessionStore(root).Load().Snapshot;
    var chronological = snapshot.Observations
        .OrderBy(value => value.Timestamp)
        .ThenBy(value => value.Id)
        .ToArray();
    var rooms = chronological.Select(value => value.RoomId).Distinct().ToArray();
    if (rooms.Length != 2)
    {
        throw new InvalidDataException(
            $"The live room audit requires exactly two persisted rooms; found {rooms.Length}.");
    }
    var rightRoomID = chronological[0].RoomId;
    var firstLaterRoom = chronological.FirstOrDefault(value => value.RoomId != rightRoomID)
        ?? throw new InvalidDataException(
            "The live room audit could not derive a chronological later room.");
    var leftRoomID = firstLaterRoom.RoomId;

    var result = new LiveWorldPipelineReflection(root, _ => { })
        .BoundaryEvidence(leftRoomID, rightRoomID);
    if (result.PersistenceIssue is { Length: > 0 } issue)
    {
        throw new InvalidDataException(issue);
    }
    var evidence = result.Evidence ?? throw new InvalidDataException(
        "The requested live-world root has no usable room-boundary evidence.");
    Equal(leftRoomID, evidence.LeftRoomID);
    Equal(rightRoomID, evidence.RightRoomID);
    AssertNonemptyBoundaryBand(
        evidence.RightRoomLeftBand,
        "prior/right room left edge");
    AssertNonemptyBoundaryBand(
        evidence.LeftRoomRightBand,
        "later/left room right edge");
}

static void TestLiveRoomBoundaryAuditSelectsChronologicalPersistedEvidence()
{
    WithTemporaryDirectory(workspace =>
    {
        const int width = 64;
        const int height = 36;
        const int rightRoomID = 0x13579;
        const int leftRoomID = 0x24680;
        var root = Path.Combine(workspace, "live-world");
        var store = new LiveWorldSessionStore(root);
        var rightOld = store.Append(
            RoomBoundaryFrame(width, height, blackLeftWidth: 3, blackRightWidth: 0, 1),
            1,
            new AtlasPoint(10, 1),
            width);
        var rightLatest = store.Append(
            RoomBoundaryFrame(width, height, blackLeftWidth: 8, blackRightWidth: 0, 2),
            2,
            new AtlasPoint(20, 2),
            width);
        var leftFirst = store.Append(
            RoomBoundaryFrame(width, height, blackLeftWidth: 0, blackRightWidth: 6, 3),
            3,
            new AtlasPoint(30, 3),
            width);
        var leftLater = store.Append(
            RoomBoundaryFrame(width, height, blackLeftWidth: 0, blackRightWidth: 12, 4),
            4,
            new AtlasPoint(40, 4),
            width);

        LiveWorldObservation Observation(
            int id,
            StoredFrameObservation source,
            int roomID,
            AtlasPoint optimizedPose) => LiveWorldObservation.Create(
                id,
                source.Id,
                source.Timestamp,
                width,
                height,
                source.SolveWidth,
                16,
                [],
                source.CameraPosition,
                optimizedPose,
                roomId: roomID);

        var rightOldPose = new AtlasPoint(101, 11);
        var rightLatestPose = new AtlasPoint(202, 22);
        var leftFirstPose = new AtlasPoint(303, 33);
        var leftLaterPose = new AtlasPoint(404, 44);
        var snapshot = new LiveWorldSnapshot(
            mapRevision: 1,
            anchoredObservationId: 0,
            observations:
            [
                Observation(0, rightOld, rightRoomID, rightOldPose),
                Observation(1, rightLatest, rightRoomID, rightLatestPose),
                Observation(2, leftFirst, leftRoomID, leftFirstPose),
                Observation(3, leftLater, leftRoomID, leftLaterPose)
            ]);
        True(store.Commit(snapshot, expectedRevision: 0, newRevision: 1));

        var pipeline = new LiveWorldPipelineReflection(root, _ => { });
        var result = pipeline.BoundaryEvidence(leftRoomID, rightRoomID);
        True(result.PersistenceIssue is null);
        var evidence = result.Evidence ?? throw new InvalidOperationException(
            "Persisted two-room fixture produced no boundary evidence.");
        Equal(leftRoomID, evidence.LeftRoomID);
        Equal(rightRoomID, evidence.RightRoomID);
        ClosePoint(leftFirstPose, evidence.LeftOriginalEntryWorldPose);
        ClosePoint(rightLatestPose, evidence.RightDepartureWorldPose);
        Close(width, evidence.SolveWidth);
        Close(height, evidence.SolveHeight);
        var rightBand = AssertNonemptyBoundaryBand(
            evidence.RightRoomLeftBand,
            "selected prior/right room left edge");
        var leftBand = AssertNonemptyBoundaryBand(
            evidence.LeftRoomRightBand,
            "selected later/left room right edge");
        CloseWithin(8d / width, rightBand.WidthFraction, 0.000_001);
        CloseWithin(6d / width, leftBand.WidthFraction, 0.000_001);

        File.Delete(Path.Combine(
            root,
            leftFirst.ImagePath.Replace('/', Path.DirectorySeparatorChar)));
        var missing = pipeline.BoundaryEvidence(leftRoomID, rightRoomID);
        True(missing.Evidence is null);
        True(!string.IsNullOrWhiteSpace(missing.PersistenceIssue));
    });
}

static FrameEdgeBlackBand AssertNonemptyBoundaryBand(
    FrameEdgeBlackBand? band,
    string label)
{
    var value = band ?? throw new InvalidOperationException(
        $"Missing {label} boundary band.");
    True(value.WidthFraction > 0);
    True(value.ContentYRangeFraction.Upper > value.ContentYRangeFraction.Lower);
    return value;
}

static BgraFrame RoomBoundaryFrame(
    int width,
    int height,
    int blackLeftWidth,
    int blackRightWidth,
    long captureID)
{
    var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var offset = checked((y * width + x) * BgraFrame.BytesPerPixel);
            var visible = x >= blackLeftWidth && x < width - blackRightWidth;
            var value = visible ? (byte)180 : (byte)0;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = byte.MaxValue;
        }
    }
    return new BgraFrame(
        width,
        height,
        checked(width * BgraFrame.BytesPerPixel),
        pixels,
        captureID,
        DateTimeOffset.UnixEpoch.AddSeconds(captureID));
}

static BgraFrame PipelineSolidFrame(long captureId) => new(
    320,
    180,
    320 * BgraFrame.BytesPerPixel,
    Enumerable.Repeat((byte)24, 320 * 180 * BgraFrame.BytesPerPixel).ToArray(),
    captureId,
    DateTimeOffset.UnixEpoch);

static byte[] PipelineTexturePixels(int seed)
{
    const int width = 320;
    const int height = 180;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    var state = unchecked((uint)seed);
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            state = unchecked(state * 1_664_525u + 1_013_904_223u);
            var value = (byte)((state >> 24) ^ (uint)(x * 11 + y * 17));
            var offset = (y * width + x) * BgraFrame.BytesPerPixel;
            pixels[offset] = value;
            pixels[offset + 1] = (byte)(value ^ 0x59);
            pixels[offset + 2] = (byte)(value ^ 0xA6);
            pixels[offset + 3] = 255;
        }
    }
    return pixels;
}

static BgraFrame PipelineTextureFrame(long captureId, int seed) =>
    PipelineTextureFrameFromPixels(captureId, PipelineTexturePixels(seed));

static BgraFrame PipelineTextureFrameFromPixels(long captureId, byte[] pixels) => new(
    320,
    180,
    320 * BgraFrame.BytesPerPixel,
    pixels.ToArray(),
    captureId,
    DateTimeOffset.UnixEpoch);

static BgraFrame Frame(long id) => new(
    2,
    2,
    8,
    Enumerable.Repeat((byte)id, 16).ToArray(),
    id,
    DateTimeOffset.UnixEpoch);

static void TestSynchronizerRequiresMatchingUnityFrameInEitherArrivalOrder()
{
    var synchronizer = new HackerFrameSynchronizer(capacity: 4);
    var image = HackerSolidFrame();
    var firstSample = HackerSample(frame: 99, cameraX: 0);

    True(synchronizer.Append(firstSample, observedAt: 1) is null);
    var first = synchronizer.Append(new HackerCapturedFrame(
        99,
        image,
        1.1,
        false)) ?? throw new InvalidOperationException("Matching telemetry did not join its capture.");
    Equal(99L, first.Sample.UnityFrame);
    Equal(99, first.Capture.UnityFrame);

    True(synchronizer.Append(new HackerCapturedFrame(
        100,
        image,
        2,
        true)) is null);
    True(synchronizer.Append(
        HackerSample(frame: 101, cameraX: 1),
        observedAt: 2.1) is null);
    var second = synchronizer.Append(
        HackerSample(frame: 100, cameraX: 1),
        observedAt: 2.2) ?? throw new InvalidOperationException(
            "Matching capture did not join its telemetry.");
    Equal(100, second.Capture.UnityFrame);
    Equal(100L, second.Sample.UnityFrame);
}

static void TestDirectCameraPlacementUsesIndependentXYProjectionScale()
{
    var transform = new GroundTruthCameraTransform(
        "Room",
        new AtlasPoint(12, -3),
        4,
        6,
        new WorldSize(640, 360));

    Equal(
        new AtlasPoint(48, -18),
        HackerAtlasCameraPlacement.CameraPosition(transform));
}

static void TestRoomLayoutKeepsTreeDoorwaysRectilinearWithAGap()
{
    var a = HackerRoom("A", new AtlasPoint(0, 0), new WorldSize(100, 80));
    var b = HackerRoom("B", new AtlasPoint(0, 0), new WorldSize(120, 60));
    var c = HackerRoom("C", new AtlasPoint(0, 0), new WorldSize(90, 100));
    var ab = HackerConnection(
        a,
        b,
        HackerRoomSide.Right,
        fromCoordinate: 40,
        toCoordinate: 30);
    var bc = HackerConnection(
        b,
        c,
        HackerRoomSide.Right,
        fromCoordinate: 30,
        toCoordinate: 50);

    var layout = HackerRoomLayout.Make([a, b, c], [ab, bc]);

    Equal(new AtlasPoint(0, 0), layout.Positions[a.Id]);
    Equal(new AtlasPoint(124, 10), layout.Positions[b.Id]);
    Equal(new AtlasPoint(268, -10), layout.Positions[c.Id]);
    True(layout.FreeformConnectionIds.Count == 0);
}

static void TestRoomLayoutLeavesContradictoryLoopAsFreeformConnection()
{
    var a = HackerRoom("A", new AtlasPoint(0, 0), new WorldSize(100, 100));
    var b = HackerRoom("B", new AtlasPoint(0, 0), new WorldSize(100, 100));
    var c = HackerRoom("C", new AtlasPoint(0, 0), new WorldSize(100, 100));
    var ab = HackerConnection(a, b, HackerRoomSide.Right);
    var bc = HackerConnection(b, c, HackerRoomSide.Top);
    var impossibleClosure = HackerConnection(
        c,
        a,
        HackerRoomSide.Left,
        HackerRoomSide.Right);

    var layout = HackerRoomLayout.Make(
        [a, b, c],
        [ab, bc, impossibleClosure]);

    True(!layout.FreeformConnectionIds.Contains(ab.Id));
    True(!layout.FreeformConnectionIds.Contains(bc.Id));
    True(layout.FreeformConnectionIds.Contains(impossibleClosure.Id));
    Equal(new AtlasPoint(124, 0), layout.Positions[b.Id]);
    Equal(new AtlasPoint(124, 124), layout.Positions[c.Id]);
}

static void TestRoomLayoutFansOverlappingBranchIntoNewRectifiedIsland()
{
    var a = HackerRoom("A", new AtlasPoint(0, 0), new WorldSize(100, 100));
    var b = HackerRoom("B", new AtlasPoint(0, 0), new WorldSize(100, 100));
    var c = HackerRoom("C", new AtlasPoint(0, 0), new WorldSize(100, 100));
    var ab = HackerConnection(a, b, HackerRoomSide.Right);
    var ac = HackerConnection(a, c, HackerRoomSide.Right);

    var layout = HackerRoomLayout.Make([a, b, c], [ab, ac]);
    var bPosition = layout.Positions[b.Id];
    var cPosition = layout.Positions[c.Id];

    Equal(new AtlasPoint(124, 0), bPosition);
    Equal(124d, cPosition.X);
    True(cPosition.Y != 0);
    True(layout.FreeformConnectionIds.Contains(ac.Id));
    True(!b.Bounds.Offset(bPosition.X, bPosition.Y).Intersects(
        c.Bounds.Offset(cPosition.X, cPosition.Y)));
}

static HackerAtlasRoom HackerRoom(
    string name,
    AtlasPoint position,
    WorldSize size) => new(
        Guid.NewGuid(),
        name,
        position,
        new HackerAtlasRect(0, 0, size.Width, size.Height));

static HackerRoomConnection HackerConnection(
    HackerAtlasRoom from,
    HackerAtlasRoom to,
    HackerRoomSide fromSide,
    HackerRoomSide? toSide = null,
    double fromCoordinate = 50,
    double toCoordinate = 50) => new(
        Guid.NewGuid(),
        from.Id,
        to.Id,
        fromSide,
        toSide ?? fromSide.Opposite(),
        fromCoordinate,
        toCoordinate);

static ReceiverGroundTruthSample HackerSample(
    long frame,
    double cameraX,
    string scene = "A") => new(
        Version: 2,
        Type: "groundTruth",
        SessionId: Guid.NewGuid(),
        Sequence: checked((ulong)frame),
        UnityFrame: frame,
        UnityRealtime: frame,
        SceneName: scene,
        HeroAvailable: true,
        HeroX: 0,
        HeroY: 0,
        HeroZ: 0,
        VelocityX: 0,
        VelocityY: 0,
        FacingRight: true,
        Grounded: true,
        CameraAvailable: true,
        CameraX: cameraX,
        CameraY: 4,
        CameraZ: -38,
        CameraTargetX: cameraX,
        CameraTargetY: 4,
        CameraTargetZ: 0,
        OrthographicSize: 18,
        PixelsPerWorldUnitX: 1,
        PixelsPerWorldUnitY: 1,
        HeroScreenX: null,
        HeroScreenY: null,
        ProjectionPixelWidth: 64,
        ProjectionPixelHeight: 36,
        ScreenWidth: 64,
        ScreenHeight: 36);

static BgraFrame HackerSolidFrame()
{
    const int width = 64;
    const int height = 36;
    var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
    for (var offset = 0; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
    {
        pixels[offset] = 153;
        pixels[offset + 1] = 102;
        pixels[offset + 2] = 51;
        pixels[offset + 3] = 255;
    }
    return new BgraFrame(
        width,
        height,
        width * BgraFrame.BytesPerPixel,
        pixels,
        1,
        DateTimeOffset.UnixEpoch);
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }
}

static void Close(double expected, double actual)
{
    CloseWithin(expected, actual, 0.0001);
}

static void CloseWithin(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }
}

static void ClosePoint(AtlasPoint expected, AtlasPoint actual, double tolerance = 0.000001)
{
    CloseWithin(expected.X, actual.X, tolerance);
    CloseWithin(expected.Y, actual.Y, tolerance);
}

internal sealed record LiveWorldPipelineReflectionResult(
    string State,
    string Diagnostic,
    int Revision,
    object? AcceptedClosure,
    AtlasPoint CameraPose,
    AtlasPoint AppliedCorrection,
    AtlasPoint WorldBasisOffset,
    AtlasPoint? ConfirmedRecoveryPose,
    bool AllowsAtlasWrite,
    bool HasRebuiltAtlas);

internal sealed record LiveRoomBoundaryEvidenceReflectionResult(
    VisualRoomBoundaryEvidence? Evidence,
    string? PersistenceIssue);

internal sealed class LiveWorldPipelineReflection
{
    private static readonly Assembly AppAssembly = LoadAppAssembly();
    private static readonly Type PipelineType = RequiredType(
        "HollowKnightVision.Windows.App.WindowsLiveWorldPipeline");
    private static readonly Type FrameType = RequiredType(
        "HollowKnightVision.Windows.App.WindowsLiveWorldFrame");
    private static readonly ConstructorInfo PipelineConstructor = PipelineType.GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [typeof(string), typeof(Action<string>), typeof(Func<string, bool>)],
        modifiers: null) ?? throw new MissingMethodException(
            PipelineType.FullName,
            ".ctor(string, Action<string>, Func<string, bool>)");
    private static readonly ConstructorInfo FrameConstructor = FrameType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(constructor => constructor.GetParameters().Length == 13);
    private static readonly MethodInfo ProcessMethod = PipelineType.GetMethod(
        "Process",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(PipelineType.FullName, "Process");
    private static readonly MethodInfo NotifyTrackingLossMethod = PipelineType.GetMethod(
        "NotifyTrackingLoss",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(PipelineType.FullName, "NotifyTrackingLoss");
    private static readonly MethodInfo InvalidatePendingWorkMethod = PipelineType.GetMethod(
        "InvalidatePendingWork",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(PipelineType.FullName, "InvalidatePendingWork");
    private static readonly MethodInfo ReopenMethod = PipelineType.GetMethod(
        "Reopen",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(PipelineType.FullName, "Reopen");
    private static readonly FieldInfo AcceptsWorkField = RequiredField(
        "acceptsWork",
        typeof(bool));
    private static readonly MethodInfo BoundaryEvidenceMethod = PipelineType.GetMethod(
        "BoundaryEvidence",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(PipelineType.FullName, "BoundaryEvidence");

    private readonly object pipeline;

    internal LiveWorldPipelineReflection(
        string root,
        Action<string> transactionObserver,
        Func<string, bool>? transactionFault = null)
    {
        pipeline = PipelineConstructor.Invoke([root, transactionObserver, transactionFault]);
    }

    internal LiveWorldPipelineReflectionResult Process(
        BgraFrame frame,
        double wallTimestamp,
        double monotonicTimestamp,
        ulong captureGeneration,
        string roomName,
        bool stableTrackingVerified = true,
        AtlasPoint? localPose = null,
        IReadOnlyList<StencilRect>? exclusions = null,
        IReadOnlyList<StencilRect>? groundAlignmentExclusions = null)
    {
        var request = FrameConstructor.Invoke(
        [
            frame,
            frame,
            wallTimestamp,
            monotonicTimestamp,
            localPose ?? new AtlasPoint(0, 0),
            160d,
            exclusions ?? Array.Empty<StencilRect>(),
            groundAlignmentExclusions ?? exclusions ?? Array.Empty<StencilRect>(),
            captureGeneration,
            roomName,
            1d,
            64,
            stableTrackingVerified
        ]);
        var result = Invoke(ProcessMethod, pipeline, request);
        var type = result.GetType();
        return new LiveWorldPipelineReflectionResult(
            type.GetProperty("State")?.GetValue(result)?.ToString()
                ?? throw new MissingMemberException(type.FullName, "State"),
            type.GetProperty("Diagnostic")?.GetValue(result) as string
                ?? throw new MissingMemberException(type.FullName, "Diagnostic"),
            type.GetProperty("Revision")?.GetValue(result) is int revision
                ? revision
                : throw new MissingMemberException(type.FullName, "Revision"),
            type.GetProperty("AcceptedClosure")?.GetValue(result),
            type.GetProperty("CameraPose")?.GetValue(result) is AtlasPoint cameraPose
                ? cameraPose
                : throw new MissingMemberException(type.FullName, "CameraPose"),
            type.GetProperty("AppliedCorrection")?.GetValue(result) is AtlasPoint appliedCorrection
                ? appliedCorrection
                : throw new MissingMemberException(type.FullName, "AppliedCorrection"),
            type.GetProperty("WorldBasisOffset")?.GetValue(result) is AtlasPoint basis
                ? basis
                : throw new MissingMemberException(type.FullName, "WorldBasisOffset"),
            type.GetProperty("ConfirmedRecoveryPose")?.GetValue(result) is AtlasPoint confirmed
                ? confirmed
                : null,
            type.GetProperty("AllowsAtlasWrite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(result) is true,
            type.GetProperty("RebuiltAtlas")?.GetValue(result) is not null);
    }

    internal void NotifyTrackingLoss(ulong captureGeneration) =>
        _ = Invoke(NotifyTrackingLossMethod, pipeline, captureGeneration);

    internal void InvalidatePendingWork() =>
        _ = Invoke(InvalidatePendingWorkMethod, pipeline);

    internal bool Reopen() => Invoke(ReopenMethod, pipeline) is true;

    internal bool AcceptsWork => AcceptsWorkField.GetValue(pipeline) is bool value
        ? value
        : throw new InvalidOperationException("Live-world work admission is not Boolean.");

    internal LiveRoomBoundaryEvidenceReflectionResult BoundaryEvidence(
        int leftRoomID,
        int rightRoomID)
    {
        var result = Invoke(
            BoundaryEvidenceMethod,
            pipeline,
            leftRoomID,
            rightRoomID);
        var type = result.GetType();
        return new LiveRoomBoundaryEvidenceReflectionResult(
            type.GetProperty("Evidence")?.GetValue(result)
                as VisualRoomBoundaryEvidence,
            type.GetProperty("PersistenceIssue")?.GetValue(result) as string);
    }

    private static object Invoke(MethodInfo method, object target, params object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments) ?? new object();
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static Type RequiredType(string name) =>
        AppAssembly.GetType(name, throwOnError: true)
        ?? throw new TypeLoadException(name);

    private static FieldInfo RequiredField(string name, Type expectedType)
    {
        var field = PipelineType.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(PipelineType.FullName, name);
        if (field.FieldType != expectedType)
        {
            throw new InvalidOperationException(
                $"{PipelineType.FullName}.{name} must be {expectedType.FullName}.");
        }
        return field;
    }

    private static Assembly LoadAppAssembly()
    {
        var configuration = Directory.GetParent(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Name
            ?? "Release";
        var windowsRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var path = Path.Combine(
            windowsRoot,
            "src",
            "HollowKnightVision.Windows.App",
            "bin",
            configuration,
            "net8.0-windows10.0.19041.0",
            "HollowKnightVision.Windows.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Build HollowKnightVision.Windows.App before running pipeline regressions.",
                path);
        }
        return Assembly.LoadFrom(path);
    }
}

file static class GroundPixelsParityTests
{
    private static void Check(
        bool value,
        string message = "GroundPixels parity assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void TestGrayscaleOrientationAndBoundaryPatchPreserveEveryPixel()
    {
        const int width = 19;
        const int height = 15;
        var pixels = Enumerable.Range(0, width * height)
            .Select(index => (byte)((index * 37 + 11) % 256))
            .ToArray();
        var bgra = new byte[width * height * BgraFrame.BytesPerPixel];
        for (var index = 0; index < pixels.Length; index++)
        {
            var offset = index * BgraFrame.BytesPerPixel;
            bgra[offset] = pixels[index];
            bgra[offset + 1] = pixels[index];
            bgra[offset + 2] = pixels[index];
            bgra[offset + 3] = byte.MaxValue;
        }
        var frame = new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            bgra,
            1,
            DateTimeOffset.UnixEpoch);

        var extracted = GroundPixels.Luma(frame, width, height)
            ?? throw new InvalidOperationException("Expected BGRA luma extraction.");
        Check(extracted.SequenceEqual(pixels));
        var patch = GroundPixels.Patch(extracted, width, height, 3, 3)
            ?? throw new InvalidOperationException("Expected boundary patch.");
        Check(patch.Length == 192);
        for (var index = 0; index < patch.Length; index++)
        {
            Check(patch[index]
                == pixels[(3 + index / 16) * width + 3 + index % 16]);
        }
    }

    public static void TestOutsideOrIncompleteTextureCannotBecomePatchEvidence()
    {
        var pixels = Enumerable.Repeat((byte)20, 16 * 12).ToArray();
        Check(GroundPixels.Patch(pixels, 16, 12, -1, 0) is null);
        Check(GroundPixels.Patch(pixels, 16, 12, 1, 0) is null);
        Check(GroundPixels.Patch(pixels, 16, 12, 0, 1) is null);
        Check(GroundPixels.Patch(pixels[..^1], 16, 12, 0, 0) is null);
    }
}

file static class GameStartupCliTailParityTests
{
    private static readonly Assembly HostAssembly = LoadHostAssembly();

    public static void TestAutomaticNavigationRequiresExplicitLaunchFlag()
    {
        var type = RequiredType(
            "HollowKnightVision.Windows.Host.HollowKnightVisionLaunchOptions");
        var ordinary = InvokeStatic(type, "Parse", [new[] { "HollowKnightVision" }])
            ?? throw new InvalidOperationException("Launch options parser returned null.");
        Check(!Property<bool>(ordinary, "AutoNavigateToGameplay"));
        Check(!Property<bool>(ordinary, "AutomationControlEnabled"));

        var automated = InvokeStatic(type, "Parse", [new[]
        {
            "HollowKnightVision", "--auto-navigate-gameplay",
            "--enable-automation-control"
        }]) ?? throw new InvalidOperationException("Launch options parser returned null.");
        Check(Property<bool>(automated, "AutoNavigateToGameplay"));
        Check(Property<bool>(automated, "AutomationControlEnabled"));
        Equal(
            "--ground-texture-subpixel",
            InvokeStatic(type, "RetiredArgument", [new[]
            {
                "HollowKnightVision", "--ground-texture-subpixel"
            }]) as string);
        Check(InvokeStatic(type, "RetiredArgument", [new[]
        {
            "HollowKnightVision", "--ground-keyframe-subpixel"
        }]) is null);
        Equal(
            "--ground-texture-affine",
            InvokeStatic(type, "RetiredArgument", [new[]
            {
                "HollowKnightVision", "--ground-texture-affine"
            }]) as string);
    }

    public static void TestAutomationCommandParsingBoundsDurations()
    {
        var type = RequiredType("HollowKnightVision.Windows.Host.GameAutomationCommand");
        AssertCommand(type, "up:2.25", "Up", 2.25);
        AssertCommand(type, "left", "Left", 1.5);
        Check(InvokeStatic(type, "Parse", ["up:0"]) is null);
        Check(InvokeStatic(type, "Parse", ["up:11"]) is null);
        Check(InvokeStatic(type, "Parse", ["unknown:1"]) is null);
    }

    public static void TestRetiredOptionsCannotSilentlyRunDefaultExperiment()
    {
        var type = RequiredType(
            "HollowKnightVision.Windows.Host.HollowKnightVisionLaunchOptions");
        Equal(
            "--ground-snap-audit-directory=/tmp/audit",
            InvokeStatic(type, "RetiredArgument", [new[]
            {
                "vision", "--ground-snap-audit-directory=/tmp/audit"
            }]) as string);
        Equal(
            "--replay",
            InvokeStatic(type, "RetiredArgument", [new[]
            {
                "vision", "--replay", "movie.mov"
            }]) as string);
        Check(InvokeStatic(type, "RetiredArgument", [new[]
        {
            "vision", "--world-replay-session", "/tmp/session",
            "--trace-ground-tracking", "--render-frame-marker"
        }]) is null);
    }

    private static void AssertCommand(
        Type type,
        string serialized,
        string expectedButton,
        double expectedDuration)
    {
        var command = InvokeStatic(type, "Parse", [serialized])
            ?? throw new InvalidOperationException($"Command was rejected: {serialized}");
        Equal(expectedButton, Property<object>(command, "Button").ToString());
        Equal(expectedDuration, Property<double>(command, "Duration"));
    }

    private static T Property<T>(object value, string name) =>
        (T)(value.GetType().GetProperty(name)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, name));

    private static object? InvokeStatic(Type type, string name, object?[] arguments)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, name);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static Type RequiredType(string name) =>
        HostAssembly.GetType(name, throwOnError: true)
        ?? throw new TypeLoadException(name);

    private static Assembly LoadHostAssembly()
    {
        var configuration = Directory.GetParent(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Name
            ?? "Release";
        var windowsRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var path = Path.Combine(
            windowsRoot,
            "src",
            "HollowKnightVision.Windows.Host",
            "bin",
            configuration,
            "net8.0",
            "HollowKnightVision.Windows.Host.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Build HollowKnightVision.Windows.Host before running CLI regressions.",
                path);
        }
        return Assembly.LoadFrom(path);
    }

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Game startup CLI parity assertion failed.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }
}

file static class DashboardPresentationParityTests
{
    private static void Check(bool value, string message = "Dashboard presentation parity assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, double tolerance = .001) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");

    public static void TestModelWorkspaceSelectionRetainsTabAndRowsAcrossViewRecreation()
    {
        var objectId = Guid.NewGuid();
        var screenshot = Guid.NewGuid();
        var contract = Guid.NewGuid();
        var failure = Guid.NewGuid();
        var selection = new LabelingModelWorkspaceSelection
        {
            Tab = LabelingModelTab.Screenshots,
            ObjectExampleIdentifier = objectId,
            ScreenshotIdentifier = screenshot,
            ScreenshotGroup = "brightness",
            ContractExampleIdentifier = contract,
            SummaryReviewClassIdentifier = "enemies.crawlid",
            SummaryFailureIdentifier = failure,
        };

        Check(selection.Tab == LabelingModelTab.Screenshots);
        Check(selection.ObjectExampleIdentifier == objectId);
        Check(selection.ScreenshotIdentifier == screenshot);
        Check(selection.ScreenshotGroup == "brightness");
        Check(selection.ContractExampleIdentifier == contract);
        Check(selection.SummaryReviewClassIdentifier == "enemies.crawlid");
        Check(selection.SummaryFailureIdentifier == failure);
    }

    public static void TestMenuObjectParticlesSpawnAtBottomAndRecycleUpward()
    {
        var size = new WorldSize(600, 400);
        Check(MenuObjectParticleMotion.Placement(0, 1, 4, size) is null);

        var spawned = MenuObjectParticleMotion.Placement(0, 0, 4, size)
            ?? throw new InvalidOperationException("Expected first particle to spawn.");
        Near(435, spawned.Position.Y);
        Near(0, spawned.Opacity);

        var duration = MenuObjectParticleMotion.TravelDuration(0);
        var halfway = MenuObjectParticleMotion.Placement(duration / 2, 0, 4, size)
            ?? throw new InvalidOperationException("Expected halfway particle placement.");
        Near(200, halfway.Position.Y);
        Near(.10 + MenuObjectParticleMotion.Depth(0) * .18, halfway.Opacity);

        var fadingAtTop = MenuObjectParticleMotion.Placement(
                duration * .95, 0, MenuObjectParticleMotion.DefaultParticleCount, size)
            ?? throw new InvalidOperationException("Expected top particle placement.");
        Check(fadingAtTop.Position.Y < 0);
        Check(fadingAtTop.Opacity < .1);

        var emitterPositions = Enumerable.Range(0, MenuObjectParticleMotion.DefaultParticleCount)
            .Select(index => MenuObjectParticleMotion.Placement(
                index * MenuObjectParticleMotion.SpawnInterval,
                index,
                MenuObjectParticleMotion.DefaultParticleCount,
                size)?.Position.X)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        Check(emitterPositions.Min() < 100);
        Check(emitterPositions.Max() > 500);
        Check(emitterPositions.All(x => x < 150 || x > 450));

        var renderOrder = MenuObjectParticleMotion.RenderOrder(
            MenuObjectParticleMotion.DefaultParticleCount);
        var backIndex = renderOrder.First();
        var frontIndex = renderOrder.Last();
        Check(MenuObjectParticleMotion.Depth(backIndex)
            < MenuObjectParticleMotion.Depth(frontIndex));
        Check(MenuObjectParticleMotion.BaseScale(backIndex)
            < MenuObjectParticleMotion.BaseScale(frontIndex));
        Check(MenuObjectParticleMotion.TravelDuration(backIndex)
            > MenuObjectParticleMotion.TravelDuration(frontIndex));

        var recycled = MenuObjectParticleMotion.Placement(duration, 0, 4, size)
            ?? throw new InvalidOperationException("Expected recycled particle placement.");
        Near(spawned.Position.Y, recycled.Position.Y);
        Near(0, recycled.Opacity);
    }

    public static void TestBottomStatusReportsOnlyRecognizedContext()
    {
        Check(LiveGameStatusPresentationPolicy.Text("Select Profile - 1")
            == "Select Profile - 1");
        Check(LiveGameStatusPresentationPolicy.Text(null) == "Finding State");
        Check(LiveGameStatusPresentationPolicy.Text("Gameplay", "Room 2")
            == "Gameplay · Room 2");
    }

    public static void TestGroundHypothesesProduceOnlyGroundReviewGeometry()
    {
        var tracking = GroundHypothesisTrackingResult.Empty with
        {
            Features =
            [
                new GroundHypothesisFeature(
                    12,
                    7,
                    3,
                    new PixelRect(20, 32, 16, 12),
                    GroundHypothesisClassification.Occluded,
                    null,
                    null),
            ],
            GlobalFeatureCount = 1,
            GroundSegmentCount = 1,
            AtlasFeatures =
            [
                new GroundHypothesisAtlasFeature(
                    7,
                    3,
                    new AtlasPoint(24, 40),
                    Enumerable.Repeat((byte)91, GroundHypothesisTracker.FeaturePixelCount)
                        .ToArray(),
                    []),
            ],
            AtlasLines =
            [
                new GroundHypothesisAtlasLine(
                    7,
                    new AtlasPoint(16, 48),
                    new AtlasPoint(48, 48)),
            ],
            LineReviews =
            [
                new GroundLinePresenceReview(
                    7,
                    new CleanFloorLine(40, new PixelSpan(20, 51)),
                    GroundLinePresenceState.Confirmed,
                    3,
                    .9),
            ],
        };

        var overlay = LiveFeatureReviewOverlay.Make(
            tracking,
            liveBounds: new DashboardRect(100, 200, 640, 360));
        Check(overlay.Markers.Count == 2);
        var atlasMarker = overlay.Markers[0];
        var liveMarker = overlay.Markers[1];
        Check(atlasMarker.Kind == DashboardFeatureReviewMarkerKind.GroundFeature);
        Check(atlasMarker.WorldPosition == new AtlasPoint(24, 40));
        Check(atlasMarker.WorldSize == new WorldSize(16, 12));
        Check(atlasMarker.GroundFeatureDetails?.SegmentId == 7);
        Check(atlasMarker.GroundFeatureDetails?.SequenceIndex == 3);
        Check(atlasMarker.GroundFeatureDetails?.ReferencePixels.Count
            == GroundHypothesisTracker.FeaturePixelCount);
        Check(atlasMarker.IsVisible);
        Check(atlasMarker.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Atlas);
        Check(liveMarker.WorldPosition == new AtlasPoint(128, 522));
        Check(liveMarker.IsVisible);
        Check(liveMarker.Kind == DashboardFeatureReviewMarkerKind.OccludedGroundFeature);
        Check(liveMarker.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Live);
        Check(overlay.Lines.Count == 2);
        var liveLine = overlay.Lines.Single(line =>
            line.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Live);
        Check(liveLine.Kind == DashboardFeatureReviewLineKind.CurrentGround);
        Check(liveLine.Start == new AtlasPoint(120, 519.5));
        Check(liveLine.End == new AtlasPoint(152, 519.5));
        var atlasLine = overlay.Lines.Single(line =>
            line.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Atlas);
        Check(atlasLine.Kind == DashboardFeatureReviewLineKind.PersistentGround);
        Check(atlasLine.Start == new AtlasPoint(16, 48));
        Check(atlasLine.End == new AtlasPoint(48, 48));

        var selectedOverlay = LiveFeatureReviewOverlay.Make(
            tracking,
            selectedGroundFeature: atlasMarker.GroundFeatureDetails,
            liveBounds: new DashboardRect(100, 200, 640, 360));
        var liveHighlight = selectedOverlay.Markers.Single(marker =>
            marker.Kind == DashboardFeatureReviewMarkerKind.SelectedGroundFeature
            && marker.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Live);
        var atlasHighlight = selectedOverlay.Markers.Single(marker =>
            marker.Kind == DashboardFeatureReviewMarkerKind.SelectedGroundFeature
            && marker.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Atlas);
        Check(liveHighlight.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Live);
        Check(liveHighlight.WorldPosition == new AtlasPoint(128, 522));
        Check(liveHighlight.IsVisible);
        Check(atlasHighlight.WorldPosition == new AtlasPoint(24, 40));
        Check(atlasHighlight.IsVisible);
    }

    public static void TestPersistentCleanLineStaysAtFixedAtlasCoordinates()
    {
        static GroundHypothesisTrackingResult Tracking(AtlasPoint start, AtlasPoint end) =>
            GroundHypothesisTrackingResult.Empty with
            {
                GroundSegmentCount = 1,
                AtlasLines = [new GroundHypothesisAtlasLine(4, start, end)],
            };

        var first = LiveFeatureReviewOverlay.Make(
            Tracking(new AtlasPoint(16, 48), new AtlasPoint(48, 48)),
            liveBounds: new DashboardRect(100, 200, 640, 360));
        var moved = LiveFeatureReviewOverlay.Make(
            Tracking(new AtlasPoint(16, 48), new AtlasPoint(48, 48)),
            liveBounds: new DashboardRect(132, 190, 640, 360));
        Check(first.Lines.First().Start == moved.Lines.First().Start);
        Check(first.Lines.First().End == moved.Lines.First().End);
    }

    public static void TestTransitionPortalProducesTwoDoorsAndConnector()
    {
        var overlay = LiveFeatureReviewOverlay.Make(
            transitionPortals:
            [
                new VisualRoomPortal(
                    4,
                    1,
                    0,
                    -140,
                    -120,
                    new FrameFractionRange(168, 268),
                    new FrameFractionRange(158, 278)),
            ]);
        Check(overlay.Markers.Count == 2);
        Check(overlay.Markers.All(marker =>
            marker.Kind == DashboardFeatureReviewMarkerKind.TransitionDoor));
        Check(overlay.Markers.Select(marker => marker.WorldPosition.X)
            .SequenceEqual(new double[] { -140, -120 }));
        Check(overlay.Lines.Count == 1);
        Check(overlay.Lines[0].Kind == DashboardFeatureReviewLineKind.RoomTransition);
        Near(-140, overlay.Lines[0].Start.X);
        Near(-120, overlay.Lines[0].End.X);
        Near(218, overlay.Lines[0].Start.Y);
        Near(218, overlay.Lines[0].End.Y);
    }

    public static void TestRoomBoundaryUsesThinCyanLine()
    {
        var cyan = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewLineKind.RoomBoundary);
        Check(cyan.Green > cyan.Red);
        Check(cyan.Blue > cyan.Red);
        Check(DashboardFeatureReviewGeometry.LineWidth == 1);
    }

    public static void TestGroundReviewPaletteUsesPurpleForPersistentTileAndLine()
    {
        var tile = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewMarkerKind.GroundFeature);
        var selection = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewMarkerKind.SelectedGroundFeature);
        var line = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewLineKind.PersistentGround);
        Check(tile != selection);
        Check(tile.Blue > tile.Red);
        Check(tile.Red > tile.Green);
        Check(tile.Red == line.Red);
        Check(tile.Green == line.Green);
        Check(tile.Blue == line.Blue);
    }

    public static void TestLiveGroundReviewPaletteMatchesLegend()
    {
        var green = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewMarkerKind.CameraConsistentGround);
        var orange = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewMarkerKind.OccludedGroundFeature);
        var red = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewMarkerKind.GroundFeatureDepthError);
        var currentLine = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewLineKind.CurrentGround);
        Check(green.Green > green.Red);
        Check(orange.Red > orange.Green);
        Check(red.Red > red.Green);
        Check(green.Red == currentLine.Red);
        Check(green.Green == currentLine.Green);
        Check(green.Blue == currentLine.Blue);
    }

    public static void TestFeatureMarkersRemainScreenConstantAcrossZoom()
    {
        var overlay = new DashboardFeatureReviewOverlay(
            [new DashboardFeatureReviewMarker(
                new AtlasPoint(10, 20),
                DashboardFeatureReviewMarkerKind.GroundFeature)],
            []);
        var near = DashboardFeatureReviewGeometry.MarkerQuads(
            overlay, 1, new AtlasPoint(0, 0))[0];
        var far = DashboardFeatureReviewGeometry.MarkerQuads(
            overlay, 10, new AtlasPoint(0, 0))[0];
        Check(near.Points[1].X - near.Points[0].X == 8);
        Check(far.Points[1].X - far.Points[0].X == 8);
    }

    public static void TestGroundFeatureUsesOnePixelOutlinedPurpleSquare()
    {
        var overlay = new DashboardFeatureReviewOverlay(
            [new DashboardFeatureReviewMarker(
                new AtlasPoint(10, 20),
                DashboardFeatureReviewMarkerKind.GroundFeature)],
            []);
        var quads = DashboardFeatureReviewGeometry.MarkerQuads(
            overlay, 1, new AtlasPoint(0, 0));
        Check(quads.Count == 4);
        var purple = DashboardFeatureReviewPalette.Rgba(
            DashboardFeatureReviewMarkerKind.GroundFeature);
        Check(purple.Blue > purple.Red);
        Check(purple.Red > purple.Green);
        Check(DashboardFeatureReviewGeometry.LineWidth == 1);
    }

    public static void TestPersistentGroundCellKeepsSixteenByTwelveWorldSize()
    {
        var overlay = new DashboardFeatureReviewOverlay(
            [new DashboardFeatureReviewMarker(
                new AtlasPoint(32, 48),
                DashboardFeatureReviewMarkerKind.GroundFeature,
                new WorldSize(16, 12))],
            []);
        var near = DashboardFeatureReviewGeometry.MarkerQuads(
            overlay, 1, new AtlasPoint(0, 0));
        var zoomed = DashboardFeatureReviewGeometry.MarkerQuads(
            overlay, 2, new AtlasPoint(0, 0));
        Check(near[0].Points[1].X - near[0].Points[0].X == 16);
        Check(zoomed[0].Points[1].X - zoomed[0].Points[0].X == 32);
        Check(near[2].Points[3].Y - near[2].Points[0].Y == 10);
    }

    public static void TestGroundFeatureHitTestingReturnsRegisteredCellDetails()
    {
        var details = new DashboardGroundFeatureDetails(
            12,
            4,
            new AtlasPoint(32, 48),
            Enumerable.Repeat((byte)80, GroundHypothesisTracker.FeaturePixelCount)
                .ToArray(),
            []);
        var overlay = new DashboardFeatureReviewOverlay(
            [new DashboardFeatureReviewMarker(
                details.WorldPosition,
                DashboardFeatureReviewMarkerKind.GroundFeature,
                new WorldSize(16, 12),
                details)],
            []);

        var selected = DashboardFeatureReviewGeometry.GroundFeature(
            new AtlasPoint(69, 101),
            overlay,
            2,
            new AtlasPoint(5, 5),
            new AtlasPoint(0, 0));
        Check(selected == details);
        Check(DashboardFeatureReviewGeometry.GroundFeature(
            new AtlasPoint(100, 140),
            overlay,
            2,
            new AtlasPoint(5, 5),
            new AtlasPoint(0, 0)) is null);
    }

    public static void TestInvisibleLiveGroundFeatureUsesLivePanForHitTesting()
    {
        var details = new DashboardGroundFeatureDetails(
            7,
            3,
            new AtlasPoint(400, 500),
            Enumerable.Repeat((byte)90, GroundHypothesisTracker.FeaturePixelCount)
                .ToArray(),
            []);
        var overlay = new DashboardFeatureReviewOverlay(
            [new DashboardFeatureReviewMarker(
                new AtlasPoint(20, 30),
                DashboardFeatureReviewMarkerKind.GroundFeature,
                new WorldSize(16, 12),
                details,
                DashboardFeatureReviewCoordinateSpace.Live,
                false)],
            []);

        Check(DashboardFeatureReviewGeometry.MarkerQuads(
            overlay, 2, new AtlasPoint(100, 200)).Count == 0);
        Check(DashboardFeatureReviewGeometry.GroundFeature(
                new AtlasPoint(140, 260),
                overlay,
                2,
                new AtlasPoint(-500, -500),
                new AtlasPoint(100, 200))
            == details);
    }

    public static void TestFeatureOverlayFiltersNonFiniteAndCapsDrawWork()
    {
        var finiteMarkers = Enumerable.Range(
                0, DashboardFeatureReviewGeometry.MaximumMarkers + 3)
            .Select(index => new DashboardFeatureReviewMarker(
                new AtlasPoint(index, 1),
                DashboardFeatureReviewMarkerKind.GroundFeature));
        var markers = new[]
            {
                new DashboardFeatureReviewMarker(
                    new AtlasPoint(double.PositiveInfinity, 1),
                    DashboardFeatureReviewMarkerKind.GroundFeature),
            }
            .Concat(finiteMarkers)
            .ToArray();
        var finiteLines = Enumerable.Range(
                0, DashboardFeatureReviewGeometry.MaximumLines + 3)
            .Select(index => new DashboardFeatureReviewLine(
                new AtlasPoint(index, 0),
                new AtlasPoint(index, 4),
                DashboardFeatureReviewLineKind.PersistentGround));
        var lines = new[]
            {
                new DashboardFeatureReviewLine(
                    new AtlasPoint(double.NaN, 0),
                    new AtlasPoint(1, 1),
                    DashboardFeatureReviewLineKind.PersistentGround),
            }
            .Concat(finiteLines)
            .ToArray();
        var overlay = new DashboardFeatureReviewOverlay(markers, lines);
        Check(DashboardFeatureReviewGeometry.MarkerQuads(
                overlay, 1, new AtlasPoint(0, 0)).Count
            == DashboardFeatureReviewGeometry.MaximumMarkers * 4);
        Check(DashboardFeatureReviewGeometry.LineQuads(
                overlay, 1, new AtlasPoint(0, 0)).Count
            == DashboardFeatureReviewGeometry.MaximumLines);
        Check(DashboardFeatureReviewGeometry.MarkerQuads(
            null, 1, new AtlasPoint(0, 0)).Count == 0);
    }

    public static void TestRowAssemblerStitchesFeaturesAndPreservesDeletedGap()
    {
        var features = new[]
        {
            GroundViewerFeature(2, 20),
            GroundViewerFeature(3, 30),
            GroundViewerFeature(5, 50),
        };
        var slots = GroundViewerRowAssembler.Slots(features, 7);

        Check(slots.Select(slot => slot.SequenceIndex).SequenceEqual([2, 3, 4, 5]));
        Check(slots[2].Feature is null);
        var pixels = GroundViewerRowAssembler.StitchedPixels(slots);
        var width = 4 * GroundHypothesisTracker.FeatureSize;
        Check(pixels[0] == 20);
        Check(pixels[GroundHypothesisTracker.FeatureSize] == 30);
        Check(pixels[2 * GroundHypothesisTracker.FeatureSize] == 0);
        Check(pixels[3 * GroundHypothesisTracker.FeatureSize] == 50);
        Check(pixels.Count == width * GroundHypothesisTracker.FeatureHeight);
        Check(GroundViewerRowAssembler.NearestFeature(2, slots)?.SequenceIndex == 3);
    }

    public static void TestStitchedViewerCompositesPerPixelOpacityOverBlack()
    {
        var transparent = GroundViewerFeature(0, 100, 0);
        var partial = GroundViewerFeature(1, 100, 128);
        var opaque = GroundViewerFeature(2, 100, 255);
        var pixels = GroundViewerRowAssembler.StitchedPixels(
        [
            new GroundViewerSlot(0, transparent),
            new GroundViewerSlot(1, partial),
            new GroundViewerSlot(2, opaque),
        ]);

        Check(pixels[0] == 0);
        Check(pixels[16] == 50);
        Check(pixels[32] == 100);
    }

    public static void TestLiveRowStateAddsNewFeaturesAndRefreshesSelectedEvidence()
    {
        var selected = GroundViewerFeature(2, 20, 32);
        var state = GroundViewerRowState.Refreshed(
            selected,
        [
            GroundViewerFeature(0, 10),
            GroundViewerFeature(1, 15),
            GroundViewerFeature(2, 90, 180),
            GroundViewerFeature(3, 30),
        ]);

        Check(state.Slots.Select(slot => slot.SequenceIndex).SequenceEqual([0, 1, 2, 3]));
        Check(state.Selected.ReferencePixels.First() == 90);
        Check(state.Selected.ReferenceOpacity.First() == 180);
    }

    public static void TestCountsPaintedPixelsNotTilePaddingAndAllowsVerticalGrowth()
    {
        var image = OpaqueWhiteFrame(16, 12);
        var atlas = new LiveTiledAtlas(anchorPosition: new AtlasPoint(0, 0));
        Check(atlas.Insert(0, image, 16, new AtlasPoint(0, 0)));
        Check(atlas.Insert(1, image, 16, new AtlasPoint(0, 30)));
        var clean = GroundAtlasFootprint.Measure(atlas.Snapshot, 0, 16);
        Check(clean.MinimumX == 0);
        Check(clean.MaximumX == 16);
        Check(clean.LeftPixels == 0);
        Check(clean.RightPixels == 0);
        Check(clean.PaintedPixels == 384);

        Check(atlas.Insert(2, image, 16, new AtlasPoint(-4, 0)));
        Check(atlas.Insert(3, image, 16, new AtlasPoint(5, 0)));
        var drift = GroundAtlasFootprint.Measure(atlas.Snapshot, 0, 16);
        Check(drift.MinimumX == -4);
        Check(drift.MaximumX == 21);
        Check(drift.LeftPixels == 48);
        Check(drift.RightPixels == 60);
    }

    private static GroundHypothesisAtlasFeature GroundViewerFeature(
        int sequence,
        byte value,
        byte? opacity = null) =>
        new(
            7,
            sequence,
            new AtlasPoint(sequence * GroundHypothesisTracker.FeatureWidth, 40),
            Enumerable.Repeat(value, GroundHypothesisTracker.FeaturePixelCount).ToArray(),
            opacity is { } alpha
                ? Enumerable.Repeat(alpha, GroundHypothesisTracker.FeaturePixelCount).ToArray()
                : []);

    private static BgraFrame OpaqueWhiteFrame(int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)255, checked(width * height * 4)).ToArray();
        return new BgraFrame(
            width,
            height,
            checked(width * BgraFrame.BytesPerPixel),
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    public static void TestTileUUIDRetainsHighBitsOfTileID()
    {
        const long low = 1;
        var high = unchecked((long)((1UL << 48) | (ulong)low));
        var image = new BgraFrame(
            1, 1, 4, new byte[4], 1, DateTimeOffset.UnixEpoch);
        HackerAtlasLayer Layer(long id) => new(
            id,
            image,
            new HackerAtlasRect(0, 0, 1, 1),
            0,
            null);

        Check(DashboardTileIdentity.For(Layer(low))
            != DashboardTileIdentity.For(Layer(high)));
        Check(DashboardTileIdentity.For(Layer(-1))
            != DashboardTileIdentity.For(Layer(low)));
    }
}

file static class GroundHypothesisLocalParityTests
{
    private static void Check(
        bool value,
        string message = "Ground hypothesis local parity assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, double tolerance = .000_001) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");

    public static void TestFeatureOverlayDoesNotDrawASecondLiveGroundLine()
    {
        var image = GroundHypothesisTracker.OverlayImage(
            GroundHypothesisTrackingResult.Empty, 20, 12)
            ?? throw new InvalidOperationException("Expected overlay image.");
        Check(image.Rgba.All(value => value == 0));
    }

    public static void TestFeatureOverlayDoesNotPaintPersistentAtlasLineIntoLiveRaster()
    {
        var tracking = GroundHypothesisTrackingResult.Empty with
        {
            GroundSegmentCount = 1,
            AtlasLines =
            [
                new GroundHypothesisAtlasLine(
                    1,
                    new AtlasPoint(2, 6),
                    new AtlasPoint(17, 6))
            ],
        };
        var image = GroundHypothesisTracker.OverlayImage(tracking, 20, 12)
            ?? throw new InvalidOperationException("Expected overlay image.");
        Check(image.Rgba.All(value => value == 0));
    }

    public static void TestFeatureOverlayDrawsRectangularGroundCell()
    {
        var feature = new GroundHypothesisFeature(
            1,
            1,
            0,
            new PixelRect(10, 4, 16, 12),
            GroundHypothesisClassification.Candidate,
            null,
            null);
        var tracking = GroundHypothesisTrackingResult.Empty with
        {
            Features = [feature],
            GroundSegmentCount = 1,
        };
        const int width = 32;
        var image = GroundHypothesisTracker.OverlayImage(tracking, width, 24)
            ?? throw new InvalidOperationException("Expected overlay image.");
        byte[] Pixel(int x, int y) => image.Rgba.AsSpan((y * width + x) * 4, 4).ToArray();
        Check(Pixel(10, 4).SequenceEqual(new byte[] { 0, 200, 255, 255 }));
        Check(Pixel(10, 15).SequenceEqual(new byte[] { 0, 200, 255, 255 }));
        Check(Pixel(8, 4).SequenceEqual(new byte[] { 0, 0, 0, 0 }));
    }

    public static void TestRANSACSelectsDominantGroundMotionAndRejectsDepthOutliers()
    {
        var ground = Enumerable.Range(0, 20).Select(index => new GroundFeatureMotion(
            index,
            new CameraVector(
                3 + (index % 3 - 1) * .2,
                -2 + index % 2 * .2),
            4));
        var otherDepth = Enumerable.Range(20, 8).Select(index => new GroundFeatureMotion(
            index,
            new CameraVector(-6, 7),
            4));
        var fit = GroundTranslationRansac.Fit([.. ground, .. otherDepth])
            ?? throw new InvalidOperationException("Expected dominant translation fit.");
        Near(3, fit.ImageTranslation.X, .1);
        Near(-1.9, fit.ImageTranslation.Y, .1);
        Check(fit.InlierIds.Count == 20);
        Check(!fit.InlierIds.Overlaps(Enumerable.Range(20, 8)));
        Check(fit.ResidualRms < .3);
    }

    public static void TestTrackerTilesEverySixteenPixelsAndSolvesFrameTranslation()
    {
        const int width = 640;
        const int height = 120;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var seeded = tracker.Update(
            Texture(width, height, 0, 0), width, height,
            [Line(40, 40, 600)]);
        Check(seeded.Features.Count == 35);
        Check(seeded.Features.Select(feature => feature.ImageRect.X)
            .SequenceEqual(Enumerable.Range(0, 35).Select(index => 40 + index * 16)));
        Check(seeded.Features.All(feature =>
            feature.ImageRect.Width == 16
            && feature.ImageRect.Height == 12
            && feature.ImageRect.Y == 40));
        AssertNoHorizontalOverlap(seeded.Features);

        var tracked = tracker.Update(
            Texture(width, height, -4, 2), width, height,
            [Line(42, 36, 596)]);
        var camera = tracked.CameraTranslation
            ?? throw new InvalidOperationException("Expected camera translation.");
        Near(4, camera.X, .5);
        Near(2, camera.Y, .5);
        Check(tracked.InlierCount >= 24);
        Check(tracked.Features.Count(feature =>
            feature.Classification == GroundHypothesisClassification.GroundPlane) >= 24);
        Check(tracked.Features.All(feature =>
            feature.ImageRect.Y == 42
            && feature.ImageRect.X >= 36
            && feature.ImageRect.Right - 1 <= 596));
        AssertNoHorizontalOverlap(tracked.Features);
        Check(tracker.LatestStabilizedFloorLines.Select(line => line.Row)
            .SequenceEqual(new[] { 42 }));
    }

    public static void TestTrackedTextureStabilizesOnePixelDetectorRowJitter()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(160, 100, 0, 0);
        _ = tracker.Update(frame, 160, 100, [Line(40, 0, 159)]);
        var stabilized = tracker.Update(frame, 160, 100, [Line(41, 0, 159)]);

        Near(0, stabilized.CameraTranslation?.Y
            ?? throw new InvalidOperationException("Expected camera translation."));
        Check(tracker.LatestStabilizedFloorLines.Select(line => line.Row)
            .SequenceEqual(new[] { 40 }));
        Check(stabilized.Features.All(feature => feature.ImageRect.Y == 40));
    }

    public static void TestLifecycleAddsUnlimitedTilesToFillNewlyVisibleScreenArea()
    {
        const int width = 640;
        const int height = 120;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var first = tracker.Update(
            Texture(width, height, 0, 0), width, height,
            [Line(40, 40, 420)]);
        var firstIds = first.Features.Select(feature => feature.Id).ToHashSet();
        Check(first.Features.Count == 23);

        var moved = tracker.Update(
            Texture(width, height, -12, 0), width, height,
            [Line(40, 28, 620)], timestamp: .1);
        var newFeatures = moved.Features.Where(feature => !firstIds.Contains(feature.Id)).ToArray();
        Check(moved.Features.Count == 37);
        Check(newFeatures.Length > 0);
        Check(newFeatures.Any(feature => feature.ImageRect.X > 500));
        Check(moved.Features.All(feature =>
            feature.ImageRect.Y == 40
            && feature.ImageRect.X >= 28
            && feature.ImageRect.Right - 1 <= 620));
        AssertNoHorizontalOverlap(moved.Features);
    }

    public static void TestEveryCleanLineIsFullyTiledWithoutThirtyTwoFeatureLimit()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var result = tracker.Update(
            Texture(640, 140, 0, 0), 640, 140,
            [Line(24, 0, 639), Line(80, 0, 639)]);

        Check(result.Features.Count == 80);
        Check(result.Features.Count(feature => feature.ImageRect.Y == 24) == 40);
        Check(result.Features.Count(feature => feature.ImageRect.Y == 80) == 40);
        AssertNoHorizontalOverlap(result.Features);
    }

    public static void TestPermanentSegmentDoesNotRephaseWhenDetectedEndpointJitters()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(320, 100, 0, 0);
        var first = tracker.Update(frame, 320, 100, [Line(40, 40, 200)]);
        var firstPositions = first.Features.ToDictionary(feature => feature.Id,
            feature => feature.ImageRect.X);

        var jittered = tracker.Update(frame, 320, 100, [Line(40, 42, 202)]);
        Check(jittered.GroundSegmentCount == 1);
        Check(jittered.Features.First().ImageRect.X == 40);
        foreach (var feature in jittered.Features)
            if (firstPositions.TryGetValue(feature.Id, out var priorX))
                Check(feature.ImageRect.X == priorX);
    }

    public static void TestPermanentSequenceKeepsOrangeCellsAcrossOcclusion()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var result = tracker.Update(
            Texture(160, 100, 0, 0), 160, 100,
            [Line(40, 0, 159, new PixelSpan(0, 47), new PixelSpan(112, 159))]);

        Check(result.GroundSegmentCount == 1);
        Check(result.Features.Count == 10);
        Check(result.Features.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 10)));
        Check(result.Features.Where(feature =>
                feature.Classification == GroundHypothesisClassification.Occluded)
            .Select(feature => feature.SequenceIndex)
            .SequenceEqual(new[] { 3, 4, 5, 6 }));
    }

    public static void TestInteriorOccludedCellsRemainInPersistentLineLattice()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(160, 100, 0, 0);
        var merged = Line(40, 0, 159,
            new PixelSpan(0, 47), new PixelSpan(112, 159));

        var result = tracker.Update(frame, 160, 100, [merged]);
        Check(result.Features.Where(feature =>
                feature.Classification == GroundHypothesisClassification.Occluded)
            .Select(feature => feature.SequenceIndex)
            .SequenceEqual(new[] { 3, 4, 5, 6 }));
        for (var index = 1; index < 5; index++)
            result = tracker.Update(frame, 160, 100, [merged]);

        Check(result.Features.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 10)));
        Check(result.AtlasFeatures.Any(feature => feature.SequenceIndex is >= 3 and <= 6));
    }

    public static void TestKnightCoveredCellsAreUnobservedInsteadOfRejected()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .1);
        var frame = Texture(160, 100, 0, 0);
        var fullLine = new[] { Line(40, 0, 159) };
        for (var index = 0; index < 20; index++)
            _ = tracker.Update(frame, 160, 100, fullLine);

        var knight = new PixelRect(48, 40, 64, 32);
        var hidden = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 5; index++)
            hidden = tracker.Update(frame, 160, 100, [], protectedOcclusions: [knight]);

        Check(hidden.Features.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 10)));
        Check(hidden.Features.All(feature =>
            feature.Classification == GroundHypothesisClassification.Occluded));
        Check(hidden.AtlasFeatures.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 10)));

        var visibleAgain = tracker.Update(frame, 160, 100, fullLine);
        Check(visibleAgain.Features.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 10)));
        Check(visibleAgain.Features.All(feature =>
            feature.Classification != GroundHypothesisClassification.Occluded));
    }

    public static void TestUnverifiedFeaturelessFrameCannotOverwriteAtlasPatches()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var line = new[] { Line(40, 0, 159) };
        var first = tracker.Update(Solid(160, 100, 40), 160, 100, line);
        Check(first.AtlasFeatures.All(feature =>
            feature.ReferencePixels.Count == GroundHypothesisTracker.FeaturePixelCount
            && feature.ReferencePixels.All(pixel => pixel == 40)));

        var refreshed = tracker.Update(Solid(160, 100, 90), 160, 100, line);
        Check(refreshed.AtlasFeatures.All(feature =>
            feature.ReferencePixels.Count == GroundHypothesisTracker.FeaturePixelCount
            && feature.ReferencePixels.All(pixel => pixel == 40)));
        Check(!refreshed.PoseVerified);
        Check(refreshed.AtlasFeatures.Count == first.AtlasFeatures.Count);
        for (var index = 0; index < first.AtlasFeatures.Count; index++)
            Check(refreshed.AtlasFeatures[index].ReferenceOpacity.SequenceEqual(
                first.AtlasFeatures[index].ReferenceOpacity));
    }

    public static void TestCameraConsistentPixelsGainOpacity()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(160, 100, 0, 0);
        var line = new[] { Line(40, 0, 159) };
        var first = tracker.Update(frame, 160, 100, line);
        var second = tracker.Update(frame, 160, 100, line);
        Check(OpacityMean(second.AtlasFeatures) > OpacityMean(first.AtlasFeatures));
    }

    public static void TestEntireUnsupportedLineExpiresTogether()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(160, 100, 0, 0);
        _ = tracker.Update(frame, 160, 100, [Line(40, 0, 159)]);
        var result = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 5; index++)
            result = tracker.Update(frame, 160, 100, []);
        Check(result.GroundSegmentCount == 0);
        Check(result.Features.Count == 0);
        Check(result.AtlasFeatures.Count == 0);
    }

    public static void TestMergingSegmentsRebuildsOneOrderedSequence()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(320, 100, 0, 0);
        var separate = tracker.Update(frame, 320, 100,
            [Line(40, 0, 79), Line(40, 200, 279)], occlusionMergeGap: 50);
        Check(separate.GroundSegmentCount == 2);

        var merged = tracker.Update(frame, 320, 100,
            [Line(40, 0, 279)], occlusionMergeGap: 300);
        Check(merged.GroundSegmentCount == 1);
        Check(merged.Features.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 17)));
        AssertNoHorizontalOverlap(merged.Features);
    }

    public static void TestMatureGlobalFeaturesReindexWhenSegmentsMerge()
    {
        const int width = 320;
        const int height = 100;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(width, height, 0, 0);
        var separateLines = new[] { Line(40, 0, 79), Line(40, 200, 279) };
        var learned = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 3; index++)
            learned = tracker.Update(frame, width, height, separateLines,
                occlusionMergeGap: 50, timestamp: index / 60d);
        Check(learned.GroundSegmentCount == 2);
        Check(learned.GlobalFeatureCount >= 8);

        var merged = tracker.Update(frame, width, height, [Line(40, 0, 279)],
            occlusionMergeGap: 300, timestamp: .1);
        Check(merged.GroundSegmentCount == 1);
        Check(merged.GlobalFeatureCount == learned.GlobalFeatureCount);

        tracker.Reset(keepingGlobalFeatures: true);
        var returned = tracker.Update(frame, width, height, [Line(40, 0, 279)],
            occlusionMergeGap: 300, timestamp: .5);
        Check(returned.GlobalMatchCount == learned.GlobalFeatureCount);
        Check(returned.Features.Count(feature =>
            feature.Classification == GroundHypothesisClassification.GlobalMatch)
            == learned.GlobalFeatureCount);
        Check(returned.Features.Where(feature =>
                feature.Classification == GroundHypothesisClassification.GlobalMatch)
            .Select(feature => feature.SegmentId).Distinct().Count() == 1);

        // Two independently mature rows can project onto the same horizontal
        // lattice when a later observation associates both with one segment.
        // The retained segment owns each cell exactly once; persistent globals
        // attached to the discarded duplicates must leave the searchable set.
        var collisionTracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var collisionLines = new[] { Line(40, 0, 127), Line(45, 16, 159) };
        var collisionLearned = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 3; index++)
            collisionLearned = collisionTracker.Update(
                frame, width, height, collisionLines,
                occlusionMergeGap: -1, timestamp: 1 + index / 60d);
        Check(collisionLearned.GroundSegmentCount == 6,
            $"Expected six independently admitted overlapping segments, got "
            + $"{collisionLearned.GroundSegmentCount}.");
        Check(collisionLearned.GlobalFeatureCount == 17,
            $"Expected 17 mature globals before collision merge, got "
            + $"{collisionLearned.GlobalFeatureCount}.");

        var collisionMerged = collisionTracker.Update(
            frame, width, height, collisionLines,
            occlusionMergeGap: 300, timestamp: 1.1);
        Check(collisionMerged.GroundSegmentCount == 1,
            $"Expected one collision-merged segment, got {collisionMerged.GroundSegmentCount}; "
            + $"verified={collisionMerged.PoseVerified}, globals={collisionMerged.GlobalFeatureCount}, "
            + $"rows={string.Join(',', collisionTracker.LatestStabilizedFloorLines.Select(item => item.Row))}, "
            + $"reviews={string.Join(';', collisionMerged.LineReviews.Select(item => $"{item.Line.Row}:{item.State}"))}.");
        Check(collisionMerged.AtlasFeatures.Count == 10,
            $"Expected ten unique collision-merged cells, got {collisionMerged.AtlasFeatures.Count}.");
        Check(collisionMerged.GlobalFeatureCount == 10,
            $"Expected seven orphan globals to drop during collision merge, got "
            + $"{collisionMerged.GlobalFeatureCount} globals.");
    }

    public static void TestDisconnectedSegmentsShareGlobalPhaseThenMergeIntoOneRectifiedGrid()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(320, 100, 0, 0);
        var separate = tracker.Update(frame, 320, 100,
            [Line(40, 3, 82), Line(40, 202, 281)], occlusionMergeGap: 50);
        Check(separate.GroundSegmentCount == 2);
        Check(separate.Features.Select(feature => feature.ImageRect.X)
            .SequenceEqual(new[] { 3, 19, 35, 51, 67, 211, 227, 243, 259 }));
        Check(separate.Features.All(feature => (feature.ImageRect.X - 3) % 16 == 0));

        var merged = tracker.Update(frame, 320, 100,
            [Line(40, 3, 281)], occlusionMergeGap: 300);
        Check(merged.GroundSegmentCount == 1);
        Check(merged.Features.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 17)));
        Check(merged.Features.Select(feature => feature.ImageRect.X)
            .SequenceEqual(Enumerable.Range(0, 17).Select(index => 3 + index * 16)));
        AssertNoHorizontalOverlap(merged.Features);
    }

    public static void TestFeaturelessTilesNeverEnterGlobalSet()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Solid(320, 100, 90);
        var result = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 5; index++)
            result = tracker.Update(frame, 320, 100, [Line(40, 0, 319)]);
        Check(result.Features.Count == 20);
        Check(result.GlobalFeatureCount == 0);
        Check(!result.Features.Any(feature =>
            feature.Classification == GroundHypothesisClassification.GlobalMatch));
    }

    public static void TestReturnViewReacquiresPersistentFeaturesAndCorrectsGlobalPose()
    {
        const int width = 320;
        const int height = 100;
        var line = new[] { Line(40, 0, 319) };
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(width, height, 0, 0);
        var learned = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 3; index++)
            learned = tracker.Update(origin, width, height, line);
        Check(learned.GlobalFeatureCount >= 16);
        Check(learned.AtlasLines.Count > 0, "Expected learned atlas line.");
        var originalLineId = learned.AtlasLines[0].SegmentId;

        tracker.Reset(keepingGlobalFeatures: true);
        var right = tracker.Update(Texture(width, height, -64, 0), width, height, line);
        Check(right.GlobalMatchCount >= 8);
        Near(64, right.GlobalCameraPosition?.X
            ?? throw new InvalidOperationException("Expected global camera position."), .5);

        var returned = tracker.Update(origin, width, height, line, timestamp: .5);
        Check(returned.GlobalMatchCount >= 8);
        Near(0, returned.GlobalCameraPosition?.X
            ?? throw new InvalidOperationException("Expected returned global pose."), .5);
        Near(0, returned.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected returned camera pose."), 1);
        Check(returned.Features.Count(feature =>
            feature.Classification == GroundHypothesisClassification.GlobalMatch) >= 8);
        Check(returned.AtlasFeatures.Count >= 8);
        Check(returned.AtlasLines.Count > 0);
        Check(returned.GroundSegmentCount == 1);
        Check(returned.AtlasLines.All(lineResult => lineResult.SegmentId == originalLineId));
    }

    public static void TestBackgroundRecoveryConfirmsReturnAndSurvivesReset()
    {
        var tracker = new GroundHypothesisTracker(
            minimumObservationSeconds: 0, backgroundGlobalSearch: true);
        var origin = Texture(320, 100, 0, 0);
        var line = new[] { Line(40, 0, 319) };
        for (var index = 0; index < 5; index++)
            _ = tracker.Update(origin, 320, 100, line, timestamp: index / 60d);
        tracker.Reset(keepingGlobalFeatures: true);
        var shifted = Texture(320, 100, -64, 0);
        var recovered = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 80; index++)
        {
            recovered = tracker.Update(
                shifted, 320, 100, line, timestamp: 1 + index * .025);
            if (recovered.GlobalMatchCount >= 6) break;
            Thread.Sleep(10);
        }
        Check(recovered.GlobalMatchCount >= 6);
        Near(64, recovered.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected recovered camera pose."), 1);
        tracker.Reset();
        for (var index = 0; index < 5; index++)
        {
            var result = tracker.Update(
                origin, 320, 100, line, timestamp: 5 + index * .025);
            Near(0, result.CameraPosition?.X
                ?? throw new InvalidOperationException("Expected reset camera pose."), 1);
            Thread.Sleep(10);
        }
    }

    public static void TestBackgroundRecoverySurvivesRepeatedFloorlessReanchors()
    {
        var tracker = new GroundHypothesisTracker(
            minimumObservationSeconds: 0, backgroundGlobalSearch: true);
        var origin = Texture(320, 100, 0, 0);
        var line = new[] { Line(40, 0, 319) };
        for (var index = 0; index < 5; index++)
            _ = tracker.Update(origin, 320, 100, line, timestamp: index / 60d);
        var fallback = new AtlasPoint(96, 0);
        tracker.ReanchorLocalCamera(fallback);
        var recovered = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 120; index++)
        {
            var timestamp = 1 + index * .025;
            recovered = tracker.Update(origin, 320, 100, line, timestamp: timestamp);
            if (recovered.GlobalMatchCount >= 6
                && Math.Abs(recovered.CameraPosition?.X ?? double.PositiveInfinity) <= 1)
                break;
            tracker.ReanchorLocalCamera(
                fallback, origin, 320, 100, line, timestamp);
            Thread.Sleep(10);
        }
        Check(recovered.GlobalMatchCount >= 6);
        Near(0, recovered.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected recovered camera pose."), 1);
    }

    public static void TestDelayedBackgroundRecoveryFollowsContinuousCameraMotion()
    {
        const int width = 320;
        const int height = 100;
        using var searchEntered = new SemaphoreSlim(0);
        using var releaseSearch = new SemaphoreSlim(0);
        var gateArmed = 0;
        var completedSearches = new System.Collections.Concurrent.ConcurrentQueue<AtlasPoint?>();
        Action beforeBackgroundSearch = () =>
        {
            if (Volatile.Read(ref gateArmed) == 0) return;
            searchEntered.Release();
            if (!releaseSearch.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out releasing background ground search.");
        };
        var constructor = typeof(GroundHypothesisTracker).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(double), typeof(bool), typeof(double), typeof(Action), typeof(Action<AtlasPoint?>)],
            modifiers: null)
            ?? throw new InvalidOperationException("Missing deterministic background-search constructor.");
        var tracker = (GroundHypothesisTracker)constructor.Invoke(
            [0d, true, 0d, beforeBackgroundSearch,
                (Action<AtlasPoint?>)(position =>
                {
                    completedSearches.Enqueue(position);
                })]);
        var workerField = typeof(GroundHypothesisTracker).GetField(
            "globalSearchWorker", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing production background-search worker.");
        var worker = workerField.GetValue(tracker)
            ?? throw new InvalidOperationException("Missing production background-search state.");
        var workerType = worker.GetType();
        var workerSync = workerType.GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(worker)
            ?? throw new InvalidOperationException("Missing background-search publication lock.");
        var workerHasCompleted = workerType.GetField(
            "hasCompleted", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing background-search readiness state.");
        bool BackgroundResultIsPublished()
        {
            lock (workerSync)
                return (bool)(workerHasCompleted.GetValue(worker) ?? false);
        }
        var line = new[] { Line(40, 0, width - 1) };
        var origin = Texture(width, height, 0, 0);
        var trained = GroundHypothesisTrackingResult.Empty;
        // Promotion occurs after the third update. No background search can
        // start before that promotion, so reset begins with an idle worker.
        for (var index = 0; index < 3; index++)
            trained = tracker.Update(origin, width, height, line, timestamp: index / 60d);
        Check(trained.GlobalFeatureCount >= 6,
            "Training did not produce enough persistent features for recovery.");
        tracker.Reset(keepingGlobalFeatures: true);

        // Keep every recovery observation inside the originally promoted
        // world span. This exercises transport while the camera moves without
        // admitting new, incorrectly based cells into the persistent bank.
        var searchLine = new[] { Line(40, 0, width - 81) };
        var shifted64 = Texture(width, height, -64, 0);
        tracker.ReanchorLocalCamera(new AtlasPoint(72, 0), shifted64,
            width, height, searchLine, timestamp: .9);
        Volatile.Write(ref gateArmed, 1);
        _ = tracker.Update(shifted64, width, height, searchLine, timestamp: 1);
        Check(searchEntered.Wait(TimeSpan.FromSeconds(5)),
            "First background search did not enter its deterministic barrier.");

        var shifted68 = Texture(width, height, -68, 0);
        var moved = tracker.Update(shifted68, width, height, searchLine, timestamp: 1.1);
        Near(76, moved.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected moving local camera."), 1);
        releaseSearch.Release();
        Check(SpinWait.SpinUntil(BackgroundResultIsPublished, TimeSpan.FromSeconds(5)),
            "First background search result was not published by the production worker.");
        var shifted72 = Texture(width, height, -72, 0);
        // A real odometry handoff clears the abandoned local feature epoch
        // before the delayed result is consumed. Confirmation intentionally
        // survives this public reanchor, and the stored global bank stays
        // uncontaminated for the independent retry.
        tracker.ReanchorLocalCamera(new AtlasPoint(80, 0), shifted72,
            width, height, searchLine, timestamp: 1.19);
        var withheld = tracker.Update(shifted72, width, height, searchLine, timestamp: 1.2);
        Check(withheld.GlobalMatchCount == 0,
            "One background observation bypassed correction confirmation.");
        Near(80, withheld.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected uncorrected moving local camera."), 1);
        Check(completedSearches.Count == 1 && !BackgroundResultIsPublished(),
            "The first production result was not consumed before confirmation retry.");

        _ = tracker.Update(shifted72, width, height, searchLine, timestamp: 1.3);
        Check(searchEntered.Wait(TimeSpan.FromSeconds(5)),
            "Second background search did not enter its deterministic barrier.");
        Check(completedSearches.Count == 1 && !BackgroundResultIsPublished(),
            "Second search was not independently blocked before publication.");
        var shifted76 = Texture(width, height, -76, 0);
        tracker.ReanchorLocalCamera(new AtlasPoint(84, 0), shifted76,
            width, height, searchLine, timestamp: 1.39);
        var movedAgain = tracker.Update(shifted76, width, height, searchLine, timestamp: 1.4);
        Near(84, movedAgain.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected second moving local camera."), 1);
        releaseSearch.Release();
        Volatile.Write(ref gateArmed, 0);
        Check(SpinWait.SpinUntil(BackgroundResultIsPublished, TimeSpan.FromSeconds(5)),
            "Second background search result was not published by the production worker.");
        var shifted80 = Texture(width, height, -80, 0);
        var recovered = tracker.Update(shifted80, width, height, searchLine, timestamp: 1.5);
        Check(recovered.GlobalMatchCount >= 6,
            $"Expected independently confirmed transported background match; "
            + $"camera={recovered.CameraPosition}, "
            + $"global={recovered.GlobalCameraPosition}, correction={recovered.GlobalCorrection}, "
            + $"globalCounts={trained.GlobalFeatureCount}/{moved.GlobalFeatureCount}/"
            + $"{withheld.GlobalFeatureCount}/{movedAgain.GlobalFeatureCount}/"
            + $"{recovered.GlobalFeatureCount}, "
            + $"searches={string.Join(';', completedSearches)}.");
        Check(completedSearches.Count == 2,
            $"Expected two completed production searches, got {completedSearches.Count}.");
        Near(80, recovered.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected transported global pose."), 1);
    }

    public static void TestReturnSearchUsesVisibleTextureBelowFragmentedFloorEdge()
    {
        const int width = 320;
        const int height = 100;
        var origin = Texture(width, height, 0, 0);
        var gaps = Enumerable.Range(0, (width - 8 + 11) / 12)
            .Select(index => index * 12)
            .Where(start => start < width - 8)
            .Select(start => new PixelSpan(start, start + 7))
            .ToArray();
        foreach (var isKnownPlace in new[] { true, false })
        {
            var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
            for (var index = 0; index < 3; index++)
                _ = tracker.Update(origin, width, height, [Line(40, 0, 319)],
                    timestamp: index / 60d);
            tracker.Reset(keepingGlobalFeatures: true);
            var result = tracker.Update(
                Texture(width, height, isKnownPlace ? -62 : -5000, 5),
                width, height, [Line(45, 0, 319, gaps)], timestamp: .5);
            if (isKnownPlace)
            {
                Check(result.GlobalMatchCount >= 6);
                Near(62, result.GlobalCameraPosition?.X
                    ?? throw new InvalidOperationException("Expected known global pose."), .5);
                Near(5, result.GlobalCameraPosition?.Y
                    ?? throw new InvalidOperationException("Expected known global pose."), .5);
            }
            else Check(result.GlobalMatchCount == 0,
                "A continuous floor span must not turn unrelated texture into a closure.");
        }
    }

    public static void TestGlobalReturnSearchIsIndependentOfSixteenPixelLatticePhase()
    {
        const int width = 320;
        const int height = 100;
        var line = new[] { Line(40, 0, 319) };
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(width, height, 0, 0);
        for (var index = 0; index < 3; index++)
            _ = tracker.Update(origin, width, height, line, timestamp: index / 60d);
        tracker.Reset(keepingGlobalFeatures: true);
        var shifted = tracker.Update(Texture(width, height, -62, 5), width, height,
            [Line(45, 0, 319)], timestamp: .5);
        Check(shifted.GlobalMatchCount >= 6);
        Near(62, shifted.GlobalCameraPosition?.X
            ?? throw new InvalidOperationException("Expected global pose."), .5);
        Near(5, shifted.GlobalCameraPosition?.Y
            ?? throw new InvalidOperationException("Expected global pose."), .5);
        Near(62, shifted.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected camera pose."), .5);
        Near(5, shifted.CameraPosition?.Y
            ?? throw new InvalidOperationException("Expected camera pose."), .5);
    }

    public static void TestGlobalReturnSearchHandlesScreenSpaceToneChange()
    {
        const int width = 320;
        const int height = 100;
        var line = new[] { Line(40, 0, 319) };
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(width, height, 0, 0);
        for (var index = 0; index < 3; index++)
            _ = tracker.Update(origin, width, height, line, timestamp: index / 60d);
        tracker.Reset(keepingGlobalFeatures: true);
        var shifted = tracker.Update(Texture(width, height, -62, 5, .6, 50),
            width, height, [Line(45, 0, 319)], timestamp: .5);
        Check(shifted.GlobalMatchCount >= 6);
        Near(62, shifted.GlobalCameraPosition?.X
            ?? throw new InvalidOperationException("Expected tone-robust global pose."), .5);
        Near(5, shifted.GlobalCameraPosition?.Y
            ?? throw new InvalidOperationException("Expected tone-robust global pose."), .5);
    }

    public static void TestOneFrameExtensionDoesNotGrowPersistentSequence()
    {
        var tracker = new GroundHypothesisTracker();
        var frame = Texture(240, 100, 0, 0);
        var baseline = new[] { Line(40, 40, 119) };
        for (var index = 0; index <= 120; index++)
            _ = tracker.Update(frame, 240, 100, baseline);

        var transient = tracker.Update(frame, 240, 100, [Line(40, 40, 199)]);
        _ = tracker.Update(frame, 240, 100, baseline);
        var expired = tracker.Update(frame, 240, 100, baseline);

        Check(transient.Features.Count == 5);
        Check(expired.Features.Count == 5);
        Check(expired.Features.Select(feature => feature.ImageRect.X)
            .SequenceEqual(new[] { 40, 56, 72, 88, 104 }));
    }

    public static void TestPartiallyVisibleLineKeepsCompleteLattice()
    {
        var tracker = new GroundHypothesisTracker();
        var frame = Texture(160, 100, 0, 0);
        var longLine = new[] { Line(40, 0, 159) };
        for (var index = 0; index <= 120; index++)
            _ = tracker.Update(frame, 160, 100, longLine);

        var shortLine = new[] { Line(40, 0, 79) };
        var result = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 8; index++)
            result = tracker.Update(frame, 160, 100, shortLine);

        Check(result.Features.Count == 10);
        Check(result.Features.Select(feature => feature.SequenceIndex)
            .SequenceEqual(Enumerable.Range(0, 10)));
        Check(result.GlobalFeatureCount >= 5);
    }

    public static void TestRejectedLineRemovesItsEntirePersistentSequence()
    {
        const int width = 320;
        const int height = 100;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var line = new[] { Line(40, 0, width - 1) };
        for (var shift = 0; shift >= -96; shift -= 12)
        {
            _ = tracker.Update(
                Texture(width, height, shift, 0), width, height, line,
                timestamp: -shift / 120d);
        }

        var lastFrame = Texture(width, height, -96, 0);
        var cleaned = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 120; index++)
            cleaned = tracker.Update(lastFrame, width, height, []);

        Check(cleaned.Features.Count == 0);
        Check(cleaned.AtlasFeatures.Count == 0,
            "Rejected line cannot keep registered tiles.");
        Check(cleaned.AtlasLines.Count == 0);
    }

    public static void TestPersistentLineImmediatelyCorrectsVerticalCameraPose()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Texture(160, 100, 0, 0);
        _ = tracker.Update(frame, 160, 100, [Line(40, 0, 159)]);

        var shifted = Texture(160, 100, 0, 5);
        var corrected = tracker.Update(shifted, 160, 100, [Line(45, 0, 159)]);
        Near(5, corrected.CameraTranslation?.Y
            ?? throw new InvalidOperationException("Expected vertical correction."), .001);
        Check(corrected.Features.All(feature => feature.ImageRect.Y == 45));

        var stable = tracker.Update(shifted, 160, 100, [Line(45, 0, 159)]);
        Near(0, stable.CameraTranslation?.Y
            ?? throw new InvalidOperationException("Expected stable translation."), .001);
    }

    public static void TestPersistentAtlasLineDoesNotMoveDuringVerticalCameraRoundTrip()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var originFrame = Texture(160, 100, 0, 0);
        var origin = tracker.Update(
            originFrame, 160, 100, [Line(40, 0, 159)]);
        var originalLines = origin.AtlasLines.ToArray();
        var originalPositions = origin.AtlasFeatures
            .Select(feature => feature.AtlasPosition).ToArray();

        var down = tracker.Update(
            Texture(160, 100, 0, 8), 160, 100, [Line(48, 0, 159)]);
        Check(down.AtlasLines.SequenceEqual(originalLines));
        Check(down.AtlasFeatures.Select(feature => feature.AtlasPosition)
            .SequenceEqual(originalPositions));

        var returned = tracker.Update(
            originFrame, 160, 100, [Line(40, 0, 159)]);
        Check(returned.AtlasLines.SequenceEqual(originalLines));
        Check(returned.AtlasFeatures.Select(feature => feature.AtlasPosition)
            .SequenceEqual(originalPositions));
    }

    public static void TestGroundOnlyTrackingKeepsLineIDAcrossLargeVerticalLook()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = tracker.Update(
            Texture(160, 180, 0, 0), 160, 180, [Line(40, 0, 159)]);
        Check(origin.AtlasLines.Count > 0, "Expected persistent ground line.");
        var lineId = origin.AtlasLines[0].SegmentId;
        var shifts = Enumerable.Range(1, 22).Select(index => index * 5)
            .Concat(Enumerable.Range(0, 22).Select(index => 105 - index * 5));
        foreach (var shift in shifts)
        {
            var result = tracker.Update(
                Texture(160, 180, 0, shift), 160, 180,
                [Line(40 + shift, 0, 159)]);
            Check(result.PoseVerified);
            Near(shift, result.CameraPosition?.Y ?? double.NaN, .5);
            Check(result.GroundSegmentCount == 1);
            Check(result.AtlasLines.All(line => line.SegmentId == lineId));
        }
    }

    public static void TestFeaturelessVerticalLookMustHoldPose()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var frame = Solid(160, 240, 90);
        var learned = tracker.Update(frame, 160, 240, [Line(40, 0, 159)]);
        Check(learned.AtlasLines.Count > 0, "Expected persistent ground line.");
        var lineId = learned.AtlasLines[0].SegmentId;

        var looked = tracker.Update(
            frame, 160, 240, [Line(150, 0, 159)], timestamp: 1);

        Check(!looked.PoseVerified);
        var camera = looked.CameraPosition
            ?? throw new InvalidOperationException("Expected held camera pose.");
        Check(camera == new AtlasPoint(0, 0),
            "Featureless vertical look changed the held camera pose.");
        Check(looked.GroundSegmentCount == 1);
        Check(looked.AtlasLines.All(line => line.SegmentId == lineId));
        Check(looked.Features.All(feature => feature.ImageRect.Y == 40));
    }

    public static void TestUnresolvedVerticalLookCannotCreateOrDeleteDuplicateLineOne()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(160, 180, 0, 0);
        var learned = tracker.Update(
            origin, 160, 180, [Line(40, 0, 159)]);
        Check(learned.AtlasLines.Count > 0, "Expected persistent ground line.");
        var lineId = learned.AtlasLines[0].SegmentId;
        var shifted = Texture(160, 180, 0, 110);

        var unresolved = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 12; index++)
        {
            unresolved = tracker.Update(
                shifted, 160, 180, [Line(150, 0, 159)]);
        }

        Check(unresolved.GroundSegmentCount == 1);
        Check(unresolved.AtlasLines.All(line => line.SegmentId == lineId));
    }

    public static void TestMeasuredFallbackSeedsCurrentCaptureWithoutLosingNextMotion()
    {
        const int width = 320;
        const int height = 100;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(width, height, 0, 0);
        var initial = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 3; index++)
        {
            initial = tracker.Update(
                origin, width, height, [Line(40, 0, width - 1)],
                timestamp: index / 60d);
        }
        Check(initial.AtlasLines.Count > 0, "Expected original atlas line.");
        var originalLine = initial.AtlasLines[0];
        tracker.ReanchorLocalCamera(
            new AtlasPoint(12, 4), Texture(width, height, -12, 4),
            width, height, [Line(44, 0, width - 1)], timestamp: .05);

        var resumed = tracker.Update(
            Texture(width, height, -16, 8), width, height,
            [Line(48, 0, width - 1)], timestamp: .07);
        Check(resumed.PoseVerified);
        Near(16, resumed.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected resumed camera pose."), .01);
        Near(8, resumed.CameraPosition?.Y
            ?? throw new InvalidOperationException("Expected resumed camera pose."), .01);
        var preservedLines = resumed.AtlasLines.Where(line =>
            line.SegmentId == originalLine.SegmentId).ToArray();
        Check(preservedLines.Length > 0, "Expected preserved atlas line.");
        var preservedLine = preservedLines[0];
        Check(preservedLine.AtlasStart.Y == originalLine.AtlasStart.Y,
            "Measured fallback moved the preserved atlas line vertically.");
    }

    public static void TestMeasuredFallbackDefinesEpochBeforeAnyGroundIsEstablished()
    {
        const int width = 320;
        const int height = 100;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .2);
        var frame = Texture(width, height, 0, 0);
        var line = new[] { Line(40, 0, width - 1) };
        _ = tracker.Update(frame, width, height, line, timestamp: 0);
        tracker.ReanchorLocalCamera(
            new AtlasPoint(13, 0), frame, width, height, line, timestamp: .01);

        var result = GroundHypothesisTrackingResult.Empty;
        for (var index = 1; index <= 20; index++)
        {
            result = tracker.Update(
                frame, width, height, line,
                timestamp: .01 + index / 60d);
        }
        Check(result.PoseVerified);
        Check(result.HasConfirmedGround);
        Near(13, result.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected fallback epoch camera pose."), .01);
    }

    public static void TestStableShortPlatformNeedsCameraConsistentMotionBeforeAtlasAdmission()
    {
        const int width = 160;
        const int height = 100;
        var tracker = new GroundHypothesisTracker();
        var frame = Texture(width, height, 0, 0);
        var initialLines = new[]
        {
            Line(40, 40, 71),
            Line(70, 0, 159),
        };
        var result = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index <= 120; index++)
            result = tracker.Update(
                frame, width, height, initialLines, timestamp: index / 60d);
        Check(result.GroundSegmentCount == 1);

        for (var shift = 2; shift <= 12; shift += 2)
        {
            result = tracker.Update(
                Texture(width, height, -shift, 0), width, height,
                [Line(40, 40 - shift, 71 - shift), Line(70, 0, 159)],
                timestamp: 2 + shift / 60d);
        }
        var movedFrame = Texture(width, height, -12, 0);
        var movedLines = new[] { Line(40, 28, 59), Line(70, 0, 159) };
        for (var index = 1; index <= 8; index++)
            _ = tracker.Update(
                movedFrame, width, height, [movedLines[1]],
                timestamp: 2.2 + index / 60d);
        for (var index = 1; index <= 120; index++)
            result = tracker.Update(
                movedFrame, width, height, movedLines,
                timestamp: 2.4 + index / 60d);

        Check(result.GroundSegmentCount == 2);
        var platform = result.Features
            .Where(feature => feature.ImageRect.Y == 40).ToArray();
        Check(platform.Select(feature => feature.SequenceIndex)
            .SequenceEqual([0]));
        Check(platform.Select(feature => feature.ImageRect.X)
            .SequenceEqual([36]));
        Check(platform.All(feature => feature.ImageRect.Y == 40));
    }

    public static void TestNewFloorEnteringWithoutCompletePatchEventuallyPassesMotionChecks()
    {
        const int width = 320;
        const int height = 140;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .2);
        var result = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index <= 100; index++)
        {
            var shift = Math.Max(0, index - 30) * 2;
            var bytes = new byte[width * height];
            for (var pixel = 0; pixel < bytes.Length; pixel++)
            {
                var x = pixel % width + shift;
                var y = pixel / width;
                var hash = (ulong)x * 73_856_093 ^ (ulong)y * 19_349_663;
                hash ^= hash >> 13;
                hash = unchecked(hash * 1_274_126_177);
                bytes[pixel] = unchecked((byte)(hash >> 11));
            }
            var lines = new List<DetectedFloorLine> { Line(40, 0, 319) };
            if (index >= 30)
                lines.Add(Line(90, Math.Max(0, 314 - shift), 319));
            result = tracker.Update(
                bytes, width, height, lines, timestamp: index / 60d);
        }
        Check(result.AtlasLines.Any(line => Math.Abs(line.AtlasStart.Y - 50) < 4));
    }

    public static void TestRejectedLineKeepsHiddenLoopClosureEvidence()
    {
        const int width = 320;
        const int height = 100;
        var line = new[] { Line(40, 0, width - 1) };
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(width, height, 0, 0);
        var learned = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 3; index++)
            learned = tracker.Update(origin, width, height, line, timestamp: index / 60d);
        Check(learned.GlobalFeatureCount >= 16);

        var cleaned = learned;
        for (var index = 3; index < 130; index++)
            cleaned = tracker.Update(origin, width, height, [], timestamp: index / 60d);
        Check(cleaned.AtlasFeatures.Count == 0);
        Check(cleaned.AtlasLines.Count == 0);
        Check(cleaned.GlobalFeatureCount >= 16,
            "Visual cleanup erased hidden loop-closure descriptors.");

        tracker.Reset(keepingGlobalFeatures: true);
        var returned = tracker.Update(
            Texture(width, height, -62, 5), width, height,
            [Line(45, 0, width - 1)], timestamp: 3);
        Check(returned.GlobalMatchCount >= 6);
        Near(62, returned.GlobalCameraPosition?.X
            ?? throw new InvalidOperationException("Expected global return X."), .5);
        Near(5, returned.GlobalCameraPosition?.Y
            ?? throw new InvalidOperationException("Expected global return Y."), .5);
    }

    public static void TestProvisionalContinuitySurvivesBriefTextureMissThenExpires()
    {
        const int width = 320;
        const int height = 100;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(width, height, 0, 0);
        for (var index = 0; index < 3; index++)
            _ = tracker.Update(origin, width, height,
                [Line(40, 0, width - 1)], timestamp: index / 60d);
        tracker.ReanchorLocalCamera(
            new AtlasPoint(12, 4), Texture(width, height, -12, 4),
            width, height, [Line(44, 0, width - 1)], timestamp: .05);

        var missed = tracker.Update(
            Solid(width, height, 1), width, height, [], timestamp: .10);
        Check(!missed.PoseVerified);
        Check(missed.ProvisionalContinuityActive);

        var stillRecoverable = tracker.Update(
            Solid(width, height, 2), width, height, [], timestamp: .39);
        Check(stillRecoverable.ProvisionalContinuityActive);

        var expired = tracker.Update(
            Solid(width, height, 3), width, height, [], timestamp: .41);
        Check(!expired.ProvisionalContinuityActive);
    }

    public static void TestDeferredPlacementKeepsAtlasEvidenceAndRecoversAgainstTrustedPixels()
    {
        const int width = 640;
        const int height = 160;
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        var origin = Texture(width, height, 0, 0);
        var initial = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 3; index++)
            initial = tracker.Update(origin, width, height,
                [Line(40, 16, 623)], timestamp: index / 60d);
        Check(initial.AtlasLines.Count > 0);

        var proposals = new List<GroundPlacementProposal>();
        var recovery = new GroundPlacementRecovery();
        var deferred = tracker.Update(
            Texture(width, height, -4, 48), width, height,
            [Line(88, 16, 623)], timestamp: .05,
            placementValidator: proposal =>
            {
                proposals.Add(proposal);
                return recovery.Accepts(proposal, new AtlasPoint(0, 0), width,
                    .05, 1d / 60);
            });
        Check(proposals.Count == 1);
        Check(!deferred.PoseVerified,
            $"Expected deferred placement; proposal={proposals[^1]}.");
        Check(recovery.RetainsCandidate(.05));
        Check(!recovery.AllowsAtlasWrite);
        Check(deferred.AtlasLines.Select(line => line.AtlasStart)
            .SequenceEqual(initial.AtlasLines.Select(line => line.AtlasStart)));
        Check(deferred.AtlasLines.Select(line => line.AtlasEnd)
            .SequenceEqual(initial.AtlasLines.Select(line => line.AtlasEnd)));
        Check(deferred.GlobalFeatureCount == initial.GlobalFeatureCount);

        var resumed = tracker.Update(
            Texture(width, height, -8, 44), width, height,
            [Line(84, 16, 623)], timestamp: .067,
            placementValidator: proposal =>
            {
                proposals.Add(proposal);
                return recovery.Accepts(proposal, new AtlasPoint(0, 3), width,
                    .067, .017);
            });
        Check(resumed.PoseVerified);
        Check(recovery.AllowsAtlasWrite);
        Near(8, resumed.CameraPosition?.X
            ?? throw new InvalidOperationException("Expected recovered camera X."), .01);
        Near(44, resumed.CameraPosition?.Y
            ?? throw new InvalidOperationException("Expected recovered camera Y."), .01);
        Check(proposals[^1].DistinctFrame);
    }

    private static DetectedFloorLine Line(
        int row,
        int start,
        int end,
        params PixelSpan[] evidence) =>
        new(row, new PixelSpan(start, end), evidence.Length == 0 ? null : evidence);

    private static byte[] Texture(
        int width,
        int height,
        int shiftX,
        int shiftY,
        double gain = 1,
        double bias = 0)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - shiftX;
                var sourceY = y - shiftY;
                if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height)
                    continue;
                var hash = (ulong)sourceX * 73_856_093
                    ^ (ulong)sourceY * 19_349_663;
                hash ^= hash >> 13;
                hash = unchecked(hash * 1_274_126_177);
                var original = unchecked((byte)(hash >> 11));
                pixels[y * width + x] = (byte)Math.Clamp(
                    Math.Round(original * gain + bias, MidpointRounding.AwayFromZero),
                    0,
                    255);
            }
        }
        return pixels;
    }

    private static byte[] Solid(int width, int height, byte value) =>
        Enumerable.Repeat(value, checked(width * height)).ToArray();

    private static double OpacityMean(
        IReadOnlyList<GroundHypothesisAtlasFeature> features)
    {
        var values = features.SelectMany(feature => feature.ReferenceOpacity).ToArray();
        return values.Length == 0 ? 0 : values.Average(value => (double)value);
    }

    private static void AssertNoHorizontalOverlap(
        IReadOnlyList<GroundHypothesisFeature> features)
    {
        foreach (var row in features.GroupBy(feature => feature.ImageRect.Y))
        {
            var ordered = row.OrderBy(feature => feature.ImageRect.X).ToArray();
            for (var index = 1; index < ordered.Length; index++)
                Check(ordered[index - 1].ImageRect.Right <= ordered[index].ImageRect.X);
        }
    }
}

file static class GroundBootstrapMaskCohortParityTests
{
    private const int BootstrapWidth = 320;
    private const int BootstrapHeight = 100;
    private const int MaskWidth = 320;
    private const int MaskHeight = 96;
    private const int CohortWidth = 640;
    private const int CohortHeight = 96;

    private static void Check(
        bool value,
        string message = "Ground bootstrap/mask/cohort parity assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, double tolerance = .000_001) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");

    public static void TestUntrustedFirstViewCannotPreventStableNewGroundFromInitializing()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .2);
        var lines = new[] { Line(40, 16, 303) };
        var original = BootstrapPixels(0);
        var current = BootstrapPixels(87_654);
        _ = tracker.Update(original, BootstrapWidth, BootstrapHeight, lines, timestamp: 0);
        double? confirmedAt = null;
        for (var index = 1; index <= 60; index++)
        {
            var result = tracker.Update(
                current, BootstrapWidth, BootstrapHeight, lines,
                timestamp: index / 60d);
            if (result.PoseVerified && result.HasConfirmedGround && confirmedAt is null)
                confirmedAt = index / 60d;
        }

        Check(confirmedAt is not null, "Stable replacement ground never initialized.");
        Check(confirmedAt < .6,
            $"Stable replacement ground initialized too late: {confirmedAt}.");
    }

    public static void TestEstablishedMapSurvivesUnrelatedPixelsWithoutReseedingThem()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .2);
        var lines = new[] { Line(40, 16, 303) };
        var original = BootstrapPixels(0);
        var unrelated = BootstrapPixels(87_654);
        var result = GroundHypothesisTrackingResult.Empty;
        for (var index = 0; index < 60; index++)
        {
            result = tracker.Update(
                original, BootstrapWidth, BootstrapHeight, lines,
                timestamp: index / 60d);
        }
        var established = result.AtlasFeatures.ToArray();
        Check(result.GlobalFeatureCount > 0);

        for (var index = 60; index < 150; index++)
        {
            result = tracker.Update(
                unrelated, BootstrapWidth, BootstrapHeight, lines,
                timestamp: index / 60d);
            Check(!result.PoseVerified);
            Check(AtlasFeaturesEqual(result.AtlasFeatures, established),
                "Unrelated pixels reseeded the established atlas features.");
        }
    }

    public static void TestContinuallyChangingBootstrapImagesCannotInventConfirmedGround()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .2);
        var lines = new[] { Line(40, 16, 303) };
        for (var index = 0; index < 90; index++)
        {
            var values = BootstrapPixels(unchecked((ulong)index * 87_654));
            var result = tracker.Update(
                values, BootstrapWidth, BootstrapHeight, lines,
                timestamp: index / 60d);
            Check(!result.HasConfirmedGround);
            Check(result.GlobalFeatureCount == 0);
        }
    }

    public static void TestFullyMaskedRecoveryWaitsForVisiblePixelsWithoutDiscardingGround()
    {
        var (tracker, initial) = EstablishedMaskedTracker();
        var moved = MaskPixels(4);
        var hidden = UpdateMasked(
            tracker, moved, 24, [new PixelRect(0, 32, MaskWidth, 32)]);
        Check(!hidden.PoseVerified);
        AssertPoint(new AtlasPoint(0, 0), hidden.CameraPosition, "hidden camera");
        Check(hidden.LocalTextureSupport == 0);
        Check(hidden.GlobalFeatureCount == initial.GlobalFeatureCount);

        var revealed = UpdateMasked(tracker, moved, 25);
        Check(revealed.PoseVerified);
        AssertPoint(new AtlasPoint(4, 0), revealed.CameraPosition, "revealed camera");
    }

    public static void TestSparseVisibleRecoveryReportsPixelCostInsteadOfZeroSpatialResidual()
    {
        var (tracker, _) = EstablishedMaskedTracker();
        var moved = MaskPixels(4);
        for (var index = 0; index < moved.Length; index++)
        {
            moved[index] = (byte)Math.Clamp(
                moved[index] + (index % 2 == 0 ? 8 : -8), 0, 255);
        }
        var masks = Enumerable.Range(0, 6)
            .Select(index => new PixelRect(16 + index * 48, 36, 2, 24))
            .ToArray();
        var recovered = UpdateMasked(tracker, moved, 24, masks);

        Check(recovered.PoseVerified,
            $"Expected verified sparse recovery; support={recovered.LocalTextureSupport}, error={recovered.LocalTextureError}, residual={recovered.ResidualRms}.");
        AssertPoint(new AtlasPoint(4, 0), recovered.CameraPosition, "sparse recovery camera");
        var textureError = recovered.LocalTextureError
            ?? throw new InvalidOperationException("Expected local texture error.");
        Check(textureError > 7, $"Expected texture error > 7, got {textureError}.");
        Check(textureError < 9, $"Expected texture error < 9, got {textureError}.");
        Near(0, recovered.ResidualRms
            ?? throw new InvalidOperationException("Expected spatial residual."));
    }

    public static void TestPartialMaskRetainsReferencePixelsAndFeatureIdentity()
    {
        var (tracker, initial) = EstablishedMaskedTracker();
        var original = MaskPixels(0);
        var mask = new PixelRect(64, 36, 64, 24);
        var covered = original.ToArray();
        for (var y = 36; y < 60; y++)
        {
            for (var x = 64; x < 128; x++)
                covered[y * MaskWidth + x] = (byte)Math.Clamp(
                    original[y * MaskWidth + x] + 12, 0, 255);
        }

        var result = initial;
        for (var frame = 24; frame < 44; frame++)
            result = UpdateMasked(tracker, covered, frame, [mask]);
        Check(result.PoseVerified);
        AssertPoint(new AtlasPoint(0, 0), result.CameraPosition, "partially masked camera");
        var hidden = result.Features.Where(feature =>
            Intersects(feature.ImageRect, mask)).ToArray();
        Check(hidden.Length > 0);
        Check(hidden.All(feature =>
            feature.Classification == GroundHypothesisClassification.Occluded));
        foreach (var feature in result.AtlasFeatures)
        {
            var prior = initial.AtlasFeatures.Where(candidate =>
                    candidate.SegmentId == feature.SegmentId
                    && candidate.SequenceIndex == feature.SequenceIndex)
                .ToArray();
            Check(prior.Length == 1, "Initial atlas feature identity was lost.");
            Check(feature.ReferencePixels.SequenceEqual(prior[0].ReferencePixels));
        }

        var revealed = UpdateMasked(tracker, original, 44);
        var hiddenIds = hidden.Select(feature => feature.Id).ToHashSet();
        var restoredIds = revealed.Features.Where(feature =>
                hiddenIds.Contains(feature.Id)
                && feature.Classification != GroundHypothesisClassification.Occluded)
            .Select(feature => feature.Id)
            .ToHashSet();
        Check(restoredIds.SetEquals(hiddenIds));
    }

    public static void TestOlderViewRecoversDespiteLaterChangedGroundTemplates()
    {
        var poses = ReturningPoses(unrelated: false);
        Check(poses[^1] == new AtlasPoint(0, 0),
            $"Expected returned camera at origin; tail={string.Join(",", poses.TakeLast(20).Select(point => point.X))}.");
        Check(poses.TakeLast(20).All(position => position == new AtlasPoint(0, 0)));
    }

    public static void TestUnrelatedViewCannotUseTheFrozenPopulationAsAFalseMatch()
    {
        Check(ReturningPoses(unrelated: true)
            .All(position => position == new AtlasPoint(20, 0)));
    }

    private static (GroundHypothesisTracker Tracker, GroundHypothesisTrackingResult Result)
        EstablishedMaskedTracker()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .1);
        var result = GroundHypothesisTrackingResult.Empty;
        for (var frame = 0; frame < 24; frame++)
            result = UpdateMasked(tracker, MaskPixels(0), frame);
        Check(result.GlobalFeatureCount == 18);
        Check(result.PoseVerified);
        return (tracker, result);
    }

    private static GroundHypothesisTrackingResult UpdateMasked(
        GroundHypothesisTracker tracker,
        byte[] pixels,
        int frame,
        IReadOnlyList<PixelRect>? masks = null) =>
        tracker.Update(
            pixels, MaskWidth, MaskHeight, [Line(40, 16, 303)],
            timestamp: frame / 60d,
            protectedOcclusions: masks ?? []);

    private static AtlasPoint[] ReturningPoses(bool unrelated)
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .1);
        var result = GroundHypothesisTrackingResult.Empty;
        var frame = 0;
        void Update(int camera, bool altered, bool narrow, bool unknown = false)
        {
            var values = CohortPixels(camera, altered, unknown);
            var line = narrow
                ? Line(40, 64 - camera, 255 - camera)
                : Line(40, 8, 631);
            result = tracker.Update(
                values, CohortWidth, CohortHeight, [line],
                timestamp: frame / 60d);
            frame++;
        }

        for (var index = 0; index < 24; index++)
            Update(0, altered: false, narrow: true);
        Check(result.GlobalFeatureCount == 12);
        for (var step = 1; step <= 8; step++)
            Update(step * 8, altered: false, narrow: true);
        for (var index = 0; index < 40; index++)
            Update(64, altered: true, narrow: false);
        Check(result.GlobalFeatureCount > 30);
        AssertPoint(new AtlasPoint(64, 0), result.CameraPosition, "expanded camera");

        tracker.ReanchorLocalCamera(new AtlasPoint(20, 0));
        var poses = new List<AtlasPoint>();
        for (var index = 0; index < 90; index++)
        {
            Update(0, altered: false, narrow: false, unknown: unrelated);
            poses.Add(result.CameraPosition
                ?? throw new InvalidOperationException("Expected returning camera pose."));
        }
        return poses.ToArray();
    }

    private static byte[] BootstrapPixels(ulong seed)
    {
        var pixels = new byte[BootstrapWidth * BootstrapHeight];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % BootstrapWidth;
            var y = index / BootstrapWidth;
            var hash = (ulong)x * 73_856_093 ^ (ulong)y * 19_349_663 ^ seed;
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            pixels[index] = unchecked((byte)(hash >> 11));
        }
        return pixels;
    }

    private static byte[] MaskPixels(int camera)
    {
        var pixels = new byte[MaskWidth * MaskHeight];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % MaskWidth + camera;
            var y = index / MaskWidth;
            var hash = (ulong)x * 73_856_093 ^ (ulong)y * 19_349_663;
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            pixels[index] = unchecked((byte)(hash >> 11));
        }
        return pixels;
    }

    private static byte[] CohortPixels(int camera, bool altered, bool unrelated)
    {
        var pixels = new byte[CohortWidth * CohortHeight];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % CohortWidth + camera;
            var y = index / CohortWidth;
            var hash = (ulong)x * 73_856_093 ^ (ulong)y * 19_349_663
                ^ (unrelated ? 87_654UL : 0);
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            var value = unchecked((byte)(hash >> 11));
            var noise = altered && x >= 272
                ? ((x + y) % 2 == 0 ? 64 : -64)
                : 0;
            pixels[index] = (byte)Math.Clamp(value + noise, 0, 255);
        }
        return pixels;
    }

    private static DetectedFloorLine Line(int row, int start, int end) =>
        new(row, new PixelSpan(start, end));

    private static bool Intersects(PixelRect left, PixelRect right) =>
        left.X < right.Right && right.X < left.Right
        && left.Y < right.Bottom && right.Y < left.Bottom;

    private static void AssertPoint(
        AtlasPoint expected,
        AtlasPoint? actual,
        string label)
    {
        var point = actual
            ?? throw new InvalidOperationException($"Expected {label}.");
        Near(expected.X, point.X);
        Near(expected.Y, point.Y);
    }

    private static bool AtlasFeaturesEqual(
        IReadOnlyList<GroundHypothesisAtlasFeature> left,
        IReadOnlyList<GroundHypothesisAtlasFeature> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.SegmentId != second.SegmentId
                || first.SequenceIndex != second.SequenceIndex
                || first.AtlasPosition != second.AtlasPosition
                || !first.ReferencePixels.SequenceEqual(second.ReferencePixels)
                || !first.ReferenceOpacity.SequenceEqual(second.ReferenceOpacity))
            {
                return false;
            }
        }
        return true;
    }
}

file static class LiveTiledAtlasBatchOneTests
{
    public static void TestPoseCorrectionReplacesOldPlacementAtomically()
    {
        var atlas = new LiveTiledAtlas();
        var red = Solid(16, 16, red: 255);
        Check(atlas.Insert(1, red, 16, new(0, 0)), "Initial placement was rejected.");
        Check(atlas.Insert(2, red, 16, new(300, 0)), "Far placement was rejected.");
        var old = atlas.Snapshot;

        Check(atlas.ReplaceCameraPositions(
                new Dictionary<long, AtlasPoint> { [1] = new(0, 0), [2] = new(40, 0) },
                old.Revision),
            "Complete current pose correction was rejected.");
        var corrected = atlas.Snapshot;
        Check(corrected.Revision > old.Revision, "Correction did not advance the revision.");
        Check(!corrected.Tiles.Any(tile => tile.TileX == 1),
            "Correction left the obsolete far tile published.");
        Check(corrected.Tiles.Any(tile => tile.TileX == 0),
            "Correction did not publish the corrected tile.");
    }

    public static void TestContentBoundsGrowInsideOneStorageTile()
    {
        var atlas = new LiveTiledAtlas();
        var image = Solid(64, 32, red: 255, green: 255, blue: 255);
        Check(atlas.Insert(1, image, 64, new(0, 0)), "First observation was rejected.");
        var first = atlas.Snapshot;
        EqualRect(new(0, 0, 64, 32), first.ContentBounds, "first content bounds");

        Check(atlas.Insert(2, image, 64, new(1, 0)), "Second observation was rejected.");
        var second = atlas.Snapshot;
        EqualRect(first.TotalBounds, second.TotalBounds, "storage bounds");
        Check(second.ContentBounds.Width == first.ContentBounds.Width + 1,
            "Content width did not grow by one pixel inside the same storage tile.");
        Check(second.ContentBounds.Height == first.ContentBounds.Height,
            "Content height changed unexpectedly.");
    }

    public static void TestRejectsStaleAndIncompletePoseCorrections()
    {
        var atlas = new LiveTiledAtlas();
        var image = Solid(8, 8, red: 255, green: 255, blue: 255);
        Check(atlas.Insert(1, image, 8, new(0, 0)), "First observation was rejected.");
        Check(atlas.Insert(2, image, 8, new(16, 0)), "Second observation was rejected.");
        var revision = atlas.Revision;

        Check(!atlas.ReplaceCameraPositions(
                new Dictionary<long, AtlasPoint> { [1] = new(0, 0) }, revision),
            "Incomplete correction was accepted.");
        Check(!atlas.ReplaceCameraPositions(
                new Dictionary<long, AtlasPoint> { [1] = new(0, 0), [2] = new(16, 0) },
                revision - 1),
            "Stale correction was accepted.");
        Check(atlas.Revision == revision, "Rejected corrections changed the revision.");
    }

    public static void TestCancelledPoseCorrectionRollsBackAndCanBeRetried()
    {
        var atlas = new LiveTiledAtlas();
        var image = Solid(16, 16, red: 255, green: 255, blue: 255);
        Check(atlas.Insert(1, image, 16, new(0, 0)), "First observation was rejected.");
        Check(atlas.Insert(2, image, 16, new(300, 0)), "Second observation was rejected.");
        var before = atlas.Snapshot;
        var corrected = new Dictionary<long, AtlasPoint>
        {
            [1] = new(0, 0),
            [2] = new(40, 0)
        };
        var cancellationChecks = 0;

        Check(!atlas.ReplaceCameraPositions(
                corrected,
                before.Revision,
                () => ++cancellationChecks >= 3),
            "Cancelled correction was published.");
        var rolledBack = atlas.Snapshot;
        Check(rolledBack.Revision == before.Revision, "Cancellation changed the revision.");
        Check(rolledBack.Tiles.Select(tile => tile.Id)
                .SequenceEqual(before.Tiles.Select(tile => tile.Id)),
            "Cancellation changed the published tile identities.");
        EqualRect(before.TotalBounds, rolledBack.TotalBounds, "cancelled total bounds");

        Check(atlas.ReplaceCameraPositions(corrected, before.Revision),
            "Correction could not be retried after cancellation.");
        Check(!atlas.Snapshot.Tiles.Any(tile => tile.TileX == 1),
            "Retried correction retained the obsolete tile.");
    }

    public static void TestPoseCorrectionCanCancelInsideOneCrowdedTile()
    {
        var atlas = new LiveTiledAtlas();
        var image = Solid(1, 1, red: 255, green: 255, blue: 255);
        const int count = 20;
        for (var id = 0; id < count; id++)
        {
            Check(atlas.Insert(id, image, 1, new(0, 0)),
                $"Crowded observation {id} was rejected.");
        }
        var before = atlas.Snapshot;
        var corrected = Enumerable.Range(0, count)
            .ToDictionary(id => (long)id, _ => new AtlasPoint(1, 0));
        var cancellationChecks = 0;

        Check(!atlas.ReplaceCameraPositions(
                corrected,
                before.Revision,
                () => ++cancellationChecks >= count + 7),
            "Crowded correction ignored cancellation.");
        Check(cancellationChecks >= count + 7,
            "Correction did not reach cancellable per-tile work.");
        Check(atlas.LastPublicationRasterizedContributionCount < count,
            "Cancellation occurred only after rasterizing the entire crowded tile.");
        var rolledBack = atlas.Snapshot;
        Check(rolledBack.Revision == before.Revision, "Crowded cancellation changed revision.");
        Check(rolledBack.Tiles.Select(tile => tile.Id)
                .SequenceEqual(before.Tiles.Select(tile => tile.Id)),
            "Crowded cancellation changed tile identities.");
        EqualRect(before.TotalBounds, rolledBack.TotalBounds, "crowded cancellation bounds");
    }

    public static void TestIncrementalInsertCanCancelInsidePixelWorkAndRollBack()
    {
        var atlas = new LiveTiledAtlas();
        var image = Solid(256, 256, red: 255, green: 255, blue: 255);
        Check(atlas.Insert(1, image, 256, new(0, 0)), "Initial full-tile insert failed.");
        var before = atlas.Snapshot;
        var linksBefore = atlas.RetainedContributionTileLinkCount;
        var cancellationChecks = 0;

        Check(!atlas.Insert(
                2,
                image,
                256,
                new(0, 0),
                shouldCancel: () => ++cancellationChecks >= 7),
            "Cancelled incremental insertion was published.");
        var rolledBack = atlas.Snapshot;
        Check(rolledBack.Revision == before.Revision, "Cancelled insert changed revision.");
        Check(rolledBack.Tiles.Select(tile => tile.Id)
                .SequenceEqual(before.Tiles.Select(tile => tile.Id)),
            "Cancelled insert changed tile identities.");
        Check(atlas.RetainedContributionTileLinkCount == linksBefore,
            "Cancelled insert leaked retained contribution links.");

        Check(atlas.Insert(2, image, 256, new(0, 0)),
            "Insertion could not be retried after cancellation.");
    }

    public static void TestIncrementalOpaqueBlackWinsWhileTransparentLeavesPriorPixel()
    {
        var atlas = new LiveTiledAtlas();
        var green = Solid(2, 1, green: 255);
        var transparentThenBlack = Pixels(
            2,
            1,
            (0, 0, 0, 0),
            (0, 0, 0, 255));
        Check(atlas.Insert(1, green, 2, new(0, 0)), "Green source was rejected.");
        Check(atlas.Insert(2, transparentThenBlack, 2, new(0, 0)),
            "Transparent/black source was rejected.");

        var left = Pixel(atlas, 0, 0);
        var right = Pixel(atlas, 1, 0);
        Check(left.Green > 200, "Transparent source erased the prior green pixel.");
        Check(right.Red == 0 && right.Green == 0 && right.Blue == 0 && right.Alpha > 200,
            "Opaque black was not retained as observed scenery.");
    }

    public static void TestPreserveBrighterRejectsDarkOverwriteIncrementallyAndOnRebuild()
    {
        var atlas = new LiveTiledAtlas(pixelPolicy: LiveTiledAtlasPixelPolicy.PreserveBrighter);
        var green = Solid(1, 1, green: 255);
        var black = Solid(1, 1);
        var white = Solid(1, 1, red: 255, green: 255, blue: 255);
        Check(atlas.Insert(1, green, 1, new(0, 0)), "Green source was rejected.");
        Check(atlas.Insert(2, black, 1, new(0, 0)), "Black source was rejected.");
        Check(Pixel(atlas, 0, 0).Green > 200,
            "PreserveBrighter accepted an incremental dark overwrite.");

        Check(atlas.Insert(2, black, 1, new(0, 0)), "Replacement rebuild failed.");
        Check(Pixel(atlas, 0, 0).Green > 200,
            "PreserveBrighter accepted a dark overwrite during rebuild.");

        Check(atlas.Insert(3, white, 1, new(0, 0)), "White source was rejected.");
        var value = Pixel(atlas, 0, 0);
        Check(value.Red > 240 && value.Green > 240 && value.Blue > 240,
            "Brighter evidence did not replace the prior pixel.");
    }

    public static void TestIncrementalInsertionMatchesFullRebuild()
    {
        var first = Pixels(2, 1, (255, 0, 0, 255), (0, 0, 0, 0));
        var second = Pixels(2, 1, (0, 0, 0, 0), (0, 0, 255, 255));
        var newest = Pixels(2, 1, (0, 255, 0, 255), (0, 0, 0, 0));
        var incremental = new LiveTiledAtlas();
        var rebuilt = new LiveTiledAtlas();
        foreach (var atlas in new[] { incremental, rebuilt })
        {
            Check(atlas.Insert(1, first, 2, new(0, 0)), "First source was rejected.");
            Check(atlas.Insert(2, second, 2, new(0, 0)), "Second source was rejected.");
            Check(atlas.Insert(3, newest, 2, new(0, 0)), "Newest source was rejected.");
        }
        Check(rebuilt.Insert(3, newest, 2, new(0, 0)),
            "Identical replacement did not force the rebuild path.");

        var incrementalTile = incremental.Tiles.Single();
        var rebuiltTile = rebuilt.Tiles.Single();
        Check(incrementalTile.Id == rebuiltTile.Id, "Tile IDs differ after rebuild.");
        EqualRect(incrementalTile.WorldBounds, rebuiltTile.WorldBounds, "rebuilt world bounds");
        Check(incrementalTile.ContributionIds.SequenceEqual(rebuiltTile.ContributionIds),
            "Contribution IDs differ after rebuild.");
        Check(incrementalTile.Image.Pixels.SequenceEqual(rebuiltTile.Image.Pixels),
            "Incremental pixels differ from full rebuild pixels.");
    }

    public static void TestUniqueInsertRasterizesOnlyNewSourcePerDirtyTile()
    {
        var atlas = new LiveTiledAtlas();
        var image = Solid(8, 8, red: 255);
        for (var id = 0; id < 24; id++)
        {
            Check(atlas.Insert(id, image, 8, new(0, 0)),
                $"Unique observation {id} was rejected.");
            Check(atlas.LastPublicationRasterizedContributionCount == 1,
                $"Unique observation {id} replayed older sources.");
        }
        Check(atlas.Tiles.Single().ContributionIds.Count == 24,
            "The crowded tile did not retain all observation IDs.");
    }

    public static void TestNewestQuiltedOverwritesInteriorAndFollowsLowErrorEdgeSeam()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.NewestQuilted);
        const int width = 64;
        const int height = 64;
        var reference = new byte[width * height * BgraFrame.BytesPerPixel];
        for (var topY = 0; topY < height; topY++)
        {
            var seamX = 2 + topY % 3;
            for (var x = 0; x < width; x++)
            {
                var index = (topY * width + x) * BgraFrame.BytesPerPixel;
                var component = x == seamX ? (byte)255 : (byte)0;
                reference[index] = component;
                reference[index + 1] = component;
                reference[index + 2] = component;
                reference[index + 3] = 255;
            }
        }
        var initial = new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            reference,
            0,
            DateTimeOffset.UnixEpoch);
        var newest = Solid(width, height, red: 255, green: 255, blue: 255);
        Check(atlas.Insert(0, initial, width, new(0, 0)),
            "Initial quilt reference was rejected.");
        Check(atlas.Insert(1, newest, width, new(0, 0)),
            "Newest quilt source was rejected.");

        Check(Pixel(atlas, 32, 32).Red == 255,
            "Newest quilt did not overwrite the capture interior.");
        Check(Pixel(atlas, 0, 32).Red == 0,
            "Newest quilt did not retain older pixels outside the low-error seam.");
        Check(Pixel(atlas, 7, 32).Red == 255,
            "Newest quilt did not use new pixels inside the edge seam.");
        Check(atlas.TemporalDiagnostics.RetainedFrameCount == 0,
            "Newest quilting retained temporal frames.");
    }

    public static void TestNewestQuiltedIncrementalOutputMatchesCrossTileRebuild()
    {
        var incremental = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.NewestQuilted);
        var rebuilt = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.NewestQuilted);
        const int width = 300;
        var images = new BgraFrame[4];
        for (var frame = 0; frame < images.Length; frame++)
        {
            (int X, int Y)? particle = frame == 3 ? (255, 31) : null;
            images[frame] = TexturedImage(
                width,
                64,
                brightnessOffset: frame * 6,
                particle);
        }
        foreach (var atlas in new[] { incremental, rebuilt })
        {
            for (var frame = 0; frame < images.Length; frame++)
            {
                Check(atlas.Insert(frame, images[frame], width, new(0, 0)),
                    $"Newest-quilt sample {frame} was rejected.");
            }
        }
        Check(rebuilt.Insert(3, images[3], width, new(0, 0)),
            "Newest-quilt replacement rebuild was rejected.");

        var incrementalTiles = incremental.Tiles;
        var rebuiltTiles = rebuilt.Tiles;
        Check(incrementalTiles.Select(tile => tile.WorldBounds).SequenceEqual(
                rebuiltTiles.Select(tile => tile.WorldBounds)),
            "Newest-quilt incremental and rebuilt tile bounds differ.");
        Check(incrementalTiles.Select(tile => tile.Image.Pixels).Zip(
                rebuiltTiles.Select(tile => tile.Image.Pixels),
                (left, right) => left.SequenceEqual(right)).All(equal => equal),
            "Newest-quilt incremental and rebuilt tile bytes differ.");
    }

    public static void TestNewestQuiltedFullResolutionFiveRunBenchmark()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.NewestQuilted);
        var timings = new List<double>();
        for (var frame = 0; frame < 6; frame++)
        {
            (int X, int Y)? particle = frame == 4 ? (320, 180) : null;
            var image = TexturedImage(
                640,
                360,
                brightnessOffset: frame % 4,
                particle);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(atlas.Insert(frame, image, 640, new(0, 0)),
                $"Full-resolution newest-quilt sample {frame} was rejected.");
            if (frame > 0)
            {
                timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started)
                    .TotalMilliseconds);
            }
        }
        Check(timings.Count == 5,
            "Newest-quilt benchmark did not measure exactly five runs.");
        Check(timings.Max() < 133,
            $"Newest quilting exceeded the 7.5 fps atlas interval: {string.Join(", ", timings)}.");
        Check(atlas.TemporalDiagnostics.RetainedFrameCount == 0,
            "Newest-quilt benchmark retained temporal frames.");
        Console.WriteLine(
            $"NEWEST_QUILT_640x360_MS runs=5 ms={string.Join(",", timings.Select(value => value.ToString("F3")))}");
    }

    public static void TestTemporalAgreementRemovesInitialBrightAttackAfterThreeBackgroundSamples()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var attack = Solid(3, 3, red: 255, green: 255, blue: 255);
        var backgrounds = new[]
        {
            Solid(3, 3, red: 10, green: 11, blue: 12),
            Solid(3, 3, red: 12, green: 13, blue: 14),
            Solid(3, 3, red: 14, green: 15, blue: 16)
        };
        Check(atlas.Insert(1, attack, 3, new(0, 0), timestamp: 0),
            "Initial bright attack was rejected.");
        for (var offset = 0; offset < backgrounds.Length; offset++)
        {
            Check(atlas.Insert(
                    offset + 2,
                    backgrounds[offset],
                    3,
                    new(0, 0),
                    timestamp: (offset + 1) * .1),
                $"Background sample {offset} was rejected.");
        }
        var value = Pixel(atlas, 1, 1);
        Check(value.Red < 30 && value.Green < 30 && value.Blue < 30,
            $"Stable background did not remove the initial attack: {value}.");
    }

    public static void TestTemporalAgreementRejectsOneDarkOccluderAndKeepsStaticWhiteScenery()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var white = Solid(3, 3, red: 245, green: 246, blue: 247);
        var black = Solid(3, 3);
        for (var id = 1; id <= 3; id++)
        {
            Check(atlas.Insert(id, white, 3, new(0, 0), timestamp: id * .1),
                $"White sample {id} was rejected.");
        }
        Check(atlas.Insert(4, black, 3, new(0, 0), timestamp: .4),
            "Dark occluder sample was rejected.");
        var value = Pixel(atlas, 1, 1);
        Check(value.Red > 240 && value.Green > 240 && value.Blue > 240,
            $"One dark occluder replaced stable white scenery: {value}.");
    }

    public static void TestTemporalAgreementLetsStableDarkSceneryReplaceBrightProvisionalPixels()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var white = Solid(3, 3, red: 255, green: 255, blue: 255);
        var dark = Solid(3, 3, red: 18, green: 20, blue: 22);
        Check(atlas.Insert(1, white, 3, new(0, 0), timestamp: 0),
            "Bright provisional sample was rejected.");
        for (var id = 2; id <= 4; id++)
        {
            Check(atlas.Insert(id, dark, 3, new(0, 0), timestamp: id * .1),
                $"Stable dark sample {id} was rejected.");
        }
        var value = Pixel(atlas, 1, 1);
        Check(value == (18, 20, 22, 255),
            $"Stable dark scenery did not replace the provisional pixel: {value}.");
    }

    public static void TestTemporalAgreementSelectsAnObservedMedoidInsteadOfAveraging()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var components = new byte[] { 10, 20, 30 };
        for (var id = 0; id < components.Length; id++)
        {
            var component = components[id];
            Check(atlas.Insert(
                    id,
                    Solid(3, 3, component, component, component),
                    3,
                    new(0, 0),
                    timestamp: id * .1),
                $"Medoid sample {id} was rejected.");
        }
        var value = Pixel(atlas, 1, 1);
        Check(value.Red == 20 && value.Green == 20 && value.Blue == 20,
            $"Temporal policy averaged instead of selecting the observed medoid: {value}.");
    }

    public static void TestTemporalAgreementCountsDuplicateCaptureOnlyOnce()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var white = Solid(3, 3, red: 255, green: 255, blue: 255);
        var dark = Solid(3, 3, red: 16, green: 16, blue: 16);
        Check(atlas.Insert(
                1, white, 3, new(0, 0), captureIdentity: 100, timestamp: 0),
            "Initial capture was rejected.");
        var revision = atlas.Revision;
        Check(atlas.Insert(
                2, dark, 3, new(0, 0), captureIdentity: 100, timestamp: 0),
            "Duplicate capture was not harmlessly accepted.");
        Check(atlas.Revision == revision, "Duplicate capture changed the atlas revision.");
        for (var id = 3; id <= 4; id++)
        {
            Check(atlas.Insert(
                    id,
                    dark,
                    3,
                    new(0, 0),
                    captureIdentity: id,
                    timestamp: id * .1),
                $"Independent dark capture {id} was rejected.");
        }
        Check(Pixel(atlas, 1, 1).Red > 240,
            "Duplicate capture was counted toward a false dark consensus.");
        Check(atlas.Insert(
                5, dark, 3, new(0, 0), captureIdentity: 5, timestamp: .5),
            "Third independent dark capture was rejected.");
        Check(Pixel(atlas, 1, 1).Red == 16,
            "Three independent dark captures did not establish consensus.");
    }

    public static void TestTemporalAgreementWindowIsBoundedAndClearsAcrossDiscontinuity()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var image = Solid(4, 4, red: 40, green: 50, blue: 60);
        for (var id = 0; id < 10; id++)
        {
            Check(atlas.Insert(id, image, 4, new(0, 0), timestamp: id * .1),
                $"Window sample {id} was rejected.");
        }
        Check(atlas.TemporalDiagnostics.RetainedFrameCount == 7,
            "Temporal decoded-frame cache exceeded or missed its seven-frame bound.");
        Check(atlas.TemporalDiagnostics.RetainedDecodedBytes <= 7 * 4 * 4 * 4,
            "Temporal decoded-byte cache exceeded its exact frame bound.");
        Check(atlas.Insert(10, image, 4, new(0, 0), timestamp: 3),
            "Post-gap sample was rejected.");
        Check(atlas.TemporalDiagnostics.RetainedFrameCount == 1,
            "A timestamp discontinuity did not clear the temporal window.");
        atlas.EndTemporalWindow();
        Check(atlas.TemporalDiagnostics.RetainedFrameCount == 0,
            "Explicit temporal-window end retained decoded frames.");
        Check(atlas.TemporalDiagnostics.RetainedDecodedBytes == 0,
            "Explicit temporal-window end retained decoded bytes.");
    }

    public static void TestTemporalAgreementAlignsMovingFramesInWorldCoordinates()
    {
        var atlas = new LiveTiledAtlas(
            anchorPosition: new AtlasPoint(0, 0),
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var colors = new (byte Red, byte Green, byte Blue)[]
        {
            (20, 30, 40), (50, 60, 70), (80, 90, 100),
            (110, 120, 130), (140, 150, 160), (170, 180, 190),
            (200, 210, 220)
        };
        for (var frame = 0; frame < 4; frame++)
        {
            var row = colors.Skip(frame).Take(4).ToArray();
            if (frame == 1) row[2] = (255, 255, 255);
            Check(atlas.Insert(
                    frame,
                    ThreeRow(row),
                    4,
                    new(frame, 0),
                    timestamp: frame * .1),
                $"Moving frame {frame} was rejected.");
        }
        var value = Pixel(atlas, 3, 1);
        Check(value.Red == 110 && value.Green == 120 && value.Blue == 130,
            $"World-aligned consensus did not reject the moving attack: {value}.");
    }

    public static void TestTemporalAgreementCancellationRollsBackWindowAndPublishedPixels()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var first = Solid(64, 64, red: 30, green: 40, blue: 50);
        var second = Solid(64, 64, red: 80, green: 90, blue: 100);
        for (var id = 0; id < 7; id++)
        {
            Check(atlas.Insert(id, first, 64, new(0, 0), timestamp: id * .1),
                $"Initial temporal sample {id} was rejected.");
        }
        var before = atlas.Snapshot;
        var diagnostics = atlas.TemporalDiagnostics;
        var cancellationChecks = 0;
        Check(!atlas.Insert(
                7,
                second,
                64,
                new(0, 0),
                timestamp: .7,
                shouldCancel: () => ++cancellationChecks >= 12),
            "Cancelled temporal insertion was published.");
        var rolledBack = atlas.Snapshot;
        Check(rolledBack.Revision == before.Revision,
            "Cancelled temporal insertion changed the revision.");
        Check(rolledBack.Tiles.Single().Image.Pixels.SequenceEqual(
                before.Tiles.Single().Image.Pixels),
            "Cancelled temporal insertion changed published pixels.");
        Check(atlas.TemporalDiagnostics == diagnostics,
            "Cancelled temporal insertion changed diagnostics or its retained window.");

        Check(atlas.Insert(7, second, 64, new(0, 0), timestamp: .7),
            "Temporal insertion could not be retried after cancellation.");
        Check(atlas.TemporalDiagnostics.RetainedFrameCount == 7,
            "Retried temporal insertion did not retain the bounded seven-frame window.");
        var value = Pixel(atlas, 32, 32);
        Check(value.Red == 30 && value.Green == 40 && value.Blue == 50,
            $"Retried temporal consensus changed stable scenery: {value}.");
    }

    public static void TestTemporalAgreementTreatsTransparentPixelsAsNoEvidenceAndBlackAsScenery()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var green = Solid(3, 3, green: 180);
        var values = Enumerable.Repeat((Red: (byte)0, Green: (byte)0,
                Blue: (byte)0, Alpha: (byte)0), 9).ToArray();
        values[4] = (0, 0, 0, 255);
        var masked = Pixels(3, 3, values);
        Check(atlas.Insert(0, green, 3, new(0, 0), timestamp: 0),
            "Initial green scenery was rejected.");
        for (var id = 1; id <= 3; id++)
        {
            Check(atlas.Insert(id, masked, 3, new(0, 0), timestamp: id * .1),
                $"Masked black sample {id} was rejected.");
        }
        var untouched = Pixel(atlas, 0, 0);
        var acceptedBlack = Pixel(atlas, 1, 1);
        Check(untouched.Green > 170,
            "Transparent pixels counted as temporal evidence.");
        Check(acceptedBlack == (0, 0, 0, 255),
            $"Opaque black was not accepted as stable scenery: {acceptedBlack}.");
    }

    public static void TestTemporalAgreementUsesNeighborhoodAcrossTileBoundary()
    {
        var atlas = new LiveTiledAtlas(
            anchorPosition: new AtlasPoint(0, 0),
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var bright = Solid(3, 3, red: 255, green: 255, blue: 255);
        var dark = Solid(3, 3, red: 24, green: 25, blue: 26);
        Check(atlas.Insert(0, bright, 3, new(255, 0), timestamp: 0),
            "Boundary bright source was rejected.");
        for (var id = 1; id <= 3; id++)
        {
            Check(atlas.Insert(id, dark, 3, new(255, 0), timestamp: id * .1),
                $"Boundary dark source {id} was rejected.");
        }
        Check(atlas.Tiles.Any(tile => tile.TileX == 0)
              && atlas.Tiles.Any(tile => tile.TileX == 1),
            "The boundary fixture did not span two tiles.");
        Check(Pixel(atlas, 255, 1).Red == 24,
            "Left tile did not use the cross-boundary temporal neighborhood.");
        Check(Pixel(atlas, 256, 1).Red == 24,
            "Right tile did not use the cross-boundary temporal neighborhood.");
    }

    public static void TestTemporalAgreementIncrementalOutputMatchesPoseCorrectedRebuild()
    {
        var images = new byte[] { 25, 27, 255, 29 }
            .Select(value => Solid(3, 3, value, value, value))
            .ToArray();
        var incremental = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var corrected = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        for (var index = 0; index < images.Length; index++)
        {
            var finalPosition = index == 0 ? new AtlasPoint(0, 0) : new AtlasPoint(1, 0);
            Check(incremental.Insert(
                    index,
                    images[index],
                    3,
                    finalPosition,
                    timestamp: index * .1),
                $"Incremental temporal sample {index} was rejected.");
            Check(corrected.Insert(
                    index,
                    images[index],
                    3,
                    new(0, 0),
                    timestamp: index * .1),
                $"Uncorrected temporal sample {index} was rejected.");
        }

        var beforeCorrection = corrected.Revision;
        var positions = Enumerable.Range(0, images.Length).ToDictionary(
            index => (long)index,
            index => index == 0 ? new AtlasPoint(0, 0) : new AtlasPoint(1, 0));
        Check(corrected.ReplaceCameraPositions(positions, beforeCorrection),
            "Current complete temporal pose correction was rejected.");
        Check(incremental.Tiles.Select(tile => tile.WorldBounds).SequenceEqual(
                corrected.Tiles.Select(tile => tile.WorldBounds)),
            "Incremental and pose-corrected temporal tile bounds differ.");
        Check(incremental.Tiles.Select(tile => tile.Image.Pixels).Zip(
                corrected.Tiles.Select(tile => tile.Image.Pixels),
                (left, right) => left.SequenceEqual(right)).All(equal => equal),
            "Incremental and pose-corrected temporal tile bytes differ.");
    }

    public static void TestTemporalQuiltingIncrementalOutputMatchesOrderedRebuild()
    {
        var incremental = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalQuilting);
        var rebuilt = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalQuilting);
        const int width = 300;
        var images = Enumerable.Range(0, 4)
            .Select(frame => TexturedImage(
                width,
                64,
                frame * 6,
                frame == 3 ? (255, 31) : null))
            .ToArray();
        foreach (var atlas in new[] { incremental, rebuilt })
        {
            for (var frame = 0; frame < images.Length; frame++)
            {
                Check(atlas.Insert(
                        frame,
                        images[frame],
                        width,
                        new(0, 0),
                        timestamp: frame * .1),
                    $"Temporal-quilt sample {frame} was rejected.");
            }
        }
        Check(rebuilt.Insert(3, images[3], width, new(0, 0), timestamp: .3),
            "Temporal-quilt ordered rebuild was rejected.");
        Check(incremental.Tiles.Select(tile => tile.WorldBounds).SequenceEqual(
                rebuilt.Tiles.Select(tile => tile.WorldBounds)),
            "Temporal-quilt incremental and rebuilt tile bounds differ.");
        Check(incremental.Tiles.Select(tile => tile.Image.Pixels).Zip(
                rebuilt.Tiles.Select(tile => tile.Image.Pixels),
                (left, right) => left.SequenceEqual(right)).All(equal => equal),
            "Temporal-quilt incremental and rebuilt tile bytes differ.");
    }

    public static void TestTemporalQuiltingWindowRemainsBounded()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalQuilting);
        for (var frame = 0; frame < 10; frame++)
        {
            var image = TexturedImage(64, 64, frame % 4);
            Check(atlas.Insert(
                    frame,
                    image,
                    64,
                    new(0, 0),
                    timestamp: frame * .1),
                $"Temporal-quilt window sample {frame} was rejected.");
        }
        Check(atlas.TemporalDiagnostics.RetainedFrameCount == 7,
            "Temporal quilting did not retain exactly seven frames.");
        Check(atlas.TemporalDiagnostics.RetainedDecodedBytes <= 7 * 64 * 64 * 4,
            "Temporal quilting exceeded its seven-frame decoded-byte bound.");
    }

    public static void TestTemporalQuiltingFullResolutionFiveRunBenchmark()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalQuilting);
        var timings = new List<double>();
        for (var frame = 0; frame < 7; frame++)
        {
            var image = TexturedImage(
                640,
                360,
                frame % 4,
                frame == 4 ? (320, 180) : null);
            Check(atlas.Insert(
                    frame,
                    image,
                    640,
                    new(0, 0),
                    timestamp: frame / 7.5),
                $"Full-resolution temporal-quilt sample {frame} was rejected.");
            if (frame >= 2)
                timings.Add(atlas.TemporalDiagnostics.LastFilterMilliseconds);
        }
        Check(timings.Count == 5,
            "Temporal-quilt benchmark did not measure exactly five runs.");
        Check(timings.Max() < 133,
            $"Temporal quilting exceeded the 7.5 fps atlas interval: {string.Join(", ", timings)}.");
        Check(atlas.TemporalDiagnostics.RetainedDecodedBytes <= 7 * 640 * 360 * 4,
            "Temporal quilting exceeded its full-resolution decoded-byte bound.");
        Console.WriteLine(
            $"QUILT_640x360_MS runs=5 ms={string.Join(",", timings.Select(value => value.ToString("F3")))}");
    }

    private static BgraFrame ThreeRow(
        IReadOnlyList<(byte Red, byte Green, byte Blue)> row)
    {
        var values = Enumerable.Range(0, 3)
            .SelectMany(_ => row)
            .Select(value => (value.Red, value.Green, value.Blue, (byte)255))
            .ToArray();
        return Pixels(row.Count, 3, values);
    }

    private static BgraFrame TexturedImage(
        int width,
        int height,
        int brightnessOffset,
        (int X, int Y)? particle = null)
    {
        var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
        for (var topY = 0; topY < height; topY++)
        {
            var worldY = height - 1 - topY;
            for (var x = 0; x < width; x++)
            {
                var isParticle = particle is { } point
                                 && Math.Abs(x - point.X) <= 1
                                 && Math.Abs(worldY - point.Y) <= 1;
                var component = isParticle
                    ? (byte)255
                    : TexturedComponent(x, worldY, brightnessOffset);
                var index = checked(
                    (topY * width + x) * BgraFrame.BytesPerPixel);
                pixels[index] = component;
                pixels[index + 1] = component;
                pixels[index + 2] = component;
                pixels[index + 3] = 255;
            }
        }
        return new(
            width,
            height,
            checked(width * BgraFrame.BytesPerPixel),
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static byte TexturedComponent(int x, int y, int brightnessOffset) =>
        checked((byte)(30 + (x * 7 + y * 11) % 40 + brightnessOffset));

    private static BgraFrame Solid(
        int width,
        int height,
        byte red = 0,
        byte green = 0,
        byte blue = 0,
        byte alpha = 255)
    {
        var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
        for (var offset = 0; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
        return new(
            width,
            height,
            checked(width * BgraFrame.BytesPerPixel),
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static BgraFrame Pixels(
        int width,
        int height,
        params (byte Red, byte Green, byte Blue, byte Alpha)[] values)
    {
        Check(values.Length == width * height, "Pixel fixture size mismatch.");
        var pixels = new byte[checked(values.Length * BgraFrame.BytesPerPixel)];
        for (var index = 0; index < values.Length; index++)
        {
            var offset = index * BgraFrame.BytesPerPixel;
            pixels[offset] = values[index].Blue;
            pixels[offset + 1] = values[index].Green;
            pixels[offset + 2] = values[index].Red;
            pixels[offset + 3] = values[index].Alpha;
        }
        return new(
            width,
            height,
            checked(width * BgraFrame.BytesPerPixel),
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static (byte Red, byte Green, byte Blue, byte Alpha) Pixel(
        LiveTiledAtlas atlas,
        int x,
        int y)
    {
        var tileX = FloorDivide(x, LiveTiledAtlas.TileSize);
        var tileY = FloorDivide(y, LiveTiledAtlas.TileSize);
        var tile = atlas.Tiles.Single(value => value.TileX == tileX && value.TileY == tileY);
        var localX = x - tileX * LiveTiledAtlas.TileSize;
        var localY = y - tileY * LiveTiledAtlas.TileSize;
        var topY = LiveTiledAtlas.TileSize - 1 - localY;
        var offset = checked(topY * tile.Image.Stride + localX * BgraFrame.BytesPerPixel);
        return (
            tile.Image.Pixels[offset + 2],
            tile.Image.Pixels[offset + 1],
            tile.Image.Pixels[offset],
            tile.Image.Pixels[offset + 3]);
    }

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static void EqualRect(StencilRect expected, StencilRect actual, string label)
    {
        Check(expected == actual, $"Expected {label} {expected}; received {actual}.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
