# Hollow Knight live camera vision

Native macOS prototype with a deliberately small live baseline. Live capture
normalizes the gameplay surface, estimates camera translation, masks the HUD and
Knight, and stores source-backed 256 px atlas tiles. Sparse keyframes and global
feature descriptors persist across launches. A pose-independent search revisits
saved features at up to 2 Hz; two consistent observations add a loop edge and
rebuild affected tiles at optimized camera poses.

Windows preparation now lives in the sibling
[`windows`](../windows) project. It provides a buildable
Win32 capture/normalization bootstrap and Windows CI; the feature-complete app is
still macOS-only. See [WINDOWS_PORT.md](WINDOWS_PORT.md) for the dependency audit
and ordered port gates.

## Thinking

Select **Enable Thinking (load local model)** before choosing Normal or Modded.
The checkbox starts unchecked on each ordinary launch. For a command-line
dashboard, add `--enable-thinking` to opt in. Gymnasium observation workers
run independently of Thinking.

Click the orange log at the bottom right of Gameplay or Hacker, or press **T**, to expand the Thinking context. Drag across readings in the Text log and press **⌘C**, or choose **Copy log** for the whole transcript. **Copy schema explorer** copies the selected request’s system message, user message and expected reply schema. Context also shows game evidence and the five-second decision brief. Screenshot and JSON show the event frame and actual model reply. Settings → Debug View → Thinking context hides the box. The notebook is available with Thinking off. The inspector shows the exact image attached to an earlier reply when a newer capture has arrived. **Show decision JSON in Finder** reveals the validated selection.

Starting the dashboard with Thinking enabled loads Gemma in the background. Choosing another model
immediately shows its load status and elapsed time. When the local Ollama log
reports weight mapping, GPU placement or warm-up, the status shows that stage.
It keeps the reported GPU layer count and CPU/Metal mapped buffer sizes visible
once available. Ollama confirms when the model can process a request. Its load
API does not supply a running count of bytes processed.

**Ollama** uses the installed Gemma 3 4B, Qwen 2.5 VL 7B and Llama 3.2 Vision
11B weights. Gemma is the default. The primary local service uses port 11434.
When configured, a second local Ollama version serves Llama on port 11435. The app asks
Ollama to keep the chosen model warm for thirty minutes. Llama 11B can exceed 8 GB.
On another Mac, set the server’s `OLLAMA_MODELS` directory to the desired drive
and pull the model tags `gemma3:4b`, `qwen2.5vl:7b` and `llama3.2-vision:11b`.

### Reading signs and dialogue

**Settings → Debug View → Text Reader** works with Thinking off. **Read game text** enables Apple Vision OCR. **On screen** shows the captured lines, positions and confidence. **Log** stores unique observed readings for this playthrough. Case, punctuation, spacing and small recognition errors can match. Different numbers remain distinct. Fading fragments consolidate into a full line. A bundled English and Hallownest dictionary prefers correctly spelled readings and can reconcile two different misspellings. Resuming a save restores its notebook. **Thinking → Log** opens the same view.

### Policies for a first ending

Vision uses the Atlas objective, the event screenshot, notebook excerpts and current observations. Gemma returns an offered Explore, Traverse or Interact selection in gameplay. Each newly recognized menu or confirmation screen sends its own screenshot and visible elements to the local model. Gemma can select an element, including Quit, or request one navigation step with `more`. A screen with no mapped controls offers `wait` until new evidence arrives. **T** shows the screenshot, offer and JSON reply. **Automatic policies** controls dispatch in Modded Gameplay and Hacker. **B** can stop active playback. [Event triggers, exact JSON and replay tests](../docs/playing-policies/README.md) · [Motor training guide](BOT_SKILLS.md)

## Build, capture, and dashboard

After changing code, use the canonical build command:

```bash
macos/build-app.sh
```

