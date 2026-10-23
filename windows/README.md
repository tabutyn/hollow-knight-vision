# Hollow Knight Vision Windows lab

This is the native Windows host and desktop lab for Hollow Knight Vision. It
builds without Apple frameworks and keeps capture, atlas, inference, dataset
export, and training on CPU.

Implemented now:

- Win32 Hollow Knight process/window discovery.
- CPU target-window capture via `PrintWindow` into owned BGRA pixels. Failed
  captures never fall back to desktop pixels that could contain Vision itself.
- Per-monitor DPI awareness so capture geometry remains physical pixels when
  Windows display scaling is enabled.
- Deterministic center crop and bilinear normalization to the existing 640×360
  vision contract.
- A one-frame latest-only slot so capture cannot create an unbounded queue.
- A portable protocol v1/v2 TCP client for complete input state, capability
  negotiation, acknowledgements, and Hacker camera ground truth.
- Persistent semantic-ground approval, including stable-line evidence and
  segment reassociation parity with the macOS atlas.
- Named atlas save states, immutable restore paths, archive import, disk usage,
  and permanent state cleanup with macOS-compatible metadata.
- Atomic schema-v5 labeling examples with language-specific positive boxes, hard negatives,
  known-empty frames, normalized top-origin coordinates, and PNG capture.
- Leak-safe dataset export: capture groups and duplicate images remain in one
  split, hard-negative groups stay in training, and every class retains
  positive training data.
- A WPF Gameplay/Label/Stencil/Model/Hacker lab for drawing and editing boxes,
  exporting datasets, CPU training, live ONNX review, gameplay startup, and
  atlas recording.
- The mac-calibrated camera accumulator, fresh-atlas admission gate, and
  fade/dark-mask-resistant 64×36 visual translation solver.
- The macOS 32×8 CPU ground kernel, occlusion-aware line theory, semantic
  surface validation, and foreground rejection.
- Persisted visual-route evidence with receiver camera truth and repeatable
  offline error gates.
- Offline route-to-room-graph construction using visual motion only; receiver
  camera truth remains evaluation evidence and never supplies world poses.
- Versioned live-world observations, keyframes, landmarks, motion/loop edges,
  atomic compare-and-swap persistence, and optimized-pose replacement.
- Deterministic mac-parity translation pose-graph solving with exact anchors,
  weighted loop closures, per-room connected components, and atomic revisions.
- Mac-compatible recorded input-path loading and replay with optional receiver
  checkpoint restoration, repeated runs, and unconditional neutral release.
- BMP/PNG export, SHA-256 diagnostics, CPU tests, and self-contained artifacts.

Build on Windows with .NET 8 or newer:

```powershell
./build-windows.ps1
```

The resulting CLI host is
`artifacts/win-x64/HollowKnightVision.Windows.Host.exe`. The desktop lab is
`artifacts/app-win-x64/HollowKnightVision.Windows.exe`. Both are self-contained;
the published executables do not require a separate .NET installation. A
successful `build-windows.ps1` also installs and hash-verifies the desktop app
at `%LOCALAPPDATA%\Programs\Hollow Knight Vision` and refreshes its Start Menu
shortcut. The build fails instead of leaving that installed copy stale; close
the installed app before building.

Parity verification is tracked in `mac-parity-audit.json`. Run
`./Test-MacParity.ps1` for the complete Mac source/test inventory and remaining
verification counts (`-Json` gives individual obligations; `-RequireComplete`
fails until all are verified). This is a functional evidence map, not measured
line coverage. The macOS CI workflow separately collects Swift code coverage;
its existing corpus-dependent `MenuStencilTests` exclusion remains an explicit
verification gap. Full Mac/Windows parity has **not** yet been established.

Windows runs the same ten photographic HUD fixtures as Mac. Two labeled health
icons learn row spacing; every additional full, empty, or lifeblood icon must
match independently. A half-second hold preserves exclusion masks during
animation without inventing health counts. Stencil Detection renders all matched
icons, and atlas/motion processing excludes the HUD shield and exact masks.
Camera and label-edit controls share a persistent blue selected highlight.

Label context and object selection persist separately from the Normal/Modded
launch chooser. Switching context restores that screen's saved stencil, or starts
an empty draft from the paused capture; Gameplay starts a fresh training draft.
Programmatic selector changes cannot reclassify the selected box or recursively
open another screenshot. Autosave refreshes the example list without reopening
the image, preserving undo history and zoom. Failed reference reads retain the
previous context and draft. Offscreen WPF tests exercise selector-event guarding,
not an end-to-end game-driven MainWindow session.

