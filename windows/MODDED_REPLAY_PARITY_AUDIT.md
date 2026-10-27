# Modded recording and replay parity audit — 2026-10-27

Audited source: `661b5e3`. Scope: recording/replay, preparation, persistence,
diagnostics and test wiring. This is not a whole-app parity certification.

**Windows recording/replay is incomplete relative to Mac.** Both record player
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

## Confirmed gaps

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