It builds and signs the application, atomically replaces
`/Applications/Hollow Knight Vision.app`, unregisters the disposable package
copy, and rebuilds one canonical Dock pin in its original position. Duplicate
Vision pins and stale recent tiles are removed without changing other Dock
items or settings. The package copy is deleted after a successful install.
`macos/build-app.sh --package-only` is reserved for CI and artifact packaging.
Canonical builds use the BallRoller Apple signing certificate, preserving the
same macOS Accessibility identity across source changes. The installer rejects
ad-hoc signatures at `/Applications/Hollow Knight Vision.app` so a rebuild
cannot silently invalidate keyboard and pointer permission again. Set
`HKV_CODE_SIGN_IDENTITY=-` only with `--package-only` for a disposable bundle.

To build, install, and immediately launch the installed copy:

```bash
macos/run-live.sh
```

`run-live.sh` builds the current source, installs it at the stable
`/Applications/Hollow Knight Vision.app` path, and launches that installed
copy. Pin only that application to the Dock. Do not pin the disposable
`build/Hollow Knight Vision.app` packaging output; doing so gives macOS two
paths for the same bundle identifier and can produce two Dock icons.

To add the app to the Dock when it is not already pinned, run once:

```bash
macos/install-app.sh --repair-dock
```

The installer stages and verifies the new bundle before replacing the installed
copy, registers only the canonical path with Launch Services, and terminates an
older running Vision process before the next launch. If that process does not
exit, installation stops before replacing the app. Dock refresh is mandatory
and uses built-in macOS preferences, without requiring `dockutil`. For an isolated
installer test, set `HKV_INSTALL_APP_PATH` to an absolute temporary `.app` path.
Run `python3 macos/test-refresh-dock.py` for Dock regression tests; preference
writes in these tests target temporary plist files, never the live Dock.

On first launch, grant **Privacy & Security → Screen & System Audio Recording**.
The app asks on every ordinary launch whether to use Normal or Modded Hollow Knight. Normal mode
uses capture, visual tracking, and native keyboard and pointer forwarding; Modded mode adds receiver input,
Hacker, and Ops after the required capabilities connect. Agents can bypass the chooser:

```bash
macos/run-live.sh --game-mode normal
macos/run-live.sh --game-mode modded --game-path '/path/to/hollow_knight.app'
macos/run-live.sh --game-mode modded --enable-thinking
```

The chooser disables a mode when Vision cannot find a complete compatible app.
Selecting an available mode gracefully closes a different running copy before
launching the matching app. A Steam shortcut is not treated as a stock install.
For a bundle belonging to Steam's Hollow Knight manifest, Vision requests one
local Steam launch (`-applaunch 367520`) instead of opening the game executable
first or a URL that can select Remote Play on another computer. Steam launches
through macOS Launch Services so its local-network permissions belong to Steam,
not Vision. An already-running Steam client receives the command through its
command-line forwarding process. Separate copies still launch directly. For a
separate Steam build, Vision starts the Steam client without requesting a library
game, waits for the client to finish launching, then passes `SteamAppId=367520`
and `SteamGameId=367520` only to the selected game. This preserves the copy's
Steam identity instead of allowing its [Steam restart check](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_RestartAppIfNecessary)
to close Modded and open the registered Normal installation. Non-Steam builds
remain independent of Steam. Launch Services substitution is disabled and the
returned application path and mode are verified before attaching controls.
During startup, Vision follows replacement processes from the selected installation
and reconnects capture for up to 60 seconds
after an early process exit. After 15 seconds of stable capture, or a confirmed
Quit, game termination retains the ordinary five-second shutdown countdown.
Use `--game-path` when the compatible app is outside the scanned locations. The
bottom-bar gear opens settings; blue guidance directs menu labeling;
Reset Stencils and Factory Reset create recoverable backups under
`~/Library/Application Support/HollowKnightVision Backups`.

Automatic title/profile navigation is off by default. Enable it only for an
automated run:

```bash
macos/run-live.sh --auto-navigate-gameplay
```

