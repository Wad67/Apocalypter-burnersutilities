using System;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace BurnersUtilities
{
    // Lock volumes inside lock volumes (a crate's in a trailer's). An item locks itself into a volume (layer 18)
    // every few seconds while loose, and on loading a save: its LockPhysics FSM casts a ray down from 10 m above and
    // up from 10 m below, and parents itself to the volume the upward ray hits first. From below, the outer volume
    // is hit first, so an item in a crate in a trailer locks into the trailer, and stays there when the crate is
    // taken out. Here it locks into the innermost volume it is in instead (the smallest one around it), not
    // counting its own (a crate's own volume), as the game would choose with one volume.
    internal static class PhysLock
    {
        private const int VolumeLayer = 18;
        private static readonly Collider[] Found = new Collider[32];

        [HarmonyPatch(typeof(SetParent), "OnEnter")]
        private static class InnermostVolume
        {
            private static void Prefix(SetParent __instance)
            {
                if (!Plugin.PhysLockEntry.Value || __instance.Fsm == null || __instance.Fsm.Name != "LockPhysics"
                    || __instance.State == null || __instance.State.Name != "InHandLock" || __instance.parent == null || __instance.parent.Value == null) return;
                try
                {
                    var item = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
                    if (item == null) return;
                    var inner = Innermost(item);
                    if (inner == null || inner == __instance.parent.Value) return;
                    Plugin.Verbose("physlock: " + item.name + " into " + inner.name + " (not " + __instance.parent.Value.name + ")");
                    __instance.parent.Value = inner;
                }
                catch (Exception e) { Plugin.Verbose("physlock: " + e.Message); }
            }
        }

        // the smallest lock volume around the item's position that isn't its own
        private static GameObject Innermost(GameObject item)
        {
            Vector3 at = item.transform.position;
            int n = Physics.OverlapSphereNonAlloc(at, 0.01f, Found, 1 << VolumeLayer, QueryTriggerInteraction.Collide);
            Collider best = null;
            float bestSize = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = Found[i];
                if (c == null || c.transform.IsChildOf(item.transform)) continue;
                var size = c.bounds.size;
                float volume = size.x * size.y * size.z;
                if (volume < bestSize) { bestSize = volume; best = c; }
            }
            return best != null ? best.gameObject : null;
        }
    }
}
