# One-second tracking replay

This Windows development workflow evaluates the visual camera tracker against
the mod's rendered camera coordinates. Receiver coordinates are used only for
scoring; they are not available to `TrackingStepProcessor`.

Select **Modded Hollow Knight**, enter an existing save, choose a recorded path,
then click **Step 1s** in Gameplay. The receiver restores the starting checkpoint
and pauses while Vision reconstructs tracking state. Each click evaluates the
next second, stopping immediately on the first error above 5 pixels, processing
time above 33.33 ms, an unavailable exact frame, or a repeated game frame. Coordinates
are measured in the captured tracking image (currently 640 × 360). Only a complete
passing second advances the saved boundary. The final partial second uses its
actual frame count.

Receiver 0.9.5 is required for room-mask checkpoint capture. Receiver 0.9.4 introduced `controlled-frame-replay-v1`. A recorded second advances
30 simulation ticks of 1/30 second, independent of wall-clock game/capture delays.
Each command sets Unity's capture delta, advances exactly one tick, reapplies
the recorded hero pose in LateUpdate, then pauses when the gameplay camera
renders. The camera follows the hero normally; no recorded camera answer is
sent to the game or the visual tracker. The host checks the acknowledged delta
and waits for scoring before sending the next command. Reports retain every
command ID, simulation delta, rendered/captured frame pair, game-command wait,
and capture wait separately from tracker processing time.

Each telemetry sample carries the completed command ID as well as its real
Unity frame ID. Vision scores the first available exact pixel/telemetry pair
at or after that command's render, while simulation remains paused. Later
paused renders retain their own IDs and freshly sampled truth; they are never
relabeled as an older frame. This tolerates a compositor skipping a presentation
without skipping any simulation tick. Unscaled effects can continue during
the wait; this is controlled simulation, not a complete Unity process snapshot.

Hero coordinates and velocity are interpolated from the full recording at
tick endpoints. Buttons are sampled at tick starts; sub-tick taps are therefore
quantized. Held keys survive second boundaries. Source scene transitions are
rejected rather than interpolated between rooms. Ending, cancellation, lost
connection, or pause-lease expiry restores the previous capture clock.

The game remains paused after each attempt. The button becomes **Next** or
**Retry**. Its context menu can restart from the beginning in a new session or
leave step replay and resume the game. Existing sessions are retained.

## Warm-up

Before scored frames, Vision reserves CUDA buffers for the broad search and
processes at least 180 untimed frames over at least two seconds in a disposable
tracker. These include sustained small movement to mature global features,
reversals, sudden vertical shifts, and fractional movement that exercises subpixel fitting and fine consensus. GPU launches and CPU
JIT paths are exercised before the recorded tracker is created. The throwaway
tracker also calls feature recovery explicitly after each warm-up observation;
ordinary successful registration does not otherwise exercise that rare branch. Warm-up runs
once per process and image size. Warm-up history never enters the measured
tracker or its initial alignment. Temporary warm-up/decompression allocation
debt is collected before the first controlled simulation tick.

The strict replay enables a broad search every frame, including large changes
without a smooth-motion prediction. It distributes up to 32 disjoint patches across visible floors and verifies independent patch motion near the broad-search result. A spatially distributed majority must agree before this refinement rejects foreground parallax; only the agreeing patches enter subpixel fitting. It uses the production ground/visual camera
implementation with one consistent ground-plane calibration across anchor and
recent references. The sparse matcher’s older displacement-dependent shrinkage
is disabled for this dense consensus path. When the durable anchor stays unavailable
for at least three consecutive recent-reference matches, the tracker retains
an overlapping recent reference until it is 0.2 seconds old or travels 48 pixels.
Every current frame still receives a broad search and its own pose. This limits
accumulated short-baseline registration error; anchor recovery restores the
normal cadence. Reset and global correction cancel retention from the old basis. It uses background global search and a fixed two-observation
handoff so saved inputs can be reconstructed deterministically. Slow worker
completion is awaited at that boundary and counts against the live frame budget;
the harness never applies a result earlier just because a worker ran faster. The normal live Gameplay search policy remains separate
until the experiment is validated.

Long-baseline matches also fit individual patches at subpixel precision. A
spatially spread cluster must agree before replacing the joint fit; equally
supported nearby motion groups prefer the slower ground layer. Held-out samples
must improve before accepting the refinement. This separates small foreground
parallax differences that integer displacement voting cannot resolve.

