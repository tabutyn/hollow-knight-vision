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
