# Modded Hollow Knight recording and replay audit — 2026-10-25

## Readiness

The Mac implementation can record receiver checkpoints, input transitions,
player poses, tracking results and exact-frame camera truth, then replay a route
and save each iteration. This is a useful controlled experiment facility, not a
complete game save-state system or proof of identical rendered frames.

Windows has the protocol/data types and many equivalent algorithms, but its
live replay integration is incomplete. Do not treat the Windows Replay button
as equivalent to Mac's controlled replay yet. No modded game was launched or
reset during this audit; all runtime claims below are limited to the named tests.

The standalone repository was fetched and merged before this audit; it was
already current. Focused fixes were tested, installed on Windows and pushed in
`befdd54`. Parent BallRoller work was untouched.

## Fixed and verified

- **Held input expired during Windows replay.** `InputPathPlaybackRunner` sent
  only transitions, while the receiver releases input after 250ms without a
  fresh snapshot. Playback now emits full-state heartbeats every 100ms, preserving
  transition order/timing and ending neutral. A 60Hz regression checks a 1.4s
  hold with overlapping buttons against the lease. This fixes the timeline,
  not the separate UI connection-ownership problem below.
- **Rapid Windows recordings could overwrite one another.** The desktop action
  used a seconds-only filename even though the shared store supports unique
  IDs. It now uses the store's timestamp-plus-UUID naming and atomic save.
- **Comparison could crash on empty traces or absent timing fields.** Missing
  measurements now remain null, and incomplete runs produce explicit issues.
- **Unrelated runs could be pooled.** Analysis checks source ID, checkpoint,
  complete input sequence, elapsed source timeline and exact-frame truth.
  Maximum input-event timing error defaults to 50ms and is explicitly configurable.
  Invalid comparisons emit diagnostic JSON and exit 2 rather than appearing
  successful. This gate does not claim complete restoration of the game world.
- **Different builds/configurations were averaged together.** Reports now retain
  runtime metadata and aggregate separately by build/configuration. Runs without
  a Git revision are individually marked unidentified and are not pooled.
- **Mac's two-second settling tail changed the comparison window.** Primary
  summaries now use the source duration and report excluded settling seconds.
  The original recordings remain unchanged. Source content receives a canonical
  SHA-256 so later reports can identify the exact baseline document.