When floor references fail, strict replay can register textured surfaces such
as a remaining wall. CUDA scores every integer displacement within 96 pixels
of the predicted position in both axes; independent patches then verify the
result and refine subpixel precision. Full sampling avoids aliases from sparse
search lattices on detailed textures.
The fallback selects up to 16 patches from connected textured regions, rejecting
isolated particles. Agreement must cover a majority of visible patches, and
spatial spread may be vertical so narrow walls remain usable. Competing peaks
receive held-out subpixel validation. Masks still exclude HUD/player regions,
and textureless or inconsistent matches are rejected. The fallback is warmed separately before scoring. Neither this
fallback nor subpixel selection receives camera ground truth.

## Saved conditions and iteration

Data lives under
`%LOCALAPPDATA%\HollowKnightVision\tracking-steps\<recording-id>\<session-id>`.
`current-controlled-v2.txt` selects the controlled session with room-mask restoration. The earlier `current-controlled-v1.txt` session is preserved but is not reused. Earlier wall-clock
sessions and their `current.txt` pointer remain intact; their timing conditions
cannot be reused as controlled-frame acceptance evidence.

- `progress.json`: recording/condition hashes, initial seed, accepted boundaries,
  and the Core assembly fingerprint.
- `seed.json.br` and `accepted-*.json.br`: immutable compressed raw pixels,
  masks, admission decisions, timestamps, rendered-frame IDs, and scoring truth.
- `attempt-*.json`: immutable per-attempt reports with first failure, per-frame
  timing/error/profile, and the hashed evidence file. `last-attempt.json` is the
  latest report for automation.
- `retry-report.json`: an offline recheck of those exact stopped pixels.

Game checkpoints now retain secret-room mask state-machine states, boolean/float variables, and sprite alpha/visibility. Enemy restoration no longer resets every semi-persistent room object. Camera restoration reapplies the separately saved target after `SnapTo`, which otherwise overwrites it. Restores reject missing objects, state names, or variables before changing the checkpoint. Both Windows and Mac checkpoint codecs preserve the optional room-mask payload.

These are not complete Unity snapshots. In-flight mask fades restart their saved state action from the saved alpha; arbitrary animation/FSM timers, broken props, particle RNG, and other unrecorded world state are not serialized. Older recordings have no room-mask payload. Their original scene appearance must be checked before treating a replay as representative. Any reconstructed initial world state must be documented, and changed replay conditions require a new session.

The original October 27 route exposed this issue at second 38: `Secret Mask Top` remained opaque although the recording's low-resolution evidence showed the Knight and floor. The previous 39-second prefix remains diagnostic evidence, not acceptance of the corrected replay. A new run reconstructs that mask's revealed state before the original start checkpoint; inputs and hero trajectory are unchanged. No original recording is overwritten.

The tracking checkpoint is an input journal, not serialized GPU pointers.
Reconstruction replays the accepted prefix through the algorithm and checks
every frame for accuracy without replaying that prefix in the live game.
Reconstruction belongs to untimed preparation; it cannot invalidate a previously
passing prefix merely because decompression/GC or cold preparation takes longer.
Every new live sample and saved failed-segment retry still has the 33.33 ms gate. If a changed
algorithm fails an accepted second, that boundary and all later boundaries are
invalidated. Changed recording or labeling conditions require a new session.
Alignment is established once at the recording start; later seconds never
reset accumulated drift.

Run an offline retry using the installed host:

```powershell
& "$env:LOCALAPPDATA\Programs\Hollow Knight Vision\HollowKnightVision.Windows.Host.exe" `
  --tracking-step-retry '<session-directory>' '<original-path.json>'
```

An offline retry cannot advance progress when its saved attempt contains fewer
than the full expected frames. After fixing a stopped frame, retry the complete
live second. Offline timings should be measured without another GPU test/build
running concurrently.

To measure every accepted frame again under the current installed runtime:

```powershell
& "$env:LOCALAPPDATA\Programs\Hollow Knight Vision\HollowKnightVision.Windows.Host.exe" `
  --tracking-step-verify-prefix '<session-directory>' '<original-path.json>'
```

