> Historical design/evidence. Experimental commands described here were retired on 2026-09-29; use the current README and RETIRED_FEATURES.md.

# Layered reconstruction work packets

Each implementation packet owns at most two files. Terra builders implement;
the main agent audits, integrates, fixes and runs package/replay/runtime checks.
No packet is accepted merely because it compiles.

| Packet | Behavior / acceptance | State |
| --- | --- | --- |
| 0 | Baseline: current 48 Swift tests pass | Passed |
| 1 | Robust motion fits, persistent layer IDs, measured evidence only | Tests validated |
| 2 | Transition gating, room/visit identity, conservative portal graph | Tests validated |
| 3 | Replayable observations, atomic session revisions, reopening | Tests validated |
| 4 | Boundary ownership, independent measurements, masked references | Tests validated |
| 5 | Stable layer masks and replaceable tiled RGBA atlases | Tests validated |
| 6 | Serial ingestion, bounded refinement, live capture integration | Tests validated |
| 7 | Layer isolation, parallax/exploded views, reversible corrections | Tests validated |
| 8 | Revisit verification and spatial world integration | Tests validated; real multi-room pending |
| 9 | Recorded-video replay, synthetic metrics, release build/runtime | Synthetic validated; real-recording gate pending |

Quality gates: unknown stays unknown without evidence; valid black stays opaque;
filled interiors never count as motion measurements; stale revisions cannot win;
old incorrect atlas pixels can be removed; room changes cannot mix camera frames.
First-room replay must produce reviewable artifacts before world integration is
considered validated. Media and sessions remain under ignored artifacts.

## Current implementation and replay gate

Packets 1–9 now have concrete Swift implementations: motion/layer fitting,
room graph and transition gating, replayable source storage, tiled layer atlases,
scene ownership and refinement, visual revisit matching, room merge transforms,
and deterministic video replay. The important public entry points are
`LayeredScene`, `SceneSessionStore`, `RoomRevisitMatcher`, and
`ReplayRunner.run(arguments:)`. They are deliberately source-backed: replay can
rebuild an atlas from retained PNG observations instead of treating a flattened
map as evidence.

The synthetic three-plane regression is a hard gate. It renders independent
flat silhouettes at β `.65`, `1`, and `1.8`, includes backward motion, and
requires factor error ≤ `.05`, ownership precision ≥ `95%`, and observable
interior coverage ≥ `80%` (with one pixel of boundary uncertainty). It tests
primitives and scene integration; it does not certify a real game recording.

The final first-room replay v06 processed 86 frames in 5.75 seconds and promoted
two layers. Mean held-out coverage was 15.15%, RGB MAE 25.21/255, and contour
distance 31.36 px. Earlier v04 measured 19.4% coverage and 21.30 px; stricter
promotion did not resolve the underlying alignment problem. Both miss the 2 px
contour target. v05 edge-only registration promoted zero layers and remains an
explicit experiment, with RGB as default. The final source frames, session,
held-out renders and report are retained under
`artifacts/side-scroller-map-lab/layered-replay-2026-09-06` at repository root.

Focused next packets each have a measurable gate:

| Priority packet | Scope and output | Automated gate | Real-recording acceptance |
| --- | --- | --- | --- |
| Joint camera/layer fit | Solver + tests; independent contour tracks → jointly fitted camera and layer motion, then a separate integration packet | Synthetic β factors stay within `.05` when the dominant visible plane changes, including reversals | First-room contour distance ≤2 px; coverage must stay ≥15.15%, with no held-out images used in fitting |
| Contour/interior ownership | 1–2 files; visible contours → interior assignments | Synthetic ownership precision ≥95% and interior coverage ≥80% | Held-out coverage rises without assigning masked or unknown background |
| Occlusion order | 1–2 files; overlap evidence → front/back order | Known overlap fixture selects the measured front layer | Review images retain stable foreground ownership through overlap |
| Multi-room A–B–A replay | 1–2 files; replay evidence → revisit/portal report | Duplicate-artwork fixture stays ambiguous and verified A–B–A succeeds | A real recording revisits A without inventing an unverified portal |

