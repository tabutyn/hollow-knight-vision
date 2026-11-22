# First-bench tracking implementation status

Persistent islands, atomic connected-group correction, measured doorway-floor
alignment, immutable image replay and conservative migration are implemented.
The unchanged benchmark has 9,993 frames and fixed reference origins. All
unlocalized errors remain in the scores. No commit or push has been requested.

Selected tracking candidate AP combines the normalized photometric derivative,
bounded independent floor-patch consensus, conservative quality and final-pose
validation. AQ preserves that algorithm and fixes recording export: nonfinite
diagnostic numbers survive as explicit JSON strings instead of losing the entire
recording. The canonical installation has source hash
`c7417f4fd76464e104287278ffaf89d9fad1f4ad0ad653c14bf357fe5769f21b`.

The fixed replay meets the numerical targets: 652 loop-linked measured screenshots
have maximum placement error 9.926 px (none above 12), and the 218 globally placed
camera samples have p95 error 1.247 px. Measured doorway floor mismatch is zero.
Only about 2.2% of camera samples are globally placed; 636 evaluated loop-linked
images remain globally unanchored. No per-island reference refit is used.
All-camera RMS remains 167.843 px and p95 430.031 px. This is not whole-route
global localization. Final AQ validation is complete: three full replays have
identical visual/coarse/world digests and saved-world bytes, including telemetry
removal and altered stored poses. All 1,012 Swift tests pass, with 57 optional
skips; Python diagnostic/scorer tests also pass. Both the fixed and fresh saved
atlases reopen twice with identical tiles/bounds, world and owned ground, leaving
their snapshot and manifest bytes unchanged. These compare two reconstructions,
not an independently captured pre-close framebuffer. Installed source hash,
signature and one canonical Dock pin are verified.

The agreed Mac implementation and validation scope is complete with the coverage
limits below. Evidence and a compact result table are in
[`export-aq/VALIDATION.md`](../artifacts/audits/first-bench-2026-10-31/export-aq/VALIDATION.md).
Disconnected islands remain permitted; no claim of full-route global localization
is made. Windows implementation remains a separate handoff.

A fresh screen-guided traversal now reaches and rests at the bench with changed
timing, detours, deliberate revisits, moving foreground, falls, darkness and a
room transition. No checkpoint or hero-pose replay occurs after recording starts.
The 755.648-second AQ recording saves 41,106 images and all measurement JSON.
Its 154 globally placed camera samples have p95 error 0.489 px; 28 measured
loop-linked screenshots have maximum error 1.851 px, including all 28 globally
unanchored samples. Tutorial camera p95 is 7.030 px across 34,028 exact matches.
The fresh doorway remains provisional: no trusted floor on the first arrival,
and the next saved floor is 11.457 seconds later, outside the evidence window.
This live run does not validate measured doorway alignment; the fixed replay does.

Fresh all-camera RMS/p95 is 1,234.211/3,727.879 px, with large unlocalized Town
errors retained. Town's first telemetry sample precedes its camera initialization;
the next exact joined reference jumps 3,832.076 px. Origins are not refitted to
erase this transient or later drift. The archive reports ten backpressure drops,
zero image failures and at most four pending images. Live capture/tracking rates
are 54.414/50.534 fps; tracking latency p95 is 43.624 ms. One vision job crosses
the recording stop boundary. No fall-physics prediction activates on either route.

Two earlier live attempts missed the platform route; a third reached the bench
but could not export its measurement JSON. Their evidence remains preserved and
excluded from acceptance claims. AQ's explicit nonfinite marker regression fixes
that failure class; the exact field that broke the earlier export is unknown.
Windows algorithm, persistence, export and test requirements are documented below.
No Windows implementation or native validation was performed.

## Fixed development dataset

Input-path store: `~/Library/Application Support/HollowKnightVision/input-paths-v1/`.
Use `path-20261031-193407-96b59c3b-a1bb-4efe-8ee1-3519789d3cf8.json` plus its
`frames-96b59c3b-a1bb-4efe-8ee1-3519789d3cf8/` directory: all 9,993 images saved,
100% frame markers, 9,870/9,870 ground inputs linked. Keep dataset immutable.
Path SHA-256: `bdc1cacbf616a49dfcc5e36b77f35de31cb5c90ff1836305364cb166cfa57980`.
This is a checkpoint replay of the original user input route, not the same rendered
images as the original recording. Original path remains untouched.

Original input route: `path-20261031-184803-a3530920-d4db-4955-864f-864c5c5bfe09.json`.
Local audit and plots: `artifacts/audits/first-bench-2026-10-31/` (ignored generated
artifacts). Runtime/test logs: `/private/tmp/hkv-first-bench-20261031/`.

## Implementation and evaluation history (latest status above)

- Bounded concurrent full-image archive, timestamps, stage inputs, masks/tuning,
  image hashes, explicit missing evidence, legacy compatibility and CLI inspection.
- Fixed diagnostic origins across room/island splits and scene revisits.
- 108 recording/low-resolution/room tests, 77 ground/camera tests, 49 existing
  Python diagnostic tests and four exact screenshot-scoring tests pass.
- Builds use the canonical installer; one Dock pin verified. Live and offline
  replays use isolated worlds under `/private/tmp`.
- Full-image replay processes every archived frame with shared production stages,
  archived masks/tuning and synchronous global searches. It starts cold rather
  than inheriting the live tracker's pre-recording state.
- Baseline offline camera p95: 346.73 px; persisted screenshot p95: 346.73 px.
  9,724 exact camera joins, 1,229 screenshot joins, zero accepted world loops.
  These figures are NOT acceptance success.
- Two complete 9,993-frame runs now have identical visual digests, byte-identical
  world snapshots and identical coarse trajectories after stable group ordering.
  One run removed telemetry/tracking arrays and altered stored coarse poses.
  Digest: `a25e6b337e1a3d69103fcbea477461fab50272dc84b341c2838b6d7a4c78b3e7`.
- Edge fallback passed synthetic bottom-floor tests and all 185 focused Swift
  tests. Full-route camera p95 remains 346.73 px, RMS 222.21 px; saved screenshot
  placement is unchanged. This is a coverage fix, not the global-error solution.
- Island ownership replay `offline-islands-exact-full-b` keeps the same camera
  digest and all 1,229 screenshots: 62 persisted islands, no missing ownership.
  41 retained screenshots belong to the arbitrary initial reference component;
  1,188 remain estimated. Initial-component camera p95 is 11.74 px, still above
  the 6 px target. Unconnected errors remain in the overall score.
- Coarse motion now records measured X/Y independently of velocity-prediction
  requirements. Held intermediate captures cannot claim a fresh measurement.
  Continuity uses exact supported image references, including repeated matches
  to one bootstrap image; a timestamp inside a prediction gap is not evidence.
- Snapshot schema 2 retains immutable local member poses plus island world
  translations. Old snapshots migrate to unknown singleton islands; the original
  schema-1 snapshot is backed up byte-for-byte before the first v2 commit, and
  source PNGs remain unchanged. Disk reopen and source preservation are tested.
- Rigid corrections move entire connected groups, including owned landmarks.
  Partial-island bending, stale revisions and contradictory accepted geometry
  are rejected. A rejected loop preserves the new capture and existing graph.
  Repeated closure confirmation cannot cross unrelated islands.
- The existing global mask is often only eight image pixels high; the matcher
  requires 72 pixels of vertical feature spread at 640-wide solve scale. Its
  saved-world audit found zero matches. An isolated broader-feature audit found
  six supported loops, independently measured constraint errors 0.21–4.92 px,
  and reduced screenshot RMS from 227.72 to 202.44 px. These components remain
  globally estimated. Broad matching is NOT enabled in the live atlas pipeline.
- Added explicit held/transition diagnostics: `offline-visible-transition-full-c`
  reports all 9,993 capture samples (9,965 exact reference joins). Its retained
  screenshot poses are unchanged. The next candidate, `offline-visible-signal-full-d`,
  separates `hasGameplaySignal` from pending room ownership. Near 29.6 seconds,
  error falls from about 116 px to 6.2 px. Tutorial_01 camera RMS falls from
  202.00 to 131.83 px (coverage also expands). However whole-route p95 increases
  from 346.73 to 471.05 px; Town is still not recognized as a separate room.
  This is an intermediate candidate, NOT an acceptance pass. Save all results,
  including regressions. Across all 9,965 exact joins RMS is 207.77 px; 1,230
  screenshots remain, with RMS 216.66 px and p95 471.07 px.
- The current visible-signal candidate also passes telemetry independence: run E
  removes reference arrays and alters recorded poses, yet all three visual/coarse/
  world digests match D and saved worlds are byte-identical. Run E processes
  9,993 frames at 58.06 fps without competing heavy audits (baseline 60.6 fps).
  These are offline rates, excluding display rendering and live capture.
- New image-only loading-cut detection requires sustained complete blackness
  (including HUD disappearance), recent horizontal intent, changed scenery on
  both sides and a stable arrival. It rejects dark gameplay, brief glitches,
  missing captures and same-scene fades. Archived grids produce exactly one cut
  at 151.384 s. The full production run `offline-scene-cut-full-f` separates Town:
  992 screenshots in room 0 and 233 in room 1, no invented portal. It retains
  1,225 screenshots (pending ownership withholds five former admissions).
  Whole-route camera RMS/p95 are 214.11/501.51 px; screenshot RMS/p95 are
  224.21/501.70 px. This is a topology improvement, not an accuracy pass.
  Offline rate 58.93 fps; a short test build overlapped the run.
- Reopening disconnected rooms no longer invents a two-room portal or shifts
  their saved positions. Missing topology imports ownership only. Tests preserve
  schema-1 placements, empty portal sets, unique room IDs and saved/reopened state.
- Pixel-only doorway audit finds repeated floor rows 321 before the loading cut
  and 265 after it (640x360). Equal floor elevation therefore requires measuring
  the camera displacement; equal camera Y is wrong. These are currently candidate
  floor observations, not an accepted horizontal-walking constraint.
- Confirmed, currently visible floor spans now persist in solve-scale image
  coordinates on their owning observations. Prediction-only captures cannot own
  trusted ground geometry. `ownedGroundLines` derives corrected geometry from the
  same snapshot revision as screenshots/landmarks; loop tests and Codable reopen
  verify that ground follows the whole island without changing its source data.
  Live overlays now derive persistent ground from those owned spans. Current-frame
  feature details remain available; old raw-bank world markers are suppressed for
  island worlds so corrections cannot leave stale geometry behind.
  Production run H keeps F's visual/coarse digests exactly, retains all 1,225
  screenshots and saves 7,105 spans across 1,110 observations. It runs at 54.04
  fps offline. Telemetry-free run I produces identical visual/coarse/world digests
  and byte-identical saved geometry, including all ground spans. Ground/model/pipeline tests:
  71 passing (one opt-in audit skipped), plus 29 projection/model tests passing.
- Island coordination now separates immutable solver origins from corrected world
  translations. Tests cover stale/duplicate revisions, corrected old image
  references, prediction gaps and session resets. `applyWorldSnapshot` now updates
  capture and UI projection tables; raw registration coordinates remain unchanged.
  A queued render projects through the latest matching island identity and cannot
  pair new atlas pixels with an older graph. Reset/load clears stale membership.
