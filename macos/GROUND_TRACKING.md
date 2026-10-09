> Historical design/evidence. Experimental commands described here were retired on 2026-09-29; use the current README and RETIRED_FEATURES.md.

# Ground-only live tracking

Ground texture drives normal camera tracking. When it cannot establish a pose,
the transition bridge can use masked Apple Vision translation registration or
bounded coarse motion until ground tracking recovers. Object detection uses its
existing model pipeline, independently, every fourth capture.

1. Offer every capture to the bounded motion worker. Keep one active frame and
   only the latest pending frame; never build a backlog.
2. Apply radial correction, convert to grayscale once, subtract adjacent rows,
   mask known foreground, and apply the existing 32-by-8 ground kernel.
3. Extract line islands once. The camera tracker, ground reference and diagnostic
   renderers share that extraction. Current defaults: threshold 10, minimum
   evidence length 48px, parallel-line separation 36px, occlusion gap 180px.
   Lower-floor evidence inside an unsupported upper span splits the two upper
   ends. Persistent lower-floor geometry keeps that split during brief cover.
   Knight rejection uses the fraction of actual edge evidence inside the body,
   not any overlap with the merged line span. A small moving Knight box cannot
   invalidate the whole room-wide floor.
4. Clean rows propose +/-3-pixel vertical bands. Ordered 16-by-12 ground patches
   search integer horizontal positions and verify row identity. A stable spatial
   keyframe removes integrated drift; a recent-frame fallback bridges mismatches.
   Keep up to twelve spatial references, including the original view, and select
   nearby historical references with hysteresis on return. A weak anchor may be
   compared with recent evidence from the same pre-solve state. Identical capture
   pixels do not imply deceleration; horizontal acceleration has a +/-16px
   minimum search allowance. Shear is anchored at the ground surface (tile top),
   not at the middle of the below-ground patch.
   Mean/contrast-normalized texture error, spatial support and competing matches
   gate acceptance. Search growth and stale-velocity prediction are bounded.
5. Ordered global matching, at most four times per second, proposes corrections.
   Frozen full-resolution patches across the visible strip verify them. A change
   to a trusted pose must improve strip evidence, not just match four repeated
   tiles. A +/-8px fine-phase search uses a fixed visibility set and independent
   physical-cell votes; tile identity does not force a whole-16px correction.
   Frozen raw source crops are re-rectified at the current calibration before
   comparison. Accepted global corrections also update the local solver's current
   coordinate basis and its same-capture references, without moving older spatial
   anchors. Choose the final pose before committing any persistent evidence.
6. Update line presence in world coordinates using elapsed observable time, not
   frame counts. Offscreen, masked, uncertain-pose and long unsampled gaps do not
   count as misses. The live tracker requires 0.2 seconds before classification:
   above 50% is green/confirmed; below 50% is yellow/rejected; exactly 50% remains
   cyan/observing. Confirmed lines get three seconds of observable-miss grace
   across temporary cover. Persistent misses still reject; accumulated positive
   evidence can recover a rejected line. Extensions earn their own evidence.
   Independent new floors must also pass two texture-motion checks over at least
   4px camera travel. Incomplete entry references refresh as more texture appears.
   The generic tracker/presence API retains its two-second, zero-grace defaults;
   the live app selects the shorter admission and bounded retention policy.
   This evidence ledger is currently in-memory.
7. Tile confirmed ground on the shared 16-pixel lattice. Rejected lines lose their
   associated tiles. An unverified frame cannot update reference pixels or enter
   the atlas. Hide stale live projections while keeping map review metadata.

## Short-route feature recognition (2026-09-27)

The right-left recording `CD0377C8-87FD-4101-ADE8-611DC77E8EF9` has an upper
floor, a shallow lowered floor, and a second upper floor. Five final-build
replays retained those three IDs after discovery. In two audited final runs,
false persistent floor rows were absent, median entering-tile recognition was
0.39–0.42s (baseline 2.42s), and median recognized visible lifetime was 2.72–2.75s
(baseline 0.94s). All 75 checked tile IDs were reused on return.

