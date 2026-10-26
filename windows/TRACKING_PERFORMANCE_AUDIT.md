# Windows tracking latency audit — 2026-10-25

Baseline: `3ceda38`, reported substantially worse during Windows gameplay.
Machine: Ryzen 5 5500, GTX 1080. Normal Hollow Knight, 1280x720 capture normalized
to 640x360. CUDA was active before the change; missing GPU dispatch was not the
observed problem.

## Evidence

A 15-second `dotnet-trace` CPU sample of the installed process attributed about
3.59s of managed CPU to `GroundTruthAtlasMosaic.AddFrame`, versus 0.67s to the
ground tracker on the analysis thread. Total managed analysis CPU was 7.76s.
These inclusive sampled CPU times exclude CUDA waits and cannot be added as
independent frame durations. Capture normalization also used about 1.85s on its
separate path.

The newest-pixel atlas still rasterized a temporary full-frame temporal
observation, hashed tile coordinates per pixel, then traversed and looked up the
same pixels again. Ground feature search allocated and copied a 192-byte patch
for every candidate and used interface-indexed pixel comparisons.

## Changes

- Newest-pixel atlas projection stages each touched tile once and writes pixels
  directly. Source mappings are computed once per row/column. Atomic commit,
  duplicate-capture handling, masks, black pixels, transparency, weights and
  rollback semantics remain covered by regressions. Projection overflow limits
  and extreme positive/negative coordinates have a dedicated regression.
- Ground searches compare the source image rows directly. Scoring, search order,
  thresholds, GPU solver, global corrections and trust decisions are unchanged.
- `tracking-gpu.json` now includes analysis/capture rates, queue delay, frame age,
  ground duration, atlas/world duration, pose source, trust, support, error and
  camera coordinates. The atlas/world duration includes post-ground world and
  publication preparation; it is not an isolated `AddFrame` measurement.

## Paired measurements

Old and new release core assemblies ran in the same PowerShell 7.6.5/.NET 10.0.11
harness, using one captured gameplay image. The installed app itself runs .NET 8;
these paired timings compare the implementation and are not a promise of exact
in-game frame times. Atlas runs warmed for 100 frames, then measured 100 frames
per scale, with fractional/negative positions, masks and transparent pixels.

| Operation | Before median | After median |
| --- | ---: | ---: |
| Newest-pixel atlas, 1.0 scale | 5.14ms | 2.08ms |
| Newest-pixel atlas, 0.75 scale | 4.15ms | 1.34ms |
| Newest-pixel atlas, 1.4 scale | 10.17ms | 3.75ms |
| Ground adapter, known-motion replay | 15.00ms | 7.74ms |

All 29 final tile hashes, written counts, observation counts and accepted-frame
counts matched. Ground replay used 30 stationary warm-up frames followed by 90
known horizontal-motion frames over real captured texture. All 120 published
poses, trust decisions, sources and support counts matched. Worst pose error was
0.639px before and after. Mean measured ground allocation fell from 1.64MB to
1.30MB per frame. This is a controlled image-motion replay, not a recorded game
input path or a Mac/Windows end-to-end accuracy comparison.

The installed app's extended live sample remained CUDA-backed and ground
verified in all 11 observations, with a median 24.0 analyzed frames/s and 17.5ms
frame age (worst sampled age 60.0ms). Camera position stayed stationary throughout;
actual traversal, drift and transitions still need live confirmation. The user
was offered a movement check; no movement reply was received during the audit.

Measured results and tile digests are in
[audits/2026-10-25-tracking-performance.json](audits/2026-10-25-tracking-performance.json).
Local profiler trace, original binary snapshot and replay harnesses were retained
in the parent workspace's `.tmp/hkv-*` files. The capture was not added to Git.

Final canonical Windows validation: 974 core tests, 67 UI regressions, receiver
protocol/state tests, hardware-required CUDA checks, and parity inventory passed.


