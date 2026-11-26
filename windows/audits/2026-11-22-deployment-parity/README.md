# November deployment coverage and Windows parity

The deployed source range is `b6f647c..8d67fa3`. The Mac coverage run used
`6a15e70`, which has identical Mac sources, because the last two commits in
the deployed range touched only Windows tests and the parity audit.

Run the test inventory from the repository root to audit either the current
checkout or a particular release candidate:

```sh
python3 windows/audits/2026-11-22-deployment-parity/summarize-test-parity.py --target HEAD
```

The report checks new XCTest methods and edits within existing test methods in
the deployment range. Each Windows reference is checked at the selected commit.
It reports intentionally omitted behavior with the Windows tests for the active
behavior. `--require-accounted` fails when a surviving test has neither a mapping
nor a documented disposition. Line counters, live gameplay and GPU execution
still require their own evidence.

## Measured coverage

| Deployed production surface | Covered changed executable lines | Coverage |
| --- | ---: | ---: |
| macOS Swift | 10,652 / 15,323 | 69.52% |
| Bot Python | 4,012 / 5,879 | 68.24% |
| Incremental object trainer | 4 / 8 | 50.00% |
| Python diagnostics and replay tools | 328 / 489 | 67.08% |
| Shared receiver protocol | 61 / 61 | 100.00% |
| Windows receiver defaults | 2 / 2 | 100.00% |

The measured per-file report in `artifacts/audits/2026-11-22-deployment-parity/`
records exact uncovered line numbers and lists files without counters. Swift numbers count
unique executable lines. The summarizer was checked against LLVM LCOV output
for 105 source files from the previous parity audit. The native game receiver
has five changed files without counters, including the live room reload and
Gym entry points. The two Mac Gym observation encoders had no executed lines
in the portable Swift suite. Unit and loopback suites do not measure runtime
game interactions, WPF keyboard focus in a live game, XAML, or installer
scripts.

- [Mac CI run and Swift coverage artifact](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45080490072)
- Bot Python coverage came from 147 tests. Incremental training coverage came
  from 15 tests. The four diagnostics scripts ran 56 tests under coverage.py.
- Shared receiver coverage came from its protocol executable, and Windows
  defaults from the deployed player-operations tests, both under Coverlet.

## Windows port inventory

Atlas paths, Estimated Room Outlines, and Ground Tracking Patches retain
their Debug View choices across Windows launches. Tracking patches default on
and outlines default off. The Windows desktop regression reopens the same
settings, checks that choosing Hacker Path also saves the enabled path display,
and covers missing, partial, damaged, and unwritable preferences.

