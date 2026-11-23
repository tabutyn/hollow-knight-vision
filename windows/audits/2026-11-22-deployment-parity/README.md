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
| Settings, Clear Save capture, local stencil search, and object detector controls | The draft requires Clear Save during Select Profile setup, offers it in Add until a positive box is present, and restores guidance when a reference is deleted. Eight focused Windows setup and selection tests passed. The rest of Windows settings, menu-locality, and detector parity remains open. |
| Hacker terrain editing | The draft provides Ground, Wall, Spike, Door, and Rooms controls independently of the camera controls. Walls and floors share editable corners. Doors can be drawn along either axis and follow an attached floor. Terrain uses the room coordinate system, and the editor preserves the floor-only document format. Older detector records stay stored and excluded from the editor and overlay. The core tests cover corner movement, room isolation, reload, undo, both door axes, and save failures. Live game rendering and behavior after leaving and returning to a room still need verification. |
| Game Play visual tracking | Walked-floor continuity, camera and patch tracking, corner detection, and the Game Play Atlas placement still need individual port and replay verification. |
| Atlas objectives, jumps, one-way drops, and path display | The deployed Mac path editor and graph have no Windows equivalent in the inspected Windows source. |
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
