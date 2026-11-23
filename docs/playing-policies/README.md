# Playing policy architecture

The macOS player has six policies. The November 23 planning update makes exploring unmapped Atlas territory the default objective. A player can replace it with an explicit goal in Thinking and restore it with **Use Atlas goal**. Shopping remains part of the goal proposal. The planner checks the visible item, price, wallet and reserve before recommending a purchase.

![A queued proposal is validated against the current screen and its highest eligible choice wins](policy-selection.png)

For a tablet, queue **Interact / Inspect** with a visible INSPECT cue and give **Explore / native** fewer points. Back in Gameplay or Hacker, press **B**. A visible prompt selects Inspect. If the prompt is absent, a fresh eligible Explore choice can run. A room change between queuing and pressing **B** expires the queue.

![Native and direct maneuvers share the control lease and record their outcomes](policy-controls.png)

Five policies project the four-frame observation into role-specific numeric inputs. Interact needs six flags for each proposed control. With no learned weights installed, each policy can already issue game controls. Reading a passage and mapping new cells have explicit outcome checks. Other maneuvers record their end reason and need task-specific evaluation to establish success.

## Latest matched-start result

On November 23 the Hacker Gymnasium comparison gave each candidate 512 training decisions from the round-69 learner, then evaluated all three actors on the same three King’s Pass seeds. Mean path progress was 92.40 for the champion, 85.58 for the 820-input candidate and 82.69 for the 337-input candidate. All nine episodes stalled in the starting room. The champion retained its place. This experiment measured the shared motor. It leaves the performance of the six specialists open.

Open the [six-policy guide](../../macos/BOT_SKILLS.md) for field groups, starter actions, training tasks, controls and the Gymnasium comparison procedure. The [PowerPoint deck](playing-policies.pptx) walks through selection, inputs, execution and the latest matched-start result. Regenerate both figures by running `python draw_diagrams.py` in an environment with Matplotlib.
