# Capture pixels

ScreenCaptureKit supplies 32-bit BGRA buffers at 640px width, native display
cadence, and queue depth 3. `CaptureFrameConverter` uses Core Image to preserve
color management, remove window chrome/letterboxing, and produce the 640×360
presentation image. Ground detection and tracking share DeviceGray extraction
with no interpolation; integer texture patches retain their original orientation.

The owned-BGRA copy, pixel-copy audit, and conversion benchmark were retired on
2026-09-29. Their opt-in path had not replaced the working Core Image default.
Historical pixel-equality measurements and recordings remain in
`hkv-pixel-copy-20260928` at the outer workspace root. No new capture speed or
image-quality improvement is claimed by this cleanup.
