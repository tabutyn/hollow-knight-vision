# November deployment coverage and Windows parity

The original deployment spans `b6f647c..8d67fa3`. The Mac coverage run used
`6a15e70`, whose `macos/` tree matches the deployment. The reviewed main
counters use `b6f647c..44989f75e940328c2f83c8ff0e6844ae491a68b2` and include
all measured Windows and shared-source changes in that range. The behavior
inventory below follows the original deployment and its Windows ports.
[Original deployment](https://github.com/tabutyn/hollow-knight-vision/compare/b6f647c...8d67fa3), [reviewed main](https://github.com/tabutyn/hollow-knight-vision/commit/44989f75e940328c2f83c8ff0e6844ae491a68b2)

The source inventory accounts for all **489** introduced or modified XCTest
cases in the original deployment. It found **479** Windows test mappings,
**10** documented dispositions, and **0** unresolved cases. It checks that the
mapped tests still exist at the selected revision. Use `--require-accounted`
to fail on an unresolved case.

```sh
python3 windows/audits/2026-11-22-deployment-parity/summarize-test-parity.py \
  --target 44989f7 --require-accounted
```

## Measured changed-line coverage

| Build and production surface | Covered lines | Coverage |
| --- | ---: | ---: |
| Original deployment, macOS Swift | 10,652 / 15,323 | 69.52% |
| Original deployment, Bot Python | 4,012 / 5,879 | 68.24% |
| Original deployment, incremental object trainer | 4 / 8 | 50.00% |
| Original deployment, Python diagnostics and replay tools | 328 / 489 | 67.08% |
| Original deployment, shared receiver protocol | 61 / 61 | 100.00% |
| Original deployment, Windows receiver defaults | 2 / 2 | 100.00% |
| Reviewed main, hosted Windows desktop C# | 18,545 / 19,678 | 94.24% |
| Reviewed main, shared receiver C# in hosted Windows tests | 119 / 119 | 100.00% |
| Reviewed main, Bot Python in hosted Windows tests | 4,768 / 6,293 | 75.77% |
| Reviewed main, incremental object trainer on Windows | 7 / 8 | 87.50% |
| Reviewed main, incremental object trainer across Windows and Mac | 8 / 8 | 100.00% |

The original local Python runs executed 147 bot tests, 15 incremental trainer
tests, and 56 diagnostics tests. The hosted Windows run used the installed
desktop app, installed Python runtime, and installed Gym encoder. Attempt 2 of
run `45812267076` tested merge commit `42c1450`, whose tree matches reviewed
main. Artifact `15115004222` contains the four LCOV reports and the combined
Python report from that attempt.
[Original Mac run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45080490072), [Windows run, attempt 2](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45812267076/attempts/2), [Windows coverage artifact](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45812267076/artifacts/15115004222)

The eighth changed object-trainer line writes trained-class metadata into the
Core ML model. A local Mac run covered it by exporting an actual trained
checkpoint, checking the class metadata, and running CPU inference. The shared
trainer source matches the version in reviewed main. The Windows run trained
from native captures and loaded the resulting ONNX model through raw detection.
[Windows object trainer run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45801595494)

The coverage script lists executable misses and files without counters.
`Mod.HackerGym.cs`, `Mod.RoomReset.cs`, and `Mod.cs` lack counters in the
Windows receiver report. XAML, installer scripts, and nonexecutable assembly
metadata are outside the measured C# denominator. The original Mac run also
left both Swift Gym observation encoders without executed lines. In the latest
Windows run, the Gym wrapper covered 320 of 351 changed executable lines and
the PPO training script covered 1,067 of 1,779.

## Deployment behavior verified on Windows

| Behavior | Evidence |
| --- | --- |
| Settings, Atlas Debug View, and camera | Atlas, Debug View, and System use the left column. The Atlas settings match across modes, and Hacker retains its bottom camera controls. Tracking patches default on and estimated room outlines default off. Offscreen WPF checks both modes cycle through Current at 1.1×, Current at 2.0×, and Fit, and that path display follows its persisted preference. Choosing Hacker Path also enables that preference. [Tab framing run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45087249498), [Debug View tests](https://github.com/tabutyn/hollow-knight-vision/pull/215) |
| Menu stencils and gameplay recognition | Select Profile requires a visible Clear Save. Add keeps offering it until there is a positive box, and deleting a reference restores capture guidance. Captured probe locations constrain stencil matches. Selector rectangles align with text when the select decoration differs. Reference priority, HUD confirmation, and stale or conflicting menu evidence have regression tests. Screenshot setup is optional, and object detection enables classes with training evidence. [Menu and stencil tests](https://github.com/tabutyn/hollow-knight-vision/pull/168), [profile and HUD tests](https://github.com/tabutyn/hollow-knight-vision/pull/173), [stencil recovery tests](https://github.com/tabutyn/hollow-knight-vision/pull/228) |
| Hacker terrain editing and rooms | Ground, Wall, Spike, Door, Rooms, Path, and Objective are available independently of the camera. The editor tests vertical wall and horizontal floor constraints, attached corners, both door axes, room isolation, reload, undo, and save failure. Authored markings retain their calibration and remain scoped to their room. The native receiver loopback verifies separate authored floors across room visits and floor alignment across a doorway with both contacts. [Native terrain run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45086876700), [room-boundary run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45785879137) |
| Game Play floor, wall, and corner tracking | Registered replay and synthetic frames exercise dark occlusions, changes in walk direction, jumping, support for short platform patches, wall contact, and persisted walls. Frame-matched arrows, observed feet, and a verified level camera establish walked ground. Tests cover rejecting enclosed shorter floors within the Knight’s height, adjusting slight overlap at longer platform tips, keeping a continuous walked span through combat clutter, and restoring ground with corrected Atlas poses. Offscreen rendering tests exercise a single live ground stroke with tracking boxes. The Game Play overlay hides the vertical debug marks. [Walked-ground run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45089001817), [wall and corner run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45098167394), [terrain replay tests](https://github.com/tabutyn/hollow-knight-vision/pull/187) |
| Atlas placement, objectives, and routes | Pose-graph tests cover committed room imagery, walked ground, interrupted initial capture, and saved Mac doorway crops. The route tests cover connected shelving, clearing jumps, directed drops with steering branches, and suppression of duplicate drop links between jump-connected platforms. Hacker defaults to Objective, which accepts clicks along authored paths beyond the visited area and highlights the route. Choosing Path enables the overlay preference and supports editing jump and double-jump connections. Game Play gates its overlay on active gameplay and uses registered or current-frame visual terrain. [Objective tests](https://github.com/tabutyn/hollow-knight-vision/pull/166), [Path gesture tests](https://github.com/tabutyn/hollow-knight-vision/pull/223), [Atlas recovery tests](https://github.com/tabutyn/hollow-knight-vision/pull/222) |
| Reset Room and player controls | The installed desktop loopback verifies that both Atlas replacements wait for a confirmed reset, preserves the saved profile data, invalidates stale frames, and exercises R with simulated game focus. It also checks invincibility starts off, updates once per click and recovers on rejection. The shared receiver command requests a fresh load at the room entrance. [Receiver build and installation](https://github.com/tabutyn/hollow-knight-vision/pull/214), [reset and private skill controls](https://github.com/tabutyn/hollow-knight-vision/pull/211) |
| Object training and class admission | The installed trainer tests initial and incremental training from the native Windows export. The incremental run keeps a certified class with validation-only data, admits the newly labeled Geo class, and excludes a new validation-only class and an uncertified legacy head. The installed ONNX detector loads the exported model and checks its class metadata and decoded output. [Native training run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45801595494) |
| Bot capture, preview, and model selection | K captures both domains from one modded run in either workspace. Unmodded Game Play captures the visual domain. Hacker observations carry authored terrain and mod actors. Game Play observations carry visual terrain and detections. Tests verify the shared frame clock, exact actions, stored clip preview, eligible publication into Bot Summary, and newest verified model selection with explicit rollback. Bot verification compares hashes and CPU logits. Tests cover masked Knight observations and clearing stale input or tracking at a room boundary. [Paired training tests](https://github.com/tabutyn/hollow-knight-vision/pull/216), [masked Knight run](https://github.com/tabutyn/hollow-knight-vision/pull/230), [capture transition run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45792276934) |
| Bot startup and control | Native loopback runs cover B before the first trained model, opening the highlighted save, and pulsing X and Z across conflicting menu cues until fresh gameplay HUD evidence arrives. The tests cover the controller lease, startup heartbeat and stalled capture, deactivation, and switching mode. [Pre-training startup tests](https://github.com/tabutyn/hollow-knight-vision/pull/227), [watchdog tests](https://github.com/tabutyn/hollow-knight-vision/pull/225) |
| Gym rewards and PPO persistence | The installed Gym encoder and simulated receiver exercise both domains, exact action masks, held-attack penalty, blue-health reward, timing, aborts, and cleanup. Both real PPO learners update from Gym transitions, save their private optimizer and actor state, and resume it after restart. The Game Play run loads the active installed detector and HUD stencils. The stationary fixture retains the verified playable model because the new candidate earns no qualifying gain. [Receiver and encoder run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45805590109), [both-domain PPO run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45812267076/attempts/2) |
| Offline diagnostics | Windows Host replays recorded low-resolution camera traces and reconstructs reports from saved Atlas evidence, including archival Mac timestamps and masks. A saved Mac run and a 170-frame Windows Atlas were read locally. A fresh prefix replay matched 150 receiver frames and marked 137 reliable for ground. Windows also evaluates and sweeps the saved ground-label dataset format, including the deployed detector thresholds. The older offline label authoring and import tools fall outside the changed evaluator scope. [Recorded terrain tests](https://github.com/tabutyn/hollow-knight-vision/pull/187), [recorded Atlas recovery tests](https://github.com/tabutyn/hollow-knight-vision/pull/194) |

## Live verification pending

- A modded Hollow Knight session on Windows is needed to verify key delivery
  with the game focused, the Unity receiver’s actual room-entrance reload,
  and visual tracking, room alignment, and enemy and Geo observations while
  playing. The hosted tests use archived frames and a simulated receiver.
- PPO checkpoint persistence is verified on Windows. Measuring improvement at
  pit recovery, damage, combat, and Atlas exploration requires repeatable live
  episodes against the existing policy.
- CUDA selection and fallback have tests. Running the CUDA kernel and measuring
  speed and agreement with the CPU path requires a compatible Windows GPU.
  [Windows CUDA port](https://github.com/tabutyn/hollow-knight-vision/pull/201)

## Reproduce the changed-line report

From the repository root, extract artifact `15115004222` and run the report
with the paths to the extracted files.

```sh
python3 windows/audits/2026-11-22-deployment-parity/summarize-coverage.py \
  b6f647c 44989f7 --python-json trainer-python.json \
  --lcov desktop-core.info --lcov desktop-ui.info \
  --lcov receiver.info --lcov trainer-interop.info
```

The local Mac object-trainer result can be included with a second
`--python-json` pointing to its coverage.py report.

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
scripts fall outside the C# line coverage report.

The [walked-ground native test run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45089001817)
passed and reached 291 of 308 changed executable lines in the instrumented
files. The focused platform-patch tests reached 71 of 71 changed Core lines.

The [WPF instrumentation run](https://github.com/tabutyn/hollow-knight-vision/actions/runs/45089975353)
passed. At `19bd296`, native suites reached 949 of 1,381
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
was 1,044 of 1,517 changed C# executable lines across that port revision, including WPF.


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