- Cross-run scoring now pins reference origins to an immutable baseline report.
  Earlier blackout samples must not redefine a scene origin. Use
  `--reference-origins artifacts/audits/first-bench-2026-10-31/reference-origins.json`
  for every new comparison. That report is derived from `offline-edge-full-a`.
- Reports, exact screenshot scoring and plots are saved in the audit directory.
  Candidate runs were concurrent; do not report their rates as isolated speed.
- A spatial-majority prototype was kept out of production: Town parallax can win
  a multi-tile vote while underestimating camera motion. Local agreement alone
  cannot establish ground-plane scale or global confidence.

- A saved-world reopen audit reconstructs H twice through the real atlas pipeline:
  all tile bytes/bounds match, all 1,225 observations and 7,105 ground spans survive,
  original world/manifest bytes remain unchanged. Debug audit: 148.93 seconds.
- Atomic-publication run L processes all 9,993 frames at 53.86 fps offline and
  retains H's byte-identical world (`0a84f595856df1e1a95a3ca0defa6e615a799c4715f84933ab7257239832337a`).
  Reference-free run M matches all three L digests and its saved world byte-for-byte;
  M runs at 52.72 fps with a short test build overlapping. Display-coordinate
  arithmetic differs by at most 4.55e-13 px; fixed-origin error
  scores are unchanged. Tests: 95 projection/UI/pipeline tests (one opt-in skip),
  then 70 composition/pipeline tests (five opt-in skips), no failures.
- Composer transactions stage insertion and whole-world correction together.
  Failed composition never commits a graph; failed persistence restores original
  image objects, revisions, tile ownership, temporal caches and anchor. A later
  successful frame cannot publish pixels from the rejected transaction.
- Coarse timing investigation found older reference images anchored to a newer
  predicted camera pose, then displaced again. New bounded capture-time history
  lets delayed completed solves replace the exact image origin. Three dedicated
  timing tests and 115 existing recording/room/timing tests pass (one opt-in skip).
  Timing candidate N reduces early error, but full-route camera RMS/p95 regresses
  to 217.05/534.62 px; screenshot RMS/p95 is 228.41/534.87 px (1,220 shots).
  Initial-reference camera p95 is 4.99 px over only 209 samples, versus 11.74 px
  over 434 previously; this coverage change is NOT an acceptance pass. Run N
  processes all 9,993 frames at 51.67 fps offline. Source hash:
  `5ab95cf73d0f391b6ba0f7180e97f0f46e03247f598f01bee6c6f4f393c98059`.
  It remains an intermediate candidate, not the validated delivery build.
- N exposes a horizontal recovery deadlock around 10 seconds: the same 3–5
  ground patches still match the trusted anchor, but placement verification
  rejects them once fallback falls more than 16 px behind. Existing sparse
  recovery supports only vertical motion. Candidate O adds three distinct,
  coherent horizontal votes spanning at least 8 px; weak, stationary, opposite,
  stale, duplicate and vertically displaced evidence cannot use that new path.
  46 ground tracker tests pass; 23 placement/recovery/recorder tests pass after
  correcting rejection fixtures to stay outside ordinary continuity tolerance.
  Four capture-time tests, including falling, pass. O's first 1,300 frames show
  recovery restored around 10.2 seconds; the subsequent 75 px offset disappears.
  All 1,298 exact camera joins in this prefix have RMS 4.02 px and p95 5.57 px
  (max 26.56); 146 screenshots have p95 5.71 px (max 24.43). This is a partial
  replay, not final acceptance. Full O retains 1,225 screenshots over all 9,993
  frames at 54.33 fps offline. Camera RMS/p95 is 208.99/458.02 px; screenshot
  RMS/p95 is 218.05/458.12 px, still far outside targets. Large unresolved drift
  starts around 95 seconds and during the Town fall. Initial-reference coverage
  remains 209 camera samples; no accepted loops. Reference-free P matches visual, coarse and world digests exactly; saved world
  bytes are identical. P overlaps the closure audit, so its performance is not
  an isolated comparison. Installed
  source hash: `034f6192ad7224418f546e7d5384c97bc70fe806c34c83260d9048481141fa1d`.

## Remaining acceptance checklist

1. Full-route determinism and telemetry independence are checked for the current
   tracker. Continue repeating these checks after island/solver changes; shared
   production replay and separate fixed-origin scoring are implemented.
2. Complete provisional island rendering/retention and calibrated local-pose
   uncertainty. Ownership, immutable local screenshot/landmark geometry and
   translations already persist; origin uncertainty is distinct from local motion.
   Estimated placement may remain disconnected forever. Room IDs stay separate.
3. Low-resolution spatial consensus always runs; reject moving occluders/blackness,
   use independent axis confidence, fresh measurements over short prediction.
   Calibrate falling separately and model camera following/clamps independently.
4. Repeated visual closure proof moves full confident groups; disconnected graphs
   remain valid. Atomically update pixels, landmarks, ground and connectors without
   ghost placements; reject stale async work. Preserve and conservatively migrate
   legacy saved worlds.
5. Horizontal walking doorways use measured entrance floor elevations, not camera
   heights or decorative connector lines. Keep unsupported connections provisional.
6. Error/uncertainty/source/island/correction plots with fixed references; local
   islands never silently count as globally located. Compare baseline performance.
7. Verify confident camera p95 ≤6 px, connected corrected screenshots ≤12 px,
   horizontal doorway floor mismatch ≤4 px, all at 640×360 reference scale.
8. Tests cover occlusion, complete darkness, falls, camera clamps, false loops,
   doorways, disconnected graphs, whole-island correction, stale work, reopen,
   telemetry independence and bounded processing. Three complete acceptance replays,
   reopen and fresh traversal with different timing, then canonical install.
9. Document Windows algorithm, persistence/migration and native test requirements.

## Evidence guiding implementation

- Original tracker misses a large camera movement during near-black falling at
  ~95 s, then labels locally measured but globally misplaced poses as confident.
- Tutorial_01 to Town telemetry first changes at 150.446 s in the fixed development
  archive; visual room stays 0 throughout. The original input-only route changes
  earlier, so use the archive timestamps for image evaluation.
  No Knight masks were recorded around the doorway (142–152 s). The old room-change
  guard required edge-position Knight evidence, and the doorway is inside a black
  border. The loading-cut path now handles this case without claiming adjacency.
- Separate 30 October calibration has 225 airborne samples near -20.948 units/s.
  Installed game metadata shows a serialized `MAX_FALL_VELOCITY` clamp outside acid;
  its numeric serialized value was not read. Never use player cap as camera cap.

## Earlier implementation seams (before candidates Q–T)

- Island lineage now follows actual image references, not `poseVerified` alone.
  Ground, masked and coarse motion expose their reference capture. Coarse motion
  distinguishes measured/predicted axes and freshness. Continue auditing whether
  those local measurements actually describe the ground plane through parallax;
  nominal image-registration confidence alone is not a calibrated pose bound.
- `LiveAtlasPipeline.processIntegration` currently disables world closure search
  for ground-anchored and transition-bridge poses. Thus local ground confidence
  prevents whole-world corrections even after large accumulated error.
- Do not simply remove that gate: live camera, screenshots and owned ground now
  share corrected island placement, while private registration banks stay raw.
  Portal endpoints still need island ownership; broad visual support must reject
  moving foreground/parallax before world closure search is enabled.
- `LiveWorldTracker.optimize` uses rigid island translations for new data; legacy
  callers retain the old keyframe solver. Measured loop groups move together and
  disconnected groups preserve their estimated placement. Next add weak visual
  associations and measured doorway-height constraints without bending reliable
  internal geometry or using display spacing as a graph constraint.
- Persist island membership and local geometry independently of room IDs. Keep
  prediction-only observations explicitly provisional and out of trusted feature
  geometry. Never turn estimated spacing into a measured graph edge.
- Legacy migration must preserve original snapshot/source data and original
  placements without inferring reliable continuity from old capture epochs.
- The recorded Tutorial doorway has no Knight detections. Its opening is inside
  a black border rather than at the image edge. Room recognition must use visual
  aperture/arrival evidence without requiring a trained Knight detector; a dark
  fall alone must not manufacture a room transition. Floor elevations need real
  measured departure/arrival surfaces, not the current camera-Y equality rule.

## Reproduce isolated island closure evaluation

The opt-in test reads only the visual replay world and immutable masked source
PNGs. It does not read reference telemetry. Broad features are an audit variant,
not the live production mask. Output directory must not already exist.

```sh
HKV_ISLAND_AUDIT_INPUT=/absolute/path/to/visual-replay \
HKV_ISLAND_AUDIT_OUTPUT=/absolute/path/to/new-audit \
HKV_ISLAND_AUDIT_BROAD_FEATURES=1 \
swift test --package-path macos --filter LiveIslandReplayAuditTests

python3 macos/score-visual-replay.py \
  --reference /absolute/path/to/original-recording.json \
  --replay /absolute/path/to/visual-replay \
  --reference-origins artifacts/audits/first-bench-2026-10-31/reference-origins.json \
  --corrected-world /absolute/path/to/new-audit/world-snapshot.json
```

The independent scorer rejects changed raw trajectories or dropped screenshots,
reports every evaluated loop constraint, and never scores cross-scene reference
frames as zero error. Use the optimized test configuration for performance work;
debug audit timings are not live-frame budgets. Profiling found appearance
signatures being recomputed inside sort comparisons; ranking now computes each
signature once for the main ranking while preserving deterministic ordering.
A repeat broad audit produced byte-identical corrected worlds and the same six
closure events after this optimization. Debug audit duration changed from 368 to
284 seconds under different concurrent loads; this is not a controlled benchmark.

## Windows handoff (pending implementation/native validation)

- Port capture-time coarse origin history (`VisualMotionPoseHistory.swift`):
  reference-image displacement starts from that exact capture pose, and delayed
  completed visual solves replace estimates at their own timestamps. Preserve
  the distinction between an estimated origin and measured displacement.
- Port image-only loading-cut detection from `VisualSceneDiscontinuityTracker.swift`.
  A cut creates unconnected ownership, withholds atlas writes while pending and
  resets room-local tracking banks. No Knight detector or telemetry is required.
  Preserve disconnected rooms on reopen; remove two-room placement inference.
- Port `LiveWorldGroundGeometry.swift` and the optional observation field. Confirmed
  observed spans use image-local solve coordinates and follow optimized screenshot
  poses. Keep legacy fields absent; never generate ground evidence for predictions.
  The scorer rejects corrections that rewrite this source geometry.
- Keep solver-local origins independent of world translations as in
  `LiveTrackingIslandCoordinator.applyWorldSnapshot`; stale revisions cannot undo
  a correction. Mac publication now carries immutable island identity and world
  revision through capture/render queues. Port the same stale-result checks.
- Port image/graph commit ordering: stage insertion and full image recomposition,
  commit persistence only after rendering succeeds, roll back pixels and metadata
  on rejection. An externally changed disk graph disables writes until reload;
  never silently pair it with pixels from an older graph.

- Port island ownership/reference lineage to `VisualRoomTransitionTracker.cs`,
  `TransitionMotionBridge.cs` and `WindowsLiveWorldPipeline.cs`. Sampled coarse
  holds and velocity prediction must remain distinct from measured displacement.