## Follow-up: whole-app choppiness during movement

The user then confirmed whole-app choppiness and moved through the room. The
installed `5137a8e` baseline produced a median 24.98 captures/s and 22.99 analyzed
frames/s across six distinct five-second reports, while using about 5.12 CPU
cores during the 24-second sample. CUDA was active. Camera X changed from about
33 to 1621, so this sample includes actual traversal rather than stationary
warm-up. The follow-up managed CPU profile attributed 1.89s to normalization,
5.13s to background global search, and 5.11s to foreground analysis over 15s.
Those are inclusive CPU samples, not mutually exclusive frame durations.

A separate 35-frame GDI capture probe (discarding the first five frames) measured
36.27ms average in capture alone, before normalization or tracking. The desktop
also imposed a 33ms capture interval. Mac already consumes a continuous native
capture stream; Windows was still using synchronous `PrintWindow` readback.

The Windows desktop now uses a free-threaded Windows Graphics Capture stream
with two buffers, drains to the latest available frame, crops the physical game
client area before normalization, and reuses a Direct3D staging texture. Cursor
capture is disabled. Resizes discard clipped old-size frames; cancellation,
source closure and minimized windows do not publish saved pixels. Unsupported
initialization explicitly reports a GDI fallback. The CLI retains GDI. Capture
timestamps use compositor QPC time capped at local frame receipt: this machine
sometimes reports compositor times ahead of receipt, which otherwise produce
impossible frame ages shorter than analysis itself. The hardware test now
rejects future capture timestamps. Reported frame age includes copy/queue time;
the earlier baseline timestamp was taken after capture. Do not compare the two
frame-age metrics as if their starting point were identical.

The normalized image remains 640x360. Exact 1:1 and 2:1 conversion paths preserve
owned pixel storage, source identity, bilinear center sampling and ties-to-even
rounding. In the same PowerShell 7.6.5/.NET 10 harness, 1,000 warm-up conversions
followed by 100 measured conversions reduced 1280x720 normalization median from
5.22ms to 1.12ms; output SHA-256 matched. The app runs .NET 8, so this is a paired
implementation benchmark, not an in-app latency claim. Global descriptor
comparisons now use array indexing and a stable three-candidate insertion buffer
instead of materializing and sorting every match; search thresholds, tie order,
pose verification and correction policy are unchanged.

Validation passed 975 core tests, 67 existing UI regressions, receiver tests,
hardware-required CUDA tests, and a new opt-in compositor test covering fades,
client cropping, resizing, occlusion, cancellation and closure. The uniform-color
fixture disables Windows 11 corner rounding only on its temporary test window;
a separate probe confirmed rounding caused the original corner-pixel mismatch.
The parity inventory check passed but still reports incomplete overall parity.
An existing concurrency-readiness test and an existing GDI fixture each failed
once and passed unchanged on rerun; neither failure was suppressed.

Clean publication also fixes a reproducible packaging problem: the graphics
package requires System.Text.Json 9, but timestamp-based incremental publication
retained an older assembly-version 8 DLL with a newer filesystem timestamp. That
broke the installed selector despite successful source tests. Builds now clean
only their two generated publish directories, with a bounded retry for Windows
pending directory deletions, before publishing and hash-verifying installation.


Final installed verification (process 10632): median 50.87 captures/s, 38.66
analyzed frames/s, and 2.65ms capture readback plus normalization over seven
reports. The sample includes startup settling (17.65 analyzed frames/s initially,
then 37.70–49.62); a later report reached 50.65 analyzed frames/s. All seven reports
used GPU capture and CUDA, admitted verified ground, and had valid frame ages.
CPU use averaged 5.34 cores while processing substantially more frames, so this
does not claim reduced total CPU consumption. The camera remained at X=0: live
movement smoothness and drift still await user confirmation. The installed
window was visually checked for client framing and ground overlays. The user
was asked to repeat movement after the capture update; no reply had arrived at
this verification point.