| Deployed behavior | Windows evidence |
| --- | --- |
| Reset Room returns to the room entrance and clears both atlases | Windows uses the shared receiver reload command and requires a confirmed scene from its current receiver session before replacing the active maps. R works in the Vision window and has a game-focused route scoped to the managed modded process. The desktop loopback test exercises both routes with simulated game focus and checks fresh key presses, modifier rejection, focus loss, active bot-capture protection, preservation on an unconfirmed reset, both active map replacements, profile and saved map retention, and invalidation of stale Gameplay and Hacker frames. The actual reload at the room entrance and physical key delivery with Hollow Knight focused still need live verification. |
| Player Ops and invincibility | Invincibility starts off and applies on a single click. Windows uses the current receiver session's counters for the toggle, keeps unsent numeric edits in the form, and restores the confirmed toggle on rejection. Pending operations serialize updates and block Reset Room. The shared randomizer preserves the current game's invincibility state. Randomization after Label defaults on and saves the user's choice. Windows numeric fields use Enter or Apply. The desktop receiver loopback covers the toggle, rejection, pending controls, normalization, and the saved Label preference. |
| Object training uses available class evidence | Windows uses the shared incremental Python trainer. The detector and raw review filter untrained heads before choosing detections. Model version fixtures cover evidence on promotion and restore. Native incremental training and live detections still need behavior-level comparison. |
| Settings, Clear Save capture, local stencil search, and object detector controls | The draft requires Clear Save during Select Profile setup, offers it in Add until a positive box is present, and restores guidance when a reference is deleted. Eight focused Windows setup and selection tests passed. Settings has a left Atlas, Debug View, and System column. Estimated room connections are hidden by default and can be enabled from Debug View. Menu tracking uses the captured probe positions across frames. Rejected menus leave the overlay clear. Plain text rows place selector boxes beside the matched text and limit recovery to the original language reference. Hacker save-state management and detector parity remain open. |
| Hacker terrain editing | The draft provides Ground, Wall, Spike, Door, and Rooms controls independently of the camera controls. Walls and floors share editable corners. Doors can be drawn along either axis and follow an attached floor. Terrain uses the room coordinate system, and the editor preserves the floor-only document format. Older detector records stay stored and excluded from the editor and overlay. The core tests cover corner movement, room isolation, reload, undo, both door axes, and save failures. Windows retains macOS room calibration when editing the shared terrain file. Live game rendering and behavior after leaving and returning to a room still need verification. |
| Atlas camera shortcuts | The Windows draft cycles both workspaces through Current at 1.1× zoomed out, Current at 2× zoomed out, and Fit. The offscreen WPF viewport regression checks the requested scale and keeps the follow point centered in both Current presets. |
| Offline ground-label evaluation | The Windows host accepts `--evaluate-ground-labels DATASET_DIRECTORY` with optional `--sweep-ground-labels` and Mac-style tuning overrides such as `--ground-threshold=12`. It reads saved frame labels, scores only fully reviewed Train and Check frames in their respective totals, and writes a schema-3 JSON report in `evaluations`. Its base parameters and default sweep candidate use the semantic detector defaults. The test runs both report modes on a saved macOS ImageIO RGB capture and confirms the dataset is unchanged. The Windows reader supports bounded non-interlaced RGB8 and RGBA8 captures. The macOS report types decoded the Windows sweep output. Live label creation and import remain to be ported. |
| Recorded camera diagnostics | The Windows Host evaluates a recorded path with `--evaluate-low-resolution-trace`. It can write the report using `--low-resolution-report` and reconstruct the trace from a saved Atlas using `--low-resolution-world-evidence`. Native Windows reconstruction joins the exact capture timestamp even if Atlas masking removed the barcode. macOS evidence uses its saved wall-clock window and frame marker. Offline visual replay exports the visual trace. Both scoring tools join receiver measurements from the original recording, and the Host scorer accepts it through `--low-resolution-reference`. Regression tests execute the Host against saved traces with both clock formats and confirm that the input and evidence manifest remain intact. A saved macOS run and a 170-frame Windows Atlas were read locally. A fresh prefix replay retained 150 exact receiver matches and marked 137 of them as reliable ground. The macOS report types decoded the Windows results. |
| Game Play visual tracking | Windows rejects fully enclosed shorter floors within the Knight’s height and trims a slight overlap at supported platform tips. Game Play records level walks when frame-matched arrows, visible foot samples, and a verified camera agree. The draft keeps walked spans by room for the current capture and uses one green line over walked columns while keeping tracking boxes visible. Synthetic frames verify dark intervals, reversing, room isolation, and jump rejection. Camera references now need at least one patch width of floor support. Short platforms can show their own box after two adjacent, unmasked, registered observations. Existing live boxes align with the current floor and saved boxes show outside the live image. With the modded game, admitted walks now travel with their saved room observations and are reprojected when recovering a room or correcting the atlas. The store and pipeline regressions exercise reopen, new-column admission, stale pose correction, and room isolation. Game Play detects walls from visual edges and textured platform faces. It joins nearby visible floor endpoints and uses sustained, corroborated movement contact to extend wall evidence. The overlay draws corner markers and horizontal joins. Walked-ground reopen in normal game mode, wall evidence in bot observations, and live Windows recordings remain open. |
| Atlas objectives, jumps, one-way drops, and path display | The draft builds a directed graph from authored Hacker terrain. Verified fixtures cover a continuous walked span, the measured jump apex, authored obstacles, one-way falls, and steered branches. Objective is the default Hacker tool. The atlas shows paths beyond the visited tiles, draws the selected route in white, and gates the path display on active gameplay and the Debug View setting. Game Play builds the directed graph from visual floors, persistent walks, and corroborated walls. It advances the automatic objective through reachable shelves, requires observed space along automatic descents, and limits unregistered paths to the originating image. Combat edges cannot cut a previously exposed floor without support. The Hacker Path tool adds and deletes jump and double-jump connections. Saved links use the shared room and scene formats, and choosing Path enables the Debug View setting. Deeper navigation and reward parity, plus live Windows interaction, remain open. |
| Paired bot capture and runtime, human evidence, policy selection, and Gym PPO | K captures both domains from one modded run in either workspace. Normal Game Play produces a visual-only clip. The two observations use the same frame clock and input path, with authored markings and mod actors in Hacker and current visual detections in Game Play. The shared Python fixture verifies exact actions, compact observation versions, held attacks, and blue-health outcomes. Bot Summary updates when eligible clips are published and previews the stored terrain in green for Hacker and blue for Game Play. The Windows host supports the trainer’s native policy verification command and checks both checkpoint hashes and CPU logits. Bot Summary selects the latest complete verified checkpoint for each domain, or honors an explicit rollback through the previous newest version. The native policy input history matches the trainer field by field across all 16 deployed checkpoint schemas, including masked visual captures, previous controls, landing motion, and room resets. The shared trainer uses an OS file lock on both platforms for checkpoint publication and Gym worker ownership, including the queue time limit. Gym subprocess replies use a complete-line read with a deadline on both platforms. B toggles modded Game Play or Hacker playback using the selected domain and latest verified policy. The input bridge revokes stale controls, releases on deactivation and mode changes, and yields to manual input. The startup flow accepts the highlighted profile and pulses X/Z through the opening until fresh HUD confirmation. It works before a model is available and stops at gameplay if no checkpoint exists. Native game runs, dedicated slot-4 archival, and live route and Atlas coverage inputs remain open. The Hacker Gym executable now returns authored terrain, frame-matched mod actors, Atlas footprint, route reachability, directional movement rewards, and stationary push evidence through the shared Gym pipe. Its default objective follows the authored upper-right platform, and the encoder preserves fall steering and reward history during camera calibration changes. Game Play Gym reads Unity RGB frames and uses Windows ONNX detection, captured HUD stencils, visual ground and wall tracking, and verified episode Atlas footprints. The authored route supplies guidance and reward independently of visual actors and health. The Windows subprocess fixture changes mod actors, camera, and health and checks that the visual features remain identical. Game Play Gym loads the active detector’s verified ONNX calibrations and adds their receipt and model digests to the training identity. The interchange fixture exercises default and explicit Knight calibrations and reads the recovered confidence from the policy input. Physical Windows gameplay and live end-to-end training remain open. |