- Port schema-3 island/member/translation/uncertainty and measured doorway-floor fields to `LiveWorldModel.cs`
  and conservative original-preserving migration to `LiveWorldSessionStore.cs`.
- Port rigid connected-group correction and rejected-loop rollback to
  `LiveWorldTracker.cs`; do not interpolate new islands by capture epoch.
- Mirror immutable full-image replay, source-only visual evaluation and optional
  island diagnostics in `RecordedInputPath.cs`/`LiveWorldReplayEvaluator.cs`.
- Persist the original optional capture timestamp on every observation, forward
  it through asynchronous requests and preserve it through corrections/Codable.
  Legacy captures retain their old data without inventing a timestamp. Exact live
  screenshot scoring uses this capture identity rather than processing wall time.
- Carry capture generation, island ID and basis revision through asynchronous
  workers. Enable loop publication only once screenshots, ground geometry,
  camera and room connectors share an atomic corrected revision.
- Mirror three-vote horizontal sparse recovery in `GroundPlacementRecovery.cs`:
  use distinct images, coherent measured displacement, bounded time and error;
  do not let stationary or duplicate captures manufacture confirmation.
- Port `GroundPlaneClosureVerifier.swift` to the world worker: broad masked scene
  features propose a loop, independently observed floor pixels refine/verify it.
  Require both verified confirmation votes; differing reference views must share
  the same rigid island. Persist the actual exclusions used by graph features so
  source-manifest validation stays exact. Unowned legacy portals still gate loops.
- Candidate AI: port `LivePatchClosureMatcher.swift` as a fallback after an
  unambiguous feature-search miss. Reuse the existing 32-reference shortlist and
  self-repetition guard. Match up to 80 saved landmark patches on the 160-wide
  normalized grid; no current pose prior. Require at least six distributed
  agreeing patches, 65% motion consensus, and reject competing world placements.
  Cache at most 64 immutable source grids; clear on session change. Independent
  floor verification and two fresh votes remain mandatory. A pending first vote
  permits extra probes inside the usual 0.5-second search gate. AJ keeps these
  probes separate from the periodic clock: inconclusive probes preserve a vote
  only for less than 0.5 seconds in the same measured island. They never count as
  confirmation. Failed scheduled scans and stale/unmeasured/different-island
  probes clear it; verified inconsistent corrections replace it with a new first
  vote. Extra probes must not postpone the regular scan. Port both scheduling
  and negative-evidence regression tests.
- Candidate AI: persist optional `observedFloorCandidates` separately from
  confirmed `groundGeometry`. Keep only measured capture-local detector evidence
  ranges, bounded to 128 spans; these may verify the current image against a
  confirmed source floor but never render owned ground or establish doorway floors.
  Preserve both fields through correction/reopen; old snapshots leave candidates
  absent. Scoring treats both as immutable. Port the actual recorded early-return
  pipeline regression, ambiguity/occlusion/cache tests, and prediction exclusion.
- Mirror `LiveIslandAxisSolver.swift` in the Core world solver. Image loops connect
  X and Y; measured doorway floors connect Y only. Port the tests for whole Y-connected
  groups, unrelated X spacing, reverse edges and conflicting anchors. Mac now persists
  measured floor constraints and carries image-confirmed cuts into the atomic pipeline;
  port `LiveDoorwayFloorEvidence` and its conservative evidence gates. Preserve
  original JSON for both schema 1 and schema 2 before the first schema-3 commit.
  Estimated room spacing is a display translation only, with no X constraint.
- Port `CalibratedSceneMotionTracker.swift`: continuous 160-wide area-averaged
  images, bounded history/patch count, ground-trained per-axis motion gains,
  separated NCC support, ambiguity/boundary rejection and two-vote new-patch
  admission. Ground calibration must use the exact reference pair's projected
  camera-plane displacement, while preserving raw texture displacement for diagnostics.
  Unknown axes cannot authorize rigid 2D island lineage. New patches inherit only
  measured source axes; the second scale vote also requires those measured axes.
  Compute each measured axis's consensus and residual from calibrated voters so
  unknown-scale patches cannot dominate it. Port both replenishment regression tests.
  Candidate X only overrides the established fallback when vertical scale is calibrated.
  Batched Metal scoring is Mac-specific; preserve the CPU algorithm or implement
  equivalent batched compute on Windows, then compare candidate acceptance and
  subpixel displacement against CPU. Do not substitute another similarity metric.
- Port AK's `GroundCameraSolver` scale separation: when adaptive recent/provisional
  matching succeeds at subpixel refinement, use the existing camera-plane anchor
  projection rather than applying the additional integer-phase adjustment. Coarse
  recent matches retain the existing distance-dependent policy; explicit caller
  scales remain authoritative. Port the synthetic pixel regression that reproduced
  16.029 px for a 16.284 px camera-plane movement. No new coefficient is fitted.
  AL's all-frame refinement configuration was rejected for severe fall regression;
  retain the selected keyframe-only refinement policy.
- Port AM's normalized photometric derivative: `GroundSubpixelRegistration.cs`
  has the same missing mean
  and contrast-normalization derivatives as Mac before AM. Port the exposure
  regression before any fix. For normalized residual `g * (t - mean(t)) - s`,
  use `g * (gradient - mean(gradient)) + gainGradient * (t - mean(t))`;
  gainGradient is zero when the gain is clamped. Retain bounds, held-out pixel
  validation and the iteration budget. Keep keyframe-only refinement. AP retains
  this fix and passes its full fixed replay and 1,011-test Swift suite.
  Windows' separate symmetric/consensus refinements are currently enabled via
  experimental `TrackingStepReplay` broad search, not its normal live path.
  Preserve that distinction when planning parity; no Windows runtime was tested.
- Port AP's `GroundIndependentPatchConsensus.swift` after the existing full-search
  ambiguity gate and original weak-pose acceptance gate. Challenge only an already
  accepted coarse candidate: at most 32 distinct 192-pixel source patches and 81
  nearby shifts per patch. Reject ambiguous patch matches and costs above 12;
  require at least four patches, a strict majority and 32-pixel horizontal spread.
  Reverify each patch at the final common median, remove nonfinite/failed patches,
  then recheck majority/count/spread. Preserve current floor-row and long-row gates.
  Unsupported refinement retains the original result. Use minimum old/new support
  and maximum old/new error so selecting a cleaner population cannot inflate
  confidence. Pass verified patches into the existing keyframe subpixel fit.
  Reject nonfinite or negative pose errors for every support count and reference
  promotion. Windows' experimental `GroundPatchConsensus.cs` provides a starting
  point but lacks these final-pose and quality safeguards. Port tests for moving
  foreground, ties, darkness/masks/repetition, duplicate/narrow/invalid support,
  cleaner-subset confidence inflation, a disappearing edge at the common median,
  and invalid quality even with broad support. Do not clamp invalid scores or
  copy experimental broad-search settings into the production path.
- Port `VisualFallMotionPrior.swift` only with its separate calibration/projection
  assumptions, current player evidence, 80 ms expiry, provisional status and
  camera-clamp tests. Never cap camera speed at player terminal speed. Missing
  player evidence must produce no physics camera estimate.
- Port amber dashed provisional-island footprints and cyan owned floor connectors;
  render from a single optimized revision even when reopening without live frames.
  Display estimated room spacing explicitly in every supported language. Bound
  dashed geometry at extreme zoom. Preserve rejected topology before any overwrite.
- Port AQ's diagnostic number encoding contract to `RecordedInputPath` and replay
  reporting: metadata `ieee754-nonfinite-strings-v1`, with explicit strings
  `Infinity`, `-Infinity` and `NaN` for diagnostic values. Decode those values back
  without substituting zero, null or a plausible pose. Keep published-camera and
  world-digest validation strict. Do not globally enable permissive JSON numbers
  for geometry. Emit a counted, indexed diagnostic report; invalid published poses
  must remain rejected by the scorer. Preserve finite-value digest bytes. Test a
  full save/load with finite camera coordinates, nonfinite diagnostic fields,
  events, checkpoint and reference telemetry, plus malformed published coordinates.
  A real fresh Mac capture contains `placeMatchMargin = "Infinity"` and now exports
  successfully. This does not establish which field broke the preceding attempt.
- Run native Windows regressions for false loops, whole-island motion, source
  preservation/reopen, stale work and telemetry independence. No Windows build
  or native runtime validation has been performed for these changes yet.

## Earlier doorway evidence (before loading cuts and owned-ground publication)

- The fixed archive's real Tutorial exit is inside the right black border, around
  image X=430, not the outer 640-pixel window edge. Health/HUD remains visible in
  the first dark passage (~26 s). The actual room load (~150 s) goes fully black,
  including HUD, then shows a different scene with the entrance on the left.
  The evaluation marker strip is excluded from this visual interpretation.
- Arrival floor is around image Y=267; departure floor around Y=323. Equal floor
  elevations therefore require a measured camera-height offset, not equal camera
  Y values. Those approximate image rows are inspection evidence, not constants
  to hardcode into production. Measure the user's actual ground stencils/lines.
- Candidate D preserves the first passage better but later Town motion also
  diverges. The initial world translation differs because the preceding room's
  motion improved; after ~160 s additional divergence appears without a pending
  doorway. Ground/reference banks still mix both game scenes under visual room 0.
  Do not blame the later error solely on the earlier initial-placement shift.
- Next: detect a complete loading blackout/novel arrival without requiring a
  trained Knight object or an outer-window-edge departure. Dark gameplay with
  a visible HUD must not manufacture a new room. Unsupported topology and floor
  links remain provisional; vertical changes are not walking-door constraints.
- For live global corrections, keep raw local odometry distinct from corrected
  world placement. Mapping an island's immutable local geometry to its world
  translation avoids feeding a global correction back as apparent image motion.
  Ground review geometry must have explicit island ownership before publication;
  current private ground banks/semantic lines do not yet provide this boundary.

## Earlier accuracy investigation (before capture-time recovery N/O)

The initial reference component spans 0.203–7.936 s (434 measured samples).
Its earliest error is near zero, then masked registration overshoots around
3.15–3.32 s despite Vision confidence 1: at 3.219 s the error is about
[-22.87, +4.54] px. The coarse estimate is closer during this short event.
`FloorlessPoseCandidateSelector` currently favors the larger directional step;
that can treat foreground movement as stronger evidence. Trial J preferred fresh,
fully measured coarse motion, retaining masked refinement when both agreed. It
failed: reference-component p95 stayed 11.74 px and whole-route p95 worsened from
501.51 to 519.22 px (RMS 217.27 px). The trial is reverted; all results are retained.
The apparent coarse pose in the early interval is not always a fresh, fully
measured pose, so merely preferring fresh samples does not resolve that failure.
Do not count an improved confidence label as an accuracy improvement: report all
samples and changed island ownership with the same fixed reference origins.

For the longer-term fallback, evaluate depth/ground-plane calibration of separated
low-resolution regions against reliable visual ground motion. A naive spatial
majority failed in Town because parallax dominates. Do not calibrate from Hacker
telemetry, and do not infer a camera velocity bound directly from player fall speed.

## Ground verification candidate (not enabled live)

