# Playing policy architecture

## What runs today on macOS

Vision watches gameplay for events. When an event occurs, it captures a clean game frame, attaches it to a local Ollama request, and asks the model to **return one gameplay selection or one menu command**. The fixed objective is to expand the Atlas. Thinking is optional at launch. OCR and Gymnasium run independently of it. [Earlier v3 presentation](playing-policies.pptx)

## Test the notebook first

Leave **Enable Thinking** unchecked. Open **Settings → Debug View → Text Reader**, enable **Read game text**, and open a tablet. Watch the boxes in **On screen**, then switch to **Log**. Hold the same page on screen. Repeated readings should leave the entry count unchanged. Resume the playthrough to see its saved text, or start a new one to see a fresh notebook. **Thinking → Log** opens the same reader.

To run the same algorithm checks locally from the repository root, use the following command with Ollama stopped or running.

```sh
swift test --package-path macos --filter GameTextReaderTests
```

Apple Vision supplies raw text, confidence and line bounds. The notebook indexes normalized text within each language. Nearby scans can complete a clipped prefix or suffix and absorb several fragments into one entry. Matching uses time, room and line position when available. It protects quantities, directions and controls. The displayed English line prefers dictionary words before OCR confidence. If two aligned misspellings share exactly one single-edit dictionary correction, the notebook can use that word. The first OCR string remains inspectable. Other languages keep their observed spellings. The dictionary works with Thinking off. [Dictionary license](../../macos/Sources/HollowKnightVision/Resources/notebook-english-NOTICE.txt) · [Hallownest words](../../macos/Sources/HollowKnightVision/Resources/notebook-hallownest.txt)

On this Mac, ten warm scans of each 640 × 360 recording took a median of 25 ms for the two-line Inspect prompt and 45 ms for the seven-line tablet. Those timings cover Apple Vision recognition only. The Text Reader displays the measured recognition time for each live scan.

The policy request retrieves bounded excerpts from this save, newest first for selection and then in reading order for presentation. References and room names identify their sources. Stored text can inform a decision. A current interaction prompt must still be visible before playback presses Up. [Notebook](../../macos/Sources/HollowKnightVision/GameTextReader.swift) · [Offline OCR tests](../../macos/Tests/HollowKnightVisionTests/GameTextReaderTests.swift)

### Live opening test on November 25

On this Mac, a new slot-4 game reached King’s Pass with Thinking off. The reader collected 14 temporary poem entries and seven credit entries. A bright cinematic frame was classified as **Quit To Menu**. The profile detector clears the pending slot on that classification. The reader still displayed **Before save selection** after the credits. Hollow Knight saved slot 4, but Vision created no notebook file for it. Reopening synthetic save profiles in a unit test does not exercise this transition.

For a local frame-by-frame audit, launch Vision with `--notebook-trace /absolute/output/directory`. Each successful scan writes the clean game image, raw OCR, match decisions, profile and temporary row snapshot under a unique run directory. Normal launches leave the trace disabled.

## What causes a screenshot

| Evidence | Capture event |
| --- | --- |
| First recognized state, entry into a different state, or a return to an earlier state | Queue the image that established this visit. For menus, include the state ID, visible elements, their labels and the highlight when known. |
| A room or save changes | Re-establish gameplay context. |
| A new sign track or an INSPECT prompt | A tracked sign sends the detector frame and its normalized bounds. An OCR-only prompt sends the current game frame. |
| A new NPC track or a LISTEN prompt | NPC visible. Visual detection requires a detector that recognizes that NPC class. LISTEN works without a trained NPC class. |
| Measured Knight position reaches the bound chart waypoint or goal, or the default frontier while there is no active route | Waypoint reached, target reached or Explore destination reached. A route disappearing on its own is a separate outcome. |
| Fresh measured graph state shows grounded Knight and no route | Dead end after 1.5 seconds of continuous evidence, or immediately with a confirmed uncrossed blockade. Missing telemetry alone supplies no dead-end evidence. |
| A route becomes available, an enemy appears, Focus becomes available, the selected menu entry changes or a bound maneuver ends | Reconsider the action on a fresh frame. |

