# Application icon

`app-icon-source.png` is the 1024×1024 master for the macOS and Windows
application icons. It uses a red HAL-inspired bionic eye, concentric metal
rings, and mechanical connections over the head and around its right side.
The face has circular eyes, a flat horizontal chin, and a plain white
background without a cast shadow.

The current artwork was generated with the built-in image generation tool,
using Hollow Knight's installed `PlayerIcon.icns` as the geometric reference.
It follows the original head proportions; it is not a pixel-identical edit.
The complete prompt is recorded in `app-icon-hal-prompt.txt`.

`app-icon-monocle-overlay.png` and `app-icon-robotic-prompt.txt` are retained
for provenance of the earlier monocle designs. Neither is used by the current
icon.

The compiled files are `macos/Resources/HollowKnightVision.icns` and
`windows/src/HollowKnightVision.Windows.App/Resources/HollowKnightVision.ico`.
The macOS packager includes an artwork hash in the icon resource filename so
an uncommitted icon revision can refresh the installed app's icon.

Hollow Knight and its original icon artwork are © Team Cherry. The original
artwork and these icon derivatives are not covered by this repository's MIT
license.