This passes the narrower feature/topology improvement, not broad stability:
one of five final runs lost pose for 7.24s near the end of return; four had no
pose loss. Final throughput was 51.9–54.3 solved capture samples/s, not a verified
60 FPS result. Baseline had no pose loss in two runs; this sample cannot establish
non-regression for pose stability. The failure is retained in the report.

Evidence: repository-root `hkv-short-ground-20260927/REPORT.md`, replay JSON,
frame audits, parameter sweeps, and before/after annotated captures. Release
suite: 748 tests, 4 skipped, 0 failures. Use `--show-ground-features` for live
feature overlay and opt-in `--ground-audit-directory=/absolute/path` for bounded,
asynchronous PNG/JSON capture; ordinary launches perform no audit writes.

## Edge-stage optimization

- Compute differences in a tight linear pass; clear excluded row spans instead
  of testing CGRect containment at every pixel.
- Pair the fixed signed kernel coefficients; avoid the nested per-pixel
  coefficient loop.
- Share line extraction and source grayscale across consumers.
- Preserve full resolution, every-frame scheduling, masks, kernel, thresholds
  and integer normalization. `GroundDetectionEfficiencyTests` contains the old
  implementation as a pixel-exact oracle, including border/fractional-mask tests.

Release benchmark on the development Mac at 640-by-360: edge plus kernel median
3.22 ms before, 1.68 ms after (12 alternating samples). This excludes radial
correction, line extraction, camera tracking and rendering; not a 60-FPS claim.

## Earlier validation and remaining limits

Ground solver/presence/tracker/reference/efficiency suites: 68 release tests pass.
The subsequent Knight-overlap regression and detector suite add one test;
all 23 detector/reference tests pass after that fix. Live optimized motion
processing measured about 4.4 ms median versus 8.4 ms before, at roughly 58 FPS
capture in the starting scene. These timings depend on scene and machine load.
The detector can still miss real floor evidence; the presence rule does not
distinguish a reliably detected false edge from real ground. Fresh-atlas live
testing is required in addition to synthetic tests. Hold atlas writes when
texture cannot establish a pose; never substitute an overlap-only row correction.

Live check after the Knight fix: the starting floor remains confirmed at 100%
presence and atlas writes resume. Automated look-up/return still fails: horizontal
pose drift (observed -39 pixels), duplicate mapped rows, then loss of registration.
Do not treat the camera/atlas rewrite as runtime-passed. Edge optimization is
verified separately. Next investigation should distinguish integer frame-motion
drift from periodic global corrections and capture the per-frame pose/line matches.

## Automated 20-cycle investigation (2026-09-18)

`control-look-test.sh` now repeats 20 bounded Up/release cycles, capturing both
the game and Vision windows. Opt-in `--trace-ground-tracking` logs each local
solve/rejection and the final pose/global correction; disabled by default.
No tracking algorithm changes were made for this diagnostic run.

Evidence is in repository-root `hkv-up20.YovWbz/REPORT.md` and accompanying
screenshots/logs. All 20 source images returned to the original position.
Local horizontal motion stayed zero; false global corrections caused +112,
then repeated +/-80-pixel jumps. Ten returned poses were 80 pixels sideways.
Vertical edge jitter of two pixels can exclude the correct zero-motion match
from the +/-1 proposal refinement. Fast camera movement after a repeated frame
also exceeds the +/-8 row gate. Two returns froze at Y=61 and Y=50; ordinary
return error was 1–2 pixels. About 25% of sampled solves were unverified.
One persistent atlas ground segment remained throughout this run.

Priority: validate global phase/line candidates over the visible ground strip;
make vertical texture search tolerant of detector jitter and camera acceleration;
add independent recovery and commit map evidence only after final pose selection.
Repeat this same fresh-atlas test before expanding to walking/loop-back.

## Implemented fixes and measured repeat (2026-09-18)

See repository-root `hkv-ground-tracking-metrics.md` for protocol, formulas,
artifacts and the complete baseline/after table. `score-ground-tracking.py`
aligns all 20 cycles to exact bridge press/release timestamps, reports sample
standard deviations plus bias, and counts painted atlas pixels outside the
current horizontal view. `--baseline RUN_DIRECTORY` exits unsuccessfully if
the improvement checks fail. Scorer arithmetic has three regression tests.

