# Input and frame-rate audit — 2026-09-07

The integration has a confirmed default action-mapping error, a reproducible input queue delay, and capture-buffer starvation reproduced in an isolated live probe. Fix these before broad rendering changes. This audit did not change or restart either running application or install a new receiver.

## Measurements

Environment: Hollow Knight 1.5.12620 / Unity 6000.0.61f1, native macOS arm64. Running Vision was `/private/tmp/hkv-live-run/arm64-apple-macosx/debug/HollowKnightVision` (PID 47345). Game PID 45097.

| Check | Result | Scope |
| --- | --- | --- |
| Existing Vision log, roughly 16:36–16:39 local | About 2.2 rendered outputs/s | Counter measures render outputs using source timestamps, not physical display presentation. |
| Capture-only probe against same background game, 6 seconds | 178 complete frames; 28.38 FPS; frame-gap p95 37.81 ms | Source can deliver near 30 FPS while current Vision is running. |
| Isolated probe: every other capture enters a latest-frame worker, registration plus 900 ms simulated work, retaining capture-backed CIImages | 17 frames; 2.84 FPS; gap p95 916.22 ms | Reproduces starvation from slow-worker ownership with queueDepth 3. |
| Same probe/work, copying worker input into owned CVPixelBuffers | 174 frames; 28.21 FPS; gap p95 37.63 ms | Ownership change restores capture delivery. Isolated reproduction, not a patched-app measurement. |
| Existing feature relocalization fixture, debug | Passed; 6.357 s | Identical fixture below. |
| Same source and fixture, release | Passed; 0.176 s | About 36x faster for this fixture, not a claim of 36x application FPS. |

A five-second `sample` of live Vision found: main thread 3536 samples, mostly waiting in its event loop; vision worker 3536 samples, 3457 inside persistent feature tracking; render worker 28 samples; sample callback worker 16. Counts are thread samples, not application-wide CPU percentages. Feature matching was the observed expensive work; rendering was not the dominant sampled bottleneck in this scene.

The offline queue probe compiles the actual C# `Protocol.cs` and `LocalInputServer.cs`. It never starts a listener or talks to the game. Input is produced at 62.5 Hz for 5 seconds and released at 4 seconds:

| Simulated consumer | Max queue | Oldest actual state consumed | Release delay | Overflow-injected neutral states consumed |
| --- | --- | --- | --- | --- |
| 30 Hz | 32 | 512 ms | 200 ms | 5 |
| 60 Hz | 13 | 200 ms | 166.7 ms | 0 |

This proves production FIFO behavior under deterministic scheduling, not actual network or end-to-end gameplay latency. The harness does not simulate wall-clock watchdog expiry: continuous production refreshes receipt freshness while stale snapshots remain queued.

The current receiver replaces that FIFO with a 256-transition bounded edge path; identical held-state heartbeats update freshness without entering it. The reproduction command compiles both `Protocol.cs` and `LocalInputServer.cs`.

## Findings

1. **X and A are swapped relative to default game actions.** `hollow-knight-input-receiver/src/VisionInputDevice.cs:17,32` sends X to Action2 and A to Action3. Read-only inspection of the installed assembly's `ControllerMapping` constructor establishes `jump=19 (Action1)`, `attack=21 (Action3)`, `cast=20 (Action2)`. `InputHandler.MapControllerButtons` binds actions from these mapping fields. Thus X sends default cast and A sends default attack. Current custom bindings were not inspected in live game memory. A robust fix must resolve current bindings rather than assuming action numbers describe their meaning.

2. **Heartbeat snapshots compete with real transitions.** `GameControlForwarder.swift:24` uses 16 ms (62.5 Hz). `LocalInputServer.cs:120` queues every snapshot, including duplicates. `Mod.cs:48` consumes one per Unity Update. Queue age grows even at 60 Hz; at 30 Hz overflow injects disabled neutral states. The watchdog measures recent network receipt while the game consumes older queued states. ACK writes run synchronously under the server lock on Unity's main thread (`LocalInputServer.cs:48`), so a non-reading peer can block game updates.