The editor uses Mac's contract-based remaining-label rules: completed menu labels
disappear in Add and remain available in Modify/Delete; selector pairs and the
HUD reference need two positive boxes; gameplay labels remain repeatable. Normal
mode omits Mods and fresh profiles defer Clear Save. Saved screenshots with a
clearly wrong context are repaired only when their positive boxes match exactly
one complete contract. Partial, ambiguous, excess, or unexpected labels never
trigger a guess. Repairs preserve PNG pixels, annotation and capture identities,
language, and creation date, and do not repeatedly rewrite unchanged manifests.

The Model toolbar counts only eligible gameplay screenshots whose `example.json`
was modified after the latest candidate's dataset snapshot; with no readable
snapshot, all eligible screenshots count as untrained. A blue count/dot signals
changes. Train becomes Stop Training while running, with stage/epoch/ETA text.
Shared-model toolbar training uses 100 iterations, matching Mac. The native
Windows progress row stays clear of the centered mode tabs at minimum width.

Training runs and child processes belong to a kill-on-close Windows job. A start
gate contains the PowerShell launcher before Git/Python can spawn. Stop and app
shutdown terminate only that run's descendants and drain redirected output;
UI updates are coalesced, generation-checked, and never synchronously block on
the dispatcher. Logs retain at most 128 KiB with incremental UTF-8 decoding.
Cancelled/failed run output and staging are preserved as hidden `.interrupted-*`
siblings, excluded from model discovery; pre-existing output is refused.
Factory reset will not move support data unless training shutdown completes.
Isolated process-tree tests and offscreen toolbar/shutdown tests exercise these
paths without real training or user data. Full live MainWindow, actual Python
training, and native Mac differential execution are still unverified.

Dataset export now applies Mac's context guard to known-empty screenshots:
a menu remembering gameplay classes does not become a gameplay negative.
Explicit positive/negative boxes still export across contexts when requested.
Repeated instances and canonical aliases are retained; reversed rectangles
produce positive pixel dimensions. Snapshot JSON uses lowercase split names,
whole-second UTC dates, and omitted nil annotation flags for Swift/Python
interchange. Invalid split histories fail visibly instead of silently
reassigning examples. Capture/duplicate groups, hard-negative overrides,
per-class training coverage, stable splits, preliminary thresholds, and failed
snapshot cleanup have direct regressions, including all nine Mac exporter tests.

Saved-label updates now refresh introduced classes from actual saved annotations,
matching Mac; the editor no longer feeds its previous remembered snapshot back
into each save. Explicit CLI known-class arguments remain supported. Class and
context training queries retain known-empty examples and do not require Ready;
the separate exporter still excludes menu-only negatives. Drafts standardize
reverse-drag rectangles, canonical known-class migrations persist sorted unique
values, and example JSON uses whole-second UTC dates and omitted nil fields.
All 11 Mac example-store tests have Windows counterparts, including actual native
PNG decoding with clean pixel/identity checks. Extra regressions cover stale
history, old Windows fractional timestamps, collisions and failed atomic writes.
Malformed-directory/error reporting, legacy schema-1 migration semantics and
native Mac fixture interchange remain open source-review obligations.

With training dependencies installed, run `./Test-TrainingInterop.ps1` after the
Windows build (or pass `-Python` and `-Dotnet` explicitly). It runs the 13 shared
trainer unit tests and creates a temporary Windows PNG/JSON export, validates it
through the real launcher and Python reader, and checks CPU tensors and target
semantics. It does not train, download weights, or use existing labels; it removes
only its path-checked disposable fixture. Windows CI runs this after dependency
setup. Like Mac's launcher, Windows honors `HKV_TRAINER_PYTHON`; otherwise it uses
the repository's `.tools/hkv-training-windows/Scripts/python.exe`.

