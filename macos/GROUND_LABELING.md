# World ground-line labels

Open **Hacker** or press `H`. **Debug View → Mark Ground…** also routes there. Hacker owns a second atlas that is never shared with Gameplay.

Choose **Ground**, **Wall**, **Spike**, or **Door** in the top bar to edit the corresponding lines, or **Rooms** to drag rooms into alignment. Ground and spike lines are horizontal. Wall lines are vertical. Door lines follow the dominant drag direction, horizontally or vertically. Editing works with Free Fly, Fit, and Current camera views. Endpoints have visible resize handles.

- **Add**: drag from one endpoint to the other over a missed ground edge.
- **Modify**: drag an endpoint to resize a line, or drag its middle to move it. Lines remain horizontal.
- **Delete**: click a line to remove it.
- **Undo** reverses the last saved edit. Changes save automatically.

Green ground lines are authored only through the editor. Detector output and room registration never add saved labels, and deleted lines are never regenerated. Older documents can still contain detector-imported lines; loading them preserves existing edits without importing any more. The toolbar separates the current room's saved line count from the total across all rooms. These counts include lines outside the viewport.

The game mod reports the Unity frame, scene, camera world position, and projection scale. While Hacker is open, a barcode in the excluded top border pairs each capture with telemetry from that exact Unity frame. Captures without an exact pair are skipped. The Hacker atlas places the paired frame directly from the game camera; it does not consume Gameplay's visual pose or atlas.

Labels are stored in Unity world coordinates and projected using their owning room's saved calibration and placement. Moving another room or its camera does not move them. Older rooms need one revisit to establish their calibration. Room alignment is saved with the Hacker atlas. Camera truth and human labels remain debug/evaluation inputs; the visual tracker never reads them.

Ground and walls render green, spikes orange, doors cyan, legacy negative labels red, and the selected line white. Door lines mark exits and prevent a fall path from being inferred beyond a marked floor edge. Spike lines mark hazards. The path planner leaves clearance around them when choosing where to walk or land. Shift-drag pans the atlas and the scroll wheel zooms. The toolbar accepts `1`–`3` for Add, Modify, and Delete, and Command-Z for undo.

The world-line document defaults to:

```text
~/Library/Application Support/HollowKnightVision/world-ground-lines-v2.json
```

Set `HKV_GROUND_LABEL_ROOT` to use an isolated directory for testing. Lines are grouped by Unity scene name and contain their minimum/maximum world X, world Y, positive/negative kind, and legacy detector provenance. New lines always have `seededFromDetector: false`.
