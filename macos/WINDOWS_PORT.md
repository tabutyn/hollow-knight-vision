# Windows port status

The current application is a macOS executable, not a portable Swift package.
Its live host directly imports AppKit, SwiftUI, ScreenCaptureKit, CoreImage,
CoreML, Vision, Metal, Network, and ApplicationServices. Most camera, ground,
menu-stencil, atlas, and room algorithms also use CoreGraphics image and geometry
types. Swift on Windows cannot supply those Apple frameworks.

## Ready now

The sibling [`windows`](../windows) project provides a
buildable .NET 8 host:

- Win32 game-window discovery.
- Owned BGRA capture frames and a fixed 640×360 normalization contract.
- Center-crop coordinate projection.
- Latest-only bounded frame delivery.
- Versioned JSON-lines receiver client and Hacker camera telemetry models.
- BMP/hash evidence for cross-platform pixel comparisons.
- Semantic-ground atlas approval with stable-line reassociation parity.
- Cross-platform named atlas-state persistence and archive lifecycle.
- CPU-only incremental detector training with ONNX export.
- CPU-only ONNX Runtime inference with trainer-identical local maxima and NMS.
- Calibrated title/profile selector recognition and stable gameplay gating.
- `--auto-navigate-gameplay` with safe receiver Up/Z pulses and telemetry drain.
- Reversible Windows loader/receiver installation with exact vanilla hashes.
- Headless receiver-projected tiled atlas recording with BMP/JSON output.
- Schema-compatible label example persistence and PNG capture.
- Leak-safe training/validation dataset export with hard-negative handling.
- Native WPF Gameplay/Label/Model/Ops/Hacker workspaces.
- Live CPU ONNX model-review overlays in the Gameplay workspace.
- Mac-parity camera accumulation, fresh-atlas gating, and low-resolution visual
  room-motion translation with fade and visibility-mask handling.
- Mac-parity CPU ground extraction: 32×8 response kernel, occlusion bridging,
  row suppression, semantic surface validation, and foreground rejection.
- Persisted visual-route capture and repeated offline camera-truth error gates.
- Route-to-room-graph materialization driven only by accepted visual motion;
  receiver truth is held out for replay scoring.
- Versioned live-world observation/keyframe/landmark/edge persistence with
  graph validation and compare-and-swap snapshot updates.
- Deterministic translation pose-graph optimization matching macOS golden
  vectors, including weighted loop closures and immutable raw poses.
- Schema-v1 saved input-path replay with receiver checkpoint restore and safe
  neutral release.
- A non-launching readiness audit covering runtime hashes, CPU training, and
  published ONNX inference.
- CPU tests and Windows CI publishing self-contained CLI and desktop artifacts.

The in-game receiver already uses C# and a loopback JSON-lines protocol on port
36752. The Windows preparation path builds it against the exact experimental
Unity-6 loader/API and provides a dry-run-first installer with per-file backup,
automatic rollback, and restore. Runtime installation remains separate from a
normal host build.

## Port seams

| Capability | macOS implementation | Windows direction |
| --- | --- | --- |
| Window discovery | ScreenCaptureKit | Win32 enumeration, implemented |
| Frame capture | ScreenCaptureKit/CoreVideo | Occlusion-tested `PrintWindow` target capture with GDI fallback implemented; Windows Graphics Capture next for throughput |
| Pixel normalization | CoreImage | owned BGRA CPU implementation, implemented |
| Input/telemetry | Network.framework | `TcpClient` JSON-lines client and startup integration implemented |
| Vision geometry | CoreGraphics | portable camera/atlas/world geometry and BGRA image views implemented |
| Image operations | CoreImage/Vision | ground and motion CPU kernels implemented; optional Direct3D compute later |
| Object model | CoreML/Vision | CPU ONNX Runtime implemented; validation-set parity vectors next |
| UI/presentation | SwiftUI/Metal | Mac-shaped WPF canvas/workspace shell, atlas preview, authoring, startup, and live ONNX overlays implemented |
| Persistence | Foundation paths/Codable | versioned atlas and label JSON implemented |

## Ordered work

1. Record macOS golden vectors for frame crop, stencils, ground pixels, motion,
   atlas placement, and room transitions. The vectors must contain inputs and
   expected numeric outputs without Apple object archives.
2. Move the pure algorithms behind owned BGRA buffers and portable point/rect
   types. Port one subsystem at a time into `Windows.Core`, starting with HUD
   and menu stencils because their fixed-position results are easy to compare.
3. Replay longer saved receiver sessions in CI; checkpoint-aware path playback
   is implemented, but still needs a real-game repeated-route run.
4. Add Windows Graphics Capture with a bounded staging pool. Compare pixels and
   timing with the GDI reference before enabling it by default.
5. Require ONNX/CoreML class/box parity on the saved validation set before
   making ONNX detections authoritative.
6. The Windows dashboard now performs continuous receiver-projected atlas and
   room ingestion, persistent ground tracking, dirty-tile rendering, state
   management, and ground/room review editing. Continue comparing ordered
   rebuild output against recorded Mac evidence.
7. Hacker camera truth remains receiver telemetry, independent of the desktop
   capture backend. The remaining promotion gate is a repeated real-game route
   proving the edited topology and offline optimizer agree.

## Acceptance gate

Windows now recognizes menu/HUD state, controls the receiver, continuously
persists the atlas and room topology, edits ground/rooms, records input paths,
reviews/trains models, applies player operations, and repeats persisted route
tests. Promotion still requires a recorded real-game repeated-route run; that
live gate cannot be claimed from synthetic evidence alone.