Native room and exploded Metal views were inspected after fixing the empty
active-room filter, flipped atlas pixels, tile projection and cramped controls.
The signed application bundle builds successfully. The final full Swift suite
passed 118 tests with zero failures at 08:53:02 on 2026-09-06. This includes
asynchronous refinement: stale work cannot overwrite an edit, and refinement
survives reopening. `git diff --check` passes. The test log and native review
screenshot are retained alongside the final replay report.

## Phase two: Desktop recording and independent motion

Input: `Screen Recording 2026-09-06 at 8.59.29 AM.mov`, 14.415 seconds,
1696 × 1896 at 60 fps. The verified gameplay crop is top-left source pixels
`114,480,1468,826`. The original Desktop recording is unchanged.

Terra builders implemented bounded packets; the main thread audited, corrected,
integrated, compiled, replayed, and inspected their results:

| Packet | Implementation and acceptance | Outcome |
| --- | --- | --- |
| 10: registration probe | Persistent Vision request must return adjacent-frame X/Y translations through reversals, including the production ROI | Passed; cumulative-registration hypothesis ruled out |
| 11: independent evidence | `FrameMotionEvidenceTracker` + tests: camera-independent correspondences, bidirectional flow, reciprocal normal profiles, masks, ambiguity and occlusion rejection | 8 tests pass; audit fixed top/bottom coordinates, source/target direction, unsupported Vision revision, and profile uncertainty |
| 12: joint gauge | `JointCameraLayerSolver` + tests: robust camera consensus, learned factor transfer when the initial layer disappears, outliers, rank-deficient edges, held-out isolation | 10 tests pass; audit added normal hypotheses, inlier spatial coverage, no silent gauge reset, and stale track pruning |
| 13: replay integration | Explicit crop and `--registration joint`; retain the last accepted reference across rejected fits; preserve source units and room gauge | Runs the supplied recording; 6/15 fps trials lose calibration during the fast pan, 30 fps accepts every frame after initial calibration |
| 14: review evidence | `MotionEvidenceRenderer` + tests; sparse factor-colored motion overlays, full held-out schedule, source/reconstruction/depth/coverage comparisons | 2 renderer tests pass; native saved-session view and interactive browser review inspected |

The full suite passed **141 tests, zero failures** on September 6 at 09:33:52.
After the final evaluation audit, the affected tracker and replay suites passed
**13 tests, zero failures** at 09:40:59. The release application rebuilt and was
ad hoc signed. The final saved session reopened in the native exploded view.

The evaluation audit also corrected survivorship bias: `heldOutAttempts` includes
all scheduled checks, including unposed frames. Held-out images cannot advance
joint factor history, seed/advance the tracking reference, update temporal masks,
or contribute atlas pixels. Their pose may be estimated against a training
reference. Tracking failures retain an unknown current pose rather than silently
creating another camera gauge; only a visual transition starts another visit.

Both final runs use the original recording, exact crop and 30 fps schedule:

| Measured result | RGB | Joint |
| --- | ---: | ---: |
| Sampled frames | 421 | 421 |
| Accepted camera frames | 421 | 318 |
| Accepted / scheduled held-out poses | 84 / 84 | 64 / 84 |
| Coverage over all scheduled checks, unavailable pose = 0 | 31.82% | 24.38% |
| Coverage at the 64 common accepted timestamps | 32.06% | 31.99% |
| RGB MAE /255 at common timestamps | 25.03 | 22.88 |
| Contour distance at common timestamps | 14.54 px | 14.01 px |
| Candidate layer models | 4 | 7 |
| Replay runtime | 37.45 s | 48.74 s |