Model → Summary now mirrors Mac's Least Registrations / Most Errors lists and
failed-screenshot review. Rankings include unlabeled model/custom classes and
exclude hard negatives from registration counts. Review matching canonicalizes
aliases and uses confidence-ordered, one-to-one IoU matches; low-confidence
predictions and baseline runs do not inflate current errors. Selecting a class
shows its existing failed screenshots in review order, with wraparound Previous
and Next controls. Human boxes retain class/negative colors; only that class's
above-threshold predictions get yellow dashed outlines. Clicking the screenshot
opens the paused Add editor for repair. Selection survives refreshes; deleted
screenshots are skipped, missing images remain navigable, and analysis failures
hide stale content. Summary and Objects share the selected object and atlas
legend. The three Mac review-analysis regressions plus edge cases and themed
offscreen WPF controls/rendering pass. Confidence/overlap ties use deterministic
input ordering on Windows; Swift's unordered matching ties are not promised
bit-identical. Native Mac differential/runtime verification remains outstanding.

Model → Objects now shows the complete learned-object/custom catalog, individual
positive reference crops in two columns, and the selected full screenshot with
all positive/negative boxes. Choose a crop to inspect its source; click its star
or drag the same object's star onto another crop to change the preferred
reference. Clicking the full screenshot opens the paused label editor in Delete
mode with that object selected. Missing/deleted preferred examples fall back to
the oldest available positive reference, while unlabeled classes remain visible.

Model → Screenshots now has an oldest-first, virtualized thumbnail list and a
full screenshot preview with canonical class colors, selected-class emphasis,
negative boxes, and an icon legend. The same preview is used by Objects. Keys
1/2/3 select Summary/Objects/Screenshots; Up/Down move through screenshots without
wrapping. Clicking the preview opens the paused label editor in Add mode.
Contract failures are red; exemptions are blue and persist per screenshot.
Failed writes leave admission state unchanged. All unexempted gameplay-screen
failures block training, including via the legacy trainer button; selected-class
eligibility and contracts are reloaded before export/launch. Toolbar training
uses the full shared gameplay/custom vocabulary, excluding fixed HUD classes.
Deletion requires a native Windows confirmation and removes only selected,
owned, identity-checked screenshots. Partial failures retain surviving rows and
show errors; missing images remain deletable. Satisfied contract text stays
readable even though those checks cannot be newly exempted. Isolated core and
offscreen WPF tests cover these controls and failure paths. This is not yet a
complete port of training cancellation/version controls, localization, or live
MainWindow interaction. Mac's batch-contract panel is currently unreferenced by
its reachable tab content; Windows does not introduce an extra visible panel.

Reference preferences use `labeling-v1/object-references.json`. Reusable icons
use a transparent 2048×2048 PNG with sorted 128×128 slots and schema-2 metadata.
The Windows cache also checks crop coordinates and source-image content, so
editing a box under its existing ID cannot keep a stale icon. Atlas builds run
off the UI thread; stale publications are rejected and shutdown drains writes
before a factory reset can archive data. Cache failures are nonfatal and visible
inside the reference panel. Native Mac resampling equivalence and the other
live-renderer/icon consumers still need verification.

Live object processing now separates filtered model boxes, pixel-refined temporal
tracks, and moving-Knight exclusion masks. The live ONNX path uses Mac's 0.5
ordinary-object / 0.05 Knight confidence thresholds, clipped normalized areas,
classwise overlap suppression and count limits. Tracks smooth jitter, confirm
ordinary candidates twice, bridge four missed inferences, and reset with capture,
labeling pause/resume, reference refresh, or a new game session. Frame/version metadata
is retained; manually selected unregistered runs have no invented version.
`Raw Detection` switches only the overlay, not gameplay recognition or mapping.
`Detection Extend` defaults to 12% **per side** before edge refinement;
`Detection Pixels` renders the actual magenta edge-support pixels instead of a
filled rectangle. Both live ground and atlas masks use the unsmoothed Knight
body, including weak observations, with age-bounded motion sweep and padding.
The live model runs separately from capture/analysis, retaining only one active
and the newest pending frame. Admission preserves Mac's nominal 15 Hz rate in
wall time (Mac captures at 60 Hz and samples every fourth frame; Windows currently
captures near 30 Hz). Menus schedule no new inference; labeling suspends it and
invalidates results. Results retain capture timestamps for freshness and motion
prediction, even when the model finishes later. Reset rejects late results and
errors; shutdown drains model work before disposing ONNX, and capture restarts
cannot overlap. Model failures show a non-blocking top-right notice, clear after
recovery, and never stop menu/HUD recognition or preview. Debug settings expose
model/refinement timing and source-frame metadata. No-model capture schedules no
inference. Synthetic barrier-based tests and offscreen live-recognizer tests
cover this lifecycle; an isolated promoted ONNX model also runs through the worker.
Native Core ML/ONNX output equivalence and measured cross-platform live
performance/latency still require verification.