Broad feature matches now support an independent verification callback before
either confirmation vote. The pixel verifier searches only near that proposal,
measures Y from observed floor rows, and requires distributed textured floor
patches to agree on X. Black floors, repeated texture, missing/occluded spans,
wrong parallax displacement, and clipped search optima cannot establish a link.
Six synthetic pixel tests and 18 tracker tests pass, including rejection of a
failed second floor vote and storage of refined displacement. Saved O-world audit checks 1,225 screenshots in 246.18 seconds (debug, concurrent
with reference-free replay), evaluates 45 proposals, verifies 18 floor fits, and
accepts one two-vote loop at 118.27 seconds. Its independently scored displacement
error is 0.52 px. Whole-island correction is (-185.16, -112.44) px; screenshot RMS
falls from 218.05 to 209.46 px, while p95 remains 458.12 px. This isolated audit
cannot claim a live camera improvement or fix the Town fall.

Candidate Q enables the same floor verification in the production pipeline for
owned islands with saved images. HUD/actor exclusions supply broad proposals;
immutable manifest and graph exclusions remain identical. Worlds with old unowned
portals remain gated. Reference votes may use different views of the same rigid
island, but cannot cross unrelated islands. Pipeline persistence/whole-island
correction tests pass; 54 targeted tests include one opt-in skip. A failing
reference-view test used repetitive noise as its image fixture; replacing it with
unique texture makes the dedicated positive/negative regression pass. Slow-load
coverage also passes: continuously observed black loading is retained beyond five
seconds; capture gaps and returning to old scenery still fail room discovery.
Canonical Q build installs successfully (74.10 seconds), verifying one Dock pin;
source hash `d6bf78a9054e0f6c1df5d7e9b39a377425705dd3aac9588ef133d805dc97fbb2`.
Full Q processes all 9,993 frames at 43.39 fps (concurrent with small Python
probes), versus O at 54.33 fps. It retains 1,225 screenshots and accepts four loops
at 98.77, 101.70, 118.42 and 120.75 seconds. Withheld-reference edge errors are
0.077, 0.178, 1.125 and 0.605 px (p95 1.047). Camera RMS/median/p95 is
187.89/8.24/481.08 px; screenshot RMS/median/p95 is 194.58/8.80/481.23 px.
The revisit recovers from about 228 px to 2–5 px around seconds 99–114. All raw
camera fields remain exactly identical to O. Camera/world publication carries
whole-island corrections; no private ground or coarse origin was silently moved.
Town inherits the previous island's estimated display correction (-184.10,
-112.07) px and still has no measured floor connection. Frozen origins are NOT
refitted to hide that placement change or the unresolved fall. Initial-reference
coverage stays 209 camera samples at p95 4.99 px; disconnected segments are
reported separately and are not a localization success. Q reopen passes: both atlas reconstructions have identical tile bytes/bounds,
all 1,225 screenshots and 8,430 ground spans survive, graph/manifest bytes remain
unchanged (173.94 seconds debug with concurrent replay). Reference-free R matches
all three Q digests and saved world bytes exactly, including all four loops.
R runs at 36.75 fps with the reopen audit overlapping; this is not an isolated
performance comparison. This remains an intermediate build, not acceptance.

## Floorless patch experiments (analysis only)

`/private/tmp/hkv-first-bench-20261031/town-plane-probe-v2.py` through `v7.py`
remain outside production. Features calibrated against visible ground, tracked
through successive 160x90 frames, can retain scenery through the Town fall when
sampled at roughly 25–40 Hz and replenished. The uncalibrated-depth variant v6
accumulates roughly 119 px extra Y displacement before landing; current fallback
underestimates by roughly 364 px. Per-axis depth calibration v7 follows only three
background points, then loses support at 157.73 s. Neither is suitable for release:
new-patch admission needs repeated plane evidence, calibration needs uncertainty,
and landing must not snap back to the old drifting ground pose. Player terminal
fall speed cannot be substituted for camera-follow velocity.

## Axis-specific constraints foundation

`LiveIslandAxisSolver` separates horizontal and vertical connectivity. Existing
visual loops still constrain both axes. A future doorway-floor link constrains
Y only; a later loop moves every floor-linked island in Y while X moves only
through measured X links. Fixed anchors and the existing 4 px vector-consistency
gate remain enforced. Seven new solver tests and 23 existing island/tracker tests
pass (30 total). Persistent doorway observations/constraints, detection and
connector publication are not wired yet. Candidate S refactors existing loop
correction through this solver. S completes all 9,993 frames at 43.38 fps and
matches Q/R visual, coarse and world digests; saved world bytes and scores are
identical. Canonical S build takes 73.23 seconds, verifies one installed Dock pin,
and records source hash `0e7e5085cb8c2cd63935e09c8f3e410781a8e4fa16cd8d324ab0cbd3b1c82f7d`.
Q/R/S are three complete reproducible runs of this intermediate algorithm;
repeat final acceptance after remaining doorway/fall/display changes.

## Measured doorway integration (candidate T, validation underway)

Schema 3 retains observation-owned floor endpoints and repeated support IDs. Schema
1/2 imports invent no floor connection; the first newer commit backs up original
JSON bytes. Applying a floor constraint moves the complete Y-connected group, leaves
X layout estimated, and derives connectors from the same optimized snapshot. Later
visual loops preserve these floor connections while independently solving X.

Image-confirmed loading cuts now retain source/target rooms and capture timestamps.
Each atlas request explicitly converts the cut to its durable observation clock.
The floor evidence gate requires recent, distinct measured source/arrival captures,
at least 0.25 seconds of stable floor/camera height, ground on the correct image
sides, at least 48 reference pixels of support, and one unambiguous lower-screen
floor elevation. Falling, prediction-only, stale, ambiguous and missing evidence
remain disconnected. Arrival evidence expires after three seconds.

Ingestion, floor alignment, retained-image recomposition and persistence share one
rollback transaction. A failed commit publishes no partial correction; a following
capture can retry. Queued captures use the corrected island transform while raw
poses and image-local floors remain unchanged. Targeted integration/evidence tests:
50 executed, 48 passed, 2 opt-in audits skipped. Earlier persistence/solver tests:
60 passed; ownership/pipeline regressions: 64 passed, 1 opt-in audit skipped.

Candidate T full replay and connector UI verification pending. Horizontal walking
is conservatively inferred from directional loading plus stable measured floors;
unsupported/vertical transitions must remain provisional. Native Windows execution
has not been performed.

Candidate T completes all 9,993 frames at 43.35 fps offline (overlapping UI/test
compilation; not isolated timing). It retains 1,225 screenshots, 63 islands and
four verified loops. Doorway support IDs 988/989/990 and 992/993/994 independently
measure local floor heights 39 and 95. Optimized camera-height difference is
-56 px; measured floor mismatch is zero versus 37.68 px before alignment. Initial
room spacing remains unlocalized and frozen scoring origins remain unchanged.
T source hash: `b27412d25a76e0d51efcac47ae7b95491d6aa63b93ee99de28a3ba2842b7f86a`.

Provisional display candidate U adds amber dashed island footprints, cyan dashed
owned floor connectors, and an estimated-placement notice in all 11 languages.
These are visible without enabling Debug View and rebuild from a saved snapshot
before live capture. Dashed rendering caps at 64 pieces per line and 4,096 quads
overall. A newly image-separated room gets one estimated horizontal placement
beside the source room; no X edge, confidence promotion or raw pose change occurs.
Later screenshots and future corrections preserve its island ownership. UI/owned
overlay tests: 57 passed; full language catalog: 14 passed; room layout/atomic
pipeline tests: 43 passed, 1 opt-in audit skipped.

Candidate U/reference-free and V/original each complete 9,993 images with all three
digests equal and byte-identical saved worlds. They retain 1,225 screenshots, 63
islands, four loops and one floor constraint. U runs at 43.34 fps, V at 43.36 fps
(with short native prototype work overlapping). Raw observation poses are EXACTLY
unchanged from T; differences are display placement only. The horizontal doorway
connector now runs from (3721.94, 1251.79) to (4159.25, 1251.79), explicitly estimated
in X. U camera RMS/median/p95: 192.60/8.24/450.12 px; saved-shot RMS/median/p95:
199.35/8.80/450.12 px. These fixed-origin scores include the estimated room-spacing
shift and must not be described as improved local tracking. Source hash:
`ce76bf03b9181f07e694a743f8e42c6809963e66df315ccab37933f1434775a5`.

## Calibrated scene fallback (experimental integration, not validated delivery)

Python v10 tracks the entire Town fall using only source pixels and earlier visual
ground solves. Patches learn per-axis image-motion gain from ground; new patches
need two coherent scale observations before joining the vote. Relative added fall
drift is about (-1.11, -13.48) px, compared with +371.89 px vertical drift from the
previous production solver. This remains an isolated experiment on one interval;
its starting room position is unlocalized and no reference origin was refitted.

`CalibratedSceneMotionTracker.swift` ports that experiment with bounded 160-wide
grids/history/patches, contrast-normalized correlation, subpixel gradient refinement,
spatially distributed support and independent X/Y calibration flags. Synthetic
black/repeated/masked/gap/exposure tests pass. Native isolated source-image audit
follows the full fall, ending within one pixel of the Python prototype. This audit
uses frozen earlier visual ground solves, NOT the full new production feedback
loop, and does not read Hacker telemetry. The next mandatory check is production
replay with masks, landing handoff, long-route regressions and bounded performance.

Native audit with archived masks also retains the full fall: 30 estimates, final
pose (3544.86, 606.90), versus unmasked (3541.16, 611.59). These remain isolated
experiments, not production accuracy scores. Native/room/lineage tests: 98 passed,
1 opt-in skip; integration/bridge/timing tests: 24 passed, 1 opt-in skip; recorder
and fallback diagnostics tests: 18 passed, 1 opt-in skip.

Candidate W integrates the fallback into production. Ground-calibrated axes can
override broad-image fallback; an unknown axis retains the existing estimate and
prevents a rigid 2D island lineage claim. Capture-time revision guards rebase the
coarse presentation trajectory, and the masked bridge is anchored to the accepted
current frame. Diagnostics retain per-axis flags, support, residuals and processing
cost. Full-route production feedback, landing behavior and throughput are pending.


## Full fall integration evidence and next candidate

W processes 9,993 frames at 39.60 fps (tests/compilation overlapped), retaining all
1,225 images, 93 islands, two loops and one measured floor link. Camera RMS/p95 is
178.43/450.91 px; retained-image RMS/p95 is 181.66/450.91 px. Town remains explicitly
unlocalized in X. Between 157.224 and 158.600 s, added Y drift is -11.20 px versus
+362.93 px in V. After landing, that improved vertical estimate survives through
the bench. However 0–30 s camera p95 regresses from 5.46 to 7.23 px and two of four
loop closures disappear. This is NOT an acceptance pass. Scenery stage timing:
median 0.162 ms, p95 5.221 ms, max 22.052 ms. Frozen references remain unchanged.

X retains the established horizontal fallback when scene calibration knows only X.
Only calibrated Y can override floorless tracking. A bounded Metal NCC batch
replaces the expensive CPU candidate scoring; shared ambiguity/subpixel checks and
CPU fallback remain. CPU/Metal agreement, black/masked/occluder tests pass; 116
integration tests include one opt-in audit skip. Full X is in progress.

Rejected legacy room topology is now copied byte-for-byte to a unique
`visual-rooms.rejected-*.json` before disconnected import. If preservation or save
fails, later topology writes remain disabled for that loaded session. Duplicate
appearance/edge/portal keys fail validation rather than trapping in dictionary
construction. Topology and doorway tests: 93 passed.

