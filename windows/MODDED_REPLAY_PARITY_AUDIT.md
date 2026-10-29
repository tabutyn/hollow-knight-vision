# Modded recording and replay parity audit — 2026-10-27

Audited source: `661b5e3`. Scope: recording/replay, preparation, persistence,
diagnostics and test wiring. This is not a whole-app parity certification.

**Historical finding at `661b5e3`: Windows recording/replay was incomplete relative to Mac.** Both record player
and camera positions. Mac also drives the Knight through its recorded pose trace
during replay; Windows currently plays only button events. The shared receiver
already implements pose playback, so this is primarily missing Windows app wiring.

The Oct 26 Windows recording contains 128 button events and **4,548 samples with
both player and camera positions**. Its existing position trace can be reused;
missing enemy/story checkpoint fields cannot be reconstructed from that trace.

A precise distinction: continuous `playerPose` messages set the Knight's scene,
position, velocity, facing and grounded state. They do not force camera
coordinates. The initial checkpoint restores camera/target state; thereafter the
camera follows the Knight. Vision must still estimate movement from pixels.

## Non-returning route: live Hacker and Gameplay replay — 2026-10-27

Both installed-app replays completed the new 60.58-second route with all 146
input transitions and exactly the recorded final hero position. The end is
157.27 world units from the start. Continuous hero poses prevent cumulative
input drift, but wall-clock scheduling still produces transient differences:
Gameplay median hero error 0.103 world units, maximum 3.836. Camera transforms
are not forced during playback. This is not frame-identical simulation.

The audit enumerates all 3,633 Hacker and 3,634 Gameplay Unity telemetry frames
with no missing Unity sequence numbers. Capture, analysis and display omissions
remain explicit. It samples actual WPF image placement and matches the image's
rendered frame ID to receiver telemetry. WPF publication is not monitor scanout.

- **Hacker display:** median 0.82, maximum 1.42 tracking-pixel camera error;
  consistent with integer image-placement rounding. All 1,816 scored 30 Hz
  deadlines were within 2 pixels; two startup deadlines were unscored. The
  background visual solver nevertheless drifted to 263.62 pixels, so the
  aggregate replay divergence flag does not describe Hacker's displayed camera.
- **Gameplay display:** median 33.03, maximum 190.99 pixels, final 20.80 pixels.
  Only 28 of 1,818 fixed 30 Hz deadlines were within 2 pixels. There were 2,791
  analyzed samples marked verified despite greater than 8 pixels absolute error.
- **Timing:** 156 Hacker and 95 Gameplay 30 Hz deadlines reused an image.
  Image-age p95 at those deadlines was 66.6 ms and 52.3 ms respectively. These
  results do not establish steady, fresh, correct 30 fps output.
- **GPU:** 12 point samples per run; CUDA and Windows Graphics Capture throughout.
  Gameplay GPU load 12–39%, CUDA search median 0.71 ms, encoder/decoder both zero.
  Sparse utilization samples cannot exclude brief spikes, but do not support GPU
  saturation or video encoding as the main explanation for this run's drift.

Errors use 640x360 tracking pixels (half the 1280x720 game capture dimensions).
Gameplay removes one initial translation only; Hacker uses absolute coordinates
reconstructed from its displayed image bounds. No endpoint fitting or tracker
interpolation hides drift. A 2-pixel threshold is an explicit audit target.

The first preparation attempt also exposed a real lifecycle bug: capture resumed
at the previous endpoint before checkpoint restoration. Capture now stays stopped
until the restore acknowledgment, then seeds and validates fresh references.
Both subsequent replays passed preparation. The transient hammer was the empty
Hacker-atlas placeholder; it now says “Waiting for a matched game frame…”.

Priority work: independent keyframe/atlas relocalization and stronger confidence;
GPU coarse-to-fine recovery for unexpected camera jumps; publish pixels, camera
and overlays under one capture ID; decouple Hacker live frames from atlas staging.
Use fixed-tick playback with applied-pose acknowledgments for stricter benchmark
repeatability. Do not feed telemetry into the Gameplay solver to hide errors.