3. **Slow vision retains capture-pool memory.** The active model is `LiveCaptureBaselineModel.swift`, class `LiveCaptureModel`; `LiveCaptureModel.swift` contains inactive `LegacyLiveCaptureModel`. At baseline lines 417–431, a CIImage originating from a capture CVPixelBuffer enters `VisionFrame.registrationImage`. `LatestFramePump` retains active/pending items; capture pool depth is three. The isolated probe reproduces the ownership pattern and restores FPS by copying worker input. Exact private retention internals of Vision were not inspected. Apple documents that holding capture surfaces too long stalls delivery: [ScreenCaptureKit surface lifecycle](https://developer.apple.com/videos/play/wwdc2022/10155/?time=937).

4. **Feature matching is expensive in the debug live build.** `PersistentFeatureTracker.swift:550` searches each candidate pixel. Candidates allocate/normalize 9×9×2 float descriptors (`:635`) and use zip/reduce for distance (`:660`). Relocalization permits 48 radius-12 searches, in addition to active tracking and corner replenishment. The CPU sample and release fixture comparison support prioritizing this work. Registration, boxes, and landmark refinement share one serial vision task; slow refinement also delays publishing the new camera pose (`LiveCaptureBaselineModel.swift:455–509`).

5. **ACKs do not establish gameplay success.** `Mod.cs:52–53` acknowledges immediately after setting a field, before device input necessarily updates/commits or the hero consumes it. ACKs contain raw requested bits, not effective opposing-direction cancellation. `Protocol.cs:52` derives enabled from nonzero buttons, giving enabled-neutral snapshots an incorrect disabled ACK. Swift accepts any valid same-session ACK as connected without tracking age. The Python client reads only one 4096-byte buffer after its whole run (`test-client/vision_input_client.py:52`); the previous claim that all 100 cycles were acknowledged was unsupported. Swift receiver-model tests do not cover the production C# queue.

6. **Presentation and atlas geometry need separation.** Every visible frame is annotated, composited with the whole atlas, converted to CGImage, published via SwiftUI, then wrapped as NSImage. This becomes costly as the atlas grows, although it was not dominant in this sample. Fresh frames use the latest completed pose without a matching frame timestamp; atlas writes can pair new imagery with old poses (`LiveCaptureBaselineModel.swift:434–446`). Smooth display must not come at the expense of stamping imagery at stale positions.

## Small work packets, in order

1. **Correct and prove bindings.** Resolve Arrow/A/Z/X through active keyboard-action/device bindings using public APIs, with explicit default-mapping coverage. Apply state within the virtual device input update; acknowledge effective committed state via an asynchronous socket writer. Pass: X attacks, Z jumps, A casts/focuses as currently bound; menus and ordinary direct game controls work. Verify the hero action/animation in a playable room, not just a wire ACK.

2. **Eliminate input backlog, preserve taps.** Coalesce identical held-state heartbeats into freshness metadata. Preserve distinct press/release edges in a bounded path so a short tap reaches an input tick. Merely taking the last snapshot each frame would lose taps. Prioritize focus-loss/disconnect disable; expire stale edges with monotonic timing. Test actual C# consumption at 30/60/120 Hz, 100 cycles with every release counted, combinations, non-reading peers, UI stalls and reconnects. Pass: no accumulating delay during holds, no overflow neutral glitches, and measured key-event-to-effective-input p95 under 50 ms during representative gameplay.

3. **Free capture buffers and use a release build.** Give slow workers reusable owned image storage at the correct registration resolution. Display consumes the newest capture independently. Repeat the probe inside Vision with deliberate 200–900 ms vision work. Pass: at least 28 FPS across a 30-second animated run targeting 30 FPS, p95 presentation gaps below 50 ms, and capture-to-present age measured separately. Capture FPS alone is not a display pass.

4. **Separate fast camera registration from slow landmark refinement.** Give refinement its own bounded worker and explicit work/time budget. Return timestamped corrections. Write an atlas observation only against its corresponding solved pose. Cache descriptors/use fixed storage or vectorized comparison before expanding search. Pass: the elevation/return recording stays aligned while deliberately slow refinement neither stalls the view nor smears the atlas.

5. **Present live imagery over a persistent atlas texture.** After those gates pass, draw the latest frame as a GPU layer/quad transformed by camera pose, with HUD/knight vector boxes. Update dirty atlas tiles at a slower cadence. Use a display-driven presentation callback and one latest pending frame. Preserve Fit/Reset atlas and ordinary window focus. Establish stable 30 FPS first; evaluate 60 FPS against measured source delivery.

## Reproduction

The three source files beside this report are audit utilities, not production changes. The capture probe requests no new permission; it checks existing Screen Recording access, creates no visible window, sends no input, and stops capture after six seconds. Run modes separately. CPU sample: `/private/tmp/hkv-input-audit-sample.txt`.

From the repository's `macos` directory:

```sh
CLANG_MODULE_CACHE_PATH=/private/tmp/hkv-clang-module-cache swiftc -parse-as-library audits/input-latency-2026-09-07/CaptureProbe.swift -o /private/tmp/hkv-capture-audit -framework ScreenCaptureKit -framework AppKit -framework CoreImage -framework Vision
/private/tmp/hkv-capture-audit count
/private/tmp/hkv-capture-audit retain
/private/tmp/hkv-capture-audit copy

CLANG_MODULE_CACHE_PATH=/private/tmp/hkv-clang-module-cache swift test --scratch-path /private/tmp/hkv-test-final --filter CameraSolverTests.testFeatureAtlasRelocalizesAndCorrectsBacktrackingDrift
CLANG_MODULE_CACHE_PATH=/private/tmp/hkv-clang-module-cache swift test -c release --scratch-path /private/tmp/hkv-latency-audit-release --filter CameraSolverTests.testFeatureAtlasRelocalizesAndCorrectsBacktrackingDrift

HK_AUDIT_MANAGED='/Volumes/Expansion/Relocated/thomasbutyn/steam/steamapps/common/Hollow Knight/hollow_knight.app/Contents/Resources/Data/Managed'
mcs -out:/private/tmp/HkInputQueueAudit.exe -r:/opt/homebrew/Cellar/mono/6.14.1/lib/mono/4.7.2-api/Facades/netstandard.dll -r:"$HK_AUDIT_MANAGED/Newtonsoft.Json.dll" hollow-knight-input-receiver/src/Protocol.cs hollow-knight-input-receiver/src/LocalInputServer.cs audits/input-latency-2026-09-07/InputQueueProbe.cs
cp "$HK_AUDIT_MANAGED/Newtonsoft.Json.dll" /private/tmp/Newtonsoft.Json.dll
mono /private/tmp/HkInputQueueAudit.exe

mcs -out:/private/tmp/hkv-audit-il.exe -r:/private/tmp/hk-modding-api-12620/OutputFinal/Mono.Cecil.dll audits/input-latency-2026-09-07/InspectBindings.cs
MONO_PATH=/private/tmp/hk-modding-api-12620/OutputFinal mono /private/tmp/hkv-audit-il.exe "$HK_AUDIT_MANAGED/Assembly-CSharp.dll" ControllerMapping
MONO_PATH=/private/tmp/hk-modding-api-12620/OutputFinal mono /private/tmp/hkv-audit-il.exe "$HK_AUDIT_MANAGED/Assembly-CSharp.dll" InControl.InputControlType
```