[audits/2026-10-25-capture-throughput.json](audits/2026-10-25-capture-throughput.json)
records the final measurements, comparison limits, validation and installed
assembly hashes. The final canonical build completed installation verification;
its captured diagnostics use the guard against future compositor timestamps.

## 2026-10-26: persistent-history stalls and inference spinning

The user confirmed movement was still choppy. Process 10632 had remained open
overnight: GPU capture stayed near 43–59 FPS while analysis fell to 8–12 FPS.
A reported frame spent 95ms in ground tracking, compared with 0.87ms in CUDA
camera search. A 15-second CPU trace identified repeated persistent-bank merges,
deep snapshot copies, and exhaustive descriptor comparisons. Inclusive profile
times overlap and are not additive frame durations.

Windows rebuilt and sorted the merged live/retired/canonical feature population
several times per update. It also copied all four arrays on every stored feature
before asking an occupied background worker to accept the snapshot. Mac already
caches the merged bank and uses value snapshots with copy-on-write arrays.

Windows now caches membership with invalidation on promotion, retirement,
collision removal, and reset. Worker admission precedes synchronous snapshot
preparation on the foreground owner. Snapshots copy mutable metadata but share
immutable bank-owned arrays; evidence refresh replaces arrays rather than
mutating them. A view contained within one unchanged ground strip skips rebuilding
its cells. Descriptor comparisons use exact widened integer vectors, preserving
scalar scores, thresholds, tie ordering, and verification. No history cap or
atlas reset is used. Diagnostics now distinguish active features, total stored
features, and persistent segments.

A deterministic occupied-worker workload retained 20,020 features throughout
100 updates. Sequential old/new runs after the test suite finished reduced
median update time from 33.52ms to 13.07ms, p95 from 58.60ms to 17.01ms, and
allocated bytes per frame from 25,574,179 to 949,815 (96.3% less). All 100 poses
remained verified at (0,0). The 262,144-comparison descriptor workload fell from
40.56ms to 4.06ms median with exactly the same score checksum. These paired
benchmarks run in PowerShell 7.6.5/.NET 10.0.11; the installed app runs .NET 8.
They isolate implementation cost and do not establish live movement smoothness.