Installed assembly SHA-256:
`4B67021430ED3745C298C833DCB59CF306C60958A5BFBA2FD81B4444B4734BE2`.
Canonical build passed 986 core tests, 69 UI regression groups and shared receiver
protocol/state tests. Evaluator tests cover missing frames, retained drift and
absolute Hacker scoring. One route/run per mode; no Mac runtime claim.

[Measured report](audits/2026-10-27-dual-mode-replay.json) ·
[Comparison chart](audits/2026-10-27-dual-mode-replay.png) ·
[Evaluator](benchmarks/evaluate-replay-frames.py).
Raw `frames.json` and `evaluation.json` remain in the app's benchmark directories
listed in the report; original and replay path files remain in the user's paths
folder. Per-frame CSVs are under the workstation's
`ballroller-2026/.tmp/hkv-new-route-audit/{hacker-valid,gameplay-valid}`.

## Windows integration update — 2026-10-27

The seven orchestration gaps below now have Windows implementation and regression
coverage. This update does not certify the entire tracker or Mac runtime.

- Desktop and standalone host schedule recorded player poses and inputs on one
  receiver session. Missing pose capability fails explicitly. Input leases,
  monotonic pose sequence, scene identity, cancellation and neutral release are tested.
- Recording requests an acknowledged full checkpoint. A live K recording retained
  entry gate `top2`, seven enemies, story flags and visited scenes.
- Desktop replay creates isolated `atlas-v1/benchmarks/<id>/settling` and `run`
  stores. It restores once, requires four fresh stable pose/ground/world checks,
  resets the visual epoch, and checks again. User Auto Save is restored afterward,
  including failure/cancellation. Each stage has a 30-second readiness deadline.
- World integration, tile mutations and disk snapshot replacement share one
  bounded worker. Camera analysis uses the current visual pose plus the committed
  world offset; it never substitutes an old integrated frame's camera position.
  Slow-save tests verify serialization and that queued frames remain bounded.
- Replay's right-click menu selects full trace (default) or stop on divergence.
  Both exact-frame streams feed evaluation. Each benchmark saves `evaluation.json`
  alongside the recorded replay path; completion is separate from tracking success.
- Traces now include ground timing, stable room identity/revision, recovery state,
  local reference/displacement and the masked floorless estimate when used. Windows
  does not manufacture a second coarse candidate where its current bridge computes
  only the masked estimate. Recording metadata no longer claims replay preparation.
- Integration tests exercise actual pose messages, full checkpoint fields, missing
  capability, preparation order, scene boundaries, cancellation, and slow persistence.
  Shared receiver protocol tests still do not execute Unity `Mod.cs`; the installed
  receiver was exercised in the live replay below.

Live route: original `9756db31-bd5d-4195-8754-403ce13fb45e`, replay
`cc178cc7-308d-4144-a02f-430c912e75bb`. All 128 input transitions preserved.
Hero distance from the source at equal elapsed time: median **0.197**, p95 **0.445**,
maximum **0.971 world units** (previous input-only median: 22.115). This establishes
substantially closer trajectory fidelity, not frame-identical simulation.

Exact rendered-frame camera comparison (640×360 solve, one initial translation,
3,309 matched frames): median **35.55 px**, p95 **40.81 px**, max **59.25 px**;
endpoint **0.84 px**. Previous input-only replay median: 1,407.82 px. The longest
recorded analysis interval fell from **3.330 s to 0.109 s**. Sampled camera/world
submission work was at most 0.502 ms while sampled save durations reached 10.73 s.
The watchdog found no sustained error above its 160 px threshold; that is not a
zero-error verdict. Atlas admission still pauses when persistent recovery lacks
searchable reference evidence, although local camera tracking continues.

Existing old recordings still lack enemy/story checkpoint state. Pose replay
cannot reconstruct that missing state. Cross-room command order has regression
coverage; this live route remained in `Tutorial_01`. No Mac runtime test was run
on this Windows workstation. The standalone host has no desktop visual/atlas
lifecycle and explicitly reports `restore-ack-only` preparation.

