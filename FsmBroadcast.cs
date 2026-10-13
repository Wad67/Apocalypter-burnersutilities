using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace BurnersUtilities
{
    // Faster PlayMaker events sent to a GameObject (and its children). PlayMaker finds the receivers by going through
    // every running FSM in the scene and, for each, walking up its transform's parents to see whether the target is
    // among them. With a few thousand FSMs in a loaded world that is a million parent walks for a few hundred events,
    // which is the multi-second stall just after a load.
    //
    // This asks the target for its own FSMs (and its children's) instead, then keeps PlayMaker's list order by going
    // through the running list once and picking those out. The receivers, their order and the event data they get
    // are the same as PlayMaker's own; only the search changes. Anything unexpected falls back to PlayMaker's own.
    internal static class FsmBroadcast
    {
        private static bool Enabled => Plugin.FastFsmEventsEntry != null && Plugin.FastFsmEventsEntry.Value;

        private sealed class ByReference : IEqualityComparer<PlayMakerFSM>
        {
            public bool Equals(PlayMakerFSM a, PlayMakerFSM b) => ReferenceEquals(a, b);
            public int GetHashCode(PlayMakerFSM o) => RuntimeHelpers.GetHashCode(o);
        }

        private static readonly HashSet<PlayMakerFSM> Targets = new HashSet<PlayMakerFSM>(new ByReference());
        private static readonly List<PlayMakerFSM> Found = new List<PlayMakerFSM>();

        [HarmonyPatch(typeof(Fsm), nameof(Fsm.BroadcastEventToGameObject),
            typeof(GameObject), typeof(FsmEvent), typeof(FsmEventData), typeof(bool), typeof(bool))]
        private static class BroadcastPatch
        {
            private static bool Prefix(Fsm __instance, GameObject go, FsmEvent fsmEvent, FsmEventData eventData, bool sendToChildren, bool excludeSelf)
            {
                if (!Enabled) return true;
                if (go == null) return false;   // PlayMaker does nothing either
                var list = new List<Fsm>();
                try
                {
                    foreach (var f in Receivers(go, sendToChildren)) list.Add(f.Fsm);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("fast fsm events: " + e.Message + "; PlayMaker's own search instead");
                    return true;
                }

                // the rest exactly as PlayMaker does it
                var saved = new FsmEventData(Fsm.EventData);
                Fsm.SetEventDataSentByInfo();
                if (excludeSelf) list.Remove(__instance);
                foreach (var fsm in list)
                {
                    Fsm.EventData = new FsmEventData(eventData);
                    fsm.ProcessEvent(fsmEvent);
                }
                Fsm.EventData = saved;
                return false;
            }
        }

        // An event sent to the FSMs on one object (all of them, or the first with a given name): PlayMaker copies its
        // whole running list and compares every FSM's object with the target. Same receivers, found the same way.
        [HarmonyPatch(typeof(Fsm), nameof(Fsm.SendEventToFsmOnGameObject), typeof(GameObject), typeof(string), typeof(FsmEvent))]
        private static class SendPatch
        {
            private static bool Prefix(GameObject gameObject, string fsmName, FsmEvent fsmEvent)
            {
                if (!Enabled) return true;
                if (gameObject == null) return false;   // PlayMaker does nothing either
                List<PlayMakerFSM> list;
                try
                {
                    list = Receivers(gameObject, false);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("fast fsm events: " + e.Message + "; PlayMaker's own search instead");
                    return true;
                }

                // the rest exactly as PlayMaker does it, checking each FSM as it comes to it
                Fsm.SetEventDataSentByInfo();
                bool named = !string.IsNullOrEmpty(fsmName);
                foreach (var fsm in list)
                {
                    if (fsm == null || fsm.gameObject != gameObject) continue;
                    if (named && fsmName != fsm.Fsm.Name) continue;
                    fsm.Fsm.ProcessEvent(fsmEvent);
                    if (named) break;
                }
                return false;
            }
        }

        // the running FSMs on go (and under it), in PlayMaker's list order
        private static List<PlayMakerFSM> Receivers(GameObject go, bool children)
        {
            var list = new List<PlayMakerFSM>();
            try
            {
                Found.Clear();
                if (children) go.GetComponentsInChildren(true, Found);
                else go.GetComponents(Found);
                if (Found.Count == 0) return list;
                Targets.Clear();
                foreach (var f in Found) Targets.Add(f);
                foreach (var fsm in PlayMakerFSM.FsmList)
                    if (!ReferenceEquals(fsm, null) && Targets.Contains(fsm) && fsm != null)
                        list.Add(fsm);
                return list;
            }
            finally
            {
                Found.Clear();
                Targets.Clear();
            }
        }
    }
}
