# November deployment coverage and Windows parity

The deployed source range is `b6f647c..8d67fa3`. The Mac coverage run used
`6a15e70`, which has identical Mac sources, because the last two commits in
the deployed range touched only Windows tests and the parity audit.

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

| Deployed behavior | Windows evidence |
| --- | --- |
| Reset Room returns to the room entrance and clears both atlases | The draft uses the same receiver operation, requires a matching session and confirmed scene, and then clears Gameplay and Hacker atlas state. Loopback tests passed, with 26 of 27 changed executable lines reached in the Windows receiver/forwarder slice. The native Windows build, offscreen WPF suite, and installed-package verification passed. The actual game reload and the R shortcut with the game focused still need live verification. |
| Invincibility defaults off, object training uses available class evidence | Windows already uses the deployed receiver defaults and the shared incremental Python trainer. The default is covered. The current Windows detector's class filtering and native incremental training still need behavior-level comparison. |
| Settings, Clear Save capture, local stencil search, and object detector controls | The draft requires Clear Save during Select Profile setup, offers it in Add until a positive box is present, and restores guidance when a reference is deleted. Eight focused Windows setup and selection tests passed. Settings has a left Atlas, Debug View, and System column. Estimated room connections are hidden by default and can be enabled from Debug View. Hacker save-state management, menu-locality, and detector parity remain open. |
| Hacker terrain editing | The draft provides Ground, Wall, Spike, Door, and Rooms controls independently of the camera controls. Walls and floors share editable corners. Doors can be drawn along either axis and follow an attached floor. Terrain uses the room coordinate system, and the editor preserves the floor-only document format. Older detector records stay stored and excluded from the editor and overlay. The core tests cover corner movement, room isolation, reload, undo, both door axes, and save failures. Live game rendering and behavior after leaving and returning to a room still need verification. |
| Atlas camera shortcuts | The Windows draft cycles both workspaces through Current at 1.1× zoomed out, Current at 2× zoomed out, and Fit. The offscreen WPF viewport regression checks the requested scale and keeps the follow point centered in both Current presets. |
| Game Play visual tracking | Windows rejects fully enclosed shorter floors within the Knight’s height and trims a slight overlap at supported platform tips. Game Play records level walks when frame-matched arrows, visible foot samples, and a verified camera agree. The draft keeps walked spans by room for the current capture and uses one green line over walked columns while keeping tracking boxes visible. Synthetic frames verify dark intervals, reversing, room isolation, and jump rejection. Camera references now need at least one patch width of floor support. Short platforms can show their own box after two adjacent, unmasked, registered observations. Existing live boxes align with the current floor and saved boxes show outside the live image. With the modded game, admitted walks now travel with their saved room observations and are reprojected when recovering a room or correcting the atlas. The store and pipeline regressions exercise reopen, new-column admission, stale pose correction, and room isolation. Game Play detects walls from visual edges and textured platform faces. It joins nearby visible floor endpoints and uses sustained, corroborated movement contact to extend wall evidence. The overlay draws corner markers and horizontal joins. Walked-ground reopen in normal game mode, wall evidence in bot observations, and live Windows recordings remain open. |
| Atlas objectives, jumps, one-way drops, and path display | The draft builds a directed graph from authored Hacker terrain. Verified fixtures cover a continuous walked span, the measured jump apex, authored obstacles, one-way falls, and steered branches. Objective is the default Hacker tool. The atlas shows paths beyond the visited tiles, draws the selected route in white, and gates the path display on active gameplay and the Debug View setting. Path editing and saved connections, Game Play graph presentation and automatic frontier navigation, and live Windows interaction need verification or implementation. |
| Paired bot capture and runtime, human evidence, policy selection, and Gym PPO | Windows has no bot playback or Gym observation encoder in the inspected application. The bot Python package is not connected to the Windows application or trainer environment. |

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