One precise unchanged baseline plus two improved runs (60 new cycles): press
SD X/Y 19.38/5.61 px became 0/0.57–0.75; release SD 29.34/11.10 became
0/0.63–0.74. Both improved runs returned exactly to (0,0), with zero side-painted
pixels and 100% verified poses. Baseline had 80px spill on both sides and a 3.96s
tracking loss. Source screenshots independently verify 72px up and zero on return.
136 related release tests pass. Median capture rate stayed approximately 55 FPS.

This supersedes the earlier starting-area look-up failure, not every tracking
limitation. Horizontal walking, room transitions, arbitrary large relocation and
subpixel accuracy need separate live validation. No forced-X constraint, input-
specific tracking rule or atlas clipping is used to obtain these scores.

## Horizontal walking validation (2026-09-18)

Repository-root `hkv-walk-improvements.md` records five new 20-cycle walking
trials, including rejected regressions, and a final 20-cycle vertical check.
Fine phase refinement, top-anchored consistent shear, duplicate-frame acceleration
handling, historical spatial anchors and synchronized local/global coordinates
are now implemented. Forty final-build returns were independently checked using
game images: worst horizontal error 1px, return-error SD 0–0.37px and RMS
0–0.39px, compared with baseline 5px / 1.08px / 4.14px respectively.
Both final runs had zero false vertical movement and zero tracking losses.
Maximum frame jump fell from 29px to 15–17px. Residual-step SD improved in the
repeat but slightly worsened in the first final run; smoothness is not uniformly
improved. Capture stayed near 55 FPS while tracking CPU cost increased.

The final vertical check returned to (0,0) in all 20 cycles, with 100% verified
poses and zero side-painted atlas pixels. 144 relevant release Swift tests and
seven Python metrics tests pass. These results supersede the unvalidated
horizontal status only for the tested starting-cave route. Complex platforms,
room transitions, large relocation and subpixel accuracy remain unproven.

## Complex-platform investigation (2026-09-18, not runtime-passed)

Repository-root `hkv-platform-tracking.md` records two 20-cycle Up/release runs
in the user's five-floor area. The unchanged build verifies only 4.87% of poses;
independent images prove a 130px pan and exact return, while the tracker drifts.
Narrow capture-padding exclusion and evidence-ranked line suppression recover
five lower and three revealed upper floors in screenshot audits. Empty occlusion
gaps no longer count as strong edge support. The detector-only live candidate
retains the original five floor IDs but regresses camera accuracy; it is not a
passing solution.

The subsequent solver candidate compares hypotheses over a common reference
patch set, permits previously observed texture behind partial edge dropout, and
removes stale projected negative-line masks from the unknown-pose image solve.
Negative line presence still controls atlas admission. 153 relevant release
tests pass (one optional screenshot audit skipped), plus nine Python metrics
tests. A final live repeat is pending restoration of the original game position;
do not claim the platform tracking problem solved.

Opt-in `--trace-ground-tracking` now includes sampled per-segment atlas geometry
and untruncated per-line presence records. `--review-ground-scene` exposes review
controls when investigating a known gameplay scene whose object-model state cue
is missing; it does not force recognized state or relax camera/atlas acceptance.


## Recorded walking route investigation (2026-09-27)

Repository-root `hkv-route-stability-20260927/REPORT.md` retains 40 complete
replays: 20 investigative trials and 20 consecutive trials on the final build.
Rejected history/quality experiments remain in the evidence directory and were
reverted from active source. Three additional candidate repairs addressed specific
defects, but were also reverted after the final accuracy gate failed. Their
source, regression tests, and task-only patch remain in the evidence directory:

- A zero global correction preserves the existing local image reference; repeated
  confirmations must not round away fractional travel. The archived solver loses
  an entire -8px synthetic displacement; the changed solver measures it exactly.
- An absent ground solve follows the measured published fallback pose. When
  current floor pixels are available, seed that capture immediately at the same
  position. The next capture must independently verify texture, and persistent
  atlas coordinates remain unchanged. Do not mark the failed capture verified.
