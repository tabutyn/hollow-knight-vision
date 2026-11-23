# Capture pixels

ScreenCaptureKit supplies 32-bit BGRA buffers at 640px width, native display
cadence, and queue depth 3. `CaptureFrameConverter` uses the window content
rectangle and display scale attached to each frame to remove stream padding
after a resize or display move. Core Image then applies the gameplay crop and
produces the 640×360 presentation image. Ground detection and tracking share DeviceGray extraction
with no interpolation; integer texture patches retain their original orientation.

The owned-BGRA copy, pixel-copy audit, and conversion benchmark were retired on
2026-09-29. Their opt-in path had not replaced the working Core Image default.
Historical pixel-equality measurements and recordings remain in
`hkv-pixel-copy-20260928` at the outer workspace root. No new capture speed or
image-quality improvement is claimed by this cleanup.
