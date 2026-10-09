# World ground-line labels

Open **Hacker** or press `H`. **Debug View → Mark Ground…** also routes there. Hacker owns a second atlas that is never shared with Gameplay. Ground truth contains horizontal lines only.

- **Add**: drag from one endpoint to the other over a missed ground edge.
- **Modify**: drag an endpoint to resize a line, or drag its middle to move it. Lines remain horizontal.
- **Delete**: click a line to remove it.
- **Negative**: click a seeded line that should not exist, or drag a horizontal negative span.
- **Undo** reverses the last saved edit. Changes save automatically.

On the first entry into a game scene, the document is initialized from the currently confirmed green ground lines and persistent atlas ground lines. If no solved line is ready, initialization waits and retries as tracking updates. A scene is seeded once, so later detector output cannot overwrite human edits.

The game mod reports the Unity frame, scene, camera world position, and projection scale. While Hacker is open, a barcode in the excluded top border pairs each capture with telemetry from that exact Unity frame. Captures without an exact pair are skipped. The Hacker atlas places the paired frame directly from the game camera; it does not consume Gameplay's visual pose or atlas.

Labels are stored in Unity world coordinates. Each render projects them back into Hacker atlas coordinates with the paired camera transform and live-frame bounds. This keeps a long line fixed to the same ground while the camera moves and lets it extend beyond one screen. Camera truth and human labels remain debug/evaluation inputs; the visual tracker never reads them.

Positive labels render green, negative labels red, and the selected line white. Shift-drag pans the atlas and the scroll wheel zooms. The toolbar also accepts `1`–`4` for its four modes and Command-Z for undo.

The world-line document defaults to:

```text
~/Library/Application Support/HollowKnightVision/world-ground-lines-v2.json
```

Set `HKV_GROUND_LABEL_ROOT` to use an isolated directory for testing. Lines are grouped by Unity scene name and contain their minimum/maximum world X, world Y, positive/negative kind, and whether they came from detector initialization.