A short player-fall prior is being integrated AFTER X: no telemetry input, frozen
from separate `path-20261030-010714-c00ba92c-697f-4c2d-a15f-7831b4698bbc.json`
(SHA-256 `a0e4b428ebbb3a03ae7ce1dc062ba441c0f1161341301ae364644b15cc7aa31d`).
Calibration is 20.948 units/s and 22.22423 pixels/unit at 640x360. Measured projection
range in that archive is 22.224060–22.224386 pixels/unit. Three measured near-terminal
player displacements and a CURRENT visible Knight are required. Prediction lasts
at most 80 ms and cannot seed itself. Player world motion minus current screen
motion estimates camera displacement, so clamps/lag are not confused with a camera
speed cap. Missing player pixels, different aspect, size changes, landing/jumping
measurements or stale evidence reject it. Any output is provisional display-only,
with an explicit interval recorded; fresh image measurements always take priority.
The interval is a conditional physics/detection tolerance, not calibrated global
localization confidence. Final replay must report how often it actually activates.


## Integrated candidates X/Y (final validation still pending)

X completes all 9,993 frames at 40.27 fps (short tests/compilation overlapped),
restores four loops and keeps one measured floor link. It retains 1,225 shots and
55 islands. Camera RMS/median/p95 is 176.82/9.00/444.88 px; retained shots
179.36/9.35/450.39 px. Added Town-fall Y drift is -11.20 px; the improved Y estimate
survives the remainder of the route. Large X error in Town remains unlocalized
estimated room spacing, NOT a solved global origin.

Y adds the conditional player-fall prior and treats uncalibrated masked/coarse
parallax as provisional. Its raw poses and complete camera scores are EXACTLY X.
This ownership change must never be described as improved camera accuracy.
Y retains all 1,225 shots, 95 islands, four loops and one measured floor constraint.
Retained-shot RMS is 179.77 px (slightly worse because provisional captures do not
inherit an unproven rigid correction). It runs at 41.56 fps without concurrent
build/test work. Scene stage median/p95/max is 0.160/4.526/15.519 ms; ground stage
2.104/6.340/41.611 ms. No player-physics prediction activates in this archive:
the required current Knight evidence is absent. The actual fall improvement is
from image tracking, not the physics prior.

Y globally referenced coverage is only 123 camera samples (p95 0.876 px) and two
retained shots (max 0.591 px). This limited coverage is explicitly NOT evidence that
the whole route meets the targets. Four loop edges have p95 1.047 px/max 1.125 px
relative displacement error. The unanchored loop-connected groups contain 314
measured retained shots, with max absolute fixed-origin error 14.53 px near
123.62 s. These groups remain estimated globally; retain this limitation in all
acceptance reports. The current 12 px absolute target is not established for them.
Y source hash `908a17c1564e2d14c1dfabb3e28bb8ff69f4ad946436cf8ec529bd338c87bb3a`.
Canonical install takes 78.67 s and verifies one Dock pin.

Physics/GPU/recorder targeted tests: 22 passed, one opt-in skip. Island/bridge/world
pipeline tests: 82 passed, two opt-in skips. Scorer tests: nine passed. Full Mac
suite, final identical-input repeats, saved-world reopen and changed-timing live
traversal remain pending. Runtime version tags and a physics-diagnostic round-trip
test have changed since Y; the next canonical build includes them.


Full Mac suite passed: 1,041 XCTest cases, 51 optional skips, zero failures
(311.99 s), plus one Swift Testing case passed. Thus 991 tests actually ran and
passed. Final review then excluded physics-only display predictions from the
separate ground-reference cache; targeted prediction/recorder/reference tests
are running. This guard cannot affect the fixed route, which has zero physics
predictions, but must be included in the final canonical binary and live trial.

`owned-result-audit.json` for Y confirms all 1,225 screenshots have ownership;
measured doorway mismatch is 0 px with camera-height difference -56 px. All six
support captures are measured. Unanchored loop-linked screenshot RMS/p95/max is
7.643/13.283/14.530 px; 39/314 exceed 12 px. No reference origin was changed.


## Final fixed-image validation and live integration (Z)

Three complete runs each processed all 9,993 frames. Visual/coarse/world digests
and saved world-snapshot bytes match exactly. Z2 removed all reference telemetry
and changed stored poses; output is unchanged. Throughput was 41.55, 37.00 and
35.88 fps. Z2 overlapped native tests/reopen; the later slowdown has no established
cause. These offline rates exclude rendering and do not establish live throughput.
Scores and ownership are unchanged from Y, including all reported unresolved errors.

The final physics reference-cache guard passes 41 targeted tests. Saved-atlas
reopen plus viewport tests pass 34 cases: 1,225 screenshots and 8,380 ground spans
survive; two independent reopen reconstructions have identical tile pixels/bounds,
and the saved snapshot/manifest bytes are unchanged. This compares two reopens,
not a hash of the original live framebuffer. Transaction tests separately cover
repaint and rollback.

Viewport connectors/outlines now clip to the visible viewport before bounded dash
tessellation. Offscreen provisional islands cannot consume the line budget and
hide a visible connector. The canonical build installed this rendering change in
74.34 s and verified one Dock pin. Tracking-repeat source hash was
`77607c3d340fedaa0f73ca8c705923a0d27ad5fe1314bec9fcb46269ee6f99e7`;
installed live-validation source hash, including clipping, is
`058c427c6c15c44e50afea5d425d32975c34689e43c9c539c1c3b283ce82ee3f`.

Fresh live validation is in progress with variant
`path-20261101-000423-6c7e592e-9342-4af1-b4cf-8b61262cc9e7.json`.
It preserves input hold lengths and checkpoint, adds an initial 0.37 s delay and
four grounded idle pauses (0.17/0.23/0.19/0.13 s), and carries no prior reference
or visual pose arrays. Existing saves were backed up before checkpoint restore;
the live atlas uses an isolated temporary workspace. Old Python/HKV crash dialogs
were dismissed after their timestamps established they predated this live run.
No fresh crash was observed at the start of recording.

Evidence is retained under `artifacts/audits/first-bench-2026-10-31/final-z/`.
Acceptance remains unresolved: only 123 camera samples are globally referenced;
39 of 314 measured screenshots in globally unanchored loop groups exceed 12 px.
Neither tiny reference coverage nor relabeling uncertain motion proves accuracy.


## Fresh traversal attempt 1: complete capture, incomplete route

Fresh archive `path-20261101-003950-8a566562-01c5-428c-80d5-31443a075ebc.json`
contains 10,439 lossless frames over 186.42 s: 56.00 capture fps, zero dropped/failed
archive images, peak pending images three. Vision started 10,290 and completed
10,289 samples before the recording closed (one in flight). Capture-receipt age
p95/max is 0.91/40.22 ms; tracking publication latency p95/max is 30.73/74.03 ms.
Two processed capture gaps exceed 100 ms; no growing capture/write queue appears
in this run. Ground stage p95/max 6.12/18.22 ms; scenery 6.60/15.89 ms.

The input-only timing variant missed platform jumps and stayed in Tutorial_01;
it did not reach the doorway or bench. Thus it is NOT the required complete live
validation. All 10,283 exact reference matches remain reported: camera RMS/p95
189.73/273.06 px. Only 211 samples have a globally referenced measured island,
p95 0.903 px; there are no accepted world loops or doorway floor links. This is
not an accuracy pass. Scheduled input delivery median delay was 4.24 ms, maximum
15.06 ms; two expected button transitions were absent from the recorded output.
Do not infer their cause or blame tracking without evidence.

Live saved screenshots had wall-clock observation time, not exact capture uptime;
therefore their exact-frame placement errors are unavailable. The next build adds
optional immutable `captureTimestamp` to each world observation, forwards the
capture request's actual timestamp, and preserves it through island corrections
and reopening. Old snapshots decode it as absent; no guessed time is promoted to
exact evidence. Windows parity must preserve this optional field too. Ten scorer
tests pass. Native persistence/correction coverage passes after using a numerical
tolerance for subtraction between clocks (49 passed, one optional skip in the
rerun; the other 17 selected cases already passed).

Second input variant `path-20261101-004657-d93fe120-ed69-4782-9fa6-03f8af76082d.json`
adds only a 1.13 s initial wait, preserving all action intervals. No prior pose or
telemetry arrays are supplied to playback. This is a smaller timing perturbation
than attempt 1; neither variant is allowed to borrow saved hero poses.


## Fresh traversal attempt 2: exact live screenshot evaluation

The second launch initially remained in the game's pause menu and preparation
correctly timed out. After resuming, iteration 5 restored once and recorded
`path-20261101-005033-ea1b9509-ff62-467e-ae3a-4e53925f44ac.json` for 186.43 s.
10,440 frames saved, zero archive drops/failures, peak queue three. Capture rate
56.00 fps; tracking 55.75 fps. All 10,393 started vision frames completed. Publication
latency median/p95/max 13.75/27.02/70.24 ms. No player-fall prediction activates.
No UI interaction was sent during this playback.

The route again missed the platform jumps and remained in Tutorial_01, so it
cannot satisfy doorway/fall-to-bench live validation. 10,391 exact camera joins:
RMS/p95/max 48.04/82.28/84.16 px. All 891 screenshots captured during the recording
join their exact image timestamps (zero missing joins); 104 stored captures were
outside the recording and are explicitly excluded. Retained placement RMS/p95/max
34.04/38.24/82.48 px. Four loop edges have RMS/max relative displacement error
0.296/0.435 px. However all 425 measured retained shots in those unanchored loop
groups remain over 12 px absolute error (max 38.76 px). This is NOT an accuracy pass.
Globally referenced coverage is 188 camera samples, p95 0.601 px, and 24 retained
shots, max 1.214 px. Do not substitute this small subset for overall quality.

The current production solver already refines keyframes. An attempted all-frame
subpixel comparison was rejected by the launcher's retired-option guard; no such
experiment ran and no result is claimed. The unused argument plumbing was removed.
A new image-only candidate audit checks whether pooling estimated island origins
makes the appearance matcher reject otherwise floor-verified proposals. It changes
no production selection, accepts no edges and does not modify the source world.
Fresh manual K recording was requested asynchronously; independent work continues
against existing recordings. No commit or push has been performed.


## Candidate ambiguity audit and projected-ground calibration

The optimized candidate audit passed in 3.296 s (the slow debug run was interrupted,
not an assertion failure). Of 164 retained ground-bearing references, 116 pooled
queries rejected a match. Partitioning references by island exposed only two extra
floor-verified proposals, both already in the same accepted connected component.
Their independent displacement errors were 0.405 and 0.605 px. No additional
component connection was found; production ambiguity rules remain unchanged.

A separate code inspection found a real calibration unit mismatch. Ground image
matching already scales its raw displacement into camera-plane units, but scenery
patches learned their scale from the raw displacement. The next candidate forwards
`projectedImageDisplacement` for that exact reference pair into scenery calibration;
raw displacement stays available for diagnostics. This does not change ground
camera solving itself or use any reference telemetry. A synthetic unequal-axis
scale test distinguishes the two quantities. Targeted tests/build/replay are pending;
no improvement is claimed yet. Horizontal-only fallback priority is unchanged.