Joint initialization waits until camera movement at about 3.55 seconds. Its 103
unaccepted opening frames are explicitly accounted for. Color and contour errors
score only reconstructed pixels; the two modes detect their own transient masks.
The improvement is modest, full-schedule coverage is lower, and **the 2 px real
contour gate still fails**. Seven models do not certify seven physical layers.
The sparse camera/depth tracks now work through this recording's pan and look-up,
but the current correlation-based region ownership still fragments silhouettes
and assigns inconsistent background depths. Joint mode remains offline and
opt-in; live RGB behavior is retained while this gate is open.

Review: `artifacts/side-scroller-map-lab/camera-depth-phase2-2026-09-06/review.html`.
`rgb-final/` and `joint-final/` contain the final reports and source-backed sessions;
`comparison.json` records the same-timestamp comparison. PNGs include isolated
candidate layers, motion evidence, native review and browser review.

Next bounded packets, in dependency order:

1. **Measured contour seeds** — solver + tests. Accept independent calibrated
   samples with IDs, normal uncertainty and room-gauge conversion. Filled pixels
   cannot manufacture support. Gate: an ambiguous region cannot override measured
   boundary motion; X/Y reversals keep synthetic factor error ≤.05.
2. **Contour ownership integration** — scene integration + tests. Associate those
   samples with retained region boundaries before matching a region to a layer.
   Do not assign by factor similarity alone. Gate: spatially separate unrelated
   regions and mixed-depth boundaries stay unknown; edits survive refinement.
3. **Flat interior and occlusion evidence** — ownership solver + tests, followed
   by a separate replay audit. Fill only coherent boundaries or repeated visibility
   support, including opaque black and frame-border uncertainty. Gate: synthetic
   precision ≥95% and interior coverage ≥80%; real coverage must improve without
   increasing contour error, and ultimately reach ≤2 px.
4. **Frequent motion, bounded atlas work** — capture/replay coordinator + tests.
   Decouple ≥30 fps motion evidence from less frequent atlas ingestion, then test
   the same fast pan and look-up at the live processing budget. Do not enable
   joint live capture until both runtime and real ownership gates pass.

Multi-room A–B–A validation follows those packets; this one-room recording cannot
validate portal identity or room merging.

## Phase three: measured contour ownership and interior filling

Implemented with Terra builders, followed by root audit, corrections, integration,
synthetic tests, three real replays, and native/browser inspection. The final
full Swift suite passed **170 tests, zero failures**, September 6 at 10:45:03.
The final focused run passed 55 tests before the full regression run. The release
app builds and is ad hoc signed; its saved two-layer session reopened successfully.

| Bounded packet | Result |
| --- | --- |
| Factor uncertainty | Joint results expose per-track uncertainty in factor units, including bootstrap, propagation, pruning and held-out behavior |
| Independent contour ownership | Source-pixel points, normals, IDs and uncertainty enter geometry-only Sobel topology; require three spatially distinct compatible samples; preserve raw evidence |
| Open contour correction | Connected edge components can retain directly measured rims at frame boundaries; this does not authorize filling their open interiors |
| Closed interior filling | Add visible closed-region pixels to unique owned rims; preserve opaque black, masks, unknown exterior, mixed ownership and occluded foreground |
| Scene integration | Visit-scoped track bindings, temporal confirmation, room-gauge conversion, persistence, refinement and manual corrections |
| Replay and review | `--ownership correlation`, `contours`, or `filled-contours`; contour modes require joint registration; export ownership diagnostics and compare fixed held-out checks |

Audit corrections included retaining the explicit solver's camera history,
inverse-transpose normal conversion, deterministic shared-rim rejection,
preventing filled pixels from entering measured evidence, invalidating stale
refinement after promotion between retained frames, and scaling the tracker's
factor limit after a slow reference is selected. Additional scene tests cover
vertical motion, fresh tracks after changing reference and reopening, opaque
black atlas pixels through refinement, and a promotion/refinement race.

