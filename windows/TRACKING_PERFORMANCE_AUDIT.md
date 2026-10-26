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
