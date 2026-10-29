using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HollowKnightVisionInputReceiver
{
    internal sealed partial class ReceiverBehaviour
    {
        private static int replayDiagnosticFrames;
        private static void ScheduleReplayRenderDiagnostics()
        {
            // Explicit local diagnostic opt-in. Complete during the paused
            // preparation renders, before any measured replay frame.
            if (File.Exists(Path.Combine(Application.persistentDataPath, "hkv-replay-diagnostics.enabled")))
                replayDiagnosticFrames = 10;
        }
        private static JArray V(Vector3 v) { return new JArray(v.x, v.y, v.z); }
        private static JArray M(Matrix4x4 m) {
            var result = new JArray();
            for (int i = 0; i < 16; i++) result.Add(m[i]);
            return result;
        }
        private static JObject T(Transform t) {
            return t == null ? null : new JObject {
                ["name"] = t.name, ["position"] = V(t.position), ["localPosition"] = V(t.localPosition),
                ["scale"] = V(t.lossyScale), ["parent"] = t.parent == null ? null : t.parent.name,
                ["parentPosition"] = t.parent == null ? null : V(t.parent.position)
            };
        }
        private static void WriteReplayRenderDiagnostics()
        {
            if (replayDiagnosticFrames <= 0) return;
            int remaining = --replayDiagnosticFrames;
            if (remaining != 9 && remaining != 0) return;
            try {
                var hero = HeroController.SilentInstance;
                var cameras = new JArray();
                foreach (var c in Camera.allCameras) cameras.Add(new JObject {
                    ["transform"] = T(c.transform), ["mask"] = c.cullingMask,
                    ["near"] = c.nearClipPlane, ["far"] = c.farClipPlane,
                    ["worldToCamera"] = M(c.worldToCameraMatrix), ["projection"] = M(c.projectionMatrix),
                    ["culling"] = M(c.cullingMatrix), ["heroScreen"] = hero == null ? null : V(c.WorldToScreenPoint(hero.transform.position))
                });
                var renderers = new JArray();
                foreach (var item in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    if (hero == null || (item.bounds.center - hero.transform.position).sqrMagnitude > 2500f) continue;
                    renderers.Add(new JObject { ["transform"] = T(item.transform), ["boundsCenter"] = V(item.bounds.center),
                        ["boundsSize"] = V(item.bounds.size), ["enabled"] = item.enabled, ["active"] = item.gameObject.activeInHierarchy,
                        ["visible"] = item.isVisible, ["forceRenderingOff"] = item.forceRenderingOff, ["layer"] = item.gameObject.layer,
                        ["heroChild"] = item.transform.IsChildOf(hero.transform)
                    });
                }
                var masks = new JArray();
                foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    var name = fsm.gameObject.name.ToLowerInvariant();
                    if (!name.Contains("mask") && !name.Contains("secret") && !name.Contains("door")) continue;
                    if (hero == null || (fsm.transform.position - hero.transform.position).sqrMagnitude > 2500f) continue;
                    var states = new JArray();
                    foreach (var st in fsm.FsmStates) {
                        var actions = new JArray();
                        foreach (var ac in st.Actions) {
                            var info = ac.GetType().FullName;
                            var tag = ac.GetType().GetField("collideTag");
                            if (tag != null) info += " tag=" + tag.GetValue(ac);
                            actions.Add(info);
                        }
                        var transitions = new JArray();
                        foreach (var tr in st.Transitions) transitions.Add(tr.EventName + ":" + tr.ToState);
                        states.Add(new JObject { ["name"] = st.Name, ["actions"] = actions, ["transitions"] = transitions });
                    }
                    var vars = new JObject();
                    foreach (var b in fsm.FsmVariables.BoolVariables) vars[b.Name] = b.Value;
                    var sprites = new JArray();
                    foreach (var sp in fsm.GetComponentsInChildren<SpriteRenderer>(true)) sprites.Add(new JObject {
                        ["name"] = sp.name, ["alpha"] = sp.color.a, ["enabled"] = sp.enabled, ["active"] = sp.gameObject.activeSelf });
                    masks.Add(new JObject { ["transform"] = T(fsm.transform), ["fsm"] = fsm.FsmName,
                        ["state"] = fsm.ActiveStateName, ["enabled"] = fsm.enabled, ["active"] = fsm.gameObject.activeSelf,
                        ["variables"] = vars, ["states"] = states, ["sprites"] = sprites });
                }
                var snapshot = new JObject { ["masks"] = masks,
                    ["frame"] = Time.frameCount, ["remaining"] = remaining, ["timeScale"] = Time.timeScale,
                    ["hero"] = hero == null ? null : T(hero.transform), ["cameras"] = cameras, ["renderers"] = renderers
                };
                File.WriteAllText(Path.Combine(Application.persistentDataPath,
                    "hkv-replay-render-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + remaining + ".json"), snapshot.ToString());
            } catch (Exception error) { UnityEngine.Debug.LogWarning("HKV replay render diagnostic failed: " + error.Message); }
        }
    }
}