- Capture intervals above 100ms can widen local search within bounded image
  overlap. Larger published catch-up requires at least six independent texture
  patches and error at most 12. Ordinary-frame and unsupported teleport limits
  remain strict. The archived solver fails a 300ms/128x92px synthetic motion;
  the changed solver measures the expected translation.

The final twenty live replays had no multi-second ground lockup; worst private
and published-ground interruptions were approximately 74ms. They contained four
held-pose samples and averaged 49.1–54.1 solves/sec per run. Endpoint errors ranged
0.46–84.7px, with 24.9px median. Mean endpoint error and its spread were worse than
in the three fresh-atlas baseline runs. These sequential, unequal samples do not
establish a complete accuracy/consistency improvement. The 60 FPS, zero-drop
runtime goal remains unmet. Full release suite: 747 tests, four skipped, no
failures. The normal app and active tracking source were restored to their exact pre-task
versions. No candidate passed the complete runtime gate. Read the report before
reapplying any experimental repair.


## Six-hour atlas investigation (2026-09-27)

Repository-root `hkv-atlas-stability-20260927/REPORT.md`, `report.html`, and
`EXPERIMENTS.md` retain the complete comparisons, rejected hypotheses, raw logs,
and exact rendered-frame scores. Twenty unchanged-build long replays and six
original-tracker controls used native capture, two drawables, and foreground
Vision. Three additional commands exceeded the protocol's iteration-label limit
and never started; the harness now separates wire labels from evidence IDs.

Median per-run camera-error p95 was 17.60px versus
25.98px in controls. Median tracker execution p95 was
5.72ms versus 9.48ms.
13/20 returns were within 2px, versus
2/6 controls. Worst candidate camera error was
73.16px. Landing recovery and persistent atlas
misplacement remain unresolved; the complete 60 FPS/zero-drop target is unmet.

Trusted references survive measured fallback handoffs; unmeasured held poses
cannot reseed tracking. Masked tiles retain their reference pixels and IDs.
Recovery reports measured photometric error. Additional first-view verification,
strict recent-texture preference, and bounded temporal recovery have independent
pixel regressions. Duplicate vertical scans are cached without decision changes.

The original short route was rechecked with markers off and ordinary defaults:
3 persistent floor segments, 0
sampled frames with false persistent lines, median feature-entry delay
0.351s, and 75/
75 returning feature IDs retained.
The earlier short-route baseline delay was 2.42s; this investigation retained
those recognition changes and verified them again.

Release suite: 773 tests, four skipped, zero failures. Three scorer arithmetic
checks verify one initial coordinate alignment without removing later drift.
Ground truth is diagnostic only. Native capture and two Metal drawables are
defaults; `--capture-fixed-refresh` and `--presentation-three-drawables` retain
the measured control settings. User-activity and interactive-worker experiments
remain disabled because paired runs showed no gain.


## 2026-09-28 stability continuation

Global relocalization uses observed foreground-filtered detector rows before local temporal stabilization; `--global-search-stabilized-rows` retains the comparison path. Local tracking remains stabilized. The two-hour measured continuation produced19/20 returns within2px versus6/8 same-day controls, median per-run exact-frame p95 13.13 versus19.04px. Worst error remains worse,70.56 versus48.38px. Solve rate50.08–59.72Hz with zero measured worker discards does not mean zero capture gaps:4–631 game frames/run were unobserved. Short-route pose improvement is unproven despite retained feature continuity. Exact rendered-frame IDs, fixed initial alignment, immutable build metadata, invalid replay exclusions, and isolated atlas data support these measurements. Replays apply recorded hero poses as well as input events; camera ground truth is scoring-only.

Bounded long-row nomination and coherent broad recovery have regression/stress coverage, but neither accepted a recovery in the20-run live cohort. Multi-row and pending-verification experiments remain diagnostic prototypes. Capture-gap motion loss, descent texture failure, gradual local drift, and failed second confirmation remain open. Detailed report and traces: workspace `hkv-stability-20260928/REPORT.md` and `review.html`.