Latest completed canonical build before this candidate: 77.13 s, one Dock pin,
source hash `710c02a2d48bdbbc4e386a60737465405e9875fbe26167ced94f58e3855d4167`.


## Projected calibration candidate AD: mixed result, not acceptance

80 targeted tests passed with one optional skip (81 cases). Canonical install
completed in 76.36 s, one Dock pin; source hash
`463275dfbd9cf5dff3150c78994216d99ae1eb8cea2547cc7ee99040c0602a57`.
Full replay processes all 9,993 frames at 41.89 fps, retaining all 1,225 screenshots,
99 islands, four loops and one measured doorway floor constraint. Exact capture
identity now persists in every observation. Reference origins remain unchanged.

Camera RMS/median/p95 is 183.47/8.99/443.31 px, versus Z1 176.82/9.00/444.88.
Retained-image RMS/p95 is 187.55/450.36 versus 179.77/450.39. Thus overall RMS
regresses despite a slightly lower p95. The large Town fall's added Y error
improves from about -11.20 to -7.88 px; later unlocalized Town X drift changes
unfavorably. The correction is dimensionally justified, but it is NOT established
as an overall tracking improvement. It remains an installed candidate under
investigation, not a validated final release.

The loop-linked first-room group score is exactly unchanged: 314 measured shots,
p95 13.283 px, max 14.530 px, 39 over 12 px. Measured doorway floor mismatch remains
0 px; camera-height difference remains -56 px. No player physics prediction activates.
Previous three deterministic repeats/reopen describe Z, not this changed candidate.
A final winner still requires those checks and a complete changed-timing live route.

Next investigation should examine calibration baseline/scale reliability and
horizontal fallback continuity. Do not weaken ambiguity gates or align references
to hide the remaining error. Both input-only fresh route attempts and their exact
coverage/errors remain recorded above. Manual fresh recording request is pending.


## Rejected all-frame ground refinement AE

The production constructor was temporarily changed to refine every accepted ground
match. No retired CLI option was restored. Canonical build hash was
`4af8bb5fbcda98081284df2c7f67209bfc6cb8fc7861a9e104ad049bbf403703`.
All 9,993 frames replayed, but camera RMS rose from AD's 183.47 to 205.77 px;
retained-image RMS rose from 187.55 to 211.45 px. Only one loop remained, versus four.
All 146 evaluated loop-group screenshots exceeded 12 px, with a maximum of 20.65 px.
Added Town-fall Y error worsened from -7.88 to -15.16 px. The experiment was reverted.

A post-hoc pair audit found lower per-match errors on many refined pairs, but that
local result did not predict the full trajectory or closure recovery. It is not
an acceptance substitute. Replay ran at 35.81 fps while a release test build was
also running; this is not a clean performance comparison with AD.
Evidence is under `all-frame-subpixel-ae/` in the ignored audit directory.

## Reproduced calibration-confidence failures, AF and AG

A new synthetic replenishment test calibrates only X or only Y, then tracks many
replacement generations without further ground input. Previously it fabricated the
missing calibration after five/six updates: 39 assertions failed. AF makes replacement
patches inherit only the source's measured axes. The fixed focused suite passed
39 tests with one optional audit skip. Canonical AF source hash:
`577c8a896b456bbea1ca87e582d588fabaebb2e2e80d9bab70b60f41b4cee140`.

AF's full replay retains all 1,225 screenshots, four accurate loops (maximum edge
error 1.125 px) and one zero-mismatch measured doorway floor. There are 412 evaluated
measured screenshots in globally unanchored loop groups: RMS 6.863, p95 12.862,
maximum 14.635 px; 44 exceed 12 px. Overall camera RMS/p95 is 200.15/488.15 px and
image RMS/p95 is 202.99/488.20 px. Added Town-fall Y error remains -7.879 px.
Confidence semantics are corrected, but overall accuracy still regresses from AD.
The 33.34 fps run overlapped a release test build, so its speed needs isolated recheck.

A second synthetic test gives scenery a known X gain of 1.2, then moves vertically
until replacement patches with unknown X scale dominate. A real 10 px camera step
was reported as 12 px while X remained marked measured. AG fixes this by using
calibrated voters for each measured axis and its residual. An unknown source axis
also cannot provide the second confirmation vote for a pending calibrated patch.
Both reproduced failures now pass: 40 focused tests passed, one optional audit skipped.
Canonical AG source hash:
`35ffe0caccfb8579317ff8780a13e183f1b65c5fe32edfc534bfa946a69e0413`.
AG full replay and accuracy comparison are still in progress at this entry.

AF first diverges from AD at 47.923 s: honest X confidence changes from true to false,
which makes integration choose coarse X 682.732 instead of scenery X 688.146.
The next capture then loses its ground solve. A follow-up worth measuring is to keep
scenery's best provisional X when Y is calibrated, while retaining false X confidence
and preventing rigid island/ground admission. Another is preserving prior calibrated
axes through actual patch correspondences during recalibration. Neither hypothesis
is implemented or validated here; never infer an unknown scale from patch proximity.


## AG complete, provisional-X experiment AH rejected

AG completed all 9,993 frames at 38.106 fps without an overlapping test build.
It retains 1,225 screenshots, 92 islands, four loops and one measured floor link.
Camera RMS/median/p95 is 200.140/11.280/488.147 px; screenshot RMS/p95 is
202.972/488.201 px. Loop-group error remains AF's 412 images, p95 12.862,
maximum 14.635 px, with 44 over 12 px. The four loop edges themselves remain
accurate within 1.125 px. Measured doorway floor mismatch is zero, with distinct
camera heights separated by 56 px. No player-fall prediction activates.

An exact persisted-confidence audit found 53 scenery-selected captures, of which
24 lacked at least one calibrated axis. All 24 retain unmeasured island ownership.
The four retained screenshots from those captures remain unmeasured and carry no
trusted ground geometry. This verifies the confidence fix through production output.

AH kept scenery's provisional X while retaining honest axis flags. It also processed
9,993 frames, at 38.018 fps. Camera RMS increased to 201.229 px and screenshot RMS
to 204.211 px, although their p95 values improved to 480.584/480.465 px. Loop-group
maximum/over-target count and the Town fall's -7.879 px added Y error did not improve.
Its confidence audit also passed: 24 captures/four retained images stayed unmeasured.
The mixed result gives no improvement on the unresolved connected-image target,
so AH was reverted and AG selected for continued development. AG is not an acceptance
pass, and still needs accuracy work before final three-run/reopen/fresh-live validation.

A new read-only probe inspects a missed early return: source observation 0 at 2.22 s
and observations 46/47 at 8.80/8.95 s show nearly the same ground/camera view with
different player lighting. The source has 19 stored landmarks. A Python patch-NCC
probe finds 9/10 consistent existing landmark patch locations (zero grid displacement,
wide X/Y support), while raw descriptor reciprocal matching finds only two coherent
corner pairs for observation 46. Normalizing descriptors alone raises that to four,
still below the six-point requirement. These are diagnostic proposals, not edges.
The native optional `SameViewClosureAuditTests` must apply real exclusions and the
independent floor verifier before any production change. Never lower ambiguity or
spread requirements merely to accept this pair.


## Native missed-return audit: evidence exists, recovery still pending

The optional native audit passed in 0.396 s, preserving the saved world. With actual
masks and the production patch matcher, source observation 0 matches observations
46 and 47 with eight coherent existing landmark patches each. Their 160-grid spans
are 61x36 and 48x36, mean NCC 0.952/0.950. Unrelated probes at observations 13, 42,
132, 343 and 622 produce no patch matches. However 46/47 have zero retained confirmed
ground spans, so the current production closure verifier has no current-floor input.

A second native audit replays the exact archived floor detector inputs (pixels,
exclusions, Knight mask and tuning), decoding only recording identity/archive metadata.
It passed in 0.623 s. Both return frames have three raw observed floor spans; the
unchanged floor-pixel verifier accepts each with seven supporting patches. Source
observation 3 has a strong scenery match but still fails floor verification, so broad
appearance alone does not bypass the independent gate. No candidate geometry was
persisted and no graph edge was accepted.

Post-hoc fixed-reference errors for the two return proposals are 0.403 and 0.062 px.
The scoring script receives reference telemetry separately; the native matcher does
not. Reference origin hash remains
`a1b1b3f03a8e27549b01172001a7140853fa491ff0de3cd6cf1523466b243a1d`.
Evidence is in `same-view-return-audit/` in the ignored audit directory.

Next implementation should evaluate a bounded contrast-normalized patch proposal
using existing source landmark pixels, plus separately carried raw observed floor
candidates for closure verification. These candidates must remain distinct from
confirmed owned atlas ground. Preserve independent floor pixel verification, two
fresh confirmation votes, reference ambiguity checks and stale-result handling;
never use estimated-pose proximity as evidence. This could reconnect the initial
reference island, but that outcome has not been implemented or measured yet.

Canonical app was restored to AG, source hash
`35ffe0caccfb8579317ff8780a13e183f1b65c5fe32edfc534bfa946a69e0413`,
with one Dock pin verified. Runtime source has no unbuilt edits; the new optional
audit tests were subsequently compiled and run. Game remains paused and GUI closed.
All current accuracy limits and the pending complete fresh live route still apply.

## Dense return recovery candidate AI (evaluation pending)

The native same-view audit exposed a genuine evidence handoff failure: source 0
has confirmed floor, and captures 46/47 have independent current floor pixels,
but no retained confirmed ground after reacquisition. `observedFloorCandidates`
now carries those measured detections separately. It never contributes owned
geometry or doorway constraints. The source still requires confirmed floor.

`LivePatchClosureMatcher` proposes near-view returns from saved landmark pixels
using the shared contrast-normalized NCC engine. It preserves the original
feature shortlisting/self-repetition and cross-location ambiguity guards, has no
current pose prior, and cannot accept an edge itself. The existing floor verifier
and two-observation rigid-island correction path remain authoritative. Pending
votes can be confirmed on the next capture rather than waiting 0.5 seconds past
a short revisit. No telemetry is consulted.

Focused release tests: 74 discovered, 73 passed, one optional audit skipped, zero
failures. Recorded pipeline test and full unchanged route will determine whether
this candidate improves accuracy and whether its additional query cost is viable.
Canonical installed version is still AG at this entry. No acceptance claim yet.

## AI full-route results

Installed source hash: `5d4bcc94ec5811013271d3ebaff6b878e9062b2a4147e171613d951e6e937bc1`.
Canonical installer verified one Dock pin. All 9,993 frames completed at 36.353 fps
without an overlapping test build (AG 38.106 fps). There are 1,225 retained captures,
92 islands, eight accepted image loops and one measured doorway-floor constraint.
Every accepted loop has fixed-reference displacement error below 0.591 px; the
missed early return is recovered by source 0 to target 47, error 0.062 px.

The recorded production regression confirms two fresh floor-verified votes inside
0.5 seconds, rigid movement of the island, unchanged raw poses, no raw-detection
promotion to owned ground, and equal disk reload. Omitting current raw candidates
prevents that connection. It passes in 0.760 seconds.