Validation: 970 Windows core tests, 67 UI checks, receiver protocol/state tests,
19 input-path analysis tests and 26 exact-frame camera diagnostic tests passed
locally. Windows build/publish/install/hash verification completed; installed
app SHA-256 is `28D13C36B13FD8B3FD3AD8F06494F3450CAE9BD87DE7CFEA8AF536555762F9BB`.
[Windows CI](https://github.com/tabutyn/hollow-knight-vision/actions/runs/40205336758)
passed. [Mac CI](https://github.com/tabutyn/hollow-knight-vision/actions/runs/40205336781)
validates the shared analyzer and packages the Mac app.

## Remaining findings, in priority order

1. **Windows UI replay competes with the live receiver owner.**
   [MainWindow.ReplayInputPath_Click](windows/src/HollowKnightVision.Windows.App/MainWindow.xaml.cs)
   invokes [InputPathPlaybackRunner](windows/src/HollowKnightVision.Windows.Host/InputPathPlaybackRunner.cs),
   which opens another TCP client. The active
   [WindowsGameControlForwarder](windows/src/HollowKnightVision.Windows.App/WindowsGameControlForwarder.cs)
   retains its own connection. The receiver accepts and serves one client at a
   time in [LocalInputServer](macos/hollow-knight-input-receiver/src/LocalInputServer.cs).
   The new connection can wait without negotiation while the first is active.
   Replay must own the existing forwarder connection, its heartbeat, cancellation
   and telemetry, with manual controls excluded until completion.

2. **Windows recording does not capture a full receiver checkpoint.**
   `StartRecording()` constructs a checkpoint from the last telemetry sample,
   without checking freshness, stillness or grounded state. Enemy snapshots,
   entry gate and story/visited-scene fields are absent. Mac explicitly requests
   `captureCheckpoint`, and the receiver validates and captures on Unity's main
   thread. Windows should await that acknowledgement before starting its clock.
   The Windows checkpoint schema can preserve these fields when loading a Mac
   file; the problem is its own recording entrypoint.

3. **Windows replay does not stream player poses or save measured iterations.**
   `player-pose-playback-v1` and `ReceiverPlayerPoseCommand` exist as types, but
   the Windows runner only sends held-button states. It does not start a new
   recorder with `sourcePathID` / `replayIteration`, collect the full analysis
   stream, or save a comparison recording. It also does not reproduce Mac's
   reset/restore/fresh-ground preparation. Its 500ms delay is not proof of a
   matching start pose. Its global timeout allows only 30s total overhead across
   every repeat, which can expire during repeated checkpoint loads.

4. **Mac replay permanently replaces the active Auto Save atlas.**
   [LiveCaptureModel.restoreAndStartPathPlayback](macos/Sources/HollowKnightVision/LiveCaptureModel.swift)
   calls `purgeActiveAtlas()` before restore, then again after settling. That
   helper deletes the reset archive and legacy trash. Use a separately saved
   atlas copy before invoking the existing replay command. A future benchmark
   session should own an isolated atlas directory and preserve the user's active
   atlas automatically. This audit did not invoke that destructive path.

5. **Checkpoints are partial game state, on both platforms.**
   [Mod.TryCaptureCheckpoint / TryRestoreCheckpoint](macos/hollow-knight-input-receiver/src/Mod.cs)
   save hero/camera transforms, selected story flags, visited scenes and enemy
   snapshots. They do not serialize all player health/soul/equipment, RNG,
   animation/FSM state, projectiles and arbitrary mod state. Enemy restoration
   searches existing matching objects; a destroyed object may not be recreated
   by that method. Pose playback coalesces pending samples and rejects poses for
   another scene. These are sensible protections but do not imply deterministic
   physics, enemies or pixels. Keep save, loadout, mods and invincibility fixed;
   measure actual route/camera differences each iteration.

6. **Large checkpoints can exceed the 4096-character protocol line limit.**
   Checkpoints include unbounded enemy collections and up to 256 visited-scene
   names. A synthetic 50-enemy/150-visited-scene restore command is 6,283 characters.
   Valid snapshot content can exceed the cap enforced by the receiver and
   Windows codec. A safe transport change needs a negotiated bounded checkpoint
   size or chunking, with symmetric client/server tests; never truncate state.
   Small protocol fixtures do not exercise a populated late-game checkpoint.

7. **Windows recording lacks important measurement provenance.**
   `WindowsGameControlForwarder.RecordVisualTracking()` does not supply the
   recorder's ground-tracking duration. Room IDs in coarse samples are currently
   zero. Default recorder metadata lacks an actual Git/source build identity,
   and advertises Mac's `purge-single-restore-settle-repurge-v2` preparation even
   though the Windows runner does not implement it. Correct those fields before
   using Windows recordings for build-to-build acceptance decisions. The analyzer
   now exposes missing values and unknown builds instead of inventing results.

8. **Completion and abort outcome are not persisted as explicit run status.**
   Mac saves interrupted traces, which is useful, but the JSON does not state
   completed/aborted or preserve the divergence decision. The analyzer rejects
   missing events, short timelines and missing truth; those checks cannot identify
   every interruption occurring after the source timeline. Save outcome, abort
   reason, restore/ready acknowledgements and event-send/pose-application timing.
   `replay-input-path.sh` returning `accepted` only acknowledges starting the
   asynchronous operation. It does not mean the replay finished or was saved.

9. **Frozen-input replay does not yet cover the production GPU pipeline.**
   [VisualRouteReplayEvaluator](windows/src/HollowKnightVision.Windows.Core/VisualRouteReplay.cs)
   reruns a coarse 64x36 CPU matcher. `LiveWorldReplayEvaluator` tests persisted
   mapping/loop closure. Neither is a complete replay of the live 640x360 ground
   detector, CUDA/Metal search, presentation and atlas admission. Mac's
   [GroundFrameAudit](macos/Sources/HollowKnightVision/GroundFrameAudit.swift)
   saves full images at at most 10Hz and drops captures while disk is busy; it is
   useful diagnostic evidence, not a lossless capture-rate replay stream.

## Component review and evidence

| Area | What exists / checked | Still requires live qualification |
| --- | --- | --- |
| Install and selector | Windows validates normal/modded DLL layout; receiver README pins game/API generation. | DLL presence does not verify matching versions, hashes or a working loader. Smoke-test the selected modded executable and negotiated capabilities. |
| Controls and background ticking | Shared input bitset, sequence/session checks, applied-state acknowledgements; receiver requests background updates and 60Hz, restores settings on unload. | Sustained movement, jump, attack, inventory/pause, reserved Vision shortcuts and focus changes with the actual loader. |
| Input safety | Receiver watchdog, neutral on disconnect, bounded transitions/acks; Windows heartbeat replay fix tested. | Kill/disconnect/reconnect during held input; ensure game and Vision recover together. |
| Pointer and labeling pause | Pointer protocol and pause lease tests; acknowledged pause, renewal, resume and disconnect restoration. | Label freeze/resume, mouse buttons and wheel without leaking controls into gameplay. |
| Player Ops | Query/apply, invincibility, enemy collision bypass/restoration and reset requests exist. | Real health/soul/charm/hazard state, destroyed enemies, scene reloads, restoring previous player settings. |
| Capture and truth | Receiver samples gameplay camera at pre-render; rendered-frame marker joins capture to telemetry. | Frame drops, resize/fullscreen, room changes, projection changes, marker transport latency. |
| Hacker atlas versus visual estimate | Camera solve uses visual evidence; Hacker/diagnostics consume receiver truth. Windows modded room routing also uses receiver scene names. | Keep oracle accuracy distinct from the visual tracker; room-ID assistance must be recorded when comparing modes. |
| Recording | Source ID, events, tracking, camera truth, low-resolution trace, metadata and optional checkpoint supported. Mac saves atomically with unique UUID filenames. | Checkpoint handshake, large traces, record start while keys held, disk failure, actual saved route evidence. |
| Replay ownership and readiness | Mac owns playback inside the existing forwarder, restores once, verifies fresh hero/camera/ground and records each iteration. | UI/transport lifecycle integration is not covered by pure recorder tests; Windows lacks equivalent ownership and preparation. |
| Divergence | Tests cover sustained exact-frame error and missing Vision output during truth-camera movement. Current guard: 160px for 12 samples; silence 750ms with 48px truth travel. | This is an abort guard, not an acceptable tracking-quality threshold. Preserve failed runs and use tighter accuracy metrics for comparisons. |
| Persistence | Atomic recorded-path storage and scene/world replay tests exist. | Isolated benchmark atlas, interrupted saves, large sessions and complete state cleanup across many repetitions. |
| Measurement | Exact-frame published-camera errors, rejected joins, loss, correction, timing and cross-run spread; new comparison gates/build groups. | More than a few matched frames are needed for useful coverage; inspect missing joins and transition coverage, not just a low error value. |

Receiver protocol tests compile `Protocol.cs`, `LocalInputServer.cs` and
`VisionInputDevice.cs` against stubs. They do **not** execute `Mod.cs` against
Unity, camera hooks, health/physics, actual checkpoint restore or enemy FSMs.
The earlier GPU audit also measured 0% portable-suite coverage of Mac's
`LiveCaptureModel`; high compute coverage must not be mistaken for lifecycle
coverage. A prior documentation-only Windows CI run failed a one-second
concurrent-test startup barrier; the current run passed. No evidence from that
failure establishes a production persistence bug.

## Repeatable test protocol

Use two complementary records:

1. A live modded replay checks input, restore, capture, queues, frame cadence and
   the installed app together. Repeat one source route at least three times per
   build, keep failed iterations, and compare spread as well as mean errors.
2. An immutable full-resolution frame/telemetry sequence should drive the exact
   production tracking pipeline offline. Preserve source timestamps, frame IDs,
   masks/calibration/configuration and isolated state; compare CPU/GPU outputs,
   atlas admission, drift and timings across builds. Building this complete
   runner remains work; existing coarse/world evaluators are narrower tests.

For the upcoming Mac live test:

- Preserve the current Auto Save as a separate saved atlas first; existing replay
  permanently purges Auto Save. Use a disposable game save and fixed player Ops
  settings. Do not interpret checkpoint restore as restoration of the whole save.
- Start the installed `/Applications/Hollow Knight Vision.app` with modded mode.
  Confirm receiver capabilities, enter the chosen room, stand grounded and still,
  press K, traverse a short route, then press K to stop.
- Source files are under
  `~/Library/Application Support/HollowKnightVision/input-paths-v1/`.
  Keep the original file unchanged and retain game/loader/receiver versions,
  save identity, loadout/invincibility, calibration/model hashes and GPU/device.
- Send one replay at a time from the repository root:

```sh
macos/replay-input-path.sh path-SOURCE-TIMESTAMP-UUID.json 1
```

Wait for the matching new recording to be saved with the original `sourcePathID`
and requested `replayIteration` before submitting iteration 2 or 3. An immediate
`accepted` reply is not completion. Do not loop commands without waiting.
If preparation aborts before a recording exists, retain the failure message as
an unsuccessful iteration; do not silently omit it from the experiment.

Analyze baseline-build and changed-build recordings together:

```sh
python3 macos/analyze-input-paths.py /path/source.json \
  /path/baseline-1.json /path/baseline-2.json /path/baseline-3.json \
  /path/changed-1.json /path/changed-2.json /path/changed-3.json \
  --max-input-jitter-ms 50 > /path/comparison.json
```

The report retains per-run exact-frame match/rejection counts, camera error,
loss episodes, jumps/corrections and timing. `buildGroups` contains separate
aggregates; mixed builds deliberately have no pooled top-level aggregate.
`comparisonEligible` checks input/evidence prerequisites, not acceptable tracking
quality. Exit 2 means investigate the listed issues; do not drop that run to
make a build look better. Unknown build identities are explicit.

Before the Windows modded benchmark, complete connection ownership, acknowledged
checkpoint capture, pose streaming, isolated preparation and per-iteration
recording. Then test cancel, disconnect, room transition and repeat completion
with the real game. The heartbeat fix alone does not satisfy that gate.