Live-tested build (`2D256F8C…5554879`) passed 986 core tests, the UI suite
(reporting 69 regressions plus the replay integration checks), and shared receiver
protocol tests with CUDA/GPU capture required. A second independent live replay
(`506721d3-66aa-42aa-b018-685946e2f8bf`) preserved all 128 transitions. Hero route
distance: median 0.224, p95 0.527, max 1.057 world units. Camera error: median
**2.55 px**, p95 **14.90 px**, max **57.74 px**, endpoint **0.26 px**; longest
tracking interval **70.4 ms**. Its report contains 3,406 evaluated samples and
`completed=true`. Variation between runs remains visible; the original trace
has incomplete world state and playback does not control animation/RNG timing.

Final release (`03A86A52…9A9A1404`) also blocks live pointer input during replay
and checkpoint capture. The full build, tests and installed-file verification
were repeated after adding the pointer-isolation regression.

Machine-readable results: [Windows integration evidence](audits/2026-10-27-windows-replay-integration.json).

## Findings at the audited commit (before integration)

### P1 R1: Windows does not replay recorded player poses

**Mac:** playInputPath schedules both button events and filtered groundTruthTrace poses. applyPlaybackPoseLocked sends playerPose commands with scene, position, velocity, facing and grounded state.

**Windows:** Desktop ReplayPathAsync iterates PlaybackStates only. Host InputPathPlaybackRunner also sends states only. ReceiverPlayerPoseCommand exists in Core, but ReceiverClient has no pose send method and production callers do not create/send pose commands.

**Effect:** Buttons alone do not reproduce the recorded trajectory. This explains a concrete replay-parity gap behind the observed route divergence.

**Acceptance:** Use one live session to negotiate pose capability and schedule input plus recorded poses; test actual outgoing poses, sequence/order, scene boundaries, cancellation and receiver application. Compare actual hero trajectory against the source recording.

