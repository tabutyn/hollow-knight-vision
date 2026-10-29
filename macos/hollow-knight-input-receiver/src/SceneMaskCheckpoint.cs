using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HollowKnightVisionInputReceiver
{
    internal static class SceneMaskCheckpoint
    {
        internal static bool MaskFsm(string name) {
            return name == "unmasker" || name == "remasker" || name == "remasker_inverse";
        }
        private static bool Text(JToken value, int maximum) {
            return value != null && value.Type == JTokenType.String
                && !String.IsNullOrWhiteSpace((string)value) && ((string)value).Length <= maximum;
        }
        private static bool Number(JToken value, double low, double high) {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) return false;
            double v = (double)value; return !Double.IsNaN(v) && !Double.IsInfinity(v) && v >= low && v <= high;
        }
        internal static bool Validate(JToken token) {
            var masks = token as JArray;
            if (masks == null || masks.Count > 128) return false;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in masks) {
                var m = entry as JObject;
                if (m == null || !Text(m["objectPath"], 2048) || !Text(m["fsmName"], 64)
                    || !MaskFsm((string)m["fsmName"]) || !Text(m["stateName"], 128)
                    || m["enabled"]?.Type != JTokenType.Boolean || m["active"]?.Type != JTokenType.Boolean
                    || !identities.Add((string)m["objectPath"] + "|" + (string)m["fsmName"])) return false;
                var bools = m["booleans"] as JObject; var floats = m["floats"] as JObject;
                var sprites = m["sprites"] as JArray;
                if (bools == null || floats == null || bools.Count > 64 || floats.Count > 64 || sprites == null || sprites.Count > 256) return false;
                foreach (var b in bools.Properties()) if (b.Name.Length > 128 || b.Value.Type != JTokenType.Boolean) return false;
                foreach (var f in floats.Properties()) if (f.Name.Length > 128 || !Number(f.Value, -1000000, 1000000)) return false;
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in sprites) {
                    var sp = item as JObject;
                    if (sp == null || !Text(sp["objectPath"], 2048) || !paths.Add((string)sp["objectPath"])
                        || !Number(sp["alpha"], 0, 1) || sp["enabled"]?.Type != JTokenType.Boolean
                        || sp["active"]?.Type != JTokenType.Boolean) return false;
                }
            }
            return true;
        }
    }
}