This stops at the first accuracy or timing failure, writes
`current-code-prefix-validation.json` with the current assembly fingerprint,
and leaves historical attempts and accepted boundaries untouched. Exit code 0
means every accepted frame passed both limits; code 1 means failure. Preparation
and decompression remain untimed, while every tracking call is timed.

## Game checkpoints and limits

Receiver 0.9.2 allows moving or airborne checkpoints only while the requesting
session owns Vision's acknowledged pause. Ordinary path recording keeps the
standing-still requirement. Checkpoints preserve hero pose/velocity, camera and
target positions, enemies, and the existing story state fields. They are not a
complete Unity memory snapshot: animation, camera smoothing internals, and all
enemy FSM state are not serialized. Saved pixel journals provide the exact
algorithm regression input despite those live-game differences.

The present journal contract supports one scene and image size. Scene/resolution
changes stop explicitly. Capture reads stop once preparation has enough seed
frames, and resume only when an acknowledged controlled tick needs pixels.
Latest presentation drains paused backlog safely because simulation cannot
advance again until that tick is scored. Exact frame IDs and original
presentation timestamps are retained. CUDA reuses both device buffers and
large host staging arrays; warm-up reserves the full broad-search capacity.
The solver also reuses candidate-sample storage, and subpixel fitting uses
bounded stack scratch space instead of per-patch temporary lists.
Broad-search setup precomputes per-row source validity masks, retaining every
candidate and sample while avoiding repeated row and exclusion tests. A
100-fixture parity test covers fractional masks and searches outside the image.
A low-error anchor with eight or fewer supporting patches also gets checked
against the recent view. A recent match can replace it only with at least twice
the independent support (minimum twelve), comparable texture error, a short
reference age, and a disagreement between one and six pixels. Subpixel
agreement keeps the longer anchor baseline; ambiguous matches retain the
existing fallback rules. This avoids promoting a coherent foreground
strip when a broader recent ground match disagrees. Selection uses image
evidence only; recorded camera truth remains outside the tracker.
If durable and recent texture searches both fail, the solver may try the
latest verified image within 0.1 seconds. This fallback retains the same broad
search and strict texture-majority checks; it does not eagerly replace the
longer baseline. Failed/provisional placement cannot establish this reference,
and reset or placement rollback restores its ownership with the other state.
Trusted texture references are tried before a provisional reference, whose
origin may have come from coarse fallback motion.
Broad replay subpixel refinement also measures the reverse image pair. The
two offsets are averaged only when both independently pass held-out pixel
validation and their forward/backward disagreement is at most 0.5 pixels.
Fine parallax grouping selects the coherent layer first; reverse refinement
then balances that same layer without changing cluster membership. This passes the complete 1,818-frame saved sequence below; other recordings
remain unvalidated.
Camera-coordinate calibration is applied at the solver boundary, including
reference positions and the inverse conversion for incoming corrections.
Registration coordinates retain their existing normalization, so changing
output calibration cannot change search windows or reference cadence. The
experimental horizontal factor 0.9956 comes from a robust least-squares fit of
strong image/truth pairs in the first 30 seconds of this recording. Of 263 pairs,
ratios farther than 0.005 from their median are excluded, then displacement-weighted
least squares gives 0.995615. This avoids giving short, noisy motions equal weight
with long baselines. It is a fixed calibration, not a per-frame truth correction.
Later seconds check the fixed factor, but have already been inspected during
iteration; this is not a blind validation set. Other rooms and recordings remain
untested.
The vertical factor remains 0.9947.
The broad solver passes independent-consensus provenance into placement
validation. Distinct frames with at least ten agreeing patches, texture error
at most ten, and displacement within 192 tracking pixels on each axis can
accept a sudden camera jump immediately. A four-to-nine-patch consensus can
also qualify after an additional CUDA search of those exact patches over
plus/minus 96 pixels in both axes. Every selected patch must support a peak
within two pixels of the proposed displacement, with error at most ten and
no competing peak within the existing ambiguity margin. This verification
does not change the estimated pose. Repeated patterns, masked pixels, duplicate
frames, and unverified global corrections cannot use this path. This prevents the continuity guard
from discarding an accurate large displacement and displaying a smaller
coarse estimate during shake or checkpoint camera settling.
CUDA stops evaluating a candidate only when too few eligible/remaining
patches exist to satisfy the existing strict-majority rule. No valid candidate
or search displacement is removed. CPU/GPU parity tests cover exact majority
boundaries, masked patches, flat mismatches, and both sample orders.
The bounded candidate builder also supplies a validated sample-layout contract.
GPU upload can reuse its bounds checks for the same image dimensions and
source count; arbitrary input and changed layouts retain full validation.
CPU/GPU parity tests exercise both paths. Matching bounded batches upload
compact displacement and source masks instead of repeating each patch target;
source-position changes disable this compact path. Pixel scoring order stays
unchanged.
Large CUDA searches precompute target-patch mean and energy with separable
integer sums. The largest possible squared sum is below 2^24, preserving the
original float accumulation exactly. These statistics are rebuilt per search,
so mutation or image-size changes cannot reuse stale target data. CUDA can also
stop a patch once its accumulated nonnegative error reaches the existing cost
cap; such a patch cannot provide support or change its capped contribution.
Small searches retain direct statistics to avoid extra kernel-launch overhead.
Host result capacity follows the accepted GPU output count instead of allocating
space for every searched candidate, avoiding unnecessary large-object allocations.
CPU feature matching uses SSE2 byte SAD when available, with a scalar fallback.
Unaligned and final-image-row fixtures check exact scores and threshold ties.
Recovery matching caches candidate floor lines and exclusion masks by target row,
preserving nearest-row ordering and stable ties. A shared scratch array avoids
allocating match lists for rejected candidates; surviving candidates own copies.
CPU feature matching stops a candidate once its nonnegative accumulated error
cannot beat the incumbent. Winning scores and tie order remain exact; boundary
tests compare against full scoring.
Frame profiles separate search setup, aggregate GPU searches, independent
consensus, subpixel refinement, feature matching and atlas bookkeeping. GC
collection counts distinguish allocation stalls from computation costs.
Timing measures the tracking processor; a missing exact controlled frame
separately fails the step. The harness does not yet certify the
latency of atlas persistence or final display composition.