Ground edge quality can now be measured independently with manual source-pixel labels: see [Ground labeling](GROUND_LABELING.md). Do not interpret row-count agreement or a correct return as exact edge detection or accurate intermediate atlas placement.

## Bounded affine texture experiment (2026-09-28)

`GroundTextureRegistration` optionally refines an already accepted integer
translation. `--ground-texture-subpixel` enables fractional translation;
`--ground-texture-affine` also tests a shared six-parameter warp across ground
patches at multiple elevations. The image center is a fixed pivot. Bilinear
sampling, robust fitting, bounded deformation, foreground exclusion margins,
and disjoint fit/validation pixels constrain the experiment. Camera output
remains a translation; atlas geometry is not warped. Hacker transforms and
rendered-frame IDs are diagnostic only. Default remains the integer matcher.

Nine complete, interleaved live replays (three per mode) did not establish an
overall stability gain. Median per-run camera-error p95 was 21.77px for integer,
26.55px for subpixel, and 25.13px for affine. Worst errors were 87.34, 41.02, and
60.57px respectively, so neither means nor tails establish the same ranking as
medians in this small, variable sample. Affine reduced median per-run camera-step
error p95 from 0.94 to 0.36px, but sustained atlas-position drift remained.
Median per-run tracking p50/p95 rose from 2.03/4.66ms to 2.41/5.09ms. Both
refinements remain opt-in. These are tracking timings, not display frame rates.

The largest affine-run failure began with a diagonal camera snap while the
motion bridge recovered horizontal motion only. An integer control missed a
similar snap while holding its previous pose. Errors then persisted through
locally verified tracking until later global correction. Audit full 2D fallback
recovery and trusted-reference handoffs before widening affine freedom. The
cohort has no saved image pairs to prove which row hypothesis failed.

The replay harness now validates duration, all 158 input transitions, source
build hash, and mode before scoring a run. Two earlier partial recordings are
archived and excluded. `--allow-background-path-playback`, together with
`--enable-automation-control`, permits explicitly scheduled replays after focus
loss; ordinary physical input still requires focus and the main-thread
heartbeat/watchdog remains active. This option is off for normal launches.

100 selected release tests passed, including known affine geometry, exact
integer round trips, uninformative/clipped input rejection, and existing
tracking/input regressions. Full logs, exact-frame scores, source snapshots,
and failure traces: workspace `hkv-affine-development-20260928/RESULTS.md`.

## Bounded 2D snap recovery experiment (2026-09-28)

`--ground-snap-recovery` retries failed reference comparisons within +/-96px
in both axes. It samples observed edge islands within a balanced 32-patch
budget and combines floor votes within the existing +/-3px texture band.
Broad support, ambiguity, exclusion, and placement checks remain. A bounded
asynchronous pixel audit and exact pre-search-state replay permit reproducible
failure analysis. Camera telemetry is scoring-only; normal startup stays on
the previous integer path.

Four of 128 captured failed comparisons were recovered with 0.14–1.30px
displacement error. All 93 selected release tests passed. Six complete
interleaved live replays produced seven recovery outputs with camera-step
error at most 1.03px. Median per-run camera-error p95 improved from 21.44 to
15.48px, but worst error increased from 52.56 to 76.11px. Tracking p95 rose
from 4.63 to 12.38ms (median of per-run percentiles); logged vision supersession
rose from 1 to 117 frames. Tracking ran at median 58.95 versus 58.52 samples/s,
with Metal submission medians near 60 FPS. These rates do not imply zero drops.
The experiment remains opt-in because the accuracy/cost tradeoff is unproven.

The worst recovery-run error began at 57.069s: a (-32.54,-44.89)px camera snap
produced a held pose and denied atlas writing. Subsequent locally verified
tracking carried the bad origin until global correction. Wide search produced
no accepted proposal at that onset. Next investigate trusted versus provisional
reference coordinates across fallback, and avoid costly wide retries when
trustworthy recent evidence suffices. Unconditionally preferring fresh fallback
references could instead preserve their origin bias. Full evidence, timings,
source snapshots, and limitations: workspace
`hkv-snap-development-20260928/RESULTS.md`.