The first real trial incorrectly required closed topology even for directly
measured contours and confirmed zero layers. It is retained as
`closed-only-trial/`. The corrected implementation separates measured open rims
from eligible closed interiors. Synthetic filling passes the 95% precision and
80% eroded interior coverage requirements for three independent planes. Nested
foreground remains separate; conflicted surrounding background may stay unknown.

All final replays use the supplied Desktop recording, crop `114,480,1468,826`,
30 fps, 421 sampled frames, and the same joint camera path. All accept 318 camera
frames and 64 of 84 scheduled held-out poses. The unposed stationary opening is
accounted for rather than omitted from total coverage.

| Metric | Correlation | Contour points | Contours + interiors |
| --- | ---: | ---: | ---: |
| Candidate layers | 7 | 2 | 2 |
| Coverage, all 84 checks | 24.28% | 0.062% | 0.160% |
| Coverage, common 64 posed checks | 31.87% | 0.081% | 0.210% |
| RGB MAE /255, observed pixels | 22.95 | 22.48 | 20.65 |
| Contour distance, observed pixels | 13.95 px | 16.43 px | 16.53 px |

**Real-room quality is not accepted.** Filling adds a small amount of visible
interior, but coverage is far below the existing correlation reconstruction and
the 2 px contour gate still fails. Lower RGB error on a tiny observed subset does
not establish better reconstruction. Two candidates are not verified physical
depth planes. New ownership modes remain explicit offline experiments.

The failure is now measurable. Across 254 training ingests, 4,105 of 4,106 valid
calibrated samples associated with image contours. There were 6,026 closed region
candidates across frames, but only three had three supporting tracks and coherent
ownership. Of 378 supported open component candidates, 88 passed coherence and
spatial checks. Thus association itself is working; sparse samples do not yet
give enough local evidence to the broad surfaces and numerous small regions.

Review artifacts at repository root:
`artifacts/side-scroller-map-lab/contour-ownership-phase3-2026-09-06/`.
`review.html` compares all three modes; `comparison.json` and `validation.json`
record results. Each mode has a source-backed `session`, held-out PNGs and report.
Contour runs also include `ownership-evidence.json`; the filled session contains
isolated candidate PNGs. Native and browser screenshots are retained beside the
test/build logs. Timings are operational measurements, not controlled benchmarks.

Next packets, based on the observed failure:

1. **Contour chain ownership** — trace branches and junctions, extend calibrated
   evidence along the same visible contour, and split at incompatible motion or
   occlusion. Test long textureless edges with sparse anchors, multiple depths
   meeting at a junction, and no ownership propagation across an exclusion.
2. **Temporal visible-surface support** — accumulate contour and region evidence
   in the room gauge across pans/reversals; distinguish a frame-clipped surface
   from an intrinsically open or occluded boundary. Test fill on a large flat
   foreground crossing the viewport, without inventing hidden background.
3. **Real replay gate** — rerun this exact crop/schedule; require useful coverage
   improvement without worse contour error and ultimately ≤2 px before enabling
   the new ownership path in live capture. Occlusion order and multi-room A–B–A
   validation still follow this first-room gate.

## Phase four: dense reconstruction from the existing video

The same 14.415-second Desktop recording supplies the data. The user requested
more pixels assigned depth, so the new path combines temporal image evidence
with spatial inference rather than requiring every flat region to contain three
sparse contour tracks in one frame. Terra builders implemented bounded scoring,
propagation, visibility and test packets; root audited, corrected and integrated
them, then compared three complete recording runs.

| Packet | Behavior / audit |
| --- | --- |
| Temporal depth costs | Pixel-major RGB/gradient costs over candidate parallax factors; bilinear warps in explicit image-grid units; masks and unsupported hypotheses remain unknown; at least two reference views |
| Guided propagation | Deterministic geodesic propagation from independent contour or discriminative temporal anchors; image edges penalize crossing; masks disconnect paths; flat opaque black stays valid; ties stay unknown |
| Dense batch integration | Keep all training motion calibration, retain 65 training images for dense processing, preserve confidence/evidence/source IDs, maintain editable connected components and stable installed layer identities |
| Visibility consensus | Project each claimed surface into diverse training camera positions; nearer observed layers may occlude it, while consistent views of a farther layer contradict a claimed foreground pixel |
| Review and regression | Original/reconstruction comparisons, false-color source depth, confidence and evidence masks, saved native session, held-out isolation, synthetic reversals, exclusions and opaque foreground |