Normal mode forwards arrows and common keyboard controls (A/D/Z/X/C, I,
Return, Tab, Space, Escape, and P for Pause) from the Vision window to the selected unmodified
Hollow Knight process. Pointer events over the displayed live frame are mapped
through the captured content rectangle, including title-bar cropping, and sent
to the same validated process. macOS Accessibility permission is checked when
normal input starts, before a key or click can fail silently. Migrating from an
older ad-hoc build requires removing its stale Accessibility row and approving
the stable installed app once. Later canonical rebuilds retain that approval.
The prompt links to the correct settings pane. Unity ignores background keyboard events, so Normal
uses a nonactivating key panel: Vision receives the keys while the game remains
the active application underneath. The panel stays above the game only while
in use and returns to normal window level when you switch away. Held keys are
released on focus loss, workspace changes, and exit. Labeling, stencil review,
model review, and text fields keep their own keys; Settings retains arrow
forwarding. L and S remain Vision’s labeling and stencil shortcuts. The dashboard
opens at the full usable display size by default. Modded mode keeps its receiver
and ordinary window behavior.

For local development runs that need repeatable game input without a human
holding keys, enable the loopback automation channel explicitly and pulse a
button through the same in-game receiver used by normal input:

```bash
macos/run-live.sh --enable-automation-control
macos/control-game.sh up 2
```

The control channel is disabled on ordinary launches.

Gameplay HUD masking uses bundled icon templates independently of the object
detector's Gameplay/menu classification. Open **Debug View → HUD Stencil** to
review per-frame health counts and the exact health, mana, and fixed Geo cutouts.
See [HUD_STENCIL.md](HUD_STENCIL.md) for reference provenance and matching limits.

Use **Capture** during gameplay, **Fit** to frame the growing atlas, and
**Reset atlas** before a bounded test. **World feature overlay** is the single
review checkbox on the bottom bar; it overlays saved landmarks, keyframes, attempted and
accepted matches, loop edges, and raw-to-optimized corrections directly on the
map. Health has a red box, Geo yellow, Soul blue, and the Knight green. All four
regions are omitted from atlas writes. The status row reports camera coordinates,
display rate, and atlas size. Capture requests native display cadence; Metal presentation targets 60 fps with two drawable slots.
See [GROUND_TRACKING.md](GROUND_TRACKING.md) for the measured limits and replay evidence.
Use [Camera diagnosis](CAMERA_DIAGNOSIS.md) to compare solved poses with Hacker's
exact rendered-frame camera transforms and score identical floor observations
under both placements. These tools run offline and never feed truth into tracking.
Ground texture estimates camera motion on a bounded latest-frame worker;
expensive global recovery runs on a separate bounded snapshot worker. Object
inference runs every fourth capture. See [GROUND_TRACKING.md](GROUND_TRACKING.md)
for the current tracker and [TRACKING_STABILITY.md](TRACKING_STABILITY.md) for the
September 26 implementation and live validation.
Apple-silicon patch search uses Metal with CPU fallback. Ground preprocessing
also has a pixel-exact Metal implementation, but current measurements keep its
automatic path on CPU; [GPU_TRACKING.md](GPU_TRACKING.md) records that decision,
the retained benchmarks, and the expanded recovery-search capacity.
The sparse atlas has no fixed 4096 × 1440 reset boundary. The live world is saved
under `~/Library/Application Support/HollowKnightVision/live-world-v3`; Reset atlas
archives it and starts a fresh world. Set `HKV_LIVE_WORLD_ROOT` to an absolute
scratch directory when running destructive atlas-reset tests.

See [LIVE_BASELINE.md](LIVE_BASELINE.md) for the acceptance tests that must pass
before another reconstruction component returns to the live path.
See [CAPTURE_PIXELS.md](CAPTURE_PIXELS.md) for the retained capture path.
[RETIRED_FEATURES.md](RETIRED_FEATURES.md) records the cleanup and supported commands.

### Saved-route replay gate

The live-world evaluator replays a saved source session as independent visits.
With the reopen flag it commits each observation and graph revision to an
isolated world, then reconstructs that store from disk before each visit:

