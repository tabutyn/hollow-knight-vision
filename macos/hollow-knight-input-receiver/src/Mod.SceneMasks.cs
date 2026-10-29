using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HollowKnightVisionInputReceiver
{
    internal sealed partial class ReceiverBehaviour
    {
        // Names plus same-name sibling ordinals survive a game restart without
        // trusting Unity instance IDs or selecting another room's objects.
        private static string SceneObjectPath(Transform transform) {
            var pieces = new List<string>();
            for (var t = transform; t != null; t = t.parent) {
                int ordinal = 0;
                if (t.parent == null) {
                    foreach (var root in t.gameObject.scene.GetRootGameObjects()) {
                        if (root.transform == t) break;
                        if (root.name == t.name) ordinal++;
                    }
                } else {
                    for (int i = 0; i < t.GetSiblingIndex(); i++) if (t.parent.GetChild(i).name == t.name) ordinal++;
                }
                pieces.Add(Uri.EscapeDataString(t.name) + "[" + ordinal + "]");
            }
            pieces.Reverse(); return String.Join("/", pieces.ToArray());
        }
        private static JArray CaptureSceneMasks(string sceneName) {
            var result = new JArray();
            foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                if (fsm.gameObject.scene.name != sceneName || !SceneMaskCheckpoint.MaskFsm(fsm.FsmName)) continue;
                var bools = new JObject(); var floats = new JObject(); var sprites = new JArray();
                foreach (var b in fsm.FsmVariables.BoolVariables) bools[b.Name] = b.Value;
                foreach (var f in fsm.FsmVariables.FloatVariables) floats[f.Name] = f.Value;
                foreach (var sp in fsm.GetComponentsInChildren<SpriteRenderer>(true)) sprites.Add(new JObject {
                    ["objectPath"] = SceneObjectPath(sp.transform), ["alpha"] = sp.color.a,
                    ["enabled"] = sp.enabled, ["active"] = sp.gameObject.activeSelf });
                result.Add(new JObject {
                    ["objectPath"] = SceneObjectPath(fsm.transform), ["fsmName"] = fsm.FsmName,
                    ["stateName"] = fsm.ActiveStateName, ["enabled"] = fsm.enabled, ["active"] = fsm.gameObject.activeSelf,
                    ["booleans"] = bools, ["floats"] = floats, ["sprites"] = sprites });
            }
            if (!SceneMaskCheckpoint.Validate(result)) throw new InvalidOperationException("Room mask checkpoint is not serializable.");
            return result;
        }
        private static bool MaskAlreadyMatches(PlayMakerFSM fsm, JObject saved, Dictionary<string, Transform> objects) {
            if (fsm.ActiveStateName != (string)saved["stateName"] || fsm.enabled != (bool)saved["enabled"]
                || fsm.gameObject.activeSelf != (bool)saved["active"]) return false;
            foreach (var b in ((JObject)saved["booleans"]).Properties())
                if (fsm.FsmVariables.FindFsmBool(b.Name).Value != (bool)b.Value) return false;
            foreach (var f in ((JObject)saved["floats"]).Properties())
                if (Math.Abs(fsm.FsmVariables.FindFsmFloat(f.Name).Value - (double)f.Value) > 0.000001) return false;
            foreach (JObject sp in (JArray)saved["sprites"]) {
                var sprite = objects[(string)sp["objectPath"]].GetComponent<SpriteRenderer>();
                if (Math.Abs(sprite.color.a - (double)sp["alpha"]) > 0.000001
                    || sprite.enabled != (bool)sp["enabled"] || sprite.gameObject.activeSelf != (bool)sp["active"]) return false;
            }
            return true;
        }
        private static bool RestoreSceneMasks(PlayerCheckpoint checkpoint, out string failure, bool validateOnly) {
            failure = null;
            if (checkpoint.SceneMasks == null) return true; // Legacy recordings did not save room masks.
            var objects = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.gameObject.scene.name == checkpoint.SceneName) objects[SceneObjectPath(t)] = t;
            var bindings = new List<Tuple<JObject, PlayMakerFSM>>();
            foreach (JObject m in checkpoint.SceneMasks) {
                Transform target;
                if (!objects.TryGetValue((string)m["objectPath"], out target)) { failure = "Saved room mask is missing: " + m["objectPath"]; return false; }
                PlayMakerFSM match = null;
                foreach (var fsm in target.GetComponents<PlayMakerFSM>()) if (fsm.FsmName == (string)m["fsmName"]) match = fsm;
                bool stateFound = false;
                if (match != null) foreach (var state in match.FsmStates) if (state.Name == (string)m["stateName"]) stateFound = true;
                if (!stateFound) { failure = "Saved room mask state is incompatible: " + m["objectPath"]; return false; }
                foreach (var b in ((JObject)m["booleans"]).Properties()) if (match.FsmVariables.FindFsmBool(b.Name) == null) { failure = "Saved mask boolean is unavailable."; return false; }
                foreach (var f in ((JObject)m["floats"]).Properties()) if (match.FsmVariables.FindFsmFloat(f.Name) == null) { failure = "Saved mask float is unavailable."; return false; }
                foreach (JObject sp in (JArray)m["sprites"]) {
                    Transform sprite;
                    if (!objects.TryGetValue((string)sp["objectPath"], out sprite) || !sprite.IsChildOf(target)
                        || sprite.GetComponent<SpriteRenderer>() == null) { failure = "Saved mask sprite is incompatible."; return false; }
                }
                bindings.Add(Tuple.Create(m, match));
            }
            if (validateOnly) return true;
            foreach (var binding in bindings) {
                var m = binding.Item1; var fsm = binding.Item2;
                // Advancing from the current boundary must not restart an
                // unchanged fade/state action once every recorded second.
                if (MaskAlreadyMatches(fsm, m, objects)) continue;
                // Stop an abandoned attempt's fade before restoring its state.
                iTween.Stop(fsm.gameObject, "color", true);
                foreach (var b in ((JObject)m["booleans"]).Properties()) fsm.FsmVariables.FindFsmBool(b.Name).Value = (bool)b.Value;
                foreach (var f in ((JObject)m["floats"]).Properties()) fsm.FsmVariables.FindFsmFloat(f.Name).Value = (float)f.Value;
                fsm.gameObject.SetActive((bool)m["active"]); fsm.enabled = (bool)m["enabled"];
                // Set sprite alpha before entering the saved state, so a pending
                // fade resumes from this boundary instead of the failed attempt.
                foreach (JObject sp in (JArray)m["sprites"]) {
                    var sprite = objects[(string)sp["objectPath"]].GetComponent<SpriteRenderer>();
                    var color = sprite.color; color.a = (float)sp["alpha"]; sprite.color = color;
                    sprite.enabled = (bool)sp["enabled"]; sprite.gameObject.SetActive((bool)sp["active"]);
                }
                fsm.SetState((string)m["stateName"]);
            }
            return true;
        }
    }
}