The root audit rejected contradicted seeds **before** propagation so they cannot
leave inferred descendants behind. Batch installation rejects nonempty rooms to
avoid erasing prior corrections. Dense masks survive the ordinary single-frame
refinement path. Region IDs identify connected patches, independently of their
shared factor/layer. Global factor calibration uses all 254 training ingests;
source-frame thinning must not remove short-lived but calibrated tracks.

Final measurements, identical camera paths and 64 common held-out timestamps:

| Metric | Previous correlation | Previous contour fill | Temporal + guided depth |
| --- | ---: | ---: | ---: |
| Coverage, common posed checks | 31.87% | 0.210% | **99.824%** |
| Coverage, all 84 scheduled checks | 24.28% | 0.160% | **76.057%** |
| RGB MAE /255 | 22.95 | 20.65 | **15.24** |
| Contour distance | 13.95 px | 16.53 px | **12.39 px** |
| Candidate layers | 7 | 2 | 8 |

The final candidate factors span approximately .576 to 1.491, relative to the
joint camera gauge. They are estimates rather than eight certified physical
planes. Flat surface pixels are mostly spatial inference, recorded separately
from contour and temporal photometric anchors. Confidence is a relative score.
Across retained source frames, 50.47% of eligible pixels receive depth, and
82.32% of those assignments are spatial inference. The much higher held-out
coverage comes from assembling observations across the complete recording.
No held-out image contributes camera-factor learning, dense source references,
depth seeds, spatial propagation, visibility votes, or atlas pixels.

**The coverage objective improved substantially; accurate scene reproduction
remains incomplete.** The global map still has noisy background overlaps and
imperfect contour alignment. The 2 px gate remains open. The first 20 scheduled
checks have no accepted camera pose, so they count as zero in all-check coverage.
Runtime measurements are operational timings, not controlled benchmarks.

Artifacts: `artifacts/side-scroller-map-lab/dense-scene-phase4-2026-09-06/`.
`temporal-v1/` and `temporal-v2/` retain the prior trials; `temporal-evaluated/` is
the untouched final calibrated batch. The open `temporal-final/session` has a
saved layer-slider adjustment and remains intact as an editable working copy.
`review.html` includes reconstruction and source-depth
inspectors; `comparison.json` and `validation.json` record results. Next accuracy
work should consolidate persistent surface ownership and improve global camera /
layer alignment, with particular attention to the blurred background and thin
foreground contours. Recovering the unposed opening and validating multi-room
revisits remain separate tasks.

Final validation: **188 release tests passed, zero failures** (4.875 seconds).
The synthetic three-plane reconstruction measured 100% observable coverage and
100% ownership precision; its required floors remain 80% and 95%. This synthetic
result does not establish real-video depth accuracy. The signed native app loaded
all eight saved layers, and the browser review was visually inspected. Test and
build logs, browser/native screenshots, and the held-out isolation audit are
retained with the phase-four artifacts. The native screenshot shows the editable
preview, including its saved factor adjustment, rather than the evaluation state.

## Phase five: provenance-aware texture atlas

The dense scene geometry, camera path and eight candidate factors are fixed for
this phase. The work changes only which retained source observation supplies an
atlas texel. A texel always keeps one original RGBA sample; it is never a blend or
an averaged colour. Transparent samples remain unknown, while opaque black stays
valid evidence.