Strict broad replay requires at least eight matching map patches, 75% support,
128 pixels of horizontal spread, and error at most ten before a global
correction can replace the current pose. These are the existing strong-recovery
criteria, also applied after transporting a background proposal to a new frame.
An ambiguous or insufficient refinement retains its rejection instead of
falling back to an unrefined proposal. The legacy sparse loop-closure exception
remains available outside strict broad mode. Strong corrections and lost-anchor
recovery remain available. During second 54, a local estimate within 3.51
tracking pixels was overridden by an approximately 11.28-pixel global correction,
producing an 8.61-pixel error. Its transported evidence had only five inliers
among seven patches. Policy tests cover these measured scores.

## Measured Windows validation — 2026-10-28

The complete 60.577-second recording passed in 61 gated segments (60 full
seconds plus 18 final frames). After the last code change, all 1,818 saved
frames were revalidated together under installed core SHA-256
`B909CF543DFC470166A22481F5C44E494D00B3361AA42B0F2428A86C4195916B`.

| Measurement | Median | 95th percentile | Maximum | Limit |
| --- | ---: | ---: | ---: | ---: |
| Camera error, 640×360 tracking pixels | 1.986 | 3.435 | 3.967 | 5 |
| Tracking wall time, milliseconds | 8.277 | 12.863 | 31.213 | 33.333 |

Every frame used CUDA. CPU/GPU warm-up ran before scoring; tracking-time GC
remained timed. Game/capture waits are excluded under the controlled-tick
contract. This does not certify end-to-end display or persistence latency.
The temporarily paused 2DGS worker was resumed after validation. The original
recording hash remains unchanged. The Windows build passed 1,014 core tests,
70 UI regression groups and the shared receiver protocol tests, and refreshed
and hash-verified the Start Menu installation.

- [Per-frame measurements](audits/2026-10-28-controlled-frames.csv)
- [Accuracy and tracking-time plot](audits/2026-10-28-controlled-tracking.png)
- [Measured summary](audits/2026-10-28-controlled-tracking-summary.json)
- [Run history and provenance](audits/2026-10-27-controlled-step-replay.json)
- [Calibration samples and method](audits/2026-10-28-camera-calibration.json)

This route was used during iteration and calibration. Passing it is not evidence
of the same error bound in other rooms, recordings, or on macOS. Checkpoint
reconstruction and the older recording's missing world-state limitations above
still apply. The strict broad solver is exercised by step replay; these numbers
do not certify the separate ordinary Gameplay pipeline.