Recent Frames now keeps the latest 64 live captures alongside saved labeled
examples in independently scrolling columns. Thumbnails preview saved positive
and negative boxes in cyan/pink and captured predictions in purple. Windows
retains native title-bar controls. Single-click opens a frame; Resume (L or X)
returns to gameplay, while Close/Escape only dismisses the picker. Saved images
keep their screen context and every rectangle. Captured predictions infer the
screen with Mac's priority rules, retain repeated/cross-context boxes and save
a draft in the current capture group/language. Empty captures remain unsaved.
Asynchronous predictions replace only their exact generation/frame entry,
without changing order, pixels or an already-open picker snapshot. Capture
generations are independent of model/reference refreshes. Core and offscreen
WPF tests cover these policies, rendered previews, controls and the real
recognizer publication path. Complete editor-state transitions, adaptive sizing
and measured native Mac comparison remain audit obligations.

Label entry now waits for a clean captured frame. In Modded mode, Vision releases
held controls, requests the receiver's negotiated pause lease, and enables the
editor only after a valid pause acknowledgement. The lease renews every 750ms.
Normal mode freezes Vision's captured image locally, without pausing the game.
A frozen editor stops capture conversion/analysis; queued work is tagged with a
pause revision so old results cannot reappear after resume. Recent-frame history
only grows while ready. Leaving Label resumes the receiver before post-label
randomization; pausing/resuming and path recording prevent conflicting label
entry. Shutdown attempts resume with a 500ms send bound, then disconnects; the
receiver's lease/disconnect behavior provides recovery if sending fails. Pure
state tests, an ephemeral loopback receiver and offscreen control tests cover
these paths. Real Windows/Mac game-session verification remains outstanding.

The shared RGB stencil kernel and multi-capture menu consensus are also ported
and covered by synthetic Mac-equivalent regressions: capture alignment, stable
glyph masks, localized word widths, and idle-text subtraction. HUD matching uses
the shared kernel. Selector calibration data/persistence is also ported: the
exact bundled Mac positions, independent language placements, stable two-second
learning, reviewed corrections and explicit-only project mirrors. Isolated
tests cover high frame rates, animation outliers, stale/corrupt files and failed
atomic writes. The catalog reference builder now includes focused drafts,
foreign text/probes, capped live captures, held-out positive exclusion, inferred
selector rows, idle-text masks, four Clear Save pairs and the 46 reviewed Mac
correction mappings. Synthetic pixel tests exercise catalog-to-calibration
persistence as well as individual reference rules. The core image-driven tracker
now acquires/revalidates scenes and languages, ranks paired arrows, handles
modal ownership and probe-loss grace, and learns positions from stable live
matches. Direct Mac 72x40 fixtures test search regions, row rejection and
image-driven recalibration; synthetic transitions test shared headings,
language changes, two-column rows and animated dialog titles. Production now
loads contract-valid labels and live captures into this tracker. Gameplay uses
exact-frame recognition; Hacker uses a bounded background worker with a 250ms
result limit. Reference refreshes invalidate old results, and stopping capture
drains calibration writes before a factory reset can archive data. WPF draws
Mac-style dashed search regions, verified text pixels, and accepted arrow pairs.
Offscreen tests exercise saved PNG references through the production recognizer.
These tests do not establish full menu
recognition parity across the real multilingual corpus. Non-integral
resampling still needs comparison with CoreGraphics on macOS. The audit records
two stale Mac calibration-test assumptions discovered against the current
bundled resource.

The live status handoff also follows Mac's rules: Gameplay remains latched when
HUD cues disappear, menu animation grace lasts 0.8 seconds, and an established
game requires repeated menu evidence after the HUD's 0.35-second hold. HUD
matching always runs so a false menu candidate cannot suppress its contrary
evidence. Menu stencils own menu states; retired HUD/menu object-model outputs
cannot establish a state. Current knight/enemy/world evidence can establish
Gameplay. Credits requires prior Extras navigation and keeps object inference
running so gameplay can disprove it. Transport resets preserve established
Gameplay; switching game processes clears it. A presentation latch alone does
not admit frames to the atlas. Full visual transition/ground-recovery/room
tracking integration remains an audit obligation, not implied by these tests.