With Thinking enabled, the capture path reports accepted state changes immediately, including transitions during a menu script. Lightweight gameplay observations also run every 200 ms. An unchanged menu costs no further model request. The bottom-right Thinking context opens with a click or **T**. Its Screenshot tab displays the latest event and its screenshot. Apple Vision OCR scans independently between events, waiting 450 ms between scans during the opening cutscene and 850 ms elsewhere. The image attached to a decision and its visible OCR share the same pixels. While the local model is busy, event frames queue in capture order. Older waiting images use temporary local PNG files. [Event detector](../../macos/Sources/HollowKnightVision/ThinkingInspector.swift) · [Navigation evidence](../../macos/Sources/HollowKnightVision/AtlasPathMap.swift)

## One request and one reply

Each decision sends both bundled schemas in the user message, alongside the screenshot, event, notebook excerpts and Vision’s legal selections. The message says which schema applies. Ollama’s `format` narrows the reply to the exact IDs and actions offered on this screenshot. Gemma returns one JSON object. A recognized menu with no selectable elements still reaches Gemma with a single legal reply, `{"menu":"wait"}`. [Gameplay reply schema](../../macos/Sources/HollowKnightVision/Resources/thinking-policy.schema.json) · [Menu reply schema](../../macos/Sources/HollowKnightVision/Resources/thinking-menu.schema.json)

### Decision contract and policy inputs

| Policy | Example Gemma reply | What Vision does |
| --- | --- | --- |
| Explore | `{"policy":"explore","parameters":{}}` | Runs the Explore motor. Vision binds the current frontier when available and measures new Atlas coverage. |
| Traverse | `{"policy":"traverse","parameters":{"waypointId":1}}` | Resolves this offer’s waypoint to its room, chart and coordinates. Checks the route again before moving. |
| Interact | `{"policy":"interact","parameters":{"verb":"inspect","targetId":0}}` | Checks that the same INSPECT prompt is still present, taps Up and verifies a new passage. A LISTEN prompt offers `talk` with its own handle. |

A detected sign triggers reasoning even before the Knight can read it. Vision sends the full clean game image with the sign’s top-left-origin bounds. If the sign is distant, Gemma can choose among the offered movement actions. A later INSPECT cue triggers a fresh offer with Interact.

| Menu script reply | What Vision does |
| --- | --- |
| `{"menu":"select","elementId":"pause.continue"}` | Moves one step at a time toward the observed element, checks the highlight after each step, then confirms when the requested ID is highlighted. |
| `{"menu":"more"}` | Pulses forward once. A verified change in highlight or visible elements triggers a new screenshot and a new offer. An unchanged screen ends the script. Returning to an already browsed page suppresses another forward request there. |
| `{"menu":"wait"}` | Sends no input. When Vision identifies a selectable element or recognizes another state, it sends the new evidence. |

Gemma can select **Quit to Menu** on Pause. When Vision detects the confirmation screen it sends that screenshot, its own Yes and No element IDs, the preceding state, and the element Vision recently confirmed, if any. Gemma chooses again. The same sequence applies to Title, Options, their submenus and other recognized screens. Each visit has its own identity. A slow reply from an earlier visit cannot press a button on a later one.

The Hollow Knight detector obtains element IDs, labels and positions from menu stencils. Gemma can choose a visible target before the cursor is identified. The script waits for verified focus before moving and confirms only on the target. Screens such as a shop without mapped controls still reach Gemma, with `wait` as their available command. Another game can reuse the same state history, menu JSON and navigation script. Its integration must supply its objective, a detector that identifies its screens and elements, and bindings for the four directions and confirm. [Menu data and script](../../macos/Sources/HollowKnightVision/ThinkingMenuScript.swift) · [Hollow Knight input mapping](../../macos/Sources/HollowKnightVision/BotDemoNavigation.swift)