447 measured loop-linked screenshots have p95 12.199 px, max 13.788 px, and 28 over
12 px (AG: 412 shots, p95 12.862, max 14.635, 44 over 12). Only 19 of these have
a global reference; 428 remain globally unanchored and all their absolute errors
are retained. Camera RMS rises slightly to 202.445 from 200.140; screenshot RMS
is essentially unchanged at 202.978 versus 202.972. Whole-route camera p95 remains
487.925 px: this is not an overall accuracy pass.

Doorway floor mismatch remains zero with all six supports measured, while camera
Y differs by -56 px and X spacing remains estimated. Unknown-axis scene captures
remain unmeasured and cannot supply trusted ground (53 scene outputs, 24 with an
unknown axis, four retained unknown-axis captures). Physics predictions remain zero.
Ground/scene p95 processing is 6.100/4.417 ms. Full live integration is still pending.

AI missed an earlier useful loop present in AG. Audit confirmed current floor
versus raw detections before selecting a final evidence-priority policy. Evidence
is retained in `dense-closure-ai/`; no reference origin was refitted.

## Bounded confirmation probes AJ (full evaluation running)

A source-only world-matcher replay reproduces all eight AI edges exactly. It
shows why an older useful loop disappeared: frame 640 verifies a first vote;
641 selects a different appearance match whose floor fails, clearing that vote
and shifting the regular search time. The pattern repeats at 645/646. A fixed
0.5-second cadence recovers 171→644 then 146→648, but misses the short early return.

Confirmed-first floor evidence does not resolve this scheduling failure. Separate
original-image audits find no valid alternate source 171 match at 641, and no
verified 171/146/153 alternative at 646. These negative results are preserved.

AJ retains one pending vote on an inconclusive extra probe only when the probe
is less than 0.5 seconds old and belongs to the same measured island. A probe
never counts as verification. Scheduled failures, expired probes, unmeasured or
different ownership still clear the vote. A verified contradictory correction
replaces it and needs a new second vote. Extra probes do not move the periodic
query clock. No ambiguity or pixel-support threshold was lowered.

75 focused tests pass; one optional audit skips. A source-only atlas diagnostic
recovers nine edges including 171→643, 146→649, and the earlier 0→47 return. Its
separate reference check finds no edge displacement error above 0.57 pixels.
The recorded early-return pipeline regression still passes. These diagnostics
freeze upstream tracking and are not a substitute for a complete production replay.

Canonical AJ source hash: `6e8748cab7d80c3b1b195b086299f6289b8fc07f3224623220b411fb5af68a69`.
The full unchanged route is running; no final accuracy claim yet.

## AJ complete; boundary references next

The full production replay reproduces all nine audited edges, each within 0.562 px
of the independently measured displacement. 9,993 frames complete at 36.135 fps,
with 1,225 retained images, 92 islands and one measured floor link. No other test
build ran during this replay. Ground/scene processing p95 is 6.151/4.458 ms.

464 measured loop-linked screenshots have RMS 6.841 px, p95 11.842 px, max 13.435 px,
and 20 above 12 px. Of these, 445 remain globally unanchored; their errors stay in
the score. AI had 28 over 12 and AG had 44. Camera RMS is 199.572 px (AI 202.445,
AG 200.140); retained-image RMS is 202.900 (AI 202.978, AG 202.972). Whole-route
camera p95 remains 487.819 px. Only 242 globally placed camera samples are eligible
for the confident score; their p95 is 1.164 px. This limited coverage is explicit.

Doorway floor mismatch stays zero, camera Y differs by -56 px, X is estimated,
and all six supporting captures are measured. Unknown-axis scene ownership and
trusted-floor exclusion pass unchanged. No physics prediction activates.

AJ is selected for continued development, not final acceptance. Remaining errors
include first-pass drift within the island spanning captures 343–427 and its later
return. Some last reliable captures before a break (e.g. 342) are retained as PNGs
but omitted from the sparse landmark bank. Source-only pair audits will check
whether those images provide valid two-vote bridge evidence before changing
keyframe admission. Do not force a connection or use telemetry to choose a pose.

Three final deterministic repeats, final saved-atlas reopen and a complete fresh
live traversal remain required after the tracking solution settles.

## Boundary hypotheses rejected; refined scale AK selected

The final measured PNG before an island break sometimes verifies the first new
capture, but does not supply a second verified observation. Extra tail views and
current floor-row perturbations within ±1 pixel also fail to provide the missing
second vote at 342→344. Known negative returns remain rejected. No new keyframe
admission, floor threshold or floor-row refinement was shipped on this evidence.
These negative audits are retained in `boundary-negative-audits/`.

A separate diagnostic found a systematic bias in refined recent ground motion.
The recent-motion adjustment includes an integer-phase component even when the
match has already been refined to subpixel precision. A synthetic pixel regression
reproduces 16.028703 px for a 16.28356 px camera-plane movement; explicit custom
calibration passes. AK retains the existing camera-plane projection for refined
adaptive recent/provisional matches and retains the old coarse policy otherwise.
No coefficient is changed. The regression passes after the correction, together
with 57 other focused tests (one optional audit skipped).

Canonical AK source hash: `3fc15ce730dc2c6af5c2b8270cc0636d3648cf4ca37c60c937ec8926ac23b63d`.
The full route completes all 9,993 frames at 35.801 fps without overlapping builds,
retaining 1,225 screenshots in 81 islands. Ten loops all have independently scored
displacement error ≤0.529 px. Camera RMS/median/p95 is 168.885/5.316/442.097 px,
versus AJ 199.572/10.768/487.819. Screenshot RMS is 171.709 versus AJ 202.900.

631 measured loop-linked screenshots have RMS 5.927 px, p95 9.806 px and max
12.354 px. Three exceed 12: captures 498, 832 and 840. 612 of the 631 are still
globally unanchored; all their absolute errors are included. The globally placed
camera subset remains only 242 samples, p95 1.164 px. Unlocalized errors remain
large and visible. The measured doorway floor mismatch stays zero with camera Y
still -56 px apart; X spacing is estimated and all six supporting samples measured.

Scene-confidence audit passes: 69 scene outputs, 38 unknown-axis outputs, six
retained unknown-axis screenshots all unmeasured with no trusted ground. Physics
predictions remain zero. Ground/scene processing p95 is 6.281/4.252 ms. A fresh
complete changed-timing live traversal remains outstanding.

All three remaining outliers used integer motion estimates (498 recent, 832 anchor,
840 recent). AL therefore retests all-frame subpixel refinement with AK's scale
composition fixed. This differs from rejected AE, which still applied the recent
integer-phase adjustment after refinement. Preserve AK as the rollback and decide
using the complete fixed route. Three final repeats and saved-atlas reopen remain
pending after the winner is selected.

## AL rejected: intermediate-frame refinement changes state

The full AL run retains 548 evaluated connected screenshots with max error 8.761 px,
but reduces coverage from AK's 631. Overall camera RMS worsens from 168.885 to
204.636 px. Added vertical error during the fall rises from -7.879 to +363.466 px;
Town camera RMS rises from 401.603 to 475.113 px. All 9,993 frames were evaluated
with the same frozen origins. Thirteen loop edges remain individually accurate
(max 1.172 px), and measured doorway floor mismatch stays zero, but these local
metrics do not justify the route regression. Exact AK runtime files restored.
All-frame subpixel refinement is not the selected default. Evidence: `all-refined-al/`.

## Capture-local refinement diagnostic and normalized derivative regression

An optional image-only audit reconstructed ground patches from archived raw floor
rows for 969 integer capture pairs. Fourteen reference pairs lack archived ground
inputs and are explicitly skipped; 529 produce independent refined matches. This
is a diagnostic using raw rows, not a replay of the stabilized solver reference
bank. Capture 832 would improve from 12.051 to 11.630 px, but 840 worsens from
12.354 to 12.511 px; 498 has no usable reconstructed patches. No capture-local pose
adjustment was added to production. Data retained in `capture-refinement-diagnostic/`.

A separate synthetic pixel test reproduces a normalization-derivative bug. The
photometric residual subtracts patch means and normalizes contrast, but its old
Jacobian differentiates neither operation. At three exposure gains (0.55, 1, 1.3),
known 0.7 px vertical movement is reported as 0.513, 0.506 and 0.511 px. The native
regression fails all three assertions before the correction. Candidate AM includes
the mean-gradient and gain-gradient terms, with zero gain derivative when clamped.
It keeps the existing camera projection, bounds, six-iteration budget and keyframe
refinement cadence. Focused tests/full fixed replay are pending; AK remains selected.

## AM complete: corrected derivative exposes a longer connected drift segment

AM source hash: `5d24a67cb8c11d5ce5f0a15d6b2693d694b7d37d27cbedf259ff979ad97ab749`.
Canonical build/install succeeds in 78.18 s with one Dock pin. All 9,993 unchanged
frames finish at 34.683 fps offline. The 59 focused tests pass; one optional audit
is skipped. Camera RMS/median/p95: 168.214/6.148/438.857 px. Screenshot RMS:
170.299 px. The measured doorway has zero floor mismatch, camera Y differs by
-56 px and all six supporting captures remain measured. Fourteen loop edges have
independent error <=0.585 px. No new false loop is indicated by these measurements.

637 measured loop-linked screenshots have RMS 6.659 px, p95 10.491 px, maximum
19.957 px; 29 exceed 12 and 618 remain globally unanchored. The comparison must
account for changed coverage: all 603 common linked screenshots improve from
RMS 5.947/max 12.354/three over target to RMS 5.490/max 11.576/zero over target.
The 34 newly linked screenshots already had RMS 17.334 and maximum 20.672 in AK;
AM gives RMS 17.214 and maximum 19.957. Twenty-nine of these were already over
12 in AK, but were disconnected then. Twenty-eight previously linked screenshots
lose that link; their errors improve from RMS 5.473 to 4.351 but they are still
reported as disconnected. These coverage differences cannot be hidden by either
aggregate metric. The acceptance target is still unmet.

All new over-target images belong to island 55, which now carries continuity
farther into the 78-83 s moving-foreground section. Capture 498 improves to 11.576,
832 to 8.084 and 840 to 10.525 px. Added vertical error during the Town fall stays
-7.878 px. Ground/scene p95 costs are 6.312/4.271 ms; physics fallback still has
zero activations. The 242 globally placed camera samples have p95 1.164 px, so
this is limited coverage, not full-route global localization. Confidence audit:
59 scene outputs, 20 unknown-axis outputs, four retained unknown-axis captures
all unmeasured with no trusted ground.

AM is the current working candidate because its corrected math improves the
common connected images and overall camera RMS while retaining accurate loops.
No final acceptance claim is made. Next diagnosis: independent floor-patch motion
consensus in the newly connected drifting section. Source-pair checks reveal
coarse matches several pixels faster than the reference camera (for example a
69 px image match versus 64.037 px true movement around 75.986 s). This is an
evaluation observation, never a hardcoded correction or new fitted coefficient.
The Windows experimental coarse patch-consensus algorithm is a possible bounded
image-only diagnostic; do not blindly enable its broader replay tracking policy.

## Independent coarse floor-patch diagnostic (not yet production)

