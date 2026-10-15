using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace BurnersUtilities
{
    // The player's eye and hands, for the tools: the game's global objects, what the aim hits, what is held, and
    // the game's grab (the player camera's 'GrabItem'), which a tool holds off while it is out so a left click
    // works the tool instead of picking the thing up.
    internal static class Hands
    {
        private const string GrabFsm = "GrabItem";
        private static readonly string[] Waiting = { "idle", "over" };
        private static readonly HashSet<object> GrabHolders = new HashSet<object>();
        private static PlayMakerFSM _grab;

        public static GameObject Global(string name)
        {
            var g = FsmVariables.GlobalVariables.FindFsmGameObject(name);
            return g != null ? g.Value : null;
        }

        public static Camera Eye()
        {
            var pc = Global("PlayerCamera");
            var cam = pc != null ? pc.GetComponent<Camera>() : null;
            return cam != null && cam.isActiveAndEnabled ? cam : Camera.main;
        }

        // what the player sees through: the camera the game picks things with (its MousePick aims with the main
        // camera, at the screen's centre); in a car's third-person view that is the car's camera, not the player's eye
        public static Camera View()
        {
            var main = Camera.main;
            return main != null && main.isActiveAndEnabled ? main : Eye();
        }

        // the aim: from the view, through the crosshair
        public static Ray Aim(Camera view) { return view.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)); }

        // the player and the rig that carries their camera (the camera holder and its pivot have bodies of their own):
        // never something the hands take hold of, nor what the player sits in
        public static bool PlayerRig(Transform t)
        {
            if (t == null) return false;
            foreach (var name in Rig)
            {
                var g = Global(name);
                if (g != null && t.IsChildOf(g.transform)) return true;
            }
            return false;
        }
        private static readonly string[] Rig = { "Player", "PlayerCameraHolder", "PlayerPivot" };

        // something in the hands (in use, or grabbed)
        public static bool Busy()
        {
            var use = Global("HandItemUse");
            var hand = Global("Hand");
            return (use != null && use.transform.childCount > 0) || (hand != null && hand.transform.childCount > 0);
        }

        // grabbed: carried in the 'Hand' (the game places it there each frame)
        public static bool Grabbed(Transform t)
        {
            var hand = Global("Hand");
            return t != null && hand != null && t.IsChildOf(hand.transform);
        }

        // held by the player, not by physics: in use in the hands, on the player, or grabbed (unless the hands
        // hold it by force, HandHold: then it is a body like any other)
        public static bool Held(Transform t)
        {
            if (t == null) return false;
            if (Grabbed(t)) return !HandHold.On;
            var use = Global("HandItemUse");
            var player = Global("Player");
            return (use != null && t.IsChildOf(use.transform)) || (player != null && t.IsChildOf(player.transform));
        }

        // what the aim hits first, within reach of the player's eye (the aim may start behind them, in a third-person
        // view): not the player or their camera rig, not triggers
        public static bool Pick(Ray ray, float reach, out RaycastHit hit)
        {
            var eye = Eye();
            Vector3 from = eye != null ? eye.transform.position : ray.origin;
            float along = Vector3.Distance(from, ray.origin) + reach;   // the ray's length that can reach that far
            hit = default(RaycastHit);
            float best = float.MaxValue;
            bool found = false;
            foreach (var h in Physics.RaycastAll(ray, along, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || PlayerRig(h.collider.transform) || (h.collider.attachedRigidbody != null && PlayerRig(h.collider.attachedRigidbody.transform))) continue;
                if (h.distance < best) { best = h.distance; hit = h; found = true; }
            }
            return found && Vector3.Distance(from, hit.point) <= reach;
        }

        private static PlayMakerFSM Grab()
        {
            if (_grab == null)
            {
                var pc = Global("PlayerCamera");
                _grab = pc != null ? pc.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == GrabFsm) : null;
            }
            return _grab;
        }

        // Hold the grab off for a tool: while the hands are empty it is only waiting ('idle' / 'over'); it goes
        // back to waiting (clearing its "grab" prompt) and is switched off until no tool holds it.
        public static void HoldGrab(object holder)
        {
            GrabHolders.Add(holder);
            var g = Grab();
            if (g == null || !g.enabled || !Waiting.Contains(g.ActiveStateName)) return;
            if (g.ActiveStateName != "idle") g.SetState("idle");
            g.enabled = false;
        }

        public static void ReleaseGrab(object holder)
        {
            GrabHolders.Remove(holder);
            if (GrabHolders.Count > 0) return;
            var g = Grab();
            if (g != null && !g.enabled) g.enabled = true;
        }

        // the grabbed thing pulled out of the hand (the grab's own drop)
        public static void LetGo(GameObject item, string why)
        {
            var g = Grab();
            if (g == null || g.ActiveStateName != "ItemInHand") return;
            Plugin.Verbose((item != null ? item.name : "the grabbed thing") + " pulled out of the hand: " + why);
            g.SendEvent("drop");
        }
    }
}