```sh
HollowKnightVision \
  --world-replay-session /path/to/session \
  --world-replay-report /tmp/live-world-report.json \
  --world-replay-repeats 3 --world-replay-reopen
```

Routes are one-way by default, so one pass ending in Town never creates an
implied connection back to Tutorial_01. Use `--world-replay-returning` only for
a route known to end where it started. Returning-route evaluation requires a
confirmed closure and an optimized endpoint residual of at most 12 px.

## Manual ground-edge labels

Open **Hacker** or press `H` to build a second atlas that is independent from Gameplay. Hacker joins each capture to the mod camera transform from the exact Unity frame and places it without the visual pose solver. Green ground lines are authored only by the user; detector output never creates saved labels. The top bar switches between **Ground** (Add/Modify/Delete, endpoint handles, undo, automatic saves) and **Rooms** (drag rooms into alignment), independently of the camera view. Labels remain attached to their owning room. The counter distinguishes the current room from all saved rooms. **Debug View → Mark Ground…** opens the same workspace. See [Ground labeling](GROUND_LABELING.md).

## Saved-input replay and checks

Gameplay, Label, Stencil, Model, and Hacker are the workspaces; Ops is part of Settings. The
old video reconstruction command is retired; recorded game input still replays
through the actual game and receiver:

```sh
./run-live.sh --enable-automation-control --allow-background-path-playback --render-frame-marker
./control-game.sh replay path-RECORDING.json 1
swift test -c release
python3 test-camera-diagnostics.py
python3 test-analyze-input-paths.py
./build/Hollow\ Knight\ Vision.app/Contents/MacOS/HollowKnightVision \
  --evaluate-low-resolution-trace /path/to/path-REPLAY.json \
  --low-resolution-report /tmp/low-resolution-report.json
```

Use `HKV_LIVE_WORLD_ROOT` with an absolute scratch directory for replay tests.
Frame truth is diagnostic only. The independent Hacker atlas and manual ground
labels never supply poses or edges to Gameplay tracking.

New recordings retain the matcher input at about 20 Hz. The low-resolution
trace evaluator reuses the production matcher, joins Hacker truth only by exact
rendered Unity frame, measures per-step and integrated ground-loss drift, and
audits room-local place recognition. Recordings made before this trace was
added report `evaluable: false` rather than an apparent zero-error result.

### Stencil walkthrough and model workspace

The gear opens a Settings dialog with an Atlas, Debug View, and System sidebar. Atlas holds save/load actions; System holds Game language, Ops, and Factory Reset. Language names use their native spelling. Navigate to the screen named by the blue guidance in the single bottom row, then press **L** or **Label**. Game State is an optional overlay under Debug View. Free Fly, Fit, and Current appear only after Phase 1 while Gameplay is recognized in the Gameplay workspace; Ops also requires Modded mode and a recognized gameplay state. Required menu labels follow menu order and disappear from Add after their required boxes are present (two for selection decorations); Modify/Delete keep them editable. Gameplay objects remain repeatable and alone expose **+ Object**.

**Stencil** derives its checklist from the contracts: 26 required screens in Modded, 25 in Normal (excluding Mods). Phase 1 is Main Title, Select Profile including Clear Save, and a separately captured Gameplay HUD reference (two adjacent full health icons). Before capturing Select Profile, create a save if necessary, return to that screen and highlight the occupied slot so Clear Save is visible. Add offers Clear Save immediately and keeps it available until its positive box is drawn. It unlocks Model and Stencil Detection. Phase 2 contains the other menu/game screens; Phase 3 captures the Clear Save confirmation dialog and is dismissed with No. Credits has no required boxes. Each stencil category keeps one reference; gameplay model objects remain repeatable. Reset Stencils below the sidebar archives stencil references and calibration while preserving gameplay training examples. Factory Reset also removes stencils. The gear opens a sidebar Settings dialog: Atlas contains Save State, New, Delete, and Load; Debug View contains visual diagnostics; System contains language, available Ops, and Factory Reset. Estimated Room Outlines controls the orange dashed provisional-room borders and their legend, and defaults off. Its setting is retained across launches. Object Detection stays disabled until an active model has completed training and evaluation on current gameplay labels; bundled models and models from a cleared dataset do not unlock it. Framing controls require both Phase 1 readiness and recognized Gameplay in the Gameplay workspace.