Scene exclusions now use the Mac live policy: only the highest-confidence
`game.playable-knight` detection supplies the moving region, with a 0.5 atlas
threshold and a separate 0.05 ground-tracking threshold. Unrelated objects and
retired HUD detections cannot erase room pixels. The persistent HUD shield plus
exact stencil masks remain independent of model output. Modded atlas writes
instead use the receiver-projected hero bounds, with the same dimensions and
vertical offset as Mac. Masks use the actual capture dimensions, not the fixed
editor canvas. Regression tests exercise atlas persistence, weak-Knight motion
exclusion, coordinate conversion, clipped rectangles, opaque black scenery,
and the shared photographic HUD fixture. The legacy image-based detector is
also ported and tested against Mac's synthetic fixtures: fixed-size Knight
boxes, velocity filtering/occlusion expiry, dark-cloak/bright-halo rejection,
currency digit bounds, conditional focus-prompt fade holding, and temporal
particle lifecycle with camera compensation. Both current applications keep
this helper off the live model/stencil-owned path. Windows adds an input-size
reset guard to keep differently sized intensity histories from mixing. The
complete diagnostic renderer and native CoreGraphics resampling verification
remain outstanding.

Frame-signal profiling now retains Mac's full-aspect 64-wide RGB peak/luma
sample, signal thresholds, black-edge measurement and straight matte-mask
geometry. Capture admission follows the Mac blackout gate: ordinary darkening
stays trackable, a true blackout suspends updates, and three stable comparisons
are required before resuming. The first settled frame seeds motion without an
atlas write. Both visual and modded atlas writes require this image gate in
addition to current gameplay evidence. The capture worker owns gate state and
resets it when capture generations change. Ground updates and motion follow
the same admission decision. Padded foreground masks are scaled from capture
coordinates; both motion images receive the union of old and new exclusions so
the moving Knight mask cannot become false camera motion. Tests cover dark
silhouettes, HUD-only loading frames, settling/reset boundaries, nonzero mask
origins and real background translation under a moving foreground.

These additions do not replace the remaining ground-first solver, Mac Vision
registration, transition-bridge and room-ownership work. Straight edge masks
are available but are not applied indiscriminately to live dark scenery: their
integration requires the still-unported transition-bridge decision. Windows
signal resampling uses center-sampled bilinear RGB; native CoreGraphics
interpolation and fractional mask coverage still require Mac verification.

The desktop lab contains five workspaces:

- **Gameplay** continuously builds and auto-saves the tiled atlas while showing
  the current game frame. Current/Fit/Free Fly framing, pan/zoom, object,
  stencil, motion, transition, and persistent-ground overlays run on CPU.
- **Label** captures an already-running visible game, adds/moves/resizes/
  reclassifies boxes, converts hard negatives, supports undo and 1–4/R
  shortcuts, and provides a thumbnail-backed Recent Frames review. Scroll to
  zoom at the pointer (1–8×), right-drag or Shift-drag to pan, and right-click
  without dragging to fit. Dashed
  horizontal/vertical guides follow the pointer in Add and Negative modes;
  the system cursor hides while guides are visible. Box borders stay two DIPs
  wide and outside the selection, with their interior edges aligned to the
  guides. Zooming and panning never change saved annotation coordinates.
- **Stencil** uses a dark, phase-grouped context list with completion indicators
  and a responsive screenshot preview with named annotation boxes. Preview
  overlays follow the fitted image at every window size; missing screenshots
  show context-specific guidance. Edit and delete actions stay with the preview.
- **Model** provides data-backed class counts, object references, screenshot
  review, leak-safe export/validation, and CPU-only ONNX training. Completed runs
  promote to durable `models-v1` versions, including ONNX class metadata and
  checkpoints; the toolbar displays the active version. Training automatically
  selects the stored development checkpoint, and live inference reloads after
  its previous worker stops. The full Mac model-review UI is still being ported.
- **Ops** reads and applies the receiver's player test state, randomizes it,
  controls invincibility, and restores enemies.
- **Hacker / Atlas** incrementally updates only dirty atlas tiles, supports
  Current/Fit/Free Fly framing, persistent ground and room editing, atlas
  reset, path recording/replay, and offline room-graph build/optimization.

Every ordinary desktop launch asks for **Normal Hollow Knight** or **Modded
Hollow Knight**. Normal keeps local menu recognition and visual Gameplay atlas building
active without a receiver. Recognized menu frames are excluded from atlas and
ground updates. Modded enables Hacker only after fresh ground-truth
telemetry and Ops only after the player-ops capability arrives. For managed
launches, bypass the chooser and select a dedicated installation:

```powershell
./HollowKnightVision.Windows.exe --game-mode normal --game-path 'D:\Games\Hollow Knight\hollow_knight.exe'
./HollowKnightVision.Windows.exe --game-mode modded --game-path 'D:\Modded\Hollow Knight\hollow_knight.exe'
```

The chooser disables a mode when Vision cannot find a complete compatible
installation. Selecting an available mode gracefully closes a different running
copy before launching the matching executable. Normal requires an unmodified
installation; Modded requires both the Modding API loader and Vision receiver.

Normal mode supports keyboard play without a receiver or virtual device driver.
The first gameplay key gives Hollow Knight Windows keyboard focus while Vision
stays visible above it. Moving the mouse returns focus and the pointer to Vision,
so the stock game's cursor lock cannot trap navigation. Startup and local
shortcuts do not automatically hand focus back to the game.
G/L/S/M/H/O/K remain Vision shortcuts; switching to Label, Stencil, Model,
Settings, or a text field gives Vision its own input. The label editor retains
1-4, R, Ctrl+Z, and X; holding X while leaving an editor does not attack in the
game. Switching to another application ends the foreground presentation.
After clicking Vision's game view, the first gameplay key transfers focus and
replays that press; subsequent keys reach the stock game directly. Run Vision
and Hollow Knight at the same Windows privilege level for native input.

In Settings, select **Auto Save → Delete** to clear the current atlas after
confirmation. Auto Save remains available for fresh captures. Capture and pending
writes are drained first; the old world becomes
a **Before reset** restore point that can be loaded again. Stencil labels, model
training data, other saved states, and game saves are untouched. Only recognized
Gameplay frames register in the new atlas, never known menus or unknown fades.

The single bottom bar shows blue guidance for language-specific 640×360 menu labels. The gear opens settings. Reset Stencils and Factory Reset move data to
`%LOCALAPPDATA%\HollowKnightVision Backups`; neither action edits the game,
mods, or Hollow Knight saves.

Building does not launch the app or game. Buttons clearly distinguish the one
workflow that launches Hollow Knight from workflows that only attach to it.

Run the complete non-launching readiness audit at any time:

```powershell
./test-readiness-windows.ps1
```

It verifies the installed loader/receiver against local build hashes, both
published executables, CPU-only PyTorch, the smoke ONNX model, blank-frame CPU
inference, and that neither the game nor desktop lab is running.

With Hollow Knight already running, locate its window without reading pixels:

```powershell
dotnet run --project src/HollowKnightVision.Windows.Host -c Release -- --probe
```

Capture one normalized reference frame:

```powershell
dotnet run --project src/HollowKnightVision.Windows.Host -c Release -- `
  --capture-once artifacts/hollow-knight-640x360.bmp
```

Verify the in-game receiver or print one Hacker camera-transform sample:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --receiver-probe
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --ground-truth-once
```

After the matching receiver is installed, automatically launch Hollow Knight,
select **Start Game**, select profile **1**, and require eight stable gameplay
frames before reporting success:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --auto-navigate-gameplay `
  --game-exe 'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe'
```

This startup path is CPU-only. It uses bundled 640x360 menu calibration and
sends 85 ms Up/Z pulses through `127.0.0.1:36752`; it never injects desktop
keys. The default deadline is 120 seconds; change it with
`--startup-timeout-seconds N`.

The matching Unity-6 loader and receiver can be inspected, installed with an
automatic per-file backup, or restored with:

```powershell
./install-runtime-windows.ps1 -Mode Plan
./install-runtime-windows.ps1 -Mode Install
./install-runtime-windows.ps1 -Mode Restore
```

Installation refuses unsupported vanilla assembly hashes and a running game.
Backups live under `hollow_knight_Data/Managed/.hkv-windows-backups` and the
installer rolls back automatically if any copied file fails hash verification.

Exercise the latest-frame policy at the current atlas cadence:

```powershell
dotnet run --project src/HollowKnightVision.Windows.Host -c Release -- `
  --frames 7 --interval-ms 133 --capture-once artifacts/latest.bmp
```

While gameplay and the receiver are active, record a world-projected atlas:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --record-atlas C:\path\to\atlas-session `
  --atlas-seconds 60 --atlas-ppu 64 --interval-ms 133