A test-only port of the Windows experimental `GroundPatchConsensus` checks each
patch within a bounded ±4 px neighborhood, rejects ambiguous individual matches,
and requires at least four spatially spread patches forming a strict majority.
The immutable archive supplies pixels/masks/raw floor rows; telemetry is joined
only afterward for scoring. The 764-pair 70-85 s diagnostic completes in 7.190 s
and passes; 620 pairs verify. On those pairs, projected displacement RMS changes
from 0.465 to 0.452 px and maximum from 4.485 to 1.537 px. The 505 verified coarse
pairs improve RMS from 0.482 to 0.438. The 115 refined pairs become coarser in
this diagnostic, worsening RMS from 0.384 to 0.509: any integration must retain
fine refinement after choosing the independent patch population.

At 75.986 s, eight of nine independently matched patches choose -65 px rather
than the original pooled -69 px; true projected image movement is -64.037 px,
and fixed camera-plane projection reduces X error from -4.473 to -0.501 px.
At 74.953 s, seven of thirteen choose 26 px versus 27.879 px; true displacement
is 25.967 px. Other weak three-patch cases have no independent consensus.
These are diagnostic raw-row reconstructions, not exact solver-bank replay or
an end-to-end improvement. No production consensus policy has been added yet.
Keep the existing outer ambiguity rejection, bounds, masked-pixel protection and
whole-island safeguards when investigating integration. Data: `floor-patch-consensus-diagnostic/`.

## AN evaluated and rejected despite the connected-image threshold pass

AN adds bounded independent coarse patch refinement after the original full-search
ambiguity test, preserves the current floor-row/long-row gates, and passes its
selected patch population into existing keyframe subpixel refinement. It adds no
motion coefficient. Three new tests cover moving foreground, tied modes, darkness,
masks, repetition, duplicate/narrow support and invalid bounds. 62 focused tests
pass; one optional audit is skipped. Canonical source hash:
`ba1edfc2b3c4da1efebf3256dcf72d7504a0d14d5bbf6569ba03298c874f6522`.

All 9,993 frames finish at 35.940 fps, retaining 1,225 screenshots in 77 islands.
483 loop-linked measured images have max 10.147 px and none exceed 12; 463 remain
globally unanchored. Ten accepted edge displacements stay <=1.050 px. The measured
doorway floor mismatch remains zero; added fall Y error stays -7.878 px. Ground
and scene p95 costs are 6.873 and 4.453 ms. Confidence exclusion still holds for
339 unknown-axis scene outputs and all 39 retained unknown-axis captures.

This is not selected: camera RMS worsens from AM's 168.214 to 180.317 px, and
shared-image accuracy/coverage regress. Of 435 images connected in both versions,
RMS rises from 4.994 to 6.298 px. AN loses connections for 202 previous images;
their errors rise from RMS 9.281 to 26.527 px, with 86 over 12 instead of 29.
The 48 newly connected images reach max 8.902 px. All comparisons join immutable
capture timestamps. The 70-85 s section's maximum error rises from 25.409 to
73.145 px. A threshold pass obtained through lost connections is insufficient.

Code inspection identifies a confidence feedback risk: replacing the original
population score with a cleaner subset's lower error can change source ranking
and permit reference promotions. The optional challenge also currently runs before
the original weak-pose rejection. Candidate AO will require original acceptance
first and combine quality conservatively (minimum support, maximum error), so it
can reduce but never increase confidence. A native occluder regression is being
run before that change. No final acceptance or deterministic-repeat claim for AN.

## AO export failure and AP final-pose validation fix

AO conservatively combines population quality (minimum support, maximum error)
and rejects the original weak fit before refinement. Its full 1,009-test Swift
suite passes (57 optional tests skipped). The full replay processes the archive
but fails result export: `trackingSamples[1947].residualRMS` is infinite. It has
no valid completed score/report and is not an acceptance run. AO source hash:
`7e3deb73963c9f2ce4444bae916f2b29c622f0d489be9bc13430d47cdc1ba9fe`.
Failure logs and tests are preserved in `conservative-consensus-ao/`.

Cause: nearby individual best shifts do not guarantee that every patch verifies
at their common median. A thin edge can match at its own zero shift, then vanish
from the target window at the majority's one-pixel shift, producing infinite
cost. The old broad-support acceptance rule also failed to reject nonfinite costs.
AP revalidates each patch at the final common displacement, removes failed patches,
and rechecks strict majority, minimum four distinct patches and horizontal spread.
Only finite per-patch errors <=12 contribute. It also rejects nonfinite or negative
pose quality for every support count, including reference promotion.

The native regression retains fourteen moving patches and removes the disappearing
edge; its common cost is finite. The full AP suite passes 1,011 actual Swift tests
with 57 optional skips. AP canonical build/install succeeds in 79.39 s with one
Dock pin, source hash `678edaf5d55537c2c932d97eb2fc8fab204c616f28bdd61b5bdf83de65cf1d70`.
No nonfinite value is clamped or silently hidden in scoring. The completed
fixed replay and coverage comparison are recorded below.

## AP selected: full fixed replay and exact-capture coverage

All 9,993 images finish at 34.649 fps (288.409 s), with 9,870 ground inputs,
1,223 retained screenshots, 90 islands, 12 accepted loops and one measured
floor connection. The 652 measured loop-linked screenshots have RMS 4.814 px,
p95 7.980 px and maximum 9.926 px; none exceed 12 px. All 12 loop displacement
errors are at most 0.661 px. Of these screenshots, 636 remain globally unanchored;
the same fixed reference is used for their absolute error without any refit.
The globally placed camera subset has only 218 samples, p95 1.247 px and maximum
2.104 px. All-camera error remains RMS 167.843, p95 430.031 and max 4,131.485 px.
All-screenshot RMS is 171.084 px. These large unlocalized errors are not excluded
from the full report. The first room camera RMS is 35.378 px and Town is 400.565 px.

The measured doorway floor mismatch is zero even though the camera Y coordinates
differ by 56 px. All six support captures are measured. X spacing remains estimated.
Added fall Y error is -7.878 px; the physical fall prior has zero activations on
this route, so its live behavior is not established by this replay. Ground/scene
processing p95 is 6.849/4.495 ms; the offline rate excludes capture and display.
All seven retained unknown-axis scene captures remain unmeasured without owned
trusted ground (37 unknown-axis outputs among 54 scene outputs).

Comparison joins immutable capture timestamps because selection cadence changes.
For 593 screenshots connected in both AM and AP, RMS improves 5.599 -> 4.984 px,
maximum 15.473 -> 9.926 px and violations 4 -> 0. AP gains 59 linked capture times
and loses 44 from AM (35 have exact-time counterparts, nine are not selected at
that same timestamp). Of the 35 counterparts, seven still exceed 12 px and remain
disconnected. The 59 new linked screenshots have maximum 6.800 px. None of the
9,993 archived input images is dropped; differing selected atlas times are not
missing capture inputs. This coverage comparison is part of the selection evidence,
not merely the improved error among whichever images happen to remain connected.

Evidence: `verified-consensus-ap/` below the audit directory contains reports,
fixed-reference score/plot, ownership/confidence audits, full test/build logs and
exact-time coverage comparisons. Final repeat/reopen/live status is tracked above.

## AP final deterministic/reopen checks

Three complete runs each process all 9,993 captured images. Offline rates are
34.649, 34.453 and 34.275 fps; capture/display costs are excluded. Run 2 removes
all ground-truth samples and changes stored tracking/coarse poses. All three
visual/coarse/world digests and saved-world bytes match exactly:

- Visual: `d793973368964b31d466df9bb0cc57ef1746dd7b61f882dcd8ec8109e6fe3ba0`.
- Coarse: `896cf3edd317b3aa431552d79516bf35a391593c1cf118c91c391c00513c45e3`.
- World: `68bcb82a75345aa13f2c7869d4b374f65f1ad5d2e9bfb32cf2975f23d47bc321`.

`LiveIslandReplayAuditTests.testRecordedOwnedWorldReopensWithIdenticalPixels`
passes against AP's saved world. It verifies all 1,223 retained screenshots and
owned ground, rebuilds the atlas twice through the production pipeline, checks
identical tile bytes/bounds/world revisions, and confirms snapshot and manifest
bytes remain unchanged. It does not compare against an independently captured
pre-close framebuffer. Source hash matches the canonical installed application;
one Dock pin points to `/Applications/Hollow Knight Vision.app`.

Remaining acceptance evidence: a complete new live route with changed timing,
including occlusion, a deliberate return, falling, the doorway and the bench.
Do not replay stored hero poses for this test. The two incomplete live attempts
remain preserved and explicitly excluded from completed-traversal claims.

## AQ final validation and fresh bench traversal

AQ changes diagnostic persistence/reporting only; its production tracking output
is byte-identical to AP on all three 9,993-frame replays. Offline rates are
34.781, 33.900, 33.655 fps, excluding capture/display. The initial baseline was about
60.6 fps; the added tracking, graph and composition work reduces offline throughput.
This is not a sustained 60 fps end-to-end claim. Fresh live capture/tracking averages
54.414/50.534 fps with p95 processing latency 43.624 ms; pending archive work never
exceeds four images. Ten of 41,116 staged images are explicitly dropped, with zero
failed image writes. One vision job crosses the recording stop boundary.

Full AQ suite: 1,012 actual Swift passes, 57 optional skips, zero failures;
31 Python camera diagnostic tests and 10 replay-scoring tests pass. Native saved
world audits reconstruct the fixed atlas (1,223 images, 8,265 owned spans) and
fresh atlas (2,201 images, 18,390 owned spans) twice. All pixels/bounds, snapshot,
ground and revision checks pass; original disk snapshot/manifest bytes are retained.
All source images have unique ownership, each observation belongs to exactly one
island, and no recorded loop edge joins different recognized rooms. Regression
tests cover rejected/false loops, darkness, foreground occlusion, terminal falls,
camera clamps, stale asynchronous results, atomic rollback, migration and reopen.

Fresh recording:
`path-20261101-051538-5329cb11-0a0b-440d-b52b-72a91b4f2438.json`, SHA-256
`bd12a3b8013bd50a5705a461305d233f26dc09fb13e0f54fd1d373ee3cfedb8c`.
It lasts 755.648 seconds, with 192 input events and 38,186 tracking samples.
38,158 samples have exact reference joins; 28 missing references are counted.
There are 2,047 recorded-window retained images, 2,045 exact screenshot joins,
two missing references and 154 pre-recording images excluded from scoring.
One accepted loop has 14 spatial supports and 0.147 px independent displacement
error; all 28 measured loop-group images retain their absolute unanchored errors.

Doorway evidence stays conservative in the fresh run: first arrival image 2032
has no owned floor; image 2033's floor arrives 11.457 seconds later, beyond the
three-second arrival window. Departure image gaps also exceed the 0.35-second
support-continuity gate. No floor edge is invented. Fixed replay separately verifies
the measured floor constraint with zero mismatch and -56 px camera-height difference.

Town's first fresh reference sample is Unity frame 2235909, before the new scene's
camera initialization. The next joined capture (2235912) has a 3,832.076 px reference
jump while the published pose is held. The first reference and all resulting errors
remain unchanged. Later unlocalized drift is also present; the transient does not
explain all error. No new scoring origin is fitted after recovery or transition.

Generated evidence, plots, bench screenshot, input journal, exact scores, frozen
origins and both retained source-image worlds are archived under `export-aq/` and
`fresh-live-guided-aq/` in the ignored audit directory. Original recording JSON/PNGs
remain in the input-path store. Historical failed attempts remain preserved.
No commit, push, Windows source change or Windows native validation was performed.