Localization coverage and remaining native-language/Windows UI checks are tracked in [TODO](../TODO.md).

Existing partial references show **Click Stencil and Edit Labels** guidance. Each reference has **Edit Labels** and **Delete Screen Shot** actions, and label boxes show their localized class names without requiring captured icons. **L** selects Gameplay when gameplay is recognized; explicit Edit Labels still opens the selected stencil. The wider object selector sits above the editing controls to avoid overlap at small window sizes.

Hacker is offered only for recognized gameplay with mod telemetry. Its Atlas settings use the same Save State, New, Delete, and Load controls as Gameplay, backed by a separate Hacker state library. Hacker automatically saves room pixels, camera anchors and connections under `hacker-atlas-v1/`, and restores them on reopening. New and Load retain the previous Hacker atlas as a named restore point. Its separate settings view selector is removed; Free Fly, Fit, and Current remain in the bottom bar. The view starts with automatic framing and supports drag/zoom navigation. Ops controls live in System and require recognized gameplay plus the player-ops capability. Clicking outside Settings closes it; arrow keys continue forwarding to the modded game.

**🤖 B** is the single bot control. During Gameplay it starts Hacker bot play in the current game; from menus it archives/resets slot 4, starts a fresh game using stencils, and hands off after Gameplay is detected. B stops either phase. Menu navigation works before bot training; when Gameplay is reached without a model, control returns to you with a training prompt. Hacker always uses frame-matched mod Knight/enemy positions and your green floors. **K** records manual input, replay checkpoints, and learning observations together under one recording ID. Press K again to save both. Right-click K to reveal the recordings. The separate Record and Demo buttons have been removed. Blue instructions disappear once required stencil setup is complete. See [Recording and bot controls](BOT_DEMONSTRATIONS.md).

For left/right Hacker passages, three stable exact-frame grounded Knight samples near each doorway refine the room-height constraint. The fixed Knight-center-to-floor offset cancels across the passage, avoiding jump and entry-animation heights. Samples must be recent and near the corresponding exit/entry edge; an unsupported passage retains its initial placement. Once supported, airborne revisits preserve the floor alignment. Overlapping horizontal branches move farther sideways instead of being shifted vertically. Horizontal spacing remains a presentation layout; verify convincing joins with a recorded out-and-back run.

Model → Screenshots reviews gameplay captures and their label boxes. Gameplay
objects are all optional, including Hollow Knight; no completeness, occurrence,
or exemption contracts block training. Model training exports only the Gameplay
context and trainable gameplay classes. Menu and HUD stencil validation stays in
the separate Stencil workflow. A dataset still needs at least one positive
trainable object to start a run.

### Installed training runtime

The installed app includes its trainer, requirements, and setup script. Run:

```sh
"/Applications/Hollow Knight Vision.app/Contents/Resources/setup-incremental-trainer.sh"
```

Setup requires Python 3.12 at `/opt/homebrew/bin/python3.12` and installs the
training environment under `~/Library/Application Support/HollowKnightVision/
training-v1/runtime/`. Its interpreter is registered in `training-v1/runtime-settings.json`,
so Finder/Dock launches work without Git, the source checkout, or terminal variables.
App updates preserve that registration. An existing source `.training-venv` is
registered during installation if no registration exists. `HKV_TRAINER_PYTHON`
overrides the registered interpreter; passing it to the installer updates the
registration. Workstation paths are never embedded in the app package.

Training review also accepts older compact Python/Windows run UUIDs and checkpoint
lineage. New Mac runs and restore-point identifiers use canonical UUIDs. This
metadata compatibility does not convert Windows ONNX models to Core ML.


### Complete visual evidence for tracking development