```

The recorder uses camera telemetry, omits the top HUD band, blends repeated
observations, and writes 256x256 BGRA BMP tiles plus `atlas.json`. Output must
be new or empty, so existing atlas evidence is never overwritten silently.

## Label examples and dataset export from the CLI

The WPF Label workspace is the normal authoring path. Equivalent commands are
available for automation. Capture does not launch Hollow Knight:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-capture "$env:LOCALAPPDATA\HollowKnightVision\labeling-v1\examples" `
  --label-context game `
  --label-box "enemies.crawlid,0.10,0.20,0.25,0.30" `
  --label-known-class enemies.crawlid
```

List examples, replace an example's boxes, or delete one example:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --label-list C:\path\to\examples
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-update C:\path\to\examples\EXAMPLE_UUID `
  --label-box "enemies.crawlid,0.10,0.20,0.25,0.30,negative"
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-delete C:\path\to\examples\EXAMPLE_UUID
```

Export one or more repeatable `--label-known-class` identifiers:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-export C:\path\to\examples `
  --dataset-root C:\path\to\training-v1\datasets `
  --label-model shared-object-model `
  --label-known-class enemies.crawlid
```

The target-window path continues capturing Hollow Knight while the Vision
dashboard covers it, including black/low-contrast fade frames, without a
desktop-capture fallback.
Current mode supports smooth wheel zoom (0.1–8×), anchored to the moving live
view's center rather than the mouse. Clicking Current restores its default zoom.
Gameplay Fit is applied on request or window resize, not when atlas bounds grow;
unrecognized cutscene frames retain the last live placement. Free Fly uses plain
left-drag to pan and the wheel to zoom.
Ground Features is enabled when the dashboard opens. Confirmed persistent and
global-match cells use the same purple review rectangles as Mac; live ground,
occlusion, and depth-error cells retain the shared green, orange, and red
palette. Candidate cells remain hidden.

The widened 192-pixel production ground-patch search uses deterministic
multicore CPU evaluation on Windows and retains a scalar fallback. The smaller
edge/kernel preprocessing remains scalar automatically because dispatch overhead
usually costs more than it saves. This ports the latest Mac tracking behavior
and measurement seams, but it does not claim Apple Metal or NVIDIA execution.
Set `HKV_WINDOWS_COMPUTE_BENCHMARK=1` while running the core tests to print
scalar/parallel medians. Capture, normalization, ONNX inference, and training
remain CPU-only. Windows Graphics Capture is still the planned high-frame-rate
capture backend and would normally use a GPU copy path.

Diagnose visual camera motion without receiver telemetry or launching the game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --visual-track-seconds 20 --interval-ms 133
```

The diagnostic downsamples captures to 64×36 luma, masks moving dark/fade
regions, estimates sub-cell 2D translation, rejects scene jumps, and reports
the accumulated CPU camera pose and rejection reasons.

Inspect semantic ground on one frame from an already-running visible game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --ground-detect-once
```

The one-shot command runs the macOS-parity 32×8 ground kernel, occlusion
bridging, row suppression, and dark-platform-body validation on CPU. Developer
sweeps can override `--ground-threshold`, `--ground-minimum-segment`,
`--ground-line-separation`, and `--ground-occlusion-gap`.

Record a visual route together with receiver camera truth, then replay the
saved CPU evidence three times without the game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --record-visual-route C:\path\to\route --route-seconds 30
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --replay-visual-route C:\path\to\route --route-repeats 3 `
  --route-report C:\path\to\route\replay-report.json
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --build-route-world C:\path\to\route
```

Recording attaches to active gameplay and never launches it. Replay reopens
the persisted evidence between passes and gates mean and worst-step pixel error.
World construction writes `ROUTE_DIR\world\world.json`, assigns stable rooms by
scene, emits only accepted visual-motion edges, and refuses to overwrite prior
world evidence.

Optimize a persisted graph after motion or loop-closure edges are added:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --optimize-world C:\path\to\route\world
```

The solver keeps raw capture poses immutable, holds each connected component's
anchor exact, distributes loop drift deterministically, and commits exactly one
new map revision.