## Reproduce the changed-line report

Fetch the Swift JSON artifact for the matching revision and supply the coverage.py
JSON and Coverlet LCOV reports that were collected from that same source. The
script keeps unmeasured files out of the denominator and lists them separately.

```sh
python3 windows/audits/2026-11-22-deployment-parity/summarize-coverage.py \
  b6f647c 8d67fa3 --swift-json swift-coverage.json \
  --python-json bot-coverage.json --python-json object-trainer-coverage.json \
  --python-json diagnostics-coverage.json \
  --lcov receiver.info --lcov windows-defaults.info
```

[Windows CI run 45084072013](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45084072013) collected desktop core, offscreen WPF, and receiver protocol LCOV
reports during the canonical build using Coverlet's custom-runner mode. The
instrumented executables are run directly so they are not rebuilt before
collecting counters. [Coverlet custom runner documentation](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/GlobalTool.md)

[Native Windows terrain coverage](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45086876700)
reached 423 of 438 changed executable lines in the Core terrain editor and room
projection. The [Tab framing run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45087249498)
reached 10 of 20 measured changed lines in the viewport policy. Those
runs lacked WPF counters because Coverlet could not resolve desktop framework
types. The coverage build now stages the WPF tests with their framework and
checks that the offscreen renderer produces WPF app line hits. Installer
scripts and XAML themselves have no executable line counters.

The [walked-ground native test run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45089001817)
passed and reached 291 of 308 changed executable lines in the instrumented
files. The focused platform-patch tests reached 71 of 71 changed Core lines.

The [WPF instrumentation run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45089975353)
passed. Across the draft up to `19bd296`, native suites reached 949 of 1,381
changed C# lines (68.72%). The ground renderer reached 48 of 49 changed lines.
Most window event handlers had counters and received few or no hits.

The walked-ground persistence suites passed through the Windows world
pipeline, including disk reopen, room recovery and Atlas rebuild. The Core tests now compile
the pipeline from the app source, so their execution contributes line hits
to the same report as the WPF app.

The focused production-pipeline run reached all 46 of its changed lines.
Combined with the native counters for the same production revision, the
walked-ground storage changes reached 96 of 134 executable lines. The 38
new or changed live window integration lines still need runtime coverage.

The [native pipeline instrumentation run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45092008893)
confirmed 46 of 46 changed live-world pipeline lines. At `d3eb18b`, coverage
was 1,044 of 1,517 changed C# executable lines across the draft, including WPF.


The [native wall-detector run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45098167394)
passed the installed build and offscreen WPF tests. It exercised 292 of 305
changed executable lines in the wall and corner port. All 17 changed renderer
lines were reached. The 10 changed live window integration lines had counters
and received no hits.

An offline Windows Core replay consumed 1,100 registered frames from King's
Pass and 346 frames from a second recording that included blocked motion. The
detector produced wall and corner evidence in 1,082 and 326 frames respectively,
with right-wall contact in 58 frames of the second run while the recorded player
position remained stationary. Contact used the recordings' verified visual
camera poses. With a match defined by an 8-pixel horizontal distance and 50%
overlap of the shorter span, 3,552 of 6,860 projected authored wall instances
matched the detector in the first run. This measures agreement with existing
labels. The annotations do not include a per-frame inventory of visible walls.


The focused Windows Core navigation tests at `7590b63` reached 454 of 473
changed executable lines on macOS. They cover steered descent, one-way
midpoint selection, jump shelf clearance, and stitching walked ground.
The shared terrain round trip also preserved the current King's Pass
labels and both room calibrations when saved to a temporary file.


The Windows Hacker observation builder processed 169 sampled rendered frames
from the saved King's Pass traversal. All 169 matched the receiver frame,
contained authored walls, retained enemy outcome counts, and passed observation
eligibility. Four contained mod geo-cache positions. The runner used archived
pixels and telemetry on macOS. The paired trainer interchange test uses
controlled Windows-format Game Play and Hacker data.

The portable Windows host verified 32 CPU PyTorch reference rows for each
of the selected local Hacker and Game Play checkpoints. Both snapshots used
the 205-field schema 14 contract. The installed Windows CI checks synthetic
PyTorch fixtures for schemas 14, 15 and 16 using the same host command as
the shared trainer.