In Gameplay or Hacker, click the orange notebook or press **T**. **Context** shows the full save log, evidence from the latest prepared request, Vision’s decision brief, and the schema explorer. The brief refreshes from observations every five seconds and on decision events. Each request includes its own copy. **Screenshot** shows the last event frame. **JSON** shows the actual reply and its dispatch outcome. Settings → Debug View → Thinking context controls panel visibility. The notebook works with Thinking off. **Show decision JSON in Finder** reveals the validated reply inside the session’s Thinking directory. Modded Gameplay and Hacker dispatch current replies after rechecking the save, model and live target. Normal mode supports inspection. A newer event supersedes an older reply before dispatch. [Request and resolver](../../macos/Sources/HollowKnightVision/ThinkingPolicyContract.swift) · [Menu script](../../macos/Sources/HollowKnightVision/ThinkingMenuScript.swift) · [Motor inputs](../../macos/BOT_SKILLS.md)

## Verification

The notebook tests exercise typos, repeat captures, distinct prices and directions, languages, and save isolation. The recorded-image test runs Apple Vision on the King’s Pass prompt and open tablet, repeats the page, and reopens its notebook without a language model. The selection tests check exact file contents, alternate routes, stale handles, event suppression and a real local Gemma tablet reply. [Recorded-image and event tests](../../macos/Tests/HollowKnightVisionTests/ThinkingEventTests.swift) · [Contract tests](../../macos/Tests/HollowKnightVisionTests/ThinkingClientTests.swift) · [Local model and playback tests](../../macos/Tests/HollowKnightVisionTests/BotSkillTests.swift)

On November 25, local Gemma returned Interact/Inspect for the recorded King’s Pass sign. The playback test resolved the offered prompt and emitted Up on a test input bridge. On two requests with the same recorded Pause image and Continue highlighted, Gemma selected Continue once and Quit to Menu once. Both elements were offered in that test. The menu test validated the replies without pressing a button in the running game. A scripted-response test exercised the separate more path and checked the actual pixels and updated offer in the second request.

A five-screen replay sent recorded English menu images through state recognition, the Thinking inspector and local Gemma 3 4B. Gemma chose Start Game on Title with Extras highlighted, Game on Options, Master Volume on Audio, slot 4 on Select Profile and No on Quit to Menu. The replay intercepted dispatch before game input. A separate input-bridge test chose Quit, confirmed Yes, reached Title and navigated through Settings to Audio. A return to a previously visited screen invalidated its earlier pending reply. [Replay and transition tests](../../macos/Tests/HollowKnightVisionTests/ThinkingMenuStateTests.swift)

An earlier v3 [save-disabled King’s Pass run](handoff-v3.json) records the offered routes, actual replies, button inputs and measured outcomes. The fixed controller cases exercise both directions and Focus. In a separate text-only choice within the same run, local Gemma picked `waypointId: 1` from two routes. The offer bound that handle to the leftward route. The Knight moved from x = 35.33 to x = 33.72.

The [v3 schema](policy-selection-v3.schema.json), [v2 handoff](handoff-v2.json), [v2 tablet trace](gemma-v2-tablet.json), and [earlier diagrams and experimental schemas](archive-v2/) are historical records of the retired scoring and quest-summary experiments.

## November 23 motor comparison

On November 23 the Hacker Gymnasium comparison gave each candidate 512 training decisions from the round-69 learner, then evaluated all three actors on the same three King’s Pass seeds. Mean path progress was 92.40 for the champion, 85.58 for the 820-input candidate and 82.69 for the 337-input candidate. All nine episodes stalled in the starting room. The champion retained its place. This experiment measured the shared motor. It leaves the performance of the six specialists open.