Replay a schema-v1 input path recorded by the macOS app while gameplay is
already running:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --replay-input-path C:\path\to\path.json --path-repeats 3
```

When the file has a start checkpoint, Windows restores it through
`player-checkpoint-v1` before each pass. `--skip-path-checkpoint` preserves the
current game state. Playback drains receiver telemetry and always sends a
neutral state on completion, cancellation, or failure.

## CPU model training on Windows

The shared incremental detector can train on Windows without Core ML and export
an ONNX model. Setup installs CPU-only PyTorch into the repository's ignored
`.tools/hkv-training-windows` environment and caches the pretrained MobileNet
backbone under `.tools/hkv-training-cache`:

```powershell
./setup-training-windows.ps1
```

Validate an exported labeling dataset without training:

```powershell
./train-model-windows.ps1 -Dataset C:\path\to\dataset -ValidateOnly
```

Train and export `Detector.onnx`, `Detector.pt`, predictions, metrics, and the
run manifest:

```powershell
./train-model-windows.ps1 `
  -Dataset C:\path\to\dataset `
  -Output C:\path\to\runs\first-windows-run `
  -Iterations 100
```

Run the exported detector against one live frame with CPU-only ONNX Runtime:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --infer-once C:\path\to\runs\first-windows-run
```

Validate model loading and native runtime packaging without launching the game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --model-probe C:\path\to\runs\first-windows-run
```

The host reads `training.json`, converts BGRA to the trainer's RGB NCHW tensor,
and applies the same 3x3 local-maximum filter, 0.05 confidence floor, per-class
nonmaximum suppression, and detection limits as training review.

Pass `-BaseCheckpoint C:\path\to\Detector.pt` for an incremental run. The
wrapper forces `--device cpu` and hides CUDA devices. The WPF Model workspace
drives the same validator and trainer.

Windows has implementations for atlas persistence, state management,
ground/room editing, input-path recording, training and player operations.
These surfaces do not establish full Mac parity: menu calibration, model review,
live world tracking and other obligations remain in `mac-parity-audit.json`.
The recorded real-game route acceptance gate is also outstanding. All current
Windows analysis, training, tests, and published readiness probes are CPU-only.

See [WINDOWS_PORT.md](../macos/WINDOWS_PORT.md) for the dependency
audit and ordered port plan.

### Stencil walkthrough and model workspace

The gear opens a centered, scrollable Settings dialog with atlas actions, Debug View, Game language, and Factory Reset. Language names use their native spelling. Navigate to the screen named by the blue guidance in the single bottom row, then press **L** or **Label**. Game State is an optional overlay under Debug View. Free Fly, Fit, and Current appear only after Phase 1 while Gameplay is recognized in the Gameplay workspace; Ops also requires Modded mode and a recognized gameplay state. Required menu labels follow menu order and disappear from Add after their required boxes are present (two for selection decorations); Modify/Delete keep them editable. Gameplay objects remain repeatable and alone expose **+ Object**.

**Stencil** derives its checklist from the contracts: 26 required screens in Modded, 25 in Normal (excluding Mods). Phase 1 is Main Title, Select Profile without Clear Save, and a separately captured Gameplay HUD reference (two adjacent full health icons). It unlocks Model and Stencil Detection. Phase 2 contains the other menu/game screens; Phase 3 contains Clear Save, which requires an existing save and is dismissed with No. Credits has no required boxes. Each stencil category keeps one reference; gameplay model objects remain repeatable. Reset Stencils below the sidebar archives stencil references and calibration while preserving gameplay training examples. Factory Reset also removes stencils. The gear opens a centered, scrolling dialog with Save State, New, Delete, Load, all debug controls, language, and Factory Reset. Object Detection stays disabled until an active model has completed training and evaluation on current gameplay labels; bundled models and models from a cleared dataset do not unlock it. Framing controls require both Phase 1 readiness and recognized Gameplay in the Gameplay workspace.

Localization coverage and remaining native-language/Windows UI checks are tracked in [TODO](../TODO.md).

Existing partial references show **Click Stencil and Edit Labels** guidance. Each reference has **Edit Labels** and **Delete Screen Shot** actions, and label boxes show their localized class names without requiring captured icons. **L** selects Gameplay when gameplay is recognized; explicit Edit Labels still opens the selected stencil. The wider object selector sits above the editing controls to avoid overlap at small window sizes.

Hacker is offered only for recognized gameplay with mod telemetry. Ops controls live in Settings and require recognized gameplay plus the player-ops capability. Clicking outside Settings closes it; arrow keys continue forwarding to the modded game. Dragging in Current camera mode switches to Free Fly while ordinary clicks retain game input.