The selector receives source ID, RGBA and quality while rebuilding one tile at a
time. Quality combines dense depth confidence, evidence kind (independent contour,
temporal match, spatial inference or legacy), and mild distance from the
source-frame edge. It rejects disagreeing colours, normalizes local agreement so a
high-view-count texel cannot dominate neighboring decisions, then uses a bounded
source-wide coverage/quality preference and deterministic neighbor continuity. A
one-pixel halo around each 256-pixel tile preserves a decision across tile
boundaries. `LayerAtlas` retains contribution labels, transforms and
contribution-to-touched-tile IDs, not per-pixel fragments or RGBA. It
inverse-projects a one-pixel-halo source ROI and rebuilds the tile from the
authoritative source PNG. `LayeredScene` keeps a bounded 12-frame decoded-source
LRU and reloads that PNG from the session on a cache miss.

Every fifth frame remains held out. Its evaluation runs against copied
tracker/gate/solver state, so it may receive a pose and reconstruction without
mutating training state or entering source selection, atlas rebuild inputs, factor
learning, references, dense costs or propagation. `atlas-quality.json` records per-tile
selected source IDs, confidence, conflict, source-boundary edges and connected
source components. `atlas-diagnostics/` contains source, seam, confidence and
conflict PNGs. Review
`artifacts/side-scroller-map-lab/atlas-phase5-2026-09-06/review.html` compares
`legacy-fixed`, `coherent-v1` and final `coherent-final` at the same schedule.
The camera path, normalized dense evidence, held-out array and reconstruction
PNGs, and normalized atlas diagnostics are byte-identical across the storage
rewrite.

All three runs have the identical 421-entry camera path, 65 dense source
segments, one room, and eight candidate factors from `.576177` through `1.490815`.
They retain 64 accepted held-out poses from 84 scheduled checks; the other 20 are
counted as zero coverage. Accepted-pose coverage is unchanged at **99.959%**
(`14,439,706 / 14,445,634` pixels), or 76.159% across all scheduled checks.

| Metric | legacy-fixed | coherent-v1 | coherent-final |
| --- | ---: | ---: | ---: |
| RGB MAE /255 | 15.239 | 14.799 | **14.885** |
| Gradient MAE /255 | 5.231 | 4.865 | **4.794** |
| Contour distance | 12.390 px | 12.059 px | **11.979 px** |
| Atlas source boundaries | — | 336,477 | **223,427** |
| Atlas source components | — | 55,161 | **29,192** |

`coherent-final` reduces v1 tile-local source boundaries by **33.5981%** (113,050) and
components by **47.0786%** (25,969), with identical observed atlas pixels. This
is a provenance and seam-quality improvement, not evidence that the eight
candidate factors are physical layers or that a world map is solved. The real
2 px contour gate still fails at 11.979 px.

The identical replay exposes the storage tradeoff. Maximum RSS falls from
842,350,592 to **674,955,264 bytes** (-19.87%), and peak footprint falls from
798,411,152 to **528,025,640 bytes** (-33.87%). Tile rebuilds from source PNG
ROIs raise wall time from 57.42 to **88.76 seconds** (+54.58%). The full suite
now passes **218/218**.

## Phase six: atlas-guided residual pose alignment

This phase keeps dense labels, factors, assignments, evidence and source-choice
rules fixed. It searches a bounded 5 × 5 camera correction for each retained
training segment using leave-one-selected-source-out residuals, smooths the
accepted corrections within each visit, and anchors the earliest training pose.
The candidate rebuilds the atlas once after fitting. A training residual gain and
non-regressing source-seam score are required; any rejected fit restores the
original positions exactly.

Held-out observations remain outside the source list, fit and atlas rebuild.
Their one-shot release evaluation uses interpolated training corrections only.
The selected renderer is an explicit `effective-atlas-cascade-v1`: it uses the
refined atlas first and the immutable, pre-refinement training atlas at the raw
held-out pose only for a primary-atlas hole. Primary refined-atlas coverage,
fallback count and effective cascade coverage are serialized separately in
`pose-refinement.json`; amber pixels in `*-coverage.png` are fallback and
`*-primary-coverage.png` excludes them. Thus effective coverage is the release
promise, while primary coverage remains visible rather than being masked.