The freshly installed app still used about five CPU cores. The installed object
detector creates six inference threads on this 12-logical-processor machine.
ONNX Runtime enables idle thread spinning by default; see its
[threading guidance](https://onnxruntime.ai/docs/performance/tune-performance/threading.html#thread-spinning-behavior).
A controlled comparison using the exact installed model/runtime at approximately
30 inferences/s reduced detector process CPU from 4.64 cores to 0.61 when spinning
was disabled. Median inference was 10.63ms versus 10.58ms, p95 16.58ms versus
14.09ms; SHA-256 of every scores/boxes output matched. Windows now disables
intra-op and inter-op spinning while retaining thread counts, graph optimization,
and the same model. This reduces competition with capture, tracking, rendering,
and Hollow Knight. The comparison ran in the same PowerShell/.NET 10 harness,
with the app open during both variants; final app timings are recorded separately.

Five new regressions cover rejected-snapshot admission, failed preparation,
20,000-feature foreground allocations without dropping history, frozen metadata
and evidence, and exact vector/scalar scores including signed-short extremes and
vector tails. Existing recovery, merge, collision, and canonical-search tests also
pass. The full suite passes 980 core tests, 67 UI regressions, required CUDA and
GPU-capture hardware tests, and receiver tests. Missing reported-menu captures
remain an existing fixture skip, not a claimed tested case.

Reproduce implementation benchmarks in separate PowerShell processes (a process
cannot reload different builds with the same assembly identity):

```powershell
pwsh -File windows/benchmarks/Measure-GroundHistory.ps1 -AssemblyDirectory <build-directory> -Output history.json
pwsh -File windows/benchmarks/Measure-GroundDescriptors.ps1 -AssemblyDirectory <build-directory> -Output descriptors.json
pwsh -File windows/benchmarks/Measure-DetectorSpinning.ps1 -Output detector.json
```

The first two require published Core/ILGPU assemblies; preserve the old directory
before building. The detector comparison uses the installed model/runtime without
changing app settings. [Measured results](audits/2026-10-26-ground-history.json)
include fixture limits and final installation/live verification.

Final installed verification (process 29100): 40.36 seconds
averaged 1.87 CPU cores, compared with 5.23 in the fresh-session
sample before the detector-thread fix (64.3% less). Median analysis was
47.48 FPS, capture 53.71 FPS, and ground tracking 4.62ms.
Every report used Windows Graphics Capture and CUDA with no fallback. This
sample includes startup settling, retains 16 stored features, and stayed at
camera (0,0); the user's movement confirmation has not arrived. These readings
verify reduced app CPU, not smooth movement or eight-hour session behavior.
The separate accumulated-history fixture covers the original history-copy cost.
The final canonical build installed successfully, and both installed app/Core
assembly hashes match the publication. Game process and saved atlases were kept.

Follow-up: the user reported smoother movement. A 30.63-second sample of the same
installed process averaged 1.82 CPU cores and 37.85–56.09 analyzed frames/s with
CUDA and GPU capture throughout. Stored features had grown to 107. Camera
coordinates stayed fixed within this sample, so it does not establish tracking
accuracy. The user subsequently reported inconsistent tracking relative to Mac;
responsiveness and tracking accuracy remain separate findings.

## 2026-10-26: sparse ground recovery and atlas controls

The next user report separated improved responsiveness from inconsistent camera
tracking. Windows was missing Mac's direct current-feature-to-bank recovery and
bounded alternative-line challenges. It always ran a pixel-phase search first,
requiring at least six inliers over 96 pixels. Mac first considers four ordered
current features over 48 pixels, verifies the complete visible strip, challenges
other/canonical lines on their existing cadence, and scans all phases only when
no earlier proposal passes verification.

Windows now follows that search order. Search preparation projects current-frame
pixels against existing geometry before committing votes/evidence, clones mutable
feature metadata, and runs only after worker admission. Six nearest candidates,
up to four other unrepresented lines, and missing oldest-line candidates match Mac's
selection without sorting the full persistent bank for every visible feature.
Alternative-line challenges retain the one-second cadence; canonical searches,
current-pixel re-verification, epoch invalidation, pose transport, and independent
correction confirmation remain in force. Four-cell recovery of a 64-pixel camera
offset now passes both foreground and production background tests; occluded and
unrelated pixels fail closed. This is a focused recovery port, not full platform
parity or proof of live drift accuracy.

The angled blue line was an extra ground-camera translation vector drawn whenever
Ground Features was enabled, even with Coarse Motion Overlay disabled. That vector
is removed from the feature layer; ground lines stay horizontal and feature boxes
remain. The separate optional motion diagnostic is unchanged.

Atlas settings now use explicit dark list/selection/hover backgrounds with light
name/detail text. Delete and New no longer share backup behavior: Delete publishes
an empty autosave without retaining/importing the previous generation, while New
keeps a named restore point. Both retain transaction rollback on failed activation.
Tests verify saved states/labels survive and deletion creates no recoverable copy.
Only temporary test stores were deleted; the user's atlas remains untouched.

The canonical Windows build passed 984 core tests, 67 UI regressions, required
CUDA/capture hardware checks, and receiver tests, then installed and hash-verified
the Start Menu copy. The installed settings were visually checked. GPU capture
and CUDA remained active after launch. [Verification details and limits](audits/2026-10-26-atlas-settings-recovery.json)
record the final hashes and stationary diagnostic report. Live tracking improvement
still needs a repeat of the user's route.