Sources: [GameControlForwarder.swift:587](../macos/Sources/HollowKnightVision/GameControlForwarder.swift#L587), [GameControlForwarder.swift:1259](../macos/Sources/HollowKnightVision/GameControlForwarder.swift#L1259), [WindowsGameControlForwarder.Replay.cs:67](src/HollowKnightVision.Windows.App/WindowsGameControlForwarder.Replay.cs#L67), [ReceiverProtocol.cs:343](src/HollowKnightVision.Windows.Core/ReceiverProtocol.cs#L343), [ReceiverClient.cs:107](src/HollowKnightVision.Windows.Core/ReceiverClient.cs#L107).

### P1 R2: Windows records an incomplete, unvalidated start checkpoint

**Mac:** K recording requests captureCheckpoint, waits for accepted acknowledgement, and records the returned full checkpoint. The receiver requires the Knight to be grounded and still and includes entry gate, enemies, first-region flags and visited scenes.

**Windows:** StartRecording constructs ToCheckpoint from latestGroundTruth without a freshness/standing-still/full-capture acknowledgement gate. EntryGateName, Enemies and story/visited-scene fields are null. It can start with a null checkpoint.

**Effect:** Starting state is less reproducible; physics, enemy state, first-entry events and cross-room restore can differ.

**Acceptance:** Capture through the receiver before starting the recorder; reject moving/unavailable/stale state, handle timeout/rejection, and round-trip all checkpoint fields. Existing recordings retain their missing-state limitation.

Sources: [LiveCaptureModel.swift:1237](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1237), [GameControlForwarder.swift:386](../macos/Sources/HollowKnightVision/GameControlForwarder.swift#L386), [Mod.cs:741](../macos/hollow-knight-input-receiver/src/Mod.cs#L741), [WindowsGameControlForwarder.cs:70](src/HollowKnightVision.Windows.App/WindowsGameControlForwarder.cs#L70), [WindowsGameControlForwarder.cs:578](src/HollowKnightVision.Windows.App/WindowsGameControlForwarder.cs#L578).

### P1 R3: Windows omits Mac replay reset and stable-start procedure

**Mac:** Pauses capture, purges active atlas, restores checkpoint once, verifies fresh pose/ground/committed evidence, purges the settling epoch, and verifies again. Four ready checks at 100ms intervals, 30s timeout per phase, pose tolerance 0.05.

**Windows:** Restores checkpoint, waits a fixed 500ms, then records and plays. Neither desktop replay method resets the visual/atlas epoch nor proves fresh ground, committed evidence or stable start pose.

**Effect:** A pass can inherit a prior endpoint, drift, stale atlas and camera easing. Repeats are not controlled algorithm comparisons.

**Acceptance:** Port preparation as an explicit benchmark lifecycle using an isolated test atlas, preserving user atlases. Prove one restore, fresh generations, stable recorded pose, readiness timeouts, and identical reset state per iteration.

Sources: [LiveCaptureModel.swift:1269](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1269), [LiveCaptureModel.swift:1375](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1375), [LiveCaptureModel.swift:1445](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1445), [LiveCaptureModel.swift:1493](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1493), [WindowsGameControlForwarder.Replay.cs:57](src/HollowKnightVision.Windows.App/WindowsGameControlForwarder.Replay.cs#L57).

### P1 R4: Windows modded persistence blocks camera analysis

**Mac:** Atlas integration and recovery normally enqueue on a dedicated atlasQueue with bounded pumps; synchronous integration defaults to false.

**Windows:** MainWindow awaits AtlasPersistenceBarrier before processing the next analysis frame in Modded mode because live-world writes share the directory replaced by snapshot publication.

**Effect:** Observed 3.318s replay analysis gap and saves up to 3.284s despite unsaturated sampled GPU. Camera motion during gaps exceeds the tracking search range.

**Acceptance:** Separate camera analysis from world persistence ownership. Keep generation and directory-swap safety; test artificial slow saves while camera analysis and pose publication continue. Do not simply delete the current barrier.

Sources: [LiveAtlasPipeline.swift:232](../macos/Sources/HollowKnightVision/LiveAtlasPipeline.swift#L232), [LiveAtlasPipeline.swift:909](../macos/Sources/HollowKnightVision/LiveAtlasPipeline.swift#L909), [MainWindow.xaml.cs:2836](src/HollowKnightVision.Windows.App/MainWindow.xaml.cs#L2836), [AtlasPersistenceBarrier.cs:5](src/HollowKnightVision.Windows.App/AtlasPersistenceBarrier.cs#L5).

### P2 R5: Windows desktop divergence monitoring is disconnected

**Mac:** Prepares/begins evaluation, supplies exact-frame ground truth and vision samples, and aborts playback on sustained error or vision silence.

**Windows:** MainWindow retains an AppendVision branch but the current desktop replay never calls Prepare/BeginEvaluation or supplies ground truth to that monitor. The standalone host monitor is optional and its CLI call supplies none.

**Effect:** Replay completion reports input delivery rather than a measured tracking pass/fail. The previous connection fix did not preserve an active evaluation lifecycle.

**Acceptance:** Wire both streams to one monitor; offer explicit full-trace versus fail-fast evaluation and persist the decision/report rather than equating replay completion with success.

Sources: [LiveCaptureModel.swift:1280](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1280), [LiveCaptureModel.swift:1534](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1534), [LiveCaptureModel.swift:1957](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L1957), [MainWindow.xaml.cs:3444](src/HollowKnightVision.Windows.App/MainWindow.xaml.cs#L3444), [MainWindow.xaml.cs:6936](src/HollowKnightVision.Windows.App/MainWindow.xaml.cs#L6936).

### P2 R6: Windows omits tracking diagnostics and overstates preparation metadata

**Mac:** Populates ground timing, room identity/revision, local reference/displacement, recovery state and floorless alternatives. The preparation metadata corresponds to its implemented lifecycle.

**Windows:** Core schema contains these fields, but the app omits many values when recording. All 4036 original tracking samples have null values for the audited diagnostic fields. Every Windows path nevertheless claims pathPlaybackPreparation=purge-single-restore-settle-repurge-v2.

**Effect:** A schema-compatible file is not equivalent diagnostic coverage. The metadata falsely implies comparable experiment preparation.

**Acceptance:** Populate available production diagnostics and report only procedures actually executed, including explicit input-only/pose-driven and evaluation modes. Test production recorder wiring, not just DTO fields.

Sources: [LiveCaptureModel.swift:3751](../macos/Sources/HollowKnightVision/LiveCaptureModel.swift#L3751), [InputPathRecorder.swift:382](../macos/Sources/HollowKnightVision/InputPathRecorder.swift#L382), [MainWindow.xaml.cs:3486](src/HollowKnightVision.Windows.App/MainWindow.xaml.cs#L3486), [WindowsGameControlForwarder.cs:127](src/HollowKnightVision.Windows.App/WindowsGameControlForwarder.cs#L127), [RecordedInputPath.cs:317](src/HollowKnightVision.Windows.Core/RecordedInputPath.cs#L317).

### P2 R7: Passing tests do not establish functional replay parity

**Mac:** Source contains a complete pose-driven playback and preparation flow; this audit did not execute it on macOS or establish runtime coverage.

**Windows:** TestPlayerPosePlaybackEncodesRecordedGroundTruth only checks JSON encode/decode. Desktop loopback integration uses an input-only path with no recorded pose trace. It proves connection, checkpoint, telemetry and lease handling, not pose playback or deterministic route fidelity. Shared receiver tests compile protocol/server/device with stubs and exclude Unity Mod.cs.

**Effect:** The prior 986 core and 69 UI passes do not validate missing runtime features.

**Acceptance:** Add nonempty pose-trace integration tests and live mod replay evidence; validate checkpoint fidelity, fresh startup, cross-room transitions, cancellation and bounded camera stalls. Measure route equality before comparing tracker scores.

Sources: [InputBridgeParityTests.cs:119](tests/HollowKnightVision.Windows.Tests/InputBridgeParityTests.cs#L119), [InputPathReplayIntegrationTests.cs:77](tests/HollowKnightVision.Windows.UiTests/InputPathReplayIntegrationTests.cs#L77), [HollowKnightVisionInputReceiver.ProtocolTests.csproj:1](../macos/hollow-knight-input-receiver/tests/HollowKnightVisionInputReceiver.ProtocolTests.csproj#L1).

## What is already present

Windows retains ground truth, exact frame markers, low-resolution frames and
tracking records. The recent fix makes desktop replay share its live receiver
connection, restore its checkpoint, renew held keys and save replay evidence.
The shared mod handles full checkpoints, scene checks and newest-pose coalescing.
The restored DLL contains the pose capability literal and `ApplyPlayerPose`
metadata name; static inspection does not establish current runtime behavior.

The Windows validity helper also mirrors Mac's 15-minute/10,000-event/100,000-
pose bounds, but the playback callers use the weaker `Validate` method instead.
That difference should be resolved explicitly rather than inferred from a
schema parity test.

## Porting order and validation

1. Wire full checkpoint capture and recorded player-pose playback into the one
   live connection. Exercise real outgoing pose commands and their application.
2. Port reset/settle/readiness as an isolated benchmark session so tests preserve
   the user's atlas. Verify one restore and fresh visual state for every pass.
3. Decouple camera analysis from slow world persistence, retaining transaction
   and generation safety.
4. Wire evaluation, complete diagnostics, correct metadata and test the production
   paths. First measure route fidelity; only then compare tracker improvements.

No Mac runtime test or new live game run was performed for this code audit.
Mac uses wall-clock pose scheduling and latest-pose coalescing, so its source
alone does not prove frame-identical rendering or fully deterministic world state.
The Oct 26 replay already demonstrated Windows tracking failure; this audit
identifies missing Mac behavior instead of attributing divergence solely to
input timing or GPU throughput. No runtime code changed during this audit.

[Structured findings and evidence](audits/2026-10-27-modded-replay-parity.json).
[Prior measured replay](audits/2026-10-26-input-path-replay.json).