| Held-out result, 64 accepted poses | Baseline atlas | Refined primary | Effective cascade |
| --- | ---: | ---: | ---: |
| Coverage pixels | 14,439,706 | 14,439,649 | **14,439,931** |
| Coverage | 99.95896% | 99.95857% | **99.96052%** |
| Fallback pixels | 0 | — | 282 |
| RGB MAE /255 | 14.88523 | — | **14.87030** |
| Gradient MAE | 4.79384 | — | **4.78622** |
| Contour distance | 11.97899 px | — | **11.97849 px** |

The training leave-one-source-out residual changes from 0.0310075 to 0.0308117.
The selected atlas has 222,490 source boundaries and 28,966 components, down
from 223,427 and 29,192. It has 1,171,358 observed primary-atlas pixels versus
1,171,491 baseline pixels. These measurements improve image alignment and
provenance, not the physical-depth claim; the real contour target remains 2 px.

Artifact and review:
`artifacts/side-scroller-map-lab/atlas-phase6e-2026-09-06/`. The replay has 421
sampled frames, 254 retained training observations and 64 accepted held-out poses
from 84 scheduled checks; it completed in 196.16 seconds. The release suite
passes **232/232** tests. `validation.json` verifies the held-out schedule,
coverage accounting, effective-coverage gate, source isolation and output PNGs.
Synthetic tests cover injected camera-offset recovery,
unchanged labels/factors/assignments/evidence, held-out source isolation, and
primary-versus-effective cascade coverage accounting.

Next packet: use the explicitly reported primary-coverage holes to target atlas
expansion or ownership repair in the first room, then validate room transitions
and revisits before treating separate recordings as a connected world map.

## Phase seven: persistent live-world loop closure

Implemented as bounded Terra packets for the world model and matcher, translation
pose graph, source-backed tiled atlas, transactional persistence, direct Metal
review overlay, and repeat/reopen replay gate. Root audited the combined path,
fixed coordinate scaling, confirmation during camera motion, cross-launch visit
boundaries, persistence CAS serialization, delayed-frame ownership, ambiguity
handling, and carried accepted corrections into subsequent live poses.

| Packet | Tested behavior |
| --- | --- |
| Durable world evidence | Versioned observations, keyframes, descriptors, landmarks, motion edges and loop edges; all source IDs validate against immutable masked PNG evidence |
| Global retrieval | No pose prior; HUD/Knight exclusions; bounded spatially diverse search; repeated-texture and distinct-placement ambiguity rejection; saved references searchable immediately after reopen |
| Closure acceptance | At least two scans separated by the 2 Hz cadence must infer the same drift even while the camera continues moving |
| Graph correction | Deterministic weighted translation solve with a fixed anchor; corrections propagate across historical observations and their landmarks |
| Tiled atlas | Sparse 256 px tiles, incremental unique inserts, source-backed full rebuild after graph correction, old published tiles remain visible during work |
| Transaction boundary | Reset serializes with append/commit/publish; failed append or CAS cannot publish; stale timestamps cannot create unowned atlas contributions |
| Review UI | One bottom **World feature overlay** checkbox; direct Metal landmarks, keyframes, candidates, closures, and correction vectors with bounded overlay counts |

The real gate uses the supplied September 6 recording's saved 115-frame,
640 × 360 session. It replays the right hall, elevation loop, descent and return
three times, committing its source PNGs and graph envelope to an isolated live
world and reopening that world from disk between visits.
Endpoint feature registration measures 13.006 px of raw accumulated drift. All
three visits accept 2, 3 and 2 excursion-spaced closures and finish at 0.363,
0.095 and 0.475 px optimized residual. The 12 px acceptance gate passes on every
visit. The final optimized suite passes **354/354** tests, the app bundle builds,
and its ad hoc signature verifies. This proves the bounded first-room return
path; separate rooms and portal topology remain a later phase.