For the recording → replay → diagnosis cycle, enter recognized Gameplay in
Modded mode, press **K** to start a run, play through the problem, then press
**K** again to stop and save. A 30–60 second run with a brief stationary start,
the tracking failure, and a brief stationary finish gives useful comparisons.
Mention the action or approximate moment where tracking looked wrong. Record
before the failure so its preceding frames are available.

Runs save under `~/Library/Application Support/HollowKnightVision/input-paths-v1/`.
Keep the `path-*.json` and matching `frames-<path-id>/` directory together. After a
run, inspect archive completeness, replay those images through the installed
production tracker, and score the replay with the commands below. Report the
first divergence, held/rejected camera estimates, recovery, room transitions,
and reference coverage. Compare the same run before and after a tracking change;
only diagnose measured errors where matching reference telemetry exists.

Red Ground Features boxes mean a patch disagrees with the current camera-motion
estimate. They can accompany bad matches, parallax, or an incorrect camera
estimate; the color alone does not identify the cause. Keep them as diagnostic
evidence and use the replay to correlate them with tracking loss.

New path recordings also save a sibling `frames-<path-id>/` directory. It contains
lossless PNG inputs for every staged capture (including dark/floorless frames),
original timestamps and solve dimensions, exact frame markers when available,
vision-stage ordering, and the masks/tuning consumed by ground extraction.
Input events, checkpoint and reference telemetry remain in the path JSON.
Copy both the JSON and its frame directory when sharing a recording.

The writer retains at most four pending images. Disk overload or failure creates
explicit missing-frame entries; capture never waits for PNG compression. The
path's `visualFrameArchive` summary reports completeness and queue pressure.
Older input-only paths remain readable and playable, but cannot supply a complete
image replay. The production image-replay driver is available below. Tracking
islands and measured doorway-floor correction are implemented; disconnected
components remain explicitly provisional. See [FIRST_BENCH_TRACKING.md](FIRST_BENCH_TRACKING.md)
for accuracy, coverage limits, reproducibility and Windows parity requirements.
Nonfinite diagnostic numbers use explicit `Infinity`, `-Infinity` and `NaN` JSON
strings, identified by `ieee754-nonfinite-strings-v1` metadata. They do not become
valid camera poses or silently discard the recording.

Validate the saved images, hashes, and ground-input associations without launching
the game:

```sh
'/Applications/Hollow Knight Vision.app/Contents/MacOS/HollowKnightVision' \
  --inspect-visual-replay '/absolute/path/to/path-recording.json'
```

Exit 0 means the image archive is complete. Exit 2 means missing/incomplete
archive; exit 1 indicates corrupt evidence or an inspection error. Marker coverage
is reported separately: missing truth cannot count as zero tracking error.


Replay the exact captured images through the production capture, vision, ground,
fallback and atlas stages, with a new isolated output directory:

```sh
'/Applications/Hollow Knight Vision.app/Contents/MacOS/HollowKnightVision' \
  --replay-visual-archive '/absolute/path/to/path-recording.json' \
  --visual-replay-output '/absolute/path/to/new-replay-directory'

python3 macos/score-visual-replay.py \
  --reference '/absolute/path/to/path-recording.json' \
  --replay '/absolute/path/to/new-replay-directory'
```

Add `--visual-replay-frame-limit 600` for a short smoke check. The driver validates
image hashes, starts a fresh tracker/atlas, freezes archived visual masks/tuning,
and consumes recorded input directions. It never supplies recorded camera poses
or mod telemetry to tracking. Reference-only scoring happens in the separate
Python command. Add `--plot '/absolute/path/to/errors.png'` to export a plot
(requires Matplotlib). Camera and saved-screenshot errors use fixed reference
origins; missing exact matches are excluded explicitly.

This adapter replays a gameplay recording from a cold tracker. It does not recreate
pre-recording tracker history, rerun external stencil/model inference, or measure
UI rendering performance. Global ground searches are synchronous during replay so
worker scheduling cannot select different images. The live app keeps its bounded
asynchronous searches. All output goes into the specified directory; the normal
saved atlas and installed game are untouched.
