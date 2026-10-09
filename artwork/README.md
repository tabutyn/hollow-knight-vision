# Application icon

`app-icon-source.png` is the 1024×1024 master for the macOS and Windows
application icons. Its monocle uses segmented metal plating, fasteners, a
mechanical shutter iris, an articulated arm, and cyan sensor indicators.

The current artwork was edited with the built-in image generation tool from
the previous icon composite. That composite used Hollow Knight's installed
`PlayerIcon.icns` with a right-eye monocle. The edit preserves the original
mask design and white background; it is not a pixel-identical composite.
The complete editing prompt is recorded in `app-icon-robotic-prompt.txt`.

`app-icon-monocle-overlay.png` is the earlier thin-ring overlay retained for
provenance. It is not used by the current icon.

The compiled files are `macos/Resources/HollowKnightVision.icns` and
`windows/src/HollowKnightVision.Windows.App/Resources/HollowKnightVision.ico`.
The macOS packager includes an artwork hash in the icon resource filename so
an uncommitted icon revision can refresh the installed app's icon.

Hollow Knight and its original icon artwork are © Team Cherry. The original
artwork and these icon derivatives are not covered by this repository's MIT
license.
