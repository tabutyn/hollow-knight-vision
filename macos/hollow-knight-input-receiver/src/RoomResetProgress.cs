using System;
using System.Collections.Generic;

namespace HollowKnightVisionInputReceiver
{
    internal static class RoomResetProgress
    {
        internal static bool BarrierName(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
            return n.Contains("breakable") || n.Contains("break wall") || n.Contains("break floor")
                || n.Contains("blockade") || n.Contains("barrier") || n.Contains("one way wall")
                || n.Contains("barricade") || n.Contains("gate") || n.Contains("lever")
                || n.Contains("quake floor") || n.Contains("secret wall") || n.Contains("break door")
                || n.Contains("tute door") || n.Contains("tut door") || n.Contains("great door")
                || n.Contains("collapser") || n.Contains("collapse floor");
        }

        internal static void Clear(SceneData data, string sceneName, HashSet<string> boolIDs, HashSet<string> intIDs)
        {
            data.geoRocks.RemoveAll(r => r.sceneName == sceneName);
            data.persistentBoolItems.RemoveAll(r => r.sceneName == sceneName && (r.semiPersistent || boolIDs.Contains(r.id) || BarrierName(r.id)));
            data.persistentIntItems.RemoveAll(r => r.sceneName == sceneName && (r.semiPersistent || intIDs.Contains(r.id) || BarrierName(r.id)));
        }
    }
}
